# Publication artifact roadmap

Last updated: 2026-08-10

## Destination

Lorekeeper will let an author create, inspect, validate, and download the files
needed to publish a supported digital book or have a supported physical book
printed without routing the project through a separate word processor, layout
application, PDF converter, or preflight utility.

This roadmap is about producing publication artifacts. It does not make proof
approval, physical-copy tracking, vendor-upload tracking, direct submission,
pricing, rights administration, or catalog operations part of artifact
readiness. Authors may still review files and printed samples, but Lorekeeper
does not model those activities as publication gates.

Current implementation boundaries live in
[`architecture.md`](architecture.md). Research evidence and historical renderer
decisions remain under [`research/`](research/README.md) and
[`decisions/`](decisions/).

## Product and artifact model

### One source, Core Book, and publication releases

- The semantic manuscript and project assets are authoritative.
- Core Book owns shared metadata, content choices, matter, page setup, Book Text
  Styles, opening and ending artwork, and the reusable front cover.
- Optional publication releases inherit Core Book and override only what must
  differ for a product.
- Core Book can produce a private reading PDF. That file is useful for review or
  sharing but is not a publication release.
- Release-specific manuscript text, Figures, Designed Pages, and text styling
  are authored in Editor. Publish consumes the resulting effective release;
  it does not become a second manuscript editor.

### Generic and specific publication profiles

Every release uses one of two profile classes:

- **Generic profiles** provide safe, configurable output for an unknown vendor,
  a custom workflow, private distribution, or any purpose for which the user
  does not want a named vendor contract. A Generic artifact makes no vendor
  compatibility claim.
- **Specific profiles** are built-in, application-owned, versioned presets for a
  named vendor and product. They encode the vendor's supported geometry,
  metadata, cover, color, file, and validation rules. Initial examples are
  Amazon KDP and IngramSpark; future profiles may include Google Books and other
  vendors as their documented requirements are researched and implemented.

Specific profile versions are internal implementation details. Normal UI and
assistant tools select a destination and product, not a raw profile identifier.
Every specific profile records its sources and review date, and profile changes
make affected artifacts stale rather than silently changing an existing file's
claim.

### Required artifact sets

| Product | Required files |
|---|---|
| Core reading copy | One tagged reading PDF; private/review output only |
| Paperback | Separate single-page interior PDF and full-wrap cover PDF |
| EPUB ebook | EPUB 3 plus a separately downloadable front-cover image |
| PDF ebook | One tagged Book PDF with the front cover as page one |
| Hardcover | Future product-specific interior plus case, jacket, or cover files required by the selected profile |

Manifests, validation reports, and deterministic packages are useful supporting
artifacts. They do not replace the directly downloadable files a vendor expects.

## Completion rules

### Human and assistant parity

A publishing capability is incomplete until its owning assistant can discover
it, read current state, perform the same safe operation as the UI, receive the
same validation, and observe the accepted result. Stable IDs, revisions, compact
results, and shared application services remain mandatory.

Edition-specific text and style operations belong to Editor and the Editor
assistant. Release configuration, cover production, validation, preparation,
and artifact inspection belong to Publish and the Publish assistant.

### Safe data evolution

- Back up and validate local data before destructive transformation.
- Use forward EF migrations and versioned transformation journals.
- Preserve semantic content, stable IDs, assets, artifact bytes, and hashes.
- Import older project exports through isolated versioned adapters.
- Remove superseded runtime paths after a successful cutover.

### In-app production and truthful claims

- Supported artifacts render, validate, preview, and download inside Lorekeeper.
- Users do not install a separate converter, renderer, or validation program.
- External vendor portals may be used to test a profile during development, but
  direct submission is not required product behavior.
- Lorekeeper names the exact profile and validation scope it has checked. It
  does not claim vendor acceptance, formal PDF/UA certification, or general
  accessibility certification without corresponding evidence.

## Current foundation

Status: `Implemented core; completion work remains`

The current application provides:

- a structured semantic manuscript with stable blocks and Book Text Styles;
- project-owned page setup, flowing Figures, Designed Pages, and structured
  cover scenes;
- Core Book plus sparse paperback, EPUB ebook, and PDF ebook releases;
- opt-in, chapter-level edition content in Editor with live Core inheritance,
  copy-on-write snapshots, edition-owned Designed Pages, shared reusable Book
  Text Styles, reset-to-Core, and Publish difference/diagnostic links;
- a Lorekeeper-owned Rust renderer with contained assets, deterministic PDF
  output, embedded fonts, page maps, and independent post-write inspection;
- Generic and Amazon KDP PDF 1.7 paperback output;
- Ingram PDF/X-1a:2001 paperback output with the owned CMYK profile;
- reflowable and fixed-layout EPUB generation;
- tagged Digital PDF with its front cover as page one;
- immutable artifacts, validation reports, manifests, packages, and downloads;
- private Core reading-PDF generation and preview;
- conversational Publish assistance and shared page/cover composition tools.

This foundation is broad enough to keep. The remaining work is chiefly human UI
completeness, artifact inspection, external-standard
validation integration, profile coverage, and native release verification—not
a replacement renderer.

## Next phase — artifact-complete publishing

Status: `Planned`

### 1. Edition-specific authoring in Editor

Status: `Implemented`

- Editor selects Core Book or one release whose edition content is enabled.
- Chapters inherit Core until their first text/layout mutation, then keep a
  frozen release snapshot and edition-owned Designed Pages.
- Reset returns one chapter to Core without deleting project images or shared
  Book Text Styles.
- Editor assistant, Review Edits, contests, revision workers, previews, search,
  Figures, and Pages carry the protected target; Publish reports bounded
  differences and layout diagnostics with Editor deep links.
- Exports, render preparation, and fingerprints consume effective release
  content. Publish does not edit edition manuscript/page layouts.

Gate: the same project can intentionally produce different paperback, EPUB, and
PDF ebook content without copying the entire book or changing Core content.

### 2. Complete release-configuration UI

- Expose release matter additions, exclusions, replacements, and ordering.
- Expose opening and ending image placement overrides.
- Expose release content inclusion and reading order.
- Keep project-wide Book Text Styles reusable from Core and every release;
  release-specific appearance comes from edition manuscript references and
  direct formatting in Editor.
- Provide an explicit path to create and edit a release layout when its target
  geometry differs from the authoring layout.
- Let every customized section return to its inherited Core value.

Gate: every artifact-affecting setting supported by the services and assistant
has an understandable manual workflow.

### 3. Artifact preview, diagnostics, and downloads

- Preview paperback interiors, full-wrap covers, PDF ebooks, and EPUB books
  inside the corresponding release workspace.
- Show all prioritized validation diagnostics rather than truncating the result
  to a small fixed subset.
- Link diagnostics to the relevant chapter, block, Figure, Designed Page, cover
  object, or release setting when a stable source exists.
- Provide direct downloads for the interior PDF, cover PDF, EPUB, front-cover
  image, and Book PDF in addition to optional packages, manifests, and reports.
- Preserve immutable artifact identity and clearly distinguish current, stale,
  invalid, and legacy output.

Artifact history, render comparison, and production audit browsing may improve
diagnosis, but they are not prerequisites for producing an uploadable file.

Gate: a user can visually inspect and download every required constituent file
without unpacking a package or opening another production application.

### 4. Authoritative EPUB validation

- Bundle and invoke the current production EPUBCheck release through an
  integrity-checked, app-owned runtime boundary.
- Validate every prepared EPUB before reporting it ready.
- Store and display the complete validator result with source-oriented recovery
  actions.
- Keep visual review and semantic accessibility diagnostics separate from the
  structural EPUBCheck result.

Gate: a user does not need to install or run EPUBCheck separately, and an
invalid EPUB cannot be reported as ready.

### 5. Versioned profile registry and broader rules

- Formalize Generic versus Specific as the durable profile taxonomy.
- Review and encode current KDP and Ingram product constraints, including trim,
  page count, margins, bleed, paper, color, image resolution, spine, barcode,
  font, transparency, and file-size rules.
- Keep Generic paperback, Generic EPUB, and Generic PDF ebook output available
  without a named-vendor claim.
- Add new Specific profiles one vendor/product at a time with sources,
  review dates, fixtures, and explicit supported scope. Google Books is a
  planned digital-profile target; it is not implied by generic EPUB output.
- Make raw profile versions unavailable to normal UI and assistant mutations.

Gate: each named destination has a reproducible versioned contract, while
unknown or custom destinations remain possible through Generic profiles.

### 6. Native release acceptance

- Build and exercise the integrity-checked Press runtime on every platform the
  application claims to distribute.
- Generate a Core reading PDF, Generic paperback, KDP paperback, Ingram
  paperback, EPUB, and PDF ebook on applicable clean target machines.
- Verify operation without Rust, Cargo, Python, Java, or machine PDF software on
  the runtime `PATH`, except for any explicitly bundled validator runtime.
- Do not claim an untested platform.

Gate: Windows x64, macOS x64, and macOS arm64 release artifacts execute the full
supported publication pipeline; other platforms remain unclaimed until tested.

### Definition of done

- Existing local data and older project exports migrate without content loss.
- Core and each release can be authored intentionally through the correct
  Editor target.
- Every supported product can be configured through safe defaults and complete
  manual/assistant controls.
- Every required publication file can be generated, previewed, validated, and
  downloaded inside Lorekeeper.
- Generic artifacts carry no named-vendor claim; Specific artifacts identify
  the exact built-in profile and version used.
- Windows and supported macOS builds exercise the same packaged pipeline.

## Later phases

### DOCX manuscript interchange

Status: `Planned after artifact completion`

DOCX import and export remain product goals because authors and editors need a
common interchange format. The initial scope is not a full Word-compatible
collaboration system. It will preserve semantic paragraphs, headings, lists,
inline emphasis, Book Text Styles, Figures, captions, and useful document
metadata where representable, with explicit fidelity warnings for unsupported
content. Comments, tracked changes, and exhaustive Word layout fidelity may be
added later but are not prerequisites for baseline DOCX interchange.

DOCX import writes through the semantic manuscript boundary. DOCX export reads
the selected Core or effective release source; it does not become an alternate
authoritative manuscript.

### Hardcover and expanded physical products

Status: `Planned`

Hardcover remains an explicit publication goal. It will be added as one or more
product forms backed by Generic and Specific profiles rather than treated as a
paperback flag. Profiles must describe their required case-wrap, printed-case,
dust-jacket, spine, hinge, board, bleed, and interior files. Page-count and
material-dependent geometry stays derived from the selected product profile.

Additional printers and bindings follow the same profile process: research the
current requirements, define the artifact set, implement a versioned Specific
profile, validate it independently, and retain Generic output for custom work.

### Advanced design, language, and accessibility

Status: `Deferred without structural redesign`

- multilingual shaping, bidirectional and vertical layout, fallback chains,
  locale-specific line breaking, and hyphenation;
- SVG/vector import, arbitrary paths, gradients, masks, advanced effects, spot
  colors, separations, soft proofing, PDF/X-4, and overprint controls;
- parent/master pages, reusable templates, linked text frames, baseline and
  column grids, alignment/distribution, and manual page intervention;
- independent formal PDF/UA validation and certification evidence.

The semantic manuscript, scene model, reading order, tagged PDF, and EPUB
semantics must remain suitable foundations for these additions.

### Nonfiction and reference books

Status: `Researched`

Future structured authoring includes footnotes, endnotes, citations,
bibliographies, tables, equations, code, cross-references, generated numbering,
indexes, glossaries, and accessible reference structures. These features use
the same Core/release and artifact pipeline rather than creating a parallel
publishing system.

### Optional publisher operations

Status: `Outside artifact-creation scope`

ISBN purchasing/allocation, ONIX, pricing, rights, catalog management, direct
retailer submission, team approvals, and distribution integrations may be
considered separately. None is a prerequisite or readiness gate for Lorekeeper
to create complete publication artifacts.

## Research maintenance

The roadmap is supported by:

- [Publishing industry workflow and file standards](research/publishing-industry-and-file-standards.md)
- [Book authoring and visual-design software](research/book-authoring-and-design-software.md)
- [Open-source publishing stack preliminary screen](research/open-source-publishing-stack.md)
- [Structured manuscripts and safe migration](research/structured-manuscripts-and-safe-migration.md)
- [Page composition and typesetting](research/page-composition-and-typesetting.md)
- [Lorekeeper Press requirements](research/lorekeeper-press-requirements.md)

Vendor requirements, standards, validator behavior, and dependency licenses are
time-sensitive. Recheck authoritative sources when implementing or updating a
Specific profile and record the adopted versions in architecture and shipped
notices.
