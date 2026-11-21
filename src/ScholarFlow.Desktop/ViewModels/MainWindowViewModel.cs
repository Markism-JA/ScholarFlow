using Avalonia.Threading;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Pdf;
using ScholarFlow.Core.Services;
using ScholarFlow.Core.Sync;
using ScholarFlow.Core.Tasks;
using ScholarFlow.Desktop.Services;
using ScholarFlow.Desktop.Theme;

namespace ScholarFlow.Desktop.ViewModels;

public enum WorkspaceTabMode
{
    TaskList,
    Calendar,
    CourseWorkspace,
    PdfReader,
    SourceViewer,
    QuizStudio,
}

public enum TaskGroupingMode
{
    Default,
    Term,
    DueDate,
    Priority,
    Course,
}

public enum UiChangeKind
{
    FullLayout,
    SelectionOnly,
    QuickAddOnly,
    SearchOrTaskList,
    StatusOnly,
    NotificationsOnly,
    OverlayOnly,
}

public enum SearchResultKind
{
    Course,
    Task,
    PdfDocument,
    CourseFile,
    Quiz,
}

public sealed record SearchResultItem(
    SearchResultKind Kind,
    string Title,
    string Subtitle,
    string Badge,
    TaskItem? Task = null,
    BasinCourseEntry? Course = null,
    string? PdfPath = null,
    Core.Quiz.QuizSessionRecord? Quiz = null
);

/// <summary>
/// Central application ViewModel coordinating Basin state, Todoist smart views, Calendar,
/// Built-in PDF Reader, two-tier eLMS polling, and notifications.
/// </summary>
public sealed partial class MainWindowViewModel : IAsyncDisposable
{
    private readonly BasinService _basinService = new();
    private readonly TaskLifecycleService _lifecycleService = new();
    private readonly LegacyImporter _legacyImporter;
    private readonly MasterPdfService _pdfService = new();
    public MultiBasinSyncCoordinator SyncCoordinator { get; }
    private readonly DesktopNotificationService _notificationService = new();
    private readonly SemaphoreSlim _reloadLock = new(1, 1);

    private BasinWatcher? _watcher;

    public AppSettings Settings { get; private set; } = new();
    public string BasinRoot { get; private set; } = string.Empty;
    public BasinConfig BasinConfig { get; private set; } = new();
    public IBasinSyncProvider ActiveSyncProvider => SyncCoordinator.ResolveProvider(BasinConfig);
    public ProviderCapabilities ActiveCapabilities => ActiveSyncProvider.Capabilities;
    public string ActiveProviderName => ActiveSyncProvider.DisplayName;
    public bool SupportsActiveBasinSync => ActiveCapabilities.SupportsSync;
    public bool SupportsActiveFastPoll => ActiveCapabilities.SupportsFastPoll;
    public bool SupportsActivePerCourseSync => ActiveCapabilities.SupportsPerCourseSync;
    public bool SupportsActiveSingleItemRefresh => ActiveCapabilities.SupportsSingleItemRefresh;
    public bool EnforcesActiveSingleSession => ActiveCapabilities.EnforcesSingleConcurrentSession;
    public IReadOnlyList<string> ActiveTerms => BasinConfig.Terms;
    public IReadOnlyList<IBasinSyncProvider> AvailableSyncProviders =>
        SyncCoordinator.AvailableProviders;

    public WorkspaceTabMode ActiveMode { get; private set; } = WorkspaceTabMode.TaskList;
    public SmartViewKind ActiveSmartView { get; private set; } = SmartViewKind.Today;
    public BasinCourseEntry? ActiveCourse { get; private set; }
    public bool ShowCourseMaterialsSubTab { get; private set; }

    public string? ActiveTermFilter { get; private set; }
    public string? ActiveLabelFilter { get; private set; }
    public string? CompletedCourseFilter { get; private set; }
    public TaskGroupingMode ActiveGrouping { get; private set; } = TaskGroupingMode.Default;

    public IReadOnlyList<BasinCourseEntry> Courses { get; private set; } = [];
    public IReadOnlyList<TaskItem> AllTasks { get; private set; } = [];
    public IReadOnlyList<TaskItem> VisibleTasks { get; private set; } = [];
    public IReadOnlyList<string> AllKnownLabels { get; private set; } = [];
    public IReadOnlyList<UnregisteredOngoingFile> UnregisteredFiles { get; private set; } = [];

    public TaskItem? SelectedTask { get; private set; }
    public string? SelectedTaskNoteContent { get; private set; }
    public TaskItem? PdfReturnTask { get; private set; }
    public bool PdfOpenedFromQuiz { get; private set; }
    public WorkspaceTabMode PdfReturnMode { get; private set; } = WorkspaceTabMode.TaskList;

    public string QuickAddText { get; private set; } = string.Empty;
    public IReadOnlyList<QuickAddChip> QuickAddChips { get; private set; } = [];
    public string SearchQuery { get; private set; } = string.Empty;
    public IReadOnlyList<SearchResultItem> SearchResults { get; private set; } = [];
    public int SelectedSearchResultIndex { get; private set; }

    public bool IsSidebarCollapsed { get; private set; }
    public bool IsInspectorOpen { get; private set; }
    public bool IsQuickAddModalOpen { get; private set; }
    public bool IsSearchModalOpen { get; private set; }
    public bool IsShortcutsModalOpen { get; private set; }
    public bool IsSettingsModalOpen { get; private set; }
    public bool IsOnboardingOpen { get; private set; }
    public bool IsAddCourseModalOpen { get; private set; }
    public bool IsPacketOrganizerModalOpen { get; private set; }
    public BasinCourseEntry? PacketOrganizerCourse { get; private set; }
    public CoursePacketPlan? ActivePacketPlan { get; private set; }
    public string OnboardingVaultPath { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents",
            "ScholarFlow-Basin"
        );
    public string OnboardingProviderId { get; set; } = SyncProviderIds.StiElms;
    public string OnboardingAccountKey { get; set; } = "default";
    public Dictionary<string, string> OnboardingProviderSettings { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _isLocalSyncOperation;
    public bool IsSyncing
    {
        get =>
            _isLocalSyncOperation
            || (SyncCoordinator.GetBasinState(BasinRoot)?.IsSyncing ?? false);
        private set => _isLocalSyncOperation = value;
    }
    public string StatusText { get; private set; } = "Ready";

    public List<AppNotification> Notifications { get; } = [];
    public int UnreadNotificationsCount { get; private set; }
    public bool IsNotificationDrawerOpen { get; private set; }

    public CalendarViewModel Calendar { get; } = new();
    public PdfReaderViewModel PdfReader { get; } = new();
    public QuizStudioViewModel QuizStudio { get; }

    public event EventHandler? StateChanged;
    public event EventHandler<UiChangeKind>? UiChanged;

    public MainWindowViewModel(
        Func<BasinService, MasterPdfService, IEnumerable<IBasinSyncProvider>>? providerFactory = null
    )
    {
        _legacyImporter = new LegacyImporter(_basinService);
        var providers = providerFactory is not null
            ? providerFactory(_basinService, _pdfService)
            : [];
        SyncCoordinator = new MultiBasinSyncCoordinator(
            new BasinSyncProviderRegistry(providers),
            _basinService
        );
        SyncCoordinator.NotificationEmitted += OnCoordinatorNotification;
        SyncCoordinator.StateChanged += (_, _) =>
            Dispatcher.UIThread.Post(() => NotifyStateChanged(UiChangeKind.StatusOnly));
        QuizStudio = new QuizStudioViewModel(this);
        SourceViewer = new SourceViewerViewModel(() =>
            Dispatcher.UIThread.Post(() => NotifyStateChanged(UiChangeKind.SearchOrTaskList))
        );
    }

    public SourceViewerViewModel SourceViewer { get; }
    private WorkspaceTabMode _sourceViewerReturnMode = WorkspaceTabMode.CourseWorkspace;

    public void OpenFileInSourceViewer(
        string filePath,
        int? highlightStartLine = null,
        int? highlightEndLine = null,
        string? sectionTitle = null
    )
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        if (ActiveMode != WorkspaceTabMode.SourceViewer)
            _sourceViewerReturnMode = ActiveMode;

        ActiveMode = WorkspaceTabMode.SourceViewer;
        IsSearchModalOpen = false;
        _ = SourceViewer.LoadFileAsync(filePath, highlightStartLine, highlightEndLine, sectionTitle);
        NotifyStateChanged();
    }

    public void ReturnFromSourceViewer()
    {
        ActiveMode =
            _sourceViewerReturnMode == WorkspaceTabMode.SourceViewer
                ? WorkspaceTabMode.TaskList
                : _sourceViewerReturnMode;
        NotifyStateChanged();
    }

    public async Task InitializeAsync()
    {
        Settings = await AppSettingsStore.LoadAsync();
        ThemeService.ApplyTheme(Settings.Theme);

        var defaultSchoolFiles = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents",
            "School-Files",
            "YR4_01"
        );

        if (
            !Settings.HasCompletedOnboarding
            && Settings.KnownBasins.Count == 0
            && string.IsNullOrWhiteSpace(Settings.LastOpenedBasinPath)
            && !Directory.Exists(defaultSchoolFiles)
        )
        {
            IsOnboardingOpen = true;
            NotifyStateChanged(UiChangeKind.OverlayOnly);
            return;
        }

        if (!Settings.HasCompletedOnboarding)
        {
            Settings.HasCompletedOnboarding = true;
            await AppSettingsStore.SaveAsync(Settings);
        }

        var initialBasin =
            !string.IsNullOrWhiteSpace(Settings.LastOpenedBasinPath)
            && Directory.Exists(Settings.LastOpenedBasinPath)
                ? Settings.LastOpenedBasinPath
            : Directory.Exists(defaultSchoolFiles) ? defaultSchoolFiles
            : OnboardingVaultPath;

        await SyncCoordinator.RegisterKnownBasinsAsync(
            Settings.KnownBasins,
            globalElmsFallback: Settings.EnableElmsIntegration
        );

        await OpenBasinAsync(initialBasin);
    }

    public async Task OpenBasinAsync(string basinPath)
    {
        _watcher?.Dispose();
        ResetWorkspaceStateForBasinSwitch();

        BasinRoot = Path.GetFullPath(basinPath);
        BasinConfig = await _basinService.InitializeOrOpenAsync(BasinRoot);
        if (BasinConfig.SchemaVersion < 2 && !Settings.EnableElmsIntegration)
            BasinConfig.ProviderId = SyncProviderIds.None;

        // If courses.json is empty and legacy subdirectories exist, run non-destructive legacy import automatically
        var existingCourses = await _basinService.LoadCoursesAsync(BasinRoot);
        if (
            existingCourses.Count == 0
            && Directory.GetDirectories(BasinRoot).Any(d => !Path.GetFileName(d).StartsWith('.'))
        )
        {
            var legacyYaml = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                "Ongoing",
                "metadata.yaml"
            );
            var summary = await _legacyImporter.ImportAsync(BasinRoot, legacyYaml);
            StatusText =
                $"Imported {summary.CoursesImported} courses & {summary.OngoingTasksImported} tasks from Basin";
        }

        Settings.LastOpenedBasinPath = BasinRoot;
        if (
            !Settings.KnownBasins.Any(b =>
                b.FilePath.Equals(BasinRoot, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            Settings.KnownBasins.Insert(
                0,
                new KnownBasinEntry { Name = BasinConfig.Name, FilePath = BasinRoot }
            );
        }
        await AppSettingsStore.SaveAsync(Settings);

        await ReloadBasinDataAsync();

        _watcher = new BasinWatcher(BasinRoot);
        _watcher.BasinChanged += (_, _) =>
            Dispatcher.UIThread.Post(async () => await ReloadBasinDataAsync());

        await SyncCoordinator.RegisterOrUpdateBasinAsync(
            BasinRoot,
            BasinConfig,
            startLoop: BasinConfig.BackgroundSyncEnabled
        );
    }

    private void ResetWorkspaceStateForBasinSwitch()
    {
        ActiveMode = WorkspaceTabMode.TaskList;
        ActiveSmartView = SmartViewKind.Today;
        ActiveCourse = null;
        ShowCourseMaterialsSubTab = false;
        ActiveTermFilter = null;
        ActiveLabelFilter = null;
        CompletedCourseFilter = null;
        ActiveGrouping = TaskGroupingMode.Default;

        Courses = [];
        AllTasks = [];
        VisibleTasks = [];
        AllKnownLabels = [];
        UnregisteredFiles = [];

        SelectedTask = null;
        SelectedTaskNoteContent = null;
        PdfReturnTask = null;
        PdfOpenedFromQuiz = false;
        PdfReturnMode = WorkspaceTabMode.TaskList;

        IsInspectorOpen = false;
        IsAddCourseModalOpen = false;
        ConfirmingDeleteCourseFolder = null;
        IsPacketOrganizerModalOpen = false;
        PacketOrganizerCourse = null;
        ActivePacketPlan = null;
        IsQuickAddModalOpen = false;
        QuickAddText = string.Empty;
        QuickAddChips = [];
        IsSearchModalOpen = false;
        SearchQuery = string.Empty;
        SearchCategoryFilter = "All";
        ExcludeFilesInQuickSearch = false;
        SearchResults = [];
        SelectedSearchResultIndex = 0;

        Notifications.Clear();
        UnreadNotificationsCount = 0;
        IsNotificationDrawerOpen = false;

        PdfReader.Reset();
        SourceViewer.Reset();
        QuizStudio.ResetForBasinSwitch();
        ResetCourseMaterialsUiState();
    }

    public async Task ReloadBasinDataAsync()
    {
        if (string.IsNullOrWhiteSpace(BasinRoot))
            return;

        await _reloadLock.WaitAsync();
        try
        {
            Courses = await _basinService.LoadCoursesAsync(BasinRoot);
            AllTasks = await _basinService.LoadAllTasksAsync(BasinRoot);
            AllKnownLabels =
            [
                .. EnumerateAllTaskNodes(AllTasks)
                    .SelectMany(t => t.Labels)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(l => l, StringComparer.OrdinalIgnoreCase),
            ];
            UnregisteredFiles = TaskQueryService.FindUnregisteredOngoingFiles(
                BasinRoot,
                Courses,
                AllTasks
            );
            Calendar.UpdateTasks([.. EnumerateAllTaskNodes(AllTasks)]);
            await QuizStudio.RefreshAsync();

            RecomputeVisibleTasks();
        }
        finally
        {
            _reloadLock.Release();
        }

        NotifyStateChanged();
    }

    public static IEnumerable<TaskItem> EnumerateAllTaskNodes(IEnumerable<TaskItem> roots)
    {
        foreach (var t in roots)
        {
            yield return t;
            foreach (var c in EnumerateAllTaskNodes(t.ChildTasks))
                yield return c;
        }
    }

    private void NotifyStateChanged(UiChangeKind kind = UiChangeKind.FullLayout)
    {
        UiChanged?.Invoke(this, kind);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        _watcher?.Dispose();
        SyncCoordinator.NotificationEmitted -= OnCoordinatorNotification;
        PdfReader.Dispose();
        SourceViewer.Dispose();
        await SyncCoordinator.DisposeAsync();
    }
}
