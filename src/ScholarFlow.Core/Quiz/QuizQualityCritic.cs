using System.Text.RegularExpressions;
using ScholarFlow.Core.Knowledge;

namespace ScholarFlow.Core.Quiz;

public enum QualityIssueSeverity
{
    Warning,
    Error,
}

public sealed record QuestionQualityIssue(
    string QuestionId,
    string Code,
    string Message,
    QualityIssueSeverity Severity
);

public sealed record QuizQualityReport(IReadOnlyList<QuestionQualityIssue> Issues)
{
    public bool HasIssues => Issues.Count > 0;
    public int ErrorCount => Issues.Count(i => i.Severity == QualityIssueSeverity.Error);
    public int WarningCount => Issues.Count(i => i.Severity == QualityIssueSeverity.Warning);

    public HashSet<string> FlaggedQuestionIds =>
        Issues.Select(i => i.QuestionId).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<QuestionQualityIssue> GetIssuesFor(string questionId) =>
        Issues
            .Where(i => string.Equals(i.QuestionId, questionId, StringComparison.OrdinalIgnoreCase))
            .ToList();

    public string SummaryText =>
        Issues.Count == 0
            ? "All checks passed"
            : $"{FlaggedQuestionIds.Count} item(s) flagged ({ErrorCount} error{(ErrorCount == 1 ? "" : "s")}, {WarningCount} warning{(WarningCount == 1 ? "" : "s")})";
}

/// <summary>
/// Deterministic, zero-token local post-generation quality critic.
/// Runs automatically after BOTH AI API generation and Manual Web UI JSON import,
/// flagging weak distractors, length giveaways, answer leakage, missing blanks, duplicates, and ungrounded evidence.
/// </summary>
public static class QuizQualityCritic
{
    private static readonly Regex LazyDistractorRegex = new(
        @"\b(all\s+of\s+the\s+above|none\s+of\s+the\s+above|both\s+[a-d]\s+and\s+[a-d]|all\s+of\s+these)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex BlankMarkerRegex = new(@"_{3,}", RegexOptions.Compiled);

    private static readonly HashSet<string> StemStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the",
        "a",
        "an",
        "is",
        "are",
        "was",
        "were",
        "of",
        "in",
        "on",
        "to",
        "for",
        "with",
        "and",
        "or",
        "by",
        "that",
        "this",
        "which",
        "what",
        "how",
        "why",
        "when",
        "where",
        "from",
        "be",
        "it",
    };

    public static QuizQualityReport Audit(QuizSessionRecord? session)
    {
        if (session is null || session.Questions.Count == 0)
            return new QuizQualityReport([]);

        return Audit(session.Questions);
    }

    public static QuizQualityReport Audit(IReadOnlyList<QuizQuestionRecord> questions)
    {
        var issues = new List<QuestionQualityIssue>();

        for (int i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            AuditSingleQuestion(q, issues);
        }

        // Cross-question duplicate / near-duplicate stem detection
        var tokenSets = questions.Select(q => TokenizeStem(q.Prompt)).ToList();

        var alreadyMarkedDup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < questions.Count; i++)
        {
            if (tokenSets[i].Count < 4)
                continue;

            for (int j = i + 1; j < questions.Count; j++)
            {
                if (tokenSets[j].Count < 4)
                    continue;

                double sim = ComputeJaccard(tokenSets[i], tokenSets[j]);
                if (sim >= 0.80 && alreadyMarkedDup.Add(questions[j].Id))
                {
                    issues.Add(
                        new QuestionQualityIssue(
                            questions[j].Id,
                            "DUPLICATE_STEM",
                            $"Near-duplicate stem with Q{i + 1} ({sim * 100:0}% word overlap).",
                            QualityIssueSeverity.Warning
                        )
                    );
                }
            }
        }

        return new QuizQualityReport(issues);
    }

    private static void AuditSingleQuestion(QuizQuestionRecord q, List<QuestionQualityIssue> issues)
    {
        // 1. Options & CorrectAnswer integrity
        if (q.Kind is QuizQuestionKind.MultipleChoice or QuizQuestionKind.Situational)
        {
            if (q.Options.Count < 3)
            {
                issues.Add(
                    new QuestionQualityIssue(
                        q.Id,
                        "TOO_FEW_OPTIONS",
                        $"Only {q.Options.Count} option(s); expected 4 options.",
                        QualityIssueSeverity.Error
                    )
                );
            }

            if (q.Options.Count > 0 && !q.Options.Any(o => q.EvaluateAnswer(o)))
            {
                issues.Add(
                    new QuestionQualityIssue(
                        q.Id,
                        "INVALID_CORRECT_ANSWER",
                        "CorrectAnswer does not match any of the listed options.",
                        QualityIssueSeverity.Error
                    )
                );
            }

            // Duplicate options check
            var distinctOpts = q
                .Options.Select(o => o.Trim().ToLowerInvariant())
                .Distinct()
                .Count();
            if (distinctOpts < q.Options.Count)
            {
                issues.Add(
                    new QuestionQualityIssue(
                        q.Id,
                        "DUPLICATE_OPTIONS",
                        "Contains duplicate answer options.",
                        QualityIssueSeverity.Error
                    )
                );
            }

            // Lazy distractor check ("All of the above", "None of the above")
            foreach (var opt in q.Options)
            {
                if (LazyDistractorRegex.IsMatch(opt))
                {
                    issues.Add(
                        new QuestionQualityIssue(
                            q.Id,
                            "LAZY_DISTRACTOR",
                            $"Avoid lazy option \"{opt.Trim()}\"; replace with a specific misconception distractor.",
                            QualityIssueSeverity.Warning
                        )
                    );
                    break;
                }
            }

            // Option length skew ("longest option is always right" giveaway)
            if (q.Options.Count >= 4)
            {
                var correctOpt = q.Options.FirstOrDefault(o => q.EvaluateAnswer(o));
                var distractors = q
                    .Options.Where(o => !string.Equals(o, correctOpt, StringComparison.Ordinal))
                    .Select(o => o.Trim().Length)
                    .OrderBy(len => len)
                    .ToList();

                if (correctOpt is not null && distractors.Count >= 3)
                {
                    int correctLen = correctOpt.Trim().Length;
                    double avgDistractor = distractors.Average();
                    int medianDistractor = distractors[distractors.Count / 2];

                    if (
                        correctLen >= 45
                        && correctLen > Math.Max(10.0, avgDistractor * 1.85)
                        && correctLen - medianDistractor >= 40
                    )
                    {
                        issues.Add(
                            new QuestionQualityIssue(
                                q.Id,
                                "LENGTH_SKEW_GIVEAWAY",
                                $"Correct option ({correctLen} chars) is much longer than distractors (avg {avgDistractor:0} chars) — length giveaway.",
                                QualityIssueSeverity.Warning
                            )
                        );
                    }
                }
            }
        }
        else if (q.Kind == QuizQuestionKind.Ordering)
        {
            if (q.Options.Count < 3)
            {
                issues.Add(
                    new QuestionQualityIssue(
                        q.Id,
                        "TOO_FEW_STEPS",
                        $"Ordering question has only {q.Options.Count} step(s); expected 3–5 steps.",
                        QualityIssueSeverity.Error
                    )
                );
            }
        }
        else if (q.Kind == QuizQuestionKind.FillInTheBlank)
        {
            bool hasBlank =
                BlankMarkerRegex.IsMatch(q.Prompt ?? string.Empty)
                || BlankMarkerRegex.IsMatch(q.CodeSnippet ?? string.Empty);
            if (!hasBlank)
            {
                issues.Add(
                    new QuestionQualityIssue(
                        q.Id,
                        "MISSING_BLANK",
                        "FillInTheBlank question is missing a \"_____\" blank in the prompt or code snippet.",
                        QualityIssueSeverity.Error
                    )
                );
            }
        }

        // 2. Empty CorrectAnswer
        if (string.IsNullOrWhiteSpace(q.CorrectAnswer))
        {
            issues.Add(
                new QuestionQualityIssue(
                    q.Id,
                    "EMPTY_ANSWER",
                    "CorrectAnswer is empty.",
                    QualityIssueSeverity.Error
                )
            );
        }

        // 3. Answer leakage in Hint (or in Prompt for FillInTheBlank)
        var cleanAns = (q.CorrectAnswer ?? string.Empty).Trim();
        if (cleanAns.Length >= 4)
        {
            if (!string.IsNullOrWhiteSpace(q.Hint) && ContainsWholeWord(q.Hint, cleanAns))
            {
                issues.Add(
                    new QuestionQualityIssue(
                        q.Id,
                        "HINT_LEAKS_ANSWER",
                        "Hint contains the exact answer verbatim.",
                        QualityIssueSeverity.Warning
                    )
                );
            }

            if (q.Kind == QuizQuestionKind.FillInTheBlank && ContainsWholeWord(q.Prompt, cleanAns))
            {
                issues.Add(
                    new QuestionQualityIssue(
                        q.Id,
                        "PROMPT_LEAKS_ANSWER",
                        "FillInTheBlank prompt leaks the missing answer.",
                        QualityIssueSeverity.Warning
                    )
                );
            }
        }

        // 4. Missing or ultra-short explanation
        if (string.IsNullOrWhiteSpace(q.Explanation) || q.Explanation.Trim().Length < 15)
        {
            issues.Add(
                new QuestionQualityIssue(
                    q.Id,
                    "WEAK_EXPLANATION",
                    "Explanation is missing or too brief to support learning.",
                    QualityIssueSeverity.Warning
                )
            );
        }

        // 5. Evidence grounding health
        if (string.IsNullOrWhiteSpace(q.VerbatimQuote))
        {
            issues.Add(
                new QuestionQualityIssue(
                    q.Id,
                    "UNGROUNDED_EVIDENCE",
                    "Missing verbatim evidence quote or visual pointer.",
                    QualityIssueSeverity.Warning
                )
            );
        }
        else if (q.IsPdfSource)
        {
            if (q.HighlightRects.Count == 0)
            {
                issues.Add(
                    new QuestionQualityIssue(
                        q.Id,
                        "UNGROUNDED_EVIDENCE",
                        $"Evidence quote could not be matched to a highlight box on PDF page {q.SourcePage}.",
                        QualityIssueSeverity.Warning
                    )
                );
            }
        }
        else
        {
            var (kind, _) = StudySourceClassifier.Classify(q.SourcePdfPath);
            if (kind != StudySourceKind.Image && q.SourceStartLine is null or <= 0)
            {
                issues.Add(
                    new QuestionQualityIssue(
                        q.Id,
                        "UNGROUNDED_EVIDENCE",
                        "Could not resolve exact line range in the source file.",
                        QualityIssueSeverity.Warning
                    )
                );
            }
        }
    }

    private static bool ContainsWholeWord(string? text, string? needle)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(needle))
            return false;

        try
        {
            return Regex.IsMatch(
                text,
                $@"(?<!\w){Regex.Escape(needle)}(?!\w)",
                RegexOptions.IgnoreCase
            );
        }
        catch
        {
            return text.Contains(needle, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static HashSet<string> TokenizeStem(string? stem)
    {
        if (string.IsNullOrWhiteSpace(stem))
            return [];

        var matches = Regex.Matches(stem.ToLowerInvariant(), @"[a-z0-9]+");
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in matches)
        {
            if (m.Value.Length >= 3 && !StemStopwords.Contains(m.Value))
                set.Add(m.Value);
        }
        return set;
    }

    private static double ComputeJaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0;

        int inter = 0;
        foreach (var tok in a)
        {
            if (b.Contains(tok))
                inter++;
        }

        int union = a.Count + b.Count - inter;
        return union == 0 ? 0 : (double)inter / union;
    }
}
