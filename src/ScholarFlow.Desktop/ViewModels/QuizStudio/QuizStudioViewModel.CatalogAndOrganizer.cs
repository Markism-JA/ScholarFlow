using ScholarFlow.Core.Database;
using ScholarFlow.Core.Quiz;

namespace ScholarFlow.Desktop.ViewModels;

public sealed partial class QuizStudioViewModel
{
    public void ToggleCatalogShowAllCourses()
    {
        CatalogShowAllCourses = !CatalogShowAllCourses;
        _mainVm.RequestUiRefresh();
    }

    public void SetCatalogTermFilter(string? term)
    {
        CatalogTermFilter = string.Equals(
            CatalogTermFilter,
            term,
            StringComparison.OrdinalIgnoreCase
        )
            ? null
            : term;
        _mainVm.RequestUiRefresh();
    }

    public void ToggleShuffleQuestionsOnStart()
    {
        ShuffleQuestionsOnStart = !ShuffleQuestionsOnStart;
        _mainVm.RequestUiRefresh();
    }

    public void ToggleShuffleOptionsOnStart()
    {
        ShuffleOptionsOnStart = !ShuffleOptionsOnStart;
        _mainVm.RequestUiRefresh();
    }

    public void ToggleQuizSelectionForMerge(string quizId)
    {
        if (!SelectedQuizIdsForMerge.Add(quizId))
            SelectedQuizIdsForMerge.Remove(quizId);
        _mainVm.RequestUiRefresh();
    }

    public async Task CombineSelectedQuizzesAsync(string? customTitle, bool shuffleQuestions)
    {
        var selectedQuizzes = SavedQuizzes
            .Where(q => SelectedQuizIdsForMerge.Contains(q.Id))
            .ToList();

        if (selectedQuizzes.Count < 2)
        {
            StatusMessage = "Select at least 2 saved quizzes in the catalog to combine.";
            IsStatusError = true;
            _mainVm.RequestUiRefresh();
            return;
        }

        var combinedQuestions = new List<QuizQuestionRecord>();
        foreach (var qz in selectedQuizzes)
        {
            foreach (var orig in qz.Questions)
            {
                combinedQuestions.Add(
                    new QuizQuestionRecord
                    {
                        Id = Guid.NewGuid().ToString("N")[..12],
                        Kind = orig.Kind,
                        CognitiveLevel = orig.CognitiveLevel,
                        Difficulty = orig.Difficulty,
                        TopicTag = orig.TopicTag,
                        Prompt = orig.Prompt,
                        Options = [.. orig.Options],
                        CorrectAnswer = orig.CorrectAnswer,
                        AcceptedAliases = [.. orig.AcceptedAliases],
                        Explanation = orig.Explanation,
                        Hint = orig.Hint,
                        Archetype = orig.Archetype,
                        CodeSnippet = orig.CodeSnippet,
                        CodeLanguage = orig.CodeLanguage,
                        MathLatex = orig.MathLatex,
                        Diagram = orig.Diagram,
                        ImagePath = orig.ImagePath,
                        SourcePdfPath = orig.SourcePdfPath,
                        SourcePage = orig.SourcePage,
                        SourceStartLine = orig.SourceStartLine,
                        SourceEndLine = orig.SourceEndLine,
                        SectionTitle = orig.SectionTitle,
                        VerbatimQuote = orig.VerbatimQuote,
                        HighlightRects = [.. orig.HighlightRects],
                        EvidenceAnchors = [.. orig.EvidenceAnchors],
                        SrsDueDate = orig.SrsDueDate ?? DateOnly.FromDateTime(DateTime.Today),
                    }
                );
            }
        }

        if (shuffleQuestions)
        {
            var rng = new Random();
            combinedQuestions = [.. combinedQuestions.OrderBy(_ => rng.Next())];
        }

        var first = selectedQuizzes[0];
        var distinctTerms = selectedQuizzes
            .Select(q => q.Term)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var mergedSession = new QuizSessionRecord
        {
            CourseFolder = selectedQuizzes.All(q => q.CourseFolder == first.CourseFolder)
                ? first.CourseFolder
                : (SelectedCourse?.FolderName ?? first.CourseFolder),
            CourseTitle = selectedQuizzes.All(q => q.CourseTitle == first.CourseTitle)
                ? first.CourseTitle
                : "Combined Multi-Subject Assessment",
            Term = distinctTerms.Count == 1 ? distinctTerms[0] : "Combined",
            Title = !string.IsNullOrWhiteSpace(customTitle)
                ? customTitle.Trim()
                : $"Combined Exam ({string.Join(" + ", selectedQuizzes.Select(q => q.Term ?? q.Title).Take(3))})",
            SourcePdfPath = first.SourcePdfPath,
            ModelUsed = "Combined Catalog",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Questions = combinedQuestions,
        };

        await ScholarFlowDatabase.SaveQuizSessionAsync(_mainVm.BasinRoot, mergedSession);
        SelectedQuizIdsForMerge.Clear();
        await RefreshAsync(SelectedCourse, SelectedPdfPath);
        StatusMessage =
            $"Combined {selectedQuizzes.Count} quizzes into '{mergedSession.Title}' ({combinedQuestions.Count} questions).";
        IsStatusError = false;
        StartQuizSession(mergedSession);
    }

    public async Task DeleteQuizAsync(QuizSessionRecord quiz)
    {
        await ScholarFlowDatabase.DeleteQuizSessionAsync(_mainVm.BasinRoot, quiz.Id);
        if (ActiveSession?.Id == quiz.Id)
            ActiveSession = null;
        await RefreshAsync(SelectedCourse, SelectedPdfPath);
        StatusMessage = $"Deleted quiz '{quiz.Title}'.";
        _mainVm.RequestUiRefresh();
    }

    public void RemapSourcePdfPaths(string oldPathOrPrefix, string newPathOrPrefix, bool isPrefix)
    {
        void RemapSession(QuizSessionRecord? session)
        {
            if (session is null)
                return;

            var remappedSession = ScholarFlowDatabase.TryRemapPath(
                session.SourcePdfPath,
                oldPathOrPrefix,
                newPathOrPrefix,
                isPrefix
            );
            if (remappedSession is not null)
                session.SourcePdfPath = remappedSession;

            foreach (var q in session.Questions)
            {
                var remappedQ = ScholarFlowDatabase.TryRemapPath(
                    q.SourcePdfPath,
                    oldPathOrPrefix,
                    newPathOrPrefix,
                    isPrefix
                );
                if (remappedQ is not null)
                    q.SourcePdfPath = remappedQ;
            }
        }

        foreach (var q in SavedQuizzes)
            RemapSession(q);
        RemapSession(OrganizerQuiz);
        RemapSession(ActiveSession);

        if (!string.IsNullOrWhiteSpace(SelectedPdfPath))
        {
            var remappedSelected = ScholarFlowDatabase.TryRemapPath(
                SelectedPdfPath,
                oldPathOrPrefix,
                newPathOrPrefix,
                isPrefix
            );
            if (remappedSelected is not null)
                SelectedPdfPath = remappedSelected;
        }
    }

    public async Task RegroundQuizAsync(QuizSessionRecord quiz)
    {
        if (
            !string.IsNullOrWhiteSpace(quiz.SourcePdfPath)
            && !quiz.SourcePdfPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
        )
        {
            StatusMessage =
                "Re-grounding applies to PDF sources only; code/text evidence uses exact line ranges.";
            IsStatusError = false;
            _mainVm.RequestUiRefresh();
            return;
        }

        if (string.IsNullOrWhiteSpace(quiz.SourcePdfPath) || !File.Exists(quiz.SourcePdfPath))
        {
            var fallbackMaster =
                !string.IsNullOrWhiteSpace(_mainVm.BasinRoot)
                && !string.IsNullOrWhiteSpace(quiz.CourseFolder)
                    ? Core.Pdf.MasterPdfService.FindExistingMasterPdfPath(
                        _mainVm.BasinRoot,
                        quiz.CourseFolder
                    )
                    : null;

            if (!string.IsNullOrWhiteSpace(fallbackMaster) && File.Exists(fallbackMaster))
            {
                quiz.SourcePdfPath = fallbackMaster;
            }
            else
            {
                StatusMessage = $"Cannot re-ground: PDF not found at {quiz.SourcePdfPath}";
                IsStatusError = true;
                _mainVm.RequestUiRefresh();
                return;
            }
        }

        IsGenerating = true;
        IsStatusError = false;
        StatusMessage =
            $"Re-grounding '{quiz.Title}' against {Path.GetFileName(quiz.SourcePdfPath)}...";
        _mainVm.RequestUiRefresh();
        await Task.Yield();

        try
        {
            var pdfOnlyQuestions = quiz.Questions.Where(q => q.IsPdfSource).ToList();
            int matched = await Task.Run(() =>
            {
                if (pdfOnlyQuestions.Count == quiz.Questions.Count)
                    return PdfQuizGrounder.RegroundSessionToPdf(quiz);

                PdfQuizGrounder.GroundQuestionsToPdf(
                    quiz.SourcePdfPath,
                    pdfOnlyQuestions,
                    forceUpdateSection: true
                );
                if (File.Exists(quiz.SourcePdfPath))
                    quiz.SourcePdfMtimeTicks = File.GetLastWriteTimeUtc(quiz.SourcePdfPath).Ticks;
                return pdfOnlyQuestions.Count(q => q.HighlightRects.Count > 0);
            });
            await ScholarFlowDatabase.SaveQuizSessionAsync(_mainVm.BasinRoot, quiz);
            SavedQuizzes = await ScholarFlowDatabase.LoadQuizSessionsAsync(_mainVm.BasinRoot);
            if (OrganizerQuiz?.Id == quiz.Id)
                OrganizerQuiz = SavedQuizzes.FirstOrDefault(x => x.Id == quiz.Id) ?? quiz;

            StatusMessage =
                $"Re-grounded '{quiz.Title}' against updated PDF ({matched}/{quiz.Questions.Count} statements anchored).";
            IsStatusError = false;
        }
        finally
        {
            IsGenerating = false;
            _mainVm.RequestUiRefresh();
        }
    }

    public async Task RelinkAndRegroundQuizAsync(QuizSessionRecord quiz, string newPdfPath)
    {
        if (string.IsNullOrWhiteSpace(newPdfPath) || !File.Exists(newPdfPath))
        {
            StatusMessage = "Selected PDF file does not exist.";
            IsStatusError = true;
            _mainVm.RequestUiRefresh();
            return;
        }

        quiz.SourcePdfPath = newPdfPath;
        foreach (var q in quiz.Questions)
            q.SourcePdfPath = newPdfPath;

        if (OrganizerQuiz?.Id == quiz.Id)
        {
            OrganizerQuiz.SourcePdfPath = newPdfPath;
            foreach (var q in OrganizerQuiz.Questions)
                q.SourcePdfPath = newPdfPath;
        }

        await RegroundQuizAsync(quiz);
    }

    // =========================================================================
    // DECK ORGANIZER (SINGLE & MULTI-QUIZ QUESTION REORGANIZATION)
    // =========================================================================

    public void OpenQuizInOrganizer(QuizSessionRecord quiz)
    {
        OrganizerQuiz = new QuizSessionRecord
        {
            Id = quiz.Id,
            CourseFolder = quiz.CourseFolder,
            CourseTitle = quiz.CourseTitle,
            Term = quiz.Term,
            Title = quiz.Title,
            SourcePdfPath = quiz.SourcePdfPath,
            SourcePdfMtimeTicks = quiz.SourcePdfMtimeTicks,
            ModelUsed = quiz.ModelUsed,
            CreatedAtUtc = quiz.CreatedAtUtc,
            AttemptCount = quiz.AttemptCount,
            LastScorePercent = quiz.LastScorePercent,
            Questions = [.. quiz.Questions.Select(CloneQuestionForEdit)],
        };
        OrganizerKindFilter = null;
        OrganizerTopicFilter = null;
        OrganizerHealthFilter = OrganizerHealthFilter.All;
        OrganizerSearchQuery = string.Empty;
        SelectedOrganizerQuestionId = OrganizerQuiz.Questions.FirstOrDefault()?.Id;
        IsOrganizerDirty = false;
        EditingQuestionId = null;
        EditingQuestionDraft = null;
        EditingPageTextPreview = null;
        EditingGroundStatus = null;
        IsEditingAdvancedOpen = false;
        IsDeckAiFixModalOpen = false;
        DeckAiFixSelectedIds.Clear();
        ActiveSubTab = QuizStudioSubTab.Organizer;
        StatusMessage =
            $"Organizing '{quiz.Title}' ({quiz.Questions.Count} items). Reorder, filter by kind, edit, or shuffle.";
        IsStatusError = false;
        _mainVm.RequestUiRefresh();
    }

    public void OpenSelectedQuizzesInOrganizer(string? customTitle = null)
    {
        var selected = SavedQuizzes.Where(q => SelectedQuizIdsForMerge.Contains(q.Id)).ToList();
        if (selected.Count == 0)
        {
            StatusMessage =
                "Select 1 or more quizzes in the catalog to open in the Deck Organizer.";
            IsStatusError = true;
            _mainVm.RequestUiRefresh();
            return;
        }

        if (selected.Count == 1)
        {
            OpenQuizInOrganizer(selected[0]);
            return;
        }

        var first = selected[0];
        var distinctTerms = selected
            .Select(q => q.Term)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var mergedQuestions = new List<QuizQuestionRecord>();
        foreach (var qz in selected)
        {
            foreach (var orig in qz.Questions)
            {
                var copy = CloneQuestionForEdit(orig);
                copy.Id = Guid.NewGuid().ToString("N")[..12];
                copy.OrderIndex = mergedQuestions.Count;
                mergedQuestions.Add(copy);
            }
        }

        OrganizerQuiz = new QuizSessionRecord
        {
            CourseFolder = selected.All(q => q.CourseFolder == first.CourseFolder)
                ? first.CourseFolder
                : (SelectedCourse?.FolderName ?? first.CourseFolder),
            CourseTitle = selected.All(q => q.CourseTitle == first.CourseTitle)
                ? first.CourseTitle
                : "Combined Multi-Subject Assessment",
            Term = distinctTerms.Count == 1 ? distinctTerms[0] : "Combined",
            Title = !string.IsNullOrWhiteSpace(customTitle)
                ? customTitle.Trim()
                : $"Combined Deck ({string.Join(" + ", selected.Select(q => q.Term ?? q.Title).Take(3))})",
            SourcePdfPath = first.SourcePdfPath,
            SourcePdfMtimeTicks = first.SourcePdfMtimeTicks,
            ModelUsed = "Curated Multi-Deck",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Questions = mergedQuestions,
        };
        OrganizerKindFilter = null;
        OrganizerTopicFilter = null;
        OrganizerHealthFilter = OrganizerHealthFilter.All;
        OrganizerSearchQuery = string.Empty;
        SelectedOrganizerQuestionId = OrganizerQuiz.Questions.FirstOrDefault()?.Id;
        IsOrganizerDirty = false;
        EditingQuestionId = null;
        EditingQuestionDraft = null;
        EditingPageTextPreview = null;
        EditingGroundStatus = null;
        IsEditingAdvancedOpen = false;
        IsDeckAiFixModalOpen = false;
        DeckAiFixSelectedIds.Clear();
        ActiveSubTab = QuizStudioSubTab.Organizer;
        StatusMessage =
            $"Curating combined deck from {selected.Count} quizzes ({mergedQuestions.Count} items). Customize and click Save Deck.";
        IsStatusError = false;
        _mainVm.RequestUiRefresh();
    }

    private static QuizQuestionRecord CloneQuestionForEdit(QuizQuestionRecord orig) =>
        new()
        {
            Id = orig.Id,
            QuizId = orig.QuizId,
            OrderIndex = orig.OrderIndex,
            Kind = orig.Kind,
            CognitiveLevel = orig.CognitiveLevel,
            Difficulty = orig.Difficulty,
            TopicTag = orig.TopicTag,
            Prompt = orig.Prompt,
            Options = [.. orig.Options],
            CorrectAnswer = orig.CorrectAnswer,
            AcceptedAliases = [.. orig.AcceptedAliases],
            Explanation = orig.Explanation,
            Hint = orig.Hint,
            Archetype = orig.Archetype,
            CodeSnippet = orig.CodeSnippet,
            CodeLanguage = orig.CodeLanguage,
            MathLatex = orig.MathLatex,
            Diagram = orig.Diagram,
            ImagePath = orig.ImagePath,
            SourcePdfPath = orig.SourcePdfPath,
            SourcePage = orig.SourcePage,
            SourceStartLine = orig.SourceStartLine,
            SourceEndLine = orig.SourceEndLine,
            SectionTitle = orig.SectionTitle,
            VerbatimQuote = orig.VerbatimQuote,
            HighlightRects = [.. orig.HighlightRects],
            EvidenceAnchors =
            [
                .. orig.EvidenceAnchors.Select(a =>
                    a with
                    {
                        HighlightRects = [.. a.HighlightRects],
                    }
                ),
            ],
            SrsIntervalDays = orig.SrsIntervalDays,
            SrsEaseFactor = orig.SrsEaseFactor,
            SrsDueDate = orig.SrsDueDate,
            SrsReps = orig.SrsReps,
            SrsLapses = orig.SrsLapses,
            LastAnsweredCorrect = orig.LastAnsweredCorrect,
        };

    public void MarkOrganizerDirty()
    {
        IsOrganizerDirty = true;
        InvalidateOrganizerQualityCache();
    }

    public void SetOrganizerSearchQueryQuiet(string query)
    {
        OrganizerSearchQuery = query ?? string.Empty;
    }

    public void SetOrganizerSearchQuery(string query)
    {
        OrganizerSearchQuery = query ?? string.Empty;
        _mainVm.RequestUiRefresh();
    }

    public event Action<string?, bool, bool>? OrganizerSelectionChanged;

    public List<QuizQuestionRecord> GetVisibleOrganizerQuestions()
    {
        var deck = OrganizerQuiz;
        if (deck is null)
            return [];

        var qualityFlaggedIds =
            OrganizerHealthFilter == OrganizerHealthFilter.QualityIssues
                ? GetCachedOrganizerQualityReport().FlaggedQuestionIds
                : null;
        var qSearch = OrganizerSearchQuery.Trim();
        return
        [
            .. deck.Questions.Where(q =>
                (!OrganizerKindFilter.HasValue || q.Kind == OrganizerKindFilter.Value)
                && (
                    string.IsNullOrWhiteSpace(OrganizerTopicFilter)
                    || string.Equals(
                        q.TopicTag,
                        OrganizerTopicFilter,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                && OrganizerHealthFilter switch
                {
                    OrganizerHealthFilter.QualityIssues => qualityFlaggedIds is not null
                        && qualityFlaggedIds.Contains(q.Id),
                    OrganizerHealthFilter.MissingEvidence => string.IsNullOrWhiteSpace(
                        q.VerbatimQuote
                    ) || (q.IsPdfSource && q.HighlightRects.Count == 0),
                    OrganizerHealthFilter.HasRichMedia => !string.IsNullOrWhiteSpace(q.CodeSnippet)
                        || !string.IsNullOrWhiteSpace(q.MathLatex)
                        || !string.IsNullOrWhiteSpace(q.Diagram)
                        || !string.IsNullOrWhiteSpace(q.ImagePath),
                    OrganizerHealthFilter.FlaggedForAi => DeckAiFixSelectedIds.Contains(q.Id)
                        || string.Equals(
                            FlaggingQuestionId,
                            q.Id,
                            StringComparison.OrdinalIgnoreCase
                        ),
                    _ => true,
                }
                && (
                    string.IsNullOrEmpty(qSearch)
                    || Core.Services.QuickSearchMatcher.Matches(
                        qSearch,
                        q.Prompt,
                        q.CorrectAnswer,
                        q.TopicTag,
                        q.SectionTitle,
                        q.VerbatimQuote,
                        q.Kind.ToString(),
                        q.Options.Count > 0 ? string.Join(' ', q.Options) : null
                    )
                )
            ),
        ];
    }

    public void SelectOrganizerQuestion(
        string questionId,
        bool notify = true,
        bool scrollCardIntoView = false,
        bool scrollTocIntoView = false
    )
    {
        SelectedOrganizerQuestionId = questionId;
        OrganizerSelectionChanged?.Invoke(questionId, scrollCardIntoView, scrollTocIntoView);
        if (notify)
            _mainVm.RequestUiRefresh();
    }

    public void MoveOrganizerSelection(int delta)
    {
        var visible = GetVisibleOrganizerQuestions();
        if (visible.Count == 0)
            return;

        int currentIdx = visible.FindIndex(q =>
            string.Equals(q.Id, SelectedOrganizerQuestionId, StringComparison.OrdinalIgnoreCase)
        );
        int nextIdx =
            currentIdx < 0
                ? (delta >= 0 ? 0 : visible.Count - 1)
                : Math.Clamp(currentIdx + delta, 0, visible.Count - 1);

        var target = visible[nextIdx];
        SelectOrganizerQuestion(
            target.Id,
            notify: false,
            scrollCardIntoView: true,
            scrollTocIntoView: true
        );
    }

    public void JumpOrganizerSelection(bool toEnd)
    {
        var visible = GetVisibleOrganizerQuestions();
        if (visible.Count == 0)
            return;

        var target = toEnd ? visible[^1] : visible[0];
        SelectOrganizerQuestion(
            target.Id,
            notify: false,
            scrollCardIntoView: true,
            scrollTocIntoView: true
        );
    }

    public void SetOrganizerKindFilter(QuizQuestionKind? kind)
    {
        OrganizerKindFilter = OrganizerKindFilter == kind ? null : kind;
        _mainVm.RequestUiRefresh();
    }

    public void SetOrganizerTopicFilter(string? topic)
    {
        OrganizerTopicFilter = string.Equals(
            OrganizerTopicFilter,
            topic,
            StringComparison.OrdinalIgnoreCase
        )
            ? null
            : topic;
        _mainVm.RequestUiRefresh();
    }

    public void SetOrganizerHealthFilter(OrganizerHealthFilter health)
    {
        OrganizerHealthFilter =
            OrganizerHealthFilter == health ? OrganizerHealthFilter.All : health;
        _mainVm.RequestUiRefresh();
    }

    public void ToggleOrganizerToc()
    {
        IsOrganizerTocOpen = !IsOrganizerTocOpen;
        _mainVm.RequestUiRefresh();
    }

    public void SetOrganizerTocOpenQuiet(bool isOpen)
    {
        IsOrganizerTocOpen = isOpen;
    }

    public void ClearAllOrganizerFilters()
    {
        OrganizerKindFilter = null;
        OrganizerTopicFilter = null;
        OrganizerHealthFilter = OrganizerHealthFilter.All;
        OrganizerSearchQuery = string.Empty;
        _mainVm.RequestUiRefresh();
    }

    public void ToggleOrganizerFilters()
    {
        IsOrganizerFiltersOpen = !IsOrganizerFiltersOpen;
        _mainVm.RequestUiRefresh();
    }

    public void ToggleEditingAdvancedOpen()
    {
        IsEditingAdvancedOpen = !IsEditingAdvancedOpen;
        _mainVm.RequestUiRefresh();
    }

    public void ToggleEditQuestion(string questionId)
    {
        if (
            string.Equals(EditingQuestionId, questionId, StringComparison.OrdinalIgnoreCase)
            && EditingQuestionDraft is not null
        )
        {
            CancelEditingQuestionDraft();
            return;
        }

        var target = OrganizerQuiz?.Questions.FirstOrDefault(q =>
            string.Equals(q.Id, questionId, StringComparison.OrdinalIgnoreCase)
        );
        if (target is null)
            return;

        EditingQuestionId = target.Id;
        SelectedOrganizerQuestionId = target.Id;
        EditingQuestionDraft = CloneQuestionForEdit(target);
        if (
            EditingQuestionDraft.EvidenceAnchors.Count == 0
            && (
                EditingQuestionDraft.HighlightRects.Count > 0
                || !string.IsNullOrWhiteSpace(EditingQuestionDraft.VerbatimQuote)
            )
        )
        {
            EditingQuestionDraft.EvidenceAnchors.Add(
                new GroundedEvidenceAnchor(
                    EditingQuestionDraft.SourcePage,
                    EditingQuestionDraft.SectionTitle,
                    EditingQuestionDraft.VerbatimQuote,
                    [.. EditingQuestionDraft.HighlightRects]
                )
            );
        }
        EditingPageTextPreview = null;
        EditingSnipPageBitmap = null;
        EditingSnipPageNumber = 0;
        IsDedicatedSnipperOpen = false;
        EditingGroundStatus =
            target.HighlightRects.Count > 0
                ? $"✓ Highlighted ({target.HighlightRects.Count} box(es) on p.{target.SourcePage})"
                : null;
        IsEditingAdvancedOpen =
            !string.IsNullOrWhiteSpace(target.CodeSnippet)
            || !string.IsNullOrWhiteSpace(target.MathLatex)
            || !string.IsNullOrWhiteSpace(target.Diagram);
        _mainVm.RequestUiRefresh();
    }

    public async Task LoadEditingPageTextAsync()
    {
        var draft = EditingQuestionDraft;
        var pdfPath = !string.IsNullOrWhiteSpace(draft?.SourcePdfPath)
            ? draft.SourcePdfPath
            : OrganizerQuiz?.SourcePdfPath;
        if (draft is null || string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
        {
            EditingPageTextPreview = "Source PDF not found for this deck.";
            _mainVm.RequestUiRefresh();
            return;
        }

        int page = Math.Max(1, draft.SourcePage);
        EditingPageTextPreview = $"Loading page {page} text...";
        _mainVm.RequestUiRefresh();

        var extracted = await Task.Run(() =>
            PdfQuizGrounder.ExtractGroundedPages(
                pdfPath,
                startPage: page,
                endPage: page,
                maxPages: 1
            )
        );
        if (extracted.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(draft.SectionTitle))
                draft.SectionTitle = extracted[0].SectionBreadcrumb;
            EditingPageTextPreview = extracted[0].Text;
        }
        else
        {
            EditingPageTextPreview = $"No extractable text found on page {page}.";
        }
        _mainVm.RequestUiRefresh();
    }

    public void GroundEditingDraftToPdf()
    {
        var draft = EditingQuestionDraft;
        var pdfPath = !string.IsNullOrWhiteSpace(draft?.SourcePdfPath)
            ? draft.SourcePdfPath
            : OrganizerQuiz?.SourcePdfPath;
        if (draft is null || string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
        {
            EditingGroundStatus = "⚠ No source PDF available for grounding.";
            _mainVm.RequestUiRefresh();
            return;
        }

        draft.SourcePdfPath = pdfPath;
        PdfQuizGrounder.GroundQuestionsToPdf(pdfPath, [draft], forceUpdateSection: true);
        EditingGroundStatus =
            draft.HighlightRects.Count > 0
                ? $"✓ Grounded ({draft.HighlightRects.Count} highlight box(es) on p.{draft.SourcePage})"
                : $"⚠ Quote not matched on p.{draft.SourcePage} — check spelling or load page text.";
        _mainVm.RequestUiRefresh();
    }

    public void CommitEditingQuestionDraft(int moveDelta = 0)
    {
        if (OrganizerQuiz is null || EditingQuestionDraft is null)
            return;

        int idx = OrganizerQuiz.Questions.FindIndex(q =>
            string.Equals(q.Id, EditingQuestionDraft.Id, StringComparison.OrdinalIgnoreCase)
        );
        if (idx >= 0)
        {
            if (EditingQuestionDraft.Kind == QuizQuestionKind.TrueFalse)
            {
                EditingQuestionDraft.Options = ["True", "False"];
                if (
                    !string.Equals(
                        EditingQuestionDraft.CorrectAnswer,
                        "False",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                    EditingQuestionDraft.CorrectAnswer = "True";
            }
            else if (
                EditingQuestionDraft.Kind == QuizQuestionKind.Ordering
                && EditingQuestionDraft.Options.Count >= 2
            )
            {
                EditingQuestionDraft.CorrectAnswer = string.Join(
                    " -> ",
                    EditingQuestionDraft.Options
                );
            }
            else if (
                EditingQuestionDraft.Kind
                    is QuizQuestionKind.MultipleChoice
                        or QuizQuestionKind.Situational
                && EditingQuestionDraft.Options.Count > 0
                && !EditingQuestionDraft.Options.Any(o =>
                    string.Equals(
                        o.Trim(),
                        EditingQuestionDraft.CorrectAnswer.Trim(),
                        StringComparison.OrdinalIgnoreCase
                    )
                )
            )
            {
                EditingQuestionDraft.CorrectAnswer = EditingQuestionDraft.Options[0];
            }

            OrganizerQuiz.Questions[idx] = CloneQuestionForEdit(EditingQuestionDraft);
            IsOrganizerDirty = true;
            InvalidateOrganizerQualityCache();
        }

        if (moveDelta != 0 && idx >= 0)
        {
            int nextIdx = Math.Clamp(idx + moveDelta, 0, OrganizerQuiz.Questions.Count - 1);
            if (nextIdx != idx)
            {
                var nextQ = OrganizerQuiz.Questions[nextIdx];
                EditingQuestionId = nextQ.Id;
                SelectedOrganizerQuestionId = nextQ.Id;
                EditingQuestionDraft = CloneQuestionForEdit(nextQ);
                IsEditingAdvancedOpen =
                    !string.IsNullOrWhiteSpace(nextQ.CodeSnippet)
                    || !string.IsNullOrWhiteSpace(nextQ.MathLatex)
                    || !string.IsNullOrWhiteSpace(nextQ.Diagram);
                _mainVm.RequestUiRefresh();
                return;
            }
        }

        EditingQuestionId = null;
        EditingQuestionDraft = null;
        EditingPageTextPreview = null;
        EditingGroundStatus = null;
        EditingSnipPageBitmap = null;
        IsDedicatedSnipperOpen = false;
        _mainVm.RequestUiRefresh();
    }

    public void CancelEditingQuestionDraft()
    {
        EditingQuestionId = null;
        EditingQuestionDraft = null;
        EditingPageTextPreview = null;
        EditingGroundStatus = null;
        EditingSnipPageBitmap = null;
        IsDedicatedSnipperOpen = false;
        _mainVm.RequestUiRefresh();
    }

    private void EnsureSnipReaderHooked()
    {
        if (_snipReaderHooked)
            return;
        _snipReaderHooked = true;
        SnipPdfReader.Changed += (_, _) =>
        {
            if (IsDedicatedSnipperOpen)
                _mainVm.RequestUiRefresh();
        };
    }

    public Task OpenDedicatedSnipperAsync()
    {
        if (EditingQuestionDraft is null)
            return Task.CompletedTask;

        EnsureSnipReaderHooked();
        IsDedicatedSnipperOpen = true;
        int startPage =
            EditingSnipPageNumber > 0
                ? EditingSnipPageNumber
                : Math.Max(1, EditingQuestionDraft.SourcePage);
        return LoadEditorSnipPageAsync(startPage);
    }

    public void CloseDedicatedSnipper()
    {
        IsDedicatedSnipperOpen = false;
        _mainVm.RequestUiRefresh();
    }

    public void SetSnipMode(SnipCaptureMode mode)
    {
        SnipMode = mode;
        _mainVm.RequestUiRefresh();
    }

    public string? ResolveQuestionImageFullPath(string? storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
            return null;
        if (Path.IsPathRooted(storedPath))
            return File.Exists(storedPath) ? storedPath : null;
        if (string.IsNullOrWhiteSpace(_mainVm.BasinRoot))
            return null;
        var full = Path.Combine(_mainVm.BasinRoot, storedPath);
        return File.Exists(full) ? full : null;
    }

    public void OpenImageZoom(string? path)
    {
        var full = ResolveQuestionImageFullPath(path);
        if (string.IsNullOrWhiteSpace(full))
            return;
        ZoomedImagePath = full;
        _mainVm.RequestUiRefresh();
    }

    public void CloseImageZoom()
    {
        ZoomedImagePath = null;
        _mainVm.RequestUiRefresh();
    }

    public void SetEditingQuestionImage(string? filePath)
    {
        if (EditingQuestionDraft is null)
            return;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            EditingQuestionDraft.ImagePath = null;
            EditingGroundStatus = "Removed question image.";
            _mainVm.RequestUiRefresh();
            return;
        }

        if (!string.IsNullOrWhiteSpace(_mainVm.BasinRoot) && File.Exists(filePath))
        {
            var mediaDir = Path.Combine(
                _mainVm.BasinRoot,
                ".scholarflow",
                "quiz_media",
                OrganizerQuiz?.Id ?? "manual"
            );
            Directory.CreateDirectory(mediaDir);
            var ext = Path.GetExtension(filePath);
            if (string.IsNullOrWhiteSpace(ext))
                ext = ".png";
            var destName = $"{EditingQuestionDraft.Id}_{DateTime.UtcNow:yyyyMMddHHmmss}{ext}";
            var destFull = Path.Combine(mediaDir, destName);
            if (
                !string.Equals(
                    Path.GetFullPath(filePath),
                    Path.GetFullPath(destFull),
                    StringComparison.OrdinalIgnoreCase
                )
            )
                File.Copy(filePath, destFull, overwrite: true);
            EditingQuestionDraft.ImagePath = Path.GetRelativePath(_mainVm.BasinRoot, destFull);
        }
        else
        {
            EditingQuestionDraft.ImagePath = filePath;
        }

        EditingGroundStatus = "✓ Attached image to question.";
        _mainVm.RequestUiRefresh();
    }

    public async Task CaptureQuestionImageFromSnipperAsync(
        NormalizedHighlightRect region,
        int? targetPage = null
    )
    {
        var draft = EditingQuestionDraft;
        var pdfPath = !string.IsNullOrWhiteSpace(draft?.SourcePdfPath)
            ? draft.SourcePdfPath
            : OrganizerQuiz?.SourcePdfPath;
        if (draft is null || string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            return;

        int page =
            targetPage is > 0 ? targetPage.Value
            : EditingSnipPageNumber > 0 ? EditingSnipPageNumber
            : Math.Max(1, draft.SourcePage);
        EditingSnipPageNumber = page;
        SnipPdfReader.SetCurrentPageQuietly(page);
        var mediaDir = !string.IsNullOrWhiteSpace(_mainVm.BasinRoot)
            ? Path.Combine(
                _mainVm.BasinRoot,
                ".scholarflow",
                "quiz_media",
                OrganizerQuiz?.Id ?? "manual"
            )
            : Path.Combine(Path.GetTempPath(), "ScholarFlow-QuizMedia");
        var outFull = Path.Combine(mediaDir, $"{draft.Id}_p{page}_{DateTime.UtcNow:HHmmss}.png");

        await Task.Run(() =>
        {
            byte[] pdfBytes;
            lock (_pendingPreviewRenders)
            {
                if (
                    !string.Equals(_cachedBytesPath, pdfPath, StringComparison.OrdinalIgnoreCase)
                    || _cachedPdfBytes is null
                )
                {
                    _cachedPdfBytes = File.ReadAllBytes(pdfPath);
                    _cachedBytesPath = pdfPath;
                }
                pdfBytes = _cachedPdfBytes;
            }
            Services.PdfBitmapRenderer.CropAndSavePageRegion(
                pdfBytes,
                page - 1,
                region,
                outFull,
                dpi: 220
            );
        });

        draft.ImagePath = !string.IsNullOrWhiteSpace(_mainVm.BasinRoot)
            ? Path.GetRelativePath(_mainVm.BasinRoot, outFull)
            : outFull;
        draft.SourcePage = page;
        EditingGroundStatus = $"✓ Captured diagram/image from p.{page}";
        _mainVm.RequestUiRefresh();
    }

    public async Task ExtractEmbeddedFiguresOnSnipPageAsync()
    {
        var draft = EditingQuestionDraft;
        var pdfPath = !string.IsNullOrWhiteSpace(draft?.SourcePdfPath)
            ? draft.SourcePdfPath
            : OrganizerQuiz?.SourcePdfPath;
        if (draft is null || string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            return;

        int page =
            EditingSnipPageNumber > 0 ? EditingSnipPageNumber : Math.Max(1, draft.SourcePage);
        var outDir = Path.Combine(Path.GetTempPath(), "ScholarFlow-ExtractedFigs", draft.Id);
        var figs = await Task.Run(() =>
            PdfQuizGrounder.ExtractEmbeddedImagesFromPage(pdfPath, page, outDir)
        );
        SnipExtractedFigures = figs;
        EditingGroundStatus =
            figs.Count > 0
                ? $"Found {figs.Count} embedded figure(s) on p.{page} — click one to attach."
                : $"No embedded raster images on p.{page} (use Capture Image drag-crop for vector diagrams).";
        _mainVm.RequestUiRefresh();
    }

    public void ToggleSnipOutline()
    {
        IsSnipOutlineOpen = !IsSnipOutlineOpen;
        _mainVm.RequestUiRefresh();
    }

    public void SetSnipOutlineOpenQuiet(bool open)
    {
        IsSnipOutlineOpen = open;
    }

    public void SetSnipAnchorsOpenQuiet(bool open)
    {
        IsSnipAnchorsOpen = open;
    }

    public void SetSnipZoomPercentQuiet(int percent)
    {
        // Zoom state is now owned directly by SnipPdfReader
        _ = percent;
    }

    public void ToggleSnipOutlineNodeExpanded(PdfBookmarkNode node)
    {
        SnipPdfReader.ToggleNodeExpanded(node);
    }

    public PdfBookmarkNode? FindActiveSnipBookmarkNode(int currentPage)
    {
        PdfBookmarkNode? best = null;
        void Walk(IEnumerable<PdfBookmarkNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (
                    n.PageNumber <= currentPage
                    && (best is null || n.PageNumber >= best.PageNumber)
                )
                    best = n;
                if (n.Children.Count > 0)
                    Walk(n.Children);
            }
        }
        Walk(SnipOutlineRoots);
        return best;
    }

    public void ZoomPreview(int delta)
    {
        PreviewZoomPercent = Math.Clamp(PreviewZoomPercent + delta, 40, 350);
        _mainVm.RequestUiRefresh();
    }

    public void ResetPreviewZoom()
    {
        PreviewZoomPercent = 100;
        _mainVm.RequestUiRefresh();
    }

    public Task ToggleSnipFitToWidthAsync()
    {
        SnipPdfReader.ToggleFitToWidth();
        return Task.CompletedTask;
    }

    public Task ZoomSnipAsync(int delta)
    {
        if (delta > 0)
            SnipPdfReader.ZoomIn(delta);
        else if (delta < 0)
            SnipPdfReader.ZoomOut(Math.Abs(delta));
        return Task.CompletedTask;
    }

    public Task RotateSnipAsync(int deltaDegrees)
    {
        if (deltaDegrees > 0)
            SnipPdfReader.RotateRight();
        else if (deltaDegrees < 0)
            SnipPdfReader.RotateLeft();
        return Task.CompletedTask;
    }

    public Task LoadEditorSnipPageAsync(int oneBasedPage, bool showLoading = true)
    {
        _ = showLoading;
        var draft = EditingQuestionDraft;
        var pdfPath = !string.IsNullOrWhiteSpace(draft?.SourcePdfPath)
            ? draft.SourcePdfPath
            : OrganizerQuiz?.SourcePdfPath;
        if (draft is null || string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
        {
            EditingGroundStatus = "⚠ Source PDF not found for visual snipping.";
            _mainVm.RequestUiRefresh();
            return Task.CompletedTask;
        }

        EnsureSnipReaderHooked();
        draft.SourcePdfPath = pdfPath;
        int targetPage = Math.Max(1, oneBasedPage);
        EditingSnipPageNumber = targetPage;

        if (
            !string.Equals(
                SnipPdfReader.CurrentFilePath,
                pdfPath,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            SnipPdfReader.LoadPdf(pdfPath, _mainVm.BasinRoot);
            SnipPdfReader.JumpToPage(targetPage);
            _mainVm.RequestUiRefresh();
        }
        else
        {
            SnipPdfReader.JumpToPage(targetPage);
        }

        return Task.CompletedTask;
    }

    public async Task ApplyVisualSnipRegionAsync(
        NormalizedHighlightRect region,
        bool additive,
        int? targetPage = null
    )
    {
        var draft = EditingQuestionDraft;
        var pdfPath = !string.IsNullOrWhiteSpace(draft?.SourcePdfPath)
            ? draft.SourcePdfPath
            : OrganizerQuiz?.SourcePdfPath;
        if (draft is null || string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            return;

        int page =
            targetPage is > 0 ? targetPage.Value
            : EditingSnipPageNumber > 0 ? EditingSnipPageNumber
            : Math.Max(1, draft.SourcePage);
        EditingSnipPageNumber = page;
        SnipPdfReader.SetCurrentPageQuietly(page);
        draft.SourcePdfPath = pdfPath;

        var (quote, sectionTitle, _) = await Task.Run(() =>
            PdfQuizGrounder.ExtractQuoteFromNormalizedRegion(pdfPath, page, region)
        );

        var existingOnPageIdx = draft.EvidenceAnchors.FindIndex(a => a.PageNumber == page);
        if (additive && existingOnPageIdx >= 0)
        {
            var prev = draft.EvidenceAnchors[existingOnPageIdx];
            var updatedRects = new List<NormalizedHighlightRect>(prev.HighlightRects) { region };
            var mergedQuote =
                string.IsNullOrWhiteSpace(quote) ? prev.VerbatimQuote
                : string.IsNullOrWhiteSpace(prev.VerbatimQuote) ? quote
                : $"{prev.VerbatimQuote} ... {quote}";
            draft.EvidenceAnchors[existingOnPageIdx] = new GroundedEvidenceAnchor(
                page,
                !string.IsNullOrWhiteSpace(sectionTitle) ? sectionTitle : prev.SectionTitle,
                mergedQuote,
                updatedRects
            );
        }
        else
        {
            draft.EvidenceAnchors.Add(
                new GroundedEvidenceAnchor(page, sectionTitle, quote, [region])
            );
        }

        SyncPrimaryEvidenceFromAnchors(draft);
        EditingGroundStatus =
            $"✓ {draft.EvidenceAnchors.Count} evidence anchor(s) pinned (latest on p.{page})";
        _mainVm.RequestUiRefresh();
    }

    public void RemoveEvidenceAnchor(int index)
    {
        var draft = EditingQuestionDraft;
        if (draft is null || index < 0 || index >= draft.EvidenceAnchors.Count)
            return;

        draft.EvidenceAnchors.RemoveAt(index);
        SyncPrimaryEvidenceFromAnchors(draft);
        EditingGroundStatus =
            draft.EvidenceAnchors.Count > 0
                ? $"✓ {draft.EvidenceAnchors.Count} evidence anchor(s) remaining"
                : "Cleared all evidence anchors.";
        _mainVm.RequestUiRefresh();
    }

    public void ClearVisualSnipHighlights()
    {
        var draft = EditingQuestionDraft;
        if (draft is null)
            return;
        draft.EvidenceAnchors.Clear();
        draft.HighlightRects.Clear();
        EditingGroundStatus = "Cleared all visual highlights.";
        _mainVm.RequestUiRefresh();
    }

    private static void SyncPrimaryEvidenceFromAnchors(QuizQuestionRecord draft)
    {
        if (draft.EvidenceAnchors.Count == 0)
        {
            draft.HighlightRects.Clear();
            return;
        }

        var primary = draft.EvidenceAnchors[0];
        draft.SourcePage = primary.PageNumber;
        if (!string.IsNullOrWhiteSpace(primary.SectionTitle))
            draft.SectionTitle = primary.SectionTitle;
        if (!string.IsNullOrWhiteSpace(primary.VerbatimQuote))
            draft.VerbatimQuote = primary.VerbatimQuote;
        draft.HighlightRects = [.. primary.HighlightRects];
    }

    public void MoveOrganizerQuestion(QuizQuestionRecord q, int delta)
    {
        if (OrganizerQuiz is null)
            return;
        int idx = OrganizerQuiz.Questions.IndexOf(q);
        int target = idx + delta;
        if (idx < 0 || target < 0 || target >= OrganizerQuiz.Questions.Count)
            return;

        (OrganizerQuiz.Questions[idx], OrganizerQuiz.Questions[target]) = (
            OrganizerQuiz.Questions[target],
            OrganizerQuiz.Questions[idx]
        );
        ReindexOrganizerQuestions();
        _mainVm.RequestUiRefresh();
    }

    public void ShuffleOrganizerQuestions(QuizQuestionKind? onlyKind = null)
    {
        if (OrganizerQuiz is null || OrganizerQuiz.Questions.Count <= 1)
            return;
        var rng = new Random();

        if (onlyKind.HasValue)
        {
            var indices = new List<int>();
            var items = new List<QuizQuestionRecord>();
            for (int i = 0; i < OrganizerQuiz.Questions.Count; i++)
            {
                if (OrganizerQuiz.Questions[i].Kind == onlyKind.Value)
                {
                    indices.Add(i);
                    items.Add(OrganizerQuiz.Questions[i]);
                }
            }
            var shuffled = items.OrderBy(_ => rng.Next()).ToList();
            for (int i = 0; i < indices.Count; i++)
                OrganizerQuiz.Questions[indices[i]] = shuffled[i];
            StatusMessage = $"Shuffled {items.Count} {onlyKind.Value} question(s) in place.";
        }
        else
        {
            OrganizerQuiz.Questions = [.. OrganizerQuiz.Questions.OrderBy(_ => rng.Next())];
            StatusMessage = $"Shuffled all {OrganizerQuiz.Questions.Count} questions in deck.";
        }

        ReindexOrganizerQuestions();
        _mainVm.RequestUiRefresh();
    }

    public void SortOrganizerQuestionsByKind()
    {
        if (OrganizerQuiz is null)
            return;
        OrganizerQuiz.Questions =
        [
            .. OrganizerQuiz.Questions.OrderBy(q => (int)q.Kind).ThenBy(q => q.SourcePage),
        ];
        ReindexOrganizerQuestions();
        StatusMessage = "Grouped and ordered deck questions by Question Kind.";
        _mainVm.RequestUiRefresh();
    }

    public void SortOrganizerQuestionsByPage()
    {
        if (OrganizerQuiz is null)
            return;
        OrganizerQuiz.Questions =
        [
            .. OrganizerQuiz.Questions.OrderBy(q => q.SourcePage).ThenBy(q => q.OrderIndex),
        ];
        ReindexOrganizerQuestions();
        StatusMessage = "Ordered deck questions chronologically by PDF Page.";
        _mainVm.RequestUiRefresh();
    }

    public void DeleteOrganizerQuestion(QuizQuestionRecord q)
    {
        if (OrganizerQuiz is null)
            return;
        OrganizerQuiz.Questions.Remove(q);
        ReindexOrganizerQuestions();
        if (EditingQuestionId == q.Id)
            EditingQuestionId = null;
        _mainVm.RequestUiRefresh();
    }

    public void AddBlankOrganizerQuestion(QuizQuestionKind kind)
    {
        if (OrganizerQuiz is null)
            return;
        var q = new QuizQuestionRecord
        {
            QuizId = OrganizerQuiz.Id,
            OrderIndex = OrganizerQuiz.Questions.Count,
            Kind = kind,
            Prompt =
                kind == QuizQuestionKind.FillInTheBlank
                    ? "Enter statement with _____ for the missing concept..."
                    : "Enter question prompt...",
            Options = kind switch
            {
                QuizQuestionKind.MultipleChoice or QuizQuestionKind.Situational =>
                [
                    "Option A",
                    "Option B",
                    "Option C",
                    "Option D",
                ],
                QuizQuestionKind.TrueFalse => ["True", "False"],
                QuizQuestionKind.Ordering => ["Step 1", "Step 2", "Step 3"],
                _ => [],
            },
            CorrectAnswer = kind switch
            {
                QuizQuestionKind.MultipleChoice or QuizQuestionKind.Situational => "Option A",
                QuizQuestionKind.TrueFalse => "True",
                QuizQuestionKind.Ordering => "Step 1 -> Step 2 -> Step 3",
                _ => "Answer",
            },
            SourcePdfPath = OrganizerQuiz.SourcePdfPath,
            SourcePage = 1,
            SrsDueDate = DateOnly.FromDateTime(DateTime.Today),
        };
        OrganizerQuiz.Questions.Add(q);
        IsOrganizerDirty = true;
        SelectedOrganizerQuestionId = q.Id;
        EditingQuestionId = q.Id;
        EditingQuestionDraft = CloneQuestionForEdit(q);
        IsEditingAdvancedOpen = false;
        _mainVm.RequestUiRefresh();
    }

    public async Task SaveOrganizerDeckAsync()
    {
        if (OrganizerQuiz is null)
            return;
        ReindexOrganizerQuestions();
        await ScholarFlowDatabase.SaveQuizSessionAsync(_mainVm.BasinRoot, OrganizerQuiz);
        IsOrganizerDirty = false;
        SelectedQuizIdsForMerge.Clear();
        SavedQuizzes = await ScholarFlowDatabase.LoadQuizSessionsAsync(_mainVm.BasinRoot);
        StatusMessage =
            $"Saved deck '{OrganizerQuiz.Title}' ({OrganizerQuiz.Questions.Count} questions).";
        IsStatusError = false;
        _mainVm.RequestUiRefresh();
    }

    private void ReindexOrganizerQuestions()
    {
        if (OrganizerQuiz is null)
            return;
        IsOrganizerDirty = true;
        for (int i = 0; i < OrganizerQuiz.Questions.Count; i++)
        {
            OrganizerQuiz.Questions[i].QuizId = OrganizerQuiz.Id;
            OrganizerQuiz.Questions[i].OrderIndex = i;
        }
    }

    public async Task ExportQuizToJsonFileAsync(QuizSessionRecord quiz, string filePath)
    {
        var json = QuizExportService.ExportDeckToJson(quiz);
        await File.WriteAllTextAsync(filePath, json);
        StatusMessage = $"Exported JSON deck to {Path.GetFileName(filePath)}.";
        IsStatusError = false;
        _mainVm.RequestUiRefresh();
    }

    public async Task ExportQuizToPdfFileAsync(
        QuizSessionRecord quiz,
        string filePath,
        QuizPdfExportOptions? options = null
    )
    {
        var effectiveOptions = (options ?? QuizPdfExportOptions.ExamDefault) with
        {
            BasinRoot = _mainVm.BasinRoot,
        };
        await Task.Run(() =>
            QuizExportService.ExportDeckToPrintablePdf(quiz, filePath, effectiveOptions)
        );
        var modeLabel =
            options?.Mode == QuizPdfExportMode.StudyGuideInline
                ? "Study Guide PDF"
                : (
                    options?.IncludeAnswerKey == false ? "Questions-Only PDF" : "Printable Exam PDF"
                );
        StatusMessage = $"Exported {modeLabel} to {Path.GetFileName(filePath)}.";
        IsStatusError = false;
        _mainVm.RequestUiRefresh();
    }

    public async Task ImportDeckJsonToSelectedCourseAsync(string rawJson)
    {
        var course = SelectedCourse ?? _mainVm.Courses.FirstOrDefault(c => !c.IsArchived);
        var courseFolder = course?.FolderName ?? string.Empty;
        var courseTitle = course?.Title ?? "Imported Course";

        var imported = await Task.Run(() =>
            QuizExportService.ImportDeckFromJson(
                rawJson,
                courseFolder,
                courseTitle,
                SelectedPdfPath
            )
        );

        await ScholarFlowDatabase.SaveQuizSessionAsync(_mainVm.BasinRoot, imported);
        SavedQuizzes = await ScholarFlowDatabase.LoadQuizSessionsAsync(_mainVm.BasinRoot);
        StatusMessage =
            $"Imported '{imported.Title}' ({imported.Questions.Count} questions) into {courseTitle}.";
        IsStatusError = false;
        _mainVm.RequestUiRefresh();
    }

    public void ToggleFlagQuestionForAiFix(string questionId)
    {
        if (string.Equals(FlaggingQuestionId, questionId, StringComparison.OrdinalIgnoreCase))
        {
            FlaggingQuestionId = null;
            FlagProblemDescription = string.Empty;
            FlagFixPromptPreview = string.Empty;
            FlagFixJsonInput = string.Empty;
        }
        else
        {
            FlaggingQuestionId = questionId;
            EditingQuestionId = null;
            FlagProblemDescription = string.Empty;
            FlagFixPromptPreview = string.Empty;
            FlagFixJsonInput = string.Empty;
        }
        _mainVm.RequestUiRefresh();
    }

    public void CloseQuestionAiFixModal()
    {
        if (string.IsNullOrWhiteSpace(FlaggingQuestionId))
            return;

        FlaggingQuestionId = null;
        FlagProblemDescription = string.Empty;
        FlagFixPromptPreview = string.Empty;
        FlagFixJsonInput = string.Empty;
        _mainVm.RequestUiRefresh();
    }

    public string BuildQuestionAiFixPrompt(QuizQuestionRecord question, string problemDescription)
    {
        FlagProblemDescription = problemDescription;
        var deck =
            OrganizerQuiz
            ?? new QuizSessionRecord
            {
                Title = "Quiz Deck",
                CourseTitle = SelectedCourse?.Title ?? "Course",
            };
        FlagFixPromptPreview = QuizExportService.BuildSingleQuestionFixPrompt(
            deck,
            question,
            problemDescription
        );
        _mainVm.RequestUiRefresh();
        return FlagFixPromptPreview;
    }

    public async Task ApplyQuestionAiFixAsync(QuizQuestionRecord question, string rawJson)
    {
        try
        {
            await Task.Run(() => QuizExportService.ApplySingleQuestionFixJson(question, rawJson));
            InvalidateOrganizerQualityCache();
            if (OrganizerQuiz is not null)
                await SaveOrganizerDeckAsync();
            FlaggingQuestionId = null;
            FlagFixPromptPreview = string.Empty;
            FlagFixJsonInput = string.Empty;
            StatusMessage = "Applied AI repair to question and re-grounded PDF evidence.";
            IsStatusError = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Fix JSON error: {ex.Message}";
            IsStatusError = true;
        }
        _mainVm.RequestUiRefresh();
    }

    public async Task RunSingleQuestionAiFixWithApiAsync(
        QuizQuestionRecord question,
        string problemDescription
    )
    {
        if (IsGenerating)
            return;

        var (DisplayLabel, IsConfigured, MissingReason) = GetActiveAiEngineStatus();
        if (!IsConfigured)
        {
            StatusMessage =
                $"AI Engine not configured: {MissingReason}. Configure it in AI Engine settings or use Web AI JSON.";
            IsStatusError = true;
            IsAiSettingsOpen = true;
            _mainVm.RequestUiRefresh();
            return;
        }

        IsGenerating = true;
        IsStatusError = false;
        StatusMessage = $"Repairing question via {DisplayLabel}...";
        _mainVm.RequestUiRefresh();

        try
        {
            var prompt = BuildQuestionAiFixPrompt(question, problemDescription);
            var req = BuildCurrentRequest();
            var rawJson = await _llmService.ExecuteRawJsonPromptAsync(
                req.ProviderBaseUrl,
                req.ApiKey,
                req.ModelName,
                "You are an expert academic quiz editor. Return ONLY a single valid JSON object matching the requested question schema.",
                prompt
            );
            await ApplyQuestionAiFixAsync(question, rawJson);
        }
        catch (Exception ex)
        {
            StatusMessage = $"AI fix failed: {ex.Message}";
            IsStatusError = true;
        }
        finally
        {
            IsGenerating = false;
            _mainVm.RequestUiRefresh();
        }
    }

    public string? DeckAiFixFeedback { get; private set; }
    public bool IsDeckAiFixError { get; private set; }

    public void OpenDeckAiFixModal()
    {
        if (OrganizerQuiz is null)
            return;

        IsDeckAiFixModalOpen = true;
        DeckAiFixFeedback = null;
        IsDeckAiFixError = false;
        _mainVm.RequestUiRefresh();
    }

    public void CloseDeckAiFixModal()
    {
        IsDeckAiFixModalOpen = false;
        DeckAiFixFeedback = null;
        IsDeckAiFixError = false;
        _mainVm.RequestUiRefresh();
    }

    public void ToggleDeckAiFixTarget(string questionId)
    {
        if (!DeckAiFixSelectedIds.Add(questionId))
            DeckAiFixSelectedIds.Remove(questionId);
        _mainVm.RequestUiRefresh();
    }

    public void SelectQualityIssuesForBulkAiFix()
    {
        if (OrganizerQuiz is null)
            return;

        var report = GetCachedOrganizerQualityReport();
        DeckAiFixSelectedIds.Clear();
        foreach (var qid in report.FlaggedQuestionIds)
            DeckAiFixSelectedIds.Add(qid);

        if (report.HasIssues)
        {
            var distinctCodes = report.Issues.Select(i => i.Code).Distinct().ToList();
            var directives = new List<string>
            {
                $"Repair the {report.FlaggedQuestionIds.Count} quality-critic flagged question(s) ({string.Join(", ", distinctCodes)}):",
            };
            foreach (var issue in report.Issues.Take(15))
            {
                int qNum =
                    OrganizerQuiz.Questions.FindIndex(x =>
                        string.Equals(x.Id, issue.QuestionId, StringComparison.OrdinalIgnoreCase)
                    ) + 1;
                directives.Add($"- Q{qNum} [{issue.Code}]: {issue.Message}");
            }
            DeckAiFixInstructions = string.Join("\n", directives);
            DeckAiFixFeedback =
                $"Selected {DeckAiFixSelectedIds.Count} question(s) with quality issues and pre-filled repair notes.";
            IsDeckAiFixError = false;
        }
        else
        {
            DeckAiFixFeedback = "No quality issues detected in this deck.";
            IsDeckAiFixError = false;
        }
        _mainVm.RequestUiRefresh();
    }

    public void SelectAllQuestionsForBulkAiFix()
    {
        if (OrganizerQuiz is null)
            return;
        DeckAiFixSelectedIds.Clear();
        foreach (var q in OrganizerQuiz.Questions)
            DeckAiFixSelectedIds.Add(q.Id);
        DeckAiFixFeedback = $"Selected all {DeckAiFixSelectedIds.Count} question(s) in this deck.";
        IsDeckAiFixError = false;
        _mainVm.RequestUiRefresh();
    }

    public void ClearBulkAiFixSelection()
    {
        DeckAiFixSelectedIds.Clear();
        DeckAiFixFeedback = null;
        IsDeckAiFixError = false;
        _mainVm.RequestUiRefresh();
    }

    public void SetDeckAiFixOutputMode(QuizBuilderOutputMode mode)
    {
        DeckAiFixOutputMode = mode;
        _mainVm.RequestUiRefresh();
    }

    public string BuildDeckAiFixPrompt()
    {
        if (OrganizerQuiz is null)
            return string.Empty;

        var targets = OrganizerQuiz
            .Questions.Where(q => DeckAiFixSelectedIds.Contains(q.Id))
            .ToList();
        if (targets.Count == 0)
            targets = [.. OrganizerQuiz.Questions.Take(15)];

        DeckAiFixPromptPreview = QuizExportService.BuildDeckComprehensiveFixPrompt(
            OrganizerQuiz,
            targets,
            DeckAiFixInstructions
        );
        DeckAiFixFeedback =
            $"Built & copied fix prompt for {targets.Count} targeted question(s). Paste the AI JSON response below.";
        IsDeckAiFixError = false;
        _mainVm.RequestUiRefresh();
        return DeckAiFixPromptPreview;
    }

    public async Task RunDeckComprehensiveAiFixWithApiAsync()
    {
        if (OrganizerQuiz is null || IsGenerating)
            return;

        var (DisplayLabel, IsConfigured, MissingReason) = GetActiveAiEngineStatus();
        if (!IsConfigured)
        {
            StatusMessage =
                $"AI Engine not configured: {MissingReason}. Configure it in AI Engine settings or use Web AI JSON.";
            IsStatusError = true;
            IsAiSettingsOpen = true;
            _mainVm.RequestUiRefresh();
            return;
        }

        IsGenerating = true;
        IsStatusError = false;
        StatusMessage = $"Running comprehensive deck fix via {DisplayLabel}...";
        _mainVm.RequestUiRefresh();

        try
        {
            var prompt = BuildDeckAiFixPrompt();
            var req = BuildCurrentRequest();
            var rawJson = await _llmService.ExecuteRawJsonPromptAsync(
                req.ProviderBaseUrl,
                req.ApiKey,
                req.ModelName,
                "You are an expert academic quiz editor. Return ONLY a valid JSON object with updatedQuestions and newQuestions arrays.",
                prompt
            );
            await ApplyDeckComprehensiveAiFixJsonAsync(rawJson);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Comprehensive AI fix failed: {ex.Message}";
            IsStatusError = true;
        }
        finally
        {
            IsGenerating = false;
            _mainVm.RequestUiRefresh();
        }
    }

    public async Task ApplyDeckComprehensiveAiFixJsonAsync(string rawJson)
    {
        if (OrganizerQuiz is null)
            return;

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            DeckAiFixFeedback = "Paste the AI JSON response before clicking Apply.";
            IsDeckAiFixError = true;
            _mainVm.RequestUiRefresh();
            return;
        }

        var fallbackTargetIds = OrganizerQuiz
            .Questions.Where(q => DeckAiFixSelectedIds.Contains(q.Id))
            .Select(q => q.Id)
            .ToList();
        if (fallbackTargetIds.Count == 0)
            fallbackTargetIds = [.. OrganizerQuiz.Questions.Take(15).Select(q => q.Id)];

        try
        {
            var (updated, added) = await Task.Run(() =>
                QuizExportService.ApplyDeckComprehensiveFixJson(
                    OrganizerQuiz,
                    rawJson,
                    fallbackTargetIds
                )
            );
            InvalidateOrganizerQualityCache();
            await SaveOrganizerDeckAsync();
            IsDeckAiFixModalOpen = false;
            DeckAiFixPromptPreview = string.Empty;
            DeckAiFixJsonInput = string.Empty;
            DeckAiFixFeedback = null;
            IsDeckAiFixError = false;
            DeckAiFixSelectedIds.Clear();
            StatusMessage =
                $"Bulk AI fix applied: {updated} question(s) updated, {added} question(s) added & grounded.";
            IsStatusError = false;
        }
        catch (Exception ex)
        {
            DeckAiFixFeedback = $"Could not apply JSON: {ex.Message}";
            IsDeckAiFixError = true;
            StatusMessage = $"Bulk Fix JSON error: {ex.Message}";
            IsStatusError = true;
        }
        _mainVm.RequestUiRefresh();
    }
}
