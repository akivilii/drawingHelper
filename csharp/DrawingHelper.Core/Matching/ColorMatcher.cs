using DrawingHelper.Core.ColorScience;
using DrawingHelper.Core.Palette;

namespace DrawingHelper.Core.Matching;

public enum DitherMode
{
    None,
    Bayer4,
    Bayer8,
    FloydSteinberg
}

public readonly record struct MatchResult(
    PaletteColor Color,
    int PaletteIndex,
    double Distance,
    ColorMetric Metric);

/// <summary>High-speed parallel perceptual nearest-color matcher against an ItemPalette.</summary>
public sealed class ColorMatcher
{
    private static readonly double[,] Bayer4 =
    {
        {  0 / 16.0 - 0.5,  8 / 16.0 - 0.5,  2 / 16.0 - 0.5, 10 / 16.0 - 0.5 },
        { 12 / 16.0 - 0.5,  4 / 16.0 - 0.5, 14 / 16.0 - 0.5,  6 / 16.0 - 0.5 },
        {  3 / 16.0 - 0.5, 11 / 16.0 - 0.5,  1 / 16.0 - 0.5,  9 / 16.0 - 0.5 },
        { 15 / 16.0 - 0.5,  7 / 16.0 - 0.5, 13 / 16.0 - 0.5,  5 / 16.0 - 0.5 }
    };

    private static readonly double[,] Bayer8 = BuildBayer8();

    private static double[,] BuildBayer8()
    {
        int[,] m4 =
        {
            {  0,  8,  2, 10 },
            { 12,  4, 14,  6 },
            {  3, 11,  1,  9 },
            { 15,  7, 13,  5 }
        };
        var m8 = new double[8, 8];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                m8[y, x] = (4 * m4[y, x]) / 64.0 - 0.5;
                m8[y, x + 4] = (4 * m4[y, x] + 2) / 64.0 - 0.5;
                m8[y + 4, x] = (4 * m4[y, x] + 3) / 64.0 - 0.5;
                m8[y + 4, x + 4] = (4 * m4[y, x] + 1) / 64.0 - 0.5;
            }
        }
        return m8;
    }

    private readonly Rgb24[] _paletteRgb;
    private readonly LinearRgb[] _paletteLin;
    private readonly LabColor[] _paletteLab;
    private readonly double[] _paletteChroma;
    private readonly OkLabColor[] _paletteOkLab;

    public ItemPalette Palette { get; }
    public ColorMetric Metric { get; }
    public double KL { get; }
    public double KC { get; }
    public double KH { get; }

    public ColorMatcher(
        ItemPalette palette,
        ColorMetric metric = ColorMetric.Ciede2000,
        double kL = 1.0,
        double kC = 1.0,
        double kH = 1.0)
    {
        Palette = palette;
        Metric = metric;
        KL = kL;
        KC = kC;
        KH = kH;

        int n = palette.Count;
        _paletteRgb = new Rgb24[n];
        _paletteLin = new LinearRgb[n];
        _paletteLab = new LabColor[n];
        _paletteChroma = new double[n];
        _paletteOkLab = new OkLabColor[n];

        for (int i = 0; i < n; i++)
        {
            var rgb = palette[i].Rgb;
            _paletteRgb[i] = rgb;
            _paletteLin[i] = ColorSpace.RgbToLinear(rgb);
            var lab = ColorSpace.LinearToLab(_paletteLin[i]);
            _paletteLab[i] = lab;
            _paletteChroma[i] = Math.Sqrt(lab.A * lab.A + lab.B * lab.B);
            _paletteOkLab[i] = ColorSpace.LinearToOkLab(_paletteLin[i]);
        }
    }

    public (int BestIndex, double BestDistance) MatchSingle(Rgb24 target)
    {
        int bestIdx = 0;
        double bestDist = double.PositiveInfinity;
        int m = _paletteRgb.Length;

        switch (Metric)
        {
            case ColorMetric.Ciede2000:
            {
                LabColor qLab = ColorSpace.RgbToLab(target);
                double qC = Math.Sqrt(qLab.A * qLab.A + qLab.B * qLab.B);

                // Seed tight upper bound using ultra-fast CIE76 nearest neighbor
                int seedIdx = 0;
                double seedSq = double.PositiveInfinity;
                for (int i = 0; i < m; i++)
                {
                    double dl = qLab.L - _paletteLab[i].L;
                    double da = qLab.A - _paletteLab[i].A;
                    double db = qLab.B - _paletteLab[i].B;
                    double sq = dl * dl + da * da + db * db;
                    if (sq < seedSq) { seedSq = sq; seedIdx = i; }
                }

                bestIdx = seedIdx;
                double bestSq = ColorDistance.Ciede2000SquaredBounded(
                    in qLab, qC, in _paletteLab[seedIdx], _paletteChroma[seedIdx],
                    double.PositiveInfinity, KL, KC, KH);

                // Exact branch-and-bound scan over all palette entries
                for (int i = 0; i < m; i++)
                {
                    if (i == seedIdx) continue;
                    double sq = ColorDistance.Ciede2000SquaredBounded(
                        in qLab, qC, in _paletteLab[i], _paletteChroma[i],
                        bestSq, KL, KC, KH);
                    if (sq < bestSq || (sq == bestSq && i < bestIdx))
                    {
                        bestSq = sq;
                        bestIdx = i;
                    }
                }
                bestDist = Math.Sqrt(bestSq);
                break;
            }
            case ColorMetric.OkLab:
                OkLabColor qOk = ColorSpace.RgbToOkLab(target);
                for (int i = 0; i < m; i++)
                {
                    double d = ColorDistance.OkLabDistance(in qOk, in _paletteOkLab[i]);
                    if (d < bestDist) { bestDist = d; bestIdx = i; }
                }
                break;
            case ColorMetric.Cie76:
                LabColor qLab76 = ColorSpace.RgbToLab(target);
                for (int i = 0; i < m; i++)
                {
                    double d = ColorDistance.Cie76Distance(in qLab76, in _paletteLab[i]);
                    if (d < bestDist) { bestDist = d; bestIdx = i; }
                }
                break;
            case ColorMetric.Redmean:
                for (int i = 0; i < m; i++)
                {
                    double d = ColorDistance.RedmeanDistance(target, _paletteRgb[i]);
                    if (d < bestDist) { bestDist = d; bestIdx = i; }
                }
                break;
            case ColorMetric.Rgb:
                for (int i = 0; i < m; i++)
                {
                    double d = ColorDistance.RgbEuclideanDistance(target, _paletteRgb[i]);
                    if (d < bestDist) { bestDist = d; bestIdx = i; }
                }
                break;
        }

        return (bestIdx, bestDist);
    }

    public MatchResult[] MatchTopK(Rgb24 target, int topK = 5)
    {
        int m = _paletteRgb.Length;
        int k = Math.Clamp(topK, 1, m);
        var scores = new (int Idx, double Dist)[m];

        LabColor? qLab = Metric is ColorMetric.Ciede2000 or ColorMetric.Cie76 ? ColorSpace.RgbToLab(target) : null;
        OkLabColor? qOk = Metric == ColorMetric.OkLab ? ColorSpace.RgbToOkLab(target) : null;

        for (int i = 0; i < m; i++)
        {
            double d = Metric switch
            {
                ColorMetric.Ciede2000 => ColorDistance.Ciede2000(qLab!.Value, in _paletteLab[i], KL, KC, KH),
                ColorMetric.OkLab => ColorDistance.OkLabDistance(qOk!.Value, in _paletteOkLab[i]),
                ColorMetric.Cie76 => ColorDistance.Cie76Distance(qLab!.Value, in _paletteLab[i]),
                ColorMetric.Redmean => ColorDistance.RedmeanDistance(target, _paletteRgb[i]),
                ColorMetric.Rgb => ColorDistance.RgbEuclideanDistance(target, _paletteRgb[i]),
                _ => throw new ArgumentOutOfRangeException()
            };
            scores[i] = (i, d);
        }

        Array.Sort(scores, (a, b) =>
        {
            int cmp = a.Dist.CompareTo(b.Dist);
            return cmp != 0 ? cmp : a.Idx.CompareTo(b.Idx);
        });

        var result = new MatchResult[k];
        for (int i = 0; i < k; i++)
            result[i] = new MatchResult(Palette[scores[i].Idx], scores[i].Idx, scores[i].Dist, Metric);
        return result;
    }

    /// <summary>Deduplicated, multi-threaded exact batch matching for an RGB image buffer.</summary>
    public (int[] Indices, double[] Distances) MatchBatch(ReadOnlySpan<Rgb24> pixels)
    {
        int n = pixels.Length;
        var indices = new int[n];
        var distances = new double[n];
        if (n == 0)
            return (indices, distances);

        // Deduplicate exact sRGB values so repeated pixels are computed only once
        var uniqueMap = new Dictionary<uint, int>(Math.Min(n, 4096));
        var uniqueList = new List<Rgb24>(Math.Min(n, 4096));
        var pixelToUnique = new int[n];

        for (int i = 0; i < n; i++)
        {
            uint key = pixels[i].Packed;
            if (!uniqueMap.TryGetValue(key, out int uIdx))
            {
                uIdx = uniqueList.Count;
                uniqueMap[key] = uIdx;
                uniqueList.Add(pixels[i]);
            }
            pixelToUnique[i] = uIdx;
        }

        int uCount = uniqueList.Count;
        var uIndices = new int[uCount];
        var uDists = new double[uCount];

        Parallel.For(0, uCount, i =>
        {
            var (idx, dist) = MatchSingle(uniqueList[i]);
            uIndices[i] = idx;
            uDists[i] = dist;
        });

        for (int i = 0; i < n; i++)
        {
            int u = pixelToUnique[i];
            indices[i] = uIndices[u];
            distances[i] = uDists[u];
        }

        return (indices, distances);
    }

    public (int[] Indices, double[] Distances) MatchImage(
        ReadOnlySpan<Rgb24> pixels,
        int width,
        int height,
        DitherMode dithering = DitherMode.None,
        double ditherStrength = 1.0)
    {
        double strength = Math.Clamp(ditherStrength, 0.0, 1.0);
        if (dithering == DitherMode.None || strength <= 0.0)
            return MatchBatch(pixels);

        if (dithering is DitherMode.Bayer4 or DitherMode.Bayer8)
        {
            double[,] mat = dithering == DitherMode.Bayer4 ? Bayer4 : Bayer8;
            int mh = mat.GetLength(0);
            int mw = mat.GetLength(1);
            double spread = 24.0 * strength;

            var perturbed = new Rgb24[pixels.Length];
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    double delta = mat[y % mh, x % mw] * spread;
                    var p = pixels[row + x];
                    perturbed[row + x] = new Rgb24(
                        (byte)Math.Clamp((int)Math.Round(p.R + delta), 0, 255),
                        (byte)Math.Clamp((int)Math.Round(p.G + delta), 0, 255),
                        (byte)Math.Clamp((int)Math.Round(p.B + delta), 0, 255));
                }
            }

            var (indices, _) = MatchBatch(perturbed);
            var dists = ComputePairwiseDistances(pixels, indices);
            return (indices, dists);
        }

        return MatchFloydSteinberg(pixels, width, height, strength);
    }

    private (int[] Indices, double[] Distances) MatchFloydSteinberg(
        ReadOnlySpan<Rgb24> pixels,
        int width,
        int height,
        double strength)
    {
        int n = width * height;
        var buf = new LinearRgb[n];
        for (int i = 0; i < n; i++)
            buf[i] = ColorSpace.RgbToLinear(pixels[i]);

        var indices = new int[n];
        for (int y = 0; y < height; y++)
        {
            bool ltr = (y % 2) == 0;
            int step = ltr ? 1 : -1;
            int startX = ltr ? 0 : width - 1;
            int endX = ltr ? width : -1;
            int row = y * width;

            for (int x = startX; x != endX; x += step)
            {
                var oldLin = buf[row + x];
                var clamped = new LinearRgb(
                    Math.Clamp(oldLin.R, 0.0, 1.0),
                    Math.Clamp(oldLin.G, 0.0, 1.0),
                    Math.Clamp(oldLin.B, 0.0, 1.0));
                var oldSrgb = ColorSpace.LinearToRgb(clamped);

                var (idx, _) = MatchSingle(oldSrgb);
                indices[row + x] = idx;

                var chosenLin = _paletteLin[idx];
                double er = Math.Clamp(clamped.R - chosenLin.R, -0.35, 0.35) * strength;
                double eg = Math.Clamp(clamped.G - chosenLin.G, -0.35, 0.35) * strength;
                double eb = Math.Clamp(clamped.B - chosenLin.B, -0.35, 0.35) * strength;

                int nx = x + step;
                if (nx >= 0 && nx < width)
                    AddError(buf, row + nx, er, eg, eb, 7.0 / 16.0);
                if (y + 1 < height)
                {
                    int nextRow = (y + 1) * width;
                    int px = x - step;
                    if (px >= 0 && px < width)
                        AddError(buf, nextRow + px, er, eg, eb, 3.0 / 16.0);
                    AddError(buf, nextRow + x, er, eg, eb, 5.0 / 16.0);
                    if (nx >= 0 && nx < width)
                        AddError(buf, nextRow + nx, er, eg, eb, 1.0 / 16.0);
                }
            }
        }

        var dists = ComputePairwiseDistances(pixels, indices);
        return (indices, dists);
    }

    private static void AddError(LinearRgb[] buf, int idx, double er, double eg, double eb, double w)
    {
        var cur = buf[idx];
        buf[idx] = new LinearRgb(cur.R + er * w, cur.G + eg * w, cur.B + eb * w);
    }

    private double[] ComputePairwiseDistances(ReadOnlySpan<Rgb24> source, int[] indices)
    {
        var dists = new double[indices.Length];
        for (int i = 0; i < indices.Length; i++)
        {
            var s = source[i];
            int idx = indices[i];
            dists[i] = Metric switch
            {
                ColorMetric.Ciede2000 => ColorDistance.Ciede2000(ColorSpace.RgbToLab(s), in _paletteLab[idx], KL, KC, KH),
                ColorMetric.OkLab => ColorDistance.OkLabDistance(ColorSpace.RgbToOkLab(s), in _paletteOkLab[idx]),
                ColorMetric.Cie76 => ColorDistance.Cie76Distance(ColorSpace.RgbToLab(s), in _paletteLab[idx]),
                ColorMetric.Redmean => ColorDistance.RedmeanDistance(s, _paletteRgb[idx]),
                ColorMetric.Rgb => ColorDistance.RgbEuclideanDistance(s, _paletteRgb[idx]),
                _ => 0.0
            };
        }
        return dists;
    }
}
