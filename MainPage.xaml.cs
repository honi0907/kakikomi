using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Kakikomi.Services;
using Kakikomi.ViewModels;

namespace Kakikomi;

public sealed partial class MainPage : Page
{
    private static readonly Color IdleBg = Color.FromArgb(255, 248, 250, 252);
    private static readonly Color IdleFg = Color.FromArgb(255, 15, 23, 42);
    private static readonly Color IdleBorder = Color.FromArgb(255, 71, 85, 105);
    private static readonly Color SelectedRateBg = Color.FromArgb(255, 13, 148, 136);
    private static readonly Color SelectedRateBorder = Color.FromArgb(255, 15, 118, 110);
    private static readonly Color White = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color PlayStoppedBg = Color.FromArgb(255, 220, 38, 38);
    private static readonly Color PlayStoppedBorder = Color.FromArgb(255, 127, 29, 29);
    private static readonly Color PlayPlayingBg = Color.FromArgb(255, 37, 99, 235);
    private static readonly Color PlayPlayingBorder = Color.FromArgb(255, 30, 64, 175);
    private static readonly Color DisabledBg = Color.FromArgb(255, 51, 65, 85);
    private static readonly Color DisabledFg = Color.FromArgb(255, 148, 163, 184);
    private static readonly Color DisabledBorder = Color.FromArgb(255, 30, 41, 59);

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
        OperatorPlayerA.SetMediaPlayer(ViewModel.Session.GetOperatorPlayerForSlot(0));
        OperatorPlayerB.SetMediaPlayer(ViewModel.Session.GetOperatorPlayerForSlot(1));
        UpdateOperatorSlotVisibility(ViewModel.Session.VisibleSlotIndex);
        InkLayer.Attach(ViewModel.Session, inputEnabled: true);
        ViewModel.PenChanged += OnPenChanged;
        ViewModel.TimelineSliderSync += OnTimelineSliderSync;
        AppSettings.Changed += OnAppSettingsChanged;
        App.SettingsOpenFailed += OnSettingsOpenFailed;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        InkLayer.IsHitTestVisible = !ViewModel.IsPlaying;

        ApplyButtonBrushes(PickFolderBtn);
        ApplyButtonBrushes(RefreshBtn);
        ApplyButtonBrushes(EditModeBtn);
        ApplyButtonBrushes(SettingsBtn);
        ApplyButtonBrushes(SkipBackBtn);
        ApplyButtonBrushes(SkipForwardBtn);
        ApplyButtonBrushes(ClearInkBtn);
        ApplyButtonBrushes(PenThicknessMinusBtn);
        ApplyButtonBrushes(PenThicknessPlusBtn);

        UpdatePlayToggleLook();
        UpdateRateToggleLooks();
        UpdateEraserToggleLook();
        UpdateEditModeButtonLook();
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
        OperatorPlayerA.SetMediaPlayer(session.GetOperatorPlayerForSlot(0));
        OperatorPlayerB.SetMediaPlayer(session.GetOperatorPlayerForSlot(1));
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
            UpdateEditModeButtonLook();

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

    private static void ApplyButtonBrushes(FrameworkElement button)
    {
        var hover = Color.FromArgb(255, 226, 232, 240);
        var pressed = Color.FromArgb(255, 203, 213, 225);
        var borderHot = Color.FromArgb(255, 51, 65, 85);

        SetBrush(button, "ButtonBackground", IdleBg);
        SetBrush(button, "ButtonBackgroundPointerOver", hover);
        SetBrush(button, "ButtonBackgroundPressed", pressed);
        SetBrush(button, "ButtonBackgroundDisabled", Color.FromArgb(255, 71, 85, 105));
        SetBrush(button, "ButtonForeground", IdleFg);
        SetBrush(button, "ButtonForegroundPointerOver", IdleFg);
        SetBrush(button, "ButtonForegroundPressed", Color.FromArgb(255, 30, 41, 59));
        SetBrush(button, "ButtonForegroundDisabled", Color.FromArgb(255, 148, 163, 184));
        SetBrush(button, "ButtonBorderBrush", IdleBorder);
        SetBrush(button, "ButtonBorderBrushPointerOver", borderHot);
        SetBrush(button, "ButtonBorderBrushPressed", Color.FromArgb(255, 30, 41, 59));
        SetBrush(button, "ButtonBorderBrushDisabled", Color.FromArgb(255, 51, 65, 85));

        if (button is Control control)
            ApplyChrome(control, IdleBg, IdleFg, IdleBorder);
    }

    private void UpdatePlayToggleLook()
    {
        if (ViewModel.IsPlaying)
            ApplyChrome(PlayToggle, PlayPlayingBg, White, PlayPlayingBorder);
        else
            ApplyChrome(PlayToggle, PlayStoppedBg, White, PlayStoppedBorder);
    }

    private void UpdateRateToggleLooks()
    {
        ApplySelectionChrome(RateNormalBtn, ViewModel.IsRateNormal, SelectedRateBg, SelectedRateBorder);
        ApplySelectionChrome(RateHalfBtn, ViewModel.IsRateHalf, SelectedRateBg, SelectedRateBorder);
        ApplySelectionChrome(RateQuarterBtn, ViewModel.IsRateQuarter, SelectedRateBg, SelectedRateBorder);
        ApplySelectionChrome(RateDoubleBtn, ViewModel.IsRateDouble, SelectedRateBg, SelectedRateBorder);
    }

    private void UpdateEraserToggleLook()
    {
        var checkedBg = Color.FromArgb(255, 71, 85, 105);
        var checkedBorder = Color.FromArgb(255, 51, 65, 85);
        ApplySelectionChrome(PenEraserBtn, ViewModel.IsPenEraser, checkedBg, checkedBorder);
    }

    private static void ApplySelectionChrome(Control button, bool selected, Color selectedBg, Color selectedBorder)
    {
        if (selected)
            ApplyChrome(button, selectedBg, White, selectedBorder);
        else
            ApplyChrome(button, IdleBg, IdleFg, IdleBorder);
    }

    private static void ApplyChrome(Control button, Color background, Color foreground, Color border)
    {
        button.Background = new SolidColorBrush(background);
        button.Foreground = new SolidColorBrush(foreground);
        button.BorderBrush = new SolidColorBrush(border);
    }

    private static void SetBrush(FrameworkElement element, string key, Color color)
    {
        element.Resources[key] = new SolidColorBrush(color);
    }

    private void UpdateEditModeButtonLook()
    {
        if (ViewModel.IsEditMode)
        {
            ApplyChrome(EditModeBtn, SelectedRateBg, White, SelectedRateBorder);
            ApplyChrome(PickFolderBtn, IdleBg, IdleFg, IdleBorder);
            ApplyChrome(RefreshBtn, IdleBg, IdleFg, IdleBorder);
            ApplyChrome(SettingsBtn, IdleBg, IdleFg, IdleBorder);
        }
        else
        {
            ApplyChrome(EditModeBtn, IdleBg, Color.FromArgb(255, 100, 116, 139), IdleBorder);
            ApplyChrome(PickFolderBtn, DisabledBg, DisabledFg, DisabledBorder);
            ApplyChrome(RefreshBtn, DisabledBg, DisabledFg, DisabledBorder);
            ApplyChrome(SettingsBtn, DisabledBg, DisabledFg, DisabledBorder);
        }
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
