using System.Text.Json;

namespace ScholarFlow.Core.Pdf;

public sealed record SavedPdfSection(
    string Id,
    string Title,
    int StartPage,
    int EndPage,
    string? RangeExpression = null,
    string? Term = null
);

/// <summary>
/// Non-destructive per-basin sidecar store for user-created custom PDF sections/bookmarks
/// (.scholarflow/pdf_sections.json), enabling TOC-less or poorly-bookmarked PDFs to work
/// identically to structured Master PDFs across sessions.
/// </summary>
public static class PdfCustomSectionStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static string GetStorePath(string basinRoot) =>
        Path.Combine(basinRoot, ".scholarflow", "pdf_sections.json");

    private static string NormalizePdfKey(string basinRoot, string pdfPath)
    {
        if (string.IsNullOrWhiteSpace(pdfPath))
            return string.Empty;

        try
        {
            if (!string.IsNullOrWhiteSpace(basinRoot))
            {
                var rel = Path.GetRelativePath(basinRoot, pdfPath);
                if (!rel.StartsWith("..", StringComparison.Ordinal))
                    return rel.Replace('\\', '/');
            }
        }
        catch
        {
            // Fall back to filename key
        }

        return Path.GetFileName(pdfPath);
    }

    public static IReadOnlyList<SavedPdfSection> LoadSections(string basinRoot, string pdfPath)
    {
        if (string.IsNullOrWhiteSpace(basinRoot) || string.IsNullOrWhiteSpace(pdfPath))
            return [];

        var path = GetStorePath(basinRoot);
        if (!File.Exists(path))
            return [];

        try
        {
            var dict = LoadAll(path);
            var key = NormalizePdfKey(basinRoot, pdfPath);
            return dict.TryGetValue(key, out var list) ? list : [];
        }
        catch
        {
            return [];
        }
    }

    public static SavedPdfSection AddSection(
        string basinRoot,
        string pdfPath,
        string title,
        int startPage,
        int endPage,
        string? rangeExpression = null,
        string? term = null
    )
    {
        var path = GetStorePath(basinRoot);
        var dict = LoadAll(path);
        var key = NormalizePdfKey(basinRoot, pdfPath);

        if (!dict.TryGetValue(key, out var list))
        {
            list = [];
            dict[key] = list;
        }

        int s = Math.Max(1, Math.Min(startPage, endPage));
        int e = Math.Max(s, Math.Max(startPage, endPage));
        var created = new SavedPdfSection(
            Id: Guid.NewGuid().ToString("N")[..10],
            Title: title.Trim(),
            StartPage: s,
            EndPage: e,
            RangeExpression: string.IsNullOrWhiteSpace(rangeExpression)
                ? null
                : rangeExpression.Trim(),
            Term: string.IsNullOrWhiteSpace(term) ? null : term.Trim()
        );

        list.Add(created);
        list.Sort((a, b) => a.StartPage.CompareTo(b.StartPage));
        SaveAll(path, dict);
        return created;
    }

    public static bool DeleteSection(string basinRoot, string pdfPath, string sectionId)
    {
        if (
            string.IsNullOrWhiteSpace(basinRoot)
            || string.IsNullOrWhiteSpace(pdfPath)
            || string.IsNullOrWhiteSpace(sectionId)
        )
            return false;

        var path = GetStorePath(basinRoot);
        if (!File.Exists(path))
            return false;

        var dict = LoadAll(path);
        var key = NormalizePdfKey(basinRoot, pdfPath);
        if (!dict.TryGetValue(key, out var list))
            return false;

        int removed = list.RemoveAll(s =>
            string.Equals(s.Id, sectionId, StringComparison.OrdinalIgnoreCase)
        );
        if (removed <= 0)
            return false;

        if (list.Count == 0)
            dict.Remove(key);

        SaveAll(path, dict);
        return true;
    }

    private static Dictionary<string, List<SavedPdfSection>> LoadAll(string storePath)
    {
        if (!File.Exists(storePath))
            return new(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(storePath);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, List<SavedPdfSection>>>(
                json,
                JsonOpts
            );
            return parsed is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveAll(string storePath, Dictionary<string, List<SavedPdfSection>> dict)
    {
        var dir = Path.GetDirectoryName(storePath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(storePath, JsonSerializer.Serialize(dict, JsonOpts));
    }
}
