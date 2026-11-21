using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PdfSharpCore.Drawing;
using PdfSharpCore.Fonts;
using PdfSharpCore.Pdf;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;
using PdfSharpDocument = PdfSharpCore.Pdf.PdfDocument;

namespace ScholarFlow.Core.Pdf;

public sealed record MasterPdfCompileResult(
    string CourseFolder,
    string OutputPdfPath,
    bool WasRegenerated,
    int PageCount
);

/// <summary>
/// Pure C# (.NET 10) port of Compile_School using PDFsharp + PdfPig.
/// Crops whitespace margins via PdfPig text/image bounding boxes, splits landscape slides
/// into 2 portrait A4 pages (or rotates syllabi), builds Cover/Term/TOC pages + PDF bookmarks,
/// and caches source fingerprints so unchanged courses are skipped automatically.
/// </summary>
public sealed partial class MasterPdfService
{
    private const double TargetWidth = 595.0;
    private const double TargetHeight = 842.0;
    private const double SafetyPadding = 30.0;

    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true,
    };

    private static int _fontResolverInitialized;

    [GeneratedRegex(@"/P\s+(\d{10})\b")]
    private static partial Regex UnsignedPdfPermissionRegex();

    public static string GetPdfStatePath(string basinRoot) =>
        Path.Combine(basinRoot, ".scholarflow", "pdf_state.json");

    public static string GetExpectedMasterPdfPath(string basinRoot, string courseFolder) =>
        Path.Combine(basinRoot, courseFolder, $"{courseFolder}_Master.pdf");

    public static string? FindExistingMasterPdfPath(string basinRoot, string courseFolder)
    {
        var coursePath = Path.Combine(basinRoot, courseFolder);
        if (!Directory.Exists(coursePath))
            return null;

        var exact = Path.Combine(coursePath, $"{courseFolder}_Master.pdf");
        if (File.Exists(exact))
            return exact;

        return Directory
            .EnumerateFiles(coursePath, "*_Master.pdf", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Invoked via DI-injected service instance"
    )]
    public async Task<MasterPdfCompileResult> CompileIfStaleAsync(
        string basinRoot,
        string courseFolder,
        string? displayTitle = null,
        bool force = false,
        CancellationToken ct = default,
        IReadOnlyList<string>? configuredTerms = null,
        bool? useStiSyllabusFallback = null
    )
    {
        EnsureFontResolver();

        var coursePath = Path.Combine(basinRoot, courseFolder);
        var title = displayTitle ?? courseFolder.Replace('_', ' ').Trim();
        var outPath = Path.Combine(coursePath, $"{courseFolder}_Master.pdf");

        var (resolvedTerms, resolvedStiFallback) = ResolveBasinCompilationRules(
            basinRoot,
            configuredTerms,
            useStiSyllabusFallback
        );

        var fingerprint = ComputeCourseFingerprint(coursePath);
        var statePath = GetPdfStatePath(basinRoot);
        var state = await LoadStateAsync(statePath, ct);

        if (
            !force
            && File.Exists(outPath)
            && state.TryGetValue(courseFolder, out var prevHash)
            && string.Equals(prevHash, fingerprint, StringComparison.Ordinal)
        )
        {
            return new MasterPdfCompileResult(
                courseFolder,
                outPath,
                WasRegenerated: false,
                PageCount: 0
            );
        }

        int pageCount = await Task.Run(
            () =>
                CompileCourseSync(
                    coursePath,
                    title,
                    outPath,
                    ct,
                    resolvedTerms,
                    resolvedStiFallback
                ),
            ct
        );

        state[courseFolder] = ComputeCourseFingerprint(coursePath);
        await SaveStateAsync(statePath, state, ct);

        return new MasterPdfCompileResult(
            courseFolder,
            outPath,
            WasRegenerated: true,
            PageCount: pageCount
        );
    }

    private static (IReadOnlyList<string>? Terms, bool UseStiFallback) ResolveBasinCompilationRules(
        string basinRoot,
        IReadOnlyList<string>? explicitTerms,
        bool? explicitStiFallback
    )
    {
        if (explicitTerms is not null && explicitStiFallback.HasValue)
            return (explicitTerms, explicitStiFallback.Value);

        var configPath = Services.BasinService.GetBasinConfigPath(basinRoot);
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                var cfg = JsonSerializer.Deserialize<Models.BasinConfig>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                );
                if (cfg is not null)
                {
                    var isSti = string.Equals(
                        cfg.ProviderId,
                        Models.SyncProviderIds.StiElms,
                        StringComparison.OrdinalIgnoreCase
                    );
                    return (
                        explicitTerms ?? cfg.Terms,
                        explicitStiFallback ?? isSti
                    );
                }
            }
            catch
            {
                // Fall back to default rules
            }
        }

        return (explicitTerms, explicitStiFallback ?? true);
    }

    private static int CompileCourseSync(
        string subjectPath,
        string subjectName,
        string outPath,
        CancellationToken ct,
        IReadOnlyList<string>? configuredTerms = null,
        bool useStiSyllabusFallback = true
    )
    {
        bool hasCustomPacketPlan = File.Exists(CoursePacketPlan.GetPacketPlanPath(subjectPath));
        bool hasDirectRootPdfs =
            Directory.Exists(subjectPath)
            && Directory
                .GetFiles(subjectPath, "*.pdf", SearchOption.TopDirectoryOnly)
                .Any(f => !f.EndsWith("_Master.pdf", StringComparison.OrdinalIgnoreCase));

        if (
            hasCustomPacketPlan
            || hasDirectRootPdfs
            || !useStiSyllabusFallback
            || configuredTerms is { Count: 0 }
        )
        {
            var plan = CoursePacketPlan.LoadOrBuildPlan(
                subjectPath,
                configuredTerms,
                useStiSyllabusFallback,
                includeNonPdfByDefault: !useStiSyllabusFallback
            );
            return CompileCourseFromPacketPlanSync(subjectPath, subjectName, outPath, plan, ct);
        }
        using var destDoc = new PdfSharpDocument();
        destDoc.Info.Title = $"{subjectName} - Master Study Packet";

        var visualToc = new List<(int Level, string Text, int LogicalPage)>();
        var sidebarToc = new List<(int Level, string Title, int PhysicalPageOneBased)>();
        var categorized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Main Cover (page index 0)
        AddCenteredTitlePage(destDoc, subjectName, isMainCover: true);
        sidebarToc.Add((1, "Cover Page", 1));

        var allDirs = Directory.Exists(subjectPath)
            ? Directory
                .GetDirectories(subjectPath)
                .Select(Path.GetFileName)
                .Where(n =>
                    !string.IsNullOrEmpty(n)
                    && !n.StartsWith('.')
                    && !n.Equals("ongoing", StringComparison.OrdinalIgnoreCase)
                    && !n.Equals("completed", StringComparison.OrdinalIgnoreCase)
                )
                .Select(n => n!)
                .ToList()
            : [];

        // 2. Syllabus
        var syllabusDir = allDirs.FirstOrDefault(d =>
            d.Contains("syllabus", StringComparison.OrdinalIgnoreCase)
        );
        if (syllabusDir is not null)
        {
            var startLogical = destDoc.PageCount;
            var dirPath = Path.Combine(subjectPath, syllabusDir);
            var files = GetUniquePdfFiles(dirPath);
            if (files.Count > 0)
            {
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    AppendScaledOrSplit(destDoc, f, forceRotate: true);
                }
                visualToc.Add((1, "Course Syllabus", startLogical));
                sidebarToc.Add((1, "Syllabus", startLogical + 1));
                categorized.Add(syllabusDir);
            }
        }

        // 3. Term Logic from term.json (with smart STI 4-term fallback for unmapped numbered prefixes)
        var termConfig = LoadOrInferTermConfig(Path.Combine(subjectPath, "term.json"), allDirs);
        foreach (var (term, prefixes) in termConfig)
        {
            var handouts = new List<string>();
            var activities = new List<string>();

            foreach (var prefix in prefixes)
            {
                var matches = allDirs
                    .Where(d =>
                        d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && !categorized.Contains(d)
                    )
                    .ToList();
                foreach (var m in matches)
                {
                    if (
                        m.Contains("activity", StringComparison.OrdinalIgnoreCase)
                        || m.Contains("quiz", StringComparison.OrdinalIgnoreCase)
                        || m.Contains("seatwork", StringComparison.OrdinalIgnoreCase)
                        || m.Contains("exercise", StringComparison.OrdinalIgnoreCase)
                        || m.Contains("task", StringComparison.OrdinalIgnoreCase)
                    )
                    {
                        activities.Add(m);
                    }
                    else
                    {
                        handouts.Add(m);
                    }
                    categorized.Add(m);
                }
            }

            if (handouts.Count == 0 && activities.Count == 0)
                continue;

            var termLogical = destDoc.PageCount;
            AddCenteredTitlePage(destDoc, term, isMainCover: false);
            visualToc.Add((1, term, termLogical));
            sidebarToc.Add((1, term, termLogical + 1));

            foreach (var mod in handouts.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
            {
                var mPath = Path.Combine(subjectPath, mod);
                var files = GetUniquePdfFiles(mPath);
                if (files.Count == 0)
                    continue;

                var modP = destDoc.PageCount;
                var label = mod.Replace('_', ' ').Trim();
                visualToc.Add((2, label, modP));
                sidebarToc.Add((2, label, modP + 1));

                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    AppendScaledOrSplit(destDoc, f, forceRotate: false);
                }
            }

            if (activities.Count > 0)
            {
                var actHeaderP = destDoc.PageCount;
                visualToc.Add((2, $"{term} Activities", actHeaderP));
                sidebarToc.Add((2, "Activities", actHeaderP + 1));

                foreach (var act in activities.OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
                {
                    var mPath = Path.Combine(subjectPath, act);
                    var files = GetUniquePdfFiles(mPath);
                    if (files.Count == 0)
                        continue;

                    var actP = destDoc.PageCount;
                    var label = act.Replace('_', ' ').Trim();
                    visualToc.Add((3, label, actP));
                    sidebarToc.Add((3, label, actP + 1));

                    foreach (var f in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        AppendScaledOrSplit(destDoc, f, forceRotate: false);
                    }
                }
            }
        }

        // 4. Appendices (uncategorized folders with PDFs)
        var appendixDirs = allDirs
            .Where(d => !categorized.Contains(d))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(d => (Name: d, Files: GetUniquePdfFiles(Path.Combine(subjectPath, d))))
            .Where(x => x.Files.Count > 0)
            .ToList();

        if (appendixDirs.Count > 0)
        {
            var appLogical = destDoc.PageCount;
            AddCenteredTitlePage(destDoc, "Appendices", isMainCover: false);
            visualToc.Add((1, "Appendices", appLogical));
            sidebarToc.Add((1, "Appendices", appLogical + 1));

            foreach (var (appName, files) in appendixDirs)
            {
                var appP = destDoc.PageCount;
                var label = $"Appendix: {appName.Replace('_', ' ').Trim()}";
                visualToc.Add((2, label, appP));
                sidebarToc.Add((2, label, appP + 1));

                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    AppendScaledOrSplit(destDoc, f, forceRotate: false);
                }
            }
        }

        // 5. TOC Injection at index 1
        int tocPagesAdded = InsertTocPages(destDoc, visualToc, insertionIndex: 1);

        // 6. Centered Page Footers (skip cover + TOC pages)
        var footerFont = new XFont("Sans", 10, XFontStyle.Regular);
        int contentStartIndex = 1 + tocPagesAdded;
        for (int i = contentStartIndex; i < destDoc.PageCount; i++)
        {
            using var gfx = XGraphics.FromPdfPage(destDoc.Pages[i]);
            var pageNumText = (i - tocPagesAdded).ToString();
            gfx.DrawString(
                pageNumText,
                footerFont,
                XBrushes.Black,
                new XRect(0, TargetHeight - 36, TargetWidth, 20),
                XStringFormats.TopCenter
            );
        }

        // 7. PDF Outline Bookmarks
        BuildOutlines(destDoc, sidebarToc, tocPagesAdded);

        var tempOut = $"{outPath}.tmp.{Guid.NewGuid():N}";
        destDoc.Save(tempOut);
        File.Move(tempOut, outPath, overwrite: true);

        return destDoc.PageCount;
    }

    private static int CompileCourseFromPacketPlanSync(
        string subjectPath,
        string subjectName,
        string outPath,
        CoursePacketPlan plan,
        CancellationToken ct
    )
    {
        using var destDoc = new PdfSharpDocument();
        destDoc.Info.Title = $"{subjectName} - Master Study Packet";

        var visualToc = new List<(int Level, string Text, int LogicalPage)>();
        var sidebarToc = new List<(int Level, string Title, int PhysicalPageOneBased)>();

        if (plan.IncludeCoverPage)
        {
            AddCenteredTitlePage(destDoc, subjectName, isMainCover: true);
            sidebarToc.Add((1, "Cover Page", 1));
        }

        if (plan.IncludeSyllabusSection)
        {
            var syllabusDir = Directory.Exists(subjectPath)
                ? Directory
                    .GetDirectories(subjectPath)
                    .Select(Path.GetFileName)
                    .FirstOrDefault(d =>
                        d is not null
                        && !d.StartsWith('.')
                        && d.Contains("syllabus", StringComparison.OrdinalIgnoreCase)
                    )
                : null;
            if (syllabusDir is not null)
            {
                var startLogical = destDoc.PageCount;
                var files = GetUniquePdfFiles(Path.Combine(subjectPath, syllabusDir));
                if (files.Count > 0)
                {
                    foreach (var f in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        AppendScaledOrSplit(destDoc, f, forceRotate: true);
                    }
                    visualToc.Add((1, "Course Syllabus", startLogical));
                    sidebarToc.Add((1, "Syllabus", startLogical + 1));
                }
            }
        }

        bool emitSectionCovers = plan.Sections.Count > 1 || plan.IsCustomized;
        foreach (var sec in plan.Sections)
        {
            var activeItems = sec
                .Items.Where(i =>
                    !i.IsExcluded
                    && File.Exists(
                        Path.Combine(
                            subjectPath,
                            i.RelativePath.Replace('/', Path.DirectorySeparatorChar)
                        )
                    )
                )
                .ToList();
            if (activeItems.Count == 0)
                continue;

            int itemTocLevel = 1;
            if (emitSectionCovers && !string.IsNullOrWhiteSpace(sec.Title))
            {
                var secLogical = destDoc.PageCount;
                AddCenteredTitlePage(destDoc, sec.Title, isMainCover: false);
                visualToc.Add((1, sec.Title, secLogical));
                sidebarToc.Add((1, sec.Title, secLogical + 1));
                itemTocLevel = 2;
            }

            foreach (var item in activeItems)
            {
                ct.ThrowIfCancellationRequested();
                var fullPath = Path.Combine(
                    subjectPath,
                    item.RelativePath.Replace('/', Path.DirectorySeparatorChar)
                );
                var startP = destDoc.PageCount;
                var label = string.IsNullOrWhiteSpace(item.DisplayTitle)
                    ? Path.GetFileNameWithoutExtension(item.RelativePath).Replace('_', ' ').Trim()
                    : item.DisplayTitle.Trim();

                var (itemKind, itemLang) = Knowledge.StudySourceClassifier.Classify(fullPath);
                switch (itemKind)
                {
                    case Knowledge.StudySourceKind.Pdf:
                        AppendScaledOrSplit(
                            destDoc,
                            fullPath,
                            forceRotate: item.ForceRotateLandscape,
                            pageRangesExpr: item.PageRanges
                        );
                        break;

                    case Knowledge.StudySourceKind.Image:
                        AppendImagePage(destDoc, fullPath, label);
                        break;

                    case Knowledge.StudySourceKind.Code:
                    case Knowledge.StudySourceKind.Markdown:
                    case Knowledge.StudySourceKind.PlainText:
                        AppendTextOrCodePages(destDoc, fullPath, label, itemKind, itemLang);
                        break;
                }

                if (destDoc.PageCount > startP)
                {
                    visualToc.Add((itemTocLevel, label, startP));
                    sidebarToc.Add((itemTocLevel, label, startP + 1));
                }
            }
        }

        if (destDoc.PageCount == 0)
            AddCenteredTitlePage(destDoc, subjectName, isMainCover: true);

        int tocPagesAdded = 0;
        int tocInsertIdx = plan.IncludeCoverPage && destDoc.PageCount >= 1 ? 1 : 0;
        if (plan.IncludeTableOfContents && visualToc.Count > 0)
        {
            tocPagesAdded = InsertTocPages(destDoc, visualToc, insertionIndex: tocInsertIdx);
        }

        var footerFont = new XFont("Sans", 10, XFontStyle.Regular);
        int contentStartIndex = (plan.IncludeCoverPage ? 1 : 0) + tocPagesAdded;
        for (int i = contentStartIndex; i < destDoc.PageCount; i++)
        {
            using var gfx = XGraphics.FromPdfPage(destDoc.Pages[i]);
            var pageNumText = (i - tocPagesAdded + (plan.IncludeCoverPage ? 0 : 1)).ToString();
            gfx.DrawString(
                pageNumText,
                footerFont,
                XBrushes.Black,
                new XRect(0, TargetHeight - 36, TargetWidth, 20),
                XStringFormats.TopCenter
            );
        }

        BuildOutlines(destDoc, sidebarToc, tocPagesAdded);

        var tempOut = $"{outPath}.tmp.{Guid.NewGuid():N}";
        destDoc.Save(tempOut);
        File.Move(tempOut, outPath, overwrite: true);

        return destDoc.PageCount;
    }

    private static void AppendImagePage(PdfSharpDocument destDoc, string imgPath, string title)
    {
        try
        {
            using var ximg = XImage.FromFile(imgPath);
            var page = destDoc.AddPage();
            page.Width = XUnit.FromPoint(TargetWidth);
            page.Height = XUnit.FromPoint(TargetHeight);

            using var gfx = XGraphics.FromPdfPage(page);
            var headerFont = new XFont("Sans", 11, XFontStyle.Bold);
            var metaFont = new XFont("Sans", 8.5, XFontStyle.Regular);
            gfx.DrawString(
                title,
                headerFont,
                XBrushes.Black,
                new XRect(36, 26, TargetWidth - 72, 18),
                XStringFormats.TopLeft
            );
            gfx.DrawString(
                $"Image · {Path.GetFileName(imgPath)} ({ximg.PixelWidth}×{ximg.PixelHeight})",
                metaFont,
                XBrushes.Gray,
                new XRect(36, 44, TargetWidth - 72, 14),
                XStringFormats.TopLeft
            );
            gfx.DrawLine(new XPen(XColors.LightGray, 0.7), 36, 60, TargetWidth - 36, 60);

            double availW = TargetWidth - 72;
            double availH = TargetHeight - 120;
            double scale = Math.Min(
                availW / Math.Max(1, ximg.PixelWidth),
                availH / Math.Max(1, ximg.PixelHeight)
            );
            double drawW = ximg.PixelWidth * scale;
            double drawH = ximg.PixelHeight * scale;
            double drawX = 36 + (availW - drawW) / 2.0;
            double drawY = 70 + (availH - drawH) / 2.0;

            gfx.DrawImage(ximg, drawX, drawY, drawW, drawH);
        }
        catch
        {
            // Skip unreadable or unsupported image format
        }
    }

    private static void AppendTextOrCodePages(
        PdfSharpDocument destDoc,
        string filePath,
        string title,
        Knowledge.StudySourceKind kind,
        string language
    )
    {
        string[] rawLines;
        try
        {
            rawLines = File.ReadAllLines(filePath);
        }
        catch
        {
            return;
        }

        if (rawLines.Length == 0)
            return;

        bool isCode = kind == Knowledge.StudySourceKind.Code;
        bool isMarkdown = kind == Knowledge.StudySourceKind.Markdown;

        var titleFont = new XFont("Sans", 11, XFontStyle.Bold);
        var metaFont = new XFont("Sans", 8.5, XFontStyle.Regular);
        var headingFont = new XFont("Sans", 11, XFontStyle.Bold);
        var bodyFont = new XFont("Sans", isCode ? 8.5 : 9.5, XFontStyle.Regular);
        var lineNumFont = new XFont("Sans", 7.5, XFontStyle.Regular);

        const double marginLeft = 36;
        const double marginRight = 36;
        const double contentTop = 68;
        const double contentBottom = TargetHeight - 52;
        double lineHeight = isCode ? 12.2 : 13.8;
        int maxCols = isCode ? 88 : 92;

        // Pre-wrap lines into printable visual lines
        var visualLines = new List<(int SourceLine, string Text, bool IsHeading)>();
        for (int i = 0; i < rawLines.Length; i++)
        {
            var expanded = rawLines[i].Replace("\t", "    ");
            bool isHeading =
                isMarkdown
                && expanded.TrimStart().StartsWith('#')
                && expanded.TrimStart().TrimStart('#').StartsWith(' ');
            var displayLine = isHeading
                ? expanded.TrimStart().TrimStart('#', ' ').Trim()
                : expanded;

            if (displayLine.Length <= maxCols)
            {
                visualLines.Add((i + 1, displayLine, isHeading));
            }
            else
            {
                int pos = 0;
                bool first = true;
                while (pos < displayLine.Length)
                {
                    int len = Math.Min(maxCols, displayLine.Length - pos);
                    visualLines.Add((first ? i + 1 : 0, displayLine.Substring(pos, len), isHeading));
                    pos += len;
                    first = false;
                }
            }
        }

        int lineIdx = 0;
        int pagePart = 1;
        var kindTag = isCode
            ? $"Code ({language.ToUpperInvariant()})"
            : (isMarkdown ? "Markdown Notes" : "Plain Text");

        while (lineIdx < visualLines.Count)
        {
            var page = destDoc.AddPage();
            page.Width = XUnit.FromPoint(TargetWidth);
            page.Height = XUnit.FromPoint(TargetHeight);

            using var gfx = XGraphics.FromPdfPage(page);
            var partSuffix = pagePart > 1 ? $" (cont. {pagePart})" : string.Empty;
            gfx.DrawString(
                title + partSuffix,
                titleFont,
                XBrushes.Black,
                new XRect(marginLeft, 24, TargetWidth - marginLeft - marginRight, 18),
                XStringFormats.TopLeft
            );
            gfx.DrawString(
                $"{kindTag} · {Path.GetFileName(filePath)}",
                metaFont,
                XBrushes.Gray,
                new XRect(marginLeft, 42, TargetWidth - marginLeft - marginRight, 14),
                XStringFormats.TopLeft
            );
            gfx.DrawLine(
                new XPen(XColors.LightGray, 0.7),
                marginLeft,
                58,
                TargetWidth - marginRight,
                58
            );

            double y = contentTop;
            while (lineIdx < visualLines.Count && y + lineHeight <= contentBottom)
            {
                var (srcLine, text, isHeading) = visualLines[lineIdx++];
                if (isHeading)
                {
                    y += 4;
                    if (y + lineHeight > contentBottom)
                    {
                        lineIdx--;
                        break;
                    }
                    gfx.DrawString(
                        text,
                        headingFont,
                        XBrushes.Black,
                        new XRect(marginLeft, y, TargetWidth - marginLeft - marginRight, lineHeight + 2),
                        XStringFormats.TopLeft
                    );
                    y += lineHeight + 3;
                }
                else
                {
                    double textX = isCode ? marginLeft + 28 : marginLeft;
                    if (isCode && srcLine > 0)
                    {
                        gfx.DrawString(
                            srcLine.ToString(),
                            lineNumFont,
                            XBrushes.Gray,
                            new XRect(marginLeft, y + 1, 22, lineHeight),
                            XStringFormats.TopRight
                        );
                    }
                    gfx.DrawString(
                        text,
                        bodyFont,
                        XBrushes.Black,
                        new XRect(textX, y, TargetWidth - textX - marginRight, lineHeight),
                        XStringFormats.TopLeft
                    );
                    y += lineHeight;
                }
            }

            pagePart++;
        }
    }

    private readonly record struct ContentBox(
        double X0,
        double Y0,
        double X1,
        double Y1,
        double W,
        double H
    );

    private static void AppendScaledOrSplit(
        PdfSharpDocument destDoc,
        string srcPath,
        bool forceRotate,
        string? pageRangesExpr = null
    )
    {
        List<ContentBox>? pigBoxes = null;
        try
        {
            using var pig = PdfPigDocument.Open(srcPath);
            pigBoxes = [];
            foreach (var p in pig.GetPages())
            {
                pigBoxes.Add(ComputePdfPigContentBox(p));
            }
        }
        catch
        {
            pigBoxes = null;
        }

        using var form = OpenPdfFormSafe(srcPath, out var tempNormPath);
        try
        {
            var allowedPages = CoursePacketPlan.ParsePageRangeFilter(
                pageRangesExpr,
                form.PageCount
            );
            for (int idx = 0; idx < form.PageCount; idx++)
            {
                if (allowedPages is not null && !allowedPages.Contains(idx + 1))
                    continue;

                form.PageNumber = idx + 1;
                double w = form.PointWidth;
                double h = form.PointHeight;
                if (w <= 0 || h <= 0)
                    continue;

                var contentBox =
                    (pigBoxes is not null && idx < pigBoxes.Count)
                        ? pigBoxes[idx]
                        : new ContentBox(0.0, 0.0, w, h, w, h);

                if (contentBox.W < 50 || contentBox.H < 50)
                    contentBox = new ContentBox(0, 0, w, h, w, h);

                if (w > h)
                {
                    if (forceRotate)
                    {
                        var page = AddA4Page(destDoc);
                        using var gfx = XGraphics.FromPdfPage(page);
                        var target = GetTargetRectForContent(contentBox.H, contentBox.W);

                        gfx.Save();
                        gfx.TranslateTransform(
                            target.X + target.Width / 2,
                            target.Y + target.Height / 2
                        );
                        gfx.RotateTransform(90);
                        gfx.TranslateTransform(-(target.Height / 2), -(target.Width / 2));

                        var scale = Math.Min(
                            target.Height / contentBox.W,
                            target.Width / contentBox.H
                        );
                        gfx.IntersectClip(
                            new XRect(0, 0, contentBox.W * scale, contentBox.H * scale)
                        );
                        gfx.DrawImage(
                            form,
                            new XRect(
                                -contentBox.X0 * scale,
                                -contentBox.Y0 * scale,
                                w * scale,
                                h * scale
                            )
                        );
                        gfx.Restore();
                    }
                    else
                    {
                        double midW = w / 2.0;
                        DrawClippedRegion(destDoc, form, w, h, new XRect(0, 0, midW, h));
                        DrawClippedRegion(destDoc, form, w, h, new XRect(midW, 0, midW, h));
                    }
                }
                else
                {
                    DrawClippedRegion(
                        destDoc,
                        form,
                        w,
                        h,
                        new XRect(contentBox.X0, contentBox.Y0, contentBox.W, contentBox.H)
                    );
                }
            }
        }
        finally
        {
            if (tempNormPath is not null && File.Exists(tempNormPath))
            {
                try
                {
                    File.Delete(tempNormPath);
                }
                catch
                { /* ignore */
                }
            }
        }
    }

    /// <summary>
    /// Normalizes unsigned 32-bit /P permissions integers (e.g. /P 4294965444 -> /P -1852     )
    /// that cause PdfSharpCore's EncryptorBase.Initialize -> DictionaryElements.GetInteger("/P")
    /// to throw InvalidCastException ("GetInteger: Object is not an integer.").
    /// Preserves exact byte length so PDF xref byte offsets remain 100% intact.
    /// </summary>
    private static XPdfForm OpenPdfFormSafe(string srcPath, out string? tempNormalizedPath)
    {
        tempNormalizedPath = null;
        NormalizeUnsignedPdfPermissionsInPlace(srcPath);
        return XPdfForm.FromFile(srcPath);
    }

    public static bool NormalizeUnsignedPdfPermissionsInPlace(string pdfPath)
    {
        if (!File.Exists(pdfPath))
            return false;

        var bytes = File.ReadAllBytes(pdfPath);
        var text = Encoding.Latin1.GetString(bytes);
        if (!UnsignedPdfPermissionRegex().IsMatch(text))
            return false;

        bool modified = false;
        var patched = UnsignedPdfPermissionRegex()
            .Replace(
                text,
                m =>
                {
                    if (uint.TryParse(m.Groups[1].Value, out uint u) && u > int.MaxValue)
                    {
                        modified = true;
                        int signed = unchecked((int)u);
                        var rep = $"/P {signed}";
                        return rep.PadRight(m.Value.Length, ' ');
                    }
                    return m.Value;
                }
            );

        if (modified)
        {
            File.WriteAllBytes(pdfPath, Encoding.Latin1.GetBytes(patched));
        }

        return modified;
    }

    internal static List<string> GetUniquePdfFiles(string dirPath)
    {
        if (!Directory.Exists(dirPath))
            return [];

        var allFiles = Directory
            .GetFiles(dirPath, "*.pdf", SearchOption.TopDirectoryOnly)
            .OrderBy(f => Path.GetFileNameWithoutExtension(f).EndsWith('_') ? 1 : 0)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unique = new List<string>();
        var seenStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in allFiles)
        {
            var rawStem = Path.GetFileNameWithoutExtension(file);
            var cleanStem = rawStem.TrimEnd('_', ' ', '.');

            string hash;
            try
            {
                NormalizeUnsignedPdfPermissionsInPlace(file);
                hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
            }
            catch
            {
                hash = cleanStem;
            }

            if (!seenStems.Add(cleanStem) || !seenHashes.Add(hash))
            {
                // If this is a legacy trailing-underscore duplicate (e.g. 05_Handout_1_.pdf alongside 05_Handout_1.pdf), clean it up
                if (rawStem.EndsWith('_'))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    { /* ignore */
                    }
                }
                continue;
            }

            unique.Add(file);
        }

        return unique;
    }

    private static void DrawClippedRegion(
        PdfSharpDocument destDoc,
        XPdfForm form,
        double fullW,
        double fullH,
        XRect srcClip
    )
    {
        var page = AddA4Page(destDoc);
        using var gfx = XGraphics.FromPdfPage(page);
        var target = GetTargetRectForContent(srcClip.Width, srcClip.Height);
        var scale = Math.Min(target.Width / srcClip.Width, target.Height / srcClip.Height);

        gfx.Save();
        gfx.IntersectClip(target);
        gfx.DrawImage(
            form,
            new XRect(
                target.X - srcClip.X * scale,
                target.Y - srcClip.Y * scale,
                fullW * scale,
                fullH * scale
            )
        );
        gfx.Restore();
    }

    private static ContentBox ComputePdfPigContentBox(UglyToad.PdfPig.Content.Page page)
    {
        double pageW = page.Width;
        double pageH = page.Height;

        double minX = double.MaxValue,
            minY = double.MaxValue;
        double maxX = double.MinValue,
            maxY = double.MinValue;
        bool hasContent = false;

        foreach (var word in page.GetWords())
        {
            var b = word.BoundingBox;
            // PdfPig origin is bottom-left; convert to top-left coordinates
            double topY = pageH - b.Top;
            double bottomY = pageH - b.Bottom;
            minX = Math.Min(minX, b.Left);
            minY = Math.Min(minY, topY);
            maxX = Math.Max(maxX, b.Right);
            maxY = Math.Max(maxY, bottomY);
            hasContent = true;
        }

        foreach (var img in page.GetImages())
        {
            var b = img.BoundingBox;
            double topY = pageH - b.Top;
            double bottomY = pageH - b.Bottom;
            minX = Math.Min(minX, b.Left);
            minY = Math.Min(minY, topY);
            maxX = Math.Max(maxX, b.Right);
            maxY = Math.Max(maxY, bottomY);
            hasContent = true;
        }

        if (!hasContent)
            return new ContentBox(0, 0, pageW, pageH, pageW, pageH);

        minX = Math.Max(0, minX - 2);
        minY = Math.Max(0, minY - 2);
        maxX = Math.Min(pageW, maxX + 2);
        maxY = Math.Min(pageH, maxY + 2);

        return new ContentBox(
            minX,
            minY,
            maxX,
            maxY,
            Math.Max(1, maxX - minX),
            Math.Max(1, maxY - minY)
        );
    }

    private static XRect GetTargetRectForContent(double contentW, double contentH)
    {
        double maxW = TargetWidth - (SafetyPadding * 2);
        double maxH = TargetHeight - (SafetyPadding * 2);
        double scale = Math.Min(maxW / contentW, maxH / contentH);
        double newW = contentW * scale;
        double newH = contentH * scale;
        return new XRect((TargetWidth - newW) / 2, (TargetHeight - newH) / 2, newW, newH);
    }

    private static PdfPage AddA4Page(PdfSharpDocument doc)
    {
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(TargetWidth);
        page.Height = XUnit.FromPoint(TargetHeight);
        return page;
    }

    private static void AddCenteredTitlePage(PdfSharpDocument doc, string title, bool isMainCover)
    {
        var page = AddA4Page(doc);
        using var gfx = XGraphics.FromPdfPage(page);
        var font = new XFont(
            "Sans",
            isMainCover ? 36 : 30,
            isMainCover ? XFontStyle.Bold : XFontStyle.Regular
        );

        double usableWidth = TargetWidth - (SafetyPadding * 2);
        var lines = WrapText(gfx, title, font, usableWidth);
        double lineHeight = font.Size * 1.25;
        double totalH = lines.Count * lineHeight;
        double y = (TargetHeight - totalH) / 2;

        foreach (var line in lines)
        {
            gfx.DrawString(
                line,
                font,
                XBrushes.Black,
                new XRect(SafetyPadding, y, usableWidth, lineHeight),
                XStringFormats.TopCenter
            );
            y += lineHeight;
        }
    }

    private static int InsertTocPages(
        PdfSharpDocument doc,
        List<(int Level, string Text, int LogicalPage)> entries,
        int insertionIndex
    )
    {
        int pagesCreated = 1;
        var tocPage = doc.Pages.Insert(insertionIndex);
        tocPage.Width = XUnit.FromPoint(TargetWidth);
        tocPage.Height = XUnit.FromPoint(TargetHeight);

        var gfx = XGraphics.FromPdfPage(tocPage);
        var titleFont = new XFont("Sans", 24, XFontStyle.Bold);
        var l1Font = new XFont("Sans", 13, XFontStyle.Bold);
        var l2Font = new XFont("Sans", 10.5, XFontStyle.Regular);
        var l3Font = new XFont("Sans", 9, XFontStyle.Regular);

        gfx.DrawString(
            "Table of Contents",
            titleFont,
            XBrushes.Black,
            new XRect(0, 45, TargetWidth, 36),
            XStringFormats.TopCenter
        );

        double y = 110;
        double textMargin = SafetyPadding + 10;

        foreach (var (level, text, logicalPage) in entries)
        {
            if (y > TargetHeight - 60)
            {
                gfx.Dispose();
                tocPage = doc.Pages.Insert(insertionIndex + pagesCreated);
                tocPage.Width = XUnit.FromPoint(TargetWidth);
                tocPage.Height = XUnit.FromPoint(TargetHeight);
                gfx = XGraphics.FromPdfPage(tocPage);
                pagesCreated++;
                y = 60;
            }

            double indent =
                textMargin
                + (
                    level == 1 ? 0
                    : level == 2 ? 20
                    : 40
                );
            var font =
                level == 1 ? l1Font
                : level == 2 ? l2Font
                : l3Font;

            gfx.DrawString(text, font, XBrushes.Black, new XPoint(indent, y));
            gfx.DrawString(
                logicalPage.ToString(),
                font,
                XBrushes.Black,
                new XPoint(TargetWidth - textMargin - 25, y)
            );
            y += level == 1 ? 24 : 17;
        }

        gfx.Dispose();
        return pagesCreated;
    }

    private static void BuildOutlines(
        PdfSharpDocument doc,
        List<(int Level, string Title, int PhysicalPageOneBased)> sidebarToc,
        int tocPagesAdded
    )
    {
        doc.Outlines.Add("Table of Contents", doc.Pages[1], true);

        PdfOutline? lastL1 = null;
        PdfOutline? lastL2 = null;

        foreach (var (level, title, physicalP) in sidebarToc)
        {
            int adjZeroBased = (physicalP > 1 ? physicalP + tocPagesAdded : physicalP) - 1;
            adjZeroBased = Math.Clamp(adjZeroBased, 0, doc.PageCount - 1);
            var page = doc.Pages[adjZeroBased];

            if (level == 1)
            {
                lastL1 = doc.Outlines.Add(title, page, true);
                lastL2 = null;
            }
            else if (level == 2 && lastL1 is not null)
            {
                lastL2 = lastL1.Outlines.Add(title, page, true);
            }
            else if (level >= 3 && lastL2 is not null)
            {
                lastL2.Outlines.Add(title, page, true);
            }
            else
            {
                doc.Outlines.Add(title, page, true);
            }
        }
    }

    private static List<string> WrapText(XGraphics gfx, string text, XFont font, double maxWidth)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = new List<string>();

        foreach (var word in words)
        {
            var test = string.Join(' ', current.Append(word));
            if (gfx.MeasureString(test, font).Width <= maxWidth || current.Count == 0)
            {
                current.Add(word);
            }
            else
            {
                lines.Add(string.Join(' ', current));
                current = [word];
            }
        }

        if (current.Count > 0)
            lines.Add(string.Join(' ', current));

        return lines;
    }

    internal static Dictionary<string, List<string>> LoadOrInferTermConfig(
        string termJsonPath,
        List<string> allDirs,
        IReadOnlyList<string>? configuredTerms = null,
        bool useStiSyllabusFallback = true
    )
    {
        var effectiveTerms =
            configuredTerms is not null
                ? configuredTerms.Where(t => !string.IsNullOrWhiteSpace(t)).ToList()
                : ["Prelims", "Midterms", "Prefinals", "Finals"];

        var config = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in effectiveTerms)
            config[t] = [];

        if (File.Exists(termJsonPath))
        {
            try
            {
                var json = File.ReadAllText(termJsonPath);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
                if (loaded is not null)
                {
                    foreach (var (k, v) in loaded)
                        config[k] = v ?? [];
                }
            }
            catch
            {
                // Fall back to inference
            }
        }

        if (!useStiSyllabusFallback)
            return config;

        var assignedPrefixes = config
            .Values.SelectMany(x => x)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Discover any numbered module folders (e.g., 01_Handout_1 .. 09_Handout_1) not covered by term.json
        var numberedPrefixes = allDirs
            .Where(d => d.Length >= 2 && char.IsDigit(d[0]) && char.IsDigit(d[1]))
            .Select(d => d[..2])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unassigned = numberedPrefixes.Where(p => !assignedPrefixes.Contains(p)).ToList();
        if (unassigned.Count > 0)
        {
            var courseDir = Path.GetDirectoryName(termJsonPath);
            var syllabusPdf = courseDir is not null
                ? SyllabusTermParser.FindSyllabusPdfPath(courseDir)
                : null;
            if (syllabusPdf is not null && courseDir is not null)
            {
                var schedule = SyllabusTermParser.ParseTermScheduleFromSyllabus(
                    syllabusPdf,
                    courseDir
                );
                bool enriched = false;
                foreach (var term in SyllabusTermParser.TermOrder)
                {
                    if (!schedule.TermPrefixes.TryGetValue(term, out var mapped))
                        continue;
                    foreach (var prefix in mapped)
                    {
                        if (assignedPrefixes.Contains(prefix))
                            continue;
                        if (!config.TryGetValue(term, out var list))
                        {
                            list = [];
                            config[term] = list;
                        }
                        list.Add(prefix);
                        assignedPrefixes.Add(prefix);
                        enriched = true;
                    }
                }

                if (enriched)
                {
                    try
                    {
                        var json = JsonSerializer.Serialize(config, IndentedJsonOptions);
                        File.WriteAllText(termJsonPath, json);
                    }
                    catch
                    {
                        // Non-fatal if term.json cannot be persisted
                    }
                }
            }
        }

        foreach (var prefix in numberedPrefixes)
        {
            if (assignedPrefixes.Contains(prefix))
                continue;

            if (!int.TryParse(prefix, out int num))
                continue;

            // Standard STI 4-term module distribution fallback
            var targetTerm = num switch
            {
                <= 2 => "Prelims",
                <= 4 => "Midterms",
                <= 6 => "Prefinals",
                _ => "Finals",
            };

            if (!config.TryGetValue(targetTerm, out var list))
            {
                list = [];
                config[targetTerm] = list;
            }

            list.Add(prefix);
        }

        return config;
    }

    public static string ComputeCourseFingerprint(string coursePath)
    {
        if (!Directory.Exists(coursePath))
            return string.Empty;

        var entries = new List<string>();
        var packetJson = Path.Combine(coursePath, "packet.json");
        if (File.Exists(packetJson))
        {
            var packetHash = Convert.ToHexStringLower(
                SHA256.HashData(File.ReadAllBytes(packetJson))
            );
            entries.Add($"packet.json|{packetHash}");
        }

        var termJson = Path.Combine(coursePath, "term.json");
        if (File.Exists(termJson))
        {
            var termHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(termJson)));
            entries.Add($"term.json|{termHash}");
        }

        foreach (
            var rootPdf in Directory
                .GetFiles(coursePath, "*.pdf", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
        )
        {
            var rootName = Path.GetFileName(rootPdf);
            if (rootName.EndsWith("_Master.pdf", StringComparison.OrdinalIgnoreCase))
                continue;
            var rootInfo = new FileInfo(rootPdf);
            entries.Add($"./{rootInfo.Name}|{rootInfo.Length}|{rootInfo.LastWriteTimeUtc.Ticks}");
        }

        foreach (
            var dir in Directory
                .GetDirectories(coursePath)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
        )
        {
            var name = Path.GetFileName(dir);
            if (
                name.StartsWith('.')
                || name.Equals("ongoing", StringComparison.OrdinalIgnoreCase)
                || name.Equals("completed", StringComparison.OrdinalIgnoreCase)
            )
                continue;

            foreach (
                var pdf in Directory
                    .GetFiles(dir, "*.pdf", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            )
            {
                var info = new FileInfo(pdf);
                entries.Add($"{name}/{info.Name}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
            }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', entries)));
        return Convert.ToHexStringLower(hash);
    }

    private static async Task<Dictionary<string, string>> LoadStateAsync(
        string statePath,
        CancellationToken ct
    )
    {
        if (!File.Exists(statePath))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await using var stream = File.OpenRead(statePath);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(
                stream,
                cancellationToken: ct
            ) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task SaveStateAsync(
        string statePath,
        Dictionary<string, string> state,
        CancellationToken ct
    )
    {
        var dir = Path.GetDirectoryName(statePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var temp = $"{statePath}.tmp.{Guid.NewGuid():N}";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, state, IndentedJsonOptions, ct);
        }
        File.Move(temp, statePath, overwrite: true);
    }

    internal static void EnsureFontResolver()
    {
        if (Interlocked.Exchange(ref _fontResolverInitialized, 1) == 0)
        {
            GlobalFontSettings.FontResolver ??= new SystemFontResolver();
        }
    }

    private sealed class SystemFontResolver : IFontResolver
    {
        public string DefaultFontName => "Sans";

        private static readonly string[] RegularCandidates = BuildRegularCandidates();
        private static readonly string[] BoldCandidates = BuildBoldCandidates();

        private static string[] BuildRegularCandidates()
        {
            var list = new List<string>();
            if (OperatingSystem.IsWindows())
            {
                var winFonts = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "Fonts"
                );
                list.Add(Path.Combine(winFonts, "segoeui.ttf"));
                list.Add(Path.Combine(winFonts, "arial.ttf"));
                list.Add(Path.Combine(winFonts, "calibri.ttf"));
                list.Add(Path.Combine(winFonts, "tahoma.ttf"));
            }
            list.AddRange([
                "/usr/share/fonts/noto/NotoSans-Regular.ttf",
                "/usr/share/fonts/TTF/DejaVuSans.ttf",
                "/usr/share/fonts/dejavu/DejaVuSans.ttf",
                "/usr/share/fonts/liberation/LiberationSans-Regular.ttf",
                "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            ]);
            return [.. list];
        }

        private static string[] BuildBoldCandidates()
        {
            var list = new List<string>();
            if (OperatingSystem.IsWindows())
            {
                var winFonts = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "Fonts"
                );
                list.Add(Path.Combine(winFonts, "segoeuib.ttf"));
                list.Add(Path.Combine(winFonts, "arialbd.ttf"));
                list.Add(Path.Combine(winFonts, "calibrib.ttf"));
                list.Add(Path.Combine(winFonts, "tahomabd.ttf"));
            }
            list.AddRange([
                "/usr/share/fonts/noto/NotoSans-Bold.ttf",
                "/usr/share/fonts/TTF/DejaVuSans-Bold.ttf",
                "/usr/share/fonts/dejavu/DejaVuSans-Bold.ttf",
                "/usr/share/fonts/liberation/LiberationSans-Bold.ttf",
                "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
            ]);
            return [.. list];
        }

        public byte[]? GetFont(string faceName)
        {
            var candidates = faceName.Contains("Bold", StringComparison.OrdinalIgnoreCase)
                ? BoldCandidates.Concat(RegularCandidates)
                : RegularCandidates;

            var found = candidates.FirstOrDefault(File.Exists);
            if (found is not null)
                return File.ReadAllBytes(found);

            // Fallback: scan system fonts directory for any .ttf
            var fontDirs = OperatingSystem.IsWindows()
                ? new[]
                {
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        "Fonts"
                    ),
                }
                : ["/usr/share/fonts", "/usr/local/share/fonts"];

            foreach (var dir in fontDirs)
            {
                if (!Directory.Exists(dir))
                    continue;

                var anyTtf =
                    Directory
                        .EnumerateFiles(dir, "*.ttf", SearchOption.AllDirectories)
                        .FirstOrDefault(f =>
                            f.Contains("Regular", StringComparison.OrdinalIgnoreCase)
                        )
                    ?? Directory
                        .EnumerateFiles(dir, "*.ttf", SearchOption.AllDirectories)
                        .FirstOrDefault();

                if (anyTtf is not null)
                    return File.ReadAllBytes(anyTtf);
            }

            return null;
        }

        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
            new(isBold ? "Sans-Bold" : "Sans-Regular");
    }
}
