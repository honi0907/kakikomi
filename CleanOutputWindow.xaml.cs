using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;
using Kakikomi.Helpers;
using Kakikomi.Services;

namespace Kakikomi;

public sealed partial class CleanOutputWindow : Window
{
    public CleanOutputWindow()
    {
        InitializeComponent();
        Title = "Kakikomi Clean";
        AppSettings.Changed += OnAppSettingsChanged;
        Closed += (_, _) => AppSettings.Changed -= OnAppSettingsChanged;
        UpdateDemoWatermark();
    }

    public void Attach(EngineSession session)
    {
        PlayerElementA.Attach(session.GetCleanPlayerForSlot(0));
        PlayerElementB.Attach(session.GetCleanPlayerForSlot(1));
        UpdateCleanSlotVisibility(session.VisibleSlotIndex);
        InkLayer.Attach(session, inputEnabled: false);
        session.VisibleSlotChanged += OnVisibleSlotChanged;
    }

    private void OnVisibleSlotChanged(int visibleSlotIndex)
    {
        var dq = DispatcherQueue;
        if (dq.HasThreadAccess)
            ApplyVisibleSlot(visibleSlotIndex);
        else
            dq.TryEnqueue(() => ApplyVisibleSlot(visibleSlotIndex));
    }

    private void ApplyVisibleSlot(int visibleSlotIndex)
    {
        var session = App.Engine;
        if (session is null)
            return;

        PlayerElementA.Attach(session.GetCleanPlayerForSlot(0));
        PlayerElementB.Attach(session.GetCleanPlayerForSlot(1));
        UpdateCleanSlotVisibility(visibleSlotIndex);
    }

    private void UpdateCleanSlotVisibility(int visibleSlotIndex)
    {
        PlayerElementA.Visibility = visibleSlotIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlayerElementB.Visibility = visibleSlotIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAppSettingsChanged()
    {
        var dq = DispatcherQueue;
        if (dq.HasThreadAccess)
            UpdateDemoWatermark();
        else
            dq.TryEnqueue(UpdateDemoWatermark);
    }

    private void UpdateDemoWatermark() =>
        DemoWatermark.Visibility = AppSettings.DemoMode ? Visibility.Visible : Visibility.Collapsed;

    public void PlaceOnOutputMonitor()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));

        var secondary = MonitorHelper.GetSecondaryMonitorBounds();
        if (secondary is null)
        {
            appWindow.Resize(new Windows.Graphics.SizeInt32(1280, 720));
            Activate();
            return;
        }

        try
        {
            appWindow.Show();
            var expanded = MonitorHelper.ExpandBounds(secondary.Value, 2);
            MonitorHelper.ApplyWin32BorderlessAndBounds(hwnd, expanded);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CleanOutputWindow] place failed: {ex}");
            Activate();
        }
    }
}
