using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Kakikomi.Services;
using Kakikomi.ViewModels;

namespace Kakikomi;

public sealed partial class MainPage : Page
{
    private static readonly Color IdleBg = Color.FromArgb(255, 85, 106, 132);
    private static readonly Color IdleFg = Color.FromArgb(255, 248, 250, 252);
    private static readonly Color SelectedRateBg = Color.FromArgb(255, 13, 148, 136);
    private static readonly Color PlayStoppedBg = Color.FromArgb(255, 220, 38, 38);
    private static readonly Color PlayPlayingBg = Color.FromArgb(255, 37, 99, 235);
    private static readonly Color DisabledBg = Color.FromArgb(255, 46, 58, 79);
    private static readonly Color DisabledFg = Color.FromArgb(255, 148, 163, 184);
    private static readonly Color White = Color.FromArgb(255, 255, 255, 255);

    public MainPageViewModel ViewModel { get; }

    public MainPage()
    {
        var session = App.Engine ?? throw new InvalidOperationException("Engine is not ready.");
        ViewModel = new MainPageViewModel(session);
        App.MainViewModel = ViewModel;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            if (ReferenceEquals(App.MainViewModel, ViewModel))
                App.MainViewModel = null;
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        OperatorPlayerA.Attach(ViewModel.Session.GetOperatorPlayerForSlot(0));
        OperatorPlayerB.Attach(ViewModel.Session.GetOperatorPlayerForSlot(1));
        UpdateOperatorSlotVisibility(ViewModel.Session.VisibleSlotIndex);
        InkLayer.Attach(ViewModel.Session, inputEnabled: true);
        ViewModel.PenChanged += OnPenChanged;
        ViewModel.TimelineSliderSync += OnTimelineSliderSync;
        AppSettings.Changed += OnAppSettingsChanged;
        App.SettingsOpenFailed += OnSettingsOpenFailed;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        InkLayer.IsHitTestVisible = !ViewModel.IsPlaying;

        ApplyChrome(PlayToggle, IdleBg, IdleFg);
        ApplyIdleChromeToSecondaryButtons();
        UpdatePlayToggleLook();
        UpdateRateToggleLooks();
        UpdateEraserToggleLook();
        UpdateEditModeButtonLook();
        UpdateNetaListReorderMode();
        ApplyPenSwatches();
        UpdateDemoWatermark();
        ViewModel.SetPenRed();
        OnTimelineSliderSync(ViewModel.TimelinePosition, ViewModel.TimelineMaximum);
        SubscribeVisibleSlotUi(ViewModel.Session);
        await ViewModel.LoadSavedFolderAsync();
        _ = ViewModel.CheckForUpdatesOnStartupAsync();
    }

    private void SubscribeVisibleSlotUi(EngineSession session)
    {
        session.VisibleSlotChanged += OnVisibleSlotChanged;
    }

    private void OnVisibleSlotChanged(int visibleSlotIndex)
    {
        var dq = App.DispatcherQueue;
        if (dq.HasThreadAccess)
            ApplyVisibleSlot(visibleSlotIndex);
        else
            dq.TryEnqueue(() => ApplyVisibleSlot(visibleSlotIndex));
    }

    private void ApplyVisibleSlot(int visibleSlotIndex)
    {
        var session = ViewModel.Session;
        OperatorPlayerA.Attach(session.GetOperatorPlayerForSlot(0));
        OperatorPlayerB.Attach(session.GetOperatorPlayerForSlot(1));
        UpdateOperatorSlotVisibility(visibleSlotIndex);
    }

    private void UpdateOperatorSlotVisibility(int visibleSlotIndex)
    {
        OperatorPlayerA.Visibility = visibleSlotIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        OperatorPlayerB.Visibility = visibleSlotIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.IsEditMode))
        {
            UpdateEditModeButtonLook();
            UpdateNetaListReorderMode();
        }

        if (e.PropertyName == nameof(ViewModel.IsPlaying))
        {
            InkLayer.IsHitTestVisible = !ViewModel.IsPlaying;
            UpdatePlayToggleLook();
        }

        if (e.PropertyName is nameof(ViewModel.IsRateNormal)
            or nameof(ViewModel.IsRateHalf)
            or nameof(ViewModel.IsRateQuarter)
            or nameof(ViewModel.IsRateDouble))
        {
            UpdateRateToggleLooks();
        }

        if (e.PropertyName == nameof(ViewModel.IsPenEraser))
            UpdateEraserToggleLook();
    }

    private void OnAppSettingsChanged()
    {
        var dq = App.DispatcherQueue;
        if (dq.HasThreadAccess)
            ApplySettingsToUi();
        else
            dq.TryEnqueue(ApplySettingsToUi);
    }

    private void OnSettingsOpenFailed(string message) =>
        ViewModel.StatusText = $"設定画面を開けませんでした: {message}";

    private void ApplySettingsToUi()
    {
        ApplyPenSwatches();
        ViewModel.ReapplyActivePen();
        UpdateDemoWatermark();
    }

    private void UpdateDemoWatermark() =>
        DemoWatermark.Visibility = AppSettings.DemoMode ? Visibility.Visible : Visibility.Collapsed;

    private void ApplyPenSwatches()
    {
        PenRedSwatch.Fill = new SolidColorBrush(AppSettings.PenRed);
        PenGreenSwatch.Fill = new SolidColorBrush(AppSettings.PenGreen);
        PenBlueSwatch.Fill = new SolidColorBrush(AppSettings.PenBlue);
    }

    private static void ApplyChrome(Control button, Color background, Color foreground)
    {
        button.Background = new SolidColorBrush(background);
        button.Foreground = new SolidColorBrush(foreground);
        button.BorderThickness = new Thickness(0);
        button.BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
    }

    private void ApplyIdleChromeToSecondaryButtons()
    {
        ApplyChrome(SkipBackBtn, IdleBg, IdleFg);
        ApplyChrome(SkipForwardBtn, IdleBg, IdleFg);
        ApplyChrome(ClearInkBtn, IdleBg, IdleFg);
        ApplyChrome(PenThicknessMinusBtn, IdleBg, IdleFg);
        ApplyChrome(PenThicknessPlusBtn, IdleBg, IdleFg);
    }

    private void UpdatePlayToggleLook()
    {
        if (ViewModel.IsPlaying)
            ApplyChrome(PlayToggle, PlayPlayingBg, White);
        else
            ApplyChrome(PlayToggle, PlayStoppedBg, White);
    }

    private void UpdateRateToggleLooks()
    {
        ApplySelectionChrome(RateNormalBtn, ViewModel.IsRateNormal, SelectedRateBg);
        ApplySelectionChrome(RateHalfBtn, ViewModel.IsRateHalf, SelectedRateBg);
        ApplySelectionChrome(RateQuarterBtn, ViewModel.IsRateQuarter, SelectedRateBg);
        ApplySelectionChrome(RateDoubleBtn, ViewModel.IsRateDouble, SelectedRateBg);
    }

    private void UpdateEraserToggleLook()
    {
        var checkedBg = Color.FromArgb(255, 71, 85, 105);
        ApplySelectionChrome(PenEraserBtn, ViewModel.IsPenEraser, checkedBg);
    }

    private static void ApplySelectionChrome(Control button, bool selected, Color selectedBg)
    {
        if (selected)
            ApplyChrome(button, selectedBg, White);
        else
            ApplyChrome(button, IdleBg, IdleFg);
    }

    private void UpdateEditModeButtonLook()
    {
        if (ViewModel.IsEditMode)
        {
            ApplyChrome(EditModeBtn, SelectedRateBg, White);
            ApplyChrome(AddNetaBtn, IdleBg, IdleFg);
            ApplyChrome(SettingsBtn, IdleBg, IdleFg);
        }
        else
        {
            ApplyChrome(EditModeBtn, IdleBg, Color.FromArgb(255, 148, 163, 184));
            ApplyChrome(AddNetaBtn, DisabledBg, DisabledFg);
            ApplyChrome(SettingsBtn, DisabledBg, DisabledFg);
        }
    }

    private void UpdateNetaListReorderMode()
    {
        var enabled = ViewModel.IsEditMode;
        NetaList.CanDragItems = enabled;
        NetaList.CanReorderItems = enabled;
        NetaList.AllowDrop = enabled;
    }

    private void OnPenChanged(Color color, double thickness, bool isEraser) =>
        InkLayer.SetPen(color, thickness, isEraser);

    private void OnTimelineSliderSync(double positionSeconds, double durationSeconds)
    {
        if (ViewModel.IsUserSeeking)
            return;

        TimelineSeek.SetRange(positionSeconds, durationSeconds);
    }

    private void OnTimelineSeekStarted(object sender, double seconds)
    {
        ViewModel.BeginTimelineSeek();
        ViewModel.OnTimelineSliderChanged(seconds);
    }

    private void OnTimelineSeekPreview(object sender, double seconds) =>
        ViewModel.OnTimelineSliderChanged(seconds);

    private void OnTimelineSeekCompleted(object sender, double seconds) =>
        ViewModel.EndTimelineSeek(seconds);
}
