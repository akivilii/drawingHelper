using DrawingHelper.Core.ColorScience;
using DrawingHelper.Core.Palette;

namespace DrawingHelper.Core.Matching;

public enum ResizeMode
{
    Fit,
    Stretch,
    Cover
}

public enum ResampleMode
{
    Box,
    Bilinear,
    Nearest
}

/// <summary>Gamma-correct linear-light image resampler (zero external dependencies).</summary>
public static class LinearResampler
{
    public static (int Width, int Height) ComputeTargetSize(
        int srcW,
        int srcH,
        int? width,
        int? height,
        ResizeMode mode = ResizeMode.Fit)
    {
        if (width is null && height is null)
            return (srcW, srcH);
        if (width is <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
        if (height is <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");

        if (width is not null && height is null)
            return (width.Value, Math.Max(1, (int)Math.Round(srcH * (width.Value / (double)srcW))));
        if (height is not null && width is null)
            return (Math.Max(1, (int)Math.Round(srcW * (height.Value / (double)srcH))), height.Value);

        int w = width!.Value;
        int h = height!.Value;
        return mode switch
        {
            ResizeMode.Stretch or ResizeMode.Cover => (w, h),
            ResizeMode.Fit => (
                Math.Max(1, (int)Math.Round(srcW * Math.Min(w / (double)srcW, h / (double)srcH))),
                Math.Max(1, (int)Math.Round(srcH * Math.Min(w / (double)srcW, h / (double)srcH)))
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    /// <summary>
    /// Resize packed Rgb24 buffer in linear-light RGB space so downsampling preserves
    /// physically accurate average brightness without gamma darkening.
    /// </summary>
    public static (Rgb24[] Pixels, int Width, int Height) Resize(
        ReadOnlySpan<Rgb24> source,
        int srcW,
        int srcH,
        int? width,
        int? height,
        ResizeMode mode = ResizeMode.Fit,
        ResampleMode resample = ResampleMode.Box)
    {
        int cropX = 0, cropY = 0, cropW = srcW, cropH = srcH;
        if (mode == ResizeMode.Cover && width is not null && height is not null)
        {
            double srcRatio = srcW / (double)srcH;
            double dstRatio = width.Value / (double)height.Value;
            if (srcRatio > dstRatio)
            {
                cropW = Math.Max(1, (int)Math.Round(srcH * dstRatio));
                cropX = (srcW - cropW) / 2;
            }
            else if (srcRatio < dstRatio)
            {
                cropH = Math.Max(1, (int)Math.Round(srcW / dstRatio));
                cropY = (srcH - cropH) / 2;
            }
        }

        var (dstW, dstH) = ComputeTargetSize(cropW, cropH, width, height, mode);
        var output = new Rgb24[dstW * dstH];

        if (cropW == dstW && cropH == dstH)
        {
            for (int y = 0; y < dstH; y++)
            {
                source.Slice((cropY + y) * srcW + cropX, dstW)
                    .CopyTo(output.AsSpan(y * dstW, dstW));
            }
            return (output, dstW, dstH);
        }

        if (resample == ResampleMode.Nearest)
        {
            for (int y = 0; y < dstH; y++)
            {
                int sy = cropY + Math.Min(cropH - 1, (int)((y + 0.5) * cropH / dstH));
                int rowOff = y * dstW;
                int srcRowOff = sy * srcW;
                for (int x = 0; x < dstW; x++)
                {
                    int sx = cropX + Math.Min(cropW - 1, (int)((x + 0.5) * cropW / dstW));
                    output[rowOff + x] = source[srcRowOff + sx];
                }
            }
            return (output, dstW, dstH);
        }

        // Convert cropped region to linear RGB once
        var linBuf = new LinearRgb[cropW * cropH];
        for (int y = 0; y < cropH; y++)
        {
            int srcRow = (cropY + y) * srcW + cropX;
            int dstRow = y * cropW;
            for (int x = 0; x < cropW; x++)
                linBuf[dstRow + x] = ColorSpace.RgbToLinear(source[srcRow + x]);
        }

        if (resample == ResampleMode.Bilinear || (dstW > cropW && dstH > cropH))
        {
            ResizeLinearBilinear(linBuf, cropW, cropH, output, dstW, dstH);
        }
        else
        {
            ResizeLinearBoxArea(linBuf, cropW, cropH, output, dstW, dstH);
        }

        return (output, dstW, dstH);
    }

    private static void ResizeLinearBoxArea(
        LinearRgb[] src,
        int srcW,
        int srcH,
        Rgb24[] dst,
        int dstW,
        int dstH)
    {
        double scaleX = srcW / (double)dstW;
        double scaleY = srcH / (double)dstH;

        Parallel.For(0, dstH, dy =>
        {
            double fy0 = dy * scaleY;
            double fy1 = (dy + 1) * scaleY;
            int iy0 = Math.Max(0, (int)Math.Floor(fy0));
            int iy1 = Math.Min(srcH - 1, (int)Math.Ceiling(fy1) - 1);
            if (iy1 < iy0) iy1 = iy0;

            int dstRow = dy * dstW;
            for (int dx = 0; dx < dstW; dx++)
            {
                double fx0 = dx * scaleX;
                double fx1 = (dx + 1) * scaleX;
                int ix0 = Math.Max(0, (int)Math.Floor(fx0));
                int ix1 = Math.Min(srcW - 1, (int)Math.Ceiling(fx1) - 1);
                if (ix1 < ix0) ix1 = ix0;

                double sumR = 0, sumG = 0, sumB = 0, sumW = 0;
                for (int sy = iy0; sy <= iy1; sy++)
                {
                    double wy = Math.Min(fy1, sy + 1.0) - Math.Max(fy0, sy);
                    if (wy <= 0) wy = 1.0;
                    int sRow = sy * srcW;

                    for (int sx = ix0; sx <= ix1; sx++)
                    {
                        double wx = Math.Min(fx1, sx + 1.0) - Math.Max(fx0, sx);
                        if (wx <= 0) wx = 1.0;
                        double w = wx * wy;
                        var p = src[sRow + sx];
                        sumR += p.R * w;
                        sumG += p.G * w;
                        sumB += p.B * w;
                        sumW += w;
                    }
                }

                double inv = sumW > 0 ? 1.0 / sumW : 1.0;
                dst[dstRow + dx] = ColorSpace.LinearToRgb(new LinearRgb(sumR * inv, sumG * inv, sumB * inv));
            }
        });
    }

    private static void ResizeLinearBilinear(
        LinearRgb[] src,
        int srcW,
        int srcH,
        Rgb24[] dst,
        int dstW,
        int dstH)
    {
        double scaleX = srcW / (double)dstW;
        double scaleY = srcH / (double)dstH;

        Parallel.For(0, dstH, dy =>
        {
            double gy = (dy + 0.5) * scaleY - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(gy), 0, srcH - 1);
            int y1 = Math.Clamp(y0 + 1, 0, srcH - 1);
            double wy = Math.Clamp(gy - y0, 0.0, 1.0);

            int dstRow = dy * dstW;
            for (int dx = 0; dx < dstW; dx++)
            {
                double gx = (dx + 0.5) * scaleX - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(gx), 0, srcW - 1);
                int x1 = Math.Clamp(x0 + 1, 0, srcW - 1);
                double wx = Math.Clamp(gx - x0, 0.0, 1.0);

                var c00 = src[y0 * srcW + x0];
                var c10 = src[y0 * srcW + x1];
                var c01 = src[y1 * srcW + x0];
                var c11 = src[y1 * srcW + x1];

                double w00 = (1 - wx) * (1 - wy);
                double w10 = wx * (1 - wy);
                double w01 = (1 - wx) * wy;
                double w11 = wx * wy;

                dst[dstRow + dx] = ColorSpace.LinearToRgb(new LinearRgb(
                    c00.R * w00 + c10.R * w10 + c01.R * w01 + c11.R * w11,
                    c00.G * w00 + c10.G * w10 + c01.G * w01 + c11.G * w11,
                    c00.B * w00 + c10.B * w10 + c01.B * w01 + c11.B * w11));
            }
        });
    }
}
