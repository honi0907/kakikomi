using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Kakikomi.Helpers;
using Kakikomi.Services;

namespace Kakikomi;

public partial class App : Application
{
    public static Window Window { get; private set; } = null!;
    public static CleanOutputWindow? CleanWindow { get; private set; }
    public static SettingsWindow? SettingsWindowInstance { get; private set; }
    public static EngineSession? Engine { get; private set; }

    public static DispatcherQueue DispatcherQueue { get; private set; } = null!;

    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    private static DispatcherQueueTimer? _monitorWatchTimer;
    private static bool _hadSecondaryMonitor;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[Unhandled] {e.Exception}");
            e.Handled = true;
        };
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        AppSettings.Load();
        SaveFolderService.EnsureExists();
        Engine = new EngineSession();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        Window = new MainWindow();
        Window.Closed += OnMainClosed;
        Window.Activate();
        ApplyControlPanelLaunchSize();

        StartMonitorWatch();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, TryAutoOpenCleanForSecondary);
    }

    public static void OpenCleanWindow()
    {
        if (Engine is null)
            return;

        if (CleanWindow is not null)
        {
            CleanWindow.Activate();
            CleanWindow.PlaceOnOutputMonitor();
            return;
        }

        try
        {
            CleanWindow = new CleanOutputWindow();
            CleanWindow.Closed += (_, _) => CleanWindow = null;
            CleanWindow.Attach(Engine);
            CleanWindow.PlaceOnOutputMonitor();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CleanWindow] {ex}");
            CleanWindow = null;
        }
    }

    public static void OpenSettingsWindow()
    {
        const int width = 920;
        const int height = 640;

        try
        {
            if (SettingsWindowInstance is not null)
            {
                BringSettingsToFront(SettingsWindowInstance, width, height);
                return;
            }

            var window = new SettingsWindow();
            SettingsWindowInstance = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(SettingsWindowInstance, window))
                    SettingsWindowInstance = null;
            };

            BringSettingsToFront(window, width, height);
        }
        catch (Exception ex)
        {
            SettingsWindowInstance = null;
            System.Diagnostics.Debug.WriteLine($"[SettingsWindow] {ex}");
            SettingsOpenFailed?.Invoke(ex.Message);
        }
    }

    public static event Action<string>? SettingsOpenFailed;

    private static void BringSettingsToFront(SettingsWindow window, int width, int height)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        window.AppWindow.Show();
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMinimizable = true;
            presenter.Restore();
        }

        MonitorHelper.ShowCenteredOnPrimary(hwnd, width, height);
        window.Activate();
    }

    public static void RequestExit()
    {
        try { SettingsWindowInstance?.Close(); } catch { /* ignore */ }
        try { CleanWindow?.Close(); } catch { /* ignore */ }
        try { Window.Close(); } catch { /* ignore */ }
    }

    public static void EnterControlPanelFullScreen()
    {
        try
        {
            Window.Activate();
            Window.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ControlPanelFullScreen] {ex}");
        }
    }

    private static void ApplyControlPanelLaunchSize()
    {
        if (!AppSettings.LaunchControlPanelFullSize)
            return;

        try
        {
            if (Window.AppWindow.Presenter is OverlappedPresenter presenter)
                presenter.Maximize();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LaunchFullSize] {ex}");
        }
    }

    private static void StartMonitorWatch()
    {
        _hadSecondaryMonitor = false;
        _monitorWatchTimer = DispatcherQueue.CreateTimer();
        _monitorWatchTimer.Interval = TimeSpan.FromSeconds(2);
        _monitorWatchTimer.IsRepeating = true;
        _monitorWatchTimer.Tick += (_, _) => TryAutoOpenCleanForSecondary();
        _monitorWatchTimer.Start();
    }

    private static void TryAutoOpenCleanForSecondary()
    {
        var hasSecondary = MonitorHelper.HasSecondaryMonitor();
        if (hasSecondary && !_hadSecondaryMonitor)
            OpenCleanWindow();

        _hadSecondaryMonitor = hasSecondary;
    }

    private void OnMainClosed(object sender, WindowEventArgs args)
    {
        try { _monitorWatchTimer?.Stop(); } catch { /* ignore */ }
        _monitorWatchTimer = null;

        if (SettingsWindowInstance is not null)
        {
            try { SettingsWindowInstance.Close(); } catch { /* ignore */ }
            SettingsWindowInstance = null;
        }

        if (CleanWindow is not null)
        {
            try { CleanWindow.Close(); } catch { /* ignore */ }
            CleanWindow = null;
        }

        Engine?.Dispose();
        Engine = null;
    }
}
