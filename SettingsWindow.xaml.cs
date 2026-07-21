using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Kakikomi.Services;
using Kakikomi.Updates;

namespace Kakikomi;

public sealed partial class SettingsWindow : Window
{
    private bool _loadingUi;

    public SettingsWindow()
    {
        InitializeComponent();
        Title = "Kakikomi 設定";
        TrySetIcon();
        WireEvents();
        LoadFromSettings();
        VersionInfoText.Text = $"Kakikomi v{AppVersionReader.GetCurrentVersion()}";

        if (NavList.Items.Count > 0)
        {
            foreach (var item in NavList.Items)
            {
                if (item is ListViewItem lvi && lvi.Tag as string == "Clean")
                {
                    NavList.SelectedItem = lvi;
                    break;
                }
            }
        }

        ShowPanel("Clean");
    }

    private void TrySetIcon()
    {
        try
        {
            var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (System.IO.File.Exists(icon))
                AppWindow.SetIcon(icon);
        }
        catch
        {
            // ignore
        }
    }

    private void WireEvents()
    {
        NavList.SelectionChanged += (_, _) =>
        {
            var tag = (NavList.SelectedItem as ListViewItem)?.Tag as string;
            if (!string.IsNullOrEmpty(tag))
                ShowPanel(tag);
        };

        OpenCleanBtn.Click += (_, _) => App.OpenCleanWindow();
        ExitAppBtn.Click += (_, _) => App.RequestExit();
        OpenSaveFolderBtn.Click += (_, _) =>
        {
            try
            {
                SaveFolderService.OpenInExplorer();
            }
            catch (Exception ex)
            {
                SaveFolderPathText.Text = $"フォルダを開けません: {ex.Message}";
            }
        };

        ApplyRedBtn.Click += (_, _) => ApplyHex(HexRed, AppSettings.SetPenRed, SwatchRed);
        ApplyGreenBtn.Click += (_, _) => ApplyHex(HexGreen, AppSettings.SetPenGreen, SwatchGreen);
        ApplyBlueBtn.Click += (_, _) => ApplyHex(HexBlue, AppSettings.SetPenBlue, SwatchBlue);

        PenSizeBox.ValueChanged += (_, args) =>
        {
            if (_loadingUi || double.IsNaN(args.NewValue))
                return;
            AppSettings.SetPenThickness(args.NewValue);
        };

        EraserSizeBox.ValueChanged += (_, args) =>
        {
            if (_loadingUi || double.IsNaN(args.NewValue))
                return;
            AppSettings.SetEraserThickness(args.NewValue);
        };

        FullSizeNextLaunchCheck.Checked += (_, _) =>
        {
            if (_loadingUi)
                return;
            AppSettings.SetLaunchControlPanelFullSize(true);
        };
        FullSizeNextLaunchCheck.Unchecked += (_, _) =>
        {
            if (_loadingUi)
                return;
            AppSettings.SetLaunchControlPanelFullSize(false);
        };

        FullScreenNowBtn.Click += (_, _) => App.EnterControlPanelFullScreen();

        DemoUnlockBtn.Click += (_, _) =>
        {
            if (AppSettings.TryUnlockDemoMode(DemoPasswordBox.Password))
            {
                DemoPasswordBox.Password = string.Empty;
                DemoUnlockErrorText.Visibility = Visibility.Collapsed;
                RefreshDemoPanel();
            }
            else
            {
                DemoUnlockErrorText.Text = "パスワードが違います。";
                DemoUnlockErrorText.Visibility = Visibility.Visible;
            }
        };

        DemoEnableBtn.Click += (_, _) =>
        {
            AppSettings.SetDemoMode(true);
            DemoPasswordBox.Password = string.Empty;
            DemoUnlockErrorText.Visibility = Visibility.Collapsed;
            RefreshDemoPanel();
        };

        OnlineUpdateBtn.Click += async (_, _) =>
        {
            OnlineUpdateBtn.IsEnabled = false;
            try
            {
                var root = Content?.XamlRoot;
                await OnlineUpdateUiHelper.RunAsync(
                    root,
                    status => UpdateStatusText.Text = status,
                    beforeExitAsync: null);
            }
            finally
            {
                var dq = App.DispatcherQueue;
                if (dq.HasThreadAccess)
                    OnlineUpdateBtn.IsEnabled = true;
                else
                    dq.TryEnqueue(() => OnlineUpdateBtn.IsEnabled = true);
            }
        };

        StyleActionButton(OpenCleanBtn);
        StyleActionButton(ExitAppBtn);
        StyleActionButton(OpenSaveFolderBtn);
        StyleActionButton(DemoUnlockBtn);
        StyleActionButton(DemoEnableBtn);
        StyleActionButton(OnlineUpdateBtn);
        StyleActionButton(ApplyRedBtn);
        StyleActionButton(ApplyGreenBtn);
        StyleActionButton(ApplyBlueBtn);
        StyleActionButton(FullScreenNowBtn);
    }

    private void LoadFromSettings()
    {
        _loadingUi = true;
        try
        {
            HexRed.Text = ToHex(AppSettings.PenRed);
            HexGreen.Text = ToHex(AppSettings.PenGreen);
            HexBlue.Text = ToHex(AppSettings.PenBlue);
            SetSwatch(SwatchRed, AppSettings.PenRed);
            SetSwatch(SwatchGreen, AppSettings.PenGreen);
            SetSwatch(SwatchBlue, AppSettings.PenBlue);
            PenSizeBox.Value = AppSettings.PenThickness;
            EraserSizeBox.Value = AppSettings.EraserThickness;
            FullSizeNextLaunchCheck.IsChecked = AppSettings.LaunchControlPanelFullSize;
            SaveFolderPathText.Text = SaveFolderService.EnsureExists();
            RefreshDemoPanel();
        }
        finally
        {
            _loadingUi = false;
        }
    }

    private void RefreshDemoPanel()
    {
        if (AppSettings.DemoMode)
        {
            DemoStatusText.Text = "状態: ON";
            DemoStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 251, 191, 36));
            DemoUnlockPanel.Visibility = Visibility.Visible;
            DemoEnableBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            DemoStatusText.Text = "状態: OFF";
            DemoStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184));
            DemoUnlockPanel.Visibility = Visibility.Collapsed;
            DemoEnableBtn.Visibility = Visibility.Visible;
        }
    }

    private void ShowPanel(string tag)
    {
        PanelClean.Visibility = tag == "Clean" ? Visibility.Visible : Visibility.Collapsed;
        PanelExit.Visibility = tag == "Exit" ? Visibility.Visible : Visibility.Collapsed;
        PanelSave.Visibility = tag == "Save" ? Visibility.Visible : Visibility.Collapsed;
        PanelVersion.Visibility = tag == "Version" ? Visibility.Visible : Visibility.Collapsed;
        PanelDemo.Visibility = tag == "Demo" ? Visibility.Visible : Visibility.Collapsed;
        PanelPalette.Visibility = tag == "Palette" ? Visibility.Visible : Visibility.Collapsed;
        PanelPenSize.Visibility = tag == "PenSize" ? Visibility.Visible : Visibility.Collapsed;
        PanelFullSize.Visibility = tag == "FullSize" ? Visibility.Visible : Visibility.Collapsed;

        if (tag == "Save")
            SaveFolderPathText.Text = SaveFolderService.EnsureExists();
        if (tag == "Demo")
            RefreshDemoPanel();
        if (tag == "Version")
            VersionInfoText.Text = $"Kakikomi v{AppVersionReader.GetCurrentVersion()}";
    }

    private void ApplyHex(TextBox box, Action<Color> setter, Ellipse swatch)
    {
        if (!TryParseHex(box.Text, out var color))
        {
            box.Header = "形式が不正です（例: #EF4444）";
            return;
        }

        setter(color);
        SetSwatch(swatch, color);
        box.Text = ToHex(color);
    }

    private static void SetSwatch(Ellipse swatch, Color color) =>
        swatch.Fill = new SolidColorBrush(color);

    private static string ToHex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hex = text.Trim();
        if (hex.StartsWith('#'))
            hex = hex[1..];
        if (hex.Length == 3)
            hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
        if (hex.Length != 6)
            return false;
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return false;

        color = Color.FromArgb(
            255,
            (byte)((rgb >> 16) & 0xFF),
            (byte)((rgb >> 8) & 0xFF),
            (byte)(rgb & 0xFF));
        return true;
    }

    private static void StyleActionButton(Button button)
    {
        button.Background = new SolidColorBrush(Color.FromArgb(255, 248, 250, 252));
        button.Foreground = new SolidColorBrush(Color.FromArgb(255, 15, 23, 42));
        button.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 203, 213, 225));
        button.BorderThickness = new Thickness(1);
    }
}
