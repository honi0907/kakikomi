using System.Threading;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Media.Playback;
using Windows.UI;
using Kakikomi.Services;

namespace Kakikomi.Controls;

/// <summary>
/// MediaPlayer Frame Server 映像を Image に描画する。
/// Pump の共有バッファはホスト専用へ再コピーし、単一の ImageSource へ上書き描画する。
/// </summary>
public sealed class CompositionVideoHost : Grid
{
    private readonly Image _image;
    private readonly object _drawLock = new();

    private MediaPlayer? _player;
    private IDisposable? _subscription;
    private CanvasImageSource? _page;
    private int _surfaceWidth;
    private int _surfaceHeight;
    private int _drawQueued;
    private int _pendingRevision;
    private CanvasRenderTarget? _pendingTarget;
    private int _pendingWidth;
    private int _pendingHeight;
    private int _drawFailures;
    private bool _skipVisibilityReset;
    private long _lastPresentMs;

    public CompositionVideoHost()
    {
        Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        _image = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Children.Add(_image);

        CompositionVideoHostRegistry.Register(this);
        RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);
        Unloaded += (_, _) =>
        {
            CompositionVideoHostRegistry.Unregister(this);
            Detach();
        };
    }

    public void Attach(MediaPlayer? player)
    {
        if (ReferenceEquals(_player, player) && _subscription is not null)
            return;

        DetachSubscription();
        ClearPendingFrame_NoLock();
        ResetImageSource();
        _player = player;

        if (_player is null)
            return;

        _player.IsVideoFrameServerEnabled = true;
        _subscription = MediaFramePump.Subscribe(_player, OnFrameCopied);
        _drawFailures = 0;
        TryAdoptRelayFrame();
    }

    private void TryAdoptRelayFrame()
    {
        if (_player is null)
            return;

        if (!VideoFrameRelay.TryGetFrame(_player, out var target, out var width, out var height, out _))
            return;

        if (target is null || width <= 0 || height <= 0)
            return;

        try
        {
            lock (_drawLock)
            {
                if (!EnsurePendingTarget_NoLock(width, height) || _pendingTarget is null)
                    return;

                using var session = _pendingTarget.CreateDrawingSession();
                session.DrawImage(target);
            }

            Interlocked.Increment(ref _pendingRevision);
            QueueDraw();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CompositionVideoHost] relay adopt: {ex.Message}");
            lock (_drawLock)
                ClearPendingFrame_NoLock();
        }
    }

    /// <summary>購読を強制張り直し（映像経路復帰用）。表示中フレームは維持する。</summary>
    public void ForceRebind()
    {
        var player = _player;
        if (player is null)
            return;

        DetachSubscription();
        player.IsVideoFrameServerEnabled = true;
        _subscription = MediaFramePump.Subscribe(player, OnFrameCopied);
        _drawFailures = 0;
    }

    private void Detach()
    {
        DetachSubscription();
        ResetImageSource();
        _player = null;
    }

    private void DetachSubscription()
    {
        try
        {
            _subscription?.Dispose();
        }
        catch
        {
            // ignore
        }

        _subscription = null;
    }

    internal void SetVideoOpacity(double opacity) => _image.Opacity = opacity;

    internal void BeginIncomingCrossfade()
    {
        _skipVisibilityReset = true;
        try
        {
            Opacity = 1;
            Visibility = Visibility.Visible;
            SetVideoOpacity(0);
            DrawPending();
        }
        finally
        {
            _skipVisibilityReset = false;
        }
    }

    internal void FinishOutgoingCrossfade()
    {
        Opacity = 1;
        SetVideoOpacity(1);
        Visibility = Visibility.Collapsed;
        ResetImageSource();
    }

    internal void FinishIncomingInstant()
    {
        Opacity = 1;
        SetVideoOpacity(1);
        Visibility = Visibility.Visible;
    }

    internal void FinishOutgoingInstant()
    {
        Opacity = 1;
        SetVideoOpacity(1);
        Visibility = Visibility.Collapsed;
        ResetImageSource();
    }

    internal void ShowInstant()
    {
        _skipVisibilityReset = true;
        try
        {
            Opacity = 1;
            Visibility = Visibility.Visible;
            ResetImageSource();
            DrawPending();
            SetVideoOpacity(1);
        }
        finally
        {
            _skipVisibilityReset = false;
        }
    }

    internal void HideInstant() => FinishOutgoingInstant();

    private void OnVisibilityChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (_skipVisibilityReset)
            return;

        if (Visibility == Visibility.Visible)
        {
            DrawPending();
            SetVideoOpacity(1);
            return;
        }

        ResetImageSource();
        SetVideoOpacity(1);
    }

    private void ResetImageSource()
    {
        lock (_drawLock)
        {
            _image.Source = null;
            _page = null;
            _surfaceWidth = 0;
            _surfaceHeight = 0;
            _lastPresentMs = 0;
        }
    }

    private void ClearPendingFrame_NoLock()
    {
        try
        {
            _pendingTarget?.Dispose();
        }
        catch
        {
            // ignore
        }

        _pendingTarget = null;
        _pendingWidth = 0;
        _pendingHeight = 0;
    }

    private bool EnsurePendingTarget_NoLock(int width, int height)
    {
        if (_pendingTarget is not null && _pendingWidth == width && _pendingHeight == height)
            return true;

        try
        {
            _pendingTarget?.Dispose();
            _pendingTarget = null;
            _pendingWidth = 0;
            _pendingHeight = 0;

            var device = CanvasDevice.GetSharedDevice();
            if (device.IsDeviceLost())
                device = new CanvasDevice();

            _pendingTarget = new CanvasRenderTarget(device, width, height, 96);
            _pendingWidth = width;
            _pendingHeight = height;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CompositionVideoHost] pending: {ex.Message}");
            try
            {
                _pendingTarget?.Dispose();
                _pendingTarget = new CanvasRenderTarget(new CanvasDevice(), width, height, 96);
                _pendingWidth = width;
                _pendingHeight = height;
                return true;
            }
            catch
            {
                _pendingTarget = null;
                _pendingWidth = 0;
                _pendingHeight = 0;
                return false;
            }
        }
    }

    private void OnFrameCopied(CanvasRenderTarget target, int width, int height)
    {
        try
        {
            lock (_drawLock)
            {
                if (!EnsurePendingTarget_NoLock(width, height) || _pendingTarget is null)
                    return;

                using var session = _pendingTarget.CreateDrawingSession();
                session.DrawImage(target);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CompositionVideoHost] frame copy: {ex.Message}");
        }

        Interlocked.Increment(ref _pendingRevision);
        QueueDraw();
    }

    private void QueueDraw()
    {
        if (Interlocked.Exchange(ref _drawQueued, 1) == 1)
            return;

        var dq = DispatcherQueue;
        if (dq is null)
        {
            Interlocked.Exchange(ref _drawQueued, 0);
            return;
        }

        if (!dq.TryEnqueue(DrawPendingLoop))
            Interlocked.Exchange(ref _drawQueued, 0);
    }

    private void DrawPendingLoop()
    {
        var lastDrawn = -1;
        try
        {
            while (true)
            {
                var current = Volatile.Read(ref _pendingRevision);
                if (current == lastDrawn)
                    break;

                DrawPending();
                lastDrawn = current;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CompositionVideoHost] draw loop: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _drawQueued, 0);
            if (Volatile.Read(ref _pendingRevision) != lastDrawn)
                QueueDraw();
        }
    }

    private void DrawPending()
    {
        if (Visibility != Visibility.Visible)
            return;

        if (ShouldThrottlePresent())
            return;

        CanvasRenderTarget? target;
        int width;
        int height;

        lock (_drawLock)
        {
            target = _pendingTarget;
            width = _pendingWidth;
            height = _pendingHeight;
        }

        if (target is null || width <= 0 || height <= 0)
            return;

        try
        {
            var page = EnsurePage(width, height);
            if (page is null)
                return;

            var dest = new Rect(0, 0, width, height);
            using (var session = page.CreateDrawingSession(Color.FromArgb(255, 0, 0, 0)))
                session.DrawImage(target, dest, dest);

            _lastPresentMs = Environment.TickCount64;
            _drawFailures = 0;
            VideoPipelineRecovery.NotifyFrameDelivered();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CompositionVideoHost] draw: {ex.Message}");
            _drawFailures++;

            if (_drawFailures >= 3)
            {
                _drawFailures = 0;
                VideoPipelineRecovery.NotifyDrawFailure(ex.Message);
                ForceRebind();
            }
        }
    }

    private bool ShouldThrottlePresent()
    {
        if ((App.Engine?.ClockRate ?? 1.0) <= 1.5)
            return false;

        var intervalMs = AppSettings.GetFastPlaybackPresentIntervalMs();
        if (intervalMs <= 0)
            return false;

        return Environment.TickCount64 - _lastPresentMs < intervalMs;
    }

    private CanvasImageSource? EnsurePage(int width, int height)
    {
        if (_page is not null && _surfaceWidth == width && _surfaceHeight == height)
            return _page;

        _surfaceWidth = width;
        _surfaceHeight = height;

        try
        {
            var device = CanvasDevice.GetSharedDevice();
            if (device.IsDeviceLost())
                device = new CanvasDevice();

            _page = new CanvasImageSource(device, width, height, 96);
            _image.Source = _page;
            return _page;
        }
        catch (Exception ex)
        {
            try
            {
                var device = new CanvasDevice();
                _page = new CanvasImageSource(device, width, height, 96);
                _image.Source = _page;
                PerfMonitorService.Instance.LogEvent(
                    "WARN",
                    $"CompositionVideoHost page recreated: {ex.Message}");
                return _page;
            }
            catch (Exception ex2)
            {
                _page = null;
                PerfMonitorService.Instance.LogEvent(
                    "WARN",
                    $"CompositionVideoHost page failed: {ex.Message} / {ex2.Message}");
                return null;
            }
        }
    }
}
