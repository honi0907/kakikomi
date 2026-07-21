using Windows.Foundation;
using Windows.UI;

namespace Kakikomi.Models;

public sealed class InkStrokeData
{
    public required IReadOnlyList<Point> Points { get; init; }
    public required Color Color { get; init; }
    public required double Thickness { get; init; }
}
