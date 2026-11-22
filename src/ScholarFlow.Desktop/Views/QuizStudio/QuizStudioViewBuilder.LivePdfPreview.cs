using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
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
                    ? ThemeService.Brush(ThemeService.TextSecondary)
                    : ThemeService.Brush(ThemeService.TextMuted),
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
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
            ToolTip.SetTip(scopeModeBtn, "Step only through pages currently included in scope");
            scopeModeBtn.Margin = new Thickness(0, 0, 4, 2);
            headerActions.Children.Add(scopeModeBtn);

            var allModeBtn = UiBuilder.GhostButton(
                "All Pages",
                null,
                () => studio.SetPreviewBrowseMode(true),
                isActive: !studio.IsPageGridOpen && studio.PreviewBrowseAllPages
            );
            ToolTip.SetTip(allModeBtn, "Browse every page of the PDF without changing your selected scope");
            allModeBtn.Margin = new Thickness(0, 0, 4, 2);
            headerActions.Children.Add(allModeBtn);

            var gridModeBtn = UiBuilder.GhostButton(
                "Page Studio",
                MaterialIconKind.ViewGridOutline,
                studio.OpenPageGrid
            );
            ToolTip.SetTip(
                gridModeBtn,
                "Open full-screen Page & Section Studio to visually pick pages, exclude slides, and save chapters"
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
            int localZoom = studio.PreviewZoomPercent;
            UiBuilder.AttachPdfWheelAndPan(
                previewScroll,
                delta =>
                {
                    int oldZ = localZoom;
                    localZoom = Math.Clamp(localZoom + delta, 100, 300);
                    if (localZoom == oldZ)
                        return;
                    double ratio = localZoom / (double)oldZ;
                    previewImg.Width = localZoom == 100 ? double.NaN : 480 * (localZoom / 100.0);
                    pageSheet.HorizontalAlignment =
                        localZoom == 100
                            ? HorizontalAlignment.Stretch
                            : HorizontalAlignment.Center;
                    previewScroll.HorizontalScrollBarVisibility =
                        localZoom > 100
                            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                            : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
                    previewScroll.Offset = new Vector(
                        previewScroll.Offset.X,
                        Math.Max(0, previewScroll.Offset.Y * ratio)
                    );
                }
            );
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
            ToolTip.SetTip(setStartBtn, $"Start the selected page range at page {studio.CurrentPreviewPdfPage}");
            setStartBtn.Margin = new Thickness(0, 0, 5, 2);
            manipWrap.Children.Add(setStartBtn);

            var setEndBtn = UiBuilder.GhostButton(
                $"Set End (p.{studio.CurrentPreviewPdfPage})",
                MaterialIconKind.PageLast,
                studio.MarkCurrentPreviewPageAsEnd
            );
            ToolTip.SetTip(setEndBtn, $"End the selected page range at page {studio.CurrentPreviewPdfPage}");
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
            ToolTip.SetTip(
                togglePageBtn,
                inScope
                    ? $"Remove page {studio.CurrentPreviewPdfPage} from the quiz scope"
                    : $"Add page {studio.CurrentPreviewPdfPage} back into the quiz scope"
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
    private static double _lastStudioViewportWidth;

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

        // 1. Responsive Top Navigation & Scope Header Bar
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
        Action<bool>? setTocOpenAction = null;
        var tocToggleBtn = UiBuilder.GhostButton(
            "Pages TOC",
            MaterialIconKind.FormatListChecks,
            () => setTocOpenAction?.Invoke(!studio.IsStudioPageTocOpen),
            isActive: studio.IsStudioPageTocOpen
        );
        ToolTip.SetTip(tocToggleBtn, "Toggle collapsible Pages TOC drawer");
        leftHeaderNav.Children.Add(tocToggleBtn);

        var titleStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
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

        var rightHeaderActions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
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
        ToolTip.SetTip(saveSecBtn, "Save current page selection as a reusable custom chapter");
        saveSecBtn.Margin = new Thickness(0, 0, 5, 2);
        rightHeaderActions.Children.Add(saveSecBtn);

        if (studio.IsCurrentPdfOutlineWeak)
        {
            var autoDetectBtn = UiBuilder.GhostButton(
                studio.IsDetectingSmartSections ? "Scanning..." : "Auto-Detect",
                MaterialIconKind.AutoFix,
                async () => await studio.AutoDetectSmartSectionsAsync()
            );
            autoDetectBtn.IsEnabled = !studio.IsDetectingSmartSections;
            ToolTip.SetTip(
                autoDetectBtn,
                "Scan PDF headings & font sizes to auto-build reusable chapters"
            );
            autoDetectBtn.Margin = new Thickness(0, 0, 5, 2);
            rightHeaderActions.Children.Add(autoDetectBtn);
        }

        var doneBtn = UiBuilder.PrimaryButton(
            "Done",
            MaterialIconKind.CheckCircleOutline,
            studio.ClosePageGrid
        );
        doneBtn.Margin = new Thickness(0, 0, 0, 2);

        var responsiveHeaderHost = new Border();
        bool? lastCompactStudioHeader = null;
        void ApplyStudioHeaderLayout(bool compactHeader)
        {
            if (lastCompactStudioHeader == compactHeader && responsiveHeaderHost.Child is not null)
                return;
            lastCompactStudioHeader = compactHeader;

            static void Detach(Control c)
            {
                if (c.Parent is Panel p)
                    p.Children.Remove(c);
                else if (c.Parent is Decorator d && ReferenceEquals(d.Child, c))
                    d.Child = null;
            }

            Detach(leftHeaderNav);
            Detach(titleStack);
            Detach(rightHeaderActions);
            Detach(doneBtn);

            if (compactHeader)
            {
                var row1 = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
                    ColumnSpacing = 10,
                };
                row1.Children.Add(leftHeaderNav);
                Grid.SetColumn(titleStack, 1);
                row1.Children.Add(titleStack);
                Grid.SetColumn(doneBtn, 2);
                row1.Children.Add(doneBtn);

                responsiveHeaderHost.Child = new StackPanel
                {
                    Spacing = 6,
                    Children = { row1, rightHeaderActions },
                };
            }
            else
            {
                rightHeaderActions.Children.Add(doneBtn);
                var row = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
                    ColumnSpacing = 12,
                };
                row.Children.Add(leftHeaderNav);
                Grid.SetColumn(titleStack, 1);
                row.Children.Add(titleStack);
                Grid.SetColumn(rightHeaderActions, 2);
                row.Children.Add(rightHeaderActions);
                responsiveHeaderHost.Child = row;
            }
        }
        ApplyStudioHeaderLayout(false);
        root.Children.Add(responsiveHeaderHost);

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

        // 2. Animated 3-Pane Split Body: [Animated TOC Slot] | [Animated Thumbnail Grid Slot] | [Page Inspector (*)]
        var splitBody = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,Auto,*"),
        };

        var tocPanel = BuildCollapsiblePageTocPanel(
            studio,
            isFullHeight: true,
            onCloseOverride: () => setTocOpenAction?.Invoke(false)
        );

        var dockedTocSlot = new Border
        {
            ClipToBounds = true,
            Padding = new Thickness(0, 0, 12, 0),
            Width = 0,
            Opacity = 0,
            IsHitTestVisible = false,
            Child = tocPanel,
        };
        Grid.SetColumn(dockedTocSlot, 0);
        splitBody.Children.Add(dockedTocSlot);

        // Floating TOC drawer + scrim (slides smoothly from left on Mid/Compact or Expanded/Full Inspector)
        var tocFloatingScrim = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#66000000")),
            Opacity = 0,
            IsHitTestVisible = false,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        tocFloatingScrim.PointerPressed += (_, _) => setTocOpenAction?.Invoke(false);

        var tocDrawerTransform = new TranslateTransform(-270, 0);
        var tocFloatingDrawer = new Border
        {
            Width = 258,
            HorizontalAlignment = HorizontalAlignment.Left,
            BoxShadow = BoxShadows.Parse("8 0 28 0 #70000000"),
            RenderTransform = tocDrawerTransform,
            Opacity = 0,
            IsHitTestVisible = false,
        };

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
        ToolTip.SetTip(selBatchBtn, $"Include all visible pages ({batchStart}–{batchEnd}) in scope");
        selBatchBtn.Margin = new Thickness(0, 0, 5, 3);
        actionBar.Children.Add(selBatchBtn);

        var exclBatchBtn = UiBuilder.GhostButton(
            "Exclude Batch",
            MaterialIconKind.CheckboxMultipleBlankOutline,
            () => studio.SetBatchPagesInScope(false)
        );
        ToolTip.SetTip(exclBatchBtn, $"Remove all visible pages ({batchStart}–{batchEnd}) from scope");
        exclBatchBtn.Margin = new Thickness(0, 0, 5, 3);
        actionBar.Children.Add(exclBatchBtn);

        var invertBtn = UiBuilder.GhostButton(
            "Invert Scope",
            MaterialIconKind.SwapHorizontal,
            studio.InvertPdfScope
        );
        ToolTip.SetTip(invertBtn, "Flip included and excluded pages across the entire PDF");
        invertBtn.Margin = new Thickness(0, 0, 5, 3);
        actionBar.Children.Add(invertBtn);

        var resetAllBtn = UiBuilder.GhostButton(
            "Select All",
            MaterialIconKind.SelectAll,
            studio.ClearScopeFilters
        );
        ToolTip.SetTip(resetAllBtn, "Reset scope filters to include all pages");
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
                    Transitions =
                    [
                        new DoubleTransition
                        {
                            Property = Visual.OpacityProperty,
                            Duration = TimeSpan.FromMilliseconds(130),
                        },
                    ],
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
                        ? ThemeService.Brush(ThemeService.SuccessFg)
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
            ToolTip.SetTip(inspectBtn, $"Inspect page {capturedPage} full-size");
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
                    ? ThemeService.Brush(ThemeService.BgElevated)
                    : ThemeService.Brush(ThemeService.BgSurface),
                BorderBrush = isInspected
                    ? ThemeService.Brush(ThemeService.TextPrimary)
                    : inScope
                        ? ThemeService.Brush(ThemeService.BorderStrong)
                        : ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(isInspected ? 1.8 : 1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(6),
                Margin = new Thickness(0, 0, 10, 10),
                Cursor = new Cursor(StandardCursorType.Hand),
                Transitions =
                [
                    new BrushTransition
                    {
                        Property = Border.BorderBrushProperty,
                        Duration = TimeSpan.FromMilliseconds(120),
                    },
                    new BrushTransition
                    {
                        Property = Border.BackgroundProperty,
                        Duration = TimeSpan.FromMilliseconds(120),
                    },
                ],
                Child = tileContent,
            };

            var baseTileBg = tileBorder.Background;
            var baseTileBorder = tileBorder.BorderBrush;
            tileBorder.PointerEntered += (_, _) =>
            {
                if (!isInspected)
                {
                    tileBorder.Background = ThemeService.Brush(ThemeService.BgHover);
                    tileBorder.BorderBrush = ThemeService.Brush(ThemeService.BorderStrong);
                }
            };
            tileBorder.PointerExited += (_, _) =>
            {
                if (!isInspected)
                {
                    tileBorder.Background = baseTileBg;
                    tileBorder.BorderBrush = baseTileBorder;
                }
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
        var leftGridSlot = new Border
        {
            ClipToBounds = true,
            Padding = new Thickness(0, 0, 12, 0),
            Child = leftCard,
        };
        Grid.SetColumn(leftGridSlot, 1);
        splitBody.Children.Add(leftGridSlot);

        // --- RIGHT: Live High-Res Page Inspector (with Expand Left 2-Col & Full View toggles) ---
        var rightInspector = new Grid { RowDefinitions = RowDefinitions.Parse("Auto,*,Auto") };

        var inspHeader = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 8,
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
                    ? ThemeService.Brush(ThemeService.TextSecondary)
                    : ThemeService.Brush(ThemeService.TextMuted),
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
            }
        );
        inspHeader.Children.Add(inspInfo);

        Action<StudioInspectorMode>? setInspectorModeAction = null;
        var inspHeaderActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var expandLeftIcon = UiBuilder.Icon(
            studio.StudioInspectorLayout == StudioInspectorMode.ExpandedLeft
                ? MaterialIconKind.ChevronDoubleRight
                : MaterialIconKind.ChevronDoubleLeft,
            15.5,
            ThemeService.Brush(ThemeService.TextSecondary)
        );
        var expandLeftBtn = new Button
        {
            Content = expandLeftIcon,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(5),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        expandLeftBtn.PointerEntered += (_, _) =>
            expandLeftBtn.Background = ThemeService.Brush(ThemeService.BgHover);
        expandLeftBtn.PointerExited += (_, _) =>
            expandLeftBtn.Background =
                studio.StudioInspectorLayout == StudioInspectorMode.ExpandedLeft
                    ? ThemeService.Brush(ThemeService.BgElevated)
                    : Brushes.Transparent;
        expandLeftBtn.Click += (_, _) =>
        {
            var next =
                studio.StudioInspectorLayout == StudioInspectorMode.ExpandedLeft
                    ? StudioInspectorMode.Balanced
                    : StudioInspectorMode.ExpandedLeft;
            setInspectorModeAction?.Invoke(next);
        };
        ToolTip.SetTip(expandLeftBtn, "Expand Inspector left (collapses thumbnail grid to 2 columns)");
        inspHeaderActions.Children.Add(expandLeftBtn);

        var fullViewIcon = UiBuilder.Icon(
            studio.StudioInspectorLayout == StudioInspectorMode.FullView
                ? MaterialIconKind.FullscreenExit
                : MaterialIconKind.Fullscreen,
            15.5,
            ThemeService.Brush(ThemeService.TextSecondary)
        );
        var fullViewBtn = new Button
        {
            Content = fullViewIcon,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(5),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        fullViewBtn.PointerEntered += (_, _) =>
            fullViewBtn.Background = ThemeService.Brush(ThemeService.BgHover);
        fullViewBtn.PointerExited += (_, _) =>
            fullViewBtn.Background =
                studio.StudioInspectorLayout == StudioInspectorMode.FullView
                    ? ThemeService.Brush(ThemeService.BgElevated)
                    : Brushes.Transparent;
        fullViewBtn.Click += (_, _) =>
        {
            var next =
                studio.StudioInspectorLayout == StudioInspectorMode.FullView
                    ? StudioInspectorMode.Balanced
                    : StudioInspectorMode.FullView;
            setInspectorModeAction?.Invoke(next);
        };
        ToolTip.SetTip(fullViewBtn, "Toggle full-canvas Page Inspector (Esc to restore)");
        inspHeaderActions.Children.Add(fullViewBtn);

        if (
            !string.IsNullOrWhiteSpace(studio.SelectedPdfPath)
            && File.Exists(studio.SelectedPdfPath)
        )
        {
            var openReaderBtn = UiBuilder.GhostButton(
                "Reader",
                MaterialIconKind.BookOpenPageVariantOutline,
                () =>
                {
                    vm.OpenPdfInReader(studio.SelectedPdfPath);
                    vm.PdfReader.JumpToPage(studio.CurrentPreviewPdfPage);
                }
            );
            ToolTip.SetTip(openReaderBtn, $"Open page {studio.CurrentPreviewPdfPage} in Full PDF Reader");
            inspHeaderActions.Children.Add(openReaderBtn);
        }
        Grid.SetColumn(inspHeaderActions, 1);
        inspHeader.Children.Add(inspHeaderActions);
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
            int inspZoom = Math.Clamp(studio.PreviewZoomPercent, 40, 350);

            void ApplyInspectorZoom(int zoomPercent)
            {
                if (zoomPercent == 100)
                {
                    img.Width = double.NaN;
                    sheet.HorizontalAlignment = HorizontalAlignment.Stretch;
                    scroll.HorizontalScrollBarVisibility =
                        Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
                }
                else
                {
                    double viewportBase = Math.Max(320, scroll.Bounds.Width - 28);
                    img.Width = Math.Max(140, viewportBase * (zoomPercent / 100.0));
                    sheet.HorizontalAlignment = HorizontalAlignment.Center;
                    scroll.HorizontalScrollBarVisibility =
                        zoomPercent > 100
                            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                            : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
                }
            }

            scroll.SizeChanged += (_, e) =>
            {
                if (e.NewSize.Width > 0 && inspZoom != 100)
                    ApplyInspectorZoom(inspZoom);
            };

            UiBuilder.AttachPdfWheelAndPan(
                scroll,
                delta =>
                {
                    int oldZ = inspZoom;
                    inspZoom = Math.Clamp(inspZoom + delta, 40, 350);
                    if (inspZoom == oldZ)
                        return;
                    studio.SetPreviewZoomPercentQuiet(inspZoom);
                    double ratio = inspZoom / (double)oldZ;
                    ApplyInspectorZoom(inspZoom);
                    scroll.Offset = new Vector(
                        Math.Max(0, scroll.Offset.X * ratio),
                        Math.Max(0, scroll.Offset.Y * ratio)
                    );
                }
            );
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
        Grid.SetColumn(rightCard, 2);
        splitBody.Children.Add(rightCard);

        var compactBackToGridBtn = UiBuilder.GhostButton(
            "Back to Grid",
            MaterialIconKind.ViewGridOutline,
            () => setInspectorModeAction?.Invoke(StudioInspectorMode.Balanced)
        );
        compactBackToGridBtn.IsVisible = false;
        compactBackToGridBtn.Margin = new Thickness(0, 0, 6, 0);
        var inspTitleRow = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
            ColumnSpacing = 4,
        };
        var existingInspInfo = inspHeader.Children[0];
        inspHeader.Children.RemoveAt(0);
        inspTitleRow.Children.Add(compactBackToGridBtn);
        Grid.SetColumn(existingInspInfo, 1);
        inspTitleRow.Children.Add(existingInspInfo);
        inspHeader.Children.Insert(0, inspTitleRow);

        void SetStudioPaneTransitions(bool enabled)
        {
            if (enabled)
            {
                dockedTocSlot.Transitions =
                [
                    new DoubleTransition
                    {
                        Property = Layoutable.WidthProperty,
                        Duration = TimeSpan.FromMilliseconds(185),
                        Easing = new CubicEaseOut(),
                    },
                    new DoubleTransition
                    {
                        Property = Visual.OpacityProperty,
                        Duration = TimeSpan.FromMilliseconds(150),
                        Easing = new CubicEaseOut(),
                    },
                ];
                leftGridSlot.Transitions =
                [
                    new DoubleTransition
                    {
                        Property = Layoutable.WidthProperty,
                        Duration = TimeSpan.FromMilliseconds(185),
                        Easing = new CubicEaseOut(),
                    },
                    new DoubleTransition
                    {
                        Property = Visual.OpacityProperty,
                        Duration = TimeSpan.FromMilliseconds(150),
                        Easing = new CubicEaseOut(),
                    },
                ];
                tocFloatingScrim.Transitions =
                [
                    new DoubleTransition
                    {
                        Property = Visual.OpacityProperty,
                        Duration = TimeSpan.FromMilliseconds(160),
                        Easing = new CubicEaseOut(),
                    },
                ];
                tocDrawerTransform.Transitions =
                [
                    new DoubleTransition
                    {
                        Property = TranslateTransform.XProperty,
                        Duration = TimeSpan.FromMilliseconds(190),
                        Easing = new CubicEaseOut(),
                    },
                ];
                tocFloatingDrawer.Transitions =
                [
                    new DoubleTransition
                    {
                        Property = Visual.OpacityProperty,
                        Duration = TimeSpan.FromMilliseconds(160),
                        Easing = new CubicEaseOut(),
                    },
                ];
            }
            else
            {
                dockedTocSlot.Transitions = null;
                leftGridSlot.Transitions = null;
                tocFloatingScrim.Transitions = null;
                tocDrawerTransform.Transitions = null;
                tocFloatingDrawer.Transitions = null;
            }
        }

        void ApplyStudioResponsiveLayout(double width, bool animate)
        {
            if (width <= 0)
                return;

            _lastStudioViewportWidth = width;
            SetStudioPaneTransitions(animate);

            ApplyStudioHeaderLayout(width < 1060);

            bool isCompact = width < 900;
            var mode = studio.StudioInspectorLayout;
            bool isFullView = mode == StudioInspectorMode.FullView || (isCompact && studio.IsStudioCompactInspectorOpen);
            bool isExpandedLeft = !isCompact && mode == StudioInspectorMode.ExpandedLeft;
            bool tocOpen = studio.IsStudioPageTocOpen;

            // Update TOC button active styling in place
            tocToggleBtn.Background = tocOpen
                ? ThemeService.Brush(ThemeService.BgElevated)
                : Brushes.Transparent;
            tocToggleBtn.BorderBrush = tocOpen
                ? ThemeService.Brush(ThemeService.BorderStrong)
                : Brushes.Transparent;
            tocToggleBtn.BorderThickness = new Thickness(tocOpen ? 1 : 0);

            // Update Inspector layout button icons & tooltips in place
            expandLeftBtn.IsVisible = !isCompact && !isFullView;
            expandLeftIcon.Kind = isExpandedLeft
                ? MaterialIconKind.ChevronDoubleRight
                : MaterialIconKind.ChevronDoubleLeft;
            expandLeftBtn.Background = isExpandedLeft
                ? ThemeService.Brush(ThemeService.BgElevated)
                : Brushes.Transparent;
            ToolTip.SetTip(
                expandLeftBtn,
                isExpandedLeft
                    ? "Restore balanced Grid / Inspector split"
                    : "Expand Inspector left (collapses thumbnail grid to 2 columns)"
            );

            fullViewIcon.Kind = isFullView
                ? MaterialIconKind.FullscreenExit
                : MaterialIconKind.Fullscreen;
            fullViewBtn.Background = isFullView
                ? ThemeService.Brush(ThemeService.BgElevated)
                : Brushes.Transparent;
            compactBackToGridBtn.IsVisible = isFullView;

            // 1. Animate TOC: docked width-shift on Wide (>= 1180px) in Balanced mode, floating drawer otherwise!
            bool useDockedToc = width >= 1180 && !isExpandedLeft && !isFullView;
            if (useDockedToc)
            {
                tocFloatingScrim.Opacity = 0;
                tocFloatingScrim.IsHitTestVisible = false;
                tocDrawerTransform.X = -270;
                tocFloatingDrawer.Opacity = 0;
                tocFloatingDrawer.IsHitTestVisible = false;

                if (tocPanel.Parent == tocFloatingDrawer)
                {
                    tocFloatingDrawer.Child = null;
                    dockedTocSlot.Child = tocPanel;
                }

                dockedTocSlot.Width = tocOpen ? 248 : 0;
                dockedTocSlot.Opacity = tocOpen ? 1.0 : 0.0;
                dockedTocSlot.IsHitTestVisible = tocOpen;
            }
            else
            {
                dockedTocSlot.Width = 0;
                dockedTocSlot.Opacity = 0;
                dockedTocSlot.IsHitTestVisible = false;

                if (tocPanel.Parent == dockedTocSlot)
                {
                    dockedTocSlot.Child = null;
                    tocFloatingDrawer.Child = tocPanel;
                }

                tocFloatingScrim.Opacity = tocOpen ? 1.0 : 0.0;
                tocFloatingScrim.IsHitTestVisible = tocOpen;
                tocDrawerTransform.X = tocOpen ? 0 : -270;
                tocFloatingDrawer.Opacity = tocOpen ? 1.0 : 0.0;
                tocFloatingDrawer.IsHitTestVisible = tocOpen;
            }

            // 2. Snap Left Thumbnail Grid Slot Width to exact column multiples (zero empty dead space; extra pixels go to PDF Inspector!)
            // Each tile is 126 + 12 (padding) + 10 (margin) = 148px; card + scroll + slot gap chrome = ~62px
            const double TilePitch = 148;
            const double GridChrome = 62;
            const double MinInspectorWidth = 340;
            double avail = Math.Max(320, width - 36);

            if (isCompact)
            {
                if (isFullView)
                {
                    leftGridSlot.Padding = new Thickness(0);
                    leftGridSlot.Width = 0;
                    leftGridSlot.Opacity = 0;
                    leftGridSlot.IsHitTestVisible = false;
                    rightCard.IsVisible = true;
                }
                else
                {
                    leftGridSlot.Padding = new Thickness(0);
                    leftGridSlot.Width = avail;
                    leftGridSlot.Opacity = 1;
                    leftGridSlot.IsHitTestVisible = true;
                    rightCard.IsVisible = false;
                }
            }
            else
            {
                rightCard.IsVisible = true;
                if (isFullView)
                {
                    leftGridSlot.Padding = new Thickness(0);
                    leftGridSlot.Width = 0;
                    leftGridSlot.Opacity = 0;
                    leftGridSlot.IsHitTestVisible = false;
                }
                else if (isExpandedLeft)
                {
                    // Exact 2-column strip (358px) if Inspector still gets >= 380px, else exact 1-column strip (210px)
                    double twoColWidth = 2 * TilePitch + GridChrome;
                    double oneColWidth = 1 * TilePitch + GridChrome;
                    double targetW = avail - twoColWidth >= 380 ? twoColWidth : oneColWidth;
                    leftGridSlot.Padding = new Thickness(0, 0, 12, 0);
                    leftGridSlot.Width = targetW;
                    leftGridSlot.Opacity = 1;
                    leftGridSlot.IsHitTestVisible = true;
                }
                else
                {
                    double rem = avail - (useDockedToc && tocOpen ? 248 : 0);
                    double maxGridBudget = Math.Max(1 * TilePitch + GridChrome, rem - MinInspectorWidth);
                    int targetCols = Math.Max(1, (int)Math.Floor((Math.Min(rem * 0.62, maxGridBudget) - GridChrome) / TilePitch));
                    double snappedW = Math.Min(maxGridBudget, targetCols * TilePitch + GridChrome);
                    leftGridSlot.Padding = new Thickness(0, 0, 12, 0);
                    leftGridSlot.Width = snappedW;
                    leftGridSlot.Opacity = 1;
                    leftGridSlot.IsHitTestVisible = true;
                }
            }
        }

        setTocOpenAction = open =>
        {
            studio.SetStudioPageTocOpenQuiet(open);
            ApplyStudioResponsiveLayout(
                root.Bounds.Width > 0 ? root.Bounds.Width : _lastStudioViewportWidth,
                animate: true
            );
        };

        setInspectorModeAction = nextMode =>
        {
            studio.SetStudioInspectorLayoutQuiet(nextMode);
            ApplyStudioResponsiveLayout(
                root.Bounds.Width > 0 ? root.Bounds.Width : _lastStudioViewportWidth,
                animate: true
            );
        };

        // Apply exact layout synchronously BEFORE first frame so page switches / fresh boot never flicker or shift!
        if (_lastStudioViewportWidth > 0)
            ApplyStudioResponsiveLayout(_lastStudioViewportWidth, animate: false);

        root.SizeChanged += (_, e) => ApplyStudioResponsiveLayout(e.NewSize.Width, animate: false);

        Grid.SetRow(splitBody, 2);
        root.Children.Add(splitBody);

        Grid.SetRow(tocFloatingScrim, 2);
        root.Children.Add(tocFloatingScrim);
        Grid.SetRow(tocFloatingDrawer, 2);
        root.Children.Add(tocFloatingDrawer);

        return root;
    }

    /// <summary>
    /// Shared collapsible Page TOC showing each page, its scope state (In Scope / Excluded),
    /// optional section subtitle, 1-click include/exclude toggle, and jump-to-page preview.
    /// Used in both the Builder Scope card and as a collapsible left sidebar in Page &amp; Section Studio.
    /// </summary>
    private static Control BuildCollapsiblePageTocPanel(
        QuizStudioViewModel studio,
        bool isFullHeight,
        Action? onCloseOverride = null
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
            ThemeService.Brush(ThemeService.TextSecondary),
            ThemeService.Brush(ThemeService.BgElevated),
            fontSize: 10.5
        );
        Grid.SetColumn(countPill, 1);
        countPill.HorizontalAlignment = HorizontalAlignment.Left;
        headerBar.Children.Add(countPill);

        if (isFullHeight)
        {
            var collapseBtn = UiBuilder.IconButton(
                MaterialIconKind.ChevronDoubleLeft,
                onCloseOverride ?? studio.ToggleStudioPageToc,
                15,
                ThemeService.Brush(ThemeService.TextSecondary)
            );
            ToolTip.SetTip(collapseBtn, "Collapse Pages TOC");
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
                        ? ThemeService.Brush(ThemeService.SuccessFg)
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
                        ? ThemeService.Brush(ThemeService.SuccessFg)
                        : ThemeService.Brush(ThemeService.TextMuted),
                    inScope
                        ? ThemeService.Brush(ThemeService.SuccessBg)
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
