# ADR 0002: Accept WeasyPrint for the Phase 1 preview press runtime

Date: 2026-07-30

Status: Accepted

## Context

The first press candidate proved deterministic PDF 1.7 pagination but could not
emit PDF/X. Phase 1 needs a contained renderer capable of prose pagination,
full-wrap cover geometry, CMYK output intent, structural preflight, and eventual
independent PDF/X-1a:2001 validation before Lorekeeper can replace its
manuscript and publishing models safely.

WeasyPrint 69 exposes the needed paged-CSS and color machinery. Its stock
PDF/X-1a variant declares the 2003 revision, while Ingram's current file guide
requires PDF/X-1a:2001 or PDF/X-3:2002.

## Decision

Use exact-pinned WeasyPrint 69 as the Phase 1 production-renderer foundation,
subject to the following boundaries:

- a contained sidecar receives versioned semantic data, never project HTML or
  arbitrary CSS;
- a pinned adapter registers PDF/X-1a:2001 with PDF 1.3 and a fingerprinted
  CMYK output intent;
- all generated artifacts pass Lorekeeper's independent structural parser;
- every artifact remains `Preview` and carries no PDF/X/vendor claim until
  independently validated;
- production packaging must use controlled native packages with a complete
  license/notice inventory rather than copying the exploratory portable bundle;
- the application owns immutable jobs, manifests, assistant-safe diagnostics,
  cancellation, and crash containment; and
- renderer/profile upgrades are version changes that invalidate proofs and
  rerun the full fixture and external acceptance matrix.

This is an explicit reduced-scope acceptance that unblocks Phase 1 Feature 2.
It does not mark PDF/X or either vendor profile as verified.

## Evidence

- The frozen Windows x64 spike emits parseable PDF 1.3 interior and wrap-cover
  artifacts declaring PDF/X-1a:2001.
- Both artifacts contain one four-component output intent, embedded fonts,
  stable page boxes, and no detected DeviceRGB operators, transparency,
  encryption, annotations, or forbidden actions.
- Generated HTML is application-owned and escaped. The URL fetcher admits only
  the exact fingerprinted profile URI.
- Output is staged and atomically published into an immutable validated child
  directory.
- Exact Python dependency resolution is committed.
- The redistributable basICColor profile and its zlib notice are fingerprinted.
- WeasyPrint itself warns that standards output is not guaranteed valid;
  Acrobat/vendor/physical-proof checks were not available.

Full evidence is in
[`weasyprint-pdfx-fallback-spike.md`](../research/weasyprint-pdfx-fallback-spike.md).

## Consequences

- Structured manuscript implementation may proceed without binding user data
  to the rejected Typst runtime.
- The Rust spike remains a disposable comparison fixture and is not integrated.
- Feature 5 owns production sidecar packaging, job orchestration, long-book
  pagination fixtures, page maps, and preview integration.
- Feature 7 owns independent validation, vendor uploads, EPUBCheck, proof state,
  and the transition from `Preview` to any narrower `Verified` claim.
- If the pinned internal adapter breaks or external validation rejects its
  output, Lorekeeper must replace the adapter/renderer and invalidate affected
  artifacts; it must not post-process around failed conformance silently.
