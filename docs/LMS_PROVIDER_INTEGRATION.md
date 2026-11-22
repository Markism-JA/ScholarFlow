# LMS & eLMS Provider Integration Architecture

ScholarFlow separates academic workspace management from Learning Management System (LMS) transport, authentication, and scraping through a three-layer pluggable architecture. New LMS integrations—such as Canvas, Moodle, Google Classroom, Blackboard, or custom institutional portals—can be implemented as standalone class libraries without modifying `ScholarFlow.Core` or the Avalonia desktop UI.

---

## 1. Architectural Layers & Dependency Graph

```text
                        ┌──────────────────────────┐
                        │    ScholarFlow.Core      │
                        │  Domain Models, SQLite,  │
                        │  PDF/Quiz Studio, Sync   │
                        │  Contracts & Coordinator │
                        └────────────▲─────────────┘
                                     │
                        ┌────────────┴─────────────┐
                        │    ScholarFlow.Elms      │
                        │  Reusable LMS Sync SDK:  │
                        │  IElmsClient, Resolver,  │
                        │  Executor, Artifact &    │
                        │  Session Utilities       │
                        └────────────▲─────────────┘
                                     │
          ┌──────────────────────────┼──────────────────────────┐
          │                          │                          │
┌─────────┴──────────────┐ ┌─────────┴──────────────┐ ┌─────────┴──────────────┐
│  ScholarFlow.Elms.Sti  │ │ ScholarFlow.Elms.Canvas│ │ ScholarFlow.Elms.<...> │
│  STI eLMS (CYPHER)     │ │ (Example REST API      │ │ (Future LMS Providers) │
│  Playwright + Parsers  │ │  Provider — no browser)│ │                        │
└─────────▲──────────────┘ └────────────────────────┘ └────────────────────────┘
          │
┌─────────┴──────────────┐
│  ScholarFlow.Desktop   │  Composition root registers concrete providers in App.cs
└────────────────────────┘
```

### Project Boundaries

| Project | Role | Allowed Dependencies |
| --- | --- | --- |
| `src/ScholarFlow.Core` | Canonical domain models (`BasinConfig`, `TaskItem`, `BasinCourseEntry`), SQLite persistence, Master PDF compiler, Quiz Studio, `IBasinSyncProvider`, and `MultiBasinSyncCoordinator`. | No LMS-specific packages or provider references. |
| `src/ScholarFlow.Elms` | LMS-agnostic synchronization SDK: client/auth interfaces, snapshot diffing (`SyncResolver`), concurrent file downloader (`SyncExecutor`), Markdown artifact writer, session persistence, and `ElmsBasinSyncService`. | `ScholarFlow.Core`, `AngleSharp` (for shared HTML-to-Markdown and DOM helpers), `Microsoft.Extensions.Logging.Abstractions`. No Playwright or browser automation. |
| `src/ScholarFlow.Elms.<Provider>` | Concrete LMS provider (e.g., `ScholarFlow.Elms.Sti`). Implements authentication, HTTP/API or HTML scraping, and `IBasinSyncProvider`. | `ScholarFlow.Core`, `ScholarFlow.Elms`, plus provider-specific packages (e.g., `Microsoft.Playwright` for browser-based SSO, or REST SDKs). |
| `tests/ScholarFlow.Elms.Tests` | Unit tests for the pure LMS SDK (`SyncResolver`, `SyncExecutor`, `CourseFilter`, `ElmsSessionStore`, `ElmsBasinSyncService`, `ElmsUrlCanonicalizer`). | `ScholarFlow.Elms` only. |
| `tests/ScholarFlow.Elms.<Provider>.Tests` | Provider-specific unit tests (e.g., `tests/ScholarFlow.Elms.Sti.Tests`) covering HTML/JSON parsers, client mapping, and provider capabilities using deterministic fixtures. | `ScholarFlow.Elms`, `ScholarFlow.Elms.<Provider>`. |

---

## 2. Core Sync Contracts (`ScholarFlow.Core.Sync`)

All desktop views, view models, and background schedulers interact exclusively with `IBasinSyncProvider` and `MultiBasinSyncCoordinator`.

### 2.1 `IBasinSyncProvider` Lifecycle

Defined in `src/ScholarFlow.Core/Sync/IBasinSyncProvider.cs`, `IBasinSyncProvider` exposes the complete contract between ScholarFlow and an external LMS:

| Member | Purpose |
| --- | --- |
| `ProviderId` | Stable, lowercase identifier persisted in `BasinConfig.ProviderId` (e.g., `"sti-elms"`, `"canvas"`, `"moodle"`). Reserved value `"none"` represents a Local Knowledge Hub basin. |
| `DisplayName` | Human-readable label shown in the Onboarding Wizard, Settings Modal, Basin Switcher, and Sidebar Sync menu. |
| `Capabilities` | Declarative `ProviderCapabilities` record governing UI affordances and scheduler behavior. |
| `GetConcurrencyKey(BasinConfig)` | Returns a normalized lock key (typically `"{providerId}:{host}:{accountKey}"`) used by `MultiBasinSyncCoordinator` to serialize operations when `EnforcesSingleConcurrentSession` is enabled. |
| `AuthenticateAsync(...)` | Establishes or verifies credentials/sessions. Accepts `allowInteractive` so background polling never pops up unexpected login dialogs. |
| `DisconnectAsync(...)` | Tears down cached HTTP clients and deletes persisted session tokens/cookies for the basin's account key. |
| `DiscoverCoursesAsync(...)` | Returns enrolled remote courses (`DiscoveredCourse`) for initial basin setup or course picker views. |
| `SyncBasinAsync(...)` | Runs a full synchronization across all non-excluded courses, downloading handouts, updating tasks, and compiling Master PDFs. |
| `SyncSingleCourseAsync(...)` | Runs a targeted synchronization for a single `BasinCourseEntry`. |
| `PollAssignmentsAsync(...)` | Lightweight fast-poll path that checks deadlines, scores, and newly posted assignments without re-downloading every course handout. |
| `RefreshSingleTaskAsync(...)` | Fetches and updates a single `TaskItem` from its remote `LmsUrl`. |
| `OpenInSessionBrowser(...)` | Optional hook to launch a remote URL inside an authenticated standalone browser window when `SupportsInteractiveSessionBrowser` is `true`. |

### 2.2 Capability-Driven UI Adaptation (`ProviderCapabilities`)

ScholarFlow Desktop inspects `IBasinSyncProvider.Capabilities` dynamically. Adding a provider requires zero UI changes across the Sidebar, Onboarding Wizard, Settings Modal, or Task Inspector:

- **`SupportsSync`**: Controls whether the basin exposes sync triggers, background polling toggles, and provider configuration fields. Local-only basins set this to `false`.
- **`SupportsFastPoll`**: Enables the fast-poll (`R`) action in the Sidebar utility strip and Sync flyout.
- **`SupportsPerCourseSync`**: Adds per-course sync items to the Sidebar Sync menu and right-click course context menus.
- **`SupportsSingleItemRefresh`**: Displays the per-task refresh button in the right-hand Task Inspector.
- **`EnforcesSingleConcurrentSession`**: Signals that the remote LMS invalidates earlier sessions on concurrent logins. When `true`, `MultiBasinSyncCoordinator` enforces mutual exclusion per `GetConcurrencyKey`, and the Sidebar surfaces a Pause/Resume Polling (`Shift+P`) control.
- **`DefaultTerms`**: Seeds default academic terms for new basins (e.g., `["Prelims", "Midterms", "Prefinals", "Finals"]` for STI eLMS, or `["Semester 1", "Semester 2"]` for semester-based schools).
- **`SupportsInteractiveSessionBrowser`**: Enables "Open in LMS" actions that reuse the provider's authenticated browser profile.
- **`ConfigFields` (`IReadOnlyList<ProviderConfigField>`)**: Declaratively defines provider settings (`Key`, `Label`, `DefaultValue`, `Placeholder`, `IsSecret`). The Onboarding Wizard and Settings Modal automatically render labeled inputs—masking values with `PasswordChar = '•'` when `IsSecret` is `true`—and persist them into `BasinConfig.ProviderSettings`.

### 2.3 Background Coordination & Deferred Auth

`MultiBasinSyncCoordinator` manages multiple registered Study Basins simultaneously:

- Background timers invoke `PollAssignmentsAsync` / `SyncBasinAsync` with `allowInteractive: false`.
- If a provider's session or API token is expired during a background run, the provider must throw `ProviderAuthenticationRequiredException`. The coordinator catches this exception, emits a non-intrusive notification, and defers sync until the user re-authenticates interactively.

---

## 3. Shared LMS Sync SDK (`ScholarFlow.Elms`)

Most LMS integrations share the same local synchronization problem: diffing remote course materials against local folders, preserving user-reorganized files, avoiding redundant downloads, generating Markdown notes, and upserting tasks into SQLite. `ScholarFlow.Elms` provides this entire pipeline as reusable components.

### 3.1 Core SDK Abstractions

- **`IElmsClient`**: High-level remote LMS contract (`EnsureConnectedAsync`, `GetEnrolledCoursesAsync`, `GetCourseSnapshotAsync`, `GetLessonAsync`, `GetAssignmentsAsync`, `GetAssignmentAsync`, `FetchAuthenticatedHtmlAsync`, `CreateSyncExecutor`).
- **`IElmsAuthProvider`**: Optional authentication abstraction for providers that separate session acquisition (OAuth2, cookies, Playwright SSO) from HTTP transport.
- **`ISyncResolver` & `ISyncExecutor`**: Deterministic diff planner and bounded-concurrency download executor.

### 3.2 Data Transfer Models (`ScholarFlow.Elms.Models`)

Providers map remote API payloads or scraped HTML into immutable SDK records:

- `ElmsCourse`: Course ID, display title, course/section codes, URL, sanitized folder name, and administrative/completed flags.
- `ElmsCourseSnapshot`: Complete point-in-time view of a course (general files, modules, lessons, assignments, and downloadable `ElmsResource` items).
- `ElmsLesson`: Inline lesson/handout content (Markdown) plus attached resources and term metadata.
- `ElmsAssignment`: Assignment ID, kind (`Freeform`, `Quiz`, etc.), title, URL, due/start dates, weight, score, submission status, instructions Markdown, and submitted artifacts.
- `ElmsResource`: Canonical URL, target relative path, media type, and optional size/ETag metadata.

### 3.3 Built-In Sync Pipeline

When a concrete provider delegates to `ElmsBasinSyncService` (passing a `Func<BasinConfig, IElmsClient>` resolver), it automatically inherits:

1. **Snapshot Diffing (`SyncResolver`)**: Compares each remote `ElmsResource` against `LocalSyncState` and the filesystem, classifying every item as `New`, `Updated`, `MovedLocally` (preserving user-moved files via SHA-256 hash matching), `Unchanged`, or `SkippedExternal`.
2. **Bounded Concurrent Downloads (`SyncExecutor`)**: Executes the `SyncPlan` with controlled parallelism, content hashing, retry resilience, dry-run support, and `IProgress<SyncProgress>` reporting.
3. **Markdown Artifact Generation (`CourseArtifactWriter` & `HtmlToMarkdownConverter`)**: Writes structured Markdown notes for lessons and assignments (including YAML frontmatter, timers, submission attempts, and links) into `ongoing/` and `completed/` directories.
4. **Basin & Task Synchronization (`ElmsBasinSyncService`)**: Registers discovered courses in `BasinConfig`, upserts tasks and due dates via `BasinTaskSynchronizer`, generates `AppNotification` alerts for new/updated assignments, and triggers `MasterPdfService` compilation for updated handouts.
5. **Reusable Helpers**:
   - `ElmsUrlCanonicalizer`: Strips ephemeral auth query parameters (e.g., `lmsauth`) so resource identity stays stable across sessions.
   - `ElmsSessionStore`: Persists account-scoped cookie/token JSON files cleanly separated by `AccountKey`.
   - `HtmlParsingHelpers`: Public AngleSharp DOM parsing, URL resolution, and date/term normalization helpers for HTML-based providers.
   - `CourseFilterStore`: Persists per-basin course inclusion/exclusion rules.

---

## 4. Reference Implementation: `ScholarFlow.Elms.Sti`

`src/ScholarFlow.Elms.Sti/` demonstrates a complete HTML-scraping + browser-SSO provider built on top of `ScholarFlow.Elms`:

| File | Responsibility |
| --- | --- |
| `Sync/StiElmsBasinSyncProvider.cs` | Implements `IBasinSyncProvider`, declares STI capabilities and `baseUrl` config field, manages account-scoped `StiElmsClient` instances in a `ConcurrentDictionary`, and delegates sync operations to `ElmsBasinSyncService`. |
| `Client/StiElmsClient.cs` | Implements `IElmsClient` using `HttpClient` + `CookieContainer`, throttled parallel fetches, automatic cookie refresh persistence, and CYPHER endpoint routing. |
| `Auth/PlaywrightAuthProvider.cs` | Implements `IElmsAuthProvider` using Playwright Chromium for Microsoft SSO / CYPHER login, exporting authenticated cookies into `ElmsSession`. |
| `Parsing/DashboardParser.cs` | Parses enrolled/completed courses and filters administrative shells. |
| `Parsing/CoursePageParser.cs` | Parses CYPHER module rows, section kinds, and term prefixes. |
| `Parsing/LessonParser.cs` | Extracts inline lesson HTML and downloadable handout attachments. |
| `Parsing/AssignmentsListParser.cs` | Parses course assignment tables, due dates, weights, and quiz links. |
| `Parsing/AssignmentParser.cs` | Parses individual assignment detail pages, submission metadata, and uploaded submission files. |

### REST API vs. HTML Scraping Providers

Providers targeting modern REST or GraphQL LMS platforms (e.g., **Canvas REST API**, **Moodle Web Services**, **Google Classroom API**) are significantly simpler than `ScholarFlow.Elms.Sti`:

- **No Playwright or AngleSharp dependency** is required in the provider `.csproj` unless HTML sanitization is needed.
- `ConfigFields` can declare an `"apiToken"` field with `IsSecret: true` alongside `"baseUrl"`.
- `IElmsClient` calls JSON endpoints via `HttpClient` + `System.Text.Json`, maps responses to `ElmsCourse` / `ElmsCourseSnapshot` / `ElmsAssignment`, and still reuses `ElmsBasinSyncService`, `SyncResolver`, `SyncExecutor`, and `CourseArtifactWriter` unchanged.

---

## 5. Step-by-Step Guide: Adding a New LMS Provider

### Step 1 — Scaffold the Provider Class Library

Create `src/ScholarFlow.Elms.<Provider>/ScholarFlow.Elms.<Provider>.csproj` targeting `net10.0`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\ScholarFlow.Core\ScholarFlow.Core.csproj" />
    <ProjectReference Include="..\ScholarFlow.Elms\ScholarFlow.Elms.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="11.0.0" />
  </ItemGroup>
</Project>
```

Add the project under `<Folder Name="/src/">` in `ScholarFlow.slnx`.

### Step 2 — Implement `IElmsClient` (or Direct `IBasinSyncProvider`)

- **Recommended path (standard LMS):** Implement `ScholarFlow.Elms.Abstractions.IElmsClient` so the provider reuses `ElmsBasinSyncService`, `SyncResolver`, `SyncExecutor`, and `CourseArtifactWriter`.
  - Populate stable canonical URLs (strip transient tokens) so `SyncResolver` avoids redundant re-downloads.
  - Implement `GetAssignmentAsync(...)` to support `RefreshSingleTaskAsync` in the Task Inspector.
  - Return a `SyncExecutor` bound to the provider's authenticated `HttpClient` from `CreateSyncExecutor()`.
- **Direct path (non-standard data source):** For sources that do not fit the course/lesson/assignment model, implement `IBasinSyncProvider` directly against `ScholarFlow.Core`.

### Step 3 — Implement `IBasinSyncProvider`

Create `<Provider>BasinSyncProvider : IBasinSyncProvider, IAsyncDisposable`:

- Assign a unique `ProviderId` (e.g., `"canvas"`), `DisplayName`, and accurate `ProviderCapabilities`.
- Scope client instances by `GetConcurrencyKey(BasinConfig)` so multiple basins using different accounts or portals remain completely isolated.
- Read configuration values via `config.GetProviderSetting("key", defaultValue)`.
- Respect `allowInteractive` in `AuthenticateAsync(...)`; throw `ProviderAuthenticationRequiredException` when a non-interactive background call lacks valid credentials.
- Dispose owned `HttpClient` / transport resources in `DisposeAsync()`.

### Step 4 — Register in the Desktop Composition Root

1. Add a `<ProjectReference>` to `src/ScholarFlow.Elms.<Provider>/ScholarFlow.Elms.<Provider>.csproj` in `src/ScholarFlow.Desktop/ScholarFlow.Desktop.csproj`.
2. Instantiate and return the provider in `src/ScholarFlow.Desktop/App.cs`:

```csharp
_mainVm = new MainWindowViewModel(
    (basin, pdf) =>
    [
        new StiElmsBasinSyncProvider(basin, pdf),
        new CanvasBasinSyncProvider(basin, pdf),
    ]
);
```

Once registered, the provider automatically appears in the **New Basin Setup Wizard**, **Basin Switcher**, and **Desktop Settings Modal**.

### Step 5 — Create a Decoupled Test Project

Create `tests/ScholarFlow.Elms.<Provider>.Tests/ScholarFlow.Elms.<Provider>.Tests.csproj` referencing `ScholarFlow.Elms` and `ScholarFlow.Elms.<Provider>`, and register it under `<Folder Name="/tests/">` in `ScholarFlow.slnx`.

Keep `tests/ScholarFlow.Elms.Tests` strictly free of concrete provider references so the shared SDK contract remains independently verifiable.

---

## 6. Operational Guarantees & Verification Checklist

Every LMS provider should verify the following invariants before merging:

1. **Idempotent Sync**: Re-running `SyncBasinAsync` with unchanged remote content produces zero re-downloads and zero duplicate tasks in SQLite.
2. **Local Move Preservation**: Moving a downloaded handout to another subfolder inside the course directory is detected via `SyncResolver` hash matching rather than overwritten.
3. **Background Safety**: Calling `PollAssignmentsAsync` or `SyncBasinAsync` with `allowInteractive: false` never blocks on UI prompts or launches a browser; expired sessions throw `ProviderAuthenticationRequiredException`.
4. **Cancellation Propagation**: All network and disk I/O methods honor `CancellationToken`.
5. **Deterministic Unit Tests**: Parsers and client mappings are verified against saved HTML/JSON fixtures or mocked `HttpMessageHandler` instances—requiring no live network credentials or browser binaries in CI.
6. **Clean Build & Test Suite**:

   ```bash
   dotnet build ScholarFlow.slnx --disable-build-servers -m:1
   dotnet test ScholarFlow.slnx --no-build --disable-build-servers -m:1
   ```
