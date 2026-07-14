# Lorekeeper editorial and composition research

Last reviewed: 2026-07-14  
Research access date: 2026-07-14  
Runtime model scope: OpenAI `gpt-image-2` and the OpenAI Image/Responses APIs documented on the review date.

These briefs are engineering references for Lorekeeper's prompts, automation, contracts, and diagnostics. They are not prompt payloads and must not be injected wholesale into a model request. Runtime instructions should contain only the compact rules needed for the active task; the application should calculate geometry, assemble context, validate contracts, and measure diagnostics itself.

## Briefs

- [Image generation prompting](image-generation-prompting.md) — production prompt structure, references, edits, masks, story-page targeting, and direct mappings to Lorekeeper's structured image contracts.
- [Story writing and editorial practice](story-writing-and-editorial-practice.md) — professional editorial stages, narrative craft, picture-book practice, and the requirements for Lorekeeper's code-owned system role.
- [Page composition and typesetting](page-composition-and-typesetting.md) — page/spread design, typography, accessibility, diagnostic thresholds, and shared page geometry.

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
