namespace Kakikomi.Updates;

public static class VersionComparer
{
    public static string? Normalize(string? input)
    {
        var value = (input ?? string.Empty).Trim();
        if (value.Length == 0)
            return null;

        if (!value.All(c => char.IsDigit(c) || char.IsLetter(c) || c is '.' or '_' or '-'))
            return null;

        return value;
    }

    public static int Compare(string? left, string? right)
    {
        var a = ParseParts(left);
        var b = ParseParts(right);
        var len = Math.Max(a.Count, b.Count);

        for (var i = 0; i < len; i++)
        {
            var va = i < a.Count ? a[i] : 0;
            var vb = i < b.Count ? b[i] : 0;
            if (va > vb) return 1;
            if (va < vb) return -1;
        }

        return 0;
    }

    public static string ExtractFromTag(string tagName, string tagPrefix)
    {
        var tag = tagName.Trim();
        if (tag.StartsWith(tagPrefix, StringComparison.OrdinalIgnoreCase))
            tag = tag[tagPrefix.Length..];

        if (tag.StartsWith('v') || tag.StartsWith('V'))
            tag = tag[1..];

        return tag.Trim();
    }

    private static List<int> ParseParts(string? value)
    {
        var text = value ?? "0";
        return text
            .Split('.', '_', '-')
            .Select(part =>
            {
                var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
                return int.TryParse(digits, out var n) ? n : 0;
            })
            .ToList();
    }
}
