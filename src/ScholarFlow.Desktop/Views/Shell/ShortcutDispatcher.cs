using Avalonia.Controls;
using Avalonia.Input;
using ScholarFlow.Core.Quiz;
using ScholarFlow.Core.Tasks;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views.Shell;

/// <summary>
/// Centralized keyboard shortcut dispatcher decoupled from MainWindow.
/// </summary>
public static class ShortcutDispatcher
{
    private static DateTime _lastOrganizerGPressUtc = DateTime.MinValue;

    public static async Task HandleKeyDownAsync(
        Window window,
        MainWindowViewModel vm,
        KeyEventArgs e
    )
    {
        if (e.Handled)
            return;

        // Global Escape always untoggles/closes the top-most floating modal or inspector drawer
        if (e.Key == Key.Escape)
        {
            // Floating overlays (Knowledge Hub, Quick Search, Quick Add, Settings, etc.) take top priority
            // even when opened on top of PDF Reader, Source Viewer, or Quiz Studio.
            if (
                vm.IsPacketOrganizerModalOpen
                || vm.IsAddCourseModalOpen
                || vm.IsSettingsModalOpen
                || (vm.IsOnboardingOpen && !string.IsNullOrWhiteSpace(vm.BasinRoot))
                || vm.IsQuickAddModalOpen
                || vm.IsKnowledgeSearchModalOpen
                || vm.IsSearchModalOpen
                || vm.IsShortcutsModalOpen
                || vm.IsNotificationDrawerOpen
            )
            {
                if (vm.CloseTopOverlayOrDrawer())
                {
                    e.Handled = true;
                    window.Focus();
                    return;
                }
            }
            if (vm.ActiveMode == WorkspaceTabMode.QuizStudio)
            {
                if (vm.QuizStudio.ZoomedImagePath is not null)
                {
                    e.Handled = true;
                    vm.QuizStudio.CloseImageZoom();
                    return;
                }
                if (vm.QuizStudio.IsPageGridOpen)
                {
                    e.Handled = true;
                    vm.QuizStudio.ClosePageGrid();
                    return;
                }
                if (vm.QuizStudio.IsDedicatedSnipperOpen)
                {
                    e.Handled = true;
                    vm.QuizStudio.CloseDedicatedSnipper();
                    return;
                }
                if (vm.QuizStudio.EditingQuestionDraft is not null)
                {
                    e.Handled = true;
                    vm.QuizStudio.CancelEditingQuestionDraft();
                    return;
                }
                if (vm.QuizStudio.IsDeckAiFixModalOpen)
                {
                    e.Handled = true;
                    vm.QuizStudio.CloseDeckAiFixModal();
                    return;
                }
                if (!string.IsNullOrWhiteSpace(vm.QuizStudio.FlaggingQuestionId))
                {
                    e.Handled = true;
                    vm.QuizStudio.CloseQuestionAiFixModal();
                    return;
                }
            }
            if (vm.ActiveMode == WorkspaceTabMode.PdfReader)
            {
                e.Handled = true;
                if (vm.PdfReader.IsSearchBarOpen)
                    vm.PdfReader.CloseSearchBar();
                else
                    vm.ReturnFromPdfReader();
                return;
            }
            if (vm.ActiveMode == WorkspaceTabMode.SourceViewer)
            {
                e.Handled = true;
                vm.ReturnFromSourceViewer();
                return;
            }
            if (vm.CloseTopOverlayOrDrawer())
            {
                e.Handled = true;
                window.Focus();
                return;
            }
        }

        // Global Ctrl+K / Ctrl+F / Ctrl+B / Ctrl+S + PDF Zoom shortcuts
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key is Key.OemPlus or Key.Add)
            {
                if (vm.ActiveMode == WorkspaceTabMode.PdfReader)
                {
                    e.Handled = true;
                    vm.PdfReader.ZoomIn();
                    return;
                }
                if (vm.ActiveMode == WorkspaceTabMode.SourceViewer)
                {
                    e.Handled = true;
                    vm.SourceViewer.ZoomIn();
                    return;
                }
                if (
                    vm.ActiveMode == WorkspaceTabMode.QuizStudio
                    && vm.QuizStudio.IsDedicatedSnipperOpen
                )
                {
                    e.Handled = true;
                    _ = vm.QuizStudio.ZoomSnipAsync(+25);
                    return;
                }
            }
            if (e.Key is Key.OemMinus or Key.Subtract)
            {
                if (vm.ActiveMode == WorkspaceTabMode.PdfReader)
                {
                    e.Handled = true;
                    vm.PdfReader.ZoomOut();
                    return;
                }
                if (vm.ActiveMode == WorkspaceTabMode.SourceViewer)
                {
                    e.Handled = true;
                    vm.SourceViewer.ZoomOut();
                    return;
                }
                if (
                    vm.ActiveMode == WorkspaceTabMode.QuizStudio
                    && vm.QuizStudio.IsDedicatedSnipperOpen
                )
                {
                    e.Handled = true;
                    _ = vm.QuizStudio.ZoomSnipAsync(-25);
                    return;
                }
            }
            if (e.Key is Key.D0 or Key.NumPad0)
            {
                if (vm.ActiveMode == WorkspaceTabMode.PdfReader)
                {
                    e.Handled = true;
                    vm.PdfReader.ToggleFitToWidth();
                    return;
                }
                if (vm.ActiveMode == WorkspaceTabMode.SourceViewer)
                {
                    e.Handled = true;
                    vm.SourceViewer.ToggleFitToWindow();
                    return;
                }
                if (
                    vm.ActiveMode == WorkspaceTabMode.QuizStudio
                    && vm.QuizStudio.IsDedicatedSnipperOpen
                )
                {
                    e.Handled = true;
                    _ = vm.QuizStudio.ToggleSnipFitToWidthAsync();
                    return;
                }
            }
            if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                e.Handled = true;
                vm.ToggleKnowledgeSearchModal();
                return;
            }
            if (e.Key == Key.F && vm.ActiveMode == WorkspaceTabMode.PdfReader)
            {
                e.Handled = true;
                vm.PdfReader.ToggleSearchBar();
                return;
            }
            if (e.Key is Key.K or Key.F)
            {
                e.Handled = true;
                vm.ToggleSearchModal();
                return;
            }
            if (e.Key == Key.B)
            {
                e.Handled = true;
                vm.ToggleSidebar();
                return;
            }
            if (e.Key == Key.S)
            {
                e.Handled = true;
                await vm.RunFullSyncAsync();
                return;
            }
        }

        // Skip single-key shortcuts when typing inside any TextBox
        if (window.FocusManager?.GetFocusedElement() is TextBox)
            return;

        // Active Quiz Runner hotkeys (1-4 option/SRS pick, Left/P prev, Right/N next)
        if (
            vm.ActiveMode == WorkspaceTabMode.QuizStudio
            && vm.QuizStudio.ActiveSession is not null
            && !vm.QuizStudio.IsShowingResultsSummary
            && !vm.IsSearchModalOpen
        )
        {
            var curQ = vm.QuizStudio.CurrentQuestion;
            if (e.Key is Key.D1 or Key.D2 or Key.D3 or Key.D4)
            {
                int slot = e.Key switch
                {
                    Key.D1 => 0,
                    Key.D2 => 1,
                    Key.D3 => 2,
                    _ => 3,
                };

                if (
                    !vm.QuizStudio.IsAnswerRevealed
                    && curQ is not null
                    && curQ.Kind
                        is QuizQuestionKind.MultipleChoice
                            or QuizQuestionKind.Situational
                            or QuizQuestionKind.TrueFalse
                    && slot < curQ.Options.Count
                )
                {
                    e.Handled = true;
                    await vm.QuizStudio.SubmitAnswerAsync(curQ.Options[slot]);
                    return;
                }

                if (vm.QuizStudio.IsAnswerRevealed)
                {
                    e.Handled = true;
                    int rating = slot switch
                    {
                        0 => 1,
                        1 => 3,
                        2 => 4,
                        _ => 5,
                    };
                    await vm.QuizStudio.RateAndAdvanceAsync(rating);
                    return;
                }
            }
            else if (e.Key is Key.Left or Key.P)
            {
                e.Handled = true;
                vm.QuizStudio.PreviousQuestion();
                return;
            }
            else if (e.Key is Key.Right or Key.N)
            {
                e.Handled = true;
                await vm.QuizStudio.AdvanceNextQuestionAsync();
                return;
            }
        }

        bool isOrganizerActive =
            vm.ActiveMode == WorkspaceTabMode.QuizStudio
            && vm.QuizStudio.ActiveSubTab == QuizStudioSubTab.Organizer
            && vm.QuizStudio.ActiveSession is null
            && !vm.QuizStudio.IsDedicatedSnipperOpen
            && vm.QuizStudio.EditingQuestionDraft is null
            && !vm.QuizStudio.IsDeckAiFixModalOpen
            && string.IsNullOrWhiteSpace(vm.QuizStudio.FlaggingQuestionId);

        switch (e.Key)
        {
            case Key.J or Key.Down:
                e.Handled = true;
                if (vm.IsKnowledgeSearchModalOpen)
                    vm.KnowledgeSearch.MoveSelection(+1);
                else if (vm.IsSearchModalOpen)
                    vm.MoveSearchSelection(+1);
                else if (vm.ActiveMode == WorkspaceTabMode.KnowledgeSearch)
                    vm.KnowledgeSearch.MoveSelection(+1);
                else if (vm.ActiveMode == WorkspaceTabMode.PdfReader)
                    vm.PdfReader.NextPage();
                else if (isOrganizerActive)
                    vm.QuizStudio.MoveOrganizerSelection(+1);
                else
                    vm.MoveSelection(+1);
                break;

            case Key.K
            or Key.Up:
                e.Handled = true;
                if (vm.IsKnowledgeSearchModalOpen)
                    vm.KnowledgeSearch.MoveSelection(-1);
                else if (vm.IsSearchModalOpen)
                    vm.MoveSearchSelection(-1);
                else if (vm.ActiveMode == WorkspaceTabMode.KnowledgeSearch)
                    vm.KnowledgeSearch.MoveSelection(-1);
                else if (vm.ActiveMode == WorkspaceTabMode.PdfReader)
                    vm.PdfReader.PreviousPage();
                else if (isOrganizerActive)
                    vm.QuizStudio.MoveOrganizerSelection(-1);
                else
                    vm.MoveSelection(-1);
                break;

            case Key.PageDown when vm.IsKnowledgeSearchModalOpen:
                e.Handled = true;
                vm.KnowledgeSearch.MoveSelection(+5);
                break;

            case Key.PageUp when vm.IsKnowledgeSearchModalOpen:
                e.Handled = true;
                vm.KnowledgeSearch.MoveSelection(-5);
                break;

            case Key.Enter when vm.IsKnowledgeSearchModalOpen:
                e.Handled = true;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    await vm.KnowledgeSearch.QuizThisAsync();
                else
                    vm.KnowledgeSearch.JumpToSource();
                break;

            case Key.PageDown
            or Key.Right when vm.ActiveMode == WorkspaceTabMode.PdfReader:
                e.Handled = true;
                vm.PdfReader.NextPage();
                break;

            case Key.PageUp
            or Key.Left when vm.ActiveMode == WorkspaceTabMode.PdfReader:
                e.Handled = true;
                vm.PdfReader.PreviousPage();
                break;

            case Key.PageDown
            or Key.Right
                when vm.ActiveMode == WorkspaceTabMode.QuizStudio
                    && vm.QuizStudio.IsDedicatedSnipperOpen:
                e.Handled = true;
                _ = vm.QuizStudio.LoadEditorSnipPageAsync(
                    Math.Min(
                        Math.Max(1, vm.QuizStudio.EditingSnipTotalPages),
                        vm.QuizStudio.EditingSnipPageNumber + 1
                    )
                );
                break;

            case Key.PageUp
            or Key.Left
                when vm.ActiveMode == WorkspaceTabMode.QuizStudio
                    && vm.QuizStudio.IsDedicatedSnipperOpen:
                e.Handled = true;
                _ = vm.QuizStudio.LoadEditorSnipPageAsync(
                    Math.Max(1, vm.QuizStudio.EditingSnipPageNumber - 1)
                );
                break;

            case Key.Home when vm.ActiveMode == WorkspaceTabMode.PdfReader:
                e.Handled = true;
                vm.PdfReader.JumpToPage(1);
                break;

            case Key.End when vm.ActiveMode == WorkspaceTabMode.PdfReader:
                e.Handled = true;
                vm.PdfReader.JumpToPage(vm.PdfReader.PageCount);
                break;

            case Key.R when vm.ActiveMode == WorkspaceTabMode.PdfReader:
                e.Handled = true;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    vm.PdfReader.RotateLeft();
                else
                    vm.PdfReader.RotateRight();
                break;

            case Key.Home when isOrganizerActive:
                e.Handled = true;
                vm.QuizStudio.JumpOrganizerSelection(toEnd: false);
                break;

            case Key.End when isOrganizerActive:
                e.Handled = true;
                vm.QuizStudio.JumpOrganizerSelection(toEnd: true);
                break;

            case Key.G:
                e.Handled = true;
                if (vm.ActiveMode == WorkspaceTabMode.PdfReader)
                {
                    vm.PdfReader.JumpToPage(
                        e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? vm.PdfReader.PageCount : 1
                    );
                }
                else if (isOrganizerActive)
                {
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    {
                        _lastOrganizerGPressUtc = DateTime.MinValue;
                        vm.QuizStudio.JumpOrganizerSelection(toEnd: true);
                    }
                    else
                    {
                        var now = DateTime.UtcNow;
                        if ((now - _lastOrganizerGPressUtc).TotalMilliseconds <= 600)
                        {
                            _lastOrganizerGPressUtc = DateTime.MinValue;
                            vm.QuizStudio.JumpOrganizerSelection(toEnd: false);
                        }
                        else
                        {
                            _lastOrganizerGPressUtc = now;
                        }
                    }
                }
                else
                {
                    vm.JumpSelection(toEnd: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                }
                break;

            case Key.D
            or Key.Space
                when vm.SelectedTask is not null && vm.ActiveMode != WorkspaceTabMode.PdfReader:
                e.Handled = true;
                await vm.ToggleTaskCompletedAsync(vm.SelectedTask);
                break;

            case Key.E
                when vm.SelectedTask is not null && vm.ActiveMode != WorkspaceTabMode.PdfReader:
                e.Handled = true;
                vm.ToggleInspector();
                break;

            case Key.Enter
                when vm.SelectedTask is not null && vm.ActiveMode != WorkspaceTabMode.PdfReader:
                e.Handled = true;
                if (!vm.IsInspectorOpen)
                    vm.OpenInspector(vm.SelectedTask);
                else
                    vm.OpenPrimaryTaskFileOrNote(vm.SelectedTask);
                break;

            case Key.P
                when e.KeyModifiers.HasFlag(KeyModifiers.Shift) && vm.EnforcesActiveSingleSession:
                e.Handled = true;
                vm.TogglePollingPaused();
                break;

            case Key.O:
                e.Handled = true;
                await vm.OpenTaskInElmsAppAsync(vm.SelectedTask);
                break;

            case Key.Q:
                e.Handled = true;
                vm.ToggleQuickAddModal();
                break;

            case Key.OemQuestion
            or Key.Divide:
                e.Handled = true;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    vm.ToggleShortcutsModal();
                else
                    vm.ToggleSearchModal();
                break;

            case Key.OemOpenBrackets:
                e.Handled = true;
                if (vm.ActiveMode == WorkspaceTabMode.Calendar)
                    vm.Calendar.PreviousMonth();
                else
                    vm.ToggleSidebar();
                break;

            case Key.OemCloseBrackets when vm.ActiveMode == WorkspaceTabMode.Calendar:
                e.Handled = true;
                vm.Calendar.NextMonth();
                break;

            case Key.T:
                e.Handled = true;
                await vm.ToggleThemeAsync();
                break;

            case Key.R:
                e.Handled = true;
                await vm.RunFastPollAsync();
                break;

            case Key.D1:
                e.Handled = true;
                vm.SelectSmartView(SmartViewKind.Inbox);
                break;

            case Key.D2:
                e.Handled = true;
                vm.SelectSmartView(SmartViewKind.Today);
                break;

            case Key.D3:
                e.Handled = true;
                vm.SelectSmartView(SmartViewKind.Upcoming);
                break;

            case Key.D4:
                e.Handled = true;
                vm.SelectCalendarView();
                break;

            case Key.D5:
                e.Handled = true;
                await vm.SelectQuizStudioViewAsync();
                break;

            case Key.D6:
                e.Handled = true;
                vm.SelectSmartView(SmartViewKind.All);
                break;

            case Key.D7:
                e.Handled = true;
                vm.SelectSmartView(SmartViewKind.Completed);
                break;
        }
    }
}
