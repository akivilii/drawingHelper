namespace DrawingHelper.Core.Palette;

/// <summary>Compact 24-bit sRGB color (0..255 per channel).</summary>
public readonly record struct Rgb24(byte R, byte G, byte B)
{
    public uint Packed => ((uint)R << 16) | ((uint)G << 8) | B;

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public static Rgb24 FromPacked(uint packed) =>
        new((byte)((packed >> 16) & 0xFF), (byte)((packed >> 8) & 0xFF), (byte)(packed & 0xFF));

    public static Rgb24 FromHex(string hex)
    {
        ReadOnlySpan<char> span = hex.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '#')
            span = span[1..];
        if (span.Length != 6)
            throw new FormatException($"Invalid HEX color: '{hex}'");

        byte r = Convert.ToByte(span[0..2].ToString(), 16);
        byte g = Convert.ToByte(span[2..4].ToString(), 16);
        byte b = Convert.ToByte(span[4..6].ToString(), 16);
        return new Rgb24(r, g, b);
    }

    public override string ToString() => $"rgb({R}, {G}, {B})";
}

/// <summary>Single paintable Minecraft color corresponding to an item and shade level (1..4).</summary>
public sealed record PaletteColor(
    int Index,
    string ItemName,
    string KoreanName,
    string EnglishName,
    int Shade,
    Rgb24 Rgb,
    string HexCode,
    string Group = "")
{
    public string ShadeLabel => Shade switch
    {
        1 => "가장 밝음",
        2 => "밝음",
        3 => "어두움",
        4 => "가장 어두움",
        _ => $"{Shade}단계"
    };

    public string DisplayLabel => $"{KoreanName} ({Shade}단계: {ShadeLabel})";
}

/// <summary>Immutable collection of Minecraft palette colors with filtering support.</summary>
public sealed class ItemPalette
{
    private readonly PaletteColor[] _colors;
    private readonly string[] _itemNames;

    public ItemPalette(IEnumerable<PaletteColor> colors)
    {
        var list = colors.ToList();
        if (list.Count == 0)
            throw new ArgumentException("Palette must contain at least one color.", nameof(colors));

        _colors = new PaletteColor[list.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();
        for (int i = 0; i < list.Count; i++)
        {
            _colors[i] = list[i] with { Index = i };
            if (seen.Add(_colors[i].ItemName))
                names.Add(_colors[i].ItemName);
        }
        _itemNames = names.ToArray();
    }

    public int Count => _colors.Length;
    public PaletteColor this[int index] => _colors[index];
    public IReadOnlyList<PaletteColor> Colors => _colors;
    public IReadOnlyList<string> ItemNames => _itemNames;

    public ItemPalette Filter(
        IEnumerable<int>? shades = null,
        IEnumerable<string>? includeItems = null,
        IEnumerable<string>? excludeItems = null,
        IEnumerable<string>? groups = null)
    {
        HashSet<int>? shadeSet = shades is not null ? new HashSet<int>(shades) : null;
        HashSet<string>? includeSet = includeItems is not null
            ? new HashSet<string>(includeItems.Select(s => s.Trim().ToLowerInvariant()))
            : null;
        HashSet<string> excludeSet = excludeItems is not null
            ? new HashSet<string>(excludeItems.Select(s => s.Trim().ToLowerInvariant()))
            : new HashSet<string>();
        HashSet<string>? groupSet = groups is not null
            ? new HashSet<string>(groups.Select(g => g.Trim()))
            : null;

        var filtered = new List<PaletteColor>(_colors.Length);
        foreach (var c in _colors)
        {
            if (shadeSet is not null && !shadeSet.Contains(c.Shade))
                continue;
            if (groupSet is not null && !groupSet.Contains(c.Group))
                continue;

            string fullL = c.ItemName.ToLowerInvariant();
            string koL = c.KoreanName.ToLowerInvariant();
            string enL = c.EnglishName.ToLowerInvariant();

            if (includeSet is not null &&
                !includeSet.Contains(fullL) &&
                !includeSet.Contains(koL) &&
                !includeSet.Contains(enL))
                continue;

            if (excludeSet.Contains(fullL) || excludeSet.Contains(koL) || excludeSet.Contains(enL))
                continue;

            filtered.Add(c);
        }

        return new ItemPalette(filtered);
    }
}
