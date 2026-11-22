using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using ScholarFlow.Core.Quiz;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views;

public static partial class QuizStudioViewBuilder
{
    private static Control BuildActiveQuestionRunnerView(
        MainWindowViewModel vm,
        QuizStudioViewModel studio
    )
    {
        var session = studio.ActiveSession!;
        var q = studio.CurrentQuestion;
        if (q is null)
            return BuildStudioWorkspaceView(vm, studio);

        var stack = new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(12, 8, 12, 28),
        };

        var topBar = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
            ColumnSpacing = 12,
        };
        var topLeftActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        topLeftActions.Children.Add(
            UiBuilder.GhostButton(
                "Exit to Studio",
                MaterialIconKind.ArrowLeft,
                studio.ExitActiveSession
            )
        );
        topLeftActions.Children.Add(
            UiBuilder.GhostButton(
                "Restart",
                MaterialIconKind.Restart,
                () => studio.StartQuizSession(session, resumeSavedProgress: false)
            )
        );
        topBar.Children.Add(topLeftActions);

        var centerTitle = new TextBlock
        {
            Text =
                $"{session.Title}  ·  Question {studio.CurrentQuestionIndex + 1} of {session.Questions.Count}",
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(centerTitle, 1);
        topBar.Children.Add(centerTitle);

        var metaBadges = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        metaBadges.Children.Add(
            UiBuilder.PillBadge(
                q.Kind.ToString(),
                ThemeService.Brush(ThemeService.AccentPrimary),
                ThemeService.Brush(ThemeService.AccentSoftBg)
            )
        );
        metaBadges.Children.Add(
            UiBuilder.PillBadge(
                $"Cognitive: {q.CognitiveLevel}",
                ThemeService.Brush(ThemeService.PriorityP3Fg),
                ThemeService.Brush(ThemeService.PriorityP3Bg)
            )
        );
        if (!string.IsNullOrWhiteSpace(q.Archetype))
        {
            metaBadges.Children.Add(
                UiBuilder.PillBadge(
                    QuizBlueprintBuilder.FormatArchetypeBadge(q.Archetype),
                    ThemeService.Brush(ThemeService.PriorityP2Fg),
                    ThemeService.Brush(ThemeService.PriorityP2Bg)
                )
            );
        }
        metaBadges.Children.Add(
            UiBuilder.PillBadge(
                $"@{q.TopicTag}",
                ThemeService.Brush(ThemeService.TextSecondary),
                ThemeService.Brush(ThemeService.BgElevated)
            )
        );

        Grid.SetColumn(metaBadges, 2);
        topBar.Children.Add(metaBadges);
        stack.Children.Add(topBar);

        // Progress Bar + Interactive Question Navigator Strip (1..N)
        var progressCardStack = new StackPanel { Spacing = 8 };
        int totalQ = Math.Max(1, session.Questions.Count);
        int answeredQ = session.Questions.Count(qq => studio.SessionResults.ContainsKey(qq.Id));

        progressCardStack.Children.Add(
            new ProgressBar
            {
                Minimum = 0,
                Maximum = totalQ,
                Value = answeredQ,
                Height = 4,
                Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
                Background = ThemeService.Brush(ThemeService.BgElevated),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            }
        );

        var qNavWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        for (int qi = 0; qi < session.Questions.Count; qi++)
        {
            int capturedIndex = qi;
            var itemQ = session.Questions[qi];
            bool isCurrent = qi == studio.CurrentQuestionIndex;
            bool hasRes = studio.SessionResults.TryGetValue(itemQ.Id, out var qRes);

            var navBtn = UiBuilder.GhostButton(
                $"{qi + 1}",
                hasRes ? (qRes.IsCorrect ? MaterialIconKind.Check : MaterialIconKind.Close) : null,
                () => studio.JumpToQuestion(capturedIndex),
                isActive: isCurrent || hasRes
            );
            navBtn.Margin = new Thickness(0, 0, 4, 4);
            qNavWrap.Children.Add(navBtn);
        }
        progressCardStack.Children.Add(qNavWrap);
        stack.Children.Add(UiBuilder.Card(progressCardStack, padding: new Thickness(12, 8)));

        var qCardStack = new StackPanel { Spacing = 14 };

        if (!string.IsNullOrWhiteSpace(q.SectionTitle))
        {
            qCardStack.Children.Add(
                new TextBlock
                {
                    Text =
                        $"Source Section: {q.SectionTitle} ({(q.IsPdfSource ? $"Page {q.SourcePage}" : q.EvidenceChipLabel)})",
                    FontSize = 11.5,
                    FontWeight = FontWeight.Medium,
                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                }
            );
        }

        qCardStack.Children.Add(
            new TextBlock
            {
                Text = QuizRichContentRenderer.FormatInlineMath(q.Prompt),
                FontSize = 15.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 23,
            }
        );

        if (!string.IsNullOrWhiteSpace(q.CodeSnippet))
            qCardStack.Children.Add(
                QuizRichContentRenderer.BuildCodeSnippetCard(q.CodeSnippet, q.CodeLanguage)
            );

        if (!string.IsNullOrWhiteSpace(q.MathLatex))
            qCardStack.Children.Add(QuizRichContentRenderer.BuildMathLatexCard(q.MathLatex));

        if (!string.IsNullOrWhiteSpace(q.Diagram))
            qCardStack.Children.Add(QuizRichContentRenderer.BuildDiagramCard(q.Diagram));

        var resolvedImg = studio.ResolveQuestionImageFullPath(q.ImagePath);
        if (!string.IsNullOrWhiteSpace(resolvedImg))
            qCardStack.Children.Add(
                QuizRichContentRenderer.BuildQuestionImageCard(
                    resolvedImg,
                    () => studio.OpenImageZoom(q.ImagePath)
                )
            );

        var hintAndConfidenceBar = BuildRunnerHintAndConfidenceBar(studio, q);
        if (hintAndConfidenceBar is not null)
            qCardStack.Children.Add(hintAndConfidenceBar);

        if (
            q.Kind
                is QuizQuestionKind.MultipleChoice
                    or QuizQuestionKind.Situational
                    or QuizQuestionKind.TrueFalse
            && q.Options.Count > 0
        )
        {
            var optsStack = new StackPanel { Spacing = 8 };
            for (int i = 0; i < q.Options.Count; i++)
            {
                var optText = q.Options[i];
                var letter = (char)('A' + i);
                bool isChosen = string.Equals(
                    studio.CurrentAnswerDraft,
                    optText,
                    StringComparison.OrdinalIgnoreCase
                );
                bool isThisCorrect = q.EvaluateAnswer(optText);

                IBrush borderBrush = ThemeService.Brush(ThemeService.BorderSubtle);
                IBrush bgBrush = ThemeService.Brush(ThemeService.BgElevated);
                IBrush fgBrush = ThemeService.Brush(ThemeService.TextPrimary);

                if (studio.IsAnswerRevealed)
                {
                    if (isThisCorrect)
                    {
                        borderBrush = ThemeService.Brush(ThemeService.SuccessFg);
                        bgBrush = ThemeService.Brush(ThemeService.SuccessBg);
                        fgBrush = ThemeService.Brush(ThemeService.SuccessFg);
                    }
                    else if (isChosen)
                    {
                        borderBrush = ThemeService.Brush(ThemeService.PriorityP1Fg);
                        bgBrush = ThemeService.Brush(ThemeService.PriorityP1Bg);
                        fgBrush = ThemeService.Brush(ThemeService.PriorityP1Fg);
                    }
                }
                else if (isChosen)
                {
                    borderBrush = ThemeService.Brush(ThemeService.AccentPrimary);
                    bgBrush = ThemeService.Brush(ThemeService.AccentSoftBg);
                }

                var optContentGrid = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
                    ColumnSpacing = 12,
                };
                optContentGrid.Children.Add(
                    new Border
                    {
                        Width = 24,
                        Height = 24,
                        CornerRadius = new CornerRadius(12),
                        Background = ThemeService.Brush(ThemeService.BgSurface),
                        BorderBrush = borderBrush,
                        BorderThickness = new Thickness(1),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock
                        {
                            Text = letter.ToString(),
                            FontSize = 11.5,
                            FontWeight = FontWeight.Bold,
                            Foreground = fgBrush,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    }
                );
                optContentGrid.Children.Add(
                    new TextBlock
                    {
                        Text = QuizRichContentRenderer.FormatInlineMath(optText),
                        FontSize = 13.5,
                        Foreground = fgBrush,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center,
                        [Grid.ColumnProperty] = 1,
                    }
                );

                var optBtn = new Button
                {
                    Content = optContentGrid,
                    Background = bgBrush,
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(14, 11),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                };

                var capturedOpt = optText;
                optBtn.Click += async (_, _) =>
                {
                    if (!studio.IsAnswerRevealed)
                        await studio.SubmitAnswerAsync(capturedOpt);
                };
                optsStack.Children.Add(optBtn);
            }
            qCardStack.Children.Add(optsStack);
        }
        else if (q.Kind == QuizQuestionKind.Ordering)
        {
            var steps = studio.GetOrderingItemsForQuestion(q);
            var orderStack = new StackPanel { Spacing = 8 };
            orderStack.Children.Add(
                new TextBlock
                {
                    Text =
                        "Use the Up / Down arrows to arrange these steps into the correct sequence, then click Check Sequence:",
                    FontSize = 12,
                    Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                }
            );

            for (int i = 0; i < steps.Count; i++)
            {
                int capturedIdx = i;
                var stepText = steps[i];
                bool isStepInRightSpot =
                    studio.IsAnswerRevealed
                    && i < q.Options.Count
                    && string.Equals(
                        stepText.Trim(),
                        q.Options[i].Trim(),
                        StringComparison.OrdinalIgnoreCase
                    );

                IBrush borderBrush = studio.IsAnswerRevealed
                    ? (
                        isStepInRightSpot
                            ? ThemeService.Brush(ThemeService.SuccessFg)
                            : ThemeService.Brush(ThemeService.PriorityP1Fg)
                    )
                    : ThemeService.Brush(ThemeService.BorderSubtle);
                IBrush bgBrush = studio.IsAnswerRevealed
                    ? (
                        isStepInRightSpot
                            ? ThemeService.Brush(ThemeService.SuccessBg)
                            : ThemeService.Brush(ThemeService.PriorityP1Bg)
                    )
                    : ThemeService.Brush(ThemeService.BgElevated);

                var stepGrid = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
                    ColumnSpacing = 10,
                };
                stepGrid.Children.Add(
                    UiBuilder.PillBadge(
                        $"Step {i + 1}",
                        ThemeService.Brush(ThemeService.AccentPrimary),
                        ThemeService.Brush(ThemeService.AccentSoftBg)
                    )
                );

                stepGrid.Children.Add(
                    new TextBlock
                    {
                        Text = stepText,
                        FontSize = 13.5,
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center,
                        [Grid.ColumnProperty] = 1,
                    }
                );

                var moveBtns = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                moveBtns.Children.Add(
                    UiBuilder.IconButton(
                        MaterialIconKind.ArrowUp,
                        () => studio.MoveOrderingStep(q, capturedIdx, -1),
                        14.5,
                        ThemeService.Brush(ThemeService.TextSecondary)
                    )
                );
                moveBtns.Children.Add(
                    UiBuilder.IconButton(
                        MaterialIconKind.ArrowDown,
                        () => studio.MoveOrderingStep(q, capturedIdx, +1),
                        14.5,
                        ThemeService.Brush(ThemeService.TextSecondary)
                    )
                );
                Grid.SetColumn(moveBtns, 2);
                stepGrid.Children.Add(moveBtns);

                orderStack.Children.Add(
                    new Border
                    {
                        Background = bgBrush,
                        BorderBrush = borderBrush,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(12, 9),
                        Child = stepGrid,
                    }
                );
            }

            if (!studio.IsAnswerRevealed)
            {
                orderStack.Children.Add(
                    UiBuilder.PrimaryButton(
                        "Check Sequence",
                        MaterialIconKind.Check,
                        async () =>
                        {
                            var currSeq = string.Join(
                                " -> ",
                                studio.GetOrderingItemsForQuestion(q)
                            );
                            await studio.SubmitAnswerAsync(currSeq);
                        }
                    )
                );
            }

            qCardStack.Children.Add(orderStack);
        }
        else
        {
            if (q.Kind == QuizQuestionKind.FillInTheBlank)
            {
                qCardStack.Children.Add(
                    new Border
                    {
                        Background = ThemeService.Brush(ThemeService.AccentSoftBg),
                        BorderBrush = ThemeService.Brush(ThemeService.AccentPrimary),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(10, 6),
                        Child = new TextBlock
                        {
                            Text =
                                "Fill in the missing term or phrase for the blank ( _____ ) above:",
                            FontSize = 12,
                            FontWeight = FontWeight.Medium,
                            Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
                        },
                    }
                );
            }

            var idRow = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                ColumnSpacing = 8,
            };
            var ansBox = new TextBox
            {
                Text = studio.CurrentAnswerDraft,
                Watermark =
                    q.Kind == QuizQuestionKind.FillInTheBlank
                        ? "Type the missing word/phrase for _____ and press Enter..."
                        : "Type your answer and press Enter...",
                FontSize = 14,
                IsEnabled = !studio.IsAnswerRevealed,
            };
            ansBox.KeyDown += async (_, e) =>
            {
                if (
                    e.Key == Key.Enter
                    && !studio.IsAnswerRevealed
                    && !string.IsNullOrWhiteSpace(ansBox.Text)
                )
                {
                    e.Handled = true;
                    await studio.SubmitAnswerAsync(ansBox.Text);
                }
            };
            idRow.Children.Add(ansBox);

            var submitBtn = UiBuilder.PrimaryButton(
                "Check Answer",
                MaterialIconKind.Check,
                async () =>
                {
                    if (!studio.IsAnswerRevealed && !string.IsNullOrWhiteSpace(ansBox.Text))
                        await studio.SubmitAnswerAsync(ansBox.Text);
                }
            );
            Grid.SetColumn(submitBtn, 1);
            idRow.Children.Add(submitBtn);
            qCardStack.Children.Add(idRow);
        }

        stack.Children.Add(UiBuilder.Card(qCardStack, padding: new Thickness(18, 16)));

        if (studio.IsAnswerRevealed)
        {
            var feedbackStack = new StackPanel { Spacing = 10 };

            var verdictRow = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
                ColumnSpacing = 8,
            };
            var verdictIcon = UiBuilder.Icon(
                studio.IsCurrentAnswerCorrect
                    ? MaterialIconKind.CheckCircle
                    : MaterialIconKind.CloseCircle,
                18,
                studio.IsCurrentAnswerCorrect
                    ? ThemeService.Brush(ThemeService.SuccessFg)
                    : ThemeService.Brush(ThemeService.PriorityP1Fg)
            );
            verdictIcon.VerticalAlignment = VerticalAlignment.Center;
            verdictRow.Children.Add(verdictIcon);

            var verdictText =
                studio.IsCurrentAnswerCorrect ? "Correct!"
                : q.Kind == QuizQuestionKind.Ordering ? "Incorrect  ·  Correct Sequence:"
                : $"Incorrect  ·  Correct Answer: {q.CorrectAnswer}";

            var verdictLbl = new TextBlock
            {
                Text = verdictText,
                FontSize = 14,
                FontWeight = FontWeight.Bold,
                Foreground = studio.IsCurrentAnswerCorrect
                    ? ThemeService.Brush(ThemeService.SuccessFg)
                    : ThemeService.Brush(ThemeService.PriorityP1Fg),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(verdictLbl, 1);
            verdictRow.Children.Add(verdictLbl);
            feedbackStack.Children.Add(verdictRow);

            // Visual step-by-step sequence card for Ordering corrections (Issue #1)
            if (!studio.IsCurrentAnswerCorrect && q.Kind == QuizQuestionKind.Ordering)
            {
                var expectedSteps =
                    q.Options.Count >= 2
                        ? q.Options
                        :
                        [
                            .. q.CorrectAnswer.Split(
                                ["->", "|", "\n", ";"],
                                StringSplitOptions.RemoveEmptyEntries
                                    | StringSplitOptions.TrimEntries
                            ),
                        ];

                if (expectedSteps.Count > 0)
                {
                    var seqStack = new StackPanel { Spacing = 4 };
                    for (int sIdx = 0; sIdx < expectedSteps.Count; sIdx++)
                    {
                        var stepRow = new Grid
                        {
                            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
                            ColumnSpacing = 8,
                        };
                        var stepNumBadge = UiBuilder.Pill(
                            $"{sIdx + 1}",
                            ThemeService.Brush(ThemeService.SuccessFg),
                            ThemeService.Brush(ThemeService.SuccessBg),
                            fontSize: 11
                        );
                        stepNumBadge.VerticalAlignment = VerticalAlignment.Center;
                        stepRow.Children.Add(stepNumBadge);

                        var stepLbl = new TextBlock
                        {
                            Text = expectedSteps[sIdx],
                            FontSize = 12.5,
                            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                            TextWrapping = TextWrapping.Wrap,
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        Grid.SetColumn(stepLbl, 1);
                        stepRow.Children.Add(stepLbl);

                        seqStack.Children.Add(
                            new Border
                            {
                                Background = ThemeService.Brush(ThemeService.BgSurface),
                                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                                BorderThickness = new Thickness(1),
                                CornerRadius = new CornerRadius(5),
                                Padding = new Thickness(10, 6),
                                Child = stepRow,
                            }
                        );

                        if (sIdx < expectedSteps.Count - 1)
                        {
                            var arrow = UiBuilder.Icon(
                                MaterialIconKind.ArrowDown,
                                13,
                                ThemeService.Brush(ThemeService.TextMuted)
                            );
                            arrow.HorizontalAlignment = HorizontalAlignment.Center;
                            seqStack.Children.Add(arrow);
                        }
                    }

                    feedbackStack.Children.Add(
                        new Border
                        {
                            Background = ThemeService.Brush(ThemeService.BgBase),
                            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(6),
                            Padding = new Thickness(10, 8),
                            Child = seqStack,
                        }
                    );
                }
            }

            if (!string.IsNullOrWhiteSpace(studio.LastConfidenceFeedback))
            {
                feedbackStack.Children.Add(
                    new Border
                    {
                        Background = ThemeService.Brush(ThemeService.AccentSoftBg),
                        BorderBrush = ThemeService.Brush(ThemeService.AccentPrimary),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(5),
                        Padding = new Thickness(10, 5),
                        Child = new TextBlock
                        {
                            Text = studio.LastConfidenceFeedback,
                            FontSize = 11.5,
                            FontWeight = FontWeight.Medium,
                            Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
                            TextWrapping = TextWrapping.Wrap,
                        },
                    }
                );
            }

            if (!string.IsNullOrWhiteSpace(q.Explanation))
            {
                feedbackStack.Children.Add(BuildContrastiveExplanationView(q.Explanation));
            }

            var evidenceStack = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        Text =
                            $"{(q.IsPdfSource ? "Master PDF Evidence" : "Source Evidence")}  ·  {q.SectionTitle}  ·  {(q.IsPdfSource ? $"Page {q.SourcePage}" : q.EvidenceChipLabel)}",
                        FontSize = 11.5,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = ThemeService.Brush(ThemeService.PriorityP2Fg),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(q.VerbatimQuote)
                            ? "(Grounded to Page " + q.SourcePage + ")"
                            : $"\"{q.VerbatimQuote}\"",
                        FontSize = 12.5,
                        Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            };

            if (q.EvidenceAnchors.Count > 1)
            {
                var jumpWrap = new WrapPanel { Orientation = Orientation.Horizontal };
                for (int aIdx = 0; aIdx < q.EvidenceAnchors.Count; aIdx++)
                {
                    var anchor = q.EvidenceAnchors[aIdx];
                    var jumpBtn = UiBuilder.GhostButton(
                        $"Statement {aIdx + 1} · p. {anchor.PageNumber}",
                        MaterialIconKind.Marker,
                        () => studio.OpenEvidenceAnchorInPdfReader(q, anchor),
                        isActive: aIdx == 0
                    );
                    jumpBtn.Margin = new Thickness(0, 0, 6, 4);
                    jumpWrap.Children.Add(jumpBtn);
                }
                evidenceStack.Children.Add(jumpWrap);
            }
            else
            {
                evidenceStack.Children.Add(
                    UiBuilder.GhostButton(
                        q.IsPdfSource
                            ? $"Jump to Highlighted Statement on Page {q.SourcePage} in PDF Reader"
                            : $"Open {q.EvidenceChipLabel} in Source Viewer",
                        MaterialIconKind.Marker,
                        () => studio.OpenQuestionEvidenceInPdfReader(q),
                        isActive: true
                    )
                );
            }

            var evidenceBox = new Border
            {
                Background = ThemeService.Brush(ThemeService.BgBase),
                BorderBrush = ThemeService.Brush(ThemeService.PriorityP2Fg),
                BorderThickness = new Thickness(3, 0, 0, 0),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 10),
                Child = evidenceStack,
            };
            feedbackStack.Children.Add(evidenceBox);

            var srsRow = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto") };
            var prevBtn = UiBuilder.GhostButton(
                "Previous",
                MaterialIconKind.ChevronLeft,
                studio.PreviousQuestion
            );
            srsRow.Children.Add(prevBtn);

            var rateGroup = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            rateGroup.Children.Add(
                UiBuilder.GhostButton(
                    "Again (1d)",
                    MaterialIconKind.Refresh,
                    async () => await studio.RateAndAdvanceAsync(1)
                )
            );
            rateGroup.Children.Add(
                UiBuilder.GhostButton("Hard", null, async () => await studio.RateAndAdvanceAsync(3))
            );
            rateGroup.Children.Add(
                UiBuilder.GhostButton("Good", null, async () => await studio.RateAndAdvanceAsync(4))
            );
            rateGroup.Children.Add(
                UiBuilder.GhostButton("Easy", null, async () => await studio.RateAndAdvanceAsync(5))
            );
            Grid.SetColumn(rateGroup, 1);
            srsRow.Children.Add(rateGroup);

            bool isLast = studio.CurrentQuestionIndex + 1 >= session.Questions.Count;
            var nextBtn = UiBuilder.PrimaryButton(
                isLast ? "Finish & View Summary" : "Next Question",
                MaterialIconKind.ArrowRight,
                async () => await studio.AdvanceNextQuestionAsync()
            );
            Grid.SetColumn(nextBtn, 2);
            srsRow.Children.Add(nextBtn);

            feedbackStack.Children.Add(srsRow);
            stack.Children.Add(UiBuilder.Card(feedbackStack, padding: new Thickness(16, 14)));
        }

        var scroll = UiBuilder.PersistentScrollViewer(
            $"quiz-runner:{session.Id}:{studio.CurrentQuestionIndex}",
            stack
        );

        if (
            !string.IsNullOrWhiteSpace(studio.ZoomedImagePath)
            && File.Exists(studio.ZoomedImagePath)
        )
        {
            var host = new Grid();
            host.Children.Add(scroll);
            host.Children.Add(
                BuildImageZoomLightbox(studio.ZoomedImagePath, studio.CloseImageZoom)
            );
            return host;
        }

        return scroll;
    }

    private static Border BuildImageZoomLightbox(string fullImagePath, Action onClose)
    {
        var closeBtn = UiBuilder.PrimaryButton("Close", MaterialIconKind.Close, onClose);
        closeBtn.HorizontalAlignment = HorizontalAlignment.Right;

        var inner = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                closeBtn,
                new Border
                {
                    Background = Brushes.White,
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(6),
                    Child = new Image
                    {
                        Source = new Avalonia.Media.Imaging.Bitmap(fullImagePath),
                        Stretch = Stretch.Uniform,
                        MaxWidth = 960,
                        MaxHeight = 680,
                    },
                },
            },
        };

        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#C8000000")),
            Padding = new Thickness(24),
            Child = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = inner,
            },
        };
        overlay.PointerPressed += (_, _) => onClose();
        return overlay;
    }

    private static ScrollViewer BuildSessionSummaryView(
        MainWindowViewModel vm,
        QuizStudioViewModel studio
    )
    {
        var session = studio.ActiveSession!;
        int total = Math.Max(1, session.Questions.Count);
        int correct = session.Questions.Count(q =>
            studio.SessionResults.TryGetValue(q.Id, out var r) && r.IsCorrect
        );
        double pct = 100.0 * correct / total;

        var stack = new StackPanel
        {
            Spacing = 14,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(12, 8, 12, 28),
        };

        var headerRow = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"),
            ColumnSpacing = 8,
        };
        headerRow.Children.Add(
            new TextBlock
            {
                Text = $"{session.Title} — Results & Evidence Review",
                FontSize = 19,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );

        var retryBtn = UiBuilder.GhostButton(
            "Retake Assessment",
            MaterialIconKind.Restart,
            () => studio.StartQuizSession(session)
        );
        Grid.SetColumn(retryBtn, 1);
        headerRow.Children.Add(retryBtn);

        var doneBtn = UiBuilder.PrimaryButton(
            "Back to Studio",
            MaterialIconKind.Check,
            studio.ExitActiveSession
        );
        Grid.SetColumn(doneBtn, 2);
        headerRow.Children.Add(doneBtn);
        stack.Children.Add(headerRow);

        stack.Children.Add(
            UiBuilder.Card(
                new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"Score: {correct} / {session.Questions.Count} ({pct:0.#}%)",
                            FontSize = 17,
                            FontWeight = FontWeight.Bold,
                            Foreground =
                                pct >= 75
                                    ? ThemeService.Brush(ThemeService.SuccessFg)
                                    : ThemeService.Brush(ThemeService.PriorityP2Fg),
                        },
                        new TextBlock
                        {
                            Text =
                                "Click 'Highlight on Page N' on any question below to inspect the exact grounded statement inside the Built-in PDF Reader.",
                            FontSize = 12,
                            Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                        },
                    },
                },
                padding: new Thickness(16, 12)
            )
        );

        for (int i = 0; i < session.Questions.Count; i++)
        {
            var q = session.Questions[i];
            studio.SessionResults.TryGetValue(q.Id, out var res);
            bool ok = res.IsCorrect;

            var qStack = new StackPanel { Spacing = 6 };
            var topGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
            topGrid.Children.Add(
                new TextBlock
                {
                    Text = $"{i + 1}. {q.Prompt}",
                    FontSize = 13.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    TextWrapping = TextWrapping.Wrap,
                }
            );

            var jumpBtn = UiBuilder.GhostButton(
                q.IsPdfSource ? $"Highlight on Page {q.SourcePage}" : $"View {q.EvidenceChipLabel}",
                MaterialIconKind.Marker,
                () => studio.OpenQuestionEvidenceInPdfReader(q),
                isActive: true
            );
            Grid.SetColumn(jumpBtn, 1);
            topGrid.Children.Add(jumpBtn);
            qStack.Children.Add(topGrid);

            qStack.Children.Add(
                new TextBlock
                {
                    Text = ok
                        ? $"Your Answer: {res.Answer} (Correct)"
                        : $"Your Answer: {(string.IsNullOrWhiteSpace(res.Answer) ? "Skipped" : res.Answer)}  ·  Correct Answer: {q.CorrectAnswer}",
                    FontSize = 12,
                    FontWeight = FontWeight.Medium,
                    Foreground = ok
                        ? ThemeService.Brush(ThemeService.SuccessFg)
                        : ThemeService.Brush(ThemeService.PriorityP1Fg),
                }
            );

            if (!string.IsNullOrWhiteSpace(q.Explanation))
            {
                qStack.Children.Add(BuildContrastiveExplanationView(q.Explanation, fontSize: 12));
            }

            if (!string.IsNullOrWhiteSpace(q.VerbatimQuote))
            {
                qStack.Children.Add(
                    new TextBlock
                    {
                        Text =
                            $"Evidence ({q.SectionTitle}, {(q.IsPdfSource ? $"p. {q.SourcePage}" : q.EvidenceChipLabel)}): \"{q.VerbatimQuote}\"",
                        FontSize = 11.5,
                        Foreground = ThemeService.Brush(ThemeService.TextMuted),
                        TextWrapping = TextWrapping.Wrap,
                    }
                );
            }

            stack.Children.Add(UiBuilder.Card(qStack, padding: new Thickness(14, 10)));
        }

        return UiBuilder.PersistentScrollViewer($"quiz-summary:{session.Id}", stack);
    }
}
