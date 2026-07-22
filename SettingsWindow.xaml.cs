using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Kakikomi.Models;
using Kakikomi.Services;
using Kakikomi.Updates;
using Kakikomi.ViewModels;

namespace Kakikomi;

public sealed partial class SettingsWindow : Window
{
    private bool _loadingUi;
    private int _editingPenSlot; // 1/2/3, 0=閉じ

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

        OpenConvertFolderBtn.Click += (_, _) =>
        {
            try
            {
                MovTranscodeService.OpenCacheInExplorer();
            }
            catch (Exception ex)
            {
                ConvertFolderPathText.Text = $"フォルダを開けません: {ex.Message}";
            }
        };

        RemoveSelectedNetaBtn.Click += async (_, _) => await RemoveSelectedNetaAsync();
        RemoveAllNetaBtn.Click += async (_, _) => await RemoveAllNetaAsync();

        ActiveColorPicker.ColorChanged += OnActiveColorChanged;
        RgbRBox.ValueChanged += OnRgbBoxChanged;
        RgbGBox.ValueChanged += OnRgbBoxChanged;
        RgbBBox.ValueChanged += OnRgbBoxChanged;

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

        // Checked/Unchecked はウィンドウ閉鎖時にも発火し、false で上書きすることがあるため Click のみ保存する
        FullSizeNextLaunchCheck.Click += (_, _) =>
        {
            if (_loadingUi)
                return;
            AppSettings.SetLaunchControlPanelFullSize(FullSizeNextLaunchCheck.IsChecked == true);
        };
        ResumePlaybackCheck.Click += (_, _) =>
        {
            if (_loadingUi)
                return;
            AppSettings.SetResumePlayback(ResumePlaybackCheck.IsChecked == true);
        };
        Closed += (_, _) =>
        {
            _loadingUi = true;
            DetachNetaListEvents();
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
        StyleActionButton(OpenConvertFolderBtn);
        StyleActionButton(RemoveSelectedNetaBtn);
        StyleActionButton(RemoveAllNetaBtn);
        StyleActionButton(DemoUnlockBtn);
        StyleActionButton(DemoEnableBtn);
        StyleActionButton(OnlineUpdateBtn);
        StyleActionButton(FullScreenNowBtn);
        StyleActionButton(Pen1SwatchBtn);
        StyleActionButton(Pen2SwatchBtn);
        StyleActionButton(Pen3SwatchBtn);
        StyleActionButton(CloseColorEditorBtn);
    }

    private void DetachNetaListEvents()
    {
        var vm = App.MainViewModel;
        if (vm is not null)
            vm.NetaItems.CollectionChanged -= OnNetaItemsCollectionChanged;
    }

    private void BindNetaManageList()
    {
        DetachNetaListEvents();

        var vm = App.MainViewModel;
        if (vm is null)
        {
            NetaManageList.ItemsSource = null;
            NetaListCountText.Text = "操作画面の一覧がまだありません";
            RemoveSelectedNetaBtn.IsEnabled = false;
            RemoveAllNetaBtn.IsEnabled = false;
            return;
        }

        NetaManageList.ItemsSource = vm.NetaItems;
        RefreshNetaListCount(vm);
        RemoveSelectedNetaBtn.IsEnabled = true;
        RemoveAllNetaBtn.IsEnabled = vm.NetaItems.Count > 0;
        vm.NetaItems.CollectionChanged += OnNetaItemsCollectionChanged;
    }

    private void OnNetaItemsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        var vm = App.MainViewModel;
        if (vm is null)
            return;
        RefreshNetaListCount(vm);
        RemoveAllNetaBtn.IsEnabled = vm.NetaItems.Count > 0;
    }

    private void RefreshNetaListCount(MainPageViewModel vm) =>
        NetaListCountText.Text = $"{vm.NetaItems.Count} 本";

    private async Task RemoveSelectedNetaAsync()
    {
        var vm = App.MainViewModel;
        if (vm is null)
            return;

        var selected = NetaManageList.SelectedItems.OfType<NetaItem>().ToList();
        if (selected.Count == 0)
        {
            await ShowInfoAsync("選択なし", "消去するネタにチェックを入れてください。");
            return;
        }

        var ok = await ConfirmAsync(
            "選択を消去",
            $"選択中の {selected.Count} 本を一覧から外しますか？\nPC上のファイルは削除されません。");
        if (!ok)
            return;

        vm.RemoveNetaItems(selected);
        NetaManageList.SelectedItems.Clear();
    }

    private async Task RemoveAllNetaAsync()
    {
        var vm = App.MainViewModel;
        if (vm is null || vm.NetaItems.Count == 0)
            return;

        var count = vm.NetaItems.Count;
        var ok = await ConfirmAsync(
            "一斉消去",
            $"一覧の {count} 本すべてを外しますか？\nPC上のファイルは削除されません。");
        if (!ok)
            return;

        vm.ClearAllNetaItems();
        NetaManageList.SelectedItems.Clear();
    }

    private async Task<bool> ConfirmAsync(string title, string content)
    {
        var root = Content?.XamlRoot;
        if (root is null)
            return false;

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = "消去",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowInfoAsync(string title, string content)
    {
        var root = Content?.XamlRoot;
        if (root is null)
            return;

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root,
        };
        await dialog.ShowAsync();
    }

    private void LoadFromSettings()
    {
        _loadingUi = true;
        try
        {
            SetSwatch(SwatchRed, AppSettings.PenRed);
            SetSwatch(SwatchGreen, AppSettings.PenGreen);
            SetSwatch(SwatchBlue, AppSettings.PenBlue);
            PenSizeBox.Value = AppSettings.PenThickness;
            EraserSizeBox.Value = AppSettings.EraserThickness;
            FullSizeNextLaunchCheck.IsChecked = AppSettings.LaunchControlPanelFullSize;
            ResumePlaybackCheck.IsChecked = AppSettings.ResumePlayback;
            SaveFolderPathText.Text = SaveFolderService.EnsureExists();
            ConvertFolderPathText.Text = MovTranscodeService.EnsureCacheDirectory();
            RefreshDemoPanel();
            CloseColorEditor();
        }
        finally
        {
            _loadingUi = false;
        }
    }

    private void OnPen1SwatchClick(object sender, RoutedEventArgs e) => OpenColorEditor(1);
    private void OnPen2SwatchClick(object sender, RoutedEventArgs e) => OpenColorEditor(2);
    private void OnPen3SwatchClick(object sender, RoutedEventArgs e) => OpenColorEditor(3);
    private void OnCloseColorEditorClick(object sender, RoutedEventArgs e) => CloseColorEditor();

    private void OpenColorEditor(int slot)
    {
        _editingPenSlot = slot;
        ColorEditorTitle.Text = slot switch
        {
            1 => "ペン1 の色",
            2 => "ペン2 の色",
            _ => "ペン3 の色"
        };

        var color = slot switch
        {
            1 => AppSettings.PenRed,
            2 => AppSettings.PenGreen,
            _ => AppSettings.PenBlue
        };

        SetActiveColor(color, updatePicker: true, updateRgb: true);
        ColorEditorPanel.Visibility = Visibility.Visible;
    }

    private void CloseColorEditor()
    {
        _editingPenSlot = 0;
        ColorEditorPanel.Visibility = Visibility.Collapsed;
    }

    private void OnActiveColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_loadingUi || _editingPenSlot == 0)
            return;

        ApplyEditedColor(args.NewColor, updateRgb: true);
    }

    private void OnRgbBoxChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loadingUi || _editingPenSlot == 0)
            return;
        if (double.IsNaN(RgbRBox.Value) || double.IsNaN(RgbGBox.Value) || double.IsNaN(RgbBBox.Value))
            return;

        var color = Color.FromArgb(
            255,
            (byte)Math.Clamp((int)Math.Round(RgbRBox.Value), 0, 255),
            (byte)Math.Clamp((int)Math.Round(RgbGBox.Value), 0, 255),
            (byte)Math.Clamp((int)Math.Round(RgbBBox.Value), 0, 255));

        ApplyEditedColor(color, updateRgb: false);
        SetActiveColor(color, updatePicker: true, updateRgb: false);
    }

    private void ApplyEditedColor(Color color, bool updateRgb)
    {
        switch (_editingPenSlot)
        {
            case 1:
                AppSettings.SetPenRed(color);
                SetSwatch(SwatchRed, color);
                break;
            case 2:
                AppSettings.SetPenGreen(color);
                SetSwatch(SwatchGreen, color);
                break;
            case 3:
                AppSettings.SetPenBlue(color);
                SetSwatch(SwatchBlue, color);
                break;
        }

        if (updateRgb)
            SetActiveColor(color, updatePicker: false, updateRgb: true);
    }

    private void SetActiveColor(Color color, bool updatePicker, bool updateRgb)
    {
        _loadingUi = true;
        try
        {
            if (updatePicker)
                ActiveColorPicker.Color = color;
            if (updateRgb)
            {
                RgbRBox.Value = color.R;
                RgbGBox.Value = color.G;
                RgbBBox.Value = color.B;
            }
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
        PanelConvert.Visibility = tag == "Convert" ? Visibility.Visible : Visibility.Collapsed;
        PanelNetaList.Visibility = tag == "NetaList" ? Visibility.Visible : Visibility.Collapsed;
        PanelPlayback.Visibility = tag == "Playback" ? Visibility.Visible : Visibility.Collapsed;
        PanelVersion.Visibility = tag == "Version" ? Visibility.Visible : Visibility.Collapsed;
        PanelDemo.Visibility = tag == "Demo" ? Visibility.Visible : Visibility.Collapsed;
        PanelPalette.Visibility = tag == "Palette" ? Visibility.Visible : Visibility.Collapsed;
        PanelPenSize.Visibility = tag == "PenSize" ? Visibility.Visible : Visibility.Collapsed;
        PanelFullSize.Visibility = tag == "FullSize" ? Visibility.Visible : Visibility.Collapsed;

        if (tag != "Palette")
            CloseColorEditor();

        if (tag == "Save")
            SaveFolderPathText.Text = SaveFolderService.EnsureExists();
        if (tag == "Convert")
            ConvertFolderPathText.Text = MovTranscodeService.EnsureCacheDirectory();
        if (tag == "NetaList")
            BindNetaManageList();
        if (tag == "Demo")
            RefreshDemoPanel();
        if (tag == "Version")
            VersionInfoText.Text = $"Kakikomi v{AppVersionReader.GetCurrentVersion()}";
        if (tag == "Palette")
        {
            SetSwatch(SwatchRed, AppSettings.PenRed);
            SetSwatch(SwatchGreen, AppSettings.PenGreen);
            SetSwatch(SwatchBlue, AppSettings.PenBlue);
        }
    }

    private static void SetSwatch(Ellipse swatch, Color color) =>
        swatch.Fill = new SolidColorBrush(color);

    private static void StyleActionButton(Button button)
    {
        button.Style = (Style)Application.Current.Resources["OpButtonStyle"];
        button.Background = new SolidColorBrush(Color.FromArgb(255, 248, 250, 252));
        button.Foreground = new SolidColorBrush(Color.FromArgb(255, 15, 23, 42));
        button.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 71, 85, 105));
        button.FontSize = 16;
        button.MinHeight = 44;
        button.Padding = new Thickness(16, 10, 16, 10);
    }
}
