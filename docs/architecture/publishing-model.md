# Publishing model architecture

## When to read

Read this chapter when a change touches the Core Book, publication releases,
release inheritance, edition-specific manuscript content, publication sections,
chapter inclusion or order, section start-side behavior, PDF presentation,
publication metadata, Publish UI, Publish assistant tools, effective
configuration/fingerprints, format projections, preparation orchestration, or
publication-facing cover ownership. Read it with `composition-media.md` for
Designed Page scenes, shared canvas behavior, images, fonts, and page setup.
Read it with `press-production.md` for print-artifact profiles, cover surface
geometry, native rendering, PDF/EPUB validation, artifacts, and packages.
Read it with `persistence-migrations-import.md` when changing stored release,
section, artifact, job, or migration data.

This chapter describes the format-neutral publication model and the boundaries
between authoring and production. A Core Book is not a publication release,
Publish is not a second manuscript editor, and an artifact is never treated as
ready merely because a render completed.

## Scope and ownership

`IPublicationBookService` owns the one-to-one revisioned Core Book per project.
It owns shared bibliographic metadata, language, title/contents and heading
presentation, fixed outline content, chapter inclusion, Core publication
sections, project-linked page setup usage, the shared paginated chapter-start
policy, Digital PDF presentation defaults, and the reusable Core front-cover
scene. Core exists even when no publication release exists and can produce only
a private `ReadingPdf`.

`IPublicationSectionService` owns matter around the fixed Core outline. A
publication section is either a semantic prose document with optional flowing
Figures or one or more section-owned Designed Page canvases. The service
rejects mixed prose/canvas blocks so manual UI, assistant tools, export, and
rendering share one durable boundary. System sections such as title,
copyright, and contents retain live metadata bindings; user sections include
dedications, acknowledgements, author notes, references, image pages, and
other production material. Every successful durable section mutation publishes
the shared Review Edits invalidation only after its save or transaction commit;
read paths and true no-ops do not refresh the top-bar review projection.

`IPublicationEditionService` owns optional paperback, hardcover, EPUB ebook,
and PDF ebook release aggregates. Releases own destination, exact immutable
artifact profile, ISBN, status, sparse Core overrides, edition
content state, artifacts, packages, cloning, archival, comparison, and audit
history. `IPublicationEffectiveConfigurationResolver` combines Core values
with explicit release overrides at read/render time. `IPublicationReleasePresetService`
creates safe product/destination defaults without exposing raw profile IDs as
ordinary UI or assistant inputs.

`EditionContentService` owns opt-in release-specific chapter and style content.
`EditorContentTarget` identifies Core or one enabled release and protects every
manuscript, review, contest, revision, Figure, composition, preview, search,
and assistant mutation from redirecting to another target. Publish consumes
the effective result and reports differences or layout diagnostics; it does
not mutate edition manuscript or page layout.

`IPublicationPreparationService` owns persisted reconnectable one-action jobs
that coordinate readiness, rendering, validation, and package assembly.
`IPublishService` remains projection/export-only for Core TXT/Markdown/Reading
PDF and release EPUB/PDF/print outputs. `PublishChatService` and
`PublishAssistantTools` expose bounded, revision-safe publication operations
through those owning services; they do not own independent persistence or
profile implementations.

## Current architecture and invariants

Core Book follows the project outline: acts sort by `Act.Order` and stable ID,
then each act's chapters sort by their act-local `Chapter.Order` and stable ID,
followed by unassigned chapters with the same chapter ordering. The persisted
Core outline is reconciled to that canonical sequence on read while preserving
inclusion for surviving targets; additions, removals, and reorders advance the
Core revision once, so source/artifact freshness changes only for a real
effective-outline change. Release outline rows are sparse inclusion overlays,
not independent ordering: effective Core and release reads always inherit that
canonical sequence. Acts remain structural groups; heading and synopsis
settings determine whether act presentation is emitted. Chapter rows are
selectable publication content. Core owns shared title/author/language
metadata, chapter inclusion, publication sections, page setup and Book Text
Styles through their respective services, and the reusable front cover.
Core also owns the chapter-start policy for paginated output. It defaults to
next available page, which never adds a chapter-parity blank. The optional
right-hand policy adds a blank only when needed to place a chapter opening on a
recto leaf. Reading PDF, Digital PDF, paperback, and hardcover use the effective
policy; EPUB has no fixed leaf parity and neither shows nor applies it. Releases
inherit the Core policy live and may store one sparse override, with reset
returning to Core inheritance.
The current Core page aspect also supplies the default provider-valid raster for
free-standing image generation and editing throughout the application; concrete
Figure, page, frame, and cover targets retain their own exact geometry. Editor
and Publish resolve layout-bound generation to at least 300 effective DPI,
including hardcover targets. Digital-PDF placement validation remains 180 DPI;
that validation threshold does not lower the authoring-generation default.
Release creation is explicit: no release or ISBN is created automatically. The
creation dialog establishes release format, destination, name, and any
destination-level use mode. Release setup then exposes only artifact-affecting
choices: trim, interior color process, paper weight/thickness, cover
construction, cover printing topology, and cover submission. There is no print
product selector. Paper color, finish, price, listing, account, tax, and
fulfillment settings remain at the printer because they do not change generated
bytes or geometry.

Publication sections have an anchor before, after, or around the Core outline,
an inclusion state, and a start-side choice of next available, right/recto, or
left/verso. Inclusion, order, and starting side are authored settings rather
than silently derived vendor policy. A two-leaf Designed Page spread is the
specific parity exception that must begin on a verso leaf so its two leaves
form one physical opening. KDP/common front-matter guidance is surfaced as a
non-blocking recommendation; it does not rewrite the author’s section order.

Releases inherit Core sections live. They may replace, omit, add, reset, or
reorder sections through sparse overlays. A release order overlay does not
materialize section content or imply that inherited prose/canvas content was
customized. Content, order, and inclusion therefore remain separate axes in
the effective configuration and source fingerprint. A Core collection change
stales only releases whose effective source fingerprint changes.

Core and release forms use one serialized debounce queue. Navigation, assistant
turns, cover entry, and preparation flush pending edits before reading or
acting. A revision race reloads the current target and retries a still-dirty
patch when intent remains unambiguous. Successful background saves remain
silent but refresh artifact freshness. A stale assistant or UI operation must
receive a compact conflict/recovery result rather than overwriting newer
content.

Physical-release cover entry is also a pagination boundary. After flushing the
current release, Publish automatically runs the compact Press interior layout
needed to obtain the current page count and opens the cover only after that
snapshot succeeds. The release cover and its assistant tools therefore use the
calculated spine rather than minimum-page placeholder geometry. The persisted
page count survives release, printer, paper, cover, identifier, and other changes
that cannot affect interior pagination. While the cover is open, Publish compares
the pagination fingerprint after an assistant mutation and closes the editor only
when the interior layout identity actually changed; reopening then refreshes
pagination before further cover work.

Release-specific manuscript content is opt-in. An untouched release chapter
reads current Core live. Its first text or layout mutation creates a complete
copy-on-write snapshot, records the Core revision/hash, and clones referenced
Designed Page compositions with source identities and stable scene
relationships. Later Core edits do not alter that snapshot. Reset removes only
the release-owned manuscript/compositions and returns to live Core inheritance.
Project images and Book Text Styles remain shared resources; resetting or
deleting a release never deletes them. Shared style changes report Core/release
usage and affect every actual reference.

The Editor assistant is locked to the selected target. Edition mode omits
outline/canon mutations and cannot update or delete an existing shared style,
although it may create and apply a reusable copy. Review annotations,
  contests, revision workers, process-lifetime manual history, Figures, Designed Pages, and previews all
carry the same protected target. Publish can link to the exact Editor target
for a difference or diagnostic, but does not become an edition manuscript
authoring surface.

The Core Book can create a private reading copy with no ISBN, vendor,
or publication claim. A Core reading copy may retain visible warnings for
pending image alternative-text/decorative decisions; unresolved decisions
remain blockers for publication releases. Paperback and hardcover output use
resolved print-artifact profiles, EPUB output uses EPUB releases, and PDF ebook output uses a
PDF ebook release. Format-specific identifiers and metadata never cross formats:
EPUB editions cannot request Press output, and non-EPUB editions cannot export
EPUB.

Effective values are resolved at read/render/preflight time. Absence means
inheritance; optional text may be explicitly empty; reset removes the
override. Core metadata updates linked system-page copy transactionally while
preserving canonical field bindings. Every Publish page read, preview,
validation, and preparation refreshes the active target before returning state,
so the UI, assistant, canvas preview, validator, and renderer inspect the same
resolved copy. Cover validation returns structured severity/code diagnostics as
well as legacy messages; low placed-image DPI is a warning (180 DPI for Core
and digital PDF, 300 DPI for paperback and hardcover) and does not by itself
block a Core reading copy. An empty optional bound frame is omitted from
output when its canonical cover binding or semantic content reference is
valid; a frame with neither valid binding nor reference remains an error.

System title and copyright Designed Page compositions have one semantic block
per bound field. Their live metadata values are projected into those blocks
without changing the canonical Core or release field. The projection converts
CRLF and CR line endings to LF, preserves single hard breaks, and collapses
blank-line paragraph delimiters to one hard break because a semantic block must
remain one paragraph. Initial composition creation and later binding refreshes
use the same projection for Core and effective release values.

The expected page map sent through preparation includes the same blocks Press
can place: all nonempty text blocks plus every SceneBreak, Figure, and
DesignedPage block. Empty ordinary paragraphs, headings, block quotes, and
list items are omitted because Press intentionally skips them; optional empty
bound fields therefore do not produce incomplete-page warnings.

Publish route initialization is progressive. Core metadata and the release
navigator form the blocking shell. Outline/publication-section details,
preparation state, artifact freshness, and the project-aware assistant mount
after the first interactive render. Artifact list and render-status queries
return metadata only; PDF, EPUB, and package bytes remain behind immutable
project-scoped endpoints.

Publish presents publication sections and chapter inclusion as one collapsible
Front/Main content/Back flow. Main content owns act/chapter heading, synopsis,
numbering, and navigation choices. New prose or Designed Page sections are
created at the selected group/anchor. Designed sections open in the shared
canvas workspace; prose sections use the shared semantic manuscript editor
wired to the section. Section customization copies the current Core canvas and
adapts the complete scene to release geometry. An older saved layout for the
same geometry must not replace current typography, artwork, or styling during
that cutover.

The Publish assistant receives complete ordered outline context, stable IDs,
synopses, beats, Book Brief, Project Guidance, project facts, and a protected
snapshot of the visible Core/release surface. Bounded search and explicit reads
provide manuscript, source, entity, fact, and artwork detail on demand. Tools
return compact changed IDs, fields, revisions, diagnostic counts, and refresh
notices rather than echoing complete unchanged records. Large scene changes
use persisted non-replayable stages; preview returns a stage ID and compact
diagnostics, and apply accepts only that ID plus expected revision.

Publish mutation tools cover section metadata, focused prose operations,
section page scenes, cover operations, readiness, preparation, cancellation,
and artifact metadata. Section creation starts empty and never accepts a
bounded `blocks` read as a manuscript payload. Focused tools can fill a canvas,
replace a single image frame, patch an object, stage semantic-only or
scene-only changes, stage coupled changes, preview, and validate the active
target. The assistant cannot reorder chapters or mutate chapter manuscript
content from Publish. Artifact-profile resolution, exact geometry, and cover
surface rules belong to the Press production boundary.

Print releases carry provider-neutral `PrintProjectUse`, `PrintIdentifierMode`,
and `PrintCoverSubmissionMode` settings. B&N supports personal-use and for-sale
projects and defaults new releases to personal use; existing releases migrate
to for-sale semantics. Switching use keeps entered ISBN text but changes the
applicable vendor-SKU/vendor-assigned/user-supplied identifier behavior and
therefore stales prepared artifacts and `print-setup.json`. A user-supplied mode
requires ISBN-13; B&N-assigned ISBN mode remains preparable because assignment
occurs during upload. These settings exist only to prepare and map artifacts;
external account, rights, tax, pricing, listing, and order workflows are not
modeled here. B&N documents the identifier distinction between its project
choices ([B&N personal-use versus for-sale guidance](https://help-press.barnesandnoble.com/hc/en-us/articles/5358880235547-Print-Books-for-Sale-vs-Print-for-Personal-Use)).

Release cover reads expose exact Back, Spine, and Front region descriptors with
bounds, physical aspect, safety/guides, participation, and geometry fingerprints.
The assistant can read or preview one region, fill it with a project image, set
spine direction, and request an exact region generation target. Region-targeted
generation remains unattached and must resolve a 300-DPI-compatible raster
without stretching. If one native raster cannot satisfy provider limits but the
target's aspect is provider-representable, the request proceeds through the
print-upscale pipeline: generation at the largest compatible raster plus a
deterministic Lanczos3 print upscale to the exact print raster without adding detail. Back, Spine, and
Front are preferred split boundaries and already resolve natively; when no single
provider raster can represent a target's aspect, the request fails before
dispatch with a smallest equal-panel plan, each panel targets exact surface
bounds, and the assistant must inspect annotated regions plus the clean whole
wrap without claiming independent generations are seamless.

`read_publication_section` returns section metadata, canvas summaries, and the
shared `agent-manuscript-v1` projection for its bounded prose blocks. Core or
customized section reads retain the persisted source label; live release
inheritance is labeled inherited. Absolute block indexes, source hash,
revision, completeness, pagination, Figure semantics, Designed Page references,
and publication-bound fields remain intact, while empty overlays and null fields
are omitted.

`IPublishService` projects every included ordered semantic-matter document into
TXT, Markdown, EPUB, and contained Press output. Display-ready numbered titles
are carried by the projection; Press-side title numbering is disabled so a
chapter or act prefix is emitted exactly once. Semantic text remains text in
the output. Flowing Figures, mixed-layout EPUB segments, Designed Page scenes,
captions, alternatives, reading order, and cover presentation are projected
from the effective Core/release model rather than a flattened parallel
document.

The EPUB exporter segments mixed semantic and Designed Page content at actual
content boundaries. A fixed-layout canvas is one spine item; it does not get
synthetic empty reflowable siblings. The first real segment retains the
chapter/section navigation target. Fixed-layout viewports use the scene’s
point-sized coordinate space as CSS pixels so canvas text size, wrapping,
padding, alignment, and object placement remain faithful. Changing this
serialization contract advances the EPUB exporter version and stales prepared
EPUB/package artifacts.

Reflowable EPUB preserves the manuscript style cascade used by Edit and Press:
built-in block defaults, then sparse paragraph/character Book Text Styles, then
direct presentation, including explicit `false` overrides for italic and small
caps. Figure paragraph styles apply to captions. Links inherit surrounding
text color and remain underlined, and overlay captions use the same translucent
backing opacity and padding as the shared manuscript typography contract.

Artifacts carry source revision/fingerprint, edition settings, assets,
renderer/profile provenance, validation evidence, and applicable exporter or
assembler versions. Core/release changes, typography/font changes, renderer
changes, registry/profile changes, exporter changes, and source-content
changes stale only the artifacts they affect. Archived releases are immutable
at owning mutation boundaries; existing artifacts remain readable/exportable,
and cloning creates the editable continuation.

Version-history snapshots capture the canonical Core Book, publication editions,
and publication sections so authored publication intent can be restored. They
exclude preparation jobs, render/package bytes, page maps, audits, migration
journals, and other derived or operational production state; those artifacts
are regenerated after restore through the Press boundary.

Publication preparation runs permanent image preparation after basic readiness
checks and before reusable-render lookup. EPUB records no DPI transformation;
Core and Digital PDF use 180 DPI, while every print profile uses 300 DPI. The
managed publication model resolves each included placement's effective geometry
and a maximum proportional raster per source asset before staging Press. If any included placement is undersized, one suitable
derivative is created or reused and every included reference owned by the prepared
target is replaced atomically. Excluded content, unrelated releases, entity
visuals, and chat attachments are outside this mutation. When
`Images:PrintUpscale` is disabled, an undersized placement blocks preparation
without creating a derivative or changing references.

Ownership follows effective publication inheritance: an inherited chapter,
publication section, composition, or Core cover updates Core; content already
customized by the release updates that release without manufacturing a new
customization. The mutation validates the queued source fingerprint immediately
before commit, updates all owner revisions and invalidations, stores the new
fingerprint, and then reruns managed image validation. A stale source is retryable;
corrupt input, an oversized required raster, or any still-undersized placement
blocks before final rendering. Cancellation before commit rolls back; cancellation
after a coherent replacement commit preserves it and stops before rendering.

Each preparation job persists and projects a structured image summary containing
the threshold, created/reused asset counts, replaced-reference count, and
actionable failures. Publish UI and assistant preparation results surface this
summary. Since image preparation precedes artifact reuse, both the final artifact
and its fingerprint describe the permanent replacement references.

## Key files and file families

| Path or family | Primary responsibility |
|---|---|
| `Lorekeeper/Publish/PublicationBookService.cs` | Core Book creation, metadata/presentation patches, chapter inclusion, reusable cover coordination, revisions, source fingerprints, and Core-plus-release effective configuration resolution. |
| `Lorekeeper/Publish/IPublicationEditionService.cs` / `PublicationEditionService.cs` | Release lifecycle, sparse overrides, naming, cloning, archiving, comparison, and effective fingerprints. |
| `Lorekeeper/Publish/PublicationSectionService.cs` | Core/release section lifecycle, inclusion, anchors, prose/designed boundaries, bindings, in-process manual history, and release materialization. |
| `Lorekeeper/Publish/EditionContentService.cs` | Opt-in release content, chapter reset/discard, Core-drift differences, and release Designed Page diagnostics. |
| `Lorekeeper/Publish/PublicationReleasePresetService.cs` | Safe release defaults for paperback, hardcover, EPUB ebook, and PDF ebook products. |
| `Lorekeeper/Publish/PublishModels.cs` | Core/release targets, effective workspace/readiness, sparse mutations, sections, covers, preparation, and artifact contracts. |
| `Lorekeeper/Publish/IPublishService.cs` / `PublishService.cs` | Effective Core/release projection and TXT/Markdown/Reading PDF, EPUB, PDF, and print export facade. |
| `Lorekeeper/Publish/PublishAssistantTools.cs` | Compact revision-safe Publish reads/mutations, section/canvas/cover stages, readiness, preparation, and artifact metadata. |
| `Lorekeeper/Components/Pages/Projects/Publish/PublishContent.razor` | Progressive Core/release workspace, Front/Main/Back flow, readiness, inclusion/order controls, and downloads. |
| `Lorekeeper/Components/Pages/Projects/Publish/PublishChatPanel.razor` | Refresh-safe Publish chat adapter, flush-before-turn context, streaming, attachments, and workspace callbacks. |
| `Lorekeeper/Components/Pages/Projects/Publish/PublicationPdfPreview.razor` | Lorekeeper-owned immutable PDF page/facing preview surface. |
| `Lorekeeper/Components/Pages/Projects/Publish/PublicationEpubPreview.razor` | Artifact-backed sandboxed EPUB reader and spine/navigation inspection. |
| `Lorekeeper/Publish/PublicationPreparationService.cs` | Persisted one-action Core/release preparation, progress, blockers, cancellation, and retained Core warnings. |
| `Lorekeeper/Publish/PublicationDiagnosticPresentationService.cs` | Safe user-facing resolution of Press/preparation diagnostics to chapters, sections, pages, covers, and editor links. |
| `Lorekeeper/Publish/PublicationSectionOrderCodec.cs` | Stable sparse release section-order overlay serialization. |
| `Lorekeeper/Models/PublicationBook.cs`, `PublicationEdition.cs`, `PublicationSection.cs` | EF aggregates for Core, releases, and publication sections. |
| `Lorekeeper/Models/PublicationEditionChapterOverride.cs` / `PublicationEditionOutlineItem.cs` | Copy-on-write release chapter snapshots and sparse chapter-inclusion overlays. |
| `Lorekeeper/Models/PublicationPreparation.cs` | Durable preparation progress, blockers, cancellation, and target state. |
| `Lorekeeper/Publish/IPublishExportFormatter.cs` / `PublishExportFormatters.cs` | TXT/Markdown and semantic EPUB projections from effective publication state. |

## Related chapters

- [`composition-media.md`](./composition-media.md) owns images, Figures,
  fonts, page setup, Designed Pages, scene variants, canvas interaction, and
  transient visual previews.
- [`press-production.md`](./press-production.md) owns exact products,
  geometry, covers, native Press rendering, PDF/EPUB validation, artifacts,
  packages, and production claims.
- [`manuscript-authoring.md`](./manuscript-authoring.md) owns semantic chapter
  and section prose, styles, annotations, revisions, and history.
- [`persistence-migrations-import.md`](./persistence-migrations-import.md)
  owns durable release/section/render rows, lock order, migrations, recovery,
  and import/export boundaries.
- [`version-history-sync.md`](./version-history-sync.md) owns deterministic
  capture/restore of publication core state; generated artifacts and packages
  remain owned by Press and are intentionally absent from snapshots.
- [`assistants-chat.md`](./assistants-chat.md) owns shared chat runtime,
  context compaction, assistant mutation identity, and general tool protocol.

## Relevant verification

Normal source changes require a successful solution build and HTTP-host startup
check:

```powershell
dotnet build Lorekeeper.sln
dotnet run --project Lorekeeper --launch-profile http
```

Terminate the host after confirming startup. When publication model or export
contracts change, inspect the target-aware service callers, effective
configuration resolver, source-fingerprint inputs, publication-section order
codec, artifact freshness rules, and immutable view/download endpoints. Search
for every old release/default/profile/section field and confirm the obsolete
runtime path is removed rather than retained as a compatibility branch.

When preparation or package behavior changes, use the relevant Press and
persistence verification in [`press-production.md`](./press-production.md) and
[`persistence-migrations-import.md`](./persistence-migrations-import.md).
Do not claim vendor upload acceptance, formal accessibility certification, or
third-party reader compatibility from compilation or Lorekeeper’s internal
validation alone. Browser UI, screenshots, Playwright, and manual publishing
checks require explicit authorization.
