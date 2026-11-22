using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using ScholarFlow.Core.Models;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views;

public static partial class QuizStudioViewBuilder
{
    private static readonly (string Id, string Label, MaterialIconKind Icon)[] AiProviderPresets =
    [
        ("OpenAI", "OpenAI", MaterialIconKind.CreationOutline),
        ("OpenRouter", "OpenRouter", MaterialIconKind.SwapHorizontal),
        ("GeminiAiStudio", "Google AI Studio", MaterialIconKind.AutoFix),
        ("DeepSeek", "DeepSeek", MaterialIconKind.Brain),
        ("Groq", "Groq", MaterialIconKind.LightningBoltOutline),
        ("OllamaLocal", "Ollama (Local)", MaterialIconKind.ServerNetwork),
        ("LmStudio", "LM Studio / Custom", MaterialIconKind.DesktopClassic),
        ("OfflineSmart", "Offline PDF Analyzer", MaterialIconKind.FileSearchOutline),
    ];

    /// <summary>
    /// Compact header pill showing the active AI model/profile + readiness dot.
    /// Clicking opens a floating quick-switch Flyout popover (zero workspace shrink),
    /// with a button to open the full floating AI Engine Configuration modal.
    /// </summary>
    private static Button BuildAiQuickSwitchButton(
        MainWindowViewModel vm,
        QuizStudioViewModel studio
    )
    {
        var settings = vm.Settings;
        var (displayLabel, isConfigured, missingReason) = studio.GetActiveAiEngineStatus();

        // Concise pill label: prefer active profile name, or model name, or "Offline Analyzer"
        var activeProfile = !string.IsNullOrWhiteSpace(settings.ActiveAiProfileId)
            ? settings.SavedAiProfiles.FirstOrDefault(p =>
                string.Equals(p.Id, settings.ActiveAiProfileId, StringComparison.OrdinalIgnoreCase)
            )
            : null;

        bool isOffline =
            activeProfile?.IsOffline == true
            || string.Equals(
                settings.AiProviderPreset,
                "OfflineSmart",
                StringComparison.OrdinalIgnoreCase
            );

        string shortLabel =
            !isConfigured ? "Configure AI"
            : isOffline ? "Offline Analyzer"
            : activeProfile is not null ? activeProfile.Name
            : !string.IsNullOrWhiteSpace(settings.AiModelName) ? settings.AiModelName
            : displayLabel;

        var statusDotColor =
            !isConfigured ? ThemeService.Brush(ThemeService.PriorityP2Fg)
            : isOffline ? ThemeService.Brush(ThemeService.TextSecondary)
            : new SolidColorBrush(
                Color.Parse(ThemeService.CurrentTheme == ThemeMode.Dark ? "#4ADE80" : "#16A34A")
            );

        var dot = new Border
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(3.5),
            Background = statusDotColor,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var labelText = new TextBlock
        {
            Text = shortLabel,
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            MaxWidth = 148,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var normalBg = studio.IsAiSettingsOpen
            ? ThemeService.Brush(ThemeService.BgElevated)
            : Brushes.Transparent;
        var hoverBg = ThemeService.Brush(ThemeService.BgHover);
        var normalBorder = studio.IsAiSettingsOpen
            ? ThemeService.Brush(ThemeService.BorderStrong)
            : ThemeService.Brush(ThemeService.BorderSubtle);

        var btn = new Button
        {
            Background = normalBg,
            BorderBrush = normalBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    dot,
                    labelText,
                    UiBuilder.Icon(
                        MaterialIconKind.ChevronDown,
                        13.5,
                        ThemeService.Brush(ThemeService.TextMuted)
                    ),
                },
            },
        };
        btn.PointerEntered += (_, _) => btn.Background = hoverBg;
        btn.PointerExited += (_, _) => btn.Background = normalBg;

        ToolTip.SetTip(
            btn,
            isConfigured
                ? $"Active AI: {displayLabel} — Click to switch model or configure"
                : $"AI Engine needs setup: {missingReason} — Click to configure"
        );

        var flyoutHost = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(2),
            Width = 304,
            MaxHeight = 390,
        };
        var flyout = new Flyout
        {
            Content = flyoutHost,
            Placement = PlacementMode.BottomEdgeAlignedRight,
        };

        flyout.Opening += (_, _) =>
        {
            var (currLabel, currReady, currMissing) = studio.GetActiveAiEngineStatus();
            var stack = new StackPanel { Spacing = 8 };

            // Active status summary header
            var statusCard = new Border
            {
                Background = ThemeService.Brush(ThemeService.BgElevated),
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 7),
                Child = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "ACTIVE AI ENGINE",
                            FontSize = 10,
                            FontWeight = FontWeight.SemiBold,
                            LetterSpacing = 0.6,
                            Foreground = ThemeService.Brush(ThemeService.TextMuted),
                        },
                        new TextBlock
                        {
                            Text = currLabel,
                            FontSize = 12.5,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                        new TextBlock
                        {
                            Text = currReady ? "Ready to generate & fix decks" : currMissing ?? "Configuration required",
                            FontSize = 11,
                            Foreground = currReady
                                ? ThemeService.Brush(ThemeService.TextSecondary)
                                : ThemeService.Brush(ThemeService.PriorityP2Fg),
                            TextWrapping = TextWrapping.Wrap,
                        },
                    },
                },
            };
            stack.Children.Add(statusCard);

            // Saved Profiles (1-click switch)
            if (settings.SavedAiProfiles.Count > 0)
            {
                stack.Children.Add(UiBuilder.SectionHeader("Saved Profiles"));
                var profList = new StackPanel { Spacing = 3 };
                foreach (var prof in settings.SavedAiProfiles)
                {
                    var capturedProf = prof;
                    bool isActiveProf = string.Equals(
                        settings.ActiveAiProfileId,
                        prof.Id,
                        StringComparison.OrdinalIgnoreCase
                    );
                    var pBtn = UiBuilder.GhostButton(
                        $"{prof.Name} · {prof.ModelName}",
                        isActiveProf ? MaterialIconKind.Check : MaterialIconKind.RobotOutline,
                        async () =>
                        {
                            flyout.Hide();
                            await studio.SelectSavedAiProfileAsync(capturedProf);
                        },
                        isActive: isActiveProf
                    );
                    pBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
                    pBtn.HorizontalContentAlignment = HorizontalAlignment.Left;
                    profList.Children.Add(pBtn);
                }
                stack.Children.Add(profList);
            }

            // Quick Provider Presets
            stack.Children.Add(UiBuilder.SectionHeader("Quick Switch Provider"));
            var presetWrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var (id, label, icon) in AiProviderPresets)
            {
                var capturedId = id;
                bool active =
                    string.Equals(settings.AiProviderPreset, id, StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrWhiteSpace(settings.ActiveAiProfileId);
                var pBtn = UiBuilder.GhostButton(
                    label,
                    icon,
                    async () =>
                    {
                        flyout.Hide();
                        await studio.ApplyAiProviderPresetAsync(capturedId);
                    },
                    isActive: active
                );
                pBtn.Margin = new Thickness(0, 0, 4, 4);
                presetWrap.Children.Add(pBtn);
            }
            stack.Children.Add(presetWrap);

            stack.Children.Add(UiBuilder.HairlineDivider());

            // Footer: Open Full Floating Configuration Modal
            var configBtn = UiBuilder.GhostButton(
                "Configure Provider, Model & API Keys...",
                MaterialIconKind.CogOutline,
                () =>
                {
                    flyout.Hide();
                    if (!studio.IsAiSettingsOpen)
                        studio.ToggleAiSettings();
                }
            );
            configBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
            configBtn.HorizontalContentAlignment = HorizontalAlignment.Center;
            stack.Children.Add(configBtn);

            flyoutHost.Child = new ScrollViewer
            {
                Content = stack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
        };

        btn.Flyout = flyout;
        return btn;
    }

    /// <summary>
    /// Full floating centered AI Engine Configuration modal overlay.
    /// Zero impact on workspace layout; responsive 2-column/stacked form fields.
    /// </summary>
    private static Grid BuildFloatingAiSettingsOverlay(
        MainWindowViewModel vm,
        QuizStudioViewModel studio
    )
    {
        var settings = vm.Settings;
        var (displayLabel, isConfigured, _) = studio.GetActiveAiEngineStatus();

        var overlay = new Grid();

        var scrim = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#7A000000")),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        scrim.PointerPressed += (_, _) => studio.ToggleAiSettings();
        overlay.Children.Add(scrim);

        // Modal Header
        var headerGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
            ColumnSpacing = 8,
            Margin = new Thickness(0, 0, 0, 10),
        };
        headerGrid.Children.Add(
            UiBuilder.Icon(
                MaterialIconKind.RobotOutline,
                18,
                ThemeService.Brush(ThemeService.TextPrimary)
            )
        );
        var titleStack = new StackPanel
        {
            Spacing = 1,
            [Grid.ColumnProperty] = 1,
            Children =
            {
                new TextBlock
                {
                    Text = "AI Engine & Saved Profiles",
                    FontSize = 14.5,
                    FontWeight = FontWeight.Bold,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                },
                new TextBlock
                {
                    Text = isConfigured ? $"Active: {displayLabel}" : $"Needs setup: {displayLabel}",
                    FontSize = 11.5,
                    Foreground = isConfigured
                        ? ThemeService.Brush(ThemeService.TextSecondary)
                        : ThemeService.Brush(ThemeService.PriorityP2Fg),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };
        headerGrid.Children.Add(titleStack);

        var closeBtn = UiBuilder.IconButton(
            MaterialIconKind.Close,
            studio.ToggleAiSettings,
            16,
            ThemeService.Brush(ThemeService.TextSecondary)
        );
        ToolTip.SetTip(closeBtn, "Close (Esc)");
        Grid.SetColumn(closeBtn, 2);
        headerGrid.Children.Add(closeBtn);

        // Scrollable Modal Body
        var bodyStack = new StackPanel { Spacing = 10 };

        bodyStack.Children.Add(UiBuilder.SectionHeader("Provider Presets"));
        var presetRow = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (id, label, icon) in AiProviderPresets)
        {
            var capturedId = id;
            bool active =
                string.Equals(settings.AiProviderPreset, id, StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(settings.ActiveAiProfileId);
            var btn = UiBuilder.GhostButton(
                label,
                icon,
                async () => await studio.ApplyAiProviderPresetAsync(capturedId),
                isActive: active
            );
            btn.Margin = new Thickness(0, 0, 5, 5);
            presetRow.Children.Add(btn);
        }
        bodyStack.Children.Add(presetRow);

        // Saved Custom Model Profiles
        if (settings.SavedAiProfiles.Count > 0)
        {
            bodyStack.Children.Add(UiBuilder.SectionHeader("Saved Model Profiles"));
            var profilesWrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var prof in settings.SavedAiProfiles)
            {
                var capturedProf = prof;
                bool isActiveProf = string.Equals(
                    settings.ActiveAiProfileId,
                    prof.Id,
                    StringComparison.OrdinalIgnoreCase
                );

                var chipStack = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 3,
                    Margin = new Thickness(0, 0, 6, 5),
                };
                chipStack.Children.Add(
                    UiBuilder.GhostButton(
                        $"{prof.Name} ({prof.ModelName})",
                        isActiveProf ? MaterialIconKind.Check : MaterialIconKind.RobotOutline,
                        async () => await studio.SelectSavedAiProfileAsync(capturedProf),
                        isActive: isActiveProf
                    )
                );
                var delBtn = UiBuilder.IconButton(
                    MaterialIconKind.Close,
                    async () => await studio.DeleteSavedAiProfileAsync(capturedProf.Id),
                    12.5,
                    ThemeService.Brush(ThemeService.TextMuted)
                );
                ToolTip.SetTip(delBtn, $"Delete profile '{prof.Name}'");
                chipStack.Children.Add(delBtn);
                profilesWrap.Children.Add(chipStack);
            }
            bodyStack.Children.Add(profilesWrap);
        }

        bodyStack.Children.Add(UiBuilder.HairlineDivider());
        bodyStack.Children.Add(UiBuilder.SectionHeader("Endpoint & Credentials"));

        static StackPanel FieldGroup(string label, Control input) =>
            new()
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = label,
                        FontSize = 11,
                        FontWeight = FontWeight.Medium,
                        Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                    },
                    input,
                },
            };

        var urlBox = new TextBox
        {
            Text = settings.AiBaseUrl,
            Watermark = "OpenAI-compatible /chat/completions URL",
            FontSize = 12,
        };
        bodyStack.Children.Add(FieldGroup("Base URL", urlBox));

        var modelKeyRow = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,*"),
            ColumnSpacing = 10,
        };
        var modelBox = new TextBox
        {
            Text = settings.AiModelName,
            Watermark = "e.g. gpt-4o-mini, claude-3.5-sonnet, qwen2.5:7b",
            FontSize = 12,
        };
        var keyBox = new TextBox
        {
            Text = settings.AiApiKey ?? string.Empty,
            Watermark = "API Key (or env var)",
            PasswordChar = '•',
            FontSize = 12,
        };
        modelKeyRow.Children.Add(FieldGroup("Model ID", modelBox));
        var keyField = FieldGroup("API Key", keyBox);
        Grid.SetColumn(keyField, 1);
        modelKeyRow.Children.Add(keyField);
        bodyStack.Children.Add(modelKeyRow);

        var profileNameBox = new TextBox
        {
            Watermark = "Profile name (optional — used when saving a new profile)",
            FontSize = 12,
        };
        bodyStack.Children.Add(FieldGroup("Save As Named Profile", profileNameBox));

        var scrollBody = new ScrollViewer
        {
            Content = bodyStack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 4, 0),
        };

        // Footer Actions
        var footerGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,Auto"),
            ColumnSpacing = 6,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var doneBtn = UiBuilder.GhostButton("Done", null, studio.ToggleAiSettings);
        footerGrid.Children.Add(doneBtn);

        var addProfileBtn = UiBuilder.GhostButton(
            "+ Save Profile",
            MaterialIconKind.BookmarkPlusOutline,
            async () =>
                await studio.SaveAsNewAiProfileAsync(
                    profileNameBox.Text ?? "",
                    urlBox.Text ?? "",
                    modelBox.Text ?? "",
                    keyBox.Text
                )
        );
        Grid.SetColumn(addProfileBtn, 2);
        footerGrid.Children.Add(addProfileBtn);

        var saveBtn = UiBuilder.PrimaryButton(
            "Save",
            MaterialIconKind.ContentSaveOutline,
            async () =>
            {
                await studio.SaveAiCredentialsAsync(
                    urlBox.Text ?? "",
                    modelBox.Text ?? "",
                    keyBox.Text
                );
                if (studio.IsAiSettingsOpen)
                    studio.ToggleAiSettings();
            }
        );
        Grid.SetColumn(saveBtn, 3);
        footerGrid.Children.Add(saveBtn);

        var modalGrid = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,*,Auto"),
        };
        modalGrid.Children.Add(headerGrid);
        Grid.SetRow(scrollBody, 1);
        modalGrid.Children.Add(scrollBody);
        Grid.SetRow(footerGrid, 2);
        modalGrid.Children.Add(footerGrid);

        var modalCard = new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderStrong),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 16),
            MaxWidth = 540,
            MaxHeight = 580,
            Margin = new Thickness(20),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            BoxShadow = BoxShadows.Parse("0 12 36 0 #70000000"),
            Child = modalGrid,
        };
        overlay.Children.Add(modalCard);

        return overlay;
    }

    private static Border BuildManualJsonBridgeCard(QuizStudioViewModel studio)
    {
        var cardStack = new StackPanel { Spacing = 8 };
        cardStack.Children.Add(
            UiBuilder.SectionHeader(
                "Universal Web AI JSON Bridge (ChatGPT, Claude, Gemini, DeepSeek — Zero API Quota)"
            )
        );

        // Coverage & Budget Controls Row
        var controlsRow = new WrapPanel { Orientation = Orientation.Horizontal };
        bool isTruncated =
            studio.ManualPromptTotalPages > 0
            && studio.ManualPromptIncludedPages < studio.ManualPromptTotalPages;
        string coverageText =
            studio.IsBuildingPrompt ? "Calculating scope coverage..."
            : studio.ManualPromptTotalPages > 0
                ? (
                    isTruncated
                        ? $"⚠ Truncated: {studio.ManualPromptIncludedPages}/{studio.ManualPromptTotalPages} pages ({studio.GeneratedPromptPreview.Length:N0} chars)"
                        : $"Coverage: {studio.ManualPromptIncludedPages}/{studio.ManualPromptTotalPages} pages (100% · {studio.GeneratedPromptPreview.Length:N0} chars)"
                )
            : "Select a PDF scope";

        var coveragePill = UiBuilder.Pill(
            coverageText,
            isTruncated
                ? ThemeService.Brush(ThemeService.PriorityP2Fg)
                : ThemeService.Brush(ThemeService.AccentPrimary),
            isTruncated
                ? ThemeService.Brush(ThemeService.PriorityP2Bg)
                : ThemeService.Brush(ThemeService.BgElevated),
            fontSize: 11
        );
        coveragePill.Margin = new Thickness(0, 0, 8, 4);
        controlsRow.Children.Add(coveragePill);

        foreach (
            var (label, limit) in new[]
            {
                ("48k", 48_000),
                ("160k", 160_000),
                ("Uncapped", int.MaxValue),
            }
        )
        {
            int capturedLimit = limit;
            var budgetBtn = UiBuilder.GhostButton(
                $"Budget: {label}",
                null,
                () => studio.SetManualPromptBudget(capturedLimit),
                isActive: studio.ManualPromptBudgetChars == capturedLimit
            );
            budgetBtn.Margin = new Thickness(0, 0, 4, 4);
            controlsRow.Children.Add(budgetBtn);
        }
        cardStack.Children.Add(controlsRow);

        var split = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,12,*"),
            RowDefinitions = RowDefinitions.Parse("*,Auto"),
            RowSpacing = 8,
        };

        var promptBox = new TextBox
        {
            Text = studio.IsBuildingPrompt
                ? "Building grounded prompt from selected PDF pages..."
                : studio.GeneratedPromptPreview,
            IsReadOnly = true,
            AcceptsReturn = true,
            MinHeight = 125,
            MaxHeight = 220,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
        };
        split.Children.Add(promptBox);

        var leftActions = new WrapPanel { Orientation = Orientation.Horizontal };
        var copyBtn = UiBuilder.GhostButton(
            "Copy Grounded Prompt",
            MaterialIconKind.ContentCopy,
            async () =>
            {
                var top = TopLevel.GetTopLevel(promptBox);
                if (top?.Clipboard is not null)
                    await top.Clipboard.SetTextAsync(studio.GeneratedPromptPreview);
            }
        );
        copyBtn.Margin = new Thickness(0, 0, 6, 0);
        leftActions.Children.Add(copyBtn);

        var exportPdfBtn = UiBuilder.GhostButton(
            studio.IsMixedSourceMode ? "Export Combined PDF + Copy Prompt"
                : studio.IsFileSourceMode ? "Pack Images as PDF + Copy Prompt"
                : "Export Scoped PDF + Copy Prompt",
            MaterialIconKind.FileExportOutline,
            async () =>
            {
                var top = TopLevel.GetTopLevel(promptBox);
                if (
                    studio.IsFileSourceMode
                    && !studio.IsMixedSourceMode
                    && !studio.SelectedFileSourcePaths.Any(Core.Knowledge.ImagePacketPacker.IsImage)
                )
                {
                    await studio.ExportScopedPdfForUploadAsync(top?.Clipboard);
                    return;
                }

                string? chosenPath = null;
                if (top?.StorageProvider is not null)
                {
                    var courseTag = Core.Services.FilenameSanitizer.SanitizeFileName(
                        studio.SelectedCourse?.Alias ?? "ScholarFlow",
                        ""
                    );
                    var suggested =
                        studio.IsMixedSourceMode
                            ? $"{courseTag}_Combined_Packet_{DateTime.Now:yyyyMMdd_HHmm}.pdf"
                        : studio.IsFileSourceMode
                            ? $"{courseTag}_Images_Packet_{DateTime.Now:yyyyMMdd_HHmm}.pdf"
                        : $"{courseTag}_Scoped_{DateTime.Now:yyyyMMdd_HHmm}.pdf";

                    var saved = await top.StorageProvider.SaveFilePickerAsync(
                        new Avalonia.Platform.Storage.FilePickerSaveOptions
                        {
                            Title =
                                studio.IsMixedSourceMode ? "Save Combined PDF + Images Packet"
                                : studio.IsFileSourceMode ? "Save Packed Images PDF"
                                : "Save Scoped PDF for Upload",
                            SuggestedFileName = suggested,
                            DefaultExtension = "pdf",
                            FileTypeChoices =
                            [
                                new Avalonia.Platform.Storage.FilePickerFileType("PDF Document")
                                {
                                    Patterns = ["*.pdf"],
                                },
                            ],
                        }
                    );
                    chosenPath = saved is null
                        ? null
                        : Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(
                            saved
                        );
                    if (string.IsNullOrWhiteSpace(chosenPath))
                        return;
                }

                await studio.ExportScopedPdfForUploadAsync(top?.Clipboard, chosenPath);
            }
        );
        leftActions.Children.Add(exportPdfBtn);

        Grid.SetRow(leftActions, 1);
        Grid.SetColumn(leftActions, 0);
        split.Children.Add(leftActions);

        var jsonInputBox = new TextBox
        {
            Text = studio.ManualJsonInput,
            Watermark =
                "Paste the JSON response from any AI chat (ChatGPT, Claude, Gemini, DeepSeek...) here...",
            AcceptsReturn = true,
            MinHeight = 125,
            MaxHeight = 220,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
        };
        jsonInputBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                studio.ManualJsonInput = jsonInputBox.Text ?? string.Empty;
        };
        Grid.SetColumn(jsonInputBox, 2);
        split.Children.Add(jsonInputBox);

        var importBtn = UiBuilder.PrimaryButton(
            studio.IsGenerating ? "Importing & Grounding..."
                : studio.IsMixedSourceMode ? "Import JSON & Ground PDF + Files"
                : studio.IsFileSourceMode ? "Import JSON & Ground to Files"
                : "Import JSON & Ground to PDF",
            MaterialIconKind.CheckDecagramOutline,
            async () => await studio.ImportPastedJsonQuizAsync(jsonInputBox.Text ?? string.Empty)
        );
        Grid.SetRow(importBtn, 1);
        Grid.SetColumn(importBtn, 2);
        split.Children.Add(importBtn);

        cardStack.Children.Add(split);
        return UiBuilder.Card(cardStack, padding: new Thickness(12, 10));
    }

    // =========================================================================
    // 2. INTERACTIVE QUESTION RUNNER & GROUNDED PDF EVIDENCE VIEW
    // =========================================================================
}
