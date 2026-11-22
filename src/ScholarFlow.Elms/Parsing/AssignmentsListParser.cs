using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using ScholarFlow.Elms.Models;

namespace ScholarFlow.Elms.Parsing;

/// <summary>
/// Parses the course assignments table (/student_assignments/list/{courseId}).
/// </summary>
public static partial class AssignmentsListParser
{
    [GeneratedRegex(
        @"/student_([a-z_]+)_assignment/show/(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    )]
    private static partial Regex TypedAssignmentUrlRegex();

    [GeneratedRegex(
        @"/(?:student_assignment|student_quiz|quiz|assignment)/(?:show|take|start|intro|view)/(\d+)|/student_quiz/(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    )]
    private static partial Regex FallbackAssignmentIdRegex();

    public static IReadOnlyList<ElmsAssignmentSummary> Parse(
        string html,
        string courseId,
        string baseUrl = "https://elms.sti.edu",
        int? referenceYear = null
    )
    {
        var doc = HtmlParsingHelpers.ParseDocument(html);
        var results = new List<ElmsAssignmentSummary>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var year = referenceYear ?? DateTimeOffset.Now.Year;

        foreach (var tr in doc.QuerySelectorAll("tr"))
        {
            var assignmentCell = tr.QuerySelector("td.assignment");
            const string linkSelector =
                "a[href*=\"_assignment/show/\"], a[href*=\"/assignment/show/\"], a[href*=\"/student_quiz/\"], a[href*=\"/quiz/show/\"]";
            var primaryLink = (assignmentCell ?? tr).QuerySelector(linkSelector);
            if (primaryLink is null)
                continue;

            var href = primaryLink.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href))
                continue;

            var (lmsId, kind) = ParseIdAndKind(href, primaryLink.GetAttribute("title"));
            if (!seenIds.Add(lmsId))
                continue;

            var title = HtmlParsingHelpers.NormalizeWhitespace(primaryLink.TextContent);
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var absUrl = HtmlParsingHelpers.ToAbsoluteUrl(href, baseUrl);

            var moduleLink = (assignmentCell ?? tr).QuerySelector(
                "a[href*=\"/student_lesson/show/\"]"
            );
            var parentModuleTitle = HtmlParsingHelpers.NormalizeWhitespace(moduleLink?.TextContent);
            if (string.IsNullOrWhiteSpace(parentModuleTitle))
                parentModuleTitle = null;

            var (termFromModule, _) = HtmlParsingHelpers.ExtractTermAndPrefix(parentModuleTitle);
            var (termFromTitle, _) = HtmlParsingHelpers.ExtractTermAndPrefix(title);
            var termHeader = termFromModule ?? termFromTitle;

            var cells = tr.QuerySelectorAll("td");
            string? dueText =
                cells.Length > 3
                    ? HtmlParsingHelpers.NormalizeWhitespace(cells[3].TextContent)
                    : null;
            string? weightText =
                cells.Length > 4
                    ? HtmlParsingHelpers.NormalizeWhitespace(cells[4].TextContent)
                    : null;
            var submittedCell = cells.Length > 5 ? cells[5] : null;
            var gradedCell = cells.Length > 6 ? cells[6] : null;
            string? scoreText =
                cells.Length > 7
                    ? HtmlParsingHelpers.NormalizeWhitespace(cells[7].TextContent)
                    : null;
            string? gradeText =
                cells.Length > 8
                    ? HtmlParsingHelpers.NormalizeWhitespace(cells[8].TextContent)
                    : null;

            double? weight = double.TryParse(
                weightText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var w
            )
                ? w
                : null;
            var dueDate = AssignmentParser.TryParseFlexibleDate(dueText, year);
            var status = ClassifyRowStatus(submittedCell, gradedCell, scoreText, gradeText);

            results.Add(
                new ElmsAssignmentSummary(
                    LmsId: lmsId,
                    CourseId: courseId,
                    Title: title,
                    Url: absUrl,
                    Kind: kind,
                    ParentModuleTitle: parentModuleTitle,
                    TermHeader: termHeader,
                    DueDate: dueDate,
                    DueText: string.IsNullOrWhiteSpace(dueText) ? null : dueText,
                    WeightPercent: weight,
                    Status: status,
                    ScoreText: string.IsNullOrWhiteSpace(scoreText) || scoreText == "-"
                        ? null
                        : scoreText,
                    GradeText: string.IsNullOrWhiteSpace(gradeText) || gradeText == "-"
                        ? null
                        : gradeText
                )
            );
        }

        return results;
    }

    internal static (string LmsId, ElmsAssignmentKind Kind) ParseIdAndKind(
        string urlOrHref,
        string? titleAttr = null
    )
    {
        var match = TypedAssignmentUrlRegex().Match(urlOrHref);
        if (match.Success)
        {
            var rawKind = match.Groups[1].Value.ToLowerInvariant();
            var id = match.Groups[2].Value;
            var kind = rawKind switch
            {
                "freeform" => ElmsAssignmentKind.Freeform,
                "dropbox" => ElmsAssignmentKind.Dropbox,
                "quiz" => ElmsAssignmentKind.Quiz,
                "discussion" => ElmsAssignmentKind.Discussion,
                "survey" => ElmsAssignmentKind.Survey,
                "offline" => ElmsAssignmentKind.Offline,
                _ => ElmsAssignmentKind.Unknown,
            };
            return (id, kind);
        }

        var fallback = FallbackAssignmentIdRegex().Match(urlOrHref);
        var fallbackId = fallback.Success
            ? (
                !string.IsNullOrEmpty(fallback.Groups[1].Value)
                    ? fallback.Groups[1].Value
                    : fallback.Groups[2].Value
            )
            : urlOrHref;

        var inferredKind = titleAttr?.Split(':')[0].Trim().ToLowerInvariant() switch
        {
            "essay" => ElmsAssignmentKind.Freeform,
            "dropbox" => ElmsAssignmentKind.Dropbox,
            "quiz" => ElmsAssignmentKind.Quiz,
            _ when urlOrHref.Contains("quiz", StringComparison.OrdinalIgnoreCase) =>
                ElmsAssignmentKind.Quiz,
            _ => ElmsAssignmentKind.Unknown,
        };

        return (fallbackId, inferredKind);
    }

    private static AssignmentStatus ClassifyRowStatus(
        IElement? submittedCell,
        IElement? gradedCell,
        string? scoreText,
        string? gradeText
    )
    {
        var subTitle = submittedCell?.QuerySelector("span[title]")?.GetAttribute("title")
            ?? submittedCell?.TextContent
            ?? string.Empty;
        var gradedTitle = gradedCell?.QuerySelector("span[title]")?.GetAttribute("title")
            ?? gradedCell?.TextContent
            ?? string.Empty;

        if (
            gradedTitle.Contains("Yes", StringComparison.OrdinalIgnoreCase)
            || (
                !string.IsNullOrWhiteSpace(scoreText)
                && !scoreText.StartsWith('?')
                && !scoreText.StartsWith("0/")
                && scoreText.Contains('/')
            )
        )
        {
            return AssignmentStatus.Graded;
        }

        if (
            subTitle.Contains("Yes", StringComparison.OrdinalIgnoreCase)
            || submittedCell?.QuerySelector("i.tick") is not null
        )
        {
            return AssignmentStatus.Submitted;
        }

        if (
            subTitle.Contains("Past due", StringComparison.OrdinalIgnoreCase)
            || submittedCell?.QuerySelector("i.flag") is not null
            || string.Equals(gradeText, "M", StringComparison.OrdinalIgnoreCase)
        )
        {
            return AssignmentStatus.Missing;
        }

        return AssignmentStatus.Pending;
    }
}
