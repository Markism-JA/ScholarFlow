using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScholarFlow.Core.Database;
using ScholarFlow.Core.Models;

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
        IReadOnlyList<string>? imageDataUris = null)
    {
        // Multimodal (vision) user turn: text part + one image_url part per image.
        object userContent = userPrompt;
        if (imageDataUris is { Count: > 0 })
        {
            var parts = new List<object> { new { type = "text", text = userPrompt } };
            parts.AddRange(imageDataUris.Select(uri => (object)new
            {
                type = "image_url",
                image_url = new { url = uri },
            }));
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
                        new { role = "user", content = userContent }
                    }
                }
                : new
                {
                    model = modelName,
                    temperature = 0.25,
                    messages = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userContent }
                    }
                };

            var jsonBody = JsonSerializer.Serialize(payload);
            reqMsg.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            using var resp = await SharedHttp.SendAsync(reqMsg, ct);
            var respText = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                if (includeJsonResponseFormat && (int)resp.StatusCode == 400)
                    continue;

                throw new InvalidOperationException($"AI endpoint returned HTTP {(int)resp.StatusCode}: {respText}");
            }

            using var doc = JsonDocument.Parse(respText);
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var contentEl))
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

        if (u.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ||
            u.EndsWith("/openai", StringComparison.OrdinalIgnoreCase))
        {
            return $"{u}/chat/completions";
        }

        return $"{u}/v1/chat/completions";
    }

    public static QuizGenerationResponseDto ParseQuizResponseJson(string rawText)
    {
        var cleaned = ExtractJsonObjectSubstring(rawText);
        var dto = JsonSerializer.Deserialize<QuizGenerationResponseDto>(cleaned, JsonOpts);
        if (dto is null || dto.Questions.Count == 0)
            throw new InvalidOperationException("Could not parse any questions from the JSON payload.");
        return dto;
    }

    private static string ExtractJsonObjectSubstring(string raw)
    {
        var text = (raw ?? string.Empty).Trim();

        // Strip markdown ```json ... ``` fences if present
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewline = text.IndexOf('\n');
            if (firstNewline > 0)
                text = text[(firstNewline + 1)..];
            int lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0)
                text = text[..lastFence];
            text = text.Trim();
        }

        int startBrace = text.IndexOf('{');
        int endBrace = text.LastIndexOf('}');
        if (startBrace >= 0 && endBrace > startBrace)
            return text[startBrace..(endBrace + 1)];

        return text;
    }

}
