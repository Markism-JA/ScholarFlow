using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using ScholarFlow.Core.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Outline;

namespace ScholarFlow.Core.Quiz;

public sealed record GroundedPdfPage(
    int PageNumber,
    string? Term,
    string SectionBreadcrumb,
    string Text,
    IReadOnlyList<ExtractablePdfFigure>? Figures = null
);

/// <summary>
/// Extracts bookmark-aware grounded page chunks from Master/Handout PDFs and resolves
/// verbatim quote evidence anchors into exact normalized [0..1] highlight bounding boxes via PdfPig.
/// </summary>
public static class PdfQuizGrounder
{
    /// <summary>
    /// Optional high-fidelity PDF page region rasterizer (registered by Desktop/Test hosts using PDFium/PDFtoImage)
    /// that renders a normalized [0..1] top-left rectangle of a 1-based PDF page to a PNG file.
    /// Parameters: (pdfPath, oneBasedPage, region, outputPngPath, dpi).
    /// When null, <see cref="MaterializeQuestionSourceFigures"/> falls back to extracting embedded raster images via PdfPig.
    /// </summary>
    public static Action<
        string,
        int,
        NormalizedHighlightRect,
        string,
        int
    >? CustomPageRegionRasterizer { get; set; }

    private static readonly Regex FigureCaptionLineRegex = new(
        @"^(?:Figure|Fig\.?|Diagram|Chart|Exhibit|Plate)\s+\d+(?:[\.\-]\d+)?[a-z]?(?:\s*[:\.\-—]\s*|\s+).{3,140}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private sealed record CachedPdfTextData(
        long LastWriteTicks,
        int TotalPages,
        (string? Term, string SectionBreadcrumb)[] PageSections,
        ConcurrentDictionary<int, string> PageTexts,
        ConcurrentDictionary<int, IReadOnlyList<ExtractablePdfFigure>> PageFigures,
        object ExtractionLock
    );

    private static readonly ConcurrentDictionary<string, CachedPdfTextData> PdfTextCache = new(
        StringComparer.OrdinalIgnoreCase
    );

    private sealed record CachedPdfWordData(
        long LastWriteTicks,
        int TotalPages,
        (string? Term, string SectionBreadcrumb)[] PageSections,
        ConcurrentDictionary<int, CachedPageWordIndex> PageWords,
        object ExtractionLock
    );

    private static readonly ConcurrentDictionary<string, CachedPdfWordData> PdfWordIndexCache = new(
        StringComparer.OrdinalIgnoreCase
    );

    public static IReadOnlyList<PdfOutlineSectionInfo> InspectOutlineSections(
        string pdfPath,
        out int totalPages
    )
    {
        totalPages = 0;
        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            return [];

        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            int pageCount = doc.NumberOfPages;
            totalPages = pageCount;
            if (
                pageCount == 0
                || !doc.TryGetBookmarks(out var bookmarks)
                || bookmarks.Roots.Count == 0
            )
                return [];

            var flat = new List<(string Title, string? Term, int Level, int StartPage)>();

            void Walk(IEnumerable<BookmarkNode> nodes, string? inheritedTerm, int level)
            {
                foreach (var node in nodes)
                {
                    var title = (node.Title ?? string.Empty).Trim();
                    var term = ResolveOutlineTermOrSection(title, level, inheritedTerm);
                    if (
                        node is DocumentBookmarkNode docNode
                        && docNode.PageNumber >= 1
                        && docNode.PageNumber <= pageCount
                        && !title.Equals("Table of Contents", StringComparison.OrdinalIgnoreCase)
                    )
                    {
                        flat.Add((title, term, level, docNode.PageNumber));
                    }

                    if (node.Children.Count > 0)
                        Walk(node.Children, term, level + 1);
                }
            }

            Walk(bookmarks.Roots, null, 1);

            var sections = new List<PdfOutlineSectionInfo>();
            for (int i = 0; i < flat.Count; i++)
            {
                var (Title, Term, Level, StartPage) = flat[i];
                int endPage = totalPages;
                for (int j = i + 1; j < flat.Count; j++)
                {
                    if (flat[j].Level <= Level && flat[j].StartPage > StartPage)
                    {
                        endPage = flat[j].StartPage - 1;
                        break;
                    }
                    if (flat[j].Level > Level && flat[j].StartPage > StartPage && Level == 1)
                        continue;
                }

                // For leaf/handout nodes, also cap at next sibling or parent boundary
                if (Level >= 2)
                {
                    for (int j = i + 1; j < flat.Count; j++)
                    {
                        if (flat[j].StartPage > StartPage)
                        {
                            endPage = Math.Min(endPage, flat[j].StartPage - 1);
                            break;
                        }
                    }
                }

                endPage = Math.Clamp(endPage, StartPage, totalPages);
                sections.Add(new PdfOutlineSectionInfo(Title, Term, Level, StartPage, endPage));
            }

            return sections;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Fast (< 0.1ms) metadata-only resolution of which PDF pages and section titles are included in the active scope,
    /// without re-opening or re-parsing the PDF on the UI thread.
    /// </summary>
    public static IReadOnlyList<(
        int PageNumber,
        string SectionTitle,
        string? Term
    )> ResolveScopePagesFast(
        int totalPages,
        IReadOnlyList<PdfOutlineSectionInfo> outlineSections,
        string? termFilter = null,
        int? startPage = null,
        int? endPage = null,
        IReadOnlyList<PdfPageRange>? explicitRanges = null
    )
    {
        if (totalPages <= 0)
            return [];

        var normalizedTerm =
            TaskItem.NormalizeTerm(termFilter)
            ?? (string.IsNullOrWhiteSpace(termFilter) ? null : termFilter.Trim());
        bool hasExplicitRanges = explicitRanges is { Count: > 0 };
        var leafSections = outlineSections.Where(s => s.Level >= 2).ToList();
        if (leafSections.Count == 0)
            leafSections = [.. outlineSections];

        bool hasNonSyllabusSections = leafSections.Any(s =>
            !s.Title.Contains("Syllabus", StringComparison.OrdinalIgnoreCase)
        );
        bool hasLevel2Sections = outlineSections.Any(s => s.Level >= 2);
        int minP = Math.Clamp(startPage ?? 1, 1, totalPages);
        int maxP = Math.Clamp(endPage ?? totalPages, minP, totalPages);

        // Precompute O(1) per-page lookup tables instead of O(Pages * Sections) scans
        var secByPage = new PdfOutlineSectionInfo?[totalPages + 1];
        foreach (var s in outlineSections)
        {
            if (s.Level >= 2 && leafSections.Count > 0)
                continue;
            int sStart = Math.Clamp(s.StartPage, 1, totalPages);
            int sEnd = Math.Clamp(s.EndPage, sStart, totalPages);
            for (int p = sStart; p <= sEnd; p++)
                secByPage[p] = s;
        }
        foreach (var s in leafSections)
        {
            int sStart = Math.Clamp(s.StartPage, 1, totalPages);
            int sEnd = Math.Clamp(s.EndPage, sStart, totalPages);
            for (int p = sStart; p <= sEnd; p++)
                secByPage[p] = s;
        }

        var termDividerPages = new HashSet<int>();
        if (hasLevel2Sections)
        {
            foreach (var s in outlineSections)
            {
                if (s.Level == 1 && !string.IsNullOrWhiteSpace(s.Term))
                    termDividerPages.Add(s.StartPage);
            }
        }

        bool[]? explicitPageMask = null;
        if (hasExplicitRanges)
        {
            explicitPageMask = new bool[totalPages + 1];
            foreach (var r in explicitRanges!)
            {
                int rStart = Math.Clamp(r.StartPage, 1, totalPages);
                int rEnd = Math.Clamp(r.EndPage, rStart, totalPages);
                for (int p = rStart; p <= rEnd; p++)
                    explicitPageMask[p] = true;
            }
        }

        var matched = new List<(int PageNumber, string SectionTitle, string? Term)>();
        for (int p = minP; p <= maxP; p++)
        {
            if (explicitPageMask is not null && !explicitPageMask[p])
                continue;

            var sec = secByPage[p];

            // Skip cover / TOC / single-page term divider headers when outline sections exist and no explicit page range was typed
            if (
                !hasExplicitRanges
                && !startPage.HasValue
                && !endPage.HasValue
                && outlineSections.Count > 0
            )
            {
                if (sec is null)
                    continue;

                // Skip the 1-page Term Divider sheet itself (Level 1 start page before Level 2 handout starts)
                if (termDividerPages.Contains(p))
                    continue;

                // Skip Course Syllabus boilerplate by default unless explicitly checked
                if (
                    hasNonSyllabusSections
                    && sec.Title.Contains("Syllabus", StringComparison.OrdinalIgnoreCase)
                )
                    continue;
            }

            if (!hasExplicitRanges && !string.IsNullOrWhiteSpace(normalizedTerm))
            {
                if (
                    sec is null
                    || !string.Equals(sec.Term, normalizedTerm, StringComparison.OrdinalIgnoreCase)
                )
                    continue;
            }

            var label = sec is not null
                ? (
                    !string.IsNullOrWhiteSpace(sec.Term)
                    && !sec.Title.Contains(sec.Term, StringComparison.OrdinalIgnoreCase)
                        ? $"{sec.Term} / {sec.Title}"
                        : sec.Title
                )
                : $"Page {p}";

            matched.Add((p, label, sec?.Term ?? normalizedTerm));
        }

        // Fallback if individual handout PDF has no bookmarks
        if (matched.Count == 0 && outlineSections.Count == 0)
        {
            for (int p = minP; p <= maxP; p++)
            {
                if (hasExplicitRanges && !explicitRanges!.Any(r => r.Contains(p)))
                    continue;
                matched.Add((p, $"Page {p}", normalizedTerm));
            }
        }

        return matched;
    }

    public static (int PageCount, int CharCount, string SampleExcerpt) BuildScopePreview(
        string pdfPath,
        string? termFilter = null,
        string? sectionFilter = null,
        int? startPage = null,
        int? endPage = null,
        IReadOnlyList<PdfPageRange>? explicitRanges = null
    )
    {
        var pages = ExtractGroundedPages(
            pdfPath,
            termFilter,
            sectionFilter,
            startPage,
            endPage,
            explicitRanges,
            maxPages: 30
        );
        if (pages.Count == 0)
            return (0, 0, "No readable content pages match the current filter.");

        int totalChars = pages.Sum(p => p.Text.Length);
        var first = pages[0];
        var snippet = first.Text.Length > 260 ? first.Text[..260].Trim() + "..." : first.Text;
        return (
            pages.Count,
            totalChars,
            $"Page {first.PageNumber} ({first.SectionBreadcrumb}): {snippet}"
        );
    }

    public static IReadOnlyList<GroundedPdfPage> ExtractGroundedPages(
        string pdfPath,
        string? termFilter = null,
        string? sectionFilter = null,
        int? startPage = null,
        int? endPage = null,
        IReadOnlyList<PdfPageRange>? explicitRanges = null,
        int maxPages = 150
    )
    {
        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            return [];

        long currentTicks = File.GetLastWriteTimeUtc(pdfPath).Ticks;
        if (
            !PdfTextCache.TryGetValue(pdfPath, out var cached)
            || cached.LastWriteTicks != currentTicks
        )
        {
            using var doc = PdfDocument.Open(pdfPath);
            int docPages = doc.NumberOfPages;
            if (docPages == 0)
                return [];

            var sections = BuildPageSectionMap(doc);

            if (PdfTextCache.Count >= 8)
                PdfTextCache.Clear();

            cached = new CachedPdfTextData(
                currentTicks,
                docPages,
                sections,
                new ConcurrentDictionary<int, string>(),
                new ConcurrentDictionary<int, IReadOnlyList<ExtractablePdfFigure>>(),
                new object()
            );
            PdfTextCache[pdfPath] = cached;
        }

        int totalPages = cached.TotalPages;
        if (totalPages == 0)
            return [];

        var pageSections = cached.PageSections;
        var normalizedTerm =
            TaskItem.NormalizeTerm(termFilter)
            ?? (string.IsNullOrWhiteSpace(termFilter) ? null : termFilter.Trim());
        bool hasAnyTermBookmarks = pageSections.Any(ps => !string.IsNullOrWhiteSpace(ps.Term));
        bool hasNonSyllabusPages = pageSections.Any(ps =>
            !string.IsNullOrWhiteSpace(ps.SectionBreadcrumb)
            && !ps.SectionBreadcrumb.Contains("Syllabus", StringComparison.OrdinalIgnoreCase)
            && !ps.SectionBreadcrumb.Equals("Table of Contents", StringComparison.OrdinalIgnoreCase)
        );
        bool hasExplicitRanges = explicitRanges is { Count: > 0 };

        int minP = Math.Clamp(startPage ?? 1, 1, totalPages);
        int maxP = Math.Clamp(endPage ?? totalPages, minP, totalPages);

        var candidatePages = new List<int>();
        for (int p = minP; p <= maxP; p++)
        {
            if (hasExplicitRanges && !explicitRanges!.Any(r => r.Contains(p)))
                continue;

            var (pageTerm, breadcrumb) = pageSections[p];

            if (
                !hasExplicitRanges
                && !startPage.HasValue
                && !endPage.HasValue
                && string.IsNullOrWhiteSpace(sectionFilter)
                && hasNonSyllabusPages
                && breadcrumb.Contains("Syllabus", StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            if (
                !hasExplicitRanges
                && !string.IsNullOrWhiteSpace(normalizedTerm)
                && hasAnyTermBookmarks
                && !string.Equals(pageTerm, normalizedTerm, StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            if (
                !string.IsNullOrWhiteSpace(sectionFilter)
                && !breadcrumb.Contains(sectionFilter.Trim(), StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            candidatePages.Add(p);
        }

        EnsurePagesExtracted(pdfPath, cached, candidatePages, maxPages + 25);

        var results = new List<GroundedPdfPage>();
        foreach (int p in candidatePages)
        {
            var (pageTerm, breadcrumb) = pageSections[p];
            var text = cached.PageTexts.GetValueOrDefault(p, string.Empty);

            var pageFigures = cached.PageFigures.GetValueOrDefault(p, []);

            // Skip cover pages, term divider pages, table of contents, or nearly empty pages
            // (but retain figure-bearing slides/pages that have at least 40 chars of context text)
            if (
                (text.Length < 180 && (pageFigures.Count == 0 || text.Length < 40))
                || text.StartsWith("TABLE OF CONTENTS", StringComparison.OrdinalIgnoreCase)
                || (
                    text.Contains("Includes:", StringComparison.OrdinalIgnoreCase)
                    && text.Length < 320
                )
            )
            {
                continue;
            }

            results.Add(
                new GroundedPdfPage(
                    PageNumber: p,
                    Term: pageTerm ?? normalizedTerm,
                    SectionBreadcrumb: string.IsNullOrWhiteSpace(breadcrumb)
                        ? Path.GetFileNameWithoutExtension(pdfPath)
                        : breadcrumb,
                    Text: text,
                    Figures: pageFigures
                )
            );

            if (results.Count >= maxPages)
                break;
        }

        // Fallback if term filter excluded everything (e.g. individual handout PDF without term bookmarks)
        if (
            results.Count == 0
            && (
                !string.IsNullOrWhiteSpace(normalizedTerm)
                || !string.IsNullOrWhiteSpace(sectionFilter)
            )
        )
        {
            var fallbackRange = Enumerable
                .Range(minP, maxP - minP + 1)
                .Take(maxPages + 25)
                .ToList();
            EnsurePagesExtracted(pdfPath, cached, fallbackRange, maxPages + 25);

            for (int p = minP; p <= maxP && results.Count < maxPages; p++)
            {
                var text = cached.PageTexts.GetValueOrDefault(p, string.Empty);
                if (text.Length < 55)
                    continue;

                var (_, breadcrumb) = pageSections[p];
                results.Add(
                    new GroundedPdfPage(
                        PageNumber: p,
                        Term: normalizedTerm,
                        SectionBreadcrumb: string.IsNullOrWhiteSpace(breadcrumb)
                            ? Path.GetFileNameWithoutExtension(pdfPath)
                            : breadcrumb,
                        Text: text,
                        Figures: cached.PageFigures.GetValueOrDefault(p, [])
                    )
                );
            }
        }

        return results;
    }

    public static string FormatPagesForPrompt(
        IReadOnlyList<GroundedPdfPage> pages,
        int maxChars = 42_000
    ) => FormatPagesForPrompt(pages, maxChars, out _);

    public static string FormatPagesForPrompt(
        IReadOnlyList<GroundedPdfPage> pages,
        int maxChars,
        out int includedCount
    )
    {
        var cleaned = StripRepeatedRunningHeadersAndFooters(pages);
        var sb = new StringBuilder();
        includedCount = 0;

        foreach (var p in cleaned)
        {
            var header = $"=== [PAGE {p.PageNumber}] | Section: {p.SectionBreadcrumb} ===";
            var figLine =
                p.Figures is { Count: > 0 }
                    ? $"[EXTRACTABLE SOURCE FIGURES ON PAGE {p.PageNumber}: {string.Join(", ", p.Figures.Select(f => $"id=\"{f.FigureId}\" (\"{f.Caption}\")"))}]\n"
                    : string.Empty;
            if (
                maxChars < int.MaxValue
                && sb.Length + header.Length + figLine.Length + p.Text.Length + 8 > maxChars
                && sb.Length > 0
            )
                break;

            sb.AppendLine(header);
            if (figLine.Length > 0)
                sb.Append(figLine);
            sb.AppendLine(p.Text);
            sb.AppendLine();
            includedCount++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Packs pages into contiguous batches that each fit under maxCharsPerBatch
    /// without silently dropping tail pages.
    /// </summary>
    public static List<List<GroundedPdfPage>> PackPagesIntoBudgets(
        IReadOnlyList<GroundedPdfPage> pages,
        int maxCharsPerBatch
    )
    {
        var cleaned = StripRepeatedRunningHeadersAndFooters(pages);
        var batches = new List<List<GroundedPdfPage>>();
        if (cleaned.Count == 0)
            return batches;

        var current = new List<GroundedPdfPage>();
        int currentChars = 0;

        foreach (var p in cleaned)
        {
            int entryCost = p.Text.Length + p.SectionBreadcrumb.Length + 48;
            if (current.Count > 0 && currentChars + entryCost > maxCharsPerBatch)
            {
                batches.Add(current);
                current = [];
                currentChars = 0;
            }
            current.Add(p);
            currentChars += entryCost;
        }

        if (current.Count > 0)
            batches.Add(current);

        return batches;
    }

    /// <summary>
    /// Losslessly removes repeated running headers/footers (e.g., chapter titles, page numbers,
    /// institution footers) appearing on 3+ pages while preserving body text verbatim for grounding.
    /// </summary>
    public static IReadOnlyList<GroundedPdfPage> StripRepeatedRunningHeadersAndFooters(
        IReadOnlyList<GroundedPdfPage> pages
    )
    {
        if (pages.Count < 4)
            return pages;

        static string NormalizeEdgeLine(string line) =>
            new string([.. line.Trim().Where(c => !char.IsDigit(c))]).Trim();

        var firstLineCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lastLineCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in pages)
        {
            var lines = p.Text.Split('\n');
            if (lines.Length >= 3)
            {
                var firstNorm = NormalizeEdgeLine(lines[0]);
                if (firstNorm.Length is >= 4 and <= 120)
                    firstLineCounts[firstNorm] = firstLineCounts.GetValueOrDefault(firstNorm) + 1;

                var lastNorm = NormalizeEdgeLine(lines[^1]);
                if (lastNorm.Length is >= 4 and <= 120)
                    lastLineCounts[lastNorm] = lastLineCounts.GetValueOrDefault(lastNorm) + 1;
            }
        }

        int threshold = Math.Max(3, pages.Count / 3);
        var result = new List<GroundedPdfPage>(pages.Count);

        foreach (var p in pages)
        {
            var lines = p.Text.Split('\n').ToList();
            if (lines.Count >= 3)
            {
                var firstNorm = NormalizeEdgeLine(lines[0]);
                if (firstLineCounts.GetValueOrDefault(firstNorm) >= threshold)
                    lines.RemoveAt(0);

                if (lines.Count >= 2)
                {
                    var lastNorm = NormalizeEdgeLine(lines[^1]);
                    if (lastLineCounts.GetValueOrDefault(lastNorm) >= threshold)
                        lines.RemoveAt(lines.Count - 1);
                }
            }

            result.Add(p with { Text = string.Join('\n', lines).Trim() });
        }

        return result;
    }

    /// <summary>
    /// Exports only the selected 1-based pages from a source PDF into a lightweight
    /// standalone PDF suitable for Web AI file upload.
    /// </summary>
    public static void ExportScopedPdfPages(
        string sourcePdfPath,
        IReadOnlyList<int> oneBasedPages,
        string outputPath
    )
    {
        using var input = PdfSharpCore.Pdf.IO.PdfReader.Open(
            sourcePdfPath,
            PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Import
        );
        using var output = new PdfSharpCore.Pdf.PdfDocument();
        foreach (int p in oneBasedPages)
        {
            if (p >= 1 && p <= input.PageCount)
                output.AddPage(input.Pages[p - 1]);
        }

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
        output.Save(outputPath);
    }

    /// <summary>
    /// Grounds every question in the list against the source PDF, snapping SourcePage if off by 1-2 pages
    /// and computing exact normalized [0..1] highlight bounding boxes for the VerbatimQuote.
    /// Uses a persistent per-PDF word-index cache and parallelizes quote matching across CPU cores.
    /// </summary>
    public static void GroundQuestionsToPdf(
        string pdfPath,
        IEnumerable<QuizQuestionRecord> questions,
        bool forceUpdateSection = false
    )
    {
        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            return;

        var questionList = questions as IList<QuizQuestionRecord> ?? [.. questions];
        if (questionList.Count == 0)
            return;

        try
        {
            long currentTicks = File.GetLastWriteTimeUtc(pdfPath).Ticks;
            PdfDocument? lazyDoc = null;

            PdfDocument EnsureDocOpen()
            {
                return lazyDoc ??= PdfDocument.Open(pdfPath);
            }

            if (
                !PdfWordIndexCache.TryGetValue(pdfPath, out var cached)
                || cached.LastWriteTicks != currentTicks
            )
            {
                var doc = EnsureDocOpen();
                if (doc.NumberOfPages == 0)
                    return;

                var sections = BuildPageSectionMap(doc);
                cached = new CachedPdfWordData(
                    currentTicks,
                    doc.NumberOfPages,
                    sections,
                    new ConcurrentDictionary<int, CachedPageWordIndex>(),
                    new object()
                );
                PdfWordIndexCache[pdfPath] = cached;
            }

            int totalPages = cached.TotalPages;
            if (totalPages <= 0)
                return;

            var pageSections = cached.PageSections;
            var pageWordCache = cached.PageWords;

            CachedPageWordIndex GetPageIndex(int pageNum)
            {
                if (pageWordCache.TryGetValue(pageNum, out var existing))
                    return existing;

                lock (cached.ExtractionLock)
                {
                    if (pageWordCache.TryGetValue(pageNum, out existing))
                        return existing;

                    var doc = EnsureDocOpen();
                    var built = BuildPageWordIndex(doc, pageNum);
                    pageWordCache[pageNum] = built;
                    return built;
                }
            }

            void GroundSingleQuestion(QuizQuestionRecord q)
            {
                q.SourcePdfPath = pdfPath;
                var candidateQuotes = CollectCandidateQuotes(q);
                var resolvedAnchors = new List<GroundedEvidenceAnchor>();
                var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                int primaryPage = q.SourcePage;
                List<NormalizedHighlightRect> primaryRects = [];

                foreach (var quote in candidateQuotes)
                {
                    var rects = ComputeHighlightRectsFromProvider(
                        totalPages,
                        GetPageIndex,
                        q.SourcePage,
                        quote,
                        out int resolvedPage
                    );
                    resolvedPage = Math.Clamp(resolvedPage, 1, totalPages);
                    if (rects.Count == 0)
                        continue;

                    var sec = pageSections[resolvedPage].SectionBreadcrumb;
                    var key = $"{resolvedPage}:{quote.Trim()}";
                    if (seenKeys.Add(key))
                    {
                        resolvedAnchors.Add(
                            new GroundedEvidenceAnchor(
                                resolvedPage,
                                !string.IsNullOrWhiteSpace(sec) ? sec : q.SectionTitle,
                                quote.Trim(),
                                rects
                            )
                        );
                    }

                    if (primaryRects.Count == 0)
                    {
                        primaryPage = resolvedPage;
                        primaryRects = [.. rects];
                    }
                    else if (resolvedPage == primaryPage)
                    {
                        primaryRects.AddRange(rects);
                    }
                }

                // Fallback to whole VerbatimQuote if splitting yielded nothing
                if (primaryRects.Count == 0 && !string.IsNullOrWhiteSpace(q.VerbatimQuote))
                {
                    primaryRects = ComputeHighlightRectsFromProvider(
                        totalPages,
                        GetPageIndex,
                        q.SourcePage,
                        q.VerbatimQuote,
                        out int fallbackPage
                    );
                    primaryPage = Math.Clamp(fallbackPage, 1, totalPages);
                    if (primaryRects.Count > 0)
                    {
                        var sec = pageSections[primaryPage].SectionBreadcrumb;
                        resolvedAnchors.Add(
                            new GroundedEvidenceAnchor(
                                primaryPage,
                                !string.IsNullOrWhiteSpace(sec) ? sec : q.SectionTitle,
                                q.VerbatimQuote.Trim(),
                                primaryRects
                            )
                        );
                    }
                }

                q.SourcePage = Math.Clamp(primaryPage, 1, totalPages);
                q.HighlightRects = primaryRects;
                q.EvidenceAnchors = resolvedAnchors;

                if (forceUpdateSection || string.IsNullOrWhiteSpace(q.SectionTitle))
                {
                    var sec = pageSections[q.SourcePage].SectionBreadcrumb;
                    if (!string.IsNullOrWhiteSpace(sec))
                        q.SectionTitle = sec;
                }
            }

            try
            {
                if (questionList.Count >= 4)
                {
                    Parallel.ForEach(questionList, GroundSingleQuestion);
                }
                else
                {
                    foreach (var q in questionList)
                        GroundSingleQuestion(q);
                }
            }
            finally
            {
                lazyDoc?.Dispose();
            }
        }
        catch
        {
            // Keep questions usable even if PDF grounding encounters an unexpected structure
        }
    }

    /// <summary>
    /// Re-grounds every question in an existing quiz session against its current SourcePdfPath
    /// (for example after a Master PDF recompiles and page numbers shift) and updates SourcePdfMtimeTicks.
    /// </summary>
    public static int RegroundSessionToPdf(QuizSessionRecord session)
    {
        if (
            session is null
            || string.IsNullOrWhiteSpace(session.SourcePdfPath)
            || !File.Exists(session.SourcePdfPath)
        )
            return 0;

        GroundQuestionsToPdf(session.SourcePdfPath, session.Questions, forceUpdateSection: true);
        session.SourcePdfMtimeTicks = File.GetLastWriteTimeUtc(session.SourcePdfPath).Ticks;
        return session.Questions.Count(q => q.HighlightRects.Count > 0);
    }

    /// <summary>
    /// Extracts words inside a normalized [0..1] top-left bounding box on the given 1-based PDF page
    /// and resolves the page's section breadcrumb for visual drag-to-highlight grounding.
    /// </summary>
    public static (
        string Quote,
        string SectionTitle,
        int TotalPages
    ) ExtractQuoteFromNormalizedRegion(
        string pdfPath,
        int oneBasedPage,
        NormalizedHighlightRect region
    )
    {
        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            return (string.Empty, string.Empty, 0);

        using var doc = PdfDocument.Open(pdfPath);
        int totalPages = doc.NumberOfPages;
        if (totalPages <= 0)
            return (string.Empty, string.Empty, 0);

        int p = Math.Clamp(oneBasedPage, 1, totalPages);
        var sectionMap = BuildPageSectionMap(doc);
        var sectionTitle = sectionMap[p].SectionBreadcrumb;

        var page = doc.GetPage(p);
        double pw = Math.Max(1.0, page.Width);
        double ph = Math.Max(1.0, page.Height);

        double rx1 = Math.Clamp(region.X, 0.0, 1.0);
        double ry1 = Math.Clamp(region.Y, 0.0, 1.0);
        double rx2 = Math.Clamp(region.X + region.Width, 0.0, 1.0);
        double ry2 = Math.Clamp(region.Y + region.Height, 0.0, 1.0);

        var matchingWords = GetVisibleWordsInReadingOrder(page)
            .Where(w =>
            {
                double cx = (w.BoundingBox.Left + w.BoundingBox.Right) * 0.5 / pw;
                double cy = 1.0 - ((w.BoundingBox.Bottom + w.BoundingBox.Top) * 0.5 / ph);
                return cx >= rx1 && cx <= rx2 && cy >= ry1 && cy <= ry2;
            })
            .ToList();

        var quote = string.Join(
            " ",
            matchingWords.Select(w => w.Text.Trim()).Where(t => t.Length > 0)
        );
        return (quote, sectionTitle, totalPages);
    }

    /// <summary>
    /// Extracts embedded raster XObject images from a 1-based PDF page (useful for slide decks)
    /// and saves them as PNGs in outputDir, returning saved file paths.
    /// </summary>
    public static List<string> ExtractEmbeddedImagesFromPage(
        string pdfPath,
        int oneBasedPage,
        string outputDir
    )
    {
        var saved = new List<string>();
        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            return saved;

        using var doc = PdfDocument.Open(pdfPath);
        if (oneBasedPage < 1 || oneBasedPage > doc.NumberOfPages)
            return saved;

        var page = doc.GetPage(oneBasedPage);
        var images = page.GetImages().ToList();
        if (images.Count == 0)
            return saved;

        Directory.CreateDirectory(outputDir);
        int idx = 1;
        foreach (var img in images)
        {
            if (img.WidthInSamples < 48 || img.HeightInSamples < 48)
                continue;

            var outPath = Path.Combine(outputDir, $"p{oneBasedPage}_fig{idx++}.png");
            if (TryWritePdfImageAsPng(img, outPath))
            {
                saved.Add(outPath);
            }
        }
        return saved;
    }

    private static bool TryWritePdfImageAsPng(IPdfImage img, string outPath)
    {
        try
        {
            if (img.TryGetPng(out var pngBytes) && pngBytes is { Length: > 0 })
            {
                var dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllBytes(outPath, pngBytes);
                return true;
            }

            var rawSpan = img.RawBytes;
            if (rawSpan.Length > 4)
            {
                // Fallback if the embedded stream is already a valid JPEG/PNG image stream
                if (
                    (rawSpan[0] == 0x89 && rawSpan[1] == 0x50)
                    || (rawSpan[0] == 0xFF && rawSpan[1] == 0xD8)
                )
                {
                    var dir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrWhiteSpace(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllBytes(outPath, rawSpan.ToArray());
                    return true;
                }
            }
        }
        catch
        {
            // Ignore malformed image object
        }
        return false;
    }

    /// <summary>
    /// Discovers extractable figures on a PDF page using a 2-tier hybrid strategy:
    /// Tier A: Embedded raster XObject images (filtered to exclude small logos/headers/footers) paired with nearby Figure captions.
    /// Tier B: Caption-anchored vector diagram regions ("Figure X.Y: ...") when a diagram is drawn with vector paths (TikZ/PowerPoint) rather than a bitmap.
    /// </summary>
    private static List<ExtractablePdfFigure> DiscoverPageFigures(
        Page page,
        int pageNumber,
        List<Word> sortedWords
    )
    {
        var discovered = new List<ExtractablePdfFigure>();
        double pw = Math.Max(1.0, page.Width);
        double ph = Math.Max(1.0, page.Height);

        // 1. Group words into horizontal reading lines with normalized top-left [0..1] Y bounds
        var lines = new List<(string Text, double TopY, double BottomY, double LeftX, double RightX)>();
        if (sortedWords.Count > 0)
        {
            var curWords = new List<Word> { sortedWords[0] };
            double lastBottom = sortedWords[0].BoundingBox.Bottom;

            void FlushLine()
            {
                if (curWords.Count == 0)
                    return;
                var txt = string.Join(" ", curWords.Select(w => w.Text.Trim())).Trim();
                if (txt.Length > 0)
                {
                    double minL = curWords.Min(w => w.BoundingBox.Left) / pw;
                    double maxR = curWords.Max(w => w.BoundingBox.Right) / pw;
                    double topY = 1.0 - (curWords.Max(w => w.BoundingBox.Top) / ph);
                    double botY = 1.0 - (curWords.Min(w => w.BoundingBox.Bottom) / ph);
                    lines.Add(
                        (
                            txt,
                            Math.Clamp(topY, 0.0, 1.0),
                            Math.Clamp(botY, 0.0, 1.0),
                            Math.Clamp(minL, 0.0, 1.0),
                            Math.Clamp(maxR, 0.0, 1.0)
                        )
                    );
                }
                curWords.Clear();
            }

            for (int i = 1; i < sortedWords.Count; i++)
            {
                var w = sortedWords[i];
                if (Math.Abs(w.BoundingBox.Bottom - lastBottom) > 5.0)
                    FlushLine();
                curWords.Add(w);
                lastBottom = w.BoundingBox.Bottom;
            }
            FlushLine();
        }

        var captions = new List<(int LineIndex, string Caption, double TopY, double BottomY, bool Paired)>();
        for (int i = 0; i < lines.Count; i++)
        {
            var ln = lines[i];
            if (FigureCaptionLineRegex.IsMatch(ln.Text))
            {
                var capText = ln.Text.Length > 110 ? ln.Text[..110].Trim() + "..." : ln.Text;
                captions.Add((i, capText, ln.TopY, ln.BottomY, false));
            }
        }

        // 2. Tier A: Embedded Raster Images (page.GetImages())
        List<IPdfImage> rawImages;
        try
        {
            rawImages = [.. page.GetImages()];
        }
        catch
        {
            rawImages = [];
        }

        int figCounter = 1;
        for (int imgIdx = 0; imgIdx < rawImages.Count; imgIdx++)
        {
            var img = rawImages[imgIdx];
            if (img.WidthInSamples < 80 || img.HeightInSamples < 60)
                continue;

            var b = img.BoundingBox;
            double nx = Math.Clamp(b.Left / pw, 0.0, 0.96);
            double ny = Math.Clamp(1.0 - (b.Top / ph), 0.0, 0.96);
            double nw = Math.Clamp(b.Width / pw, 0.04, 1.0 - nx);
            double nh = Math.Clamp(b.Height / ph, 0.04, 1.0 - ny);

            // Skip small logos/icons and top-banner institutional logos (e.g. STI header emblem)
            if (nw < 0.14 || nh < 0.07)
                continue;
            if (ny < 0.09 && nh < 0.10 && nw < 0.35)
                continue;
            // Skip full-page background scan/watermark plates (> 92% width AND > 92% height)
            if (nw > 0.94 && nh > 0.94 && rawImages.Count == 1 && captions.Count == 0)
                continue;

            // Pair with nearest Figure caption within 0.18 vertical distance
            string? matchedCaption = null;
            double bestDist = 0.18;
            int matchedCapIdx = -1;

            for (int cIdx = 0; cIdx < captions.Count; cIdx++)
            {
                var cap = captions[cIdx];
                if (cap.Paired)
                    continue;

                double distBelow = Math.Abs(cap.TopY - (ny + nh));
                double distAbove = Math.Abs(ny - cap.BottomY);
                double dist = Math.Min(distBelow, distAbove);
                if (dist <= bestDist)
                {
                    bestDist = dist;
                    matchedCaption = cap.Caption;
                    matchedCapIdx = cIdx;
                }
            }

            if (matchedCapIdx >= 0)
            {
                var cap = captions[matchedCapIdx];
                captions[matchedCapIdx] = cap with { Paired = true };
                // Expand crop slightly to include the figure caption for context
                double minTop = Math.Min(ny, cap.TopY);
                double maxBot = Math.Max(ny + nh, cap.BottomY);
                ny = Math.Max(0.0, minTop - 0.01);
                nh = Math.Min(1.0 - ny, (maxBot - minTop) + 0.02);
            }

            // Add a small 1.5% padding margin around the embedded figure so borders/axes aren't clipped
            double padX = Math.Max(0.0, nx - 0.015);
            double padY = Math.Max(0.0, ny - 0.015);
            double padW = Math.Min(1.0 - padX, nw + 0.03);
            double padH = Math.Min(1.0 - padY, nh + 0.03);

            var figId = $"p{pageNumber}_fig{figCounter++}";
            var label = matchedCaption ?? $"Embedded Figure {figCounter - 1} (Page {pageNumber})";
            discovered.Add(
                new ExtractablePdfFigure(
                    figId,
                    pageNumber,
                    label,
                    new NormalizedHighlightRect(padX, padY, padW, padH),
                    EmbeddedImageIndex: imgIdx
                )
            );
        }

        // 3. Tier B: Caption-Anchored Vector Diagram Band (for pure-vector TikZ / PowerPoint / Visio figures with no IPdfImage)
        foreach (var cap in captions)
        {
            if (cap.Paired)
                continue;

            // Search upwards from the caption for the preceding full-width prose paragraph line
            double bandTopY = Math.Max(0.06, cap.TopY - 0.34);
            for (int li = cap.LineIndex - 1; li >= 0; li--)
            {
                var prevLine = lines[li];
                double gapAboveLine = cap.TopY - prevLine.BottomY;
                if (gapAboveLine > 0.42)
                    break;

                bool looksLikeBodyParagraph =
                    prevLine.Text.Length >= 55 && (prevLine.RightX - prevLine.LeftX) >= 0.48;
                if (looksLikeBodyParagraph && gapAboveLine >= 0.10)
                {
                    bandTopY = prevLine.BottomY + 0.012;
                    break;
                }
            }

            double bandBottomY = Math.Min(0.95, cap.BottomY + 0.015);
            double bandHeight = bandBottomY - bandTopY;
            if (bandHeight >= 0.10)
            {
                var figId = $"p{pageNumber}_fig{figCounter++}";
                discovered.Add(
                    new ExtractablePdfFigure(
                        figId,
                        pageNumber,
                        cap.Caption,
                        new NormalizedHighlightRect(
                            0.06,
                            bandTopY,
                            0.88,
                            Math.Clamp(bandHeight, 0.10, 0.55)
                        ),
                        EmbeddedImageIndex: -1
                    )
                );
            }
        }

        // 4. Tier B2: Uncaptioned Pure-Vector Graphic Clusters (e.g. Matplotlib / TikZ / PowerPoint plots without "Figure N" text)
        try
        {
            var rawPaths = page.Paths;
            if (rawPaths is { Count: >= 8 })
            {
                var pathRects = new List<(double X1, double Y1, double X2, double Y2)>();
                foreach (var path in rawPaths)
                {
                    var rect = path.GetBoundingRectangle();
                    if (rect is null)
                        continue;
                    var r = rect.Value;
                    double nx1 = Math.Clamp(r.Left / pw, 0.0, 1.0);
                    double ny1 = Math.Clamp(1.0 - (r.Top / ph), 0.0, 1.0);
                    double nx2 = Math.Clamp(r.Right / pw, 0.0, 1.0);
                    double ny2 = Math.Clamp(1.0 - (r.Bottom / ph), 0.0, 1.0);
                    double rw = nx2 - nx1;
                    double rh = ny2 - ny1;

                    // Skip full-page frames/backgrounds and degenerate zero-area specks
                    if ((rw > 0.90 && rh > 0.90) || (rw < 0.003 && rh < 0.003))
                        continue;
                    // Skip header/footer rules in the very top/bottom 5% margin
                    if ((ny2 < 0.05 || ny1 > 0.95) && rh < 0.02)
                        continue;

                    pathRects.Add((nx1, ny1, nx2, ny2));
                }

                // Connected-component clustering with 0.035 normalized proximity margin
                const double clusterMargin = 0.035;
                var visited = new bool[pathRects.Count];
                var vectorClusters = new List<(double X1, double Y1, double X2, double Y2, int Count)>();

                for (int i = 0; i < pathRects.Count; i++)
                {
                    if (visited[i])
                        continue;
                    visited[i] = true;
                    var queue = new Queue<int>();
                    queue.Enqueue(i);
                    double cx1 = pathRects[i].X1;
                    double cy1 = pathRects[i].Y1;
                    double cx2 = pathRects[i].X2;
                    double cy2 = pathRects[i].Y2;
                    int count = 1;

                    while (queue.Count > 0)
                    {
                        var cur = pathRects[queue.Dequeue()];
                        for (int j = 0; j < pathRects.Count; j++)
                        {
                            if (visited[j])
                                continue;
                            var cand = pathRects[j];
                            if (
                                cand.X1 <= cur.X2 + clusterMargin
                                && cand.X2 >= cur.X1 - clusterMargin
                                && cand.Y1 <= cur.Y2 + clusterMargin
                                && cand.Y2 >= cur.Y1 - clusterMargin
                            )
                            {
                                visited[j] = true;
                                queue.Enqueue(j);
                                cx1 = Math.Min(cx1, cand.X1);
                                cy1 = Math.Min(cy1, cand.Y1);
                                cx2 = Math.Max(cx2, cand.X2);
                                cy2 = Math.Max(cy2, cand.Y2);
                                count++;
                            }
                        }
                    }

                    double cw = cx2 - cx1;
                    double ch = cy2 - cy1;
                    if (count >= 8 && cw >= 0.22 && ch >= 0.14 && !(cw > 0.92 && ch > 0.92))
                    {
                        vectorClusters.Add((cx1, cy1, cx2, cy2, count));
                    }
                }

                int addedVectorFigs = 0;
                foreach (var vc in vectorClusters.OrderByDescending(c => c.Count))
                {
                    if (addedVectorFigs >= 2)
                        break;

                    // Skip if it significantly overlaps an already-discovered Tier A or Tier B figure
                    bool overlapsExisting = discovered.Any(existing =>
                    {
                        double ex1 = existing.Region.X;
                        double ey1 = existing.Region.Y;
                        double ex2 = ex1 + existing.Region.Width;
                        double ey2 = ey1 + existing.Region.Height;
                        double ix = Math.Max(0.0, Math.Min(vc.X2, ex2) - Math.Max(vc.X1, ex1));
                        double iy = Math.Max(0.0, Math.Min(vc.Y2, ey2) - Math.Max(vc.Y1, ey1));
                        double interArea = ix * iy;
                        double vcArea = Math.Max(1e-6, (vc.X2 - vc.X1) * (vc.Y2 - vc.Y1));
                        return (interArea / vcArea) >= 0.35;
                    });
                    if (overlapsExisting)
                        continue;

                    // Skip dense prose paragraphs / text-heavy tables (many long full-width lines inside)
                    int longBodyLinesInside = lines.Count(ln =>
                        ln.TopY >= vc.Y1 - 0.01
                        && ln.BottomY <= vc.Y2 + 0.01
                        && ln.Text.Length >= 55
                        && (ln.RightX - ln.LeftX) >= 0.45
                    );
                    if (longBodyLinesInside >= 4)
                        continue;

                    // Try to find a concise heading/title line directly above the chart
                    string? nearbyHeading = lines
                        .Where(ln =>
                            ln.BottomY <= vc.Y1 + 0.01
                            && (vc.Y1 - ln.BottomY) <= 0.065
                            && ln.Text.Length is >= 4 and <= 80
                        )
                        .OrderBy(ln => vc.Y1 - ln.BottomY)
                        .Select(ln => ln.Text)
                        .FirstOrDefault();

                    double padX = Math.Max(0.0, vc.X1 - 0.02);
                    double padY = Math.Max(0.0, vc.Y1 - 0.025);
                    double padW = Math.Min(1.0 - padX, (vc.X2 - vc.X1) + 0.04);
                    double padH = Math.Min(1.0 - padY, (vc.Y2 - vc.Y1) + 0.05);

                    var figId = $"p{pageNumber}_fig{figCounter++}";
                    var label = !string.IsNullOrWhiteSpace(nearbyHeading)
                        ? $"Vector Chart: {nearbyHeading}"
                        : $"Vector Figure / Chart {figCounter - 1} (Page {pageNumber})";

                    discovered.Add(
                        new ExtractablePdfFigure(
                            figId,
                            pageNumber,
                            label,
                            new NormalizedHighlightRect(padX, padY, padW, padH),
                            EmbeddedImageIndex: -1
                        )
                    );
                    addedVectorFigs++;
                }
            }
        }
        catch
        {
            // Non-fatal: some malformed PDFs may throw during path inspection
        }

        return discovered;
    }

    /// <summary>
    /// Materializes cropped/extracted source figure images for any question that specified a <c>sourceFigureId</c>
    /// (Tier A/B) or an explicit <c>evidence.region</c> bounding box on a PDF page (Tier C), saving the PNG under
    /// <c>.scholarflow/quiz_media/&lt;quizId&gt;/</c> and populating <see cref="QuizQuestionRecord.ImagePath"/>.
    /// </summary>
    public static void MaterializeQuestionSourceFigures(
        string pdfPath,
        string? basinRoot,
        string quizId,
        IReadOnlyList<GroundedPdfPage> extractedPages,
        IReadOnlyList<(
            QuizQuestionRecord Question,
            string? SourceFigureId,
            EvidenceAnchorDto? Evidence
        )> items
    )
    {
        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath) || items.Count == 0)
            return;

        var actionable = items
            .Where(x =>
                string.IsNullOrWhiteSpace(x.Question.ImagePath)
                && (
                    !string.IsNullOrWhiteSpace(x.SourceFigureId)
                    || x.Evidence?.TryParseRegion() is not null
                )
            )
            .ToList();

        if (actionable.Count == 0)
            return;

        // Build lookup of discovered figures across extracted pages (and on-demand for any referenced page)
        var figsById = new Dictionary<string, ExtractablePdfFigure>(
            StringComparer.OrdinalIgnoreCase
        );
        var figsByPage = new Dictionary<int, List<ExtractablePdfFigure>>();

        void RegisterFigures(IEnumerable<ExtractablePdfFigure>? figs)
        {
            if (figs is null)
                return;
            foreach (var f in figs)
            {
                figsById[f.FigureId] = f;
                if (!figsByPage.TryGetValue(f.PageNumber, out var list))
                {
                    list = [];
                    figsByPage[f.PageNumber] = list;
                }
                if (
                    !list.Any(x =>
                        x.FigureId.Equals(f.FigureId, StringComparison.OrdinalIgnoreCase)
                    )
                )
                    list.Add(f);
            }
        }

        foreach (var p in extractedPages)
            RegisterFigures(p.Figures);

        var mediaDir = !string.IsNullOrWhiteSpace(basinRoot)
            ? Path.Combine(
                basinRoot,
                ".scholarflow",
                "quiz_media",
                string.IsNullOrWhiteSpace(quizId) ? "auto" : quizId
            )
            : Path.Combine(
                Path.GetTempPath(),
                "ScholarFlow-QuizMedia",
                string.IsNullOrWhiteSpace(quizId) ? "auto" : quizId
            );

        using var doc = PdfDocument.Open(pdfPath);
        int totalPages = doc.NumberOfPages;
        if (totalPages <= 0)
            return;

        foreach (var (question, rawFigId, evidence) in actionable)
        {
            int targetPage = Math.Clamp(
                question.SourcePage > 0 ? question.SourcePage : (evidence?.PageNumber ?? 1),
                1,
                totalPages
            );

            if (!figsByPage.ContainsKey(targetPage))
            {
                var page = doc.GetPage(targetPage);
                var words = GetVisibleWordsInReadingOrder(page);
                RegisterFigures(DiscoverPageFigures(page, targetPage, words));
            }

            ExtractablePdfFigure? matchedFig = null;
            if (!string.IsNullOrWhiteSpace(rawFigId))
            {
                var cleanId = rawFigId.Trim();
                if (!figsById.TryGetValue(cleanId, out matchedFig))
                {
                    // Fuzzy match by caption substring or first figure on the question's grounded page
                    if (figsByPage.TryGetValue(targetPage, out var pageFigs) && pageFigs.Count > 0)
                    {
                        matchedFig =
                            pageFigs.FirstOrDefault(f =>
                                f.Caption.Contains(cleanId, StringComparison.OrdinalIgnoreCase)
                                || cleanId.Contains(f.FigureId, StringComparison.OrdinalIgnoreCase)
                            ) ?? pageFigs[0];
                    }
                }
            }

            NormalizedHighlightRect? cropRegion = matchedFig?.Region ?? evidence?.TryParseRegion();
            if (matchedFig is null && cropRegion is null)
                continue;

            int figPage = matchedFig?.PageNumber ?? targetPage;
            var outFileName =
                $"{question.Id}_{matchedFig?.FigureId ?? $"p{figPage}_crop"}.png";
            var outFullPath = Path.Combine(mediaDir, outFileName);
            bool saved = false;

            // 1. Prefer CustomPageRegionRasterizer (PDFtoImage/PDFium) when available:
            //    renders both embedded raster images AND vector TikZ/PowerPoint drawings + overlaid labels at 220 DPI.
            if (CustomPageRegionRasterizer is not null && cropRegion is not null)
            {
                try
                {
                    CustomPageRegionRasterizer(pdfPath, figPage, cropRegion, outFullPath, 220);
                    saved = File.Exists(outFullPath);
                }
                catch
                {
                    saved = false;
                }
            }

            // 2. Pure-Core PdfPig fallback for embedded raster images (works even without CustomPageRegionRasterizer)
            if (!saved && matchedFig is { EmbeddedImageIndex: >= 0 })
            {
                try
                {
                    var pdfPage = doc.GetPage(figPage);
                    var pageImages = pdfPage.GetImages().ToList();
                    if (matchedFig.EmbeddedImageIndex < pageImages.Count)
                    {
                        saved = TryWritePdfImageAsPng(
                            pageImages[matchedFig.EmbeddedImageIndex],
                            outFullPath
                        );
                    }
                }
                catch
                {
                    saved = false;
                }
            }

            if (saved && File.Exists(outFullPath))
            {
                question.ImagePath = !string.IsNullOrWhiteSpace(basinRoot)
                    ? Path.GetRelativePath(basinRoot, outFullPath)
                    : outFullPath;
            }
        }
    }

    private sealed record CachedPageWordIndex(
        double Width,
        double Height,
        List<(Word Word, string Token)> TokenWords,
        HashSet<string> UniqueTokens
    );

    private static CachedPageWordIndex BuildPageWordIndex(PdfDocument doc, int pageNum)
    {
        var page = doc.GetPage(pageNum);
        var words = GetVisibleWordsInReadingOrder(page);
        var tokenWords = new List<(Word Word, string Token)>(words.Count);
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in words)
        {
            var tok = NormalizeToken(w.Text);
            if (tok.Length > 0)
            {
                tokenWords.Add((w, tok));
                if (tok.Length >= 2)
                    unique.Add(tok);
            }
        }

        return new CachedPageWordIndex(
            Math.Max(1.0, page.Width),
            Math.Max(1.0, page.Height),
            tokenWords,
            unique
        );
    }

    public static List<NormalizedHighlightRect> ComputeHighlightRects(
        PdfDocument doc,
        int hintPage,
        string? verbatimQuote,
        out int resolvedPage
    )
    {
        var localCache = new Dictionary<int, CachedPageWordIndex>();
        return ComputeHighlightRectsFromProvider(
            doc.NumberOfPages,
            p =>
            {
                if (!localCache.TryGetValue(p, out var idx))
                {
                    idx = BuildPageWordIndex(doc, p);
                    localCache[p] = idx;
                }
                return idx;
            },
            hintPage,
            verbatimQuote,
            out resolvedPage
        );
    }

    private static List<NormalizedHighlightRect> ComputeHighlightRectsFromProvider(
        int totalPages,
        Func<int, CachedPageWordIndex> getPageIndex,
        int hintPage,
        string? verbatimQuote,
        out int resolvedPage
    )
    {
        resolvedPage = Math.Clamp(hintPage <= 0 ? 1 : hintPage, 1, Math.Max(1, totalPages));
        if (totalPages == 0 || string.IsNullOrWhiteSpace(verbatimQuote))
            return [];

        var quoteTokens = TokenizeWords(verbatimQuote);
        if (quoteTokens.Count == 0)
            return [];

        var quoteSet = new HashSet<string>(
            quoteTokens.Where(t => t.Length >= 2),
            StringComparer.OrdinalIgnoreCase
        );
        if (quoteSet.Count == 0)
            return [];

        // 1. Search hintPage first, then nearby pages (+/- 1..5)
        var localPages = new List<int>(11) { resolvedPage };
        for (int delta = 1; delta <= 5; delta++)
        {
            if (resolvedPage - delta >= 1)
                localPages.Add(resolvedPage - delta);
            if (resolvedPage + delta <= totalPages)
                localPages.Add(resolvedPage + delta);
        }

        double bestScore = 0;
        int bestPage = resolvedPage;
        List<Word>? bestSpan = null;
        double bestPageWidth = 1;
        double bestPageHeight = 1;

        foreach (var pageNum in localPages)
        {
            var pageIdx = getPageIndex(pageNum);
            if (pageIdx.TokenWords.Count == 0)
                continue;

            var (score, span) = FindBestMatchingWordSpanCached(
                pageIdx.TokenWords,
                quoteTokens,
                quoteSet
            );
            if (score > bestScore && span.Count > 0)
            {
                bestScore = score;
                bestPage = pageNum;
                bestSpan = span;
                bestPageWidth = pageIdx.Width;
                bestPageHeight = pageIdx.Height;

                if (bestScore >= 0.85 || (pageNum == resolvedPage && bestScore >= 0.52))
                    break;
            }
        }

        // 2. Only scan remaining pages if the local +/- 5 page window did not find a strong match (< 0.52)
        if (bestScore < 0.52 && totalPages > localPages.Count)
        {
            var localSet = new HashSet<int>(localPages);
            int minRequiredOverlap = Math.Max(1, (int)Math.Ceiling(quoteSet.Count * 0.45));

            for (int pageNum = 1; pageNum <= totalPages; pageNum++)
            {
                if (localSet.Contains(pageNum))
                    continue;

                var pageIdx = getPageIndex(pageNum);
                if (pageIdx.TokenWords.Count == 0)
                    continue;

                // Fast O(Q) token-set prefilter skips non-matching pages without running O(W * Q) sliding window
                int overlap = 0;
                foreach (var qt in quoteSet)
                {
                    if (pageIdx.UniqueTokens.Contains(qt))
                        overlap++;
                }
                if (overlap < minRequiredOverlap)
                    continue;

                var (score, span) = FindBestMatchingWordSpanCached(
                    pageIdx.TokenWords,
                    quoteTokens,
                    quoteSet
                );
                if (score > bestScore && span.Count > 0)
                {
                    bestScore = score;
                    bestPage = pageNum;
                    bestSpan = span;
                    bestPageWidth = pageIdx.Width;
                    bestPageHeight = pageIdx.Height;

                    if (bestScore >= 0.88)
                        break;
                }
            }
        }

        if (bestSpan is null || bestSpan.Count == 0 || bestScore < 0.35)
            return [];

        resolvedPage = bestPage;
        return BuildLineRectangles(bestSpan, bestPageWidth, bestPageHeight);
    }

    private static (double Score, List<Word> Span) FindBestMatchingWordSpanCached(
        List<(Word Word, string Token)> tokenWords,
        List<string> quoteTokens,
        HashSet<string> quoteSet
    )
    {
        if (tokenWords.Count == 0 || quoteTokens.Count == 0 || quoteSet.Count == 0)
            return (0, []);

        int qLen = quoteTokens.Count;
        int windowLen = Math.Clamp(qLen, 2, Math.Min(tokenWords.Count, qLen + 4));

        double bestScore = 0;
        int bestStart = -1;
        int bestCount = 0;

        var consumed = new bool[qLen];

        for (int i = 0; i < tokenWords.Count; i++)
        {
            var startToken = tokenWords[i].Token;

            bool matchesStart = false;
            for (int k = 0; k < Math.Min(3, qLen); k++)
            {
                if (TokensMatch(startToken, quoteTokens[k]))
                {
                    matchesStart = true;
                    break;
                }
            }
            if (!matchesStart && !quoteSet.Contains(startToken))
                continue;

            Array.Clear(consumed, 0, qLen);
            int maxEnd = Math.Min(tokenWords.Count - 1, i + windowLen + 3);
            int hits = 0;
            int orderedIdx = 0;

            for (int j = i; j <= maxEnd; j++)
            {
                var pw = tokenWords[j].Token;

                while (orderedIdx < qLen && consumed[orderedIdx])
                    orderedIdx++;

                if (orderedIdx < qLen && TokensMatch(pw, quoteTokens[orderedIdx]))
                {
                    consumed[orderedIdx] = true;
                    hits += 2;
                    orderedIdx++;
                }
                else
                {
                    for (int k = 0; k < qLen; k++)
                    {
                        if (
                            !consumed[k]
                            && quoteTokens[k].Length >= 3
                            && TokensMatch(pw, quoteTokens[k])
                        )
                        {
                            consumed[k] = true;
                            hits += 1;
                            break;
                        }
                    }
                }

                int spanLen = j - i + 1;
                if (spanLen >= Math.Max(2, qLen - 2))
                {
                    double score = hits / (2.0 * qLen + Math.Max(0, spanLen - qLen) * 0.35);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestStart = i;
                        bestCount = spanLen;
                    }
                }
            }
        }

        if (bestStart < 0 || bestCount <= 0)
            return (0, []);

        return (bestScore, tokenWords.Skip(bestStart).Take(bestCount).Select(x => x.Word).ToList());
    }

    private static List<NormalizedHighlightRect> BuildLineRectangles(
        List<Word> spanWords,
        double pageWidth,
        double pageHeight
    )
    {
        var rects = new List<NormalizedHighlightRect>();
        var lines = new List<List<Word>>();

        foreach (var word in spanWords)
        {
            if (lines.Count == 0)
            {
                lines.Add([word]);
                continue;
            }

            var currentLine = lines[^1];
            double avgBottom = currentLine.Average(w => w.BoundingBox.Bottom);
            if (Math.Abs(word.BoundingBox.Bottom - avgBottom) <= 5.0)
            {
                currentLine.Add(word);
            }
            else
            {
                lines.Add([word]);
            }
        }

        const double padPt = 1.8;
        foreach (var line in lines)
        {
            double minLeft = line.Min(w => w.BoundingBox.Left) - padPt;
            double maxRight = line.Max(w => w.BoundingBox.Right) + padPt;
            double minBottom = line.Min(w => w.BoundingBox.Bottom) - padPt;
            double maxTop = line.Max(w => w.BoundingBox.Top) + padPt;

            double x = Math.Clamp(minLeft / pageWidth, 0.0, 0.98);
            double y = Math.Clamp((pageHeight - maxTop) / pageHeight, 0.0, 0.98);
            double w = Math.Clamp((maxRight - minLeft) / pageWidth, 0.015, 1.0 - x);
            double h = Math.Clamp((maxTop - minBottom) / pageHeight, 0.012, 1.0 - y);

            rects.Add(new NormalizedHighlightRect(x, y, w, h));
        }

        return rects;
    }

    internal static string? ResolveOutlineTermOrSection(
        string title,
        int level,
        string? inheritedTerm
    )
    {
        var normalized = TaskItem.NormalizeTerm(title);
        if (normalized is not null)
            return normalized;

        if (
            level == 1
            && !string.IsNullOrWhiteSpace(title)
            && !title.Equals("Table of Contents", StringComparison.OrdinalIgnoreCase)
            && !title.Contains("Syllabus", StringComparison.OrdinalIgnoreCase)
        )
        {
            return title.Trim();
        }

        return inheritedTerm;
    }

    private static (string? Term, string SectionBreadcrumb)[] BuildPageSectionMap(PdfDocument doc)
    {
        int n = doc.NumberOfPages;
        var map = new (string? Term, string SectionBreadcrumb)[n + 1];
        for (int i = 1; i <= n; i++)
            map[i] = (null, string.Empty);

        if (!doc.TryGetBookmarks(out var bookmarks) || bookmarks.Roots.Count == 0)
            return map;

        var anchors = new List<(int Page, string? Term, string Breadcrumb)>();

        void Walk(
            IEnumerable<BookmarkNode> nodes,
            string? inheritedTerm,
            string parentPath,
            int level
        )
        {
            foreach (var node in nodes)
            {
                var title = (node.Title ?? string.Empty).Trim();
                var term = ResolveOutlineTermOrSection(title, level, inheritedTerm);
                var path = string.IsNullOrEmpty(parentPath) ? title : $"{parentPath} / {title}";

                if (
                    node is DocumentBookmarkNode docNode
                    && docNode.PageNumber >= 1
                    && docNode.PageNumber <= n
                )
                {
                    anchors.Add((docNode.PageNumber, term, path));
                }

                if (node.Children.Count > 0)
                    Walk(node.Children, term, path, level + 1);
            }
        }

        Walk(bookmarks.Roots, null, string.Empty, 1);

        if (anchors.Count == 0)
            return map;

        var ordered = anchors.OrderBy(a => a.Page).ToList();
        int aIdx = 0;
        string? currentTerm = ordered[0].Term;
        string currentCrumb = ordered[0].Breadcrumb;

        for (int p = 1; p <= n; p++)
        {
            while (aIdx < ordered.Count && ordered[aIdx].Page <= p)
            {
                if (ordered[aIdx].Term is not null)
                    currentTerm = ordered[aIdx].Term;
                currentCrumb = ordered[aIdx].Breadcrumb;
                aIdx++;
            }
            map[p] = (currentTerm, currentCrumb);
        }

        return map;
    }

    private static void EnsurePagesExtracted(
        string pdfPath,
        CachedPdfTextData cached,
        IReadOnlyList<int> pagesToEnsure,
        int maxToExtract
    )
    {
        var missing = pagesToEnsure
            .Where(p => p >= 1 && p <= cached.TotalPages && !cached.PageTexts.ContainsKey(p))
            .Take(maxToExtract)
            .ToList();

        if (missing.Count == 0)
            return;

        lock (cached.ExtractionLock)
        {
            missing = [.. missing.Where(p => !cached.PageTexts.ContainsKey(p))];
            if (missing.Count == 0)
                return;

            using var doc = PdfDocument.Open(pdfPath);
            foreach (int p in missing)
            {
                var page = doc.GetPage(p);
                cached.PageTexts[p] = ExtractCleanPageText(page, p, out var figures);
                cached.PageFigures[p] = figures;
            }
        }
    }

    private static string ExtractCleanPageText(
        Page page,
        int pageNumber,
        out IReadOnlyList<ExtractablePdfFigure> figures
    )
    {
        var words = GetVisibleWordsInReadingOrder(page);
        figures = DiscoverPageFigures(page, pageNumber, words);
        if (words.Count == 0)
            return string.Empty;

        double leftMargin = words.Min(w => w.BoundingBox.Left);
        var sb = new StringBuilder();
        double? lastBottom = null;

        foreach (var w in words)
        {
            var token = w.Text.Trim();
            if (string.IsNullOrEmpty(token))
                continue;

            bool isNewLine =
                !lastBottom.HasValue || Math.Abs(w.BoundingBox.Bottom - lastBottom.Value) > 5.0;
            if (isNewLine)
            {
                if (lastBottom.HasValue)
                    sb.AppendLine();

                double indentPts = w.BoundingBox.Left - leftMargin;
                if (indentPts > 14.0)
                {
                    int spaces = Math.Clamp((int)Math.Round(indentPts / 7.0), 2, 16);
                    sb.Append(' ', spaces);
                }
            }
            else if (sb.Length > 0 && sb[^1] != '\n')
            {
                sb.Append(' ');
            }

            sb.Append(token);
            lastBottom = w.BoundingBox.Bottom;
        }

        return sb.ToString().Trim();
    }

    private static List<Word> GetVisibleWordsInReadingOrder(Page page)
    {
        double pw = Math.Max(1.0, page.Width);
        double ph = Math.Max(1.0, page.Height);

        var visible = page.GetWords()
            .Where(w =>
            {
                double cx = (w.BoundingBox.Left + w.BoundingBox.Right) * 0.5;
                double cy = (w.BoundingBox.Bottom + w.BoundingBox.Top) * 0.5;
                return cx >= 0 && cx <= pw && cy >= 0 && cy <= ph;
            })
            .ToList();

        return SortWordsReadingOrder(visible);
    }

    private static List<Word> SortWordsReadingOrder(List<Word> words)
    {
        // PDF coordinates have Y increasing upwards: group into approximate horizontal bands (6pt tolerance), top-to-bottom, left-to-right
        return
        [
            .. words
                .OrderByDescending(w => Math.Round(w.BoundingBox.Bottom / 5.0) * 5.0)
                .ThenBy(w => w.BoundingBox.Left),
        ];
    }

    private static List<string> TokenizeWords(string text)
    {
        return
        [
            .. text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeToken)
                .Where(t => t.Length > 0),
        ];
    }

    private static string NormalizeToken(string raw)
    {
        var chars = raw.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
        return new string(chars);
    }

    private static bool TokensMatch(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return true;
        if (
            a.Length >= 5
            && b.Length >= 5
            && (
                a.StartsWith(b, StringComparison.OrdinalIgnoreCase)
                || b.StartsWith(a, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return true;
        }
        return false;
    }

    private static List<string> CollectCandidateQuotes(QuizQuestionRecord q)
    {
        var quotes = new List<string>();
        void AddIfUseful(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return;
            var trimmed = raw.Trim();
            if (trimmed.Length >= 6 && !quotes.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                quotes.Add(trimmed);
        }

        if (!string.IsNullOrWhiteSpace(q.VerbatimQuote))
        {
            var segments = q.VerbatimQuote.Split(
                ["...", " | ", "; ", " vs. ", " vs "],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );
            if (segments.Length > 1)
            {
                foreach (var seg in segments)
                    AddIfUseful(seg);
            }
            else
            {
                AddIfUseful(q.VerbatimQuote);
            }
        }

        foreach (var anchor in q.EvidenceAnchors)
            AddIfUseful(anchor.VerbatimQuote);

        if (q.Kind == QuizQuestionKind.Ordering && q.Options.Count >= 2 && quotes.Count <= 1)
        {
            foreach (var step in q.Options)
                AddIfUseful(step);
        }

        return quotes;
    }
}
