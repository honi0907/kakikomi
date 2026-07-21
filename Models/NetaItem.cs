namespace Kakikomi.Models;

public sealed class NetaItem
{
    public required string DisplayName { get; init; }
    public required string Path { get; init; }

    public override string ToString() => DisplayName;
}
