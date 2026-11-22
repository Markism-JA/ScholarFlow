using System.Diagnostics;
using System.Text.RegularExpressions;
using ScholarFlow.Core.Database;
using ScholarFlow.Core.Services;

namespace ScholarFlow.Core.Knowledge;

/// <summary>
/// Heuristic query parser (#course, @pdf/@code/@md/@text/@image/@quiz/@weak, @topic, "exact phrase")
/// and composite ranker (FTS5 BM25 + Title/Heading/Symbol Match + Exact Phrase Boost + Quiz Weak-Spot Boost)
/// with QuickSearchMatcher acronym/subsequence fallback.
/// </summary>
public static partial class KnowledgeSearchEngine
{
    public static ParsedKnowledgeQuery ParseQuery(
        string? rawQuery,
        string? explicitCourseFilter = null,
        KnowledgeSourceFilterKind? explicitSourceFilter = null,
        bool explicitWeakOnly = false
    )
    {
        var courses = new List<string>();
        var sources = new List<KnowledgeSourceFilterKind>();
        var topics = new List<string>();
        var phrases = new List<string>();
        var freeTokens = new List<string>();
        bool weakOnly = explicitWeakOnly;

        if (!string.IsNullOrWhiteSpace(explicitCourseFilter))
        {
            var c = explicitCourseFilter.Trim().TrimStart('#');
            if (!string.IsNullOrEmpty(c))
                courses.Add(c);
        }

        if (explicitSourceFilter.HasValue)
        {
            sources.Add(explicitSourceFilter.Value);
        }

        var working = rawQuery ?? string.Empty;

        // 1. Extract quoted "exact phrases"
        working = QuotedPhraseRegex()
            .Replace(
                working,
                m =>
                {
                    var phrase = m.Groups["phrase"].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(phrase))
                        phrases.Add(phrase);
                    return " ";
                }
            );

        // 2. Parse whitespace-delimited tokens
        var rawTokens = working.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

        foreach (var tok in rawTokens)
        {
            if (tok.StartsWith('#') && tok.Length > 1)
            {
                var course = tok[1..].Trim();
                if (
                    !string.IsNullOrEmpty(course)
                    && !courses.Contains(course, StringComparer.OrdinalIgnoreCase)
                )
                {
                    courses.Add(course);
                }
                continue;
            }

            if (tok.StartsWith('@') && tok.Length > 1)
            {
                var tag = tok[1..].Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(tag))
                    continue;

                if (tag is "weak" or "weakspot" or "weakspots" or "lapse" or "lapses")
                {
                    weakOnly = true;
                    continue;
                }

                if (TryParseSourceKindToken(tag, out var sk))
                {
                    if (!sources.Contains(sk))
                        sources.Add(sk);
                    continue;
                }

                if (!topics.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    topics.Add(tag);
                continue;
            }

            freeTokens.Add(tok);
        }

        var freeCombined = string.Join(' ', freeTokens);
        return new ParsedKnowledgeQuery(
            RawQuery: rawQuery ?? string.Empty,
            CourseFilters: courses,
            SourceFilters: sources,
            TopicFilters: topics,
            ExactPhrases: phrases,
            FreeTextTokens: freeTokens,
            FreeTextCombined: freeCombined,
            OnlyWeakSpots: weakOnly
        );
    }

    public static string? BuildFts5MatchExpression(
        ParsedKnowledgeQuery parsed,
        bool useOrBetweenTokens = false
    )
    {
        var clauses = new List<string>();

        foreach (var phrase in parsed.ExactPhrases)
        {
            var words = ExtractSafeFtsWords(phrase);
            if (words.Count > 0)
            {
                clauses.Add($"\"{string.Join(' ', words)}\"");
            }
        }

        var tokenClauses = new List<string>();
        foreach (var tok in parsed.FreeTextTokens)
        {
            var words = ExtractSafeFtsWords(tok);
            foreach (var w in words)
            {
                if (w.Length >= 2)
                    tokenClauses.Add($"(\"{w}\" OR {w}*)");
                else if (w.Length == 1)
                    tokenClauses.Add($"{w}*");
            }
        }

        // If no free-text or phrase query was typed, but @topic tags were provided, use @topic in FTS5
        if (clauses.Count == 0 && tokenClauses.Count == 0 && parsed.TopicFilters.Count > 0)
        {
            foreach (var topic in parsed.TopicFilters)
            {
                var words = ExtractSafeFtsWords(topic);
                foreach (var w in words)
                {
                    if (w.Length >= 2)
                        tokenClauses.Add($"(\"{w}\" OR {w}*)");
                }
            }
        }

        if (tokenClauses.Count == 1)
        {
            clauses.Add(tokenClauses[0]);
        }
        else if (tokenClauses.Count > 1)
        {
            var joinOp = useOrBetweenTokens ? " OR " : " AND ";
            clauses.Add("(" + string.Join(joinOp, tokenClauses) + ")");
        }

        if (clauses.Count == 0)
            return null;

        return string.Join(" AND ", clauses);
    }

    public static async Task<KnowledgeSearchResponse> SearchAsync(
        string basinRoot,
        string? rawQuery,
        KnowledgeWeakSpotProfile? weakProfile = null,
        string? explicitCourseFilter = null,
        KnowledgeSourceFilterKind? explicitSourceFilter = null,
        bool explicitWeakOnly = false,
        int maxResults = 80,
        CancellationToken ct = default
    )
    {
        var sw = Stopwatch.StartNew();
        var parsed = ParseQuery(
            rawQuery,
            explicitCourseFilter,
            explicitSourceFilter,
            explicitWeakOnly
        );

        if (string.IsNullOrWhiteSpace(basinRoot))
        {
            return new KnowledgeSearchResponse(parsed, [], 0, 0);
        }

        var profile = weakProfile ?? KnowledgeWeakSpotProfile.Empty;
        var sourceKindStrings = parsed
            .SourceFilters.Select(ToDatabaseSourceKind)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var ftsMatch = BuildFts5MatchExpression(parsed, useOrBetweenTokens: false);
        int fetchLimit = Math.Max(160, maxResults * 2);

        IReadOnlyList<ScholarFlowDatabase.RawKnowledgeCandidate> rawCandidates;
        try
        {
            rawCandidates = await ScholarFlowDatabase.QueryKnowledgeChunksRawAsync(
                basinRoot,
                ftsMatch,
                parsed.CourseFilters,
                sourceKindStrings,
                fetchLimit,
                ct
            );
        }
        catch
        {
            // Fallback to non-MATCH query if an unusual token sequence couldn't be parsed by FTS5
            rawCandidates = await ScholarFlowDatabase.QueryKnowledgeChunksRawAsync(
                basinRoot,
                null,
                parsed.CourseFilters,
                sourceKindStrings,
                fetchLimit,
                ct
            );
        }

        var candidateMap = new Dictionary<string, ScholarFlowDatabase.RawKnowledgeCandidate>(
            StringComparer.Ordinal
        );
        foreach (var c in rawCandidates)
            candidateMap[c.ChunkId] = c;

        // Multi-token OR relaxation fallback when strict AND matches fewer than 4 chunks
        if (candidateMap.Count < 4 && parsed.FreeTextTokens.Count >= 2)
        {
            var orMatch = BuildFts5MatchExpression(parsed, useOrBetweenTokens: true);
            if (!string.IsNullOrWhiteSpace(orMatch) && orMatch != ftsMatch)
            {
                try
                {
                    var orCandidates = await ScholarFlowDatabase.QueryKnowledgeChunksRawAsync(
                        basinRoot,
                        orMatch,
                        parsed.CourseFilters,
                        sourceKindStrings,
                        fetchLimit,
                        ct
                    );
                    foreach (var c in orCandidates)
                        candidateMap.TryAdd(c.ChunkId, c);
                }
                catch
                {
                    // Ignore secondary query error
                }
            }
        }

        // Acronym / Subsequence fuzzy fallback via QuickSearchMatcher when FTS5 returns < 3 hits
        if (candidateMap.Count < 3 && parsed.HasTextQuery)
        {
            var scanCandidates = await ScholarFlowDatabase.QueryKnowledgeChunksRawAsync(
                basinRoot,
                null,
                parsed.CourseFilters,
                sourceKindStrings,
                limit: 260,
                ct
            );
            var lookupText = !string.IsNullOrWhiteSpace(parsed.FreeTextCombined)
                ? parsed.FreeTextCombined
                : string.Join(' ', parsed.ExactPhrases);

            foreach (var c in scanCandidates)
            {
                if (candidateMap.ContainsKey(c.ChunkId))
                    continue;
                if (
                    QuickSearchMatcher.Score(
                        lookupText,
                        c.Title,
                        c.SectionTitle,
                        c.Tags,
                        c.CourseAlias,
                        c.Content
                    ) > 0
                )
                {
                    candidateMap[c.ChunkId] = c;
                }
            }
        }

        var highlightTerms = BuildHighlightTerms(parsed);
        var scored = new List<KnowledgeSearchResult>(candidateMap.Count);

        foreach (var cand in candidateMap.Values)
        {
            ct.ThrowIfCancellationRequested();

            if (
                !TryScoreCandidate(
                    cand,
                    parsed,
                    profile,
                    out double score,
                    out bool isWeak,
                    out string? weakReason
                )
            )
                continue;

            var snippet = BuildHighlightedSnippet(cand, highlightTerms);
            var preview = cand.Content.Length > 280 ? cand.Content[..280].Trim() : cand.Content;

            scored.Add(
                new KnowledgeSearchResult(
                    ChunkId: cand.ChunkId,
                    FilePath: cand.FilePath,
                    CourseFolder: cand.CourseFolder,
                    CourseAlias: cand.CourseAlias,
                    SourceKind: cand.SourceKind,
                    Title: cand.Title,
                    SectionTitle: cand.SectionTitle,
                    LocationLabel: cand.LocationLabel,
                    StartLine: cand.StartLine,
                    EndLine: cand.EndLine,
                    PageNumber: cand.PageNumber,
                    QuizId: cand.QuizId,
                    QuestionId: cand.QuestionId,
                    Tags: cand.Tags,
                    HighlightedSnippet: snippet,
                    RawContentPreview: preview,
                    Score: score,
                    IsWeakSpot: isWeak,
                    WeakSpotReason: weakReason
                )
            );
        }

        var ordered = scored
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.IsWeakSpot)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.PageNumber)
            .ThenBy(r => r.StartLine)
            .Take(maxResults)
            .ToList();

        sw.Stop();
        return new KnowledgeSearchResponse(
            Query: parsed,
            Results: ordered,
            TotalCandidates: scored.Count,
            ElapsedMs: sw.Elapsed.TotalMilliseconds
        );
    }

    private static bool TryScoreCandidate(
        ScholarFlowDatabase.RawKnowledgeCandidate cand,
        ParsedKnowledgeQuery parsed,
        KnowledgeWeakSpotProfile weakProfile,
        out double totalScore,
        out bool isWeakSpot,
        out string? weakReason
    )
    {
        totalScore = 0;
        isWeakSpot = false;
        weakReason = null;

        // 1. Verify @topic filters if specified
        double topicScore = 0;
        foreach (var topic in parsed.TopicFilters)
        {
            var normTopic = topic.Replace('-', ' ').Replace('_', ' ');
            bool inTags =
                cand.Tags.Contains(topic, StringComparison.OrdinalIgnoreCase)
                || cand.Tags.Contains(normTopic, StringComparison.OrdinalIgnoreCase);
            bool inSection =
                cand.SectionTitle.Contains(topic, StringComparison.OrdinalIgnoreCase)
                || cand.SectionTitle.Contains(normTopic, StringComparison.OrdinalIgnoreCase);
            bool inTitle =
                cand.Title.Contains(topic, StringComparison.OrdinalIgnoreCase)
                || cand.Title.Contains(normTopic, StringComparison.OrdinalIgnoreCase);
            bool inContent =
                cand.Content.Contains(topic, StringComparison.OrdinalIgnoreCase)
                || cand.Content.Contains(normTopic, StringComparison.OrdinalIgnoreCase);

            if (!inTags && !inSection && !inTitle && !inContent)
                return false;

            topicScore += inTags || inSection ? 26.0 : (inTitle ? 20.0 : 10.0);
        }

        // 2. Verify "exact phrases" if specified
        double exactPhraseScore = 0;
        foreach (var phrase in parsed.ExactPhrases)
        {
            bool inSecOrTitle =
                cand.SectionTitle.Contains(phrase, StringComparison.OrdinalIgnoreCase)
                || cand.Title.Contains(phrase, StringComparison.OrdinalIgnoreCase);
            bool inContent = cand.Content.Contains(phrase, StringComparison.OrdinalIgnoreCase);

            if (!inSecOrTitle && !inContent)
            {
                // Still allow if FTS5 stemmed phrase matched, but only with lower bonus
                int ftsPhraseScore = QuickSearchMatcher.Score(
                    phrase,
                    cand.SectionTitle,
                    cand.Title,
                    cand.Content
                );
                if (ftsPhraseScore == 0)
                    return false;
                exactPhraseScore += 8.0;
            }
            else
            {
                exactPhraseScore += inSecOrTitle ? 38.0 : 26.0;
            }
        }

        // 3. Evaluate Weak-Spot Boost
        var (weakBoost, weakHit, reason) = weakProfile.Evaluate(
            cand.CourseFolder,
            cand.QuestionId,
            cand.Tags,
            cand.SectionTitle,
            cand.Content
        );
        isWeakSpot = weakHit;
        weakReason = reason;

        if (parsed.OnlyWeakSpots && !isWeakSpot)
            return false;

        // 4. FTS5 BM25 base score (raw_bm25 is negative; more negative = higher relevance)
        double bm25Norm = cand.RawBm25 < 0 ? Math.Clamp(-cand.RawBm25 * 4.5, 1.0, 55.0) : 5.0;

        // 5. Title / Heading / Code Symbol & Exact Substring Boost
        double titleSymbolBoost = 0;
        if (!string.IsNullOrWhiteSpace(parsed.FreeTextCombined))
        {
            int titleMatch = QuickSearchMatcher.Score(
                parsed.FreeTextCombined,
                cand.SectionTitle,
                cand.Title,
                cand.Tags
            );
            titleSymbolBoost += Math.Min(36.0, titleMatch * 0.35);

            if (parsed.FreeTextCombined.Length >= 3)
            {
                if (
                    cand.SectionTitle.Contains(
                        parsed.FreeTextCombined,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                    exactPhraseScore += 24.0;
                else if (
                    cand.Title.Contains(parsed.FreeTextCombined, StringComparison.OrdinalIgnoreCase)
                )
                    exactPhraseScore += 18.0;
                else if (
                    cand.Content.Contains(
                        parsed.FreeTextCombined,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                    exactPhraseScore += 14.0;
            }

            foreach (var tok in parsed.FreeTextTokens)
            {
                if (tok.Length < 2)
                    continue;
                if (cand.SectionTitle.Equals(tok, StringComparison.OrdinalIgnoreCase))
                    titleSymbolBoost += 20.0;
                else if (cand.SectionTitle.Contains(tok, StringComparison.OrdinalIgnoreCase))
                    titleSymbolBoost += 10.0;

                if (cand.Tags.Contains(tok, StringComparison.OrdinalIgnoreCase))
                    titleSymbolBoost += 8.0;
            }
        }

        totalScore = bm25Norm + titleSymbolBoost + exactPhraseScore + topicScore + weakBoost;
        return true;
    }

    public static string BuildHighlightedSnippet(
        ScholarFlowDatabase.RawKnowledgeCandidate cand,
        IReadOnlyList<string> highlightTerms
    )
    {
        bool isCode = cand.SourceKind.Equals("code", StringComparison.OrdinalIgnoreCase);

        // If FTS5 snippet already contains highlight markers «...», clean up whitespace & return
        if (
            !isCode
            && !string.IsNullOrWhiteSpace(cand.FtsSnippet)
            && cand.FtsSnippet.Contains('«')
            && cand.FtsSnippet.Contains('»')
        )
        {
            return CollapseSnippetWhitespace(cand.FtsSnippet, preserveLineBreaks: false);
        }

        var content = cand.Content ?? string.Empty;
        if (string.IsNullOrWhiteSpace(content))
            return cand.SectionTitle;

        string window;
        if (isCode)
        {
            window = ExtractCodeWindow(content, highlightTerms, maxLines: 4);
        }
        else
        {
            window = ExtractProseWindow(content, highlightTerms, maxChars: 230);
        }

        return ApplyHighlightMarkers(window, highlightTerms);
    }

    private static string ExtractCodeWindow(
        string content,
        IReadOnlyList<string> terms,
        int maxLines
    )
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        int bestLine = 0;
        int bestHits = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            int hits = 0;
            foreach (var t in terms)
            {
                if (lines[i].Contains(t, StringComparison.OrdinalIgnoreCase))
                    hits += t.Length;
            }
            if (hits > bestHits)
            {
                bestHits = hits;
                bestLine = i;
            }
        }

        int start = Math.Clamp(bestLine - 1, 0, Math.Max(0, lines.Length - maxLines));
        int count = Math.Min(maxLines, lines.Length - start);
        var slice = lines.Skip(start).Take(count).Select(l => l.TrimEnd());
        return string.Join('\n', slice).Trim();
    }

    private static string ExtractProseWindow(
        string content,
        IReadOnlyList<string> terms,
        int maxChars
    )
    {
        var flat = CollapseSnippetWhitespace(content, preserveLineBreaks: false);
        if (flat.Length <= maxChars)
            return flat;

        int firstHit = -1;
        foreach (var t in terms)
        {
            int idx = flat.IndexOf(t, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && (firstHit < 0 || idx < firstHit))
                firstHit = idx;
        }

        if (firstHit < 0)
            return flat[..maxChars].Trim() + " …";

        int start = Math.Max(0, firstHit - 55);
        if (start > 0)
        {
            int space = flat.IndexOf(' ', start);
            if (space > start && space - start < 18)
                start = space + 1;
        }

        int len = Math.Min(maxChars, flat.Length - start);
        var slice = flat.Substring(start, len).Trim();
        return (start > 0 ? "… " : string.Empty)
            + slice
            + (start + len < flat.Length ? " …" : string.Empty);
    }

    private static string ApplyHighlightMarkers(string text, IReadOnlyList<string> terms)
    {
        if (string.IsNullOrWhiteSpace(text) || terms.Count == 0)
            return text;

        var clean = text.Replace("«", string.Empty).Replace("»", string.Empty);
        var pattern = string.Join(
            '|',
            terms.Where(t => t.Length >= 2).OrderByDescending(t => t.Length).Select(Regex.Escape)
        );
        if (string.IsNullOrEmpty(pattern))
            return clean;

        return Regex.Replace(
            clean,
            pattern,
            m => $"«{m.Value}»",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
        );
    }

    private static List<string> BuildHighlightTerms(ParsedKnowledgeQuery parsed)
    {
        var list = new List<string>();
        foreach (var p in parsed.ExactPhrases)
        {
            if (p.Length >= 2 && !list.Contains(p, StringComparer.OrdinalIgnoreCase))
                list.Add(p);
        }
        foreach (var t in parsed.FreeTextTokens)
        {
            var clean = t.Trim('"', '\'', '(', ')', '[', ']', ',', ';', ':');
            if (clean.Length >= 2 && !list.Contains(clean, StringComparer.OrdinalIgnoreCase))
                list.Add(clean);
        }
        foreach (var top in parsed.TopicFilters)
        {
            if (top.Length >= 2 && !list.Contains(top, StringComparer.OrdinalIgnoreCase))
                list.Add(top);
        }
        return list;
    }

    private static string CollapseSnippetWhitespace(string input, bool preserveLineBreaks)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        if (!preserveLineBreaks)
            return MultiWhitespaceRegex().Replace(input, " ").Trim();

        var lines = input
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('\n', lines);
    }

    private static List<string> ExtractSafeFtsWords(string text)
    {
        return
        [
            .. FtsWordRegex()
                .Matches(text)
                .Select(m => m.Value)
                .Where(w => !string.IsNullOrWhiteSpace(w)),
        ];
    }

    public static bool TryParseSourceKindToken(string tag, out KnowledgeSourceFilterKind kind)
    {
        switch (tag.Trim().TrimStart('@').ToLowerInvariant())
        {
            case "pdf":
            case "pdfs":
                kind = KnowledgeSourceFilterKind.Pdf;
                return true;
            case "code":
            case "src":
                kind = KnowledgeSourceFilterKind.Code;
                return true;
            case "md":
            case "markdown":
            case "note":
            case "notes":
                kind = KnowledgeSourceFilterKind.Markdown;
                return true;
            case "text":
            case "txt":
                kind = KnowledgeSourceFilterKind.PlainText;
                return true;
            case "image":
            case "images":
            case "img":
                kind = KnowledgeSourceFilterKind.Image;
                return true;
            case "quiz":
            case "quizzes":
            case "card":
            case "cards":
                kind = KnowledgeSourceFilterKind.Quiz;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    public static string ToDatabaseSourceKind(KnowledgeSourceFilterKind kind) =>
        kind switch
        {
            KnowledgeSourceFilterKind.Pdf => "pdf",
            KnowledgeSourceFilterKind.Code => "code",
            KnowledgeSourceFilterKind.Markdown => "md",
            KnowledgeSourceFilterKind.PlainText => "text",
            KnowledgeSourceFilterKind.Image => "image",
            KnowledgeSourceFilterKind.Quiz => "quiz",
            _ => "pdf",
        };

    [GeneratedRegex("\"(?<phrase>[^\"]+)\"")]
    private static partial Regex QuotedPhraseRegex();

    [GeneratedRegex(@"[A-Za-z0-9_]+")]
    private static partial Regex FtsWordRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiWhitespaceRegex();
}
