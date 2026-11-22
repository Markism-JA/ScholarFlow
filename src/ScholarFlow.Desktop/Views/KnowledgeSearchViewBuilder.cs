using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Material.Icons;
using ScholarFlow.Core.Knowledge;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views;

/// <summary>
/// Pure-C# Dedicated Knowledge Hub Search workspace (Ctrl+Shift+F).
/// Features an instant focus-preserving query bar, 1-click #course / @source / @weak filter chips,
/// a virtualized FTS5 results list with highlighted snippet spans, weak-spot badges, skeleton loading UI,
/// and 1-click Jump to Source &amp; Quiz This actions.
/// </summary>
public static class KnowledgeSearchViewBuilder
{
    private static readonly FontFamily MonoFont = new(
        "JetBrains Mono, Fira Code, Cascadia Code, Consolas, monospace"
    );

    private static TextBox? _activeQueryBox;
    private static Border? _activeTelemetryHost;
    private static Border? _activeFilterBarHost;
    private static Border? _activeResultsHost;
    private static ListBox? _activeResultsListBox;
    private static List<IndexedResultRow> _activeIndexedItems = [];
    private static readonly Dictionary<int, (Border Card, bool IsWeakSpot)> _renderedCardsByIndex = [];
    private static int _lastRenderedSelectedIndex = -1;
    private static Action? _activeOnClose;
    private static MainWindowViewModel? _boundVm;
    private static bool _suppressTextCallback;

    public static void FocusQueryBox(bool selectAll = false)
    {
        if (_activeQueryBox is null)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            _activeQueryBox.Focus();
            if (selectAll)
                _activeQueryBox.SelectAll();
            else
                _activeQueryBox.CaretIndex = _activeQueryBox.Text?.Length ?? 0;
        });
    }

    public static Border BuildFloatingModal(MainWindowViewModel vm, Action onClose)
    {
        var content = Build(vm, onClose, isFloatingModal: true);
        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderStrong),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            BoxShadow = BoxShadows.Parse("0 16 40 0 #66000000"),
            MaxWidth = 780,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(24, 56, 24, 24),
            Padding = new Thickness(14, 12),
            ClipToBounds = true,
            Child = content,
        };
    }

    public static Control Build(
        MainWindowViewModel vm,
        Action? onClose = null,
        bool isFloatingModal = false
    )
    {
        var ks = vm.KnowledgeSearch;
        _activeOnClose = onClose;

        if (!ReferenceEquals(_boundVm, vm))
        {
            if (_boundVm is not null)
            {
                _boundVm.KnowledgeSearch.ResultsUpdated -= OnKnowledgeSearchResultsUpdated;
                _boundVm.KnowledgeSearch.SelectionChanged -= OnKnowledgeSearchSelectionChanged;
            }
            _boundVm = vm;
            ks.ResultsUpdated += OnKnowledgeSearchResultsUpdated;
            ks.SelectionChanged += OnKnowledgeSearchSelectionChanged;
        }

        if (!ks.HasCompletedInitialIndex && !ks.IsIndexing)
        {
            ks.TriggerBackgroundIndex(forceReindex: false);
        }
        else if (ks.Results.Count == 0 && !ks.IsSearching)
        {
            _ = ks.ExecuteSearchNowAsync();
        }

        if (isFloatingModal)
        {
            return BuildSpotlightPaletteBody(vm, ks);
        }

        var root = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,Auto,Auto,*"),
            RowSpacing = 10,
            Margin = new Thickness(18, 14, 18, 14),
        };

        // 1. Header Bar + Telemetry + Reindex Button
        var headerBar = BuildHeaderBar(vm, ks, isFloatingModal: false, out var telemetryHost);
        _activeTelemetryHost = telemetryHost;
        root.Children.Add(headerBar);

        // 2. Query Input Box + Syntax Hints
        var queryCard = BuildQueryBarCard(ks, out var queryBox);
        _activeQueryBox = queryBox;
        Grid.SetRow(queryCard, 1);
        root.Children.Add(queryCard);

        // 3. Interactive Filter Chips (#course, @pdf, @code, @md, @text, @image, @quiz, @weak)
        var filterBarHost = new Border { Child = BuildFilterChipsPanel(vm, ks) };
        _activeFilterBarHost = filterBarHost;
        Grid.SetRow(filterBarHost, 2);
        root.Children.Add(filterBarHost);

        // 4. Virtualized Results Host (or Skeleton / Empty State)
        var resultsHost = new Border { Child = BuildResultsPane(ks) };
        _activeResultsHost = resultsHost;
        Grid.SetRow(resultsHost, 3);
        root.Children.Add(resultsHost);

        return root;
    }

    private static StackPanel BuildSpotlightPaletteBody(
        MainWindowViewModel vm,
        KnowledgeSearchViewModel ks
    )
    {
        var stack = new StackPanel { Spacing = 8 };

        // 1. Spotlight Search Input Row (matches Quick Search: [Magnify] [TextBox] [Clear] [Esc])
        var searchHeader = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,Auto"),
        };

        var icon = UiBuilder.Icon(
            MaterialIconKind.TextBoxSearchOutline,
            18,
            ThemeService.Brush(ThemeService.TextSecondary)
        );
        icon.Margin = new Thickness(4, 0, 8, 0);
        searchHeader.Children.Add(icon);

        var queryBox = CreateQueryTextBox(ks);
        _activeQueryBox = queryBox;
        Grid.SetColumn(queryBox, 1);
        searchHeader.Children.Add(queryBox);

        var clearBtn = UiBuilder.GhostButton(
            "Clear",
            MaterialIconKind.Close,
            () =>
            {
                ks.ClearAllFilters();
                FocusQueryBox();
            }
        );
        clearBtn.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(clearBtn, 2);
        searchHeader.Children.Add(clearBtn);

        var escChip = UiBuilder.KbdChip("Esc");
        escChip.Margin = new Thickness(8, 0, 0, 0);
        escChip.Cursor = new Cursor(StandardCursorType.Hand);
        escChip.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(escChip).Properties.IsLeftButtonPressed)
            {
                e.Handled = true;
                _activeOnClose?.Invoke();
            }
        };
        ToolTip.SetTip(escChip, "Close Knowledge Hub Search (Esc)");
        Grid.SetColumn(escChip, 3);
        searchHeader.Children.Add(escChip);

        stack.Children.Add(searchHeader);

        // 2. Compact Filter Chips (#course, @pdf, @code, @md, @text, @image, @quiz, @weak)
        var filterBarHost = new Border { Child = BuildFilterChipsPanel(vm, ks) };
        _activeFilterBarHost = filterBarHost;
        stack.Children.Add(filterBarHost);

        stack.Children.Add(UiBuilder.HairlineDivider());

        // 3. Scrollable Virtualized Results Viewport (bounded MaxHeight like Quick Search)
        var resultsHost = new Border { MaxHeight = 460, Child = BuildResultsPane(ks) };
        _activeResultsHost = resultsHost;
        stack.Children.Add(resultsHost);

        stack.Children.Add(UiBuilder.HairlineDivider());

        // 4. Quiet Footer Bar (keyboard hints on left, telemetry + Reindex on right)
        var footerGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 10,
        };

        footerGrid.Children.Add(
            new TextBlock
            {
                Text =
                    "Up/Down to navigate · Enter to jump · Shift+Enter to quiz · #course @pdf @code @weak · Esc to close",
                FontSize = 11,
                Foreground = ThemeService.Brush(ThemeService.TextMuted),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            }
        );

        var rightFooter = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var telemetryHost = new Border
        {
            VerticalAlignment = VerticalAlignment.Center,
            Child = BuildTelemetryPills(ks),
        };
        _activeTelemetryHost = telemetryHost;
        rightFooter.Children.Add(telemetryHost);

        var reindexBtn = UiBuilder.GhostButton(
            ks.IsIndexing ? "Indexing..." : "Reindex",
            MaterialIconKind.DatabaseRefreshOutline,
            () =>
            {
                ks.TriggerBackgroundIndex(forceReindex: true);
                FocusQueryBox();
            },
            isActive: ks.IsIndexing
        );
        ToolTip.SetTip(
            reindexBtn,
            "Force a full SQLite FTS5 reindex of all PDFs, code files, notes, images, and quizzes"
        );
        rightFooter.Children.Add(reindexBtn);

        Grid.SetColumn(rightFooter, 1);
        footerGrid.Children.Add(rightFooter);
        stack.Children.Add(footerGrid);

        return stack;
    }

    private static void OnKnowledgeSearchResultsUpdated(object? sender, EventArgs e)
    {
        if (_boundVm is null)
            return;

        var ks = _boundVm.KnowledgeSearch;

        if (_activeQueryBox is not null)
        {
            _suppressTextCallback = true;
            try
            {
                if (
                    !string.Equals(
                        _activeQueryBox.Text ?? string.Empty,
                        ks.QueryText,
                        StringComparison.Ordinal
                    )
                )
                {
                    _activeQueryBox.Text = ks.QueryText;
                    _activeQueryBox.CaretIndex = _activeQueryBox.Text?.Length ?? 0;
                }
            }
            finally
            {
                _suppressTextCallback = false;
            }
        }

        _activeTelemetryHost?.Child = BuildTelemetryPills(ks);
        _activeFilterBarHost?.Child = BuildFilterChipsPanel(_boundVm, ks);
        _activeResultsHost?.Child = BuildResultsPane(ks);
    }

    private static void OnKnowledgeSearchSelectionChanged(object? sender, EventArgs e)
    {
        if (_boundVm is null || _activeResultsListBox is null)
            return;

        var ks = _boundVm.KnowledgeSearch;
        int nextIdx = ks.SelectedResultIndex;
        int prevIdx = _lastRenderedSelectedIndex;

        if (prevIdx >= 0 && _renderedCardsByIndex.TryGetValue(prevIdx, out var prevEntry))
            ApplyCardSelectionVisual(prevEntry.Card, isSelected: false, prevEntry.IsWeakSpot);

        if (nextIdx >= 0 && _renderedCardsByIndex.TryGetValue(nextIdx, out var nextEntry))
            ApplyCardSelectionVisual(nextEntry.Card, isSelected: true, nextEntry.IsWeakSpot);

        _lastRenderedSelectedIndex = nextIdx;

        if (nextIdx >= 0 && nextIdx < _activeIndexedItems.Count)
        {
            var lb = _activeResultsListBox;
            var targetRow = _activeIndexedItems[nextIdx];
            lb.ScrollIntoView(targetRow);

            // Post once more on Loaded priority so virtualized items that just realized
            // get both scrolled fully into view and visually highlighted.
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (!ReferenceEquals(_activeResultsListBox, lb))
                        return;

                    lb.ScrollIntoView(targetRow);
                    if (
                        ks.SelectedResultIndex == nextIdx
                        && _renderedCardsByIndex.TryGetValue(nextIdx, out var realized)
                    )
                    {
                        ApplyCardSelectionVisual(
                            realized.Card,
                            isSelected: true,
                            realized.IsWeakSpot
                        );
                    }
                },
                DispatcherPriority.Loaded
            );
        }
    }

    private static void ApplyCardSelectionVisual(Border card, bool isSelected, bool isWeakSpot)
    {
        card.Background = isSelected
            ? ThemeService.Brush(ThemeService.BgElevated)
            : Brushes.Transparent;
        card.BorderBrush =
            isSelected ? ThemeService.Brush(ThemeService.TextSecondary)
            : isWeakSpot ? ThemeService.Brush(ThemeService.PriorityP2Fg)
            : Brushes.Transparent;
        card.BorderThickness = new Thickness(isSelected || isWeakSpot ? 2.5 : 0, 0, 0, 0);
    }

    private static Grid BuildHeaderBar(
        MainWindowViewModel vm,
        KnowledgeSearchViewModel ks,
        bool isFloatingModal,
        out Border telemetryHost
    )
    {
        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 12,
        };

        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                UiBuilder.Icon(
                    MaterialIconKind.TextBoxSearchOutline,
                    20,
                    ThemeService.Brush(ThemeService.TextSecondary)
                ),
                new TextBlock
                {
                    Text = "Knowledge Hub Search",
                    FontSize = 17,
                    FontWeight = FontWeight.Bold,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    VerticalAlignment = VerticalAlignment.Center,
                },
                UiBuilder.KbdChip("Ctrl+Shift+F"),
            },
        };

        grid.Children.Add(titleRow);

        var rightBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };

        telemetryHost = new Border
        {
            VerticalAlignment = VerticalAlignment.Center,
            Child = BuildTelemetryPills(ks),
        };
        rightBar.Children.Add(telemetryHost);

        var reindexBtn = UiBuilder.GhostButton(
            ks.IsIndexing ? "Indexing..." : "Reindex",
            MaterialIconKind.DatabaseRefreshOutline,
            () => ks.TriggerBackgroundIndex(forceReindex: true),
            isActive: ks.IsIndexing
        );
        ToolTip.SetTip(
            reindexBtn,
            "Force a full SQLite FTS5 reindex of all PDFs, code files, notes, images, and quizzes"
        );
        rightBar.Children.Add(reindexBtn);

        if (isFloatingModal)
        {
            var closeBtn = UiBuilder.GhostButton(
                "Esc",
                MaterialIconKind.Close,
                () => _activeOnClose?.Invoke()
            );
            ToolTip.SetTip(closeBtn, "Close Knowledge Hub Search (Esc)");
            rightBar.Children.Add(closeBtn);
        }

        Grid.SetColumn(rightBar, 1);
        grid.Children.Add(rightBar);
        return grid;
    }

    private static StackPanel BuildTelemetryPills(KnowledgeSearchViewModel ks)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (ks.Results.Count > 0 || ks.ParsedQuery.HasAnyFilterOrQuery)
        {
            var countLabel =
                ks.TotalCandidates > ks.Results.Count
                    ? $"{ks.Results.Count} of {ks.TotalCandidates} hits · {ks.LastSearchElapsedMs:F1} ms"
                    : $"{ks.Results.Count} hit(s) · {ks.LastSearchElapsedMs:F1} ms";

            row.Children.Add(
                UiBuilder.Pill(
                    countLabel,
                    ThemeService.Brush(ThemeService.TextPrimary),
                    ThemeService.Brush(ThemeService.BgElevated),
                    MaterialIconKind.FlashOutline,
                    fontSize: 10.5
                )
            );
        }

        row.Children.Add(
            UiBuilder.Pill(
                ks.IndexStatusText,
                ks.IsIndexing
                    ? ThemeService.Brush(ThemeService.PriorityP2Fg)
                    : ThemeService.Brush(ThemeService.TextSecondary),
                ks.IsIndexing
                    ? ThemeService.Brush(ThemeService.PriorityP2Bg)
                    : ThemeService.Brush(ThemeService.BgElevated),
                ks.IsIndexing ? MaterialIconKind.Sync : MaterialIconKind.DatabaseCheckOutline,
                fontSize: 10.5
            )
        );

        return row;
    }

    private static TextBox CreateQueryTextBox(KnowledgeSearchViewModel ks)
    {
        var queryBox = new TextBox
        {
            Text = ks.QueryText,
            Watermark =
                "Search PDF pages, code symbols, notes, or quizzes — e.g. #os @pdf \"page fault\" @weak",
            FontSize = 14,
            FontWeight = FontWeight.Medium,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        queryBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && !_suppressTextCallback)
            {
                ks.UpdateQuery(queryBox.Text ?? string.Empty);
            }
        };

        queryBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Down)
            {
                e.Handled = true;
                ks.MoveSelection(+1);
            }
            else if (e.Key == Key.Up)
            {
                e.Handled = true;
                ks.MoveSelection(-1);
            }
            else if (e.Key == Key.PageDown)
            {
                e.Handled = true;
                ks.MoveSelection(+5);
            }
            else if (e.Key == Key.PageUp)
            {
                e.Handled = true;
                ks.MoveSelection(-5);
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    await ks.QuizThisAsync();
                else
                    ks.JumpToSource();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (_activeOnClose is not null)
                    _activeOnClose();
                else
                    ks.ClearAllFilters();
            }
        };

        return queryBox;
    }

    private static Border BuildQueryBarCard(KnowledgeSearchViewModel ks, out TextBox queryBox)
    {
        var cardGrid = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,Auto"),
            RowSpacing = 6,
        };

        var inputRow = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
            ColumnSpacing = 8,
        };

        inputRow.Children.Add(
            UiBuilder.Icon(
                MaterialIconKind.Magnify,
                18,
                ThemeService.Brush(ThemeService.TextSecondary)
            )
        );

        queryBox = CreateQueryTextBox(ks);

        Grid.SetColumn(queryBox, 1);
        inputRow.Children.Add(queryBox);

        var clearBtn = UiBuilder.GhostButton("Clear", MaterialIconKind.Close, ks.ClearAllFilters);
        Grid.SetColumn(clearBtn, 2);
        inputRow.Children.Add(clearBtn);

        cardGrid.Children.Add(inputRow);

        // Syntax quick-reference strip
        var syntaxStrip = new WrapPanel { Orientation = Orientation.Horizontal };

        void AddSyntaxHint(string token, string desc)
        {
            var sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    UiBuilder.KbdChip(token),
                    new TextBlock
                    {
                        Text = desc,
                        FontSize = 11,
                        Foreground = ThemeService.Brush(ThemeService.TextMuted),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            };
            syntaxStrip.Children.Add(sp);
        }

        AddSyntaxHint("#course", "Course filter");
        AddSyntaxHint("@pdf @code @md @image @quiz", "Source filter");
        AddSyntaxHint("@topic", "Topic / symbol tag");
        AddSyntaxHint("@weak", "Quiz weak spots");
        AddSyntaxHint("\"phrase\"", "Exact match");
        AddSyntaxHint("Enter / Shift+Enter", "Jump / Quiz This");

        Grid.SetRow(syntaxStrip, 1);
        cardGrid.Children.Add(syntaxStrip);

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderStrong),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8),
            Child = cardGrid,
        };
    }

    private static StackPanel BuildFilterChipsPanel(
        MainWindowViewModel vm,
        KnowledgeSearchViewModel ks
    )
    {
        var stack = new StackPanel { Spacing = 6 };

        // Row 1: Source Kind Filters + Weak Spot Toggle
        var sourceWrap = new WrapPanel { Orientation = Orientation.Horizontal };

        void AddSourceChip(string label, MaterialIconKind icon, KnowledgeSourceFilterKind? kind)
        {
            bool active =
                ks.SelectedSourceFilter == kind
                || (kind.HasValue && ks.ParsedQuery.SourceFilters.Contains(kind.Value));
            var btn = UiBuilder.GhostButton(
                label,
                icon,
                () =>
                {
                    ks.SetSourceFilter(kind);
                    FocusQueryBox();
                },
                isActive: active
            );
            btn.Margin = new Thickness(0, 0, 4, 2);
            sourceWrap.Children.Add(btn);
        }

        AddSourceChip("All", MaterialIconKind.LayersOutline, null);
        AddSourceChip("PDF", MaterialIconKind.FilePdfBox, KnowledgeSourceFilterKind.Pdf);
        AddSourceChip("Code", MaterialIconKind.CodeBraces, KnowledgeSourceFilterKind.Code);
        AddSourceChip(
            "Notes",
            MaterialIconKind.LanguageMarkdownOutline,
            KnowledgeSourceFilterKind.Markdown
        );
        AddSourceChip(
            "Text",
            MaterialIconKind.FileDocumentOutline,
            KnowledgeSourceFilterKind.PlainText
        );
        AddSourceChip(
            "Images",
            MaterialIconKind.FileImageOutline,
            KnowledgeSourceFilterKind.Image
        );
        AddSourceChip(
            "Quizzes",
            MaterialIconKind.HeadLightbulbOutline,
            KnowledgeSourceFilterKind.Quiz
        );

        bool weakActive = ks.OnlyWeakSpots || ks.ParsedQuery.OnlyWeakSpots;
        var weakLabel =
            ks.WeakTopicCount > 0 ? $"Weak Spots ({ks.WeakTopicCount})" : "Weak Spots";
        var weakBtn = UiBuilder.GhostButton(
            weakLabel,
            MaterialIconKind.AlertRhombusOutline,
            () =>
            {
                ks.ToggleOnlyWeakSpots();
                FocusQueryBox();
            },
            isActive: weakActive,
            customFg: weakActive ? ThemeService.Brush(ThemeService.PriorityP2Fg) : null
        );
        weakBtn.Margin = new Thickness(4, 0, 0, 2);
        ToolTip.SetTip(
            weakBtn,
            "Filter or boost concepts and questions you recently missed or lapsed in Active Recall (@weak)"
        );
        sourceWrap.Children.Add(weakBtn);

        stack.Children.Add(sourceWrap);

        // Row 2: Course Filter Chips
        if (vm.Courses.Count > 0)
        {
            var courseWrap = new WrapPanel { Orientation = Orientation.Horizontal };

            bool allCoursesActive =
                string.IsNullOrWhiteSpace(ks.SelectedCourseFilter)
                && ks.ParsedQuery.CourseFilters.Count == 0;
            var allCourseBtn = UiBuilder.GhostButton(
                "All Courses",
                MaterialIconKind.FolderMultipleOutline,
                () =>
                {
                    ks.SetCourseFilter(null);
                    FocusQueryBox();
                },
                isActive: allCoursesActive
            );
            allCourseBtn.Margin = new Thickness(0, 0, 4, 2);
            courseWrap.Children.Add(allCourseBtn);

            foreach (var course in vm.Courses)
            {
                var alias = course.EffectiveAlias;
                bool active =
                    string.Equals(
                        ks.SelectedCourseFilter,
                        alias,
                        StringComparison.OrdinalIgnoreCase
                    )
                    || string.Equals(
                        ks.SelectedCourseFilter,
                        course.FolderName,
                        StringComparison.OrdinalIgnoreCase
                    )
                    || ks.ParsedQuery.CourseFilters.Any(cf =>
                        cf.Equals(alias, StringComparison.OrdinalIgnoreCase)
                        || course.FolderName.Contains(cf, StringComparison.OrdinalIgnoreCase)
                    );

                var cBtn = UiBuilder.GhostButton(
                    $"#{alias}",
                    null,
                    () =>
                    {
                        ks.SetCourseFilter(alias);
                        FocusQueryBox();
                    },
                    isActive: active
                );
                ToolTip.SetTip(cBtn, course.DisplayTitle);
                cBtn.Margin = new Thickness(0, 0, 4, 2);
                courseWrap.Children.Add(cBtn);
            }

            stack.Children.Add(courseWrap);
        }

        return stack;
    }

    private static Control BuildResultsPane(KnowledgeSearchViewModel ks)
    {
        _activeResultsListBox = null;
        _activeIndexedItems = [];
        _renderedCardsByIndex.Clear();
        _lastRenderedSelectedIndex = ks.SelectedResultIndex;

        if (ks.IsIndexing && !ks.HasCompletedInitialIndex && ks.Results.Count == 0)
        {
            return BuildIndexingSkeleton();
        }

        if (ks.Results.Count == 0)
        {
            return BuildEmptyState(ks);
        }

        var indexedItems = ks
            .Results.Select((item, idx) => new IndexedResultRow(idx, item))
            .ToList();
        _activeIndexedItems = indexedItems;

        var lb = new ListBox
        {
            ItemsSource = indexedItems,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Focusable = false,
            Padding = new Thickness(0, 2, 0, 18),
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<IndexedResultRow>(
                (row, _) =>
                {
                    if (row is null)
                        return new Border();
                    var card = BuildResultRowCard(ks, row.Index, row.Item);
                    _renderedCardsByIndex[row.Index] = (card, row.Item.IsWeakSpot);
                    return card;
                }
            ),
        };
        _activeResultsListBox = lb;

        lb.Styles.Add(
            new Style(x => x.OfType<ListBoxItem>())
            {
                Setters =
                {
                    new Setter(
                        Avalonia.Controls.Primitives.TemplatedControl.PaddingProperty,
                        new Thickness(0, 0, 0, 3)
                    ),
                    new Setter(Layoutable.MarginProperty, new Thickness(0)),
                    new Setter(
                        Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty,
                        Brushes.Transparent
                    ),
                    new Setter(
                        Avalonia.Controls.Primitives.TemplatedControl.BorderThicknessProperty,
                        new Thickness(0)
                    ),
                    new Setter(InputElement.FocusableProperty, false),
                },
            }
        );

        foreach (var pseudo in new[] { ":pointerover", ":selected", ":pressed" })
        {
            var captured = pseudo;
            lb.Styles.Add(
                new Style(x =>
                    x.OfType<ListBoxItem>()
                        .Class(captured)
                        .Template()
                        .OfType<ContentPresenter>()
                        .Name("PART_ContentPresenter")
                )
                {
                    Setters =
                    {
                        new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent),
                    },
                }
            );
        }

        return lb;
    }

    private sealed record IndexedResultRow(int Index, KnowledgeSearchResult Item);

    private static Border BuildResultRowCard(
        KnowledgeSearchViewModel ks,
        int index,
        KnowledgeSearchResult item
    )
    {
        bool isSelected = index == ks.SelectedResultIndex;
        var (kindIcon, kindLabel, kindFg, kindBg) = GetSourceVisualStyle(item.SourceKind);

        var outerGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 12,
        };

        var leftStack = new StackPanel { Spacing = 5 };

        // Top metadata row: Source badge, Title, Section/Symbol breadcrumb, Location pill, #course pill, Weak-spot badge
        var metaWrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        void AddMetaControl(Control c)
        {
            c.Margin = new Thickness(0, 0, 6, 3);
            metaWrap.Children.Add(c);
        }

        AddMetaControl(UiBuilder.Pill(kindLabel, kindFg, kindBg, kindIcon, fontSize: 10));

        AddMetaControl(
            new TextBlock
            {
                Text = item.Title,
                FontSize = 13.5,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );

        if (
            !string.IsNullOrWhiteSpace(item.SectionTitle)
            && !item.SectionTitle.Equals(item.Title, StringComparison.OrdinalIgnoreCase)
        )
        {
            AddMetaControl(
                new TextBlock
                {
                    Text = $"›  {item.SectionTitle}",
                    FontSize = 12.5,
                    FontWeight = FontWeight.Medium,
                    FontFamily = item.SourceKind == "code" ? MonoFont : FontFamily.Default,
                    Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );
        }

        AddMetaControl(
            UiBuilder.Pill(
                item.LocationLabel,
                ThemeService.Brush(ThemeService.TextSecondary),
                ThemeService.Brush(ThemeService.BgElevated),
                fontSize: 10
            )
        );

        if (!string.IsNullOrWhiteSpace(item.CourseAlias))
        {
            AddMetaControl(
                UiBuilder.Pill(
                    $"#{item.CourseAlias}",
                    ThemeService.Brush(ThemeService.TextSecondary),
                    ThemeService.Brush(ThemeService.BgElevated),
                    fontSize: 10
                )
            );
        }

        if (item.IsWeakSpot)
        {
            AddMetaControl(
                UiBuilder.Pill(
                    item.WeakSpotReason ?? "Weak Spot",
                    ThemeService.Brush(ThemeService.PriorityP2Fg),
                    ThemeService.Brush(ThemeService.PriorityP2Bg),
                    MaterialIconKind.AlertOutline,
                    fontSize: 10
                )
            );
        }

        leftStack.Children.Add(metaWrap);

        // Highlighted Context Snippet
        var snippetControl = BuildHighlightedSnippetControl(item);
        leftStack.Children.Add(snippetControl);

        outerGrid.Children.Add(leftStack);

        // Right-side 1-click actions: Jump to Source & Quiz This
        var actionsCol = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var jumpLabel = item.SourceKind switch
        {
            "pdf" => $"Open p.{item.PageNumber}",
            "code" or "md" or "text" when item.StartLine > 0 => $"Open L{item.StartLine}",
            "quiz" => "Open in Organizer",
            _ => "Jump to Source",
        };

        var jumpBtn = UiBuilder.GhostButton(
            jumpLabel,
            MaterialIconKind.OpenInApp,
            () => ks.JumpToSource(item),
            isActive: isSelected
        );
        ToolTip.SetTip(
            jumpBtn,
            "Jump directly to this exact page, line range, image, or quiz card (Enter)"
        );
        actionsCol.Children.Add(jumpBtn);

        var quizThisBtn = UiBuilder.GhostButton(
            "Quiz This",
            MaterialIconKind.HeadLightbulbOutline,
            async () => await ks.QuizThisAsync(item)
        );
        ToolTip.SetTip(
            quizThisBtn,
            "Pre-scope Test & Recall → Build to this exact PDF page or study file (Shift+Enter)"
        );
        actionsCol.Children.Add(quizThisBtn);

        Grid.SetColumn(actionsCol, 1);
        outerGrid.Children.Add(actionsCol);

        var normalBg = isSelected
            ? ThemeService.Brush(ThemeService.BgElevated)
            : Brushes.Transparent;
        var hoverBg = ThemeService.Brush(ThemeService.BgHover);
        var normalBorder =
            isSelected ? ThemeService.Brush(ThemeService.TextSecondary)
            : item.IsWeakSpot ? ThemeService.Brush(ThemeService.PriorityP2Fg)
            : Brushes.Transparent;

        var card = new Border
        {
            Background = normalBg,
            BorderBrush = normalBorder,
            BorderThickness = new Thickness(isSelected || item.IsWeakSpot ? 2.5 : 0, 0, 0, 0),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7),
            Cursor = new Cursor(StandardCursorType.Hand),
            Transitions = UiBuilder.CreateSmoothBrushTransitions(90),
            Child = outerGrid,
        };

        card.PointerEntered += (_, _) =>
        {
            if (index != ks.SelectedResultIndex)
                card.Background = hoverBg;
        };
        card.PointerExited += (_, _) =>
            card.Background =
                index == ks.SelectedResultIndex
                    ? ThemeService.Brush(ThemeService.BgElevated)
                    : Brushes.Transparent;
        card.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
            {
                e.Handled = true;
                ks.SelectResult(index);
                ks.JumpToSource(item);
            }
        };

        return card;
    }

    private static Control BuildHighlightedSnippetControl(KnowledgeSearchResult item)
    {
        bool isCode = item.SourceKind.Equals("code", StringComparison.OrdinalIgnoreCase);
        var tb = new TextBlock
        {
            FontSize = isCode ? 12 : 12.5,
            FontFamily = isCode ? MonoFont : FontFamily.Default,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = isCode ? 4 : 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = isCode ? 17.5 : 18.5,
        };

        PopulateHighlightedInlines(tb, item.HighlightedSnippet);

        if (!isCode)
            return tb;

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgBase),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 5),
            Child = tb,
        };
    }

    private static void PopulateHighlightedInlines(TextBlock tb, string snippet)
    {
        var inlines = tb.Inlines ??= [];
        inlines.Clear();

        if (string.IsNullOrEmpty(snippet))
            return;

        var normalFg = ThemeService.Brush(ThemeService.TextSecondary);
        var highlightFg = ThemeService.Brush(ThemeService.PriorityP2Fg);
        var highlightBg = ThemeService.Brush(ThemeService.PriorityP2Bg);

        int pos = 0;
        while (pos < snippet.Length)
        {
            int open = snippet.IndexOf('«', pos);
            if (open < 0)
            {
                inlines.Add(new Run(snippet[pos..]) { Foreground = normalFg });
                break;
            }

            if (open > pos)
            {
                inlines.Add(new Run(snippet[pos..open]) { Foreground = normalFg });
            }

            int close = snippet.IndexOf('»', open + 1);
            if (close < 0)
            {
                inlines.Add(new Run(snippet[(open + 1)..]) { Foreground = normalFg });
                break;
            }

            var matchedText = snippet[(open + 1)..close];
            if (matchedText.Length > 0)
            {
                inlines.Add(
                    new Run(matchedText)
                    {
                        Foreground = highlightFg,
                        Background = highlightBg,
                        FontWeight = FontWeight.Bold,
                    }
                );
            }

            pos = close + 1;
        }
    }

    private static (MaterialIconKind Icon, string Label, IBrush Fg, IBrush Bg) GetSourceVisualStyle(
        string sourceKind
    )
    {
        var calmFg = ThemeService.Brush(ThemeService.TextSecondary);
        var calmBg = ThemeService.Brush(ThemeService.BgElevated);
        return sourceKind.ToLowerInvariant() switch
        {
            "pdf" => (MaterialIconKind.FilePdfBox, "PDF", calmFg, calmBg),
            "code" => (MaterialIconKind.CodeBraces, "CODE", calmFg, calmBg),
            "md" => (MaterialIconKind.LanguageMarkdownOutline, "MD", calmFg, calmBg),
            "text" => (MaterialIconKind.FileDocumentOutline, "TEXT", calmFg, calmBg),
            "image" => (MaterialIconKind.FileImageOutline, "IMAGE", calmFg, calmBg),
            "quiz" => (
                MaterialIconKind.HeadLightbulbOutline,
                "QUIZ",
                ThemeService.Brush(ThemeService.SuccessFg),
                ThemeService.Brush(ThemeService.SuccessBg)
            ),
            _ => (MaterialIconKind.FileOutline, sourceKind.ToUpperInvariant(), calmFg, calmBg),
        };
    }

    private static StackPanel BuildIndexingSkeleton()
    {
        var stack = new StackPanel { Spacing = 10, Margin = new Thickness(0, 6, 0, 0) };
        for (int i = 0; i < 6; i++)
        {
            var cardStack = new StackPanel { Spacing = 8 };
            var topRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    BuildSkeletonBar(52, 18),
                    BuildSkeletonBar(180 + i % 3 * 45, 18),
                    BuildSkeletonBar(110, 18),
                    BuildSkeletonBar(64, 18),
                },
            };
            cardStack.Children.Add(topRow);
            cardStack.Children.Add(BuildSkeletonBar(520 + i % 2 * 120, 14));
            cardStack.Children.Add(BuildSkeletonBar(380 + i % 3 * 80, 14));

            stack.Children.Add(
                new Border
                {
                    Background = ThemeService.Brush(ThemeService.BgSurface),
                    BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(14, 12),
                    Child = cardStack,
                }
            );
        }
        return stack;
    }

    private static Border BuildSkeletonBar(double width, double height) =>
        new()
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(4),
            Background = ThemeService.Brush(ThemeService.BgElevated),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

    private static Border BuildEmptyState(KnowledgeSearchViewModel ks)
    {
        var center = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 540,
        };

        center.Children.Add(
            new Border
            {
                Width = 44,
                Height = 44,
                CornerRadius = new CornerRadius(22),
                Background = ThemeService.Brush(ThemeService.BgElevated),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = UiBuilder.Icon(
                    MaterialIconKind.TextBoxSearchOutline,
                    22,
                    ThemeService.Brush(ThemeService.TextSecondary)
                ),
            }
        );

        center.Children.Add(
            new TextBlock
            {
                Text = ks.ParsedQuery.HasAnyFilterOrQuery
                    ? "No matching knowledge chunks found"
                    : "Your Knowledge Hub index is ready",
                FontSize = 15,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                HorizontalAlignment = HorizontalAlignment.Center,
            }
        );

        center.Children.Add(
            new TextBlock
            {
                Text = ks.ParsedQuery.HasAnyFilterOrQuery
                    ? "Try relaxing a #course or @source filter, or click an example query below:"
                    : "Search across every PDF page, code symbol, Markdown section, image, and quiz question in milliseconds:",
                FontSize = 12.5,
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            }
        );

        var examplesWrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        foreach (
            var (label, query) in new[]
            {
                ("All PDFs (@pdf)", "@pdf"),
                ("All Code (@code)", "@code"),
                ("Markdown Notes (@md)", "@md"),
                ("Quiz Cards (@quiz)", "@quiz"),
                ("Weak Spots (@weak)", "@weak"),
            }
        )
        {
            var captured = query;
            var btn = UiBuilder.GhostButton(
                label,
                MaterialIconKind.Magnify,
                () =>
                {
                    ks.ApplyExampleQuery(captured);
                    FocusQueryBox();
                }
            );
            btn.Margin = new Thickness(3);
            examplesWrap.Children.Add(btn);
        }

        center.Children.Add(examplesWrap);

        return new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(16, 28),
            Child = center,
        };
    }
}
