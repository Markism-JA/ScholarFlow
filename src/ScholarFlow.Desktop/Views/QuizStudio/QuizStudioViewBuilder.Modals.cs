using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views;

public static partial class QuizStudioViewBuilder
{
    private static Border BuildAiSettingsCard(MainWindowViewModel vm, QuizStudioViewModel studio)
    {
        var settings = vm.Settings;
        var cardStack = new StackPanel { Spacing = 8 };

        cardStack.Children.Add(
            UiBuilder.SectionHeader("Unified OpenAI-Compatible AI Engine & Saved Model Profiles")
        );

        var presetRow = new WrapPanel { Orientation = Orientation.Horizontal };
        void AddProviderPreset(string id, string label, MaterialIconKind icon)
        {
            bool active =
                string.Equals(settings.AiProviderPreset, id, StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(settings.ActiveAiProfileId);
            var btn = UiBuilder.GhostButton(
                label,
                icon,
                async () => await studio.ApplyAiProviderPresetAsync(id),
                isActive: active
            );
            btn.Margin = new Thickness(0, 0, 6, 4);
            presetRow.Children.Add(btn);
        }

        AddProviderPreset("OpenAI", "OpenAI", MaterialIconKind.CreationOutline);
        AddProviderPreset(
            "OpenRouter",
            "OpenRouter (Claude / Multi)",
            MaterialIconKind.SwapHorizontal
        );
        AddProviderPreset("GeminiAiStudio", "Google AI Studio", MaterialIconKind.AutoFix);
        AddProviderPreset("DeepSeek", "DeepSeek", MaterialIconKind.Brain);
        AddProviderPreset("Groq", "Groq", MaterialIconKind.LightningBoltOutline);
        AddProviderPreset("OllamaLocal", "Ollama (Local)", MaterialIconKind.ServerNetwork);
        AddProviderPreset("LmStudio", "LM Studio / Custom", MaterialIconKind.DesktopClassic);
        AddProviderPreset(
            "OfflineSmart",
            "Offline PDF Analyzer",
            MaterialIconKind.FileSearchOutline
        );
        cardStack.Children.Add(presetRow);

        // Saved Custom Model Profiles
        if (settings.SavedAiProfiles.Count > 0)
        {
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
                    Spacing = 4,
                };
                var selectBtn = UiBuilder.GhostButton(
                    $"{prof.Name} ({prof.ModelName})",
                    isActiveProf ? MaterialIconKind.Check : MaterialIconKind.RobotOutline,
                    async () => await studio.SelectSavedAiProfileAsync(capturedProf),
                    isActive: isActiveProf
                );
                chipStack.Children.Add(selectBtn);

                var delBtn = UiBuilder.IconButton(
                    MaterialIconKind.Close,
                    async () => await studio.DeleteSavedAiProfileAsync(capturedProf.Id),
                    12.5,
                    ThemeService.Brush(ThemeService.TextMuted)
                );
                chipStack.Children.Add(delBtn);

                chipStack.Margin = new Thickness(0, 0, 8, 4);
                profilesWrap.Children.Add(chipStack);
            }
            cardStack.Children.Add(profilesWrap);
        }

        var fieldsGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("1.1*,1.8*,1.2*,1.3*,Auto,Auto"),
            ColumnSpacing = 6,
        };

        var profileNameBox = new TextBox { Watermark = "Profile name (optional)", FontSize = 12 };
        fieldsGrid.Children.Add(profileNameBox);

        var urlBox = new TextBox
        {
            Text = settings.AiBaseUrl,
            Watermark = "OpenAI-compatible /chat/completions URL",
            FontSize = 12,
        };
        Grid.SetColumn(urlBox, 1);
        fieldsGrid.Children.Add(urlBox);

        var modelBox = new TextBox
        {
            Text = settings.AiModelName,
            Watermark = "Model ID (e.g. gpt-4o-mini, claude-3.5-sonnet, qwen2.5:7b)",
            FontSize = 12,
        };
        Grid.SetColumn(modelBox, 2);
        fieldsGrid.Children.Add(modelBox);

        var keyBox = new TextBox
        {
            Text = settings.AiApiKey ?? string.Empty,
            Watermark = "API Key (or env var)",
            PasswordChar = '•',
            FontSize = 12,
        };
        Grid.SetColumn(keyBox, 3);
        fieldsGrid.Children.Add(keyBox);

        var saveBtn = UiBuilder.PrimaryButton(
            "Save",
            MaterialIconKind.ContentSaveOutline,
            async () =>
                await studio.SaveAiCredentialsAsync(
                    urlBox.Text ?? "",
                    modelBox.Text ?? "",
                    keyBox.Text
                )
        );
        Grid.SetColumn(saveBtn, 4);
        fieldsGrid.Children.Add(saveBtn);

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
        Grid.SetColumn(addProfileBtn, 5);
        fieldsGrid.Children.Add(addProfileBtn);

        cardStack.Children.Add(fieldsGrid);
        return UiBuilder.Card(cardStack, padding: new Thickness(12, 10));
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
