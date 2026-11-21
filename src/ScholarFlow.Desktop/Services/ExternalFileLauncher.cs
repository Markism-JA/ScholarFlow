using System.Diagnostics;

namespace ScholarFlow.Desktop.Services;

/// <summary>
/// Cross-platform launcher (Linux + Windows + macOS):
/// Opens text/Markdown notes using $VISUAL / $EDITOR (with terminal wrapper when needed) or the OS default handler,
/// and opens binary/PDF files via the native OS shell (UseShellExecute on Windows, xdg-open on Linux, open on macOS).
/// </summary>
public static class ExternalFileLauncher
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md",
        ".txt",
        ".json",
        ".yaml",
        ".yml",
        ".py",
        ".sh",
        ".cs",
        ".toml",
        ".conf",
    };

    private static readonly HashSet<string> TerminalEditors = new(StringComparer.OrdinalIgnoreCase)
    {
        "nvim",
        "vim",
        "vi",
        "nano",
        "hx",
        "helix",
        "micro",
        "emacs",
        "emacsclient",
    };

    public static string? GetConfiguredEditor()
    {
        var visual = Environment.GetEnvironmentVariable("VISUAL");
        if (!string.IsNullOrWhiteSpace(visual))
            return visual.Trim();

        var editor = Environment.GetEnvironmentVariable("EDITOR");
        return string.IsNullOrWhiteSpace(editor) ? null : editor.Trim();
    }

    public static void OpenLocalFile(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return;

        if (Directory.Exists(fullPath))
        {
            OpenWithOsDefault(fullPath);
            return;
        }

        if (!File.Exists(fullPath))
            return;

        var ext = Path.GetExtension(fullPath);
        if (TextExtensions.Contains(ext) && TryLaunchConfiguredEditor(fullPath))
            return;

        OpenWithOsDefault(fullPath);
    }

    public static void OpenUrl(string url)
    {
        if (
            string.IsNullOrWhiteSpace(url)
            || (
                !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return;
        }
        OpenWithOsDefault(url);
    }

    private static bool TryLaunchConfiguredEditor(string fullPath)
    {
        var editorEnv = GetConfiguredEditor();
        if (string.IsNullOrWhiteSpace(editorEnv))
        {
            if (OperatingSystem.IsLinux() && File.Exists("/usr/bin/kitty"))
                return TryStart("/usr/bin/kitty", ["--detach", "nvim", fullPath]);
            return false;
        }

        var parts = editorEnv.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        var editorExe = parts[0];
        var editorName = Path.GetFileNameWithoutExtension(editorExe);
        var extraArgs = parts.Skip(1).ToList();

        if (TerminalEditors.Contains(editorName))
        {
            if (OperatingSystem.IsWindows())
            {
                // Launch inside Windows Terminal (wt) if available, otherwise let cmd spawn a console
                if (TryStart("wt.exe", [editorExe, .. extraArgs, fullPath]))
                    return true;
                return TryStart("cmd.exe", ["/c", "start", "", editorExe, .. extraArgs, fullPath]);
            }

            if (OperatingSystem.IsLinux())
            {
                var term = Environment.GetEnvironmentVariable("TERMINAL")?.Trim();
                if (!string.IsNullOrWhiteSpace(term))
                {
                    if (
                        Path.GetFileNameWithoutExtension(term)
                            .Equals("kitty", StringComparison.OrdinalIgnoreCase)
                    )
                        return TryStart(term, ["--detach", editorExe, .. extraArgs, fullPath]);
                    if (TryStart(term, ["-e", editorExe, .. extraArgs, fullPath]))
                        return true;
                }

                if (
                    File.Exists("/usr/bin/kitty")
                    && TryStart("/usr/bin/kitty", ["--detach", editorExe, .. extraArgs, fullPath])
                )
                    return true;
                if (
                    File.Exists("/usr/bin/alacritty")
                    && TryStart("/usr/bin/alacritty", ["-e", editorExe, .. extraArgs, fullPath])
                )
                    return true;
                if (
                    File.Exists("/usr/bin/wezterm")
                    && TryStart(
                        "/usr/bin/wezterm",
                        ["start", "--", editorExe, .. extraArgs, fullPath]
                    )
                )
                    return true;
            }

            return false;
        }

        // GUI editor (e.g. code, obsidian, notepad++, subl)
        return TryStart(editorExe, [.. extraArgs, fullPath]);
    }

    private static void OpenWithOsDefault(string fullPath)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
            }
            catch
            {
                // Ignore if OS shell fails
            }
            return;
        }

        var launcher = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        TryStart(launcher, [fullPath]);
    }

    private static bool TryStart(string fileName, IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = fileName, UseShellExecute = false };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
