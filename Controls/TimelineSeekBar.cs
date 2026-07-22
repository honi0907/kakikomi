using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Kakikomi.Controls;

/// <summary>
/// 指・ペン操作向けの自前シークバー。位置はポインタ X から直接計算する。
/// </summary>
public sealed class TimelineSeekBar : UserControl
{
    private const double ThumbSize = 28;
    private const double TrackHeight = 10;
    private const double HorizontalPad = 16;

    private readonly Border _hitArea = new();
    private readonly Grid _visuals = new();
    private readonly Rectangle _track = new();
    private readonly Rectangle _fill = new();
    private readonly Ellipse _thumb = new();
    private bool _dragging;
    private uint? _activePointerId;

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(
            nameof(Maximum),
            typeof(double),
            typeof(TimelineSeekBar),
            new PropertyMetadata(1.0, OnRangeChanged));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(double),
            typeof(TimelineSeekBar),
            new PropertyMetadata(0.0, OnRangeChanged));

    public event EventHandler<double>? SeekStarted;
    public event EventHandler<double>? SeekPreview;
    public event EventHandler<double>? SeekCompleted;

    public TimelineSeekBar()
    {
        Height = 48;
        MinHeight = 48;
        IsTabStop = true;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        // ManipulationMode は Pointer キャプチャと衝突するので付けない
        ManipulationMode = ManipulationModes.None;

        _track.Height = TrackHeight;
        _track.RadiusX = TrackHeight / 2;
        _track.RadiusY = TrackHeight / 2;
        _track.Fill = new SolidColorBrush(Color.FromArgb(255, 30, 41, 59));
        _track.Stroke = new SolidColorBrush(Color.FromArgb(255, 71, 85, 105));
        _track.StrokeThickness = 1;
        _track.VerticalAlignment = VerticalAlignment.Center;
        _track.HorizontalAlignment = HorizontalAlignment.Stretch;
        _track.Margin = new Thickness(HorizontalPad, 0, HorizontalPad, 0);
        _track.IsHitTestVisible = false;

        _fill.Height = TrackHeight;
        _fill.RadiusX = TrackHeight / 2;
        _fill.RadiusY = TrackHeight / 2;
        _fill.Fill = new SolidColorBrush(Color.FromArgb(255, 56, 189, 248));
        _fill.VerticalAlignment = VerticalAlignment.Center;
        _fill.HorizontalAlignment = HorizontalAlignment.Left;
        _fill.Margin = new Thickness(HorizontalPad, 0, 0, 0);
        _fill.Width = 0;
        _fill.IsHitTestVisible = false;

        _thumb.Width = ThumbSize;
        _thumb.Height = ThumbSize;
        _thumb.Fill = new SolidColorBrush(Color.FromArgb(255, 248, 250, 252));
        _thumb.Stroke = new SolidColorBrush(Color.FromArgb(255, 14, 165, 233));
        _thumb.StrokeThickness = 3;
        _thumb.VerticalAlignment = VerticalAlignment.Center;
        _thumb.HorizontalAlignment = HorizontalAlignment.Left;
        _thumb.RenderTransformOrigin = new Point(0.5, 0.5);
        _thumb.RenderTransform = new TranslateTransform();
        _thumb.IsHitTestVisible = false;

        _visuals.IsHitTestVisible = false;
        _visuals.Children.Add(_track);
        _visuals.Children.Add(_fill);
        _visuals.Children.Add(_thumb);

        // ヒット用の全面 Border（Transparent はヒットする / null はしない）
        // null だとヒットしない。A=0 でも Background 設定済みならヒットする想定だが、念のため A=1
        _hitArea.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        _hitArea.Child = _visuals;
        Content = _hitArea;

        _hitArea.PointerPressed += OnPointerPressed;
        _hitArea.PointerMoved += OnPointerMoved;
        _hitArea.PointerReleased += OnPointerReleased;
        _hitArea.PointerCanceled += OnPointerCanceled;
        _hitArea.PointerCaptureLost += OnPointerCaptureLost;
        SizeChanged += (_, _) => UpdateVisuals();
        IsEnabledChanged += (_, _) => UpdateVisuals();
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public void SetRange(double value, double maximum)
    {
        Maximum = maximum > 0 ? maximum : 1;
        Value = Clamp(value, 0, Maximum);
        UpdateVisuals();
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimelineSeekBar bar)
            bar.UpdateVisuals();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsEnabled || Maximum <= 0 || _dragging)
            return;

        _dragging = true;
        _activePointerId = e.Pointer.PointerId;
        SetThumbHot(true);
        _hitArea.CapturePointer(e.Pointer);

        var seconds = SecondsFromPoint(e.GetCurrentPoint(_hitArea).Position);
        SeekStarted?.Invoke(this, seconds);
        ApplyPointerSeek(seconds, completed: false);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || _activePointerId != e.Pointer.PointerId)
            return;

        var seconds = SecondsFromPoint(e.GetCurrentPoint(_hitArea).Position);
        ApplyPointerSeek(seconds, completed: false);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || _activePointerId != e.Pointer.PointerId)
            return;

        FinishDrag(e.GetCurrentPoint(_hitArea).Position, releaseCapture: true, pointer: e.Pointer);
        e.Handled = true;
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || _activePointerId != e.Pointer.PointerId)
            return;

        FinishDrag(e.GetCurrentPoint(_hitArea).Position, releaseCapture: true, pointer: e.Pointer);
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || _activePointerId != e.Pointer.PointerId)
            return;

        // キャプチャ喪失時は最後の Value で確定（座標は取れないことがある）
        FinishDrag(null, releaseCapture: false, pointer: null);
    }

    private void FinishDrag(Point? point, bool releaseCapture, Microsoft.UI.Xaml.Input.Pointer? pointer)
    {
        if (!_dragging)
            return;

        var seconds = point.HasValue ? SecondsFromPoint(point.Value) : Value;
        _dragging = false;
        _activePointerId = null;
        SetThumbHot(false);

        if (releaseCapture && pointer is not null)
        {
            try
            {
                _hitArea.ReleasePointerCapture(pointer);
            }
            catch
            {
                // already lost
            }
        }

        ApplyPointerSeek(seconds, completed: true);
    }

    private void ApplyPointerSeek(double seconds, bool completed)
    {
        Value = seconds;
        UpdateVisuals();

        if (completed)
            SeekCompleted?.Invoke(this, seconds);
        else
            SeekPreview?.Invoke(this, seconds);
    }

    private double SecondsFromPoint(Point point)
    {
        var trackWidth = Math.Max(ActualWidth - HorizontalPad * 2, 1);
        var x = Clamp(point.X - HorizontalPad, 0, trackWidth);
        var ratio = x / trackWidth;
        return Math.Round(ratio * Maximum, 2);
    }

    private void UpdateVisuals()
    {
        var trackWidth = Math.Max(ActualWidth - HorizontalPad * 2, 0);
        var max = Maximum > 0 ? Maximum : 1;
        var ratio = Clamp(Value / max, 0, 1);
        var x = trackWidth * ratio;

        _fill.Width = x;

        if (_thumb.RenderTransform is TranslateTransform tx)
            tx.X = HorizontalPad + x - ThumbSize / 2;

        var dimmed = !IsEnabled;
        _track.Opacity = dimmed ? 0.45 : 1;
        _fill.Opacity = dimmed ? 0.45 : 1;
        _thumb.Opacity = dimmed ? 0.45 : 1;
    }

    private void SetThumbHot(bool hot)
    {
        var scale = hot ? 1.15 : 1.0;
        _thumb.Width = ThumbSize * scale;
        _thumb.Height = ThumbSize * scale;
        _thumb.StrokeThickness = hot ? 4 : 3;
    }

    private static double Clamp(double value, double min, double max)
    {
        if (value < min)
            return min;
        if (value > max)
            return max;
        return value;
    }
}
