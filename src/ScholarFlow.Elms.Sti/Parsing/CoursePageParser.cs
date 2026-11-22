using System.Text.RegularExpressions;
using AngleSharp.Dom;
using ScholarFlow.Core.Services;
using ScholarFlow.Elms.Models;

using ScholarFlow.Elms.Parsing;

namespace ScholarFlow.Elms.Sti.Parsing;

/// <summary>
/// Result of parsing a course landing page (/student_class/show/{id}).
/// </summary>
public sealed record ParsedCoursePage(
    IReadOnlyList<ElmsResource> GeneralResources,
    IReadOnlyList<ElmsModule> Modules,
    IReadOnlyList<string> AssignmentUrls,
    string AssignmentsListUrl
);

/// <summary>
/// Parses a course landing page for general resources, structured modern_module_row modules
/// (with Prelims/Midterms/Prefinals/Finals term tags and child sections), and assignment links.
/// </summary>
public static partial class CoursePageParser
{
    [GeneratedRegex(@"lesson_id=(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LessonIdRegex();

    [GeneratedRegex(@"section_id=(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SectionIdRegex();

    public static ParsedCoursePage Parse(
        string html,
        ElmsCourse course,
        string baseUrl = "https://elms.sti.edu"
    )
    {
        var doc = HtmlParsingHelpers.ParseDocument(html);
        var contentRoot = doc.QuerySelector("#centreColumn, #mainContent") ?? doc.Body;

        var generalResources = HtmlParsingHelpers.ExtractFileResources(
            contentRoot,
            baseUrl,
            relativeTargetFolder: "General_Resources",
            kind: ElmsResourceKind.CourseFile,
            courseId: course.CourseId,
            sourceTitle: course.Title
        );

        var modernRows = doc.QuerySelectorAll("tr.modern_module_row");
        var modules =
            modernRows.Length > 0
                ? ParseModernModuleRows(modernRows, baseUrl)
                : ParseFallbackModuleLinks(doc, baseUrl);

        var assignmentUrls = new List<string>();
        var seenAssignmentUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (
            var link in doc.QuerySelectorAll(
                "a[href*=\"_assignment/show/\"], a[href*=\"/student_quiz/\"], a[href*=\"/assignment/\"]"
            )
        )
        {
            var href = link.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href))
                continue;

            var absUrl = HtmlParsingHelpers.ToAbsoluteUrl(href, baseUrl);
            if (seenAssignmentUrls.Add(absUrl))
                assignmentUrls.Add(absUrl);
        }

        var assignmentsListUrl =
            $"{baseUrl.TrimEnd('/')}/student_assignments/list/{course.CourseId}";

        return new ParsedCoursePage(generalResources, modules, assignmentUrls, assignmentsListUrl);
    }

    private static List<ElmsModule> ParseModernModuleRows(
        IHtmlCollection<IElement> modernRows,
        string baseUrl
    )
    {
        var modules = new List<ElmsModule>();
        var orderIndex = 0;

        foreach (var row in modernRows)
        {
            var wrapLink = row.QuerySelector(
                "a.modern_module_wrap, a[href*=\"/student_lesson/show/\"]"
            );
            var href = wrapLink?.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href))
                continue;

            var lessonUrl = HtmlParsingHelpers.ToAbsoluteUrl(href, baseUrl);
            var titleEl = row.QuerySelector("h3.evo-lesson-name, h3, h2");
            var title = HtmlParsingHelpers.NormalizeWhitespace(
                titleEl?.TextContent ?? titleEl?.GetAttribute("title")
            );
            if (string.IsNullOrWhiteSpace(title))
                title = $"Module_{orderIndex + 1:D2}";

            var lessonIdMatch = LessonIdRegex().Match(href);
            var lessonId = lessonIdMatch.Success ? lessonIdMatch.Groups[1].Value : null;

            var (termHeader, numberPrefix) = HtmlParsingHelpers.ExtractTermAndPrefix(title);
            var sections = ParseSiblingSectionRow(row.NextElementSibling, baseUrl);

            modules.Add(
                new ElmsModule(
                    Title: title,
                    SanitizedFolderName: FilenameSanitizer.SanitizeFolderName(
                        title,
                        $"Module_{orderIndex + 1:D2}"
                    ),
                    LessonUrl: lessonUrl,
                    OrderIndex: orderIndex++,
                    TermHeader: termHeader,
                    IsScormOrExternal: false,
                    LessonId: lessonId,
                    ModuleNumberPrefix: numberPrefix,
                    Sections: sections
                )
            );
        }

        return modules;
    }

    private static List<ElmsModuleSection> ParseSiblingSectionRow(
        IElement? siblingRow,
        string baseUrl
    )
    {
        var sections = new List<ElmsModuleSection>();
        if (siblingRow is null || !siblingRow.ClassList.Contains("section"))
            return sections;

        foreach (var tr in siblingRow.QuerySelectorAll("tr"))
        {
            var flexSpan = tr.QuerySelector("span.flex_section_name");
            if (flexSpan is null)
                continue;

            var link = flexSpan.QuerySelector("a[href]");
            var href = link?.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href))
                continue;

            var title = HtmlParsingHelpers.NormalizeWhitespace(link!.TextContent);
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var absUrl = HtmlParsingHelpers.ToAbsoluteUrl(href, baseUrl);
            var secMatch = SectionIdRegex().Match(href);
            var sectionId = secMatch.Success ? secMatch.Groups[1].Value : absUrl;

            var iconEl = flexSpan.QuerySelector("i");
            var kind = ClassifySectionIcon(iconEl, title);
            var isCompleted = tr.QuerySelector("i.tick") is not null;

            var cells = tr.QuerySelectorAll("td");
            string? scoreText =
                cells.Length >= 3
                    ? HtmlParsingHelpers.NormalizeWhitespace(cells[2].TextContent)
                    : null;
            string? dueText =
                cells.Length >= 4
                    ? HtmlParsingHelpers.NormalizeWhitespace(cells[3].TextContent)
                    : null;

            sections.Add(
                new ElmsModuleSection(
                    SectionId: sectionId,
                    Title: title,
                    SanitizedFolderName: FilenameSanitizer.SanitizeFolderName(title),
                    Url: absUrl,
                    Kind: kind,
                    IsCompleted: isCompleted,
                    DueText: string.IsNullOrEmpty(dueText) ? null : dueText,
                    ScoreText: string.IsNullOrEmpty(scoreText) ? null : scoreText
                )
            );
        }

        return sections;
    }

    private static ElmsSectionKind ClassifySectionIcon(IElement? iconEl, string title)
    {
        var classes = iconEl?.ClassName ?? string.Empty;
        if (
            classes.Contains("package", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Online Presentation", StringComparison.OrdinalIgnoreCase)
        )
        {
            return ElmsSectionKind.Presentation;
        }

        if (
            classes.Contains("quiz", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Quiz", StringComparison.OrdinalIgnoreCase)
        )
        {
            return ElmsSectionKind.Quiz;
        }

        if (
            classes.Contains("pencil", StringComparison.OrdinalIgnoreCase)
            || classes.Contains("folderUp", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Task Performance", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Activity", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Laboratory Exercise", StringComparison.OrdinalIgnoreCase)
        )
        {
            return ElmsSectionKind.Assignment;
        }

        if (
            classes.Contains("pageText", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Handout", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Syllabus", StringComparison.OrdinalIgnoreCase)
        )
        {
            return ElmsSectionKind.Handout;
        }

        return ElmsSectionKind.Other;
    }

    private static List<ElmsModule> ParseFallbackModuleLinks(IDocument doc, string baseUrl)
    {
        var modules = new List<ElmsModule>();
        var seenLessonUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orderIndex = 0;

        foreach (var link in doc.QuerySelectorAll("a[href*=\"/student_lesson/show/\"]"))
        {
            var href = link.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href))
                continue;

            var absUrl = HtmlParsingHelpers.ToAbsoluteUrl(href, baseUrl);
            if (!seenLessonUrls.Add(absUrl))
                continue;

            var title = HtmlParsingHelpers.NormalizeWhitespace(link.TextContent);
            if (string.IsNullOrWhiteSpace(title))
                title = $"Lesson_{orderIndex + 1:D2}";

            var (termFromTitle, prefix) = HtmlParsingHelpers.ExtractTermAndPrefix(title);
            var termHeader = termFromTitle ?? FindNearestTermHeader(link);
            var folderName = FilenameSanitizer.SanitizeFolderName(
                title,
                $"Lesson_{orderIndex + 1:D2}"
            );

            modules.Add(
                new ElmsModule(
                    Title: title,
                    SanitizedFolderName: folderName,
                    LessonUrl: absUrl,
                    OrderIndex: orderIndex++,
                    TermHeader: termHeader,
                    ModuleNumberPrefix: prefix
                )
            );
        }

        return modules;
    }

    private static string? FindNearestTermHeader(IElement link)
    {
        var section = link.Closest(
            ".module-section, .term-section, .lesson-group, section, .accordion-group"
        );
        var header = section?.QuerySelector("h2, h3, .section-title, .group-title");
        return HtmlParsingHelpers.NormalizeTermName(header?.TextContent);
    }
}
