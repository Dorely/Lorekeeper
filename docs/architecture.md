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
picture-page composition, import/export, TXT, Markdown, EPUB, and immutable
Lorekeeper-validated PDF generation/view/download. `VISION.md` remains the product direction and is not
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
- exact-pinned Rust 1.97.1 `Lorekeeper.Press`, using `pdf-writer`, `harfrust`,
  `subsetter`, `moxcms`, `png`, and a separate `lopdf` post-write inspector
- Bootstrap and vis-network vendored under `Lorekeeper/wwwroot`

The solution contains the application and its authorized migration/format
fixture test project. `Lorekeeper.Press` is a Lorekeeper-owned native subproject
and the sole paperback renderer. MSBuild builds its locked native executable and
packages it into the application-owned runtime for Debug and Release. The
retired Typst and WeasyPrint implementations have no runtime code, registration,
settings, scripts, or machine fallback.
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

The six user-facing assistant surfaces are Outline, Editor, Writing Coach,
Research, Images, and Publish. Their adapters use the shared `ChatTurnEngine` protocol
and feature-specific tools, prompts, repositories, and streaming update records.
Tool rounds keep the provider-originated `FunctionCallContent` in memory until
the correlated tool result has been submitted. OpenAI-compatible clients retain
unknown tool-call `extra_content` from both buffered and streaming responses and
restore it on that assistant message; this is required by providers such as
Gemini that validate thought signatures on the immediate tool-result round.
Persisted cross-turn history remains text-only and does not persist or replay
provider tool protocol metadata.
`ChatTurnRuntime` and the singleton feature turn runners keep active turns alive
across component disposal, buffer updates for reopened panels, and preserve
explicit Stop as the cancellation path. Do not move active-turn ownership into a
single Razor component or circuit. Surface-scoped maintenance leases make
conversation reset atomic against active or newly starting turns across windows.

Assistant tool mutations that require review are stored as `AiChange` batches
and applied through `IAiChangeApprovalService`. Editor Contest Mode captures a
terminal context snapshot and stores independent model candidates. Editor
revision agents persist one worker session per assigned chapter and return only
compact IDs, statuses, summaries, and errors to the coordinating Editor turn.
Full instructions, operations, proposals, raw responses, and transcripts remain
in durable session detail for the review UI and never enter the parent model
result. The coordinator cancels and awaits any pending progress-channel read
before disposing its async enumerator, so a terminal job cannot end the parent
turn with a concurrent-disposal `NotSupportedException`. Tool contracts,
prompts, persistence, review UI, and approval behavior must evolve together.

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
Editor Chat submits each semantic operation payload only to
`preview_manuscript_operations`. That tool validates and retains the exact
projected document in turn-local memory behind an opaque, chapter-specific
preview ID. `apply_manuscript_operations` accepts only that one-use ID, verifies
that the source document is still identical, then writes the retained document
through `IManuscriptService` (or stages it in the Review-edits overlay). Preview
results intentionally return only IDs, hashes, revisions, and counts; full
manuscript text and operations are never echoed into the apply round. Preview
IDs do not persist across turns, and a newer preview for the same chapter
supersedes the older one.
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
matter, named-style mappings, image placements, dedicated project-image cover
selection,
revision tokens, cloning, archival, comparison, audit history, and deterministic
source fingerprints. `IPublishService` is now projection/export-only; UI and
assistant mutations use the edition service rather than parallel publish logic.
The v14 forward migration replaces the obsolete cover-chapter foreign key with
an edition-owned project-image foreign key. It preserves an existing selection
only when the referenced Picture Page resolves to one distinct project image;
for every former cover selection, the Chapter and PageLayout bytes remain intact
while that edition's outline row is excluded to preserve its prior cover-only
body semantics. Ambiguous compositions require an explicit new cover choice.
If SQLite is interrupted after a provider-required table-rebuild commit but
before EF records v14, the guarded edition migration validates and completes
the known intermediate schema, restores required indexes, and records the
history row transactionally before normal migration startup continues.
Archived editions are immutable at every owning mutation boundary, including
cover, package-build, and proof writes; their existing artifacts remain readable
and exportable, and cloning creates the editable continuation. New paperback
editions use their vendor-owned profile defaults of 6 × 9 in, 0.75 in
margins, 11 pt body text, and 1.4 line height.
Project export v13 writes the complete edition aggregate, semantic manuscripts,
named styles, visual references, and project-owned font families/faces with
binary hashes. Isolated v8-v12 adapters handle older structured exports and the
v1-v7 adapters handle legacy plain text. A pre-v12 backup that references a
project font fails closed because those formats did not carry the required font
binary; a foreign custom-font key is never persisted. Each coherent database
import is transactional across project direction, images, fonts, styles,
structure, editions, and graph state; validation failure rolls back the entire
destination mutation before the failed job/report is recorded. The Completed
job state commits in that same transaction. Service-triggered context/vector
index work is deferred for the transaction and discarded at its boundary;
explicit post-commit indexing rebuilds the committed state instead. That
post-commit work is best-effort and can add warnings, but cannot relabel
committed imported data as a failed import.

Paperback PDF output exists only through the contained press runtime, which
generates, parses, fingerprints, and stores immutable interior and full-wrap
cover bytes and feeds versioned vendor-specific internal preflight. The Publish
workspace selects Generate, active/cancel, Retry, Regenerate, or separate Save
interior/cover actions from current render and artifact state; Save uses the
hash-verifying immutable artifact endpoints and never invokes browser print.
EPUB editions cannot request press output, and paperback editions cannot export
EPUB, preventing product-form identifiers and metadata from crossing formats.

`Lorekeeper.Press` owns protocol v3, deterministic layout, English/Latin shaping
and glyph diagnostics, mixed-face paragraph/character/inline typography, font
subsetting and ToUnicode maps, shaped glyph advances/offsets, bounded vertical
pagination, measured/wrapped TOC convergence, and stable page maps, ordered front
and back matter, illustrated prose, contain-fit whole Picture Page spreads,
crop-fit split leaves and other placements, bounded captions,
full-wrap cover geometry, EAN-13 bars, and PDF serialization. KDP and generic
profiles emit PDF 1.7. The Ingram profile emits PDF 1.3 with
PDF/X-1a:2001 identification, the registered CGATS21 CRPC1 CMYK output intent,
CMYK/gray-only resources, flattened alpha, embedded fonts, no encryption,
annotations, actions, or transparency, and image/page-paint colors capped at
240% total ink. A separate production `lopdf` pass reparses completed bytes before
atomic promotion. An independent black-box test harness parses raw PDF objects
without calling that validator. This is the evidence behind the scoped
“Lorekeeper validated” state; it is not evidence of vendor upload acceptance or
a human proof attestation.

Protocol v3 stages `input/request.json` and declared relative PNG assets in a
bounded job root. Each declaration carries media type, byte length, dimensions,
and SHA-256. Absolute paths, traversal, links/reparse points, undeclared files,
changed bytes, corrupt assets, unsupported formats, existing output, and
cancellation fail before artifact promotion. The renderer writes a fresh
staging directory, validates both PDFs, and renames it to `output` only after
every check succeeds; it never overwrites prior output.

The structured-manuscript, publication-edition, and owned press-runtime
boundaries are implemented. `PublicationRenderService` persists edition-scoped
queue state, immutable artifact bytes/hashes, renderer/profile provenance,
diagnostics, and stable block-to-page mappings. `PublicationRenderWorker`
recovers interrupted jobs and owns cancellation; `PublicationRenderProcessor`
contains the child process, while `PublicationPressRuntime` resolves only a
relative directory beneath the stable binary installation root
(`AppContext.BaseDirectory`), independent of the launch working/content root,
rejects reparse/missing files, verifies the schema-v3 platform/architecture
manifest, exact file inventory, sizes, and SHA-256 fingerprints, and constructs
a cleared environment. There is no inherited `PATH`, Cargo, Python, uv, Typst,
WeasyPrint, Chromium, machine PDF software, or repository fallback. Runtime
readiness and the dynamic `describe` contract are checked by the
UI, assistant, and service before a render can be queued. The processor captures
bounded stdout/stderr, bounds its lifetime and paths,
and verifies every returned length/hash before persistence. Project-
scoped range endpoints serve actual PDF/package bytes only after recomputing
their stored length and SHA-256; corrupt rows fail closed before an ETag or body
is returned. Source-fingerprint mismatch marks otherwise valid immutable
artifacts stale. Installed-renderer or selected-profile provenance mismatch also
stales otherwise current PDF artifacts. Queued jobs snapshot the dynamic
`describe` version and reject a different executable response. The guarded
Press cutover owner runs before general EF migration. Edition recovery and Press
schema advancement share one database-scoped process-local and crash-releasing
cross-process lease, so no second startup can enter the known v14 SQLite table-
rebuild/history-write handoff while another owner advances it. The cutover
creates a protected SQLite backup and ACL-protected
atomic marker, then hashes every canonical application table except migration
bookkeeping and regenerable virtual search/vector indexes; only the explicitly
transformed profile, legacy, and recovered-job columns are excluded, and
`PublishAssets` image blobs are hashed in full. Expected
publication tables must exist. The owner validates integrity, foreign keys,
unknown profiles, and exact artifact bytes/hashes, and journals either a guarded
v14-to-v15 cutover or an honestly labeled post-v15 reconciliation baseline.
Malformed markers are quarantined: a pending v15 restarts from a fresh protected
snapshot, while an already-applied cutover restores its newest protected source
backup into the existing projectless recovery shell.

`PublicationCoverService` owns the one-to-one revisioned cover design and derives
the wrap template from the latest interior page count plus edition trim, bleed,
paper, vendor, and profile. The template fingerprint forces explicit
acknowledgement after geometry changes. Acknowledgement-only revision changes
are deliberately excluded from the render source fingerprint so acknowledging
the geometry produced by a completed first render does not stale that render.
ISBN-13 validation is shared by UI,
assistant, and render gating; the contained renderer emits the EAN-13 symbol or
the permitted KDP overlay reserve, prevents back copy from entering that
reserve, and suppresses unsafe narrow-spine text. KDP and Ingram covers both
composite selected normalized PNG artwork with edition focal coordinates;
Ingram uses the owned ICC conversion and ink-limit path. Black-and-white
editions convert interior raster content to DeviceGray; cover color remains
independent and uses RGB for KDP/generic or CMYK for Ingram.

`PublicationPackageService` owns versioned Lorekeeper validation and the final
artifact-assembly boundary. It verifies current source fingerprints, correlated
interior/cover render evidence, page geometry, page-box consistency, embedded
fonts, annotations, security, Ingram output intent/transparency and fail-closed
color-space evidence, cover diagnostics, ISBN, metadata, content, supported
product geometry, page count, language, and the current English/Latin and
contained-raster-image boundaries before packaging. It
normalizes the EPUB modification timestamp and ZIP entry metadata, validates
safe EPUB entry paths plus container, OPF 3 metadata/manifest/spine, resource,
XHTML, TOC/landmark, and navigation relationships, and writes deterministic
product-form-specific package bytes. EPUB editions contain the normalized EPUB;
paperback editions contain only their validated interior/cover PDFs, so a print
identifier is never copied into a digital artifact. Every included ordered
semantic-matter document is projected into TXT, Markdown, EPUB, and contained
press output. The service persists SHA-256-addressed EPUB,
front-cover, report, manifest, package, and
exact-package proof records as edition artifacts. Package freshness combines
the source fingerprint, stable applicable input hashes/runtime provenance,
profile/rule version, assembler version, and the EPUB-exporter version only for
EPUB packages; exact PDF row IDs remain
an internal correlation snapshot and never leak into portable bytes. A short
serializable transaction rechecks
the source immediately before each package/proof write, so a concurrent
fingerprint-affecting mutation cannot be mislabeled. Digital and
physical proof confirmations are explicit user UI actions; assistant tools may
read preflight/proof state and build an eligible package but cannot approve a
proof. Physical-proof state and approval apply only to paperback editions; EPUB
reports it as not applicable. Title, copyright, and visible contents pages are
generated exclusively from edition settings, so user-authored semantic matter
cannot claim those reserved kinds and duplicate generated output. EPUB
validation remains structural and internal; broader reader-matrix results are
recorded separately. Vendor-upload results and digital/physical proof
attestations are optional human evidence and never alter the renderer's scoped
structural result.

ISBN values are strict, checksum-validated, and stored in canonical ISBN-13
form. The same ISBN may be shared only by same-format vendor editions whose
bibliographic metadata, visible content settings, physical product settings,
ordered outline, semantic matter, style mappings, and image placements match.
Vendor/profile production settings may differ. Once shared, content-affecting
edition mutations fail closed until the ISBN is cleared and the editions are
synchronized.

`eng/BuildPressRuntime.ps1` builds `Cargo.lock` with Rust 1.97.1, rejects any
unknown direct or transitive license expression, and packages only the native
executable, the OFL Lora/Nunito/Roboto Mono assets, the registered ICC profile,
third-party notices, SBOM, and exact hash manifest. Debug and Release use this
same boundary. Windows x64, Linux x64, macOS x64, and macOS arm64 build on their
native target runners; neither the app nor release package compiles Rust at
runtime.
`PublishChatService` owns one persisted, ordered, project-scoped Publish
conversation and uses the shared turn runtime for streaming, cancellation,
reconnection, image attachments, and text-only cross-turn replay. The two-column
Publish workspace flushes pending manual autosaves before each turn and refreshes
its selected edition and artifact state from structured mutation notices. Manual
publishing mutations are locked for the duration of a Publish turn, render state
is re-read after autosave, and assistant render/package changes reconnect polling,
preflight, and current artifact downloads.
`PublishAssistantTools` exposes the full edition, matter, style, placement,
render, cover, preflight, package, audit, comparison, and export service surfaces
with the same IDs, validation, revisions, fingerprints, and diagnostics as the
UI. Artifact results include current/stale state and safe view/download URLs;
proof-attestation writes remain unavailable to the assistant.

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

`IDatabaseMigrationRecoveryService` owns provider-specific backup paths,
owner-only permissions, SQLite Online Backup creation, expiring restore
confirmations, scheduled restore markers, recovery-shell creation, recursive
backup discovery, and protected pruning for every guarded migration. It never
prunes a backup referenced by a running/failed journal, scheduled restore, or
active recovery state.

Startup delegates the structured-manuscript cutover to
`IManuscriptMigrationService` before normal initialization. For a legacy
database it runs `PRAGMA quick_check`, creates a consistent SQLite Online Backup
API snapshot under `.migration-backups/manuscripts`, applies the forward schema,
converts all live and historical prose and visual anchors transactionally,
compares aggregate normalized-text hashes, and records a migration journal.
Migration and restore scheduling share a crash-releasing cross-process file lock, and the
transformed rows plus completed journal commit in one SQLite transaction so an
interrupted schema-only startup resumes safely. Any transform failure delegates
to the database recovery service, preserves the pre-migration backup, and
starts a current-schema, projectless recovery shell. Settings > Data Recovery
remains reachable. That screen
exposes redacted journal/backup state and requires a short-lived, explicit
confirmation token to schedule restore. The database is replaced only during
the next startup, before normal workers start, and a diagnostic backup is made
first. Backup files and their directory use owner-only ACLs/permissions.
The current manuscript schema is v2. Startup safely upgrades v1 documents in
live chapters and every historical/review JSON payload under the same protected
backup, transaction, projection-hash, and journal boundary. Project export
format v13 carries v2 manuscripts, stable visual references, project named
paragraph/character style definitions, publication editions, and complete
project font binaries. The v8-v12 structured adapters and v1-v7 text adapters
exist only at the import boundary.
The manuscript migration owner targets its historical EF schema only when that
schema migration itself is pending; a later unrelated EF migration never causes
the startup orchestrator to downgrade current application tables. This preserves
edition, render, artifact, and chat rows while later forward migrations run.

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
recording its journal. A failure delegates to the shared database recovery
service and opens only the projectless recovery shell rather than permitting
edits against a partially cut-over database.

Manuscript v2 stores structural heading level separately from edition-independent
style role, so assigning or removing a named paragraph style cannot change a
chapter heading into a subheading. Figure blocks own a project image ID,
alternative text, and caption content. Manuscript saves and assistant previews
validate image ownership; image deletion refuses live figure references; v13
imports preflight and remap figure asset IDs. Current Markdown and EPUB
publication projections consume the structured manuscript rather than flattening
these blocks and marks through the plain-text projection.

The chapter editor is an exact-pinned ProseMirror bundle built from
`tools/semantic-editor/package-lock.json`. Browser transactions are adapted to
Lorekeeper manuscript JSON and cross `IManuscriptService.ReplaceDocumentAsync`
with an expected revision; neither DOM nor HTML is persisted. Paste is
constrained by the owned schema and reports removed elements. Manual edits and
Editor/revision-worker tools share block, mark, style, validation, and
structural-inspection semantics. Assistant operation conversion canonicalizes
known built-in role aliases and the visible `***` scene-break representation;
unknown custom roles still fail closed. GUID-backed stable block IDs compare by
identity across strict compact and hyphenated representations, with exact
ordinal matches taking precedence and ambiguous fallback matches failing
closed; mutation results return the exact stored ID. Named style semantic roles and
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
Codex refresh, callback replacement, and revoke operations are serialized per
provider across the application process and use fresh persisted-token reads. A
token endpoint `401`, or an `invalid_grant`/`invalid_token` rejection, marks
that exact persisted token unusable in memory and presents the account as
disconnected without deleting it; a new PKCE connection replaces it.
Transport, server, and malformed-response failures remain errors, while
Settings catches them at the provider-card boundary so one unavailable OAuth
service cannot break page rendering.

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
- Rust 1.97.1, pinned by `Lorekeeper.Press/rust-toolchain.toml`, for source builds

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

The owned Press project runs an independent black-box conformance suite plus
unit and adversarial fixtures:

```powershell
cd Lorekeeper.Press
cargo fmt --check
cargo clippy --all-targets -- -D warnings
cargo test --locked
```

`dotnet build Lorekeeper.sln` also builds and packages the same locked native
runtime used by the app. `LorekeeperPressProcessIntegrationTests` stages a real
protocol-v3 job through the C# runtime boundary with `PATH` removed and verifies
the generated interior and cover bytes.

Successful compilation does not validate OAuth, provider calls,
embeddings, web search, image generation, publication output, packaging,
automatic updates, or OS-specific Electron behavior. Exercise the relevant
integration on the relevant platform before claiming it works, and report
anything not exercised.
