using System.Net;
using Microsoft.Extensions.Logging;
using ScholarFlow.Core.Sync;
using ScholarFlow.Elms.Abstractions;
using ScholarFlow.Elms.Auth;
using ScholarFlow.Elms.Models;
using ScholarFlow.Elms.Parsing;
using ScholarFlow.Elms.Sti.Auth;
using ScholarFlow.Elms.Sti.Parsing;
using ScholarFlow.Elms.Sync;

namespace ScholarFlow.Elms.Sti.Client;

/// <summary>
/// Concurrent HttpClient + AngleSharp implementation of <see cref="IElmsClient"/> backed by <see cref="IElmsAuthProvider"/>.
/// </summary>
public sealed class StiElmsClient : IElmsClient
{
    private readonly IElmsAuthProvider _authProvider;
    private readonly ILogger<StiElmsClient>? _logger;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    private AuthOptions _authOptions = new();
    private ElmsSession? _session;
    private readonly CookieContainer _cookieContainer = new();
    private readonly HttpClientHandler _httpHandler;
    private readonly HttpClient _httpClient;

    public StiElmsClient(
        IElmsAuthProvider? authProvider = null,
        ILogger<StiElmsClient>? logger = null
    )
    {
        _authProvider = authProvider ?? new PlaywrightAuthProvider();
        _logger = logger;
        _httpHandler = new HttpClientHandler
        {
            CookieContainer = _cookieContainer,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        _httpClient = new HttpClient(_httpHandler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public HttpClient HttpClient =>
        _session is not null
            ? _httpClient
            : throw new InvalidOperationException(
                "Client is not connected. Call EnsureConnectedAsync first."
            );
    public string BaseUrl => _authOptions.BaseUrl.TrimEnd('/');

    public async Task EnsureConnectedAsync(
        AuthOptions? options = null,
        CancellationToken ct = default
    )
    {
        if (options is not null)
        {
            _authOptions = options;
        }

        if (_session is not null && options?.ForceInteractive != true)
            return;

        await _authLock.WaitAsync(ct);
        try
        {
            if (options is not null)
                _authOptions = options;

            if (_session is not null && options?.ForceInteractive != true)
                return;

            _session = await _authProvider.EnsureAuthenticatedAsync(_authOptions, ct);
            ApplySessionToClient(_session);
        }
        finally
        {
            _authLock.Release();
        }
    }

    public async Task<IReadOnlyList<ElmsCourse>> GetEnrolledCoursesAsync(
        CourseFilter? filter = null,
        bool includeCompleted = false,
        bool includeAdministrative = false,
        CancellationToken ct = default
    )
    {
        var html = await FetchAuthenticatedHtmlAsync("/user_dashboard", ct);
        var courses = DashboardParser.ParseEnrolledCourses(
            html,
            BaseUrl,
            includeCompleted,
            includeAdministrative
        );
        return filter is null ? courses : filter.Apply(courses);
    }

    public async Task<ElmsCourseSnapshot> GetCourseSnapshotAsync(
        ElmsCourse course,
        IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        var warnings = new List<string>();
        progress?.Report(
            new SyncProgress(
                "Discover",
                $"Scanning course: {course.Title}",
                CourseTitle: course.Title
            )
        );

        var courseHtml = await FetchAuthenticatedHtmlAsync(course.CourseUrl, ct);
        var parsedCourse = CoursePageParser.Parse(courseHtml, course, BaseUrl);

        if (parsedCourse.Modules.Count == 0 && parsedCourse.GeneralResources.Count == 0)
        {
            warnings.Add(
                $"No modules or resources discovered on {course.CourseUrl}; course may be empty or use non-standard layout."
            );
        }

        // Build target list of handout/lesson pages to scrape (skipping SCORM Online Presentations and embedded assignments)
        var lessonTargets = new List<(string Url, string Title, string? TermHeader)>();
        foreach (var mod in parsedCourse.Modules)
        {
            if (mod.ModuleSections.Count > 0)
            {
                foreach (
                    var sec in mod.ModuleSections.Where(s =>
                        s.Kind is ElmsSectionKind.Handout or ElmsSectionKind.Other
                    )
                )
                {
                    lessonTargets.Add((sec.Url, sec.Title, mod.TermHeader));
                }
            }
            else if (
                !mod.IsScormOrExternal
                && !mod.LessonUrl.Contains("scorm_player.html", StringComparison.OrdinalIgnoreCase)
            )
            {
                lessonTargets.Add((mod.LessonUrl, mod.Title, mod.TermHeader));
            }
        }

        using var throttle = new SemaphoreSlim(4, 4);
        var lessonTasks = lessonTargets.Select(async target =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                return await GetLessonAsync(
                    target.Url,
                    course.CourseId,
                    target.TermHeader,
                    target.Title,
                    ct
                );
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (warnings)
                    warnings.Add(
                        $"Failed to fetch lesson '{target.Title}' ({target.Url}): {ex.Message}"
                    );
                return null;
            }
            finally
            {
                throttle.Release();
            }
        });

        var fetchedLessons = await Task.WhenAll(lessonTasks);
        var lessons = fetchedLessons.Where(l => l is not null).Select(l => l!).ToList();

        progress?.Report(
            new SyncProgress(
                "Assignments",
                $"Scanning assignments for {course.Title}",
                CourseTitle: course.Title
            )
        );
        var assignments = await FetchCourseAssignmentsInternalAsync(
            course.CourseId,
            parsedCourse,
            throttle,
            warnings,
            ct
        );

        return new ElmsCourseSnapshot(
            Course: course,
            GeneralResources: parsedCourse.GeneralResources,
            Modules: parsedCourse.Modules,
            Lessons: lessons,
            Assignments: assignments,
            Warnings: warnings,
            CapturedAt: DateTimeOffset.UtcNow
        );
    }

    public async Task<ElmsLesson> GetLessonAsync(
        string lessonUrl,
        string courseId,
        string? termHeader = null,
        string? fallbackTitle = null,
        CancellationToken ct = default
    )
    {
        var html = await FetchAuthenticatedHtmlAsync(lessonUrl, ct);
        return LessonParser.Parse(html, lessonUrl, courseId, termHeader, BaseUrl, fallbackTitle);
    }

    public async Task<IReadOnlyList<ElmsAssignment>> GetAssignmentsAsync(
        string courseId,
        CancellationToken ct = default
    )
    {
        var courseUrl = $"{BaseUrl}/student_class/show/{courseId}";
        var courseHtml = await FetchAuthenticatedHtmlAsync(courseUrl, ct);
        var parsed = CoursePageParser.Parse(
            courseHtml,
            new ElmsCourse(courseId, courseId, courseUrl, courseId),
            BaseUrl
        );

        using var throttle = new SemaphoreSlim(4, 4);
        var warnings = new List<string>();
        return await FetchCourseAssignmentsInternalAsync(courseId, parsed, throttle, warnings, ct);
    }

    private async Task<IReadOnlyList<ElmsAssignment>> FetchCourseAssignmentsInternalAsync(
        string courseId,
        ParsedCoursePage parsedCourse,
        SemaphoreSlim throttle,
        List<string> warnings,
        CancellationToken ct
    )
    {
        var summariesByUrl = new Dictionary<string, ElmsAssignmentSummary>(
            StringComparer.OrdinalIgnoreCase
        );

        var extraAssignmentUrls = new List<string>();

        try
        {
            var listHtml = await FetchAuthenticatedHtmlAsync(parsedCourse.AssignmentsListUrl, ct);
            foreach (var summary in AssignmentsListParser.Parse(listHtml, courseId, BaseUrl))
            {
                summariesByUrl[ElmsUrlCanonicalizer.GetCanonicalUrl(summary.Url)] = summary;
            }

            // Follow paginated assignment list pages (if any) so standalone/upcoming items on page 2+ aren't missed
            var listDoc = HtmlParsingHelpers.ParseDocument(listHtml);
            var pageUrls = listDoc
                .QuerySelectorAll("a[href*=\"/student_assignments/list/\"][href*=\"page=\"]")
                .Select(a => a.GetAttribute("href"))
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .Select(h => HtmlParsingHelpers.ToAbsoluteUrl(h!, BaseUrl))
                .DistinctBy(ElmsUrlCanonicalizer.GetCanonicalUrl, StringComparer.OrdinalIgnoreCase)
                .Where(u =>
                    !ElmsUrlCanonicalizer
                        .GetCanonicalUrl(u)
                        .Equals(
                            ElmsUrlCanonicalizer.GetCanonicalUrl(parsedCourse.AssignmentsListUrl),
                            StringComparison.OrdinalIgnoreCase
                        )
                )
                .Take(5)
                .ToList();

            foreach (var pageUrl in pageUrls)
            {
                var pageHtml = await FetchAuthenticatedHtmlAsync(pageUrl, ct);
                foreach (var summary in AssignmentsListParser.Parse(pageHtml, courseId, BaseUrl))
                {
                    summariesByUrl[ElmsUrlCanonicalizer.GetCanonicalUrl(summary.Url)] = summary;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (warnings)
                warnings.Add(
                    $"Could not fetch assignments list ({parsedCourse.AssignmentsListUrl}): {ex.Message}"
                );
        }

        // Also check Course Dashboard (/student_class_dashboard/show/{courseId}) for standalone upcoming/not-yet-started items
        try
        {
            var dashHtml = await FetchAuthenticatedHtmlAsync(
                $"{BaseUrl}/student_class_dashboard/show/{courseId}",
                ct
            );
            var dashDoc = HtmlParsingHelpers.ParseDocument(dashHtml);
            foreach (
                var link in dashDoc.QuerySelectorAll(
                    "a[href*=\"_assignment/show/\"], a[href*=\"/student_quiz/\"], a[href*=\"/assignment/show/\"]"
                )
            )
            {
                var href = link.GetAttribute("href");
                if (!string.IsNullOrWhiteSpace(href))
                    extraAssignmentUrls.Add(HtmlParsingHelpers.ToAbsoluteUrl(href, BaseUrl));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (warnings)
                warnings.Add($"Could not fetch course dashboard ({courseId}): {ex.Message}");
        }

        var listedTitles = summariesByUrl
            .Values.Select(s => HtmlParsingHelpers.NormalizeWhitespace(s.Title))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var moduleAssignmentUrls = parsedCourse
            .Modules.SelectMany(m => m.ModuleSections)
            .Where(s =>
                s.Kind == ElmsSectionKind.Assignment
                && !listedTitles.Contains(HtmlParsingHelpers.NormalizeWhitespace(s.Title))
            )
            .Select(s => s.Url);

        var targetUrls = summariesByUrl
            .Values.Select(s => s.Url)
            .Concat(parsedCourse.AssignmentUrls)
            .Concat(extraAssignmentUrls)
            .Concat(moduleAssignmentUrls)
            .DistinctBy(ElmsUrlCanonicalizer.GetCanonicalUrl, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tasks = targetUrls.Select(async url =>
        {
            summariesByUrl.TryGetValue(ElmsUrlCanonicalizer.GetCanonicalUrl(url), out var summary);

            await throttle.WaitAsync(ct);
            try
            {
                var (html, finalUrl) = await FetchAuthenticatedHtmlWithFinalUrlAsync(url, ct);
                var assignment = AssignmentParser.Parse(html, finalUrl, courseId, BaseUrl, summary);
                if (assignment is null)
                    return SummaryToFallbackAssignment(summary);

                var subUrl = assignment.SubmissionInfo.SubmissionDetailsUrl;
                if (!string.IsNullOrWhiteSpace(subUrl))
                {
                    try
                    {
                        var subHtml = await FetchAuthenticatedHtmlAsync(subUrl, ct);
                        var submittedFiles = AssignmentParser.ParseSubmissionFiles(
                            subHtml,
                            courseId,
                            assignment.Title,
                            BaseUrl
                        );
                        if (submittedFiles.Count > 0)
                        {
                            assignment = assignment with
                            {
                                Submission = assignment.SubmissionInfo with
                                {
                                    SubmittedFiles = submittedFiles,
                                },
                            };
                        }
                    }
                    catch (Exception subEx) when (subEx is not OperationCanceledException)
                    {
                        lock (warnings)
                            warnings.Add(
                                $"Could not fetch submission details ({subUrl}): {subEx.Message}"
                            );
                    }
                }

                return assignment;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (warnings)
                    warnings.Add($"Failed to fetch assignment ({url}): {ex.Message}");
                return SummaryToFallbackAssignment(summary);
            }
            finally
            {
                throttle.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return
        [
            .. results
                .Where(a => a is not null)
                .Select(a => a!)
                .DistinctBy(a => a.LmsId, StringComparer.OrdinalIgnoreCase),
        ];
    }

    private static ElmsAssignment? SummaryToFallbackAssignment(ElmsAssignmentSummary? summary)
    {
        if (summary is null)
            return null;

        return new ElmsAssignment(
            LmsId: summary.LmsId,
            CourseId: summary.CourseId,
            Title: summary.Title,
            Url: summary.Url,
            DueDate: summary.DueDate,
            Status: summary.Status,
            Score: summary.ScoreText,
            InstructionsHtml: null,
            Attachments: [],
            Kind: summary.Kind,
            ParentModuleTitle: summary.ParentModuleTitle,
            TermHeader: summary.TermHeader,
            WeightPercent: summary.WeightPercent
        );
    }

    public async Task<ElmsAssignment?> GetAssignmentAsync(
        string assignmentUrl,
        string courseId,
        CancellationToken ct = default
    )
    {
        var html = await FetchAuthenticatedHtmlAsync(assignmentUrl, ct);
        return AssignmentParser.Parse(html, assignmentUrl, courseId, BaseUrl);
    }

    public async Task<string> FetchAuthenticatedHtmlAsync(
        string urlOrPath,
        CancellationToken ct = default
    )
    {
        var (html, _) = await FetchAuthenticatedHtmlWithFinalUrlAsync(urlOrPath, ct);
        return html;
    }

    private async Task<(string Html, string FinalUrl)> FetchAuthenticatedHtmlWithFinalUrlAsync(
        string urlOrPath,
        CancellationToken ct = default
    )
    {
        await EnsureConnectedAsync(ct: ct);
        var targetUrl = HtmlParsingHelpers.ToAbsoluteUrl(urlOrPath, BaseUrl);
        var sessionBefore = _session;

        using var response = await _httpClient.GetAsync(targetUrl, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await RefreshAndRebuildAsync(sessionBefore, ct);
            using var retry = await _httpClient.GetAsync(targetUrl, ct);
            retry.EnsureSuccessStatusCode();
            var finalRetryUrl = retry.RequestMessage?.RequestUri?.ToString() ?? targetUrl;
            return (await retry.Content.ReadAsStringAsync(ct), finalRetryUrl);
        }

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(ct);
        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? targetUrl;

        if (HtmlParsingHelpers.IsLoginRedirectHtml(html, response.RequestMessage?.RequestUri))
        {
            _logger?.LogWarning(
                "Detected login redirect while fetching {Url}; refreshing session.",
                targetUrl
            );
            await RefreshAndRebuildAsync(sessionBefore, ct);
            using var retry = await _httpClient.GetAsync(targetUrl, ct);
            retry.EnsureSuccessStatusCode();
            var retryHtml = await retry.Content.ReadAsStringAsync(ct);
            var finalRetryUrl = retry.RequestMessage?.RequestUri?.ToString() ?? targetUrl;
            await PersistRotatedCookiesIfNeededAsync(ct);
            return (retryHtml, finalRetryUrl);
        }

        await PersistRotatedCookiesIfNeededAsync(ct);
        return (html, finalUrl);
    }

    private async Task RefreshAndRebuildAsync(ElmsSession? failedSession, CancellationToken ct)
    {
        await _authLock.WaitAsync(ct);
        try
        {
            if (_session is not null && !ReferenceEquals(_session, failedSession))
            {
                // Another concurrent task already refreshed the session while we waited for the lock.
                return;
            }

            _session = await _authProvider.RefreshSessionAsync(_authOptions, ct);
            ApplySessionToClient(_session);
        }
        finally
        {
            _authLock.Release();
        }
    }

    private void ApplySessionToClient(ElmsSession session)
    {
        PlaywrightAuthProvider.PopulateCookieContainer(_cookieContainer, session);
        _httpClient.DefaultRequestHeaders.UserAgent.Clear();
        if (!string.IsNullOrWhiteSpace(session.UserAgent))
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(session.UserAgent);
        }
    }

    private async Task PersistRotatedCookiesIfNeededAsync(CancellationToken ct)
    {
        var current = _session;
        if (current is null || string.IsNullOrWhiteSpace(_authOptions.SessionFilePath))
            return;

        try
        {
            var baseUri = new Uri(current.BaseUrl);
            var containerCookies = _cookieContainer.GetCookies(baseUri);
            var liveSessionCookie = containerCookies["lms_session_v1"]?.Value;
            var cachedSessionCookie = current
                .Cookies.FirstOrDefault(c =>
                    c.Name.Equals("lms_session_v1", StringComparison.OrdinalIgnoreCase)
                )
                ?.Value;

            if (
                !string.IsNullOrWhiteSpace(liveSessionCookie)
                && !string.Equals(liveSessionCookie, cachedSessionCookie, StringComparison.Ordinal)
            )
            {
                await _authLock.WaitAsync(ct);
                try
                {
                    if (_session is not null)
                    {
                        _session = PlaywrightAuthProvider.MergeContainerCookies(
                            _session,
                            _cookieContainer,
                            replaceStiCookies: false
                        );
                        await ElmsSessionStore.SaveAsync(
                            _session,
                            _authOptions.SessionFilePath,
                            ct
                        );
                    }
                }
                finally
                {
                    _authLock.Release();
                }
            }
        }
        catch
        {
            // Ignore non-fatal cookie persistence race
        }
    }

    public ISyncExecutor CreateSyncExecutor() => new SyncExecutor(HttpClient);

    public ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        _httpHandler.Dispose();
        _authLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
