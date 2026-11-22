using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using ScholarFlow.Elms.Abstractions;
using ScholarFlow.Elms.Models;
using ScholarFlow.Elms.Parsing;
using ScholarFlow.Elms.Sync;

namespace ScholarFlow.Elms.Auth;

/// <summary>
/// Authenticates against STI eLMS using cached HTTP cookies first, then persistent Playwright
/// headless profile + optional automated login, falling back to interactive Chromium only if needed.
/// </summary>
public sealed class PlaywrightAuthProvider(ILogger<PlaywrightAuthProvider>? logger = null)
    : IElmsAuthProvider
{
    private readonly ILogger<PlaywrightAuthProvider>? _logger = logger;

    public async Task<ElmsSession> EnsureAuthenticatedAsync(
        AuthOptions options,
        CancellationToken ct = default
    )
    {
        Directory.CreateDirectory(options.BrowserProfileDirectory);

        if (!options.ForceInteractive)
        {
            // 1. Fast path: validate cached session.json via HttpClient without launching Chromium
            var cached = await ElmsSessionStore.LoadAsync(options.SessionFilePath, ct);
            if (cached is not null)
            {
                var validated = await ValidateAndRefreshCookiesAsync(
                    cached,
                    options.SessionFilePath,
                    ct
                );
                if (validated is not null)
                {
                    if (_logger?.IsEnabled(LogLevel.Information) == true)
                    {
                        _logger.LogInformation(
                            "Reused cached HTTP session ({CookieCount} cookies).",
                            validated.Cookies.Count
                        );
                    }
                    return validated;
                }

                // 2. Silent HTTP Microsoft Entra SSO refresh using cached ESTSAUTHPERSISTENT cookies (zero browser launch)
                var silentSso = await TrySilentHttpSsoRefreshAsync(cached, options, ct);
                if (silentSso is not null)
                {
                    await ElmsSessionStore.SaveAsync(silentSso, options.SessionFilePath, ct);
                    if (_logger?.IsEnabled(LogLevel.Information) == true)
                    {
                        _logger.LogInformation(
                            "Silently refreshed eLMS session via HTTP Microsoft Entra SSO ({CookieCount} cookies).",
                            silentSso.Cookies.Count
                        );
                    }
                    return silentSso;
                }
            }

            // 3. Headless Playwright persistent profile (+ Microsoft SSO redirect or optional automated credential login)
            var credentials = ResolveCredentials(options);
            var headlessSession = await TryCaptureSessionAsync(
                options,
                headless: true,
                credentials,
                ct
            );
            if (headlessSession is not null)
            {
                await ElmsSessionStore.SaveAsync(headlessSession, options.SessionFilePath, ct);
                if (_logger?.IsEnabled(LogLevel.Information) == true)
                {
                    _logger.LogInformation(
                        "Established headless Playwright session ({CookieCount} cookies).",
                        headlessSession.Cookies.Count
                    );
                }
                return headlessSession;
            }

            if (!options.AllowInteractiveBrowser)
            {
                throw new ElmsAuthenticationRequiredException(
                    "eLMS session expired and interactive browser login is disabled for background sync. Click 'Sync eLMS' to sign in."
                );
            }

            _logger?.LogInformation(
                "Headless session unavailable; launching interactive login window."
            );
        }

        var interactiveSession =
            await TryCaptureSessionAsync(options, headless: false, ResolveCredentials(options), ct)
            ?? throw new InvalidOperationException(
                "Failed to establish an authenticated eLMS session."
            );

        await ElmsSessionStore.SaveAsync(interactiveSession, options.SessionFilePath, ct);
        return interactiveSession;
    }

    public async Task<bool> ValidateSessionAsync(
        ElmsSession session,
        CancellationToken ct = default
    )
    {
        var updated = await ValidateAndRefreshCookiesAsync(session, sessionFilePath: null, ct);
        return updated is not null;
    }

    private static async Task<ElmsSession?> ValidateAndRefreshCookiesAsync(
        ElmsSession session,
        string? sessionFilePath,
        CancellationToken ct
    )
    {
        if (!session.HasCookies)
            return null;

        try
        {
            using var handler = CreateCookieHandler(session);
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(session.UserAgent);

            var dashboardUrl = $"{session.BaseUrl.TrimEnd('/')}/user_dashboard";
            using var response = await client.GetAsync(dashboardUrl, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var html = await response.Content.ReadAsStringAsync(ct);
            if (HtmlParsingHelpers.IsLoginRedirectHtml(html, response.RequestMessage?.RequestUri))
                return null;

            var updatedSession = MergeContainerCookies(
                session,
                handler.CookieContainer,
                replaceStiCookies: false
            );
            if (!string.IsNullOrWhiteSpace(sessionFilePath))
            {
                await ElmsSessionStore.SaveAsync(updatedSession, sessionFilePath, ct);
            }

            return updatedSession;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to silently re-authenticate against STI eLMS over pure HttpClient using cached Microsoft Entra SSO cookies
    /// (ESTSAUTHPERSISTENT / ESTSAUTH on login.microsoftonline.com), avoiding any browser window launch.
    /// </summary>
    private async Task<ElmsSession?> TrySilentHttpSsoRefreshAsync(
        ElmsSession cachedSession,
        AuthOptions options,
        CancellationToken ct
    )
    {
        var msCookies = cachedSession
            .Cookies.Where(c =>
                c.Domain.Contains("microsoftonline.com", StringComparison.OrdinalIgnoreCase)
                || c.Domain.Contains("microsoft.com", StringComparison.OrdinalIgnoreCase)
                || c.Domain.Contains("live.com", StringComparison.OrdinalIgnoreCase)
                || c.Domain.Contains("msauth.net", StringComparison.OrdinalIgnoreCase)
            )
            .ToList();

        if (msCookies.Count == 0)
            return null;

        try
        {
            var container = new CookieContainer();
            foreach (var c in msCookies)
            {
                if (c.ExpiresAt.HasValue && c.ExpiresAt.Value <= DateTimeOffset.UtcNow)
                    continue;

                var cleanDomain = c.Domain.TrimStart('.');
                if (string.IsNullOrWhiteSpace(cleanDomain))
                    continue;

                try
                {
                    var cookie = new System.Net.Cookie(
                        c.Name,
                        c.Value,
                        string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
                        c.Domain
                    )
                    {
                        Secure = c.Secure,
                        HttpOnly = c.HttpOnly,
                    };
                    container.Add(new Uri($"https://{cleanDomain}"), cookie);
                }
                catch
                {
                    // Ignore malformed third-party cookies
                }
            }

            using var handler = new HttpClientHandler
            {
                CookieContainer = container,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All,
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(cachedSession.UserAgent);

            var ssoUrl =
                $"{options.BaseUrl.TrimEnd('/')}/auth/microsoft_office365?state=%7B%22visitor_portal_id%22%3A1200%7D";
            using var response = await client.GetAsync(ssoUrl, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var finalUri = response.RequestMessage?.RequestUri;
            var html = await response.Content.ReadAsStringAsync(ct);

            if (
                finalUri is null
                || !IsOnDashboard(finalUri.ToString())
                || HtmlParsingHelpers.IsLoginRedirectHtml(html, finalUri)
            )
            {
                return null;
            }

            return MergeContainerCookies(cachedSession, container, replaceStiCookies: true);
        }
        catch (Exception ex)
        {
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                _logger.LogDebug(
                    "Silent HTTP Microsoft SSO refresh did not succeed: {Message}",
                    ex.Message
                );
            }
            return null;
        }
    }

    public Task<ElmsSession> RefreshSessionAsync(
        AuthOptions options,
        CancellationToken ct = default
    ) => EnsureAuthenticatedAsync(options, ct);

    private async Task<ElmsSession?> TryCaptureSessionAsync(
        AuthOptions options,
        bool headless,
        ElmsCredentials? credentials,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();

        using var playwright = await Playwright.CreateAsync();
        await using var context = await LaunchPersistentContextWithFallbackAsync(
            playwright,
            options.BrowserProfileDirectory,
            headless
        );

        var page = context.Pages.Count > 0 ? context.Pages[0] : await context.NewPageAsync();
        var dashboardUrl = $"{options.BaseUrl.TrimEnd('/')}/user_dashboard";

        await page.GotoAsync(
            dashboardUrl,
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }
        );

        if (IsOnDashboard(page.Url))
            return await ExtractSessionAsync(context, page, options.BaseUrl);

        // Try direct Microsoft Office 365 SSO redirect using persistent Chromium cookies
        try
        {
            var ssoUrl =
                $"{options.BaseUrl.TrimEnd('/')}/auth/microsoft_office365?state=%7B%22visitor_portal_id%22%3A1200%7D";
            await page.GotoAsync(
                ssoUrl,
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 }
            );
            if (IsOnDashboard(page.Url))
                return await ExtractSessionAsync(context, page, options.BaseUrl);
        }
        catch (Exception ex)
        {
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                _logger.LogDebug(
                    "Playwright SSO redirect probe did not land on dashboard: {Message}",
                    ex.Message
                );
            }
        }

        if (credentials is not null)
        {
            var loggedIn = await TryAutomatedLoginAsync(page, credentials);
            if (loggedIn && IsOnDashboard(page.Url))
                return await ExtractSessionAsync(context, page, options.BaseUrl);
        }

        if (headless)
            return null;

        if (_logger?.IsEnabled(LogLevel.Information) == true)
        {
            _logger.LogInformation(
                "Waiting for manual login on {BaseUrl} (timeout: {Timeout})...",
                options.BaseUrl,
                options.LoginTimeout
            );
        }

        await page.WaitForURLAsync(
            IsOnDashboard,
            new PageWaitForURLOptions { Timeout = (float)options.LoginTimeout.TotalMilliseconds }
        );

        return await ExtractSessionAsync(context, page, options.BaseUrl);
    }

    private async Task<bool> TryAutomatedLoginAsync(IPage page, ElmsCredentials credentials)
    {
        try
        {
            // Case A: Microsoft / Office 365 SSO button on NEO LMS login page
            var o365Button = await page.QuerySelectorAsync(
                "a[href*=\"office365\"], a[href*=\"microsoft\"], a:has-text(\"Office 365\"), button:has-text(\"Microsoft\")"
            );
            if (o365Button is not null)
            {
                await o365Button.ClickAsync();
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            }

            // Case B: Microsoft Entra / O365 login flow
            if (page.Url.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
            {
                var emailInput = await page.WaitForSelectorAsync(
                    "input[name=\"loginfmt\"], #i0116",
                    new PageWaitForSelectorOptions { Timeout = 8000 }
                );
                if (emailInput is not null)
                {
                    await emailInput.FillAsync(credentials.Username);
                    await page.ClickAsync("#idSIButton9, input[type=\"submit\"]");
                }

                var passInput = await page.WaitForSelectorAsync(
                    "input[name=\"passwd\"], #i0118",
                    new PageWaitForSelectorOptions { Timeout = 8000 }
                );
                if (passInput is not null)
                {
                    await passInput.FillAsync(credentials.Password);
                    await page.ClickAsync("#idSIButton9, input[type=\"submit\"]");
                }

                // Handle "Stay signed in?" prompt if it appears (short timeout; MFA will time out cleanly)
                try
                {
                    var staySignedIn = await page.WaitForSelectorAsync(
                        "#idSIButton9",
                        new PageWaitForSelectorOptions { Timeout = 5000 }
                    );
                    if (
                        staySignedIn is not null
                        && page.Url.Contains(
                            "login.microsoftonline.com",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        await staySignedIn.ClickAsync();
                }
                catch
                {
                    // Ignore if redirected immediately
                }

                await page.WaitForURLAsync(
                    IsOnDashboard,
                    new PageWaitForURLOptions { Timeout = 15000 }
                );
                return true;
            }

            // Case C: Standard NEO LMS username/password form
            var userField = await page.QuerySelectorAsync(
                "input[name=\"userid\"], input[name=\"username\"], input[type=\"email\"]"
            );
            var passwordField = await page.QuerySelectorAsync("input[name=\"password\"]");
            if (userField is not null && passwordField is not null)
            {
                await userField.FillAsync(credentials.Username);
                await passwordField.FillAsync(credentials.Password);
                var submit = await page.QuerySelectorAsync(
                    "button[type=\"submit\"], input[type=\"submit\"]"
                );
                if (submit is not null)
                    await submit.ClickAsync();
                await page.WaitForURLAsync(
                    IsOnDashboard,
                    new PageWaitForURLOptions { Timeout = 15000 }
                );
                return true;
            }
        }
        catch (Exception ex)
        {
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                _logger.LogDebug("Automated login did not complete: {Message}", ex.Message);
        }

        return false;
    }

    private static ElmsCredentials? ResolveCredentials(AuthOptions options)
    {
        if (options.Credentials is not null)
            return options.Credentials;

        var user = Environment.GetEnvironmentVariable("ELMS_USERNAME");
        var pass = Environment.GetEnvironmentVariable("ELMS_PASSWORD");
        if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pass))
            return new ElmsCredentials(user, pass);

        return null;
    }

    private static bool IsOnDashboard(string url) =>
        url.Contains("/user_dashboard", StringComparison.OrdinalIgnoreCase)
        && !url.Contains("login", StringComparison.OrdinalIgnoreCase);

    private static async Task<ElmsSession> ExtractSessionAsync(
        IBrowserContext context,
        IPage page,
        string baseUrl
    )
    {
        var pwCookies = await context.CookiesAsync();
        var ua = await page.EvaluateAsync<string>("() => navigator.userAgent");

        var cookies = pwCookies
            .Select(c => new ElmsCookie(
                Name: c.Name,
                Value: c.Value,
                Domain: c.Domain,
                Path: c.Path,
                Secure: c.Secure,
                HttpOnly: c.HttpOnly,
                ExpiresAt: c.Expires > 0
                    ? DateTimeOffset.FromUnixTimeSeconds((long)c.Expires)
                    : null
            ))
            .ToList();

        return new ElmsSession(baseUrl, ua, cookies, DateTimeOffset.UtcNow);
    }

    internal static HttpClientHandler CreateCookieHandler(ElmsSession session)
    {
        var container = new CookieContainer();
        var baseUri = new Uri(session.BaseUrl);

        foreach (var c in session.Cookies)
        {
            if (!ElmsUrlCanonicalizer.IsSameOrSubdomain(c.Domain, baseUri.Host))
                continue;

            try
            {
                var cookie = new System.Net.Cookie(
                    c.Name,
                    c.Value,
                    string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
                    c.Domain.TrimStart('.')
                )
                {
                    Secure = c.Secure,
                    HttpOnly = c.HttpOnly,
                };
                container.Add(baseUri, cookie);
            }
            catch (CookieException)
            {
                // Ignore third-party cookies incompatible with baseUri
            }
        }

        return new HttpClientHandler
        {
            CookieContainer = container,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
    }

    internal static void PopulateCookieContainer(CookieContainer container, ElmsSession session)
    {
        var baseUri = new Uri(session.BaseUrl);

        foreach (var c in session.Cookies)
        {
            if (!ElmsUrlCanonicalizer.IsSameOrSubdomain(c.Domain, baseUri.Host))
                continue;

            try
            {
                var cookie = new System.Net.Cookie(
                    c.Name,
                    c.Value,
                    string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
                    c.Domain.TrimStart('.')
                )
                {
                    Secure = c.Secure,
                    HttpOnly = c.HttpOnly,
                };
                container.Add(baseUri, cookie);
            }
            catch (CookieException)
            {
                // Ignore third-party cookies incompatible with baseUri
            }
        }
    }

    internal static ElmsSession MergeContainerCookies(
        ElmsSession baseSession,
        CookieContainer container,
        bool replaceStiCookies
    )
    {
        var baseHost = new Uri(baseSession.BaseUrl).Host;
        var merged = new Dictionary<string, ElmsCookie>(StringComparer.OrdinalIgnoreCase);

        foreach (var existing in baseSession.Cookies)
        {
            if (
                replaceStiCookies
                && ElmsUrlCanonicalizer.IsSameOrSubdomain(existing.Domain, baseHost)
            )
                continue;

            var key = $"{existing.Domain.TrimStart('.')}|{existing.Path}|{existing.Name}";
            merged[key] = existing;
        }

        foreach (System.Net.Cookie c in container.GetAllCookies())
        {
            if (c.Expired)
                continue;

            var domain = string.IsNullOrWhiteSpace(c.Domain) ? baseHost : c.Domain;
            var path = string.IsNullOrWhiteSpace(c.Path) ? "/" : c.Path;
            var key = $"{domain.TrimStart('.')}|{path}|{c.Name}";

            merged[key] = new ElmsCookie(
                Name: c.Name,
                Value: c.Value,
                Domain: domain,
                Path: path,
                Secure: c.Secure,
                HttpOnly: c.HttpOnly,
                ExpiresAt: c.Expires == DateTime.MinValue
                    ? null
                    : new DateTimeOffset(c.Expires.ToUniversalTime())
            );
        }

        return baseSession with
        {
            Cookies = [.. merged.Values],
            AuthenticatedAt = DateTimeOffset.UtcNow,
        };
    }

    private async Task<IBrowserContext> LaunchPersistentContextWithFallbackAsync(
        IPlaywright playwright,
        string profileDir,
        bool headless
    )
    {
        var launchOpts = new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = headless,
            Args = ["--no-sandbox", "--disable-setuid-sandbox"],
        };

        var discoveredExe = ElmsBrowserLauncher.FindChromiumExecutable();
        if (!string.IsNullOrWhiteSpace(discoveredExe))
        {
            launchOpts.ExecutablePath = discoveredExe;
            return await playwright.Chromium.LaunchPersistentContextAsync(profileDir, launchOpts);
        }

        try
        {
            return await playwright.Chromium.LaunchPersistentContextAsync(profileDir, launchOpts);
        }
        catch (PlaywrightException ex)
            when (ex.Message.Contains(
                    "Executable doesn't exist",
                    StringComparison.OrdinalIgnoreCase
                )
            )
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (var channel in new[] { "msedge", "chrome" })
                {
                    try
                    {
                        var chOpts = new BrowserTypeLaunchPersistentContextOptions
                        {
                            Headless = headless,
                            Channel = channel,
                            Args = ["--no-sandbox", "--disable-setuid-sandbox"],
                        };
                        return await playwright.Chromium.LaunchPersistentContextAsync(
                            profileDir,
                            chOpts
                        );
                    }
                    catch
                    {
                        // Try next channel
                    }
                }
            }

            _logger?.LogInformation("Installing Playwright Chromium browser on first run...");
            Program.Main(["install", "chromium"]);
            return await playwright.Chromium.LaunchPersistentContextAsync(profileDir, launchOpts);
        }
    }
}
