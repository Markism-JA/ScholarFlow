using System.Text.RegularExpressions;
using ScholarFlow.Core.Quiz;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace ScholarFlow.Core.Pdf;

/// <summary>
/// Detects weak/missing embedded PDF outlines and synthesizes a virtual section outline
/// using PdfPig font-height analysis + structural heading heuristics (filtering out repeated
/// running headers/footers).
/// </summary>
public static partial class PdfSmartSectionDetector
{
    [GeneratedRegex(@"^(?:Slide|Page|Sheet|Img|Image|Frame)\s*#?\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex DegenerateBookmarkRegex();

    [GeneratedRegex(
        @"^(?:Chapter|Module|Unit|Lesson|Lecture|Part|Week|Topic|Section|Appendix)\s+[A-Z0-9IVX]+(?:[\s:\.\-—]+.{0,90})?$",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex ExplicitHeadingRegex();

    [GeneratedRegex(@"^\d+(?:\.\d+){0,2}\s+[A-Z][A-Za-z0-9\s,\-–—:&/()]{2,80}$")]
    private static partial Regex NumberedHeadingRegex();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex PureNumberRegex();

    public static bool IsWeakOrMissingOutline(
        IReadOnlyList<PdfOutlineSectionInfo> outline,
        int totalPages
    )
    {
        if (totalPages <= 0)
            return false;
        if (outline.Count == 0)
            return true;
        if (outline.Count == 1 && totalPages >= 6)
            return true;

        int degenerateCount = outline.Count(s => DegenerateBookmarkRegex().IsMatch(s.Title.Trim()));
        return degenerateCount * 4 >= outline.Count * 3;
    }

    public static List<PdfOutlineSectionInfo> DetectSections(
        string pdfPath,
        int maxPagesToScan = 250
    )
    {
        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            return [];

        using var doc = PdfDocument.Open(pdfPath);
        int totalPages = doc.NumberOfPages;
        if (totalPages == 0)
            return [];

        int pagesToScan = Math.Min(totalPages, Math.Max(1, maxPagesToScan));

        // Pass 1: Extract top-of-page lines and compute median body font height
        var allFontHeights = new List<double>(4096);
        var pageLines = new List<(int Page, List<LineCandidate> Lines)>(pagesToScan);
        var lineFrequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int p = 1; p <= pagesToScan; p++)
        {
            var page = doc.GetPage(p);
            double pageHeight = page.Height > 0 ? page.Height : 792.0;
            var words = page.GetWords().ToList();
            if (words.Count == 0)
                continue;

            foreach (var w in words)
            {
                double h = GetWordFontHeight(w);
                if (h is > 3 and < 96)
                    allFontHeights.Add(h);
            }

            var lines = GroupWordsIntoLines(words, pageHeight);
            pageLines.Add((p, lines));

            foreach (var normalized in lines.Select(l => NormalizeKey(l.Text)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (normalized.Length >= 3)
                    lineFrequency[normalized] = lineFrequency.GetValueOrDefault(normalized, 0) + 1;
            }
        }

        if (allFontHeights.Count == 0 || pageLines.Count == 0)
            return [];

        allFontHeights.Sort();
        double medianBodyHeight = allFontHeights[allFontHeights.Count / 2];
        int repeatCutoff = Math.Max(3, (int)Math.Ceiling(pagesToScan * 0.22));

        bool IsRunningHeaderOrNoise(string text)
        {
            var t = text.Trim();
            if (t.Length < 4 || t.Length > 110)
                return true;
            if (PureNumberRegex().IsMatch(t))
                return true;
            if (t.StartsWith("Figure ", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("Table ", StringComparison.OrdinalIgnoreCase))
                return true;
            var key = NormalizeKey(t);
            return lineFrequency.GetValueOrDefault(key, 0) >= repeatCutoff;
        }

        // Pass 2: Tier 1 explicit structural headings (Chapter/Module/Unit/1.2 Title)
        var tier1 = new List<(int Page, string Title, int Level)>();
        foreach (var (p, lines) in pageLines)
        {
            foreach (var line in lines)
            {
                if (line.TopFraction > 0.55 || IsRunningHeaderOrNoise(line.Text))
                    continue;

                if (ExplicitHeadingRegex().IsMatch(line.Text) && line.AvgHeight >= medianBodyHeight * 1.05)
                {
                    tier1.Add((p, CleanTitle(line.Text), 1));
                    break;
                }

                if (NumberedHeadingRegex().IsMatch(line.Text) && line.AvgHeight >= medianBodyHeight * 1.15)
                {
                    int level = line.Text.Contains('.') ? 2 : 1;
                    tier1.Add((p, CleanTitle(line.Text), level));
                    break;
                }
            }
        }

        List<(int Page, string Title, int Level)> chosen = tier1;

        // Pass 3: Tier 2 dominant-font title lines (great for slide decks / lecture handouts)
        if (chosen.Count < 2)
        {
            var tier2 = new List<(int Page, string Title, int Level)>();
            foreach (var (p, lines) in pageLines)
            {
                var best = lines
                    .Where(l =>
                        l.TopFraction <= 0.35
                        && l.AvgHeight >= medianBodyHeight * 1.32
                        && !IsRunningHeaderOrNoise(l.Text)
                    )
                    .OrderByDescending(l => l.AvgHeight)
                    .ThenBy(l => l.TopFraction)
                    .FirstOrDefault();

                if (best is not null)
                {
                    // Avoid repeating the exact same title on consecutive pages
                    if (
                        tier2.Count == 0
                        || !string.Equals(tier2[^1].Title, CleanTitle(best.Text), StringComparison.OrdinalIgnoreCase)
                    )
                    {
                        tier2.Add((p, CleanTitle(best.Text), 1));
                    }
                }
            }

            if (tier2.Count > chosen.Count)
                chosen = tier2;
        }

        if (chosen.Count == 0)
            return [];

        // Convert detected heading start pages into contiguous PdfOutlineSectionInfo spans
        var sections = new List<PdfOutlineSectionInfo>(chosen.Count);
        for (int i = 0; i < chosen.Count; i++)
        {
            var (startPage, title, level) = chosen[i];
            int endPage =
                i + 1 < chosen.Count
                    ? Math.Max(startPage, chosen[i + 1].Page - 1)
                    : totalPages;
            sections.Add(new PdfOutlineSectionInfo(title, "Auto", level, startPage, endPage));
        }

        return sections;
    }

    private sealed record LineCandidate(string Text, double AvgHeight, double TopFraction);

    private static List<LineCandidate> GroupWordsIntoLines(List<Word> words, double pageHeight)
    {
        // PdfPig Y increases upward; sort descending by Y (top to bottom), then ascending by X (left to right)
        var ordered = words
            .OrderByDescending(w => w.BoundingBox.Bottom)
            .ThenBy(w => w.BoundingBox.Left)
            .ToList();

        var groups = new List<List<Word>>();
        foreach (var w in ordered)
        {
            if (groups.Count == 0)
            {
                groups.Add([w]);
                continue;
            }

            var lastGroup = groups[^1];
            double refY = lastGroup[0].BoundingBox.Bottom;
            if (Math.Abs(w.BoundingBox.Bottom - refY) <= 4.0)
                lastGroup.Add(w);
            else
                groups.Add([w]);
        }

        var result = new List<LineCandidate>(groups.Count);
        foreach (var g in groups)
        {
            g.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));
            var text = string.Join(" ", g.Select(w => w.Text.Trim())).Trim();
            if (text.Length == 0)
                continue;

            double avgHeight = g.Average(GetWordFontHeight);
            double topY = g.Max(w => w.BoundingBox.Top);
            double topFraction = Math.Clamp((pageHeight - topY) / pageHeight, 0.0, 1.0);
            result.Add(new LineCandidate(text, avgHeight, topFraction));
        }
        return result;
    }

    private static double GetWordFontHeight(Word w)
    {
        if (w.Letters.Count > 0)
        {
            double maxPoint = w.Letters.Max(l => l.FontSize);
            if (maxPoint > 0)
                return maxPoint;
        }
        return w.BoundingBox.Height;
    }

    private static string NormalizeKey(string text) =>
        Regex.Replace(text.Trim(), @"\s+", " ");

    private static string CleanTitle(string text)
    {
        var cleaned = Regex.Replace(text.Trim(), @"\s+", " ");
        return cleaned.Length > 85 ? cleaned[..82].TrimEnd() + "..." : cleaned;
    }
}
