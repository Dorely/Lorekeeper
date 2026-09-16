# Deferred plan: rich Word paste, single-chapter DOCX import, and DOCX export

Status: `Superseded on 2026-09-16 - historical plan only`

This document preserves the implementation plan agreed on 2026-08-14 as
historical context. The approved [v1 roadmap](../v1-roadmap.md) now owns active
DOCX scope, sequencing, and acceptance. In particular, v1 imports at an explicit
manuscript location rather than requiring an empty chapter, omits Word comments
and revision history, and includes citations/bibliographies. Do not implement
the conflicting requirements or reuse the historical version numbers below.
This proposal is neither current runtime behavior nor an active readiness gate.

## Summary

Add two focused Word-to-Lorekeeper authoring workflows:

- seamless, richly formatted paste from Microsoft Word and compatible
  applications directly into any manuscript editor;
- direct import of one DOCX file into one specifically selected, semantically
  empty chapter.

DOCX import never creates chapters, acts, publication sections, or metadata.
Headings and section breaks remain content inside the selected chapter; the
user or Editor assistant may restructure it afterward.

DOCX export remains available for either the manuscript or the complete
effective Core/release book.

## 1. Rich manuscript and style support

Advance the manuscript to schema v5 and extend the then-current Press protocol
(currently v10).

Add first-class Editor, assistant, search, EPUB, and PDF support for:

- nested numbered and bulleted lists;
- tables with rows, cells, spans, widths, borders, shading, and header rows;
- footnotes and endnotes;
- flowing callouts for Word text boxes;
- one-to-three-column manuscript sections;
- inline tabs, tab stops, and explicit line breaks;
- paragraph borders and shading;
- keep-lines-together and widow/orphan controls.

Expand paragraph, direct, and character formatting with:

- text and highlight colors;
- underline variants and single/double strike;
- letter spacing, all caps, and small caps;
- proportional, exact, and minimum line spacing;
- border, shading, tab-stop, and pagination settings.

Use stable semantic models such as `ListPresentation`, `TableContent`,
`ManuscriptNote`, `CalloutPresentation`, and `SectionPresentation`. Notes and
table cells own semantic manuscript fragments rather than duplicated plain
text.

Update the shared ProseMirror schema and toolbar, Book Text Style creation and
application, compact Editor-assistant operations, Review Edits, plain-text
extraction, search, context, word counts, Press pagination/tagging, and EPUB
semantics. Features unsupported by reflowable EPUB readers produce
compatibility warnings without losing semantic content.

## 2. Seamless Word paste

Replace the presentation-stripping paste normalizer with a bounded Word-aware
clipboard pipeline.

- Prefer `text/html` and parse Word HTML, stylesheets, `mso-*` properties, list
  metadata, language, links, tables, and formatting.
- Use RTF and clipboard image items as fallbacks for embedded pictures missing
  from the HTML fragment.
- Import accessible clipboard images into the project image library and insert
  Figures at their pasted positions.
- Never load `file:`, remote, or external relationship resources.
- Strip scripts, event handlers, unsafe links, active content, unsupported CSS,
  and hidden tracking metadata.

Pasted Word styles become direct paragraph and inline formatting only. Pasting
does not add Book Text Styles. Inherited Word style properties are resolved
before insertion, and unavailable font names retain a source-family label for
diagnostics while rendering through a deterministic Lorekeeper fallback.

Pasting is one undoable editor transaction. During asynchronous image
processing, preserve the insertion point and disable conflicting controls. A
chapter revision change produces a conflict instead of placing content at the
wrong location. Show a compact normalization report only when content was
changed or omitted, using human-readable locations rather than Office IDs.

## 3. Import one DOCX into one empty chapter

Add `IDocxInterchangeService.ImportIntoEmptyChapterAsync` using the exact-pinned
MIT `DocumentFormat.OpenXml 3.5.1` package.

Expose **Import DOCX** in the shared manuscript toolbar when the selected Core
or edition chapter contains only empty placeholder paragraphs.

The import:

- reads the entire Word body as one chapter manuscript;
- does not split on headings, sections, page breaks, or styles;
- does not rename the chapter or alter the outline, Core metadata, page setup,
  or publication sections;
- retains supported headings, tables, lists, Figures, notes, callouts, and
  section columns in their original order;
- creates or reuses project images;
- imports supported Word named styles as reusable Book Text Styles;
- reuses equivalent styles and gives conflicting definitions a unique imported
  name;
- keeps font family names but never transfers embedded font binaries;
- resolves tracked changes to Word's final text; imports empty classic comments
  as review highlights and non-empty classic comments as review notes; reports
  resolved comments, replies/threads, and other flattened review content.

Import begins immediately after file selection. There is no staging wizard or
multi-chapter preview.

Parse and validate the complete document before mutation. At commit time,
confirm the selected target and revision, recheck that the effective chapter is
empty, create styles/images, and replace the placeholder manuscript in one
transaction. A failure leaves the chapter, styles, and images unchanged.

Permit `.docx` only. Reject macros, ActiveX, OLE objects, encryption, traversal,
duplicate parts, external resources, malformed relationships, excessive
expanded size, and unsafe XML. Bound compressed size, entry count, expansion
ratio, XML depth, images, tables, and total semantic nodes.

After success, focus the imported manuscript and emit a target-aware mutation
notice so the Editor assistant can help restructure it.

## 4. DOCX export

Add `DocxExportOptions` and `IDocxInterchangeService.ExportAsync`.

- **Export manuscript DOCX** from Editor uses the selected Core or edition
  target.
- **Export full-book DOCX** from Publish uses Core or the selected effective
  release.
- Full-book export offers an optional front-cover toggle.

Manuscript export includes chapters only. Full-book export uses the existing
effective `PublishDocument` sequence, including selected publication sections
and edition-specific content.

DOCX output uses Word-native paragraph and character styles, headings, lists,
tables, notes, links, tabs, columns, callouts, Figures, captions, metadata, page
size, and margins. Non-empty Lorekeeper notes use classic comment
range/reference structures. Highlight-only annotations use yellow run
highlighting plus an empty comment so they return as review highlights; bare
Word highlighting remains manuscript formatting in schema v5.

Designed Pages become clean canvas PNGs at approximately 150 PPI, capped at a
4096-pixel long edge. Logical reading-order and accessibility text are stored in
the image's long-description metadata without visibly duplicating page copy.
The export report states that the canvas is rasterized and not editable as a
Word layout.

Optional cover export includes only the effective front cover. Print wraps,
spines, jackets, barcodes, and vendor production surfaces are excluded.

Validate every generated DOCX with `OpenXmlValidator`, reopen it through the
SDK, and verify parts, relationships, styles, images, and note references before
download.

Assistant parity remains compact:

- Editor can export the selected manuscript target.
- Publish can export the current full-book target and inspect fidelity
  diagnostics.
- Assistants cannot select a local DOCX file or invoke chapter import without
  the user's file selection.
- Results contain target, filename, counts, warnings, and a safe download URL,
  never document bytes or XML.

## 5. Data safety and verification

Use a protected startup migration to advance manuscript-v4 documents to v5
while preserving IDs, text, styles, Figures, Designed Pages, edition snapshots,
compositions, sidecar review annotations, and artifacts. Project export/import
is already v26 for the B&N print-preparation contract; advance it again only if
the schema-v5 payload requires another boundary version.

Only approved Lorekeeper migration/import fixtures are added or changed. Press
TDD covers the PDF-facing behavior of lists, tables, notes, callouts, columns,
tabs, borders, tagging, pagination, determinism, and cancellation.

The authorized live browser pass will:

- paste formatted Word content into Editor and publication-section prose
  editors;
- verify fonts, sizes, colors, spacing, lists, tables, images, notes, columns,
  and callouts;
- confirm paste is one undoable operation;
- import representative DOCX files into empty Core and edition chapters;
- confirm import is unavailable for non-empty chapters;
- verify failures leave the chapter unchanged;
- use the Editor assistant to restructure imported content;
- export Core and edition manuscript DOCX files;
- export full-book DOCX with and without the front cover;
- re-import an exported DOCX into a disposable empty chapter;
- inspect app-owned loading, warning, error, and download UI.

Run approved migration/import tests, Press formatting/clippy/conformance tests,
the semantic-editor build, solution build, dependency/license audit, HTTP
startup smoke check, and explicit host termination.

When this plan is activated, update the architecture index and owning routed
chapters, README, VISION delivery order, the publishing roadmap, manuscript
schema, Press requirements, assistant conventions, and DOCX interchange
documentation as part of the implementation.

## Assumptions and deferred behavior

- DOCX file import always targets one selected empty chapter.
- Rich paste may be used in any shared manuscript editor.
- Pasted formatting remains direct formatting; DOCX-imported named styles
  become reusable Book Text Styles.
- Word text boxes become flowing callouts rather than positioned page objects.
- Headers/footers, equations, charts, SmartArt, macros, embedded objects,
  bibliography/index fields, and exact Word pagination remain deferred.
- Non-empty Lorekeeper notes map to classic Word comments. Highlight-only
  annotations map to yellow Word run highlighting plus an empty comment;
  imported empty comments become highlights and non-empty comments become notes.
- Preserve that distinction with WordprocessingML's separate
  [comment range/reference](https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.wordprocessing.commentrangeend)
  and [run highlight](https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.wordprocessing.highlight)
  structures; do not add an Open XML runtime dependency until this phase starts.
- Word highlighting without a comment remains manuscript formatting under the
  planned schema-v5 formatting model. Resolved comments, replies/threads, and
  tracked-change review remain excluded or are flattened with explicit diagnostics.
- DOCX is an interchange format, never Lorekeeper's authoritative manuscript.
- Native packaged-pipeline acceptance remains the next artifact-readiness gate
  before this deferred phase is activated.
