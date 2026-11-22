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
    private CancellationTokenSource? _groundHighlightCts;
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

    /// <summary>
    /// Fires on the UI thread whenever only the highlighted SelectedResultIndex changes (e.g. Up/Down keys),
    /// so the view can swap highlight visuals in-place and scroll the item into view without resetting scroll.
    /// </summary>
    public event EventHandler? SelectionChanged;

    public void ResetForBasinSwitch()
    {
        _searchCts?.Cancel();
        _indexCts?.Cancel();
        _groundHighlightCts?.Cancel();
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

        var next = Math.Clamp(SelectedResultIndex + delta, 0, Results.Count - 1);
        if (next == SelectedResultIndex)
            return;

        SelectedResultIndex = next;
        NotifySelectionChanged();
    }

    public void SelectResult(int index)
    {
        if (index < 0 || index >= Results.Count)
            return;

        if (index == SelectedResultIndex)
            return;

        SelectedResultIndex = index;
        NotifySelectionChanged();
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

        _mainVm.CloseKnowledgeSearchModal();

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
        var filePath = target.FilePath;
        var snippet = target.PlainSnippet;
        var heading = $"{target.Title} · {target.SectionTitle}";
        var quoteProbe = ExtractBestHighlightQuote(target);
        var matchedTerms = ExtractMatchedTerms(target, QueryText);

        // Open the PDF Reader immediately (0 ms UI lag) with the snippet banner,
        // then ground exact word bounding-box highlights asynchronously in the background.
        _mainVm.OpenPdfFromKnowledgeSearch(filePath, page, [], snippet, heading);

        if (string.IsNullOrWhiteSpace(quoteProbe) && matchedTerms.Count == 0)
            return;

        _groundHighlightCts?.Cancel();
        var cts = new CancellationTokenSource();
        _groundHighlightCts = cts;
        var ct = cts.Token;

        _ = Task.Run(
            () =>
            {
                try
                {
                    if (ct.IsCancellationRequested)
                        return;

                    var rects = new List<NormalizedHighlightRect>();

                    // 1. Primary: ground the matched snippet window directly on the target page
                    if (!string.IsNullOrWhiteSpace(quoteProbe))
                    {
                        var probeQuestion = new QuizQuestionRecord
                        {
                            SourcePdfPath = filePath,
                            SourcePage = page,
                            VerbatimQuote = quoteProbe,
                        };
                        PdfQuizGrounder.GroundQuestionsToPdf(filePath, [probeQuestion]);
                        if (
                            probeQuestion.SourcePage == page
                            && probeQuestion.HighlightRects.Count > 0
                        )
                        {
                            rects.AddRange(probeQuestion.HighlightRects);
                        }
                    }

                    // 2. Reliable fallback: locate the exact searched/highlighted terms directly on `page`
                    if (rects.Count == 0 && matchedTerms.Count > 0)
                    {
                        rects.AddRange(FindTermRectsOnPage(filePath, page, matchedTerms));
                    }

                    if (ct.IsCancellationRequested || rects.Count == 0)
                        return;

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ct.IsCancellationRequested)
                            return;

                        if (
                            _mainVm.ActiveMode == WorkspaceTabMode.PdfReader
                            && string.Equals(
                                _mainVm.PdfReader.CurrentFilePath,
                                filePath,
                                StringComparison.OrdinalIgnoreCase
                            )
                            && (
                                _mainVm.PdfReader.ActiveHighlightPage == page
                                || _mainVm.PdfReader.CurrentPage == page
                            )
                        )
                        {
                            _mainVm.PdfReader.SetEvidenceHighlight(page, rects, snippet, heading);
                        }
                    });
                }
                catch
                {
                    // Best-effort highlight grounding; PDF page is already open.
                }
            },
            ct
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
            // Expand to whole-word boundaries around the first highlighted match
            int start = Math.Max(0, openIdx - 32);
            while (start > 0 && !char.IsWhiteSpace(snip[start - 1]))
                start--;
            int end = Math.Min(snip.Length, closeIdx + 48);
            while (end < snip.Length && !char.IsWhiteSpace(snip[end]))
                end++;
            return snip[start..end]
                .Replace("«", string.Empty)
                .Replace("»", string.Empty)
                .Replace("…", string.Empty)
                .Trim();
        }

        var plain = target.PlainSnippet.Replace("…", string.Empty).Trim();
        return plain.Length > 90 ? plain[..90].Trim() : plain;
    }

    private static List<string> ExtractMatchedTerms(KnowledgeSearchResult target, string rawQuery)
    {
        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddToken(string raw)
        {
            var cleaned = raw.Trim().Trim('.', ',', ';', ':', '(', ')', '[', ']', '"', '\'', '«', '»');
            if (cleaned.Length >= 2 && seen.Add(cleaned))
                terms.Add(cleaned);
        }

        var snip = target.HighlightedSnippet ?? string.Empty;
        int pos = 0;
        while (pos < snip.Length)
        {
            int o = snip.IndexOf('«', pos);
            if (o < 0)
                break;
            int c = snip.IndexOf('»', o + 1);
            if (c < 0)
                break;
            foreach (
                var part in snip[(o + 1)..c]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            )
            {
                AddToken(part);
            }
            pos = c + 1;
        }

        if (terms.Count == 0 && !string.IsNullOrWhiteSpace(rawQuery))
        {
            foreach (
                var part in rawQuery.Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                )
            )
            {
                if (!part.Contains(':'))
                    AddToken(part);
            }
        }

        return terms;
    }

    private static List<NormalizedHighlightRect> FindTermRectsOnPage(
        string pdfPath,
        int oneBasedPage,
        IReadOnlyList<string> terms
    )
    {
        var results = new List<NormalizedHighlightRect>();
        using var doc = UglyToad.PdfPig.PdfDocument.Open(pdfPath);
        if (oneBasedPage < 1 || oneBasedPage > doc.NumberOfPages)
            return results;

        var page = doc.GetPage(oneBasedPage);
        double pw = page.Width > 1 ? page.Width : 612.0;
        double ph = page.Height > 1 ? page.Height : 792.0;

        foreach (var word in page.GetWords())
        {
            var txt = word.Text;
            if (string.IsNullOrWhiteSpace(txt))
                continue;
            if (terms.Any(t => txt.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                var b = word.BoundingBox;
                results.Add(
                    new NormalizedHighlightRect(
                        Math.Clamp(b.Left / pw, 0.0, 1.0),
                        Math.Clamp((ph - b.Top) / ph, 0.0, 1.0),
                        Math.Clamp(b.Width / pw, 0.015, 1.0),
                        Math.Clamp(b.Height / ph, 0.012, 1.0)
                    )
                );
                if (results.Count >= 16)
                    break;
            }
        }

        return results;
    }

    private void NotifyResultsUpdated()
    {
        if (Dispatcher.UIThread.CheckAccess())
            ResultsUpdated?.Invoke(this, EventArgs.Empty);
        else
            Dispatcher.UIThread.Post(() => ResultsUpdated?.Invoke(this, EventArgs.Empty));
    }

    private void NotifySelectionChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        else
            Dispatcher.UIThread.Post(() => SelectionChanged?.Invoke(this, EventArgs.Empty));
    }

    public void Dispose()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _indexCts?.Cancel();
        _indexCts?.Dispose();
        _groundHighlightCts?.Cancel();
        _groundHighlightCts?.Dispose();
    }
}
