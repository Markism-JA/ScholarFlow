using System.Text.RegularExpressions;
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
    [GeneratedRegex(
        @"(?i)\b(Why correct:|Why distractors? fail:|Core principle:)",
        RegexOptions.Compiled,
        "en-US"
    )]
    private static partial Regex SplitConstructiveSplit();

    private static readonly Regex ContrastiveSplitRegex = SplitConstructiveSplit();

    /// <summary>
    /// Interactive 4-column Cognitive Depth allocation bar (Recall / Comprehension / Application / Analysis)
    /// integrated directly underneath Question-Type distribution.
    /// </summary>
    private static StackPanel BuildCognitiveDepthAllocationSection(QuizStudioViewModel studio)
    {
        var stack = new StackPanel { Spacing = 5 };

        var headerGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 8,
        };
        headerGrid.Children.Add(
            new TextBlock
            {
                Text =
                    "Cognitive Depth Distribution (Recall · Comprehension · Application · Analysis)",
                FontSize = 11.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );

        var mixPills = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        foreach (
            var (mix, label) in new[]
            {
                (CognitiveMixPreset.Balanced, "Balanced"),
                (CognitiveMixPreset.Foundations, "Foundations"),
                (CognitiveMixPreset.ExamApplication, "Applied"),
                (CognitiveMixPreset.DeepMastery, "Deep Analysis"),
            }
        )
        {
            var captured = mix;
            var btn = UiBuilder.GhostButton(
                label,
                null,
                () => studio.SetCognitiveMix(captured),
                isActive: studio.SelectedPreset != QuizPresetMode.Custom
                    && studio.CognitiveMix == captured
            );
            btn.Margin = new Thickness(4, 0, 0, 2);
            mixPills.Children.Add(btn);
        }
        Grid.SetColumn(mixPills, 1);
        headerGrid.Children.Add(mixPills);
        stack.Children.Add(headerGrid);

        var resolvedCogCounts = studio.GetResolvedExactCognitiveCounts();
        var resolvedCogPcts = studio.GetResolvedCognitivePercentages();

        var colsGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,8,*"),
            RowDefinitions = RowDefinitions.Parse("Auto,Auto"),
            RowSpacing = 6,
        };

        var levels = new[]
        {
            (CognitiveLevel.Recall, "Recall"),
            (CognitiveLevel.Comprehension, "Comprehension"),
            (CognitiveLevel.Application, "Application"),
            (CognitiveLevel.Analysis, "Analysis"),
        };

        for (int i = 0; i < levels.Length; i++)
        {
            var (lvl, title) = levels[i];
            var capturedLvl = lvl;
            int rawVal = studio.CognitiveAllocations.GetValueOrDefault(lvl, 0);
            int pct = resolvedCogPcts.GetValueOrDefault(lvl, 0);
            int exactItems = resolvedCogCounts.GetValueOrDefault(lvl, 0);
            bool active = exactItems > 0 || rawVal > 0;

            var cellRow = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"),
                ColumnSpacing = 8,
            };
            cellRow.Children.Add(
                new TextBlock
                {
                    Text = title,
                    FontSize = 12,
                    FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal,
                    Foreground = active
                        ? ThemeService.Brush(ThemeService.TextPrimary)
                        : ThemeService.Brush(ThemeService.TextMuted),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                }
            );

            var stepperGroup = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 3,
                VerticalAlignment = VerticalAlignment.Center,
            };
            stepperGroup.Children.Add(
                UiBuilder.IconButton(
                    MaterialIconKind.Minus,
                    () => studio.StepCognitiveAllocation(capturedLvl, -1),
                    12.5,
                    ThemeService.Brush(ThemeService.TextSecondary)
                )
            );

            var valBox = new TextBox
            {
                Text = rawVal.ToString(),
                FontSize = 11.5,
                MinWidth = 42,
                MaxWidth = 56,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            valBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && int.TryParse(valBox.Text?.Trim(), out int v))
                {
                    e.Handled = true;
                    studio.SetCognitiveAllocation(capturedLvl, v, notify: true);
                }
            };
            valBox.LostFocus += (_, _) =>
            {
                if (
                    int.TryParse(valBox.Text?.Trim(), out int v)
                    && studio.CognitiveAllocations.GetValueOrDefault(capturedLvl, -1) != v
                )
                {
                    studio.SetCognitiveAllocation(capturedLvl, v, notify: true);
                }
            };
            stepperGroup.Children.Add(valBox);

            stepperGroup.Children.Add(
                UiBuilder.IconButton(
                    MaterialIconKind.Plus,
                    () => studio.StepCognitiveAllocation(capturedLvl, +1),
                    12.5,
                    ThemeService.Brush(ThemeService.TextSecondary)
                )
            );
            Grid.SetColumn(stepperGroup, 1);
            cellRow.Children.Add(stepperGroup);

            var itemPill = UiBuilder.Pill(
                $"{pct}% · {exactItems}",
                active
                    ? ThemeService.Brush(ThemeService.PriorityP3Fg)
                    : ThemeService.Brush(ThemeService.TextMuted),
                active
                    ? ThemeService.Brush(ThemeService.PriorityP3Bg)
                    : ThemeService.Brush(ThemeService.BgSurface),
                fontSize: 10.5
            );
            itemPill.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(itemPill, 2);
            cellRow.Children.Add(itemPill);

            var cellBorder = new Border
            {
                Background = active
                    ? ThemeService.Brush(ThemeService.BgElevated)
                    : ThemeService.Brush(ThemeService.BgBase),
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(9, 6),
                Child = cellRow,
            };
            Grid.SetRow(cellBorder, i / 2);
            Grid.SetColumn(cellBorder, i % 2 * 2);
            colsGrid.Children.Add(cellBorder);
        }

        stack.Children.Add(colsGrid);
        return stack;
    }

    private static Border BuildSkeletonBlock(
        double height,
        double? width = null,
        double opacity = 0.65,
        CornerRadius? radius = null
    )
    {
        var b = new Border
        {
            Height = height,
            Background = ThemeService.Brush(ThemeService.BgElevated),
            CornerRadius = radius ?? new CornerRadius(4),
            Opacity = opacity,
            HorizontalAlignment = width.HasValue
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Stretch,
        };
        if (width.HasValue)
            b.Width = width.Value;
        return b;
    }

    private static Border BuildOutlineSkeleton()
    {
        var list = new StackPanel { Spacing = 8, Margin = new Thickness(6, 8) };
        list.Children.Add(
            new ProgressBar
            {
                IsIndeterminate = true,
                Height = 3,
                Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
                Background = ThemeService.Brush(ThemeService.BgElevated),
            }
        );
        foreach (var (w, op) in new[] { (240.0, 0.7), (190.0, 0.55), (215.0, 0.65), (165.0, 0.5) })
        {
            var row = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("16,*,54"),
                ColumnSpacing = 8,
            };
            row.Children.Add(BuildSkeletonBlock(14, 14, op));
            var titleBar = BuildSkeletonBlock(13, w, op);
            Grid.SetColumn(titleBar, 1);
            row.Children.Add(titleBar);
            var pageBar = BuildSkeletonBlock(13, 46, op * 0.85);
            Grid.SetColumn(pageBar, 2);
            row.Children.Add(pageBar);
            list.Children.Add(row);
        }

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgBase),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6),
            Child = list,
        };
    }

    private static Border BuildPdfPageSkeleton(int pageNumber)
    {
        var sheetContent = new StackPanel { Spacing = 12, Margin = new Thickness(24, 26) };
        sheetContent.Children.Add(
            new ProgressBar
            {
                IsIndeterminate = true,
                Height = 3,
                Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
                Background = ThemeService.Brush(ThemeService.BgElevated),
            }
        );
        sheetContent.Children.Add(BuildSkeletonBlock(18, 220, 0.75));
        sheetContent.Children.Add(BuildSkeletonBlock(11, null, 0.55));
        sheetContent.Children.Add(BuildSkeletonBlock(11, null, 0.50));
        sheetContent.Children.Add(BuildSkeletonBlock(11, 280, 0.45));
        sheetContent.Children.Add(BuildSkeletonBlock(110, null, 0.40, new CornerRadius(6)));
        sheetContent.Children.Add(BuildSkeletonBlock(11, null, 0.55));
        sheetContent.Children.Add(BuildSkeletonBlock(11, null, 0.50));
        sheetContent.Children.Add(BuildSkeletonBlock(11, 240, 0.45));
        sheetContent.Children.Add(
            new TextBlock
            {
                Text = $"Rendering PDF Page {pageNumber}...",
                FontSize = 11.5,
                Foreground = ThemeService.Brush(ThemeService.TextMuted),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0),
            }
        );

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(14),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = sheetContent,
        };
    }

    /// <summary>
    /// Unified Content Modality, Question Archetypes &amp; Active Recall section (replaces the old separate
    /// Specialized Content Formats row and disconnected Learning-Science box).
    /// </summary>
    private static StackPanel BuildIntegratedModalityAndArchetypesSection(
        QuizStudioViewModel studio
    )
    {
        var stack = new StackPanel { Spacing = 8 };

        // 1. Content Modality & Visual Representation
        var modHeader = new TextBlock
        {
            Text =
                "Content Modality & Question Styles (Auto-adapts to Code, Math LaTeX, Diagrams & Images)",
            FontSize = 11.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeService.Brush(ThemeService.TextSecondary),
        };
        stack.Children.Add(modHeader);

        var modalityWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (
            var (mMode, mLabel, mIcon) in new[]
            {
                (
                    QuizContentModalityMode.AutoDetect,
                    "Auto-Detect Format",
                    MaterialIconKind.AutoFix
                ),
                (
                    QuizContentModalityMode.StandardProseOnly,
                    "Prose Only",
                    MaterialIconKind.TextBoxOutline
                ),
                (
                    QuizContentModalityMode.IncludeCodeSnippets,
                    "Code Snippets",
                    MaterialIconKind.CodeTags
                ),
                (
                    QuizContentModalityMode.IncludeMathLatex,
                    "Math LaTeX (MCQ)",
                    MaterialIconKind.FunctionVariant
                ),
                (
                    QuizContentModalityMode.IncludeDiagrams,
                    "Flow Diagrams",
                    MaterialIconKind.SitemapOutline
                ),
            }
        )
        {
            var capturedMode = mMode;
            var mBtn = UiBuilder.GhostButton(
                mLabel,
                mIcon,
                () => studio.SetModalityMode(capturedMode),
                isActive: studio.ModalityMode == capturedMode
            );
            mBtn.Margin = new Thickness(0, 0, 5, 4);
            modalityWrap.Children.Add(mBtn);
        }
        stack.Children.Add(modalityWrap);

        // 2. Question Archetypes + Active Recall Toggles in one unified row
        var stylesWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (
            var (arch, label, icon) in new[]
            {
                (QuestionArchetype.FarTransfer, "Far-Transfer Scenarios", MaterialIconKind.Earth),
                (
                    QuestionArchetype.DiscriminativeContrast,
                    "Contrast X vs Y",
                    MaterialIconKind.CompareHorizontal
                ),
                (
                    QuestionArchetype.CausalPerturbation,
                    "What-If / Causal",
                    MaterialIconKind.SourceBranch
                ),
                (
                    QuestionArchetype.SpotTheFlaw,
                    "Spot-the-Flaw / Debug",
                    MaterialIconKind.BugOutline
                ),
            }
        )
        {
            var capturedArch = arch;
            bool active = studio.SelectedArchetypes.Contains(capturedArch);
            var btn = UiBuilder.GhostButton(
                label,
                active ? MaterialIconKind.Check : icon,
                () => studio.ToggleArchetype(capturedArch),
                isActive: active
            );
            btn.Margin = new Thickness(0, 0, 5, 4);
            stylesWrap.Children.Add(btn);
        }

        if (studio.SelectedArchetypes.Count > 0)
        {
            var resetArchBtn = UiBuilder.GhostButton(
                "All Styles",
                MaterialIconKind.Refresh,
                studio.ClearArchetypes
            );
            resetArchBtn.Margin = new Thickness(0, 0, 6, 4);
            stylesWrap.Children.Add(resetArchBtn);
        }
        stack.Children.Add(stylesWrap);

        // 3. Remediation & Feedback Toggles
        var weakTags = studio.GetCourseWeakTopicTags();
        var recallWrap = new WrapPanel { Orientation = Orientation.Horizontal };

        var weakBtn = UiBuilder.GhostButton(
            weakTags.Count > 0 ? $"Target Weak Topics ({weakTags.Count})" : "Target Weak Topics",
            studio.FocusWeakSpots
                ? MaterialIconKind.CheckboxMarked
                : MaterialIconKind.CheckboxBlankOutline,
            studio.ToggleFocusWeakSpots,
            isActive: studio.FocusWeakSpots
        );
        weakBtn.Margin = new Thickness(0, 0, 5, 4);
        recallWrap.Children.Add(weakBtn);

        var contrastiveBtn = UiBuilder.GhostButton(
            "Contrastive Explanations",
            studio.EnableContrastiveExplanations
                ? MaterialIconKind.CheckboxMarked
                : MaterialIconKind.CheckboxBlankOutline,
            studio.ToggleContrastiveExplanations,
            isActive: studio.EnableContrastiveExplanations
        );
        contrastiveBtn.Margin = new Thickness(0, 0, 5, 4);
        recallWrap.Children.Add(contrastiveBtn);

        var hintsBtn = UiBuilder.GhostButton(
            "Progressive Hints",
            studio.IncludeProgressiveHints
                ? MaterialIconKind.CheckboxMarked
                : MaterialIconKind.CheckboxBlankOutline,
            studio.ToggleProgressiveHints,
            isActive: studio.IncludeProgressiveHints
        );
        hintsBtn.Margin = new Thickness(0, 0, 5, 4);
        recallWrap.Children.Add(hintsBtn);

        stack.Children.Add(recallWrap);

        if (studio.FocusWeakSpots && weakTags.Count > 0)
        {
            stack.Children.Add(
                new TextBlock
                {
                    Text =
                        $"Remediation focus: {string.Join("  ·  ", weakTags.Select(t => "@" + t))}",
                    FontSize = 11,
                    Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
                    TextWrapping = TextWrapping.Wrap,
                }
            );
        }

        return stack;
    }

    /// <summary>
    /// Pre-reveal Progressive Hint &amp; Opt-in Confidence Calibration bar in Quiz Runner.
    /// </summary>
    private static StackPanel? BuildRunnerHintAndConfidenceBar(
        QuizStudioViewModel studio,
        QuizQuestionRecord q
    )
    {
        if (studio.IsAnswerRevealed)
            return null;

        bool hasHint = !string.IsNullOrWhiteSpace(q.Hint);
        var stack = new StackPanel { Spacing = 8 };

        var leftControls = new WrapPanel { Orientation = Orientation.Horizontal };
        if (hasHint && !studio.IsHintRevealedForCurrent)
        {
            var hintBtn = UiBuilder.GhostButton(
                "Show Hint",
                MaterialIconKind.LightbulbOutline,
                studio.RevealCurrentHint
            );
            hintBtn.Margin = new Thickness(0, 0, 6, 2);
            leftControls.Children.Add(hintBtn);
        }

        var confToggle = UiBuilder.GhostButton(
            studio.UseConfidenceCalibration ? "Confidence: On" : "Confidence Calibration",
            studio.UseConfidenceCalibration
                ? MaterialIconKind.GaugeFull
                : MaterialIconKind.GaugeEmpty,
            studio.ToggleConfidenceCalibration,
            isActive: studio.UseConfidenceCalibration
        );
        confToggle.Margin = new Thickness(0, 0, 6, 2);
        leftControls.Children.Add(confToggle);

        if (studio.UseConfidenceCalibration)
        {
            foreach (
                var (lvl, lbl) in new[]
                {
                    (ConfidenceLevel.Guess, "Guess"),
                    (ConfidenceLevel.Unsure, "Unsure"),
                    (ConfidenceLevel.Certain, "Certain"),
                }
            )
            {
                var captured = lvl;
                var cBtn = UiBuilder.GhostButton(
                    lbl,
                    studio.SelectedConfidence == captured ? MaterialIconKind.Check : null,
                    () => studio.SetSelectedConfidence(captured),
                    isActive: studio.SelectedConfidence == captured
                );
                cBtn.Margin = new Thickness(0, 0, 4, 2);
                leftControls.Children.Add(cBtn);
            }
        }

        stack.Children.Add(leftControls);

        if (hasHint && studio.IsHintRevealedForCurrent)
        {
            var hintRow = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
                ColumnSpacing = 8,
            };
            var bulb = UiBuilder.Icon(
                MaterialIconKind.LightbulbOnOutline,
                15,
                ThemeService.Brush(ThemeService.PriorityP2Fg)
            );
            bulb.VerticalAlignment = VerticalAlignment.Top;
            bulb.Margin = new Thickness(0, 2, 0, 0);
            hintRow.Children.Add(bulb);

            var hintText = new TextBlock
            {
                Text = $"Hint: {q.Hint}",
                FontSize = 12.5,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(hintText, 1);
            hintRow.Children.Add(hintText);

            stack.Children.Add(
                new Border
                {
                    Background = ThemeService.Brush(ThemeService.PriorityP2Bg),
                    BorderBrush = ThemeService.Brush(ThemeService.PriorityP2Fg),
                    BorderThickness = new Thickness(2.5, 0, 0, 0),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10, 7),
                    Child = hintRow,
                }
            );
        }

        return stack;
    }

    /// <summary>
    /// Renders structured contrastive explanations ("Why correct:", "Why distractors fail:", "Core principle:")
    /// cleanly as themed scannable cards across Quiz Runner, Results Summary, and Deck Organizer.
    /// </summary>
    private static Control BuildContrastiveExplanationView(
        string explanation,
        double fontSize = 12.8
    )
    {
        var matches = ContrastiveSplitRegex.Matches(explanation);
        if (matches.Count < 2)
        {
            return new TextBlock
            {
                Text = explanation,
                FontSize = fontSize,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                TextWrapping = TextWrapping.Wrap,
            };
        }

        var stack = new StackPanel { Spacing = 6 };
        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            int contentStart = m.Index + m.Length;
            int contentEnd = i + 1 < matches.Count ? matches[i + 1].Index : explanation.Length;
            var heading = m.Value.Trim().TrimEnd(':');
            var body = explanation[contentStart..contentEnd].Trim();
            if (string.IsNullOrWhiteSpace(body))
                continue;

            bool isWhyCorrect = heading.Contains("correct", StringComparison.OrdinalIgnoreCase);
            bool isDistractor = heading.Contains("distractor", StringComparison.OrdinalIgnoreCase);

            var labelBrush =
                isWhyCorrect ? ThemeService.Brush(ThemeService.SuccessFg)
                : isDistractor ? ThemeService.Brush(ThemeService.PriorityP2Fg)
                : ThemeService.Brush(ThemeService.AccentPrimary);

            var itemStack = new StackPanel { Spacing = 2 };
            itemStack.Children.Add(
                new TextBlock
                {
                    Text = heading.ToUpperInvariant(),
                    FontSize = 10.5,
                    FontWeight = FontWeight.Bold,
                    Foreground = labelBrush,
                }
            );
            itemStack.Children.Add(
                new TextBlock
                {
                    Text = body,
                    FontSize = fontSize,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    TextWrapping = TextWrapping.Wrap,
                }
            );
            stack.Children.Add(itemStack);
        }

        return stack;
    }

    /// <summary>
    /// Themed quality-critic diagnostic callout inside Deck Organizer question cards with 1-click Fix with AI.
    /// </summary>
    private static Border BuildOrganizerCriticCallout(
        QuizStudioViewModel studio,
        QuizQuestionRecord q,
        IReadOnlyList<QuestionQualityIssue> issues
    )
    {
        bool hasError = issues.Any(i => i.Severity == QualityIssueSeverity.Error);
        var fg = hasError
            ? ThemeService.Brush(ThemeService.PriorityP1Fg)
            : ThemeService.Brush(ThemeService.PriorityP2Fg);
        var bg = hasError
            ? ThemeService.Brush(ThemeService.PriorityP1Bg)
            : ThemeService.Brush(ThemeService.PriorityP2Bg);

        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
            ColumnSpacing = 8,
        };

        var icon = UiBuilder.Icon(MaterialIconKind.AlertOutline, 14, fg);
        icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);

        var msgText = new TextBlock
        {
            Text = string.Join("  ·  ", issues.Select(i => $"{i.Code}: {i.Message}")),
            FontSize = 11.5,
            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(msgText, 1);
        grid.Children.Add(msgText);

        var fixBtn = UiBuilder.GhostButton(
            "Fix with AI",
            MaterialIconKind.AutoFix,
            () =>
            {
                studio.FlagProblemDescription = string.Join(
                    "; ",
                    issues.Select(i => $"{i.Code}: {i.Message}")
                );
                studio.ToggleFlagQuestionForAiFix(q.Id);
            },
            isActive: string.Equals(
                studio.FlaggingQuestionId,
                q.Id,
                StringComparison.OrdinalIgnoreCase
            )
        );
        Grid.SetColumn(fixBtn, 2);
        grid.Children.Add(fixBtn);

        return new Border
        {
            Background = bg,
            BorderBrush = fg,
            BorderThickness = new Thickness(2.5, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 6),
            Child = grid,
        };
    }
}
