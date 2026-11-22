using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views;

/// <summary>
/// 100% pure C# Todoist + Obsidian Desktop Window (zero .axaml, zero emojis).
/// Features a responsive collapsible sidebar, low-noise hairline task rows, a contextual slide-over
/// Task Inspector drawer with rendered Markdown, a floating centered Todoist Quick-Add modal (Q),
/// and a floating Quick-Search &amp; Navigation command palette (/ or Ctrl+K).
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm;

    private readonly Grid _rootOverlayGrid;
    private readonly Grid _shellGrid;
    private readonly Border _sidebarContainer = new();
    private readonly Border _centerBodyContainer = new();
    private readonly Border _inspectorContainer = new();
    private readonly Border _statusBarContainer = new();
    private readonly Border _modalOverlayContainer = new() { IsVisible = false };

    private readonly TextBox _quickAddBox;
    private StackPanel _quickAddChipsContainer = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
    };
    private readonly TextBox _searchBox;
    private StackPanel _searchResultsStack = new() { Spacing = 2 };
    private WrapPanel _searchFilterBar = new() { Orientation = Orientation.Horizontal };

    private readonly Dictionary<
        string,
        (Border Row, TextBlock TitleBlock, StackPanel HoverActions)
    > _taskRowsById = new(StringComparer.Ordinal);
    private string? _lastHighlightedTaskId;
    private string _mountedModalKind = string.Empty;
    private bool _lastRenderedIsSyncing;
    private bool _suppressTextCallbacks;
    private bool _lastAutoSidebarCollapse;
    private Vector _sidebarScrollOffset;
    private Vector _taskListScrollOffset;

    public MainWindow(MainWindowViewModel vm)
    {
        _vm = vm;
        Title = "ScholarFlow";
        Width = 1360;
        Height = 860;
        MinWidth = 760;
        MinHeight = 560;

        _quickAddBox = new TextBox
        {
            Watermark = "Task name — e.g. Finish SQA lab 2 tomorrow p1 d4 #sqa @lab",
            FontSize = 14.5,
            FontWeight = FontWeight.Medium,
        };
        _quickAddBox.KeyDown += OnQuickAddKeyDown;
        _quickAddBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && !_suppressTextCallbacks)
                _vm.UpdateQuickAddText(_quickAddBox.Text ?? string.Empty);
        };

        _searchBox = new TextBox
        {
            Watermark = "Search tasks, courses (#sqa), or Master PDFs...",
            FontSize = 14,
        };
        _searchBox.KeyDown += OnSearchBoxKeyDown;
        _searchBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && !_suppressTextCallbacks)
                _vm.UpdateSearchQuery(_searchBox.Text ?? string.Empty);
        };

        _shellGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("225,*,0"),
            RowDefinitions = RowDefinitions.Parse("*,26"),
        };

        _shellGrid.Children.Add(_sidebarContainer);

        Grid.SetColumn(_centerBodyContainer, 1);
        _shellGrid.Children.Add(_centerBodyContainer);

        Grid.SetColumn(_inspectorContainer, 2);
        _shellGrid.Children.Add(_inspectorContainer);

        Grid.SetRow(_statusBarContainer, 1);
        Grid.SetColumnSpan(_statusBarContainer, 3);
        _shellGrid.Children.Add(_statusBarContainer);

        _rootOverlayGrid = new Grid();
        _rootOverlayGrid.Children.Add(_shellGrid);
        _rootOverlayGrid.Children.Add(_modalOverlayContainer);

        Content = _rootOverlayGrid;

        _vm.UiChanged += OnVmUiChanged;
        _vm.Calendar.Changed += (_, _) => RefreshCenterBodyAndInspector();
        _vm.PdfReader.Changed += (_, _) => RefreshCenterBodyAndInspector();
        ThemeService.ThemeChanged += (_, _) =>
        {
            _mountedModalKind = string.Empty;
            RebuildAllSections();
        };

        RebuildAllSections();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        bool shouldAutoCollapse = e.NewSize.Width < 860;
        if (shouldAutoCollapse != _lastAutoSidebarCollapse)
        {
            _lastAutoSidebarCollapse = shouldAutoCollapse;
            RefreshSidebar();
        }
        UpdateResponsiveShellColumns(e.NewSize.Width);
        if (_vm.IsInspectorOpen)
            RefreshOverlayModal();
    }

    private bool IsSidebarCurrentlyCompact =>
        _vm.IsSidebarCollapsed || Bounds.Width is > 0 and < 860;
    private bool ShouldDockInspectorInline =>
        _vm.IsInspectorOpen
        && _vm.ActiveMode
            is not (
                WorkspaceTabMode.PdfReader
                or WorkspaceTabMode.SourceViewer
                or WorkspaceTabMode.QuizStudio
                or WorkspaceTabMode.KnowledgeSearch
            )
        && (Bounds.Width <= 0 || Bounds.Width >= 1060);
    private bool ShouldFloatInspectorOverlay =>
        _vm.IsInspectorOpen
        && _vm.ActiveMode
            is not (
                WorkspaceTabMode.PdfReader
                or WorkspaceTabMode.SourceViewer
                or WorkspaceTabMode.QuizStudio
                or WorkspaceTabMode.KnowledgeSearch
            )
        && Bounds.Width is > 0 and < 1060;

    private void UpdateResponsiveShellColumns(double width)
    {
        int sideW = (_vm.IsSidebarCollapsed || (width is > 0 and < 860)) ? 54 : 225;
        int inspW =
            (
                _vm.IsInspectorOpen
                && _vm.ActiveMode
                    is not (
                        WorkspaceTabMode.PdfReader
                        or WorkspaceTabMode.SourceViewer
                        or WorkspaceTabMode.QuizStudio
                        or WorkspaceTabMode.KnowledgeSearch
                    )
                && (width <= 0 || width >= 1060)
            )
                ? 360
                : 0;

        _shellGrid.ColumnDefinitions = ColumnDefinitions.Parse($"{sideW},*,{inspW}");
        _inspectorContainer.IsVisible = inspW > 0;
    }

    private void OnVmUiChanged(object? sender, UiChangeKind kind)
    {
        switch (kind)
        {
            case UiChangeKind.QuickAddOnly:
                SyncTextBoxesFromVm();
                RefreshQuickAddChips();
                break;

            case UiChangeKind.StatusOnly:
                RefreshStatusBar();
                if (_lastRenderedIsSyncing != _vm.IsSyncing)
                    RefreshSidebar();
                break;

            case UiChangeKind.NotificationsOnly:
                RefreshSidebar();
                RefreshOverlayModal();
                break;

            case UiChangeKind.OverlayOnly:
                SyncTextBoxesFromVm();
                RefreshOverlayModal();
                break;

            case UiChangeKind.SelectionOnly:
                UpdateTaskSelectionInPlace();
                if (_vm.IsInspectorOpen)
                {
                    RefreshInspector();
                    if (ShouldFloatInspectorOverlay)
                        RefreshOverlayModal();
                }
                break;

            case UiChangeKind.CenterBodyOnly:
                RefreshCenterBodyAndInspector();
                break;

            case UiChangeKind.SidebarOnly:
                UpdateResponsiveShellColumns(Bounds.Width);
                RefreshSidebar();
                break;

            case UiChangeKind.SearchOrTaskList:
                SyncTextBoxesFromVm();
                if (_vm.IsSearchModalOpen)
                    RefreshSearchResultsList();
                else
                    RefreshCenterBodyAndInspector();
                break;

            default:
                RebuildAllSections();
                break;
        }
    }

    private void RebuildAllSections()
    {
        Background = ThemeService.Brush(ThemeService.BgBase);
        UpdateResponsiveShellColumns(Bounds.Width);
        SyncTextBoxesFromVm();
        RefreshSidebar();
        RefreshCenterBodyAndInspector();
        RefreshOverlayModal();
        RefreshStatusBar();
    }

    private void SyncTextBoxesFromVm()
    {
        _suppressTextCallbacks = true;
        try
        {
            if (
                !string.Equals(
                    _quickAddBox.Text ?? string.Empty,
                    _vm.QuickAddText,
                    StringComparison.Ordinal
                )
            )
            {
                _quickAddBox.Text = _vm.QuickAddText;
                _quickAddBox.CaretIndex = _quickAddBox.Text?.Length ?? 0;
            }

            if (
                !string.Equals(
                    _searchBox.Text ?? string.Empty,
                    _vm.SearchQuery,
                    StringComparison.Ordinal
                )
            )
            {
                _searchBox.Text = _vm.SearchQuery;
                _searchBox.CaretIndex = _searchBox.Text?.Length ?? 0;
            }
        }
        finally
        {
            _suppressTextCallbacks = false;
        }
    }

    private void RefreshSidebar()
    {
        _lastRenderedIsSyncing = _vm.IsSyncing;
        _sidebarContainer.Child = BuildSidebar();
    }

    private void RefreshCenterBodyAndInspector()
    {
        _taskRowsById.Clear();
        _lastHighlightedTaskId = _vm.SelectedTask?.Id;

        _centerBodyContainer.Child = _vm.ActiveMode switch
        {
            WorkspaceTabMode.Calendar => CalendarViewBuilder.Build(_vm),
            WorkspaceTabMode.PdfReader => PdfReaderViewBuilder.Build(_vm),
            WorkspaceTabMode.SourceViewer => SourceViewerViewBuilder.Build(_vm),
            WorkspaceTabMode.QuizStudio => QuizStudioViewBuilder.Build(_vm),
            WorkspaceTabMode.KnowledgeSearch => KnowledgeSearchViewBuilder.Build(_vm),
            WorkspaceTabMode.CourseWorkspace when _vm.ActiveCourse is not null =>
                CourseWorkspaceViewBuilder.Build(_vm, _vm.ActiveCourse, BuildTaskListView),
            _ => BuildTaskListView(_vm),
        };

        RefreshInspector();
    }

    private void RefreshInspector()
    {
        if (ShouldDockInspectorInline)
        {
            _inspectorContainer.Child = InspectorPaneBuilder.Build(_vm);
        }
        else
        {
            _inspectorContainer.Child = null;
        }
    }

    private void RefreshStatusBar()
    {
        _statusBarContainer.Child = BuildStatusBar();
    }

    private void UpdateTaskSelectionInPlace()
    {
        var newId = _vm.SelectedTask?.Id;
        if (
            _lastHighlightedTaskId is not null
            && _taskRowsById.TryGetValue(_lastHighlightedTaskId, out var prevRow)
        )
        {
            prevRow.Row.Background = Brushes.Transparent;
            prevRow.TitleBlock.FontWeight = FontWeight.Normal;
            prevRow.HoverActions.IsVisible = false;
        }

        if (newId is not null && _taskRowsById.TryGetValue(newId, out var nextRow))
        {
            nextRow.Row.Background = ThemeService.Brush(ThemeService.BgElevated);
            nextRow.TitleBlock.FontWeight = FontWeight.Medium;
            nextRow.HoverActions.IsVisible = true;
            nextRow.Row.BringIntoView();
        }

        _lastHighlightedTaskId = newId;
    }

    private static void DetachFromParent(Control control)
    {
        switch (control.Parent)
        {
            case Panel panel:
                panel.Children.Remove(control);
                break;
            case ContentControl contentControl
                when ReferenceEquals(contentControl.Content, control):
                contentControl.Content = null;
                break;
            case Decorator decorator when ReferenceEquals(decorator.Child, control):
                decorator.Child = null;
                break;
            case ContentPresenter presenter when ReferenceEquals(presenter.Content, control):
                presenter.Content = null;
                break;
        }
    }

    private void RefreshOverlayModal()
    {
        string targetKind =
            _vm.IsOnboardingOpen ? "Onboarding"
            : _vm.IsPacketOrganizerModalOpen ? "PacketOrganizer"
            : _vm.IsAddCourseModalOpen ? "AddCourse"
            : _vm.IsSettingsModalOpen ? "Settings"
            : _vm.IsQuickAddModalOpen ? "QuickAdd"
            : _vm.IsKnowledgeSearchModalOpen ? "KnowledgeSearch"
            : _vm.IsSearchModalOpen ? "Search"
            : _vm.IsNotificationDrawerOpen ? "Notifications"
            : _vm.IsShortcutsModalOpen ? "Shortcuts"
            : ShouldFloatInspectorOverlay ? "FloatingInspector"
            : string.Empty;

        if (string.IsNullOrEmpty(targetKind))
        {
            _mountedModalKind = string.Empty;
            _modalOverlayContainer.IsVisible = false;
            _modalOverlayContainer.Child = null;
            return;
        }

        // If QuickAdd, Search, or KnowledgeSearch modal is already mounted, update its inner dynamic list in-place
        if (targetKind == _mountedModalKind && _modalOverlayContainer.IsVisible)
        {
            if (targetKind == "QuickAdd")
            {
                RefreshQuickAddChips();
                return;
            }
            if (targetKind == "Search")
            {
                RefreshSearchResultsList();
                return;
            }
            if (targetKind == "KnowledgeSearch")
            {
                KnowledgeSearchViewBuilder.FocusQueryBox(selectAll: false);
                return;
            }
        }

        _mountedModalKind = targetKind;

        var backdrop = new Border
        {
            Background = ThemeService.Brush(ThemeService.BgBackdrop),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        backdrop.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.Source, backdrop))
            {
                e.Handled = true;
                _vm.CloseTopOverlayOrDrawer();
                Focus();
            }
        };

        if (_vm.IsOnboardingOpen)
        {
            backdrop.Child = Shell.OnboardingAndSettingsModalBuilder.BuildOnboardingModal(
                this,
                _vm
            );
            _modalOverlayContainer.Child = backdrop;
            _modalOverlayContainer.IsVisible = true;
            return;
        }

        if (_vm.IsPacketOrganizerModalOpen)
        {
            backdrop.Child = Shell.OnboardingAndSettingsModalBuilder.BuildPacketOrganizerModal(_vm);
            _modalOverlayContainer.Child = backdrop;
            _modalOverlayContainer.IsVisible = true;
            return;
        }

        if (_vm.IsAddCourseModalOpen)
        {
            backdrop.Child = Shell.OnboardingAndSettingsModalBuilder.BuildAddCourseModal(_vm);
            _modalOverlayContainer.Child = backdrop;
            _modalOverlayContainer.IsVisible = true;
            return;
        }

        if (_vm.IsSettingsModalOpen)
        {
            backdrop.Child = Shell.OnboardingAndSettingsModalBuilder.BuildSettingsModal(_vm);
            _modalOverlayContainer.Child = backdrop;
            _modalOverlayContainer.IsVisible = true;
            return;
        }

        if (_vm.IsQuickAddModalOpen)
        {
            backdrop.Child = BuildFloatingQuickAddModal();
            _modalOverlayContainer.Child = backdrop;
            _modalOverlayContainer.IsVisible = true;
            Dispatcher.UIThread.Post(() =>
            {
                _quickAddBox.Focus();
                _quickAddBox.CaretIndex = _quickAddBox.Text?.Length ?? 0;
            });
            return;
        }

        if (_vm.IsKnowledgeSearchModalOpen)
        {
            backdrop.Child = KnowledgeSearchViewBuilder.BuildFloatingModal(
                _vm,
                () =>
                {
                    _vm.CloseKnowledgeSearchModal();
                    Focus();
                }
            );
            _modalOverlayContainer.Child = backdrop;
            _modalOverlayContainer.IsVisible = true;
            KnowledgeSearchViewBuilder.FocusQueryBox(selectAll: true);
            return;
        }

        if (_vm.IsSearchModalOpen)
        {
            backdrop.Child = BuildFloatingSearchModal();
            _modalOverlayContainer.Child = backdrop;
            _modalOverlayContainer.IsVisible = true;
            RefreshSearchResultsList();
            Dispatcher.UIThread.Post(() =>
            {
                _searchBox.Focus();
                _searchBox.SelectAll();
            });
            return;
        }

        if (_vm.IsNotificationDrawerOpen)
        {
            backdrop.Child = BuildFloatingNotificationsModal();
            _modalOverlayContainer.Child = backdrop;
            _modalOverlayContainer.IsVisible = true;
            return;
        }

        if (_vm.IsShortcutsModalOpen)
        {
            backdrop.Child = BuildFloatingShortcutsModal();
            _modalOverlayContainer.Child = backdrop;
            _modalOverlayContainer.IsVisible = true;
            return;
        }

        if (ShouldFloatInspectorOverlay)
        {
            var drawer = InspectorPaneBuilder.Build(_vm);
            drawer.Width = Math.Min(360, Math.Max(280, Bounds.Width - 60));
            drawer.HorizontalAlignment = HorizontalAlignment.Right;
            drawer.VerticalAlignment = VerticalAlignment.Stretch;
            backdrop.Child = drawer;
            _modalOverlayContainer.Child = backdrop;
            _modalOverlayContainer.IsVisible = true;
        }
    }

    private async Task PromptOpenBasinFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Select Academic Basin Directory (e.g. ~/Documents/School-Files/YR4_01)",
                AllowMultiple = false,
            }
        );

        if (folders.Count > 0)
        {
            var localPath = folders[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(localPath))
                await _vm.OpenBasinAsync(localPath);
        }
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        await Shell.ShortcutDispatcher.HandleKeyDownAsync(this, _vm, e);
    }
}
