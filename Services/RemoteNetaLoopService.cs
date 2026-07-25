using Kakikomi.Models;

namespace Kakikomi.Services;

/// <summary>
/// 遠隔ネタ一覧ループ。1本を最後まで再生し、次へ。最終ネタの次は先頭へ戻る。
/// </summary>
internal sealed class RemoteNetaLoopService
{
    public static RemoteNetaLoopService Instance { get; } = new();

    private readonly object _gate = new();
    private int _index = -1;
    private int _busy;
    private bool _running;
    private bool _subscribed;

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    public void Start()
    {
        var engine = App.Engine;
        if (engine is null)
            return;

        lock (_gate)
        {
            if (_running)
                return;
            _running = true;
            _index = -1;
            EnsureSubscribed_NoLock(engine);
        }

        // いまのネタ（なければ先頭）から頭出し再生
        _ = PlayCurrentOrFirstAsync();
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running)
                return;
            _running = false;
        }
    }

    private void EnsureSubscribed_NoLock(EngineSession engine)
    {
        if (_subscribed)
            return;
        engine.MediaEnded += OnMediaEnded;
        _subscribed = true;
    }

    private void OnMediaEnded()
    {
        if (!IsRunning)
            return;

        var dq = App.DispatcherQueue;
        if (dq is null)
            return;

        if (dq.HasThreadAccess)
            _ = AdvanceAsync();
        else
            dq.TryEnqueue(() => _ = AdvanceAsync());
    }

    private async Task PlayCurrentOrFirstAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return;

        try
        {
            var vm = App.MainViewModel;
            if (vm is null)
                return;

            var items = GetPlayable(vm);
            if (items.Count == 0)
            {
                vm.StatusText = "ネタループ: 再生可能なネタがありません";
                return;
            }

            var currentPath = App.Engine?.CurrentPath;
            var index = -1;
            if (!string.IsNullOrWhiteSpace(currentPath))
            {
                index = items.FindIndex(i =>
                    string.Equals(i.Path, currentPath, StringComparison.OrdinalIgnoreCase));
            }

            if (index < 0)
                index = 0;

            await OpenAndPlayAsync(vm, items, index).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NetaLoop] {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task AdvanceAsync()
    {
        if (!IsRunning)
            return;

        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return;

        try
        {
            var vm = App.MainViewModel;
            if (vm is null)
                return;

            var items = GetPlayable(vm);
            if (items.Count == 0)
            {
                vm.StatusText = "ネタループ: 再生可能なネタがありません";
                return;
            }

            var currentPath = App.Engine?.CurrentPath;
            var currentIndex = -1;
            if (!string.IsNullOrWhiteSpace(currentPath))
            {
                currentIndex = items.FindIndex(i =>
                    string.Equals(i.Path, currentPath, StringComparison.OrdinalIgnoreCase));
            }

            if (currentIndex < 0)
                currentIndex = _index;

            // 次へ。最後の次は先頭
            var next = currentIndex < 0 ? 0 : (currentIndex + 1) % items.Count;
            await OpenAndPlayAsync(vm, items, next).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NetaLoop] {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task OpenAndPlayAsync(
        ViewModels.MainPageViewModel vm,
        IReadOnlyList<NetaItem> items,
        int index)
    {
        _index = index;
        var item = items[index];
        vm.StatusText = $"ネタループ: {index + 1}/{items.Count} {item.DisplayName}";
        await vm.SelectNetaForRemoteAsync(item).ConfigureAwait(true);

        var engine = App.Engine;
        if (engine is null || !IsRunning)
            return;

        engine.Play();
        await Task.Delay(50).ConfigureAwait(true);
        if (IsRunning &&
            !engine.IsPlaying &&
            string.Equals(engine.CurrentPath, item.Path, StringComparison.OrdinalIgnoreCase))
            engine.Play();
    }

    private static List<NetaItem> GetPlayable(ViewModels.MainPageViewModel vm) =>
        vm.NetaItems
            .Where(i => !i.IsMissing && !string.IsNullOrWhiteSpace(i.Path))
            .ToList();
}
