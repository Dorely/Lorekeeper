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

The solution contains the application and its authorized migration/format
fixture test project. Standalone, non-production
`Lorekeeper.Press` and `Lorekeeper.Press.Weasy` projects are retained as
disposable renderer/conformance spikes; neither is referenced by the solution,
packaged, or invoked by the application. The Rust candidate is the rejected PDF
1.7 comparison implementation. The exact-pinned Python/WeasyPrint 69 candidate
is accepted only as the foundation for a future `Preview` press runtime; it is
not current application behavior or independently verified PDF/X output.
`Program.cs` owns host startup,
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
synchronization, fonts, geometry, fitting, and layout diagnostics. Chapters now
persist one versioned semantic manuscript JSON document plus a monotonic
revision; stable block IDs are the canonical prose anchors for Picture Page text
ranges and illustrated-prose images. `IManuscriptService` is the only runtime
write boundary for manuscript content. The revision is an EF optimistic
concurrency token, so simultaneous writers cannot silently overwrite one
another. Manuscript reference writes and named-style/project-image deletion
also share a project-scoped async mutation boundary backed by an in-process
semaphore and an adjacent per-database file lock. The boundary therefore
serializes browser-hosted and desktop processes sharing one SQLite file, so
validate-then-save and validate-then-delete cannot interleave into dangling JSON
references. Composite import, Picture Page text, and image-deletion workflows
use explicit under-held-lease service methods; mutation ownership is never
inferred from async execution context.
The
ProseMirror editor, assistant, contest, revision-agent, approval,
import, indexing, visual, and publishing paths consume the same document or
projection. Direct chapter-body persistence is no longer a runtime path.
Picture Page text edits resolve whole-block references into move/insert/delete/
replace manuscript operations, preserving block identity when text boxes move
in reading order. Partial-block range text edits fail closed and must be made
in the manuscript editor; layout-only changes and whole-block edits remain
available in Picture Page. Picture Page and Illustrated Prose layouts carry
monotonic revisions. Every reference-changing save, manuscript reconciliation,
mode initialization, and image-reference removal advances the relevant layout
revision; stale visual editors fail closed, reload the current layout, and show
a visible conflict notice. Visual saves validate every referenced image under
the same cross-process project mutation lease.
`IPageGeometryService` provides the shared page/spread calculations used by the
editor, image targets, diagnostics, previews, and exporters.
`IPublicationEditionService` owns the one-to-many edition aggregate: product
settings and identifiers, independently ordered content, semantic front/back
matter, named-style mappings, image placements, Picture Page cover selection,
revision tokens, cloning, archival, comparison, audit history, and deterministic
source fingerprints. `IPublishService` is now projection/export-only; UI and
assistant mutations use the edition service rather than parallel publish logic.
Project export v10 writes the edition aggregate and isolated v9-or-earlier import
adapters translate the obsolete single-profile shape at the import boundary.

The current Print/PDF path remains an edition-scoped HTML print view handed to
the browser or operating-system print dialog. It does not yet generate, parse,
or certify PDF bytes, implement PDF/X, or represent vendor-specific preflight.
Those are the next publishing-roadmap features rather than current claims.

`Lorekeeper.Press` proves only a local PDF 1.7 fixture path. Its versioned JSON
protocol validates child job IDs, accepts semantic book content and explicit
geometry, and returns artifact hashes plus structured diagnostics. Its Typst
world has no filesystem or network loader. The experimental Ingram profile
fails closed and emits no artifact because Typst 0.15.1 has no PDF/X mode and no
reviewed CMYK press profile is bundled. This spike is not a production renderer,
KDP/Ingram compatibility claim, or substitute for the future application
service and assistant boundary.

`Lorekeeper.Press.Weasy` retains that protocol shape and proves a contained
Windows x64 fixture that emits PDF 1.3 files declaring PDF/X-1a:2001 with a
fingerprinted CMYK output intent. Generated HTML/CSS is code-owned, external
resource reads are restricted to the exact profile URI, and output is atomically
published into immutable child job directories. Its internal pypdf inspection
does not establish standards conformance. The adapter uses an exact-version
internal WeasyPrint API because the stock `pdf/x-1a` variant declares the wrong
2003 revision. Its controlled Windows fixture carries pinned Liberation Serif
files and requires the future owning application service to set the sibling
Fontconfig environment before native process startup. Its launcher verifies that
environment and the sibling font/config/license hashes before importing
WeasyPrint. Native payloads are extracted into a fresh build-owned directory
from the fingerprinted official portable executable, then the final collected
binaries are source-classified. The release-license gate remains closed because
the native notice bundle is incomplete. Acrobat/vendor/physical-proof evidence
and cross-platform packaging also remain incomplete, so the spike returns no
independently validated or claimed standard and is not a production runtime.

The structured-manuscript, publication-edition, and Preview press-runtime
boundaries are implemented. `PublicationRenderService` persists edition-scoped
queue state, immutable artifact bytes/hashes, renderer/profile provenance,
diagnostics, and stable block-to-page mappings. `PublicationRenderWorker`
recovers interrupted jobs and owns cancellation; `PublicationRenderProcessor`
contains the child process, clears its environment, bounds its lifetime and
paths, and verifies every returned length/hash before persistence. Project-
scoped range endpoints serve actual PDF bytes, and source-fingerprint mismatch
marks immutable artifacts stale.

Development resolves the exact-locked Python project and fingerprinted native
payload. Packaged releases must configure a frozen renderer and ship controlled
fonts/notices. This remains Preview, not a PDF/X or vendor-conformance claim.
`PublishAssistantTools` exposes the full edition and render service surfaces
with the same IDs, validation, revisions, fingerprints, and diagnostics as the
UI.

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

Startup delegates the structured-manuscript cutover to
`IManuscriptMigrationService` before normal initialization. For a legacy
database it runs `PRAGMA quick_check`, creates a consistent SQLite Online Backup
API snapshot under `.migration-backups/manuscripts`, applies the forward schema,
converts all live and historical prose and visual anchors transactionally,
compares aggregate normalized-text hashes, and records a migration journal.
Migration and restore scheduling share a crash-releasing cross-process file lock, and the
transformed rows plus completed journal commit in one SQLite transaction so an
interrupted schema-only startup resumes safely. Any transform failure preserves
the pre-migration backup and starts a current-schema, projectless recovery shell
whose failed journal keeps Settings > Data Recovery reachable. That screen
exposes redacted journal/backup state and requires a short-lived, explicit
confirmation token to schedule restore. The database is replaced only during
the next startup, before normal workers start, and a diagnostic backup is made
first. Backup files and their directory use owner-only ACLs/permissions.
The current manuscript schema is v2. Startup safely upgrades v1 documents in
live chapters and every historical/review JSON payload under the same protected
backup, transaction, projection-hash, and journal boundary. Project export
format v9 carries v2 manuscripts, stable visual references, and project named
paragraph/character style definitions. The v8 manuscript-v1 adapter and v1-v7
text adapters exist only at the import boundary.

Startup then delegates the independent single-profile-to-editions cutover to
`IPublicationEditionMigrationService`. Before applying the v10 forward
migration it runs `quick_check`, creates an SQLite Online Backup snapshot under
`.migration-backups/editions`, and hashes the normalized ownership mapping for
profiles, selections, and placements. The migration creates a default paperback
for each legacy profile and for profileless projects that already contain
publishing rows, converts legacy dedication/acknowledgments/references into
schema-v2 semantic matter, attaches every selection and placement to exactly
one edition, removes the obsolete runtime tables, and validates row counts,
foreign keys, matter documents, and an equal post-cutover mapping hash before
recording its journal. A failure aborts startup and reports the protected backup
path rather than permitting edits against a partially cut-over database.

Manuscript v2 stores structural heading level separately from edition-independent
style role, so assigning or removing a named paragraph style cannot change a
chapter heading into a subheading. Figure blocks own a project image ID,
alternative text, and caption content. Manuscript saves and assistant previews
validate image ownership; image deletion refuses live figure references; v9
imports preflight and remap figure asset IDs. Current Markdown and EPUB
publication projections consume the structured manuscript rather than flattening
these blocks and marks through the plain-text projection.

The chapter editor is an exact-pinned ProseMirror bundle built from
`tools/semantic-editor/package-lock.json`. Browser transactions are adapted to
Lorekeeper manuscript JSON and cross `IManuscriptService.ReplaceDocumentAsync`
with an expected revision; neither DOM nor HTML is persisted. Paste is
constrained by the owned schema and reports removed elements. Manual edits and
Editor/revision-worker tools share block, mark, style, validation, and
structural-inspection semantics. Named style semantic roles and
paragraph/character kinds are immutable stable keys; definitions are
revision-checked and semantic roles are unique per project and kind.

On a manuscript revision conflict, the browser adapter preserves the unsaved
v2 JSON in chapter-keyed browser/Electron local storage, locks the stale editor,
and exposes download or explicit reload-current actions. The copy is
unencrypted manuscript content outside SQLite; it survives UI remounts, is not
part of database backup/export, and is deleted only when the user explicitly
loads the current saved manuscript. Clearing site data removes it.

Windows and macOS release builders run the semantic-editor locked install,
fixtures, audit, and deterministic rebuild, fail if the committed bundle is
stale, and verify that exactly one matching bundle and shipped notice reached
the release stage.

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
- Python 3.13 or 3.14 plus uv only when exercising the disposable
  `Lorekeeper.Press.Weasy` fallback spike

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

The user explicitly authorized automated publishing fixtures on 2026-07-30.
Run the application-level manuscript, migration, visual-anchor, and project
export fixtures with:

```powershell
dotnet test Lorekeeper.Tests\Lorekeeper.Tests.csproj
npm ci --prefix tools/semantic-editor
npm test --prefix tools/semantic-editor
npm run build --prefix tools/semantic-editor
```

The standalone press spike owns separate Rust fixture tests. Run its locked
verification separately:

```powershell
cd Lorekeeper.Press
cargo test --locked
.\scripts\verify-spike.ps1 -PopplerBin <poppler-bin-directory>
```

Run the fallback source fixtures separately:

```powershell
cd Lorekeeper.Press.Weasy
uv run --locked python -m unittest discover -s tests -v
```

The frozen fallback verification additionally requires an explicitly supplied,
fingerprinted CMYK profile and a controlled native Pango/Fontconfig stack; see
`Lorekeeper.Press.Weasy/scripts/build-windows-spike.ps1` and
`Lorekeeper.Press.Weasy/scripts/verify-spike.ps1`.

Successful compilation does not validate OAuth, provider calls,
embeddings, web search, image generation, publication output, packaging,
automatic updates, or OS-specific Electron behavior. Exercise the relevant
integration on the relevant platform before claiming it works, and report
anything not exercised.
