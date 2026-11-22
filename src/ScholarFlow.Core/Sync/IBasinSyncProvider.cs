using ScholarFlow.Core.Models;

namespace ScholarFlow.Core.Sync;

public sealed record SyncProgress(
    string Stage,
    string Message,
    int? Completed = null,
    int? Total = null,
    string? CourseTitle = null
);

public sealed record BasinSyncReport(
    int CoursesSynced,
    int FilesDownloaded,
    int FilesUpdated,
    int FilesRelocated,
    int TasksUpserted,
    int MasterPdfsCompiled,
    IReadOnlyList<AppNotification> Notifications,
    IReadOnlyList<string> Errors
);

/// <summary>
/// Exception thrown by a pluggable provider when non-interactive background sync encounters
/// an expired or missing remote session, allowing the coordinator to defer without crashing.
/// </summary>
public class ProviderAuthenticationRequiredException(
    string message,
    Exception? innerException = null
) : InvalidOperationException(message, innerException);

/// <summary>
/// Describes a configurable provider setting displayed dynamically in Basin/Onboarding UI.
/// </summary>
public sealed record ProviderConfigField(
    string Key,
    string Label,
    string DefaultValue = "",
    string? Placeholder = null,
    bool IsSecret = false
);

public sealed record ProviderAuthStatus(
    bool IsAuthenticated,
    string Message,
    string? AccountLabel = null
);

/// <summary>
/// Declares the runtime capabilities and invariants of a pluggable basin sync provider
/// so the coordinator and UI adapt without hardcoding LMS-specific assumptions.
/// </summary>
public sealed record ProviderCapabilities(
    bool SupportsSync,
    bool SupportsFastPoll,
    bool SupportsPerCourseSync,
    bool SupportsSingleItemRefresh,
    bool EnforcesSingleConcurrentSession,
    IReadOnlyList<string> DefaultTerms,
    bool SupportsInteractiveSessionBrowser = false,
    IReadOnlyList<ProviderConfigField>? ConfigFields = null
);

/// <summary>
/// Canonical sync provider abstraction isolating LMS-specific auth, scraping, or APIs
/// behind ScholarFlow's unified domain model.
/// </summary>
public interface IBasinSyncProvider
{
    string ProviderId { get; }
    string DisplayName { get; }
    ProviderCapabilities Capabilities { get; }

    string GetConcurrencyKey(BasinConfig config);

    Task<ProviderAuthStatus> AuthenticateAsync(
        string basinRoot,
        BasinConfig config,
        bool allowInteractive = true,
        CancellationToken ct = default
    ) => Task.FromResult(new ProviderAuthStatus(true, "Connected"));

    Task DisconnectAsync(string basinRoot, BasinConfig config, CancellationToken ct = default) =>
        Task.CompletedTask;

    Task<IReadOnlyList<DiscoveredCourse>> DiscoverCoursesAsync(
        string basinRoot,
        BasinConfig config,
        CancellationToken ct = default
    ) => Task.FromResult<IReadOnlyList<DiscoveredCourse>>([]);

    Task<IReadOnlyList<AppNotification>> FastPollAsync(
        string basinRoot,
        bool allowInteractiveAuth = true,
        CancellationToken ct = default
    );

    Task<BasinSyncReport> SyncAllAsync(
        string basinRoot,
        bool forceRedownload = false,
        bool allowInteractiveAuth = true,
        IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default
    );

    Task<BasinSyncReport> SyncCourseAsync(
        string basinRoot,
        string courseFolder,
        IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default
    );

    Task<bool> SyncSingleTaskAsync(string basinRoot, TaskItem task, CancellationToken ct = default);

    Task<bool> OpenExternalUrlAsync(
        string basinRoot,
        BasinConfig config,
        string urlOrPath,
        Func<Task>? onWindowClosed = null,
        CancellationToken ct = default
    ) => Task.FromResult(false);
}

/// <summary>
/// Provider for local-only Knowledge Hub / book / Markdown basins with zero remote sync.
/// </summary>
public sealed class LocalKnowledgeBasinSyncProvider : IBasinSyncProvider
{
    public static LocalKnowledgeBasinSyncProvider Instance { get; } = new();

    public string ProviderId => SyncProviderIds.None;
    public string DisplayName => "Local Knowledge Hub";

    public ProviderCapabilities Capabilities { get; } =
        new(
            SupportsSync: false,
            SupportsFastPoll: false,
            SupportsPerCourseSync: false,
            SupportsSingleItemRefresh: false,
            EnforcesSingleConcurrentSession: false,
            DefaultTerms: [],
            SupportsInteractiveSessionBrowser: false
        );

    public string GetConcurrencyKey(BasinConfig config) => SyncProviderIds.None;

    public Task<IReadOnlyList<AppNotification>> FastPollAsync(
        string basinRoot,
        bool allowInteractiveAuth = true,
        CancellationToken ct = default
    ) => Task.FromResult<IReadOnlyList<AppNotification>>([]);

    public Task<BasinSyncReport> SyncAllAsync(
        string basinRoot,
        bool forceRedownload = false,
        bool allowInteractiveAuth = true,
        IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default
    ) => Task.FromResult(new BasinSyncReport(0, 0, 0, 0, 0, 0, [], []));

    public Task<BasinSyncReport> SyncCourseAsync(
        string basinRoot,
        string courseFolder,
        IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default
    ) => Task.FromResult(new BasinSyncReport(0, 0, 0, 0, 0, 0, [], []));

    public Task<bool> SyncSingleTaskAsync(
        string basinRoot,
        TaskItem task,
        CancellationToken ct = default
    ) => Task.FromResult(false);
}

public sealed class BasinSyncProviderRegistry
{
    private readonly Dictionary<string, IBasinSyncProvider> _providers = new(
        StringComparer.OrdinalIgnoreCase
    );

    public BasinSyncProviderRegistry(IEnumerable<IBasinSyncProvider> providers)
    {
        Register(LocalKnowledgeBasinSyncProvider.Instance);
        foreach (var p in providers)
            Register(p);
    }

    public void Register(IBasinSyncProvider provider) => _providers[provider.ProviderId] = provider;

    public IBasinSyncProvider Resolve(string? providerId)
    {
        if (
            !string.IsNullOrWhiteSpace(providerId)
            && _providers.TryGetValue(providerId.Trim(), out var found)
        )
        {
            return found;
        }
        return LocalKnowledgeBasinSyncProvider.Instance;
    }

    public IReadOnlyList<IBasinSyncProvider> AllProviders => [.. _providers.Values];
}
