using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using ScholarFlow.Core.Quiz;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views;

public static partial class QuizStudioViewBuilder
{
    private static string? _lastAnimatedRunnerQuestionKey;
    private static string? _lastAnimatedRunnerFeedbackKey;

    private static void AttachSmoothEntrance(
        Control target,
        double slideY = 8,
        int durationMs = 160
    )
    {
        var transform = new TranslateTransform(0, slideY)
        {
            Transitions =
            [
                new DoubleTransition
                {
                    Property = TranslateTransform.YProperty,
                    Duration = TimeSpan.FromMilliseconds(durationMs),
                    Easing = new CubicEaseOut(),
                },
            ],
        };
        target.RenderTransform = transform;
        target.Opacity = 0;
        target.Transitions =
        [
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                Easing = new CubicEaseOut(),
            },
        ];
        target.AttachedToVisualTree += (_, _) =>
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    target.Opacity = 1;
                    transform.Y = 0;
                },
                DispatcherPriority.Render
            );
        };
    }

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
            MaxWidth = 820,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(16, 10, 16, 28),
        };

        // 1. Clean Minimal Top Bar (Exit | Title | Quick Edit + Flag + Restart)
        var topBar = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
            ColumnSpacing = 10,
        };

        var exitBtn = UiBuilder.GhostButton(
            "Exit",
            MaterialIconKind.ArrowLeft,
            studio.ExitActiveSession
        );
        ToolTip.SetTip(exitBtn, "Exit back to Quiz Studio");
        topBar.Children.Add(exitBtn);

        var centerTitle = new TextBlock
        {
            Text = session.Title,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(centerTitle, 1);
        topBar.Children.Add(centerTitle);

        bool isFlaggedForAi = studio.DeckAiFixSelectedIds.Contains(q.Id);
        var topRightActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var quickEditBtn = UiBuilder.GhostButton(
            "Edit",
            MaterialIconKind.PencilOutline,
            studio.OpenRunnerQuickEdit
        );
        ToolTip.SetTip(quickEditBtn, "Quick edit this question (E)");
        topRightActions.Children.Add(quickEditBtn);

        var aiFixBtn = UiBuilder.IconButton(
            MaterialIconKind.AutoFix,
            studio.OpenRunnerSingleQuestionAiFix,
            15.5,
            ThemeService.Brush(ThemeService.TextSecondary)
        );
        ToolTip.SetTip(aiFixBtn, "Fix this question with AI");
        topRightActions.Children.Add(aiFixBtn);

        var openInDeckBtn = UiBuilder.IconButton(
            MaterialIconKind.TuneVariant,
            studio.OpenCurrentQuestionInDeckOrganizer,
            15.5,
            ThemeService.Brush(ThemeService.TextSecondary)
        );
        ToolTip.SetTip(openInDeckBtn, "Open this question in Deck Organizer");
        topRightActions.Children.Add(openInDeckBtn);

        var flagAiBtn = UiBuilder.IconButton(
            isFlaggedForAi ? MaterialIconKind.Flag : MaterialIconKind.FlagOutline,
            () => studio.ToggleRunnerQuestionFlagForAi(q.Id),
            15.5,
            isFlaggedForAi
                ? ThemeService.Brush(ThemeService.PriorityP2Fg)
                : ThemeService.Brush(ThemeService.TextSecondary)
        );
        ToolTip.SetTip(
            flagAiBtn,
            isFlaggedForAi
                ? "Flagged for AI Fix in Deck Organizer — click to unflag"
                : "Flag this question for AI Fix in Deck Organizer"
        );
        topRightActions.Children.Add(flagAiBtn);

        var restartBtn = UiBuilder.IconButton(
            MaterialIconKind.Restart,
            () => studio.StartQuizSession(session, resumeSavedProgress: false),
            15.5,
            ThemeService.Brush(ThemeService.TextSecondary)
        );
        ToolTip.SetTip(restartBtn, "Restart session from question 1");
        topRightActions.Children.Add(restartBtn);

        Grid.SetColumn(topRightActions, 2);
        topBar.Children.Add(topRightActions);
        stack.Children.Add(topBar);

        // 2. Compact Progress Bar + Live Stats + Floating Question Map Popover (zero 50-button wall)
        int totalQ = Math.Max(1, session.Questions.Count);
        int answeredQ = session.Questions.Count(qq => studio.SessionResults.ContainsKey(qq.Id));
        int correctQ = session.Questions.Count(qq =>
            studio.SessionResults.TryGetValue(qq.Id, out var r) && r.IsCorrect
        );
        int missedQ = answeredQ - correctQ;

        var progressCardStack = new StackPanel { Spacing = 6 };
        progressCardStack.Children.Add(
            new ProgressBar
            {
                Minimum = 0,
                Maximum = totalQ,
                Value = answeredQ,
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
                Background = ThemeService.Brush(ThemeService.BgElevated),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            }
        );

        var statsRow = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        var statsText = new TextBlock
        {
            Text =
                answeredQ > 0
                    ? $"{answeredQ} / {session.Questions.Count} answered  ·  {correctQ} correct  ·  {missedQ} missed"
                    : $"0 / {session.Questions.Count} answered",
            FontSize = 11.5,
            Foreground = ThemeService.Brush(ThemeService.TextSecondary),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        statsRow.Children.Add(statsText);

        // Floating Question Map Flyout (compact color-coded tiles 1..N)
        var mapFlyoutHost = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(2),
            Width = 312,
            MaxHeight = 290,
        };
        var mapFlyout = new Flyout
        {
            Content = mapFlyoutHost,
            Placement = PlacementMode.BottomEdgeAlignedRight,
        };
        mapFlyout.Opening += (_, _) =>
        {
            var mapWrap = new WrapPanel { Orientation = Orientation.Horizontal };
            for (int qi = 0; qi < session.Questions.Count; qi++)
            {
                int capturedIndex = qi;
                var itemQ = session.Questions[qi];
                bool isCurrent = qi == studio.CurrentQuestionIndex;
                bool hasRes = studio.SessionResults.TryGetValue(itemQ.Id, out var qRes);

                var tileBg =
                    hasRes
                        ? (
                            qRes.IsCorrect
                                ? ThemeService.Brush(ThemeService.SuccessBg)
                                : ThemeService.Brush(ThemeService.PriorityP1Bg)
                        )
                    : isCurrent ? ThemeService.Brush(ThemeService.BgElevated)
                    : Brushes.Transparent;
                var tileFg =
                    hasRes
                        ? (
                            qRes.IsCorrect
                                ? ThemeService.Brush(ThemeService.SuccessFg)
                                : ThemeService.Brush(ThemeService.PriorityP1Fg)
                        )
                    : isCurrent ? ThemeService.Brush(ThemeService.TextPrimary)
                    : ThemeService.Brush(ThemeService.TextSecondary);
                var tileBorder =
                    isCurrent ? ThemeService.Brush(ThemeService.TextPrimary)
                    : hasRes ? tileFg
                    : ThemeService.Brush(ThemeService.BorderSubtle);

                var tileBtn = new Button
                {
                    Content = new TextBlock
                    {
                        Text = (qi + 1).ToString(),
                        FontSize = 11,
                        FontWeight = isCurrent || hasRes ? FontWeight.SemiBold : FontWeight.Normal,
                        Foreground = tileFg,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    Width = 32,
                    Height = 28,
                    Margin = new Thickness(2),
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(5),
                    Background = tileBg,
                    BorderBrush = tileBorder,
                    BorderThickness = new Thickness(isCurrent ? 1.5 : 1),
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Cursor = new Cursor(StandardCursorType.Hand),
                };
                ToolTip.SetTip(
                    tileBtn,
                    $"Q{qi + 1}: {(hasRes ? (qRes.IsCorrect ? "Correct" : "Missed") : "Unanswered")}"
                );
                tileBtn.Click += (_, _) =>
                {
                    mapFlyout.Hide();
                    studio.JumpToQuestion(capturedIndex);
                };
                mapWrap.Children.Add(tileBtn);
            }

            mapFlyoutHost.Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    UiBuilder.SectionHeader($"Jump to Question (1–{session.Questions.Count})"),
                    new ScrollViewer
                    {
                        Content = mapWrap,
                        MaxHeight = 240,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    },
                },
            };
        };

        var jumpMapBtn = UiBuilder.GhostButton(
            $"Q {studio.CurrentQuestionIndex + 1} / {session.Questions.Count} ▾",
            MaterialIconKind.ViewGridOutline,
            () => { }
        );
        jumpMapBtn.Flyout = mapFlyout;
        ToolTip.SetTip(jumpMapBtn, "Open question map to jump to any question");
        Grid.SetColumn(jumpMapBtn, 1);
        statsRow.Children.Add(jumpMapBtn);

        progressCardStack.Children.Add(statsRow);
        stack.Children.Add(UiBuilder.Card(progressCardStack, padding: new Thickness(12, 8)));

        // 3. Main Question Card (with calm metadata strip + smooth entrance)
        var qCardStack = new StackPanel { Spacing = 14 };

        var metaStrip = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        void AddCalmMetaPill(string text)
        {
            var pill = UiBuilder.Pill(
                text,
                ThemeService.Brush(ThemeService.TextSecondary),
                ThemeService.Brush(ThemeService.BgElevated),
                fontSize: 10.5
            );
            pill.Margin = new Thickness(0, 0, 5, 4);
            metaStrip.Children.Add(pill);
        }

        AddCalmMetaPill(q.Kind.ToString());
        AddCalmMetaPill($"Cognitive: {q.CognitiveLevel}");
        if (!string.IsNullOrWhiteSpace(q.Archetype))
            AddCalmMetaPill(QuizBlueprintBuilder.FormatArchetypeBadge(q.Archetype));
        if (!string.IsNullOrWhiteSpace(q.TopicTag))
            AddCalmMetaPill($"@{q.TopicTag}");
        if (isFlaggedForAi)
        {
            var flagPill = UiBuilder.Pill(
                "Flagged for AI Fix",
                ThemeService.Brush(ThemeService.PriorityP2Fg),
                ThemeService.Brush(ThemeService.PriorityP2Bg),
                fontSize: 10.5
            );
            flagPill.Margin = new Thickness(0, 0, 5, 4);
            metaStrip.Children.Add(flagPill);
        }
        qCardStack.Children.Add(metaStrip);

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
                    Margin = new Thickness(0, -6, 0, 0),
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

                if (!studio.IsAnswerRevealed)
                {
                    var capturedNormalBg = bgBrush;
                    var capturedNormalBorder = borderBrush;
                    optBtn.PointerEntered += (_, _) =>
                    {
                        optBtn.Background = ThemeService.Brush(ThemeService.BgHover);
                        optBtn.BorderBrush = ThemeService.Brush(ThemeService.BorderStrong);
                    };
                    optBtn.PointerExited += (_, _) =>
                    {
                        optBtn.Background = capturedNormalBg;
                        optBtn.BorderBrush = capturedNormalBorder;
                    };
                }

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
                    UiBuilder.Pill(
                        $"Step {i + 1}",
                        ThemeService.Brush(ThemeService.TextSecondary),
                        ThemeService.Brush(ThemeService.BgSurface),
                        fontSize: 10.5
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
                        Background = ThemeService.Brush(ThemeService.BgElevated),
                        BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(10, 6),
                        Child = new TextBlock
                        {
                            Text =
                                "Fill in the missing term or phrase for the blank ( _____ ) above:",
                            FontSize = 12,
                            FontWeight = FontWeight.Medium,
                            Foreground = ThemeService.Brush(ThemeService.TextSecondary),
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

        var qCard = UiBuilder.Card(qCardStack, padding: new Thickness(18, 16));
        var qAnimKey = $"{session.Id}:{q.Id}";
        if (!string.Equals(_lastAnimatedRunnerQuestionKey, qAnimKey, StringComparison.Ordinal))
        {
            _lastAnimatedRunnerQuestionKey = qAnimKey;
            _lastAnimatedRunnerFeedbackKey = null;
            AttachSmoothEntrance(qCard, slideY: 6, durationMs: 155);
        }
        if (!studio.IsAnswerRevealed)
            _lastAnimatedRunnerFeedbackKey = null;
        stack.Children.Add(qCard);

        if (!studio.IsAnswerRevealed)
        {
            // Always-accessible bottom Previous / Skip navigation bar
            var preNavRow = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto") };
            var prePrevBtn = UiBuilder.GhostButton(
                "Previous",
                MaterialIconKind.ChevronLeft,
                studio.PreviousQuestion
            );
            prePrevBtn.IsEnabled = studio.CurrentQuestionIndex > 0;
            preNavRow.Children.Add(prePrevBtn);

            bool isLastPre = studio.CurrentQuestionIndex + 1 >= session.Questions.Count;
            var skipBtn = UiBuilder.GhostButton(
                isLastPre ? "Finish" : "Skip",
                MaterialIconKind.ChevronRight,
                async () => await studio.AdvanceNextQuestionAsync()
            );
            Grid.SetColumn(skipBtn, 2);
            preNavRow.Children.Add(skipBtn);
            stack.Children.Add(preNavRow);
        }

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
                        Background = ThemeService.Brush(ThemeService.BgElevated),
                        BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(5),
                        Padding = new Thickness(10, 5),
                        Child = new TextBlock
                        {
                            Text = studio.LastConfidenceFeedback,
                            FontSize = 11.5,
                            FontWeight = FontWeight.Medium,
                            Foreground = ThemeService.Brush(ThemeService.TextSecondary),
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
            var feedbackCard = UiBuilder.Card(feedbackStack, padding: new Thickness(16, 14));
            var fbAnimKey = $"{session.Id}:{q.Id}:revealed";
            if (!string.Equals(_lastAnimatedRunnerFeedbackKey, fbAnimKey, StringComparison.Ordinal))
            {
                _lastAnimatedRunnerFeedbackKey = fbAnimKey;
                AttachSmoothEntrance(feedbackCard, slideY: 10, durationMs: 180);
            }
            stack.Children.Add(feedbackCard);
        }

        var scroll = UiBuilder.PersistentScrollViewer(
            $"quiz-runner:{session.Id}:{studio.CurrentQuestionIndex}",
            stack
        );

        bool hasImageLightbox =
            !string.IsNullOrWhiteSpace(studio.ZoomedImagePath)
            && File.Exists(studio.ZoomedImagePath);
        if (hasImageLightbox)
        {
            var host = new Grid();
            host.Children.Add(scroll);
            host.Children.Add(
                BuildImageZoomLightbox(studio.ZoomedImagePath!, studio.CloseImageZoom)
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

        int missed = session.Questions.Count - correct;

        var stack = new StackPanel
        {
            Spacing = 14,
            MaxWidth = 820,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(16, 10, 16, 28),
        };

        var headerRow = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 8,
        };
        headerRow.Children.Add(
            new TextBlock
            {
                Text = $"{session.Title} — Results",
                FontSize = 18.5,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            }
        );

        var summaryActions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (missed > 0)
        {
            var retryMissedBtn = UiBuilder.GhostButton(
                $"Retry Missed ({missed})",
                MaterialIconKind.Target,
                studio.RetryMissedQuestions,
                isActive: true
            );
            retryMissedBtn.Margin = new Thickness(0, 0, 6, 0);
            summaryActions.Children.Add(retryMissedBtn);
        }

        var retryBtn = UiBuilder.GhostButton(
            "Retake All",
            MaterialIconKind.Restart,
            () => studio.StartQuizSession(session, resumeSavedProgress: false)
        );
        retryBtn.Margin = new Thickness(0, 0, 6, 0);
        summaryActions.Children.Add(retryBtn);

        var doneBtn = UiBuilder.PrimaryButton(
            "Back to Studio",
            MaterialIconKind.Check,
            studio.ExitActiveSession
        );
        summaryActions.Children.Add(doneBtn);

        Grid.SetColumn(summaryActions, 1);
        headerRow.Children.Add(summaryActions);
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
