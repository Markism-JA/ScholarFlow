using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Material.Icons;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Pdf;
using ScholarFlow.Core.Services;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views.Shell;

/// <summary>
/// Pure-C# builders for the First-Run Onboarding Wizard and Desktop Settings Modal
/// (Pluggable Basin Sync Providers, Per-Basin Account/Config Fields, Tray, OS Notifications, and Startup).
/// </summary>
public static class OnboardingAndSettingsModalBuilder
{
    public static Control BuildOnboardingModal(Window owner, MainWindowViewModel vm)
    {
        var stack = new StackPanel { Spacing = 14 };

        var header = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        header.Children.Add(
            new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Welcome to ScholarFlow",
                        FontSize = 18,
                        FontWeight = FontWeight.Bold,
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    },
                    new TextBlock
                    {
                        Text =
                            "Set up a local Study Basin and choose its sync provider (Local Knowledge Hub or an LMS integration).",
                        FontSize = 12.5,
                        Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            }
        );

        if (!string.IsNullOrWhiteSpace(vm.BasinRoot))
        {
            var closeBtn = UiBuilder.IconButton(
                MaterialIconKind.Close,
                () => vm.CloseTopOverlayOrDrawer(),
                16
            );
            Grid.SetColumn(closeBtn, 1);
            header.Children.Add(closeBtn);
        }
        stack.Children.Add(header);

        // Step 1: Study Basin Folder
        var vaultCard = new StackPanel { Spacing = 8 };
        vaultCard.Children.Add(
            UiBuilder.SectionHeader("1. Basin Folder Location (courses, PDFs, tasks & quizzes)")
        );

        var pathGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 8,
        };
        var pathBox = new TextBox
        {
            Text = vm.OnboardingVaultPath,
            Watermark = "/path/to/Study-Basin",
            FontSize = 12.5,
        };
        pathBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                vm.OnboardingVaultPath = pathBox.Text ?? string.Empty;
        };
        pathGrid.Children.Add(pathBox);

        var browseBtn = UiBuilder.GhostButton(
            "Browse...",
            MaterialIconKind.FolderOpenOutline,
            async () =>
            {
                var picked = await owner.StorageProvider.OpenFolderPickerAsync(
                    new FolderPickerOpenOptions
                    {
                        Title = "Select or Create ScholarFlow Study Basin Folder",
                        AllowMultiple = false,
                    }
                );
                if (picked.Count > 0)
                {
                    var local = picked[0].TryGetLocalPath();
                    if (!string.IsNullOrWhiteSpace(local))
                    {
                        vm.OnboardingVaultPath = local;
                        pathBox.Text = local;
                    }
                }
            }
        );
        Grid.SetColumn(browseBtn, 1);
        pathGrid.Children.Add(browseBtn);
        vaultCard.Children.Add(pathGrid);
        stack.Children.Add(UiBuilder.Card(vaultCard, padding: new Thickness(12, 10)));

        // Step 2: Pluggable Sync Provider Selector & Dynamic Config Fields
        var providerCard = new StackPanel { Spacing = 8 };
        providerCard.Children.Add(UiBuilder.SectionHeader("2. Basin Sync Provider"));

        var providerPills = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var provider in vm.AvailableSyncProviders)
        {
            var captured = provider;
            bool isSelected = string.Equals(
                vm.OnboardingProviderId,
                captured.ProviderId,
                StringComparison.OrdinalIgnoreCase
            );
            var pill = UiBuilder.GhostButton(
                captured.DisplayName,
                captured.Capabilities.SupportsSync
                    ? MaterialIconKind.CloudSyncOutline
                    : MaterialIconKind.BookOpenPageVariantOutline,
                () => vm.SelectOnboardingProvider(captured.ProviderId),
                isActive: isSelected
            );
            pill.Margin = new Thickness(0, 0, 6, 6);
            providerPills.Children.Add(pill);
        }
        providerCard.Children.Add(providerPills);

        var selectedProvider = vm.SyncCoordinator.ResolveProvider(
            new BasinConfig { ProviderId = vm.OnboardingProviderId }
        );

        if (selectedProvider.Capabilities.SupportsSync)
        {
            var acctBox = new TextBox
            {
                Text = vm.OnboardingAccountKey,
                Watermark = "Account Profile Key (e.g. default, school-main)",
                FontSize = 12,
            };
            acctBox.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                    vm.OnboardingAccountKey = acctBox.Text ?? "default";
            };
            providerCard.Children.Add(acctBox);

            foreach (var field in selectedProvider.Capabilities.ConfigFields ?? [])
            {
                var capturedField = field;
                var initialVal = vm.OnboardingProviderSettings.TryGetValue(
                    capturedField.Key,
                    out var existing
                )
                    ? existing
                    : capturedField.DefaultValue;
                var fieldBox = new TextBox
                {
                    Text = initialVal,
                    Watermark = capturedField.Placeholder ?? capturedField.Label,
                    PasswordChar = capturedField.IsSecret ? '•' : '\0',
                    FontSize = 12,
                };
                fieldBox.PropertyChanged += (_, e) =>
                {
                    if (e.Property == TextBox.TextProperty)
                        vm.OnboardingProviderSettings[capturedField.Key] = fieldBox.Text
                            ?? string.Empty;
                };
                providerCard.Children.Add(fieldBox);
            }
        }
        else
        {
            providerCard.Children.Add(
                new TextBlock
                {
                    Text =
                        "Local Knowledge Hub basins store subjects, books, PDFs, notes, and recall decks purely offline with zero background LMS polling.",
                    FontSize = 12,
                    Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                    TextWrapping = TextWrapping.Wrap,
                }
            );
        }

        stack.Children.Add(UiBuilder.Card(providerCard, padding: new Thickness(12, 10)));

        // Action Buttons
        var actions = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"),
            ColumnSpacing = 8,
        };
        var offlineBtn = UiBuilder.GhostButton(
            "Create as Local Basin",
            MaterialIconKind.FolderCheckOutline,
            async () =>
                await vm.CompleteOnboardingAsync(
                    vm.OnboardingVaultPath,
                    runElmsAuth: false,
                    runInitialSync: false
                )
        );
        Grid.SetColumn(offlineBtn, 1);
        actions.Children.Add(offlineBtn);

        if (selectedProvider.Capabilities.SupportsSync)
        {
            var connectBtn = UiBuilder.PrimaryButton(
                $"Connect {selectedProvider.DisplayName} & Sync",
                MaterialIconKind.CloudSyncOutline,
                async () =>
                    await vm.CompleteOnboardingAsync(
                        vm.OnboardingVaultPath,
                        runElmsAuth: true,
                        runInitialSync: true
                    )
            );
            Grid.SetColumn(connectBtn, 2);
            actions.Children.Add(connectBtn);
        }

        stack.Children.Add(actions);

        return new Border
        {
            Width = 580,
            Background = ThemeService.Brush(ThemeService.BgElevated),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20, 18),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = stack,
        };
    }

    public static Control BuildSettingsModal(MainWindowViewModel vm)
    {
        var stack = new StackPanel { Spacing = 10 };

        var header = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        header.Children.Add(
            new TextBlock
            {
                Text = "Basin Sync Provider & Desktop Settings",
                FontSize = 16,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        var closeBtn = UiBuilder.IconButton(MaterialIconKind.Close, vm.ToggleSettingsModal, 16);
        Grid.SetColumn(closeBtn, 1);
        header.Children.Add(closeBtn);
        stack.Children.Add(header);
        stack.Children.Add(UiBuilder.HairlineDivider());

        // 1. Active Basin & Pluggable Sync Provider Configuration Card
        var basinCard = new StackPanel { Spacing = 8 };
        basinCard.Children.Add(UiBuilder.SectionHeader($"Active Basin: {vm.BasinConfig.Name}"));

        var providerRow = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var provider in vm.AvailableSyncProviders)
        {
            var captured = provider;
            bool isCurrent = string.Equals(
                vm.ActiveSyncProvider.ProviderId,
                captured.ProviderId,
                StringComparison.OrdinalIgnoreCase
            );
            var btn = UiBuilder.GhostButton(
                captured.DisplayName,
                captured.Capabilities.SupportsSync
                    ? MaterialIconKind.CloudSyncOutline
                    : MaterialIconKind.BookOpenPageVariantOutline,
                async () => await vm.SetBasinProviderAsync(captured.ProviderId),
                isActive: isCurrent
            );
            btn.Margin = new Thickness(0, 0, 6, 4);
            providerRow.Children.Add(btn);
        }
        basinCard.Children.Add(providerRow);

        if (vm.SupportsActiveBasinSync)
        {
            // Dynamic Config Fields + Account Key
            var fieldsGrid = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("160,*"),
                ColumnSpacing = 8,
            };

            var acctBox = new TextBox
            {
                Text = vm.BasinConfig.AccountKey,
                Watermark = "Account Key (default)",
                FontSize = 12,
            };
            acctBox.LostFocus += async (_, _) =>
                await vm.SetBasinAccountKeyAsync(acctBox.Text ?? "default");
            fieldsGrid.Children.Add(acctBox);

            var configFields = vm.ActiveCapabilities.ConfigFields ?? [];
            if (configFields.Count > 0)
            {
                var firstField = configFields[0];
                var val = vm.BasinConfig.GetProviderSetting(
                    firstField.Key,
                    firstField.DefaultValue
                );
                var fieldBox = new TextBox
                {
                    Text = val,
                    Watermark = firstField.Placeholder ?? firstField.Label,
                    PasswordChar = firstField.IsSecret ? '•' : '\0',
                    FontSize = 12,
                };
                fieldBox.LostFocus += async (_, _) =>
                    await vm.SetBasinProviderSettingAsync(
                        firstField.Key,
                        fieldBox.Text ?? firstField.DefaultValue
                    );
                Grid.SetColumn(fieldBox, 1);
                fieldsGrid.Children.Add(fieldBox);
            }
            basinCard.Children.Add(fieldsGrid);

            foreach (var extraField in configFields.Skip(1))
            {
                var capturedField = extraField;
                var val = vm.BasinConfig.GetProviderSetting(
                    capturedField.Key,
                    capturedField.DefaultValue
                );
                var extraBox = new TextBox
                {
                    Text = val,
                    Watermark = capturedField.Placeholder ?? capturedField.Label,
                    PasswordChar = capturedField.IsSecret ? '•' : '\0',
                    FontSize = 12,
                };
                extraBox.LostFocus += async (_, _) =>
                    await vm.SetBasinProviderSettingAsync(
                        capturedField.Key,
                        extraBox.Text ?? capturedField.DefaultValue
                    );
                basinCard.Children.Add(extraBox);
            }

            // Per-Basin Background Polling & Auth Action Strip
            var providerActions = new WrapPanel { Orientation = Orientation.Horizontal };
            var bgSyncBtn = UiBuilder.GhostButton(
                vm.BasinConfig.BackgroundSyncEnabled
                    ? "Background Polling: ON"
                    : "Background Polling: OFF",
                vm.BasinConfig.BackgroundSyncEnabled
                    ? MaterialIconKind.CheckCircleOutline
                    : MaterialIconKind.PauseCircleOutline,
                async () => await vm.ToggleBasinBackgroundSyncAsync(),
                isActive: vm.BasinConfig.BackgroundSyncEnabled
            );
            bgSyncBtn.Margin = new Thickness(0, 2, 6, 2);
            providerActions.Children.Add(bgSyncBtn);

            var authBtn = UiBuilder.GhostButton(
                "Connect / Verify Session",
                MaterialIconKind.AccountCheckOutline,
                async () => await vm.AuthenticateActiveProviderAsync()
            );
            authBtn.Margin = new Thickness(0, 2, 6, 2);
            providerActions.Children.Add(authBtn);

            var signOutBtn = UiBuilder.GhostButton(
                "Sign Out",
                MaterialIconKind.LogoutVariant,
                async () => await vm.DisconnectActiveProviderAsync()
            );
            signOutBtn.Margin = new Thickness(0, 2, 0, 2);
            providerActions.Children.Add(signOutBtn);

            basinCard.Children.Add(providerActions);

            // Excluded Courses Quick-Restore Chips
            var excludedCourses = vm
                .Courses.Where(vm.IsCourseSyncExcluded)
                .OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (excludedCourses.Count > 0)
            {
                var excludedWrap = new WrapPanel { Orientation = Orientation.Horizontal };
                foreach (var course in excludedCourses)
                {
                    var capturedCourse = course;
                    var chip = UiBuilder.GhostButton(
                        $"Excluded: {capturedCourse.Title} (Click to Include)",
                        MaterialIconKind.EyeOffOutline,
                        async () => await vm.ToggleCourseSyncExclusionAsync(capturedCourse)
                    );
                    chip.Margin = new Thickness(0, 2, 6, 2);
                    excludedWrap.Children.Add(chip);
                }
                basinCard.Children.Add(excludedWrap);
            }
        }

        // Academic Terms / Study Sections Editor (available for both Manual/Local and LMS basins)
        basinCard.Children.Add(UiBuilder.HairlineDivider(new Thickness(0, 4)));
        var termsHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        termsHeader.Children.Add(
            UiBuilder.SectionHeader("Academic Terms / Study Sections (Sidebar & Master PDF)")
        );
        var resetTermsBtn = UiBuilder.GhostButton(
            "Reset to Defaults",
            MaterialIconKind.Restore,
            async () => await vm.ResetBasinTermsToProviderDefaultAsync()
        );
        Grid.SetColumn(resetTermsBtn, 1);
        termsHeader.Children.Add(resetTermsBtn);
        basinCard.Children.Add(termsHeader);

        if (vm.ActiveTerms.Count > 0)
        {
            var termsWrap = new WrapPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < vm.ActiveTerms.Count; i++)
            {
                var capturedTerm = vm.ActiveTerms[i];
                var chipRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
                chipRow.Children.Add(
                    new TextBlock
                    {
                        Text = capturedTerm,
                        FontSize = 11.5,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(4, 0, 4, 0),
                    }
                );
                if (i > 0)
                {
                    chipRow.Children.Add(
                        UiBuilder.IconButton(
                            MaterialIconKind.ChevronLeft,
                            async () => await vm.MoveBasinTermAsync(capturedTerm, -1),
                            12
                        )
                    );
                }
                if (i < vm.ActiveTerms.Count - 1)
                {
                    chipRow.Children.Add(
                        UiBuilder.IconButton(
                            MaterialIconKind.ChevronRight,
                            async () => await vm.MoveBasinTermAsync(capturedTerm, +1),
                            12
                        )
                    );
                }
                chipRow.Children.Add(
                    UiBuilder.IconButton(
                        MaterialIconKind.Close,
                        async () => await vm.RemoveBasinTermAsync(capturedTerm),
                        12
                    )
                );

                termsWrap.Children.Add(
                    new Border
                    {
                        Background = ThemeService.Brush(ThemeService.BgSurface),
                        BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(4, 2),
                        Margin = new Thickness(0, 0, 6, 4),
                        Child = chipRow,
                    }
                );
            }
            basinCard.Children.Add(termsWrap);
        }
        else
        {
            basinCard.Children.Add(
                new TextBlock
                {
                    Text =
                        "No terms configured — tasks and Master PDFs use flat subject/module organization.",
                    FontSize = 11.5,
                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                }
            );
        }

        var addTermGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 6,
        };
        var newTermBox = new TextBox
        {
            Watermark = "Add term or study section (e.g. Prelims, Unit 1, Part I, Exam Prep)...",
            FontSize = 12,
        };
        addTermGrid.Children.Add(newTermBox);
        var addTermBtn = UiBuilder.GhostButton("Add Term", MaterialIconKind.Plus, async () =>
        {
            if (!string.IsNullOrWhiteSpace(newTermBox.Text))
            {
                var txt = newTermBox.Text;
                newTermBox.Text = string.Empty;
                await vm.AddBasinTermAsync(txt);
            }
        });
        Grid.SetColumn(addTermBtn, 1);
        addTermGrid.Children.Add(addTermBtn);
        basinCard.Children.Add(addTermGrid);

        stack.Children.Add(UiBuilder.Card(basinCard, padding: new Thickness(12, 10)));

        Control BuildToggleRow(string title, string subtitle, bool isEnabled, Func<Task> onToggle)
        {
            var row = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                ColumnSpacing = 12,
            };
            row.Children.Add(
                new StackPanel
                {
                    Spacing = 2,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = title,
                            FontSize = 13,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                        },
                        new TextBlock
                        {
                            Text = subtitle,
                            FontSize = 11.5,
                            Foreground = ThemeService.Brush(ThemeService.TextMuted),
                            TextWrapping = TextWrapping.Wrap,
                        },
                    },
                }
            );

            var btn = UiBuilder.GhostButton(
                isEnabled ? "Enabled" : "Disabled",
                isEnabled ? MaterialIconKind.CheckCircleOutline : MaterialIconKind.CircleOutline,
                async () => await onToggle(),
                isActive: isEnabled
            );
            Grid.SetColumn(btn, 1);
            row.Children.Add(btn);
            return UiBuilder.Card(row, padding: new Thickness(12, 9));
        }

        stack.Children.Add(
            BuildToggleRow(
                "Minimize to System Tray on Close",
                "Keep ScholarFlow running in the background when closing the window so multi-basin polling and reminders stay active.",
                vm.Settings.MinimizeToTrayOnClose,
                vm.ToggleMinimizeToTrayAsync
            )
        );

        stack.Children.Add(
            BuildToggleRow(
                "OS Notification Tray Alerts",
                "Dispatch native Linux (notify-send) or Windows Toast notifications for new assignments and deadlines across all synced basins.",
                vm.Settings.EnableDesktopNotifications,
                vm.ToggleDesktopNotificationsAsync
            )
        );

        stack.Children.Add(
            BuildToggleRow(
                "Launch on System Startup (--minimized)",
                "Automatically start ScholarFlow minimized in the system tray when signing into Windows or Linux.",
                vm.Settings.LaunchOnStartup,
                vm.ToggleLaunchOnStartupAsync
            )
        );

        // Study Basin & Setup Wizard Footer
        var vaultRow = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 10,
        };
        vaultRow.Children.Add(
            new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Active Basin Path",
                        FontSize = 12.5,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    },
                    new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(vm.BasinRoot)
                            ? "Not configured"
                            : vm.BasinRoot,
                        FontSize = 11.5,
                        Foreground = ThemeService.Brush(ThemeService.TextMuted),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                },
            }
        );
        var wizardBtn = UiBuilder.GhostButton(
            "New Basin Wizard",
            MaterialIconKind.WizardHat,
            vm.OpenOnboardingWizard
        );
        Grid.SetColumn(wizardBtn, 1);
        vaultRow.Children.Add(wizardBtn);
        stack.Children.Add(UiBuilder.Card(vaultRow, padding: new Thickness(12, 9)));

        return new Border
        {
            Width = 560,
            Background = ThemeService.Brush(ThemeService.BgElevated),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20, 18),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = stack,
        };
    }

    public static Control BuildAddCourseModal(MainWindowViewModel vm)
    {
        var stack = new StackPanel { Spacing = 12 };

        // 1. Unified Floating Modal Header
        var header = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,Auto"),
            ColumnSpacing = 8,
        };
        var headerIcon = UiBuilder.Icon(
            MaterialIconKind.FolderPlusOutline,
            18,
            ThemeService.Brush(ThemeService.AccentPrimary)
        );
        headerIcon.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(headerIcon);

        var titleStack = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = "Add or Import Course",
                    FontSize = 14.5,
                    FontWeight = FontWeight.Bold,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                },
                new TextBlock
                {
                    Text =
                        "Create a new subject folder in this Basin or import an existing folder.",
                    FontSize = 11.5,
                    Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                },
            },
        };
        Grid.SetColumn(titleStack, 1);
        header.Children.Add(titleStack);

        var escChip = UiBuilder.KbdChip("Esc");
        escChip.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(escChip, 2);
        header.Children.Add(escChip);

        var closeBtn = UiBuilder.IconButton(MaterialIconKind.Close, vm.ToggleAddCourseModal, 16);
        closeBtn.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(closeBtn, 3);
        header.Children.Add(closeBtn);
        stack.Children.Add(header);
        stack.Children.Add(UiBuilder.HairlineDivider());

        // 2. Mode Switcher (Create New vs Import Existing Folder)
        bool isImportMode = false;
        string? selectedImportFolder = null;
        var selectedImportMode = FileImportMode.Copy;

        var titleBox = new TextBox
        {
            Watermark = "Course Title (e.g. Linear Algebra, Organic Chemistry, Ethics)",
            FontSize = 13,
        };
        var aliasBox = new TextBox
        {
            Watermark = "Short #alias (optional, e.g. math)",
            FontSize = 12.5,
        };
        var codeBox = new TextBox
        {
            Watermark = "Course Code (optional, e.g. MATH101)",
            FontSize = 12.5,
        };

        var folderPathText = new TextBlock
        {
            Text = "No folder selected yet.",
            FontSize = 11.5,
            Foreground = ThemeService.Brush(ThemeService.TextMuted),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var folderHintText = new TextBlock
        {
            Text =
                "Folders already inside this Basin are linked in-place; external folders are copied into the Basin.",
            FontSize = 10.5,
            Foreground = ThemeService.Brush(ThemeService.TextMuted),
            TextWrapping = TextWrapping.Wrap,
        };

        var importCard = new Border
        {
            IsVisible = false,
            Background = ThemeService.Brush(ThemeService.BgElevated),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
        };
        var importInner = new StackPanel { Spacing = 8 };
        var pickRow = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
            ColumnSpacing = 10,
        };
        var chooseFolderBtn = UiBuilder.GhostButton(
            "Choose Folder...",
            MaterialIconKind.FolderOpenOutline,
            async () =>
            {
                var top = TopLevel.GetTopLevel(importCard);
                if (top?.StorageProvider is null)
                    return;

                var startFolder =
                    !string.IsNullOrWhiteSpace(vm.BasinRoot) && Directory.Exists(vm.BasinRoot)
                        ? await top.StorageProvider.TryGetFolderFromPathAsync(new Uri(vm.BasinRoot))
                        : null;

                var picked = await top.StorageProvider.OpenFolderPickerAsync(
                    new FolderPickerOpenOptions
                    {
                        Title = "Select Course Folder to Import",
                        AllowMultiple = false,
                        SuggestedStartLocation = startFolder,
                    }
                );
                var localPath = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
                if (string.IsNullOrWhiteSpace(localPath))
                    return;

                selectedImportFolder = localPath;
                folderPathText.Text = localPath;
                folderPathText.Foreground = ThemeService.Brush(ThemeService.TextPrimary);

                if (string.IsNullOrWhiteSpace(titleBox.Text))
                {
                    var inferred = Path.GetFileName(
                            localPath.TrimEnd(
                                Path.DirectorySeparatorChar,
                                Path.AltDirectorySeparatorChar
                            )
                        )
                        .Replace('_', ' ');
                    titleBox.Text = inferred;
                }

                var parentDir = Path.GetDirectoryName(
                    localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                );
                bool inBasin =
                    parentDir is not null
                    && !string.IsNullOrWhiteSpace(vm.BasinRoot)
                    && string.Equals(
                        Path.GetFullPath(parentDir)
                            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        Path.GetFullPath(vm.BasinRoot)
                            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase
                    );
                folderHintText.Text = inBasin
                    ? "Detected inside this Basin — will register in-place without copying files."
                    : "External folder — will copy contents into a new course folder in this Basin.";
            }
        );
        pickRow.Children.Add(chooseFolderBtn);
        Grid.SetColumn(folderPathText, 1);
        pickRow.Children.Add(folderPathText);
        importInner.Children.Add(pickRow);

        var importModeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        importModeRow.Children.Add(
            new TextBlock
            {
                Text = "External folder mode:",
                FontSize = 11,
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        var importModeButtons = new List<(FileImportMode Mode, Button Btn)>();
        void RefreshImportModeButtons()
        {
            foreach (var (m, b) in importModeButtons)
            {
                bool active = m == selectedImportMode;
                b.Background = active
                    ? ThemeService.Brush(ThemeService.AccentSoftBg)
                    : Brushes.Transparent;
                b.BorderBrush = active
                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : ThemeService.Brush(ThemeService.BorderSubtle);
            }
        }
        foreach (
            var (m, lbl, ic) in new[]
            {
                (FileImportMode.Copy, "Copy", MaterialIconKind.ContentCopy),
                (FileImportMode.Move, "Move", MaterialIconKind.FileMoveOutline),
                (FileImportMode.Symlink, "Symlink", MaterialIconKind.LinkVariant),
            }
        )
        {
            var capturedM = m;
            var mb = UiBuilder.GhostButton(
                lbl,
                ic,
                () =>
                {
                    selectedImportMode = capturedM;
                    RefreshImportModeButtons();
                },
                isActive: capturedM == selectedImportMode
            );
            importModeButtons.Add((capturedM, mb));
            importModeRow.Children.Add(mb);
        }
        importInner.Children.Add(importModeRow);
        importInner.Children.Add(folderHintText);
        importCard.Child = importInner;

        var modeStrip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        Button? createModeBtn = null;
        Button? importModeBtn = null;
        Button? submitBtn = null;

        void UpdateModeUi()
        {
            importCard.IsVisible = isImportMode;
            if (createModeBtn is not null)
            {
                createModeBtn.Background = !isImportMode
                    ? ThemeService.Brush(ThemeService.AccentSoftBg)
                    : Brushes.Transparent;
                createModeBtn.BorderBrush = !isImportMode
                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : ThemeService.Brush(ThemeService.BorderSubtle);
            }
            if (importModeBtn is not null)
            {
                importModeBtn.Background = isImportMode
                    ? ThemeService.Brush(ThemeService.AccentSoftBg)
                    : Brushes.Transparent;
                importModeBtn.BorderBrush = isImportMode
                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : ThemeService.Brush(ThemeService.BorderSubtle);
            }
            if (submitBtn?.Content is StackPanel sp && sp.Children.LastOrDefault() is TextBlock tb)
            {
                tb.Text = isImportMode ? "Import Course" : "Create Course";
            }
        }

        createModeBtn = UiBuilder.GhostButton(
            "Create New",
            MaterialIconKind.Plus,
            () =>
            {
                isImportMode = false;
                UpdateModeUi();
            },
            isActive: true
        );
        importModeBtn = UiBuilder.GhostButton(
            "Import Folder",
            MaterialIconKind.FolderDownloadOutline,
            () =>
            {
                isImportMode = true;
                UpdateModeUi();
            },
            isActive: false
        );
        modeStrip.Children.Add(createModeBtn);
        modeStrip.Children.Add(importModeBtn);
        stack.Children.Add(modeStrip);
        stack.Children.Add(importCard);

        async Task SubmitAsync()
        {
            if (isImportMode)
            {
                if (!string.IsNullOrWhiteSpace(selectedImportFolder))
                    await vm.ImportCourseFolderAsync(
                        selectedImportFolder,
                        titleBox.Text,
                        aliasBox.Text,
                        codeBox.Text,
                        selectedImportMode
                    );
            }
            else if (!string.IsNullOrWhiteSpace(titleBox.Text))
            {
                await vm.CreateLocalCourseAsync(titleBox.Text, aliasBox.Text, codeBox.Text);
            }
        }

        titleBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await SubmitAsync();
            }
        };
        stack.Children.Add(titleBox);

        var row2 = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,*"),
            ColumnSpacing = 8,
        };
        row2.Children.Add(aliasBox);
        Grid.SetColumn(codeBox, 1);
        row2.Children.Add(codeBox);
        stack.Children.Add(row2);

        stack.Children.Add(UiBuilder.HairlineDivider());

        var actions = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"),
            ColumnSpacing = 8,
        };
        var cancelBtn = UiBuilder.GhostButton("Cancel", null, vm.ToggleAddCourseModal);
        Grid.SetColumn(cancelBtn, 1);
        actions.Children.Add(cancelBtn);

        submitBtn = UiBuilder.PrimaryButton("Create Course", MaterialIconKind.Check, async () =>
            await SubmitAsync()
        );
        Grid.SetColumn(submitBtn, 2);
        actions.Children.Add(submitBtn);
        stack.Children.Add(actions);

        return new Border
        {
            MinWidth = 420,
            MaxWidth = 540,
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderStrong),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            BoxShadow = BoxShadows.Parse("0 16 40 0 #66000000"),
            Padding = new Thickness(18, 16),
            Margin = new Thickness(24, 32),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Child = stack,
        };
    }

    public static Control BuildPacketOrganizerModal(MainWindowViewModel vm)
    {
        var course = vm.PacketOrganizerCourse;
        var plan = vm.ActivePacketPlan;
        if (course is null || plan is null)
            return new Border();

        int totalItems = plan.Sections.Sum(s => s.Items.Count);
        int includedItems = plan.Sections.Sum(s => s.Items.Count(i => !i.IsExcluded));

        var root = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,Auto,Auto,*,Auto,Auto"),
            RowSpacing = 10,
        };

        // 1. Unified Floating Modal Header (matches QuickAdd / Search / Fix with AI)
        var header = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,Auto"),
            ColumnSpacing = 8,
        };
        var headerIcon = UiBuilder.Icon(
            MaterialIconKind.FileDocumentEditOutline,
            18,
            ThemeService.Brush(ThemeService.AccentPrimary)
        );
        headerIcon.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(headerIcon);

        var allItems = plan.Sections.SelectMany(s => s.Items).ToList();
        int pdfCount = allItems.Count(i => i.IsPdf);
        int imgCount = allItems.Count(i =>
            i.ResolvedKind == ScholarFlow.Core.Knowledge.StudySourceKind.Image
        );
        int noteCodeCount = allItems.Count(i =>
            i.ResolvedKind
                is ScholarFlow.Core.Knowledge.StudySourceKind.Markdown
                    or ScholarFlow.Core.Knowledge.StudySourceKind.PlainText
                    or ScholarFlow.Core.Knowledge.StudySourceKind.Code
        );
        int nonPdfCount = allItems.Count - pdfCount;
        int includedNonPdf = allItems.Count(i => !i.IsPdf && !i.IsExcluded);

        var titleStack = new StackPanel
        {
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Organize Study Packet",
                            FontSize = 14.5,
                            FontWeight = FontWeight.Bold,
                            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 8, 2),
                        },
                        new Border
                        {
                            Margin = new Thickness(0, 0, 6, 2),
                            Child = UiBuilder.Pill(
                                course.Title,
                                ThemeService.Brush(ThemeService.AccentPrimary),
                                ThemeService.Brush(ThemeService.AccentSoftBg),
                                fontSize: 10.5
                            ),
                        },
                        new Border
                        {
                            Margin = new Thickness(0, 0, 0, 2),
                            Child = UiBuilder.Pill(
                                course.IsSyncableCourse
                                    ? "eLMS Auto-Syllabus · Non-PDFs Opt-in"
                                    : "Local Hub · Multi-Format Packet",
                                ThemeService.Brush(ThemeService.TextSecondary),
                                ThemeService.Brush(ThemeService.BgElevated),
                                course.IsSyncableCourse
                                    ? MaterialIconKind.CloudSyncOutline
                                    : MaterialIconKind.LayersOutline,
                                fontSize: 10
                            ),
                        },
                    },
                },
                new TextBlock
                {
                    Text =
                        $"{includedItems} of {totalItems} item{(totalItems == 1 ? "" : "s")} included ({pdfCount} PDF, {imgCount} img, {noteCodeCount} note/code) · {plan.Sections.Count} section{(plan.Sections.Count == 1 ? "" : "s")}",
                    FontSize = 11.5,
                    Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };
        Grid.SetColumn(titleStack, 1);
        header.Children.Add(titleStack);

        var escChip = UiBuilder.KbdChip("Esc");
        escChip.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(escChip, 2);
        header.Children.Add(escChip);

        var closeBtn = UiBuilder.IconButton(
            MaterialIconKind.Close,
            vm.ClosePacketOrganizerModal,
            16
        );
        closeBtn.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(closeBtn, 3);
        header.Children.Add(closeBtn);
        root.Children.Add(header);

        var topDivider = UiBuilder.HairlineDivider();
        Grid.SetRow(topDivider, 1);
        root.Children.Add(topDivider);

        // 2. Controls Bar: Format Filters + Packet Options + Add Section Strip
        var controlsStack = new StackPanel { Spacing = 8 };

        var filterAndOptionsWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        void AddFilterChip(string key, string label, MaterialIconKind? icon = null)
        {
            var btn = UiBuilder.GhostButton(
                label,
                icon,
                () => vm.SetPacketOrganizerFilter(key),
                isActive: string.Equals(
                    vm.PacketOrganizerFilter,
                    key,
                    StringComparison.OrdinalIgnoreCase
                )
            );
            btn.Margin = new Thickness(0, 0, 5, 4);
            filterAndOptionsWrap.Children.Add(btn);
        }

        AddFilterChip("All", $"All ({totalItems})", MaterialIconKind.LayersOutline);
        AddFilterChip("PDFs", $"PDFs ({pdfCount})", MaterialIconKind.FilePdfBox);
        if (imgCount > 0)
            AddFilterChip("Images", $"Images ({imgCount})", MaterialIconKind.FileImageOutline);
        if (noteCodeCount > 0)
            AddFilterChip(
                "Notes & Code",
                $"Notes & Code ({noteCodeCount})",
                MaterialIconKind.CodeBraces
            );

        if (nonPdfCount > 0)
        {
            bool anyNonPdfIncluded = includedNonPdf > 0;
            var bulkNonPdfBtn = UiBuilder.GhostButton(
                anyNonPdfIncluded ? "Exclude All Non-PDFs" : "Include All Non-PDFs",
                anyNonPdfIncluded ? MaterialIconKind.EyeOffOutline : MaterialIconKind.CheckAll,
                () => vm.SetAllNonPdfPacketItemsExcluded(anyNonPdfIncluded)
            );
            bulkNonPdfBtn.Margin = new Thickness(4, 0, 5, 4);
            filterAndOptionsWrap.Children.Add(bulkNonPdfBtn);
        }

        var coverToggle = UiBuilder.GhostButton(
            "Cover",
            plan.IncludeCoverPage ? MaterialIconKind.Check : null,
            vm.TogglePacketIncludeCoverPage,
            isActive: plan.IncludeCoverPage
        );
        coverToggle.Margin = new Thickness(4, 0, 4, 4);
        filterAndOptionsWrap.Children.Add(coverToggle);

        var tocToggle = UiBuilder.GhostButton(
            "TOC",
            plan.IncludeTableOfContents ? MaterialIconKind.Check : null,
            vm.TogglePacketIncludeToc,
            isActive: plan.IncludeTableOfContents
        );
        tocToggle.Margin = new Thickness(0, 0, 0, 4);
        filterAndOptionsWrap.Children.Add(tocToggle);

        controlsStack.Children.Add(filterAndOptionsWrap);

        var addSectionGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 8,
        };
        var newSecBox = new TextBox
        {
            Watermark = "Add section or term (e.g. Prelims, Chapters 1-3, Diagrams, Code Labs)...",
            FontSize = 12,
        };
        void SubmitNewSection()
        {
            if (!string.IsNullOrWhiteSpace(newSecBox.Text))
                vm.AddPacketSection(newSecBox.Text);
        }
        newSecBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                SubmitNewSection();
            }
        };
        addSectionGrid.Children.Add(newSecBox);

        var addSecBtn = UiBuilder.GhostButton(
            "Add Section",
            MaterialIconKind.FolderPlusOutline,
            SubmitNewSection
        );
        Grid.SetColumn(addSecBtn, 1);
        addSectionGrid.Children.Add(addSecBtn);
        controlsStack.Children.Add(addSectionGrid);

        Grid.SetRow(controlsStack, 2);
        root.Children.Add(controlsStack);

        bool MatchesFilter(PacketItemPlan item) =>
            vm.PacketOrganizerFilter switch
            {
                "PDFs" => item.IsPdf,
                "Images" => item.ResolvedKind == ScholarFlow.Core.Knowledge.StudySourceKind.Image,
                "Notes & Code" => item.ResolvedKind
                    is ScholarFlow.Core.Knowledge.StudySourceKind.Markdown
                        or ScholarFlow.Core.Knowledge.StudySourceKind.PlainText
                        or ScholarFlow.Core.Knowledge.StudySourceKind.Code,
                _ => true,
            };

        // 3. Scrollable Sections & Items List (scroll-persistent, responsive)
        var sectionsStack = new StackPanel { Spacing = 10 };
        for (int sIdx = 0; sIdx < plan.Sections.Count; sIdx++)
        {
            var capturedSec = plan.Sections[sIdx];
            var visibleSecItems = capturedSec.Items.Where(MatchesFilter).ToList();
            var secStack = new StackPanel { Spacing = 6 };

            // Section Header Row
            var secHeader = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,Auto,Auto,Auto"),
                ColumnSpacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var secIcon = UiBuilder.Icon(
                MaterialIconKind.FolderOutline,
                15,
                ThemeService.Brush(ThemeService.AccentPrimary)
            );
            secIcon.VerticalAlignment = VerticalAlignment.Center;
            secHeader.Children.Add(secIcon);

            var secTitleBox = new TextBox
            {
                Text = capturedSec.Title,
                Watermark = "Section Title",
                FontSize = 12.5,
                FontWeight = FontWeight.SemiBold,
            };
            secTitleBox.LostFocus += (_, _) =>
                vm.RenamePacketSection(capturedSec, secTitleBox.Text ?? capturedSec.Title);
            Grid.SetColumn(secTitleBox, 1);
            secHeader.Children.Add(secTitleBox);

            int secIncluded = capturedSec.Items.Count(i => !i.IsExcluded);
            var countPill = UiBuilder.Pill(
                $"{secIncluded}/{capturedSec.Items.Count}",
                ThemeService.Brush(ThemeService.TextSecondary),
                ThemeService.Brush(ThemeService.BgSurface),
                fontSize: 10
            );
            countPill.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(countPill, 2);
            secHeader.Children.Add(countPill);

            var upSecBtn = UiBuilder.IconButton(
                MaterialIconKind.ArrowUp,
                () => vm.MovePacketSection(capturedSec, -1),
                14
            );
            upSecBtn.IsEnabled = sIdx > 0;
            Grid.SetColumn(upSecBtn, 3);
            secHeader.Children.Add(upSecBtn);

            var downSecBtn = UiBuilder.IconButton(
                MaterialIconKind.ArrowDown,
                () => vm.MovePacketSection(capturedSec, +1),
                14
            );
            downSecBtn.IsEnabled = sIdx < plan.Sections.Count - 1;
            Grid.SetColumn(downSecBtn, 4);
            secHeader.Children.Add(downSecBtn);

            var delSecBtn = UiBuilder.IconButton(
                MaterialIconKind.DeleteOutline,
                () => vm.RemovePacketSection(capturedSec),
                14
            );
            delSecBtn.IsEnabled = plan.Sections.Count > 1;
            Grid.SetColumn(delSecBtn, 5);
            secHeader.Children.Add(delSecBtn);

            secStack.Children.Add(secHeader);

            if (visibleSecItems.Count == 0)
            {
                secStack.Children.Add(
                    new TextBlock
                    {
                        Text =
                            capturedSec.Items.Count == 0
                                ? "No study files in this section."
                                : $"No items match the '{vm.PacketOrganizerFilter}' filter in this section ({capturedSec.Items.Count} hidden).",
                        FontSize = 11.5,
                        Foreground = ThemeService.Brush(ThemeService.TextMuted),
                        Margin = new Thickness(4, 2),
                    }
                );
            }
            else
            {
                for (int iIdx = 0; iIdx < visibleSecItems.Count; iIdx++)
                {
                    var capturedItem = visibleSecItems[iIdx];
                    int realIdx = capturedSec.Items.IndexOf(capturedItem);
                    var (kindBadge, kindFg, kindBg) = capturedItem.ResolvedKind switch
                    {
                        ScholarFlow.Core.Knowledge.StudySourceKind.Pdf => (
                            "PDF",
                            ThemeService.Brush(ThemeService.PriorityP1Fg),
                            ThemeService.Brush(ThemeService.PriorityP1Bg)
                        ),
                        ScholarFlow.Core.Knowledge.StudySourceKind.Image => (
                            "IMG",
                            ThemeService.Brush(ThemeService.PriorityP2Fg),
                            ThemeService.Brush(ThemeService.PriorityP2Bg)
                        ),
                        ScholarFlow.Core.Knowledge.StudySourceKind.Code => (
                            string.IsNullOrWhiteSpace(capturedItem.ResolvedLanguage)
                                ? "CODE"
                                : capturedItem.ResolvedLanguage.ToUpperInvariant(),
                            ThemeService.Brush(ThemeService.AccentPrimary),
                            ThemeService.Brush(ThemeService.AccentSoftBg)
                        ),
                        ScholarFlow.Core.Knowledge.StudySourceKind.Markdown => (
                            "MD",
                            ThemeService.Brush(ThemeService.PriorityP3Fg),
                            ThemeService.Brush(ThemeService.PriorityP3Bg)
                        ),
                        _ => (
                            "TXT",
                            ThemeService.Brush(ThemeService.TextSecondary),
                            ThemeService.Brush(ThemeService.BgSurface)
                        ),
                    };

                    var itemOuter = new Grid
                    {
                        ColumnDefinitions = ColumnDefinitions.Parse("28,*,Auto"),
                        ColumnSpacing = 6,
                        VerticalAlignment = VerticalAlignment.Center,
                    };

                    var includeBtn = UiBuilder.IconButton(
                        capturedItem.IsExcluded
                            ? MaterialIconKind.EyeOffOutline
                            : MaterialIconKind.CheckCircleOutline,
                        () => vm.TogglePacketItemExcluded(capturedItem),
                        15,
                        capturedItem.IsExcluded
                            ? ThemeService.Brush(ThemeService.TextMuted)
                            : ThemeService.Brush(ThemeService.SuccessFg)
                    );
                    includeBtn.VerticalAlignment = VerticalAlignment.Top;
                    includeBtn.Margin = new Thickness(0, 4, 0, 0);
                    itemOuter.Children.Add(includeBtn);

                    var titleBox = new TextBox
                    {
                        Text = capturedItem.DisplayTitle,
                        Watermark = capturedItem.RelativePath,
                        FontSize = 12,
                        Opacity = capturedItem.IsExcluded ? 0.55 : 1.0,
                    };
                    titleBox.LostFocus += (_, _) =>
                        vm.SetPacketItemDisplayTitle(capturedItem, titleBox.Text);

                    var subMetaRow = new WrapPanel
                    {
                        Orientation = Orientation.Horizontal,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    var badgeControl = UiBuilder.Pill(kindBadge, kindFg, kindBg, fontSize: 9.5);
                    badgeControl.Margin = new Thickness(0, 2, 6, 0);
                    subMetaRow.Children.Add(badgeControl);

                    subMetaRow.Children.Add(
                        new TextBlock
                        {
                            Text = capturedItem.IsExcluded
                                ? $"Excluded · {capturedItem.RelativePath}"
                                : capturedItem.RelativePath,
                            FontSize = 10.5,
                            Foreground = ThemeService.Brush(ThemeService.TextMuted),
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            MaxLines = 1,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 2, 8, 0),
                        }
                    );

                    var titleCol = new StackPanel
                    {
                        Spacing = 2,
                        Children = { titleBox, subMetaRow },
                    };
                    Grid.SetColumn(titleCol, 1);
                    itemOuter.Children.Add(titleCol);

                    var rightItemControls = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 3,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 1, 0, 0),
                    };

                    if (capturedItem.IsPdf)
                    {
                        var pagesBox = new TextBox
                        {
                            Text = capturedItem.PageRanges ?? string.Empty,
                            Watermark = "Pages (e.g. 1-12)",
                            Width = 114,
                            FontSize = 11,
                            Opacity = capturedItem.IsExcluded ? 0.55 : 1.0,
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        pagesBox.LostFocus += (_, _) =>
                            vm.SetPacketItemPageRanges(capturedItem, pagesBox.Text);
                        rightItemControls.Children.Add(pagesBox);
                    }

                    var upItemBtn = UiBuilder.IconButton(
                        MaterialIconKind.ChevronUp,
                        () => vm.MovePacketItem(capturedSec, capturedItem, -1),
                        14
                    );
                    upItemBtn.IsEnabled = realIdx > 0;
                    rightItemControls.Children.Add(upItemBtn);

                    var downItemBtn = UiBuilder.IconButton(
                        MaterialIconKind.ChevronDown,
                        () => vm.MovePacketItem(capturedSec, capturedItem, +1),
                        14
                    );
                    downItemBtn.IsEnabled = realIdx >= 0 && realIdx < capturedSec.Items.Count - 1;
                    rightItemControls.Children.Add(downItemBtn);

                    if (plan.Sections.Count > 1)
                    {
                        Button? moveBtn = null;
                        moveBtn = UiBuilder.IconButton(
                            MaterialIconKind.FolderMoveOutline,
                            () =>
                            {
                                if (moveBtn is null)
                                    return;

                                var moveFlyout = new MenuFlyout();
                                foreach (
                                    var targetSec in plan.Sections.Where(s =>
                                        !ReferenceEquals(s, capturedSec)
                                    )
                                )
                                {
                                    var capturedTarget = targetSec;
                                    var mi = new MenuItem
                                    {
                                        Header = $"Move to {capturedTarget.Title}",
                                    };
                                    mi.Click += (_, _) =>
                                        vm.MovePacketItemToSection(
                                            capturedSec,
                                            capturedItem,
                                            capturedTarget
                                        );
                                    moveFlyout.Items.Add(mi);
                                }
                                moveFlyout.ShowAt(moveBtn);
                            },
                            14
                        );
                        rightItemControls.Children.Add(moveBtn);
                    }

                    Grid.SetColumn(rightItemControls, 2);
                    itemOuter.Children.Add(rightItemControls);
                    secStack.Children.Add(itemOuter);
                }
            }

            sectionsStack.Children.Add(
                new Border
                {
                    Background = ThemeService.Brush(ThemeService.BgElevated),
                    BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 10),
                    Child = secStack,
                }
            );
        }

        var scroll = UiBuilder.PersistentScrollViewer(
            $"packet-organizer:{course.FolderName}",
            sectionsStack
        );
        scroll.MaxHeight = 460;
        Grid.SetRow(scroll, 3);
        root.Children.Add(scroll);

        var bottomDivider = UiBuilder.HairlineDivider();
        Grid.SetRow(bottomDivider, 4);
        root.Children.Add(bottomDivider);

        // 4. Footer Actions
        var footer = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,Auto"),
            ColumnSpacing = 8,
        };
        var resetBtn = UiBuilder.GhostButton("Reset to Auto", MaterialIconKind.Restore, async () =>
            await vm.ResetPacketPlanToAutoAsync()
        );
        footer.Children.Add(resetBtn);

        var cancelBtn = UiBuilder.GhostButton("Cancel", null, vm.ClosePacketOrganizerModal);
        Grid.SetColumn(cancelBtn, 2);
        footer.Children.Add(cancelBtn);

        var saveCompileBtn = UiBuilder.PrimaryButton(
            "Save & Recompile",
            MaterialIconKind.HammerWrench,
            async () => await vm.SaveAndCompilePacketPlanAsync()
        );
        Grid.SetColumn(saveCompileBtn, 3);
        footer.Children.Add(saveCompileBtn);

        Grid.SetRow(footer, 5);
        root.Children.Add(footer);

        return new Border
        {
            MaxWidth = 820,
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderStrong),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            BoxShadow = BoxShadows.Parse("0 16 40 0 #66000000"),
            Padding = new Thickness(18, 16),
            Margin = new Thickness(18, 22),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Child = root,
        };
    }
}
