using System.Runtime.CompilerServices;
using DrawingHelper.Core.Palette;

namespace DrawingHelper.Core.ColorScience;

public readonly record struct LinearRgb(double R, double G, double B);
public readonly record struct LabColor(double L, double A, double B);
public readonly record struct OkLabColor(double L, double A, double B);

/// <summary>Exact IEC 61966-2-1 sRGB, Linear RGB, CIE XYZ (D65), CIE L*a*b*, and OKLab conversions.</summary>
public static class ColorSpace
{
    private const double D65Xn = 0.95047;
    private const double D65Yn = 1.00000;
    private const double D65Zn = 1.08883;

    private const double LabEpsilon = (6.0 / 29.0) * (6.0 / 29.0) * (6.0 / 29.0); // 216/24389
    private const double LabFactor = (1.0 / 3.0) * (29.0 / 6.0) * (29.0 / 6.0);
    private const double LabOffset = 4.0 / 29.0;

    /// <summary>Precomputed 8-bit sRGB [0..255] -> Linear RGB [0..1] lookup table.</summary>
    private static readonly double[] SrgbByteToLinearLut = BuildLut();

    private static double[] BuildLut()
    {
        var lut = new double[256];
        for (int i = 0; i < 256; i++)
            lut[i] = SrgbUnitToLinear(i / 255.0);
        return lut;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double SrgbByteToLinear(byte value) => SrgbByteToLinearLut[value];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double SrgbUnitToLinear(double c)
    {
        c = Math.Clamp(c, 0.0, 1.0);
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double LinearToSrgbUnit(double c)
    {
        c = Math.Clamp(c, 0.0, 1.0);
        return c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte LinearToSrgbByte(double c) =>
        (byte)Math.Clamp((int)Math.Round(LinearToSrgbUnit(c) * 255.0, MidpointRounding.AwayFromZero), 0, 255);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static LinearRgb RgbToLinear(Rgb24 rgb) =>
        new(SrgbByteToLinearLut[rgb.R], SrgbByteToLinearLut[rgb.G], SrgbByteToLinearLut[rgb.B]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Rgb24 LinearToRgb(LinearRgb lin) =>
        new(LinearToSrgbByte(lin.R), LinearToSrgbByte(lin.G), LinearToSrgbByte(lin.B));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static LabColor RgbToLab(Rgb24 rgb) => LinearToLab(RgbToLinear(rgb));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static LabColor LinearToLab(LinearRgb lin)
    {
        double x = 0.4124564 * lin.R + 0.3575761 * lin.G + 0.1804375 * lin.B;
        double y = 0.2126729 * lin.R + 0.7151522 * lin.G + 0.0721750 * lin.B;
        double z = 0.0193339 * lin.R + 0.1191920 * lin.G + 0.9503041 * lin.B;

        double fx = LabF(x / D65Xn);
        double fy = LabF(y / D65Yn);
        double fz = LabF(z / D65Zn);

        return new LabColor(
            116.0 * fy - 16.0,
            500.0 * (fx - fy),
            200.0 * (fy - fz));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double LabF(double t) =>
        t > LabEpsilon ? Math.Cbrt(t) : LabFactor * t + LabOffset;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static OkLabColor RgbToOkLab(Rgb24 rgb) => LinearToOkLab(RgbToLinear(rgb));

    /// <summary>Exact OKLab conversion (Bjorn Ottosson, 2020).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static OkLabColor LinearToOkLab(LinearRgb lin)
    {
        double l = 0.4122214708 * lin.R + 0.5363325363 * lin.G + 0.0514459929 * lin.B;
        double m = 0.2119034982 * lin.R + 0.6806995451 * lin.G + 0.1073969566 * lin.B;
        double s = 0.0883024619 * lin.R + 0.2817188376 * lin.G + 0.6299787005 * lin.B;

        double l_ = Math.Cbrt(Math.Max(l, 0.0));
        double m_ = Math.Cbrt(Math.Max(m, 0.0));
        double s_ = Math.Cbrt(Math.Max(s, 0.0));

        return new OkLabColor(
            0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_,
            1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_,
            0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_);
    }
}
