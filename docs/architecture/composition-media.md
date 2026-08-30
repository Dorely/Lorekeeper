# Composition and media architecture

## When to read

Read this chapter when a change touches project images, image generation or
editing, canonical entity visuals, Figures, page setup, Designed Pages,
composition scenes or variants, cover-canvas primitives that are shared with
Pages, fonts, preview rasterization, image deletion protection, crop or fit
behavior, geometry-bound generation, image-bearing assistant context, or any
browser canvas/runtime that presents those capabilities. Read it with
`publishing-model.md` when the change concerns publication sections, Core or
release cover materialization, or edition-specific geometry. Read it with
`press-production.md` when the change affects output rendering, physical
product geometry, PDF/EPUB artifacts, or production validation. Read it with
`persistence-migrations-import.md` when a new composition/image/font field,
asset relationship, history dependency, or migration boundary is involved.

This chapter describes the current runtime contract, not a proposal for a
second document or rendering model. The semantic manuscript remains the
searchable and accessible source of meaning. Composition stores an authored
visual arrangement and media services store reusable assets; neither boundary
is allowed to become an opaque replacement for manuscript semantics.

## Scope and ownership

The composition/media boundary owns the project image library, image jobs and
provider-independent image workflow, canonical visual associations, project
fonts, project authoring page setup, semantic Figure presentation, Designed
Page semantic fragments and exact-geometry composition variants, visual canvas
interaction, canvas previews, and the shared layout contracts used by Pages,
covers, assistants, EPUB projection, and Press requests. Its services are the
only owners of the corresponding validation, revision, asset-ownership,
geometry, and preview rules.

`IProjectImageService` owns reusable project assets and local crops. It is the
authority for metadata, image bytes, usage projections, upload, crop/reuse,
and deletion guards. `IProjectImageJobService` owns durable generation/edit
job records, prompt/audit metadata, received partial-image artifacts, and
explicit partial promotion. `IProjectImageGenerationRuntime` owns FIFO
execution, retry/cancellation propagation, ordered partial capture, and job
notifications. Provider transport remains behind `IProjectImageProvider`;
the application never lets a provider-specific response become the asset or
placement contract.

`IEntityVisualExampleService` owns ordered associations between eligible graph
entities and project images. An association is canonical visual evidence with
an origin and source provenance; it is not a general image-library tag.
`EntityVisualContextService` and `IReferenceVisualService` build bounded,
read-only visual context for assistants after validating project/reference
scope. Canonical visual evidence can be read from a directly referenced
project, but foreign images cannot become active-project mutation or placement
targets.

`IProjectPageSetupService` owns one project-level authoring width, height,
margins, body typography, preset, and revision. That setup drives authoring
previews, Figures, new Designed Pages, and authoring-time generation. It does
not create a publication release and is not release-owned. A setup change
reflows the reusable Core cover and every active Designed Page layout in one
project-scoped transaction while preserving objects and bindings and advancing
affected revisions.

`ICompositionService` owns Designed Page semantic fragments exactly once and
revisioned `PageCompositionVariant` scenes by exact geometry fingerprint. The
canvas is an authoring surface over those contracts. It must not persist HTML,
DOM coordinates, or a second manuscript body. `ICompositionCanvasPreviewService`
owns transient complete-scene inspection for Pages and covers; previews are
not project images and do not enter the library.

The owning publication services remain authoritative for Core/release
publication sections, release covers, package readiness, and production
artifacts. This chapter supplies their shared media, canvas, geometry, and
preview primitives; it does not own publication claims or vendor validation.

## Current architecture and invariants

Project images are reusable assets with bytes, media metadata, crop lineage,
alt text, prompt/source metadata, masks, and placement relationships. Generated
or edited work first creates one unattached project image through the shared
assistant workflow. Placement into a Figure, Designed Page, cover, entity
visual association, or chat context is a separate revision-safe operation
using the completed image ID. Generation never embeds a destination, creates a
partial placement, or silently crops an output to satisfy a target.

The image generation boundary distinguishes a free-standing library request
from a layout-bound request. Every free-standing generation or edit defaults to
a moderate provider-valid raster with the current Core Book page aspect; this
page-shaped default is included in the compiled prompt and provider request but
does not make the asset layout-bound. Outline and Images requests omit concrete
placement geometry. `IProjectImageDefaultRasterResolver` owns the shared Core
Book page-to-provider-raster default used by prompt compilation, direct job
creation, and the manual Images UI. A `LayoutGenerationTargetDescriptor` is
used only when art must honor physical regions such as a page, frame, or cover.
It carries exact aspect, provider-valid final-DPI recommendation plus moderate
provider-valid default raster guidance, effective-DPI expectation, geometry
fingerprint, and named trim, bleed, safe, gutter, barcode, cover, or reserved-text
regions. An assistant may select a proportional larger provider
size when the requested quality or effective DPI warrants it; explicit
overrides are accepted only when provider-valid and, for layout-bound targets,
when they preserve the server-owned aspect. It is guidance and prompt context,
not an acceptance rule imposed on provider pixels.

Provider output is stored without layout cropping or resizing, apart from
supported-format normalization such as WebP to lossless PNG. The result
reports `rasterMatched` separately from `aspectMatched`; any explicit
requested raster is compared with actual decoded pixels even for a
free-standing request, and mismatches are surfaced as warnings. Generation
does not infer publication DPI readiness; placed-image validation owns DPI
diagnostics. The provider-output byte boundary is separately configurable and
defaults to 64 MiB.

The manual Images Generate panel uses that Core Book page raster by default and
still offers explicit provider-valid raster overrides. Authors can select
existing project images or upload new project-library images as ordered
generation references, give each reference a visible role, and remove it before
queueing. The compiled reference manifest and the exact ordered image IDs are
persisted on the job and sent to the provider; merely uploading or selecting a
reference does not create a generation job or an entity association.

`IProjectImageService.ResizeAsync` creates a new unattached, source-linked
`Resized` asset at an exact provider-valid raster using deterministic
SkiaSharp sampling. It does not invent visual detail and records the source,
target, interpolation, and raster storage in provenance. A larger version of
an existing image uses the ordinary source-driven edit workflow: the original
image is supplied directly to the provider and the prompt describes the
complete larger framing. Wider targets ask for natural extension to the left
and right, taller targets above and below, and effectively unchanged aspects
outward on all sides. This is model-driven editing and does not guarantee
exact preservation of existing image detail. A regional guide remains an
exception for genuinely localized changes or changes that cannot be described
reliably in words. The resulting `Edited` asset remains unattached and linked
to its source, while provider-versus-final raster details are recorded without
selecting a materially different fallback implicitly.

Every valid streamed partial is retained as exact job-owned PNG, JPEG, or WebP
bytes, identified by output, request attempt, and provider partial index. A
successful final output associates its partials with that output image; partials
from interrupted, failed, or cancelled outputs remain on their job. The Images
workspace exposes them behind the owning image or job card. Promotion is an
explicit atomic transfer: the partial is normalized through the ordinary image
asset boundary, stored as a separate unattached project image with provenance,
and removed from the partial collection. It never inherits entity associations
or a placement. Deleting a final image also deletes every unpromoted partial
still associated with that image; already promoted images and unrelated orphan
partials remain independent.

The shared prompt composer gives generation and editing the same spatial
discipline. A reserved or quiet region must be explicit when copy needs space;
the rest of the frame must contribute purposeful subject, setting, depth,
scale, atmosphere, visual flow, or other meaningful information. The Images
surface owns concept-art iteration and approved Visual Direction. Outline is
restricted to explicit canonical entity appearance work. Editor consumes a
completed image for a Figure or Designed Page. Publish consumes it for a
publication section or cover. Each surface uses the same bounded inspection
and revision rules, while mutation scope remains surface-specific.

Entity visual examples are ordered associations, not copied image records.
They carry association origin and source provenance and are reused by image
prompting, Editor context, Research/Ingest promotion, and entity indexing.
Canonical-reference reads are bounded, deduplicated, and validated against
the owning entity and project/reference scope. General-library images are not
continuity evidence. Changing visual ownership or reference semantics must
therefore be traced through all of those consumers and through search/vector
projection where applicable.

The semantic manuscript stores Figure blocks with stable IDs, project image
IDs, captions, alternative/decorative decisions, language and semantic roles,
flow/wrap/width/spacing/fit/crop intent, bleed, page-break, and caption
placement. A Figure is geometry-neutral: the source raster is fitted at
layout/render time and remains reusable. A Figure's semantic content and
accessibility data participate in manuscript validation, search/plain-text
projection where appropriate, EPUB projection, Press requests, and deletion
guards. Image deletion refuses live Figure, Designed Page, cover, publication,
or other placement references and repeats the authoritative lookup inside its
write transaction so stale UI cannot remove a protected asset.

Figure captions share one manuscript typography contract across Edit, Read,
EPUB, and Press. Non-overlay captions use the shared muted text color. Overlay
captions use the shared foreground color, translucent backing color/opacity,
and vertical/horizontal padding; Press emits the backing as vector paint below
selectable caption text. A paragraph Book Text Style or direct paragraph
presentation on a Figure styles and indents its caption, not the image-bearing
wrapper.

Designed Pages contain semantic fragments and a scene, not duplicate prose.
Text objects bind stable block/range identities; a newly authored text frame
creates its own semantic block and writes ordinary manuscript inline marks.
The canvas intentionally hides reusable-content binding, block IDs, and
character-offset controls from authors. Legacy range-based layouts remain
losslessly readable and are materialized into frame-owned blocks when directly
edited, subject to same-role validation. A mixed-role legacy frame fails
closed. Meaningful content that cannot be placed remains in an unplaced tray
with a blocking diagnostic rather than being discarded.

Figures and Designed Pages are direct owning-service mutations in live SQLite.
Review Edits compares those semantic changes from Git HEAD to live state rather
than flattening them into text or a second manuscript representation. Pending Review
shows Figure metadata with editable captions and Designed Page visual
before/after previews; structural insertions, moves, formatting, and scene
changes remain atomic and reviewable. Historical Undo restores the approved
parent value into live state as a normal pending reversal.

During an unresolved Contest, candidate drafts preserve Figure semantics,
captions, Designed Page references, and scene structure independently. The
project-wide Editor lock blocks manual and assistant layout, Figure, and
Designed Page mutations until the contest is resolved or discarded; authorized
candidate-draft edits and resolution remain available.

The shared scene vocabulary includes image, text, rectangle, ellipse, line,
and group objects. Visibility, opacity, locks, grouping, object styles, named
regions, z-order, and logical reading order have runtime meaning. Authors do
not manage internal renderer stacking planes. New images enter behind content
but above older images; new text and shapes enter at the front. Front/back
actions move to the actual applicable stack edge. Group transforms, opacity,
visibility, clipping, rotation, and z-order must resolve consistently in the
canvas, previews, generation-target inspection, EPUB projection, and Press.

Active authoring variants always use the current project page setup and retain
only single-page or facing-spread mode. A variant is selected by exact
geometry fingerprint. Opening an older mismatched variant repairs it to the
current setup; changing setup reflows active layouts transactionally. Publish
may copy an authoring scene into a separate exact edition variant for review,
but a publication release never governs authoring geometry. A two-leaf
`EditionLeaves` spread is split into sequential leaves at output; digital
output may preserve a wide or independent page box according to Core Book
presentation settings, while physical output accepts only valid trim leaves or
two-leaf EditionLeaves variants.

Pages and covers share image-frame layout rules. Fill-canvas uses the complete
surface. Proportional fitting crops to fill; explicit constraint removal
permits deliberate stretching. Crop repositioning changes only the image
position inside a fixed frame and remains an explicit mode until finished or
another object is selected. Frames retain raster aspect by default. Pointer
interaction uses measured native image geometry rather than a generation
request's guessed aspect. Press clips valid out-of-surface paint to the page;
the authoring editor reports overflow and clipping without silently resizing
art.

The visual editor uses `CompositionVisualEditorShell`: a one-line view
toolbar, largest practical canvas, fixed contextual bottom controls, and an
on-demand details drawer for accessibility, reading order, exact geometry,
diagnostics, and secondary settings. It does not expose a permanent inspector
or layer manager that steals canvas width. Saves are serialized per mounted
workspace. Each save uses an immutable semantic/scene snapshot, adopts returned
revisions before the next queued save, and clears dirty state only when no
newer local mutation exists. Revision-checked assistant mutations acquire the
project mutation lease, reread tracked state, commit, and return the
authoritative snapshot. Stale or failed mutations cannot leak tracked entities
into a later operation.

`ICompositionCanvasPreviewService` renders the exact selected scene revision
as one transient PNG surface. Clean mode returns the composed artwork;
annotated mode adds safe/trim/gutter/center/bleed/object indicators plus
overflow and clipping diagnostics. Cache keys include semantic and scene
revisions, referenced image/font bytes, and any resolved cover-copy bindings.
Cover previews resolve canonical title, subtitle, author, spine, and back-copy
bindings at render time without changing the persisted scene; page previews
retain their existing semantic-manuscript resolution. Editor and Publish
persist the preview through their transcript visual boundary so the exact image
inspected by a model is visible in the tool chip and survives transcript
reload. A preview is an inspection gate, never a new project image. Assistants
inspect the same current revision before another visual mutation, then validate
and inspect a clean preview for final verification.

Project fonts include bundled OFL families and imported static TTF/OTF faces.
The project font catalog owns validation, face resolution, browser URLs, and
deletion guards. Imported-font deletion is blocked while a paragraph, saved
style, page, cover, or retained in-process Undo/Redo stream references its
family. Version-history snapshots retain the canonical font bytes as ordinary
Git blobs, but do not create a live-asset deletion blocker.
Browser, Read preview, EPUB, cover, and Press all stage the same referenced
faces rather than substituting a machine font. Font changes affect manuscript,
composition, Core/release fingerprints, and artifact freshness.

## Key files and file families

| Path or family | Primary responsibility |
|---|---|
| `Lorekeeper/Images/IProjectImageService.cs` / `ProjectImageService.cs` | Reusable image-library reads, uploads, crops, deterministic exact resizing, metadata, usage projections, and deletion guards. |
| `Lorekeeper/Images/IProjectImageJobService.cs` / `ProjectImageJobService.cs` | Durable generation/edit job records, streamed partial artifacts, explicit promotion, structured briefs, provider audit fields, output validation, and diagnostics. |
| `Lorekeeper/Images/AgentProjectImageWorkflow.cs` | Assistant generation/edit boundary, terminal-state waiting, reconnectable jobs, target diagnostics, and unattached output semantics. |
| `Lorekeeper/Images/ImagePromptComposer.cs` / `ProjectImageDefaultRasterResolver.cs` | Structured generation/edit briefs, reference labels, reserved regions, spatial guidance, rendered-text policy, and the shared Core Book page raster default. |
| `Lorekeeper/EntityVisuals/` | Canonical entity-image associations, visual context, bounded reference reads, and provenance. |
| `Lorekeeper/Composition/CompositionService.cs` | Revision-aware Designed Page aggregates, exact variants, scene validation, autosave snapshots, and geometry-bound descriptors. |
| `Lorekeeper/Composition/CompositionSceneResolver.cs` | Group flattening, object visibility/z-order semantics, and shared overlap validation. |
| `Lorekeeper/Composition/CompositionCanvasPreviewService.cs` | Exact transient clean/annotated page and cover canvas rasterization. |
| `Lorekeeper/Composition/ProjectPageSetupService.cs` | Project authoring geometry, typography, setup revisions, and transactional reflow. |
| `Lorekeeper/Composition/CompositionAgentPayloads.cs` | Bounded assistant reads and revision-safe scene/object/style patch envelopes. |
| `Lorekeeper/Fonts/` and `ProjectFont*` models | Bundled/imported font catalogs, static-face validation, bytes, URLs, and live/in-process-history use guards. |
| `Lorekeeper/Components/Pages/Projects/DesignedPageWorkspace.razor` | Canvas-first authoring interaction, variant selection, autosave, diagnostics, and history refresh. |
| `Lorekeeper/Components/Pages/Projects/CoverCompositionWorkspace.razor` | Shared visual shell for cover editing; publication ownership remains in Publish services. |
| `Lorekeeper/wwwroot/js/composition-workspace.js` | Measured stage, pointer capture, image geometry, text editing, selection, and formatting bridges. |

## Related chapters

- [`manuscript-authoring.md`](./manuscript-authoring.md) owns semantic manuscript
  writes, styles, annotations, authoring history, and Editor/revision behavior.
- [`publishing-model.md`](./publishing-model.md) owns Core Book, releases,
  publication sections, edition content, Publish UI, and effective projections.
- [`press-production.md`](./press-production.md) owns physical products,
  covers, native rendering, PDF/EPUB validation, artifacts, and packages.
- [`persistence-migrations-import.md`](./persistence-migrations-import.md)
  owns SQLite/EF persistence, migrations, recovery, import/export, and durable
  live-asset relationships. Manual Undo/Redo dependency retention is process
  memory; durable version history is the separate Git snapshot boundary.
- [`version-history-sync.md`](./version-history-sync.md) owns deterministic
  image/font blob capture and restore; this chapter owns the live asset and
  scene semantics those snapshots represent.
- [`assistants-chat.md`](./assistants-chat.md) owns shared chat turns, direct
  assistant mutation policy, assistant context, contest drafts, and model-visible
  visual preview protocol.

## Relevant verification

For normal source changes, run `dotnet build Lorekeeper.sln`. The build must
compile the application and package the same owned Press runtime used by the
app. For composition/media changes, also inspect the relevant source paths and
run the project-appropriate native Press checks when output contracts change:

```powershell
dotnet build Lorekeeper.sln
dotnet run --project Lorekeeper --launch-profile http
cd Lorekeeper.Press
cargo fmt --check
cargo clippy --all-targets -- -D warnings
cargo test --locked
```

Terminate the HTTP host after its startup check. Do not perform browser,
Playwright, screenshot, or manual UI checks unless explicitly requested.
Image generation, provider calls, and platform-specific behavior are not
validated by compilation alone. Report those integrations as unexercised
unless the relevant provider and target platform were actually used.

When image, Figure, composition, font, or history ownership changes, inspect
the complete diff and search for every old field/name and every deletion path.
The minimum static review should cover `IProjectImageService`,
`IManuscriptService`, `ICompositionService`, `IProjectFontService`, image
endpoints, EPUB/Press request assembly, in-process history dependency
retention, version-history blob validation, and the owning persistence
migration. Confirm that no image bytes or font secrets are
copied into unrelated assistant payloads and that no stale asset can be
deleted through a UI-only check.
