using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Kakikomi.Services;

public enum DiagnosticCaptureReason
{
    Periodic,
    Anomaly,
    RecoveryStart,
    RecoverySuccess,
    RecoveryFailed,
}

/// <summary>
/// 操作プレビュー（1920×1080）とクリーン出力を PNG で自動保存する（調査用）。
/// </summary>
public sealed class DiagnosticCaptureService : IDisposable
{
    public static DiagnosticCaptureService Instance { get; } = new();

    private const int RetainDays = 7;
    private const int MinSameReasonIntervalMs = 30_000;
    private const int DefaultIntervalMinutes = 10;

    private readonly object _gate = new();
    private WeakReference<UIElement>? _operatorSurface;
    private WeakReference<UIElement>? _cleanSurface;
    private DispatcherQueue? _dispatcherQueue;
    private DispatcherQueueTimer? _periodicTimer;
    private int _captureInFlight;
    private string? _lastReasonKey;
    private long _lastReasonMs;
    private bool _disposed;

    public static string DirectoryPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kakikomi",
            "diagnostics");

    public void RegisterOperatorSurface(UIElement surface) =>
        _operatorSurface = new WeakReference<UIElement>(surface);

    public void RegisterCleanSurface(UIElement surface) =>
        _cleanSurface = new WeakReference<UIElement>(surface);

    public void ApplyFromSettings()
    {
        var dq = App.DispatcherQueue;
        if (dq is null)
            return;

        if (dq.HasThreadAccess)
            ApplyFromSettingsCore(dq);
        else
            dq.TryEnqueue(() => ApplyFromSettingsCore(dq));
    }

    private void ApplyFromSettingsCore(DispatcherQueue dq)
    {
        _dispatcherQueue = dq;
        _periodicTimer?.Stop();
        _periodicTimer = null;

        if (!AppSettings.DiagnosticCaptureEnabled)
            return;

        var minutes = Math.Clamp(AppSettings.DiagnosticCaptureIntervalMinutes, 1, 120);
        _periodicTimer = dq.CreateTimer();
        _periodicTimer.Interval = TimeSpan.FromMinutes(minutes);
        _periodicTimer.Tick += (_, _) => RequestCapture(DiagnosticCaptureReason.Periodic, "timer");
        _periodicTimer.Start();
    }

    public void RequestCapture(DiagnosticCaptureReason reason, string? detail = null)
    {
        if (!AppSettings.DiagnosticCaptureEnabled)
            return;

        var reasonKey = $"{reason}:{detail ?? ""}";
        var now = Environment.TickCount64;
        if (reason is DiagnosticCaptureReason.Periodic or DiagnosticCaptureReason.Anomaly)
        {
            lock (_gate)
            {
                if (_lastReasonKey == reasonKey && now - _lastReasonMs < MinSameReasonIntervalMs)
                    return;
                _lastReasonKey = reasonKey;
                _lastReasonMs = now;
            }
        }

        var dq = _dispatcherQueue ?? App.DispatcherQueue;
        if (dq is null)
            return;

        if (Interlocked.CompareExchange(ref _captureInFlight, 1, 0) != 0)
            return;

        if (!dq.TryEnqueue(async () =>
            {
                try
                {
                    await CaptureAsync(reason, detail).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DiagnosticCapture] {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _captureInFlight, 0);
                }
            }))
        {
            Interlocked.Exchange(ref _captureInFlight, 0);
        }
    }

    private async Task CaptureAsync(DiagnosticCaptureReason reason, string? detail)
    {
        Directory.CreateDirectory(DirectoryPath);
        PruneOldFiles();

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var tag = SanitizeFileToken(ReasonToken(reason, detail));
        var engine = App.Engine;
        var memMb = (int)Math.Round(PerfMonitorService.Instance.MemoryMb);
        var suffix = $"_{memMb}MB";

        if (TryGetSurface(_operatorSurface, out var op))
            await SaveSurfaceAsync(op, $"{stamp}_{tag}_operator{suffix}.png").ConfigureAwait(true);

        if (TryGetSurface(_cleanSurface, out var clean))
            await SaveSurfaceAsync(clean, $"{stamp}_{tag}_clean{suffix}.png").ConfigureAwait(true);
        else if (engine is not null && TryGetSurface(_operatorSurface, out op))
            await SaveSurfaceAsync(op, $"{stamp}_{tag}_clean-missing{suffix}.png").ConfigureAwait(true);

        PerfMonitorService.Instance.LogEvent(
            "INFO",
            $"DiagnosticCapture {ReasonToken(reason, detail)} mem={memMb}MB path={engine?.CurrentPath}");
    }

    private static bool TryGetSurface(WeakReference<UIElement>? reference, out UIElement surface)
    {
        surface = null!;
        return reference is not null &&
               reference.TryGetTarget(out var target) &&
               (surface = target) is not null;
    }

    private static async Task SaveSurfaceAsync(UIElement element, string fileName)
    {
        var path = Path.Combine(DirectoryPath, fileName);
        var renderTarget = new RenderTargetBitmap();
        await renderTarget.RenderAsync(element);

        var pixels = await renderTarget.GetPixelsAsync();
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)renderTarget.PixelWidth,
            (uint)renderTarget.PixelHeight,
            96,
            96,
            pixels.ToArray());
        await encoder.FlushAsync();

        stream.Seek(0);
        using var fs = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await stream.AsStreamForRead().CopyToAsync(fs);
    }

    private static string ReasonToken(DiagnosticCaptureReason reason, string? detail)
    {
        var baseToken = reason switch
        {
            DiagnosticCaptureReason.Periodic => "periodic",
            DiagnosticCaptureReason.Anomaly => "anomaly",
            DiagnosticCaptureReason.RecoveryStart => "recovery-start",
            DiagnosticCaptureReason.RecoverySuccess => "recovery-ok",
            DiagnosticCaptureReason.RecoveryFailed => "recovery-failed",
            _ => reason.ToString().ToLowerInvariant(),
        };

        if (string.IsNullOrWhiteSpace(detail))
            return baseToken;

        return $"{baseToken}-{SanitizeFileToken(detail)}";
    }

    private static string SanitizeFileToken(string value)
    {
        var chars = value
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')
            .ToArray();
        var token = new string(chars).Trim('-');
        if (token.Length > 48)
            token = token[..48].TrimEnd('-');
        return string.IsNullOrEmpty(token) ? "event" : token;
    }

    private static void PruneOldFiles()
    {
        try
        {
            if (!Directory.Exists(DirectoryPath))
                return;

            var cutoff = DateTime.Now.AddDays(-RetainDays);
            foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.png"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch
        {
            // ignore
        }
    }

    public static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = DirectoryPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DiagnosticCapture] open folder: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _periodicTimer?.Stop();
        _periodicTimer = null;
    }
}
