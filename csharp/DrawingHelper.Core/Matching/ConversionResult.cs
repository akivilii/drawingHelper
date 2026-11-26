using DrawingHelper.Core.ColorScience;
using DrawingHelper.Core.Palette;

namespace DrawingHelper.Core.Matching;

public sealed record ConversionOptions(
    int? Width = 64,
    int? Height = 64,
    ResizeMode ResizeMode = ResizeMode.Fit,
    ResampleMode ResampleMode = ResampleMode.Box,
    ColorMetric Metric = ColorMetric.Ciede2000,
    DitherMode Dithering = DitherMode.None,
    double DitherStrength = 1.0,
    double KL = 1.0,
    double KC = 1.0,
    double KH = 1.0);

public readonly record struct ColorUsage(PaletteColor Color, int Count);

public readonly record struct TileBounds(int X0, int Y0, int X1, int Y1)
{
    public int Width => X1 - X0;
    public int Height => Y1 - Y0;
}

/// <summary>Result of converting an image into a Minecraft item-palette pixel grid.</summary>
public sealed class ConversionResult
{
    public int Width { get; }
    public int Height { get; }
    public Rgb24[] SourceResized { get; }
    public Rgb24[] PixelRgb { get; }
    public int[] PaletteIndices { get; }
    public double[] Distances { get; }
    public double[] Ciede2000Errors { get; }
    public ItemPalette Palette { get; }
    public ColorMetric Metric { get; }
    public DitherMode Dithering { get; }

    public double MeanDeltaE2000 { get; }
    public double P95DeltaE2000 { get; }
    public double MaxDeltaE2000 { get; }

    public ConversionResult(
        int width,
        int height,
        Rgb24[] sourceResized,
        Rgb24[] pixelRgb,
        int[] paletteIndices,
        double[] distances,
        double[] ciede2000Errors,
        ItemPalette palette,
        ColorMetric metric,
        DitherMode dithering)
    {
        Width = width;
        Height = height;
        SourceResized = sourceResized;
        PixelRgb = pixelRgb;
        PaletteIndices = paletteIndices;
        Distances = distances;
        Ciede2000Errors = ciede2000Errors;
        Palette = palette;
        Metric = metric;
        Dithering = dithering;

        if (ciede2000Errors.Length > 0)
        {
            double sum = 0;
            double max = 0;
            for (int i = 0; i < ciede2000Errors.Length; i++)
            {
                double v = ciede2000Errors[i];
                sum += v;
                if (v > max) max = v;
            }
            MeanDeltaE2000 = sum / ciede2000Errors.Length;
            MaxDeltaE2000 = max;

            var sorted = (double[])ciede2000Errors.Clone();
            Array.Sort(sorted);
            int p95Idx = Math.Clamp((int)Math.Floor(0.95 * (sorted.Length - 1)), 0, sorted.Length - 1);
            P95DeltaE2000 = sorted[p95Idx];
        }
    }

    public (int Cols, int Rows) TileGridShape(int tileSize = 32)
    {
        if (tileSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(tileSize));
        return ((Width + tileSize - 1) / tileSize, (Height + tileSize - 1) / tileSize);
    }

    public (ConversionResult Tile, TileBounds Bounds) SliceTile(int tileCol, int tileRow, int tileSize = 32)
    {
        var (cols, rows) = TileGridShape(tileSize);
        if (tileCol < 0 || tileCol >= cols || tileRow < 0 || tileRow >= rows)
            throw new ArgumentOutOfRangeException($"Tile ({tileCol}, {tileRow}) out of range ({cols}x{rows}).");

        int x0 = tileCol * tileSize;
        int y0 = tileRow * tileSize;
        int x1 = Math.Min(x0 + tileSize, Width);
        int y1 = Math.Min(y0 + tileSize, Height);
        int tw = x1 - x0;
        int th = y1 - y0;
        int n = tw * th;

        var src = new Rgb24[n];
        var pix = new Rgb24[n];
        var idxs = new int[n];
        var dists = new double[n];
        var de00 = new double[n];

        for (int ly = 0; ly < th; ly++)
        {
            int srcOff = (y0 + ly) * Width + x0;
            int dstOff = ly * tw;
            Array.Copy(SourceResized, srcOff, src, dstOff, tw);
            Array.Copy(PixelRgb, srcOff, pix, dstOff, tw);
            Array.Copy(PaletteIndices, srcOff, idxs, dstOff, tw);
            Array.Copy(Distances, srcOff, dists, dstOff, tw);
            Array.Copy(Ciede2000Errors, srcOff, de00, dstOff, tw);
        }

        var sub = new ConversionResult(tw, th, src, pix, idxs, dists, de00, Palette, Metric, Dithering);
        return (sub, new TileBounds(x0, y0, x1, y1));
    }

    public List<ColorUsage> GetColorCounts()
    {
        var counts = new int[Palette.Count];
        for (int i = 0; i < PaletteIndices.Length; i++)
        {
            int idx = PaletteIndices[i];
            if (idx >= 0) counts[idx]++;
        }

        var list = new List<ColorUsage>();
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] > 0)
                list.Add(new ColorUsage(Palette[i], counts[i]));
        }
        list.Sort((a, b) => b.Count.CompareTo(a.Count));
        return list;
    }

    public int DistinctItemCount()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < PaletteIndices.Length; i++)
        {
            int idx = PaletteIndices[i];
            if (idx >= 0) set.Add(Palette[idx].ItemName);
        }
        return set.Count;
    }
}

public static class ImageConverterEngine
{
    public static ConversionResult Convert(
        ReadOnlySpan<Rgb24> sourcePixels,
        int srcW,
        int srcH,
        ItemPalette palette,
        ConversionOptions? options = null)
    {
        options ??= new ConversionOptions();
        var (resized, dstW, dstH) = LinearResampler.Resize(
            sourcePixels,
            srcW,
            srcH,
            options.Width,
            options.Height,
            options.ResizeMode,
            options.ResampleMode);

        var matcher = new ColorMatcher(palette, options.Metric, options.KL, options.KC, options.KH);
        var (indices, dists) = matcher.MatchImage(
            resized, dstW, dstH, options.Dithering, options.DitherStrength);

        int n = dstW * dstH;
        var pixelRgb = new Rgb24[n];
        var de00 = new double[n];

        // Precompute palette Labs once for objective CIEDE2000 error
        var palLabs = new LabColor[palette.Count];
        for (int i = 0; i < palette.Count; i++)
            palLabs[i] = ColorSpace.RgbToLab(palette[i].Rgb);

        Parallel.For(0, n, i =>
        {
            int idx = indices[i];
            pixelRgb[i] = palette[idx].Rgb;
            if (options.Metric == ColorMetric.Ciede2000 &&
                options.KL == 1.0 && options.KC == 1.0 && options.KH == 1.0)
            {
                de00[i] = dists[i];
            }
            else
            {
                var sLab = ColorSpace.RgbToLab(resized[i]);
                de00[i] = ColorDistance.Ciede2000(in sLab, in palLabs[idx]);
            }
        });

        return new ConversionResult(
            dstW, dstH, resized, pixelRgb, indices, dists, de00, palette, options.Metric, options.Dithering);
    }
}
