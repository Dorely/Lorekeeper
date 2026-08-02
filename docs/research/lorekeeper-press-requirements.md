# Lorekeeper Press requirements and evidence matrix

Last reviewed: 2026-08-02

## Scope

Lorekeeper Press 2.0 targets English/Latin, left-to-right paperback interiors,
color full-wrap covers, and tagged Digital PDF books with front cover page one.
The supported profiles are
`generic-paperback-v1`, `kdp-paperback-v1`, and
`ingram-paperback-pdfx1a-v1`, plus `generic-digital-pdf-v1`. The application does not require a separate PDF
converter or preflight product.

“Lorekeeper validated” means the artifact passed the renderer's scoped
post-write rules and the corresponding independently implemented test fixture.
It does not mean a retailer accepted an upload or that a human approved a proof.

Canonical fixtures are requirements inputs, not snapshots of renderer output.
After the in-flow illustration test exposed that the initial full-model fixture
omitted the protocol's anchor position, alignment, and page-break fields, those
fields were added explicitly as a requirements correction. No conformance
assertion was removed or relaxed.

## Requirements matrix

| Area | Required behavior | Evidence boundary |
|---|---|---|
| Protocol | Version 5; absolute job root; BOM-free UTF-8 app requests with compatible BOM parsing; job-bound terminal responses; staged request; declared relative image/font assets; hashes, sizes, dimensions/rights where applicable, limits, structured layout response, and diagnostics | Black-box CLI adversarial fixtures, including BOM compatibility, deterministic page-paint responses, and parsed-rejection identity assertions |
| Containment | Reject traversal, absolute asset paths, symlinks/reparse points, undeclared files, corrupt/changed assets, pre-existing output, unsupported formats, and unsupported scripts | Test-owned filesystem fixtures; no production validator calls |
| Atomicity | Cancellation before or during rendering and any failure leave no promoted artifact; existing output is never overwritten | Process tests, staging observation, and sentinel bytes |
| Determinism | Identical semantic input, settings, assets, fonts, profile, and renderer produce byte-identical PDFs and hashes | Two independent job roots compared byte for byte |
| Typography | Bundled OFL Lora/Nunito/Roboto Mono plus declared rights-confirmed project TTF and TrueType/CFF OTF; OpenType shaping with emitted advances/offsets; paragraph/character/object styles; semantic inline marks; glyph coverage and embedding-right failure; subset embedding; shaped widths and ToUnicode, including multi-codepoint ligatures | Deterministic CLI layout trace plus mixed-run/custom-font assertions and raw font dictionaries, descendant descriptors, streams, and CMaps parsed with test-only `lopdf` |
| Line layout | Unicode line opportunities, English hyphenation, bounded overflow, widow/orphan minima, sparse alignment/whole/right/first-line/hanging indent and spacing/pagination overrides, and deterministic line positions | Intermediate layout unit fixtures, protocol-v5 page-paint responses, plus extracted PDF content |
| Pagination | Recto chapter starts, intentional blanks, running heads, page numbers, bounded/wrapped multi-page TOC convergence, stable chapter/block page maps, and page limit | Long-title/long-TOC glyph-bound fixtures, forced nonconvergence, narrow pages, long prose, and repeated renders |
| Publication model | Ordered matter, globally numbered acts/chapters, semantic text, flowing Figures, edition placements, captions, exact-geometry Designed Pages/spreads, structured cover scenes, and Digital PDF page overrides | Canonical full-model request, layout traces, caption/flow/page-box assertions, semantic page maps, and structured-object evidence |
| Images | Hash-validated arbitrary-aspect PNG/JPEG, alpha flattening, B&W conversion, color-cover independence, flow/wrap/contain-or-cover/crop-position/bleed geometry, effective-DPI evidence, facing-spread splitting, and deterministic compression | Image XObjects, page maps, transformations, boxes, and color spaces parsed independently |
| Composition | Text, image, rectangle, ellipse, line, resolved group transforms/visibility/opacity/z-order, layer/style behavior, alignment, vertical alignment, letter spacing, backgrounds, shadows, logical reading order, vector shapes, selectable text, overflow failure, and no C#/Skia page rasterization | Layout traces and raw PDF text/path/image operations |
| Covers | Structured front-only digital and page-count-derived back/spine/front print scenes, trim/bleed/safe/fold regions, canonical copy bindings, barcode reserve, and valid EAN-13 bars | One-page print cover and Digital PDF page-one geometry, spine/reflow evidence, checksum negatives, and raw object inspection |
| KDP | PDF 1.7, embedded fonts, ToUnicode, correct page tree/boxes, no encryption, immutable interior and cover | Raw object and stream inspection |
| Ingram | PDF 1.3; PDF/X-1a:2001 identification; embedded registered CMYK output intent; CMYK/gray only; no transparency, actions, annotations, or encryption; embedded fonts; maximum 240% total ink | Raw object/resource/content inspection plus deliberate RGB/structure/ink negatives |
| Digital PDF | One PDF with front cover page one; PDF 1.7; uniform geometry unless overrides are explicitly enabled; bookmarks, internal TOC links, metadata, selectable text, document language, structure/parent trees, MCIDs, logical block grouping, list parents, Figure/Caption nesting, semantic roles, alt text, decorative artifacts, and logical reading order | Raw catalog, page-tree, annotation, marked-content, structure-tree, and page-box inspection; no formal PDF/UA claim |
| Object opacity | Digital PDF and KDP PDF 1.7 preserve scene-object opacity through bounded graphics states. Ingram PDF/X-1a flattens non-overlapping opacity against the page/cover substrate; shared profile validation rejects opacity over lower page art with object-specific diagnostics so semantic text and vector content are never silently rasterized or visually miscomposited. Source-image alpha is flattened during normalization. | Display-list order and graphics-state operators are parsed for PDF 1.7; PDF/X artifacts are independently checked for no transparency; overlapping PDF/X opacity has a fail-closed negative fixture. |
| Post-write validation | Reparse candidates independently from the writer and reject mismatched version, boxes, fonts, CMaps, color, output intent, actions, annotations, security, or transparency | Production `lopdf` inspector challenged by malformed fixtures |
| Packaging | Rust 1.97.1; exact `Cargo.lock`; approved license-expression allowlist; native executable, approved font faces/profile, asset-inclusive notices/SBOM, and exact hash manifest only | Debug/Release build, tamper/unexpected-file tests, native release matrix |
| Application | Current render states are NotGenerated, Rendering, Invalid, Validated, Stale, and Legacy; renderer/profile upgrades invalidate prior readiness; immutable downloads; database-scoped cross-process guarded backup/journal/recovery cutover; package provenance | Fully populated whole-database migration/hash/image-byte fixtures, malformed-marker and cross-service competing-owner drills, state/endpoint tests, and real C#-to-Rust renders |
| Assistant | Dynamic renderer capabilities, complete configuration/render/preflight/package tools, structured refresh notices, truthful scoped claims, and no proof-approval tool | Prompt/tool catalog and denial fixtures |

## Dependencies and distribution

The production crate exact-pins `pdf-writer 0.15.0`, `harfrust 0.12.0`,
`subsetter 0.2.6`, `unicode-linebreak 0.1.5`, `unicode-segmentation 1.13.3`,
English-only `hypher 0.1.7`, `moxcms 0.9.0`, `png 0.18.1`,
`zune-jpeg 0.5.15`, `zune-core 0.5.1`, `flate2 1.1.9`,
`serde 1.0.229`, `serde_json 1.0.151`, and `sha2 0.11.0`. `lopdf 0.44.0`
is isolated to post-write inspection. The build audits the complete transitive
graph rather than assuming direct licenses cover it.

The regular, italic, bold, and bold-italic faces of Lora, Nunito, and Roboto
Mono are distributed under the SIL Open Font License.
CGATS21 CRPC1 is the registered 240% TAC profile; its source and redistribution
conditions are shipped beside the profile. The runtime notice includes the
English hyphenation component's pattern-license notice as well as crate license
files. The generated SBOM fingerprints both the complete Cargo graph and every
bundled font/profile asset. An unknown license expression fails the build;
there is no bypass flag.

## Security model

The runtime manifest is an integrity and provisioning boundary, not a defense
against a user who can replace the application and its manifest together. The
renderer has no network loader and no general document-language execution. The
app normalizes project images into staged PNGs and passes no source filesystem
paths. The renderer accepts no undeclared file and inherits no `PATH`.

## Known 2.0 limitations and future phases

- Multilingual typography: add script/direction segmentation, tested fallback
  chains, language-specific line breaking and dictionaries, and complex-script
  fixtures before enabling another locale.
- Vector art and advanced paths: add contained SVG parsing/editing with resource
  and complexity limits; never execute scripts or resolve external references.
- Advanced print color: add spot colors and PDF/X-4 as new versioned profiles,
  preserving PDF/X-1a and adding explicit overprint rules.
- Accessibility certification: retain the implemented tagged structure, reading
  order, artifact tags, language, roles, and alt-text associations while adding
  an independent formal PDF/UA validator/evidence profile. No PDF/UA claim is
  made today.
- Composition productivity: add master pages/templates, linked text frames,
  baseline/column grids, alignment/distribution, advanced effects, and manual
  page intervention without changing semantic ownership.
- Manual page intervention: add stable page/column breaks and keep controls that
  remain edition-scoped, revisioned, assistant-accessible, and comparable.
- Standalone extraction: move `Lorekeeper.Press` only after its protocol,
  fixtures, licenses, release matrix, and deterministic build remain independently
  versioned without weakening the app's integrity checks.
