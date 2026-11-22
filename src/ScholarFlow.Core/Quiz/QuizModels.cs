using System.Text.Json.Serialization;

namespace ScholarFlow.Core.Quiz;

public enum QuizQuestionKind
{
    MultipleChoice,
    TrueFalse,
    Identification,
    Situational,
    FillInTheBlank,
    Ordering,
}

public enum CognitiveLevel
{
    Recall,
    Comprehension,
    Application,
    Analysis,
}

public enum CognitiveMixPreset
{
    Balanced,
    Foundations,
    ExamApplication,
    DeepMastery,
}

public enum QuestionArchetype
{
    FarTransfer,
    DiscriminativeContrast,
    CausalPerturbation,
    SpotTheFlaw,
    DualCoding,
}

public enum ConfidenceLevel
{
    None = 0,
    Guess = 1,
    Unsure = 2,
    Certain = 3,
}

public enum QuizPresetMode
{
    ExamSimulator,
    QuickReview,
    WeakTopicDrill,
    DeepMastery,
    CodeLab,
    Custom,
}

public enum QuestionAllocationMode
{
    Percentages,
    ExactCounts,
}

public enum QuizContentModalityMode
{
    AutoDetect,
    StandardProseOnly,
    IncludeCodeSnippets,
    IncludeMathLatex,
    IncludeDiagrams,
}

[Flags]
public enum QuizContentModalityFlags
{
    None = 0,
    Code = 1 << 0,
    MathLatex = 1 << 1,
    Diagrams = 1 << 2,
}

public sealed record ContentModalityProfile(
    bool HasCode,
    bool HasMath,
    bool HasDiagrams,
    string? DetectedLanguage = null,
    bool DetectedCodeInSource = false,
    bool DetectedMathInSource = false,
    bool DetectedDiagramsInSource = false,
    bool IsProseOnlyOverride = false,
    bool HasImageSources = false
);

/// <summary>
/// Normalized top-left [0..1] bounding box on a PDF page so highlights scale cleanly at any zoom level.
/// </summary>
public sealed record NormalizedHighlightRect(double X, double Y, double Width, double Height);

/// <summary>
/// Represents an extractable visual figure (embedded raster image or caption-anchored vector diagram region)
/// discovered on a 1-based PDF page during grounding.
/// </summary>
public sealed record ExtractablePdfFigure(
    string FigureId,
    int PageNumber,
    string Caption,
    NormalizedHighlightRect Region,
    int EmbeddedImageIndex = -1
);

public sealed record GroundedEvidenceAnchor(
    int PageNumber,
    string SectionTitle,
    string VerbatimQuote,
    List<NormalizedHighlightRect> HighlightRects
);

/// <summary>
/// JSON DTO representing the exact statement and page inside the Master/Handout PDF that grounds a question.
/// </summary>
public sealed class EvidenceAnchorDto
{
    [JsonPropertyName("pageNumber")]
    public int PageNumber { get; set; } = 1;

    [JsonPropertyName("sectionTitle")]
    public string SectionTitle { get; set; } = string.Empty;

    [JsonPropertyName("verbatimQuote")]
    public string VerbatimQuote { get; set; } = string.Empty;

    [JsonPropertyName("secondaryQuotes")]
    public List<string>? SecondaryQuotes { get; set; }

    /// <summary>File name or relative path for non-PDF (code / markdown / text / image) sources.</summary>
    [JsonPropertyName("file")]
    public string? File { get; set; }

    [JsonPropertyName("startLine")]
    public int? StartLine { get; set; }

    [JsonPropertyName("endLine")]
    public int? EndLine { get; set; }

    /// <summary>
    /// Optional normalized [0..1] visual region for image-grounded questions.
    /// Accepts either {"x":0.1,"y":0.2,"w":0.4,"h":0.3} (or width/height) or [x, y, w, h].
    /// </summary>
    [JsonPropertyName("region")]
    public System.Text.Json.JsonElement? Region { get; set; }

    public NormalizedHighlightRect? TryParseRegion()
    {
        if (Region is not { } el)
            return null;

        double x,
            y,
            w,
            h;
        if (el.ValueKind == System.Text.Json.JsonValueKind.Array && el.GetArrayLength() >= 4)
        {
            if (
                !el[0].TryGetDouble(out x)
                || !el[1].TryGetDouble(out y)
                || !el[2].TryGetDouble(out w)
                || !el[3].TryGetDouble(out h)
            )
                return null;
        }
        else if (el.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            if (!TryGetProp(el, "x", out x) || !TryGetProp(el, "y", out y))
                return null;
            if (!TryGetProp(el, "w", out w) && !TryGetProp(el, "width", out w))
                return null;
            if (!TryGetProp(el, "h", out h) && !TryGetProp(el, "height", out h))
                return null;
        }
        else
        {
            return null;
        }

        if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(w) || double.IsNaN(h))
            return null;

        // Normalize percentage-style 0..100 coordinates if a model returns them
        if (x > 1.0 || y > 1.0 || w > 1.0 || h > 1.0)
        {
            if (x <= 100 && y <= 100 && w <= 100 && h <= 100)
            {
                x /= 100.0;
                y /= 100.0;
                w /= 100.0;
                h /= 100.0;
            }
            else
            {
                return null;
            }
        }

        x = Math.Clamp(x, 0.0, 0.98);
        y = Math.Clamp(y, 0.0, 0.98);
        w = Math.Clamp(w, 0.02, 1.0 - x);
        h = Math.Clamp(h, 0.02, 1.0 - y);
        return new NormalizedHighlightRect(x, y, w, h);

        static bool TryGetProp(System.Text.Json.JsonElement obj, string name, out double val)
        {
            foreach (var p in obj.EnumerateObject())
            {
                if (
                    p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                    && p.Value.TryGetDouble(out val)
                )
                    return true;
            }
            val = 0;
            return false;
        }
    }
}

/// <summary>
/// JSON DTO for a single AI-generated or imported question.
/// </summary>
public sealed class GeneratedQuestionDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "MultipleChoice";

    [JsonPropertyName("cognitiveLevel")]
    public string CognitiveLevel { get; set; } = "Comprehension";

    [JsonPropertyName("difficulty")]
    public int Difficulty { get; set; } = 3;

    [JsonPropertyName("topicTag")]
    public string TopicTag { get; set; } = "general";

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;

    [JsonPropertyName("options")]
    public List<string> Options { get; set; } = [];

    [JsonPropertyName("correctAnswer")]
    public string CorrectAnswer { get; set; } = string.Empty;

    [JsonPropertyName("acceptedAliases")]
    public List<string> AcceptedAliases { get; set; } = [];

    [JsonPropertyName("explanation")]
    public string Explanation { get; set; } = string.Empty;

    [JsonPropertyName("hint")]
    public string? Hint { get; set; }

    [JsonPropertyName("archetype")]
    public string? Archetype { get; set; }

    [JsonPropertyName("codeSnippet")]
    public string? CodeSnippet { get; set; }

    [JsonPropertyName("codeLanguage")]
    public string? CodeLanguage { get; set; }

    [JsonPropertyName("mathLatex")]
    public string? MathLatex { get; set; }

    [JsonPropertyName("diagram")]
    public string? Diagram { get; set; }

    /// <summary>
    /// Optional ID of an extractable figure discovered on a source PDF page (e.g. "p12_fig1").
    /// When set, ScholarFlow crops/extracts the actual figure from the PDF and attaches it to <see cref="QuizQuestionRecord.ImagePath"/>.
    /// </summary>
    [JsonPropertyName("sourceFigureId")]
    public string? SourceFigureId { get; set; }

    [JsonPropertyName("evidence")]
    public EvidenceAnchorDto Evidence { get; set; } = new();
}

/// <summary>
/// Root JSON DTO returned by the AI model or pasted via Manual JSON Import.
/// </summary>
public sealed class QuizGenerationResponseDto
{
    [JsonPropertyName("quizTitle")]
    public string QuizTitle { get; set; } = string.Empty;

    [JsonPropertyName("courseTerm")]
    public string CourseTerm { get; set; } = string.Empty;

    [JsonPropertyName("keyConceptsCovered")]
    public List<string> KeyConceptsCovered { get; set; } = [];

    [JsonPropertyName("questions")]
    public List<GeneratedQuestionDto> Questions { get; set; } = [];
}

/// <summary>
/// Persistent question record stored in SQLite (scholarflow.db) with grounded PDF highlight coordinates and SRS state.
/// </summary>
public sealed class QuizQuestionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string QuizId { get; set; } = string.Empty;
    public int OrderIndex { get; set; }
    public QuizQuestionKind Kind { get; set; } = QuizQuestionKind.MultipleChoice;
    public CognitiveLevel CognitiveLevel { get; set; } = CognitiveLevel.Comprehension;
    public int Difficulty { get; set; } = 3;
    public string TopicTag { get; set; } = "general";
    public string Prompt { get; set; } = string.Empty;
    public List<string> Options { get; set; } = [];
    public string CorrectAnswer { get; set; } = string.Empty;
    public List<string> AcceptedAliases { get; set; } = [];
    public string Explanation { get; set; } = string.Empty;
    public string? Hint { get; set; }
    public string? Archetype { get; set; }
    public string? CodeSnippet { get; set; }
    public string? CodeLanguage { get; set; }
    public string? MathLatex { get; set; }
    public string? Diagram { get; set; }
    public string? ImagePath { get; set; }

    // Grounding to Master / Handout PDF
    public string SourcePdfPath { get; set; } = string.Empty;
    public int SourcePage { get; set; } = 1;

    /// <summary>1-based inclusive line range when the evidence lives in a code / markdown / text file.</summary>
    public int? SourceStartLine { get; set; }
    public int? SourceEndLine { get; set; }
    public string SectionTitle { get; set; } = string.Empty;
    public string VerbatimQuote { get; set; } = string.Empty;
    public List<NormalizedHighlightRect> HighlightRects { get; set; } = [];
    public List<GroundedEvidenceAnchor> EvidenceAnchors { get; set; } = [];

    // Spaced Repetition (SM-2 / FSRS hybrid) & Attempt History
    public double SrsIntervalDays { get; set; }
    public double SrsEaseFactor { get; set; } = 2.5;
    public DateOnly? SrsDueDate { get; set; }
    public int SrsReps { get; set; }
    public int SrsLapses { get; set; }
    public bool? LastAnsweredCorrect { get; set; }

    /// <summary>True when the grounding source is a PDF (page + highlight rects); false for code / text / image files.</summary>
    public bool IsPdfSource =>
        string.IsNullOrWhiteSpace(SourcePdfPath)
        || Knowledge.StudySourceClassifier.Classify(SourcePdfPath).Kind
            is Knowledge.StudySourceKind.Pdf
                or Knowledge.StudySourceKind.Unknown;

    /// <summary>Short evidence chip text: "p.12" for PDFs, "L10–24" for text/code, file name for images.</summary>
    public string EvidenceChipLabel
    {
        get
        {
            if (IsPdfSource)
                return $"p.{SourcePage}";
            var (kind, _) = Knowledge.StudySourceClassifier.Classify(SourcePdfPath);
            if (kind == Knowledge.StudySourceKind.Image)
                return Path.GetFileName(SourcePdfPath);
            if (SourceStartLine is > 0)
                return SourceEndLine is > 0 && SourceEndLine != SourceStartLine
                    ? $"L{SourceStartLine}–{SourceEndLine}"
                    : $"L{SourceStartLine}";
            return Path.GetFileName(SourcePdfPath);
        }
    }

    public bool EvaluateAnswer(string? userAnswer)
    {
        if (string.IsNullOrWhiteSpace(userAnswer))
            return false;

        var cleanUser = NormalizeAnswerToken(userAnswer);
        var cleanCorrect = NormalizeAnswerToken(CorrectAnswer);

        if (string.Equals(cleanUser, cleanCorrect, StringComparison.OrdinalIgnoreCase))
            return true;

        if (Kind == QuizQuestionKind.Ordering)
        {
            var expectedOrder =
                Options.Count >= 2
                    ? string.Join(" -> ", Options.Select(NormalizeAnswerToken))
                    : cleanCorrect;
            var userParts = userAnswer
                .Split(
                    ["->", "|", "\n", ";"],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                )
                .Select(NormalizeAnswerToken)
                .Where(s => s.Length > 0)
                .ToList();
            if (userParts.Count >= 2)
            {
                var normalizedUserOrder = string.Join(" -> ", userParts);
                if (
                    string.Equals(
                        normalizedUserOrder,
                        expectedOrder,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                    return true;
            }
        }

        // If MCQ option starts with "A) ..." or user passed option text matching CorrectAnswer
        if (Kind is QuizQuestionKind.MultipleChoice or QuizQuestionKind.Situational)
        {
            foreach (var opt in Options)
            {
                if (
                    string.Equals(
                        NormalizeAnswerToken(opt),
                        cleanUser,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    var strippedOpt = StripOptionPrefix(opt);
                    var strippedCorrect = StripOptionPrefix(CorrectAnswer);
                    if (
                        string.Equals(
                            NormalizeAnswerToken(strippedOpt),
                            NormalizeAnswerToken(strippedCorrect),
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        return true;
                }
            }
        }

        foreach (var alias in AcceptedAliases)
        {
            if (
                !string.IsNullOrWhiteSpace(alias)
                && string.Equals(
                    cleanUser,
                    NormalizeAnswerToken(alias),
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return true;
            }
        }

        if (Kind is QuizQuestionKind.FillInTheBlank or QuizQuestionKind.Identification)
        {
            if (LatexMathFormatter.AreMathAnswersEquivalent(userAnswer, CorrectAnswer))
                return true;

            if (IsFuzzyTextMatch(userAnswer, CorrectAnswer))
                return true;

            foreach (var alias in AcceptedAliases)
            {
                if (
                    !string.IsNullOrWhiteSpace(alias)
                    && (
                        LatexMathFormatter.AreMathAnswersEquivalent(userAnswer, alias)
                        || IsFuzzyTextMatch(userAnswer, alias)
                    )
                )
                    return true;
            }
        }

        return false;
    }

    private static readonly HashSet<string> AnswerStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a",
        "an",
        "the",
        "of",
        "in",
        "on",
        "at",
        "to",
        "for",
        "and",
        "or",
        "by",
        "with",
        "is",
        "are",
        "was",
        "were",
        "that",
        "which",
    };

    private static bool IsFuzzyTextMatch(string userRaw, string targetRaw)
    {
        var userNorm = NormalizeAnswerToken(userRaw);
        var targetNorm = NormalizeAnswerToken(targetRaw);
        if (string.IsNullOrEmpty(userNorm) || string.IsNullOrEmpty(targetNorm))
            return false;

        // Strip leading articles / filler words
        var userStripped = StripLeadingStopwords(userNorm);
        var targetStripped = StripLeadingStopwords(targetNorm);
        if (string.Equals(userStripped, targetStripped, StringComparison.OrdinalIgnoreCase))
            return true;

        // Avoid fuzzy matching short codes/acronyms or pure numbers (e.g. "TCP" vs "UDP", "1984" vs "1985")
        if (
            targetStripped.All(c => char.IsDigit(c) || char.IsWhiteSpace(c))
            || targetStripped.Length < 4
        )
            return false;

        // Single/phrase Levenshtein tolerance
        int maxDist = targetStripped.Length switch
        {
            <= 4 => 0,
            <= 9 => 1,
            <= 18 => 2,
            _ => 3,
        };
        if (maxDist > 0 && ComputeLevenshteinDistance(userStripped, targetStripped) <= maxDist)
            return true;

        // Multi-word / key-token coverage (ignores articles/prepositions and handles minor plural/typo differences)
        var userTokens = TokenizeSignificant(userRaw);
        var targetTokens = TokenizeSignificant(targetRaw);
        if (targetTokens.Count == 0 || userTokens.Count == 0)
            return false;

        if (userTokens.Count > targetTokens.Count + 2)
            return false;

        int matched = 0;
        foreach (var tTok in targetTokens)
        {
            if (userTokens.Any(uTok => TokensApproximatelyEqual(uTok, tTok)))
                matched++;
        }

        double coverage = (double)matched / targetTokens.Count;
        return targetTokens.Count == 1
            ? matched == 1
            : coverage >= 0.85 && userTokens.Count >= Math.Max(1, targetTokens.Count - 1);
    }

    private static string StripLeadingStopwords(string normalized)
    {
        var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (parts.Count > 1 && AnswerStopwords.Contains(parts[0]))
            parts.RemoveAt(0);
        return string.Join(' ', parts);
    }

    private static List<string> TokenizeSignificant(string raw)
    {
        return
        [
            .. NormalizeAnswerToken(raw)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(t => !AnswerStopwords.Contains(t))
                .Select(SingularizeToken),
        ];
    }

    private static string SingularizeToken(string token)
    {
        if (token.Length >= 5 && token.EndsWith("ies", StringComparison.Ordinal))
            return token[..^3] + "y";
        if (
            token.Length >= 4
            && token.EndsWith('s')
            && !token.EndsWith("ss", StringComparison.Ordinal)
        )
            return token[..^1];
        return token;
    }

    private static bool TokensApproximatelyEqual(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return true;
        if (a.Length < 5 || b.Length < 5)
            return false;
        return ComputeLevenshteinDistance(a, b) <= 1;
    }

    private static int ComputeLevenshteinDistance(string s, string t)
    {
        int n = s.Length,
            m = t.Length;
        if (Math.Abs(n - m) > 4)
            return Math.Abs(n - m);

        var prev = new int[m + 1];
        var curr = new int[m + 1];
        for (int j = 0; j <= m; j++)
            prev[j] = j;

        for (int i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return prev[m];
    }

    private static string StripOptionPrefix(string text)
    {
        var t = text.Trim();
        if (
            t.Length >= 3
            && char.IsLetter(t[0])
            && (t[1] is ')' or '.' or ':')
            && char.IsWhiteSpace(t[2])
        )
            return t[3..].Trim();
        return t;
    }

    private static string NormalizeAnswerToken(string raw)
    {
        var chars = raw.Trim()
            .Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))
            .ToArray();
        return string.Join(
                ' ',
                new string(chars).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            )
            .ToLowerInvariant();
    }
}

public sealed record QuizInProgressAnswer(string Answer, bool IsCorrect);

/// <summary>
/// Persistent quiz session stored in SQLite (scholarflow.db).
/// </summary>
public sealed class QuizSessionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string CourseFolder { get; set; } = string.Empty;
    public string CourseTitle { get; set; } = string.Empty;
    public string? Term { get; set; }
    public string Title { get; set; } = string.Empty;
    public string SourcePdfPath { get; set; } = string.Empty;
    public long? SourcePdfMtimeTicks { get; set; }
    public string ModelUsed { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public int AttemptCount { get; set; }
    public double? LastScorePercent { get; set; }
    public int? InProgressQuestionIndex { get; set; }
    public Dictionary<string, QuizInProgressAnswer> InProgressAnswers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<QuizQuestionRecord> Questions { get; set; } = [];

    public bool HasInProgressAttempt =>
        Questions.Count > 0 && (InProgressAnswers.Count > 0 || (InProgressQuestionIndex ?? 0) > 0);

    public bool IsSourcePdfStale()
    {
        if (string.IsNullOrWhiteSpace(SourcePdfPath) || !File.Exists(SourcePdfPath))
            return false;
        if (!SourcePdfPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return false;

        var currentTicks = File.GetLastWriteTimeUtc(SourcePdfPath).Ticks;
        if (SourcePdfMtimeTicks.HasValue)
            return SourcePdfMtimeTicks.Value != currentTicks;

        // For legacy sessions without SourcePdfMtimeTicks, compare PDF write time vs quiz creation time
        return File.GetLastWriteTimeUtc(SourcePdfPath) > CreatedAtUtc.UtcDateTime.AddSeconds(5);
    }
}

public sealed record PdfPageRange(int StartPage, int EndPage)
{
    public bool Contains(int page) => page >= StartPage && page <= EndPage;
}

public sealed record PdfOutlineSectionInfo(
    string Title,
    string? Term,
    int Level,
    int StartPage,
    int EndPage
)
{
    public int PageCount => Math.Max(1, EndPage - StartPage + 1);
}

public sealed class QuizGenerationRequest
{
    public string BasinRoot { get; set; } = string.Empty;
    public string CourseFolder { get; set; } = string.Empty;
    public string CourseTitle { get; set; } = string.Empty;
    public string? Term { get; set; }
    public string SourcePdfPath { get; set; } = string.Empty;

    /// <summary>Non-PDF study sources (code, markdown, text, images).</summary>
    public List<string> SourceFilePaths { get; set; } = [];

    /// <summary>When true and both SourcePdfPath and SourceFilePaths are set, combines PDF pages + files in one quiz.</summary>
    public bool IncludePdfWithFileSources { get; set; }
    public bool HasFileSources => SourceFilePaths.Count > 0;
    public bool HasPdfSource =>
        !string.IsNullOrWhiteSpace(SourcePdfPath)
        && File.Exists(SourcePdfPath)
        && (!HasFileSources || IncludePdfWithFileSources);
    public bool IsMixedSources => HasFileSources && HasPdfSource;
    public string? SectionFilter { get; set; }
    public int? StartPage { get; set; }
    public int? EndPage { get; set; }
    public List<PdfPageRange> SelectedRanges { get; set; } = [];
    public int QuestionCount { get; set; } = 10;
    public QuizPresetMode Preset { get; set; } = QuizPresetMode.ExamSimulator;
    public QuestionAllocationMode AllocationMode { get; set; } = QuestionAllocationMode.Percentages;
    public Dictionary<QuizQuestionKind, int> KindAllocations { get; set; } =
        GetDefaultAllocationsForPreset(QuizPresetMode.ExamSimulator);
    public QuizContentModalityMode ModalityMode { get; set; } = QuizContentModalityMode.AutoDetect;

    /// <summary>
    /// Multi-select modality flags (Code, MathLatex, Diagrams). When non-empty (and ModalityMode is not StandardProseOnly),
    /// enables all selected modalities simultaneously while guarding against overfitting on prose sections.
    /// </summary>
    public QuizContentModalityFlags SelectedModalityFlags { get; set; } =
        QuizContentModalityFlags.None;

    /// <summary>
    /// Optional configured academic terms for the active basin (empty on Local-Only basins without terms).
    /// </summary>
    public List<string> AvailableTerms { get; set; } = [];

    public (
        bool IsProseOnly,
        bool IsAutoDetect,
        QuizContentModalityFlags ExplicitFlags
    ) ResolveModalitySelection()
    {
        if (ModalityMode == QuizContentModalityMode.StandardProseOnly)
            return (true, false, QuizContentModalityFlags.None);

        var flags = SelectedModalityFlags;
        flags |= ModalityMode switch
        {
            QuizContentModalityMode.IncludeCodeSnippets => QuizContentModalityFlags.Code,
            QuizContentModalityMode.IncludeMathLatex => QuizContentModalityFlags.MathLatex,
            QuizContentModalityMode.IncludeDiagrams => QuizContentModalityFlags.Diagrams,
            _ => QuizContentModalityFlags.None,
        };

        return flags == QuizContentModalityFlags.None
            ? (false, true, QuizContentModalityFlags.None)
            : (false, false, flags);
    }

    public CognitiveMixPreset CognitiveMix { get; set; } = CognitiveMixPreset.Balanced;

    /// <summary>
    /// Customizable cognitive depth weights (Recall / Comprehension / Application / Analysis).
    /// When null or empty, falls back to <see cref="CognitiveMix"/> weights.
    /// </summary>
    public Dictionary<CognitiveLevel, int>? CognitiveAllocations { get; set; }
    public List<QuestionArchetype> SelectedArchetypes { get; set; } = [];
    public bool FocusWeakSpots { get; set; }
    public bool EnableContrastiveExplanations { get; set; } = true;
    public bool IncludeProgressiveHints { get; set; } = true;
    public List<string> ExcludePromptsOrTopics { get; set; } = [];
    public string? CustomInstructions { get; set; }
    public string? AppendToQuizId { get; set; }
    public string ProviderBaseUrl { get; set; } =
        "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
    public string? ApiKey { get; set; }
    public string ModelName { get; set; } = "gemini-2.5-flash";
    public bool UseOfflineHeuristicFallback { get; set; }

    public static Dictionary<CognitiveLevel, int> GetDefaultCognitiveAllocationsForMix(
        CognitiveMixPreset mix
    ) =>
        mix switch
        {
            CognitiveMixPreset.Foundations => new()
            {
                [CognitiveLevel.Recall] = 40,
                [CognitiveLevel.Comprehension] = 35,
                [CognitiveLevel.Application] = 20,
                [CognitiveLevel.Analysis] = 5,
            },
            CognitiveMixPreset.ExamApplication => new()
            {
                [CognitiveLevel.Recall] = 10,
                [CognitiveLevel.Comprehension] = 20,
                [CognitiveLevel.Application] = 40,
                [CognitiveLevel.Analysis] = 30,
            },
            CognitiveMixPreset.DeepMastery => new()
            {
                [CognitiveLevel.Recall] = 5,
                [CognitiveLevel.Comprehension] = 15,
                [CognitiveLevel.Application] = 35,
                [CognitiveLevel.Analysis] = 45,
            },
            _ => new()
            {
                [CognitiveLevel.Recall] = 20,
                [CognitiveLevel.Comprehension] = 30,
                [CognitiveLevel.Application] = 30,
                [CognitiveLevel.Analysis] = 20,
            },
        };

    public static Dictionary<CognitiveLevel, int> GetDefaultCognitiveAllocationsForPreset(
        QuizPresetMode preset
    ) =>
        preset switch
        {
            QuizPresetMode.QuickReview => GetDefaultCognitiveAllocationsForMix(
                CognitiveMixPreset.Foundations
            ),
            QuizPresetMode.WeakTopicDrill or QuizPresetMode.CodeLab =>
                GetDefaultCognitiveAllocationsForMix(CognitiveMixPreset.ExamApplication),
            QuizPresetMode.DeepMastery => GetDefaultCognitiveAllocationsForMix(
                CognitiveMixPreset.DeepMastery
            ),
            _ => GetDefaultCognitiveAllocationsForMix(CognitiveMixPreset.Balanced),
        };

    public Dictionary<CognitiveLevel, int> ResolveCognitivePercentages()
    {
        var allLevels = Enum.GetValues<CognitiveLevel>();
        if (
            CognitiveAllocations is null
            || CognitiveAllocations.Values.Sum(v => Math.Max(0, v)) <= 0
        )
            return GetDefaultCognitiveAllocationsForMix(CognitiveMix);

        int sum = allLevels.Sum(l => Math.Max(0, CognitiveAllocations.GetValueOrDefault(l, 0)));
        if (sum == 100)
        {
            return allLevels.ToDictionary(
                l => l,
                l => Math.Max(0, CognitiveAllocations.GetValueOrDefault(l, 0))
            );
        }

        // Normalize arbitrary positive weights to percentages summing to 100
        var scaled = new Dictionary<CognitiveLevel, int>();
        var remainders = new List<(CognitiveLevel Level, double Rem, int Weight)>();
        int allocated = 0;
        foreach (var l in allLevels)
        {
            int w = Math.Max(0, CognitiveAllocations.GetValueOrDefault(l, 0));
            double exact = w * 100.0 / sum;
            int fl = (int)Math.Floor(exact);
            scaled[l] = fl;
            allocated += fl;
            remainders.Add((l, exact - fl, w));
        }
        foreach (
            var (Level, Rem, Weight) in remainders
                .OrderByDescending(r => r.Rem)
                .ThenByDescending(r => r.Weight)
                .Take(Math.Max(0, 100 - allocated))
        )
        {
            scaled[Level]++;
        }
        return scaled;
    }

    public Dictionary<CognitiveLevel, int> ResolveExactCognitiveCounts(
        int? targetTotalOverride = null
    )
    {
        int total = Math.Clamp(targetTotalOverride ?? QuestionCount, 1, 150);
        var pcts = ResolveCognitivePercentages();
        var allLevels = Enum.GetValues<CognitiveLevel>();
        var result = allLevels.ToDictionary(l => l, _ => 0);
        int sum = Math.Max(1, pcts.Values.Sum());

        var remainders = new List<(CognitiveLevel Level, double Rem, int Weight)>();
        int allocated = 0;
        foreach (var l in allLevels)
        {
            int w = Math.Max(0, pcts.GetValueOrDefault(l, 0));
            if (w == 0)
                continue;
            double exact = (double)w * total / sum;
            int fl = (int)Math.Floor(exact);
            result[l] = fl;
            allocated += fl;
            remainders.Add((l, exact - fl, w));
        }
        foreach (
            var (Level, Rem, Weight) in remainders
                .OrderByDescending(r => r.Rem)
                .ThenByDescending(r => r.Weight)
                .Take(Math.Max(0, total - allocated))
        )
        {
            result[Level]++;
        }
        return result;
    }

    public static Dictionary<QuizQuestionKind, int> GetDefaultAllocationsForPreset(
        QuizPresetMode preset,
        QuestionAllocationMode mode = QuestionAllocationMode.Percentages
    )
    {
        if (preset == QuizPresetMode.CodeLab)
            return GetCodeAwareAllocations(mode, 10);

        if (mode == QuestionAllocationMode.ExactCounts)
        {
            return preset switch
            {
                QuizPresetMode.QuickReview => new()
                {
                    [QuizQuestionKind.MultipleChoice] = 5,
                    [QuizQuestionKind.Situational] = 0,
                    [QuizQuestionKind.Identification] = 3,
                    [QuizQuestionKind.TrueFalse] = 2,
                    [QuizQuestionKind.FillInTheBlank] = 0,
                    [QuizQuestionKind.Ordering] = 0,
                },
                QuizPresetMode.WeakTopicDrill => new()
                {
                    [QuizQuestionKind.MultipleChoice] = 2,
                    [QuizQuestionKind.Situational] = 3,
                    [QuizQuestionKind.Identification] = 3,
                    [QuizQuestionKind.TrueFalse] = 0,
                    [QuizQuestionKind.FillInTheBlank] = 2,
                    [QuizQuestionKind.Ordering] = 0,
                },
                QuizPresetMode.DeepMastery => new()
                {
                    [QuizQuestionKind.MultipleChoice] = 2,
                    [QuizQuestionKind.Situational] = 3,
                    [QuizQuestionKind.Identification] = 2,
                    [QuizQuestionKind.TrueFalse] = 0,
                    [QuizQuestionKind.FillInTheBlank] = 1,
                    [QuizQuestionKind.Ordering] = 2,
                },
                _ => new()
                {
                    [QuizQuestionKind.MultipleChoice] = 3,
                    [QuizQuestionKind.Situational] = 2,
                    [QuizQuestionKind.Identification] = 2,
                    [QuizQuestionKind.TrueFalse] = 1,
                    [QuizQuestionKind.FillInTheBlank] = 1,
                    [QuizQuestionKind.Ordering] = 1,
                },
            };
        }

        return preset switch
        {
            QuizPresetMode.QuickReview => new()
            {
                [QuizQuestionKind.MultipleChoice] = 50,
                [QuizQuestionKind.Situational] = 0,
                [QuizQuestionKind.Identification] = 30,
                [QuizQuestionKind.TrueFalse] = 20,
                [QuizQuestionKind.FillInTheBlank] = 0,
                [QuizQuestionKind.Ordering] = 0,
            },
            QuizPresetMode.WeakTopicDrill => new()
            {
                [QuizQuestionKind.MultipleChoice] = 20,
                [QuizQuestionKind.Situational] = 30,
                [QuizQuestionKind.Identification] = 30,
                [QuizQuestionKind.TrueFalse] = 0,
                [QuizQuestionKind.FillInTheBlank] = 20,
                [QuizQuestionKind.Ordering] = 0,
            },
            QuizPresetMode.DeepMastery => new()
            {
                [QuizQuestionKind.MultipleChoice] = 20,
                [QuizQuestionKind.Situational] = 30,
                [QuizQuestionKind.Identification] = 20,
                [QuizQuestionKind.TrueFalse] = 0,
                [QuizQuestionKind.FillInTheBlank] = 10,
                [QuizQuestionKind.Ordering] = 20,
            },
            _ => new()
            {
                [QuizQuestionKind.MultipleChoice] = 30,
                [QuizQuestionKind.Situational] = 20,
                [QuizQuestionKind.Identification] = 20,
                [QuizQuestionKind.TrueFalse] = 10,
                [QuizQuestionKind.FillInTheBlank] = 10,
                [QuizQuestionKind.Ordering] = 10,
            },
        };
    }

    /// <summary>
    /// Code-oriented default mix emphasizing trace/output prediction (Identification),
    /// code completion (FillInTheBlank), bug/edge-case spotting (Situational), and execution ordering (Ordering).
    /// </summary>
    public static Dictionary<QuizQuestionKind, int> GetCodeAwareAllocations(
        QuestionAllocationMode mode = QuestionAllocationMode.Percentages,
        int targetCount = 10
    )
    {
        var weights = new Dictionary<QuizQuestionKind, int>
        {
            [QuizQuestionKind.MultipleChoice] = 20,
            [QuizQuestionKind.Situational] = 15,
            [QuizQuestionKind.Identification] = 25,
            [QuizQuestionKind.TrueFalse] = 10,
            [QuizQuestionKind.FillInTheBlank] = 20,
            [QuizQuestionKind.Ordering] = 10,
        };

        return mode == QuestionAllocationMode.ExactCounts
            ? ScaleAllocationsToTarget(weights, Math.Clamp(targetCount, 1, 150))
            : weights;
    }

    /// <summary>
    /// Resolves either exact counts or percentage weights into deterministic per-kind item counts summing to totalCount.
    /// </summary>
    public Dictionary<QuizQuestionKind, int> ResolveExactKindCounts(int? targetTotalOverride = null)
    {
        var allKinds = Enum.GetValues<QuizQuestionKind>();
        var result = allKinds.ToDictionary(k => k, _ => 0);

        if (AllocationMode == QuestionAllocationMode.ExactCounts)
        {
            int explicitSum = 0;
            foreach (var k in allKinds)
            {
                int val = Math.Max(0, KindAllocations.GetValueOrDefault(k, 0));
                result[k] = val;
                explicitSum += val;
            }

            if (explicitSum == 0)
            {
                int fallbackTotal = Math.Clamp(targetTotalOverride ?? QuestionCount, 1, 150);
                result[QuizQuestionKind.MultipleChoice] = fallbackTotal;
                return result;
            }

            if (
                targetTotalOverride.HasValue
                && targetTotalOverride.Value > 0
                && targetTotalOverride.Value != explicitSum
            )
            {
                return ScaleAllocationsToTarget(result, targetTotalOverride.Value);
            }

            return result;
        }

        int targetTotal = Math.Clamp(targetTotalOverride ?? QuestionCount, 1, 150);
        return ScaleAllocationsToTarget(KindAllocations, targetTotal);
    }

    private static Dictionary<QuizQuestionKind, int> ScaleAllocationsToTarget(
        IReadOnlyDictionary<QuizQuestionKind, int> weights,
        int targetTotal
    )
    {
        var allKinds = Enum.GetValues<QuizQuestionKind>();
        var result = allKinds.ToDictionary(k => k, _ => 0);

        int weightSum = allKinds.Sum(k => Math.Max(0, weights.GetValueOrDefault(k, 0)));
        if (weightSum <= 0)
        {
            result[QuizQuestionKind.MultipleChoice] = targetTotal;
            return result;
        }

        var remainders = new List<(QuizQuestionKind Kind, double Remainder, int Weight)>();
        int allocated = 0;

        foreach (var k in allKinds)
        {
            int w = Math.Max(0, weights.GetValueOrDefault(k, 0));
            if (w == 0)
                continue;

            double exact = (double)w * targetTotal / weightSum;
            int floor = (int)Math.Floor(exact);
            result[k] = floor;
            allocated += floor;
            remainders.Add((k, exact - floor, w));
        }

        int leftover = targetTotal - allocated;
        foreach (
            var (Kind, Remainder, Weight) in remainders
                .OrderByDescending(r => r.Remainder)
                .ThenByDescending(r => r.Weight)
                .ThenBy(r => (int)r.Kind)
                .Take(Math.Max(0, leftover))
        )
        {
            result[Kind]++;
        }

        return result;
    }
}
