using System.Threading;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Playback;
using Windows.UI;
using Kakikomi.Services;

namespace Kakikomi.Controls;

/// <summary>
/// MediaPlayer Frame Server 映像を Image に描画する。
/// Copy は <see cref="MediaFramePump"/> が1回だけ行い、ここは描画のみ。
/// 描画失敗時は ImageSource を破棄して次フレームで再生成する。
/// </summary>
public sealed class CompositionVideoHost : Grid
{
    private readonly Image _image;
    private readonly object _drawLock = new();

    private MediaPlayer? _player;
    private IDisposable? _subscription;
    private CanvasImageSource? _imageSource;
    private int _surfaceWidth;
    private int _surfaceHeight;
    private int _drawQueued;
    private CanvasRenderTarget? _pendingTarget;
    private int _pendingWidth;
    private int _pendingHeight;
    private int _drawFailures;

    public CompositionVideoHost()
    {
        Background = new SolidColorBrush(Color.FromArgb(255, 0, 0, 0));
        _image = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Children.Add(_image);

        CompositionVideoHostRegistry.Register(this);
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
        _player = player;

        if (_player is null)
            return;

        _player.IsVideoFrameServerEnabled = true;
        _subscription = MediaFramePump.Subscribe(_player, OnFrameCopied);
        _drawFailures = 0;
    }

    /// <summary>購読を強制張り直し（映像経路復帰用）。</summary>
    public void ForceRebind()
    {
        var player = _player;
        if (player is null)
            return;

        DetachSubscription();
        ResetImageSource();
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

    private void ResetImageSource()
    {
        lock (_drawLock)
        {
            _image.Source = null;
            _imageSource = null;
            _surfaceWidth = 0;
            _surfaceHeight = 0;
            _pendingTarget = null;
        }
    }

    private void OnFrameCopied(CanvasRenderTarget target, int width, int height)
    {
        lock (_drawLock)
        {
            _pendingTarget = target;
            _pendingWidth = width;
            _pendingHeight = height;
        }

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

        if (dq.HasThreadAccess)
        {
            Interlocked.Exchange(ref _drawQueued, 0);
            DrawPending();
            return;
        }

        if (!dq.TryEnqueue(() =>
            {
                Interlocked.Exchange(ref _drawQueued, 0);
                DrawPending();
            }))
        {
            Interlocked.Exchange(ref _drawQueued, 0);
        }
    }

    private void DrawPending()
    {
        if (Visibility != Visibility.Visible)
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
            EnsureImageSource(width, height);
            if (_imageSource is null)
                return;

            // Copy 済みターゲットをそのまま描く（再 Copy しない）
            using var session = _imageSource.CreateDrawingSession(Color.FromArgb(255, 0, 0, 0));
            session.DrawImage(target);
            _drawFailures = 0;
            VideoPipelineRecovery.NotifyFrameDelivered();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CompositionVideoHost] draw: {ex.Message}");
            _drawFailures++;
            ResetImageSource();

            if (_drawFailures >= 3)
            {
                _drawFailures = 0;
                VideoPipelineRecovery.NotifyDrawFailure(ex.Message);

                ForceRebind();
            }
        }
    }

    private void EnsureImageSource(int width, int height)
    {
        if (_imageSource is not null && _surfaceWidth == width && _surfaceHeight == height)
            return;

        _surfaceWidth = width;
        _surfaceHeight = height;

        try
        {
            var device = CanvasDevice.GetSharedDevice();
            if (device.IsDeviceLost())
                device = new CanvasDevice();
            _imageSource = new CanvasImageSource(device, width, height, 96);
            _image.Source = _imageSource;
        }
        catch (Exception ex)
        {
            try
            {
                var device = new CanvasDevice();
                _imageSource = new CanvasImageSource(device, width, height, 96);
                _image.Source = _imageSource;
                PerfMonitorService.Instance.LogEvent(
                    "WARN",
                    $"CompositionVideoHost ImageSource recreated: {ex.Message}");
            }
            catch (Exception ex2)
            {
                _imageSource = null;
                PerfMonitorService.Instance.LogEvent(
                    "WARN",
                    $"CompositionVideoHost ImageSource failed: {ex.Message} / {ex2.Message}");
            }
        }
    }
}
