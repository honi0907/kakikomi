namespace Kakikomi.Updates;

public static class PackagedAppDetector
{
    /// <summary>
    /// 開発ビルド（bin/obj）では自己更新を適用しない。dist/ や Program Files などは許可。
    /// </summary>
    public static bool CanApplyOnlineUpdate()
    {
        var dir = AppContext.BaseDirectory;
        if (dir.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
            return false;
        if (dir.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}
