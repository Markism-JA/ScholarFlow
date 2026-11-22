using System.Text;
using System.Text.Json;
using ScholarFlow.Core.Database;
using ScholarFlow.Core.Knowledge;
using ScholarFlow.Core.Models;

namespace ScholarFlow.Core.Quiz;

/// <summary>
/// Unified OpenAI-compatible AI Quiz Generator (supports Google AI Studio Gemini, Local Ollama, OpenRouter/OpenAI,
/// Manual Gemini Web JSON Import, and Offline Smart PDF Heuristic Generation) with deterministic PdfPig highlight grounding.
/// </summary>
public sealed partial class QuizLlmService
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(3) };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string BuildPromptForManualCopy(
        QuizGenerationRequest request,
        out IReadOnlyList<GroundedPdfPage> extractedPages
    ) => BuildPromptForManualCopy(request, 160_000, out extractedPages, out _);

    public string BuildPromptForManualCopy(
        QuizGenerationRequest request,
        int maxChars,
        out IReadOnlyList<GroundedPdfPage> extractedPages,
        out int includedPageCount
    )
    {
        extractedPages = PdfQuizGrounder.ExtractGroundedPages(
            request.SourcePdfPath,
            request.Term,
            request.SectionFilter,
            request.StartPage,
            request.EndPage,
            request.SelectedRanges,
            maxPages: 250
        );

        var groundedContent = PdfQuizGrounder.FormatPagesForPrompt(
            extractedPages,
            maxChars: maxChars,
            out includedPageCount
        );
        var weakTopics = ScholarFlowDatabase.GetWeakTopicTags(
            request.BasinRoot,
            request.CourseFolder
        );
        var modality = DetectContentModalities(
            extractedPages,
            request.CourseTitle,
            request.ModalityMode
        );
        int count = Math.Clamp(request.QuestionCount, 1, 150);
        return BuildSystemInstructions(request, weakTopics, modality)
            + "\n\n"
            + BuildUserPrompt(request, groundedContent, count, modality: modality);
    }

    public string BuildInstructionPromptForUploadedPdf(
        QuizGenerationRequest request,
        IReadOnlyList<(int OriginalPage, string SectionBreadcrumb)> pageManifest
    )
    {
        var weakTopics = ScholarFlowDatabase.GetWeakTopicTags(
            request.BasinRoot,
            request.CourseFolder
        );
        var modality = new ContentModalityProfile(HasCode: true, HasMath: true, HasDiagrams: true);
        int count = Math.Clamp(request.QuestionCount, 1, 150);

        var sb = new StringBuilder();
        sb.AppendLine(BuildSystemInstructions(request, weakTopics, modality));
        sb.AppendLine();
        sb.AppendLine("ATTACHED SCOPED PDF PAGE MAPPING:");
        sb.AppendLine(
            "Use the ORIGINAL page number below for \"evidence.pageNumber\" so highlights align with the source textbook:"
        );
        for (int i = 0; i < pageManifest.Count; i++)
        {
            var (origPage, section) = pageManifest[i];
            sb.AppendLine($"- Uploaded PDF Page {i + 1} = [PAGE {origPage}] | Section: {section}");
        }
        sb.AppendLine();
        sb.AppendLine(
            $"Generate {count} grounded questions from the attached PDF strictly following the JSON schema above."
        );
        return sb.ToString();
    }

    public Task<string> ExecuteRawJsonPromptAsync(
        string providerBaseUrl,
        string? apiKey,
        string modelName,
        string systemPrompt,
        string userPrompt,
        CancellationToken ct = default
    )
    {
        var effectiveKey = !string.IsNullOrWhiteSpace(apiKey)
            ? apiKey.Trim()
            : Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        return CallOpenAiCompatibleChatAsync(
            providerBaseUrl,
            effectiveKey,
            modelName,
            systemPrompt,
            userPrompt,
            ct
        );
    }

    public async Task<QuizSessionRecord> GenerateQuizAsync(
        QuizGenerationRequest request,
        CancellationToken ct = default
    )
    {
        if (request.HasFileSources)
            return await GenerateFromFileSourcesAsync(request, ct);

        var pages = await Task.Run(
            () =>
                PdfQuizGrounder.ExtractGroundedPages(
                    request.SourcePdfPath,
                    request.Term,
                    request.SectionFilter,
                    request.StartPage,
                    request.EndPage,
                    request.SelectedRanges,
                    maxPages: 250
                ),
            ct
        );

        if (pages.Count == 0)
            throw new InvalidOperationException(
                "No readable text pages were found in the selected PDF scope."
            );

        int desiredCount = Math.Clamp(request.QuestionCount, 1, 150);

        var effectiveKey = !string.IsNullOrWhiteSpace(request.ApiKey)
            ? request.ApiKey.Trim()
            : Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        bool isLocalEndpoint =
            request.ProviderBaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
            || request.ProviderBaseUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase);

        QuizGenerationResponseDto dto;
        string modelLabel;

        if (
            request.UseOfflineHeuristicFallback
            || (!isLocalEndpoint && string.IsNullOrWhiteSpace(effectiveKey))
        )
        {
            dto = await Task.Run(
                () => GenerateOfflineGroundedDto(request, pages, desiredCount),
                ct
            );
            modelLabel = "ScholarFlow Smart PDF Analyzer (Offline)";
        }
        else
        {
            var weakTopics = ScholarFlowDatabase.GetWeakTopicTags(
                request.BasinRoot,
                request.CourseFolder
            );
            var modality = DetectContentModalities(
                pages,
                request.CourseTitle,
                request.ModalityMode
            );
            var sysPrompt = BuildSystemInstructions(request, weakTopics, modality);

            int batchCharBudget = isLocalEndpoint ? 28_000 : 72_000;
            var packedBatches = PdfQuizGrounder.PackPagesIntoBudgets(pages, batchCharBudget);
            if (packedBatches.Count > desiredCount)
            {
                // If fewer questions than batches, sample batches evenly across the whole scope
                var sampled = new List<List<GroundedPdfPage>>(desiredCount);
                for (int i = 0; i < desiredCount; i++)
                {
                    int idx = (int)Math.Floor(i * (packedBatches.Count / (double)desiredCount));
                    sampled.Add(packedBatches[Math.Clamp(idx, 0, packedBatches.Count - 1)]);
                }
                packedBatches = sampled;
            }

            int batchCount = Math.Max(1, packedBatches.Count);
            var allQuestions = new List<GeneratedQuestionDto>();
            var allConcepts = new List<string>();
            string? firstTitle = null;
            string? firstTerm = null;

            for (int b = 0; b < batchCount; b++)
            {
                int remaining = desiredCount - allQuestions.Count;
                if (remaining <= 0)
                    break;

                int batchesLeft = batchCount - b;
                int thisBatchCount = Math.Max(
                    1,
                    (int)Math.Ceiling(remaining / (double)batchesLeft)
                );
                var batchPages = packedBatches[b];
                var groundedContent = PdfQuizGrounder.FormatPagesForPrompt(
                    batchPages,
                    maxChars: int.MaxValue,
                    out _
                );
                var usrPrompt = BuildUserPrompt(
                    request,
                    groundedContent,
                    thisBatchCount,
                    b + 1,
                    batchCount,
                    modality
                );

                var rawResponse = await CallOpenAiCompatibleChatAsync(
                    request.ProviderBaseUrl,
                    effectiveKey,
                    request.ModelName,
                    sysPrompt,
                    usrPrompt,
                    ct
                );

                var batchDto = ParseQuizResponseJson(rawResponse);
                firstTitle ??= batchDto.QuizTitle;
                firstTerm ??= batchDto.CourseTerm;
                allConcepts.AddRange(batchDto.KeyConceptsCovered);
                allQuestions.AddRange(batchDto.Questions);
            }

            dto = new QuizGenerationResponseDto
            {
                QuizTitle = firstTitle ?? string.Empty,
                CourseTerm = firstTerm ?? string.Empty,
                KeyConceptsCovered = [.. allConcepts.Distinct(StringComparer.OrdinalIgnoreCase)],
                Questions = [.. allQuestions.Take(desiredCount)],
            };
            modelLabel = request.ModelName;
        }

        return await BuildGroundAndPersistSessionAsync(request, dto, modelLabel, pages, ct);
    }

    public async Task<QuizSessionRecord> ImportQuizFromJsonAsync(
        QuizGenerationRequest request,
        string rawJson,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            throw new ArgumentException("JSON input cannot be empty.", nameof(rawJson));

        var dto = await Task.Run(() => ParseQuizResponseJson(rawJson), ct);
        return await BuildGroundAndPersistSessionAsync(
            request,
            dto,
            "Imported Web AI JSON",
            [],
            ct
        );
    }

    private static async Task<QuizSessionRecord> BuildGroundAndPersistSessionAsync(
        QuizGenerationRequest request,
        QuizGenerationResponseDto dto,
        string modelLabel,
        IReadOnlyList<GroundedPdfPage> pages,
        CancellationToken ct
    )
    {
        var inferredTerm =
            TaskItem.NormalizeTerm(dto.CourseTerm)
            ?? TaskItem.NormalizeTerm(request.Term)
            ?? pages.FirstOrDefault(p => p.Term is not null)?.Term;

        var defaultTitle = !string.IsNullOrWhiteSpace(dto.QuizTitle)
            ? dto.QuizTitle.Trim()
            : $"{request.CourseTitle} — {inferredTerm ?? "Comprehensive"} ({request.Preset})";

        var sessionSourcePath =
            request.HasPdfSource ? request.SourcePdfPath
            : request.HasFileSources
                ? request.SourceFilePaths.FirstOrDefault(p =>
                    StudySourceClassifier.Classify(p).Kind != StudySourceKind.Image
                ) ?? request.SourceFilePaths[0]
            : request.SourcePdfPath;
        var groundItems = new List<(QuizQuestionRecord, EvidenceAnchorDto?)>();

        var session = new QuizSessionRecord
        {
            CourseFolder = request.CourseFolder,
            CourseTitle = request.CourseTitle,
            Term = inferredTerm,
            Title = defaultTitle,
            SourcePdfPath = sessionSourcePath,
            SourcePdfMtimeTicks = request.HasPdfSource
                ? File.GetLastWriteTimeUtc(request.SourcePdfPath).Ticks
                : null,
            ModelUsed = modelLabel,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        var today = DateOnly.FromDateTime(DateTime.Today);
        var seenStemKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < dto.Questions.Count; i++)
        {
            var qDto = dto.Questions[i];
            if (string.IsNullOrWhiteSpace(qDto.Prompt))
                continue;

            var cleanPrompt = qDto.Prompt.Trim();
            var stemKey = CanonicalizeStemKey(cleanPrompt);
            if (!string.IsNullOrEmpty(stemKey) && !seenStemKeys.Add(stemKey))
                continue;

            var kind = Enum.TryParse<QuizQuestionKind>(
                qDto.Kind,
                ignoreCase: true,
                out var parsedKind
            )
                ? parsedKind
                : QuizQuestionKind.MultipleChoice;

            var cog = Enum.TryParse<CognitiveLevel>(
                qDto.CognitiveLevel,
                ignoreCase: true,
                out var parsedCog
            )
                ? parsedCog
                : CognitiveLevel.Comprehension;

            var (options, correctAnswer) = CanonicalizeOptionsAndAnswer(
                kind,
                qDto.Options,
                qDto.CorrectAnswer
            );

            var mathLatex = string.IsNullOrWhiteSpace(qDto.MathLatex)
                ? null
                : qDto.MathLatex.Trim();
            var codeSnippet = string.IsNullOrWhiteSpace(qDto.CodeSnippet)
                ? null
                : qDto.CodeSnippet.Trim();
            var codeLanguage = string.IsNullOrWhiteSpace(qDto.CodeLanguage)
                ? null
                : qDto.CodeLanguage.Trim();
            var diagram = string.IsNullOrWhiteSpace(qDto.Diagram) ? null : qDto.Diagram.Trim();

            // Strict Math Guardrail: symbolic/LaTeX math questions must use MultipleChoice/Situational
            // when options are available so students never have to type raw LaTeX into an Identification box.
            if (
                !string.IsNullOrWhiteSpace(mathLatex)
                && kind is QuizQuestionKind.Identification or QuizQuestionKind.FillInTheBlank
                && options.Count >= 2
            )
            {
                kind = QuizQuestionKind.MultipleChoice;
            }

            var qRecord = new QuizQuestionRecord
            {
                QuizId = session.Id,
                OrderIndex = session.Questions.Count,
                Kind = kind,
                CognitiveLevel = cog,
                Difficulty = Math.Clamp(qDto.Difficulty, 1, 5),
                TopicTag = CanonicalizeTopicTag(qDto.TopicTag),
                Prompt = cleanPrompt,
                Options = options,
                CorrectAnswer = correctAnswer,
                AcceptedAliases =
                    qDto.AcceptedAliases?.Where(a => !string.IsNullOrWhiteSpace(a))
                        .Select(a => a.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    ?? [],
                Explanation = (qDto.Explanation ?? string.Empty).Trim(),
                Hint = string.IsNullOrWhiteSpace(qDto.Hint) ? null : qDto.Hint.Trim(),
                Archetype = CanonicalizeArchetype(qDto.Archetype),
                CodeSnippet = codeSnippet,
                CodeLanguage = codeLanguage,
                MathLatex = mathLatex,
                Diagram = diagram,
                SourcePdfPath = sessionSourcePath,
                SourcePage =
                    qDto.Evidence?.PageNumber > 0
                        ? qDto.Evidence.PageNumber
                        : (pages.Count > 0 ? pages[0].PageNumber : 1),

                SectionTitle = qDto.Evidence?.SectionTitle ?? string.Empty,
                VerbatimQuote = qDto.Evidence?.VerbatimQuote ?? string.Empty,
                EvidenceAnchors =
                    (request.HasFileSources && !request.IncludePdfWithFileSources)
                        ? []
                        : qDto.Evidence?.SecondaryQuotes?.Where(sq =>
                                !string.IsNullOrWhiteSpace(sq)
                            )
                            .Select(sq => new GroundedEvidenceAnchor(
                                qDto.Evidence.PageNumber > 0 ? qDto.Evidence.PageNumber : 1,
                                qDto.Evidence.SectionTitle ?? string.Empty,
                                sq.Trim(),
                                []
                            ))
                            .ToList()
                            ?? [],
                SrsDueDate = today,
            };

            session.Questions.Add(qRecord);
            groundItems.Add((qRecord, qDto.Evidence));
        }

        if (session.Questions.Count == 0)
            throw new InvalidOperationException(
                "No valid questions were produced from the response."
            );

        // Deterministically ground each question against its matching source (PDF highlight rects or file line range / image)
        if (request.IsMixedSources)
        {
            var fileItems = new List<(QuizQuestionRecord Question, EvidenceAnchorDto? Evidence)>();
            var pdfQuestions = new List<QuizQuestionRecord>();
            foreach (var item in groundItems)
            {
                bool matchesFile =
                    FileSourceQuizSupport.TryMatchSourceFile(
                        request.SourceFilePaths,
                        item.Item2?.File
                    )
                    is not null;
                if (matchesFile)
                {
                    item.Item1.EvidenceAnchors.Clear();
                    fileItems.Add(item);
                }
                else
                {
                    pdfQuestions.Add(item.Item1);
                }
            }

            await Task.Run(
                () =>
                {
                    if (fileItems.Count > 0)
                        FileSourceQuizSupport.GroundQuestionsToFiles(
                            request.SourceFilePaths,
                            fileItems
                        );
                    if (pdfQuestions.Count > 0)
                        PdfQuizGrounder.GroundQuestionsToPdf(request.SourcePdfPath, pdfQuestions);
                },
                ct
            );
        }
        else if (request.HasFileSources)
        {
            await Task.Run(
                () =>
                    FileSourceQuizSupport.GroundQuestionsToFiles(
                        request.SourceFilePaths,
                        groundItems
                    ),
                ct
            );
        }
        else
        {
            await Task.Run(
                () =>
                    PdfQuizGrounder.GroundQuestionsToPdf(request.SourcePdfPath, session.Questions),
                ct
            );
        }

        if (!string.IsNullOrWhiteSpace(request.BasinRoot))
        {
            if (!string.IsNullOrWhiteSpace(request.AppendToQuizId))
            {
                var existingList = await ScholarFlowDatabase.LoadQuizSessionsAsync(
                    request.BasinRoot,
                    ct: ct
                );
                var existing = existingList.FirstOrDefault(x =>
                    string.Equals(x.Id, request.AppendToQuizId, StringComparison.OrdinalIgnoreCase)
                );
                if (existing is not null)
                {
                    foreach (var nq in session.Questions)
                    {
                        nq.QuizId = existing.Id;
                        nq.OrderIndex = existing.Questions.Count;
                        existing.Questions.Add(nq);
                    }
                    existing.SourcePdfMtimeTicks ??= session.SourcePdfMtimeTicks;
                    await ScholarFlowDatabase.SaveQuizSessionAsync(request.BasinRoot, existing, ct);
                    return existing;
                }
            }

            await ScholarFlowDatabase.SaveQuizSessionAsync(request.BasinRoot, session, ct);
        }

        return session;
    }

    internal static (List<string> Options, string CorrectAnswer) CanonicalizeOptionsAndAnswer(
        QuizQuestionKind kind,
        IReadOnlyList<string>? rawOptions,
        string? rawCorrectAnswer
    )
    {
        var options =
            rawOptions
                ?.Where(o => !string.IsNullOrWhiteSpace(o))
                .Select(StripLeadingOptionMarker)
                .Where(o => o.Length > 0)
                .ToList()
            ?? [];

        var correct = StripLeadingOptionMarker(rawCorrectAnswer ?? string.Empty);

        if (kind == QuizQuestionKind.TrueFalse)
        {
            options = ["True", "False"];
            if (correct.StartsWith("t", StringComparison.OrdinalIgnoreCase))
                correct = "True";
            else if (correct.StartsWith("f", StringComparison.OrdinalIgnoreCase))
                correct = "False";
            return (options, correct);
        }

        if (kind == QuizQuestionKind.Ordering)
        {
            if (string.IsNullOrWhiteSpace(correct) && options.Count >= 2)
                correct = string.Join(" -> ", options);
            return (options, correct);
        }

        if (
            kind is QuizQuestionKind.MultipleChoice or QuizQuestionKind.Situational
            && options.Count > 0
        )
        {
            // Resolve letter-only answers like "A", "B)", "Option C" directly to the matching option text
            var trimmedRaw = (rawCorrectAnswer ?? string.Empty).Trim();
            if (TryResolveOptionLetterIndex(trimmedRaw, options.Count, out int letterIdx))
            {
                correct = options[letterIdx];
            }
            else
            {
                var exactMatch = options.FirstOrDefault(o =>
                    string.Equals(o, correct, StringComparison.OrdinalIgnoreCase)
                );
                if (exactMatch is not null)
                    correct = exactMatch;
            }
        }

        return (options, correct);
    }

    private static string StripLeadingOptionMarker(string raw)
    {
        var t = raw.Trim();
        if (
            t.Length >= 3
            && char.IsLetter(t[0])
            && (t[1] is ')' or '.' or ':')
            && char.IsWhiteSpace(t[2])
        )
            return t[3..].Trim();
        if (
            t.Length >= 4
            && t[0] == '('
            && char.IsLetter(t[1])
            && t[2] == ')'
            && char.IsWhiteSpace(t[3])
        )
            return t[4..].Trim();
        return t;
    }

    private static bool TryResolveOptionLetterIndex(string raw, int optionCount, out int index)
    {
        index = -1;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var t = raw.Trim();
        if (t.StartsWith("Option ", StringComparison.OrdinalIgnoreCase))
            t = t[7..].Trim();
        t = t.Trim('(', ')', '.', ':', ' ');
        if (t.Length == 1 && char.IsLetter(t[0]))
        {
            int idx = char.ToUpperInvariant(t[0]) - 'A';
            if (idx >= 0 && idx < optionCount)
            {
                index = idx;
                return true;
            }
        }
        return false;
    }

    private static string CanonicalizeStemKey(string prompt)
    {
        var sb = new StringBuilder(prompt.Length);
        bool prevSpace = false;
        foreach (char c in prompt)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                prevSpace = false;
            }
            else if (!prevSpace && sb.Length > 0)
            {
                sb.Append(' ');
                prevSpace = true;
            }
        }
        return sb.ToString().Trim();
    }

    private static string CanonicalizeTopicTag(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "general";
        var cleaned = raw.Trim().ToLowerInvariant().TrimStart('@', '#');
        return string.IsNullOrWhiteSpace(cleaned) ? "general" : cleaned;
    }

    private static string? CanonicalizeArchetype(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var clean = raw.Trim();
        if (
            Enum.TryParse<QuestionArchetype>(
                clean.Replace("-", "").Replace(" ", ""),
                ignoreCase: true,
                out var parsed
            )
        )
            return parsed.ToString();
        return clean;
    }
}
