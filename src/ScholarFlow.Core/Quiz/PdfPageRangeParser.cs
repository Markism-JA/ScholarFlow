using ScholarFlow.Core.Models;

namespace ScholarFlow.Core.Quiz;

/// <summary>
/// Parses and formats expressive printer-style PDF page range expressions:
/// - Single pages: "5"
/// - Contiguous ranges: "12-28" or "12–28"
/// - Open ranges: "10-" (10..total) or "-15" (1..15)
/// - Exclusions: "!20" or "!20-22" (e.g. "1-50, !3, !18-21" or standalone "!1-2")
/// </summary>
public static class PdfPageRangeParser
{
    public static bool TryParse(
        string? expression,
        int totalPages,
        out List<PdfPageRange> ranges,
        out string? error
    )
    {
        ranges = [];
        error = null;

        if (string.IsNullOrWhiteSpace(expression))
            return true;

        int maxPage = Math.Max(1, totalPages);
        var tokens = expression.Split(
            [',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

        var includeMask = new bool[maxPage + 1];
        var excludeMask = new bool[maxPage + 1];
        bool hasAnyInclude = false;
        bool hasAnyExclude = false;

        foreach (var rawToken in tokens)
        {
            var token = rawToken.Trim();
            if (token.Length == 0)
                continue;

            bool isExclude = token.StartsWith('!') || token.StartsWith('-');
            // Disambiguate "-15" (open range 1..15) vs "!15" (exclusion): only '!' is exclusion
            isExclude = token.StartsWith('!');
            var body = isExclude ? token[1..].Trim() : token;
            if (body.Length == 0)
            {
                error = $"Invalid empty page token '{rawToken}'.";
                return false;
            }

            // Normalize en-dash / em-dash to hyphen
            body = body.Replace('–', '-').Replace('—', '-');

            int start;
            int end;
            int dashIdx = body.IndexOf('-');
            if (dashIdx < 0)
            {
                if (!int.TryParse(body, out int single) || single <= 0)
                {
                    error = $"Invalid page number '{rawToken}'. Use format like 1-15, 22, !5.";
                    return false;
                }
                start = single;
                end = single;
            }
            else
            {
                var left = body[..dashIdx].Trim();
                var right = body[(dashIdx + 1)..].Trim();
                if (left.Length == 0 && right.Length == 0)
                {
                    error = $"Invalid range '{rawToken}'.";
                    return false;
                }

                if (left.Length == 0)
                {
                    start = 1;
                }
                else if (!int.TryParse(left, out start) || start <= 0)
                {
                    error = $"Invalid start page in '{rawToken}'.";
                    return false;
                }

                if (right.Length == 0)
                {
                    end = maxPage;
                }
                else if (!int.TryParse(right, out end) || end <= 0)
                {
                    error = $"Invalid end page in '{rawToken}'.";
                    return false;
                }

                if (end < start)
                    (start, end) = (end, start);
            }

            int clampedStart = Math.Clamp(start, 1, maxPage);
            int clampedEnd = Math.Clamp(end, clampedStart, maxPage);

            if (isExclude)
            {
                hasAnyExclude = true;
                for (int p = clampedStart; p <= clampedEnd; p++)
                    excludeMask[p] = true;
            }
            else
            {
                hasAnyInclude = true;
                for (int p = clampedStart; p <= clampedEnd; p++)
                    includeMask[p] = true;
            }
        }

        if (!hasAnyInclude && hasAnyExclude)
        {
            for (int p = 1; p <= maxPage; p++)
                includeMask[p] = true;
        }

        var selectedPages = new List<int>();
        for (int p = 1; p <= maxPage; p++)
        {
            if (includeMask[p] && !excludeMask[p])
                selectedPages.Add(p);
        }

        if (selectedPages.Count == 0)
        {
            error = "Page expression resolved to 0 pages.";
            return false;
        }

        ranges = CompressToRanges(selectedPages);
        return true;
    }

    public static List<PdfPageRange> CompressToRanges(IEnumerable<int> pages)
    {
        var sorted = pages.Where(p => p > 0).Distinct().OrderBy(p => p).ToList();
        if (sorted.Count == 0)
            return [];

        var result = new List<PdfPageRange>();
        int runStart = sorted[0];
        int runEnd = sorted[0];

        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] == runEnd + 1)
            {
                runEnd = sorted[i];
            }
            else
            {
                result.Add(new PdfPageRange(runStart, runEnd));
                runStart = sorted[i];
                runEnd = sorted[i];
            }
        }

        result.Add(new PdfPageRange(runStart, runEnd));
        return result;
    }

    public static string Format(IEnumerable<PdfPageRange> ranges)
    {
        var list = ranges
            .Where(r => r.StartPage > 0 && r.EndPage >= r.StartPage)
            .OrderBy(r => r.StartPage)
            .ToList();
        if (list.Count == 0)
            return string.Empty;

        return string.Join(
            ", ",
            list.Select(r =>
                r.StartPage == r.EndPage ? $"{r.StartPage}" : $"{r.StartPage}-{r.EndPage}"
            )
        );
    }

    public static string Format(IEnumerable<int> pages) => Format(CompressToRanges(pages));
}
