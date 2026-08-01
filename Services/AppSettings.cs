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

    private static readonly double[] DefaultPenThicknessPresets = [8, 16, 24];
    private static readonly double[] PenThicknessPresets = (double[])DefaultPenThicknessPresets.Clone();
    private static int _penThicknessPresetIndex;

    /// <summary>現在選択中のペン太さ（プリセットから取得）。</summary>
    public static double PenThickness => PenThicknessPresets[_penThicknessPresetIndex];

    public static int PenThicknessPresetIndex => _penThicknessPresetIndex;

    /// <summary>ON: 操作パネルのボタン＋シークバーを映像の上（ボタン→シークバー→映像）。OFF（既定）: 映像の下。</summary>
    public static bool ControlPanelChromeAtTop { get; private set; }

    public static double EraserThickness { get; private set; } = 28;
    public static bool LaunchControlPanelFullSize { get; private set; }

    /// <summary>ネタ一覧サムネイル倍率（1.0 / 1.2 / 1.5）。</summary>
    public static double NetaThumbnailScale { get; private set; } = 1.0;

    /// <summary>ON: ネタ切替時に短いクロスフェード。操作画面・クリーン出力の両方。</summary>
    public static bool NetaSwitchCrossfadeEnabled { get; private set; }

    /// <summary>
    /// ON: 別ネタへ行って戻ると、前回止めた位置から再開。
    /// OFF（既定）: 戻るたびに先頭から。
    /// </summary>
    public static bool ResumePlayback { get; private set; }

    /// <summary>ON: 0.25x / 0.5x / 2x 再生中も音声を出す（ピッチも速度に連動）。OFF（既定）: 等速のみ音声 ON。</summary>
    public static bool VariableSpeedAudioEnabled { get; private set; }

    /// <summary>2倍速の画面更新上限 fps。0 = 制限なし。既定 45。</summary>
    public static int FastPlaybackMaxFps { get; private set; } = 60;

    /// <summary>
    /// ON（既定）: コンパネ映像右上に大きめの再生/停止オーバーレイを表示。
    /// </summary>
    public static bool OverlayPlayButton { get; private set; } = true;

    /// <summary>
    /// ON: 再生中にペン/指で映像へ触れると一時停止し、そのまま書き込み可能。既定 OFF。
    /// </summary>
    public static bool TouchVideoPauseAndDraw { get; private set; }

    /// <summary>
    /// ON: 操作パネル左下に CPU / メモリ / 遠隔プレビュー fps を表示。既定 OFF。
    /// </summary>
    public static bool PerfMonitorEnabled { get; private set; }

    /// <summary>
    /// ON: 負荷をファイルへ記録（平常 30 秒、危険時 5 秒）。既定 OFF。
    /// </summary>
    public static bool PerfLogEnabled { get; private set; }

    /// <summary>
    /// ON: 操作プレビューとクリーン出力を PNG 自動保存（定期・異常・復旧時）。既定 OFF。
    /// </summary>
    public static bool DiagnosticCaptureEnabled { get; private set; }

    /// <summary>診断キャプチャの定期間隔（分）。既定 10。</summary>
    public static int DiagnosticCaptureIntervalMinutes { get; private set; } = 10;

    /// <summary>LAN 遠隔操作（Web）を有効化。既定 OFF。</summary>
    public static bool RemoteControlEnabled { get; private set; }

    /// <summary>遠隔 HTTP ポート（既定 18765）。</summary>
    public static int RemoteControlPort { get; private set; } = 18765;

    /// <summary>遠隔接続用 PIN（空なら認証なし）。</summary>
    public static string RemoteControlPin { get; private set; } = "kakikomi";

    /// <summary>既定 OFF。設定で ON にできる（解除パスワードで OFF に戻す）。</summary>
    public static bool DemoMode { get; private set; } = false;

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

            if (dto.PenThicknessPresets is { Length: >= 3 } presetValues)
            {
                for (var i = 0; i < 3; i++)
                    PenThicknessPresets[i] = ClampPenThickness(presetValues[i]);
            }
            else if (dto.PenThickness is { } legacyPenT)
            {
                PenThicknessPresets[0] = ClampPenThickness(legacyPenT);
            }

            if (dto.PenThicknessPresetIndex is { } presetIndex)
                _penThicknessPresetIndex = Math.Clamp(presetIndex, 0, 2);
            if (dto.ControlPanelChromeAtTop is { } chromeAtTop)
                ControlPanelChromeAtTop = chromeAtTop;
            if (dto.EraserThickness is { } eraserT)
                EraserThickness = Math.Clamp(eraserT, 4, 80);
            if (dto.LaunchControlPanelFullSize is { } full)
                LaunchControlPanelFullSize = full;
            if (dto.NetaThumbnailScale is { } thumbScale)
                NetaThumbnailScale = NetaThumbnailMetrics.NormalizeScale(thumbScale);
            if (dto.NetaSwitchCrossfadeEnabled is { } crossfade)
                NetaSwitchCrossfadeEnabled = crossfade;
            if (dto.ResumePlayback is { } resume)
                ResumePlayback = resume;
            if (dto.VariableSpeedAudioEnabled is { } variableSpeedAudio)
                VariableSpeedAudioEnabled = variableSpeedAudio;
            if (dto.FastPlaybackMaxFps is { } fastFps)
                FastPlaybackMaxFps = NormalizeFastPlaybackMaxFps(fastFps);
            if (dto.OverlayPlayButton is { } overlayPlay)
                OverlayPlayButton = overlayPlay;
            if (dto.TouchVideoPauseAndDraw is { } touchPauseDraw)
                TouchVideoPauseAndDraw = touchPauseDraw;
            if (dto.PerfMonitorEnabled is { } perfMon)
                PerfMonitorEnabled = perfMon;
            if (dto.PerfLogEnabled is { } perfLog)
                PerfLogEnabled = perfLog;
            if (dto.DiagnosticCaptureEnabled is { } diagCap)
                DiagnosticCaptureEnabled = diagCap;
            if (dto.DiagnosticCaptureIntervalMinutes is { } diagMin)
                DiagnosticCaptureIntervalMinutes = Math.Clamp(diagMin, 1, 120);
            if (dto.RemoteControlEnabled is { } remoteOn)
                RemoteControlEnabled = remoteOn;
            if (dto.RemoteControlPort is { } remotePort)
                RemoteControlPort = Math.Clamp(remotePort, 1024, 65535);
            if (dto.RemoteControlPin is not null)
                RemoteControlPin = dto.RemoteControlPin;
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

    public static double GetPenThicknessPreset(int index) =>
        PenThicknessPresets[Math.Clamp(index, 0, 2)];

    public static void SetPenThicknessPreset(int index, double thickness)
    {
        index = Math.Clamp(index, 0, 2);
        PenThicknessPresets[index] = ClampPenThickness(thickness);
        Persist();
        Changed?.Invoke();
    }

    public static void SetPenThicknessPresetIndex(int index)
    {
        index = Math.Clamp(index, 0, 2);
        if (_penThicknessPresetIndex == index)
            return;

        _penThicknessPresetIndex = index;
        Persist();
        Changed?.Invoke();
    }

    public static void ResetPenThicknessPresetsToDefault()
    {
        Array.Copy(DefaultPenThicknessPresets, PenThicknessPresets, 3);
        _penThicknessPresetIndex = 0;
        Persist();
        Changed?.Invoke();
    }

    private static double ClampPenThickness(double thickness) =>
        Math.Clamp(thickness, 1, 40);

    public static void SetControlPanelChromeAtTop(bool atTop)
    {
        ControlPanelChromeAtTop = atTop;
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

    public static void SetNetaThumbnailScale(double scale)
    {
        NetaThumbnailScale = NetaThumbnailMetrics.NormalizeScale(scale);
        Persist();
        Changed?.Invoke();
    }

    public static void SetNetaSwitchCrossfadeEnabled(bool enabled)
    {
        NetaSwitchCrossfadeEnabled = enabled;
        Persist();
        Changed?.Invoke();
    }

    public static void SetResumePlayback(bool enabled)
    {
        ResumePlayback = enabled;
        Persist();
        Changed?.Invoke();
    }

    public static void SetVariableSpeedAudioEnabled(bool enabled)
    {
        VariableSpeedAudioEnabled = enabled;
        Persist();
        Changed?.Invoke();
    }

    public static void SetFastPlaybackMaxFps(int fps)
    {
        FastPlaybackMaxFps = NormalizeFastPlaybackMaxFps(fps);
        Persist();
        Changed?.Invoke();
    }

    /// <summary>2倍速の最小表示間隔（ms）。0 なら制限なし。</summary>
    public static int GetFastPlaybackPresentIntervalMs() =>
        FastPlaybackMaxFps <= 0 ? 0 : (int)Math.Round(1000.0 / FastPlaybackMaxFps);

    public static int NormalizeFastPlaybackMaxFps(int fps) =>
        fps switch
        {
            0 => 0,
            24 => 24,
            30 => 30,
            45 => 45,
            50 => 50,
            60 => 60,
            _ => 60
        };

    public static void SetOverlayPlayButton(bool enabled)
    {
        OverlayPlayButton = enabled;
        Persist();
        Changed?.Invoke();
    }

    public static void SetTouchVideoPauseAndDraw(bool enabled)
    {
        TouchVideoPauseAndDraw = enabled;
        Persist();
        Changed?.Invoke();
    }

    public static void SetPerfMonitorEnabled(bool enabled)
    {
        PerfMonitorEnabled = enabled;
        Persist();
        Changed?.Invoke();
    }

    public static void SetPerfLogEnabled(bool enabled)
    {
        PerfLogEnabled = enabled;
        Persist();
        Changed?.Invoke();
    }

    public static void SetDiagnosticCaptureEnabled(bool enabled)
    {
        DiagnosticCaptureEnabled = enabled;
        Persist();
        Changed?.Invoke();
    }

    public static void SetDiagnosticCaptureIntervalMinutes(int minutes)
    {
        DiagnosticCaptureIntervalMinutes = Math.Clamp(minutes, 1, 120);
        Persist();
        Changed?.Invoke();
    }

    public static void SetRemoteControlEnabled(bool enabled)
    {
        RemoteControlEnabled = enabled;
        Persist();
        Changed?.Invoke();
    }

    public static void SetRemoteControlPort(int port)
    {
        RemoteControlPort = Math.Clamp(port, 1024, 65535);
        Persist();
        Changed?.Invoke();
    }

    public static void SetRemoteControlPin(string? pin)
    {
        RemoteControlPin = pin ?? "";
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
                PenThicknessPresets = PenThicknessPresets.ToArray(),
                PenThicknessPresetIndex = _penThicknessPresetIndex,
                ControlPanelChromeAtTop = ControlPanelChromeAtTop,
                EraserThickness = EraserThickness,
                LaunchControlPanelFullSize = LaunchControlPanelFullSize,
                NetaThumbnailScale = NetaThumbnailScale,
                NetaSwitchCrossfadeEnabled = NetaSwitchCrossfadeEnabled,
                ResumePlayback = ResumePlayback,
                VariableSpeedAudioEnabled = VariableSpeedAudioEnabled,
                FastPlaybackMaxFps = FastPlaybackMaxFps,
                OverlayPlayButton = OverlayPlayButton,
                TouchVideoPauseAndDraw = TouchVideoPauseAndDraw,
                PerfMonitorEnabled = PerfMonitorEnabled,
                PerfLogEnabled = PerfLogEnabled,
                DiagnosticCaptureEnabled = DiagnosticCaptureEnabled,
                DiagnosticCaptureIntervalMinutes = DiagnosticCaptureIntervalMinutes,
                RemoteControlEnabled = RemoteControlEnabled,
                RemoteControlPort = RemoteControlPort,
                RemoteControlPin = RemoteControlPin,
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
        public double[]? PenThicknessPresets { get; set; }
        public int? PenThicknessPresetIndex { get; set; }
        public bool? ControlPanelChromeAtTop { get; set; }
        public double? EraserThickness { get; set; }
        public bool? LaunchControlPanelFullSize { get; set; }
        public double? NetaThumbnailScale { get; set; }
        public bool? NetaSwitchCrossfadeEnabled { get; set; }
        public bool? ResumePlayback { get; set; }
        public bool? VariableSpeedAudioEnabled { get; set; }
        public int? FastPlaybackMaxFps { get; set; }
        public bool? OverlayPlayButton { get; set; }
        public bool? TouchVideoPauseAndDraw { get; set; }
        public bool? PerfMonitorEnabled { get; set; }
        public bool? PerfLogEnabled { get; set; }
        public bool? DiagnosticCaptureEnabled { get; set; }
        public int? DiagnosticCaptureIntervalMinutes { get; set; }
        public bool? RemoteControlEnabled { get; set; }
        public int? RemoteControlPort { get; set; }
        public string? RemoteControlPin { get; set; }
        public bool? DemoMode { get; set; }
    }
}
