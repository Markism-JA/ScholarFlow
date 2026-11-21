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

    /// <summary>Selector card: pick code / notes / images as quiz sources (alone or combined with the active PDF).</summary>
    private static Control BuildFileSourcesCard(QuizStudioViewModel studio)
    {
        var stack = new StackPanel { Spacing = 8 };
        var header = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 6 };
        header.Children.Add(
            UiBuilder.SectionHeader(
                studio.IsMixedSourceMode
                    ? $"Code, Notes & Images ({studio.SelectedFileSourcePaths.Count} selected · Combined with PDF)"
                    : studio.IsFileSourceMode
                        ? $"Code, Notes & Images ({studio.SelectedFileSourcePaths.Count} selected · Files Only)"
                        : "Code, Notes & Images (files only or combine with PDF)"
            )
        );

        var headerActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        bool hasValidPdf = !string.IsNullOrWhiteSpace(studio.SelectedPdfPath) && File.Exists(studio.SelectedPdfPath);
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
                UiBuilder.GhostButton("Clear Files", MaterialIconKind.Close, studio.ClearFileSources)
            );
        }

        headerActions.Children.Add(
            UiBuilder.GhostButton(
                studio.ShowStudyFiles ? "Hide Files" : $"Files ({studio.StudyFiles.Count})",
                studio.ShowStudyFiles ? MaterialIconKind.ChevronUp : MaterialIconKind.ChevronDown,
                studio.ToggleShowStudyFiles,
                isActive: studio.ShowStudyFiles
            )
        );
        Grid.SetColumn(headerActions, 1);
        header.Children.Add(headerActions);
        stack.Children.Add(header);

        if (studio.StudyFiles.Count == 0)
        {
            stack.Children.Add(MutedNote("No code, markdown, text, or image files found in this course folder."));
            return UiBuilder.Card(stack, padding: new Thickness(12, 10));
        }

        // Always show selected files as removable pills so the chosen scope stays visible when the list is collapsed.
        var selected = studio.StudyFiles.Where(f => studio.IsFileSourceSelected(f.FilePath)).ToList();
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
                        MaxHeight = 190,
                        Child = UiBuilder.PersistentScrollViewer("quiz-study-files", wrap),
                    }
                    : wrap
            );
        }
        else
        {
            stack.Children.Add(MutedNote("Open Files and tap code, notes, or images to quiz from them (alone or combined with your PDF)."));
        }

        if (studio.HasCodeFileSelected)
        {
            var mixRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            mixRow.Children.Add(
                UiBuilder.GhostButton(
                    studio.IsCodeAwareMixActive ? "Code-Aware Mix Active" : "Apply Code-Aware Mix",
                    MaterialIconKind.CodeBraces,
                    studio.ApplyCodeAwareMix,
                    isActive: studio.IsCodeAwareMixActive
                )
            );
            mixRow.Children.Add(
                new TextBlock
                {
                    Text = "Boosts Trace/Output (ID), Code Completion (Fill), Bug Spotting (Sit) & Execution Order",
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                }
            );
            stack.Children.Add(mixRow);
        }

        return UiBuilder.Card(stack, padding: new Thickness(12, 10));
    }

    private static Control BuildFileModeScopeNote(QuizStudioViewModel studio)
    {
        int images = studio.SelectedFileSourcePaths.Count(ImagePacketPacker.IsImage);
        int text = studio.SelectedFileSourcePaths.Count - images;
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(
            UiBuilder.SectionHeader(
                studio.IsMixedSourceMode
                    ? "Combined Files Included Alongside PDF Scope"
                    : "2. Scope = Selected Files"
            )
        );
        stack.Children.Add(
            MutedNote(
                $"{text} code/text file(s) and {images} image(s). Evidence is grounded to exact line ranges "
                    + "(or the source image). API AI sends numbered lines + vision images; Web AI mode copies the same prompt "
                    + "and can pack images/PDF into one uploadable PDF."
            )
        );
        return UiBuilder.Card(stack, padding: new Thickness(12, 10));
    }

    /// <summary>Right pane in file-only mode: selected files with one-click open in the Source Viewer.</summary>
    private static Control BuildFileSourcesPreviewPane(MainWindowViewModel vm, QuizStudioViewModel studio)
    {
        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 20) };
        stack.Children.Add(
            new TextBlock
            {
                Text = "Selected Source Files",
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            }
        );

        foreach (var path in studio.SelectedFileSourcePaths.ToList())
        {
            var d = StudySourceClassifier.Describe(path);
            var row = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"), ColumnSpacing = 8 };
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

            var open = UiBuilder.GhostButton("View", MaterialIconKind.OpenInNew, () => vm.OpenFileInSourceViewer(path));
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
            var btn = UiBuilder.GhostButton(
                d.DisplayName,
                IconForSource(d.Kind),
                () => vm.OpenFileInSourceViewer(captured)
            );
            btn.Margin = new Thickness(0, 0, 5, 4);
            filesWrap.Children.Add(btn);
        }

        var stripStack = new StackPanel { Spacing = 4 };
        stripStack.Children.Add(UiBuilder.SectionHeader($"Combined Files ({studio.SelectedFileSourcePaths.Count}) — Click to View"));
        stripStack.Children.Add(filesWrap);
        grid.Children.Add(UiBuilder.Card(stripStack, padding: new Thickness(10, 8)));

        var pdfPane = BuildLivePdfPreviewPane(vm, studio);
        Grid.SetRow(pdfPane, 1);
        grid.Children.Add(pdfPane);
        return grid;
    }

    private static TextBlock MutedNote(string text) =>
        new()
        {
            Text = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeService.Brush(ThemeService.TextSecondary),
        };
}
