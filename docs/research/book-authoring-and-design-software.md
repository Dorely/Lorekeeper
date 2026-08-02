# Book authoring and visual-design software gap analysis

Last reviewed: 2026-08-01
Research access date: 2026-07-29

Implementation update: the unified manuscript-v3 Figure/Designed Page model,
shared page/cover scene engine, geometry-bound generation, project fonts,
color/bleed Press rendering, tagged Digital PDF, fixed-layout EPUB, and compact
assistant parity are implemented. The original gap inventory below is retained
as the research basis; `docs/publishing-roadmap.md` is the current status source.

## Executive conclusion

Lorekeeper already has a differentiated narrative workspace: graph-backed canon,
retrieval, multiple assistants, reviewable AI changes, image workflows,
illustrated prose, and Picture Pages. It does not yet have the document model,
editorial review mechanics, deterministic composition engine, or production
controls expected of end-to-end book software.

The right direction is not to copy a single competitor. Lorekeeper should combine
four established product shapes:

- Scrivener's project organization and snapshots;
- Word's editorial review and semantic styles;
- Atticus, Vellum, and Reedsy's one-source/multiple-edition simplicity;
- InDesign, Affinity Publisher, QuarkXPress, and Scribus's composition,
  preflight, packaging, and press controls.

## Evidence: product capability families

### Long-form authoring

Scrivener organizes a manuscript and research in a binder, supports corkboard
and outline views, keeps document snapshots, and compiles selected material into
multiple output formats. Its strength is planning and source organization rather
than final press production.

Source: [Scrivener overview](https://www.literatureandlatte.com/scrivener/overview).

### Editorial collaboration

Word supports named styles whose changes propagate through matching text,
tracked insertions/deletions/moves/formatting, comments, accept/reject review,
cross-references, generated tables of contents, and indexes. These are baseline
expectations for professional author-editor exchange.

Sources: [Word styles](https://support.microsoft.com/en-us/word/customize-or-create-new-styles),
[Track Changes](https://support.microsoft.com/en-us/word/training/track-changes-in-word),
[cross-references](https://support.microsoft.com/en-us/word/create-a-cross-reference),
[table of contents](https://support.microsoft.com/en-us/word/update-a-table-of-contents),
and [indexes](https://support.microsoft.com/en-us/word/create-and-update-an-index).

### Author-focused formatting

Atticus uses one book to export EPUB and print PDF, while allowing duplicated
versions for format-specific choices. It includes writing organization,
formatting themes, previews, trim/layout settings, and EPUB/PDF export, but its
support documentation says print covers are not exported. Vellum applies
book-oriented styles and separate print settings, headers, body typography, and
preview behavior. Reedsy Studio exports EPUB 3 and PDF/X-1a:2001 and presents a
smaller theme-driven formatting surface.

Sources: [Atticus product overview](https://www.atticus.io/),
[Atticus support](https://www.atticus.io/support/),
[Vellum print settings](https://help.vellum.pub/print/),
[Vellum styles](https://help.vellum.pub/styles/), and
[Reedsy Studio formatting](https://reedsy.com/studio/format-a-book).

### Professional page design

InDesign and its peers provide a much deeper object and production model:
paragraph/character/object styles, parent/master pages, book files, page
numbering, grids and guides, linked assets, layers, text frames, tables, notes,
cross-references, TOC/index generation, color management, PDF presets, preflight,
and packaging. InDesign's preflight can flag missing fonts, broken links,
low-resolution images, overset text, and color problems against a reusable
profile. Affinity Publisher and QuarkXPress expose comparable book, master-page,
reference, and preflight concepts; Scribus demonstrates that many press-oriented
features can be implemented in open source.

Sources: [InDesign styles](https://helpx.adobe.com/indesign/using/styles.html),
[InDesign book files](https://helpx.adobe.com/indesign/using/creating-book-files.html),
[InDesign preflight and package](https://helpx.adobe.com/indesign/using/preflighting-files-handoff.html),
[Affinity Publisher help](https://affinity.help/publisher2/English.lproj/index.html),
[QuarkXPress book features](https://www.quark.com/documentation/quarkxpress/2024/english/User%20Guide/_5_409772980395826E7_13.html),
and [Scribus specifications](https://wiki.scribus.net/canvas/Help%3ASpecifications).

## Capability matrix

Status reflects repository behavior on the review date:

- `Present`: coherent user-facing capability exists.
- `Partial`: a related capability exists, but not at professional book scope.
- `Missing`: no coherent implementation exists.
- `Later`: intentionally outside the first delivery phase.

| Capability | Lorekeeper | Required destination | Delivery |
|---|---|---|---|
| Project binder, acts, chapters, reorder | Present | Preserve and make the manuscript spine edition-selectable | Phase 1 |
| Graph-backed canon and retrieval | Present | Preserve as Lorekeeper's differentiator | Existing |
| AI-assisted drafting and reviewable mutations | Present | Move from whole-body strings to addressable semantic ranges | Phase 1 |
| Semantic document model | Missing | Blocks, inline marks, stable IDs, schema version, revisions, references | Phase 1 |
| Rich text editing | Partial | headings, emphasis, small caps intent, lists, block quotes, scene breaks, links, special characters, undo/redo, keyboard semantics | Phase 1 |
| Named paragraph/character styles | Missing | semantic roles mapped separately per edition | Phase 1 |
| Find/replace and document navigation | Partial | project/selection scope, regex or structural filters, result preview | Phase 1/2 |
| Deterministic pagination | Missing | widow/orphan control, keep rules, recto starts, blank-page accounting, stable page map | Phase 1 |
| Front/back matter builder | Partial | title, copyright, dedication, epigraph, contents, acknowledgments, about author, also-by, custom matter | Phase 1 |
| Multiple publication editions | Missing | independently versioned paperback/EPUB/vendor settings and artifacts | Phase 1 |
| True PDF preview/export | Missing | renderer-produced PDF bytes viewed in-app | Phase 1 |
| Full-wrap cover | Missing | back/spine/front, template, barcode reserve, page-count-dependent spine | Phase 1 |
| Vendor-aware preflight/package | Missing | versioned profiles, actionable diagnostics, manifest and artifacts | Phase 1 |
| EPUB 3 validation/accessibility | Partial | semantic nav/landmarks/metadata/alt text plus EPUBCheck | Phase 1 |
| Track changes and comments | Partial | insert/delete/move/format changes, comments, queries, authorship, accept/reject | Phase 2 |
| Professional DOCX interchange | Missing | import/export named styles, comments, tracked changes, document metadata, and explicit fidelity diagnostics | Phase 2 |
| Version comparison and page proofs | Partial | snapshots, semantic compare, edition page-map comparison, proof annotations | Phase 2 |
| Editorial modes and house style | Missing | developmental/copy/line/proof modes, term/style rules, consistency reports | Phase 2 |
| International typography | Partial | Phase 1 certifies English and tested Latin-script LTR text; complex shaping, RTL, CJK, vertical text, line-breaking, and localized proofing require dedicated profiles | Phase 3 |
| Master/parent pages and templates | Missing | reusable page geometry and running furniture | Phase 3 |
| Frames, guides, grids, snapping | Partial | direct manipulation with deterministic constraints | Phase 3 |
| Shapes, paths, masks, text wrap, layers | Partial | page-object model and z-order/locking/visibility | Phase 3 |
| Advanced color and press controls | Missing | ICC profiles, CMYK/spot policy, ink limits, separations/soft proof | Phase 3 |
| Fixed-layout EPUB | Partial | semantic reading order, accessibility, device preview and validation | Phase 3 |
| Tables, notes, citations, bibliography | Missing | structured, referenceable, renumberable objects | Phase 4 |
| Figures, captions, cross-references | Missing | stable targets and generated labels | Phase 4 |
| TOC, index, glossary | Missing | generated structures from semantic source | Phase 4 |
| Equations and code | Missing | semantic source plus accessible print/digital rendering | Phase 4 |
| Rights, ISBN, contributors, imprints | Missing | edition-scoped publishing operations | Phase 5 |
| ONIX, catalog, submissions, audit | Missing | publisher operations with explicit external-action approval | Phase 5 |

## Editing tools Lorekeeper is missing

### Phase 1 authoring tools

- semantic block insertion, deletion, split, merge, duplicate, and movement;
- paragraphs, headings, scene breaks, block quotes, lists, images/figures, and
  custom front/back-matter blocks;
- inline emphasis, strong emphasis, underline where deliberately supported,
  small-caps intent, superscript/subscript, links, language spans, and character
  styles;
- named paragraph and character styles, with style usage search;
- paste/import normalization that preserves supported intent and reports
  discarded formatting;
- document outline, word/character counts, selection statistics, and
  project/edition navigation;
- undo/redo over semantic transactions;
- special-character and discretionary-break controls;
- find/replace with a previewable result set;
- structural validation before saving or export.

### Phase 2 professional editorial tools

- suggestions with insertion, deletion, replacement, move, and formatting
  operations;
- comments, threaded replies, editorial queries, assignments, and resolution;
- filter by author, date, type, chapter, status, or editorial pass;
- accept/reject one, selection, chapter, filtered set, or all;
- compare revisions and show changed passages without line-number fragility;
- snapshots and named milestones;
- DOCX import/export that round-trips supported named styles, comments, tracked
  changes, authorship, dates, and document metadata, while surfacing every
  unsupported or lossy construct before acceptance;
- copyediting rules for spelling variants, capitalization, numbers, quotations,
  punctuation, character names, and forbidden terms;
- style sheet/house-style workspace;
- proof annotations anchored to an edition and page-map version;
- read-aloud and accessibility review hooks.

### Phase 3 visual composition tools

- parent/master pages and reusable page templates;
- margins, columns, baseline grids, rulers, guides, snapping, alignment, and
  distribution;
- text and image frames, linking/overflow, wrap contours, crop/focal point,
  fitting modes, and captions;
- page/spread thumbnails, insert/delete/move, recto/verso control;
- shapes, strokes, fills, gradients, transparency, clipping masks, groups, and
  layers;
- object styles and reusable components;
- color swatches, ICC profiles, total-ink diagnostics, separations, and soft
  proofing;
- semantic reading order and alt text independent of visual z-order;
- script-aware font fallback, shaping, bidirectional text, CJK line-breaking,
  vertical writing where supported, localized hyphenation/dictionaries, and
  language-profile fixtures. Phase 1's certified output remains English and
  explicitly tested Latin-script left-to-right text until these pass.

### Phase 4 reference-book tools

- footnotes, endnotes, citations, bibliography, and source manager;
- figures, tables, captions, equations, code blocks, callouts, and sidebars;
- stable cross-references and automatic numbering;
- generated TOC, lists of figures/tables, index, and glossary;
- table editing with repeating headers and accessible structure;
- reference integrity and missing-target preflight.

## How the tools fit Lorekeeper's current product

### Editor workspace

Replace the chapter textarea with a schema-driven editor while retaining acts,
chapters, graph context, contests, revision agents, and review UI. The editor
operates on domain commands, not raw HTML. Existing prose-only behavior becomes
a schema profile rather than a separate body-string path.

### Publish workspace

Evolve the current Publish tab around editions:

- left: edition list and content/matter outline;
- center: actual paginated PDF or EPUB device preview;
- right: edition settings, named-style overrides, preflight, and artifact
  history;
- dedicated cover mode: template, spine, back/front frames, safety, and barcode
  reserve.

### Designed Page and cover workspaces

Use one scene vocabulary for Designed Pages/spreads and format-aware covers,
while keeping page semantic fragments project-owned and cover copy edition-owned.
Implemented core objects, layers, styles, guides, grouping, reading order, and
exact geometry variants leave master pages, linked frames, SVG/path tooling,
advanced effects, and manual page intervention as additive future work rather
than a competing layout stack.

### AI assistants

Do not give assistants direct database or editor-HTML mutation. Expose the same
domain commands used by the UI:

- reads return stable block/object IDs, revision tokens, and bounded content;
- writes specify semantic operations and expected revisions;
- large transformations return a proposed change set;
- application services validate schema, permissions, references, geometry, and
  stale revisions;
- accepted changes update persistence, indexes, graph mentions, previews, and
  artifact staleness together.

The Editor assistant owns manuscript and editorial operations. A dedicated
Publish assistant owns edition, style, layout, preflight, and packaging
operations. Cross-surface work is coordinated through stable IDs and shared
services rather than duplicate tools.

## Product principles derived from the comparison

1. **Semantic intent before appearance.** “Chapter title” and “body paragraph”
   are source concepts; font and spacing are edition mappings.
2. **One source, explicit editions.** Reflowable EPUB and paperback can share
   content without pretending their layouts are identical.
3. **Automation with escape hatches.** Good defaults should produce a credible
   novel, while advanced controls remain available and inspectable.
4. **Proofs are artifacts.** A proof is tied to exact content, settings, assets,
   renderer, and profile versions.
5. **AI parity is part of feature completeness.** A feature without complete,
   safe assistant reads and commands is unfinished.
6. **No hidden external-tool requirement.** Certified paths render, validate,
   preview, and package inside Lorekeeper.

## Sources

- Literature & Latte, [Scrivener overview](https://www.literatureandlatte.com/scrivener/overview).
- Microsoft, [Word Track Changes](https://support.microsoft.com/en-us/word/training/track-changes-in-word)
  and [Word styles](https://support.microsoft.com/en-us/word/customize-or-create-new-styles).
- Adobe, [InDesign styles](https://helpx.adobe.com/indesign/using/styles.html),
  [book files](https://helpx.adobe.com/indesign/using/creating-book-files.html),
  and [preflight/package](https://helpx.adobe.com/indesign/using/preflighting-files-handoff.html).
- Serif, [Affinity Publisher 2 help](https://affinity.help/publisher2/English.lproj/index.html).
- Quark, [QuarkXPress 2024 User Guide](https://www.quark.com/documentation/quarkxpress/2024/english/User%20Guide/_5_409772980395826E7_13.html).
- Scribus, [specifications](https://wiki.scribus.net/canvas/Help%3ASpecifications).
- Atticus, [product overview](https://www.atticus.io/) and
  [support](https://www.atticus.io/support/).
- 180g, [Vellum print help](https://help.vellum.pub/print/) and
  [styles](https://help.vellum.pub/styles/).
- Reedsy, [Studio book formatting](https://reedsy.com/studio/format-a-book).
