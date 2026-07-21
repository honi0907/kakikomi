namespace Kakikomi.Updates;

public sealed class AppUpdateCheckResult
{
    public bool Ok { get; init; }
    public bool Available { get; init; }
    public string? Error { get; init; }
    public string CurrentVersion { get; init; } = "0.0.0";
    public string? LatestVersion { get; init; }
    public string? ReleaseTag { get; init; }
    public string? ReleaseUrl { get; init; }
    public string? AssetName { get; init; }
    public string? DownloadUrl { get; init; }

    public static AppUpdateCheckResult Fail(string error) => new() { Ok = false, Error = error };

    public static AppUpdateCheckResult UpToDate(string current, string latest, string? releaseTag, string? releaseUrl) =>
        new()
        {
            Ok = true,
            Available = false,
            CurrentVersion = current,
            LatestVersion = latest,
            ReleaseTag = releaseTag,
            ReleaseUrl = releaseUrl,
        };

    public static AppUpdateCheckResult UpdateAvailable(
        string current,
        string latest,
        string releaseTag,
        string? releaseUrl,
        string assetName,
        string downloadUrl) =>
        new()
        {
            Ok = true,
            Available = true,
            CurrentVersion = current,
            LatestVersion = latest,
            ReleaseTag = releaseTag,
            ReleaseUrl = releaseUrl,
            AssetName = assetName,
            DownloadUrl = downloadUrl,
        };
}
