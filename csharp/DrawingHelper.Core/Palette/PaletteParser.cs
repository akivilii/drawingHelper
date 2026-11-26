using System.Text;
using System.Text.RegularExpressions;

namespace DrawingHelper.Core.Palette;

/// <summary>Parses minecraft_palette_rgb.md and verifies HEX/RGB consistency.</summary>
public static partial class PaletteParser
{
    [GeneratedRegex(@"^##\s+(.+?)\s*$")]
    private static partial Regex GroupHeaderRegex();

    [GeneratedRegex(@"^-\s+`((?:#[0-9A-Fa-f]{6}\s*)+)`\s+-\s+(.+?)\s*$")]
    private static partial Regex SummaryItemRegex();

    [GeneratedRegex(
        @"^\|\s*([^|]+?)\s*\|\s*" +
        @"`#([0-9A-Fa-f]{6})`<br>rgb\((\d+),\s*(\d+),\s*(\d+)\)\s*\|\s*" +
        @"`#([0-9A-Fa-f]{6})`<br>rgb\((\d+),\s*(\d+),\s*(\d+)\)\s*\|\s*" +
        @"`#([0-9A-Fa-f]{6})`<br>rgb\((\d+),\s*(\d+),\s*(\d+)\)\s*\|\s*" +
        @"`#([0-9A-Fa-f]{6})`<br>rgb\((\d+),\s*(\d+),\s*(\d+)\)\s*\|$")]
    private static partial Regex TableRowRegex();

    [GeneratedRegex(@"^(.+?)\s*\(([^()]+)\)\s*$")]
    private static partial Regex NameSplitRegex();

    public static ItemPalette FromMarkdownFile(string path, bool verifyConsistency = true)
    {
        string text = File.ReadAllText(path, Encoding.UTF8);
        return FromMarkdownText(text, verifyConsistency);
    }

    public static ItemPalette FromMarkdownText(string markdown, bool verifyConsistency = true)
    {
        var tableColors = new List<PaletteColor>(256);
        var summaryColors = new List<PaletteColor>(256);
        string currentGroup = string.Empty;

        using var reader = new StringReader(markdown);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) is not null)
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            var headerMatch = GroupHeaderRegex().Match(line);
            if (headerMatch.Success)
            {
                currentGroup = headerMatch.Groups[1].Value.Trim();
                continue;
            }

            var tableMatch = TableRowRegex().Match(line);
            if (tableMatch.Success)
            {
                string fullName = tableMatch.Groups[1].Value.Trim();
                var (koName, enName) = SplitItemName(fullName);
                for (int shadeIdx = 0; shadeIdx < 4; shadeIdx++)
                {
                    int gBase = 2 + shadeIdx * 4;
                    string hexStr = "#" + tableMatch.Groups[gBase].Value.ToUpperInvariant();
                    var rgb = new Rgb24(
                        byte.Parse(tableMatch.Groups[gBase + 1].Value),
                        byte.Parse(tableMatch.Groups[gBase + 2].Value),
                        byte.Parse(tableMatch.Groups[gBase + 3].Value));

                    if (verifyConsistency && Rgb24.FromHex(hexStr) != rgb)
                    {
                        throw new InvalidDataException(
                            $"HEX/RGB mismatch for {fullName} shade {shadeIdx + 1}: {hexStr} vs {rgb}");
                    }

                    tableColors.Add(new PaletteColor(
                        tableColors.Count, fullName, koName, enName, shadeIdx + 1, rgb, hexStr, currentGroup));
                }
                continue;
            }

            var summaryMatch = SummaryItemRegex().Match(line);
            if (summaryMatch.Success)
            {
                string[] hexes = summaryMatch.Groups[1].Value
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string fullName = summaryMatch.Groups[2].Value.Trim();
                var (koName, enName) = SplitItemName(fullName);
                for (int i = 0; i < hexes.Length; i++)
                {
                    string hexStr = "#" + hexes[i].TrimStart('#').ToUpperInvariant();
                    summaryColors.Add(new PaletteColor(
                        summaryColors.Count, fullName, koName, enName, i + 1, Rgb24.FromHex(hexStr), hexStr, currentGroup));
                }
            }
        }

        if (tableColors.Count > 0 && summaryColors.Count > 0 && verifyConsistency)
        {
            if (tableColors.Count != summaryColors.Count)
                throw new InvalidDataException(
                    $"Summary ({summaryColors.Count}) and table ({tableColors.Count}) color counts differ.");

            for (int i = 0; i < tableColors.Count; i++)
            {
                if (tableColors[i].ItemName != summaryColors[i].ItemName ||
                    tableColors[i].Shade != summaryColors[i].Shade ||
                    tableColors[i].Rgb != summaryColors[i].Rgb)
                {
                    throw new InvalidDataException(
                        $"Summary/table mismatch at index {i}: {summaryColors[i]} != {tableColors[i]}");
                }
            }
        }

        var chosen = tableColors.Count > 0 ? tableColors : summaryColors;
        if (chosen.Count == 0)
            throw new InvalidDataException("No palette entries found in markdown.");

        return new ItemPalette(chosen);
    }

    private static (string Korean, string English) SplitItemName(string fullName)
    {
        var m = NameSplitRegex().Match(fullName);
        if (m.Success)
            return (m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim());
        return (fullName, fullName);
    }
}
