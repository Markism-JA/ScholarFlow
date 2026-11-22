using System.Text;
using ScholarFlow.Core.Database;
using ScholarFlow.Core.Knowledge;

namespace ScholarFlow.Core.Quiz;

/// <summary>
/// Quiz generation from non-PDF study sources (code, markdown, plain text, images).
/// API mode sends numbered text + base64 images to a vision-capable model; Manual Web UI mode
/// produces the equivalent copy-paste prompt plus an optional packed-image PDF manifest.
/// </summary>
public sealed partial class QuizLlmService
{
    private const int MaxVisionImages = 8;

    public static ContentModalityProfile DetectFileSourceModalities(QuizGenerationRequest request)
    {
        if (request.ModalityMode == QuizContentModalityMode.StandardProseOnly)
            return new ContentModalityProfile(HasCode: false, HasMath: false, HasDiagrams: false);

        string? codeLang = null;
        bool hasCode = request.ModalityMode == QuizContentModalityMode.IncludeCodeSnippets;
        bool hasImages = false;
        foreach (var path in request.SourceFilePaths)
        {
            var (kind, lang) = StudySourceClassifier.Classify(path);
            if (kind == StudySourceKind.Code)
            {
                hasCode = true;
                codeLang ??= lang;
            }
            else if (kind == StudySourceKind.Image)
            {
                hasImages = true;
            }
        }

        return new ContentModalityProfile(
            hasCode,
            request.ModalityMode == QuizContentModalityMode.IncludeMathLatex,
            hasImages || request.ModalityMode == QuizContentModalityMode.IncludeDiagrams,
            codeLang
        );
    }

    /// <summary>Manual Web UI: full copy-paste prompt (system rules + numbered file content, plus scoped PDF pages when combined).</summary>
    public string BuildFileSourcePromptForManualCopy(
        QuizGenerationRequest request,
        int maxChars,
        out FileSourcePromptBundle bundle
    ) => BuildFileSourcePromptForManualCopy(request, maxChars, out bundle, out _, out _);

    public string BuildFileSourcePromptForManualCopy(
        QuizGenerationRequest request,
        int maxChars,
        out FileSourcePromptBundle bundle,
        out int totalPdfPages,
        out int includedPdfPages
    )
    {
        totalPdfPages = 0;
        includedPdfPages = 0;
        string? pdfContent = null;
        IReadOnlyList<GroundedPdfPage> pdfPages = [];

        int fileBudget = request.IsMixedSources
            ? Math.Max(12_000, (int)(maxChars * 0.45))
            : maxChars;
        bundle = FileSourceQuizSupport.BuildPromptBundle(request.SourceFilePaths, fileBudget);

        if (request.IsMixedSources)
        {
            pdfPages = PdfQuizGrounder.ExtractGroundedPages(
                request.SourcePdfPath,
                request.Term,
                request.SectionFilter,
                request.StartPage,
                request.EndPage,
                request.SelectedRanges,
                maxPages: 250
            );
            totalPdfPages = pdfPages.Count;
            int remainingPdfBudget = Math.Max(10_000, maxChars - bundle.Content.Length);
            pdfContent = PdfQuizGrounder.FormatPagesForPrompt(
                pdfPages,
                remainingPdfBudget,
                out includedPdfPages
            );
        }

        var weakTopics = ScholarFlowDatabase.GetWeakTopicTags(
            request.BasinRoot,
            request.CourseFolder
        );
        var modality = MergeModalities(request, pdfPages);
        int count = Math.Clamp(request.QuestionCount, 1, 150);
        return BuildSystemInstructions(request, weakTopics, modality)
            + "\n\n"
            + BuildFileUserPrompt(
                request,
                bundle,
                count,
                modality,
                imagesAttachedAsPdf: bundle.ImageFiles.Count > 0,
                pdfContent: pdfContent
            );
    }

    private static ContentModalityProfile MergeModalities(
        QuizGenerationRequest request,
        IReadOnlyList<GroundedPdfPage> pdfPages
    )
    {
        var fileMod = DetectFileSourceModalities(request);
        if (pdfPages.Count == 0)
            return fileMod;

        var pdfMod = DetectContentModalities(pdfPages, request.CourseTitle, request.ModalityMode);
        return new ContentModalityProfile(
            HasCode: fileMod.HasCode || pdfMod.HasCode,
            HasMath: fileMod.HasMath || pdfMod.HasMath,
            HasDiagrams: fileMod.HasDiagrams || pdfMod.HasDiagrams,
            DetectedLanguage: fileMod.DetectedLanguage ?? pdfMod.DetectedLanguage
        );
    }

    /// <summary>Manual Web UI: upload instructions for the packed image PDF (1 image per page), optionally combined with scoped PDF pages and text/code files.</summary>
    public string BuildInstructionPromptForPackedImages(
        QuizGenerationRequest request,
        IReadOnlyList<PackedImagePage> pages,
        FileSourcePromptBundle? textBundle = null,
        IReadOnlyList<(int OriginalPage, string SectionBreadcrumb)>? pdfManifest = null
    )
    {
        var weakTopics = ScholarFlowDatabase.GetWeakTopicTags(
            request.BasinRoot,
            request.CourseFolder
        );
        var modality = DetectFileSourceModalities(request);
        int count = Math.Clamp(request.QuestionCount, 1, 150);

        var sb = new StringBuilder();
        sb.AppendLine(BuildSystemInstructions(request, weakTopics, modality));
        sb.AppendLine();

        if (pdfManifest is { Count: > 0 })
        {
            sb.AppendLine("ATTACHED SCOPED PDF PAGES:");
            sb.AppendLine(
                "For PDF-grounded questions, use the ORIGINAL page number below for \"evidence.pageNumber\" and set \"evidence.file\": null:"
            );
            for (int i = 0; i < pdfManifest.Count; i++)
            {
                var (origPage, section) = pdfManifest[i];
                sb.AppendLine(
                    $"- Uploaded PDF Page {i + 1} = [PAGE {origPage}] | Section: {section}"
                );
            }
            sb.AppendLine();
        }

        if (pages.Count > 0)
        {
            int pageOffset = pdfManifest?.Count ?? 0;
            sb.AppendLine("ATTACHED IMAGE PACKET (PDF, one image per page):");
            sb.AppendLine(
                "Use the ORIGINAL image file name below for \"evidence.file\" so the app can link each question back to its image:"
            );
            foreach (var p in pages)
                sb.AppendLine($"- Uploaded PDF Page {pageOffset + p.PageNumber} = {p.FileName}");
            sb.AppendLine();
            sb.AppendLine(
                "Image-based questions must be answerable from what is visible (labels, structure, arrows, values). Prefer diagram reading, component identification, and flow/ordering questions. For each image question, include a specific directional visual pointer in \"evidence.verbatimQuote\" (where to look + what proves the answer) and an approximate normalized \"evidence.region\": {\"x\": 0..1, \"y\": 0..1, \"w\": 0..1, \"h\": 0..1} box."
            );
        }

        if (textBundle is not null && !string.IsNullOrWhiteSpace(textBundle.Content))
        {
            sb.AppendLine();
            sb.AppendLine("ADDITIONAL TEXT / CODE SOURCES:");
            sb.AppendLine(textBundle.Content);
        }

        sb.AppendLine();
        sb.AppendLine(
            $"Generate {count} grounded questions from the attached sources strictly following the JSON schema above."
        );
        return sb.ToString();
    }

    private static string BuildFileUserPrompt(
        QuizGenerationRequest req,
        FileSourcePromptBundle bundle,
        int count,
        ContentModalityProfile modality,
        bool imagesAttachedAsPdf = false,
        string? pdfContent = null
    )
    {
        var exactCounts = req.ResolveExactKindCounts(count);
        var breakdown = string.Join(
            ", ",
            exactCounts.Where(kv => kv.Value > 0).Select(kv => $"{kv.Value} {kv.Key}")
        );

        var excludeBlock =
            req.ExcludePromptsOrTopics.Count > 0
                ? $"\nDo NOT repeat or duplicate any of these existing questions/topics:\n- {string.Join("\n- ", req.ExcludePromptsOrTopics.Take(25))}\n"
                : string.Empty;

        var imageNote =
            bundle.ImageFiles.Count > 0
                ? $"\nImages ({bundle.ImageFiles.Count}): {string.Join(", ", bundle.ImageFiles.Select(Path.GetFileName))} — {(imagesAttachedAsPdf ? "attached as a PDF packet (one image per page)" : "attached to this message")}. Use the exact image file name as \"evidence.file\"."
                : string.Empty;

        var truncNote =
            bundle.TruncatedFiles.Count > 0
                ? $"\nNote: some content was truncated or skipped for size: {string.Join(", ", bundle.TruncatedFiles)}."
                : string.Empty;

        bool hasPdf = !string.IsNullOrWhiteSpace(pdfContent);
        var sourceTypeLabel = hasPdf
            ? "combined PDF pages + course source files (code / notes / images)"
            : "course files (code / notes / images)";

        var body = new StringBuilder();
        if (hasPdf)
        {
            body.AppendLine("--- PDF STUDY PAGES ---");
            body.AppendLine(pdfContent);
            body.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(bundle.Content))
        {
            if (hasPdf)
                body.AppendLine("--- COURSE SOURCE FILES ---");
            body.Append(bundle.Content);
        }

        return $"""
            Course: {req.CourseTitle}
            Source type: {sourceTypeLabel}
            Required Question Breakdown ({Math.Clamp(
                count,
                1,
                150
            )} total): {breakdown}{imageNote}{truncNote}{excludeBlock}{BuildGuardedCustomInstructionsBlock(
                req.CustomInstructions
            )}
            Generate high-quality, evidence-grounded questions strictly matching the required question breakdown from the following sources:

            {body}
            """;
    }

    private async Task<QuizSessionRecord> GenerateFromFileSourcesAsync(
        QuizGenerationRequest request,
        CancellationToken ct
    )
    {
        var effectiveKey = !string.IsNullOrWhiteSpace(request.ApiKey)
            ? request.ApiKey.Trim()
            : Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        bool isLocalEndpoint =
            request.ProviderBaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
            || request.ProviderBaseUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase);

        if (
            request.UseOfflineHeuristicFallback
            || (!isLocalEndpoint && string.IsNullOrWhiteSpace(effectiveKey))
        )
        {
            throw new InvalidOperationException(
                "The Offline Smart analyzer only supports PDFs. Configure an AI engine, or use Web AI JSON for code / text / image sources."
            );
        }

        int desiredCount = Math.Clamp(request.QuestionCount, 1, 150);
        int charBudget = isLocalEndpoint ? 28_000 : 100_000;
        int fileBudget = request.IsMixedSources
            ? Math.Max(12_000, (int)(charBudget * 0.45))
            : charBudget;

        var bundle = await Task.Run(
            () => FileSourceQuizSupport.BuildPromptBundle(request.SourceFilePaths, fileBudget),
            ct
        );

        IReadOnlyList<GroundedPdfPage> pdfPages = [];
        string? pdfContent = null;
        if (request.IsMixedSources)
        {
            pdfPages = await Task.Run(
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
            if (pdfPages.Count > 0)
            {
                int remainingBudget = Math.Max(10_000, charBudget - bundle.Content.Length);
                pdfContent = PdfQuizGrounder.FormatPagesForPrompt(pdfPages, remainingBudget, out _);
            }
        }

        if (
            string.IsNullOrWhiteSpace(bundle.Content)
            && bundle.ImageFiles.Count == 0
            && string.IsNullOrWhiteSpace(pdfContent)
        )
            throw new InvalidOperationException(
                "No readable text, code, image, or PDF content was found in the selected sources."
            );

        var imageUris = new List<string>();
        foreach (var img in bundle.ImageFiles.Take(MaxVisionImages))
        {
            var uri = ImagePacketPacker.BuildDataUri(img);
            if (uri is not null)
                imageUris.Add(uri);
        }

        var weakTopics = ScholarFlowDatabase.GetWeakTopicTags(
            request.BasinRoot,
            request.CourseFolder
        );
        var modality = MergeModalities(request, pdfPages);
        var sysPrompt = BuildSystemInstructions(request, weakTopics, modality);
        var usrPrompt = BuildFileUserPrompt(
            request,
            bundle,
            desiredCount,
            modality,
            pdfContent: pdfContent
        );

        var raw = await CallOpenAiCompatibleChatAsync(
            request.ProviderBaseUrl,
            effectiveKey,
            request.ModelName,
            sysPrompt,
            usrPrompt,
            ct,
            imageUris
        );

        var dto = ParseQuizResponseJson(raw);
        dto.Questions = dto.Questions.Take(desiredCount).ToList();
        return await BuildGroundAndPersistSessionAsync(
            request,
            dto,
            request.ModelName,
            pdfPages,
            ct
        );
    }
}
