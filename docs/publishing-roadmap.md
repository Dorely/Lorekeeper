# End-to-end book publishing roadmap

Last updated: 2026-08-03

## Destination

Lorekeeper will become an AI-native, end-to-end book creation system: authors
can plan, draft, edit, design, proof, and produce validated publication files
without routing the manuscript through a separate word processor, formatter,
PDF converter, or preflight application.

Narrative coherence remains the product's foundation. Publishing expands that
foundation from story intelligence into a complete book workflow; it does not
replace it.

This document is the planned delivery sequence. It is not a statement of current
implementation. Current boundaries remain in
[`architecture.md`](architecture.md), and research evidence lives under
[`research/`](research/README.md).

## Status vocabulary

Every roadmap capability uses one status:

- `Researched` — evidence and a candidate product approach are documented.
- `Planned` — scope, ownership, dependencies, and acceptance evidence are
  defined.
- `In development` — an active implementation feature owns the capability.
- `Preview` — users can exercise it, but its certification or full parity gate
  is incomplete.
- `Verified` — automated, integration, and real-world acceptance evidence for
  the declared scope has passed.
- `Deferred` — deliberately outside the current delivery scope.

No output is called “print-ready,” “accessible,” or vendor-compatible until the
corresponding verified gate passes. A PDF/X label is limited to the exact
versioned structural profile and evidence named here; it never implies vendor
acceptance or human proof.

## Cross-cutting completion rules

These rules apply to every phase and feature:

### Human and assistant parity

A user-visible capability is incomplete until the appropriate assistant can:

- discover that it exists and read its current state;
- perform every safe operation exposed by the UI;
- receive the same validation and diagnostics;
- use stable IDs and revision tokens;
- propose reviewable mutations through the owning application service;
- observe the accepted result and resulting stale/ready state.

The UI and assistants do not maintain parallel business logic. External
submissions, purchases, rights declarations, proof approval, and other
consequential actions always require explicit user approval.

### Safe data evolution

- Back up and validate local data before destructive transformation.
- Use forward EF migrations and versioned transformation journals.
- Prove content equivalence with counts, normalized projections, and hashes.
- Fail closed with a visible recovery path.
- Support old project exports through isolated import adapters.
- Remove superseded runtime paths after cutover; do not leave indefinite
  dual-write or compatibility branches.

### One source, Core intent, multiple releases

The semantic manuscript is authoritative. PDF, EPUB, HTML previews, search
fragments, page maps, and cover dimensions are versioned projections. Core Book
owns shared publication intent and a private reading copy. Optional releases
inherit Core live and own only their product-specific choices, identifiers,
proofs, packages, and publication artifacts.

### In-app production

A verified path must render, validate, preview, and package inside Lorekeeper.
Development-only validators and vendor upload portals may be used to certify the
implementation, but users are not required to install or operate another
conversion/preflight tool.

### Verification

- Tests and fixtures are part of the feature when correctness is data-, layout-,
  or conformance-sensitive.
- Documentation and the roadmap status change in the same commit as the feature.

## Phase overview

| Phase | Outcome | Current status |
|---|---|---|
| 1. Publisher-ready novel foundation | Structured editing, Core Book/reading copy, and independently validated paperback/EPUB/PDF release artifacts for a narrow certified scope | Implemented; native release-matrix verification pending |
| 2. Professional editing and proofing | Track changes, comments, comparisons, house style, and page-proof workflows | Researched |
| 3. Illustrated and picture-book design | Semantic Figures, Designed Pages, cover composition, color/bleed, fixed-layout EPUB, and tagged Digital PDF | Implemented core; advanced DTP additions deferred |
| 4. Nonfiction and reference books | Notes, citations, tables, figures, cross-references, generated references, equations, and code | Researched |
| 5. Publisher operations | Imprints, contributors, rights, identifiers, ONIX, proofs, catalog, team audit, and controlled distribution | Researched |

## Phase 1 — publisher-ready novel foundation

Status: `Lorekeeper validated; native release-matrix verification pending`

### Certified outcome

An existing or new prose-first project can be edited as a structured manuscript
and exported as:

- a black-and-white paperback interior;
- a color full-wrap paperback cover;
- a reflowable EPUB 3;
- a manifest and actionable preflight report.

The initial vendor profiles are Amazon KDP and IngramSpark. KDP and Ingram files
are separate edition artifacts. The UI previews actual generated PDF bytes, not
an approximation rendered only with browser CSS.

The initial certified language scope is English and explicitly tested
Latin-script, left-to-right locales using pinned fonts and hyphenation
dictionaries. Language marks remain semantic, but preflight blocks a certified
export when the manuscript uses an unverified script, direction, shaping path,
or dictionary. Phase 3 owns the later complex-script expansion.

Hardcover/dust jackets, rich nonfiction, direct retailer submission, and collaborative editing
are explicitly outside this phase.

### Implementation prerequisite

Automated fixtures and test-project work were explicitly authorized by the user
on 2026-07-30. Every data-, layout-, and conformance-sensitive feature must add
and maintain the relevant tests; this authorization does not relax the
feature-by-feature migration, review, verification, or commit gates.

### Phase 1 feature sequence

The numbered spike records below are retained as historical research. The owned
renderer, application cutover, migration, packaging, assistant parity, and
documentation are delivered and reviewed as one coherent feature.

#### 1. Renderer and conformance spike

Status: `Researched` — executable candidate rejected on 2026-07-30

Build a disposable, test-focused `Lorekeeper.Press` prototype to prove or reject
the Typst + krilla + moxcms direction before changing user data.

Deliverables:

- versioned request/response protocol draft;
- representative 6 × 9 in novel interior and page-count-dependent wrap cover;
- PDF 1.7 KDP output;
- experimental PDF/X-1a:2001 Ingram output;
- font, image, color-space, page-box, output-intent, transparency, and forbidden
  feature inspection;
- independent rendering/text extraction;
- external Adobe Acrobat Pro Preflight verification against the named
  PDF/X-1a:2001 profile and normative ISO 15930-1:2001 revision, recording the
  validator/profile versions, fingerprint, report, and artifact hash;
- deliberate invalid fixtures that fail the same external profile;
- vendor upload-preflight results;
- dependency, binary-size, startup-time, and release-level license report with
  exact tags/commits, transitive dependencies, license-file hashes, notices, and
  shipped-asset obligations;
- an architecture decision recording go, fallback, or reduced scope.

Gate: no claim of PDF/X support and no persistent-model work until the spike
passes. If it fails, evaluate the documented fallbacks explicitly.

Assistant parity: expose no end-user assistant tools for a disposable spike.
The resulting protocol must nonetheless be designed for future structured
assistant diagnostics.

Outcome: the pinned Typst 0.15.1 + krilla 0.8.2 + moxcms 0.9.0 candidate
proved deterministic PDF 1.7 interior/cover generation but cannot emit
PDF/X-1a:2001. It was rejected as the sole press renderer. No Acrobat or vendor
claim was made, no production runtime or persisted data changed, and Feature 2
remains blocked. Evidence and the accepted decision are in
[`research/press-renderer-conformance-spike.md`](research/press-renderer-conformance-spike.md)
and
[`decisions/0001-reject-typst-as-sole-press-renderer.md`](decisions/0001-reject-typst-as-sole-press-renderer.md).

#### 1a. PDF/X fallback conformance spike (historical)

Status: `Superseded` — reduced-scope decision retired on 2026-07-31

Repeat Feature 1's conformance and release gates with WeasyPrint 69 as the lead
free fallback. Its current BSD-licensed API exposes PDF/X-1a, PDF/X-3, PDF/X-4,
CMYK colors, and custom ICC output intents, but those API capabilities are not
accepted as conformance evidence.

Additional deliverables:

- select a vendor/paper-appropriate CMYK ICC profile only after its
  redistribution terms, fingerprint, notices, and security scan are recorded;
- compare its prose pagination, widow/orphan, recto, page-number, running-head,
  hyphenation, and cover-placement control against the Typst fixture;
- contain HTML/CSS/file/network inputs so imported or assistant-authored content
  cannot read local files or fetch undeclared URLs;
- measure Python/native packaging, cold/warm startup, binary footprint, and
  cross-platform release obligations;
- run the named Acrobat PDF/X-1a profile, deliberate invalid fixtures, KDP and
  Ingram vendor upload preflights, and record all artifact hashes.

Gate: Feature 2 remains blocked until this fallback passes or a subsequent
candidate is explicitly accepted with a reduced certified scope.

Outcome: exact-pinned WeasyPrint 69 generated a contained Windows x64 PDF 1.3
interior and wrap cover that declare PDF/X-1a:2001 and pass Lorekeeper's
structural inspection. The stock WeasyPrint PDF/X-1a mode targets the wrong
2003 revision, so the accepted adapter is exact-version-pinned and must be
revalidated on every renderer change. The redistributable basICColor fixture
profile and dependency graph are fingerprinted. External Acrobat/vendor,
physical-proof, macOS packaging, and complete native-notice gates remain open;
therefore no conformance or vendor claim is made. This decision temporarily
unblocked implementation, but its distribution and
machine-state assumptions did not satisfy the original production requirement.
ADR 0003 removes the WeasyPrint runtime entirely. See
[`research/weasyprint-pdfx-fallback-spike.md`](research/weasyprint-pdfx-fallback-spike.md)
and
[`decisions/0002-accept-weasyprint-for-preview-press-runtime.md`](decisions/0002-accept-weasyprint-for-preview-press-runtime.md).

#### 1b. Lorekeeper-owned Press renderer

Status: `Implemented; Windows verified locally, native macOS matrix pending`

On 2026-07-31 Lorekeeper replaced both candidate paths with the owned Rust
`Lorekeeper.Press` subproject. Protocol v5 now carries structured Figures,
Designed Pages, covers, custom fonts, semantics, and per-page Digital PDF boxes. Requirements and black-box
conformance fixtures were written and run against the Typst implementation
before production work began; the expected failures were recorded in the work
log. The current suite independently parses raw objects, streams, fonts, page
trees, boxes, colors, output intents, metadata, annotations, security state,
and image XObjects.

The KDP profile emits PDF 1.7. The Ingram profile emits restricted PDF 1.3 with
PDF/X-1a:2001 identification, registered CGATS21 CRPC1 output intent,
ICC-converted images, bounded CMYK/gray page paint, flattened alpha, embedded subset fonts,
ToUnicode maps, and a 240% ink ceiling. Staged assets are declared and hashed;
output is promoted atomically after separate post-write inspection. The same
locked native runtime, fonts, profile, notices, SBOM, and hash manifest are
packaged automatically in Debug and Release. Runtime execution has no machine
tool fallback or inherited `PATH`.

ADR 0003 records the decision. The complete requirements, dependency/license
scope, security boundary, and future phases are in
[`research/lorekeeper-press-requirements.md`](research/lorekeeper-press-requirements.md).

#### 2. Structured manuscript schema and migration

Status: `Implementation complete; release validation pending`

Implemented in the application on 2026-07-30 and subsequently advanced to
manuscript schema v4 by Phase 3. Stable IDs, revisions, migration/recovery,
assistant operations, contest/revision, import, and projection paths use the
shared manuscript service. The authorized fixture suite covers codec and mark
behavior, WAL migration/restore, versioned visual/authoring/Core cutover, and current v17
serialization. This status does not close the release gate below:
production-like copied-database rehearsal and the remaining historical/export,
permission, retention, failure-injection, and projection evidence are required
before distributing this migration to existing users.

Replace plain chapter-body runtime ownership with versioned semantic documents
and a single manuscript application service.

Deliverables:

- versioned schema and stable block IDs;
- lossless text projection and normalized import codec;
- transactional domain commands with revision tokens;
- WAL-safe backup, migration journal, expand/transform/validate/contract
  migrations, recovery screen, and restore drill;
- conversion of every live and historical body-bearing record;
- lossless migration of legacy page-layout text boxes to stable manuscript block/range
  references while preserving element identity, geometry, typography, z-order,
  and reading order;
- remapping of legacy anchored-image paragraph index/hash anchors to stable block
  IDs with hash validation and fail-closed ambiguity handling;
- project export v8 for structured manuscripts and stable visual-layout
  references, with isolated v1–v7 import adapters;
- the temporary textarea adapter used during the cutover (removed when Feature
  3 installed the schema-driven editor), which translated revision-aware saves
  through the manuscript service and never wrote `Chapter.Body`;
- rebuilt search/vector/graph and visual-content projections;
- removal of direct obsolete runtime body access after cutover.

Assistant parity:

- bounded block reads;
- insert/replace/delete/move/split/merge block operations;
- inline-mark and style-role operations;
- semantic diffs, expected revisions, and reviewable changes;
- then-existing page-layout and anchored-image assistant operations resolve and
  mutate the same stable blocks/anchors as their UI services;
- migration preflight, journal, validation-report, and recovery-state reads,
  with safe retry diagnostics; restore remains an explicit user-confirmed
  action;
- complete removal of obsolete line/whole-string mutation tools.

Gate: fixture databases, interruption injection, hash equivalence, multi-text-
box page-layout fixtures, anchored-image fixtures, import/export, index, projection,
and restore tests pass before any existing database is contracted.

#### 3. Semantic rich-text editor and Book Text Styles

Status: `Implementation complete; release validation pending`

Replace the textarea experience with a ProseMirror-based editor that uses the
new service boundary.

Deliverables:

- paragraph, structurally persistent heading levels 1-6, intentional hard line
  breaks, scene break, block quote, list, link, and project-image figure nodes
  with required alt text and captions;
- emphasis, strong, small-caps intent, superscript/subscript, language, and
  character-style marks;
- user-facing Book Text Styles with edition-independent paragraph/character
  semantics and generated stable keys;
- semantic Markdown/EPUB projection so authored block/mark/figure intent is not
  flattened at the existing publishing boundary;
- project export introduced semantic style round-tripping in v9; the current
  v17 format also carries manuscript-v4 project page setup, compositions,
  Core Book, sparse release overlays, cover scenes, and complete custom-font binaries, with isolated older
  adapters;
- removal of the temporary textarea adapter after every editor workflow uses
  schema-driven transactions (implemented);
- paste/import normalization with warnings;
- undo/redo, keyboard behavior, special characters, find/replace preview,
  document outline, counts, autosave/conflict handling, and accessible focus;
- context, contest, revision-agent, visual-layout, and review workflows
  operating through structured commands. Whole-block visual text edits,
  insertions, deletions, and reading-order moves preserve stable references;
  partial-block range edits deliberately fail closed and are routed to the
  manuscript editor. Visual layouts use monotonic revisions and project-scoped
  image validation so stale editors cannot reintroduce deleted assets.

Assistant parity: every block/mark/style command, structural search, validation,
and normalization diagnostic is available to the Editor assistant through the
same services.

Gate: no raw HTML authority, no direct database mutation, and no divergence
between manual and assistant edits.

#### 4. Core Book, publication releases, and book structure

Status: `Implementation complete; release validation pending`

Provide one shared Core Book and layer optional product releases over it.

Deliverables:

- one revisioned `PublicationBook` per project for shared metadata, content,
  matter, placements, presentation, and the reusable front cover;
- optional paperback, EPUB ebook, and PDF ebook releases with live field-level
  inheritance, explicit-empty/reset semantics, and sparse collection overlays;
- release and Core-specific migration journals and recovery boundaries independent of
  the completed manuscript migration;
- conversion of the historical optional `PublishProfile` into a release,
  followed by a protected Core migration that chooses that historical default
  only as seed data and then removes default-release runtime state;
- release-owned format, destination, internally managed profile, trim, binding,
  paper, ink, bleed, identifier, status, artifacts, packages, and proofs;
- installed-profile defaults for new paperbacks (6 × 9 in, 0.75 in margins,
  11 pt body text, and 1.4 line height);
- Core content and order with release rows only for changed inclusion/order;
- generated title, copyright, and contents pages plus a front/back-matter
  builder for dedication, epigraph, acknowledgments, about-author, also-by,
  references, and custom matter; generated page kinds are reserved so they
  cannot be duplicated by user-authored matter;
- project-owned Book Text Styles with sparse release overrides;
- artifact/proof staleness derived from content, settings, assets, renderer, and
  profile versions;
- edition clone, immutable archive, compare, and audit history; archived
  artifacts remain readable/exportable and changes continue through a clone.
- a private tagged Core reading PDF with no publication or ISBN claim;
- centralized safe release presets and one reconnectable **Prepare files** job;
- project export introduced release rows in v10; current v17 exports Core Book
  plus sparse overlays and retains isolated older import adapters.

Assistant parity: the Publish assistant operates Core by default, can create and
customize releases through compact revision-safe tools, receives the complete
outline every turn, and can search bounded chapter/research/project sources.
All assistant image generation/editing waits for a terminal unattached project
image and uses a separate focused placement call; cover editing is embedded
beside the assistant with a live structured canvas. It cannot select raw
profiles, invent ISBNs, or approve proofs. Destructive recovery remains
user-confirmed.

Gate: Core works without releases; inherited values update live; explicit
overrides remain stable; all release products can prepare files while retaining
independent identifiers and proof state.

#### 5. Deterministic novel typesetting and real PDF output

Status: `Implemented; native release-matrix validation pending`

Implemented in the application and replaced with the owned renderer on
2026-07-31. Paperback editions queue contained native render jobs with restart
recovery, progress, cancellation,
timeout/process-tree containment, safe job roots, response validation, and
SHA-256 verification before immutable PDF bytes are persisted. Semantic block
anchors produce page maps; actual PDF bytes are served with range requests to
the embedded Chromium PDF viewer; artifacts report current/stale state and two
renders can explain page and block movement. The Publish assistant has the same
request, cancel, inspect, page-map, and comparison surface. Runtime execution is
fail-closed: the UI, assistant, service, and worker share an app-owned bundle
resolver that verifies an exact platform-matched file/hash inventory before
queueing. Debug and Release build the same locked runtime and fail-closed
license/asset inventory. A real .NET-to-Rust fixture renders with `PATH` absent.
Native macOS x64/arm64 workflows build on matching runners; their execution
remains a release-matrix acceptance item.

Integrate the proven press sidecar as a production runtime.

Deliverables:

- pinned cross-platform sidecar binaries and notices;
- job queue, progress, cancellation, crash containment, and safe job
  directories;
- body typography, hyphenation, justification, widow/orphan rules, keep rules,
  recto chapter starts, intentional blanks, page numbering, running heads, and
  front-matter numbering;
- actual PDF viewer with page thumbnails, zoom, search, and stale-state display;
- page map from manuscript blocks to edition pages;
- deterministic artifact storage and SHA-256 manifest;
- render comparison explaining pagination changes.

Assistant parity: Publish tools request/cancel renders, inspect page maps and
artifacts, compare versions, and explain diagnostics; they cannot fabricate a
successful render state.

Gate: reproducible pagination fixtures and independent renderer snapshots pass
on every packaged runtime.

#### 6. Full-wrap cover builder

Status: `Implemented; physical proof remains a user record`

Implemented in the application on 2026-07-30 for the initial prose-paperback
scope. Each edition owns revision-checked cover copy, background, focal intent,
barcode behavior, and a calculated template derived from the latest interior
page count, trim, paper caliper, bleed, vendor/profile version, safety zone, and
barcode reserve. Geometry changes invalidate acknowledgement. ISBN-13 checksum
validation, true EAN-13 bars/quiet zones, separate KDP overlay and Ingram
embedded-barcode behavior, conditional spine text, actual cover PDF artifacts,
and complete assistant reads/writes are wired through shared services. Physical
template/proof measurements and vendor acceptance remain separate evidence. KDP
and Ingram output composite selected normalized PNG artwork with focal controls.
Ingram artwork is converted through the bundled registered CMYK profile,
flattened, and capped at 240% total ink.

Turn existing project images into edition-aware dedicated cover sources.

Deliverables:

- vendor/product template from final page count, trim, paper, and binding;
- back, spine, and front regions with bleed, safety, fold, and barcode reserve;
- EAN-13 barcode generation and validation from a user-supplied ISBN, including
  checksum, quiet zone, 100% black, background, size, and placement rules;
- separate vendor behavior: Ingram output always contains the required barcode,
  while KDP can either contain Lorekeeper's barcode or reserve the area for
  KDP's optional overlay;
- editable title/subtitle/author/spine/back-copy text;
- direct image crop-position controls, background handling, font licensing diagnostics,
  and metadata consistency checks;
- conditional spine text based on profile rules;
- actual cover PDF preview and artifact.

Assistant parity: complete object/property reads and writes, template
recalculation, copy suggestions, image operations, and diagnostics through
shared services.

Gate: cover geometry is invalidated and recalculated whenever a dependency
changes; a user must acknowledge material layout changes before proof approval.

#### 7. Preflight, EPUB 3, and publication package

Status: `Lorekeeper validated; reader/vendor/proof results remain separate`

Implemented in the application on 2026-07-30. Edition-scoped preflight now
checks metadata, content, ISBN/vendor rules, current artifact fingerprints,
correlated render evidence, page geometry/boxes, embedded fonts, annotations,
security, fail-closed output-intent/transparency/color evidence for the Ingram
profile, supported product/page/language scope, and cover
template/barcode diagnostics. Eligible editions produce deterministic
product-form-specific ZIPs: paperback packages contain the exact validated
interior/cover PDFs, while EPUB packages contain a normalized,
relationship-checked EPUB 3; both include a front-cover image when available, a
SHA-256 manifest, and the complete preflight report. Print packages never
synthesize a digital artifact from print-edition metadata or its ISBN. Every
included semantic front/back-matter row is preserved in explicit order through
TXT, Markdown, EPUB, and contained press rendering. The EPUB path
emits and validates TOC and landmark navigation,
spine XHTML, internal resources, and alternative text; mixed unsupported
scripts and language marks fail closed for the initial English/Latin scope,
including manuscript Figure and legacy anchored-image captions/alternative text.
Missing, malformed, empty, RGB, or unknown Ingram color-space evidence also
fails closed.
Package identity includes stable validated input hashes/provenance and every
owning runtime/profile version; exact row IDs stay internal for build
correlation, and source/input state is transactionally rechecked before
persistence. ISBN rules distinguish optional supplied identifiers,
Ingram requirements, Lorekeeper-generated barcodes, and KDP overlay reserves.
Exact-package digital and physical proof records are explicit
user actions; the Publish assistant can run/explain preflight and build packages
through the same service but cannot approve proofs. Physical proofs are
paperback-only; EPUB reports that proof stage as not applicable. The in-app EPUB
check is structural rather than a complete reader matrix. Vendor uploads and
digital/physical proofs are explicit human records, not requirements for the
owned renderer to report its scoped validation result.

Complete the in-app production path.

Deliverables:

- versioned KDP and Ingram profile registry with sources/review dates;
- preflight for metadata, content order, blank pages, dimensions, page boxes,
  fonts, images, colors, output intent, transparency, annotations, security,
  cover/spine/barcode geometry, and artifact staleness;
- reflowable EPUB 3 with navigation, landmarks, language, semantic structure,
  alt text, metadata, and validator integration;
- downloadable interior, cover, EPUB, front-cover image, manifest, and report;
- explicit digital-proof checklist and physical-proof status;
- `Legacy`, stale, invalid, and `Lorekeeper validated` labels in product language.

Assistant parity: the Publish assistant can run and explain every rule, navigate
to affected source, apply safe fixes through owning services, rerun validation,
and package artifacts. Only the user can approve a proof.

Gate: representative artifacts pass the independent versioned structural
profiles. Vendor upload, reader/viewer, and physical-proof outcomes remain
versioned evidence the user may record without changing internal conformance.

### Phase 1 definition of done

- Existing local data migrates without content loss and can be restored.
- The old chapter-body and single-profile runtime paths are gone.
- A user can complete the end-to-end production-file workflow without a separate
  conversion or preflight application; vendor acceptance remains a recorded
  external fact.
- All UI capabilities have complete assistant parity.
- Artifacts identify source revision, edition, renderer, profile, and hashes.
- Documentation clearly separates implemented, preview, and verified claims.
- Windows and both macOS architectures include and exercise the press runtime;
  other declared targets are not claimed until tested.

## Phase 2 — professional editing and proofing

Status: `Researched`

Outcome:

- tracked insertions, deletions, replacements, moves, and formatting changes;
- comments, replies, editorial queries, assignments, and resolution;
- accept/reject with filters and batch scope;
- snapshots, named milestones, semantic comparisons, and revision history;
- developmental, line, copyediting, and proofreading modes;
- house style, term lists, spelling variants, consistency checks, and editorial
  reports;
- edition-aware page proofs and annotations anchored to a page-map version;
- professional DOCX import/export with semantic style mapping, comments,
  tracked changes, stable-reference/fidelity warnings, and a structured
  interchange report;
- complete Editor and Publish assistant parity for reads, suggestions,
  navigation, resolution, DOCX interchange diagnostics, and reports.

Dependencies: Phase 1 schema, transaction log, stable IDs, revisions, editions,
and page maps.

Verification emphasis: anchor remapping, concurrent/stale edits, attribution,
accept/reject reversibility, comparison accuracy, and proof invalidation.

## Phase 3 — illustrated and picture-book design

Status: `Implemented core; advanced DTP additions deferred`

Implemented:

- format-neutral chapters containing semantic text, flowing Figures, and
  Designed Page references;
- inline/centered/floated/full-width/full-bleed/dedicated-page Figure intent,
  wrap, contain/cover fit and internal crop position, captions, page breaks,
  alt/decorative state, and language;
- project-owned authoring page setup plus single-page, facing-spread, and
  eligible independent Digital PDF composition variants keyed to exact geometry;
- shared scene objects, layers, locks, visibility, grouping, z-order, rulers,
  computed overlays, snapping, zoom, keyboard movement, object styles, reading order,
  semantic text bindings, unplaced-content and overflow diagnostics;
- contextual Edit, Press-backed Read, Pages, and Review chapter modes, including
  real paginated preview and project page-setup controls;
- format-aware back/spine/front print covers and front-only digital covers;
- server-owned geometry descriptors for Figure/page/cover generation, including
  exact raster/aspect, effective DPI, safe/bleed/gutter/barcode/text regions;
- protocol-v5 structured Figure, page, and cover rendering without rasterized
  text, project font embedding, color/grayscale/CMYK paths, bleed, and ink limits;
- one-file tagged Digital PDF with cover page one, bookmarks, internal links,
  mixed page boxes when explicitly enabled, and accessible semantic structure;
- fixed-layout EPUB scenes with real text, semantic Figures, reading order, and
  alternatives plus EPUB accessibility metadata; Markdown/TXT preserve
  captions, alternatives, and composition content in reading order;
- compact, revision-safe Outline, Editor, Images, and Publish assistant tools,
  one-use persisted staging for large scenes, and genre-aware Outline guidance;
- guarded protected-backup migration and v17 Core/release import/export cutover.

Deferred without changing the semantic/scene ownership model:

- formal PDF/UA validation or certification;
- SVG/vector import and editing, arbitrary paths, gradients, masks, advanced
  effects, spot colors, separations/soft proofing, and PDF/X-4;
- parent/master pages, reusable templates/components, baseline/column grids,
  linked text frames, alignment/distribution commands, and manual page
  intervention;
- multilingual shaping beyond the certified English/Latin left-to-right scope,
  including RTL/bidirectional text, CJK/vertical layout, script fallback, and
  locale-specific hyphenation/punctuation;
- hardcover/dust-jacket and additional illustrated-book vendor profiles.

Future verification emphasis: formal accessibility validation, geometry and
reading-order regression, advanced color/vector correctness, multilingual
fixtures, device matrices, and physical proofs.

## Phase 4 — nonfiction and reference books

Status: `Researched`

Outcome:

- footnotes, endnotes, citations, bibliography, and source management;
- figures, captions, tables, callouts, sidebars, equations, and code blocks;
- stable cross-references and generated numbering;
- TOC, lists of figures/tables, index, and glossary generation;
- table headers across pages and accessible table semantics;
- reference integrity and update diagnostics;
- import/export of common structured reference data where licensing and
  fidelity permit;
- complete Editor and Publish assistant parity for creating, querying,
  renumbering, validating, and repairing reference structures.

Dependencies: Phase 1 semantic schema/styles and Phase 2 review/versioning.

Verification emphasis: renumbering stability, missing/ambiguous targets,
bibliographic fidelity, pagination interactions, math/text accessibility, and
EPUB parity.

## Phase 5 — publisher operations

Status: `Researched`

Outcome:

- imprints, contributors and roles, territories, rights, permissions, and
  edition/product metadata;
- ISBN purchasing/allocation tracking, identifier pools, and governance around
  the Phase 1 EAN-13 generator;
- ONIX for Books export and validation;
- accessibility statements, certification evidence, and remediation workflow;
- proofs, corrections, submission packages, release checklists, and status;
- catalog, series, pricing, availability, and edition relationships;
- team roles, attribution, audit log, approvals, and handoffs;
- controlled retailer/distributor integrations where APIs and terms permit;
- complete operations-assistant parity, with explicit human approval before any
  purchase, legal declaration, external upload, publication, pricing, rights, or
  distribution action.

Dependencies: certified artifacts and proof states from Phases 1–4.

Verification emphasis: identifier uniqueness, metadata consistency, permission
boundaries, audit completeness, idempotent external operations, and recovery
from partial submissions.

## Research maintenance

The roadmap is supported by:

- [Publishing industry workflow and file standards](research/publishing-industry-and-file-standards.md)
- [Book authoring and visual-design software](research/book-authoring-and-design-software.md)
- [Open-source publishing stack preliminary screen](research/open-source-publishing-stack.md)
- [Structured manuscripts and safe migration](research/structured-manuscripts-and-safe-migration.md)
- [Page composition and typesetting](research/page-composition-and-typesetting.md)

Vendor requirements, standards, library behavior, and licenses are time-sensitive.
Recheck the relevant sources at the start of each implementation feature and
record adopted dependency versions in the architecture and shipped notices.
