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
        PreviewScopeIndex = 0;
        RecomputeScopePreview();
        _mainVm.RequestUiRefresh();
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
        int selectedCount = leaves.Count(n => SelectedSectionKeys.Contains(GetSectionKey(n.Section)));
        if (selectedCount == 0)
            return OutlineSelectionState.Unchecked;
        if (selectedCount == leaves.Count)
            return OutlineSelectionState.Checked;
        return OutlineSelectionState.Indeterminate;
    }

    public void ToggleOutlineNodeSelection(PdfOutlineTreeNode node)
    {
        bool currentlyAllSelected = GetOutlineNodeSelectionState(node) == OutlineSelectionState.Checked;
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

    private static IReadOnlyList<PdfOutlineTreeNode> BuildOutlineTree(
        IReadOnlyList<PdfOutlineSectionInfo> sections)
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
        if (PreviewScopeIndex > 0)
        {
            PreviewScopeIndex--;
            _mainVm.RequestUiRefresh();
        }
    }

    public void NextPreviewPage()
    {
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
        if (SelectedSectionKeys.Count == 0)
            return [];

        var raw = new List<PdfPageRange>();

        void CollectRanges(PdfOutlineTreeNode node)
        {
            var key = GetSectionKey(node.Section);
            if (node.Children.Count == 0)
            {
                if (SelectedSectionKeys.Contains(key))
                    raw.Add(new PdfPageRange(node.Section.StartPage, node.Section.EndPage));
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
            raw.AddRange(
                OutlineSections
                    .Where(s => SelectedSectionKeys.Contains(GetSectionKey(s)))
                    .Select(s => new PdfPageRange(s.StartPage, s.EndPage))
            );
        }

        raw = raw.OrderBy(r => r.StartPage).ThenBy(r => r.EndPage).ToList();
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
                    list.Add(($"Master Study Packet ({Path.GetFileName(masterPdf)})", masterPdf, true));
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
                            || list.Any(x => x.FullPath.Equals(rootPdf, StringComparison.OrdinalIgnoreCase))
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
            OutlineSections = cached.Sections;
            OutlineSectionRoots = BuildOutlineTree(cached.Sections);
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
                OutlineSections = sections;
                OutlineSectionRoots = BuildOutlineTree(sections);
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

        RefreshLivePromptPreviewIfActive();
    }
}
