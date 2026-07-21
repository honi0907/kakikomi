namespace Kakikomi.Updates;

public sealed class OnlineUpdateCoordinator
{
    private readonly AppUpdateService _service = new();

    public async Task<OnlineUpdateRunResult> RunAsync(
        Func<string, Task<bool>> confirmUpdateAsync,
        IProgress<OnlineUpdateProgress>? progress,
        Func<Task>? beforeExitAsync,
        Action requestExit,
        bool forceSameVersion = false,
        string? githubToken = null,
        CancellationToken cancellationToken = default)
    {
        var profile = AppReleaseProfile.Default;
        var current = AppVersionReader.GetCurrentVersion();
        var token = githubToken ?? Environment.GetEnvironmentVariable("KAKIKOMI_GITHUB_TOKEN");

        try
        {
            progress?.Report(new OnlineUpdateProgress($"{profile.DisplayName} の更新を確認中...", 0));
            var check = await _service.CheckForUpdateAsync(
                profile,
                current,
                releaseTag: null,
                forceSameVersion,
                token,
                cancellationToken);

            if (!check.Ok)
                return OnlineUpdateRunResult.Failed(check.Error ?? "更新確認に失敗しました。");

            if (!check.Available)
            {
                return OnlineUpdateRunResult.UpToDate(
                    $"最新です（現在 {check.CurrentVersion} / 最新 {check.LatestVersion}）");
            }

            var message =
                $"{profile.DisplayName} を更新しますか？\n" +
                $"{check.CurrentVersion} → {check.LatestVersion}\n" +
                $"ファイル: {check.AssetName}";

            if (!await confirmUpdateAsync(message))
                return OnlineUpdateRunResult.Cancelled("更新をキャンセルしました。");

            var installerPath = await _service.DownloadInstallerAsync(check, progress, token, cancellationToken);

            if (!PackagedAppDetector.CanApplyOnlineUpdate())
            {
                return OnlineUpdateRunResult.Failed(
                    "開発ビルドでは自己更新を実行できません。インストール済みの exe、または dist から起動してください。");
            }

            progress?.Report(new OnlineUpdateProgress("インストーラーを起動中...", 100));
            if (!AppUpdateService.TryLaunchInstaller(installerPath, out var launchError))
                return OnlineUpdateRunResult.Failed(launchError ?? "インストーラーの起動に失敗しました。");

            if (beforeExitAsync is not null)
                await beforeExitAsync();

            progress?.Report(new OnlineUpdateProgress("再起動します...", 100));
            requestExit();
            return OnlineUpdateRunResult.Applied("インストーラーを起動しました。アプリを終了します。");
        }
        catch (OperationCanceledException)
        {
            return OnlineUpdateRunResult.Cancelled("更新をキャンセルしました。");
        }
        catch (Exception ex)
        {
            return OnlineUpdateRunResult.Failed(ex.Message);
        }
    }
}

public sealed class OnlineUpdateRunResult
{
    public OnlineUpdateOutcome Outcome { get; init; }
    public string Message { get; init; } = string.Empty;

    public static OnlineUpdateRunResult UpToDate(string message) =>
        new() { Outcome = OnlineUpdateOutcome.UpToDate, Message = message };

    public static OnlineUpdateRunResult Cancelled(string message) =>
        new() { Outcome = OnlineUpdateOutcome.Cancelled, Message = message };

    public static OnlineUpdateRunResult Failed(string message) =>
        new() { Outcome = OnlineUpdateOutcome.Failed, Message = message };

    public static OnlineUpdateRunResult Applied(string message) =>
        new() { Outcome = OnlineUpdateOutcome.Applied, Message = message };
}

public enum OnlineUpdateOutcome
{
    UpToDate,
    Cancelled,
    Failed,
    Applied,
}
