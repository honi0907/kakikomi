using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Kakikomi.Controls;

/// <summary>
/// 添付型の混色パレット。列=色相、行=白混ぜ（明るさ↑・彩度↓）、白バー/スライダーで微調整。
/// </summary>
public sealed class MixColorPalette : UserControl
{
    private const int ColumnCount = 11;
    private const int RowCount = 6;
    private const double MaxGridWhiteMix = 0.92;
    private const double SwatchHeight = 16;
    private const double SwatchMinWidth = 14;
    private const double GridGap = 1;

    // 最下段=各列の純色（白混ぜ0%）。マーカー向けに彩度を最大化。
    private static readonly Color[] BaseColors =
    [
        Color.FromArgb(255, 255, 255, 0),   // 黄
        Color.FromArgb(255, 255, 128, 0),   // 橙
        Color.FromArgb(255, 128, 255, 0),   // 黄緑
        Color.FromArgb(255, 0, 255, 255),   // シアン
        Color.FromArgb(255, 0, 128, 255),   // 青
        Color.FromArgb(255, 128, 128, 255), // 藤
        Color.FromArgb(255, 128, 0, 255),   // 紫
        Color.FromArgb(255, 255, 0, 255),   // マゼンタ
        Color.FromArgb(255, 255, 0, 0),     // 赤（#FF0000）
        Color.FromArgb(255, 128, 64, 0),    // 茶
        Color.FromArgb(255, 0, 0, 0),       // 黒（グレー段）
    ];

    private readonly Grid _root = new();
    private readonly Grid _swatchGrid = new();
    private readonly Slider _whiteMixSlider = new();
    private readonly Border _whiteMixPreview = new();
    private readonly Border _whiteBar = new();
    private readonly Border[,] _swatches = new Border[RowCount, ColumnCount];
    private readonly List<Border> _swatchBorders = [];

    private bool _suppressEvents;
    private int _selectedColumn;
    private int _selectedRow = RowCount - 1;
    private double _whiteMix;

    public event EventHandler<Color>? ColorChanged;

    public MixColorPalette()
    {
        BuildUi();
        SelectColumnRow(8, RowCount - 1, 0, raiseEvent: false);
    }

    public void SetColor(Color color, bool raiseEvent = false)
    {
        var (column, mix) = FindBestMatch(color);
        var row = MixToRow(mix);
        SelectColumnRow(column, row, mix, raiseEvent);
    }

    public Color GetColor() => MixWithWhite(BaseColors[_selectedColumn], _whiteMix);

    private void BuildUi()
    {
        _root.RowSpacing = 4;
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _whiteBar.Height = 20;
        _whiteBar.Background = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
        _whiteBar.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184));
        _whiteBar.BorderThickness = new Thickness(1);
        _whiteBar.CornerRadius = new CornerRadius(0);
        _whiteBar.Child = new TextBlock
        {
            Text = "ホワイト",
            Foreground = new SolidColorBrush(Color.FromArgb(255, 15, 23, 42)),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(_whiteBar, "白100%（明るさ最大・彩度ゼロ）");
        _whiteBar.PointerPressed += (_, _) => SelectWhite(raiseEvent: true);
        Grid.SetRow(_whiteBar, 0);
        _root.Children.Add(_whiteBar);

        var hint = new TextBlock
        {
            Text = "白を混ぜる（明るさ↑・彩度↓）",
            Foreground = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184)),
            FontSize = 10,
        };
        Grid.SetRow(hint, 1);
        _root.Children.Add(hint);

        _whiteMixPreview.Height = 8;
        _whiteMixPreview.CornerRadius = new CornerRadius(0);
        _whiteMixPreview.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184));
        _whiteMixPreview.BorderThickness = new Thickness(1);
        Grid.SetRow(_whiteMixPreview, 2);
        _root.Children.Add(_whiteMixPreview);

        _whiteMixSlider.Minimum = 0;
        _whiteMixSlider.Maximum = 100;
        _whiteMixSlider.ValueChanged += OnWhiteMixSliderChanged;
        Grid.SetRow(_whiteMixSlider, 3);
        _root.Children.Add(_whiteMixSlider);

        _swatchGrid.Margin = new Thickness(0, 2, 0, 0);
        _swatchGrid.ColumnSpacing = GridGap;
        _swatchGrid.RowSpacing = GridGap;
        for (var c = 0; c < ColumnCount; c++)
            _swatchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < RowCount; r++)
            _swatchGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        for (var row = 0; row < RowCount; row++)
        {
            for (var col = 0; col < ColumnCount; col++)
            {
                var swatch = new Border
                {
                    Height = SwatchHeight,
                    MinWidth = SwatchMinWidth,
                    Background = new SolidColorBrush(GetGridColor(col, row)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(255, 51, 65, 85)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(0),
                    Tag = (col, row),
                };
                swatch.PointerPressed += OnSwatchPressed;
                _swatches[row, col] = swatch;
                _swatchBorders.Add(swatch);
                Grid.SetRow(swatch, row);
                Grid.SetColumn(swatch, col);
                _swatchGrid.Children.Add(swatch);
            }
        }

        var paletteHost = new StackPanel { Spacing = 0 };
        paletteHost.Children.Add(_swatchGrid);
        _root.Children.Add(paletteHost);
        Grid.SetRow(paletteHost, 4);
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Content = _root;
    }

    private void OnSwatchPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border swatch || swatch.Tag is not ValueTuple<int, int> tag)
            return;

        var mix = RowToMix(tag.Item2);
        SelectColumnRow(tag.Item1, tag.Item2, mix, raiseEvent: true);
    }

    private void OnWhiteMixSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressEvents || double.IsNaN(e.NewValue))
            return;

        ApplyWhiteMix(e.NewValue / 100.0, raiseEvent: true, syncRowFromMix: true);
    }

    private void SelectWhite(bool raiseEvent)
    {
        SelectColumnRow(_selectedColumn, 0, 1.0, raiseEvent);
    }

    private void SelectColumnRow(int column, int row, double whiteMix, bool raiseEvent)
    {
        _selectedColumn = Math.Clamp(column, 0, ColumnCount - 1);
        _selectedRow = Math.Clamp(row, 0, RowCount - 1);
        _whiteMix = Math.Clamp(whiteMix, 0, 1);

        _suppressEvents = true;
        try
        {
            _whiteMixSlider.Value = Math.Round(_whiteMix * 100);
            UpdateWhiteMixPreview();
            UpdateSelectionChrome();
        }
        finally
        {
            _suppressEvents = false;
        }

        if (raiseEvent)
            ColorChanged?.Invoke(this, GetColor());
    }

    private void ApplyWhiteMix(double mix, bool raiseEvent, bool syncRowFromMix)
    {
        _whiteMix = Math.Clamp(mix, 0, 1);
        if (syncRowFromMix)
            _selectedRow = MixToRow(_whiteMix);

        UpdateWhiteMixPreview();
        UpdateSelectionChrome();

        if (raiseEvent)
            ColorChanged?.Invoke(this, GetColor());
    }

    private void UpdateWhiteMixPreview()
    {
        var baseColor = BaseColors[_selectedColumn];
        var mixed = MixWithWhite(baseColor, _whiteMix);
        _whiteMixPreview.Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            GradientStops =
            {
                new GradientStop { Offset = 0, Color = baseColor },
                new GradientStop { Offset = 1, Color = Color.FromArgb(255, 255, 255, 255) },
            },
        };
        ToolTipService.SetToolTip(
            _whiteMixSlider,
            $"列の元色 → 白  ({Math.Round(_whiteMix * 100)}%)");
    }

    private void UpdateSelectionChrome()
    {
        foreach (var swatch in _swatchBorders)
        {
            if (swatch.Tag is not ValueTuple<int, int> tag)
                continue;

            var selected = tag.Item1 == _selectedColumn && tag.Item2 == _selectedRow;
            swatch.BorderBrush = new SolidColorBrush(
                selected ? Color.FromArgb(255, 248, 250, 252) : Color.FromArgb(255, 51, 65, 85));
            swatch.BorderThickness = new Thickness(selected ? 2 : 1);
        }
    }

    private static Color GetGridColor(int column, int row) =>
        MixWithWhite(BaseColors[column], RowToMix(row));

    private static double RowToMix(int row) =>
        (RowCount - 1 - row) / (double)(RowCount - 1) * MaxGridWhiteMix;

    private static int MixToRow(double mix)
    {
        if (mix >= 0.999)
            return 0;
        var normalized = mix / MaxGridWhiteMix;
        var row = RowCount - 1 - (int)Math.Round(normalized * (RowCount - 1));
        return Math.Clamp(row, 0, RowCount - 1);
    }

    private static Color MixWithWhite(Color baseColor, double whiteAmount)
    {
        whiteAmount = Math.Clamp(whiteAmount, 0, 1);
        byte Lerp(byte channel) =>
            (byte)Math.Clamp(Math.Round(channel + (255 - channel) * whiteAmount), 0, 255);

        return Color.FromArgb(255, Lerp(baseColor.R), Lerp(baseColor.G), Lerp(baseColor.B));
    }

    private static (int Column, double Mix) FindBestMatch(Color color)
    {
        if (color.R > 250 && color.G > 250 && color.B > 250)
            return (0, 1.0);

        var bestColumn = 0;
        var bestMix = 0.0;
        var bestDistance = double.MaxValue;

        for (var column = 0; column < ColumnCount; column++)
        {
            for (var step = 0; step <= 40; step++)
            {
                var mix = step / 40.0;
                var candidate = MixWithWhite(BaseColors[column], mix);
                var distance = ColorDistance(candidate, color);
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                bestColumn = column;
                bestMix = mix;
            }
        }

        return (bestColumn, bestMix);
    }

    private static double ColorDistance(Color a, Color b)
    {
        var dr = a.R - b.R;
        var dg = a.G - b.G;
        var db = a.B - b.B;
        return dr * dr + dg * dg + db * db;
    }
}
