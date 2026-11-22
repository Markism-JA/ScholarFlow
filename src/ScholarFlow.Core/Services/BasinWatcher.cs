namespace ScholarFlow.Core.Services;

/// <summary>
/// Watches a Basin directory for external edits to Markdown task files and ongoing/completed folders,
/// debouncing rapid filesystem bursts into a single change notification while ignoring self-writes and SQLite WAL files.
/// </summary>
public sealed class BasinWatcher : IDisposable
{
    private static long _globalSuppressUntilTicks;

    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounceTimer;
    private readonly Lock _gate = new();
    private bool _disposed;

    public event EventHandler? BasinChanged;

    /// <summary>
    /// Temporarily suppresses BasinWatcher notifications across the process during app-initiated saves.
    /// </summary>
    public static void SuppressGlobal(TimeSpan duration)
    {
        var until = DateTime.UtcNow.Add(duration).Ticks;
        Interlocked.Exchange(ref _globalSuppressUntilTicks, until);
    }

    public static bool IsSuppressed =>
        DateTime.UtcNow.Ticks < Interlocked.Read(ref _globalSuppressUntilTicks);

    public BasinWatcher(string basinRoot, int debounceMs = 200)
    {
        if (!Directory.Exists(basinRoot))
            throw new DirectoryNotFoundException($"Basin directory does not exist: {basinRoot}");

        _debounceTimer = new Timer(
            _ => OnDebounceElapsed(),
            null,
            Timeout.Infinite,
            Timeout.Infinite
        );

        _watcher = new FileSystemWatcher(basinRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter =
                NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
            EnableRaisingEvents = true,
        };

        _watcher.Changed += (_, e) => ScheduleIfRelevant(e.FullPath, debounceMs);
        _watcher.Created += (_, e) => ScheduleIfRelevant(e.FullPath, debounceMs);
        _watcher.Deleted += (_, e) => ScheduleIfRelevant(e.FullPath, debounceMs);
        _watcher.Renamed += (_, e) => ScheduleIfRelevant(e.FullPath, debounceMs);
    }

    private void ScheduleIfRelevant(string fullPath, int debounceMs)
    {
        if (IsSuppressed)
            return;

        if (
            fullPath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || fullPath.Contains(".tmp.", StringComparison.OrdinalIgnoreCase)
            || fullPath.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
            || fullPath.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase)
            || fullPath.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase)
            || fullPath.EndsWith(".db-journal", StringComparison.OrdinalIgnoreCase)
            || fullPath.EndsWith("poll_state.json", StringComparison.OrdinalIgnoreCase)
        )
        {
            return;
        }

        lock (_gate)
        {
            if (!_disposed)
                _debounceTimer.Change(debounceMs, Timeout.Infinite);
        }
    }

    private void OnDebounceElapsed()
    {
        if (!_disposed && !IsSuppressed)
            BasinChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _debounceTimer.Dispose();
    }
}
