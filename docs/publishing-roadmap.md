# End-to-end book publishing roadmap

Last updated: 2026-07-30

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

No output is called “print-ready,” “PDF/X,” “accessible,” or vendor-compatible
until the corresponding verified gate passes.

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

### One source, multiple editions

The semantic manuscript is authoritative. PDF, EPUB, HTML previews, search
fragments, page maps, and cover dimensions are versioned projections. Each
publication edition owns its product-specific choices and artifacts.

### In-app production

A verified path must render, validate, preview, and package inside Lorekeeper.
Development-only validators and vendor upload portals may be used to certify the
implementation, but users are not required to install or operate another
conversion/preflight tool.

### Verification and review

- Tests and fixtures are part of the feature when correctness is data-, layout-,
  or conformance-sensitive.
- Every completed feature receives a fresh independent harsh review before its
  commit, following `AGENTS.md`.
- Documentation and the roadmap status change in the same commit as the feature.

## Phase overview

| Phase | Outcome | Initial status |
|---|---|---|
| 1. Publisher-ready novel foundation | Structured editing and independently validated paperback/EPUB artifacts for a narrow certified scope | Planned |
| 2. Professional editing and proofing | Track changes, comments, comparisons, house style, and page-proof workflows | Researched |
| 3. Illustrated and picture-book design | General page-layout system, master pages, object tools, advanced color, and fixed-layout EPUB | Researched |
| 4. Nonfiction and reference books | Notes, citations, tables, figures, cross-references, generated references, equations, and code | Researched |
| 5. Publisher operations | Imprints, contributors, rights, identifiers, ONIX, proofs, catalog, team audit, and controlled distribution | Researched |

## Phase 1 — publisher-ready novel foundation

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

Color interiors, hardcover/dust jackets, fixed-layout EPUB, arbitrary page
design, rich nonfiction, direct retailer submission, and collaborative editing
are explicitly outside this phase.

### Implementation prerequisite

Automated fixtures and test-project work were explicitly authorized by the user
on 2026-07-30. Every data-, layout-, and conformance-sensitive feature must add
and maintain the relevant tests; this authorization does not relax the
feature-by-feature migration, review, verification, or commit gates.

### Phase 1 feature sequence

Each numbered item is a coherent feature, reviewed and committed before the next
begins.

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

#### 1a. PDF/X fallback conformance spike

Status: `Preview` — reduced-scope renderer accepted on 2026-07-30

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
therefore no conformance or vendor claim is made. This explicit reduced
`Preview` acceptance unblocks Feature 2 while reserving `Verified` for Feature
7's external evidence. See
[`research/weasyprint-pdfx-fallback-spike.md`](research/weasyprint-pdfx-fallback-spike.md)
and
[`decisions/0002-accept-weasyprint-for-preview-press-runtime.md`](decisions/0002-accept-weasyprint-for-preview-press-runtime.md).

#### 2. Structured manuscript schema and migration

Status: `Planned`

Replace plain chapter-body runtime ownership with versioned semantic documents
and a single manuscript application service.

Deliverables:

- versioned schema and stable block IDs;
- lossless text projection and normalized import codec;
- transactional domain commands with revision tokens;
- WAL-safe backup, migration journal, expand/transform/validate/contract
  migrations, recovery screen, and restore drill;
- conversion of every live and historical body-bearing record;
- lossless migration of Picture Page text boxes to stable manuscript block/range
  references while preserving element identity, geometry, typography, z-order,
  and reading order;
- remapping of illustrated-prose paragraph index/hash anchors to stable block
  IDs with hash validation and fail-closed ambiguity handling;
- project export v8 for structured manuscripts and stable visual-layout
  references, with isolated v1–v7 import adapters;
- a temporary textarea adapter that reads the manuscript's plain-text
  projection and translates revision-aware saves through the manuscript
  service's parse/diff commands, never through `Chapter.Body` or a parallel
  whole-string persistence path;
- rebuilt search/vector/graph/Picture Page projections;
- removal of direct obsolete runtime body access after cutover.

Assistant parity:

- bounded block reads;
- insert/replace/delete/move/split/merge block operations;
- inline-mark and style-role operations;
- semantic diffs, expected revisions, and reviewable changes;
- existing Picture Page and illustrated-prose assistant operations resolve and
  mutate the same stable blocks/anchors as their UI services;
- migration preflight, journal, validation-report, and recovery-state reads,
  with safe retry diagnostics; restore remains an explicit user-confirmed
  action;
- complete removal of obsolete line/whole-string mutation tools.

Gate: fixture databases, interruption injection, hash equivalence, multi-text-
box Picture Pages, illustrated-prose anchors, import/export, index, projection,
and restore tests pass before any existing database is contracted.

#### 3. Semantic rich-text editor and named styles

Status: `Planned`

Replace the textarea experience with a ProseMirror-based editor that uses the
new service boundary.

Deliverables:

- paragraph, chapter heading, subheading, scene break, block quote, list, link,
  and image/figure-capable base nodes;
- emphasis, strong, small-caps intent, superscript/subscript, language, and
  character-style marks;
- named paragraph/character styles with edition-independent semantics;
- project export v9 that round-trips semantic style definitions, with an
  isolated v8 import adapter;
- removal of the temporary textarea adapter after every editor workflow uses
  schema-driven transactions;
- paste/import normalization with warnings;
- undo/redo, keyboard behavior, special characters, find/replace preview,
  document outline, counts, autosave/conflict handling, and accessible focus;
- existing context, contest, revision-agent, Picture Page, and review workflows
  operating through structured commands.

Assistant parity: every block/mark/style command, structural search, validation,
and normalization diagnostic is available to the Editor assistant through the
same services.

Gate: no raw HTML authority, no direct database mutation, and no divergence
between manual and assistant edits.

#### 4. Publication editions and book structure

Status: `Planned`

Evolve the one-profile Publish workspace into edition-scoped configuration.

Deliverables:

- one-to-many `PublicationEdition` model with a safely migrated default;
- an edition-specific migration journal and recovery boundary independent of
  the completed manuscript migration;
- conversion of the optional zero-or-one `PublishProfile` and all current
  project-scoped selections, placements, and cover references, creating one
  default edition when a project has publishing rows but no profile;
- format, vendor/profile version, trim, binding, paper, ink, bleed, margins,
  metadata, identifier, and status;
- edition-specific included content and order;
- front/back-matter builder for title, copyright, dedication, epigraph,
  contents, acknowledgments, about-author, also-by, and custom matter;
- named-style mappings and overrides per edition;
- artifact/proof staleness derived from content, settings, assets, renderer, and
  profile versions;
- edition clone, archive, compare, and audit history.
- project export v10 for editions and edition-style mappings, with isolated
  v9-or-earlier import adapters.

Assistant parity: a dedicated Publish assistant can read and operate the entire
edition/matter/style surface through the owning services, including edition-
migration status and diagnostics. Destructive recovery remains user-confirmed.

Gate: EPUB and two paperback editions can share source content while retaining
independent settings and identifiers.

#### 5. Deterministic novel typesetting and real preview

Status: `Planned`

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

Status: `Planned`

Turn existing project images/Picture Pages into edition-aware cover sources.

Deliverables:

- vendor/product template from final page count, trim, paper, and binding;
- back, spine, and front regions with bleed, safety, fold, and barcode reserve;
- EAN-13 barcode generation and validation from a user-supplied ISBN, including
  checksum, quiet zone, 100% black, background, size, and placement rules;
- separate vendor behavior: Ingram output always contains the required barcode,
  while KDP can either contain Lorekeeper's barcode or reserve the area for
  KDP's optional overlay;
- editable title/subtitle/author/spine/back-copy text;
- image crop/focal controls, background handling, font licensing diagnostics,
  and metadata consistency checks;
- conditional spine text based on profile rules;
- actual cover PDF preview and artifact.

Assistant parity: complete object/property reads and writes, template
recalculation, copy suggestions, image operations, and diagnostics through
shared services.

Gate: cover geometry is invalidated and recalculated whenever a dependency
changes; a user must acknowledge material layout changes before proof approval.

#### 7. Preflight, EPUB 3, and publication package

Status: `Planned`

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
- `Preview` versus `Verified` labels in product language.

Assistant parity: the Publish assistant can run and explain every rule, navigate
to affected source, apply safe fixes through owning services, rerun validation,
and package artifacts. Only the user can approve a proof.

Gate: representative artifacts pass internal checks, the independently
versioned PDF/X profile where claimed, KDP/Ingram upload preflight, EPUBCheck,
multiple reader/viewer checks, and documented physical-proof review.

### Phase 1 definition of done

- Existing local data migrates without content loss and can be restored.
- The old chapter-body and single-profile runtime paths are gone.
- A user can complete the certified workflow without a separate production
  application.
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

Status: `Researched`

Outcome:

- parent/master pages, templates, reusable components, and object styles;
- page/spread thumbnails, page insertion/reorder, recto/verso, and sections;
- rulers, guides, grids, baseline grids, snapping, alignment, and distribution;
- text/image frames, linked overflow, columns, wrap, crop, focal point, and
  fitting;
- shapes, paths, fills, strokes, gradients, masks, groups, layers, locking, and
  visibility;
- reading order, alt text, and semantic roles independent of visual z-order;
- ICC-aware color workflow, CMYK policy, ink limits, separations, and soft proof;
- fixed-layout EPUB and accessible alternatives;
- additional color-interior and illustrated-book vendor profiles;
- complex-script expansion covering bidirectional/RTL text, CJK line breaking,
  vertical text where supported, complex shaping, fallback fonts,
  locale-specific punctuation, and matching assistant/preflight behavior;
- complete Publish assistant parity for page/object/layer/guide/color commands.

Integration principle: generalize the existing Picture Page model into the
shared page-object system; do not create a competing layout stack.

Verification emphasis: geometry determinism, text overflow, reading order,
color conversion, visual regression, device behavior, and physical proofs.

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
