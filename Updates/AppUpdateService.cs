using System.ComponentModel;
using System.Diagnostics;

namespace Kakikomi.Updates;

public sealed class AppUpdateService
{
    private readonly GitHubReleaseClient _github = new();

    public async Task<AppUpdateCheckResult> CheckForUpdateAsync(
        AppReleaseProfile profile,
        string currentVersion,
        string? releaseTag = null,
        bool forceSameVersion = false,
        string? githubToken = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profile.GitHubRepo) || !profile.GitHubRepo.Contains('/'))
            return AppUpdateCheckResult.Fail("GitHub リポジトリが不正です（owner/repo）。");

        var current = VersionComparer.Normalize(currentVersion) ?? currentVersion;
        GitHubReleaseDto? release;

        try
        {
            if (!string.IsNullOrWhiteSpace(releaseTag))
            {
                release = await _github.GetReleaseByTagAsync(profile.GitHubRepo, releaseTag.Trim(), githubToken, cancellationToken);
                if (release is null)
                    return AppUpdateCheckResult.Fail($"Release が見つかりません: {releaseTag}");
            }
            else
            {
                release = await FindLatestReleaseAsync(profile, githubToken, cancellationToken);
                if (release is null)
                {
                    return AppUpdateCheckResult.Fail(
                        $"GitHub Release が取得できません（{profile.GitHubRepo} / tag 接頭辞: {profile.ReleaseTagPrefix}）。" +
                        "リポジトリが非公開の場合は公開するか、環境変数 KAKIKOMI_GITHUB_TOKEN を設定してください。");
                }
            }
        }
        catch (Exception ex)
        {
            return AppUpdateCheckResult.Fail($"GitHub の確認に失敗しました: {ex.Message}");
        }

        var latestVersion = ResolveReleaseVersion(release, profile);
        if (string.IsNullOrWhiteSpace(latestVersion))
            return AppUpdateCheckResult.Fail("リリースのバージョンを判別できません。");

        var asset = PickInstallerAsset(release.Assets, profile.AssetNamePattern);
        if (asset is null)
            return AppUpdateCheckResult.Fail("Release に Setup .exe アセットがありません。");

        var cmp = VersionComparer.Compare(latestVersion, current);
        var available = cmp > 0 || (forceSameVersion && cmp == 0);
        if (!available)
        {
            return AppUpdateCheckResult.UpToDate(
                current,
                latestVersion,
                release.TagName,
                release.HtmlUrl);
        }

        return AppUpdateCheckResult.UpdateAvailable(
            current,
            latestVersion,
            release.TagName ?? latestVersion,
            release.HtmlUrl,
            asset.Name ?? "update.exe",
            asset.BrowserDownloadUrl ?? string.Empty);
    }

    public async Task<string> DownloadInstallerAsync(
        AppUpdateCheckResult check,
        IProgress<OnlineUpdateProgress>? progress,
        string? githubToken = null,
        CancellationToken cancellationToken = default)
    {
        if (!check.Ok || !check.Available)
            throw new InvalidOperationException(check.Error ?? "更新情報がありません。");

        if (string.IsNullOrWhiteSpace(check.DownloadUrl))
            throw new InvalidOperationException("ダウンロード URL が空です。");

        var assetName = Path.GetFileName(check.AssetName ?? "Kakikomi-Update.exe");
        var targetPath = Path.Combine(
            Path.GetTempPath(),
            $"kakikomi-update-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{assetName}");

        progress?.Report(new OnlineUpdateProgress("ダウンロード準備中...", 0));
        await _github.DownloadAsync(check.DownloadUrl, targetPath, githubToken, progress, cancellationToken);
        return targetPath;
    }

    public static bool TryLaunchInstaller(string installerPath, out string? error)
    {
        error = null;

        if (!File.Exists(installerPath))
        {
            error = "更新ファイルが見つかりません。";
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true,
                Verb = "runas",
            };
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "インストーラーの起動に失敗しました（UAC が拒否された可能性があります）。";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private async Task<GitHubReleaseDto?> FindLatestReleaseAsync(
        AppReleaseProfile profile,
        string? githubToken,
        CancellationToken cancellationToken)
    {
        var releases = await _github.ListReleasesAsync(profile.GitHubRepo, githubToken, cancellationToken);
        var candidates = releases
            .Where(r => !string.IsNullOrWhiteSpace(r.TagName))
            .Where(r =>
                r.TagName!.StartsWith(profile.ReleaseTagPrefix, StringComparison.OrdinalIgnoreCase)
                || (profile.ReleaseTagPrefix == "v"
                    && (r.TagName.StartsWith('v') || r.TagName.StartsWith('V')
                        || char.IsDigit(r.TagName[0]))))
            .Select(r => new
            {
                Release = r,
                Version = ResolveReleaseVersion(r, profile),
                PublishedAt = r.PublishedAt ?? DateTimeOffset.MinValue,
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Version))
            .OrderByDescending(x => VersionComparer.Compare(x.Version, "0.0.0"))
            .ThenByDescending(x => x.PublishedAt)
            .ToList();

        return candidates.FirstOrDefault()?.Release;
    }

    private static string? ResolveReleaseVersion(GitHubReleaseDto release, AppReleaseProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(release.TagName))
        {
            var fromTag = VersionComparer.ExtractFromTag(release.TagName, profile.ReleaseTagPrefix);
            var normalized = VersionComparer.Normalize(fromTag);
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
        }

        return VersionComparer.Normalize(release.Name);
    }

    private static GitHubReleaseAssetDto? PickInstallerAsset(List<GitHubReleaseAssetDto>? assets, string assetPattern)
    {
        if (assets is null || assets.Count == 0)
            return null;

        var pattern = assetPattern.Trim().ToLowerInvariant();
        var exeAssets = assets
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .Where(a => a.Name!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exeAssets.Count == 0)
            return null;

        IEnumerable<GitHubReleaseAssetDto> filtered = exeAssets;
        if (!string.IsNullOrWhiteSpace(pattern))
        {
            filtered = exeAssets
                .Where(a => a.Name!.Replace('-', '_').Contains(pattern, StringComparison.OrdinalIgnoreCase)
                            || a.Name.Contains(pattern.Replace('_', '-'), StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (!filtered.Any())
                filtered = exeAssets;
        }

        var list = filtered.ToList();
        return list.FirstOrDefault(a => a.Name!.Contains("setup", StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault(a => !a.Name!.Contains("portable", StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault();
    }
}
