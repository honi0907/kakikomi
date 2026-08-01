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
    private bool _skipVisibilityReset;

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

                // Relay は Pump の共有バッファ参照。購読解除後は破棄済みになり得るので必ずコピーする。
                using var session = _pendingTarget.CreateDrawingSession();
                session.Clear(Color.FromArgb(255, 0, 0, 0));
                session.DrawImage(target);
            }

            QueueDraw();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CompositionVideoHost] relay adopt: {ex.Message}");
            lock (_drawLock)
                ClearPendingFrame_NoLock();
        }
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

    /// <summary>クロスフェード用。Grid ではなく映像 Image だけの不透明度。</summary>
    internal void SetVideoOpacity(double opacity) => _image.Opacity = opacity;

    /// <summary>クロスフェード開始: 古い ImageSource を残したまま表示し、映像だけ透明にする。</summary>
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

    /// <summary>クロスフェード終了後に非表示へ。</summary>
    internal void FinishOutgoingCrossfade()
    {
        Opacity = 1;
        SetVideoOpacity(1);
        Visibility = Visibility.Collapsed;
        ResetImageSource();
    }

    /// <summary>即時切替後の状態を整える。</summary>
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

    internal void HideInstant()
    {
        FinishOutgoingInstant();
    }

    private void OnVisibilityChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (_skipVisibilityReset)
            return;

        if (Visibility == Visibility.Visible)
        {
            // 非表示中に溜まった古い ImageSource を捨て、最新の pending を描く。
            ResetImageSource();
            DrawPending();
            SetVideoOpacity(1);
            return;
        }

        // 非表示にしたら前ネタの静止画を残さない。
        ResetImageSource();
        SetVideoOpacity(1);
    }

    private void ResetImageSource()
    {
        lock (_drawLock)
        {
            _image.Source = null;
            DisposeImageSource_NoLock();
            _surfaceWidth = 0;
            _surfaceHeight = 0;
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

    private void DisposeImageSource_NoLock()
    {
        _imageSource = null;
    }

    private void OnFrameCopied(CanvasRenderTarget target, int width, int height)
    {
        try
        {
            lock (_drawLock)
            {
                if (!EnsurePendingTarget_NoLock(width, height) || _pendingTarget is null)
                    return;

                // MediaFramePump の共有バッファは次フレームで上書きされる。2倍速などで UI 描画が遅れると黒チラつきの原因になる。
                using var session = _pendingTarget.CreateDrawingSession();
                session.Clear(Color.FromArgb(255, 0, 0, 0));
                session.DrawImage(target);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CompositionVideoHost] frame copy: {ex.Message}");
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
            DisposeImageSource_NoLock();
            _imageSource = new CanvasImageSource(device, width, height, 96);
            _image.Source = _imageSource;
        }
        catch (Exception ex)
        {
            try
            {
                var device = new CanvasDevice();
                DisposeImageSource_NoLock();
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
