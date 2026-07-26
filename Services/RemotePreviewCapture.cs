using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;
using Windows.Foundation;
using Windows.Media.Playback;

namespace Kakikomi.Services;

/// <summary>
/// Operator 映像を縮小 JPEG 化して遠隔へ送る。
/// MediaFramePump には購読せず <see cref="VideoFrameRelay"/> から参照する（本番映像を優先）。
/// </summary>
internal sealed class RemotePreviewCapture : IDisposable
{
    private const int TargetWidth = 480;
    private const int IdleIntervalMs = 80;
    private const int PlayingIntervalMs = 200;
    private const int SwitchPauseMs = 450;
    private const int CircuitBreakerFailures = 5;
    private const int CircuitBreakerCooldownMs = 60_000;
    private const long JpegQuality = 35;

    private readonly Action<byte[]> _onJpeg;
    private readonly object _gate = new();
    private readonly CanvasDevice _previewDevice = new();

    private EngineSession? _engine;
    private DispatcherQueueTimer? _pollTimer;
    private MediaPlayer? _targetPlayer;
    private CanvasRenderTarget? _scaled;
    private int _scaledW;
    private int _scaledH;
    private int _encoding;
    private int _forceNext;
    private int _forceGeneration;
    private int _retryScheduled;
    private long _lastEncodeTicks;
    private long _pauseUntilTicks;
    private long _lastRelaySequence;
    private int _clientsConnected;
    private int _previewFailures;
    private long _circuitOpenUntil;
    private long _lastFailureLogMs;
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
        UpdateTargetPlayer(engine.VisibleSlotIndex);
    }

    public void SetClientsConnected(bool connected)
    {
        var was = Interlocked.Exchange(ref _clientsConnected, connected ? 1 : 0);
        if (connected)
        {
            EnsurePollTimer();
            ArmForceCapture();
        }
        else if (was == 1)
        {
            StopPollTimer();
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

        StopPollTimer();
        lock (_gate)
        {
            try { _scaled?.Dispose(); } catch { /* ignore */ }
            _scaled = null;
        }

        try { _previewDevice.Dispose(); } catch { /* ignore */ }
    }

    private void OnVisibleSlotChanged(int slot)
    {
        PauseCaptureBriefly();
        UpdateTargetPlayer(slot);
        if (Volatile.Read(ref _clientsConnected) == 1)
            ArmForceCapture();
    }

    private void OnSourceChanged()
    {
        PauseCaptureBriefly();
        var engine = _engine;
        if (engine is not null)
        {
            UpdateTargetPlayer(engine.VisibleSlotIndex);
            if (Volatile.Read(ref _clientsConnected) == 1)
                ArmForceCapture();
        }
    }

    private void OnKeyframeRequested()
    {
        if (Volatile.Read(ref _clientsConnected) != 1)
            return;
        ArmForceCapture();
    }

    private void UpdateTargetPlayer(int slot)
    {
        var engine = _engine;
        if (engine is null)
            return;

        try
        {
            _targetPlayer = engine.GetOperatorPlayerForSlot(slot);
        }
        catch
        {
            _targetPlayer = null;
        }
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

    private void EnsurePollTimer()
    {
        if (_pollTimer is not null)
            return;

        var dq = App.DispatcherQueue;
        if (dq is null)
            return;

        _pollTimer = dq.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromMilliseconds(IdleIntervalMs);
        _pollTimer.IsRepeating = true;
        _pollTimer.Tick += OnPollTimerTick;
        _pollTimer.Start();
    }

    private void StopPollTimer()
    {
        if (_pollTimer is null)
            return;

        _pollTimer.Stop();
        _pollTimer.Tick -= OnPollTimerTick;
        _pollTimer = null;
    }

    private void OnPollTimerTick(DispatcherQueueTimer sender, object args) => PollRelay();

    private void PollRelay()
    {
        if (_disposed || Volatile.Read(ref _clientsConnected) != 1)
            return;

        var now = Environment.TickCount64;
        if (now < Volatile.Read(ref _circuitOpenUntil))
            return;
        if (now < Interlocked.Read(ref _pauseUntilTicks))
            return;

        var player = _targetPlayer;
        if (player is null)
            return;

        if (!VideoFrameRelay.TryGetFrame(player, out var target, out var width, out var height, out var sequence))
            return;

        if (target is null || width <= 0 || height <= 0)
            return;

        var force = Volatile.Read(ref _forceNext) == 1;
        if (!force && sequence == Interlocked.Read(ref _lastRelaySequence))
            return;

        var playing = _engine?.IsPlaying == true;
        var minInterval = playing ? PlayingIntervalMs : IdleIntervalMs;
        if (!force && now - Interlocked.Read(ref _lastEncodeTicks) < minInterval)
            return;

        if (Interlocked.Exchange(ref _encoding, 1) == 1)
            return;

        Interlocked.Exchange(ref _lastRelaySequence, sequence);
        TryEncodeFrame(target, width, height, now);
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

    private void TryEncodeFrame(CanvasRenderTarget target, int width, int height, long now)
    {
        byte[]? bgra = null;
        var outW = 0;
        var outH = 0;

        try
        {
            Interlocked.Exchange(ref _lastEncodeTicks, now);
            if (!TryDownsample(target, width, height, out bgra, out outW, out outH) || bgra is null)
            {
                NotePreviewFailure("downsample empty");
                Interlocked.Exchange(ref _encoding, 0);
                return;
            }

            Interlocked.Exchange(ref _forceNext, 0);
            Interlocked.Exchange(ref _previewFailures, 0);
        }
        catch (Exception ex)
        {
            NotePreviewFailure(ex.Message);
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

    private void NotePreviewFailure(string detail)
    {
        var failures = Interlocked.Increment(ref _previewFailures);
        var now = Environment.TickCount64;
        if (now - Volatile.Read(ref _lastFailureLogMs) >= 5_000)
        {
            Volatile.Write(ref _lastFailureLogMs, now);
            PerfMonitorService.Instance.LogEvent("WARN", $"RemotePreview {detail} (failures={failures})");
        }

        if (failures < CircuitBreakerFailures)
            return;

        Interlocked.Exchange(ref _previewFailures, 0);
        Volatile.Write(ref _circuitOpenUntil, now + CircuitBreakerCooldownMs);
        PerfMonitorService.Instance.LogEvent(
            "WARN",
            $"RemotePreview circuit open {CircuitBreakerCooldownMs / 1000}s to protect production video");
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
                _scaled = new CanvasRenderTarget(_previewDevice, outW, outH, 96);
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
