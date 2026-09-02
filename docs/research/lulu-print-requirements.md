# Lulu.com print requirements and evidence

Last reviewed: 2026-09-02
Research access date: 2026-09-02

## Executive conclusion
Lulu.com is viable as a Lorekeeper Specific print preset for paperback perfect-bound and hardcover casewrap products. The cover topology (one-piece integrated back+spine+front spread, back cover first) matches Lorekeeper's existing FullWrapMeasured construction, spine math is fully published (formula for paperback, banded table for hardcover), and PDF requirements are satisfiable by the existing PDF 1.7 KDP-family rendering path. Lulu always prints covers in color. Global Distribution has stricter, partially divergent requirements (separate distribution cover with different spine width, no Premium B&W, color minimum 72 pages for Amazon, barcode must be author-supplied on custom covers) that are documented below but only partially modeled in this first pass.

## Evidence: product matrix
- Bindings: Softcover Perfect Bound (32–800 pages), Hardcover Casewrap (24–800 pages). Also offered: Coil, Saddle Stitch, Hardcover Linen with Dust Jacket, Calendar Wire-O (out of scope for the first preset).
- Interior ink: Standard Black & White, Premium Black & White, Standard Color, Premium Color. Covers always print in premium color.
- Interior paper: 60# Cream (uncoated), 60# White (uncoated), 80# White (coated), 100# White (coated, calendar only).
- Cover finish: glossy or matte laminate (paperback and casewrap).
- Trim sizes (16): Pocket Book 4.25×6.875 in (108×175 mm); Novella 5×8 (127×203); Digest 5.5×8.5 (140×216); A5 5.83×8.27 (148×210); Royal 6.14×9.21 (156×234); US Trade 6×9 (152×229); Comic Book 6.63×10.25 (168×260); Executive 7×10 (178×254); Crown Quarto 7.44×9.68 (189×246); Small Square 7.5×7.5 (191×191); A4 8.27×11.69 (210×297); Square 8.5×8.5 (216×216); US Letter 8.5×11 (216×279); Small Landscape 9×7 (229×178); US Letter Landscape 11×8.5 (279×216); A4 Landscape 11.69×8.27 (297×210). Calendar 11×8.5.
- For the Lulu print network (bookstore, Lulu Direct, personal printing) all product options are selectable; availability restrictions apply only to Global Distribution.

## Evidence: spine calculation
- Softcover Perfect Bound: spine = (page count / 444) + 0.06 inches (uniform across their interior papers; a 460 PPI variant exists for magazines/comics only). Millimeter form: (pages / 17.48) + 1.524 mm.
- Hardcover (Casewrap and Linen share one table), page count → spine inches (mm): 24–84 → 0.25 (6); 84–140 → 0.5 (13); 140–168 → 0.625 (16); 169–194 → 0.6875 (17); 195–222 → 0.75 (19); 223–250 → 0.8125 (21); 251–278 → 0.875 (22); 279–306 → 0.9375 (24); 307–334 → 1.0 (25); 335–360 → 1.0625 (27); 361–388 → 1.125 (29); 389–416 → 1.1875 (30); 417–444 → 1.25 (32); 445–472 → 1.3125 (33); 473–500 → 1.375 (35); 501–528 → 1.4375 (37); 529–556 → 1.5 (38); 557–582 → 1.5625 (40); 583–610 → 1.625 (41); 611–638 → 1.6875 (43); 639–666 → 1.75 (44); 667–694 → 1.8125 (46); 695–722 → 1.875 (48); 723–750 → 1.9375 (49); 751–778 → 2.0 (51); 779–800 → 2.0625 (52); at exactly 800 → 2.125 (54); 0–23: no hardcover spine.
- Coil and saddle stitch have no spine.

## Evidence: interior PDF requirements
Single PDF including blanks and all matter; fonts embedded or outlined; images 300 PPI minimum, 600 PPI maximum; transparency flattened; single-page layout; bleed 0.125 in (a 6×9 book needs 6.25×9.25 pages); 0.5 in safety margin; minimum 0.2 in gutter margin with a gutter-addition table for >60-page books (61–150: +0.125 in; 151–400: +0.5 in; 400–600: +0.625 in; over 600: +0.75 in added to the inside margin); no trim/bleed marks; no security/encryption; odd pages print on the right.

## Evidence: cover PDF requirements
One-piece integrated spread PDF containing back cover, spine, front cover, back cover first. Template is generated from the uploaded interior (paper, binding, size, page count) and reserves a barcode area. Images 300–600 PPI; fonts embedded; transparency flattened; vector images rasterized; bleed 0.125 in; safety margin 0.5 in (0.75 in for hardcover casewrap per the PDF settings article); no trim/bleed marks; no security. Spine text guidance: leave at least 0.125 in between spine text and the spine edges; keep the spine the same color/graphic as the rest of the cover; do not include spine text at 80 pages or fewer (creation guide). The Global Distribution requirements article is stricter: no spine text below 100 pages and spine text at least 0.0625 in from all spine edges. Trimming tolerance is 0.125 in.

## Evidence: identifiers and barcodes
- Print-only / personal books require no ISBN and no barcode. Lulu Bookstore and Lulu Direct sales do not require an ISBN.
- Global Distribution requires an ISBN-13. For custom-uploaded covers the author adds the barcode themselves — Lulu provides barcode SVG/PNG/PDF downloads after ISBN assignment; the Lulu cover tool adds it automatically.
- Distribution barcode spec: correct 13-digit ISBN/EAN barcode, black on white, 2 in wide × 1.2 in tall, positioned at least 0.25 in from the cover edge. (The ISBN basics article describes a 2.5×1.7 in box including 0.25 in white padding on all sides; the distribution requirements article states 2×1.2 in — resolved as the 2×1.2 in printed barcode inside a padded quiet zone.)
- Global Distribution requires a separate Distribution Cover in addition to the Lulu Bookstore Cover because the spine width differs for distribution partners; Lulu supplies a template for each.

## Evidence: color
Lulu printers accept sRGB (IEC 61966-2.1 preferred) or CMYK (Coated GRACoL 2006); TAC (total area coverage) must not exceed 270%; avoid tints below 20%; grayscale images in color books must be grayscale with gamma 2.2–2.4. Lulu provides Adobe job options for interior and cover exports.

## Lorekeeper interpretation
- Ink mapping: Lulu Standard and Premium Black & White both map to Lorekeeper's BlackAndWhite interior process (the premium/standard distinction changes ink density, not Lorekeeper's generated bytes or geometry). Standard Color and Premium Color map to StandardColor and PremiumColor.
- gsm values are not published by Lulu; basis weight conversions used in the registry (60# ≈ 90 gsm, 80# coated ≈ 118 gsm) are standard conversions, labeled as Lorekeeper interpretation.
- Spine-text eligibility: Lorekeeper enforces the stricter distribution rule (no spine text below 100 pages) so one rule covers both the print network (80) and Global Distribution (100).
- The 60# cream paper is paired with black & white ink only in Lulu's guidance; 80# coated is paired with premium color (and premium black & white); 60# white supports black & white or color. Standard Color on 80# coated is not confirmed by Lulu's guidance and is excluded from the first preset.
- Hardcover casewrap paper/ink availability beyond 80# white (e.g., 60# interiors) is not confirmed by published guidance and is excluded until verified against Lulu's pricing calculator.

## Implementation decisions
- First preset scope: paperback perfect-bound profiles (60# cream B&W, 60# white B&W, 60# white Standard Color, 80# white B&W [premium B&W], 80# white Premium Color) and hardcover casewrap profiles (80# white B&W, Standard Color, Premium Color).
- Paperback spine uses kind Caliper with inchesPerPage 1/444 ≈ 0.0022523 and baseInches 0.06. Hardcover casewrap spine uses the banded table expanded to FrozenLookup anchors for every even page 24–800.
- pdfProfile `lulu-print-v1` maps to the existing PDF 1.7, fonts-embedded, transparency-flattened rendering family; sRGB output; TAC ceiling already enforced.
- Cover submission is always FullWrapMeasured (single integrated spread); no separate-panel mode. Barcode: user-supplied ISBN mode generates the EAN-13 barcode in the reserved area; personal-use mode omits ISBN and barcode. No print-setup.json (that is Barnes & Noble-specific).
- Trim lists: paperback includes all 15 book trims except Calendar; hardcover casewrap includes the same 15 book trims pending per-size verification during maintainer registry review.

## Platform limitations
- Lorekeeper does not upload to Lulu or exercise their ingestion; no vendor-acceptance claim is made for any artifact.
- The separate Distribution Cover required for Global Distribution (different spine width) is not produced in this first pass; only the Lulu print-network cover is generated. Distribution submissions need that second artifact outside Lorekeeper for now.
- Per-binding trim and paper availability was gathered from Lulu's published guides, not from driving their pricing calculator; the maintainer registry review step must verify per-size hardcover availability and the paper×ink matrix before expanding the profile set.

## Sources
- Lulu, "Cover and Interior Paper Stocks" — https://help.lulu.com/en/support/solutions/articles/64000255473-cover-and-interior-paper-stocks
- Lulu, "Printing: The Basics" — https://help.lulu.com/en/support/solutions/articles/64000255474-printing-the-basics
- Lulu, "Book Creation Guide" (PDF) — https://assets.lulu.com/media/guides/en/lulu-book-creation-guide.pdf
- Lulu Developer Portal, "How is spine width calculated?" — https://help.api.lulu.com/en/support/solutions/articles/64000254616-how-is-spine-width-calculated-
- Lulu Developer Portal, "What is the difference between binding types?" — https://help.api.lulu.com/en/support/solutions/articles/64000254625-what-is-the-difference-between-binding-types-
- Lulu Developer Portal, "What is the difference between print color options?" — https://help.api.lulu.com/en/support/solutions/articles/64000254624-what-is-the-difference-between-print-color-options-
- Lulu, "PDF Creation Settings" — https://help.lulu.com/en/support/solutions/articles/64000255519-pdf-creation-settings
- Lulu, "Upload a Custom Book Cover File" — https://help.lulu.com/en/support/solutions/articles/64000282777-upload-your-cover-file
- Lulu, "Mandatory Print Book Distribution Requirements" — https://help.lulu.com/en/support/solutions/articles/64000255462-mandatory-print-book-distribution-requirements
- Lulu, "ISBN: The Basics" — https://help.lulu.com/en/support/solutions/articles/64000255457-isbn-the-basics
- Lulu, "Retail Distribution Eligible Products" (PDF) — https://assets.lulu.com/media/guides/en/lulu-retail-distribution-eligible-products.pdf
- Lulu, "Custom Book Sizes & Book Binding Options" (Products) — https://www.lulu.com/print-api/products
- Lulu Blog, "Lulu's Book Product Type Options" — https://blog.lulu.com/lulus-book-product-type-options/
- Amazon KDP, "Create a Paperback Cover" (used only for Lorekeeper's Generic caliper declarations, not Lulu specs) — https://kdp.amazon.com/en_US/help/topic/G201953020
