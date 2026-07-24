using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.UI;

namespace Kakikomi.Services;

/// <summary>
/// アプリ設定の読み書き。
/// アンパッケージ（ポータブル）でも確実に残るよう、
/// %LocalAppData%\Kakikomi\settings.json に保存する。
/// </summary>
public static class AppSettings
{
    private const string FileName = "settings.json";

    /// <summary>DEMO モード解除用パスワード。</summary>
    public const string DemoUnlockPassword = "incre1881";

    public static event Action? Changed;

    public static Color DefaultPenRed { get; } = Color.FromArgb(255, 239, 68, 68);
    public static Color DefaultPenGreen { get; } = Color.FromArgb(255, 34, 197, 94);
    public static Color DefaultPenBlue { get; } = Color.FromArgb(255, 59, 130, 246);

    public static Color PenRed { get; private set; } = DefaultPenRed;
    public static Color PenGreen { get; private set; } = DefaultPenGreen;
    public static Color PenBlue { get; private set; } = DefaultPenBlue;
    public static double PenThickness { get; private set; } = 8;
    public static double EraserThickness { get; private set; } = 28;
    public static bool LaunchControlPanelFullSize { get; private set; }

    /// <summary>
    /// ON: 別ネタへ行って戻ると、前回止めた位置から再開。
    /// OFF（既定）: 戻るたびに先頭から。
    /// </summary>
    public static bool ResumePlayback { get; private set; }

    /// <summary>既定 ON。解除パスワードで OFF にできる。</summary>
    public static bool DemoMode { get; private set; } = true;

    private static string StorePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kakikomi",
            FileName);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Load()
    {
        try
        {
            if (!File.Exists(StorePath))
                return;

            var dto = JsonSerializer.Deserialize<SettingsDto>(File.ReadAllText(StorePath), JsonOptions);
            if (dto is null)
                return;

            if (TryParseColorHex(dto.PenRed, out var red))
                PenRed = Unpack(red);
            if (TryParseColorHex(dto.PenGreen, out var green))
                PenGreen = Unpack(green);
            if (TryParseColorHex(dto.PenBlue, out var blue))
                PenBlue = Unpack(blue);

            if (dto.PenThickness is { } penT)
                PenThickness = Math.Clamp(penT, 1, 40);
            if (dto.EraserThickness is { } eraserT)
                EraserThickness = Math.Clamp(eraserT, 4, 80);
            if (dto.LaunchControlPanelFullSize is { } full)
                LaunchControlPanelFullSize = full;
            if (dto.ResumePlayback is { } resume)
                ResumePlayback = resume;
            if (dto.DemoMode is { } demo)
                DemoMode = demo;
        }
        catch
        {
            // 読めなくても既定値で動かす
        }
    }

    public static void SetPenRed(Color color)
    {
        PenRed = color;
        Persist();
        Changed?.Invoke();
    }

    public static void SetPenGreen(Color color)
    {
        PenGreen = color;
        Persist();
        Changed?.Invoke();
    }

    public static void SetPenBlue(Color color)
    {
        PenBlue = color;
        Persist();
        Changed?.Invoke();
    }

    /// <summary>ペン1〜3を既定の赤・緑・青に戻す。</summary>
    public static void ResetPenColorsToDefault()
    {
        PenRed = DefaultPenRed;
        PenGreen = DefaultPenGreen;
        PenBlue = DefaultPenBlue;
        Persist();
        Changed?.Invoke();
    }

    public static void SetPenThickness(double thickness)
    {
        PenThickness = Math.Clamp(thickness, 1, 40);
        Persist();
        Changed?.Invoke();
    }

    public static void SetEraserThickness(double thickness)
    {
        EraserThickness = Math.Clamp(thickness, 4, 80);
        Persist();
        Changed?.Invoke();
    }

    public static void SetLaunchControlPanelFullSize(bool enabled)
    {
        LaunchControlPanelFullSize = enabled;
        Persist();
        Changed?.Invoke();
    }

    public static void SetResumePlayback(bool enabled)
    {
        ResumePlayback = enabled;
        Persist();
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
        Persist();
        Changed?.Invoke();
    }

    private static void Persist()
    {
        try
        {
            var dir = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var dto = new SettingsDto
            {
                PenRed = ToHex(PenRed),
                PenGreen = ToHex(PenGreen),
                PenBlue = ToHex(PenBlue),
                PenThickness = PenThickness,
                EraserThickness = EraserThickness,
                LaunchControlPanelFullSize = LaunchControlPanelFullSize,
                ResumePlayback = ResumePlayback,
                DemoMode = DemoMode
            };

            File.WriteAllText(StorePath, JsonSerializer.Serialize(dto, JsonOptions));
        }
        catch
        {
            // 永続化失敗でも実行中の設定は維持
        }
    }

    private static string ToHex(Color color) =>
        $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Color Unpack(uint packed) =>
        Color.FromArgb(
            (byte)((packed >> 24) & 0xFF),
            (byte)((packed >> 16) & 0xFF),
            (byte)((packed >> 8) & 0xFF),
            (byte)(packed & 0xFF));

    private static bool TryParseColorHex(string? s, out uint packed)
    {
        packed = 0;
        if (string.IsNullOrWhiteSpace(s))
            return false;

        var hex = s.Trim();
        if (hex.StartsWith('#'))
            hex = hex[1..];
        if (hex.Length is not (6 or 8))
            return false;
        if (hex.Length == 6)
            hex = "FF" + hex;
        return uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out packed);
    }

    private sealed class SettingsDto
    {
        public string? PenRed { get; set; }
        public string? PenGreen { get; set; }
        public string? PenBlue { get; set; }
        public double? PenThickness { get; set; }
        public double? EraserThickness { get; set; }
        public bool? LaunchControlPanelFullSize { get; set; }
        public bool? ResumePlayback { get; set; }
        public bool? DemoMode { get; set; }
    }
}
