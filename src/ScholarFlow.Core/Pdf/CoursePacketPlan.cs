using System.Text.Json;
using System.Text.Json.Serialization;
using ScholarFlow.Core.Knowledge;

namespace ScholarFlow.Core.Pdf;

/// <summary>
/// Persisted per-course Master PDF packet organization manifest (<c>packet.json</c>).
/// Allows both automatic provider/syllabus section organization and manual customization
/// of sections/terms, file ordering, file exclusion, and page-range extraction.
/// </summary>
public sealed class CoursePacketPlan
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public int SchemaVersion { get; set; } = 1;
    public bool IsCustomized { get; set; }
    public bool IncludeCoverPage { get; set; } = true;
    public bool IncludeTableOfContents { get; set; } = true;
    public bool IncludeSyllabusSection { get; set; } = true;
    public List<PacketSectionPlan> Sections { get; set; } = [];

    public static string GetPacketPlanPath(string courseDir) =>
        Path.Combine(courseDir, "packet.json");

    public static CoursePacketPlan? TryLoadSavedPlan(string courseDir)
    {
        var path = GetPacketPlanPath(courseDir);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<CoursePacketPlan>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void SavePlan(string courseDir, CoursePacketPlan plan)
    {
        Directory.CreateDirectory(courseDir);
        plan.IsCustomized = true;
        var path = GetPacketPlanPath(courseDir);
        var json = JsonSerializer.Serialize(plan, JsonOptions);
        File.WriteAllText(path, json);
        SyncTermJsonFromPlan(courseDir, plan);
    }

    public static void DeleteSavedPlan(string courseDir)
    {
        var path = GetPacketPlanPath(courseDir);
        if (File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Ignore if locked
            }
        }
    }

    /// <summary>
    /// Synchronizes <c>term.json</c> two-digit module prefixes from the packet plan so
    /// task term inference and Master PDF sections stay aligned.
    /// </summary>
    public static void SyncTermJsonFromPlan(string courseDir, CoursePacketPlan plan)
    {
        var termMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sec in plan.Sections)
        {
            if (string.IsNullOrWhiteSpace(sec.Title))
                continue;

            if (!termMap.TryGetValue(sec.Title, out var prefixes))
            {
                prefixes = [];
                termMap[sec.Title] = prefixes;
            }

            foreach (var item in sec.Items.Where(i => !i.IsExcluded))
            {
                var p = TryExtractLeadingTwoDigits(item.RelativePath);
                if (p is not null && !prefixes.Contains(p, StringComparer.OrdinalIgnoreCase))
                    prefixes.Add(p);
            }
        }

        if (termMap.Count == 0)
            return;

        try
        {
            var termPath = Path.Combine(courseDir, "term.json");
            File.WriteAllText(termPath, JsonSerializer.Serialize(termMap, JsonOptions));
        }
        catch
        {
            // Non-fatal
        }
    }

    private static readonly HashSet<string> InternalPacketIgnoreFiles = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "packet.json",
        "term.json",
        "course.json",
        "syllabus.json",
        "tasks.md",
        "inbox.md",
    };

    private static readonly HashSet<string> PackableImageExtensions = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".bmp",
    };

    /// <summary>
    /// Loads a saved <c>packet.json</c> (merging any newly discovered study files on disk) or builds
    /// a default plan from <c>term.json</c>, configured basin terms, and optional STI syllabus fallback.
    /// </summary>
    public static CoursePacketPlan LoadOrBuildPlan(
        string courseDir,
        IReadOnlyList<string>? configuredTerms = null,
        bool useStiSyllabusFallback = true,
        bool includeNonPdfByDefault = false
    )
    {
        var discoveredFiles = DiscoverCoursePacketItems(courseDir, includeNonPdfByDefault);
        var saved = TryLoadSavedPlan(courseDir);
        if (saved is not null)
        {
            MergeNewDiscoveredItemsIntoPlan(saved, discoveredFiles);
            return saved;
        }

        return BuildDefaultPlan(
            courseDir,
            discoveredFiles,
            configuredTerms,
            useStiSyllabusFallback
        );
    }

    public static List<PacketItemPlan> DiscoverCoursePdfItems(string courseDir) =>
        [
            .. DiscoverCoursePacketItems(courseDir, includeNonPdfByDefault: false)
                .Where(i => i.IsPdf),
        ];

    public static List<PacketItemPlan> DiscoverCoursePacketItems(
        string courseDir,
        bool includeNonPdfByDefault = false
    )
    {
        var items = new List<PacketItemPlan>();
        if (!Directory.Exists(courseDir))
            return items;

        var courseFolderName = Path.GetFileName(Path.GetFullPath(courseDir));

        static bool IsSupportedNonPdfPacketFile(string filePath)
        {
            var fileName = Path.GetFileName(filePath);
            if (fileName.StartsWith('.') || InternalPacketIgnoreFiles.Contains(fileName))
                return false;

            var (kind, _) = StudySourceClassifier.Classify(filePath);
            if (kind == StudySourceKind.Image)
                return PackableImageExtensions.Contains(Path.GetExtension(filePath));

            return kind
                is StudySourceKind.Markdown
                    or StudySourceKind.PlainText
                    or StudySourceKind.Code;
        }

        // 1. Direct PDFs & non-PDF study files in root of courseDir (excluding compiled *_Master.pdf)
        foreach (
            var file in Directory
                .GetFiles(courseDir, "*", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
        )
        {
            var fileName = Path.GetFileName(file);
            if (fileName.StartsWith('.'))
                continue;

            if (fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                if (
                    fileName.EndsWith("_Master.pdf", StringComparison.OrdinalIgnoreCase)
                    || fileName.EndsWith("_.pdf", StringComparison.OrdinalIgnoreCase)
                    || fileName.Equals(
                        $"{courseFolderName}.pdf",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    continue;
                }

                items.Add(
                    new PacketItemPlan
                    {
                        RelativePath = fileName,
                        DisplayTitle = Path.GetFileNameWithoutExtension(fileName)
                            .Replace('_', ' ')
                            .Trim(),
                        IsActivity = IsActivityName(fileName),
                        IsExcluded = false,
                        ForceRotateLandscape = fileName.Contains(
                            "syllabus",
                            StringComparison.OrdinalIgnoreCase
                        ),
                    }
                );
            }
            else if (IsSupportedNonPdfPacketFile(file))
            {
                items.Add(
                    new PacketItemPlan
                    {
                        RelativePath = fileName,
                        DisplayTitle = Path.GetFileNameWithoutExtension(fileName)
                            .Replace('_', ' ')
                            .Trim(),
                        IsActivity = IsActivityName(fileName),
                        IsExcluded = !includeNonPdfByDefault,
                    }
                );
            }
        }

        // 2. Module subdirectory PDFs & non-PDF study files
        foreach (
            var dir in Directory
                .GetDirectories(courseDir)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
        )
        {
            var dirName = Path.GetFileName(dir);
            if (
                string.IsNullOrWhiteSpace(dirName)
                || dirName.StartsWith('.')
                || dirName.Equals("ongoing", StringComparison.OrdinalIgnoreCase)
                || dirName.Equals("completed", StringComparison.OrdinalIgnoreCase)
                || dirName.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || dirName.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || dirName.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            var pdfs = MasterPdfService.GetUniquePdfFiles(dir);
            foreach (var pdf in pdfs)
            {
                var rel = $"{dirName}/{Path.GetFileName(pdf)}";
                var label =
                    pdfs.Count == 1
                        ? dirName.Replace('_', ' ').Trim()
                        : $"{dirName.Replace('_', ' ').Trim()} — {Path.GetFileNameWithoutExtension(pdf).Replace('_', ' ').Trim()}";
                items.Add(
                    new PacketItemPlan
                    {
                        RelativePath = rel,
                        DisplayTitle = label,
                        IsActivity = IsActivityName(dirName) || IsActivityName(pdf),
                        IsExcluded = false,
                        ForceRotateLandscape = dirName.Contains(
                            "syllabus",
                            StringComparison.OrdinalIgnoreCase
                        ),
                    }
                );
            }

            foreach (
                var nonPdf in Directory
                    .GetFiles(dir, "*", SearchOption.TopDirectoryOnly)
                    .Where(f =>
                        !f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                        && IsSupportedNonPdfPacketFile(f)
                    )
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            )
            {
                var rel = $"{dirName}/{Path.GetFileName(nonPdf)}";
                var label =
                    $"{dirName.Replace('_', ' ').Trim()} — {Path.GetFileNameWithoutExtension(nonPdf).Replace('_', ' ').Trim()}";
                items.Add(
                    new PacketItemPlan
                    {
                        RelativePath = rel,
                        DisplayTitle = label,
                        IsActivity = IsActivityName(dirName) || IsActivityName(nonPdf),
                        IsExcluded = !includeNonPdfByDefault,
                    }
                );
            }
        }

        return items;
    }

    private static CoursePacketPlan BuildDefaultPlan(
        string courseDir,
        List<PacketItemPlan> discoveredItems,
        IReadOnlyList<string>? configuredTerms,
        bool useStiSyllabusFallback
    )
    {
        var plan = new CoursePacketPlan { IsCustomized = false };
        var assignedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Exclude syllabus from term sections if IncludeSyllabusSection handles it
        foreach (
            var syl in discoveredItems.Where(i =>
                i.RelativePath.Contains("syllabus", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            assignedPaths.Add(syl.RelativePath);
        }

        var effectiveTerms = configuredTerms is not null
            ? configuredTerms.Where(t => !string.IsNullOrWhiteSpace(t)).ToList()
            : ["Prelims", "Midterms", "Prefinals", "Finals"];

        // Case 1: Local / Manual basin with zero terms configured -> flat single section
        if (effectiveTerms.Count == 0)
        {
            var flatItems = discoveredItems
                .Where(i => !assignedPaths.Contains(i.RelativePath))
                .ToList();
            plan.Sections.Add(
                new PacketSectionPlan { Title = "Study Materials", Items = flatItems }
            );
            return plan;
        }

        // Case 2: Term-structured course
        var allSubDirs = Directory.Exists(courseDir)
            ? Directory
                .GetDirectories(courseDir)
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

        var termConfig = MasterPdfService.LoadOrInferTermConfig(
            Path.Combine(courseDir, "term.json"),
            allSubDirs,
            effectiveTerms,
            useStiSyllabusFallback
        );

        foreach (var termName in effectiveTerms)
        {
            var sec = new PacketSectionPlan { Title = termName };
            if (termConfig.TryGetValue(termName, out var prefixes))
            {
                foreach (var prefix in prefixes)
                {
                    foreach (
                        var item in discoveredItems.Where(i =>
                            !assignedPaths.Contains(i.RelativePath)
                            && i.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        )
                    )
                    {
                        sec.Items.Add(item);
                        assignedPaths.Add(item.RelativePath);
                    }
                }
            }
            plan.Sections.Add(sec);
        }

        var remaining = discoveredItems
            .Where(i => !assignedPaths.Contains(i.RelativePath))
            .ToList();
        if (remaining.Count > 0)
        {
            if (plan.Sections.Count == 1 && plan.Sections[0].Items.Count == 0)
            {
                plan.Sections[0].Items.AddRange(remaining);
            }
            else
            {
                plan.Sections.Add(
                    new PacketSectionPlan { Title = "Appendices", Items = remaining }
                );
            }
        }

        return plan;
    }

    private static void MergeNewDiscoveredItemsIntoPlan(
        CoursePacketPlan plan,
        List<PacketItemPlan> discovered
    )
    {
        var existingPaths = plan
            .Sections.SelectMany(s => s.Items)
            .Select(i => i.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = discovered
            .Where(i =>
                !existingPaths.Contains(i.RelativePath)
                && !i.RelativePath.Contains("syllabus", StringComparison.OrdinalIgnoreCase)
            )
            .ToList();

        if (missing.Count == 0)
            return;

        var targetSec =
            plan.Sections.LastOrDefault() ?? new PacketSectionPlan { Title = "Study Materials" };
        if (plan.Sections.Count == 0)
            plan.Sections.Add(targetSec);

        targetSec.Items.AddRange(missing);
    }

    /// <summary>
    /// Parses a page-range expression like <c>"1-5, 8, 11-14"</c> into a 1-based page set,
    /// or returns <c>null</c> when all pages should be included.
    /// </summary>
    public static HashSet<int>? ParsePageRangeFilter(string? rangeExpr, int totalPages)
    {
        if (string.IsNullOrWhiteSpace(rangeExpr) || totalPages <= 0)
            return null;

        var allowed = new HashSet<int>();
        var parts = rangeExpr.Split(
            [',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

        foreach (var part in parts)
        {
            var dashIdx = part.IndexOf('-');
            if (dashIdx < 0)
            {
                if (
                    int.TryParse(part.Trim(), out int single)
                    && single >= 1
                    && single <= totalPages
                )
                    allowed.Add(single);
            }
            else
            {
                var startStr = part[..dashIdx].Trim();
                var endStr = part[(dashIdx + 1)..].Trim();
                int start = string.IsNullOrEmpty(startStr)
                    ? 1
                    : (int.TryParse(startStr, out int s) ? Math.Max(1, s) : -1);
                int end = string.IsNullOrEmpty(endStr)
                    ? totalPages
                    : (int.TryParse(endStr, out int e) ? Math.Min(totalPages, e) : -1);

                if (start >= 1 && end >= start)
                {
                    for (int p = start; p <= Math.Min(end, totalPages); p++)
                        allowed.Add(p);
                }
            }
        }

        return allowed.Count > 0 ? allowed : null;
    }

    private static bool IsActivityName(string name) =>
        name.Contains("activity", StringComparison.OrdinalIgnoreCase)
        || name.Contains("quiz", StringComparison.OrdinalIgnoreCase)
        || name.Contains("seatwork", StringComparison.OrdinalIgnoreCase)
        || name.Contains("exercise", StringComparison.OrdinalIgnoreCase)
        || name.Contains("task", StringComparison.OrdinalIgnoreCase);

    private static string? TryExtractLeadingTwoDigits(string relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath) || relPath.Length < 2)
            return null;
        return char.IsDigit(relPath[0]) && char.IsDigit(relPath[1]) ? relPath[..2] : null;
    }
}

public sealed class PacketSectionPlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = "Section";
    public List<PacketItemPlan> Items { get; set; } = [];
}

public sealed class PacketItemPlan
{
    public string RelativePath { get; set; } = string.Empty;
    public string DisplayTitle { get; set; } = string.Empty;
    public string? PageRanges { get; set; }
    public bool IsActivity { get; set; }
    public bool IsExcluded { get; set; }
    public bool ForceRotateLandscape { get; set; }

    [JsonIgnore]
    public StudySourceKind ResolvedKind => StudySourceClassifier.Classify(RelativePath).Kind;

    [JsonIgnore]
    public string ResolvedLanguage => StudySourceClassifier.Classify(RelativePath).Language;

    [JsonIgnore]
    public bool IsPdf => ResolvedKind == StudySourceKind.Pdf;
}
