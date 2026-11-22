using System.Collections.Concurrent;
using System.Text;
using Avalonia.Media.Imaging;
using ScholarFlow.Core.Quiz;
using SkiaSharp;
using Svg.Skia;

namespace ScholarFlow.Desktop.UI;

/// <summary>
/// Pure-.NET Mermaid diagram rasterizer backed by Mermaider (SVG) + Svg.Skia (SkiaSharp).
/// Renders crisp 2x HiDPI Avalonia Bitmaps for Desktop Dark/Light themes and high-DPI PNG bytes
/// for Printable Exam PDFs, with LRU caching and zero Node.js/browser dependencies.
/// </summary>
public static class MermaidBitmapRenderer
{
    private static readonly ConcurrentDictionary<
        (string Spec, MermaidThemePreset Preset, float Scale),
        byte[]?
    > PngCache = new();

    public static byte[]? TryRenderPngBytes(
        string rawDiagram,
        bool isDarkTheme,
        bool forPrint = false
    )
    {
        if (string.IsNullOrWhiteSpace(rawDiagram))
            return null;

        var preset = forPrint
            ? MermaidThemePreset.Print
            : (isDarkTheme ? MermaidThemePreset.Dark : MermaidThemePreset.Light);
        float scale = forPrint ? 2.5f : 2.0f;

        var key = (rawDiagram.Trim(), preset, scale);
        if (PngCache.TryGetValue(key, out var cached))
            return cached;

        if (PngCache.Count >= 160)
            PngCache.Clear();

        var bytes = RenderInternal(rawDiagram, preset, scale);
        PngCache[key] = bytes;
        return bytes;
    }

    public static Bitmap? TryRenderAvaloniaBitmap(string rawDiagram, bool isDarkTheme)
    {
        var pngBytes = TryRenderPngBytes(rawDiagram, isDarkTheme, forPrint: false);
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

    private static byte[]? RenderInternal(
        string rawDiagram,
        MermaidThemePreset preset,
        float scale
    )
    {
        if (!MermaidDiagramService.TryRenderSvg(rawDiagram, preset, out var svgMarkup))
            return null;

        try
        {
            using var svgStream = new MemoryStream(Encoding.UTF8.GetBytes(svgMarkup));
            using var svg = new SKSvg();
            var picture = svg.Load(svgStream);
            if (picture is null)
                return null;

            var cull = picture.CullRect;
            if (cull.Width <= 1f || cull.Height <= 1f)
                return null;

            int bmpW = Math.Clamp((int)Math.Ceiling(cull.Width * scale), 32, 4096);
            int bmpH = Math.Clamp((int)Math.Ceiling(cull.Height * scale), 32, 4096);
            float effectiveScaleX = bmpW / cull.Width;
            float effectiveScaleY = bmpH / cull.Height;
            float uniformScale = Math.Min(effectiveScaleX, effectiveScaleY);

            int finalW = Math.Max(32, (int)Math.Ceiling(cull.Width * uniformScale));
            int finalH = Math.Max(32, (int)Math.Ceiling(cull.Height * uniformScale));

            var info = new SKImageInfo(finalW, finalH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface is null)
                return null;

            var canvas = surface.Canvas;
            var bgHex = preset == MermaidThemePreset.Dark ? "#14161B" : "#FFFFFF";
            canvas.Clear(SKColor.Parse(bgHex));
            canvas.Scale(uniformScale);
            canvas.Translate(-cull.Left, -cull.Top);
            canvas.DrawPicture(picture);
            canvas.Flush();

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data?.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
