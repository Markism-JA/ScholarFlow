using System.Text;
using System.Text.Json;

namespace ScholarFlow.Core.Quiz;

/// <summary>
/// Handles portable JSON export/import, printable PDF generation (via PdfSharpCore),
/// and point-to-point single-question AI repair prompts for quiz decks.
/// </summary>
public static class QuizExportService
{
    private static readonly JsonSerializerOptions PrettyJsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string ExportDeckToJson(QuizSessionRecord quiz) =>
        JsonSerializer.Serialize(quiz, PrettyJsonOpts);

    public static QuizSessionRecord ImportDeckFromJson(
        string rawJson,
        string targetCourseFolder,
        string targetCourseTitle,
        string? sourcePdfPathOverride = null
    )
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            throw new ArgumentException("JSON content is empty.", nameof(rawJson));

        var cleaned = ExtractJsonPayload(rawJson);

        // 1. Try full QuizSessionRecord format first
        try
        {
            var imported = JsonSerializer.Deserialize<QuizSessionRecord>(cleaned, PrettyJsonOpts);
            if (imported is not null && imported.Questions.Count > 0)
            {
                var newQuizId = Guid.NewGuid().ToString("N")[..10];
                imported.Id = newQuizId;
                imported.CourseFolder = targetCourseFolder;
                imported.CourseTitle = targetCourseTitle;
                if (!string.IsNullOrWhiteSpace(sourcePdfPathOverride))
                    imported.SourcePdfPath = sourcePdfPathOverride;
                imported.InProgressQuestionIndex = null;
                imported.InProgressAnswers.Clear();

                for (int i = 0; i < imported.Questions.Count; i++)
                {
                    var q = imported.Questions[i];
                    q.Id = Guid.NewGuid().ToString("N")[..10];
                    q.QuizId = newQuizId;
                    q.OrderIndex = i;
                    if (!string.IsNullOrWhiteSpace(sourcePdfPathOverride))
                        q.SourcePdfPath = sourcePdfPathOverride;
                }

                if (
                    !string.IsNullOrWhiteSpace(imported.SourcePdfPath)
                    && File.Exists(imported.SourcePdfPath)
                )
                {
                    var ungrounded = imported
                        .Questions.Where(q => q.HighlightRects.Count == 0)
                        .ToList();
                    if (ungrounded.Count > 0)
                        PdfQuizGrounder.GroundQuestionsToPdf(imported.SourcePdfPath, ungrounded);
                }

                return imported;
            }
        }
        catch
        {
            // Fall through to QuizGenerationResponseDto
        }

        // 2. Fallback to AI QuizGenerationResponseDto format
        var dto =
            JsonSerializer.Deserialize<QuizGenerationResponseDto>(cleaned, PrettyJsonOpts)
            ?? throw new InvalidOperationException(
                "Could not parse quiz JSON as a QuizSessionRecord or QuizGenerationResponseDto."
            );

        if (dto.Questions.Count == 0)
            throw new InvalidOperationException("Imported JSON contains 0 questions.");

        var quizId = Guid.NewGuid().ToString("N")[..10];
        var session = new QuizSessionRecord
        {
            Id = quizId,
            CourseFolder = targetCourseFolder,
            CourseTitle = targetCourseTitle,
            Term = string.IsNullOrWhiteSpace(dto.CourseTerm) ? null : dto.CourseTerm.Trim(),
            Title = string.IsNullOrWhiteSpace(dto.QuizTitle)
                ? $"{targetCourseTitle} — Imported Deck"
                : dto.QuizTitle.Trim(),
            SourcePdfPath = sourcePdfPathOverride ?? string.Empty,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        for (int i = 0; i < dto.Questions.Count; i++)
        {
            var qDto = dto.Questions[i];
            if (!Enum.TryParse<QuizQuestionKind>(qDto.Kind, ignoreCase: true, out var kind))
                kind = QuizQuestionKind.MultipleChoice;
            if (!Enum.TryParse<CognitiveLevel>(qDto.CognitiveLevel, ignoreCase: true, out var cog))
                cog = CognitiveLevel.Comprehension;

            session.Questions.Add(
                new QuizQuestionRecord
                {
                    Id = Guid.NewGuid().ToString("N")[..10],
                    QuizId = quizId,
                    OrderIndex = i,
                    Kind = kind,
                    CognitiveLevel = cog,
                    Difficulty = Math.Clamp(qDto.Difficulty, 1, 5),
                    TopicTag = string.IsNullOrWhiteSpace(qDto.TopicTag)
                        ? "general"
                        : qDto.TopicTag.Trim(),
                    Prompt = qDto.Prompt.Trim(),
                    Options =
                        qDto.Options?.Where(o => !string.IsNullOrWhiteSpace(o))
                            .Select(o => o.Trim())
                            .ToList()
                        ?? [],
                    CorrectAnswer = qDto.CorrectAnswer.Trim(),
                    AcceptedAliases =
                        qDto.AcceptedAliases?.Where(a => !string.IsNullOrWhiteSpace(a))
                            .Select(a => a.Trim())
                            .ToList()
                        ?? [],
                    Explanation = (qDto.Explanation ?? string.Empty).Trim(),
                    CodeSnippet = string.IsNullOrWhiteSpace(qDto.CodeSnippet)
                        ? null
                        : qDto.CodeSnippet,
                    CodeLanguage = string.IsNullOrWhiteSpace(qDto.CodeLanguage)
                        ? null
                        : qDto.CodeLanguage,
                    MathLatex = string.IsNullOrWhiteSpace(qDto.MathLatex) ? null : qDto.MathLatex,
                    Diagram = string.IsNullOrWhiteSpace(qDto.Diagram) ? null : qDto.Diagram,
                    SourcePdfPath = sourcePdfPathOverride ?? string.Empty,
                    SourcePage = qDto.Evidence?.PageNumber > 0 ? qDto.Evidence.PageNumber : 1,
                    SectionTitle = qDto.Evidence?.SectionTitle ?? string.Empty,
                    VerbatimQuote = qDto.Evidence?.VerbatimQuote ?? string.Empty,
                    SrsDueDate = DateOnly.FromDateTime(DateTime.Today),
                }
            );
        }

        if (!string.IsNullOrWhiteSpace(session.SourcePdfPath) && File.Exists(session.SourcePdfPath))
            PdfQuizGrounder.GroundQuestionsToPdf(session.SourcePdfPath, session.Questions);

        return session;
    }

    public static void ExportDeckToPrintablePdf(
        QuizSessionRecord quiz,
        string outputPdfPath,
        QuizPdfExportOptions? options = null
    ) => QuizPdfPrintRenderer.Render(quiz, outputPdfPath, options);

    public static string BuildSingleQuestionFixPrompt(
        QuizSessionRecord quiz,
        QuizQuestionRecord question,
        string problemDescription
    )
    {
        var dto = new GeneratedQuestionDto
        {
            Kind = question.Kind.ToString(),
            CognitiveLevel = question.CognitiveLevel.ToString(),
            Difficulty = question.Difficulty,
            TopicTag = question.TopicTag,
            Prompt = question.Prompt,
            Options = [.. question.Options],
            CorrectAnswer = question.CorrectAnswer,
            AcceptedAliases = [.. question.AcceptedAliases],
            Explanation = question.Explanation,
            CodeSnippet = question.CodeSnippet,
            CodeLanguage = question.CodeLanguage,
            MathLatex = question.MathLatex,
            Diagram = question.Diagram,
            Evidence = new EvidenceAnchorDto
            {
                PageNumber = question.SourcePage,
                SectionTitle = question.SectionTitle,
                VerbatimQuote = question.VerbatimQuote,
            },
        };

        IReadOnlyList<GroundedPdfPage> contextPages = [];
        var pdfPath = !string.IsNullOrWhiteSpace(question.SourcePdfPath)
            ? question.SourcePdfPath
            : quiz.SourcePdfPath;
        if (!string.IsNullOrWhiteSpace(pdfPath) && File.Exists(pdfPath) && question.SourcePage > 0)
        {
            try
            {
                contextPages = PdfQuizGrounder.ExtractGroundedPages(
                    pdfPath,
                    startPage: Math.Max(1, question.SourcePage - 1),
                    endPage: question.SourcePage + 1,
                    maxPages: 3
                );
            }
            catch
            {
                // Ignore if PDF cannot be read
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine(
            $"You are repairing ONE question from the quiz \"{quiz.Title}\" ({quiz.CourseTitle})."
        );
        sb.AppendLine(
            "Return ONLY a single valid JSON object matching the exact question schema below (no markdown fences or commentary)."
        );
        sb.AppendLine();
        sb.AppendLine("REPORTED ISSUE WITH THIS QUESTION:");
        sb.AppendLine(
            string.IsNullOrWhiteSpace(problemDescription)
                ? "Fix clarity, answer accuracy, and PDF evidence grounding."
                : problemDescription.Trim()
        );
        sb.AppendLine();
        sb.AppendLine("CURRENT QUESTION JSON:");
        sb.AppendLine(JsonSerializer.Serialize(dto, PrettyJsonOpts));

        if (contextPages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("GROUNDED SOURCE PDF CONTEXT:");
            sb.AppendLine(
                PdfQuizGrounder.FormatPagesForPrompt(contextPages, maxChars: 14_000, out _)
            );
        }

        return sb.ToString();
    }

    public static string BuildDeckComprehensiveFixPrompt(
        QuizSessionRecord deck,
        IReadOnlyList<QuizQuestionRecord> targetQuestions,
        string instructions
    )
    {
        var dtoList = targetQuestions
            .Select(q => new
            {
                targetId = q.Id,
                kind = q.Kind.ToString(),
                cognitiveLevel = q.CognitiveLevel.ToString(),
                archetype = q.Archetype,
                difficulty = q.Difficulty,
                topicTag = q.TopicTag,
                prompt = q.Prompt,
                options = q.Options,
                correctAnswer = q.CorrectAnswer,
                acceptedAliases = q.AcceptedAliases,
                hint = q.Hint,
                explanation = q.Explanation,
                codeSnippet = q.CodeSnippet,
                codeLanguage = q.CodeLanguage,
                mathLatex = q.MathLatex,
                diagram = q.Diagram,
                evidence = new
                {
                    pageNumber = q.SourcePage,
                    sectionTitle = q.SectionTitle,
                    verbatimQuote = q.VerbatimQuote,
                },
            })
            .ToList();

        var ranges = targetQuestions
            .Where(q => q.SourcePage > 0)
            .Select(q => new PdfPageRange(Math.Max(1, q.SourcePage - 1), q.SourcePage + 1))
            .OrderBy(r => r.StartPage)
            .ToList();

        IReadOnlyList<GroundedPdfPage> contextPages = [];
        if (!string.IsNullOrWhiteSpace(deck.SourcePdfPath) && File.Exists(deck.SourcePdfPath))
        {
            try
            {
                contextPages =
                    ranges.Count > 0
                        ? PdfQuizGrounder.ExtractGroundedPages(
                            deck.SourcePdfPath,
                            explicitRanges: ranges,
                            maxPages: 30
                        )
                        : PdfQuizGrounder.ExtractGroundedPages(deck.SourcePdfPath, maxPages: 20);
            }
            catch
            {
                // Ignore if PDF cannot be read
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine(
            $"You are performing a comprehensive AI repair & enhancement on the quiz deck \"{deck.Title}\" ({deck.CourseTitle})."
        );
        sb.AppendLine(
            "Return ONLY a valid JSON object matching this exact schema (no markdown fences, preserve each item's exact \"targetId\"):"
        );
        sb.AppendLine("{");
        sb.AppendLine(
            "  \"updatedQuestions\": [ { \"targetId\": \"existing-id\", \"kind\": \"...\", \"cognitiveLevel\": \"Recall | Comprehension | Application | Analysis\", \"archetype\": \"FarTransfer | DiscriminativeContrast | CausalPerturbation | SpotTheFlaw | DualCoding | null\", \"difficulty\": 3, \"topicTag\": \"...\", \"prompt\": \"...\", \"options\": [...], \"correctAnswer\": \"...\", \"acceptedAliases\": [], \"hint\": \"1-sentence clue without revealing the answer (or null)\", \"explanation\": \"Why correct: ... Why distractors fail: ... Core principle: ...\", \"codeSnippet\": null, \"codeLanguage\": null, \"mathLatex\": null, \"diagram\": null, \"evidence\": { \"pageNumber\": 1, \"sectionTitle\": \"...\", \"verbatimQuote\": \"...\" } } ],"
        );
        sb.AppendLine(
            "  \"newQuestions\": [ { \"kind\": \"...\", ...same fields without targetId... } ]"
        );
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("USER INSTRUCTIONS / ISSUES TO FIX:");
        sb.AppendLine(
            string.IsNullOrWhiteSpace(instructions)
                ? "Fix inaccuracies, improve distractors, and ensure every question has accurate PDF evidence."
                : instructions.Trim()
        );
        sb.AppendLine();
        sb.AppendLine($"TARGETED QUESTIONS ({dtoList.Count}):");
        sb.AppendLine(JsonSerializer.Serialize(dtoList, PrettyJsonOpts));

        if (contextPages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("GROUNDED SOURCE PDF CONTEXT:");
            sb.AppendLine(
                PdfQuizGrounder.FormatPagesForPrompt(contextPages, maxChars: 60_000, out _)
            );
        }

        return sb.ToString();
    }

    private sealed class ComprehensiveFixResponseDto
    {
        public List<ComprehensiveUpdatedQuestionDto> UpdatedQuestions { get; set; } = [];
        public List<GeneratedQuestionDto> NewQuestions { get; set; } = [];

        /// <summary>Fallback when an LLM returns standard { "questions": [...] } instead of updatedQuestions.</summary>
        public List<ComprehensiveUpdatedQuestionDto> Questions { get; set; } = [];
    }

    private sealed class ComprehensiveUpdatedQuestionDto
    {
        public string? TargetId { get; set; }
        public string? Id { get; set; }
        public int? QuestionNumber { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string CognitiveLevel { get; set; } = string.Empty;
        public string? Archetype { get; set; }
        public int Difficulty { get; set; } = 3;
        public string TopicTag { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public List<string> Options { get; set; } = [];
        public string CorrectAnswer { get; set; } = string.Empty;
        public List<string> AcceptedAliases { get; set; } = [];
        public string? Hint { get; set; }
        public string Explanation { get; set; } = string.Empty;
        public string? CodeSnippet { get; set; }
        public string? CodeLanguage { get; set; }
        public string? MathLatex { get; set; }
        public string? Diagram { get; set; }
        public EvidenceAnchorDto? Evidence { get; set; }

        public string? EffectiveId => !string.IsNullOrWhiteSpace(TargetId) ? TargetId : Id;

        public GeneratedQuestionDto ToGeneratedDto() =>
            new()
            {
                Kind = Kind,
                CognitiveLevel = CognitiveLevel,
                Archetype = Archetype,
                Difficulty = Difficulty,
                TopicTag = TopicTag,
                Prompt = Prompt,
                Options = Options,
                CorrectAnswer = CorrectAnswer,
                AcceptedAliases = AcceptedAliases,
                Hint = Hint,
                Explanation = Explanation,
                CodeSnippet = CodeSnippet,
                CodeLanguage = CodeLanguage,
                MathLatex = MathLatex,
                Diagram = Diagram,
                Evidence = Evidence ?? new EvidenceAnchorDto(),
            };
    }

    public static (int UpdatedCount, int AddedCount) ApplyDeckComprehensiveFixJson(
        QuizSessionRecord deck,
        string rawJson,
        IReadOnlyList<string>? fallbackTargetIds = null
    )
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            throw new InvalidOperationException("Paste a valid JSON response before applying.");

        var cleaned = ExtractJsonPayload(rawJson);
        List<ComprehensiveUpdatedQuestionDto> updateCandidates = [];
        List<GeneratedQuestionDto> newCandidates = [];

        if (cleaned.StartsWith('['))
        {
            updateCandidates =
                JsonSerializer.Deserialize<List<ComprehensiveUpdatedQuestionDto>>(
                    cleaned,
                    PrettyJsonOpts
                ) ?? [];
        }
        else
        {
            var payload =
                JsonSerializer.Deserialize<ComprehensiveFixResponseDto>(cleaned, PrettyJsonOpts)
                ?? throw new InvalidOperationException(
                    "Could not parse comprehensive deck fix JSON."
                );

            updateCandidates =
                payload.UpdatedQuestions.Count > 0 ? payload.UpdatedQuestions : payload.Questions;
            newCandidates = payload.NewQuestions;

            // Single-object fallback: { "targetId": "...", "prompt": "..." }
            if (updateCandidates.Count == 0 && newCandidates.Count == 0)
            {
                var single = JsonSerializer.Deserialize<ComprehensiveUpdatedQuestionDto>(
                    cleaned,
                    PrettyJsonOpts
                );
                if (single is not null && !string.IsNullOrWhiteSpace(single.Prompt))
                    updateCandidates.Add(single);
            }
        }

        int updated = 0;
        var touched = new List<(QuizQuestionRecord Question, EvidenceAnchorDto? Evidence)>();
        var alreadyUpdated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int idx = 0; idx < updateCandidates.Count; idx++)
        {
            var upd = updateCandidates[idx];
            var target = ResolveTargetQuestion(
                deck,
                upd,
                idx,
                updateCandidates.Count,
                fallbackTargetIds,
                alreadyUpdated
            );
            if (target is null)
                continue;

            ApplyDtoToQuestion(target, upd.ToGeneratedDto());
            alreadyUpdated.Add(target.Id);
            touched.Add((target, upd.Evidence));
            updated++;
        }

        int added = 0;
        foreach (var addDto in newCandidates)
        {
            if (string.IsNullOrWhiteSpace(addDto.Prompt))
                continue;

            var newQ = new QuizQuestionRecord
            {
                QuizId = deck.Id,
                OrderIndex = deck.Questions.Count,
                SourcePdfPath = deck.SourcePdfPath,
                SrsDueDate = DateOnly.FromDateTime(DateTime.Today),
            };
            ApplyDtoToQuestion(newQ, addDto);
            deck.Questions.Add(newQ);
            touched.Add((newQ, addDto.Evidence));
            added++;
        }

        if (updated == 0 && added == 0)
        {
            throw new InvalidOperationException(
                "No matching questions were updated or added. Ensure the JSON contains \"updatedQuestions\" (with each item's \"targetId\") or \"newQuestions\"."
            );
        }

        var pdfTouched = touched
            .Where(t =>
                t.Question.IsPdfSource
                && !string.IsNullOrWhiteSpace(deck.SourcePdfPath)
                && File.Exists(deck.SourcePdfPath)
            )
            .Select(t => t.Question)
            .ToList();
        if (pdfTouched.Count > 0)
            PdfQuizGrounder.GroundQuestionsToPdf(deck.SourcePdfPath, pdfTouched);

        var fileTouched = touched
            .Where(t =>
                !t.Question.IsPdfSource
                && !string.IsNullOrWhiteSpace(t.Question.SourcePdfPath)
                && File.Exists(t.Question.SourcePdfPath)
            )
            .ToList();
        if (fileTouched.Count > 0)
        {
            var distinctPaths = fileTouched
                .Select(t => t.Question.SourcePdfPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            Knowledge.FileSourceQuizSupport.GroundQuestionsToFiles(distinctPaths, fileTouched);
        }

        return (updated, added);
    }

    private static QuizQuestionRecord? ResolveTargetQuestion(
        QuizSessionRecord deck,
        ComprehensiveUpdatedQuestionDto upd,
        int candidateIndex,
        int candidateCount,
        IReadOnlyList<string>? fallbackTargetIds,
        HashSet<string> alreadyUpdated
    )
    {
        var rawId = upd.EffectiveId?.Trim();

        // 1. Exact ID match
        if (!string.IsNullOrWhiteSpace(rawId))
        {
            var exact = deck.Questions.FirstOrDefault(q =>
                string.Equals(q.Id, rawId, StringComparison.OrdinalIgnoreCase)
            );
            if (exact is not null)
                return exact;

            // 2. 1-based label match ("1", "#1", "Q1")
            var digits = rawId.TrimStart('#', 'Q', 'q', ' ');
            if (
                int.TryParse(digits, out int oneBased)
                && oneBased >= 1
                && oneBased <= deck.Questions.Count
            )
                return deck.Questions[oneBased - 1];
        }

        // 3. Explicit questionNumber (1-based)
        if (upd.QuestionNumber is >= 1 && upd.QuestionNumber.Value <= deck.Questions.Count)
            return deck.Questions[upd.QuestionNumber.Value - 1];

        // 4. Positional match against selected target IDs when counts align
        if (
            fallbackTargetIds is { Count: > 0 }
            && candidateIndex < fallbackTargetIds.Count
            && candidateCount <= fallbackTargetIds.Count
        )
        {
            var posId = fallbackTargetIds[candidateIndex];
            var byPos = deck.Questions.FirstOrDefault(q =>
                string.Equals(q.Id, posId, StringComparison.OrdinalIgnoreCase)
                && !alreadyUpdated.Contains(q.Id)
            );
            if (byPos is not null)
                return byPos;
        }

        // 5. Fallback: exact prompt match against an un-updated question in the deck
        if (!string.IsNullOrWhiteSpace(upd.Prompt))
        {
            var cleanPrompt = upd.Prompt.Trim();
            var byPrompt = deck.Questions.FirstOrDefault(q =>
                !alreadyUpdated.Contains(q.Id)
                && string.Equals(q.Prompt.Trim(), cleanPrompt, StringComparison.OrdinalIgnoreCase)
            );
            if (byPrompt is not null)
                return byPrompt;
        }

        return null;
    }

    public static void ApplySingleQuestionFixJson(QuizQuestionRecord target, string rawJson)
    {
        var cleaned = ExtractJsonPayload(rawJson);
        GeneratedQuestionDto? dto = null;

        try
        {
            var wrapper = JsonSerializer.Deserialize<QuizGenerationResponseDto>(
                cleaned,
                PrettyJsonOpts
            );
            if (wrapper?.Questions is { Count: > 0 })
                dto = wrapper.Questions[0];
        }
        catch
        {
            // Try direct single-object deserialization
        }

        dto ??=
            JsonSerializer.Deserialize<GeneratedQuestionDto>(cleaned, PrettyJsonOpts)
            ?? throw new InvalidOperationException("Could not parse fixed question JSON.");

        ApplyDtoToQuestion(target, dto);

        if (
            target.IsPdfSource
            && !string.IsNullOrWhiteSpace(target.SourcePdfPath)
            && File.Exists(target.SourcePdfPath)
        )
            PdfQuizGrounder.GroundQuestionsToPdf(target.SourcePdfPath, [target]);
        else if (
            !target.IsPdfSource
            && !string.IsNullOrWhiteSpace(target.SourcePdfPath)
            && File.Exists(target.SourcePdfPath)
        )
            Knowledge.FileSourceQuizSupport.GroundQuestionsToFiles(
                [target.SourcePdfPath],
                [(target, dto.Evidence)]
            );
    }

    private static void ApplyDtoToQuestion(QuizQuestionRecord target, GeneratedQuestionDto dto)
    {
        if (Enum.TryParse<QuizQuestionKind>(dto.Kind, ignoreCase: true, out var kind))
            target.Kind = kind;
        if (Enum.TryParse<CognitiveLevel>(dto.CognitiveLevel, ignoreCase: true, out var cog))
            target.CognitiveLevel = cog;

        target.Difficulty = Math.Clamp(dto.Difficulty, 1, 5);
        if (!string.IsNullOrWhiteSpace(dto.TopicTag))
            target.TopicTag = dto.TopicTag.Trim().ToLowerInvariant().TrimStart('@', '#');
        if (!string.IsNullOrWhiteSpace(dto.Prompt))
            target.Prompt = dto.Prompt.Trim();

        var (options, correctAnswer) = QuizLlmService.CanonicalizeOptionsAndAnswer(
            target.Kind,
            dto.Options,
            dto.CorrectAnswer
        );
        target.Options = options;
        target.CorrectAnswer = correctAnswer;
        target.AcceptedAliases =
            dto.AcceptedAliases?.Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            ?? [];
        target.Explanation = (dto.Explanation ?? string.Empty).Trim();
        if (dto.Hint is not null)
            target.Hint = string.IsNullOrWhiteSpace(dto.Hint) ? null : dto.Hint.Trim();
        if (dto.Archetype is not null)
            target.Archetype = string.IsNullOrWhiteSpace(dto.Archetype)
                ? null
                : dto.Archetype.Trim();
        target.CodeSnippet = string.IsNullOrWhiteSpace(dto.CodeSnippet) ? null : dto.CodeSnippet;
        target.CodeLanguage = string.IsNullOrWhiteSpace(dto.CodeLanguage) ? null : dto.CodeLanguage;
        target.MathLatex = string.IsNullOrWhiteSpace(dto.MathLatex) ? null : dto.MathLatex;
        target.Diagram = string.IsNullOrWhiteSpace(dto.Diagram) ? null : dto.Diagram;

        if (dto.Evidence is not null)
        {
            if (dto.Evidence.PageNumber > 0)
                target.SourcePage = dto.Evidence.PageNumber;
            if (!string.IsNullOrWhiteSpace(dto.Evidence.SectionTitle))
                target.SectionTitle = dto.Evidence.SectionTitle;
            if (!string.IsNullOrWhiteSpace(dto.Evidence.VerbatimQuote))
                target.VerbatimQuote = dto.Evidence.VerbatimQuote;
        }
    }

    private static string ExtractJsonPayload(string raw)
    {
        var trimmed = raw.Trim();

        // Strip markdown ```json ... ``` code fences first if present
        int fenceStart = trimmed.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart >= 0)
        {
            int afterFence = trimmed.IndexOf('\n', fenceStart);
            if (afterFence > fenceStart)
            {
                int fenceEnd = trimmed.IndexOf("```", afterFence + 1, StringComparison.Ordinal);
                if (fenceEnd > afterFence)
                    trimmed = trimmed[(afterFence + 1)..fenceEnd].Trim();
            }
        }

        int firstBrace = trimmed.IndexOf('{');
        int firstBracket = trimmed.IndexOf('[');
        if (firstBracket >= 0 && (firstBrace < 0 || firstBracket < firstBrace))
        {
            int lastBracket = trimmed.LastIndexOf(']');
            if (lastBracket > firstBracket)
                return trimmed[firstBracket..(lastBracket + 1)];
        }

        int lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
            return trimmed[firstBrace..(lastBrace + 1)];
        return trimmed;
    }
}
