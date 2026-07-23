using Windows.Media.Playback;

namespace Kakikomi.Services;

/// <summary>
/// 表示スロット用の単一 MediaPlayer。
/// コンパネ／外部出力は同じインスタンスを Composition サーフェスで共有し、映像ずれを構造的に無くす。
/// </summary>
internal sealed class MediaPlayerPair : IDisposable
{
    public MediaPlayer Player { get; }

    /// <summary>互換エイリアス（実体は <see cref="Player"/> と同じ）。</summary>
    public MediaPlayer Operator => Player;

    /// <summary>互換エイリアス（実体は <see cref="Player"/> と同じ）。</summary>
    public MediaPlayer Clean => Player;

    public string? Path { get; set; }
    public bool EventsWired { get; set; }

    public MediaPlayerPair()
    {
        Player = CreatePlayer();
    }

    public void ClearSource()
    {
        try
        {
            Player.Source = null;
        }
        catch
        {
            // ignore
        }

        Path = null;
    }

    public void Dispose()
    {
        ClearSource();
        Player.Dispose();
    }

    private static MediaPlayer CreatePlayer()
    {
        var player = new MediaPlayer
        {
            AutoPlay = false,
            RealTimePlayback = false,
            IsMuted = true,
            Volume = 0,
            IsVideoFrameServerEnabled = true
        };
        player.CommandManager.IsEnabled = false;
        return player;
    }
}
