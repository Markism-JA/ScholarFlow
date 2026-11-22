using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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
            RowDefinitions = RowDefinitions.Parse("Auto,Auto,Auto,*"),
            Margin = new Thickness(12, 8),
        };

        // 1. Clean 2-Row Structured Toolbar (Row 1: Back/Outline · Title · Find/Quiz/External | Row 2: Grouped Nav · Fit/Zoom · Rotate)
        var toolbarBorder = new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0, 0, 0, 8),
        };

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

        var backBtn = UiBuilder.GhostButton(
            backLabel,
            MaterialIconKind.ArrowLeft,
            vm.ReturnFromPdfReader
        );
        ToolTip.SetTip(backBtn, "Return to previous workspace view");
        leftGroup.Children.Add(backBtn);

        if (reader.BookmarkRoots.Count > 0)
        {
            var outlineBtn = UiBuilder.GhostButton(
                "Outline",
                MaterialIconKind.FileTreeOutline,
                reader.ToggleOutline,
                isActive: reader.IsOutlineVisible
            );
            ToolTip.SetTip(outlineBtn, "Show or hide document bookmarks & saved sections");
            leftGroup.Children.Add(outlineBtn);
        }

        var titleText = new TextBlock
        {
            Text = reader.Title,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTip.SetTip(titleText, reader.Title);

        var topRightActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var searchBtn = UiBuilder.GhostButton(
            "Find",
            MaterialIconKind.Magnify,
            reader.ToggleSearchBar,
            isActive: reader.IsSearchBarOpen
        );
        ToolTip.SetTip(searchBtn, "Search text inside this PDF (Ctrl+F)");
        topRightActions.Children.Add(searchBtn);

        var quizPdfBtn = UiBuilder.GhostButton(
            "Quiz PDF",
            MaterialIconKind.HeadLightbulbOutline,
            vm.OpenQuizStudioForCurrentPdf
        );
        ToolTip.SetTip(quizPdfBtn, "Open this PDF in Quiz Studio");
        topRightActions.Children.Add(quizPdfBtn);

        var extBtn = UiBuilder.GhostButton("External", MaterialIconKind.OpenInNew, reader.OpenExternal);
        ToolTip.SetTip(extBtn, "Open in system default PDF viewer");
        topRightActions.Children.Add(extBtn);

        var topRowGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto") };
        topRowGrid.Children.Add(leftGroup);
        Grid.SetColumn(titleText, 1);
        topRowGrid.Children.Add(titleText);
        Grid.SetColumn(topRightActions, 2);
        topRowGrid.Children.Add(topRightActions);

        // Row 2: Clean grouped pill controls (Page Nav | Fit & Zoom | Rotate)
        static Border PillGroup(Control inner) =>
            new()
            {
                Background = ThemeService.Brush(ThemeService.BgBase),
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4, 2),
                Margin = new Thickness(0, 0, 6, 2),
                Child = inner,
            };

        var navInner = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var firstBtn = UiBuilder.IconButton(MaterialIconKind.PageFirst, () => reader.JumpToPage(1));
        ToolTip.SetTip(firstBtn, "First page (Home)");
        navInner.Children.Add(firstBtn);

        var prevBtn = UiBuilder.IconButton(MaterialIconKind.ChevronLeft, reader.PreviousPage);
        ToolTip.SetTip(prevBtn, "Previous page (←)");
        navInner.Children.Add(prevBtn);

        var pageInput = new TextBox
        {
            Text = reader.CurrentPage.ToString(),
            Width = 48,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(4, 2),
        };
        ToolTip.SetTip(pageInput, "Type a page number and press Enter to jump");
        pageInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && int.TryParse(pageInput.Text?.Trim(), out int target))
            {
                e.Handled = true;
                reader.JumpToPage(target);
            }
        };
        navInner.Children.Add(pageInput);
        navInner.Children.Add(
            new TextBlock
            {
                Text = $"/ {Math.Max(1, reader.PageCount)}",
                FontSize = 11.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 3, 0),
            }
        );
        var nextBtn = UiBuilder.IconButton(MaterialIconKind.ChevronRight, reader.NextPage);
        ToolTip.SetTip(nextBtn, "Next page (→)");
        navInner.Children.Add(nextBtn);

        var lastBtn = UiBuilder.IconButton(
            MaterialIconKind.PageLast,
            () => reader.JumpToPage(reader.PageCount)
        );
        ToolTip.SetTip(lastBtn, "Last page (End)");
        navInner.Children.Add(lastBtn);

        var zoomInner = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var fitWBtn = UiBuilder.GhostButton(
            "Fit Width",
            MaterialIconKind.ArrowExpandHorizontal,
            reader.ToggleFitToWidth,
            isActive: reader.FitMode == PdfFitMode.FitWidth
        );
        ToolTip.SetTip(fitWBtn, "Scale pages to fit viewport width");
        zoomInner.Children.Add(fitWBtn);

        var fitPBtn = UiBuilder.GhostButton(
            "Fit Page",
            MaterialIconKind.FitToPageOutline,
            reader.ToggleFitToPage,
            isActive: reader.FitMode == PdfFitMode.FitPage
        );
        ToolTip.SetTip(fitPBtn, "Fit entire single page inside viewport");
        zoomInner.Children.Add(fitPBtn);

        var zoomOutBtn = UiBuilder.IconButton(MaterialIconKind.MagnifyMinusOutline, reader.ZoomOut);
        ToolTip.SetTip(zoomOutBtn, "Zoom out (Ctrl + Scroll Down)");
        zoomInner.Children.Add(zoomOutBtn);

        var zoomResetBtn = UiBuilder.GhostButton(
            reader.FitMode == PdfFitMode.FitWidth ? "Width"
                : reader.FitMode == PdfFitMode.FitPage ? "Page"
                : $"{reader.ZoomPercent}%",
            null,
            reader.ResetZoom
        );
        ToolTip.SetTip(zoomResetBtn, "Reset zoom to 100%");
        zoomInner.Children.Add(zoomResetBtn);

        var zoomInBtn = UiBuilder.IconButton(MaterialIconKind.MagnifyPlusOutline, reader.ZoomIn);
        ToolTip.SetTip(zoomInBtn, "Zoom in up to 350% (Ctrl + Scroll Up)");
        zoomInner.Children.Add(zoomInBtn);

        var rotInner = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var rotLBtn = UiBuilder.IconButton(MaterialIconKind.RotateLeft, reader.RotateLeft);
        ToolTip.SetTip(rotLBtn, "Rotate 90° counter-clockwise (Shift+R)");
        rotInner.Children.Add(rotLBtn);

        var rotRBtn = UiBuilder.IconButton(MaterialIconKind.RotateRight, reader.RotateRight);
        ToolTip.SetTip(rotRBtn, "Rotate 90° clockwise (R)");
        rotInner.Children.Add(rotRBtn);

        var controlsBar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        controlsBar.Children.Add(PillGroup(navInner));
        controlsBar.Children.Add(PillGroup(zoomInner));
        controlsBar.Children.Add(PillGroup(rotInner));

        var toolbarStack = new StackPanel { Spacing = 6 };
        toolbarStack.Children.Add(topRowGrid);
        toolbarStack.Children.Add(controlsBar);
        toolbarBorder.Child = toolbarStack;
        root.Children.Add(toolbarBorder);

        // 1a. Inline In-PDF Text Search Bar (Ctrl+F)
        if (reader.IsSearchBarOpen)
        {
            var searchGrid = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto,Auto,Auto,Auto"),
                ColumnSpacing = 6,
            };
            var findBox = new TextBox
            {
                Text = reader.SearchQuery,
                Watermark = "Search text in PDF (Enter to search)...",
                FontSize = 12,
                Padding = new Thickness(8, 4),
            };
            findBox.KeyDown += async (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    if (
                        string.Equals(
                            findBox.Text?.Trim(),
                            reader.SearchQuery,
                            StringComparison.OrdinalIgnoreCase
                        ) && reader.SearchMatches.Count > 0
                    )
                    {
                        reader.NextSearchMatch();
                    }
                    else
                    {
                        await reader.RunSearchAsync(findBox.Text);
                    }
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    reader.CloseSearchBar();
                }
            };
            searchGrid.Children.Add(findBox);

            var findRunBtn = UiBuilder.GhostButton(
                reader.IsSearchingPdf ? "Searching..." : "Search",
                MaterialIconKind.Magnify,
                async () => await reader.RunSearchAsync(findBox.Text)
            );
            Grid.SetColumn(findRunBtn, 1);
            searchGrid.Children.Add(findRunBtn);

            var matchCountText =
                reader.IsSearchingPdf ? "Scanning..."
                : reader.SearchMatches.Count > 0
                    ? $"{reader.ActiveSearchMatchIndex + 1} / {reader.SearchMatches.Count}"
                : !string.IsNullOrWhiteSpace(reader.SearchQuery) ? "0 matches"
                : string.Empty;
            var matchLabel = new TextBlock
            {
                Text = matchCountText,
                FontSize = 11.5,
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0),
                [Grid.ColumnProperty] = 2,
            };
            searchGrid.Children.Add(matchLabel);

            var prevMatchBtn = UiBuilder.IconButton(
                MaterialIconKind.ChevronUp,
                reader.PreviousSearchMatch
            );
            prevMatchBtn.IsEnabled = reader.SearchMatches.Count > 0;
            ToolTip.SetTip(prevMatchBtn, "Previous match");
            Grid.SetColumn(prevMatchBtn, 3);
            searchGrid.Children.Add(prevMatchBtn);

            var nextMatchBtn = UiBuilder.IconButton(
                MaterialIconKind.ChevronDown,
                reader.NextSearchMatch
            );
            nextMatchBtn.IsEnabled = reader.SearchMatches.Count > 0;
            ToolTip.SetTip(nextMatchBtn, "Next match");
            Grid.SetColumn(nextMatchBtn, 4);
            searchGrid.Children.Add(nextMatchBtn);

            var closeFindBtn = UiBuilder.IconButton(MaterialIconKind.Close, reader.CloseSearchBar);
            ToolTip.SetTip(closeFindBtn, "Close search (Esc)");
            Grid.SetColumn(closeFindBtn, 5);
            searchGrid.Children.Add(closeFindBtn);

            var searchBorder = new Border
            {
                Background = ThemeService.Brush(ThemeService.BgSurface),
                BorderBrush = ThemeService.Brush(ThemeService.BorderStrong),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 5),
                Margin = new Thickness(0, 0, 0, 8),
                MaxWidth = 540,
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = searchGrid,
            };
            Grid.SetRow(searchBorder, 1);
            root.Children.Add(searchBorder);
        }

        // 1b. Grounded Quiz Evidence Banner (supports 1 or multiple highlight anchors across pages)
        Action? syncEvidenceBannerInPlace = null;
        if (reader.ActiveHighlightPage.HasValue)
        {
            int totalAnchors = reader.EvidenceAnchors.Count;

            var bannerOuter = new StackPanel { Spacing = 6 };
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

            var metaTextBlock = new TextBlock
            {
                FontSize = 11.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.PriorityP2Fg),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            quoteStack.Children.Add(metaTextBlock);

            var quoteTextBlock = new TextBlock
            {
                FontSize = 12,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            quoteStack.Children.Add(quoteTextBlock);

            Grid.SetColumn(quoteStack, 1);
            bannerGrid.Children.Add(quoteStack);

            var actionRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
            };

            if (totalAnchors > 1)
            {
                var prevHighlightBtn = UiBuilder.IconButton(
                    MaterialIconKind.ChevronLeft,
                    reader.PreviousEvidenceAnchor
                );
                ToolTip.SetTip(prevHighlightBtn, "Previous highlight");
                actionRow.Children.Add(prevHighlightBtn);

                var nextHighlightBtn = UiBuilder.IconButton(
                    MaterialIconKind.ChevronRight,
                    reader.NextEvidenceAnchor
                );
                ToolTip.SetTip(nextHighlightBtn, "Next highlight");
                actionRow.Children.Add(nextHighlightBtn);
            }

            var jumpHighlightBtn = UiBuilder.GhostButton(
                "Jump to Highlight",
                MaterialIconKind.CrosshairsGps,
                reader.FocusActiveEvidence,
                isActive: true
            );
            ToolTip.SetTip(jumpHighlightBtn, "Scroll directly to the highlighted passage");
            actionRow.Children.Add(jumpHighlightBtn);

            Grid.SetColumn(actionRow, 2);
            bannerGrid.Children.Add(actionRow);

            var clearHighlightBtn = UiBuilder.IconButton(
                MaterialIconKind.Close,
                () => reader.ClearEvidenceHighlight(),
                14,
                ThemeService.Brush(ThemeService.TextMuted)
            );
            ToolTip.SetTip(clearHighlightBtn, "Dismiss highlight overlay");
            Grid.SetColumn(clearHighlightBtn, 3);
            bannerGrid.Children.Add(clearHighlightBtn);

            bannerOuter.Children.Add(bannerGrid);

            var pillButtons = new List<Button>();
            if (totalAnchors > 1)
            {
                var pillsRow = new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(28, 0, 0, 0),
                };
                for (int i = 0; i < totalAnchors; i++)
                {
                    int capturedIdx = i;
                    var anc = reader.EvidenceAnchors[i];
                    var pillLabel =
                        anc.HighlightRects.Count > 1
                            ? $"#{i + 1} · p.{anc.PageNumber} ({anc.HighlightRects.Count})"
                            : $"#{i + 1} · p.{anc.PageNumber}";

                    var pillBtn = UiBuilder.GhostButton(
                        pillLabel,
                        MaterialIconKind.Marker,
                        () => reader.SelectEvidenceAnchor(capturedIdx),
                        isActive: false
                    );
                    pillBtn.Margin = new Thickness(0, 0, 4, 2);

                    var tipText = !string.IsNullOrWhiteSpace(anc.VerbatimQuote)
                        ? $"Page {anc.PageNumber}: \"{anc.VerbatimQuote}\""
                        : !string.IsNullOrWhiteSpace(anc.SectionTitle)
                            ? $"Page {anc.PageNumber} · {anc.SectionTitle}"
                            : $"Jump to highlight #{i + 1} on Page {anc.PageNumber}";
                    ToolTip.SetTip(pillBtn, tipText);
                    pillsRow.Children.Add(pillBtn);
                    pillButtons.Add(pillBtn);
                }
                bannerOuter.Children.Add(pillsRow);
            }

            syncEvidenceBannerInPlace = () =>
            {
                if (!reader.ActiveHighlightPage.HasValue)
                    return;

                int count = reader.EvidenceAnchors.Count;
                int activeIdx = Math.Clamp(reader.ActiveEvidenceIndex, 0, Math.Max(0, count - 1));
                int activePage = reader.ActiveHighlightPage.Value;

                var metaParts = new List<string>
                {
                    count > 1
                        ? $"Highlight {activeIdx + 1} of {count} · Page {activePage}"
                        : $"Grounded Evidence · Page {activePage}",
                };
                if (!string.IsNullOrWhiteSpace(reader.ActiveEvidenceSectionTitle))
                    metaParts.Add(reader.ActiveEvidenceSectionTitle);
                if (!string.IsNullOrWhiteSpace(reader.ActiveEvidenceQuestionPrompt))
                    metaParts.Add(reader.ActiveEvidenceQuestionPrompt);
                metaTextBlock.Text = string.Join(" · ", metaParts);

                bool hasQuote = !string.IsNullOrWhiteSpace(reader.ActiveEvidenceQuote);
                quoteTextBlock.IsVisible = hasQuote;
                quoteTextBlock.Text = hasQuote ? $"\"{reader.ActiveEvidenceQuote}\"" : string.Empty;

                var jumpLabel =
                    reader.CurrentPage != activePage ? $"Jump to p.{activePage}" : "Jump to Highlight";
                if (jumpHighlightBtn.Content is StackPanel jsp)
                {
                    foreach (var child in jsp.Children)
                    {
                        if (child is TextBlock jtb)
                        {
                            jtb.Text = jumpLabel;
                            break;
                        }
                    }
                }

                for (int i = 0; i < pillButtons.Count; i++)
                {
                    bool selected = i == activeIdx;
                    var pb = pillButtons[i];
                    pb.Background = selected
                        ? ThemeService.Brush(ThemeService.AccentSoftBg)
                        : Brushes.Transparent;
                    pb.BorderBrush = selected
                        ? ThemeService.Brush(ThemeService.AccentPrimary)
                        : ThemeService.Brush(ThemeService.BorderSubtle);
                    pb.BorderThickness = new Thickness(1);
                    if (pb.Content is StackPanel psp)
                    {
                        foreach (var child in psp.Children)
                        {
                            if (child is TextBlock ptb)
                            {
                                ptb.Foreground = selected
                                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                                    : ThemeService.Brush(ThemeService.TextSecondary);
                                ptb.FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal;
                            }
                        }
                    }
                }
            };
            syncEvidenceBannerInPlace();

            var bannerBorder = new Border
            {
                Background = ThemeService.Brush(ThemeService.PriorityP2Bg),
                BorderBrush = ThemeService.Brush(ThemeService.PriorityP2Fg),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(12, 8),
                Margin = new Thickness(0, 0, 0, 8),
                Child = bannerOuter,
            };
            Grid.SetRow(bannerBorder, 2);
            root.Children.Add(bannerBorder);
        }

        // 2. Body Host: Always mount Outline controls when bookmarks exist so toggling Outline
        //    animates smoothly in-place (docked on >= 920px, floating slide-in -> / <- on < 920px)
        //    without ever tearing down or flashing the PDF canvas!
        bool hasBookmarks = reader.BookmarkRoots.Count > 0;
        var bodyHost = new Grid();
        var body = new Grid
        {
            ColumnDefinitions =
                hasBookmarks && reader.IsOutlineVisible
                    ? ColumnDefinitions.Parse("235,10,*")
                    : ColumnDefinitions.Parse("0,0,*"),
        };

        Border? dockedOutlinePanel = null;
        Grid? floatingOutlineOverlay = null;
        Border? floatingScrim = null;
        Border? floatingDrawer = null;
        TranslateTransform? floatingDrawerTranslate = null;
        bool isConstrainedWidth = false;

        if (hasBookmarks)
        {
            Border BuildOutlineCard(bool isFloating)
            {
                var outlineHeader = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                    Margin = new Thickness(2, 0, 2, 4),
                };
                outlineHeader.Children.Add(UiBuilder.SectionHeader("Document Outline"));
                var closeOutlineBtn = UiBuilder.IconButton(
                    MaterialIconKind.ChevronDoubleLeft,
                    reader.ToggleOutline,
                    15,
                    ThemeService.Brush(ThemeService.TextSecondary)
                );
                ToolTip.SetTip(closeOutlineBtn, "Collapse outline");
                Grid.SetColumn(closeOutlineBtn, 1);
                outlineHeader.Children.Add(closeOutlineBtn);

                var outlineStack = new StackPanel { Spacing = 2, Margin = new Thickness(4) };
                outlineStack.Children.Add(outlineHeader);

                foreach (var rootNode in reader.BookmarkRoots)
                {
                    outlineStack.Children.Add(BuildBookmarkTreeNode(reader, rootNode, activeNode));
                }

                return new Border
                {
                    Background = ThemeService.Brush(ThemeService.BgSurface),
                    BorderBrush = isFloating
                        ? ThemeService.Brush(ThemeService.BorderStrong)
                        : ThemeService.Brush(ThemeService.BorderSubtle),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(6),
                    Width = isFloating ? 265 : double.NaN,
                    HorizontalAlignment = isFloating
                        ? HorizontalAlignment.Left
                        : HorizontalAlignment.Stretch,
                    BoxShadow = isFloating
                        ? BoxShadows.Parse("8 0 28 0 #70000000")
                        : default,
                    Child = UiBuilder.PersistentScrollViewer(
                        $"pdf-reader-outline:{reader.CurrentFilePath}",
                        outlineStack
                    ),
                };
            }

            dockedOutlinePanel = BuildOutlineCard(isFloating: false);
            dockedOutlinePanel.IsVisible = reader.IsOutlineVisible;
            body.Children.Add(dockedOutlinePanel);

            // Floating drawer with GPU-accelerated -> / <- slide transition for constrained widths (< 920px)
            floatingScrim = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#66000000")),
                Opacity = 0,
                IsHitTestVisible = false,
                Cursor = new Cursor(StandardCursorType.Hand),
                Transitions =
                [
                    new DoubleTransition
                    {
                        Property = Visual.OpacityProperty,
                        Duration = TimeSpan.FromMilliseconds(160),
                    },
                ],
            };
            floatingScrim.PointerPressed += (_, _) => reader.ToggleOutline();

            floatingDrawerTranslate = new TranslateTransform(reader.IsOutlineVisible ? 0 : -280, 0)
            {
                Transitions =
                [
                    new DoubleTransition
                    {
                        Property = TranslateTransform.XProperty,
                        Duration = TimeSpan.FromMilliseconds(190),
                        Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
                    },
                ],
            };
            floatingDrawer = BuildOutlineCard(isFloating: true);
            floatingDrawer.Margin = new Thickness(4);
            floatingDrawer.RenderTransform = floatingDrawerTranslate;
            floatingDrawer.IsHitTestVisible = false;

            floatingOutlineOverlay = new Grid
            {
                IsVisible = true,
                IsHitTestVisible = false,
                ClipToBounds = true,
            };
            floatingOutlineOverlay.Children.Add(floatingScrim);
            floatingOutlineOverlay.Children.Add(floatingDrawer);
        }

        void SyncOutlineVisualState()
        {
            if (
                !hasBookmarks
                || dockedOutlinePanel is null
                || floatingOutlineOverlay is null
                || floatingScrim is null
                || floatingDrawer is null
                || floatingDrawerTranslate is null
            )
                return;

            bool open = reader.IsOutlineVisible;
            if (isConstrainedWidth)
            {
                dockedOutlinePanel.IsVisible = false;
                body.ColumnDefinitions = ColumnDefinitions.Parse("0,0,*");
                floatingOutlineOverlay.IsHitTestVisible = open;
                floatingScrim.IsHitTestVisible = open;
                floatingScrim.Opacity = open ? 1.0 : 0.0;
                floatingDrawer.IsHitTestVisible = open;
                floatingDrawerTranslate.X = open ? 0 : -280;
            }
            else
            {
                floatingOutlineOverlay.IsHitTestVisible = false;
                floatingScrim.IsHitTestVisible = false;
                floatingScrim.Opacity = 0.0;
                floatingDrawer.IsHitTestVisible = false;
                floatingDrawerTranslate.X = -280;
                dockedOutlinePanel.IsVisible = open;
                body.ColumnDefinitions = open
                    ? ColumnDefinitions.Parse("235,10,*")
                    : ColumnDefinitions.Parse("0,0,*");
            }
        }

        Action<bool> onOutlineChanged = _ => SyncOutlineVisualState();
        reader.OutlineVisibilityChanged += onOutlineChanged;
        root.DetachedFromVisualTree += (_, _) =>
            reader.OutlineVisibilityChanged -= onOutlineChanged;

        // 3. Center Page Canvas (True Continuous Vertical Scroll for FitWidth/Custom; Crisp Single Page for FitPage)
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
        else if (reader.IsLoadingDocument || reader.PageCount <= 0)
        {
            var skeletonLines = new StackPanel
            {
                Spacing = 12,
                Margin = new Thickness(24, 28),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Children =
                {
                    new TextBlock
                    {
                        Text = reader.IsLoadingDocument
                            ? $"Loading {reader.Title}..."
                            : "No PDF page loaded.",
                        FontSize = 13,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 0, 0, 8),
                    },
                },
            };

            if (reader.IsLoadingDocument)
            {
                foreach (double op in new[] { 0.75, 0.85, 0.65, 0.80, 0.55, 0.72, 0.60 })
                {
                    skeletonLines.Children.Add(
                        new Border
                        {
                            Height = 11,
                            CornerRadius = new CornerRadius(4),
                            Background = ThemeService.Brush(ThemeService.BgHover),
                            Opacity = op,
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                        }
                    );
                }
            }

            canvasContent = new Border
            {
                Background = ThemeService.Brush(ThemeService.BgSurface),
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(20, 16),
                MaxWidth = 560,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Child = skeletonLines,
            };
        }
        else if (reader.FitMode == PdfFitMode.FitPage)
        {
            var bmp = reader.GetPageBitmap(reader.CurrentPage);
            var img = new Image
            {
                Source = bmp,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
            };

            Action<int, Bitmap> onFitBmp = (pg, newBmp) =>
            {
                if (pg == reader.CurrentPage)
                    img.Source = newBmp;
            };
            reader.PageBitmapReady += onFitBmp;
            img.DetachedFromVisualTree += (_, _) => reader.PageBitmapReady -= onFitBmp;

            var pageLayerGrid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
            };
            pageLayerGrid.Children.Add(img);

            var (fitPrimary, fitSecondary) = CollectPageHighlights(reader, reader.CurrentPage);
            if (fitPrimary.Count > 0 || fitSecondary.Count > 0)
            {
                pageLayerGrid.Children.Add(
                    new PdfHighlightOverlayControl(fitPrimary, fitSecondary)
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
                Margin = new Thickness(14, 10),
                Padding = new Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = pageLayerGrid,
            };

            var fitContainer = new Border
            {
                Background = Brushes.Transparent,
                Child = pageSheet,
            };
            fitContainer.AddHandler(
                InputElement.PointerWheelChangedEvent,
                (_, e) =>
                {
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && Math.Abs(e.Delta.Y) > 0.01)
                    {
                        e.Handled = true;
                        if (e.Delta.Y > 0)
                            reader.ZoomIn(15);
                        else
                            reader.ZoomOut(15);
                    }
                    else if (Math.Abs(e.Delta.Y) > 0.01)
                    {
                        e.Handled = true;
                        if (e.Delta.Y < 0)
                            reader.NextPage();
                        else
                            reader.PreviousPage();
                    }
                },
                Avalonia.Interactivity.RoutingStrategies.Tunnel
            );
            canvasContent = fitContainer;
        }
        else
        {
            // TRUE CONTINUOUS VERTICAL SCROLLING — renders in-between pages seamlessly!
            int totalPages = reader.PageCount;
            double pageTargetWidth =
                reader.FitMode == PdfFitMode.FitWidth
                    ? 840
                    : 760 * (reader.ZoomPercent / 100.0);
            double currentPageWidth = pageTargetWidth;
            double GetSlotHeight(int pageNum) =>
                Math.Max(120, currentPageWidth * reader.GetPageAspectRatio(pageNum));
            const double pageGap = 14.0;
            const double sheetChromeH = 10.0; // BorderThickness(1*2) + Padding(4*2)

            var pagesStack = new StackPanel
            {
                Spacing = pageGap,
                Margin = new Thickness(14, 12),
                HorizontalAlignment =
                    reader.FitMode == PdfFitMode.FitWidth
                        ? HorizontalAlignment.Stretch
                        : HorizontalAlignment.Center,
            };

            var pageImages = new Image[totalPages + 1];
            var pageSheets = new Border[totalPages + 1];
            var pageOverlays = new PdfHighlightOverlayControl?[totalPages + 1];
            int initPage = Math.Clamp(reader.CurrentPage, 1, totalPages);

            for (int p = 1; p <= totalPages; p++)
            {
                bool preloadNow = Math.Abs(p - initPage) <= 1;
                var initialBmp = preloadNow ? reader.GetPageBitmap(p) : null;

                var img = new Image
                {
                    Source = initialBmp,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Top,
                };
                if (reader.FitMode == PdfFitMode.Custom)
                    img.Width = pageTargetWidth;
                pageImages[p] = img;

                var layer = new Grid
                {
                    MinHeight = initialBmp is null ? GetSlotHeight(p) : 0,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Top,
                };
                layer.Children.Add(img);

                var (pagePrimary, pageSecondary) = CollectPageHighlights(reader, p);
                if (pagePrimary.Count > 0 || pageSecondary.Count > 0)
                {
                    var ov = new PdfHighlightOverlayControl(pagePrimary, pageSecondary)
                    {
                        IsHitTestVisible = false,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Stretch,
                    };
                    pageOverlays[p] = ov;
                    layer.Children.Add(ov);
                }

                var sheet = new Border
                {
                    Background = Brushes.White,
                    BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(4),
                    HorizontalAlignment =
                        reader.FitMode == PdfFitMode.FitWidth
                            ? HorizontalAlignment.Stretch
                            : HorizontalAlignment.Center,
                    MaxWidth =
                        reader.FitMode == PdfFitMode.FitWidth ? 900 : double.PositiveInfinity,
                    Child = layer,
                };
                pageSheets[p] = sheet;
                pagesStack.Children.Add(sheet);
            }

            var continuousScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility =
                    reader.FitMode == PdfFitMode.FitWidth
                        ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
                        : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = pagesStack,
            };

            UiBuilder.AttachPdfWheelAndPan(
                continuousScroll,
                delta =>
                {
                    if (delta > 0)
                        reader.ZoomIn(Math.Abs(delta));
                    else
                        reader.ZoomOut(Math.Abs(delta));
                }
            );

            // Track pending highlight focus so async bitmap arrival re-centers accurately unless the user scrolled manually
            int? pendingFocusPage =
                reader.ActiveHighlightPage == initPage ? initPage : null;
            NormalizedHighlightRect? pendingFocusRect =
                reader.ActiveHighlightPage == initPage
                    ? PdfReaderViewModel.GetPrimaryFocusRect(reader.ActiveEvidenceAnchor)
                    : null;
            DispatcherTimer? smoothScrollTimer = null;
            bool isApplyingZoom = false;
            DispatcherTimer? zoomSharpenTimer = null;

            void CancelSmoothScroll()
            {
                if (smoothScrollTimer is not null)
                {
                    smoothScrollTimer.Stop();
                    smoothScrollTimer = null;
                }
            }

            // Cancel smooth scroll / pending focus ONLY on real user input (never on Avalonia's async ScrollChanged!)
            continuousScroll.AddHandler(
                InputElement.PointerWheelChangedEvent,
                (_, _) =>
                {
                    CancelSmoothScroll();
                    pendingFocusPage = null;
                    pendingFocusRect = null;
                },
                Avalonia.Interactivity.RoutingStrategies.Tunnel
            );
            continuousScroll.AddHandler(
                InputElement.PointerPressedEvent,
                (_, _) =>
                {
                    CancelSmoothScroll();
                    pendingFocusPage = null;
                    pendingFocusRect = null;
                },
                Avalonia.Interactivity.RoutingStrategies.Tunnel
            );

            void SetScrollOffsetProgrammatic(double y)
            {
                double maxY = Math.Max(
                    0.0,
                    continuousScroll.Extent.Height - continuousScroll.Viewport.Height
                );
                double clampedY = maxY > 1.0 ? Math.Clamp(y, 0.0, maxY) : Math.Max(0.0, y);
                continuousScroll.Offset = new Vector(continuousScroll.Offset.X, clampedY);
            }

            void AnimateScrollToY(double targetY, bool animate)
            {
                CancelSmoothScroll();
                double maxY = Math.Max(
                    0.0,
                    continuousScroll.Extent.Height - continuousScroll.Viewport.Height
                );
                double clampedTarget =
                    maxY > 1.0 ? Math.Clamp(targetY, 0.0, maxY) : Math.Max(0.0, targetY);
                double startY = continuousScroll.Offset.Y;
                double delta = clampedTarget - startY;

                if (!animate || Math.Abs(delta) < 6.0)
                {
                    SetScrollOffsetProgrammatic(clampedTarget);
                    SyncVisiblePages();
                    return;
                }

                // For long cross-document jumps (> 2800px), jump close immediately and glide the final 360px
                if (Math.Abs(delta) > 2800.0)
                {
                    startY = clampedTarget - Math.Sign(delta) * 360.0;
                    delta = clampedTarget - startY;
                    SetScrollOffsetProgrammatic(startY);
                }

                const double durationMs = 175.0;
                var startUtc = DateTime.UtcNow;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(14) };
                smoothScrollTimer = timer;
                timer.Tick += (_, _) =>
                {
                    double elapsed = (DateTime.UtcNow - startUtc).TotalMilliseconds;
                    double t = Math.Clamp(elapsed / durationMs, 0.0, 1.0);
                    // Cubic ease-out: snappy start, silky settle
                    double eased = 1.0 - Math.Pow(1.0 - t, 3.0);
                    SetScrollOffsetProgrammatic(startY + delta * eased);
                    if (t >= 1.0)
                    {
                        CancelSmoothScroll();
                        SyncVisiblePages();
                    }
                };
                timer.Start();
            }

            // Smooth virtualized viewport tracking: loads visible +/- 1 pages and updates page box in-place
            int lastReportedPage = initPage;

            void ScrollToPageAndRectInPlace(
                int targetPage,
                NormalizedHighlightRect? targetRect,
                bool animate = false
            )
            {
                int clamped = Math.Clamp(targetPage, 1, totalPages);

                // Ensure placeholder heights match the live FitWidth viewport width before computing targetY
                if (reader.FitMode == PdfFitMode.FitWidth && continuousScroll.Viewport.Width > 100)
                {
                    double liveW = Math.Min(900, continuousScroll.Viewport.Width - 36);
                    if (Math.Abs(liveW - currentPageWidth) > 2.0)
                    {
                        currentPageWidth = liveW;
                        for (int p = 1; p <= totalPages; p++)
                        {
                            if (pageImages[p].Source is null && pageSheets[p].Child is Grid g)
                                g.MinHeight = GetSlotHeight(p);
                        }
                    }
                }

                // Kick off target page bitmap decode immediately on frame 0
                if (pageImages[clamped].Source is null)
                {
                    var pre = reader.GetPageBitmap(clamped);
                    if (pre is not null)
                    {
                        pageImages[clamped].Source = pre;
                        if (pageSheets[clamped].Child is Grid g)
                            g.MinHeight = 0;
                    }
                }

                double pageTopY = 12.0;
                for (int p = 1; p < clamped; p++)
                {
                    double h = pageSheets[p].Bounds.Height > 10
                        ? pageSheets[p].Bounds.Height
                        : GetSlotHeight(p);
                    pageTopY += h + pageGap;
                }

                double targetY = pageTopY;
                if (targetRect is not null)
                {
                    double targetPageH = pageSheets[clamped].Bounds.Height > 10
                        ? pageSheets[clamped].Bounds.Height
                        : GetSlotHeight(clamped);
                    double viewH = continuousScroll.Viewport.Height > 100
                        ? continuousScroll.Viewport.Height
                        : 650;
                    double highlightY = pageTopY + (targetRect.Y * targetPageH);
                    double topLead = Math.Max(56.0, viewH * 0.28);
                    targetY = Math.Max(0.0, highlightY - topLead);
                }

                lastReportedPage = clamped;
                if (!pageInput.IsFocused)
                    pageInput.Text = clamped.ToString();

                AnimateScrollToY(targetY, animate);
            }

            void ScrollToPageInPlace(int targetPage) =>
                ScrollToPageAndRectInPlace(targetPage, null, animate: true);

            void RefreshHighlightOverlaysInPlace()
            {
                for (int p = 1; p <= totalPages; p++)
                {
                    var (pri, sec) = CollectPageHighlights(reader, p);
                    if (pageOverlays[p] is not null)
                    {
                        pageOverlays[p]!.UpdateHighlights(pri, sec);
                    }
                    else if ((pri.Count > 0 || sec.Count > 0) && pageSheets[p].Child is Grid g)
                    {
                        var ov = new PdfHighlightOverlayControl(pri, sec)
                        {
                            IsHitTestVisible = false,
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            VerticalAlignment = VerticalAlignment.Stretch,
                        };
                        pageOverlays[p] = ov;
                        g.Children.Add(ov);
                    }
                }
            }

            // Live in-place bitmap swap when background renders complete (ZERO ScrollViewer teardown!)
            Action<int, Bitmap> onContinuousBmp = (pg, newBmp) =>
            {
                if (pg >= 1 && pg <= totalPages)
                {
                    pageImages[pg].Source = newBmp;
                    if (pageSheets[pg].Child is Grid g)
                        g.MinHeight = 0;

                    if (pendingFocusPage == pg)
                    {
                        var rectToFocus = pendingFocusRect;
                        Dispatcher.UIThread.Post(
                            () =>
                            {
                                if (pendingFocusPage == pg)
                                {
                                    ScrollToPageAndRectInPlace(pg, rectToFocus, animate: false);
                                    pendingFocusPage = null;
                                    pendingFocusRect = null;
                                }
                            },
                            DispatcherPriority.Render
                        );
                    }
                }
            };
            reader.PageBitmapReady += onContinuousBmp;

            void ApplyZoomInPlace()
            {
                CancelSmoothScroll();
                isApplyingZoom = true;

                // 1. Capture exact (anchorPage, anchorFraction) at 40% down the viewport BEFORE resizing.
                //    This eliminates cumulative drift on large multi-hundred-page books caused by non-scaling page gaps!
                double viewH = continuousScroll.Viewport.Height > 100
                    ? continuousScroll.Viewport.Height
                    : 650.0;
                double anchorOffsetInViewport = viewH * 0.40;
                double anchorViewY = continuousScroll.Offset.Y + anchorOffsetInViewport;

                int anchorPage = Math.Clamp(lastReportedPage, 1, totalPages);
                double anchorFraction = 0.0;
                double scanY = 12.0;
                for (int p = 1; p <= totalPages; p++)
                {
                    double h = pageSheets[p].Bounds.Height > 10
                        ? pageSheets[p].Bounds.Height
                        : (GetSlotHeight(p) + sheetChromeH);
                    double pBottom = scanY + h;
                    if (anchorViewY <= pBottom + (pageGap * 0.5) || p == totalPages)
                    {
                        anchorPage = p;
                        anchorFraction = Math.Clamp((anchorViewY - scanY) / Math.Max(1.0, h), 0.0, 1.0);
                        break;
                    }
                    scanY = pBottom + pageGap;
                }

                // 2. Apply new page width & placeholder heights across all pages
                bool isFitW = reader.FitMode == PdfFitMode.FitWidth;
                double viewportW = continuousScroll.Viewport.Width > 100
                    ? Math.Min(900, continuousScroll.Viewport.Width - 36)
                    : 840;
                double newWidth = isFitW ? viewportW : 760 * (reader.ZoomPercent / 100.0);
                currentPageWidth = newWidth;

                pagesStack.HorizontalAlignment = isFitW
                    ? HorizontalAlignment.Stretch
                    : HorizontalAlignment.Center;
                continuousScroll.HorizontalScrollBarVisibility = isFitW
                    ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
                    : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;

                for (int p = 1; p <= totalPages; p++)
                {
                    pageImages[p].Width = isFitW ? double.NaN : newWidth;
                    pageSheets[p].HorizontalAlignment = isFitW
                        ? HorizontalAlignment.Stretch
                        : HorizontalAlignment.Center;
                    pageSheets[p].MaxWidth = isFitW ? 900 : double.PositiveInfinity;
                    if (pageImages[p].Source is null && pageSheets[p].Child is Grid g)
                        g.MinHeight = GetSlotHeight(p);
                }

                // Update zoom button text in-place (ZERO toolbar rebuild!)
                if (zoomResetBtn.Content is StackPanel zsp)
                {
                    foreach (var child in zsp.Children)
                    {
                        if (child is TextBlock ztb)
                        {
                            ztb.Text = isFitW ? "Width" : $"{reader.ZoomPercent}%";
                            break;
                        }
                    }
                }
                else if (zoomResetBtn.Content is TextBlock directTb)
                {
                    directTb.Text = isFitW ? "Width" : $"{reader.ZoomPercent}%";
                }

                // 3. Synchronously compute & apply the exact new Offset.Y in the SAME frame (zero 1-frame jump/flicker!)
                double newTopY = 12.0;
                for (int p = 1; p < anchorPage; p++)
                    newTopY += GetSlotHeight(p) + sheetChromeH + pageGap;
                double newAnchorH = GetSlotHeight(anchorPage) + sheetChromeH;
                double targetY = Math.Max(
                    0.0,
                    newTopY + (anchorFraction * newAnchorH) - anchorOffsetInViewport
                );

                continuousScroll.Offset = new Vector(continuousScroll.Offset.X, targetY);

                // Release zoom guard after layout settles and ensure visible pages are loaded (zero mid-zoom eviction!)
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        continuousScroll.Offset = new Vector(continuousScroll.Offset.X, targetY);
                        isApplyingZoom = false;
                        SyncVisiblePages();
                    },
                    DispatcherPriority.Render
                );

                // Debounce >150% high-DPI bitmap upgrades so rapid Ctrl+Scroll / Ctrl+/- bursts stay flicker-free
                zoomSharpenTimer?.Stop();
                var sharpenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                zoomSharpenTimer = sharpenTimer;
                int capturedAnchor = anchorPage;
                sharpenTimer.Tick += (_, _) =>
                {
                    sharpenTimer.Stop();
                    int cur = Math.Clamp(capturedAnchor, 1, totalPages);
                    for (int p = Math.Max(1, cur - 1); p <= Math.Min(totalPages, cur + 1); p++)
                        reader.GetPageBitmap(p);
                };
                sharpenTimer.Start();
            }

            Action<int> onNavRequested = pg =>
            {
                pendingFocusPage = null;
                pendingFocusRect = null;
                ScrollToPageInPlace(pg);
                // If user clicked a bookmark inside the floating drawer on narrow width, close drawer cleanly
                if (isConstrainedWidth && reader.IsOutlineVisible)
                    reader.ToggleOutline();
            };
            Action<int, NormalizedHighlightRect?> onHighlightFocus = (pg, rect) =>
            {
                pendingFocusPage = pg;
                pendingFocusRect = rect;
                ScrollToPageAndRectInPlace(pg, rect, animate: true);
                if (pageImages[Math.Clamp(pg, 1, totalPages)].Source is not null)
                {
                    pendingFocusPage = null;
                    pendingFocusRect = null;
                }
            };
            Action onEvidenceSelectionInPlace = () =>
            {
                syncEvidenceBannerInPlace?.Invoke();
                RefreshHighlightOverlaysInPlace();
            };
            Action onZoomInPlace = ApplyZoomInPlace;

            reader.NavigationRequested += onNavRequested;
            reader.HighlightFocusRequested += onHighlightFocus;
            reader.EvidenceSelectionChangedInPlace += onEvidenceSelectionInPlace;
            reader.ZoomChangedInPlace += onZoomInPlace;
            continuousScroll.DetachedFromVisualTree += (_, _) =>
            {
                CancelSmoothScroll();
                zoomSharpenTimer?.Stop();
                reader.PageBitmapReady -= onContinuousBmp;
                reader.NavigationRequested -= onNavRequested;
                reader.HighlightFocusRequested -= onHighlightFocus;
                reader.EvidenceSelectionChangedInPlace -= onEvidenceSelectionInPlace;
                reader.ZoomChangedInPlace -= onZoomInPlace;
            };

            void SyncVisiblePages()
            {
                if (isApplyingZoom)
                    return;
                double viewTop = continuousScroll.Offset.Y;
                double viewH = continuousScroll.Viewport.Height > 0
                    ? continuousScroll.Viewport.Height
                    : 650;
                double viewBottom = viewTop + viewH;
                double viewCenter = viewTop + (viewH * 0.45);

                double y = 12.0;
                int centerPage = lastReportedPage;
                int firstVis = totalPages;
                int lastVis = 1;

                for (int p = 1; p <= totalPages; p++)
                {
                    double h = pageSheets[p].Bounds.Height > 10
                        ? pageSheets[p].Bounds.Height
                        : GetSlotHeight(p);
                    double pBottom = y + h;

                    if (pBottom >= viewTop - 250 && y <= viewBottom + 350)
                    {
                        if (p < firstVis)
                            firstVis = p;
                        if (p > lastVis)
                            lastVis = p;
                    }
                    if (viewCenter >= y && viewCenter <= pBottom + pageGap)
                    {
                        centerPage = p;
                    }
                    y = pBottom + pageGap;
                }

                if (firstVis <= lastVis)
                {
                    int loadFrom = Math.Max(1, firstVis - 1);
                    int loadTo = Math.Min(totalPages, lastVis + 1);
                    for (int p = loadFrom; p <= loadTo; p++)
                    {
                        if (pageImages[p].Source is null)
                        {
                            var b = reader.GetPageBitmap(p);
                            if (b is not null)
                                pageImages[p].Source = b;
                        }
                    }

                    // Release distant Image references (> 8 pages away) to keep memory tiny on 500-page PDFs
                    for (int p = 1; p <= totalPages; p++)
                    {
                        if ((p < loadFrom - 8 || p > loadTo + 8) && pageImages[p].Source is not null)
                        {
                            double curH = pageSheets[p].Bounds.Height;
                            if (curH > 10 && pageSheets[p].Child is Grid g)
                                g.MinHeight = curH;
                            pageImages[p].Source = null;
                        }
                    }
                }

                if (centerPage != lastReportedPage)
                {
                    lastReportedPage = centerPage;
                    reader.SetCurrentPageQuietly(centerPage);
                    if (!pageInput.IsFocused)
                        pageInput.Text = centerPage.ToString();
                    syncEvidenceBannerInPlace?.Invoke();
                }
            }

            continuousScroll.ScrollChanged += (_, _) => SyncVisiblePages();

            // Scroll directly to the target CurrentPage & highlight rect once layout settles
            continuousScroll.AttachedToVisualTree += (_, _) =>
            {
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        if (reader.FitMode == PdfFitMode.FitWidth && continuousScroll.Viewport.Width > 100)
                        {
                            currentPageWidth = Math.Min(900, continuousScroll.Viewport.Width - 36);
                            for (int p = 1; p <= totalPages; p++)
                            {
                                if (pageImages[p].Source is null && pageSheets[p].Child is Grid g)
                                    g.MinHeight = GetSlotHeight(p);
                            }
                        }

                        var initRect =
                            reader.ActiveHighlightPage == initPage
                                ? PdfReaderViewModel.GetPrimaryFocusRect(reader.ActiveEvidenceAnchor)
                                : null;

                        if (initPage > 1 || initRect is not null)
                        {
                            ScrollToPageAndRectInPlace(initPage, initRect);
                        }
                        else
                        {
                            SyncVisiblePages();
                        }
                    },
                    DispatcherPriority.Loaded
                );
            };

            canvasContent = continuousScroll;
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

        bodyHost.Children.Add(body);
        if (floatingOutlineOverlay is not null)
            bodyHost.Children.Add(floatingOutlineOverlay);

        Grid.SetRow(bodyHost, 3);
        root.Children.Add(bodyHost);

        root.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width <= 0)
                return;

            isConstrainedWidth = e.NewSize.Width < 920;
            SyncOutlineVisualState();
        };

        return root;
    }

    private static (
        List<NormalizedHighlightRect> Primary,
        List<NormalizedHighlightRect> Secondary
    ) CollectPageHighlights(PdfReaderViewModel reader, int oneBasedPage)
    {
        var primary = new List<NormalizedHighlightRect>();
        var secondary = new List<NormalizedHighlightRect>();
        if (reader.RotationDegrees != 0)
            return (primary, secondary);

        var (evPrimary, evSecondary) = reader.GetPageEvidenceHighlights(oneBasedPage);
        if (evPrimary.Count > 0)
            primary.AddRange(evPrimary);
        if (evSecondary.Count > 0)
            secondary.AddRange(evSecondary);

        var searchHits = reader.GetPageSearchHighlights(oneBasedPage);
        if (searchHits.Count > 0)
            secondary.AddRange(searchHits);

        return (primary, secondary);
    }

    private sealed class PdfHighlightOverlayControl(
        IReadOnlyList<NormalizedHighlightRect> primaryRects,
        IReadOnlyList<NormalizedHighlightRect> secondaryRects
    ) : Control
    {
        private static readonly IBrush PrimaryFill = new SolidColorBrush(Color.Parse("#52F59E0B"));
        private static readonly IPen PrimaryBorder = new Pen(
            new SolidColorBrush(Color.Parse("#F5D97706")),
            2.0
        );
        private static readonly IBrush SecondaryFill = new SolidColorBrush(Color.Parse("#2AF59E0B"));
        private static readonly IPen SecondaryBorder = new Pen(
            new SolidColorBrush(Color.Parse("#99D97706")),
            1.1
        );

        private IReadOnlyList<NormalizedHighlightRect> _primaryRects = primaryRects;
        private IReadOnlyList<NormalizedHighlightRect> _secondaryRects = secondaryRects;

        public void UpdateHighlights(
            IReadOnlyList<NormalizedHighlightRect> primary,
            IReadOnlyList<NormalizedHighlightRect> secondary
        )
        {
            _primaryRects = primary;
            _secondaryRects = secondary;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            double w = Bounds.Width;
            double h = Bounds.Height;
            if (w <= 1 || h <= 1)
                return;

            foreach (var r in _secondaryRects)
            {
                var rect = new Rect(
                    r.X * w,
                    r.Y * h,
                    Math.Max(8, r.Width * w),
                    Math.Max(10, r.Height * h)
                );
                context.DrawRectangle(SecondaryFill, SecondaryBorder, rect, 3, 3);
            }

            foreach (var r in _primaryRects)
            {
                var rect = new Rect(
                    r.X * w,
                    r.Y * h,
                    Math.Max(8, r.Width * w),
                    Math.Max(10, r.Height * h)
                );
                context.DrawRectangle(PrimaryFill, PrimaryBorder, rect, 3, 3);
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
