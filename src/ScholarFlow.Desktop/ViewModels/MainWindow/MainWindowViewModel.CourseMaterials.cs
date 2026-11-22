using System.Text.RegularExpressions;
using ScholarFlow.Core.Database;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Pdf;
using ScholarFlow.Core.Services;

namespace ScholarFlow.Desktop.ViewModels;

/// <summary>
/// MainWindowViewModel partial: Comprehensive Modules &amp; Handouts file/folder organization
/// (create/rename/reorder/delete folders, import/rename/move/delete files, search/filter,
/// and automatic <c>packet.json</c> path synchronization).
/// </summary>
public sealed partial class MainWindowViewModel
{
    [GeneratedRegex(@"^\d{2}_+")]
    private static partial Regex StripNumericPrefaceRegex();

    public string MaterialsSearchQuery { get; private set; } = string.Empty;
    public string MaterialsTypeFilter { get; private set; } = "All";
    public FileImportMode MaterialsImportMode { get; private set; } = FileImportMode.Copy;
    public HashSet<string> CollapsedMaterialFolders { get; } =
        new(StringComparer.OrdinalIgnoreCase);
    public bool IsNewMaterialFolderBarOpen { get; private set; }
    public string? RenamingMaterialPath { get; private set; }
    public string? ConfirmingDeleteMaterialPath { get; private set; }

    private void ResetCourseMaterialsUiState()
    {
        MaterialsSearchQuery = string.Empty;
        MaterialsTypeFilter = "All";
        CollapsedMaterialFolders.Clear();
        IsNewMaterialFolderBarOpen = false;
        RenamingMaterialPath = null;
        ConfirmingDeleteMaterialPath = null;
    }

    public void SetMaterialsSearchQueryQuiet(string query)
    {
        MaterialsSearchQuery = query?.Trim() ?? string.Empty;
    }

    public void SetMaterialsSearchQuery(string query)
    {
        var next = query?.Trim() ?? string.Empty;
        if (string.Equals(MaterialsSearchQuery, next, StringComparison.Ordinal))
            return;

        MaterialsSearchQuery = next;
        NotifyStateChanged(UiChangeKind.SearchOrTaskList);
    }

    public void SetMaterialsTypeFilter(string filter)
    {
        if (string.Equals(MaterialsTypeFilter, filter, StringComparison.OrdinalIgnoreCase))
            return;

        MaterialsTypeFilter = filter;
        NotifyStateChanged(UiChangeKind.SearchOrTaskList);
    }

    public void SetMaterialsImportMode(FileImportMode mode)
    {
        if (MaterialsImportMode == mode)
            return;

        MaterialsImportMode = mode;
        NotifyStateChanged(UiChangeKind.SearchOrTaskList);
    }

    public void ToggleMaterialFolderCollapsed(string folderKey)
    {
        if (!CollapsedMaterialFolders.Remove(folderKey))
            CollapsedMaterialFolders.Add(folderKey);
        NotifyStateChanged(UiChangeKind.SearchOrTaskList);
    }

    public void SetAllMaterialFoldersCollapsed(IEnumerable<string> folderKeys, bool collapsed)
    {
        CollapsedMaterialFolders.Clear();
        if (collapsed)
        {
            foreach (var key in folderKeys)
                CollapsedMaterialFolders.Add(key);
        }
        NotifyStateChanged(UiChangeKind.SearchOrTaskList);
    }

    public void ToggleNewMaterialFolderBar()
    {
        IsNewMaterialFolderBarOpen = !IsNewMaterialFolderBarOpen;
        RenamingMaterialPath = null;
        ConfirmingDeleteMaterialPath = null;
        NotifyStateChanged(UiChangeKind.SearchOrTaskList);
    }

    public void BeginRenameMaterial(string fullPath)
    {
        RenamingMaterialPath = fullPath;
        ConfirmingDeleteMaterialPath = null;
        NotifyStateChanged(UiChangeKind.SearchOrTaskList);
    }

    public void CancelRenameMaterial()
    {
        RenamingMaterialPath = null;
        NotifyStateChanged(UiChangeKind.SearchOrTaskList);
    }

    public void BeginConfirmDeleteMaterial(string fullPath)
    {
        ConfirmingDeleteMaterialPath = fullPath;
        RenamingMaterialPath = null;
        NotifyStateChanged(UiChangeKind.SearchOrTaskList);
    }

    public void CancelConfirmDeleteMaterial()
    {
        ConfirmingDeleteMaterialPath = null;
        NotifyStateChanged(UiChangeKind.SearchOrTaskList);
    }

    public void CreateMaterialFolder(BasinCourseEntry course, string rawFolderName)
    {
        if (string.IsNullOrWhiteSpace(BasinRoot) || string.IsNullOrWhiteSpace(rawFolderName))
            return;

        var courseDir = Path.Combine(BasinRoot, course.FolderName);
        Directory.CreateDirectory(courseDir);

        var safeName = SanitizeMaterialSegment(rawFolderName);
        if (string.IsNullOrWhiteSpace(safeName))
            return;

        var targetDir = Path.Combine(courseDir, safeName);
        if (Directory.Exists(targetDir))
        {
            StatusText = $"Folder '{safeName}' already exists.";
            NotifyStateChanged(UiChangeKind.SearchOrTaskList);
            return;
        }

        try
        {
            Directory.CreateDirectory(targetDir);
            IsNewMaterialFolderBarOpen = false;
            CollapsedMaterialFolders.Remove(targetDir);
            StatusText = $"Created folder '{safeName}' in {course.Title}";
            NotifyStateChanged(UiChangeKind.SearchOrTaskList);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not create folder: {ex.Message}";
            NotifyStateChanged(UiChangeKind.SearchOrTaskList);
        }
    }

    public void RenameMaterialFolder(
        BasinCourseEntry course,
        string oldFolderPath,
        string rawNewName
    )
    {
        if (
            string.IsNullOrWhiteSpace(BasinRoot)
            || !Directory.Exists(oldFolderPath)
            || string.IsNullOrWhiteSpace(rawNewName)
        )
        {
            CancelRenameMaterial();
            return;
        }

        var courseDir = Path.Combine(BasinRoot, course.FolderName);
        var safeName = SanitizeMaterialSegment(rawNewName);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            CancelRenameMaterial();
            return;
        }

        var parentDir = Path.GetDirectoryName(oldFolderPath) ?? courseDir;
        var newFolderPath = Path.Combine(parentDir, safeName);

        if (string.Equals(oldFolderPath, newFolderPath, StringComparison.Ordinal))
        {
            CancelRenameMaterial();
            return;
        }

        if (
            !string.Equals(oldFolderPath, newFolderPath, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(newFolderPath)
        )
        {
            StatusText = $"A folder named '{safeName}' already exists.";
            RenamingMaterialPath = null;
            NotifyStateChanged(UiChangeKind.SearchOrTaskList);
            return;
        }

        try
        {
            var oldRelPrefix = GetNormalizedRelativePath(courseDir, oldFolderPath) + "/";
            var newRelPrefix = GetNormalizedRelativePath(courseDir, newFolderPath) + "/";

            if (string.Equals(oldFolderPath, newFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                var tempPath = Path.Combine(parentDir, $".tmp_rename_{Guid.NewGuid():N}");
                Directory.Move(oldFolderPath, tempPath);
                Directory.Move(tempPath, newFolderPath);
            }
            else
            {
                Directory.Move(oldFolderPath, newFolderPath);
            }

            if (CollapsedMaterialFolders.Remove(oldFolderPath))
                CollapsedMaterialFolders.Add(newFolderPath);

            UpdatePacketPlanPaths(courseDir, oldRelPrefix, newRelPrefix, isPrefix: true);
            RemapQuizPathsForMaterialMove(oldFolderPath, newFolderPath, isPrefix: true);
            RenamingMaterialPath = null;
            StatusText = $"Renamed folder to '{safeName}'";
            NotifyStateChanged(UiChangeKind.SearchOrTaskList);
        }
        catch (Exception ex)
        {
            RenamingMaterialPath = null;
            StatusText = $"Could not rename folder: {ex.Message}";
            NotifyStateChanged(UiChangeKind.SearchOrTaskList);
        }
    }

    public void ReorderMaterialFolder(BasinCourseEntry course, string folderPath, int delta)
    {
        if (string.IsNullOrWhiteSpace(BasinRoot) || !Directory.Exists(folderPath) || delta == 0)
            return;

        var courseDir = Path.Combine(BasinRoot, course.FolderName);
        var editableDirs = Directory
            .GetDirectories(courseDir)
            .Where(d =>
            {
                var name = Path.GetFileName(d);
                return !name.StartsWith('.')
                    && !string.Equals(name, "Master_PDF", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .ToList();

        int idx = editableDirs.FindIndex(d =>
            string.Equals(d, folderPath, StringComparison.OrdinalIgnoreCase)
        );
        int targetIdx = idx + delta;
        if (idx < 0 || targetIdx < 0 || targetIdx >= editableDirs.Count)
            return;

        // Reorder list and normalize 2-digit prefixes (01_, 02_, ...) so disk order matches user order
        (editableDirs[idx], editableDirs[targetIdx]) = (editableDirs[targetIdx], editableDirs[idx]);

        try
        {
            var renames =
                new List<(
                    string OldPath,
                    string TempPath,
                    string FinalPath,
                    string OldRel,
                    string NewRel
                )>();
            for (int i = 0; i < editableDirs.Count; i++)
            {
                var currentPath = editableDirs[i];
                var currentName = Path.GetFileName(currentPath);
                var baseName = StripNumericPrefix(currentName);
                var desiredName = $"{i + 1:00}_{baseName}";
                var finalPath = Path.Combine(courseDir, desiredName);

                if (string.Equals(currentPath, finalPath, StringComparison.Ordinal))
                    continue;

                var tempPath = Path.Combine(courseDir, $".tmp_reorder_{i}_{Guid.NewGuid():N}");
                renames.Add(
                    (
                        currentPath,
                        tempPath,
                        finalPath,
                        GetNormalizedRelativePath(courseDir, currentPath) + "/",
                        GetNormalizedRelativePath(courseDir, finalPath) + "/"
                    )
                );
            }

            foreach (var (OldPath, TempPath, FinalPath, OldRel, NewRel) in renames)
            {
                Directory.Move(OldPath, TempPath);
                RemapQuizPathsForMaterialMove(OldPath, TempPath, isPrefix: true);
            }

            foreach (var (OldPath, TempPath, FinalPath, OldRel, NewRel) in renames)
            {
                Directory.Move(TempPath, FinalPath);
                RemapQuizPathsForMaterialMove(TempPath, FinalPath, isPrefix: true);
                if (CollapsedMaterialFolders.Remove(OldPath))
                    CollapsedMaterialFolders.Add(FinalPath);
            }

            if (renames.Count > 0)
            {
                var plan = CoursePacketPlan.TryLoadSavedPlan(courseDir);
                if (plan is not null)
                {
                    bool changed = false;
                    foreach (var sec in plan.Sections)
                    {
                        foreach (var item in sec.Items)
                        {
                            foreach (var (OldPath, TempPath, FinalPath, OldRel, NewRel) in renames)
                            {
                                if (
                                    item.RelativePath.StartsWith(
                                        OldRel,
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                                {
                                    item.RelativePath = NewRel + item.RelativePath[OldRel.Length..];
                                    changed = true;
                                    break;
                                }
                            }
                        }
                    }
                    if (changed)
                        CoursePacketPlan.SavePlan(courseDir, plan);
                }
            }

            StatusText = "Reordered course module folders";
            NotifyStateChanged(UiChangeKind.SearchOrTaskList);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not reorder folder: {ex.Message}";
            NotifyStateChanged(UiChangeKind.SearchOrTaskList);
        }
    }

    public void DeleteMaterialFolder(
        BasinCourseEntry course,
        string folderPath,
        bool moveFilesToRootFirst
    )
    {
        if (string.IsNullOrWhiteSpace(BasinRoot) || !Directory.Exists(folderPath))
        {
            CancelConfirmDeleteMaterial();
            return;
        }

        var courseDir = Path.Combine(BasinRoot, course.FolderName);
        var folderName = Path.GetFileName(folderPath);

        try
        {
            var oldRelPrefix = GetNormalizedRelativePath(courseDir, folderPath) + "/";

            if (moveFilesToRootFirst)
            {
                foreach (var file in Directory.GetFiles(folderPath))
                {
                    var destPath = GetUniqueFilePath(courseDir, Path.GetFileName(file));
                    var oldFileRel = GetNormalizedRelativePath(courseDir, file);
                    var newFileRel = GetNormalizedRelativePath(courseDir, destPath);
                    File.Move(file, destPath);
                    UpdatePacketPlanPaths(courseDir, oldFileRel, newFileRel, isPrefix: false);
                    RemapQuizPathsForMaterialMove(file, destPath, isPrefix: false);
                }
            }
            else
            {
                RemovePacketPlanPaths(courseDir, oldRelPrefix, isPrefix: true);
            }

            Directory.Delete(folderPath, recursive: true);
            CollapsedMaterialFolders.Remove(folderPath);
            ConfirmingDeleteMaterialPath = null;
            StatusText = moveFilesToRootFirst
                ? $"Removed folder '{folderName}' and moved files to Course Root"
                : $"Deleted folder '{folderName}'";
            NotifyStateChanged(UiChangeKind.SearchOrTaskList);
        }
        catch (Exception ex)
        {
            ConfirmingDeleteMaterialPath = null;
            StatusText = $"Could not delete folder: {ex.Message}";
            NotifyStateChanged(UiChangeKind.SearchOrTaskList);
        }
    }

    public async Task ImportFilesToFolderAsync(
        BasinCourseEntry course,
        string targetDir,
        IReadOnlyList<string> sourcePaths,
        FileImportMode? modeOverride = null
    )
    {
        if (string.IsNullOrWhiteSpace(BasinRoot) || sourcePaths.Count == 0)
            return;

        var mode = modeOverride ?? MaterialsImportMode;
        Directory.CreateDirectory(targetDir);
        int imported = 0;
        int skipped = 0;
        bool anyFallbackToCopy = false;
        bool hasPdfOrDir = false;

        foreach (var src in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(src))
                continue;

            try
            {
                var dest = FileImportHelper.ImportItem(src, targetDir, mode, out bool fellBack);
                if (dest is null)
                {
                    skipped++;
                    continue;
                }

                imported++;
                if (fellBack)
                    anyFallbackToCopy = true;
                if (
                    Directory.Exists(dest)
                    || dest.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                )
                    hasPdfOrDir = true;
            }
            catch
            {
                skipped++;
            }
        }

        if (imported > 0)
        {
            if (hasPdfOrDir)
                await ReloadBasinDataAsync();

            var destLabel = string.Equals(
                Path.GetFullPath(targetDir),
                Path.GetFullPath(Path.Combine(BasinRoot, course.FolderName)),
                StringComparison.OrdinalIgnoreCase
            )
                ? "Course Root"
                : Path.GetFileName(targetDir).Replace('_', ' ');

            var verb = mode switch
            {
                FileImportMode.Move => "Moved",
                FileImportMode.Symlink => anyFallbackToCopy ? "Copied" : "Linked",
                _ => "Copied",
            };
            var suffix =
                anyFallbackToCopy ? " (symlink requires Developer Mode on Windows — copied instead)"
                : skipped > 0 ? $" ({skipped} already in folder)"
                : string.Empty;

            StatusText = $"{verb} {imported} item(s) into '{destLabel}'{suffix}";
            NotifyStateChanged(UiChangeKind.SearchOrTaskList);
        }
        else if (skipped > 0)
        {
            StatusText = "Skipped — selected item(s) are already in that folder.";
            NotifyStateChanged(UiChangeKind.StatusOnly);
        }
    }

    public void RenameMaterialFile(BasinCourseEntry course, string oldFilePath, string rawNewName)
    {
        if (
            string.IsNullOrWhiteSpace(BasinRoot)
            || !File.Exists(oldFilePath)
            || string.IsNullOrWhiteSpace(rawNewName)
        )
        {
            CancelRenameMaterial();
            return;
        }

        var courseDir = Path.Combine(BasinRoot, course.FolderName);
        var originalExt = Path.GetExtension(oldFilePath);
        var cleaned = FilenameSanitizer.SanitizeFileName(rawNewName.Trim(), "").TrimEnd('.');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            CancelRenameMaterial();
            return;
        }

        if (!string.IsNullOrEmpty(originalExt) && string.IsNullOrEmpty(Path.GetExtension(cleaned)))
        {
            cleaned += originalExt;
        }

        var parentDir = Path.GetDirectoryName(oldFilePath) ?? courseDir;
        var newFilePath = Path.Combine(parentDir, cleaned);

        if (string.Equals(oldFilePath, newFilePath, StringComparison.Ordinal))
        {
            CancelRenameMaterial();
            return;
        }

        if (
            !string.Equals(oldFilePath, newFilePath, StringComparison.OrdinalIgnoreCase)
            && File.Exists(newFilePath)
        )
        {
            StatusText = $"A file named '{cleaned}' already exists in this folder.";
            RenamingMaterialPath = null;
            NotifyStateChanged(UiChangeKind.SearchOrTaskList);
            return;
        }

        try
        {
            var oldRel = GetNormalizedRelativePath(courseDir, oldFilePath);
            var newRel = GetNormalizedRelativePath(courseDir, newFilePath);

            if (string.Equals(oldFilePath, newFilePath, StringComparison.OrdinalIgnoreCase))
            {
                var tempPath = Path.Combine(parentDir, $".tmp_rename_{Guid.NewGuid():N}");
                File.Move(oldFilePath, tempPath);
                File.Move(tempPath, newFilePath);
            }
            else
            {
                File.Move(oldFilePath, newFilePath);
            }

            UpdatePacketPlanPaths(courseDir, oldRel, newRel, isPrefix: false);
            RemapQuizPathsForMaterialMove(oldFilePath, newFilePath, isPrefix: false);
            RenamingMaterialPath = null;
            StatusText = $"Renamed file to '{cleaned}'";
            NotifyStateChanged(UiChangeKind.SearchOrTaskList);
        }
        catch (Exception ex)
        {
            RenamingMaterialPath = null;
            StatusText = $"Could not rename file: {ex.Message}";
            NotifyStateChanged(UiChangeKind.SearchOrTaskList);
        }
    }

    public void MoveMaterialFile(
        BasinCourseEntry course,
        string sourceFilePath,
        string targetFolderPath
    )
    {
        if (
            string.IsNullOrWhiteSpace(BasinRoot)
            || !File.Exists(sourceFilePath)
            || string.IsNullOrWhiteSpace(targetFolderPath)
        )
            return;

        var courseDir = Path.Combine(BasinRoot, course.FolderName);
        var currentParent = Path.GetDirectoryName(sourceFilePath);
        if (
            currentParent is not null
            && string.Equals(
                Path.GetFullPath(currentParent),
                Path.GetFullPath(targetFolderPath),
                StringComparison.OrdinalIgnoreCase
            )
        )
            return;

        try
        {
            Directory.CreateDirectory(targetFolderPath);
            var destPath = GetUniqueFilePath(targetFolderPath, Path.GetFileName(sourceFilePath));
            var oldRel = GetNormalizedRelativePath(courseDir, sourceFilePath);
            var newRel = GetNormalizedRelativePath(courseDir, destPath);

            File.Move(sourceFilePath, destPath);
            UpdatePacketPlanPaths(courseDir, oldRel, newRel, isPrefix: false);
            RemapQuizPathsForMaterialMove(sourceFilePath, destPath, isPrefix: false);

            var destLabel = string.Equals(
                Path.GetFullPath(targetFolderPath),
                Path.GetFullPath(courseDir),
                StringComparison.OrdinalIgnoreCase
            )
                ? "Course Root"
                : Path.GetFileName(targetFolderPath).Replace('_', ' ');

            StatusText = $"Moved '{Path.GetFileName(sourceFilePath)}' to '{destLabel}'";
            NotifyStateChanged(UiChangeKind.SearchOrTaskList);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not move file: {ex.Message}";
            NotifyStateChanged(UiChangeKind.SearchOrTaskList);
        }
    }

    public void DeleteMaterialFile(BasinCourseEntry course, string filePath)
    {
        bool isLink = FileImportHelper.IsSymbolicLink(filePath);
        if (string.IsNullOrWhiteSpace(BasinRoot) || (!File.Exists(filePath) && !isLink))
        {
            CancelConfirmDeleteMaterial();
            return;
        }

        var courseDir = Path.Combine(BasinRoot, course.FolderName);
        var fileName = Path.GetFileName(filePath);

        try
        {
            var oldRel = GetNormalizedRelativePath(courseDir, filePath);
            File.Delete(filePath);
            RemovePacketPlanPaths(courseDir, oldRel, isPrefix: false);
            ConfirmingDeleteMaterialPath = null;
            StatusText = isLink ? $"Unlinked '{fileName}'" : $"Deleted '{fileName}'";
            NotifyStateChanged(UiChangeKind.SearchOrTaskList);
        }
        catch (Exception ex)
        {
            ConfirmingDeleteMaterialPath = null;
            StatusText = $"Could not delete file: {ex.Message}";
            NotifyStateChanged(UiChangeKind.SearchOrTaskList);
        }
    }

    private void RemapQuizPathsForMaterialMove(
        string oldFullPath,
        string newFullPath,
        bool isPrefix
    )
    {
        if (string.IsNullOrWhiteSpace(BasinRoot))
            return;

        ScholarFlowDatabase.RemapQuizSourcePdfPaths(BasinRoot, oldFullPath, newFullPath, isPrefix);
        QuizStudio.RemapSourcePdfPaths(oldFullPath, newFullPath, isPrefix);
    }

    private static string SanitizeMaterialSegment(string raw)
    {
        var trimmed = raw.Trim();
        var sanitized = FilenameSanitizer.SanitizeFileName(trimmed, "").TrimEnd('.');
        return sanitized.Replace(' ', '_');
    }

    private static string StripNumericPrefix(string folderName) =>
        StripNumericPrefaceRegex().Replace(folderName, string.Empty);

    private static string GetNormalizedRelativePath(string rootDir, string targetPath) =>
        Path.GetRelativePath(rootDir, targetPath).Replace('\\', '/');

    private static string GetUniqueFilePath(string targetDir, string fileName)
    {
        var candidate = Path.Combine(targetDir, fileName);
        if (!File.Exists(candidate))
            return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 2; i < 1000; i++)
        {
            var numbered = Path.Combine(targetDir, $"{stem} ({i}){ext}");
            if (!File.Exists(numbered))
                return numbered;
        }
        return Path.Combine(targetDir, $"{stem}_{Guid.NewGuid():N}{ext}");
    }

    private static void UpdatePacketPlanPaths(
        string courseDir,
        string oldPathOrPrefix,
        string newPathOrPrefix,
        bool isPrefix
    )
    {
        var plan = CoursePacketPlan.TryLoadSavedPlan(courseDir);
        if (plan is null)
            return;

        bool changed = false;
        foreach (var sec in plan.Sections)
        {
            foreach (var item in sec.Items)
            {
                if (isPrefix)
                {
                    if (
                        item.RelativePath.StartsWith(
                            oldPathOrPrefix,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        item.RelativePath =
                            newPathOrPrefix + item.RelativePath[oldPathOrPrefix.Length..];
                        changed = true;
                    }
                }
                else if (
                    string.Equals(
                        item.RelativePath,
                        oldPathOrPrefix,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    item.RelativePath = newPathOrPrefix;
                    changed = true;
                }
            }
        }

        if (changed)
            CoursePacketPlan.SavePlan(courseDir, plan);
    }

    private static void RemovePacketPlanPaths(string courseDir, string pathOrPrefix, bool isPrefix)
    {
        var plan = CoursePacketPlan.TryLoadSavedPlan(courseDir);
        if (plan is null)
            return;

        bool changed = false;
        foreach (var sec in plan.Sections)
        {
            int removed = sec.Items.RemoveAll(item =>
                isPrefix
                    ? item.RelativePath.StartsWith(pathOrPrefix, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(
                        item.RelativePath,
                        pathOrPrefix,
                        StringComparison.OrdinalIgnoreCase
                    )
            );
            if (removed > 0)
                changed = true;
        }

        if (changed)
            CoursePacketPlan.SavePlan(courseDir, plan);
    }
}
