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

## Current architecture and invariants

### Host topology and dependency ownership

[`Program.cs`](../../Lorekeeper/Program.cs) is the composition root. It determines
Electron mode, resolves development versus packaged data placement, configures
ASP.NET Core and Blazor, registers application services and hosted workers, maps
OAuth and media endpoints, installs the startup gate, and creates the desktop
window. Keep provider-specific transport behavior behind provider-neutral
contracts and keep feature behavior in injected services; the composition root
should wire those owners rather than reproduce their logic.

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

Minimal API endpoints exist only where an HTTP boundary is necessary: Codex OAuth
redirects and scoped binary/media delivery are representative examples. Endpoint
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
`localhost:1455`; Codex OAuth depends on
`http://localhost:1455/auth/callback`. A port change must update the desktop
binding and an accepted OAuth redirect together. The host remains local-only
unless a deliberate architecture and security change expands its exposure.

Electron-only responsibilities include hardened binding, desktop-window creation,
shutdown, installed-Windows automatic updates, and opening deliberate external
handoffs. Installed Windows builds use Electron's updater. Windows portable and
macOS builds use the constrained public latest-release endpoint, require a newer
stable version with an applicable platform/architecture asset, and open the
release in the operating system browser. Update state is exposed to the shared
top-bar control rather than owned by individual pages.

The automatic updater downloads in the background and offers an application-owned
restart action only after Electron reports readiness. Manual discovery reuses an
ETag, accepts stable SemVer releases only, constrains release and asset URLs to the
configured public repository, and shows a download action only for an applicable
newer artifact. Failures remain non-fatal UI state and must not prevent the local
workspace from opening. Never treat public release metadata as trusted executable
content without the packaging and updater checks described in the validation
chapter.

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
`Desktop:UsePerUserDataDirectory` controls packaged data placement;
`Desktop:UpdateCheckIntervalMinutes` and `Desktop:ReleaseApiUrl` define update
discovery; `Startup:MinimumSplashMilliseconds` controls the minimum startup
surface duration; and `Blazor:MaximumReceiveMessageSizeBytes` bounds interactive
payloads. `VersionHistory:HistoryRoot` optionally overrides the local Git
history root. The distributable supplies Lorekeeper's public GitHub OAuth client
ID; forks and custom deployments can replace it through
`VersionHistory:GitHub:ClientId` or the standard environment-variable override.
The version-history chapter owns those feature contracts;
provider-owned keys are described in the providers chapter.

## Key files and file families

| File or family | Architectural role |
|---|---|
| [`Lorekeeper/Program.cs`](../../Lorekeeper/Program.cs) | Application composition root for host mode, DI, middleware, endpoints, startup gating, Electron window creation, and desktop update setup. |
| [`Lorekeeper/Lorekeeper.csproj`](../../Lorekeeper/Lorekeeper.csproj) | .NET 10 application definition, warnings-as-errors policy, Electron packaging integration, and managed/native dependency boundary. |
| [`Lorekeeper/appsettings*.json`](../../Lorekeeper/appsettings.json) | Versioned defaults for desktop, startup, persistence, providers, Blazor, agents, images, ingest, embeddings, and research; environment variables supply deployment overrides. |
| [`Lorekeeper/Properties/launchSettings.json`](../../Lorekeeper/Properties/launchSettings.json) | Local Electron, HTTP, and HTTPS profiles; Electron is the default product/debug shape and HTTP is the explicit browser-validation profile. |
| [`Lorekeeper/Properties/electron-builder.json`](../../Lorekeeper/Properties/electron-builder.json) and [`PublishProfiles/`](../../Lorekeeper/Properties/PublishProfiles/) | Electron packaging metadata/targets and runtime-specific self-contained .NET publication profiles. |
| [`Lorekeeper/Startup/`](../../Lorekeeper/Startup/) | Immutable startup state/readiness gate and the hosted bootstrap worker that releases normal workers only after migration and initialization are safe. |
| [`Lorekeeper/Components/App.razor`](../../Lorekeeper/Components/App.razor), [`Routes.razor`](../../Lorekeeper/Components/Routes.razor), and [`StartupScreen.razor`](../../Lorekeeper/Components/StartupScreen.razor) | Document shell, route wiring, pre-paint theming, and gated startup/recovery/failure presentation. |
| [`Lorekeeper/Components/Layout/`](../../Lorekeeper/Components/Layout/) | Shared application chrome, update control, theme switch, configuration shell, print layout, headings, and reconnect UI. |
| [`Lorekeeper/Components/ConfirmationDialog.razor`](../../Lorekeeper/Components/ConfirmationDialog.razor) | Required application-owned destructive/consequential confirmation surface. |
| [`Lorekeeper/wwwroot/app.css`](../../Lorekeeper/wwwroot/app.css) and [`wwwroot/branding/`](../../Lorekeeper/wwwroot/branding/) | Global design tokens and application identity assets shared by browser and desktop hosts. |
| [`Lorekeeper/Desktop/`](../../Lorekeeper/Desktop/) | Desktop update state and constrained public-release discovery; platform-specific Electron mechanics remain invoked from the composition root. |

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

For normal host or shared-UI source changes, build the solution and start the
explicit HTTP profile, confirm the local host reaches a safe startup state without
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
