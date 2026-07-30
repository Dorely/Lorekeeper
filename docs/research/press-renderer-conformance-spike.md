# Press renderer and conformance spike

Last reviewed: 2026-07-30

## Scope and outcome

This spike tested whether a pinned Typst + krilla + moxcms sidecar could be the
sole renderer for Phase 1's 6 × 9 inch prose paperback scope before Lorekeeper
changes any persisted publishing or manuscript data.

The result is a split decision:

- **PDF 1.7 prototype: technically viable.** The local prototype produces a
  deterministic 6 × 9 inch interior and a one-page wrap cover whose spine width
  is calculated from the rendered interior page count.
- **PDF/X-1a:2001 path: rejected.** Typst 0.15.1 exposes PDF versions 1.4–2.0,
  PDF/A, and PDF/UA, but not PDF/X. Its default is PDF 1.7. This matches both the
  pinned API and the current [Typst PDF documentation](https://typst.app/docs/reference/pdf/).
- **CMYK path: incomplete.** moxcms 0.9.0 can execute ICC transforms, and its
  sRGB transform is exercised by the fixture suite, but the spike does not
  contain a reviewed CMYK output profile or a renderer path that emits
  PDF/X-compliant CMYK objects and output intent.
- **Phase 1 production integration: blocked.** No EF model, application service,
  publish UI, assistant tool, or current export path was changed. The next
  renderer candidate must pass a separate fallback spike before Feature 2 can
  begin.

The architecture decision is recorded in
[`0001-reject-typst-as-sole-press-renderer.md`](../decisions/0001-reject-typst-as-sole-press-renderer.md).

## Implemented prototype

`Lorekeeper.Press` is a standalone Rust 2024 crate and is not part of the .NET
solution or runtime. It has no file or network access from Typst source. The
host supplies the output root as a process argument; the JSON request can only
select a validated child job ID.

Protocol version 1 contains:

- an explicit protocol version and renderer version;
- a constrained job ID;
- a versioned profile identifier;
- semantic title, author, and chapter content;
- explicit trim and cover-caliper inputs;
- status, artifact hashes, page counts, structured diagnostics, and inspection
  evidence;
- no boolean or metadata field that can fabricate a standard-conformance claim.

The `kdp-paperback-6x9-spike-v1` profile name identifies the fixture target. It
is not a KDP certification claim. The response always reports that vendor
upload preflight has not been run. The
`ingram-pdf-x1a-experimental-v1` request is deliberately rejected with
`PRESS_PDFX_UNAVAILABLE` and `PRESS_CMYK_PROFILE_REQUIRED`, emits no artifact,
and leaves `claimedStandard` empty.

### Produced evidence

The representative fixture produced:

| Evidence | Result |
|---|---|
| Interior | PDF 1.7, 3 pages, 432 × 648 pt (6 × 9 in) |
| Cover | PDF 1.7, 1 page, 882.54 × 666 pt |
| Spine | 0.54 pt from 3 pages × 0.0025 in/page |
| Page boxes | MediaBox present; CropBox, BleedBox, TrimBox, and ArtBox absent |
| Fonts | Subset Libertinus Serif regular/bold, embedded |
| Colors | ICCBased objects reported by the independent parser |
| Output intents | 0; therefore no PDF/X claim |
| Encryption/actions/transparency | None detected |
| Determinism | Identical SHA-256 hashes across repeated renders |
| Independent parsing/text extraction | `lopdf` 0.44.0 passed |
| Independent rendering | Poppler `pdftoppm` produced a valid PNG |
| Poppler structure check | 3 pages, 432 × 648 pt, tagged, unencrypted, PDF 1.7 |
| Release binary | 47,534,592 bytes on Windows x64 |
| Fresh-process whole-job time | 1,020.2–1,023.0 ms; median 1,022.3 ms over five cached-filesystem runs |

The timings are development-machine observations, not product performance
guarantees. Generated PDFs, PNGs, and timing responses live under ignored
`.tmp/` directories and are not source artifacts.

## Automated fixtures

`cargo test --locked` currently runs 13 tests covering:

1. representative interior/cover rendering;
2. independent PDF parsing and text extraction;
3. embedded-font, page geometry, encryption, and action evidence;
4. identical artifact hashes for the same input;
5. page-count-dependent cover width;
6. fail-closed PDF/X rejection;
7. protocol, trim, and path-traversal rejection;
8. stable profile serialization without legacy aliases;
9. nested Form XObject fonts, device colors, transparency groups, blend modes,
   later-page box mismatch, annotations, and `/S /JavaScript` actions;
10. Typst warning promotion to structured render failures;
11. immutable output collisions and symlink/junction containment;
12. complete process responses for malformed JSON and missing arguments;
13. strict rejection of unknown request fields.

The internal parser is defense-in-depth evidence, not an independent PDF/X
validator. It walks every page and nested Form XObject represented by the
fixture and fails page-box variation, annotations, automatic actions,
transparency, encryption, and unembedded fonts. The external Acrobat and vendor
gates remain authoritative for any future compatibility claim.

Run the complete release-level fixture, including optional Poppler rendering:

```powershell
cd Lorekeeper.Press
.\scripts\verify-spike.ps1 -PopplerBin <directory-containing-pdfinfo-and-pdftoppm>
```

This is a disposable engineering harness. It is not an end-user workflow, so no
assistant tool is exposed. Its structured diagnostics and immutable artifact
hashes are shaped for future UI/assistant parity through an owning application
service.

## External gate disposition

Adobe Acrobat Pro was not installed in the verification environment, no
PDF/X artifact could truthfully be produced, and no authenticated KDP or
IngramSpark upload was authorized or performed. Consequently:

- no Acrobat profile/version/fingerprint report exists;
- no vendor upload result exists;
- no valid or deliberately invalid artifact was submitted to an external
  PDF/X profile;
- the candidate failed before those acceptance gates, and no compatibility
  language is used.

KDP's current guidance says the full-wrap file must use the exact calculator
dimensions, include 0.125 inch bleed where required, and pass Print Previewer;
the spike's caller-supplied paper caliper is not a substitute for KDP's cover
calculator or upload check. See the
[KDP cover calculator](https://kdp.amazon.com/cover-templates?language=en_US)
and [formatting issue guidance](https://kdp.amazon.com/en_US/help/topic/G201834260).

IngramSpark's May 2026 file guide states that files must be PDF/X-1a:2001 or
PDF/X-3:2002 compliant. The spike meets neither condition. See the
[IngramSpark File Creation Guide](https://www.ingramspark.com/hubfs/downloads/file-creation-guide.pdf).

## Dependency and license evidence

All direct dependencies are exact-version pins and `Cargo.lock` is committed.
The generated
[`press-spike-license-inventory.json`](press-spike-license-inventory.json)
records 334 target-inclusive transitive packages with:

- crate version and registry source;
- crates.io checksum;
- upstream VCS commit and path where the crate archive records them;
- SPDX license expression;
- repository;
- hashes of license, copying, and notice files present in each crate archive.

Important release findings:

- Typst 0.15.1 is Apache-2.0 and resolves krilla 0.8.2
  (MIT OR Apache-2.0).
- The direct color dependency is moxcms 0.9.0
  (BSD-3-Clause OR Apache-2.0); Typst's image stack also resolves moxcms 0.8.1.
- `typst-assets` embeds Libertinus Serif, New Computer Modern, and DejaVu Sans
  Mono. Its large NOTICE file is hashed in the inventory and must be reproduced
  in an eventual distribution notice.
- Twenty-five crate archives declare a license expression but do not carry a
  top-level license-named file. A production release needs a reviewed notice
  assembly, not merely a copy of this inventory.
- No CMYK ICC profile is bundled.

Regenerate the inventory with:

```powershell
cd Lorekeeper.Press
.\scripts\generate-license-report.ps1
```

The inventory is engineering evidence, not legal advice or a completed release
approval.

## Free fallback evaluation

| Candidate | Current evidence | Disposition |
|---|---|---|
| WeasyPrint 69 | BSD licensed; current API lists `pdf/x-1a`, `pdf/x-3`, `pdf/x-4`, custom ICC output intent, and `device-cmyk()` support | **Next spike**; strongest free candidate, but native dependency packaging, pagination fidelity, CMYK profile rights, Acrobat validation, and vendor upload still must pass |
| Apache FOP | Apache-2.0 and emits PDF/X-3:2003 | Reject for this route: its own documentation calls support potentially incomplete and says normal RGB is not converted to CMYK |
| Ghostscript | Technically capable PDF conversion/preflight | Reject for bundled proprietary distribution without a commercial license; the official FAQ identifies AGPL/commercial alternatives |
| Extend krilla directly | Permissive foundation already in the graph | Reserve fallback: implementing and maintaining PDF/X validation, output intents, trapping/color rules, and conformance metadata ourselves is high risk |
| Typst + external postprocessor | Preserves the proven prose compositor | Not selected until the postprocessor independently passes license, deterministic conversion, accessibility, PDF/X, and packaging gates |

Sources:

- [WeasyPrint 69 PDF variants and output intent API](https://doc.courtbouillon.org/weasyprint/latest/api_reference.html)
- [WeasyPrint PDF/X and CMYK usage](https://doc.courtbouillon.org/weasyprint/latest/common_use_cases.html)
- [Apache FOP PDF/X limitations](https://xmlgraphics.apache.org/fop/1.1/pdfx.html)
- [Ghostscript licensing FAQ](https://ghostscript.com/faq/)

The ICC and ECI registries provide downloadable printing-condition profiles, but
registry availability does not by itself prove a profile is suitable for a
vendor/paper or grant every redistribution right Lorekeeper needs. The fallback
spike must select the vendor printing condition first, preserve the profile's
license and notices, scan the profile, hash it, and validate the emitted output
intent. See the
[ICC Profile Registry](https://registry.color.org/profile-registry/)
and [ECI downloads](https://eci.org/doku.php_id%3Den_downloads.html).
