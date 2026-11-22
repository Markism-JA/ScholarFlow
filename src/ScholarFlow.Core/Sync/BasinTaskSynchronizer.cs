using ScholarFlow.Core.Markdown;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Pdf;
using ScholarFlow.Core.Services;

namespace ScholarFlow.Core.Sync;

public enum SyncedAssignmentStatus
{
    Pending,
    Submitted,
    Graded,
    Missing,
}

public sealed record SyncedLessonResource(string RelativeTargetPath);

public sealed record SyncedLessonInfo(
    string Title,
    string SanitizedFolderName,
    IReadOnlyList<SyncedLessonResource> Resources
);

public sealed record SyncedAssignment(
    string ExternalId,
    string CourseId,
    string Title,
    string Url,
    DateTimeOffset? DueDate,
    SyncedAssignmentStatus Status,
    string? Score,
    string? TermHeader,
    string? ParentModuleTitle,
    string? Kind,
    double? WeightPercent,
    IReadOnlyList<string> InstructionFiles,
    IReadOnlyList<string> SubmissionFiles
);

/// <summary>
/// Provider-neutral assignment upsert and notification diff engine shared across all LMS plugins.
/// Merges remote assignments into the course's <c>tasks.md</c>, enriches task files/terms, and diffs
/// <see cref="PollState"/> to emit <see cref="AppNotification"/> items.
/// </summary>
public static class BasinTaskSynchronizer
{
    public static async Task<int> UpsertCourseAssignmentsAsync(
        string basinRoot,
        BasinCourseEntry course,
        IReadOnlyList<SyncedAssignment> assignments,
        PollState pollState,
        List<AppNotification> notifications,
        CancellationToken ct,
        IReadOnlyList<SyncedLessonInfo>? lessons = null
    )
    {
        var tasksPath = BasinService.GetCourseTasksPath(basinRoot, course.FolderName);
        var existingTasks = await MarkdownTaskCodec.LoadFileAsync(tasksPath, course.FolderName, ct);
        var basinConfig = await new BasinService().InitializeOrOpenAsync(basinRoot, ct: ct);
        bool isStiProvider = string.Equals(
            basinConfig.ProviderId,
            SyncProviderIds.StiElms,
            StringComparison.OrdinalIgnoreCase
        );
        var courseTermMap = BasinService.LoadCourseTermMap(
            basinRoot,
            course.FolderName,
            basinConfig.Terms,
            useStiSyllabusFallback: isStiProvider
        );
        var courseDir = Path.Combine(basinRoot, course.FolderName);
        var syllabusPdfPath = isStiProvider
            ? SyllabusTermParser.FindSyllabusPdfPath(courseDir)
            : null;

        var incomingTasks = new List<TaskItem>();
        foreach (var a in assignments)
        {
            var isDone =
                a.Status is SyncedAssignmentStatus.Submitted or SyncedAssignmentStatus.Graded;
            var noteSubfolder = isDone ? "completed" : "ongoing";
            var noteFileName = FilenameSanitizer.SanitizeFileName(a.Title, ".md");
            var assignmentStem = FilenameSanitizer.SanitizeFileName(a.Title, "").TrimEnd('.');

            var instructionFiles = a
                .InstructionFiles.Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (lessons is not null && !string.IsNullOrWhiteSpace(assignmentStem))
            {
                foreach (var lesson in lessons)
                {
                    bool lessonMatches =
                        lesson.Title.Equals(a.Title, StringComparison.OrdinalIgnoreCase)
                        || lesson.SanitizedFolderName.Equals(
                            assignmentStem,
                            StringComparison.OrdinalIgnoreCase
                        );

                    foreach (var res in lesson.Resources)
                    {
                        var resStem = Path.GetFileNameWithoutExtension(res.RelativeTargetPath)
                            .TrimEnd('_');
                        if (
                            lessonMatches
                            || resStem.Equals(assignmentStem, StringComparison.OrdinalIgnoreCase)
                        )
                        {
                            if (
                                !instructionFiles.Contains(
                                    res.RelativeTargetPath,
                                    StringComparer.OrdinalIgnoreCase
                                )
                            )
                                instructionFiles.Add(res.RelativeTargetPath);
                        }
                    }
                }
            }

            var submissionFiles = a
                .SubmissionFiles.Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var resolvedTerm = BasinService.ResolveAcademicTerm(
                courseTermMap,
                a.TermHeader,
                a.Title,
                a.ParentModuleTitle,
                basinConfig.Terms,
                useStiNumericFallback: isStiProvider
            );
            if (resolvedTerm is null && syllabusPdfPath is not null)
            {
                resolvedTerm = SyllabusTermParser.InferTaskTermFromSyllabus(
                    syllabusPdfPath,
                    courseDir,
                    a.Title,
                    a.ParentModuleTitle
                );
            }

            var inferredTags = BasinService.InferAcademicTags(a.Title, resolvedTerm, a.Kind);

            var priority = ComputeInitialPriority(a);
            var task = new TaskItem
            {
                Id = $"lms-{a.ExternalId}",
                Title = a.Title,
                IsCompleted = isDone,
                DueDate = a.DueDate.HasValue
                    ? DateOnly.FromDateTime(a.DueDate.Value.LocalDateTime)
                    : null,
                DueTime = a.DueDate.HasValue
                    ? TimeOnly.FromDateTime(a.DueDate.Value.LocalDateTime)
                    : null,
                CompletedDate = isDone ? DateOnly.FromDateTime(DateTime.Today) : null,
                Priority = priority,
                CourseFolder = course.FolderName,
                CourseAlias = course.Alias,
                Term = resolvedTerm,
                Section = a.ParentModuleTitle,
                LmsKind = a.Kind,
                WeightPercent = a.WeightPercent,
                Labels = inferredTags,
                InstructionFiles = instructionFiles,
                SubmissionFiles = submissionFiles,
                LinkedFiles = [],
                NotePath = $"{noteSubfolder}/{noteFileName}",
                LmsId = a.ExternalId,
                LmsStatus = a.Status.ToString(),
                LmsScore = a.Score,
                LmsUrl = a.Url,
                SourceMarkdownPath = tasksPath,
            };
            incomingTasks.Add(task);

            DiffPollSnapshot(course, a, pollState, notifications);
        }

        var merged = MarkdownTaskCodec.MergeLmsTasks(existingTasks, incomingTasks);
        foreach (var t in merged)
        {
            BasinService.EnrichTaskMetadataAndFiles(basinRoot, course, t, courseTermMap);
        }
        await MarkdownTaskCodec.SaveDocumentAsync(tasksPath, merged, course.Title, ct: ct);
        return incomingTasks.Count;
    }

    private static void DiffPollSnapshot(
        BasinCourseEntry course,
        SyncedAssignment assignment,
        PollState pollState,
        List<AppNotification> notifications
    )
    {
        if (!pollState.AssignmentsByLmsId.TryGetValue(assignment.ExternalId, out var prev))
        {
            if (assignment.Status == SyncedAssignmentStatus.Pending)
            {
                notifications.Add(
                    new AppNotification(
                        NotificationKind.NewAssignment,
                        $"New Assignment: {assignment.Title}",
                        $"[{course.Title}] Due {assignment.DueDate?.ToString("MMM d, h:mm tt") ?? "TBD"}",
                        course.FolderName,
                        assignment.Url
                    )
                );
            }
        }
        else
        {
            if (prev.DueDate != assignment.DueDate && assignment.DueDate.HasValue)
            {
                notifications.Add(
                    new AppNotification(
                        NotificationKind.DueDateChanged,
                        $"Due Date Updated: {assignment.Title}",
                        $"[{course.Title}] New due date: {assignment.DueDate.Value:MMM d, h:mm tt}",
                        course.FolderName,
                        assignment.Url
                    )
                );
            }

            if (
                assignment.Status == SyncedAssignmentStatus.Graded
                && !string.Equals(
                    prev.Status,
                    SyncedAssignmentStatus.Graded.ToString(),
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                notifications.Add(
                    new AppNotification(
                        NotificationKind.GradePosted,
                        $"Grade Posted: {assignment.Title}",
                        $"[{course.Title}] Score: {assignment.Score ?? "Graded"}",
                        course.FolderName,
                        assignment.Url
                    )
                );
            }
        }

        if (assignment.Status == SyncedAssignmentStatus.Pending && assignment.DueDate.HasValue)
        {
            var remaining = assignment.DueDate.Value - DateTimeOffset.Now;
            if (remaining > TimeSpan.Zero && remaining <= TimeSpan.FromHours(24))
            {
                notifications.Add(
                    new AppNotification(
                        NotificationKind.DueSoon,
                        $"Due Soon: {assignment.Title}",
                        $"[{course.Title}] Due in {(int)remaining.TotalHours}h ({assignment.DueDate.Value:MMM d, h:mm tt})",
                        course.FolderName,
                        assignment.Url
                    )
                );
            }
        }

        pollState.AssignmentsByLmsId[assignment.ExternalId] = new PollAssignmentSnapshot
        {
            LmsId = assignment.ExternalId,
            CourseId = assignment.CourseId,
            Title = assignment.Title,
            Status = assignment.Status.ToString(),
            Score = assignment.Score,
            DueDate = assignment.DueDate,
            Url = assignment.Url,
        };
    }

    private static TaskPriority ComputeInitialPriority(SyncedAssignment a)
    {
        if (a.Status == SyncedAssignmentStatus.Missing)
            return TaskPriority.P1;

        if (a.DueDate.HasValue)
        {
            var days = (a.DueDate.Value.Date - DateTime.Today).TotalDays;
            if (days <= 2)
                return TaskPriority.P1;
            if (days <= 7)
                return TaskPriority.P2;
            if (days <= 14)
                return TaskPriority.P3;
        }

        return TaskPriority.P4;
    }
}
