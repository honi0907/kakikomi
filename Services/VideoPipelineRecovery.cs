using Windows.Media.Playback;

namespace Kakikomi.Services;

/// <summary>
/// 本番映像（操作＋クリーン）の Frame Server 経路を段階的に復旧する。
/// 遠隔プレビューは本番より優先度を下げ、失敗してもここを優先する。
/// </summary>
internal static class VideoPipelineRecovery
{
    private const int CopyFailuresBeforeLevel2 = 3;
    private const int NoFrameMsBeforeLevel2 = 3_000;
    private const int RecoverCooldownMs = 8_000;
    private const int FrameResumeTimeoutMs = 4_000;

    private static readonly object Gate = new();
    private static int _copyFailures;
    private static int _recoverGeneration;
    private static long _lastRecoverMs;
    private static long _lastFrameMs;
    private static long _recoverStartedMs;
    private static int _recoverInFlight;

    public static void NotifyFrameDelivered()
    {
        Interlocked.Exchange(ref _lastFrameMs, Environment.TickCount64);
        Interlocked.Exchange(ref _copyFailures, 0);
    }

    public static void NotifyCopyFailure(MediaPlayer player, Exception ex)
    {
        var failures = Interlocked.Increment(ref _copyFailures);
        if (failures < CopyFailuresBeforeLevel2)
            return;

        Interlocked.Exchange(ref _copyFailures, 0);
        RequestRecover(player, level: 2, $"copy:{FormatEx(ex)}");
    }

    public static void NotifyDrawFailure(string detail)
    {
        RequestRecover(App.Engine?.OperatorPlayer, level: 2, $"draw:{detail}");
    }

    public static void TickHealthCheck()
    {
        var engine = App.Engine;
        if (engine is null || !engine.IsPlaying || engine.CurrentPath is null)
            return;

        var last = Interlocked.Read(ref _lastFrameMs);
        if (last == 0)
            return;

        var now = Environment.TickCount64;
        var noFrameMs = engine.ClockRate > 1.5 ? 8_000 : NoFrameMsBeforeLevel2;
        if (now - last < noFrameMs)
            return;

        RequestRecover(engine.OperatorPlayer, level: 2, "no-frames-while-playing");
    }

    private static void RequestRecover(MediaPlayer? player, int level, string reason)
    {
        if (player is null)
            return;

        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastRecoverMs) < RecoverCooldownMs)
            return;

        if (Interlocked.CompareExchange(ref _recoverInFlight, 1, 0) != 0)
            return;

        Interlocked.Exchange(ref _lastRecoverMs, now);
        var generation = Interlocked.Increment(ref _recoverGeneration);

        DiagnosticCaptureService.Instance.RequestCapture(DiagnosticCaptureReason.Anomaly, reason);
        PerfMonitorService.Instance.LogEvent("WARN", $"VideoPipeline recover L{level} reason={reason}");

        var dq = App.DispatcherQueue;
        if (dq is null)
        {
            Interlocked.Exchange(ref _recoverInFlight, 0);
            return;
        }

        dq.TryEnqueue(async () =>
        {
            try
            {
                if (generation != Volatile.Read(ref _recoverGeneration))
                    return;

                await RunRecoverAsync(player, level, reason).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PerfMonitorService.Instance.LogEvent("WARN", $"VideoPipeline recover failed: {FormatEx(ex)}");
                DiagnosticCaptureService.Instance.RequestCapture(
                    DiagnosticCaptureReason.RecoveryFailed,
                    $"exception:{FormatEx(ex)}");
            }
            finally
            {
                Interlocked.Exchange(ref _recoverInFlight, 0);
            }
        });
    }

    private static async Task RunRecoverAsync(MediaPlayer player, int level, string reason)
    {
        Interlocked.Exchange(ref _recoverStartedMs, Environment.TickCount64);
        DiagnosticCaptureService.Instance.RequestCapture(DiagnosticCaptureReason.RecoveryStart, reason);

        if (level >= 2)
            await Level2Async(player).ConfigureAwait(true);

        if (await WaitForFramesAfterRecoverAsync(FrameResumeTimeoutMs).ConfigureAwait(true))
        {
            PerfMonitorService.Instance.LogEvent("INFO", "VideoPipeline recover L2 ok");
            DiagnosticCaptureService.Instance.RequestCapture(DiagnosticCaptureReason.RecoverySuccess, "L2");
            return;
        }

        PerfMonitorService.Instance.LogEvent("WARN", "VideoPipeline recover L2 failed, escalating L3");
        DiagnosticCaptureService.Instance.RequestCapture(DiagnosticCaptureReason.RecoveryStart, "L3-escalation");

        var engine = App.Engine;
        if (engine is not null)
        {
            Interlocked.Exchange(ref _recoverStartedMs, Environment.TickCount64);
            await engine.RecoverVideoPipelineAsync().ConfigureAwait(true);
        }

        if (await WaitForFramesAfterRecoverAsync(FrameResumeTimeoutMs).ConfigureAwait(true))
        {
            PerfMonitorService.Instance.LogEvent("INFO", "VideoPipeline recover L3 ok");
            DiagnosticCaptureService.Instance.RequestCapture(DiagnosticCaptureReason.RecoverySuccess, "L3");
            return;
        }

        PerfMonitorService.Instance.LogEvent("WARN", "VideoPipeline recover L3 failed");
        DiagnosticCaptureService.Instance.RequestCapture(DiagnosticCaptureReason.RecoveryFailed, reason);
    }

    private static async Task Level2Async(MediaPlayer player)
    {
        var fastPlayback = (App.Engine?.ClockRate ?? 1.0) > 1.5;

        if (!fastPlayback)
        {
            try
            {
                player.IsVideoFrameServerEnabled = false;
                await Task.Delay(80).ConfigureAwait(true);
                player.IsVideoFrameServerEnabled = true;
            }
            catch (Exception ex)
            {
                PerfMonitorService.Instance.LogEvent("WARN", $"VideoPipeline frame-server toggle: {FormatEx(ex)}");
            }
        }

        CompositionVideoHostRegistry.ForceRebindAll();

        var engine = App.Engine;
        if (engine?.CurrentPath is not null)
        {
            if (engine.IsPlaying)
                engine.RequestPlayingFrameRefresh();
            else
                engine.RequestPausedFrameRefresh();
        }
    }

    private static async Task<bool> WaitForFramesAfterRecoverAsync(int timeoutMs)
    {
        var started = Interlocked.Read(ref _recoverStartedMs);
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var last = Interlocked.Read(ref _lastFrameMs);
            if (last >= started)
                return true;

            await Task.Delay(100).ConfigureAwait(true);
        }

        return false;
    }

    private static string FormatEx(Exception ex)
    {
        if (!string.IsNullOrWhiteSpace(ex.Message))
            return ex.Message;

        try
        {
            return $"HRESULT=0x{ex.HResult:X8}";
        }
        catch
        {
            return ex.GetType().Name;
        }
    }
}
