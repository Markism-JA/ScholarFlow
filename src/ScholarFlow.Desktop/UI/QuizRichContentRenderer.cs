using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Quiz;
using ScholarFlow.Desktop.Theme;

namespace ScholarFlow.Desktop.UI;

/// <summary>
/// Pure-C# rich content renderer for specialized quiz modalities:
/// - Monospace Code Snippet blocks with language pill &amp; one-click copy
/// - LaTeX Mathematical Equations (stacked visual fractions + Unicode math typography)
/// - Visual Node-Flow / Architecture Diagrams
/// </summary>
public static partial class QuizRichContentRenderer
{
    private static readonly FontFamily MonospaceFont = new(
        "JetBrains Mono, Fira Code, Cascadia Mono, Consolas, monospace"
    );

    private static readonly FontFamily MathFont = new(
        "Cambria Math, STIX Two Math, Latin Modern Math, Georgia, serif"
    );

    /// <summary>
    /// Formats inline LaTeX math tokens ($...$, \frac, \sqrt, superscripts, AMS/Greek symbols) into readable mathematical Unicode.
    /// Delegates to the shared token-safe <see cref="LatexMathFormatter"/> in Core.
    /// </summary>
    public static string FormatInlineMath(string? raw) =>
        LatexMathFormatter.FormatInlineMath(raw);

    public static Control BuildCodeSnippetCard(string codeSnippet, string? language)
    {
        var cleanCode = (codeSnippet ?? string.Empty).Trim();
        var langLabel = string.IsNullOrWhiteSpace(language)
            ? "CODE"
            : language.Trim().ToUpperInvariant();

        var outerStack = new StackPanel { Spacing = 6 };
        var topBar = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };

        var leftBadge = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        leftBadge.Children.Add(
            UiBuilder.Icon(
                MaterialIconKind.CodeTags,
                14,
                ThemeService.Brush(ThemeService.AccentPrimary)
            )
        );
        leftBadge.Children.Add(
            new TextBlock
            {
                Text = $"CODE SNIPPET · {langLabel}",
                FontSize = 10.5,
                FontWeight = FontWeight.Bold,
                LetterSpacing = 0.6,
                Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        topBar.Children.Add(leftBadge);

        var codeBlock = new SelectableTextBlock
        {
            FontFamily = MonospaceFont,
            FontSize = 12.5,
            Padding = new Thickness(4, 2),
            TextWrapping = TextWrapping.NoWrap,
        };
        SyntaxHighlighter.PopulateInlines(codeBlock, cleanCode, language);

        var codeScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = codeBlock,
        };

        var copyBtn = UiBuilder.GhostButton(
            "Copy Code",
            MaterialIconKind.ContentCopy,
            async () =>
            {
                var top = TopLevel.GetTopLevel(codeBlock);
                if (top?.Clipboard is not null)
                    await top.Clipboard.SetTextAsync(cleanCode);
            }
        );
        Grid.SetColumn(copyBtn, 1);
        topBar.Children.Add(copyBtn);
        outerStack.Children.Add(topBar);

        outerStack.Children.Add(
            new Border
            {
                Background = ThemeService.Brush(ThemeService.BgBase),
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6),
                Child = codeScroller,
            }
        );

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgElevated),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            Child = outerStack,
        };
    }

    public static Control BuildMathLatexCard(string rawLatex)
    {
        var stack = new StackPanel { Spacing = 8 };
        var topRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        topRow.Children.Add(
            UiBuilder.Icon(
                MaterialIconKind.FunctionVariant,
                14,
                ThemeService.Brush(ThemeService.PriorityP3Fg)
            )
        );
        topRow.Children.Add(
            new TextBlock
            {
                Text = "MATHEMATICAL EXPRESSION",
                FontSize = 10.5,
                FontWeight = FontWeight.Bold,
                LetterSpacing = 0.6,
                Foreground = ThemeService.Brush(ThemeService.PriorityP3Fg),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        stack.Children.Add(topRow);

        Control equationVisual;
        var textColor = (ThemeService.Brush(ThemeService.TextPrimary) as ISolidColorBrush)?.Color
            ?? Colors.White;
        uint argbColor =
            ((uint)textColor.A << 24)
            | ((uint)textColor.R << 16)
            | ((uint)textColor.G << 8)
            | textColor.B;

        var texBmp = LatexBitmapRenderer.TryRenderAvaloniaBitmap(rawLatex, 17f, argbColor);
        if (texBmp is not null)
        {
            // Bitmap is rendered at 2x HiDPI scale; display at 0.5x logical size for crisp typography
            equationVisual = new Image
            {
                Source = texBmp,
                Width = texBmp.PixelSize.Width * 0.5,
                Height = texBmp.PixelSize.Height * 0.5,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else
        {
            equationVisual = new TextBlock
            {
                Text = FormatInlineMath(rawLatex),
                FontFamily = MathFont,
                FontSize = 18,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
        }

        stack.Children.Add(
            new Border
            {
                Background = ThemeService.Brush(ThemeService.BgBase),
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 10),
                Child = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia
                        .Controls
                        .Primitives
                        .ScrollBarVisibility
                        .Auto,
                    VerticalScrollBarVisibility = Avalonia
                        .Controls
                        .Primitives
                        .ScrollBarVisibility
                        .Disabled,
                    Content = equationVisual,
                },
            }
        );

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgElevated),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            Child = stack,
        };
    }

    public static Control BuildDiagramCard(string diagramSpec)
    {
        var normalized = MermaidDiagramService.NormalizeMermaidSource(diagramSpec);
        var title = MermaidDiagramService.DetectDiagramTitle(normalized);
        bool isDark = ThemeService.CurrentTheme == ThemeMode.Dark;

        var stack = new StackPanel { Spacing = 8 };
        var topBar = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };

        var leftBadge = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        leftBadge.Children.Add(
            UiBuilder.Icon(
                title.Contains("SEQUENCE", StringComparison.Ordinal)
                    ? MaterialIconKind.SwapHorizontal
                    : MaterialIconKind.SitemapOutline,
                14,
                ThemeService.Brush(ThemeService.TextSecondary)
            )
        );
        leftBadge.Children.Add(
            new TextBlock
            {
                Text = title,
                FontSize = 10.5,
                FontWeight = FontWeight.Bold,
                LetterSpacing = 0.6,
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        topBar.Children.Add(leftBadge);

        var copyMermaidBtn = UiBuilder.GhostButton(
            "Copy Mermaid",
            MaterialIconKind.ContentCopy,
            async () =>
            {
                var top = TopLevel.GetTopLevel(topBar);
                if (top?.Clipboard is not null)
                    await top.Clipboard.SetTextAsync(normalized);
            }
        );
        ToolTip.SetTip(copyMermaidBtn, "Copy Mermaid diagram source to clipboard");
        Grid.SetColumn(copyMermaidBtn, 1);
        topBar.Children.Add(copyMermaidBtn);
        stack.Children.Add(topBar);

        Control diagramBody;
        var bmp = MermaidBitmapRenderer.TryRenderAvaloniaBitmap(normalized, isDark);
        if (bmp is not null)
        {
            // Bitmap is rendered at 2x HiDPI scale; display at 0.5x logical size for crisp vector-grade quality
            diagramBody = new Image
            {
                Source = bmp,
                Width = bmp.PixelSize.Width * 0.5,
                Height = bmp.PixelSize.Height * 0.5,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else
        {
            diagramBody = new SelectableTextBlock
            {
                Text = normalized,
                FontFamily = MonospaceFont,
                FontSize = 12,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                TextWrapping = TextWrapping.Wrap,
            };
        }

        stack.Children.Add(
            new Border
            {
                Background = isDark
                    ? new SolidColorBrush(Color.Parse("#14161B"))
                    : Brushes.White,
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10),
                Child = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia
                        .Controls
                        .Primitives
                        .ScrollBarVisibility
                        .Auto,
                    VerticalScrollBarVisibility = Avalonia
                        .Controls
                        .Primitives
                        .ScrollBarVisibility
                        .Disabled,
                    Content = diagramBody,
                },
            }
        );

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgElevated),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            Child = stack,
        };
    }

    public static Control BuildQuestionImageCard(string fullImagePath, Action? onZoomClick = null)
    {
        var stack = new StackPanel { Spacing = 8 };
        var header = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        header.Children.Add(
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    UiBuilder.Icon(
                        MaterialIconKind.ImageOutline,
                        14,
                        ThemeService.Brush(ThemeService.AccentPrimary)
                    ),
                    new TextBlock
                    {
                        Text = "DIAGRAM / FIGURE ATTACHMENT",
                        FontSize = 10.5,
                        FontWeight = FontWeight.Bold,
                        LetterSpacing = 0.6,
                        Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            }
        );

        if (onZoomClick is not null)
        {
            var zoomBtn = UiBuilder.GhostButton(
                "Zoom",
                MaterialIconKind.MagnifyPlusOutline,
                onZoomClick
            );
            Grid.SetColumn(zoomBtn, 1);
            header.Children.Add(zoomBtn);
        }
        stack.Children.Add(header);

        try
        {
            var bmp = new Avalonia.Media.Imaging.Bitmap(fullImagePath);
            var img = new Image
            {
                Source = bmp,
                Stretch = Stretch.Uniform,
                MaxHeight = 280,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var imgBorder = new Border
            {
                Background = Brushes.White,
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = img,
            };
            if (onZoomClick is not null)
            {
                imgBorder.Cursor = new Avalonia.Input.Cursor(
                    Avalonia.Input.StandardCursorType.Hand
                );
                imgBorder.PointerPressed += (_, _) => onZoomClick();
            }
            stack.Children.Add(imgBorder);
        }
        catch
        {
            stack.Children.Add(
                new TextBlock
                {
                    Text = $"Could not load image: {Path.GetFileName(fullImagePath)}",
                    FontSize = 11.5,
                    Foreground = ThemeService.Brush(ThemeService.PriorityP1Fg),
                }
            );
        }

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgElevated),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            Child = stack,
        };
    }
}
