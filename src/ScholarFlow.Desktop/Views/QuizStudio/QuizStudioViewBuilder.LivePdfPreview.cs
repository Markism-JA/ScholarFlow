using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views;

public static partial class QuizStudioViewBuilder
{
    private static Border BuildLivePdfPreviewPane(
        MainWindowViewModel vm,
        QuizStudioViewModel studio
    )
    {
        var grid = new Grid { RowDefinitions = RowDefinitions.Parse("Auto,*,Auto") };

        // 1. Top Preview Header Bar (responsive WrapPanel for actions)
        var headerGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 8,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var leftInfo = new StackPanel { Spacing = 2 };
        leftInfo.Children.Add(
            new TextBlock
            {
                Text = "Live PDF Scope Preview",
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            }
        );
        var scopeStateLabel = studio.IsCurrentPreviewPageInScope ? "In Scope" : "Excluded";
        leftInfo.Children.Add(
            new TextBlock
            {
                Text =
                    $"{studio.CurrentPreviewSectionTitle}  ·  PDF p.{studio.CurrentPreviewPdfPage}/{Math.Max(1, studio.PdfTotalPages)} ({scopeStateLabel})",
                FontSize = 11.5,
                Foreground = studio.IsCurrentPreviewPageInScope
                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : ThemeService.Brush(ThemeService.PriorityP1Fg),
                TextTrimming = TextTrimming.CharacterEllipsis,
            }
        );
        headerGrid.Children.Add(leftInfo);

        var headerActions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (studio.PdfTotalPages > 0)
        {
            var scopeModeBtn = UiBuilder.GhostButton(
                "Scope",
                null,
                () => studio.SetPreviewBrowseMode(false),
                isActive: !studio.IsPageGridOpen && !studio.PreviewBrowseAllPages
            );
            scopeModeBtn.Margin = new Thickness(0, 0, 4, 2);
            headerActions.Children.Add(scopeModeBtn);

            var allModeBtn = UiBuilder.GhostButton(
                "All Pages",
                null,
                () => studio.SetPreviewBrowseMode(true),
                isActive: !studio.IsPageGridOpen && studio.PreviewBrowseAllPages
            );
            allModeBtn.Margin = new Thickness(0, 0, 4, 2);
            headerActions.Children.Add(allModeBtn);

            var gridModeBtn = UiBuilder.GhostButton(
                "Page Studio",
                MaterialIconKind.ViewGridOutline,
                studio.OpenPageGrid
            );
            gridModeBtn.Margin = new Thickness(0, 0, 4, 2);
            headerActions.Children.Add(gridModeBtn);
        }

        if (
            !string.IsNullOrWhiteSpace(studio.SelectedPdfPath)
            && File.Exists(studio.SelectedPdfPath)
        )
        {
            var openReaderBtn = UiBuilder.GhostButton(
                "Reader",
                MaterialIconKind.ArrowExpandAll,
                () =>
                {
                    vm.OpenPdfInReader(studio.SelectedPdfPath);
                    vm.PdfReader.JumpToPage(studio.CurrentPreviewPdfPage);
                }
            );
            openReaderBtn.Margin = new Thickness(0, 0, 0, 2);
            headerActions.Children.Add(openReaderBtn);
        }
        Grid.SetColumn(headerActions, 1);
        headerGrid.Children.Add(headerActions);
        grid.Children.Add(headerGrid);

        // 2. Center Visual PDF Page Bitmap Sheet
        var bmp = studio.GetCurrentPreviewBitmap();
        Control pageVisual;
        if (bmp is not null)
        {
            var previewImg = new Image
            {
                Source = bmp,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
            };
            if (studio.PreviewZoomPercent != 100)
                previewImg.Width = 480 * (studio.PreviewZoomPercent / 100.0);

            var pageSheet = new Border
            {
                Background = Brushes.White,
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                Margin = new Thickness(6),
                HorizontalAlignment =
                    studio.PreviewZoomPercent == 100
                        ? HorizontalAlignment.Stretch
                        : HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Child = previewImg,
            };

            var previewScroll = UiBuilder.PersistentScrollViewer(
                $"quiz-preview:{studio.SelectedPdfPath}:{studio.CurrentPreviewPdfPage}",
                pageSheet,
                horizontalVisibility: studio.PreviewZoomPercent > 100
                    ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                    : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            );
            UiBuilder.AttachPdfWheelAndPan(previewScroll, studio.ZoomPreview);
            pageVisual = previewScroll;
        }
        else if (studio.IsLoadingPreviewPage || studio.IsInspectingPdf)
        {
            pageVisual = BuildPdfPageSkeleton(studio.CurrentPreviewPdfPage);
        }
        else
        {
            pageVisual = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 8,
                Children =
                {
                    UiBuilder.Icon(
                        MaterialIconKind.FilePdfBox,
                        28,
                        ThemeService.Brush(ThemeService.TextMuted)
                    ),
                    new TextBlock
                    {
                        Text = "No PDF page available in current scope.",
                        FontSize = 12,
                        Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    },
                },
            };
        }

        var canvasBorder = new Border
        {
            Background = ThemeService.Brush(ThemeService.BgBase),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = pageVisual,
        };
        Grid.SetRow(canvasBorder, 1);
        grid.Children.Add(canvasBorder);

        // 3. Bottom Responsive Manipulation & Navigation Bar
        var footerStack = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };

        if (studio.PdfTotalPages > 0)
        {
            var manipWrap = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var setStartBtn = UiBuilder.GhostButton(
                $"Set Start (p.{studio.CurrentPreviewPdfPage})",
                MaterialIconKind.PageFirst,
                studio.MarkCurrentPreviewPageAsStart
            );
            setStartBtn.Margin = new Thickness(0, 0, 5, 2);
            manipWrap.Children.Add(setStartBtn);

            var setEndBtn = UiBuilder.GhostButton(
                $"Set End (p.{studio.CurrentPreviewPdfPage})",
                MaterialIconKind.PageLast,
                studio.MarkCurrentPreviewPageAsEnd
            );
            setEndBtn.Margin = new Thickness(0, 0, 5, 2);
            manipWrap.Children.Add(setEndBtn);

            bool inScope = studio.IsCurrentPreviewPageInScope;
            var togglePageBtn = UiBuilder.GhostButton(
                inScope
                    ? $"Exclude p.{studio.CurrentPreviewPdfPage}"
                    : $"Include p.{studio.CurrentPreviewPdfPage}",
                inScope ? MaterialIconKind.MinusCircleOutline : MaterialIconKind.PlusCircleOutline,
                studio.ToggleCurrentPreviewPageInScope,
                isActive: !inScope
            );
            togglePageBtn.Margin = new Thickness(0, 0, 0, 2);
            manipWrap.Children.Add(togglePageBtn);

            footerStack.Children.Add(manipWrap);
        }

        var footerBar = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
            ColumnSpacing = 6,
        };

        var prevBtn = UiBuilder.GhostButton(
            "Prev",
            MaterialIconKind.ChevronLeft,
            studio.PreviousPreviewPage
        );
        prevBtn.IsEnabled = studio.PreviewBrowseAllPages
            ? studio.CurrentPreviewPdfPage > 1
            : studio.PreviewScopeIndex > 0;
        footerBar.Children.Add(prevBtn);

        var centerNav = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            [Grid.ColumnProperty] = 1,
        };
        if (studio.PdfTotalPages > 0)
        {
            centerNav.Children.Add(
                new TextBlock
                {
                    Text = "p.",
                    FontSize = 11.5,
                    Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );
            var jumpBox = new TextBox
            {
                Text = studio.CurrentPreviewPdfPage.ToString(),
                MinWidth = 48,
                MaxWidth = 64,
                FontSize = 11.5,
                Padding = new Thickness(6, 3),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            jumpBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && int.TryParse(jumpBox.Text?.Trim(), out int target))
                {
                    e.Handled = true;
                    studio.JumpPreviewToPdfPage(target);
                }
            };
            centerNav.Children.Add(jumpBox);

            var counterSummary = studio.PreviewBrowseAllPages
                ? $"/ {studio.PdfTotalPages}  ({studio.ScopePreviewPageCount} in scope)"
                : $"({studio.PreviewScopeIndex + 1}/{studio.ScopePreviewPageCount} in scope)";
            centerNav.Children.Add(
                new TextBlock
                {
                    Text = counterSummary,
                    FontSize = 11.5,
                    Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );
        }
        else
        {
            centerNav.Children.Add(
                new TextBlock
                {
                    Text = "0 pages in scope",
                    FontSize = 11.5,
                    Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );
        }
        footerBar.Children.Add(centerNav);

        var nextBtn = UiBuilder.GhostButton(
            "Next",
            MaterialIconKind.ChevronRight,
            studio.NextPreviewPage
        );
        nextBtn.IsEnabled = studio.PreviewBrowseAllPages
            ? studio.CurrentPreviewPdfPage < studio.PdfTotalPages
            : studio.PreviewScopeIndex + 1 < studio.ScopePreviewPageCount;
        Grid.SetColumn(nextBtn, 2);
        footerBar.Children.Add(nextBtn);

        footerStack.Children.Add(footerBar);
        Grid.SetRow(footerStack, 2);
        grid.Children.Add(footerStack);

        return UiBuilder.Card(grid, padding: new Thickness(12, 10));
    }

    /// <summary>
    /// Full-canvas, reader-style PDF Page &amp; Section Studio (100% workspace width &amp; height)
    /// with a spacious multi-column Thumbnail Grid on the left and a zoomable High-Res Page Inspector
    /// + Saved Custom Sections panel on the right.
    /// </summary>
    private static Control BuildDedicatedPageStudioView(
        MainWindowViewModel vm,
        QuizStudioViewModel studio
    )
    {
        studio.EnsurePageGridBatchRendered();
        var (batchStart, batchEnd, totalBatches) = studio.GetPageGridBatchRange();
        var inScopeSet = studio.MatchedScopePages.Select(p => p.PageNumber).ToHashSet();

        var root = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,Auto,*"),
            Margin = new Thickness(18, 12, 18, 12),
            RowSpacing = 10,
            ClipToBounds = true,
        };

        // 1. Top Navigation & Scope Header Bar
        var headerGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
            ColumnSpacing = 12,
        };

        var leftHeaderNav = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        leftHeaderNav.Children.Add(
            UiBuilder.GhostButton(
                "Back to Builder",
                MaterialIconKind.ArrowLeft,
                studio.ClosePageGrid
            )
        );
        leftHeaderNav.Children.Add(
            UiBuilder.GhostButton(
                "Pages TOC",
                MaterialIconKind.FormatListChecks,
                studio.ToggleStudioPageToc,
                isActive: studio.IsStudioPageTocOpen
            )
        );
        headerGrid.Children.Add(leftHeaderNav);

        var titleStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            [Grid.ColumnProperty] = 1,
        };
        var pdfName = string.IsNullOrWhiteSpace(studio.SelectedPdfPath)
            ? "PDF"
            : Path.GetFileName(studio.SelectedPdfPath);
        titleStack.Children.Add(
            new TextBlock
            {
                Text = $"Page & Section Studio — {pdfName}",
                FontSize = 15,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                TextTrimming = TextTrimming.CharacterEllipsis,
            }
        );
        titleStack.Children.Add(
            new TextBlock
            {
                Text =
                    $"{studio.ScopePreviewPageCount} of {studio.PdfTotalPages} page(s) selected  ·  Click thumbnail to toggle · Shift+Click for range · Click eye to inspect",
                FontSize = 11.5,
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                TextTrimming = TextTrimming.CharacterEllipsis,
            }
        );
        headerGrid.Children.Add(titleStack);

        var rightHeaderActions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            [Grid.ColumnProperty] = 2,
        };
        var rangeBox = new TextBox
        {
            Text = studio.PageRangeExpression,
            Watermark = "Pages e.g. 1-15, 22, !5",
            MinWidth = 150,
            MaxWidth = 210,
            FontSize = 12,
            Margin = new Thickness(0, 0, 5, 2),
        };
        rangeBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                studio.ApplyPageRangeExpression(rangeBox.Text);
            }
        };
        rightHeaderActions.Children.Add(rangeBox);

        var applyRangeBtn = UiBuilder.GhostButton(
            "Apply",
            MaterialIconKind.Check,
            () => studio.ApplyPageRangeExpression(rangeBox.Text)
        );
        applyRangeBtn.Margin = new Thickness(0, 0, 5, 2);
        rightHeaderActions.Children.Add(applyRangeBtn);

        var saveSecBtn = UiBuilder.GhostButton(
            "Save Section",
            MaterialIconKind.BookmarkPlusOutline,
            studio.ToggleAddCustomSectionForm,
            isActive: studio.IsAddCustomSectionOpen
        );
        saveSecBtn.Margin = new Thickness(0, 0, 5, 2);
        rightHeaderActions.Children.Add(saveSecBtn);

        var doneBtn = UiBuilder.PrimaryButton(
            "Done",
            MaterialIconKind.CheckCircleOutline,
            studio.ClosePageGrid
        );
        doneBtn.Margin = new Thickness(0, 0, 0, 2);
        rightHeaderActions.Children.Add(doneBtn);
        headerGrid.Children.Add(rightHeaderActions);
        root.Children.Add(headerGrid);

        // Optional inline Save Section bar / error row
        var subBar = new StackPanel { Spacing = 6 };
        if (!string.IsNullOrWhiteSpace(studio.PageRangeError))
        {
            subBar.Children.Add(
                new TextBlock
                {
                    Text = studio.PageRangeError,
                    FontSize = 11.5,
                    Foreground = ThemeService.Brush(ThemeService.PriorityP1Fg),
                }
            );
        }
        if (studio.IsAddCustomSectionOpen)
        {
            var customRow = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"),
                ColumnSpacing = 8,
            };
            var nameBox = new TextBox
            {
                Text = studio.CustomSectionNameDraft,
                Watermark = "Section name (e.g. Ch 3: Divisibility)",
                FontSize = 12,
            };
            nameBox.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                    studio.CustomSectionNameDraft = nameBox.Text ?? string.Empty;
            };
            nameBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    studio.SaveCurrentScopeAsCustomSection(nameBox.Text);
                }
            };
            customRow.Children.Add(nameBox);

            var saveConfirm = UiBuilder.PrimaryButton(
                "Save Custom Section",
                MaterialIconKind.Check,
                () => studio.SaveCurrentScopeAsCustomSection(nameBox.Text)
            );
            Grid.SetColumn(saveConfirm, 1);
            customRow.Children.Add(saveConfirm);

            var saveCancel = UiBuilder.GhostButton("Cancel", null, studio.ToggleAddCustomSectionForm);
            Grid.SetColumn(saveCancel, 2);
            customRow.Children.Add(saveCancel);

            subBar.Children.Add(
                new Border
                {
                    Background = ThemeService.Brush(ThemeService.BgSurface),
                    BorderBrush = ThemeService.Brush(ThemeService.AccentPrimary),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 6),
                    Child = customRow,
                }
            );
        }
        if (subBar.Children.Count > 0)
        {
            Grid.SetRow(subBar, 1);
            root.Children.Add(subBar);
        }

        // 2. Main Split Body: [Optional Collapsible 245px Page TOC] | Thumbnail Grid | High-Res Page Inspector
        var splitBody = new Grid
        {
            ColumnDefinitions = studio.IsStudioPageTocOpen
                ? ColumnDefinitions.Parse("245,12,1.8*,12,1.1*")
                : ColumnDefinitions.Parse("1.9*,14,1.1*"),
        };

        int gridCol = 0;
        int inspCol = 2;
        if (studio.IsStudioPageTocOpen)
        {
            var tocPanel = BuildCollapsiblePageTocPanel(studio, isFullHeight: true);
            Grid.SetColumn(tocPanel, 0);
            splitBody.Children.Add(tocPanel);
            gridCol = 2;
            inspCol = 4;
        }

        // --- LEFT: Spacious Thumbnail Grid ---
        var leftGrid = new Grid { RowDefinitions = RowDefinitions.Parse("Auto,*,Auto") };

        var actionBar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var selBatchBtn = UiBuilder.GhostButton(
            $"Select p.{batchStart}–{batchEnd}",
            MaterialIconKind.CheckboxMultipleMarkedOutline,
            () => studio.SetBatchPagesInScope(true)
        );
        selBatchBtn.Margin = new Thickness(0, 0, 5, 3);
        actionBar.Children.Add(selBatchBtn);

        var exclBatchBtn = UiBuilder.GhostButton(
            "Exclude Batch",
            MaterialIconKind.CheckboxMultipleBlankOutline,
            () => studio.SetBatchPagesInScope(false)
        );
        exclBatchBtn.Margin = new Thickness(0, 0, 5, 3);
        actionBar.Children.Add(exclBatchBtn);

        var invertBtn = UiBuilder.GhostButton(
            "Invert Scope",
            MaterialIconKind.SwapHorizontal,
            studio.InvertPdfScope
        );
        invertBtn.Margin = new Thickness(0, 0, 5, 3);
        actionBar.Children.Add(invertBtn);

        var resetAllBtn = UiBuilder.GhostButton(
            "Select All",
            MaterialIconKind.SelectAll,
            studio.ClearScopeFilters
        );
        resetAllBtn.Margin = new Thickness(0, 0, 5, 3);
        actionBar.Children.Add(resetAllBtn);
        leftGrid.Children.Add(actionBar);

        var tilesWrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8),
        };

        for (int p = batchStart; p <= batchEnd; p++)
        {
            int capturedPage = p;
            bool inScope = inScopeSet.Contains(capturedPage);
            bool isInspected = studio.CurrentPreviewPdfPage == capturedPage;
            var thumb = studio.GetThumbnailBitmap(capturedPage);

            Control thumbVisual = thumb is not null
                ? new Image
                {
                    Source = thumb,
                    Stretch = Stretch.Uniform,
                    MaxWidth = 122,
                    MaxHeight = 156,
                    Opacity = inScope ? 1.0 : 0.36,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }
                : new Border
                {
                    Width = 114,
                    Height = 148,
                    Background = ThemeService.Brush(ThemeService.BgElevated),
                    CornerRadius = new CornerRadius(3),
                    Child = new TextBlock
                    {
                        Text = $"p.{capturedPage}",
                        FontSize = 12,
                        Foreground = ThemeService.Brush(ThemeService.TextMuted),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };

            var tileFooter = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
                ColumnSpacing = 4,
                Margin = new Thickness(0, 5, 0, 0),
            };
            tileFooter.Children.Add(
                UiBuilder.Icon(
                    inScope ? MaterialIconKind.CheckCircle : MaterialIconKind.CircleOutline,
                    13,
                    inScope
                        ? ThemeService.Brush(ThemeService.AccentPrimary)
                        : ThemeService.Brush(ThemeService.TextMuted)
                )
            );
            tileFooter.Children.Add(
                new TextBlock
                {
                    Text = $"p.{capturedPage}",
                    FontSize = 11.5,
                    FontWeight = inScope || isInspected ? FontWeight.SemiBold : FontWeight.Normal,
                    Foreground = inScope
                        ? ThemeService.Brush(ThemeService.TextPrimary)
                        : ThemeService.Brush(ThemeService.TextMuted),
                    VerticalAlignment = VerticalAlignment.Center,
                    [Grid.ColumnProperty] = 1,
                }
            );
            var inspectBtn = UiBuilder.IconButton(
                MaterialIconKind.EyeOutline,
                () => studio.InspectPageFromGrid(capturedPage),
                13,
                isInspected
                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : ThemeService.Brush(ThemeService.TextSecondary)
            );
            Grid.SetColumn(inspectBtn, 2);
            tileFooter.Children.Add(inspectBtn);

            var tileContent = new Grid { RowDefinitions = RowDefinitions.Parse("*,Auto") };
            var sheetFrame = new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(3),
                Height = 160,
                Width = 126,
                Child = thumbVisual,
            };
            tileContent.Children.Add(sheetFrame);
            Grid.SetRow(tileFooter, 1);
            tileContent.Children.Add(tileFooter);

            var tileBorder = new Border
            {
                Background = inScope
                    ? ThemeService.Brush(ThemeService.AccentSoftBg)
                    : ThemeService.Brush(ThemeService.BgSurface),
                BorderBrush = isInspected
                    ? ThemeService.Brush(ThemeService.PriorityP2Fg)
                    : inScope
                        ? ThemeService.Brush(ThemeService.AccentPrimary)
                        : ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(isInspected ? 2 : inScope ? 1.5 : 1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(6),
                Margin = new Thickness(0, 0, 10, 10),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = tileContent,
            };

            tileBorder.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(tileBorder).Properties.IsLeftButtonPressed)
                {
                    bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                    studio.TogglePageInScopeFromGrid(capturedPage, shift);
                    e.Handled = true;
                }
            };

            tilesWrap.Children.Add(tileBorder);
        }

        var gridCanvas = new Border
        {
            Background = ThemeService.Brush(ThemeService.BgBase),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = UiBuilder.PersistentScrollViewer(
                $"quiz-page-studio-grid:{studio.SelectedPdfPath}:{studio.PageGridBatchIndex}",
                tilesWrap
            ),
        };
        Grid.SetRow(gridCanvas, 1);
        leftGrid.Children.Add(gridCanvas);

        // Batch pagination footer
        var batchFooter = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
            Margin = new Thickness(0, 8, 0, 0),
        };
        var prevBatchBtn = UiBuilder.GhostButton(
            $"Prev {QuizStudioViewModel.PageGridBatchSize}",
            MaterialIconKind.ChevronLeft,
            studio.PreviousPageGridBatch
        );
        prevBatchBtn.IsEnabled = studio.PageGridBatchIndex > 0;
        batchFooter.Children.Add(prevBatchBtn);

        var batchLabel = new TextBlock
        {
            Text =
                $"Pages {batchStart}–{batchEnd} of {studio.PdfTotalPages}  ·  Batch {studio.PageGridBatchIndex + 1}/{totalBatches}",
            FontSize = 12,
            Foreground = ThemeService.Brush(ThemeService.TextSecondary),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            [Grid.ColumnProperty] = 1,
        };
        batchFooter.Children.Add(batchLabel);

        var nextBatchBtn = UiBuilder.GhostButton(
            $"Next {QuizStudioViewModel.PageGridBatchSize}",
            MaterialIconKind.ChevronRight,
            studio.NextPageGridBatch
        );
        nextBatchBtn.IsEnabled = studio.PageGridBatchIndex + 1 < totalBatches;
        Grid.SetColumn(nextBatchBtn, 2);
        batchFooter.Children.Add(nextBatchBtn);

        Grid.SetRow(batchFooter, 2);
        leftGrid.Children.Add(batchFooter);

        var leftCard = UiBuilder.Card(leftGrid, padding: new Thickness(12, 10));
        Grid.SetColumn(leftCard, gridCol);
        splitBody.Children.Add(leftCard);

        // --- RIGHT: Live High-Res Page Inspector ---
        var rightInspector = new Grid { RowDefinitions = RowDefinitions.Parse("Auto,*,Auto") };

        var inspHeader = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            Margin = new Thickness(0, 0, 0, 8),
        };
        var inspInfo = new StackPanel { Spacing = 2 };
        inspInfo.Children.Add(
            new TextBlock
            {
                Text = $"Page Inspector — p.{studio.CurrentPreviewPdfPage}",
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            }
        );
        bool curInScope = studio.IsCurrentPreviewPageInScope;
        inspInfo.Children.Add(
            new TextBlock
            {
                Text = $"{studio.CurrentPreviewSectionTitle}  ·  {(curInScope ? "In Scope" : "Excluded")}",
                FontSize = 11.5,
                Foreground = curInScope
                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : ThemeService.Brush(ThemeService.PriorityP1Fg),
                TextTrimming = TextTrimming.CharacterEllipsis,
            }
        );
        inspHeader.Children.Add(inspInfo);

        if (
            !string.IsNullOrWhiteSpace(studio.SelectedPdfPath)
            && File.Exists(studio.SelectedPdfPath)
        )
        {
            var openReaderBtn = UiBuilder.GhostButton(
                "Reader",
                MaterialIconKind.ArrowExpandAll,
                () =>
                {
                    vm.OpenPdfInReader(studio.SelectedPdfPath);
                    vm.PdfReader.JumpToPage(studio.CurrentPreviewPdfPage);
                }
            );
            Grid.SetColumn(openReaderBtn, 1);
            inspHeader.Children.Add(openReaderBtn);
        }
        rightInspector.Children.Add(inspHeader);

        var bmp = studio.GetCurrentPreviewBitmap();
        Control inspVisual;
        if (bmp is not null)
        {
            var img = new Image
            {
                Source = bmp,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
            };
            if (studio.PreviewZoomPercent != 100)
                img.Width = 480 * (studio.PreviewZoomPercent / 100.0);

            var sheet = new Border
            {
                Background = Brushes.White,
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                Margin = new Thickness(6),
                HorizontalAlignment =
                    studio.PreviewZoomPercent == 100
                        ? HorizontalAlignment.Stretch
                        : HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Child = img,
            };
            var scroll = UiBuilder.PersistentScrollViewer(
                $"quiz-studio-inspector:{studio.SelectedPdfPath}:{studio.CurrentPreviewPdfPage}",
                sheet,
                horizontalVisibility: studio.PreviewZoomPercent > 100
                    ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                    : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            );
            UiBuilder.AttachPdfWheelAndPan(scroll, studio.ZoomPreview);
            inspVisual = scroll;
        }
        else
        {
            inspVisual = BuildPdfPageSkeleton(studio.CurrentPreviewPdfPage);
        }

        var inspCanvas = new Border
        {
            Background = ThemeService.Brush(ThemeService.BgBase),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = inspVisual,
        };
        Grid.SetRow(inspCanvas, 1);
        rightInspector.Children.Add(inspCanvas);

        var inspFooter = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var setStartBtn = UiBuilder.GhostButton(
            $"Set Start (p.{studio.CurrentPreviewPdfPage})",
            MaterialIconKind.PageFirst,
            studio.MarkCurrentPreviewPageAsStart
        );
        setStartBtn.Margin = new Thickness(0, 0, 5, 2);
        inspFooter.Children.Add(setStartBtn);

        var setEndBtn = UiBuilder.GhostButton(
            $"Set End (p.{studio.CurrentPreviewPdfPage})",
            MaterialIconKind.PageLast,
            studio.MarkCurrentPreviewPageAsEnd
        );
        setEndBtn.Margin = new Thickness(0, 0, 5, 2);
        inspFooter.Children.Add(setEndBtn);

        var toggleInspBtn = UiBuilder.GhostButton(
            curInScope
                ? $"Exclude p.{studio.CurrentPreviewPdfPage}"
                : $"Include p.{studio.CurrentPreviewPdfPage}",
            curInScope ? MaterialIconKind.MinusCircleOutline : MaterialIconKind.PlusCircleOutline,
            studio.ToggleCurrentPreviewPageInScope,
            isActive: !curInScope
        );
        toggleInspBtn.Margin = new Thickness(0, 0, 0, 2);
        inspFooter.Children.Add(toggleInspBtn);

        Grid.SetRow(inspFooter, 2);
        rightInspector.Children.Add(inspFooter);

        var rightCard = UiBuilder.Card(rightInspector, padding: new Thickness(12, 10));
        Grid.SetColumn(rightCard, inspCol);
        splitBody.Children.Add(rightCard);

        Grid.SetRow(splitBody, 2);
        root.Children.Add(splitBody);
        return root;
    }

    /// <summary>
    /// Shared collapsible Page TOC showing each page, its scope state (In Scope / Excluded),
    /// optional section subtitle, 1-click include/exclude toggle, and jump-to-page preview.
    /// Used in both the Builder Scope card and as a collapsible left sidebar in Page &amp; Section Studio.
    /// </summary>
    private static Control BuildCollapsiblePageTocPanel(
        QuizStudioViewModel studio,
        bool isFullHeight
    )
    {
        var inScopeSet = studio.MatchedScopePages.Select(p => p.PageNumber).ToHashSet();
        int totalPages = studio.PdfTotalPages;
        int inScopeCount = inScopeSet.Count;
        int excludedCount = Math.Max(0, totalPages - inScopeCount);

        bool isExpanded = isFullHeight || studio.IsBuilderPageTocOpen;

        var container = new Grid
        {
            RowDefinitions = isFullHeight
                ? RowDefinitions.Parse("Auto,Auto,*,Auto")
                : RowDefinitions.Parse("Auto,Auto,Auto,Auto"),
            RowSpacing = 6,
        };

        // 1. Header bar (collapsible toggle in Builder; titled header with collapse button in Studio)
        var headerBar = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
            ColumnSpacing = 6,
        };

        if (!isFullHeight)
        {
            var toggleBtn = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 5,
                    Children =
                    {
                        UiBuilder.Icon(
                            isExpanded
                                ? MaterialIconKind.ChevronDown
                                : MaterialIconKind.ChevronRight,
                            15,
                            ThemeService.Brush(ThemeService.TextSecondary)
                        ),
                        new TextBlock
                        {
                            Text = "Pages TOC",
                            FontSize = 12,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    },
                },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(2, 2),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            toggleBtn.Click += (_, _) => studio.ToggleBuilderPageToc();
            headerBar.Children.Add(toggleBtn);
        }
        else
        {
            headerBar.Children.Add(
                new TextBlock
                {
                    Text = "Pages TOC",
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );
        }

        var countPill = UiBuilder.Pill(
            $"{inScopeCount}/{totalPages} in scope",
            ThemeService.Brush(ThemeService.AccentPrimary),
            ThemeService.Brush(ThemeService.AccentSoftBg),
            fontSize: 10.5
        );
        Grid.SetColumn(countPill, 1);
        countPill.HorizontalAlignment = HorizontalAlignment.Left;
        headerBar.Children.Add(countPill);

        if (isFullHeight)
        {
            var collapseBtn = UiBuilder.IconButton(
                MaterialIconKind.ChevronDoubleLeft,
                studio.ToggleStudioPageToc,
                15,
                ThemeService.Brush(ThemeService.TextSecondary)
            );
            Grid.SetColumn(collapseBtn, 2);
            headerBar.Children.Add(collapseBtn);
        }

        container.Children.Add(headerBar);

        if (!isExpanded)
        {
            return new Border
            {
                Background = ThemeService.Brush(ThemeService.BgBase),
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 5),
                Child = container,
            };
        }

        // 2. Filter chips: All | In Scope | Excluded
        var filterWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        void AddFilterChip(string label, PageTocFilterMode mode)
        {
            var btn = UiBuilder.GhostButton(
                label,
                null,
                () => studio.SetPageTocFilter(mode),
                isActive: studio.PageTocFilter == mode
            );
            btn.Margin = new Thickness(0, 0, 4, 2);
            filterWrap.Children.Add(btn);
        }
        AddFilterChip($"All ({totalPages})", PageTocFilterMode.All);
        AddFilterChip($"In ({inScopeCount})", PageTocFilterMode.InScope);
        AddFilterChip($"Out ({excludedCount})", PageTocFilterMode.Excluded);
        Grid.SetRow(filterWrap, 1);
        container.Children.Add(filterWrap);

        // 3. Build filtered page list + windowing (80 rows per window for large PDFs)
        var filteredPages = new List<int>(totalPages);
        for (int p = 1; p <= totalPages; p++)
        {
            bool inc = inScopeSet.Contains(p);
            if (
                studio.PageTocFilter == PageTocFilterMode.All
                || (studio.PageTocFilter == PageTocFilterMode.InScope && inc)
                || (studio.PageTocFilter == PageTocFilterMode.Excluded && !inc)
            )
            {
                filteredPages.Add(p);
            }
        }

        int totalWindows = Math.Max(
            1,
            (int)Math.Ceiling(filteredPages.Count / (double)QuizStudioViewModel.PageTocWindowSize)
        );
        int windowIdx = Math.Clamp(studio.PageTocWindowIndex, 0, totalWindows - 1);
        int skip = windowIdx * QuizStudioViewModel.PageTocWindowSize;
        var visiblePages = filteredPages
            .Skip(skip)
            .Take(QuizStudioViewModel.PageTocWindowSize)
            .ToList();

        var rowsStack = new StackPanel { Spacing = 1 };
        if (visiblePages.Count == 0)
        {
            rowsStack.Children.Add(
                new TextBlock
                {
                    Text = "No pages match this filter.",
                    FontSize = 11.5,
                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    Margin = new Thickness(6, 8),
                }
            );
        }
        else
        {
            int curPreview = studio.CurrentPreviewPdfPage;
            foreach (var p in visiblePages)
            {
                int capturedPage = p;
                bool inScope = inScopeSet.Contains(capturedPage);
                bool isCurrent = capturedPage == curPreview;
                var secLabel = studio.GetPageSectionLabel(capturedPage);

                var rowGrid = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
                    ColumnSpacing = 5,
                };

                var chkBtn = UiBuilder.IconButton(
                    inScope
                        ? MaterialIconKind.CheckboxMarked
                        : MaterialIconKind.CheckboxBlankOutline,
                    () => studio.ToggleSinglePageInScope(capturedPage),
                    15,
                    inScope
                        ? ThemeService.Brush(ThemeService.AccentPrimary)
                        : ThemeService.Brush(ThemeService.TextMuted)
                );
                rowGrid.Children.Add(chkBtn);

                var labelStack = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
                labelStack.Children.Add(
                    new TextBlock
                    {
                        Text = $"Page {capturedPage}",
                        FontSize = 12,
                        FontWeight =
                            inScope || isCurrent ? FontWeight.SemiBold : FontWeight.Normal,
                        Foreground = inScope
                            ? ThemeService.Brush(ThemeService.TextPrimary)
                            : ThemeService.Brush(ThemeService.TextMuted),
                    }
                );
                if (!string.IsNullOrWhiteSpace(secLabel))
                {
                    labelStack.Children.Add(
                        new TextBlock
                        {
                            Text = secLabel,
                            FontSize = 10,
                            Foreground = ThemeService.Brush(ThemeService.TextMuted),
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        }
                    );
                }

                var jumpBtn = new Button
                {
                    Content = labelStack,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(2, 2),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                jumpBtn.Click += (_, _) => studio.JumpToPageFromToc(capturedPage);
                Grid.SetColumn(jumpBtn, 1);
                rowGrid.Children.Add(jumpBtn);

                var stateBadge = UiBuilder.Pill(
                    inScope ? "In" : "Out",
                    inScope
                        ? ThemeService.Brush(ThemeService.AccentPrimary)
                        : ThemeService.Brush(ThemeService.TextMuted),
                    inScope
                        ? ThemeService.Brush(ThemeService.AccentSoftBg)
                        : ThemeService.Brush(ThemeService.BgElevated),
                    fontSize: 9.5
                );
                stateBadge.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(stateBadge, 2);
                rowGrid.Children.Add(stateBadge);

                rowsStack.Children.Add(
                    new Border
                    {
                        Background = isCurrent
                            ? ThemeService.Brush(ThemeService.BgElevated)
                            : Brushes.Transparent,
                        BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(4, 2),
                        Child = rowGrid,
                    }
                );
            }
        }

        var scroll = UiBuilder.PersistentScrollViewer(
            $"quiz-page-toc:{(isFullHeight ? "studio" : "builder")}:{studio.SelectedPdfPath}:{studio.PageTocFilter}:{windowIdx}",
            rowsStack
        );
        if (!isFullHeight)
            scroll.MaxHeight = 205;

        var listBorder = new Border
        {
            Background = ThemeService.Brush(ThemeService.BgBase),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4, 3),
            Child = scroll,
        };
        Grid.SetRow(listBorder, 2);
        container.Children.Add(listBorder);

        // 4. Optional window pager for large PDFs (>80 matching pages)
        if (totalWindows > 1)
        {
            var pager = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto") };
            var prevWin = UiBuilder.GhostButton(
                "Prev",
                MaterialIconKind.ChevronLeft,
                studio.PreviousPageTocWindow
            );
            prevWin.IsEnabled = windowIdx > 0;
            pager.Children.Add(prevWin);

            pager.Children.Add(
                new TextBlock
                {
                    Text = $"{skip + 1}–{skip + visiblePages.Count} / {filteredPages.Count}",
                    FontSize = 11,
                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    [Grid.ColumnProperty] = 1,
                }
            );

            var nextWin = UiBuilder.GhostButton(
                "Next",
                MaterialIconKind.ChevronRight,
                () => studio.NextPageTocWindow(totalWindows)
            );
            nextWin.IsEnabled = windowIdx + 1 < totalWindows;
            Grid.SetColumn(nextWin, 2);
            pager.Children.Add(nextWin);

            Grid.SetRow(pager, 3);
            container.Children.Add(pager);
        }

        return isFullHeight
            ? UiBuilder.Card(container, padding: new Thickness(10, 10))
            : new Border
            {
                Background = ThemeService.Brush(ThemeService.BgBase),
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6),
                Child = container,
            };
    }

    // =========================================================================
    // 1B. HAIRLINE QUIZ CATALOG & MULTI-QUIZ COMBINER VIEW
    // =========================================================================
}
