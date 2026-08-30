# Press production architecture

## When to read

Read this chapter when a change touches the owned native renderer, protocol v9,
render requests, deterministic pagination or shaping, Press runtime packaging,
physical-product registry entries, stock/spine/cover geometry, cover surfaces,
PDF versions/output intent/color/ink/transparency, tagged Digital PDF,
artifact inspection, EPUB structural validation, immutable artifact/package
bytes, renderer integrity, cancellation/atomicity, or publication claims.
Read it with `publishing-model.md` for Core Book, release, section, inheritance,
edition-content, Publish UI, and effective projection rules. Read it with
`composition-media.md` for source image/font staging, scene semantics, canvas
previews, Figures, Designed Pages, page setup, and geometry-bound generation.
Read it with `persistence-migrations-import.md` for durable render/preparation
rows, recovery, lock ownership, artifact hashes, and import/export boundaries.

This chapter defines Lorekeeper’s evidence boundary. A successful renderer
process is not by itself proof of print readiness, vendor compatibility,
accessibility certification, or third-party reader behavior. Claims must name
the exact profile and validation evidence that produced them.

## Scope and ownership

`IPrintProductRegistry` and `PrintProductRegistry` own the checked-in offline
physical-product catalog, immutable registry version/hash, product keys,
vendor/process/stock/binding/finish/cover-mode rules, trim choices, submitted
and manufacturing page limits, required artifacts, and frozen spine evidence.
Specific products are versioned application-owned contracts. Generic products
make no named-vendor claim and require a complete printer template manifest.
No runtime path contacts a vendor site or accepts a raw profile identifier as
an ordinary UI or assistant input.

`IPrintGeometryService` owns submitted, normalized cover-calculation, and
reported production page counts, exact stock/spine calculations, trim, bleed,
safe, hinge, board, wrap, flap, gutter, barcode, and duplex no-ink regions.
The interior layout pass precedes final cover geometry. Constraint-bound cover
objects reflow when geometry changes; free-positioned objects retain their
coordinates and produce actionable compatibility warnings.

`PublicationCoverService`, `PublicationCoverDesign`, and the cover-oriented
publication services own release cover aggregates and product-surface scenes.
Core owns the reusable front scene; physical releases project it into the
product front panel while retaining product-owned outside, inside, case,
jacket, cloth, spine, flap, and barcode regions. Canonical title, subtitle,
author, spine, and back copy remain bindings, not duplicated frame text.
Cover reads and mutations normalize a valid current-schema scene to effective
trim/bleed/page-count geometry before validation. Incomplete copy bindings,
accessibility choices, reading order, and layout placement remain editable
draft state and visible readiness blockers; autosave must not discard them.
Intentional full-surface Page/Front artwork may cross the inset safe area while
remaining bounded by the physical surface; barcode reserve, accessibility,
reading order, and image-resolution rules still apply. Text and ordinary
figure placement remain inside their safe regions.

`PublicationPressRuntime` owns packaged executable discovery, exact manifest
validation, platform/architecture checks, file hashes, environment isolation,
dynamic `describe`, and renderer readiness. `PublicationRenderService` owns
render queue state, request assembly, image/font staging, native progress,
immutable artifact bytes/hashes, renderer/profile provenance, and block/page
maps. `PublicationRenderWorker` owns recovery/cancellation and
`PublicationRenderProcessor` owns the bounded child process. `PublicationPackageService`
owns final product-form preflight and deterministic package assembly, consuming
validated evidence rather than silently rerunning a different validation path.

The native `Lorekeeper.Press` project owns protocol v9, shaping, pagination,
PDF serialization, color/asset normalization, and post-write inspection.
The application may request a compact browser layout trace and rasterize it for
preview, but Press remains the pagination and typesetting authority. App-owned
preview surfaces do not delegate layout to a browser-native PDF viewer.

## Current architecture and invariants

Every Press request stages `input/request.json`, declared PNG/JPEG assets, and
approved project TTF/OTF fonts under a bounded job root. Declarations carry
media type, byte length, dimensions where applicable, rights state, and
SHA-256. Physical requests carry product descriptor, registry version/hash,
submitted/normalized/reported page counts, and explicit cover-surface scenes.
Press rejects descriptors that differ from the packaged registry. Generic
products require complete printer geometry. KDP paperback uses the published
stock formula; KDP hardcover and Ingram products require an exact frozen
calculator measurement for normalized even page count. B&N products require
imported `PrintTemplateEvidence` whose product, construction, trim, page count,
source hashes, and exact full/front/back dimensions match the release.
Interpolation or a generic caliper fallback is not permitted.

Requests explicitly identify `outputPurpose` as `publication` or
`reading-copy`. Reading-copy output is accepted only by the generic Digital
PDF profile used by Core Book and cannot weaken a publication profile.
Terminal responses echo protocol version and job identity. If malformed input
prevents the body from being decoded and no identity can be recovered, the
application preserves the Press diagnostic rather than inventing an identity
mismatch. Protocol JSON is BOM-free UTF-8; the renderer tolerates an optional
UTF-8 BOM for compatibility. Redirected stdout/stderr are decoded as UTF-8 on
all platforms.

The renderer rejects absolute paths, traversal, links/reparse points,
undeclared or changed bytes, corrupt assets, restricted/unsupported fonts,
existing output, and cancellation before promotion. It creates a fresh
staging directory, validates every PDF independently, and atomically renames
only after all checks succeed. It never overwrites previous output. Declared
rasters are decoded and structurally validated once; validated pixels are
reused only by interior or cover surfaces that reference them. Shared color
transforms are reused when interior and cover intent match.

Press protocol v9 owns deterministic layout, English/Latin shaping and glyph
diagnostics, custom TTF/OTF staging and embedding, subsetting and ToUnicode
maps, bounded pagination, headings/TOC, stable block/page maps, inline
typography, sparse paragraph presentation, flowing Figures, crop positioning,
bleed, captions, structured Designed Pages and cover scenes, reusable styles,
vector shapes, reading order, page-size overrides for eligible Digital PDFs,
full-wrap geometry, EAN-13 bars, and PDF serialization. Structured text
remains selectable text rather than a rasterized page image.
The versioned `assets/manuscript-typography-v2.json` contract is embedded by
both Press and the application. It owns bundled family aliases and default
metrics for body text, heading levels 1-6, captions, lists, scene breaks, and
inline marks. Version 2 also owns inset-quotation text/rule colors and rule/gap
geometry plus ordinary and overlay-caption paint. Page setup supplies the
effective body size and leading for body and quotation text; named styles and
direct manuscript presentation remain sparse overrides, with direct explicit
`false` values able to disable inherited italic and small caps. Figure-caption
paragraph indents constrain the caption measure without moving its image. A role selector
such as `chapter-heading` or `block-quote` receives the same built-in visual
defaults regardless of its semantic block type. Paragraph styles on Figures
apply to their captions.

Inset-quotation left indent is the total text inset. Press places the rule and
gap within that measure, emits a rule segment on each occupied page, retains
selectable quotation text, and exposes both authored colors in the layout
trace. Overlay captions paint their shared translucent backing above the image
and below selectable caption text; wrapped overlay lines retain top-to-bottom
reading order on flowing and dedicated Figures. Browser-preview traces carry the
Press-resolved face, color, run language, line language,
artifact/light-text state, semantic decoration IDs, paint order, and a
face-metric baseline offset so Read reproduces the authoritative layout without
reconstructing typography from browser defaults.
Block spacing collapses across adjacent semantic blocks. A flowing Figure's
declared after-spacing participates in that same baseline calculation so the
following prose enters and remains within the active float exclusion region.

Physical print profiles differ deliberately. KDP and Generic print products
emit PDF 1.7 and extend page-edge art through the vendor bleed box while the
authored scene remains trim-sized. Ingram emits PDF 1.3 with PDF/X-1a:2001
identification, the registered CGATS21 CRPC1 CMYK output intent, CMYK/gray-only
resources, flattened raster alpha, non-overlapping scene opacity, no
transparent PDF objects, embedded fonts, no encryption/annotations/actions,
and a 240% total-ink ceiling. The separate production `lopdf` pass reparses
completed bytes before atomic promotion. An independent black-box harness
parses raw PDF objects without calling the production validator.
Text, manuscript-decoration vectors, and images follow the selected interior
color space; a black-and-white job converts quotation-rule and caption tones to
gray while preserving color-cover independence.

B&N `bn-print-pdfa1b-v1` emits PDF 1.4 with PDF/A-1b identification, embedded
fonts, output intent, flattened transparency, and exact imported-template page
boxes. Authoring always remains one connected `[BACK][SPINE][FRONT]` scene.
`FullWrapMeasured` produces one measured cover PDF including the spine.
`SeparatePanelsVendorSpine` crops that connected scene into front and back PDFs
and records the spine as vendor-generated; it never creates a separate spine
artifact. This matches B&N's documented template ZIP topology: the separate
front/back templates omit a spine, while the full template includes the
precisely measured spine ([B&N Cover Template Tool FAQs](https://help-press.barnesandnoble.com/hc/en-us/articles/5358927293211-Cover-Template-Tool-FAQs)).
B&N requires exact dimensions and embedded fonts and recommends PDF/A-1b
([B&N cover/interior acceptance guidance](https://help-press.barnesandnoble.com/hc/en-us/articles/5359031189787-Cover-Interior-Not-Accepted)).
Uploaded covers reserve the bottom-right barcode area and use `VendorOverlay`,
because B&N adds the applicable SKU or ISBN barcode
([B&N ISBN FAQs](https://help-press.barnesandnoble.com/hc/en-us/articles/5358254743963-ISBN-FAQs)).

Composition opacity rules preserve Digital PDF appearance with bounded
graphics states. KDP PDF 1.7 omits fully transparent backing paint and
deterministically composites translucent backing shapes where safe while
retaining selectable opaque text. Ingram PDF/X-1a flattens opacity against the
page or cover substrate. A translucent object overlapping lower page art is
rejected with exact object IDs when flattening would change appearance or
rasterize semantic text/vector content. Raster source alpha is flattened in
owned image normalization.

Digital PDF jobs produce one immutable Book PDF whose front cover is page one,
followed by publication sections and manuscript content. Tagged structure,
bookmarks, links, document language, logical reading order, heading levels 1-6,
paragraphs, lists, Figures/Captions, alt text, and decorative artifacts are
mandatory output. Lorekeeper reports implemented tagged output within its
declared boundary; it does not claim formal PDF/UA certification. Digital
covers reject print-only barcode modes, and physical cover surfaces cannot be
silently reused across product constructions.

Chapter starts use the next available page by default and therefore introduce
no parity blanks. When the effective Core/release right-hand policy is enabled,
Press inserts only the blanks needed for recto chapter openings. Parity is based
on final artifact page numbers: a Digital PDF front cover counts as page one,
while coverless chapter layout traces have no synthetic leading-page offset.

The application requests the full glyph-evidence `layout` trace for
conformance and `layoutTraceMode: browser-preview` for bounded chapter Read or
assistant page images. The browser trace retains paint order and typographic
runs but omits unused glyph arrays. Authoring traces may render unresolved
image accessibility as warnings and clip text at an authored frame; Core
reading-copy and publication renders retain strict overflow validation.
Publication renders reject meaningful images until alternative text or an
explicit decorative decision is present. Core reading copies may retain an
unresolved image as a warning-bearing private artifact so tagged reading order
remains structurally valid. Cover scene text retains canonical bindings until
Press materializes the resolved title, subtitle, author, spine, and back copy;
an optional binding or valid semantic content reference that resolves to empty
is omitted without creating a text frame, while a frame with neither binding
nor reference remains a hard layout error. Application-side cover validation
also decodes placed image assets before rendering and reports effective-DPI
warnings using the selected profile threshold; final Press evidence remains
authoritative.

Press owns immutable artifact evidence. `PublicationRenderService` persists
edition/Core target, status, bytes, length, SHA-256, source fingerprint,
renderer/profile provenance, diagnostics, and stable block/page mappings.
Project-scoped range endpoints recompute length and hash before returning an
ETag or body. Corrupt rows fail closed. Renderer version, installed manifest,
selected profile/registry, source fingerprint, EPUB exporter version, and
applicable assembler/rule versions participate in staleness. A queued job
snapshots the dynamic `describe` version and rejects a different executable
response.

EPUB preparation and preview are artifact-backed. The exporter writes
semantic XHTML, EPUB 3 metadata/navigation, reflowable content, Figures, and
fixed-layout Designed Pages without synthetic empty spine siblings. The
preview parser revalidates length/hash, bounds ZIP entries and expanded bytes,
rejects traversal, duplicates, undeclared resources, scripts/remote
properties, and unsupported media, then parses container, OPF, manifest,
spine, navigation, and fixed-layout viewports in memory. Validated resources
are exposed through artifact-scoped endpoints with MIME enforcement,
no-sniff headers, external-resource blocking, and sanitized XHTML/SVG/CSS.
The opaque-origin sandboxed iframe never runs artifact scripts. This is artifact
inspection, not a general third-party EPUB reader or external compatibility
claim.

Product packages contain only the files required by the selected form. EPUB
packages contain normalized EPUB plus separately downloadable front cover;
paperback packages contain validated interior and required cover PDFs; Digital
PDF packages contain one validated Book PDF; hardcover and complex Ingram
products contain exact case/jacket/cloth/duplex roles from the registry.
B&N packages additionally contain `print-setup.json`; separate-panel packages
contain front and back PDFs, while full-wrap packages retain the construction's
ordinary full-cover role. The manifest records only artifact-relevant product,
template, geometry, project-use identifier/barcode behavior, region
participation, and upload mapping. Account, rights, tax, pricing, and other
external business workflow are outside this boundary.
Package identity includes profile/rule/assembler versions, applicable input
hashes, deterministic ZIP metadata, and correlated validated render evidence.
Changing construction cannot pull an older surface artifact into a new
package.

The packaged runtime is built by `eng/BuildPressRuntime.ps1` from locked Rust
dependencies. It includes only the native executable, approved OFL fonts,
registered ICC profile, canonical print-product registry, notices, SBOM, and
exact hash manifest. Runtime resolution is relative to `AppContext.BaseDirectory`,
rejects missing/modified/linked/unexpected files, clears the child environment,
and provides no inherited PATH, Cargo, Python, uv, Typst, WeasyPrint,
Chromium, machine PDF tool, or repository fallback. Debug and Release use this
same boundary; Rust is not compiled at application runtime.

## Key files and file families

| Path or family | Primary responsibility |
|---|---|
| `Lorekeeper.Press/src/model.rs` | Protocol-v9 request/response, product/cover descriptors, purpose, diagnostics, artifacts, evidence, and layout contracts. |
| `Lorekeeper.Press/src/renderer.rs` | Containment, validation, deterministic pagination, composition, cover rendering, atomic promotion, progress, and evidence. |
| `Lorekeeper.Press/src/pdf.rs` | Owned PDF 1.7/1.3 writer, tagged structure, color/bleed/compositing, fonts, images, and barcodes. |
| `Lorekeeper.Press/src/font.rs` | TTF/OTF validation, shaping, subsetting, widths, embedding, ToUnicode, and glyph outlines. |
| `Lorekeeper.Press/src/image.rs` | Bounded raster decoding, alpha/color conversion, crop positioning, and total-ink enforcement. |
| `Lorekeeper.Press/src/inspect.rs` | Independent post-write geometry, font, color, output-intent, transparency, security, annotation, and tagged-PDF inspection. |
| `Lorekeeper.Press/src/main.rs` / `src/lib.rs` | `describe`, layout traces, bounded protocol-v9 render CLI, and testable library surface. |
| `Lorekeeper.Press/tests/conformance_v9.rs` | Protocol, containment, atomicity, determinism, layout, publication, product, cover, typography, color, PDF, and negative evidence harness. |
| `Lorekeeper.Press/fixtures/` | Frozen full-model, negative protocol, malformed raw-PDF, and test asset fixtures. |
| `Lorekeeper.Press/assets/` | Approved fonts/notices, registered ICC profile, canonical product registry, and shared manuscript typography defaults. |
| `Lorekeeper/Publish/PrintProductRegistry.cs` | Application loader/validator for registry version/hash and products plus submitted/normalized/reported page counts, spine, stock, surfaces, cover regions, barcode, duplex, case, jacket, and cloth geometry. |
| `Lorekeeper/Publish/PrintTemplateEvidenceService.cs` | Bounded B&N ZIP/PDF template import, one-page geometry measurement, source hashing, topology checks, and evidence serialization. |
| `Lorekeeper/Publish/PublicationCoverService.cs` | Revisioned Core/release cover aggregate, independent product surfaces, canonical bindings, geometry reflow, diagnostics, and acknowledgement invalidation. |
| `Lorekeeper/Publish/PublicationPressRuntime.cs` | Exact packaged runtime manifest, integrity, `describe`, controlled child environment, and readiness. |
| `Lorekeeper/Publish/PublicationRenderService.cs` | Queue, canonical staging, protocol requests, progress, hash-verified artifacts, page maps and staleness plus interrupted-job recovery, cancellation, bounded child process, and output verification. |
| `Lorekeeper/Publish/PublicationPackageService.cs` | Versioned preflight, correlated render evidence, exact product artifact roles, deterministic EPUB/package assembly, and package freshness. |
| `Lorekeeper/Publish/PublicationArtifactPreviewService.cs` | Bounded cached PDF-to-PNG rendering from immutable Lorekeeper artifacts. |
| `Lorekeeper/Publish/PublicationEpubPreviewService.cs` | Hash-verified bounded EPUB parser/cache, sanitized preview metadata, spine/navigation, and resource safety. |
| `Lorekeeper/Publish/PublishEndpoints.cs` | Project-scoped immutable artifact, range, PDF-page preview, and validated EPUB resource endpoints. |
| `Lorekeeper/Models/PublicationRender.cs` / publication artifact models | Durable target, status, artifact bytes/hash, provenance, diagnostics, and page-map data. |
| `eng/BuildPressRuntime.ps1` | Locked native build, license audit, notices/SBOM, registry packaging, and exact hash manifest. |
| `eng/ReviewPrintProductRegistry.ps1` | Maintainer-only registry candidate validation, frozen-measurement coverage, hash/evidence report, and installer. |

## Related chapters

- [`publishing-model.md`](./publishing-model.md) owns Core/release model,
  inheritance, sections, edition content, Publish UI/tools, projections, and
  preparation intent.
- [`composition-media.md`](./composition-media.md) owns images, Figures,
  fonts, page setup, Designed Pages, scene semantics, canvas previews, and
  source asset staging contracts.
- [`persistence-migrations-import.md`](./persistence-migrations-import.md)
  owns render/preparation persistence, lock ordering, migration/recovery,
  import/export, and local-data/security boundaries.
- [`manuscript-authoring.md`](./manuscript-authoring.md) owns semantic text,
  styles, annotations, revision/history, and target-aware authoring.
- [`validation-documentation.md`](./validation-documentation.md) owns the
  evidence matrix and documentation-level verification policy.

## Relevant verification

For native Press changes, run all locked formatting, lint, and conformance
checks:

```powershell
cd Lorekeeper.Press
cargo fmt --check
cargo clippy --all-targets -- -D warnings
cargo test --locked
```

For application integration changes, also run:

```powershell
dotnet build Lorekeeper.sln
dotnet run --project Lorekeeper --launch-profile http
```

Terminate the HTTP host after startup confirmation. Inspect protocol fixtures,
negative cases, raw-PDF black-box cases, registry version/hash, packaged
runtime manifest, artifact length/hash checks, and atomic promotion behavior.
Search for stale renderer/profile/product names and obsolete machine-tool
fallbacks. Do not claim vendor upload acceptance, formal PDF/UA certification,
or third-party reader compatibility unless the exact external integration and
scope were exercised. Browser UI, screenshots, Playwright, and manual release
checks require explicit authorization.
