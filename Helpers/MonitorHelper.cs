using System.Runtime.InteropServices;

namespace Kakikomi.Helpers;

internal static class MonitorHelper
{
    private const int MonitorInfoPrimary = 0x00000001;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WsExWindowEdge = 0x00000100;
    private const int WsExClientEdge = 0x00000200;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsVisible = 0x10000000;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpDonotround = 1;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaNcRenderingPolicy = 2;
    private const int DwmncrpDisabled = 1;
    private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);

    public readonly record struct MonitorBounds(int Left, int Top, int Width, int Height, bool IsPrimary);

    public static MonitorBounds? GetSecondaryMonitorBounds()
    {
        var monitors = EnumerateMonitors();
        foreach (var monitor in monitors)
        {
            if (monitor.IsPrimary)
                continue;

            return new MonitorBounds(
                monitor.Monitor.Left,
                monitor.Monitor.Top,
                monitor.Monitor.Right - monitor.Monitor.Left,
                monitor.Monitor.Bottom - monitor.Monitor.Top,
                false);
        }

        // セカンダリが無いときは null（プライマリへフルスクリーンしない）
        return null;
    }

    public static bool HasSecondaryMonitor() => GetSecondaryMonitorBounds() is not null;

    public static MonitorBounds? GetPrimaryMonitorBounds()
    {
        var monitors = EnumerateMonitors();
        foreach (var monitor in monitors)
        {
            if (!monitor.IsPrimary)
                continue;

            return new MonitorBounds(
                monitor.Monitor.Left,
                monitor.Monitor.Top,
                monitor.Monitor.Right - monitor.Monitor.Left,
                monitor.Monitor.Bottom - monitor.Monitor.Top,
                true);
        }

        return null;
    }

    /// <summary>
    /// 指定ウィンドウをプライマリ（なければ先頭）モニタ中央に出し、最前面へ。
    /// </summary>
    public static void ShowCenteredOnPrimary(IntPtr hwnd, int width, int height)
    {
        try
        {
            var bounds = GetPrimaryMonitorBounds();
            if (bounds is null)
            {
                var monitors = EnumerateMonitors();
                if (monitors.Count == 0)
                    return;

                var m = monitors[0].Monitor;
                bounds = new MonitorBounds(m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top, true);
            }

            var b = bounds.Value;
            var x = b.Left + Math.Max(0, (b.Width - width) / 2);
            var y = b.Top + Math.Max(0, (b.Height - height) / 2);

            // いったん TOPMOST で前面化し、直後に通常へ戻す（クリーン出力の裏に隠れない）
            SetWindowPos(hwnd, new IntPtr(-1), x, y, width, height, SwpShowWindow | SwpFrameChanged);
            SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow | SwpFrameChanged);
            _ = SetForegroundWindow(hwnd);
            _ = BringWindowToTop(hwnd);
            _ = ShowWindow(hwnd, 5); // SW_SHOW
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MonitorHelper] ShowCenteredOnPrimary failed: {ex}");
        }
    }

    public static MonitorBounds ExpandBounds(MonitorBounds bounds, int overscanPixels)
    {
        var px = Math.Max(0, overscanPixels);
        return new MonitorBounds(
            bounds.Left - px,
            bounds.Top - px,
            bounds.Width + px * 2,
            bounds.Height + px * 2,
            bounds.IsPrimary);
    }

    public static bool ApplyWin32BorderlessAndBounds(IntPtr hwnd, MonitorBounds bounds)
    {
        try
        {
            SetWindowLong(hwnd, GwlStyle, WsPopup | WsVisible);

            var exStyle = GetWindowLong(hwnd, GwlExStyle);
            exStyle &= ~(WsExWindowEdge | WsExClientEdge);
            SetWindowLong(hwnd, GwlExStyle, exStyle);

            ApplyDwmBorderless(hwnd);

            SetWindowPos(
                hwnd,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged | SwpNoActivate);

            return SetWindowPos(
                hwnd,
                IntPtr.Zero,
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height,
                SwpShowWindow | SwpFrameChanged | SwpNoActivate);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MonitorHelper] ApplyWin32BorderlessAndBounds failed: {ex}");
            return false;
        }
    }

    private static void ApplyDwmBorderless(IntPtr hwnd)
    {
        try
        {
            var corner = DwmwcpDonotround;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));

            var policy = DwmncrpDisabled;
            _ = DwmSetWindowAttribute(hwnd, DwmwaNcRenderingPolicy, ref policy, sizeof(int));

            var colorNone = DwmwaColorNone;
            _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref colorNone, sizeof(int));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MonitorHelper] ApplyDwmBorderless failed: {ex}");
        }
    }

    private static List<MonitorInfo> EnumerateMonitors()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (hMonitor, _, _, _) =>
            {
                var info = new NativeMonitorInfo { cbSize = Marshal.SizeOf<NativeMonitorInfo>() };
                if (GetMonitorInfo(hMonitor, ref info))
                {
                    list.Add(new MonitorInfo(
                        ToRect(info.rcMonitor),
                        (info.dwFlags & MonitorInfoPrimary) != 0));
                }

                return true;
            },
            IntPtr.Zero);
        return list;
    }

    private static NativeRect ToRect(NativeRect native) => native;

    private readonly struct MonitorInfo(NativeRect monitor, bool isPrimary)
    {
        public NativeRect Monitor { get; } = monitor;
        public bool IsPrimary { get; } = isPrimary;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref NativeMonitorInfo lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static int GetWindowLong(IntPtr hwnd, int index) =>
        IntPtr.Size == 8 ? (int)GetWindowLongPtr64(hwnd, index) : GetWindowLong32(hwnd, index);

    private static void SetWindowLong(IntPtr hwnd, int index, int value)
    {
        if (IntPtr.Size == 8)
            SetWindowLongPtr64(hwnd, index, new IntPtr(value));
        else
            SetWindowLong32(hwnd, index, value);
    }
}
