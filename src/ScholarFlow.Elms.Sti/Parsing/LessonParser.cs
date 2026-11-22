using ScholarFlow.Core.Services;
using ScholarFlow.Elms.Models;

using ScholarFlow.Elms.Parsing;

namespace ScholarFlow.Elms.Sti.Parsing;

/// <summary>
/// Parses an individual lesson or handout section page (/student_lesson/show/{id}) for content, attachments, and SCORM/iframe players.
/// </summary>
public static class LessonParser
{
    public static ElmsLesson Parse(
        string html,
        string lessonUrl,
        string courseId,
        string? termHeader = null,
        string baseUrl = "https://elms.sti.edu",
        string? fallbackTitle = null
    )
    {
        var doc = HtmlParsingHelpers.ParseDocument(html);
        var centre = doc.QuerySelector("#centreColumn, #mainContent") ?? doc.Body;

        var titleEl = centre?.QuerySelector(
            "#fixedSectionHeader h2, h2, h3, .lesson-title, .page-title"
        );
        var title = HtmlParsingHelpers.NormalizeWhitespace(titleEl?.TextContent);
        if (string.IsNullOrWhiteSpace(title))
            title = fallbackTitle ?? "Untitled_Lesson";

        var folder = FilenameSanitizer.SanitizeFolderName(title);

        if (string.IsNullOrWhiteSpace(termHeader))
        {
            var moduleHeading = centre
                ?.QuerySelector("#fixedSectionHeader h1.centerHeading")
                ?.TextContent;
            termHeader = HtmlParsingHelpers.ExtractTermAndPrefix(moduleHeading).TermHeader;
        }

        var hasScorm =
            lessonUrl.Contains("scorm_player.html", StringComparison.OrdinalIgnoreCase)
            || centre?.QuerySelector("iframe[src*=\"scorm\"], iframe[src*=\"player\"]") is not null;

        var contentScope =
            centre?.QuerySelector(".lesson-content, .content-body, .materialStyle, main, article")
            ?? centre;
        var resources = HtmlParsingHelpers.ExtractFileResources(
            contentScope,
            baseUrl,
            relativeTargetFolder: folder,
            kind: ElmsResourceKind.LessonAttachment,
            courseId: courseId,
            sourceTitle: title
        );

        var markdown = HtmlToMarkdownConverter.Convert(contentScope?.InnerHtml);

        return new ElmsLesson(
            LessonUrl: lessonUrl,
            Title: title,
            SanitizedFolderName: folder,
            TermHeader: termHeader,
            HtmlContent: contentScope?.InnerHtml,
            Resources: resources,
            HasScormOrEmbeddedPlayer: hasScorm,
            MarkdownContent: string.IsNullOrWhiteSpace(markdown) ? null : markdown
        );
    }
}
