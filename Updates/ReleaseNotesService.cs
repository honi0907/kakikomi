namespace Kakikomi.Updates;

/// <summary>設定画面などに表示する GitHub Release の更新履歴。</summary>
public sealed class ReleaseNotesEntry
{
    public string Title { get; init; } = "";
    public string PublishedLabel { get; init; } = "";
    public string Body { get; init; } = "";
    public string? HtmlUrl { get; init; }
}

public static class ReleaseNotesService
{
    private static readonly GitHubReleaseClient Github = new();

    public static async Task<IReadOnlyList<ReleaseNotesEntry>> LoadAsync(
        AppReleaseProfile? profile = null,
        string? githubToken = null,
        CancellationToken cancellationToken = default)
    {
        profile ??= AppReleaseProfile.Default;
        var token = githubToken ?? Environment.GetEnvironmentVariable("KAKIKOMI_GITHUB_TOKEN");
        var releases = await Github.ListReleasesAsync(profile.GitHubRepo, token, cancellationToken);

        return releases
            .Where(r => !r.Draft)
            .Where(r =>
                string.IsNullOrWhiteSpace(r.TagName)
                || r.TagName.StartsWith(profile.ReleaseTagPrefix, StringComparison.OrdinalIgnoreCase)
                || (profile.ReleaseTagPrefix == "v"
                    && char.IsDigit(r.TagName![0])))
            .Select(ToEntry)
            .ToList();
    }

    private static ReleaseNotesEntry ToEntry(GitHubReleaseDto release)
    {
        var title = !string.IsNullOrWhiteSpace(release.Name)
            ? release.Name!.Trim()
            : (release.TagName ?? "Release");

        var published = release.PublishedAt is { } at
            ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "";

        var tag = release.TagName?.Trim();
        var publishedLabel = string.IsNullOrEmpty(tag)
            ? published
            : string.IsNullOrEmpty(published)
                ? tag
                : $"{tag}  ·  {published}";

        if (release.Prerelease && !string.IsNullOrEmpty(publishedLabel))
            publishedLabel += "  ·  Pre-release";

        var body = NormalizeBody(release.Body);
        if (string.IsNullOrWhiteSpace(body))
            body = "(更新内容の記載なし)";

        return new ReleaseNotesEntry
        {
            Title = title,
            PublishedLabel = publishedLabel,
            Body = body,
            HtmlUrl = release.HtmlUrl
        };
    }

    private static string NormalizeBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";

        return body
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }
}
