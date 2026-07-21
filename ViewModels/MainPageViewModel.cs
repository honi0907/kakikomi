using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Windows.UI;
using Kakikomi.Helpers;
using Kakikomi.Models;
using Kakikomi.Services;

namespace Kakikomi.ViewModels;

public partial class MainPageViewModel : ObservableObject
{
    private readonly EngineSession _session;
    private readonly InkAutoSave _inkAutoSave;
    private readonly DispatcherQueueTimer _timelineTimer;
    private CancellationTokenSource? _thumbnailLoadCts;
    private bool _isUserSeeking;
    private double _timelineDurationSeconds;
    private DateTime _lastPreviewSeekUtc;

    public ObservableCollection<NetaItem> NetaItems { get; } = [];

    [ObservableProperty]
    private NetaItem? selectedNeta;

    [ObservableProperty]
    private string statusText = "ネタフォルダを選んでください";

    [ObservableProperty]
    private string rateLabel = "等速";

    [ObservableProperty]
    private bool isPlaying;

    [ObservableProperty]
    private bool isRateNormal = true;

    [ObservableProperty]
    private bool isRateHalf;

    [ObservableProperty]
    private bool isRateQuarter;

    [ObservableProperty]
    private bool isRateDouble;

    [ObservableProperty]
    private bool isPenRed = true;

    [ObservableProperty]
    private bool isPenGreen;

    [ObservableProperty]
    private bool isPenBlue;

    [ObservableProperty]
    private bool isPenEraser;

    [ObservableProperty]
    private bool isEditMode = true;

    [ObservableProperty]
    private double timelinePosition;

    [ObservableProperty]
    private double timelineMaximum;

    [ObservableProperty]
    private string timelineText = "0:00 / 0:00";

    [ObservableProperty]
    private bool hasTimeline;

    public bool IsUserSeeking => _isUserSeeking;

    public event Action<double, double>? TimelineSliderSync;

    public EngineSession Session => _session;

    public event Action<Color, double, bool>? PenChanged;

    public MainPageViewModel(EngineSession session)
    {
        _session = session;
        _session.PlaybackStateChanged += OnPlaybackStateChanged;
        _session.SourceChanged += OnTimelineEvent;
        _session.TimelineChanged += OnTimelineEvent;

        var dq = DispatcherQueue.GetForCurrentThread();
        _inkAutoSave = new InkAutoSave(session, dq);

        _timelineTimer = dq.CreateTimer();
        _timelineTimer.Interval = TimeSpan.FromMilliseconds(200);
        _timelineTimer.Tick += (_, _) => RefreshTimeline();
    }

    public void BeginTimelineSeek()
    {
        if (_isUserSeeking)
            return;

        _isUserSeeking = true;
        if (_session.IsPlaying)
            _session.Pause();

        _timelineTimer.Stop();
    }

    public void EndTimelineSeek(double seconds)
    {
        if (!_isUserSeeking)
            return;

        _isUserSeeking = false;
        ApplyTimelineSeek(seconds, preview: false);
        SyncTimelineSlider(seconds, _timelineDurationSeconds);
    }

    public void OnTimelineSliderChanged(double seconds)
    {
        ApplyTimelineSeek(seconds, preview: true);
    }

    private void ApplyTimelineSeek(double seconds, bool preview)
    {
        if (double.IsNaN(seconds) || seconds < 0)
            seconds = 0;

        var duration = _timelineDurationSeconds;
        if (duration <= 0)
        {
            duration = _session.TimelineDuration.TotalSeconds;
            if (!double.IsNaN(duration) && duration > 0)
                _timelineDurationSeconds = duration;
        }

        if (duration > 0 && seconds > duration)
            seconds = duration;

        TimelinePosition = seconds;
        UpdateTimelineText(seconds, duration > 0 ? duration : TimelineMaximum);

        if (preview)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastPreviewSeekUtc).TotalMilliseconds < 33)
                return;

            _lastPreviewSeekUtc = now;
            _session.SeekTo(TimeSpan.FromSeconds(seconds), syncClean: false, notifyTimeline: false);
            return;
        }

        _session.SeekTo(TimeSpan.FromSeconds(seconds), syncClean: true, notifyTimeline: false);
    }

    private void SyncTimelineSlider(double positionSeconds, double durationSeconds)
    {
        TimelinePosition = positionSeconds;
        TimelineMaximum = durationSeconds;
        TimelineSliderSync?.Invoke(positionSeconds, durationSeconds);
    }

    public async Task LoadSavedFolderAsync()
    {
        try
        {
            var items = await _session.TryLoadSavedFolderAsync();
            if (items is null)
            {
                StatusText = "ネタフォルダを選んでください（MP4 など）";
                return;
            }

            ReplaceNetaList(items);
            StatusText = $"フォルダ読込: {NetaItems.Count} 本";
        }
        catch
        {
            StatusText = "ネタフォルダを選んでください（MP4 など）";
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseEditControls))]
    private async Task PickFolderAsync()
    {
        string? path;
        try
        {
            path = NativeFolderPicker.PickFolder(App.WindowHandle);
            if (string.IsNullOrWhiteSpace(path))
                return;
        }
        catch (Exception ex)
        {
            StatusText = $"フォルダ選択失敗: {ex.Message}";
            return;
        }

        try
        {
            var items = await _session.LoadNetaFolderFromPathAsync(path);
            ReplaceNetaList(items);
            var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name))
                name = path;
            StatusText = items.Count == 0
                ? $"{name}（動画なし。mp4/mov/mkv/wmv を入れて再選択）"
                : $"{name}（{NetaItems.Count} 本）";
        }
        catch (Exception ex)
        {
            StatusText = $"読込失敗: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseEditControls))]
    private async Task RefreshFolderAsync()
    {
        var items = await _session.TryLoadSavedFolderAsync();
        if (items is null)
        {
            StatusText = "保存済みフォルダがありません。フォルダ選択してください";
            return;
        }

        ReplaceNetaList(items);
        StatusText = $"更新: {NetaItems.Count} 本";
    }

    partial void OnSelectedNetaChanged(NetaItem? value)
    {
        if (value is null)
            return;

        _ = OpenSelectedAsync(value);
    }

    private async Task OpenSelectedAsync(NetaItem item)
    {
        try
        {
            _inkAutoSave.FlushNow();
            await _session.OpenNetaAsync(item);
            StatusText = $"読込: {item.DisplayName}（先頭ポーズ）";
            UpdatePlaybackLabels();
        }
        catch (Exception ex)
        {
            StatusText = $"読込失敗: {ex.Message}";
        }
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (!_session.IsPlaying)
            _inkAutoSave.FlushNow();
        _session.TogglePlayPause();
    }

    [RelayCommand]
    private void RateNormal()
    {
        _session.SetRate(1.0);
        SetRateSelection(normal: true);
    }

    [RelayCommand]
    private void RateHalf()
    {
        _session.SetRate(0.5);
        SetRateSelection(half: true);
    }

    [RelayCommand]
    private void RateQuarter()
    {
        _session.SetRate(0.25);
        SetRateSelection(quarter: true);
    }

    [RelayCommand]
    private void RateDouble()
    {
        _session.SetRate(2.0);
        SetRateSelection(dbl: true);
    }

    [RelayCommand]
    private void SkipBack() => _session.SeekRelative(TimeSpan.FromSeconds(-5));

    [RelayCommand]
    private void SkipForward() => _session.SeekRelative(TimeSpan.FromSeconds(5));

    [RelayCommand]
    private void ClearInk()
    {
        _inkAutoSave.Cancel();
        _session.ClearStrokes();
    }

    [RelayCommand]
    private void SelectPenRed()
    {
        SetPenSelection(red: true);
        PenChanged?.Invoke(AppSettings.PenRed, AppSettings.PenThickness, false);
    }

    [RelayCommand]
    private void SelectPenGreen()
    {
        SetPenSelection(green: true);
        PenChanged?.Invoke(AppSettings.PenGreen, AppSettings.PenThickness, false);
    }

    [RelayCommand]
    private void SelectPenBlue()
    {
        SetPenSelection(blue: true);
        PenChanged?.Invoke(AppSettings.PenBlue, AppSettings.PenThickness, false);
    }

    [RelayCommand]
    private void SelectPenEraser()
    {
        SetPenSelection(eraser: true);
        PenChanged?.Invoke(Color.FromArgb(255, 148, 163, 184), AppSettings.EraserThickness, true);
    }

    public void ReapplyActivePen()
    {
        if (IsPenRed)
            SelectPenRed();
        else if (IsPenGreen)
            SelectPenGreen();
        else if (IsPenBlue)
            SelectPenBlue();
        else if (IsPenEraser)
            SelectPenEraser();
    }

    [RelayCommand]
    private void ToggleEditMode()
    {
        if (IsEditMode)
        {
            IsEditMode = false;
            _editModeUnlockPressCount = 0;
            _lastEditUnlockPressUtc = null;
            // 編集オフ中は設定も触れない
            try { App.SettingsWindowInstance?.Close(); } catch { /* ignore */ }
            return;
        }

        var now = DateTime.UtcNow;
        // 3連打は短い間隔のみ有効（間隔が空いたらやり直し）
        if (_lastEditUnlockPressUtc is null
            || (now - _lastEditUnlockPressUtc.Value) > EditModeUnlockTapWindow)
        {
            _editModeUnlockPressCount = 1;
        }
        else
        {
            _editModeUnlockPressCount++;
        }

        _lastEditUnlockPressUtc = now;

        if (_editModeUnlockPressCount >= 3)
        {
            IsEditMode = true;
            _editModeUnlockPressCount = 0;
            _lastEditUnlockPressUtc = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseEditControls))]
    private void OpenSettings() => App.OpenSettingsWindow();

    private int _editModeUnlockPressCount;
    private DateTime? _lastEditUnlockPressUtc;
    private static readonly TimeSpan EditModeUnlockTapWindow = TimeSpan.FromMilliseconds(450);

    private bool CanUseEditControls() => IsEditMode;

    partial void OnIsEditModeChanged(bool value)
    {
        PickFolderCommand.NotifyCanExecuteChanged();
        RefreshFolderCommand.NotifyCanExecuteChanged();
        OpenSettingsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectPrevious()
    {
        if (NetaItems.Count == 0 || SelectedNeta is null)
            return;

        var index = NetaItems.IndexOf(SelectedNeta);
        if (index > 0)
            SelectedNeta = NetaItems[index - 1];
    }

    [RelayCommand]
    private void SelectNext()
    {
        if (NetaItems.Count == 0)
            return;

        if (SelectedNeta is null)
        {
            SelectedNeta = NetaItems[0];
            return;
        }

        var index = NetaItems.IndexOf(SelectedNeta);
        if (index >= 0 && index < NetaItems.Count - 1)
            SelectedNeta = NetaItems[index + 1];
    }

    public void SetPenRed() => SelectPenRed();
    public void SetPenGreen() => SelectPenGreen();
    public void SetPenBlue() => SelectPenBlue();

    private void SetRateSelection(bool normal = false, bool half = false, bool quarter = false, bool dbl = false)
    {
        IsRateNormal = normal;
        IsRateHalf = half;
        IsRateQuarter = quarter;
        IsRateDouble = dbl;
    }

    private void SetPenSelection(bool red = false, bool green = false, bool blue = false, bool eraser = false)
    {
        IsPenRed = red;
        IsPenGreen = green;
        IsPenBlue = blue;
        IsPenEraser = eraser;
    }

    private void ReplaceNetaList(IReadOnlyList<NetaItem> items)
    {
        _thumbnailLoadCts?.Cancel();
        _thumbnailLoadCts?.Dispose();
        _thumbnailLoadCts = new CancellationTokenSource();
        var token = _thumbnailLoadCts.Token;

        NetaItems.Clear();
        foreach (var item in items)
            NetaItems.Add(item);

        _ = LoadThumbnailsAsync(items, token);
    }

    private static async Task LoadThumbnailsAsync(IReadOnlyList<NetaItem> items, CancellationToken token)
    {
        try
        {
            await VideoThumbnailLoader.LoadAsync(items, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 一覧差し替え
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ThumbnailLoad] {ex}");
        }
    }

    private void OnTimelineEvent()
    {
        var dq = App.DispatcherQueue;
        if (dq.HasThreadAccess)
            RefreshTimeline();
        else
            dq.TryEnqueue(RefreshTimeline);
    }

    private void RefreshTimeline()
    {
        if (_isUserSeeking)
            return;

        var duration = _session.TimelineDuration;
        var max = duration.TotalSeconds;
        if (double.IsNaN(max) || max < 0)
            max = 0;

        _timelineDurationSeconds = max;
        TimelineMaximum = max;
        HasTimeline = _session.CurrentPath is not null && max > 0;

        var pos = _session.TimelinePosition.TotalSeconds;
        if (double.IsNaN(pos) || pos < 0)
            pos = 0;
        if (max > 0 && pos > max)
            pos = max;

        SyncTimelineSlider(pos, max);
        UpdateTimelineText(pos, max);
    }

    private void UpdateTimelineText(double positionSeconds, double durationSeconds)
    {
        TimelineText = $"{FormatClock(positionSeconds)} / {FormatClock(durationSeconds)}";
    }

    private static string FormatClock(double totalSeconds)
    {
        if (double.IsNaN(totalSeconds) || totalSeconds < 0)
            totalSeconds = 0;

        totalSeconds = Math.Round(totalSeconds, 1);
        var time = TimeSpan.FromSeconds(totalSeconds);
        var tenths = (int)Math.Round((totalSeconds - Math.Floor(totalSeconds)) * 10);
        if (tenths >= 10)
        {
            totalSeconds = Math.Floor(totalSeconds) + 1;
            time = TimeSpan.FromSeconds(totalSeconds);
            tenths = 0;
        }

        if (time.TotalHours >= 1)
            return $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}.{tenths}";

        return $"{(int)time.TotalMinutes}:{time.Seconds:D2}.{tenths}";
    }

    private void OnPlaybackStateChanged()
    {
        var dq = App.DispatcherQueue;
        if (dq.HasThreadAccess)
            UpdatePlaybackLabels();
        else
            dq.TryEnqueue(UpdatePlaybackLabels);
    }

    private void UpdatePlaybackLabels()
    {
        IsPlaying = _session.IsPlaying;
        if (_session.IsPlaying)
            _timelineTimer.Start();
        else
            _timelineTimer.Stop();

        RefreshTimeline();
        RateLabel = Math.Abs(_session.ClockRate - 1.0) < 0.001
            ? "等速（音声ON）"
            : $"{_session.ClockRate:0.##}x（音声ミュート）";

        var rate = _session.ClockRate;
        SetRateSelection(
            normal: Math.Abs(rate - 1.0) < 0.001,
            half: Math.Abs(rate - 0.5) < 0.001,
            quarter: Math.Abs(rate - 0.25) < 0.001,
            dbl: Math.Abs(rate - 2.0) < 0.001);
    }
}
