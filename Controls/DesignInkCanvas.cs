using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Kakikomi.Models;
using Kakikomi.Services;

namespace Kakikomi.Controls;

public sealed class DesignInkCanvas : Canvas
{
    private EngineSession? _session;
    private bool _inputEnabled;
    private Color _color = Color.FromArgb(255, 255, 48, 48);
    private double _thickness = 6;
    private bool _isEraser;
    private bool _drawing;
    private uint? _activePointerId;

    public DesignInkCanvas()
    {
        Width = EngineSession.DesignWidth;
        Height = EngineSession.DesignHeight;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        // ピンチ等のマニピュレーションを起こさない
        ManipulationMode = ManipulationModes.None;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerReleased;
        PointerCaptureLost += OnPointerReleased;
    }

    public void Attach(EngineSession session, bool inputEnabled)
    {
        if (_session is not null)
            _session.StrokesChanged -= Redraw;

        _session = session;
        _inputEnabled = inputEnabled;
        _session.StrokesChanged += Redraw;
        Redraw();
    }

    public void SetPen(Color color, double thickness, bool isEraser = false)
    {
        _color = color;
        _thickness = thickness;
        _isEraser = isEraser;
    }

    private void Redraw()
    {
        if (_session is null)
            return;

        Children.Clear();
        foreach (var stroke in _session.Strokes)
            Children.Add(ToPolyline(stroke));

        if (_session.ActiveStroke is not null)
            Children.Add(ToPolyline(_session.ActiveStroke));
    }

    private static Polyline ToPolyline(InkStrokeData stroke)
    {
        var line = new Polyline
        {
            Stroke = new SolidColorBrush(stroke.Color),
            StrokeThickness = stroke.Thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };

        foreach (var point in stroke.Points)
            line.Points.Add(point);

        return line;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 書き込みは停止中のみ。描画中の追加指は無視（シングルタッチ）
        if (!_inputEnabled || _session is null || _session.IsPlaying)
            return;
        if (_drawing || _activePointerId is not null)
            return;

        _drawing = true;
        _activePointerId = e.Pointer.PointerId;
        CapturePointer(e.Pointer);
        var point = Clamp(e.GetCurrentPoint(this).Position);
        if (_isEraser)
            _session.EraseNear(point, _thickness);
        else
            _session.BeginStroke(_color, _thickness, point);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_drawing || _session is null || _session.IsPlaying)
            return;
        if (_activePointerId != e.Pointer.PointerId)
            return;

        var point = Clamp(e.GetCurrentPoint(this).Position);
        if (_isEraser)
            _session.EraseNear(point, _thickness);
        else
            _session.AppendStrokePoint(point);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_drawing || _session is null)
            return;
        if (_activePointerId != e.Pointer.PointerId)
            return;

        _drawing = false;
        _activePointerId = null;
        ReleasePointerCapture(e.Pointer);
        if (!_isEraser)
            _session.EndStroke();
        e.Handled = true;
    }

    private static Point Clamp(Point point)
    {
        var x = Math.Clamp(point.X, 0, EngineSession.DesignWidth);
        var y = Math.Clamp(point.Y, 0, EngineSession.DesignHeight);
        return new Point(x, y);
    }
}
