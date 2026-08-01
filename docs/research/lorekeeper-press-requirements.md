# Lorekeeper Press requirements and evidence matrix

Last reviewed: 2026-07-31

## Scope

Lorekeeper Press 1.0 targets English/Latin, left-to-right, prose-first paperback
interiors and color full-wrap covers. The supported profiles are
`generic-paperback-v1`, `kdp-paperback-v1`, and
`ingram-paperback-pdfx1a-v1`. The application does not require a separate PDF
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
| Protocol | Version 3; absolute job root; staged request; declared relative assets; hashes, sizes, dimensions, limits, and structured diagnostics | Black-box CLI adversarial fixtures |
| Containment | Reject traversal, absolute asset paths, symlinks/reparse points, undeclared files, corrupt/changed assets, pre-existing output, unsupported formats, and unsupported scripts | Test-owned filesystem fixtures; no production validator calls |
| Atomicity | Cancellation before or during rendering and any failure leave no promoted artifact; existing output is never overwritten | Process tests, staging observation, and sentinel bytes |
| Determinism | Identical semantic input, settings, assets, fonts, profile, and renderer produce byte-identical PDFs and hashes | Two independent job roots compared byte for byte |
| Typography | Bundled OFL Lora/Nunito/Roboto Mono regular/italic/bold/bold-italic faces; OpenType run shaping with emitted advances/offsets; paragraph and character styles; every semantic inline mark; glyph coverage failure; six-letter-tagged subset embedding; nominal PDF widths; shaped widths and ToUnicode, including multi-codepoint ligatures | Deterministic CLI layout trace plus mixed-run assertions and raw font dictionaries, descendant descriptors, font streams, and CMaps parsed with test-only `lopdf` |
| Line layout | Unicode line opportunities, English hyphenation, bounded overflow, widow/orphan minima, and deterministic line positions | Intermediate layout unit fixtures plus extracted PDF content |
| Pagination | Recto chapter starts, intentional blanks, running heads, page numbers, bounded/wrapped multi-page TOC convergence, stable chapter/block page maps, and page limit | Long-title/long-TOC glyph-bound fixtures, forced nonconvergence, narrow pages, long prose, and repeated renders |
| Publication model | Ordered matter, globally numbered acts/chapters, semantic text blocks, figures, illustrated anchors, publication placements, captions, whole-image and cropped Picture Page modes, and dedicated cover assets | Canonical full-model request, non-square mode-specific PDF transforms/layout traces, caption bounds, and feature/page-map evidence |
| Images | PNG decoding from the exact hash-validated bytes, alpha flattening, B&W interior conversion, color-cover independence, bounded placement, focal/crop geometry, placement-specific effective-DPI evidence, and deterministic compression | Image XObjects and color spaces parsed independently |
| Covers | Final-page-count spine, trim/bleed boxes, safe regions, back/spine/front composition, barcode reserve, and valid EAN-13 bars | One-page cover geometry, spine evidence, checksum negatives, and visual render inspection |
| KDP | PDF 1.7, embedded fonts, ToUnicode, correct page tree/boxes, no encryption, immutable interior and cover | Raw object and stream inspection |
| Ingram | PDF 1.3; PDF/X-1a:2001 identification; embedded registered CMYK output intent; CMYK/gray only; no transparency, actions, annotations, or encryption; embedded fonts; maximum 240% total ink | Raw object/resource/content inspection plus deliberate RGB/structure/ink negatives |
| Post-write validation | Reparse candidates independently from the writer and reject mismatched version, boxes, fonts, CMaps, color, output intent, actions, annotations, security, or transparency | Production `lopdf` inspector challenged by malformed fixtures |
| Packaging | Rust 1.97.1; exact `Cargo.lock`; approved license-expression allowlist; native executable, approved font faces/profile, asset-inclusive notices/SBOM, and exact hash manifest only | Debug/Release build, tamper/unexpected-file tests, native release matrix |
| Application | Current render states are NotGenerated, Rendering, Invalid, Validated, Stale, and Legacy; renderer/profile upgrades invalidate prior readiness; immutable downloads; database-scoped cross-process guarded backup/journal/recovery cutover; package provenance | Fully populated whole-database migration/hash/image-byte fixtures, malformed-marker and cross-service competing-owner drills, state/endpoint tests, and real C#-to-Rust renders |
| Assistant | Dynamic renderer capabilities, complete configuration/render/preflight/package tools, structured refresh notices, truthful scoped claims, and no proof-approval tool | Prompt/tool catalog and denial fixtures |

## Dependencies and distribution

The production crate exact-pins `pdf-writer 0.15.0`, `harfrust 0.12.0`,
`subsetter 0.2.6`, `unicode-linebreak 0.1.5`, `unicode-segmentation 1.13.3`,
English-only `hypher 0.1.7`, `moxcms 0.9.0`, `png 0.18.1`, `flate2 1.1.9`,
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

## Known 1.0 limitations and future phases

- Multilingual typography: add script/direction segmentation, tested fallback
  chains, language-specific line breaking and dictionaries, and complex-script
  fixtures before enabling another locale.
- Custom project fonts: add declared font assets, license attestations,
  embedding-right checks, deterministic family resolution, and diagnostics.
- Vector art: add contained SVG parsing/flattening with resource and complexity
  limits; never execute scripts or resolve external references.
- Advanced print color: add spot colors and PDF/X-4 as new versioned profiles,
  preserving PDF/X-1a and adding explicit overprint/transparency rules.
- Accessibility: add tagged structure, reading order, artifact tags, document
  language, alt-text associations, and PDF/UA evidence. Current PDFs are not
  labeled accessible.
- Manual page intervention: add stable page/column breaks and keep controls that
  remain edition-scoped, revisioned, assistant-accessible, and comparable.
- Standalone extraction: move `Lorekeeper.Press` only after its protocol,
  fixtures, licenses, release matrix, and deterministic build remain independently
  versioned without weakening the app's integrity checks.
