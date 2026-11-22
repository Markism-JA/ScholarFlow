namespace ScholarFlow.Core.Services;

public enum FileImportMode
{
    Copy,
    Move,
    Symlink,
}

/// <summary>
/// Cross-platform file and folder import helper supporting Copy, Move (with cross-volume fallback),
/// and Symbolic Link (with Windows privilege-error fallback to Copy), plus self-duplicate guarding.
/// </summary>
public static class FileImportHelper
{
    /// <summary>
    /// Imports a single file or directory into <paramref name="targetDir"/> using <paramref name="mode"/>.
    /// Returns the created destination path, or <c>null</c> if skipped (e.g. already inside <paramref name="targetDir"/>).
    /// </summary>
    public static string? ImportItem(
        string sourcePath,
        string targetDir,
        FileImportMode mode,
        out bool fellBackToCopy
    )
    {
        fellBackToCopy = false;

        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(targetDir))
            return null;

        bool isFile = File.Exists(sourcePath);
        bool isDir = !isFile && Directory.Exists(sourcePath);
        if (!isFile && !isDir)
            return null;

        Directory.CreateDirectory(targetDir);

        var fullSource = Path.GetFullPath(sourcePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullTargetDir = Path.GetFullPath(targetDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Self-duplicate guard: skip if source is already directly inside targetDir or is targetDir itself
        if (string.Equals(fullSource, fullTargetDir, StringComparison.OrdinalIgnoreCase))
            return null;

        var sourceParent = Path.GetDirectoryName(fullSource)
            ?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (
            sourceParent is not null
            && string.Equals(sourceParent, fullTargetDir, StringComparison.OrdinalIgnoreCase)
        )
        {
            return null;
        }

        // Prevent copying/moving a directory into its own descendant
        if (
            isDir
            && fullTargetDir.StartsWith(
                fullSource + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return null;
        }

        var itemName = Path.GetFileName(fullSource);
        if (string.IsNullOrWhiteSpace(itemName))
            return null;

        if (isFile)
        {
            var destPath = GetUniqueFilePath(fullTargetDir, itemName);
            switch (mode)
            {
                case FileImportMode.Copy:
                    File.Copy(fullSource, destPath, overwrite: false);
                    return destPath;

                case FileImportMode.Move:
                    MoveFileCrossVolumeSafe(fullSource, destPath);
                    return destPath;

                case FileImportMode.Symlink:
                    try
                    {
                        File.CreateSymbolicLink(destPath, fullSource);
                        return destPath;
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        fellBackToCopy = true;
                        File.Copy(fullSource, destPath, overwrite: false);
                        return destPath;
                    }
            }
        }
        else
        {
            var destDir = GetUniqueDirectoryPath(fullTargetDir, itemName);
            switch (mode)
            {
                case FileImportMode.Copy:
                    CopyDirectoryRecursive(fullSource, destDir);
                    return destDir;

                case FileImportMode.Move:
                    MoveDirectoryCrossVolumeSafe(fullSource, destDir);
                    return destDir;

                case FileImportMode.Symlink:
                    try
                    {
                        Directory.CreateSymbolicLink(destDir, fullSource);
                        return destDir;
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        fellBackToCopy = true;
                        CopyDirectoryRecursive(fullSource, destDir);
                        return destDir;
                    }
            }
        }

        return null;
    }

    public static bool IsSymbolicLink(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var fi = new FileInfo(path);
            if (fi.LinkTarget is not null)
                return true;

            var di = new DirectoryInfo(path);
            return di.LinkTarget is not null;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsBrokenSymbolicLink(string path)
    {
        if (!IsSymbolicLink(path))
            return false;

        try
        {
            var target = File.ResolveLinkTarget(path, returnFinalTarget: true);
            if (target is null)
                return true;

            return !File.Exists(target.FullName) && !Directory.Exists(target.FullName);
        }
        catch
        {
            return true;
        }
    }

    public static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith('.'))
                continue;
            File.Copy(file, Path.Combine(destDir, name), overwrite: true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var name = Path.GetFileName(subDir);
            if (name.StartsWith('.'))
                continue;
            CopyDirectoryRecursive(subDir, Path.Combine(destDir, name));
        }
    }

    public static void MoveFileCrossVolumeSafe(string sourceFile, string destFile)
    {
        try
        {
            File.Move(sourceFile, destFile);
        }
        catch (IOException)
        {
            File.Copy(sourceFile, destFile, overwrite: false);
            File.Delete(sourceFile);
        }
    }

    public static void MoveDirectoryCrossVolumeSafe(string sourceDir, string destDir)
    {
        try
        {
            Directory.Move(sourceDir, destDir);
        }
        catch (IOException)
        {
            CopyDirectoryRecursive(sourceDir, destDir);
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    public static string GetUniqueFilePath(string targetDir, string fileName)
    {
        var candidate = Path.Combine(targetDir, fileName);
        if (!File.Exists(candidate) && !IsSymbolicLink(candidate))
            return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 2; i < 1000; i++)
        {
            var numbered = Path.Combine(targetDir, $"{stem} ({i}){ext}");
            if (!File.Exists(numbered) && !IsSymbolicLink(numbered))
                return numbered;
        }

        return Path.Combine(targetDir, $"{stem}_{Guid.NewGuid():N}{ext}");
    }

    public static string GetUniqueDirectoryPath(string targetDir, string dirName)
    {
        var candidate = Path.Combine(targetDir, dirName);
        if (!Directory.Exists(candidate) && !IsSymbolicLink(candidate))
            return candidate;

        for (int i = 2; i < 1000; i++)
        {
            var numbered = Path.Combine(targetDir, $"{dirName} ({i})");
            if (!Directory.Exists(numbered) && !IsSymbolicLink(numbered))
                return numbered;
        }

        return Path.Combine(targetDir, $"{dirName}_{Guid.NewGuid():N}");
    }
}
