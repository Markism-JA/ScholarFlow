using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ScholarFlow.Core.Database;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Pdf;
using ScholarFlow.Core.Quiz;
using ScholarFlow.Core.Services;
using UglyToad.PdfPig;

namespace ScholarFlow.Core.Knowledge;

/// <summary>
/// Incremental Knowledge Hub indexing engine backed by SQLite FTS5 (porter unicode61).
/// Tracks (mtime_ticks, size_bytes) in knowledge_files to skip unchanged files in O(1),
/// prunes deleted files via knowledge_chunk_map, and extracts fine-grained chunks across
/// PDFs, Markdown, Plain Text, Source Code, Images, and Quizzes.
/// </summary>
public sealed partial class KnowledgeIndexService
{
    private readonly SemaphoreSlim _indexRunLock = new(1, 1);

    private sealed record DiscoveredTarget(
        string FileKey,
        string PhysicalPath,
        string CourseFolder,
        string CourseAlias,
        string SourceKind,
        string Language,
        long MtimeTicks,
        long SizeBytes,
        QuizSessionRecord? Quiz = null
    );

    public async Task<KnowledgeIndexSummary> IndexBasinIncrementalAsync(
        string basinRoot,
        IReadOnlyList<BasinCourseEntry>? courses = null,
        IReadOnlyList<QuizSessionRecord>? quizzes = null,
        bool forceReindex = false,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(basinRoot) || !Directory.Exists(basinRoot))
            return new KnowledgeIndexSummary(0, 0, 0, 0, 0, 0, 0);

        var sw = Stopwatch.StartNew();
        await _indexRunLock.WaitAsync(ct);
        try
        {
            if (forceReindex)
            {
                await ScholarFlowDatabase.ClearKnowledgeIndexAsync(basinRoot, ct);
            }

            var existingSnapshot = await ScholarFlowDatabase.GetKnowledgeFileIndexSnapshotAsync(
                basinRoot,
                ct
            );

            var activeCourses = courses ?? await new BasinService().LoadCoursesAsync(basinRoot);
            var activeQuizzes = quizzes
                ?? await ScholarFlowDatabase.LoadQuizSessionsAsync(basinRoot, ct: ct);

            var courseAliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in activeCourses)
            {
                courseAliasMap[c.FolderName] = c.EffectiveAlias;
            }

            var discovered = DiscoverAllTargets(
                basinRoot,
                activeCourses,
                activeQuizzes,
                courseAliasMap
            );

            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var toIndex = new List<DiscoveredTarget>();
            int skippedCount = 0;

            foreach (var target in discovered)
            {
                ct.ThrowIfCancellationRequested();
                seenKeys.Add(target.FileKey);

                if (
                    !forceReindex
                    && existingSnapshot.TryGetValue(target.FileKey, out var snap)
                    && snap.MtimeTicks == target.MtimeTicks
                    && snap.SizeBytes == target.SizeBytes
                )
                {
                    skippedCount++;
                    continue;
                }

                toIndex.Add(target);
            }

            var toDelete = existingSnapshot.Keys.Where(k => !seenKeys.Contains(k)).ToList();

            if (toIndex.Count == 0 && toDelete.Count == 0)
            {
                var (currFiles, currChunks) =
                    await ScholarFlowDatabase.GetKnowledgeIndexCountsAsync(basinRoot, ct);
                sw.Stop();
                return new KnowledgeIndexSummary(
                    FilesIndexed: 0,
                    FilesSkipped: skippedCount,
                    FilesPruned: 0,
                    ChunksWritten: 0,
                    TotalIndexedFiles: currFiles,
                    TotalIndexedChunks: currChunks,
                    ElapsedMs: sw.Elapsed.TotalMilliseconds
                );
            }

            // Build image -> linked quiz questions lookup for richer image indexing
            var questionsByImage = BuildQuestionsByImageMap(activeQuizzes);

            // Extract chunks outside DbLock (parallelized across CPU cores for file I/O & parsing)
            var extractedBag = new ConcurrentBag<KnowledgeFileIndexEntry>();
            if (toIndex.Count == 1)
            {
                var entry = ExtractTargetEntry(toIndex[0], questionsByImage);
                if (entry is not null)
                    extractedBag.Add(entry);
            }
            else if (toIndex.Count > 1)
            {
                Parallel.ForEach(
                    toIndex,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 4),
                        CancellationToken = ct,
                    },
                    target =>
                    {
                        var entry = ExtractTargetEntry(target, questionsByImage);
                        if (entry is not null)
                            extractedBag.Add(entry);
                    }
                );
            }

            var upsertList = extractedBag.ToList();
            int chunksWritten = upsertList.Sum(e => e.Chunks.Count);

            var (totalFiles, totalChunks) = await ScholarFlowDatabase.ApplyKnowledgeIndexBatchAsync(
                basinRoot,
                upsertList,
                toDelete,
                ct
            );

            sw.Stop();
            return new KnowledgeIndexSummary(
                FilesIndexed: upsertList.Count,
                FilesSkipped: skippedCount,
                FilesPruned: toDelete.Count,
                ChunksWritten: chunksWritten,
                TotalIndexedFiles: totalFiles,
                TotalIndexedChunks: totalChunks,
                ElapsedMs: sw.Elapsed.TotalMilliseconds
            );
        }
        finally
        {
            _indexRunLock.Release();
        }
    }

    private static List<DiscoveredTarget> DiscoverAllTargets(
        string basinRoot,
        IReadOnlyList<BasinCourseEntry> courses,
        IReadOnlyList<QuizSessionRecord> quizzes,
        Dictionary<string, string> courseAliasMap
    )
    {
        var targets = new List<DiscoveredTarget>();
        var addedPhysicalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var course in courses)
        {
            var courseDir = Path.Combine(basinRoot, course.FolderName);
            if (!Directory.Exists(courseDir))
                continue;

            var alias = course.EffectiveAlias;

            // 1. Course PDFs (Master PDF + standalone PDFs + any handout newer than Master PDF)
            foreach (var pdfPath in DiscoverCoursePdfs(basinRoot, course.FolderName, courseDir))
            {
                if (!addedPhysicalPaths.Add(pdfPath))
                    continue;

                if (!TryStatFile(pdfPath, out long ticks, out long size))
                    continue;

                targets.Add(
                    new DiscoveredTarget(
                        FileKey: pdfPath,
                        PhysicalPath: pdfPath,
                        CourseFolder: course.FolderName,
                        CourseAlias: alias,
                        SourceKind: "pdf",
                        Language: "pdf",
                        MtimeTicks: ticks,
                        SizeBytes: size
                    )
                );
            }

            // 2. Course Non-PDF Study Files (Code, Markdown, PlainText, Image)
            foreach (
                var desc in FileSourceQuizSupport.DiscoverCourseStudyFiles(courseDir, maxFiles: 500)
            )
            {
                if (!addedPhysicalPaths.Add(desc.FilePath))
                    continue;

                if (!TryStatFile(desc.FilePath, out long ticks, out long size))
                    continue;

                var kindToken = ToSourceKindToken(desc.Kind);
                if (string.IsNullOrEmpty(kindToken))
                    continue;

                targets.Add(
                    new DiscoveredTarget(
                        FileKey: desc.FilePath,
                        PhysicalPath: desc.FilePath,
                        CourseFolder: course.FolderName,
                        CourseAlias: alias,
                        SourceKind: kindToken,
                        Language: desc.Language,
                        MtimeTicks: ticks,
                        SizeBytes: size
                    )
                );
            }
        }

        // 3. Also discover any top-level courses/directories not yet in courses.json
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(basinRoot))
            {
                var folderName = Path.GetFileName(dir);
                if (folderName.StartsWith('.') || courseAliasMap.ContainsKey(folderName))
                    continue;

                var fallbackAlias = CourseAliasGenerator.Generate(folderName);
                courseAliasMap[folderName] = fallbackAlias;

                foreach (var pdfPath in DiscoverCoursePdfs(basinRoot, folderName, dir))
                {
                    if (
                        !addedPhysicalPaths.Add(pdfPath)
                        || !TryStatFile(pdfPath, out long ticks, out long size)
                    )
                        continue;

                    targets.Add(
                        new DiscoveredTarget(
                            FileKey: pdfPath,
                            PhysicalPath: pdfPath,
                            CourseFolder: folderName,
                            CourseAlias: fallbackAlias,
                            SourceKind: "pdf",
                            Language: "pdf",
                            MtimeTicks: ticks,
                            SizeBytes: size
                        )
                    );
                }

                foreach (
                    var desc in FileSourceQuizSupport.DiscoverCourseStudyFiles(dir, maxFiles: 300)
                )
                {
                    if (
                        !addedPhysicalPaths.Add(desc.FilePath)
                        || !TryStatFile(desc.FilePath, out long ticks, out long size)
                    )
                        continue;

                    var kindToken = ToSourceKindToken(desc.Kind);
                    if (string.IsNullOrEmpty(kindToken))
                        continue;

                    targets.Add(
                        new DiscoveredTarget(
                            FileKey: desc.FilePath,
                            PhysicalPath: desc.FilePath,
                            CourseFolder: folderName,
                            CourseAlias: fallbackAlias,
                            SourceKind: kindToken,
                            Language: desc.Language,
                            MtimeTicks: ticks,
                            SizeBytes: size
                        )
                    );
                }
            }
        }
        catch
        {
            // Ignore directory enumeration errors
        }

        // 4. Saved Quizzes
        foreach (var quiz in quizzes)
        {
            var alias = courseAliasMap.TryGetValue(quiz.CourseFolder, out var mapped)
                ? mapped
                : CourseAliasGenerator.Generate(quiz.CourseFolder);
            var (fingerprintTicks, virtualSize) = ComputeQuizFingerprint(quiz);

            targets.Add(
                new DiscoveredTarget(
                    FileKey: $"quiz://{quiz.Id}",
                    PhysicalPath: quiz.SourcePdfPath,
                    CourseFolder: quiz.CourseFolder,
                    CourseAlias: alias,
                    SourceKind: "quiz",
                    Language: "quiz",
                    MtimeTicks: fingerprintTicks,
                    SizeBytes: virtualSize,
                    Quiz: quiz
                )
            );
        }

        return targets;
    }

    private static List<string> DiscoverCoursePdfs(
        string basinRoot,
        string courseFolder,
        string courseDir
    )
    {
        var list = new List<string>();
        var masterPdf = MasterPdfService.FindExistingMasterPdfPath(basinRoot, courseFolder);
        long masterTicks = 0;

        if (!string.IsNullOrWhiteSpace(masterPdf) && File.Exists(masterPdf))
        {
            list.Add(masterPdf);
            try
            {
                masterTicks = File.GetLastWriteTimeUtc(masterPdf).Ticks;
            }
            catch
            {
                masterTicks = 0;
            }
        }

        try
        {
            foreach (var rootPdf in Directory.EnumerateFiles(courseDir, "*.pdf"))
            {
                var name = Path.GetFileName(rootPdf);
                if (
                    name.StartsWith('.')
                    || name.EndsWith("_.pdf", StringComparison.OrdinalIgnoreCase)
                    || list.Any(p => p.Equals(rootPdf, StringComparison.OrdinalIgnoreCase))
                )
                    continue;
                list.Add(rootPdf);
            }

            foreach (var subDir in Directory.EnumerateDirectories(courseDir))
            {
                var subName = Path.GetFileName(subDir);
                if (
                    subName.StartsWith('.')
                    || subName.Equals("completed", StringComparison.OrdinalIgnoreCase)
                    || subName.Equals("ongoing", StringComparison.OrdinalIgnoreCase)
                    || subName.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || subName.Equals("obj", StringComparison.OrdinalIgnoreCase)
                )
                    continue;

                foreach (var subPdf in Directory.EnumerateFiles(subDir, "*.pdf"))
                {
                    var name = Path.GetFileName(subPdf);
                    if (
                        name.StartsWith('.')
                        || name.EndsWith("_.pdf", StringComparison.OrdinalIgnoreCase)
                    )
                        continue;

                    // If Master PDF exists and is up-to-date with this subdirectory handout, skip duplicate indexing
                    if (masterTicks > 0)
                    {
                        try
                        {
                            long subTicks = File.GetLastWriteTimeUtc(subPdf).Ticks;
                            if (subTicks <= masterTicks)
                                continue;
                        }
                        catch
                        {
                            continue;
                        }
                    }

                    list.Add(subPdf);
                }
            }
        }
        catch
        {
            // Ignore unreadable directories
        }

        return list;
    }

    private static KnowledgeFileIndexEntry? ExtractTargetEntry(
        DiscoveredTarget target,
        Dictionary<string, List<QuizQuestionRecord>> questionsByImage
    )
    {
        try
        {
            IReadOnlyList<KnowledgeChunkRecord> chunks = target.SourceKind switch
            {
                "pdf" => ExtractPdfChunks(
                    target.PhysicalPath,
                    target.CourseFolder,
                    target.CourseAlias
                ),
                "md" => ExtractMarkdownOrTextChunks(
                    target.PhysicalPath,
                    target.CourseFolder,
                    target.CourseAlias,
                    StudySourceKind.Markdown
                ),
                "text" => ExtractMarkdownOrTextChunks(
                    target.PhysicalPath,
                    target.CourseFolder,
                    target.CourseAlias,
                    StudySourceKind.PlainText
                ),
                "code" => ExtractCodeChunks(
                    target.PhysicalPath,
                    target.CourseFolder,
                    target.CourseAlias,
                    target.Language
                ),
                "image" => ExtractImageChunks(
                    target.PhysicalPath,
                    target.CourseFolder,
                    target.CourseAlias,
                    target.Language,
                    questionsByImage.GetValueOrDefault(target.PhysicalPath) ?? []
                ),
                "quiz" when target.Quiz is not null => ExtractQuizChunks(
                    target.Quiz,
                    target.CourseAlias
                ),
                _ => [],
            };

            return new KnowledgeFileIndexEntry(
                FileKey: target.FileKey,
                CourseFolder: target.CourseFolder,
                CourseAlias: target.CourseAlias,
                SourceKind: target.SourceKind,
                MtimeTicks: target.MtimeTicks,
                SizeBytes: target.SizeBytes,
                Chunks: chunks
            );
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<KnowledgeChunkRecord> ExtractPdfChunks(
        string pdfPath,
        string courseFolder,
        string courseAlias
    )
    {
        var chunks = new List<KnowledgeChunkRecord>();
        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            return chunks;

        var fileName = Path.GetFileName(pdfPath);
        var groundedPages = PdfQuizGrounder.ExtractGroundedPages(pdfPath, maxPages: 600);

        if (groundedPages.Count > 0)
        {
            foreach (var p in groundedPages)
            {
                var tags = !string.IsNullOrWhiteSpace(p.Term) ? $"pdf {p.Term}" : "pdf";
                chunks.Add(
                    new KnowledgeChunkRecord(
                        ChunkId: $"pdf:{pdfPath}:p{p.PageNumber}",
                        FilePath: pdfPath,
                        CourseFolder: courseFolder,
                        CourseAlias: courseAlias,
                        SourceKind: "pdf",
                        Title: fileName,
                        SectionTitle: p.SectionBreadcrumb,
                        LocationLabel: $"PDF · p.{p.PageNumber}",
                        StartLine: 0,
                        EndLine: 0,
                        PageNumber: p.PageNumber,
                        QuizId: string.Empty,
                        QuestionId: string.Empty,
                        Tags: tags,
                        Content: p.Text
                    )
                );
            }
            return chunks;
        }

        // Fallback for short-page PDFs (< 180 chars per page) so short slides/diagrams are still indexed
        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            for (int i = 1; i <= doc.NumberOfPages; i++)
            {
                var raw = doc.GetPage(i).Text?.Trim() ?? string.Empty;
                if (raw.Length < 12)
                    continue;

                chunks.Add(
                    new KnowledgeChunkRecord(
                        ChunkId: $"pdf:{pdfPath}:p{i}",
                        FilePath: pdfPath,
                        CourseFolder: courseFolder,
                        CourseAlias: courseAlias,
                        SourceKind: "pdf",
                        Title: fileName,
                        SectionTitle: $"Page {i}",
                        LocationLabel: $"PDF · p.{i}",
                        StartLine: 0,
                        EndLine: 0,
                        PageNumber: i,
                        QuizId: string.Empty,
                        QuestionId: string.Empty,
                        Tags: "pdf",
                        Content: raw
                    )
                );
            }
        }
        catch
        {
            // Ignore unreadable PDF
        }

        return chunks;
    }

    public static IReadOnlyList<KnowledgeChunkRecord> ExtractMarkdownOrTextChunks(
        string filePath,
        string courseFolder,
        string courseAlias,
        StudySourceKind kind
    )
    {
        var chunks = new List<KnowledgeChunkRecord>();
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return chunks;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(filePath);
        }
        catch
        {
            return chunks;
        }

        if (lines.Length == 0)
            return chunks;

        var fileName = Path.GetFileName(filePath);
        var kindToken = kind == StudySourceKind.Markdown ? "md" : "text";
        var kindBadge = kind == StudySourceKind.Markdown ? "MD" : "TEXT";

        const int maxLinesPerChunk = 55;
        string currentHeading = Path.GetFileNameWithoutExtension(filePath);
        int blockStartLine = 1;
        var sb = new StringBuilder();

        void FlushChunk(int endLine)
        {
            var body = sb.ToString().Trim();
            sb.Clear();
            if (body.Length < 4)
                return;

            int s = Math.Clamp(blockStartLine, 1, lines.Length);
            int e = Math.Clamp(endLine, s, lines.Length);

            chunks.Add(
                new KnowledgeChunkRecord(
                    ChunkId: $"{kindToken}:{filePath}:L{s}-{e}",
                    FilePath: filePath,
                    CourseFolder: courseFolder,
                    CourseAlias: courseAlias,
                    SourceKind: kindToken,
                    Title: fileName,
                    SectionTitle: currentHeading,
                    LocationLabel: $"{kindBadge} · L{s}–L{e}",
                    StartLine: s,
                    EndLine: e,
                    PageNumber: 1,
                    QuizId: string.Empty,
                    QuestionId: string.Empty,
                    Tags: $"{kindToken} {currentHeading}",
                    Content: body
                )
            );
        }

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            var line = lines[i];
            var trimmed = line.Trim();

            bool isHeading =
                kind == StudySourceKind.Markdown
                && trimmed.StartsWith('#')
                && trimmed.TrimStart('#').StartsWith(' ');

            if (isHeading)
            {
                if (sb.Length > 0)
                    FlushChunk(lineNum - 1);

                currentHeading = trimmed.TrimStart('#', ' ').Trim();
                blockStartLine = lineNum;
                sb.AppendLine(line);
                continue;
            }

            if (lineNum - blockStartLine >= maxLinesPerChunk && sb.Length > 0)
            {
                FlushChunk(lineNum - 1);
                blockStartLine = lineNum;
            }

            sb.AppendLine(line);
        }

        if (sb.Length > 0)
            FlushChunk(lines.Length);

        return chunks;
    }

    public static IReadOnlyList<KnowledgeChunkRecord> ExtractCodeChunks(
        string filePath,
        string courseFolder,
        string courseAlias,
        string language
    )
    {
        var chunks = new List<KnowledgeChunkRecord>();
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return chunks;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(filePath);
        }
        catch
        {
            return chunks;
        }

        if (lines.Length == 0)
            return chunks;

        var fileName = Path.GetFileName(filePath);
        var lang = string.IsNullOrWhiteSpace(language) ? "code" : language.ToLowerInvariant();
        var langUpper = lang.ToUpperInvariant();

        const int minLinesBeforeSplit = 14;
        const int maxLinesPerChunk = 60;

        string currentSymbol = Path.GetFileNameWithoutExtension(filePath);
        int blockStartLine = 1;
        var sb = new StringBuilder();

        void FlushChunk(int endLine)
        {
            var body = sb.ToString().Trim();
            sb.Clear();
            if (body.Length < 4)
                return;

            int s = Math.Clamp(blockStartLine, 1, lines.Length);
            int e = Math.Clamp(endLine, s, lines.Length);

            chunks.Add(
                new KnowledgeChunkRecord(
                    ChunkId: $"code:{filePath}:L{s}-{e}",
                    FilePath: filePath,
                    CourseFolder: courseFolder,
                    CourseAlias: courseAlias,
                    SourceKind: "code",
                    Title: fileName,
                    SectionTitle: currentSymbol,
                    LocationLabel: $"CODE ({langUpper}) · L{s}–L{e}",
                    StartLine: s,
                    EndLine: e,
                    PageNumber: 1,
                    QuizId: string.Empty,
                    QuestionId: string.Empty,
                    Tags: $"code {lang} {currentSymbol}",
                    Content: body
                )
            );
        }

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            var line = lines[i];
            var trimmed = line.Trim();

            var matchedSymbol = TryMatchCodeSymbol(trimmed);
            if (matchedSymbol is not null)
            {
                if (lineNum - blockStartLine >= minLinesBeforeSplit && sb.Length > 0)
                {
                    FlushChunk(lineNum - 1);
                    blockStartLine = lineNum;
                }
                currentSymbol = matchedSymbol;
            }
            else if (lineNum - blockStartLine >= maxLinesPerChunk && sb.Length > 0)
            {
                FlushChunk(lineNum - 1);
                blockStartLine = lineNum;
            }

            sb.AppendLine(line);
        }

        if (sb.Length > 0)
            FlushChunk(lines.Length);

        return chunks;
    }

    public static IReadOnlyList<KnowledgeChunkRecord> ExtractImageChunks(
        string filePath,
        string courseFolder,
        string courseAlias,
        string ext,
        IReadOnlyList<QuizQuestionRecord> linkedQuestions
    )
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return [];

        var fileName = Path.GetFileName(filePath);
        var baseName = Path.GetFileNameWithoutExtension(filePath)
            .Replace('_', ' ')
            .Replace('-', ' ');
        var parentDir = Path.GetFileName(Path.GetDirectoryName(filePath) ?? string.Empty)
            .Replace('_', ' ');
        var extLabel = string.IsNullOrWhiteSpace(ext) ? "IMAGE" : ext.ToUpperInvariant();

        var sb = new StringBuilder();
        sb.AppendLine($"Image: {fileName} ({baseName})");
        if (
            !string.IsNullOrWhiteSpace(parentDir)
            && !parentDir.Equals(courseFolder, StringComparison.OrdinalIgnoreCase)
        )
        {
            sb.AppendLine($"Section / Folder: {parentDir}");
        }

        var tagSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image", ext };
        string sectionTitle = !string.IsNullOrWhiteSpace(parentDir) ? parentDir : baseName;

        foreach (var q in linkedQuestions)
        {
            if (!string.IsNullOrWhiteSpace(q.TopicTag))
                tagSet.Add(q.TopicTag.TrimStart('@'));
            if (!string.IsNullOrWhiteSpace(q.SectionTitle))
                sectionTitle = q.SectionTitle;
            sb.AppendLine($"Linked Quiz Prompt: {q.Prompt}");
            if (!string.IsNullOrWhiteSpace(q.VerbatimQuote))
                sb.AppendLine($"Visual Region Description: {q.VerbatimQuote}");
            if (!string.IsNullOrWhiteSpace(q.Explanation))
                sb.AppendLine($"Concept Explanation: {q.Explanation}");
        }

        return
        [
            new KnowledgeChunkRecord(
                ChunkId: $"image:{filePath}",
                FilePath: filePath,
                CourseFolder: courseFolder,
                CourseAlias: courseAlias,
                SourceKind: "image",
                Title: fileName,
                SectionTitle: sectionTitle,
                LocationLabel: $"IMAGE ({extLabel})",
                StartLine: 0,
                EndLine: 0,
                PageNumber: 1,
                QuizId: string.Empty,
                QuestionId: string.Empty,
                Tags: string.Join(' ', tagSet),
                Content: sb.ToString().Trim()
            ),
        ];
    }

    public static IReadOnlyList<KnowledgeChunkRecord> ExtractQuizChunks(
        QuizSessionRecord quiz,
        string courseAlias
    )
    {
        var chunks = new List<KnowledgeChunkRecord>(quiz.Questions.Count);
        for (int i = 0; i < quiz.Questions.Count; i++)
        {
            var q = quiz.Questions[i];
            int displayNum = i + 1;
            var topic = (q.TopicTag ?? string.Empty).Trim().TrimStart('@');
            var section = !string.IsNullOrWhiteSpace(q.SectionTitle)
                ? q.SectionTitle
                : (!string.IsNullOrWhiteSpace(topic) ? $"@{topic}" : quiz.Title);

            var sb = new StringBuilder();
            sb.AppendLine(q.Prompt);
            if (q.Options.Count > 0)
                sb.AppendLine("Options: " + string.Join(" | ", q.Options));
            if (!string.IsNullOrWhiteSpace(q.CorrectAnswer))
                sb.AppendLine($"Correct Answer: {q.CorrectAnswer}");
            if (!string.IsNullOrWhiteSpace(q.Explanation))
                sb.AppendLine(q.Explanation);
            if (!string.IsNullOrWhiteSpace(q.Hint))
                sb.AppendLine($"Hint: {q.Hint}");
            if (!string.IsNullOrWhiteSpace(q.VerbatimQuote))
                sb.AppendLine($"Evidence: {q.VerbatimQuote}");
            if (!string.IsNullOrWhiteSpace(q.CodeSnippet))
                sb.AppendLine(q.CodeSnippet);

            var tagBuilder = new StringBuilder("quiz ");
            if (!string.IsNullOrWhiteSpace(topic))
                tagBuilder.Append(topic).Append(' ');
            tagBuilder.Append(q.Kind).Append(' ').Append(q.CognitiveLevel);
            if (!string.IsNullOrWhiteSpace(q.Archetype))
                tagBuilder.Append(' ').Append(q.Archetype);
            if (!string.IsNullOrWhiteSpace(quiz.Term))
                tagBuilder.Append(' ').Append(quiz.Term);

            var backingPath = !string.IsNullOrWhiteSpace(q.SourcePdfPath)
                ? q.SourcePdfPath
                : quiz.SourcePdfPath;
            var locLabel = !string.IsNullOrWhiteSpace(topic)
                ? $"QUIZ · #{displayNum} · @{topic}"
                : $"QUIZ · #{displayNum}";

            chunks.Add(
                new KnowledgeChunkRecord(
                    ChunkId: $"quiz:{quiz.Id}:{q.Id}",
                    FilePath: backingPath,
                    CourseFolder: quiz.CourseFolder,
                    CourseAlias: courseAlias,
                    SourceKind: "quiz",
                    Title: quiz.Title,
                    SectionTitle: section,
                    LocationLabel: locLabel,
                    StartLine: q.SourceStartLine ?? 0,
                    EndLine: q.SourceEndLine ?? 0,
                    PageNumber: Math.Max(1, q.SourcePage),
                    QuizId: quiz.Id,
                    QuestionId: q.Id,
                    Tags: tagBuilder.ToString().Trim(),
                    Content: sb.ToString().Trim()
                )
            );
        }

        return chunks;
    }

    private static Dictionary<string, List<QuizQuestionRecord>> BuildQuestionsByImageMap(
        IReadOnlyList<QuizSessionRecord> quizzes
    )
    {
        var map = new Dictionary<string, List<QuizQuestionRecord>>(
            StringComparer.OrdinalIgnoreCase
        );

        foreach (var qz in quizzes)
        {
            foreach (var q in qz.Questions)
            {
                var imgPath = !string.IsNullOrWhiteSpace(q.ImagePath)
                    ? q.ImagePath
                    : (
                        StudySourceClassifier.Classify(q.SourcePdfPath).Kind
                        == StudySourceKind.Image
                            ? q.SourcePdfPath
                            : null
                    );

                if (string.IsNullOrWhiteSpace(imgPath))
                    continue;

                if (!map.TryGetValue(imgPath, out var list))
                {
                    list = [];
                    map[imgPath] = list;
                }
                list.Add(q);
            }
        }

        return map;
    }

    private static (long FingerprintTicks, long VirtualSize) ComputeQuizFingerprint(
        QuizSessionRecord quiz
    )
    {
        unchecked
        {
            long hash = 17;
            hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(quiz.Title ?? string.Empty);
            hash = (hash * 31) + quiz.Questions.Count;
            long totalChars = 0;

            foreach (var q in quiz.Questions)
            {
                hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(q.Id ?? string.Empty);
                hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(q.Prompt ?? string.Empty);
                hash =
                    (hash * 31)
                    + StringComparer.Ordinal.GetHashCode(q.CorrectAnswer ?? string.Empty);
                hash =
                    (hash * 31) + StringComparer.Ordinal.GetHashCode(q.Explanation ?? string.Empty);
                hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(q.TopicTag ?? string.Empty);
                hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(q.Hint ?? string.Empty);
                totalChars +=
                    (q.Prompt?.Length ?? 0)
                    + (q.Explanation?.Length ?? 0)
                    + (q.CorrectAnswer?.Length ?? 0);
            }

            long ticks = quiz.CreatedAtUtc.UtcTicks ^ (hash & 0x7FFF_FFFF_FFFF_FFFFL);
            return (ticks, Math.Max(1, totalChars));
        }
    }

    private static bool TryStatFile(string path, out long mtimeTicks, out long sizeBytes)
    {
        mtimeTicks = 0;
        sizeBytes = 0;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists)
                return false;
            mtimeTicks = fi.LastWriteTimeUtc.Ticks;
            sizeBytes = fi.Length;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ToSourceKindToken(StudySourceKind kind) =>
        kind switch
        {
            StudySourceKind.Pdf => "pdf",
            StudySourceKind.Code => "code",
            StudySourceKind.Markdown => "md",
            StudySourceKind.PlainText => "text",
            StudySourceKind.Image => "image",
            _ => string.Empty,
        };

    private static string? TryMatchCodeSymbol(string trimmedLine)
    {
        if (
            string.IsNullOrEmpty(trimmedLine)
            || trimmedLine.StartsWith("//", StringComparison.Ordinal)
            || trimmedLine.StartsWith('#')
            || trimmedLine.StartsWith("/*", StringComparison.Ordinal)
            || trimmedLine.StartsWith('*')
        )
        {
            return null;
        }

        var m = CodeDeclarationRegex().Match(trimmedLine);
        if (!m.Success)
            return null;

        var sig = m.Groups["sig"].Value.Trim();
        // Exclude control flow keywords that look like function calls
        if (
            sig.StartsWith("if ", StringComparison.Ordinal)
            || sig.StartsWith("if(", StringComparison.Ordinal)
            || sig.StartsWith("for ", StringComparison.Ordinal)
            || sig.StartsWith("for(", StringComparison.Ordinal)
            || sig.StartsWith("foreach ", StringComparison.Ordinal)
            || sig.StartsWith("foreach(", StringComparison.Ordinal)
            || sig.StartsWith("while ", StringComparison.Ordinal)
            || sig.StartsWith("while(", StringComparison.Ordinal)
            || sig.StartsWith("switch ", StringComparison.Ordinal)
            || sig.StartsWith("switch(", StringComparison.Ordinal)
            || sig.StartsWith("catch ", StringComparison.Ordinal)
            || sig.StartsWith("catch(", StringComparison.Ordinal)
            || sig.StartsWith("return ", StringComparison.Ordinal)
        )
        {
            return null;
        }

        return sig.Length > 90 ? sig[..90].Trim() : sig;
    }

    [GeneratedRegex(
        @"^(?:export\s+)?(?:public|private|protected|internal|static|async|virtual|override|abstract|sealed|partial|inline|extern|const\s+)*\s*(?<sig>(?:class|struct|interface|enum|record|def|fn|func|function|CREATE\s+(?:OR\s+REPLACE\s+)?(?:TABLE|VIEW|FUNCTION|PROCEDURE))\s+[A-Za-z_][A-Za-z0-9_]*|[A-Za-z_][A-Za-z0-9_<>\[\]\*&\s\?]+\s+[A-Za-z_][A-Za-z0-9_]*\s*\([^;]*\))",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex CodeDeclarationRegex();
}
