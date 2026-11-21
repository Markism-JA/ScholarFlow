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
    private static Grid BuildSplitBuilderAndPreviewView(
        MainWindowViewModel vm,
        QuizStudioViewModel studio
    )
    {
        var splitGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("1.1*,14,0.9*") };

        // LEFT COLUMN: Scope, Outline Tree Picker, Mode & Question Count
        var leftStack = new StackPanel { Spacing = 12, Margin = new Thickness(0, 0, 4, 20) };

        // 1. Source Material Bar (Master PDF default + optional Individual Handout switcher)
        var sourceCard = new StackPanel { Spacing = 8 };
        var sourceHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        sourceHeader.Children.Add(
            UiBuilder.SectionHeader("1. Material Source (Defaults to Master PDF)")
        );

        var masterPdfs = studio.AvailablePdfs.Where(p => p.IsMaster).ToList();
        var handoutPdfs = studio.AvailablePdfs.Where(p => !p.IsMaster).ToList();

        if (handoutPdfs.Count > 0)
        {
            var toggleHandoutsBtn = UiBuilder.GhostButton(
                studio.ShowIndividualHandouts ? "Hide Handouts" : $"Handouts ({handoutPdfs.Count})",
                studio.ShowIndividualHandouts
                    ? MaterialIconKind.ChevronUp
                    : MaterialIconKind.ChevronDown,
                studio.ToggleShowIndividualHandouts,
                isActive: studio.ShowIndividualHandouts
            );
            Grid.SetColumn(toggleHandoutsBtn, 1);
            sourceHeader.Children.Add(toggleHandoutsBtn);
        }
        sourceCard.Children.Add(sourceHeader);

        var pdfPills = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, fullPath, _) in masterPdfs)
        {
            var captured = fullPath;
            bool active = string.Equals(
                studio.SelectedPdfPath,
                captured,
                StringComparison.OrdinalIgnoreCase
            );
            var btn = UiBuilder.GhostButton(
                label,
                MaterialIconKind.BookEducationOutline,
                () => studio.SelectPdfScope(captured),
                isActive: active
            );
            btn.Margin = new Thickness(0, 0, 6, 4);
            pdfPills.Children.Add(btn);
        }

        if (studio.ShowIndividualHandouts || masterPdfs.Count == 0)
        {
            foreach (var (label, fullPath, _) in handoutPdfs)
            {
                var captured = fullPath;
                bool active = string.Equals(
                    studio.SelectedPdfPath,
                    captured,
                    StringComparison.OrdinalIgnoreCase
                );
                var btn = UiBuilder.GhostButton(
                    label,
                    MaterialIconKind.FilePdfBox,
                    () => studio.SelectPdfScope(captured),
                    isActive: active
                );
                btn.Margin = new Thickness(0, 0, 5, 4);
                pdfPills.Children.Add(btn);
            }
        }
        sourceCard.Children.Add(pdfPills);
        leftStack.Children.Add(UiBuilder.Card(sourceCard, padding: new Thickness(12, 10)));
        leftStack.Children.Add(BuildFileSourcesCard(studio));

        // 2. Academic Term, Custom Page Range & Outline Tree Picker
        var scopeCard = new StackPanel { Spacing = 8 };
        var scopeHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        scopeHeader.Children.Add(
            UiBuilder.SectionHeader(
                $"2. Term, Page Range & Section Picker ({studio.ScopePreviewPageCount} pages in scope)"
            )
        );

        if (
            studio.StartPage.HasValue
            || studio.EndPage.HasValue
            || studio.SelectedSectionKeys.Count > 0
            || !string.IsNullOrEmpty(studio.SelectedTerm)
        )
        {
            var resetBtn = UiBuilder.GhostButton(
                "Reset Scope",
                MaterialIconKind.Refresh,
                studio.ClearScopeFilters
            );
            Grid.SetColumn(resetBtn, 1);
            scopeHeader.Children.Add(resetBtn);
        }
        scopeCard.Children.Add(scopeHeader);

        // Term + Page Range Row
        var termRangeWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        if (vm.ActiveTerms.Count > 0)
        {
            var allTermBtn = UiBuilder.GhostButton(
                "All Terms",
                null,
                () => studio.SelectTermScope(null),
                isActive: string.IsNullOrEmpty(studio.SelectedTerm)
            );
            allTermBtn.Margin = new Thickness(0, 0, 4, 4);
            termRangeWrap.Children.Add(allTermBtn);

            foreach (var termName in vm.ActiveTerms)
            {
                var capturedTerm = termName;
                bool isTermActive = string.Equals(
                    studio.SelectedTerm,
                    capturedTerm,
                    StringComparison.OrdinalIgnoreCase
                );
                var tBtn = UiBuilder.GhostButton(
                    capturedTerm,
                    null,
                    () => studio.SelectTermScope(capturedTerm),
                    isActive: isTermActive
                );
                tBtn.Margin = new Thickness(0, 0, 4, 4);
                termRangeWrap.Children.Add(tBtn);
            }
        }

        var startPageBox = new TextBox
        {
            Text = studio.StartPage?.ToString() ?? string.Empty,
            Watermark = "From pg",
            MinWidth = 64,
            MaxWidth = 88,
            FontSize = 12,
            Margin = new Thickness(6, 0, 4, 4),
        };
        var endPageBox = new TextBox
        {
            Text = studio.EndPage?.ToString() ?? string.Empty,
            Watermark = "To pg",
            MinWidth = 64,
            MaxWidth = 88,
            FontSize = 12,
            Margin = new Thickness(0, 0, 4, 4),
        };

        void ApplyPageInputs()
        {
            int? s = int.TryParse(startPageBox.Text?.Trim(), out var sv) && sv > 0 ? sv : null;
            int? e = int.TryParse(endPageBox.Text?.Trim(), out var ev) && ev > 0 ? ev : null;
            studio.SetPageRange(s, e);
        }

        startPageBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ApplyPageInputs();
            }
        };
        endPageBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ApplyPageInputs();
            }
        };

        var applyPagesBtn = UiBuilder.GhostButton(
            "Set Pages",
            MaterialIconKind.Check,
            ApplyPageInputs
        );
        applyPagesBtn.Margin = new Thickness(0, 0, 4, 4);

        termRangeWrap.Children.Add(startPageBox);
        termRangeWrap.Children.Add(endPageBox);
        termRangeWrap.Children.Add(applyPagesBtn);
        scopeCard.Children.Add(termRangeWrap);

        // Hierarchical Outline Section Tree Picker (supports Mother -> Subsections + tri-state selection)
        static bool NodeMatchesTermFilter(PdfOutlineTreeNode node, string? selectedTerm)
        {
            if (string.IsNullOrWhiteSpace(selectedTerm))
                return true;
            if (
                string.IsNullOrWhiteSpace(node.Section.Term)
                || string.Equals(node.Section.Term, selectedTerm, StringComparison.OrdinalIgnoreCase)
            )
                return true;
            return node.Children.Any(c => NodeMatchesTermFilter(c, selectedTerm));
        }

        var visibleRoots = studio
            .OutlineSectionRoots.Where(r => NodeMatchesTermFilter(r, studio.SelectedTerm))
            .ToList();

        if (visibleRoots.Count > 0)
        {
            var treeListStack = new StackPanel { Spacing = 1 };

            void AddOutlineNodeRows(PdfOutlineTreeNode node, int depth)
            {
                if (!NodeMatchesTermFilter(node, studio.SelectedTerm))
                    return;

                var capturedNode = node;
                var capturedSec = node.Section;
                var selState = studio.GetOutlineNodeSelectionState(capturedNode);
                bool isSelected = selState != OutlineSelectionState.Unchecked;
                bool isCurrentlyPreviewed =
                    studio.CurrentPreviewPdfPage >= capturedSec.StartPage
                    && studio.CurrentPreviewPdfPage <= capturedSec.EndPage;

                var secRow = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("Auto,Auto,*,Auto,Auto"),
                    ColumnSpacing = 4,
                    Margin = new Thickness(depth * 14, 0, 0, 0),
                };

                if (capturedNode.Children.Count > 0)
                {
                    var expandBtn = UiBuilder.IconButton(
                        capturedNode.IsExpanded
                            ? MaterialIconKind.ChevronDown
                            : MaterialIconKind.ChevronRight,
                        () => studio.ToggleOutlineNodeExpanded(capturedNode),
                        14,
                        ThemeService.Brush(ThemeService.TextSecondary)
                    );
                    secRow.Children.Add(expandBtn);
                }
                else
                {
                    secRow.Children.Add(new Border { Width = 22 });
                }

                var chkIcon = selState switch
                {
                    OutlineSelectionState.Checked => MaterialIconKind.CheckboxMarked,
                    OutlineSelectionState.Indeterminate => MaterialIconKind.MinusBoxOutline,
                    _ => MaterialIconKind.CheckboxBlankOutline,
                };
                var chkBtn = UiBuilder.IconButton(
                    chkIcon,
                    () => studio.ToggleOutlineNodeSelection(capturedNode),
                    15,
                    isSelected
                        ? ThemeService.Brush(ThemeService.AccentPrimary)
                        : ThemeService.Brush(ThemeService.TextMuted)
                );
                Grid.SetColumn(chkBtn, 1);
                secRow.Children.Add(chkBtn);

                var secTitleBtn = new Button
                {
                    Content = new TextBlock
                    {
                        Text = capturedSec.Title,
                        FontSize = 12.5,
                        FontWeight =
                            isSelected || isCurrentlyPreviewed
                                ? FontWeight.SemiBold
                                : FontWeight.Normal,
                        Foreground =
                            selState == OutlineSelectionState.Checked
                                ? ThemeService.Brush(ThemeService.AccentPrimary)
                            : isCurrentlyPreviewed ? ThemeService.Brush(ThemeService.TextPrimary)
                            : ThemeService.Brush(ThemeService.TextSecondary),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(2, 4),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                secTitleBtn.Click += (_, _) => studio.ToggleOutlineNodeSelection(capturedNode);
                Grid.SetColumn(secTitleBtn, 2);
                secRow.Children.Add(secTitleBtn);

                if (!string.IsNullOrWhiteSpace(capturedSec.Term))
                {
                    var termBadge = UiBuilder.Pill(
                        capturedSec.Term,
                        ThemeService.Brush(ThemeService.TextMuted),
                        ThemeService.Brush(ThemeService.BgElevated),
                        fontSize: 10
                    );
                    Grid.SetColumn(termBadge, 3);
                    secRow.Children.Add(termBadge);
                }

                var previewSecBtn = UiBuilder.GhostButton(
                    $"p.{capturedSec.StartPage}–{capturedSec.EndPage}",
                    MaterialIconKind.EyeOutline,
                    () => studio.JumpPreviewToSection(capturedSec),
                    isActive: isCurrentlyPreviewed
                );
                Grid.SetColumn(previewSecBtn, 4);
                secRow.Children.Add(previewSecBtn);

                treeListStack.Children.Add(
                    new Border
                    {
                        Background = isCurrentlyPreviewed
                            ? ThemeService.Brush(ThemeService.BgElevated)
                            : Brushes.Transparent,
                        BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(4, 2),
                        Child = secRow,
                    }
                );

                if (capturedNode.IsExpanded)
                {
                    foreach (var child in capturedNode.Children)
                        AddOutlineNodeRows(child, depth + 1);
                }
            }

            foreach (var root in visibleRoots)
                AddOutlineNodeRows(root, depth: 0);

            scopeCard.Children.Add(
                new Border
                {
                    Background = ThemeService.Brush(ThemeService.BgBase),
                    BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(6, 4),
                    MaxHeight = 215,
                    Child = UiBuilder.PersistentScrollViewer(
                        $"quiz-outline:{studio.SelectedPdfPath}",
                        treeListStack
                    ),
                }
            );
        }

        if (studio.IsMixedSourceMode)
        {
            leftStack.Children.Add(UiBuilder.Card(scopeCard, padding: new Thickness(12, 10)));
            leftStack.Children.Add(BuildFileModeScopeNote(studio));
        }
        else
        {
            leftStack.Children.Add(
                studio.IsFileSourceMode
                    ? BuildFileModeScopeNote(studio)
                    : UiBuilder.Card(scopeCard, padding: new Thickness(12, 10))
            );
        }

        // 3. Assessment Mode, Custom Question-Type Distribution & Output Engine
        var configCard = new StackPanel { Spacing = 10 };
        configCard.Children.Add(
            UiBuilder.SectionHeader("3. Question-Type Distribution, Item Count & Generator Mode")
        );

        if (!string.IsNullOrWhiteSpace(studio.AppendTargetQuizId))
        {
            var appendBanner = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                ColumnSpacing = 8,
            };
            appendBanner.Children.Add(
                new TextBlock
                {
                    Text =
                        $"Appending new non-duplicate questions into: {studio.AppendTargetQuizTitle}",
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );
            var cancelAppendBtn = UiBuilder.GhostButton(
                "Create New Deck Instead",
                MaterialIconKind.Close,
                studio.ClearAppendTarget
            );
            Grid.SetColumn(cancelAppendBtn, 1);
            appendBanner.Children.Add(cancelAppendBtn);

            configCard.Children.Add(
                new Border
                {
                    Background = ThemeService.Brush(ThemeService.AccentSoftBg),
                    BorderBrush = ThemeService.Brush(ThemeService.AccentPrimary),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 6),
                    Child = appendBanner,
                }
            );
        }

        // Preset Templates Row
        var modeWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (
            var (preset, label, icon) in new[]
            {
                (
                    QuizPresetMode.ExamSimulator,
                    "STI Exam Simulator",
                    MaterialIconKind.ClipboardTextOutline
                ),
                (QuizPresetMode.QuickReview, "Quick Concept Review", MaterialIconKind.FlashOutline),
                (QuizPresetMode.WeakTopicDrill, "Weak-Topic Drill", MaterialIconKind.Target),
                (QuizPresetMode.Custom, "Custom Mix", MaterialIconKind.TuneVariant),
            }
        )
        {
            var capturedPreset = preset;
            var pBtn = UiBuilder.GhostButton(
                label,
                icon,
                () => studio.SetPreset(capturedPreset),
                isActive: studio.SelectedPreset == capturedPreset
            );
            pBtn.Margin = new Thickness(0, 0, 6, 4);
            modeWrap.Children.Add(pBtn);
        }
        configCard.Children.Add(modeWrap);

        // Total Item Count + Allocation Mode Switcher (Weights % vs Exact Counts #)
        var countAndModeHeader = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 8,
        };
        var countWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (int cnt in new[] { 10, 20, 30, 50, 60, 100 })
        {
            int capturedCnt = cnt;
            var cBtn = UiBuilder.GhostButton(
                $"{cnt}",
                null,
                () => studio.SetQuestionCount(capturedCnt),
                isActive: studio.QuestionCount == capturedCnt
            );
            cBtn.Margin = new Thickness(0, 0, 4, 4);
            countWrap.Children.Add(cBtn);
        }

        var customCountBox = new TextBox
        {
            Text = studio.QuestionCount.ToString(),
            Watermark = "Items (1-150)",
            Width = 78,
            FontSize = 12,
            Margin = new Thickness(4, 0, 4, 4),
        };
        customCountBox.PropertyChanged += (_, e) =>
        {
            if (
                e.Property == TextBox.TextProperty
                && int.TryParse(customCountBox.Text?.Trim(), out var parsed)
                && parsed is >= 1 and <= 150
            )
            {
                studio.SetQuestionCount(parsed, notify: false);
            }
        };
        customCountBox.KeyDown += (_, e) =>
        {
            if (
                e.Key == Key.Enter
                && int.TryParse(customCountBox.Text?.Trim(), out var parsed)
                && parsed > 0
            )
            {
                e.Handled = true;
                studio.SetQuestionCount(parsed, notify: true);
            }
        };
        countWrap.Children.Add(customCountBox);
        countAndModeHeader.Children.Add(countWrap);

        var allocModeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        allocModeRow.Children.Add(
            UiBuilder.GhostButton(
                "Weights (%)",
                MaterialIconKind.PercentOutline,
                () => studio.SetAllocationMode(QuestionAllocationMode.Percentages),
                isActive: studio.AllocationMode == QuestionAllocationMode.Percentages
            )
        );
        allocModeRow.Children.Add(
            UiBuilder.GhostButton(
                "Exact Counts (#)",
                MaterialIconKind.Pound,
                () => studio.SetAllocationMode(QuestionAllocationMode.ExactCounts),
                isActive: studio.AllocationMode == QuestionAllocationMode.ExactCounts
            )
        );
        Grid.SetColumn(allocModeRow, 1);
        countAndModeHeader.Children.Add(allocModeRow);
        configCard.Children.Add(countAndModeHeader);

        // Per-Question-Kind Interactive Allocation Grid
        var resolvedKindCounts = studio.GetResolvedExactKindCounts();
        var kindsGrid = new StackPanel { Spacing = 4 };
        foreach (
            var (kind, title, desc) in new[]
            {
                (
                    QuizQuestionKind.MultipleChoice,
                    "Multiple Choice",
                    "4 options with plausible sibling distractors"
                ),
                (
                    QuizQuestionKind.Situational,
                    "Situational / Case",
                    "Applied troubleshooting & scenario analysis"
                ),
                (
                    QuizQuestionKind.Identification,
                    "Identification",
                    "Direct canonical term or concept recall"
                ),
                (
                    QuizQuestionKind.FillInTheBlank,
                    "Fill in the Blank",
                    "Contextual statement with _____ missing keyword"
                ),
                (
                    QuizQuestionKind.TrueFalse,
                    "True / False",
                    "Claim verification against handout facts"
                ),
                (
                    QuizQuestionKind.Ordering,
                    "Step Ordering",
                    "Chronological / procedural sequence arrangement"
                ),
            }
        )
        {
            var capturedKind = kind;
            int rawVal = studio.KindAllocations.GetValueOrDefault(kind, 0);
            int exactItems = resolvedKindCounts.GetValueOrDefault(kind, 0);
            bool isEnabled = exactItems > 0;

            var row = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"),
                ColumnSpacing = 10,
            };

            var labelStack = new StackPanel
            {
                Spacing = 1,
                VerticalAlignment = VerticalAlignment.Center,
            };
            labelStack.Children.Add(
                new TextBlock
                {
                    Text = title,
                    FontSize = 12.5,
                    FontWeight = isEnabled ? FontWeight.SemiBold : FontWeight.Normal,
                    Foreground = isEnabled
                        ? ThemeService.Brush(ThemeService.TextPrimary)
                        : ThemeService.Brush(ThemeService.TextMuted),
                    TextWrapping = TextWrapping.Wrap,
                }
            );
            labelStack.Children.Add(
                new TextBlock
                {
                    Text = desc,
                    FontSize = 11,
                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    TextWrapping = TextWrapping.Wrap,
                }
            );
            row.Children.Add(labelStack);

            var stepperGroup = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var minusBtn = UiBuilder.IconButton(
                MaterialIconKind.Minus,
                () => studio.StepKindAllocation(capturedKind, -1),
                13.5,
                ThemeService.Brush(ThemeService.TextSecondary)
            );
            minusBtn.VerticalAlignment = VerticalAlignment.Center;
            stepperGroup.Children.Add(minusBtn);

            var valBox = new TextBox
            {
                Text = rawVal.ToString(),
                MinWidth = 54,
                MaxWidth = 76,
                FontSize = 12,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            valBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && int.TryParse(valBox.Text?.Trim(), out int v))
                {
                    e.Handled = true;
                    studio.SetKindAllocation(capturedKind, v, notify: true);
                }
            };
            valBox.LostFocus += (_, _) =>
            {
                if (
                    int.TryParse(valBox.Text?.Trim(), out int v)
                    && studio.KindAllocations.GetValueOrDefault(capturedKind, -1) != v
                )
                {
                    studio.SetKindAllocation(capturedKind, v, notify: true);
                }
            };
            stepperGroup.Children.Add(valBox);

            var plusBtn = UiBuilder.IconButton(
                MaterialIconKind.Plus,
                () => studio.StepKindAllocation(capturedKind, +1),
                13.5,
                ThemeService.Brush(ThemeService.TextSecondary)
            );
            plusBtn.VerticalAlignment = VerticalAlignment.Center;
            stepperGroup.Children.Add(plusBtn);

            Grid.SetColumn(stepperGroup, 1);
            row.Children.Add(stepperGroup);

            var resolvedBadge = UiBuilder.PillBadge(
                studio.AllocationMode == QuestionAllocationMode.Percentages
                    ? $"{rawVal}% -> {exactItems} items"
                    : $"{exactItems} item(s)",
                isEnabled
                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : ThemeService.Brush(ThemeService.TextMuted),
                isEnabled
                    ? ThemeService.Brush(ThemeService.AccentSoftBg)
                    : ThemeService.Brush(ThemeService.BgSurface)
            );
            resolvedBadge.HorizontalAlignment = HorizontalAlignment.Right;
            resolvedBadge.VerticalAlignment = VerticalAlignment.Center;

            var badgeSlot = new Border
            {
                MinWidth = 114,
                VerticalAlignment = VerticalAlignment.Center,
                Child = resolvedBadge,
            };
            Grid.SetColumn(badgeSlot, 2);
            row.Children.Add(badgeSlot);

            kindsGrid.Children.Add(
                new Border
                {
                    Background = isEnabled
                        ? ThemeService.Brush(ThemeService.BgElevated)
                        : Brushes.Transparent,
                    BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 4),
                    Child = row,
                }
            );
        }

        configCard.Children.Add(
            new Border
            {
                Background = ThemeService.Brush(ThemeService.BgBase),
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4, 2),
                Child = kindsGrid,
            }
        );

        // Specialized Content Modality Mode (Auto-Detect vs Prose-Only vs Code / Math LaTeX / Diagrams)
        var modalityStack = new StackPanel { Spacing = 5 };
        modalityStack.Children.Add(
            new TextBlock
            {
                Text =
                    "Specialized Content Formats (Auto-detects whether material uses Code, Math LaTeX, or Diagrams):",
                FontSize = 11.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
            }
        );

        var modalityWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (
            var (mMode, mLabel, mIcon) in new[]
            {
                (QuizContentModalityMode.AutoDetect, "Smart Auto-Detect", MaterialIconKind.AutoFix),
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
                    "Math LaTeX (Strict MCQ)",
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
        modalityStack.Children.Add(modalityWrap);
        configCard.Children.Add(modalityStack);

        // Guarded Custom Prompt Instructions (Focus / Tone / Difficulty — schema & evidence locked)
        var customPromptStack = new StackPanel { Spacing = 6 };
        var customPromptHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        customPromptHeader.Children.Add(
            new TextBlock
            {
                Text =
                    "Custom Focus & Style Directives (Optional — JSON schema & PDF evidence rules stay locked)",
                FontSize = 11.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        if (!string.IsNullOrWhiteSpace(studio.CustomPromptInstructions))
        {
            var clearDirBtn = UiBuilder.GhostButton(
                "Clear",
                MaterialIconKind.Close,
                () => studio.SetCustomPromptInstructions(string.Empty, notify: true)
            );
            Grid.SetColumn(clearDirBtn, 1);
            customPromptHeader.Children.Add(clearDirBtn);
        }
        customPromptStack.Children.Add(customPromptHeader);

        var directiveChips = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (
            var (chipLabel, chipText) in new[]
            {
                (
                    "+ Edge Cases",
                    "Focus on edge cases, common pitfalls, and troubleshooting scenarios."
                ),
                ("+ Hard Difficulty", "Target difficulty 4-5 analysis and application questions."),
                (
                    "+ Concise Stems",
                    "Keep question stems concise (1-2 sentences) and distractors subtle."
                ),
                (
                    "+ Definitions & Acronyms",
                    "Emphasize exact technical definitions, acronyms, and comparisons."
                ),
            }
        )
        {
            var capturedText = chipText;
            var chipBtn = UiBuilder.GhostButton(
                chipLabel,
                null,
                () => studio.AppendCustomPromptDirective(capturedText)
            );
            chipBtn.Margin = new Thickness(0, 0, 5, 4);
            directiveChips.Children.Add(chipBtn);
        }
        customPromptStack.Children.Add(directiveChips);

        var customPromptBox = new TextBox
        {
            Text = studio.CustomPromptInstructions,
            Watermark =
                "e.g. Focus on TCP congestion control vs flow control, avoid trivial date questions... (max 1200 chars)",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 52,
            MaxHeight = 110,
            FontSize = 12,
        };
        customPromptBox.LostFocus += (_, _) =>
        {
            studio.SetCustomPromptInstructions(customPromptBox.Text, notify: false);
        };
        customPromptBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                studio.SetCustomPromptInstructions(customPromptBox.Text, notify: true);
            }
        };
        customPromptStack.Children.Add(customPromptBox);
        configCard.Children.Add(customPromptStack);

        // Unified Output Mode Bar (AI API vs Auto-Synced Web JSON vs Offline Smart PDF)
        var outputModeBar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 0),
        };
        foreach (
            var (outMode, label, icon) in new[]
            {
                (QuizBuilderOutputMode.AiApi, "AI Engine API", MaterialIconKind.AutoFix),
                (
                    QuizBuilderOutputMode.WebJson,
                    "Web AI JSON (Auto-Synced Prompt)",
                    MaterialIconKind.CodeJson
                ),
                (
                    QuizBuilderOutputMode.OfflineSmart,
                    "Instant Offline PDF Analyzer",
                    MaterialIconKind.LightningBoltOutline
                ),
            }
        )
        {
            var capturedOut = outMode;
            var btn = UiBuilder.GhostButton(
                label,
                icon,
                () => studio.SetBuilderOutputMode(capturedOut),
                isActive: studio.BuilderOutputMode == capturedOut
            );
            btn.Margin = new Thickness(0, 0, 6, 4);
            outputModeBar.Children.Add(btn);
        }
        configCard.Children.Add(outputModeBar);

        if (studio.BuilderOutputMode == QuizBuilderOutputMode.WebJson)
        {
            if (string.IsNullOrWhiteSpace(studio.GeneratedPromptPreview))
                studio.RefreshLivePromptPreviewIfActive(force: true);
            configCard.Children.Add(BuildManualJsonBridgeCard(studio));
        }
        else
        {
            bool isOfflineMode = studio.BuilderOutputMode == QuizBuilderOutputMode.OfflineSmart;
            var aiStatus = studio.GetActiveAiEngineStatus();

            if (!isOfflineMode)
            {
                var statusGrid = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
                    ColumnSpacing = 8,
                };

                var fullStatusText = aiStatus.IsConfigured
                    ? $"Active AI Engine: {aiStatus.DisplayLabel}"
                    : $"Setup Required ({aiStatus.DisplayLabel}): {aiStatus.MissingReason}";

                var statusIcon = UiBuilder.Icon(
                    aiStatus.IsConfigured
                        ? MaterialIconKind.RobotOutline
                        : MaterialIconKind.AlertCircleOutline,
                    15,
                    aiStatus.IsConfigured
                        ? ThemeService.Brush(ThemeService.AccentPrimary)
                        : ThemeService.Brush(ThemeService.PriorityP2Fg)
                );
                statusIcon.VerticalAlignment = VerticalAlignment.Center;
                statusGrid.Children.Add(statusIcon);

                var statusText = new TextBlock
                {
                    Text = fullStatusText,
                    FontSize = 12,
                    FontWeight = aiStatus.IsConfigured ? FontWeight.Medium : FontWeight.SemiBold,
                    Foreground = aiStatus.IsConfigured
                        ? ThemeService.Brush(ThemeService.TextSecondary)
                        : ThemeService.Brush(ThemeService.PriorityP2Fg),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 2,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MinWidth = 0,
                };
                Grid.SetColumn(statusText, 1);
                statusGrid.Children.Add(statusText);

                var cfgBtn = UiBuilder.GhostButton(
                    aiStatus.IsConfigured ? "Change" : "Configure",
                    MaterialIconKind.CogOutline,
                    studio.ToggleAiSettings,
                    isActive: !aiStatus.IsConfigured
                );
                cfgBtn.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(cfgBtn, 2);
                statusGrid.Children.Add(cfgBtn);

                configCard.Children.Add(
                    new Border
                    {
                        Background = aiStatus.IsConfigured
                            ? ThemeService.Brush(ThemeService.BgBase)
                            : ThemeService.Brush(ThemeService.PriorityP2Bg),
                        BorderBrush = aiStatus.IsConfigured
                            ? ThemeService.Brush(ThemeService.BorderSubtle)
                            : ThemeService.Brush(ThemeService.PriorityP2Fg),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(10, 6),
                        Child = statusGrid,
                    }
                );
            }

            var generateActions = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2, 0, 0),
            };
            bool canSubmit = isOfflineMode || aiStatus.IsConfigured;
            var genPrimaryBtn = UiBuilder.PrimaryButton(
                studio.IsGenerating ? "Generating Grounded Assessment..."
                    : isOfflineMode ? $"Generate {studio.QuestionCount}-Item Offline PDF Quiz"
                    : !aiStatus.IsConfigured ? "Configure AI Engine to Generate"
                    : $"Generate {studio.QuestionCount}-Item Grounded Exam",
                isOfflineMode ? MaterialIconKind.LightningBoltOutline
                    : !aiStatus.IsConfigured ? MaterialIconKind.CogOutline
                    : MaterialIconKind.AutoFix,
                async () => await studio.GenerateQuizNowAsync(forceOffline: isOfflineMode)
            );
            genPrimaryBtn.IsEnabled = !studio.IsGenerating && canSubmit;
            genPrimaryBtn.Margin = new Thickness(0, 0, 8, 4);
            generateActions.Children.Add(genPrimaryBtn);

            if (!isOfflineMode)
            {
                var genOfflineBtn = UiBuilder.GhostButton(
                    "Quick Offline Fallback",
                    MaterialIconKind.LightningBoltOutline,
                    async () => await studio.GenerateQuizNowAsync(forceOffline: true)
                );
                genOfflineBtn.Margin = new Thickness(0, 0, 8, 4);
                generateActions.Children.Add(genOfflineBtn);
            }

            configCard.Children.Add(generateActions);
        }

        leftStack.Children.Add(UiBuilder.Card(configCard, padding: new Thickness(12, 10)));

        // Also show quick recent quizzes for this subject right under the builder
        var subjectQuizzes = studio
            .SavedQuizzes.Where(q =>
                studio.SelectedCourse is null
                || string.Equals(
                    q.CourseFolder,
                    studio.SelectedCourse.FolderName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .Take(4)
            .ToList();
        if (subjectQuizzes.Count > 0)
        {
            var recentStack = new StackPanel { Spacing = 4 };
            var recentHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
            recentHeader.Children.Add(
                UiBuilder.SectionHeader(
                    $"Recent Assessments for {studio.SelectedCourse?.Alias.ToUpperInvariant() ?? "Subject"}"
                )
            );
            var openCatBtn = UiBuilder.GhostButton(
                $"Open Catalog & Combiner ({studio.SavedQuizzes.Count})",
                MaterialIconKind.ArrowRight,
                () => studio.SetSubTab(QuizStudioSubTab.Catalog)
            );
            Grid.SetColumn(openCatBtn, 1);
            recentHeader.Children.Add(openCatBtn);
            recentStack.Children.Add(recentHeader);

            foreach (var qz in subjectQuizzes)
                recentStack.Children.Add(
                    BuildHairlineQuizRow(vm, studio, qz, showCheckbox: false, compact: true)
                );

            leftStack.Children.Add(UiBuilder.Card(recentStack, padding: new Thickness(12, 8)));
        }

        splitGrid.Children.Add(UiBuilder.PersistentScrollViewer("quiz-builder-left", leftStack));

        // RIGHT COLUMN: LIVE VISUAL PDF SCOPE PREVIEWER / FILE SOURCES PREVIEWER
        var previewPane = studio.IsMixedSourceMode
            ? BuildMixedPreviewPane(vm, studio)
            : studio.IsFileSourceMode
                ? BuildFileSourcesPreviewPane(vm, studio)
                : BuildLivePdfPreviewPane(vm, studio);
        Grid.SetColumn(previewPane, 2);
        splitGrid.Children.Add(previewPane);

        return splitGrid;
    }
}
