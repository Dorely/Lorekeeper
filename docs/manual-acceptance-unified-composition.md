# Unified composition manual acceptance checklist

Use this checklist after automated migration, editor, Press, build, and startup
verification. Browser/Electron visual automation is intentionally not part of
this feature's authorized checks.

## Outline and assistants

- Confirm Outline describes chapters as semantic text, Figures, and Designed
  Pages without offering chapter visual types.
- Check fiction, nonfiction, picture-book, illustrated-book, poetry, and hybrid
  briefs receive concise relevant guidance subordinate to explicit direction.
- Ask Outline, Editor, Images, and Publish to read and mutate a Figure and a
  composition; verify compact IDs/revisions/diagnostics and conflict recovery.
- Stage a large scene once, apply by stage ID, and verify the payload is neither
  repeated nor replayable.
- Confirm every assistant defaults reusable art and ordinary flowing Figures to
  free-standing generation, and uses a geometry-bound target only when artwork
  must honor a concrete page, frame, Figure placement, or cover region. Verify
  bound guidance does not crop or resize the stored raster, and confirm no
  assistant can approve a proof, claim vendor acceptance, or claim a URL was
  downloaded.

## Manuscript Figures

- Insert/replace a project image and edit caption, alt text, decorative state,
  alignment, width, wrap, fit, crop position, bleed, caption placement, and
  page-break behavior.
- Confirm arbitrary portrait, landscape, and square source rasters insert
  without aspect-ratio rejection. `Contain` must show the whole raster and
  `Cover` must fill its frame non-destructively.
- Verify undo/redo, autosave, revision conflict recovery, stable IDs, paste,
  search/index text, and plain-text/Markdown/EPUB projections.
- Generate art for a Figure target and confirm the displayed aspect, raster,
  intended fit, and geometry descriptor match the project/page target without
  requiring a publication edition.

## Edit and Read modes

- Confirm Edit exposes common block style, emphasis, link, alignment, whole
  paragraph indent, lists, Figure/Designed Page insertion, search, undo, and redo
  in the primary toolbar; advanced marks, paragraph controls, Figure settings,
  and Book Text Styles remain available without crowding it.
- Exercise toolbar and Tab/Shift+Tab indentation, first-line and hanging indent,
  spacing, keep-with-next, and start-on-new-page. Confirm inline emphasis survives
  Book Text Style changes and clearing paragraph formatting removes only direct
  paragraph presentation.
- Open Advanced at both edges of the editor and confirm its panel remains fully
  inside the viewport. Insert a project image through the Figure picker, insert
  a Designed Page and confirm Pages opens immediately, then click List twice and
  confirm the block returns to ordinary body text. Confirm Figure and Designed
  Page setup use in-app forms in Electron and never depend on native browser
  prompt dialogs.
- Switch to Read with unsaved edits and confirm it flushes, lays out the current
  chapter through Press, and displays actual lines, captions, images, Designed
  Pages, boxes, labels, and parity. Exercise single/facing display, fit page,
  fit width, zoom, presets, custom page setup, failure diagnostics, retry, and
  switching away during typesetting. Confirm cancellation never leaves a Press
  process or a permanently spinning preview. Confirm every returned page has
  its own non-overlapping canvas at every display mode and zoom level. Include
  curly apostrophes, quotation marks, dashes, and accented Latin text and
  confirm the preview preserves those characters exactly.
- Confirm Read leaves selected side panes mounted and never displays the
  read-only editing surface as a preview.

## Designed Pages and spreads

- Insert a Designed Page between ordinary blocks, click its atom, and confirm the
  center column switches to Pages while assistant/context panes remain mounted.
  Confirm a newly created page can open immediately without reloading the chapter.
  Exercise zero-, one-, and multiple-page selection plus previous/next movement,
  mode/page switches, autosave, and revision conflicts. Rapidly drag and resize,
  then press Save or switch pages while pointer-up autosave is still completing;
  confirm saves serialize, retain the newest edit, and do not conflict with
  their own returned revisions.
- Confirm a new page uses project page setup and an existing page retains its
  authored geometry after project setup changes.
- Confirm the canvas has no left sidebar, all creation/selection/layer/content
  controls live in one scrolling right sidebar, and only page/spread selection
  plus the computed guide toggle appear above the canvas.
- Add an existing image only after choosing `Contain` or `Cover`. Confirm native
  browser dragging is suppressed, frame drag/resize works, Cover crop
  repositioning pans the raster, and pointer-up saves. Exercise replace image,
  text binding, z-order, delete, undo/redo, zoom, and computed overlay toggle.
- Create/edit rectangle, ellipse, and line primitives from Advanced. Confirm no
  custom-guide, SVG/path, or surface-generation navigation control is present.
- Bind semantic text, reorder its logical reading sequence independently from
  layers, and confirm unplaced content and overflow block validation.
- Generate surface/frame art and confirm reserved text and gutter regions reach
  the generation brief, the provider's returned raster is retained, and the
  required fit determines letterboxing or crop-to-fill placement.

## Covers

- Confirm paperback shows back, spine, front, bleed/safe/fold guides, and barcode
  reserve, while Digital PDF/EPUB shows only a front surface.
- Verify canonical title/subtitle/author/spine/back-copy bindings, project font
  selection, image crop-position state, shapes, layers, grouping, styles, and
  logical reading order.
- Change trim or page count and confirm constraint-bound objects reflow, free
  objects are not stretched, and overflow is surfaced.
- Confirm full-wrap generation requires current interior page count while
  front-panel work remains available independently.
- In Publish, choose an edition whose geometry lacks an exact page layout and
  confirm compatibility is reported there. Use **Create layout for this
  edition** and verify it copies the authoring scene into a reviewable edition
  variant without altering the active authoring layout.

## Outputs and accessibility

- Render color and grayscale paperback jobs; inspect separate interior and cover
  downloads, full bleed, captions, spreads, project fonts, and cover geometry.
- Render a Digital PDF and confirm the front cover is page one of one Book PDF,
  bookmarks/internal TOC links work, text is searchable/selectable, and enabled
  independent pages retain their boxes.
- Inspect the Digital PDF structure/reading order, Figure alternatives and
  decorative artifacts; do not label it PDF/UA certified.
- Export EPUB and confirm flowing semantic Figures plus fixed-layout Designed
  Pages/cover preserve real text, source reading order, language, captions, and
  alternatives. Confirm TXT/Markdown contain Designed Page text in reading
  order.
- Review final text contrast over artwork manually; automated composed-background
  contrast sampling is documented as deferred.
