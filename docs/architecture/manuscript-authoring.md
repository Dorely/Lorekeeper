# Manuscript and Authoring

## When to read

Read this chapter completely before changing the manuscript schema, chapter
body persistence, semantic operations, editor bridge or toolbar, Core/release
content targets, Book Text Styles, review annotations, process-lifetime Undo/Redo,
Git-backed Review Edits, authoring migrations, or any projection that
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
payloads fail closed. Legacy historical review payloads may retain plain text,
and the literal JSON `null` may represent an absent historical snapshot; those
bytes are handled only by guarded migration/import readers rather than a runtime
editing route.

### Model-facing manuscript projection

`AgentManuscriptProjection` is the single model-facing manuscript serializer.
It emits `agent-manuscript-v1` without changing canonical manuscript v4
persistence, import/export, audit rows, or stable-ID mutation contracts. The
projection repeats identity, v4 schema version, revision, plain-text source
hash, source state, completeness, and pagination. Each included block appears
exactly once as `[absoluteIndex, stableBlockId, exactText]`; absolute indexes
remain document-relative for bounded and filtered reads but are compact
cross-references, never mutation identities.

Sparse overlays carry only applicable meaning: structure rows, overlapping
UTF-16 mark ranges and optional values, first-use deterministic interned
paragraph formats and block references, complete Figure asset/accessibility/
presentation data, Designed Page composition references, and bound publication
fields. Null and empty overlays are omitted while meaningful `false` and `0`
values remain explicit. Inline-node segmentation may normalize away when it
does not change text or mark ranges. Direct paragraph formatting overrides a
named Book Text Style, which overrides built-in defaults.

The active Editor/revision-worker chapter context, manuscript read and inspect
tools, Publish section reads, and Contest candidate source all use this shared
projection. Named-style context is a separate
`agent-manuscript-styles-v1` payload containing only versioned definitions;
per-block direct formatting and structural roles remain solely in the
manuscript projection.

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

Insertion is strictly additive and never carries replacement semantics. An
assistant revising one existing text block uses `ReplaceBlockText` to preserve
its stable identity. Before a multi-block rewrite it assigns every source block
in the intended range to retain, replace, or delete, and only then inserts any
additional replacement blocks. The runtime does not automatically deduplicate
similar prose because repeated language may be intentional.

Editor assistants never send a full projected document back to an apply call.
`EditorManuscriptApplyService` converts and validates one complete operation
batch, verifies the protected source revision, and writes the resulting document
through `IManuscriptService` in that same call. Review Edits controls whether the
completed turn checkpoints; it does not create an overlay. The result reports
compact changed IDs, operation and before/after block counts, the full resulting
hash, diagnostics, the committed live revision, and exact bounded readback
ranges; no turn-local preview ID or projected document is retained. Text and
structure changes require focused readback from the resulting live source. Each
range includes one adjacent block on either
side where available, overlapping ranges merge, and long ranges split at the
100-block read limit. An insertion-only text batch against a non-empty source
returns `MANUSCRIPT_INSERT_WITHOUT_REPLACEMENT` as a warning rather than
rejecting intentional additive work.

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
caller-controlled Designed Page insertion. Publish section prose passes
`AllowAnnotations=false` because a publication-section ID is not a chapter ID;
the shared editor must not call chapter-scoped annotation APIs for that surface.
Chapter/editor initialization failures are logged with their project, chapter,
and target identity and remain visible with explicit retry state; cancellation,
disconnect, and disposal remain non-errors, while user-action and save failures
retain their existing error handling. The editor surface fills its row and only
the owning surface scrolls when content exceeds available space.

The primary toolbar loads the project font catalog and provides direct font,
size, line-spacing, emphasis, alignment, indentation, list, link, paragraph,
and Figure controls. Project-image selection uses the shared visual library
modal; it must not regress to filename dropdowns or native prompts. Bundled and
imported fonts load from project-owned URLs and are staged identically for Read
preview and publication.

Edit and Read share the versioned manuscript typography defaults embedded from
Press. Edit applies the effective project body size and leading to its
continuous responsive canvas, while Press remains authoritative for physical
line breaks and pagination. Explicit heading levels, bundled font aliases,
inline marks, block spacing, list and scene-break metrics, inset-quotation
text/rule color and rule geometry, and Figure caption color/overlay treatment
must resolve from the same defaults before sparse named styles and direct
presentation are applied. The quotation's configured left indent is the total
text inset; its rule and gap are placed within that measure, and a quotation
continued across pages receives one rule segment on every occupied page. Body
size and leading from page setup apply to quotation text in both surfaces.
Heading levels 1-6 retain their distinct semantic levels in Read and tagged
Digital PDF output.

Paragraph Book Text Styles and direct paragraph presentation on a Figure target
its caption rather than the image-bearing Figure wrapper. Caption left/right
and first-line indents constrain and position caption text within the image
measure. Direct presentation remains the final precedence layer, including
explicit `false` values that turn off inherited italic or small-caps styling.
Visible links inherit the surrounding text color and remain underlined.
Switching back to Edit refreshes page-setup typography without remounting or
replacing the manuscript document.

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
semantic blocks through the versioned browser-preview trace; visual artifact
lines remain present but are excluded from author selection and annotation
mapping.

Direct chapter selection carries the currently visible mode into the selected
chapter: Review remains Review, Read remains Read, Edit opens Pages only for a
Designed-Page-only destination, and Pages remains Pages when the destination has
any Designed Page. An unresolved Contest does not force Review or change the
selected chapter, target, mode, or pane state. Route and reload selection instead
uses the destination chapter's stored mode or its default; the resolved carried
mode is then written as that chapter's preference. Chapter transitions clear the
prior chapter's loaded Review projection, comparison commit, pending target, and
Contest state before loading the new chapter without deleting durable Git review
data. An active Contest keeps the project-wide Editor lock in force, so Edit and
Pages remain readable but their mutations are disabled. The contest projection
appears in the normal Review surface only after the user explicitly selects
Review.

Contest generation uses two stable boundary anchors anywhere in one chapter at an
exact manuscript revision. A null before or after anchor selects the corresponding
document edge, and both null anchors select the whole chapter. The replacement
begins after the before anchor and ends before the after anchor; anchors and all
blocks outside the span remain unchanged. The interior may cross scene breaks and
rich/atomic blocks, which are intentionally removed when that span is replaced.
Empty spans between adjacent anchors, at either document edge, or in an empty
full chapter are valid insertion targets. Tool-less contestants return natural
Markdown prose. The service parses any nonzero prose into fresh semantic Paragraph
and SceneBreak blocks, normalizing headings, blockquotes, list prefixes,
links/images, emphasis, and inline code into paragraph text; standalone `***`,
`###`, `---`, and `___` become scene breaks. It rejects empty, machine-readable,
stale, or invalid-boundary output and, apart from scene separators, never
interprets structural operations.
Generated proposals and later candidate review edits remain isolated drafts until
atomic resolution applies the selected draft to live state.

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
create or rewrite them. Annotation completion is a live sidecar mutation;
Review Edits records it in the Git HEAD-to-live comparison with the affected
target.

### In-process manual history and Git-backed assistant review

The singleton `IAuthoringHistoryRuntime` owns process-lifetime Undo/Redo for Core
and release chapters, publication prose sections, complete Designed Page
aggregates, and Core/release covers. It has no EF or SQLite dependency. Each
target-isolated stream stores Brotli-fast compressed snapshots, retains at most
100 manual actions, and participates in a 128 MiB process-wide budget. Navigation
and page reloads retain streams while Lorekeeper is running; a full process exit
clears them. This process-lifetime history is never exported; the separate
version-history system captures selected canonical manuscript/style state in
deterministic Git snapshots.

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
assistant mutation, the owning service resets the affected target's manual
history, immediately invalidating its Undo/Redo buttons. Failed, cancelled,
conflicted, and no-op assistant work leaves history unchanged. Superseded
assistant-review persistence and assistant-facing history tools are not runtime
state.

Review compares canonical live state with Git HEAD. With Review Edits enabled,
assistant mutations remain local and dirty until the user approves them. With it
disabled, a completed mutating assistant turn creates a complete checkpoint.
Pending review supports semantic block approval, Undo, and inline text editing;
historical review compares the newest affecting approved commit with its parent.
Undo in historical mode restores the parent value into live state as a normal
pending reversal. The Review Edits preference is excluded from Git snapshots and
does not alter manual history or restore behavior.

In-memory snapshots index image, font, and composition dependencies rather than
copying binary assets. Live dependencies are hard deletion blockers. A
history-only image/font dependency means a current-process Undo/Redo stream and
requires explicit Lorekeeper-owned confirmation to delete and clear the affected
streams; Git version-history blobs are durable history and do not act as live
asset-deletion blockers. Deleting a chapter, publication
section, release, edition-content branch, or entire project clears its owned
streams as lifecycle cleanup, not as an Undo action.

### Migration and projection boundaries

The original structured-manuscript migration uses a protected SQLite backup,
cross-process lease, journal, transactional live/history conversion, normalized
text-hash comparison, and recovery-shell fallback. Later v3 composition and v4
authoring-page cutovers preserve semantic IDs/text, Figures, live pending
manuscript state, Picture Page geometry, compositions, artifacts, and hashes while
removing obsolete runtime fields through forward migrations. Historical
migration names and source version numbers remain accurate even though v4 is
current.

Project export v30 writes v4 manuscripts, Core/release annotation rows, page
setup and compositions, style definitions, release snapshots, and current
publication content, including linked image-upscale provenance. Older manuscript inputs are accepted only through isolated
versioned transformers. Search, context, TXT, Markdown, EPUB, Read preview, and
Press all consume the semantic document or its explicit projection. A change to
block meaning, reading order, styles, or direct formatting must be traced across
every one of those consumers.

Publication image preparation is a system-owned manuscript mutation. It changes
only a Figure's image identity after the managed publication model proves that
the effective placement is below the active DPI threshold; semantic block IDs, captions, accessibility,
presentation, crop, reading order, and every non-image field remain unchanged.
Inherited release chapters update their Core owner, while an existing edition
chapter override updates that override without creating a new customization.
The owning revision, derived projections, Review Edits state, artifact freshness,
and process-lifetime manual history are invalidated through the same target-aware
mutation boundary as other persisted manuscript changes.

## Key files and file families

| File or family | Architectural role |
|---|---|
| [`Lorekeeper/Manuscripts/ManuscriptModels.cs`](../../Lorekeeper/Manuscripts/ManuscriptModels.cs) and [`docs/schemas/manuscript-v4.schema.json`](../schemas/manuscript-v4.schema.json) | Current v4 document, block, inline, mark, Figure, presentation, and schema contract. |
| [`Lorekeeper/Context/AgentManuscriptProjection.cs`](../../Lorekeeper/Context/AgentManuscriptProjection.cs) and [`ContextManuscriptFormatter.cs`](../../Lorekeeper/Context/ContextManuscriptFormatter.cs) | Shared versioned model-facing manuscript and named-style projections; canonical v4 serialization remains in `ManuscriptCodec`. |
| [`Lorekeeper/Manuscripts/ManuscriptCodec.cs`](../../Lorekeeper/Manuscripts/ManuscriptCodec.cs), [`ManuscriptOperations.cs`](../../Lorekeeper/Manuscripts/ManuscriptOperations.cs), and inspection/range helpers | Validation, normalization, hashing, stable-ID lookup, semantic operations, structural inspection, and exact UTF-16 range resolution. |
| [`Lorekeeper/Manuscripts/IManuscriptService.cs`](../../Lorekeeper/Manuscripts/IManuscriptService.cs) and [`Lorekeeper/Chapters/`](../../Lorekeeper/Chapters/) | Sole target-aware runtime chapter-manuscript boundary plus chapter lifecycle, copy-on-write release content, projections, and side effects. |
| [`Lorekeeper/Manuscripts/EditorContentTarget.cs`](../../Lorekeeper/Manuscripts/EditorContentTarget.cs) | Protected Core/release target carried through manuscript, review, context, apply, and assistant operations. |
| [`Lorekeeper/Manuscripts/ManuscriptStyleService.cs`](../../Lorekeeper/Manuscripts/ManuscriptStyleService.cs) and [`ManuscriptStyleTemplateExtractor.cs`](../../Lorekeeper/Manuscripts/ManuscriptStyleTemplateExtractor.cs) | Revision-safe Book Text Style ownership and the shared manual/assistant style-capture policy. |
| [`Lorekeeper/Manuscripts/ManuscriptAnnotationModels.cs`](../../Lorekeeper/Manuscripts/ManuscriptAnnotationModels.cs) and [`ManuscriptAnnotationService.cs`](../../Lorekeeper/Manuscripts/ManuscriptAnnotationService.cs) | Exact-target sidecar annotation contract, rebasing, paging, state, and completion. |
| [`Lorekeeper/Authoring/`](../../Lorekeeper/Authoring/) | In-process manual history runtime, selection/dependency state, and assistant mutation identity; Git-backed review owns pending/approved comparison. |
| [`Lorekeeper/Components/Pages/Projects/ChapterBodyEditor.razor`](../../Lorekeeper/Components/Pages/Projects/ChapterBodyEditor.razor), [`ManuscriptViewLocation.cs`](../../Lorekeeper/Components/Pages/Projects/ManuscriptViewLocation.cs), and related Editor components | Shared semantic editor host, revision-aware autosave, transient cross-view location, Figure/style controls, Read/Review modes, annotations, and authoring workspace state. |
| [`tools/semantic-editor/`](../../tools/semantic-editor/) and shipped bundle under `Lorekeeper/wwwroot/js/` | Exact-pinned ProseMirror schema/adapter source, deterministic build, shipped runtime, and notices. |
| [`Lorekeeper/EditorChat/EditorManuscriptApplyService.cs`](../../Lorekeeper/EditorChat/EditorManuscriptApplyService.cs) | One-step assistant manuscript operation validation and direct apply over the canonical manuscript service. |
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
- [version-history-sync.md](version-history-sync.md) owns durable deterministic
  Git snapshots and restore; this chapter owns process-lifetime Undo/Redo and
  the canonical manuscript/style/annotation state captured by that boundary.

## Relevant verification

- Search every manuscript block/type/style/presentation field across the codec,
  operations, service, editor bridge, annotations, history, context/indexing,
  import/export, composition, EPUB, preview, Press request, and tests.
- Confirm all runtime writes use `IManuscriptService`, carry the exact
  `EditorContentTarget`, validate expected revision and referenced ownership,
  and update derived graph/search/index state.
- Confirm assistant revisions distinguish insert from replace, every superseded
  source block is accounted for, direct text/structure applies require all
  returned range reads with matching revision/hash, and Review Edits compares
  the live source with Git HEAD without an overlay.
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
  reconciliation, Git pending/historical review, dependency retention, and
  export exclusion.
- In addition to startup migration and versioned import/export safety, the
  validation chapter permits deterministic, headless DOCX/citation and
  authoring-operation/Undo/save-recovery contract regressions. Do not add broad
  editor, assistant, annotation, style, or service test suites, or tests that
  simulate browser UI or external integrations.
- Run `dotnet build Lorekeeper.sln`, relevant approved migration/import tests,
  and the HTTP startup smoke check for source changes. Documentation-only edits
  require link/path validation and the broader verification selected by the
  coordinating task.
