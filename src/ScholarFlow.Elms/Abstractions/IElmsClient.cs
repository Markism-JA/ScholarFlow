using ScholarFlow.Core.Sync;
using ScholarFlow.Elms.Models;

namespace ScholarFlow.Elms.Abstractions;

/// <summary>
/// Unified API for discovering and scraping STI eLMS courses, modules, lessons, and assignments.
/// </summary>
public interface IElmsClient : IAsyncDisposable
{
    /// <summary>
    /// Ensures the client holds a valid authenticated session before making LMS requests.
    /// </summary>
    Task EnsureConnectedAsync(AuthOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Auto-discovers enrolled courses from the user's eLMS dashboard, filtering out completed and administrative shells by default.
    /// </summary>
    Task<IReadOnlyList<ElmsCourse>> GetEnrolledCoursesAsync(
        CourseFilter? filter = null,
        bool includeCompleted = false,
        bool includeAdministrative = false,
        CancellationToken ct = default
    );

    /// <summary>
    /// Captures a complete snapshot of a course: general files, modules/lessons, and assignments (including submitted artifacts).
    /// </summary>
    Task<ElmsCourseSnapshot> GetCourseSnapshotAsync(
        ElmsCourse course,
        IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Scrapes a single lesson/handout page for inline content and downloadable resources.
    /// </summary>
    Task<ElmsLesson> GetLessonAsync(
        string lessonUrl,
        string courseId,
        string? termHeader = null,
        string? fallbackTitle = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Scrapes assignments, deadlines, and submission details for the specified course.
    /// </summary>
    Task<IReadOnlyList<ElmsAssignment>> GetAssignmentsAsync(
        string courseId,
        CancellationToken ct = default
    );

    /// <summary>
    /// Fetches and parses a single assignment page by URL for targeted refresh.
    /// </summary>
    Task<ElmsAssignment?> GetAssignmentAsync(
        string assignmentUrl,
        string courseId,
        CancellationToken ct = default
    ) => Task.FromResult<ElmsAssignment?>(null);

    /// <summary>
    /// Fetches raw HTML of an authenticated eLMS URL for selector inspection and diagnostics.
    /// </summary>
    Task<string> FetchAuthenticatedHtmlAsync(string urlOrPath, CancellationToken ct = default);

    /// <summary>
    /// Creates a download executor bound to this client's authenticated HTTP transport.
    /// </summary>
    ISyncExecutor CreateSyncExecutor();
}
