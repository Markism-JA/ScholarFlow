using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace ScholarFlow.Desktop.Services;

public sealed record RenderedPdfPixels(byte[] BgraPixels, int Width, int Height);

/// <summary>
/// Renders PDF pages directly via PDFtoImage (SKBitmap) -> Avalonia WriteableBitmap
/// without PNG encode/decode round-trips.
/// </summary>
public static class PdfBitmapRenderer
{
    public static RenderedPdfPixels RenderPagePixels(byte[] pdfBytes, int zeroBasedPage, int dpi)
    {
#pragma warning disable CA1416
        var renderOpts = new PDFtoImage.RenderOptions(Dpi: dpi, BackgroundColor: SKColors.White);
        using var raw = PDFtoImage.Conversion.ToImage(
            pdfBytes,
            page: (Index)zeroBasedPage,
            options: renderOpts
        );
#pragma warning restore CA1416

        if (raw.ColorType == SKColorType.Bgra8888 && raw.AlphaType == SKAlphaType.Premul)
        {
            var pixels = new byte[raw.ByteCount];
            Marshal.Copy(raw.GetPixels(), pixels, 0, pixels.Length);
            return new RenderedPdfPixels(pixels, raw.Width, raw.Height);
        }

        var targetInfo = new SKImageInfo(
            raw.Width,
            raw.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul
        );
        using var converted = new SKBitmap(targetInfo);
        using (var canvas = new SKCanvas(converted))
        {
            canvas.DrawBitmap(raw, 0, 0);
        }
        var convertedPixels = new byte[converted.ByteCount];
        Marshal.Copy(converted.GetPixels(), convertedPixels, 0, convertedPixels.Length);
        return new RenderedPdfPixels(convertedPixels, converted.Width, converted.Height);
    }

    public static WriteableBitmap CreateWriteableBitmap(RenderedPdfPixels data)
    {
        var wb = new WriteableBitmap(
            new PixelSize(data.Width, data.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul
        );
        using var fb = wb.Lock();
        Marshal.Copy(data.BgraPixels, 0, fb.Address, data.BgraPixels.Length);
        return wb;
    }

    public static void CropAndSavePageRegion(
        byte[] pdfBytes,
        int zeroBasedPage,
        ScholarFlow.Core.Quiz.NormalizedHighlightRect region,
        string outputPngPath,
        int dpi = 220
    )
    {
#pragma warning disable CA1416
        var renderOpts = new PDFtoImage.RenderOptions(Dpi: dpi, BackgroundColor: SKColors.White);
        using var pageBmp = PDFtoImage.Conversion.ToImage(
            pdfBytes,
            page: (Index)zeroBasedPage,
            options: renderOpts
        );
#pragma warning restore CA1416

        int x = Math.Clamp(
            (int)Math.Floor(region.X * pageBmp.Width),
            0,
            Math.Max(0, pageBmp.Width - 2)
        );
        int y = Math.Clamp(
            (int)Math.Floor(region.Y * pageBmp.Height),
            0,
            Math.Max(0, pageBmp.Height - 2)
        );
        int w = Math.Clamp((int)Math.Ceiling(region.Width * pageBmp.Width), 2, pageBmp.Width - x);
        int h = Math.Clamp(
            (int)Math.Ceiling(region.Height * pageBmp.Height),
            2,
            pageBmp.Height - y
        );

        var cropRect = new SKRectI(x, y, x + w, y + h);
        using var cropped = new SKBitmap(w, h, pageBmp.ColorType, pageBmp.AlphaType);
        if (!pageBmp.ExtractSubset(cropped, cropRect))
        {
            using var canvas = new SKCanvas(cropped);
            canvas.DrawBitmap(pageBmp, cropRect, new SKRect(0, 0, w, h));
        }

        var dir = Path.GetDirectoryName(outputPngPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        using var image = SKImage.FromBitmap(cropped);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 95);
        using var fs = File.Create(outputPngPath);
        encoded.SaveTo(fs);
    }
}
