using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScholarFlow.Core.Quiz;

public sealed record QuizJsonRecoveryResult(
    QuizGenerationResponseDto? Dto,
    bool WasSanitized,
    bool WasPartiallySalvaged,
    int DroppedItemCount,
    string? WarningSummary,
    string? ErrorMessage,
    string? ErrorExcerpt,
    string? TargetedFixPrompt
);

public sealed class QuizJsonParseException : InvalidOperationException
{
    public string ErrorExcerpt { get; }
    public string TargetedFixPrompt { get; }

    public QuizJsonParseException(
        string message,
        string errorExcerpt,
        string targetedFixPrompt,
        Exception? inner = null
    )
        : base(message, inner)
    {
        ErrorExcerpt = errorExcerpt;
        TargetedFixPrompt = targetedFixPrompt;
    }
}

/// <summary>
/// 3-layer zero-token local JSON recovery for LLM quiz responses:
/// 1) Direct strict/lenient deserialize.
/// 2) String-aware JSON sanitizer (fixes unescaped LaTeX backslashes like \implies, \frac, \text, \mathbb,
///    unescaped literal newlines/tabs inside strings, and trailing markdown fences).
/// 3) Brace-balanced per-question salvage parser for truncated or partially malformed "questions" arrays
///    so 96 valid items out of 100 are preserved instead of discarded.
/// Also builds surgical, low-token targeted fix prompts when 0 items can be recovered.
/// </summary>
public static partial class QuizJsonRecovery
{
    private static readonly JsonSerializerOptions LenientJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Common LaTeX commands starting with b, f, n, r, t that JSON would otherwise misread as \b \f \n \r \t
    private static readonly string[] LatexEscapeCollisionPrefixes =
    [
        "frac",
        "forall",
        "text",
        "textbf",
        "textit",
        "textrm",
        "times",
        "theta",
        "tau",
        "to",
        "top",
        "triangle",
        "tfrac",
        "tan",
        "tanh",
        "beta",
        "binom",
        "bmod",
        "bot",
        "big",
        "Big",
        "begin",
        "bar",
        "bullet",
        "because",
        "nabla",
        "neq",
        "ne",
        "nmid",
        "notin",
        "nexists",
        "neg",
        "nu",
        "rightarrow",
        "Rightarrow",
        "right",
        "rho",
        "rfloor",
        "rceil",
        "rangle",
    ];

    [GeneratedRegex(@"""quizTitle""\s*:\s*""(?<v>(?:\\.|[^""\\])*)""")]
    private static partial Regex QuizTitlePropRegex();

    [GeneratedRegex(@"""courseTerm""\s*:\s*""(?<v>(?:\\.|[^""\\])*)""")]
    private static partial Regex CourseTermPropRegex();

    public static QuizJsonRecoveryResult ParseWithRecovery(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            const string emptyMsg = "JSON input is empty.";
            return new QuizJsonRecoveryResult(
                null,
                false,
                false,
                0,
                null,
                emptyMsg,
                string.Empty,
                BuildTargetedFixPrompt(string.Empty, emptyMsg, null, null)
            );
        }

        var extracted = ExtractJsonPayload(rawText);

        // Layer 1: Direct deserialize (fast path)
        Exception? firstEx = null;
        try
        {
            var direct = JsonSerializer.Deserialize<QuizGenerationResponseDto>(
                extracted,
                LenientJsonOpts
            );
            if (direct is not null && direct.Questions.Count > 0)
            {
                return new QuizJsonRecoveryResult(
                    direct,
                    WasSanitized: false,
                    WasPartiallySalvaged: false,
                    DroppedItemCount: 0,
                    WarningSummary: null,
                    ErrorMessage: null,
                    ErrorExcerpt: null,
                    TargetedFixPrompt: null
                );
            }
        }
        catch (Exception ex)
        {
            firstEx = ex;
        }

        // Layer 2: Deterministic string-aware sanitizer (unescaped LaTeX backslashes, control chars, etc.)
        var sanitized = SanitizeJsonStringLiterals(extracted);
        Exception? secondEx = null;
        try
        {
            var repaired = JsonSerializer.Deserialize<QuizGenerationResponseDto>(
                sanitized,
                LenientJsonOpts
            );
            if (repaired is not null && repaired.Questions.Count > 0)
            {
                return new QuizJsonRecoveryResult(
                    repaired,
                    WasSanitized: true,
                    WasPartiallySalvaged: false,
                    DroppedItemCount: 0,
                    WarningSummary: "Auto-repaired unescaped JSON/LaTeX characters locally (0 extra tokens).",
                    ErrorMessage: null,
                    ErrorExcerpt: null,
                    TargetedFixPrompt: null
                );
            }
        }
        catch (Exception ex)
        {
            secondEx = ex;
        }

        // Layer 3: Per-question brace-balanced salvage parser (handles truncated tail or 1-2 bad items in a 100-item deck)
        var (salvagedDto, droppedCount) = TrySalvageQuestionsArray(sanitized);
        if (salvagedDto is not null && salvagedDto.Questions.Count > 0)
        {
            var warn =
                droppedCount > 0
                    ? $"Salvaged {salvagedDto.Questions.Count} valid question(s) locally ({droppedCount} truncated/malformed item(s) skipped — 0 extra tokens)."
                    : $"Salvaged {salvagedDto.Questions.Count} valid question(s) from truncated JSON locally (0 extra tokens).";

            return new QuizJsonRecoveryResult(
                salvagedDto,
                WasSanitized: true,
                WasPartiallySalvaged: true,
                DroppedItemCount: droppedCount,
                WarningSummary: warn,
                ErrorMessage: null,
                ErrorExcerpt: null,
                TargetedFixPrompt: null
            );
        }

        // All 3 local layers failed -> produce surgical error excerpt + compact targeted fix prompt
        var primaryEx = secondEx ?? firstEx;
        long? errLine = (primaryEx as JsonException)?.LineNumber;
        long? errPos = (primaryEx as JsonException)?.BytePositionInLine;
        var cleanErrMsg = primaryEx?.Message ?? "Could not parse any questions from the JSON payload.";
        var excerpt = ExtractErrorWindow(extracted, errLine, errPos);
        var fixPrompt = BuildTargetedFixPrompt(excerpt, cleanErrMsg, errLine, errPos);

        return new QuizJsonRecoveryResult(
            null,
            WasSanitized: true,
            WasPartiallySalvaged: false,
            DroppedItemCount: 0,
            WarningSummary: null,
            ErrorMessage: cleanErrMsg,
            ErrorExcerpt: excerpt,
            TargetedFixPrompt: fixPrompt
        );
    }

    public static string ExtractJsonPayload(string raw)
    {
        var text = (raw ?? string.Empty).Trim();

        // Strip markdown ```json ... ``` fences even if surrounded by conversational prose
        int fenceStart = text.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart >= 0)
        {
            int afterHeader = text.IndexOf('\n', fenceStart);
            if (afterHeader > fenceStart)
            {
                int fenceEnd = text.LastIndexOf("```", StringComparison.Ordinal);
                if (fenceEnd > afterHeader)
                    text = text[(afterHeader + 1)..fenceEnd].Trim();
                else
                    text = text[(afterHeader + 1)..].Trim();
            }
        }

        int startBrace = text.IndexOf('{');
        if (startBrace < 0)
            return text;

        // Walk string-aware brace depth from the root '{' to find its matching closing '}'.
        // If the JSON is truncated before the root object closes, keep everything from '{' onward
        // so Layer 3 can salvage complete items AND accurately count the truncated trailing item.
        bool inStr = false;
        bool esc = false;
        int depth = 0;
        for (int i = startBrace; i < text.Length; i++)
        {
            char c = text[i];
            if (inStr)
            {
                if (esc)
                {
                    esc = false;
                }
                else if (c == '\\')
                {
                    esc = true;
                }
                else if (c == '"')
                {
                    inStr = false;
                }
                continue;
            }

            if (c == '"')
            {
                inStr = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return text[startBrace..(i + 1)];
            }
        }

        return text[startBrace..];
    }

    /// <summary>
    /// Walks JSON character-by-character inside string literals:
    /// - Double-escapes invalid JSON backslash escapes (e.g. \implies, \mathbb, \mid, \leq, \sum, \int)
    /// - Protects LaTeX commands starting with b/f/n/r/t (e.g. \frac, \text, \theta, \beta, \neq, \rightarrow)
    /// - Escapes raw literal newlines/tabs/control characters inside strings
    /// </summary>
    public static string SanitizeJsonStringLiterals(string json)
    {
        if (string.IsNullOrEmpty(json))
            return json;

        var sb = new StringBuilder(json.Length + 64);
        bool inString = false;

        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];

            if (!inString)
            {
                sb.Append(c);
                if (c == '"')
                    inString = true;
                continue;
            }

            if (c == '"')
            {
                inString = false;
                sb.Append(c);
                continue;
            }

            if (c == '\\')
            {
                if (i + 1 >= json.Length)
                {
                    sb.Append(@"\\");
                    continue;
                }

                char next = json[i + 1];
                if (next is '"' or '\\' or '/')
                {
                    sb.Append('\\');
                    sb.Append(next);
                    i++;
                    continue;
                }

                if (next == 'u' && i + 5 < json.Length && IsHex4(json, i + 2))
                {
                    sb.Append(json, i, 6);
                    i += 5;
                    continue;
                }

                if (next is 'b' or 'f' or 'n' or 'r' or 't')
                {
                    // Check if this is actually a LaTeX command like \frac, \text, \beta, \neq, \rightarrow
                    if (StartsWithLatexCollisionCommand(json, i + 1))
                    {
                        sb.Append(@"\\");
                        continue;
                    }

                    sb.Append('\\');
                    sb.Append(next);
                    i++;
                    continue;
                }

                // Any other \X (e.g. \implies, \mid, \mathbb, \sum, \int, \leq, \sqrt, \, \;) is illegal in JSON -> double-escape!
                sb.Append(@"\\");
                continue;
            }

            // Raw unescaped control chars inside string literals are illegal in JSON
            if (c == '\r')
            {
                if (i + 1 < json.Length && json[i + 1] == '\n')
                    i++;
                sb.Append(@"\n");
                continue;
            }
            if (c == '\n')
            {
                sb.Append(@"\n");
                continue;
            }
            if (c == '\t')
            {
                sb.Append(@"\t");
                continue;
            }
            if (c < 0x20)
            {
                sb.Append(' ');
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static (QuizGenerationResponseDto? Dto, int DroppedCount) TrySalvageQuestionsArray(
        string json
    )
    {
        int qKeyIdx = json.IndexOf("\"questions\"", StringComparison.OrdinalIgnoreCase);
        if (qKeyIdx < 0)
            return (null, 0);

        int arrStart = json.IndexOf('[', qKeyIdx);
        if (arrStart < 0)
            return (null, 0);

        var salvaged = new List<GeneratedQuestionDto>();
        int dropped = 0;

        bool inString = false;
        bool escaped = false;
        int depth = 0;
        int objStart = -1;

        for (int i = arrStart + 1; i < json.Length; i++)
        {
            char c = json[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '{')
            {
                if (depth == 0)
                    objStart = i;
                depth++;
            }
            else if (c == '}')
            {
                if (depth > 0)
                {
                    depth--;
                    if (depth == 0 && objStart >= 0)
                    {
                        var itemJson = json[objStart..(i + 1)];
                        try
                        {
                            var q = JsonSerializer.Deserialize<GeneratedQuestionDto>(
                                itemJson,
                                LenientJsonOpts
                            );
                            if (q is not null && !string.IsNullOrWhiteSpace(q.Prompt))
                                salvaged.Add(q);
                            else
                                dropped++;
                        }
                        catch
                        {
                            dropped++;
                        }
                        objStart = -1;
                    }
                }
            }
            else if (c == ']' && depth == 0)
            {
                break;
            }
        }

        // Count a trailing truncated object if depth > 0 at EOF
        if (depth > 0 && objStart >= 0)
            dropped++;

        if (salvaged.Count == 0)
            return (null, dropped);

        var titleMatch = QuizTitlePropRegex().Match(json);
        var termMatch = CourseTermPropRegex().Match(json);

        var dto = new QuizGenerationResponseDto
        {
            QuizTitle = titleMatch.Success ? titleMatch.Groups["v"].Value : string.Empty,
            CourseTerm = termMatch.Success ? termMatch.Groups["v"].Value : string.Empty,
            Questions = salvaged,
        };
        return (dto, dropped);
    }

    private static string ExtractErrorWindow(string json, long? lineNumber, long? bytePos)
    {
        if (string.IsNullOrWhiteSpace(json))
            return string.Empty;

        var lines = json.Replace("\r\n", "\n").Split('\n');
        if (lineNumber.HasValue && lineNumber.Value >= 0 && lineNumber.Value < lines.Length)
        {
            int idx = (int)lineNumber.Value;
            int from = Math.Max(0, idx - 2);
            int to = Math.Min(lines.Length - 1, idx + 2);
            var sb = new StringBuilder();
            for (int l = from; l <= to; l++)
            {
                string marker = l == idx ? ">>> " : "    ";
                var content = lines[l].Length > 200 ? lines[l][..200] + "..." : lines[l];
                sb.AppendLine($"{marker}L{l + 1}: {content}");
            }
            return sb.ToString().TrimEnd();
        }

        return json.Length > 320 ? json[..320] + "..." : json;
    }

    private static string BuildTargetedFixPrompt(
        string errorExcerpt,
        string errorMessage,
        long? lineNumber,
        long? bytePos
    )
    {
        var loc = lineNumber.HasValue
            ? $" (around line {lineNumber.Value + 1}{(bytePos.HasValue ? $", col {bytePos.Value + 1}" : "")})"
            : string.Empty;

        var excerptBlock = string.IsNullOrWhiteSpace(errorExcerpt)
            ? string.Empty
            : $"\nBroken JSON excerpt{loc}:\n```json\n{errorExcerpt}\n```\n";

        return $"""
            The JSON you generated failed strict parsing with this error{loc}:
            {errorMessage}
            {excerptBlock}
            Please fix ONLY the JSON syntax issue without regenerating the entire quiz from scratch:
            1. Double-escape all LaTeX backslashes inside JSON strings (e.g. use "\\frac", "\\implies", "\\text", "\\mathbb", "\\sqrt" instead of single "\").
            2. Escape any inner double quotes inside text or code snippets as \".
            3. Do not leave unescaped raw newlines inside JSON strings (use \n) and ensure all arrays/objects are properly closed.
            4. Return ONLY the corrected valid JSON object (no markdown commentary outside the JSON).
            """;
    }

    private static bool StartsWithLatexCollisionCommand(string s, int startIdx)
    {
        foreach (var prefix in LatexEscapeCollisionPrefixes)
        {
            if (
                startIdx + prefix.Length <= s.Length
                && string.Compare(s, startIdx, prefix, 0, prefix.Length, StringComparison.Ordinal)
                    == 0
            )
            {
                int after = startIdx + prefix.Length;
                if (after >= s.Length || !char.IsLetter(s[after]))
                    return true;
            }
        }
        return false;
    }

    private static bool IsHex4(string s, int start)
    {
        for (int k = 0; k < 4; k++)
        {
            char ch = s[start + k];
            bool ok =
                (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');
            if (!ok)
                return false;
        }
        return true;
    }
}
