using Kakikomi.Updates;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Kakikomi.Services;

public static class OnlineUpdateUiHelper
{
    private static readonly OnlineUpdateCoordinator Coordinator = new();
    private static int _busy;

    public static async Task RunAsync(
        XamlRoot? xamlRoot,
        Action<string> setStatus,
        Func<Task>? beforeExitAsync = null)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            setStatus("更新処理が既に実行中です。");
            return;
        }

        // WinUI には SynchronizationContext が無いため、await 継続はスレッドプールに乗る。
        // UI 触る処理は必ず App.DispatcherQueue 経由にする。
        var dispatcher = App.DispatcherQueue
            ?? DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("DispatcherQueue が取得できません。");

        void SetStatusSafe(string message) =>
            RunOnUi(dispatcher, () => setStatus(message));

        try
        {
            if (xamlRoot is null)
            {
                SetStatusSafe("画面の準備が完了していません。もう一度お試しください。");
                return;
            }

            var progress = new Progress<OnlineUpdateProgress>(p => SetStatusSafe(p.Message));
            var result = await Coordinator.RunAsync(
                message => ConfirmOnUiAsync(dispatcher, xamlRoot, message),
                progress,
                beforeExitAsync,
                () => RunOnUi(dispatcher, App.RequestExit)).ConfigureAwait(false);

            SetStatusSafe(result.Message);
        }
        catch (Exception ex)
        {
            SetStatusSafe($"更新に失敗しました: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static void RunOnUi(DispatcherQueue dispatcher, Action action)
    {
        if (dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        _ = dispatcher.TryEnqueue(() =>
        {
            try
            {
                action();
            }
            catch
            {
                // UI 破棄後などは無視
            }
        });
    }

    private static Task<bool> ConfirmOnUiAsync(DispatcherQueue dispatcher, XamlRoot xamlRoot, string message)
    {
        if (dispatcher.HasThreadAccess)
            return ConfirmAsync(xamlRoot, message);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() => _ = CompleteConfirmAsync(tcs, xamlRoot, message)))
            tcs.TrySetResult(false);

        return tcs.Task;
    }

    private static async Task CompleteConfirmAsync(
        TaskCompletionSource<bool> tcs,
        XamlRoot xamlRoot,
        string message)
    {
        try
        {
            tcs.TrySetResult(await ConfirmAsync(xamlRoot, message));
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
    }

    private static async Task<bool> ConfirmAsync(XamlRoot xamlRoot, string message)
    {
        var dialog = new ContentDialog
        {
            Title = "オンライン更新",
            Content = message,
            PrimaryButtonText = "更新する",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
