using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using ScholarFlow.Core.Knowledge;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views;

/// <summary>Quiz Builder UI for non-PDF study sources (code, markdown, text, images) and mixed PDF+file mode.</summary>
public static partial class QuizStudioViewBuilder
{
    private static MaterialIconKind IconForSource(StudySourceKind kind) =>
        kind switch
        {
            StudySourceKind.Image => MaterialIconKind.ImageOutline,
            StudySourceKind.Markdown => MaterialIconKind.LanguageMarkdownOutline,
            StudySourceKind.PlainText => MaterialIconKind.FileDocumentOutline,
            _ => MaterialIconKind.CodeTags,
        };

    /// <summary>
    /// Inline Course Files selector strip embedded directly inside Card 1 ("1. Study Sources &amp; Scope")
    /// so PDFs and Code/Markdown/Images live in one unified source section.
    /// </summary>
    private static Control BuildInlineFileSourcesSection(QuizStudioViewModel studio)
    {
        var stack = new StackPanel { Spacing = 6 };
        var header = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 6,
        };

        var labelText =
            studio.IsMixedSourceMode
                ? $"Course Files ({studio.SelectedFileSourcePaths.Count} selected · Combined with PDF)"
            : studio.IsFileSourceMode
                ? $"Course Files ({studio.SelectedFileSourcePaths.Count} selected · Files Only)"
            : $"Course Files — Code, Notes & Diagrams ({studio.StudyFiles.Count} available)";

        header.Children.Add(
            new TextBlock
            {
                Text = labelText,
                FontSize = 11.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );

        var headerActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        bool hasValidPdf =
            !string.IsNullOrWhiteSpace(studio.SelectedPdfPath)
            && File.Exists(studio.SelectedPdfPath);
        if (studio.IsFileSourceMode && hasValidPdf)
        {
            headerActions.Children.Add(
                UiBuilder.GhostButton(
                    studio.IncludePdfWithFiles ? "PDF + Files" : "+ Combine PDF",
                    MaterialIconKind.FilePlusOutline,
                    studio.ToggleIncludePdfWithFiles,
                    isActive: studio.IncludePdfWithFiles
                )
            );
        }

        if (studio.IsFileSourceMode)
        {
            headerActions.Children.Add(
                UiBuilder.GhostButton(
                    "Clear Files",
                    MaterialIconKind.Close,
                    studio.ClearFileSources
                )
            );
        }

        if (studio.StudyFiles.Count > 0)
        {
            headerActions.Children.Add(
                UiBuilder.GhostButton(
                    studio.ShowStudyFiles
                        ? "Hide Files"
                        : $"Browse Files ({studio.StudyFiles.Count})",
                    studio.ShowStudyFiles
                        ? MaterialIconKind.ChevronUp
                        : MaterialIconKind.ChevronDown,
                    studio.ToggleShowStudyFiles,
                    isActive: studio.ShowStudyFiles
                )
            );
        }

        Grid.SetColumn(headerActions, 1);
        header.Children.Add(headerActions);
        stack.Children.Add(header);

        if (studio.IsLoadingStudyFiles)
        {
            var skelRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            skelRow.Children.Add(BuildSkeletonBlock(24, 96, 0.55, new CornerRadius(5)));
            skelRow.Children.Add(BuildSkeletonBlock(24, 118, 0.45, new CornerRadius(5)));
            skelRow.Children.Add(BuildSkeletonBlock(24, 84, 0.40, new CornerRadius(5)));
            stack.Children.Add(skelRow);
            return stack;
        }

        if (studio.StudyFiles.Count == 0)
            return stack;

        var selected = studio
            .StudyFiles.Where(f => studio.IsFileSourceSelected(f.FilePath))
            .ToList();
        var visible = studio.ShowStudyFiles ? studio.StudyFiles : selected;
        if (visible.Count > 0)
        {
            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var f in visible)
            {
                var path = f.FilePath;
                var btn = UiBuilder.GhostButton(
                    f.DisplayName,
                    IconForSource(f.Kind),
                    () => studio.ToggleFileSource(path),
                    isActive: studio.IsFileSourceSelected(path)
                );
                btn.Margin = new Thickness(0, 0, 5, 4);
                wrap.Children.Add(btn);
            }

            stack.Children.Add(
                studio.ShowStudyFiles && visible.Count > 18
                    ? new Border
                    {
                        MaxHeight = 180,
                        Child = UiBuilder.PersistentScrollViewer("quiz-study-files", wrap),
                    }
                    : wrap
            );
        }

        return stack;
    }

    /// <summary>Right pane in file-only mode: selected files with one-click open in the Source Viewer.</summary>
    private static Control BuildFileSourcesPreviewPane(
        MainWindowViewModel vm,
        QuizStudioViewModel studio
    )
    {
        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 20) };
        stack.Children.Add(
            new TextBlock
            {
                Text = $"Selected Study Files ({studio.SelectedFileSourcePaths.Count})",
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            }
        );

        foreach (var path in studio.SelectedFileSourcePaths.ToList())
        {
            var d = StudySourceClassifier.Describe(path);
            var row = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
                ColumnSpacing = 8,
            };
            row.Children.Add(
                new Material.Icons.Avalonia.MaterialIcon
                {
                    Kind = IconForSource(d.Kind),
                    Width = 16,
                    Height = 16,
                    Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );

            var info = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(
                new TextBlock
                {
                    Text = d.DisplayName,
                    FontSize = 12.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                }
            );
            info.Children.Add(
                new TextBlock
                {
                    Text = $"{d.Kind} · {d.Language} · {d.SizeBytes / 1024.0:0.#} KB",
                    FontSize = 11,
                    Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                }
            );
            Grid.SetColumn(info, 1);
            row.Children.Add(info);

            var open = UiBuilder.GhostButton("View", MaterialIconKind.OpenInNew, () =>
                vm.OpenFileInSourceViewer(path)
            );
            Grid.SetColumn(open, 2);
            row.Children.Add(open);

            stack.Children.Add(UiBuilder.Card(row, padding: new Thickness(10, 7)));
        }

        return UiBuilder.PersistentScrollViewer("quiz-file-sources-preview", stack);
    }

    /// <summary>Right pane in mixed mode: compact selected-files strip above the Live PDF Scope Preview.</summary>
    private static Control BuildMixedPreviewPane(MainWindowViewModel vm, QuizStudioViewModel studio)
    {
        var grid = new Grid { RowDefinitions = RowDefinitions.Parse("Auto,*"), RowSpacing = 10 };
        var filesWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var path in studio.SelectedFileSourcePaths.ToList())
        {
            var d = StudySourceClassifier.Describe(path);
            var captured = path;
            var btn = UiBuilder.GhostButton(d.DisplayName, IconForSource(d.Kind), () =>
                vm.OpenFileInSourceViewer(captured)
            );
            btn.Margin = new Thickness(0, 0, 5, 4);
            filesWrap.Children.Add(btn);
        }

        var stripStack = new StackPanel { Spacing = 4 };
        stripStack.Children.Add(
            UiBuilder.SectionHeader(
                $"Combined Files ({studio.SelectedFileSourcePaths.Count}) — Click to View"
            )
        );
        stripStack.Children.Add(filesWrap);
        grid.Children.Add(UiBuilder.Card(stripStack, padding: new Thickness(10, 8)));

        var pdfPane = BuildLivePdfPreviewPane(vm, studio);
        Grid.SetRow(pdfPane, 1);
        grid.Children.Add(pdfPane);
        return grid;
    }
}
