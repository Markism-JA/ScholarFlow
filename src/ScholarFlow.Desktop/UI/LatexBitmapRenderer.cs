using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using CSharpMath.SkiaSharp;
using ScholarFlow.Core.Quiz;
using SkiaSharp;

namespace ScholarFlow.Desktop.UI;

/// <summary>
/// Publication-grade TeX math renderer backed by CSharpMath.SkiaSharp (pure C# TeX layout engine).
/// Renders display LaTeX expressions to HiDPI Avalonia Bitmaps (for Quiz Runner / Organizer / Editor)
/// and PNG bytes (for Printable Exam PDFs), with automatic AMS-alias normalization and LRU caching.
/// </summary>
public static class LatexBitmapRenderer
{
    private static readonly ConcurrentDictionary<
        (string Latex, float FontSize, uint ArgbColor),
        byte[]?
    > PngCache = new();

    public static byte[]? TryRenderPngBytes(string rawLatex, float fontSizePt = 20f, uint argbTextColor = 0xFF0F172A)
    {
        if (string.IsNullOrWhiteSpace(rawLatex))
            return null;

        var key = (rawLatex.Trim(), fontSizePt, argbTextColor);
        if (PngCache.TryGetValue(key, out var cached))
            return cached;

        if (PngCache.Count >= 180)
            PngCache.Clear();

        var normalized = LatexMathFormatter.NormalizeForTeXEngine(rawLatex);
        var rendered = RenderWithMathPainter(normalized, fontSizePt, argbTextColor);

        // If CSharpMath rejected an exotic macro, fall back to null so caller displays token-safe Unicode
        PngCache[key] = rendered;
        return rendered;
    }

    public static Bitmap? TryRenderAvaloniaBitmap(string rawLatex, float fontSizePt, uint argbTextColor)
    {
        var pngBytes = TryRenderPngBytes(rawLatex, fontSizePt, argbTextColor);
        if (pngBytes is null || pngBytes.Length == 0)
            return null;

        try
        {
            using var ms = new MemoryStream(pngBytes, writable: false);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? RenderWithMathPainter(string latex, float fontSizePt, uint argbTextColor)
    {
        if (string.IsNullOrWhiteSpace(latex))
            return null;

        try
        {
            // Render at 2x HiDPI scale for crisp retina/desktop & PDF print output
            const float hiDpiScale = 2.0f;
            var painter = new MathPainter
            {
                LaTeX = latex,
                FontSize = fontSizePt * hiDpiScale,
                TextColor = new SKColor(argbTextColor),
                LineStyle = CSharpMath.Atom.LineStyle.Display,
                AntiAlias = true,
            };

            if (!string.IsNullOrWhiteSpace(painter.ErrorMessage))
                return null;

            var bounds = painter.Measure();
            if (bounds.Width <= 0.5f || bounds.Height <= 0.5f)
                return null;

            const float pad = 10f;
            int bmpW = Math.Clamp((int)Math.Ceiling(bounds.Width + (pad * 2)), 16, 4096);
            int bmpH = Math.Clamp((int)Math.Ceiling(bounds.Height + (pad * 2)), 16, 2048);

            var info = new SKImageInfo(bmpW, bmpH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface is null)
                return null;

            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            // CSharpMath draws with origin at the math baseline; translate using measured bounds
            painter.Draw(
                canvas,
                new SKPoint(pad - bounds.Left, pad - bounds.Top)
            );

            using var snapshot = surface.Snapshot();
            using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            return data?.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
