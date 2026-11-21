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

        if (studio.ActiveSession is not null)
        {
            return studio.IsShowingResultsSummary
                ? BuildSessionSummaryView(vm, studio)
                : BuildActiveQuestionRunnerView(vm, studio);
        }

        if (
            studio.ActiveSubTab == QuizStudioSubTab.Organizer
            && studio.IsDedicatedSnipperOpen
            && studio.EditingQuestionDraft is not null
        )
        {
            return BuildDedicatedPdfSnipperView(vm, studio);
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

        var titleLeft = new StackPanel { Spacing = 3 };
        var titleLine = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleLine.Children.Add(
            new TextBlock
            {
                Text = "Test & Active Recall",
                FontSize = 19,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 2),
            }
        );
        if (studio.SelectedCourse is not null)
        {
            var coursePill = UiBuilder.Pill(
                $"#{studio.SelectedCourse.EffectiveAlias} · {studio.SelectedCourse.DisplayTitle}",
                ThemeService.Brush(ThemeService.AccentPrimary),
                ThemeService.Brush(ThemeService.AccentSoftBg)
            );
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
        var builderScopeSubtitle = studio.IsMixedSourceMode
            ? $"Sources: {studio.SelectedFileSourcePaths.Count} file(s) + {pdfFileName} ({studio.ScopePreviewPageCount} PDF page(s) in scope)"
            : studio.IsFileSourceMode
                ? $"Sources: {studio.SelectedFileSourcePaths.Count} study file(s) selected"
                : $"Source: {pdfFileName} ({studio.ScopePreviewPageCount}/{studio.PdfTotalPages} page(s) in scope)";

        titleLeft.Children.Add(
            new TextBlock
            {
                Text = studio.ActiveSubTab == QuizStudioSubTab.Builder
                    ? builderScopeSubtitle
                    : $"{courseQuizCount} saved quiz(zes) in subject  ·  {studio.AllDueReviewQuestions.Count} due card(s) across basin",
                FontSize = 11.5,
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                TextTrimming = TextTrimming.CharacterEllipsis,
            }
        );

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
        dueBtn.Margin = new Thickness(0, 0, 6, 4);
        utilityWrap.Children.Add(dueBtn);

        var aiEngineBtn = UiBuilder.GhostButton(
            "AI Engine",
            MaterialIconKind.CogOutline,
            studio.ToggleAiSettings,
            isActive: studio.IsAiSettingsOpen
        );
        aiEngineBtn.Margin = new Thickness(0, 0, 0, 4);
        utilityWrap.Children.Add(aiEngineBtn);

        var subjectWrap = new WrapPanel { Orientation = Orientation.Horizontal };
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
            btn.Margin = new Thickness(0, 0, 5, 4);
            subjectWrap.Children.Add(btn);
        }

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
        buildTabBtn.Margin = new Thickness(0, 0, 5, 4);
        subTabWrap.Children.Add(buildTabBtn);

        var catalogTabBtn = UiBuilder.GhostButton(
            $"Quiz Catalog ({studio.SavedQuizzes.Count})",
            MaterialIconKind.LibraryShelves,
            () => studio.SetSubTab(QuizStudioSubTab.Catalog),
            isActive: studio.ActiveSubTab == QuizStudioSubTab.Catalog
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
        organizerTabBtn.Margin = new Thickness(0, 0, 0, 4);
        subTabWrap.Children.Add(organizerTabBtn);

        // Responsive header host: switches cleanly between 2-column (>= 820px) and stacked (< 820px)
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
            Detach(subjectWrap);
            Detach(subTabWrap);

            var hdrStack = new StackPanel { Spacing = 8 };
            if (compact)
            {
                hdrStack.Children.Add(titleLeft);
                hdrStack.Children.Add(utilityWrap);
                hdrStack.Children.Add(subTabWrap);
                if (subjectWrap.Children.Count > 0)
                    hdrStack.Children.Add(subjectWrap);
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
                row2.Children.Add(subjectWrap);
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
                ApplyHeaderLayout(e.NewSize.Width < 820);
        };

        topStack.Children.Add(responsiveHeaderHost);

        // Status / Feedback Line + Active Indeterminate Progress Indicator
        if (!string.IsNullOrWhiteSpace(studio.StatusMessage) || studio.IsBusy)
        {
            var statusRowGrid = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
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
                new TextBlock
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

        if (studio.IsAiSettingsOpen)
            topStack.Children.Add(BuildAiSettingsCard(vm, studio));

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
            host.Children.Add(BuildImageZoomLightbox(studio.ZoomedImagePath, studio.CloseImageZoom));
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
