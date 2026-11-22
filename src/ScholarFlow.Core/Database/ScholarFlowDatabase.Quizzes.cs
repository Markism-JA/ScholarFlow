using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ScholarFlow.Core.Quiz;

namespace ScholarFlow.Core.Database;

public static partial class ScholarFlowDatabase
{
    public static async Task SaveQuizSessionAsync(
        string basinRoot,
        QuizSessionRecord quiz,
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
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

            await using (var qCmd = conn.CreateCommand())
            {
                qCmd.Transaction = tx;
                qCmd.CommandText = """
                    INSERT OR REPLACE INTO quizzes (
                        id, course_folder, course_title, term, title,
                        source_pdf_path, model_used, created_at_utc, attempt_count, last_score_percent, source_pdf_mtime_ticks
                    ) VALUES (
                        $id, $course_folder, $course_title, $term, $title,
                        $source_pdf_path, $model_used, $created_at_utc, $attempt_count, $last_score_percent, $source_pdf_mtime_ticks
                    );
                    """;
                qCmd.Parameters.AddWithValue("$id", quiz.Id);
                qCmd.Parameters.AddWithValue("$course_folder", quiz.CourseFolder);
                qCmd.Parameters.AddWithValue("$course_title", quiz.CourseTitle);
                qCmd.Parameters.AddWithValue("$term", (object?)quiz.Term ?? DBNull.Value);
                qCmd.Parameters.AddWithValue("$title", quiz.Title);
                qCmd.Parameters.AddWithValue("$source_pdf_path", quiz.SourcePdfPath);
                qCmd.Parameters.AddWithValue("$model_used", quiz.ModelUsed);
                qCmd.Parameters.AddWithValue(
                    "$created_at_utc",
                    quiz.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)
                );
                qCmd.Parameters.AddWithValue("$attempt_count", quiz.AttemptCount);
                qCmd.Parameters.AddWithValue(
                    "$last_score_percent",
                    quiz.LastScorePercent.HasValue ? quiz.LastScorePercent.Value : DBNull.Value
                );
                qCmd.Parameters.AddWithValue(
                    "$source_pdf_mtime_ticks",
                    quiz.SourcePdfMtimeTicks.HasValue
                        ? quiz.SourcePdfMtimeTicks.Value
                        : DBNull.Value
                );
                await qCmd.ExecuteNonQueryAsync(ct);
            }

            await using (var delOld = conn.CreateCommand())
            {
                delOld.Transaction = tx;
                delOld.CommandText = "DELETE FROM quiz_questions WHERE quiz_id = $qid;";
                delOld.Parameters.AddWithValue("$qid", quiz.Id);
                await delOld.ExecuteNonQueryAsync(ct);
            }

            await using var itemCmd = conn.CreateCommand();
            itemCmd.Transaction = tx;
            itemCmd.CommandText = """
                INSERT OR REPLACE INTO quiz_questions (
                    id, quiz_id, order_index, kind, cognitive_level, difficulty, topic_tag,
                    prompt, options_json, correct_answer, aliases_json, explanation,
                    source_pdf_path, source_page, section_title, verbatim_quote, highlight_rects_json,
                    srs_interval_days, srs_ease_factor, srs_due_date, srs_reps, srs_lapses, last_answered_correct,
                    code_snippet, code_language, math_latex, diagram_text, evidence_anchors_json, image_path,
                    source_start_line, source_end_line, hint, archetype
                ) VALUES (
                    $id, $quiz_id, $order_index, $kind, $cognitive_level, $difficulty, $topic_tag,
                    $prompt, $options_json, $correct_answer, $aliases_json, $explanation,
                    $source_pdf_path, $source_page, $section_title, $verbatim_quote, $highlight_rects_json,
                    $srs_interval_days, $srs_ease_factor, $srs_due_date, $srs_reps, $srs_lapses, $last_answered_correct,
                    $code_snippet, $code_language, $math_latex, $diagram_text, $evidence_anchors_json, $image_path,
                    $source_start_line, $source_end_line, $hint, $archetype
                );
                """;

            for (int i = 0; i < quiz.Questions.Count; i++)
            {
                var q = quiz.Questions[i];
                q.QuizId = quiz.Id;
                q.OrderIndex = i;

                itemCmd.Parameters.Clear();
                itemCmd.Parameters.AddWithValue("$id", q.Id);
                itemCmd.Parameters.AddWithValue("$quiz_id", quiz.Id);
                itemCmd.Parameters.AddWithValue("$order_index", i);
                itemCmd.Parameters.AddWithValue("$kind", q.Kind.ToString());
                itemCmd.Parameters.AddWithValue("$cognitive_level", q.CognitiveLevel.ToString());
                itemCmd.Parameters.AddWithValue("$difficulty", q.Difficulty);
                itemCmd.Parameters.AddWithValue("$topic_tag", q.TopicTag);
                itemCmd.Parameters.AddWithValue("$prompt", q.Prompt);
                itemCmd.Parameters.AddWithValue(
                    "$options_json",
                    JsonSerializer.Serialize(q.Options)
                );
                itemCmd.Parameters.AddWithValue("$correct_answer", q.CorrectAnswer);
                itemCmd.Parameters.AddWithValue(
                    "$aliases_json",
                    JsonSerializer.Serialize(q.AcceptedAliases)
                );
                itemCmd.Parameters.AddWithValue("$explanation", q.Explanation);
                itemCmd.Parameters.AddWithValue("$source_pdf_path", q.SourcePdfPath);
                itemCmd.Parameters.AddWithValue("$source_page", q.SourcePage);
                itemCmd.Parameters.AddWithValue(
                    "$source_start_line",
                    (object?)q.SourceStartLine ?? DBNull.Value
                );
                itemCmd.Parameters.AddWithValue(
                    "$source_end_line",
                    (object?)q.SourceEndLine ?? DBNull.Value
                );
                itemCmd.Parameters.AddWithValue("$section_title", q.SectionTitle);
                itemCmd.Parameters.AddWithValue("$verbatim_quote", q.VerbatimQuote);
                itemCmd.Parameters.AddWithValue(
                    "$highlight_rects_json",
                    JsonSerializer.Serialize(q.HighlightRects)
                );
                itemCmd.Parameters.AddWithValue(
                    "$evidence_anchors_json",
                    JsonSerializer.Serialize(q.EvidenceAnchors)
                );
                itemCmd.Parameters.AddWithValue("$srs_interval_days", q.SrsIntervalDays);
                itemCmd.Parameters.AddWithValue("$srs_ease_factor", q.SrsEaseFactor);
                itemCmd.Parameters.AddWithValue(
                    "$srs_due_date",
                    q.SrsDueDate.HasValue
                        ? q.SrsDueDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : DBNull.Value
                );
                itemCmd.Parameters.AddWithValue("$srs_reps", q.SrsReps);
                itemCmd.Parameters.AddWithValue("$srs_lapses", q.SrsLapses);
                itemCmd.Parameters.AddWithValue(
                    "$last_answered_correct",
                    q.LastAnsweredCorrect.HasValue
                        ? (q.LastAnsweredCorrect.Value ? 1 : 0)
                        : DBNull.Value
                );
                itemCmd.Parameters.AddWithValue(
                    "$code_snippet",
                    (object?)q.CodeSnippet ?? DBNull.Value
                );
                itemCmd.Parameters.AddWithValue(
                    "$code_language",
                    (object?)q.CodeLanguage ?? DBNull.Value
                );
                itemCmd.Parameters.AddWithValue(
                    "$math_latex",
                    (object?)q.MathLatex ?? DBNull.Value
                );
                itemCmd.Parameters.AddWithValue(
                    "$diagram_text",
                    (object?)q.Diagram ?? DBNull.Value
                );
                itemCmd.Parameters.AddWithValue(
                    "$image_path",
                    (object?)q.ImagePath ?? DBNull.Value
                );
                itemCmd.Parameters.AddWithValue("$hint", (object?)q.Hint ?? DBNull.Value);
                itemCmd.Parameters.AddWithValue("$archetype", (object?)q.Archetype ?? DBNull.Value);
                await itemCmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static async Task<IReadOnlyList<QuizSessionRecord>> LoadQuizSessionsAsync(
        string basinRoot,
        string? courseFolder = null,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(basinRoot) || !File.Exists(GetDatabasePath(basinRoot)))
            return [];

        await DbLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
            await conn.OpenAsync(ct);
            await EnsureSchemaOnConnectionAsync(conn, ct);

            var sessions = new List<QuizSessionRecord>();
            var byId = new Dictionary<string, QuizSessionRecord>(StringComparer.OrdinalIgnoreCase);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = string.IsNullOrWhiteSpace(courseFolder)
                    ? "SELECT id, course_folder, course_title, term, title, source_pdf_path, model_used, created_at_utc, attempt_count, last_score_percent, source_pdf_mtime_ticks, in_progress_index, in_progress_answers_json FROM quizzes ORDER BY created_at_utc DESC;"
                    : "SELECT id, course_folder, course_title, term, title, source_pdf_path, model_used, created_at_utc, attempt_count, last_score_percent, source_pdf_mtime_ticks, in_progress_index, in_progress_answers_json FROM quizzes WHERE course_folder = $cf ORDER BY created_at_utc DESC;";
                if (!string.IsNullOrWhiteSpace(courseFolder))
                    cmd.Parameters.AddWithValue("$cf", courseFolder);

                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var s = new QuizSessionRecord
                    {
                        Id = r.GetString(0),
                        CourseFolder = r.GetString(1),
                        CourseTitle = r.GetString(2),
                        Term = r.IsDBNull(3) ? null : r.GetString(3),
                        Title = r.GetString(4),
                        SourcePdfPath = r.GetString(5),
                        ModelUsed = r.GetString(6),
                        CreatedAtUtc = DateTimeOffset.TryParse(
                            r.GetString(7),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out var dt
                        )
                            ? dt
                            : DateTimeOffset.UtcNow,
                        AttemptCount = r.GetInt32(8),
                        LastScorePercent = r.IsDBNull(9) ? null : r.GetDouble(9),
                        SourcePdfMtimeTicks =
                            r.FieldCount > 10 && !r.IsDBNull(10) ? r.GetInt64(10) : null,
                        InProgressQuestionIndex =
                            r.FieldCount > 11 && !r.IsDBNull(11) ? r.GetInt32(11) : null,
                        InProgressAnswers =
                            r.FieldCount > 12 && !r.IsDBNull(12)
                                ? TryDeserializeDictionary<QuizInProgressAnswer>(r.GetString(12))
                                : new Dictionary<string, QuizInProgressAnswer>(
                                    StringComparer.OrdinalIgnoreCase
                                ),
                    };
                    sessions.Add(s);
                    byId[s.Id] = s;
                }
            }

            if (sessions.Count == 0)
                return sessions;

            await using (var qCmd = conn.CreateCommand())
            {
                qCmd.CommandText = """
                    SELECT
                        id, quiz_id, order_index, kind, cognitive_level, difficulty, topic_tag,
                        prompt, options_json, correct_answer, aliases_json, explanation,
                        source_pdf_path, source_page, section_title, verbatim_quote, highlight_rects_json,
                        srs_interval_days, srs_ease_factor, srs_due_date, srs_reps, srs_lapses, last_answered_correct,
                        code_snippet, code_language, math_latex, diagram_text, evidence_anchors_json, image_path,
                    source_start_line, source_end_line, hint, archetype
                    FROM quiz_questions
                    ORDER BY quiz_id, order_index;
                    """;

                await using var r = await qCmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var quizId = r.GetString(1);
                    if (!byId.TryGetValue(quizId, out var parentQuiz))
                        continue;

                    var q = new QuizQuestionRecord
                    {
                        Id = r.GetString(0),
                        QuizId = quizId,
                        OrderIndex = r.GetInt32(2),
                        Kind = Enum.TryParse<QuizQuestionKind>(
                            r.GetString(3),
                            ignoreCase: true,
                            out var k
                        )
                            ? k
                            : QuizQuestionKind.MultipleChoice,
                        CognitiveLevel = Enum.TryParse<CognitiveLevel>(
                            r.GetString(4),
                            ignoreCase: true,
                            out var cl
                        )
                            ? cl
                            : CognitiveLevel.Comprehension,
                        Difficulty = r.GetInt32(5),
                        TopicTag = r.GetString(6),
                        Prompt = r.GetString(7),
                        Options = TryDeserializeList<string>(r.GetString(8)),
                        CorrectAnswer = r.GetString(9),
                        AcceptedAliases = TryDeserializeList<string>(r.GetString(10)),
                        Explanation = r.GetString(11),
                        SourcePdfPath = r.GetString(12),
                        SourcePage = r.GetInt32(13),
                        SectionTitle = r.GetString(14),
                        VerbatimQuote = r.GetString(15),
                        HighlightRects = TryDeserializeList<NormalizedHighlightRect>(
                            r.GetString(16)
                        ),
                        EvidenceAnchors =
                            r.FieldCount > 27 && !r.IsDBNull(27)
                                ? TryDeserializeList<GroundedEvidenceAnchor>(r.GetString(27))
                                : [],
                        SrsIntervalDays = r.GetDouble(17),
                        SrsEaseFactor = r.GetDouble(18),
                        SrsDueDate =
                            !r.IsDBNull(19)
                            && DateOnly.TryParseExact(
                                r.GetString(19),
                                "yyyy-MM-dd",
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.None,
                                out var sd
                            )
                                ? sd
                                : null,
                        SrsReps = r.GetInt32(20),
                        SrsLapses = r.GetInt32(21),
                        LastAnsweredCorrect = r.IsDBNull(22) ? null : r.GetInt32(22) != 0,
                        CodeSnippet = r.FieldCount > 23 && !r.IsDBNull(23) ? r.GetString(23) : null,
                        CodeLanguage =
                            r.FieldCount > 24 && !r.IsDBNull(24) ? r.GetString(24) : null,
                        MathLatex = r.FieldCount > 25 && !r.IsDBNull(25) ? r.GetString(25) : null,
                        Diagram = r.FieldCount > 26 && !r.IsDBNull(26) ? r.GetString(26) : null,
                        ImagePath = r.FieldCount > 28 && !r.IsDBNull(28) ? r.GetString(28) : null,
                        SourceStartLine =
                            r.FieldCount > 29 && !r.IsDBNull(29) ? r.GetInt32(29) : null,
                        SourceEndLine =
                            r.FieldCount > 30 && !r.IsDBNull(30) ? r.GetInt32(30) : null,
                        Hint = r.FieldCount > 31 && !r.IsDBNull(31) ? r.GetString(31) : null,
                        Archetype = r.FieldCount > 32 && !r.IsDBNull(32) ? r.GetString(32) : null,
                    };

                    parentQuiz.Questions.Add(q);
                }
            }

            return sessions;
        }
        finally
        {
            DbLock.Release();
        }
    }

    /// <summary>
    /// Applies SM-2 / FSRS spaced-repetition update with optional metacognitive confidence calibration:
    /// - Certain + Wrong (Hypercorrection penalty): +2 lapses, -0.30 ease, 1d reset.
    /// - Guess + Right (Lucky guess dampener): caps effective quality at 3 (Hard) and dampens interval growth.
    /// - Unsure + Right: reduces quality by 1 (min 3).
    /// - Certain + Right: boosts default Good (4) to Easy (5).
    /// </summary>
    public static int ApplyConfidenceCalibratedSrs(
        QuizQuestionRecord question,
        bool isCorrect,
        int qualityRating = 4,
        ConfidenceLevel confidence = ConfidenceLevel.None,
        DateOnly? todayOverride = null
    )
    {
        int q = !isCorrect ? 1 : Math.Clamp(qualityRating, 3, 5);

        if (isCorrect)
        {
            q = confidence switch
            {
                ConfidenceLevel.Guess => 3,
                ConfidenceLevel.Unsure => Math.Max(3, q - 1),
                ConfidenceLevel.Certain when q == 4 => 5,
                _ => q,
            };
        }

        var today = todayOverride ?? DateOnly.FromDateTime(DateTime.Today);

        if (q < 3)
        {
            question.SrsReps = 0;
            bool hypercorrection = confidence == ConfidenceLevel.Certain;
            question.SrsLapses += hypercorrection ? 2 : 1;
            question.SrsIntervalDays = 1.0;
            question.SrsEaseFactor = Math.Max(
                1.3,
                question.SrsEaseFactor - (hypercorrection ? 0.30 : 0.20)
            );
            question.SrsDueDate = today.AddDays(1);
        }
        else
        {
            question.SrsReps++;
            if (confidence == ConfidenceLevel.Guess)
            {
                // Lucky guess dampener: keep interval conservative so the student re-tests soon
                if (question.SrsReps == 1)
                    question.SrsIntervalDays = 1.0;
                else if (question.SrsReps == 2)
                    question.SrsIntervalDays = 2.0;
                else
                    question.SrsIntervalDays = Math.Round(
                        Math.Max(
                            2.0,
                            question.SrsIntervalDays * Math.Max(1.2, question.SrsEaseFactor * 0.75)
                        ),
                        1
                    );
            }
            else
            {
                if (question.SrsReps == 1)
                    question.SrsIntervalDays = q == 5 ? 3.0 : 1.0;
                else if (question.SrsReps == 2)
                    question.SrsIntervalDays = q == 5 ? 6.0 : 4.0;
                else
                    question.SrsIntervalDays = Math.Round(
                        question.SrsIntervalDays * question.SrsEaseFactor,
                        1
                    );
            }

            double easeDelta = 0.1 - (5 - q) * (0.08 + (5 - q) * 0.02);
            question.SrsEaseFactor = Math.Clamp(question.SrsEaseFactor + easeDelta, 1.3, 3.2);
            question.SrsDueDate = today.AddDays(
                Math.Max(1, (int)Math.Round(question.SrsIntervalDays))
            );
        }

        question.LastAnsweredCorrect = isCorrect;
        return q;
    }

    /// <summary>
    /// Records a student's question attempt and updates SM-2 / FSRS spaced repetition interval and due date.
    /// qualityRating: 1 = Again (Fail), 3 = Hard, 4 = Good, 5 = Easy.
    /// </summary>
    public static async Task RecordQuestionAttemptAsync(
        string basinRoot,
        QuizQuestionRecord question,
        string userAnswer,
        bool isCorrect,
        int qualityRating = 4,
        ConfidenceLevel confidence = ConfidenceLevel.None,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(basinRoot))
            return;

        int q = ApplyConfidenceCalibratedSrs(question, isCorrect, qualityRating, confidence);

        await DbLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
            await conn.OpenAsync(ct);
            await EnsureSchemaOnConnectionAsync(conn, ct);

            await using (var upd = conn.CreateCommand())
            {
                upd.CommandText = """
                    UPDATE quiz_questions
                    SET srs_interval_days = $ivl,
                        srs_ease_factor = $ef,
                        srs_due_date = $due,
                        srs_reps = $reps,
                        srs_lapses = $lapses,
                        last_answered_correct = $ok
                    WHERE id = $id;
                    """;
                upd.Parameters.AddWithValue("$ivl", question.SrsIntervalDays);
                upd.Parameters.AddWithValue("$ef", question.SrsEaseFactor);
                upd.Parameters.AddWithValue(
                    "$due",
                    (question.SrsDueDate ?? DateOnly.FromDateTime(DateTime.Today)).ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture
                    )
                );
                upd.Parameters.AddWithValue("$reps", question.SrsReps);
                upd.Parameters.AddWithValue("$lapses", question.SrsLapses);
                upd.Parameters.AddWithValue("$ok", isCorrect ? 1 : 0);
                upd.Parameters.AddWithValue("$id", question.Id);
                await upd.ExecuteNonQueryAsync(ct);
            }

            await using var att = conn.CreateCommand();
            att.CommandText = """
                INSERT INTO quiz_attempts (id, quiz_id, question_id, user_answer, is_correct, quality_rating, answered_at_utc)
                VALUES ($id, $qid, $question_id, $ans, $ok, $qual, $ts);
                """;
            att.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")[..12]);
            att.Parameters.AddWithValue("$qid", question.QuizId);
            att.Parameters.AddWithValue("$question_id", question.Id);
            att.Parameters.AddWithValue("$ans", userAnswer ?? string.Empty);
            att.Parameters.AddWithValue("$ok", isCorrect ? 1 : 0);
            att.Parameters.AddWithValue("$qual", q);
            att.Parameters.AddWithValue(
                "$ts",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            );
            await att.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static async Task SaveQuizInProgressAsync(
        string basinRoot,
        string quizId,
        int currentQuestionIndex,
        IReadOnlyDictionary<string, QuizInProgressAnswer> answers,
        CancellationToken ct = default
    )
    {
        if (
            string.IsNullOrWhiteSpace(basinRoot)
            || string.IsNullOrWhiteSpace(quizId)
            || quizId == "srs-deck"
        )
            return;

        await DbLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
            await conn.OpenAsync(ct);
            await EnsureSchemaOnConnectionAsync(conn, ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE quizzes
                SET in_progress_index = $idx,
                    in_progress_answers_json = $json
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$idx", Math.Max(0, currentQuestionIndex));
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(answers));
            cmd.Parameters.AddWithValue("$id", quizId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static async Task ClearQuizInProgressAsync(
        string basinRoot,
        string quizId,
        CancellationToken ct = default
    )
    {
        if (
            string.IsNullOrWhiteSpace(basinRoot)
            || string.IsNullOrWhiteSpace(quizId)
            || quizId == "srs-deck"
        )
            return;

        await DbLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
            await conn.OpenAsync(ct);
            await EnsureSchemaOnConnectionAsync(conn, ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE quizzes
                SET in_progress_index = NULL,
                    in_progress_answers_json = NULL
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", quizId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            DbLock.Release();
        }
    }

    private static Dictionary<string, T> TryDeserializeDictionary<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, T>>(json);
            return parsed is not null
                ? new Dictionary<string, T>(parsed, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static async Task UpdateQuizSessionScoreAsync(
        string basinRoot,
        QuizSessionRecord quiz,
        double scorePercent,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(basinRoot))
            return;

        quiz.AttemptCount++;
        quiz.LastScorePercent = Math.Round(scorePercent, 1);

        await DbLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
            await conn.OpenAsync(ct);
            await EnsureSchemaOnConnectionAsync(conn, ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE quizzes
                SET attempt_count = $cnt,
                    last_score_percent = $score
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$cnt", quiz.AttemptCount);
            cmd.Parameters.AddWithValue("$score", quiz.LastScorePercent.Value);
            cmd.Parameters.AddWithValue("$id", quiz.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static async Task DeleteQuizSessionAsync(
        string basinRoot,
        string quizId,
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
                DELETE FROM quiz_attempts WHERE quiz_id = $id;
                DELETE FROM quiz_questions WHERE quiz_id = $id;
                DELETE FROM quizzes WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", quizId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static IReadOnlyList<string> GetWeakTopicTags(
        string basinRoot,
        string? courseFolder = null,
        int limit = 8
    )
    {
        if (string.IsNullOrWhiteSpace(basinRoot) || !File.Exists(GetDatabasePath(basinRoot)))
            return [];

        try
        {
            using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = string.IsNullOrWhiteSpace(courseFolder)
                ? """
                    SELECT topic_tag
                    FROM quiz_questions
                    WHERE srs_lapses > 0 OR last_answered_correct = 0
                    GROUP BY topic_tag
                    ORDER BY SUM(srs_lapses) DESC, COUNT(*) DESC
                    LIMIT $limit;
                    """
                : """
                    SELECT qq.topic_tag
                    FROM quiz_questions qq
                    JOIN quizzes q ON q.id = qq.quiz_id
                    WHERE q.course_folder = $cf AND (qq.srs_lapses > 0 OR qq.last_answered_correct = 0)
                    GROUP BY qq.topic_tag
                    ORDER BY SUM(qq.srs_lapses) DESC, COUNT(*) DESC
                    LIMIT $limit;
                    """;

            if (!string.IsNullOrWhiteSpace(courseFolder))
                cmd.Parameters.AddWithValue("$cf", courseFolder);
            cmd.Parameters.AddWithValue("$limit", limit);

            var tags = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var tag = r.GetString(0);
                if (!string.IsNullOrWhiteSpace(tag))
                    tags.Add(tag);
            }
            return tags;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Updates stored <c>source_pdf_path</c> values in both <c>quizzes</c> and <c>quiz_questions</c>
    /// after a course PDF file or module folder is renamed, moved, or reordered on disk.
    /// </summary>
    public static int RemapQuizSourcePdfPaths(
        string basinRoot,
        string oldPathOrPrefix,
        string newPathOrPrefix,
        bool isPrefix
    )
    {
        if (
            string.IsNullOrWhiteSpace(basinRoot)
            || string.IsNullOrWhiteSpace(oldPathOrPrefix)
            || string.IsNullOrWhiteSpace(newPathOrPrefix)
            || string.Equals(oldPathOrPrefix, newPathOrPrefix, StringComparison.Ordinal)
        )
            return 0;

        DbLock.Wait();
        try
        {
            using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
            conn.Open();
            EnsureSchemaOnConnectionAsync(conn, CancellationToken.None).GetAwaiter().GetResult();

            using var tx = conn.BeginTransaction();
            int updated = 0;

            foreach (var table in new[] { "quizzes", "quiz_questions" })
            {
                var rows = new List<(string Id, string OldPath)>();
                using (var sel = conn.CreateCommand())
                {
                    sel.Transaction = tx;
                    sel.CommandText =
                        $"SELECT id, source_pdf_path FROM {table} WHERE source_pdf_path != '';";
                    using var reader = sel.ExecuteReader();
                    while (reader.Read())
                        rows.Add((reader.GetString(0), reader.GetString(1)));
                }

                using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = $"UPDATE {table} SET source_pdf_path = $newPath WHERE id = $id;";
                var pNew = upd.Parameters.Add("$newPath", SqliteType.Text);
                var pId = upd.Parameters.Add("$id", SqliteType.Text);

                foreach (var (id, currentPath) in rows)
                {
                    string? remapped = TryRemapPath(
                        currentPath,
                        oldPathOrPrefix,
                        newPathOrPrefix,
                        isPrefix
                    );
                    if (remapped is null)
                        continue;

                    pNew.Value = remapped;
                    pId.Value = id;
                    updated += upd.ExecuteNonQuery();
                }
            }

            tx.Commit();
            return updated;
        }
        catch
        {
            return 0;
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static string? TryRemapPath(
        string currentPath,
        string oldPathOrPrefix,
        string newPathOrPrefix,
        bool isPrefix
    )
    {
        if (string.IsNullOrWhiteSpace(currentPath))
            return null;

        if (!isPrefix)
        {
            if (string.Equals(currentPath, oldPathOrPrefix, StringComparison.OrdinalIgnoreCase))
                return newPathOrPrefix;
            return null;
        }

        var oldNorm = oldPathOrPrefix.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
        );
        var newNorm = newPathOrPrefix.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
        );

        if (
            currentPath.Length > oldNorm.Length
            && currentPath.StartsWith(oldNorm, StringComparison.OrdinalIgnoreCase)
            && (
                currentPath[oldNorm.Length] == Path.DirectorySeparatorChar
                || currentPath[oldNorm.Length] == Path.AltDirectorySeparatorChar
            )
        )
        {
            return newNorm + currentPath[oldNorm.Length..];
        }

        return null;
    }

    private static List<T> TryDeserializeList<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
