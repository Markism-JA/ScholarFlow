using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
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

    [GeneratedRegex(@"\\frac\{([^{}]+)\}\{([^{}]+)\}")]
    private static partial Regex FracInlineRegex();

    [GeneratedRegex(@"\\sqrt\{([^{}]+)\}")]
    private static partial Regex SqrtInlineRegex();

    [GeneratedRegex(@"\\text\{([^{}]+)\}")]
    private static partial Regex TextCommandRegex();

    [GeneratedRegex(@"\\mathbf\{([^{}]+)\}")]
    private static partial Regex MathbfCommandRegex();

    [GeneratedRegex(@"\^\{([^{}]+)\}")]
    private static partial Regex BracedSuperscriptRegex();

    [GeneratedRegex(@"\^([0-9ni])")]
    private static partial Regex SingleSuperscriptRegex();

    [GeneratedRegex(@"_\{([^{}]+)\}")]
    private static partial Regex BracedSubscriptRegex();

    [GeneratedRegex(@"_([0-9ijknm])")]
    private static partial Regex SingleSubscriptRegex();

    [GeneratedRegex(@"^(?<pre>.*?)\\frac\{(?<num>[^{}]+)\}\{(?<den>[^{}]+)\}(?<post>.*)$")]
    private static partial Regex StandaloneFractionRegex();

    [GeneratedRegex(@"\s*(?:-->|==>|->)\s*")]
    private static partial Regex DiagramArrowSplitRegex();

    private static readonly (string Latex, string Symbol)[] LatexReplacements =
    [
        (@"\leq", "≤"),
        (@"\le", "≤"),
        (@"\geq", "≥"),
        (@"\ge", "≥"),
        (@"\neq", "≠"),
        (@"\ne", "≠"),
        (@"\approx", "≈"),
        (@"\equiv", "≡"),
        (@"\pm", "±"),
        (@"\times", "×"),
        (@"\cdot", "·"),
        (@"\div", "÷"),
        (@"\infty", "∞"),
        (@"\Rightarrow", "⇒"),
        (@"\Leftrightarrow", "⇔"),
        (@"\rightarrow", "→"),
        (@"\to", "→"),
        (@"\leftarrow", "←"),
        (@"\forall", "∀"),
        (@"\exists", "∃"),
        (@"\in", "∈"),
        (@"\notin", "∉"),
        (@"\subseteq", "⊆"),
        (@"\subset", "⊂"),
        (@"\cup", "∪"),
        (@"\cap", "∩"),
        (@"\emptyset", "∅"),
        (@"\sum", "∑"),
        (@"\prod", "∏"),
        (@"\int", "∫"),
        (@"\partial", "∂"),
        (@"\nabla", "∇"),
        (@"\Delta", "Δ"),
        (@"\Theta", "Θ"),
        (@"\Lambda", "Λ"),
        (@"\Sigma", "Σ"),
        (@"\Omega", "Ω"),
        (@"\alpha", "α"),
        (@"\beta", "β"),
        (@"\gamma", "γ"),
        (@"\delta", "δ"),
        (@"\epsilon", "ε"),
        (@"\theta", "θ"),
        (@"\lambda", "λ"),
        (@"\mu", "μ"),
        (@"\pi", "π"),
        (@"\sigma", "σ"),
        (@"\tau", "τ"),
        (@"\phi", "φ"),
        (@"\omega", "ω"),
        (@"\log", "log"),
        (@"\ln", "ln"),
        (@"\sin", "sin"),
        (@"\cos", "cos"),
        (@"\tan", "tan"),
        (@"\lim", "lim"),
        (@"\max", "max"),
        (@"\min", "min"),
    ];

    private static readonly Dictionary<char, char> SuperscriptMap = new()
    {
        ['0'] = '⁰',
        ['1'] = '¹',
        ['2'] = '²',
        ['3'] = '³',
        ['4'] = '⁴',
        ['5'] = '⁵',
        ['6'] = '⁶',
        ['7'] = '⁷',
        ['8'] = '⁸',
        ['9'] = '⁹',
        ['+'] = '⁺',
        ['-'] = '⁻',
        ['='] = '⁼',
        ['('] = '⁽',
        [')'] = '⁾',
        ['n'] = 'ⁿ',
        ['i'] = 'ⁱ',
        ['x'] = 'ˣ',
        ['y'] = 'ʸ',
        ['k'] = 'ᵏ',
        ['m'] = 'ᵐ',
    };

    private static readonly Dictionary<char, char> SubscriptMap = new()
    {
        ['0'] = '₀',
        ['1'] = '₁',
        ['2'] = '₂',
        ['3'] = '₃',
        ['4'] = '₄',
        ['5'] = '₅',
        ['6'] = '₆',
        ['7'] = '₇',
        ['8'] = '₈',
        ['9'] = '₉',
        ['+'] = '₊',
        ['-'] = '₋',
        ['='] = '₌',
        ['('] = '₍',
        [')'] = '₎',
        ['a'] = 'ₐ',
        ['e'] = 'ₑ',
        ['i'] = 'ᵢ',
        ['j'] = 'ⱼ',
        ['k'] = 'ₖ',
        ['m'] = 'ₘ',
        ['n'] = 'ₙ',
        ['o'] = 'ₒ',
        ['p'] = 'ₚ',
        ['r'] = 'ᵣ',
        ['s'] = 'ₛ',
        ['t'] = 'ₜ',
        ['u'] = 'ᵤ',
        ['v'] = 'ᵥ',
        ['x'] = 'ₓ',
    };

    /// <summary>
    /// Formats inline LaTeX math tokens ($...$, \frac, \sqrt, superscripts, Greek symbols) into readable mathematical Unicode.
    /// </summary>
    public static string FormatInlineMath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var s = raw.Trim();
        if (!s.Contains('\\') && !s.Contains('$') && !s.Contains('^') && !s.Contains('_'))
            return s;

        s = s.Replace("$$", "").Replace("$", "");
        s = FracInlineRegex().Replace(s, "($1) / ($2)");
        s = SqrtInlineRegex().Replace(s, "√($1)");
        s = TextCommandRegex().Replace(s, "$1");
        s = MathbfCommandRegex().Replace(s, "$1");

        foreach (var (latex, sym) in LatexReplacements)
            s = s.Replace(latex, sym, StringComparison.Ordinal);

        // Superscripts ^{...} or ^x
        s = BracedSuperscriptRegex()
            .Replace(s, m => TryConvertScript(m.Groups[1].Value, SuperscriptMap, '^'));
        s = SingleSuperscriptRegex()
            .Replace(s, m => TryConvertScript(m.Groups[1].Value, SuperscriptMap, '^'));

        // Subscripts _{...} or _x
        s = BracedSubscriptRegex()
            .Replace(s, m => TryConvertScript(m.Groups[1].Value, SubscriptMap, '_'));
        s = SingleSubscriptRegex()
            .Replace(s, m => TryConvertScript(m.Groups[1].Value, SubscriptMap, '_'));

        s = s.Replace(@"\left", "").Replace(@"\right", "").Replace("{", "").Replace("}", "");
        return s.Trim();
    }

    private static string TryConvertScript(
        string content,
        Dictionary<char, char> map,
        char fallbackPrefix
    )
    {
        var chars = new char[content.Length];
        for (int i = 0; i < content.Length; i++)
        {
            if (!map.TryGetValue(content[i], out chars[i]))
                return $"{fallbackPrefix}({content})";
        }
        return new string(chars);
    }

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
        var clean = rawLatex.Trim().Replace("$$", "").Replace("$", "");
        var fracMatch = StandaloneFractionRegex().Match(clean);
        if (fracMatch.Success)
        {
            // Render stacked visual fraction (pre + [num / horizontal bar / den] + post)
            var horiz = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var pre = FormatInlineMath(fracMatch.Groups["pre"].Value);
            if (!string.IsNullOrWhiteSpace(pre))
            {
                horiz.Children.Add(
                    new TextBlock
                    {
                        Text = pre,
                        FontFamily = MathFont,
                        FontSize = 18,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                        VerticalAlignment = VerticalAlignment.Center,
                    }
                );
            }

            var fracStack = new StackPanel
            {
                Spacing = 3,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            fracStack.Children.Add(
                new TextBlock
                {
                    Text = FormatInlineMath(fracMatch.Groups["num"].Value),
                    FontFamily = MathFont,
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    HorizontalAlignment = HorizontalAlignment.Center,
                }
            );
            fracStack.Children.Add(
                new Border
                {
                    Height = 1.5,
                    MinWidth = 48,
                    Background = ThemeService.Brush(ThemeService.TextPrimary),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                }
            );
            fracStack.Children.Add(
                new TextBlock
                {
                    Text = FormatInlineMath(fracMatch.Groups["den"].Value),
                    FontFamily = MathFont,
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    HorizontalAlignment = HorizontalAlignment.Center,
                }
            );
            horiz.Children.Add(fracStack);

            var post = FormatInlineMath(fracMatch.Groups["post"].Value);
            if (!string.IsNullOrWhiteSpace(post))
            {
                horiz.Children.Add(
                    new TextBlock
                    {
                        Text = post,
                        FontFamily = MathFont,
                        FontSize = 18,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                        VerticalAlignment = VerticalAlignment.Center,
                    }
                );
            }

            equationVisual = horiz;
        }
        else
        {
            equationVisual = new TextBlock
            {
                Text = FormatInlineMath(clean),
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
                Padding = new Thickness(16, 12),
                Child = equationVisual,
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
        var clean = (diagramSpec ?? string.Empty).Trim();
        var stack = new StackPanel { Spacing = 8 };

        var topRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        topRow.Children.Add(
            UiBuilder.Icon(
                MaterialIconKind.SitemapOutline,
                14,
                ThemeService.Brush(ThemeService.PriorityP2Fg)
            )
        );
        topRow.Children.Add(
            new TextBlock
            {
                Text = "ARCHITECTURE / PROCESS FLOW DIAGRAM",
                FontSize = 10.5,
                FontWeight = FontWeight.Bold,
                LetterSpacing = 0.6,
                Foreground = ThemeService.Brush(ThemeService.PriorityP2Fg),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        stack.Children.Add(topRow);

        // Parse node chains separated by ->, -->, or ==>
        var nodes = DiagramArrowSplitRegex()
            .Split(clean)
            .Select(n => n.Trim().Trim('[', ']', '(', ')'))
            .Where(n => n.Length > 0)
            .ToList();

        Control diagramBody;
        if (clean.StartsWith("sequenceDiagram", StringComparison.OrdinalIgnoreCase))
        {
            diagramBody = BuildSequenceDiagramCanvas(clean);
        }
        else if (
            clean.Contains('\n')
            || clean.StartsWith("flowchart", StringComparison.OrdinalIgnoreCase)
            || clean.StartsWith("graph ", StringComparison.OrdinalIgnoreCase)
        )
        {
            diagramBody = BuildFlowchartCanvas(clean) ?? (Control)new TextBlock
            {
                Text = clean,
                FontFamily = MonospaceFont,
                FontSize = 12,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                TextWrapping = TextWrapping.Wrap,
            };
        }
        else if (nodes.Count >= 2)
        {
            var flowWrap = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            for (int i = 0; i < nodes.Count; i++)
            {
                bool isCloze = nodes[i].Contains("???", StringComparison.Ordinal);
                var nodeBox = new Border
                {
                    Background = isCloze
                        ? ThemeService.Brush(ThemeService.PriorityP2Bg)
                        : ThemeService.Brush(ThemeService.BgElevated),
                    BorderBrush = isCloze
                        ? ThemeService.Brush(ThemeService.PriorityP2Fg)
                        : ThemeService.Brush(ThemeService.AccentPrimary),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 7),
                    Margin = new Thickness(0, 3, 0, 3),
                    Child = new TextBlock
                    {
                        Text = nodes[i],
                        FontSize = 12.5,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = isCloze
                            ? ThemeService.Brush(ThemeService.PriorityP2Fg)
                            : ThemeService.Brush(ThemeService.TextPrimary),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                flowWrap.Children.Add(nodeBox);

                if (i + 1 < nodes.Count)
                {
                    flowWrap.Children.Add(
                        new TextBlock
                        {
                            Text = "  →  ",
                            FontSize = 15,
                            FontWeight = FontWeight.Bold,
                            Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(2, 0),
                        }
                    );
                }
            }

            diagramBody = flowWrap;
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
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
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
            var zoomBtn = UiBuilder.GhostButton("Zoom", MaterialIconKind.MagnifyPlusOutline, onZoomClick);
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
                imgBorder.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
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

    private sealed record FlowNode(string Id, string Label, string Shape, string? Subgraph);
    private sealed record FlowEdge(string From, string To, string? Label, bool IsDashed);

    private static Control? BuildFlowchartCanvas(string spec)
    {
        var lines = spec.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
            return null;

        bool isLeftToRight = lines[0].Contains("LR", StringComparison.OrdinalIgnoreCase);
        var nodes = new Dictionary<string, FlowNode>(StringComparer.OrdinalIgnoreCase);
        var edges = new List<FlowEdge>();
        string? currentSubgraph = null;

        FlowNode RegisterNode(string rawToken)
        {
            var tok = rawToken.Trim();
            string id = tok;
            string label = tok;
            string shape = "box";

            var mDb = Regex.Match(tok, @"^([A-Za-z0-9_]+)\[\((.+)\)\]$");
            var mDec = Regex.Match(tok, @"^([A-Za-z0-9_]+)\{(.+)\}$");
            var mRnd = Regex.Match(tok, @"^([A-Za-z0-9_]+)\((.+)\)$");
            var mBox = Regex.Match(tok, @"^([A-Za-z0-9_]+)\[(.+)\]$");

            if (mDb.Success)
            {
                id = mDb.Groups[1].Value;
                label = mDb.Groups[2].Value.Trim('"');
                shape = "db";
            }
            else if (mDec.Success)
            {
                id = mDec.Groups[1].Value;
                label = mDec.Groups[2].Value.Trim('"');
                shape = "decision";
            }
            else if (mRnd.Success)
            {
                id = mRnd.Groups[1].Value;
                label = mRnd.Groups[2].Value.Trim('"');
                shape = "rounded";
            }
            else if (mBox.Success)
            {
                id = mBox.Groups[1].Value;
                label = mBox.Groups[2].Value.Trim('"');
            }
            else
            {
                id = tok.Trim('[', ']', '(', ')', '{', '}', '"');
                label = id;
            }

            if (!nodes.TryGetValue(id, out var existing))
            {
                existing = new FlowNode(id, label, shape, currentSubgraph);
                nodes[id] = existing;
            }
            else if (label != id && existing.Label == existing.Id)
            {
                existing = existing with { Label = label, Shape = shape };
                nodes[id] = existing;
            }
            return existing;
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim().TrimEnd(';');
            if (
                line.StartsWith("flowchart", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("graph ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("%%")
            )
                continue;

            if (line.StartsWith("subgraph ", StringComparison.OrdinalIgnoreCase))
            {
                currentSubgraph = line["subgraph ".Length..].Trim().Trim('"');
                continue;
            }
            if (line.Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                currentSubgraph = null;
                continue;
            }

            // Edge patterns: A -->|label| B, A -.-> B, A ==> B, A --> B, A -> B
            var edgeMatch = Regex.Match(
                line,
                @"^(.+?)\s*(-->|-\.->|==>|->)\s*(?:\|([^|]+)\|)?\s*(.+)$"
            );
            if (edgeMatch.Success)
            {
                var left = RegisterNode(edgeMatch.Groups[1].Value);
                var op = edgeMatch.Groups[2].Value;
                var eLabel = edgeMatch.Groups[3].Success ? edgeMatch.Groups[3].Value.Trim() : null;
                var right = RegisterNode(edgeMatch.Groups[4].Value);
                edges.Add(new FlowEdge(left.Id, right.Id, eLabel, op.Contains('.')));
            }
            else
            {
                RegisterNode(line);
            }
        }

        if (nodes.Count == 0)
            return null;

        // Topological layer assignment (cycle-safe)
        var layer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in nodes.Keys)
            layer[id] = 0;

        for (int pass = 0; pass < nodes.Count; pass++)
        {
            bool changed = false;
            foreach (var e in edges)
            {
                if (layer[e.To] <= layer[e.From] && layer[e.From] + 1 < nodes.Count)
                {
                    layer[e.To] = layer[e.From] + 1;
                    changed = true;
                }
            }
            if (!changed)
                break;
        }

        var byLayer = nodes.Values.GroupBy(n => layer[n.Id]).OrderBy(g => g.Key).ToList();
        const double nodeW = 148;
        const double nodeH = 44;
        const double gapX = 56;
        const double gapY = 34;

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
                    : 16 + idx * (nodeW + gapX) + ((maxPerLayer - group.Count) * (nodeW + gapX)) * 0.5;
                double y = isLeftToRight
                    ? 16 + idx * (nodeH + gapY) + ((maxPerLayer - group.Count) * (nodeH + gapY)) * 0.5
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
        foreach (var sg in nodes.Values.Where(n => !string.IsNullOrWhiteSpace(n.Subgraph)).GroupBy(n => n.Subgraph!))
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
            var p1 = pos[e.From];
            var p2 = pos[e.To];
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

            // Arrowhead
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

            if (!string.IsNullOrWhiteSpace(e.Label))
            {
                var lblBorder = new Border
                {
                    Background = ThemeService.Brush(ThemeService.BgSurface),
                    BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(4, 1),
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
            bool isCloze = n.Label.Contains("???", StringComparison.Ordinal);
            bool isDecision = n.Shape == "decision";
            var nodeBorder = new Border
            {
                Width = nodeW,
                Height = nodeH,
                Background = isCloze
                    ? ThemeService.Brush(ThemeService.PriorityP2Bg)
                    : ThemeService.Brush(ThemeService.BgElevated),
                BorderBrush = isCloze || isDecision
                    ? ThemeService.Brush(ThemeService.PriorityP2Fg)
                    : ThemeService.Brush(ThemeService.AccentPrimary),
                BorderThickness = new Thickness(isDecision ? 1.5 : 1),
                CornerRadius = new CornerRadius(n.Shape == "rounded" ? 18 : 6),
                Padding = new Thickness(6, 4),
                Child = new TextBlock
                {
                    Text = isDecision ? $"◆ {n.Label}" : (n.Shape == "db" ? $"[DB] {n.Label}" : n.Label),
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

    private static Control BuildSequenceDiagramCanvas(string spec)
    {
        var lines = spec.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var participants = new List<string>();
        var messages = new List<(string From, string To, string Msg, bool Dashed)>();

        void EnsureParticipant(string p)
        {
            if (!participants.Contains(p, StringComparer.OrdinalIgnoreCase))
                participants.Add(p);
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("sequenceDiagram", StringComparison.OrdinalIgnoreCase))
                continue;

            if (line.StartsWith("participant ", StringComparison.OrdinalIgnoreCase))
            {
                EnsureParticipant(line["participant ".Length..].Trim());
                continue;
            }

            var m = Regex.Match(line, @"^(.+?)\s*(-->>|->>|-->|->)\s*(.+?)\s*:\s*(.+)$");
            if (m.Success)
            {
                var from = m.Groups[1].Value.Trim();
                var op = m.Groups[2].Value;
                var to = m.Groups[3].Value.Trim();
                var msg = m.Groups[4].Value.Trim();
                EnsureParticipant(from);
                EnsureParticipant(to);
                messages.Add((from, to, msg, op.StartsWith("--")));
            }
        }

        if (participants.Count < 2 || messages.Count == 0)
        {
            return new TextBlock
            {
                Text = spec,
                FontFamily = MonospaceFont,
                FontSize = 12,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                TextWrapping = TextWrapping.Wrap,
            };
        }

        const double colSpacing = 175;
        const double rowSpacing = 44;
        double canvasW = Math.Max(340, participants.Count * colSpacing);
        double canvasH = 70 + messages.Count * rowSpacing;

        var canvas = new Canvas { Width = canvasW, Height = canvasH };
        double CenterX(int idx) => 85 + idx * colSpacing;

        // Participant boxes + vertical lifelines
        for (int i = 0; i < participants.Count; i++)
        {
            double cx = CenterX(i);
            var lifeline = new Avalonia.Controls.Shapes.Line
            {
                StartPoint = new Point(cx, 38),
                EndPoint = new Point(cx, canvasH - 12),
                Stroke = ThemeService.Brush(ThemeService.BorderSubtle),
                StrokeThickness = 1.2,
                StrokeDashArray = [4, 4],
            };
            canvas.Children.Add(lifeline);

            var box = new Border
            {
                Width = 126,
                Height = 30,
                Background = ThemeService.Brush(ThemeService.BgElevated),
                BorderBrush = ThemeService.Brush(ThemeService.AccentPrimary),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = new TextBlock
                {
                    Text = participants[i],
                    FontSize = 11.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };
            Canvas.SetLeft(box, cx - 63);
            Canvas.SetTop(box, 8);
            canvas.Children.Add(box);
        }

        // Messages
        for (int mi = 0; mi < messages.Count; mi++)
        {
            var (from, to, msg, dashed) = messages[mi];
            int iFrom = participants.FindIndex(p => p.Equals(from, StringComparison.OrdinalIgnoreCase));
            int iTo = participants.FindIndex(p => p.Equals(to, StringComparison.OrdinalIgnoreCase));
            double x1 = CenterX(iFrom);
            double x2 = CenterX(iTo);
            double y = 64 + mi * rowSpacing;

            var line = new Avalonia.Controls.Shapes.Line
            {
                StartPoint = new Point(x1, y),
                EndPoint = new Point(x2, y),
                Stroke = ThemeService.Brush(ThemeService.AccentPrimary),
                StrokeThickness = 1.6,
                StrokeDashArray = dashed ? [5, 3] : null,
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
                Text = msg,
                FontSize = 11,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                TextAlignment = TextAlignment.Center,
                Width = Math.Abs(x2 - x1) + 40,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Canvas.SetLeft(lbl, Math.Min(x1, x2) - 20);
            Canvas.SetTop(lbl, y - 18);
            canvas.Children.Add(lbl);
        }

        return canvas;
    }
}
