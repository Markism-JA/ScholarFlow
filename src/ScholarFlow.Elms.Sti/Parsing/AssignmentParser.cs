using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using ScholarFlow.Elms.Models;

using ScholarFlow.Elms.Parsing;

namespace ScholarFlow.Elms.Sti.Parsing;

/// <summary>
/// Parses assignment/activity detail pages (/student_*_assignment/show/{id}) and submission pages (/student_*_assignment/submissions/{id}).
/// </summary>
public static partial class AssignmentParser
{
    [GeneratedRegex(@"\b(20\d{2})\b", RegexOptions.Compiled)]
    private static partial Regex FourDigitYearRegex();

    [GeneratedRegex(
        @"^([A-Za-z]{3,9})\s+(\d{1,2})(?:\s*,?\s*(\d{1,2}:\d{2}\s*(?:am|pm)))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    )]
    private static partial Regex MonthDayOptionalTimeRegex();

    [GeneratedRegex(@"Max\s+score:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MaxScoreRegex();

    [GeneratedRegex(@"Due:\s*([^\n\r<]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DueLineRegex();

    [GeneratedRegex(@"Submitted:\s*([^\n\r<]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SubmittedLineRegex();

    [GeneratedRegex(@"Max\.\s*attempts:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MaxAttemptsRegex();

    public static ElmsAssignment? Parse(
        string html,
        string assignmentUrl,
        string courseId,
        string baseUrl = "https://elms.sti.edu",
        ElmsAssignmentSummary? summary = null,
        int? referenceYear = null
    )
    {
        var doc = HtmlParsingHelpers.ParseDocument(html);
        var centre = doc.QuerySelector("#centreColumn, #mainContent") ?? doc.Body;
        if (centre is null)
            return null;

        var (lmsId, kind) = AssignmentsListParser.ParseIdAndKind(assignmentUrl);
        if (kind == ElmsAssignmentKind.Unknown)
        {
            var canonicalTabHref = centre
                .QuerySelector(
                    "#tab_instructions[href*=\"_assignment/show/\"], a[href*=\"_assignment/show/\"]"
                )
                ?.GetAttribute("href");
            if (!string.IsNullOrWhiteSpace(canonicalTabHref))
            {
                assignmentUrl = HtmlParsingHelpers.ToAbsoluteUrl(canonicalTabHref, baseUrl);
                (lmsId, kind) = AssignmentsListParser.ParseIdAndKind(assignmentUrl);
            }
        }
        if (summary is not null && kind == ElmsAssignmentKind.Unknown)
            kind = summary.Kind;

        // Query #fixedSectionHeader h2 first so we don't accidentally match the module h1 ("Standalone assignment")
        var titleEl = centre.QuerySelector("#fixedSectionHeader h2")
            ?? centre.QuerySelector("h2.assignment-title, .assignment-title")
            ?? centre.QuerySelector("h2, h3, h1, .page-title");

        var title = HtmlParsingHelpers.NormalizeWhitespace(titleEl?.TextContent);
        if (
            string.IsNullOrWhiteSpace(title)
            || title.Equals("Standalone assignment", StringComparison.OrdinalIgnoreCase)
        )
            title = summary?.Title ?? string.Empty;

        if (string.IsNullOrWhiteSpace(title))
            return null;

        var moduleHeadingEl = centre.QuerySelector("#fixedSectionHeader h1.centerHeading a");
        var parentModuleTitle = HtmlParsingHelpers.NormalizeWhitespace(
            moduleHeadingEl?.TextContent
        );
        if (
            string.IsNullOrWhiteSpace(parentModuleTitle)
            || parentModuleTitle.Equals("Standalone assignment", StringComparison.OrdinalIgnoreCase)
        )
            parentModuleTitle = summary?.ParentModuleTitle;

        var (termFromModule, _) = HtmlParsingHelpers.ExtractTermAndPrefix(parentModuleTitle);
        var (termFromTitle, _) = HtmlParsingHelpers.ExtractTermAndPrefix(title);
        var termHeader = termFromModule ?? termFromTitle ?? summary?.TermHeader;

        var rightCol = centre.QuerySelector("aside.rightColumn, .grading_fixed_col");
        var rightColText = HtmlParsingHelpers.NormalizeWhitespace(rightCol?.TextContent);
        if (
            kind == ElmsAssignmentKind.Unknown
            && rightColText.Contains("Type: Quiz", StringComparison.OrdinalIgnoreCase)
        )
            kind = ElmsAssignmentKind.Quiz;

        int? maxScore = null;
        var maxScoreMatch = MaxScoreRegex().Match(rightColText);
        if (maxScoreMatch.Success && int.TryParse(maxScoreMatch.Groups[1].Value, out var ms))
            maxScore = ms;

        var dueEl = centre.QuerySelector(".due-date, .due_date, [data-due-date], .deadline");
        var dueRaw = dueEl?.GetAttribute("data-due-date") ?? dueEl?.TextContent;
        if (string.IsNullOrWhiteSpace(dueRaw))
        {
            var dueLineMatch = DueLineRegex().Match(rightCol?.TextContent ?? string.Empty);
            if (dueLineMatch.Success)
                dueRaw = dueLineMatch.Groups[1].Value;
        }

        var year = referenceYear ?? DateTimeOffset.Now.Year;
        var dueDate = TryParseFlexibleDate(dueRaw, year)
            ?? summary?.DueDate
            ?? TryParseDueDateFromPageText(centre.TextContent, year);

        DateTimeOffset? startDate = null;
        string? timerText = null;
        if (rightCol is not null)
        {
            foreach (var li in rightCol.QuerySelectorAll("li"))
            {
                var liText = HtmlParsingHelpers.NormalizeWhitespace(li.TextContent);
                if (liText.StartsWith("Start:", StringComparison.OrdinalIgnoreCase))
                {
                    startDate ??= TryParseFlexibleDate(liText["Start:".Length..].Trim(), year);
                }
                else if (liText.StartsWith("Timer:", StringComparison.OrdinalIgnoreCase))
                {
                    var t = liText["Timer:".Length..].Trim();
                    if (!string.IsNullOrWhiteSpace(t))
                        timerText ??= t;
                }
            }
        }

        var submissionInfo = ParseSubmissionBlock(rightCol, baseUrl);
        var status = DetermineStatus(centre, rightColText, submissionInfo, summary);

        var score = summary?.ScoreText ?? ExtractScoreText(rightCol, centre);

        var instructionsEl = centre.QuerySelector(
            ".leftColumn .materialStyle, .instructions, .assignment-description, .description"
        );
        if (
            instructionsEl is not null
            && string.IsNullOrWhiteSpace(
                HtmlParsingHelpers.NormalizeWhitespace(instructionsEl.TextContent)
            )
        )
            instructionsEl = null;
        instructionsEl ??= centre.QuerySelector(".leftColumn, main, article") ?? centre;

        string? instructionsHtml;
        if (instructionsEl.ClassList.Contains("leftColumn"))
        {
            var clone = instructionsEl.Clone(true) as IElement;
            if (clone is not null)
            {
                foreach (
                    var actionBar in clone.QuerySelectorAll(".action-toolbar, .action-menu, script")
                )
                    actionBar.Remove();
                var firstH2 = clone.QuerySelector("h2");
                if (
                    firstH2 is not null
                    && HtmlParsingHelpers
                        .NormalizeWhitespace(firstH2.TextContent)
                        .Equals("Instructions", StringComparison.OrdinalIgnoreCase)
                )
                    firstH2.Remove();
                instructionsHtml = clone.InnerHtml;
            }
            else
            {
                instructionsHtml = instructionsEl.InnerHtml;
            }
        }
        else
        {
            instructionsHtml = instructionsEl.InnerHtml;
        }
        var instructionsMd = HtmlToMarkdownConverter.Convert(instructionsHtml);

        var targetFolder = status is AssignmentStatus.Submitted or AssignmentStatus.Graded
            ? "completed"
            : "ongoing";
        var attachments = HtmlParsingHelpers.ExtractFileResources(
            instructionsEl,
            baseUrl,
            relativeTargetFolder: targetFolder,
            kind: ElmsResourceKind.AssignmentAttachment,
            courseId: courseId,
            sourceTitle: title,
            includeExternalLinks: true
        );

        return new ElmsAssignment(
            LmsId: lmsId,
            CourseId: courseId,
            Title: title,
            Url: assignmentUrl,
            DueDate: dueDate,
            Status: status,
            Score: score,
            InstructionsHtml: instructionsHtml,
            Attachments: attachments,
            Kind: kind,
            ParentModuleTitle: parentModuleTitle,
            TermHeader: termHeader,
            MaxScore: maxScore,
            WeightPercent: summary?.WeightPercent,
            InstructionsMarkdown: string.IsNullOrWhiteSpace(instructionsMd) ? null : instructionsMd,
            Submission: submissionInfo,
            StartDate: startDate,
            TimerText: timerText
        );
    }

    /// <summary>
    /// Parses /student_*_assignment/submissions/{id} to extract downloadable files submitted by the student.
    /// </summary>
    public static IReadOnlyList<ElmsResource> ParseSubmissionFiles(
        string html,
        string courseId,
        string assignmentTitle,
        string baseUrl = "https://elms.sti.edu"
    )
    {
        var doc = HtmlParsingHelpers.ParseDocument(html);
        var leftCol = doc.QuerySelector("#centreColumn .leftColumn, #mainContent .leftColumn")
            ?? doc.Body;

        return HtmlParsingHelpers.ExtractFileResources(
            leftCol,
            baseUrl,
            relativeTargetFolder: "completed",
            kind: ElmsResourceKind.SubmittedArtifact,
            courseId: courseId,
            sourceTitle: assignmentTitle
        );
    }

    internal static DateTimeOffset? TryParseFlexibleDate(string? raw, int defaultYear)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var normalized = HtmlParsingHelpers.NormalizeWhitespace(raw);

        // 1. If a 4-digit year is absent (e.g. "Oct 12, 8:00 pm" or "Sep 20"), parse with explicit defaultYear first
        if (!FourDigitYearRegex().IsMatch(normalized))
        {
            var m = MonthDayOptionalTimeRegex().Match(normalized);
            if (m.Success)
            {
                var month = m.Groups[1].Value;
                var day = m.Groups[2].Value;
                var time = m.Groups[3].Success ? m.Groups[3].Value.ToUpperInvariant() : null;

                var candidateWithYear = time is not null
                    ? $"{month} {day} {defaultYear} {time}"
                    : $"{month} {day} {defaultYear}";

                string[] formats =
                [
                    "MMM d yyyy h:mm tt",
                    "MMM d yyyy hh:mm tt",
                    "MMMM d yyyy h:mm tt",
                    "MMMM d yyyy hh:mm tt",
                    "MMM d yyyy",
                    "MMMM d yyyy",
                ];

                if (
                    DateTimeOffset.TryParseExact(
                        HtmlParsingHelpers.NormalizeWhitespace(candidateWithYear),
                        formats,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal,
                        out var exactNoYear
                    )
                )
                {
                    return exactNoYear;
                }
            }
            return null;
        }

        // 2. Contains a 4-digit year (e.g. "2026-11-25 23:59" or "Nov 25, 2026")
        if (
            DateTimeOffset.TryParse(
                normalized,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var direct
            )
        )
            return direct;

        return null;
    }

    private static ElmsSubmissionInfo ParseSubmissionBlock(IElement? rightCol, string baseUrl)
    {
        if (rightCol is null)
            return ElmsSubmissionInfo.Empty;

        var rawText = rightCol.TextContent;
        var isClosed = rawText.Contains(
            "No more submissions are allowed",
            StringComparison.OrdinalIgnoreCase
        );

        string? submittedAtText = null;
        var subMatch = SubmittedLineRegex().Match(rawText);
        if (subMatch.Success)
            submittedAtText = HtmlParsingHelpers.NormalizeWhitespace(subMatch.Groups[1].Value);

        var attemptsEl = rightCol.QuerySelector(".number-of-attempts");
        int attempts = int.TryParse(
            HtmlParsingHelpers.NormalizeWhitespace(attemptsEl?.TextContent),
            out var a
        )
            ? a
            : 0;

        int? maxAttempts = null;
        var maxAttMatch = MaxAttemptsRegex().Match(rawText);
        if (maxAttMatch.Success && int.TryParse(maxAttMatch.Groups[1].Value, out var ma))
            maxAttempts = ma;

        var allowLate = !rawText.Contains(
            "Allow late submissions: No",
            StringComparison.OrdinalIgnoreCase
        );

        var detailsLink = rightCol
            .QuerySelector("a.sectionLink[href*=\"/submissions/\"]")
            ?.GetAttribute("href");
        var detailsUrl = string.IsNullOrWhiteSpace(detailsLink)
            ? null
            : HtmlParsingHelpers.ToAbsoluteUrl(detailsLink, baseUrl);

        var comments = rightCol
            .QuerySelectorAll("#grading-thread-comment-list .comment")
            .Select(c => HtmlParsingHelpers.NormalizeWhitespace(c.TextContent))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        var isSubmitted =
            attempts > 0 || !string.IsNullOrWhiteSpace(submittedAtText) || detailsUrl is not null;

        return new ElmsSubmissionInfo(
            IsSubmitted: isSubmitted,
            SubmittedAtText: submittedAtText,
            Attempts: attempts,
            MaxAttempts: maxAttempts,
            AllowLateSubmissions: allowLate,
            IsClosed: isClosed,
            SubmissionDetailsUrl: detailsUrl,
            Comments: comments,
            SubmittedFiles: []
        );
    }

    private static string? ExtractScoreText(IElement? rightCol, IElement centre)
    {
        var scoreEl = centre.QuerySelector(".score, .grade, .points");
        if (scoreEl is not null)
        {
            var s = HtmlParsingHelpers.NormalizeWhitespace(scoreEl.TextContent);
            if (!string.IsNullOrWhiteSpace(s))
                return s;
        }

        if (rightCol is null)
            return null;

        foreach (var block in rightCol.QuerySelectorAll(".block"))
        {
            var h2 = HtmlParsingHelpers.NormalizeWhitespace(block.QuerySelector("h2")?.TextContent);
            if (h2.Equals("Score", StringComparison.OrdinalIgnoreCase))
            {
                var p = HtmlParsingHelpers.NormalizeWhitespace(
                    block.QuerySelector("p")?.TextContent
                );
                if (
                    !string.IsNullOrWhiteSpace(p)
                    && !p.Contains("Nothing submitted", StringComparison.OrdinalIgnoreCase)
                    && !p.Contains("Waiting for grade", StringComparison.OrdinalIgnoreCase)
                    && !p.Contains("submission is used", StringComparison.OrdinalIgnoreCase)
                )
                {
                    return p;
                }
            }
        }

        return null;
    }

    private static AssignmentStatus DetermineStatus(
        IElement centre,
        string rightColText,
        ElmsSubmissionInfo submissionInfo,
        ElmsAssignmentSummary? summary
    )
    {
        if (submissionInfo.IsSubmitted)
        {
            if (rightColText.Contains("Waiting for grade", StringComparison.OrdinalIgnoreCase))
                return AssignmentStatus.Submitted;
            return summary?.Status == AssignmentStatus.Graded
                ? AssignmentStatus.Graded
                : AssignmentStatus.Submitted;
        }

        if (summary is not null && summary.Status != AssignmentStatus.Unknown)
            return summary.Status;

        var lower = centre.TextContent.ToLowerInvariant();
        if (
            lower.Contains("not submitted")
            || lower.Contains("nothing submitted yet")
            || lower.Contains("submit assignment")
            || lower.Contains("prepare answer")
        )
            return submissionInfo.IsClosed ? AssignmentStatus.Missing : AssignmentStatus.Pending;
        if (lower.Contains("missing") || lower.Contains("past due"))
            return AssignmentStatus.Missing;
        if (lower.Contains("graded"))
            return AssignmentStatus.Graded;
        if (lower.Contains("submitted"))
            return AssignmentStatus.Submitted;

        return AssignmentStatus.Unknown;
    }

    private static DateTimeOffset? TryParseDueDateFromPageText(string? bodyText, int defaultYear)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
            return null;

        var idx = bodyText.IndexOf("Due", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        var slice = bodyText.Substring(idx, Math.Min(120, bodyText.Length - idx));
        return TryParseFlexibleDate(slice, defaultYear);
    }
}
