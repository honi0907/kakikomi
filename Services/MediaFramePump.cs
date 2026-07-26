using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Graphics.Canvas;
using Windows.Media.Playback;

namespace Kakikomi.Services;

/// <summary>
/// MediaPlayer の Frame Server 出力を1回だけ Copy し、本番表示ホストへ配信する。
/// 遠隔プレビューは <see cref="VideoFrameRelay"/> 経由で参照し、購読者（sinks）には含めない。
/// </summary>
internal static class MediaFramePump
{
    private static readonly ConditionalWeakTable<MediaPlayer, Pump> Pumps = new();

    public static IDisposable Subscribe(MediaPlayer player, Action<CanvasRenderTarget, int, int> onFrame)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(onFrame);

        var pump = Pumps.GetValue(player, static p => new Pump(p));
        return pump.AddSink(onFrame);
    }

    private sealed class Pump
    {
        private const int FailuresBeforeRecover = 3;

        private readonly MediaPlayer _player;
        private readonly object _gate = new();
        private readonly List<Action<CanvasRenderTarget, int, int>> _sinks = [];
        private CanvasRenderTarget? _bufferA;
        private CanvasRenderTarget? _bufferB;
        private bool _writeA = true;
        private int _width;
        private int _height;
        private bool _hooked;
        private int _busy;
        private int _consecutiveFailures;
        private long _lastRecoverLogMs;

        public Pump(MediaPlayer player)
        {
            _player = player;
        }

        public IDisposable AddSink(Action<CanvasRenderTarget, int, int> onFrame)
        {
            lock (_gate)
            {
                _sinks.Add(onFrame);
                EnsureHooked_NoLock();
            }

            return new SinkSubscription(this, onFrame);
        }

        private void RemoveSink(Action<CanvasRenderTarget, int, int> onFrame)
        {
            lock (_gate)
            {
                _sinks.Remove(onFrame);
                if (_sinks.Count == 0)
                    Unhook_NoLock();
            }
        }

        private void EnsureHooked_NoLock()
        {
            if (_hooked)
                return;

            try
            {
                _player.IsVideoFrameServerEnabled = true;
                _player.VideoFrameAvailable += OnVideoFrameAvailable;
                _hooked = true;
            }
            catch (Exception ex)
            {
                LogWarnThrottled($"hook failed: {ex.Message}");
            }
        }

        private void Unhook_NoLock()
        {
            if (!_hooked)
                return;

            try
            {
                _player.VideoFrameAvailable -= OnVideoFrameAvailable;
            }
            catch
            {
                // ignore
            }

            _hooked = false;
            DisposeBuffers_NoLock();
        }

        private void OnVideoFrameAvailable(MediaPlayer sender, object args)
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1)
                return;

            try
            {
                Action<CanvasRenderTarget, int, int>[] sinks;
                CanvasRenderTarget readable;
                int width;
                int height;

                lock (_gate)
                {
                    if (_sinks.Count == 0)
                        return;

                    width = (int)sender.PlaybackSession.NaturalVideoWidth;
                    height = (int)sender.PlaybackSession.NaturalVideoHeight;
                    if (width <= 0 || height <= 0)
                    {
                        width = 1920;
                        height = 1080;
                    }

                    if (!EnsureBuffers_NoLock(width, height))
                    {
                        NoteFailure_NoLock(sender, "buffers");
                        return;
                    }

                    var write = _writeA ? _bufferA! : _bufferB!;
                    sender.CopyFrameToVideoSurface(write);
                    _writeA = !_writeA;
                    readable = _writeA ? _bufferB! : _bufferA!;
                    sinks = _sinks.ToArray();
                    _consecutiveFailures = 0;
                }

                // 本番表示を最優先
                foreach (var sink in sinks)
                {
                    try
                    {
                        sink(readable, width, height);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MediaFramePump] sink: {ex.Message}");
                    }
                }

                // 遠隔プレビューは購読ではなくリレー参照（Copy は増やさない）
                VideoFrameRelay.Publish(sender, readable, width, height);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaFramePump] copy: {ex.Message}");
                lock (_gate)
                    NoteFailure_NoLock(sender, $"copy: {ex.Message}", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        private void NoteFailure_NoLock(MediaPlayer player, string reason, Exception? ex = null)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures < FailuresBeforeRecover)
                return;

            _consecutiveFailures = 0;
            Recover_NoLock(reason);

            if (ex is not null)
                VideoPipelineRecovery.NotifyCopyFailure(player, ex);
        }

        private void Recover_NoLock(string reason)
        {
            var now = Environment.TickCount64;
            if (now - _lastRecoverLogMs >= 5_000)
            {
                _lastRecoverLogMs = now;
                lock (_gate)
                    LogWarnThrottled($"recover sinks={_sinks.Count} reason={reason}");
            }

            Unhook_NoLock();
            if (_sinks.Count > 0)
                EnsureHooked_NoLock();
        }

        private bool EnsureBuffers_NoLock(int width, int height)
        {
            if (_bufferA is not null && _bufferB is not null && _width == width && _height == height)
                return true;

            DisposeBuffers_NoLock();
            _width = width;
            _height = height;

            try
            {
                var device = CanvasDevice.GetSharedDevice();
                if (device.IsDeviceLost())
                    device = CreateFallbackDevice();

                _bufferA = new CanvasRenderTarget(device, width, height, 96);
                _bufferB = new CanvasRenderTarget(device, width, height, 96);
                _writeA = true;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaFramePump] buffers: {ex.Message}");
                DisposeBuffers_NoLock();

                try
                {
                    var device = CreateFallbackDevice();
                    _bufferA = new CanvasRenderTarget(device, width, height, 96);
                    _bufferB = new CanvasRenderTarget(device, width, height, 96);
                    _writeA = true;
                    LogWarnThrottled($"buffers recreated with fallback device ({width}x{height})");
                    return true;
                }
                catch (Exception ex2)
                {
                    LogWarnThrottled($"buffers failed: {ex.Message} / {ex2.Message}");
                    DisposeBuffers_NoLock();
                    return false;
                }
            }
        }

        private static CanvasDevice CreateFallbackDevice() => new();

        private void DisposeBuffers_NoLock()
        {
            try { _bufferA?.Dispose(); } catch { /* ignore */ }
            try { _bufferB?.Dispose(); } catch { /* ignore */ }
            _bufferA = null;
            _bufferB = null;
            _width = 0;
            _height = 0;
        }

        private static void LogWarnThrottled(string message) =>
            PerfMonitorService.Instance.LogEvent("WARN", "MediaFramePump " + message);

        private sealed class SinkSubscription : IDisposable
        {
            private Pump? _pump;
            private Action<CanvasRenderTarget, int, int>? _sink;

            public SinkSubscription(Pump pump, Action<CanvasRenderTarget, int, int> sink)
            {
                _pump = pump;
                _sink = sink;
            }

            public void Dispose()
            {
                var pump = Interlocked.Exchange(ref _pump, null);
                var sink = Interlocked.Exchange(ref _sink, null);
                if (pump is not null && sink is not null)
                    pump.RemoveSink(sink);
            }
        }
    }
}
