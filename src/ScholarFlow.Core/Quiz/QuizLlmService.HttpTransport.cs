using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ScholarFlow.Core.Quiz;

public sealed partial class QuizLlmService
{
    private static async Task<string> CallOpenAiCompatibleChatAsync(
        string endpointUrl,
        string? apiKey,
        string modelName,
        string systemPrompt,
        string userPrompt,
        CancellationToken ct,
        IReadOnlyList<string>? imageDataUris = null
    )
    {
        // Multimodal (vision) user turn: text part + one image_url part per image.
        object userContent = userPrompt;
        if (imageDataUris is { Count: > 0 })
        {
            var parts = new List<object> { new { type = "text", text = userPrompt } };
            parts.AddRange(
                imageDataUris.Select(uri =>
                    (object)new { type = "image_url", image_url = new { url = uri } }
                )
            );
            userContent = parts;
        }

        var normalizedUrl = NormalizeChatCompletionsEndpoint(endpointUrl);

        // Try with response_format: json_object first, fallback without response_format if provider rejects it
        foreach (bool includeJsonResponseFormat in new[] { true, false })
        {
            using var reqMsg = new HttpRequestMessage(HttpMethod.Post, normalizedUrl);
            if (!string.IsNullOrWhiteSpace(apiKey))
                reqMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            object payload = includeJsonResponseFormat
                ? new
                {
                    model = modelName,
                    temperature = 0.25,
                    response_format = new { type = "json_object" },
                    messages = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userContent },
                    },
                }
                : new
                {
                    model = modelName,
                    temperature = 0.25,
                    messages = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userContent },
                    },
                };

            var jsonBody = JsonSerializer.Serialize(payload);
            reqMsg.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            using var resp = await SharedHttp.SendAsync(reqMsg, ct);
            var respText = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                if (includeJsonResponseFormat && (int)resp.StatusCode == 400)
                    continue;

                throw new InvalidOperationException(
                    $"AI endpoint returned HTTP {(int)resp.StatusCode}: {respText}"
                );
            }

            using var doc = JsonDocument.Parse(respText);
            if (
                doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var contentEl)
            )
            {
                return contentEl.GetString() ?? string.Empty;
            }

            return respText;
        }

        throw new InvalidOperationException("Failed to receive a valid chat completion response.");
    }

    private static string NormalizeChatCompletionsEndpoint(string rawUrl)
    {
        var u = (rawUrl ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(u))
            return "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";

        if (u.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return u;

        if (
            u.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            || u.EndsWith("/openai", StringComparison.OrdinalIgnoreCase)
        )
        {
            return $"{u}/chat/completions";
        }

        return $"{u}/v1/chat/completions";
    }

    public static QuizJsonRecoveryResult ParseQuizResponseJsonWithRecovery(string rawText) =>
        QuizJsonRecovery.ParseWithRecovery(rawText);

    public static QuizGenerationResponseDto ParseQuizResponseJson(string rawText)
    {
        var recovery = QuizJsonRecovery.ParseWithRecovery(rawText);
        if (recovery.Dto is not null && recovery.Dto.Questions.Count > 0)
            return recovery.Dto;

        throw new QuizJsonParseException(
            recovery.ErrorMessage ?? "Could not parse any questions from the JSON payload.",
            recovery.ErrorExcerpt ?? string.Empty,
            recovery.TargetedFixPrompt ?? string.Empty
        );
    }

    /// <summary>
    /// Parses an AI API response using 3-layer zero-token local recovery (direct -> LaTeX/control-char sanitizer ->
    /// per-question brace-balanced salvage). Only if 0 items can be salvaged locally does it perform at most ONE
    /// compact syntax-repair API call without re-sending the source PDF/files.
    /// </summary>
    private static async Task<QuizJsonRecoveryResult> ParseOrSurgicallyRepairApiResponseAsync(
        string rawResponse,
        string endpointUrl,
        string? apiKey,
        string modelName,
        CancellationToken ct
    )
    {
        var recovery = QuizJsonRecovery.ParseWithRecovery(rawResponse);
        if (recovery.Dto is not null && recovery.Dto.Questions.Count > 0)
            return recovery;

        const string repairSystemPrompt =
            "You are a strict JSON syntax repair utility. Fix ONLY the JSON syntax or escaping errors in the provided quiz JSON payload and return ONLY valid JSON preserving the original questions. Do not add commentary.";

        var boundedRaw = rawResponse.Length > 24_000 ? rawResponse[..24_000] : rawResponse;
        var repairUserPrompt = $"""
            {recovery.TargetedFixPrompt}

            Malformed JSON payload to repair:
            ```json
            {boundedRaw}
            ```
            """;

        try
        {
            var repairedRaw = await CallOpenAiCompatibleChatAsync(
                endpointUrl,
                apiKey,
                modelName,
                repairSystemPrompt,
                repairUserPrompt,
                ct
            );
            var repaired = QuizJsonRecovery.ParseWithRecovery(repairedRaw);
            if (repaired.Dto is not null && repaired.Dto.Questions.Count > 0)
            {
                return repaired with
                {
                    WarningSummary =
                        repaired.WarningSummary
                        ?? "Recovered malformed AI response with 1 compact syntax-repair pass (source content not re-sent).",
                };
            }
        }
        catch
        {
            // Fall through and throw the original structured parse exception with excerpt + targeted fix prompt.
        }

        throw new QuizJsonParseException(
            recovery.ErrorMessage ?? "Could not parse any questions from the AI response.",
            recovery.ErrorExcerpt ?? string.Empty,
            recovery.TargetedFixPrompt ?? string.Empty
        );
    }
}
