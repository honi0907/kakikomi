using Windows.Media.Playback;

namespace Kakikomi.Services;

/// <summary>Operator / Clean の MediaPlayer ペア。A/B 表示スロットまたはウォームキャッシュで使う。</summary>
internal sealed class MediaPlayerPair : IDisposable
{
    public MediaPlayer Operator { get; }
    public MediaPlayer Clean { get; }
    public string? Path { get; set; }

    public MediaPlayerPair()
    {
        Operator = CreatePlayer();
        Clean = CreatePlayer();
    }

    public void ClearSource()
    {
        try
        {
            Operator.Source = null;
            Clean.Source = null;
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
        Operator.Dispose();
        Clean.Dispose();
    }

    private static MediaPlayer CreatePlayer()
    {
        var player = new MediaPlayer
        {
            AutoPlay = false,
            RealTimePlayback = false,
            // ウォーム／先頭フレーム確定の Play で音が出ないように既定ミュート
            IsMuted = true,
            Volume = 0
        };
        player.CommandManager.IsEnabled = false;
        return player;
    }
}
