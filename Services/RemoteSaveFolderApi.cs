namespace Kakikomi.Services;

/// <summary>遠隔 HTTP から save フォルダの PNG 一覧・配信。</summary>
internal static class RemoteSaveFolderApi
{
    private const int MaxEntries = 200;

    public static object BuildListResponse()
    {
        try
        {
            var dir = SaveFolderService.EnsureExists();
            var files = Directory.EnumerateFiles(dir, "*.png", SearchOption.TopDirectoryOnly)
                .Select(static path => new FileInfo(path))
                .OrderByDescending(static f => f.LastWriteTimeUtc)
                .Take(MaxEntries)
                .Select(static f => new SaveFileDto(
                    f.Name,
                    f.Length,
                    f.LastWriteTimeUtc.ToString("o")))
                .ToList();

            return new { ok = true, folderPath = dir, files };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    public static bool TryResolveFile(string fileName, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(fileName) ||
            !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            return false;
        }

        if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return false;

        var dir = Path.GetFullPath(SaveFolderService.EnsureExists());
        var dirPrefix = dir.EndsWith(Path.DirectorySeparatorChar)
            ? dir
            : dir + Path.DirectorySeparatorChar;

        fullPath = Path.GetFullPath(Path.Combine(dir, fileName));
        if (!fullPath.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        return File.Exists(fullPath);
    }

    private sealed record SaveFileDto(string Name, long SizeBytes, string ModifiedUtc);
}
