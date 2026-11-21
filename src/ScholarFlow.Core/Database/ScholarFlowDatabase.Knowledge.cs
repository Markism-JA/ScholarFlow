using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using ScholarFlow.Core.Knowledge;

namespace ScholarFlow.Core.Database;

public static partial class ScholarFlowDatabase
{
    public sealed record RawKnowledgeCandidate(
        string ChunkId,
        string FilePath,
        string CourseFolder,
        string CourseAlias,
        string SourceKind,
        string Title,
        string SectionTitle,
        string LocationLabel,
        int StartLine,
        int EndLine,
        int PageNumber,
        string QuizId,
        string QuestionId,
        string Tags,
        string Content,
        string FtsSnippet,
        double RawBm25
    );

    public static async Task<
        Dictionary<string, (long MtimeTicks, long SizeBytes, string CourseFolder, string SourceKind)>
    > GetKnowledgeFileIndexSnapshotAsync(string basinRoot, CancellationToken ct = default)
    {
        var map = new Dictionary<
            string,
            (long MtimeTicks, long SizeBytes, string CourseFolder, string SourceKind)
        >(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(basinRoot))
            return map;

        await DbLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
            await conn.OpenAsync(ct);
            await EnsureSchemaOnConnectionAsync(conn, ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT file_path, mtime_ticks, size_bytes, course_folder, source_kind
                FROM knowledge_files;
                """;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var path = reader.GetString(0);
                var ticks = reader.GetInt64(1);
                var size = reader.GetInt64(2);
                var course = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                var kind = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                map[path] = (ticks, size, course, kind);
            }
        }
        finally
        {
            DbLock.Release();
        }

        return map;
    }

    public static async Task<(int TotalFiles, int TotalChunks)> GetKnowledgeIndexCountsAsync(
        string basinRoot,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(basinRoot))
            return (0, 0);

        await DbLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
            await conn.OpenAsync(ct);
            await EnsureSchemaOnConnectionAsync(conn, ct);

            int files = 0;
            int chunks = 0;

            await using (var fCmd = conn.CreateCommand())
            {
                fCmd.CommandText = "SELECT COUNT(*) FROM knowledge_files;";
                files = Convert.ToInt32(
                    await fCmd.ExecuteScalarAsync(ct),
                    CultureInfo.InvariantCulture
                );
            }

            await using (var cCmd = conn.CreateCommand())
            {
                cCmd.CommandText = "SELECT COUNT(*) FROM knowledge_chunk_map;";
                chunks = Convert.ToInt32(
                    await cCmd.ExecuteScalarAsync(ct),
                    CultureInfo.InvariantCulture
                );
            }

            return (files, chunks);
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static async Task ClearKnowledgeIndexAsync(
        string basinRoot,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(basinRoot))
            return;

        await DbLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
            await conn.OpenAsync(ct);
            await EnsureSchemaOnConnectionAsync(conn, ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM knowledge_chunks_fts;
                DELETE FROM knowledge_chunk_map;
                DELETE FROM knowledge_files;
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static async Task<(int TotalFiles, int TotalChunks)> ApplyKnowledgeIndexBatchAsync(
        string basinRoot,
        IReadOnlyList<KnowledgeFileIndexEntry> upsertEntries,
        IReadOnlyList<string> deleteFileKeys,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(basinRoot))
            return (0, 0);

        await DbLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
            await conn.OpenAsync(ct);
            await EnsureSchemaOnConnectionAsync(conn, ct);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

            // Prepared commands for O(log N) chunk pruning via knowledge_chunk_map
            await using var delFtsCmd = conn.CreateCommand();
            delFtsCmd.Transaction = tx;
            delFtsCmd.CommandText = """
                DELETE FROM knowledge_chunks_fts
                WHERE rowid IN (SELECT chunk_rowid FROM knowledge_chunk_map WHERE file_path = $key);
                DELETE FROM knowledge_chunk_map WHERE file_path = $key;
                """;
            var pDelKey = delFtsCmd.Parameters.Add("$key", SqliteType.Text);

            await using var delFileCmd = conn.CreateCommand();
            delFileCmd.Transaction = tx;
            delFileCmd.CommandText = "DELETE FROM knowledge_files WHERE file_path = $key;";
            var pDelFileKey = delFileCmd.Parameters.Add("$key", SqliteType.Text);

            foreach (var key in deleteFileKeys)
            {
                ct.ThrowIfCancellationRequested();
                pDelKey.Value = key;
                await delFtsCmd.ExecuteNonQueryAsync(ct);

                pDelFileKey.Value = key;
                await delFileCmd.ExecuteNonQueryAsync(ct);
            }

            await using var upsertFileCmd = conn.CreateCommand();
            upsertFileCmd.Transaction = tx;
            upsertFileCmd.CommandText = """
                INSERT OR REPLACE INTO knowledge_files (
                    file_path, course_folder, course_alias, source_kind,
                    mtime_ticks, size_bytes, indexed_at_utc
                ) VALUES (
                    $file_path, $course_folder, $course_alias, $source_kind,
                    $mtime_ticks, $size_bytes, $indexed_at_utc
                );
                """;
            var pFileKey = upsertFileCmd.Parameters.Add("$file_path", SqliteType.Text);
            var pFileCourse = upsertFileCmd.Parameters.Add("$course_folder", SqliteType.Text);
            var pFileAlias = upsertFileCmd.Parameters.Add("$course_alias", SqliteType.Text);
            var pFileKind = upsertFileCmd.Parameters.Add("$source_kind", SqliteType.Text);
            var pFileMtime = upsertFileCmd.Parameters.Add("$mtime_ticks", SqliteType.Integer);
            var pFileSize = upsertFileCmd.Parameters.Add("$size_bytes", SqliteType.Integer);
            var pFileUtc = upsertFileCmd.Parameters.Add("$indexed_at_utc", SqliteType.Text);

            await using var insChunkCmd = conn.CreateCommand();
            insChunkCmd.Transaction = tx;
            insChunkCmd.CommandText = """
                INSERT INTO knowledge_chunks_fts (
                    chunk_id, file_path, course_folder, course_alias, source_kind,
                    title, section_title, location_label, start_line, end_line,
                    page_number, quiz_id, question_id, tags, content
                ) VALUES (
                    $chunk_id, $file_path, $course_folder, $course_alias, $source_kind,
                    $title, $section_title, $location_label, $start_line, $end_line,
                    $page_number, $quiz_id, $question_id, $tags, $content
                );
                INSERT INTO knowledge_chunk_map (chunk_rowid, file_path)
                VALUES (last_insert_rowid(), $map_key);
                """;
            var pChunkId = insChunkCmd.Parameters.Add("$chunk_id", SqliteType.Text);
            var pChunkPath = insChunkCmd.Parameters.Add("$file_path", SqliteType.Text);
            var pChunkCourse = insChunkCmd.Parameters.Add("$course_folder", SqliteType.Text);
            var pChunkAlias = insChunkCmd.Parameters.Add("$course_alias", SqliteType.Text);
            var pChunkKind = insChunkCmd.Parameters.Add("$source_kind", SqliteType.Text);
            var pChunkTitle = insChunkCmd.Parameters.Add("$title", SqliteType.Text);
            var pChunkSec = insChunkCmd.Parameters.Add("$section_title", SqliteType.Text);
            var pChunkLoc = insChunkCmd.Parameters.Add("$location_label", SqliteType.Text);
            var pChunkStart = insChunkCmd.Parameters.Add("$start_line", SqliteType.Integer);
            var pChunkEnd = insChunkCmd.Parameters.Add("$end_line", SqliteType.Integer);
            var pChunkPage = insChunkCmd.Parameters.Add("$page_number", SqliteType.Integer);
            var pChunkQuiz = insChunkCmd.Parameters.Add("$quiz_id", SqliteType.Text);
            var pChunkQues = insChunkCmd.Parameters.Add("$question_id", SqliteType.Text);
            var pChunkTags = insChunkCmd.Parameters.Add("$tags", SqliteType.Text);
            var pChunkContent = insChunkCmd.Parameters.Add("$content", SqliteType.Text);
            var pMapKey = insChunkCmd.Parameters.Add("$map_key", SqliteType.Text);

            var nowIso = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            foreach (var entry in upsertEntries)
            {
                ct.ThrowIfCancellationRequested();

                pDelKey.Value = entry.FileKey;
                await delFtsCmd.ExecuteNonQueryAsync(ct);

                pFileKey.Value = entry.FileKey;
                pFileCourse.Value = entry.CourseFolder;
                pFileAlias.Value = entry.CourseAlias;
                pFileKind.Value = entry.SourceKind;
                pFileMtime.Value = entry.MtimeTicks;
                pFileSize.Value = entry.SizeBytes;
                pFileUtc.Value = nowIso;
                await upsertFileCmd.ExecuteNonQueryAsync(ct);

                foreach (var ch in entry.Chunks)
                {
                    pChunkId.Value = ch.ChunkId;
                    pChunkPath.Value = ch.FilePath;
                    pChunkCourse.Value = ch.CourseFolder;
                    pChunkAlias.Value = ch.CourseAlias;
                    pChunkKind.Value = ch.SourceKind;
                    pChunkTitle.Value = ch.Title;
                    pChunkSec.Value = ch.SectionTitle;
                    pChunkLoc.Value = ch.LocationLabel;
                    pChunkStart.Value = ch.StartLine;
                    pChunkEnd.Value = ch.EndLine;
                    pChunkPage.Value = ch.PageNumber;
                    pChunkQuiz.Value = ch.QuizId;
                    pChunkQues.Value = ch.QuestionId;
                    pChunkTags.Value = ch.Tags;
                    pChunkContent.Value = ch.Content;
                    pMapKey.Value = entry.FileKey;
                    await insChunkCmd.ExecuteNonQueryAsync(ct);
                }
            }

            await tx.CommitAsync(ct);

            int totalFiles = 0;
            int totalChunks = 0;
            await using (var fCmd = conn.CreateCommand())
            {
                fCmd.CommandText = "SELECT COUNT(*) FROM knowledge_files;";
                totalFiles = Convert.ToInt32(
                    await fCmd.ExecuteScalarAsync(ct),
                    CultureInfo.InvariantCulture
                );
            }
            await using (var cCmd = conn.CreateCommand())
            {
                cCmd.CommandText = "SELECT COUNT(*) FROM knowledge_chunk_map;";
                totalChunks = Convert.ToInt32(
                    await cCmd.ExecuteScalarAsync(ct),
                    CultureInfo.InvariantCulture
                );
            }

            return (totalFiles, totalChunks);
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static async Task<IReadOnlyList<RawKnowledgeCandidate>> QueryKnowledgeChunksRawAsync(
        string basinRoot,
        string? ftsMatchExpression,
        IReadOnlyList<string> courseFilters,
        IReadOnlyList<string> sourceKinds,
        int limit = 160,
        CancellationToken ct = default
    )
    {
        var results = new List<RawKnowledgeCandidate>();
        if (string.IsNullOrWhiteSpace(basinRoot))
            return results;

        await DbLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
            await conn.OpenAsync(ct);
            await EnsureSchemaOnConnectionAsync(conn, ct);

            await using var cmd = conn.CreateCommand();
            var sb = new StringBuilder();
            bool hasMatch = !string.IsNullOrWhiteSpace(ftsMatchExpression);

            if (hasMatch)
            {
                sb.AppendLine(
                    """
                    SELECT
                        chunk_id, file_path, course_folder, course_alias, source_kind,
                        title, section_title, location_label, start_line, end_line,
                        page_number, quiz_id, question_id, tags, content,
                        snippet(knowledge_chunks_fts, 14, '«', '»', ' … ', 28) AS fts_snip,
                        bm25(knowledge_chunks_fts, 0.0, 0.0, 1.5, 2.0, 1.0, 5.0, 4.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 3.5, 1.0) AS raw_bm25
                    FROM knowledge_chunks_fts
                    WHERE knowledge_chunks_fts MATCH $match
                    """
                );
                cmd.Parameters.AddWithValue("$match", ftsMatchExpression!);
            }
            else
            {
                sb.AppendLine(
                    """
                    SELECT
                        chunk_id, file_path, course_folder, course_alias, source_kind,
                        title, section_title, location_label, start_line, end_line,
                        page_number, quiz_id, question_id, tags, content,
                        substr(content, 1, 240) AS fts_snip,
                        0.0 AS raw_bm25
                    FROM knowledge_chunks_fts
                    WHERE 1 = 1
                    """
                );
            }

            AppendStructuredSqlFilters(sb, cmd, courseFilters, sourceKinds);

            sb.AppendLine(hasMatch ? "ORDER BY raw_bm25 ASC" : "ORDER BY rowid DESC");
            sb.AppendLine("LIMIT $limit;");
            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.CommandText = sb.ToString();

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(ReadCandidateRow(reader));
            }
        }
        finally
        {
            DbLock.Release();
        }

        return results;
    }

    private static void AppendStructuredSqlFilters(
        StringBuilder sb,
        SqliteCommand cmd,
        IReadOnlyList<string> courseFilters,
        IReadOnlyList<string> sourceKinds
    )
    {
        if (sourceKinds.Count > 0)
        {
            sb.Append(" AND source_kind IN (");
            for (int i = 0; i < sourceKinds.Count; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                var pName = $"$sk{i}";
                sb.Append(pName);
                cmd.Parameters.AddWithValue(pName, sourceKinds[i]);
            }
            sb.AppendLine(")");
        }

        if (courseFilters.Count > 0)
        {
            sb.Append(" AND (");
            for (int i = 0; i < courseFilters.Count; i++)
            {
                if (i > 0)
                    sb.Append(" OR ");
                var pExact = $"$cfExact{i}";
                var pLike = $"$cfLike{i}";
                sb.Append(
                    $"(LOWER(course_alias) = {pExact} OR LOWER(course_folder) LIKE {pLike} OR LOWER(course_alias) LIKE {pLike})"
                );
                var norm = courseFilters[i].Trim().TrimStart('#').ToLowerInvariant();
                cmd.Parameters.AddWithValue(pExact, norm);
                cmd.Parameters.AddWithValue(pLike, $"%{norm}%");
            }
            sb.AppendLine(")");
        }
    }

    private static RawKnowledgeCandidate ReadCandidateRow(SqliteDataReader reader) =>
        new(
            ChunkId: reader.GetString(0),
            FilePath: reader.GetString(1),
            CourseFolder: reader.GetString(2),
            CourseAlias: reader.GetString(3),
            SourceKind: reader.GetString(4),
            Title: reader.GetString(5),
            SectionTitle: reader.GetString(6),
            LocationLabel: reader.GetString(7),
            StartLine: Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture),
            EndLine: Convert.ToInt32(reader.GetValue(9), CultureInfo.InvariantCulture),
            PageNumber: Convert.ToInt32(reader.GetValue(10), CultureInfo.InvariantCulture),
            QuizId: reader.GetString(11),
            QuestionId: reader.GetString(12),
            Tags: reader.GetString(13),
            Content: reader.GetString(14),
            FtsSnippet: reader.IsDBNull(15) ? string.Empty : reader.GetString(15),
            RawBm25: reader.IsDBNull(16) ? 0.0 : reader.GetDouble(16)
        );
}
