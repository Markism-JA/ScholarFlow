using Avalonia.Media.Imaging;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Pdf;
using ScholarFlow.Core.Quiz;

namespace ScholarFlow.Desktop.ViewModels;

public sealed partial class QuizStudioViewModel
{
    private readonly Dictionary<
        string,
        IReadOnlyList<(string Label, string FullPath, bool IsMaster)>
    > _availablePdfsCache = new(StringComparer.OrdinalIgnoreCase);

    public void SelectCourseScope(BasinCourseEntry course)
    {
        if (
            SelectedCourse is not null
            && string.Equals(
                SelectedCourse.FolderName,
                course.FolderName,
                StringComparison.OrdinalIgnoreCase
            )
        )
            return;

        SaveActiveCourseState();
        SelectedCourse = course;
        RestoreCourseState(course);

        if (ActiveSubTab == QuizStudioSubTab.Builder)
        {
            RebuildAvailablePdfsForCourse(SelectedPdfPath);
        }
        else
        {
            // Keep Catalog/Organizer course switches instant: defer PDF scanning & outline inspection until Builder is opened
            PdfTotalPages = 0;
            OutlineSections = [];
            OutlineSectionRoots = [];
            MatchedScopePages = [];
        }

        _mainVm.RequestUiRefresh();
    }

    public void ToggleShowIndividualHandouts()
    {
        ShowIndividualHandouts = !ShowIndividualHandouts;
        _mainVm.RequestUiRefresh();
    }

    public void SelectPdfScope(string pdfPath)
    {
        if (!IncludePdfWithFiles)
        {
            bool hadCode = HasCodeFileSelected;
            _selectedFileSourcePaths.Clear();
            SyncCodeAwareMixAfterFileChange(hadCode);
        }
        SelectedPdfPath = pdfPath;
        StartPage = null;
        EndPage = null;
        PageRangeExpression = string.Empty;
        PageRangeError = null;
        PreviewBrowseAllPages = false;
        PreviewBrowsePageNumber = 1;
        IsAddCustomSectionOpen = false;
        CustomSectionNameDraft = string.Empty;
        SelectedSectionKeys.Clear();
        PreviewScopeIndex = 0;
        InspectSelectedPdfOutlineAndPreview();
        _mainVm.RequestUiRefresh();
    }

    public void SelectTermScope(string? term)
    {
        SelectedTerm = string.Equals(SelectedTerm, term, StringComparison.OrdinalIgnoreCase)
            ? null
            : term;
        SelectedSectionKeys.Clear();
        PreviewScopeIndex = 0;
        RecomputeScopePreview();
        _mainVm.RequestUiRefresh();
    }

    public void SetPageRange(int? start, int? end)
    {
        StartPage = start is > 0 ? start : null;
        EndPage = end is > 0 ? end : null;
        if (StartPage.HasValue && EndPage.HasValue && EndPage.Value < StartPage.Value)
            EndPage = StartPage.Value;
        PageRangeExpression =
            StartPage.HasValue || EndPage.HasValue
                ? $"{StartPage ?? 1}-{(EndPage.HasValue ? EndPage.Value.ToString() : string.Empty)}"
                : string.Empty;
        PageRangeError = null;
        PreviewScopeIndex = 0;
        RecomputeScopePreview();
        _mainVm.RequestUiRefresh();
    }

    public void ApplyPageRangeExpression(string? rawExpression)
    {
        var expr = (rawExpression ?? string.Empty).Trim();
        PageRangeExpression = expr;
        if (string.IsNullOrWhiteSpace(expr))
        {
            StartPage = null;
            EndPage = null;
            PageRangeError = null;
            PreviewScopeIndex = 0;
            RecomputeScopePreview();
            _mainVm.RequestUiRefresh();
            return;
        }

        int total = Math.Max(1, PdfTotalPages);
        if (!PdfPageRangeParser.TryParse(expr, total, out var parsed, out var err))
        {
            PageRangeError = err;
            _mainVm.RequestUiRefresh();
            return;
        }

        PageRangeError = null;
        if (parsed.Count == 1)
        {
            StartPage = parsed[0].StartPage;
            EndPage = parsed[0].EndPage;
        }
        else
        {
            StartPage = null;
            EndPage = null;
        }

        PreviewScopeIndex = 0;
        RecomputeScopePreview();
        _mainVm.RequestUiRefresh();
    }

    public void SetPreviewBrowseMode(bool browseAll)
    {
        if (PreviewBrowseAllPages == browseAll)
            return;

        int curPage = CurrentPreviewPdfPage;
        PreviewBrowseAllPages = browseAll;
        if (browseAll)
        {
            PreviewBrowsePageNumber = Math.Clamp(curPage, 1, Math.Max(1, PdfTotalPages));
        }
        else if (MatchedScopePages.Count > 0)
        {
            int idx = 0;
            for (int i = 0; i < MatchedScopePages.Count; i++)
            {
                if (MatchedScopePages[i].PageNumber >= curPage)
                {
                    idx = i;
                    break;
                }
            }
            PreviewScopeIndex = idx;
        }
        _mainVm.RequestUiRefresh();
    }

    public void JumpPreviewToPdfPage(int pageNumber)
    {
        if (PdfTotalPages <= 0)
            return;

        int clamped = Math.Clamp(pageNumber, 1, PdfTotalPages);
        if (!PreviewBrowseAllPages)
        {
            for (int i = 0; i < MatchedScopePages.Count; i++)
            {
                if (MatchedScopePages[i].PageNumber == clamped)
                {
                    PreviewScopeIndex = i;
                    _mainVm.RequestUiRefresh();
                    return;
                }
            }

            // Page is outside current scope -> seamlessly switch to All Pages browse mode so user can inspect it
            PreviewBrowseAllPages = true;
        }

        PreviewBrowsePageNumber = clamped;
        _mainVm.RequestUiRefresh();
    }

    public void MarkCurrentPreviewPageAsStart()
    {
        if (PdfTotalPages <= 0)
            return;

        int cur = CurrentPreviewPdfPage;
        int currentEnd =
            EndPage
            ?? (
                MatchedScopePages.Count > 0
                    ? MatchedScopePages[^1].PageNumber
                    : PdfTotalPages
            );
        if (currentEnd < cur)
            currentEnd = PdfTotalPages;

        SelectedSectionKeys.Clear();
        StartPage = cur;
        EndPage = currentEnd;
        PageRangeExpression = $"{cur}-{currentEnd}";
        PageRangeError = null;
        RecomputeScopePreview();
        SyncPreviewToPage(cur);
        _mainVm.RequestUiRefresh();
    }

    public void MarkCurrentPreviewPageAsEnd()
    {
        if (PdfTotalPages <= 0)
            return;

        int cur = CurrentPreviewPdfPage;
        int currentStart =
            StartPage
            ?? (MatchedScopePages.Count > 0 ? MatchedScopePages[0].PageNumber : 1);
        if (currentStart > cur)
            currentStart = 1;

        SelectedSectionKeys.Clear();
        StartPage = currentStart;
        EndPage = cur;
        PageRangeExpression = $"{currentStart}-{cur}";
        PageRangeError = null;
        RecomputeScopePreview();
        SyncPreviewToPage(cur);
        _mainVm.RequestUiRefresh();
    }

    public void ToggleCurrentPreviewPageInScope()
    {
        if (PdfTotalPages <= 0)
            return;

        int cur = CurrentPreviewPdfPage;
        var currentPages = MatchedScopePages.Select(p => p.PageNumber).ToHashSet();
        if (currentPages.Contains(cur))
        {
            if (currentPages.Count <= 1)
            {
                StatusMessage = "At least 1 page must remain in scope.";
                IsStatusError = false;
                _mainVm.RequestUiRefresh();
                return;
            }
            currentPages.Remove(cur);
        }
        else
        {
            currentPages.Add(cur);
        }

        SelectedSectionKeys.Clear();
        var ranges = PdfPageRangeParser.CompressToRanges(currentPages);
        PageRangeExpression = PdfPageRangeParser.Format(ranges);
        PageRangeError = null;
        if (ranges.Count == 1)
        {
            StartPage = ranges[0].StartPage;
            EndPage = ranges[0].EndPage;
        }
        else
        {
            StartPage = null;
            EndPage = null;
        }

        RecomputeScopePreview();
        if (PreviewBrowseAllPages)
            PreviewBrowsePageNumber = cur;
        _mainVm.RequestUiRefresh();
    }

    private void SyncPreviewToPage(int pageNumber)
    {
        if (PreviewBrowseAllPages)
        {
            PreviewBrowsePageNumber = Math.Clamp(pageNumber, 1, Math.Max(1, PdfTotalPages));
            return;
        }

        for (int i = 0; i < MatchedScopePages.Count; i++)
        {
            if (MatchedScopePages[i].PageNumber >= pageNumber)
            {
                PreviewScopeIndex = i;
                return;
            }
        }
        PreviewScopeIndex = Math.Max(0, MatchedScopePages.Count - 1);
    }

    public void ToggleAddCustomSectionForm()
    {
        IsAddCustomSectionOpen = !IsAddCustomSectionOpen;
        if (IsAddCustomSectionOpen && string.IsNullOrWhiteSpace(CustomSectionNameDraft))
        {
            if (MatchedScopePages.Count > 0)
            {
                int first = MatchedScopePages[0].PageNumber;
                int last = MatchedScopePages[^1].PageNumber;
                CustomSectionNameDraft = first == last ? $"Page {first}" : $"Pages {first}-{last}";
            }
        }
        _mainVm.RequestUiRefresh();
    }

    public void SaveCurrentScopeAsCustomSection(string? rawTitle)
    {
        if (
            string.IsNullOrWhiteSpace(_mainVm.BasinRoot)
            || string.IsNullOrWhiteSpace(SelectedPdfPath)
            || MatchedScopePages.Count == 0
        )
            return;

        var title = (rawTitle ?? CustomSectionNameDraft ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            StatusMessage = "Enter a section name before saving.";
            IsStatusError = true;
            _mainVm.RequestUiRefresh();
            return;
        }

        int start = MatchedScopePages[0].PageNumber;
        int end = MatchedScopePages[^1].PageNumber;
        var expr = PdfPageRangeParser.Format(MatchedScopePages.Select(p => p.PageNumber));
        PdfCustomSectionStore.AddSection(
            _mainVm.BasinRoot,
            SelectedPdfPath,
            title,
            start,
            end,
            rangeExpression: expr,
            term: SelectedTerm
        );

        IsAddCustomSectionOpen = false;
        CustomSectionNameDraft = string.Empty;
        RefreshMergedOutlineForCurrentPdf();
        StatusMessage = $"Saved custom section '{title}' ({expr}).";
        IsStatusError = false;
        _mainVm.RequestUiRefresh();
    }

    public void DeleteCustomSection(string sectionId)
    {
        if (
            string.IsNullOrWhiteSpace(_mainVm.BasinRoot)
            || string.IsNullOrWhiteSpace(SelectedPdfPath)
        )
            return;

        if (PdfCustomSectionStore.DeleteSection(_mainVm.BasinRoot, SelectedPdfPath, sectionId))
        {
            RefreshMergedOutlineForCurrentPdf();
            StatusMessage = "Deleted custom section.";
            IsStatusError = false;
            _mainVm.RequestUiRefresh();
        }
    }

    public string? GetCustomSectionIdForOutlineSection(PdfOutlineSectionInfo sec)
    {
        var match = CustomSavedSections.FirstOrDefault(c =>
            c.StartPage == sec.StartPage
            && c.EndPage == sec.EndPage
            && string.Equals(c.Title, sec.Title, StringComparison.OrdinalIgnoreCase)
        );
        return match?.Id;
    }

    private void RefreshMergedOutlineForCurrentPdf()
    {
        if (string.IsNullOrWhiteSpace(SelectedPdfPath))
            return;

        IReadOnlyList<PdfOutlineSectionInfo> embedded =
            _outlineCache.TryGetValue(SelectedPdfPath, out var cached) ? cached.Sections : [];
        CustomSavedSections = PdfCustomSectionStore.LoadSections(
            _mainVm.BasinRoot,
            SelectedPdfPath
        );
        var merged = MergeEmbeddedAndCustomSections(embedded, CustomSavedSections, PdfTotalPages);
        OutlineSections = merged;
        OutlineSectionRoots = BuildOutlineTree(merged);
        RecomputeScopePreview();
    }

    private static List<PdfOutlineSectionInfo> MergeEmbeddedAndCustomSections(
        IReadOnlyList<PdfOutlineSectionInfo> embedded,
        IReadOnlyList<SavedPdfSection> custom,
        int totalPages
    )
    {
        var list = new List<PdfOutlineSectionInfo>(embedded);
        int maxP = Math.Max(1, totalPages);
        foreach (var c in custom)
        {
            int s = Math.Clamp(c.StartPage, 1, maxP);
            int e = Math.Clamp(c.EndPage, s, maxP);
            list.Add(new PdfOutlineSectionInfo(c.Title, c.Term ?? "Custom", 1, s, e));
        }
        return list;
    }

    public static string GetSectionKey(PdfOutlineSectionInfo s) =>
        $"{s.StartPage}-{s.EndPage}:{s.Title}";

    public void ToggleOutlineSection(PdfOutlineSectionInfo sec)
    {
        var key = GetSectionKey(sec);
        bool added = SelectedSectionKeys.Add(key);
        if (!added)
            SelectedSectionKeys.Remove(key);
        RecomputeScopePreview();
        if (added)
        {
            var idx = MatchedScopePages
                .Select((p, i) => (p.PageNumber, Index: i))
                .FirstOrDefault(x => x.PageNumber >= sec.StartPage)
                .Index;
            PreviewScopeIndex = idx;
        }
        _mainVm.RequestUiRefresh();
    }

    public void ToggleOutlineNodeExpanded(PdfOutlineTreeNode node)
    {
        node.IsExpanded = !node.IsExpanded;
        _mainVm.RequestUiRefresh();
    }

    public OutlineSelectionState GetOutlineNodeSelectionState(PdfOutlineTreeNode node)
    {
        if (node.Children.Count == 0)
        {
            return SelectedSectionKeys.Contains(GetSectionKey(node.Section))
                ? OutlineSelectionState.Checked
                : OutlineSelectionState.Unchecked;
        }

        var leaves = GetLeafDescendants(node);
        int selectedCount = leaves.Count(n =>
            SelectedSectionKeys.Contains(GetSectionKey(n.Section))
        );
        if (selectedCount == 0)
            return OutlineSelectionState.Unchecked;
        if (selectedCount == leaves.Count)
            return OutlineSelectionState.Checked;
        return OutlineSelectionState.Indeterminate;
    }

    public void ToggleOutlineNodeSelection(PdfOutlineTreeNode node)
    {
        bool currentlyAllSelected =
            GetOutlineNodeSelectionState(node) == OutlineSelectionState.Checked;
        var subtree = FlattenSubtree(node)
            .Where(n =>
                string.IsNullOrWhiteSpace(SelectedTerm)
                || string.IsNullOrWhiteSpace(n.Section.Term)
                || string.Equals(n.Section.Term, SelectedTerm, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();

        if (currentlyAllSelected)
        {
            foreach (var n in subtree)
                SelectedSectionKeys.Remove(GetSectionKey(n.Section));
        }
        else
        {
            foreach (var n in subtree)
                SelectedSectionKeys.Add(GetSectionKey(n.Section));
        }

        SyncAncestorSelectionKeys();
        RecomputeScopePreview();
        if (!currentlyAllSelected)
        {
            var idx = MatchedScopePages
                .Select((p, i) => (p.PageNumber, Index: i))
                .FirstOrDefault(x => x.PageNumber >= node.Section.StartPage)
                .Index;
            PreviewScopeIndex = idx;
        }
        _mainVm.RequestUiRefresh();
    }

    private void SyncAncestorSelectionKeys()
    {
        bool SyncNode(PdfOutlineTreeNode node)
        {
            if (node.Children.Count == 0)
                return SelectedSectionKeys.Contains(GetSectionKey(node.Section));

            int selectedChildren = 0;
            bool allChildrenSelected = true;
            foreach (var child in node.Children)
            {
                bool childHasSelection = SyncNode(child);
                if (childHasSelection)
                    selectedChildren++;
                if (GetOutlineNodeSelectionState(child) != OutlineSelectionState.Checked)
                    allChildrenSelected = false;
            }

            var key = GetSectionKey(node.Section);
            if (selectedChildren == 0)
                SelectedSectionKeys.Remove(key);
            else if (allChildrenSelected)
                SelectedSectionKeys.Add(key);

            return selectedChildren > 0;
        }

        foreach (var root in OutlineSectionRoots)
            SyncNode(root);
    }

    private static List<PdfOutlineTreeNode> GetLeafDescendants(PdfOutlineTreeNode root)
    {
        var leaves = new List<PdfOutlineTreeNode>();
        foreach (var n in FlattenSubtree(root))
        {
            if (n.Children.Count == 0)
                leaves.Add(n);
        }
        return leaves;
    }

    private static List<PdfOutlineTreeNode> FlattenSubtree(PdfOutlineTreeNode root)
    {
        var result = new List<PdfOutlineTreeNode> { root };
        var stack = new Stack<PdfOutlineTreeNode>();
        for (int i = root.Children.Count - 1; i >= 0; i--)
            stack.Push(root.Children[i]);

        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            result.Add(cur);
            for (int i = cur.Children.Count - 1; i >= 0; i--)
                stack.Push(cur.Children[i]);
        }
        return result;
    }

    private static List<PdfOutlineTreeNode> BuildOutlineTree(
        IReadOnlyList<PdfOutlineSectionInfo> sections
    )
    {
        if (sections.Count == 0)
            return [];

        bool collapseTopLevel = sections.Count > 20;
        var roots = new List<PdfOutlineTreeNode>();
        var stack = new Stack<PdfOutlineTreeNode>();

        foreach (var sec in sections)
        {
            var node = new PdfOutlineTreeNode
            {
                Section = sec,
                IsExpanded = !collapseTopLevel || sec.Level > 1,
            };

            while (stack.Count > 0 && stack.Peek().Section.Level >= sec.Level)
                stack.Pop();

            if (stack.Count == 0)
                roots.Add(node);
            else
                stack.Peek().Children.Add(node);

            stack.Push(node);
        }

        return roots;
    }

    public void JumpPreviewToSection(PdfOutlineSectionInfo sec)
    {
        var matchIdx = -1;
        for (int i = 0; i < MatchedScopePages.Count; i++)
        {
            if (MatchedScopePages[i].PageNumber >= sec.StartPage)
            {
                matchIdx = i;
                break;
            }
        }

        if (matchIdx >= 0)
        {
            PreviewScopeIndex = matchIdx;
            _mainVm.RequestUiRefresh();
        }
    }

    public void PreviousPreviewPage()
    {
        if (PreviewBrowseAllPages)
        {
            if (PreviewBrowsePageNumber > 1)
            {
                PreviewBrowsePageNumber--;
                _mainVm.RequestUiRefresh();
            }
            return;
        }

        if (PreviewScopeIndex > 0)
        {
            PreviewScopeIndex--;
            _mainVm.RequestUiRefresh();
        }
    }

    public void NextPreviewPage()
    {
        if (PreviewBrowseAllPages)
        {
            if (PreviewBrowsePageNumber < PdfTotalPages)
            {
                PreviewBrowsePageNumber++;
                _mainVm.RequestUiRefresh();
            }
            return;
        }

        if (PreviewScopeIndex + 1 < MatchedScopePages.Count)
        {
            PreviewScopeIndex++;
            _mainVm.RequestUiRefresh();
        }
    }

    public void ClearScopeFilters()
    {
        SelectedTerm = null;
        StartPage = null;
        EndPage = null;
        PageRangeExpression = string.Empty;
        PageRangeError = null;
        SelectedSectionKeys.Clear();
        PreviewScopeIndex = 0;
        RecomputeScopePreview();
        _mainVm.RequestUiRefresh();
    }

    public void OpenQuestionEvidenceInPdfReader(QuizQuestionRecord question)
    {
        if (TryOpenNonPdfEvidence(question))
            return;

        if (
            string.IsNullOrWhiteSpace(question.SourcePdfPath)
            || !File.Exists(question.SourcePdfPath)
        )
        {
            StatusMessage = $"Source PDF not found: {question.SourcePdfPath}";
            IsStatusError = true;
            _mainVm.RequestUiRefresh();
            return;
        }

        var parentQuiz = SavedQuizzes.FirstOrDefault(qz =>
            string.Equals(qz.Id, question.QuizId, StringComparison.OrdinalIgnoreCase)
        );
        bool isStale = parentQuiz?.IsSourcePdfStale() == true;

        // Re-compute highlight rects on-the-fly if empty or if the source Master PDF was recompiled
        if (
            (isStale || question.HighlightRects.Count == 0)
            && !string.IsNullOrWhiteSpace(question.VerbatimQuote)
        )
        {
            PdfQuizGrounder.GroundQuestionsToPdf(
                question.SourcePdfPath,
                [question],
                forceUpdateSection: true
            );
        }

        _mainVm.OpenPdfWithQuizEvidence(
            question.SourcePdfPath,
            question.SourcePage,
            question.HighlightRects,
            question.VerbatimQuote,
            question.Prompt
        );
    }

    public void OpenEvidenceAnchorInPdfReader(
        QuizQuestionRecord question,
        GroundedEvidenceAnchor anchor
    )
    {
        if (TryOpenNonPdfEvidence(question))
            return;

        if (
            string.IsNullOrWhiteSpace(question.SourcePdfPath)
            || !File.Exists(question.SourcePdfPath)
        )
        {
            StatusMessage = $"Source PDF not found: {question.SourcePdfPath}";
            IsStatusError = true;
            _mainVm.RequestUiRefresh();
            return;
        }

        _mainVm.OpenPdfWithQuizEvidence(
            question.SourcePdfPath,
            anchor.PageNumber > 0 ? anchor.PageNumber : question.SourcePage,
            anchor.HighlightRects,
            anchor.VerbatimQuote,
            question.Prompt
        );
    }

    private List<PdfPageRange> GetSelectedPageRanges()
    {
        List<PdfPageRange>? exprRanges = null;
        if (
            !string.IsNullOrWhiteSpace(PageRangeExpression)
            && PdfPageRangeParser.TryParse(
                PageRangeExpression,
                Math.Max(1, PdfTotalPages),
                out var parsedExpr,
                out _
            )
            && parsedExpr.Count > 0
        )
        {
            exprRanges = parsedExpr;
        }

        if (SelectedSectionKeys.Count == 0)
            return exprRanges ?? [];

        var raw = new List<PdfPageRange>();

        void AddSectionRanges(PdfOutlineSectionInfo sec)
        {
            // If this outline node came from a custom saved section with an expressive range (e.g. "1-10, !4"), honor it
            var custom = CustomSavedSections.FirstOrDefault(c =>
                c.StartPage == sec.StartPage
                && c.EndPage == sec.EndPage
                && string.Equals(c.Title, sec.Title, StringComparison.OrdinalIgnoreCase)
            );
            if (
                custom is not null
                && !string.IsNullOrWhiteSpace(custom.RangeExpression)
                && PdfPageRangeParser.TryParse(
                    custom.RangeExpression,
                    Math.Max(1, PdfTotalPages),
                    out var customRanges,
                    out _
                )
                && customRanges.Count > 0
            )
            {
                raw.AddRange(customRanges);
            }
            else
            {
                raw.Add(new PdfPageRange(sec.StartPage, sec.EndPage));
            }
        }

        void CollectRanges(PdfOutlineTreeNode node)
        {
            var key = GetSectionKey(node.Section);
            if (node.Children.Count == 0)
            {
                if (SelectedSectionKeys.Contains(key))
                    AddSectionRanges(node.Section);
                return;
            }

            // For parent nodes with children, only emit exclusive intro pages before the first child
            // so deselecting any child subsection genuinely excludes that child's pages.
            if (SelectedSectionKeys.Contains(key))
            {
                int firstChildStart = node.Children.Min(c => c.Section.StartPage);
                if (firstChildStart > node.Section.StartPage)
                    raw.Add(new PdfPageRange(node.Section.StartPage, firstChildStart - 1));
            }

            foreach (var child in node.Children)
                CollectRanges(child);
        }

        if (OutlineSectionRoots.Count > 0)
        {
            foreach (var root in OutlineSectionRoots)
                CollectRanges(root);
        }
        else
        {
            foreach (var s in OutlineSections.Where(s => SelectedSectionKeys.Contains(GetSectionKey(s))))
                AddSectionRanges(s);
        }

        if (exprRanges is not null)
            raw.AddRange(exprRanges);

        raw = [.. raw.OrderBy(r => r.StartPage).ThenBy(r => r.EndPage)];
        if (raw.Count <= 1)
            return raw;

        var merged = new List<PdfPageRange> { raw[0] };
        for (int i = 1; i < raw.Count; i++)
        {
            var last = merged[^1];
            var cur = raw[i];
            if (cur.StartPage <= last.EndPage + 1)
                merged[^1] = new PdfPageRange(last.StartPage, Math.Max(last.EndPage, cur.EndPage));
            else
                merged.Add(cur);
        }
        return merged;
    }

    public Bitmap? GetCurrentPreviewBitmap()
    {
        if (
            string.IsNullOrWhiteSpace(SelectedPdfPath)
            || !File.Exists(SelectedPdfPath)
            || PdfTotalPages <= 0
        )
            return null;

        int pageOneBased = Math.Clamp(CurrentPreviewPdfPage, 1, PdfTotalPages);
        var key = (SelectedPdfPath, pageOneBased);
        if (_previewBitmapCache.TryGetValue(key, out var cached))
            return cached;

        _ = LoadPreviewBitmapInBackgroundAsync(SelectedPdfPath, pageOneBased);
        return null;
    }

    private async Task LoadPreviewBitmapInBackgroundAsync(string pdfPath, int pageOneBased)
    {
        var key = (pdfPath, pageOneBased);
        if (!_pendingPreviewRenders.Add(key))
            return;

        IsLoadingPreviewPage = true;
        try
        {
            var rendered = await Task.Run(() =>
            {
                byte[] pdfBytes;
                lock (_pendingPreviewRenders)
                {
                    if (
                        !string.Equals(
                            _cachedBytesPath,
                            pdfPath,
                            StringComparison.OrdinalIgnoreCase
                        ) || _cachedPdfBytes is null
                    )
                    {
                        _cachedPdfBytes = File.ReadAllBytes(pdfPath);
                        _cachedBytesPath = pdfPath;
                    }
                    pdfBytes = _cachedPdfBytes;
                }

                return Services.PdfBitmapRenderer.RenderPagePixels(
                    pdfBytes,
                    pageOneBased - 1,
                    dpi: 135
                );
            });

            var bmp = Services.PdfBitmapRenderer.CreateWriteableBitmap(rendered);

            if (_previewBitmapCache.Count >= 18)
            {
                var firstKey = _previewBitmapCache.Keys.First();
                _previewBitmapCache[firstKey].Dispose();
                _previewBitmapCache.Remove(firstKey);
            }

            _previewBitmapCache[key] = bmp;
        }
        catch
        {
            // Non-fatal preview error
        }
        finally
        {
            _pendingPreviewRenders.Remove(key);
            IsLoadingPreviewPage = _pendingPreviewRenders.Count > 0;
            Avalonia.Threading.Dispatcher.UIThread.Post(_mainVm.RequestUiRefresh);
        }
    }

    private void RebuildAvailablePdfsForCourse(string? preferredPdfPath)
    {
        List<(string Label, string FullPath, bool IsMaster)> list;
        if (
            SelectedCourse is not null
            && _availablePdfsCache.TryGetValue(SelectedCourse.FolderName, out var cachedList)
        )
        {
            list = [.. cachedList];
        }
        else
        {
            list = [];
            if (SelectedCourse is not null && !string.IsNullOrWhiteSpace(_mainVm.BasinRoot))
            {
                var courseDir = Path.Combine(_mainVm.BasinRoot, SelectedCourse.FolderName);
                var masterPdf = MasterPdfService.FindExistingMasterPdfPath(
                    _mainVm.BasinRoot,
                    SelectedCourse.FolderName
                );
                if (masterPdf is not null && File.Exists(masterPdf))
                {
                    list.Add(
                        ($"Master Study Packet ({Path.GetFileName(masterPdf)})", masterPdf, true)
                    );
                }

                if (Directory.Exists(courseDir))
                {
                    // PDFs (including symlinked/linked ones) placed directly in the course root.
                    foreach (
                        var rootPdf in Directory
                            .GetFiles(courseDir, "*.pdf")
                            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    )
                    {
                        var rootName = Path.GetFileName(rootPdf);
                        if (
                            rootName.EndsWith("_.pdf", StringComparison.OrdinalIgnoreCase)
                            || list.Any(x =>
                                x.FullPath.Equals(rootPdf, StringComparison.OrdinalIgnoreCase)
                            )
                            || !File.Exists(rootPdf)
                        )
                            continue;
                        list.Add((Path.GetFileNameWithoutExtension(rootName), rootPdf, false));
                    }

                    foreach (
                        var dir in Directory
                            .GetDirectories(courseDir)
                            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    )
                    {
                        var dirName = Path.GetFileName(dir);
                        if (
                            dirName.StartsWith('.')
                            || dirName.Equals("completed", StringComparison.OrdinalIgnoreCase)
                            || dirName.Equals("ongoing", StringComparison.OrdinalIgnoreCase)
                        )
                        {
                            continue;
                        }

                        foreach (
                            var pdf in Directory
                                .GetFiles(dir, "*.pdf")
                                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                        )
                        {
                            var fileName = Path.GetFileName(pdf);
                            if (fileName.EndsWith("_.pdf", StringComparison.OrdinalIgnoreCase))
                                continue;
                            list.Add((dirName.Replace('_', ' '), pdf, false));
                        }
                    }
                }

                _availablePdfsCache[SelectedCourse.FolderName] = [.. list];
            }
        }

        AvailablePdfs = list;
        if (!string.IsNullOrWhiteSpace(preferredPdfPath) && File.Exists(preferredPdfPath))
        {
            if (
                !AvailablePdfs.Any(p =>
                    p.FullPath.Equals(preferredPdfPath, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                list.Insert(0, (Path.GetFileName(preferredPdfPath), preferredPdfPath, false));
                AvailablePdfs = list;
            }
            SelectedPdfPath = preferredPdfPath;
        }
        else if (
            SelectedPdfPath is null
            || !AvailablePdfs.Any(p =>
                p.FullPath.Equals(SelectedPdfPath, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            SelectedPdfPath =
                AvailablePdfs.FirstOrDefault(p => p.IsMaster).FullPath
                ?? (AvailablePdfs.Count > 0 ? AvailablePdfs[0].FullPath : null);
        }

        RebuildStudyFilesForCourse();

        if (ActiveSubTab == QuizStudioSubTab.Builder)
            InspectSelectedPdfOutlineAndPreview();
    }

    private void InspectSelectedPdfOutlineAndPreview()
    {
        if (string.IsNullOrWhiteSpace(SelectedPdfPath) || !File.Exists(SelectedPdfPath))
        {
            PdfTotalPages = 0;
            OutlineSections = [];
            OutlineSectionRoots = [];
            MatchedScopePages = [];
            PreviewScopeIndex = 0;
            return;
        }

        if (_outlineCache.TryGetValue(SelectedPdfPath, out var cached))
        {
            PdfTotalPages = cached.TotalPages;
            CustomSavedSections = PdfCustomSectionStore.LoadSections(
                _mainVm.BasinRoot,
                SelectedPdfPath
            );
            var merged = MergeEmbeddedAndCustomSections(
                cached.Sections,
                CustomSavedSections,
                cached.TotalPages
            );
            OutlineSections = merged;
            OutlineSectionRoots = BuildOutlineTree(merged);
            RecomputeScopePreview();
        }
        else
        {
            _ = InspectOutlineInBackgroundAsync(SelectedPdfPath);
        }
    }

    private async Task InspectOutlineInBackgroundAsync(string pdfPath)
    {
        IsInspectingPdf = true;
        try
        {
            var (sections, totalPages) = await Task.Run(() =>
            {
                var secs = PdfQuizGrounder.InspectOutlineSections(pdfPath, out int pages);
                return (secs, pages);
            });

            _outlineCache[pdfPath] = (totalPages, sections);
            if (string.Equals(SelectedPdfPath, pdfPath, StringComparison.OrdinalIgnoreCase))
            {
                PdfTotalPages = totalPages;
                CustomSavedSections = PdfCustomSectionStore.LoadSections(
                    _mainVm.BasinRoot,
                    pdfPath
                );
                var merged = MergeEmbeddedAndCustomSections(
                    sections,
                    CustomSavedSections,
                    totalPages
                );
                OutlineSections = merged;
                OutlineSectionRoots = BuildOutlineTree(merged);
                RecomputeScopePreview();
            }
        }
        finally
        {
            IsInspectingPdf = false;
            Avalonia.Threading.Dispatcher.UIThread.Post(_mainVm.RequestUiRefresh);
        }
    }

    private void RecomputeScopePreview()
    {
        if (
            string.IsNullOrWhiteSpace(SelectedPdfPath)
            || !File.Exists(SelectedPdfPath)
            || PdfTotalPages <= 0
        )
        {
            MatchedScopePages = [];
            PreviewScopeIndex = 0;
            return;
        }

        MatchedScopePages = PdfQuizGrounder.ResolveScopePagesFast(
            PdfTotalPages,
            OutlineSections,
            SelectedTerm,
            StartPage,
            EndPage,
            GetSelectedPageRanges()
        );

        if (PreviewScopeIndex >= MatchedScopePages.Count)
            PreviewScopeIndex = 0;

        TriggerPdfScopeSignalProbe();
        RefreshLivePromptPreviewIfActive();
    }
}
