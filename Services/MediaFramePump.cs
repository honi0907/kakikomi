using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Graphics.Canvas;
using Windows.Media.Playback;

namespace Kakikomi.Services;

/// <summary>
/// MediaPlayer の Frame Server 出力を1回だけ Copy し、購読ホストへ配信する。
/// ホストごとに CopyFrame すると2回目以降が空フレームになることがある。
/// 描画中の上書きを避けるためダブルバッファする。
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

            _player.IsVideoFrameServerEnabled = true;
            _player.VideoFrameAvailable += OnVideoFrameAvailable;
            _hooked = true;
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
                        return;

                    var write = _writeA ? _bufferA! : _bufferB!;
                    sender.CopyFrameToVideoSurface(write);
                    _writeA = !_writeA;
                    readable = _writeA ? _bufferB! : _bufferA!;
                    sinks = _sinks.ToArray();
                }

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
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaFramePump] copy: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
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
                _bufferA = new CanvasRenderTarget(device, width, height, 96);
                _bufferB = new CanvasRenderTarget(device, width, height, 96);
                _writeA = true;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaFramePump] buffers: {ex.Message}");
                DisposeBuffers_NoLock();
                return false;
            }
        }

        private void DisposeBuffers_NoLock()
        {
            try { _bufferA?.Dispose(); } catch { /* ignore */ }
            try { _bufferB?.Dispose(); } catch { /* ignore */ }
            _bufferA = null;
            _bufferB = null;
            _width = 0;
            _height = 0;
        }

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
