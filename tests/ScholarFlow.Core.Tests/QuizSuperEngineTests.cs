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

    [Fact]
    public void PrintablePdfExport_RendersFullAssessmentBreadthHintsContrastiveFeedbackAndLineCitations()
    {
        var deck = new QuizSessionRecord
        {
            Id = "print-test-1",
            CourseTitle = "Operating Systems",
            Term = "Midterms",
            Title = "OS Full-Breadth Assessment",
            Questions =
            [
                new QuizQuestionRecord
                {
                    Id = "q-mcq",
                    Kind = QuizQuestionKind.MultipleChoice,
                    CognitiveLevel = CognitiveLevel.Analysis,
                    Difficulty = 4,
                    Archetype = "SpotTheFlaw",
                    TopicTag = "deadlock",
                    Prompt = "Which condition is broken by imposing a global lock ordering?",
                    CodeSnippet = "lock(A);\nlock(B);\n// critical section\nunlock(B);\nunlock(A);",
                    CodeLanguage = "c",
                    Options = ["Mutual exclusion", "Hold and wait", "No preemption", "Circular wait"],
                    CorrectAnswer = "Circular wait",
                    Hint = "Think about whether a cycle can form in the resource-allocation graph.",
                    Explanation =
                        "Why Correct: Strict total ordering prevents any cycle of lock waits. Why Distractors Fail: Locks are still exclusive and held while requesting the next lock. Core Principle: Breaking circular wait guarantees deadlock freedom.",
                    SourcePdfPath = "/tmp/locks.c",
                    SourceStartLine = 12,
                    SourceEndLine = 18,
                    SectionTitle = "acquire_ordered",
                    VerbatimQuote = "lock(A); lock(B);",
                },
                new QuizQuestionRecord
                {
                    Id = "q-ord",
                    Kind = QuizQuestionKind.Ordering,
                    CognitiveLevel = CognitiveLevel.Application,
                    Difficulty = 3,
                    TopicTag = "paging",
                    Prompt = "Order the hardware steps during a TLB miss.",
                    Options = ["Lookup TLB", "Walk page table", "Update TLB entry", "Access physical frame"],
                    CorrectAnswer = "Lookup TLB -> Walk page table -> Update TLB entry -> Access physical frame",
                    MathLatex = @"EMAT = \alpha(t_{TLB} + t_m) + (1 - \alpha)(t_{TLB} + 2t_m)",
                    Diagram = "[VA] --> [TLB?] --miss--> [Page Table] --> [PA]",
                    Explanation = "Why Correct: Page table walk populates the TLB before physical memory access.",
                    SourcePdfPath = "/tmp/OS_Master.pdf",
                    SourcePage = 42,
                    SectionTitle = "Virtual Memory",
                    VerbatimQuote = "On a TLB miss, the MMU walks the page table.",
                },
            ],
        };

        // 1. Verify deterministic Ordering scramble does not leak exact answer order
        var scrambled = QuizPdfPrintRenderer.ScrambleOrderingStepsDeterministically(
            deck.Questions[1].Options,
            deck.Questions[1].Id
        );
        Assert.Equal(4, scrambled.Count);
        Assert.False(scrambled.SequenceEqual(deck.Questions[1].Options));

        // 2. Verify code line-range vs PDF page evidence formatting
        var codeCitation = QuizPdfPrintRenderer.FormatSourceCitation(deck.Questions[0]);
        Assert.Contains("locks.c", codeCitation);
        Assert.Contains("L12–L18", codeCitation);
        Assert.DoesNotContain("p. 1", codeCitation);

        var pdfCitation = QuizPdfPrintRenderer.FormatSourceCitation(deck.Questions[1]);
        Assert.Contains("OS_Master.pdf", pdfCitation);
        Assert.Contains("p. 42", pdfCitation);

        // 2b. Verify PdfDiagramGraphRenderer parses inline and Mermaid graphs into vector nodes & labeled edges
        Assert.True(
            PdfDiagramGraphRenderer.TryParseGraph(
                deck.Questions[1].Diagram,
                out var parsedFlow
            )
        );
        Assert.Equal(4, parsedFlow.Nodes.Count);
        Assert.Equal(3, parsedFlow.Edges.Count);
        Assert.Contains(parsedFlow.Edges, e => e.Label == "miss");

        Assert.True(
            PdfDiagramGraphRenderer.TryParseGraph(
                "graph LR\n  A[Fetch] --> B{Branch?}\n  B -->|Yes| C(Target)",
                out var mermaidGraph
            )
        );
        Assert.Equal(3, mermaidGraph.Nodes.Count);
        Assert.Contains(mermaidGraph.Nodes, n => n.Label == "Branch?" && n.Shape == DiagramNodeShape.Diamond);
        Assert.Contains(mermaidGraph.Nodes, n => n.Label == "Target" && n.Shape == DiagramNodeShape.RoundedPill);
        Assert.Contains(mermaidGraph.Edges, e => e.Label == "Yes");

        // 2b-ii. Verify Mermaid undirected "---" links and multi-hop chained edges on a single line
        var busDiagram = """
            flowchart LR
                CPU["CPU"] --- SB["System Bus"] --- BR["???"]
                BR --- MB["Memory Bus"] --- RAM["Main Memory"]
                BR --- IOB["I/O Bus"] --- IO["I/O Devices"]
            """;
        Assert.True(PdfDiagramGraphRenderer.TryParseGraph(busDiagram, out var parsedBus));
        Assert.Equal(7, parsedBus.Nodes.Count);
        Assert.Equal(6, parsedBus.Edges.Count);
        Assert.Contains(parsedBus.Nodes, n => n.Id == "BR" && n.IsCloze);
        Assert.All(parsedBus.Edges, e => Assert.False(e.IsDirected));

        // 2c. Verify sequenceDiagram is parsed as a Sequence Diagram (not flattened into a flowchart)
        var seqSpec = """
            sequenceDiagram
              participant C as Client
              participant S as Server
              C ->> S: SYN (seq=x)
              S -->> C: SYN-ACK (seq=y, ack=x+1)
              C ->> S: ACK (ack=y+1)
            """;
        Assert.True(PdfDiagramGraphRenderer.TryParseSequenceDiagram(seqSpec, out var parsedSeq));
        Assert.False(PdfDiagramGraphRenderer.TryParseGraph(seqSpec, out _));
        Assert.Equal(2, parsedSeq.Participants.Count);
        Assert.Equal("Client", parsedSeq.Participants[0].Label);
        Assert.Equal("Server", parsedSeq.Participants[1].Label);
        Assert.Equal(3, parsedSeq.Steps.Count);
        Assert.True(parsedSeq.Steps[1].IsDashed);

        // 3. Verify Exam Worksheet PDF (Questions + Hints + Answer Key) and Study Guide Inline PDF compile cleanly
        var tempDir = Path.Combine(Path.GetTempPath(), "ScholarFlow_PrintTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var examPath = Path.Combine(tempDir, "exam.pdf");
            var guidePath = Path.Combine(tempDir, "guide.pdf");

            QuizExportService.ExportDeckToPrintablePdf(deck, examPath, QuizPdfExportOptions.ExamDefault);
            QuizExportService.ExportDeckToPrintablePdf(deck, guidePath, QuizPdfExportOptions.StudyGuide);

            Assert.True(File.Exists(examPath));
            Assert.True(File.Exists(guidePath));

            using var examPdf = UglyToad.PdfPig.PdfDocument.Open(examPath);
            // Questions + Progressive Hints + Answer Key = at least 3 pages
            Assert.True(examPdf.NumberOfPages >= 3);
            var fullExamText = string.Join("\n", Enumerable.Range(1, examPdf.NumberOfPages).Select(p => examPdf.GetPage(p).Text));
            Assert.Contains("ASSESSMENT BLUEPRINT", fullExamText);
            Assert.Contains("QUICK-GRADING KEY", fullExamText);
            Assert.Contains("Figure 2.1", fullExamText);
            Assert.Contains("Progressive Hints", fullExamText);
            Assert.Contains("WHY CORRECT:", fullExamText);
            Assert.Contains("CORE PRINCIPLE:", fullExamText);
            Assert.Contains("L12–L18", fullExamText);

            using var guidePdf = UglyToad.PdfPig.PdfDocument.Open(guidePath);
            Assert.True(guidePdf.NumberOfPages >= 1);
            var guideText = string.Join("\n", Enumerable.Range(1, guidePdf.NumberOfPages).Select(p => guidePdf.GetPage(p).Text));
            Assert.Contains("Annotated Study Guide", guideText);
            Assert.Contains("WHY DISTRACTORS FAIL:", guideText);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup lock
            }
        }
    }

    [Fact]
    public void ModalityDetector_DetectsMathTextbookProseMultiSelectFlagsAntiOverfittingAndLazyHints()
    {
        // 1. Math textbook extracted via PdfPig (where special symbols are stripped into theorem/derivative/limit prose)
        var mathPages = new List<GroundedPdfPage>
        {
            new(
                PageNumber: 14,
                SectionBreadcrumb: "Chapter 3: Differentiation and the Mean Value Theorem",
                Term: null,
                Text: """
                Theorem 3.1 (Rolle's Theorem). Let f(x) = x^3 - 3x be continuous on [a, b] and differentiable on (a, b).
                Proof. By the limit definition of the derivative, dy/dx = lim_{h->0} (f(x+h) - f(x))/h.
                Corollary 3.2. If the determinant det(A) != 0, the matrix is invertible and its eigenvalues are non-zero.
                """
            ),
        };

        var autoMathProfile = QuizLlmService.DetectContentModalities(
            mathPages,
            courseTitle: "Handout 03",
            mode: QuizContentModalityMode.AutoDetect
        );
        Assert.True(autoMathProfile.HasMath);
        Assert.True(autoMathProfile.DetectedMathInSource);
        Assert.False(autoMathProfile.HasCode);

        // 2. Multi-select modality flags (Code + MathLatex + Diagrams simultaneously) on a prose page
        var prosePages = new List<GroundedPdfPage>
        {
            new(
                PageNumber: 1,
                SectionBreadcrumb: "Unit 1: History of Software Engineering Ethics",
                Term: null,
                Text: "Software engineers shall act consistently with the public interest and maintain professional integrity in all client deliverables."
            ),
        };

        var multiSelectProfile = QuizLlmService.DetectContentModalities(
            prosePages,
            courseTitle: "Professional Ethics",
            mode: QuizContentModalityMode.AutoDetect,
            selectedFlags: QuizContentModalityFlags.Code | QuizContentModalityFlags.MathLatex
        );
        Assert.True(multiSelectProfile.HasCode);
        Assert.True(multiSelectProfile.HasMath);
        Assert.False(multiSelectProfile.DetectedCodeInSource);
        Assert.False(multiSelectProfile.DetectedMathInSource);

        // 3. Verify anti-overfitting prompt clause and closed-book hint rules when user enables Math/Code on prose
        var tempPdfDir = Path.Combine(
            Path.GetTempPath(),
            "ScholarFlow_ModalityTest_" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(tempPdfDir);
        try
        {
            var mdFile = Path.Combine(tempPdfDir, "calculus_notes.md");
            File.WriteAllText(
                mdFile,
                "# Fundamental Theorem of Calculus\nTheorem 1: If $f(x) = \\int_a^x g(t) dt$, then $dy/dx = g(x)$."
            );

            var fileReq = new QuizGenerationRequest
            {
                CourseTitle = "Notes",
                SourceFilePaths = [mdFile],
                ModalityMode = QuizContentModalityMode.AutoDetect,
            };
            var fileModality = QuizLlmService.DetectFileSourceModalities(fileReq);
            Assert.True(fileModality.HasMath);
            Assert.True(fileModality.DetectedMathInSource);

            var proseFile = Path.Combine(tempPdfDir, "ethics.txt");
            File.WriteAllText(
                proseFile,
                "Professional ethics requires transparency, accountability, and informed consent across organizational stakeholders."
            );
            var overfitReq = new QuizGenerationRequest
            {
                CourseTitle = "Ethics",
                SourceFilePaths = [proseFile],
                ModalityMode = QuizContentModalityMode.AutoDetect,
                SelectedModalityFlags =
                    QuizContentModalityFlags.MathLatex | QuizContentModalityFlags.Code,
                AvailableTerms = [],
            };
            var svc = new QuizLlmService();
            var prompt = svc.BuildFileSourcePromptForManualCopy(overfitReq, 40_000, out _);

            Assert.Contains("ANTI-OVERFITTING GUARDRAIL", prompt);
            Assert.Contains("NEVER invent artificial equations", prompt);
            Assert.Contains("NEVER cite or point to document locations such as \"See Section X\"", prompt);
            Assert.Contains("\"courseTerm\": null", prompt);
        }
        finally
        {
            try
            {
                Directory.Delete(tempPdfDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup error
            }
        }

        // 4. Verify QuizQualityCritic flags lazy section/figure/page meta-hints AND dangling figure references in prompts
        var lazyHintQuestion = new QuizQuestionRecord
        {
            Kind = QuizQuestionKind.MultipleChoice,
            Prompt = "In Figure 3.2, what is the time complexity of binary search on a sorted array?",
            Options = ["O(log n)", "O(n)", "O(n log n)", "O(1)"],
            CorrectAnswer = "O(log n)",
            Hint = "See Section 2.1 and Figure 4 in the handout for the tree depth.",
            Explanation = "Why correct: Halving the search space takes log2(n) steps. Why distractors fail: O(n) is linear scan. Core principle: Divide and conquer.",
            VerbatimQuote = "Binary search runs in O(log n) logarithmic time.",
            HighlightRects = [new NormalizedHighlightRect(0.1, 0.1, 0.5, 0.05)],
        };
        var report = QuizQualityCritic.Audit([lazyHintQuestion]);
        Assert.Contains(report.Issues, i => i.Code == "LAZY_META_HINT");
        Assert.Contains(report.Issues, i => i.Code == "DANGLING_SOURCE_REF");
    }

    [Fact]
    public async Task SourceFigureAutoExtraction_DiscoversFiguresFormatsPromptAndMaterializesCroppedImagePath()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "ScholarFlow_FigExtractTest_" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(tempDir);

        try
        {
            // Register PDFtoImage / SkiaSharp region rasterizer (same as Desktop host)
            PdfQuizGrounder.CustomPageRegionRasterizer = (
                pdfFile,
                oneBasedPage,
                region,
                outPng,
                dpi
            ) =>
            {
                var pdfBytes = File.ReadAllBytes(pdfFile);
#pragma warning disable CA1416
                using var pageBmp = PDFtoImage.Conversion.ToImage(
                    pdfBytes,
                    page: (Index)Math.Max(0, oneBasedPage - 1),
                    options: new PDFtoImage.RenderOptions(
                        Dpi: dpi,
                        BackgroundColor: SkiaSharp.SKColors.White
                    )
                );
#pragma warning restore CA1416
                int x = Math.Clamp(
                    (int)Math.Floor(region.X * pageBmp.Width),
                    0,
                    Math.Max(0, pageBmp.Width - 2)
                );
                int y = Math.Clamp(
                    (int)Math.Floor(region.Y * pageBmp.Height),
                    0,
                    Math.Max(0, pageBmp.Height - 2)
                );
                int w = Math.Clamp(
                    (int)Math.Ceiling(region.Width * pageBmp.Width),
                    2,
                    pageBmp.Width - x
                );
                int h = Math.Clamp(
                    (int)Math.Ceiling(region.Height * pageBmp.Height),
                    2,
                    pageBmp.Height - y
                );
                using var subset = new SkiaSharp.SKBitmap(w, h);
                using (var canvas = new SkiaSharp.SKCanvas(subset))
                {
                    canvas.DrawBitmap(
                        pageBmp,
                        new SkiaSharp.SKRectI(x, y, x + w, y + h),
                        new SkiaSharp.SKRect(0, 0, w, h)
                    );
                }
                Directory.CreateDirectory(Path.GetDirectoryName(outPng)!);
                using var img = SkiaSharp.SKImage.FromBitmap(subset);
                using var enc = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
                using var fs = File.Create(outPng);
                enc.SaveTo(fs);
            };

            // 1. Build a test PDF page containing a vector plot/diagram region and a "Figure 3.2: ..." caption
            var pdfPath = Path.Combine(tempDir, "Computer_Arch_Plots.pdf");
            ScholarFlow.Core.Pdf.MasterPdfService.EnsureFontResolver();
            using (var doc = new PdfSharpCore.Pdf.PdfDocument())
            {
                var page = doc.AddPage();
                page.Width = 612;
                page.Height = 792;
                using var gfx = PdfSharpCore.Drawing.XGraphics.FromPdfPage(page);
                var font = new PdfSharpCore.Drawing.XFont("Sans", 11);

                gfx.DrawString(
                    "In modern superscalar processors, cache hierarchy latency scales non-linearly across L1, L2, and L3.",
                    font,
                    PdfSharpCore.Drawing.XBrushes.Black,
                    new PdfSharpCore.Drawing.XPoint(54, 90)
                );
                gfx.DrawString(
                    "When working set size exceeds the L2 capacity boundary, miss penalties dominate execution cycles.",
                    font,
                    PdfSharpCore.Drawing.XBrushes.Black,
                    new PdfSharpCore.Drawing.XPoint(54, 110)
                );

                // Vector plot drawing between Y=140 and Y=310
                gfx.DrawRectangle(
                    PdfSharpCore.Drawing.XPens.DarkBlue,
                    PdfSharpCore.Drawing.XBrushes.AliceBlue,
                    72,
                    145,
                    440,
                    155
                );
                gfx.DrawLine(PdfSharpCore.Drawing.XPens.Red, 90, 280, 480, 165);

                // Caption right below the vector plot
                gfx.DrawString(
                    "Figure 3.2: Working Set Size vs. Memory Hierarchy Access Latency Curve",
                    font,
                    PdfSharpCore.Drawing.XBrushes.Black,
                    new PdfSharpCore.Drawing.XPoint(72, 325)
                );

                gfx.DrawString(
                    "As a consequence, spatial locality optimizations such as stride prefetching reduce compulsory misses.",
                    font,
                    PdfSharpCore.Drawing.XBrushes.Black,
                    new PdfSharpCore.Drawing.XPoint(54, 370)
                );

                doc.Save(pdfPath);
            }

            // 2. Verify ExtractGroundedPages discovers p1_fig1 and FormatPagesForPrompt lists it
            var pages = PdfQuizGrounder.ExtractGroundedPages(pdfPath);
            Assert.Single(pages);
            Assert.NotNull(pages[0].Figures);
            Assert.NotEmpty(pages[0].Figures!);
            var fig = pages[0].Figures![0];
            Assert.Equal("p1_fig1", fig.FigureId);
            Assert.Contains("Figure 3.2", fig.Caption);

            var formatted = PdfQuizGrounder.FormatPagesForPrompt(pages);
            Assert.Contains("[EXTRACTABLE SOURCE FIGURES ON PAGE 1:", formatted);
            Assert.Contains("id=\"p1_fig1\"", formatted);

            // 3. Verify ImportQuizFromJsonAsync materializes q.ImagePath when sourceFigureId is returned
            var svc = new QuizLlmService();
            var req = new QuizGenerationRequest
            {
                BasinRoot = tempDir,
                CourseFolder = "Arch",
                CourseTitle = "Computer Architecture",
                SourcePdfPath = pdfPath,
            };

            var sampleJson = """
                {
                  "quizTitle": "Cache Hierarchy Assessment",
                  "courseTerm": null,
                  "keyConceptsCovered": ["Cache Latency"],
                  "questions": [
                    {
                      "kind": "MultipleChoice",
                      "cognitiveLevel": "Analysis",
                      "archetype": "DualCoding",
                      "difficulty": 4,
                      "topicTag": "cache-latency",
                      "prompt": "Based on the working-set latency curve, what happens when the working set exceeds the L2 capacity boundary?",
                      "options": [
                        "Miss penalties dominate total execution cycles",
                        "Latency remains constant regardless of working set size",
                        "L1 cache automatically expands its associativity",
                        "TLB translations bypass main memory completely"
                      ],
                      "correctAnswer": "Miss penalties dominate total execution cycles",
                      "hint": "Consider the transition from on-chip SRAM to off-chip DRAM access latency.",
                      "explanation": "Why correct: Crossing L2 capacity triggers steep L3/DRAM miss penalties. Why distractors fail: Hardware cache capacity is fixed. Core principle: Memory wall hierarchy.",
                      "sourceFigureId": "p1_fig1",
                      "evidence": {
                        "pageNumber": 1,
                        "sectionTitle": "Cache Hierarchy",
                        "verbatimQuote": "When working set size exceeds the L2 capacity boundary, miss penalties dominate execution cycles."
                      }
                    }
                  ]
                }
                """;

            var session = await svc.ImportQuizFromJsonAsync(req, sampleJson);
            Assert.Single(session.Questions);
            var q = session.Questions[0];
            Assert.False(string.IsNullOrWhiteSpace(q.ImagePath));
            var fullCroppedPath = Path.Combine(tempDir, q.ImagePath!);
            Assert.True(File.Exists(fullCroppedPath));
            Assert.True(new FileInfo(fullCroppedPath).Length > 200);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup lock
            }
        }
    }

    [Fact]
    public void LatexMathFormatter_FormatsAmsSymbolsWithoutPrefixCollisionsAndGradesNumericEquivalence()
    {
        // 1. User-reported divisibility proof with \text and \implies
        var proofLatex = @"b = ha \text{ and } a = kb \implies b = (hk)b \implies hk = 1";
        var formatted = LatexMathFormatter.FormatInlineMath(proofLatex);
        Assert.Equal("b = ha and a = kb ⇒ b = (hk)b ⇒ hk = 1", formatted);

        // 2. Prefix-collision regression checks (\in must NOT corrupt \int; \le must NOT corrupt \leftarrow or \left)
        var collisionLatex = @"\int_0^1 x^2 \, dx \leftarrow \le \in \mathbb{Z} \iff a \mid b \pmod{m}";
        var formattedCollision = LatexMathFormatter.FormatInlineMath(collisionLatex);
        Assert.Contains("∫₀¹", formattedCollision);
        Assert.Contains("x²", formattedCollision);
        Assert.Contains("←", formattedCollision);
        Assert.Contains("≤", formattedCollision);
        Assert.Contains("∈", formattedCollision);
        Assert.Contains("ℤ", formattedCollision);
        Assert.Contains("⇔", formattedCollision);
        Assert.Contains("∣", formattedCollision);
        Assert.Contains("(mod m)", formattedCollision);
        Assert.DoesNotContain("∈t", formattedCollision);
        Assert.DoesNotContain("≤ft", formattedCollision);

        // 3. Direct calculated answer equivalence in QuizQuestionRecord.EvaluateAnswer
        var calcQ = new QuizQuestionRecord
        {
            Kind = QuizQuestionKind.Identification,
            Prompt = "Evaluate the definite integral of x from 0 to 1.",
            CorrectAnswer = "1/2",
        };
        Assert.True(calcQ.EvaluateAnswer("0.5"));
        Assert.True(calcQ.EvaluateAnswer(".5"));
        Assert.True(calcQ.EvaluateAnswer("1 / 2"));
        Assert.True(calcQ.EvaluateAnswer("0.50"));
        Assert.False(calcQ.EvaluateAnswer("0.25"));

        var exprQ = new QuizQuestionRecord
        {
            Kind = QuizQuestionKind.FillInTheBlank,
            Prompt = "If b = ha and a = kb, then _____.",
            CorrectAnswer = "hk = 1",
        };
        Assert.True(exprQ.EvaluateAnswer("hk=1"));
        Assert.True(exprQ.EvaluateAnswer("hk = 1"));

        // 4. QuizQualityCritic flags raw LaTeX in free-text Identification/FillInTheBlank answers, but allows plain scalars
        var rawLatexTextQ = new QuizQuestionRecord
        {
            Kind = QuizQuestionKind.Identification,
            Prompt = "Write the quadratic formula.",
            CorrectAnswer = @"\frac{-b \pm \sqrt{b^2 - 4ac}}{2a}",
            Explanation = "Why correct: Standard quadratic formula. Why distractors fail: N/A. Core principle: Completing the square.",
            VerbatimQuote = "The quadratic formula solves ax^2 + bx + c = 0.",
            HighlightRects = [new NormalizedHighlightRect(0.1, 0.1, 0.5, 0.05)],
        };
        var badReport = QuizQualityCritic.Audit([rawLatexTextQ]);
        Assert.Contains(badReport.Issues, i => i.Code == "COMPLEX_LATEX_TEXT_ANSWER");

        var scalarQ = new QuizQuestionRecord
        {
            Kind = QuizQuestionKind.Identification,
            Prompt = "What is the value of hk?",
            CorrectAnswer = "1",
            Explanation = "Why correct: Canceling nonzero b gives hk = 1. Why distractors fail: N/A. Core principle: Divisibility.",
            VerbatimQuote = "Divisibility in both directions yields hk = 1.",
            HighlightRects = [new NormalizedHighlightRect(0.1, 0.1, 0.5, 0.05)],
        };
        var okReport = QuizQualityCritic.Audit([scalarQ]);
        Assert.DoesNotContain(okReport.Issues, i => i.Code == "COMPLEX_LATEX_TEXT_ANSWER");
    }
}
