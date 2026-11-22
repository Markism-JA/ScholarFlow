using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ScholarFlow.Core.Services;
using ScholarFlow.Elms.Models;

namespace ScholarFlow.Elms.Parsing;

public static partial class HtmlParsingHelpers
{
    private static readonly HtmlParser Parser = new();

    [GeneratedRegex(@"^\s*(\d{2})\b", RegexOptions.Compiled)]
    private static partial Regex LeadingModuleNumberRegex();

    [GeneratedRegex(
        @"\((Prelims?|Midterms?|Pre-?finals?|Finals?)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    )]
    private static partial Regex ParenthesizedTermRegex();

    public static IDocument ParseDocument(string html) => Parser.ParseDocument(html);

    public static string ToAbsoluteUrl(string href, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(href))
            return string.Empty;

        var trimmed = href.Trim();
        if (
            trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        )
        {
            return trimmed;
        }

        if (
            Uri.TryCreate(
                new Uri(baseUrl.TrimEnd('/') + "/"),
                trimmed.TrimStart('/'),
                out var resolved
            )
        )
        {
            return resolved.ToString();
        }

        return trimmed;
    }

    public static bool IsLoginRedirectHtml(string html, Uri? finalUri)
    {
        if (finalUri is not null)
        {
            var pathAndQuery = finalUri.PathAndQuery;
            if (
                pathAndQuery.Contains("not_logged_in", StringComparison.OrdinalIgnoreCase)
                || pathAndQuery.Contains(
                    "simultaneous_logins_detected",
                    StringComparison.OrdinalIgnoreCase
                )
                || pathAndQuery.Contains("log_in_required=true", StringComparison.OrdinalIgnoreCase)
                || finalUri.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase)
                || finalUri.AbsolutePath.Contains("log_in", StringComparison.OrdinalIgnoreCase)
            )
            {
                return true;
            }
        }

        if (html.Contains("simultaneous_logins_detected", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (
            html.Contains("/site/not_logged_in", StringComparison.OrdinalIgnoreCase)
            && html.Contains("log_in_required=true", StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        return html.Contains("name=\"password\"", StringComparison.OrdinalIgnoreCase)
            && html.Contains("login", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts normalized term name (Prelims, Midterms, Prefinals, Finals) and 2-digit module prefix (e.g. "01") from a module title.
    /// </summary>
    public static (string? TermHeader, string? ModulePrefix) ExtractTermAndPrefix(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return (null, null);

        string? prefix = null;
        var numMatch = LeadingModuleNumberRegex().Match(title);
        if (numMatch.Success)
            prefix = numMatch.Groups[1].Value;
        var termMatch = ParenthesizedTermRegex().Match(title);

        string? term;
        if (termMatch.Success)
        {
            term = NormalizeTermName(termMatch.Groups[1].Value);
        }
        else
        {
            term = NormalizeTermName(title);
        }

        return (term, prefix);
    }

    public static string? NormalizeTermName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var lower = raw.ToLowerInvariant();
        if (lower.Contains("prelim"))
            return "Prelims";
        if (lower.Contains("midterm"))
            return "Midterms";
        if (lower.Contains("prefinal") || lower.Contains("pre-final"))
            return "Prefinals";
        if (lower.Contains("final"))
            return "Finals";

        return null;
    }

    /// <summary>
    /// Extracts downloadable file links (e.g. /files/...) and optional external links from an AngleSharp scope element.
    /// </summary>
    public static List<ElmsResource> ExtractFileResources(
        IElement? scope,
        string baseUrl,
        string relativeTargetFolder,
        ElmsResourceKind kind,
        string? courseId = null,
        string? sourceTitle = null,
        bool includeExternalLinks = false
    )
    {
        var results = new List<ElmsResource>();
        if (scope is null)
            return results;

        var seenCanonicalUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var anchor in scope.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href");
            if (
                string.IsNullOrWhiteSpace(href)
                || href.StartsWith('#')
                || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            )
                continue;

            var absUrl = ToAbsoluteUrl(href, baseUrl);
            var isLmsFile =
                href.Contains("/files/", StringComparison.OrdinalIgnoreCase)
                || href.Contains("/file/", StringComparison.OrdinalIgnoreCase)
                || anchor.HasAttribute("download");

            if (!isLmsFile)
            {
                if (includeExternalLinks && IsExternalAcademicLink(absUrl, baseUrl))
                {
                    var extCanonical = ElmsUrlCanonicalizer.GetCanonicalUrl(absUrl);
                    if (seenCanonicalUrls.Add(extCanonical))
                    {
                        var extTitle = NormalizeWhitespace(anchor.TextContent);
                        if (string.IsNullOrWhiteSpace(extTitle))
                            extTitle = absUrl;

                        results.Add(
                            new ElmsResource(
                                Url: absUrl,
                                DisplayName: extTitle,
                                SanitizedFileName: FilenameSanitizer.SanitizeFolderName(
                                    extTitle,
                                    "external_link"
                                ),
                                RelativeTargetFolder: relativeTargetFolder,
                                Kind: ElmsResourceKind.ExternalLink,
                                CourseId: courseId,
                                SourceTitle: sourceTitle
                            )
                        );
                    }
                }
                continue;
            }

            var canonical = ElmsUrlCanonicalizer.GetCanonicalUrl(absUrl);
            if (!seenCanonicalUrls.Add(canonical))
                continue;

            var displayText = NormalizeWhitespace(anchor.TextContent);
            // If display text is a raw /files/<id>/<name> path (e.g. on /submissions/ pages), extract just the filename
            if (
                string.IsNullOrWhiteSpace(displayText)
                || displayText.StartsWith("/files/", StringComparison.OrdinalIgnoreCase)
            )
            {
                displayText = Uri.UnescapeDataString(
                    Path.GetFileName(new Uri(absUrl).AbsolutePath)
                );
            }

            if (string.IsNullOrWhiteSpace(displayText))
                continue;

            var urlExt = TryGetExtensionFromUrl(absUrl);
            var sanitized = FilenameSanitizer.SanitizeFileName(displayText, urlExt);

            results.Add(
                new ElmsResource(
                    Url: absUrl,
                    DisplayName: displayText,
                    SanitizedFileName: sanitized,
                    RelativeTargetFolder: relativeTargetFolder,
                    Kind: kind,
                    CourseId: courseId,
                    SourceTitle: sourceTitle
                )
            );
        }

        return results;
    }

    public static string NormalizeWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Trim();
    }

    private static bool IsExternalAcademicLink(string absUrl, string baseUrl)
    {
        if (!Uri.TryCreate(absUrl, UriKind.Absolute, out var uri))
            return false;

        if (ElmsUrlCanonicalizer.IsSameOrSubdomain(uri.Host, new Uri(baseUrl).Host))
            return false;

        var host = uri.Host.ToLowerInvariant();
        if (
            host.Contains("facebook.com")
            || host.Contains("twitter.com")
            || host.Contains("instagram.com")
            || host.Contains("google.com/chrome")
            || host.Contains("mozilla.org")
            || host.Contains("apple.com")
            || host.Contains("microsoft.com/en-us/edge")
        )
        {
            return false;
        }

        return true;
    }

    private static string? TryGetExtensionFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var ext = Path.GetExtension(uri.AbsolutePath);
        return string.IsNullOrWhiteSpace(ext) || ext.Length > 10 ? null : ext;
    }
}
