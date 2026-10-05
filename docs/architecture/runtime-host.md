# Runtime and host architecture

## When to read

Read this chapter completely when a task changes application startup, dependency
registration, middleware or endpoints, hosting configuration, Electron behavior,
desktop updates, shared application chrome, dialogs, themes, launch profiles, or
runtime packaging. Also read it when another chapter routes here because a feature
adds a hosted worker, application-wide singleton, media endpoint, or service that
must participate in the startup readiness gate.

For provider clients and app-process queues, continue with
[Providers and background work](providers-background.md). For database bootstrap,
migration safety, and local-data placement, continue with
[Persistence, migrations, and import](persistence-migrations-import.md). For build,
startup, and packaging checks, continue with
[Validation and documentation](validation-documentation.md).

## Scope and ownership

Lorekeeper is a desktop-first, local AI-assisted bookmaking workbench. Normal user
operation is an Electron.NET shell backed by a local ASP.NET Core process. The same
Blazor application can run directly in a browser for development and validation.
The declared runtime identifiers are Windows x64, Linux x64, and macOS arm64; a
declared target is not evidence that packaging, updates, or platform-specific
behavior has been exercised there.

This chapter owns the server and desktop process topology, host initialization,
dependency-injection composition, startup gating, application shell, shared dialog
policy, theming, local host binding, and desktop update boundary. It does not own
feature-domain rules simply because their services are registered in the host.
Those contracts remain in their routed chapters.

The primary stack is .NET 10 ASP.NET Core with Blazor Interactive Server,
Electron.NET, EF Core with SQLite, sqlite-vec and SQLite FTS5,
`Microsoft.Extensions.AI` and the OpenAI SDK, and SkiaSharp. The owned Rust
`Lorekeeper.Press` executable is packaged into the application runtime but its
protocol and publishing behavior belong to
[Press production](press-production.md).
The packaged descriptor and every managed invocation require Press protocol
v15. Durable render workers use its scoped production requests. Cover editor
previews stay inside the Blazor request scope and use the shared in-process
composition rasterizer, creating no child process, temporary Press job directory,
PDF bytes, endpoint cache, or database record.

## Current architecture and invariants

### Host topology and dependency ownership

[`Program.cs`](../../Lorekeeper/Program.cs) is the composition root. It determines
Electron mode, resolves development versus packaged data placement, configures
ASP.NET Core and Blazor, registers application services and hosted workers, maps
OAuth and media endpoints, installs the startup gate, and creates the desktop
window. Keep provider-specific transport behavior behind provider-neutral
contracts and keep feature behavior in injected services; the composition root
should wire those owners rather than reproduce their logic.
The scoped OpenAI account-catalog, authorization, and token services are such
owners. Process-local authorization state, per-account operation coordination,
callback-origin validation, and the platform-selected external launcher are
registered at the host boundary; Settings and the callback invoke their
contracts while the composition root contains no OAuth behavior.
The catalog service only materializes the bundled versioned manifest into local
rows; it does not perform account-backed model-list HTTP requests during startup,
Settings reload, or authorization completion.

Razor pages and components own interaction and presentation state. Domain services
own validation, persistence, graph/index maintenance, provider resolution, image
workflows, publishing, and durable background behavior. Manual UI actions,
assistant tools, endpoints, imports, and workers must enter through the same owning
services so they cannot create parallel validation or mutation paths.

Blazor interactivity is opted into per page or component with Interactive Server
render mode. There is no WebAssembly client application. Provider calls,
persistence, indexing, rendering coordination, and background work run in the
server process and are consumed through dependency injection. App-process
runtimes and notifiers may keep work alive across component disposal, but a Razor
component or Blazor circuit must never own a durable operation's lifetime.

Service lifetimes must match that topology. Stateless application operations and
repository-backed feature facades are normally scoped. Process-wide coordination,
queues, notifiers, startup state, update state, and shared turn runtimes are
singletons. Hosted services create fresh scopes for database-backed work. A
singleton must not capture a scoped service or EF context, and a circuit must not
retain a database context across user think time, streaming, network waits, or
background polling. The database-operation lifetime and lock order are defined in
the persistence chapter.

### HTTP pipeline and endpoint boundary

The server uses the normal ASP.NET Core error/status pipeline, antiforgery, static
assets, Razor components, and Interactive Server endpoints. Non-Electron
production hosting enables HSTS and HTTPS redirection; the local Electron path is
explicitly HTTP on its hardened loopback binding. Status-code handling re-executes
the application-owned not-found route rather than exposing host chrome.

Minimal API endpoints exist only where an HTTP boundary is necessary: the OpenAI
OAuth callback and scoped binary/media delivery are representative examples. The
callback returns a standalone completion page and never redirects into a Blazor
circuit; authorization starts from the still-mounted Settings component. Endpoint
handlers validate identifiers and delegate to owning services; they do not become
an alternate domain layer. Local image, mask, chat-visual, artifact, or other media
routes must enforce the same project/ownership rules as the Razor surface and must
not expose filesystem paths or credentials. A new endpoint is therefore both a
host-routing change and a feature-owner change, and should route to both chapters.

The configured Blazor receive limit accommodates current interactive image and
editor payloads. Raising it affects server resource exposure and must be justified
against the owning upload/provider byte boundaries. Static assets under
`wwwroot` are shipped application resources; generated project media and local
database content do not belong there.

### Startup readiness and recovery

The HTTP host and Electron bridge intentionally become available before database
bootstrap. [`App.razor`](../../Lorekeeper/Components/App.razor) gates normal routes
and renders the application-owned startup surface instead of leaving a blank
window. `ApplicationStartupState` publishes immutable snapshots with named,
non-sensitive migration stages, progress, readiness, recovery, and terminal
failure state. Every database-backed hosted worker waits on the same readiness
gate before reconciliation or queue processing.

`ApplicationStartupWorker` yields before opening SQLite so the host surface can
render even when early calls complete synchronously. It then owns ordered database
migration, interrupted-work reconciliation, vector initialization, and graph
repair. On success the app reloads the originally requested route. A recovery
state opens the data-recovery surface. An unexpected exception remains visible on
the startup screen with a safe close action and does not release database workers.
A configured minimum display interval keeps the branded splash legible even when
the database is already current.

Do not bypass this gate for a new database-backed worker. A worker that starts
early can query obsolete or partially migrated schema and defeats the fail-closed
recovery contract. Detailed migration order, backup handling, and recovery-shell
semantics are owned by
[Persistence, migrations, and import](persistence-migrations-import.md).

### Browser and Electron boundaries

Electron is the primary debug and product target. The desktop host binds to the
configured local host and port, while the explicit `http` launch profile supports
browser development and validation. Both development profiles use
`localhost:1455`; OpenAI OAuth depends on
`http://localhost:1455/auth/callback`. Connect remains disabled unless that exact
configured loopback origin is present in the active server addresses. A port
change must update the host binding and accepted OAuth redirect together; no
second listener is created to compensate. The host remains local-only unless a
deliberate architecture and security change expands its exposure.

Electron-only responsibilities include hardened binding, desktop-window creation,
shutdown, and deliberately narrow external-browser handoffs. The shared
authorization launcher opens only exact allowlisted OpenAI/GitHub HTTPS targets;
browser hosting instead renders explicit user-activated links. The Free update
channel retains its separate validated release-page handoff.
`DistributionChannelPolicy` resolves immutable assembly
metadata at startup; it is not a user setting. Development always disables update
checks. A Release build defaults to `Free`, which registers the constrained public
GitHub release checker only after the user chooses the shared-top-bar **Check for
updates** action. A newer stable release can expose **Download Update**, which
opens the release page in the operating system browser. It never downloads,
installs, or restarts Electron automatically, and it never polls in the
background.

`Store` is an explicit Release build channel. It does not register a GitHub
checker, invoke a browser handoff, or configure an Electron updater; the shared
top bar says that Microsoft Store manages updates. Missing, duplicate, or malformed
Release metadata fails closed with all update behavior disabled. The Free checker
accepts stable SemVer releases only and constrains release and asset URLs to the
configured public repository. Its failures remain non-fatal UI state and must not
prevent the local workspace from opening. Never treat public release metadata as
trusted executable content without the package evidence described in the validation
chapter.

`Dorely/Lorekeeper` is the sole runtime release/update feed. The v1.0.0 bridge
publication may also be mirrored to `Dorely/Lorekeeper-Releases` so installed
0.3.x clients can discover it; later releases publish only to the main
repository. The old feed remains immutable after the separately validated
handoff and is not deleted by preparation. Linux Free builds discover AppImage
and Debian artifacts through the same explicit manual-update boundary.
Stable discovery rejects both GitHub prerelease flags and SemVer prerelease
tags, even if a release is incorrectly marked stable in GitHub metadata.

The current macOS distribution remains the direct-DMG path. No Mac App Store
runtime or sandboxed `mas-dev` flavor exists while M0.6 lacks the owner-supplied
Apple Developer Team ID, MAS development certificate/profile, test App ID,
Apple-silicon test Mac, and signed-host decision. Do not treat the ordinary
macOS Electron runtime, ad-hoc package, or dispatch-only release workflow as MAS
evidence. When those prerequisites exist, the MAS channel must use Electron’s
MAS runtime with separate application/helper entitlements and validate the full
native process/data/file/print boundary on that signed device; the validation
chapter owns the evidence procedure.

Development uses the repository-local database by default. Packaged builds use a
per-user application-data location so installers, portable executables, and
mounted DMGs remain disposable. Database-path and migration safety details belong
to the persistence chapter. Version history follows the same split through
`GitRepositoryStoreOptions`: development repositories live under the ignored
`History/` directory beside the app data base, while packaged repositories live
under `%LocalAppData%/Lorekeeper/History/<repository-id>.git`. Release
orchestration and platform evidence belong to the validation chapter. The
automatic checkpoint-push worker is startup-gated on database readiness and
uses fresh scopes plus durable operation rows; its process-local queue is only a
wake-up signal.

The shared project top bar owns the Checkpoint, Review Edits, and Pending
changes controls. History events refresh these controls across independently
rendered pages and assistant panels. Review Edits is excluded from Git snapshots:
enabled assistant turns mutate live state without checkpointing, while disabled
mutating turns checkpoint the complete live project. An unapproved dirty project
remains local and cannot be pushed or checked out; checkpoint failures stay
visible and reviewable.

An unresolved Contest restores a project-wide Editor lock after application
restart. The startup gate and owning mutation services reject Editor manuscript,
layout, Figure, Designed Page, assistant, revision-worker, and new-contest
mutations while read-only navigation remains available. Contest start and
candidate progress preserve the current Editor mode, chapter, target, pane
visibility, and layout preferences. When the initiating assistant turn ends,
the mounted Editor switches only to read-only Edit if the contest remains
unresolved. Candidate draft edits and resolution use the explicit authorized
path, and the persistent lock notice links to the contested chapter's normal
Review page; the contest is not opened automatically. Non-Editor workspaces and
their background work remain available.

### Application-owned interaction surfaces

Product confirmations, alerts, pickers, and modal workflows are Lorekeeper
Razor/HTML/CSS. Destructive actions use the shared `ConfirmationDialog`; choices,
validation, progress, and failures stay in the owning surface. Runtime code must
not use browser `alert`, `confirm`, or `prompt`, Electron dialog APIs, operating
system message boxes, or another native product dialog.

Browser-mediated file selection is allowed only at explicit upload/import
boundaries where the web security model requires it. Opening an external browser
is allowed only for intentional handoffs such as OAuth, vendor documentation, or
application updates. This keeps product context visible and makes browser and
Electron behavior consistent.

Printing is the other narrow device handoff: the shared `PrintPreviewDialog`
owns source loading, layout choices, page selection, progress, and errors; only
its explicit Print action opens the browser/system printer dialog. `IPrintHost`
separates browser iframe printing from the desktop adapter. Electron uses an
application-owned host hook to wait for the native print callback, because the
pinned Electron.NET bridge returns before that callback. A sandboxed temporary
window loads only the local print-session document and is destroyed after the
operation or cancellation. Printer selection, copies, duplex, and device
settings remain in the final system dialog. No silent printing is used.

`Printing/` owns bounded, transient print sources and prepared sheet sessions.
Project-scoped, unguessable session routes serve immutable sheet PNGs and a
standalone print document with no app chrome. Responses are not cached, and
sessions are released on replacement, close, failure, or expiry. Printing creates
no database rows, assets, publication jobs, or PDFs. The browser and desktop
handoffs consume the same prepared sheets shown in preview.

The `Printing` configuration section bounds source bytes, selected pages, raster
pixels, session count, lifetime, and aggregate encoded session bytes. Its session
byte limit also caps the decoded sheets in a single handoff; oversized selections
must be split into smaller ranges. Preparation is serialized to bound native
raster memory. Image snapshots preserve EXIF orientation. The project build
replaces only Electron.NET's empty host hook with `ElectronHostHook/index.js`
and retains the pinned connector in build and publish output.
The Electron 43.6.0 pin includes the upstream fix for rejected print options
introduced in 43.0/43.1; printer settings must not be dropped to work around that
runtime regression.

Shared layout components own the viewport shell, navigation, page headings,
configuration navigation, circuit-reconnect UI, print-specific overflow, and
desktop-update presentation. Feature pages should reuse those components instead
of creating new application chrome.

### Theme contract

The theme is token-driven and browser-local. The light palette lives on `:root`
and the dark palette on `html[data-lk-theme="dark"]` in
[`app.css`](../../Lorekeeper/wwwroot/app.css), with both Lorekeeper `--lk-*`
tokens and Bootstrap `--bs-*` overrides. Component stylesheets consume these
tokens rather than embedding independent palettes.

`App.razor` runs a synchronous pre-paint script that reads
`Lorekeeper.ui.theme`, falls back to `prefers-color-scheme` only when no choice is
stored, and sets `data-lk-theme`, `data-bs-theme`, and the `theme-color` metadata
before the body renders. `ThemeToggle` applies and persists explicit light/dark
selection. The graph visualization is intentionally always dark, while print and
publication output render on white; do not reinterpret those exceptions as
missing theme support.

### Configuration boundary

Host configuration belongs in
[`appsettings.json`](../../Lorekeeper/appsettings.json), environment-specific
settings, and environment-variable overrides. Options or explicit validation at
the composition root own desktop hosting and updates, startup display timing,
Blazor message limits, provider timeouts, image generation, ingest, embeddings,
agents, and web research. Do not hard-code configuration in components or feature
entities.

`Desktop:BindHost` and `Desktop:HttpPort` define the Electron-backed local URL;
`Desktop:UsePerUserDataDirectory` controls packaged data placement; and
`Desktop:ReleaseApiUrl` defines the Free-channel, user-initiated discovery
endpoint. `Startup:MinimumSplashMilliseconds` controls the minimum startup
surface duration; and `Blazor:MaximumReceiveMessageSizeBytes` bounds interactive
payloads. `VersionHistory:HistoryRoot` optionally overrides the local Git
history root. The distributable supplies Lorekeeper's public GitHub OAuth client
ID; forks and custom deployments can replace it through
`VersionHistory:GitHub:ClientId` or the standard environment-variable override.
The version-history chapter owns those feature contracts;
provider-owned keys are described in the providers chapter.

`Diagnostics:PerformanceTracePath` is absent by default. When set, startup
accepts it only for a file below `.artifacts/performance/`; any other value fails
closed. The opt-in writer records only UTC timestamp, fixed metric name, numeric
duration, and sequence for the M0.3 local diagnostic path. It never records
manuscript text, document identity, credentials, provider payloads, or requests,
and it does not change authoring, save, update, or provider behavior.
`Diagnostics:PerformanceTraceLimitPerMetric` is an optional positive local-run
bound used to separate warm-ups from measured samples. Neither key is a shipped
product setting or a replacement for target-platform validation.

## Key files and file families

| File or family | Architectural role |
|---|---|
| [`Lorekeeper/Program.cs`](../../Lorekeeper/Program.cs) | Application composition root for host mode, DI, middleware, endpoints, startup gating, Electron window creation, and desktop update setup. |
| [`Lorekeeper/Lorekeeper.csproj`](../../Lorekeeper/Lorekeeper.csproj) | .NET 10 application definition, warnings-as-errors policy, Electron packaging integration, and managed/native dependency boundary. Release alone pins runtime patch 10.0.12; Debug preserves its installed SDK runtime. |
| [`Lorekeeper/appsettings*.json`](../../Lorekeeper/appsettings.json) | Versioned defaults for desktop, startup, persistence, providers, Blazor, agents, images, ingest, embeddings, and research; environment variables supply deployment overrides. |
| [`Lorekeeper/Properties/launchSettings.json`](../../Lorekeeper/Properties/launchSettings.json) | Local Electron, HTTP, and HTTPS profiles; Electron is the default product/debug shape and HTTP is the explicit browser-validation profile. |
| [`Lorekeeper/Properties/electron-builder.json`](../../Lorekeeper/Properties/electron-builder.json) and [`PublishProfiles/`](../../Lorekeeper/Properties/PublishProfiles/) | Electron packaging metadata/targets and runtime-specific self-contained .NET publication profiles. |
| [`Lorekeeper/Startup/`](../../Lorekeeper/Startup/) | Immutable startup state/readiness gate and the hosted bootstrap worker that releases normal workers only after migration and initialization are safe. |
| [`Lorekeeper/Diagnostics/DevFileLoggerProvider.cs`](../../Lorekeeper/Diagnostics/DevFileLoggerProvider.cs), [`LogRedaction.cs`](../../Lorekeeper/Diagnostics/LogRedaction.cs), and [`PerformanceTraceWriter.cs`](../../Lorekeeper/Diagnostics/PerformanceTraceWriter.cs) | Development-only diagnostic file logging (bounded daily files under `%LOCALAPPDATA%\Lorekeeper\dev-logs`, 14-day/50 MB retention), fail-safe provider/tool log redaction, and the separately opt-in timestamp-only M0.3 local trace writer. The trace writer rejects outputs outside ignored `.artifacts/performance/`. |
| [`Lorekeeper/Components/App.razor`](../../Lorekeeper/Components/App.razor), [`Routes.razor`](../../Lorekeeper/Components/Routes.razor), and [`StartupScreen.razor`](../../Lorekeeper/Components/StartupScreen.razor) | Document shell, route wiring, pre-paint theming, and gated startup/recovery/failure presentation. |
| [`Lorekeeper/Components/Layout/`](../../Lorekeeper/Components/Layout/) | Shared application chrome, update control, theme switch, configuration shell, print layout, headings, and reconnect UI. |
| [`Lorekeeper/Components/ConfirmationDialog.razor`](../../Lorekeeper/Components/ConfirmationDialog.razor) | Required application-owned destructive/consequential confirmation surface. |
| [`Lorekeeper/wwwroot/app.css`](../../Lorekeeper/wwwroot/app.css) and [`wwwroot/branding/`](../../Lorekeeper/wwwroot/branding/) | Global design tokens and application identity assets shared by browser and desktop hosts. |
| [`Lorekeeper/Desktop/`](../../Lorekeeper/Desktop/) | Desktop update state and constrained public-release discovery; platform-specific Electron mechanics remain invoked from the composition root. |
| [`eng/linux/AppRun.sh`](../../eng/linux/AppRun.sh) | Source-owned AppImage launcher; preserves normal Electron sandbox behavior instead of the pinned builder's automatic no-sandbox fallback. Native desktop/AppArmor acceptance is separate evidence. |

Linux packaging uses electron-builder's supported AppImage toolset override with
the SHA-pinned 1.0.3 archive and official 20251108 static runtime. The temporary
toolset excludes the old optional `lib/x64` compatibility libraries; the final
AppImage must have no such bundled compatibility closure, match the exact runtime
prefix hash, and retain the source-owned launcher. Native desktop/AppArmor
acceptance and the modified libfuse LGPL source/relink material remain separate
unresolved evidence.

## Related chapters

- [Architecture index](../architecture.md) — global invariants and task-to-chapter routing.
- [Providers and background work](providers-background.md) — provider configuration, network transports, queues, workers, and cancellation.
- [Persistence, migrations, and import](persistence-migrations-import.md) — database operations, startup migration order, recovery, and data paths.
- [Assistants and chat](assistants-chat.md) — Blazor-independent conversation runtimes and shared chat surfaces.
- [Composition and media](composition-media.md) — shared image UI and visual-workspace browser bridges.
- [Version history and synchronization](version-history-sync.md) — local Git
  paths, checkpoint/restore boundaries, explicit remote attachment, and durable
  automatic push actions.
- [Press production](press-production.md) — packaged native renderer and publication job processing.
- [Validation and documentation](validation-documentation.md) — required build/startup checks and desktop/release validation.

## Relevant verification

For normal host or shared-UI source changes, build the solution and run the
permitted data-safety tests. Only with explicit authorization, start the HTTP
profile and confirm the local host reaches a safe startup state without
startup exceptions, and terminate it. Do not reorder launch profiles to make HTTP
the default. Electron startup is checked only when explicitly required, and that
process must also be terminated afterward.

For theming or shared layout changes, static inspection must confirm all surfaces
use the shared tokens and application-owned dialog policy. Browser automation,
screenshots, and manual UI checks require explicit user authorization. For desktop
binding, updater, packaging, or target-specific behavior, use the applicable
release scripts and target operating system; a solution build or HTTP startup does
not prove those integrations. Exact commands and claim boundaries are listed in
[Validation and documentation](validation-documentation.md).
