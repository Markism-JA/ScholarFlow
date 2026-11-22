using System.Text;

namespace ScholarFlow.Core.Quiz;

/// <summary>
/// Unified pedagogical &amp; modality blueprint compiler shared across AI API and Manual Web UI modes.
/// Integrates cognitive depth allocations, question archetypes, specialized modalities (Code, Math LaTeX, Diagrams),
/// weak-spot remediation, contrastive explanations, and progressive hints into one cohesive prompt specification.
/// </summary>
public static class QuizBlueprintBuilder
{
    public static (
        int Recall,
        int Comprehension,
        int Application,
        int Analysis
    ) GetCognitiveWeights(CognitiveMixPreset preset) =>
        preset switch
        {
            CognitiveMixPreset.Foundations => (40, 35, 20, 5),
            CognitiveMixPreset.ExamApplication => (10, 20, 40, 30),
            CognitiveMixPreset.DeepMastery => (5, 15, 35, 45),
            _ => (20, 30, 30, 20), // Balanced
        };

    public static string GetArchetypeDisplayName(QuestionArchetype archetype) =>
        archetype switch
        {
            QuestionArchetype.FarTransfer => "Far-Transfer",
            QuestionArchetype.DiscriminativeContrast => "Contrast X vs Y",
            QuestionArchetype.CausalPerturbation => "What-If / Causal",
            QuestionArchetype.SpotTheFlaw => "Spot-the-Flaw",
            QuestionArchetype.DualCoding => "Dual-Coding",
            _ => archetype.ToString(),
        };

    /// <summary>
    /// Formats a raw archetype token (e.g. "DiscriminativeContrast") into a clean UI badge label.
    /// </summary>
    public static string FormatArchetypeBadge(string? rawArchetype)
    {
        if (string.IsNullOrWhiteSpace(rawArchetype))
            return string.Empty;

        var clean = rawArchetype.Trim();
        if (Enum.TryParse<QuestionArchetype>(clean, ignoreCase: true, out var parsed))
            return GetArchetypeDisplayName(parsed);

        return clean switch
        {
            var s when s.Contains("Transfer", StringComparison.OrdinalIgnoreCase) => "Far-Transfer",
            var s when s.Contains("Contrast", StringComparison.OrdinalIgnoreCase) =>
                "Contrast X vs Y",
            var s
                when s.Contains("Causal", StringComparison.OrdinalIgnoreCase)
                    || s.Contains("Perturb", StringComparison.OrdinalIgnoreCase)
                    || s.Contains("WhatIf", StringComparison.OrdinalIgnoreCase) =>
                "What-If / Causal",
            var s
                when s.Contains("Flaw", StringComparison.OrdinalIgnoreCase)
                    || s.Contains("Bug", StringComparison.OrdinalIgnoreCase) => "Spot-the-Flaw",
            var s when s.Contains("Dual", StringComparison.OrdinalIgnoreCase) => "Dual-Coding",
            _ => clean,
        };
    }

    /// <summary>
    /// Compiles Cognitive Depth distribution, Modality-Aware Question Archetypes, Weak-Spot Focus,
    /// Plausible Misconception Distractors, Contrastive Explanations, and Progressive Hints.
    /// </summary>
    public static string BuildPedagogicalBlueprintBlock(
        QuizGenerationRequest req,
        IReadOnlyList<string> weakTopics,
        ContentModalityProfile? modality = null
    )
    {
        var sb = new StringBuilder();
        sb.AppendLine("ASSESSMENT DESIGN & COGNITIVE BLUEPRINT:");

        // 1. Dynamic Cognitive Depth Distribution (supports both custom per-level weights and presets)
        var pcts = req.ResolveCognitivePercentages();
        var exactCog = req.ResolveExactCognitiveCounts();
        int rec = pcts.GetValueOrDefault(CognitiveLevel.Recall, 20);
        int comp = pcts.GetValueOrDefault(CognitiveLevel.Comprehension, 30);
        int app = pcts.GetValueOrDefault(CognitiveLevel.Application, 30);
        int ana = pcts.GetValueOrDefault(CognitiveLevel.Analysis, 20);

        var exactCogSpec = string.Join(
            ", ",
            exactCog.Where(kv => kv.Value > 0).Select(kv => $"{kv.Value} {kv.Key}")
        );
        sb.AppendLine(
            $"- COGNITIVE DEPTH DISTRIBUTION: Target {rec}% Recall, {comp}% Comprehension, {app}% Application, and {ana}% Analysis (approx. {exactCogSpec}). Set each question's \"cognitiveLevel\" accurately."
        );

        // 2. Modality-Integrated Question Archetypes
        bool hasCode = modality?.HasCode == true;
        bool hasMath = modality?.HasMath == true;
        bool hasDiagrams = modality?.HasDiagrams == true;

        var archetypes = req.SelectedArchetypes is { Count: > 0 }
            ? req.SelectedArchetypes.Distinct().ToList()
            :
            [
                QuestionArchetype.FarTransfer,
                QuestionArchetype.DiscriminativeContrast,
                QuestionArchetype.CausalPerturbation,
                QuestionArchetype.SpotTheFlaw,
                .. hasDiagrams || hasMath
                    ? [QuestionArchetype.DualCoding]
                    : Array.Empty<QuestionArchetype>(),
            ];

        sb.AppendLine(
            "- QUESTION ARCHETYPES (weave these patterns across the assessment and set each question's \"archetype\" field):"
        );
        foreach (var arch in archetypes)
        {
            switch (arch)
            {
                case QuestionArchetype.FarTransfer:
                    sb.AppendLine(
                        hasCode
                            ? "  * \"FarTransfer\": Apply an algorithm, pattern, or function from the source to a novel input trace or new runtime constraint."
                            : "  * \"FarTransfer\": Apply a rule, concept, or theory to a novel real-world scenario rather than repeating textbook phrasing."
                    );
                    break;
                case QuestionArchetype.DiscriminativeContrast:
                    sb.AppendLine(
                        hasCode
                            ? "  * \"DiscriminativeContrast\": Contrast two easily confused language constructs, data structures, or complexity/architecture tradeoffs (\"How does X differ from Y under condition Z?\")."
                            : "  * \"DiscriminativeContrast\": Contrast two easily confused sibling concepts or boundary conditions (\"How does X differ from Y under condition Z?\")."
                    );
                    break;
                case QuestionArchetype.CausalPerturbation:
                    sb.AppendLine(
                        hasCode
                            ? "  * \"CausalPerturbation\": Ask \"What-If\" code/system perturbation questions (\"If condition X, line Y, or parameter Z is modified, how does the output or state change?\")."
                        : hasMath
                            ? "  * \"CausalPerturbation\": Ask \"What-If\" parameter perturbation questions (\"If variable X doubles or constraint Y changes, how does the formula or bound behave?\")."
                        : "  * \"CausalPerturbation\": Ask \"What-If\" causal-chain questions (\"If step B fails, doubles, or is omitted, what happens downstream?\")."
                    );
                    break;
                case QuestionArchetype.SpotTheFlaw:
                    sb.AppendLine(
                        hasCode
                            ? "  * \"SpotTheFlaw\": Present a code snippet or trace with ONE subtle bug, edge-case failure, or off-by-one/resource flaw and ask the student to diagnose or fix it."
                        : hasMath
                            ? "  * \"SpotTheFlaw\": Present a derivation or formula application with ONE subtle flawed step or violated assumption and ask the student to identify the flaw."
                        : "  * \"SpotTheFlaw\": Present a plausible explanation, argument, or scenario containing ONE subtle conceptual flaw and ask the student to diagnose or correct it."
                    );
                    break;
                case QuestionArchetype.DualCoding:
                    sb.AppendLine(
                        "  * \"DualCoding\": Pair verbal reasoning with a visual/structural representation (\"diagram\" flowchart/sequence, \"mathLatex\" formula, \"codeSnippet\", or attached diagram image)."
                    );
                    break;
            }
        }

        // 3. Weak-Spot Remediation
        bool targetWeakSpots = req.FocusWeakSpots || req.Preset == QuizPresetMode.WeakTopicDrill;
        if (targetWeakSpots && weakTopics.Count > 0)
        {
            sb.AppendLine(
                $"- WEAK-SPOT REMEDIATION: Dedicate at least 40% of questions to testing and reinforcing these previously missed/high-lapse topics (where supported by the provided scope): {string.Join(", ", weakTopics.Take(10))}."
            );
        }

        // 4. Plausible Misconception Distractors
        sb.AppendLine(
            "- PLAUSIBLE MISCONCEPTION DISTRACTORS: For MultipleChoice and Situational items, craft 3 wrong options grounded in real misconceptions or sibling concepts from the material. Keep all 4 options similar in length and grammatical form; NEVER use \"All of the above\", \"None of the above\", or obvious length giveaways."
        );

        // 5. Contrastive Explanations
        if (req.EnableContrastiveExplanations)
        {
            sb.AppendLine(
                "- CONTRASTIVE EXPLANATIONS: Structure each \"explanation\" concisely using these labeled parts: \"Why correct: <reason>. Why distractors fail: <why the most tempting wrong option/misconception fails>. Core principle: <1-sentence anchor takeaway>.\""
            );
        }

        // 6. Progressive Hints
        if (req.IncludeProgressiveHints)
        {
            sb.AppendLine(
                "- PROGRESSIVE HINTS: Populate \"hint\" with a brief 1-sentence cognitive nudge (a guiding clue, invariant, or boundary condition) that helps recall WITHOUT revealing or containing the answer itself."
            );
        }
        else
        {
            sb.AppendLine("- Set \"hint\": null on all questions.");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Orders a pool of questions from one or more decks/courses into an interleaved review sequence
    /// (Rohrer &amp; Taylor, 2007): prioritizes missed/due items while avoiding consecutive questions
    /// from the same topic tag or source deck whenever possible.
    /// </summary>
    public static List<QuizQuestionRecord> BuildInterleavedReviewOrder(
        IReadOnlyList<QuizQuestionRecord> pool,
        int maxItems = 25,
        DateOnly? todayOverride = null
    )
    {
        if (pool.Count == 0 || maxItems <= 0)
            return [];

        var today = todayOverride ?? DateOnly.FromDateTime(DateTime.Today);

        var ranked = pool.OrderBy(q => q.LastAnsweredCorrect == false ? 0 : 1)
            .ThenByDescending(q => q.SrsLapses)
            .ThenBy(q => !q.SrsDueDate.HasValue || q.SrsDueDate.Value <= today ? 0 : 1)
            .ThenBy(q => q.SrsDueDate ?? today)
            .ThenBy(q => q.SrsReps)
            .ToList();

        var buckets = ranked
            .GroupBy(NormalizeBucketKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Queue<QuizQuestionRecord>(g))
            .OrderByDescending(b => b.Count)
            .ToList();

        var result = new List<QuizQuestionRecord>(Math.Min(maxItems, pool.Count));
        string? lastTopic = null;
        string? lastQuizId = null;

        while (result.Count < maxItems)
        {
            buckets.RemoveAll(b => b.Count == 0);
            if (buckets.Count == 0)
                break;

            int pickIdx = -1;
            int fallbackTopicIdx = -1;
            int fallbackAnyIdx = 0;

            for (int i = 0; i < buckets.Count; i++)
            {
                var candidate = buckets[i].Peek();
                bool diffTopic = !string.Equals(
                    candidate.TopicTag,
                    lastTopic,
                    StringComparison.OrdinalIgnoreCase
                );
                bool diffQuiz = !string.Equals(
                    candidate.QuizId,
                    lastQuizId,
                    StringComparison.OrdinalIgnoreCase
                );

                if (diffTopic && diffQuiz)
                {
                    pickIdx = i;
                    break;
                }
                if (diffTopic && fallbackTopicIdx < 0)
                    fallbackTopicIdx = i;
            }

            int chosenIdx =
                pickIdx >= 0
                    ? pickIdx
                    : (fallbackTopicIdx >= 0 ? fallbackTopicIdx : fallbackAnyIdx);
            var chosen = buckets[chosenIdx].Dequeue();
            result.Add(chosen);
            lastTopic = chosen.TopicTag;
            lastQuizId = chosen.QuizId;

            if (buckets[chosenIdx].Count > 0 && buckets.Count > 1)
            {
                var b = buckets[chosenIdx];
                buckets.RemoveAt(chosenIdx);
                buckets.Add(b);
            }
        }

        return result;
    }

    private static string NormalizeBucketKey(QuizQuestionRecord q)
    {
        var tag = string.IsNullOrWhiteSpace(q.TopicTag)
            ? "general"
            : q.TopicTag.Trim().ToLowerInvariant();
        var quiz = string.IsNullOrWhiteSpace(q.QuizId) ? "" : q.QuizId.Trim().ToLowerInvariant();
        return $"{quiz}::{tag}";
    }
}
