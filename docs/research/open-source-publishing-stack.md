# Open-source publishing stack preliminary screen

Last reviewed: 2026-07-30
Research access date: 2026-07-30
Decision state: the first renderer candidate was implemented and rejected;
WeasyPrint 69 completed the fallback spike and is accepted as the contained
Preview sidecar, with release licensing and external conformance still gated.

## Executive conclusion

There is no mature, permissively licensed .NET package that can be dropped into
Lorekeeper today and truthfully guarantee professional book pagination,
PDF/X-1a:2001, color conversion, cover production, and preflight.

The first deliberately bounded renderer path was:

- **ProseMirror** for the browser-hosted semantic editor;
- **Typst** for deterministic typesetting and pagination;
- **krilla** as the PDF-writing and validation extension point already used by
  Typst;
- **moxcms** for pure-Rust ICC color transforms;
- Lorekeeper-owned PDF/X and vendor-profile rules, followed by independent
  parsing and real vendor-preflight certification;
- **EPUBCheck** for EPUB conformance after a transitive-license and packaging
  review.

The renderer should be a separately owned Rust executable (`Lorekeeper.Press`)
whose shipped dependencies satisfy the permissive-license policy, with a
versioned JSON protocol. That boundary keeps the
Blazor application provider-neutral, makes crashes and timeouts containable,
allows exact binary pinning per desktop runtime, and avoids binding business
logic to Typst source text.

The 2026-07-30 conformance spike proved the PDF 1.7 layout path and rejected
this stack as the sole Phase 1 renderer because it cannot emit PDF/X-1a. The
executable evidence and no-go decision are in
[`press-renderer-conformance-spike.md`](press-renderer-conformance-spike.md).
WeasyPrint 69 became the contained Preview fallback because its BSD-licensed API
exposes PDF/X-1a/-3/-4, CMYK colors, and custom ICC output intents. The exact
pin now renders and internally inspects deterministic Preview artifacts behind
a bounded process protocol. Those artifacts remain unapproved for release
claims until the external conformance, complete native-license, cross-platform
packaging, and vendor gates pass.

## Evidence boundary

The links and license labels below are discovery evidence captured on the
research date. Some point to moving project pages or default branches; they are
not a release-level legal or security audit. Before any component is added,
Lorekeeper must pin an exact release tag or commit and record:

- resolved direct and transitive dependency versions;
- the exact upstream license file and its SHA-256 hash;
- notices, bundled assets, fonts, ICC profiles, native libraries, and source
  distribution obligations;
- known vulnerabilities and the supported desktop runtimes;
- the package/binary hashes actually included in Lorekeeper's release.

The adoption pull request must carry that inventory and receive a separate
license review. A permissive top-level label in this brief is not approval to
ship.

## License policy

For runtime dependencies shipped in Lorekeeper:

- prefer MIT, BSD-2/3-Clause, or Apache-2.0;
- allow other licenses only after an explicit distribution and product-licensing
  decision;
- reject revenue-threshold, source-available, copyleft, network-copyleft,
  production-only commercial, or ambiguous dual-license terms by default;
- record the exact version, upstream URL, license files, notices, source offer
  obligations if any, and a hash of the audited material;
- audit transitive dependencies and bundled fonts/ICC profiles, not only the
  top-level package;
- re-run the audit on every version update.

“Free to download” and “source visible” are not sufficient.

## Recommended components

### ProseMirror — preferred semantic-editor candidate

ProseMirror is an MIT-licensed toolkit for rich semantic editing with custom
schemas. Its immutable transactions leave a replayable sequence of steps, and
its documents serialize to and from JSON. That matches Lorekeeper's need for
stable structural commands, undo, revision-aware assistant edits, and future
collaboration without making HTML authoritative.

Adoption constraints:

- Lorekeeper owns and versions the schema;
- the selected package repositories and their licenses must be audited
  individually;
- persisted JSON is a domain format, not an unversioned dump of whichever
  extension set happens to be installed;
- node IDs and reference targets are explicit Lorekeeper attributes;
- UI transactions cross a typed .NET application-service boundary;
- all HTML paste/import is sanitized and normalized through the schema.

Sources: [ProseMirror guide](https://prosemirror.net/docs/guide/) and
[repository/license](https://github.com/ProseMirror/prosemirror).

### Typst — proven PDF 1.7 reference; rejected as sole press renderer

The Typst compiler is Apache-2.0 and can be embedded or run locally. It provides
high-level document layout, font handling, automatic page breaking, headers,
footers, references, tables, and fast deterministic PDF generation. Current
documentation supports PDF 1.4–2.0 plus PDF/A and PDF/UA profiles, defaulting to
PDF 1.7.

The spike confirmed the important limitation: Typst's documented standards list
does **not** include PDF/X. It can provide layout and much of the PDF foundation,
but its ordinary export cannot be labeled PDF/X-1a. It is retained only as a
reference/benchmark unless a separately licensed and independently verified
conformance path emerges.

Sources: [Typst open-source policy](https://typst.app/open-source/),
[repository](https://github.com/typst/typst), and
[PDF export documentation](https://typst.app/docs/reference/pdf/).

### krilla — proven backend; unselected custom extension point

krilla is dual MIT/Apache-2.0. It exposes high-level PDF graphics and metadata,
font subsetting, page labels, tagged PDF, PDF versions, and validated PDF/A/UA
profiles. Its upstream tests include Arlington/veraPDF checks, snapshots, and
multiple viewers. It deliberately does not do text layout or page breaking,
which is why it complements rather than replaces Typst.

Important limitation: krilla does not currently advertise PDF/X. Implementing
the exact PDF/X-1a rules ourselves remains a last-resort fallback because it
would make Lorekeeper responsible for output intent, metadata, version, page
boxes, font embedding, permitted colors, transparency/prohibited features, and
validation diagnostics.

Source: [krilla repository and license](https://github.com/LaurenzV/krilla).

### moxcms — exercised color-conversion component

moxcms is a pure-Rust color-management library under Apache-2.0/BSD-3-Clause.
It converts among ICC profiles without adding a native C runtime. It is a strong
fit for controlled RGB-to-CMYK and grayscale conversion in the press sidecar.

The spike exercised its sRGB transform but did not select or bundle a CMYK
profile. It is not itself a PDF writer or prepress policy engine. Lorekeeper
must own rendering intents, black-generation choices, total-area-coverage
diagnostics, profile provenance, and golden-image tests.

Source: [moxcms repository and license](https://github.com/awxkee/moxcms).

### EPUBCheck — evaluate for packaged validation

EPUBCheck is the official EPUB conformance checker, BSD-3-Clause, and currently
validates EPUB 3.3. It is Java-based and includes further dependencies.

Adoption requires:

- a complete transitive-license and security audit;
- a decision whether to ship a minimal runtime, translate the checks, or invoke
  a separately packaged validator;
- structured parsing of its results into Lorekeeper diagnostics;
- proof that desktop packaging works on every declared runtime.

Until that work is complete, EPUBCheck is `Evaluate`, not an adopted runtime
dependency.

Source: [EPUBCheck repository](https://github.com/w3c/epubcheck).

## Alternatives reviewed

| Component | Evidence on review date | Decision | Reason |
|---|---|---|---|
| [QuestPDF](https://www.questpdf.com/license/community.html) | Source-available community terms with a revenue threshold; commercial tiers apply | Reject for core renderer | Strong C# API, but not a stable permissive license and no PDF/X target |
| [Ghostscript](https://ghostscript.com/faq/index.html) | AGPL or commercial | Reject for shipped runtime | Strong conversion/prepress history, but incompatible with the default permissive-distribution policy |
| [speedata Publisher](https://www.speedata.de/en/product/) | AGPL source with a separate production offering | Reject for shipped runtime | Capable automated layout, but licensing does not meet the default |
| [tc-lib-pdf](https://github.com/tecnickcom/tc-lib-pdf) | LGPL-3.0, PHP runtime | Reject for Phase 1 runtime; keep as reference | Advertises broad PDF/X support, but adds a PHP stack and a license/distribution decision outside the chosen boundary |
| [Apache FOP](https://xmlgraphics.apache.org/fop/2.11/pdfx.html) | Apache-2.0, Java/XSL-FO | Reject as primary; keep as reference | Permissive and mature, but a poor fit for interactive book design and the selected Rust sidecar; documented PDF/X support is limited |
| [PDFsharp](https://github.com/empira/PDFsharp) | MIT, .NET; moving repository evidence | Reject as primary | Useful low-level PDF API, but no book-layout or PDF/X conformance engine |
| [printpdf](https://github.com/fschutt/printpdf) | MIT, Rust; moving repository evidence | Monitor/reference | Exposes print-oriented PDF concepts, but conformance identifiers are not a validation story |
| [VMPrint](https://github.com/cosmiciron/vmprint) | Apache-2.0, TypeScript; moving repository evidence | Monitor | Interesting AI-era deterministic layout project, but too new and without proven PDF/X/color certification |
| [GoPdfSuit v6](https://pkg.go.dev/github.com/chinmay-sawant/gopdfsuit/v6) | MIT, Go; v6 published 2026-06-16 | Monitor | An actively developed AI-assisted entrant, but it has not demonstrated the required book-pagination and PDF/X certification scope |
| [PDFluent](https://pdfluent.com/) | Desktop editor is free; developer SDK is separately licensed and paid | Reject as a free runtime dependency; commercial fallback | The SDK is not open source or royalty-free merely because the editor is free |
| [Paged.js](https://github.com/pagedjs/pagedjs/) | MIT, JavaScript; moving repository evidence | Spike comparison | Strong CSS Paged Media/book preview fit, but its Chromium print path does not establish PDF/X, color, or deterministic cross-platform output |
| [WeasyPrint 69](https://doc.courtbouillon.org/weasyprint/latest/api_reference.html) | BSD-3-Clause, Python plus native/transitive dependencies | **Accepted and integrated for Preview** | The exact-pinned Windows fixture now powers contained Preview render jobs; cross-platform packaging, complete native notices, independent Acrobat/PDF-X evidence, and vendor acceptance remain open gates |
| [Vivliostyle Core 2.44.1](https://www.npmjs.com/package/%40vivliostyle/core) | AGPL-3.0 | Reject for shipped runtime | Strong web-publication layout, but the core license does not meet the default |
| [SILE](https://github.com/sile-typesetter/sile) | MIT, Lua/native toolchain; moving repository evidence | Spike comparison | Book-focused typesetting is promising, but integration, packaging, and PDF/X still require proof |
| [Chromium](https://chromium.googlesource.com/chromium/src/+/main/LICENSE) | BSD-style root license with a large mixed-license dependency inventory | Existing preview only | Lorekeeper already receives Chromium through Electron, but browser print is not PDF/X or vendor preflight; a separate full inventory would be required for any new renderer use |
| [veraPDF](https://github.com/veraPDF/veraPDF-library) | GPL-3.0 PDF/A/UA validation ecosystem | Development/reference only | Valuable for PDF/A/UA testing, but it does not validate PDF/X |
| [Apache PDFBox](https://github.com/apache/pdfbox) | Apache-2.0, Java | Development/reference | Useful independent parser; its preflight focus is PDF/A, not PDF/X |
| [qpdf](https://github.com/qpdf/qpdf) | Apache-2.0 | Development/reference | Strong structural inspection/transformation, not a typesetter or PDF/X validator |
| [Little CMS](https://github.com/mm2/Little-CMS) | MIT, native C | Fallback | Mature ICC engine; use only if pure-Rust moxcms fails the color spike |

Version numbers above record the observed release, not an approved pin. Every
`moving repository evidence` entry requires the exact-tag audit described
earlier before a spike or adoption.

## Proposed renderer boundary

Lorekeeper sends a canonical publication document to the integrated
`Lorekeeper.Press.Weasy` sidecar; it never sends arbitrary user-authored HTML,
CSS, Python, or renderer code. `Lorekeeper.Press` remains the rejected Rust
comparison implementation.

Minimum request envelope:

```json
{
  "protocolVersion": 1,
  "jobId": "stable-id",
  "operation": "render-and-preflight",
  "edition": {},
  "document": {},
  "assets": [],
  "profile": {
    "id": "ingramspark-paperback-bw",
    "version": "dated-version"
  }
}
```

The sidecar returns structured progress and a terminal result containing:

- renderer and dependency versions;
- artifact paths, media types, byte sizes, and SHA-256 hashes;
- page map and cover geometry;
- font/image/color inventory;
- diagnostics with rule, severity, location, observed/expected values, and
  correction;
- conformance claims actually validated;
- a reproducibility manifest.

Security and reliability requirements:

- no network access;
- one job-scoped input/output directory;
- allowlisted local assets identified by hash;
- bounded memory, runtime, file count, and output size;
- no shell interpolation or dynamic package download;
- cancellation and forced process-tree termination;
- protocol/schema validation on both sides;
- deterministic clocks/IDs in output metadata;
- crash logs scrubbed of manuscript content where possible;
- exact renderer binary pinned and included in the release license inventory.

## PDF/X-1a conformance spike

Before the Phase 1 renderer architecture is considered proven, implement a
representative 6 × 9 in black-and-white novel interior and color wrap cover.
The spike must demonstrate:

1. deterministic pagination, page boxes, recto starts, running heads, and
   embedded/subset fonts;
2. a PDF 1.3-compatible path for PDF/X-1a:2001, including flattened
   transparency;
3. CMYK cover output with a declared, redistributable output intent and
   grayscale interior images;
4. inspection proving that no RGB objects, unembedded fonts, prohibited
   actions, annotations, encryption, or missing page boxes remain;
5. correct spine and cover dimensions from the selected paper/binding/page
   count;
6. rendering in independent PDF engines;
7. an independent Adobe Acrobat Pro Preflight pass against the named
   PDF/X-1a:2001 profile, recording the Acrobat version, operating system,
   profile name/fingerprint, report, and PDF hash;
8. negative fixtures for RGB content, a missing output intent, unembedded
   fonts, transparency, annotations, encryption, and wrong page boxes that fail
   the same independent profile;
9. successful KDP and IngramSpark upload preflights using separate profiles.

Typst/krilla did not meet the gate. The accepted next comparison is WeasyPrint
69, followed only if needed by a direct krilla extension, a narrowly isolated
LGPL component, a commercial SDK, or a reduced certified vendor scope. Do not
hide conversion behind an uninspected command-line postprocessor.

## Validation strategy

No single validator is accepted as proof. Use layers:

- Lorekeeper domain validation before render;
- renderer invariants while constructing the file;
- independent PDF parsing for catalog, objects, fonts, images, page boxes,
  metadata, output intent, actions, annotations, encryption, and color spaces;
- normative conformance to
  [ISO 15930-1:2001](https://www.iso.org/standard/29061.html), verified with an
  independent versioned PDF/X-1a:2001 preflight profile;
- pixel snapshots across at least two independent renderers;
- golden pagination and text-extraction fixtures;
- vendor upload preflight;
- physical proof review for certification;
- dependency license/SBOM audit during packaging.

Conformance fixtures must include deliberate failures so each diagnostic is
shown to fail closed.

## Decision record

| Decision | Status |
|---|---|
| Semantic editor based on ProseMirror | Implemented with exact-pinned MIT dependencies and an owned Lorekeeper schema/adapter |
| Versioned JSON press-process protocol | Adopted for the contained Preview sidecar; production certification remains gated |
| Typst for high-level layout | Rejected as the sole Phase 1 renderer; retained as benchmark |
| krilla extension for PDF writing/conformance | Last-resort fallback, not selected |
| moxcms for ICC transforms | sRGB path exercised; CMYK/profile path unproven |
| WeasyPrint 69 fallback | Integrated as the exact-pinned Preview sidecar; native notices, cross-platform packaging, Acrobat/vendor evidence, and physical proofs remain open |
| EPUBCheck in shipped runtime | Evaluate |
| Claim PDF/X-1a support | Blocked until independent and vendor validation |
| Ship copyleft or revenue-restricted PDF dependencies | Rejected by default |

## Sources

- ProseMirror, [guide](https://prosemirror.net/docs/guide/) and
  [repository](https://github.com/ProseMirror/prosemirror).
- Typst, [open-source use](https://typst.app/open-source/),
  [PDF documentation](https://typst.app/docs/reference/pdf/), and
  [repository](https://github.com/typst/typst).
- krilla, [repository](https://github.com/LaurenzV/krilla).
- moxcms, [repository](https://github.com/awxkee/moxcms).
- W3C, [EPUBCheck](https://github.com/w3c/epubcheck).
- QuestPDF, [Community License](https://www.questpdf.com/license/community.html).
- Artifex, [Ghostscript licensing FAQ](https://ghostscript.com/faq/index.html).
- Tecnick, [tc-lib-pdf](https://github.com/tecnickcom/tc-lib-pdf).
- ISO, [ISO 15930-1:2001](https://www.iso.org/standard/29061.html).
- Adobe, [PDF/X conformance verification](https://helpx.adobe.com/acrobat/using/pdf-x-pdf-a-pdf.html)
  and [Preflight profiles](https://helpx.adobe.com/acrobat/using/preflight-profiles-acrobat-pro.html).
- Paged.js, [repository and license](https://github.com/pagedjs/pagedjs/).
- Kozea, [WeasyPrint repository and license](https://github.com/Kozea/WeasyPrint),
  [PDF variant/output-intent API](https://doc.courtbouillon.org/weasyprint/latest/api_reference.html),
  and [PDF/X/CMYK usage](https://doc.courtbouillon.org/weasyprint/latest/common_use_cases.html).
- Vivliostyle, [Core 2.44.1 package and license](https://www.npmjs.com/package/%40vivliostyle/core).
- SILE, [repository and license](https://github.com/sile-typesetter/sile).
- PDFluent, [editor and SDK licensing](https://pdfluent.com/).
- Chromium, [source license](https://chromium.googlesource.com/chromium/src/+/main/LICENSE).
