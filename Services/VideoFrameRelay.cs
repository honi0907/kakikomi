using Microsoft.Graphics.Canvas;
using Windows.Media.Playback;

namespace Kakikomi.Services;

/// <summary>
/// MediaFramePump の Copy 結果を遠隔プレビューへ中継する。
/// プレビューは Pump の購読者にならず、本番表示（操作＋クリーン）だけが sinks に乗る。
/// </summary>
internal static class VideoFrameRelay
{
    private static readonly object Gate = new();
    private static MediaPlayer? _player;
    private static CanvasRenderTarget? _target;
    private static int _width;
    private static int _height;
    private static long _sequence;

    public static long Sequence => Interlocked.Read(ref _sequence);

    public static void Publish(MediaPlayer player, CanvasRenderTarget target, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return;

        lock (Gate)
        {
            _player = player;
            _target = target;
            _width = width;
            _height = height;
        }

        Interlocked.Increment(ref _sequence);
    }

    /// <summary>
    /// 指定プレイヤーの最新フレームを取得する。Pump のダブルバッファを参照するだけ（再 Copy しない）。
    /// </summary>
    public static bool TryGetFrame(
        MediaPlayer player,
        out CanvasRenderTarget? target,
        out int width,
        out int height,
        out long sequence)
    {
        lock (Gate)
        {
            if (_player is null || _target is null || !ReferenceEquals(_player, player))
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

    public static void Clear()
    {
        lock (Gate)
        {
            _player = null;
            _target = null;
            _width = 0;
            _height = 0;
        }
    }
}
