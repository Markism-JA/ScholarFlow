using Avalonia.Media.Imaging;
using ScholarFlow.Core.Database;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Quiz;

namespace ScholarFlow.Desktop.ViewModels;

public enum QuizStudioSubTab
{
    Builder,
    Catalog,
    Organizer,
}

public enum QuizBuilderOutputMode
{
    AiApi,
    WebJson,
    OfflineSmart,
}

/// <summary>
/// ViewModel for the Evidence-Grounded PDF Quiz &amp; Active Recall Studio.
/// Supports Google AI Studio (Gemini OpenAI-compatible API), Local Ollama, Manual Gemini Web JSON Import,
/// Offline Smart PDF Analysis, SM-2/FSRS Spaced Repetition, live visual PDF scope previewing,
/// and one-click jump to highlighted statements in the PDF Reader.
/// </summary>
public enum SnipCaptureMode
{
    PinHighlight,
    CaptureImage,
}

public enum OrganizerHealthFilter
{
    All,
    QualityIssues,
    MissingEvidence,
    HasRichMedia,
    FlaggedForAi,
}

public enum OutlineSelectionState
{
    Unchecked,
    Indeterminate,
    Checked,
}

public sealed class PdfOutlineTreeNode
{
    public required PdfOutlineSectionInfo Section { get; init; }
    public List<PdfOutlineTreeNode> Children { get; } = [];
    public bool IsExpanded { get; set; } = true;
}

public sealed partial class QuizStudioViewModel(MainWindowViewModel mainVm)
{
    private readonly MainWindowViewModel _mainVm = mainVm;
    private readonly QuizLlmService _llmService = new();
    private readonly Dictionary<
        string,
        (int TotalPages, IReadOnlyList<PdfOutlineSectionInfo> Sections)
    > _outlineCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Path, int Page), Bitmap> _previewBitmapCache = [];
    private string? _cachedBytesPath;
    private byte[]? _cachedPdfBytes;

    private sealed class CourseStudioState
    {
        public QuizStudioSubTab ActiveSubTab { get; set; } = QuizStudioSubTab.Catalog;
        public string? SelectedPdfPath { get; set; }
        public bool ShowIndividualHandouts { get; set; }
        public string? SelectedTerm { get; set; }
        public int? StartPage { get; set; }
        public int? EndPage { get; set; }
        public HashSet<string> SelectedSectionKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SelectedFileSourcePaths { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public bool IncludePdfWithFiles { get; set; }
        public bool ShowStudyFiles { get; set; }
        public bool IsCodeAwareMixActive { get; set; }
        public int PreviewScopeIndex { get; set; }
        public QuizBuilderOutputMode BuilderOutputMode { get; set; } = QuizBuilderOutputMode.AiApi;
        public int QuestionCount { get; set; } = 10;
        public QuizPresetMode SelectedPreset { get; set; } = QuizPresetMode.ExamSimulator;
        public QuestionAllocationMode AllocationMode { get; set; } =
            QuestionAllocationMode.Percentages;
        public Dictionary<QuizQuestionKind, int> KindAllocations { get; set; } =
            QuizGenerationRequest.GetDefaultAllocationsForPreset(QuizPresetMode.ExamSimulator);
        public CognitiveMixPreset CognitiveMix { get; set; } = CognitiveMixPreset.Balanced;
        public Dictionary<CognitiveLevel, int> CognitiveAllocations { get; set; } =
            QuizGenerationRequest.GetDefaultCognitiveAllocationsForPreset(
                QuizPresetMode.ExamSimulator
            );
        public QuizContentModalityMode ModalityMode { get; set; } =
            QuizContentModalityMode.AutoDetect;
        public HashSet<QuestionArchetype> SelectedArchetypes { get; } = [];
        public bool FocusWeakSpots { get; set; }
        public bool EnableContrastiveExplanations { get; set; } = true;
        public bool IncludeProgressiveHints { get; set; } = true;
        public string CustomPromptInstructions { get; set; } = string.Empty;
        public QuizSessionRecord? OrganizerQuiz { get; set; }
        public bool IsManualJsonModalOpen { get; set; }
    }

    private readonly Dictionary<string, CourseStudioState> _courseStates = new(
        StringComparer.OrdinalIgnoreCase
    );

    public QuizStudioSubTab ActiveSubTab { get; private set; } = QuizStudioSubTab.Catalog;
    public BasinCourseEntry? SelectedCourse { get; private set; }
    public IReadOnlyList<(string Label, string FullPath, bool IsMaster)> AvailablePdfs
    {
        get;
        private set;
    } = [];
    public bool ShowIndividualHandouts { get; private set; }
    public string? SelectedPdfPath { get; private set; }
    public int PdfTotalPages { get; private set; }
    public IReadOnlyList<PdfOutlineSectionInfo> OutlineSections { get; private set; } = [];
    public IReadOnlyList<PdfOutlineTreeNode> OutlineSectionRoots { get; private set; } = [];
    public HashSet<string> SelectedSectionKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? SelectedTerm { get; private set; }
    public int? StartPage { get; set; }
    public int? EndPage { get; set; }
    public int QuestionCount { get; private set; } = 10;
    public QuizPresetMode SelectedPreset { get; private set; } = QuizPresetMode.ExamSimulator;
    public QuestionAllocationMode AllocationMode { get; private set; } =
        QuestionAllocationMode.Percentages;
    public Dictionary<QuizQuestionKind, int> KindAllocations { get; private set; } =
        QuizGenerationRequest.GetDefaultAllocationsForPreset(
            QuizPresetMode.ExamSimulator,
            QuestionAllocationMode.Percentages
        );
    public QuizBuilderOutputMode BuilderOutputMode { get; private set; } =
        QuizBuilderOutputMode.AiApi;
    public string? AppendTargetQuizId { get; private set; }
    public string? AppendTargetQuizTitle { get; private set; }
    public string CustomPromptInstructions { get; private set; } = string.Empty;
    public QuizContentModalityMode ModalityMode { get; private set; } =
        QuizContentModalityMode.AutoDetect;

    public IReadOnlyList<(int PageNumber, string SectionTitle, string? Term)> MatchedScopePages
    {
        get;
        private set;
    } = [];
    public int PreviewScopeIndex { get; private set; }
    public int ScopePreviewPageCount => MatchedScopePages.Count;
    public int CurrentPreviewPdfPage =>
        MatchedScopePages.Count > 0
            ? MatchedScopePages[
                Math.Clamp(PreviewScopeIndex, 0, MatchedScopePages.Count - 1)
            ].PageNumber
            : Math.Clamp(StartPage ?? 1, 1, Math.Max(1, PdfTotalPages));
    public string CurrentPreviewSectionTitle =>
        MatchedScopePages.Count > 0
            ? MatchedScopePages[
                Math.Clamp(PreviewScopeIndex, 0, MatchedScopePages.Count - 1)
            ].SectionTitle
            : (
                string.IsNullOrWhiteSpace(SelectedPdfPath)
                    ? "No PDF Selected"
                    : Path.GetFileNameWithoutExtension(SelectedPdfPath)
            );

    public bool CatalogShowAllCourses { get; private set; }
    public string? CatalogTermFilter { get; private set; }
    public HashSet<string> SelectedQuizIdsForMerge { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ShuffleQuestionsOnStart { get; private set; }
    public bool ShuffleOptionsOnStart { get; private set; } = true;

    // Deck Organizer State (Single & Multi-Quiz Reorganization)
    public QuizSessionRecord? OrganizerQuiz { get; private set; }
    public QuizQuestionKind? OrganizerKindFilter { get; private set; }
    public string? OrganizerTopicFilter { get; private set; }
    public OrganizerHealthFilter OrganizerHealthFilter { get; private set; } =
        OrganizerHealthFilter.All;
    public string OrganizerSearchQuery { get; private set; } = string.Empty;
    public string? SelectedOrganizerQuestionId { get; private set; }
    public bool IsOrganizerDirty { get; private set; }
    public bool IsOrganizerTocOpen { get; private set; } = true;
    public bool IsOrganizerFiltersOpen { get; private set; }
    public string? EditingQuestionId { get; private set; }
    public QuizQuestionRecord? EditingQuestionDraft { get; private set; }
    public bool IsEditingAdvancedOpen { get; private set; }
    public string? EditingPageTextPreview { get; private set; }
    public string? EditingGroundStatus { get; private set; }
    public bool IsDedicatedSnipperOpen { get; private set; }
    public bool IsLoadingSnipPage { get; private set; }
    public Avalonia.Media.Imaging.Bitmap? EditingSnipPageBitmap { get; private set; }
    public int EditingSnipPageNumber { get; private set; }
    public int EditingSnipTotalPages { get; private set; }
    public bool SnipFitToWidth { get; private set; } = true;
    public int SnipZoomPercent { get; private set; } = 100;
    public int PreviewZoomPercent { get; private set; } = 100;
    public bool IsSnipOutlineOpen { get; private set; } = true;
    public SnipCaptureMode SnipMode { get; private set; } = SnipCaptureMode.PinHighlight;
    public IReadOnlyList<string> SnipExtractedFigures { get; private set; } = [];
    public string? ZoomedImagePath { get; private set; }
    public IReadOnlyList<PdfBookmarkNode> SnipOutlineRoots { get; private set; } = [];
    private string? _snipOutlinePdfPath;
    public string? FlaggingQuestionId { get; private set; }
    public bool IsDeckAiFixModalOpen { get; private set; }
    public HashSet<string> DeckAiFixSelectedIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string DeckAiFixInstructions { get; set; } = string.Empty;
    public string DeckAiFixPromptPreview { get; private set; } = string.Empty;
    public string DeckAiFixJsonInput { get; set; } = string.Empty;
    public QuizBuilderOutputMode DeckAiFixOutputMode { get; set; } = QuizBuilderOutputMode.AiApi;
    public string FlagProblemDescription { get; set; } = string.Empty;
    public string FlagFixPromptPreview { get; private set; } = string.Empty;
    public string FlagFixJsonInput { get; set; } = string.Empty;

    public bool IsAiSettingsOpen { get; private set; }
    public bool IsManualJsonModalOpen { get; private set; }
    public string ManualJsonInput { get; set; } = string.Empty;
    public string GeneratedPromptPreview { get; private set; } = string.Empty;
    public int ManualPromptBudgetChars { get; private set; } = 160_000;
    public int ManualPromptTotalPages { get; private set; }
    public int ManualPromptIncludedPages { get; private set; }

    public bool IsGenerating { get; private set; }
    public bool IsInspectingPdf { get; private set; }
    public bool IsBuildingPrompt { get; private set; }
    public bool IsLoadingPreviewPage { get; private set; }
    public bool IsBusy => IsGenerating || IsInspectingPdf || IsBuildingPrompt;
    private CancellationTokenSource? _promptBuildCts;
    private readonly HashSet<(string Path, int Page)> _pendingPreviewRenders = [];
    public string StatusMessage { get; private set; } =
        "Select a course & PDF to generate an evidence-grounded assessment.";
    public bool IsStatusError { get; private set; }

    public IReadOnlyList<QuizSessionRecord> SavedQuizzes { get; private set; } = [];
    public IReadOnlyList<QuizQuestionRecord> AllDueReviewQuestions { get; private set; } = [];

    // Active Runner State
    public QuizSessionRecord? ActiveSession { get; private set; }
    public int CurrentQuestionIndex { get; private set; }
    public QuizQuestionRecord? CurrentQuestion =>
        ActiveSession is not null
        && CurrentQuestionIndex >= 0
        && CurrentQuestionIndex < ActiveSession.Questions.Count
            ? ActiveSession.Questions[CurrentQuestionIndex]
            : null;

    public string CurrentAnswerDraft { get; set; } = string.Empty;
    public bool IsAnswerRevealed { get; private set; }
    public bool IsCurrentAnswerCorrect { get; private set; }
    public bool IsShowingResultsSummary { get; private set; }

    public Dictionary<string, (string Answer, bool IsCorrect)> SessionResults { get; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> OrderingDrafts { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task RefreshAsync(
        BasinCourseEntry? preferredCourse = null,
        string? preferredPdfPath = null,
        int? focusPage = null
    )
    {
        var activeCourses = _mainVm.Courses.Where(c => !c.IsArchived).ToList();
        var previousCourse = SelectedCourse;
        BasinCourseEntry? targetCourse = SelectedCourse;

        if (preferredCourse is not null)
        {
            targetCourse = preferredCourse;
        }
        else if (
            targetCourse is null
            || !activeCourses.Any(c => c.FolderName == targetCourse.FolderName)
        )
        {
            targetCourse = _mainVm.ActiveCourse ?? activeCourses.FirstOrDefault();
        }

        if (
            previousCourse is not null
            && targetCourse is not null
            && !string.Equals(
                previousCourse.FolderName,
                targetCourse.FolderName,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            SaveActiveCourseState();
            SelectedCourse = targetCourse;
            RestoreCourseState(targetCourse);
        }
        else
        {
            SelectedCourse = targetCourse;
        }

        if (!string.IsNullOrWhiteSpace(preferredPdfPath) || focusPage.HasValue)
            ActiveSubTab = QuizStudioSubTab.Builder;

        if (ActiveSubTab == QuizStudioSubTab.Builder)
        {
            RebuildAvailablePdfsForCourse(preferredPdfPath ?? SelectedPdfPath);

            if (focusPage.HasValue && focusPage.Value > 0)
            {
                StartPage = focusPage.Value;
                EndPage = Math.Min(Math.Max(focusPage.Value, PdfTotalPages), focusPage.Value + 4);
                SelectedSectionKeys.Clear();
                RecomputeScopePreview();
            }
        }

        if (!string.IsNullOrWhiteSpace(_mainVm.BasinRoot))
        {
            SavedQuizzes = await ScholarFlowDatabase.LoadQuizSessionsAsync(_mainVm.BasinRoot);
            var today = DateOnly.FromDateTime(DateTime.Today);
            AllDueReviewQuestions =
            [
                .. SavedQuizzes
                    .SelectMany(q => q.Questions)
                    .Where(qq =>
                        !qq.SrsDueDate.HasValue
                        || qq.SrsDueDate.Value <= today
                        || qq.LastAnsweredCorrect == false
                    ),
            ];
        }
    }

    private void SaveActiveCourseState()
    {
        if (SelectedCourse is null)
            return;

        if (!_courseStates.TryGetValue(SelectedCourse.FolderName, out var state))
        {
            state = new CourseStudioState();
            _courseStates[SelectedCourse.FolderName] = state;
        }

        state.ActiveSubTab = ActiveSubTab;
        state.SelectedPdfPath = SelectedPdfPath;
        state.ShowIndividualHandouts = ShowIndividualHandouts;
        state.SelectedTerm = SelectedTerm;
        state.StartPage = StartPage;
        state.EndPage = EndPage;
        state.SelectedSectionKeys.Clear();
        foreach (var k in SelectedSectionKeys)
            state.SelectedSectionKeys.Add(k);
        state.SelectedFileSourcePaths.Clear();
        foreach (var fp in _selectedFileSourcePaths)
            state.SelectedFileSourcePaths.Add(fp);
        state.IncludePdfWithFiles = IncludePdfWithFiles;
        state.ShowStudyFiles = ShowStudyFiles;
        state.IsCodeAwareMixActive = IsCodeAwareMixActive;
        state.PreviewScopeIndex = PreviewScopeIndex;
        state.BuilderOutputMode = BuilderOutputMode;
        state.QuestionCount = QuestionCount;
        state.SelectedPreset = SelectedPreset;
        state.AllocationMode = AllocationMode;
        state.KindAllocations = new Dictionary<QuizQuestionKind, int>(KindAllocations);
        state.CognitiveMix = CognitiveMix;
        state.CognitiveAllocations = new Dictionary<CognitiveLevel, int>(CognitiveAllocations);
        state.ModalityMode = ModalityMode;
        state.SelectedArchetypes.Clear();
        foreach (var a in SelectedArchetypes)
            state.SelectedArchetypes.Add(a);
        state.FocusWeakSpots = FocusWeakSpots;
        state.EnableContrastiveExplanations = EnableContrastiveExplanations;
        state.IncludeProgressiveHints = IncludeProgressiveHints;
        state.CustomPromptInstructions = CustomPromptInstructions;
        state.OrganizerQuiz = OrganizerQuiz;
        state.IsManualJsonModalOpen = IsManualJsonModalOpen;
    }

    private void RestoreCourseState(BasinCourseEntry course)
    {
        if (_courseStates.TryGetValue(course.FolderName, out var state))
        {
            ActiveSubTab = state.ActiveSubTab;
            SelectedPdfPath = state.SelectedPdfPath;
            ShowIndividualHandouts = state.ShowIndividualHandouts;
            SelectedTerm = state.SelectedTerm;
            StartPage = state.StartPage;
            EndPage = state.EndPage;
            SelectedSectionKeys.Clear();
            foreach (var k in state.SelectedSectionKeys)
                SelectedSectionKeys.Add(k);
            _selectedFileSourcePaths.Clear();
            foreach (var fp in state.SelectedFileSourcePaths)
                _selectedFileSourcePaths.Add(fp);
            IncludePdfWithFiles = state.IncludePdfWithFiles;
            ShowStudyFiles = state.ShowStudyFiles;
            IsCodeAwareMixActive = state.IsCodeAwareMixActive;
            PreviewScopeIndex = state.PreviewScopeIndex;
            BuilderOutputMode = state.BuilderOutputMode;
            QuestionCount = state.QuestionCount;
            SelectedPreset = state.SelectedPreset;
            AllocationMode = state.AllocationMode;
            KindAllocations = new Dictionary<QuizQuestionKind, int>(state.KindAllocations);
            CognitiveMix = state.CognitiveMix;
            CognitiveAllocations = new Dictionary<CognitiveLevel, int>(state.CognitiveAllocations);
            ModalityMode = state.ModalityMode;
            SelectedArchetypes.Clear();
            foreach (var a in state.SelectedArchetypes)
                SelectedArchetypes.Add(a);
            FocusWeakSpots = state.FocusWeakSpots;
            EnableContrastiveExplanations = state.EnableContrastiveExplanations;
            IncludeProgressiveHints = state.IncludeProgressiveHints;
            CustomPromptInstructions = state.CustomPromptInstructions;
            OrganizerQuiz = state.OrganizerQuiz;
            IsManualJsonModalOpen = state.IsManualJsonModalOpen;
        }
        else
        {
            ActiveSubTab = QuizStudioSubTab.Catalog;
            SelectedPdfPath = null;
            ShowIndividualHandouts = false;
            SelectedTerm = null;
            StartPage = null;
            EndPage = null;
            SelectedSectionKeys.Clear();
            _selectedFileSourcePaths.Clear();
            IncludePdfWithFiles = false;
            ShowStudyFiles = false;
            IsCodeAwareMixActive = false;
            PreviewScopeIndex = 0;
            BuilderOutputMode = QuizBuilderOutputMode.AiApi;
            QuestionCount = 10;
            SelectedPreset = QuizPresetMode.ExamSimulator;
            AllocationMode = QuestionAllocationMode.Percentages;
            KindAllocations = QuizGenerationRequest.GetDefaultAllocationsForPreset(
                QuizPresetMode.ExamSimulator,
                QuestionAllocationMode.Percentages
            );
            CognitiveMix = CognitiveMixPreset.Balanced;
            CognitiveAllocations = QuizGenerationRequest.GetDefaultCognitiveAllocationsForPreset(
                QuizPresetMode.ExamSimulator
            );
            ModalityMode = QuizContentModalityMode.AutoDetect;
            SelectedArchetypes.Clear();
            FocusWeakSpots = false;
            EnableContrastiveExplanations = true;
            IncludeProgressiveHints = true;
            CustomPromptInstructions = string.Empty;
            OrganizerQuiz = null;
            IsManualJsonModalOpen = false;
        }
        InvalidateOrganizerQualityCache();
    }

    public void SetSubTab(QuizStudioSubTab tab)
    {
        ActiveSubTab = tab;
        if (tab == QuizStudioSubTab.Builder)
            RebuildAvailablePdfsForCourse(SelectedPdfPath);
        _mainVm.RequestUiRefresh();
    }

    /// <summary>
    /// Clears all basin-scoped Quiz Studio, Builder, Catalog, Runner, Snipper, and Deck Organizer
    /// state so switching basins never leaks decks, PDFs, or active sessions across workspaces.
    /// </summary>
    public void ResetForBasinSwitch()
    {
        _promptBuildCts?.Cancel();
        _promptBuildCts?.Dispose();
        _promptBuildCts = null;

        _courseStates.Clear();
        _outlineCache.Clear();
        foreach (var bmp in _previewBitmapCache.Values)
            bmp.Dispose();
        _previewBitmapCache.Clear();
        foreach (var bmp in _snipBitmapCache.Values)
            bmp.Dispose();
        _snipBitmapCache.Clear();
        _pendingPreviewRenders.Clear();

        _cachedBytesPath = null;
        _cachedPdfBytes = null;

        ActiveSubTab = QuizStudioSubTab.Catalog;
        SelectedCourse = null;
        AvailablePdfs = [];
        ShowIndividualHandouts = false;
        SelectedPdfPath = null;
        PdfTotalPages = 0;
        OutlineSections = [];
        OutlineSectionRoots = [];
        SelectedSectionKeys.Clear();
        MatchedScopePages = [];
        PreviewScopeIndex = 0;

        SelectedTerm = null;
        StartPage = null;
        EndPage = null;
        AppendTargetQuizId = null;
        AppendTargetQuizTitle = null;

        CatalogShowAllCourses = false;
        CatalogTermFilter = null;
        SelectedQuizIdsForMerge.Clear();

        // Deck Organizer & Snipper
        OrganizerQuiz = null;
        OrganizerKindFilter = null;
        OrganizerTopicFilter = null;
        OrganizerHealthFilter = OrganizerHealthFilter.All;
        OrganizerSearchQuery = string.Empty;
        SelectedOrganizerQuestionId = null;
        IsOrganizerDirty = false;
        EditingQuestionId = null;
        EditingQuestionDraft = null;
        IsEditingAdvancedOpen = false;
        EditingPageTextPreview = null;
        EditingGroundStatus = null;
        IsDedicatedSnipperOpen = false;
        IsLoadingSnipPage = false;
        EditingSnipPageBitmap = null;
        EditingSnipPageNumber = 0;
        EditingSnipTotalPages = 0;
        SnipExtractedFigures = [];
        ZoomedImagePath = null;
        SnipOutlineRoots = [];
        _snipOutlinePdfPath = null;

        // AI Fix / Import Modals
        FlaggingQuestionId = null;
        IsDeckAiFixModalOpen = false;
        DeckAiFixSelectedIds.Clear();
        DeckAiFixInstructions = string.Empty;
        DeckAiFixPromptPreview = string.Empty;
        DeckAiFixJsonInput = string.Empty;
        FlagProblemDescription = string.Empty;
        FlagFixPromptPreview = string.Empty;
        FlagFixJsonInput = string.Empty;
        IsManualJsonModalOpen = false;
        ManualJsonInput = string.Empty;
        GeneratedPromptPreview = string.Empty;

        // Catalog & Active Runner
        SavedQuizzes = [];
        AllDueReviewQuestions = [];
        ActiveSession = null;
        CurrentQuestionIndex = 0;
        CurrentAnswerDraft = string.Empty;
        IsAnswerRevealed = false;
        IsCurrentAnswerCorrect = false;
        IsShowingResultsSummary = false;
        SessionResults.Clear();
        OrderingDrafts.Clear();

        IsGenerating = false;
        IsInspectingPdf = false;
        IsBuildingPrompt = false;
        IsLoadingPreviewPage = false;
        StatusMessage = "Select a course & PDF to generate an evidence-grounded assessment.";
        IsStatusError = false;
    }
}
