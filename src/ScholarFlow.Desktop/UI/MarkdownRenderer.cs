using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using ScholarFlow.Desktop.Services;
using ScholarFlow.Desktop.Theme;

namespace ScholarFlow.Desktop.UI;

/// <summary>
/// Pure C# Markdown-to-Avalonia visual tree renderer (Obsidian style, zero .axaml).
/// Supports YAML frontmatter stripping, headings, inline bold/italic/code/links,
/// bulleted &amp; numbered lists, task checklists, blockquotes, horizontal dividers, and fenced code blocks.
/// </summary>
public static partial class MarkdownRenderer
{
    private static readonly FontFamily MonoFont = new(
        "JetBrains Mono, Fira Code, Cascadia Code, DejaVu Sans Mono, monospace"
    );

    [GeneratedRegex(@"^(\d+)\.\s+(.+)$", RegexOptions.Compiled)]
    private static partial Regex OrderedListRegex();

    [GeneratedRegex(@"^[-*+]\s+\[([ xX])\]\s+(.+)$", RegexOptions.Compiled)]
    private static partial Regex ChecklistRegex();

    [GeneratedRegex(@"^[-*+]\s+(.+)$", RegexOptions.Compiled)]
    private static partial Regex BulletListRegex();

    [GeneratedRegex(
        @"(\*\*[^*]+\*\*|__[^_]+__|`[^`]+`|\[[^\]]+\]\([^)]+\)|\*[^*\s][^*]*\*|_[^_\s][^_]*_)",
        RegexOptions.Compiled
    )]
    private static partial Regex InlineTokenRegex();

    public static Control Render(string? markdown, Action<string>? onLinkClicked = null)
    {
        var container = new StackPanel { Spacing = 8 };
        if (string.IsNullOrWhiteSpace(markdown))
            return container;

        var lines = StripYamlFrontmatter(markdown.Replace("\r\n", "\n").Split('\n'));

        bool inCodeFence = false;
        var codeBuffer = new StringBuilder();
        var paragraphBuffer = new List<string>();

        void FlushParagraph()
        {
            if (paragraphBuffer.Count == 0)
                return;

            var joined = string.Join(" ", paragraphBuffer).Trim();
            paragraphBuffer.Clear();
            if (!string.IsNullOrEmpty(joined))
            {
                container.Children.Add(
                    BuildFormattedTextBlock(
                        joined,
                        12.5,
                        ThemeService.Brush(ThemeService.TextPrimary),
                        onLinkClicked
                    )
                );
            }
        }

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();

            // 1. Fenced code block toggle
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                if (!inCodeFence)
                {
                    inCodeFence = true;
                    codeBuffer.Clear();
                }
                else
                {
                    inCodeFence = false;
                    container.Children.Add(BuildCodeBlock(codeBuffer.ToString().TrimEnd()));
                    codeBuffer.Clear();
                }
                continue;
            }

            if (inCodeFence)
            {
                codeBuffer.AppendLine(rawLine);
                continue;
            }

            // 2. Empty line flushes paragraph
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                FlushParagraph();
                continue;
            }

            // 3. Horizontal rule
            if (trimmed is "---" or "***" or "___")
            {
                FlushParagraph();
                container.Children.Add(UiBuilder.HairlineDivider(new Thickness(0, 4)));
                continue;
            }

            // 4. Headings (# .. ####)
            if (trimmed.StartsWith('#'))
            {
                int level = 0;
                while (level < trimmed.Length && trimmed[level] == '#')
                    level++;

                if (
                    level is >= 1 and <= 6
                    && level < trimmed.Length
                    && char.IsWhiteSpace(trimmed[level])
                )
                {
                    FlushParagraph();
                    var headingText = trimmed[(level + 1)..].Trim();
                    container.Children.Add(BuildHeading(headingText, level, onLinkClicked));
                    continue;
                }
            }

            // 5. Blockquote (> ...)
            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                var quoteText = trimmed.TrimStart('>', ' ');
                container.Children.Add(BuildBlockquote(quoteText, onLinkClicked));
                continue;
            }

            // 6. Task checklist (- [ ] / - [x])
            var checkMatch = ChecklistRegex().Match(trimmed);
            if (checkMatch.Success)
            {
                FlushParagraph();
                bool isChecked = !string.IsNullOrWhiteSpace(checkMatch.Groups[1].Value);
                var itemText = checkMatch.Groups[2].Value;
                container.Children.Add(BuildChecklistItem(itemText, isChecked, onLinkClicked));
                continue;
            }

            // 7. Unordered bullet list (- / *)
            var bulletMatch = BulletListRegex().Match(trimmed);
            if (bulletMatch.Success)
            {
                FlushParagraph();
                container.Children.Add(BuildBulletItem(bulletMatch.Groups[1].Value, onLinkClicked));
                continue;
            }

            // 8. Ordered list (1. ...)
            var orderedMatch = OrderedListRegex().Match(trimmed);
            if (orderedMatch.Success)
            {
                FlushParagraph();
                container.Children.Add(
                    BuildOrderedItem(
                        orderedMatch.Groups[1].Value,
                        orderedMatch.Groups[2].Value,
                        onLinkClicked
                    )
                );
                continue;
            }

            paragraphBuffer.Add(trimmed);
        }

        FlushParagraph();
        if (inCodeFence && codeBuffer.Length > 0)
        {
            container.Children.Add(BuildCodeBlock(codeBuffer.ToString().TrimEnd()));
        }

        return container;
    }

    internal static IReadOnlyList<string> StripYamlFrontmatter(string[] lines)
    {
        if (lines.Length < 3 || lines[0].Trim() != "---")
            return lines;

        for (int i = 1; i < Math.Min(lines.Length, 30); i++)
        {
            if (lines[i].Trim() is "---" or "...")
            {
                return lines[(i + 1)..];
            }
        }

        return lines;
    }

    private static Control BuildHeading(string text, int level, Action<string>? onLinkClicked)
    {
        double fontSize = level switch
        {
            1 => 16.5,
            2 => 14.5,
            3 => 13.5,
            _ => 12.5,
        };

        var tb = BuildFormattedTextBlock(
            text,
            fontSize,
            ThemeService.Brush(ThemeService.TextPrimary),
            onLinkClicked,
            defaultWeight: FontWeight.Bold
        );

        if (level <= 2)
        {
            return new StackPanel
            {
                Spacing = 4,
                Margin = new Thickness(0, level == 1 ? 6 : 4, 0, 2),
                Children = { tb, UiBuilder.HairlineDivider() },
            };
        }

        tb.Margin = new Thickness(0, 4, 0, 0);
        return tb;
    }

    private static Border BuildBlockquote(string text, Action<string>? onLinkClicked)
    {
        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgElevated),
            BorderBrush = ThemeService.Brush(ThemeService.AccentPrimary),
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(0, 5, 5, 0),
            Padding = new Thickness(10, 6),
            Child = BuildFormattedTextBlock(
                text,
                12,
                ThemeService.Brush(ThemeService.TextSecondary),
                onLinkClicked
            ),
        };
    }

    private static Grid BuildChecklistItem(
        string text,
        bool isChecked,
        Action<string>? onLinkClicked
    )
    {
        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("20,*") };
        var icon = UiBuilder.Icon(
            isChecked ? MaterialIconKind.CheckboxMarked : MaterialIconKind.CheckboxBlankOutline,
            14.5,
            isChecked
                ? ThemeService.Brush(ThemeService.SuccessFg)
                : ThemeService.Brush(ThemeService.TextSecondary)
        );
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 0, 0);
        grid.Children.Add(icon);

        var tb = BuildFormattedTextBlock(
            text,
            12.5,
            isChecked
                ? ThemeService.Brush(ThemeService.TextSecondary)
                : ThemeService.Brush(ThemeService.TextPrimary),
            onLinkClicked
        );
        if (isChecked)
            tb.TextDecorations = TextDecorations.Strikethrough;

        Grid.SetColumn(tb, 1);
        grid.Children.Add(tb);
        return grid;
    }

    private static Grid BuildBulletItem(string text, Action<string>? onLinkClicked)
    {
        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("16,*") };
        grid.Children.Add(
            new TextBlock
            {
                Text = "•",
                FontSize = 13,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(2, -1, 0, 0),
            }
        );

        var tb = BuildFormattedTextBlock(
            text,
            12.5,
            ThemeService.Brush(ThemeService.TextPrimary),
            onLinkClicked
        );
        Grid.SetColumn(tb, 1);
        grid.Children.Add(tb);
        return grid;
    }

    private static Grid BuildOrderedItem(string number, string text, Action<string>? onLinkClicked)
    {
        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("22,*") };
        grid.Children.Add(
            new TextBlock
            {
                Text = $"{number}.",
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 0, 0),
            }
        );

        var tb = BuildFormattedTextBlock(
            text,
            12.5,
            ThemeService.Brush(ThemeService.TextPrimary),
            onLinkClicked
        );
        Grid.SetColumn(tb, 1);
        grid.Children.Add(tb);
        return grid;
    }

    private static Border BuildCodeBlock(string code)
    {
        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgElevated),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8),
            Child = new SelectableTextBlock
            {
                Text = code,
                FontFamily = MonoFont,
                FontSize = 11.5,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                TextWrapping = TextWrapping.Wrap,
            },
        };
    }

    private static SelectableTextBlock BuildFormattedTextBlock(
        string text,
        double fontSize,
        IBrush foreground,
        Action<string>? onLinkClicked,
        FontWeight defaultWeight = FontWeight.Normal
    )
    {
        var tb = new SelectableTextBlock
        {
            FontSize = fontSize,
            FontWeight = defaultWeight,
            Foreground = foreground,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = fontSize * 1.42,
        };

        var inlines = tb.Inlines ??= [];
        int lastIdx = 0;

        foreach (Match m in InlineTokenRegex().Matches(text))
        {
            if (m.Index > lastIdx)
            {
                inlines.Add(new Run(text[lastIdx..m.Index]));
            }

            var token = m.Value;
            if (
                (token.StartsWith("**") && token.EndsWith("**"))
                || (token.StartsWith("__") && token.EndsWith("__"))
            )
            {
                var inner = token[2..^2];
                inlines.Add(new Run(inner) { FontWeight = FontWeight.Bold });
            }
            else if (token.StartsWith('`') && token.EndsWith('`'))
            {
                var inner = token[1..^1];
                inlines.Add(
                    new Run(inner)
                    {
                        FontFamily = MonoFont,
                        FontSize = Math.Max(10.5, fontSize - 1),
                        Background = ThemeService.Brush(ThemeService.BgElevated),
                        Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
                    }
                );
            }
            else if (token.StartsWith('[') && token.Contains("](") && token.EndsWith(')'))
            {
                int split = token.IndexOf("](", StringComparison.Ordinal);
                var label = token[1..split];
                var url = token[(split + 2)..^1];

                var linkBtn = new Button
                {
                    Content = new TextBlock
                    {
                        Text = label,
                        FontSize = fontSize,
                        Foreground = ThemeService.Brush(ThemeService.PriorityP3Fg),
                        TextDecorations = TextDecorations.Underline,
                    },
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 0, 0, -2),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                linkBtn.Click += (_, e) =>
                {
                    e.Handled = true;
                    if (onLinkClicked is not null)
                        onLinkClicked(url);
                    else
                        ExternalFileLauncher.OpenLocalFile(url);
                };
                inlines.Add(
                    new InlineUIContainer(linkBtn)
                    {
                        BaselineAlignment = BaselineAlignment.Baseline,
                    }
                );
            }
            else if (
                (token.StartsWith('*') && token.EndsWith('*'))
                || (token.StartsWith('_') && token.EndsWith('_'))
            )
            {
                var inner = token[1..^1];
                inlines.Add(new Run(inner) { FontStyle = FontStyle.Italic });
            }
            else
            {
                inlines.Add(new Run(token));
            }

            lastIdx = m.Index + m.Length;
        }

        if (lastIdx < text.Length)
        {
            inlines.Add(new Run(text[lastIdx..]));
        }

        return tb;
    }
}
