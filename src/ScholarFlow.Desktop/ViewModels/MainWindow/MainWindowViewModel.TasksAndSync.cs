using Avalonia.Threading;
using ScholarFlow.Core.Markdown;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Services;
using ScholarFlow.Core.Sync;
using ScholarFlow.Core.Tasks;
using ScholarFlow.Desktop.Services;
using ScholarFlow.Desktop.Theme;

namespace ScholarFlow.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    public async Task ToggleTaskCompletedAsync(TaskItem task)
    {
        await _lifecycleService.SetCompletedAsync(BasinRoot, task, !task.IsCompleted);
        StatusText = !task.IsCompleted ? $"Completed: {task.Title}" : $"Reopened: {task.Title}";
        await ReloadBasinDataAsync();
    }

    public async Task SaveInspectorEditsAsync(
        TaskItem task,
        string newTitle,
        string? newDueDateIso,
        TaskPriority newPriority,
        int newDifficulty
    )
    {
        if (!string.IsNullOrWhiteSpace(newTitle))
            task.Title = newTitle.Trim();

        if (string.IsNullOrWhiteSpace(newDueDateIso))
            task.DueDate = null;
        else if (DateOnly.TryParse(newDueDateIso.Trim(), out var parsedDue))
            task.DueDate = parsedDue;

        task.Priority = newPriority;
        task.Difficulty = Math.Clamp(newDifficulty, 1, 5);

        await MarkdownTaskCodec.UpdateTaskInFileAsync(BasinRoot, task);
        StatusText = $"Saved changes to '{task.Title}'";
        await ReloadBasinDataAsync();
    }

    public TaskItem? GetParentTask(TaskItem task) =>
        TaskItem.FindParentOf(AllTasks, task.Id)
        ?? (
            !string.IsNullOrWhiteSpace(task.ParentTaskId)
                ? TaskItem.FindById(AllTasks, task.ParentTaskId)
                : null
        );

    public async Task AddSubtaskAsync(TaskItem parentTask, string subtaskInput)
    {
        if (string.IsNullOrWhiteSpace(subtaskInput))
            return;

        parentTask.EnsureChildTasksSynced();

        var parsed = QuickAddParser.Parse(subtaskInput.Trim(), Courses);
        var child = parsed.Task;
        if (string.IsNullOrWhiteSpace(child.Title))
            child.Title = subtaskInput.Trim();

        child.Id = $"{parentTask.Id}:sub-{parentTask.ChildTasks.Count + 1}";
        child.ParentTaskId = parentTask.Id;
        child.CourseFolder ??= parentTask.CourseFolder;
        child.CourseAlias ??= parentTask.CourseAlias;
        child.Term ??= parentTask.Term;
        child.Section ??= parentTask.Section;
        child.SourceMarkdownPath = parentTask.SourceMarkdownPath;

        parentTask.ChildTasks.Add(child);
        parentTask.EnsureChildTasksSynced();

        await MarkdownTaskCodec.UpdateTaskInFileAsync(BasinRoot, parentTask);
        StatusText = $"Added subtask '{child.Title}' under '{parentTask.Title}'";
        await ReloadBasinDataAsync();
    }

    public async Task ToggleSubtaskAsync(TaskItem parentTask, int subIndex)
    {
        parentTask.EnsureChildTasksSynced();
        if (subIndex < 0 || subIndex >= parentTask.ChildTasks.Count)
            return;

        var child = parentTask.ChildTasks[subIndex];
        child.IsCompleted = !child.IsCompleted;
        child.CompletedDate = child.IsCompleted ? DateOnly.FromDateTime(DateTime.Today) : null;
        parentTask.EnsureChildTasksSynced();

        await MarkdownTaskCodec.UpdateTaskInFileAsync(BasinRoot, parentTask);
        StatusText = child.IsCompleted
            ? $"Completed subtask: {child.Title}"
            : $"Reopened subtask: {child.Title}";
        await ReloadBasinDataAsync();
    }

    public async Task ToggleChildTaskAsync(TaskItem parentTask, TaskItem childTask)
    {
        parentTask.EnsureChildTasksSynced();
        var idx = parentTask.ChildTasks.FindIndex(c =>
            string.Equals(c.Id, childTask.Id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.Title, childTask.Title, StringComparison.OrdinalIgnoreCase)
        );
        if (idx >= 0)
        {
            await ToggleSubtaskAsync(parentTask, idx);
        }
        else
        {
            await ToggleTaskCompletedAsync(childTask);
        }
    }

    public async Task DeleteTaskAsync(TaskItem task)
    {
        var parent = GetParentTask(task);
        await MarkdownTaskCodec.DeleteTaskFromFileAsync(BasinRoot, task);
        if (SelectedTask?.Id == task.Id)
            SelectedTask = parent;
        StatusText = $"Deleted task '{task.Title}'";
        await ReloadBasinDataAsync();
    }

    public async Task RegisterOngoingFileAsync(UnregisteredOngoingFile file)
    {
        var created = await _lifecycleService.RegisterOngoingFileAsync(BasinRoot, file);
        StatusText = $"Registered '{file.FileName}' as task";
        await ReloadBasinDataAsync();
        SelectTask(created);
    }

    public async Task SetTaskDueDateAsync(TaskItem task, DateOnly? dueDate)
    {
        if (task.DueDate == dueDate)
            return;

        task.DueDate = dueDate;
        if (!dueDate.HasValue)
            task.DueTime = null;
        else if (!task.DueTime.HasValue)
            task.DueTime = new TimeOnly(0, 0);

        await MarkdownTaskCodec.UpdateTaskInFileAsync(BasinRoot, task);
        StatusText = dueDate.HasValue
            ? $"Due date set to {dueDate.Value:MMM d, yyyy} ({task.DueTime:h:mm tt}) for '{task.Title}'"
            : $"Cleared due date for '{task.Title}'";
        await ReloadBasinDataAsync();
    }

    public async Task SetTaskDueTimeAsync(TaskItem task, TimeOnly? dueTime)
    {
        if (task.DueTime == dueTime)
            return;

        task.DueTime = dueTime;
        if (dueTime.HasValue && !task.DueDate.HasValue)
            task.DueDate = DateOnly.FromDateTime(DateTime.Today);

        await MarkdownTaskCodec.UpdateTaskInFileAsync(BasinRoot, task);
        StatusText = dueTime.HasValue
            ? $"Due time set to {dueTime.Value:h:mm tt} for '{task.Title}'"
            : $"Cleared due time for '{task.Title}'";
        await ReloadBasinDataAsync();
    }

    public async Task SetTaskTermAsync(TaskItem task, string? term)
    {
        var normalized = TaskItem.NormalizeTerm(term);
        task.Term = normalized;

        // Remove old term tags and add the new term tag
        task.Labels.RemoveAll(l => TaskItem.NormalizeTerm(l) is not null);
        if (normalized is not null)
            task.Labels.Insert(0, normalized.ToLowerInvariant());

        await MarkdownTaskCodec.UpdateTaskInFileAsync(BasinRoot, task);
        StatusText = normalized is not null
            ? $"Assigned '{task.Title}' to {normalized}"
            : $"Cleared term for '{task.Title}'";
        await ReloadBasinDataAsync();
    }

    public async Task SetTaskSectionAsync(TaskItem task, string? section)
    {
        task.Section = string.IsNullOrWhiteSpace(section) ? null : section.Trim();
        await MarkdownTaskCodec.UpdateTaskInFileAsync(BasinRoot, task);
        StatusText = $"Updated section for '{task.Title}'";
        await ReloadBasinDataAsync();
    }

    public async Task AddTaskLabelAsync(TaskItem task, string label)
    {
        var clean = label.Trim().TrimStart('@').Replace(' ', '-').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(clean))
            return;

        if (!task.Labels.Contains(clean, StringComparer.OrdinalIgnoreCase))
        {
            task.Labels.Add(clean);
            var maybeTerm = TaskItem.NormalizeTerm(clean);
            if (maybeTerm is not null)
                task.Term = maybeTerm;

            await MarkdownTaskCodec.UpdateTaskInFileAsync(BasinRoot, task);
            StatusText = $"Added @{clean} to '{task.Title}'";
            await ReloadBasinDataAsync();
        }
    }

    public async Task RemoveTaskLabelAsync(TaskItem task, string label)
    {
        if (task.Labels.RemoveAll(l => l.Equals(label, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            await MarkdownTaskCodec.UpdateTaskInFileAsync(BasinRoot, task);
            StatusText = $"Removed @{label} from '{task.Title}'";
            await ReloadBasinDataAsync();
        }
    }

    public async Task AttachFileToTaskAsync(TaskItem task, string fullFilePath, bool isSubmission)
    {
        if (!File.Exists(fullFilePath))
            return;

        var courseDir = string.IsNullOrWhiteSpace(task.CourseFolder)
            ? BasinRoot
            : Path.Combine(BasinRoot, task.CourseFolder);

        string relPath;
        if (fullFilePath.StartsWith(courseDir, StringComparison.OrdinalIgnoreCase))
        {
            relPath = Path.GetRelativePath(courseDir, fullFilePath).Replace('\\', '/');
        }
        else
        {
            // Copy external file into ongoing/ or completed/ inside the course folder
            var targetSub = isSubmission ? (task.IsCompleted ? "completed" : "ongoing") : "ongoing";
            var destDir = Path.Combine(courseDir, targetSub);
            Directory.CreateDirectory(destDir);
            var destPath = Path.Combine(destDir, Path.GetFileName(fullFilePath));
            if (!File.Exists(destPath))
                File.Copy(fullFilePath, destPath);
            relPath = $"{targetSub}/{Path.GetFileName(fullFilePath)}";
        }

        if (isSubmission)
        {
            if (!task.SubmissionFiles.Contains(relPath, StringComparer.OrdinalIgnoreCase))
                task.SubmissionFiles.Add(relPath);
        }
        else
        {
            if (!task.InstructionFiles.Contains(relPath, StringComparer.OrdinalIgnoreCase))
                task.InstructionFiles.Add(relPath);
        }

        if (!task.LinkedFiles.Contains(relPath, StringComparer.OrdinalIgnoreCase))
            task.LinkedFiles.Add(relPath);

        await MarkdownTaskCodec.UpdateTaskInFileAsync(BasinRoot, task);
        StatusText = $"Linked '{Path.GetFileName(relPath)}' to '{task.Title}'";
        await ReloadBasinDataAsync();
    }

    public void ToggleTaskSubtasksExpanded(TaskItem task)
    {
        task.IsSubtasksExpanded = !task.IsSubtasksExpanded;
        NotifyStateChanged(UiChangeKind.SearchOrTaskList);
    }

    public void OpenTaskFile(TaskItem task, string relativeOrFullFilePath)
    {
        var courseDir = string.IsNullOrWhiteSpace(task.CourseFolder)
            ? BasinRoot
            : Path.Combine(BasinRoot, task.CourseFolder);
        var fullPath = Path.IsPathRooted(relativeOrFullFilePath)
            ? relativeOrFullFilePath
            : Path.Combine(courseDir, relativeOrFullFilePath);

        if (fullPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath))
        {
            OpenPdfInReader(fullPath, associatedTask: task);
            return;
        }

        if (
            Core.Knowledge.StudySourceClassifier.IsViewableInSourceViewer(fullPath)
            && File.Exists(fullPath)
        )
        {
            OpenFileInSourceViewer(fullPath, sectionTitle: task.Title);
            return;
        }

        ExternalFileLauncher.OpenLocalFile(fullPath);
    }

    public void OpenPrimaryTaskFileOrNote(TaskItem? task)
    {
        if (task is null)
            return;

        var primaryPdf = task.PrimaryPdfFile;
        if (!string.IsNullOrWhiteSpace(primaryPdf))
        {
            OpenTaskFile(task, primaryPdf);
            return;
        }

        var firstFile = (task.AllFiles.Count > 0 ? task.AllFiles[0] : null) ?? task.NotePath;
        if (!string.IsNullOrWhiteSpace(firstFile))
        {
            OpenTaskFile(task, firstFile);
            return;
        }

        if (!string.IsNullOrWhiteSpace(task.SourceMarkdownPath))
            ExternalFileLauncher.OpenLocalFile(task.SourceMarkdownPath);
    }

    public Task OpenTaskInElmsAppAsync(TaskItem? task)
    {
        var url = task?.LmsUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            if (ActiveCourse?.CourseUrl is not null)
                url = ActiveCourse.CourseUrl;
            else
                url = "/user_dashboard";
        }

        return OpenUrlInProviderAppAsync(url);
    }

    public async Task OpenUrlInProviderAppAsync(string urlOrPath)
    {
        var provider = ActiveSyncProvider;
        if (!provider.Capabilities.SupportsInteractiveSessionBrowser)
        {
            var baseUrl = BasinConfig.GetProviderSetting("baseUrl", "");
            var targetUrl =
                urlOrPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || urlOrPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? urlOrPath
                : !string.IsNullOrWhiteSpace(baseUrl)
                    ? $"{baseUrl.TrimEnd('/')}/{urlOrPath.TrimStart('/')}"
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(targetUrl))
                ExternalFileLauncher.OpenUrl(targetUrl);
            return;
        }

        StatusText = provider.Capabilities.EnforcesSingleConcurrentSession
            ? $"Opening {provider.DisplayName} window (background polling paused)..."
            : $"Opening {provider.DisplayName} window...";
        NotifyStateChanged(UiChangeKind.FullLayout);

        await SyncCoordinator.OpenExternalUrlWithPauseShieldAsync(
            BasinRoot,
            urlOrPath,
            async () =>
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    StatusText =
                        $"{provider.DisplayName} window closed — refreshing assignments...";
                    NotifyStateChanged(UiChangeKind.FullLayout);
                    await RunFastPollAsync();
                });
            }
        );

        NotifyStateChanged(UiChangeKind.FullLayout);
    }

    public bool IsPollingPaused =>
        SyncCoordinator.IsProviderPaused(ActiveSyncProvider.GetConcurrencyKey(BasinConfig));

    public void TogglePollingPaused()
    {
        var provider = ActiveSyncProvider;
        var concurrencyKey = provider.GetConcurrencyKey(BasinConfig);
        if (SyncCoordinator.IsProviderPaused(concurrencyKey))
        {
            SyncCoordinator.ResumeProvider(concurrencyKey);
            StatusText = $"Background {provider.DisplayName} polling resumed.";
        }
        else
        {
            SyncCoordinator.PauseProvider(concurrencyKey);
            StatusText =
                $"Background {provider.DisplayName} polling paused (single-session protection).";
        }
        NotifyStateChanged(UiChangeKind.FullLayout);
    }

    public async Task RunFastPollAsync()
    {
        if (IsSyncing)
            return;

        IsSyncing = true;
        StatusText = "Polling eLMS assignments...";
        NotifyStateChanged(UiChangeKind.StatusOnly);

        try
        {
            var notifs = await SyncCoordinator.TriggerFastPollAsync(BasinRoot);
            StatusText = $"Fast poll complete ({notifs.Count} update(s))";
            await ReloadBasinDataAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Poll error: {ex.Message}";
        }
        finally
        {
            IsSyncing = false;
            NotifyStateChanged(UiChangeKind.StatusOnly);
        }
    }

    public async Task RunFullSyncAsync()
    {
        if (IsSyncing)
            return;

        IsSyncing = true;
        StatusText = "Running full eLMS sync...";
        NotifyStateChanged(UiChangeKind.StatusOnly);

        var progress = new Progress<SyncProgress>(p =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusText =
                    $"[{p.Stage}] {(p.CourseTitle is not null ? $"{p.CourseTitle}: " : "")}{p.Message}";
                NotifyStateChanged(UiChangeKind.StatusOnly);
            });
        });

        try
        {
            var report = await SyncCoordinator.TriggerFullSyncAsync(BasinRoot, progress: progress);

            StatusText =
                $"Synced {report.CoursesSynced} courses (+{report.FilesDownloaded} files, {report.MasterPdfsCompiled} PDFs compiled)";
            await ReloadBasinDataAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Sync failed: {ex.Message}";
        }
        finally
        {
            IsSyncing = false;
            NotifyStateChanged(UiChangeKind.StatusOnly);
        }
    }

    public async Task CompileCourseMasterPdfAsync(BasinCourseEntry course, bool force = true)
    {
        IsSyncing = true;
        StatusText = $"Compiling {course.Title} Master PDF...";
        NotifyStateChanged(UiChangeKind.StatusOnly);

        try
        {
            var res = await _pdfService.CompileIfStaleAsync(
                BasinRoot,
                course.FolderName,
                course.Title,
                force
            );
            StatusText = res.WasRegenerated
                ? $"Compiled {Path.GetFileName(res.OutputPdfPath)} ({res.PageCount} pages)"
                : $"{Path.GetFileName(res.OutputPdfPath)} is already up to date";

            if (File.Exists(res.OutputPdfPath))
                OpenPdfInReader(res.OutputPdfPath);
        }
        catch (Exception ex)
        {
            StatusText = $"PDF compile error: {ex.Message}";
        }
        finally
        {
            IsSyncing = false;
            NotifyStateChanged(UiChangeKind.StatusOnly);
        }
    }

    public async Task RunCourseSyncAsync(BasinCourseEntry course)
    {
        if (IsSyncing)
            return;

        IsSyncing = true;
        StatusText = $"Syncing {course.Title} from eLMS...";
        NotifyStateChanged(UiChangeKind.StatusOnly);

        try
        {
            var progress = new Progress<SyncProgress>(p =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    StatusText = p.Message;
                    NotifyStateChanged(UiChangeKind.StatusOnly);
                });
            });

            var report = await SyncCoordinator.TriggerCourseSyncAsync(
                BasinRoot,
                course.FolderName,
                progress: progress
            );

            StatusText =
                report.Errors.Count > 0
                    ? $"{course.Title} sync finished with {report.Errors.Count} warning(s): {report.Errors[0]}"
                    : $"Synced {course.Title}: {report.FilesDownloaded} new, {report.FilesUpdated} updated, {report.MasterPdfsCompiled} PDF(s) compiled";
            await ReloadBasinDataAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"{course.Title} sync failed: {ex.Message}";
            NotifyStateChanged(UiChangeKind.StatusOnly);
        }
        finally
        {
            IsSyncing = false;
            NotifyStateChanged(UiChangeKind.FullLayout);
        }
    }

    public async Task RefreshTaskFromElmsAsync(TaskItem task)
    {
        if (IsSyncing || string.IsNullOrWhiteSpace(task.LmsUrl))
            return;

        IsSyncing = true;
        StatusText = $"Refreshing '{task.Title}' from eLMS...";
        NotifyStateChanged(UiChangeKind.StatusOnly);

        try
        {
            var ok = await SyncCoordinator.TriggerSingleTaskSyncAsync(BasinRoot, task);
            StatusText = ok
                ? $"Refreshed '{task.Title}' from eLMS"
                : $"Could not refresh '{task.Title}'";
            await ReloadBasinDataAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Refresh failed: {ex.Message}";
            NotifyStateChanged(UiChangeKind.StatusOnly);
        }
        finally
        {
            IsSyncing = false;
            NotifyStateChanged(UiChangeKind.FullLayout);
        }
    }

    public async Task ToggleThemeAsync()
    {
        var next = ThemeService.Toggle();
        Settings.Theme = next;
        await AppSettingsStore.SaveAsync(Settings);
        NotifyStateChanged();
    }

    public void ToggleNotificationDrawer()
    {
        IsNotificationDrawerOpen = !IsNotificationDrawerOpen;
        if (IsNotificationDrawerOpen)
            UnreadNotificationsCount = 0;
        NotifyStateChanged(UiChangeKind.NotificationsOnly);
    }

    private void OnCoordinatorNotification(object? sender, BasinNotificationEvent ev)
    {
        Dispatcher.UIThread.Post(() =>
        {
            bool isActiveBasin =
                !string.IsNullOrWhiteSpace(BasinRoot)
                && string.Equals(
                    Path.GetFullPath(BasinRoot),
                    Path.GetFullPath(ev.BasinRoot),
                    StringComparison.OrdinalIgnoreCase
                );

            if (isActiveBasin)
            {
                AddNotification(ev.Notification);
                _ = ReloadBasinDataAsync();
            }
            else
            {
                var basinState = SyncCoordinator.GetBasinState(ev.BasinRoot);
                var prefix = basinState is not null ? $"[{basinState.Config.Name}] " : "";
                AddNotification(
                    ev.Notification with
                    {
                        Title = $"{prefix}{ev.Notification.Title}",
                    }
                );
            }
        });
    }

    private void AddNotification(AppNotification notification)
    {
        Notifications.Insert(0, notification);
        if (!IsNotificationDrawerOpen)
            UnreadNotificationsCount++;
        if (Settings.EnableDesktopNotifications)
            _notificationService.SendSystemNotification(notification);
        NotifyStateChanged(UiChangeKind.NotificationsOnly);
    }

    private void RecomputeVisibleTasks()
    {
        var viewKind =
            ActiveMode == WorkspaceTabMode.CourseWorkspace && ActiveCourse is not null
                ? SmartViewKind.Course
                : ActiveSmartView;

        var targetVal = viewKind switch
        {
            SmartViewKind.Course => ActiveCourse?.FolderName,
            SmartViewKind.Label => ActiveLabelFilter,
            SmartViewKind.Term => ActiveTermFilter,
            SmartViewKind.Completed => CompletedCourseFilter,
            _ => null,
        };

        // Include subtasks with due dates in Today & Upcoming views
        var querySource = viewKind is SmartViewKind.Today or SmartViewKind.Upcoming
            ? EnumerateAllTaskNodes(AllTasks)
            : AllTasks;

        VisibleTasks = TaskQueryService.Filter(
            querySource,
            viewKind,
            targetValue: targetVal,
            searchQuery: IsSearchModalOpen ? null : SearchQuery,
            termFilter: ActiveTermFilter
        );

        if (SelectedTask is not null)
        {
            var prev = SelectedTask;
            var allNodes = EnumerateAllTaskNodes(AllTasks).ToList();
            SelectedTask =
                TaskItem.FindById(AllTasks, prev.Id)
                ?? allNodes.FirstOrDefault(t =>
                    (
                        !string.IsNullOrWhiteSpace(prev.LmsId)
                        && string.Equals(t.LmsId, prev.LmsId, StringComparison.OrdinalIgnoreCase)
                    )
                    || (
                        string.Equals(
                            t.CourseFolder,
                            prev.CourseFolder,
                            StringComparison.OrdinalIgnoreCase
                        )
                        && string.Equals(t.Title, prev.Title, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(
                            t.ParentTaskId ?? "",
                            prev.ParentTaskId ?? "",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                )
                ?? (VisibleTasks.Count > 0 ? VisibleTasks[0] : null);
            SelectedTaskNoteContent = LoadTaskNotePreview(SelectedTask);
        }
        else if (VisibleTasks.Count > 0)
        {
            SelectedTask = VisibleTasks[0];
            SelectedTaskNoteContent = LoadTaskNotePreview(SelectedTask);
        }
    }

    private string? LoadTaskNotePreview(TaskItem? task)
    {
        if (task is null || string.IsNullOrWhiteSpace(task.NotePath))
            return null;

        var courseDir = string.IsNullOrWhiteSpace(task.CourseFolder)
            ? BasinRoot
            : Path.Combine(BasinRoot, task.CourseFolder);
        var full = Path.Combine(courseDir, task.NotePath);
        if (!File.Exists(full))
            return null;

        try
        {
            return File.ReadAllText(full);
        }
        catch
        {
            return null;
        }
    }
}
