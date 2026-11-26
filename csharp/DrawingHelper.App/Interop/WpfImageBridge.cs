using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingHelper.Core.ColorScience;
using DrawingHelper.Core.Palette;

namespace DrawingHelper.App.Interop;

/// <summary>Zero-dependency WPF image codec bridge between BitmapSource and DrawingHelper.Core Rgb24[].</summary>
public static class WpfImageBridge
{
    public static (Rgb24[] Pixels, int Width, int Height, BitmapSource Preview) LoadImageFile(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();

        var bgra = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth;
        int h = bgra.PixelHeight;
        int stride = w * 4;
        var raw = new byte[stride * h];
        bgra.CopyPixels(raw, stride, 0);

        var pixels = new Rgb24[w * h];
        for (int i = 0; i < pixels.Length; i++)
        {
            int o = i * 4;
            byte b = raw[o];
            byte g = raw[o + 1];
            byte r = raw[o + 2];
            byte a = raw[o + 3];

            if (a == 255)
            {
                pixels[i] = new Rgb24(r, g, b);
            }
            else if (a == 0)
            {
                pixels[i] = new Rgb24(255, 255, 255);
            }
            else
            {
                // Alpha composite over white in linear light
                double af = a / 255.0;
                double lr = ColorSpace.SrgbByteToLinear(r) * af + (1.0 - af);
                double lg = ColorSpace.SrgbByteToLinear(g) * af + (1.0 - af);
                double lb = ColorSpace.SrgbByteToLinear(b) * af + (1.0 - af);
                pixels[i] = ColorSpace.LinearToRgb(new LinearRgb(lr, lg, lb));
            }
        }

        return (pixels, w, h, bmp);
    }

    public static WriteableBitmap CreateBitmapFromRgb(ReadOnlySpan<Rgb24> pixels, int width, int height)
    {
        var wb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr24, null);
        var buf = new byte[width * height * 3];
        for (int i = 0; i < pixels.Length; i++)
        {
            int o = i * 3;
            buf[o] = pixels[i].B;
            buf[o + 1] = pixels[i].G;
            buf[o + 2] = pixels[i].R;
        }
        wb.WritePixels(new Int32Rect(0, 0, width, height), buf, width * 3, 0);
        wb.Freeze();
        return wb;
    }

    public static void SaveRgbAsPng(string path, ReadOnlySpan<Rgb24> pixels, int width, int height)
    {
        var wb = CreateBitmapFromRgb(pixels, width, height);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(wb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
