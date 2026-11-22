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

public enum PdfFitMode
{
    FitWidth,
    FitPage,
    Custom,
}

/// <summary>
/// Built-in PDF reader ViewModel backed by PDFtoImage (PDFium + SkiaSharp) and PdfPig hierarchical outlines.
/// Features parallel fast-preview cold open, 2-tier progressive rendering (96 -> 180/280 DPI),
/// 24-page LRU cache with bidirectional prefetch, page rotation, Fit Width/Page modes, and in-PDF search.
/// </summary>
public sealed class PdfReaderViewModel : IDisposable
{
    private const int FastPreviewDpi = 96;
    private const int MaxCacheEntries = 24;

    private readonly Dictionary<(int Page, int Dpi, int Rotation), Bitmap> _pageCache = [];
    private readonly LinkedList<(int Page, int Dpi, int Rotation)> _lruOrder = [];
    private readonly HashSet<(int Page, int Dpi, int Rotation)> _pendingPageRenders = [];
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _searchCts;
    private int? _pendingJumpPage;
    private byte[]? _pdfBytes;
    private double[] _pageAspectRatios = [];

    public string? CurrentFilePath { get; private set; }
    public string Title =>
        string.IsNullOrEmpty(CurrentFilePath) ? "No PDF Loaded" : Path.GetFileName(CurrentFilePath);
    public int PageCount { get; private set; }
    public int CurrentPage { get; private set; } = 1; // 1-based
    public int ZoomPercent { get; private set; } = 100;
    public PdfFitMode FitMode { get; private set; } = PdfFitMode.FitWidth;
    public bool FitToWidth => FitMode == PdfFitMode.FitWidth;
    public int RotationDegrees { get; private set; }
    public bool IsOutlineVisible { get; private set; } = true;
    public bool IsLoadingDocument { get; private set; }
    public bool IsRenderingPage { get; private set; }
    public IReadOnlyList<PdfBookmarkNode> BookmarkRoots { get; private set; } = [];
    public string? ErrorMessage { get; private set; }

    // Multi-highlight grounded evidence anchors (supports 1 or many highlights across pages)
    public IReadOnlyList<GroundedEvidenceAnchor> EvidenceAnchors { get; private set; } = [];
    public int ActiveEvidenceIndex { get; private set; } = -1;
    public GroundedEvidenceAnchor? ActiveEvidenceAnchor =>
        ActiveEvidenceIndex >= 0 && ActiveEvidenceIndex < EvidenceAnchors.Count
            ? EvidenceAnchors[ActiveEvidenceIndex]
            : null;
    public int? ActiveHighlightPage => ActiveEvidenceAnchor?.PageNumber;
    public IReadOnlyList<NormalizedHighlightRect> ActiveHighlights =>
        ActiveEvidenceAnchor?.HighlightRects ?? [];
    public string? ActiveEvidenceQuote => ActiveEvidenceAnchor?.VerbatimQuote;
    public string? ActiveEvidenceSectionTitle => ActiveEvidenceAnchor?.SectionTitle;
    public string? ActiveEvidenceQuestionPrompt { get; private set; }

    // In-PDF Text Search (Ctrl+F)
    public bool IsSearchBarOpen { get; private set; }
    public string SearchQuery { get; set; } = string.Empty;
    public bool IsSearchingPdf { get; private set; }
    public IReadOnlyList<(int PageNumber, NormalizedHighlightRect Rect)> SearchMatches
    {
        get;
        private set;
    } = [];
    public int ActiveSearchMatchIndex { get; private set; } = -1;

    public event EventHandler? Changed;
    public event Action<int, Bitmap>? PageBitmapReady;
    public event Action<int>? NavigationRequested;
    public event Action<int, NormalizedHighlightRect?>? HighlightFocusRequested;
    public event Action? EvidenceSelectionChangedInPlace;
    public event Action? ZoomChangedInPlace;
    public event Action<bool>? OutlineVisibilityChanged;

    public double GetPageAspectRatio(int oneBasedPage)
    {
        double baseRatio = 1.294;
        if (oneBasedPage >= 1 && oneBasedPage < _pageAspectRatios.Length && _pageAspectRatios[oneBasedPage] > 0.1)
            baseRatio = _pageAspectRatios[oneBasedPage];
        return RotationDegrees is 90 or 270 ? (1.0 / baseRatio) : baseRatio;
    }

    public (
        IReadOnlyList<NormalizedHighlightRect> Primary,
        IReadOnlyList<NormalizedHighlightRect> Secondary
    ) GetPageEvidenceHighlights(int oneBasedPage)
    {
        if (EvidenceAnchors.Count == 0)
            return ([], []);

        var primary = new List<NormalizedHighlightRect>();
        var secondary = new List<NormalizedHighlightRect>();

        for (int i = 0; i < EvidenceAnchors.Count; i++)
        {
            var anchor = EvidenceAnchors[i];
            if (anchor.PageNumber != oneBasedPage || anchor.HighlightRects.Count == 0)
                continue;
            if (i == ActiveEvidenceIndex)
                primary.AddRange(anchor.HighlightRects);
            else
                secondary.AddRange(anchor.HighlightRects);
        }

        return (primary, secondary);
    }

    public void SetCurrentPageQuietly(int oneBasedPage)
    {
        if (PageCount <= 0)
            return;
        CurrentPage = Math.Clamp(oneBasedPage, 1, PageCount);
    }

    public IReadOnlyList<NormalizedHighlightRect> GetPageSearchHighlights(int oneBasedPage)
    {
        if (!IsSearchBarOpen || SearchMatches.Count == 0)
            return [];

        return SearchMatches
            .Where(m => m.PageNumber == oneBasedPage)
            .Select(m => m.Rect)
            .ToList();
    }

    public void LoadPdf(string filePath, string? basinRoot = null)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        ClearCache();
        ClearEvidenceHighlight(notify: false);
        _pendingPageRenders.Clear();
        _pendingJumpPage = null;
        _pdfBytes = null;
        _pageAspectRatios = [];
        RotationDegrees = 0;
        SearchMatches = [];
        ActiveSearchMatchIndex = -1;
        IsSearchingPdf = false;

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
            var (bytes, pageCount, aspectRatios, roots, initialPageZeroBased, initialPreviewPixels) =
                await Task.Run(
                    () =>
                    {
                        token.ThrowIfCancellationRequested();
                        var data = File.ReadAllBytes(filePath);
                        token.ThrowIfCancellationRequested();

                        // Native C++ PDFium reads all page dimensions in ~1-3ms without parsing any content streams
                        // (unlike PdfPig's doc.GetPage(p), which parses full page content streams and takes seconds on large books).
                        int pages = 0;
                        double[] ratios = [];
                        try
                        {
                            if (
                                OperatingSystem.IsWindows()
                                || OperatingSystem.IsLinux()
                                || OperatingSystem.IsMacOS()
                            )
                            {
                                var nativeSizes = PDFtoImage.Conversion.GetPageSizes(data);
                                pages = nativeSizes.Count;
                                ratios = new double[pages + 1];
                                for (int i = 0; i < pages; i++)
                                {
                                    var sz = nativeSizes[i];
                                    double w = sz.Width > 1f ? sz.Width : 612.0;
                                    double h = sz.Height > 1f ? sz.Height : 792.0;
                                    ratios[i + 1] = Math.Clamp(h / w, 0.35, 3.0);
                                }
                            }
                        }
                        catch
                        {
                            // Fallback below via PdfPig if native size query fails
                        }

                        using var doc = PdfDocument.Open(data);
                        if (pages <= 0)
                        {
                            pages = doc.NumberOfPages;
                            ratios = new double[pages + 1];
                            for (int p = 1; p <= pages; p++)
                                ratios[p] = 1.294;
                        }

                        var tree = ExtractBookmarkRoots(doc);
                        MergeCustomSavedSectionsIntoBookmarks(tree, basinRoot, filePath, pages);

                        // Render target start page at fast 96 DPI inside the initial load task
                        // so Page 1 appears immediately on first frame (zero extra round-trip).
                        int targetOneBased = _pendingJumpPage ?? 1;
                        int zPage = pages > 0 ? Math.Clamp(targetOneBased - 1, 0, pages - 1) : 0;
                        RenderedPdfPixels? previewPx = null;
                        if (pages > 0 && !token.IsCancellationRequested)
                        {
                            try
                            {
                                previewPx = PdfBitmapRenderer.RenderPagePixels(
                                    data,
                                    zPage,
                                    FastPreviewDpi,
                                    0
                                );
                            }
                            catch
                            {
                                // Non-fatal; standard render path will retry
                            }
                        }

                        return (data, pages, ratios, tree, zPage, previewPx);
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
                _pageAspectRatios = aspectRatios;
                BookmarkRoots = roots;
                int target = _pendingJumpPage ?? CurrentPage;
                _pendingJumpPage = null;
                CurrentPage = pageCount > 0 ? Math.Clamp(target, 1, pageCount) : 1;

                if (initialPreviewPixels is not null && pageCount > 0)
                {
                    PutCache(
                        (initialPageZeroBased, FastPreviewDpi, 0),
                        PdfBitmapRenderer.CreateWriteableBitmap(initialPreviewPixels)
                    );
                }

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

    public Bitmap? GetCurrentPageBitmap() => GetPageBitmap(CurrentPage);

    public Bitmap? GetPageBitmap(int oneBasedPage, bool requestRenderIfMissing = true)
    {
        if (IsLoadingDocument || _pdfBytes is null || PageCount <= 0)
            return null;

        int zeroBased = Math.Clamp(oneBasedPage - 1, 0, PageCount - 1);
        int rot = RotationDegrees;
        int targetDpi = (FitMode == PdfFitMode.Custom && ZoomPercent > 150) ? 280 : 180;
        var exactKey = (zeroBased, targetDpi, rot);

        if (TryGetCache(exactKey, out var cached))
            return cached;

        // Zero-flash progressive fallback: return any cached DPI tier for this (page, rotation) immediately
        Bitmap? fallback = null;
        int bestDpi = -1;
        foreach (var kv in _pageCache)
        {
            if (kv.Key.Page == zeroBased && kv.Key.Rotation == rot && kv.Key.Dpi > bestDpi)
            {
                bestDpi = kv.Key.Dpi;
                fallback = kv.Value;
            }
        }

        if (!requestRenderIfMissing)
            return fallback;

        if (fallback is null)
        {
            // Fast-preview first (~15ms at 96 DPI), then auto-sharpen to crisp targetDpi
            _ = RenderPageInBackgroundAsync(
                _pdfBytes,
                zeroBased,
                FastPreviewDpi,
                rot,
                CurrentFilePath,
                notifyOnComplete: true,
                showLoadingIndicator: oneBasedPage == CurrentPage,
                upgradeToDpi: targetDpi
            );
        }
        else
        {
            // Already displaying fast preview -> silently sharpen to targetDpi with zero flash
            _ = RenderPageInBackgroundAsync(
                _pdfBytes,
                zeroBased,
                targetDpi,
                rot,
                CurrentFilePath,
                notifyOnComplete: true,
                showLoadingIndicator: false
            );
        }

        return fallback;
    }

    private bool TryGetCache((int Page, int Dpi, int Rotation) key, out Bitmap? bmp)
    {
        if (_pageCache.TryGetValue(key, out bmp))
        {
            _lruOrder.Remove(key);
            _lruOrder.AddLast(key);
            return true;
        }
        return false;
    }

    private void PutCache((int Page, int Dpi, int Rotation) key, Bitmap bmp)
    {
        // Never synchronously Dispose() bitmaps during eviction or replacement: Avalonia's
        // background compositor may still be rendering a frame referencing the previous Bitmap.
        // Removing from the dictionary lets GC finalize safely after all frames complete.
        if (_pageCache.ContainsKey(key))
        {
            _lruOrder.Remove(key);
        }
        else if (_pageCache.Count >= MaxCacheEntries && _lruOrder.First is not null)
        {
            var oldest = _lruOrder.First.Value;
            _lruOrder.RemoveFirst();
            _pageCache.Remove(oldest);
        }

        _pageCache[key] = bmp;
        _lruOrder.AddLast(key);
    }

    private async Task RenderPageInBackgroundAsync(
        byte[] pdfBytes,
        int zeroBased,
        int dpi,
        int rotation,
        string? filePath,
        bool notifyOnComplete,
        bool showLoadingIndicator = true,
        int? upgradeToDpi = null
    )
    {
        var key = (zeroBased, dpi, rotation);
        if (!_pendingPageRenders.Add(key))
            return;

        if (notifyOnComplete && showLoadingIndicator)
            IsRenderingPage = true;

        try
        {
            var rendered = await Task.Run(() =>
                PdfBitmapRenderer.RenderPagePixels(pdfBytes, zeroBased, dpi, rotation)
            );

            Dispatcher.UIThread.Post(() =>
            {
                _pendingPageRenders.Remove(key);
                IsRenderingPage = _pendingPageRenders.Count > 0;

                if (!string.Equals(CurrentFilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    return;

                var bmp = PdfBitmapRenderer.CreateWriteableBitmap(rendered);
                PutCache(key, bmp);
                PageBitmapReady?.Invoke(zeroBased + 1, bmp);

                if (notifyOnComplete)
                {
                    // If a live continuous-scroll view is subscribed to PageBitmapReady, it updates the
                    // page Image.Source in-place without rebuilding the entire ScrollViewer!
                    if (PageBitmapReady is null)
                        Changed?.Invoke(this, EventArgs.Empty);

                    // Progressive sharpen: if we just finished the fast 96 DPI preview, upgrade to crisp DPI
                    if (
                        upgradeToDpi.HasValue
                        && upgradeToDpi.Value > dpi
                        && Math.Abs((zeroBased + 1) - CurrentPage) <= 1
                        && rotation == RotationDegrees
                        && !_pageCache.ContainsKey((zeroBased, upgradeToDpi.Value, rotation))
                    )
                    {
                        _ = RenderPageInBackgroundAsync(
                            pdfBytes,
                            zeroBased,
                            upgradeToDpi.Value,
                            rotation,
                            filePath,
                            notifyOnComplete: true,
                            showLoadingIndicator: false
                        );
                    }
                    else if (!upgradeToDpi.HasValue)
                    {
                        // Bidirectional silent prefetch (page + 1 AND page - 1) for instant flipping
                        if (
                            zeroBased + 1 < PageCount
                            && !_pageCache.ContainsKey((zeroBased + 1, dpi, rotation))
                        )
                        {
                            _ = RenderPageInBackgroundAsync(
                                pdfBytes,
                                zeroBased + 1,
                                dpi,
                                rotation,
                                filePath,
                                notifyOnComplete: false,
                                showLoadingIndicator: false
                            );
                        }
                        if (
                            zeroBased - 1 >= 0
                            && !_pageCache.ContainsKey((zeroBased - 1, dpi, rotation))
                        )
                        {
                            _ = RenderPageInBackgroundAsync(
                                pdfBytes,
                                zeroBased - 1,
                                dpi,
                                rotation,
                                filePath,
                                notifyOnComplete: false,
                                showLoadingIndicator: false
                            );
                        }
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
            if (NavigationRequested is not null)
                NavigationRequested.Invoke(CurrentPage);
            else
                Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void PreviousPage()
    {
        if (CurrentPage > 1)
        {
            CurrentPage--;
            if (NavigationRequested is not null)
                NavigationRequested.Invoke(CurrentPage);
            else
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
        if (NavigationRequested is not null)
            NavigationRequested.Invoke(CurrentPage);
        else
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetEvidenceAnchors(
        IReadOnlyList<GroundedEvidenceAnchor> anchors,
        int initialIndex = 0,
        string? questionPrompt = null,
        bool notify = true
    )
    {
        var list = (anchors ?? [])
            .Where(a => a is not null)
            .Select(a =>
            {
                int page = a.PageNumber > 0 ? a.PageNumber : 1;
                if (!IsLoadingDocument && PageCount > 0)
                    page = Math.Clamp(page, 1, PageCount);
                return new GroundedEvidenceAnchor(
                    page,
                    a.SectionTitle ?? string.Empty,
                    a.VerbatimQuote ?? string.Empty,
                    a.HighlightRects ?? []
                );
            })
            .ToList();

        if (list.Count == 0)
        {
            ClearEvidenceHighlight(notify: notify);
            return;
        }

        int prevAnchorCount = EvidenceAnchors.Count;
        int clampedIdx = Math.Clamp(initialIndex, 0, list.Count - 1);
        var active = list[clampedIdx];

        EvidenceAnchors = list;
        ActiveEvidenceIndex = clampedIdx;
        ActiveEvidenceQuestionPrompt = questionPrompt;

        if (IsLoadingDocument || PageCount <= 0)
        {
            _pendingJumpPage = active.PageNumber;
            CurrentPage = active.PageNumber;
            if (notify)
                Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        CurrentPage = Math.Clamp(active.PageNumber, 1, PageCount);

        if (!notify)
            return;

        // If the evidence banner already has the same pill structure (e.g. async Knowledge Search rects arriving
        // for an already-open PDF), update overlays + banner in-place and glide smoothly without ScrollViewer teardown!
        bool canUpdateInPlace =
            EvidenceSelectionChangedInPlace is not null
            && FitMode != PdfFitMode.FitPage
            && prevAnchorCount == list.Count
            && prevAnchorCount > 0;

        if (canUpdateInPlace)
        {
            EvidenceSelectionChangedInPlace!.Invoke();
            HighlightFocusRequested?.Invoke(CurrentPage, GetPrimaryFocusRect(active));
        }
        else
        {
            Changed?.Invoke(this, EventArgs.Empty);
            HighlightFocusRequested?.Invoke(CurrentPage, GetPrimaryFocusRect(active));
        }
    }

    public void SetEvidenceHighlight(
        int oneBasedPage,
        IReadOnlyList<NormalizedHighlightRect> highlights,
        string? verbatimQuote = null,
        string? questionPrompt = null
    )
    {
        var anchor = new GroundedEvidenceAnchor(
            Math.Max(1, oneBasedPage),
            string.Empty,
            verbatimQuote ?? string.Empty,
            [.. (highlights ?? [])]
        );
        SetEvidenceAnchors([anchor], 0, questionPrompt);
    }

    public void SelectEvidenceAnchor(int index)
    {
        if (EvidenceAnchors.Count == 0)
            return;

        int clampedIdx = Math.Clamp(index, 0, EvidenceAnchors.Count - 1);
        ActiveEvidenceIndex = clampedIdx;
        var active = EvidenceAnchors[clampedIdx];

        if (IsLoadingDocument || PageCount <= 0)
        {
            _pendingJumpPage = active.PageNumber;
            CurrentPage = active.PageNumber;
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        CurrentPage = Math.Clamp(active.PageNumber, 1, PageCount);
        if (EvidenceSelectionChangedInPlace is not null && FitMode != PdfFitMode.FitPage)
        {
            // Fast in-place update: ZERO ScrollViewer teardown or toolbar rebuild!
            EvidenceSelectionChangedInPlace.Invoke();
            HighlightFocusRequested?.Invoke(CurrentPage, GetPrimaryFocusRect(active));
        }
        else
        {
            Changed?.Invoke(this, EventArgs.Empty);
            HighlightFocusRequested?.Invoke(CurrentPage, GetPrimaryFocusRect(active));
        }
    }

    public void NextEvidenceAnchor()
    {
        if (EvidenceAnchors.Count <= 1)
        {
            FocusActiveEvidence();
            return;
        }
        SelectEvidenceAnchor((ActiveEvidenceIndex + 1) % EvidenceAnchors.Count);
    }

    public void PreviousEvidenceAnchor()
    {
        if (EvidenceAnchors.Count <= 1)
        {
            FocusActiveEvidence();
            return;
        }
        SelectEvidenceAnchor(
            (ActiveEvidenceIndex - 1 + EvidenceAnchors.Count) % EvidenceAnchors.Count
        );
    }

    public void FocusActiveEvidence()
    {
        var active = ActiveEvidenceAnchor;
        if (active is null)
            return;

        if (IsLoadingDocument || PageCount <= 0)
        {
            _pendingJumpPage = active.PageNumber;
            CurrentPage = active.PageNumber;
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        CurrentPage = Math.Clamp(active.PageNumber, 1, PageCount);
        if (HighlightFocusRequested is not null)
            HighlightFocusRequested.Invoke(CurrentPage, GetPrimaryFocusRect(active));
        else if (NavigationRequested is not null)
            NavigationRequested.Invoke(CurrentPage);
        else
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public static NormalizedHighlightRect? GetPrimaryFocusRect(GroundedEvidenceAnchor? anchor)
    {
        if (anchor is null || anchor.HighlightRects.Count == 0)
            return null;
        return anchor.HighlightRects.OrderBy(r => r.Y).First();
    }

    public void ClearEvidenceHighlight(bool notify = true)
    {
        EvidenceAnchors = [];
        ActiveEvidenceIndex = -1;
        ActiveEvidenceQuestionPrompt = null;
        if (notify)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ZoomIn() => ZoomIn(20);

    public void ZoomIn(int step)
    {
        bool wasFitPage = FitMode == PdfFitMode.FitPage;
        FitMode = PdfFitMode.Custom;
        ZoomPercent = Math.Min(350, ZoomPercent + step);
        if (!wasFitPage && ZoomChangedInPlace is not null)
            ZoomChangedInPlace.Invoke();
        else
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ZoomOut() => ZoomOut(20);

    public void ZoomOut(int step)
    {
        bool wasFitPage = FitMode == PdfFitMode.FitPage;
        FitMode = PdfFitMode.Custom;
        ZoomPercent = Math.Max(40, ZoomPercent - step);
        if (!wasFitPage && ZoomChangedInPlace is not null)
            ZoomChangedInPlace.Invoke();
        else
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ResetZoom()
    {
        bool wasFitPage = FitMode == PdfFitMode.FitPage;
        FitMode = PdfFitMode.Custom;
        ZoomPercent = 100;
        if (!wasFitPage && ZoomChangedInPlace is not null)
            ZoomChangedInPlace.Invoke();
        else
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleFitToWidth()
    {
        bool wasFitPage = FitMode == PdfFitMode.FitPage;
        FitMode = FitMode == PdfFitMode.FitWidth ? PdfFitMode.Custom : PdfFitMode.FitWidth;
        if (FitMode == PdfFitMode.FitWidth)
            ZoomPercent = 100;
        if (!wasFitPage && ZoomChangedInPlace is not null)
            ZoomChangedInPlace.Invoke();
        else
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleFitToPage()
    {
        FitMode = FitMode == PdfFitMode.FitPage ? PdfFitMode.FitWidth : PdfFitMode.FitPage;
        if (FitMode != PdfFitMode.Custom)
            ZoomPercent = 100;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleOutline()
    {
        IsOutlineVisible = !IsOutlineVisible;
        if (OutlineVisibilityChanged is not null)
            OutlineVisibilityChanged.Invoke(IsOutlineVisible);
        else
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RotateRight()
    {
        RotationDegrees = (RotationDegrees + 90) % 360;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RotateLeft()
    {
        RotationDegrees = (RotationDegrees + 270) % 360;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleSearchBar()
    {
        IsSearchBarOpen = !IsSearchBarOpen;
        if (!IsSearchBarOpen)
        {
            SearchMatches = [];
            ActiveSearchMatchIndex = -1;
            IsSearchingPdf = false;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void CloseSearchBar()
    {
        if (!IsSearchBarOpen)
            return;
        IsSearchBarOpen = false;
        SearchMatches = [];
        ActiveSearchMatchIndex = -1;
        IsSearchingPdf = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<NormalizedHighlightRect> GetActivePageSearchHighlights()
    {
        if (!IsSearchBarOpen || SearchMatches.Count == 0)
            return [];

        return SearchMatches
            .Where(m => m.PageNumber == CurrentPage)
            .Select(m => m.Rect)
            .ToList();
    }

    public async Task RunSearchAsync(string? rawQuery)
    {
        var query = (rawQuery ?? string.Empty).Trim();
        SearchQuery = query;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        if (string.IsNullOrWhiteSpace(query) || _pdfBytes is null || PageCount <= 0)
        {
            SearchMatches = [];
            ActiveSearchMatchIndex = -1;
            IsSearchingPdf = false;
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var token = cts.Token;
        var bytesSnapshot = _pdfBytes;
        IsSearchingPdf = true;
        Changed?.Invoke(this, EventArgs.Empty);

        try
        {
            var terms = query.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );
            var matches = await Task.Run(
                () =>
                {
                    var found = new List<(int PageNumber, NormalizedHighlightRect Rect)>();
                    using var doc = PdfDocument.Open(bytesSnapshot);
                    for (int p = 1; p <= doc.NumberOfPages; p++)
                    {
                        token.ThrowIfCancellationRequested();
                        var page = doc.GetPage(p);
                        double pw = page.Width > 0 ? page.Width : 612.0;
                        double ph = page.Height > 0 ? page.Height : 792.0;
                        var words = page.GetWords().ToList();
                        if (words.Count == 0)
                            continue;

                        if (terms.Length == 1)
                        {
                            var needle = terms[0];
                            foreach (var w in words)
                            {
                                if (w.Text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                                {
                                    found.Add((p, ToNormalizedRect(w.BoundingBox, pw, ph)));
                                    if (found.Count >= 400)
                                        return found;
                                }
                            }
                        }
                        else
                        {
                            for (int i = 0; i <= words.Count - terms.Length; i++)
                            {
                                bool allMatch = true;
                                for (int k = 0; k < terms.Length; k++)
                                {
                                    if (
                                        !words[i + k]
                                            .Text.Contains(
                                                terms[k],
                                                StringComparison.OrdinalIgnoreCase
                                            )
                                    )
                                    {
                                        allMatch = false;
                                        break;
                                    }
                                }
                                if (allMatch)
                                {
                                    double minL = words
                                        .Skip(i)
                                        .Take(terms.Length)
                                        .Min(w => w.BoundingBox.Left);
                                    double maxR = words
                                        .Skip(i)
                                        .Take(terms.Length)
                                        .Max(w => w.BoundingBox.Right);
                                    double minB = words
                                        .Skip(i)
                                        .Take(terms.Length)
                                        .Min(w => w.BoundingBox.Bottom);
                                    double maxT = words
                                        .Skip(i)
                                        .Take(terms.Length)
                                        .Max(w => w.BoundingBox.Top);
                                    found.Add(
                                        (
                                            p,
                                            new NormalizedHighlightRect(
                                                Math.Clamp(minL / pw, 0, 1),
                                                Math.Clamp((ph - maxT) / ph, 0, 1),
                                                Math.Clamp((maxR - minL) / pw, 0.01, 1),
                                                Math.Clamp((maxT - minB) / ph, 0.01, 1)
                                            )
                                        )
                                    );
                                    if (found.Count >= 400)
                                        return found;
                                }
                            }
                        }
                    }
                    return found;
                },
                token
            );

            if (token.IsCancellationRequested || _searchCts != cts)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested || _searchCts != cts)
                    return;

                SearchMatches = matches;
                IsSearchingPdf = false;
                if (matches.Count > 0)
                {
                    int firstOnOrAfterCurrent = matches.FindIndex(m => m.PageNumber >= CurrentPage);
                    ActiveSearchMatchIndex = firstOnOrAfterCurrent >= 0 ? firstOnOrAfterCurrent : 0;
                    CurrentPage = matches[ActiveSearchMatchIndex].PageNumber;
                }
                else
                {
                    ActiveSearchMatchIndex = -1;
                }
                Changed?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded
        }
        catch
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsSearchingPdf = false;
                Changed?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    public void NextSearchMatch()
    {
        if (SearchMatches.Count == 0)
            return;
        ActiveSearchMatchIndex = (ActiveSearchMatchIndex + 1) % SearchMatches.Count;
        var match = SearchMatches[ActiveSearchMatchIndex];
        CurrentPage = match.PageNumber;
        if (HighlightFocusRequested is not null)
            HighlightFocusRequested.Invoke(match.PageNumber, match.Rect);
        else if (NavigationRequested is not null)
            NavigationRequested.Invoke(CurrentPage);
        else
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void PreviousSearchMatch()
    {
        if (SearchMatches.Count == 0)
            return;
        ActiveSearchMatchIndex =
            (ActiveSearchMatchIndex - 1 + SearchMatches.Count) % SearchMatches.Count;
        var match = SearchMatches[ActiveSearchMatchIndex];
        CurrentPage = match.PageNumber;
        if (HighlightFocusRequested is not null)
            HighlightFocusRequested.Invoke(match.PageNumber, match.Rect);
        else if (NavigationRequested is not null)
            NavigationRequested.Invoke(CurrentPage);
        else
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private static NormalizedHighlightRect ToNormalizedRect(
        UglyToad.PdfPig.Core.PdfRectangle box,
        double pw,
        double ph
    ) =>
        new(
            Math.Clamp(box.Left / pw, 0, 1),
            Math.Clamp((ph - box.Top) / ph, 0, 1),
            Math.Clamp(box.Width / pw, 0.01, 1),
            Math.Clamp(box.Height / ph, 0.01, 1)
        );

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
        // Let GC finalize old WriteableBitmaps safely after Avalonia's compositor finishes rendering.
        _pageCache.Clear();
        _lruOrder.Clear();
    }

    public void Reset()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        ClearCache();
        ClearEvidenceHighlight(notify: false);
        _pendingPageRenders.Clear();
        _pendingJumpPage = null;
        _pdfBytes = null;
        _pageAspectRatios = [];
        RotationDegrees = 0;
        IsSearchBarOpen = false;
        SearchQuery = string.Empty;
        SearchMatches = [];
        ActiveSearchMatchIndex = -1;
        IsSearchingPdf = false;

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
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        ClearCache();
    }
}
