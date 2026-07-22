using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.UI;
using Windows.Foundation;
using Kakikomi.Models;

namespace Kakikomi.Services;

/// <summary>
/// Single-process engine: A/B display slots, dual MediaPlayer sync, ink strokes, neta warm cache.
/// </summary>
public sealed class EngineSession : IDisposable
{
    public const double DesignWidth = 1920;
    public const double DesignHeight = 1080;
    private const string FolderPathKey = "NetaFolderPath";
    private const int WarmParallelism = 2;

    private static readonly string[] VideoExtensions =
    [
        ".mp4", ".mov", ".mkv", ".wmv", ".avi", ".m4v"
    ];

    private bool _disposed;
    private string? _folderPath;

    private readonly MediaPlayerPair[] _displayPairs = [new MediaPlayerPair(), new MediaPlayerPair()];
    private readonly NetaWarmCache _warmCache = new();
    private readonly SemaphoreSlim _warmParallel = new(WarmParallelism, WarmParallelism);

    private int _visibleSlotIndex;
    private int _openGeneration;
    private CancellationTokenSource? _warmCts;

    public IReadOnlyList<InkStrokeData> Strokes => _strokes;
    private readonly List<InkStrokeData> _strokes = [];

    public InkStrokeData? ActiveStroke { get; private set; }

    public event Action? StrokesChanged;
    public event Action? SourceChanged;
    public event Action? PlaybackStateChanged;
    public event Action? TimelineChanged;
    /// <summary>表示スロットが切り替わった（0 or 1）。UI は両 MPE を再バインドして可視を更新する。</summary>
    public event Action<int>? VisibleSlotChanged;

    public double ClockRate { get; private set; } = 1.0;
    public bool IsPlaying { get; private set; }
    public string? CurrentPath { get; private set; }
    public string? FolderPath => _folderPath;
    public int VisibleSlotIndex => _visibleSlotIndex;

    public MediaPlayer OperatorPlayer => _displayPairs[_visibleSlotIndex].Operator;
    public MediaPlayer CleanPlayer => _displayPairs[_visibleSlotIndex].Clean;

    public TimeSpan TimelinePosition => OperatorPlayer.PlaybackSession.Position;
    public TimeSpan TimelineDuration => OperatorPlayer.PlaybackSession.NaturalDuration;

    public EngineSession()
    {
        WireDurationChanged(_displayPairs[0]);
        WireDurationChanged(_displayPairs[1]);
    }

    public MediaPlayer GetOperatorPlayerForSlot(int slotIndex) =>
        _displayPairs[slotIndex].Operator;

    public MediaPlayer GetCleanPlayerForSlot(int slotIndex) =>
        _displayPairs[slotIndex].Clean;

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

    public void ScheduleWarmAll(IReadOnlyList<string> paths)
    {
        _warmCts?.Cancel();
        _warmCts?.Dispose();
        _warmCts = new CancellationTokenSource();
        var token = _warmCts.Token;

        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _displayPairs)
        {
            if (!string.IsNullOrEmpty(pair.Path))
                reserved.Add(pair.Path);
        }

        if (!string.IsNullOrEmpty(CurrentPath))
            reserved.Add(CurrentPath);

        _ = Task.Run(async () =>
        {
            foreach (var path in paths)
            {
                if (token.IsCancellationRequested)
                    break;

                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    continue;

                if (reserved.Contains(path) || _warmCache.Contains(path))
                    continue;

                await _warmParallel.WaitAsync(token).ConfigureAwait(false);
                MediaPlayerPair? pair = null;
                try
                {
                    if (token.IsCancellationRequested)
                        break;

                    if (reserved.Contains(path) || _warmCache.Contains(path))
                        continue;

                    pair = new MediaPlayerPair();
                    await PreparePairAtStartAsync(pair, path, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested)
                    {
                        pair.Dispose();
                        break;
                    }

                    _warmCache.Put(path, pair);
                    pair = null;
                }
                catch (OperationCanceledException)
                {
                    pair?.Dispose();
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Warm] {path}: {ex.Message}");
                    pair?.Dispose();
                }
                finally
                {
                    _warmParallel.Release();
                }
            }
        }, token);
    }

    public async Task OpenNetaAsync(NetaItem item, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path))
            throw new FileNotFoundException("動画ファイルが見つかりません", item.Path);

        if (string.Equals(CurrentPath, item.Path, StringComparison.OrdinalIgnoreCase))
            return;

        var generation = Interlocked.Increment(ref _openGeneration);
        cancellationToken.ThrowIfCancellationRequested();

        if (IsPlaying)
            Pause();

        var standbySlot = 1 - _visibleSlotIndex;
        var oldVisibleSlot = _visibleSlotIndex;
        var oldVisiblePair = _displayPairs[oldVisibleSlot];

        try
        {
            if (_warmCache.TryTake(item.Path, out var warmed))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (generation != _openGeneration)
                {
                    _warmCache.Put(item.Path, warmed);
                    return;
                }

                ReleaseDisplayPair(standbySlot);
                _displayPairs[standbySlot] = warmed;
                WireDurationChanged(warmed);
            }
            else
            {
                var standbyPair = _displayPairs[standbySlot];
                await PreparePairAtStartAsync(standbyPair, item.Path, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (generation != _openGeneration)
                    return;
            }

            ClearStrokes();
            SetRate(1.0);
            IsPlaying = false;
            CurrentPath = item.Path;
            _visibleSlotIndex = standbySlot;
            ApplyMutePolicy();

            if (!string.IsNullOrEmpty(oldVisiblePair.Path))
                _warmCache.Put(oldVisiblePair.Path, oldVisiblePair);
            else
                oldVisiblePair.Dispose();

            _displayPairs[oldVisibleSlot] = new MediaPlayerPair();
            WireDurationChanged(_displayPairs[oldVisibleSlot]);

            VisibleSlotChanged?.Invoke(_visibleSlotIndex);
            SourceChanged?.Invoke();
            PlaybackStateChanged?.Invoke();
            TimelineChanged?.Invoke();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw;
        }
    }

    /// <summary>指定パスのネタを解放（再生中なら停止、ウォームからも除去）。</summary>
    public void UnloadNeta(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        _warmCache.Remove(path);

        for (var i = 0; i < _displayPairs.Length; i++)
        {
            var pair = _displayPairs[i];
            if (!string.Equals(pair.Path, path, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                pair.Operator.Pause();
                pair.Clean.Pause();
                pair.ClearSource();
            }
            catch
            {
                // ignore
            }

            if (i == _visibleSlotIndex)
            {
                CurrentPath = null;
                IsPlaying = false;
                ClearStrokes();
                PlaybackStateChanged?.Invoke();
                SourceChanged?.Invoke();
                TimelineChanged?.Invoke();
            }
        }
    }

    public void Play()
    {
        if (CurrentPath is null)
            return;

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
        foreach (var pair in _displayPairs)
        {
            pair.Operator.PlaybackSession.PlaybackRate = rate;
            pair.Clean.PlaybackSession.PlaybackRate = rate;
        }

        ApplyMutePolicy();
        PlaybackStateChanged?.Invoke();
    }

    public void SeekRelative(TimeSpan delta) => SeekTo(TimelinePosition + delta);

    private bool _scrubPreviewActive;
    private double _scrubSavedRate = 1.0;

    /// <summary>
    /// シークバードラッグ開始。ポーズ中の Position だけではコマが出ないため、
    /// Operator だけ再生状態にしておく（Rate=0 で時間は進めない）。
    /// Play/Pause 連打はシーク待ち行列を積み遅れが増えるので使わない。
    /// </summary>
    public void BeginScrubPreview()
    {
        if (CurrentPath is null || _scrubPreviewActive)
            return;

        _scrubPreviewActive = true;
        _scrubSavedRate = ClockRate <= 0 ? 1.0 : ClockRate;

        try
        {
            OperatorPlayer.IsMuted = true;
            OperatorPlayer.Volume = 0;
            // Rate=0 で時間を止めつつ Playing にする（Position 変更でコマが出る）
            try
            {
                OperatorPlayer.PlaybackSession.PlaybackRate = 0;
            }
            catch
            {
                OperatorPlayer.PlaybackSession.PlaybackRate = 0.01;
            }

            OperatorPlayer.Play();
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>シークバードラッグ終了。ポーズに戻し Clean と同期する。</summary>
    public void EndScrubPreview(TimeSpan finalPosition)
    {
        if (!_scrubPreviewActive)
        {
            SeekTo(finalPosition, syncClean: true, notifyTimeline: false);
            return;
        }

        _scrubPreviewActive = false;

        if (finalPosition < TimeSpan.Zero)
            finalPosition = TimeSpan.Zero;
        var duration = TimelineDuration;
        if (duration > TimeSpan.Zero && finalPosition > duration)
            finalPosition = duration;

        try
        {
            OperatorPlayer.Pause();
            OperatorPlayer.PlaybackSession.PlaybackRate = _scrubSavedRate;
            OperatorPlayer.PlaybackSession.Position = finalPosition;
            CleanPlayer.PlaybackSession.PlaybackRate = _scrubSavedRate;
            CleanPlayer.PlaybackSession.Position = finalPosition;
            CleanPlayer.Pause();
            ForcePausedFrameRefresh(OperatorPlayer);
            ForcePausedFrameRefresh(CleanPlayer);
        }
        catch
        {
            // ignore
        }

        IsPlaying = false;
        ApplyMutePolicy();
        PlaybackStateChanged?.Invoke();
    }

    /// <summary>ドラッグ中プレビュー。Position のみ（Play/Pause しない）。</summary>
    public void SeekPreview(TimeSpan position)
    {
        if (CurrentPath is null)
            return;

        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;

        var duration = TimelineDuration;
        if (duration > TimeSpan.Zero && position > duration)
            position = duration;

        OperatorPlayer.PlaybackSession.Position = position;
    }

    public void SeekTo(
        TimeSpan position,
        bool syncClean = true,
        bool notifyTimeline = true,
        bool refreshOperatorFrame = false)
    {
        if (CurrentPath is null)
            return;

        if (_scrubPreviewActive)
        {
            SeekPreview(position);
            return;
        }

        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;

        var duration = TimelineDuration;
        if (duration > TimeSpan.Zero && position > duration)
            position = duration;

        OperatorPlayer.PlaybackSession.Position = position;
        if (syncClean)
        {
            CleanPlayer.PlaybackSession.Position = position;
            if (!IsPlaying)
                ForcePausedFrameRefresh(OperatorPlayer);
        }
        else if (refreshOperatorFrame && !IsPlaying)
        {
            ForcePausedFrameRefresh(OperatorPlayer);
        }

        ApplyMutePolicy();
        if (notifyTimeline)
            TimelineChanged?.Invoke();
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

    private void WireDurationChanged(MediaPlayerPair pair)
    {
        pair.Operator.PlaybackSession.NaturalDurationChanged += (_, _) => TimelineChanged?.Invoke();
    }

    private void ReleaseDisplayPair(int slotIndex)
    {
        var pair = _displayPairs[slotIndex];
        if (!string.IsNullOrEmpty(pair.Path))
            _warmCache.Put(pair.Path, pair);
        else
            pair.Dispose();
    }

    private static async Task PreparePairAtStartAsync(
        MediaPlayerPair pair,
        string path,
        CancellationToken cancellationToken)
    {
        pair.ClearSource();

        var uri = new Uri(path, UriKind.Absolute);
        pair.Operator.Source = MediaSource.CreateFromUri(uri);
        pair.Clean.Source = MediaSource.CreateFromUri(uri);
        pair.Path = path;

        await WaitForOpenedAsync(pair.Operator, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await WaitForOpenedAsync(pair.Clean, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        pair.Operator.Pause();
        pair.Clean.Pause();
        pair.Operator.IsMuted = true;
        pair.Operator.Volume = 0;
        pair.Clean.IsMuted = true;
        pair.Clean.Volume = 0;
        pair.Operator.PlaybackSession.Position = TimeSpan.Zero;
        pair.Clean.PlaybackSession.Position = TimeSpan.Zero;

        await PrimeFirstFrameAsync(pair.Operator, cancellationToken).ConfigureAwait(false);
        await PrimeFirstFrameAsync(pair.Clean, cancellationToken).ConfigureAwait(false);
    }

    private static async Task PrimeFirstFrameAsync(MediaPlayer player, CancellationToken cancellationToken)
    {
        try
        {
            player.IsMuted = true;
            player.Volume = 0;
            player.Play();
            await Task.Delay(32, cancellationToken).ConfigureAwait(false);
            player.Pause();
            player.PlaybackSession.Position = TimeSpan.Zero;
        }
        catch
        {
            // ignore
        }
    }

    private static void ForcePausedFrameRefresh(MediaPlayer player)
    {
        try
        {
            // ポーズ中コマ更新用の一瞬 Play。音は絶対に出さない
            player.IsMuted = true;
            player.Volume = 0;
            player.Play();
            player.Pause();
        }
        catch
        {
            // ignore refresh failures
        }
    }

    private void SyncPositionFromOperator()
    {
        CleanPlayer.PlaybackSession.Position = OperatorPlayer.PlaybackSession.Position;
    }

    private void ApplyMutePolicy()
    {
        var muted = Math.Abs(ClockRate - 1.0) > 0.001;
        foreach (var pair in _displayPairs)
        {
            pair.Operator.IsMuted = true;
            pair.Operator.Volume = 0;
            pair.Clean.IsMuted = muted;
            pair.Clean.Volume = muted ? 0 : 1.0;
        }
    }

    private static async Task WaitForOpenedAsync(MediaPlayer player, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var session = player.PlaybackSession;
        if (session.PlaybackState is not MediaPlaybackState.Opening)
        {
            await WaitForMediaReadyAsync(player, cancellationToken).ConfigureAwait(false);
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

        session.PlaybackStateChanged += Handler;
        if (session.PlaybackState is MediaPlaybackState.Paused
            or MediaPlaybackState.Playing
            or MediaPlaybackState.None)
        {
            session.PlaybackStateChanged -= Handler;
            await WaitForMediaReadyAsync(player, cancellationToken).ConfigureAwait(false);
            return;
        }

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000, cancellationToken)).ConfigureAwait(false);
        if (completed != tcs.Task)
            session.PlaybackStateChanged -= Handler;

        cancellationToken.ThrowIfCancellationRequested();
        await WaitForMediaReadyAsync(player, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WaitForMediaReadyAsync(MediaPlayer player, CancellationToken cancellationToken)
    {
        var session = player.PlaybackSession;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var duration = session.NaturalDuration;
            if (duration > TimeSpan.Zero)
                break;

            await Task.Delay(16, cancellationToken).ConfigureAwait(false);
        }

        await Task.Delay(32, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _warmCts?.Cancel();
        _warmCts?.Dispose();
        _warmParallel.Dispose();
        _warmCache.Dispose();

        foreach (var pair in _displayPairs)
            pair.Dispose();
    }
}
