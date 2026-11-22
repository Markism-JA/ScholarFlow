using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Pdf;
using ScholarFlow.Core.Services;
using ScholarFlow.Core.Sync;
using ScholarFlow.Elms.Auth;
using ScholarFlow.Elms.Client;

namespace ScholarFlow.Elms.Sync;

/// <summary>
/// Pluggable <see cref="IBasinSyncProvider"/> implementation for STI eLMS (CYPHER).
/// Owns account-scoped <see cref="StiElmsClient"/> instances, session persistence, browser launch,
/// and sync execution cleanly isolated behind <see cref="Core"/> contracts.
/// </summary>
public sealed class StiElmsBasinSyncProvider : IBasinSyncProvider, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, StiElmsClient> _clientsByKey = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ElmsBasinSyncService _syncService;

    public StiElmsBasinSyncProvider(
        BasinService? basinService = null,
        MasterPdfService? pdfService = null,
        ILoggerFactory? loggerFactory = null
    )
    {
        _loggerFactory = loggerFactory;
        _syncService = new ElmsBasinSyncService(
            GetOrCreateClient,
            basinService,
            pdfService: pdfService,
            logger: loggerFactory?.CreateLogger<ElmsBasinSyncService>()
        );
    }

    public string ProviderId => SyncProviderIds.StiElms;
    public string DisplayName => "STI eLMS";

    public ProviderCapabilities Capabilities { get; } =
        new(
            SupportsSync: true,
            SupportsFastPoll: true,
            SupportsPerCourseSync: true,
            SupportsSingleItemRefresh: true,
            EnforcesSingleConcurrentSession: true,
            DefaultTerms: ["Prelims", "Midterms", "Prefinals", "Finals"],
            SupportsInteractiveSessionBrowser: true,
            ConfigFields:
            [
                new ProviderConfigField(
                    Key: "baseUrl",
                    Label: "STI eLMS Portal URL",
                    DefaultValue: "https://elms.sti.edu",
                    Placeholder: "https://elms.sti.edu"
                ),
            ]
        );

    public string GetConcurrencyKey(BasinConfig config)
    {
        var baseUrl = config.GetProviderSetting("baseUrl", "https://elms.sti.edu");
        var host = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : "elms.sti.edu";
        var acct = string.IsNullOrWhiteSpace(config.AccountKey)
            ? "default"
            : config.AccountKey.Trim();
        return $"{ProviderId}:{host}:{acct}".ToLowerInvariant();
    }

    private StiElmsClient GetOrCreateClient(BasinConfig config)
    {
        var key = GetConcurrencyKey(config);
        return _clientsByKey.GetOrAdd(
            key,
            _ => new StiElmsClient(logger: _loggerFactory?.CreateLogger<StiElmsClient>())
        );
    }

    public async Task<ProviderAuthStatus> AuthenticateAsync(
        string basinRoot,
        BasinConfig config,
        bool allowInteractive = true,
        CancellationToken ct = default
    )
    {
        var client = GetOrCreateClient(config);
        var baseUrl = config.GetProviderSetting("baseUrl", "https://elms.sti.edu");
        await client.EnsureConnectedAsync(
            ElmsSessionStore.BuildAuthOptions(config.AccountKey, baseUrl, allowInteractive),
            ct
        );
        return new ProviderAuthStatus(true, $"Authenticated with {baseUrl}", config.AccountKey);
    }

    public async Task DisconnectAsync(
        string basinRoot,
        BasinConfig config,
        CancellationToken ct = default
    )
    {
        var key = GetConcurrencyKey(config);
        if (_clientsByKey.TryRemove(key, out var client))
            await client.DisposeAsync();

        var sessionPath = ElmsSessionStore.GetSessionPathForAccount(config.AccountKey);
        if (File.Exists(sessionPath))
        {
            try
            {
                File.Delete(sessionPath);
            }
            catch
            {
                // Ignore if locked
            }
        }
    }

    public async Task<IReadOnlyList<DiscoveredCourse>> DiscoverCoursesAsync(
        string basinRoot,
        BasinConfig config,
        CancellationToken ct = default
    )
    {
        var client = GetOrCreateClient(config);
        var baseUrl = config.GetProviderSetting("baseUrl", "https://elms.sti.edu");
        await client.EnsureConnectedAsync(
            ElmsSessionStore.BuildAuthOptions(
                config.AccountKey,
                baseUrl,
                allowInteractiveBrowser: true
            ),
            ct
        );
        var courses = await client.GetEnrolledCoursesAsync(ct: ct);
        return
        [
            .. courses.Select(c => new DiscoveredCourse(
                c.CourseId,
                c.Title,
                c.SanitizedFolderName,
                c.CourseCode,
                c.TermOrSection,
                c.TermSuffix,
                c.CourseUrl,
                CourseOrigin.Synced
            )),
        ];
    }

    public Task<IReadOnlyList<AppNotification>> FastPollAsync(
        string basinRoot,
        bool allowInteractiveAuth = true,
        CancellationToken ct = default
    ) => _syncService.FastPollAssignmentsAsync(basinRoot, allowInteractiveAuth, ct);

    public Task<BasinSyncReport> SyncAllAsync(
        string basinRoot,
        bool forceRedownload = false,
        bool allowInteractiveAuth = true,
        IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default
    ) =>
        _syncService.SyncAllAsync(
            basinRoot,
            forceRedownload: forceRedownload,
            compileMasterPdfs: true,
            progress: progress,
            allowInteractiveAuth: allowInteractiveAuth,
            ct: ct
        );

    public Task<BasinSyncReport> SyncCourseAsync(
        string basinRoot,
        string courseFolder,
        IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default
    ) =>
        _syncService.SyncCourseAsync(
            basinRoot,
            courseFolder,
            compileMasterPdf: true,
            progress: progress,
            ct: ct
        );

    public Task<bool> SyncSingleTaskAsync(
        string basinRoot,
        TaskItem task,
        CancellationToken ct = default
    ) => _syncService.SyncSingleAssignmentAsync(basinRoot, task, ct);

    public async Task<bool> OpenExternalUrlAsync(
        string basinRoot,
        BasinConfig config,
        string urlOrPath,
        Func<Task>? onWindowClosed = null,
        CancellationToken ct = default
    )
    {
        var baseUrl = config.GetProviderSetting("baseUrl", "https://elms.sti.edu");
        var authOptions = ElmsSessionStore.BuildAuthOptions(config.AccountKey, baseUrl);
        var proc = ElmsBrowserLauncher.LaunchAppWindow(urlOrPath, authOptions);
        if (proc is null)
            return false;

        if (onWindowClosed is not null)
        {
            try
            {
                await proc.WaitForExitAsync(ct);
                await onWindowClosed();
            }
            catch
            {
                // Ignore process watch errors
            }
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clientsByKey.Values)
            await client.DisposeAsync();
        _clientsByKey.Clear();
    }
}
