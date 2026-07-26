using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.UI;
using Windows.Foundation;
using Kakikomi.Models;

namespace Kakikomi.Services;

/// <summary>
/// Single-process engine: A/B slots, single MediaPlayer per slot (shared surfaces), ink, warm cache.
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

    private static readonly string[] ImageExtensions =
    [
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff"
    ];

    /// <summary>ネタループで静止画を表示してから次へ進む秒数。</summary>
    private const int ImageLoopHoldMs = 5_000;

    private CancellationTokenSource? _imageLoopCts;

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
    /// <summary>Operator の再生が末尾まで到達したとき。</summary>
    public event Action? MediaEnded;
    /// <summary>遠隔プレビュー向け。ポーズ中でも JPEG を取り直してほしいときに発火。</summary>
    public event Action? PreviewKeyframeRequested;
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
        WirePairEvents(_displayPairs[0]);
        WirePairEvents(_displayPairs[1]);
    }

    public MediaPlayer GetOperatorPlayerForSlot(int slotIndex) =>
        _displayPairs[slotIndex].Operator;

    public MediaPlayer GetCleanPlayerForSlot(int slotIndex) =>
        _displayPairs[slotIndex].Clean;

    public Task<IReadOnlyList<NetaItem>> LoadNetaFolderFromPathAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return Task.FromResult<IReadOnlyList<NetaItem>>([]);

        RememberFolderPath(folderPath);

        List<NetaItem> items;
        try
        {
            items = Directory.EnumerateFiles(folderPath)
                .Where(IsNetaFile)
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .Select(p => CreateNetaItem(p, allowMissing: false))
                .ToList();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"フォルダを読めません: {folderPath} / {ex.Message}", ex);
        }

        return Task.FromResult<IReadOnlyList<NetaItem>>(items);
    }

    public IReadOnlyList<NetaItem> CreateNetaItemsFromPaths(IEnumerable<string> paths)
    {
        var items = paths
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p) && IsNetaFile(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .Select(p => CreateNetaItem(p, allowMissing: false))
            .ToList();

        var firstDir = items
            .Select(i => Path.GetDirectoryName(i.Path))
            .FirstOrDefault(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d));
        if (firstDir is not null)
            RememberFolderPath(firstDir);

        return items;
    }

    /// <summary>保存済みパス順を復元。欠落ファイルも一覧に残す。</summary>
    public IReadOnlyList<NetaItem> CreateNetaItemsFromStoredPaths(IEnumerable<string> paths)
    {
        var items = new List<NetaItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !seen.Add(path))
                continue;

            items.Add(CreateNetaItem(path, allowMissing: true));
        }

        var firstDir = items
            .Where(i => !i.IsMissing)
            .Select(i => Path.GetDirectoryName(i.Path))
            .FirstOrDefault(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d));
        if (firstDir is not null)
            RememberFolderPath(firstDir);

        return items;
    }

    public void RememberFolderPath(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return;

        _folderPath = folderPath;
        try
        {
            ApplicationData.Current.LocalSettings.Values[FolderPathKey] = folderPath;
        }
        catch
        {
            // LocalSettings が使えない環境でも一覧は出す
        }
    }

    public static bool IsVideoFile(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsNetaFile(string path) =>
        IsVideoFile(path) || IsImageFile(path);

    public bool IsCurrentNetaImage =>
        CurrentPath is not null && IsImageFile(CurrentPath);

    private static NetaItem CreateNetaItem(string path, bool allowMissing)
    {
        var exists = File.Exists(path);
        if (!allowMissing && !exists)
            throw new FileNotFoundException("素材ファイルが見つかりません", path);

        var item = new NetaItem
        {
            DisplayName = Path.GetFileNameWithoutExtension(path),
            Path = path,
            IsMissing = !exists
        };
        if (exists)
            item.RefreshConvertState();
        return item;
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

    public async Task OpenNetaAsync(
        NetaItem item,
        CancellationToken cancellationToken = default,
        bool forceToHead = false)
    {
        if (string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path))
            throw new FileNotFoundException("素材ファイルが見つかりません", item.Path);

        // 同一ネタの再選択: 通常は何もしない。forceToHead なら先頭へ戻す。
        if (string.Equals(CurrentPath, item.Path, StringComparison.OrdinalIgnoreCase))
        {
            if (forceToHead)
                RestartToHead();
            return;
        }

        var generation = Interlocked.Increment(ref _openGeneration);
        cancellationToken.ThrowIfCancellationRequested();

        if (IsPlaying)
            Pause();

        var standbySlot = 1 - _visibleSlotIndex;
        var oldVisibleSlot = _visibleSlotIndex;
        var oldVisiblePair = _displayPairs[oldVisibleSlot];
        var resetToHead = forceToHead || !AppSettings.ResumePlayback;

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

                if (resetToHead)
                    ResetPairToStart(warmed);

                ReleaseDisplayPair(standbySlot);
                _displayPairs[standbySlot] = warmed;
                WirePairEvents(warmed);
            }
            else
            {
                var standbyPair = _displayPairs[standbySlot];
                await PreparePairAtStartAsync(standbyPair, item.Path, cancellationToken).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
                if (generation != _openGeneration)
                    return;
            }

            ClearStrokes();
            SetRate(1.0);
            IsPlaying = false;
            CurrentPath = item.Path;
            _visibleSlotIndex = standbySlot;
            if (resetToHead)
                ResetPairToStart(_displayPairs[_visibleSlotIndex]);

            ApplyMutePolicy();

            if (!string.IsNullOrEmpty(oldVisiblePair.Path))
            {
                if (!AppSettings.ResumePlayback)
                    ResetPairToStart(oldVisiblePair);
                _warmCache.Put(oldVisiblePair.Path, oldVisiblePair);
            }
            else
            {
                oldVisiblePair.Dispose();
            }

            _displayPairs[oldVisibleSlot] = new MediaPlayerPair();
            WirePairEvents(_displayPairs[oldVisibleSlot]);

            // 先にスロット切替を通知してからコマ更新する。
            // （遠隔プレビュー等が新プレイヤーを購読してから VideoFrameAvailable を受け取る）
            VisibleSlotChanged?.Invoke(_visibleSlotIndex);
            SourceChanged?.Invoke();
            PlaybackStateChanged?.Invoke();
            TimelineChanged?.Invoke();
            RequestPreviewKeyframe(withRetries: true);
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

    /// <summary>いま開いているネタを先頭ポーズに戻す（同一ネタ再選択用）。</summary>
    public void RestartToHead()
    {
        if (CurrentPath is null)
            return;

        if (IsPlaying)
            Pause();

        ClearStrokes();
        SetRate(1.0);
        IsPlaying = false;

        var visible = _displayPairs[_visibleSlotIndex];
        ResetPairToStart(visible);
        ApplyMutePolicy();

        // SourceChanged は購読の張り直しを誘発して先頭コマを落とすので使わない
        PlaybackStateChanged?.Invoke();
        TimelineChanged?.Invoke();
        RequestPreviewKeyframe(withRetries: true);
    }

    /// <summary>ポーズ中プレビュー用に VideoFrameAvailable を起こす。</summary>
    public void RequestPausedFrameRefresh()
    {
        if (CurrentPath is null || _disposed)
            return;
        if (IsPlaying)
            return;
        ForcePausedFrameRefresh(OperatorPlayer, ClockRate);
    }

    /// <summary>再生中に Frame Server を再起動する（一時停止しない）。</summary>
    public void RequestPlayingFrameRefresh()
    {
        if (CurrentPath is null || _disposed || !IsPlaying)
            return;

        try
        {
            var player = OperatorPlayer;
            player.IsVideoFrameServerEnabled = false;
            player.IsVideoFrameServerEnabled = true;
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>映像経路の最終手段: いまのネタを同位置で再オープン。</summary>
    public async Task RecoverVideoPipelineAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentPath is null || _disposed)
            return;

        var path = CurrentPath;
        var position = TimelinePosition;
        var wasPlaying = IsPlaying;
        var rate = ClockRate;
        var slot = _visibleSlotIndex;
        var pair = _displayPairs[slot];

        if (wasPlaying)
            Pause();

        try
        {
            pair.ClearSource();
            await PreparePairAtStartAsync(pair, path, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();

            pair.Player.PlaybackSession.Position = position;
            SetRate(rate);
            ApplyMutePolicy();

            VisibleSlotChanged?.Invoke(slot);
            PlaybackStateChanged?.Invoke();
            TimelineChanged?.Invoke();

            if (wasPlaying)
                Play();
            else
                ForcePausedFrameRefresh(pair.Player, rate);

            RequestPreviewKeyframe(withRetries: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PerfMonitorService.Instance.LogEvent("WARN", $"RecoverVideoPipeline: {ex.Message}");
        }
    }

    private void RequestPreviewKeyframe(bool withRetries)
    {
        PreviewKeyframeRequested?.Invoke();
        RequestPausedFrameRefresh();
        if (!withRetries)
            return;

        var path = CurrentPath;
        var slot = _visibleSlotIndex;
        var generation = _openGeneration;
        _ = Task.Run(async () =>
        {
            foreach (var delayMs in new[] { 40, 120, 280 })
            {
                try
                {
                    await Task.Delay(delayMs).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                if (_disposed || generation != _openGeneration)
                    return;
                if (!string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase))
                    return;
                if (_visibleSlotIndex != slot)
                    return;
                if (IsPlaying)
                    return;

                var dq = App.DispatcherQueue;
                if (dq is null)
                    return;

                dq.TryEnqueue(() =>
                {
                    if (_disposed || generation != _openGeneration)
                        return;
                    if (!string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase))
                        return;
                    if (IsPlaying)
                        return;
                    PreviewKeyframeRequested?.Invoke();
                    RequestPausedFrameRefresh();
                });
            }
        });
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
                pair.Player.Pause();
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

        CancelImageLoopAdvance();
        ClearStrokes();
        IsPlaying = true;
        ApplyMutePolicy();

        if (IsImageFile(CurrentPath))
        {
            ForcePausedFrameRefresh(OperatorPlayer, ClockRate);
            if (RemoteNetaLoopService.Instance.IsRunning)
                ScheduleImageLoopAdvance();
            PlaybackStateChanged?.Invoke();
            return;
        }

        OperatorPlayer.PlaybackSession.PlaybackRate = ClockRate;
        OperatorPlayer.Play();
        PlaybackStateChanged?.Invoke();
    }

    public void Pause()
    {
        CancelImageLoopAdvance();
        OperatorPlayer.Pause();
        IsPlaying = false;
        ApplyMutePolicy();
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
            if (_scrubPreviewActive && ReferenceEquals(pair, _displayPairs[_visibleSlotIndex]))
                continue;

            pair.Player.PlaybackSession.PlaybackRate = rate;
        }

        ApplyMutePolicy();
        PlaybackStateChanged?.Invoke();
    }

    public void SeekRelative(TimeSpan delta) => SeekTo(TimelinePosition + delta);

    private bool _scrubPreviewActive;
    private double _scrubSavedRate = 1.0;

    /// <summary>
    /// シークバードラッグ開始。ポーズ中の Position だけではコマが出ないため、
    /// Operator を Rate=0 で再生状態にする（時間は進めない）。
    /// </summary>
    public void BeginScrubPreview()
    {
        if (CurrentPath is null || _scrubPreviewActive)
            return;

        _scrubPreviewActive = true;
        _scrubSavedRate = ClockRate <= 0 ? 1.0 : ClockRate;

        try
        {
            MuteBothPlayers();
            try
            {
                OperatorPlayer.PlaybackSession.PlaybackRate = 0;
            }
            catch
            {
                OperatorPlayer.PlaybackSession.PlaybackRate = 0.01;
            }

            OperatorPlayer.Play();
            MuteBothPlayers();
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
        finalPosition = ClampPosition(finalPosition);

        try
        {
            MuteBothPlayers();
            OperatorPlayer.Pause();
            OperatorPlayer.PlaybackSession.PlaybackRate = _scrubSavedRate;
            OperatorPlayer.PlaybackSession.Position = finalPosition;
            MuteBothPlayers();
            ForcePausedFrameRefresh(OperatorPlayer, restoreRate: _scrubSavedRate);
            MuteBothPlayers();
        }
        catch
        {
            // ignore
        }

        IsPlaying = false;
        ApplyMutePolicy();
        PlaybackStateChanged?.Invoke();
    }

    /// <summary>ドラッグ中プレビュー。Operator/Clean 両方の Position を更新。</summary>
    public void SeekPreview(TimeSpan position)
    {
        if (CurrentPath is null)
            return;

        position = ClampPosition(position);
        MuteBothPlayers();
        OperatorPlayer.PlaybackSession.Position = position;
        MuteBothPlayers();
    }

    private void MuteBothPlayers()
    {
        try
        {
            OperatorPlayer.IsMuted = true;
            OperatorPlayer.Volume = 0;
        }
        catch
        {
            // ignore
        }
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

        position = ClampPosition(position);
        OperatorPlayer.PlaybackSession.Position = position;
        if (!IsPlaying && (syncClean || refreshOperatorFrame))
            ForcePausedFrameRefresh(OperatorPlayer, ClockRate);

        ApplyMutePolicy();
        if (notifyTimeline)
            TimelineChanged?.Invoke();
    }

    private TimeSpan ClampPosition(TimeSpan position)
    {
        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;

        var duration = TimelineDuration;
        if (duration > TimeSpan.Zero && position > duration)
            position = duration;

        return position;
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

    private void WirePairEvents(MediaPlayerPair pair)
    {
        if (pair.EventsWired)
            return;

        pair.EventsWired = true;
        pair.Player.PlaybackSession.NaturalDurationChanged += (_, _) => TimelineChanged?.Invoke();
        pair.Player.MediaEnded += OnOperatorMediaEnded;
    }

    private void OnOperatorMediaEnded(MediaPlayer sender, object args)
    {
        if (_disposed || !IsPlaying)
            return;

        if (!ReferenceEquals(sender, OperatorPlayer))
            return;

        Pause();
        MediaEnded?.Invoke();
    }

    private void ReleaseDisplayPair(int slotIndex)
    {
        var pair = _displayPairs[slotIndex];
        if (!string.IsNullOrEmpty(pair.Path))
        {
            if (!AppSettings.ResumePlayback)
                ResetPairToStart(pair);
            _warmCache.Put(pair.Path, pair);
        }
        else
        {
            pair.Dispose();
        }
    }

    /// <summary>ウォーム再利用時に先頭へ戻す（レジューム OFF）。</summary>
    private static void ResetPairToStart(MediaPlayerPair pair)
    {
        try
        {
            pair.Player.Pause();
            pair.Player.IsMuted = true;
            pair.Player.Volume = 0;
            var session = pair.Player.PlaybackSession;
            // すでに先頭だと FrameAvailable が来ないことがあるので一度ずらす
            try
            {
                if (session.Position <= TimeSpan.FromMilliseconds(16))
                    session.Position = TimeSpan.FromMilliseconds(48);
            }
            catch
            {
                // ignore
            }

            session.Position = TimeSpan.Zero;
        }
        catch
        {
            // ignore
        }
    }

    private static async Task PreparePairAtStartAsync(
        MediaPlayerPair pair,
        string path,
        CancellationToken cancellationToken)
    {
        pair.ClearSource();

        var playbackPath = MovTranscodeService.ResolvePlaybackPath(path);
        var file = await StorageFile.GetFileFromPathAsync(playbackPath);
        cancellationToken.ThrowIfCancellationRequested();

        using var fail = AttachMediaFailed(pair.Player, out var error);

        pair.Player.Source = MediaSource.CreateFromStorageFile(file);
        pair.Path = path;

        await WaitForOpenedAsync(pair.Player, cancellationToken, path).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        ThrowIfMediaUnusable(pair.Player, playbackPath, error.Error);

        pair.Player.Pause();
        pair.Player.IsMuted = true;
        pair.Player.Volume = 0;
        pair.Player.PlaybackSession.Position = TimeSpan.Zero;

        await PrimeFirstFrameAsync(pair.Player, cancellationToken).ConfigureAwait(false);
    }

    private sealed class MediaFailBox
    {
        public string? Error;
    }

    private static IDisposable AttachMediaFailed(MediaPlayer player, out MediaFailBox box)
    {
        box = new MediaFailBox();
        var captured = box;
        TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs> handler = (_, args) =>
        {
            captured.Error = string.IsNullOrWhiteSpace(args.ErrorMessage)
                ? args.Error.ToString()
                : args.ErrorMessage;
        };
        player.MediaFailed += handler;
        return new ActionDisposable(() => player.MediaFailed -= handler);
    }

    private static void ThrowIfMediaUnusable(MediaPlayer player, string path, string? mediaError)
    {
        if (!string.IsNullOrWhiteSpace(mediaError))
        {
            throw new InvalidOperationException(
                BuildUnsupportedMediaMessage(path, mediaError));
        }

        // サムネ（シェル）は出ても、MF がデコードできないと duration が 0 のまま黒画面になる
        if (!IsImageFile(path) && player.PlaybackSession.NaturalDuration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                BuildUnsupportedMediaMessage(path, "デコーダーが動画を開けませんでした"));
        }

        if (IsImageFile(path))
        {
            var w = player.PlaybackSession.NaturalVideoWidth;
            var h = player.PlaybackSession.NaturalVideoHeight;
            if (w <= 0 || h <= 0)
            {
                throw new InvalidOperationException(
                    BuildUnsupportedMediaMessage(path, "画像を表示できませんでした"));
            }
        }
    }

    private static string BuildUnsupportedMediaMessage(string path, string detail)
    {
        var ext = Path.GetExtension(path);
        return $"再生できません ({ext}): {detail}。サムネだけ出る場合があります。H.264 の mp4 へ変換してください。";
    }

    private sealed class ActionDisposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
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

    private static void ForcePausedFrameRefresh(MediaPlayer player, double restoreRate = 1.0)
    {
        try
        {
            // ポーズ中コマ更新。通常速度の一瞬 Play はクリック音が漏れやすいので Rate=0。
            player.IsMuted = true;
            player.Volume = 0;
            var session = player.PlaybackSession;
            try
            {
                session.PlaybackRate = 0;
            }
            catch
            {
                session.PlaybackRate = 0.01;
            }

            player.Play();
            player.IsMuted = true;
            player.Volume = 0;
            player.Pause();
            session.PlaybackRate = restoreRate <= 0 ? 1.0 : restoreRate;
            player.IsMuted = true;
            player.Volume = 0;
        }
        catch
        {
            // ignore refresh failures
        }
    }

    private void ApplyMutePolicy()
    {
        // 単一プレイヤー: 等速再生中だけ音声 ON（変速・スクラブ中はミュート）。
        var rateMute = Math.Abs(ClockRate - 1.0) > 0.001;
        var mute = rateMute || !IsPlaying || _scrubPreviewActive;
        foreach (var pair in _displayPairs)
        {
            pair.Player.IsMuted = mute;
            pair.Player.Volume = mute ? 0 : 1.0;
        }
    }

    private static async Task WaitForOpenedAsync(
        MediaPlayer player,
        CancellationToken cancellationToken = default,
        string? path = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var session = player.PlaybackSession;
        if (session.PlaybackState is not MediaPlaybackState.Opening)
        {
            await WaitForMediaReadyAsync(player, cancellationToken, path).ConfigureAwait(false);
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
            await WaitForMediaReadyAsync(player, cancellationToken, path).ConfigureAwait(false);
            return;
        }

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000, cancellationToken)).ConfigureAwait(false);
        if (completed != tcs.Task)
            session.PlaybackStateChanged -= Handler;

        cancellationToken.ThrowIfCancellationRequested();
        await WaitForMediaReadyAsync(player, cancellationToken, path).ConfigureAwait(false);
    }

    private static async Task WaitForMediaReadyAsync(
        MediaPlayer player,
        CancellationToken cancellationToken,
        string? path = null)
    {
        var session = player.PlaybackSession;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var duration = session.NaturalDuration;
            if (duration > TimeSpan.Zero)
                break;

            if (path is not null && IsImageFile(path))
            {
                if (session.NaturalVideoWidth > 0 && session.NaturalVideoHeight > 0)
                    break;
            }

            await Task.Delay(16, cancellationToken).ConfigureAwait(false);
        }

        await Task.Delay(32, cancellationToken).ConfigureAwait(false);
    }

    private void CancelImageLoopAdvance()
    {
        try { _imageLoopCts?.Cancel(); } catch { /* ignore */ }
        _imageLoopCts?.Dispose();
        _imageLoopCts = null;
    }

    private void ScheduleImageLoopAdvance()
    {
        CancelImageLoopAdvance();
        _imageLoopCts = new CancellationTokenSource();
        var token = _imageLoopCts.Token;
        var path = CurrentPath;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ImageLoopHoldMs, token).ConfigureAwait(false);
                if (token.IsCancellationRequested || _disposed)
                    return;

                var dq = App.DispatcherQueue;
                if (dq is null)
                    return;

                dq.TryEnqueue(() =>
                {
                    if (_disposed || !IsPlaying || path is null)
                        return;
                    if (!string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase))
                        return;
                    if (!IsImageFile(path))
                        return;
                    if (!RemoteNetaLoopService.Instance.IsRunning)
                        return;

                    Pause();
                    MediaEnded?.Invoke();
                });
            }
            catch (OperationCanceledException)
            {
                // loop cancelled
            }
        }, token);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        CancelImageLoopAdvance();

        _disposed = true;
        _warmCts?.Cancel();
        _warmCts?.Dispose();
        _warmParallel.Dispose();
        _warmCache.Dispose();

        foreach (var pair in _displayPairs)
            pair.Dispose();
    }
}
