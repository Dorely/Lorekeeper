# Publishing industry workflow and file standards

Last reviewed: 2026-07-29
Research access date: 2026-07-29
Scope: trade books, print-on-demand paperbacks, reflowable EPUB, and the
submission artifacts Lorekeeper should eventually create without requiring a
separate conversion or preflight application.

## Executive conclusion

Publishers and print-on-demand services do not accept an editable manuscript as
the final print artifact. The normal workflow turns source content into
edition-specific, validated deliverables:

1. acquire and structurally edit the manuscript;
2. copyedit and resolve queries;
3. design the interior and cover;
4. paginate and proof the exact edition;
5. preflight fonts, images, color, trim, bleed, metadata, and PDF constraints;
6. deliver separate interior and full-wrap cover files for print, plus EPUB and
   a front-cover image for digital editions;
7. review a digital proof and, where appropriate, a physical proof before
   release.

The durable product implication is that Lorekeeper needs one semantic manuscript
feeding multiple versioned publication editions and immutable rendered
artifacts. A browser print dialog is not a press-output system, and a generic
“PDF” label is not a conformance claim.

## Evidence: what services expect

Requirements change, and a service can vary them by trim, paper, binding,
market, or page count. Lorekeeper must version its vendor profiles and show the
source and review date behind every profile.

| Destination | Interior | Cover | Important constraints |
|---|---|---|---|
| Amazon KDP print | One PDF with single pages; DOC/DOCX, RTF, HTML, or TXT are accepted for some non-bleed workflows, but PDF is the dependable layout-preserving submission | Separate one-page PDF containing back, spine, and front, or KDP Cover Creator | Embed fonts; use 300 DPI images; remove comments, annotations, security, and unintended marks; bleed extends 0.125 in; cover uses the calculated spine; KDP recommends CMYK images and flattened transparency |
| IngramSpark print | Separate interior PDF | Separate full-wrap cover PDF made to the title-specific template | File Creation Guide 5.11.26 requires PDF/X-1a:2001 or PDF/X-3:2002, embedded fonts, 300 PPI images, correct page size/bleed, and grayscale images for black-and-white interiors. The exact accepted standard must remain profile-versioned rather than globally assumed |
| Lulu print | One single-page-layout interior PDF | Separate single-page full-wrap PDF | Fonts embedded or outlined; images around 300 PPI; transparency flattened; no trim/bleed marks; 0.125 in bleed; page and cover dimensions must match the selected product |
| Blurb PDF-to-Book | One PDF of individual pages with dimensions from its product calculator | Separate cover PDF for the normal two-file workflow, or supported single-file layouts | Even page count, no printer marks, correct full bleed, 100% black for body text, no spot/registration colors, and product-specific dimensions |
| EPUB retailers and aggregators | EPUB, normally EPUB 3, with store metadata supplied separately | Front-cover raster image and a cover represented in the EPUB package | EPUB is a ZIP-based package containing package metadata, navigation, XHTML content, CSS, and resources. Reflowable EPUB is the default for prose; fixed layout is for content whose meaning depends on exact placement |

Sources: [KDP manuscript requirements](https://kdp.amazon.com/en_US/help/topic/G201857950),
[KDP cover requirements](https://kdp.amazon.com/en_US/help/topic/G201953020),
[IngramSpark File Creation Guide 5.11.26](https://www.ingramspark.com/hubfs/downloads/file-creation-guide.pdf),
[Lulu PDF creation settings](https://help.lulu.com/en/support/solutions/articles/64000255519-pdf-creation-settings),
[Blurb PDF checklist](https://support.blurb.com/hc/en-us/articles/207792946-PDF-to-Book-specifications-and-checklist),
and [EPUB 3.3](https://www.w3.org/TR/epub-33/).

### Shared print invariants

Across the reviewed services, the repeated requirements are:

- an interior PDF made of individual pages rather than reader spreads;
- a separate full-wrap cover whose width depends on trim, binding, paper, and
  final interior page count;
- exact page dimensions, trim, margins, safety area, and bleed;
- embedded fonts and sufficient image resolution;
- consistent page size and orientation;
- no password protection, review markup, accidental blank pages, or printer
  marks unless a particular printer explicitly requests them;
- controlled color spaces, with grayscale/black handling for monochrome
  interiors and color-managed cover output;
- metadata and visible title/author/ISBN information that agree;
- a final human proof of the actual generated files.

These are shared patterns, not universal numeric values. Lorekeeper must obtain
the numbers from a selected edition and vendor profile.

### PDF/X is a bounded conformance target

PDF/X is a family of ISO print-exchange standards, not a synonym for “high
quality PDF.” A PDF/X profile constrains such matters as fonts, color spaces,
output intent, page boxes, transparency, and prohibited interactive features.
The output must be validated against the exact requested part and year.

For the Phase 1 Ingram-targeted edition, Lorekeeper will investigate and certify
PDF/X-1a:2001 against the normative `ISO 15930-1:2001` revision only if the
current Ingram profile still requires or recommends it at implementation time.
KDP can use a separately rendered, validated PDF 1.7 edition if the
implementation spike proves that profile against KDP's current upload checks.
One lowest-common-denominator file must not silently stand in for both.

Certification cannot be based on Lorekeeper's own writer and parser agreeing
with each other. The gate requires an independent standards validator, initially
Adobe Acrobat Pro Preflight's named PDF/X-1a:2001 compliance profile. The
evidence record captures the Acrobat version, operating system, preflight
library/profile name and fingerprint, result report, PDF hash, and the exact ISO
revision. Deliberately invalid fixtures for RGB content, absent output intent,
unembedded fonts, transparency, annotations, encryption, and wrong page boxes
must fail that external check. Internal parsing, independent visual rendering,
and vendor upload preflights remain additional gates; none substitutes for the
normative conformance check.

Sources: [ISO 15930-1:2001](https://www.iso.org/standard/29061.html),
[Adobe Acrobat PDF/X conformance verification](https://helpx.adobe.com/acrobat/using/pdf-x-pdf-a-pdf.html),
[Adobe Acrobat Preflight profiles](https://helpx.adobe.com/acrobat/using/preflight-profiles-acrobat-pro.html),
and [PDF Association, “PDF/X in a Nutshell”](https://pdfa.org/wp-content/uploads/2017/05/PDFX-in-a-Nutshell.pdf).

## Evidence: digital publication requirements

EPUB 3.3 is a W3C Recommendation. Its publication container includes package
metadata and a reading order, and its content documents use web technologies.
EPUB Accessibility 1.1 adds discoverability and accessibility metadata and
conformance requirements. Fixed-layout EPUB preserves authored geometry but has
additional accessibility risks because visual order, source order, resizing, and
reflow may diverge.

Lorekeeper should therefore:

- generate semantic reflowable EPUB for novels and ordinary illustrated prose;
- reserve fixed layout for picture books and layout-dependent material;
- generate navigation, landmarks, language, semantic headings, alt text, and
  accessibility metadata from structured source data;
- validate every EPUB with EPUBCheck, while treating a clean EPUBCheck result as
  necessary but not sufficient for visual or accessibility quality;
- preview in more than one reading system before declaring an EPUB edition
  verified.

Sources: [EPUB 3.3](https://www.w3.org/TR/epub-33/),
[EPUB Accessibility 1.1](https://www.w3.org/TR/epub-a11y-11/),
[EPUB Fixed Layout Accessibility](https://www.w3.org/TR/epub-fxl-a11y/), and
[EPUBCheck](https://github.com/w3c/epubcheck).

## Evidence: identifiers and metadata

An ISBN identifies a particular publication, edition, and format. A materially
different product form needs a distinct ISBN; a paperback and EPUB should not
share one. ISBNs are represented in retail barcodes using EAN-13. Rich trade
metadata is commonly exchanged through ONIX for Books, but ONIX is broader than
the metadata required for Phase 1 file generation.

Lorekeeper should model identifiers on publication editions, not on the abstract
project. It should validate but never silently purchase, allocate, or reuse an
ISBN. Phase 1 must generate and validate an EAN-13 barcode from a user-supplied
ISBN, including checksum, scannable dimensions, quiet zones, contrast, and
placement checks. Ingram's profile includes the barcode in the generated cover;
KDP's profile allows either that embedded barcode or a deliberately empty
reserve for KDP's overlay. Purchasing, assigning, inventorying, and governing
ISBNs remain later publisher-operations work.

Sources: [ISBN Users' Manual](https://www.isbn-international.org/index.php/content/isbn-users-manual/29),
[ISO 2108 overview](https://www.iso.org/standard/65483.html),
[GS1 ISBN barcode guidance](https://support.gs1.org/support/solutions/articles/43000734165-how-is-an-isbn-used-in-a-gs1-barcode-),
and [EDItEUR ONIX for Books](https://www.editeur.org/83/Overview/).

## Current Lorekeeper gap

Current behavior, confirmed in the repository on the review date:

- chapters persist their body as a plain string;
- TXT, Markdown, and EPUB are generated by export formatters;
- “Print / PDF” opens an HTML print view and delegates file creation to the
  browser/operating-system print path;
- one `PublishProfile` owns general page metrics and presentation choices;
- no edition-scoped identifier, vendor profile, rendered artifact, preflight
  report, PDF standard declaration, output intent, or proof approval is stored;
- the cover is selected from a Picture Page rather than built as a
  page-count-dependent full wrap;
- assistant tools do not have a complete publishing control surface.

The current preview and exports remain useful composition features, but they
must not be described as vendor-certified print-ready files.

## Lorekeeper implementation decisions

### Canonical source and edition model

The editable source will be a versioned semantic manuscript. A
`PublicationEdition` will contain the choices that can legitimately differ
between products:

- format and destination profile;
- trim, binding, paper, ink, bleed, safety, and margins;
- typography and named-style mapping;
- included matter and content order;
- ISBN and edition metadata;
- cover variant and calculated spine;
- renderer/profile version;
- artifact hashes, preflight results, proof state, and export history.

The existing publish profile becomes the initial default edition during a safe
migration. It is not discarded.

### Render, preflight, package

Every export follows an inspectable pipeline:

```text
semantic manuscript
  -> normalized publication document
  -> edition-specific pagination
  -> interior and cover render
  -> structural and visual preflight
  -> immutable artifact set + manifest + report
  -> explicit user proof approval
```

The package contains the actual deliverables, not instructions to use another
application:

- interior PDF;
- full-wrap cover PDF;
- EPUB and front-cover image when selected;
- machine-readable manifest with hashes, profile versions, dimensions, fonts,
  color/output-intent information, and validation results;
- human-readable preflight report.

### Phase 1 certification boundary

The first certified scope is deliberately narrow:

- prose-first novels;
- black-and-white paperback interior;
- color full-wrap cover;
- KDP and IngramSpark vendor profiles;
- reflowable EPUB 3;
- actual downloadable PDF preview and artifacts.

Hardcover, dust jackets, color interiors, fixed-layout EPUB, rich nonfiction,
ONIX, and direct retailer submission remain later phases.

### Assistant parity

The Publish assistant must read everything a user can inspect and invoke every
safe operation a user can invoke:

- list, create, clone, update, and archive editions;
- select content and front/back matter;
- manage named styles and edition overrides;
- set vendor/product geometry and metadata;
- calculate the cover template after pagination;
- request render, preflight, preview, and package creation;
- enumerate issues with locations and suggested corrections;
- compare artifacts and explain why pagination changed.

Mutations use the same application services as the UI, honor revision tokens,
and enter the existing review/approval system where appropriate. Export and
preflight may be automated. Proof approval and any external submission remain
explicit user decisions.

## Acceptance evidence for a certified profile

A vendor profile is `Verified` only when all of the following exist:

- its source requirements and access date are recorded;
- its normative standard revision and independent validator/profile versions
  are recorded when a standards claim is made;
- unit and integration fixtures cover page boxes, fonts, images, color,
  pagination, cover dimensions, and prohibited features;
- the PDF is independently parsed and passes an external standards validator
  with deliberate negative fixtures, rather than being trusted because the
  renderer returned success;
- representative files pass the vendor's upload preflight;
- physical proofs have been inspected for at least the supported reference
  products;
- the same inputs reproduce byte-stable or semantically equivalent outputs
  under a pinned renderer;
- failure messages identify the edition, page/object, rule, observed value, and
  correction;
- UI and assistant workflows expose the same settings and results.

Until these gates pass, a profile is `In development` or `Preview`, never
“print-ready.”

## Sources

- Amazon KDP, [Format Your Paperback](https://kdp.amazon.com/en_US/help/topic/G201857950) and
  [Paperback and Hardcover Cover Requirements](https://kdp.amazon.com/en_US/help/topic/G201953020).
- IngramSpark, [File Creation Guide 5.11.26](https://www.ingramspark.com/hubfs/downloads/file-creation-guide.pdf).
- Lulu, [PDF Creation Settings](https://help.lulu.com/en/support/solutions/articles/64000255519-pdf-creation-settings)
  and [Upload Your Cover File](https://help.lulu.com/en/support/solutions/articles/64000282777-upload-your-cover-file).
- Blurb, [PDF to Book Specifications and Checklist](https://support.blurb.com/hc/en-us/articles/207792946-PDF-to-Book-specifications-and-checklist).
- W3C, [EPUB 3.3](https://www.w3.org/TR/epub-33/),
  [EPUB Accessibility 1.1](https://www.w3.org/TR/epub-a11y-11/), and
  [EPUB Fixed Layout Accessibility](https://www.w3.org/TR/epub-fxl-a11y/).
- W3C, [EPUBCheck](https://github.com/w3c/epubcheck).
- PDF Association, [PDF/X in a Nutshell](https://pdfa.org/wp-content/uploads/2017/05/PDFX-in-a-Nutshell.pdf).
- ISO, [ISO 15930-1:2001](https://www.iso.org/standard/29061.html).
- Adobe, [PDF/X conformance verification](https://helpx.adobe.com/acrobat/using/pdf-x-pdf-a-pdf.html)
  and [Preflight profiles](https://helpx.adobe.com/acrobat/using/preflight-profiles-acrobat-pro.html).
- International ISBN Agency, [ISBN Users' Manual](https://www.isbn-international.org/index.php/content/isbn-users-manual/29).
