using System.Globalization;
using Microsoft.Data.Sqlite;
using ScholarFlow.Core.Markdown;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Services;

namespace ScholarFlow.Core.Database;

public static partial class ScholarFlowDatabase
{
    public static string GetLogicalTaskScopeKey(string? courseFolder) =>
        string.IsNullOrWhiteSpace(courseFolder) ? "db:inbox" : $"db:course:{courseFolder.Trim()}";

    public static async Task<IReadOnlyList<TaskItem>> SyncAndLoadAllTasksAsync(
        string basinRoot,
        IReadOnlyList<BasinCourseEntry> courses,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(basinRoot) || !Directory.Exists(basinRoot))
            return [];

        await DbLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
            await conn.OpenAsync(ct);
            await EnsureSchemaOnConnectionAsync(conn, ct);

            var indexedTicks = await LoadIndexedFileTicksAsync(conn, ct);
            var activeSourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Inbox (SQLite-canonical; one-time automatic import from legacy .scholarflow/inbox.md if present)
            var inboxScopeKey = GetLogicalTaskScopeKey(null);
            activeSourcePaths.Add(inboxScopeKey);
            var legacyInboxPath = Path.GetFullPath(BasinService.GetInboxPath(basinRoot));
            await MigrateLegacyScopeIfNeededAsync(
                conn,
                basinRoot,
                inboxScopeKey,
                legacyInboxPath,
                course: null,
                indexedTicks,
                ct
            );

            // 2. Active Courses (SQLite-canonical; one-time automatic import from legacy <Course>/tasks.md if present)
            foreach (var course in courses.Where(c => !c.IsArchived))
            {
                var courseScopeKey = GetLogicalTaskScopeKey(course.FolderName);
                activeSourcePaths.Add(courseScopeKey);

                var legacyTasksPath = Path.GetFullPath(
                    BasinService.GetCourseTasksPath(basinRoot, course.FolderName)
                );
                await MigrateLegacyScopeIfNeededAsync(
                    conn,
                    basinRoot,
                    courseScopeKey,
                    legacyTasksPath,
                    course,
                    indexedTicks,
                    ct
                );
            }

            // 3. Load canonical task hierarchy directly from SQLite
            return await LoadAllTasksFromConnectionAsync(conn, activeSourcePaths, ct);
        }
        finally
        {
            DbLock.Release();
        }
    }

    private static async Task MigrateLegacyScopeIfNeededAsync(
        SqliteConnection conn,
        string basinRoot,
        string logicalScopeKey,
        string legacyMdPath,
        BasinCourseEntry? course,
        Dictionary<string, long> indexedTicks,
        CancellationToken ct
    )
    {
        var migrationMarker = $"migrated:{legacyMdPath}";
        if (indexedTicks.ContainsKey(migrationMarker))
            return;

        var existingUnderLegacyPath = await LoadAllTasksFromConnectionAsync(
            conn,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { legacyMdPath },
            ct
        );
        var existingUnderLogical = await LoadAllTasksFromConnectionAsync(
            conn,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { logicalScopeKey },
            ct
        );

        var combined = new List<TaskItem>(existingUnderLogical);

        void MergeUnique(IEnumerable<TaskItem> incoming)
        {
            foreach (var item in incoming)
            {
                if (!combined.Any(e => MarkdownTaskCodec.IsMatchingTaskIdentity(e, item)))
                    combined.Add(item);
            }
        }

        if (File.Exists(legacyMdPath))
        {
            var fromDisk = (
                await MarkdownTaskCodec.LoadFileAsync(legacyMdPath, course?.FolderName, ct)
            ).ToList();
            if (fromDisk.Count > 0)
            {
                if (course is not null)
                {
                    var termMap = BasinService.LoadCourseTermMap(basinRoot, course.FolderName);
                    foreach (var t in fromDisk)
                    {
                        t.CourseFolder ??= course.FolderName;
                        t.CourseAlias ??= course.Alias;
                        BasinService.EnrichTaskMetadataAndFiles(basinRoot, course, t, termMap);
                    }
                }
                MergeUnique(fromDisk);
            }
        }
        else if (existingUnderLegacyPath.Count > 0)
        {
            MergeUnique(existingUnderLegacyPath);
        }

        if (combined.Count > 0)
        {
            await ReplaceFileTasksInConnectionAsync(
                conn,
                basinRoot,
                logicalScopeKey,
                course?.FolderName,
                combined,
                DateTime.UtcNow.Ticks,
                ct
            );
        }

        // Remove superseded physical-path rows if any existed
        if (existingUnderLegacyPath.Count > 0)
        {
            await DeleteScopeRowsInConnectionAsync(conn, legacyMdPath, ct);
        }

        // Record one-time migration marker so legacy tasks.md/inbox.md is never re-imported or overwritten
        await using var markCmd = conn.CreateCommand();
        markCmd.CommandText = """
            INSERT INTO indexed_files (path, course_folder, last_write_ticks)
            VALUES ($path, $course, $ticks)
            ON CONFLICT(path) DO UPDATE SET last_write_ticks = excluded.last_write_ticks;
            """;
        markCmd.Parameters.AddWithValue("$path", migrationMarker);
        markCmd.Parameters.AddWithValue("$course", (object?)course?.FolderName ?? DBNull.Value);
        markCmd.Parameters.AddWithValue("$ticks", DateTime.UtcNow.Ticks);
        await markCmd.ExecuteNonQueryAsync(ct);
        indexedTicks[migrationMarker] = DateTime.UtcNow.Ticks;
    }

    private static async Task DeleteScopeRowsInConnectionAsync(
        SqliteConnection conn,
        string sourcePath,
        CancellationToken ct
    )
    {
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        foreach (
            var sql in new[]
            {
                "DELETE FROM task_labels WHERE task_id IN (SELECT id FROM tasks WHERE source_path = $path);",
                "DELETE FROM task_files WHERE task_id IN (SELECT id FROM tasks WHERE source_path = $path);",
                "DELETE FROM tasks_fts WHERE task_id IN (SELECT id FROM tasks WHERE source_path = $path);",
                "DELETE FROM tasks WHERE source_path = $path;",
                "DELETE FROM indexed_files WHERE path = $path;",
            }
        )
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$path", sourcePath);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Loads canonical root tasks (with nested children) for Inbox (when <paramref name="courseFolder"/> is null)
    /// or a specific course directly from SQLite.
    /// </summary>
    public static async Task<List<TaskItem>> LoadScopeTasksAsync(
        string basinRoot,
        string? courseFolder,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(basinRoot) || !Directory.Exists(basinRoot))
            return [];

        var scopeKey = GetLogicalTaskScopeKey(courseFolder);
        await DbLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
            await conn.OpenAsync(ct);
            await EnsureSchemaOnConnectionAsync(conn, ct);
            return
            [
                .. await LoadAllTasksFromConnectionAsync(
                    conn,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { scopeKey },
                    ct
                ),
            ];
        }
        finally
        {
            DbLock.Release();
        }
    }

    /// <summary>
    /// Atomically replaces all canonical tasks for Inbox or a specific course directly in SQLite.
    /// </summary>
    public static async Task SaveScopeTasksAsync(
        string basinRoot,
        string? courseFolder,
        IReadOnlyList<TaskItem> rootTasks,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(basinRoot))
            return;

        Directory.CreateDirectory(BasinService.GetMetaDir(basinRoot));
        var scopeKey = GetLogicalTaskScopeKey(courseFolder);

        await DbLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
            await conn.OpenAsync(ct);
            await EnsureSchemaOnConnectionAsync(conn, ct);
            await ReplaceFileTasksInConnectionAsync(
                conn,
                basinRoot,
                scopeKey,
                courseFolder,
                rootTasks,
                DateTime.UtcNow.Ticks,
                ct
            );
        }
        finally
        {
            DbLock.Release();
        }
    }

    /// <summary>
    /// Inserts or updates a single task (or nested subtask) directly in SQLite without touching any Markdown files.
    /// </summary>
    public static async Task UpsertTaskAsync(
        string basinRoot,
        TaskItem updatedTask,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(basinRoot) || updatedTask is null)
            return;

        updatedTask.EnsureChildTasksSynced();
        var tasks = await LoadScopeTasksAsync(basinRoot, updatedTask.CourseFolder, ct);

        bool replaced = ReplaceMatchingTaskInTree(tasks, updatedTask);
        if (!replaced)
        {
            tasks.Add(updatedTask);
        }

        await SaveScopeTasksAsync(basinRoot, updatedTask.CourseFolder, tasks, ct);
    }

    /// <summary>
    /// Deletes a single task (or nested subtask) directly from SQLite.
    /// </summary>
    public static async Task DeleteTaskAsync(
        string basinRoot,
        TaskItem targetTask,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(basinRoot) || targetTask is null)
            return;

        var tasks = await LoadScopeTasksAsync(basinRoot, targetTask.CourseFolder, ct);
        if (RemoveMatchingTaskInTree(tasks, targetTask))
        {
            await SaveScopeTasksAsync(basinRoot, targetTask.CourseFolder, tasks, ct);
        }
    }

    /// <summary>
    /// Exports a clean one-way Markdown snapshot of all canonical SQLite tasks (Inbox + active courses).
    /// </summary>
    public static async Task ExportAllTasksToMarkdownAsync(
        string basinRoot,
        IReadOnlyList<BasinCourseEntry> courses,
        string outputFilePath,
        CancellationToken ct = default
    )
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# ScholarFlow Tasks Snapshot");
        sb.AppendLine();
        sb.AppendLine($"_Exported {DateTime.Now:yyyy-MM-dd HH:mm}_");
        sb.AppendLine();

        var inbox = await LoadScopeTasksAsync(basinRoot, null, ct);
        if (inbox.Count > 0)
        {
            sb.AppendLine(MarkdownTaskCodec.RenderDocument(inbox, "Inbox"));
            sb.AppendLine();
        }

        foreach (var c in courses.Where(c => !c.IsArchived))
        {
            var cTasks = await LoadScopeTasksAsync(basinRoot, c.FolderName, ct);
            if (cTasks.Count == 0)
                continue;
            sb.AppendLine(MarkdownTaskCodec.RenderDocument(cTasks, c.Title));
            sb.AppendLine();
        }

        var dir = Path.GetDirectoryName(outputFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(outputFilePath, sb.ToString(), ct);
    }

    private static bool ReplaceMatchingTaskInTree(List<TaskItem> nodes, TaskItem updated)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            if (MarkdownTaskCodec.IsMatchingTaskIdentity(nodes[i], updated))
            {
                nodes[i] = updated;
                return true;
            }
            if (nodes[i].ChildTasks.Count > 0 && ReplaceMatchingTaskInTree(nodes[i].ChildTasks, updated))
            {
                nodes[i].EnsureChildTasksSynced();
                return true;
            }
        }
        return false;
    }

    private static bool RemoveMatchingTaskInTree(List<TaskItem> nodes, TaskItem target)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            if (MarkdownTaskCodec.IsMatchingTaskIdentity(nodes[i], target))
            {
                nodes.RemoveAt(i);
                return true;
            }
            if (nodes[i].ChildTasks.Count > 0 && RemoveMatchingTaskInTree(nodes[i].ChildTasks, target))
            {
                nodes[i].EnsureChildTasksSynced();
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Updates SQLite under the canonical logical scope key whenever a legacy Markdown task file is synced.
    /// </summary>
    public static async Task SyncMarkdownFileToDbAsync(
        string basinRoot,
        string filePath,
        string? courseFolder,
        IReadOnlyList<TaskItem> tasks,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(basinRoot) || !Directory.Exists(basinRoot))
            return;

        var scopeKey = GetLogicalTaskScopeKey(courseFolder);
        var fullPath = Path.GetFullPath(filePath);
        var ticks = File.Exists(fullPath)
            ? File.GetLastWriteTimeUtc(fullPath).Ticks
            : DateTime.UtcNow.Ticks;

        await DbLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
            await conn.OpenAsync(ct);
            await EnsureSchemaOnConnectionAsync(conn, ct);
            await ReplaceFileTasksInConnectionAsync(
                conn,
                basinRoot,
                scopeKey,
                courseFolder,
                tasks,
                ticks,
                ct
            );
        }
        finally
        {
            DbLock.Release();
        }
    }

    /// <summary>
    /// Performs a ranked SQLite FTS5 full-text search across task titles, subtasks, course aliases, terms, sections, tags, and Markdown notes.
    /// </summary>
    public static IReadOnlyList<string> SearchTaskIdsFts(
        string basinRoot,
        string rawQuery,
        int limit = 25
    )
    {
        if (string.IsNullOrWhiteSpace(basinRoot) || string.IsNullOrWhiteSpace(rawQuery))
            return [];

        var dbPath = GetDatabasePath(basinRoot);
        if (!File.Exists(dbPath))
            return [];

        var ftsQuery = BuildFts5PrefixQuery(rawQuery);
        if (string.IsNullOrWhiteSpace(ftsQuery))
            return [];

        try
        {
            using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT task_id
                FROM tasks_fts
                WHERE tasks_fts MATCH $q
                ORDER BY bm25(tasks_fts, 0.0, 10.0, 5.0, 4.0, 3.0, 5.0, 1.5)
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$q", ftsQuery);
            cmd.Parameters.AddWithValue("$limit", limit);

            var ids = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }
            return ids;
        }
        catch
        {
            return [];
        }
    }

    private static string BuildFts5PrefixQuery(string raw)
    {
        var tokens = raw.Split(
                [' ', '\t', '\r', '\n', '#', '@', '/'],
                StringSplitOptions.RemoveEmptyEntries
            )
            .Select(t => new string([
                .. t.Where(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-'),
            ]))
            .Where(t => t.Length > 0)
            .ToList();

        if (tokens.Count == 0)
            return string.Empty;

        return string.Join(" AND ", tokens.Select(t => $"\"{t}\"*"));
    }

    private static async Task<Dictionary<string, long>> LoadIndexedFileTicksAsync(
        SqliteConnection conn,
        CancellationToken ct
    )
    {
        var dict = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path, last_write_ticks FROM indexed_files;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            dict[reader.GetString(0)] = reader.GetInt64(1);
        }
        return dict;
    }

    private static async Task ReplaceFileTasksInConnectionAsync(
        SqliteConnection conn,
        string basinRoot,
        string fullSourcePath,
        string? courseFolder,
        IReadOnlyList<TaskItem> rootTasks,
        long lastWriteTicks,
        CancellationToken ct
    )
    {
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using (var delLabels = conn.CreateCommand())
        {
            delLabels.Transaction = tx;
            delLabels.CommandText =
                "DELETE FROM task_labels WHERE task_id IN (SELECT id FROM tasks WHERE source_path = $path);";
            delLabels.Parameters.AddWithValue("$path", fullSourcePath);
            await delLabels.ExecuteNonQueryAsync(ct);
        }

        await using (var delFiles = conn.CreateCommand())
        {
            delFiles.Transaction = tx;
            delFiles.CommandText =
                "DELETE FROM task_files WHERE task_id IN (SELECT id FROM tasks WHERE source_path = $path);";
            delFiles.Parameters.AddWithValue("$path", fullSourcePath);
            await delFiles.ExecuteNonQueryAsync(ct);
        }

        await using (var delFts = conn.CreateCommand())
        {
            delFts.Transaction = tx;
            delFts.CommandText =
                "DELETE FROM tasks_fts WHERE task_id IN (SELECT id FROM tasks WHERE source_path = $path);";
            delFts.Parameters.AddWithValue("$path", fullSourcePath);
            await delFts.ExecuteNonQueryAsync(ct);
        }

        await using (var delTasks = conn.CreateCommand())
        {
            delTasks.Transaction = tx;
            delTasks.CommandText = "DELETE FROM tasks WHERE source_path = $path;";
            delTasks.Parameters.AddWithValue("$path", fullSourcePath);
            await delTasks.ExecuteNonQueryAsync(ct);
        }

        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rootTasks.Count; i++)
        {
            rootTasks[i].EnsureChildTasksSynced();
            await InsertTaskTreeNodeAsync(
                conn,
                tx,
                basinRoot,
                fullSourcePath,
                courseFolder,
                rootTasks[i],
                parentId: null,
                sortOrder: i,
                usedIds,
                ct
            );
        }

        await using (var upsertFile = conn.CreateCommand())
        {
            upsertFile.Transaction = tx;
            upsertFile.CommandText = """
                INSERT INTO indexed_files (path, course_folder, last_write_ticks)
                VALUES ($path, $course, $ticks)
                ON CONFLICT(path) DO UPDATE SET
                    course_folder = excluded.course_folder,
                    last_write_ticks = excluded.last_write_ticks;
                """;
            upsertFile.Parameters.AddWithValue("$path", fullSourcePath);
            upsertFile.Parameters.AddWithValue("$course", (object?)courseFolder ?? DBNull.Value);
            upsertFile.Parameters.AddWithValue("$ticks", lastWriteTicks);
            await upsertFile.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    private static async Task InsertTaskTreeNodeAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        string basinRoot,
        string fullSourcePath,
        string? defaultCourseFolder,
        TaskItem task,
        string? parentId,
        int sortOrder,
        HashSet<string> usedIds,
        CancellationToken ct
    )
    {
        var effectiveCourse = task.CourseFolder ?? defaultCourseFolder;
        var baseId = string.IsNullOrWhiteSpace(task.Id)
            ? $"{effectiveCourse ?? "inbox"}:{task.LineNumber}:{sortOrder}"
            : task.Id;

        if (
            !string.IsNullOrWhiteSpace(effectiveCourse)
            && !baseId.StartsWith($"{effectiveCourse}:", StringComparison.OrdinalIgnoreCase)
            && !baseId.StartsWith("lms-", StringComparison.OrdinalIgnoreCase)
        )
        {
            baseId = $"{effectiveCourse}:{baseId}";
        }

        var uniqueId = baseId;
        int dup = 2;
        while (!usedIds.Add(uniqueId))
        {
            uniqueId = $"{baseId}#{dup++}";
        }

        task.Id = uniqueId;
        task.ParentTaskId = parentId;
        task.SourceMarkdownPath = fullSourcePath;
        task.CourseFolder = effectiveCourse;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO tasks (
                    id, parent_id, sort_order, source_path, line_number,
                    course_folder, course_alias, title, is_completed,
                    due_date, due_time, completed_date, priority, difficulty,
                    term, section, lms_kind, weight_percent,
                    lms_id, lms_status, lms_score, lms_url, note_path, updated_at_utc
                ) VALUES (
                    $id, $parent_id, $sort_order, $source_path, $line_number,
                    $course_folder, $course_alias, $title, $is_completed,
                    $due_date, $due_time, $completed_date, $priority, $difficulty,
                    $term, $section, $lms_kind, $weight_percent,
                    $lms_id, $lms_status, $lms_score, $lms_url, $note_path, $updated_at_utc
                );
                """;
            cmd.Parameters.AddWithValue("$id", uniqueId);
            cmd.Parameters.AddWithValue("$parent_id", (object?)parentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sort_order", sortOrder);
            cmd.Parameters.AddWithValue("$source_path", fullSourcePath);
            cmd.Parameters.AddWithValue("$line_number", task.LineNumber);
            cmd.Parameters.AddWithValue("$course_folder", (object?)effectiveCourse ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$course_alias", (object?)task.CourseAlias ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$title", task.Title);
            cmd.Parameters.AddWithValue("$is_completed", task.IsCompleted ? 1 : 0);
            cmd.Parameters.AddWithValue(
                "$due_date",
                task.DueDate.HasValue
                    ? task.DueDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : DBNull.Value
            );
            cmd.Parameters.AddWithValue(
                "$due_time",
                task.DueTime.HasValue
                    ? task.DueTime.Value.ToString("HH:mm", CultureInfo.InvariantCulture)
                    : DBNull.Value
            );
            cmd.Parameters.AddWithValue(
                "$completed_date",
                task.CompletedDate.HasValue
                    ? task.CompletedDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : DBNull.Value
            );
            cmd.Parameters.AddWithValue("$priority", (int)task.Priority);
            cmd.Parameters.AddWithValue("$difficulty", task.Difficulty);
            cmd.Parameters.AddWithValue("$term", (object?)task.Term ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$section", (object?)task.Section ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lms_kind", (object?)task.LmsKind ?? DBNull.Value);
            cmd.Parameters.AddWithValue(
                "$weight_percent",
                task.WeightPercent.HasValue ? task.WeightPercent.Value : DBNull.Value
            );
            cmd.Parameters.AddWithValue("$lms_id", (object?)task.LmsId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lms_status", (object?)task.LmsStatus ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lms_score", (object?)task.LmsScore ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lms_url", (object?)task.LmsUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$note_path", (object?)task.NotePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue(
                "$updated_at_utc",
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            );
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (
            var lbl in task
                .Labels.Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
        )
        {
            await using var lblCmd = conn.CreateCommand();
            lblCmd.Transaction = tx;
            lblCmd.CommandText =
                "INSERT OR IGNORE INTO task_labels (task_id, label) VALUES ($tid, $lbl);";
            lblCmd.Parameters.AddWithValue("$tid", uniqueId);
            lblCmd.Parameters.AddWithValue("$lbl", lbl.Trim());
            await lblCmd.ExecuteNonQueryAsync(ct);
        }

        async Task InsertFilesOfKindAsync(IEnumerable<string> paths, string kind)
        {
            foreach (
                var p in paths
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
            )
            {
                await using var fCmd = conn.CreateCommand();
                fCmd.Transaction = tx;
                fCmd.CommandText =
                    "INSERT OR IGNORE INTO task_files (task_id, kind, rel_path) VALUES ($tid, $kind, $path);";
                fCmd.Parameters.AddWithValue("$tid", uniqueId);
                fCmd.Parameters.AddWithValue("$kind", kind);
                fCmd.Parameters.AddWithValue("$path", p.Trim());
                await fCmd.ExecuteNonQueryAsync(ct);
            }
        }

        await InsertFilesOfKindAsync(task.InstructionFiles, "instruction");
        await InsertFilesOfKindAsync(task.SubmissionFiles, "submission");
        await InsertFilesOfKindAsync(task.LinkedFiles, "linked");

        var noteBody = TryReadNoteBody(basinRoot, effectiveCourse, task.NotePath);
        await using (var ftsCmd = conn.CreateCommand())
        {
            ftsCmd.Transaction = tx;
            ftsCmd.CommandText = """
                INSERT INTO tasks_fts (task_id, title, course_alias, term, section, labels, note_body)
                VALUES ($tid, $title, $alias, $term, $section, $labels, $note);
                """;
            ftsCmd.Parameters.AddWithValue("$tid", uniqueId);
            ftsCmd.Parameters.AddWithValue("$title", task.Title);
            ftsCmd.Parameters.AddWithValue("$alias", task.CourseAlias ?? string.Empty);
            ftsCmd.Parameters.AddWithValue("$term", task.Term ?? string.Empty);
            ftsCmd.Parameters.AddWithValue("$section", task.Section ?? string.Empty);
            ftsCmd.Parameters.AddWithValue("$labels", string.Join(' ', task.Labels));
            ftsCmd.Parameters.AddWithValue("$note", noteBody);
            await ftsCmd.ExecuteNonQueryAsync(ct);
        }

        for (int cIdx = 0; cIdx < task.ChildTasks.Count; cIdx++)
        {
            await InsertTaskTreeNodeAsync(
                conn,
                tx,
                basinRoot,
                fullSourcePath,
                effectiveCourse,
                task.ChildTasks[cIdx],
                parentId: uniqueId,
                sortOrder: cIdx,
                usedIds,
                ct
            );
        }
    }

    private static string TryReadNoteBody(string basinRoot, string? courseFolder, string? notePath)
    {
        if (string.IsNullOrWhiteSpace(notePath))
            return string.Empty;

        try
        {
            var baseDir = string.IsNullOrWhiteSpace(courseFolder)
                ? basinRoot
                : Path.Combine(basinRoot, courseFolder);
            var full = Path.Combine(baseDir, notePath);
            if (!File.Exists(full))
                return string.Empty;

            return File.ReadAllText(full);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<IReadOnlyList<TaskItem>> LoadAllTasksFromConnectionAsync(
        SqliteConnection conn,
        HashSet<string> activeSourcePaths,
        CancellationToken ct
    )
    {
        var byId = new Dictionary<string, TaskItem>(StringComparer.OrdinalIgnoreCase);
        var orderedNodes = new List<TaskItem>();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT
                    id, parent_id, sort_order, source_path, line_number,
                    course_folder, course_alias, title, is_completed,
                    due_date, due_time, completed_date, priority, difficulty,
                    term, section, lms_kind, weight_percent,
                    lms_id, lms_status, lms_score, lms_url, note_path
                FROM tasks
                ORDER BY source_path, parent_id NULLS FIRST, sort_order, line_number;
                """;

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var sourcePath = r.GetString(3);
                if (!activeSourcePaths.Contains(sourcePath))
                    continue;

                var item = new TaskItem
                {
                    Id = r.GetString(0),
                    ParentTaskId = r.IsDBNull(1) ? null : r.GetString(1),
                    SourceMarkdownPath = sourcePath,
                    LineNumber = r.GetInt32(4),
                    CourseFolder = r.IsDBNull(5) ? null : r.GetString(5),
                    CourseAlias = r.IsDBNull(6) ? null : r.GetString(6),
                    Title = r.GetString(7),
                    IsCompleted = r.GetInt32(8) != 0,
                    DueDate =
                        !r.IsDBNull(9)
                        && DateOnly.TryParseExact(
                            r.GetString(9),
                            "yyyy-MM-dd",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out var dd
                        )
                            ? dd
                            : null,
                    DueTime =
                        !r.IsDBNull(10)
                        && TimeOnly.TryParseExact(
                            r.GetString(10),
                            "HH:mm",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out var dt
                        )
                            ? dt
                            : null,
                    CompletedDate =
                        !r.IsDBNull(11)
                        && DateOnly.TryParseExact(
                            r.GetString(11),
                            "yyyy-MM-dd",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out var cd
                        )
                            ? cd
                            : null,
                    Priority = (TaskPriority)Math.Clamp(r.GetInt32(12), 1, 4),
                    Difficulty = Math.Clamp(r.GetInt32(13), 1, 5),
                    Term = r.IsDBNull(14) ? null : r.GetString(14),
                    Section = r.IsDBNull(15) ? null : r.GetString(15),
                    LmsKind = r.IsDBNull(16) ? null : r.GetString(16),
                    WeightPercent = r.IsDBNull(17) ? null : r.GetDouble(17),
                    LmsId = r.IsDBNull(18) ? null : r.GetString(18),
                    LmsStatus = r.IsDBNull(19) ? null : r.GetString(19),
                    LmsScore = r.IsDBNull(20) ? null : r.GetString(20),
                    LmsUrl = r.IsDBNull(21) ? null : r.GetString(21),
                    NotePath = r.IsDBNull(22) ? null : r.GetString(22),
                };

                byId[item.Id] = item;
                orderedNodes.Add(item);
            }
        }

        await using (var lblCmd = conn.CreateCommand())
        {
            lblCmd.CommandText = "SELECT task_id, label FROM task_labels;";
            await using var r = await lblCmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (byId.TryGetValue(r.GetString(0), out var t))
                    t.Labels.Add(r.GetString(1));
            }
        }

        await using (var fCmd = conn.CreateCommand())
        {
            fCmd.CommandText = "SELECT task_id, kind, rel_path FROM task_files;";
            await using var r = await fCmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (!byId.TryGetValue(r.GetString(0), out var t))
                    continue;
                var kind = r.GetString(1);
                var path = r.GetString(2);
                switch (kind)
                {
                    case "instruction":
                        t.InstructionFiles.Add(path);
                        break;
                    case "submission":
                        t.SubmissionFiles.Add(path);
                        break;
                    case "linked":
                        t.LinkedFiles.Add(path);
                        break;
                }
            }
        }

        var roots = new List<TaskItem>();
        foreach (var node in orderedNodes)
        {
            if (
                !string.IsNullOrWhiteSpace(node.ParentTaskId)
                && byId.TryGetValue(node.ParentTaskId, out var parent)
            )
            {
                parent.ChildTasks.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        foreach (var root in roots)
            root.EnsureChildTasksSynced();

        return roots;
    }
}
