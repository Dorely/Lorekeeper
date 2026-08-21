# Manuscript and Authoring

## When to read

Read this chapter completely before changing the manuscript schema, chapter
body persistence, semantic operations, editor bridge or toolbar, Core/release
content targets, Book Text Styles, review annotations, process-lifetime Undo/Redo,
assistant manuscript staging, authoring migrations, or any projection that
turns semantic manuscript content into text or searchable/readable order.

Also read it when an image, composition, publishing, import/export, or assistant
change creates, rewrites, validates, indexes, previews, or deletes manuscript
references. Pair it with [composition-media.md](composition-media.md) for
Designed Page scenes and image assets, [publishing-model.md](publishing-model.md)
for release inheritance, and
[persistence-migrations-import.md](persistence-migrations-import.md) for guarded
schema/data upgrades.

## Scope and ownership

This chapter owns the current manuscript v4 document contract, target-aware
chapter content, semantic editing operations, the ProseMirror adapter boundary,
Book Text Styles and direct paragraph presentation, review annotations, and
in-process manual authoring history. It also owns the manuscript-facing side of Figure
and Designed Page references; image bytes and composition scenes belong to the
composition/media chapter.

`IManuscriptService` is the sole runtime chapter-manuscript write boundary.
Manual editing, assistants, review approval, contests, revision workers,
imports, indexing, composition, previews, and publishing consume that contract
or a derived projection. Direct body strings, HTML/DOM persistence, and
chapter-level visual-mode persistence are obsolete runtime models and must not
return.

## Current architecture and invariants

### Manuscript v4 is the current contract

Each Core or release-owned chapter persists one format-neutral manuscript v4
JSON document plus a monotonic revision. `ManuscriptDocument.CurrentSchemaVersion`
is `4`, and [`docs/schemas/manuscript-v4.schema.json`](../schemas/manuscript-v4.schema.json)
is the current interchange schema. The v1, v2, and v3 schemas and identifiers
remain only as immutable migration history and isolated versioned import inputs;
they are not current runtime compatibility paths.

A document has a stable manuscript ID, revision, and ordered semantic blocks.
Blocks have stable string IDs and one of the supported types: Paragraph,
Heading, SceneBreak, BlockQuote, ListItem, Figure, or DesignedPage. Heading level
is structural and separate from style role. Text-bearing blocks contain inline
text nodes with semantic marks; neither HTML nor browser DOM is authoritative.

`ManuscriptCodec` owns normalization, serialization, validation, hashing,
plain-text projection, deterministic migration IDs, and stable-ID reparsing.
GUID-backed stable IDs compare by identity across compact and hyphenated forms,
with exact ordinal matches taking priority and ambiguous normalized matches
failing closed. Mutation responses return the exact stored spelling. Unknown
custom semantic roles fail validation rather than being guessed.

`ManuscriptSchemaUpgrade` performs strict, lossless older-to-v4 upgrades for
live and nested historical payloads. Startup migration, import adapters, and
historical audit readers call that explicit boundary; normal runtime services
accept only current documents. Malformed current documents or non-result audit
payloads fail closed. Historical `AiChange.ResultJson` may retain legacy plain
text, and the literal JSON `null` may represent an absent historical snapshot;
those bytes remain audit history rather than a runtime editing route.

### Semantic blocks, Figures, and Designed Pages

Text blocks carry semantic style roles and optional sparse paragraph
presentation. Direct presentation can specify font family/size/weight, italics,
small caps, line height, alignment, whole/right/first-line/hanging indentation,
spacing, keep behavior, and page starts. Direct values override the named style
and built-in defaults. Inline marks remain distinct from paragraph
presentation.

Figures are flowing semantic blocks with stable IDs and a project image ID,
caption, alternative/decorative decision, language, accessibility role, and
format-neutral presentation. Presentation includes flow/placement, wrap,
relative width, alignment, spacing, contain/cover fit, crop position, bleed,
page-break intent, and caption placement. The manuscript stores a reference and
intent, not copied image bytes or a release-specific placement row.

Designed Page blocks contain stable references to project-owned compositions.
The composition owns its semantic fragment exactly once, and
`ChapterSemanticProjectionService` expands that fragment into chapter reading
order for plain text, context, search, and publication without duplicating
storage. Range resolution validates UTF-16 boundaries, surrogate pairs,
non-overlap, one-time placement, and unplaced semantic content. Scene geometry
and object editing remain under [composition-media.md](composition-media.md).

There is no chapter-level visual classification. A chapter can freely mix
semantic prose, Figures, and Designed Pages. Legacy Picture Page/layout records
are interpreted only by guarded migration/import DTOs. New code must reason from
the v4 block sequence.

### Core and release authoring targets

`EditorContentTarget` is the protected value identifying Core Book or one
enabled publication release. Every manuscript read/write, annotation, contest,
revision worker, Figure, Designed Page, context, preview, search, and assistant
mutation carries that target. Tool inputs and UI state must not permit a target
switch during an operation.

Core owns the canonical chapter. Release content is copy-on-write: an untouched
release chapter reads Core live, while its first manuscript or layout mutation
snapshots the complete current document, records the Core base revision/hash,
and clones referenced Designed Page compositions while preserving stable scene
relationships. Later Core edits do not rewrite that release snapshot. Reset
deletes only the release chapter snapshot and edition-owned compositions,
returning the chapter to live Core inheritance. Shared images and Book Text
Styles survive reset, discard, and release deletion.

`IChapterService` owns outline-level chapter lifecycle while `ChapterService`
implements the target-aware manuscript boundary. Replacing or applying a
document validates expected revision, stable structure, referenced project
images and compositions, then commits manuscript, plain-text/search projection,
history, graph/auto-mention, and index side effects through coordinated services.
Optimistic concurrency plus the project mutation lease prevents simultaneous
editors, assistants, imports, and image deletion from producing dangling or
lost references.

### Semantic operations and assistant apply

`ManuscriptOperations` is the canonical transactional transformation engine for
insert, replace, delete, move, split, merge, block-type, style, mark, paragraph
presentation, Figure, and Designed Page changes. `ManuscriptOperationInput`
defines assistant-safe DTO conversion and exposes the complete operation
vocabulary. Known built-in role aliases and the visible `***` scene-break form
are canonicalized. An inserted block may supply its stable ID so later
operations in the same atomic batch can target it.

Editor assistants never send a full projected document back to an apply call.
`EditorManuscriptApplyService` converts and validates one complete operation
batch, verifies the protected source revision, and writes the resulting document
through `IManuscriptService` or the Review Edits overlay in that same call. The
result reports compact changed IDs, counts, the committed hash when available,
and the committed or staged revision; no turn-local preview ID or projected
document is retained.

`ManuscriptInspection` supplies shared validation, normalization diagnostics,
and structural search to Editor and revision workers. Prompt/tool guidance and
operation conversion must remain aligned with this engine; assistants must not
infer undocumented JSON patches or persist the bounded read projection.

### Semantic editor and authoring UI

The chapter editor is an exact-pinned ProseMirror bundle built from
`tools/semantic-editor/package-lock.json`. Its owned schema/adapter converts
browser transactions to manuscript JSON and crosses
`IManuscriptService.ReplaceDocumentAsync` with an expected revision. Paste is
constrained to the owned schema and reports removed content. The DOM, HTML, and
ProseMirror local-history plugin are not persistence mechanisms.

`ChapterBodyEditor` is shared by Editor and Publish prose sections. It owns the
component/JavaScript bridge, visible-mode focus, stable selection restoration,
the persistent caret while focus is elsewhere, gap selection around non-text
blocks, revision-aware save/flush, annotation selection, Figure controls, and
caller-controlled Designed Page insertion. The editor surface fills its row and
only the owning surface scrolls when content exceeds available space.

The primary toolbar loads the project font catalog and provides direct font,
size, line-spacing, emphasis, alignment, indentation, list, link, paragraph,
and Figure controls. Project-image selection uses the shared visual library
modal; it must not regress to filename dropdowns or native prompts. Bundled and
imported fonts load from project-owned URLs and are staged identically for Read
preview and publication.

On revision conflict, the service returns the latest persisted document and
revision. The adapter adopts that authoritative snapshot, refreshes history,
and continues without a browser/Electron copy or conflict dialog. Out-of-order
autosaves similarly adopt the authoritative current result and cannot overwrite
newer state. JavaScript attachment validates that the target element still
exists, and asynchronous component work rechecks disposal so navigation cannot
turn stale element references into circuit-ending errors.

Editor has Edit, Read, Pages, and Review modes. A chapter containing only
Designed Pages initially opens Pages; after the user chooses, per-project
browser-local preferences restore its mode. Read flushes edits and asks the
Press-backed preview service to paginate the selected target with the effective
Core/release presentation. It is a private selected-chapter trace, not a full
publication render. Read annotation ranges map rendered text back to stable
semantic blocks without changing the Press protocol.

Edit, Read, and Review also share a transient `ManuscriptViewLocation` scoped to
the current project, chapter, and Core/release target; it is never persisted as
manuscript data or workspace preference. Edit records the semantic block ID,
selection offsets, and normalized progress so a collapsed caret or non-text
node can be restored exactly, with nearest-progress fallback after a document
change. Read records the selected endpoint when available, otherwise the
viewport-center semantic line, and restores only the preview scroll position.
Review lines expose exact block/source metadata when the projected line can be
derived from the manuscript and otherwise carry normalized progress; Review
restoration scrolls the viewport-center line without changing expanded review
blocks. Pages neither consumes nor replaces this location. Async capture and
restore must re-check the chapter/target identity, and failed saves leave the
previous location untouched.

### Book Text Styles and fonts

`ManuscriptStyleService` owns project-wide Book Text Styles. Each style has a
stable generated semantic key, paragraph or character kind, immutable semantic
identity, revision token, and JSON definition. Semantic roles are unique per
project and kind. Logical Start/Center/End/Justify alignment is canonical;
left/right aliases normalize at the service boundary.

Saving the current paragraph as a style first flushes it, extracts inherited
style properties plus sparse direct presentation through
`ManuscriptStyleTemplateExtractor`, creates a reusable definition, and applies
the stable role to that paragraph. The manual editor and Editor assistant share
that extraction and compact application policy. Applying a style clears direct
paragraph presentation while preserving inline marks. Chapter-wide application
is one focused service/tool operation rather than one model operation per block.

Imported fonts are shared project resources. Deletion is blocked while any
live paragraph, named style, Designed Page, or cover references the family. A
history-only font dependency requires an application-owned confirmation that
clears affected history in the same project mutation transaction. Release
content may create/apply a reusable style copy but cannot mutate or delete an
existing shared style from an edition-scoped Editor turn.

### Review annotations

`IManuscriptAnnotationService` owns single-author review highlights and notes as
sidecar rows scoped to project, chapter, and exact Core/release target. Anchors
store stable start/end block IDs, UTF-16 offsets, the selected quote, bounded
surrounding context, revision state, and timestamps. They remain outside the
manuscript document.

Every committed manuscript mutation rebases annotations for its target in the
same database operation. A Core mutation also rebases release annotations for
chapters still inheriting Core. Exact quote/context relocation must resolve to
one location; otherwise the annotation becomes Outdated rather than moving
ambiguously. Edit and Read share a collapsible top-stacked margin rail. Designed
Page content cannot be selected through the Read text-range bridge. Manual
completion permanently deletes the annotation immediately without a
confirmation dialog.

Annotations are excluded from Undo/Redo, indexing, plain-text projection,
publication fingerprints, Press requests, and artifacts. They are protected
assistant context. Tools can page open annotations and complete them, but not
create or rewrite them. Under Review Edits, completion is a dependent staged
change so rejecting the manuscript proposal preserves the annotation.

### In-process manual history and durable latest assistant review

The singleton `IAuthoringHistoryRuntime` owns process-lifetime Undo/Redo for Core
and release chapters, publication prose sections, complete Designed Page
aggregates, and Core/release covers. It has no EF or SQLite dependency. Each
target-isolated stream stores Brotli-fast compressed snapshots, retains at most
100 manual actions, and participates in a 128 MiB process-wide budget. Navigation
and page reloads retain streams while Lorekeeper is running; a full process exit
clears them. History is never exported.

The owning domain service first commits the live document with its next revision,
then records the successful manual before/after pair in memory. Undo and Redo
restore through that same domain boundary and move the in-memory cursor only
after the live commit succeeds. If the current live snapshot no longer matches
the cursor, the live document wins: the runtime clears the stale stream, adopts
the authoritative state, and treats the stale request as a no-op. New manual
work after Undo deletes the Redo branch.

Adjacent typing and IME activity within 500 ms coalesce into one action. Paste,
formatting, block conversion, Figure changes, and structural mutations force a
boundary. Selection anchors use stable block IDs and offsets with a nearest-valid
fallback. Removing a Designed Page detaches its composition instead of destroying
it; Undo can restore the original IDs and exact scene. At startup no in-memory
stream can retain detached data, so orphaned detached compositions and variants
are removed safely.

Direct assistant mutations are never Undo/Redo actions. After a successful
assistant commit, the owning service resets the affected target to the committed
current snapshot, immediately invalidating its manual Undo/Redo buttons. Failed,
cancelled, staged-only, conflicted, and no-op assistant work leaves history
unchanged. Assistant history batches, completion statuses, abandoned-batch
recovery, approval reconnection, and assistant-facing history tools are obsolete.

`IAssistantReviewBaselineService` separately stores the latest applied-assistant
review anchor for an exact Core/release chapter target: the immediately preceding
canonical manuscript JSON/hash, source turn, action label, and capture time. The
first successful chapter mutation in a turn stages this row in the same database
commit as the manuscript; later mutations in that turn retain the first baseline.
Once pending and Contest projections are resolved, Review compares that durable
Before state with live Current, so later manual edits remain visible. Undo, Redo,
and manual reversion do not rewrite it. A later successful assistant turn replaces
it. Review baselines are working-database data and are excluded from export/import.

In-memory snapshots index image, font, and composition dependencies rather than
copying binary assets. Live dependencies are hard deletion blockers. History-only
image/font deletion requires explicit Lorekeeper-owned confirmation to delete and
clear the affected current-process streams. Deleting a chapter, publication
section, release, edition-content branch, or entire project clears its owned
streams as lifecycle cleanup, not as an Undo action.

### Migration and projection boundaries

The original structured-manuscript migration uses a protected SQLite backup,
cross-process lease, journal, transactional live/history conversion, normalized
text-hash comparison, and recovery-shell fallback. Later v3 composition and v4
authoring-page cutovers preserve semantic IDs/text, Figures, staged review
payloads, Picture Page geometry, compositions, artifacts, and hashes while
removing obsolete runtime fields through forward migrations. Historical
migration names and source version numbers remain accurate even though v4 is
current.

Project export v24 writes v4 manuscripts, Core/release annotation rows, page
setup and compositions, style definitions, release snapshots, and current
publication content. Older manuscript inputs are accepted only through isolated
versioned transformers. Search, context, TXT, Markdown, EPUB, Read preview, and
Press all consume the semantic document or its explicit projection. A change to
block meaning, reading order, styles, or direct formatting must be traced across
every one of those consumers.

## Key files and file families

| File or family | Architectural role |
|---|---|
| [`Lorekeeper/Manuscripts/ManuscriptModels.cs`](../../Lorekeeper/Manuscripts/ManuscriptModels.cs) and [`docs/schemas/manuscript-v4.schema.json`](../schemas/manuscript-v4.schema.json) | Current v4 document, block, inline, mark, Figure, presentation, and schema contract. |
| [`Lorekeeper/Manuscripts/ManuscriptCodec.cs`](../../Lorekeeper/Manuscripts/ManuscriptCodec.cs), [`ManuscriptOperations.cs`](../../Lorekeeper/Manuscripts/ManuscriptOperations.cs), and inspection/range helpers | Validation, normalization, hashing, stable-ID lookup, semantic operations, structural inspection, and exact UTF-16 range resolution. |
| [`Lorekeeper/Manuscripts/IManuscriptService.cs`](../../Lorekeeper/Manuscripts/IManuscriptService.cs) and [`Lorekeeper/Chapters/`](../../Lorekeeper/Chapters/) | Sole target-aware runtime chapter-manuscript boundary plus chapter lifecycle, copy-on-write release content, projections, and side effects. |
| [`Lorekeeper/Manuscripts/EditorContentTarget.cs`](../../Lorekeeper/Manuscripts/EditorContentTarget.cs) | Protected Core/release target carried through manuscript, review, context, apply, and assistant operations. |
| [`Lorekeeper/Manuscripts/ManuscriptStyleService.cs`](../../Lorekeeper/Manuscripts/ManuscriptStyleService.cs) and [`ManuscriptStyleTemplateExtractor.cs`](../../Lorekeeper/Manuscripts/ManuscriptStyleTemplateExtractor.cs) | Revision-safe Book Text Style ownership and the shared manual/assistant style-capture policy. |
| [`Lorekeeper/Manuscripts/ManuscriptAnnotationModels.cs`](../../Lorekeeper/Manuscripts/ManuscriptAnnotationModels.cs) and [`ManuscriptAnnotationService.cs`](../../Lorekeeper/Manuscripts/ManuscriptAnnotationService.cs) | Exact-target sidecar annotation contract, rebasing, paging, state, and completion. |
| [`Lorekeeper/Authoring/`](../../Lorekeeper/Authoring/) and [`Lorekeeper/Models/AssistantReviewBaseline.cs`](../../Lorekeeper/Models/AssistantReviewBaseline.cs) | In-process manual history runtime, selection/dependency state, assistant mutation identity, and the separate durable latest-review baseline. |
| [`Lorekeeper/Components/Pages/Projects/ChapterBodyEditor.razor`](../../Lorekeeper/Components/Pages/Projects/ChapterBodyEditor.razor), [`ManuscriptViewLocation.cs`](../../Lorekeeper/Components/Pages/Projects/ManuscriptViewLocation.cs), and related Editor components | Shared semantic editor host, revision-aware autosave, transient cross-view location, Figure/style controls, Read/Review modes, annotations, and authoring workspace state. |
| [`tools/semantic-editor/`](../../tools/semantic-editor/) and shipped bundle under `Lorekeeper/wwwroot/js/` | Exact-pinned ProseMirror schema/adapter source, deterministic build, shipped runtime, and notices. |
| [`Lorekeeper/EditorChat/EditorManuscriptApplyService.cs`](../../Lorekeeper/EditorChat/EditorManuscriptApplyService.cs) | One-step assistant manuscript operation validation and apply/stage bridge over the canonical manuscript service. |
| [`Lorekeeper/Manuscripts/ManuscriptSchemaUpgrade.cs`](../../Lorekeeper/Manuscripts/ManuscriptSchemaUpgrade.cs) | Strict lossless v1-v3 document and nested historical-payload upgrade logic used only by migration/import owners. |

## Related chapters

- [narrative-context.md](narrative-context.md) owns outline chapter identity,
  canon associations, context assembly, and search/index projections.
- [assistants-chat.md](assistants-chat.md) owns Editor tool protocol, Review
  batches, Contest Mode, revision-worker orchestration, and chat lifetime.
- [composition-media.md](composition-media.md) owns image assets, page setup,
  composition variants/scenes, canvas behavior, and visual preview rendering.
- [publishing-model.md](publishing-model.md) owns Core/release inheritance,
  publication sections, edition content lifecycle, and effective projections.
- [press-production.md](press-production.md) owns pagination, PDF/EPUB output,
  renderer validation, artifacts, and production claims.
- [persistence-migrations-import.md](persistence-migrations-import.md) owns the
  database operation model, startup orchestration, protected backups/recovery,
  EF migrations, and import/export transactions.

## Relevant verification

- Search every manuscript block/type/style/presentation field across the codec,
  operations, service, editor bridge, annotations, history, context/indexing,
  import/export, composition, EPUB, preview, Press request, and tests.
- Confirm all runtime writes use `IManuscriptService`, carry the exact
  `EditorContentTarget`, validate expected revision and referenced ownership,
  and update derived graph/search/index state.
- For schema changes, advance the current schema and JSON Schema together, add a
  strict forward upgrader, preserve applied migrations, update versioned import
  handling, and prove live plus historical payload conversion under the guarded
  migration boundary.
- For editor changes, rebuild the exact-pinned semantic editor, verify the
  committed bundle is current, and inspect conflict reconciliation, exact
  cross-view location anchors, normalized fallback, selection/non-text blocks,
  paste diagnostics, and action grouping. Browser UI checks require explicit
  authorization.
- For annotation/history changes, verify same-transaction rebasing, inherited
  release behavior, outdated-state fallback, action coalescing, stale-stream
  reconciliation, assistant-batch finalization, dependency retention, and
  export exclusion.
- Automated .NET tests may cover only startup migration or versioned
  import/export preservation and fail-closed behavior; do not add ordinary
  editor, assistant, annotation, style, or service tests.
- Run `dotnet build Lorekeeper.sln`, relevant approved migration/import tests,
  and the HTTP startup smoke check for source changes. Documentation-only edits
  require link/path validation and the broader verification selected by the
  coordinating task.
