using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views;

/// <summary>
/// Cohesive pure-C# view builder for the Evidence-Grounded PDF Quiz &amp; Active Recall Studio (zero .axaml, zero emojis).
/// Matches the low-noise hairline aesthetic of TaskList, CourseWorkspace, and PdfReaderViewBuilder,
/// featuring a 2-column Split Builder + Live Visual PDF Scope Previewer and a hairline Assessment Catalog.
/// </summary>
public static partial class QuizStudioViewBuilder
{
    public static Control Build(MainWindowViewModel vm)
    {
        var studio = vm.QuizStudio;

        if (studio.IsDedicatedSnipperOpen && studio.EditingQuestionDraft is not null)
        {
            return BuildDedicatedPdfSnipperView(vm, studio);
        }

        if (studio.ActiveSession is not null)
        {
            var runnerView = studio.IsShowingResultsSummary
                ? BuildSessionSummaryView(vm, studio)
                : BuildActiveQuestionRunnerView(vm, studio);

            if (
                studio.EditingQuestionDraft is not null
                || !string.IsNullOrWhiteSpace(studio.FlaggingQuestionId)
            )
            {
                var host = new Grid();
                host.Children.Add(runnerView);
                if (studio.EditingQuestionDraft is not null)
                    host.Children.Add(BuildFloatingQuestionEditorOverlay(vm, studio));
                else if (!string.IsNullOrWhiteSpace(studio.FlaggingQuestionId))
                    host.Children.Add(BuildSingleQuestionAiFixOverlay(vm, studio));
                return host;
            }

            return runnerView;
        }

        if (
            studio.ActiveSubTab == QuizStudioSubTab.Builder
            && studio.IsPageGridOpen
            && studio.PdfTotalPages > 0
        )
        {
            return BuildDedicatedPageStudioView(vm, studio);
        }

        return BuildStudioWorkspaceView(vm, studio);
    }

    private static Grid BuildStudioWorkspaceView(MainWindowViewModel vm, QuizStudioViewModel studio)
    {
        var root = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,*"),
            Margin = new Thickness(18, 12, 18, 12),
            ClipToBounds = true,
        };

        // Top Fixed Header & Subject / Sub-Tab Strip (fully responsive, wraps cleanly on narrow windows)
        var topStack = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };

        var titleLeft = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var titleLine = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var titleHeading = new TextBlock
        {
            Text = "Test & Active Recall",
            FontSize = 18.5,
            FontWeight = FontWeight.Bold,
            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 2),
        };
        titleLine.Children.Add(titleHeading);

        Border? coursePill = null;
        if (studio.SelectedCourse is not null)
        {
            coursePill = UiBuilder.Pill(
                $"#{studio.SelectedCourse.EffectiveAlias} · {studio.SelectedCourse.DisplayTitle}",
                ThemeService.Brush(ThemeService.AccentPrimary),
                ThemeService.Brush(ThemeService.AccentSoftBg)
            );
            coursePill.MaxWidth = 280;
            coursePill.Margin = new Thickness(0, 0, 6, 2);
            titleLine.Children.Add(coursePill);
        }
        titleLeft.Children.Add(titleLine);

        var pdfFileName = string.IsNullOrWhiteSpace(studio.SelectedPdfPath)
            ? "No PDF found"
            : Path.GetFileName(studio.SelectedPdfPath);
        int courseQuizCount = studio.SelectedCourse is null
            ? studio.SavedQuizzes.Count
            : studio.SavedQuizzes.Count(q =>
                string.Equals(
                    q.CourseFolder,
                    studio.SelectedCourse.FolderName,
                    StringComparison.OrdinalIgnoreCase
                )
            );
        var builderScopeSubtitle =
            studio.IsMixedSourceMode
                ? $"Sources: {studio.SelectedFileSourcePaths.Count} file(s) + {pdfFileName} ({studio.ScopePreviewPageCount} PDF page(s) in scope)"
            : studio.IsFileSourceMode
                ? $"Sources: {studio.SelectedFileSourcePaths.Count} study file(s) selected"
            : $"Source: {pdfFileName} ({studio.ScopePreviewPageCount}/{studio.PdfTotalPages} page(s) in scope)";

        var subtitleBlock = new TextBlock
        {
            Text =
                studio.ActiveSubTab == QuizStudioSubTab.Builder
                    ? builderScopeSubtitle
                    : $"{courseQuizCount} saved quiz(zes) in subject  ·  {studio.AllDueReviewQuestions.Count} due card(s) across basin",
            FontSize = 11.5,
            Foreground = ThemeService.Brush(ThemeService.TextSecondary),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        titleLeft.Children.Add(subtitleBlock);

        var utilityWrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        int dueCount = studio.AllDueReviewQuestions.Count;
        var dueBtn = UiBuilder.GhostButton(
            dueCount > 0 ? $"Practice Due ({dueCount})" : "Practice Deck",
            MaterialIconKind.CardsOutline,
            () => studio.StartDueSpacedRepetitionDeck(studio.SelectedCourse?.FolderName),
            isActive: dueCount > 0
        );
        ToolTip.SetTip(
            dueBtn,
            "Start a spaced-repetition review session for due or previously missed cards"
        );
        dueBtn.Margin = new Thickness(0, 0, 6, 4);
        utilityWrap.Children.Add(dueBtn);

        var aiEngineBtn = BuildAiQuickSwitchButton(vm, studio);
        aiEngineBtn.Margin = new Thickness(0, 0, 0, 4);
        utilityWrap.Children.Add(aiEngineBtn);

        var subjectStrip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var c in vm.Courses.Where(x => !x.IsArchived))
        {
            var captured = c;
            bool isSelected = studio.SelectedCourse?.FolderName == c.FolderName;
            var btn = UiBuilder.GhostButton(
                $"#{c.EffectiveAlias}",
                isSelected ? MaterialIconKind.Check : null,
                () => studio.SelectCourseScope(captured),
                isActive: isSelected
            );
            ToolTip.SetTip(btn, c.DisplayTitle);
            subjectStrip.Children.Add(btn);
        }
        var subjectScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = subjectStrip,
        };

        var subTabWrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var buildTabBtn = UiBuilder.GhostButton(
            "Build",
            MaterialIconKind.CreationOutline,
            () => studio.SetSubTab(QuizStudioSubTab.Builder),
            isActive: studio.ActiveSubTab == QuizStudioSubTab.Builder
        );
        ToolTip.SetTip(buildTabBtn, "Scope study sources and generate new evidence-grounded quizzes");
        buildTabBtn.Margin = new Thickness(0, 0, 5, 4);
        subTabWrap.Children.Add(buildTabBtn);

        var catalogTabBtn = UiBuilder.GhostButton(
            $"Quiz Catalog ({studio.SavedQuizzes.Count})",
            MaterialIconKind.LibraryShelves,
            () => studio.SetSubTab(QuizStudioSubTab.Catalog),
            isActive: studio.ActiveSubTab == QuizStudioSubTab.Catalog
        );
        ToolTip.SetTip(
            catalogTabBtn,
            "Browse saved quizzes, launch practice runs, merge decks, or export printable exam PDFs"
        );
        catalogTabBtn.Margin = new Thickness(0, 0, 5, 4);
        subTabWrap.Children.Add(catalogTabBtn);

        var organizerTabBtn = UiBuilder.GhostButton(
            studio.OrganizerQuiz is not null
            && (
                studio.SelectedCourse is null
                || string.Equals(
                    studio.OrganizerQuiz.CourseFolder,
                    studio.SelectedCourse.FolderName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                ? $"Deck Organizer ({studio.OrganizerQuiz.Questions.Count})"
                : "Deck Organizer",
            MaterialIconKind.TuneVariant,
            () =>
            {
                bool matchesCourse =
                    studio.OrganizerQuiz is not null
                    && (
                        studio.SelectedCourse is null
                        || string.Equals(
                            studio.OrganizerQuiz.CourseFolder,
                            studio.SelectedCourse.FolderName,
                            StringComparison.OrdinalIgnoreCase
                        )
                    );
                if (!matchesCourse)
                {
                    var courseQuiz = studio.SavedQuizzes.FirstOrDefault(q =>
                        studio.SelectedCourse is null
                        || string.Equals(
                            q.CourseFolder,
                            studio.SelectedCourse.FolderName,
                            StringComparison.OrdinalIgnoreCase
                        )
                    );
                    if (courseQuiz is not null)
                    {
                        studio.OpenQuizInOrganizer(courseQuiz);
                        return;
                    }
                }
                studio.SetSubTab(QuizStudioSubTab.Organizer);
            },
            isActive: studio.ActiveSubTab == QuizStudioSubTab.Organizer
        );
        ToolTip.SetTip(
            organizerTabBtn,
            "Edit questions, audit quality issues, crop PDF figures, and pin highlight evidence"
        );
        organizerTabBtn.Margin = new Thickness(0, 0, 0, 4);
        subTabWrap.Children.Add(organizerTabBtn);

        // Responsive header host: switches cleanly between wide (>= 960px) and compact (< 960px)
        var responsiveHeaderHost = new Border();
        bool? lastCompactHeader = null;

        void ApplyHeaderLayout(bool compact)
        {
            if (lastCompactHeader == compact && responsiveHeaderHost.Child is not null)
                return;
            lastCompactHeader = compact;

            static void Detach(Control c)
            {
                if (c.Parent is Panel p)
                    p.Children.Remove(c);
                else if (c.Parent is Decorator d && ReferenceEquals(d.Child, c))
                    d.Child = null;
            }

            Detach(titleLeft);
            Detach(utilityWrap);
            Detach(subjectScroller);
            Detach(subTabWrap);

            titleHeading.FontSize = compact ? 16.5 : 18.5;
            subtitleBlock.IsVisible = !compact;
            if (coursePill is not null)
                coursePill.IsVisible = !compact;

            var hdrStack = new StackPanel { Spacing = compact ? 6 : 8 };
            if (compact)
            {
                // Compact Row 1: Title + Utility buttons side-by-side (never 4 bloated stacked rows)
                var compactTop = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                    ColumnSpacing = 8,
                };
                compactTop.Children.Add(titleLeft);
                Grid.SetColumn(utilityWrap, 1);
                compactTop.Children.Add(utilityWrap);
                hdrStack.Children.Add(compactTop);

                // Compact Row 2: Sub-tabs (Build / Catalog / Organizer)
                hdrStack.Children.Add(subTabWrap);

                // Compact Row 3: Single-line horizontally scrollable course chips (if >1 course)
                if (subjectStrip.Children.Count > 1)
                    hdrStack.Children.Add(subjectScroller);
            }
            else
            {
                var row1 = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                    ColumnSpacing = 12,
                };
                row1.Children.Add(titleLeft);
                Grid.SetColumn(utilityWrap, 1);
                row1.Children.Add(utilityWrap);
                hdrStack.Children.Add(row1);

                var row2 = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                    ColumnSpacing = 12,
                };
                row2.Children.Add(subjectScroller);
                Grid.SetColumn(subTabWrap, 1);
                row2.Children.Add(subTabWrap);
                hdrStack.Children.Add(row2);
            }

            responsiveHeaderHost.Child = hdrStack;
        }

        ApplyHeaderLayout(false);
        root.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width > 0)
                ApplyHeaderLayout(e.NewSize.Width < 960);
        };

        topStack.Children.Add(responsiveHeaderHost);

        // Status / Feedback Line + Active Indeterminate Progress Indicator
        if (!string.IsNullOrWhiteSpace(studio.StatusMessage) || studio.IsBusy)
        {
            var statusRowGrid = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
                ColumnSpacing = 8,
            };
            statusRowGrid.Children.Add(
                UiBuilder.Icon(
                    studio.IsStatusError ? MaterialIconKind.AlertCircleOutline
                        : studio.IsBusy ? MaterialIconKind.Autorenew
                        : MaterialIconKind.CheckCircleOutline,
                    14,
                    studio.IsStatusError
                        ? ThemeService.Brush(ThemeService.PriorityP1Fg)
                        : ThemeService.Brush(ThemeService.AccentPrimary)
                )
            );
            statusRowGrid.Children.Add(
                new SelectableTextBlock
                {
                    Text = studio.StatusMessage,
                    FontSize = 12,
                    Foreground = studio.IsStatusError
                        ? ThemeService.Brush(ThemeService.PriorityP1Fg)
                        : ThemeService.Brush(ThemeService.TextSecondary),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    [Grid.ColumnProperty] = 1,
                }
            );

            if (studio.IsStatusError)
            {
                var errorActions = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center,
                    [Grid.ColumnProperty] = 2,
                };

                var copyErrText = studio.LastErrorCopyText ?? studio.StatusMessage;
                Button? copyErrBtn = null;
                copyErrBtn = UiBuilder.GhostButton(
                    "Copy Error",
                    MaterialIconKind.ContentCopy,
                    async () =>
                    {
                        var top = TopLevel.GetTopLevel(copyErrBtn);
                        if (top?.Clipboard is not null)
                            await top.Clipboard.SetTextAsync(copyErrText);
                        if (copyErrBtn?.Content is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock tb)
                        {
                            tb.Text = "Copied";
                            await Task.Delay(1500);
                            tb.Text = "Copy Error";
                        }
                    }
                );
                ToolTip.SetTip(copyErrBtn, "Copy full error message & broken JSON excerpt to clipboard");
                errorActions.Children.Add(copyErrBtn);

                if (!string.IsNullOrWhiteSpace(studio.LastErrorFixPrompt))
                {
                    var fixPromptText = studio.LastErrorFixPrompt;
                    Button? copyFixBtn = null;
                    copyFixBtn = UiBuilder.GhostButton(
                        "Copy Fix Prompt",
                        MaterialIconKind.AutoFix,
                        async () =>
                        {
                            var top = TopLevel.GetTopLevel(copyFixBtn);
                            if (top?.Clipboard is not null)
                                await top.Clipboard.SetTextAsync(fixPromptText);
                            if (copyFixBtn?.Content is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock tb)
                            {
                                tb.Text = "Copied!";
                                await Task.Delay(1500);
                                tb.Text = "Copy Fix Prompt";
                            }
                        }
                    );
                    ToolTip.SetTip(
                        copyFixBtn,
                        "Copy a compact, low-token repair prompt quoting ONLY the broken JSON excerpt (avoids regenerating the full quiz)"
                    );
                    errorActions.Children.Add(copyFixBtn);
                }

                statusRowGrid.Children.Add(errorActions);
            }

            var statusContainer = new StackPanel { Spacing = 6 };
            statusContainer.Children.Add(statusRowGrid);
            if (studio.IsBusy)
            {
                statusContainer.Children.Add(
                    new ProgressBar
                    {
                        IsIndeterminate = true,
                        Height = 3,
                        Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
                        Background = ThemeService.Brush(ThemeService.BgElevated),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                    }
                );
            }

            topStack.Children.Add(
                new Border
                {
                    Background = studio.IsStatusError
                        ? ThemeService.Brush(ThemeService.PriorityP1Bg)
                        : ThemeService.Brush(ThemeService.BgSurface),
                    BorderBrush =
                        studio.IsStatusError ? ThemeService.Brush(ThemeService.PriorityP1Fg)
                        : studio.IsBusy ? ThemeService.Brush(ThemeService.AccentPrimary)
                        : ThemeService.Brush(ThemeService.BorderSubtle),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 6),
                    Child = statusContainer,
                }
            );
        }

        if (studio.IsManualJsonModalOpen)
            topStack.Children.Add(BuildManualJsonBridgeCard(studio));

        topStack.Children.Add(UiBuilder.HairlineDivider());
        root.Children.Add(topStack);

        // Body Content (2-Column Builder + Live PDF Page Preview, Hairline Catalog, Deck Organizer, or Dedicated PDF Snipper)
        Control bodyContent = studio.ActiveSubTab switch
        {
            QuizStudioSubTab.Catalog => BuildCatalogView(vm, studio),
            QuizStudioSubTab.Organizer
                when studio.IsDedicatedSnipperOpen && studio.EditingQuestionDraft is not null =>
                BuildDedicatedPdfSnipperView(vm, studio),
            QuizStudioSubTab.Organizer => BuildDeckOrganizerView(vm, studio),
            _ => BuildSplitBuilderAndPreviewView(vm, studio),
        };

        Grid.SetRow(bodyContent, 1);
        root.Children.Add(bodyContent);

        if (
            !string.IsNullOrWhiteSpace(studio.ZoomedImagePath)
            && File.Exists(studio.ZoomedImagePath)
        )
        {
            var host = new Grid();
            host.Children.Add(root);
            host.Children.Add(
                BuildImageZoomLightbox(studio.ZoomedImagePath, studio.CloseImageZoom)
            );
            return host;
        }

        if (studio.IsAiSettingsOpen)
        {
            var host = new Grid();
            host.Children.Add(root);
            host.Children.Add(BuildFloatingAiSettingsOverlay(vm, studio));
            return host;
        }

        if (
            studio.ActiveSubTab == QuizStudioSubTab.Organizer
            && !studio.IsDedicatedSnipperOpen
            && (
                studio.EditingQuestionDraft is not null
                || studio.IsDeckAiFixModalOpen
                || !string.IsNullOrWhiteSpace(studio.FlaggingQuestionId)
            )
        )
        {
            var host = new Grid();
            host.Children.Add(root);
            if (studio.EditingQuestionDraft is not null)
                host.Children.Add(BuildFloatingQuestionEditorOverlay(vm, studio));
            else if (studio.IsDeckAiFixModalOpen)
                host.Children.Add(BuildDeckComprehensiveAiFixOverlay(vm, studio));
            else if (!string.IsNullOrWhiteSpace(studio.FlaggingQuestionId))
                host.Children.Add(BuildSingleQuestionAiFixOverlay(vm, studio));
            return host;
        }

        return root;
    }
}
