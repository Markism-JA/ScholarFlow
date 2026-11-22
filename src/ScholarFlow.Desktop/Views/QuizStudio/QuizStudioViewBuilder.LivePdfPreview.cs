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
                isActive: !studio.PreviewBrowseAllPages
            );
            scopeModeBtn.Margin = new Thickness(0, 0, 4, 2);
            headerActions.Children.Add(scopeModeBtn);

            var allModeBtn = UiBuilder.GhostButton(
                "All Pages",
                null,
                () => studio.SetPreviewBrowseMode(true),
                isActive: studio.PreviewBrowseAllPages
            );
            allModeBtn.Margin = new Thickness(0, 0, 4, 2);
            headerActions.Children.Add(allModeBtn);
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

    // =========================================================================
    // 1B. HAIRLINE QUIZ CATALOG & MULTI-QUIZ COMBINER VIEW
    // =========================================================================
}
