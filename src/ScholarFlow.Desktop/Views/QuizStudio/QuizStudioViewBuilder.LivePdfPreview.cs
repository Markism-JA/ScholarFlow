using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views;

public static partial class QuizStudioViewBuilder
{
    private static Control BuildLivePdfPreviewPane(
        MainWindowViewModel vm,
        QuizStudioViewModel studio
    )
    {
        var grid = new Grid { RowDefinitions = RowDefinitions.Parse("Auto,*,Auto") };

        // 1. Top Preview Header Bar
        var headerGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
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
        leftInfo.Children.Add(
            new TextBlock
            {
                Text =
                    $"{studio.CurrentPreviewSectionTitle}  ·  PDF Page {studio.CurrentPreviewPdfPage}",
                FontSize = 11.5,
                Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
                TextTrimming = TextTrimming.CharacterEllipsis,
            }
        );
        headerGrid.Children.Add(leftInfo);

        if (
            !string.IsNullOrWhiteSpace(studio.SelectedPdfPath)
            && File.Exists(studio.SelectedPdfPath)
        )
        {
            var openReaderBtn = UiBuilder.GhostButton(
                "Full Reader",
                MaterialIconKind.ArrowExpandAll,
                () =>
                {
                    vm.OpenPdfInReader(studio.SelectedPdfPath);
                    vm.PdfReader.JumpToPage(studio.CurrentPreviewPdfPage);
                }
            );
            Grid.SetColumn(openReaderBtn, 1);
            headerGrid.Children.Add(openReaderBtn);
        }
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
                HorizontalAlignment = studio.PreviewZoomPercent == 100
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
            UiBuilder.AttachPdfWheelAndPan(previewScroll, delta => studio.ZoomPreview(delta));
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

        // 3. Bottom Scope Page Flipper Bar (< Prev in Scope | Page X of Y (PDF p. Z) | Next in Scope >)
        var footerBar = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
            Margin = new Thickness(0, 8, 0, 0),
        };

        var prevBtn = UiBuilder.GhostButton(
            "Prev in Scope",
            MaterialIconKind.ChevronLeft,
            studio.PreviousPreviewPage
        );
        prevBtn.IsEnabled = studio.PreviewScopeIndex > 0;
        footerBar.Children.Add(prevBtn);

        var pageCounter = new TextBlock
        {
            Text =
                studio.ScopePreviewPageCount > 0
                    ? $"Scope Page {studio.PreviewScopeIndex + 1} of {studio.ScopePreviewPageCount}  (PDF p. {studio.CurrentPreviewPdfPage})"
                    : "0 pages in scope",
            FontSize = 11.5,
            FontWeight = FontWeight.Medium,
            Foreground = ThemeService.Brush(ThemeService.TextSecondary),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(pageCounter, 1);
        footerBar.Children.Add(pageCounter);

        var nextBtn = UiBuilder.GhostButton(
            "Next in Scope",
            MaterialIconKind.ChevronRight,
            studio.NextPreviewPage
        );
        nextBtn.IsEnabled = studio.PreviewScopeIndex + 1 < studio.ScopePreviewPageCount;
        Grid.SetColumn(nextBtn, 2);
        footerBar.Children.Add(nextBtn);

        Grid.SetRow(footerBar, 2);
        grid.Children.Add(footerBar);

        return UiBuilder.Card(grid, padding: new Thickness(12, 10));
    }

    // =========================================================================
    // 1B. HAIRLINE QUIZ CATALOG & MULTI-QUIZ COMBINER VIEW
    // =========================================================================
}
