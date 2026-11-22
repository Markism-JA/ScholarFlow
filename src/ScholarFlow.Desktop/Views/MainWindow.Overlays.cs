using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using ScholarFlow.Core.Models;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views;

/// <summary>
/// MainWindow partial: Floating QuickAdd, Search, Notifications, Shortcuts modals and StatusBar.
/// </summary>
public sealed partial class MainWindow
{
    private Border BuildFloatingQuickAddModal()
    {
        DetachFromParent(_quickAddBox);
        _quickAddChipsContainer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
        };
        RefreshQuickAddChips();

        var stack = new StackPanel { Spacing = 10 };

        var topHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        topHeader.Children.Add(
            new TextBlock
            {
                Text = "Quick Add Task",
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        var escChip = UiBuilder.KbdChip("Esc");
        Grid.SetColumn(escChip, 1);
        topHeader.Children.Add(escChip);
        stack.Children.Add(topHeader);

        stack.Children.Add(_quickAddBox);
        stack.Children.Add(_quickAddChipsContainer);

        // Compact Attribute Selector Strip (Due Date, Priority, Course)
        var selectorsWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        void AddTokenBtn(string label, MaterialIconKind? icon, string token, string replaceRegex)
        {
            var b = UiBuilder.GhostButton(
                label,
                icon,
                () =>
                {
                    _vm.InsertQuickAddToken(token, replaceRegex);
                    _quickAddBox.Focus();
                    _quickAddBox.CaretIndex = _quickAddBox.Text?.Length ?? 0;
                }
            );
            b.Margin = new Thickness(0, 0, 5, 5);
            selectorsWrap.Children.Add(b);
        }

        var dateRegex = @"\b(today|tomorrow|tmrw|next\s+week|\+\d+d|\d{4}-\d{2}-\d{2})\b";
        var quickDatePicker = DateTimePickerWidgets.BuildDatePickerButton(
            null,
            picked =>
            {
                if (picked.HasValue)
                {
                    _vm.InsertQuickAddToken(picked.Value.ToString("yyyy-MM-dd"), dateRegex);
                }
                else
                {
                    _vm.InsertQuickAddToken(string.Empty, dateRegex);
                }
                _quickAddBox.Focus();
                _quickAddBox.CaretIndex = _quickAddBox.Text?.Length ?? 0;
                return Task.CompletedTask;
            },
            placeholder: "Pick date...",
            minWidth: 132
        );
        quickDatePicker.Margin = new Thickness(0, 0, 5, 5);
        selectorsWrap.Children.Add(quickDatePicker);

        AddTokenBtn("Today", MaterialIconKind.CalendarToday, "today", dateRegex);
        AddTokenBtn("Tomorrow", MaterialIconKind.CalendarArrowRight, "tomorrow", dateRegex);
        AddTokenBtn("Next week", MaterialIconKind.CalendarWeek, "+7d", dateRegex);

        var timeRegex =
            @"\b(?:at\s+)?(?:(?:[01]?\d|2[0-3]):[0-5]\d(?:\s?[ap]m)?|(?:1[0-2]|[1-9])\s?[ap]m)\b";
        var quickTimePicker = DateTimePickerWidgets.BuildTimePickerButton(
            null,
            picked =>
            {
                if (picked.HasValue)
                {
                    var token = picked
                        .Value.ToString("h:mmtt", System.Globalization.CultureInfo.InvariantCulture)
                        .ToLowerInvariant();
                    _vm.InsertQuickAddToken(token, timeRegex);
                }
                else
                {
                    _vm.InsertQuickAddToken(string.Empty, timeRegex);
                }
                _quickAddBox.Focus();
                _quickAddBox.CaretIndex = _quickAddBox.Text?.Length ?? 0;
                return Task.CompletedTask;
            },
            placeholder: "Pick time...",
            minWidth: 126
        );
        quickTimePicker.Margin = new Thickness(0, 0, 5, 5);
        selectorsWrap.Children.Add(quickTimePicker);

        AddTokenBtn("12:00 AM", MaterialIconKind.ClockOutline, "12:00am", timeRegex);
        AddTokenBtn("12:00 PM", MaterialIconKind.ClockOutline, "12:00pm", timeRegex);
        AddTokenBtn("5:00 PM", MaterialIconKind.ClockOutline, "5:00pm", timeRegex);
        AddTokenBtn("11:59 PM", MaterialIconKind.ClockOutline, "11:59pm", timeRegex);

        var termRegex = @"\b@?(prelims?|midterms?|pre-?finals?|finals?)\b";
        foreach (var termName in _vm.ActiveTerms)
        {
            AddTokenBtn(
                termName,
                MaterialIconKind.SchoolOutline,
                termName.ToLowerInvariant(),
                termRegex
            );
        }

        foreach (
            var p in new[] { TaskPriority.P1, TaskPriority.P2, TaskPriority.P3, TaskPriority.P4 }
        )
        {
            AddTokenBtn($"P{(int)p}", MaterialIconKind.FlagOutline, $"p{(int)p}", @"\bp[1-4]\b");
        }

        foreach (
            var c in _vm
                .Courses.Where(c => !c.IsArchived && !string.IsNullOrWhiteSpace(c.Alias))
                .Take(8)
        )
        {
            AddTokenBtn($"#{c.Alias}", null, $"#{c.Alias}", @"#[a-z0-9_-]+");
        }

        stack.Children.Add(selectorsWrap);
        stack.Children.Add(UiBuilder.HairlineDivider());

        // Footer (Cancel + Add task)
        var footer = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto") };
        footer.Children.Add(
            new TextBlock
            {
                Text = _vm.ActiveCourse is not null
                    ? $"Default course: #{_vm.ActiveCourse.Alias}"
                    : "Default: Inbox",
                FontSize = 11.5,
                Foreground = ThemeService.Brush(ThemeService.TextMuted),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );

        var cancelBtn = UiBuilder.GhostButton("Cancel", null, _vm.CloseQuickAddModal);
        cancelBtn.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(cancelBtn, 1);
        footer.Children.Add(cancelBtn);

        var submitBtn = UiBuilder.ActionButton(
            "Add task",
            MaterialIconKind.Check,
            async () => await _vm.SubmitQuickAddAsync(),
            isPrimary: true
        );
        Grid.SetColumn(submitBtn, 2);
        footer.Children.Add(submitBtn);

        stack.Children.Add(footer);

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderStrong),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            BoxShadow = BoxShadows.Parse("0 16 40 0 #66000000"),
            MaxWidth = 540,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(24, 85, 24, 24),
            Padding = new Thickness(16, 14),
            Child = stack,
        };
    }

    private Border BuildFloatingSearchModal()
    {
        DetachFromParent(_searchBox);
        _searchResultsStack = new StackPanel { Spacing = 2 };
        _searchFilterBar = new WrapPanel { Orientation = Orientation.Horizontal };

        var stack = new StackPanel { Spacing = 8 };

        var searchHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto") };
        var icon = UiBuilder.Icon(
            MaterialIconKind.Magnify,
            18,
            ThemeService.Brush(ThemeService.TextSecondary)
        );
        icon.Margin = new Thickness(4, 0, 8, 0);
        searchHeader.Children.Add(icon);

        Grid.SetColumn(_searchBox, 1);
        searchHeader.Children.Add(_searchBox);

        var escChip = UiBuilder.KbdChip("Esc");
        escChip.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(escChip, 2);
        searchHeader.Children.Add(escChip);

        stack.Children.Add(searchHeader);
        RefreshSearchFilterBar();
        stack.Children.Add(_searchFilterBar);
        stack.Children.Add(UiBuilder.HairlineDivider());

        var scroll = new ScrollViewer { MaxHeight = 360, Content = _searchResultsStack };
        stack.Children.Add(scroll);

        stack.Children.Add(UiBuilder.HairlineDivider());
        stack.Children.Add(
            new TextBlock
            {
                Text = "Up/Down to navigate · Enter to jump to match · Esc to close",
                FontSize = 11,
                Foreground = ThemeService.Brush(ThemeService.TextMuted),
            }
        );

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderStrong),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            BoxShadow = BoxShadows.Parse("0 16 40 0 #66000000"),
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(24, 70, 24, 24),
            Padding = new Thickness(14, 12),
            Child = stack,
        };
    }

    private void RefreshSearchFilterBar()
    {
        _searchFilterBar.Children.Clear();
        foreach (var cat in new[] { "All", "Tasks", "Courses", "Quizzes", "Files" })
        {
            var captured = cat;
            bool isActive = string.Equals(
                _vm.SearchCategoryFilter,
                captured,
                StringComparison.OrdinalIgnoreCase
            );
            var btn = UiBuilder.GhostButton(
                captured,
                null,
                () =>
                {
                    _vm.SetSearchCategoryFilter(captured);
                    _searchBox.Focus();
                },
                isActive: isActive
            );
            btn.Margin = new Thickness(0, 0, 4, 2);
            _searchFilterBar.Children.Add(btn);
        }

        if (_vm.SearchCategoryFilter == "All")
        {
            var hideFilesBtn = UiBuilder.GhostButton(
                _vm.ExcludeFilesInQuickSearch ? "Files Hidden" : "Hide Files",
                _vm.ExcludeFilesInQuickSearch
                    ? MaterialIconKind.EyeOffOutline
                    : MaterialIconKind.FilterRemoveOutline,
                () =>
                {
                    _vm.ToggleExcludeFilesInQuickSearch();
                    _searchBox.Focus();
                },
                isActive: _vm.ExcludeFilesInQuickSearch
            );
            hideFilesBtn.Margin = new Thickness(6, 0, 0, 2);
            _searchFilterBar.Children.Add(hideFilesBtn);
        }
    }

    private void RefreshSearchResultsList()
    {
        RefreshSearchFilterBar();
        _searchResultsStack.Children.Clear();
        if (_vm.SearchResults.Count == 0)
        {
            _searchResultsStack.Children.Add(
                new TextBlock
                {
                    Text = "No matching tasks, courses, quizzes, or files found.",
                    FontSize = 12.5,
                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    Margin = new Thickness(8, 16),
                }
            );
            return;
        }

        for (int i = 0; i < _vm.SearchResults.Count; i++)
        {
            int idx = i;
            var item = _vm.SearchResults[i];
            bool isSelected = i == _vm.SelectedSearchResultIndex;

            var iconKind = item.Kind switch
            {
                SearchResultKind.Course => MaterialIconKind.Pound,
                SearchResultKind.PdfDocument => MaterialIconKind.FilePdfBox,
                SearchResultKind.CourseFile => item.PdfPath?.EndsWith(
                    ".pdf",
                    StringComparison.OrdinalIgnoreCase
                ) == true
                    ? MaterialIconKind.FilePdfBox
                    : MaterialIconKind.FileDocumentOutline,
                SearchResultKind.Quiz => MaterialIconKind.HeadLightbulbOutline,
                _ => MaterialIconKind.CheckboxBlankCircleOutline,
            };

            var iconFg = item.Kind switch
            {
                SearchResultKind.PdfDocument => ThemeService.Brush(ThemeService.AccentPrimary),
                SearchResultKind.CourseFile => item.PdfPath?.EndsWith(
                    ".pdf",
                    StringComparison.OrdinalIgnoreCase
                ) == true
                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : ThemeService.Brush(ThemeService.TextSecondary),
                SearchResultKind.Course => ThemeService.Brush(ThemeService.PriorityP3Fg),
                SearchResultKind.Quiz => ThemeService.Brush(ThemeService.PriorityP2Fg),
                _ => ThemeService.Brush(ThemeService.TextSecondary),
            };

            var rowGrid = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("24,*,Auto,Auto"),
            };
            rowGrid.Children.Add(UiBuilder.Icon(iconKind, 15.5, iconFg));

            rowGrid.Children.Add(
                new TextBlock
                {
                    Text = item.Title,
                    FontSize = 13,
                    FontWeight = isSelected ? FontWeight.SemiBold : FontWeight.Normal,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    [Grid.ColumnProperty] = 1,
                }
            );

            rowGrid.Children.Add(
                new TextBlock
                {
                    Text = item.Subtitle,
                    FontSize = 11.5,
                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0),
                    [Grid.ColumnProperty] = 2,
                }
            );

            var badge = UiBuilder.Pill(
                item.Badge,
                ThemeService.Brush(ThemeService.TextSecondary),
                ThemeService.Brush(ThemeService.BgElevated),
                fontSize: 10.5
            );
            Grid.SetColumn(badge, 3);
            rowGrid.Children.Add(badge);

            var normalBg = isSelected
                ? ThemeService.Brush(ThemeService.AccentSoftBg)
                : Brushes.Transparent;
            var btn = new Button
            {
                Content = rowGrid,
                Background = normalBg,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 7),
                Cursor = new Cursor(StandardCursorType.Hand),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            btn.PointerEntered += (_, _) =>
                btn.Background = isSelected ? normalBg : ThemeService.Brush(ThemeService.BgHover);
            btn.PointerExited += (_, _) => btn.Background = normalBg;
            btn.Click += (_, e) =>
            {
                e.Handled = true;
                _vm.ExecuteSearchResult(item);
            };

            _searchResultsStack.Children.Add(btn);
            if (isSelected)
            {
                var selectedBtn = btn;
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => selectedBtn.BringIntoView(),
                    Avalonia.Threading.DispatcherPriority.Loaded
                );
            }
        }
    }

    private Border BuildFloatingNotificationsModal()
    {
        var stack = new StackPanel { Spacing = 8 };
        var header = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        header.Children.Add(
            new TextBlock
            {
                Text = $"Notifications ({_vm.Notifications.Count})",
                FontSize = 14,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        var closeBtn = UiBuilder.IconButton(MaterialIconKind.Close, _vm.ToggleNotificationDrawer);
        Grid.SetColumn(closeBtn, 1);
        header.Children.Add(closeBtn);
        stack.Children.Add(header);
        stack.Children.Add(UiBuilder.HairlineDivider());

        if (_vm.Notifications.Count == 0)
        {
            stack.Children.Add(
                new TextBlock
                {
                    Text =
                        "No notifications yet. Background eLMS polling will post new deadlines and grades here.",
                    FontSize = 12.5,
                    Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                    Margin = new Thickness(0, 12),
                }
            );
        }
        else
        {
            foreach (var n in _vm.Notifications.Take(8))
            {
                var nRow = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
                    Margin = new Thickness(0, 4),
                };
                nRow.Children.Add(
                    UiBuilder.Icon(
                        MaterialIconKind.BellOutline,
                        15,
                        ThemeService.Brush(ThemeService.AccentPrimary)
                    )
                );
                nRow.Children.Add(
                    new StackPanel
                    {
                        Margin = new Thickness(8, 0),
                        Children =
                        {
                            new TextBlock
                            {
                                Text = n.Title,
                                FontSize = 12.5,
                                FontWeight = FontWeight.SemiBold,
                                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                            },
                            new TextBlock
                            {
                                Text = n.Message,
                                FontSize = 11.5,
                                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                            },
                        },
                        [Grid.ColumnProperty] = 1,
                    }
                );
                nRow.Children.Add(
                    new TextBlock
                    {
                        Text = n.OccurredAt.LocalDateTime.ToString("h:mm tt"),
                        FontSize = 11,
                        Foreground = ThemeService.Brush(ThemeService.TextMuted),
                        [Grid.ColumnProperty] = 2,
                    }
                );
                stack.Children.Add(nRow);
            }
        }

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderStrong),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            BoxShadow = BoxShadows.Parse("0 16 40 0 #66000000"),
            MaxWidth = 480,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(24, 70, 24, 24),
            Padding = new Thickness(16, 12),
            Child = stack,
        };
    }

    private Border BuildFloatingShortcutsModal()
    {
        var stack = new StackPanel { Spacing = 8 };
        var header = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        header.Children.Add(
            new TextBlock
            {
                Text = "Keyboard Shortcuts",
                FontSize = 14,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        var closeBtn = UiBuilder.IconButton(MaterialIconKind.Close, _vm.ToggleShortcutsModal);
        Grid.SetColumn(closeBtn, 1);
        header.Children.Add(closeBtn);
        stack.Children.Add(header);
        stack.Children.Add(UiBuilder.HairlineDivider());

        (string Key, string Desc)[] items =
        [
            ("Q", "Open floating Quick-Add Task modal"),
            ("/ or Ctrl+K", "Open Quick-Search & Navigation palette"),
            ("Ctrl+Shift+F", "Open Knowledge Hub FTS5 Search (#course @pdf @code @quiz @weak)"),
            ("Esc", "Close active modal or Task Inspector drawer"),
            ("j / k or Up / Down", "Move task selection or turn PDF pages"),
            ("g / Shift+G", "Jump to top / bottom item"),
            ("Enter or E", "Inspect selected task (or open primary file/PDF)"),
            ("Space or D", "Complete / reopen selected task"),
            ("O", "Open selected assignment in STI eLMS app"),
            ("[ or Ctrl+B", "Collapse / expand left navigation sidebar"),
            (
                "1 - 6",
                "Switch smart views (Inbox, Today, Upcoming, Calendar, Completed, Test & Recall)"
            ),
            ("R / Ctrl+S", "Fast poll assignments / Run full eLMS sync"),
            ("Shift+P", "Pause / resume background eLMS polling (single-session shield)"),
            ("T", "Toggle Dark / Light mode"),
        ];

        foreach (var (key, desc) in items)
        {
            var row = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("130,*"),
                Margin = new Thickness(0, 2),
            };
            var chip = UiBuilder.KbdChip(key);
            chip.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(chip);
            row.Children.Add(
                new TextBlock
                {
                    Text = desc,
                    FontSize = 12,
                    Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    [Grid.ColumnProperty] = 1,
                }
            );
            stack.Children.Add(row);
        }

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderStrong),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            BoxShadow = BoxShadows.Parse("0 16 40 0 #66000000"),
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(24, 70, 24, 24),
            Padding = new Thickness(16, 14),
            Child = stack,
        };
    }

    private void RefreshQuickAddChips()
    {
        _quickAddChipsContainer.Children.Clear();
        if (_vm.QuickAddChips.Count == 0)
        {
            _quickAddChipsContainer.IsVisible = false;
            return;
        }

        _quickAddChipsContainer.IsVisible = true;
        foreach (var chip in _vm.QuickAddChips)
        {
            _quickAddChipsContainer.Children.Add(
                UiBuilder.Pill(
                    $"{chip.Kind}: {chip.Text}",
                    ThemeService.Brush(ThemeService.AccentPrimary),
                    ThemeService.Brush(ThemeService.AccentSoftBg)
                )
            );
        }
    }

    private async void OnQuickAddKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await _vm.SubmitQuickAddAsync();
            Focus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _vm.CloseQuickAddModal();
            Focus();
        }
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _vm.CloseSearchModal();
            Focus();
        }
        else if (e.Key == Key.Down)
        {
            e.Handled = true;
            _vm.MoveSearchSelection(+1);
        }
        else if (e.Key == Key.Up)
        {
            e.Handled = true;
            _vm.MoveSearchSelection(-1);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _vm.ExecuteSelectedSearchResult();
            Focus();
        }
    }

    // =========================================================================
    // 4. QUIET MINIMAL STATUS BAR & KEYBOARD NAVIGATION
    // =========================================================================

    private Border BuildStatusBar()
    {
        bool isPaused = _vm.EnforcesActiveSingleSession && _vm.IsPollingPaused;
        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            Margin = new Thickness(12, 0),
        };

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        left.Children.Add(
            UiBuilder.Icon(
                isPaused ? MaterialIconKind.PauseCircle
                    : _vm.IsSyncing ? MaterialIconKind.Sync
                    : MaterialIconKind.CheckCircleOutline,
                13,
                isPaused ? ThemeService.Brush(ThemeService.PriorityP2Fg)
                    : _vm.IsSyncing ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : ThemeService.Brush(ThemeService.TextMuted)
            )
        );
        left.Children.Add(
            new TextBlock
            {
                Text = isPaused
                    ? $"{_vm.ActiveProviderName.ToUpperInvariant()} POLLING PAUSED (Browser session protected)"
                    : _vm.StatusText,
                FontSize = 11,
                FontWeight = isPaused ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground = isPaused
                    ? ThemeService.Brush(ThemeService.PriorityP2Fg)
                    : ThemeService.Brush(ThemeService.TextMuted),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            }
        );
        if (isPaused)
        {
            var resumeBtn = UiBuilder.GhostButton(
                "Resume",
                MaterialIconKind.PlayCircleOutline,
                _vm.TogglePollingPaused,
                isActive: true
            );
            resumeBtn.Padding = new Thickness(6, 1);
            left.Children.Add(resumeBtn);
        }
        grid.Children.Add(left);

        var rightHints = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        rightHints.Children.Add(
            new TextBlock
            {
                Text = _vm.EnforcesActiveSingleSession
                    ? "Q Add · / Search · j/k Nav · Shift+P Pause Sync · ? Help"
                    : "Q Add · / Search · j/k Nav · Enter Inspect · ? Help",
                FontSize = 11,
                Foreground = isPaused
                    ? ThemeService.Brush(ThemeService.PriorityP2Fg)
                    : ThemeService.Brush(ThemeService.TextMuted),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        Grid.SetColumn(rightHints, 1);
        grid.Children.Add(rightHints);

        return new Border
        {
            Background = isPaused
                ? ThemeService.Brush(ThemeService.PriorityP2Bg)
                : ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = isPaused
                ? ThemeService.Brush(ThemeService.PriorityP2Fg)
                : ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = grid,
        };
    }
}
