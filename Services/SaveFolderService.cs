using System.Diagnostics;

namespace Kakikomi.Services;

/// <summary>
/// 書き込み PNG の保存先。
/// ポータブルは exe 隣の save、インストール版（Program Files 等で書けない場合）は
/// %LocalAppData%\Kakikomi\save にフォールバックする。
/// </summary>
public static class SaveFolderService
{
    public const string FolderName = "save";
    private static string? _resolvedPath;

    public static string FolderPath => _resolvedPath ??= ResolveFolderPath();

    public static string EnsureExists()
    {
        var path = FolderPath;
        Directory.CreateDirectory(path);
        return path;
    }

    public static void OpenInExplorer()
    {
        var path = EnsureExists();
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = path,
            UseShellExecute = true
        });
    }

    private static string ResolveFolderPath()
    {
        var nextToExe = Path.Combine(AppContext.BaseDirectory, FolderName);
        if (CanWriteDirectory(AppContext.BaseDirectory))
            return nextToExe;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kakikomi",
            FolderName);
    }

    private static bool CanWriteDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".kakikomi-write-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
