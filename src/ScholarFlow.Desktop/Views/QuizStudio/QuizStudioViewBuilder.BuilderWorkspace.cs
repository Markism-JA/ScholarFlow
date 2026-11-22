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

        // 1. Unified Study Sources & Scope (PDFs + Course Files + Page/Section Scope Picker)
        var sourceAndScopeCard = new StackPanel { Spacing = 9 };
        var sourceHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        var scopeBadgeText =
            studio.IsMixedSourceMode
                ? $"{studio.ScopePreviewPageCount} PDF pages + {studio.SelectedFileSourcePaths.Count} file(s)"
            : studio.IsFileSourceMode ? $"{studio.SelectedFileSourcePaths.Count} file(s) selected"
            : $"{studio.ScopePreviewPageCount} pages in scope";

        sourceHeader.Children.Add(
            UiBuilder.SectionHeader($"1. Study Sources & Scope ({scopeBadgeText})")
        );

        var masterPdfs = studio.AvailablePdfs.Where(p => p.IsMaster).ToList();
        var handoutPdfs = studio.AvailablePdfs.Where(p => !p.IsMaster).ToList();

        var sourceRightActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
        };
        if (
            studio.StartPage.HasValue
            || studio.EndPage.HasValue
            || !string.IsNullOrWhiteSpace(studio.PageRangeExpression)
            || studio.SelectedSectionKeys.Count > 0
            || !string.IsNullOrEmpty(studio.SelectedTerm)
        )
        {
            sourceRightActions.Children.Add(
                UiBuilder.GhostButton(
                    "Reset Scope",
                    MaterialIconKind.Refresh,
                    studio.ClearScopeFilters
                )
            );
        }
        if (handoutPdfs.Count > 0)
        {
            sourceRightActions.Children.Add(
                UiBuilder.GhostButton(
                    studio.ShowIndividualHandouts
                        ? "Hide Handouts"
                        : $"Handouts ({handoutPdfs.Count})",
                    studio.ShowIndividualHandouts
                        ? MaterialIconKind.ChevronUp
                        : MaterialIconKind.ChevronDown,
                    studio.ToggleShowIndividualHandouts,
                    isActive: studio.ShowIndividualHandouts
                )
            );
        }
        Grid.SetColumn(sourceRightActions, 1);
        sourceHeader.Children.Add(sourceRightActions);
        sourceAndScopeCard.Children.Add(sourceHeader);

        var pdfPills = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, fullPath, _) in masterPdfs)
        {
            var captured = fullPath;
            bool active =
                (!studio.IsFileSourceMode || studio.IsMixedSourceMode)
                && string.Equals(
                    studio.SelectedPdfPath,
                    captured,
                    StringComparison.OrdinalIgnoreCase
                );
            var btn = UiBuilder.GhostButton(
                label,
                MaterialIconKind.BookEducationOutline,
                () =>
                {
                    if (studio.IsFileSourceMode && !studio.IsMixedSourceMode)
                        studio.ClearFileSources();
                    studio.SelectPdfScope(captured);
                },
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
                bool active =
                    (!studio.IsFileSourceMode || studio.IsMixedSourceMode)
                    && string.Equals(
                        studio.SelectedPdfPath,
                        captured,
                        StringComparison.OrdinalIgnoreCase
                    );
                var btn = UiBuilder.GhostButton(
                    label,
                    MaterialIconKind.FilePdfBox,
                    () =>
                    {
                        if (studio.IsFileSourceMode && !studio.IsMixedSourceMode)
                            studio.ClearFileSources();
                        studio.SelectPdfScope(captured);
                    },
                    isActive: active
                );
                btn.Margin = new Thickness(0, 0, 5, 4);
                pdfPills.Children.Add(btn);
            }
        }
        if (pdfPills.Children.Count > 0)
            sourceAndScopeCard.Children.Add(pdfPills);

        sourceAndScopeCard.Children.Add(BuildInlineFileSourcesSection(studio));

        // PDF Term, Page Range & Outline Tree Picker (shown whenever a PDF is in scope: PDF-only or PDF+Files)
        var scopeCard = new StackPanel { Spacing = 8 };

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

        var pageRangeBox = new TextBox
        {
            Text = studio.PageRangeExpression,
            Watermark = "Pages e.g. 1-15, 22, !5",
            MinWidth = 155,
            MaxWidth = 220,
            FontSize = 12,
            Margin = new Thickness(4, 0, 4, 4),
        };
        ToolTip.SetTip(
            pageRangeBox,
            "Enter pages or ranges (e.g. 1-15, 22, !4-6). Prefix ! to exclude pages."
        );

        void ApplyPageExpr() => studio.ApplyPageRangeExpression(pageRangeBox.Text);

        pageRangeBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ApplyPageExpr();
            }
        };

        var applyPagesBtn = UiBuilder.GhostButton(
            "Apply",
            MaterialIconKind.Check,
            ApplyPageExpr
        );
        ToolTip.SetTip(applyPagesBtn, "Apply page range expression to scope");
        applyPagesBtn.Margin = new Thickness(0, 0, 4, 4);

        var saveSectionBtn = UiBuilder.GhostButton(
            "Save Section",
            MaterialIconKind.BookmarkPlusOutline,
            studio.ToggleAddCustomSectionForm,
            isActive: studio.IsAddCustomSectionOpen
        );
        ToolTip.SetTip(
            saveSectionBtn,
            "Save current page selection as a reusable custom chapter for this PDF"
        );
        saveSectionBtn.Margin = new Thickness(0, 0, 4, 4);

        termRangeWrap.Children.Add(pageRangeBox);
        termRangeWrap.Children.Add(applyPagesBtn);
        if (studio.ScopePreviewPageCount > 0)
            termRangeWrap.Children.Add(saveSectionBtn);
        scopeCard.Children.Add(termRangeWrap);

        if (!string.IsNullOrWhiteSpace(studio.PageRangeError))
        {
            scopeCard.Children.Add(
                new TextBlock
                {
                    Text = studio.PageRangeError,
                    FontSize = 11,
                    Foreground = ThemeService.Brush(ThemeService.PriorityP1Fg),
                    TextWrapping = TextWrapping.Wrap,
                }
            );
        }

        if (studio.IsAddCustomSectionOpen)
        {
            var customSecRow = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"),
                ColumnSpacing = 6,
            };
            var nameBox = new TextBox
            {
                Text = studio.CustomSectionNameDraft,
                Watermark = "Section name (e.g. Ch 3: Divisibility)",
                FontSize = 12,
            };
            nameBox.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                    studio.CustomSectionNameDraft = nameBox.Text ?? string.Empty;
            };
            nameBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    studio.SaveCurrentScopeAsCustomSection(nameBox.Text);
                }
            };
            customSecRow.Children.Add(nameBox);

            var confirmSaveBtn = UiBuilder.PrimaryButton(
                "Save",
                MaterialIconKind.Check,
                () => studio.SaveCurrentScopeAsCustomSection(nameBox.Text)
            );
            Grid.SetColumn(confirmSaveBtn, 1);
            customSecRow.Children.Add(confirmSaveBtn);

            var cancelSaveBtn = UiBuilder.GhostButton(
                "Cancel",
                null,
                studio.ToggleAddCustomSectionForm
            );
            Grid.SetColumn(cancelSaveBtn, 2);
            customSecRow.Children.Add(cancelSaveBtn);

            scopeCard.Children.Add(
                new Border
                {
                    Background = ThemeService.Brush(ThemeService.BgBase),
                    BorderBrush = ThemeService.Brush(ThemeService.AccentPrimary),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 6),
                    Child = customSecRow,
                }
            );
        }

        // Hierarchical Outline Section Tree Picker (supports Mother -> Subsections + tri-state selection)
        static bool NodeMatchesTermFilter(PdfOutlineTreeNode node, string? selectedTerm)
        {
            if (string.IsNullOrWhiteSpace(selectedTerm))
                return true;
            if (
                string.IsNullOrWhiteSpace(node.Section.Term)
                || string.Equals(
                    node.Section.Term,
                    selectedTerm,
                    StringComparison.OrdinalIgnoreCase
                )
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
                    ColumnDefinitions = ColumnDefinitions.Parse("Auto,Auto,*,Auto,Auto,Auto"),
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

                var customId = studio.GetCustomSectionIdForOutlineSection(capturedSec);
                if (!string.IsNullOrWhiteSpace(customId))
                {
                    var capturedCustomId = customId;
                    var delBtn = UiBuilder.IconButton(
                        MaterialIconKind.DeleteOutline,
                        () => studio.DeleteCustomSection(capturedCustomId),
                        14,
                        ThemeService.Brush(ThemeService.TextMuted)
                    );
                    Grid.SetColumn(delBtn, 5);
                    secRow.Children.Add(delBtn);
                }

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
        else if (studio.IsInspectingPdf)
        {
            scopeCard.Children.Add(BuildOutlineSkeleton());
        }
        else if (studio.PdfTotalPages > 0)
        {
            scopeCard.Children.Add(
                new Border
                {
                    Background = ThemeService.Brush(ThemeService.BgBase),
                    BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(9, 7),
                    Child = new TextBlock
                    {
                        Text =
                            "No embedded TOC in this PDF — toggle individual pages below, type ranges (e.g. 1-15, !4), open Page Studio, or Save Section to build reusable chapters.",
                        FontSize = 11.5,
                        Foreground = ThemeService.Brush(ThemeService.TextMuted),
                        TextWrapping = TextWrapping.Wrap,
                    },
                }
            );
        }

        if (studio.PdfTotalPages > 0)
        {
            scopeCard.Children.Add(BuildCollapsiblePageTocPanel(studio, isFullHeight: false));
        }

        if (!studio.IsFileSourceMode || studio.IsMixedSourceMode)
        {
            sourceAndScopeCard.Children.Add(scopeCard);
        }
        leftStack.Children.Add(UiBuilder.Card(sourceAndScopeCard, padding: new Thickness(12, 10)));

        // 2. Unified Assessment Blueprint & Generator Engine
        var configCard = new StackPanel { Spacing = 10 };
        configCard.Children.Add(UiBuilder.SectionHeader("2. Assessment Blueprint & Generator"));

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

        // Unified Preset Templates Row (configures Question Types, Cognitive Depth & Focus together)
        var modeWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (
            var (preset, label, icon) in new[]
            {
                (
                    QuizPresetMode.ExamSimulator,
                    "Exam Simulator",
                    MaterialIconKind.ClipboardTextOutline
                ),
                (QuizPresetMode.QuickReview, "Quick Review", MaterialIconKind.FlashOutline),
                (QuizPresetMode.WeakTopicDrill, "Weak-Topic Drill", MaterialIconKind.Target),
                (QuizPresetMode.DeepMastery, "Deep Mastery", MaterialIconKind.Brain),
                (QuizPresetMode.CodeLab, "Code Lab", MaterialIconKind.CodeBraces),
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

        // Interactive Cognitive Depth Distribution (Recall / Comprehension / Application / Analysis)
        configCard.Children.Add(BuildCognitiveDepthAllocationSection(studio));

        // Unified Content Modality, Question Archetypes & Active Recall Options
        configCard.Children.Add(BuildIntegratedModalityAndArchetypesSection(studio));

        // Optional Custom Focus Directive (compact single box — quick style chips are now first-class toggles above)
        var customPromptStack = new StackPanel { Spacing = 4 };
        var customPromptHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        customPromptHeader.Children.Add(
            new TextBlock
            {
                Text = "Custom Topic / Focus Note (Optional)",
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

        var customPromptBox = new TextBox
        {
            Text = studio.CustomPromptInstructions,
            Watermark =
                "e.g. Emphasize TCP congestion control vs flow control, skip historical dates... (max 1200 chars)",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 40,
            MaxHeight = 84,
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
            if (
                string.IsNullOrWhiteSpace(studio.GeneratedPromptPreview) && !studio.IsBuildingPrompt
            )
                studio.RefreshLivePromptPreviewIfActive(force: true);
            configCard.Children.Add(BuildManualJsonBridgeCard(studio));
        }
        else
        {
            bool isOfflineMode = studio.BuilderOutputMode == QuizBuilderOutputMode.OfflineSmart;
            var (DisplayLabel, IsConfigured, MissingReason) = studio.GetActiveAiEngineStatus();

            if (!isOfflineMode)
            {
                var statusGrid = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
                    ColumnSpacing = 8,
                };

                var fullStatusText = IsConfigured
                    ? $"Active AI Engine: {DisplayLabel}"
                    : $"Setup Required ({DisplayLabel}): {MissingReason}";

                var statusIcon = UiBuilder.Icon(
                    IsConfigured
                        ? MaterialIconKind.RobotOutline
                        : MaterialIconKind.AlertCircleOutline,
                    15,
                    IsConfigured
                        ? ThemeService.Brush(ThemeService.AccentPrimary)
                        : ThemeService.Brush(ThemeService.PriorityP2Fg)
                );
                statusIcon.VerticalAlignment = VerticalAlignment.Center;
                statusGrid.Children.Add(statusIcon);

                var statusText = new TextBlock
                {
                    Text = fullStatusText,
                    FontSize = 12,
                    FontWeight = IsConfigured ? FontWeight.Medium : FontWeight.SemiBold,
                    Foreground = IsConfigured
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
                    IsConfigured ? "Change" : "Configure",
                    MaterialIconKind.CogOutline,
                    studio.ToggleAiSettings,
                    isActive: !IsConfigured
                );
                cfgBtn.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(cfgBtn, 2);
                statusGrid.Children.Add(cfgBtn);

                configCard.Children.Add(
                    new Border
                    {
                        Background = IsConfigured
                            ? ThemeService.Brush(ThemeService.BgBase)
                            : ThemeService.Brush(ThemeService.PriorityP2Bg),
                        BorderBrush = IsConfigured
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
            bool canSubmit = isOfflineMode || IsConfigured;
            var genPrimaryBtn = UiBuilder.PrimaryButton(
                studio.IsGenerating ? "Generating Grounded Assessment..."
                    : isOfflineMode ? $"Generate {studio.QuestionCount}-Item Offline PDF Quiz"
                    : !IsConfigured ? "Configure AI Engine to Generate"
                    : $"Generate {studio.QuestionCount}-Item Grounded Exam",
                isOfflineMode ? MaterialIconKind.LightningBoltOutline
                    : !IsConfigured ? MaterialIconKind.CogOutline
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
        Control previewPane =
            studio.IsMixedSourceMode ? BuildMixedPreviewPane(vm, studio)
            : studio.IsFileSourceMode ? BuildFileSourcesPreviewPane(vm, studio)
            : BuildLivePdfPreviewPane(vm, studio);
        Grid.SetColumn(previewPane, 2);
        splitGrid.Children.Add(previewPane);

        return splitGrid;
    }
}
