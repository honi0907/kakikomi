using Kakikomi.Controls;

namespace Kakikomi.Services;

/// <summary>映像経路復旧時に全 <see cref="CompositionVideoHost"/> を再バインドする。</summary>
internal static class CompositionVideoHostRegistry
{
    private static readonly object Gate = new();
    private static readonly List<WeakReference<CompositionVideoHost>> Hosts = [];

    public static void Register(CompositionVideoHost host)
    {
        lock (Gate)
        {
            Prune_NoLock();
            Hosts.Add(new WeakReference<CompositionVideoHost>(host));
        }
    }

    public static void Unregister(CompositionVideoHost host)
    {
        lock (Gate)
        {
            for (var i = Hosts.Count - 1; i >= 0; i--)
            {
                if (!Hosts[i].TryGetTarget(out var existing) || ReferenceEquals(existing, host))
                    Hosts.RemoveAt(i);
            }
        }
    }

    public static void ForceRebindAll()
    {
        CompositionVideoHost[] live;
        lock (Gate)
        {
            Prune_NoLock();
            live = Hosts
                .Select(r => r.TryGetTarget(out var h) ? h : null)
                .Where(h => h is not null)
                .Cast<CompositionVideoHost>()
                .ToArray();
        }

        foreach (var host in live)
        {
            try
            {
                host.ForceRebind();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VideoHostRegistry] rebind: {ex.Message}");
            }
        }
    }

    private static void Prune_NoLock()
    {
        for (var i = Hosts.Count - 1; i >= 0; i--)
        {
            if (!Hosts[i].TryGetTarget(out _))
                Hosts.RemoveAt(i);
        }
    }
}
