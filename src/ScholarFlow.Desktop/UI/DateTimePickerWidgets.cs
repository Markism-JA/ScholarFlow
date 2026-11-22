using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using ScholarFlow.Desktop.Theme;

namespace ScholarFlow.Desktop.UI;

/// <summary>
/// Pure-C# themed Month Date Picker and Digital Scroll-Drum Time Picker widgets.
/// Uses ThemeService brushes exclusively so Dark and Light modes stay 100% cohesive
/// across Task Overview (Inspector) and Quick Add without unthemed Fluent popups.
/// </summary>
public static class DateTimePickerWidgets
{
    private static readonly string[] ShortDayHeaders = ["Su", "Mo", "Tu", "We", "Th", "Fr", "Sa"];

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

                var cellNormalBg =
                    isSelected ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : isToday ? ThemeService.Brush(ThemeService.AccentSoftBg)
                    : Brushes.Transparent;

                var cellFg =
                    isSelected ? Brushes.White
                    : isToday ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : inMonth ? ThemeService.Brush(ThemeService.TextPrimary)
                    : ThemeService.Brush(ThemeService.TextMuted);

                var cellBorder =
                    isToday && !isSelected
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
            var footer = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("Auto,Auto,*,Auto"),
            };
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
    /// Builds a themed Time Picker button that opens a compact digital time input
    /// paired with 3 synchronized scroll-drum columns (Hour, Minute, AM/PM) and quick presets.
    /// </summary>
    public static Button BuildTimePickerButton(
        TimeOnly? initialTime,
        Func<TimeOnly?, Task> onTimeSelected,
        string placeholder = "Pick time...",
        double minWidth = 132
    )
    {
        const double DrumRowHeight = 28;
        const double DrumHeight = 152;

        TimeOnly? committedTime = initialTime;
        int draftHour12 = initialTime.HasValue ? ToHour12(initialTime.Value.Hour) : 11;
        int draftMinute = initialTime.HasValue ? initialTime.Value.Minute : 59;
        bool draftIsPm = !initialTime.HasValue || initialTime.Value.Hour >= 12;

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

        var triggerIcon = UiBuilder.Icon(
            MaterialIconKind.ClockOutline,
            14,
            committedTime.HasValue
                ? ThemeService.Brush(ThemeService.TextPrimary)
                : ThemeService.Brush(ThemeService.TextSecondary)
        );

        var triggerContent = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
            ColumnSpacing = 6,
            Children =
            {
                triggerIcon,
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
            Width = 236,
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
            triggerIcon.Foreground = committedTime.HasValue
                ? ThemeService.Brush(ThemeService.TextPrimary)
                : ThemeService.Brush(ThemeService.TextSecondary);
        }

        async Task CommitDraftAndCloseAsync()
        {
            var selected = BuildDraftTime();
            committedTime = selected;
            UpdateTriggerVisual();
            flyout.Hide();
            await onTimeSelected(selected);
        }

        void BuildTimePopup()
        {
            bool syncingInputs = false;

            var hourButtons = new Button[12];
            var minuteButtons = new Button[60];
            Button amBtn = null!;
            Button pmBtn = null!;
            Button periodToggleBtn = null!;
            ScrollViewer hourScroll = null!;
            ScrollViewer minuteScroll = null!;

            TextBox hourBox = BuildTimeInputBox(
                draftHour12.ToString("00", CultureInfo.InvariantCulture),
                "Type or scroll hour (1–12)"
            );
            TextBox minuteBox = BuildTimeInputBox(
                draftMinute.ToString("00", CultureInfo.InvariantCulture),
                "Type or scroll minute (00–59)"
            );

            void ScrollToSelection()
            {
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        double hourTarget = Math.Max(
                            0,
                            (draftHour12 - 1) * DrumRowHeight - (DrumHeight - DrumRowHeight) / 2.0
                        );
                        double minTarget = Math.Max(
                            0,
                            draftMinute * DrumRowHeight - (DrumHeight - DrumRowHeight) / 2.0
                        );
                        hourScroll.Offset = new Vector(0, hourTarget);
                        minuteScroll.Offset = new Vector(0, minTarget);
                    },
                    DispatcherPriority.Loaded
                );
            }

            void SyncVisuals(bool scrollIntoView = false, bool syncTextBoxes = true)
            {
                if (syncTextBoxes)
                {
                    syncingInputs = true;
                    hourBox.Text = draftHour12.ToString("00", CultureInfo.InvariantCulture);
                    minuteBox.Text = draftMinute.ToString("00", CultureInfo.InvariantCulture);
                    syncingInputs = false;
                }

                for (int i = 0; i < 12; i++)
                {
                    StyleDrumRow(hourButtons[i], isSelected: draftHour12 == i + 1);
                }

                for (int i = 0; i < 60; i++)
                {
                    StyleDrumRow(minuteButtons[i], isSelected: draftMinute == i);
                }

                StyleDrumRow(amBtn, isSelected: !draftIsPm);
                StyleDrumRow(pmBtn, isSelected: draftIsPm);
                if (periodToggleBtn.Content is TextBlock periodTb)
                    periodTb.Text = draftIsPm ? "PM" : "AM";

                if (scrollIntoView)
                    ScrollToSelection();
            }

            void StepHour(int delta)
            {
                draftHour12 = ((draftHour12 - 1 + delta) % 12 + 12) % 12 + 1;
                SyncVisuals(scrollIntoView: true);
            }

            void StepMinute(int delta)
            {
                draftMinute = ((draftMinute + delta) % 60 + 60) % 60;
                SyncVisuals(scrollIntoView: true);
            }

            // Direct typing + keyboard arrows + mouse wheel for Hour
            hourBox.TextChanged += (_, _) =>
            {
                if (syncingInputs)
                    return;
                var raw = (hourBox.Text ?? "").Trim();
                if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int h))
                    return;

                if (h == 0)
                {
                    draftHour12 = 12;
                    draftIsPm = false;
                }
                else if (h is >= 1 and <= 12)
                {
                    draftHour12 = h;
                }
                else if (h is >= 13 and <= 23)
                {
                    draftHour12 = h - 12;
                    draftIsPm = true;
                }
                else
                {
                    return;
                }

                SyncVisuals(scrollIntoView: true, syncTextBoxes: false);
            };
            hourBox.LostFocus += (_, _) => SyncVisuals(scrollIntoView: false, syncTextBoxes: true);
            hourBox.KeyDown += async (_, e) =>
            {
                if (e.Key == Key.Up)
                {
                    e.Handled = true;
                    StepHour(1);
                }
                else if (e.Key == Key.Down)
                {
                    e.Handled = true;
                    StepHour(-1);
                }
                else if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    await CommitDraftAndCloseAsync();
                }
                else if (e.Key == Key.OemSemicolon)
                {
                    e.Handled = true;
                    minuteBox.Focus();
                    minuteBox.SelectAll();
                }
            };
            hourBox.PointerWheelChanged += (_, e) =>
            {
                e.Handled = true;
                StepHour(e.Delta.Y > 0 ? 1 : -1);
            };

            // Direct typing + keyboard arrows + mouse wheel for Minute
            minuteBox.TextChanged += (_, _) =>
            {
                if (syncingInputs)
                    return;
                var raw = (minuteBox.Text ?? "").Trim();
                if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int m))
                    return;
                if (m is < 0 or > 59)
                    return;

                draftMinute = m;
                SyncVisuals(scrollIntoView: true, syncTextBoxes: false);
            };
            minuteBox.LostFocus += (_, _) => SyncVisuals(scrollIntoView: false, syncTextBoxes: true);
            minuteBox.KeyDown += async (_, e) =>
            {
                if (e.Key == Key.Up)
                {
                    e.Handled = true;
                    StepMinute(1);
                }
                else if (e.Key == Key.Down)
                {
                    e.Handled = true;
                    StepMinute(-1);
                }
                else if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    await CommitDraftAndCloseAsync();
                }
            };
            minuteBox.PointerWheelChanged += (_, e) =>
            {
                e.Handled = true;
                StepMinute(e.Delta.Y > 0 ? 1 : -1);
            };

            // Top digital input bar: [ HH ] : [ MM ]   [AM/PM]
            var topInputRow = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,*,Auto"),
                ColumnSpacing = 6,
            };

            var colonText = new TextBlock
            {
                Text = ":",
                FontSize = 18,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeService.Brush(ThemeService.TextMuted),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            periodToggleBtn = new Button
            {
                Content = new TextBlock
                {
                    Text = draftIsPm ? "PM" : "AM",
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                Width = 48,
                Height = 34,
                Background = ThemeService.Brush(ThemeService.BgElevated),
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(periodToggleBtn, "Toggle AM / PM");
            periodToggleBtn.PointerEntered += (_, _) =>
                periodToggleBtn.Background = ThemeService.Brush(ThemeService.BgHover);
            periodToggleBtn.PointerExited += (_, _) =>
                periodToggleBtn.Background = ThemeService.Brush(ThemeService.BgElevated);
            periodToggleBtn.Click += (_, e) =>
            {
                e.Handled = true;
                draftIsPm = !draftIsPm;
                SyncVisuals(scrollIntoView: false, syncTextBoxes: false);
            };

            topInputRow.Children.Add(hourBox);
            Grid.SetColumn(colonText, 1);
            topInputRow.Children.Add(colonText);
            Grid.SetColumn(minuteBox, 2);
            topInputRow.Children.Add(minuteBox);
            Grid.SetColumn(periodToggleBtn, 3);
            topInputRow.Children.Add(periodToggleBtn);

            // Column headers for the 3-column time drum
            var colHeaders = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,*,54"),
                ColumnSpacing = 6,
                Margin = new Thickness(2, 2, 2, 0),
            };
            var hrHdr = BuildDrumHeader("HR");
            var minHdr = BuildDrumHeader("MIN");
            var perHdr = BuildDrumHeader("AM/PM");
            colHeaders.Children.Add(hrHdr);
            Grid.SetColumn(minHdr, 1);
            colHeaders.Children.Add(minHdr);
            Grid.SetColumn(perHdr, 2);
            colHeaders.Children.Add(perHdr);

            // Hour scroll column (1..12)
            var hourList = new StackPanel { Spacing = 0 };
            for (int i = 0; i < 12; i++)
            {
                int capturedH = i + 1;
                var rowBtn = BuildDrumRow(
                    capturedH.ToString("00", CultureInfo.InvariantCulture),
                    DrumRowHeight,
                    () =>
                    {
                        draftHour12 = capturedH;
                        SyncVisuals(scrollIntoView: false);
                    }
                );
                hourButtons[i] = rowBtn;
                hourList.Children.Add(rowBtn);
            }
            hourScroll = new ScrollViewer
            {
                Height = DrumHeight,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = hourList,
            };

            // Minute scroll column (00..59)
            var minuteList = new StackPanel { Spacing = 0 };
            for (int i = 0; i < 60; i++)
            {
                int capturedM = i;
                var rowBtn = BuildDrumRow(
                    capturedM.ToString("00", CultureInfo.InvariantCulture),
                    DrumRowHeight,
                    () =>
                    {
                        draftMinute = capturedM;
                        SyncVisuals(scrollIntoView: false);
                    }
                );
                minuteButtons[i] = rowBtn;
                minuteList.Children.Add(rowBtn);
            }
            minuteScroll = new ScrollViewer
            {
                Height = DrumHeight,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = minuteList,
            };

            // AM / PM column
            amBtn = BuildDrumRow(
                "AM",
                DrumRowHeight,
                () =>
                {
                    draftIsPm = false;
                    SyncVisuals(scrollIntoView: false, syncTextBoxes: false);
                }
            );
            pmBtn = BuildDrumRow(
                "PM",
                DrumRowHeight,
                () =>
                {
                    draftIsPm = true;
                    SyncVisuals(scrollIntoView: false, syncTextBoxes: false);
                }
            );
            var periodList = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Top,
                Children = { amBtn, pmBtn },
            };

            var drumGrid = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,1,*,1,54"),
                Height = DrumHeight,
            };
            drumGrid.Children.Add(hourScroll);

            var sep1 = new Border
            {
                Background = ThemeService.Brush(ThemeService.BorderSubtle),
                Width = 1,
                Margin = new Thickness(0, 6),
            };
            Grid.SetColumn(sep1, 1);
            drumGrid.Children.Add(sep1);

            Grid.SetColumn(minuteScroll, 2);
            drumGrid.Children.Add(minuteScroll);

            var sep2 = new Border
            {
                Background = ThemeService.Brush(ThemeService.BorderSubtle),
                Width = 1,
                Margin = new Thickness(0, 6),
            };
            Grid.SetColumn(sep2, 3);
            drumGrid.Children.Add(sep2);

            Grid.SetColumn(periodList, 4);
            drumGrid.Children.Add(periodList);

            var drumFrame = new Border
            {
                Background = ThemeService.Brush(ThemeService.BgSurface),
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(3, 4),
                Child = drumGrid,
            };

            // Compact 1-row quick academic deadline presets
            var presetsGrid = new UniformGrid { Columns = 4 };
            foreach (
                var (label, preset) in new[]
                {
                    ("9 AM", new TimeOnly(9, 0)),
                    ("12 PM", new TimeOnly(12, 0)),
                    ("5 PM", new TimeOnly(17, 0)),
                    ("11:59p", new TimeOnly(23, 59)),
                }
            )
            {
                var capturedPreset = preset;
                bool isCurrent = BuildDraftTime() == capturedPreset;
                var chip = new Button
                {
                    Content = new TextBlock
                    {
                        Text = label,
                        FontSize = 11,
                        FontWeight = isCurrent ? FontWeight.SemiBold : FontWeight.Normal,
                        Foreground = isCurrent
                            ? ThemeService.Brush(ThemeService.TextPrimary)
                            : ThemeService.Brush(ThemeService.TextSecondary),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    Background = isCurrent
                        ? ThemeService.Brush(ThemeService.BgElevated)
                        : Brushes.Transparent,
                    BorderBrush = isCurrent
                        ? ThemeService.Brush(ThemeService.BorderStrong)
                        : ThemeService.Brush(ThemeService.BorderSubtle),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Height = 25,
                    Margin = new Thickness(2, 0),
                    Padding = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Cursor = new Cursor(StandardCursorType.Hand),
                };
                ToolTip.SetTip(
                    chip,
                    $"Set to {capturedPreset.ToString("h:mm tt", CultureInfo.CurrentCulture)}"
                );
                var baseChipBg = chip.Background;
                chip.PointerEntered += (_, _) =>
                    chip.Background = ThemeService.Brush(ThemeService.BgHover);
                chip.PointerExited += (_, _) => chip.Background = baseChipBg;
                chip.Click += async (_, e) =>
                {
                    e.Handled = true;
                    committedTime = capturedPreset;
                    draftHour12 = ToHour12(capturedPreset.Hour);
                    draftMinute = capturedPreset.Minute;
                    draftIsPm = capturedPreset.Hour >= 12;
                    UpdateTriggerVisual();
                    flyout.Hide();
                    await onTimeSelected(capturedPreset);
                };
                presetsGrid.Children.Add(chip);
            }

            // Footer: Clear + Set Time
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
                async () => await CommitDraftAndCloseAsync(),
                isPrimary: true
            );
            Grid.SetColumn(applyBtn, 2);
            footer.Children.Add(applyBtn);

            var stack = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    topInputRow,
                    colHeaders,
                    drumFrame,
                    presetsGrid,
                    UiBuilder.HairlineDivider(),
                    footer,
                },
            };

            flyoutHost.Child = stack;
            SyncVisuals(scrollIntoView: true);
        }

        flyout.Opening += (_, _) =>
        {
            if (committedTime.HasValue)
            {
                draftHour12 = ToHour12(committedTime.Value.Hour);
                draftMinute = committedTime.Value.Minute;
                draftIsPm = committedTime.Value.Hour >= 12;
            }
            BuildTimePopup();
        };

        triggerBtn.Flyout = flyout;
        return triggerBtn;
    }

    private static TextBox BuildTimeInputBox(string initialText, string tooltip)
    {
        var box = new TextBox
        {
            Text = initialText,
            MaxLength = 2,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Height = 34,
            MinHeight = 34,
            Padding = new Thickness(4, 5),
            TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = ThemeService.Brush(ThemeService.BgElevated),
            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
        };
        ToolTip.SetTip(box, tooltip);
        box.GotFocus += (_, _) => box.SelectAll();
        return box;
    }

    private static TextBlock BuildDrumHeader(string text) =>
        new()
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeService.Brush(ThemeService.TextMuted),
            HorizontalAlignment = HorizontalAlignment.Center,
            LetterSpacing = 0.6,
        };

    private static Button BuildDrumRow(string label, double height, Action onClick)
    {
        var btn = new Button
        {
            Content = new TextBlock
            {
                Text = label,
                FontSize = 12.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Height = height,
            Margin = new Thickness(3, 1),
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = false,
        };

        btn.PointerEntered += (_, _) =>
        {
            if (btn.Tag is not true)
                btn.Background = ThemeService.Brush(ThemeService.BgHover);
        };
        btn.PointerExited += (_, _) =>
        {
            if (btn.Tag is not true)
                btn.Background = Brushes.Transparent;
        };
        btn.Click += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return btn;
    }

    private static void StyleDrumRow(Button btn, bool isSelected)
    {
        btn.Tag = isSelected;
        btn.Background = isSelected
            ? ThemeService.Brush(ThemeService.BgElevated)
            : Brushes.Transparent;
        btn.BorderBrush = isSelected
            ? ThemeService.Brush(ThemeService.BorderStrong)
            : Brushes.Transparent;
        if (btn.Content is TextBlock tb)
        {
            tb.FontWeight = isSelected ? FontWeight.Bold : FontWeight.Normal;
            tb.Foreground = isSelected
                ? ThemeService.Brush(ThemeService.TextPrimary)
                : ThemeService.Brush(ThemeService.TextSecondary);
        }
    }

    private static string FormatDateLabel(DateOnly? date, string placeholder) =>
        date.HasValue
            ? date.Value.ToString("MMM d, yyyy", CultureInfo.CurrentCulture)
            : placeholder;

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
