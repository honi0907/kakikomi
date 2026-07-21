using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.UI;

namespace Kakikomi.Services;

/// <summary>
/// アプリ設定の読み書き（LocalSettings）。
/// </summary>
public static class AppSettings
{
    private const string KeyPenRed = "PenColorRed";
    private const string KeyPenGreen = "PenColorGreen";
    private const string KeyPenBlue = "PenColorBlue";
    private const string KeyPenThickness = "PenThickness";
    private const string KeyEraserThickness = "EraserThickness";
    private const string KeyLaunchFullSize = "LaunchControlPanelFullSize";
    private const string KeyDemoMode = "DemoMode";

    /// <summary>DEMO モード解除用パスワード。</summary>
    public const string DemoUnlockPassword = "incre1881";

    public static event Action? Changed;

    public static Color PenRed { get; private set; } = Color.FromArgb(255, 239, 68, 68);
    public static Color PenGreen { get; private set; } = Color.FromArgb(255, 34, 197, 94);
    public static Color PenBlue { get; private set; } = Color.FromArgb(255, 59, 130, 246);
    public static double PenThickness { get; private set; } = 6;
    public static double EraserThickness { get; private set; } = 28;
    public static bool LaunchControlPanelFullSize { get; private set; }

    /// <summary>既定 ON。解除パスワードで OFF にできる。</summary>
    public static bool DemoMode { get; private set; } = true;

    public static void Load()
    {
        try
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            PenRed = ReadColor(values, KeyPenRed, PenRed);
            PenGreen = ReadColor(values, KeyPenGreen, PenGreen);
            PenBlue = ReadColor(values, KeyPenBlue, PenBlue);
            PenThickness = ReadDouble(values, KeyPenThickness, 6);
            EraserThickness = ReadDouble(values, KeyEraserThickness, 28);
            LaunchControlPanelFullSize = values.TryGetValue(KeyLaunchFullSize, out var full)
                && full is bool b
                && b;
            // 未保存時は既定 ON
            DemoMode = !values.TryGetValue(KeyDemoMode, out var demo)
                || demo is not bool demoFlag
                || demoFlag;
        }
        catch
        {
            // LocalSettings が使えない環境でも既定値で動かす
        }
    }

    public static void SetPenRed(Color color)
    {
        PenRed = color;
        WriteColor(KeyPenRed, color);
        Changed?.Invoke();
    }

    public static void SetPenGreen(Color color)
    {
        PenGreen = color;
        WriteColor(KeyPenGreen, color);
        Changed?.Invoke();
    }

    public static void SetPenBlue(Color color)
    {
        PenBlue = color;
        WriteColor(KeyPenBlue, color);
        Changed?.Invoke();
    }

    public static void SetPenThickness(double thickness)
    {
        PenThickness = Math.Clamp(thickness, 1, 40);
        WriteDouble(KeyPenThickness, PenThickness);
        Changed?.Invoke();
    }

    public static void SetEraserThickness(double thickness)
    {
        EraserThickness = Math.Clamp(thickness, 4, 80);
        WriteDouble(KeyEraserThickness, EraserThickness);
        Changed?.Invoke();
    }

    public static void SetLaunchControlPanelFullSize(bool enabled)
    {
        LaunchControlPanelFullSize = enabled;
        try
        {
            ApplicationData.Current.LocalSettings.Values[KeyLaunchFullSize] = enabled;
        }
        catch
        {
            // ignore
        }

        Changed?.Invoke();
    }

    public static bool TryUnlockDemoMode(string? password)
    {
        if (!string.Equals(password, DemoUnlockPassword, StringComparison.Ordinal))
            return false;

        SetDemoMode(false);
        return true;
    }

    public static void SetDemoMode(bool enabled)
    {
        DemoMode = enabled;
        try
        {
            ApplicationData.Current.LocalSettings.Values[KeyDemoMode] = enabled;
        }
        catch
        {
            // ignore
        }

        Changed?.Invoke();
    }

    private static Color ReadColor(IPropertySet values, string key, Color fallback)
    {
        if (!values.TryGetValue(key, out var raw) || raw is not uint packed)
            return fallback;

        return Color.FromArgb(
            (byte)((packed >> 24) & 0xFF),
            (byte)((packed >> 16) & 0xFF),
            (byte)((packed >> 8) & 0xFF),
            (byte)(packed & 0xFF));
    }

    private static void WriteColor(string key, Color color)
    {
        try
        {
            uint packed = ((uint)color.A << 24)
                | ((uint)color.R << 16)
                | ((uint)color.G << 8)
                | color.B;
            ApplicationData.Current.LocalSettings.Values[key] = packed;
        }
        catch
        {
            // ignore
        }
    }

    private static double ReadDouble(IPropertySet values, string key, double fallback)
    {
        if (!values.TryGetValue(key, out var raw))
            return fallback;

        return raw switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => fallback
        };
    }

    private static void WriteDouble(string key, double value)
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[key] = value;
        }
        catch
        {
            // ignore
        }
    }
}
