using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Services;

namespace ScholarFlow.Core.Sync;

public sealed class RegisteredBasinSyncState
{
    public required string BasinRoot { get; init; }
    public required BasinConfig Config { get; set; }
    public required IBasinSyncProvider Provider { get; set; }
    public IReadOnlyList<BasinCourseEntry> Courses { get; set; } = [];
    public bool IsSyncing { get; set; }
    public DateTimeOffset? LastSyncUtc { get; set; }
    public string LastStatus { get; set; } = "Idle";
    public int ActiveFastPollMinutes { get; set; }
    public int ActiveFullSyncMinutes { get; set; }
    public CancellationTokenSource? LoopCts { get; set; }
    public Task? LoopTask { get; set; }
}

public sealed record BasinNotificationEvent(string BasinRoot, AppNotification Notification);

/// <summary>
/// App-lifetime multi-basin background scheduler and concurrency coordinator.
/// Runs async/PeriodicTimer loops via the .NET ThreadPool across all sync-enabled KnownBasins
/// regardless of which basin is currently active in the UI, while enforcing per-provider/account
/// concurrency locks (e.g., STI eLMS 1-CCU shield) and per-basin write locks.
/// </summary>
public sealed class MultiBasinSyncCoordinator(
    BasinSyncProviderRegistry registry,
    BasinService? basinService = null,
    ILogger<MultiBasinSyncCoordinator>? logger = null
) : IAsyncDisposable
{
    private readonly BasinSyncProviderRegistry _registry = registry
        ?? throw new ArgumentNullException(nameof(registry));
    private readonly BasinService _basinService = basinService ?? new BasinService();
    private readonly ILogger<MultiBasinSyncCoordinator>? _logger = logger;

    private readonly ConcurrentDictionary<string, RegisteredBasinSyncState> _basins = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _providerLocks = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _basinWriteLocks = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly ConcurrentDictionary<
        string,
        (bool IsPaused, DateTimeOffset? PausedUntilUtc)
    > _providerPauseStates = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? StateChanged;
    public event EventHandler<BasinNotificationEvent>? NotificationEmitted;

    public IBasinSyncProvider ResolveProvider(BasinConfig? config) =>
        _registry.Resolve(config?.ProviderId);

    public IReadOnlyList<IBasinSyncProvider> AvailableProviders => _registry.AllProviders;

    public IReadOnlyList<RegisteredBasinSyncState> GetRegisteredBasins() =>
        [.. _basins.Values.OrderBy(b => b.Config.Name, StringComparer.OrdinalIgnoreCase)];

    public RegisteredBasinSyncState? GetBasinState(string basinRoot)
    {
        if (string.IsNullOrWhiteSpace(basinRoot))
            return null;
        return _basins.TryGetValue(Path.GetFullPath(basinRoot), out var state) ? state : null;
    }

    public async Task RegisterKnownBasinsAsync(
        IEnumerable<KnownBasinEntry> entries,
        bool globalElmsFallback = true,
        CancellationToken ct = default
    )
    {
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.FilePath) || !Directory.Exists(entry.FilePath))
                continue;

            var full = Path.GetFullPath(entry.FilePath);
            if (_basins.ContainsKey(full))
                continue;

            var config = await _basinService.InitializeOrOpenAsync(full, entry.Name, ct);
            if (!globalElmsFallback && config.SchemaVersion < 2)
                config.ProviderId = SyncProviderIds.None;

            await RegisterOrUpdateBasinAsync(full, config, startLoop: true, ct);
        }
    }

    public async Task<RegisteredBasinSyncState> RegisterOrUpdateBasinAsync(
        string basinRoot,
        BasinConfig config,
        bool startLoop = true,
        CancellationToken ct = default
    )
    {
        var fullRoot = Path.GetFullPath(basinRoot);
        var provider = _registry.Resolve(config.ProviderId);
        var courses = await _basinService.LoadCoursesAsync(fullRoot, ct);

        var state = _basins.AddOrUpdate(
            fullRoot,
            _ => new RegisteredBasinSyncState
            {
                BasinRoot = fullRoot,
                Config = config,
                Provider = provider,
                Courses = courses,
            },
            (_, existing) =>
            {
                existing.Config = config;
                existing.Provider = provider;
                existing.Courses = courses;
                return existing;
            }
        );

        if (startLoop && provider.Capabilities.SupportsSync && config.BackgroundSyncEnabled)
            EnsurePollingLoopStarted(state);
        else
            StopPollingLoop(state);

        StateChanged?.Invoke(this, EventArgs.Empty);
        return state;
    }

    public bool IsProviderPaused(string concurrencyKey)
    {
        if (!_providerPauseStates.TryGetValue(concurrencyKey, out var entry) || !entry.IsPaused)
            return false;

        if (entry.PausedUntilUtc.HasValue && DateTimeOffset.UtcNow >= entry.PausedUntilUtc.Value)
        {
            ResumeProvider(concurrencyKey);
            return false;
        }
        return true;
    }

    public bool IsAnySingleSessionProviderPaused() =>
        _basins.Values.Any(b =>
            b.Provider.Capabilities.EnforcesSingleConcurrentSession
            && IsProviderPaused(b.Provider.GetConcurrencyKey(b.Config))
        );

    public void PauseProvider(string concurrencyKey, TimeSpan? duration = null)
    {
        DateTimeOffset? until = duration.HasValue
            ? DateTimeOffset.UtcNow.Add(duration.Value)
            : null;
        _providerPauseStates[concurrencyKey] = (true, until);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResumeProvider(string concurrencyKey)
    {
        _providerPauseStates[concurrencyKey] = (false, null);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void PauseAllSingleSessionProviders(TimeSpan? duration = null)
    {
        DateTimeOffset? until = duration.HasValue
            ? DateTimeOffset.UtcNow.Add(duration.Value)
            : null;
        foreach (
            var b in _basins.Values.Where(x =>
                x.Provider.Capabilities.EnforcesSingleConcurrentSession
            )
        )
        {
            _providerPauseStates[b.Provider.GetConcurrencyKey(b.Config)] = (true, until);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResumeAllSingleSessionProviders()
    {
        foreach (
            var b in _basins.Values.Where(x =>
                x.Provider.Capabilities.EnforcesSingleConcurrentSession
            )
        )
        {
            _providerPauseStates[b.Provider.GetConcurrencyKey(b.Config)] = (false, null);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IReadOnlyList<AppNotification>> TriggerFastPollAsync(
        string basinRoot,
        bool allowInteractiveAuth = true,
        CancellationToken ct = default
    )
    {
        var state = await EnsureRegisteredAsync(basinRoot, ct);
        if (!state.Provider.Capabilities.SupportsFastPoll)
            return [];

        return await RunGatedBasinWorkAsync(
            state,
            async () =>
            {
                var notifs = await state.Provider.FastPollAsync(
                    state.BasinRoot,
                    allowInteractiveAuth,
                    ct
                );
                state.Courses = await _basinService.LoadCoursesAsync(state.BasinRoot, ct);
                state.LastSyncUtc = DateTimeOffset.UtcNow;
                state.LastStatus = $"Poll complete ({notifs.Count} update(s))";
                foreach (var n in notifs)
                    NotificationEmitted?.Invoke(
                        this,
                        new BasinNotificationEvent(state.BasinRoot, n)
                    );
                return notifs;
            },
            fallbackOnAuthSkip: [],
            ct: ct
        );
    }

    public async Task<BasinSyncReport> TriggerFullSyncAsync(
        string basinRoot,
        bool force = false,
        bool allowInteractiveAuth = true,
        IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        var state = await EnsureRegisteredAsync(basinRoot, ct);
        if (!state.Provider.Capabilities.SupportsSync)
            return new BasinSyncReport(0, 0, 0, 0, 0, 0, [], []);

        return await RunGatedBasinWorkAsync(
            state,
            async () =>
            {
                var report = await state.Provider.SyncAllAsync(
                    state.BasinRoot,
                    forceRedownload: force,
                    allowInteractiveAuth: allowInteractiveAuth,
                    progress: progress,
                    ct: ct
                );
                state.Courses = await _basinService.LoadCoursesAsync(state.BasinRoot, ct);
                state.LastSyncUtc = DateTimeOffset.UtcNow;
                state.LastStatus =
                    $"Synced {report.CoursesSynced} course(s) (+{report.FilesDownloaded} files)";
                foreach (var n in report.Notifications)
                    NotificationEmitted?.Invoke(
                        this,
                        new BasinNotificationEvent(state.BasinRoot, n)
                    );
                return report;
            },
            fallbackOnAuthSkip: new BasinSyncReport(
                0,
                0,
                0,
                0,
                0,
                0,
                [],
                ["Authentication required"]
            ),
            ct: ct
        );
    }

    public async Task<BasinSyncReport> TriggerCourseSyncAsync(
        string basinRoot,
        string courseFolder,
        IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        var state = await EnsureRegisteredAsync(basinRoot, ct);
        if (!state.Provider.Capabilities.SupportsPerCourseSync)
            return new BasinSyncReport(0, 0, 0, 0, 0, 0, [], []);

        return await RunGatedBasinWorkAsync(
            state,
            async () =>
            {
                var report = await state.Provider.SyncCourseAsync(
                    state.BasinRoot,
                    courseFolder,
                    progress: progress,
                    ct: ct
                );
                state.Courses = await _basinService.LoadCoursesAsync(state.BasinRoot, ct);
                state.LastSyncUtc = DateTimeOffset.UtcNow;
                state.LastStatus = $"Synced {courseFolder} (+{report.FilesDownloaded} new)";
                foreach (var n in report.Notifications)
                    NotificationEmitted?.Invoke(
                        this,
                        new BasinNotificationEvent(state.BasinRoot, n)
                    );
                return report;
            },
            fallbackOnAuthSkip: new BasinSyncReport(
                0,
                0,
                0,
                0,
                0,
                0,
                [],
                ["Authentication required"]
            ),
            ct: ct
        );
    }

    public async Task<bool> TriggerSingleTaskSyncAsync(
        string basinRoot,
        TaskItem task,
        CancellationToken ct = default
    )
    {
        var state = await EnsureRegisteredAsync(basinRoot, ct);
        if (!state.Provider.Capabilities.SupportsSingleItemRefresh)
            return false;

        return await RunGatedBasinWorkAsync(
            state,
            async () =>
            {
                var ok = await state.Provider.SyncSingleTaskAsync(state.BasinRoot, task, ct);
                state.LastSyncUtc = DateTimeOffset.UtcNow;
                state.LastStatus = ok
                    ? $"Refreshed '{task.Title}'"
                    : $"Could not refresh '{task.Title}'";
                return ok;
            },
            fallbackOnAuthSkip: false,
            ct: ct
        );
    }

    private async Task<RegisteredBasinSyncState> EnsureRegisteredAsync(
        string basinRoot,
        CancellationToken ct
    )
    {
        var fullRoot = Path.GetFullPath(basinRoot);
        if (_basins.TryGetValue(fullRoot, out var existing))
            return existing;

        var config = await _basinService.InitializeOrOpenAsync(fullRoot, ct: ct);
        return await RegisterOrUpdateBasinAsync(fullRoot, config, startLoop: false, ct);
    }

    private async Task<T> RunGatedBasinWorkAsync<T>(
        RegisteredBasinSyncState state,
        Func<Task<T>> work,
        T fallbackOnAuthSkip,
        CancellationToken ct
    )
    {
        var providerKey = state.Provider.GetConcurrencyKey(state.Config);
        var providerLock = _providerLocks.GetOrAdd(providerKey, _ => new SemaphoreSlim(1, 1));
        var basinLock = _basinWriteLocks.GetOrAdd(state.BasinRoot, _ => new SemaphoreSlim(1, 1));

        bool enforceProviderGate = state.Provider.Capabilities.EnforcesSingleConcurrentSession;
        if (enforceProviderGate)
            await providerLock.WaitAsync(ct);

        await basinLock.WaitAsync(ct);
        state.IsSyncing = true;
        state.LastStatus = "Syncing...";
        StateChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            return await work();
        }
        catch (ProviderAuthenticationRequiredException ex)
        {
            state.LastStatus = "Needs login";
            if (_logger?.IsEnabled(LogLevel.Information) == true)
                _logger.LogInformation(
                    "Sync deferred for {Basin}: {Message}",
                    state.Config.Name,
                    ex.Message
                );
            return fallbackOnAuthSkip;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            state.LastStatus = $"Error: {ex.Message}";
            _logger?.LogWarning(ex, "Sync failed for {Basin}", state.Config.Name);
            throw;
        }
        finally
        {
            state.IsSyncing = false;
            basinLock.Release();
            if (enforceProviderGate)
                providerLock.Release();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void EnsurePollingLoopStarted(RegisteredBasinSyncState state)
    {
        int fastMin = Math.Max(1, state.Config.FastPollIntervalMinutes);
        int fullMin = Math.Max(5, state.Config.FullSyncIntervalMinutes);
        if (
            state.LoopTask is not null
            && !state.LoopTask.IsCompleted
            && state.ActiveFastPollMinutes == fastMin
            && state.ActiveFullSyncMinutes == fullMin
        )
        {
            return;
        }

        StopPollingLoop(state);
        state.ActiveFastPollMinutes = fastMin;
        state.ActiveFullSyncMinutes = fullMin;
        state.LoopCts = new CancellationTokenSource();
        var ct = state.LoopCts.Token;
        state.LoopTask = Task.Run(() => RunBasinLoopAsync(state, ct), ct);
    }

    private static void StopPollingLoop(RegisteredBasinSyncState state)
    {
        if (state.LoopCts is null)
            return;
        state.LoopCts.Cancel();
        state.LoopCts.Dispose();
        state.LoopCts = null;
        state.LoopTask = null;
    }

    private async Task RunBasinLoopAsync(RegisteredBasinSyncState state, CancellationToken ct)
    {
        var fastInterval = TimeSpan.FromMinutes(Math.Max(1, state.Config.FastPollIntervalMinutes));
        var fullInterval = TimeSpan.FromMinutes(Math.Max(5, state.Config.FullSyncIntervalMinutes));
        var lastFullSync = DateTimeOffset.UtcNow;
        using var timer = new PeriodicTimer(fastInterval);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct))
                    break;

                if (
                    !state.Provider.Capabilities.SupportsSync || !state.Config.BackgroundSyncEnabled
                )
                    continue;

                var concurrencyKey = state.Provider.GetConcurrencyKey(state.Config);
                if (IsProviderPaused(concurrencyKey))
                    continue;

                if (DateTimeOffset.UtcNow - lastFullSync >= fullInterval)
                {
                    await TriggerFullSyncAsync(
                        state.BasinRoot,
                        force: false,
                        allowInteractiveAuth: false,
                        ct: ct
                    );
                    lastFullSync = DateTimeOffset.UtcNow;
                }
                else
                {
                    await TriggerFastPollAsync(
                        state.BasinRoot,
                        allowInteractiveAuth: false,
                        ct: ct
                    );
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Polling error for {Basin}", state.Config.Name);
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
        }
    }

    public async Task<ProviderAuthStatus> AuthenticateBasinAsync(
        string basinRoot,
        bool allowInteractive = true,
        CancellationToken ct = default
    )
    {
        var state = await EnsureRegisteredAsync(basinRoot, ct);
        if (!state.Provider.Capabilities.SupportsSync)
            return new ProviderAuthStatus(true, "Local Knowledge Hub");

        return await RunGatedBasinWorkAsync(
            state,
            async () =>
            {
                var status = await state.Provider.AuthenticateAsync(
                    state.BasinRoot,
                    state.Config,
                    allowInteractive,
                    ct
                );
                state.LastStatus = status.IsAuthenticated
                    ? $"Connected ({status.Message})"
                    : status.Message;
                return status;
            },
            fallbackOnAuthSkip: new ProviderAuthStatus(false, "Authentication required"),
            ct: ct
        );
    }

    public async Task DisconnectBasinAsync(string basinRoot, CancellationToken ct = default)
    {
        var state = await EnsureRegisteredAsync(basinRoot, ct);
        await state.Provider.DisconnectAsync(state.BasinRoot, state.Config, ct);
        state.LastStatus = "Disconnected";
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> OpenExternalUrlWithPauseShieldAsync(
        string basinRoot,
        string urlOrPath,
        Func<Task>? onWindowClosed = null,
        CancellationToken ct = default
    )
    {
        var state = await EnsureRegisteredAsync(basinRoot, ct);
        if (!state.Provider.Capabilities.SupportsInteractiveSessionBrowser)
            return false;

        var concurrencyKey = state.Provider.GetConcurrencyKey(state.Config);
        bool enforceSingle = state.Provider.Capabilities.EnforcesSingleConcurrentSession;
        bool wasManuallyPaused = IsProviderPaused(concurrencyKey);
        if (enforceSingle && !wasManuallyPaused)
            PauseProvider(concurrencyKey, TimeSpan.FromMinutes(90));

        try
        {
            return await state.Provider.OpenExternalUrlAsync(
                state.BasinRoot,
                state.Config,
                urlOrPath,
                async () =>
                {
                    if (enforceSingle && !wasManuallyPaused)
                        ResumeProvider(concurrencyKey);
                    if (onWindowClosed is not null)
                        await onWindowClosed();
                },
                ct
            );
        }
        catch
        {
            if (enforceSingle && !wasManuallyPaused)
                ResumeProvider(concurrencyKey);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var state in _basins.Values)
        {
            var task = state.LoopTask;
            StopPollingLoop(state);
            if (task is not null)
            {
                try
                {
                    await task;
                }
                catch (OperationCanceledException) { }
            }
        }

        foreach (var provider in _registry.AllProviders)
        {
            if (provider is IAsyncDisposable asyncDisp)
                await asyncDisp.DisposeAsync();
            else if (provider is IDisposable disp)
                disp.Dispose();
        }
    }
}
