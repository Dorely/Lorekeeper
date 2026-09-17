# Lorekeeper Architecture Map

## Status and purpose

Lorekeeper is a desktop-first, local AI-assisted bookmaking workbench. Its
implemented foundation spans narrative planning and retrieval, semantic
manuscript authoring, assistant collaboration, images and page composition, and
publication production through the owned Lorekeeper Press renderer. Normal use
is through an Electron.NET shell backed by a local ASP.NET Core host; an explicit
browser profile exists for development and validation.

This file is the required architecture entry point. It records only the global
contracts needed to route work safely. Detailed current architecture and key
source ownership live in the chapters under [`architecture/`](architecture/).
After inspecting the repository, read every chapter whose route matches the
task before planning or changing that area. If the discovered impact expands,
read the additional chapter before continuing.

Product direction lives in [`../VISION.md`](../VISION.md). User-facing
capabilities and setup live in [`../README.md`](../README.md). Roadmaps describe
intended delivery rather than proof of current implementation. Research and
accepted decisions explain evidence and history; the routed chapters describe
the current runtime. Source inspection remains the proof of what is implemented.

## Global invariants

- Razor components own interaction and presentation state. Domain behavior,
  persistence, provider access, indexing, assistant mutations, media workflows,
  publishing, and background work belong to injected owning services.
- SQLite and the current domain models are authoritative durable state. Runtime
  code uses short operation-owned database contexts and the documented lock
  order; it does not retain a circuit-scoped `AppDbContext`.
- Version history is a deterministic, app-managed Git snapshot of selected
  canonical creative state, not a SQLite mirror. Secrets, chats, jobs,
  projections, render artifacts, and other operational state stay outside the
  snapshot; restore rebuilds derived projections through their owning services.
  Remote attachment is explicit opt-in. After attachment, successful local
  checkpoints create durable automatic push intents for every attached remote;
  fetch, checkout, and manual push remain explicit operations, while checkout
  and every push require a clean workspace and fast-forward-only history.
  Divergence is preserved rather than silently
  merged or overwritten, and local checkpoint success never waits for network.
- Manuscripts persist as semantic manuscript v5 documents. Older v1-v4 forms
  survive only at immutable migration history and versioned import boundaries.
- The graph, FTS5, and sqlite-vec projections are maintained through owning
  services. Changes to retrievable content must trace both lexical and semantic
  indexing effects.
- The six user-facing assistants share the application service boundaries used
  by manual UI actions. Their tool, prompt, persistence, review, and visible
  workspace contracts evolve together.
- Project images, fonts, Designed Pages, covers, and publication artifacts keep
  explicit ownership and revision boundaries. Generated artwork is stored as a
  reusable asset before a separate revision-checked placement mutation.
- Core Book owns shared publication intent. Optional releases inherit it and
  store only explicit product or content differences.
- Lorekeeper Press is the sole PDF production runtime. Publication claims are
  limited to the exact contained renderer, profile, validation, and target
  platform evidence actually exercised.
- Credentials stay within provider and OAuth persistence boundaries. Secrets,
  authorization codes, and tokens are never copied into feature entities, tool
  payloads, or logs.
- Product dialogs are application-owned Razor/HTML/CSS. Browser, Electron, and
  operating-system dialogs are not product UI except for browser-mediated local
  file selection at explicit import/upload boundaries and the explicit final
  printer handoff from the application-owned print preview.
- Superseded runtime paths and current-state documentation are removed in the
  same change. Applied EF migrations and historical decision records remain
  immutable evidence rather than active compatibility paths.

## Routing table

Read all matching chapters, not merely the first match. Paths are navigation
hints; concepts and downstream consumers determine the final impact area.

| Task area or path signal | Required chapter | Common companion chapters |
|---|---|---|
| Host startup, DI, middleware, shared layout, dialogs, theme, Electron/browser behavior, `Program.cs`, `Startup/`, shared `Components/` | [`runtime-host.md`](architecture/runtime-host.md) | Providers for client wiring; persistence for startup database work; validation for packaging |
| Projects, outline, canon, graph, retrieval, indexing, direct references, provenance, ingest corpus, `Graph/`, `Knowledge/`, `Context/`, `Outline/` | [`narrative-context.md`](architecture/narrative-context.md) | Assistants for tool/prompt changes; persistence for stored forms |
| User-facing chat, turn runtime, prompt composition, tools, compaction, review, contests, revision workers, `ChatTurns/`, `EditorChat/`, chat components | [`assistants-chat.md`](architecture/assistants-chat.md) | The owning narrative, manuscript, composition, publishing, or provider chapter |
| LLM/search/image providers, OAuth, wire compatibility, embeddings, guarded web fetching, queues and workers, `Llm/`, provider adapters | [`providers-background.md`](architecture/providers-background.md) | Runtime for registration/configuration; assistants for conversational use; persistence for credentials/jobs |
| Manuscript schema, chapters, Core/release editing targets, ProseMirror, styles, annotations, Review, Undo/Redo, `Manuscripts/`, `Authoring/` | [`manuscript-authoring.md`](architecture/manuscript-authoring.md) | Persistence for migrations/import; assistants for tools; composition for visual blocks |
| Project images, entity visuals, Figures, fonts, page setup, Designed Pages, canvases, visual previews, `Images/`, `Composition/`, `Fonts/` | [`composition-media.md`](architecture/composition-media.md) | Manuscript for semantic ownership; publishing for sections/covers; providers for generation |
| Core Book, releases, inheritance, publication sections, edition content, Publish UI/tools, TXT/Markdown/EPUB projections, `Publish/` | [`publishing-model.md`](architecture/publishing-model.md) | Manuscript and composition for authored content; Press for production artifacts |
| Product registry, physical geometry, covers, renderer protocol, PDF/EPUB validation, artifacts, packages, previews, `Lorekeeper.Press/` | [`press-production.md`](architecture/press-production.md) | Publishing for effective inputs; composition for scenes; validation for release evidence |
| EF model, repositories, write coordination, migrations, recovery, import/export, local data and credential storage, `Persistence/`, `ImportExport/` | [`persistence-migrations-import.md`](architecture/persistence-migrations-import.md) | Every domain whose stored contract changes |
| Deterministic snapshots, local Git history, checkpoints, compare/restore, remote sync, clone import, GitHub version-control connections, `VersionHistory/`, History and Version control surfaces | [`version-history-sync.md`](architecture/version-history-sync.md) | Persistence for SQLite/identity; narrative, manuscript, composition, publishing, assistants, providers, and runtime for captured or excluded state |
| Build, tests, startup smoke checks, native validation, documentation hierarchy, release scripts, research, decisions | [`validation-documentation.md`](architecture/validation-documentation.md) | Every changed implementation chapter |

Representative cross-layer routes are intentional: a manuscript persistence
change requires manuscript plus persistence; an assistant manuscript tool
requires assistants plus manuscript; cover work commonly requires composition,
publishing, and Press; provider registration requires providers plus runtime;
release tooling requires runtime plus validation.

## Chapter and file ownership

Each routed chapter has the same structure: when to read, scope and ownership,
current architecture and invariants, key files and file families, related
chapters, and relevant verification. A key file or cohesive file family has one
primary chapter. Consumer chapters link to that owner instead of restating its
contract. The tables are architectural navigation, not an exhaustive inventory;
use `rg --files`, `rg`, Roslynk, and direct source reads to discover every file,
caller, registration, stored form, and obsolete path affected by a task.

Keep this index compact. Update it only when global invariants, chapter
boundaries, or routing rules change. Update an owning chapter when its contract,
data flow, primary entry points, or key file family materially changes. If a
chapter approaches 4,000 words, split it by responsibility rather than allowing
mandatory task context to become another monolith.

## Validation entry point

Verification is selected from the routed chapters and
[`validation-documentation.md`](architecture/validation-documentation.md).
Normal source changes require a successful solution build followed by the
explicit HTTP-profile startup smoke check and host termination. Specialized
database, import/export, semantic-editor, Press, Electron, packaging, release,
provider, or platform claims require their documented evidence. Do not infer an
integration or target-platform result from compilation alone.
