# Publishing industry workflow and file standards

Last reviewed: 2026-08-13
Research access date: 2026-08-13
Scope: trade books, front/body/back matter, retail metadata, print-on-demand
paperbacks and hardcovers, reflowable EPUB, PDF ebooks, and the submission
artifacts Lorekeeper creates without requiring a separate conversion or
preflight application.

This document records industry workflow as research evidence. References to
reviewing digital or physical proofs describe common external publishing
practice, not a Lorekeeper artifact-readiness requirement. The current product
scope is defined by [`../publishing-roadmap.md`](../publishing-roadmap.md).

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

Lorekeeper therefore distinguishes **Generic** profiles from **Specific**
profiles. Generic profiles serve unknown vendors and custom purposes without a
vendor-compatibility claim. Specific profiles are built-in, versioned contracts
for a named vendor and product, such as KDP, IngramSpark, or a future Google
Books target. Hardcover and other bindings use additional product-specific
profiles rather than inheriting paperback assumptions.

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
- final human review of the actual generated files is common publishing
  practice, but remains outside Lorekeeper's artifact contract.

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

## Current Lorekeeper implementation

Current behavior, confirmed in the repository on 2026-08-13:

- the semantic Core Book owns shared metadata, outline order, publication
  sections, typography, page setup, and reusable front-cover design;
- optional paperback, hardcover, reflowable EPUB, and PDF ebook releases inherit
  Core values until explicitly customized, while ISBN and exact product choices
  remain release-specific;
- publication sections support ordered front, body-adjacent, and back matter as
  either semantic prose with Figures or Designed Page canvases;
- title, copyright, and contents are system publication sections; contents is
  generated from the effective structure while linked title/copyright copy
  resolves from effective metadata;
- checked-in, versioned KDP and IngramSpark print-artifact profiles own
  supported constructions, paper, trim, page ranges, bleed, cover surfaces,
  required artifacts, and calculated spine/cover geometry;
- Lorekeeper Press produces cancellable, immutable, hashed PDF, cover, EPUB, and
  package artifacts with target-specific validation and in-app previews;
- the Publish workspace and assistant can edit supported metadata and matter,
  select products, design publication pages and covers, prepare files, and read
  diagnostics without mutating chapter manuscript content;
- external vendor submission, rights decisions, and final vendor acceptance
  remain human-controlled.

The runtime must describe these files as validated against the named,
versioned Lorekeeper profile, never as accepted by a vendor merely because local
preparation succeeded.

## Publication matter and presentation

Book-matter order is an editorial system, not a universal checklist. KDP's
current guidance gives a common front-matter sequence: half title, title,
copyright, praise/reviews, dedication, contents, preface, acknowledgments,
prologue, and introduction. It recommends recto starts for half title, title,
contents, and major openings, with copyright commonly verso. KDP also makes
clear that not every element is required.

General professional guidance distinguishes the jobs of the elements:

- half title: title only;
- title page: title, subtitle, and principal attribution/imprint as appropriate;
- copyright: user-approved publication and rights information;
- foreword: contextual endorsement or introduction normally written by someone
  other than the author;
- preface: the author's account of purpose, scope, or making of the book;
- prologue: narrative material preceding the main body;
- introduction: orientation to the work or subject;
- back matter: only the notes, appendices, glossary, references/bibliography,
  index, acknowledgments, author information, and related-work material that
  serves this particular book.

Lorekeeper should preserve authored inclusion, order, and start-side decisions.
The assistant may recommend conventions, but must distinguish them from a
selected product's enforceable validation. For reflowable EPUB, semantic
headings, navigation, reading order, and accessibility replace print parity;
the assistant must not simulate recto/verso with meaningless blank ebook pages.

Sources: [KDP Front Matter, Body, and Back Matter](https://kdp.amazon.com/en_US/help/topic/GDDYZG2C7RVF5N9J)
and [Oxford University Press front and end matter guidance](https://academic.oup.com/pages/for-authors/books/the-book-publishing-process/writing-and-content-preparation/front-and-end-matter).

## Retail metadata and the current boundary

IngramSpark's metadata guidance emphasizes consistency among the title page,
cover, metadata fields, formats, series and edition statements, and contributor
roles. It treats the description as consumer-facing copy and recommends useful
subject classification, audience information, and search terms. KDP and Ingram
portals also request commercial and distribution data that is separate from the
interior and cover files.

Lorekeeper currently stores Core/release title, subtitle, author, language,
publisher, copyright, description, ISBN, content settings, and selected product.
It does not store every portal field. The Publish assistant must complete and
cross-check supported fields, then identify BISAC/Thema classifications,
keywords, expanded contributor roles, pricing, territories, publication dates,
and other unsupported portal metadata as explicit external follow-up. It must
not claim to store, submit, or validate absent fields.

Sources: IngramSpark [Title Metadata Guide](https://www.ingramspark.com/hubfs/downloads/title-metadata-guide.pdf)
and [Print Book Setup Guide](https://www.ingramspark.com/hubfs/Print%20Book%20Setup%20Guide.pdf).

## Publish assistant knowledge contract

The code-owned Publish instructions should encode compact, durable judgment
rather than copies of vendor manuals:

1. audit book type, audience, formats, current Core/release state, metadata,
   matter, design, product, and diagnostics;
2. complete only appropriate supported content and metadata;
3. establish semantic typography, page presentation, and cover hierarchy;
4. select the exact versioned product and read its current calculated rules;
5. preflight and prepare through the owning application services;
6. report exact artifacts, diagnostics, inheritance/override state, and
   remaining external actions.

The assistant must maintain consistency across effective metadata, system pages,
user-authored matter, covers, and releases. It must never invent ISBNs, rights,
legal notices, endorsements, contributors, publisher identity, or commercial
terms. It distinguishes four evidence levels: professional convention, current
vendor documentation, the selected product's Lorekeeper validation, and
acceptance after a real vendor upload.

Mutations use the same application services and revision checks as the UI.
Chapter text and chapter layouts remain Editor-owned; Publish owns publication
sections, release settings, publication page/canvas work, covers, preparation,
and artifacts.

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
- representative outputs have been visually inspected for geometry and content
  regressions without making that inspection a persisted readiness gate;
- the same inputs reproduce byte-stable or semantically equivalent outputs
  under a pinned renderer;
- failure messages identify the edition, page/object, rule, observed value, and
  correction;
- UI and assistant workflows expose the same settings and results.

Until these gates pass, a profile is `In development` or `Preview`, never
“print-ready.”

## Sources

- Amazon KDP, [Format Your Paperback](https://kdp.amazon.com/en_US/help/topic/G201857950) and
  [Paperback and Hardcover Cover Requirements](https://kdp.amazon.com/en_US/help/topic/G201953020),
  and [Front Matter, Body, and Back Matter](https://kdp.amazon.com/en_US/help/topic/GDDYZG2C7RVF5N9J).
- IngramSpark, [File Creation Guide](https://www.ingramspark.com/hubfs/downloads/file-creation-guide.pdf),
  [Title Metadata Guide](https://www.ingramspark.com/hubfs/downloads/title-metadata-guide.pdf),
  and [Print Book Setup Guide](https://www.ingramspark.com/hubfs/Print%20Book%20Setup%20Guide.pdf).
- Oxford University Press, [Front and end matter](https://academic.oup.com/pages/for-authors/books/the-book-publishing-process/writing-and-content-preparation/front-and-end-matter).
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
