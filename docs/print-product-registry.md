# Physical print-product registry

Last reviewed: 2026-08-12

Lorekeeper and Lorekeeper Press consume the same checked-in registry at
`Lorekeeper.Press/assets/print-products-v1.json`. The registry is offline at
runtime. A Specific product is identified by the registry version and stable
product key; the packaged runtime records and verifies the registry SHA-256.

## Supported products

- Amazon KDP paperback: black ink on white, cream, or groundwood; standard
  color on white; and premium color on white.
- Amazon KDP hardcover: case laminate with black ink on white or cream, or
  premium color on white, across KDP's five hardcover trims.
- IngramSpark paperback: supported black-and-white, standard-color, and
  premium-color stocks with simplex or supported duplex covers.
- IngramSpark hardcover: case laminate, Digital Cloth in the supported cloth
  colors with or without a jacket, and jacketed case laminate.
- Generic paperback and hardcover: a complete printer-supplied template
  manifest. Generic output is labelled **Template validated** and never claims
  KDP, Ingram, or other vendor conformance.

Each product record owns its binding construction, interior process, exact
paper stock and basis weight/GSM, cover material, finishes, cover modes, trims,
submitted and manufacturing page ranges, PDF rules, required artifact roles,
and spine model. Paper weight is not display-only metadata: it selects the
geometry data used to calculate the cover.

KDP paperback uses the published stock-specific formulas. KDP hardcover and
Ingram products use frozen measurements for every supported normalized even
page count. Lorekeeper never interpolates or substitutes a universal caliper
for a Specific product. Submitted, normalized cover-calculation, and reported
production page counts remain distinct.

## Output surfaces

| Construction | Output |
|---|---|
| Perfect bound, simplex | Interior PDF and one-page outside cover PDF |
| Perfect bound, duplex | Interior PDF and a two-page cover PDF: outside first, inside second |
| Case laminate | Interior PDF and case-wrap PDF |
| Digital Cloth, no jacket | Interior PDF and cloth setup manifest |
| Digital Cloth with jacket | Interior PDF, dust-jacket PDF, and cloth setup manifest |
| Jacketed case laminate | Interior PDF, case-wrap PDF, and dust-jacket PDF |

Cover scenes are stored independently by exact geometry fingerprint and surface
role. The final interior layout determines the normalized manufacturing page
count and therefore the spine and full cover geometry. A geometry change creates
or selects the matching variant; it does not stretch an approved scene silently.

## Maintainer refresh workflow

1. Review the current official KDP print options, paperback cover requirements,
   hardcover requirements, and cover calculator.
2. Review the current IngramSpark File Creation Guide, Trim Size Matrix, and
   spine/template calculator for every supported stock and construction.
3. Capture calculator evidence for every changed product and boundary. Keep the
   source URL, review date, inputs, returned measurements, and calculator output
   with the proposed registry update.
4. Put the proposed snapshot in a separate file and run:

   ```powershell
   ./eng/ReviewPrintProductRegistry.ps1 -CandidatePath <candidate.json>
   ```

5. Inspect the generated product and measurement diff. Do not approve a
   Specific product with a missing even-page measurement, undocumented
   combination, approximate neighboring stock, or generic spine fallback.
6. Install the reviewed candidate with the script's explicit install option,
   update the registry version/review date, then add or update independent Press
   conformance fixtures before changing production behavior.
7. Run the full Press conformance suite, runtime/license manifest build, .NET
   migration/import suite, application build, and startup smoke check.

Current authoritative sources:

- [KDP Print Options](https://kdp.amazon.com/en_US/help/topic/G201834180)
- [KDP paperback submission guidelines](https://kdp.amazon.com/en_US/help/topic/G201857950)
- [KDP hardcover cover requirements](https://kdp.amazon.com/en_US/help/topic/GDTKFJPNQCBTMRV6)
- [KDP cover calculator](https://kdp.amazon.com/en_US/cover-templates/)
- [IngramSpark File Creation Guide](https://www.ingramspark.com/hubfs/downloads/file-creation-guide.pdf)
- [IngramSpark Trim Size Matrix](https://www.ingramspark.com/hubfs/downloads/trim-sizes.pdf)

Vendor documentation and calculator results can change. A successful Lorekeeper
preparation means **Lorekeeper validated for the named registry/profile
version**. Only the vendor can accept an uploaded file.

## Manual application acceptance checklist

Browser/Electron validation is intentionally separate from automated Press
conformance. For a release candidate, exercise these flows in the supported app
host and terminate it afterward:

- Create Paperback and Hardcover releases for Generic, KDP, and Ingram; verify
  each step filters construction, trim, process, exact stock/weight, finish, and
  cover mode without exposing unsupported combinations.
- Change paper stock and page count; verify the displayed submitted/normalized
  counts and spine width change, prior cover/output becomes stale, and an exact
  geometry variant is selected or created without stretching free objects.
- Edit and visually inspect a KDP case laminate, Ingram case laminate, duplex
  paperback outside/inside, Digital Cloth setup, jacket, and jacketed-case
  surface. Verify the appropriate wrap, hinge, board, gutter, flap, bleed,
  safety, barcode, and no-ink overlays.
- Prepare each construction. Verify the directly downloadable files, package
  filenames, manifest, validation report, and `upload-map.json` match the
  artifact table above and that duplex cover page order is outside then inside.
- Create a Generic product with a complete manifest and optional template
  underlay; verify incomplete geometry fails closed and successful output says
  Template validated rather than naming a vendor.
- Confirm blocking diagnostics use plain-language links to the relevant release
  setting, interior page, cover surface, or object, while raw codes/IDs remain
  under Technical details.
- Ask the Publish assistant to compare compatible stocks, select a product,
  inspect the calculated geometry, edit every required cover surface, prepare
  files, and report exact downloads without claiming vendor acceptance.
