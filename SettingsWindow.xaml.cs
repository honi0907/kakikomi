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
    private sealed class SettingsNavEntry
    {
        public required string Label { get; init; }
        public string? PanelTag { get; init; }

        public override string ToString() => Label;
    }

    private bool _loadingUi;
    private int _editingPenSlot; // 1/2/3, 0=閉じ
    private CancellationTokenSource? _releaseNotesCts;
    private bool _releaseNotesLoaded;

    public SettingsWindow()
    {
        InitializeComponent();
        Title = "Kakikomi 設定";
        TrySetIcon();
        BuildNavTree();
        WireEvents();
        LoadFromSettings();
        VersionInfoText.Text = $"Kakikomi v{AppVersionReader.GetCurrentVersion()}";
        SelectNavPanel("Clean");
    }

    private void BuildNavTree()
    {
        NavTree.RootNodes.Clear();
        NavTree.RootNodes.Add(MakeLeaf("ソフト終了", "Exit"));
        NavTree.RootNodes.Add(MakeGroup(
            "アプリ",
            expanded: true,
            MakeLeaf("バージョン / 更新", "Version"),
            MakeLeaf("DEMOモード", "Demo"),
            MakeLeaf("LAN 遠隔操作", "Remote"),
            MakeLeaf("コンパネ起動サイズ", "FullSize"),
            MakeLeaf("負荷・ログ", "LoadLog")));
        NavTree.RootNodes.Add(MakeGroup(
            "映像・出力",
            expanded: true,
            MakeLeaf("クリーン出力", "Clean")));
        NavTree.RootNodes.Add(MakeGroup(
            "ネタ・ファイル",
            expanded: false,
            MakeLeaf("保存", "Save"),
            MakeLeaf(".mov 変換", "Convert"),
            MakeLeaf("ネタ一覧", "NetaList")));
        NavTree.RootNodes.Add(MakeGroup(
            "操作・描画",
            expanded: false,
            MakeLeaf("再生", "Playback"),
            MakeLeaf("パレットの色編集", "Palette"),
            MakeLeaf("ペンサイズ", "PenSize")));
    }

    private static TreeViewNode MakeLeaf(string label, string panelTag) =>
        new()
        {
            Content = new SettingsNavEntry { Label = label, PanelTag = panelTag }
        };

    private static TreeViewNode MakeGroup(string label, bool expanded, params TreeViewNode[] children)
    {
        var node = new TreeViewNode
        {
            Content = new SettingsNavEntry { Label = label },
            IsExpanded = expanded
        };
        foreach (var child in children)
            node.Children.Add(child);
        return node;
    }

    private void SelectNavPanel(string panelTag)
    {
        ShowPanel(panelTag);
        foreach (var root in NavTree.RootNodes)
        {
            if (TrySelectNavNode(root, panelTag))
                break;
        }
    }

    private bool TrySelectNavNode(TreeViewNode node, string panelTag)
    {
        if (node.Content is SettingsNavEntry { PanelTag: var tag } && tag == panelTag)
        {
            NavTree.SelectedItem = node;
            return true;
        }

        foreach (var child in node.Children)
        {
            if (!TrySelectNavNode(child, panelTag))
                continue;

            node.IsExpanded = true;
            return true;
        }

        return false;
    }

    private void OnNavTreeItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not TreeViewNode node)
            return;

        if (node.Content is not SettingsNavEntry { PanelTag: { } panelTag })
            return;

        ShowPanel(panelTag);
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

        ActiveColorPalette.ColorChanged += OnMixPaletteColorChanged;
        ActiveColorPicker.ColorChanged += OnSpectrumColorChanged;
        RgbRBox.ValueChanged += OnRgbBoxChanged;
        RgbGBox.ValueChanged += OnRgbBoxChanged;
        RgbBBox.ValueChanged += OnRgbBoxChanged;

        PenPreset1Box.ValueChanged += (_, args) =>
        {
            if (_loadingUi || double.IsNaN(args.NewValue))
                return;
            AppSettings.SetPenThicknessPreset(0, args.NewValue);
        };

        PenPreset2Box.ValueChanged += (_, args) =>
        {
            if (_loadingUi || double.IsNaN(args.NewValue))
                return;
            AppSettings.SetPenThicknessPreset(1, args.NewValue);
        };

        PenPreset3Box.ValueChanged += (_, args) =>
        {
            if (_loadingUi || double.IsNaN(args.NewValue))
                return;
            AppSettings.SetPenThicknessPreset(2, args.NewValue);
        };

        ResetPenPresetsBtn.Click += (_, _) =>
        {
            if (_loadingUi)
                return;

            AppSettings.ResetPenThicknessPresetsToDefault();
            LoadPenPresetBoxes();
        };

        EraserSizeBox.ValueChanged += (_, args) =>
        {
            if (_loadingUi || double.IsNaN(args.NewValue))
                return;
            AppSettings.SetEraserThickness(args.NewValue);
        };

        ControlPanelChromeAtTopCheck.Click += (_, _) =>
        {
            if (_loadingUi)
                return;
            AppSettings.SetControlPanelChromeAtTop(ControlPanelChromeAtTopCheck.IsChecked == true);
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

        VariableSpeedAudioCheck.Click += (_, _) =>
        {
            if (_loadingUi)
                return;
            AppSettings.SetVariableSpeedAudioEnabled(VariableSpeedAudioCheck.IsChecked == true);
        };

        FastPlaybackFpsUnlimitedRadio.Checked += (_, _) => OnFastPlaybackMaxFpsChecked(0);
        FastPlaybackFps60Radio.Checked += (_, _) => OnFastPlaybackMaxFpsChecked(60);
        FastPlaybackFps50Radio.Checked += (_, _) => OnFastPlaybackMaxFpsChecked(50);
        FastPlaybackFps45Radio.Checked += (_, _) => OnFastPlaybackMaxFpsChecked(45);
        FastPlaybackFps30Radio.Checked += (_, _) => OnFastPlaybackMaxFpsChecked(30);
        FastPlaybackFps24Radio.Checked += (_, _) => OnFastPlaybackMaxFpsChecked(24);

        NetaSwitchCrossfadeCheck.Click += (_, _) =>
        {
            if (_loadingUi)
                return;
            AppSettings.SetNetaSwitchCrossfadeEnabled(NetaSwitchCrossfadeCheck.IsChecked == true);
        };

        OverlayPlayButtonCheck.Click += (_, _) =>
        {
            if (_loadingUi)
                return;
            AppSettings.SetOverlayPlayButton(OverlayPlayButtonCheck.IsChecked == true);
        };

        TouchVideoPauseAndDrawCheck.Click += (_, _) =>
        {
            if (_loadingUi)
                return;
            AppSettings.SetTouchVideoPauseAndDraw(TouchVideoPauseAndDrawCheck.IsChecked == true);
        };

        NetaLoopCheck.Click += (_, _) =>
        {
            if (_loadingUi)
                return;
            if (NetaLoopCheck.IsChecked == true)
                RemoteNetaLoopService.Instance.Start();
            else
                RemoteNetaLoopService.Instance.Stop();
        };

        PerfMonitorCheck.Click += (_, _) =>
        {
            if (_loadingUi)
                return;
            AppSettings.SetPerfMonitorEnabled(PerfMonitorCheck.IsChecked == true);
            PerfMonitorService.Instance.ApplyFromSettings();
        };

        PerfLogCheck.Click += (_, _) =>
        {
            if (_loadingUi)
                return;
            AppSettings.SetPerfLogEnabled(PerfLogCheck.IsChecked == true);
            PerfMonitorService.Instance.ApplyFromSettings();
        };

        OpenPerfLogFolderBtn.Click += (_, _) => PerfMonitorService.OpenLogFolder();

        DiagnosticCaptureCheck.Click += (_, _) =>
        {
            if (_loadingUi)
                return;
            AppSettings.SetDiagnosticCaptureEnabled(DiagnosticCaptureCheck.IsChecked == true);
            DiagnosticCaptureService.Instance.ApplyFromSettings();
        };

        DiagnosticCaptureIntervalBox.ValueChanged += (_, _) =>
        {
            if (_loadingUi)
                return;
            AppSettings.SetDiagnosticCaptureIntervalMinutes((int)Math.Round(DiagnosticCaptureIntervalBox.Value));
            DiagnosticCaptureService.Instance.ApplyFromSettings();
        };

        OpenDiagnosticCaptureFolderBtn.Click += (_, _) => DiagnosticCaptureService.OpenFolder();

        RemoteControlEnabledCheck.Click += (_, _) =>
        {
            if (_loadingUi)
                return;
            // 適用ボタンで一括反映（ポート/PIN も同時）
        };

        RemoteApplyBtn.Click += (_, _) => ApplyRemoteSettingsFromUi();

        Closed += (_, _) =>
        {
            _loadingUi = true;
            DetachNetaListEvents();
        };

        FullScreenNowBtn.Click += (_, _) => App.EnterControlPanelFullScreen();

        NetaThumbScale1Radio.Checked += (_, _) => OnNetaThumbnailScaleChecked(1.0);
        NetaThumbScale12Radio.Checked += (_, _) => OnNetaThumbnailScaleChecked(1.2);
        NetaThumbScale15Radio.Checked += (_, _) => OnNetaThumbnailScaleChecked(1.5);

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
        StyleActionButton(ResetPenColorsBtn);
        StyleActionButton(CloseColorEditorBtn);
        StyleActionButton(RemoteApplyBtn);
        StyleActionButton(OpenPerfLogFolderBtn);
        StyleActionButton(OpenDiagnosticCaptureFolderBtn);
    }

    private void ApplyRemoteSettingsFromUi()
    {
        if (_loadingUi)
            return;

        var port = (int)Math.Round(RemotePortBox.Value);
        if (double.IsNaN(RemotePortBox.Value))
            port = AppSettings.RemoteControlPort;

        AppSettings.SetRemoteControlPort(port);
        AppSettings.SetRemoteControlPin(RemotePinBox.Password);
        AppSettings.SetRemoteControlEnabled(RemoteControlEnabledCheck.IsChecked == true);
        RemoteControlHost.Instance.ApplyFromSettings();
        RefreshRemotePanel();
    }

    private void RefreshRemotePanel()
    {
        RemoteControlEnabledCheck.IsChecked = AppSettings.RemoteControlEnabled;
        RemotePortBox.Value = AppSettings.RemoteControlPort;
        RemotePinBox.Password = AppSettings.RemoteControlPin ?? "";

        var host = RemoteControlHost.Instance;
        if (host.IsRunning)
        {
            RemoteStatusText.Text = "稼働中";
            RemoteStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 74, 222, 128));
            var urls = string.Join("\n", host.GetListenUrls());
            RemoteUrlsText.Text =
                "ブラウザで次を開いてください（同じ LAN）:\n" + urls +
                "\n\nWindows ファイアウォールで受信を許可してください。";
        }
        else if (!string.IsNullOrWhiteSpace(host.LastError))
        {
            RemoteStatusText.Text = $"停止: {host.LastError}";
            RemoteStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 248, 113, 113));
            RemoteUrlsText.Text = "";
        }
        else
        {
            RemoteStatusText.Text = AppSettings.RemoteControlEnabled
                ? "有効だが待受できていません。適用を押すかポートを確認してください。"
                : "停止中";
            RemoteStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184));
            RemoteUrlsText.Text = "";
        }
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
            LoadPenPresetBoxes();
            EraserSizeBox.Value = AppSettings.EraserThickness;
            FullSizeNextLaunchCheck.IsChecked = AppSettings.LaunchControlPanelFullSize;
            ControlPanelChromeAtTopCheck.IsChecked = AppSettings.ControlPanelChromeAtTop;
            ResumePlaybackCheck.IsChecked = AppSettings.ResumePlayback;
            VariableSpeedAudioCheck.IsChecked = AppSettings.VariableSpeedAudioEnabled;
            ApplyFastPlaybackMaxFpsUi();
            NetaSwitchCrossfadeCheck.IsChecked = AppSettings.NetaSwitchCrossfadeEnabled;
            OverlayPlayButtonCheck.IsChecked = AppSettings.OverlayPlayButton;
            TouchVideoPauseAndDrawCheck.IsChecked = AppSettings.TouchVideoPauseAndDraw;
            NetaLoopCheck.IsChecked = RemoteNetaLoopService.Instance.IsRunning;
            PerfMonitorCheck.IsChecked = AppSettings.PerfMonitorEnabled;
            PerfLogCheck.IsChecked = AppSettings.PerfLogEnabled;
            DiagnosticCaptureCheck.IsChecked = AppSettings.DiagnosticCaptureEnabled;
            DiagnosticCaptureIntervalBox.Value = AppSettings.DiagnosticCaptureIntervalMinutes;
            RefreshRemotePanel();
            ApplyNetaThumbnailScaleUi();
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

    private void LoadPenPresetBoxes()
    {
        PenPreset1Box.Value = AppSettings.GetPenThicknessPreset(0);
        PenPreset2Box.Value = AppSettings.GetPenThicknessPreset(1);
        PenPreset3Box.Value = AppSettings.GetPenThicknessPreset(2);
    }

    private void OnFastPlaybackMaxFpsChecked(int fps)
    {
        if (_loadingUi)
            return;

        AppSettings.SetFastPlaybackMaxFps(fps);
    }

    private void ApplyFastPlaybackMaxFpsUi()
    {
        var fps = AppSettings.FastPlaybackMaxFps;
        FastPlaybackFpsUnlimitedRadio.IsChecked = fps == 0;
        FastPlaybackFps60Radio.IsChecked = fps == 60;
        FastPlaybackFps50Radio.IsChecked = fps == 50;
        FastPlaybackFps45Radio.IsChecked = fps == 45;
        FastPlaybackFps30Radio.IsChecked = fps == 30;
        FastPlaybackFps24Radio.IsChecked = fps == 24;
    }

    private void OnNetaThumbnailScaleChecked(double scale)
    {
        if (_loadingUi)
            return;

        AppSettings.SetNetaThumbnailScale(scale);
    }

    private void ApplyNetaThumbnailScaleUi()
    {
        var scale = AppSettings.NetaThumbnailScale;
        NetaThumbScale1Radio.IsChecked = Math.Abs(scale - 1.0) < 0.01;
        NetaThumbScale12Radio.IsChecked = Math.Abs(scale - 1.2) < 0.01;
        NetaThumbScale15Radio.IsChecked = Math.Abs(scale - 1.5) < 0.01;
    }

    private void OnPen1SwatchClick(object sender, RoutedEventArgs e) => OpenColorEditor(1);
    private void OnPen2SwatchClick(object sender, RoutedEventArgs e) => OpenColorEditor(2);
    private void OnPen3SwatchClick(object sender, RoutedEventArgs e) => OpenColorEditor(3);
    private void OnCloseColorEditorClick(object sender, RoutedEventArgs e) => CloseColorEditor();

    private void OnResetPenColorsClick(object sender, RoutedEventArgs e)
    {
        AppSettings.ResetPenColorsToDefault();
        SetSwatch(SwatchRed, AppSettings.PenRed);
        SetSwatch(SwatchGreen, AppSettings.PenGreen);
        SetSwatch(SwatchBlue, AppSettings.PenBlue);

        if (_editingPenSlot is 1 or 2 or 3)
            OpenColorEditor(_editingPenSlot);
    }

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

        SetActiveColor(color, updateMixPalette: true, updateSpectrum: true, updateRgb: true);
        ColorEditorPanel.Visibility = Visibility.Visible;
    }

    private void CloseColorEditor()
    {
        _editingPenSlot = 0;
        ColorEditorPanel.Visibility = Visibility.Collapsed;
    }

    private void OnMixPaletteColorChanged(object? sender, Color color)
    {
        if (_loadingUi || _editingPenSlot == 0)
            return;

        ApplyEditedColor(color, updateMixPalette: false, updateSpectrum: true, updateRgb: true);
    }

    private void OnSpectrumColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_loadingUi || _editingPenSlot == 0)
            return;

        ApplyEditedColor(args.NewColor, updateMixPalette: true, updateSpectrum: false, updateRgb: true);
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

        ApplyEditedColor(color, updateMixPalette: true, updateSpectrum: true, updateRgb: false);
    }

    private void ApplyEditedColor(Color color, bool updateMixPalette, bool updateSpectrum, bool updateRgb)
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

        if (updateMixPalette || updateSpectrum || updateRgb)
            SetActiveColor(color, updateMixPalette, updateSpectrum, updateRgb);
    }

    private void SetActiveColor(Color color, bool updateMixPalette, bool updateSpectrum, bool updateRgb)
    {
        _loadingUi = true;
        try
        {
            if (updateMixPalette)
                ActiveColorPalette.SetColor(color);
            if (updateSpectrum)
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
        PanelRemote.Visibility = tag == "Remote" ? Visibility.Visible : Visibility.Collapsed;
        PanelVersion.Visibility = tag == "Version" ? Visibility.Visible : Visibility.Collapsed;
        PanelDemo.Visibility = tag == "Demo" ? Visibility.Visible : Visibility.Collapsed;
        PanelPalette.Visibility = tag == "Palette" ? Visibility.Visible : Visibility.Collapsed;
        PanelPenSize.Visibility = tag == "PenSize" ? Visibility.Visible : Visibility.Collapsed;
        PanelFullSize.Visibility = tag == "FullSize" ? Visibility.Visible : Visibility.Collapsed;
        PanelLoadLog.Visibility = tag == "LoadLog" ? Visibility.Visible : Visibility.Collapsed;

        if (tag != "Palette")
            CloseColorEditor();

        if (tag == "Save")
            SaveFolderPathText.Text = SaveFolderService.EnsureExists();
        if (tag == "Convert")
            ConvertFolderPathText.Text = MovTranscodeService.EnsureCacheDirectory();
        if (tag == "Playback")
            NetaLoopCheck.IsChecked = RemoteNetaLoopService.Instance.IsRunning;
        if (tag == "NetaList")
            BindNetaManageList();
        if (tag == "Demo")
            RefreshDemoPanel();
        if (tag == "Remote")
            RefreshRemotePanel();
        if (tag == "Version")
        {
            VersionInfoText.Text = $"Kakikomi v{AppVersionReader.GetCurrentVersion()}";
            _ = LoadReleaseNotesAsync();
        }
        if (tag == "Palette")
        {
            SetSwatch(SwatchRed, AppSettings.PenRed);
            SetSwatch(SwatchGreen, AppSettings.PenGreen);
            SetSwatch(SwatchBlue, AppSettings.PenBlue);
        }
    }

    private async Task LoadReleaseNotesAsync()
    {
        if (_releaseNotesLoaded && ReleaseNotesList.ItemsSource is not null)
            return;

        _releaseNotesCts?.Cancel();
        _releaseNotesCts = new CancellationTokenSource();
        var token = _releaseNotesCts.Token;

        ReleaseNotesStatusText.Text = "GitHub から読み込み中...";
        ReleaseNotesList.ItemsSource = null;

        try
        {
            var entries = await ReleaseNotesService.LoadAsync(cancellationToken: token);
            token.ThrowIfCancellationRequested();

            if (entries.Count == 0)
            {
                ReleaseNotesStatusText.Text = "Release が見つかりませんでした。";
                return;
            }

            ReleaseNotesList.ItemsSource = entries;
            ReleaseNotesStatusText.Text = $"GitHub Release（{entries.Count} 件）";
            _releaseNotesLoaded = true;
        }
        catch (OperationCanceledException)
        {
            // 別パネルへ切り替えた等
        }
        catch (Exception ex)
        {
            ReleaseNotesStatusText.Text = $"更新履歴を取得できませんでした: {ex.Message}";
        }
    }

    private static void SetSwatch(Ellipse swatch, Color color) =>
        swatch.Fill = new SolidColorBrush(color);

    private static void StyleActionButton(Button button)
    {
        button.Style = (Style)Application.Current.Resources["OpButtonStyle"];
    }
}
