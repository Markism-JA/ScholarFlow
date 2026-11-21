namespace ScholarFlow.Core.Knowledge;

public enum StudySourceKind
{
    Unknown,
    Pdf,
    Code,
    Markdown,
    PlainText,
    Image,
}

public sealed record StudySourceDescriptor(
    string FilePath,
    string CourseFolder,
    string DisplayName,
    StudySourceKind Kind,
    string Language,
    long SizeBytes
);

public sealed record SourceLineMatch(
    int StartLine,
    int EndLine,
    string SectionTitle,
    string MatchedText
);

/// <summary>
/// Classifies local course files into first-class Knowledge Hub study source kinds and language labels.
/// </summary>
public static class StudySourceClassifier
{
    private static readonly Dictionary<string, string> CodeExtensions = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        [".cs"] = "csharp",
        [".c"] = "c",
        [".h"] = "c",
        [".cpp"] = "cpp",
        [".cc"] = "cpp",
        [".cxx"] = "cpp",
        [".hpp"] = "cpp",
        [".py"] = "python",
        [".js"] = "javascript",
        [".mjs"] = "javascript",
        [".jsx"] = "javascript",
        [".ts"] = "typescript",
        [".tsx"] = "typescript",
        [".java"] = "java",
        [".kt"] = "kotlin",
        [".sql"] = "sql",
        [".rs"] = "rust",
        [".go"] = "go",
        [".sh"] = "bash",
        [".bash"] = "bash",
        [".zsh"] = "bash",
        [".ps1"] = "powershell",
        [".html"] = "html",
        [".htm"] = "html",
        [".css"] = "css",
        [".json"] = "json",
        [".yaml"] = "yaml",
        [".yml"] = "yaml",
        [".xml"] = "xml",
        [".php"] = "php",
        [".rb"] = "ruby",
        [".swift"] = "swift",
        [".asm"] = "asm",
        [".s"] = "asm",
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".webp",
        ".gif",
        ".bmp",
    };

    public static (StudySourceKind Kind, string Language) Classify(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return (StudySourceKind.Unknown, string.Empty);

        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrWhiteSpace(ext))
            return (StudySourceKind.Unknown, string.Empty);

        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return (StudySourceKind.Pdf, "pdf");

        if (ext.Equals(".md", StringComparison.OrdinalIgnoreCase) || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
            return (StudySourceKind.Markdown, "markdown");

        if (ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) || ext.Equals(".log", StringComparison.OrdinalIgnoreCase))
            return (StudySourceKind.PlainText, "text");

        if (ImageExtensions.Contains(ext))
            return (StudySourceKind.Image, ext.TrimStart('.').ToLowerInvariant());

        if (CodeExtensions.TryGetValue(ext, out var lang))
            return (StudySourceKind.Code, lang);

        return (StudySourceKind.Unknown, ext.TrimStart('.').ToLowerInvariant());
    }

    public static bool IsViewableInSourceViewer(string? filePath)
    {
        var (kind, _) = Classify(filePath);
        return kind is StudySourceKind.Code
            or StudySourceKind.Markdown
            or StudySourceKind.PlainText
            or StudySourceKind.Image;
    }

    public static StudySourceDescriptor Describe(string filePath, string courseFolder = "")
    {
        var (kind, lang) = Classify(filePath);
        long size = 0;
        try
        {
            if (File.Exists(filePath))
                size = new FileInfo(filePath).Length;
        }
        catch
        {
            // Ignore unreadable metadata
        }

        return new StudySourceDescriptor(
            filePath,
            courseFolder,
            Path.GetFileName(filePath),
            kind,
            lang,
            size
        );
    }
}
