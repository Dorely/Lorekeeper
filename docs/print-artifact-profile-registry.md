# Print-artifact profile registry

Last reviewed: 2026-08-30

Lorekeeper and Lorekeeper Press consume the same checked-in registry at
`Lorekeeper.Press/assets/print-artifact-profiles-v1.json`. It is an offline
artifact contract, not a catalog of products a user buys. Stable internal
profile keys bind a release to reviewed PDF, page-count, paper-thickness,
spine, trim, construction, and cover-surface rules; the packaged runtime also
records and verifies the registry version and SHA-256.

The release UI never exposes a Print Product selector. After an edition exists,
it exposes only choices that can change prepared artifacts:

- interior color process;
- paper basis weight and measured thickness/spine table;
- trim size;
- cover construction and outside/inside topology;
- vendor-specific full-wrap or separate-panel submission.

Paper color, matte/gloss/textured finish, price, listing, account, tax, and
fulfillment settings are not stored in publishing state because they do not
change the files Lorekeeper creates. When two same-weight vendor stocks require
different spine geometry, Lorekeeper distinguishes them by measured thickness,
not by paper color.

## Supported artifact profiles

- Amazon KDP paperback and hardcover: black-and-white, standard-color, and
  premium-color processes where supported, with exact published calipers or
  frozen spine measurements.
- IngramSpark paperback and hardcover: supported color processes, printed
  cover/case, cloth, jacket, and duplex artifact surfaces with frozen spine
  measurements.
- Barnes & Noble Press paperback, printed-case hardcover, and dust-jacket
  hardcover at 6 × 9. The 50 lb paperback and hardcover profiles use calibrated
  rounded page-count formulas; the 45 lb groundwood paperback uses B&N's
  published 408 PPI value. Full-wrap and panel dimensions are owned by the
  profile and were checked against the official B&N cover generator.
- Generic (other-printer) paperback and hardcover: Lorekeeper-owned profiles for
  the four paper × ink families (50 lb white and 60 lb cream black-and-white,
  60 lb standard color, 80 lb premium color) with `Caliper` spine models built
  from Lorekeeper-declared calipers (0.002252 in/page white and standard color,
  0.0025 in/page cream, 0.002347 in/page premium color; corroborated against
  KDP's published per-paper spine formulas and Lulu's independent 444 PPI
  figure). These calipers are estimates to verify with the final printer before
  production; generic profiles never claim named-vendor conformance.

Submitted, normalized cover-calculation, and reported production page counts
remain distinct. The final interior layout determines the spine and full-cover
geometry. A geometry change creates or selects the matching cover variant; it
does not stretch an approved scene.

## Output surfaces

| Construction | Output |
| --- | --- |
| Perfect bound, simplex | Interior PDF and one-page outside cover PDF |
| Perfect bound, duplex | Interior PDF and a two-page cover PDF: outside first, inside second |
| Case laminate | Interior PDF and case-wrap PDF |
| Digital Cloth, no jacket | Interior PDF and cloth setup manifest |
| Digital Cloth with jacket | Interior PDF, dust-jacket PDF, and cloth setup manifest |
| Jacketed case laminate | Interior PDF, case-wrap PDF, and dust-jacket PDF |
| B&N separate panels | Interior PDF plus front and back PDFs derived from the connected wrap |

## Maintainer refresh workflow

1. Review current official vendor print and cover requirements and calculators.
2. Capture evidence for every changed boundary: source URL, review date,
   inputs, returned measurements, and calculator output.
3. Put the proposed snapshot in a separate file and run:

   ```powershell
   ./eng/ReviewPrintArtifactProfileRegistry.ps1 -CandidatePath <candidate.json>
   ```

4. Inspect the generated artifact-profile and measurement diff. Do not approve
   missing page measurements, undocumented combinations, neighboring-stock
   approximations, or generic spine fallbacks for named vendors.
5. Install the reviewed candidate explicitly, update its version/review date,
   and update Press conformance fixtures before production behavior.
6. Run the Press conformance suite, runtime manifest build, .NET
   migration/import suite, application build, and startup smoke check.

Vendor rules can change. A successful Lorekeeper preparation means
**Lorekeeper validated for the named registry/profile version**; only the
vendor can accept an uploaded file.
