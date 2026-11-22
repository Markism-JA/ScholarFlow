using ScholarFlow.Core.Database;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Pdf;
using ScholarFlow.Core.Services;
using ScholarFlow.Core.Tasks;
using ScholarFlow.Desktop.Services;

namespace ScholarFlow.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    public void UpdateQuickAddText(string text)
    {
        QuickAddText = text;
        if (string.IsNullOrWhiteSpace(text))
        {
            QuickAddChips = [];
        }
        else
        {
            var parsed = QuickAddParser.Parse(text, Courses);
            QuickAddChips = parsed.Chips;
        }
        NotifyStateChanged(UiChangeKind.QuickAddOnly);
    }

    public void InsertQuickAddToken(string token, string? replacePattern = null)
    {
        var current = QuickAddText?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(replacePattern))
        {
            current = System
                .Text.RegularExpressions.Regex.Replace(
                    current,
                    replacePattern,
                    "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                )
                .Trim();
        }

        var next = string.IsNullOrEmpty(current) ? token : $"{current} {token}";
        UpdateQuickAddText(next);
    }

    public async Task SubmitQuickAddAsync()
    {
        if (string.IsNullOrWhiteSpace(QuickAddText))
            return;

        var input = QuickAddText;
        if (ActiveCourse is not null && !input.Contains('#'))
            input = $"{input} #{ActiveCourse.Alias}";
        else if (
            ActiveMode == WorkspaceTabMode.Calendar
            && !QuickAddParser.Parse(input, Courses).Task.DueDate.HasValue
        )
            input = $"{input} {Calendar.SelectedDate:yyyy-MM-dd}";

        var created = await _basinService.AddQuickTaskAsync(BasinRoot, input);
        QuickAddText = string.Empty;
        QuickAddChips = [];
        IsQuickAddModalOpen = false;
        StatusText = $"Added task: {created.Title}";
        await ReloadBasinDataAsync();
        SelectTask(VisibleTasks.FirstOrDefault(t => t.Title == created.Title));
    }

    public string SearchCategoryFilter { get; private set; } = "All";
    public bool ExcludeFilesInQuickSearch { get; private set; }

    public void UpdateSearchQuery(string query)
    {
        SearchQuery = query;
        RecomputeSearchResults();
        NotifyStateChanged(UiChangeKind.SearchOrTaskList);
    }

    public void SetSearchCategoryFilter(string category)
    {
        if (string.Equals(SearchCategoryFilter, category, StringComparison.OrdinalIgnoreCase))
            return;

        SearchCategoryFilter = category;
        if (string.Equals(category, "Files", StringComparison.OrdinalIgnoreCase))
            ExcludeFilesInQuickSearch = false;
        RecomputeSearchResults();
        NotifyStateChanged(UiChangeKind.SearchOrTaskList);
    }

    public void ToggleExcludeFilesInQuickSearch()
    {
        ExcludeFilesInQuickSearch = !ExcludeFilesInQuickSearch;
        if (
            ExcludeFilesInQuickSearch
            && string.Equals(SearchCategoryFilter, "Files", StringComparison.OrdinalIgnoreCase)
        )
        {
            SearchCategoryFilter = "All";
        }
        RecomputeSearchResults();
        NotifyStateChanged(UiChangeKind.SearchOrTaskList);
    }

    public void MoveSearchSelection(int delta)
    {
        if (SearchResults.Count == 0)
            return;

        SelectedSearchResultIndex = Math.Clamp(
            SelectedSearchResultIndex + delta,
            0,
            SearchResults.Count - 1
        );
        NotifyStateChanged(UiChangeKind.SearchOrTaskList);
    }

    public void ExecuteSelectedSearchResult()
    {
        if (SelectedSearchResultIndex >= 0 && SelectedSearchResultIndex < SearchResults.Count)
        {
            ExecuteSearchResult(SearchResults[SelectedSearchResultIndex]);
        }
    }

    public async void ExecuteSearchResult(SearchResultItem item)
    {
        IsSearchModalOpen = false;
        SearchQuery = string.Empty;

        switch (item.Kind)
        {
            case SearchResultKind.Course when item.Course is not null:
                SelectCourse(item.Course);
                break;

            case SearchResultKind.PdfDocument when !string.IsNullOrWhiteSpace(item.PdfPath):
                OpenPdfInReader(item.PdfPath);
                break;

            case SearchResultKind.CourseFile when !string.IsNullOrWhiteSpace(item.PdfPath):
                if (item.PdfPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    OpenPdfInReader(item.PdfPath);
                else if (
                    ScholarFlow.Core.Knowledge.StudySourceClassifier.IsViewableInSourceViewer(
                        item.PdfPath
                    )
                )
                    OpenFileInSourceViewer(item.PdfPath);
                else
                    ExternalFileLauncher.OpenLocalFile(item.PdfPath);
                break;

            case SearchResultKind.Quiz when item.Quiz is not null:
                var qCourse = Courses.FirstOrDefault(c =>
                    c.FolderName.Equals(item.Quiz.CourseFolder, StringComparison.OrdinalIgnoreCase)
                );
                await SelectQuizStudioViewAsync(qCourse, item.Quiz.SourcePdfPath);
                QuizStudio.StartQuizSession(
                    item.Quiz,
                    resumeSavedProgress: item.Quiz.HasInProgressAttempt
                );
                break;

            case SearchResultKind.Task when item.Task is not null:
                var task = item.Task;
                if (!string.IsNullOrWhiteSpace(task.CourseFolder))
                {
                    var course = Courses.FirstOrDefault(c =>
                        c.FolderName.Equals(task.CourseFolder, StringComparison.OrdinalIgnoreCase)
                    );
                    if (course is not null)
                    {
                        ActiveMode = WorkspaceTabMode.CourseWorkspace;
                        ActiveSmartView = SmartViewKind.Course;
                        ActiveCourse = course;
                        ShowCourseMaterialsSubTab = false;
                    }
                    else
                    {
                        ActiveMode = WorkspaceTabMode.TaskList;
                        ActiveSmartView = task.IsCompleted
                            ? SmartViewKind.Completed
                            : SmartViewKind.All;
                    }
                }
                else
                {
                    ActiveMode = WorkspaceTabMode.TaskList;
                    ActiveSmartView = task.IsCompleted
                        ? SmartViewKind.Completed
                        : SmartViewKind.Inbox;
                }
                RecomputeVisibleTasks();
                SelectTask(task, openInspector: true);
                break;
        }
    }

    private static readonly HashSet<string> QuickSearchIgnoredManifests = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "packet.json",
        "term.json",
        "course.json",
        "syllabus.json",
    };

    private void RecomputeSearchResults()
    {
        var q = SearchQuery?.Trim() ?? string.Empty;
        var results = new List<SearchResultItem>();

        bool wantCourses = SearchCategoryFilter is "All" or "Courses";
        bool wantTasks = SearchCategoryFilter is "All" or "Tasks";
        bool wantQuizzes = SearchCategoryFilter is "All" or "Quizzes";
        bool wantFiles = (SearchCategoryFilter is "All" or "Files") && !ExcludeFilesInQuickSearch;

        // 1. Matching Courses
        if (wantCourses)
        {
            foreach (var c in Courses.Where(c => !c.IsArchived))
            {
                if (QuickSearchMatcher.Matches(q, c.Title, c.Alias, c.CourseCode))
                {
                    results.Add(
                        new SearchResultItem(
                            SearchResultKind.Course,
                            c.Title,
                            c.CourseCode ?? "Course Workspace",
                            $"#{c.Alias}",
                            Course: c
                        )
                    );
                }
            }
        }

        // 2. Matching Tasks (SQLite FTS5 + QuickSearchMatcher fallback)
        if (wantTasks)
        {
            var allNodes = EnumerateAllTaskNodes(AllTasks).ToList();
            var byId = allNodes.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
            var matchedTasks = new List<TaskItem>();

            if (!string.IsNullOrWhiteSpace(q))
            {
                var ftsIds = ScholarFlowDatabase.SearchTaskIdsFts(BasinRoot, q, limit: 18);
                foreach (var ftsId in ftsIds)
                {
                    if (byId.TryGetValue(ftsId, out var matched))
                        matchedTasks.Add(matched);
                }
            }

            if (matchedTasks.Count < 16)
            {
                var seenIds = new HashSet<string>(
                    matchedTasks.Select(t => t.Id),
                    StringComparer.OrdinalIgnoreCase
                );
                var candidateTasks = allNodes
                    .OrderBy(t => t.IsCompleted ? 1 : 0)
                    .ThenByDescending(t => UrgencyCalculator.Explain(t).Score)
                    .ToList();

                foreach (var t in candidateTasks)
                {
                    if (seenIds.Contains(t.Id))
                        continue;

                    if (
                        QuickSearchMatcher.Matches(
                            q,
                            t.Title,
                            t.CourseAlias,
                            t.Term,
                            t.Labels.Count > 0 ? string.Join(' ', t.Labels) : null
                        )
                    )
                    {
                        matchedTasks.Add(t);
                        if (matchedTasks.Count >= 18)
                            break;
                    }
                }
            }

            foreach (var t in matchedTasks)
            {
                var sub = t.DueDate.HasValue
                    ? $"Due {t.DueDate.Value:MMM d}"
                    : (t.IsCompleted ? "Completed" : "No due date");
                if (!string.IsNullOrWhiteSpace(t.ParentTaskId))
                    sub = $"Subtask · {sub}";
                var badge = !string.IsNullOrWhiteSpace(t.CourseAlias)
                    ? $"#{t.CourseAlias}"
                    : $"P{(int)t.Priority}";
                results.Add(
                    new SearchResultItem(SearchResultKind.Task, t.Title, sub, badge, Task: t)
                );

                if (SearchCategoryFilter == "All" && results.Count >= 18)
                    break;
            }
        }

        // 3. Matching Master PDFs & Course Files
        if (wantFiles && !string.IsNullOrWhiteSpace(BasinRoot) && Directory.Exists(BasinRoot))
        {
            var scoredFiles = new List<(int Score, SearchResultItem Item)>();
            foreach (var c in Courses.Where(c => !c.IsArchived))
            {
                var masterPdf = MasterPdfService.FindExistingMasterPdfPath(BasinRoot, c.FolderName);
                if (masterPdf is not null)
                {
                    int score = QuickSearchMatcher.Score(
                        q,
                        $"{c.Title} Master Study Packet",
                        Path.GetFileName(masterPdf),
                        c.Alias,
                        "pdf master file"
                    );
                    if (score > 0)
                    {
                        scoredFiles.Add(
                            (
                                score + 10,
                                new SearchResultItem(
                                    SearchResultKind.PdfDocument,
                                    $"{c.Title} — Master Study Packet",
                                    Path.GetFileName(masterPdf),
                                    "Master PDF",
                                    Course: c,
                                    PdfPath: masterPdf
                                )
                            )
                        );
                    }
                }

                var courseDir = Path.Combine(BasinRoot, c.FolderName);
                if (!Directory.Exists(courseDir))
                    continue;

                try
                {
                    foreach (
                        var file in Directory.EnumerateFiles(
                            courseDir,
                            "*",
                            SearchOption.AllDirectories
                        )
                    )
                    {
                        var fileName = Path.GetFileName(file);
                        if (
                            fileName.StartsWith('.')
                            || QuickSearchIgnoredManifests.Contains(fileName)
                        )
                            continue;

                        var rel = Path.GetRelativePath(courseDir, file).Replace('\\', '/');
                        if (
                            rel.StartsWith('.')
                            || rel.Contains("/.", StringComparison.Ordinal)
                            || rel.StartsWith("Master_PDF/", StringComparison.OrdinalIgnoreCase)
                        )
                            continue;

                        int score = QuickSearchMatcher.Score(
                            q,
                            fileName,
                            rel.Replace('_', ' '),
                            c.Title,
                            c.Alias,
                            "file"
                        );
                        if (score > 0)
                        {
                            var folderPart = Path.GetDirectoryName(rel)?.Replace('_', ' ');
                            var sub = string.IsNullOrWhiteSpace(folderPart)
                                ? c.Title
                                : $"{c.Title} · {folderPart}";
                            var ext = Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();
                            scoredFiles.Add(
                                (
                                    score,
                                    new SearchResultItem(
                                        SearchResultKind.CourseFile,
                                        fileName,
                                        sub,
                                        string.IsNullOrWhiteSpace(ext) ? "FILE" : ext,
                                        Course: c,
                                        PdfPath: file
                                    )
                                )
                            );
                        }
                    }
                }
                catch
                {
                    // Skip unreadable course directories
                }
            }

            int maxFiles = SearchCategoryFilter == "Files" ? 30 : 10;
            foreach (var (_, item) in scoredFiles.OrderByDescending(x => x.Score).Take(maxFiles))
                results.Add(item);
        }

        // 4. Matching Test & Recall Quizzes (by title, term, course, or question prompt/tag)
        if (wantQuizzes)
        {
            foreach (var qz in QuizStudio.SavedQuizzes)
            {
                bool matchesQuiz =
                    QuickSearchMatcher.Matches(
                        q,
                        qz.Title,
                        qz.CourseTitle,
                        qz.Term,
                        "quiz test recall"
                    )
                    || qz.Questions.Any(qq =>
                        QuickSearchMatcher.Matches(q, qq.Prompt, qq.TopicTag)
                    );

                if (matchesQuiz)
                {
                    var sub =
                        $"{qz.CourseTitle} · {qz.Questions.Count} items"
                        + (
                            qz.HasInProgressAttempt
                                ? $" · In progress (Q{(qz.InProgressQuestionIndex ?? 0) + 1})"
                                : ""
                        );
                    results.Add(
                        new SearchResultItem(
                            SearchResultKind.Quiz,
                            qz.Title,
                            sub,
                            qz.Term ?? "Quiz",
                            Quiz: qz
                        )
                    );
                }
            }
        }

        SearchResults = [.. results.Take(24)];
        SelectedSearchResultIndex = SearchResults.Count > 0 ? 0 : -1;
    }
}
