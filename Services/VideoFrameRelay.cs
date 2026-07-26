using System.Runtime.CompilerServices;
using Microsoft.Graphics.Canvas;
using Windows.Media.Playback;

namespace Kakikomi.Services;

/// <summary>
/// MediaFramePump の Copy 結果を遠隔プレビューへ中継する。
/// プレイヤーごとに最新フレームを保持し、Pump の購読者には含めない。
/// </summary>
internal static class VideoFrameRelay
{
    private static readonly ConditionalWeakTable<MediaPlayer, FrameSlot> Slots = new();

    /// <summary>Copy 直後（本番 sinks 配信後）に発火。遠隔プレビューはここで拾う。</summary>
    public static event Action<MediaPlayer, CanvasRenderTarget, int, int>? FramePublished;

    public static void Publish(MediaPlayer player, CanvasRenderTarget target, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return;

        var slot = Slots.GetValue(player, static _ => new FrameSlot());
        slot.Update(target, width, height);

        try
        {
            FramePublished?.Invoke(player, target, width, height);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VideoFrameRelay] handler: {ex.Message}");
        }
    }

    public static bool TryGetFrame(
        MediaPlayer player,
        out CanvasRenderTarget? target,
        out int width,
        out int height,
        out long sequence)
    {
        if (Slots.TryGetValue(player, out var slot))
            return slot.TryGet(out target, out width, out height, out sequence);

        target = null;
        width = 0;
        height = 0;
        sequence = 0;
        return false;
    }

    public static void Clear(MediaPlayer player)
    {
        if (Slots.TryGetValue(player, out var slot))
            slot.Clear();
    }

    private sealed class FrameSlot
    {
        private readonly object _gate = new();
        private CanvasRenderTarget? _target;
        private int _width;
        private int _height;
        private long _sequence;

        public void Update(CanvasRenderTarget target, int width, int height)
        {
            lock (_gate)
            {
                _target = target;
                _width = width;
                _height = height;
                _sequence++;
            }
        }

        public bool TryGet(
            out CanvasRenderTarget? target,
            out int width,
            out int height,
            out long sequence)
        {
            lock (_gate)
            {
                if (_target is null || _width <= 0 || _height <= 0)
                {
                    target = null;
                    width = 0;
                    height = 0;
                    sequence = 0;
                    return false;
                }

                target = _target;
                width = _width;
                height = _height;
                sequence = _sequence;
                return true;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _target = null;
                _width = 0;
                _height = 0;
            }
        }
    }
}
