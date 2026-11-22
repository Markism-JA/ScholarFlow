using ScholarFlow.Core.Quiz;

namespace ScholarFlow.Desktop.ViewModels;

public sealed partial class QuizStudioViewModel
{
    // Stage 2: Integrated Assessment Blueprint State
    public CognitiveMixPreset CognitiveMix { get; private set; } = CognitiveMixPreset.Balanced;
    public Dictionary<CognitiveLevel, int> CognitiveAllocations { get; private set; } =
        QuizGenerationRequest.GetDefaultCognitiveAllocationsForPreset(QuizPresetMode.ExamSimulator);
    public HashSet<QuestionArchetype> SelectedArchetypes { get; } = [];
    public bool FocusWeakSpots { get; private set; }
    public bool EnableContrastiveExplanations { get; private set; } = true;
    public bool IncludeProgressiveHints { get; private set; } = true;

    // Stage 2: Runner State (Progressive Hints & Opt-in Confidence Calibration)
    public bool IsHintRevealedForCurrent { get; private set; }
    public bool UseConfidenceCalibration { get; private set; }
    public ConfidenceLevel SelectedConfidence { get; private set; } = ConfidenceLevel.None;
    public string? LastConfidenceFeedback { get; private set; }

    // Zero-I/O In-Memory Caches for Builder Weak Topics & Organizer Quality Critic
    private IReadOnlyList<QuizSessionRecord>? _weakTagsSourceRef;
    private string? _weakTagsCourseKey;
    private IReadOnlyList<string> _cachedWeakTopics = [];

    private string? _cachedQualityQuizId;
    private int _cachedQualityQuestionCount = -1;
    private int _cachedQualityVersion;
    private int _organizerQualityVersion;
    private QuizQualityReport? _cachedQualityReport;

    /// <summary>
    /// Computes weak topic tags for the active course purely in-memory from <see cref="SavedQuizzes"/>
    /// with memoization — zero SQLite I/O on the UI render thread.
    /// </summary>
    public IReadOnlyList<string> GetCourseWeakTopicTags()
    {
        var courseKey = SelectedCourse?.FolderName ?? "";
        if (
            ReferenceEquals(_weakTagsSourceRef, SavedQuizzes)
            && string.Equals(_weakTagsCourseKey, courseKey, StringComparison.OrdinalIgnoreCase)
        )
        {
            return _cachedWeakTopics;
        }

        _weakTagsSourceRef = SavedQuizzes;
        _weakTagsCourseKey = courseKey;

        var relevantQuizzes = string.IsNullOrWhiteSpace(courseKey)
            ? SavedQuizzes
            : SavedQuizzes.Where(q =>
                string.Equals(q.CourseFolder, courseKey, StringComparison.OrdinalIgnoreCase)
            );

        _cachedWeakTopics =
        [
            .. relevantQuizzes
                .SelectMany(q => q.Questions)
                .Where(qq =>
                    !string.IsNullOrWhiteSpace(qq.TopicTag)
                    && (qq.SrsLapses > 0 || qq.LastAnsweredCorrect == false)
                )
                .GroupBy(
                    qq => qq.TopicTag.Trim().ToLowerInvariant(),
                    StringComparer.OrdinalIgnoreCase
                )
                .OrderByDescending(g => g.Sum(x => x.SrsLapses))
                .ThenByDescending(g => g.Count())
                .Take(8)
                .Select(g => g.Key),
        ];

        return _cachedWeakTopics;
    }

    public void InvalidateOrganizerQualityCache()
    {
        _organizerQualityVersion++;
        _cachedQualityReport = null;
    }

    /// <summary>
    /// Memoized local quality critic audit for the current Deck Organizer session so virtualized card
    /// rendering and scrolling perform O(1) dictionary lookups instead of repeated regex scans.
    /// </summary>
    public QuizQualityReport GetCachedOrganizerQualityReport()
    {
        var deck = OrganizerQuiz;
        if (deck is null || deck.Questions.Count == 0)
            return new QuizQualityReport([]);

        if (
            _cachedQualityReport is not null
            && string.Equals(_cachedQualityQuizId, deck.Id, StringComparison.OrdinalIgnoreCase)
            && _cachedQualityQuestionCount == deck.Questions.Count
            && _cachedQualityVersion == _organizerQualityVersion
        )
        {
            return _cachedQualityReport;
        }

        _cachedQualityQuizId = deck.Id;
        _cachedQualityQuestionCount = deck.Questions.Count;
        _cachedQualityVersion = _organizerQualityVersion;
        _cachedQualityReport = QuizQualityCritic.Audit(deck);
        return _cachedQualityReport;
    }

    public void SetCognitiveMix(CognitiveMixPreset preset)
    {
        CognitiveMix = preset;
        CognitiveAllocations = QuizGenerationRequest.GetDefaultCognitiveAllocationsForMix(preset);
        RefreshLivePromptPreviewIfActive();
        _mainVm.RequestUiRefresh();
    }

    public void SetCognitiveAllocation(CognitiveLevel level, int value, bool notify = true)
    {
        CognitiveAllocations[level] = Math.Clamp(value, 0, 100);
        SelectedPreset = QuizPresetMode.Custom;
        RefreshLivePromptPreviewIfActive();
        if (notify)
            _mainVm.RequestUiRefresh();
    }

    public void StepCognitiveAllocation(CognitiveLevel level, int delta)
    {
        int current = CognitiveAllocations.GetValueOrDefault(level, 0);
        SetCognitiveAllocation(level, Math.Clamp(current + (delta * 5), 0, 100), notify: true);
    }

    public Dictionary<CognitiveLevel, int> GetResolvedExactCognitiveCounts() =>
        BuildCurrentRequest().ResolveExactCognitiveCounts(QuestionCount);

    public Dictionary<CognitiveLevel, int> GetResolvedCognitivePercentages() =>
        BuildCurrentRequest().ResolveCognitivePercentages();

    public void ToggleArchetype(QuestionArchetype archetype)
    {
        if (!SelectedArchetypes.Add(archetype))
            SelectedArchetypes.Remove(archetype);
        RefreshLivePromptPreviewIfActive();
        _mainVm.RequestUiRefresh();
    }

    public void ClearArchetypes()
    {
        if (SelectedArchetypes.Count == 0)
            return;
        SelectedArchetypes.Clear();
        RefreshLivePromptPreviewIfActive();
        _mainVm.RequestUiRefresh();
    }

    public void ToggleFocusWeakSpots()
    {
        FocusWeakSpots = !FocusWeakSpots;
        RefreshLivePromptPreviewIfActive();
        _mainVm.RequestUiRefresh();
    }

    public void ToggleContrastiveExplanations()
    {
        EnableContrastiveExplanations = !EnableContrastiveExplanations;
        RefreshLivePromptPreviewIfActive();
        _mainVm.RequestUiRefresh();
    }

    public void ToggleProgressiveHints()
    {
        IncludeProgressiveHints = !IncludeProgressiveHints;
        RefreshLivePromptPreviewIfActive();
        _mainVm.RequestUiRefresh();
    }

    public void RevealCurrentHint()
    {
        if (IsHintRevealedForCurrent)
            return;
        IsHintRevealedForCurrent = true;
        _mainVm.RequestUiRefresh();
    }

    public void ToggleConfidenceCalibration()
    {
        UseConfidenceCalibration = !UseConfidenceCalibration;
        if (!UseConfidenceCalibration)
            SelectedConfidence = ConfidenceLevel.None;
        _mainVm.RequestUiRefresh();
    }

    public void SetSelectedConfidence(ConfidenceLevel level)
    {
        SelectedConfidence = SelectedConfidence == level ? ConfidenceLevel.None : level;
        _mainVm.RequestUiRefresh();
    }

    private void ResetPerQuestionSuperEngineState()
    {
        IsHintRevealedForCurrent = false;
        SelectedConfidence = ConfidenceLevel.None;
        LastConfidenceFeedback = null;
    }

    private static string? FormatConfidenceCalibrationFeedback(
        ConfidenceLevel confidence,
        bool isCorrect,
        double newIntervalDays
    )
    {
        if (confidence == ConfidenceLevel.None)
            return null;

        return (confidence, isCorrect) switch
        {
            (ConfidenceLevel.Certain, false) =>
                "Hypercorrection penalty (Certain + Missed): +2 lapses, reset to 1d for immediate re-testing.",
            (ConfidenceLevel.Guess, true) =>
                $"Lucky guess dampened (Guess + Correct): kept short interval ({newIntervalDays:0.#}d) to consolidate recall.",
            (ConfidenceLevel.Unsure, true) =>
                $"Uncertain recall (Unsure + Correct): moderate interval ({newIntervalDays:0.#}d).",
            (ConfidenceLevel.Certain, true) =>
                $"Calibrated mastery (Certain + Correct): boosted interval ({newIntervalDays:0.#}d).",
            _ => null,
        };
    }

    /// <summary>
    /// Runs the deterministic zero-token local quality critic on a quiz session.
    /// </summary>
    public static QuizQualityReport AuditQuizQuality(QuizSessionRecord? session) =>
        QuizQualityCritic.Audit(session);

    /// <summary>
    /// Flags all questions with local quality-critic warnings/errors in Deck Organizer
    /// and opens the Deck Fix with AI modal with pre-populated repair directives.
    /// </summary>
    public void FlagAllQualityIssuesForAiFix()
    {
        if (OrganizerQuiz is null)
            return;

        var report = GetCachedOrganizerQualityReport();
        if (!report.HasIssues)
        {
            StatusMessage = "No quality issues detected in this deck.";
            IsStatusError = false;
            _mainVm.RequestUiRefresh();
            return;
        }

        DeckAiFixSelectedIds.Clear();
        foreach (var qid in report.FlaggedQuestionIds)
            DeckAiFixSelectedIds.Add(qid);

        var distinctCodes = report.Issues.Select(i => i.Code).Distinct().ToList();
        var directives = new List<string>
        {
            $"Repair the {report.FlaggedQuestionIds.Count} quality-critic flagged question(s) ({string.Join(", ", distinctCodes)}):",
        };
        foreach (var issue in report.Issues.Take(12))
        {
            int qNum =
                OrganizerQuiz.Questions.FindIndex(x =>
                    string.Equals(x.Id, issue.QuestionId, StringComparison.OrdinalIgnoreCase)
                ) + 1;
            directives.Add($"- Q{qNum} [{issue.Code}]: {issue.Message}");
        }

        DeckAiFixInstructions = string.Join("\n", directives);
        OpenDeckAiFixModal();
    }

    /// <summary>
    /// Launches an interleaved multi-deck / cross-course review session that mixes
    /// due, weak, and unattempted items across decks without repeating consecutive topics.
    /// </summary>
    public void StartInterleavedReviewSession(
        bool allCourses = false,
        bool selectedDecksOnly = false,
        int maxItems = 25
    )
    {
        IEnumerable<QuizSessionRecord> sourceDecks = SavedQuizzes;
        if (selectedDecksOnly && SelectedQuizIdsForMerge.Count > 0)
        {
            sourceDecks = sourceDecks.Where(q => SelectedQuizIdsForMerge.Contains(q.Id));
        }
        else if (!allCourses && SelectedCourse is not null)
        {
            sourceDecks = sourceDecks.Where(q =>
                string.Equals(
                    q.CourseFolder,
                    SelectedCourse.FolderName,
                    StringComparison.OrdinalIgnoreCase
                )
            );
        }

        var deckList = sourceDecks.ToList();
        var pool = deckList.SelectMany(d => d.Questions).ToList();
        if (pool.Count == 0)
        {
            StatusMessage = allCourses
                ? "No saved quiz questions yet. Generate or import a quiz first."
                : "No saved quiz questions for this subject yet. Try Interleaved Review across all subjects!";
            IsStatusError = false;
            _mainVm.RequestUiRefresh();
            return;
        }

        var interleaved = QuizBlueprintBuilder.BuildInterleavedReviewOrder(pool, maxItems);
        var distinctTopics = interleaved
            .Select(q => q.TopicTag)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        string scopeLabel =
            selectedDecksOnly ? $"{deckList.Count} Selected Decks"
            : allCourses ? "All Subjects"
            : SelectedCourse?.Title ?? "Subject";

        var session = new QuizSessionRecord
        {
            Id = "srs-deck",
            CourseFolder = allCourses ? "" : SelectedCourse?.FolderName ?? "",
            CourseTitle = scopeLabel,
            Title =
                $"Interleaved Review · {scopeLabel} ({interleaved.Count} items · {distinctTopics} topics)",
            SourcePdfPath = interleaved[0].SourcePdfPath,
            ModelUsed = "Interleaved Active Recall",
            Questions = interleaved,
        };

        StartQuizSession(
            session,
            shuffleQuestionsOverride: false,
            shuffleOptionsOverride: ShuffleOptionsOnStart
        );
    }
}
