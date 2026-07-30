# ADR 0001: Reject Typst as the sole Phase 1 press renderer

Date: 2026-07-30

Status: Accepted

## Context

Phase 1 requires deterministic prose pagination, a real PDF preview, a
page-count-dependent wrap cover, in-app preflight, KDP output, and an
independently verified Ingram PDF/X artifact. The renderer decision must happen
before Lorekeeper migrates chapter bodies or the single publish profile because
those data changes would otherwise be designed around an unproven projection.

The first candidate was a pinned Rust sidecar using Typst 0.15.1, its krilla
0.8.2 PDF backend, and moxcms 0.9.0.

## Decision

Do not use Typst + krilla + moxcms as the sole Phase 1 press renderer and do not
integrate this prototype into the production application.

The Typst compositor remains a viable reference candidate for deterministic
prose PDF 1.7, but it cannot satisfy the declared Ingram gate because the pinned
public PDF API has no PDF/X mode. moxcms does not add PDF/X structure or an
output intent by itself, and Lorekeeper has not selected a reviewed,
redistributable CMYK press profile.

Feature 2 remains blocked. The next implementation feature is a disposable
WeasyPrint 69 conformance fallback spike. It must use the same versioned
protocol shape and acceptance evidence, and it must pass independent Acrobat
and vendor validation before any persistent publishing-model change.

## Evidence

- The representative Rust fixture emits deterministic, independently parseable
  PDF 1.7 interior and wrap-cover files with embedded fonts and correct page
  geometry.
- The Ingram profile request emits no file and returns structured PDF/X and
  CMYK-profile errors.
- Typst's supported-standard list includes PDF versions, PDF/A, and PDF/UA but
  not PDF/X.
- The Windows x64 optimized binary is 47,534,592 bytes and a representative
  fresh-process whole-job render has a median of 1,022.3 ms with a cached
  filesystem in this environment.
- The exact lock resolves 334 target-inclusive packages; embedded font notices
  and all discovered license-file hashes are inventoried.
- Acrobat Pro and vendor upload checks were not run. Since no PDF/X artifact
  exists, there is no conformance or vendor-compatibility claim to validate.

Full evidence is in
[`press-renderer-conformance-spike.md`](../research/press-renderer-conformance-spike.md).

## Consequences

- Lorekeeper's current HTML/browser print path remains unchanged.
- No database, export schema, service, UI, or assistant contract is migrated.
- The Rust crate is retained only as a disposable, reproducible benchmark and
  fail-closed protocol reference.
- A production renderer cannot depend on caller-supplied arbitrary Typst,
  arbitrary filesystem reads, or undeclared network access.
- WeasyPrint is evaluated next because its current BSD-licensed release exposes
  PDF/X-1a/-3/-4 and CMYK/output-intent controls. Those API claims are not
  accepted as conformance until the full external evidence gate passes.
- Apache FOP is not selected because its own PDF/X documentation describes
  incomplete CMYK behavior. Ghostscript is not selected for bundling because
  its distribution path is AGPL or commercial.
- Extending krilla directly remains a last-resort option, not an assumed future
  implementation.
