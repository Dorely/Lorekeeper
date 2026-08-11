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
and contest workflows, project image generation and editing, semantic Figures,
Designed Page and cover composition, import/export, TXT, Markdown, accessible
EPUB, and immutable Lorekeeper-validated print and tagged Digital PDF
generation/view/download. `VISION.md` remains the product direction and is not
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
For Editor and Editor Revision turns with an active chapter, `ContextBuilder`
loads one current `ManuscriptSnapshot` and includes a compact complete editing
surface: the current revision and source hash, every stable manuscript block ID,
semantic text, inline marks, style roles, direct paragraph overrides, Figure
metadata, and Designed Page references. A protected Book Text Styles context
item supplies named-style IDs, revisions, semantic roles, definitions, active
role counts, and compact formatting exceptions. Normal active-chapter semantic
edits therefore use the prompt's exact revision and block IDs without an
initial `read_chapter` or `read_manuscript` call; those reads remain the
fallback for missing/stale/non-active state, post-mutation verification, and
physical page geometry.
Automatic entity context uses a separate compact projection that retains
meaningful entity data and minimal relationship identities while omitting empty
fields and internal edge/provenance metadata. Detailed entity and link tools
remain explicitly paginated and lossless for follow-up graph inspection.
The chapter header's word/token metric counts only the chapter's plain-text
projection, while Assistant Memory counts the enabled assembled context
including instructions, labels, references, and structured editing metadata;
these are intentionally different scopes and are not normalized to one value.

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
After a complete tool-call batch returns, the six interactive chat surfaces use the
active model's configured `ChatTokens` input limit and existing token counter
(including its character-estimate fallback). At or above 90%, the active in-memory
context drops tool-result messages, related function-call protocol entries, and
messages marked as tool-derived visual context, while preserving system messages,
user content, assistant prose, and initial visual context. A runtime-only notice
instructs the assistant not to assume it still knows IDs or other details and to look
them up again. The operation never interrupts streamed output or an active tool call.
The live chat header follows that same boundary: completed tool chips remain visible
for transcript audit, but chips dropped from the active request are excluded from the
in-progress token estimate and the runtime notice is included until the turn ends.
The completed operation is recorded as a synthetic `Chat Compacted` tool row and
manifest entry for the transcript UI; the original tool rows remain audit history,
and no new persistence schema is required. Background ingest, revision, contest, and
other worker loops do not use this policy.
Feature adapters must accept any valid JSON shape returned by a read-only tool.
Post-tool mutation projection inspects only object envelopes that can carry a
mutation notice; array or scalar read results continue the turn without a
workspace refresh rather than being treated as malformed mutations.
`ChatTurnRuntime` and the singleton feature turn runners keep active turns alive
across component disposal, buffer updates for reopened panels, and preserve
explicit Stop as the cancellation path. Do not move active-turn ownership into a
single Razor component or circuit. Surface-scoped maintenance leases make
conversation reset atomic against active or newly starting turns across windows.
`ChatSurface` also stores unsent composer text in browser/Electron local storage,
keyed by project and the six-value `ChatTurnSurface`. Drafts therefore survive
surface remounts, project-tab navigation, and page/circuit reloads without leaking
between projects or assistant roles, and are removed when the message is sent.
This unencrypted browser-local text is outside SQLite, backup, and project export;
clearing site data removes it. Temporary composer image selections are not part of
the draft contract, although pasted/uploaded images remain normal project assets.

Editor Chat is one project conversation, not a chapter conversation. Its component
is keyed only by project. The Editor chapter picker flushes the outgoing manuscript,
loads the selected chapter inside the mounted `EditorContent`, synchronizes the page
parameter through a non-rendering delegate, and pushes the canonical chapter URL
through its browser module without starting routed/enhanced navigation or scheduling
a parent render. The chat subtree and JavaScript-sized Editor grid therefore stay
mounted without transcript hydration or default-width flashes for an ordinary
chapter switch. Direct chapter URLs remain routable entry points. The next Editor
turn always receives the selected chapter and rebuilds authoritative context in
`EditorChatService`; explicit context, review, or edition-target changes may still
refresh the panel's advisory token state.

Assistant tool mutations that require review are stored as `AiChange` batches
and applied through `IAiChangeApprovalService`. Editor Contest Mode captures a
terminal context snapshot and stores independent model candidates. Its Review
workspace opens as soon as a batch enters `Running`, renders pending candidates,
and stays reachable from both the chat controls and the chapter mode bar. The
Editor chat component remains mounted while its pane is visually collapsed for
Contest Review, preserving the active-turn subscription that delivers candidate
status updates through completion. Editor revision agents persist one worker
session per assigned chapter and return only compact IDs, statuses, summaries,
errors, and pending-change IDs to the coordinating Editor turn. Full
instructions, operations, proposals, raw responses, and transcripts remain in
durable session detail for the review UI and never enter the parent model
result. When Review edits is enabled, each worker-created pending manuscript
change is correlated to its parent tool call and adopted into the active Editor
turn's manuscript overlay, so subsequent reads and pending-change notifications
use the projected document while the persisted chapter remains unchanged until
approval. The coordinator cancels and awaits any pending progress-channel read
before disposing its async enumerator, so a terminal job cannot end the parent
turn with a concurrent-disposal `NotSupportedException`. Tool contracts,
prompts, persistence, review UI, and approval behavior must evolve together.

Outline Chat is a structural-planning and canon surface. Its normal system
prompt includes Project Guidance, the Book Brief, and structure-only
genre/audience guidance; publication releases are not queried or injected.
Outline can mutate the Book Brief, acts, chapters, synopses, beats, entities,
links, and project facts, and can search/read project sources. Chapter-body
reads are available only for explicitly relevant reconciliation or inference,
but Outline has no manuscript, Figure, Designed Page, composition, page-setup,
or geometry-target mutation tools. Desired illustrations and spatial treatment
remain synopsis/beat planning notes until Editor performs the authoring work.
Canonical-appearance generation is the sole Outline image workflow: it produces
an unattached project image without geometry, then a separate entity-reference
tool attaches the inspected result. Publication-aware book-format guidance is
available only through an explicit on-demand scope.

`OutlineCollaborationTools` owns a shared structural/canon catalog and exposes
intentional surface subsets rather than making Editor filter the entire Outline
surface. Editor retains full outline/entity mutation access alongside its own
manuscript and composition catalog, including
`insert_manuscript_designed_page`. Outline's `delete_chapter` tool rejects a
chapter containing semantic text, a scene break, Figure, Designed Page, or any
other meaningful manuscript block with `CHAPTER_HAS_MANUSCRIPT`; Editor's tool
surface and the manual UI continue to use the unrestricted chapter service.

`AiChangeRepository` keeps pending-review reads no-tracking, but its mutation
methods update only the root `AiChange` or `AiChangeBatch` row. They reuse a
locally tracked instance when one exists and copy scalar values onto it;
otherwise they mark only the supplied root entry modified. Detached
`Batch.Changes` graphs are never attached during review updates, which keeps
long-lived Blazor scopes from tracking two instances of the same change.

Editor review routing follows the fidelity of the proposed manuscript change.
Only chapters whose current and proposed manuscripts are plain body paragraphs
and scene breaks use the line-oriented Review tab. Figure, Designed Page,
semantic-style, inline-formatting, or other structured manuscript changes remain
in the pending-edits modal, which shows body text plus separate structure and
visual-block diffs. Focused Figure tools and Editor-owned Designed Page insertion obey the
same Review-edits staging boundary as the general manuscript preview/apply
protocol. A staged Designed Page preallocates its composition and manuscript
block IDs and retains its project authoring geometry and page/spread mode.
Keeping the change creates the manuscript block, composition, and exact
authoring variant in one transaction. Artwork is always an already-completed
project image placed by a separate revision-checked Figure or scene-object
mutation; generation never embeds a destination or creates a partial image
object. Staged visual reads resolve against the projected manuscript revision
until the user keeps or rejects the grouped change.

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

`IAgentProjectImageWorkflow` is the single assistant-facing generation/edit
boundary for Outline, Editor, Images, and Publish. It creates one unattached
project-image job, registers its durable job ID, waits to a readable terminal
state, cancels timed-out or interrupted work, and returns compact status,
project-image IDs, diagnostics, and temporary visual context. Layout-bound
descriptors select a deterministic, moderate raster that matches the exact
target aspect (about 1.57 MP within GPT Image 2's flexible-size limits) and add
physical/protected-region prompt guidance. Returned pixels are stored without
cropping, resizing, or rejection: a provider dimension mismatch is returned
visually with `geometryMatched=false` and a
`LAYOUT_IMAGE_GEOMETRY_MISMATCH` warning so the assistant can inspect,
regenerate, or deliberately fit it. Outline
requests omit geometry and are restricted by prompt and tool contract to
explicit canonical entity-appearance work. Focused Figure, Designed Page,
cover, and canonical-reference tools consume a completed
image ID in a subsequent tool round; revision conflicts never discard or
regenerate that library asset. Job read/wait/cancel tools reconnect without
replaying the original prompt, and persisted chat never stores image bytes or
repeated generation payloads.

Entity visual examples are ordered associations between graph entities and
project images. Image prompting, editor context, research/ingest promotion, and
entity indexing share these associations as canonical visual evidence. Changes
to visual ownership or reference semantics must be traced through all of those
consumers.

Chapters persist one format-neutral manuscript-v4 JSON document plus a monotonic
revision. The document is a sequence of semantic text blocks, Figure blocks,
and Designed Page references; there is no chapter-level visual classification.
Figures own stable IDs, project images, captions, alternative/decorative
decisions, language and semantic roles, plus flow, wrap, width, spacing,
contain/cover fit, internal crop position, bleed, page-break, and
caption-placement intent. Paragraph Book Text Styles can define reusable
typography, alignment, whole/right/first-line/hanging indentation, spacing,
keep behavior, and page starts. Text-bearing blocks can carry sparse overrides
of those properties, including font family, font size, weight, italics, small
caps, line height, alignment, indentation, spacing, and pagination. Direct
overrides take precedence over Book Text Styles and built-in
defaults. The semantic editor, EPUB formatter, chapter preview, and publication
PDF renderer resolve the same style properties.
Editor and revision-agent context includes the named-style catalog and the
active manuscript's style roles, inline marks, and sparse direct formatting so
ordinary edits can use the supplied structure without first rereading the
chapter. Full manuscript and style tools remain available for stale snapshots,
non-active chapters, and physical layout inspection.
The primary editor toolbar loads the project font catalog and exposes direct
font-family, point-size, line-spacing, emphasis, alignment, indentation, list,
and link controls in a compact document-editor layout. Bundled and imported font
faces are loaded into the browser from their project-owned URLs; Read mode and
publication requests stage the same referenced faces rather than substituting a
machine font. Imported-font deletion is blocked while a paragraph, saved style,
page, or cover still references the family.
The primary editor toolbar keeps a saved-style picker beside focused apply-to-
paragraph and apply-to-chapter actions. Saving the current paragraph as a style
flushes it first, extracts inherited style properties plus sparse presentation
overrides server-side, creates one reusable definition, and applies its stable
role back to that paragraph. Editor assistants use the same extraction policy
and a compact style-ID application tool; chapter-wide styling never requires a
model to emit or receive one operation per block. Applying a saved style clears
direct paragraph presentation while preserving inline content marks.
`IManuscriptService` is the only runtime manuscript write boundary. Its EF
optimistic-concurrency token and the project-scoped in-process/file mutation
lease prevent simultaneous editors, imports, image deletion, and assistants
from overwriting or introducing dangling references. ProseMirror, assistants,
contests, revision agents, approvals, indexing, composition, and publishing all
consume the same document or a derived projection; direct body-string and
chapter-visual persistence are not runtime paths.
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
`IProjectPageSetupService` owns each project's authoring width, height, margins,
body typography, preset selection, and revision. New projects start at 6 x 9 in.
This setup drives chapter preview, Figures, new Designed Pages, and authoring-time
generation without creating or selecting a publication release. Core Book also
edits this shared setup directly. A geometry change reflows the reusable Core
cover and every active Designed Page authoring scene in the same project-scoped
transaction. Reflow preserves objects and bindings, maps the authored layout
without stretching it, and advances the affected revisions so preview and
preparation cannot pair new page boxes with old-sized visual scenes.
`ICompositionService` owns Designed Page semantic fragments exactly once and
stores revisioned `PageCompositionVariant` scenes by exact geometry
fingerprint. Text objects bind stable block/range IDs in that fragment, so
visual layout never duplicates searchable or accessible content. The shared
authoring surface treats that reference as an internal ownership detail: every
new text frame creates its own semantic block, authors edit it directly in the
canvas, and selection-aware formatting writes ordinary `ManuscriptInline`
marks. It does not expose reusable-content binding, block IDs, or character
offset controls. Older range-based layouts remain losslessly readable and are
materialized into frame-owned blocks when directly edited.
The shared
scene vocabulary covers image, text, rectangle, ellipse, line, and group
objects; z-order, per-object locking and visibility, grouping, object styles,
named regions, and logical reading order remain independent. Persisted scenes
may retain internal stacking planes for renderer and migration compatibility,
but authors do not manage layers. Each composition records
an active authoring variant. Active authoring pages always use the current
project setup, retaining only their single-page or facing-spread layout mode;
the Pages workspace exposes no independent physical-dimension controls. Opening
an older mismatched authoring variant repairs it to the current setup, while a
project setup change reflows active layouts transactionally. Publish reports
edition compatibility and can copy the authoring scene into a separate exact
edition variant for review without making a publication release govern
authoring geometry.
Pages, covers, and assistant tools share one image-frame layout contract.
`Fill canvas` always sets the frame to the complete surface; proportional mode
uses crop-to-fill while non-proportional mode explicitly stretches. Bounded
assistant composition reads report whether each returned image frame covers the
canvas and describe its fit behavior without requiring scene-coordinate inference.
The chapter center column has Edit, Read, Pages, and Review modes. A chapter
whose manuscript contains only Designed Page blocks initially opens in Pages;
after the author chooses a mode, that chapter's last mode is restored across
Editor navigation and reloads. Read's single/facing choice and the Chat/Memory
column widths and collapsed states are retained as browser-local, per-project
workspace preferences rather than project publication data. Read flushes
pending manuscript edits and uses `IChapterPreviewService` plus the Press layout
command to render the current chapter as clean page canvases with real line
breaks, images, captions, Designed Pages, recto parity, labels, links, and boxes.
The preview request contains only the selected chapter and asks Press for its
compact browser trace, which preserves paint and text-run data but omits
glyph-level evidence used only by renderer conformance inspection. Designed Page
line fitting uses the declared font's shaped widths and preserves authored hard
line breaks, so the canvas and Press preview share the same frame capacity.
Preview work is cancellable on mode changes and produces a retryable timeout instead of
leaving the UI indefinitely busy. Designed Page text that exceeds its authored
frame is clipped in this authoring trace and returned as a visible warning;
publication renders retain strict overflow rejection. It supports single/facing display,
fit-page/fit-width, zoom, and project page setup; Fit width is the initial
display setting. Facing mode groups physical even/odd leaves into fixed
two-page spread rows, including a hidden parity placeholder when the selected
chapter begins or ends on an unpaired leaf. Workspace width changes therefore
never place a third leaf in the same row. It never substitutes a
read-only ProseMirror view. A Designed Page whose scene declares a facing
`EditionLeaves` spread always becomes two sequential leaf pages, including in
Digital PDF preview requests that permit independent page geometry. Facing
display joins those leaves into one row by default and offers an optional
visible seam without changing the document layout. The browser trace carries
each split image's source window through `IChapterPreviewService`; Read mode
clips one virtual spread image across both leaf frames instead of independently
fitting the complete source into each page. Figure and Designed Page
insertion use editor-owned forms rather than native browser prompts so the
workflows behave consistently in the Electron host. Clicking Figure artwork
selects the Figure directly; the primary toolbar and selected-Figure inspector
share a sticky editor header,
keeping the styled Figure controls visible while the manuscript scrolls.

Editor Chat's `preview_chapter_page` reuses the same Press layout request and
accepts either a stable manuscript block ID or a 1-based chapter-local typeset
page number. When Review edits is enabled, the tool supplies the projected
manuscript and staged Book Text Style catalog to Press, so the image reflects
the pending result rather than the persisted document. Press remains the sole
pagination and shaping authority; the app only rasterizes the returned paint
order, text runs, images, and shapes into a bounded PNG for visual inspection.
The PNG is persisted through the existing `EditorMessageVisual` byte boundary
and is also sent as model-only visual context when the active provider supports
vision. A block spanning pages resolves to its first Press-mapped page, while
the result reports both chapter-local and underlying Press physical page
numbers. `Publishing:Press:PreviewImageMaxEdge` bounds the generated image and
defaults to 1600 pixels. Missing fonts/assets, invalid targets, Press failures,
and unavailable vision are returned as recoverable tool state rather than
being treated as successful visual verification.

`ICompositionCanvasPreviewService` is the direct authoring-canvas inspection
boundary shared by Designed Pages and publication covers. It renders the exact
selected scene revision as one transient PNG surface, including both leaves of
a facing spread, using the visual editor's visibility, z-order, image
fit/crop/stretch, text and inline formatting, fonts, wrapping, alignment,
rotation, opacity, shapes, grouping, clipping, and page bounds. Annotated mode
adds safe/trim/gutter/object identifiers plus overflow and clipping indicators;
clean mode returns only the composed artwork. Cache keys include scene and
semantic revisions plus referenced image and font bytes. Editor's
`preview_page_canvas` and Publish's `preview_publication_cover_canvas` deliver
temporary vision context and never create project images. Assistants use
annotated previews during mutation and clean previews for final verification;
`preview_chapter_page` remains the Press pagination/typesetting inspection tool
for flowing manuscript output.

Advanced controls are bounded and positioned below the complete sticky header
and within the editor viewport, so they neither cover Figure controls nor create
horizontal overflow.
Preview canvases receive server-computed, valid CSS
lengths for their selected fit and zoom, and participate in normal flex/grid
flow so every Press page occupies a distinct canvas. When composition creation
advances the manuscript outside ordinary editor autosave, Pages navigation
reconciles the parent chapter snapshot before evaluating its available page
IDs. A Designed Page atom
shows its name, mode, status, and artwork preview, and selecting it opens the
contextual Pages mode in the same center column. The workspace provides direct
pointer move/resize/rotate, atomic image placement with required fit, crop
repositioning, direct canvas text editing, selection-aware inline formatting,
  styles, reading order, computed
  trim/safe/gutter/center/bleed overlays, diagnostics, serialized autosave, and
  undo/redo. Pages and covers share `CompositionVisualEditorShell`: a one-line
  view toolbar sits above the largest practical canvas, while creation, object
  selection, fit, typography, stacking, history, and zoom form a fixed bottom
  control strip. Accessibility, semantic roles, exact geometry,
reading order, grouping, saved variants, and other secondary
settings live in an independently scrolling details drawer that overlays the
canvas only while requested. No permanent inspector or author-facing layer
  manager consumes canvas width. New images enter behind content but immediately
  above older images, while new text and shapes enter at the front. Front/back
  controls move an object to the actual edge of its applicable stack rather than
  changing its depth by one. Rectangle, ellipse, and line tools remain a
compact secondary construction menu; custom guides and SVG/path tooling are not
  runtime capabilities. Page and cover changes schedule autosave without an
  explicit Save or Back action, and saves are serialized per mounted workspace. Each save uses
an immutable scene/semantic snapshot, adopts the returned revisions before the
next queued save, and clears the dirty state only when no newer local mutation
occurred while persistence was in flight.
Crop repositioning is an explicit selected-image mode shared by Pages and Cover:
dragging and arrow keys adjust only the image position inside its fixed frame,
resize/rotate handles are unavailable, and the mode remains active until the
author finishes it or selects another object.
Image frames retain the loaded raster's physical aspect ratio by default. The
shared pointer bridge reports native image geometry at transform start, so both
Pages and Cover constrain drag resizing without guessing from a generation
request.
Disabling the constraint stores an explicit stretched fit. `Fill canvas` either
centers the largest proportional frame or occupies the complete surface when
stretching is enabled. Rulers sit outside the page and selected handles remain
reachable beyond the surface; Press clips out-of-surface image paint to the PDF
page instead of rejecting otherwise valid image geometry.

Assistant composition mutation notices carry the chapter,
composition/variant, revision, changed IDs, and selected object. A newly
created Designed Page opens Pages mode automatically, and each later placement
or layout mutation reloads the mounted canvas and follows the affected object
without replacing dirty manual state. Authoring-variant refreshes use fresh
no-tracking reads so mutations performed by a background assistant scope cannot
be hidden by an older variant already tracked in the Blazor circuit.

`LayoutGenerationTargetDescriptor` is the server-owned geometry boundary for a
project page, Figure, page surface/frame, or publication cover surface/frame.
Only release-cover targets are edition-owned. It carries
the exact physical aspect, final-output raster recommendation, deterministic
moderate authoring raster, effective-DPI expectation, geometry fingerprint,
and named trim, bleed, safe, gutter, cover,
barcode, and reserved-text regions. It is optional composition guidance rather
than an image-acceptance constraint: free-standing generation is the default for
reusable art and flowing Figures, while a concrete target is used when the art
must honor physical regions. UI and assistants provide only stable target IDs
for that mode. Provider raster geometry and pixels are stored without layout
cropping, resizing, or mismatch rejection (supported format normalization may convert WebP to
lossless PNG); Figure and scene contain/cover/crop-position settings fit any source
aspect ratio non-destructively at layout and render time. Free-standing
image-library generation remains manually sized, and its size/aspect audit
metadata is never classified as a physical layout target.
Provider output validation has a separate configurable 64 MiB default byte
boundary rather than inheriting the smaller user-upload limit, so a valid
high-detail generated raster is not rejected after provider completion.
`IPublicationBookService` owns the one-to-one revisioned Core Book: shared
metadata, content and reading order, title/contents and heading presentation,
semantic front/back matter, opening/ending placements, and a reusable front
cover. Project page setup and Book Text Styles remain the Core geometry and
typography owners. Core Book exists even when the project has no publication
release and can produce only a private `ReadingPdf`, never a publication package
or ISBN claim. Chapter rows are the selectable publication content. Acts remain
structural groups: the act-heading and act-summary settings alone determine
whether their divider presentation is emitted, while act-targeted illustration
placements remain valid independently of that presentation.
Core Book also owns Digital PDF presentation defaults. Preserving Designed Page
sizes keeps a facing composition as one wide PDF page and retains intentional
independent page geometry; otherwise facing compositions are emitted as two
regular book leaves. PDF ebook releases inherit this value live and may store a
sparse override. Tagged structure, bookmarks, links, document language, and
logical reading order remain mandatory output rather than optional switches.
`IPublicationEditionService` owns optional paperback, EPUB ebook, and PDF ebook
release aggregates. Releases retain destination, internal immutable profile,
ISBN, product settings, status, artifacts, packages, sparse field and collection
overrides, cloning, archival, comparison, and audit history. Proof tracking,
proof artifacts, and proof gates are not part of the publication runtime.
`IPublicationEffectiveConfigurationResolver` combines Core with explicit
overrides at read/render time. Absence means inherit, optional text can be
explicitly empty, and reset removes the override. Core collection additions
flow into releases unless excluded. Core mutations stale only releases whose
effective source fingerprint changes. Digital PDF defaults to uniform release
geometry and can explicitly preserve wide or independent Designed Page boxes;
paperback leaves are always uniform.
Releases may opt into edition-specific manuscript content. `EditorContentTarget`
identifies either Core or one enabled release, and every manuscript, review,
contest, revision-worker, Figure, composition, preview, search, and assistant
mutation carries that protected target. Chapters use copy-on-write inheritance:
an untouched edition chapter reads current Core live, while its first text or
layout mutation snapshots the complete chapter, records the Core revision/hash,
and clones referenced Designed Page compositions with source identities and
stable scene relationships. Later Core edits do not alter that snapshot.
Resetting a chapter removes only its edition manuscript/compositions and returns
to live Core inheritance. Project images and Book Text Styles remain shared
project resources and are never deleted by a reset, release discard, or release
deletion. Shared-style edits report their Core/release usage count and affect
every actual reference. The Editor assistant is locked to the selected target;
edition mode omits outline/canon mutations and cannot update or delete an
existing shared style, but may create and apply a reusable copy. Publish reads
bounded stable-block differences and layout diagnostics and links into the exact
Editor target; it cannot mutate edition manuscript or page layout.
Core reading-copy fingerprints include Book Text Style definitions and the
project font catalog, so typography changes stale an existing reading PDF just
as they stale release artifacts.
The Core Book workspace places page setup, Book Text Styles, and its reusable
front cover immediately after reading-copy readiness. Page defaults are edited
inline through the shared page-setup service, while Editor and Publish reuse one
Book Text Styles modal and manager. Publish-assistant tools expose the same
revision-safe page-setup and style mutations with compact refresh notices.
`IPublicationReleasePresetService` creates releases from only product type and,
for paperback, destination. Application-owned profile versions and bleed policy
are not normal UI or assistant inputs. Publication profiles have two durable
classes. A Generic profile supplies safe configurable output for an unknown
vendor or custom purpose and carries no named-vendor claim. A Specific profile
is a built-in, versioned contract for a named vendor and product; KDP and Ingram
are the current Specific paperback profiles, and future destinations such as
Google Books follow the same source/review-date/profile-version boundary.
`IPublicationPreparationService` owns
persisted reconnectable one-action jobs: Core compiles/renders/validates its
reading copy; paperback renders and packages interior/full-wrap files; EPUB
exports, structurally validates, and packages; PDF ebook renders and packages
one cover-plus-book PDF. It reuses current artifacts and returns plain-language
blocking actions. A Core reading copy may complete while image alternative-text
or decorative decisions remain pending; those images are omitted from the
copy's tagged reading order and the preparation retains visible warnings.
Publication releases continue to fail closed on the same unresolved decisions.
`IPublishService` remains projection/export-only.
Publish documents carry display-ready numbered titles across Markdown, EPUB,
plain text, and Press. Protocol requests therefore disable Press-side title
numbering so a chapter or act prefix is emitted exactly once. Press-generated
Reading, interior, cover, and book PDFs are stale whenever the packaged renderer
version changes.
Archived releases are immutable at every owning mutation boundary; their
existing artifacts remain readable and exportable, and cloning creates the
editable continuation.
Project export v18 writes manuscript-v4 documents, project page setup, page
compositions and exact geometry variants with active authoring variants, Core
Book, sparse release overlays and cover scenes, Book Text Styles,
edition chapter snapshots and edition-owned compositions, visual references,
and project-owned font families/faces with binary hashes.
An isolated versioned transformer maps earlier visual structures into the
current model; earlier structured and text adapters remain import-only
boundaries.
A pre-v12 backup that references a
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

PDF output exists only through the contained press runtime. Paperback jobs
produce separate immutable interior and full-wrap cover PDFs; Digital PDF jobs
produce one immutable Book PDF whose front cover is page one, followed by
matter and manuscript content. The Publish workspace derives Generate,
active/cancel, Retry, Regenerate, and applicable Save actions from render and
artifact state. Save uses hash-verifying immutable endpoints and never invokes
browser print. EPUB editions cannot request Press output, and non-EPUB editions
cannot export EPUB, preventing product-form identifiers and metadata from
crossing formats.
Publish route initialization is progressive: Core metadata and the release
navigator form the blocking shell, while outline/matter/placement details,
preparation state, artifact freshness, and the project-aware assistant mount
after the first interactive render. Release selection follows the same staged
path, exposing editable effective settings before output history finishes
loading. Artifact list and render-status queries project metadata only; PDF,
EPUB, and package bytes remain behind the immutable view/download endpoints.
Once mounted, the Publish assistant remains connected while tool mutations
coalesce through one serialized target-refresh loop. Its prompt target switches
only after that Core/release refresh completes, preserving the active stream and
preventing overlapping reads on the circuit-scoped data boundary. The transcript
remains visible during that refresh while the composer is temporarily disabled.

`Lorekeeper.Press` owns protocol v5, deterministic layout, English/Latin shaping
and glyph diagnostics, custom project TTF/OTF staging and embedding, font
subsetting and ToUnicode maps, inline typography, bounded pagination, TOC
convergence, stable block/page maps, sparse paragraph presentation, flowing
Figures with wrapping, crop positioning,
bleed, and captions, structured Designed Pages and cover scenes, reusable
styles, proportional or explicitly stretched raster frames, page-clipped
out-of-surface artwork, vector shapes, logical reading order, page-size overrides for eligible
Digital PDFs, full-wrap cover geometry, EAN-13 bars, and PDF serialization.
Recto chapter starts apply to flowing-content chapters. Chapters whose meaningful
content consists only of Designed Pages remain a continuous leaf sequence, so
picture-book authoring containers do not introduce blank pages between designs.
Structured text remains text in the output rather than a rasterized page image.
Digital covers declare no barcode mode; print-only Lorekeeper and vendor-overlay
barcode modes are rejected for the Digital PDF profile. PDF structure,
annotation, and parent objects use disjoint page-scoped reference ranges so
long tagged books cannot reuse an indirect object ID across pages.
Group containers resolve into child geometry, rotation, opacity, visibility,
locks, and z-order in canvases, export, generation targets, and Press rather
than acting as editor-only metadata.
KDP and generic paperback profiles emit PDF 1.7. The Ingram profile emits PDF 1.3 with
PDF/X-1a:2001 identification, the registered CGATS21 CRPC1 CMYK output intent,
CMYK/gray-only resources, flattened raster alpha and non-overlapping scene opacity, no transparent PDF objects, embedded fonts, no encryption,
annotations, actions, or transparency, and image/page-paint colors capped at
240% total ink. A separate production `lopdf` pass reparses completed bytes before
atomic promotion. An independent black-box test harness parses raw PDF objects
without calling that validator. This is the evidence behind the scoped
“Lorekeeper validated” state; it is not evidence of vendor upload acceptance.

Protocol v5 stages `input/request.json` plus declared PNG/JPEG assets and
approved project TTF/OTF fonts in a bounded job root. Declarations carry media
type, byte length, dimensions where applicable, rights state, and SHA-256.
Render requests explicitly identify `outputPurpose` as `publication` or
`reading-copy`; the latter is accepted only by the generic Digital PDF profile
used for Core Book and cannot weaken a publication profile.
The `layout` command defaults to its full glyph-evidence trace for conformance
work; app previews explicitly request `layoutTraceMode: browser-preview`, which
retains page paint order and typographic runs while omitting unused glyph arrays.
That authoring trace renders images whose accessibility decision is still
pending and clips composition text at the authored frame, returning visible
warnings instead of rejecting the chapter preview. Core `reading-copy` renders
tolerate pending accessibility decisions but retain strict text-overflow
validation, as do publication renders. Core reading copies treat unresolved
images as artifacts in that private copy so the tagged PDF remains structurally
valid. Publication renders reject the scene until every meaningful image has
alternative text or is deliberately marked decorative.
Absolute paths, traversal, links/reparse points, undeclared or changed bytes,
corrupt assets, restricted/unsupported fonts, existing output, and cancellation
fail before promotion. The renderer writes a fresh staging directory,
independently validates every PDF, and atomically renames it to `output` only
after all checks succeed; it never overwrites prior output.

Composition-object opacity is preserved with bounded graphics states in Digital
PDF and KDP PDF 1.7. Ingram PDF/X-1a deterministically flattens opacity against
the page or cover substrate. A translucent object that overlaps lower page art
is rejected by shared profile validation with the exact object IDs because
flattening that stack would otherwise change its appearance or rasterize
selectable semantic text and vector content; the author can make it opaque or
precompose the overlapping artwork as one image. Raster source alpha is
flattened during the owned image-normalization path. Geometry-bound generation is always persisted as PNG,
while free-standing library generation may also use WebP.

The structured-manuscript, publication-edition, and owned press-runtime
boundaries are implemented. `PublicationRenderService` persists edition-scoped
queue state, immutable artifact bytes/hashes, renderer/profile provenance,
diagnostics, and stable block-to-page mappings. Render request assembly resolves
font families from the effective manuscript, Designed Page, and cover scenes;
this includes an unsaved seeded cover scene, so every referenced bundled font
is staged even when that scene has not yet been persisted. `PublicationRenderWorker`
recovers interrupted jobs and owns cancellation; `PublicationRenderProcessor`
contains the child process, while `PublicationPressRuntime` resolves only a
relative directory beneath the stable binary installation root
(`AppContext.BaseDirectory`), independent of the launch working/content root,
rejects reparse/missing files, verifies the current platform/architecture
manifest, exact file inventory, sizes, and SHA-256 fingerprints, and constructs
a cleared environment. There is no inherited `PATH`, Cargo, Python, uv, Typst,
WeasyPrint, Chromium, machine PDF software, or repository fallback. Runtime
readiness and the dynamic `describe` contract are checked by the
UI, assistant, and service before a render can be queued. The processor captures
bounded stdout/stderr, bounds its lifetime and paths,
and verifies every returned length/hash before persistence. Protocol-v5
requests are serialized as BOM-free UTF-8 JSON; the owned renderer also
tolerates an optional UTF-8 BOM for compatibility and binds every post-parse
terminal response to the parsed job identity before the app accepts its
diagnostics or artifacts. Redirected renderer stdout and stderr are decoded
explicitly as UTF-8 on every platform so punctuation and non-ASCII Latin text
survive layout traces and diagnostics independently of the host console code
page. Project-scoped range endpoints serve actual
PDF/package bytes only after recomputing
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

`PublicationBookService` owns the Core front-cover scene. EPUB and PDF ebook
releases inherit it until customized; paperback releases project it into the
front panel while retaining release-owned spine, back, and barcode regions.
Existing release covers and **Customize front** create explicit local
overrides; inherited Core objects remain protected until the release front is
customized.
`PublicationCoverService` owns each release cover override and its shared
structured scene. Canonical title, subtitle, author, spine, and back copy remain
bindings, not duplicated frame text. Paperback geometry derives the
back/spine/front surface, bleed, safe zones, folds, and barcode reserve from the
current interior page count, trim, paper, vendor, and profile; Digital PDF and
EPUB use a front-only surface. Constraint-bound objects reflow when geometry
changes, while free-positioned objects retain their coordinates and surface
overflow. ISBN-13 validation is shared by UI, assistant, and render gating; the
renderer emits EAN-13 or the permitted KDP reserve, protects back copy, and
suppresses unsafe narrow-spine text. Ingram cover scenes use the owned ICC and
ink-limit path. Black-and-white editions convert interior raster and colored
text to DeviceGray; cover color remains independent and uses RGB for
KDP/generic or CMYK for Ingram.

Cover editing is an embedded Publish workspace mode rather than a modal. The
Publish assistant remains mounted in the left column. Pages and covers use the
same `CompositionVisualEditorShell`, fixed bottom contextual controls,
on-demand details drawer, structured `CompositionScene`, object/style
primitives, pointer/crop interaction module, undo/redo semantics, explicit
aspect-ratio retention with one-action canvas fitting, deliberate free stretching,
legacy crop placement, and revision-safe persistence. The cover host supplies
canonical copy bindings, background and barcode settings, print regions, and
format-specific geometry; those details do not create a separate editor UI.

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
product-form-specific package bytes. EPUB editions contain the normalized EPUB,
paperback editions contain only their validated interior/cover PDFs, and Digital
PDF editions contain one validated Book PDF, so product-form identifiers do not
cross artifacts. Digital PDF emits document language, bookmarks, internal TOC
links, selectable text, a structure tree, parent tree, MCIDs, logical block
elements, list parents, Figure/Caption relationships, headings, paragraphs,
alt text, decorative artifacts, and
logical reading order. This is implemented accessible output but is not a formal
PDF/UA certification claim. EPUB emits corresponding semantic XHTML plus
`schema:accessMode`, sufficient-mode, feature, hazard, and human-review summary
metadata; EPUB validation remains structural rather than a certification claim.
Every included ordered
semantic-matter document is projected into TXT, Markdown, EPUB, and contained
press output. The service persists SHA-256-addressed EPUB, front-cover, report,
manifest, and package artifacts. Package freshness combines
the source fingerprint, stable applicable input hashes/runtime provenance,
profile/rule version, assembler version, and the EPUB-exporter version only for
EPUB packages; exact PDF row IDs remain
an internal correlation snapshot and never leak into portable bytes. A short
serializable transaction rechecks the source immediately before each package
write, so a concurrent fingerprint-affecting mutation cannot be
mislabeled. Title, copyright, and visible contents pages are
generated exclusively from edition settings, so user-authored semantic matter
cannot claim those reserved kinds and duplicate generated output. EPUB
validation remains structural and internal; broader reader-matrix results are
outside the current runtime. Vendor upload and physical review are external user
activities and never alter the renderer's scoped structural result.

Publish assistant matter tools expose a dedicated user-authored-kind contract
that omits generated title, copyright, and contents pages. Expected matter
validation, missing-item, and revision failures return compact recovery results
inside the assistant turn; generated-page changes route through Core or release
settings.

ISBN values are strict, checksum-validated, and stored in canonical ISBN-13
form. The same ISBN may be shared only by same-format vendor editions whose
bibliographic metadata, visible content settings, physical product settings,
ordered outline, semantic matter, effective chapter content, referenced shared
styles, and image placements match.
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
reconnection, image attachments, and text-only cross-turn replay. Every turn
includes the complete ordered act/chapter outline, stable IDs, synopses, beats,
Book Brief, Project Guidance, and project facts. Bounded project search and
source/image reads provide chapter bodies, research, ingest sources, entities,
facts, and reusable artwork on demand. The two-column
Publish workspace flushes pending manual autosaves before each turn and refreshes
its selected Core/release target and artifact state from structured mutation notices. Manual
publishing mutations are locked for the duration of a Publish turn, render state
is re-read after autosave, and assistant preparation changes reconnect polling
and current artifact downloads.
`PublishAssistantTools` exposes compact Core/release reads and revision-safe
patches, sparse content/matter/placement/cover operations, readiness,
preparation, cancellation, and artifact metadata. Tool results contain changed
IDs/fields, revisions, prioritized diagnostic counts, and refresh notices rather
than complete unchanged records. Raw profile selection and low-level
render/preflight/package orchestration are not assistant capabilities. ISBN
invention remains unavailable. Outline, Editor, Images, and
Publish share compact paginated reads and revision-aware mutations. Large scene
payloads are persisted once as project/conversation-scoped, hashed, expiring,
non-replayable stages; preview returns a stage ID and compact diagnostics, and
apply accepts only that ID plus expected revision. Tool history stores compact
summaries rather than image bytes, rendered manuscripts, or complete unchanged
scenes. Full cover-scene tools normalize supplied semantic reading order to
unique contiguous values, using object-array position as a deterministic
fallback, before strict scene validation. Expected scene and revision failures
return compact recovery results instead of escaping the assistant turn. Cover
scenes also normalize artwork beneath canonical cover copy at the shared service
boundary, so manual and assistant placement cannot obscure title, subtitle,
author, spine, or back-cover text through z-order changes. Runtime prompts
describe only the current format-neutral chapter, Figure, Designed Page, cover,
geometry, and publication-validation boundaries. Artifact results include
current/stale state and safe view/download URLs.

Preparation and cancellation tool results identify their nullable release
target at the result root. Their mutation notices refresh and reconnect polling
for either Core Book or the affected release, so a successful assistant retry
replaces a prior failure in the mounted Publish readiness card.

Editor turns default to completing direct in-scope requests with reasonable,
reversible choices. They enter proposal-only collaboration only when the user
asks to brainstorm, compare, recommend before acting, or decide together;
missing nonessential creative details do not create an extra permission gate.
Review-edits mode may still stage a completed mutation for the existing human
review workflow, but it does not make the assistant ask before using its tools.
Approval compares the staged and current semantic manuscript at the recorded
revision rather than requiring byte-identical JSON serialization. A genuine
apply conflict remains an unresolved, visible, rejectable review item with its
diagnostic; it never turns the batch into an apparently completed change.

`BookFormatGuidanceService` derives bounded, genre-aware recommendations from
the Book Brief, audience, reading level, read-aloud priority, visual direction,
accessibility goals, selected formats, and known geometry. Outline receives only
the relevant summary and can page deeper guidance explicitly. Recommendations
cover fiction, narrative/general nonfiction, picture books, illustrated books,
poetry, and hybrid work without turning conventions into chapter types or
inventing dimensions.

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

`IDatabaseStartupMigrationService` is the single ordered schema/data migration
orchestrator used by both the application host and installed-database migration
fixtures. Tests therefore exercise the same migration boundaries and recovery
checks as a normal application start instead of maintaining a parallel sequence.
It delegates the structured-manuscript cutover to `IManuscriptMigrationService`
before normal initialization. For a legacy
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
The current manuscript schema is v3. Startup safely upgrades older documents in
live chapters and every historical/review JSON payload under protected backup,
transaction, projection-hash, and journal boundaries. The schema-v3
legacy-style scan treats the JSON literal `null` as an absent audit snapshot and
permits legacy plain text only in `AiChanges.ResultJson`, where older tool
results predate that column's JSON contract; both representations are preserved
byte-for-byte. Malformed current manuscripts, structurally invalid manuscript
documents, and malformed non-result audit payloads fail closed into protected
recovery. The interrupted legacy transform is resumable only at its exact EF
schema handoff: a completed manuscript journal or any later applied migration
proves that invalid manuscript-shaped data is current corruption and must never
be reinterpreted as legacy prose.

The visual-composition cutover uses two forward EF boundaries: an additive
schema creates composition,
variant, staging, cover-scene, and Digital PDF fields; a guarded application
migration then transforms all chapters, covers, and applicable pending Outline
changes before applying the cleanup migration that removes visual-mode/layout
columns. It preserves Figure IDs and presentation, moves each page-layout
chapter's semantic blocks into exactly one composition document, preserves
frame IDs/bindings/geometry/typography/z-order/reading order, keeps unreferenced
text in an unplaced tray with a blocking diagnostic, maps layouts to leaf/spread
scenes, marks visual-only pending Outline changes `RequiresReplan`, and marks
existing artifacts `Legacy` without changing their bytes or hashes. Before
commit it validates every resulting variant or protected authoring seed,
including its SHA-256, semantic bindings, image ownership, and scene structure;
legacy text frames that contain neither literal nor semantic content are omitted.
It checks foreign keys and compares a
canonical hash of protected project, edition, asset, font, artifact, package,
audit, and page-map data while excluding only the explicitly transformed
fields. Any failed
validation enters the projectless recovery shell with the original protected
backup. Every converted Picture Page retains its original 8.5 × 11 inch leaf
geometry in a protected seed, including 17 × 11 inch `DoublePortrait` and
22 × 8.5 inch `DoubleLandscape` spreads, regardless of publication releases.
The later authoring-page cutover verifies and consumes every such seed,
materializes its exact single-page or facing-spread geometry, and selects it as
the active layout.
It adds one project-owned page setup, removes Figure
edition links and persisted workspace guides, renames stored crop coordinates,
and supplies `Contain` wherever old image fit is absent or unsupported. It seeds
appearance from an existing composition surface, then a default publication
geometry, then 6 x 9 in. Its protected migration validates semantic text and
hashes, restored scene hashes, scene/image ownership, mandatory active Designed
Page references, protected row-count deltas, artifacts,
packages and foreign keys before journaling success. Existing artifact
bytes and hashes stay unchanged and become Legacy. The same guarded service also
repairs a valid database from an interrupted/pre-release cutover when a completed
authoring journal still has orphaned Picture Page seeds; it never overwrites a
different non-empty layout for the same exact geometry.

The Core Book cutover uses an additive schema followed by the guarded
`PublicationCoreMigrationService` and a cleanup migration. For each project it
selects the former default release, then oldest release, then project/Book Brief
defaults as its source. It creates exactly one Core Book, moves equal shared
metadata/content/matter/placements into Core, converts every release to sparse
overrides while comparing its complete effective projection, and retains every
existing release cover as an explicit override. It generalizes jobs, artifacts,
page maps, and covers to Core or release target references and marks existing
artifacts Legacy without changing their bytes or hashes. The protected journal
also validates packages, audits, chat rows, row ownership, and foreign
keys. Only after successful validation does the cleanup remove the obsolete
default-release flag and duplicated-field runtime dependency. A mismatch opens
the projectless recovery shell with the original backup protected.

The edition-content cutover adds target metadata, release chapter snapshots,
and edition composition ownership before its protected application transform.
Legacy release typography and style mappings are materialized as reusable
project Book Text Styles referenced by edition snapshots, with equal effective
publication projections before and after conversion. It preserves non-proof
artifact bytes/hashes, covers, packages, audits, images, fonts, chats, and
settings; obsolete proof rows are intentionally removed. The cleanup migration
then removes proof tables/contracts, release typography columns, and publication
style mappings. Existing rendered artifacts become Legacy because effective
source fingerprinting now hashes inherited Core chapters or divergent edition
chapters and only the shared styles actually referenced by effective content.

Project export v18 contains only the current v4/page-setup/composition model,
Core Book, sparse release overlays, edition chapter snapshots, edition-owned
compositions, and target-aware publication records; older
formats remain importable only through isolated versioned transformers.
Human-readable language names from Book Briefs and publication inputs are
canonicalized to culture tags when new Core/release values are persisted and at
export, preview, preflight, and Press-request boundaries. Existing values such as
`English` therefore produce `en` without requiring a schema migration or manual
database repair. Publish presents the renderer's exact supported document-language
set (`en`, `en-US`, and `en-GB`) as selectors for Core Book and release overrides;
unsupported historical values remain visible until the user chooses a supported tag.
Startup applies later additive EF migrations only after the guarded Core Book
transformation and its cleanup migration succeed. This makes current Core-owned
tables available before runtime services execute without exposing those tables
to partial-schema migration code, preserving the protected historical cutover order.
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

Manuscript v4 stores structural heading level separately from edition-independent
style role, so assigning or removing a Book Text Style cannot change a
chapter heading into a subheading. Figure blocks own a project image ID,
alternative/decorative decisions, language/role, presentation, and caption
content. Designed Page blocks reference project-owned compositions. Manuscript
saves and assistant previews validate image and composition ownership; image
deletion refuses live Figure, scene, cover, and release-placement references; v16
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
closed; mutation results return the exact stored ID. Book Text Style semantic roles and
paragraph/character kinds are immutable stable keys; definitions are
revision-checked and semantic roles are unique per project and kind.
Assistant style alignment uses the same typed logical Start, Center, End, and
Justify values as direct paragraph formatting; the style service canonicalizes
logical and left/right aliases at its persistence boundary. Invalid style
definitions return compact recoverable tool results without terminating the
active Editor turn.
Project-image selection is a shared visual modal interaction throughout the UI.
Callers may filter supported media types and preselect the currently attached
image, but must not replace the visual library with filename dropdowns. Figure,
Designed Page, cover, chat-context, and entity-visual placement all return a
stable project-image ID through this boundary before their owning service applies
the requested attachment or replacement.
Release cover reads and every cover-scene mutation normalize a valid current-schema
scene onto the release's effective trim, bleed, and page-count-derived geometry
before validation. Constraint-bound objects retain their region-local layout and
page-bound objects retain their physical size, so a stale open workspace can save
after release geometry changes without trapping the user in a validation loop.
Core cover reads and mutations apply the same normalization against Project Page
Setup, including databases whose stored Core scene predates a page-size change.
Cover autosave validates structural integrity and asset ownership but persists
incomplete copy bindings, accessibility decisions, reading order, and layout
placement as editable draft state. Those conditions remain visible readiness
diagnostics and publication/render blockers rather than reasons to discard edits.
Cover-editor errors are dismissible overlays inside the fixed visual workspace and
never consume the canvas or bottom control area. Its top toolbar returns to the
selected Core Book or release after flushing the current autosave queue.
The keyed Blazor host rechecks disposal across asynchronous catalog loads, and
the JavaScript attach boundary rejects missing or detached elements before any
DOM mutation, so chapter switches or navigation cannot turn a stale element
reference into a circuit-ending initialization exception.

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
protocol-v5 job through the C# runtime boundary with `PATH` removed and verifies
the generated interior and cover bytes.

Successful compilation does not validate OAuth, provider calls,
embeddings, web search, image generation, publication output, packaging,
automatic updates, or OS-specific Electron behavior. Exercise the relevant
integration on the relevant platform before claiming it works, and report
anything not exercised.
