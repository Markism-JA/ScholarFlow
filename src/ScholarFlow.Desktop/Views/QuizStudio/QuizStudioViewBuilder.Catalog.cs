using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using ScholarFlow.Core.Quiz;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views;

public static partial class QuizStudioViewBuilder
{
    private static ScrollViewer BuildCatalogView(MainWindowViewModel vm, QuizStudioViewModel studio)
    {
        var stack = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 24),
        };

        var filteredQuizzes = (
            studio.CatalogShowAllCourses || studio.SelectedCourse is null
                ? studio.SavedQuizzes
                : studio.SavedQuizzes.Where(q =>
                    string.Equals(
                        q.CourseFolder,
                        studio.SelectedCourse.FolderName,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
        )
            .Where(q =>
                string.IsNullOrWhiteSpace(studio.CatalogTermFilter)
                || string.Equals(
                    q.Term,
                    studio.CatalogTermFilter,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToList();

        // Responsive Catalog Header & Actions (wraps cleanly on any viewport width)
        var catalogHeaderStack = new StackPanel { Spacing = 6 };
        catalogHeaderStack.Children.Add(
            new TextBlock
            {
                Text = studio.CatalogShowAllCourses
                    ? $"All Subjects Catalog · {filteredQuizzes.Count} saved"
                    : $"{studio.SelectedCourse?.DisplayTitle ?? "Subject"} Catalog · {filteredQuizzes.Count} saved",
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                TextTrimming = TextTrimming.CharacterEllipsis,
            }
        );

        var actionsWrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var toggleAllBtn = UiBuilder.GhostButton(
            studio.CatalogShowAllCourses
                ? "Active Subject Only"
                : $"All Subjects ({studio.SavedQuizzes.Count})",
            MaterialIconKind.FilterVariant,
            studio.ToggleCatalogShowAllCourses,
            isActive: studio.CatalogShowAllCourses
        );
        toggleAllBtn.Margin = new Thickness(0, 0, 6, 4);
        actionsWrap.Children.Add(toggleAllBtn);

        var interleavedBtn = UiBuilder.GhostButton(
            "Interleaved Review",
            MaterialIconKind.ShuffleVariant,
            () => studio.StartInterleavedReviewSession(allCourses: studio.CatalogShowAllCourses)
        );
        interleavedBtn.Margin = new Thickness(0, 0, 6, 4);
        actionsWrap.Children.Add(interleavedBtn);

        var importJsonBtn = UiBuilder.GhostButton(
            "Import JSON",
            MaterialIconKind.FileImportOutline,
            async () =>
            {
                var top = TopLevel.GetTopLevel(actionsWrap);
                if (top?.StorageProvider is null)
                    return;
                var files = await top.StorageProvider.OpenFilePickerAsync(
                    new Avalonia.Platform.Storage.FilePickerOpenOptions
                    {
                        Title =
                            $"Import Quiz Deck JSON into {studio.SelectedCourse?.Title ?? "Course"}",
                        AllowMultiple = false,
                        FileTypeFilter =
                        [
                            new Avalonia.Platform.Storage.FilePickerFileType("JSON Files")
                            {
                                Patterns = ["*.json"],
                            },
                        ],
                    }
                );
                var path = files
                    .Select(f =>
                        Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(f)
                    )
                    .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    var raw = await File.ReadAllTextAsync(path);
                    await studio.ImportDeckJsonToSelectedCourseAsync(raw);
                }
            }
        );
        importJsonBtn.Margin = new Thickness(0, 0, 6, 4);
        actionsWrap.Children.Add(importJsonBtn);

        var newAssessBtn = UiBuilder.PrimaryButton("New Assessment", MaterialIconKind.Plus, () =>
            studio.SetSubTab(QuizStudioSubTab.Builder)
        );
        newAssessBtn.Margin = new Thickness(0, 0, 0, 4);
        actionsWrap.Children.Add(newAssessBtn);

        catalogHeaderStack.Children.Add(actionsWrap);
        stack.Children.Add(catalogHeaderStack);

        // Dynamic Term Filter (driven by vm.ActiveTerms + actual deck terms; hidden on Local-Only basins with no terms)
        var effectiveTerms = new List<string>(vm.ActiveTerms);
        foreach (
            var existingTerm in studio
                .SavedQuizzes.Select(q => q.Term?.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Cast<string>()
        )
        {
            if (!effectiveTerms.Contains(existingTerm, StringComparer.OrdinalIgnoreCase))
                effectiveTerms.Add(existingTerm);
        }

        var filterAndPlaybackWrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (effectiveTerms.Count > 0)
        {
            var allTermBtn = UiBuilder.GhostButton(
                "All Terms",
                studio.CatalogTermFilter is null ? MaterialIconKind.Check : null,
                () => studio.SetCatalogTermFilter(null),
                isActive: studio.CatalogTermFilter is null
            );
            allTermBtn.Margin = new Thickness(0, 0, 5, 4);
            filterAndPlaybackWrap.Children.Add(allTermBtn);

            foreach (var term in effectiveTerms)
            {
                var capturedTerm = term;
                bool isTermActive = string.Equals(
                    studio.CatalogTermFilter,
                    term,
                    StringComparison.OrdinalIgnoreCase
                );
                var tBtn = UiBuilder.GhostButton(
                    term,
                    isTermActive ? MaterialIconKind.Check : null,
                    () => studio.SetCatalogTermFilter(capturedTerm),
                    isActive: isTermActive
                );
                tBtn.Margin = new Thickness(0, 0, 5, 4);
                filterAndPlaybackWrap.Children.Add(tBtn);
            }
        }

        var shuffleQBtn = UiBuilder.GhostButton(
            "Shuffle Questions",
            studio.ShuffleQuestionsOnStart
                ? MaterialIconKind.CheckboxMarked
                : MaterialIconKind.CheckboxBlankOutline,
            studio.ToggleShuffleQuestionsOnStart,
            isActive: studio.ShuffleQuestionsOnStart
        );
        shuffleQBtn.Margin = new Thickness(effectiveTerms.Count > 0 ? 6 : 0, 0, 5, 4);
        filterAndPlaybackWrap.Children.Add(shuffleQBtn);

        var shuffleOptBtn = UiBuilder.GhostButton(
            "Shuffle Options",
            studio.ShuffleOptionsOnStart
                ? MaterialIconKind.CheckboxMarked
                : MaterialIconKind.CheckboxBlankOutline,
            studio.ToggleShuffleOptionsOnStart,
            isActive: studio.ShuffleOptionsOnStart
        );
        shuffleOptBtn.Margin = new Thickness(0, 0, 0, 4);
        filterAndPlaybackWrap.Children.Add(shuffleOptBtn);

        stack.Children.Add(filterAndPlaybackWrap);

        // Multi-Quiz Combiner & Organizer Action Bar (responsive 2-row stack)
        if (studio.SelectedQuizIdsForMerge.Count > 0)
        {
            var mergeStack = new StackPanel { Spacing = 6 };
            var mergeTitleBox = new TextBox
            {
                Watermark = "Composite deck title (e.g. Comprehensive Review Exam)...",
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            mergeStack.Children.Add(mergeTitleBox);

            var mergeBtnsWrap = new WrapPanel { Orientation = Orientation.Horizontal };

            var interleavedSelectedBtn = UiBuilder.GhostButton(
                $"Interleaved Review ({studio.SelectedQuizIdsForMerge.Count})",
                MaterialIconKind.Brain,
                () =>
                    studio.StartInterleavedReviewSession(allCourses: true, selectedDecksOnly: true),
                isActive: true
            );
            interleavedSelectedBtn.Margin = new Thickness(0, 0, 6, 4);
            mergeBtnsWrap.Children.Add(interleavedSelectedBtn);

            var curateBtn = UiBuilder.GhostButton(
                $"Curate in Organizer ({studio.SelectedQuizIdsForMerge.Count})",
                MaterialIconKind.TuneVariant,
                () => studio.OpenSelectedQuizzesInOrganizer(mergeTitleBox.Text),
                isActive: true
            );
            curateBtn.Margin = new Thickness(0, 0, 6, 4);
            mergeBtnsWrap.Children.Add(curateBtn);

            var mergeShuffleBtn = UiBuilder.PrimaryButton(
                $"Combine & Shuffle ({studio.SelectedQuizIdsForMerge.Count})",
                MaterialIconKind.CallMerge,
                async () =>
                    await studio.CombineSelectedQuizzesAsync(
                        mergeTitleBox.Text,
                        shuffleQuestions: true
                    )
            );
            mergeShuffleBtn.Margin = new Thickness(0, 0, 6, 4);
            mergeBtnsWrap.Children.Add(mergeShuffleBtn);

            var mergeOrderedBtn = UiBuilder.GhostButton(
                "Combine In-Order",
                MaterialIconKind.FormatListNumbered,
                async () =>
                    await studio.CombineSelectedQuizzesAsync(
                        mergeTitleBox.Text,
                        shuffleQuestions: false
                    )
            );
            mergeOrderedBtn.Margin = new Thickness(0, 0, 0, 4);
            mergeBtnsWrap.Children.Add(mergeOrderedBtn);

            mergeStack.Children.Add(mergeBtnsWrap);
            stack.Children.Add(UiBuilder.Card(mergeStack, padding: new Thickness(12, 8)));
        }

        if (filteredQuizzes.Count == 0)
        {
            stack.Children.Add(
                UiBuilder.Card(
                    new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "No assessments saved for this subject yet.",
                                FontSize = 13,
                                FontWeight = FontWeight.SemiBold,
                                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                            },
                            new TextBlock
                            {
                                Text =
                                    "Switch to 'Build' to generate a grounded exam from your course materials, or click 'Show All Subjects'.",
                                FontSize = 12,
                                Foreground = ThemeService.Brush(ThemeService.TextMuted),
                            },
                        },
                    }
                )
            );
            return UiBuilder.PersistentScrollViewer("quiz-catalog", stack);
        }

        var listStack = new StackPanel { Spacing = 0 };
        foreach (var quiz in filteredQuizzes)
        {
            listStack.Children.Add(BuildHairlineQuizRow(vm, studio, quiz, showCheckbox: true));
        }
        stack.Children.Add(UiBuilder.Card(listStack, padding: new Thickness(8, 4)));

        return UiBuilder.PersistentScrollViewer("quiz-catalog", stack);
    }

    private static Border BuildHairlineQuizRow(
        MainWindowViewModel vm,
        QuizStudioViewModel studio,
        QuizSessionRecord quiz,
        bool showCheckbox,
        bool compact = false
    )
    {
        bool isChecked = studio.SelectedQuizIdsForMerge.Contains(quiz.Id);

        var rowGrid = new Grid
        {
            ColumnDefinitions = showCheckbox
                ? ColumnDefinitions.Parse("Auto,*,Auto")
                : ColumnDefinitions.Parse("0,*,Auto"),
            ColumnSpacing = 10,
        };

        if (showCheckbox)
        {
            var chk = UiBuilder.IconButton(
                isChecked ? MaterialIconKind.CheckboxMarked : MaterialIconKind.CheckboxBlankOutline,
                () => studio.ToggleQuizSelectionForMerge(quiz.Id),
                16,
                isChecked
                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : ThemeService.Brush(ThemeService.TextMuted)
            );
            chk.VerticalAlignment = VerticalAlignment.Center;
            rowGrid.Children.Add(chk);
        }

        var centerStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        centerStack.Children.Add(
            new TextBlock
            {
                Text = quiz.Title,
                FontSize = 13.5,
                FontWeight = FontWeight.Medium,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
            }
        );

        var subParts = new List<string>
        {
            quiz.CourseTitle,
            $"{quiz.Questions.Count} items",
            quiz.ModelUsed,
        };
        if (!string.IsNullOrWhiteSpace(quiz.Term))
            subParts.Insert(1, quiz.Term);
        if (quiz.LastScorePercent.HasValue)
            subParts.Add($"Score: {quiz.LastScorePercent.Value:0.#}% ({quiz.AttemptCount}x)");

        centerStack.Children.Add(
            new TextBlock
            {
                Text = string.Join(" · ", subParts),
                FontSize = 11.5,
                Foreground = ThemeService.Brush(ThemeService.TextMuted),
                TextTrimming = TextTrimming.CharacterEllipsis,
            }
        );
        Grid.SetColumn(centerStack, 1);
        rowGrid.Children.Add(centerStack);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        bool isPdfStale = quiz.IsSourcePdfStale();
        if (isPdfStale && !compact)
        {
            var regroundBtn = UiBuilder.GhostButton(
                "Re-ground PDF",
                MaterialIconKind.Autorenew,
                async () => await studio.RegroundQuizAsync(quiz),
                isActive: true
            );
            actions.Children.Add(regroundBtn);
        }

        if (quiz.LastScorePercent.HasValue)
        {
            bool high = quiz.LastScorePercent.Value >= 75;
            actions.Children.Add(
                UiBuilder.Pill(
                    $"{quiz.LastScorePercent.Value:0}%",
                    high
                        ? ThemeService.Brush(ThemeService.SuccessFg)
                        : ThemeService.Brush(ThemeService.PriorityP2Fg),
                    high
                        ? ThemeService.Brush(ThemeService.SuccessBg)
                        : ThemeService.Brush(ThemeService.PriorityP2Bg)
                )
            );
        }

        if (quiz.HasInProgressAttempt)
        {
            int resumeQ = Math.Clamp(
                (quiz.InProgressQuestionIndex ?? 0) + 1,
                1,
                quiz.Questions.Count
            );
            actions.Children.Add(
                UiBuilder.PrimaryButton(
                    $"Continue (Q{resumeQ}/{quiz.Questions.Count})",
                    MaterialIconKind.PlayOutline,
                    () => studio.StartQuizSession(quiz, resumeSavedProgress: true)
                )
            );
            actions.Children.Add(
                UiBuilder.GhostButton("Restart", MaterialIconKind.Restart, () =>
                    studio.StartQuizSession(quiz, resumeSavedProgress: false)
                )
            );
        }
        else
        {
            actions.Children.Add(
                UiBuilder.GhostButton(
                    "Start",
                    MaterialIconKind.PlayOutline,
                    () => studio.StartQuizSession(quiz, resumeSavedProgress: false),
                    isActive: true
                )
            );
        }

        var shuffleStartBtn = UiBuilder.GhostButton(
            "Shuffle",
            MaterialIconKind.ShuffleVariant,
            () =>
                studio.StartQuizSession(
                    quiz,
                    shuffleQuestionsOverride: true,
                    shuffleOptionsOverride: true
                )
        );
        actions.Children.Add(shuffleStartBtn);

        var organizeBtn = UiBuilder.GhostButton("Organize", MaterialIconKind.TuneVariant, () =>
            studio.OpenQuizInOrganizer(quiz)
        );
        actions.Children.Add(organizeBtn);

        bool isPdfQuiz = quiz.SourcePdfPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        if (!compact && File.Exists(quiz.SourcePdfPath))
        {
            var pdfBtn = UiBuilder.IconButton(
                isPdfQuiz ? MaterialIconKind.FilePdfBox : MaterialIconKind.CodeTags,
                () =>
                {
                    if (isPdfQuiz)
                        vm.OpenPdfInReader(quiz.SourcePdfPath);
                    else
                        vm.OpenFileInSourceViewer(quiz.SourcePdfPath);
                },
                14.5,
                ThemeService.Brush(ThemeService.TextSecondary)
            );
            actions.Children.Add(pdfBtn);
        }

        if (!compact)
        {
            async Task ExportCatalogPdfAsync(
                string suffix,
                string dialogTitle,
                QuizPdfExportOptions options
            )
            {
                var top = TopLevel.GetTopLevel(actions);
                if (top?.StorageProvider is null)
                    return;
                var file = await top.StorageProvider.SaveFilePickerAsync(
                    new Avalonia.Platform.Storage.FilePickerSaveOptions
                    {
                        Title = dialogTitle,
                        SuggestedFileName =
                            $"{ScholarFlow.Core.Services.FilenameSanitizer.SanitizeFileName(quiz.Title + suffix, ".pdf")}",
                        DefaultExtension = "pdf",
                    }
                );
                var outPath = file is null
                    ? null
                    : Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(file);
                if (!string.IsNullOrWhiteSpace(outPath))
                    await studio.ExportQuizToPdfFileAsync(quiz, outPath, options);
            }

            var printFlyout = new MenuFlyout();
            var mExam = new MenuItem { Header = "Printable Exam (Worksheet + Hints + Key)" };
            mExam.Click += async (_, _) =>
                await ExportCatalogPdfAsync(
                    "_Exam",
                    "Export Printable Exam PDF",
                    QuizPdfExportOptions.ExamDefault
                );
            var mGuide = new MenuItem { Header = "Study Guide (Inline Answers & Feedback)" };
            mGuide.Click += async (_, _) =>
                await ExportCatalogPdfAsync(
                    "_StudyGuide",
                    "Export Study Guide PDF",
                    QuizPdfExportOptions.StudyGuide
                );
            var mBlank = new MenuItem { Header = "Questions Only (Blank Exam Sheet)" };
            mBlank.Click += async (_, _) =>
                await ExportCatalogPdfAsync(
                    "_QuestionsOnly",
                    "Export Questions-Only PDF",
                    QuizPdfExportOptions.QuestionsOnly
                );
            printFlyout.Items.Add(mExam);
            printFlyout.Items.Add(mGuide);
            printFlyout.Items.Add(mBlank);

            Button? exportPdfBtn = null;
            exportPdfBtn = UiBuilder.IconButton(
                MaterialIconKind.PrinterOutline,
                () =>
                {
                    if (exportPdfBtn is not null)
                        printFlyout.ShowAt(exportPdfBtn);
                },
                14.5,
                ThemeService.Brush(ThemeService.TextSecondary)
            );
            ToolTip.SetTip(
                exportPdfBtn,
                "Export Printable PDF (Exam, Study Guide, or Questions Only)"
            );
            actions.Children.Add(exportPdfBtn);

            var exportJsonBtn = UiBuilder.IconButton(
                MaterialIconKind.CodeJson,
                async () =>
                {
                    var top = TopLevel.GetTopLevel(actions);
                    if (top?.StorageProvider is null)
                        return;
                    var file = await top.StorageProvider.SaveFilePickerAsync(
                        new Avalonia.Platform.Storage.FilePickerSaveOptions
                        {
                            Title = "Export Quiz Deck JSON",
                            SuggestedFileName =
                                $"{ScholarFlow.Core.Services.FilenameSanitizer.SanitizeFileName(quiz.Title, ".json")}",
                            DefaultExtension = "json",
                        }
                    );
                    var outPath = file is null
                        ? null
                        : Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(file);
                    if (!string.IsNullOrWhiteSpace(outPath))
                        await studio.ExportQuizToJsonFileAsync(quiz, outPath);
                },
                14.5,
                ThemeService.Brush(ThemeService.TextSecondary)
            );
            actions.Children.Add(exportJsonBtn);

            actions.Children.Add(
                UiBuilder.IconButton(
                    MaterialIconKind.TrashCanOutline,
                    async () => await studio.DeleteQuizAsync(quiz),
                    14.5,
                    ThemeService.Brush(ThemeService.TextMuted)
                )
            );
        }

        Grid.SetColumn(actions, 2);
        rowGrid.Children.Add(actions);

        var rowBorder = new Border
        {
            Background = isChecked
                ? ThemeService.Brush(ThemeService.BgElevated)
                : Brushes.Transparent,
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 7),
            Transitions = UiBuilder.CreateSmoothBrushTransitions(110),
            Child = rowGrid,
        };
        rowBorder.PointerEntered += (_, _) =>
        {
            if (!isChecked)
                rowBorder.Background = ThemeService.Brush(ThemeService.BgHover);
        };
        rowBorder.PointerExited += (_, _) =>
        {
            if (!isChecked)
                rowBorder.Background = Brushes.Transparent;
        };

        return rowBorder;
    }

    // =========================================================================
    // 1C. DECK ORGANIZER & QUESTION CURATION WORKBENCH (SINGLE & MULTI-DECK)
    // =========================================================================
}
