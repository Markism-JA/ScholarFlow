using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using ScholarFlow.Core.Quiz;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views;

/// <summary>
/// Responsive, error-proof Manual Question Editor overlay for Deck Organizer.
/// Features a sticky header, live Quality Critic diagnostics, wrapped responsive classification pills
/// (Kind, Cognitive Level, Archetype, @TopicTag), safe MCQ/Ordering option editing, Progressive Hint,
/// Contrastive Explanation template helper, and collapsible Rich Media &amp; Evidence Grounding.
/// </summary>
public static partial class QuizStudioViewBuilder
{
    private static Border BuildFloatingQuestionEditorOverlay(
        MainWindowViewModel vm,
        QuizStudioViewModel studio
    )
    {
        var draft = studio.EditingQuestionDraft!;
        var deck = studio.OrganizerQuiz!;
        int deckIdx = deck.Questions.FindIndex(q =>
            string.Equals(q.Id, draft.Id, StringComparison.OrdinalIgnoreCase)
        );

        // Live single-question quality critic check
        var draftIssues = QuizQualityCritic.Audit([draft]).Issues;
        bool hasBlockingError =
            string.IsNullOrWhiteSpace(draft.Prompt)
            || draftIssues.Any(i => i.Severity == QualityIssueSeverity.Error);

        // Sticky Modal Header (stays pinned above the scrollable body)
        var headerGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 8,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var titleStack = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleStack.Children.Add(
            new TextBlock
            {
                Text = $"Edit Question #{deckIdx + 1} of {deck.Questions.Count}",
                FontSize = 15,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            }
        );
        titleStack.Children.Add(
            new TextBlock
            {
                Text =
                    $"{draft.Kind} · {draft.CognitiveLevel}"
                    + (
                        string.IsNullOrWhiteSpace(draft.Archetype)
                            ? ""
                            : $" · {QuizBlueprintBuilder.FormatArchetypeBadge(draft.Archetype)}"
                    ),
                FontSize = 11,
                Foreground = ThemeService.Brush(ThemeService.TextMuted),
            }
        );
        headerGrid.Children.Add(titleStack);

        var headerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var aiFixHeaderBtn = UiBuilder.GhostButton(
            "Fix with AI",
            MaterialIconKind.AutoFix,
            () => studio.ToggleFlagQuestionForAiFix(draft.Id)
        );
        ToolTip.SetTip(aiFixHeaderBtn, "Repair or refine this question with AI");
        headerActions.Children.Add(aiFixHeaderBtn);

        if (studio.ActiveSession is not null)
        {
            var openDeckBtn = UiBuilder.GhostButton(
                "Open in Deck",
                MaterialIconKind.TuneVariant,
                studio.OpenCurrentQuestionInDeckOrganizer
            );
            ToolTip.SetTip(openDeckBtn, "Open this question in the full Deck Organizer");
            headerActions.Children.Add(openDeckBtn);
        }

        if (studio.ActiveSession is null)
        {
            var prevBtn = UiBuilder.IconButton(
                MaterialIconKind.ChevronLeft,
                () => studio.CommitEditingQuestionDraft(-1),
                16
            );
            prevBtn.IsEnabled = deckIdx > 0;
            headerActions.Children.Add(prevBtn);

            var nextBtn = UiBuilder.IconButton(
                MaterialIconKind.ChevronRight,
                () => studio.CommitEditingQuestionDraft(+1),
                16
            );
            nextBtn.IsEnabled = deckIdx >= 0 && deckIdx < deck.Questions.Count - 1;
            headerActions.Children.Add(nextBtn);
        }

        var cancelBtn = UiBuilder.GhostButton(
            "Cancel",
            MaterialIconKind.Close,
            studio.CancelEditingQuestionDraft
        );
        headerActions.Children.Add(cancelBtn);

        var doneBtn = UiBuilder.PrimaryButton(
            "Save",
            MaterialIconKind.Check,
            () => studio.CommitEditingQuestionDraft(0)
        );
        doneBtn.IsEnabled = !string.IsNullOrWhiteSpace(draft.Prompt);
        headerActions.Children.Add(doneBtn);

        Grid.SetColumn(headerActions, 1);
        headerGrid.Children.Add(headerActions);

        // Live Validation / Critic Banner
        Control validationBanner;
        if (draftIssues.Count == 0 && !string.IsNullOrWhiteSpace(draft.Prompt))
        {
            var okRow = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
                ColumnSpacing = 6,
            };
            okRow.Children.Add(
                UiBuilder.Icon(
                    MaterialIconKind.CheckCircleOutline,
                    14,
                    ThemeService.Brush(ThemeService.SuccessFg)
                )
            );
            var okText = new TextBlock
            {
                Text = "All quality checks passed — ready to save.",
                FontSize = 11.5,
                Foreground = ThemeService.Brush(ThemeService.SuccessFg),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(okText, 1);
            okRow.Children.Add(okText);

            var recheckBtn = UiBuilder.GhostButton(
                "Re-check",
                MaterialIconKind.Refresh,
                vm.RequestUiRefresh
            );
            Grid.SetColumn(recheckBtn, 2);
            okRow.Children.Add(recheckBtn);

            validationBanner = new Border
            {
                Background = ThemeService.Brush(ThemeService.SuccessBg),
                BorderBrush = ThemeService.Brush(ThemeService.SuccessFg),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 5),
                Margin = new Thickness(0, 0, 0, 10),
                Child = okRow,
            };
        }
        else
        {
            var warnFg = hasBlockingError
                ? ThemeService.Brush(ThemeService.PriorityP1Fg)
                : ThemeService.Brush(ThemeService.PriorityP2Fg);
            var warnBg = hasBlockingError
                ? ThemeService.Brush(ThemeService.PriorityP1Bg)
                : ThemeService.Brush(ThemeService.PriorityP2Bg);

            var issueLines = new List<string>();
            if (string.IsNullOrWhiteSpace(draft.Prompt))
                issueLines.Add("EMPTY_PROMPT: Enter a question prompt before saving.");
            issueLines.AddRange(draftIssues.Select(i => $"{i.Code}: {i.Message}"));

            var warnRow = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
                ColumnSpacing = 8,
            };
            var warnIcon = UiBuilder.Icon(MaterialIconKind.AlertOutline, 15, warnFg);
            warnIcon.VerticalAlignment = VerticalAlignment.Top;
            warnIcon.Margin = new Thickness(0, 2, 0, 0);
            warnRow.Children.Add(warnIcon);

            var warnText = new TextBlock
            {
                Text = string.Join("\n", issueLines),
                FontSize = 11.5,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(warnText, 1);
            warnRow.Children.Add(warnText);

            var recheckBtn = UiBuilder.GhostButton(
                "Re-check",
                MaterialIconKind.Refresh,
                vm.RequestUiRefresh
            );
            Grid.SetColumn(recheckBtn, 2);
            warnRow.Children.Add(recheckBtn);

            validationBanner = new Border
            {
                Background = warnBg,
                BorderBrush = warnFg,
                BorderThickness = new Thickness(2.5, 0, 0, 0),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 6),
                Margin = new Thickness(0, 0, 0, 10),
                Child = warnRow,
            };
        }

        // Scrollable Editor Body
        var bodyStack = new StackPanel { Spacing = 12, Margin = new Thickness(0, 0, 4, 4) };

        // SECTION 1: Classification (Responsive WrapPanels for Kind, Cognitive Level, Archetype, TopicTag)
        var classStack = new StackPanel { Spacing = 8 };
        classStack.Children.Add(EditorFieldLabel("Question Type"));
        var kindWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var k in Enum.GetValues<QuizQuestionKind>())
        {
            var capturedK = k;
            var kBtn = UiBuilder.GhostButton(
                k.ToString(),
                draft.Kind == k ? MaterialIconKind.Check : null,
                () =>
                {
                    draft.Kind = capturedK;
                    if (capturedK == QuizQuestionKind.TrueFalse)
                    {
                        draft.Options = ["True", "False"];
                        if (
                            !string.Equals(
                                draft.CorrectAnswer,
                                "False",
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                            draft.CorrectAnswer = "True";
                    }
                    else if (
                        capturedK is QuizQuestionKind.MultipleChoice or QuizQuestionKind.Situational
                        && draft.Options.Count < 2
                    )
                    {
                        draft.Options = ["Option 1", "Option 2", "Option 3", "Option 4"];
                        draft.CorrectAnswer = "Option 1";
                    }
                    else if (capturedK == QuizQuestionKind.Ordering && draft.Options.Count < 3)
                    {
                        draft.Options = ["Step 1", "Step 2", "Step 3"];
                        draft.CorrectAnswer = "Step 1 -> Step 2 -> Step 3";
                    }
                    vm.RequestUiRefresh();
                },
                isActive: draft.Kind == k
            );
            kBtn.Margin = new Thickness(0, 0, 5, 4);
            kindWrap.Children.Add(kBtn);
        }
        classStack.Children.Add(kindWrap);

        classStack.Children.Add(EditorFieldLabel("Cognitive Level"));
        var cogWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var cl in Enum.GetValues<CognitiveLevel>())
        {
            var capturedCl = cl;
            var clBtn = UiBuilder.GhostButton(
                cl.ToString(),
                draft.CognitiveLevel == capturedCl ? MaterialIconKind.Check : null,
                () =>
                {
                    draft.CognitiveLevel = capturedCl;
                    vm.RequestUiRefresh();
                },
                isActive: draft.CognitiveLevel == capturedCl
            );
            clBtn.Margin = new Thickness(0, 0, 5, 4);
            cogWrap.Children.Add(clBtn);
        }
        classStack.Children.Add(cogWrap);

        classStack.Children.Add(EditorFieldLabel("Question Archetype & Topic Tag"));
        var archWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        var noneArchBtn = UiBuilder.GhostButton(
            "None",
            string.IsNullOrWhiteSpace(draft.Archetype) ? MaterialIconKind.Check : null,
            () =>
            {
                draft.Archetype = null;
                vm.RequestUiRefresh();
            },
            isActive: string.IsNullOrWhiteSpace(draft.Archetype)
        );
        noneArchBtn.Margin = new Thickness(0, 0, 5, 4);
        archWrap.Children.Add(noneArchBtn);

        foreach (var arch in Enum.GetValues<QuestionArchetype>())
        {
            var capturedArch = arch;
            var archKey = arch.ToString();
            bool isArchActive = string.Equals(
                draft.Archetype,
                archKey,
                StringComparison.OrdinalIgnoreCase
            );
            var aBtn = UiBuilder.GhostButton(
                QuizBlueprintBuilder.GetArchetypeDisplayName(arch),
                isArchActive ? MaterialIconKind.Check : null,
                () =>
                {
                    draft.Archetype = isArchActive ? null : archKey;
                    vm.RequestUiRefresh();
                },
                isActive: isArchActive
            );
            aBtn.Margin = new Thickness(0, 0, 5, 4);
            archWrap.Children.Add(aBtn);
        }
        classStack.Children.Add(archWrap);

        var tagBox = new TextBox
        {
            Text = draft.TopicTag,
            Watermark = "Topic @tag (e.g. virtual-memory, paging, sql-joins)...",
            FontSize = 12,
        };
        tagBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                draft.TopicTag = (tagBox.Text ?? string.Empty).Trim().TrimStart('@', '#');
        };
        classStack.Children.Add(tagBox);
        bodyStack.Children.Add(EditorSectionCard(classStack));

        // SECTION 2: Question Prompt
        var promptStack = new StackPanel { Spacing = 6 };
        var promptHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        promptHeader.Children.Add(EditorFieldLabel("Question Prompt"));
        if (draft.Kind == QuizQuestionKind.FillInTheBlank)
        {
            var insertBlankBtn = UiBuilder.GhostButton(
                "+ Insert _____",
                MaterialIconKind.FormatText,
                () =>
                {
                    draft.Prompt = string.IsNullOrWhiteSpace(draft.Prompt)
                        ? "_____ "
                        : draft.Prompt.TrimEnd() + " _____ ";
                    vm.RequestUiRefresh();
                }
            );
            Grid.SetColumn(insertBlankBtn, 1);
            promptHeader.Children.Add(insertBlankBtn);
        }
        promptStack.Children.Add(promptHeader);

        var promptBox = new TextBox
        {
            Text = draft.Prompt,
            Watermark =
                draft.Kind == QuizQuestionKind.FillInTheBlank
                    ? "Enter statement containing _____ where the missing answer belongs..."
                    : "Enter clear, self-contained question stem...",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 64,
            FontSize = 13,
        };
        promptBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                draft.Prompt = promptBox.Text ?? string.Empty;
        };
        promptStack.Children.Add(promptBox);
        bodyStack.Children.Add(EditorSectionCard(promptStack));

        // SECTION 3: Error-Proof Answer & Options Editor
        var answerStack = new StackPanel { Spacing = 8 };
        if (draft.Kind == QuizQuestionKind.TrueFalse)
        {
            answerStack.Children.Add(EditorFieldLabel("Correct Answer"));
            var tfRow = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var tfVal in new[] { "True", "False" })
            {
                bool isSel = string.Equals(
                    draft.CorrectAnswer.Trim(),
                    tfVal,
                    StringComparison.OrdinalIgnoreCase
                );
                var tfBtn = UiBuilder.GhostButton(
                    tfVal,
                    isSel ? MaterialIconKind.CheckCircle : MaterialIconKind.CircleOutline,
                    () =>
                    {
                        draft.CorrectAnswer = tfVal;
                        vm.RequestUiRefresh();
                    },
                    isActive: isSel
                );
                tfBtn.Margin = new Thickness(0, 0, 8, 2);
                tfRow.Children.Add(tfBtn);
            }
            answerStack.Children.Add(tfRow);
        }
        else if (
            draft.Kind
            is QuizQuestionKind.MultipleChoice
                or QuizQuestionKind.Situational
                or QuizQuestionKind.Ordering
        )
        {
            bool isOrdering = draft.Kind == QuizQuestionKind.Ordering;
            var optsHeader = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                ColumnSpacing = 8,
            };
            optsHeader.Children.Add(
                EditorFieldLabel(
                    isOrdering
                        ? "Steps in CORRECT Order (the Runner shuffles them automatically)"
                        : "Options (select the radio circle for the correct answer · char count shown to prevent length giveaways)"
                )
            );
            var addOptBtn = UiBuilder.GhostButton(
                isOrdering ? "+ Add Step" : "+ Add Option",
                MaterialIconKind.Plus,
                () =>
                {
                    draft.Options.Add(
                        isOrdering
                            ? $"Step {draft.Options.Count + 1}"
                            : $"Option {draft.Options.Count + 1}"
                    );
                    if (isOrdering && draft.Options.Count >= 2)
                        draft.CorrectAnswer = string.Join(" -> ", draft.Options);
                    vm.RequestUiRefresh();
                }
            );
            Grid.SetColumn(addOptBtn, 1);
            optsHeader.Children.Add(addOptBtn);
            answerStack.Children.Add(optsHeader);

            var optsListStack = new StackPanel { Spacing = 6 };
            for (int oi = 0; oi < draft.Options.Count; oi++)
            {
                int capturedIdx = oi;
                var currentVal = draft.Options[oi];
                bool isCorrectChoice =
                    !isOrdering
                    && string.Equals(
                        currentVal.Trim(),
                        draft.CorrectAnswer.Trim(),
                        StringComparison.OrdinalIgnoreCase
                    );

                var optRow = new Grid
                {
                    ColumnDefinitions = isOrdering
                        ? ColumnDefinitions.Parse("Auto,*,Auto,Auto,Auto")
                        : ColumnDefinitions.Parse("Auto,Auto,*,Auto,Auto"),
                    ColumnSpacing = 6,
                };

                if (isOrdering)
                {
                    var numPill = UiBuilder.PillBadge(
                        $"{oi + 1}",
                        ThemeService.Brush(ThemeService.AccentPrimary),
                        ThemeService.Brush(ThemeService.AccentSoftBg)
                    );
                    numPill.VerticalAlignment = VerticalAlignment.Center;
                    optRow.Children.Add(numPill);
                }
                else
                {
                    var markBtn = UiBuilder.IconButton(
                        isCorrectChoice
                            ? MaterialIconKind.RadioboxMarked
                            : MaterialIconKind.RadioboxBlank,
                        () =>
                        {
                            draft.CorrectAnswer = draft.Options[capturedIdx].Trim();
                            vm.RequestUiRefresh();
                        },
                        18,
                        isCorrectChoice
                            ? ThemeService.Brush(ThemeService.SuccessFg)
                            : ThemeService.Brush(ThemeService.TextMuted)
                    );
                    markBtn.VerticalAlignment = VerticalAlignment.Center;
                    optRow.Children.Add(markBtn);

                    var letterPill = UiBuilder.Pill(
                        ((char)('A' + oi)).ToString(),
                        isCorrectChoice
                            ? ThemeService.Brush(ThemeService.SuccessFg)
                            : ThemeService.Brush(ThemeService.TextSecondary),
                        isCorrectChoice
                            ? ThemeService.Brush(ThemeService.SuccessBg)
                            : ThemeService.Brush(ThemeService.BgElevated),
                        fontSize: 10.5
                    );
                    letterPill.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(letterPill, 1);
                    optRow.Children.Add(letterPill);
                }

                var optItemBox = new TextBox
                {
                    Text = currentVal,
                    Watermark = isOrdering ? $"Step {oi + 1}..." : $"Option {(char)('A' + oi)}...",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                };
                optItemBox.PropertyChanged += (_, e) =>
                {
                    if (e.Property == TextBox.TextProperty && capturedIdx < draft.Options.Count)
                    {
                        var wasCorrect =
                            !isOrdering
                            && string.Equals(
                                draft.Options[capturedIdx].Trim(),
                                draft.CorrectAnswer.Trim(),
                                StringComparison.OrdinalIgnoreCase
                            );
                        var nextText = optItemBox.Text ?? string.Empty;
                        draft.Options[capturedIdx] = nextText;
                        if (wasCorrect)
                            draft.CorrectAnswer = nextText.Trim();
                        else if (isOrdering && draft.Options.Count >= 2)
                            draft.CorrectAnswer = string.Join(" -> ", draft.Options);
                    }
                };
                Grid.SetColumn(optItemBox, isOrdering ? 1 : 2);
                optRow.Children.Add(optItemBox);

                if (isOrdering)
                {
                    var upBtn = UiBuilder.IconButton(
                        MaterialIconKind.ArrowUp,
                        () =>
                        {
                            if (capturedIdx <= 0)
                                return;
                            (draft.Options[capturedIdx - 1], draft.Options[capturedIdx]) = (
                                draft.Options[capturedIdx],
                                draft.Options[capturedIdx - 1]
                            );
                            draft.CorrectAnswer = string.Join(" -> ", draft.Options);
                            vm.RequestUiRefresh();
                        },
                        14
                    );
                    Grid.SetColumn(upBtn, 2);
                    optRow.Children.Add(upBtn);

                    var downBtn = UiBuilder.IconButton(
                        MaterialIconKind.ArrowDown,
                        () =>
                        {
                            if (capturedIdx >= draft.Options.Count - 1)
                                return;
                            (draft.Options[capturedIdx + 1], draft.Options[capturedIdx]) = (
                                draft.Options[capturedIdx],
                                draft.Options[capturedIdx + 1]
                            );
                            draft.CorrectAnswer = string.Join(" -> ", draft.Options);
                            vm.RequestUiRefresh();
                        },
                        14
                    );
                    Grid.SetColumn(downBtn, 3);
                    optRow.Children.Add(downBtn);
                }
                else
                {
                    var lenLabel = new TextBlock
                    {
                        Text = $"{currentVal.Trim().Length}c",
                        FontSize = 10.5,
                        Foreground = ThemeService.Brush(ThemeService.TextMuted),
                        VerticalAlignment = VerticalAlignment.Center,
                        MinWidth = 28,
                        TextAlignment = TextAlignment.Right,
                    };
                    Grid.SetColumn(lenLabel, 3);
                    optRow.Children.Add(lenLabel);
                }

                bool canDelete = draft.Options.Count > (isOrdering ? 3 : 2);
                var delOptBtn = UiBuilder.IconButton(
                    MaterialIconKind.Close,
                    () =>
                    {
                        if (!canDelete || capturedIdx >= draft.Options.Count)
                            return;
                        bool deletedCorrect =
                            !isOrdering
                            && string.Equals(
                                draft.Options[capturedIdx].Trim(),
                                draft.CorrectAnswer.Trim(),
                                StringComparison.OrdinalIgnoreCase
                            );
                        draft.Options.RemoveAt(capturedIdx);
                        if (isOrdering && draft.Options.Count >= 2)
                            draft.CorrectAnswer = string.Join(" -> ", draft.Options);
                        else if (deletedCorrect && draft.Options.Count > 0)
                            draft.CorrectAnswer = draft.Options[0].Trim();
                        vm.RequestUiRefresh();
                    },
                    14,
                    canDelete
                        ? ThemeService.Brush(ThemeService.PriorityP1Fg)
                        : ThemeService.Brush(ThemeService.TextMuted)
                );
                delOptBtn.IsEnabled = canDelete;
                Grid.SetColumn(delOptBtn, 4);
                optRow.Children.Add(delOptBtn);

                optsListStack.Children.Add(optRow);
            }
            answerStack.Children.Add(optsListStack);
        }
        else
        {
            answerStack.Children.Add(EditorFieldLabel("Correct Answer"));
            var correctBox = new TextBox
            {
                Text = draft.CorrectAnswer,
                Watermark = "Exact canonical answer...",
                FontSize = 12.5,
            };
            correctBox.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                    draft.CorrectAnswer = (correctBox.Text ?? string.Empty).Trim();
            };
            answerStack.Children.Add(correctBox);

            var aliasListPanel = new StackPanel { Spacing = 6 };
            var workingAliases = new List<string>(draft.AcceptedAliases);

            void SyncAliasesToDraft()
            {
                draft.AcceptedAliases =
                [
                    .. workingAliases
                        .Select(a => a.Trim())
                        .Where(a => !string.IsNullOrWhiteSpace(a))
                        .Distinct(StringComparer.OrdinalIgnoreCase),
                ];
            }

            void RebuildAliasRows(bool focusLast = false)
            {
                aliasListPanel.Children.Clear();
                if (workingAliases.Count == 0)
                {
                    aliasListPanel.Children.Add(
                        new TextBlock
                        {
                            Text =
                                "No alternate answers yet. Click '+ Add Alias' to accept synonyms, abbreviations, or alternate phrasings.",
                            FontSize = 11.5,
                            Foreground = ThemeService.Brush(ThemeService.TextMuted),
                            TextWrapping = TextWrapping.Wrap,
                        }
                    );
                    return;
                }

                TextBox? lastBox = null;
                for (int i = 0; i < workingAliases.Count; i++)
                {
                    int idx = i;
                    var row = new Grid
                    {
                        ColumnDefinitions = ColumnDefinitions.Parse("26,*,Auto"),
                        ColumnSpacing = 6,
                    };
                    row.Children.Add(
                        new TextBlock
                        {
                            Text = $"#{idx + 1}",
                            FontSize = 11.5,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = ThemeService.Brush(ThemeService.TextMuted),
                            VerticalAlignment = VerticalAlignment.Center,
                            HorizontalAlignment = HorizontalAlignment.Center,
                        }
                    );

                    var itemBox = new TextBox
                    {
                        Text = workingAliases[idx],
                        Watermark = "Alternate accepted answer (e.g. FIFO, First-In First-Out)...",
                        FontSize = 12,
                    };
                    itemBox.PropertyChanged += (_, e) =>
                    {
                        if (e.Property == TextBox.TextProperty && idx < workingAliases.Count)
                        {
                            workingAliases[idx] = itemBox.Text ?? string.Empty;
                            SyncAliasesToDraft();
                        }
                    };
                    Grid.SetColumn(itemBox, 1);
                    row.Children.Add(itemBox);
                    lastBox = itemBox;

                    var removeBtn = UiBuilder.IconButton(
                        MaterialIconKind.Close,
                        () =>
                        {
                            if (idx < workingAliases.Count)
                            {
                                workingAliases.RemoveAt(idx);
                                SyncAliasesToDraft();
                                RebuildAliasRows();
                            }
                        },
                        14,
                        ThemeService.Brush(ThemeService.TextSecondary)
                    );
                    ToolTip.SetTip(removeBtn, "Remove alternate answer");
                    Grid.SetColumn(removeBtn, 2);
                    row.Children.Add(removeBtn);

                    aliasListPanel.Children.Add(row);
                }

                if (focusLast && lastBox is not null)
                {
                    var targetFocus = lastBox;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => targetFocus.Focus());
                }
            }

            var aliasHeader = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                ColumnSpacing = 8,
            };
            aliasHeader.Children.Add(EditorFieldLabel("Accepted Aliases / Alternate Answers"));
            var addAliasBtn = UiBuilder.GhostButton(
                "Add Alias",
                MaterialIconKind.Plus,
                () =>
                {
                    workingAliases.Add(string.Empty);
                    RebuildAliasRows(focusLast: true);
                }
            );
            Grid.SetColumn(addAliasBtn, 1);
            aliasHeader.Children.Add(addAliasBtn);

            RebuildAliasRows();
            answerStack.Children.Add(aliasHeader);
            answerStack.Children.Add(aliasListPanel);
        }
        bodyStack.Children.Add(EditorSectionCard(answerStack));

        // SECTION 4: Progressive Hint & Structured 3-Part Contrastive Explanation
        var feedbackStack = new StackPanel { Spacing = 8 };
        feedbackStack.Children.Add(
            EditorFieldLabel(
                "Progressive Hint (Optional — 1-sentence clue without revealing the answer)"
            )
        );
        var hintBox = new TextBox
        {
            Text = draft.Hint ?? string.Empty,
            Watermark =
                "e.g. Think about what happens to page-table lookups when the TLB misses...",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        };
        hintBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                var h = (hintBox.Text ?? string.Empty).Trim();
                draft.Hint = string.IsNullOrEmpty(h) ? null : h;
            }
        };
        feedbackStack.Children.Add(hintBox);

        feedbackStack.Children.Add(EditorFieldLabel("Explanation & Contrastive Feedback"));

        var (initCorrect, initDistractors, initPrinciple) = SplitContrastiveExplanation(
            draft.Explanation
        );
        string curWhyCorrect = initCorrect;
        string curWhyDistractors = initDistractors;
        string curCorePrinciple = initPrinciple;

        void SyncExplanationDraft()
        {
            draft.Explanation = ComposeContrastiveExplanation(
                curWhyCorrect,
                curWhyDistractors,
                curCorePrinciple
            );
        }

        // 1. Why Correct
        feedbackStack.Children.Add(
            new TextBlock
            {
                Text = "WHY CORRECT",
                FontSize = 10.5,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeService.Brush(ThemeService.SuccessFg),
            }
        );
        var whyCorrectBox = new TextBox
        {
            Text = curWhyCorrect,
            Watermark = "Explain why the correct answer is right...",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 46,
            FontSize = 12,
        };
        whyCorrectBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                curWhyCorrect = whyCorrectBox.Text ?? string.Empty;
                SyncExplanationDraft();
            }
        };
        feedbackStack.Children.Add(whyCorrectBox);

        // 2. Why Distractors Fail
        feedbackStack.Children.Add(
            new TextBlock
            {
                Text = "WHY DISTRACTORS FAIL (OPTIONAL)",
                FontSize = 10.5,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeService.Brush(ThemeService.PriorityP2Fg),
            }
        );
        var whyDistBox = new TextBox
        {
            Text = curWhyDistractors,
            Watermark = "Explain why the most tempting wrong option or misconception fails...",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 42,
            FontSize = 12,
        };
        whyDistBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                curWhyDistractors = whyDistBox.Text ?? string.Empty;
                SyncExplanationDraft();
            }
        };
        feedbackStack.Children.Add(whyDistBox);

        // 3. Core Principle
        feedbackStack.Children.Add(
            new TextBlock
            {
                Text = "CORE PRINCIPLE (OPTIONAL)",
                FontSize = 10.5,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
            }
        );
        var corePrincipleBox = new TextBox
        {
            Text = curCorePrinciple,
            Watermark = "1-sentence anchor takeaway to remember...",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        };
        corePrincipleBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                curCorePrinciple = corePrincipleBox.Text ?? string.Empty;
                SyncExplanationDraft();
            }
        };
        feedbackStack.Children.Add(corePrincipleBox);

        bodyStack.Children.Add(EditorSectionCard(feedbackStack));

        // SECTION 5: Collapsible Rich Media & Evidence Grounding
        bodyStack.Children.Add(
            UiBuilder.GhostButton(
                studio.IsEditingAdvancedOpen
                    ? "▾ Hide Rich Content & Evidence Grounding"
                    : "▸ Show Rich Content & Evidence Grounding (Code · Math · Diagram · Image · PDF Quote)",
                null,
                studio.ToggleEditingAdvancedOpen,
                isActive: studio.IsEditingAdvancedOpen
            )
        );

        if (studio.IsEditingAdvancedOpen)
        {
            var advStack = new StackPanel { Spacing = 8 };

            var locGrid = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("110,*"),
                ColumnSpacing = 8,
            };
            var pageBox = new TextBox
            {
                Text = draft.SourcePage > 0 ? draft.SourcePage.ToString() : "1",
                Watermark = "PDF Page #",
                FontSize = 12,
            };
            pageBox.PropertyChanged += (_, e) =>
            {
                if (
                    e.Property == TextBox.TextProperty
                    && int.TryParse(pageBox.Text?.Trim(), out int p)
                    && p > 0
                )
                    draft.SourcePage = p;
            };
            locGrid.Children.Add(pageBox);

            var secBox = new TextBox
            {
                Text = draft.SectionTitle,
                Watermark = "Section / heading / function title...",
                FontSize = 12,
            };
            secBox.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                    draft.SectionTitle = (secBox.Text ?? string.Empty).Trim();
            };
            Grid.SetColumn(secBox, 1);
            locGrid.Children.Add(secBox);
            advStack.Children.Add(locGrid);

            var quoteBox = new TextBox
            {
                Text = draft.VerbatimQuote,
                Watermark = "Verbatim quote from PDF/source file (or visual pointer for images)...",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 46,
                FontSize = 11.5,
            };
            quoteBox.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                    draft.VerbatimQuote = quoteBox.Text ?? string.Empty;
            };
            advStack.Children.Add(quoteBox);

            var groundRow = new WrapPanel { Orientation = Orientation.Horizontal };
            var snipBtn = UiBuilder.PrimaryButton(
                $"Open PDF Snipper ({draft.EvidenceAnchors.Count})",
                MaterialIconKind.SelectionDrag,
                async () => await studio.OpenDedicatedSnipperAsync()
            );
            snipBtn.Margin = new Thickness(0, 0, 6, 4);
            groundRow.Children.Add(snipBtn);

            var groundBtn = UiBuilder.GhostButton(
                "Ground Quote",
                MaterialIconKind.Marker,
                studio.GroundEditingDraftToPdf,
                isActive: draft.HighlightRects.Count > 0
            );
            groundBtn.Margin = new Thickness(0, 0, 6, 4);
            groundRow.Children.Add(groundBtn);

            var loadPageBtn = UiBuilder.GhostButton(
                "Load Page Text",
                MaterialIconKind.FileDocumentOutline,
                async () => await studio.LoadEditingPageTextAsync()
            );
            loadPageBtn.Margin = new Thickness(0, 0, 8, 4);
            groundRow.Children.Add(loadPageBtn);

            if (!string.IsNullOrWhiteSpace(studio.EditingGroundStatus))
            {
                groundRow.Children.Add(
                    new TextBlock
                    {
                        Text = studio.EditingGroundStatus,
                        FontSize = 11.5,
                        Foreground = studio.EditingGroundStatus.StartsWith('✓')
                            ? ThemeService.Brush(ThemeService.SuccessFg)
                            : ThemeService.Brush(ThemeService.PriorityP2Fg),
                        VerticalAlignment = VerticalAlignment.Center,
                    }
                );
            }
            advStack.Children.Add(groundRow);

            if (draft.EvidenceAnchors.Count > 0)
            {
                var anchorsStack = new StackPanel { Spacing = 4 };
                for (int ai = 0; ai < draft.EvidenceAnchors.Count; ai++)
                {
                    int capturedAi = ai;
                    var anchor = draft.EvidenceAnchors[ai];
                    var anchorRow = new Grid
                    {
                        ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
                        ColumnSpacing = 6,
                    };
                    anchorRow.Children.Add(
                        UiBuilder.Pill(
                            $"p.{anchor.PageNumber} ({anchor.HighlightRects.Count})",
                            ThemeService.Brush(ThemeService.AccentPrimary),
                            ThemeService.Brush(ThemeService.AccentSoftBg),
                            fontSize: 10.5
                        )
                    );
                    var anchorDesc = new TextBlock
                    {
                        Text = !string.IsNullOrWhiteSpace(anchor.VerbatimQuote)
                            ? anchor.VerbatimQuote
                            : (
                                !string.IsNullOrWhiteSpace(anchor.SectionTitle)
                                    ? anchor.SectionTitle
                                    : "Visual region"
                            ),
                        FontSize = 11.5,
                        Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                        [Grid.ColumnProperty] = 1,
                    };
                    anchorRow.Children.Add(anchorDesc);
                    var rmBtn = UiBuilder.IconButton(
                        MaterialIconKind.Close,
                        () => studio.RemoveEvidenceAnchor(capturedAi),
                        13,
                        ThemeService.Brush(ThemeService.PriorityP1Fg)
                    );
                    Grid.SetColumn(rmBtn, 2);
                    anchorRow.Children.Add(rmBtn);
                    anchorsStack.Children.Add(anchorRow);
                }
                advStack.Children.Add(
                    new Border
                    {
                        Background = ThemeService.Brush(ThemeService.BgSurface),
                        BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(8, 6),
                        Child = anchorsStack,
                    }
                );
            }

            if (!string.IsNullOrWhiteSpace(studio.EditingPageTextPreview))
            {
                advStack.Children.Add(
                    new TextBox
                    {
                        Text = studio.EditingPageTextPreview,
                        IsReadOnly = true,
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap,
                        MaxHeight = 110,
                        FontSize = 11,
                    }
                );
            }

            var mathBox = new TextBox
            {
                Text = draft.MathLatex ?? string.Empty,
                Watermark = "Optional Math LaTeX (e.g. O(n \\log n) or \\frac{a+b}{2})...",
                FontSize = 12,
            };
            var mathPreviewHost = new Border
            {
                Child = string.IsNullOrWhiteSpace(draft.MathLatex)
                    ? null
                    : QuizRichContentRenderer.BuildMathLatexCard(draft.MathLatex),
            };
            mathBox.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                {
                    draft.MathLatex = string.IsNullOrWhiteSpace(mathBox.Text)
                        ? null
                        : mathBox.Text.Trim();
                    mathPreviewHost.Child = string.IsNullOrWhiteSpace(draft.MathLatex)
                        ? null
                        : QuizRichContentRenderer.BuildMathLatexCard(draft.MathLatex);
                }
            };
            advStack.Children.Add(mathBox);
            advStack.Children.Add(mathPreviewHost);

            var diagBox = new TextBox
            {
                Text = draft.Diagram ?? string.Empty,
                Watermark =
                    "Optional Diagram (A -> B -> C, flowchart LR/TD, or sequenceDiagram)...",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                MinHeight = 52,
                FontSize = 11.5,
                FontFamily = new FontFamily("Consolas, JetBrains Mono, monospace"),
            };
            var diagPreviewHost = new Border
            {
                Child = string.IsNullOrWhiteSpace(draft.Diagram)
                    ? null
                    : QuizRichContentRenderer.BuildDiagramCard(draft.Diagram),
            };
            diagBox.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                {
                    draft.Diagram = string.IsNullOrWhiteSpace(diagBox.Text)
                        ? null
                        : diagBox.Text.Trim();
                    diagPreviewHost.Child = string.IsNullOrWhiteSpace(draft.Diagram)
                        ? null
                        : QuizRichContentRenderer.BuildDiagramCard(draft.Diagram);
                }
            };
            advStack.Children.Add(diagBox);
            advStack.Children.Add(diagPreviewHost);

            var codeLangRow = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,140"),
                ColumnSpacing = 8,
            };
            codeLangRow.Children.Add(EditorFieldLabel("Optional Code Snippet"));
            var langBox = new TextBox
            {
                Text = draft.CodeLanguage ?? string.Empty,
                Watermark = "Lang (cs, py, c...)",
                FontSize = 11.5,
            };
            langBox.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                    draft.CodeLanguage = string.IsNullOrWhiteSpace(langBox.Text)
                        ? null
                        : langBox.Text.Trim();
            };
            Grid.SetColumn(langBox, 1);
            codeLangRow.Children.Add(langBox);
            advStack.Children.Add(codeLangRow);

            var codeEditBox = new TextBox
            {
                Text = draft.CodeSnippet ?? string.Empty,
                Watermark = "Optional Code Snippet block...",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                MinHeight = 56,
                FontSize = 12,
                FontFamily = new FontFamily("Consolas, JetBrains Mono, monospace"),
            };
            codeEditBox.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                    draft.CodeSnippet = string.IsNullOrWhiteSpace(codeEditBox.Text)
                        ? null
                        : codeEditBox.Text;
            };
            advStack.Children.Add(codeEditBox);

            var imgActionRow = new WrapPanel { Orientation = Orientation.Horizontal };
            var browseImgBtn = UiBuilder.GhostButton(
                "Browse Question Image...",
                MaterialIconKind.ImagePlusOutline,
                async () =>
                {
                    var top = TopLevel.GetTopLevel(bodyStack);
                    if (top?.StorageProvider is null)
                        return;
                    var files = await top.StorageProvider.OpenFilePickerAsync(
                        new Avalonia.Platform.Storage.FilePickerOpenOptions
                        {
                            Title = "Select Diagram or Figure Image",
                            AllowMultiple = false,
                            FileTypeFilter =
                            [
                                new Avalonia.Platform.Storage.FilePickerFileType("Images")
                                {
                                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"],
                                },
                            ],
                        }
                    );
                    var path =
                        files.Count > 0
                            ? Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(
                                files[0]
                            )
                            : null;
                    if (!string.IsNullOrWhiteSpace(path))
                        studio.SetEditingQuestionImage(path);
                }
            );
            browseImgBtn.Margin = new Thickness(0, 0, 6, 4);
            imgActionRow.Children.Add(browseImgBtn);

            var resolvedDraftImg = studio.ResolveQuestionImageFullPath(draft.ImagePath);
            if (!string.IsNullOrWhiteSpace(resolvedDraftImg))
            {
                var removeImgBtn = UiBuilder.GhostButton(
                    "Remove Image",
                    MaterialIconKind.ImageRemoveOutline,
                    () => studio.SetEditingQuestionImage(null)
                );
                removeImgBtn.Margin = new Thickness(0, 0, 6, 4);
                imgActionRow.Children.Add(removeImgBtn);
            }
            advStack.Children.Add(imgActionRow);

            if (!string.IsNullOrWhiteSpace(resolvedDraftImg))
            {
                advStack.Children.Add(
                    QuizRichContentRenderer.BuildQuestionImageCard(
                        resolvedDraftImg,
                        () => studio.OpenImageZoom(draft.ImagePath)
                    )
                );
            }

            bodyStack.Children.Add(EditorSectionCard(advStack));
        }

        // Compose Sticky Header + Validation Banner + Scrollable Body inside Responsive Modal Shell
        var shellGrid = new Grid { RowDefinitions = RowDefinitions.Parse("Auto,Auto,*") };
        shellGrid.Children.Add(headerGrid);
        Grid.SetRow(validationBanner, 1);
        shellGrid.Children.Add(validationBanner);

        var scroller = UiBuilder.PersistentScrollViewer($"quiz-q-editor:{draft.Id}", bodyStack);
        Grid.SetRow(scroller, 2);
        shellGrid.Children.Add(scroller);

        var modalCard = new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.AccentPrimary),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 14),
            MaxWidth = 860,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = shellGrid,
        };

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgBackdrop),
            Padding = new Thickness(20),
            Child = modalCard,
        };
    }

    private static TextBlock EditorFieldLabel(string text) =>
        new()
        {
            Text = text,
            FontSize = 11.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeService.Brush(ThemeService.TextSecondary),
            VerticalAlignment = VerticalAlignment.Center,
        };

    private static Border EditorSectionCard(Control child) =>
        new()
        {
            Background = ThemeService.Brush(ThemeService.BgBase),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10),
            Child = child,
        };

    private static (
        string WhyCorrect,
        string WhyDistractors,
        string CorePrinciple
    ) SplitContrastiveExplanation(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (string.Empty, string.Empty, string.Empty);

        var matches = ContrastiveSplitRegex.Matches(raw);
        if (matches.Count == 0)
            return (raw.Trim(), string.Empty, string.Empty);

        string whyCorrect = string.Empty;
        string whyDistractors = string.Empty;
        string corePrinciple = string.Empty;

        var prefix = raw[..matches[0].Index].Trim();
        if (!string.IsNullOrEmpty(prefix))
            whyCorrect = prefix;

        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            int start = m.Index + m.Length;
            int end = i + 1 < matches.Count ? matches[i + 1].Index : raw.Length;
            var heading = m.Value;
            var content = raw[start..end].Trim();

            if (heading.Contains("correct", StringComparison.OrdinalIgnoreCase))
                whyCorrect = string.IsNullOrEmpty(whyCorrect) ? content : $"{whyCorrect} {content}";
            else if (heading.Contains("distractor", StringComparison.OrdinalIgnoreCase))
                whyDistractors = content;
            else
                corePrinciple = content;
        }

        return (whyCorrect, whyDistractors, corePrinciple);
    }

    private static string ComposeContrastiveExplanation(
        string whyCorrect,
        string whyDistractors,
        string corePrinciple
    )
    {
        var c = whyCorrect.Trim();
        var d = whyDistractors.Trim();
        var p = corePrinciple.Trim();

        if (string.IsNullOrEmpty(d) && string.IsNullOrEmpty(p))
            return c;

        var parts = new List<string>(3);
        if (!string.IsNullOrEmpty(c))
            parts.Add($"Why correct: {c}");
        if (!string.IsNullOrEmpty(d))
            parts.Add($"Why distractors fail: {d}");
        if (!string.IsNullOrEmpty(p))
            parts.Add($"Core principle: {p}");
        return string.Join(" ", parts);
    }
}
