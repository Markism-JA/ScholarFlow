using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using ScholarFlow.Core.Quiz;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views;

/// <summary>
/// Builds the Built-in PDF Reader view with a collapsible nested tree outline sidebar, responsive toolbar,
/// Grounded Quiz Evidence banner, and Fit-to-Width page canvas with normalized [0..1] highlight bounding box overlays.
/// </summary>
public static class PdfReaderViewBuilder
{
    public static Control Build(MainWindowViewModel vm)
    {
        var reader = vm.PdfReader;
        var activeNode = reader.FindActiveBookmarkNode();

        var root = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,Auto,*"),
            Margin = new Thickness(12, 8),
        };

        // 1. Responsive Top Toolbar
        var toolbarBorder = new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0, 0, 0, 8),
        };

        var toolbarGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto") };

        var leftGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var backLabel =
            vm.PdfOpenedFromQuiz ? "Back to Quiz"
            : vm.PdfReturnTask is not null ? "Back to Task"
            : "Back";

        leftGroup.Children.Add(
            UiBuilder.GhostButton(backLabel, MaterialIconKind.ArrowLeft, vm.ReturnFromPdfReader)
        );

        if (reader.BookmarkRoots.Count > 0)
        {
            leftGroup.Children.Add(
                UiBuilder.GhostButton(
                    "Outline",
                    MaterialIconKind.FileTreeOutline,
                    reader.ToggleOutline,
                    isActive: reader.IsOutlineVisible
                )
            );
        }
        toolbarGrid.Children.Add(leftGroup);

        var titleText = new TextBlock
        {
            Text = reader.Title,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(titleText, 1);
        toolbarGrid.Children.Add(titleText);

        var rightControls = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var navRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        navRow.Children.Add(
            UiBuilder.IconButton(MaterialIconKind.PageFirst, () => reader.JumpToPage(1))
        );
        navRow.Children.Add(
            UiBuilder.IconButton(MaterialIconKind.ChevronLeft, reader.PreviousPage)
        );
        var pageInput = new TextBox
        {
            Text = reader.CurrentPage.ToString(),
            Width = 52,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6, 3),
        };
        pageInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && int.TryParse(pageInput.Text?.Trim(), out int target))
            {
                e.Handled = true;
                reader.JumpToPage(target);
            }
        };
        navRow.Children.Add(pageInput);
        navRow.Children.Add(
            new TextBlock
            {
                Text = $"/ {Math.Max(1, reader.PageCount)}",
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 2, 0),
            }
        );
        navRow.Children.Add(UiBuilder.IconButton(MaterialIconKind.ChevronRight, reader.NextPage));
        navRow.Children.Add(
            UiBuilder.IconButton(MaterialIconKind.PageLast, () =>
                reader.JumpToPage(reader.PageCount)
            )
        );
        rightControls.Children.Add(navRow);

        var zoomRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        zoomRow.Children.Add(
            UiBuilder.GhostButton(
                "Fit Width",
                MaterialIconKind.ArrowExpandHorizontal,
                reader.ToggleFitToWidth,
                isActive: reader.FitToWidth
            )
        );
        zoomRow.Children.Add(
            UiBuilder.IconButton(MaterialIconKind.MagnifyMinusOutline, reader.ZoomOut)
        );
        zoomRow.Children.Add(
            UiBuilder.GhostButton(
                reader.FitToWidth ? "Auto" : $"{reader.ZoomPercent}%",
                null,
                reader.ResetZoom
            )
        );
        zoomRow.Children.Add(
            UiBuilder.IconButton(MaterialIconKind.MagnifyPlusOutline, reader.ZoomIn)
        );
        zoomRow.Children.Add(
            UiBuilder.GhostButton(
                "Quiz PDF",
                MaterialIconKind.HeadLightbulbOutline,
                vm.OpenQuizStudioForCurrentPdf
            )
        );
        zoomRow.Children.Add(
            UiBuilder.GhostButton("External", MaterialIconKind.OpenInNew, reader.OpenExternal)
        );
        rightControls.Children.Add(zoomRow);

        Grid.SetColumn(rightControls, 2);
        toolbarGrid.Children.Add(rightControls);
        toolbarBorder.Child = toolbarGrid;
        root.Children.Add(toolbarBorder);

        // 1b. Grounded Quiz Evidence Banner (when viewing highlighted proof from a Quiz Question)
        if (reader.ActiveHighlightPage.HasValue)
        {
            var bannerGrid = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,Auto"),
                ColumnSpacing = 10,
            };

            bannerGrid.Children.Add(
                UiBuilder.Icon(
                    MaterialIconKind.Marker,
                    18,
                    ThemeService.Brush(ThemeService.PriorityP2Fg)
                )
            );

            var quoteStack = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (!string.IsNullOrWhiteSpace(reader.ActiveEvidenceQuestionPrompt))
            {
                quoteStack.Children.Add(
                    new TextBlock
                    {
                        Text =
                            $"Grounded Evidence (Page {reader.ActiveHighlightPage.Value}) · {reader.ActiveEvidenceQuestionPrompt}",
                        FontSize = 11.5,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = ThemeService.Brush(ThemeService.PriorityP2Fg),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    }
                );
            }
            if (!string.IsNullOrWhiteSpace(reader.ActiveEvidenceQuote))
            {
                quoteStack.Children.Add(
                    new TextBlock
                    {
                        Text = $"\"{reader.ActiveEvidenceQuote}\"",
                        FontSize = 12,
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                        TextWrapping = TextWrapping.Wrap,
                    }
                );
            }
            Grid.SetColumn(quoteStack, 1);
            bannerGrid.Children.Add(quoteStack);

            if (reader.CurrentPage != reader.ActiveHighlightPage.Value)
            {
                var jumpHighlightBtn = UiBuilder.GhostButton(
                    $"Go to Page {reader.ActiveHighlightPage.Value}",
                    MaterialIconKind.Target,
                    () => reader.JumpToPage(reader.ActiveHighlightPage.Value),
                    isActive: true
                );
                Grid.SetColumn(jumpHighlightBtn, 2);
                bannerGrid.Children.Add(jumpHighlightBtn);
            }

            var clearHighlightBtn = UiBuilder.IconButton(
                MaterialIconKind.Close,
                () => reader.ClearEvidenceHighlight(),
                14,
                ThemeService.Brush(ThemeService.TextMuted)
            );
            Grid.SetColumn(clearHighlightBtn, 3);
            bannerGrid.Children.Add(clearHighlightBtn);

            var bannerBorder = new Border
            {
                Background = ThemeService.Brush(ThemeService.PriorityP2Bg),
                BorderBrush = ThemeService.Brush(ThemeService.PriorityP2Fg),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(12, 8),
                Margin = new Thickness(0, 0, 0, 8),
                Child = bannerGrid,
            };
            Grid.SetRow(bannerBorder, 1);
            root.Children.Add(bannerBorder);
        }

        // 2. Body: Left Nested Tree Outline + Center Page Canvas
        bool showOutline = reader.IsOutlineVisible && reader.BookmarkRoots.Count > 0;
        var body = new Grid
        {
            ColumnDefinitions = showOutline
                ? ColumnDefinitions.Parse("250,10,*")
                : ColumnDefinitions.Parse("0,0,*"),
        };

        if (showOutline)
        {
            var outlineStack = new StackPanel { Spacing = 2, Margin = new Thickness(4) };
            outlineStack.Children.Add(UiBuilder.SectionHeader("Document Outline"));

            foreach (var rootNode in reader.BookmarkRoots)
            {
                outlineStack.Children.Add(BuildBookmarkTreeNode(reader, rootNode, activeNode));
            }

            body.Children.Add(
                new Border
                {
                    Background = ThemeService.Brush(ThemeService.BgSurface),
                    BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(6),
                    Child = UiBuilder.PersistentScrollViewer(
                        $"pdf-reader-outline:{reader.CurrentFilePath}",
                        outlineStack
                    ),
                }
            );
        }

        // 3. Center Page Canvas
        var bmp = reader.GetCurrentPageBitmap();
        Control canvasContent;
        if (!string.IsNullOrEmpty(reader.ErrorMessage))
        {
            canvasContent = new TextBlock
            {
                Text = reader.ErrorMessage,
                Foreground = ThemeService.Brush(ThemeService.PriorityP1Fg),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(24),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else if (bmp is not null)
        {
            var img = new Image
            {
                Source = bmp,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
            };

            if (!reader.FitToWidth)
            {
                img.Width = 720 * (reader.ZoomPercent / 100.0);
            }

            var pageLayerGrid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
            };
            pageLayerGrid.Children.Add(img);

            if (
                reader.ActiveHighlightPage == reader.CurrentPage
                && reader.ActiveHighlights.Count > 0
            )
            {
                pageLayerGrid.Children.Add(
                    new PdfHighlightOverlayControl(reader.ActiveHighlights)
                    {
                        IsHitTestVisible = false,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Stretch,
                    }
                );
            }

            var pageSheet = new Border
            {
                Background = Brushes.White,
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(16, 12),
                Padding = new Thickness(4),
                HorizontalAlignment = reader.FitToWidth
                    ? HorizontalAlignment.Stretch
                    : HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                MaxWidth = reader.FitToWidth ? 860 : double.PositiveInfinity,
                Child = pageLayerGrid,
            };

            var readerScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = reader.FitToWidth
                    ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
                    : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = pageSheet,
            };
            UiBuilder.AttachPdfWheelAndPan(readerScroll, delta =>
            {
                if (delta > 0)
                    reader.ZoomIn(Math.Abs(delta));
                else
                    reader.ZoomOut(Math.Abs(delta));
            });
            canvasContent = readerScroll;
        }
        else if (reader.IsLoadingDocument || reader.IsRenderingPage)
        {
            var skeletonLines = new StackPanel
            {
                Spacing = 12,
                Margin = new Thickness(32, 36),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Children =
                {
                    new TextBlock
                    {
                        Text = reader.IsLoadingDocument
                            ? $"Loading {reader.Title}..."
                            : $"Rendering page {reader.CurrentPage} of {reader.PageCount}...",
                        FontSize = 13,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 0, 0, 8),
                    },
                },
            };

            foreach (double w in new[] { 0.85, 0.92, 0.78, 0.90, 0.65, 0.88, 0.72 })
            {
                skeletonLines.Children.Add(
                    new Border
                    {
                        Height = 12,
                        CornerRadius = new CornerRadius(4),
                        Background = ThemeService.Brush(ThemeService.BgHover),
                        Width = 520 * w,
                        HorizontalAlignment = HorizontalAlignment.Left,
                    }
                );
            }

            canvasContent = new Border
            {
                Background = ThemeService.Brush(ThemeService.BgSurface),
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(24, 18),
                MaxWidth = 640,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = skeletonLines,
            };
        }
        else
        {
            canvasContent = new TextBlock
            {
                Text = "No PDF page loaded.",
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        var canvasBorder = new Border
        {
            Background = ThemeService.Brush(ThemeService.BgBase),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = canvasContent,
        };
        Grid.SetColumn(canvasBorder, 2);
        body.Children.Add(canvasBorder);

        Grid.SetRow(body, 2);
        root.Children.Add(body);

        return root;
    }

    private sealed class PdfHighlightOverlayControl(IReadOnlyList<NormalizedHighlightRect> rects)
        : Control
    {
        private static readonly IBrush HighlightFill = new SolidColorBrush(
            Color.Parse("#48F59E0B")
        );
        private static readonly IPen HighlightBorder = new Pen(
            new SolidColorBrush(Color.Parse("#E6D97706")),
            1.6
        );

        private readonly IReadOnlyList<NormalizedHighlightRect> _rects = rects;

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            double w = Bounds.Width;
            double h = Bounds.Height;
            if (w <= 1 || h <= 1)
                return;

            foreach (var r in _rects)
            {
                var rect = new Rect(
                    r.X * w,
                    r.Y * h,
                    Math.Max(8, r.Width * w),
                    Math.Max(10, r.Height * h)
                );
                context.DrawRectangle(HighlightFill, HighlightBorder, rect, 3, 3);
            }
        }
    }

    private static StackPanel BuildBookmarkTreeNode(
        PdfReaderViewModel reader,
        PdfBookmarkNode node,
        PdfBookmarkNode? activeNode
    )
    {
        bool isCurrent = ReferenceEquals(node, activeNode);
        bool hasChildren = node.Children.Count > 0;

        var container = new StackPanel { Spacing = 1 };

        var rowGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("20,*,Auto") };

        if (hasChildren)
        {
            var chevronBtn = new Button
            {
                Content = UiBuilder.Icon(
                    node.IsExpanded ? MaterialIconKind.ChevronDown : MaterialIconKind.ChevronRight,
                    14,
                    ThemeService.Brush(ThemeService.TextSecondary)
                ),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(2),
                Cursor = new Cursor(StandardCursorType.Hand),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            chevronBtn.Click += (_, e) =>
            {
                e.Handled = true;
                reader.ToggleNodeExpanded(node);
            };
            rowGrid.Children.Add(chevronBtn);
        }
        else
        {
            var dot = UiBuilder.Icon(
                MaterialIconKind.FileDocumentOutline,
                12,
                isCurrent
                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : ThemeService.Brush(ThemeService.TextMuted)
            );
            dot.HorizontalAlignment = HorizontalAlignment.Center;
            rowGrid.Children.Add(dot);
        }

        var normalBg = isCurrent
            ? ThemeService.Brush(ThemeService.AccentSoftBg)
            : Brushes.Transparent;
        var hoverBg = isCurrent
            ? ThemeService.Brush(ThemeService.AccentSoftBg)
            : ThemeService.Brush(ThemeService.BgHover);

        var titleBtn = new Button
        {
            Content = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                Children =
                {
                    new TextBlock
                    {
                        Text = node.Title,
                        FontSize = node.Level == 1 ? 12.5 : 12,
                        FontWeight =
                            isCurrent || node.Level == 1 ? FontWeight.SemiBold : FontWeight.Normal,
                        Foreground =
                            isCurrent ? ThemeService.Brush(ThemeService.AccentPrimary)
                            : node.Level == 1 ? ThemeService.Brush(ThemeService.TextPrimary)
                            : ThemeService.Brush(ThemeService.TextSecondary),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = node.PageNumber.ToString(),
                        FontSize = 11,
                        Foreground = isCurrent
                            ? ThemeService.Brush(ThemeService.AccentPrimary)
                            : ThemeService.Brush(ThemeService.TextMuted),
                        Margin = new Thickness(6, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        [Grid.ColumnProperty] = 1,
                    },
                },
            },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 4.5, 6, 4.5),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        titleBtn.Click += (_, e) =>
        {
            e.Handled = true;
            reader.JumpToPage(node.PageNumber);
        };
        Grid.SetColumn(titleBtn, 1);
        Grid.SetColumnSpan(titleBtn, 2);
        rowGrid.Children.Add(titleBtn);

        var rowBorder = new Border
        {
            Background = normalBg,
            CornerRadius = new CornerRadius(5),
            Child = rowGrid,
        };
        rowBorder.PointerEntered += (_, _) => rowBorder.Background = hoverBg;
        rowBorder.PointerExited += (_, _) => rowBorder.Background = normalBg;

        container.Children.Add(rowBorder);

        // Nested Tree Children with vertical guide line
        if (hasChildren && node.IsExpanded)
        {
            var childrenStack = new StackPanel { Spacing = 1 };
            foreach (var child in node.Children)
            {
                childrenStack.Children.Add(BuildBookmarkTreeNode(reader, child, activeNode));
            }

            container.Children.Add(
                new Border
                {
                    BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                    BorderThickness = new Thickness(1, 0, 0, 0),
                    Margin = new Thickness(10, 1, 0, 3),
                    Padding = new Thickness(8, 0, 0, 0),
                    Child = childrenStack,
                }
            );
        }

        return container;
    }
}
