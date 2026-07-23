using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Kakikomi.Services;

namespace Kakikomi.Models;

public enum NetaConvertState
{
    None,
    NeedsConvert,
    Converting,
    Ready,
    Failed
}

public sealed partial class NetaItem : ObservableObject
{
    [ObservableProperty]
    private string displayName = string.Empty;

    [ObservableProperty]
    private string path = string.Empty;

    [ObservableProperty]
    private bool isMissing;

    [ObservableProperty]
    private ImageSource? thumbnail;

    [ObservableProperty]
    private string frameRateLabel = string.Empty;

    [ObservableProperty]
    private NetaConvertState convertState;

    [ObservableProperty]
    private string convertStatusLabel = string.Empty;

    [ObservableProperty]
    private SolidColorBrush convertStatusBrush = new(Color.FromArgb(0, 0, 0, 0));

    /// <summary>変換進捗 0〜1。左から明るさが戻るオーバーレイ用。</summary>
    [ObservableProperty]
    private double convertProgress;

    public Visibility FrameRateVisibility =>
        string.IsNullOrEmpty(FrameRateLabel) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ConvertStatusVisibility =>
        ConvertState == NetaConvertState.None ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ConvertProgressVisibility =>
        ConvertState == NetaConvertState.Converting ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MissingOverlayVisibility =>
        IsMissing ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>欠落時はカード全体を暗くする。</summary>
    public double CardOpacity => IsMissing ? 0.42 : 1.0;

    /// <summary>暗い帯の ScaleX。進捗0で1（全体暗）、1で0。右原点で縮む。</summary>
    public double ConvertDarkScaleX => 1.0 - Math.Clamp(ConvertProgress, 0, 1);

    partial void OnFrameRateLabelChanged(string value) =>
        OnPropertyChanged(nameof(FrameRateVisibility));

    partial void OnConvertProgressChanged(double value) =>
        OnPropertyChanged(nameof(ConvertDarkScaleX));

    partial void OnIsMissingChanged(bool value)
    {
        OnPropertyChanged(nameof(MissingOverlayVisibility));
        OnPropertyChanged(nameof(CardOpacity));
    }

    partial void OnConvertStateChanged(NetaConvertState value)
    {
        ConvertStatusLabel = value switch
        {
            NetaConvertState.NeedsConvert => "要変換",
            NetaConvertState.Converting => "変換中",
            NetaConvertState.Ready => "変換済",
            NetaConvertState.Failed => "失敗",
            _ => string.Empty
        };

        ConvertStatusBrush = new SolidColorBrush(value switch
        {
            NetaConvertState.NeedsConvert => Color.FromArgb(255, 251, 191, 36),
            NetaConvertState.Converting => Color.FromArgb(255, 56, 189, 248),
            NetaConvertState.Ready => Color.FromArgb(255, 74, 222, 128),
            NetaConvertState.Failed => Color.FromArgb(255, 248, 113, 113),
            _ => Color.FromArgb(0, 0, 0, 0)
        });

        if (value == NetaConvertState.Converting)
            ConvertProgress = 0;
        else if (value == NetaConvertState.Ready)
            ConvertProgress = 1;
        else
            ConvertProgress = 0;

        OnPropertyChanged(nameof(ConvertStatusVisibility));
        OnPropertyChanged(nameof(ConvertProgressVisibility));
        OnPropertyChanged(nameof(ConvertDarkScaleX));
    }

    public void RefreshMissingState()
    {
        IsMissing = string.IsNullOrWhiteSpace(Path) || !File.Exists(Path);
        if (IsMissing)
        {
            Thumbnail = null;
            FrameRateLabel = string.Empty;
            ConvertState = NetaConvertState.None;
        }
    }

    public void RelocateTo(string newPath)
    {
        Path = newPath;
        DisplayName = System.IO.Path.GetFileNameWithoutExtension(newPath);
        RefreshMissingState();
        if (!IsMissing)
            RefreshConvertState();
    }

    /// <summary>ディスク上の変換キャッシュ状態からバッジを更新。</summary>
    public void RefreshConvertState()
    {
        if (IsMissing || !MovTranscodeService.IsMovPath(Path))
        {
            ConvertState = NetaConvertState.None;
            return;
        }

        ConvertState = MovTranscodeService.NeedsConvert(Path)
            ? NetaConvertState.NeedsConvert
            : NetaConvertState.Ready;
    }

    public override string ToString() => DisplayName;
}
