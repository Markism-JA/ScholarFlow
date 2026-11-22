using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Material.Icons;
using ScholarFlow.Core.Knowledge;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Tasks;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views;

/// <summary>
/// MainWindow partial: Todoist-style hairline task list and task rows.
/// </summary>
public sealed partial class MainWindow
{
    private Control BuildTaskListView(MainWindowViewModel vm)
    {
        var isCourseSubView = vm.ActiveMode == WorkspaceTabMode.CourseWorkspace;
        var stack = new StackPanel
        {
            Spacing = 2,
            MaxWidth = 820,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = isCourseSubView ? new Thickness(0, 4, 0, 24) : new Thickness(28, 22, 28, 28),
        };

        // Top View Header (Only for top-level smart views; CourseWorkspace has its own header)
        if (!isCourseSubView)
        {
            var headerGrid = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                Margin = new Thickness(0, 0, 0, 16),
            };

            var titleLeft = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            var headingText = vm.ActiveSmartView switch
            {
                SmartViewKind.All => "All Active",
                SmartViewKind.Label when !string.IsNullOrWhiteSpace(vm.ActiveLabelFilter) =>
                    $"@{vm.ActiveLabelFilter}",
                _ => vm.ActiveSmartView.ToString(),
            };
            if (!string.IsNullOrWhiteSpace(vm.ActiveTermFilter))
                headingText += $" · {vm.ActiveTermFilter}";

            titleLeft.Children.Add(
                new TextBlock
                {
                    Text = headingText,
                    FontSize = 22,
                    FontWeight = FontWeight.Bold,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                }
            );

            var subtitleText =
                vm.ActiveSmartView == SmartViewKind.Today
                    ? $"{DateTime.Today:ddd, MMM d} · {vm.VisibleTasks.Count} task(s)"
                    : $"{vm.VisibleTasks.Count} task(s)";

            titleLeft.Children.Add(
                new TextBlock
                {
                    Text = subtitleText,
                    FontSize = 12.5,
                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 3),
                }
            );
            headerGrid.Children.Add(titleLeft);

            var headerActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };
            headerActions.Children.Add(
                UiBuilder.GhostButton(
                    "Add task",
                    MaterialIconKind.Plus,
                    vm.OpenQuickAddModal,
                    isActive: true
                )
            );
            headerActions.Children.Add(
                UiBuilder.IconButton(MaterialIconKind.Magnify, vm.OpenSearchModal, 16)
            );
            if (vm.SelectedTask is not null)
            {
                headerActions.Children.Add(
                    UiBuilder.IconButton(
                        MaterialIconKind.DockRight,
                        vm.ToggleInspector,
                        16,
                        vm.IsInspectorOpen ? ThemeService.Brush(ThemeService.AccentPrimary) : null
                    )
                );
            }
            Grid.SetColumn(headerActions, 1);
            headerGrid.Children.Add(headerActions);

            stack.Children.Add(headerGrid);
        }

        // Academic Term Filter & Grouping Bar
        var filterBar = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            Margin = new Thickness(0, 0, 0, 10),
        };

        if (vm.ActiveTerms.Count > 0)
        {
            var termPills = new WrapPanel { Orientation = Orientation.Horizontal };
            var allTermsBtn = UiBuilder.GhostButton(
                "All Terms",
                null,
                () => vm.SelectTermView(null),
                isActive: string.IsNullOrEmpty(vm.ActiveTermFilter)
            );
            allTermsBtn.Margin = new Thickness(0, 0, 4, 4);
            termPills.Children.Add(allTermsBtn);

            foreach (var termName in vm.ActiveTerms)
            {
                var capturedTerm = termName;
                bool isTermActive = string.Equals(
                    vm.ActiveTermFilter,
                    capturedTerm,
                    StringComparison.OrdinalIgnoreCase
                );
                var tBtn = UiBuilder.GhostButton(
                    capturedTerm,
                    null,
                    () => vm.SelectTermView(capturedTerm),
                    isActive: isTermActive
                );
                tBtn.Margin = new Thickness(0, 0, 4, 4);
                termPills.Children.Add(tBtn);
            }
            filterBar.Children.Add(termPills);
        }

        var effectiveGrouping =
            vm.ActiveGrouping == TaskGroupingMode.Default
                ? (isCourseSubView ? TaskGroupingMode.Term : TaskGroupingMode.DueDate)
                : vm.ActiveGrouping;

        var groupPills = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Top,
        };
        groupPills.Children.Add(
            UiBuilder.GhostButton(
                "By Term",
                MaterialIconKind.SchoolOutline,
                () => vm.SetGroupingMode(TaskGroupingMode.Term),
                isActive: effectiveGrouping == TaskGroupingMode.Term
            )
        );
        groupPills.Children.Add(
            UiBuilder.GhostButton(
                "By Date",
                MaterialIconKind.CalendarBlankOutline,
                () => vm.SetGroupingMode(TaskGroupingMode.DueDate),
                isActive: effectiveGrouping == TaskGroupingMode.DueDate
            )
        );
        groupPills.Children.Add(
            UiBuilder.GhostButton(
                "By Priority",
                MaterialIconKind.FlagOutline,
                () => vm.SetGroupingMode(TaskGroupingMode.Priority),
                isActive: effectiveGrouping == TaskGroupingMode.Priority
            )
        );

        Grid.SetColumn(groupPills, 1);
        filterBar.Children.Add(groupPills);
        stack.Children.Add(filterBar);

        // Per-Subject Filter Bar in Completed View
        if (
            vm.ActiveSmartView == SmartViewKind.Completed
            && vm.ActiveMode == WorkspaceTabMode.TaskList
        )
        {
            var subjectWrap = new WrapPanel { Orientation = Orientation.Horizontal };
            var allSubjectsBtn = UiBuilder.GhostButton(
                "All Subjects",
                string.IsNullOrWhiteSpace(vm.CompletedCourseFilter) ? MaterialIconKind.Check : null,
                () => vm.SetCompletedCourseFilter(null),
                isActive: string.IsNullOrWhiteSpace(vm.CompletedCourseFilter)
            );
            allSubjectsBtn.Margin = new Thickness(0, 0, 5, 4);
            subjectWrap.Children.Add(allSubjectsBtn);

            foreach (var c in vm.Courses.Where(x => !x.IsArchived))
            {
                var captured = c;
                bool isCourseActive = string.Equals(
                    vm.CompletedCourseFilter,
                    c.FolderName,
                    StringComparison.OrdinalIgnoreCase
                );
                int doneCount = vm.AllTasks.Count(t =>
                    t.IsCompleted
                    && string.Equals(
                        t.CourseFolder,
                        c.FolderName,
                        StringComparison.OrdinalIgnoreCase
                    )
                );
                var cBtn = UiBuilder.GhostButton(
                    $"#{c.Alias} ({doneCount})",
                    isCourseActive ? MaterialIconKind.Check : null,
                    () => vm.SetCompletedCourseFilter(captured.FolderName),
                    isActive: isCourseActive
                );
                cBtn.Margin = new Thickness(0, 0, 5, 4);
                ToolTip.SetTip(cBtn, c.Title);
                subjectWrap.Children.Add(cBtn);
            }
            stack.Children.Add(subjectWrap);
        }

        // Unregistered Ongoing Files Banner (quiet, compact)
        var relevantUnregistered =
            vm.ActiveCourse is not null
                ? vm
                    .UnregisteredFiles.Where(u =>
                        u.CourseFolder.Equals(
                            vm.ActiveCourse.FolderName,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    .ToList()
            : vm.ActiveSmartView is SmartViewKind.Inbox or SmartViewKind.All
                ? [.. vm.UnregisteredFiles]
            : [];

        if (relevantUnregistered.Count > 0)
        {
            var unregStack = new StackPanel { Spacing = 4 };
            unregStack.Children.Add(
                new TextBlock
                {
                    Text = $"{relevantUnregistered.Count} untracked file(s) in ongoing/",
                    FontSize = 11.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeService.Brush(ThemeService.PriorityP2Fg),
                }
            );

            foreach (var file in relevantUnregistered.Take(4))
            {
                var fRow = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
                fRow.Children.Add(
                    new TextBlock
                    {
                        Text = $"{file.CourseTitle} · {file.FileName}",
                        FontSize = 12,
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    }
                );
                var trackBtn = UiBuilder.GhostButton("Track", MaterialIconKind.Plus, async () =>
                    await vm.RegisterOngoingFileAsync(file)
                );
                Grid.SetColumn(trackBtn, 1);
                fRow.Children.Add(trackBtn);
                unregStack.Children.Add(fRow);
            }

            stack.Children.Add(
                new Border
                {
                    Background = ThemeService.Brush(ThemeService.BgSurface),
                    BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(12, 8),
                    Margin = new Thickness(0, 0, 0, 12),
                    Child = unregStack,
                }
            );
        }

        if (vm.VisibleTasks.Count == 0)
        {
            stack.Children.Add(
                new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 64, 0, 0),
                    Spacing = 10,
                    Children =
                    {
                        UiBuilder.Icon(
                            MaterialIconKind.CheckCircleOutline,
                            30,
                            ThemeService.Brush(ThemeService.TextMuted)
                        ),
                        new TextBlock
                        {
                            Text = "All clear in this view",
                            FontSize = 14,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                            HorizontalAlignment = HorizontalAlignment.Center,
                        },
                        new TextBlock
                        {
                            Text = "Press Q to add a task or / to search across courses and PDFs.",
                            FontSize = 12.5,
                            Foreground = ThemeService.Brush(ThemeService.TextMuted),
                            HorizontalAlignment = HorizontalAlignment.Center,
                        },
                    },
                }
            );
        }
        else
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var groups = GroupTasksTodoistStyle(
                vm.VisibleTasks,
                today,
                vm.ActiveSmartView,
                effectiveGrouping
            );

            foreach (var (sectionTitle, sectionTasks, isUrgentSection) in groups)
            {
                if (groups.Count > 1)
                {
                    var secHeader = new StackPanel
                    {
                        Spacing = 4,
                        Margin = new Thickness(0, 14, 0, 2),
                    };
                    var secTitleRow = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                    };
                    secTitleRow.Children.Add(
                        new TextBlock
                        {
                            Text = sectionTitle,
                            FontSize = 12.5,
                            FontWeight = FontWeight.Bold,
                            Foreground = isUrgentSection
                                ? ThemeService.Brush(ThemeService.PriorityP1Fg)
                                : ThemeService.Brush(ThemeService.TextPrimary),
                        }
                    );
                    secTitleRow.Children.Add(
                        new TextBlock
                        {
                            Text = sectionTasks.Count.ToString(),
                            FontSize = 12,
                            Foreground = ThemeService.Brush(ThemeService.TextMuted),
                        }
                    );
                    secHeader.Children.Add(secTitleRow);
                    secHeader.Children.Add(UiBuilder.HairlineDivider());
                    stack.Children.Add(secHeader);
                }

                foreach (var task in sectionTasks)
                {
                    stack.Children.Add(BuildHairlineTaskRow(vm, task, today));
                }
            }

            // Quiet "+ Add task" inline button at the bottom of the list just like Todoist
            var inlineAddBtn = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        UiBuilder.Icon(
                            MaterialIconKind.Plus,
                            15,
                            ThemeService.Brush(ThemeService.AccentPrimary)
                        ),
                        new TextBlock
                        {
                            Text = "Add task",
                            FontSize = 13,
                            Foreground = ThemeService.Brush(ThemeService.TextMuted),
                        },
                    },
                },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 8),
                Margin = new Thickness(0, 4, 0, 0),
                Cursor = new Cursor(StandardCursorType.Hand),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            inlineAddBtn.Click += (_, _) => vm.OpenQuickAddModal();
            stack.Children.Add(inlineAddBtn);
        }

        var savedTaskListOffset = _taskListScrollOffset;
        var taskListScroll = new ScrollViewer { Content = stack, Offset = savedTaskListOffset };
        taskListScroll.ScrollChanged += (_, _) => _taskListScrollOffset = taskListScroll.Offset;
        Dispatcher.UIThread.Post(
            () => taskListScroll.Offset = savedTaskListOffset,
            DispatcherPriority.Loaded
        );
        return taskListScroll;
    }

    private static List<(string Title, List<TaskItem> Tasks, bool IsUrgent)> GroupTasksTodoistStyle(
        IReadOnlyList<TaskItem> tasks,
        DateOnly today,
        SmartViewKind viewKind,
        TaskGroupingMode groupingMode
    )
    {
        if (viewKind == SmartViewKind.Completed && groupingMode == TaskGroupingMode.DueDate)
            return [("Completed", tasks.ToList(), false)];

        if (groupingMode == TaskGroupingMode.Term)
        {
            var termGroups = new List<(string Title, List<TaskItem> Tasks, bool IsUrgent)>();
            var configuredTerms = new List<string> { "Prelims", "Midterms", "Prefinals", "Finals" };
            foreach (
                var custom in tasks
                    .Select(t => TaskItem.NormalizeTerm(t.Term) ?? t.Term?.Trim())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Cast<string>()
            )
            {
                if (!configuredTerms.Contains(custom, StringComparer.OrdinalIgnoreCase))
                    configuredTerms.Add(custom);
            }
            var matchedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var termName in configuredTerms)
            {
                var matched = tasks.Where(t => TaskQueryService.MatchesTerm(t, termName)).ToList();
                if (matched.Count > 0)
                {
                    foreach (var m in matched)
                        matchedIds.Add(m.Id);
                    termGroups.Add((termName, matched, false));
                }
            }

            var uncategorized = tasks.Where(t => !matchedIds.Contains(t.Id)).ToList();
            if (uncategorized.Count > 0)
                termGroups.Add(("General / Uncategorized", uncategorized, false));

            return termGroups;
        }

        if (groupingMode == TaskGroupingMode.Priority)
        {
            var priGroups = new List<(string Title, List<TaskItem> Tasks, bool IsUrgent)>();
            foreach (
                var (p, label, urgent) in new[]
                {
                    (TaskPriority.P1, "Priority 1 — Urgent", true),
                    (TaskPriority.P2, "Priority 2 — High", false),
                    (TaskPriority.P3, "Priority 3 — Medium", false),
                    (TaskPriority.P4, "Priority 4 — Normal", false),
                }
            )
            {
                var matched = tasks.Where(t => t.Priority == p).ToList();
                if (matched.Count > 0)
                    priGroups.Add((label, matched, urgent));
            }
            return priGroups;
        }

        var overdue = new List<TaskItem>();
        var dueToday = new List<TaskItem>();
        var next7Days = new List<TaskItem>();
        var upcomingLater = new List<TaskItem>();
        var noDueDate = new List<TaskItem>();

        foreach (var t in tasks)
        {
            if (!t.DueDate.HasValue)
            {
                noDueDate.Add(t);
            }
            else if (!t.IsCompleted && t.DueDate.Value < today)
            {
                overdue.Add(t);
            }
            else if (t.DueDate.Value == today)
            {
                dueToday.Add(t);
            }
            else if (t.DueDate.Value <= today.AddDays(7))
            {
                next7Days.Add(t);
            }
            else
            {
                upcomingLater.Add(t);
            }
        }

        var result = new List<(string Title, List<TaskItem> Tasks, bool IsUrgent)>();
        if (overdue.Count > 0)
            result.Add(("Overdue", overdue, true));
        if (dueToday.Count > 0)
            result.Add(("Today", dueToday, false));
        if (next7Days.Count > 0)
            result.Add(("Next 7 Days", next7Days, false));
        if (upcomingLater.Count > 0)
            result.Add(("Upcoming", upcomingLater, false));
        if (noDueDate.Count > 0)
            result.Add(("No Due Date", noDueDate, false));
        return result;
    }

    private Border BuildHairlineTaskRow(MainWindowViewModel vm, TaskItem task, DateOnly today)
    {
        bool isSelected = vm.SelectedTask?.Id == task.Id;
        var (pFg, pBg) = ThemeService.GetPriorityBrushes(task.Priority);

        var rowGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto") };

        // 1. Left Circular Priority Ring
        var checkIcon = task.IsCompleted
            ? MaterialIconKind.CheckCircle
            : MaterialIconKind.CircleOutline;
        var checkBtn = new Button
        {
            Content = UiBuilder.Icon(
                checkIcon,
                18.5,
                task.IsCompleted ? ThemeService.Brush(ThemeService.SuccessFg) : pFg
            ),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            Cursor = new Cursor(StandardCursorType.Hand),
            Padding = new Thickness(3),
            Margin = new Thickness(0, 1, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        checkBtn.PointerEntered += (_, _) => checkBtn.Background = pBg;
        checkBtn.PointerExited += (_, _) => checkBtn.Background = Brushes.Transparent;
        checkBtn.Click += async (_, e) =>
        {
            e.Handled = true;
            await vm.ToggleTaskCompletedAsync(task);
        };
        rowGrid.Children.Add(checkBtn);

        // 2. Center Title + Quiet Single-Line Subtitle
        var centerStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var titleBlock = new TextBlock
        {
            Text = task.Title,
            FontSize = 13.5,
            FontWeight = isSelected ? FontWeight.Medium : FontWeight.Normal,
            Foreground = task.IsCompleted
                ? ThemeService.Brush(ThemeService.TextMuted)
                : ThemeService.Brush(ThemeService.TextPrimary),
            TextDecorations = task.IsCompleted ? TextDecorations.Strikethrough : null,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        centerStack.Children.Add(titleBlock);

        var subParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(task.ParentTaskId))
            subParts.Add("Subtask");
        if (!string.IsNullOrWhiteSpace(task.LmsScore))
            subParts.Add($"Score: {task.LmsScore}");
        else if (
            !string.IsNullOrWhiteSpace(task.LmsStatus)
            && !task.LmsStatus.Equals("Pending", StringComparison.OrdinalIgnoreCase)
        )
            subParts.Add(task.LmsStatus);
        if (task.InstructionFiles.Count > 0)
            subParts.Add($"{task.InstructionFiles.Count} attachment(s)");
        if (task.SubmissionFiles.Count > 0)
            subParts.Add($"{task.SubmissionFiles.Count} submission(s)");
        foreach (
            var lbl in task.Labels.Where(l =>
                !l.Equals("assignment", StringComparison.OrdinalIgnoreCase)
                && !l.Equals("freeform", StringComparison.OrdinalIgnoreCase)
                && !l.Equals("dropbox", StringComparison.OrdinalIgnoreCase)
                && TaskItem.NormalizeTerm(l) is null
            )
        )
        {
            subParts.Add($"@{lbl}");
        }

        if (subParts.Count > 0)
        {
            centerStack.Children.Add(
                new TextBlock
                {
                    Text = string.Join(" · ", subParts),
                    FontSize = 11.5,
                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                }
            );
        }

        Grid.SetColumn(centerStack, 1);
        rowGrid.Children.Add(centerStack);

        // 3. Right Minimal Metadata (Hover Actions + Subtasks Toggle + Attachment PDF Chip + Term Pill + Due Date + #Course)
        var rightRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Progressive disclosure hover actions
        var hoverActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            IsVisible = isSelected,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (!string.IsNullOrWhiteSpace(task.LmsUrl) || !string.IsNullOrWhiteSpace(task.LmsId))
        {
            hoverActions.Children.Add(
                UiBuilder.IconButton(
                    MaterialIconKind.OpenInApp,
                    async () => await vm.OpenTaskInElmsAppAsync(task),
                    14
                )
            );
        }

        hoverActions.Children.Add(
            UiBuilder.IconButton(MaterialIconKind.TuneVariant, () => vm.OpenInspector(task), 14)
        );

        rightRow.Children.Add(hoverActions);

        // Inline Subtasks Expand/Collapse Button
        task.EnsureChildTasksSynced();
        if (task.ChildTasks.Count > 0)
        {
            int doneSubs = task.ChildTasks.Count(s => s.IsCompleted);
            rightRow.Children.Add(
                UiBuilder.GhostButton(
                    $"{doneSubs}/{task.ChildTasks.Count}",
                    task.IsSubtasksExpanded
                        ? MaterialIconKind.ChevronDown
                        : MaterialIconKind.ChevronRight,
                    () => vm.ToggleTaskSubtasksExpanded(task)
                )
            );
        }

        // Direct Attachment / Submission PDF Viewer Chip
        var primaryPdf = task.PrimaryPdfFile;
        if (!string.IsNullOrWhiteSpace(primaryPdf))
        {
            rightRow.Children.Add(
                UiBuilder.GhostButton("PDF", MaterialIconKind.FilePdfBox, () =>
                    vm.OpenTaskFile(task, primaryPdf)
                )
            );
        }
        else if (task.AllFiles.Count > 0)
        {
            rightRow.Children.Add(
                UiBuilder.GhostButton("File", MaterialIconKind.Paperclip, () =>
                    vm.OpenTaskFile(task, task.AllFiles[0])
                )
            );
        }

        // Academic Term Badge
        var normTerm = TaskItem.NormalizeTerm(task.Term);
        if (!string.IsNullOrWhiteSpace(normTerm))
        {
            rightRow.Children.Add(
                UiBuilder.Pill(
                    normTerm,
                    ThemeService.Brush(ThemeService.TextSecondary),
                    ThemeService.Brush(ThemeService.BgElevated),
                    fontSize: 10.5
                )
            );
        }

        if (task.DueDate.HasValue)
        {
            bool isOverdue = !task.IsCompleted && task.DueDate.Value < today;
            bool isDueToday = !task.IsCompleted && task.DueDate.Value == today;
            var dFg =
                isOverdue ? ThemeService.Brush(ThemeService.PriorityP1Fg)
                : isDueToday ? ThemeService.Brush(ThemeService.SuccessFg)
                : ThemeService.Brush(ThemeService.TextSecondary);

            var baseDateText = isDueToday ? "Today" : task.DueDate.Value.ToString("MMM d");
            var dateText = task.DueTime.HasValue
                ? $"{baseDateText} · {task.DueTime.Value:h:mm tt}"
                : baseDateText;
            var dateRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
            };
            dateRow.Children.Add(UiBuilder.Icon(MaterialIconKind.CalendarBlankOutline, 12, dFg));
            dateRow.Children.Add(
                new TextBlock
                {
                    Text = dateText,
                    FontSize = 11.5,
                    FontWeight = isOverdue || isDueToday ? FontWeight.Medium : FontWeight.Normal,
                    Foreground = dFg,
                }
            );
            rightRow.Children.Add(dateRow);
        }

        if (!string.IsNullOrWhiteSpace(task.CourseAlias) && vm.ActiveCourse is null)
        {
            rightRow.Children.Add(
                new TextBlock
                {
                    Text = $"#{task.CourseAlias}",
                    FontSize = 11.5,
                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );
        }

        Grid.SetColumn(rightRow, 2);
        rowGrid.Children.Add(rightRow);

        var rowContainer = new StackPanel { Spacing = 4 };
        rowContainer.Children.Add(rowGrid);

        // Inline Nested Subtasks Tree directly under the parent task row (First-Class Child TaskItems)
        task.EnsureChildTasksSynced();
        if (task.ChildTasks.Count > 0 && task.IsSubtasksExpanded)
        {
            var subTreeStack = new StackPanel { Spacing = 2, Margin = new Thickness(28, 4, 4, 2) };

            for (int i = 0; i < task.ChildTasks.Count; i++)
            {
                int subIdx = i;
                var child = task.ChildTasks[i];
                bool isChildSelected = _vm.SelectedTask?.Id == child.Id;

                var childRow = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*") };

                var checkSubBtn = new Button
                {
                    Content = UiBuilder.Icon(
                        child.IsCompleted
                            ? MaterialIconKind.CheckCircle
                            : MaterialIconKind.CircleOutline,
                        14.5,
                        child.IsCompleted
                            ? ThemeService.Brush(ThemeService.SuccessFg)
                            : ThemeService.PriorityBrush(child.Priority)
                    ),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(4, 2.5),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                checkSubBtn.Click += async (_, e) =>
                {
                    e.Handled = true;
                    await vm.ToggleSubtaskAsync(task, subIdx);
                };
                childRow.Children.Add(checkSubBtn);

                var childInfoStack = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 7,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                childInfoStack.Children.Add(
                    new TextBlock
                    {
                        Text = child.Title,
                        FontSize = 12,
                        FontWeight = isChildSelected ? FontWeight.SemiBold : FontWeight.Normal,
                        Foreground =
                            child.IsCompleted ? ThemeService.Brush(ThemeService.TextMuted)
                            : isChildSelected ? ThemeService.Brush(ThemeService.AccentPrimary)
                            : ThemeService.Brush(ThemeService.TextSecondary),
                        TextDecorations = child.IsCompleted ? TextDecorations.Strikethrough : null,
                        VerticalAlignment = VerticalAlignment.Center,
                    }
                );

                if (child.DueDate.HasValue)
                {
                    childInfoStack.Children.Add(
                        new TextBlock
                        {
                            Text = child.DueDate.Value.ToString("MMM d"),
                            FontSize = 11,
                            Foreground = ThemeService.Brush(ThemeService.TextMuted),
                            VerticalAlignment = VerticalAlignment.Center,
                        }
                    );
                }

                if (child.Priority != TaskPriority.P4)
                {
                    childInfoStack.Children.Add(
                        new TextBlock
                        {
                            Text = $"P{(int)child.Priority}",
                            FontSize = 10.5,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = ThemeService.PriorityBrush(child.Priority),
                            VerticalAlignment = VerticalAlignment.Center,
                        }
                    );
                }

                foreach (var lbl in child.Labels.Take(2))
                {
                    childInfoStack.Children.Add(
                        new TextBlock
                        {
                            Text = $"@{lbl}",
                            FontSize = 10.5,
                            Foreground = ThemeService.Brush(ThemeService.TextMuted),
                            VerticalAlignment = VerticalAlignment.Center,
                        }
                    );
                }

                var selectChildBtn = new Button
                {
                    Content = childInfoStack,
                    Background = isChildSelected
                        ? ThemeService.Brush(ThemeService.BgHover)
                        : Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2.5),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                selectChildBtn.Click += (_, e) =>
                {
                    e.Handled = true;
                    vm.SelectTask(child, openInspector: true);
                };
                Grid.SetColumn(selectChildBtn, 1);
                childRow.Children.Add(selectChildBtn);

                subTreeStack.Children.Add(childRow);
            }

            rowContainer.Children.Add(
                new Border
                {
                    BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                    BorderThickness = new Thickness(1, 0, 0, 0),
                    Margin = new Thickness(10, 0, 0, 0),
                    Child = subTreeStack,
                }
            );
        }

        var rowBorder = new Border
        {
            Background = isSelected
                ? ThemeService.Brush(ThemeService.BgElevated)
                : Brushes.Transparent,
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            Padding = new Thickness(6, 8.5),
            Transitions = UiBuilder.CreateSmoothBrushTransitions(110),
            Child = rowContainer,
        };

        rowBorder.PointerEntered += (_, _) =>
        {
            hoverActions.IsVisible = true;
            if (_vm.SelectedTask?.Id != task.Id)
                rowBorder.Background = ThemeService.Brush(ThemeService.BgHover);
        };
        rowBorder.PointerExited += (_, _) =>
        {
            if (_vm.SelectedTask?.Id != task.Id)
            {
                hoverActions.IsVisible = false;
                rowBorder.Background = Brushes.Transparent;
            }
        };
        rowBorder.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(rowBorder).Properties.IsLeftButtonPressed)
                return;
            if (IsClickInsideChildButton(e.Source as Visual, rowBorder))
                return;
            vm.SelectTask(task, openInspector: true);
        };

        _taskRowsById[task.Id] = (rowBorder, titleBlock, hoverActions);
        return rowBorder;
    }

    private static bool IsClickInsideChildButton(Visual? source, Visual boundary)
    {
        for (
            var cur = source;
            cur is not null && !ReferenceEquals(cur, boundary);
            cur = cur.GetVisualParent()
        )
        {
            if (cur is Button)
                return true;
        }
        return false;
    }

    // =========================================================================
    // 3. FLOATING CENTERED MODALS (QUICK ADD, QUICK SEARCH, NOTIFICATIONS, HELP)
    // =========================================================================
}
