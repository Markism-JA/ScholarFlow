using System.Text;
using ScholarFlow.Core.Quiz;

namespace ScholarFlow.Core.Knowledge;

public sealed record FileSourcePromptBundle(
    string Content,
    IReadOnlyList<string> IncludedTextFiles,
    IReadOnlyList<string> ImageFiles,
    IReadOnlyList<string> TruncatedFiles
);

/// <summary>
/// Course study-file discovery, numbered-line prompt formatting and quote→line-range grounding for
/// code / markdown / plain-text / image quiz sources. Shared by API AI and Manual Web UI modes.
/// </summary>
public static class FileSourceQuizSupport
{
    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", "__pycache__", "dist", "build", "target", "completed", "ongoing",
    };

    public static IReadOnlyList<StudySourceDescriptor> DiscoverCourseStudyFiles(
        string courseDir,
        int maxFiles = 300
    )
    {
        var result = new List<StudySourceDescriptor>();
        if (string.IsNullOrWhiteSpace(courseDir) || !Directory.Exists(courseDir))
            return result;

        void Walk(string dir, int depth)
        {
            if (result.Count >= maxFiles || depth > 4)
                return;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return;
            }

            foreach (var f in files)
            {
                if (result.Count >= maxFiles)
                    return;
                var name = Path.GetFileName(f);
                if (name.StartsWith('.'))
                    continue;
                var d = StudySourceClassifier.Describe(f);
                if (d.Kind is StudySourceKind.Code or StudySourceKind.Markdown or StudySourceKind.PlainText or StudySourceKind.Image)
                    result.Add(d);
            }

            IEnumerable<string> subs;
            try
            {
                subs = Directory.EnumerateDirectories(dir).OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return;
            }

            foreach (var sub in subs)
            {
                var n = Path.GetFileName(sub);
                if (n.StartsWith('.') || IgnoredDirs.Contains(n))
                    continue;
                Walk(sub, depth + 1);
            }
        }

        Walk(courseDir, 0);
        return result;
    }

    public static FileSourcePromptBundle BuildPromptBundle(
        IEnumerable<string> filePaths,
        int maxChars = 120_000,
        int maxLinesPerFile = 600
    )
    {
        var sb = new StringBuilder();
        var included = new List<string>();
        var images = new List<string>();
        var truncated = new List<string>();

        foreach (var path in filePaths)
        {
            var (kind, lang) = StudySourceClassifier.Classify(path);
            if (kind == StudySourceKind.Image)
            {
                if (File.Exists(path))
                    images.Add(path);
                continue;
            }

            if (kind is not (StudySourceKind.Code or StudySourceKind.Markdown or StudySourceKind.PlainText))
                continue;
            if (!File.Exists(path))
                continue;

            int totalLines;
            try
            {
                totalLines = File.ReadAllLines(path).Length;
            }
            catch
            {
                continue;
            }

            var numbered = SourceEvidenceGrounder.ExtractNumberedLines(path, 1, null, maxLinesPerFile);
            if (string.IsNullOrWhiteSpace(numbered))
                continue;

            var shown = Math.Min(totalLines, maxLinesPerFile);
            var header =
                $"=== [FILE: {Path.GetFileName(path)}] | Type: {kind} | Language: {lang} | Lines 1-{shown} of {totalLines} ===\n";
            if (sb.Length + header.Length + numbered.Length > maxChars)
            {
                truncated.Add(Path.GetFileName(path));
                continue;
            }

            sb.Append(header).Append(numbered).AppendLine();
            included.Add(path);
            if (totalLines > maxLinesPerFile)
                truncated.Add($"{Path.GetFileName(path)} (first {maxLinesPerFile} lines)");
        }

        return new FileSourcePromptBundle(sb.ToString(), included, images, truncated);
    }

    /// <summary>
    /// Resolves each question's evidence into an exact file + line range (text/code) or the source image.
    /// </summary>
    public static void GroundQuestionsToFiles(
        IReadOnlyList<string> sourceFilePaths,
        IReadOnlyList<(QuizQuestionRecord Question, EvidenceAnchorDto? Evidence)> items
    )
    {
        if (sourceFilePaths.Count == 0)
            return;

        foreach (var (q, ev) in items)
        {
            var file = PickFile(sourceFilePaths, ev?.File, q.VerbatimQuote);
            if (file is null)
                continue;

            q.SourcePdfPath = file;
            q.SourcePage = 1;
            var (kind, _) = StudySourceClassifier.Classify(file);

            if (kind == StudySourceKind.Image)
            {
                if (string.IsNullOrWhiteSpace(q.ImagePath))
                    q.ImagePath = file;
                var region = ev?.TryParseRegion();
                if (region is not null)
                    q.HighlightRects = [region];
                continue;
            }

            var match = SourceEvidenceGrounder.ResolveLineRange(file, q.VerbatimQuote, ev?.StartLine);
            if (match is not null)
            {
                q.SourceStartLine = match.StartLine;
                q.SourceEndLine = match.EndLine;
                if (!string.IsNullOrWhiteSpace(match.SectionTitle))
                    q.SectionTitle = match.SectionTitle;
            }
            else if (ev?.StartLine is > 0)
            {
                q.SourceStartLine = ev.StartLine;
                q.SourceEndLine = ev.EndLine is > 0 ? ev.EndLine : ev.StartLine;
            }
        }
    }

    /// <summary>Matches an AI-reported evidence filename against the selected non-PDF study files.</summary>
    public static string? TryMatchSourceFile(IReadOnlyList<string> sources, string? requested)
    {
        if (sources.Count == 0 || string.IsNullOrWhiteSpace(requested))
            return null;

        var req = requested.Trim().Replace('\\', '/');
        return sources.FirstOrDefault(s =>
            string.Equals(Path.GetFileName(s), Path.GetFileName(req), StringComparison.OrdinalIgnoreCase)
            || s.Replace('\\', '/').EndsWith(req, StringComparison.OrdinalIgnoreCase)
        );
    }

    private static string? PickFile(IReadOnlyList<string> sources, string? requested, string fallbackHaystack)
    {
        if (sources.Count == 1)
            return sources[0];

        var byName = TryMatchSourceFile(sources, requested);
        if (byName is not null)
            return byName;

        // Fall back to whichever text file actually contains the quote.
        foreach (var s in sources)
        {
            var (kind, _) = StudySourceClassifier.Classify(s);
            if (kind == StudySourceKind.Image || kind == StudySourceKind.Unknown)
                continue;
            if (SourceEvidenceGrounder.ResolveLineRange(s, fallbackHaystack) is not null)
                return s;
        }

        return sources[0];
    }
}
