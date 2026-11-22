using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Material.Icons.Avalonia;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Sync;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.ViewModels;
using ScholarFlow.Desktop.Views;
using ScholarFlow.Elms.Sync;

namespace ScholarFlow.Desktop;

/// <summary>
/// Pure C# Avalonia Application bootstrapper (zero .axaml files).
/// </summary>
public sealed class App : Application
{
    private MainWindowViewModel? _mainVm;
    private MainWindow? _mainWindow;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private TrayIcon? _trayIcon;
    private string? _lastTrayStateSignature;
    private bool _isExplicitExit;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new MaterialIconStyles(null));
        ThemeService.ApplyTheme(ThemeMode.Dark);

        // Pure C# global styles for Linux Skia ToolTip rendering & TextBox focus aesthetics
        var tooltipStyle = new Style(x => x.OfType<ToolTip>())
        {
            Setters =
            {
                new Setter(
                    TemplatedControl.BackgroundProperty,
                    ThemeService.Brush(ThemeService.BgSurface)
                ),
                new Setter(
                    TemplatedControl.ForegroundProperty,
                    ThemeService.Brush(ThemeService.TextPrimary)
                ),
                new Setter(
                    TemplatedControl.BorderBrushProperty,
                    ThemeService.Brush(ThemeService.BorderStrong)
                ),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1)),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6)),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 6)),
                new Setter(TemplatedControl.FontSizeProperty, 12.0),
            },
        };
        Styles.Add(tooltipStyle);

        var textBoxStyle = new Style(x => x.OfType<TextBox>())
        {
            Setters =
            {
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6)),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 7)),
            },
        };
        Styles.Add(textBoxStyle);

        var datePickerStyle = new Style(x => x.OfType<CalendarDatePicker>())
        {
            Setters = { new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6)) },
        };
        Styles.Add(datePickerStyle);

        var menuFlyoutStyle = new Style(x => x.OfType<MenuFlyoutPresenter>())
        {
            Setters =
            {
                new Setter(
                    TemplatedControl.BackgroundProperty,
                    ThemeService.Brush(ThemeService.BgSurface)
                ),
                new Setter(
                    TemplatedControl.BorderBrushProperty,
                    ThemeService.Brush(ThemeService.BorderSubtle)
                ),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1)),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(8)),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(4)),
            },
        };
        Styles.Add(menuFlyoutStyle);

        var menuItemStyle = new Style(x => x.OfType<MenuItem>())
        {
            Setters =
            {
                new Setter(
                    TemplatedControl.ForegroundProperty,
                    ThemeService.Brush(ThemeService.TextPrimary)
                ),
                new Setter(TemplatedControl.FontSizeProperty, 12.5),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(5)),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 6)),
            },
        };
        Styles.Add(menuItemStyle);

        var menuItemHoverStyle = new Style(x => x.OfType<MenuItem>().Class(":pointerover"))
        {
            Setters =
            {
                new Setter(
                    TemplatedControl.BackgroundProperty,
                    ThemeService.Brush(ThemeService.BgHover)
                ),
            },
        };
        Styles.Add(menuItemHoverStyle);

        var menuItemSelectedStyle = new Style(x => x.OfType<MenuItem>().Class(":selected"))
        {
            Setters =
            {
                new Setter(
                    TemplatedControl.BackgroundProperty,
                    ThemeService.Brush(ThemeService.AccentSoftBg)
                ),
                new Setter(
                    TemplatedControl.ForegroundProperty,
                    ThemeService.Brush(ThemeService.AccentPrimary)
                ),
            },
        };
        Styles.Add(menuItemSelectedStyle);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _desktop = desktop;
            _mainVm = new MainWindowViewModel(
                (basin, pdf) => [new StiElmsBasinSyncProvider(basin, pdf)]
            );
            var window = new MainWindow(_mainVm);
            _mainWindow = window;
            desktop.MainWindow = window;

            window.Closing += (_, e) =>
            {
                if (!_isExplicitExit && _mainVm.Settings.MinimizeToTrayOnClose)
                {
                    e.Cancel = true;
                    window.Hide();
                }
            };

            InitializeTrayIcon(desktop, window);

            desktop.Exit += async (_, _) =>
            {
                if (_mainVm is not null)
                {
                    _mainVm.StateChanged -= OnMainVmStateChanged;
                    _mainVm.SyncCoordinator.StateChanged -= OnCoordinatorStateChanged;
                }
                _trayIcon?.Dispose();
                if (_mainVm is not null)
                    await _mainVm.DisposeAsync();
            };

            if (Program.StartMinimized)
                window.Hide();

            _ = _mainVm.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void InitializeTrayIcon(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window
    )
    {
        try
        {
            WindowIcon? icon = null;
            using var stream = typeof(App).Assembly.GetManifestResourceStream(
                "ScholarFlow.Assets.scholarflow.ico"
            );
            if (stream is not null)
                icon = new WindowIcon(stream);

            var menu = new NativeMenu();

            _trayIcon = new TrayIcon
            {
                ToolTipText = "ScholarFlow — Academic Workspace",
                Icon = icon,
                Menu = menu,
                IsVisible = true,
            };
            _trayIcon.Clicked += (_, _) => RestoreMainWindow(window);

            if (_mainVm is not null)
            {
                _mainVm.StateChanged += OnMainVmStateChanged;
                _mainVm.SyncCoordinator.StateChanged += OnCoordinatorStateChanged;
                UpdateTrayMenuState();
            }
        }
        catch
        {
            // TrayIcon is optional on headless/unsupported window managers
        }
    }

    private void OnMainVmStateChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(UpdateTrayMenuState);

    private void OnCoordinatorStateChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(UpdateTrayMenuState);

    private void UpdateTrayMenuState()
    {
        if (_trayIcon is null || _mainVm is null || _desktop is null || _mainWindow is null)
            return;

        var registeredBasins = _mainVm.SyncCoordinator.GetRegisteredBasins();
        var activeRoot = string.IsNullOrWhiteSpace(_mainVm.BasinRoot)
            ? string.Empty
            : Path.GetFullPath(_mainVm.BasinRoot);
        bool hasSingleSessionProvider = registeredBasins.Any(b =>
            b.Provider.Capabilities.EnforcesSingleConcurrentSession
        );
        bool isSingleSessionPaused =
            hasSingleSessionProvider && _mainVm.SyncCoordinator.IsAnySingleSessionProviderPaused();

        var syncingBasins = registeredBasins.Where(b => b.IsSyncing).ToList();
        var knownSignature = string.Join(
            "|",
            _mainVm.Settings.KnownBasins.Select(k => $"{k.Name}:{k.FilePath}")
        );
        var basinSignature = string.Join(
            "|",
            registeredBasins.Select(b =>
            {
                var excluded = new HashSet<string>(
                    b.Config.ExcludedCourseIds,
                    StringComparer.OrdinalIgnoreCase
                );
                var courses = b
                    .Courses.Where(c => c.IsSyncableCourse && !excluded.Contains(c.CourseId!))
                    .OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
                    .Select(c => $"{c.FolderName}:{c.Title}");
                return $"{b.BasinRoot}:{b.Config.Name}:{b.Config.ProviderId}:{b.Config.BackgroundSyncEnabled}:{b.IsSyncing}:{b.LastStatus}:{string.Join(",", courses)}";
            })
        );
        string sig = $"{activeRoot}::{isSingleSessionPaused}::{knownSignature}::{basinSignature}";
        if (sig == _lastTrayStateSignature)
            return;
        _lastTrayStateSignature = sig;

        _trayIcon.ToolTipText =
            syncingBasins.Count > 0
                ? $"ScholarFlow — Syncing {string.Join(", ", syncingBasins.Select(b => b.Config.Name))}..."
            : isSingleSessionPaused ? "ScholarFlow — STI eLMS Polling PAUSED"
            : "ScholarFlow — Academic Workspace";

        var menu = _trayIcon.Menu ?? [];
        menu.Items.Clear();

        var showItem = new NativeMenuItem("Show ScholarFlow");
        showItem.Click += (_, _) => RestoreMainWindow(_mainWindow);
        menu.Items.Add(showItem);

        AddSwitchBasinSubmenu(menu, registeredBasins, activeRoot);

        var syncableBasins = registeredBasins
            .Where(b => b.Provider.Capabilities.SupportsSync)
            .ToList();

        if (hasSingleSessionProvider || syncableBasins.Count > 0)
            menu.Items.Add(new NativeMenuItemSeparator());

        if (hasSingleSessionProvider)
        {
            var pauseItem = new NativeMenuItem(
                isSingleSessionPaused ? "Resume STI eLMS Polling" : "Pause STI eLMS Polling"
            );
            pauseItem.Click += (_, _) =>
            {
                if (_mainVm.SyncCoordinator.IsAnySingleSessionProviderPaused())
                    _mainVm.SyncCoordinator.ResumeAllSingleSessionProviders();
                else
                    _mainVm.SyncCoordinator.PauseAllSingleSessionProviders();
                UpdateTrayMenuState();
            };
            menu.Items.Add(pauseItem);
        }

        foreach (var basin in syncableBasins)
            menu.Items.Add(BuildSyncableBasinMenuItem(basin, activeRoot));

        menu.Items.Add(new NativeMenuItemSeparator());

        var quitItem = new NativeMenuItem("Quit ScholarFlow");
        quitItem.Click += (_, _) =>
        {
            _isExplicitExit = true;
            _desktop.Shutdown();
        };
        menu.Items.Add(quitItem);

        _trayIcon.Menu = menu;
    }

    private void AddSwitchBasinSubmenu(
        NativeMenu rootMenu,
        IReadOnlyList<RegisteredBasinSyncState> registeredBasins,
        string activeRoot
    )
    {
        var switchMenu = new NativeMenu();
        var stateByPath = registeredBasins.ToDictionary(
            b => b.BasinRoot,
            b => b,
            StringComparer.OrdinalIgnoreCase
        );

        foreach (var entry in _mainVm!.Settings.KnownBasins)
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
            string prefix = isActive ? "✓ " : "";

            var item = new NativeMenuItem($"{prefix}{entry.Name} [{kindTag}]");
            item.Click += async (_, _) =>
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await _mainVm.OpenBasinAsync(full);
                });
            };
            switchMenu.Items.Add(item);
        }

        if (switchMenu.Items.Count > 0)
        {
            rootMenu.Items.Add(new NativeMenuItem("Switch Basin") { Menu = switchMenu });
        }
    }

    private NativeMenuItem BuildSyncableBasinMenuItem(
        RegisteredBasinSyncState basin,
        string activeRoot
    )
    {
        bool isActive = string.Equals(
            basin.BasinRoot,
            activeRoot,
            StringComparison.OrdinalIgnoreCase
        );
        string activePrefix = isActive ? "● " : "";
        string syncSuffix = basin.IsSyncing ? " — Syncing..." : "";

        var subMenu = new NativeMenu();

        var statusItem = new NativeMenuItem($"Status: {basin.LastStatus}") { IsEnabled = false };
        subMenu.Items.Add(statusItem);
        subMenu.Items.Add(new NativeMenuItemSeparator());

        if (basin.Provider.Capabilities.SupportsFastPoll)
        {
            var pollItem = new NativeMenuItem("Fast Poll Assignments")
            {
                IsEnabled = !basin.IsSyncing,
            };
            pollItem.Click += async (_, _) => await TriggerTrayFastPollAsync(basin, isActive);
            subMenu.Items.Add(pollItem);
        }

        var excluded = new HashSet<string>(
            basin.Config.ExcludedCourseIds,
            StringComparer.OrdinalIgnoreCase
        );
        var syncableCourses = basin
            .Courses.Where(c => c.IsSyncableCourse && !excluded.Contains(c.CourseId!))
            .OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (basin.Provider.Capabilities.SupportsPerCourseSync && syncableCourses.Count > 0)
        {
            var courseMenu = new NativeMenu();
            foreach (var course in syncableCourses)
            {
                var capturedCourse = course;
                var courseItem = new NativeMenuItem(capturedCourse.Title)
                {
                    IsEnabled = !basin.IsSyncing,
                };
                courseItem.Click += async (_, _) =>
                    await TriggerTrayCourseSyncAsync(basin, capturedCourse, isActive);
                courseMenu.Items.Add(courseItem);
            }
            subMenu.Items.Add(
                new NativeMenuItem("Sync Course")
                {
                    IsEnabled = !basin.IsSyncing,
                    Menu = courseMenu,
                }
            );
        }

        var fullSyncItem = new NativeMenuItem(
            basin.IsSyncing ? "Syncing..." : $"Full {basin.Provider.DisplayName} Sync"
        )
        {
            IsEnabled = !basin.IsSyncing,
        };
        fullSyncItem.Click += async (_, _) => await TriggerTrayFullSyncAsync(basin, isActive);
        subMenu.Items.Add(fullSyncItem);

        subMenu.Items.Add(new NativeMenuItemSeparator());
        var openItem = new NativeMenuItem("Open Basin in Window");
        openItem.Click += async (_, _) =>
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await _mainVm!.OpenBasinAsync(basin.BasinRoot);
                RestoreMainWindow(_mainWindow!);
            });
        };
        subMenu.Items.Add(openItem);

        return new NativeMenuItem(
            $"{activePrefix}{basin.Config.Name} ({basin.Provider.DisplayName}){syncSuffix}"
        )
        {
            Menu = subMenu,
        };
    }

    private async Task TriggerTrayFastPollAsync(RegisteredBasinSyncState basin, bool isActive)
    {
        if (_mainVm is null)
            return;
        if (isActive)
            await Dispatcher.UIThread.InvokeAsync(_mainVm.RunFastPollAsync);
        else
            await _mainVm.SyncCoordinator.TriggerFastPollAsync(basin.BasinRoot);
    }

    private async Task TriggerTrayCourseSyncAsync(
        RegisteredBasinSyncState basin,
        BasinCourseEntry course,
        bool isActive
    )
    {
        if (_mainVm is null)
            return;
        if (isActive)
            await Dispatcher.UIThread.InvokeAsync(() => _mainVm.RunCourseSyncAsync(course));
        else
            await _mainVm.SyncCoordinator.TriggerCourseSyncAsync(
                basin.BasinRoot,
                course.FolderName
            );
    }

    private async Task TriggerTrayFullSyncAsync(RegisteredBasinSyncState basin, bool isActive)
    {
        if (_mainVm is null)
            return;
        if (isActive)
            await Dispatcher.UIThread.InvokeAsync(_mainVm.RunFullSyncAsync);
        else
            await _mainVm.SyncCoordinator.TriggerFullSyncAsync(basin.BasinRoot);
    }

    private static void RestoreMainWindow(MainWindow window)
    {
        if (!window.IsVisible)
            window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
