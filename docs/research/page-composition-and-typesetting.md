# Page composition, typesetting, and accessibility

Last reviewed: 2026-07-14  
Time-sensitive: W3C fixed-layout guidance is a Group Note/work in progress; recheck its latest published version. Typography heuristics are stable but should be revisited with user research.

## Executive finding

Lorekeeper should treat a page or spread as a reading system: geometry, hierarchy, visual path, text roles, images, negative space, trim/gutter safety, and semantic reading order work together. Real failures should be measured and block final output when necessary. Typography rules such as preferred line length, number of font families, or child-reader size are useful advisories, not universal hard failures.

## Page, spread, and visual hierarchy

### Evidence

W3C's fixed-layout guidance notes that visual readers infer order from position, type size/style, imagery, and colored backgrounds, while assistive technology follows source order. A mismatch can confuse screen-reader, magnifier, or keyboard users. It also cautions against reading paths that jump back and forth across separate pages in a synthetic spread ([W3C EPUB Fixed Layout Accessibility](https://www.w3.org/TR/2024/NOTE-epub-fxl-a11y-20240530/)).

BookTrust's picture-book guidance recommends pacing with a dummy book and preserving white space for the reader's interpretation rather than crowding each spread ([BookTrust guide](https://www.booktrust.org.uk/resources/find-resources/joyce-dunbars-guide-to-writing-picture-books/)).

### Lorekeeper interpretation

- A **page** is one physical leaf surface.
- A **spread** is a coordinated two-page composition and may use a shared focal field, but it still has trim edges, a center gutter, and an accessible sequence.
- Visual hierarchy should make the intended entry point and progression apparent without requiring the reader to infer arbitrary jumps.
- Negative space is active compositional capacity: it can protect readability, direct attention, carry atmosphere, or invite a page-turn pause.

### Implementation decisions

- Use project publish-profile width/height and chapter orientation/spread mode for one shared geometry calculation.
- Renderers, paged viewer, image target compiler, safe guides, diagnostics, and publishers consume that calculation.
- Full-spread art protects focal faces, actions, and text from the center gutter and trim-risk bands.
- When page art carries story copy, Lorekeeper plans text-box geometry before generation and sends corresponding buffered rectangles as hard reserved regions. The illustration should turn those regions into natural, low-detail, tonally stable negative space rather than visible placeholder panels.
- Text objects carry an explicit reading order. Duplicate order is an error; disagreement with the usual language-direction spatial path is a warning that can be accepted when deliberate visual cues support it.
- Overlapping text boxes warn; an image layer above and intersecting text is an error because it can obscure copy.

## Trim, margins, gutter, and image safety

### Evidence

The reviewed sources establish the need for intentional placement and reading paths but do not prescribe one universal trade trim inset. Physical tolerances depend on printer, binding, trim size, bleed specification, and production vendor.

### Lorekeeper interpretation

Trim safety and gutter clearance are application defaults, not claims of universal publishing law. Users need measured warnings and the ability to make intentional exceptions, while final vendor templates remain authoritative.

### Implementation decisions

- Lorekeeper's minimum diagnostic inset is 0.375 inch and preferred buffer is 0.5 inch.
- Crossing the minimum trim/gutter inset is an error; entering only the preferred buffer is a warning.
- Image prompts ask full-bleed art to reach the raster edges while keeping focal detail outside trim/gutter risk.
- `PublishProfile` exposes width, height, margin, body size, and body line height. Vendor-specific bleed remains future publication metadata rather than being silently invented.

### Platform limitation

Lorekeeper does not currently model paper creep, binding-specific gutter compensation, printer ICC profiles, or vendor trim templates. Its geometry is an editorial/preflight system, not a replacement for the printer's production specification.

## Text roles and typographic hierarchy

### Evidence

Butterick identifies point size, line spacing, line length, and font choice as the four central body-text variables, suggesting 10–12 point print body text, 120–145% line spacing, and 45–90 characters per line as general guidance ([Practical Typography](https://practicaltypography.com/summary-of-key-rules.html)). These recommendations target ordinary documents, not every display or children's format.

Ilene Strizver's children’s typography guidance favors simple, generous letterforms, open counters, non-extreme widths/weights, large x-height, and child-familiar forms for early readers. It recommends larger type (roughly 14–24 points depending on age/typeface), generous leading, short lines, limited text per page, and strong contrast; it allows more expressive typography for short titles than for sustained reading ([Typography for Children](https://www.myfonts.com/pages/fontscom-learning-fyti-situational-typography-typography-for-children/)).

W3C states there is no single font or universal WCAG font-size requirement. Fixed-layout text is difficult to resize or reflow, so creators should select a comfortable base size across devices, maintain consistent size patterns, and consider character differentiation and weight ([W3C EPUB Fixed Layout Accessibility](https://www.w3.org/TR/2024/NOTE-epub-fxl-a11y-20240530/)).

### Lorekeeper interpretation

A title should not be diagnosed like a paragraph. Text role is therefore necessary before typography can be evaluated. Body measures can use general-prose heuristics; display, title, caption, and credit text need different judgment.

### Implementation decisions

`PicturePageTextRole` values are `Body`, `Title`, `Heading`, `Caption`, `Display`, and `Credit`; legacy elements deserialize as `Body`.

- Under 10pt body copy warns as an unusually small general-print default.
- For a picture book whose Book Brief maximum reader age is eight or younger, body copy below 14pt produces an advisory warning, not a hard failure.
- General body lines estimated outside 45–90 characters warn.
- More than two font families on a page/spread warns; inconsistent body styles warn.
- Widows/orphans and conspicuously short final lines are heuristic warnings with suggested corrections.
- Overflow is an error based on actual rendered measurement.
- Missing-glyph fallback is silent. It remains a renderer behavior and does not create diagnostics, tool fields, prompt instructions, UI warnings, or manifest noise.

## Contrast and editable text

### Evidence

WCAG 2.2's minimum contrast criterion uses 4.5:1 for normal text and 3:1 for large text. Its guidance defines the common large-text cases as at least 18pt regular or 14pt bold, and says threshold values are not rounded upward ([W3C Understanding SC 1.4.3](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html)).

W3C fixed-layout guidance treats contrast, meaningful sequence, alternatives for non-text content, and avoiding unnecessary images of text as central accessibility concerns. Fixed layout often cannot satisfy reflow, resize-text, or text-spacing behavior available to reflowable publications, so the source text and semantic structure remain especially important ([W3C EPUB Fixed Layout Accessibility](https://www.w3.org/TR/2024/NOTE-epub-fxl-a11y-20240530/)).

### Lorekeeper interpretation

A color pair checked in isolation can pass while text over a photograph fails locally. Contrast diagnostics should evaluate the composed background under a text box, including its backing panel, not just compare two configured hex strings.

### Implementation decisions

- Rendered PicturePage snapshots sample the composed background within each text box and evaluate text color after blending any configured backing.
- A sampled minimum below 4.5:1 for normal text or 3:1 for large text is an error with measured ratio, threshold, element ID, and correction.
- Story copy stays in editable, ordered text elements. Generated raster text defaults off.
- PicturePage text backgrounds default to fully transparent. On a contrast failure, the preferred correction order is to use the planned quiet region, choose a suitable text color, or correct the illustration; a backing panel is an explicit request or last-resort accessibility treatment.
- Images carry alt text; complex images may need longer description in publication output.
- Prose and image-free IllustratedProse also receive render snapshots, but their page breaks are labeled advisory because reflowable EPUB pagination changes with device and reader settings.

### Platform limitation

Sampling a finite grid is a useful deterministic preflight, not a mathematical proof for every anti-aliased glyph pixel or every reading system. Human review should inspect text placed over highly variable imagery.

## Fixed-layout versus reflowable EPUB

### Evidence

The W3C Group Note recognizes that fixed-layout books preserve artistically or semantically important layout but can remain partially inaccessible. It says reflowable publication may be the only option for some conformance requirements, and notes common fixed-layout limitations around text resizing, spacing adjustment, orientation, and reflow ([W3C EPUB Fixed Layout Accessibility](https://www.w3.org/TR/2024/NOTE-epub-fxl-a11y-20240530/)).

### Lorekeeper interpretation and implementation

- `PicturePage` is a fixed-layout authorial choice, suitable when page composition is integral.
- `Prose` and `IllustratedProse` keep editable flowing text and should export reflowably whenever the format supports it.
- A preview page count for flowing prose is not a promise about EPUB page count.
- Fixed-layout output still needs source-order text, language metadata, navigation, text alternatives, and accessible story copy; a single flattened page image is not sufficient.
- The user chooses visual mode. The agent must not convert modes merely because one tool path is easier.

## Guidance/default/diagnostic matrix

| Topic | Externally sourced guidance | Lorekeeper default | Diagnostic threshold | Classification |
|---|---|---|---|---|
| Normal text contrast | WCAG 2.2 1.4.3 | Measure composed raster | `< 4.5:1` | Hard accessibility error |
| Large text contrast | WCAG 2.2 1.4.3 | Large = 18pt regular or 14pt bold | `< 3:1` | Hard accessibility error |
| General print body size | Practical Typography suggests 10–12pt | Profile default 12pt | Body `< 10pt` | Advisory warning |
| Young-reader body size | MyFonts article suggests 14–24pt depending on reader/typeface | No forced resize | Picture book, max age `<=8`, Body `<14pt` | Advisory warning |
| General prose line length | Practical Typography suggests 45–90 characters | Estimate from box/font | Outside `45–90` | Advisory warning |
| Leading | Practical Typography suggests 120–145%; child guidance favors generous leading | Profile default 1.55; PicturePage per element | Inconsistent body styles; overflow measured separately | Advisory except overflow |
| Font count | No universal rule in sources | At most two families per page/spread | `>2` | Lorekeeper heuristic warning |
| Body consistency | W3C recommends consistent size patterns | One body style per page/spread | `>1` style signature | Advisory warning |
| Trim/gutter | Vendor dependent | 0.375in minimum, 0.5in preferred | Inside minimum = error; preferred band = warning | Lorekeeper production default |
| Reading order | W3C meaningful/source order guidance | Explicit per text element | Duplicate = error; spatial conflict = warning | Structural accessibility check |
| Text/image overlap | General legibility/accessibility principle | Text remains unobscured | Higher image intersects text | Hard composition error |
| Widows/orphans | Established typesetting concern; no single cited threshold here | Short-paragraph heuristic | 3 words or fewer in isolated paragraph | Advisory heuristic |
| Short final line | Typesetting judgment | Compare explicit lines | Markedly short final line | Advisory heuristic |
| Missing glyphs | No useful warning requirement | Silent fallback font | None | Renderer behavior only |

## Direct implementation mapping

| Concern | Geometry/rendering change | Diagnostic/UI change |
|---|---|---|
| One source of page truth | `IPageGeometryService` calculates page/surface/margin/type/raster values from `PublishProfile` | Publish UI exposes width, height, margin, body size, line height |
| Page/spread image targeting | `IImagePromptComposer` uses shared geometry and buffered text regions | Reject aspect/size conflict before provider call |
| Semantic typography | `PicturePageTextRole` persisted in layout JSON and manifest | Role selector and role-aware body diagnostics |
| Actual fit | Skia render measures wrapped/drawn lines and required height | Overflow error includes measured/available height |
| Contrast | Sample composed raster before text draw, including backing blend | Ratio, threshold, element, correction returned |
| Reading path | Store independent `ReadingOrder` | Duplicate/conflicting-order diagnostics |
| Reflow distinction | Render prose snapshots with profile metrics | Label pagination advisory in model visual context |
| Font fallback | Keep fallback selection inside renderer | Remove missing-glyph fields and warnings everywhere else |

## Sources

- Matthew Butterick, “Summary of key rules,” *Butterick's Practical Typography*, update/publication date not displayed, https://practicaltypography.com/summary-of-key-rules.html (accessed 2026-07-14).
- Ilene Strizver, “Typography for Children,” commissioned by Monotype Imaging and preserved by MyFonts/Fonts.com, publication/update date not displayed, https://www.myfonts.com/pages/fontscom-learning-fyti-situational-typography-typography-for-children/ (accessed 2026-07-14).
- W3C Publishing Maintenance Working Group, Wendy Reid (editor), “EPUB Fixed Layout Accessibility,” Group Note, 30 May 2024, https://www.w3.org/TR/2024/NOTE-epub-fxl-a11y-20240530/ (accessed 2026-07-14).
- W3C Web Accessibility Initiative, “Understanding Success Criterion 1.4.3: Contrast (Minimum),” WCAG 2.2 supporting document, update date not displayed, https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html (accessed 2026-07-14).
- Joyce Dunbar, “Joyce Dunbar's guide to writing picture books,” BookTrust, publication/update date not displayed, https://www.booktrust.org.uk/resources/find-resources/joyce-dunbars-guide-to-writing-picture-books/ (accessed 2026-07-14).
