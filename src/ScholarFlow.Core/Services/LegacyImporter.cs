using System.Globalization;
using ScholarFlow.Core.Markdown;
using ScholarFlow.Core.Models;
using YamlDotNet.Serialization;

namespace ScholarFlow.Core.Services;

public sealed record LegacyImportSummary(
    int CoursesImported,
    int OngoingTasksImported,
    int MetadataEntriesMatched
);

/// <summary>
/// Non-destructively imports an existing School-Files semester folder (e.g. YR4_01)
/// and legacy task-manager metadata.yaml into .scholarflow/courses.json and per-course tasks.md.
/// </summary>
public sealed class LegacyImporter(BasinService? basinService = null)
{
    private readonly BasinService _basinService = basinService ?? new BasinService();

    public async Task<LegacyImportSummary> ImportAsync(
        string basinRoot,
        string? legacyMetadataYamlPath = null,
        CancellationToken ct = default
    )
    {
        await _basinService.InitializeOrOpenAsync(basinRoot, ct: ct);

        var yamlByRelPath = LoadLegacyMetadata(legacyMetadataYamlPath);
        var existingCourses = await _basinService.LoadCoursesAsync(basinRoot, ct);
        var usedAliases = existingCourses
            .Select(c => c.Alias)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .ToList();

        int coursesAdded = 0;
        int tasksImported = 0;
        int metadataMatched = 0;

        foreach (
            var dir in Directory
                .GetDirectories(basinRoot)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
        )
        {
            var folderName = Path.GetFileName(dir);
            if (folderName.StartsWith('.'))
                continue;

            var courseEntry = existingCourses.FirstOrDefault(c =>
                c.FolderName.Equals(folderName, StringComparison.OrdinalIgnoreCase)
            );

            if (courseEntry is null)
            {
                var displayTitle = folderName.Replace('_', ' ');
                var alias = CourseAliasGenerator.Generate(displayTitle, usedAliases);
                usedAliases.Add(alias);

                courseEntry = new BasinCourseEntry
                {
                    Title = displayTitle,
                    FolderName = folderName,
                    Alias = alias,
                };
                existingCourses.Add(courseEntry);
                coursesAdded++;
            }

            var ongoingDir = Path.Combine(dir, "ongoing");
            if (!Directory.Exists(ongoingDir))
                continue;

            var scopeKey = Database.ScholarFlowDatabase.GetLogicalTaskScopeKey(folderName);
            var existingTasks = await Database.ScholarFlowDatabase.LoadScopeTasksAsync(
                basinRoot,
                folderName,
                ct
            );
            var trackedNames = existingTasks
                .SelectMany(t => t.LinkedFiles)
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            bool changed = false;
            foreach (var filePath in Directory.EnumerateFiles(ongoingDir).OrderBy(f => f))
            {
                var fileName = Path.GetFileName(filePath);
                if (fileName.StartsWith('.') || trackedNames.Contains(fileName))
                    continue;

                var relFromBasin = $"{folderName}/ongoing/{fileName}";
                DateOnly? dueDate = null;
                int difficulty = 1;

                if (yamlByRelPath.TryGetValue(relFromBasin, out var meta))
                {
                    metadataMatched++;
                    difficulty = Math.Clamp(meta.Difficulty, 1, 5);
                    if (
                        !string.IsNullOrWhiteSpace(meta.DueDate)
                        && DateOnly.TryParseExact(
                            meta.DueDate,
                            "yyyy-MM-dd",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out var parsedDue
                        )
                    )
                    {
                        dueDate = parsedDue;
                    }
                }

                var priority =
                    difficulty >= 4 ? TaskPriority.P1
                    : difficulty == 3 ? TaskPriority.P2
                    : TaskPriority.P3;
                existingTasks.Add(
                    new TaskItem
                    {
                        Title = Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ').Trim(),
                        DueDate = dueDate,
                        Priority = priority,
                        Difficulty = difficulty,
                        CourseFolder = folderName,
                        CourseAlias = courseEntry.Alias,
                        LinkedFiles = [$"ongoing/{fileName}"],
                        SourceMarkdownPath = scopeKey,
                    }
                );

                tasksImported++;
                changed = true;
            }

            if (changed)
                await Database.ScholarFlowDatabase.SaveScopeTasksAsync(
                    basinRoot,
                    folderName,
                    existingTasks,
                    ct
                );
        }

        await _basinService.SaveCoursesAsync(basinRoot, existingCourses, ct);
        return new LegacyImportSummary(coursesAdded, tasksImported, metadataMatched);
    }

    private static Dictionary<string, LegacyYamlEntry> LoadLegacyMetadata(string? yamlPath)
    {
        if (string.IsNullOrWhiteSpace(yamlPath) || !File.Exists(yamlPath))
            return new Dictionary<string, LegacyYamlEntry>(StringComparer.OrdinalIgnoreCase);

        var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
        var raw = File.ReadAllText(yamlPath);
        var parsed = deserializer.Deserialize<Dictionary<string, LegacyYamlEntry>>(raw);
        return parsed is null
            ? new Dictionary<string, LegacyYamlEntry>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, LegacyYamlEntry>(parsed, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class LegacyYamlEntry
    {
        [YamlMember(Alias = "due_date")]
        public string? DueDate { get; set; }

        [YamlMember(Alias = "difficulty")]
        public int Difficulty { get; set; } = 1;
    }
}
