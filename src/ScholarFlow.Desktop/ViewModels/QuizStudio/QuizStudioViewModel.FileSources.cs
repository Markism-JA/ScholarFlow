using ScholarFlow.Core.Knowledge;
using ScholarFlow.Core.Quiz;
using ScholarFlow.Desktop.Services;

namespace ScholarFlow.Desktop.ViewModels;

/// <summary>
/// Quiz Studio: non-PDF study sources (code, markdown, text, images), optional PDF + file combination,
/// and code-aware default question-type weighting.
/// </summary>
public sealed partial class QuizStudioViewModel
{
    private readonly HashSet<string> _selectedFileSourcePaths = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<StudySourceDescriptor> StudyFiles { get; private set; } = [];
    public bool ShowStudyFiles { get; private set; }
    public IReadOnlyCollection<string> SelectedFileSourcePaths => _selectedFileSourcePaths;
    public bool IsFileSourceMode => _selectedFileSourcePaths.Count > 0;
    public bool IncludePdfWithFiles { get; private set; }
    public bool IsMixedSourceMode =>
        IsFileSourceMode
        && IncludePdfWithFiles
        && !string.IsNullOrWhiteSpace(SelectedPdfPath)
        && File.Exists(SelectedPdfPath);

    public bool HasCodeFileSelected =>
        _selectedFileSourcePaths.Any(p => StudySourceClassifier.Classify(p).Kind == StudySourceKind.Code);

    public bool IsCodeAwareMixActive { get; private set; }

    public bool IsFileSourceSelected(string path) => _selectedFileSourcePaths.Contains(path);

    public void ToggleShowStudyFiles()
    {
        ShowStudyFiles = !ShowStudyFiles;
        if (ShowStudyFiles)
            RebuildStudyFilesForCourse(force: true);
        _mainVm.RequestUiRefresh();
    }

    public void ToggleIncludePdfWithFiles()
    {
        IncludePdfWithFiles = !IncludePdfWithFiles;
        if (IncludePdfWithFiles)
            InspectSelectedPdfOutlineAndPreview();
        GeneratedPromptPreview = string.Empty;
        RefreshLivePromptPreviewIfActive();
        _mainVm.RequestUiRefresh();
    }

    public void ToggleFileSource(string path)
    {
        bool hadCode = HasCodeFileSelected;
        if (!_selectedFileSourcePaths.Remove(path))
            _selectedFileSourcePaths.Add(path);

        SyncCodeAwareMixAfterFileChange(hadCode);

        if (_selectedFileSourcePaths.Count == 0)
            IncludePdfWithFiles = false;

        GeneratedPromptPreview = string.Empty;
        RefreshLivePromptPreviewIfActive();
        _mainVm.RequestUiRefresh();
    }

    public void ApplyCodeAwareMix()
    {
        KindAllocations = QuizGenerationRequest.GetCodeAwareAllocations(AllocationMode, QuestionCount);
        IsCodeAwareMixActive = true;
        RefreshLivePromptPreviewIfActive();
        _mainVm.RequestUiRefresh();
    }

    public void ClearFileSources()
    {
        if (_selectedFileSourcePaths.Count == 0)
            return;

        bool hadCode = HasCodeFileSelected;
        _selectedFileSourcePaths.Clear();
        IncludePdfWithFiles = false;
        SyncCodeAwareMixAfterFileChange(hadCode);

        GeneratedPromptPreview = string.Empty;
        InspectSelectedPdfOutlineAndPreview();
        RefreshLivePromptPreviewIfActive();
        _mainVm.RequestUiRefresh();
    }

    private void SyncCodeAwareMixAfterFileChange(bool hadCodeBefore)
    {
        bool hasCodeNow = HasCodeFileSelected;
        if (!hadCodeBefore && hasCodeNow && SelectedPreset != QuizPresetMode.Custom)
        {
            KindAllocations = QuizGenerationRequest.GetCodeAwareAllocations(AllocationMode, QuestionCount);
            IsCodeAwareMixActive = true;
        }
        else if (hadCodeBefore && !hasCodeNow && IsCodeAwareMixActive && SelectedPreset != QuizPresetMode.Custom)
        {
            KindAllocations = QuizGenerationRequest.GetDefaultAllocationsForPreset(SelectedPreset, AllocationMode);
            if (AllocationMode == QuestionAllocationMode.ExactCounts)
                KindAllocations = BuildCurrentRequest().ResolveExactKindCounts(QuestionCount);
            IsCodeAwareMixActive = false;
        }
    }

    private string? _studyFilesCourseFolder;

    private void RebuildStudyFilesForCourse(bool force = false)
    {
        var folder = SelectedCourse?.FolderName;
        bool courseChanged = !string.Equals(folder, _studyFilesCourseFolder, StringComparison.OrdinalIgnoreCase);
        if (!courseChanged && !force)
            return;

        if (courseChanged)
        {
            bool hadCode = HasCodeFileSelected;
            _selectedFileSourcePaths.Clear();
            IncludePdfWithFiles = false;
            SyncCodeAwareMixAfterFileChange(hadCode);
        }

        _studyFilesCourseFolder = folder;

        if (folder is null || string.IsNullOrWhiteSpace(_mainVm.BasinRoot))
        {
            StudyFiles = [];
            return;
        }

        StudyFiles = FileSourceQuizSupport.DiscoverCourseStudyFiles(Path.Combine(_mainVm.BasinRoot, folder));
        _selectedFileSourcePaths.RemoveWhere(p => !StudyFiles.Any(f => f.FilePath.Equals(p, StringComparison.OrdinalIgnoreCase)));
    }

    private IReadOnlyList<string> GetSelectedFileSourcesInOrder() =>
        StudyFiles.Where(f => _selectedFileSourcePaths.Contains(f.FilePath)).Select(f => f.FilePath).ToList();

    /// <summary>Manual Web UI: pack selected images (and scoped PDF pages if combined) into an uploadable PDF + copy prompt.</summary>
    private async Task ExportPackedImagesForUploadAsync(
        QuizGenerationRequest req,
        Avalonia.Input.Platform.IClipboard? clipboard,
        string? outputPdfPath = null
    )
    {
        var images = req.SourceFilePaths.Where(ImagePacketPacker.IsImage).ToList();
        if (images.Count == 0 && !req.IsMixedSources)
        {
            StatusMessage = "Select at least one image (png / jpg) to pack, or use Copy Grounded Prompt for text/code.";
            IsStatusError = true;
            _mainVm.RequestUiRefresh();
            return;
        }

        try
        {
            var prefix = req.IsMixedSources ? "Combined_Packet" : "Images_Packet";
            var outPath = !string.IsNullOrWhiteSpace(outputPdfPath)
                ? outputPdfPath
                : Path.Combine(
                    Path.GetTempPath(),
                    "ScholarFlow-Scoped",
                    $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf"
                );

            var (prompt, packedImages, pdfCount, skipped) = await Task.Run(() =>
            {
                IReadOnlyList<(int OriginalPage, string SectionBreadcrumb)>? pdfManifest = null;
                IReadOnlyList<int> pdfPageNumbers = [];
                if (req.IsMixedSources)
                {
                    var pdfPages = PdfQuizGrounder.ExtractGroundedPages(
                        req.SourcePdfPath,
                        req.Term,
                        req.SectionFilter,
                        req.StartPage,
                        req.EndPage,
                        req.SelectedRanges,
                        maxPages: 250
                    );
                    pdfPageNumbers = pdfPages.Select(p => p.PageNumber).ToList();
                    pdfManifest = pdfPages.Select(p => (p.PageNumber, p.SectionBreadcrumb)).ToList();
                }

                var result = req.IsMixedSources
                    ? ImagePacketPacker.PackScopedPdfAndImages(req.SourcePdfPath, pdfPageNumbers, images, outPath)
                    : ImagePacketPacker.PackImagesToPdf(images, outPath);

                var textBundle = FileSourceQuizSupport.BuildPromptBundle(
                    req.SourceFilePaths.Where(p => !ImagePacketPacker.IsImage(p)),
                    ManualPromptBudgetChars
                );
                var text = _llmService.BuildInstructionPromptForPackedImages(req, result.Pages, textBundle, pdfManifest);
                return (text, result.Pages.Count, pdfPageNumbers.Count, result.Skipped);
            });

            if (packedImages == 0 && pdfCount == 0)
            {
                StatusMessage = "No packable images or PDF pages (Web packet supports PDF + png/jpg; webp/gif need API mode).";
                IsStatusError = true;
                _mainVm.RequestUiRefresh();
                return;
            }

            if (clipboard is not null)
                await clipboard.SetTextAsync(prompt);

            var summary = req.IsMixedSources
                ? $"Saved {outPath} ({pdfCount} PDF page(s) + {packedImages} image(s)) & copied prompt."
                : $"Saved {outPath} ({packedImages} image(s)) & copied prompt to clipboard.";
            StatusMessage = skipped.Count > 0
                ? $"{summary} Skipped: {string.Join(", ", skipped)}."
                : summary;
            IsStatusError = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Packet export failed: {ex.Message}";
            IsStatusError = true;
        }

        _mainVm.RequestUiRefresh();
    }

    /// <summary>Opens non-PDF evidence (code/text line range or image) in the in-app Source Viewer.</summary>
    private bool TryOpenNonPdfEvidence(QuizQuestionRecord question)
    {
        if (question.IsPdfSource)
            return false;

        if (!File.Exists(question.SourcePdfPath))
        {
            StatusMessage = $"Source file not found: {question.SourcePdfPath}";
            IsStatusError = true;
            _mainVm.RequestUiRefresh();
            return true;
        }

        var (kind, _) = StudySourceClassifier.Classify(question.SourcePdfPath);
        int? startLine = question.SourceStartLine;
        int? endLine = question.SourceEndLine;
        string? section = string.IsNullOrWhiteSpace(question.SectionTitle) ? null : question.SectionTitle;

        if (kind != StudySourceKind.Image && startLine is null && !string.IsNullOrWhiteSpace(question.VerbatimQuote))
        {
            var resolved = SourceEvidenceGrounder.ResolveLineRange(question.SourcePdfPath, question.VerbatimQuote);
            if (resolved is not null)
            {
                startLine = question.SourceStartLine = resolved.StartLine;
                endLine = question.SourceEndLine = resolved.EndLine;
                if (string.IsNullOrWhiteSpace(section) && !string.IsNullOrWhiteSpace(resolved.SectionTitle))
                    section = question.SectionTitle = resolved.SectionTitle;
            }
        }

        _mainVm.OpenFileInSourceViewer(
            question.SourcePdfPath,
            startLine,
            endLine,
            section,
            string.IsNullOrWhiteSpace(question.VerbatimQuote) ? null : question.VerbatimQuote,
            kind == StudySourceKind.Image ? question.HighlightRects : null
        );
        return true;
    }
}
