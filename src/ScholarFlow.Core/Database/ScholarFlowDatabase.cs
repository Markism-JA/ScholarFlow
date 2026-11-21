using Microsoft.Data.Sqlite;
using ScholarFlow.Core.Services;

namespace ScholarFlow.Core.Database;

/// <summary>
/// Relational SQLite + FTS5 database engine (.scholarflow/scholarflow.db) for first-class tasks,
/// hierarchical subtasks (parent_id), labels, instruction/submission file attachments, and full-text search.
/// Synchronizes bidirectionally with inbox.md and per-course tasks.md files.
/// </summary>
public static partial class ScholarFlowDatabase
{
    private static readonly SemaphoreSlim DbLock = new(1, 1);

    static ScholarFlowDatabase()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    public static string GetDatabasePath(string basinRoot) =>
        Path.Combine(BasinService.GetMetaDir(basinRoot), "scholarflow.db");

    private static string BuildConnectionString(string basinRoot)
    {
        var dbPath = GetDatabasePath(basinRoot);
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        return new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public static async Task EnsureInitializedAsync(
        string basinRoot,
        CancellationToken ct = default
    )
    {
        await using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
        await conn.OpenAsync(ct);
        await EnsureSchemaOnConnectionAsync(conn, ct);
    }

    private static async Task EnsureSchemaOnConnectionAsync(
        SqliteConnection conn,
        CancellationToken ct
    )
    {
        const string sql = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;

            CREATE TABLE IF NOT EXISTS indexed_files (
                path TEXT PRIMARY KEY,
                course_folder TEXT NULL,
                last_write_ticks INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS tasks (
                id TEXT PRIMARY KEY,
                parent_id TEXT NULL,
                sort_order INTEGER NOT NULL DEFAULT 0,
                source_path TEXT NOT NULL,
                line_number INTEGER NOT NULL DEFAULT 0,
                course_folder TEXT NULL,
                course_alias TEXT NULL,
                title TEXT NOT NULL,
                is_completed INTEGER NOT NULL DEFAULT 0,
                due_date TEXT NULL,
                due_time TEXT NULL,
                completed_date TEXT NULL,
                priority INTEGER NOT NULL DEFAULT 4,
                difficulty INTEGER NOT NULL DEFAULT 1,
                term TEXT NULL,
                section TEXT NULL,
                lms_kind TEXT NULL,
                weight_percent REAL NULL,
                lms_id TEXT NULL,
                lms_status TEXT NULL,
                lms_score TEXT NULL,
                lms_url TEXT NULL,
                note_path TEXT NULL,
                updated_at_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_tasks_source_path ON tasks(source_path);
            CREATE INDEX IF NOT EXISTS idx_tasks_parent_id ON tasks(parent_id);
            CREATE INDEX IF NOT EXISTS idx_tasks_course_folder ON tasks(course_folder);
            CREATE INDEX IF NOT EXISTS idx_tasks_term ON tasks(term);

            CREATE TABLE IF NOT EXISTS task_labels (
                task_id TEXT NOT NULL,
                label TEXT NOT NULL,
                PRIMARY KEY (task_id, label)
            );

            CREATE TABLE IF NOT EXISTS task_files (
                task_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                rel_path TEXT NOT NULL,
                PRIMARY KEY (task_id, kind, rel_path)
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS tasks_fts USING fts5(
                task_id UNINDEXED,
                title,
                course_alias,
                term,
                section,
                labels,
                note_body
            );

            CREATE TABLE IF NOT EXISTS quizzes (
                id TEXT PRIMARY KEY,
                course_folder TEXT NOT NULL,
                course_title TEXT NOT NULL,
                term TEXT NULL,
                title TEXT NOT NULL,
                source_pdf_path TEXT NOT NULL,
                model_used TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                attempt_count INTEGER NOT NULL DEFAULT 0,
                last_score_percent REAL NULL,
                source_pdf_mtime_ticks INTEGER NULL
            );

            CREATE TABLE IF NOT EXISTS quiz_questions (
                id TEXT PRIMARY KEY,
                quiz_id TEXT NOT NULL,
                order_index INTEGER NOT NULL DEFAULT 0,
                kind TEXT NOT NULL,
                cognitive_level TEXT NOT NULL,
                difficulty INTEGER NOT NULL DEFAULT 3,
                topic_tag TEXT NOT NULL,
                prompt TEXT NOT NULL,
                options_json TEXT NOT NULL,
                correct_answer TEXT NOT NULL,
                aliases_json TEXT NOT NULL,
                explanation TEXT NOT NULL,
                source_pdf_path TEXT NOT NULL,
                source_page INTEGER NOT NULL DEFAULT 1,
                section_title TEXT NOT NULL,
                verbatim_quote TEXT NOT NULL,
                highlight_rects_json TEXT NOT NULL,
                evidence_anchors_json TEXT NOT NULL DEFAULT '[]',
                srs_interval_days REAL NOT NULL DEFAULT 0,
                srs_ease_factor REAL NOT NULL DEFAULT 2.5,
                srs_due_date TEXT NULL,
                srs_reps INTEGER NOT NULL DEFAULT 0,
                srs_lapses INTEGER NOT NULL DEFAULT 0,
                last_answered_correct INTEGER NULL,
                code_snippet TEXT NULL,
                code_language TEXT NULL,
                math_latex TEXT NULL,
                diagram_text TEXT NULL,
                image_path TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_quiz_questions_quiz_id ON quiz_questions(quiz_id);
            CREATE INDEX IF NOT EXISTS idx_quiz_questions_srs_due ON quiz_questions(srs_due_date);

            CREATE TABLE IF NOT EXISTS quiz_attempts (
                id TEXT PRIMARY KEY,
                quiz_id TEXT NOT NULL,
                question_id TEXT NOT NULL,
                user_answer TEXT NOT NULL,
                is_correct INTEGER NOT NULL,
                quality_rating INTEGER NOT NULL,
                answered_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS knowledge_files (
                file_path TEXT PRIMARY KEY,
                course_folder TEXT NOT NULL DEFAULT '',
                course_alias TEXT NOT NULL DEFAULT '',
                source_kind TEXT NOT NULL,
                mtime_ticks INTEGER NOT NULL,
                size_bytes INTEGER NOT NULL,
                indexed_at_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_knowledge_files_course ON knowledge_files(course_folder);

            CREATE TABLE IF NOT EXISTS knowledge_chunk_map (
                chunk_rowid INTEGER PRIMARY KEY,
                file_path TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_knowledge_chunk_map_path ON knowledge_chunk_map(file_path);

            CREATE VIRTUAL TABLE IF NOT EXISTS knowledge_chunks_fts USING fts5(
                chunk_id UNINDEXED,
                file_path UNINDEXED,
                course_folder,
                course_alias,
                source_kind,
                title,
                section_title,
                location_label UNINDEXED,
                start_line UNINDEXED,
                end_line UNINDEXED,
                page_number UNINDEXED,
                quiz_id UNINDEXED,
                question_id UNINDEXED,
                tags,
                content,
                tokenize='porter unicode61'
            );
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);

        foreach (
            var alterSql in new[]
            {
                "ALTER TABLE quizzes ADD COLUMN source_pdf_mtime_ticks INTEGER NULL;",
                "ALTER TABLE quizzes ADD COLUMN in_progress_index INTEGER NULL;",
                "ALTER TABLE quizzes ADD COLUMN in_progress_answers_json TEXT NULL;",
                "ALTER TABLE quiz_questions ADD COLUMN code_snippet TEXT NULL;",
                "ALTER TABLE quiz_questions ADD COLUMN code_language TEXT NULL;",
                "ALTER TABLE quiz_questions ADD COLUMN math_latex TEXT NULL;",
                "ALTER TABLE quiz_questions ADD COLUMN diagram_text TEXT NULL;",
                "ALTER TABLE quiz_questions ADD COLUMN evidence_anchors_json TEXT NOT NULL DEFAULT '[]';",
                "ALTER TABLE quiz_questions ADD COLUMN image_path TEXT NULL;",
                "ALTER TABLE quiz_questions ADD COLUMN source_start_line INTEGER NULL;",
                "ALTER TABLE quiz_questions ADD COLUMN source_end_line INTEGER NULL;",
                "ALTER TABLE quiz_questions ADD COLUMN hint TEXT NULL;",
                "ALTER TABLE quiz_questions ADD COLUMN archetype TEXT NULL;",
            }
        )
        {
            try
            {
                await using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = alterSql;
                await alterCmd.ExecuteNonQueryAsync(ct);
            }
            catch
            {
                // Column already exists
            }
        }
    }
}
