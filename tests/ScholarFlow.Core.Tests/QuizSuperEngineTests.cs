using ScholarFlow.Core.Database;
using ScholarFlow.Core.Quiz;
using Xunit;

namespace ScholarFlow.Core.Tests;

public sealed class QuizSuperEngineTests
{
    [Fact]
    public void BlueprintBuilder_CompilesCognitiveMixArchetypesWeakSpotsAndToggles()
    {
        var req = new QuizGenerationRequest
        {
            CourseTitle = "Operating Systems",
            CognitiveMix = CognitiveMixPreset.ExamApplication,
            SelectedArchetypes = [QuestionArchetype.FarTransfer, QuestionArchetype.SpotTheFlaw],
            FocusWeakSpots = true,
            EnableContrastiveExplanations = true,
            IncludeProgressiveHints = true,
        };

        var block = QuizBlueprintBuilder.BuildPedagogicalBlueprintBlock(
            req,
            ["virtual-memory", "deadlocks"]
        );

        Assert.Contains("10% Recall, 20% Comprehension, 40% Application, and 30% Analysis", block);
        Assert.Contains("\"FarTransfer\"", block);
        Assert.Contains("\"SpotTheFlaw\"", block);
        Assert.DoesNotContain("\"DualCoding\"", block);
        Assert.Contains("virtual-memory, deadlocks", block);
        Assert.Contains("CONTRASTIVE EXPLANATIONS", block);
        Assert.Contains("PROGRESSIVE HINTS", block);
        Assert.Contains("PLAUSIBLE MISCONCEPTION DISTRACTORS", block);
    }

    [Fact]
    public void BlueprintBuilder_SupportsCustomCognitiveAllocationsAndModalityAwareArchetypes()
    {
        var req = new QuizGenerationRequest
        {
            CourseTitle = "Computer Architecture",
            QuestionCount = 10,
            CognitiveAllocations = new()
            {
                [CognitiveLevel.Recall] = 10,
                [CognitiveLevel.Comprehension] = 10,
                [CognitiveLevel.Application] = 50,
                [CognitiveLevel.Analysis] = 30,
            },
            SelectedArchetypes = [QuestionArchetype.SpotTheFlaw, QuestionArchetype.CausalPerturbation],
        };

        var exactCog = req.ResolveExactCognitiveCounts(10);
        Assert.Equal(1, exactCog[CognitiveLevel.Recall]);
        Assert.Equal(1, exactCog[CognitiveLevel.Comprehension]);
        Assert.Equal(5, exactCog[CognitiveLevel.Application]);
        Assert.Equal(3, exactCog[CognitiveLevel.Analysis]);

        var codeModality = new ContentModalityProfile(HasCode: true, HasMath: false, HasDiagrams: false, DetectedLanguage: "c");
        var block = QuizBlueprintBuilder.BuildPedagogicalBlueprintBlock(req, [], codeModality);

        Assert.Contains("Target 10% Recall, 10% Comprehension, 50% Application, and 30% Analysis", block);
        Assert.Contains("1 Recall, 1 Comprehension, 5 Application, 3 Analysis", block);
        Assert.Contains("code snippet or trace with ONE subtle bug", block);
        Assert.Equal("Spot-the-Flaw", QuizBlueprintBuilder.FormatArchetypeBadge("SpotTheFlaw"));
        Assert.Equal("Contrast X vs Y", QuizBlueprintBuilder.FormatArchetypeBadge("DiscriminativeContrast"));
    }

    [Fact]
    public void BlueprintBuilder_DisablesHintsAndContrastiveWhenToggledOff()
    {
        var req = new QuizGenerationRequest
        {
            CognitiveMix = CognitiveMixPreset.Foundations,
            EnableContrastiveExplanations = false,
            IncludeProgressiveHints = false,
        };

        var block = QuizBlueprintBuilder.BuildPedagogicalBlueprintBlock(req, []);

        Assert.Contains("40% Recall, 35% Comprehension, 20% Application, and 5% Analysis", block);
        Assert.DoesNotContain("CONTRASTIVE EXPLANATIONS", block);
        Assert.Contains("Set \"hint\": null on all questions.", block);
    }

    [Fact]
    public void ManualWebUiAndFileSourcePrompts_IncludeSuperEngineBlueprintAndSchemaParity()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sf-stage2-parity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var codePath = Path.Combine(tempDir, "scheduler.py");
            File.WriteAllText(
                codePath,
                "def round_robin(queue, quantum):\n    while queue:\n        task = queue.pop(0)\n        task.run(quantum)\n"
            );

            var req = new QuizGenerationRequest
            {
                CourseTitle = "Operating Systems",
                SourceFilePaths = [codePath],
                CognitiveMix = CognitiveMixPreset.DeepMastery,
                SelectedArchetypes = [QuestionArchetype.CausalPerturbation],
                EnableContrastiveExplanations = true,
                IncludeProgressiveHints = true,
                QuestionCount = 5,
            };

            var svc = new QuizLlmService();
            var filePrompt = svc.BuildFileSourcePromptForManualCopy(req, 50_000, out _);
            var uploadPrompt = svc.BuildInstructionPromptForUploadedPdf(
                new QuizGenerationRequest
                {
                    CourseTitle = "Operating Systems",
                    CognitiveMix = CognitiveMixPreset.DeepMastery,
                    SelectedArchetypes = [QuestionArchetype.CausalPerturbation],
                    EnableContrastiveExplanations = true,
                    IncludeProgressiveHints = true,
                    QuestionCount = 5,
                },
                [(1, "Week 1")]
            );

            foreach (var prompt in new[] { filePrompt, uploadPrompt })
            {
                Assert.Contains("5% Recall, 15% Comprehension, 35% Application, and 45% Analysis", prompt);
                Assert.Contains("\"CausalPerturbation\"", prompt);
                Assert.Contains("\"archetype\":", prompt);
                Assert.Contains("\"hint\":", prompt);
                Assert.Contains("CONTRASTIVE EXPLANATIONS", prompt);
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void QualityCritic_FlagsLengthSkewLazyDistractorAnswerLeakageAndMissingBlank()
    {
        var q1 = new QuizQuestionRecord
        {
            Id = "q1",
            Kind = QuizQuestionKind.MultipleChoice,
            Prompt = "Which scheduling policy prevents starvation while balancing interactive response time?",
            Options =
            [
                "FCFS",
                "SJF",
                "All of the above",
                "Multi-level feedback queue with dynamic aging that promotes waiting processes across priority queues over time",
            ],
            CorrectAnswer =
                "Multi-level feedback queue with dynamic aging that promotes waiting processes across priority queues over time",
            Explanation = "Too short",
            SourcePdfPath = "test.pdf",
            SourcePage = 3,
            VerbatimQuote = "Aging gradually increases priority of waiting processes.",
            HighlightRects = [], // Unmatched PDF bbox
        };

        var q2 = new QuizQuestionRecord
        {
            Id = "q2",
            Kind = QuizQuestionKind.FillInTheBlank,
            Prompt = "A deadlock occurs when circular wait holds in the resource graph.",
            CorrectAnswer = "deadlock",
            Hint = "Think about a deadlock in resource allocation.",
            Explanation = "Why correct: Circular wait is a Coffman condition. Why distractors fail: N/A. Core principle: Break one condition.",
            SourcePdfPath = "main.cs",
            SourceStartLine = 10,
            SourceEndLine = 12,
            VerbatimQuote = "if (hasCycle) return Deadlock;",
        };

        var report = QuizQualityCritic.Audit([q1, q2]);

        Assert.True(report.HasIssues);
        var q1Codes = report.GetIssuesFor("q1").Select(i => i.Code).ToHashSet();
        Assert.Contains("LAZY_DISTRACTOR", q1Codes);
        Assert.Contains("LENGTH_SKEW_GIVEAWAY", q1Codes);
        Assert.Contains("WEAK_EXPLANATION", q1Codes);
        Assert.Contains("UNGROUNDED_EVIDENCE", q1Codes);

        var q2Codes = report.GetIssuesFor("q2").Select(i => i.Code).ToHashSet();
        Assert.Contains("MISSING_BLANK", q2Codes);
        Assert.Contains("HINT_LEAKS_ANSWER", q2Codes);
        Assert.Contains("PROMPT_LEAKS_ANSWER", q2Codes);
    }

    [Fact]
    public void QualityCritic_FlagsDuplicateStemsAndPassesCleanQuestions()
    {
        var clean = new QuizQuestionRecord
        {
            Id = "clean-1",
            Kind = QuizQuestionKind.MultipleChoice,
            Prompt = "If the TLB hit rate drops from 98% to 70%, what happens to effective memory access time?",
            Options =
            [
                "Effective access time rises sharply because more lookups require a full page-table walk.",
                "Effective access time drops because the cache bypasses virtual-to-physical translation.",
                "Effective access time stays constant because page tables reside inside CPU registers.",
                "Effective access time halves because misses trigger parallel prefetching from disk.",
            ],
            CorrectAnswer =
                "Effective access time rises sharply because more lookups require a full page-table walk.",
            Hint = "Consider the extra memory reference required whenever a translation is not cached.",
            Explanation =
                "Why correct: Each TLB miss adds page-table memory lookups. Why distractors fail: Page tables live in RAM, not CPU registers. Core principle: EMAT scales with TLB miss rate.",
            SourcePdfPath = "notes.pdf",
            SourcePage = 12,
            VerbatimQuote = "A TLB miss requires an additional memory access to consult the page table.",
            HighlightRects = [new NormalizedHighlightRect(0.1, 0.2, 0.5, 0.05)],
        };

        Assert.False(QuizQualityCritic.Audit([clean]).HasIssues);

        var dup = new QuizQuestionRecord
        {
            Id = "clean-2",
            Kind = QuizQuestionKind.MultipleChoice,
            Prompt = "If the TLB hit rate drops from 98% to 70%, what happens to the effective memory access time?",
            Options = [.. clean.Options],
            CorrectAnswer = clean.CorrectAnswer,
            Explanation = clean.Explanation,
            SourcePdfPath = clean.SourcePdfPath,
            SourcePage = clean.SourcePage,
            VerbatimQuote = clean.VerbatimQuote,
            HighlightRects = [.. clean.HighlightRects],
        };

        var dupReport = QuizQualityCritic.Audit([clean, dup]);
        Assert.Contains(dupReport.GetIssuesFor("clean-2"), i => i.Code == "DUPLICATE_STEM");
    }

    [Fact]
    public void ConfidenceCalibratedSrs_AppliesHypercorrectionLuckyGuessAndMasteryBoost()
    {
        var today = new DateOnly(2026, 11, 22);

        // 1. Hypercorrection penalty: Certain + Wrong -> +2 lapses, -0.30 ease, 1d reset
        var qHyper = new QuizQuestionRecord { SrsEaseFactor = 2.5, SrsReps = 3, SrsIntervalDays = 10 };
        ScholarFlowDatabase.ApplyConfidenceCalibratedSrs(
            qHyper,
            isCorrect: false,
            qualityRating: 4,
            confidence: ConfidenceLevel.Certain,
            todayOverride: today
        );
        Assert.Equal(2, qHyper.SrsLapses);
        Assert.Equal(0, qHyper.SrsReps);
        Assert.Equal(2.2, qHyper.SrsEaseFactor, precision: 2);
        Assert.Equal(today.AddDays(1), qHyper.SrsDueDate);

        // 2. Lucky guess dampener: Guess + Right -> conservative interval (1d on rep 1, 2d on rep 2)
        var qGuess = new QuizQuestionRecord { SrsEaseFactor = 2.5, SrsReps = 1, SrsIntervalDays = 1 };
        int effQ = ScholarFlowDatabase.ApplyConfidenceCalibratedSrs(
            qGuess,
            isCorrect: true,
            qualityRating: 4,
            confidence: ConfidenceLevel.Guess,
            todayOverride: today
        );
        Assert.Equal(3, effQ);
        Assert.Equal(2.0, qGuess.SrsIntervalDays);
        Assert.Equal(today.AddDays(2), qGuess.SrsDueDate);

        // 3. Mastery boost: Certain + Right -> quality 5, 3d on rep 1
        var qCertain = new QuizQuestionRecord { SrsEaseFactor = 2.5, SrsReps = 0 };
        int boostQ = ScholarFlowDatabase.ApplyConfidenceCalibratedSrs(
            qCertain,
            isCorrect: true,
            qualityRating: 4,
            confidence: ConfidenceLevel.Certain,
            todayOverride: today
        );
        Assert.Equal(5, boostQ);
        Assert.Equal(3.0, qCertain.SrsIntervalDays);
        Assert.Equal(today.AddDays(3), qCertain.SrsDueDate);
    }

    [Fact]
    public void InterleavedReviewOrder_AvoidsConsecutiveSameTopicAndPrioritizesMissed()
    {
        var pool = new List<QuizQuestionRecord>
        {
            new() { Id = "a1", QuizId = "d1", TopicTag = "paging", LastAnsweredCorrect = false },
            new() { Id = "a2", QuizId = "d1", TopicTag = "paging", LastAnsweredCorrect = false },
            new() { Id = "b1", QuizId = "d2", TopicTag = "threads", LastAnsweredCorrect = false },
            new() { Id = "b2", QuizId = "d2", TopicTag = "threads", LastAnsweredCorrect = true },
            new() { Id = "c1", QuizId = "d3", TopicTag = "deadlock", LastAnsweredCorrect = false },
        };

        var ordered = QuizBlueprintBuilder.BuildInterleavedReviewOrder(pool, maxItems: 5);

        Assert.Equal(5, ordered.Count);
        for (int i = 1; i < ordered.Count; i++)
        {
            Assert.NotEqual(ordered[i - 1].TopicTag, ordered[i].TopicTag);
        }
    }

    [Fact]
    public async Task DatabaseRoundTrip_PersistsHintAndArchetype()
    {
        var tempBasin = Path.Combine(Path.GetTempPath(), $"sf-stage2-db-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempBasin);
        try
        {
            var session = new QuizSessionRecord
            {
                CourseFolder = "OS",
                CourseTitle = "Operating Systems",
                Title = "Stage 2 Round-Trip",
                SourcePdfPath = "os.pdf",
                ModelUsed = "test",
                Questions =
                [
                    new QuizQuestionRecord
                    {
                        Prompt = "What happens if the quantum is set too small in Round Robin?",
                        CorrectAnswer = "Context-switch overhead dominates CPU time.",
                        Hint = "Think about what the OS does every time a timer interrupt fires.",
                        Archetype = "CausalPerturbation",
                        Explanation = "Why correct: Frequent switching wastes cycles.",
                        SourcePdfPath = "os.pdf",
                        SourcePage = 5,
                        VerbatimQuote = "Very small time quanta increase context switch overhead.",
                    },
                ],
            };

            await ScholarFlowDatabase.SaveQuizSessionAsync(tempBasin, session);
            var loaded = await ScholarFlowDatabase.LoadQuizSessionsAsync(tempBasin, "OS");

            Assert.Single(loaded);
            var q = Assert.Single(loaded[0].Questions);
            Assert.Equal("Think about what the OS does every time a timer interrupt fires.", q.Hint);
            Assert.Equal("CausalPerturbation", q.Archetype);
        }
        finally
        {
            Directory.Delete(tempBasin, recursive: true);
        }
    }

    [Fact]
    public async Task ImportCanonicalizer_StripsOptionPrefixesResolvesLetterAnswersAndDeduplicates()
    {
        var rawJson = """
            {
              "quizTitle": "Canonicalizer Test",
              "courseTerm": "Midterms",
              "questions": [
                {
                  "kind": "MultipleChoice",
                  "cognitiveLevel": "Application",
                  "archetype": "spot-the-flaw",
                  "topicTag": "@Memory-Leak",
                  "prompt": "Which statement accurately describes a dangling pointer?",
                  "options": ["A) A pointer to unallocated stack space", "B) A pointer still referencing freed heap memory", "C) A null pointer", "D) A static global pointer"],
                  "correctAnswer": "B",
                  "explanation": "Why correct: Memory was freed. Why distractors fail: Null is safe. Core principle: Nullify freed pointers.",
                  "evidence": { "pageNumber": 1, "sectionTitle": "Pointers", "verbatimQuote": "A dangling pointer references freed memory." }
                },
                {
                  "kind": "MultipleChoice",
                  "cognitiveLevel": "Application",
                  "prompt": "Which statement accurately describes a dangling pointer?",
                  "options": ["A) A pointer to unallocated stack space", "B) A pointer still referencing freed heap memory", "C) A null pointer", "D) A static global pointer"],
                  "correctAnswer": "B",
                  "explanation": "Duplicate question should be dropped before grounding.",
                  "evidence": { "pageNumber": 1, "sectionTitle": "Pointers", "verbatimQuote": "A dangling pointer references freed memory." }
                }
              ]
            }
            """;

        var svc = new QuizLlmService();
        var session = await svc.ImportQuizFromJsonAsync(
            new QuizGenerationRequest { CourseTitle = "C Programming" },
            rawJson
        );

        var q = Assert.Single(session.Questions);
        Assert.Equal("A pointer still referencing freed heap memory", q.CorrectAnswer);
        Assert.Equal("A pointer to unallocated stack space", q.Options[0]);
        Assert.Equal("SpotTheFlaw", q.Archetype);
        Assert.Equal("memory-leak", q.TopicTag);
    }

    [Fact]
    public void DeckComprehensiveFixJson_HandlesFlexibleSchemasMarkdownFencesHintArchetypeAndRejectsEmpty()
    {
        var deck = new QuizSessionRecord
        {
            Id = "deck-1",
            Title = "OS Deck",
            Questions =
            [
                new QuizQuestionRecord
                {
                    Id = "q-alpha",
                    Prompt = "Old broken prompt",
                    Options = ["A", "B", "All of the above", "D"],
                    CorrectAnswer = "A",
                    Explanation = "Short",
                },
                new QuizQuestionRecord
                {
                    Id = "q-beta",
                    Prompt = "Second old prompt",
                    Options = ["X", "Y", "Z", "W"],
                    CorrectAnswer = "X",
                    Explanation = "Short",
                },
            ],
        };

        // 1. Markdown-fenced JSON using "questions" + "id" + positional fallback + Hint/Archetype
        var fencedJson = """
            Here are the repaired items:
            ```json
            {
              "questions": [
                {
                  "id": "q-alpha",
                  "kind": "MultipleChoice",
                  "cognitiveLevel": "Analysis",
                  "archetype": "SpotTheFlaw",
                  "prompt": "Updated prompt 1",
                  "options": ["A) Balanced opt 1", "B) Balanced opt 2", "C) Balanced opt 3", "D) Balanced opt 4"],
                  "correctAnswer": "C",
                  "hint": "Look at the scheduling invariant.",
                  "explanation": "Why correct: Opt 3 holds. Why distractors fail: Others violate it. Core principle: Check invariants."
                },
                {
                  "kind": "MultipleChoice",
                  "cognitiveLevel": "Application",
                  "prompt": "Updated prompt 2 via positional fallback",
                  "options": ["Opt 1", "Opt 2", "Opt 3", "Opt 4"],
                  "correctAnswer": "Opt 2",
                  "hint": "Recall page faults.",
                  "explanation": "Why correct: Opt 2."
                }
              ]
            }
            ```
            Note: {fixed 2 questions}
            """;

        var (updated, added) = QuizExportService.ApplyDeckComprehensiveFixJson(
            deck,
            fencedJson,
            fallbackTargetIds: ["q-alpha", "q-beta"]
        );

        Assert.Equal(2, updated);
        Assert.Equal(0, added);
        Assert.Equal("Updated prompt 1", deck.Questions[0].Prompt);
        Assert.Equal("Balanced opt 3", deck.Questions[0].CorrectAnswer);
        Assert.Equal("Look at the scheduling invariant.", deck.Questions[0].Hint);
        Assert.Equal("SpotTheFlaw", deck.Questions[0].Archetype);
        Assert.Equal("Updated prompt 2 via positional fallback", deck.Questions[1].Prompt);

        // 2. Rejects JSON that matches 0 questions instead of silently returning (0, 0)
        Assert.Throws<InvalidOperationException>(() =>
            QuizExportService.ApplyDeckComprehensiveFixJson(
                deck,
                """{ "updatedQuestions": [ { "targetId": "nonexistent-id", "prompt": "No match" } ] }"""
            )
        );
    }
}
