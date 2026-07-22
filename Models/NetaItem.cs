using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Kakikomi.Models;

public sealed partial class NetaItem : ObservableObject
{
    public required string DisplayName { get; init; }
    public required string Path { get; init; }

    [ObservableProperty]
    private ImageSource? thumbnail;

    [ObservableProperty]
    private string frameRateLabel = string.Empty;

    public Visibility FrameRateVisibility =>
        string.IsNullOrEmpty(FrameRateLabel) ? Visibility.Collapsed : Visibility.Visible;

    partial void OnFrameRateLabelChanged(string value) =>
        OnPropertyChanged(nameof(FrameRateVisibility));

    public override string ToString() => DisplayName;
}
