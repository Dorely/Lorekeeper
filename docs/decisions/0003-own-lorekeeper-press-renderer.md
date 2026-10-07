# ADR 0003: Own the Lorekeeper Press renderer

Date: 2026-07-31

Status: Accepted

## Context

The Typst candidate could not produce the required PDF/X-1a:2001 scope. The
subsequent WeasyPrint path introduced a Python/native distribution whose source,
notice, and platform state could not meet Lorekeeper's requirement that PDF
generation be wholly usable, distributable, app-owned, and independent of
machine state. Treating that path as a reduced Preview runtime was therefore an
incorrect production decision.

## Decision

`Lorekeeper.Press` is the sole paperback renderer and a Lorekeeper-owned Rust
subproject. It owns semantic pagination, typography policy, font embedding and
subsetting, images, color conversion, covers, EAN-13, PDF serialization, and
standards restrictions. Protocol v3 accepts only a bounded staged job with
declared, hashed PNG assets. It atomically promotes output only after a separate
post-write parser validates the finished bytes.

KDP and generic profiles emit PDF 1.7. The Ingram profile emits PDF 1.3 with
PDF/X-1a:2001 identification, embedded CGATS21 CRPC1 output intent, CMYK/gray
content, flattened alpha, embedded fonts, ToUnicode maps, and a 240% total-ink
limit. This scope is labeled “Lorekeeper validated.” Vendor acceptance and human
proofs remain separately recorded facts.

Rust 1.97.1 and every direct dependency are exact-pinned. The build derives an
SBOM and notices from `Cargo.lock`, rejects unknown license expressions, and
packages the same native runtime in Debug and Release. The application verifies
the complete runtime inventory and launches with no inherited `PATH`.

## Consequences

- WeasyPrint, Typst, Python, uv, Chromium, and machine PDF tools are absent from
  the runtime.
- Layout and PDF correctness are Lorekeeper responsibilities and must be locked
  by independent black-box conformance fixtures before implementation changes.
- Old renderer artifacts remain downloadable as `Legacy`, but cannot satisfy a
  current render, preflight, proof, or package.
- Historical ADRs and spike evidence remain for decision provenance; they do not
  describe the current architecture.
- Later extraction into a standalone repository must preserve protocol,
  determinism, test independence, licensing, and packaging guarantees.
