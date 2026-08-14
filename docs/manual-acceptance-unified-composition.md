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
- Under **PDF presentation**, enable **Preserve Designed Page sizes** and prepare
  the Core reading PDF. Confirm a full-art facing spread is one wide page while
  ordinary pages retain Project Page Setup geometry. Disable it and confirm the
  same spread becomes two regular facing leaves. Create a PDF ebook release and
  confirm it inherits the Core choice, then customize and reset that release.
- Create Paperback (KDP, IngramSpark, and Other printer where applicable), EPUB
  ebook, and PDF ebook releases. Confirm print controls appear only for
  paperback, EPUB controls only for EPUB, and inherited PDF-presentation/front-cover controls
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
  it cannot select a raw profile, invent an ISBN, mutate edition manuscript/page
  layouts, or describe the
  Core reading copy as publication files.

## Edition-specific content

- Enable edition-specific content for a draft release in Publish. Follow **Edit
  edition content** and confirm Editor opens the selected release target; reload
  and navigate chapters and confirm that target is remembered.
- Edit text in one release chapter and confirm only that chapter diverges while
  untouched chapters continue reflecting later Core edits. Confirm the frozen
  chapter reports when Core changes after its snapshot.
- Make a Figure and Designed Page change as the first release mutation. Confirm
  the chapter and referenced composition fork atomically, Pages uses release
  geometry, and Core remains unchanged.
- Create a Book Text Style in release mode, then apply it from Core and another
  release. Edit an existing shared style manually and confirm the usage-count
  warning names its global effect. Reset the release chapter and confirm the
  saved style remains available.
- Exercise Review Edits, a contest/revision job, and one Editor-assistant change
  in release mode. Attempt to switch targets mid-operation and confirm it is
  blocked; confirm accepted changes cannot land in another target.
- In Publish, verify the difference banner counts text, movement, Figures,
  formatting, styles, and Designed Pages without treating cloned IDs alone as a
  change. Follow chapter/block/composition/object warning links back to the exact
  release target. Disable edition content with confirmation and verify all
  snapshots disappear without deleting styles or project images.
- Prepare the release and confirm its artifact contains effective edition
  content. Reset one chapter and confirm only the affected release becomes stale
  and its next artifact returns to Core for that chapter.

## Outline and assistants

- Confirm Outline describes chapters as semantic text, Figures, and Designed
  Pages without offering chapter visual types.
- Check fiction, nonfiction, picture-book, illustrated-book, poetry, and hybrid
  briefs receive concise relevant guidance subordinate to explicit direction.
- Ask Editor to read and mutate a Figure/composition and ask Publish to read
  edition differences and layout diagnostics. Verify compact
  IDs/revisions/diagnostics and conflict recovery. Ask Images for the same
  Figure, page, page-setup, cover, or chapter-context mutation and confirm it
  explains the Editor/Publish handoff without exposing or invoking such a tool.
- In Images, ask for two or three materially different art directions for the
  current book. Confirm the comparison uses observable medium, mark, shape,
  proportion, palette/value, light, texture, composition, influence, genre, and
  audience traits. Approve one, confirm the exact current Visual Direction is
  read before it is saved, then simulate a stale value and confirm the newer
  direction is not overwritten.
- Generate and inspect canonical studies for one character and one location.
  Confirm exploratory art remains unattached, the approved character uses an
  isolated study or tight crop, the location may use an intentional broad
  environment study, and both references receive role-specific labels.
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
- Ask Publish to audit a book with incomplete front/back matter for both print
  and reflowable EPUB. Confirm it explains each recommended section's purpose,
  treats KDP order and recto/verso practice as guidance rather than a universal
  blocker, preserves existing authored choices, and does not add irrelevant
  ceremonial matter. Confirm it distinguishes Lorekeeper validation from
  actual KDP/Ingram acceptance.
- Ask Publish to complete the destination metadata. Confirm it works only with
  supported Core/release fields and identifies BISAC/Thema, keywords, expanded
  contributors, price, territories, and publication date as external portal
  follow-up instead of claiming to store or submit them.
- Stage a large scene once, apply by stage ID, and verify the payload is neither
  repeated nor replayable.
- Confirm Images and Outline expose only free-standing generation. Confirm
  Editor and Publish default reusable art and ordinary flowing Figures to
  free-standing generation, and use a geometry-bound target only when artwork
  must honor a concrete page, frame, Figure placement, or cover region. Verify
  bound guidance does not crop or resize the stored raster, and confirm no
  assistant claims vendor acceptance or that a URL was downloaded.

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

- Confirm Edit exposes a compact Lorekeeper toolbar with bordered controls,
  clearly separated command groups, understandable icons, native hover text,
  and a joined point-size stepper. Confirm it exposes block style, font family, point size, line spacing,
  emphasis, link, alignment, whole-paragraph indent, lists, Figure/Designed Page
  insertion, search, undo, redo, and reusable-style actions without overflow;
  character marks, paragraph controls, and Figure settings remain directly
  available without a secondary menu.
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
- Format one paragraph directly, choose **+ Style**, and confirm
  the new style is applied to that paragraph and appears in the saved-style
  picker without remounting the editor. Apply it to another selected paragraph,
  then use **All** and confirm headings, block quotes, list items,
  and ordinary paragraphs change while Figures, Designed Pages, and scene
  breaks remain intact. Confirm direct paragraph overrides are cleared but
  inline emphasis remains.
- Ask Editor to extract a style from one stable block and apply it across a
  chapter. Confirm the transcript contains compact create/apply calls rather
  than one `setBlockStyle` operation per paragraph, and that Review edits stages
  the style before the dependent manuscript change.
- Confirm every toolbar button and select has understandable hover text and no
  Advanced control or floating formatting menu exists. Select a Figure by
  clicking its artwork, scroll the manuscript, and confirm its styled
  layout/accessibility controls remain visible and interactive beneath the
  toolbar. Open and close Find while scrolled deep into a chapter and confirm
  the visible manuscript position is preserved instead of navigating to the end.
  Open **Images**, choose artwork from the shared searchable project-image
  picker, and confirm the Figure setup form receives that image. Toggle List on
  and off for a styled paragraph and confirm its font, spacing, indentation,
  alignment, and inline emphasis remain intact. Open **Manage** inside the
  bordered Book Text Style group and confirm the full style manager appears in
  a modal rather than occupying manuscript space. Insert a Designed Page and
  confirm Pages opens immediately. Confirm Figure and
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
  then switch pages while pointer-up autosave is still completing;
  confirm saves serialize, retain the newest edit, and do not conflict with
  their own returned revisions.
- Confirm new and existing authoring pages use project page setup, active layouts
  reflow after setup changes without stretched objects, and Pages offers no
  independent width or height controls. Confirm single-page and facing-spread
  mode remain available as layout choices.
- Confirm the canvas has no left sidebar, construction and selection controls
  remain in the fixed bottom strip without horizontal scrolling, and only
  page/spread selection plus the computed guide toggle appear above the canvas.
- Add an existing image with aspect-ratio retention enabled. Confirm native
  browser dragging is suppressed, proportional frame drag/resize works, and
  `Fill canvas` creates the largest centered proportional frame. Disable aspect
  retention and confirm free resizing deliberately stretches the image and
  `Fill canvas` occupies the entire surface. Existing Cover crop repositioning
  continues to pan its raster, and pointer-up saves. Exercise replace image,
  direct text editing, z-order, visibility, locking, delete, undo/redo, zoom,
  and computed overlay toggle.
- Add multiple images and confirm each new image appears above older images but
  below text and shapes. Add a text frame and confirm it starts in front. Verify
  Send back and Bring front move the selection to the actual stack edge, and
  that neither Pages nor Cover exposes manual Save or Back buttons.
- Create/edit rectangle, ellipse, and line primitives from Advanced. Confirm no
  custom-guide, SVG/path, or surface-generation navigation control is present.
- Add a text frame, click the selected frame to edit it in place, select text,
  and apply/remove bold, italic, underline, and strike formatting from the
  bottom toolbar. Confirm Page details exposes no content-binding, block-ID,
  range-offset, or technical inline-mark editor. Reorder logical reading order
  and confirm preserved unplaced content and overflow diagnostics remain clear.
- Generate surface/frame art and confirm reserved text and gutter regions reach
  the generation brief, the provider's returned raster is retained, and the
  selected aspect-ratio behavior determines proportional or stretched placement.
- Move a selected image partly outside the surface and confirm its handles remain
  reachable outside the canvas. Confirm Read mode and generated PDFs clip the
  artwork at the page edge instead of rejecting the render.

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
- Add several cover images and text objects. Confirm artwork remains behind copy,
  newly added artwork is above older artwork, front/back actions reorder within
  the appropriate artwork or content band, and every mutation autosaves.
- Change trim or page count and confirm constraint-bound objects reflow, free
  objects are not stretched, and overflow is surfaced.
- Confirm full-wrap generation requires current interior page count while
  front-panel work remains available independently.
- In Publish, choose a release whose geometry lacks an exact page layout and
  confirm compatibility is reported there. Follow **Fix in Pages** and verify
  Editor creates/reuses the release-owned layout without altering Core.

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
