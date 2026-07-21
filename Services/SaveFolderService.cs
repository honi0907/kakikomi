using System.Diagnostics;

namespace Kakikomi.Services;

/// <summary>
/// 書き込み PNG の保存先（exe 隣の save フォルダ）。
/// </summary>
public static class SaveFolderService
{
    public const string FolderName = "save";

    public static string FolderPath =>
        Path.Combine(AppContext.BaseDirectory, FolderName);

    public static string EnsureExists()
    {
        Directory.CreateDirectory(FolderPath);
        return FolderPath;
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
}
