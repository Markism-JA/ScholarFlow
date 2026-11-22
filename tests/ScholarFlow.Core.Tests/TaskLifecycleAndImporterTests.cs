using ScholarFlow.Core.Database;
using ScholarFlow.Core.Markdown;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Services;

namespace ScholarFlow.Core.Tests;

public sealed class TaskLifecycleAndImporterTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"sf-core-test-{Guid.NewGuid():N}");

    public TaskLifecycleAndImporterTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task SetCompletedAsync_MovesLinkedFileFromOngoingToCompletedAndUpdatesMarkdown()
    {
        var basin = new BasinService();
        await basin.InitializeOrOpenAsync(_tempRoot);
        await basin.SaveCoursesAsync(_tempRoot, [
            new BasinCourseEntry { Title = "Software Quality Assurance", FolderName = "Software_Quality_Assurance", Alias = "sqa" }
        ]);

        var ongoingDir = Path.Combine(_tempRoot, "Software_Quality_Assurance", "ongoing");
        Directory.CreateDirectory(ongoingDir);
        var labPdf = Path.Combine(ongoingDir, "Lab_1.pdf");
        await File.WriteAllTextAsync(labPdf, "pdf");

        var task = new TaskItem
        {
            Title = "Lab 1",
            CourseFolder = "Software_Quality_Assurance",
            CourseAlias = "sqa",
            LinkedFiles = ["ongoing/Lab_1.pdf"],
        };
        await ScholarFlowDatabase.UpsertTaskAsync(_tempRoot, task);

        var lifecycle = new TaskLifecycleService();
        await lifecycle.SetCompletedAsync(_tempRoot, task, isCompleted: true, new DateOnly(2026, 11, 19));

        Assert.False(File.Exists(labPdf));
        Assert.True(File.Exists(Path.Combine(_tempRoot, "Software_Quality_Assurance", "completed", "Lab_1.pdf")));

        var reloaded = await ScholarFlowDatabase.LoadScopeTasksAsync(_tempRoot, "Software_Quality_Assurance");
        Assert.Single(reloaded);
        Assert.True(reloaded[0].IsCompleted);
        Assert.Equal(new DateOnly(2026, 11, 19), reloaded[0].CompletedDate);
        Assert.Equal(["completed/Lab_1.pdf"], reloaded[0].LinkedFiles);
    }

    [Fact]
    public async Task SqliteCanonical_MigratesLegacyTasksMdOnceAndSupportsMarkdownExport()
    {
        var basin = new BasinService();
        await basin.InitializeOrOpenAsync(_tempRoot);
        var courses = new[]
        {
            new BasinCourseEntry { Title = "Operating Systems", FolderName = "Operating_Systems", Alias = "os" }
        };
        await basin.SaveCoursesAsync(_tempRoot, courses);

        // Simulate a legacy tasks.md created by an older version
        var legacyPath = BasinService.GetCourseTasksPath(_tempRoot, "Operating_Systems");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        await File.WriteAllTextAsync(legacyPath, "# Operating Systems\n\n- [ ] Legacy Paging Lab 📅 2026-12-01 #os\n");

        var loaded = await ScholarFlowDatabase.SyncAndLoadAllTasksAsync(_tempRoot, courses);
        var migrated = Assert.Single(loaded);
        Assert.Equal("Legacy Paging Lab", migrated.Title);

        // Export one-way Markdown snapshot
        var exportPath = Path.Combine(_tempRoot, "tasks_snapshot.md");
        await ScholarFlowDatabase.ExportAllTasksToMarkdownAsync(_tempRoot, courses, exportPath);
        Assert.True(File.Exists(exportPath));
        var exported = await File.ReadAllTextAsync(exportPath);
        Assert.Contains("Legacy Paging Lab", exported, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyImporter_ImportsOngoingFilesAndMetadataYaml()
    {
        var ongoingDir = Path.Combine(_tempRoot, "Network_Technology", "ongoing");
        Directory.CreateDirectory(ongoingDir);
        await File.WriteAllTextAsync(Path.Combine(ongoingDir, "01_Lab_1.pdf"), "lab");

        var yamlPath = Path.Combine(_tempRoot, "metadata.yaml");
        await File.WriteAllTextAsync(yamlPath, """
            Network_Technology/ongoing/01_Lab_1.pdf:
              due_date: '2026-11-28'
              difficulty: 4
            """);

        var importer = new LegacyImporter();
        var summary = await importer.ImportAsync(_tempRoot, yamlPath);

        Assert.Equal(1, summary.CoursesImported);
        Assert.Equal(1, summary.OngoingTasksImported);
        Assert.Equal(1, summary.MetadataEntriesMatched);

        var tasks = await new BasinService().LoadAllTasksAsync(_tempRoot);
        Assert.Single(tasks);
        Assert.Equal(new DateOnly(2026, 11, 28), tasks[0].DueDate);
        Assert.Equal(4, tasks[0].Difficulty);
        Assert.Equal(TaskPriority.P1, tasks[0].Priority);
    }

    [Fact]
    public async Task ImportAndDeleteCourseFolder_SupportsInBasinExternalAndSafeDelete()
    {
        var basinService = new BasinService();

        // 1. In-Basin folder registered in-place
        var inBasinDir = Path.Combine(_tempRoot, "Linear_Algebra");
        Directory.CreateDirectory(inBasinDir);
        await File.WriteAllTextAsync(Path.Combine(inBasinDir, "notes.txt"), "eigenvalues");

        var inPlace = await basinService.ImportCourseFolderAsync(_tempRoot, inBasinDir);
        Assert.Equal("Linear_Algebra", inPlace.FolderName);
        Assert.Equal("Linear Algebra", inPlace.Title);
        Assert.True(File.Exists(Path.Combine(_tempRoot, "Linear_Algebra", "notes.txt")));

        // 2. External folder copied into Basin
        var externalRoot = Path.Combine(Path.GetTempPath(), $"sf-ext-course-{Guid.NewGuid():N}");
        var externalCourse = Path.Combine(externalRoot, "Discrete_Math");
        Directory.CreateDirectory(externalCourse);
        await File.WriteAllTextAsync(Path.Combine(externalCourse, "syllabus.txt"), "graphs");
        try
        {
            var imported = await basinService.ImportCourseFolderAsync(
                _tempRoot,
                externalCourse,
                customTitle: "Discrete Mathematics",
                customAlias: "dmath"
            );
            Assert.Equal("dmath", imported.Alias);
            Assert.True(
                File.Exists(Path.Combine(_tempRoot, imported.FolderName, "syllabus.txt"))
            );

            // 3. Remove course without deleting folder
            await basinService.DeleteCourseAsync(_tempRoot, inPlace, deleteFolderFromDisk: false);
            Assert.True(Directory.Exists(inBasinDir));

            // 4. Delete course & folder from disk
            var importedDir = Path.Combine(_tempRoot, imported.FolderName);
            await basinService.DeleteCourseAsync(_tempRoot, imported, deleteFolderFromDisk: true);
            Assert.False(Directory.Exists(importedDir));

            var remaining = await basinService.LoadCoursesAsync(_tempRoot);
            Assert.Empty(remaining);
        }
        finally
        {
            if (Directory.Exists(externalRoot))
                Directory.Delete(externalRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FileImportHelper_SupportsCopyMoveSymlinkAndSelfDuplicateGuard()
    {
        var srcDir = Path.Combine(_tempRoot, "src");
        var dstDir = Path.Combine(_tempRoot, "dst");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir);

        var copySrc = Path.Combine(srcDir, "chapter1.pdf");
        var moveSrc = Path.Combine(srcDir, "chapter2.pdf");
        var linkSrc = Path.Combine(srcDir, "textbook.pdf");
        await File.WriteAllTextAsync(copySrc, "ch1");
        await File.WriteAllTextAsync(moveSrc, "ch2");
        await File.WriteAllTextAsync(linkSrc, "big-book");

        // 1. Copy
        var copied = FileImportHelper.ImportItem(copySrc, dstDir, FileImportMode.Copy, out _);
        Assert.NotNull(copied);
        Assert.True(File.Exists(copySrc));
        Assert.True(File.Exists(copied!));

        // 2. Self-duplicate guard (importing file already in dstDir returns null)
        var skipped = FileImportHelper.ImportItem(copied!, dstDir, FileImportMode.Copy, out _);
        Assert.Null(skipped);

        // 3. Move
        var moved = FileImportHelper.ImportItem(moveSrc, dstDir, FileImportMode.Move, out _);
        Assert.NotNull(moved);
        Assert.False(File.Exists(moveSrc));
        Assert.True(File.Exists(moved!));

        // 4. Symlink (on Linux/macOS creates a real symlink; on restricted Windows falls back cleanly to copy)
        var linked = FileImportHelper.ImportItem(
            linkSrc,
            dstDir,
            FileImportMode.Symlink,
            out bool fellBack
        );
        Assert.NotNull(linked);
        Assert.True(File.Exists(linked!));
        if (!fellBack)
        {
            Assert.True(FileImportHelper.IsSymbolicLink(linked!));
            Assert.False(FileImportHelper.IsBrokenSymbolicLink(linked!));

            File.Delete(linkSrc);
            Assert.True(FileImportHelper.IsBrokenSymbolicLink(linked!));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }
}
