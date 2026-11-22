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

        var newAssessBtn = UiBuilder.PrimaryButton(
            "New Assessment",
            MaterialIconKind.Plus,
            () => studio.SetSubTab(QuizStudioSubTab.Builder)
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

        Button? chk = null;
        if (showCheckbox)
        {
            chk = UiBuilder.IconButton(
                isChecked ? MaterialIconKind.CheckboxMarked : MaterialIconKind.CheckboxBlankOutline,
                () => studio.ToggleQuizSelectionForMerge(quiz.Id),
                16,
                isChecked
                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : ThemeService.Brush(ThemeService.TextMuted)
            );
            chk.VerticalAlignment = VerticalAlignment.Center;
            ToolTip.SetTip(
                chk,
                isChecked ? "Unselect from multi-deck combiner" : "Select for multi-deck combiner"
            );
        }

        bool isPdfStale = quiz.IsSourcePdfStale();

        var titleLine = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleLine.Children.Add(
            new TextBlock
            {
                Text = quiz.Title,
                FontSize = 13.5,
                FontWeight = FontWeight.Medium,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );

        if (isPdfStale && !compact)
        {
            var stalePill = UiBuilder.Pill(
                "PDF updated",
                ThemeService.Brush(ThemeService.PriorityP2Fg),
                ThemeService.Brush(ThemeService.PriorityP2Bg),
                fontSize: 10
            );
            stalePill.VerticalAlignment = VerticalAlignment.Center;
            ToolTip.SetTip(
                stalePill,
                "Source PDF changed since this quiz was generated — open ⋯ menu to Re-ground PDF"
            );
            titleLine.Children.Add(stalePill);
        }

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

        var centerStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                titleLine,
                new TextBlock
                {
                    Text = string.Join(" · ", subParts),
                    FontSize = 11.5,
                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };

        Border? scorePill = null;
        if (quiz.LastScorePercent.HasValue)
        {
            bool high = quiz.LastScorePercent.Value >= 75;
            scorePill = UiBuilder.Pill(
                $"{quiz.LastScorePercent.Value:0}%",
                high
                    ? ThemeService.Brush(ThemeService.SuccessFg)
                    : ThemeService.Brush(ThemeService.PriorityP2Fg),
                high
                    ? ThemeService.Brush(ThemeService.SuccessBg)
                    : ThemeService.Brush(ThemeService.PriorityP2Bg)
            );
            scorePill.VerticalAlignment = VerticalAlignment.Center;
        }

        // Wrapping primary actions + consolidated ⋯ overflow menu
        var actionsWrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (quiz.HasInProgressAttempt)
        {
            int resumeQ = Math.Clamp(
                (quiz.InProgressQuestionIndex ?? 0) + 1,
                1,
                quiz.Questions.Count
            );
            var contBtn = UiBuilder.PrimaryButton(
                $"Continue (Q{resumeQ}/{quiz.Questions.Count})",
                MaterialIconKind.PlayOutline,
                () => studio.StartQuizSession(quiz, resumeSavedProgress: true)
            );
            contBtn.Margin = new Thickness(0, 0, 5, 3);
            ToolTip.SetTip(contBtn, "Resume saved in-progress attempt");
            actionsWrap.Children.Add(contBtn);

            var restartBtn = UiBuilder.GhostButton(
                "Restart",
                MaterialIconKind.Restart,
                () => studio.StartQuizSession(quiz, resumeSavedProgress: false)
            );
            restartBtn.Margin = new Thickness(0, 0, 5, 3);
            ToolTip.SetTip(restartBtn, "Start fresh from question 1");
            actionsWrap.Children.Add(restartBtn);
        }
        else
        {
            var startBtn = UiBuilder.GhostButton(
                "Start",
                MaterialIconKind.PlayOutline,
                () => studio.StartQuizSession(quiz, resumeSavedProgress: false),
                isActive: true
            );
            startBtn.Margin = new Thickness(0, 0, 5, 3);
            ToolTip.SetTip(startBtn, "Start practice session");
            actionsWrap.Children.Add(startBtn);
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
        shuffleStartBtn.Margin = new Thickness(0, 0, 5, 3);
        ToolTip.SetTip(shuffleStartBtn, "Start with shuffled questions & answer choices");
        actionsWrap.Children.Add(shuffleStartBtn);

        var organizeBtn = UiBuilder.GhostButton(
            "Organize",
            MaterialIconKind.TuneVariant,
            () => studio.OpenQuizInOrganizer(quiz)
        );
        organizeBtn.Margin = new Thickness(0, 0, compact ? 0 : 4, 3);
        ToolTip.SetTip(organizeBtn, "Open deck in Deck Organizer to edit questions or run AI fix");
        actionsWrap.Children.Add(organizeBtn);

        if (!compact)
        {
            async Task ExportCatalogPdfAsync(
                string suffix,
                string dialogTitle,
                QuizPdfExportOptions options
            )
            {
                var top = TopLevel.GetTopLevel(actionsWrap);
                if (top?.StorageProvider is null)
                    return;
                var file = await top.StorageProvider.SaveFilePickerAsync(
                    new Avalonia.Platform.Storage.FilePickerSaveOptions
                    {
                        Title = dialogTitle,
                        SuggestedFileName =
                            $"{Core.Services.FilenameSanitizer.SanitizeFileName(quiz.Title + suffix, ".pdf")}",
                        DefaultExtension = "pdf",
                    }
                );
                var outPath = file is null
                    ? null
                    : Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(file);
                if (!string.IsNullOrWhiteSpace(outPath))
                    await studio.ExportQuizToPdfFileAsync(quiz, outPath, options);
            }

            var moreMenu = new MenuFlyout();

            bool isPdfQuiz = quiz.SourcePdfPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
            if (File.Exists(quiz.SourcePdfPath))
            {
                var openSourceItem = new MenuItem
                {
                    Header = isPdfQuiz ? "Open Source PDF in Reader" : "Open Source File",
                };
                openSourceItem.Click += (_, _) =>
                {
                    if (isPdfQuiz)
                        vm.OpenPdfInReader(quiz.SourcePdfPath);
                    else
                        vm.OpenFileInSourceViewer(quiz.SourcePdfPath);
                };
                moreMenu.Items.Add(openSourceItem);
            }

            if (isPdfStale)
            {
                var regroundItem = new MenuItem { Header = "Re-ground Stale PDF" };
                regroundItem.Click += async (_, _) => await studio.RegroundQuizAsync(quiz);
                moreMenu.Items.Add(regroundItem);
            }

            if (moreMenu.Items.Count > 0)
                moreMenu.Items.Add(new Separator());

            var mExam = new MenuItem { Header = "Export Printable Exam PDF (Worksheet + Key)" };
            mExam.Click += async (_, _) =>
                await ExportCatalogPdfAsync(
                    "_Exam",
                    "Export Printable Exam PDF",
                    QuizPdfExportOptions.ExamDefault
                );
            moreMenu.Items.Add(mExam);

            var mGuide = new MenuItem { Header = "Export Study Guide PDF (Inline Answers)" };
            mGuide.Click += async (_, _) =>
                await ExportCatalogPdfAsync(
                    "_StudyGuide",
                    "Export Study Guide PDF",
                    QuizPdfExportOptions.StudyGuide
                );
            moreMenu.Items.Add(mGuide);

            var mBlank = new MenuItem { Header = "Export Questions-Only PDF (Blank Sheet)" };
            mBlank.Click += async (_, _) =>
                await ExportCatalogPdfAsync(
                    "_QuestionsOnly",
                    "Export Questions-Only PDF",
                    QuizPdfExportOptions.QuestionsOnly
                );
            moreMenu.Items.Add(mBlank);

            var mJson = new MenuItem { Header = "Export Deck JSON" };
            mJson.Click += async (_, _) =>
            {
                var top = TopLevel.GetTopLevel(actionsWrap);
                if (top?.StorageProvider is null)
                    return;
                var file = await top.StorageProvider.SaveFilePickerAsync(
                    new Avalonia.Platform.Storage.FilePickerSaveOptions
                    {
                        Title = "Export Quiz Deck JSON",
                        SuggestedFileName =
                            $"{Core.Services.FilenameSanitizer.SanitizeFileName(quiz.Title, ".json")}",
                        DefaultExtension = "json",
                    }
                );
                var outPath = file is null
                    ? null
                    : Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(file);
                if (!string.IsNullOrWhiteSpace(outPath))
                    await studio.ExportQuizToJsonFileAsync(quiz, outPath);
            };
            moreMenu.Items.Add(mJson);

            moreMenu.Items.Add(new Separator());

            var mDelete = new MenuItem { Header = "Delete Quiz" };
            mDelete.Click += async (_, _) => await studio.DeleteQuizAsync(quiz);
            moreMenu.Items.Add(mDelete);

            var moreBtn = UiBuilder.IconButton(
                MaterialIconKind.DotsHorizontal,
                () => { },
                16,
                ThemeService.Brush(ThemeService.TextSecondary)
            );
            moreBtn.Flyout = moreMenu;
            moreBtn.Margin = new Thickness(0, 0, 0, 3);
            ToolTip.SetTip(moreBtn, "More options (PDF export, JSON, open source, delete)");
            actionsWrap.Children.Add(moreBtn);
        }

        // Responsive card layout host: 1-row on wide (>= 740px), clean 2-row stacked on narrow (< 740px)
        var layoutHost = new Border();
        bool? lastStacked = null;

        void ApplyRowLayout(bool stacked)
        {
            if (lastStacked == stacked && layoutHost.Child is not null)
                return;
            lastStacked = stacked;

            static void Detach(Control? c)
            {
                if (c is null)
                    return;
                if (c.Parent is Panel p)
                    p.Children.Remove(c);
                else if (c.Parent is Decorator d && ReferenceEquals(d.Child, c))
                    d.Child = null;
            }

            Detach(chk);
            Detach(centerStack);
            Detach(scorePill);
            Detach(actionsWrap);

            if (stacked)
            {
                var topLine = new Grid
                {
                    ColumnDefinitions = showCheckbox
                        ? ColumnDefinitions.Parse("Auto,*,Auto")
                        : ColumnDefinitions.Parse("0,*,Auto"),
                    ColumnSpacing = 8,
                };
                if (chk is not null)
                    topLine.Children.Add(chk);
                Grid.SetColumn(centerStack, 1);
                topLine.Children.Add(centerStack);
                if (scorePill is not null)
                {
                    Grid.SetColumn(scorePill, 2);
                    topLine.Children.Add(scorePill);
                }

                actionsWrap.Margin = new Thickness(showCheckbox ? 30 : 0, 6, 0, 0);
                layoutHost.Child = new StackPanel
                {
                    Spacing = 0,
                    Children = { topLine, actionsWrap },
                };
            }
            else
            {
                var rightGroup = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                if (scorePill is not null)
                    rightGroup.Children.Add(scorePill);
                actionsWrap.Margin = new Thickness(0, 3, 0, 0);
                rightGroup.Children.Add(actionsWrap);

                var rowGrid = new Grid
                {
                    ColumnDefinitions = showCheckbox
                        ? ColumnDefinitions.Parse("Auto,*,Auto")
                        : ColumnDefinitions.Parse("0,*,Auto"),
                    ColumnSpacing = 10,
                };
                if (chk is not null)
                    rowGrid.Children.Add(chk);
                Grid.SetColumn(centerStack, 1);
                rowGrid.Children.Add(centerStack);
                Grid.SetColumn(rightGroup, 2);
                rowGrid.Children.Add(rightGroup);
                layoutHost.Child = rowGrid;
            }
        }

        ApplyRowLayout(compact);

        var rowBorder = new Border
        {
            Background = isChecked
                ? ThemeService.Brush(ThemeService.BgElevated)
                : Brushes.Transparent,
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 8),
            Transitions = UiBuilder.CreateSmoothBrushTransitions(110),
            Child = layoutHost,
        };
        rowBorder.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width > 0)
                ApplyRowLayout(e.NewSize.Width < (compact ? 520 : 720));
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
