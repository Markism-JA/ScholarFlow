using ScholarFlow.Core.Models;
using ScholarFlow.Core.Quiz;
using ScholarFlow.Core.Services;

namespace ScholarFlow.Desktop.ViewModels;

public sealed partial class QuizStudioViewModel
{
    public void SetQuestionCount(int count, bool notify = true)
    {
        QuestionCount = Math.Clamp(count, 1, 150);
        if (AllocationMode == QuestionAllocationMode.ExactCounts)
        {
            var scaled = BuildCurrentRequest().ResolveExactKindCounts(QuestionCount);
            KindAllocations = scaled;
        }
        RefreshLivePromptPreviewIfActive();
        if (notify)
            _mainVm.RequestUiRefresh();
    }

    public void SetAllocationMode(QuestionAllocationMode mode)
    {
        if (AllocationMode == mode)
            return;

        if (mode == QuestionAllocationMode.ExactCounts)
        {
            var resolved = BuildCurrentRequest().ResolveExactKindCounts(QuestionCount);
            AllocationMode = QuestionAllocationMode.ExactCounts;
            KindAllocations = resolved;
            QuestionCount = Math.Clamp(KindAllocations.Values.Sum(), 1, 150);
        }
        else
        {
            int sum = Math.Max(1, KindAllocations.Values.Sum());
            var pcts = new Dictionary<QuizQuestionKind, int>();
            foreach (var k in Enum.GetValues<QuizQuestionKind>())
            {
                pcts[k] = (int)Math.Round(100.0 * KindAllocations.GetValueOrDefault(k, 0) / sum);
            }
            AllocationMode = QuestionAllocationMode.Percentages;
            KindAllocations = pcts;
        }

        RefreshLivePromptPreviewIfActive();
        _mainVm.RequestUiRefresh();
    }

    public void SetKindAllocation(QuizQuestionKind kind, int value, bool notify = true)
    {
        int clamped = Math.Clamp(value, 0, 150);
        KindAllocations[kind] = clamped;
        SelectedPreset = QuizPresetMode.Custom;
        IsCodeAwareMixActive = false;

        if (AllocationMode == QuestionAllocationMode.ExactCounts)
        {
            int sum = KindAllocations.Values.Sum();
            if (sum > 0)
                QuestionCount = Math.Clamp(sum, 1, 150);
        }

        RefreshLivePromptPreviewIfActive();
        if (notify)
            _mainVm.RequestUiRefresh();
    }

    public void StepKindAllocation(QuizQuestionKind kind, int delta)
    {
        int step = AllocationMode == QuestionAllocationMode.Percentages ? (delta * 5) : delta;
        int current = KindAllocations.GetValueOrDefault(kind, 0);
        SetKindAllocation(kind, Math.Max(0, current + step), notify: true);
    }

    public Dictionary<QuizQuestionKind, int> GetResolvedExactKindCounts()
    {
        return BuildCurrentRequest().ResolveExactKindCounts(QuestionCount);
    }

    public void SetCustomPromptInstructions(string? instructions, bool notify = true)
    {
        var next = instructions ?? string.Empty;
        if (next.Length > 1200)
            next = next[..1200];

        if (string.Equals(CustomPromptInstructions, next, StringComparison.Ordinal))
            return;

        CustomPromptInstructions = next;
        RefreshLivePromptPreviewIfActive();
        if (notify)
            _mainVm.RequestUiRefresh();
    }

    public void AppendCustomPromptDirective(string directive)
    {
        if (string.IsNullOrWhiteSpace(directive))
            return;

        var current = CustomPromptInstructions.Trim();
        if (current.Contains(directive, StringComparison.OrdinalIgnoreCase))
            return;

        var updated = string.IsNullOrEmpty(current) ? directive : $"{current}\n{directive}";
        SetCustomPromptInstructions(updated, notify: true);
    }

    public void SetModalityMode(QuizContentModalityMode mode)
    {
        if (ModalityMode == mode)
            return;

        ModalityMode = mode;
        RefreshLivePromptPreviewIfActive();
        _mainVm.RequestUiRefresh();
    }

    public void SetBuilderOutputMode(QuizBuilderOutputMode mode)
    {
        BuilderOutputMode = mode;
        if (mode == QuizBuilderOutputMode.WebJson)
            RefreshLivePromptPreviewIfActive(force: true);
        _mainVm.RequestUiRefresh();
    }

    public void RefreshLivePromptPreviewIfActive(bool force = false)
    {
        if (!force && !IsManualJsonModalOpen && BuilderOutputMode != QuizBuilderOutputMode.WebJson)
            return;

        var req = BuildCurrentRequest();
        if (!req.HasFileSources && (string.IsNullOrWhiteSpace(req.SourcePdfPath) || !File.Exists(req.SourcePdfPath)))
        {
            GeneratedPromptPreview = string.Empty;
            return;
        }

        _ = RebuildPromptPreviewAsync(req, updateStatus: force);
    }

    private async Task RebuildPromptPreviewAsync(QuizGenerationRequest req, bool updateStatus)
    {
        _promptBuildCts?.Cancel();
        var cts = new CancellationTokenSource();
        _promptBuildCts = cts;
        var token = cts.Token;

        IsBuildingPrompt = true;
        int budget = ManualPromptBudgetChars;
        try
        {
            if (!updateStatus)
                await Task.Delay(220, token);

            var (prompt, totalCount, includedCount) = await Task.Run(
                () =>
                {
                    token.ThrowIfCancellationRequested();
                    if (req.HasFileSources)
                    {
                        var fileText = _llmService.BuildFileSourcePromptForManualCopy(
                            req,
                            budget,
                            out var bundle,
                            out int totalPdf,
                            out int incPdf
                        );
                        int included = bundle.IncludedTextFiles.Count + bundle.ImageFiles.Count + incPdf;
                        int total =
                            bundle.IncludedTextFiles.Count
                            + bundle.ImageFiles.Count
                            + bundle.TruncatedFiles.Count(t => !t.Contains("(first"))
                            + totalPdf;
                        return (fileText, total, included);
                    }

                    var text = _llmService.BuildPromptForManualCopy(
                        req,
                        budget,
                        out var pages,
                        out var inc
                    );
                    return (text, pages.Count, inc);
                },
                token
            );

            if (token.IsCancellationRequested)
                return;

            GeneratedPromptPreview = prompt;
            ManualPromptTotalPages = totalCount;
            ManualPromptIncludedPages = includedCount;
            if (updateStatus)
            {
                StatusMessage = includedCount < totalCount
                    ? $"Prompt built for {includedCount}/{totalCount} pages (budget reached — raise budget or Export Scoped PDF)."
                    : $"Live grounded prompt ready across {includedCount}/{totalCount} PDF page(s) ({req.QuestionCount} items).";
                IsStatusError = false;
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer scope/allocation change
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                StatusMessage = $"Prompt preview error: {ex.Message}";
                IsStatusError = true;
            }
        }
        finally
        {
            if (_promptBuildCts == cts)
            {
                IsBuildingPrompt = false;
                Avalonia.Threading.Dispatcher.UIThread.Post(_mainVm.RequestUiRefresh);
            }
        }
    }

    public void StartAppendToQuiz(QuizSessionRecord quiz)
    {
        AppendTargetQuizId = quiz.Id;
        AppendTargetQuizTitle = quiz.Title;
        var course = _mainVm.Courses.FirstOrDefault(c =>
            string.Equals(c.FolderName, quiz.CourseFolder, StringComparison.OrdinalIgnoreCase)
        );
        if (course is not null)
            SelectedCourse = course;
        RebuildAvailablePdfsForCourse(quiz.SourcePdfPath);
        if (!string.IsNullOrWhiteSpace(quiz.Term))
            SelectedTerm = quiz.Term;
        RecomputeScopePreview();
        ActiveSubTab = QuizStudioSubTab.Builder;
        StatusMessage =
            $"Appending new non-duplicate questions to '{quiz.Title}'. Configure count/types and generate.";
        IsStatusError = false;
        _mainVm.RequestUiRefresh();
    }

    public void ClearAppendTarget()
    {
        AppendTargetQuizId = null;
        AppendTargetQuizTitle = null;
        RefreshLivePromptPreviewIfActive();
        _mainVm.RequestUiRefresh();
    }

    public void SetPreset(QuizPresetMode preset)
    {
        SelectedPreset = preset;
        IsCodeAwareMixActive = preset == QuizPresetMode.CodeLab;
        if (preset != QuizPresetMode.Custom)
        {
            KindAllocations =
                preset == QuizPresetMode.CodeLab
                    ? QuizGenerationRequest.GetCodeAwareAllocations(AllocationMode, QuestionCount)
                    : QuizGenerationRequest.GetDefaultAllocationsForPreset(preset, AllocationMode);
            if (AllocationMode == QuestionAllocationMode.ExactCounts)
                QuestionCount = Math.Clamp(KindAllocations.Values.Sum(), 1, 150);

            CognitiveAllocations =
                QuizGenerationRequest.GetDefaultCognitiveAllocationsForPreset(preset);
            CognitiveMix = preset switch
            {
                QuizPresetMode.QuickReview => CognitiveMixPreset.Foundations,
                QuizPresetMode.WeakTopicDrill or QuizPresetMode.CodeLab =>
                    CognitiveMixPreset.ExamApplication,
                QuizPresetMode.DeepMastery => CognitiveMixPreset.DeepMastery,
                _ => CognitiveMixPreset.Balanced,
            };

            if (preset == QuizPresetMode.WeakTopicDrill)
                FocusWeakSpots = true;
        }
        RefreshLivePromptPreviewIfActive();
        _mainVm.RequestUiRefresh();
    }

    public void ToggleAiSettings()
    {
        IsAiSettingsOpen = !IsAiSettingsOpen;
        _mainVm.RequestUiRefresh();
    }

    public async Task ApplyAiProviderPresetAsync(string presetName)
    {
        var settings = _mainVm.Settings;
        settings.AiProviderPreset = presetName;
        settings.ActiveAiProfileId = null;
        switch (presetName)
        {
            case "OpenAI":
                settings.AiBaseUrl = "https://api.openai.com/v1/chat/completions";
                settings.AiModelName = "gpt-4o-mini";
                break;
            case "OpenRouter":
                settings.AiBaseUrl = "https://openrouter.ai/api/v1/chat/completions";
                settings.AiModelName = "anthropic/claude-3.5-sonnet";
                break;
            case "GeminiAiStudio":
                settings.AiBaseUrl =
                    "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
                settings.AiModelName = "gemini-2.5-flash";
                break;
            case "DeepSeek":
                settings.AiBaseUrl = "https://api.deepseek.com/v1/chat/completions";
                settings.AiModelName = "deepseek-chat";
                break;
            case "Groq":
                settings.AiBaseUrl = "https://api.groq.com/openai/v1/chat/completions";
                settings.AiModelName = "llama-3.3-70b-versatile";
                break;
            case "OllamaLocal":
                settings.AiBaseUrl = "http://localhost:11434/v1/chat/completions";
                settings.AiModelName = "qwen2.5:7b";
                break;
            case "LmStudio":
                settings.AiBaseUrl = "http://localhost:1234/v1/chat/completions";
                settings.AiModelName = "local-model";
                break;
            case "OfflineSmart":
                break;
        }

        await AppSettingsStore.SaveAsync(settings);
        StatusMessage = $"Switched AI provider preset to {presetName} ({settings.AiModelName}).";
        IsStatusError = false;
        _mainVm.RequestUiRefresh();
    }

    public async Task SelectSavedAiProfileAsync(AiProviderProfile profile)
    {
        var settings = _mainVm.Settings;
        settings.ActiveAiProfileId = profile.Id;
        settings.AiProviderPreset = profile.IsOffline ? "OfflineSmart" : "CustomProfile";
        if (!profile.IsOffline)
        {
            settings.AiBaseUrl = profile.BaseUrl;
            settings.AiModelName = profile.ModelName;
            settings.AiApiKey = profile.ApiKey;
        }

        await AppSettingsStore.SaveAsync(settings);
        StatusMessage = $"Active AI profile: {profile.Name} ({profile.ModelName}).";
        IsStatusError = false;
        _mainVm.RequestUiRefresh();
    }

    public async Task SaveAsNewAiProfileAsync(
        string profileName,
        string baseUrl,
        string modelName,
        string? apiKey
    )
    {
        var settings = _mainVm.Settings;
        var cleanUrl = string.IsNullOrWhiteSpace(baseUrl) ? settings.AiBaseUrl : baseUrl.Trim();
        var cleanModel = string.IsNullOrWhiteSpace(modelName)
            ? settings.AiModelName
            : modelName.Trim();
        var cleanKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        var cleanName = string.IsNullOrWhiteSpace(profileName) ? cleanModel : profileName.Trim();

        var existing = settings.SavedAiProfiles.FirstOrDefault(p =>
            p.Name.Equals(cleanName, StringComparison.OrdinalIgnoreCase)
        );

        if (existing is not null)
        {
            existing.BaseUrl = cleanUrl;
            existing.ModelName = cleanModel;
            existing.ApiKey = cleanKey;
            settings.ActiveAiProfileId = existing.Id;
        }
        else
        {
            var created = new AiProviderProfile
            {
                Name = cleanName,
                BaseUrl = cleanUrl,
                ModelName = cleanModel,
                ApiKey = cleanKey,
            };
            settings.SavedAiProfiles.Add(created);
            settings.ActiveAiProfileId = created.Id;
        }

        settings.AiProviderPreset = "CustomProfile";
        settings.AiBaseUrl = cleanUrl;
        settings.AiModelName = cleanModel;
        settings.AiApiKey = cleanKey;

        await AppSettingsStore.SaveAsync(settings);
        StatusMessage = $"Saved AI profile '{cleanName}' ({cleanModel}).";
        IsStatusError = false;
        _mainVm.RequestUiRefresh();
    }

    public async Task DeleteSavedAiProfileAsync(string profileId)
    {
        var settings = _mainVm.Settings;
        var removed = settings.SavedAiProfiles.RemoveAll(p =>
            string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase)
        );
        if (removed > 0)
        {
            if (
                string.Equals(
                    settings.ActiveAiProfileId,
                    profileId,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                settings.ActiveAiProfileId = null;

            await AppSettingsStore.SaveAsync(settings);
            StatusMessage = "Removed saved AI profile.";
            IsStatusError = false;
            _mainVm.RequestUiRefresh();
        }
    }

    public async Task SaveAiCredentialsAsync(string baseUrl, string modelName, string? apiKey)
    {
        var settings = _mainVm.Settings;
        if (!string.IsNullOrWhiteSpace(baseUrl))
            settings.AiBaseUrl = baseUrl.Trim();
        if (!string.IsNullOrWhiteSpace(modelName))
            settings.AiModelName = modelName.Trim();
        settings.AiApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

        if (!string.IsNullOrWhiteSpace(settings.ActiveAiProfileId))
        {
            var active = settings.SavedAiProfiles.FirstOrDefault(p =>
                p.Id == settings.ActiveAiProfileId
            );
            if (active is not null)
            {
                active.BaseUrl = settings.AiBaseUrl;
                active.ModelName = settings.AiModelName;
                active.ApiKey = settings.AiApiKey;
            }
        }

        await AppSettingsStore.SaveAsync(settings);
        StatusMessage = $"Saved AI endpoint ({settings.AiModelName}).";
        IsStatusError = false;
        _mainVm.RequestUiRefresh();
    }

    public void SetManualPromptBudget(int maxChars)
    {
        ManualPromptBudgetChars = maxChars;
        RefreshLivePromptPreviewIfActive(force: true);
        _mainVm.RequestUiRefresh();
    }

    public async Task ExportScopedPdfForUploadAsync(
        Avalonia.Input.Platform.IClipboard? clipboard = null,
        string? outputPdfPath = null)
    {
        var req = BuildCurrentRequest();
        if (req.HasFileSources)
        {
            await ExportPackedImagesForUploadAsync(req, clipboard, outputPdfPath);
            return;
        }

        if (!req.HasFileSources && (string.IsNullOrWhiteSpace(req.SourcePdfPath) || !File.Exists(req.SourcePdfPath)))
        {
            StatusMessage = "Select a valid PDF file first.";
            IsStatusError = true;
            _mainVm.RequestUiRefresh();
            return;
        }

        try
        {
            var outPath = !string.IsNullOrWhiteSpace(outputPdfPath)
                ? outputPdfPath
                : Path.Combine(
                    Path.GetTempPath(),
                    "ScholarFlow-Scoped",
                    $"{Path.GetFileNameWithoutExtension(req.SourcePdfPath)}_Scoped_{DateTime.Now:yyyyMMdd_HHmmss}.pdf"
                );

            var instructionPrompt = await Task.Run(() =>
            {
                var pages = PdfQuizGrounder.ExtractGroundedPages(
                    req.SourcePdfPath,
                    req.Term,
                    req.SectionFilter,
                    req.StartPage,
                    req.EndPage,
                    req.SelectedRanges,
                    maxPages: 250
                );
                var pageNumbers = pages.Select(p => p.PageNumber).ToList();
                PdfQuizGrounder.ExportScopedPdfPages(req.SourcePdfPath, pageNumbers, outPath);
                var manifest = pages.Select(p => (p.PageNumber, p.SectionBreadcrumb)).ToList();
                return _llmService.BuildInstructionPromptForUploadedPdf(req, manifest);
            });

            if (clipboard is not null)
                await clipboard.SetTextAsync(instructionPrompt);

            StatusMessage = $"Saved {outPath} & copied upload prompt to clipboard.";
            IsStatusError = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scoped PDF export failed: {ex.Message}";
            IsStatusError = true;
        }
        _mainVm.RequestUiRefresh();
    }

    public void OpenManualJsonBridge()
    {
        var req = BuildCurrentRequest();
        if (!req.HasFileSources && (string.IsNullOrWhiteSpace(req.SourcePdfPath) || !File.Exists(req.SourcePdfPath)))
        {
            StatusMessage = "Please select a valid PDF file first.";
            IsStatusError = true;
            _mainVm.RequestUiRefresh();
            return;
        }

        IsManualJsonModalOpen = !IsManualJsonModalOpen;
        if (IsManualJsonModalOpen)
        {
            StatusMessage = "Building grounded prompt from selected PDF scope...";
            IsStatusError = false;
            _ = RebuildPromptPreviewAsync(req, updateStatus: true);
        }
        _mainVm.RequestUiRefresh();
    }

    public (string DisplayLabel, bool IsConfigured, string? MissingReason) GetActiveAiEngineStatus()
    {
        var settings = _mainVm.Settings;
        var activeProfile = !string.IsNullOrWhiteSpace(settings.ActiveAiProfileId)
            ? settings.SavedAiProfiles.FirstOrDefault(p =>
                string.Equals(p.Id, settings.ActiveAiProfileId, StringComparison.OrdinalIgnoreCase)
            )
            : null;

        string presetName = activeProfile?.Name
            ?? (string.IsNullOrWhiteSpace(settings.AiProviderPreset) ? "Custom" : settings.AiProviderPreset);
        string modelName = activeProfile?.ModelName ?? settings.AiModelName;
        string displayLabel = string.IsNullOrWhiteSpace(modelName)
            ? presetName
            : $"{presetName} · {modelName}";

        if (
            activeProfile?.IsOffline == true
            || string.Equals(settings.AiProviderPreset, "OfflineSmart", StringComparison.OrdinalIgnoreCase)
        )
        {
            return ("Offline Smart PDF Analyzer", true, null);
        }

        string baseUrl = activeProfile?.BaseUrl ?? settings.AiBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
            return (displayLabel, false, "Missing or invalid AI endpoint URL");

        if (string.IsNullOrWhiteSpace(modelName))
            return (displayLabel, false, "No model name specified");

        bool isLocal =
            baseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
            || baseUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase);

        if (!isLocal)
        {
            string? key = activeProfile?.ApiKey ?? settings.AiApiKey;
            bool hasEnvKey =
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY"))
                || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));

            if (string.IsNullOrWhiteSpace(key) && !hasEnvKey)
                return (displayLabel, false, "API key required for cloud provider");
        }

        return (displayLabel, true, null);
    }

    public async Task GenerateQuizNowAsync(bool forceOffline = false)
    {
        if (IsGenerating)
            return;

        var req = BuildCurrentRequest();
        if (
            forceOffline
            || string.Equals(
                _mainVm.Settings.AiProviderPreset,
                "OfflineSmart",
                StringComparison.OrdinalIgnoreCase
            )
        )
            req.UseOfflineHeuristicFallback = true;

        if (!req.UseOfflineHeuristicFallback)
        {
            var aiStatus = GetActiveAiEngineStatus();
            if (!aiStatus.IsConfigured)
            {
                StatusMessage = $"AI Engine not configured: {aiStatus.MissingReason}. Set it up below or use Offline / Web AI JSON.";
                IsStatusError = true;
                IsAiSettingsOpen = true;
                _mainVm.RequestUiRefresh();
                return;
            }
        }

        if (!req.HasFileSources && (string.IsNullOrWhiteSpace(req.SourcePdfPath) || !File.Exists(req.SourcePdfPath)))
        {
            StatusMessage = "No valid PDF selected for quiz generation.";
            IsStatusError = true;
            _mainVm.RequestUiRefresh();
            return;
        }

        IsGenerating = true;
        IsStatusError = false;
        StatusMessage = req.UseOfflineHeuristicFallback
            ? $"Analyzing {(req.HasFileSources ? $"{req.SourceFilePaths.Count} file(s)" : Path.GetFileName(req.SourcePdfPath))} and grounding statements..."
            : $"Requesting {req.QuestionCount} grounded questions from {req.ModelName}...";
        _mainVm.RequestUiRefresh();
        await Task.Yield();

        try
        {
            var session = await _llmService.GenerateQuizAsync(req);
            var qualityReport = QuizQualityCritic.Audit(session);
            AppendTargetQuizId = null;
            AppendTargetQuizTitle = null;
            UpsertSavedQuizInMemory(session);
            StartQuizSession(session);
            StatusMessage = qualityReport.HasIssues
                ? $"Generated '{session.Title}' ({session.Questions.Count} items · Critic: {qualityReport.SummaryText} — review in Organizer)."
                : $"Generated '{session.Title}' ({session.Questions.Count} grounded questions · All quality checks passed).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Generation failed: {ex.Message}";
            IsStatusError = true;
        }
        finally
        {
            IsGenerating = false;
            _mainVm.RequestUiRefresh();
        }
    }

    public async Task ImportPastedJsonQuizAsync(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            StatusMessage = "Paste the JSON output from any AI chat before importing.";
            IsStatusError = true;
            _mainVm.RequestUiRefresh();
            return;
        }

        var req = BuildCurrentRequest();
        IsGenerating = true;
        IsStatusError = false;
        StatusMessage = "Parsing JSON and grounding quotes to PDF pages...";
        _mainVm.RequestUiRefresh();
        await Task.Yield();

        try
        {
            var session = await _llmService.ImportQuizFromJsonAsync(req, rawJson);
            var qualityReport = QuizQualityCritic.Audit(session);
            AppendTargetQuizId = null;
            AppendTargetQuizTitle = null;
            IsManualJsonModalOpen = false;
            ManualJsonInput = string.Empty;
            UpsertSavedQuizInMemory(session);
            StartQuizSession(session);
            StatusMessage = qualityReport.HasIssues
                ? $"Imported {session.Questions.Count} questions · Critic: {qualityReport.SummaryText} — review in Organizer."
                : $"Imported and grounded {session.Questions.Count} questions from Web AI JSON · All quality checks passed.";
            IsStatusError = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"JSON Import error: {ex.Message}";
            IsStatusError = true;
        }
        finally
        {
            IsGenerating = false;
        }

        _mainVm.RequestUiRefresh();
    }

    private void UpsertSavedQuizInMemory(QuizSessionRecord session)
    {
        var list = SavedQuizzes.ToList();
        int existingIdx = list.FindIndex(q =>
            string.Equals(q.Id, session.Id, StringComparison.OrdinalIgnoreCase)
        );
        if (existingIdx >= 0)
            list[existingIdx] = session;
        else
            list.Insert(0, session);

        SavedQuizzes = list;
        var today = DateOnly.FromDateTime(DateTime.Today);
        AllDueReviewQuestions =
        [
            .. SavedQuizzes
                .SelectMany(q => q.Questions)
                .Where(qq =>
                    !qq.SrsDueDate.HasValue
                    || qq.SrsDueDate.Value <= today
                    || qq.LastAnsweredCorrect == false
                ),
        ];
        _mainVm.KnowledgeSearch.TriggerBackgroundIndex(forceReindex: false);
    }

    private QuizGenerationRequest BuildCurrentRequest()
    {
        var course = SelectedCourse ?? _mainVm.Courses.FirstOrDefault(c => !c.IsArchived);
        var settings = _mainVm.Settings;

        var excludes = new List<string>();
        if (!string.IsNullOrWhiteSpace(AppendTargetQuizId))
        {
            var targetQuiz = SavedQuizzes.FirstOrDefault(q =>
                string.Equals(q.Id, AppendTargetQuizId, StringComparison.OrdinalIgnoreCase)
            );
            if (targetQuiz is not null)
                excludes.AddRange(targetQuiz.Questions.Select(q => q.Prompt));
        }

        return new QuizGenerationRequest
        {
            BasinRoot = _mainVm.BasinRoot,
            CourseFolder = course?.FolderName ?? string.Empty,
            CourseTitle = course?.Title ?? "Course",
            Term = SelectedTerm,
            SourcePdfPath = SelectedPdfPath ?? string.Empty,
            SourceFilePaths = [.. GetSelectedFileSourcesInOrder()],
            IncludePdfWithFileSources = IsMixedSourceMode,
            StartPage = StartPage,
            EndPage = EndPage,
            SelectedRanges = GetSelectedPageRanges(),
            QuestionCount = QuestionCount,
            Preset = SelectedPreset,
            AllocationMode = AllocationMode,
            KindAllocations = new Dictionary<QuizQuestionKind, int>(KindAllocations),
            ModalityMode = ModalityMode,
            CognitiveMix = CognitiveMix,
            CognitiveAllocations = new Dictionary<CognitiveLevel, int>(CognitiveAllocations),
            SelectedArchetypes = [.. SelectedArchetypes],
            FocusWeakSpots = FocusWeakSpots,
            EnableContrastiveExplanations = EnableContrastiveExplanations,
            IncludeProgressiveHints = IncludeProgressiveHints,
            ExcludePromptsOrTopics = excludes,
            CustomInstructions = CustomPromptInstructions,
            AppendToQuizId = AppendTargetQuizId,
            ProviderBaseUrl = settings.AiBaseUrl,
            ApiKey = settings.AiApiKey,
            ModelName = settings.AiModelName,
            UseOfflineHeuristicFallback =
                BuilderOutputMode == QuizBuilderOutputMode.OfflineSmart
                || string.Equals(
                    settings.AiProviderPreset,
                    "OfflineSmart",
                    StringComparison.OrdinalIgnoreCase
                ),
        };
    }
}
