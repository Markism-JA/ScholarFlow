using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;

namespace ScholarFlow.Core.Knowledge;

public sealed record PackedImagePage(int PageNumber, string FilePath, string FileName);

public sealed record ImagePackResult(
    IReadOnlyList<PackedImagePage> Pages,
    IReadOnlyList<string> Skipped
);

/// <summary>
/// Prepares course images for AI quizzes: (1) Manual Web UI mode packs images 1-per-page into a single
/// uploadable PDF with a page manifest, (2) API mode encodes images as base64 data URIs for vision models.
/// </summary>
public static class ImagePacketPacker
{
    private const long MaxDataUriBytes = 8L * 1024 * 1024;

    // PdfSharpCore (ImageSharp backend) reliably embeds these; webp/gif/bmp are skipped for the PDF packet.
    private static readonly HashSet<string> PdfPackableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
    };

    private static readonly Dictionary<string, string> MimeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp",
    };

    public static bool IsImage(string path) =>
        StudySourceClassifier.Classify(path).Kind == StudySourceKind.Image;

    public static ImagePackResult PackImagesToPdf(IEnumerable<string> imagePaths, string outputPdfPath)
    {
        var pages = new List<PackedImagePage>();
        var skipped = new List<string>();

        using var doc = new PdfDocument();
        foreach (var path in imagePaths)
        {
            var name = Path.GetFileName(path);
            if (!File.Exists(path) || !PdfPackableExtensions.Contains(Path.GetExtension(path)))
            {
                skipped.Add(name);
                continue;
            }

            try
            {
                using var img = XImage.FromFile(path);
                var page = doc.AddPage();

                // Orient the page to the image, cap the long edge at A4-ish size, keep aspect ratio.
                double iw = Math.Max(1, img.PixelWidth);
                double ih = Math.Max(1, img.PixelHeight);
                double scale = Math.Min(1.0, 842.0 / Math.Max(iw, ih));
                page.Width = XUnit.FromPoint(Math.Max(120, iw * scale));
                page.Height = XUnit.FromPoint(Math.Max(120, ih * scale));

                using var gfx = XGraphics.FromPdfPage(page);
                gfx.DrawImage(img, 0, 0, page.Width.Point, page.Height.Point);
                pages.Add(new PackedImagePage(pages.Count + 1, path, name));
            }
            catch
            {
                skipped.Add(name);
            }
        }

        if (pages.Count == 0)
            return new ImagePackResult(pages, skipped);

        var dir = Path.GetDirectoryName(outputPdfPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
        doc.Save(outputPdfPath);
        return new ImagePackResult(pages, skipped);
    }

    /// <summary>
    /// Combines scoped pages from a source PDF with 1-per-page packed images into a single uploadable PDF
    /// for Manual Web UI mixed-source mode.
    /// </summary>
    public static ImagePackResult PackScopedPdfAndImages(
        string sourcePdfPath,
        IReadOnlyList<int> oneBasedPdfPages,
        IEnumerable<string> imagePaths,
        string outputPdfPath
    )
    {
        var pages = new List<PackedImagePage>();
        var skipped = new List<string>();

        using var doc = new PdfDocument();
        if (!string.IsNullOrWhiteSpace(sourcePdfPath) && File.Exists(sourcePdfPath) && oneBasedPdfPages.Count > 0)
        {
            using var input = PdfSharpCore.Pdf.IO.PdfReader.Open(
                sourcePdfPath,
                PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Import
            );
            foreach (int p in oneBasedPdfPages)
            {
                if (p >= 1 && p <= input.PageCount)
                    doc.AddPage(input.Pages[p - 1]);
            }
        }

        foreach (var path in imagePaths)
        {
            var name = Path.GetFileName(path);
            if (!File.Exists(path) || !PdfPackableExtensions.Contains(Path.GetExtension(path)))
            {
                skipped.Add(name);
                continue;
            }

            try
            {
                using var img = XImage.FromFile(path);
                var page = doc.AddPage();
                double iw = Math.Max(1, img.PixelWidth);
                double ih = Math.Max(1, img.PixelHeight);
                double scale = Math.Min(1.0, 842.0 / Math.Max(iw, ih));
                page.Width = XUnit.FromPoint(Math.Max(120, iw * scale));
                page.Height = XUnit.FromPoint(Math.Max(120, ih * scale));

                using var gfx = XGraphics.FromPdfPage(page);
                gfx.DrawImage(img, 0, 0, page.Width.Point, page.Height.Point);
                pages.Add(new PackedImagePage(pages.Count + 1, path, name));
            }
            catch
            {
                skipped.Add(name);
            }
        }

        if (doc.PageCount == 0)
            return new ImagePackResult(pages, skipped);

        var dir = Path.GetDirectoryName(outputPdfPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
        doc.Save(outputPdfPath);
        return new ImagePackResult(pages, skipped);
    }

    /// <summary>Returns null when the file is missing, not an image, or larger than the safe payload cap.</summary>
    public static string? BuildDataUri(string imagePath)
    {
        try
        {
            if (!File.Exists(imagePath) || !MimeByExtension.TryGetValue(Path.GetExtension(imagePath), out var mime))
                return null;
            if (new FileInfo(imagePath).Length > MaxDataUriBytes)
                return null;
            return $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(imagePath))}";
        }
        catch
        {
            return null;
        }
    }
}
