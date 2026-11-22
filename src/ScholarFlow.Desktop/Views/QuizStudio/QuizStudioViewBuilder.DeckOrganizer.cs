using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Material.Icons;
using ScholarFlow.Core.Quiz;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views;

public static partial class QuizStudioViewBuilder
{
    private static Control? _cachedOrganizerRoot;
    private static string? _cachedOrganizerSignature;
    private static QuizStudioViewModel? _selectionHookStudio;
    private static Action<string?, bool, bool>? _selectionHookHandler;

    private static string ComputeOrganizerStructuralSignature(
        MainWindowViewModel vm,
        QuizStudioViewModel studio
    )
    {
        var deck = studio.OrganizerQuiz;
        if (deck is null)
            return "empty";

        int qHash = 17;
        foreach (var q in deck.Questions)
        {
            qHash = unchecked(
                qHash * 31
                + (q.Id?.GetHashCode(StringComparison.Ordinal) ?? 0)
                + (q.Prompt?.GetHashCode(StringComparison.Ordinal) ?? 0)
                + (q.CorrectAnswer?.GetHashCode(StringComparison.Ordinal) ?? 0)
                + (q.Diagram?.GetHashCode(StringComparison.Ordinal) ?? 0)
                + (q.ImagePath?.GetHashCode(StringComparison.Ordinal) ?? 0)
                + q.HighlightRects.Count
            );
        }

        return string.Join(
            "|",
            ThemeService.CurrentTheme.ToString(),
            vm.BasinRoot,
            studio.SelectedCourse?.FolderName ?? "",
            deck.Id,
            deck.Title,
            deck.Term ?? "",
            deck.Questions.Count,
            qHash,
            studio.OrganizerKindFilter?.ToString() ?? "",
            studio.OrganizerTopicFilter ?? "",
            studio.OrganizerHealthFilter.ToString(),
            studio.OrganizerSearchQuery,
            studio.IsOrganizerTocOpen,
            studio.IsOrganizerFiltersOpen,
            studio.IsOrganizerDirty,
            studio.DeckAiFixSelectedIds.Count
        );
    }

    private static Control BuildDeckOrganizerView(
        MainWindowViewModel vm,
        QuizStudioViewModel studio
    )
    {
        var sig = ComputeOrganizerStructuralSignature(vm, studio);
        if (
            _cachedOrganizerRoot is not null
            && string.Equals(_cachedOrganizerSignature, sig, StringComparison.Ordinal)
        )
        {
            if (_cachedOrganizerRoot.Parent is Panel oldParent)
                oldParent.Children.Remove(_cachedOrganizerRoot);
            else if (_cachedOrganizerRoot.Parent is Decorator oldDec)
                oldDec.Child = null;
            return _cachedOrganizerRoot;
        }

        var built = BuildDeckOrganizerViewCore(vm, studio);
        _cachedOrganizerRoot = built;
        _cachedOrganizerSignature = sig;
        return built;
    }

    private static Control BuildDeckOrganizerViewCore(
        MainWindowViewModel vm,
        QuizStudioViewModel studio
    )
    {
        var deck = studio.OrganizerQuiz;
        bool deckMatchesCourse =
            deck is not null
            && (
                studio.SelectedCourse is null
                || string.Equals(
                    deck.CourseFolder,
                    studio.SelectedCourse.FolderName,
                    StringComparison.OrdinalIgnoreCase
                )
            );

        if (deck is null || !deckMatchesCourse)
        {
            var courseQuizzes = studio
                .SavedQuizzes.Where(q =>
                    studio.SelectedCourse is null
                    || string.Equals(
                        q.CourseFolder,
                        studio.SelectedCourse.FolderName,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                .ToList();

            var emptyStack = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = studio.SelectedCourse is null
                            ? "No quiz deck is currently loaded in the Organizer."
                            : $"No deck loaded for {studio.SelectedCourse.Title}.",
                        FontSize = 14,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    },
                    new TextBlock
                    {
                        Text =
                            courseQuizzes.Count > 0
                                ? "Pick a saved quiz from this subject below, or open the Quiz Catalog:"
                                : "Generate a quiz in Builder or open the Quiz Catalog to curate decks.",
                        FontSize = 12,
                        Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    },
                },
            };

            if (courseQuizzes.Count > 0)
            {
                var pickWrap = new WrapPanel { Orientation = Orientation.Horizontal };
                foreach (var cq in courseQuizzes.Take(8))
                {
                    var captured = cq;
                    var qBtn = UiBuilder.GhostButton(
                        $"{captured.Title} ({captured.Questions.Count})",
                        MaterialIconKind.TuneVariant,
                        () => studio.OpenQuizInOrganizer(captured)
                    );
                    qBtn.Margin = new Thickness(0, 0, 6, 6);
                    pickWrap.Children.Add(qBtn);
                }
                emptyStack.Children.Add(pickWrap);
            }

            emptyStack.Children.Add(
                UiBuilder.GhostButton(
                    "Open Quiz Catalog",
                    MaterialIconKind.LibraryShelves,
                    () => studio.SetSubTab(QuizStudioSubTab.Catalog),
                    isActive: true
                )
            );

            return UiBuilder.PersistentScrollViewer(
                "quiz-organizer:empty",
                UiBuilder.Card(emptyStack)
            );
        }

        var rootGrid = new Grid { RowDefinitions = RowDefinitions.Parse("Auto,*"), RowSpacing = 8 };

        // 1. Compact Header + Collapsible Filter Strip
        var metaCard = new StackPanel { Spacing = 8 };
        var topRow = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 6,
        };

        var titleBox = new TextBox
        {
            Text = deck.Title,
            Watermark = "Deck Title...",
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            MinWidth = 140,
        };
        titleBox.PropertyChanged += (_, e) =>
        {
            if (
                e.Property == TextBox.TextProperty
                && !string.IsNullOrWhiteSpace(titleBox.Text)
                && !string.Equals(titleBox.Text.Trim(), deck.Title, StringComparison.Ordinal)
            )
            {
                deck.Title = titleBox.Text.Trim();
                studio.MarkOrganizerDirty();
            }
        };
        topRow.Children.Add(titleBox);

        int col = 1;
        if (studio.IsOrganizerDirty)
        {
            var dirtyPill = UiBuilder.Pill(
                "● Unsaved",
                ThemeService.Brush(ThemeService.PriorityP2Fg),
                ThemeService.Brush(ThemeService.PriorityP2Bg),
                fontSize: 11
            );
            dirtyPill.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(dirtyPill, col);
            topRow.Children.Add(dirtyPill);
        }
        col++;

        var tocToggleBtn = UiBuilder.GhostButton(
            "TOC",
            MaterialIconKind.FormatListBulleted,
            studio.ToggleOrganizerToc,
            isActive: studio.IsOrganizerTocOpen
        );
        Grid.SetColumn(tocToggleBtn, col++);
        topRow.Children.Add(tocToggleBtn);

        bool hasActiveFilters =
            studio.OrganizerKindFilter.HasValue
            || !string.IsNullOrWhiteSpace(studio.OrganizerTopicFilter)
            || studio.OrganizerHealthFilter != OrganizerHealthFilter.All
            || !string.IsNullOrWhiteSpace(studio.OrganizerSearchQuery);

        var filterToggleBtn = UiBuilder.GhostButton(
            hasActiveFilters ? "Filters ●" : "Filters",
            MaterialIconKind.FilterOutline,
            studio.ToggleOrganizerFilters,
            isActive: studio.IsOrganizerFiltersOpen || hasActiveFilters
        );
        Grid.SetColumn(filterToggleBtn, col++);
        topRow.Children.Add(filterToggleBtn);

        var shufflePlayBtn = UiBuilder.GhostButton(
            "Play",
            MaterialIconKind.PlayOutline,
            () =>
                studio.StartQuizSession(
                    deck,
                    shuffleQuestionsOverride: true,
                    shuffleOptionsOverride: true
                )
        );
        Grid.SetColumn(shufflePlayBtn, col++);
        topRow.Children.Add(shufflePlayBtn);

        // Properly Styled More ▾ Button
        var moreNormalBg = ThemeService.Brush(ThemeService.BgElevated);
        var moreHoverBg = ThemeService.Brush(ThemeService.BgHover);
        var moreBtn = new Button
        {
            Background = moreNormalBg,
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    UiBuilder.Icon(
                        MaterialIconKind.DotsHorizontal,
                        13.5,
                        ThemeService.Brush(ThemeService.TextSecondary)
                    ),
                    new TextBlock
                    {
                        Text = "More",
                        FontSize = 12,
                        FontWeight = FontWeight.Medium,
                        Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    UiBuilder.Icon(
                        MaterialIconKind.ChevronDown,
                        13.5,
                        ThemeService.Brush(ThemeService.TextMuted)
                    ),
                },
            },
        };
        moreBtn.PointerEntered += (_, _) => moreBtn.Background = moreHoverBg;
        moreBtn.PointerExited += (_, _) => moreBtn.Background = moreNormalBg;
        var moreFlyout = new MenuFlyout();

        var aiDeckFixItem = new MenuItem { Header = "Comprehensive AI Deck Fix..." };
        aiDeckFixItem.Click += (_, _) => studio.OpenDeckAiFixModal();
        moreFlyout.Items.Add(aiDeckFixItem);

        var appendAiItem = new MenuItem { Header = "Generate & Append with AI..." };
        appendAiItem.Click += (_, _) => studio.StartAppendToQuiz(deck);
        moreFlyout.Items.Add(appendAiItem);

        moreFlyout.Items.Add(new Separator());

        async Task SaveDeckPdfWithOptionsAsync(
            string suffix,
            string dialogTitle,
            QuizPdfExportOptions options
        )
        {
            var top = TopLevel.GetTopLevel(moreBtn);
            if (top?.StorageProvider is null)
                return;
            var file = await top.StorageProvider.SaveFilePickerAsync(
                new Avalonia.Platform.Storage.FilePickerSaveOptions
                {
                    Title = dialogTitle,
                    SuggestedFileName =
                        $"{Core.Services.FilenameSanitizer.SanitizeFileName(deck.Title + suffix, ".pdf")}",
                    DefaultExtension = "pdf",
                }
            );
            var outPath = file is null
                ? null
                : Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(file);
            if (!string.IsNullOrWhiteSpace(outPath))
                await studio.ExportQuizToPdfFileAsync(deck, outPath, options);
        }

        var exportPdfItem = new MenuItem
        {
            Header = "Export Printable Exam PDF (Worksheet + Hints + Key)",
        };
        exportPdfItem.Click += async (_, _) =>
            await SaveDeckPdfWithOptionsAsync(
                "_Exam",
                "Export Printable Exam PDF",
                QuizPdfExportOptions.ExamDefault
            );
        moreFlyout.Items.Add(exportPdfItem);

        var exportStudyGuideItem = new MenuItem
        {
            Header = "Export Study Guide PDF (Inline Answers & Feedback)",
        };
        exportStudyGuideItem.Click += async (_, _) =>
            await SaveDeckPdfWithOptionsAsync(
                "_StudyGuide",
                "Export Study Guide PDF",
                QuizPdfExportOptions.StudyGuide
            );
        moreFlyout.Items.Add(exportStudyGuideItem);

        var exportBlankExamItem = new MenuItem
        {
            Header = "Export Questions-Only PDF (Blank Exam Sheet)",
        };
        exportBlankExamItem.Click += async (_, _) =>
            await SaveDeckPdfWithOptionsAsync(
                "_QuestionsOnly",
                "Export Questions-Only PDF",
                QuizPdfExportOptions.QuestionsOnly
            );
        moreFlyout.Items.Add(exportBlankExamItem);

        var exportJsonItem = new MenuItem { Header = "Export Deck JSON" };
        exportJsonItem.Click += async (_, _) =>
        {
            var top = TopLevel.GetTopLevel(moreBtn);
            if (top?.StorageProvider is null)
                return;
            var file = await top.StorageProvider.SaveFilePickerAsync(
                new Avalonia.Platform.Storage.FilePickerSaveOptions
                {
                    Title = "Export Quiz Deck JSON",
                    SuggestedFileName =
                        $"{Core.Services.FilenameSanitizer.SanitizeFileName(deck.Title, ".json")}",
                    DefaultExtension = "json",
                }
            );
            var outPath = file is null
                ? null
                : Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(file);
            if (!string.IsNullOrWhiteSpace(outPath))
                await studio.ExportQuizToJsonFileAsync(deck, outPath);
        };
        moreFlyout.Items.Add(exportJsonItem);

        var hasFallbackMaster =
            !string.IsNullOrWhiteSpace(vm.BasinRoot)
            && !string.IsNullOrWhiteSpace(deck.CourseFolder)
            && Core.Pdf.MasterPdfService.FindExistingMasterPdfPath(vm.BasinRoot, deck.CourseFolder)
                is not null;

        bool isFileDeck =
            !string.IsNullOrWhiteSpace(deck.SourcePdfPath)
            && !deck.SourcePdfPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        if (!isFileDeck && (File.Exists(deck.SourcePdfPath) || hasFallbackMaster))
        {
            var regroundItem = new MenuItem
            {
                Header =
                    !File.Exists(deck.SourcePdfPath) ? "Re-ground via Master PDF"
                    : deck.IsSourcePdfStale() ? "Re-ground Stale PDF"
                    : "Re-ground PDF",
            };
            regroundItem.Click += async (_, _) => await studio.RegroundQuizAsync(deck);
            moreFlyout.Items.Add(regroundItem);
        }

        var relinkPdfItem = new MenuItem { Header = "Relink Source PDF..." };
        relinkPdfItem.Click += async (_, _) =>
        {
            var top = TopLevel.GetTopLevel(moreBtn);
            if (top?.StorageProvider is null)
                return;

            var courseDir =
                !string.IsNullOrWhiteSpace(vm.BasinRoot)
                && !string.IsNullOrWhiteSpace(deck.CourseFolder)
                    ? Path.Combine(vm.BasinRoot, deck.CourseFolder)
                    : null;
            var startFolder =
                courseDir is not null && Directory.Exists(courseDir)
                    ? await top.StorageProvider.TryGetFolderFromPathAsync(new Uri(courseDir))
                    : null;

            var picked = await top.StorageProvider.OpenFilePickerAsync(
                new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = $"Relink Source PDF for '{deck.Title}'",
                    AllowMultiple = false,
                    SuggestedStartLocation = startFolder,
                    FileTypeFilter =
                    [
                        new Avalonia.Platform.Storage.FilePickerFileType("PDF Documents")
                        {
                            Patterns = ["*.pdf"],
                        },
                    ],
                }
            );
            var localPath =
                picked.Count > 0
                    ? Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(picked[0])
                    : null;
            if (!string.IsNullOrWhiteSpace(localPath))
                await studio.RelinkAndRegroundQuizAsync(deck, localPath);
        };
        moreFlyout.Items.Add(relinkPdfItem);

        moreFlyout.Items.Add(new Separator());

        var shuffleOrderItem = new MenuItem
        {
            Header = studio.OrganizerKindFilter.HasValue
                ? $"Shuffle {studio.OrganizerKindFilter.Value} Only"
                : "Shuffle All Order",
        };
        shuffleOrderItem.Click += (_, _) =>
            studio.ShuffleOrganizerQuestions(studio.OrganizerKindFilter);
        moreFlyout.Items.Add(shuffleOrderItem);

        var groupKindItem = new MenuItem { Header = "Group by Kind" };
        groupKindItem.Click += (_, _) => studio.SortOrganizerQuestionsByKind();
        moreFlyout.Items.Add(groupKindItem);

        var sortPageItem = new MenuItem { Header = "Sort by PDF Page" };
        sortPageItem.Click += (_, _) => studio.SortOrganizerQuestionsByPage();
        moreFlyout.Items.Add(sortPageItem);

        if (vm.ActiveTerms.Count > 0 || !string.IsNullOrWhiteSpace(deck.Term))
        {
            moreFlyout.Items.Add(new Separator());
            var termMenu = new MenuItem { Header = $"Term ({deck.Term ?? "None"})" };
            foreach (var term in vm.ActiveTerms)
            {
                var capturedTerm = term;
                bool isCurrTerm = string.Equals(
                    deck.Term,
                    term,
                    StringComparison.OrdinalIgnoreCase
                );
                var termSub = new MenuItem { Header = isCurrTerm ? $"✓ {term}" : term };
                termSub.Click += (_, _) =>
                {
                    deck.Term = isCurrTerm ? null : capturedTerm;
                    studio.MarkOrganizerDirty();
                    vm.RequestUiRefresh();
                };
                termMenu.Items.Add(termSub);
            }
            if (!string.IsNullOrWhiteSpace(deck.Term))
            {
                var clearTermSub = new MenuItem { Header = "Clear Term" };
                clearTermSub.Click += (_, _) =>
                {
                    deck.Term = null;
                    studio.MarkOrganizerDirty();
                    vm.RequestUiRefresh();
                };
                termMenu.Items.Add(clearTermSub);
            }
            moreFlyout.Items.Add(termMenu);
        }

        moreBtn.Flyout = moreFlyout;
        Grid.SetColumn(moreBtn, col++);
        topRow.Children.Add(moreBtn);

        var saveBtn = UiBuilder.PrimaryButton(
            studio.IsOrganizerDirty
                ? $"Save ({deck.Questions.Count})"
                : $"Saved ({deck.Questions.Count})",
            MaterialIconKind.ContentSaveOutline,
            async () => await studio.SaveOrganizerDeckAsync()
        );
        Grid.SetColumn(saveBtn, col);
        topRow.Children.Add(saveBtn);
        metaCard.Children.Add(topRow);

        // Collapsible Multi-Facet Filter Bar (Kind + @TopicTag + Health)
        if (studio.IsOrganizerFiltersOpen)
        {
            var filtersStack = new StackPanel { Spacing = 6 };

            // Kind Pills
            var kindWrap = new WrapPanel { Orientation = Orientation.Horizontal };
            var allKindBtn = UiBuilder.GhostButton(
                $"All Kinds ({deck.Questions.Count})",
                studio.OrganizerKindFilter is null ? MaterialIconKind.Check : null,
                () => studio.SetOrganizerKindFilter(null),
                isActive: studio.OrganizerKindFilter is null
            );
            allKindBtn.Margin = new Thickness(0, 0, 4, 4);
            kindWrap.Children.Add(allKindBtn);

            foreach (var kind in Enum.GetValues<QuizQuestionKind>())
            {
                var capturedKind = kind;
                int cnt = deck.Questions.Count(q => q.Kind == kind);
                if (cnt == 0 && studio.OrganizerKindFilter != kind)
                    continue;
                bool isFilterActive = studio.OrganizerKindFilter == kind;
                var kBtn = UiBuilder.GhostButton(
                    $"{kind} ({cnt})",
                    isFilterActive ? MaterialIconKind.Check : null,
                    () => studio.SetOrganizerKindFilter(capturedKind),
                    isActive: isFilterActive
                );
                kBtn.Margin = new Thickness(0, 0, 4, 4);
                kindWrap.Children.Add(kBtn);
            }
            filtersStack.Children.Add(kindWrap);

            // Health & @TopicTag Pills
            var deckQualityReport = studio.GetCachedOrganizerQualityReport();
            var tagHealthWrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (
                var (hf, hLabel) in new[]
                {
                    (
                        OrganizerHealthFilter.QualityIssues,
                        deckQualityReport.HasIssues
                            ? $"Quality Issues ({deckQualityReport.FlaggedQuestionIds.Count})"
                            : "Quality Issues (0)"
                    ),
                    (OrganizerHealthFilter.MissingEvidence, "Missing Evidence"),
                    (OrganizerHealthFilter.HasRichMedia, "Code / Math / Diagram"),
                    (OrganizerHealthFilter.FlaggedForAi, "Flagged for AI"),
                }
            )
            {
                var capturedHf = hf;
                var hBtn = UiBuilder.GhostButton(
                    hLabel,
                    studio.OrganizerHealthFilter == capturedHf ? MaterialIconKind.Check : null,
                    () => studio.SetOrganizerHealthFilter(capturedHf),
                    isActive: studio.OrganizerHealthFilter == capturedHf
                );
                hBtn.Margin = new Thickness(0, 0, 4, 4);
                tagHealthWrap.Children.Add(hBtn);
            }

            var distinctTags = deck
                .Questions.Select(q => q.TopicTag?.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList();

            foreach (var tag in distinctTags)
            {
                var capturedTag = tag;
                bool isTagActive = string.Equals(
                    studio.OrganizerTopicFilter,
                    capturedTag,
                    StringComparison.OrdinalIgnoreCase
                );
                var tBtn = UiBuilder.GhostButton(
                    $"@{capturedTag}",
                    isTagActive ? MaterialIconKind.Check : null,
                    () => studio.SetOrganizerTopicFilter(capturedTag),
                    isActive: isTagActive
                );
                tBtn.Margin = new Thickness(0, 0, 4, 4);
                tagHealthWrap.Children.Add(tBtn);
            }
            filtersStack.Children.Add(tagHealthWrap);
            metaCard.Children.Add(filtersStack);
        }

        rootGrid.Children.Add(UiBuilder.Card(metaCard, padding: new Thickness(12, 8)));

        // 2. Filtered Question Set
        var visibleQuestions = studio.GetVisibleOrganizerQuestions();

        // 3. Virtualized Split Body (Collapsible Left TOC + Virtualized Question Cards)
        var splitBody = new Grid
        {
            ColumnDefinitions = studio.IsOrganizerTocOpen
                ? ColumnDefinitions.Parse("235,10,*")
                : ColumnDefinitions.Parse("*"),
        };

        var expandedCardTarget = visibleQuestions.FirstOrDefault(q =>
            string.Equals(q.Id, studio.EditingQuestionId, StringComparison.OrdinalIgnoreCase)
        );
        var (cardListBox, scrollCardToItem) = CreateCleanVirtualizedListBox(
            $"quiz-organizer-cards:{deck.Id}",
            visibleQuestions,
            q => BuildOrganizerQuestionCard(vm, studio, deck, q),
            activeExpandedItem: expandedCardTarget,
            activeExpandedId: expandedCardTarget?.Id
        );
        cardListBox.Padding = new Thickness(0, 0, 0, 72);

        ListBox? tocListBox = null;
        Action<QuizQuestionRecord>? scrollTocToItem = null;

        if (studio.IsOrganizerTocOpen)
        {
            var tocPanel = new Grid
            {
                RowDefinitions = RowDefinitions.Parse("Auto,Auto,*"),
                RowSpacing = 6,
            };
            var tocHeaderText = new TextBlock
            {
                Text = $"QUESTIONS ({visibleQuestions.Count})",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                LetterSpacing = 0.6,
                Foreground = ThemeService.Brush(ThemeService.TextMuted),
                Margin = new Thickness(2, 0, 0, 2),
            };
            tocPanel.Children.Add(tocHeaderText);

            var searchBox = new TextBox
            {
                Text = studio.OrganizerSearchQuery,
                Watermark = "Search prompt, answer, @tag...",
                FontSize = 11.5,
            };
            searchBox.TextChanged += (_, _) =>
            {
                studio.SetOrganizerSearchQueryQuiet(searchBox.Text ?? string.Empty);
                var updated = studio.GetVisibleOrganizerQuestions();
                visibleQuestions = updated;
                tocHeaderText.Text = $"QUESTIONS ({updated.Count})";
                cardListBox.ItemsSource = updated;
                tocListBox?.ItemsSource = updated;
                _cachedOrganizerSignature = ComputeOrganizerStructuralSignature(vm, studio);
            };
            searchBox.KeyDown += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Escape && !string.IsNullOrEmpty(searchBox.Text))
                {
                    e.Handled = true;
                    searchBox.Text = string.Empty;
                }
            };
            Grid.SetRow(searchBox, 1);
            tocPanel.Children.Add(searchBox);

            (tocListBox, scrollTocToItem) = CreateCleanVirtualizedListBox(
                $"quiz-organizer-toc:{deck.Id}",
                visibleQuestions,
                q =>
                {
                    int deckIdx = deck.Questions.IndexOf(q);
                    bool isSelected = string.Equals(
                        studio.SelectedOrganizerQuestionId,
                        q.Id,
                        StringComparison.OrdinalIgnoreCase
                    );

                    var tocRowBtn = new Button
                    {
                        Tag = $"org-toc:{q.Id}",
                        Background = isSelected
                            ? ThemeService.Brush(ThemeService.AccentSoftBg)
                            : Brushes.Transparent,
                        BorderBrush = isSelected
                            ? ThemeService.Brush(ThemeService.AccentPrimary)
                            : Brushes.Transparent,
                        BorderThickness = new Thickness(isSelected ? 1.5 : 1),
                        CornerRadius = new CornerRadius(5),
                        Padding = new Thickness(7, 5),
                        Margin = new Thickness(0, 1),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                        Content = new Grid
                        {
                            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
                            ColumnSpacing = 6,
                            Children =
                            {
                                UiBuilder.Pill(
                                    $"#{deckIdx + 1}",
                                    isSelected
                                        ? ThemeService.Brush(ThemeService.AccentPrimary)
                                        : ThemeService.Brush(ThemeService.TextSecondary),
                                    ThemeService.Brush(ThemeService.BgElevated),
                                    fontSize: 10
                                ),
                                new StackPanel
                                {
                                    Spacing = 1,
                                    [Grid.ColumnProperty] = 1,
                                    Children =
                                    {
                                        new TextBlock
                                        {
                                            Text = q.Prompt,
                                            FontSize = 11.5,
                                            FontWeight = isSelected
                                                ? FontWeight.SemiBold
                                                : FontWeight.Normal,
                                            Foreground = ThemeService.Brush(
                                                ThemeService.TextPrimary
                                            ),
                                            TextTrimming = TextTrimming.CharacterEllipsis,
                                            MaxLines = 1,
                                        },
                                        new TextBlock
                                        {
                                            Text = $"{q.Kind} · {q.EvidenceChipLabel}",
                                            FontSize = 10,
                                            Foreground = ThemeService.Brush(ThemeService.TextMuted),
                                        },
                                    },
                                },
                            },
                        },
                    };
                    tocRowBtn.PointerEntered += (_, _) =>
                    {
                        if (
                            !string.Equals(
                                studio.SelectedOrganizerQuestionId,
                                q.Id,
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                            tocRowBtn.Background = ThemeService.Brush(ThemeService.BgHover);
                    };
                    tocRowBtn.PointerExited += (_, _) =>
                    {
                        if (
                            !string.Equals(
                                studio.SelectedOrganizerQuestionId,
                                q.Id,
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                            tocRowBtn.Background = Brushes.Transparent;
                    };
                    tocRowBtn.Click += (_, _) =>
                    {
                        studio.SelectOrganizerQuestion(
                            q.Id,
                            notify: false,
                            scrollCardIntoView: true,
                            scrollTocIntoView: false
                        );
                    };
                    return tocRowBtn;
                }
            );
            Grid.SetRow(tocListBox, 2);
            tocPanel.Children.Add(tocListBox);

            splitBody.Children.Add(UiBuilder.Card(tocPanel, padding: new Thickness(8, 8)));
            Grid.SetColumn(cardListBox, 2);
        }

        // Hook lightweight in-place selection sync between TOC, Question Cards, and Keyboard Nav (zero rebuilds)
        if (_selectionHookStudio is not null && _selectionHookHandler is not null)
            _selectionHookStudio.OrganizerSelectionChanged -= _selectionHookHandler;

        void UpdateSelectionVisuals(string? selectedId)
        {
            var cardTag = selectedId is null ? null : $"org-card:{selectedId}";
            var tocTag = selectedId is null ? null : $"org-toc:{selectedId}";

            foreach (var b in cardListBox.GetVisualDescendants().OfType<Border>())
            {
                if (b.Tag is string t && t.StartsWith("org-card:", StringComparison.Ordinal))
                {
                    bool sel = string.Equals(t, cardTag, StringComparison.OrdinalIgnoreCase);
                    b.BorderBrush = sel
                        ? ThemeService.Brush(ThemeService.AccentPrimary)
                        : ThemeService.Brush(ThemeService.BorderSubtle);
                    b.BorderThickness = new Thickness(sel ? 2 : 1);
                }
            }

            if (tocListBox is not null)
            {
                foreach (var btn in tocListBox.GetVisualDescendants().OfType<Button>())
                {
                    if (btn.Tag is string t && t.StartsWith("org-toc:", StringComparison.Ordinal))
                    {
                        bool sel = string.Equals(t, tocTag, StringComparison.OrdinalIgnoreCase);
                        btn.Background = sel
                            ? ThemeService.Brush(ThemeService.AccentSoftBg)
                            : Brushes.Transparent;
                        btn.BorderBrush = sel
                            ? ThemeService.Brush(ThemeService.AccentPrimary)
                            : Brushes.Transparent;
                        btn.BorderThickness = new Thickness(sel ? 1.5 : 1);
                    }
                }
            }
        }

        _selectionHookStudio = studio;
        _selectionHookHandler = (selectedId, scrollCard, scrollToc) =>
        {
            var targetQ = visibleQuestions.FirstOrDefault(x =>
                string.Equals(x.Id, selectedId, StringComparison.OrdinalIgnoreCase)
            );
            if (targetQ is not null)
            {
                if (scrollCard)
                    scrollCardToItem(targetQ);
                if (scrollToc && scrollTocToItem is not null)
                    scrollTocToItem(targetQ);
            }

            UpdateSelectionVisuals(selectedId);
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => UpdateSelectionVisuals(selectedId),
                Avalonia.Threading.DispatcherPriority.Render
            );
        };
        studio.OrganizerSelectionChanged += _selectionHookHandler;

        void AttachOrganizerNavKeyHandler(ListBox listBox)
        {
            listBox.AddHandler(
                Avalonia.Input.InputElement.KeyDownEvent,
                (_, e) =>
                {
                    if (
                        e.KeyModifiers != Avalonia.Input.KeyModifiers.None
                        || e.Source is TextBox
                        || studio.ActiveSession is not null
                        || studio.IsDedicatedSnipperOpen
                        || studio.EditingQuestionDraft is not null
                        || studio.IsDeckAiFixModalOpen
                        || !string.IsNullOrWhiteSpace(studio.FlaggingQuestionId)
                    )
                        return;

                    switch (e.Key)
                    {
                        case Avalonia.Input.Key.Down:
                            e.Handled = true;
                            studio.MoveOrganizerSelection(+1);
                            break;
                        case Avalonia.Input.Key.Up:
                            e.Handled = true;
                            studio.MoveOrganizerSelection(-1);
                            break;
                        case Avalonia.Input.Key.Home:
                            e.Handled = true;
                            studio.JumpOrganizerSelection(toEnd: false);
                            break;
                        case Avalonia.Input.Key.End:
                            e.Handled = true;
                            studio.JumpOrganizerSelection(toEnd: true);
                            break;
                    }
                },
                Avalonia.Interactivity.RoutingStrategies.Tunnel
            );
        }

        AttachOrganizerNavKeyHandler(cardListBox);
        if (tocListBox is not null)
            AttachOrganizerNavKeyHandler(tocListBox);

        splitBody.Children.Add(cardListBox);
        Grid.SetRow(splitBody, 1);
        rootGrid.Children.Add(splitBody);

        // 4. Unified Floating '+' Action Button (FAB)
        var fabMenu = new MenuFlyout();
        var addManualMenu = new MenuItem { Header = "Add Question Manually" };
        foreach (var kind in Enum.GetValues<QuizQuestionKind>())
        {
            var capturedK = kind;
            var kSub = new MenuItem { Header = kind.ToString() };
            kSub.Click += (_, _) => studio.AddBlankOrganizerQuestion(capturedK);
            addManualMenu.Items.Add(kSub);
        }
        fabMenu.Items.Add(addManualMenu);

        var appendAiFab = new MenuItem { Header = "Generate & Append with AI" };
        appendAiFab.Click += (_, _) => studio.StartAppendToQuiz(deck);
        fabMenu.Items.Add(appendAiFab);

        var deckFixFab = new MenuItem { Header = "Comprehensive AI Deck Fix" };
        deckFixFab.Click += (_, _) => studio.OpenDeckAiFixModal();
        fabMenu.Items.Add(deckFixFab);

        var fabNormalBg = ThemeService.Brush(ThemeService.AccentPrimary);
        var fabBtn = new Button
        {
            Width = 46,
            Height = 46,
            CornerRadius = new CornerRadius(23),
            Background = fabNormalBg,
            BorderBrush = ThemeService.Brush(ThemeService.AccentPrimary),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Content = UiBuilder.Icon(MaterialIconKind.Plus, 22, Brushes.White),
            Flyout = fabMenu,
        };
        fabBtn.PointerEntered += (_, _) => fabBtn.Opacity = 0.88;
        fabBtn.PointerExited += (_, _) => fabBtn.Opacity = 1.0;
        var fabHost = new Border
        {
            CornerRadius = new CornerRadius(23),
            BoxShadow = new BoxShadows(
                new BoxShadow
                {
                    OffsetX = 0,
                    OffsetY = 4,
                    Blur = 14,
                    Spread = 0,
                    Color = Color.Parse("#66000000"),
                }
            ),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 18, 18),
            Child = fabBtn,
        };
        fabBtn.Margin = new Thickness(0);
        Grid.SetRow(fabHost, 1);
        rootGrid.Children.Add(fabHost);

        return rootGrid;
    }

    private static readonly Dictionary<string, Vector> SavedOrganizerScrollOffsets = new(
        StringComparer.Ordinal
    );
    private static readonly Dictionary<string, string?> SavedOrganizerAnchorIds = new(
        StringComparer.Ordinal
    );

    private static (ListBox ListBox, Action<T> ScrollToItem) CreateCleanVirtualizedListBox<T>(
        string scrollKey,
        IReadOnlyList<T> items,
        Func<T, Control> templateBuilder,
        T? activeExpandedItem = default,
        string? activeExpandedId = null
    )
    {
        var lb = new ListBox
        {
            ItemsSource = items,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Focusable = false,
            SelectionMode = SelectionMode.Single,
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<T>(
                (item, _) => item is null ? new Border() : templateBuilder(item)
            ),
        };

        var itemResetStyle = new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
                new Setter(Layoutable.MarginProperty, new Thickness(0, 0, 0, 6)),
                new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                new Setter(Avalonia.Input.InputElement.FocusableProperty, false),
            },
        };
        lb.Styles.Add(itemResetStyle);

        // Suppress FluentTheme's square ListBoxItem hover/selected/pressed backdrop so
        // rounded TOC rows and rounded question cards never show a square halo behind them.
        foreach (var pseudo in new[] { ":pointerover", ":selected", ":pressed" })
        {
            var capturedPseudo = pseudo;
            lb.Styles.Add(
                new Style(x =>
                    x.OfType<ListBoxItem>()
                        .Class(capturedPseudo)
                        .Template()
                        .OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                        .Name("PART_ContentPresenter")
                )
                {
                    Setters =
                    {
                        new Setter(
                            Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty,
                            Brushes.Transparent
                        ),
                    },
                }
            );
        }
        lb.Styles.Add(
            new Style(x =>
                x.OfType<ListBoxItem>()
                    .Class(":selected")
                    .Class(":pointerover")
                    .Template()
                    .OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                    .Name("PART_ContentPresenter")
            )
            {
                Setters =
                {
                    new Setter(
                        Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty,
                        Brushes.Transparent
                    ),
                },
            }
        );

        var savedOffset = SavedOrganizerScrollOffsets.GetValueOrDefault(scrollKey, Vector.Zero);
        var prevAnchorId = SavedOrganizerAnchorIds.GetValueOrDefault(scrollKey);
        bool anchorChanged =
            !string.IsNullOrWhiteSpace(activeExpandedId)
            && !string.Equals(prevAnchorId, activeExpandedId, StringComparison.OrdinalIgnoreCase);
        SavedOrganizerAnchorIds[scrollKey] = activeExpandedId;

        bool layoutRestored = false;
        bool attached = false;
        bool allowProgrammaticScroll = false;

        void ScrollToItemSafe(T target)
        {
            allowProgrammaticScroll = true;
            lb.ScrollIntoView(target!);
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => allowProgrammaticScroll = false,
                Avalonia.Threading.DispatcherPriority.Render
            );
        }

        // Suppress automatic BringIntoView from clicks/focus inside tall cards,
        // while allowing explicit TOC/keyboard ScrollIntoView navigation.
        lb.AddHandler(
            Control.RequestBringIntoViewEvent,
            (_, e) =>
            {
                if (!allowProgrammaticScroll)
                    e.Handled = true;
            },
            Avalonia.Interactivity.RoutingStrategies.Tunnel
        );
        lb.Loaded += (_, _) =>
        {
            var sv = lb.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (sv is null || attached)
                return;

            attached = true;
            sv.ScrollChanged += (_, _) =>
            {
                if (layoutRestored)
                    SavedOrganizerScrollOffsets[scrollKey] = sv.Offset;
            };

            Avalonia.Threading.Dispatcher.UIThread.Post(
                () =>
                {
                    if (anchorChanged && activeExpandedItem is not null)
                    {
                        ScrollToItemSafe(activeExpandedItem);
                    }
                    else if (savedOffset != Vector.Zero)
                    {
                        sv.Offset = savedOffset;
                    }

                    Avalonia.Threading.Dispatcher.UIThread.Post(
                        () =>
                        {
                            if (!anchorChanged && savedOffset != Vector.Zero)
                                sv.Offset = savedOffset;
                            layoutRestored = true;
                            SavedOrganizerScrollOffsets[scrollKey] = sv.Offset;
                        },
                        Avalonia.Threading.DispatcherPriority.Render
                    );
                },
                Avalonia.Threading.DispatcherPriority.Loaded
            );
        };
        return (lb, ScrollToItemSafe);
    }

    private static Border BuildOrganizerQuestionCard(
        MainWindowViewModel vm,
        QuizStudioViewModel studio,
        QuizSessionRecord deck,
        QuizQuestionRecord q
    )
    {
        int deckIndex = deck.Questions.IndexOf(q);
        bool isSelected = string.Equals(
            studio.SelectedOrganizerQuestionId,
            q.Id,
            StringComparison.OrdinalIgnoreCase
        );
        bool isFlagging = string.Equals(
            studio.FlaggingQuestionId,
            q.Id,
            StringComparison.OrdinalIgnoreCase
        );
        bool isDeckFixChecked = studio.DeckAiFixSelectedIds.Contains(q.Id);

        var cardStack = new StackPanel { Spacing = 8 };

        // Header Meta Strip
        var headerGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 8,
        };

        var badges = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        void AddMetaBadge(Control pill)
        {
            pill.Margin = new Thickness(0, 0, 5, 3);
            badges.Children.Add(pill);
        }
        AddMetaBadge(
            UiBuilder.PillBadge(
                $"#{deckIndex + 1}",
                ThemeService.Brush(ThemeService.TextPrimary),
                ThemeService.Brush(ThemeService.BgElevated)
            )
        );
        AddMetaBadge(
            UiBuilder.PillBadge(
                q.Kind.ToString(),
                ThemeService.Brush(ThemeService.AccentPrimary),
                ThemeService.Brush(ThemeService.AccentSoftBg)
            )
        );
        AddMetaBadge(
            UiBuilder.PillBadge(
                q.EvidenceChipLabel,
                q.HighlightRects.Count > 0
                    ? ThemeService.Brush(ThemeService.SuccessFg)
                    : ThemeService.Brush(ThemeService.PriorityP3Fg),
                q.HighlightRects.Count > 0
                    ? ThemeService.Brush(ThemeService.SuccessBg)
                    : ThemeService.Brush(ThemeService.PriorityP3Bg)
            )
        );
        AddMetaBadge(
            UiBuilder.PillBadge(
                q.CognitiveLevel.ToString(),
                ThemeService.Brush(ThemeService.PriorityP3Fg),
                ThemeService.Brush(ThemeService.PriorityP3Bg)
            )
        );
        if (!string.IsNullOrWhiteSpace(q.Archetype))
        {
            AddMetaBadge(
                UiBuilder.PillBadge(
                    QuizBlueprintBuilder.FormatArchetypeBadge(q.Archetype),
                    ThemeService.Brush(ThemeService.PriorityP2Fg),
                    ThemeService.Brush(ThemeService.PriorityP2Bg)
                )
            );
        }
        var qCriticIssues = studio.GetCachedOrganizerQualityReport().GetIssuesFor(q.Id);
        if (qCriticIssues.Count > 0)
        {
            bool hasErr = qCriticIssues.Any(i => i.Severity == QualityIssueSeverity.Error);
            AddMetaBadge(
                UiBuilder.PillBadge(
                    $"{qCriticIssues.Count} issue{(qCriticIssues.Count == 1 ? "" : "s")}: {qCriticIssues[0].Code}",
                    hasErr
                        ? ThemeService.Brush(ThemeService.PriorityP1Fg)
                        : ThemeService.Brush(ThemeService.PriorityP2Fg),
                    hasErr
                        ? ThemeService.Brush(ThemeService.PriorityP1Bg)
                        : ThemeService.Brush(ThemeService.PriorityP2Bg)
                )
            );
        }
        if (!string.IsNullOrWhiteSpace(q.TopicTag))
        {
            AddMetaBadge(
                UiBuilder.PillBadge(
                    $"@{q.TopicTag}",
                    ThemeService.Brush(ThemeService.TextMuted),
                    ThemeService.Brush(ThemeService.BgBase)
                )
            );
        }
        headerGrid.Children.Add(badges);

        var rowActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        rowActions.Children.Add(
            UiBuilder.IconButton(
                isDeckFixChecked ? MaterialIconKind.Flag : MaterialIconKind.FlagOutline,
                () => studio.ToggleDeckAiFixTarget(q.Id),
                14,
                isDeckFixChecked
                    ? ThemeService.Brush(ThemeService.PriorityP2Fg)
                    : ThemeService.Brush(ThemeService.TextMuted)
            )
        );
        rowActions.Children.Add(
            UiBuilder.IconButton(
                MaterialIconKind.ArrowUp,
                () => studio.MoveOrganizerQuestion(q, -1),
                14,
                ThemeService.Brush(ThemeService.TextSecondary)
            )
        );
        rowActions.Children.Add(
            UiBuilder.IconButton(
                MaterialIconKind.ArrowDown,
                () => studio.MoveOrganizerQuestion(q, +1),
                14,
                ThemeService.Brush(ThemeService.TextSecondary)
            )
        );
        if (File.Exists(q.SourcePdfPath))
        {
            rowActions.Children.Add(
                UiBuilder.GhostButton(
                    q.EvidenceChipLabel,
                    MaterialIconKind.Marker,
                    () => studio.OpenQuestionEvidenceInPdfReader(q)
                )
            );
        }
        rowActions.Children.Add(
            UiBuilder.GhostButton(
                isFlagging ? "Close AI Fix" : "Fix with AI",
                MaterialIconKind.AutoFix,
                () => studio.ToggleFlagQuestionForAiFix(q.Id),
                isActive: isFlagging
            )
        );
        rowActions.Children.Add(
            UiBuilder.GhostButton(
                "Edit",
                MaterialIconKind.PencilOutline,
                () => studio.ToggleEditQuestion(q.Id)
            )
        );
        rowActions.Children.Add(
            UiBuilder.IconButton(
                MaterialIconKind.TrashCanOutline,
                () => studio.DeleteOrganizerQuestion(q),
                14,
                ThemeService.Brush(ThemeService.PriorityP1Fg)
            )
        );
        Grid.SetColumn(rowActions, 1);
        headerGrid.Children.Add(rowActions);
        cardStack.Children.Add(headerGrid);

        AppendQuestionPreviewContent(cardStack, studio, q);

        var cardBorder = new Border
        {
            Tag = $"org-card:{q.Id}",
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = isSelected
                ? ThemeService.Brush(ThemeService.AccentPrimary)
                : ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(isSelected ? 2 : 1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            Child = cardStack,
        };
        cardBorder.AddHandler(
            Avalonia.Input.InputElement.PointerPressedEvent,
            (_, _) =>
                studio.SelectOrganizerQuestion(
                    q.Id,
                    notify: false,
                    scrollCardIntoView: false,
                    scrollTocIntoView: true
                ),
            Avalonia.Interactivity.RoutingStrategies.Tunnel,
            handledEventsToo: true
        );
        return cardBorder;
    }

    private static Grid BuildDedicatedPdfSnipperView(
        MainWindowViewModel vm,
        QuizStudioViewModel studio
    )
    {
        var draft = studio.EditingQuestionDraft!;
        int curPage =
            studio.EditingSnipPageNumber > 0
                ? studio.EditingSnipPageNumber
                : Math.Max(1, draft.SourcePage);
        int totalPages = Math.Max(1, studio.EditingSnipTotalPages);
        var activeNode = studio.FindActiveSnipBookmarkNode(curPage);

        var root = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,*"),
            Margin = new Thickness(12, 8),
        };

        // 1. Standard PDF-Reader-Style Responsive Top Toolbar
        var toolbarBorder = new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0, 0, 0, 8),
        };

        var toolbarGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto") };

        var leftGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        leftGroup.Children.Add(
            UiBuilder.GhostButton(
                "Back to Editor",
                MaterialIconKind.ArrowLeft,
                studio.CloseDedicatedSnipper
            )
        );
        if (studio.SnipOutlineRoots.Count > 0)
        {
            leftGroup.Children.Add(
                UiBuilder.GhostButton(
                    "Outline",
                    MaterialIconKind.FileTreeOutline,
                    studio.ToggleSnipOutline,
                    isActive: studio.IsSnipOutlineOpen
                )
            );
        }
        toolbarGrid.Children.Add(leftGroup);

        var titleText = new TextBlock
        {
            Text = $"Snip Evidence · {draft.Prompt}",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(titleText, 1);
        toolbarGrid.Children.Add(titleText);

        var rightControls = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var navRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        navRow.Children.Add(
            UiBuilder.IconButton(
                MaterialIconKind.PageFirst,
                async () => await studio.LoadEditorSnipPageAsync(1)
            )
        );
        navRow.Children.Add(
            UiBuilder.IconButton(
                MaterialIconKind.ChevronLeft,
                async () => await studio.LoadEditorSnipPageAsync(Math.Max(1, curPage - 1))
            )
        );
        navRow.Children.Add(
            new Border
            {
                Background = ThemeService.Brush(ThemeService.BgElevated),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 3.5),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = $"{curPage} / {totalPages}",
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                },
            }
        );
        navRow.Children.Add(
            UiBuilder.IconButton(
                MaterialIconKind.ChevronRight,
                async () => await studio.LoadEditorSnipPageAsync(Math.Min(totalPages, curPage + 1))
            )
        );
        navRow.Children.Add(
            UiBuilder.IconButton(
                MaterialIconKind.PageLast,
                async () => await studio.LoadEditorSnipPageAsync(totalPages)
            )
        );
        rightControls.Children.Add(navRow);

        var zoomRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        zoomRow.Children.Add(
            UiBuilder.GhostButton(
                "Fit Width",
                MaterialIconKind.ArrowExpandHorizontal,
                async () => await studio.ToggleSnipFitToWidthAsync(),
                isActive: studio.SnipFitToWidth
            )
        );
        zoomRow.Children.Add(
            UiBuilder.IconButton(
                MaterialIconKind.MagnifyMinusOutline,
                async () => await studio.ZoomSnipAsync(-25)
            )
        );
        zoomRow.Children.Add(
            UiBuilder.GhostButton(
                studio.SnipFitToWidth ? "Auto" : $"{studio.SnipZoomPercent}%",
                null,
                async () => await studio.ToggleSnipFitToWidthAsync()
            )
        );
        zoomRow.Children.Add(
            UiBuilder.IconButton(
                MaterialIconKind.MagnifyPlusOutline,
                async () => await studio.ZoomSnipAsync(+25)
            )
        );
        zoomRow.Children.Add(
            UiBuilder.GhostButton(
                "Pin Highlight",
                MaterialIconKind.Marker,
                () => studio.SetSnipMode(SnipCaptureMode.PinHighlight),
                isActive: studio.SnipMode == SnipCaptureMode.PinHighlight
            )
        );
        zoomRow.Children.Add(
            UiBuilder.GhostButton(
                "Capture Image",
                MaterialIconKind.Crop,
                () => studio.SetSnipMode(SnipCaptureMode.CaptureImage),
                isActive: studio.SnipMode == SnipCaptureMode.CaptureImage
            )
        );
        zoomRow.Children.Add(
            UiBuilder.GhostButton(
                "Extract Figures",
                MaterialIconKind.ImageMultipleOutline,
                async () => await studio.ExtractEmbeddedFiguresOnSnipPageAsync()
            )
        );
        zoomRow.Children.Add(
            UiBuilder.GhostButton(
                $"Clear ({draft.EvidenceAnchors.Count})",
                MaterialIconKind.Eraser,
                studio.ClearVisualSnipHighlights
            )
        );
        rightControls.Children.Add(zoomRow);

        Grid.SetColumn(rightControls, 2);
        toolbarGrid.Children.Add(rightControls);
        toolbarBorder.Child = toolbarGrid;
        root.Children.Add(toolbarBorder);

        // 2. 3-Pane Dedicated Body: Left Outline (240px) + Center Legible PDF (*) + Right Anchors Rail (250px)
        bool showOutline = studio.IsSnipOutlineOpen && studio.SnipOutlineRoots.Count > 0;
        var body = new Grid
        {
            ColumnDefinitions = showOutline
                ? ColumnDefinitions.Parse("240,10,*,10,250")
                : ColumnDefinitions.Parse("*,10,250"),
        };

        int canvasCol = 0;
        if (showOutline)
        {
            var outlineStack = new StackPanel { Spacing = 2, Margin = new Thickness(4) };
            outlineStack.Children.Add(UiBuilder.SectionHeader("Document Outline"));
            foreach (var rootNode in studio.SnipOutlineRoots)
                outlineStack.Children.Add(BuildSnipBookmarkTreeNode(studio, rootNode, activeNode));

            body.Children.Add(
                new Border
                {
                    Background = ThemeService.Brush(ThemeService.BgSurface),
                    BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(6),
                    Child = UiBuilder.PersistentScrollViewer("quiz-snip-outline", outlineStack),
                }
            );
            canvasCol = 2;
        }

        // Center Legible PDF Canvas
        Control canvasContent;
        if (studio.IsLoadingSnipPage)
        {
            canvasContent = new TextBlock
            {
                Text = $"Rendering page {curPage}...",
                FontSize = 13,
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else if (studio.EditingSnipPageBitmap is not null)
        {
            var pageHighlights = draft
                .EvidenceAnchors.Where(a => a.PageNumber == curPage)
                .SelectMany(a => a.HighlightRects)
                .ToList();

            var img = new Image
            {
                Source = studio.EditingSnipPageBitmap,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
            };
            if (!studio.SnipFitToWidth)
                img.Width = 760 * (studio.SnipZoomPercent / 100.0);

            var pageLayer = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
            };
            pageLayer.Children.Add(img);
            pageLayer.Children.Add(
                new PdfVisualSnipperControl(
                    pageHighlights,
                    async (rect, additive) =>
                    {
                        if (studio.SnipMode == SnipCaptureMode.CaptureImage)
                            await studio.CaptureQuestionImageFromSnipperAsync(rect);
                        else
                            await studio.ApplyVisualSnipRegionAsync(rect, additive);
                    }
                )
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                }
            );

            var sheet = new Border
            {
                Background = Brushes.White,
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(12),
                Padding = new Thickness(4),
                HorizontalAlignment = studio.SnipFitToWidth
                    ? HorizontalAlignment.Stretch
                    : HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                MaxWidth = studio.SnipFitToWidth ? 920 : double.PositiveInfinity,
                Child = pageLayer,
            };

            var snipScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = studio.SnipFitToWidth
                    ? ScrollBarVisibility.Disabled
                    : ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = sheet,
            };
            UiBuilder.AttachPdfWheelAndPan(snipScroll, delta => _ = studio.ZoomSnipAsync(delta));
            canvasContent = snipScroll;
        }
        else
        {
            canvasContent = new TextBlock
            {
                Text = "Select a page to snip evidence.",
                FontSize = 13,
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        var canvasFrame = new Border
        {
            Background = ThemeService.Brush(ThemeService.BgBase),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = canvasContent,
        };
        Grid.SetColumn(canvasFrame, canvasCol);
        body.Children.Add(canvasFrame);

        // Right Pinned Evidence Anchors & Captured Image Rail
        var railStack = new StackPanel { Spacing = 8 };
        railStack.Children.Add(
            UiBuilder.SectionHeader($"Evidence Anchors ({draft.EvidenceAnchors.Count})")
        );
        railStack.Children.Add(
            new TextBlock
            {
                Text =
                    studio.SnipMode == SnipCaptureMode.CaptureImage
                        ? "Mode: Capture Image — drag a box around any diagram/figure to attach it as the question's image."
                        : "Mode: Pin Highlight — drag on the PDF to pin an anchor (Shift+Drag adds boxes to this page).",
                FontSize = 11,
                Foreground = ThemeService.Brush(ThemeService.TextMuted),
                TextWrapping = TextWrapping.Wrap,
            }
        );

        var capturedImgFull = studio.ResolveQuestionImageFullPath(draft.ImagePath);
        if (!string.IsNullOrWhiteSpace(capturedImgFull))
        {
            railStack.Children.Add(
                QuizRichContentRenderer.BuildQuestionImageCard(
                    capturedImgFull,
                    () => studio.OpenImageZoom(draft.ImagePath)
                )
            );
        }

        if (studio.SnipExtractedFigures.Count > 0)
        {
            railStack.Children.Add(
                UiBuilder.SectionHeader($"Embedded Figures ({studio.SnipExtractedFigures.Count})")
            );
            for (int fi = 0; fi < studio.SnipExtractedFigures.Count; fi++)
            {
                var figPath = studio.SnipExtractedFigures[fi];
                railStack.Children.Add(
                    UiBuilder.GhostButton(
                        $"Attach Figure {fi + 1}",
                        MaterialIconKind.ImagePlusOutline,
                        () => studio.SetEditingQuestionImage(figPath)
                    )
                );
            }
        }

        for (int ai = 0; ai < draft.EvidenceAnchors.Count; ai++)
        {
            int capturedAi = ai;
            var anchor = draft.EvidenceAnchors[ai];
            var anchorCard = new StackPanel { Spacing = 4 };
            var aHead = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
            aHead.Children.Add(
                UiBuilder.GhostButton(
                    $"Page {anchor.PageNumber} ({anchor.HighlightRects.Count} box)",
                    MaterialIconKind.Marker,
                    async () => await studio.LoadEditorSnipPageAsync(anchor.PageNumber),
                    isActive: anchor.PageNumber == curPage
                )
            );
            var delBtn = UiBuilder.IconButton(
                MaterialIconKind.Close,
                () => studio.RemoveEvidenceAnchor(capturedAi),
                13,
                ThemeService.Brush(ThemeService.PriorityP1Fg)
            );
            Grid.SetColumn(delBtn, 1);
            aHead.Children.Add(delBtn);
            anchorCard.Children.Add(aHead);

            if (!string.IsNullOrWhiteSpace(anchor.VerbatimQuote))
            {
                anchorCard.Children.Add(
                    new TextBlock
                    {
                        Text = $"\"{anchor.VerbatimQuote}\"",
                        FontSize = 11,
                        Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                        TextWrapping = TextWrapping.Wrap,
                        MaxLines = 3,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    }
                );
            }
            railStack.Children.Add(
                new Border
                {
                    Background = ThemeService.Brush(ThemeService.BgBase),
                    BorderBrush =
                        anchor.PageNumber == curPage
                            ? ThemeService.Brush(ThemeService.AccentPrimary)
                            : ThemeService.Brush(ThemeService.BorderSubtle),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 6),
                    Child = anchorCard,
                }
            );
        }

        var railCard = UiBuilder.Card(
            UiBuilder.PersistentScrollViewer("quiz-snip-anchors", railStack),
            padding: new Thickness(10, 8)
        );
        Grid.SetColumn(railCard, canvasCol + 2);
        body.Children.Add(railCard);

        Grid.SetRow(body, 1);
        root.Children.Add(body);
        return root;
    }

    private static Border BuildDeckComprehensiveAiFixOverlay(
        MainWindowViewModel vm,
        QuizStudioViewModel studio
    )
    {
        var deck = studio.OrganizerQuiz!;
        var modalStack = new StackPanel { Spacing = 10 };

        var header = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 8,
        };
        header.Children.Add(
            new TextBlock
            {
                Text = $"Comprehensive AI Deck Fix — {deck.Title}",
                FontSize = 15,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        var closeBtn = UiBuilder.GhostButton(
            "Close",
            MaterialIconKind.Close,
            studio.CloseDeckAiFixModal
        );
        Grid.SetColumn(closeBtn, 1);
        header.Children.Add(closeBtn);
        modalStack.Children.Add(header);

        var qualityReport = studio.GetCachedOrganizerQualityReport();
        var qualityFlagged = qualityReport.FlaggedQuestionIds;

        // Multi-Select Quick Actions Bar
        var quickSelectRow = new WrapPanel { Orientation = Orientation.Horizontal };
        var selectIssuesBtn = UiBuilder.GhostButton(
            $"Select Quality Issues ({qualityFlagged.Count})",
            MaterialIconKind.AlertOutline,
            studio.SelectQualityIssuesForBulkAiFix,
            isActive: qualityFlagged.Count > 0
                && qualityFlagged.IsSubsetOf(studio.DeckAiFixSelectedIds)
        );
        selectIssuesBtn.IsEnabled = qualityFlagged.Count > 0;
        selectIssuesBtn.Margin = new Thickness(0, 0, 6, 4);
        quickSelectRow.Children.Add(selectIssuesBtn);

        var selectAllBtn = UiBuilder.GhostButton(
            $"Select All ({deck.Questions.Count})",
            MaterialIconKind.CheckboxMultipleMarkedOutline,
            studio.SelectAllQuestionsForBulkAiFix
        );
        selectAllBtn.Margin = new Thickness(0, 0, 6, 4);
        quickSelectRow.Children.Add(selectAllBtn);

        if (studio.DeckAiFixSelectedIds.Count > 0)
        {
            var clearSelBtn = UiBuilder.GhostButton(
                "Clear Selection",
                MaterialIconKind.Close,
                studio.ClearBulkAiFixSelection
            );
            clearSelBtn.Margin = new Thickness(0, 0, 6, 4);
            quickSelectRow.Children.Add(clearSelBtn);
        }
        modalStack.Children.Add(quickSelectRow);

        modalStack.Children.Add(
            new TextBlock
            {
                Text =
                    studio.DeckAiFixSelectedIds.Count > 0
                        ? $"Targeting {studio.DeckAiFixSelectedIds.Count} selected question(s) (click any # pill to toggle; amber alert icon = quality critic issue):"
                        : "Select specific questions below to repair (or leave empty to target the first 15 questions / add new ones):",
                FontSize = 11.5,
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                TextWrapping = TextWrapping.Wrap,
            }
        );

        // Full Scrollable Multi-Select Question Grid (all questions in deck)
        var targetWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        for (int idx = 0; idx < deck.Questions.Count; idx++)
        {
            var captured = deck.Questions[idx];
            bool isPicked = studio.DeckAiFixSelectedIds.Contains(captured.Id);
            bool hasIssue = qualityFlagged.Contains(captured.Id);
            var pillBtn = UiBuilder.GhostButton(
                $"#{idx + 1}",
                isPicked
                    ? MaterialIconKind.Check
                    : (hasIssue ? MaterialIconKind.AlertOutline : null),
                () => studio.ToggleDeckAiFixTarget(captured.Id),
                isActive: isPicked
            );
            pillBtn.Margin = new Thickness(0, 0, 4, 4);
            targetWrap.Children.Add(pillBtn);
        }
        modalStack.Children.Add(
            new Border
            {
                Background = ThemeService.Brush(ThemeService.BgBase),
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6),
                MaxHeight = 140,
                Child = UiBuilder.PersistentScrollViewer(
                    $"deck-ai-fix-targets:{deck.Id}",
                    targetWrap
                ),
            }
        );

        var instrBox = new TextBox
        {
            Text = studio.DeckAiFixInstructions,
            Watermark =
                "e.g., Fix inaccurate answers on flagged items, add C code snippets where relevant, and add 2 new questions on Virtual Memory...",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 64,
            FontSize = 12.5,
        };
        instrBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                studio.DeckAiFixInstructions = instrBox.Text ?? string.Empty;
        };
        modalStack.Children.Add(instrBox);

        // Mode Toggle (AI Engine API vs Web AI JSON)
        var modeRow = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (
            var (m, lbl) in new[]
            {
                (QuizBuilderOutputMode.AiApi, "AI Engine API"),
                (QuizBuilderOutputMode.WebJson, "Web AI JSON (Copy/Paste)"),
            }
        )
        {
            var capturedM = m;
            var mBtn = UiBuilder.GhostButton(
                lbl,
                null,
                () => studio.SetDeckAiFixOutputMode(capturedM),
                isActive: studio.DeckAiFixOutputMode == capturedM
            );
            mBtn.Margin = new Thickness(0, 0, 6, 4);
            modeRow.Children.Add(mBtn);
        }
        modalStack.Children.Add(modeRow);

        if (studio.DeckAiFixOutputMode == QuizBuilderOutputMode.WebJson)
        {
            var copyRow = new WrapPanel { Orientation = Orientation.Horizontal };
            var copyBtn = UiBuilder.GhostButton(
                "Build & Copy Context Prompt",
                MaterialIconKind.ContentCopy,
                async () =>
                {
                    var top = TopLevel.GetTopLevel(modalStack);
                    var prompt = studio.BuildDeckAiFixPrompt();
                    if (top?.Clipboard is not null)
                        await top.Clipboard.SetTextAsync(prompt);
                },
                isActive: !string.IsNullOrWhiteSpace(studio.DeckAiFixPromptPreview)
            );
            copyRow.Children.Add(copyBtn);
            modalStack.Children.Add(copyRow);

            var pasteBox = new TextBox
            {
                Text = studio.DeckAiFixJsonInput,
                Watermark =
                    "Paste the { \"updatedQuestions\": [...], \"newQuestions\": [...] } JSON response here...",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 90,
                FontSize = 11.5,
                FontFamily = new FontFamily("Consolas, JetBrains Mono, monospace"),
            };
            pasteBox.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                    studio.DeckAiFixJsonInput = pasteBox.Text ?? string.Empty;
            };
            modalStack.Children.Add(pasteBox);

            if (!string.IsNullOrWhiteSpace(studio.DeckAiFixFeedback))
            {
                modalStack.Children.Add(
                    new Border
                    {
                        Background = studio.IsDeckAiFixError
                            ? ThemeService.Brush(ThemeService.PriorityP1Bg)
                            : ThemeService.Brush(ThemeService.SuccessBg),
                        BorderBrush = studio.IsDeckAiFixError
                            ? ThemeService.Brush(ThemeService.PriorityP1Fg)
                            : ThemeService.Brush(ThemeService.SuccessFg),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(5),
                        Padding = new Thickness(10, 6),
                        Child = new TextBlock
                        {
                            Text = studio.DeckAiFixFeedback,
                            FontSize = 11.5,
                            Foreground = studio.IsDeckAiFixError
                                ? ThemeService.Brush(ThemeService.PriorityP1Fg)
                                : ThemeService.Brush(ThemeService.SuccessFg),
                            TextWrapping = TextWrapping.Wrap,
                        },
                    }
                );
            }

            modalStack.Children.Add(
                UiBuilder.PrimaryButton(
                    "Apply Deck Fix JSON",
                    MaterialIconKind.CheckDecagramOutline,
                    async () =>
                        await studio.ApplyDeckComprehensiveAiFixJsonAsync(
                            pasteBox.Text ?? string.Empty
                        )
                )
            );
        }
        else
        {
            var (DisplayLabel, IsConfigured, MissingReason) = studio.GetActiveAiEngineStatus();
            var runBtn = UiBuilder.PrimaryButton(
                studio.IsGenerating
                    ? "Running Comprehensive AI Fix..."
                    : $"Run Comprehensive AI Fix ({DisplayLabel})",
                MaterialIconKind.AutoFix,
                async () => await studio.RunDeckComprehensiveAiFixWithApiAsync()
            );
            runBtn.IsEnabled = !studio.IsGenerating;
            modalStack.Children.Add(runBtn);
        }

        var modalCard = new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.AccentPrimary),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 14),
            MaxWidth = 760,
            MaxHeight = 680,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = modalStack,
            },
        };

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgBackdrop),
            Padding = new Thickness(24),
            Child = modalCard,
        };
    }

    private static Border BuildSingleQuestionAiFixOverlay(
        MainWindowViewModel vm,
        QuizStudioViewModel studio
    )
    {
        var deck = studio.OrganizerQuiz;
        var q = deck?.Questions.FirstOrDefault(x =>
            string.Equals(x.Id, studio.FlaggingQuestionId, StringComparison.OrdinalIgnoreCase)
        );
        if (deck is null || q is null)
            return new Border();

        int qIdx = deck.Questions.IndexOf(q);
        var modalGrid = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,*,Auto"),
            RowSpacing = 12,
        };

        // Row 0: Pinned Header
        var headerGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        headerGrid.Children.Add(
            new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Fix Question #{qIdx + 1} with AI",
                        FontSize = 15.5,
                        FontWeight = FontWeight.Bold,
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    },
                    new TextBlock
                    {
                        Text =
                            $"Context-Grounded Repair (PDF p.{Math.Max(1, q.SourcePage - 1)}–{q.SourcePage + 1}) · {q.TopicTag}",
                        FontSize = 11.5,
                        Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
                    },
                },
            }
        );
        var closeBtn = UiBuilder.GhostButton(
            "Close",
            MaterialIconKind.Close,
            studio.CloseQuestionAiFixModal
        );
        Grid.SetColumn(closeBtn, 1);
        headerGrid.Children.Add(closeBtn);
        modalGrid.Children.Add(headerGrid);

        // Row 1: Responsive Scrollable Body (Full Question View + Repair Inputs)
        var bodyStack = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 4, 0) };

        var previewStack = new StackPanel { Spacing = 8 };
        AppendQuestionPreviewContent(previewStack, studio, q);
        if (!string.IsNullOrWhiteSpace(q.Explanation))
        {
            previewStack.Children.Add(
                new TextBlock
                {
                    Text =
                        $"Explanation: {QuizRichContentRenderer.FormatInlineMath(q.Explanation)}",
                    FontSize = 11.5,
                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    TextWrapping = TextWrapping.Wrap,
                }
            );
        }
        bodyStack.Children.Add(
            new Border
            {
                Background = ThemeService.Brush(ThemeService.BgElevated),
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10),
                Child = previewStack,
            }
        );

        var issueBox = new TextBox
        {
            Text = studio.FlagProblemDescription,
            Watermark =
                "Describe the issue (e.g. wrong answer, weak distractors, add code/diagram snippet, re-ground quote)...",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 56,
            FontSize = 12,
        };
        issueBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                studio.FlagProblemDescription = issueBox.Text ?? string.Empty;
        };
        bodyStack.Children.Add(issueBox);

        var fixJsonBox = new TextBox
        {
            Text = studio.FlagFixJsonInput,
            Watermark = "Or paste repaired single-question JSON here (for Web AI workflow)...",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 76,
            FontSize = 11.5,
            FontFamily = new FontFamily("Consolas, JetBrains Mono, monospace"),
        };
        fixJsonBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                studio.FlagFixJsonInput = fixJsonBox.Text ?? string.Empty;
        };
        bodyStack.Children.Add(fixJsonBox);

        var bodyScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = bodyStack,
        };
        Grid.SetRow(bodyScroll, 1);
        modalGrid.Children.Add(bodyScroll);

        // Row 2: Pinned Responsive Footer Action Bar
        var (DisplayLabel, IsConfigured, MissingReason) = studio.GetActiveAiEngineStatus();
        var footerGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 8,
        };

        var leftActions = new WrapPanel { Orientation = Orientation.Horizontal };
        var apiFixBtn = UiBuilder.PrimaryButton(
            studio.IsGenerating ? "Repairing..." : $"Fix via AI ({DisplayLabel})",
            MaterialIconKind.AutoFix,
            async () =>
                await studio.RunSingleQuestionAiFixWithApiAsync(q, issueBox.Text ?? string.Empty)
        );
        apiFixBtn.IsEnabled = !studio.IsGenerating;
        apiFixBtn.Margin = new Thickness(0, 2, 6, 2);
        leftActions.Children.Add(apiFixBtn);

        var copyPromptBtn = UiBuilder.GhostButton(
            "Copy Context Prompt (Web AI)",
            MaterialIconKind.ContentCopy,
            async () =>
            {
                var top = TopLevel.GetTopLevel(modalGrid);
                var issueText = issueBox.Text ?? string.Empty;
                var prompt = await Task.Run(() =>
                    QuizExportService.BuildSingleQuestionFixPrompt(deck, q, issueText)
                );
                if (top?.Clipboard is not null)
                    await top.Clipboard.SetTextAsync(prompt);
            },
            isActive: !string.IsNullOrWhiteSpace(studio.FlagFixPromptPreview)
        );
        copyPromptBtn.Margin = new Thickness(0, 2, 6, 2);
        leftActions.Children.Add(copyPromptBtn);
        footerGrid.Children.Add(leftActions);

        var rightActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        rightActions.Children.Add(
            UiBuilder.GhostButton("Cancel", null, studio.CloseQuestionAiFixModal)
        );
        rightActions.Children.Add(
            UiBuilder.PrimaryButton(
                "Apply Pasted JSON",
                MaterialIconKind.Check,
                async () => await studio.ApplyQuestionAiFixAsync(q, fixJsonBox.Text ?? string.Empty)
            )
        );
        Grid.SetColumn(rightActions, 1);
        footerGrid.Children.Add(rightActions);

        Grid.SetRow(footerGrid, 2);
        modalGrid.Children.Add(footerGrid);

        var modalCard = new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.AccentPrimary),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 14),
            MinWidth = 440,
            MaxWidth = 760,
            MaxHeight = 680,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Child = modalGrid,
        };

        var backdrop = new Border
        {
            Background = ThemeService.Brush(ThemeService.BgBackdrop),
            Padding = new Thickness(32, 24),
            Child = modalCard,
        };
        backdrop.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.Source, backdrop))
            {
                e.Handled = true;
                studio.CloseQuestionAiFixModal();
            }
        };
        return backdrop;
    }

    private static void AppendQuestionPreviewContent(
        StackPanel targetStack,
        QuizStudioViewModel studio,
        QuizQuestionRecord q
    )
    {
        targetStack.Children.Add(
            new TextBlock
            {
                Text = QuizRichContentRenderer.FormatInlineMath(q.Prompt),
                FontSize = 13.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 19,
            }
        );

        if (!string.IsNullOrWhiteSpace(q.CodeSnippet))
            targetStack.Children.Add(
                QuizRichContentRenderer.BuildCodeSnippetCard(q.CodeSnippet, q.CodeLanguage)
            );
        if (!string.IsNullOrWhiteSpace(q.MathLatex))
            targetStack.Children.Add(QuizRichContentRenderer.BuildMathLatexCard(q.MathLatex));
        if (!string.IsNullOrWhiteSpace(q.Diagram))
            targetStack.Children.Add(QuizRichContentRenderer.BuildDiagramCard(q.Diagram));

        var resolvedImg = studio.ResolveQuestionImageFullPath(q.ImagePath);
        if (!string.IsNullOrWhiteSpace(resolvedImg))
            targetStack.Children.Add(
                QuizRichContentRenderer.BuildQuestionImageCard(
                    resolvedImg,
                    () => studio.OpenImageZoom(q.ImagePath)
                )
            );

        if (
            q.Kind
                is QuizQuestionKind.MultipleChoice
                    or QuizQuestionKind.Situational
                    or QuizQuestionKind.TrueFalse
            && q.Options.Count > 0
        )
        {
            int rowCount = (int)Math.Ceiling(q.Options.Count / 2.0);
            var optGrid = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,*"),
                ColumnSpacing = 8,
                RowSpacing = 5,
            };
            for (int r = 0; r < rowCount; r++)
                optGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            for (int oi = 0; oi < q.Options.Count; oi++)
            {
                var optText = q.Options[oi];
                bool isCorrectOpt = q.EvaluateAnswer(optText);
                var optBorder = new Border
                {
                    Background = isCorrectOpt
                        ? ThemeService.Brush(ThemeService.SuccessBg)
                        : ThemeService.Brush(ThemeService.BgBase),
                    BorderBrush = isCorrectOpt
                        ? ThemeService.Brush(ThemeService.SuccessFg)
                        : ThemeService.Brush(ThemeService.BorderSubtle),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(9, 6),
                    Child = new Grid
                    {
                        ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
                        ColumnSpacing = 6,
                        Children =
                        {
                            UiBuilder.Icon(
                                isCorrectOpt
                                    ? MaterialIconKind.CheckCircle
                                    : MaterialIconKind.CircleOutline,
                                13,
                                isCorrectOpt
                                    ? ThemeService.Brush(ThemeService.SuccessFg)
                                    : ThemeService.Brush(ThemeService.TextMuted)
                            ),
                            new TextBlock
                            {
                                Text = QuizRichContentRenderer.FormatInlineMath(optText),
                                FontSize = 12,
                                FontWeight = isCorrectOpt ? FontWeight.SemiBold : FontWeight.Normal,
                                Foreground = isCorrectOpt
                                    ? ThemeService.Brush(ThemeService.SuccessFg)
                                    : ThemeService.Brush(ThemeService.TextSecondary),
                                TextWrapping = TextWrapping.Wrap,
                                VerticalAlignment = VerticalAlignment.Center,
                                [Grid.ColumnProperty] = 1,
                            },
                        },
                    },
                };
                Grid.SetRow(optBorder, oi / 2);
                Grid.SetColumn(optBorder, oi % 2);
                optGrid.Children.Add(optBorder);
            }
            targetStack.Children.Add(optGrid);
        }
        else if (q.Kind == QuizQuestionKind.Ordering && q.Options.Count > 0)
        {
            var seqWrap = new WrapPanel { Orientation = Orientation.Horizontal };
            for (int si = 0; si < q.Options.Count; si++)
            {
                var pill = UiBuilder.PillBadge(
                    $"{si + 1}. {q.Options[si]}",
                    ThemeService.Brush(ThemeService.AccentPrimary),
                    ThemeService.Brush(ThemeService.AccentSoftBg)
                );
                pill.Margin = new Thickness(0, 0, 5, 3);
                seqWrap.Children.Add(pill);
            }
            targetStack.Children.Add(seqWrap);
        }
        else
        {
            var ansWrap = new WrapPanel { Orientation = Orientation.Horizontal };
            var ansBadge = UiBuilder.PillBadge(
                $"Answer: {q.CorrectAnswer}",
                ThemeService.Brush(ThemeService.SuccessFg),
                ThemeService.Brush(ThemeService.SuccessBg)
            );
            ansBadge.Margin = new Thickness(0, 0, 5, 3);
            ansWrap.Children.Add(ansBadge);

            foreach (var al in q.AcceptedAliases)
            {
                var aliasPill = UiBuilder.PillBadge(
                    $"Alias: {al}",
                    ThemeService.Brush(ThemeService.TextSecondary),
                    ThemeService.Brush(ThemeService.BgElevated)
                );
                aliasPill.Margin = new Thickness(0, 0, 5, 3);
                ansWrap.Children.Add(aliasPill);
            }
            targetStack.Children.Add(ansWrap);
        }

        if (!string.IsNullOrWhiteSpace(q.Hint))
        {
            targetStack.Children.Add(
                new TextBlock
                {
                    Text = $"Hint: {q.Hint}",
                    FontSize = 11.5,
                    Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                    TextWrapping = TextWrapping.Wrap,
                }
            );
        }

        if (!string.IsNullOrWhiteSpace(q.Explanation))
        {
            targetStack.Children.Add(
                new Border
                {
                    Background = ThemeService.Brush(ThemeService.BgBase),
                    BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(9, 6),
                    Child = BuildContrastiveExplanationView(q.Explanation, fontSize: 11.8),
                }
            );
        }

        var criticIssues = studio.GetCachedOrganizerQualityReport().GetIssuesFor(q.Id);
        if (criticIssues.Count > 0)
        {
            targetStack.Children.Add(BuildOrganizerCriticCallout(studio, q, criticIssues));
        }
    }

    private sealed class PdfVisualSnipperControl : Control
    {
        private static readonly IBrush ExistingFill = new SolidColorBrush(Color.Parse("#48F59E0B"));
        private static readonly IPen ExistingBorder = new Pen(
            new SolidColorBrush(Color.Parse("#E6D97706")),
            1.6
        );
        private static readonly IBrush DragFill = new SolidColorBrush(Color.Parse("#385E81F4"));
        private static readonly IPen DragBorder = new Pen(
            new SolidColorBrush(Color.Parse("#FF5E81F4")),
            1.8
        );

        private readonly IReadOnlyList<NormalizedHighlightRect> _existingRects;
        private readonly Func<NormalizedHighlightRect, bool, Task> _onRegionSelected;
        private Point? _dragStart;
        private Point? _dragEnd;

        public PdfVisualSnipperControl(
            IReadOnlyList<NormalizedHighlightRect> existingRects,
            Func<NormalizedHighlightRect, bool, Task> onRegionSelected
        )
        {
            _existingRects = existingRects;
            _onRegionSelected = onRegionSelected;
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Cross);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            double w = Bounds.Width;
            double h = Bounds.Height;
            if (w <= 1 || h <= 1)
                return;

            // Hit-test surface
            context.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h));

            foreach (var r in _existingRects)
            {
                var rect = new Rect(
                    r.X * w,
                    r.Y * h,
                    Math.Max(6, r.Width * w),
                    Math.Max(8, r.Height * h)
                );
                context.DrawRectangle(ExistingFill, ExistingBorder, rect, 3, 3);
            }

            if (_dragStart.HasValue && _dragEnd.HasValue)
            {
                var dragRect = MakeClampedRect(_dragStart.Value, _dragEnd.Value, w, h);
                context.DrawRectangle(DragFill, DragBorder, dragRect, 2, 2);
            }
        }

        protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;
            _dragStart = e.GetPosition(this);
            _dragEnd = _dragStart;
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
        }

        protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_dragStart.HasValue)
                return;
            _dragEnd = e.GetPosition(this);
            InvalidateVisual();
        }

        protected override void OnPointerReleased(Avalonia.Input.PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_dragStart.HasValue)
                return;

            var start = _dragStart.Value;
            var end = e.GetPosition(this);
            _dragStart = null;
            _dragEnd = null;
            e.Pointer.Capture(null);
            InvalidateVisual();

            double w = Bounds.Width;
            double h = Bounds.Height;
            if (w <= 1 || h <= 1)
                return;

            var rect = MakeClampedRect(start, end, w, h);
            if (rect.Width < 8 || rect.Height < 8)
                return;

            var norm = new NormalizedHighlightRect(
                X: Math.Clamp(rect.X / w, 0.0, 1.0),
                Y: Math.Clamp(rect.Y / h, 0.0, 1.0),
                Width: Math.Clamp(rect.Width / w, 0.01, 1.0),
                Height: Math.Clamp(rect.Height / h, 0.01, 1.0)
            );
            bool additive = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift);
            _ = _onRegionSelected(norm, additive);
        }

        private static Rect MakeClampedRect(Point a, Point b, double maxW, double maxH)
        {
            double x1 = Math.Clamp(Math.Min(a.X, b.X), 0, maxW);
            double y1 = Math.Clamp(Math.Min(a.Y, b.Y), 0, maxH);
            double x2 = Math.Clamp(Math.Max(a.X, b.X), 0, maxW);
            double y2 = Math.Clamp(Math.Max(a.Y, b.Y), 0, maxH);
            return new Rect(x1, y1, x2 - x1, y2 - y1);
        }
    }

    private static StackPanel BuildSnipBookmarkTreeNode(
        QuizStudioViewModel studio,
        PdfBookmarkNode node,
        PdfBookmarkNode? activeNode
    )
    {
        bool isCurrent = ReferenceEquals(node, activeNode);
        bool hasChildren = node.Children.Count > 0;
        var container = new StackPanel { Spacing = 1 };
        var rowGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("20,*,Auto") };

        if (hasChildren)
        {
            var chevronBtn = new Button
            {
                Content = UiBuilder.Icon(
                    node.IsExpanded ? MaterialIconKind.ChevronDown : MaterialIconKind.ChevronRight,
                    14,
                    ThemeService.Brush(ThemeService.TextSecondary)
                ),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(2),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            chevronBtn.Click += (_, e) =>
            {
                e.Handled = true;
                studio.ToggleSnipOutlineNodeExpanded(node);
            };
            rowGrid.Children.Add(chevronBtn);
        }
        else
        {
            var dot = UiBuilder.Icon(
                MaterialIconKind.FileDocumentOutline,
                12,
                isCurrent
                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : ThemeService.Brush(ThemeService.TextMuted)
            );
            dot.HorizontalAlignment = HorizontalAlignment.Center;
            rowGrid.Children.Add(dot);
        }

        var normalBg = isCurrent
            ? ThemeService.Brush(ThemeService.AccentSoftBg)
            : Brushes.Transparent;
        var hoverBg = isCurrent
            ? ThemeService.Brush(ThemeService.AccentSoftBg)
            : ThemeService.Brush(ThemeService.BgHover);

        var titleBtn = new Button
        {
            Content = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                Children =
                {
                    new TextBlock
                    {
                        Text = node.Title,
                        FontSize = node.Level == 1 ? 12.5 : 12,
                        FontWeight =
                            isCurrent || node.Level == 1 ? FontWeight.SemiBold : FontWeight.Normal,
                        Foreground =
                            isCurrent ? ThemeService.Brush(ThemeService.AccentPrimary)
                            : node.Level == 1 ? ThemeService.Brush(ThemeService.TextPrimary)
                            : ThemeService.Brush(ThemeService.TextSecondary),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = node.PageNumber.ToString(),
                        FontSize = 11,
                        Foreground = isCurrent
                            ? ThemeService.Brush(ThemeService.AccentPrimary)
                            : ThemeService.Brush(ThemeService.TextMuted),
                        Margin = new Thickness(6, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        [Grid.ColumnProperty] = 1,
                    },
                },
            },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 4.5, 6, 4.5),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        titleBtn.Click += async (_, _) => await studio.LoadEditorSnipPageAsync(node.PageNumber);
        Grid.SetColumn(titleBtn, 1);
        rowGrid.Children.Add(titleBtn);

        var rowBorder = new Border
        {
            Background = normalBg,
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(Math.Max(0, node.Level - 1) * 12, 0, 0, 0),
            Child = rowGrid,
        };
        rowBorder.PointerEntered += (_, _) => rowBorder.Background = hoverBg;
        rowBorder.PointerExited += (_, _) => rowBorder.Background = normalBg;
        container.Children.Add(rowBorder);

        if (hasChildren && node.IsExpanded)
        {
            foreach (var child in node.Children)
                container.Children.Add(BuildSnipBookmarkTreeNode(studio, child, activeNode));
        }
        return container;
    }
}
