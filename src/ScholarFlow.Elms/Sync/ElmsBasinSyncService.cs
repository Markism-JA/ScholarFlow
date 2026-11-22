using Microsoft.Extensions.Logging;
using ScholarFlow.Core.Markdown;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Pdf;
using ScholarFlow.Core.Services;
using ScholarFlow.Core.Sync;
using ScholarFlow.Elms.Abstractions;
using ScholarFlow.Elms.Models;
using ScholarFlow.Elms.Parsing;

namespace ScholarFlow.Elms.Sync;

/// <summary>
/// Orchestrates STI eLMS scraping and downloading into a Basin, upserts assignments into tasks.md,
/// diffs PollState to emit AppNotifications, and triggers incremental MasterPdfService compilation.
/// </summary>
public sealed class ElmsBasinSyncService(
    Func<BasinConfig, IElmsClient> clientResolver,
    BasinService? basinService = null,
    ISyncResolver? syncResolver = null,
    MasterPdfService? pdfService = null,
    ILogger<ElmsBasinSyncService>? logger = null
)
{
    public ElmsBasinSyncService(
        IElmsClient elmsClient,
        BasinService? basinService = null,
        ISyncResolver? syncResolver = null,
        MasterPdfService? pdfService = null,
        ILogger<ElmsBasinSyncService>? logger = null
    )
        : this(
            _ => elmsClient ?? throw new ArgumentNullException(nameof(elmsClient)),
            basinService,
            syncResolver,
            pdfService,
            logger
        ) { }

    private readonly Func<BasinConfig, IElmsClient> _clientResolver = clientResolver
        ?? throw new ArgumentNullException(nameof(clientResolver));
    private readonly BasinService _basinService = basinService ?? new BasinService();
    private readonly ISyncResolver _syncResolver = syncResolver ?? new SyncResolver();
    private readonly MasterPdfService _pdfService = pdfService ?? new MasterPdfService();
    private readonly ILogger<ElmsBasinSyncService>? _logger = logger;

    public async Task<BasinSyncReport> SyncAllAsync(
        string basinRoot,
        bool forceRedownload = false,
        bool compileMasterPdfs = true,
        IProgress<SyncProgress>? progress = null,
        bool allowInteractiveAuth = true,
        CancellationToken ct = default
    )
    {
        var notifications = new List<AppNotification>();
        var errors = new List<string>();

        var config = await _basinService.InitializeOrOpenAsync(basinRoot, ct: ct);
        var client = _clientResolver(config);
        var baseUrl = config.GetProviderSetting("baseUrl", "https://elms.sti.edu");
        await client.EnsureConnectedAsync(
            ElmsSessionStore.BuildAuthOptions(config.AccountKey, baseUrl, allowInteractiveAuth),
            ct
        );

        var ignoreFilter = await CourseFilterStore.LoadAsync(
            CourseFilterStore.GetDefaultFilterPath(basinRoot),
            ct
        );
        var discoveredCourses = (await client.GetEnrolledCoursesAsync(ignoreFilter, ct: ct))
            .Where(c =>
                !config.ExcludedCourseIds.Contains(c.CourseId, StringComparer.OrdinalIgnoreCase)
            )
            .ToList();

        var basinCourses = await _basinService.UpsertCoursesAsync(
            basinRoot,
            discoveredCourses.Select(c => new DiscoveredCourse(
                c.CourseId,
                c.Title,
                c.SanitizedFolderName,
                c.CourseCode,
                c.TermOrSection,
                c.TermSuffix,
                c.CourseUrl,
                CourseOrigin.Synced
            )),
            ct
        );

        var statePath = LocalSyncStateStore.GetDefaultStatePath(basinRoot);
        var syncState = await LocalSyncStateStore.LoadAsync(statePath, ct);
        var pollState = await _basinService.LoadPollStateAsync(basinRoot, ct);

        int downloaded = 0,
            updated = 0,
            relocated = 0,
            tasksUpserted = 0,
            pdfsCompiled = 0;

        foreach (var rawElmsCourse in discoveredCourses)
        {
            ct.ThrowIfCancellationRequested();
            var courseEntry = basinCourses.FirstOrDefault(c =>
                (!string.IsNullOrWhiteSpace(c.CourseId) && c.CourseId == rawElmsCourse.CourseId)
                || c.FolderName.Equals(
                    rawElmsCourse.SanitizedFolderName,
                    StringComparison.OrdinalIgnoreCase
                )
                || BasinService.IsSameCourseFuzzy(c.FolderName, rawElmsCourse.SanitizedFolderName)
            )
                ?? basinCourses.First(c =>
                    c.Title.Equals(rawElmsCourse.Title, StringComparison.OrdinalIgnoreCase)
                );

            var elmsCourse = rawElmsCourse with { SanitizedFolderName = courseEntry.FolderName };
            var (d, u, r, t, p) = await SyncSingleCourseInternalAsync(
                client,
                basinRoot,
                config,
                courseEntry,
                elmsCourse,
                syncState,
                pollState,
                forceRedownload,
                compileMasterPdfs,
                notifications,
                errors,
                progress,
                ct
            );
            downloaded += d;
            updated += u;
            relocated += r;
            tasksUpserted += t;
            pdfsCompiled += p;
        }

        syncState.LastFullSyncAt = DateTimeOffset.UtcNow;
        await LocalSyncStateStore.SaveAsync(statePath, syncState, ct);

        pollState.LastFullSyncAt = DateTimeOffset.UtcNow;
        await _basinService.SavePollStateAsync(basinRoot, pollState, ct);

        return new BasinSyncReport(
            discoveredCourses.Count,
            downloaded,
            updated,
            relocated,
            tasksUpserted,
            pdfsCompiled,
            notifications,
            errors
        );
    }

    public async Task<BasinSyncReport> SyncCourseAsync(
        string basinRoot,
        string courseFolder,
        bool forceRedownload = false,
        bool compileMasterPdf = true,
        IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        var notifications = new List<AppNotification>();
        var errors = new List<string>();
        var config = await _basinService.InitializeOrOpenAsync(basinRoot, ct: ct);

        var basinCourses = await _basinService.LoadCoursesAsync(basinRoot, ct);
        var courseEntry = basinCourses.FirstOrDefault(c =>
            c.FolderName.Equals(courseFolder, StringComparison.OrdinalIgnoreCase)
        );
        if (courseEntry is null || string.IsNullOrWhiteSpace(courseEntry.CourseId))
        {
            errors.Add($"Course '{courseFolder}' has no linked eLMS CourseId.");
            return new BasinSyncReport(0, 0, 0, 0, 0, 0, notifications, errors);
        }

        var client = _clientResolver(config);
        var baseUrl = config.GetProviderSetting("baseUrl", "https://elms.sti.edu");
        await client.EnsureConnectedAsync(
            ElmsSessionStore.BuildAuthOptions(
                config.AccountKey,
                baseUrl,
                allowInteractiveBrowser: true
            ),
            ct
        );

        var elmsCourse = new ElmsCourse(
            CourseId: courseEntry.CourseId,
            Title: courseEntry.Title,
            CourseUrl: $"{baseUrl.TrimEnd('/')}/student_class/show/{courseEntry.CourseId}",
            SanitizedFolderName: courseEntry.FolderName,
            CourseCode: courseEntry.CourseCode
        );

        var statePath = LocalSyncStateStore.GetDefaultStatePath(basinRoot);
        var syncState = await LocalSyncStateStore.LoadAsync(statePath, ct);
        var pollState = await _basinService.LoadPollStateAsync(basinRoot, ct);

        var (d, u, r, t, p) = await SyncSingleCourseInternalAsync(
            client,
            basinRoot,
            config,
            courseEntry,
            elmsCourse,
            syncState,
            pollState,
            forceRedownload,
            compileMasterPdf,
            notifications,
            errors,
            progress,
            ct
        );

        await LocalSyncStateStore.SaveAsync(statePath, syncState, ct);
        await _basinService.SavePollStateAsync(basinRoot, pollState, ct);

        return new BasinSyncReport(1, d, u, r, t, p, notifications, errors);
    }

    public async Task<bool> SyncSingleAssignmentAsync(
        string basinRoot,
        TaskItem task,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(task.LmsUrl) || string.IsNullOrWhiteSpace(task.CourseFolder))
            return false;

        var config = await _basinService.InitializeOrOpenAsync(basinRoot, ct: ct);
        var basinCourses = await _basinService.LoadCoursesAsync(basinRoot, ct);
        var courseEntry = basinCourses.FirstOrDefault(c =>
            c.FolderName.Equals(task.CourseFolder, StringComparison.OrdinalIgnoreCase)
        );
        if (courseEntry is null)
            return false;

        var courseId = courseEntry.CourseId ?? string.Empty;
        var client = _clientResolver(config);
        var baseUrl = config.GetProviderSetting("baseUrl", "https://elms.sti.edu");
        await client.EnsureConnectedAsync(
            ElmsSessionStore.BuildAuthOptions(
                config.AccountKey,
                baseUrl,
                allowInteractiveBrowser: true
            ),
            ct
        );

        var html = await client.FetchAuthenticatedHtmlAsync(task.LmsUrl, ct);
        var parsed = AssignmentParser.Parse(html, task.LmsUrl, courseId, baseUrl);
        if (parsed is null)
            return false;

        var isDone = parsed.Status is AssignmentStatus.Submitted or AssignmentStatus.Graded;
        var courseRoot = Path.Combine(basinRoot, courseEntry.FolderName);
        var ongoingDir = Path.Combine(courseRoot, "ongoing");
        var completedDir = Path.Combine(courseRoot, "completed");
        Directory.CreateDirectory(ongoingDir);
        Directory.CreateDirectory(completedDir);

        var noteName = FilenameSanitizer.SanitizeFileName(parsed.Title, ".md");
        var targetNote = Path.Combine(isDone ? completedDir : ongoingDir, noteName);
        var staleNote = Path.Combine(isDone ? ongoingDir : completedDir, noteName);
        await CourseArtifactWriter.WriteIfChangedAsync(
            targetNote,
            HtmlToMarkdownConverter.FormatAssignmentNote(parsed),
            ct
        );
        if (isDone && File.Exists(staleNote))
        {
            try
            {
                File.Delete(staleNote);
            }
            catch
            { /* ignore */
            }
        }

        var pollState = await _basinService.LoadPollStateAsync(basinRoot, ct);
        var notifications = new List<AppNotification>();
        await UpsertCourseAssignmentsAsync(
            basinRoot,
            courseEntry,
            [parsed],
            pollState,
            notifications,
            ct
        );
        await _basinService.SavePollStateAsync(basinRoot, pollState, ct);
        return true;
    }

    private async Task<(
        int Downloaded,
        int Updated,
        int Relocated,
        int TasksUpserted,
        int PdfsCompiled
    )> SyncSingleCourseInternalAsync(
        IElmsClient client,
        string basinRoot,
        BasinConfig config,
        BasinCourseEntry courseEntry,
        ElmsCourse elmsCourse,
        LocalSyncState syncState,
        PollState pollState,
        bool forceRedownload,
        bool compileMasterPdfs,
        List<AppNotification> notifications,
        List<string> errors,
        IProgress<SyncProgress>? progress,
        CancellationToken ct
    )
    {
        int downloaded = 0,
            updated = 0,
            relocated = 0,
            tasksUpserted = 0,
            pdfsCompiled = 0;
        try
        {
            var snapshot = await client.GetCourseSnapshotAsync(elmsCourse, progress, ct);
            var plan = _syncResolver.Resolve(snapshot, syncState, basinRoot, forceRedownload);

            await CourseArtifactWriter.WriteCourseArtifactsAsync(snapshot, basinRoot, ct);

            var executor = client.CreateSyncExecutor();
            var execResult = await executor.ExecuteAsync(
                plan,
                new SyncOptions
                {
                    OutputRootDirectory = basinRoot,
                    ForceRedownload = forceRedownload,
                },
                syncState,
                progress,
                ct
            );

            downloaded += execResult.Downloaded;
            updated += execResult.Updated;
            relocated += execResult.Relocated;
            errors.AddRange(execResult.Errors);

            if (execResult.Downloaded > 0 || execResult.Updated > 0)
            {
                notifications.Add(
                    new AppNotification(
                        NotificationKind.MaterialDownloaded,
                        $"{courseEntry.Title}: {execResult.Downloaded + execResult.Updated} file(s) synced",
                        $"Downloaded {execResult.Downloaded} new, updated {execResult.Updated}.",
                        courseEntry.FolderName,
                        elmsCourse.CourseUrl
                    )
                );
            }

            tasksUpserted += await UpsertCourseAssignmentsAsync(
                basinRoot,
                courseEntry,
                snapshot.Assignments,
                pollState,
                notifications,
                ct,
                snapshot.Lessons
            );

            if (compileMasterPdfs && config.AutoCompileMasterPdf)
            {
                var pdfResult = await _pdfService.CompileIfStaleAsync(
                    basinRoot,
                    courseEntry.FolderName,
                    courseEntry.Title,
                    force: false,
                    ct
                );
                if (pdfResult.WasRegenerated)
                {
                    pdfsCompiled++;
                    notifications.Add(
                        new AppNotification(
                            NotificationKind.MasterPdfCompiled,
                            $"Compiled {courseEntry.Title} Master PDF",
                            $"Regenerated {Path.GetFileName(pdfResult.OutputPdfPath)} ({pdfResult.PageCount} pages).",
                            courseEntry.FolderName
                        )
                    );
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Error syncing course {Course}", elmsCourse.Title);
            errors.Add($"{elmsCourse.Title}: {ex.Message}");
        }

        return (downloaded, updated, relocated, tasksUpserted, pdfsCompiled);
    }

    public async Task<IReadOnlyList<AppNotification>> FastPollAssignmentsAsync(
        string basinRoot,
        bool allowInteractiveAuth = true,
        CancellationToken ct = default
    )
    {
        var notifications = new List<AppNotification>();
        var config = await _basinService.InitializeOrOpenAsync(basinRoot, ct: ct);
        var client = _clientResolver(config);
        var baseUrl = config.GetProviderSetting("baseUrl", "https://elms.sti.edu");
        await client.EnsureConnectedAsync(
            ElmsSessionStore.BuildAuthOptions(config.AccountKey, baseUrl, allowInteractiveAuth),
            ct
        );

        var courses = (await _basinService.LoadCoursesAsync(basinRoot, ct))
            .Where(c =>
                c.IsSyncableCourse
                && !config.ExcludedCourseIds.Contains(c.CourseId!, StringComparer.OrdinalIgnoreCase)
            )
            .ToList();

        var pollState = await _basinService.LoadPollStateAsync(basinRoot, ct);

        foreach (var course in courses)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var assignments = await client.GetAssignmentsAsync(course.CourseId!, ct);
                await UpsertCourseAssignmentsAsync(
                    basinRoot,
                    course,
                    assignments,
                    pollState,
                    notifications,
                    ct
                );
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Fast poll failed for course {Course}", course.Title);
            }
        }

        pollState.LastFastPollAt = DateTimeOffset.UtcNow;
        await _basinService.SavePollStateAsync(basinRoot, pollState, ct);
        return notifications;
    }

    public static Task<int> UpsertCourseAssignmentsAsync(
        string basinRoot,
        BasinCourseEntry course,
        IReadOnlyList<ElmsAssignment> assignments,
        PollState pollState,
        List<AppNotification> notifications,
        CancellationToken ct,
        IReadOnlyList<ElmsLesson>? lessons = null
    )
    {
        var syncedAssignments = assignments
            .Select(a => new SyncedAssignment(
                ExternalId: a.LmsId,
                CourseId: a.CourseId,
                Title: a.Title,
                Url: a.Url,
                DueDate: a.DueDate,
                Status: a.Status switch
                {
                    AssignmentStatus.Submitted => SyncedAssignmentStatus.Submitted,
                    AssignmentStatus.Graded => SyncedAssignmentStatus.Graded,
                    AssignmentStatus.Missing => SyncedAssignmentStatus.Missing,
                    _ => SyncedAssignmentStatus.Pending,
                },
                Score: a.Score,
                TermHeader: a.TermHeader,
                ParentModuleTitle: a.ParentModuleTitle,
                Kind: a.Kind != ElmsAssignmentKind.Unknown ? a.Kind.ToString() : null,
                WeightPercent: a.WeightPercent,
                InstructionFiles: a.Attachments.Where(r =>
                        r.Kind == ElmsResourceKind.AssignmentAttachment
                    )
                    .Select(r => r.RelativeTargetPath)
                    .ToList(),
                SubmissionFiles: a.SubmissionInfo.SubmittedFiles.Where(r =>
                        r.Kind == ElmsResourceKind.SubmittedArtifact
                    )
                    .Select(r => r.RelativeTargetPath)
                    .ToList()
            ))
            .ToList();

        var syncedLessons = lessons
            ?.Select(l => new SyncedLessonInfo(
                l.Title,
                l.SanitizedFolderName,
                l.Resources.Select(r => new SyncedLessonResource(r.RelativeTargetPath)).ToList()
            ))
            .ToList();

        return BasinTaskSynchronizer.UpsertCourseAssignmentsAsync(
            basinRoot,
            course,
            syncedAssignments,
            pollState,
            notifications,
            ct,
            syncedLessons
        );
    }
}
