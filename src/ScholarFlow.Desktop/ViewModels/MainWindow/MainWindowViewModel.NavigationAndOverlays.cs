using ScholarFlow.Core.Models;
using ScholarFlow.Core.Tasks;

namespace ScholarFlow.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    public void SelectSmartView(SmartViewKind view)
    {
        ActiveMode = WorkspaceTabMode.TaskList;
        ActiveSmartView = view;
        ActiveCourse = null;
        ActiveLabelFilter = null;
        RecomputeVisibleTasks();
        NotifyStateChanged();
    }

    public void SelectTermView(string? term)
    {
        var normalized =
            TaskItem.NormalizeTerm(term)
            ?? (string.IsNullOrWhiteSpace(term) ? null : term.Trim());
        if (ActiveMode == WorkspaceTabMode.CourseWorkspace)
        {
            ActiveTermFilter = string.Equals(
                ActiveTermFilter,
                normalized,
                StringComparison.OrdinalIgnoreCase
            )
                ? null
                : normalized;
        }
        else
        {
            ActiveMode = WorkspaceTabMode.TaskList;
            if (
                normalized is null
                || string.Equals(ActiveTermFilter, normalized, StringComparison.OrdinalIgnoreCase)
            )
            {
                ActiveTermFilter = null;
                if (ActiveSmartView == SmartViewKind.Term)
                    ActiveSmartView = SmartViewKind.All;
            }
            else
            {
                ActiveTermFilter = normalized;
            }
        }

        RecomputeVisibleTasks();
        NotifyStateChanged(UiChangeKind.SearchOrTaskList);
    }

    public void SelectLabelView(string label)
    {
        ActiveMode = WorkspaceTabMode.TaskList;
        ActiveSmartView = SmartViewKind.Label;
        ActiveCourse = null;
        ActiveLabelFilter = label;
        RecomputeVisibleTasks();
        NotifyStateChanged();
    }

    public void SetGroupingMode(TaskGroupingMode mode)
    {
        ActiveGrouping = mode;
        NotifyStateChanged();
    }

    public void SelectCalendarView()
    {
        ActiveMode = WorkspaceTabMode.Calendar;
        ActiveCourse = null;
        Calendar.UpdateTasks([.. EnumerateAllTaskNodes(AllTasks)]);
        NotifyStateChanged();
    }

    public void SetCompletedCourseFilter(string? courseFolder)
    {
        CompletedCourseFilter = string.Equals(
            CompletedCourseFilter,
            courseFolder,
            StringComparison.OrdinalIgnoreCase
        )
            ? null
            : courseFolder;
        RecomputeVisibleTasks();
        NotifyStateChanged();
    }

    public void SelectCourse(BasinCourseEntry course, bool showMaterials = false)
    {
        ActiveMode = WorkspaceTabMode.CourseWorkspace;
        ActiveSmartView = SmartViewKind.Course;
        ActiveCourse = course;
        ActiveLabelFilter = null;
        ShowCourseMaterialsSubTab = showMaterials;
        RecomputeVisibleTasks();
        NotifyStateChanged();
    }

    public void SetCourseSubTab(bool showMaterials)
    {
        if (ShowCourseMaterialsSubTab == showMaterials)
            return;

        ShowCourseMaterialsSubTab = showMaterials;
        NotifyStateChanged(UiChangeKind.SearchOrTaskList);
    }

    public void RequestUiRefresh() => NotifyStateChanged(UiChangeKind.FullLayout);

    public async Task SelectQuizStudioViewAsync(
        BasinCourseEntry? course = null,
        string? pdfPath = null,
        int? focusPage = null
    )
    {
        ActiveMode = WorkspaceTabMode.QuizStudio;
        if (course is not null)
            ActiveCourse = course;
        await QuizStudio.RefreshAsync(course ?? ActiveCourse, pdfPath, focusPage);
        NotifyStateChanged();
    }

    public void OpenQuizStudioForCurrentPdf()
    {
        var currentPdf = PdfReader.CurrentFilePath;
        var currentPage = PdfReader.CurrentPage;
        var matchedCourse =
            Courses.FirstOrDefault(c =>
                !string.IsNullOrWhiteSpace(currentPdf)
                && currentPdf.Contains(c.FolderName, StringComparison.OrdinalIgnoreCase)
            ) ?? ActiveCourse;

        _ = SelectQuizStudioViewAsync(matchedCourse, currentPdf, currentPage);
    }

    public void OpenPdfWithQuizEvidence(
        string pdfFullPath,
        int pageNumber,
        IReadOnlyList<Core.Quiz.NormalizedHighlightRect> highlights,
        string? verbatimQuote,
        string? questionPrompt
    )
    {
        PdfReturnMode = WorkspaceTabMode.QuizStudio;
        PdfReturnTask = null;
        PdfOpenedFromQuiz = true;
        if (
            !string.Equals(
                PdfReader.CurrentFilePath,
                pdfFullPath,
                StringComparison.OrdinalIgnoreCase
            )
        )
            PdfReader.LoadPdf(pdfFullPath);
        PdfReader.SetEvidenceHighlight(pageNumber, highlights, verbatimQuote, questionPrompt);
        ActiveMode = WorkspaceTabMode.PdfReader;
        NotifyStateChanged();
    }

    public void OpenKnowledgeSearch(string? initialQuery = null)
    {
        ActiveMode = WorkspaceTabMode.KnowledgeSearch;
        IsSearchModalOpen = false;
        if (!string.IsNullOrWhiteSpace(initialQuery))
            KnowledgeSearch.ApplyExampleQuery(initialQuery);
        else if (!KnowledgeSearch.HasCompletedInitialIndex && !KnowledgeSearch.IsIndexing)
            KnowledgeSearch.TriggerBackgroundIndex();
        NotifyStateChanged();
        Views.KnowledgeSearchViewBuilder.FocusQueryBox();
    }

    public void OpenPdfFromKnowledgeSearch(
        string pdfFullPath,
        int pageNumber,
        IReadOnlyList<Core.Quiz.NormalizedHighlightRect> highlights,
        string? verbatimQuote,
        string? sourceHeading
    )
    {
        PdfReturnMode = WorkspaceTabMode.KnowledgeSearch;
        PdfReturnTask = null;
        PdfOpenedFromQuiz = false;
        if (
            !string.Equals(
                PdfReader.CurrentFilePath,
                pdfFullPath,
                StringComparison.OrdinalIgnoreCase
            )
        )
            PdfReader.LoadPdf(pdfFullPath);
        PdfReader.SetEvidenceHighlight(pageNumber, highlights, verbatimQuote, sourceHeading);
        ActiveMode = WorkspaceTabMode.PdfReader;
        NotifyStateChanged();
    }

    public void OpenQuizQuestionInOrganizerFromSearch(
        Core.Quiz.QuizSessionRecord quiz,
        string questionId
    )
    {
        ActiveMode = WorkspaceTabMode.QuizStudio;
        QuizStudio.OpenQuizInOrganizer(quiz);
        if (!string.IsNullOrWhiteSpace(questionId))
        {
            QuizStudio.SelectOrganizerQuestion(
                questionId,
                notify: false,
                scrollCardIntoView: true,
                scrollTocIntoView: true
            );
        }
        NotifyStateChanged();
    }

    public void OpenPdfInReader(string pdfFullPath, TaskItem? associatedTask = null)
    {
        if (ActiveMode != WorkspaceTabMode.PdfReader)
            PdfReturnMode = ActiveMode;
        PdfReturnTask = associatedTask;
        PdfOpenedFromQuiz = ActiveMode == WorkspaceTabMode.QuizStudio;
        PdfReader.LoadPdf(pdfFullPath);
        ActiveMode = WorkspaceTabMode.PdfReader;
        NotifyStateChanged();
    }

    public void ReturnFromPdfReader()
    {
        if (PdfOpenedFromQuiz || PdfReturnMode == WorkspaceTabMode.QuizStudio)
        {
            PdfOpenedFromQuiz = false;
            ActiveMode = WorkspaceTabMode.QuizStudio;
            NotifyStateChanged();
            return;
        }

        if (PdfReturnMode == WorkspaceTabMode.KnowledgeSearch)
        {
            OpenKnowledgeSearch();
            return;
        }

        var returnTask = PdfReturnTask;
        PdfReturnTask = null;
        if (returnTask is not null)
        {
            ActiveMode =
                PdfReturnMode == WorkspaceTabMode.PdfReader
                    ? WorkspaceTabMode.TaskList
                    : PdfReturnMode;
            SelectTask(returnTask, openInspector: true);
            return;
        }

        if (ActiveCourse is not null)
            SelectCourse(ActiveCourse, showMaterials: ShowCourseMaterialsSubTab);
        else if (PdfReturnMode == WorkspaceTabMode.Calendar)
            SelectCalendarView();
        else
            SelectSmartView(ActiveSmartView);
    }

    public void SelectTask(TaskItem? task, bool openInspector = false)
    {
        SelectedTask = task;
        SelectedTaskNoteContent = LoadTaskNotePreview(task);
        if (openInspector && task is not null)
        {
            IsInspectorOpen = true;
            NotifyStateChanged(UiChangeKind.FullLayout);
            return;
        }
        NotifyStateChanged(UiChangeKind.SelectionOnly);
    }

    public void OpenInspector(TaskItem? task = null)
    {
        if (task is not null)
        {
            SelectedTask = task;
            SelectedTaskNoteContent = LoadTaskNotePreview(task);
        }
        if (SelectedTask is not null)
        {
            IsInspectorOpen = true;
            NotifyStateChanged(UiChangeKind.FullLayout);
        }
    }

    public void CloseInspector()
    {
        if (!IsInspectorOpen)
            return;

        IsInspectorOpen = false;
        NotifyStateChanged(UiChangeKind.FullLayout);
    }

    public void ToggleInspector()
    {
        if (IsInspectorOpen)
            CloseInspector();
        else
            OpenInspector();
    }

    public void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
        NotifyStateChanged(UiChangeKind.FullLayout);
    }

    public void ToggleQuickAddModal()
    {
        IsQuickAddModalOpen = !IsQuickAddModalOpen;
        if (IsQuickAddModalOpen)
        {
            IsSearchModalOpen = false;
            IsShortcutsModalOpen = false;
            IsNotificationDrawerOpen = false;
        }
        NotifyStateChanged(UiChangeKind.OverlayOnly);
    }

    public void OpenQuickAddModal()
    {
        IsQuickAddModalOpen = true;
        IsSearchModalOpen = false;
        IsShortcutsModalOpen = false;
        IsNotificationDrawerOpen = false;
        NotifyStateChanged(UiChangeKind.OverlayOnly);
    }

    public void CloseQuickAddModal()
    {
        if (!IsQuickAddModalOpen)
            return;

        IsQuickAddModalOpen = false;
        NotifyStateChanged(UiChangeKind.OverlayOnly);
    }

    public void ToggleSearchModal()
    {
        IsSearchModalOpen = !IsSearchModalOpen;
        if (IsSearchModalOpen)
        {
            IsQuickAddModalOpen = false;
            IsShortcutsModalOpen = false;
            IsNotificationDrawerOpen = false;
            RecomputeSearchResults();
        }
        NotifyStateChanged(UiChangeKind.OverlayOnly);
    }

    public void OpenSearchModal()
    {
        IsSearchModalOpen = true;
        IsQuickAddModalOpen = false;
        IsShortcutsModalOpen = false;
        IsNotificationDrawerOpen = false;
        RecomputeSearchResults();
        NotifyStateChanged(UiChangeKind.OverlayOnly);
    }

    public void CloseSearchModal()
    {
        if (!IsSearchModalOpen)
            return;

        IsSearchModalOpen = false;
        NotifyStateChanged(UiChangeKind.OverlayOnly);
    }

    public void ToggleShortcutsModal()
    {
        IsShortcutsModalOpen = !IsShortcutsModalOpen;
        if (IsShortcutsModalOpen)
        {
            IsQuickAddModalOpen = false;
            IsSearchModalOpen = false;
            IsNotificationDrawerOpen = false;
            IsSettingsModalOpen = false;
        }
        NotifyStateChanged(UiChangeKind.OverlayOnly);
    }

    public bool CloseTopOverlayOrDrawer()
    {
        if (IsPacketOrganizerModalOpen)
        {
            IsPacketOrganizerModalOpen = false;
            NotifyStateChanged(UiChangeKind.OverlayOnly);
            return true;
        }
        if (IsAddCourseModalOpen)
        {
            IsAddCourseModalOpen = false;
            NotifyStateChanged(UiChangeKind.OverlayOnly);
            return true;
        }
        if (IsSettingsModalOpen)
        {
            IsSettingsModalOpen = false;
            NotifyStateChanged(UiChangeKind.OverlayOnly);
            return true;
        }
        if (IsOnboardingOpen && !string.IsNullOrWhiteSpace(BasinRoot))
        {
            IsOnboardingOpen = false;
            NotifyStateChanged(UiChangeKind.OverlayOnly);
            return true;
        }
        if (IsQuickAddModalOpen)
        {
            IsQuickAddModalOpen = false;
            NotifyStateChanged(UiChangeKind.OverlayOnly);
            return true;
        }
        if (IsSearchModalOpen)
        {
            IsSearchModalOpen = false;
            NotifyStateChanged(UiChangeKind.OverlayOnly);
            return true;
        }
        if (IsShortcutsModalOpen)
        {
            IsShortcutsModalOpen = false;
            NotifyStateChanged(UiChangeKind.OverlayOnly);
            return true;
        }
        if (IsNotificationDrawerOpen)
        {
            IsNotificationDrawerOpen = false;
            NotifyStateChanged(UiChangeKind.OverlayOnly);
            return true;
        }
        if (IsInspectorOpen)
        {
            IsInspectorOpen = false;
            NotifyStateChanged(UiChangeKind.FullLayout);
            return true;
        }
        return false;
    }

    public void MoveSelection(int delta)
    {
        if (VisibleTasks.Count == 0)
            return;

        if (SelectedTask is null)
        {
            SelectTask(VisibleTasks[0]);
            return;
        }

        var idx = VisibleTasks.ToList().FindIndex(t => t.Id == SelectedTask.Id);
        int next = idx < 0 ? 0 : Math.Clamp(idx + delta, 0, VisibleTasks.Count - 1);
        SelectTask(VisibleTasks[next]);
    }

    public void JumpSelection(bool toEnd)
    {
        if (VisibleTasks.Count == 0)
            return;

        SelectTask(toEnd ? VisibleTasks[^1] : VisibleTasks[0]);
    }
}
