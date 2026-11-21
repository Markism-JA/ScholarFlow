using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using ScholarFlow.Desktop.Theme;

namespace ScholarFlow.Desktop.UI;

/// <summary>
/// Pure-C# themed Month Date Picker and Interactive Clock Time Picker widgets.
/// Uses ThemeService brushes exclusively so Dark and Light modes stay 100% cohesive
/// across Task Overview (Inspector) and Quick Add without unthemed Fluent popups.
/// </summary>
public static class DateTimePickerWidgets
{
    private static readonly string[] ShortDayHeaders = ["Su", "Mo", "Tu", "We", "Th", "Fr", "Sa"];
    private static readonly int[] ClockHours = [12, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
    private static readonly int[] ClockMinutes = [0, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55];

    /// <summary>
    /// Builds a themed Date Picker button that opens a fully themed Month Calendar Flyout.
    /// </summary>
    public static Button BuildDatePickerButton(
        DateOnly? initialDate,
        Func<DateOnly?, Task> onDateSelected,
        string placeholder = "Pick date...",
        double minWidth = 142
    )
    {
        DateOnly? currentDate = initialDate;
        var displayedMonth = initialDate is { } d
            ? new DateOnly(d.Year, d.Month, 1)
            : new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);

        var labelText = new TextBlock
        {
            Text = FormatDateLabel(currentDate, placeholder),
            FontSize = 12,
            FontWeight = currentDate.HasValue ? FontWeight.Medium : FontWeight.Normal,
            Foreground = currentDate.HasValue
                ? ThemeService.Brush(ThemeService.TextPrimary)
                : ThemeService.Brush(ThemeService.TextMuted),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var triggerContent = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
            ColumnSpacing = 6,
            Children =
            {
                UiBuilder.Icon(
                    MaterialIconKind.CalendarMonthOutline,
                    14,
                    currentDate.HasValue
                        ? ThemeService.Brush(ThemeService.AccentPrimary)
                        : ThemeService.Brush(ThemeService.TextSecondary)
                ),
                labelText,
                UiBuilder.Icon(
                    MaterialIconKind.ChevronDown,
                    14,
                    ThemeService.Brush(ThemeService.TextMuted)
                ),
            },
        };
        Grid.SetColumn(labelText, 1);
        Grid.SetColumn(triggerContent.Children[2], 2);

        var normalBg = ThemeService.Brush(ThemeService.BgElevated);
        var hoverBg = ThemeService.Brush(ThemeService.BgHover);
        var normalBorder = ThemeService.Brush(ThemeService.BorderSubtle);
        var hoverBorder = ThemeService.Brush(ThemeService.BorderStrong);

        var triggerBtn = new Button
        {
            Content = triggerContent,
            MinWidth = minWidth,
            Background = normalBg,
            BorderBrush = normalBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 5),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        triggerBtn.PointerEntered += (_, _) =>
        {
            triggerBtn.Background = hoverBg;
            triggerBtn.BorderBrush = hoverBorder;
        };
        triggerBtn.PointerExited += (_, _) =>
        {
            triggerBtn.Background = normalBg;
            triggerBtn.BorderBrush = normalBorder;
        };

        var flyoutHost = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(2),
            Width = 256,
        };

        var flyout = new Flyout
        {
            Content = flyoutHost,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
        };

        void UpdateTriggerVisual()
        {
            labelText.Text = FormatDateLabel(currentDate, placeholder);
            labelText.FontWeight = currentDate.HasValue ? FontWeight.Medium : FontWeight.Normal;
            labelText.Foreground = currentDate.HasValue
                ? ThemeService.Brush(ThemeService.TextPrimary)
                : ThemeService.Brush(ThemeService.TextMuted);
            if (triggerContent.Children[0] is Material.Icons.Avalonia.MaterialIcon icon)
            {
                icon.Foreground = currentDate.HasValue
                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : ThemeService.Brush(ThemeService.TextSecondary);
            }
        }

        void RebuildCalendarPopup()
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var stack = new StackPanel { Spacing = 8 };

            // Month navigation header
            var nav = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto") };
            var prevBtn = UiBuilder.IconButton(
                MaterialIconKind.ChevronLeft,
                () =>
                {
                    displayedMonth = displayedMonth.AddMonths(-1);
                    RebuildCalendarPopup();
                },
                16
            );
            nav.Children.Add(prevBtn);

            var monthTitle = new TextBlock
            {
                Text = displayedMonth.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
                FontSize = 13,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(monthTitle, 1);
            nav.Children.Add(monthTitle);

            var nextBtn = UiBuilder.IconButton(
                MaterialIconKind.ChevronRight,
                () =>
                {
                    displayedMonth = displayedMonth.AddMonths(1);
                    RebuildCalendarPopup();
                },
                16
            );
            Grid.SetColumn(nextBtn, 2);
            nav.Children.Add(nextBtn);
            stack.Children.Add(nav);

            // Day-of-week header
            var dowGrid = new UniformGrid { Columns = 7 };
            foreach (var d in ShortDayHeaders)
            {
                dowGrid.Children.Add(
                    new TextBlock
                    {
                        Text = d,
                        FontSize = 10.5,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = ThemeService.Brush(ThemeService.TextMuted),
                        HorizontalAlignment = HorizontalAlignment.Center,
                    }
                );
            }
            stack.Children.Add(dowGrid);

            // 42-day month grid
            var daysGrid = new UniformGrid { Columns = 7, Rows = 6 };
            int offset = (int)displayedMonth.DayOfWeek;
            var gridStart = displayedMonth.AddDays(-offset);

            for (int i = 0; i < 42; i++)
            {
                var cellDate = gridStart.AddDays(i);
                bool inMonth = cellDate.Month == displayedMonth.Month;
                bool isToday = cellDate == today;
                bool isSelected = currentDate.HasValue && cellDate == currentDate.Value;

                var cellNormalBg = isSelected
                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : isToday
                        ? ThemeService.Brush(ThemeService.AccentSoftBg)
                        : Brushes.Transparent;

                var cellFg = isSelected
                    ? Brushes.White
                    : isToday
                        ? ThemeService.Brush(ThemeService.AccentPrimary)
                        : inMonth
                            ? ThemeService.Brush(ThemeService.TextPrimary)
                            : ThemeService.Brush(ThemeService.TextMuted);

                var cellBorder = isToday && !isSelected
                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : Brushes.Transparent;

                var dayBtn = new Button
                {
                    Content = new TextBlock
                    {
                        Text = cellDate.Day.ToString(CultureInfo.InvariantCulture),
                        FontSize = 11.5,
                        FontWeight = isSelected || isToday ? FontWeight.Bold : FontWeight.Normal,
                        Foreground = cellFg,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    Background = cellNormalBg,
                    BorderBrush = cellBorder,
                    BorderThickness = new Thickness(isToday && !isSelected ? 1 : 0),
                    CornerRadius = new CornerRadius(5),
                    Height = 30,
                    Margin = new Thickness(1),
                    Padding = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Cursor = new Cursor(StandardCursorType.Hand),
                };

                if (!isSelected)
                {
                    dayBtn.PointerEntered += (_, _) =>
                        dayBtn.Background = ThemeService.Brush(ThemeService.BgHover);
                    dayBtn.PointerExited += (_, _) => dayBtn.Background = cellNormalBg;
                }

                dayBtn.Click += async (_, e) =>
                {
                    e.Handled = true;
                    currentDate = cellDate;
                    displayedMonth = new DateOnly(cellDate.Year, cellDate.Month, 1);
                    UpdateTriggerVisual();
                    flyout.Hide();
                    await onDateSelected(cellDate);
                };

                daysGrid.Children.Add(dayBtn);
            }
            stack.Children.Add(daysGrid);

            stack.Children.Add(UiBuilder.HairlineDivider());

            // Footer quick actions (Today, Tomorrow, Clear)
            var footer = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,Auto,*,Auto") };
            var todayBtn = UiBuilder.GhostButton(
                "Today",
                null,
                async () =>
                {
                    currentDate = today;
                    displayedMonth = new DateOnly(today.Year, today.Month, 1);
                    UpdateTriggerVisual();
                    flyout.Hide();
                    await onDateSelected(today);
                },
                isActive: currentDate == today
            );
            todayBtn.Margin = new Thickness(0, 0, 4, 0);
            footer.Children.Add(todayBtn);

            var tmrwBtn = UiBuilder.GhostButton(
                "Tmrw",
                null,
                async () =>
                {
                    var tmrw = today.AddDays(1);
                    currentDate = tmrw;
                    displayedMonth = new DateOnly(tmrw.Year, tmrw.Month, 1);
                    UpdateTriggerVisual();
                    flyout.Hide();
                    await onDateSelected(tmrw);
                },
                isActive: currentDate == today.AddDays(1)
            );
            Grid.SetColumn(tmrwBtn, 1);
            footer.Children.Add(tmrwBtn);

            var clearBtn = UiBuilder.GhostButton(
                "Clear",
                null,
                async () =>
                {
                    currentDate = null;
                    UpdateTriggerVisual();
                    flyout.Hide();
                    await onDateSelected(null);
                }
            );
            Grid.SetColumn(clearBtn, 3);
            footer.Children.Add(clearBtn);

            stack.Children.Add(footer);
            flyoutHost.Child = stack;
        }

        flyout.Opening += (_, _) =>
        {
            if (currentDate.HasValue)
                displayedMonth = new DateOnly(currentDate.Value.Year, currentDate.Value.Month, 1);
            RebuildCalendarPopup();
        };

        triggerBtn.Flyout = flyout;
        return triggerBtn;
    }

    /// <summary>
    /// Builds a themed Time / Clock Picker button that opens an interactive 12-hour Clock Flyout
    /// with 1-click Hour grid, 5-minute + exact ±1 minute controls, AM/PM toggle, and academic deadline presets.
    /// </summary>
    public static Button BuildTimePickerButton(
        TimeOnly? initialTime,
        Func<TimeOnly?, Task> onTimeSelected,
        string placeholder = "Pick time...",
        double minWidth = 132
    )
    {
        TimeOnly? committedTime = initialTime;
        int draftHour12 = initialTime.HasValue ? ToHour12(initialTime.Value.Hour) : 11;
        int draftMinute = initialTime.HasValue ? initialTime.Value.Minute : 59;
        bool draftIsPm = initialTime.HasValue ? initialTime.Value.Hour >= 12 : true;

        var labelText = new TextBlock
        {
            Text = FormatTimeLabel(committedTime, placeholder),
            FontSize = 12,
            FontWeight = committedTime.HasValue ? FontWeight.Medium : FontWeight.Normal,
            Foreground = committedTime.HasValue
                ? ThemeService.Brush(ThemeService.TextPrimary)
                : ThemeService.Brush(ThemeService.TextMuted),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var triggerContent = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
            ColumnSpacing = 6,
            Children =
            {
                UiBuilder.Icon(
                    MaterialIconKind.ClockOutline,
                    14,
                    committedTime.HasValue
                        ? ThemeService.Brush(ThemeService.AccentPrimary)
                        : ThemeService.Brush(ThemeService.TextSecondary)
                ),
                labelText,
                UiBuilder.Icon(
                    MaterialIconKind.ChevronDown,
                    14,
                    ThemeService.Brush(ThemeService.TextMuted)
                ),
            },
        };
        Grid.SetColumn(labelText, 1);
        Grid.SetColumn(triggerContent.Children[2], 2);

        var normalBg = ThemeService.Brush(ThemeService.BgElevated);
        var hoverBg = ThemeService.Brush(ThemeService.BgHover);
        var normalBorder = ThemeService.Brush(ThemeService.BorderSubtle);
        var hoverBorder = ThemeService.Brush(ThemeService.BorderStrong);

        var triggerBtn = new Button
        {
            Content = triggerContent,
            MinWidth = minWidth,
            Background = normalBg,
            BorderBrush = normalBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 5),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        triggerBtn.PointerEntered += (_, _) =>
        {
            triggerBtn.Background = hoverBg;
            triggerBtn.BorderBrush = hoverBorder;
        };
        triggerBtn.PointerExited += (_, _) =>
        {
            triggerBtn.Background = normalBg;
            triggerBtn.BorderBrush = normalBorder;
        };

        var flyoutHost = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(2),
            Width = 276,
        };

        var flyout = new Flyout
        {
            Content = flyoutHost,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
        };

        TimeOnly BuildDraftTime() => new(ToHour24(draftHour12, draftIsPm), draftMinute);

        void UpdateTriggerVisual()
        {
            labelText.Text = FormatTimeLabel(committedTime, placeholder);
            labelText.FontWeight = committedTime.HasValue ? FontWeight.Medium : FontWeight.Normal;
            labelText.Foreground = committedTime.HasValue
                ? ThemeService.Brush(ThemeService.TextPrimary)
                : ThemeService.Brush(ThemeService.TextMuted);
            if (triggerContent.Children[0] is Material.Icons.Avalonia.MaterialIcon icon)
            {
                icon.Foreground = committedTime.HasValue
                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : ThemeService.Brush(ThemeService.TextSecondary);
            }
        }

        void RebuildClockPopup()
        {
            var stack = new StackPanel { Spacing = 9 };

            // Top Digital Clock Readout + AM/PM Toggle
            var topGrid = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                ColumnSpacing = 8,
            };

            var readoutPill = new Border
            {
                Background = ThemeService.Brush(ThemeService.BgElevated),
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 6),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        UiBuilder.Icon(
                            MaterialIconKind.ClockTimeFourOutline,
                            16,
                            ThemeService.Brush(ThemeService.AccentPrimary)
                        ),
                        new TextBlock
                        {
                            Text = $"{draftHour12:00}:{draftMinute:00} {(draftIsPm ? "PM" : "AM")}",
                            FontSize = 16,
                            FontWeight = FontWeight.Bold,
                            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    },
                },
            };
            topGrid.Children.Add(readoutPill);

            var amPmStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
            };
            amPmStack.Children.Add(
                UiBuilder.GhostButton(
                    "AM",
                    null,
                    () =>
                    {
                        draftIsPm = false;
                        RebuildClockPopup();
                    },
                    isActive: !draftIsPm
                )
            );
            amPmStack.Children.Add(
                UiBuilder.GhostButton(
                    "PM",
                    null,
                    () =>
                    {
                        draftIsPm = true;
                        RebuildClockPopup();
                    },
                    isActive: draftIsPm
                )
            );
            Grid.SetColumn(amPmStack, 1);
            topGrid.Children.Add(amPmStack);
            stack.Children.Add(topGrid);

            // Hour Selector (12, 1..11)
            stack.Children.Add(UiBuilder.SectionHeader("Hour"));
            var hourGrid = new UniformGrid { Columns = 6, Rows = 2 };
            foreach (var h in ClockHours)
            {
                var capturedH = h;
                bool active = draftHour12 == capturedH;
                hourGrid.Children.Add(
                    BuildClockCell(
                        capturedH.ToString(CultureInfo.InvariantCulture),
                        active,
                        () =>
                        {
                            draftHour12 = capturedH;
                            RebuildClockPopup();
                        }
                    )
                );
            }
            stack.Children.Add(hourGrid);

            // Minute Selector (00..55 + fine ±1m + :59)
            var minHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
            minHeader.Children.Add(UiBuilder.SectionHeader("Minute"));

            var fineBtns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 3,
                VerticalAlignment = VerticalAlignment.Center,
            };
            fineBtns.Children.Add(
                UiBuilder.GhostButton(
                    "-1m",
                    null,
                    () =>
                    {
                        draftMinute = (draftMinute + 59) % 60;
                        RebuildClockPopup();
                    }
                )
            );
            fineBtns.Children.Add(
                UiBuilder.GhostButton(
                    "+1m",
                    null,
                    () =>
                    {
                        draftMinute = (draftMinute + 1) % 60;
                        RebuildClockPopup();
                    }
                )
            );
            fineBtns.Children.Add(
                UiBuilder.GhostButton(
                    ":59",
                    null,
                    () =>
                    {
                        draftMinute = 59;
                        RebuildClockPopup();
                    },
                    isActive: draftMinute == 59
                )
            );
            Grid.SetColumn(fineBtns, 1);
            minHeader.Children.Add(fineBtns);
            stack.Children.Add(minHeader);

            var minGrid = new UniformGrid { Columns = 6, Rows = 2 };
            foreach (var m in ClockMinutes)
            {
                var capturedM = m;
                bool active = draftMinute == capturedM;
                minGrid.Children.Add(
                    BuildClockCell(
                        $":{capturedM:00}",
                        active,
                        () =>
                        {
                            draftMinute = capturedM;
                            RebuildClockPopup();
                        }
                    )
                );
            }
            stack.Children.Add(minGrid);

            // Quick Academic Presets
            var presetsWrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (
                var (label, preset) in new[]
                {
                    ("9:00 AM", new TimeOnly(9, 0)),
                    ("12:00 PM", new TimeOnly(12, 0)),
                    ("5:00 PM", new TimeOnly(17, 0)),
                    ("11:59 PM", new TimeOnly(23, 59)),
                }
            )
            {
                var capturedPreset = preset;
                var pBtn = UiBuilder.GhostButton(
                    label,
                    null,
                    async () =>
                    {
                        committedTime = capturedPreset;
                        draftHour12 = ToHour12(capturedPreset.Hour);
                        draftMinute = capturedPreset.Minute;
                        draftIsPm = capturedPreset.Hour >= 12;
                        UpdateTriggerVisual();
                        flyout.Hide();
                        await onTimeSelected(capturedPreset);
                    },
                    isActive: BuildDraftTime() == capturedPreset
                );
                pBtn.Margin = new Thickness(0, 0, 4, 4);
                presetsWrap.Children.Add(pBtn);
            }
            stack.Children.Add(presetsWrap);

            stack.Children.Add(UiBuilder.HairlineDivider());

            // Footer: Clear + Apply
            var footer = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto") };
            var clearBtn = UiBuilder.GhostButton(
                "Clear",
                MaterialIconKind.Close,
                async () =>
                {
                    committedTime = null;
                    UpdateTriggerVisual();
                    flyout.Hide();
                    await onTimeSelected(null);
                }
            );
            footer.Children.Add(clearBtn);

            var applyBtn = UiBuilder.ActionButton(
                "Set Time",
                MaterialIconKind.Check,
                async () =>
                {
                    var selected = BuildDraftTime();
                    committedTime = selected;
                    UpdateTriggerVisual();
                    flyout.Hide();
                    await onTimeSelected(selected);
                },
                isPrimary: true
            );
            Grid.SetColumn(applyBtn, 2);
            footer.Children.Add(applyBtn);

            stack.Children.Add(footer);
            flyoutHost.Child = stack;
        }

        flyout.Opening += (_, _) =>
        {
            if (committedTime.HasValue)
            {
                draftHour12 = ToHour12(committedTime.Value.Hour);
                draftMinute = committedTime.Value.Minute;
                draftIsPm = committedTime.Value.Hour >= 12;
            }
            RebuildClockPopup();
        };

        triggerBtn.Flyout = flyout;
        return triggerBtn;
    }

    private static Button BuildClockCell(string label, bool isActive, Action onClick)
    {
        var normalBg = isActive
            ? ThemeService.Brush(ThemeService.AccentPrimary)
            : ThemeService.Brush(ThemeService.BgElevated);
        var fg = isActive ? Brushes.White : ThemeService.Brush(ThemeService.TextPrimary);

        var btn = new Button
        {
            Content = new TextBlock
            {
                Text = label,
                FontSize = 11.5,
                FontWeight = isActive ? FontWeight.Bold : FontWeight.Medium,
                Foreground = fg,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Background = normalBg,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Height = 28,
            Margin = new Thickness(2),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        if (!isActive)
        {
            btn.PointerEntered += (_, _) => btn.Background = ThemeService.Brush(ThemeService.BgHover);
            btn.PointerExited += (_, _) => btn.Background = normalBg;
        }

        btn.Click += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return btn;
    }

    private static string FormatDateLabel(DateOnly? date, string placeholder) =>
        date.HasValue ? date.Value.ToString("MMM d, yyyy", CultureInfo.CurrentCulture) : placeholder;

    private static string FormatTimeLabel(TimeOnly? time, string placeholder) =>
        time.HasValue ? time.Value.ToString("h:mm tt", CultureInfo.CurrentCulture) : placeholder;

    private static int ToHour12(int hour24)
    {
        int h = hour24 % 12;
        return h == 0 ? 12 : h;
    }

    private static int ToHour24(int hour12, bool isPm)
    {
        int normalized = Math.Clamp(hour12, 1, 12) % 12;
        return isPm ? normalized + 12 : normalized;
    }
}
