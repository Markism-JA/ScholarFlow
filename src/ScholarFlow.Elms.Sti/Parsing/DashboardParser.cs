using System.Text.RegularExpressions;
using AngleSharp.Dom;
using ScholarFlow.Core.Services;
using ScholarFlow.Elms.Models;

using ScholarFlow.Elms.Parsing;

namespace ScholarFlow.Elms.Sti.Parsing;

/// <summary>
/// Parses enrolled courses from the STI eLMS user dashboard without hardcoded course maps.
/// Distinguishes enrolled vs completed courses, extracts CourseCode/SectionCode, strips trailing
/// term tags (- SY2627-1T), and automatically classifies administrative/survey shells.
/// </summary>
public static partial class DashboardParser
{
    [GeneratedRegex(@"/student_class/show/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CourseIdRegex();

    [GeneratedRegex(
        @"\s+-\s+(SY\d{4}-\d+T|SY\s*\d{4}-\d{2,4}(?:-\d+T)?|[A-Z]{2,6}\d{3,5})\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    )]
    private static partial Regex TrailingTermOrSectionSuffixRegex();

    [GeneratedRegex(
        @"Course\s+code\s*([A-Za-z0-9\-\s]+?)(?:·|&middot;|Section\s+code|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    )]
    private static partial Regex CourseCodeMetaRegex();

    [GeneratedRegex(@"Section\s+code\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SectionCodeMetaRegex();

    private static readonly string[] AdminShellKeywords =
    [
        "Teacher Evaluation",
        "Student Information Survey",
        "Student Survey",
        "Community Corner",
        "Guidance and Counseling",
        "Grade Monitoring",
        "Student Orientation",
        "Course Curriculum",
    ];

    public static IReadOnlyList<ElmsCourse> ParseEnrolledCourses(
        string html,
        string baseUrl = "https://elms.sti.edu",
        bool includeCompleted = false,
        bool includeAdministrative = false
    )
    {
        var doc = HtmlParsingHelpers.ParseDocument(html);
        var courses = new List<ElmsCourse>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var anchor in doc.QuerySelectorAll("a[href*=\"/student_class/show/\"]"))
        {
            var href = anchor.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href))
                continue;

            var match = CourseIdRegex().Match(href);
            if (!match.Success)
                continue;

            var courseId = match.Groups[1].Value;
            if (!seenIds.Add(courseId))
                continue;

            var rawTitle = ExtractCourseTitle(anchor);
            if (string.IsNullOrWhiteSpace(rawTitle))
                continue;

            var isCompleted =
                anchor.Closest("#left_nav_classes_panel_completed, .scroll.completed") is not null;
            var (courseCode, sectionCode) = ExtractMetaCodes(anchor);
            var (cleanTitle, termSuffix) = SplitTrailingTermSuffix(rawTitle);

            var isAdmin = ClassifyAdministrativeShell(rawTitle, courseCode, sectionCode);

            if (!includeCompleted && isCompleted)
                continue;

            if (!includeAdministrative && isAdmin)
                continue;

            var courseUrl = HtmlParsingHelpers.ToAbsoluteUrl(href, baseUrl);
            var folder = FilenameSanitizer.SanitizeFolderName(cleanTitle, $"Course_{courseId}");

            courses.Add(
                new ElmsCourse(
                    CourseId: courseId,
                    Title: cleanTitle,
                    CourseUrl: courseUrl,
                    SanitizedFolderName: folder,
                    CourseCode: courseCode,
                    TermOrSection: sectionCode,
                    RawTitle: rawTitle,
                    TermSuffix: termSuffix,
                    IsCompleted: isCompleted,
                    IsAdministrativeShell: isAdmin
                )
            );
        }

        return courses;
    }

    private static string ExtractCourseTitle(IElement anchor)
    {
        var titleNode = anchor.QuerySelector(
            ".course-title, .class-name, h2, h3, h4, .title, strong"
        );
        var raw = titleNode?.TextContent ?? anchor.GetAttribute("title") ?? anchor.TextContent;
        return HtmlParsingHelpers.NormalizeWhitespace(raw);
    }

    private static (string CleanTitle, string? TermSuffix) SplitTrailingTermSuffix(string rawTitle)
    {
        var match = TrailingTermOrSectionSuffixRegex().Match(rawTitle);
        if (!match.Success)
            return (rawTitle, null);

        var suffix = match.Groups[1].Value.Trim();
        var clean = rawTitle[..match.Index].Trim();
        return string.IsNullOrWhiteSpace(clean) ? (rawTitle, null) : (clean, suffix);
    }

    private static (string? CourseCode, string? SectionCode) ExtractMetaCodes(IElement anchor)
    {
        var li = anchor.Closest("li");
        if (li is null)
            return (null, null);

        var metaText = HtmlParsingHelpers.NormalizeWhitespace(li.TextContent);
        string? courseCode = null;
        string? sectionCode = null;

        var ccMatch = CourseCodeMetaRegex().Match(metaText);
        if (ccMatch.Success)
            courseCode = ccMatch.Groups[1].Value.Trim();

        var scMatch = SectionCodeMetaRegex().Match(metaText);
        if (scMatch.Success)
            sectionCode = scMatch.Groups[1].Value.Trim();

        return (courseCode, sectionCode);
    }

    private static bool ClassifyAdministrativeShell(
        string rawTitle,
        string? courseCode,
        string? sectionCode
    )
    {
        foreach (var keyword in AdminShellKeywords)
        {
            if (rawTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (
            !string.IsNullOrWhiteSpace(sectionCode)
            && (
                sectionCode.Contains("Survey", StringComparison.OrdinalIgnoreCase)
                || sectionCode.Contains("STI College", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return true;
        }

        if (
            !string.IsNullOrWhiteSpace(courseCode)
            && (
                courseCode.Equals("GCO", StringComparison.OrdinalIgnoreCase)
                || courseCode.Contains("Community Corner", StringComparison.OrdinalIgnoreCase)
                || courseCode.Contains("Course Curriculum", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return true;
        }

        return false;
    }
}
