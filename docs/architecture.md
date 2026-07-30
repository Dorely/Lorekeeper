# Lorekeeper Architecture

## Status and Scope

Lorekeeper is a desktop-first, local AI-assisted long-form bookmaking workbench.
Its implemented foundation centers on narrative coherence, project research,
drafting, images, page composition, and early publishing workflows. Normal user
operation is through an Electron.NET desktop shell backed by a local ASP.NET
Core host. The same Blazor application can run directly in a browser for local
development and debugging.

The project declares Windows x64, Linux x64, macOS x64, and macOS arm64 runtime
identifiers. Declared targets preserve the intended cross-platform desktop
shape; they are not evidence that packaging, updates, or platform-specific
behavior has been validated on every operating system.

The implemented workbench includes project and outline management, graph-backed
story state, semantic and lexical retrieval, source ingest, web research,
writing samples and coaching, context-aware chapter editing, assistant review
and contest workflows, project image generation and editing, illustrated and
picture-page composition, import/export, TXT, Markdown, EPUB, and a browser/OS
print-preview workflow. `VISION.md` remains the product direction and is not
proof that every future bookmaking goal is complete. The researched delivery
sequence and verification gates are documented in
`docs/publishing-roadmap.md`; roadmap statuses do not change this document's
current-runtime claims.

## Stack and Host Topology

- .NET 10 ASP.NET Core with Blazor Interactive Server
- Electron.NET for the desktop shell
- EF Core with SQLite for local persistence and migrations
- sqlite-vec and SQLite FTS5 for semantic and lexical project retrieval
- `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI`, and the OpenAI
  .NET SDK for chat abstraction, Codex account access, and OpenAI-compatible
  providers
- SkiaSharp for image, page, font, and publishing operations
- PdfPig, Docnet, and VersOne.Epub for source ingest and EPUB handling
- Bootstrap and vis-network vendored under `Lorekeeper/wwwroot`

The solution contains one application project. A standalone,
non-production `Lorekeeper.Press` Rust 2024 crate is retained as a disposable
renderer/conformance spike; it is not referenced by the solution, packaged, or
invoked by the application. `Program.cs` owns host startup,
dependency registration, middleware, local media and OAuth endpoints, database
migration, interrupted-work reconciliation, desktop update setup, and Electron
window creation. Blazor interactivity is opted into per page or component with
Interactive Server render mode; there is no WebAssembly client application.

Electron is the primary debug and user target. The desktop host binds to the
configured local host and port, while the explicit `http` launch profile remains
available for browser development and validation. Application services,
provider clients, persistence, indexing, and background work execute inside the
server process and are consumed through dependency injection.

## Runtime and Ownership Boundaries

Razor pages and components own interaction and presentation state. Domain
behavior belongs in injected services so manual UI actions, assistant tools,
background workers, and import workflows apply the same validation, persistence,
graph synchronization, and indexing rules.

The main application flow is:

1. A Razor component, assistant tool, endpoint, or background processor requests
   an operation through an owning service.
2. The service validates project scope and coordinates domain, graph, context,
   image, or publishing behavior.
3. Repositories and `AppDbContext` persist authoritative state.
4. Indexing services update graph links, FTS5 rows, and sqlite-vec rows where the
   changed data is retrievable context.
5. App-process runtimes and notifiers publish progress or refreshed state to
   active Blazor circuits.

### Story Structure, Graph, and Retrieval

Projects own Project Guidance, a structured Book Brief, acts, chapters, writing
samples, conversations, images, fonts, jobs, publishing configuration, and
graph rows. Acts and chapters form the editable outline spine.
`IOutlineGraphSync` projects that spine into graph nodes and ordered `HasChild`
edges. `IEntityService`, `IProjectFactService`, and `IProjectGraphService` own
non-structural entities, project facts, parentage, and relationships.

The relational graph is authoritative for structured story memory. Automatic
exact-name and alias mentions are low-priority, read-only `AutoMention` edges;
manual and structural mutations must pass through their owning services so graph
state and retrieval indexes remain synchronized.

`IProjectSearchService` combines FTS5 keyword results with sqlite-vec semantic
results. `IContextIndexingService`, ingest indexing, chapter services, and the
embedding rebuild worker maintain retrievable rows. Changing a model, graph
concept, or stored text that participates in retrieval must be traced through
both lexical and vector indexing paths.

`ContextBuilder` assembles the Editor's bounded working context from protected
authorial direction, prior chapter material, persisted include/exclude choices,
graph relationships, project search, and entity visual references.
`SystemPromptComposer` owns the one system-role prompt shape. Project Guidance
and Book Brief content remain user-owned inputs; code-owned professional and
tool instructions must not be persisted as editable project guidance.

### Assistant Conversations and Review

The five user-facing assistant surfaces are Outline, Editor, Writing Coach,
Research, and Images. Their adapters use the shared `ChatTurnEngine` protocol
and feature-specific tools, prompts, repositories, and streaming update records.
`ChatTurnRuntime` and the singleton feature turn runners keep active turns alive
across component disposal, buffer updates for reopened panels, and preserve
explicit Stop as the cancellation path. Do not move active-turn ownership into a
single Razor component or circuit.

Assistant tool mutations that require review are stored as `AiChange` batches
and applied through `IAiChangeApprovalService`. Editor Contest Mode captures a
terminal context snapshot and stores independent model candidates. Editor
revision agents persist one worker session per assigned chapter and return
prose-only edits to the coordinating Editor turn. Tool contracts, prompts,
persistence, review UI, and approval behavior must evolve together.

### Ingest, Research, and Background Work

Ingest preprocesses text, EPUB, PDF, image, and webpage material into durable
sources, logical chunks, locator-bearing blocks, visual candidates, graph
structure, canon knowledge, and retrieval fragments. `IngestJobWorker` drains
the in-process queue and the processor persists checkpoints, reports, events,
and finalization results. Restart and delete behavior subtracts source-owned
knowledge and refreshes affected indexes rather than leaving orphan graph state.

Research combines configured web search, guarded and robots-aware page reads,
cached webpage candidates, image inspection, graph reads, and reviewable graph
mutations. Web URL policy, private-network blocking, throttling, and fetch
provenance belong in the research services rather than assistant prompts or UI
components.

Embedding rebuilds, project imports, ingest jobs, image jobs, and editor revision
jobs use app-process queues, workers, or notifiers. Their durable job records are
the audit and reconciliation boundary across process restarts; restart behavior
is feature-specific, and a renderer or Blazor circuit must not own their
lifetime.

### Images, Page Composition, and Publishing

`IProjectImageService` owns reusable project assets and deterministic local
crops. `IProjectImageJobService` owns queued generation/edit records and audit
metadata. `IProjectImageGenerationRuntime` owns FIFO execution, retries,
cancellation, previews, and completion notifications, while provider transport
details remain behind `IProjectImageProvider`.

Entity visual examples are ordered associations between graph entities and
project images. Image prompting, editor context, research/ingest promotion, and
entity indexing share these associations as canonical visual evidence. Changes
to visual ownership or reference semantics must be traced through all of those
consumers.

Chapter visual services own illustrated-prose and Picture Page state, text
synchronization, fonts, geometry, fitting, and layout diagnostics.
`IPageGeometryService` provides the shared page/spread calculations used by the
editor, image targets, diagnostics, previews, and exporters. Publishing services
own metadata, outline selection, image placement, covers, and TXT, Markdown,
EPUB, and browser/OS print-preview output. Do not duplicate page geometry or
silently diverge editor and export rendering rules.

The current Print/PDF path is an HTML print view handed to the browser or
operating-system print dialog. It does not generate, parse, or certify PDF bytes,
does not implement PDF/X, and does not represent vendor-specific preflight.
`PublishProfile` is currently one project-owned profile rather than a
multi-edition production model. These are current boundaries, not press-ready
claims.

`Lorekeeper.Press` proves only a local PDF 1.7 fixture path. Its versioned JSON
protocol validates child job IDs, accepts semantic book content and explicit
geometry, and returns artifact hashes plus structured diagnostics. Its Typst
world has no filesystem or network loader. The experimental Ingram profile
fails closed and emits no artifact because Typst 0.15.1 has no PDF/X mode and no
reviewed CMYK press profile is bundled. This spike is not a production renderer,
KDP/Ingram compatibility claim, or substitute for the future application
service and assistant boundary.

The planned publishing architecture is documented, but not implemented, in the
publishing roadmap and supporting research briefs. Its intended boundaries are a
versioned semantic manuscript, edition-specific projections, a separately
contained press renderer, immutable artifacts and manifests, and complete UI/
assistant access through shared application services. When implementation
changes those boundaries, this architecture document must be updated in the
same feature; the roadmap must not be used as a substitute for current technical
documentation.

### Desktop and Release Behavior

Electron-specific binding, window creation, shutdown, update state, release
discovery, and external-browser handoff belong in the desktop host path.
Installed Windows builds use the Electron updater. Windows portable and macOS
builds use the constrained public latest-release API and open an applicable
newer release in the system browser.

Release tooling is split between clean Windows and native macOS builders plus a
Windows release orchestrator. The cross-platform publisher requires a clean
local `main` matching `origin/main`, dispatches correlated macOS jobs, builds
Windows locally, validates all requested artifacts, publishes atomically, and
removes temporary Actions artifacts afterward. Packaging and updater changes
must be validated through the relevant scripts and target operating system.

## Persistence, Configuration, and Security

Lorekeeper uses a local SQLite database through `AppDbContext`. It stores
provider and OAuth configuration, projects, Book Briefs, outlines, graph state,
chat and worker transcripts, review/contest state, writing samples, ingest and
import jobs, images and masks, entity visual links, fonts, publishing state, and
binary assets. SQLite startup applies a busy timeout and WAL journal mode.
sqlite-vec and internal FTS5 structures are initialized outside normal EF
migrations.

Applied EF Core migration files are immutable schema history. Never edit,
reorder, or delete an applied migration to make the migration directory resemble
the current model. Add a forward migration for schema changes and remove
superseded runtime models, services, and paths in the same feature. Historical
migrations that create structures later dropped by another migration are
expected and are not compatibility shims.

Configuration belongs in `appsettings.json`, environment-specific settings, and
environment-variable overrides. Options records own desktop hosting and updates,
OAuth, persistence, Blazor limits, token counting, image generation, ingest,
embeddings, agents, and web research. Avoid hard-coding configuration in
components or feature entities.

LLM and search API keys are stored on their provider rows; Codex OAuth access and
refresh tokens are stored in dedicated SQLite rows. This is local persistence,
not an operating-system credential vault, encryption-at-rest claim, or permission
to expose credentials. Never log secrets, authorization codes, access tokens,
refresh tokens, or sensitive provider payloads.

Desktop development stores its database in the repository by default. Packaged
release builds resolve per-user application-data storage so installed,
portable, and mounted-DMG application locations remain disposable. Database
files and verification databases are local state and must remain ignored by
Git.

The desktop and HTTP development profiles use `localhost:1455`. Codex OAuth
depends on the configured `http://localhost:1455/auth/callback` redirect. Port
changes must update both desktop binding and an accepted OAuth redirect
configuration. Keep the host local-only unless a deliberate architecture and
security change expands its exposure.

## Build and Validation

Requirements:

- .NET 10 SDK, pinned by `global.json`
- Node.js 22.12 or later for Electron.NET desktop builds and packaging
- Rust 1.92 or later only when building the disposable `Lorekeeper.Press` spike

Build and start the browser-hosted development app:

```powershell
dotnet build Lorekeeper.sln
dotnet run --project Lorekeeper --launch-profile http
```

Start the Electron desktop shell:

```powershell
dotnet run --project Lorekeeper --launch-profile electron
```

Build the current Windows release artifacts:

```powershell
.\scripts\build-windows-release.ps1
```

Publish Windows-only or cross-platform releases:

```powershell
.\scripts\publish-release.ps1 -Version <version> -WindowsOnly
.\scripts\publish-release.ps1 -Version <version>
```

Normal source changes require a successful solution build followed by an HTTP
profile startup check with no startup exceptions. Always terminate the host
after validation. Electron startup, browser UI checks, screenshots, Playwright,
and manual UI validation are performed only when explicitly requested.
Documentation-only work must still validate every referenced path,
configuration key, launch profile, and command, and should run broader checks
when the documentation asserts that those checks work.

The .NET solution still has no automated test project. The user explicitly
authorized automated publishing fixtures on 2026-07-30, and the standalone
press spike owns Rust fixture tests. Run its locked verification separately:

```powershell
cd Lorekeeper.Press
cargo test --locked
.\scripts\verify-spike.ps1 -PopplerBin <poppler-bin-directory>
```

Successful compilation does not validate OAuth, provider calls,
embeddings, web search, image generation, publication output, packaging,
automatic updates, or OS-specific Electron behavior. Exercise the relevant
integration on the relevant platform before claiming it works, and report
anything not exercised.
