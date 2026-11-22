using Avalonia.Threading;
using ScholarFlow.Core.Knowledge;
using ScholarFlow.Core.Quiz;

namespace ScholarFlow.Desktop.ViewModels;

/// <summary>
/// ViewModel for the Dedicated Knowledge Hub Search workspace (Ctrl+Shift+F).
/// Coordinates low-priority incremental background FTS5 indexing, cancel-on-keystroke
/// sub-25ms debounced queries, weak-spot boosting, and 1-click Jump to Source &amp; Quiz This actions.
/// </summary>
public sealed class KnowledgeSearchViewModel(MainWindowViewModel mainVm) : IDisposable
{
    private readonly MainWindowViewModel _mainVm = mainVm;
    private readonly KnowledgeIndexService _indexService = new();

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _indexCts;
    private KnowledgeWeakSpotProfile _weakProfile = KnowledgeWeakSpotProfile.Empty;

    public string QueryText { get; private set; } = string.Empty;
    public KnowledgeSourceFilterKind? SelectedSourceFilter { get; private set; }
    public string? SelectedCourseFilter { get; private set; }
    public bool OnlyWeakSpots { get; private set; }

    public ParsedKnowledgeQuery ParsedQuery { get; private set; } =
        KnowledgeSearchEngine.ParseQuery(string.Empty);
    public IReadOnlyList<KnowledgeSearchResult> Results { get; private set; } = [];
    public int TotalCandidates { get; private set; }
    public double LastSearchElapsedMs { get; private set; }
    public int SelectedResultIndex { get; private set; }

    public bool IsIndexing { get; private set; }
    public bool IsSearching { get; private set; }
    public bool HasCompletedInitialIndex { get; private set; }
    public int TotalIndexedFiles { get; private set; }
    public int TotalIndexedChunks { get; private set; }
    public double LastIndexElapsedMs { get; private set; }
    public string IndexStatusText { get; private set; } = "Index ready";

    public int WeakTopicCount => _weakProfile.WeakTopicCount;
    public int WeakQuestionCount => _weakProfile.WeakQuestionCount;

    /// <summary>
    /// Fires on the UI thread whenever search results, filter chips, or indexing telemetry change,
    /// allowing KnowledgeSearchViewBuilder to update the results pane in-place without losing TextBox focus.
    /// </summary>
    public event EventHandler? ResultsUpdated;

    public void ResetForBasinSwitch()
    {
        _searchCts?.Cancel();
        _indexCts?.Cancel();
        QueryText = string.Empty;
        SelectedSourceFilter = null;
        SelectedCourseFilter = null;
        OnlyWeakSpots = false;
        Results = [];
        TotalCandidates = 0;
        LastSearchElapsedMs = 0;
        SelectedResultIndex = 0;
        IsIndexing = false;
        IsSearching = false;
        HasCompletedInitialIndex = false;
        TotalIndexedFiles = 0;
        TotalIndexedChunks = 0;
        IndexStatusText = "Waiting for basin...";
        _weakProfile = KnowledgeWeakSpotProfile.Empty;
    }

    public void RebuildWeakSpotProfile()
    {
        _weakProfile = KnowledgeWeakSpotProfile.FromQuizzes(_mainVm.QuizStudio.SavedQuizzes);
    }

    public void TriggerBackgroundIndex(bool forceReindex = false)
    {
        _ = RunBackgroundIndexAsync(forceReindex);
    }

    public async Task RunBackgroundIndexAsync(bool forceReindex = false)
    {
        if (string.IsNullOrWhiteSpace(_mainVm.BasinRoot))
            return;

        _indexCts?.Cancel();
        var cts = new CancellationTokenSource();
        _indexCts = cts;
        var ct = cts.Token;

        var basinRoot = _mainVm.BasinRoot;
        var courses = _mainVm.Courses;
        var quizzes = _mainVm.QuizStudio.SavedQuizzes;

        RebuildWeakSpotProfile();
        IsIndexing = true;
        IndexStatusText = forceReindex ? "Rebuilding FTS5 index..." : "Syncing knowledge index...";
        NotifyResultsUpdated();

        try
        {
            var summary = await Task.Run(
                () =>
                    _indexService.IndexBasinIncrementalAsync(
                        basinRoot,
                        courses,
                        quizzes,
                        forceReindex,
                        ct
                    ),
                ct
            );

            if (ct.IsCancellationRequested)
                return;

            TotalIndexedFiles = summary.TotalIndexedFiles;
            TotalIndexedChunks = summary.TotalIndexedChunks;
            LastIndexElapsedMs = summary.ElapsedMs;
            HasCompletedInitialIndex = true;
            IsIndexing = false;
            IndexStatusText =
                summary.FilesIndexed > 0 || summary.FilesPruned > 0
                    ? $"Indexed {summary.TotalIndexedFiles} sources · {summary.TotalIndexedChunks:N0} chunks ({summary.ElapsedMs:F0} ms)"
                    : $"{summary.TotalIndexedFiles} sources · {summary.TotalIndexedChunks:N0} chunks";

            await ExecuteSearchNowAsync();
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer index pass
        }
        catch (Exception ex)
        {
            IsIndexing = false;
            HasCompletedInitialIndex = true;
            IndexStatusText = $"Index warning: {ex.Message}";
            NotifyResultsUpdated();
        }
    }

    public void UpdateQuery(string query)
    {
        var next = query ?? string.Empty;
        if (string.Equals(QueryText, next, StringComparison.Ordinal))
            return;

        QueryText = next;
        ScheduleDebouncedSearch(delayMs: 85);
    }

    public void ApplyExampleQuery(string query)
    {
        QueryText = query ?? string.Empty;
        _ = ExecuteSearchNowAsync();
    }

    public void SetSourceFilter(KnowledgeSourceFilterKind? kind)
    {
        SelectedSourceFilter = SelectedSourceFilter == kind ? null : kind;
        _ = ExecuteSearchNowAsync();
    }

    public void SetCourseFilter(string? courseAliasOrFolder)
    {
        SelectedCourseFilter = string.Equals(
            SelectedCourseFilter,
            courseAliasOrFolder,
            StringComparison.OrdinalIgnoreCase
        )
            ? null
            : courseAliasOrFolder;
        _ = ExecuteSearchNowAsync();
    }

    public void ToggleOnlyWeakSpots()
    {
        OnlyWeakSpots = !OnlyWeakSpots;
        _ = ExecuteSearchNowAsync();
    }

    public void ClearAllFilters()
    {
        QueryText = string.Empty;
        SelectedSourceFilter = null;
        SelectedCourseFilter = null;
        OnlyWeakSpots = false;
        _ = ExecuteSearchNowAsync();
    }

    private void ScheduleDebouncedSearch(int delayMs)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var ct = cts.Token;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    if (delayMs > 0)
                        await Task.Delay(delayMs, ct);
                    await RunSearchCoreAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    // Cancelled by next keystroke
                }
            },
            ct
        );
    }

    public async Task ExecuteSearchNowAsync()
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        try
        {
            await RunSearchCoreAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Cancelled
        }
    }

    private async Task RunSearchCoreAsync(CancellationToken ct)
    {
        var basinRoot = _mainVm.BasinRoot;
        if (string.IsNullOrWhiteSpace(basinRoot))
            return;

        var rawQuery = QueryText;
        var courseFilter = SelectedCourseFilter;
        var sourceFilter = SelectedSourceFilter;
        var weakOnly = OnlyWeakSpots;
        var profile = _weakProfile;

        IsSearching = true;

        var response = await Task.Run(
            () =>
                KnowledgeSearchEngine.SearchAsync(
                    basinRoot,
                    rawQuery,
                    profile,
                    courseFilter,
                    sourceFilter,
                    weakOnly,
                    maxResults: 100,
                    ct: ct
                ),
            ct
        );

        if (ct.IsCancellationRequested)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested)
                return;

            ParsedQuery = response.Query;
            Results = response.Results;
            TotalCandidates = response.TotalCandidates;
            LastSearchElapsedMs = response.ElapsedMs;
            SelectedResultIndex = Results.Count > 0 ? 0 : -1;
            IsSearching = false;
            NotifyResultsUpdated();
        });
    }

    public void MoveSelection(int delta)
    {
        if (Results.Count == 0)
            return;

        SelectedResultIndex = Math.Clamp(SelectedResultIndex + delta, 0, Results.Count - 1);
        NotifyResultsUpdated();
    }

    public void SelectResult(int index)
    {
        if (index < 0 || index >= Results.Count)
            return;

        SelectedResultIndex = index;
        NotifyResultsUpdated();
    }

    public KnowledgeSearchResult? GetSelectedResult() =>
        SelectedResultIndex >= 0 && SelectedResultIndex < Results.Count
            ? Results[SelectedResultIndex]
        : Results.Count > 0 ? Results[0]
        : null;

    public void JumpToSource(KnowledgeSearchResult? item = null)
    {
        var target = item ?? GetSelectedResult();
        if (target is null)
            return;

        var kind = target.SourceKind.ToLowerInvariant();
        switch (kind)
        {
            case "pdf":
                JumpToPdfSource(target);
                break;

            case "code":
            case "md":
            case "text":
                if (File.Exists(target.FilePath))
                {
                    _mainVm.OpenFileInSourceViewer(
                        target.FilePath,
                        target.StartLine > 0 ? target.StartLine : null,
                        target.EndLine > 0 ? target.EndLine : null,
                        target.SectionTitle,
                        target.PlainSnippet
                    );
                }
                break;

            case "image":
                if (File.Exists(target.FilePath))
                {
                    _mainVm.OpenFileInSourceViewer(
                        target.FilePath,
                        null,
                        null,
                        target.SectionTitle,
                        target.PlainSnippet
                    );
                }
                break;

            case "quiz":
                JumpToQuizOrganizer(target);
                break;
        }
    }

    public async Task QuizThisAsync(KnowledgeSearchResult? item = null)
    {
        var target = item ?? GetSelectedResult();
        if (target is null)
            return;

        var matchedCourse =
            _mainVm.Courses.FirstOrDefault(c =>
                c.FolderName.Equals(target.CourseFolder, StringComparison.OrdinalIgnoreCase)
                || c.EffectiveAlias.Equals(target.CourseAlias, StringComparison.OrdinalIgnoreCase)
            ) ?? _mainVm.ActiveCourse;

        var kind = target.SourceKind.ToLowerInvariant();

        if (kind == "pdf" && File.Exists(target.FilePath))
        {
            await _mainVm.SelectQuizStudioViewAsync(
                matchedCourse,
                target.FilePath,
                Math.Max(1, target.PageNumber)
            );
            _mainVm.QuizStudio.SetSubTab(QuizStudioSubTab.Builder);
            if (target.PageNumber > 0)
            {
                _mainVm.QuizStudio.SetPageRange(target.PageNumber, target.PageNumber);
            }
            return;
        }

        if (kind is "code" or "md" or "text" or "image" && File.Exists(target.FilePath))
        {
            await _mainVm.SelectQuizStudioViewAsync(matchedCourse);
            _mainVm.QuizStudio.ScopeBuilderToStudyFile(target.FilePath);
            return;
        }

        if (kind == "quiz")
        {
            // If the quiz card has a valid backing PDF or study file, scope Build to that exact source location
            if (!string.IsNullOrWhiteSpace(target.FilePath) && File.Exists(target.FilePath))
            {
                var (srcKind, _) = StudySourceClassifier.Classify(target.FilePath);
                if (srcKind == StudySourceKind.Pdf)
                {
                    await _mainVm.SelectQuizStudioViewAsync(
                        matchedCourse,
                        target.FilePath,
                        Math.Max(1, target.PageNumber)
                    );
                    _mainVm.QuizStudio.SetSubTab(QuizStudioSubTab.Builder);
                    if (target.PageNumber > 0)
                        _mainVm.QuizStudio.SetPageRange(target.PageNumber, target.PageNumber);
                    return;
                }

                if (StudySourceClassifier.IsViewableInSourceViewer(target.FilePath))
                {
                    await _mainVm.SelectQuizStudioViewAsync(matchedCourse);
                    _mainVm.QuizStudio.ScopeBuilderToStudyFile(target.FilePath);
                    return;
                }
            }

            // Otherwise open the quiz deck directly in Test & Recall
            var quiz = _mainVm.QuizStudio.SavedQuizzes.FirstOrDefault(q =>
                q.Id.Equals(target.QuizId, StringComparison.OrdinalIgnoreCase)
            );
            if (quiz is not null)
            {
                await _mainVm.SelectQuizStudioViewAsync(matchedCourse);
                _mainVm.QuizStudio.StartQuizSession(quiz);
            }
        }
    }

    private void JumpToPdfSource(KnowledgeSearchResult target)
    {
        if (string.IsNullOrWhiteSpace(target.FilePath) || !File.Exists(target.FilePath))
            return;

        int page = Math.Max(1, target.PageNumber);
        var quoteProbe = ExtractBestHighlightQuote(target);
        IReadOnlyList<NormalizedHighlightRect> rects = [];

        if (!string.IsNullOrWhiteSpace(quoteProbe))
        {
            var probeQuestion = new QuizQuestionRecord
            {
                SourcePdfPath = target.FilePath,
                SourcePage = page,
                VerbatimQuote = quoteProbe,
            };
            PdfQuizGrounder.GroundQuestionsToPdf(target.FilePath, [probeQuestion]);
            if (probeQuestion.HighlightRects.Count > 0 && probeQuestion.SourcePage == page)
            {
                rects = probeQuestion.HighlightRects;
            }
        }

        _mainVm.OpenPdfFromKnowledgeSearch(
            target.FilePath,
            page,
            rects,
            target.PlainSnippet,
            $"{target.Title} · {target.SectionTitle}"
        );
    }

    private void JumpToQuizOrganizer(KnowledgeSearchResult target)
    {
        var quiz = _mainVm.QuizStudio.SavedQuizzes.FirstOrDefault(q =>
            q.Id.Equals(target.QuizId, StringComparison.OrdinalIgnoreCase)
        );
        if (quiz is null)
            return;

        _mainVm.OpenQuizQuestionInOrganizerFromSearch(quiz, target.QuestionId);
    }

    private static string ExtractBestHighlightQuote(KnowledgeSearchResult target)
    {
        var snip = target.HighlightedSnippet;
        int openIdx = snip.IndexOf('«');
        int closeIdx = openIdx >= 0 ? snip.IndexOf('»', openIdx + 1) : -1;
        if (openIdx >= 0 && closeIdx > openIdx)
        {
            // Grab a ~70-char window centered around the first highlighted match
            int start = Math.Max(0, openIdx - 25);
            int end = Math.Min(snip.Length, closeIdx + 45);
            return snip[start..end]
                .Replace("«", string.Empty)
                .Replace("»", string.Empty)
                .Replace("…", string.Empty)
                .Trim();
        }

        var plain = target.PlainSnippet.Replace("…", string.Empty).Trim();
        return plain.Length > 90 ? plain[..90].Trim() : plain;
    }

    private void NotifyResultsUpdated()
    {
        if (Dispatcher.UIThread.CheckAccess())
            ResultsUpdated?.Invoke(this, EventArgs.Empty);
        else
            Dispatcher.UIThread.Post(() => ResultsUpdated?.Invoke(this, EventArgs.Empty));
    }

    public void Dispose()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _indexCts?.Cancel();
        _indexCts?.Dispose();
    }
}
