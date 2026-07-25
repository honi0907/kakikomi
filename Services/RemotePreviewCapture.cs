using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Media.Playback;

namespace Kakikomi.Services;

/// <summary>
/// Operator 映像を縮小 JPEG 化して遠隔へ送る。
/// ブラウザ未接続では購読しない。再生中は間引き、ネタ切替直後は一時休止。
/// </summary>
internal sealed class RemotePreviewCapture : IDisposable
{
    private const int TargetWidth = 480;
    private const int IdleIntervalMs = 80; // ポーズ時 ~12.5 fps
    private const int PlayingIntervalMs = 200; // 再生中 ~5 fps
    private const int SwitchPauseMs = 450;
    private const long JpegQuality = 35;

    private readonly Action<byte[]> _onJpeg;
    private readonly object _gate = new();
    private IDisposable? _subscription;
    private MediaPlayer? _subscribedPlayer;
    private EngineSession? _engine;
    private CanvasRenderTarget? _scaled;
    private int _scaledW;
    private int _scaledH;
    private int _encoding;
    private int _forceNext;
    private int _forceGeneration;
    private int _retryScheduled;
    private long _lastEncodeTicks;
    private long _pauseUntilTicks;
    private int _clientsConnected;
    private bool _disposed;

    public RemotePreviewCapture(Action<byte[]> onJpeg)
    {
        _onJpeg = onJpeg;
    }

    public void Start()
    {
        var engine = App.Engine;
        if (engine is null)
            return;

        _engine = engine;
        engine.VisibleSlotChanged += OnVisibleSlotChanged;
        engine.SourceChanged += OnSourceChanged;
        engine.PreviewKeyframeRequested += OnKeyframeRequested;
        // クライアント接続まで MediaFramePump に相乗りしない
    }

    public void SetClientsConnected(bool connected)
    {
        var was = Interlocked.Exchange(ref _clientsConnected, connected ? 1 : 0);
        if (connected)
        {
            var engine = _engine;
            if (engine is not null)
                Resubscribe(engine.VisibleSlotIndex, force: true);
        }
        else if (was == 1)
        {
            Unsubscribe();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_engine is not null)
        {
            _engine.VisibleSlotChanged -= OnVisibleSlotChanged;
            _engine.SourceChanged -= OnSourceChanged;
            _engine.PreviewKeyframeRequested -= OnKeyframeRequested;
        }

        Unsubscribe();
        lock (_gate)
        {
            try { _scaled?.Dispose(); } catch { /* ignore */ }
            _scaled = null;
        }
    }

    private void OnVisibleSlotChanged(int slot)
    {
        PauseCaptureBriefly();
        if (Volatile.Read(ref _clientsConnected) == 1)
            Resubscribe(slot, force: true);
    }

    private void OnSourceChanged()
    {
        PauseCaptureBriefly();
        var engine = _engine;
        if (engine is not null && Volatile.Read(ref _clientsConnected) == 1)
            Resubscribe(engine.VisibleSlotIndex, force: true);
    }

    private void OnKeyframeRequested()
    {
        if (Volatile.Read(ref _clientsConnected) != 1)
            return;
        ArmForceCapture();
    }

    private void PauseCaptureBriefly() =>
        Interlocked.Exchange(ref _pauseUntilTicks, Environment.TickCount64 + SwitchPauseMs);

    private void ArmForceCapture()
    {
        Interlocked.Exchange(ref _lastEncodeTicks, 0);
        Interlocked.Exchange(ref _forceNext, 1);
        var gen = Interlocked.Increment(ref _forceGeneration);
        ScheduleForceRetries(gen);
    }

    private void Unsubscribe()
    {
        lock (_gate)
        {
            _subscription?.Dispose();
            _subscription = null;
            _subscribedPlayer = null;
        }
    }

    private void Resubscribe(int slot, bool force)
    {
        if (_disposed || Volatile.Read(ref _clientsConnected) != 1)
            return;

        var engine = _engine;
        if (engine is null)
            return;

        MediaPlayer player;
        try
        {
            player = engine.GetOperatorPlayerForSlot(slot);
        }
        catch
        {
            return;
        }

        lock (_gate)
        {
            // 同一プレイヤーなら張り直さない（頭出しコマを落とす主因だった）
            if (!ReferenceEquals(_subscribedPlayer, player) || _subscription is null)
            {
                _subscription?.Dispose();
                _subscribedPlayer = player;
                _subscription = MediaFramePump.Subscribe(player, OnFrame);
            }
        }

        if (force)
            ArmForceCapture();
    }

    private void ScheduleForceRetries(int generation)
    {
        if (Interlocked.Exchange(ref _retryScheduled, 1) == 1)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var delayMs in new[] { 50, 120, 250, 400 })
                {
                    await Task.Delay(delayMs).ConfigureAwait(false);
                    if (_disposed)
                        return;
                    if (Volatile.Read(ref _clientsConnected) != 1)
                        return;
                    if (generation != Volatile.Read(ref _forceGeneration))
                        return;
                    if (Volatile.Read(ref _forceNext) == 0)
                        return;

                    var engine = _engine;
                    var dq = App.DispatcherQueue;
                    if (engine is null || dq is null)
                        return;

                    dq.TryEnqueue(() =>
                    {
                        if (_disposed || generation != Volatile.Read(ref _forceGeneration))
                            return;
                        if (Volatile.Read(ref _clientsConnected) != 1)
                            return;
                        if (Volatile.Read(ref _forceNext) == 0)
                            return;
                        if (engine.IsPlaying)
                            return;
                        engine.RequestPausedFrameRefresh();
                    });
                }
            }
            finally
            {
                Interlocked.Exchange(ref _retryScheduled, 0);
            }
        });
    }

    private void OnFrame(CanvasRenderTarget target, int width, int height)
    {
        if (_disposed || width <= 0 || height <= 0)
            return;
        if (Volatile.Read(ref _clientsConnected) != 1)
            return;

        var now = Environment.TickCount64;
        if (now < Interlocked.Read(ref _pauseUntilTicks))
            return;

        var force = Volatile.Read(ref _forceNext) == 1;
        var playing = _engine?.IsPlaying == true;
        var minInterval = playing ? PlayingIntervalMs : IdleIntervalMs;
        if (!force && now - Interlocked.Read(ref _lastEncodeTicks) < minInterval)
            return;

        if (Interlocked.Exchange(ref _encoding, 1) == 1)
            return; // force は立てたまま → リトライで再取得

        byte[]? bgra = null;
        var outW = 0;
        var outH = 0;

        try
        {
            Interlocked.Exchange(ref _lastEncodeTicks, now);
            if (!TryDownsample(target, width, height, out bgra, out outW, out outH) || bgra is null)
            {
                Interlocked.Exchange(ref _encoding, 0);
                return;
            }

            // キャプチャ成功。以降のリトライは不要
            Interlocked.Exchange(ref _forceNext, 0);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RemotePreview] downsample: {ex.Message}");
            PerfMonitorService.Instance.LogEvent("WARN", $"RemotePreview downsample: {ex.Message}");
            Interlocked.Exchange(ref _encoding, 0);
            return;
        }

        var pixels = bgra;
        var w = outW;
        var h = outH;
        _ = Task.Run(() =>
        {
            try
            {
                var jpeg = EncodeJpegFromBgra(pixels, w, h);
                if (jpeg is { Length: > 0 })
                    _onJpeg(jpeg);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RemotePreview] jpeg: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _encoding, 0);
            }
        });
    }

    private bool TryDownsample(
        CanvasRenderTarget source,
        int width,
        int height,
        out byte[]? bgra,
        out int outW,
        out int outH)
    {
        outW = TargetWidth;
        outH = Math.Max(1, (int)Math.Round(height * (outW / (double)width)));
        bgra = null;

        lock (_gate)
        {
            if (_scaled is null || _scaledW != outW || _scaledH != outH)
            {
                try { _scaled?.Dispose(); } catch { /* ignore */ }
                try
                {
                    var device = CanvasDevice.GetSharedDevice();
                    if (device.IsDeviceLost())
                        device = new CanvasDevice();
                    _scaled = new CanvasRenderTarget(device, outW, outH, 96);
                }
                catch
                {
                    _scaled = new CanvasRenderTarget(new CanvasDevice(), outW, outH, 96);
                }

                _scaledW = outW;
                _scaledH = outH;
            }

            using (var ds = _scaled.CreateDrawingSession())
            {
                ds.Clear(Windows.UI.Color.FromArgb(255, 0, 0, 0));
                ds.DrawImage(
                    source,
                    new Rect(0, 0, outW, outH),
                    new Rect(0, 0, width, height));
            }

            bgra = _scaled.GetPixelBytes();
        }

        return bgra is { Length: > 0 };
    }

    private static byte[]? EncodeJpegFromBgra(byte[] bgra, int width, int height)
    {
        if (bgra.Length < width * height * 4)
            return null;

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            var copy = Math.Min(bgra.Length, Math.Abs(data.Stride) * height);
            Marshal.Copy(bgra, 0, data.Scan0, copy);
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        using var ms = new MemoryStream();
        var encoder = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
        if (encoder is null)
        {
            bmp.Save(ms, ImageFormat.Jpeg);
        }
        else
        {
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);
            bmp.Save(ms, encoder, ep);
        }

        return ms.ToArray();
    }
}
