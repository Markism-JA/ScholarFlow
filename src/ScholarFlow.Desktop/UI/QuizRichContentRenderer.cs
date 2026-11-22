using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
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
        var clean = PdfDiagramGraphRenderer.StripMermaidCodeFences(diagramSpec);
        var stack = new StackPanel { Spacing = 8 };

        bool isSeq = PdfDiagramGraphRenderer.TryParseSequenceDiagram(clean, out var parsedSeq);
        var topRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        topRow.Children.Add(
            UiBuilder.Icon(
                isSeq ? MaterialIconKind.SwapHorizontal : MaterialIconKind.SitemapOutline,
                14,
                ThemeService.Brush(ThemeService.PriorityP2Fg)
            )
        );
        topRow.Children.Add(
            new TextBlock
            {
                Text = isSeq
                    ? "SEQUENCE / INTERACTION DIAGRAM"
                    : "ARCHITECTURE / PROCESS FLOW DIAGRAM",
                FontSize = 10.5,
                FontWeight = FontWeight.Bold,
                LetterSpacing = 0.6,
                Foreground = ThemeService.Brush(ThemeService.PriorityP2Fg),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        stack.Children.Add(topRow);

        Control diagramBody;
        if (isSeq)
        {
            diagramBody = BuildSequenceDiagramCanvas(parsedSeq);
        }
        else if (PdfDiagramGraphRenderer.TryParseGraph(clean, out var parsedGraph))
        {
            diagramBody = BuildFlowchartCanvas(parsedGraph);
        }
        else
        {
            diagramBody = new TextBlock
            {
                Text = clean,
                FontFamily = MonospaceFont,
                FontSize = 12,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
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

    private static Canvas BuildFlowchartCanvas(ParsedDiagramGraph graph)
    {
        bool isLeftToRight = !graph.PreferTopDown;
        var nodes = graph.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        var edges = graph.Edges;

        var layer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in nodes.Keys)
            layer[id] = 0;

        for (int pass = 0; pass < nodes.Count; pass++)
        {
            bool changed = false;
            foreach (var e in edges)
            {
                if (
                    layer.TryGetValue(e.FromId, out int lFrom)
                    && layer.TryGetValue(e.ToId, out int lTo)
                    && lTo <= lFrom
                    && lFrom + 1 < nodes.Count
                )
                {
                    layer[e.ToId] = lFrom + 1;
                    changed = true;
                }
            }
            if (!changed)
                break;
        }

        var byLayer = nodes.Values.GroupBy(n => layer[n.Id]).OrderBy(g => g.Key).ToList();
        if (isLeftToRight && byLayer.Count > 5)
            isLeftToRight = false;

        const double nodeW = 148;
        const double nodeH = 44;
        const double gapX = 56;
        const double gapY = 36;

        var pos = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
        int maxPerLayer = byLayer.Max(g => g.Count());
        double totalW = isLeftToRight
            ? byLayer.Count * (nodeW + gapX)
            : maxPerLayer * (nodeW + gapX);
        double totalH = isLeftToRight
            ? maxPerLayer * (nodeH + gapY)
            : byLayer.Count * (nodeH + gapY);

        for (int li = 0; li < byLayer.Count; li++)
        {
            var group = byLayer[li].ToList();
            for (int idx = 0; idx < group.Count; idx++)
            {
                double x = isLeftToRight
                    ? 16 + li * (nodeW + gapX)
                    : 16
                        + idx * (nodeW + gapX)
                        + (maxPerLayer - group.Count) * (nodeW + gapX) * 0.5;
                double y = isLeftToRight
                    ? 16 + idx * (nodeH + gapY) + (maxPerLayer - group.Count) * (nodeH + gapY) * 0.5
                    : 16 + li * (nodeH + gapY);
                pos[group[idx].Id] = new Point(x, y);
            }
        }

        var canvas = new Canvas
        {
            Width = Math.Max(320, totalW + 24),
            Height = Math.Max(120, totalH + 24),
        };

        // Draw Subgraph Containers
        foreach (
            var sg in nodes
                .Values.Where(n => !string.IsNullOrWhiteSpace(n.Subgraph))
                .GroupBy(n => n.Subgraph)
        )
        {
            double minX = sg.Min(n => pos[n.Id].X) - 10;
            double minY = sg.Min(n => pos[n.Id].Y) - 22;
            double maxX = sg.Max(n => pos[n.Id].X + nodeW) + 10;
            double maxY = sg.Max(n => pos[n.Id].Y + nodeH) + 10;
            var sgBox = new Border
            {
                Width = maxX - minX,
                Height = maxY - minY,
                Background = ThemeService.Brush(ThemeService.AccentSoftBg),
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = new TextBlock
                {
                    Text = sg.Key,
                    FontSize = 10,
                    FontWeight = FontWeight.Bold,
                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    Margin = new Thickness(8, 3),
                },
            };
            Canvas.SetLeft(sgBox, minX);
            Canvas.SetTop(sgBox, minY);
            canvas.Children.Add(sgBox);
        }

        // Draw Edges
        foreach (var e in edges)
        {
            if (!pos.TryGetValue(e.FromId, out var p1) || !pos.TryGetValue(e.ToId, out var p2))
                continue;

            var start = isLeftToRight
                ? new Point(p1.X + nodeW, p1.Y + nodeH * 0.5)
                : new Point(p1.X + nodeW * 0.5, p1.Y + nodeH);
            var end = isLeftToRight
                ? new Point(p2.X, p2.Y + nodeH * 0.5)
                : new Point(p2.X + nodeW * 0.5, p2.Y);

            var lineShape = new Avalonia.Controls.Shapes.Line
            {
                StartPoint = start,
                EndPoint = end,
                Stroke = ThemeService.Brush(ThemeService.AccentPrimary),
                StrokeThickness = 1.6,
                StrokeDashArray = e.IsDashed ? [4, 3] : null,
            };
            canvas.Children.Add(lineShape);

            if (e.IsDirected)
            {
                var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
                var arrowSize = 7.0;
                var a1 = new Point(
                    end.X - arrowSize * Math.Cos(angle - Math.PI / 6),
                    end.Y - arrowSize * Math.Sin(angle - Math.PI / 6)
                );
                var a2 = new Point(
                    end.X - arrowSize * Math.Cos(angle + Math.PI / 6),
                    end.Y - arrowSize * Math.Sin(angle + Math.PI / 6)
                );
                var head = new Avalonia.Controls.Shapes.Polygon
                {
                    Points = [end, a1, a2],
                    Fill = ThemeService.Brush(ThemeService.AccentPrimary),
                };
                canvas.Children.Add(head);
            }

            if (!string.IsNullOrWhiteSpace(e.Label))
            {
                var lblBorder = new Border
                {
                    Background = ThemeService.Brush(ThemeService.BgSurface),
                    BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 1.5),
                    Child = new TextBlock
                    {
                        Text = e.Label,
                        FontSize = 10,
                        Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                    },
                };
                Canvas.SetLeft(lblBorder, (start.X + end.X) * 0.5 - 24);
                Canvas.SetTop(lblBorder, (start.Y + end.Y) * 0.5 - 10);
                canvas.Children.Add(lblBorder);
            }
        }

        // Draw Nodes
        foreach (var n in nodes.Values)
        {
            var p = pos[n.Id];
            bool isCloze = n.IsCloze;
            bool isDecision = n.Shape == DiagramNodeShape.Diamond;
            bool isDb = n.Shape == DiagramNodeShape.CylinderDatabase;
            bool isRounded =
                n.Shape is DiagramNodeShape.RoundedPill or DiagramNodeShape.CircleState;

            var nodeBorder = new Border
            {
                Width = nodeW,
                Height = nodeH,
                Background =
                    isCloze ? ThemeService.Brush(ThemeService.PriorityP2Bg)
                    : isDb ? ThemeService.Brush(ThemeService.PriorityP3Bg)
                    : ThemeService.Brush(ThemeService.BgElevated),
                BorderBrush =
                    isCloze || isDecision ? ThemeService.Brush(ThemeService.PriorityP2Fg)
                    : isDb ? ThemeService.Brush(ThemeService.PriorityP3Fg)
                    : ThemeService.Brush(ThemeService.AccentPrimary),
                BorderThickness = new Thickness(isDecision || isCloze ? 1.5 : 1),
                CornerRadius = new CornerRadius(isRounded ? 20 : 6),
                Padding = new Thickness(6, 4),
                Child = new TextBlock
                {
                    Text = isDecision ? $"◆ {n.Label}" : (isDb ? $"[(DB)] {n.Label}" : n.Label),
                    FontSize = 11.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = isCloze
                        ? ThemeService.Brush(ThemeService.PriorityP2Fg)
                        : ThemeService.Brush(ThemeService.TextPrimary),
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            };
            Canvas.SetLeft(nodeBorder, p.X);
            Canvas.SetTop(nodeBorder, p.Y);
            canvas.Children.Add(nodeBorder);
        }

        return canvas;
    }

    private static Canvas BuildSequenceDiagramCanvas(ParsedSequenceDiagram seq)
    {
        var participants = seq.Participants;
        const double colSpacing = 178;
        double canvasW = Math.Max(360, participants.Count * colSpacing + 30);

        double estimatedH = 85;
        foreach (var s in seq.Steps)
        {
            estimatedH += s.Kind switch
            {
                SequenceStepKind.Message => string.Equals(
                    s.FromId,
                    s.ToId,
                    StringComparison.OrdinalIgnoreCase
                )
                    ? 52
                    : 42,
                SequenceStepKind.Note => 38,
                SequenceStepKind.FragmentStart => 28,
                SequenceStepKind.FragmentElse => 24,
                SequenceStepKind.FragmentEnd => 14,
                _ => 28,
            };
        }

        var canvas = new Canvas { Width = canvasW, Height = estimatedH };
        static double CenterX(int idx) => 95 + idx * colSpacing;

        var indexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < participants.Count; i++)
            indexById[participants[i].Id] = i;

        // 1. Vertical Lifelines + Top & Bottom Participant Headers
        for (int i = 0; i < participants.Count; i++)
        {
            double cx = CenterX(i);
            var lifeline = new Avalonia.Controls.Shapes.Line
            {
                StartPoint = new Point(cx, 38),
                EndPoint = new Point(cx, estimatedH - 38),
                Stroke = ThemeService.Brush(ThemeService.BorderStrong),
                StrokeThickness = 1.2,
                StrokeDashArray = [4, 4],
            };
            canvas.Children.Add(lifeline);

            foreach (double boxTop in new[] { 8.0, estimatedH - 38.0 })
            {
                var box = new Border
                {
                    Width = 134,
                    Height = 30,
                    Background = ThemeService.Brush(ThemeService.BgElevated),
                    BorderBrush = ThemeService.Brush(ThemeService.AccentPrimary),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(participants[i].IsActor ? 15 : 6),
                    Child = new TextBlock
                    {
                        Text = participants[i].Label,
                        FontSize = 11.5,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                };
                Canvas.SetLeft(box, cx - 67);
                Canvas.SetTop(box, boxTop);
                canvas.Children.Add(box);
            }
        }

        // 2. Walk Sequence Steps
        double curY = 58;
        int msgNum = 1;
        var openFragments = new Stack<(double StartY, string Tag, string Label)>();

        foreach (var step in seq.Steps)
        {
            switch (step.Kind)
            {
                case SequenceStepKind.FragmentStart:
                {
                    openFragments.Push((curY - 4, step.FragmentTag ?? "ALT", step.Text));
                    var tagPill = UiBuilder.Pill(
                        $"{step.FragmentTag} {(string.IsNullOrEmpty(step.Text) ? "" : $"[{step.Text}]")}".Trim(),
                        ThemeService.Brush(ThemeService.PriorityP3Fg),
                        ThemeService.Brush(ThemeService.PriorityP3Bg),
                        fontSize: 10
                    );
                    Canvas.SetLeft(tagPill, 18);
                    Canvas.SetTop(tagPill, curY - 4);
                    canvas.Children.Add(tagPill);
                    curY += 26;
                    break;
                }

                case SequenceStepKind.FragmentElse:
                {
                    var divLine = new Avalonia.Controls.Shapes.Line
                    {
                        StartPoint = new Point(16, curY),
                        EndPoint = new Point(canvasW - 16, curY),
                        Stroke = ThemeService.Brush(ThemeService.BorderStrong),
                        StrokeThickness = 1,
                        StrokeDashArray = [4, 3],
                    };
                    canvas.Children.Add(divLine);
                    var elseLbl = new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(step.Text)
                            ? "[else]"
                            : $"[else {step.Text}]",
                        FontSize = 10,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                    };
                    Canvas.SetLeft(elseLbl, 24);
                    Canvas.SetTop(elseLbl, curY + 3);
                    canvas.Children.Add(elseLbl);
                    curY += 22;
                    break;
                }

                case SequenceStepKind.FragmentEnd:
                {
                    if (openFragments.Count > 0)
                    {
                        var (startY, _, _) = openFragments.Pop();
                        var frame = new Border
                        {
                            Width = canvasW - 32,
                            Height = Math.Max(24, curY - startY),
                            BorderBrush = ThemeService.Brush(ThemeService.BorderStrong),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(5),
                            IsHitTestVisible = false,
                        };
                        Canvas.SetLeft(frame, 16);
                        Canvas.SetTop(frame, startY);
                        canvas.Children.Add(frame);
                    }
                    curY += 12;
                    break;
                }

                case SequenceStepKind.Note:
                {
                    int i1 = indexById.GetValueOrDefault(step.FromId, 0);
                    int i2 = indexById.GetValueOrDefault(step.ToId, i1);
                    double x1 = CenterX(Math.Min(i1, i2));
                    double x2 = CenterX(Math.Max(i1, i2));
                    double noteW = i1 == i2 ? 150 : Math.Max(160, x2 - x1 + 70);
                    double noteX = (x1 + x2) * 0.5 - noteW * 0.5;

                    var noteBox = new Border
                    {
                        Width = noteW,
                        Background = ThemeService.Brush(ThemeService.PriorityP2Bg),
                        BorderBrush = ThemeService.Brush(ThemeService.PriorityP2Fg),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(5),
                        Padding = new Thickness(8, 4),
                        Child = new TextBlock
                        {
                            Text = step.Text,
                            FontSize = 10.5,
                            Foreground = ThemeService.Brush(ThemeService.PriorityP2Fg),
                            TextAlignment = TextAlignment.Center,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                    };
                    Canvas.SetLeft(noteBox, noteX);
                    Canvas.SetTop(noteBox, curY - 2);
                    canvas.Children.Add(noteBox);
                    curY += 34;
                    break;
                }

                case SequenceStepKind.Message:
                {
                    int iFrom = indexById.GetValueOrDefault(step.FromId, 0);
                    int iTo = indexById.GetValueOrDefault(step.ToId, 0);
                    double x1 = CenterX(iFrom);
                    double x2 = CenterX(iTo);
                    var labelText = seq.AutoNumber ? $"{msgNum}. {step.Text}" : step.Text;
                    msgNum++;

                    if (iFrom == iTo)
                    {
                        // Self-call loop on the right of lifeline x1
                        double loopW = 44;
                        double loopH = 20;
                        double yTop = curY + 6;
                        double yBot = yTop + loopH;

                        var poly = new Avalonia.Controls.Shapes.Polyline
                        {
                            Points =
                            [
                                new Point(x1, yTop),
                                new Point(x1 + loopW, yTop),
                                new Point(x1 + loopW, yBot),
                                new Point(x1, yBot),
                            ],
                            Stroke = ThemeService.Brush(ThemeService.AccentPrimary),
                            StrokeThickness = 1.6,
                            StrokeDashArray = step.IsDashed ? [5, 3] : null,
                        };
                        canvas.Children.Add(poly);

                        var head = new Avalonia.Controls.Shapes.Polygon
                        {
                            Points =
                            [
                                new Point(x1, yBot),
                                new Point(x1 + 7, yBot - 4),
                                new Point(x1 + 7, yBot + 4),
                            ],
                            Fill = ThemeService.Brush(ThemeService.AccentPrimary),
                        };
                        canvas.Children.Add(head);

                        var selfLbl = new TextBlock
                        {
                            Text = labelText,
                            FontSize = 11,
                            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                        };
                        Canvas.SetLeft(selfLbl, x1 + loopW + 6);
                        Canvas.SetTop(selfLbl, yTop + 1);
                        canvas.Children.Add(selfLbl);
                        curY += 48;
                    }
                    else
                    {
                        double y = curY + 16;
                        var line = new Avalonia.Controls.Shapes.Line
                        {
                            StartPoint = new Point(x1, y),
                            EndPoint = new Point(x2, y),
                            Stroke = ThemeService.Brush(ThemeService.AccentPrimary),
                            StrokeThickness = 1.6,
                            StrokeDashArray = step.IsDashed ? [5, 3] : null,
                        };
                        canvas.Children.Add(line);

                        double dir = x2 >= x1 ? 1 : -1;
                        var head = new Avalonia.Controls.Shapes.Polygon
                        {
                            Points =
                            [
                                new Point(x2, y),
                                new Point(x2 - dir * 7, y - 4),
                                new Point(x2 - dir * 7, y + 4),
                            ],
                            Fill = ThemeService.Brush(ThemeService.AccentPrimary),
                        };
                        canvas.Children.Add(head);

                        var lbl = new TextBlock
                        {
                            Text = labelText,
                            FontSize = 11,
                            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                            TextAlignment = TextAlignment.Center,
                            Width = Math.Abs(x2 - x1) + 40,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        };
                        Canvas.SetLeft(lbl, Math.Min(x1, x2) - 20);
                        Canvas.SetTop(lbl, y - 18);
                        canvas.Children.Add(lbl);
                        curY += 40;
                    }
                    break;
                }
            }
        }

        return canvas;
    }
}
