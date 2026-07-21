using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Kakikomi.Controls;

/// <summary>
/// DEMO モード用。画面全体に斜め「DEMO」を敷き詰める（ヒット無効）。
/// </summary>
public sealed class DemoWatermarkOverlay : Canvas
{
    private static readonly SolidColorBrush DemoBrush =
        new(Color.FromArgb(72, 239, 68, 68));

    private const double CellWidth = 260;
    private const double CellHeight = 150;
    private const double FontSize = 56;
    private const double Angle = -28;

    public DemoWatermarkOverlay()
    {
        IsHitTestVisible = false;
        Background = null;
        SizeChanged += (_, _) => Rebuild();
        Loaded += (_, _) => Rebuild();
    }

    private void Rebuild()
    {
        Children.Clear();

        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 8 || height < 8 || double.IsNaN(width) || double.IsNaN(height))
            return;

        // 回転で端が空かないよう、外側にも余裕を持って敷く
        var startX = -CellWidth;
        var startY = -CellHeight;
        var endX = width + CellWidth;
        var endY = height + CellHeight;

        var row = 0;
        for (var y = startY; y < endY; y += CellHeight, row++)
        {
            var xOffset = (row % 2 == 0) ? 0 : CellWidth * 0.5;
            for (var x = startX + xOffset; x < endX; x += CellWidth)
            {
                var label = new TextBlock
                {
                    Text = "DEMO",
                    FontSize = FontSize,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = DemoBrush,
                    IsHitTestVisible = false,
                    Opacity = 0.55,
                    RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
                    RenderTransform = new RotateTransform { Angle = Angle },
                };

                SetLeft(label, x);
                SetTop(label, y);
                Children.Add(label);
            }
        }
    }
}
