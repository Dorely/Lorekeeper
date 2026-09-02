# Lorekeeper editorial and composition research

Last reviewed: 2026-08-30

Research access date: recorded by brief.

Runtime model scope for image research: OpenAI `gpt-image-2` and the OpenAI
Image/Responses APIs reviewed on 2026-08-30.

These briefs are engineering references for Lorekeeper's prompts, automation, contracts, and diagnostics. They are not prompt payloads and must not be injected wholesale into a model request. Runtime instructions should contain only the compact rules needed for the active task; the application should calculate geometry, assemble context, validate contracts, and measure diagnostics itself.

## Briefs

- [Image generation prompting](image-generation-prompting.md) — production prompt structure, references, edits, soft regional guidance, story-page targeting, and direct mappings to Lorekeeper's structured image contracts.
- [Visual development and concept-art practice](visual-development-and-concept-art.md) — style vocabulary, exploratory art direction, canonical character/location design, and the Images assistant's library/entity boundary.
- [Story writing and editorial practice](story-writing-and-editorial-practice.md) — professional editorial stages, narrative craft, picture-book practice, and the requirements for Lorekeeper's code-owned system role.
- [Page composition and typesetting](page-composition-and-typesetting.md) — page/spread design, typography, accessibility, diagnostic thresholds, and shared page geometry.
- [Genre-aware book-format guidance](book-format-guidance.md) — current Book Brief-derived Outline guidance, format-neutral structure, and compact assistant contract.
- [Publishing industry workflow and file standards](publishing-industry-and-file-standards.md)
  — print/ebook production stages, common service inputs, vendor constraints,
  metadata, preflight, and Lorekeeper's edition/package decisions.
- [Lulu.com print requirements and evidence](lulu-print-requirements.md)
  — evidence for adding Lulu.com as a Specific print preset: product matrix,
  spine-width math, interior and cover PDF requirements, identifiers and
  barcodes, color management, and the first-pass paperback and hardcover
  casewrap profile scope.
- [Book authoring and visual-design software](book-authoring-and-design-software.md)
  — capability comparison, missing editing/design tools, phased scope, and
  integration with Lorekeeper's existing workspaces and assistants.
- [Open-source publishing stack preliminary screen](open-source-publishing-stack.md)
  — permissive renderer/editor candidates, rejected licensing models, the
  proposed press sidecar, release-level audit requirements, and the PDF/X
  conformance spike.
- [Structured manuscripts and safe migration](structured-manuscripts-and-safe-migration.md)
  — semantic document ownership, WAL-safe backup, expand/migrate/validate/
  contract conversion, legacy imports, recovery, and assistant parity.
- [Lorekeeper Press requirements and evidence](lorekeeper-press-requirements.md)
  — current protocol-v3, renderer, PDF/PDF-X, security, packaging, application,
  assistant, limitation, and future-phase requirements for the owned runtime.
- [Press renderer and conformance spike](press-renderer-conformance-spike.md)
  — historical Typst/krilla/moxcms evidence, fail-closed PDF/X result,
  dependency/license inventory, and the reason that candidate was rejected.
- [WeasyPrint PDF/X fallback spike](weasyprint-pdfx-fallback-spike.md)
  — historical PDF/X declaration/inspection evidence, containment, ICC rights,
  and the packaging/distribution gaps that retired the reduced `Preview`
  decision in favor of the owned renderer.
- [WeasyPrint fallback license inventory](weasyprint-spike-license-inventory.json)
  — generated locked-package source/checksum/metadata and installed
  license-file hashes for the Windows fallback fixture, with target-native
  release obligations left explicit.
- [WeasyPrint controlled build evidence](weasyprint-spike-build-evidence.json)
  — exact interpreter, native archive, fonts, source files, executable, and
  binary-inventory fingerprints for the accepted Windows fixture.
- [WeasyPrint upstream native source](weasyprint-spike-native-source.json)
  — per-payload hashes extracted directly from the verified official portable
  executable before the controlled build selects its native dependencies.
- [WeasyPrint frozen binary inventory](weasyprint-spike-binary-inventory.json)
  — source-classified final PyInstaller binary inputs with per-file hashes and
  an explicitly closed release-license gate.
- [WeasyPrint verification evidence](weasyprint-spike-verification-evidence.json)
  — repeatability, isolation, immutable-output, independent parse, geometry,
  text-extraction, and artifact-hash results for the controlled fixture.

## Scope and evidence policy

The research distinguishes four kinds of statements:

- **Evidence** — a paraphrase traceable to a cited source.
- **Lorekeeper interpretation** — how the evidence applies to this product and its mixed prose, illustrated-prose, and fixed-layout workflows.
- **Implementation decision** — a product default or contract selected for predictable behavior. It is not represented as a universal publishing rule.
- **Platform limitation** — a model, API, renderer, EPUB, or current-implementation boundary that may change.

Rules are assigned to the narrowest effective layer:

| Layer | Appropriate content |
|---|---|
| Code-owned system instructions | Professional role, editorial judgment, preservation rules, discipline selection, and tool-use policy. |
| Application automation | Context retrieval, prompt assembly, geometry derivation, reference ordering, reserved regions, persistence, and preflight checks. |
| Diagnostics | Measurable failures or clearly labeled heuristics with values, thresholds, and corrections. |
| Project Guidance / Book Brief | User-owned authorial direction, audience, purpose, voice, constraints, and project-specific exceptions. |

## Review cadence

OpenAI model aliases, snapshots, size constraints, API behavior, pricing-related quality choices, and model limitations are time-sensitive. Review the image brief whenever Lorekeeper changes image model or API surface, and at least quarterly while using a moving alias. Editorial and typography sources are more stable; review them when standards publish a new edition or when export formats materially change.

The fixed-layout EPUB source is a W3C Group Note described by its publisher as work in progress, not a W3C Recommendation. Treat it as informed accessibility guidance and check its latest published version during future reviews.

Publishing vendors, standards, dependency features, and licenses are
time-sensitive. Recheck the relevant primary sources at the beginning of every
publishing implementation feature. A research recommendation is not a dependency
approval, conformance claim, or statement that the capability is implemented.
