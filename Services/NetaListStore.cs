using System.Text.Json;

namespace Kakikomi.Services;

/// <summary>
/// ネタ一覧のパス順を永続化（閉じて開いても同じ並び）。
/// </summary>
public static class NetaListStore
{
    private const string FileName = "neta-list.json";

    private static string StorePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kakikomi",
            FileName);

    public static void Save(IEnumerable<string> paths)
    {
        try
        {
            var list = paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();
            var dir = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(list);
            File.WriteAllText(StorePath, json);
        }
        catch
        {
            // 永続化失敗でも一覧操作は続行
        }
    }

    public static IReadOnlyList<string>? TryLoad()
    {
        try
        {
            if (!File.Exists(StorePath))
                return null;

            var json = File.ReadAllText(StorePath);
            var list = JsonSerializer.Deserialize<List<string>>(json);
            if (list is null || list.Count == 0)
                return null;

            return list
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();
        }
        catch
        {
            return null;
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(StorePath))
                File.Delete(StorePath);
        }
        catch
        {
            // ignore
        }
    }
}
