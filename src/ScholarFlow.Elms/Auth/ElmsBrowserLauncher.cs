using System.Diagnostics;
using ScholarFlow.Elms.Models;
using ScholarFlow.Elms.Parsing;

namespace ScholarFlow.Elms.Auth;

/// <summary>
/// Launches an authenticated standalone Chromium web-app window (--app=...) sharing the persistent Playwright profile.
/// </summary>
public static class ElmsBrowserLauncher
{
    public static Process? LaunchAppWindow(string urlOrPath, AuthOptions? options = null)
    {
        var authOptions = options ?? new AuthOptions();
        var targetUrl = HtmlParsingHelpers.ToAbsoluteUrl(urlOrPath, authOptions.BaseUrl);
        var executable = FindChromiumExecutable()
            ?? throw new FileNotFoundException(
                "Could not locate Playwright Chromium or system chromium executable."
            );

        Directory.CreateDirectory(authOptions.BrowserProfileDirectory);

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        psi.ArgumentList.Add($"--user-data-dir={authOptions.BrowserProfileDirectory}");
        psi.ArgumentList.Add($"--app={targetUrl}");
        psi.ArgumentList.Add("--no-first-run");
        psi.ArgumentList.Add("--no-default-browser-check");

        return Process.Start(psi);
    }

    public static string? FindChromiumExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData
            );
            var pwWinCache = Path.Combine(localAppData, "ms-playwright");
            if (Directory.Exists(pwWinCache))
            {
                var candidates = Directory
                    .GetDirectories(pwWinCache, "chromium-*")
                    .OrderByDescending(d => d)
                    .Select(d => Path.Combine(d, "chrome-win64", "chrome.exe"))
                    .Where(File.Exists)
                    .ToList();
                if (candidates.Count > 0)
                    return candidates[0];
            }

            var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string[] winSystemBinaries =
            [
                Path.Combine(progFiles, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(progFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(progFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(progFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(
                    progFiles,
                    "BraveSoftware",
                    "Brave-Browser",
                    "Application",
                    "brave.exe"
                ),
                Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"),
            ];
            return winSystemBinaries.FirstOrDefault(File.Exists);
        }

        var pwCache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache",
            "ms-playwright"
        );

        if (Directory.Exists(pwCache))
        {
            var candidates = Directory
                .GetDirectories(pwCache, "chromium-*")
                .OrderByDescending(d => d)
                .Select(d => Path.Combine(d, "chrome-linux64", "chrome"))
                .Where(File.Exists)
                .ToList();

            if (candidates.Count > 0)
                return candidates[0];
        }

        string[] systemBinaries =
        [
            "/usr/bin/chromium",
            "/usr/bin/google-chrome-stable",
            "/usr/bin/google-chrome",
            "/usr/bin/brave",
        ];
        return systemBinaries.FirstOrDefault(File.Exists);
    }
}
