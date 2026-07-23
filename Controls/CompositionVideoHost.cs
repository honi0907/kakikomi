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

        Unloaded += (_, _) => Detach();
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
    }

    private void Detach()
    {
        DetachSubscription();
        lock (_drawLock)
        {
            _image.Source = null;
            _imageSource = null;
            _surfaceWidth = 0;
            _surfaceHeight = 0;
            _pendingTarget = null;
        }

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
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CompositionVideoHost] draw: {ex.Message}");
        }
    }

    private void EnsureImageSource(int width, int height)
    {
        if (_imageSource is not null && _surfaceWidth == width && _surfaceHeight == height)
            return;

        _surfaceWidth = width;
        _surfaceHeight = height;

        var device = CanvasDevice.GetSharedDevice();
        _imageSource = new CanvasImageSource(device, width, height, 96);
        _image.Source = _imageSource;
    }
}
