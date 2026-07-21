using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.UI;
using Windows.Foundation;
using Kakikomi.Models;

namespace Kakikomi.Services;

/// <summary>
/// Single-process engine: dual MediaPlayer sync, ink strokes, rate/mute policy.
/// </summary>
public sealed class EngineSession : IDisposable
{
    public const double DesignWidth = 1920;
    public const double DesignHeight = 1080;
    private const string FolderPathKey = "NetaFolderPath";

    private static readonly string[] VideoExtensions =
    [
        ".mp4", ".mov", ".mkv", ".wmv", ".avi", ".m4v"
    ];

    private bool _disposed;
    private string? _folderPath;

    public MediaPlayer OperatorPlayer { get; } = CreatePlayer();
    public MediaPlayer CleanPlayer { get; } = CreatePlayer();

    public IReadOnlyList<InkStrokeData> Strokes => _strokes;
    private readonly List<InkStrokeData> _strokes = [];

    public InkStrokeData? ActiveStroke { get; private set; }

    public event Action? StrokesChanged;
    public event Action? SourceChanged;
    public event Action? PlaybackStateChanged;
    public event Action? TimelineChanged;

    public double ClockRate { get; private set; } = 1.0;
    public bool IsPlaying { get; private set; }
    public string? CurrentPath { get; private set; }

    public TimeSpan TimelinePosition => OperatorPlayer.PlaybackSession.Position;

    public TimeSpan TimelineDuration => OperatorPlayer.PlaybackSession.NaturalDuration;

    public EngineSession()
    {
        OperatorPlayer.PlaybackSession.NaturalDurationChanged += (_, _) => TimelineChanged?.Invoke();
    }

    public Task<IReadOnlyList<NetaItem>> LoadNetaFolderFromPathAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return Task.FromResult<IReadOnlyList<NetaItem>>([]);

        _folderPath = folderPath;
        try
        {
            ApplicationData.Current.LocalSettings.Values[FolderPathKey] = folderPath;
        }
        catch
        {
            // LocalSettings が使えない環境でも一覧は出す
        }

        List<NetaItem> items;
        try
        {
            items = Directory.EnumerateFiles(folderPath)
                .Where(p => VideoExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .Select(p => new NetaItem
                {
                    DisplayName = Path.GetFileNameWithoutExtension(p),
                    Path = p
                })
                .ToList();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"フォルダを読めません: {folderPath} / {ex.Message}", ex);
        }

        return Task.FromResult<IReadOnlyList<NetaItem>>(items);
    }

    public async Task<IReadOnlyList<NetaItem>?> TryLoadSavedFolderAsync()
    {
        try
        {
            if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(FolderPathKey, out var value)
                || value is not string path
                || string.IsNullOrWhiteSpace(path)
                || !Directory.Exists(path))
            {
                return null;
            }

            return await LoadNetaFolderFromPathAsync(path);
        }
        catch
        {
            ClearSavedFolder();
            return null;
        }
    }

    public void ClearSavedFolder()
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values.Remove(FolderPathKey);
        }
        catch
        {
            // ignore
        }

        _folderPath = null;
    }

    public async Task OpenNetaAsync(NetaItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path))
            throw new FileNotFoundException("動画ファイルが見つかりません", item.Path);

        // StorageFile.GetFileFromPathAsync は unpackaged で失敗しがちなので URI 再生
        var uri = new Uri(item.Path, UriKind.Absolute);
        var source1 = MediaSource.CreateFromUri(uri);
        var source2 = MediaSource.CreateFromUri(uri);

        OperatorPlayer.Source = source1;
        CleanPlayer.Source = source2;
        CurrentPath = item.Path;

        ClearStrokes();
        SetRate(1.0);
        ApplyMutePolicy();

        await WaitForOpenedAsync(OperatorPlayer);
        await WaitForOpenedAsync(CleanPlayer);

        OperatorPlayer.Pause();
        CleanPlayer.Pause();
        OperatorPlayer.PlaybackSession.Position = TimeSpan.Zero;
        CleanPlayer.PlaybackSession.Position = TimeSpan.Zero;
        IsPlaying = false;

        SourceChanged?.Invoke();
        PlaybackStateChanged?.Invoke();
        TimelineChanged?.Invoke();
        await Task.CompletedTask;
    }

    public void Play()
    {
        if (CurrentPath is null)
            return;

        // 再生開始で書き込みは消す（保存は呼び出し側で Flush 済み想定）
        ClearStrokes();

        ApplyMutePolicy();
        SyncPositionFromOperator();
        OperatorPlayer.Play();
        CleanPlayer.Play();
        IsPlaying = true;
        PlaybackStateChanged?.Invoke();
    }

    public void Pause()
    {
        OperatorPlayer.Pause();
        CleanPlayer.Pause();
        SyncPositionFromOperator();
        IsPlaying = false;
        PlaybackStateChanged?.Invoke();
    }

    public void TogglePlayPause()
    {
        if (IsPlaying)
            Pause();
        else
            Play();
    }

    public void SetRate(double rate)
    {
        if (rate <= 0)
            rate = 1.0;

        ClockRate = rate;
        OperatorPlayer.PlaybackSession.PlaybackRate = rate;
        CleanPlayer.PlaybackSession.PlaybackRate = rate;
        ApplyMutePolicy();
        PlaybackStateChanged?.Invoke();
    }

    public void SeekRelative(TimeSpan delta) => SeekTo(TimelinePosition + delta);

    public void SeekTo(TimeSpan position, bool syncClean = true, bool notifyTimeline = true)
    {
        if (CurrentPath is null)
            return;

        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;

        var duration = TimelineDuration;
        if (duration > TimeSpan.Zero && position > duration)
            position = duration;

        OperatorPlayer.PlaybackSession.Position = position;
        if (syncClean)
        {
            CleanPlayer.PlaybackSession.Position = position;
            // 停止中は Position だけだとフレームが更新されないことがある（確定シーク時のみ）
            if (!IsPlaying)
                ForcePausedFrameRefresh(OperatorPlayer);
        }

        ApplyMutePolicy();
        if (notifyTimeline)
            TimelineChanged?.Invoke();
    }

    private static void ForcePausedFrameRefresh(MediaPlayer player)
    {
        try
        {
            var rate = player.PlaybackSession.PlaybackRate;
            player.PlaybackSession.PlaybackRate = rate;
            player.Play();
            player.Pause();
        }
        catch
        {
            // ignore refresh failures
        }
    }

    public void BeginStroke(Color color, double thickness, Point point)
    {
        ActiveStroke = new InkStrokeData
        {
            Points = [point],
            Color = color,
            Thickness = thickness
        };
        StrokesChanged?.Invoke();
    }

    public void AppendStrokePoint(Point point)
    {
        if (ActiveStroke is null)
            return;

        var points = ActiveStroke.Points.ToList();
        points.Add(point);
        ActiveStroke = new InkStrokeData
        {
            Points = points,
            Color = ActiveStroke.Color,
            Thickness = ActiveStroke.Thickness
        };
        StrokesChanged?.Invoke();
    }

    public void EndStroke()
    {
        if (ActiveStroke is null)
            return;

        if (ActiveStroke.Points.Count >= 2)
            _strokes.Add(ActiveStroke);

        ActiveStroke = null;
        StrokesChanged?.Invoke();
    }

    public void EraseNear(Point point, double radius)
    {
        var r2 = radius * radius;
        var removed = _strokes.RemoveAll(stroke =>
            stroke.Points.Any(p =>
            {
                var dx = p.X - point.X;
                var dy = p.Y - point.Y;
                return dx * dx + dy * dy <= r2;
            }));

        if (removed > 0)
            StrokesChanged?.Invoke();
    }

    public void ClearStrokes()
    {
        _strokes.Clear();
        ActiveStroke = null;
        StrokesChanged?.Invoke();
    }

    private void SyncPositionFromOperator()
    {
        CleanPlayer.PlaybackSession.Position = OperatorPlayer.PlaybackSession.Position;
    }

    private void ApplyMutePolicy()
    {
        var muted = Math.Abs(ClockRate - 1.0) > 0.001;
        OperatorPlayer.IsMuted = true;
        OperatorPlayer.Volume = 0;
        CleanPlayer.IsMuted = muted;
        CleanPlayer.Volume = muted ? 0 : 1.0;
    }

    private static async Task WaitForOpenedAsync(MediaPlayer player)
    {
        if (player.PlaybackSession.PlaybackState != MediaPlaybackState.Opening
            && player.Source is not null)
        {
            await Task.Delay(50);
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(MediaPlaybackSession sender, object args)
        {
            if (sender.PlaybackState is MediaPlaybackState.Paused
                or MediaPlaybackState.Playing
                or MediaPlaybackState.None)
            {
                sender.PlaybackStateChanged -= Handler;
                tcs.TrySetResult();
            }
        }

        player.PlaybackSession.PlaybackStateChanged += Handler;
        if (player.PlaybackSession.PlaybackState is MediaPlaybackState.Paused
            or MediaPlaybackState.Playing
            or MediaPlaybackState.None)
        {
            player.PlaybackSession.PlaybackStateChanged -= Handler;
            tcs.TrySetResult();
        }

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(3000));
        if (completed != tcs.Task)
            player.PlaybackSession.PlaybackStateChanged -= Handler;
    }

    private static MediaPlayer CreatePlayer()
    {
        var player = new MediaPlayer
        {
            RealTimePlayback = false
        };
        player.CommandManager.IsEnabled = false;
        return player;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        OperatorPlayer.Dispose();
        CleanPlayer.Dispose();
    }
}
