using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Kakikomi.Services;
using Kakikomi.ViewModels;

namespace Kakikomi;

public sealed partial class MainPage : Page
{
    public MainPageViewModel ViewModel { get; }

    public MainPage()
    {
        var session = App.Engine ?? throw new InvalidOperationException("Engine is not ready.");
        ViewModel = new MainPageViewModel(session);
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        OperatorPlayer.SetMediaPlayer(ViewModel.Session.OperatorPlayer);
        InkLayer.Attach(ViewModel.Session, inputEnabled: true);
        ViewModel.PenChanged += OnPenChanged;
        ViewModel.TimelineSliderSync += OnTimelineSliderSync;
        AppSettings.Changed += OnAppSettingsChanged;
        App.SettingsOpenFailed += OnSettingsOpenFailed;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.IsEditMode))
                UpdateEditModeButtonLook();
            if (e.PropertyName == nameof(ViewModel.IsPlaying))
                InkLayer.IsHitTestVisible = !ViewModel.IsPlaying;
        };
        InkLayer.IsHitTestVisible = !ViewModel.IsPlaying;
        ApplyAntiWhiteHoverBrushes();
        UpdateEditModeButtonLook();
        ApplyPenSwatches();
        UpdateDemoWatermark();
        ViewModel.SetPenRed();
        OnTimelineSliderSync(ViewModel.TimelinePosition, ViewModel.TimelineMaximum);
        await ViewModel.LoadSavedFolderAsync();
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

    private void ApplyAntiWhiteHoverBrushes()
    {
        ApplyButtonBrushes(PickFolderBtn);
        ApplyButtonBrushes(RefreshBtn);
        ApplyButtonBrushes(EditModeBtn);
        ApplyButtonBrushes(SettingsBtn);
        ApplyButtonBrushes(SkipBackBtn);
        ApplyButtonBrushes(SkipForwardBtn);
        ApplyButtonBrushes(ClearInkBtn);

        ApplyPlayToggleBrushes(PlayToggle);
        ApplyToggleBrushes(RateNormalBtn, checkedBg: Color.FromArgb(255, 13, 148, 136), checkedHover: Color.FromArgb(255, 20, 184, 166), checkedPressed: Color.FromArgb(255, 15, 118, 110));
        ApplyToggleBrushes(RateHalfBtn, checkedBg: Color.FromArgb(255, 13, 148, 136), checkedHover: Color.FromArgb(255, 20, 184, 166), checkedPressed: Color.FromArgb(255, 15, 118, 110));
        ApplyToggleBrushes(RateQuarterBtn, checkedBg: Color.FromArgb(255, 13, 148, 136), checkedHover: Color.FromArgb(255, 20, 184, 166), checkedPressed: Color.FromArgb(255, 15, 118, 110));
        ApplyToggleBrushes(RateDoubleBtn, checkedBg: Color.FromArgb(255, 13, 148, 136), checkedHover: Color.FromArgb(255, 20, 184, 166), checkedPressed: Color.FromArgb(255, 15, 118, 110));

        ApplyToggleBrushes(PenEraserBtn, checkedBg: Color.FromArgb(255, 71, 85, 105), checkedHover: Color.FromArgb(255, 100, 116, 139), checkedPressed: Color.FromArgb(255, 51, 65, 85));
    }

    private static void ApplyButtonBrushes(FrameworkElement button)
    {
        var normal = Color.FromArgb(255, 248, 250, 252);
        var hover = Color.FromArgb(255, 226, 232, 240);
        var pressed = Color.FromArgb(255, 203, 213, 225);
        var fg = Color.FromArgb(255, 15, 23, 42);
        var border = Color.FromArgb(255, 203, 213, 225);
        var borderHot = Color.FromArgb(255, 148, 163, 184);

        SetBrush(button, "ButtonBackground", normal);
        SetBrush(button, "ButtonBackgroundPointerOver", hover);
        SetBrush(button, "ButtonBackgroundPressed", pressed);
        SetBrush(button, "ButtonBackgroundDisabled", Color.FromArgb(255, 71, 85, 105));
        SetBrush(button, "ButtonForeground", fg);
        SetBrush(button, "ButtonForegroundPointerOver", fg);
        SetBrush(button, "ButtonForegroundPressed", Color.FromArgb(255, 30, 41, 59));
        SetBrush(button, "ButtonForegroundDisabled", Color.FromArgb(255, 148, 163, 184));
        SetBrush(button, "ButtonBorderBrush", border);
        SetBrush(button, "ButtonBorderBrushPointerOver", borderHot);
        SetBrush(button, "ButtonBorderBrushPressed", Color.FromArgb(255, 100, 116, 139));
        SetBrush(button, "ButtonBorderBrushDisabled", Color.FromArgb(255, 51, 65, 85));

        if (button is Control control)
        {
            control.Background = new SolidColorBrush(normal);
            control.Foreground = new SolidColorBrush(fg);
            control.BorderBrush = new SolidColorBrush(border);
        }
    }

    private static void ApplyPlayToggleBrushes(FrameworkElement button)
    {
        // 再生/停止は状態色を維持（文字は白）
        var stopped = Color.FromArgb(255, 220, 38, 38);
        var stoppedHover = Color.FromArgb(255, 239, 68, 68);
        var stoppedPressed = Color.FromArgb(255, 185, 28, 28);
        var playing = Color.FromArgb(255, 37, 99, 235);
        var playingHover = Color.FromArgb(255, 59, 130, 246);
        var playingPressed = Color.FromArgb(255, 29, 78, 216);
        var fg = Color.FromArgb(255, 255, 255, 255);
        var border = Color.FromArgb(255, 203, 213, 225);
        var borderHot = Color.FromArgb(255, 248, 250, 252);

        SetBrush(button, "ToggleButtonBackground", stopped);
        SetBrush(button, "ToggleButtonBackgroundPointerOver", stoppedHover);
        SetBrush(button, "ToggleButtonBackgroundPressed", stoppedPressed);
        SetBrush(button, "ToggleButtonBackgroundChecked", playing);
        SetBrush(button, "ToggleButtonBackgroundCheckedPointerOver", playingHover);
        SetBrush(button, "ToggleButtonBackgroundCheckedPressed", playingPressed);
        SetBrush(button, "ToggleButtonForeground", fg);
        SetBrush(button, "ToggleButtonForegroundPointerOver", fg);
        SetBrush(button, "ToggleButtonForegroundPressed", fg);
        SetBrush(button, "ToggleButtonForegroundChecked", fg);
        SetBrush(button, "ToggleButtonForegroundCheckedPointerOver", fg);
        SetBrush(button, "ToggleButtonForegroundCheckedPressed", fg);
        SetBrush(button, "ToggleButtonBorderBrush", border);
        SetBrush(button, "ToggleButtonBorderBrushPointerOver", borderHot);
        SetBrush(button, "ToggleButtonBorderBrushPressed", Color.FromArgb(255, 148, 163, 184));
        SetBrush(button, "ToggleButtonBorderBrushChecked", Color.FromArgb(255, 96, 165, 250));
        SetBrush(button, "ToggleButtonBorderBrushCheckedPointerOver", Color.FromArgb(255, 147, 197, 253));
        SetBrush(button, "ToggleButtonBorderBrushCheckedPressed", Color.FromArgb(255, 59, 130, 246));

        if (button is Control control)
        {
            control.Background = new SolidColorBrush(stopped);
            control.Foreground = new SolidColorBrush(fg);
            control.BorderBrush = new SolidColorBrush(border);
        }
    }

    private static void ApplyToggleBrushes(FrameworkElement button, Color checkedBg, Color checkedHover, Color checkedPressed)
    {
        var normal = Color.FromArgb(255, 248, 250, 252);
        var hover = Color.FromArgb(255, 226, 232, 240);
        var pressed = Color.FromArgb(255, 203, 213, 225);
        var fg = Color.FromArgb(255, 15, 23, 42);
        var white = Color.FromArgb(255, 255, 255, 255);
        var border = Color.FromArgb(255, 203, 213, 225);
        var borderHot = Color.FromArgb(255, 148, 163, 184);
        var borderChecked = Color.FromArgb(
            255,
            (byte)Math.Min(255, checkedBg.R + 40),
            (byte)Math.Min(255, checkedBg.G + 40),
            (byte)Math.Min(255, checkedBg.B + 40));

        SetBrush(button, "ToggleButtonBackground", normal);
        SetBrush(button, "ToggleButtonBackgroundPointerOver", hover);
        SetBrush(button, "ToggleButtonBackgroundPressed", pressed);
        SetBrush(button, "ToggleButtonBackgroundChecked", checkedBg);
        SetBrush(button, "ToggleButtonBackgroundCheckedPointerOver", checkedHover);
        SetBrush(button, "ToggleButtonBackgroundCheckedPressed", checkedPressed);
        SetBrush(button, "ToggleButtonForeground", fg);
        SetBrush(button, "ToggleButtonForegroundPointerOver", fg);
        SetBrush(button, "ToggleButtonForegroundPressed", Color.FromArgb(255, 30, 41, 59));
        SetBrush(button, "ToggleButtonForegroundChecked", white);
        SetBrush(button, "ToggleButtonForegroundCheckedPointerOver", white);
        SetBrush(button, "ToggleButtonForegroundCheckedPressed", white);
        SetBrush(button, "ToggleButtonBorderBrush", border);
        SetBrush(button, "ToggleButtonBorderBrushPointerOver", borderHot);
        SetBrush(button, "ToggleButtonBorderBrushPressed", Color.FromArgb(255, 100, 116, 139));
        SetBrush(button, "ToggleButtonBorderBrushChecked", borderChecked);
        SetBrush(button, "ToggleButtonBorderBrushCheckedPointerOver", borderChecked);
        SetBrush(button, "ToggleButtonBorderBrushCheckedPressed", borderChecked);

        if (button is Control control)
        {
            control.Background = new SolidColorBrush(normal);
            control.Foreground = new SolidColorBrush(fg);
            control.BorderBrush = new SolidColorBrush(border);
        }
    }

    private static void SetBrush(FrameworkElement element, string key, Color color)
    {
        element.Resources[key] = new SolidColorBrush(color);
    }

    private void UpdateEditModeButtonLook()
    {
        if (ViewModel.IsEditMode)
        {
            EditModeBtn.Background = new SolidColorBrush(Color.FromArgb(255, 13, 148, 136));
            EditModeBtn.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            EditModeBtn.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 45, 212, 191));
            SettingsBtn.Background = new SolidColorBrush(Color.FromArgb(255, 248, 250, 252));
            SettingsBtn.Foreground = new SolidColorBrush(Color.FromArgb(255, 15, 23, 42));
            SettingsBtn.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 203, 213, 225));
            SettingsBtn.Opacity = 1;
        }
        else
        {
            EditModeBtn.Background = new SolidColorBrush(Color.FromArgb(255, 248, 250, 252));
            EditModeBtn.Foreground = new SolidColorBrush(Color.FromArgb(255, 100, 116, 139));
            EditModeBtn.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 203, 213, 225));
            SettingsBtn.Background = new SolidColorBrush(Color.FromArgb(255, 226, 232, 240));
            SettingsBtn.Foreground = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184));
            SettingsBtn.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 203, 213, 225));
            SettingsBtn.Opacity = 0.55;
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
