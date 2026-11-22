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
        static string FormatRelativeTime(DateTimeOffset occurredAt)
        {
            var local = occurredAt.LocalDateTime;
            var diff = DateTime.Now - local;
            if (diff.TotalMinutes < 1)
                return "Just now";
            if (diff.TotalMinutes < 60)
                return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalHours < 24 && local.Date == DateTime.Today)
                return $"{(int)diff.TotalHours}h ago";
            if (local.Date == DateTime.Today.AddDays(-1))
                return $"Yesterday · {local:h:mm tt}";
            return local.ToString("MMM d · h:mm tt");
        }

        static (MaterialIconKind Icon, IBrush Fg) GetNotificationVisual(AppNotification n)
        {
            var combined = $"{n.Title} {n.Message}";
            if (
                combined.Contains("fail", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("error", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("overdue", StringComparison.OrdinalIgnoreCase)
            )
                return (MaterialIconKind.AlertCircleOutline, ThemeService.Brush(ThemeService.PriorityP1Fg));

            if (
                combined.Contains("due", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("deadline", StringComparison.OrdinalIgnoreCase)
            )
                return (MaterialIconKind.ClockAlertOutline, ThemeService.Brush(ThemeService.PriorityP2Fg));

            if (
                combined.Contains("sync", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("grade", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("new", StringComparison.OrdinalIgnoreCase)
            )
                return (MaterialIconKind.CheckCircleOutline, ThemeService.Brush(ThemeService.SuccessFg));

            return (MaterialIconKind.BellOutline, ThemeService.Brush(ThemeService.TextSecondary));
        }

        var rootGrid = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,Auto,*"),
            RowSpacing = 8,
        };

        // Header
        var header = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,Auto"),
            ColumnSpacing = 8,
        };
        header.Children.Add(
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    UiBuilder.Icon(
                        MaterialIconKind.BellOutline,
                        16,
                        ThemeService.Brush(ThemeService.TextSecondary)
                    ),
                    new TextBlock
                    {
                        Text = "Notifications",
                        FontSize = 14,
                        FontWeight = FontWeight.Bold,
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    UiBuilder.Pill(
                        _vm.Notifications.Count.ToString(),
                        ThemeService.Brush(ThemeService.TextSecondary),
                        ThemeService.Brush(ThemeService.BgElevated),
                        fontSize: 10.5
                    ),
                },
            }
        );

        if (_vm.Notifications.Count > 0)
        {
            var clearAllBtn = UiBuilder.GhostButton(
                "Clear all",
                MaterialIconKind.NotificationClearAll,
                _vm.ClearNotifications
            );
            ToolTip.SetTip(clearAllBtn, "Dismiss all notifications");
            Grid.SetColumn(clearAllBtn, 2);
            header.Children.Add(clearAllBtn);
        }

        var closeBtn = UiBuilder.IconButton(MaterialIconKind.Close, _vm.ToggleNotificationDrawer);
        ToolTip.SetTip(closeBtn, "Close notifications (Esc)");
        Grid.SetColumn(closeBtn, 3);
        header.Children.Add(closeBtn);
        rootGrid.Children.Add(header);

        var divider = UiBuilder.HairlineDivider();
        Grid.SetRow(divider, 1);
        rootGrid.Children.Add(divider);

        Control bodyContent;
        if (_vm.Notifications.Count == 0)
        {
            var emptyStack = new StackPanel
            {
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 24),
                Children =
                {
                    new Border
                    {
                        Width = 40,
                        Height = 40,
                        CornerRadius = new CornerRadius(20),
                        Background = ThemeService.Brush(ThemeService.BgElevated),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Child = UiBuilder.Icon(
                            MaterialIconKind.BellCheckOutline,
                            20,
                            ThemeService.Brush(ThemeService.TextSecondary)
                        ),
                    },
                    new TextBlock
                    {
                        Text = "You're all caught up",
                        FontSize = 13.5,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text =
                            "New deadlines, grade updates, and background sync alerts will appear here.",
                        FontSize = 12,
                        Foreground = ThemeService.Brush(ThemeService.TextMuted),
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center,
                    },
                },
            };
            var settingsShortcut = UiBuilder.GhostButton(
                _vm.Settings.EnableDesktopNotifications
                    ? "Desktop Alerts: On"
                    : "Desktop Alerts: Off",
                MaterialIconKind.CogOutline,
                () =>
                {
                    _vm.ToggleNotificationDrawer();
                    _vm.ToggleSettingsModal();
                }
            );
            settingsShortcut.HorizontalAlignment = HorizontalAlignment.Center;
            settingsShortcut.Margin = new Thickness(0, 4, 0, 0);
            emptyStack.Children.Add(settingsShortcut);
            bodyContent = emptyStack;
        }
        else
        {
            var listStack = new StackPanel { Spacing = 6, Margin = new Thickness(0, 2, 4, 2) };
            foreach (var n in _vm.Notifications.Take(50))
            {
                var captured = n;
                var (iconKind, iconFg) = GetNotificationVisual(captured);

                var nRow = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
                    ColumnSpacing = 9,
                };
                var iconWrap = new Border
                {
                    Width = 28,
                    Height = 28,
                    CornerRadius = new CornerRadius(6),
                    Background = ThemeService.Brush(ThemeService.BgElevated),
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = UiBuilder.Icon(iconKind, 15, iconFg),
                };
                nRow.Children.Add(iconWrap);

                var textCol = new StackPanel
                {
                    Spacing = 2,
                    [Grid.ColumnProperty] = 1,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = captured.Title,
                            FontSize = 12.5,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new TextBlock
                        {
                            Text = captured.Message,
                            FontSize = 11.5,
                            Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new TextBlock
                        {
                            Text = FormatRelativeTime(captured.OccurredAt),
                            FontSize = 10.5,
                            Foreground = ThemeService.Brush(ThemeService.TextMuted),
                            Margin = new Thickness(0, 2, 0, 0),
                        },
                    },
                };
                nRow.Children.Add(textCol);

                var dismissBtn = UiBuilder.IconButton(
                    MaterialIconKind.Close,
                    () => _vm.DismissNotification(captured),
                    13,
                    ThemeService.Brush(ThemeService.TextMuted)
                );
                dismissBtn.VerticalAlignment = VerticalAlignment.Top;
                ToolTip.SetTip(dismissBtn, "Dismiss notification");
                Grid.SetColumn(dismissBtn, 2);
                nRow.Children.Add(dismissBtn);

                listStack.Children.Add(
                    new Border
                    {
                        Background = ThemeService.Brush(ThemeService.BgBase),
                        BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(7),
                        Padding = new Thickness(10, 8),
                        Child = nRow,
                    }
                );
            }

            bodyContent = new ScrollViewer
            {
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility =
                    Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = listStack,
            };
        }

        Grid.SetRow(bodyContent, 2);
        rootGrid.Children.Add(bodyContent);

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderStrong),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            BoxShadow = BoxShadows.Parse("0 16 40 0 #66000000"),
            MaxWidth = 500,
            MaxHeight = 560,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 20),
            Padding = new Thickness(16, 12),
            Child = rootGrid,
        };
    }

    private Border BuildFloatingShortcutsModal()
    {
        var rootGrid = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,Auto,*"),
            RowSpacing = 8,
        };

        var header = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"),
            ColumnSpacing = 8,
        };
        header.Children.Add(
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    UiBuilder.Icon(
                        MaterialIconKind.KeyboardOutline,
                        16,
                        ThemeService.Brush(ThemeService.TextSecondary)
                    ),
                    new TextBlock
                    {
                        Text = "Keyboard Shortcuts",
                        FontSize = 14,
                        FontWeight = FontWeight.Bold,
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            }
        );

        var escChip = UiBuilder.KbdChip("Esc");
        Grid.SetColumn(escChip, 1);
        header.Children.Add(escChip);

        var closeBtn = UiBuilder.IconButton(MaterialIconKind.Close, _vm.ToggleShortcutsModal);
        ToolTip.SetTip(closeBtn, "Close shortcuts (Esc)");
        Grid.SetColumn(closeBtn, 2);
        header.Children.Add(closeBtn);
        rootGrid.Children.Add(header);

        var divider = UiBuilder.HairlineDivider();
        Grid.SetRow(divider, 1);
        rootGrid.Children.Add(divider);

        (string Category, (string Key, string Desc)[] Items)[] groups =
        [
            (
                "Global & Navigation",
                [
                    ("Q", "Open floating Quick-Add Task modal"),
                    ("/ or Ctrl+K", "Open Quick-Search & Navigation palette"),
                    ("Ctrl+Shift+F", "Open Knowledge Hub Search (#course @pdf @code @quiz @weak)"),
                    ("[ or Ctrl+B", "Toggle left navigation sidebar (or floating drawer on narrow windows)"),
                    ("1 – 7", "Switch views (Inbox, Today, Upcoming, Calendar, Test & Recall, All Active, Completed)"),
                    ("T", "Toggle Dark / Light theme"),
                    ("Esc", "Close active modal, search bar, drawer, or full-screen studio"),
                ]
            ),
            (
                "Tasks & Deck Organizer",
                [
                    ("j / k or ↑ / ↓", "Move selection across tasks or quiz cards"),
                    ("g / Shift+G", "Jump to top / bottom item"),
                    ("Enter or E", "Inspect selected task or open primary file/PDF"),
                    ("Space or D", "Complete / reopen selected task"),
                    ("O", "Open selected assignment in STI eLMS"),
                ]
            ),
            (
                "PDF Reader & Page Studio",
                [
                    ("Ctrl + Scroll", "Smooth in-place zoom in / out"),
                    ("Shift + Scroll", "Horizontal pan when zoomed in"),
                    ("Ctrl+F", "Find & highlight text inside the current PDF"),
                    ("R / Shift+R", "Rotate PDF page 90° clockwise / counter-clockwise"),
                    ("← / → or Home / End", "Previous / next page or jump to first / last page"),
                    ("Ctrl+0", "Toggle Fit-to-Width"),
                ]
            ),
            (
                "Sync & Polling",
                [
                    ("R / Ctrl+S", "Fast poll assignments / Run full sync"),
                    ("Shift+P", "Pause / resume background polling (single-session shield)"),
                ]
            ),
        ];

        var bodyStack = new StackPanel { Spacing = 12, Margin = new Thickness(0, 2, 6, 4) };
        foreach (var (category, items) in groups)
        {
            var groupStack = new StackPanel { Spacing = 4 };
            groupStack.Children.Add(UiBuilder.SectionHeader(category));

            foreach (var (key, desc) in items)
            {
                var row = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
                    ColumnSpacing = 12,
                    Margin = new Thickness(0, 1.5),
                };
                var chip = UiBuilder.KbdChip(key);
                chip.MinWidth = 112;
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
                groupStack.Children.Add(row);
            }

            bodyStack.Children.Add(groupStack);
        }

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility =
                Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = bodyStack,
        };
        Grid.SetRow(scroll, 2);
        rootGrid.Children.Add(scroll);

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderStrong),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            BoxShadow = BoxShadows.Parse("0 16 40 0 #66000000"),
            MaxWidth = 600,
            MaxHeight = 580,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 20),
            Padding = new Thickness(16, 14),
            Child = rootGrid,
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
