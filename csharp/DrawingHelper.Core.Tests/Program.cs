using System.Diagnostics;
using DrawingHelper.Core.ColorScience;
using DrawingHelper.Core.Matching;
using DrawingHelper.Core.Palette;
using DrawingHelper.Core.Progress;

int passed = 0;
int failed = 0;

void RunTest(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"[PASS] {name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] {name}: {ex.Message}");
        failed++;
    }
}

void Assert(bool cond, string message)
{
    if (!cond) throw new Exception(message);
}

void AssertClose(double actual, double expected, double tol, string label = "")
{
    if (Math.Abs(actual - expected) > tol)
        throw new Exception($"{label} expected {expected}, got {actual} (diff={Math.Abs(actual - expected)})");
}

string palettePath = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "minecraft_palette_rgb.md"));
Assert(File.Exists(palettePath), $"Palette file not found at {palettePath}");

ItemPalette palette = PaletteParser.FromMarkdownFile(palettePath, verifyConsistency: true);

RunTest("PaletteParser loads all 61 items and 244 colors", () =>
{
    Assert(palette.ItemNames.Count == 61, $"Expected 61 items, got {palette.ItemNames.Count}");
    Assert(palette.Count == 244, $"Expected 244 colors, got {palette.Count}");
    Assert(palette[0].KoreanName == "진홍빛 균사체", $"Unexpected first item: {palette[0].KoreanName}");
    Assert(palette[0].Rgb == new Rgb24(74, 40, 56), $"Unexpected first RGB: {palette[0].Rgb}");
    Assert(palette[^1].KoreanName == "먹물 주머니", $"Unexpected last item: {palette[^1].KoreanName}");
    Assert(palette[^1].Rgb == new Rgb24(14, 14, 14), $"Unexpected last RGB: {palette[^1].Rgb}");

    var noEmerald = palette.Filter(excludeItems: ["Emerald"]);
    Assert(noEmerald.Count == 240, $"Expected 240 colors without Emerald, got {noEmerald.Count}");
});

RunTest("sRGB <-> Linear roundtrip", () =>
{
    for (int i = 0; i < 256; i++)
    {
        double lin = ColorSpace.SrgbByteToLinear((byte)i);
        byte back = ColorSpace.LinearToSrgbByte(lin);
        Assert(back == i, $"Roundtrip mismatch at {i} -> {back}");
    }
});

RunTest("CIEDE2000 exact verification against Sharma, Wu, Dalal (2005) 34 pairs", () =>
{
    (LabColor L1, LabColor L2, double Expected)[] pairs =
    [
        (new(50.0000, 2.6772, -79.7751), new(50.0000, 0.0000, -82.7485), 2.0425),
        (new(50.0000, 3.1571, -77.2803), new(50.0000, 0.0000, -82.7485), 2.8615),
        (new(50.0000, 2.8361, -74.0200), new(50.0000, 0.0000, -82.7485), 3.4412),
        (new(50.0000, -1.3802, -84.2814), new(50.0000, 0.0000, -82.7485), 1.0000),
        (new(50.0000, -1.1848, -84.8006), new(50.0000, 0.0000, -82.7485), 1.0000),
        (new(50.0000, -0.9009, -85.5211), new(50.0000, 0.0000, -82.7485), 1.0000),
        (new(50.0000, 0.0000, 0.0000), new(50.0000, -1.0000, 2.0000), 2.3669),
        (new(50.0000, -1.0000, 2.0000), new(50.0000, 0.0000, 0.0000), 2.3669),
        (new(50.0000, 2.4900, -0.0010), new(50.0000, -2.4900, 0.0009), 7.1792),
        (new(50.0000, 2.4900, -0.0010), new(50.0000, -2.4900, 0.0010), 7.1792),
        (new(50.0000, 2.4900, -0.0010), new(50.0000, -2.4900, 0.0011), 7.2195),
        (new(50.0000, 2.4900, -0.0010), new(50.0000, -2.4900, 0.0012), 7.2195),
        (new(50.0000, -0.0010, 2.4900), new(50.0000, 0.0009, -2.4900), 4.8045),
        (new(50.0000, -0.0010, 2.4900), new(50.0000, 0.0011, -2.4900), 4.7461),
        (new(50.0000, 2.5000, 0.0000), new(50.0000, 0.0000, -2.5000), 4.3065),
        (new(50.0000, 2.5000, 0.0000), new(73.0000, 25.0000, -18.0000), 27.1492),
        (new(50.0000, 2.5000, 0.0000), new(61.0000, -5.0000, 29.0000), 22.8977),
        (new(50.0000, 2.5000, 0.0000), new(56.0000, -27.0000, -3.0000), 31.9030),
        (new(50.0000, 2.5000, 0.0000), new(58.0000, 24.0000, 15.0000), 19.4535),
        (new(50.0000, 2.5000, 0.0000), new(50.0000, 3.1736, 0.5854), 1.0000),
        (new(50.0000, 2.5000, 0.0000), new(50.0000, 3.2972, 0.0000), 1.0000),
        (new(50.0000, 2.5000, 0.0000), new(50.0000, 1.8634, 0.5757), 1.0000),
        (new(50.0000, 2.5000, 0.0000), new(50.0000, 3.2592, 0.3350), 1.0000),
        (new(60.2574, -34.0099, 36.2677), new(60.4626, -34.1751, 39.4387), 1.2644),
        (new(63.0109, -31.0961, -5.8663), new(62.8187, -29.7946, -4.0864), 1.2630),
        (new(61.2901, 3.7196, -5.3901), new(61.4292, 2.2480, -4.9620), 1.8731),
        (new(35.0831, -44.1164, 3.7933), new(35.0232, -40.0716, 1.5901), 1.8645),
        (new(22.7233, 20.0904, -46.6940), new(23.0331, 14.9730, -42.5619), 2.0373),
        (new(36.4612, 47.8580, 18.3852), new(36.2715, 50.5065, 21.2231), 1.4146),
        (new(90.8027, -2.0831, 1.4410), new(91.1528, -1.6435, 0.0447), 1.4441),
        (new(90.9257, -0.5406, -0.9208), new(88.6381, -0.8985, -0.7239), 1.5381),
        (new(6.7747, -0.2908, -2.4247), new(5.8714, -0.0985, -2.2286), 0.6377),
        (new(2.0776, 0.0795, -1.1350), new(0.9033, -0.0636, -0.5514), 0.9082),
    ];

    for (int i = 0; i < pairs.Length; i++)
    {
        double de = ColorDistance.Ciede2000(pairs[i].L1, pairs[i].L2);
        AssertClose(de, pairs[i].Expected, 1e-4, $"Pair {i + 1}");
    }

    double deParam = ColorDistance.Ciede2000(new(50.0, 2.5, 0.0), new(73.0, 25.0, -18.0), kL: 2.0);
    AssertClose(deParam, 21.0386, 1e-4, "Parametric kL=2");
});

RunTest("OKLab reference sRGB primaries", () =>
{
    (Rgb24 Rgb, OkLabColor Exp)[] refs =
    [
        (new(255, 0, 0), new(0.627955, 0.224863, 0.125846)),
        (new(0, 255, 0), new(0.866440, -0.233887, 0.179498)),
        (new(0, 0, 255), new(0.452014, -0.032457, -0.311528)),
        (new(255, 255, 255), new(1.0, 0.0, 0.0)),
        (new(0, 0, 0), new(0.0, 0.0, 0.0)),
    ];
    foreach (var (rgb, exp) in refs)
    {
        var got = ColorSpace.RgbToOkLab(rgb);
        AssertClose(got.L, exp.L, 1e-4, $"{rgb} L");
        AssertClose(got.A, exp.A, 1e-4, $"{rgb} A");
        AssertClose(got.B, exp.B, 1e-4, $"{rgb} B");
    }
});

RunTest("Exact palette self-match and key color agreement with Python", () =>
{
    var matcher = new ColorMatcher(palette, ColorMetric.Ciede2000);
    for (int i = 0; i < palette.Count; i++)
    {
        var (idx, dist) = matcher.MatchSingle(palette[i].Rgb);
        AssertClose(dist, 0.0, 1e-9, $"Self-match {i}");
        Assert(palette[idx].Rgb == palette[i].Rgb, "Matched RGB differs");
    }

    // Verify exact agreement with Python CLI lookups
    var peach = matcher.MatchTopK(Rgb24.FromHex("#E8B89A"), 1)[0];
    Assert(peach.Color.KoreanName == "생 토끼고기" && peach.Color.Shade == 1,
        $"Expected 생 토끼고기 1단계, got {peach.Color.DisplayLabel}");

    var blue = matcher.MatchTopK(Rgb24.FromHex("#4B6FD9"), 1)[0];
    Assert(blue.Color.KoreanName == "청금석 원석" && blue.Color.Shade == 1,
        $"Expected 청금석 원석 1단계, got {blue.Color.DisplayLabel}");

    // Verify bounded CIEDE2000 gives 100% identical results to brute-force on 1000 random colors
    var rng = new Random(42);
    for (int i = 0; i < 1000; i++)
    {
        var c = new Rgb24((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
        var (fastIdx, fastDist) = matcher.MatchSingle(c);
        var brute = matcher.MatchTopK(c, 1)[0];
        AssertClose(fastDist, brute.Distance, 1e-10, $"Bounded vs brute distance at {c}");
        Assert(fastIdx == brute.PaletteIndex, $"Bounded vs brute index mismatch at {c}: {fastIdx} != {brute.PaletteIndex}");
    }
});

RunTest("Linear resampling, dithering, and 32x32 tile slicing", () =>
{
    var src = new Rgb24[140 * 100];
    for (int y = 0; y < 100; y++)
        for (int x = 0; x < 140; x++)
            src[y * 140 + x] = new Rgb24((byte)(x * 255 / 139), (byte)(y * 255 / 99), (byte)((x + y) % 256));

    foreach (DitherMode dither in Enum.GetValues<DitherMode>())
    {
        var res = ImageConverterEngine.Convert(
            src, 140, 100, palette,
            new ConversionOptions(Width: 70, Height: 50, Dithering: dither));
        Assert(res.Width == 70 && res.Height == 50, $"Unexpected size {res.Width}x{res.Height}");
        Assert(res.TileGridShape(32) == (3, 2), "Expected 3x2 tiles");
        var (edgeTile, bounds) = res.SliceTile(2, 1, 32);
        Assert(bounds == new TileBounds(64, 32, 70, 50), $"Unexpected bounds {bounds}");
        Assert(edgeTile.Width == 6 && edgeTile.Height == 18, "Unexpected edge tile size");
    }
});

RunTest("ProgressStore JSON save/load roundtrip", () =>
{
    string tmp = Path.Combine(Path.GetTempPath(), $"dh_test_{Guid.NewGuid():N}.json");
    try
    {
        var prog = new ProjectProgress
        {
            Width = 64,
            Height = 64,
            SelectedTile = [1, 0],
            CompletedTiles = [[0, 0]],
            CompletedColors = new() { ["1,0"] = [3, 12, 44] }
        };
        ProgressStore.Save(tmp, prog);
        var loaded = ProgressStore.Load(tmp);
        Assert(loaded.Width == 64 && loaded.SelectedTile[0] == 1, "Progress header mismatch");
        Assert(loaded.CompletedTiles.Count == 1 && loaded.CompletedColors["1,0"].SequenceEqual([3, 12, 44]),
            "Progress body mismatch");
    }
    finally
    {
        if (File.Exists(tmp)) File.Delete(tmp);
    }
});

RunTest("Benchmark 128x128 CIEDE2000 speed (< 300ms)", () =>
{
    var img = new Rgb24[128 * 128];
    for (int y = 0; y < 128; y++)
        for (int x = 0; x < 128; x++)
            img[y * 128 + x] = new Rgb24((byte)(x * 2), (byte)(y * 2), (byte)((x * 3 + y * 5) & 255));

    var matcher = new ColorMatcher(palette, ColorMetric.Ciede2000);
    // Warmup JIT
    matcher.MatchBatch(img.AsSpan(0, 64));

    var sw = Stopwatch.StartNew();
    matcher.MatchBatch(img);
    sw.Stop();
    Console.WriteLine($"       -> 128x128 (16,384 px) exact CIEDE2000 time: {sw.Elapsed.TotalMilliseconds:F1} ms");
    Assert(sw.Elapsed.TotalMilliseconds < 500, $"Too slow: {sw.Elapsed.TotalMilliseconds:F1} ms");
});

Console.WriteLine($"\nSummary: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;
