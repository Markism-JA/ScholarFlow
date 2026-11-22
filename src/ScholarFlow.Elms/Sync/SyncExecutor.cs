using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using ScholarFlow.Core.Sync;
using ScholarFlow.Elms.Abstractions;
using ScholarFlow.Elms.Models;

namespace ScholarFlow.Elms.Sync;

/// <summary>
/// Executes a <see cref="SyncPlan"/> with bounded concurrency, MIME/extension filtering,
/// path-traversal protection, transient retry, atomic file writes, and SHA-256 state tracking.
/// </summary>
public sealed class SyncExecutor(HttpClient httpClient) : ISyncExecutor
{
    private const int MaxRetries = 3;
    private readonly HttpClient _httpClient = httpClient
        ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<SyncResult> ExecuteAsync(
        SyncPlan plan,
        SyncOptions options,
        LocalSyncState localState,
        IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        var sw = Stopwatch.StartNew();
        var errors = new List<string>();
        var stateLock = new object();

        int downloaded = 0,
            updated = 0,
            relocated = 0,
            unchanged = 0,
            skipped = 0,
            failed = 0;

        foreach (var action in plan.Actions)
        {
            if (action.Kind == SyncActionKind.Unchanged)
            {
                unchanged++;
                if (
                    !options.DryRun
                    && (
                        localState.ResourcesByUrl.TryGetValue(
                            action.Resource.CanonicalUrl,
                            out var rec
                        ) || localState.ResourcesByUrl.TryGetValue(action.Resource.Url, out rec)
                    )
                    && !string.Equals(
                        rec.RelativePath,
                        action.TargetRelativePath,
                        StringComparison.Ordinal
                    )
                )
                {
                    rec.RelativePath = action.TargetRelativePath;
                }
            }
            else if (action.Kind == SyncActionKind.SkippedExternal)
            {
                skipped++;
            }
            else if (
                action.Kind == SyncActionKind.MovedLocally
                && action.ExistingRelocatedPath is not null
            )
            {
                relocated++;
                if (
                    !options.DryRun
                    && (
                        localState.ResourcesByUrl.TryGetValue(
                            action.Resource.CanonicalUrl,
                            out var rec
                        ) || localState.ResourcesByUrl.TryGetValue(action.Resource.Url, out rec)
                    )
                )
                {
                    rec.RelativePath = action.ExistingRelocatedPath;
                    rec.LastSyncedAt = DateTimeOffset.UtcNow;
                }
            }
        }

        var workItems = plan
            .Actions.Where(a => a.Kind is SyncActionKind.New or SyncActionKind.Updated)
            .ToList();

        if (options.DryRun || workItems.Count == 0)
        {
            return new SyncResult(
                plan.Course,
                downloaded,
                updated,
                relocated,
                unchanged,
                skipped,
                failed,
                errors,
                sw.Elapsed
            );
        }

        var concurrency = Math.Max(1, options.MaxConcurrentDownloads);
        using var semaphore = new SemaphoreSlim(concurrency, concurrency);
        int completedCount = 0;

        var tasks = workItems.Select(async action =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var (SkippedByFilter, Record) = await DownloadWithRetryAsync(action, options, ct);
                if (SkippedByFilter)
                {
                    Interlocked.Increment(ref skipped);
                }
                else if (Record is not null)
                {
                    if (action.Kind == SyncActionKind.Updated)
                        Interlocked.Increment(ref updated);
                    else
                        Interlocked.Increment(ref downloaded);

                    lock (stateLock)
                    {
                        localState.ResourcesByUrl[action.Resource.CanonicalUrl] = Record;
                    }
                }

                var done = Interlocked.Increment(ref completedCount);
                progress?.Report(
                    new SyncProgress(
                        "Download",
                        $"{action.Resource.SanitizedFileName}",
                        done,
                        workItems.Count,
                        plan.Course.Title
                    )
                );
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref failed);
                lock (errors)
                {
                    errors.Add($"{action.Resource.CanonicalUrl}: {ex.Message}");
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        return new SyncResult(
            plan.Course,
            downloaded,
            updated,
            relocated,
            unchanged,
            skipped,
            failed,
            errors,
            sw.Elapsed
        );
    }

    private async Task<(bool SkippedByFilter, SyncedResourceRecord? Record)> DownloadWithRetryAsync(
        SyncAction action,
        SyncOptions options,
        CancellationToken ct
    )
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await DownloadOneAsync(action, options, ct);
            }
            catch (HttpRequestException ex)
                when (attempt < MaxRetries && IsTransientStatus(ex.StatusCode))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt * attempt), ct);
            }
            catch (IOException) when (attempt < MaxRetries)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt * attempt), ct);
            }
        }
    }

    private static bool IsTransientStatus(HttpStatusCode? status) =>
        status
            is null
                or HttpStatusCode.TooManyRequests
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout;

    private async Task<(bool SkippedByFilter, SyncedResourceRecord? Record)> DownloadOneAsync(
        SyncAction action,
        SyncOptions options,
        CancellationToken ct
    )
    {
        var resource = action.Resource;
        using var response = await _httpClient.GetAsync(
            resource.Url,
            HttpCompletionOption.ResponseHeadersRead,
            ct
        );
        response.EnsureSuccessStatusCode();

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var fileName = ResolveFinalFileName(resource, response.Content.Headers);

        if (!IsAllowed(fileName, mediaType, options.Filter))
            return (true, null);

        var relPath = ReplaceFileNameInRelativePath(action.TargetRelativePath, fileName);
        var rootFullPath = Path.GetFullPath(options.OutputRootDirectory);
        var destPath = Path.GetFullPath(Path.Combine(rootFullPath, relPath));

        // Defense-in-depth path traversal guard
        if (!destPath.StartsWith(rootFullPath, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Refusing to write outside output root directory: {relPath}"
            );

        var destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        var tempPath = $"{destPath}.tmp.{Guid.NewGuid():N}";
        string sha256Hex;
        long totalBytes;

        try
        {
            await using (var networkStream = await response.Content.ReadAsStreamAsync(ct))
            await using (var fileStream = File.Create(tempPath))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                totalBytes = 0;
                int read;
                while ((read = await networkStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    hasher.AppendData(buffer.AsSpan(0, read));
                    totalBytes += read;
                }
                sha256Hex = Convert.ToHexStringLower(hasher.GetHashAndReset());
            }

            File.Move(tempPath, destPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }

        var record = new SyncedResourceRecord
        {
            Url = resource.CanonicalUrl,
            RelativePath = relPath.Replace('\\', '/'),
            FileName = fileName,
            CourseId = resource.CourseId,
            ETag = response.Headers.ETag?.Tag,
            LastModified = response.Content.Headers.LastModified,
            ContentLength = response.Content.Headers.ContentLength ?? totalBytes,
            Sha256 = sha256Hex,
            LastSyncedAt = DateTimeOffset.UtcNow,
        };

        return (false, record);
    }

    private static string ResolveFinalFileName(ElmsResource resource, HttpContentHeaders headers)
    {
        var ext = Path.GetExtension(resource.SanitizedFileName);
        if (!string.IsNullOrEmpty(ext))
            return resource.SanitizedFileName;

        var cdName = headers.ContentDisposition?.FileNameStar
            ?? headers.ContentDisposition?.FileName;
        if (!string.IsNullOrEmpty(cdName))
        {
            var cdExt = Path.GetExtension(cdName.Trim('"'));
            if (!string.IsNullOrEmpty(cdExt))
                return $"{resource.SanitizedFileName}{cdExt.ToLowerInvariant()}";
        }

        var mimeExt = headers.ContentType?.MediaType?.ToLowerInvariant() switch
        {
            "application/pdf" => ".pdf",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
            "application/zip" => ".zip",
            "text/plain" => ".txt",
            "text/markdown" => ".md",
            _ => string.Empty,
        };

        return $"{resource.SanitizedFileName}{mimeExt}";
    }

    private static bool IsAllowed(string fileName, string? mediaType, ResourceFilter filter)
    {
        var ext = Path.GetExtension(fileName);
        if (!string.IsNullOrEmpty(ext) && filter.AllowedExtensions.Contains(ext))
            return true;

        if (!string.IsNullOrEmpty(mediaType))
        {
            foreach (var prefix in filter.AllowedMimePrefixes)
            {
                if (mediaType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static string ReplaceFileNameInRelativePath(string relPath, string newFileName)
    {
        var dir = Path.GetDirectoryName(relPath);
        return string.IsNullOrEmpty(dir)
            ? newFileName
            : Path.Combine(dir, newFileName).Replace('\\', '/');
    }
}
