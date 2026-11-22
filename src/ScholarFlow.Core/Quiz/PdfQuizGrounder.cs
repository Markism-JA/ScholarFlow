using System.Collections.Concurrent;
using System.Text;
using ScholarFlow.Core.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Outline;

namespace ScholarFlow.Core.Quiz;

public sealed record GroundedPdfPage(
    int PageNumber,
    string? Term,
    string SectionBreadcrumb,
    string Text
);

/// <summary>
/// Extracts bookmark-aware grounded page chunks from Master/Handout PDFs and resolves
/// verbatim quote evidence anchors into exact normalized [0..1] highlight bounding boxes via PdfPig.
/// </summary>
public static class PdfQuizGrounder
{
    private sealed record CachedPdfTextData(
        long LastWriteTicks,
        int TotalPages,
        (string? Term, string SectionBreadcrumb)[] PageSections,
        ConcurrentDictionary<int, string> PageTexts,
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

            // Skip cover pages, term divider pages, table of contents, or nearly empty pages
            if (
                text.Length < 180
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
                    Text: text
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
                        Text: text
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
            if (
                maxChars < int.MaxValue
                && sb.Length + header.Length + p.Text.Length + 8 > maxChars
                && sb.Length > 0
            )
                break;

            sb.AppendLine(header);
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
            if (img.TryGetPng(out var pngBytes) && pngBytes is { Length: > 0 })
            {
                File.WriteAllBytes(outPath, pngBytes);
                saved.Add(outPath);
            }
        }
        return saved;
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
                cached.PageTexts[p] = ExtractCleanPageText(doc.GetPage(p));
            }
        }
    }

    private static string ExtractCleanPageText(Page page)
    {
        var words = GetVisibleWordsInReadingOrder(page);
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
