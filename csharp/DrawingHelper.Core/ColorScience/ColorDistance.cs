using System.Runtime.CompilerServices;
using DrawingHelper.Core.Palette;

namespace DrawingHelper.Core.ColorScience;

public enum ColorMetric
{
    Ciede2000,
    OkLab,
    Cie76,
    Redmean,
    Rgb
}

/// <summary>Zero-allocation perceptual color distance formulas (CIEDE2000, OKLab, CIE76, Redmean, RGB).</summary>
public static class ColorDistance
{
    private const double Pow25_7 = 6103515625.0; // 25^7
    private const double Pi = Math.PI;
    private const double TwoPi = 2.0 * Math.PI;
    private const double Rad6 = 6.0 * Math.PI / 180.0;
    private const double Rad25 = 25.0 * Math.PI / 180.0;
    private const double Rad30 = 30.0 * Math.PI / 180.0;
    private const double Rad60 = 60.0 * Math.PI / 180.0;
    private const double Rad63 = 63.0 * Math.PI / 180.0;
    private const double Rad275 = 275.0 * Math.PI / 180.0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Pow7(double x)
    {
        double x2 = x * x;
        double x4 = x2 * x2;
        return x4 * x2 * x;
    }

    /// <summary>
    /// Exact CIEDE2000 (Delta E 2000) following Sharma, Wu, Dalal (2005).
    /// All angular operations stay in radians for maximum throughput.
    /// </summary>
    public static double Ciede2000(
        in LabColor lab1,
        in LabColor lab2,
        double kL = 1.0,
        double kC = 1.0,
        double kH = 1.0)
    {
        double c1 = Math.Sqrt(lab1.A * lab1.A + lab1.B * lab1.B);
        double c2 = Math.Sqrt(lab2.A * lab2.A + lab2.B * lab2.B);
        double cBar = 0.5 * (c1 + c2);

        double cBar7 = Pow7(cBar);
        double g = 0.5 * (1.0 - Math.Sqrt(cBar7 / (cBar7 + Pow25_7)));

        double a1p = (1.0 + g) * lab1.A;
        double a2p = (1.0 + g) * lab2.A;

        double c1p = Math.Sqrt(a1p * a1p + lab1.B * lab1.B);
        double c2p = Math.Sqrt(a2p * a2p + lab2.B * lab2.B);

        double h1p = (a1p == 0.0 && lab1.B == 0.0) ? 0.0 : Math.Atan2(lab1.B, a1p);
        if (h1p < 0.0) h1p += TwoPi;

        double h2p = (a2p == 0.0 && lab2.B == 0.0) ? 0.0 : Math.Atan2(lab2.B, a2p);
        if (h2p < 0.0) h2p += TwoPi;

        double dLp = lab2.L - lab1.L;
        double dCp = c2p - c1p;

        double cProd = c1p * c2p;
        double dhp;
        if (cProd == 0.0)
        {
            dhp = 0.0;
        }
        else
        {
            double dhDiff = h2p - h1p;
            if (dhDiff > Pi) dhp = dhDiff - TwoPi;
            else if (dhDiff < -Pi) dhp = dhDiff + TwoPi;
            else dhp = dhDiff;
        }

        double dHp = 2.0 * Math.Sqrt(cProd) * Math.Sin(0.5 * dhp);

        double lBarP = 0.5 * (lab1.L + lab2.L);
        double cBarP = 0.5 * (c1p + c2p);

        double hBarP;
        double hSum = h1p + h2p;
        if (cProd == 0.0)
        {
            hBarP = hSum;
        }
        else if (Math.Abs(h1p - h2p) <= Pi)
        {
            hBarP = 0.5 * hSum;
        }
        else if (hSum < TwoPi)
        {
            hBarP = 0.5 * (hSum + TwoPi);
        }
        else
        {
            hBarP = 0.5 * (hSum - TwoPi);
        }

        double t = 1.0
            - 0.17 * Math.Cos(hBarP - Rad30)
            + 0.24 * Math.Cos(2.0 * hBarP)
            + 0.32 * Math.Cos(3.0 * hBarP + Rad6)
            - 0.20 * Math.Cos(4.0 * hBarP - Rad63);

        double u = (hBarP - Rad275) / Rad25;
        double twoDTheta = Rad60 * Math.Exp(-(u * u));
        double cBarP7 = Pow7(cBarP);
        double rC = 2.0 * Math.Sqrt(cBarP7 / (cBarP7 + Pow25_7));

        double lm50 = lBarP - 50.0;
        double lm50Sq = lm50 * lm50;
        double sL = 1.0 + (0.015 * lm50Sq) / Math.Sqrt(20.0 + lm50Sq);
        double sC = 1.0 + 0.045 * cBarP;
        double sH = 1.0 + 0.015 * cBarP * t;
        double rT = -Math.Sin(twoDTheta) * rC;

        double termL = dLp / (kL * sL);
        double termC = dCp / (kC * sC);
        double termH = dHp / (kH * sH);

        double deSq = termL * termL + termC * termC + termH * termH + rT * termC * termH;
        return deSq > 0.0 ? Math.Sqrt(deSq) : 0.0;
    }

    /// <summary>
    /// Mathematically exact branch-and-bound CIEDE2000 returning squared distance,
    /// or double.PositiveInfinity if guaranteed >= maxDistSq.
    /// Proof: |R_T| = R_C * sin(2*dTheta) < 2 * sin(60deg) = sqrt(3), so
    /// termC^2 + termH^2 + R_T*termC*termH >= 0.25 * termC^2 >= 0 always.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Ciede2000SquaredBounded(
        in LabColor lab1,
        double c1,
        in LabColor lab2,
        double c2,
        double maxDistSq,
        double kL = 1.0,
        double kC = 1.0,
        double kH = 1.0)
    {
        // Fast exact lower bound 1: Lightness term alone (zero trig!)
        double dLp = lab2.L - lab1.L;
        double lBarP = 0.5 * (lab1.L + lab2.L);
        double lm50 = lBarP - 50.0;
        double lm50Sq = lm50 * lm50;
        double sL = 1.0 + (0.015 * lm50Sq) / Math.Sqrt(20.0 + lm50Sq);
        double termL = dLp / (kL * sL);
        double termLSq = termL * termL;
        if (termLSq >= maxDistSq)
            return double.PositiveInfinity;

        // Fast exact lower bound 2: Lightness + 0.25 * Chroma term (still zero trig!)
        double cBar = 0.5 * (c1 + c2);
        double cBar7 = Pow7(cBar);
        double g = 0.5 * (1.0 - Math.Sqrt(cBar7 / (cBar7 + Pow25_7)));

        double a1p = (1.0 + g) * lab1.A;
        double a2p = (1.0 + g) * lab2.A;

        double c1p = Math.Sqrt(a1p * a1p + lab1.B * lab1.B);
        double c2p = Math.Sqrt(a2p * a2p + lab2.B * lab2.B);
        double dCp = c2p - c1p;
        double cBarP = 0.5 * (c1p + c2p);
        double sC = 1.0 + 0.045 * cBarP;
        double termC = dCp / (kC * sC);
        double termCSq = termC * termC;
        if (termLSq + 0.25 * termCSq >= maxDistSq)
            return double.PositiveInfinity;

        // Full exact hue & rotation calculation only for surviving candidates
        double h1p = (a1p == 0.0 && lab1.B == 0.0) ? 0.0 : Math.Atan2(lab1.B, a1p);
        if (h1p < 0.0) h1p += TwoPi;

        double h2p = (a2p == 0.0 && lab2.B == 0.0) ? 0.0 : Math.Atan2(lab2.B, a2p);
        if (h2p < 0.0) h2p += TwoPi;

        double cProd = c1p * c2p;
        double dhp;
        if (cProd == 0.0)
        {
            dhp = 0.0;
        }
        else
        {
            double dhDiff = h2p - h1p;
            if (dhDiff > Pi) dhp = dhDiff - TwoPi;
            else if (dhDiff < -Pi) dhp = dhDiff + TwoPi;
            else dhp = dhDiff;
        }

        double dHp = 2.0 * Math.Sqrt(cProd) * Math.Sin(0.5 * dhp);

        double hBarP;
        double hSum = h1p + h2p;
        if (cProd == 0.0)
        {
            hBarP = hSum;
        }
        else if (Math.Abs(h1p - h2p) <= Pi)
        {
            hBarP = 0.5 * hSum;
        }
        else if (hSum < TwoPi)
        {
            hBarP = 0.5 * (hSum + TwoPi);
        }
        else
        {
            hBarP = 0.5 * (hSum - TwoPi);
        }

        double t = 1.0
            - 0.17 * Math.Cos(hBarP - Rad30)
            + 0.24 * Math.Cos(2.0 * hBarP)
            + 0.32 * Math.Cos(3.0 * hBarP + Rad6)
            - 0.20 * Math.Cos(4.0 * hBarP - Rad63);

        double u = (hBarP - Rad275) / Rad25;
        double twoDTheta = Rad60 * Math.Exp(-(u * u));
        double cBarP7 = Pow7(cBarP);
        double rC = 2.0 * Math.Sqrt(cBarP7 / (cBarP7 + Pow25_7));

        double sH = 1.0 + 0.015 * cBarP * t;
        double rT = -Math.Sin(twoDTheta) * rC;
        double termH = dHp / (kH * sH);

        double deSq = termLSq + termCSq + termH * termH + rT * termC * termH;
        return deSq > 0.0 ? deSq : 0.0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double OkLabDistance(in OkLabColor c1, in OkLabColor c2)
    {
        double dl = c1.L - c2.L;
        double da = c1.A - c2.A;
        double db = c1.B - c2.B;
        return Math.Sqrt(dl * dl + da * da + db * db);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Cie76Distance(in LabColor c1, in LabColor c2)
    {
        double dl = c1.L - c2.L;
        double da = c1.A - c2.A;
        double db = c1.B - c2.B;
        return Math.Sqrt(dl * dl + da * da + db * db);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double RedmeanDistance(Rgb24 c1, Rgb24 c2)
    {
        double rBar = 0.5 * (c1.R + c2.R);
        double dr = c1.R - c2.R;
        double dg = c1.G - c2.G;
        double db = c1.B - c2.B;
        return Math.Sqrt(
            (2.0 + rBar / 256.0) * dr * dr +
            4.0 * dg * dg +
            (2.0 + (255.0 - rBar) / 256.0) * db * db);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double RgbEuclideanDistance(Rgb24 c1, Rgb24 c2)
    {
        double dr = c1.R - c2.R;
        double dg = c1.G - c2.G;
        double db = c1.B - c2.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }
}
