using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views;

/// <summary>
/// Builds the responsive Material Month Grid + Selected-Day Agenda Calendar View in pure C#.
/// Adapts smoothly across wide side-by-side split, stacked medium, and compact narrow/short viewports.
/// </summary>
public static class CalendarViewBuilder
{
    private static readonly string[] FullDayNames =
    [
        "SUN",
        "MON",
        "TUE",
        "WED",
        "THU",
        "FRI",
        "SAT",
    ];
    private static readonly string[] CompactDayNames = ["S", "M", "T", "W", "T", "F", "S"];

    private enum CalendarLayoutMode
    {
        WideSplit,
        MediumStacked,
        CompactStacked,
    }

    private sealed record CalendarLayoutState(
        CalendarLayoutMode Mode,
        int MaxPillsPerCell,
        bool UseOuterScroll
    );

    public static Control Build(MainWindowViewModel vm)
    {
        var cal = vm.Calendar;
        var host = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        CalendarLayoutState? lastState = null;

        void ApplyLayout(double width, double height)
        {
            var next = ComputeLayoutState(width, height);
            if (next == lastState && host.Child is not null)
                return;

            lastState = next;
            host.Child = BuildForLayout(vm, cal, next);
        }

        // Initial render before first measure
        ApplyLayout(1100, 740);

        host.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
                ApplyLayout(e.NewSize.Width, e.NewSize.Height);
        };

        return host;
    }

    private static CalendarLayoutState ComputeLayoutState(double width, double height)
    {
        if (width >= 1020 && height >= 520)
        {
            int maxPills = height >= 760 ? 3 : 2;
            return new CalendarLayoutState(
                CalendarLayoutMode.WideSplit,
                maxPills,
                UseOuterScroll: false
            );
        }

        if (width >= 660)
        {
            bool outerScroll = height < 540;
            int maxPills = height >= 680 ? 2 : 1;
            return new CalendarLayoutState(
                CalendarLayoutMode.MediumStacked,
                maxPills,
                UseOuterScroll: outerScroll
            );
        }

        return new CalendarLayoutState(
            CalendarLayoutMode.CompactStacked,
            MaxPillsPerCell: 0,
            UseOuterScroll: height < 580
        );
    }

    private static Control BuildForLayout(
        MainWindowViewModel vm,
        CalendarViewModel cal,
        CalendarLayoutState layout
    )
    {
        bool isCompact = layout.Mode == CalendarLayoutMode.CompactStacked;
        bool isSideBySide = layout.Mode == CalendarLayoutMode.WideSplit;

        var outerMargin = isCompact ? new Thickness(10, 8, 10, 8) : new Thickness(18, 12, 18, 12);

        // 1. Month Header Bar (responsive: wraps cleanly on narrow widths)
        var header = BuildHeaderBar(cal, isCompact);

        // 2. Month Calendar Section (Day-of-week header + 42-cell grid)
        var calendarGridSection = new Grid { RowDefinitions = RowDefinitions.Parse("Auto,*") };

        var dayLabels = isCompact ? CompactDayNames : FullDayNames;
        var dowGrid = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 4) };
        foreach (var d in dayLabels)
        {
            dowGrid.Children.Add(
                new TextBlock
                {
                    Text = d,
                    FontSize = isCompact ? 10.5 : 11,
                    FontWeight = FontWeight.Bold,
                    Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                    HorizontalAlignment = HorizontalAlignment.Center,
                }
            );
        }
        calendarGridSection.Children.Add(dowGrid);

        var monthGrid = new UniformGrid
        {
            Columns = 7,
            Rows = 6,
            MinHeight = isCompact ? 260 : 320,
        };
        foreach (var cell in cal.MonthCells)
        {
            monthGrid.Children.Add(BuildDayCell(vm, cal, cell, layout));
        }
        Grid.SetRow(monthGrid, 1);
        calendarGridSection.Children.Add(monthGrid);

        // 3. Selected-Day Agenda Card
        var agendaCard = BuildDayAgenda(vm, cal, compactRows: isCompact || isSideBySide);

        // 4. Compose responsive shell
        Control bodyContent;
        if (isSideBySide)
        {
            var splitGrid = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,340"),
                ColumnSpacing = 14,
            };
            splitGrid.Children.Add(calendarGridSection);

            Grid.SetColumn(agendaCard, 1);
            splitGrid.Children.Add(agendaCard);

            var root = new Grid
            {
                RowDefinitions = RowDefinitions.Parse("Auto,*"),
                MaxWidth = 1440,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = outerMargin,
            };
            root.Children.Add(header);
            Grid.SetRow(splitGrid, 1);
            root.Children.Add(splitGrid);
            bodyContent = root;
        }
        else if (layout.UseOuterScroll)
        {
            agendaCard.MinHeight = 190;
            agendaCard.Margin = new Thickness(0, 10, 0, 0);

            var stack = new StackPanel
            {
                Spacing = 4,
                Margin = outerMargin,
                MaxWidth = 1280,
                Children = { header, calendarGridSection, agendaCard },
            };
            bodyContent = UiBuilder.PersistentScrollViewer("calendar-outer-scroll", stack);
        }
        else
        {
            agendaCard.Margin = new Thickness(0, 10, 0, 0);
            agendaCard.MinHeight = 155;

            var root = new Grid
            {
                RowDefinitions = RowDefinitions.Parse("Auto,2.45*,1.35*"),
                MaxWidth = 1280,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = outerMargin,
            };
            root.Children.Add(header);
            Grid.SetRow(calendarGridSection, 1);
            root.Children.Add(calendarGridSection);
            Grid.SetRow(agendaCard, 2);
            root.Children.Add(agendaCard);
            bodyContent = root;
        }

        return bodyContent;
    }

    private static Control BuildHeaderBar(CalendarViewModel cal, bool isCompact)
    {
        var header = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            Margin = new Thickness(0, 0, 0, isCompact ? 6 : 10),
        };

        var titleStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = isCompact ? 6 : 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleStack.Children.Add(
            UiBuilder.Icon(
                MaterialIconKind.CalendarMonth,
                isCompact ? 18 : 22,
                ThemeService.Brush(ThemeService.AccentPrimary)
            )
        );
        titleStack.Children.Add(
            new TextBlock
            {
                Text = cal.DisplayedMonth.ToString(isCompact ? "MMM yyyy" : "MMMM yyyy"),
                FontSize = isCompact ? 16 : 19,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            }
        );
        header.Children.Add(titleStack);

        var navBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        navBtns.Children.Add(
            UiBuilder.ActionButton(
                "",
                MaterialIconKind.ChevronLeft,
                cal.PreviousMonth,
                tooltip: "Previous Month ([)"
            )
        );
        navBtns.Children.Add(
            UiBuilder.ActionButton(
                isCompact ? "" : "Today",
                MaterialIconKind.CalendarToday,
                cal.GoToToday,
                tooltip: "Jump to Today"
            )
        );
        navBtns.Children.Add(
            UiBuilder.ActionButton(
                "",
                MaterialIconKind.ChevronRight,
                cal.NextMonth,
                tooltip: "Next Month (])"
            )
        );
        Grid.SetColumn(navBtns, 1);
        header.Children.Add(navBtns);
        return header;
    }

    private static Button BuildDayCell(
        MainWindowViewModel vm,
        CalendarViewModel cal,
        CalendarDayCell cell,
        CalendarLayoutState layout
    )
    {
        bool isCompact = layout.Mode == CalendarLayoutMode.CompactStacked;

        var borderBrush =
            cell.IsSelected ? ThemeService.Brush(ThemeService.AccentPrimary)
            : cell.IsToday ? ThemeService.Brush(ThemeService.PriorityP2Fg)
            : ThemeService.Brush(ThemeService.BorderSubtle);

        var bg =
            cell.IsSelected ? ThemeService.Brush(ThemeService.AccentSoftBg)
            : cell.IsCurrentMonth ? ThemeService.Brush(ThemeService.BgSurface)
            : ThemeService.Brush(ThemeService.BgBase);

        var stack = new StackPanel { Spacing = isCompact ? 2 : 3 };

        var topRow = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        topRow.Children.Add(
            new TextBlock
            {
                Text = cell.Date.Day.ToString(),
                FontSize = isCompact ? 11.5 : 12,
                FontWeight = cell.IsToday || cell.IsSelected ? FontWeight.Bold : FontWeight.Medium,
                Foreground = cell.IsCurrentMonth
                    ? ThemeService.Brush(ThemeService.TextPrimary)
                    : ThemeService.Brush(ThemeService.TextMuted),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );

        int activeCount = cell.Tasks.Count(t => !t.IsCompleted);
        if (activeCount > 0)
        {
            var (fg, pillBg) = cell.HighestPriority.HasValue
                ? ThemeService.GetPriorityBrushes(cell.HighestPriority.Value)
                : (
                    ThemeService.Brush(ThemeService.TextSecondary),
                    ThemeService.Brush(ThemeService.BgElevated)
                );

            var badge = UiBuilder.Pill(
                activeCount.ToString(),
                fg,
                pillBg,
                fontSize: isCompact ? 9 : 10
            );
            Grid.SetColumn(badge, 1);
            topRow.Children.Add(badge);
        }
        stack.Children.Add(topRow);

        if (isCompact)
        {
            // Compact mode: colored priority dots so day cells never clip or squash text
            if (cell.Tasks.Count > 0)
            {
                var dotsWrap = new WrapPanel { Orientation = Orientation.Horizontal };
                foreach (var t in cell.Tasks.Take(5))
                {
                    var dotColor = t.IsCompleted
                        ? ThemeService.Brush(ThemeService.SuccessFg)
                        : ThemeService.PriorityBrush(t.Priority);
                    dotsWrap.Children.Add(
                        new Border
                        {
                            Width = 6,
                            Height = 6,
                            CornerRadius = new CornerRadius(3),
                            Background = dotColor,
                            Margin = new Thickness(0, 2, 3, 0),
                        }
                    );
                }
                stack.Children.Add(dotsWrap);
            }
        }
        else if (layout.MaxPillsPerCell > 0)
        {
            foreach (var t in cell.Tasks.Take(layout.MaxPillsPerCell))
            {
                var capturedTask = t;
                var (pFg, pBg) = t.IsCompleted
                    ? (
                        ThemeService.Brush(ThemeService.SuccessFg),
                        ThemeService.Brush(ThemeService.SuccessBg)
                    )
                    : ThemeService.GetPriorityBrushes(t.Priority);

                var pillLabel = t.DueTime.HasValue
                    ? $"{t.DueTime.Value:h:mm tt} · {t.Title}"
                    : t.Title;

                var pillBtn = new Button
                {
                    Content = new TextBlock
                    {
                        Text = pillLabel,
                        FontSize = 10.5,
                        Foreground = pFg,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxLines = 1,
                    },
                    Background = pBg,
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 1.5),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                pillBtn.Click += (_, e) =>
                {
                    e.Handled = true;
                    cal.SelectDate(cell.Date);
                    vm.SelectTask(capturedTask, openInspector: true);
                };
                stack.Children.Add(pillBtn);
            }

            if (cell.Tasks.Count > layout.MaxPillsPerCell)
            {
                stack.Children.Add(
                    new TextBlock
                    {
                        Text = $"+{cell.Tasks.Count - layout.MaxPillsPerCell} more",
                        FontSize = 10,
                        Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                    }
                );
            }
        }

        var btn = new Button
        {
            Content = stack,
            Background = bg,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(cell.IsSelected || cell.IsToday ? 1.5 : 0.5),
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(isCompact ? 1.5 : 2),
            Padding = isCompact ? new Thickness(4, 3) : new Thickness(6, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        btn.Click += (_, _) =>
        {
            cal.SelectDate(cell.Date);
            if (cell.Tasks.Count > 0)
                vm.SelectTask(cell.Tasks[0], openInspector: !isCompact);
            else
                vm.SelectTask(null);
        };
        btn.DoubleTapped += (_, _) =>
        {
            if (cell.Tasks.Count > 0)
                vm.OpenInspector(cell.Tasks[0]);
            else
                vm.OpenQuickAddModal();
        };

        return btn;
    }

    private static Border BuildDayAgenda(
        MainWindowViewModel vm,
        CalendarViewModel cal,
        bool compactRows
    )
    {
        var panel = new Grid { RowDefinitions = RowDefinitions.Parse("Auto,*") };

        var headerGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            Margin = new Thickness(0, 0, 0, 8),
        };

        var headerTitle = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerTitle.Children.Add(
            UiBuilder.Icon(
                MaterialIconKind.FormatListChecks,
                16,
                ThemeService.Brush(ThemeService.AccentPrimary)
            )
        );
        headerTitle.Children.Add(
            new TextBlock
            {
                Text = $"{cal.SelectedDate:ddd, MMM d, yyyy} ({cal.SelectedDayTasks.Count})",
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        headerGrid.Children.Add(headerTitle);

        var addForDayBtn = UiBuilder.GhostButton("Add", MaterialIconKind.Plus, () =>
        {
            vm.OpenQuickAddModal();
            vm.InsertQuickAddToken(
                cal.SelectedDate.ToString("yyyy-MM-dd"),
                @"\b(today|tomorrow|tmrw|next\s+week|\+\d+d|\d{4}-\d{2}-\d{2})\b"
            );
        });
        ToolTip.SetTip(addForDayBtn, $"Quick-add a task due on {cal.SelectedDate:MMM d, yyyy}");
        Grid.SetColumn(addForDayBtn, 1);
        headerGrid.Children.Add(addForDayBtn);
        panel.Children.Add(headerGrid);

        var listStack = new StackPanel { Spacing = 6 };
        if (cal.SelectedDayTasks.Count == 0)
        {
            listStack.Children.Add(
                new TextBlock
                {
                    Text =
                        "No tasks due on this date. Click Add or double-click any date cell to schedule one.",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                    Margin = new Thickness(2, 6, 2, 0),
                }
            );
        }
        else
        {
            foreach (var task in cal.SelectedDayTasks)
            {
                listStack.Children.Add(BuildAgendaTaskRow(vm, task, compactRows));
            }
        }

        var scroll = UiBuilder.PersistentScrollViewer("calendar-day-list", listStack);
        Grid.SetRow(scroll, 1);
        panel.Children.Add(scroll);

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgElevated),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            Child = panel,
        };
    }

    private static Border BuildAgendaTaskRow(
        MainWindowViewModel vm,
        Core.Models.TaskItem task,
        bool compactRows
    )
    {
        var (pFg, pBg) = ThemeService.GetPriorityBrushes(task.Priority);

        var checkBtn = UiBuilder.IconButton(
            task.IsCompleted ? MaterialIconKind.CheckCircle : MaterialIconKind.CircleOutline,
            async () => await vm.ToggleTaskCompletedAsync(task),
            16,
            task.IsCompleted
                ? ThemeService.Brush(ThemeService.SuccessFg)
                : ThemeService.Brush(ThemeService.TextSecondary)
        );

        var titleBtn = new Button
        {
            Content = new TextBlock
            {
                Text = task.Title,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                FontSize = 12.5,
                FontWeight = FontWeight.Medium,
                TextWrapping = compactRows ? TextWrapping.Wrap : TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = compactRows ? 2 : 1,
                TextDecorations = task.IsCompleted ? TextDecorations.Strikethrough : null,
            },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        titleBtn.Click += (_, _) => vm.SelectTask(task, openInspector: true);

        var metaWrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        void AddMeta(Control c)
        {
            c.Margin = new Thickness(0, 2, 5, 2);
            metaWrap.Children.Add(c);
        }

        if (task.DueTime.HasValue)
        {
            AddMeta(
                UiBuilder.Pill(
                    task.DueTime.Value.ToString("h:mm tt"),
                    ThemeService.Brush(ThemeService.TextPrimary),
                    ThemeService.Brush(ThemeService.BgBase),
                    MaterialIconKind.ClockOutline,
                    fontSize: 10.5
                )
            );
        }
        if (!string.IsNullOrWhiteSpace(task.CourseAlias))
        {
            AddMeta(
                UiBuilder.Pill(
                    $"#{task.CourseAlias}",
                    ThemeService.Brush(ThemeService.AccentPrimary),
                    ThemeService.Brush(ThemeService.AccentSoftBg),
                    fontSize: 10.5
                )
            );
        }
        if (!string.IsNullOrWhiteSpace(task.Term))
        {
            AddMeta(
                UiBuilder.Pill(
                    task.Term,
                    ThemeService.Brush(ThemeService.TextSecondary),
                    ThemeService.Brush(ThemeService.BgElevated),
                    fontSize: 10.5
                )
            );
        }
        AddMeta(UiBuilder.Pill($"P{(int)task.Priority}", pFg, pBg, fontSize: 10.5));

        if (!string.IsNullOrWhiteSpace(task.PrimaryPdfFile))
        {
            AddMeta(
                UiBuilder.GhostButton("PDF", MaterialIconKind.FilePdfBox, () =>
                    vm.OpenTaskFile(task, task.PrimaryPdfFile)
                )
            );
        }
        AddMeta(
            UiBuilder.GhostButton("Inspect", MaterialIconKind.TuneVariant, () =>
                vm.SelectTask(task, openInspector: true)
            )
        );

        Control rowContent;
        if (compactRows)
        {
            var topLine = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
                ColumnSpacing = 4,
            };
            topLine.Children.Add(checkBtn);
            Grid.SetColumn(titleBtn, 1);
            topLine.Children.Add(titleBtn);

            metaWrap.Margin = new Thickness(28, 2, 0, 0);
            rowContent = new StackPanel { Spacing = 2, Children = { topLine, metaWrap } };
        }
        else
        {
            var grid = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
                ColumnSpacing = 6,
            };
            grid.Children.Add(checkBtn);
            Grid.SetColumn(titleBtn, 1);
            grid.Children.Add(titleBtn);
            Grid.SetColumn(metaWrap, 2);
            grid.Children.Add(metaWrap);
            rowContent = grid;
        }

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6),
            Child = rowContent,
        };
    }
}
