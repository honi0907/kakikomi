namespace Kakikomi.Updates;

public sealed record AppReleaseProfile(
    string GitHubRepo,
    string ReleaseTagPrefix,
    string AssetNamePattern,
    string DisplayName)
{
    public const string DefaultRepo = "honi0907/kakikomi";

    public static AppReleaseProfile Default { get; } = new(
        DefaultRepo,
        "v",
        "kakikomi",
        "Kakikomi");
}
