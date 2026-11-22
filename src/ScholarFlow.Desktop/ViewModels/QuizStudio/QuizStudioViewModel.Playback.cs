using ScholarFlow.Core.Database;
using ScholarFlow.Core.Quiz;

namespace ScholarFlow.Desktop.ViewModels;

public sealed partial class QuizStudioViewModel
{
    public void StartQuizSession(
        QuizSessionRecord session,
        bool? shuffleQuestionsOverride = null,
        bool? shuffleOptionsOverride = null,
        bool missedOnly = false,
        bool resumeSavedProgress = false
    )
    {
        bool doShuffleQ =
            !resumeSavedProgress && (shuffleQuestionsOverride ?? ShuffleQuestionsOnStart);
        bool doShuffleOpt = shuffleOptionsOverride ?? ShuffleOptionsOnStart;
        var rng = new Random();

        var sourceQuestions = missedOnly
            ? session.Questions.Where(q => q.LastAnsweredCorrect == false).ToList()
            : [.. session.Questions];
        if (sourceQuestions.Count == 0)
            sourceQuestions = [.. session.Questions];

        if (doShuffleQ && sourceQuestions.Count > 1)
            sourceQuestions = [.. sourceQuestions.OrderBy(_ => rng.Next())];

        OrderingDrafts.Clear();
        var playbackQuestions = new List<QuizQuestionRecord>(sourceQuestions.Count);
        foreach (var orig in sourceQuestions)
        {
            var opts = new List<string>(orig.Options);
            if (
                doShuffleOpt
                && orig.Kind is QuizQuestionKind.MultipleChoice or QuizQuestionKind.Situational
                && opts.Count > 1
            )
            {
                opts = [.. opts.OrderBy(_ => rng.Next())];
            }

            if (orig.Kind == QuizQuestionKind.Ordering && orig.Options.Count >= 2)
            {
                var shuffledSteps = orig.Options.OrderBy(_ => rng.Next()).ToList();
                // Ensure ordering steps don't start already in exact canonical order when possible
                if (
                    shuffledSteps.SequenceEqual(orig.Options, StringComparer.OrdinalIgnoreCase)
                    && shuffledSteps.Count >= 2
                )
                    (shuffledSteps[0], shuffledSteps[^1]) = (shuffledSteps[^1], shuffledSteps[0]);
                OrderingDrafts[orig.Id] = shuffledSteps;
            }

            playbackQuestions.Add(
                new QuizQuestionRecord
                {
                    Id = orig.Id,
                    QuizId = orig.QuizId,
                    OrderIndex = orig.OrderIndex,
                    Kind = orig.Kind,
                    CognitiveLevel = orig.CognitiveLevel,
                    Difficulty = orig.Difficulty,
                    TopicTag = orig.TopicTag,
                    Prompt = orig.Prompt,
                    CodeSnippet = orig.CodeSnippet,
                    CodeLanguage = orig.CodeLanguage,
                    MathLatex = orig.MathLatex,
                    Diagram = orig.Diagram,
                    ImagePath = orig.ImagePath,
                    Options = opts,
                    CorrectAnswer = orig.CorrectAnswer,
                    AcceptedAliases = [.. orig.AcceptedAliases],
                    Explanation = orig.Explanation,
                    Hint = orig.Hint,
                    Archetype = orig.Archetype,
                    SourcePdfPath = orig.SourcePdfPath,
                    SourcePage = orig.SourcePage,
                    SourceStartLine = orig.SourceStartLine,
                    SourceEndLine = orig.SourceEndLine,
                    SectionTitle = orig.SectionTitle,
                    VerbatimQuote = orig.VerbatimQuote,
                    HighlightRects = [.. orig.HighlightRects],
                    EvidenceAnchors = [.. orig.EvidenceAnchors],
                    SrsIntervalDays = orig.SrsIntervalDays,
                    SrsEaseFactor = orig.SrsEaseFactor,
                    SrsDueDate = orig.SrsDueDate,
                    SrsReps = orig.SrsReps,
                    SrsLapses = orig.SrsLapses,
                    LastAnsweredCorrect = orig.LastAnsweredCorrect,
                }
            );
        }

        ActiveSession = new QuizSessionRecord
        {
            Id = session.Id,
            CourseFolder = session.CourseFolder,
            CourseTitle = session.CourseTitle,
            Term = session.Term,
            Title = session.Title,
            SourcePdfPath = session.SourcePdfPath,
            SourcePdfMtimeTicks = session.SourcePdfMtimeTicks,
            ModelUsed = session.ModelUsed,
            CreatedAtUtc = session.CreatedAtUtc,
            AttemptCount = session.AttemptCount,
            LastScorePercent = session.LastScorePercent,
            Questions = playbackQuestions,
        };
        SessionResults.Clear();

        if (resumeSavedProgress && session.HasInProgressAttempt && playbackQuestions.Count > 0)
        {
            foreach (var (qid, ans) in session.InProgressAnswers)
                SessionResults[qid] = (ans.Answer, ans.IsCorrect);

            CurrentQuestionIndex = Math.Clamp(
                session.InProgressQuestionIndex ?? 0,
                0,
                playbackQuestions.Count - 1
            );
            var cur = CurrentQuestion;
            if (cur is not null && SessionResults.TryGetValue(cur.Id, out var prev))
            {
                CurrentAnswerDraft = prev.Answer;
                IsCurrentAnswerCorrect = prev.IsCorrect;
                IsAnswerRevealed = true;
            }
            else
            {
                CurrentAnswerDraft =
                    cur is { Kind: QuizQuestionKind.Ordering }
                    && OrderingDrafts.TryGetValue(cur.Id, out var initSteps)
                        ? string.Join(" -> ", initSteps)
                        : string.Empty;
                IsAnswerRevealed = false;
                IsCurrentAnswerCorrect = false;
            }
        }
        else
        {
            CurrentQuestionIndex = 0;
            CurrentAnswerDraft =
                playbackQuestions.FirstOrDefault() is { Kind: QuizQuestionKind.Ordering } firstOrd
                && OrderingDrafts.TryGetValue(firstOrd.Id, out var initSteps)
                    ? string.Join(" -> ", initSteps)
                    : string.Empty;
            IsAnswerRevealed = false;
            IsCurrentAnswerCorrect = false;
            session.InProgressQuestionIndex = null;
            session.InProgressAnswers.Clear();
            if (!string.IsNullOrWhiteSpace(_mainVm.BasinRoot) && session.Id != "srs-deck")
                _ = ScholarFlowDatabase.ClearQuizInProgressAsync(_mainVm.BasinRoot, session.Id);
        }

        IsShowingResultsSummary = false;
        ResetPerQuestionSuperEngineState();
        _mainVm.RequestUiRefresh();
    }

    public IReadOnlyList<string> GetOrderingItemsForQuestion(QuizQuestionRecord q)
    {
        if (OrderingDrafts.TryGetValue(q.Id, out var steps) && steps.Count > 0)
            return steps;

        var rng = new Random();
        var list =
            q.Options.Count >= 2
                ? q.Options.OrderBy(_ => rng.Next()).ToList()
                :
                [
                    .. q.CorrectAnswer.Split(
                        "->",
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    ),
                ];
        if (list.SequenceEqual(q.Options, StringComparer.OrdinalIgnoreCase) && list.Count >= 2)
            (list[0], list[^1]) = (list[^1], list[0]);

        OrderingDrafts[q.Id] = list;
        return list;
    }

    public void MoveOrderingStep(QuizQuestionRecord q, int index, int delta)
    {
        if (IsAnswerRevealed)
            return;

        var list = GetOrderingItemsForQuestion(q).ToList();
        int target = index + delta;
        if (index < 0 || index >= list.Count || target < 0 || target >= list.Count)
            return;

        (list[index], list[target]) = (list[target], list[index]);
        OrderingDrafts[q.Id] = list;
        CurrentAnswerDraft = string.Join(" -> ", list);
        _mainVm.RequestUiRefresh();
    }

    public void StartDueSpacedRepetitionDeck(string? courseFolderFilter = null)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var dueQuestions = SavedQuizzes
            .Where(q =>
                string.IsNullOrWhiteSpace(courseFolderFilter)
                || q.CourseFolder.Equals(courseFolderFilter, StringComparison.OrdinalIgnoreCase)
            )
            .SelectMany(q => q.Questions)
            .Where(qq =>
                !qq.SrsDueDate.HasValue
                || qq.SrsDueDate.Value <= today
                || qq.LastAnsweredCorrect == false
            )
            .OrderBy(qq => qq.LastAnsweredCorrect == false ? 0 : 1)
            .ThenBy(qq => qq.SrsDueDate ?? today)
            .Take(25)
            .ToList();

        if (dueQuestions.Count == 0)
        {
            StatusMessage = "No due spaced-repetition cards right now. Start any saved quiz below!";
            IsStatusError = false;
            _mainVm.RequestUiRefresh();
            return;
        }

        var deckSession = new QuizSessionRecord
        {
            Id = "srs-deck",
            CourseFolder = courseFolderFilter ?? SelectedCourse?.FolderName ?? "",
            CourseTitle = SelectedCourse?.Title ?? "All Courses",
            Title = $"Active Recall & Spaced Repetition Deck ({dueQuestions.Count} due)",
            SourcePdfPath = dueQuestions[0].SourcePdfPath,
            ModelUsed = "SM-2 / FSRS Scheduler",
            Questions = dueQuestions,
        };

        StartQuizSession(deckSession);
    }

    public void ExitActiveSession()
    {
        ActiveSession = null;
        IsShowingResultsSummary = false;
        ResetPerQuestionSuperEngineState();
        _mainVm.RequestUiRefresh();
    }

    public async Task SubmitAnswerAsync(string answer, int qualityRating = 4)
    {
        var q = CurrentQuestion;
        if (q is null)
            return;

        CurrentAnswerDraft = answer;
        bool isCorrect = q.EvaluateAnswer(answer);
        IsCurrentAnswerCorrect = isCorrect;
        IsAnswerRevealed = true;
        SessionResults[q.Id] = (answer, isCorrect);

        var appliedConfidence = UseConfidenceCalibration
            ? SelectedConfidence
            : ConfidenceLevel.None;
        await ScholarFlowDatabase.RecordQuestionAttemptAsync(
            _mainVm.BasinRoot,
            q,
            answer,
            isCorrect,
            isCorrect ? qualityRating : 1,
            appliedConfidence
        );
        LastConfidenceFeedback = FormatConfidenceCalibrationFeedback(
            appliedConfidence,
            isCorrect,
            q.SrsIntervalDays
        );

        await PersistActiveInProgressAsync();
        _mainVm.RequestUiRefresh();
    }

    private async Task PersistActiveInProgressAsync()
    {
        if (
            ActiveSession is null
            || ActiveSession.Id == "srs-deck"
            || string.IsNullOrWhiteSpace(_mainVm.BasinRoot)
        )
            return;

        var dict = SessionResults.ToDictionary(
            kv => kv.Key,
            kv => new QuizInProgressAnswer(kv.Value.Answer, kv.Value.IsCorrect),
            StringComparer.OrdinalIgnoreCase
        );

        var savedTarget = SavedQuizzes.FirstOrDefault(s =>
            string.Equals(s.Id, ActiveSession.Id, StringComparison.OrdinalIgnoreCase)
        );
        if (savedTarget is not null)
        {
            savedTarget.InProgressQuestionIndex = CurrentQuestionIndex;
            savedTarget.InProgressAnswers = dict;
        }

        await ScholarFlowDatabase.SaveQuizInProgressAsync(
            _mainVm.BasinRoot,
            ActiveSession.Id,
            CurrentQuestionIndex,
            dict
        );
    }

    public async Task RateAndAdvanceAsync(int qualityRating)
    {
        var q = CurrentQuestion;
        if (q is not null && IsAnswerRevealed)
        {
            await ScholarFlowDatabase.RecordQuestionAttemptAsync(
                _mainVm.BasinRoot,
                q,
                CurrentAnswerDraft,
                IsCurrentAnswerCorrect,
                qualityRating
            );
        }

        await AdvanceNextQuestionAsync();
    }

    public async Task AdvanceNextQuestionAsync()
    {
        if (ActiveSession is null)
            return;

        if (CurrentQuestionIndex + 1 < ActiveSession.Questions.Count)
        {
            CurrentQuestionIndex++;
            ResetPerQuestionSuperEngineState();
            var nextQ = CurrentQuestion;
            if (nextQ is not null && SessionResults.TryGetValue(nextQ.Id, out var prev))
            {
                CurrentAnswerDraft = prev.Answer;
                IsCurrentAnswerCorrect = prev.IsCorrect;
                IsAnswerRevealed = true;
            }
            else
            {
                CurrentAnswerDraft =
                    nextQ is { Kind: QuizQuestionKind.Ordering }
                    && OrderingDrafts.TryGetValue(nextQ.Id, out var steps)
                        ? string.Join(" -> ", steps)
                        : string.Empty;
                IsCurrentAnswerCorrect = false;
                IsAnswerRevealed = false;
            }
            await PersistActiveInProgressAsync();
        }
        else
        {
            IsShowingResultsSummary = true;
            if (ActiveSession.Id != "srs-deck" && ActiveSession.Questions.Count > 0)
            {
                int correct = ActiveSession.Questions.Count(qq =>
                    SessionResults.TryGetValue(qq.Id, out var r) && r.IsCorrect
                );
                double pct = 100.0 * correct / ActiveSession.Questions.Count;
                await ScholarFlowDatabase.ClearQuizInProgressAsync(
                    _mainVm.BasinRoot,
                    ActiveSession.Id
                );
                await ScholarFlowDatabase.UpdateQuizSessionScoreAsync(
                    _mainVm.BasinRoot,
                    ActiveSession,
                    pct
                );
                SavedQuizzes = await ScholarFlowDatabase.LoadQuizSessionsAsync(_mainVm.BasinRoot);
            }
        }

        _mainVm.RequestUiRefresh();
    }

    public void PreviousQuestion()
    {
        if (ActiveSession is null || CurrentQuestionIndex <= 0)
            return;

        CurrentQuestionIndex--;
        ResetPerQuestionSuperEngineState();
        var q = CurrentQuestion;
        if (q is not null && SessionResults.TryGetValue(q.Id, out var prev))
        {
            CurrentAnswerDraft = prev.Answer;
            IsCurrentAnswerCorrect = prev.IsCorrect;
            IsAnswerRevealed = true;
        }
        else
        {
            CurrentAnswerDraft =
                q is { Kind: QuizQuestionKind.Ordering }
                && OrderingDrafts.TryGetValue(q.Id, out var steps)
                    ? string.Join(" -> ", steps)
                    : string.Empty;
            IsAnswerRevealed = false;
        }
        _ = PersistActiveInProgressAsync();
        _mainVm.RequestUiRefresh();
    }

    public void JumpToQuestion(int index)
    {
        if (ActiveSession is null || index < 0 || index >= ActiveSession.Questions.Count)
            return;

        CurrentQuestionIndex = index;
        ResetPerQuestionSuperEngineState();
        var q = CurrentQuestion;
        if (q is not null && SessionResults.TryGetValue(q.Id, out var prev))
        {
            CurrentAnswerDraft = prev.Answer;
            IsCurrentAnswerCorrect = prev.IsCorrect;
            IsAnswerRevealed = true;
        }
        else
        {
            CurrentAnswerDraft =
                q is { Kind: QuizQuestionKind.Ordering }
                && OrderingDrafts.TryGetValue(q.Id, out var steps)
                    ? string.Join(" -> ", steps)
                    : string.Empty;
            IsCurrentAnswerCorrect = false;
            IsAnswerRevealed = false;
        }
        _ = PersistActiveInProgressAsync();
        _mainVm.RequestUiRefresh();
    }
}
