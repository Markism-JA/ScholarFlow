using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Material.Icons;
using ScholarFlow.Core.Knowledge;
using ScholarFlow.Desktop.Services;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views;

/// <summary>
/// Pure-C# built-in Knowledge Hub Source Viewer for Code, Markdown, Plain Text (virtualized
/// line-numbered syntax-highlighted view with evidence line-range highlighting) and Images.
/// </summary>
public static class SourceViewerViewBuilder
{
    public static Control Build(MainWindowViewModel vm)
    {
        var sv = vm.SourceViewer;

        var root = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,Auto,*"),
            RowSpacing = 8,
            Margin = new Thickness(16, 12, 16, 12),
        };

        // 1. Header Toolbar
        var header = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
            ColumnSpacing = 10,
        };

        var backBtn = UiBuilder.GhostButton(
            "Back",
            MaterialIconKind.ArrowLeft,
            vm.ReturnFromSourceViewer
        );
        header.Children.Add(backBtn);

        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                UiBuilder.Icon(
                    sv.Kind == StudySourceKind.Image ? MaterialIconKind.FileImageOutline
                        : sv.Kind == StudySourceKind.Markdown
                            ? MaterialIconKind.LanguageMarkdownOutline
                        : MaterialIconKind.CodeBraces,
                    17,
                    ThemeService.Brush(ThemeService.AccentPrimary)
                ),
                new TextBlock
                {
                    Text = sv.FileName,
                    FontSize = 14,
                    FontWeight = FontWeight.Bold,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };

        if (!string.IsNullOrWhiteSpace(sv.Language))
        {
            titleRow.Children.Add(
                UiBuilder.Pill(
                    sv.Language.ToUpperInvariant(),
                    ThemeService.Brush(ThemeService.AccentPrimary),
                    ThemeService.Brush(ThemeService.AccentSoftBg),
                    fontSize: 10
                )
            );
        }

        if (sv.Kind == StudySourceKind.Image && sv.ImagePixelWidth > 0)
        {
            titleRow.Children.Add(
                UiBuilder.Pill(
                    $"{sv.ImagePixelWidth} × {sv.ImagePixelHeight}",
                    ThemeService.Brush(ThemeService.TextSecondary),
                    ThemeService.Brush(ThemeService.BgElevated),
                    fontSize: 10
                )
            );
        }
        else if (sv.Lines.Count > 0)
        {
            titleRow.Children.Add(
                UiBuilder.Pill(
                    $"{sv.Lines.Count} lines",
                    ThemeService.Brush(ThemeService.TextSecondary),
                    ThemeService.Brush(ThemeService.BgElevated),
                    fontSize: 10
                )
            );
        }

        if (sv.HighlightStartLine.HasValue)
        {
            var rangeLabel =
                sv.HighlightEndLine.HasValue && sv.HighlightEndLine != sv.HighlightStartLine
                    ? $"L{sv.HighlightStartLine}–L{sv.HighlightEndLine}"
                    : $"L{sv.HighlightStartLine}";
            titleRow.Children.Add(
                UiBuilder.Pill(
                    rangeLabel,
                    ThemeService.Brush(ThemeService.PriorityP2Fg),
                    ThemeService.Brush(ThemeService.PriorityP2Bg),
                    fontSize: 10
                )
            );
        }

        Grid.SetColumn(titleRow, 1);
        header.Children.Add(titleRow);

        var rightActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (sv.Kind == StudySourceKind.Image && sv.CurrentImage is not null)
        {
            if (sv.ImageHighlightRects.Count > 0)
            {
                rightActions.Children.Add(
                    UiBuilder.GhostButton(
                        sv.ShowImageHighlights ? "Region Highlight" : "Show Highlight",
                        MaterialIconKind.Target,
                        sv.ToggleImageHighlights,
                        isActive: sv.ShowImageHighlights
                    )
                );
            }
            rightActions.Children.Add(UiBuilder.IconButton(MaterialIconKind.Minus, sv.ZoomOut, 15));
            rightActions.Children.Add(
                UiBuilder.GhostButton(
                    sv.IsFitToWindow ? "Fit" : $"{sv.ZoomPercent}%",
                    null,
                    sv.ToggleFitToWindow,
                    isActive: sv.IsFitToWindow
                )
            );
            rightActions.Children.Add(UiBuilder.IconButton(MaterialIconKind.Plus, sv.ZoomIn, 15));
        }
        else if (!string.IsNullOrEmpty(sv.RawText))
        {
            var copyBtn = UiBuilder.GhostButton(
                "Copy",
                MaterialIconKind.ContentCopy,
                async () =>
                {
                    var top = TopLevel.GetTopLevel(header);
                    if (top?.Clipboard is not null)
                        await top.Clipboard.SetTextAsync(sv.RawText);
                }
            );
            rightActions.Children.Add(copyBtn);
        }

        if (!string.IsNullOrWhiteSpace(sv.CurrentFilePath))
        {
            rightActions.Children.Add(
                UiBuilder.GhostButton(
                    "Open Externally",
                    MaterialIconKind.OpenInNew,
                    () => ExternalFileLauncher.OpenLocalFile(sv.CurrentFilePath)
                )
            );
        }

        Grid.SetColumn(rightActions, 2);
        header.Children.Add(rightActions);
        root.Children.Add(
            new Border
            {
                Background = ThemeService.Brush(ThemeService.BgSurface),
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8),
                Child = header,
            }
        );

        // 1b. Optional Evidence / Visual Region Hint Banner (for quiz evidence jumps on code or images)
        if (
            !string.IsNullOrWhiteSpace(sv.HighlightSectionTitle)
            || !string.IsNullOrWhiteSpace(sv.EvidenceHint)
        )
        {
            var bannerStack = new StackPanel { Spacing = 2 };
            var labelPrefix =
                sv.Kind == StudySourceKind.Image ? "Visual Region Hint" : "Grounded Evidence";
            var rangeSuffix = sv.HighlightStartLine.HasValue
                ? sv.HighlightEndLine.HasValue && sv.HighlightEndLine != sv.HighlightStartLine
                    ? $" · L{sv.HighlightStartLine}–L{sv.HighlightEndLine}"
                    : $" · L{sv.HighlightStartLine}"
                : string.Empty;
            var headingText = !string.IsNullOrWhiteSpace(sv.HighlightSectionTitle)
                ? $"{labelPrefix} · {sv.HighlightSectionTitle}{rangeSuffix}"
                : $"{labelPrefix}{rangeSuffix}";

            bannerStack.Children.Add(
                new TextBlock
                {
                    Text = headingText,
                    FontSize = 11.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                }
            );

            if (!string.IsNullOrWhiteSpace(sv.EvidenceHint))
            {
                bannerStack.Children.Add(
                    new TextBlock
                    {
                        Text =
                            sv.Kind == StudySourceKind.Image
                                ? sv.EvidenceHint
                                : $"\"{sv.EvidenceHint}\"",
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        MaxLines = 3,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                    }
                );
            }

            var banner = new Border
            {
                Background = ThemeService.Brush(ThemeService.AccentSoftBg),
                BorderBrush = ThemeService.Brush(ThemeService.AccentPrimary),
                BorderThickness = new Thickness(3, 1, 1, 1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6),
                Child = bannerStack,
            };
            Grid.SetRow(banner, 1);
            root.Children.Add(banner);
        }

        // 2. Body
        Control body;
        if (sv.IsLoading)
        {
            body = new Border
            {
                Background = ThemeService.Brush(ThemeService.BgSurface),
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = new TextBlock
                {
                    Text = $"Loading {sv.FileName}...",
                    FontSize = 13,
                    Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
        }
        else if (!string.IsNullOrWhiteSpace(sv.ErrorMessage))
        {
            body = new Border
            {
                Background = ThemeService.Brush(ThemeService.BgSurface),
                BorderBrush = ThemeService.Brush(ThemeService.PriorityP1Fg),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20),
                Child = new TextBlock
                {
                    Text = sv.ErrorMessage,
                    FontSize = 13,
                    Foreground = ThemeService.Brush(ThemeService.PriorityP1Fg),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
        }
        else if (sv.Kind == StudySourceKind.Image && sv.CurrentImage is not null)
        {
            double nativeW = Math.Max(1, sv.ImagePixelWidth);
            double nativeH = Math.Max(1, sv.ImagePixelHeight);
            double scale = sv.IsFitToWindow ? 1.0 : (sv.ZoomPercent / 100.0);
            double canvasW = nativeW * scale;
            double canvasH = nativeH * scale;

            var img = new Image
            {
                Source = sv.CurrentImage,
                Width = canvasW,
                Height = canvasH,
                Stretch = Stretch.Fill,
            };

            var imgLayer = new Grid
            {
                Width = canvasW,
                Height = canvasH,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { img },
            };

            if (sv.ShowImageHighlights && sv.ImageHighlightRects.Count > 0)
            {
                var overlay = new Canvas
                {
                    Width = canvasW,
                    Height = canvasH,
                    IsHitTestVisible = false,
                };

                double strokeThickness = Math.Max(2.5, Math.Min(canvasW, canvasH) * 0.005);
                var fillBrush = new SolidColorBrush(Color.FromArgb(48, 245, 158, 11));
                var borderBrush = new SolidColorBrush(Color.FromArgb(230, 245, 158, 11));

                foreach (var rect in sv.ImageHighlightRects)
                {
                    var box = new Border
                    {
                        Width = Math.Max(12, rect.Width * canvasW),
                        Height = Math.Max(12, rect.Height * canvasH),
                        Background = fillBrush,
                        BorderBrush = borderBrush,
                        BorderThickness = new Thickness(strokeThickness),
                        CornerRadius = new CornerRadius(Math.Max(4, strokeThickness * 1.5)),
                    };
                    Canvas.SetLeft(box, rect.X * canvasW);
                    Canvas.SetTop(box, rect.Y * canvasH);
                    overlay.Children.Add(box);
                }

                imgLayer.Children.Add(overlay);
            }

            Control imageHost = sv.IsFitToWindow
                ? new Viewbox
                {
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.Both,
                    Child = imgLayer,
                }
                : new ScrollViewer
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
                        .Auto,
                    Content = imgLayer,
                };

            body = new Border
            {
                Background = ThemeService.Brush(ThemeService.BgSurface),
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Child = imageHost,
            };
        }
        else
        {
            body = BuildVirtualizedCodeViewer(sv);
        }

        Grid.SetRow(body, 2);
        root.Children.Add(body);
        return root;
    }

    private static Border BuildVirtualizedCodeViewer(SourceViewerViewModel sv)
    {
        var monoFont = new FontFamily(
            "JetBrains Mono, Fira Code, Cascadia Code, Consolas, monospace"
        );
        int gutterWidth = Math.Max(38, (sv.Lines.Count.ToString().Length * 9) + 18);

        var lb = new ListBox
        {
            ItemsSource = sv.Lines,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Focusable = false,
            Padding = new Thickness(0, 6, 0, 16),
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<SourceViewerLineItem>(
                (item, _) =>
                {
                    if (item is null)
                        return new Border();

                    var lineGrid = new Grid
                    {
                        ColumnDefinitions = ColumnDefinitions.Parse($"{gutterWidth},*"),
                        ColumnSpacing = 12,
                    };

                    lineGrid.Children.Add(
                        new TextBlock
                        {
                            Text = item.LineNumber.ToString(),
                            FontFamily = monoFont,
                            FontSize = 12,
                            Foreground = item.IsHighlighted
                                ? ThemeService.Brush(ThemeService.AccentPrimary)
                                : ThemeService.Brush(ThemeService.TextMuted),
                            TextAlignment = TextAlignment.Right,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(8, 0, 0, 0),
                        }
                    );

                    var codeTb = new TextBlock
                    {
                        FontFamily = monoFont,
                        FontSize = 12.5,
                        TextWrapping = TextWrapping.NoWrap,
                        VerticalAlignment = VerticalAlignment.Center,
                        [Grid.ColumnProperty] = 1,
                    };
                    SyntaxHighlighter.PopulateInlines(codeTb, item.Text, sv.Language);
                    lineGrid.Children.Add(codeTb);

                    return new Border
                    {
                        Background = item.IsHighlighted
                            ? ThemeService.Brush(ThemeService.AccentSoftBg)
                            : Brushes.Transparent,
                        BorderBrush = item.IsHighlighted
                            ? ThemeService.Brush(ThemeService.AccentPrimary)
                            : Brushes.Transparent,
                        BorderThickness = new Thickness(item.IsHighlighted ? 3 : 0, 0, 0, 0),
                        Padding = new Thickness(item.IsHighlighted ? 0 : 3, 1.5, 12, 1.5),
                        Child = lineGrid,
                    };
                }
            ),
        };

        lb.Styles.Add(
            new Style(x => x.OfType<ListBoxItem>())
            {
                Setters =
                {
                    new Setter(
                        Avalonia.Controls.Primitives.TemplatedControl.PaddingProperty,
                        new Thickness(0)
                    ),
                    new Setter(Layoutable.MarginProperty, new Thickness(0)),
                    new Setter(
                        Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty,
                        Brushes.Transparent
                    ),
                    new Setter(
                        Avalonia.Controls.Primitives.TemplatedControl.BorderThicknessProperty,
                        new Thickness(0)
                    ),
                    new Setter(InputElement.FocusableProperty, false),
                },
            }
        );
        foreach (var pseudo in new[] { ":pointerover", ":selected", ":pressed" })
        {
            var captured = pseudo;
            lb.Styles.Add(
                new Style(x =>
                    x.OfType<ListBoxItem>()
                        .Class(captured)
                        .Template()
                        .OfType<ContentPresenter>()
                        .Name("PART_ContentPresenter")
                )
                {
                    Setters =
                    {
                        new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent),
                    },
                }
            );
        }

        if (sv.HighlightStartLine.HasValue && sv.Lines.Count > 0)
        {
            int targetIdx = Math.Clamp(sv.HighlightStartLine.Value - 1, 0, sv.Lines.Count - 1);
            int contextIdx = Math.Clamp(targetIdx + 6, 0, sv.Lines.Count - 1);
            var targetItem = sv.Lines[targetIdx];
            var contextItem = sv.Lines[contextIdx];
            lb.Loaded += (_, _) =>
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () =>
                    {
                        lb.ScrollIntoView(contextItem);
                        lb.ScrollIntoView(targetItem);
                    },
                    Avalonia.Threading.DispatcherPriority.Background
                );
        }

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = lb,
        };
    }
}
