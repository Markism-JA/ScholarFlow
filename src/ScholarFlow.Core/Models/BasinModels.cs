namespace ScholarFlow.Core.Models;

public static class SyncProviderIds
{
    public const string None = "none";
    public const string StiElms = "sti-elms";
}

public enum CourseOrigin
{
    Synced,
    LocalSubject,
    KnowledgeCollection,
}

public sealed class BasinConfig
{
    public int SchemaVersion { get; set; } = 2;
    public string Name { get; set; } = "School Basin";
    public string ProviderId { get; set; } = SyncProviderIds.StiElms;
    public string AccountKey { get; set; } = "default";
    public bool BackgroundSyncEnabled { get; set; } = true;
    public string ElmsBaseUrl { get; set; } = "https://elms.sti.edu";
    public Dictionary<string, string> ProviderSettings { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public int FastPollIntervalMinutes { get; set; } = 15;
    public int FullSyncIntervalMinutes { get; set; } = 60;
    public bool AutoCompileMasterPdf { get; set; } = true;
    public List<string> Terms { get; set; } = ["Prelims", "Midterms", "Prefinals", "Finals"];
    public List<string> ExcludedCourseIds { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string GetProviderSetting(string key, string fallback = "")
    {
        if (ProviderSettings.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val))
            return val;
        if (
            string.Equals(key, "baseUrl", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(ElmsBaseUrl)
        )
            return ElmsBaseUrl;
        return fallback;
    }
}

/// <summary>
/// Provider-neutral discovered course metadata passed by sync plugins to <see cref="Services.BasinService"/>.
/// </summary>
public sealed record DiscoveredCourse(
    string CourseId,
    string Title,
    string FolderName,
    string? CourseCode = null,
    string? SectionCode = null,
    string? TermSuffix = null,
    string? CourseUrl = null,
    CourseOrigin Origin = CourseOrigin.Synced
);

public sealed class BasinCourseEntry
{
    public string? CourseId { get; set; }
    public CourseOrigin Origin { get; set; } = CourseOrigin.LocalSubject;
    public string Title { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public string? CourseCode { get; set; }
    public string? SectionCode { get; set; }
    public string? TermSuffix { get; set; }
    public string? CourseUrl { get; set; }
    public bool IsArchived { get; set; }

    public bool IsSyncableCourse =>
        !IsArchived
        && Origin != CourseOrigin.KnowledgeCollection
        && !string.IsNullOrWhiteSpace(CourseId)
        && !CourseId.StartsWith("local-", StringComparison.OrdinalIgnoreCase);

    public string EffectiveAlias =>
        !string.IsNullOrWhiteSpace(Alias)
            ? Alias
            : CourseAliasGenerator.Generate(!string.IsNullOrWhiteSpace(Title) ? Title : FolderName);

    public string DisplayTitle =>
        !string.IsNullOrWhiteSpace(Title) ? Title : FolderName.Replace('_', ' ');
}

public static class CourseAliasGenerator
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and",
        "or",
        "in",
        "of",
        "the",
        "for",
        "to",
        "a",
        "an",
        "with",
    };

    /// <summary>
    /// Generates a concise, unique hashtag alias (e.g. "sqa", "os", "net1", "ias") from a course title.
    /// </summary>
    public static string Generate(string titleOrFolder, IEnumerable<string>? existingAliases = null)
    {
        var used = new HashSet<string>(existingAliases ?? [], StringComparer.OrdinalIgnoreCase);

        // Well-known compact acronym/word heuristics
        var clean = titleOrFolder.Replace('_', ' ').Replace('-', ' ');
        var words = clean
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string([.. w.Where(char.IsLetterOrDigit)]))
            .Where(w => !string.IsNullOrEmpty(w) && !StopWords.Contains(w))
            .ToList();

        string candidate;
        if (words.Count == 0)
        {
            candidate = "course";
        }
        else if (words.Count == 1)
        {
            var w = words[0].ToLowerInvariant();
            candidate = w.Length <= 6 ? w : w[..Math.Min(5, w.Length)];
        }
        else
        {
            candidate = string.Concat(
                words.Select(w => char.IsDigit(w[0]) ? w : char.ToLowerInvariant(w[0]).ToString())
            );

            if (candidate.Length < 2)
                candidate = words[0].ToLowerInvariant()[..Math.Min(4, words[0].Length)];
        }

        if (!used.Contains(candidate))
            return candidate;

        for (int i = 2; ; i++)
        {
            var numbered = $"{candidate}{i}";
            if (!used.Contains(numbered))
                return numbered;
        }
    }
}
