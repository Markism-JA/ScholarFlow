using ScholarFlow.Core.Quiz;

namespace ScholarFlow.Core.Knowledge;

/// <summary>
/// Source category filter tags supported by Knowledge Search (@pdf, @code, @md, @text, @image, @quiz).
/// </summary>
public enum KnowledgeSourceFilterKind
{
    Pdf,
    Code,
    Markdown,
    PlainText,
    Image,
    Quiz,
}

/// <summary>
/// A single searchable knowledge unit extracted from a PDF page, Markdown heading section,
/// source-code symbol block, image descriptor, or quiz question card.
/// </summary>
public sealed record KnowledgeChunkRecord(
    string ChunkId,
    string FilePath,
    string CourseFolder,
    string CourseAlias,
    string SourceKind,
    string Title,
    string SectionTitle,
    string LocationLabel,
    int StartLine,
    int EndLine,
    int PageNumber,
    string QuizId,
    string QuestionId,
    string Tags,
    string Content
);

/// <summary>
/// Represents a file or virtual quiz deck tracked in knowledge_files for O(1) incremental diffing.
/// </summary>
public sealed record KnowledgeFileIndexEntry(
    string FileKey,
    string CourseFolder,
    string CourseAlias,
    string SourceKind,
    long MtimeTicks,
    long SizeBytes,
    IReadOnlyList<KnowledgeChunkRecord> Chunks
);

/// <summary>
/// Telemetry returned by an incremental or forced Knowledge Hub indexing pass.
/// </summary>
public sealed record KnowledgeIndexSummary(
    int FilesIndexed,
    int FilesSkipped,
    int FilesPruned,
    int ChunksWritten,
    int TotalIndexedFiles,
    int TotalIndexedChunks,
    double ElapsedMs
);

/// <summary>
/// Structured representation of a user's Knowledge Search query string.
/// </summary>
public sealed record ParsedKnowledgeQuery(
    string RawQuery,
    IReadOnlyList<string> CourseFilters,
    IReadOnlyList<KnowledgeSourceFilterKind> SourceFilters,
    IReadOnlyList<string> TopicFilters,
    IReadOnlyList<string> ExactPhrases,
    IReadOnlyList<string> FreeTextTokens,
    string FreeTextCombined,
    bool OnlyWeakSpots
)
{
    public bool HasAnyFilterOrQuery =>
        CourseFilters.Count > 0
        || SourceFilters.Count > 0
        || TopicFilters.Count > 0
        || ExactPhrases.Count > 0
        || FreeTextTokens.Count > 0
        || OnlyWeakSpots;

    public bool HasTextQuery => ExactPhrases.Count > 0 || FreeTextTokens.Count > 0;
}

/// <summary>
/// Precomputed in-memory weak-spot profile built from saved quiz SRS performance.
/// Allows O(1) microsecond weak-spot boosting during FTS5 result ranking without extra SQLite joins.
/// </summary>
public sealed class KnowledgeWeakSpotProfile
{
    public static KnowledgeWeakSpotProfile Empty { get; } = new();

    private readonly Dictionary<string, (double Score, string Reason)> _weakQuestionById = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly Dictionary<string, (double Score, string Topic, int Lapses)> _weakCourseTopic =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (double Score, string Topic, int Lapses)> _weakGlobalTopic =
        new(StringComparer.OrdinalIgnoreCase);

    public int WeakTopicCount => _weakGlobalTopic.Count;
    public int WeakQuestionCount => _weakQuestionById.Count;

    public static KnowledgeWeakSpotProfile FromQuizzes(IEnumerable<QuizSessionRecord>? quizzes)
    {
        var profile = new KnowledgeWeakSpotProfile();
        if (quizzes is null)
            return profile;

        var topicAgg =
            new Dictionary<
                (string Course, string Topic),
                (int Lapses, int WrongCount, double MinEase, string DisplayTopic)
            >();

        foreach (var quiz in quizzes)
        {
            var course = quiz.CourseFolder ?? string.Empty;
            foreach (var q in quiz.Questions)
            {
                bool lastWrong = q.LastAnsweredCorrect == false;
                bool hasLapses = q.SrsLapses > 0;
                bool lowEase = q.SrsReps > 0 && q.SrsEaseFactor < 2.15;

                if (!lastWrong && !hasLapses && !lowEase)
                    continue;

                double qWeakScore =
                    (lastWrong ? 16.0 : 0.0)
                    + Math.Min(12.0, q.SrsLapses * 5.0)
                    + (lowEase ? 6.0 : 0.0);

                var qReason =
                    lastWrong && q.SrsLapses > 0 ? $"Missed recently · {q.SrsLapses} lapse(s)"
                    : lastWrong ? "Missed on last attempt"
                    : $"{q.SrsLapses} SRS lapse(s)";

                profile._weakQuestionById[q.Id] = (Math.Min(30.0, qWeakScore), qReason);

                var topic = (q.TopicTag ?? string.Empty).Trim().TrimStart('@');
                if (topic.Length < 2)
                    continue;

                var key = (course.ToLowerInvariant(), topic.ToLowerInvariant());
                if (!topicAgg.TryGetValue(key, out var cur))
                    cur = (0, 0, 2.5, topic);

                topicAgg[key] = (
                    cur.Lapses + q.SrsLapses,
                    cur.WrongCount + (lastWrong ? 1 : 0),
                    Math.Min(cur.MinEase, q.SrsEaseFactor),
                    cur.DisplayTopic
                );
            }
        }

        foreach (var ((courseLower, topicLower), stats) in topicAgg)
        {
            double boost = Math.Min(
                24.0,
                8.0
                    + (stats.Lapses * 3.5)
                    + (stats.WrongCount * 5.0)
                    + (stats.MinEase < 2.1 ? 4.0 : 0.0)
            );
            var entry = (boost, stats.DisplayTopic, stats.Lapses + stats.WrongCount);
            profile._weakCourseTopic[$"{courseLower}::{topicLower}"] = entry;

            if (
                !profile._weakGlobalTopic.TryGetValue(topicLower, out var existing)
                || boost > existing.Score
            )
            {
                profile._weakGlobalTopic[topicLower] = entry;
            }
        }

        return profile;
    }

    public (double Boost, bool IsWeak, string? Reason) Evaluate(
        string courseFolder,
        string? questionId,
        string? tags,
        string? sectionTitle,
        string? content
    )
    {
        if (
            !string.IsNullOrWhiteSpace(questionId)
            && _weakQuestionById.TryGetValue(questionId, out var qHit)
        )
        {
            return (qHit.Score, true, qHit.Reason);
        }

        if (_weakGlobalTopic.Count == 0)
            return (0, false, null);

        var courseLower = (courseFolder ?? string.Empty).ToLowerInvariant();
        double bestBoost = 0;
        string? bestReason = null;

        foreach (var (topicLower, globalStats) in _weakGlobalTopic)
        {
            (double Score, string Topic, int Lapses) stats = globalStats;
            bool sameCourse = _weakCourseTopic.TryGetValue(
                $"{courseLower}::{topicLower}",
                out var courseStats
            );
            if (sameCourse)
                stats = courseStats;

            bool matchedTagOrSection =
                (!string.IsNullOrWhiteSpace(tags) && ContainsTopicToken(tags, topicLower))
                || (
                    !string.IsNullOrWhiteSpace(sectionTitle)
                    && ContainsTopicToken(sectionTitle, topicLower)
                );

            bool matchedContent =
                !matchedTagOrSection
                && sameCourse
                && topicLower.Length >= 4
                && !string.IsNullOrWhiteSpace(content)
                && ContainsTopicToken(content, topicLower);

            if (!matchedTagOrSection && !matchedContent)
                continue;

            double mult = sameCourse ? 1.0 : 0.65;
            if (matchedContent)
                mult *= 0.6;

            double candidate = stats.Score * mult;
            if (candidate > bestBoost)
            {
                bestBoost = candidate;
                bestReason = $"Weak topic @{stats.Topic}";
            }
        }

        return bestBoost >= 5.0 ? (bestBoost, true, bestReason) : (0, false, null);
    }

    private static bool ContainsTopicToken(string haystack, string topicLower)
    {
        var normalizedTopic = topicLower.Replace('-', ' ').Replace('_', ' ');
        int idx = haystack.IndexOf(topicLower, StringComparison.OrdinalIgnoreCase);
        if (idx < 0 && normalizedTopic != topicLower)
            idx = haystack.IndexOf(normalizedTopic, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return false;

        bool startOk = idx == 0 || !char.IsLetterOrDigit(haystack[idx - 1]);
        return startOk;
    }
}

/// <summary>
/// Ranked, snippet-highlighted search result item ready for virtualized UI rendering and 1-click actions.
/// </summary>
public sealed record KnowledgeSearchResult(
    string ChunkId,
    string FilePath,
    string CourseFolder,
    string CourseAlias,
    string SourceKind,
    string Title,
    string SectionTitle,
    string LocationLabel,
    int StartLine,
    int EndLine,
    int PageNumber,
    string QuizId,
    string QuestionId,
    string Tags,
    string HighlightedSnippet,
    string RawContentPreview,
    double Score,
    bool IsWeakSpot,
    string? WeakSpotReason
)
{
    /// <summary>
    /// Extracts clean plain text without the '«' and '»' highlight markers for evidence grounding / jumps.
    /// </summary>
    public string PlainSnippet =>
        HighlightedSnippet.Replace("«", string.Empty).Replace("»", string.Empty).Trim();
}

/// <summary>
/// Complete search response with timing telemetry and matched results.
/// </summary>
public sealed record KnowledgeSearchResponse(
    ParsedKnowledgeQuery Query,
    IReadOnlyList<KnowledgeSearchResult> Results,
    int TotalCandidates,
    double ElapsedMs
);
