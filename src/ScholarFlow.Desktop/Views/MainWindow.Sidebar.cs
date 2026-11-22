using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Tasks;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views;

/// <summary>
/// MainWindow partial: Responsive collapsible navigation sidebar.
/// </summary>
public sealed partial class MainWindow
{
    private bool _isCoursesSectionCollapsed;
    private bool _isTagsSectionCollapsed;

    private Border BuildSidebar(bool forceExpandedOverlay = false)
    {
        bool compact = !forceExpandedOverlay && IsSidebarCurrentlyCompact;
        var grid = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,*,Auto"),
            Margin = new Thickness(compact ? 6 : 10, 10),
        };

        // Top Basin Switcher & Collapse Toggle
        var header = new Grid
        {
            ColumnDefinitions = compact
                ? ColumnDefinitions.Parse("*")
                : ColumnDefinitions.Parse("*,Auto,Auto"),
            Margin = new Thickness(0, 0, 0, 8),
        };

        if (compact)
        {
            var compactHeaderStack = new StackPanel
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var expandBtn = UiBuilder.IconButton(
                MaterialIconKind.MenuOpen,
                HandleSidebarToggle,
                17,
                ThemeService.Brush(ThemeService.TextPrimary)
            );
            ToolTip.SetTip(
                expandBtn,
                IsWindowConstrainedForSidebar
                    ? "Open full sidebar drawer (Ctrl+B)"
                    : "Expand sidebar (Ctrl+B)"
            );
            compactHeaderStack.Children.Add(expandBtn);

            var compactBasinBtn = UiBuilder.IconButton(
                MaterialIconKind.FolderOutline,
                () => { },
                16,
                ThemeService.Brush(ThemeService.TextSecondary)
            );
            compactBasinBtn.Flyout = BuildBasinSwitcherFlyout();
            ToolTip.SetTip(
                compactBasinBtn,
                $"{_vm.BasinConfig.Name} ({_vm.ActiveProviderName}) — Switch or configure basin"
            );
            compactHeaderStack.Children.Add(compactBasinBtn);

            var compactBellIcon =
                _vm.UnreadNotificationsCount > 0
                    ? MaterialIconKind.BellBadgeOutline
                    : MaterialIconKind.BellOutline;
            var compactBellBtn = UiBuilder.IconButton(
                compactBellIcon,
                _vm.ToggleNotificationDrawer,
                16,
                _vm.UnreadNotificationsCount > 0
                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : ThemeService.Brush(ThemeService.TextSecondary)
            );
            ToolTip.SetTip(
                compactBellBtn,
                _vm.UnreadNotificationsCount > 0
                    ? $"Notifications ({_vm.UnreadNotificationsCount} unread)"
                    : "Notifications"
            );
            compactHeaderStack.Children.Add(compactBellBtn);

            header.Children.Add(compactHeaderStack);
        }
        else
        {
            var basinBtn = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 7,
                    Children =
                    {
                        UiBuilder.Icon(
                            MaterialIconKind.FolderOutline,
                            16,
                            ThemeService.Brush(ThemeService.AccentPrimary)
                        ),
                        new TextBlock
                        {
                            Text = string.IsNullOrEmpty(_vm.BasinConfig.Name)
                                ? "Basin"
                                : _vm.BasinConfig.Name,
                            FontSize = 13,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                            VerticalAlignment = VerticalAlignment.Center,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                    },
                },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Cursor = new Cursor(StandardCursorType.Hand),
                Padding = new Thickness(6, 5),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            basinBtn.PointerEntered += (_, _) =>
                basinBtn.Background = ThemeService.Brush(ThemeService.BgHover);
            basinBtn.PointerExited += (_, _) => basinBtn.Background = Brushes.Transparent;
            basinBtn.Flyout = BuildBasinSwitcherFlyout();
            ToolTip.SetTip(
                basinBtn,
                $"{_vm.BasinConfig.Name} ({_vm.ActiveProviderName}) — Click to switch or configure basin"
            );
            header.Children.Add(basinBtn);

            var bellIcon =
                _vm.UnreadNotificationsCount > 0
                    ? MaterialIconKind.BellBadgeOutline
                    : MaterialIconKind.BellOutline;
            var notifBtn = UiBuilder.IconButton(
                bellIcon,
                () =>
                {
                    if (forceExpandedOverlay)
                        SetCompactSidebarOverlayOpen(false);
                    _vm.ToggleNotificationDrawer();
                },
                15.5,
                _vm.UnreadNotificationsCount > 0
                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : ThemeService.Brush(ThemeService.TextSecondary)
            );
            ToolTip.SetTip(
                notifBtn,
                _vm.UnreadNotificationsCount > 0
                    ? $"Notifications ({_vm.UnreadNotificationsCount} unread)"
                    : "Notifications"
            );
            Grid.SetColumn(notifBtn, 1);
            header.Children.Add(notifBtn);

            var collapseBtn = UiBuilder.IconButton(
                forceExpandedOverlay ? MaterialIconKind.ChevronDoubleLeft : MaterialIconKind.DockLeft,
                forceExpandedOverlay
                    ? () => SetCompactSidebarOverlayOpen(false)
                    : HandleSidebarToggle,
                15.5,
                ThemeService.Brush(ThemeService.TextSecondary)
            );
            ToolTip.SetTip(
                collapseBtn,
                forceExpandedOverlay ? "Close sidebar drawer (←)" : "Collapse sidebar (Ctrl+B)"
            );
            Grid.SetColumn(collapseBtn, 2);
            header.Children.Add(collapseBtn);
        }

        grid.Children.Add(header);

        // Unified Scrollable Body: Quick Actions, Smart Views, Courses, and Tags (Todoist-style)
        var today = DateOnly.FromDateTime(DateTime.Today);
        var navScrollStack = new StackPanel { Spacing = 2 };

        // Todoist-style prominent "+ Add task" button at the top of the sidebar
        navScrollStack.Children.Add(
            BuildNavButton(
                "Add task",
                MaterialIconKind.PlusCircle,
                0,
                isActive: false,
                _vm.OpenQuickAddModal,
                compact: compact,
                customAccentFg: ThemeService.Brush(ThemeService.AccentPrimary)
            )
        );

        navScrollStack.Children.Add(
            BuildNavButton(
                "Search",
                MaterialIconKind.Magnify,
                0,
                isActive: false,
                _vm.OpenSearchModal,
                compact: compact,
                subtitleTag: "/"
            )
        );

        navScrollStack.Children.Add(
            BuildNavButton(
                "Knowledge Hub",
                MaterialIconKind.TextBoxSearchOutline,
                0,
                isActive: _vm.IsKnowledgeSearchModalOpen,
                () => _vm.OpenKnowledgeSearch(),
                compact: compact,
                subtitleTag: "Ctrl+Shift+F"
            )
        );

        int inboxCount =
            _vm.AllTasks.Count(t => !t.IsCompleted && t.IsInbox) + _vm.UnregisteredFiles.Count;
        int todayCount = _vm.AllTasks.Count(t =>
            !t.IsCompleted && t.DueDate.HasValue && t.DueDate.Value <= today
        );
        int upcomingCount = _vm.AllTasks.Count(t =>
            !t.IsCompleted && t.DueDate.HasValue && t.DueDate.Value > today
        );
        int allActiveCount = _vm.AllTasks.Count(t => !t.IsCompleted);
        int completedCount = _vm.AllTasks.Count(t => t.IsCompleted);

        navScrollStack.Children.Add(
            BuildNavButton(
                "Inbox",
                MaterialIconKind.InboxOutline,
                inboxCount,
                _vm.ActiveMode == WorkspaceTabMode.TaskList
                    && _vm.ActiveSmartView == SmartViewKind.Inbox,
                () => _vm.SelectSmartView(SmartViewKind.Inbox),
                compact,
                tooltipShortcut: "1"
            )
        );

        navScrollStack.Children.Add(
            BuildNavButton(
                "Today",
                MaterialIconKind.CalendarToday,
                todayCount,
                _vm.ActiveMode == WorkspaceTabMode.TaskList
                    && _vm.ActiveSmartView == SmartViewKind.Today,
                () => _vm.SelectSmartView(SmartViewKind.Today),
                compact,
                tooltipShortcut: "2"
            )
        );

        navScrollStack.Children.Add(
            BuildNavButton(
                "Upcoming",
                MaterialIconKind.CalendarClock,
                upcomingCount,
                _vm.ActiveMode == WorkspaceTabMode.TaskList
                    && _vm.ActiveSmartView == SmartViewKind.Upcoming,
                () => _vm.SelectSmartView(SmartViewKind.Upcoming),
                compact,
                tooltipShortcut: "3"
            )
        );

        navScrollStack.Children.Add(
            BuildNavButton(
                "Calendar",
                MaterialIconKind.CalendarMonthOutline,
                0,
                _vm.ActiveMode == WorkspaceTabMode.Calendar,
                _vm.SelectCalendarView,
                compact,
                tooltipShortcut: "4"
            )
        );

        int dueCardsOrQuizzes =
            _vm.QuizStudio.AllDueReviewQuestions.Count > 0
                ? _vm.QuizStudio.AllDueReviewQuestions.Count
                : _vm.QuizStudio.SavedQuizzes.Count;
        navScrollStack.Children.Add(
            BuildNavButton(
                "Test & Recall",
                MaterialIconKind.HeadLightbulbOutline,
                dueCardsOrQuizzes,
                _vm.ActiveMode == WorkspaceTabMode.QuizStudio,
                async () => await _vm.SelectQuizStudioViewAsync(),
                compact,
                tooltipShortcut: "5"
            )
        );

        navScrollStack.Children.Add(
            BuildNavButton(
                "All Active",
                MaterialIconKind.FormatListBulleted,
                allActiveCount,
                _vm.ActiveMode == WorkspaceTabMode.TaskList
                    && _vm.ActiveSmartView == SmartViewKind.All,
                () => _vm.SelectSmartView(SmartViewKind.All),
                compact,
                tooltipShortcut: "6"
            )
        );

        navScrollStack.Children.Add(
            BuildNavButton(
                "Completed",
                MaterialIconKind.CheckCircleOutline,
                completedCount,
                _vm.ActiveMode == WorkspaceTabMode.TaskList
                    && _vm.ActiveSmartView == SmartViewKind.Completed,
                () => _vm.SelectSmartView(SmartViewKind.Completed),
                compact,
                tooltipShortcut: "7"
            )
        );

        // Courses Section
        var activeCourses = _vm.Courses.Where(c => !c.IsArchived).ToList();
        if (!compact)
        {
            var coursesHeader = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                Margin = new Thickness(0, 8, 0, 2),
            };
            coursesHeader.Children.Add(
                BuildCollapsibleSectionHeader(
                    "Courses",
                    activeCourses.Count,
                    _isCoursesSectionCollapsed,
                    () =>
                    {
                        _isCoursesSectionCollapsed = !_isCoursesSectionCollapsed;
                        RefreshSidebarVisuals();
                    }
                )
            );
            var addCourseBtn = UiBuilder.IconButton(
                MaterialIconKind.Plus,
                _vm.ToggleAddCourseModal,
                14,
                ThemeService.Brush(ThemeService.TextSecondary)
            );
            ToolTip.SetTip(addCourseBtn, "Create a new local course / subject workspace");
            Grid.SetColumn(addCourseBtn, 1);
            coursesHeader.Children.Add(addCourseBtn);
            navScrollStack.Children.Add(coursesHeader);
        }
        else if (activeCourses.Count > 0)
        {
            navScrollStack.Children.Add(UiBuilder.HairlineDivider(new Thickness(4, 6)));
        }

        if (compact || !_isCoursesSectionCollapsed)
        {
            foreach (var course in activeCourses)
            {
                int courseTaskCount = _vm.AllTasks.Count(t =>
                    !t.IsCompleted
                    && string.Equals(
                        t.CourseFolder,
                        course.FolderName,
                        StringComparison.OrdinalIgnoreCase
                    )
                );

                bool isSelected =
                    _vm.ActiveMode == WorkspaceTabMode.CourseWorkspace
                    && _vm.ActiveCourse?.FolderName == course.FolderName;

                var courseBtn = BuildCourseNavButton(
                    course,
                    courseTaskCount,
                    isSelected,
                    () => _vm.SelectCourse(course),
                    compact
                );

                if (_vm.SupportsActiveBasinSync && course.IsSyncableCourse)
                {
                    var capturedCourse = course;
                    var ctxMenu = new ContextMenu();

                    if (_vm.SupportsActivePerCourseSync)
                    {
                        var syncCourseItem = new MenuItem
                        {
                            Header = $"Sync {capturedCourse.Title} from {_vm.ActiveProviderName}",
                        };
                        syncCourseItem.Click += async (_, _) =>
                            await _vm.RunCourseSyncAsync(capturedCourse);
                        ctxMenu.Items.Add(syncCourseItem);
                    }

                    bool isExcluded = _vm.IsCourseSyncExcluded(capturedCourse);
                    var excludeItem = new MenuItem
                    {
                        Header = isExcluded
                            ? "Include in Automatic Sync"
                            : "Exclude from Automatic Sync",
                    };
                    excludeItem.Click += async (_, _) =>
                        await _vm.ToggleCourseSyncExclusionAsync(capturedCourse);
                    ctxMenu.Items.Add(excludeItem);

                    courseBtn.ContextMenu = ctxMenu;
                }

                navScrollStack.Children.Add(courseBtn);
            }
        }

        // Custom Tags (@) Section
        var customLabels = _vm
            .AllKnownLabels.Where(l => TaskItem.NormalizeTerm(l) is null)
            .ToList();

        if (customLabels.Count > 0)
        {
            if (!compact)
            {
                var tagsHeader = BuildCollapsibleSectionHeader(
                    "Tags",
                    customLabels.Count,
                    _isTagsSectionCollapsed,
                    () =>
                    {
                        _isTagsSectionCollapsed = !_isTagsSectionCollapsed;
                        RefreshSidebarVisuals();
                    }
                );
                tagsHeader.Margin = new Thickness(0, 8, 0, 2);
                navScrollStack.Children.Add(tagsHeader);

                if (!_isTagsSectionCollapsed)
                {
                    foreach (var lbl in customLabels)
                    {
                        var capturedLbl = lbl;
                        int lblCount = _vm.AllTasks.Count(t =>
                            !t.IsCompleted
                            && t.Labels.Contains(capturedLbl, StringComparer.OrdinalIgnoreCase)
                        );
                        bool isLblActive =
                            _vm.ActiveMode == WorkspaceTabMode.TaskList
                            && _vm.ActiveSmartView == SmartViewKind.Label
                            && string.Equals(
                                _vm.ActiveLabelFilter,
                                capturedLbl,
                                StringComparison.OrdinalIgnoreCase
                            );

                        navScrollStack.Children.Add(
                            BuildNavButton(
                                $"@{capturedLbl}",
                                MaterialIconKind.TagOutline,
                                lblCount,
                                isLblActive,
                                () => _vm.SelectLabelView(capturedLbl),
                                compact: false
                            )
                        );
                    }
                }
            }
            else
            {
                navScrollStack.Children.Add(UiBuilder.HairlineDivider(new Thickness(4, 6)));
                bool anyTagActive =
                    _vm.ActiveMode == WorkspaceTabMode.TaskList
                    && _vm.ActiveSmartView == SmartViewKind.Label;

                var tagsCompactBtn = BuildNavButton(
                    $"Tags ({customLabels.Count})",
                    MaterialIconKind.TagOutline,
                    0,
                    anyTagActive,
                    () => { },
                    compact: true
                );
                tagsCompactBtn.Flyout = BuildCompactTagsFlyout(customLabels);
                navScrollStack.Children.Add(tagsCompactBtn);
            }
        }

        var savedSidebarOffset = _sidebarScrollOffset;
        var navScroll = new ScrollViewer
        {
            Content = navScrollStack,
            Offset = savedSidebarOffset,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility =
                Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        navScroll.ScrollChanged += (_, _) => _sidebarScrollOffset = navScroll.Offset;
        Dispatcher.UIThread.Post(
            () => navScroll.Offset = savedSidebarOffset,
            DispatcherPriority.Loaded
        );
        Grid.SetRow(navScroll, 1);
        grid.Children.Add(navScroll);

        // Bottom Quiet Utility Strip (Sync, Theme, Settings, Shortcuts)
        var footer = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0) };
        footer.Children.Add(UiBuilder.HairlineDivider(new Thickness(0, 0, 0, 4)));

        if (compact)
        {
            static Control CenterCompact(Control c)
            {
                c.HorizontalAlignment = HorizontalAlignment.Center;
                return c;
            }

            if (_vm.SupportsActiveBasinSync)
            {
                var syncCompact = UiBuilder.IconButton(
                    _vm.IsPollingPaused
                        ? MaterialIconKind.CloudOffOutline
                        : MaterialIconKind.CloudSyncOutline,
                    () => { },
                    15.5,
                    _vm.IsPollingPaused
                        ? ThemeService.Brush(ThemeService.PriorityP2Fg)
                        : ThemeService.Brush(ThemeService.TextSecondary)
                );
                syncCompact.Flyout = BuildElmsSyncFlyout();
                ToolTip.SetTip(
                    syncCompact,
                    _vm.IsPollingPaused
                        ? $"{_vm.ActiveProviderName} Sync (Polling PAUSED — click for options)"
                        : $"{_vm.ActiveProviderName} Sync options"
                );
                footer.Children.Add(CenterCompact(syncCompact));
            }

            var settingsCompact = UiBuilder.IconButton(
                MaterialIconKind.CogOutline,
                _vm.ToggleSettingsModal
            );
            ToolTip.SetTip(settingsCompact, "Desktop Settings");
            footer.Children.Add(CenterCompact(settingsCompact));

            var moreCompact = UiBuilder.IconButton(
                MaterialIconKind.DotsHorizontal,
                () => { }
            );
            moreCompact.Flyout = BuildCompactUtilitiesFlyout();
            ToolTip.SetTip(moreCompact, "More tools (Add Course, Theme, Shortcuts)");
            footer.Children.Add(CenterCompact(moreCompact));
        }
        else
        {
            if (_vm.EnforcesActiveSingleSession && _vm.IsPollingPaused)
            {
                var pausedBannerBtn = new Button
                {
                    Background = ThemeService.Brush(ThemeService.PriorityP2Bg),
                    BorderBrush = ThemeService.Brush(ThemeService.PriorityP2Fg),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 5),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Children =
                        {
                            UiBuilder.Icon(
                                MaterialIconKind.PauseCircle,
                                13.5,
                                ThemeService.Brush(ThemeService.PriorityP2Fg)
                            ),
                            new TextBlock
                            {
                                Text = "Polling Paused · Resume",
                                FontSize = 11.5,
                                FontWeight = FontWeight.SemiBold,
                                Foreground = ThemeService.Brush(ThemeService.PriorityP2Fg),
                                VerticalAlignment = VerticalAlignment.Center,
                            },
                        },
                    },
                };
                pausedBannerBtn.Click += (_, _) => _vm.TogglePollingPaused();
                ToolTip.SetTip(
                    pausedBannerBtn,
                    $"Background {_vm.ActiveProviderName} polling is paused (Shift+P to resume)"
                );
                footer.Children.Add(pausedBannerBtn);
            }

            // Row 1: Primary Sync + Fast Poll (only when basin has an active sync provider)
            if (_vm.SupportsActiveBasinSync)
            {
                var syncRow = new Grid
                {
                    ColumnDefinitions = _vm.SupportsActiveFastPoll
                        ? ColumnDefinitions.Parse("*,Auto")
                        : ColumnDefinitions.Parse("*"),
                    ColumnSpacing = 4,
                };
                var syncBtn = UiBuilder.GhostButton(
                    _vm.IsSyncing ? "Syncing..." : $"{_vm.ActiveProviderName} Sync ▾",
                    MaterialIconKind.CloudSyncOutline,
                    () => { }
                );
                syncBtn.IsEnabled = !_vm.IsSyncing;
                syncBtn.Flyout = BuildElmsSyncFlyout();
                syncBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
                syncRow.Children.Add(syncBtn);

                if (_vm.SupportsActiveFastPoll)
                {
                    var pollBtn = UiBuilder.IconButton(
                        MaterialIconKind.Refresh,
                        async () => await _vm.RunFastPollAsync(),
                        14.5
                    );
                    pollBtn.IsEnabled = !_vm.IsSyncing;
                    Grid.SetColumn(pollBtn, 1);
                    syncRow.Children.Add(pollBtn);
                }
                footer.Children.Add(syncRow);
            }

            // Row 2: Evenly spaced Utility Strip (Pause/Resume, Theme, Settings, Shortcuts)
            var utilRow = new Grid
            {
                ColumnDefinitions = _vm.EnforcesActiveSingleSession
                    ? ColumnDefinitions.Parse("*,*,*,*")
                    : ColumnDefinitions.Parse("*,*,*"),
            };

            int utilCol = 0;
            if (_vm.EnforcesActiveSingleSession)
            {
                var pauseBtn = UiBuilder.IconButton(
                    _vm.IsPollingPaused
                        ? MaterialIconKind.PlayCircleOutline
                        : MaterialIconKind.PauseCircleOutline,
                    _vm.TogglePollingPaused,
                    15,
                    _vm.IsPollingPaused
                        ? ThemeService.Brush(ThemeService.PriorityP2Fg)
                        : ThemeService.Brush(ThemeService.TextSecondary)
                );
                pauseBtn.HorizontalAlignment = HorizontalAlignment.Center;
                ToolTip.SetTip(
                    pauseBtn,
                    _vm.IsPollingPaused
                        ? $"Resume background {_vm.ActiveProviderName} polling (Shift+P)"
                        : $"Pause background {_vm.ActiveProviderName} polling (Shift+P)"
                );
                Grid.SetColumn(pauseBtn, utilCol++);
                utilRow.Children.Add(pauseBtn);
            }

            var themeBtn = UiBuilder.IconButton(
                ThemeService.CurrentTheme == ThemeMode.Dark
                    ? MaterialIconKind.WeatherSunny
                    : MaterialIconKind.WeatherNight,
                async () => await _vm.ToggleThemeAsync(),
                15
            );
            themeBtn.HorizontalAlignment = HorizontalAlignment.Center;
            ToolTip.SetTip(themeBtn, "Toggle Dark / Light theme (T)");
            Grid.SetColumn(themeBtn, utilCol++);
            utilRow.Children.Add(themeBtn);

            var settingsBtn = UiBuilder.IconButton(
                MaterialIconKind.CogOutline,
                _vm.ToggleSettingsModal,
                15
            );
            settingsBtn.HorizontalAlignment = HorizontalAlignment.Center;
            ToolTip.SetTip(settingsBtn, "Desktop Settings (Tray, Notifications, Startup, Vault)");
            Grid.SetColumn(settingsBtn, utilCol++);
            utilRow.Children.Add(settingsBtn);

            var helpBtn = UiBuilder.IconButton(
                MaterialIconKind.KeyboardOutline,
                _vm.ToggleShortcutsModal,
                15
            );
            helpBtn.HorizontalAlignment = HorizontalAlignment.Center;
            ToolTip.SetTip(helpBtn, "Keyboard Shortcuts (?)");
            Grid.SetColumn(helpBtn, utilCol);
            utilRow.Children.Add(helpBtn);

            footer.Children.Add(utilRow);
        }

        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = grid,
        };
    }

    private MenuFlyout BuildBasinSwitcherFlyout()
    {
        var flyout = new MenuFlyout();
        var activeRoot = string.IsNullOrWhiteSpace(_vm.BasinRoot)
            ? string.Empty
            : Path.GetFullPath(_vm.BasinRoot);
        var stateByPath = _vm
            .SyncCoordinator.GetRegisteredBasins()
            .ToDictionary(b => b.BasinRoot, b => b, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _vm.Settings.KnownBasins)
        {
            if (string.IsNullOrWhiteSpace(entry.FilePath))
                continue;

            var full = Path.GetFullPath(entry.FilePath);
            bool isActive = string.Equals(full, activeRoot, StringComparison.OrdinalIgnoreCase);
            string kindTag =
                stateByPath.TryGetValue(full, out var state)
                && state.Provider.Capabilities.SupportsSync
                    ? state.Provider.DisplayName
                    : "Local";
            string syncSuffix =
                stateByPath.TryGetValue(full, out var st) && st.IsSyncing ? " · Syncing..." : "";
            string prefix = isActive ? "✓ " : "";

            var item = new MenuItem { Header = $"{prefix}{entry.Name} [{kindTag}]{syncSuffix}" };
            item.Click += async (_, _) => await _vm.OpenBasinAsync(full);
            flyout.Items.Add(item);
        }

        if (flyout.Items.Count > 0)
            flyout.Items.Add(new Separator());

        var configItem = new MenuItem { Header = "Configure Basin & Sync Provider..." };
        configItem.Click += (_, _) => _vm.ToggleSettingsModal();
        flyout.Items.Add(configItem);

        var openFolderItem = new MenuItem { Header = "Open or Create Another Basin Folder..." };
        openFolderItem.Click += async (_, _) => await PromptOpenBasinFolderAsync();
        flyout.Items.Add(openFolderItem);

        return flyout;
    }

    private MenuFlyout BuildElmsSyncFlyout()
    {
        var flyout = new MenuFlyout();

        if (_vm.SupportsActiveFastPoll)
        {
            var pollItem = new MenuItem
            {
                Header = "Fast Poll Assignments (R)",
                IsEnabled = !_vm.IsSyncing,
            };
            pollItem.Click += async (_, _) => await _vm.RunFastPollAsync();
            flyout.Items.Add(pollItem);
        }

        var fullSyncItem = new MenuItem
        {
            Header = $"Full {_vm.ActiveProviderName} Sync (All Courses)",
            IsEnabled = !_vm.IsSyncing,
        };
        fullSyncItem.Click += async (_, _) => await _vm.RunFullSyncAsync();
        flyout.Items.Add(fullSyncItem);

        var excluded = new HashSet<string>(
            _vm.BasinConfig.ExcludedCourseIds,
            StringComparer.OrdinalIgnoreCase
        );
        var syncableCourses = _vm
            .Courses.Where(c => c.IsSyncableCourse && !excluded.Contains(c.CourseId!))
            .OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (_vm.SupportsActivePerCourseSync && syncableCourses.Count > 0)
        {
            flyout.Items.Add(new Separator());
            foreach (var course in syncableCourses)
            {
                var captured = course;
                var courseItem = new MenuItem
                {
                    Header = $"Sync Course: {captured.Title}",
                    IsEnabled = !_vm.IsSyncing,
                };
                courseItem.Click += async (_, _) => await _vm.RunCourseSyncAsync(captured);
                flyout.Items.Add(courseItem);
            }
        }

        if (_vm.EnforcesActiveSingleSession)
        {
            flyout.Items.Add(new Separator());
            var pauseItem = new MenuItem
            {
                Header = _vm.IsPollingPaused
                    ? $"Resume {_vm.ActiveProviderName} Polling (Shift+P)"
                    : $"Pause {_vm.ActiveProviderName} Polling (Shift+P)",
            };
            pauseItem.Click += (_, _) => _vm.TogglePollingPaused();
            flyout.Items.Add(pauseItem);
        }

        return flyout;
    }

    private static Button BuildNavButton(
        string title,
        MaterialIconKind icon,
        int badgeCount,
        bool isActive,
        Action onClick,
        bool compact = false,
        string? subtitleTag = null,
        IBrush? customAccentFg = null,
        string? tooltipShortcut = null
    )
    {
        var fg =
            customAccentFg
            ?? ThemeService.Brush(ThemeService.TextPrimary);
        var iconFg =
            customAccentFg
            ?? (
                isActive
                    ? ThemeService.Brush(ThemeService.TextPrimary)
                    : ThemeService.Brush(ThemeService.TextSecondary)
            );
        var normalBg = isActive
            ? ThemeService.Brush(ThemeService.BgElevated)
            : Brushes.Transparent;
        var hoverBg = ThemeService.Brush(ThemeService.BgHover);

        Control content;
        if (compact)
        {
            content = UiBuilder.Icon(icon, 17, iconFg);
        }
        else
        {
            var row = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("20,*,Auto") };
            row.Children.Add(UiBuilder.Icon(icon, 15.5, iconFg));

            row.Children.Add(
                new TextBlock
                {
                    Text = title,
                    FontSize = 13,
                    FontWeight =
                        isActive || customAccentFg is not null
                            ? FontWeight.SemiBold
                            : FontWeight.Normal,
                    Foreground = fg,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 4, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    [Grid.ColumnProperty] = 1,
                }
            );

            if (badgeCount > 0)
            {
                row.Children.Add(
                    new TextBlock
                    {
                        Text = badgeCount.ToString(),
                        FontSize = 11.5,
                        Foreground = isActive ? fg : ThemeService.Brush(ThemeService.TextMuted),
                        VerticalAlignment = VerticalAlignment.Center,
                        [Grid.ColumnProperty] = 2,
                    }
                );
            }
            else if (!string.IsNullOrEmpty(subtitleTag))
            {
                row.Children.Add(
                    new TextBlock
                    {
                        Text = subtitleTag,
                        FontSize = 11,
                        Foreground = ThemeService.Brush(ThemeService.TextMuted),
                        VerticalAlignment = VerticalAlignment.Center,
                        [Grid.ColumnProperty] = 2,
                    }
                );
            }

            content = row;
        }

        var btn = new Button
        {
            Content = content,
            Background = normalBg,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Padding = compact ? new Thickness(8, 8) : new Thickness(8, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = compact
                ? HorizontalAlignment.Center
                : HorizontalAlignment.Stretch,
        };
        var tipParts = new List<string> { title };
        if (badgeCount > 0)
            tipParts.Add($"({badgeCount})");
        var shortcutText = !string.IsNullOrWhiteSpace(subtitleTag)
            ? subtitleTag
            : tooltipShortcut;
        if (!string.IsNullOrWhiteSpace(shortcutText))
            tipParts.Add($"[{shortcutText}]");
        ToolTip.SetTip(btn, string.Join(" ", tipParts));

        btn.PointerEntered += (_, _) => btn.Background = hoverBg;
        btn.PointerExited += (_, _) => btn.Background = normalBg;
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private void RefreshSidebarVisuals()
    {
        RefreshSidebar();
        if (_isCompactSidebarOverlayOpen)
            _floatingSidebarDrawer.Child = BuildSidebar(forceExpandedOverlay: true);
    }

    private static Button BuildCollapsibleSectionHeader(
        string title,
        int count,
        bool isCollapsed,
        Action onToggle
    )
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                UiBuilder.Icon(
                    isCollapsed ? MaterialIconKind.ChevronRight : MaterialIconKind.ChevronDown,
                    13,
                    ThemeService.Brush(ThemeService.TextMuted)
                ),
                new TextBlock
                {
                    Text = title.ToUpperInvariant(),
                    FontSize = 10.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    LetterSpacing = 0.6,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = count.ToString(),
                    FontSize = 10,
                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    Opacity = 0.75,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 0, 0),
                },
            },
        };

        var btn = new Button
        {
            Content = row,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(4, 3),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        btn.PointerEntered += (_, _) =>
            btn.Background = ThemeService.Brush(ThemeService.BgHover);
        btn.PointerExited += (_, _) => btn.Background = Brushes.Transparent;
        btn.Click += (_, _) => onToggle();
        ToolTip.SetTip(btn, isCollapsed ? $"Expand {title}" : $"Collapse {title}");
        return btn;
    }

    private MenuFlyout BuildCompactTagsFlyout(IReadOnlyList<string> customLabels)
    {
        var flyout = new MenuFlyout();
        foreach (var lbl in customLabels)
        {
            var capturedLbl = lbl;
            int lblCount = _vm.AllTasks.Count(t =>
                !t.IsCompleted
                && t.Labels.Contains(capturedLbl, StringComparer.OrdinalIgnoreCase)
            );
            bool isLblActive =
                _vm.ActiveMode == WorkspaceTabMode.TaskList
                && _vm.ActiveSmartView == SmartViewKind.Label
                && string.Equals(
                    _vm.ActiveLabelFilter,
                    capturedLbl,
                    StringComparison.OrdinalIgnoreCase
                );

            string prefix = isLblActive ? "✓ " : "";
            string suffix = lblCount > 0 ? $" ({lblCount})" : "";
            var item = new MenuItem { Header = $"{prefix}@{capturedLbl}{suffix}" };
            item.Click += (_, _) => _vm.SelectLabelView(capturedLbl);
            flyout.Items.Add(item);
        }
        return flyout;
    }

    private MenuFlyout BuildCompactUtilitiesFlyout()
    {
        var flyout = new MenuFlyout();

        var addCourseItem = new MenuItem { Header = "Create Local Course / Subject" };
        addCourseItem.Click += (_, _) => _vm.ToggleAddCourseModal();
        flyout.Items.Add(addCourseItem);

        var themeItem = new MenuItem
        {
            Header =
                ThemeService.CurrentTheme == ThemeMode.Dark
                    ? "Switch to Light Theme (T)"
                    : "Switch to Dark Theme (T)",
        };
        themeItem.Click += async (_, _) => await _vm.ToggleThemeAsync();
        flyout.Items.Add(themeItem);

        var shortcutsItem = new MenuItem { Header = "Keyboard Shortcuts (?)" };
        shortcutsItem.Click += (_, _) => _vm.ToggleShortcutsModal();
        flyout.Items.Add(shortcutsItem);

        if (_vm.EnforcesActiveSingleSession)
        {
            flyout.Items.Add(new Separator());
            var pauseItem = new MenuItem
            {
                Header = _vm.IsPollingPaused
                    ? $"Resume {_vm.ActiveProviderName} Polling (Shift+P)"
                    : $"Pause {_vm.ActiveProviderName} Polling (Shift+P)",
            };
            pauseItem.Click += (_, _) => _vm.TogglePollingPaused();
            flyout.Items.Add(pauseItem);
        }

        return flyout;
    }

    private static Button BuildCourseNavButton(
        BasinCourseEntry course,
        int badgeCount,
        bool isActive,
        Action onClick,
        bool compact
    )
    {
        string monogram = BuildCourseMonogram(course);
        var (tintBg, tintFg, tintBorder) = GetCourseTint(course.FolderName);

        var normalBg = isActive
            ? ThemeService.Brush(ThemeService.BgElevated)
            : Brushes.Transparent;
        var hoverBg = ThemeService.Brush(ThemeService.BgHover);

        Control content;
        if (compact)
        {
            content = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(7),
                Background = tintBg,
                BorderBrush = isActive ? tintFg : tintBorder,
                BorderThickness = new Thickness(isActive ? 1.5 : 1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = monogram,
                    FontSize = 10.5,
                    FontWeight = FontWeight.Bold,
                    Foreground = tintFg,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    LetterSpacing = 0.2,
                },
            };
        }
        else
        {
            var badge = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(5),
                Background = tintBg,
                BorderBrush = tintBorder,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = monogram,
                    FontSize = 9,
                    FontWeight = FontWeight.Bold,
                    Foreground = tintFg,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            var row = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("20,*,Auto") };
            row.Children.Add(badge);

            row.Children.Add(
                new TextBlock
                {
                    Text = course.Title,
                    FontSize = 13,
                    FontWeight = isActive ? FontWeight.SemiBold : FontWeight.Normal,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(7, 0, 4, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    [Grid.ColumnProperty] = 1,
                }
            );

            if (badgeCount > 0)
            {
                row.Children.Add(
                    new TextBlock
                    {
                        Text = badgeCount.ToString(),
                        FontSize = 11.5,
                        Foreground = isActive
                            ? ThemeService.Brush(ThemeService.TextPrimary)
                            : ThemeService.Brush(ThemeService.TextMuted),
                        VerticalAlignment = VerticalAlignment.Center,
                        [Grid.ColumnProperty] = 2,
                    }
                );
            }

            content = row;
        }

        var btn = new Button
        {
            Content = content,
            Background = normalBg,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Padding = compact ? new Thickness(4, 4) : new Thickness(8, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = compact
                ? HorizontalAlignment.Center
                : HorizontalAlignment.Stretch,
        };

        string tip =
            badgeCount > 0 ? $"{course.Title} ({badgeCount} active)" : course.Title;
        ToolTip.SetTip(btn, tip);

        btn.PointerEntered += (_, _) => btn.Background = hoverBg;
        btn.PointerExited += (_, _) => btn.Background = normalBg;
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static string BuildCourseMonogram(BasinCourseEntry course)
    {
        static bool IsFillerWord(string w) =>
            w.Equals("OF", StringComparison.OrdinalIgnoreCase)
            || w.Equals("AND", StringComparison.OrdinalIgnoreCase)
            || w.Equals("THE", StringComparison.OrdinalIgnoreCase)
            || w.Equals("TO", StringComparison.OrdinalIgnoreCase)
            || w.Equals("IN", StringComparison.OrdinalIgnoreCase)
            || w.Equals("FOR", StringComparison.OrdinalIgnoreCase)
            || w.Equals("WITH", StringComparison.OrdinalIgnoreCase);

        var source = !string.IsNullOrWhiteSpace(course.Title)
            ? course.Title
            : course.Alias ?? course.FolderName;

        var words = source
            .Split(
                [' ', '-', '_', '/', ':', '(', ')', '&', ','],
                StringSplitOptions.RemoveEmptyEntries
            )
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length > 0)
            .ToList();

        var significant = words.Where(w => !IsFillerWord(w)).ToList();
        var pool = significant.Count > 0 ? significant : words;

        if (pool.Count >= 2)
            return $"{char.ToUpperInvariant(pool[0][0])}{char.ToUpperInvariant(pool[1][0])}";

        if (pool.Count == 1)
        {
            var w = pool[0];
            return w.Length >= 2
                ? w[..2].ToUpperInvariant()
                : char.ToUpperInvariant(w[0]).ToString();
        }

        return "CS";
    }

    private static (IBrush Bg, IBrush Fg, IBrush Border) GetCourseTint(string key)
    {
        // Deterministic FNV-1a hash so each course keeps a stable calm tint across sessions
        uint hash = 2166136261u;
        foreach (char c in (key ?? string.Empty).ToUpperInvariant())
        {
            hash ^= c;
            hash *= 16777619u;
        }

        bool isDark = ThemeService.CurrentTheme == ThemeMode.Dark;
        (string Bg, string Fg, string Border)[] palette = isDark
            ? [
                ("#1E2A3A", "#8BB8F0", "#2C3E55"), // Slate Blue
                ("#1C2E26", "#7FD1AE", "#2A4539"), // Sage Emerald
                ("#31281B", "#E2B571", "#4A3C27"), // Warm Amber
                ("#292338", "#B49CEB", "#3E3454"), // Soft Violet
                ("#1A2D30", "#76C7CE", "#274449"), // Muted Teal
                ("#332127", "#E097AE", "#4D313A"), // Dusty Rose
                ("#22263B", "#9AA8F2", "#333959"), // Cool Indigo
                ("#312520", "#DDA185", "#4A3730"), // Terracotta Sand
            ]
            : [
                ("#E8F0FA", "#2D5B94", "#C9DCF2"), // Slate Blue
                ("#E5F4ED", "#246B4D", "#C3E5D5"), // Sage Emerald
                ("#FAF1E1", "#8A5B1A", "#EBD8B5"), // Warm Amber
                ("#EFEBF9", "#5B4196", "#D9CFF0"), // Soft Violet
                ("#E4F3F4", "#22676E", "#C0E1E4"), // Muted Teal
                ("#F9EBEF", "#8F3B57", "#ECCCD6"), // Dusty Rose
                ("#EAECFA", "#3D4B99", "#CFD5F2"), // Cool Indigo
                ("#F8EDE8", "#8C4E32", "#E8D1C6"), // Terracotta Sand
            ];

        var entry = palette[hash % (uint)palette.Length];
        return (
            new SolidColorBrush(Color.Parse(entry.Bg)),
            new SolidColorBrush(Color.Parse(entry.Fg)),
            new SolidColorBrush(Color.Parse(entry.Border))
        );
    }
}
