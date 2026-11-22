using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ScholarFlow.Core.Models;

namespace ScholarFlow.Core.Markdown;

/// <summary>
/// Losslessly parses and serializes Obsidian-compatible Markdown task lists (tasks.md and inbox.md),
/// using clean, emoji-free Dataview inline fields ([due::], [done::], [p::], [diff::], [term::], [section::],
/// [attachment::], [submission::], [file::], [lms_id::]) and Term subheadings (### Prelims, ### Midterms, etc.).
/// </summary>
public static partial class MarkdownTaskCodec
{
    private static readonly SemaphoreSlim FileMutationLock = new(1, 1);
    private static readonly string[] OrderedTerms = ["Prelims", "Midterms", "Prefinals", "Finals"];

    [GeneratedRegex(@"^(\s*)- \[([ xX])\]\s+(.*)$", RegexOptions.Compiled)]
    private static partial Regex TaskLineRegex();

    [GeneratedRegex(@"^###\s+(.+)$", RegexOptions.Compiled)]
    private static partial Regex SubheadingRegex();

    [GeneratedRegex(@"📅\s*(\d{4}-\d{2}-\d{2})", RegexOptions.Compiled)]
    private static partial Regex LegacyDueDateRegex();

    [GeneratedRegex(@"⏰\s*(\d{2}:\d{2})", RegexOptions.Compiled)]
    private static partial Regex LegacyDueTimeRegex();

    [GeneratedRegex(@"✅\s*(\d{4}-\d{2}-\d{2})", RegexOptions.Compiled)]
    private static partial Regex LegacyCompletedDateRegex();

    [GeneratedRegex(@"\[([a-z_]+)::\s*([^\]]+)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex InlineFieldRegex();

    [GeneratedRegex(@"(?<!\S)#([A-Za-z0-9_\-]+)", RegexOptions.Compiled)]
    private static partial Regex CourseTagRegex();

    [GeneratedRegex(@"(?<!\S)@([A-Za-z0-9_\-]+)", RegexOptions.Compiled)]
    private static partial Regex LabelTagRegex();

    public static IReadOnlyList<TaskItem> Parse(
        string markdown,
        string sourcePath,
        string? defaultCourseFolder = null
    )
    {
        var tasks = new List<TaskItem>();
        if (string.IsNullOrEmpty(markdown))
            return tasks;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var parentStack = new List<(int Indent, TaskItem Task)>();
        string? currentHeadingTerm = null;
        string? currentHeadingSection = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i];
            var trimLine = rawLine.Trim();

            if (trimLine.StartsWith("## ", StringComparison.Ordinal))
            {
                parentStack.Clear();
                currentHeadingTerm = null;
                currentHeadingSection = null;
                continue;
            }

            var subheadingMatch = SubheadingRegex().Match(trimLine);
            if (subheadingMatch.Success)
            {
                parentStack.Clear();
                var headingText = subheadingMatch.Groups[1].Value.Trim();
                var normalizedTerm = TaskItem.NormalizeTerm(headingText);
                if (normalizedTerm is not null)
                {
                    currentHeadingTerm = normalizedTerm;
                    currentHeadingSection = null;
                }
                else
                {
                    currentHeadingSection = headingText;
                }
                continue;
            }

            var match = TaskLineRegex().Match(rawLine);
            if (!match.Success)
            {
                if (!string.IsNullOrWhiteSpace(rawLine))
                    parentStack.Clear();
                continue;
            }

            var indent = match.Groups[1].Value.Length;
            var isCompleted = match.Groups[2].Value is "x" or "X";
            var body = match.Groups[3].Value.Trim();

            var task = ParseTaskBody(body, isCompleted, sourcePath, i + 1, defaultCourseFolder);

            while (parentStack.Count > 0 && parentStack[^1].Indent >= indent)
                parentStack.RemoveAt(parentStack.Count - 1);

            if (indent >= 2 && parentStack.Count > 0)
            {
                var parent = parentStack[^1].Task;
                task.ParentTaskId = parent.Id;
                task.CourseFolder ??= parent.CourseFolder;
                task.CourseAlias ??= parent.CourseAlias;
                task.Term ??= parent.Term ?? currentHeadingTerm;
                task.Section ??= parent.Section ?? currentHeadingSection;
                if (
                    task.Id.StartsWith(
                        $"{Path.GetFileName(sourcePath)}:",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                    task.Id = $"{parent.Id}:sub-{parent.ChildTasks.Count + 1}";

                parent.ChildTasks.Add(task);
                parent.SubTasks.Add(
                    new TaskSubItem(task.Title, task.IsCompleted, task.CompletedDate)
                );
                parentStack.Add((indent, task));
                continue;
            }

            task.Term ??= currentHeadingTerm;
            task.Section ??= currentHeadingSection;
            tasks.Add(task);
            parentStack.Clear();
            parentStack.Add((indent, task));
        }

        foreach (var root in tasks)
            root.EnsureChildTasksSynced();

        return tasks;
    }

    public static async Task<IReadOnlyList<TaskItem>> LoadFileAsync(
        string filePath,
        string? defaultCourseFolder = null,
        CancellationToken ct = default
    )
    {
        if (!File.Exists(filePath))
            return [];

        var text = await File.ReadAllTextAsync(filePath, ct);
        return Parse(text, filePath, defaultCourseFolder);
    }

    public static string SerializeTask(
        TaskItem task,
        int indentLevel = 0,
        TaskItem? parentTask = null
    )
    {
        task.EnsureChildTasksSynced();

        var sb = new StringBuilder();
        if (indentLevel > 0)
            sb.Append(' ', indentLevel * 2);

        sb.Append(task.IsCompleted ? "- [x] " : "- [ ] ");
        sb.Append(task.Title.Trim());

        if (task.DueDate.HasValue)
            sb.Append($" [due:: {task.DueDate.Value:yyyy-MM-dd}]");

        if (task.DueTime.HasValue)
            sb.Append($" [time:: {task.DueTime.Value:HH:mm}]");

        if (task.IsCompleted && task.CompletedDate.HasValue)
            sb.Append($" [done:: {task.CompletedDate.Value:yyyy-MM-dd}]");

        if (task.Priority != TaskPriority.P4)
            sb.Append($" [p:: {(int)task.Priority}]");

        if (task.Difficulty is > 1 and <= 5)
            sb.Append($" [diff:: {task.Difficulty}]");

        var normTerm = TaskItem.NormalizeTerm(task.Term) ?? task.Term?.Trim();
        var parentTerm = TaskItem.NormalizeTerm(parentTask?.Term) ?? parentTask?.Term?.Trim();
        if (
            !string.IsNullOrWhiteSpace(normTerm)
            && (
                parentTask is null
                || !string.Equals(normTerm, parentTerm, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            sb.Append($" [term:: {normTerm}]");
        }

        if (
            !string.IsNullOrWhiteSpace(task.Section)
            && (
                parentTask is null
                || !string.Equals(
                    task.Section.Trim(),
                    parentTask.Section?.Trim(),
                    StringComparison.OrdinalIgnoreCase
                )
            )
        )
        {
            sb.Append($" [section:: {task.Section.Trim()}]");
        }

        if (!string.IsNullOrWhiteSpace(task.LmsKind))
            sb.Append($" [kind:: {task.LmsKind.Trim()}]");

        if (task.WeightPercent.HasValue && task.WeightPercent.Value > 0)
            sb.Append(
                $" [weight:: {task.WeightPercent.Value.ToString("0.#", CultureInfo.InvariantCulture)}]"
            );

        if (
            !string.IsNullOrWhiteSpace(task.CourseAlias)
            && (
                parentTask is null
                || !string.Equals(
                    task.CourseAlias.Trim(),
                    parentTask.CourseAlias?.Trim(),
                    StringComparison.OrdinalIgnoreCase
                )
            )
        )
        {
            sb.Append($" #{task.CourseAlias.Trim().TrimStart('#')}");
        }

        foreach (
            var label in task
                .Labels.Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
        )
            sb.Append($" @{label.Trim().TrimStart('@')}");

        var emittedAttachments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (
            var att in task
                .InstructionFiles.Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
        )
        {
            sb.Append($" [attachment:: {att}]");
            emittedAttachments.Add(att);
        }

        var emittedSubmissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (
            var subFile in task
                .SubmissionFiles.Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
        )
        {
            sb.Append($" [submission:: {subFile}]");
            emittedSubmissions.Add(subFile);
        }

        foreach (
            var file in task
                .LinkedFiles.Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
        )
        {
            if (!emittedAttachments.Contains(file) && !emittedSubmissions.Contains(file))
                sb.Append($" [file:: {file}]");
        }

        if (!string.IsNullOrWhiteSpace(task.NotePath))
            sb.Append($" [note:: {task.NotePath}]");

        if (!string.IsNullOrWhiteSpace(task.LmsId))
            sb.Append($" [lms_id:: {task.LmsId}]");

        if (!string.IsNullOrWhiteSpace(task.LmsStatus))
            sb.Append($" [lms_status:: {task.LmsStatus}]");

        if (!string.IsNullOrWhiteSpace(task.LmsScore))
            sb.Append($" [lms_score:: {task.LmsScore}]");

        if (!string.IsNullOrWhiteSpace(task.LmsUrl))
            sb.Append($" [lms_url:: {task.LmsUrl}]");

        foreach (var child in task.ChildTasks)
        {
            sb.AppendLine();
            sb.Append(SerializeTask(child, indentLevel + 1, task));
        }

        return sb.ToString();
    }

    public static string RenderDocument(IEnumerable<TaskItem> tasks, string? headerTitle = null)
    {
        var list = tasks.ToList();
        var ongoing = list.Where(t => !t.IsCompleted).ToList();
        var completed = list.Where(t => t.IsCompleted).ToList();

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(headerTitle))
        {
            sb.AppendLine($"# {headerTitle}");
            sb.AppendLine();
        }

        sb.AppendLine("## Ongoing");
        sb.AppendLine();
        AppendTasksGroupedByTerm(sb, ongoing);

        if (completed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Completed");
            sb.AppendLine();
            AppendTasksGroupedByTerm(sb, completed);
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    private static void AppendTasksGroupedByTerm(StringBuilder sb, List<TaskItem> items)
    {
        bool anyHasTerm = items.Any(t => !string.IsNullOrWhiteSpace(t.Term));
        if (!anyHasTerm)
        {
            foreach (var task in items)
                sb.AppendLine(SerializeTask(task));
            return;
        }

        var emittedTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var term in OrderedTerms)
        {
            var termTasks = items
                .Where(t =>
                    string.Equals(
                        TaskItem.NormalizeTerm(t.Term) ?? t.Term?.Trim(),
                        term,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                .ToList();
            if (termTasks.Count == 0)
                continue;

            emittedTerms.Add(term);
            sb.AppendLine($"### {term}");
            sb.AppendLine();
            foreach (var t in termTasks)
                sb.AppendLine(SerializeTask(t));
            sb.AppendLine();
        }

        var customTerms = items
            .Select(t => TaskItem.NormalizeTerm(t.Term) ?? t.Term?.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t) && !emittedTerms.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();

        foreach (var customTerm in customTerms)
        {
            var termTasks = items
                .Where(t =>
                    string.Equals(
                        TaskItem.NormalizeTerm(t.Term) ?? t.Term?.Trim(),
                        customTerm,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                .ToList();
            if (termTasks.Count == 0)
                continue;

            emittedTerms.Add(customTerm);
            sb.AppendLine($"### {customTerm}");
            sb.AppendLine();
            foreach (var t in termTasks)
                sb.AppendLine(SerializeTask(t));
            sb.AppendLine();
        }

        var uncategorized = items
            .Where(t => string.IsNullOrWhiteSpace(TaskItem.NormalizeTerm(t.Term) ?? t.Term?.Trim()))
            .ToList();
        if (uncategorized.Count > 0)
        {
            if (items.Count > uncategorized.Count)
            {
                sb.AppendLine("### General");
                sb.AppendLine();
            }
            foreach (var t in uncategorized)
                sb.AppendLine(SerializeTask(t));
        }
    }

    public static async Task SaveDocumentAsync(
        string filePath,
        IEnumerable<TaskItem> tasks,
        string? headerTitle = null,
        bool syncToDb = true,
        CancellationToken ct = default
    )
    {
        var list = tasks.ToList();
        foreach (var t in list)
            t.EnsureChildTasksSynced();

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        Services.BasinWatcher.SuppressGlobal(TimeSpan.FromMilliseconds(750));

        var content = RenderDocument(list, headerTitle);
        var temp = $"{filePath}.tmp.{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(temp, content, ct);
        File.Move(temp, filePath, overwrite: true);

        if (syncToDb)
        {
            var basinRoot = TryResolveBasinRootFromMarkdownPath(filePath);
            if (basinRoot is not null)
            {
                var courseFolder = list.FirstOrDefault()?.CourseFolder;
                var reparsedWithLineNumbers = Parse(content, filePath, courseFolder);
                await Database.ScholarFlowDatabase.SyncMarkdownFileToDbAsync(
                    basinRoot,
                    filePath,
                    courseFolder,
                    reparsedWithLineNumbers,
                    ct
                );
            }
        }
    }

    private static string? TryResolveBasinRootFromMarkdownPath(string filePath)
    {
        try
        {
            var full = Path.GetFullPath(filePath);
            var dir = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(dir))
                return null;

            var dirName = Path.GetFileName(dir);
            if (dirName.Equals(".scholarflow", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(dir);

            var parent = Path.GetDirectoryName(dir);
            if (
                !string.IsNullOrEmpty(parent)
                && Directory.Exists(Path.Combine(parent, ".scholarflow"))
            )
                return parent;
        }
        catch
        {
            // Ignore path resolution errors
        }

        return null;
    }

    public static async Task UpdateTaskInFileAsync(
        string basinRoot,
        TaskItem updatedTask,
        CancellationToken ct = default
    )
    {
        await FileMutationLock.WaitAsync(ct);
        try
        {
            updatedTask.EnsureChildTasksSynced();

            var sourcePath =
                !string.IsNullOrWhiteSpace(updatedTask.SourceMarkdownPath)
                    ? updatedTask.SourceMarkdownPath
                : string.IsNullOrWhiteSpace(updatedTask.CourseFolder)
                    ? Path.Combine(basinRoot, ".scholarflow", "inbox.md")
                : Path.Combine(basinRoot, updatedTask.CourseFolder, "tasks.md");

            var tasks = (await LoadFileAsync(sourcePath, updatedTask.CourseFolder, ct)).ToList();
            if (!TryReplaceTaskRecursive(tasks, updatedTask))
            {
                tasks.Add(updatedTask);
            }

            var header = string.IsNullOrWhiteSpace(updatedTask.CourseFolder)
                ? "Inbox"
                : updatedTask.CourseFolder.Replace('_', ' ');
            await SaveDocumentAsync(sourcePath, tasks, header, true, ct);
        }
        finally
        {
            FileMutationLock.Release();
        }
    }

    private static bool TryReplaceTaskRecursive(List<TaskItem> nodes, TaskItem updatedTask)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            if (IsMatchingTaskIdentity(nodes[i], updatedTask))
            {
                nodes[i] = updatedTask;
                return true;
            }

            if (
                nodes[i].ChildTasks.Count > 0
                && TryReplaceTaskRecursive(nodes[i].ChildTasks, updatedTask)
            )
            {
                nodes[i].EnsureChildTasksSynced();
                return true;
            }
        }

        // Fallback for non-LMS tasks whose in-memory/SQLite Guid Id differs from file-parsed line Id
        if (
            string.IsNullOrWhiteSpace(updatedTask.LmsId)
            && string.IsNullOrWhiteSpace(updatedTask.ParentTaskId)
        )
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                if (
                    string.IsNullOrWhiteSpace(nodes[i].LmsId)
                    && string.Equals(
                        nodes[i].Title.Trim(),
                        updatedTask.Title.Trim(),
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    nodes[i] = updatedTask;
                    return true;
                }
            }
        }

        return false;
    }

    public static async Task DeleteTaskFromFileAsync(
        string basinRoot,
        TaskItem targetTask,
        CancellationToken ct = default
    )
    {
        var sourcePath =
            !string.IsNullOrWhiteSpace(targetTask.SourceMarkdownPath)
                ? targetTask.SourceMarkdownPath
            : string.IsNullOrWhiteSpace(targetTask.CourseFolder)
                ? Path.Combine(basinRoot, ".scholarflow", "inbox.md")
            : Path.Combine(basinRoot, targetTask.CourseFolder, "tasks.md");

        var tasks = (await LoadFileAsync(sourcePath, targetTask.CourseFolder, ct)).ToList();
        RemoveTaskRecursive(tasks, targetTask);

        var header = string.IsNullOrWhiteSpace(targetTask.CourseFolder)
            ? "Inbox"
            : targetTask.CourseFolder.Replace('_', ' ');
        await SaveDocumentAsync(sourcePath, tasks, header, true, ct);
    }

    private static void RemoveTaskRecursive(List<TaskItem> nodes, TaskItem targetTask)
    {
        nodes.RemoveAll(t => IsMatchingTaskIdentity(t, targetTask));
        foreach (var node in nodes)
        {
            if (node.ChildTasks.Count > 0)
            {
                RemoveTaskRecursive(node.ChildTasks, targetTask);
                node.EnsureChildTasksSynced();
            }
        }
    }

    internal static bool IsMatchingTaskIdentity(TaskItem candidate, TaskItem target)
    {
        if (
            !string.IsNullOrWhiteSpace(target.LmsId)
            && string.Equals(candidate.LmsId, target.LmsId, StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        if (string.Equals(candidate.Id, target.Id, StringComparison.OrdinalIgnoreCase))
            return true;

        // Also match when one ID is prefixed with CourseFolder: or inbox: (from SQLite) and the other is file-local
        var prefix = string.IsNullOrWhiteSpace(target.CourseFolder) ? "inbox" : target.CourseFolder;
        if (
            string.Equals($"{prefix}:{candidate.Id}", target.Id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                candidate.Id,
                $"{prefix}:{target.Id}",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return true;
        }

        if (candidate.LineNumber == target.LineNumber && target.LineNumber > 0)
            return true;

        if (
            !string.IsNullOrWhiteSpace(target.ParentTaskId)
            && string.Equals(candidate.Title, target.Title, StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Idempotently merges incoming LMS tasks into an existing tasks list by [lms_id:: ...],
    /// preserving user-added subtasks, difficulty, custom labels, and manual [x] completion state.
    /// </summary>
    public static List<TaskItem> MergeLmsTasks(
        IEnumerable<TaskItem> existingTasks,
        IEnumerable<TaskItem> incomingLmsTasks
    )
    {
        var list = existingTasks.ToList();
        var byLmsId = list.Where(t => !string.IsNullOrWhiteSpace(t.LmsId))
            .ToDictionary(t => t.LmsId!, StringComparer.OrdinalIgnoreCase);

        foreach (var incoming in incomingLmsTasks)
        {
            if (
                !string.IsNullOrWhiteSpace(incoming.LmsId)
                && byLmsId.TryGetValue(incoming.LmsId, out var existing)
            )
            {
                existing.Title = incoming.Title;
                existing.DueDate = incoming.DueDate ?? existing.DueDate;
                existing.DueTime = incoming.DueTime ?? existing.DueTime;
                existing.Term = incoming.Term ?? existing.Term;
                existing.Section = incoming.Section ?? existing.Section;
                existing.LmsKind = incoming.LmsKind ?? existing.LmsKind;
                existing.WeightPercent = incoming.WeightPercent ?? existing.WeightPercent;
                existing.LmsStatus = incoming.LmsStatus;
                existing.LmsScore = incoming.LmsScore;
                existing.LmsUrl = incoming.LmsUrl ?? existing.LmsUrl;
                existing.NotePath = incoming.NotePath ?? existing.NotePath;
                existing.CourseAlias ??= incoming.CourseAlias;
                existing.CourseFolder ??= incoming.CourseFolder;

                foreach (var lbl in incoming.Labels)
                {
                    if (!existing.Labels.Contains(lbl, StringComparer.OrdinalIgnoreCase))
                        existing.Labels.Add(lbl);
                }

                foreach (var att in incoming.InstructionFiles)
                {
                    if (!existing.InstructionFiles.Contains(att, StringComparer.OrdinalIgnoreCase))
                        existing.InstructionFiles.Add(att);
                }

                foreach (var sub in incoming.SubmissionFiles)
                {
                    if (!existing.SubmissionFiles.Contains(sub, StringComparer.OrdinalIgnoreCase))
                        existing.SubmissionFiles.Add(sub);
                }

                foreach (var f in incoming.LinkedFiles)
                {
                    if (!existing.LinkedFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
                        existing.LinkedFiles.Add(f);
                }

                if (incoming.IsCompleted && !existing.IsCompleted)
                {
                    existing.IsCompleted = true;
                    existing.CompletedDate ??=
                        incoming.CompletedDate ?? DateOnly.FromDateTime(DateTime.Today);
                }
            }
            else
            {
                list.Add(incoming);
                if (!string.IsNullOrWhiteSpace(incoming.LmsId))
                    byLmsId[incoming.LmsId] = incoming;
            }
        }

        return list;
    }

    private static TaskItem ParseTaskBody(
        string body,
        bool isCompleted,
        string sourcePath,
        int lineNumber,
        string? defaultCourseFolder
    )
    {
        var working = body;

        DateOnly? dueDate = null;
        var dueMatch = LegacyDueDateRegex().Match(working);
        if (
            dueMatch.Success
            && DateOnly.TryParseExact(
                dueMatch.Groups[1].Value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dd
            )
        )
        {
            dueDate = dd;
            working = working.Remove(dueMatch.Index, dueMatch.Length);
        }

        TimeOnly? dueTime = null;
        var timeMatch = LegacyDueTimeRegex().Match(working);
        if (
            timeMatch.Success
            && TimeOnly.TryParseExact(
                timeMatch.Groups[1].Value,
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dt
            )
        )
        {
            dueTime = dt;
            working = working.Remove(timeMatch.Index, timeMatch.Length);
        }

        var completedDate = ExtractCompletedDate(working, out working);

        var priority = TaskPriority.P4;
        if (working.Contains("🔺"))
        {
            priority = TaskPriority.P1;
            working = working.Replace("🔺", "");
        }
        else if (working.Contains('⏫'))
        {
            priority = TaskPriority.P2;
            working = working.Replace("⏫", "");
        }
        else if (working.Contains("🔼"))
        {
            priority = TaskPriority.P3;
            working = working.Replace("🔼", "");
        }
        else if (working.Contains("🔽"))
        {
            priority = TaskPriority.P4;
            working = working.Replace("🔽", "");
        }

        int difficulty = 1;
        string? term = null,
            section = null,
            lmsKind = null;
        double? weightPercent = null;
        string? lmsId = null,
            lmsStatus = null,
            lmsScore = null,
            lmsUrl = null,
            notePath = null;
        var instructionFiles = new List<string>();
        var submissionFiles = new List<string>();
        var linkedFiles = new List<string>();

        working = InlineFieldRegex()
            .Replace(
                working,
                m =>
                {
                    var key = m.Groups[1].Value.ToLowerInvariant();
                    var val = m.Groups[2].Value.Trim();
                    switch (key)
                    {
                        case "due"
                            when DateOnly.TryParseExact(
                                val,
                                "yyyy-MM-dd",
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.None,
                                out var parsedDue
                            ):
                            dueDate = parsedDue;
                            break;
                        case "time"
                            when TimeOnly.TryParseExact(
                                val,
                                "HH:mm",
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.None,
                                out var parsedTime
                            ):
                            dueTime = parsedTime;
                            break;
                        case "done"
                            when DateOnly.TryParseExact(
                                val,
                                "yyyy-MM-dd",
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.None,
                                out var parsedDone
                            ):
                            completedDate = parsedDone;
                            break;
                        case "p"
                        or "priority"
                            when int.TryParse(val.TrimStart('p', 'P'), out var pVal)
                                && pVal is >= 1 and <= 4:
                            priority = (TaskPriority)pVal;
                            break;
                        case "diff" when int.TryParse(val, out var d):
                            difficulty = Math.Clamp(d, 1, 5);
                            break;
                        case "term":
                            term = TaskItem.NormalizeTerm(val) ?? val;
                            break;
                        case "section":
                            section = val;
                            break;
                        case "kind":
                            lmsKind = val;
                            break;
                        case "weight"
                            when double.TryParse(
                                val.TrimEnd('%'),
                                NumberStyles.Any,
                                CultureInfo.InvariantCulture,
                                out var w
                            ):
                            weightPercent = w;
                            break;
                        case "attachment":
                            instructionFiles.Add(val);
                            break;
                        case "submission":
                            submissionFiles.Add(val);
                            break;
                        case "file":
                            linkedFiles.Add(val);
                            break;
                        case "note":
                            notePath = val;
                            break;
                        case "lms_id":
                            lmsId = val;
                            break;
                        case "lms_status":
                            lmsStatus = val;
                            break;
                        case "lms_score":
                            lmsScore = val;
                            break;
                        case "lms_url":
                            lmsUrl = val;
                            break;
                    }
                    return string.Empty;
                }
            );

        string? courseAlias = null;
        working = CourseTagRegex()
            .Replace(
                working,
                m =>
                {
                    courseAlias ??= m.Groups[1].Value;
                    return string.Empty;
                }
            );

        var labels = new List<string>();
        working = LabelTagRegex()
            .Replace(
                working,
                m =>
                {
                    var lbl = m.Groups[1].Value;
                    labels.Add(lbl);
                    term ??= TaskItem.NormalizeTerm(lbl);
                    return string.Empty;
                }
            );

        var cleanTitle = string.Join(
                ' ',
                working.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            )
            .Trim();

        return new TaskItem
        {
            Id = !string.IsNullOrWhiteSpace(lmsId)
                ? $"lms-{lmsId}"
                : $"{Path.GetFileName(sourcePath)}:{lineNumber}",
            Title = cleanTitle,
            IsCompleted = isCompleted,
            DueDate = dueDate,
            DueTime = dueTime,
            CompletedDate = completedDate,
            Priority = priority,
            Difficulty = difficulty,
            CourseFolder = defaultCourseFolder,
            CourseAlias = courseAlias,
            Term = term,
            Section = section,
            LmsKind = lmsKind,
            WeightPercent = weightPercent,
            Labels = labels,
            InstructionFiles = instructionFiles,
            SubmissionFiles = submissionFiles,
            LinkedFiles = linkedFiles,
            NotePath = notePath,
            LmsId = lmsId,
            LmsStatus = lmsStatus,
            LmsScore = lmsScore,
            LmsUrl = lmsUrl,
            SourceMarkdownPath = sourcePath,
            LineNumber = lineNumber,
        };
    }

    private static DateOnly? ExtractCompletedDate(string text, out string cleaned)
    {
        var match = LegacyCompletedDateRegex().Match(text);
        if (
            match.Success
            && DateOnly.TryParseExact(
                match.Groups[1].Value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var cd
            )
        )
        {
            cleaned = text.Remove(match.Index, match.Length);
            return cd;
        }

        DateOnly? inlineDone = null;
        cleaned = InlineFieldRegex()
            .Replace(
                text,
                m =>
                {
                    if (
                        m.Groups[1].Value.Equals("done", StringComparison.OrdinalIgnoreCase)
                        && DateOnly.TryParseExact(
                            m.Groups[2].Value.Trim(),
                            "yyyy-MM-dd",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out var parsedDone
                        )
                    )
                    {
                        inlineDone = parsedDone;
                        return string.Empty;
                    }
                    return m.Value;
                }
            );

        return inlineDone;
    }
}
