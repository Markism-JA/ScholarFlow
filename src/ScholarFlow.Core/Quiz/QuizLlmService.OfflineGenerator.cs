using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScholarFlow.Core.Database;
using ScholarFlow.Core.Models;

namespace ScholarFlow.Core.Quiz;

public sealed partial class QuizLlmService
{
    /// <summary>
    /// Smart deterministic offline PDF analyzer that extracts definition & claim statements directly from GroundedPdfPages,
    /// producing evidence-grounded Identification, MultipleChoice, FillInTheBlank, and TrueFalse questions with exact PDF highlight quotes.
    /// </summary>
    public static QuizGenerationResponseDto GenerateOfflineGroundedDto(
        QuizGenerationRequest req,
        IReadOnlyList<GroundedPdfPage> pages,
        int? desiredCountOverride = null
    )
    {
        var candidates =
            new List<(
                GroundedPdfPage Page,
                string TermName,
                string Predicate,
                string FullSentence
            )>();
        var defRegex = new Regex(
            @"(?<term>[A-Z][A-Za-z0-9\-\s/()]{2,38}?)\s+(?:is\s+defined\s+as|refers\s+to|is\s+a\s+type\s+of|is\s+the\s+process\s+of|is\s+an?\s+[a-z]+\s+that|provides\s+a|ensures\s+that|is\s+used\s+to)\s+(?<pred>[^.?!:\n]{18,160}[.])",
            RegexOptions.Compiled
        );

        foreach (var page in pages)
        {
            var normalizedText = Regex.Replace(page.Text, @"\s+", " ");
            foreach (Match m in defRegex.Matches(normalizedText))
            {
                var term = m.Groups["term"].Value.Trim();
                var pred = m.Groups["pred"].Value.Trim();
                var full = m.Value.Trim();

                if (
                    term.Split(' ').Length > 5
                    || term.Contains("STI", StringComparison.OrdinalIgnoreCase)
                    || term.StartsWith("This ", StringComparison.OrdinalIgnoreCase)
                    || term.StartsWith("It ", StringComparison.OrdinalIgnoreCase)
                    || term.StartsWith("The ", StringComparison.OrdinalIgnoreCase)
                        && term.Split(' ').Length < 2
                )
                {
                    continue;
                }

                candidates.Add((page, term, pred, full));
            }

            // Also extract clean factual sentences if definition matches are sparse
            var sentences = Regex
                .Split(normalizedText, @"(?<=[.?!])\s+")
                .Where(s =>
                    s.Length is >= 45 and <= 170
                    && char.IsUpper(s[0])
                    && !s.Contains("Property of STI", StringComparison.OrdinalIgnoreCase)
                )
                .Take(6);

            foreach (var s in sentences)
            {
                var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length >= 8)
                {
                    var keyPhrase = string.Join(' ', words.Take(Math.Min(3, words.Length)))
                        .Trim(',', ':', ';');
                    var rest = string.Join(' ', words.Skip(Math.Min(3, words.Length)));
                    if (keyPhrase.Length >= 4 && rest.Length >= 20)
                        candidates.Add((page, keyPhrase, rest, s));
                }
            }
        }

        var distinctCandidates = candidates
            .GroupBy(c => c.TermName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        int desired = Math.Clamp(desiredCountOverride ?? req.QuestionCount, 1, 150);
        int targetCount = Math.Min(desired, Math.Max(1, distinctCandidates.Count));
        var allTermNames = distinctCandidates
            .Select(c => c.TermName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Build exact requested kind queue from req.ResolveExactKindCounts(targetCount)
        var exactKindCounts = req.ResolveExactKindCounts(targetCount);
        var kindQueue = new List<QuizQuestionKind>();
        foreach (var (kind, cnt) in exactKindCounts)
        {
            for (int k = 0; k < cnt; k++)
                kindQueue.Add(kind);
        }
        if (kindQueue.Count == 0)
            kindQueue.Add(QuizQuestionKind.MultipleChoice);

        var questions = new List<GeneratedQuestionDto>();
        for (int i = 0; i < Math.Min(targetCount, distinctCandidates.Count); i++)
        {
            var c = distinctCandidates[i];
            var topicTag = Regex
                .Replace(c.TermName.ToLowerInvariant(), @"[^a-z0-9]+", "-")
                .Trim('-');
            if (string.IsNullOrEmpty(topicTag))
                topicTag = "concept";

            var requestedKind = kindQueue[i % kindQueue.Count];

            if (requestedKind is QuizQuestionKind.MultipleChoice or QuizQuestionKind.Situational)
            {
                var distractors = allTermNames
                    .Where(t => !t.Equals(c.TermName, StringComparison.OrdinalIgnoreCase))
                    .Skip(i % Math.Max(1, allTermNames.Count - 3))
                    .Take(3)
                    .ToList();
                while (distractors.Count < 3)
                    distractors.Add($"Alternative Concept {distractors.Count + 1}");

                var opts = new List<string>(distractors) { c.TermName };
                opts = opts.OrderBy(o => (o.GetHashCode() ^ i) & 0x7FFFFFFF).ToList();

                var promptText =
                    requestedKind == QuizQuestionKind.Situational
                        ? $"Scenario ({c.Page.SectionBreadcrumb}, Page {c.Page.PageNumber}): A team needs to apply the concept that \"{c.Predicate}\". Which concept should they use?"
                        : $"According to {c.Page.SectionBreadcrumb} (Page {c.Page.PageNumber}), which concept best matches the following statement?\n\n\"...{c.Predicate}\"";

                questions.Add(
                    new GeneratedQuestionDto
                    {
                        Kind = requestedKind.ToString(),
                        CognitiveLevel =
                            requestedKind == QuizQuestionKind.Situational
                                ? nameof(CognitiveLevel.Application)
                                : nameof(CognitiveLevel.Comprehension),
                        Difficulty = requestedKind == QuizQuestionKind.Situational ? 4 : 3,
                        TopicTag = topicTag,
                        Prompt = promptText,
                        Options = opts,
                        CorrectAnswer = c.TermName,
                        Explanation =
                            $"Grounded on Page {c.Page.PageNumber} ({c.Page.SectionBreadcrumb}): \"{c.FullSentence}\"",
                        Evidence = new EvidenceAnchorDto
                        {
                            PageNumber = c.Page.PageNumber,
                            SectionTitle = c.Page.SectionBreadcrumb,
                            VerbatimQuote = c.FullSentence,
                        },
                    }
                );
            }
            else if (requestedKind == QuizQuestionKind.FillInTheBlank)
            {
                var blanked = c.FullSentence.Replace(
                    c.TermName,
                    "_____",
                    StringComparison.OrdinalIgnoreCase
                );
                if (blanked == c.FullSentence)
                    blanked = $"_____ {c.Predicate}";

                questions.Add(
                    new GeneratedQuestionDto
                    {
                        Kind = nameof(QuizQuestionKind.FillInTheBlank),
                        CognitiveLevel = nameof(CognitiveLevel.Recall),
                        Difficulty = 3,
                        TopicTag = topicTag,
                        Prompt =
                            $"Fill in the blank ({c.Page.SectionBreadcrumb}):\n\n\"{blanked}\"",
                        Options = [],
                        CorrectAnswer = c.TermName,
                        Explanation =
                            $"Full statement on Page {c.Page.PageNumber}: \"{c.FullSentence}\"",
                        Evidence = new EvidenceAnchorDto
                        {
                            PageNumber = c.Page.PageNumber,
                            SectionTitle = c.Page.SectionBreadcrumb,
                            VerbatimQuote = c.FullSentence,
                        },
                    }
                );
            }
            else if (requestedKind == QuizQuestionKind.Identification)
            {
                questions.Add(
                    new GeneratedQuestionDto
                    {
                        Kind = nameof(QuizQuestionKind.Identification),
                        CognitiveLevel = nameof(CognitiveLevel.Recall),
                        Difficulty = 3,
                        TopicTag = topicTag,
                        Prompt =
                            $"Identify the term or concept defined in {c.Page.SectionBreadcrumb} (Page {c.Page.PageNumber}) as:\n\n\"{c.Predicate}\"",
                        Options = [],
                        CorrectAnswer = c.TermName,
                        Explanation =
                            $"Full statement on Page {c.Page.PageNumber}: \"{c.FullSentence}\"",
                        Evidence = new EvidenceAnchorDto
                        {
                            PageNumber = c.Page.PageNumber,
                            SectionTitle = c.Page.SectionBreadcrumb,
                            VerbatimQuote = c.FullSentence,
                        },
                    }
                );
            }
            else if (requestedKind == QuizQuestionKind.Ordering)
            {
                var seqPeers = distinctCandidates
                    .Skip(Math.Max(0, Math.Min(i, Math.Max(0, distinctCandidates.Count - 4))))
                    .Take(4)
                    .Select(x => x.TermName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                while (seqPeers.Count < 3)
                    seqPeers.Add($"Phase {seqPeers.Count + 1}");

                questions.Add(
                    new GeneratedQuestionDto
                    {
                        Kind = nameof(QuizQuestionKind.Ordering),
                        CognitiveLevel = nameof(CognitiveLevel.Analysis),
                        Difficulty = 4,
                        TopicTag = topicTag,
                        Prompt =
                            $"Arrange the following concepts in the chronological order they are introduced in {c.Page.SectionBreadcrumb}:",
                        Options = seqPeers,
                        CorrectAnswer = string.Join(" -> ", seqPeers),
                        Explanation =
                            $"Appears in sequence starting at Page {c.Page.PageNumber} ({c.Page.SectionBreadcrumb}).",
                        Evidence = new EvidenceAnchorDto
                        {
                            PageNumber = c.Page.PageNumber,
                            SectionTitle = c.Page.SectionBreadcrumb,
                            VerbatimQuote = c.FullSentence,
                        },
                    }
                );
            }
            else
            {
                questions.Add(
                    new GeneratedQuestionDto
                    {
                        Kind = nameof(QuizQuestionKind.TrueFalse),
                        CognitiveLevel = nameof(CognitiveLevel.Application),
                        Difficulty = 2,
                        TopicTag = topicTag,
                        Prompt =
                            $"True or False: In the context of {c.Page.SectionBreadcrumb}, {c.FullSentence}",
                        Options = ["True", "False"],
                        CorrectAnswer = "True",
                        Explanation =
                            $"Verified directly on Page {c.Page.PageNumber} ({c.Page.SectionBreadcrumb}): \"{c.FullSentence}\"",
                        Evidence = new EvidenceAnchorDto
                        {
                            PageNumber = c.Page.PageNumber,
                            SectionTitle = c.Page.SectionBreadcrumb,
                            VerbatimQuote = c.FullSentence,
                        },
                    }
                );
            }
        }

        var termLabel = req.Term
            ?? pages.FirstOrDefault(p => p.Term is not null)?.Term
            ?? "All Terms";
        return new QuizGenerationResponseDto
        {
            QuizTitle = $"{req.CourseTitle} — {termLabel} Grounded Assessment",
            CourseTerm = termLabel,
            KeyConceptsCovered = allTermNames.Take(10).ToList(),
            Questions = questions,
        };
    }
}
