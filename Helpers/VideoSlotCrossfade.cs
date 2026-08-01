using Kakikomi.Controls;
using Kakikomi.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Kakikomi.Helpers;

/// <summary>操作画面・クリーン出力の A/B スロット切替クロスフェード。</summary>
internal static class VideoSlotCrossfade
{
    public const int DurationMs = 50;
    private const int StepMs = 10;

    public static async Task ApplySlotSwitchAsync(
        CompositionVideoHost hostA,
        CompositionVideoHost hostB,
        int visibleSlotIndex,
        int? previousVisibleSlotIndex,
        CancellationToken cancellationToken = default)
    {
        var incoming = visibleSlotIndex == 0 ? hostA : hostB;
        var outgoing = visibleSlotIndex == 0 ? hostB : hostA;

        if (!AppSettings.NetaSwitchCrossfadeEnabled
            || previousVisibleSlotIndex is null
            || previousVisibleSlotIndex == visibleSlotIndex)
        {
            ApplyInstant(incoming, outgoing);
            return;
        }

        try
        {
            await CrossfadeAsync(outgoing, incoming, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            ApplyInstant(incoming, outgoing);
            throw;
        }
    }

    private static void ApplyInstant(CompositionVideoHost incoming, CompositionVideoHost outgoing)
    {
        incoming.ShowInstant();
        outgoing.HideInstant();
    }

    private static async Task CrossfadeAsync(
        CompositionVideoHost outgoing,
        CompositionVideoHost incoming,
        CancellationToken cancellationToken)
    {
        // 旧映像は 100% のまま。新映像だけ上からフェードイン（outgoing を薄めると黒背景が見える）。
        outgoing.Opacity = 1;
        outgoing.Visibility = Visibility.Visible;
        outgoing.SetVideoOpacity(1);
        Canvas.SetZIndex(incoming, 1);
        Canvas.SetZIndex(outgoing, 0);
        incoming.BeginIncomingCrossfade();

        var steps = Math.Max(1, DurationMs / StepMs);
        for (var i = 1; i <= steps; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var t = EaseInOut((double)i / steps);
            incoming.SetVideoOpacity(t);
            await Task.Delay(StepMs, cancellationToken).ConfigureAwait(true);
        }

        incoming.SetVideoOpacity(1);
        outgoing.FinishOutgoingCrossfade();
        incoming.FinishIncomingInstant();
        Canvas.SetZIndex(incoming, 0);
        Canvas.SetZIndex(outgoing, 0);
    }

    private static double EaseInOut(double t) =>
        t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
}
