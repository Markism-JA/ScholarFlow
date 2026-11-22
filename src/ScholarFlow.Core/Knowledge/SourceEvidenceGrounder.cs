using System.Text;
using System.Text.RegularExpressions;

namespace ScholarFlow.Core.Knowledge;

/// <summary>
/// Resolves verbatim quotes from Markdown, plain text, and source code files into exact 1-based line ranges
/// plus their nearest enclosing heading or symbol declaration, and extracts numbered line chunks for LLMs.
/// </summary>
public static partial class SourceEvidenceGrounder
{
    public static SourceLineMatch? ResolveLineRange(
        string filePath,
        string? verbatimQuote,
        int? hintStartLine = null
    )
    {
        if (
            string.IsNullOrWhiteSpace(filePath)
            || !File.Exists(filePath)
            || string.IsNullOrWhiteSpace(verbatimQuote)
        )
            return null;

        try
        {
            var lines = File.ReadAllLines(filePath);
            var (kind, _) = StudySourceClassifier.Classify(filePath);
            return ResolveLineRangeFromLines(lines, kind, verbatimQuote, hintStartLine);
        }
        catch
        {
            return null;
        }
    }

    public static SourceLineMatch? ResolveLineRangeFromLines(
        IReadOnlyList<string> lines,
        StudySourceKind kind,
        string? verbatimQuote,
        int? hintStartLine = null
    )
    {
        if (lines.Count == 0 || string.IsNullOrWhiteSpace(verbatimQuote))
            return null;

        var quoteLines = verbatimQuote
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeForMatch)
            .Where(l => !string.IsNullOrEmpty(l))
            .ToList();

        if (quoteLines.Count == 0)
            return null;

        var normFileLines = new string[lines.Count];
        for (int i = 0; i < lines.Count; i++)
            normFileLines[i] = NormalizeForMatch(lines[i]);

        // 1. Multi-line contiguous or whitespace-normalized match
        int bestStart = -1;
        int bestEnd = -1;
        int bestScore = 0;

        int maxStart = Math.Max(0, lines.Count - quoteLines.Count);
        for (int i = 0; i <= maxStart; i++)
        {
            int matchedLines = 0;
            for (int j = 0; j < quoteLines.Count && (i + j) < lines.Count; j++)
            {
                var fl = normFileLines[i + j];
                var ql = quoteLines[j];
                if (
                    fl.Equals(ql, StringComparison.OrdinalIgnoreCase)
                    || fl.Contains(ql, StringComparison.OrdinalIgnoreCase)
                    || (
                        ql.Length >= 12
                        && ql.Contains(fl, StringComparison.OrdinalIgnoreCase)
                        && fl.Length >= 8
                    )
                )
                {
                    matchedLines++;
                }
            }

            if (matchedLines == 0)
                continue;

            int score = matchedLines * 100;
            if (hintStartLine.HasValue)
            {
                int dist = Math.Abs(i + 1 - hintStartLine.Value);
                if (dist <= 10)
                    score += 15 - dist;
            }

            if (score > bestScore && matchedLines >= Math.Max(1, (quoteLines.Count + 1) / 2))
            {
                bestScore = score;
                bestStart = i + 1;
                bestEnd = Math.Min(lines.Count, i + quoteLines.Count);
            }
        }

        // 2. Fallback: token-overlap single/window match if quote was flattened onto one line
        if (bestStart < 0)
        {
            var quoteTokens = ExtractTokens(verbatimQuote);
            if (quoteTokens.Count >= 2)
            {
                int window = Math.Clamp(quoteLines.Count + 2, 1, 6);
                for (int i = 0; i < lines.Count; i++)
                {
                    var sb = new StringBuilder();
                    int endIdx = Math.Min(lines.Count - 1, i + window - 1);
                    for (int k = i; k <= endIdx; k++)
                    {
                        sb.Append(' ').Append(normFileLines[k]);
                    }

                    var windowText = sb.ToString();
                    int hits = quoteTokens.Count(t =>
                        windowText.Contains(t, StringComparison.OrdinalIgnoreCase)
                    );
                    double ratio = (double)hits / quoteTokens.Count;
                    if (ratio >= 0.65)
                    {
                        int score = (int)(ratio * 100);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestStart = i + 1;
                            bestEnd = endIdx + 1;
                        }
                    }
                }
            }
        }

        if (bestStart < 0)
            return null;

        var section = FindEnclosingSection(lines, kind, bestStart);
        var matchedText = string.Join(
            '\n',
            lines.Skip(bestStart - 1).Take(bestEnd - bestStart + 1)
        );
        return new SourceLineMatch(bestStart, bestEnd, section, matchedText);
    }

    public static string ExtractNumberedLines(
        string filePath,
        int? startLine = null,
        int? endLine = null,
        int maxLines = 400
    )
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return string.Empty;

        var lines = File.ReadAllLines(filePath);
        if (lines.Length == 0)
            return string.Empty;

        int s = Math.Clamp(startLine ?? 1, 1, lines.Length);
        int e = Math.Clamp(endLine ?? Math.Min(lines.Length, s + maxLines - 1), s, lines.Length);
        if (e - s + 1 > maxLines)
            e = s + maxLines - 1;

        var sb = new StringBuilder();
        for (int i = s; i <= e; i++)
            sb.Append($"L{i:000}: ").AppendLine(lines[i - 1]);
        return sb.ToString();
    }

    public static string FindEnclosingSection(
        IReadOnlyList<string> lines,
        StudySourceKind kind,
        int oneBasedLine
    )
    {
        int idx = Math.Clamp(oneBasedLine - 1, 0, lines.Count - 1);
        for (int i = idx; i >= 0; i--)
        {
            var trimmed = lines[i].Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            if (kind == StudySourceKind.Markdown && trimmed.StartsWith('#'))
            {
                return trimmed.TrimStart('#', ' ').Trim();
            }

            if (kind == StudySourceKind.Code)
            {
                var m = CodeSymbolRegex().Match(trimmed);
                if (m.Success)
                    return m.Groups["sig"].Value.Trim();
            }
        }

        return string.Empty;
    }

    private static string NormalizeForMatch(string line) =>
        WhitespaceRegex().Replace(line.Trim(), " ");

    private static List<string> ExtractTokens(string text) =>
        [
            .. TokenRegex()
                .Matches(text)
                .Select(m => m.Value)
                .Where(t => t.Length >= 3)
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*|\d+")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(
        @"^(?:export\s+)?(?:public|private|protected|internal|static|async|virtual|override|abstract|inline|extern|const\s+)*\s*(?<sig>(?:class|struct|interface|enum|record|def|fn|func|function|CREATE\s+(?:TABLE|VIEW|FUNCTION|PROCEDURE))\s+[A-Za-z_][A-Za-z0-9_]*|[A-Za-z_][A-Za-z0-9_<>\[\]\*&\s]+\s+[A-Za-z_][A-Za-z0-9_]*\s*\([^;]*\))",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex CodeSymbolRegex();
}
