# Unified composition manual acceptance checklist

Use this checklist after automated migration, editor, Press, build, and startup
verification. The Core Book Publish-workspace checks below are authorized for
the live test project; terminate the browser host after the run.

## Core Book and publication releases

- Open Publish in a project with zero releases. Confirm Core Book is selected,
  shared details/content/matter/design/cover remain usable, and no release or
  ISBN is created implicitly.
- Supply title, author, and language, choose **Prepare reading PDF**, reconnect
  after navigation, preview the current PDF in-app, and download the immutable
  private reading copy. Confirm the Markdown and plain-text downloads sit beside
  the reading-PDF actions rather than in a separate section.
  Confirm it is never labeled publishable, packaged, vendor-validated, or ISBN
  bearing.
- Turn off **Show act headings** and confirm act divider headings disappear
  without changing chapter inclusion. Confirm the content chooser lists chapters
  only. Enable chapter numbering and confirm the contents page, bookmarks, and
  chapter openings contain exactly one `Chapter N:` prefix.
- Create Paperback (KDP, IngramSpark, and Other printer where applicable), EPUB
  ebook, and PDF ebook releases. Confirm print controls appear only for
  paperback, EPUB controls only for EPUB, and mixed-page/front-cover controls
  only for PDF ebook. Raw profiles, hashes, and page-box terms stay under
  Technical details.
- Change a Core value and confirm an inherited release updates. Customize a
  field to an explicit value and to an explicit empty value, change Core again,
  and confirm both overrides remain stable. Use **Use Core Book value** and
  confirm live inheritance resumes. Repeat for one content row and one matter or
  placement overlay.
- Confirm the Core front cover is inherited by digital releases and projected
  into the paperback front panel. Customize the release front and confirm later
  Core changes do not overwrite it.
- Run **Prepare files** for all three release formats. Confirm preparation is
  persisted/reconnectable, cancellation works, blockers are plain-language and
  prioritized, current artifacts are reused, and successful downloads match the
  selected release.
- Ask the Publish assistant to patch Core, create a release, customize and reset
  an override, inspect readiness, and prepare files. Confirm compact results
  refresh the correct target without overwriting a dirty manual field. Confirm
  it cannot select a raw profile, invent an ISBN, approve proof, or describe the
  Core reading copy as publication files.

## Outline and assistants

- Confirm Outline describes chapters as semantic text, Figures, and Designed
  Pages without offering chapter visual types.
- Check fiction, nonfiction, picture-book, illustrated-book, poetry, and hybrid
  briefs receive concise relevant guidance subordinate to explicit direction.
- Ask Outline, Editor, Images, and Publish to read and mutate a Figure and a
  composition; verify compact IDs/revisions/diagnostics and conflict recovery.
- On each assistant surface, generate or edit an image and confirm the tool
  remains active until it returns a readable terminal result and an unattached
  project-image ID. Confirm no numeric job status or queued-success result is
  returned. Resume one existing job by ID without repeating its prompt.
- Ask Editor to illustrate a chapter in one turn. Confirm it generates the
  unattached image, receives it visually, then separately inserts a Figure or
  creates/opens a Designed Page and places the same image ID before replying.
  Confirm the Pages canvas follows each affected object as the tools progress.
- Ask Publish a project-specific cover question that depends on a synopsis or
  chapter body. Confirm the full outline is already available and that bounded
  source search/read finds the body detail. Generate cover art, inspect it, and
  separately place its image ID without replaying the generation request.
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
  requiring a publication release.

## Edit and Read modes

- Confirm Edit exposes block style, font family, point size, line spacing,
  emphasis, link, alignment, whole-paragraph indent, lists, Figure/Designed Page
  insertion, search, undo, redo, and reusable-style actions without overflow;
  advanced marks, paragraph controls, and Figure settings remain available
  without crowding the primary controls.
- Exercise toolbar and Tab/Shift+Tab indentation, first-line and hanging indent,
  spacing, keep-with-next, and start-on-new-page. Confirm inline emphasis survives
  Book Text Style changes and clearing paragraph formatting removes only direct
  paragraph presentation.
- Create a paragraph Book Text Style that combines zero paragraph spacing,
  first-line indentation, and justification. Confirm Edit, Read, Core reading
  PDF, release PDF, and EPUB apply the same values, and confirm changing the
  style makes existing PDF artifacts stale.
- Place the cursor in a paragraph and use the primary font-family, point-size,
  and line-spacing controls. Confirm the paragraph updates immediately with a
  bundled font, then an imported project font, and that selection changes update
  the displayed toolbar values. Confirm Read mode, EPUB, and PDF retain the same
  family, size, weight, emphasis, and line height.
- Format one paragraph directly, choose **Save as style**, and confirm
  the new style is applied to that paragraph and appears in the saved-style
  picker without remounting the editor. Apply it to another selected paragraph,
  then use **Whole chapter** and confirm headings, block quotes, list items,
  and ordinary paragraphs change while Figures, Designed Pages, and scene
  breaks remain intact. Confirm direct paragraph overrides are cleared but
  inline emphasis remains.
- Ask Editor to extract a style from one stable block and apply it across a
  chapter. Confirm the transcript contains compact create/apply calls rather
  than one `setBlockStyle` operation per paragraph, and that Review edits stages
  the style before the dependent manuscript change.
- Open Advanced at both edges of the editor and confirm its panel remains fully
  inside the editor without horizontal scrolling or position drift. Select a
  Figure by clicking its artwork, scroll the manuscript, and confirm its styled
  layout/accessibility controls remain visible and interactive beneath the
  toolbar. Open Advanced while those controls are visible and confirm the two
  panels do not overlap. Insert a project image through the Figure picker,
  insert a Designed Page and confirm Pages opens immediately, then click List
  twice and confirm the block returns to ordinary body text. Confirm Figure and
  Designed Page setup use in-app forms in Electron and never depend on native
  browser prompt dialogs.
- Switch to Read with unsaved edits and confirm it flushes, lays out the current
  chapter through Press, and displays actual lines, captions, images, Designed
  Pages, boxes, labels, and parity. Exercise single/facing display, fit page,
  fit width, zoom, presets, custom page setup, failure diagnostics, retry, and
  switching away during typesetting. Confirm cancellation never leaves a Press
  process or a permanently spinning preview. Confirm every returned page has
  its own non-overlapping canvas at every display mode and zoom level. Include
  curly apostrophes, quotation marks, dashes, and accented Latin text and
  confirm the preview preserves those characters exactly.
- Preview a Designed Page image before assigning alternative text or marking it
  decorative. Confirm Read renders the page with a visible accessibility note,
  while publication validation still blocks output until the decision is made.
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

- Open Core and release cover editing. Confirm it replaces the normal right
  Publish workspace instead of opening a modal, leaves the Publish assistant
  visible, gives the canvas the main area, and places all cover controls in one
  independently scrolling right column. Confirm assistant cover mutations
  update and select the affected live canvas object.
- Confirm paperback shows back, spine, front, bleed/safe/fold guides, and barcode
  reserve, while Digital PDF/EPUB shows only a front surface.
- Verify canonical title/subtitle/author/spine/back-copy bindings, project font
  selection, image crop-position state, shapes, layers, grouping, styles, and
  logical reading order.
- Change trim or page count and confirm constraint-bound objects reflow, free
  objects are not stretched, and overflow is surfaced.
- Confirm full-wrap generation requires current interior page count while
  front-panel work remains available independently.
- In Publish, choose a release whose geometry lacks an exact page layout and
  confirm compatibility is reported there. Use **Create layout for this
  release** and verify it copies the authoring scene into a reviewable release
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
