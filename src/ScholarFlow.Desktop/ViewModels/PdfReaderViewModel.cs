using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ScholarFlow.Core.Quiz;
using ScholarFlow.Desktop.Services;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Outline;

namespace ScholarFlow.Desktop.ViewModels;

/// <summary>
/// Hierarchical node in a PDF document's bookmark/outline tree.
/// </summary>
public sealed class PdfBookmarkNode
{
    public required string Title { get; init; }
    public required int PageNumber { get; init; }
    public required int Level { get; init; }
    public List<PdfBookmarkNode> Children { get; } = [];
    public bool IsExpanded { get; set; } = true;
}

/// <summary>
/// Built-in PDF reader ViewModel backed by PDFtoImage (PDFium + SkiaSharp) and PdfPig hierarchical outlines.
/// </summary>
public sealed class PdfReaderViewModel : IDisposable
{
    private readonly Dictionary<(int Page, int Dpi), Bitmap> _pageCache = [];
    private readonly HashSet<(int Page, int Dpi)> _pendingPageRenders = [];
    private CancellationTokenSource? _loadCts;
    private int? _pendingJumpPage;
    private byte[]? _pdfBytes;

    public string? CurrentFilePath { get; private set; }
    public string Title =>
        string.IsNullOrEmpty(CurrentFilePath) ? "No PDF Loaded" : Path.GetFileName(CurrentFilePath);
    public int PageCount { get; private set; }
    public int CurrentPage { get; private set; } = 1; // 1-based
    public int ZoomPercent { get; private set; } = 100;
    public bool FitToWidth { get; private set; } = true;
    public bool IsOutlineVisible { get; private set; } = true;
    public bool IsLoadingDocument { get; private set; }
    public bool IsRenderingPage { get; private set; }
    public IReadOnlyList<PdfBookmarkNode> BookmarkRoots { get; private set; } = [];
    public string? ErrorMessage { get; private set; }

    public int? ActiveHighlightPage { get; private set; }
    public IReadOnlyList<NormalizedHighlightRect> ActiveHighlights { get; private set; } = [];
    public string? ActiveEvidenceQuote { get; private set; }
    public string? ActiveEvidenceQuestionPrompt { get; private set; }

    public event EventHandler? Changed;

    public void LoadPdf(string filePath, string? basinRoot = null)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        ClearCache();
        ClearEvidenceHighlight(notify: false);
        _pendingPageRenders.Clear();
        _pendingJumpPage = null;
        _pdfBytes = null;

        CurrentFilePath = filePath;
        ErrorMessage = null;
        PageCount = 0;
        CurrentPage = 1;
        BookmarkRoots = [];
        IsRenderingPage = false;

        if (!File.Exists(filePath))
        {
            IsLoadingDocument = false;
            ErrorMessage = $"File not found: {filePath}";
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        IsLoadingDocument = true;
        Changed?.Invoke(this, EventArgs.Empty);

        _ = LoadPdfInBackgroundAsync(filePath, basinRoot, cts);
    }

    private async Task LoadPdfInBackgroundAsync(
        string filePath,
        string? basinRoot,
        CancellationTokenSource cts
    )
    {
        var token = cts.Token;
        try
        {
            var (bytes, pageCount, roots) = await Task.Run(
                () =>
                {
                    token.ThrowIfCancellationRequested();
                    var data = File.ReadAllBytes(filePath);
                    token.ThrowIfCancellationRequested();
                    using var doc = PdfDocument.Open(data);
                    int pages = doc.NumberOfPages;
                    var tree = ExtractBookmarkRoots(doc);
                    MergeCustomSavedSectionsIntoBookmarks(tree, basinRoot, filePath, pages);
                    return (data, pages, tree);
                },
                token
            );

            if (token.IsCancellationRequested || _loadCts != cts)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested || _loadCts != cts)
                    return;

                _pdfBytes = bytes;
                PageCount = pageCount;
                BookmarkRoots = roots;
                int target = _pendingJumpPage ?? CurrentPage;
                _pendingJumpPage = null;
                CurrentPage = pageCount > 0 ? Math.Clamp(target, 1, pageCount) : 1;
                IsLoadingDocument = false;
                Changed?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer LoadPdf call
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested || _loadCts != cts)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested || _loadCts != cts)
                    return;

                ErrorMessage = $"Failed to open PDF: {ex.Message}";
                PageCount = 0;
                BookmarkRoots = [];
                IsLoadingDocument = false;
                Changed?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    public Bitmap? GetCurrentPageBitmap()
    {
        if (IsLoadingDocument || _pdfBytes is null || PageCount <= 0)
            return null;

        int zeroBased = Math.Clamp(CurrentPage - 1, 0, PageCount - 1);
        int targetDpi = (!FitToWidth && ZoomPercent > 150) ? 280 : 180;

        if (_pageCache.TryGetValue((zeroBased, targetDpi), out var cached))
            return cached;

        // Zero-flash zoom fallback: display any cached DPI tier for this page immediately
        // while the target DPI tier renders silently in the background.
        var fallback = _pageCache
            .Where(kv => kv.Key.Page == zeroBased)
            .OrderByDescending(kv => kv.Key.Dpi)
            .Select(kv => kv.Value)
            .FirstOrDefault();

        _ = RenderPageInBackgroundAsync(
            _pdfBytes,
            zeroBased,
            targetDpi,
            CurrentFilePath,
            notifyOnComplete: true,
            showLoadingIndicator: fallback is null
        );
        return fallback;
    }

    private async Task RenderPageInBackgroundAsync(
        byte[] pdfBytes,
        int zeroBased,
        int dpi,
        string? filePath,
        bool notifyOnComplete,
        bool showLoadingIndicator = true
    )
    {
        var key = (zeroBased, dpi);
        if (!_pendingPageRenders.Add(key))
            return;

        if (notifyOnComplete && showLoadingIndicator)
            IsRenderingPage = true;

        try
        {
            var rendered = await Task.Run(() =>
                PdfBitmapRenderer.RenderPagePixels(pdfBytes, zeroBased, dpi)
            );

            Dispatcher.UIThread.Post(() =>
            {
                _pendingPageRenders.Remove(key);
                IsRenderingPage = _pendingPageRenders.Count > 0;

                if (!string.Equals(CurrentFilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    return;

                var bmp = PdfBitmapRenderer.CreateWriteableBitmap(rendered);

                if (_pageCache.Count >= 16)
                {
                    var firstKey = _pageCache.Keys.First();
                    _pageCache[firstKey].Dispose();
                    _pageCache.Remove(firstKey);
                }

                _pageCache[key] = bmp;

                if (notifyOnComplete)
                {
                    Changed?.Invoke(this, EventArgs.Empty);

                    // Prefetch next page silently for smooth paging on large textbooks
                    if (zeroBased + 1 < PageCount && !_pageCache.ContainsKey((zeroBased + 1, dpi)))
                    {
                        _ = RenderPageInBackgroundAsync(
                            pdfBytes,
                            zeroBased + 1,
                            dpi,
                            filePath,
                            notifyOnComplete: false
                        );
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _pendingPageRenders.Remove(key);
                IsRenderingPage = _pendingPageRenders.Count > 0;
                if (
                    notifyOnComplete
                    && string.Equals(CurrentFilePath, filePath, StringComparison.OrdinalIgnoreCase)
                )
                {
                    ErrorMessage = $"Render error: {ex.Message}";
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            });
        }
    }

    public void NextPage()
    {
        if (CurrentPage < PageCount)
        {
            CurrentPage++;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void PreviousPage()
    {
        if (CurrentPage > 1)
        {
            CurrentPage--;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void JumpToPage(int oneBasedPage)
    {
        if (IsLoadingDocument || PageCount <= 0)
        {
            _pendingJumpPage = Math.Max(1, oneBasedPage);
            CurrentPage = _pendingJumpPage.Value;
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        CurrentPage = Math.Clamp(oneBasedPage, 1, PageCount);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetEvidenceHighlight(
        int oneBasedPage,
        IReadOnlyList<NormalizedHighlightRect> highlights,
        string? verbatimQuote = null,
        string? questionPrompt = null
    )
    {
        if (IsLoadingDocument || PageCount <= 0)
        {
            _pendingJumpPage = Math.Max(1, oneBasedPage);
            CurrentPage = _pendingJumpPage.Value;
        }
        else
        {
            CurrentPage = Math.Clamp(oneBasedPage, 1, PageCount);
        }

        ActiveHighlightPage = CurrentPage;
        ActiveHighlights = highlights ?? [];
        ActiveEvidenceQuote = verbatimQuote;
        ActiveEvidenceQuestionPrompt = questionPrompt;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ClearEvidenceHighlight(bool notify = true)
    {
        ActiveHighlightPage = null;
        ActiveHighlights = [];
        ActiveEvidenceQuote = null;
        ActiveEvidenceQuestionPrompt = null;
        if (notify)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ZoomIn() => ZoomIn(25);

    public void ZoomIn(int step)
    {
        FitToWidth = false;
        ZoomPercent = Math.Min(250, ZoomPercent + step);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ZoomOut() => ZoomOut(25);

    public void ZoomOut(int step)
    {
        FitToWidth = false;
        ZoomPercent = Math.Max(50, ZoomPercent - step);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ResetZoom()
    {
        FitToWidth = false;
        ZoomPercent = 100;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleFitToWidth()
    {
        FitToWidth = !FitToWidth;
        if (FitToWidth)
            ZoomPercent = 100;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleOutline()
    {
        IsOutlineVisible = !IsOutlineVisible;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleNodeExpanded(PdfBookmarkNode node)
    {
        node.IsExpanded = !node.IsExpanded;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public PdfBookmarkNode? FindActiveBookmarkNode()
    {
        PdfBookmarkNode? best = null;

        void Walk(IEnumerable<PdfBookmarkNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.PageNumber <= CurrentPage)
                {
                    if (best is null || n.PageNumber >= best.PageNumber)
                        best = n;
                }
                if (n.Children.Count > 0)
                    Walk(n.Children);
            }
        }

        Walk(BookmarkRoots);
        return best;
    }

    public void OpenExternal()
    {
        if (!string.IsNullOrEmpty(CurrentFilePath))
            ExternalFileLauncher.OpenLocalFile(CurrentFilePath);
    }

    public static List<PdfBookmarkNode> ExtractBookmarkRoots(PdfDocument doc)
    {
        var roots = new List<PdfBookmarkNode>();
        try
        {
            if (doc.TryGetBookmarks(out var bookmarks))
            {
                // Use bookmarks.Roots (NOT bookmarks.GetNodes(), which already flattens the entire tree!)
                var seenSiblings = new HashSet<(string Title, int Page)>();
                foreach (var rootNode in bookmarks.Roots)
                {
                    var converted = BuildBookmarkTree(rootNode, level: 1);
                    if (
                        converted is not null
                        && seenSiblings.Add((converted.Title, converted.PageNumber))
                    )
                    {
                        roots.Add(converted);
                    }
                }
            }
        }
        catch
        {
            // Ignore malformed PDF outline trees
        }
        return roots;
    }

    private static PdfBookmarkNode? BuildBookmarkTree(BookmarkNode node, int level)
    {
        int pageNumber = node is DocumentBookmarkNode docNode ? docNode.PageNumber : 1;
        var title = node.Title?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(title))
            return null;

        var vmNode = new PdfBookmarkNode
        {
            Title = title,
            PageNumber = pageNumber,
            Level = level,
            IsExpanded = level <= 2,
        };

        var seenChildren = new HashSet<(string Title, int Page)>();
        foreach (var child in node.Children)
        {
            var childVm = BuildBookmarkTree(child, level + 1);
            if (childVm is not null && seenChildren.Add((childVm.Title, childVm.PageNumber)))
            {
                vmNode.Children.Add(childVm);
            }
        }

        return vmNode;
    }

    private static void MergeCustomSavedSectionsIntoBookmarks(
        List<PdfBookmarkNode> roots,
        string? basinRoot,
        string pdfPath,
        int totalPages
    )
    {
        if (string.IsNullOrWhiteSpace(basinRoot) || totalPages <= 0)
            return;

        var customSections = Core.Pdf.PdfCustomSectionStore.LoadSections(basinRoot, pdfPath);
        if (customSections.Count == 0)
            return;

        if (roots.Count == 0)
        {
            foreach (var sec in customSections)
            {
                roots.Add(
                    new PdfBookmarkNode
                    {
                        Title = sec.Title,
                        PageNumber = Math.Clamp(sec.StartPage, 1, totalPages),
                        Level = 1,
                        IsExpanded = true,
                    }
                );
            }
            return;
        }

        var group = new PdfBookmarkNode
        {
            Title = "Saved Sections",
            PageNumber = Math.Clamp(customSections[0].StartPage, 1, totalPages),
            Level = 1,
            IsExpanded = true,
        };
        foreach (var sec in customSections)
        {
            group.Children.Add(
                new PdfBookmarkNode
                {
                    Title = sec.Title,
                    PageNumber = Math.Clamp(sec.StartPage, 1, totalPages),
                    Level = 2,
                    IsExpanded = true,
                }
            );
        }
        roots.Add(group);
    }

    private void ClearCache()
    {
        foreach (var bmp in _pageCache.Values)
            bmp.Dispose();
        _pageCache.Clear();
    }

    public void Reset()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        ClearCache();
        ClearEvidenceHighlight(notify: false);
        _pendingPageRenders.Clear();
        _pendingJumpPage = null;
        _pdfBytes = null;

        CurrentFilePath = null;
        ErrorMessage = null;
        PageCount = 0;
        CurrentPage = 1;
        BookmarkRoots = [];
        IsLoadingDocument = false;
        IsRenderingPage = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        ClearCache();
    }
}
