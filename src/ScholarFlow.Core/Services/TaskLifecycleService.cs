using ScholarFlow.Core.Markdown;
using ScholarFlow.Core.Models;

namespace ScholarFlow.Core.Services;

/// <summary>
/// Handles task completion, reopening, and registering untracked ongoing/ files into tasks.md,
/// safely moving linked files between ongoing/ and completed/ folders.
/// </summary>
public sealed class TaskLifecycleService
{
    public async Task SetCompletedAsync(
        string basinRoot,
        TaskItem task,
        bool isCompleted,
        DateOnly? completedDateOverride = null,
        CancellationToken ct = default
    )
    {
        var sourcePath =
            !string.IsNullOrWhiteSpace(task.SourceMarkdownPath) ? task.SourceMarkdownPath
            : string.IsNullOrWhiteSpace(task.CourseFolder) ? BasinService.GetInboxPath(basinRoot)
            : BasinService.GetCourseTasksPath(basinRoot, task.CourseFolder);

        var tasks = (
            await MarkdownTaskCodec.LoadFileAsync(sourcePath, task.CourseFolder, ct)
        ).ToList();
        var target = FindMatchingTask(tasks, task);
        if (target is null)
            return;

        target.IsCompleted = isCompleted;
        target.CompletedDate = isCompleted
            ? (completedDateOverride ?? DateOnly.FromDateTime(DateTime.Today))
            : null;

        if (!string.IsNullOrWhiteSpace(target.CourseFolder))
        {
            var courseRoot = Path.Combine(basinRoot, target.CourseFolder);
            var fromSub = isCompleted ? "ongoing" : "completed";
            var toSub = isCompleted ? "completed" : "ongoing";

            async Task MoveFileListAsync(List<string> fileList)
            {
                for (int i = 0; i < fileList.Count; i++)
                {
                    var rel = fileList[i].Replace('\\', '/');
                    if (rel.StartsWith($"{fromSub}/", StringComparison.OrdinalIgnoreCase))
                    {
                        var movedRel = await TryMoveRelativeFileAsync(courseRoot, rel, toSub);
                        if (movedRel is not null)
                            fileList[i] = movedRel;
                    }
                }
            }

            await MoveFileListAsync(target.LinkedFiles);
            await MoveFileListAsync(target.InstructionFiles);
            await MoveFileListAsync(target.SubmissionFiles);

            if (
                !string.IsNullOrWhiteSpace(target.NotePath)
                && target
                    .NotePath.Replace('\\', '/')
                    .StartsWith($"{fromSub}/", StringComparison.OrdinalIgnoreCase)
            )
            {
                var movedNote = await TryMoveRelativeFileAsync(courseRoot, target.NotePath, toSub);
                if (movedNote is not null)
                    target.NotePath = movedNote;
            }
        }

        foreach (var root in tasks)
            root.EnsureChildTasksSynced();

        var header = string.IsNullOrWhiteSpace(task.CourseFolder)
            ? "Inbox"
            : task.CourseFolder.Replace('_', ' ');
        await MarkdownTaskCodec.SaveDocumentAsync(sourcePath, tasks, header, ct: ct);
    }

    public async Task<TaskItem> RegisterOngoingFileAsync(
        string basinRoot,
        UnregisteredOngoingFile file,
        DateOnly? dueDate = null,
        TaskPriority priority = TaskPriority.P3,
        int difficulty = 2,
        CancellationToken ct = default
    )
    {
        var tasksPath = BasinService.GetCourseTasksPath(basinRoot, file.CourseFolder);
        var tasks = (
            await MarkdownTaskCodec.LoadFileAsync(tasksPath, file.CourseFolder, ct)
        ).ToList();

        var title = Path.GetFileNameWithoutExtension(file.FileName).Replace('_', ' ').Trim();
        var newTask = new TaskItem
        {
            Title = title,
            DueDate = dueDate,
            Priority = priority,
            Difficulty = Math.Clamp(difficulty, 1, 5),
            CourseFolder = file.CourseFolder,
            LinkedFiles = [file.RelativePath],
            SourceMarkdownPath = tasksPath,
        };

        tasks.Add(newTask);
        await MarkdownTaskCodec.SaveDocumentAsync(tasksPath, tasks, file.CourseTitle, ct: ct);
        return newTask;
    }

    private static TaskItem? FindMatchingTask(List<TaskItem> tasks, TaskItem target)
    {
        foreach (var t in tasks)
        {
            if (MarkdownTaskCodec.IsMatchingTaskIdentity(t, target))
                return t;
            var childMatch = FindMatchingTask(t.ChildTasks, target);
            if (childMatch is not null)
                return childMatch;
        }

        return tasks.FirstOrDefault(t =>
            t.LineNumber == target.LineNumber
            && t.Title.Equals(target.Title, StringComparison.OrdinalIgnoreCase)
        )
            ?? tasks.FirstOrDefault(t =>
                t.Title.Equals(target.Title, StringComparison.OrdinalIgnoreCase)
            );
    }

    private static async Task<string?> TryMoveRelativeFileAsync(
        string courseRoot,
        string relPath,
        string destSubfolder
    )
    {
        var normalized = relPath.Replace('\\', '/');
        var srcFull = Path.Combine(courseRoot, normalized);
        if (!File.Exists(srcFull))
            return null;

        var destDir = Path.Combine(courseRoot, destSubfolder);
        Directory.CreateDirectory(destDir);

        var fileName = Path.GetFileName(srcFull);
        var destFull = Path.Combine(destDir, fileName);
        var destRel = $"{destSubfolder}/{fileName}";

        if (Path.GetFullPath(srcFull).Equals(Path.GetFullPath(destFull), StringComparison.Ordinal))
            return destRel;

        try
        {
            File.Move(srcFull, destFull, overwrite: true);
        }
        catch (IOException)
        {
            // Fallback across mounts/filesystems
            await using (var srcStream = File.OpenRead(srcFull))
            await using (var dstStream = File.Create(destFull))
            {
                await srcStream.CopyToAsync(dstStream);
            }
            File.Delete(srcFull);
        }

        return destRel;
    }
}
