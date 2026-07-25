using System.Text.Json;
using Kakikomi.Models;

namespace Kakikomi.Services;

/// <summary>遠隔コマンドを UI スレッド経由で ViewModel / Engine に渡す。</summary>
internal static class RemoteControlBridge
{
    public static object BuildStatus()
    {
        object? result = null;
        var dq = App.DispatcherQueue;
        if (dq is null)
            return new { ok = false, error = "no dispatcher" };

        if (dq.HasThreadAccess)
            return BuildStatusCore();

        var done = new ManualResetEventSlim(false);
        if (!dq.TryEnqueue(() =>
            {
                try { result = BuildStatusCore(); }
                catch (Exception ex) { result = new { ok = false, error = ex.Message }; }
                finally { done.Set(); }
            }))
        {
            return new { ok = false, error = "enqueue failed" };
        }

        if (!done.Wait(500))
            return new { ok = false, error = "timeout" };

        return result ?? new { ok = false };
    }

    private static object BuildStatusCore()
    {
        var vm = App.MainViewModel;
        var engine = App.Engine;
        var netas = vm?.NetaItems.Select(ToNetaDto).ToList() ?? [];
        var selected = vm?.SelectedNeta;
        var folder = engine?.FolderPath;

        return new
        {
            ok = true,
            playing = engine?.IsPlaying ?? false,
            rate = engine?.ClockRate ?? 1.0,
            path = engine?.CurrentPath,
            displayName = selected?.DisplayName,
            positionSec = engine?.TimelinePosition.TotalSeconds ?? 0,
            durationSec = engine?.TimelineDuration.TotalSeconds ?? 0,
            hasTimeline = (engine?.TimelineDuration.TotalSeconds ?? 0) > 0.05,
            folderPath = folder,
            folderName = string.IsNullOrWhiteSpace(folder)
                ? null
                : (Path.GetFileName(folder.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : folder),
            statusText = vm?.StatusText,
            netaLoop = RemoteNetaLoopService.Instance.IsRunning,
            netas
        };
    }

    public static Task HandleCommandAsync(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("cmd", out var cmdEl))
            return Task.CompletedTask;

        var cmd = cmdEl.GetString() ?? "";
        string? path = null;
        double? rate = null;
        double? seconds = null;
        int? intervalSec = null;
        bool? enabled = null;
        if (root.TryGetProperty("path", out var pathEl))
            path = pathEl.GetString();
        if (root.TryGetProperty("rate", out var rateEl) && rateEl.TryGetDouble(out var r))
            rate = r;
        if (root.TryGetProperty("seconds", out var secEl) && secEl.TryGetDouble(out var s))
            seconds = s;
        if (root.TryGetProperty("intervalSec", out var intEl) && intEl.TryGetInt32(out var iv))
            intervalSec = iv;
        if (root.TryGetProperty("enabled", out var enEl) &&
            (enEl.ValueKind is JsonValueKind.True or JsonValueKind.False))
            enabled = enEl.GetBoolean();

        return EnqueueAsync(() => ApplyCommandAsync(cmd, path, rate, seconds, intervalSec, enabled));
    }

    private static async Task ApplyCommandAsync(
        string cmd,
        string? path,
        double? rate,
        double? seconds,
        int? intervalSec,
        bool? enabled)
    {
        var vm = App.MainViewModel;
        var engine = App.Engine;
        if (vm is null)
            return;

        switch (cmd)
        {
            case "playPause":
                vm.PlayPauseCommand.Execute(null);
                break;
            case "skipBack":
                vm.SkipBackCommand.Execute(null);
                break;
            case "skipForward":
                vm.SkipForwardCommand.Execute(null);
                break;
            case "clearInk":
                vm.ClearInkCommand.Execute(null);
                break;
            case "rate":
                ApplyRate(vm, rate ?? 1.0);
                break;
            case "seekPreview":
                if (seconds is { } previewSec && engine is not null)
                    engine.SeekPreview(TimeSpan.FromSeconds(Math.Max(0, previewSec)));
                break;
            case "seek":
                if (seconds is { } seekSec && engine is not null)
                    engine.SeekTo(TimeSpan.FromSeconds(Math.Max(0, seekSec)), syncClean: true, notifyTimeline: true);
                break;
            case "selectNeta":
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var item = vm.NetaItems.FirstOrDefault(i =>
                        string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
                    if (item is not null)
                        await vm.SelectNetaForRemoteAsync(item).ConfigureAwait(true);
                }
                break;
            case "reloadNetas":
                await vm.ReloadCurrentNetaFolderAsync().ConfigureAwait(true);
                break;
            case "netaLoop":
                if (enabled == true)
                    RemoteNetaLoopService.Instance.Start();
                else
                    RemoteNetaLoopService.Instance.Stop();
                break;
            case "refresh":
                break;
        }
    }

    private static void ApplyRate(ViewModels.MainPageViewModel vm, double rate)
    {
        if (Math.Abs(rate - 0.25) < 0.01)
            vm.RateQuarterCommand.Execute(null);
        else if (Math.Abs(rate - 0.5) < 0.01)
            vm.RateHalfCommand.Execute(null);
        else if (Math.Abs(rate - 2.0) < 0.01)
            vm.RateDoubleCommand.Execute(null);
        else
            vm.RateNormalCommand.Execute(null);
    }

    private static object ToNetaDto(NetaItem item) => new
    {
        path = item.Path,
        name = item.DisplayName,
        missing = item.IsMissing
    };

    private static Task EnqueueAsync(Func<Task> action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dq = App.DispatcherQueue;
        if (dq is null)
        {
            tcs.SetResult();
            return tcs.Task;
        }

        if (!dq.TryEnqueue(async () =>
            {
                try
                {
                    await action().ConfigureAwait(true);
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }))
        {
            tcs.SetResult();
        }

        return tcs.Task;
    }
}
