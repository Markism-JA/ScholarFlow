using Avalonia.Media.Imaging;
using ScholarFlow.Core.Knowledge;
using ScholarFlow.Core.Quiz;

namespace ScholarFlow.Desktop.ViewModels;

public sealed record SourceViewerLineItem(int LineNumber, string Text, bool IsHighlighted);

/// <summary>
/// ViewModel for the built-in Knowledge Hub Source Viewer supporting Code, Markdown,
/// Plain Text (with virtualized syntax-highlighted lines &amp; line-range evidence highlighting),
/// and Images (with zoom/fit controls).
/// </summary>
public sealed class SourceViewerViewModel(Action onChanged) : IDisposable
{
    private readonly Action _onChanged = onChanged;

    public string CurrentFilePath { get; private set; } = string.Empty;
    public string FileName =>
        string.IsNullOrWhiteSpace(CurrentFilePath)
            ? string.Empty
            : Path.GetFileName(CurrentFilePath);
    public StudySourceKind Kind { get; private set; } = StudySourceKind.Unknown;
    public string Language { get; private set; } = string.Empty;
    public bool IsLoading { get; private set; }
    public string? ErrorMessage { get; private set; }

    // Text / Code state
    public IReadOnlyList<SourceViewerLineItem> Lines { get; private set; } = [];
    public string RawText { get; private set; } = string.Empty;
    public int? HighlightStartLine { get; private set; }
    public int? HighlightEndLine { get; private set; }
    public string? HighlightSectionTitle { get; private set; }
    public string? EvidenceHint { get; private set; }

    // Image state
    public Bitmap? CurrentImage { get; private set; }
    public int ImagePixelWidth => CurrentImage?.PixelSize.Width ?? 0;
    public int ImagePixelHeight => CurrentImage?.PixelSize.Height ?? 0;
    public int ZoomPercent { get; private set; } = 100;
    public bool IsFitToWindow { get; private set; } = true;
    public IReadOnlyList<NormalizedHighlightRect> ImageHighlightRects { get; private set; } = [];
    public bool ShowImageHighlights { get; private set; } = true;

    private int _loadGeneration;

    public async Task LoadFileAsync(
        string filePath,
        int? highlightStartLine = null,
        int? highlightEndLine = null,
        string? sectionTitle = null,
        string? evidenceHint = null,
        IReadOnlyList<NormalizedHighlightRect>? imageHighlightRects = null
    )
    {
        int gen = Interlocked.Increment(ref _loadGeneration);
        DisposeImage();

        CurrentFilePath = filePath;
        var (kind, lang) = StudySourceClassifier.Classify(filePath);
        Kind = kind;
        Language = lang;
        HighlightStartLine = highlightStartLine;
        HighlightEndLine = highlightEndLine ?? highlightStartLine;
        HighlightSectionTitle = sectionTitle;
        EvidenceHint = evidenceHint;
        ImageHighlightRects = imageHighlightRects ?? [];
        ShowImageHighlights = true;
        ErrorMessage = null;
        Lines = [];
        RawText = string.Empty;
        ZoomPercent = 100;
        IsFitToWindow = true;
        IsLoading = true;
        _onChanged();

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            IsLoading = false;
            ErrorMessage = "File not found on disk.";
            _onChanged();
            return;
        }

        try
        {
            if (kind == StudySourceKind.Image)
            {
                var bmp = await Task.Run(() =>
                {
                    using var fs = File.OpenRead(filePath);
                    return new Bitmap(fs);
                });

                if (gen != _loadGeneration)
                {
                    bmp.Dispose();
                    return;
                }

                CurrentImage = bmp;
                IsLoading = false;
                _onChanged();
            }
            else
            {
                var (raw, items) = await Task.Run(() =>
                {
                    var text = File.ReadAllText(filePath);
                    var split = text.Replace("\r\n", "\n").Split('\n');
                    var list = new List<SourceViewerLineItem>(split.Length);
                    for (int i = 0; i < split.Length; i++)
                    {
                        int lineNum = i + 1;
                        bool hl =
                            highlightStartLine.HasValue
                            && lineNum >= highlightStartLine.Value
                            && lineNum <= (highlightEndLine ?? highlightStartLine.Value);
                        list.Add(new SourceViewerLineItem(lineNum, split[i], hl));
                    }
                    return (text, list);
                });

                if (gen != _loadGeneration)
                    return;

                RawText = raw;
                Lines = items;
                IsLoading = false;
                _onChanged();
            }
        }
        catch (Exception ex)
        {
            if (gen != _loadGeneration)
                return;

            IsLoading = false;
            ErrorMessage = $"Could not open file: {ex.Message}";
            _onChanged();
        }
    }

    public void ZoomIn()
    {
        if (Kind != StudySourceKind.Image)
            return;
        IsFitToWindow = false;
        ZoomPercent = Math.Min(300, ZoomPercent + 25);
        _onChanged();
    }

    public void ZoomOut()
    {
        if (Kind != StudySourceKind.Image)
            return;
        IsFitToWindow = false;
        ZoomPercent = Math.Max(25, ZoomPercent - 25);
        _onChanged();
    }

    public void ToggleFitToWindow()
    {
        if (Kind != StudySourceKind.Image)
            return;
        IsFitToWindow = !IsFitToWindow;
        if (!IsFitToWindow)
            ZoomPercent = 100;
        _onChanged();
    }

    public void ToggleImageHighlights()
    {
        if (Kind != StudySourceKind.Image || ImageHighlightRects.Count == 0)
            return;
        ShowImageHighlights = !ShowImageHighlights;
        _onChanged();
    }

    public void Reset()
    {
        Interlocked.Increment(ref _loadGeneration);
        DisposeImage();
        CurrentFilePath = string.Empty;
        Kind = StudySourceKind.Unknown;
        Language = string.Empty;
        IsLoading = false;
        ErrorMessage = null;
        Lines = [];
        RawText = string.Empty;
        HighlightStartLine = null;
        HighlightEndLine = null;
        HighlightSectionTitle = null;
        EvidenceHint = null;
        ImageHighlightRects = [];
        ShowImageHighlights = true;
        ZoomPercent = 100;
        IsFitToWindow = true;
    }

    private void DisposeImage()
    {
        var old = CurrentImage;
        CurrentImage = null;
        if (old is not null)
        {
            // Defer disposal until after Avalonia detaches any Image control referencing the old Bitmap
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () =>
                {
                    try
                    {
                        old.Dispose();
                    }
                    catch
                    {
                        // Ignore already-disposed bitmap
                    }
                },
                Avalonia.Threading.DispatcherPriority.Background
            );
        }
    }

    public void Dispose() => DisposeImage();
}
