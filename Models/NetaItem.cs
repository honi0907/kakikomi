using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace Kakikomi.Models;

public sealed partial class NetaItem : ObservableObject
{
    public required string DisplayName { get; init; }
    public required string Path { get; init; }

    [ObservableProperty]
    private ImageSource? thumbnail;

    public override string ToString() => DisplayName;
}
