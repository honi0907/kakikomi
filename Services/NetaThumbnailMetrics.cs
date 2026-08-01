namespace Kakikomi.Services;

/// <summary>ネタ一覧サムネイルの基準サイズとスケール。</summary>
public static class NetaThumbnailMetrics
{
    public const double BaseWidth = 84;
    public const double BaseHeight = 47;

    public static readonly double[] AllowedScales = [1.0, 1.2, 1.5];

    public static double NormalizeScale(double scale)
    {
        foreach (var allowed in AllowedScales)
        {
            if (Math.Abs(scale - allowed) < 0.01)
                return allowed;
        }

        return 1.0;
    }

    public static double Width(double scale) => BaseWidth * NormalizeScale(scale);

    public static double Height(double scale) => BaseHeight * NormalizeScale(scale);
}
