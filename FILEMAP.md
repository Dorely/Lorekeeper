# Lorekeeper — File Map

> **Auto-maintained reference.** Agents and contributors should update this file whenever files are added, removed, or significantly refactored.  
> Read this file at the start of every session to understand the codebase layout.

---

## Root

| File | Description |
|------|-------------|
| `VISION.md` | High-level product vision, narrative-coherence goals, and durable scope. |
| `README.md` | Project purpose, current capabilities, development, packaging, release/update guidance, and local-data behavior. |
| `AGENTS.md` | Authoritative operating rules for agents and contributors. |
| `CLAUDE.md` | Claude compatibility entry point that delegates all project guidance to `AGENTS.md`. |
| `docs/architecture.md` | Current technical architecture, ownership boundaries, persistence/security constraints, platform scope, and validation commands. |
| `docs/publishing-roadmap.md` | Artifact-focused route to complete paperback, hardcover, EPUB, and PDF ebook production, with Generic/Specific products, Editor-owned release variations, and eventual DOCX interchange. |
| `docs/plans/deferred-docx-interchange.md` | Deferred decision-complete plan for rich Word paste, single-empty-chapter DOCX import, broad Word-derived manuscript semantics, and Core/release DOCX export. |
| `docs/print-product-registry.md` | Current physical-product scope, exact stock/geometry rules, artifact sets, truthful claims, official sources, and maintainer refresh workflow. |
| `docs/manual-acceptance-unified-composition.md` | Manual UI/output checklist for genre guidance, Figures, Designed Pages, covers, persistent Undo/Redo, geometry-bound generation, accessibility, and assistants. |
| `FILEMAP.md` | This file — concise map of every source file. |
| `Lorekeeper.sln` | Solution file containing the application and its migration-safety fixture project. |
| `global.json` | Pins the .NET SDK version (`rollForward: latestFeature`). |
| `.editorconfig` | C#/Razor formatting and naming rules. |
| `.gitignore` | Standard .NET ignore patterns plus Lorekeeper local SQLite/temp data; publish output is scoped to the repo-root `/publish/` folder so source folders named `Publish` remain trackable. |
| `.vscode/launch.json` | VS Code debug configurations; default F5 entry launches the Electron desktop shell, with a secondary web-hosted profile. |
| `.vscode/tasks.json` | VS Code build task used by debug launch configurations. |
| `.github/workflows/build-macos-release.yml` | Dispatch-only Apple Silicon macOS release builder and verifier; uploads one correlated arm64 DMG for the Windows release orchestrator. |
| `scripts/build-windows-release.ps1` | Clean Windows x64 release builder: validates tooling/version, audits NuGet plus shipped npm/Electron dependencies, rebuilds isolated staging/output, verifies installer/updater artifacts, and writes GitHub Release checksums. |
| `scripts/build-macos-release.ps1` | Native Apple Silicon release builder: audits dependencies, packages an ad-hoc-signed arm64 DMG, verifies signatures/architectures, mounts and smoke-tests the app, and writes a checksum. |
| `scripts/publish-release.ps1` | Windows release orchestrator with a local Windows-only mode or correlated native macOS builds; verifies one staged artifact set, coordinates matching source/public-repository releases with compensating cleanup, and removes temporary Actions artifacts after success. |
| `eng/BuildPressRuntime.ps1` | Locked Rust build, license-expression audit, and exact native Press runtime packager used by Debug, Release, and platform packaging. |
| `eng/ReleaseDependencyAudit.ps1` | Shared fail-closed Electron production-audit policy, including the exact dormant-splash exception for Electron.NET's currently unpatched `image-size` dependency. |
| `eng/ReviewPrintProductRegistry.ps1` | Maintainer-only candidate validator, exact frozen-measurement coverage check, hash/evidence report, and explicit canonical-registry installer. |
| `scripts/generate-brand-assets.py` | Deterministically exports browser PNG sizes and the multi-resolution Windows ICO from the 1024px Lorekeeper icon master. |
| `docs/research/README.md` | Index and maintenance policy for Lorekeeper's sourced editorial, image-prompting, and composition research briefs. |
| `docs/research/image-generation-prompting.md` | Sourced `gpt-image-2` prompting/API brief with structured reference/edit/page-target guidance and runtime contract mappings. |
| `docs/research/visual-development-and-concept-art.md` | Sourced concept-art role, style vocabulary, canonical character/location design, and Images assistant boundary. |
| `docs/research/story-writing-and-editorial-practice.md` | Sourced professional editing, narrative craft, picture-book practice, and system-prompt requirement brief. |
| `docs/research/page-composition-and-typesetting.md` | Sourced page/spread, typography, accessibility, diagnostic threshold, and shared-geometry brief. |
| `docs/research/book-format-guidance.md` | Current genre-aware Outline guidance contract, assistant/token boundaries, research basis, and limitations. |
| `docs/research/publishing-industry-and-file-standards.md` | Sourced print/ebook workflow, service-input, metadata, preflight, edition, and artifact requirements. |
| `docs/research/book-authoring-and-design-software.md` | Competitor capability matrix, missing editing/design tools, delivery phases, and Lorekeeper integration proposals. |
| `docs/research/open-source-publishing-stack.md` | Historical editor/renderer/color/validation license screen and candidate gates that preceded the owned Press renderer. |
| `docs/research/structured-manuscripts-and-safe-migration.md` | Semantic manuscript design plus WAL-safe backup, data conversion, validation, recovery, legacy import, and assistant-parity plan. |
| `docs/research/press-renderer-conformance-spike.md` | Historical Typst/krilla/moxcms spike evidence and the fail-closed PDF/X result that rejected the candidate. |
| `docs/research/weasyprint-pdfx-fallback-spike.md` | Historical WeasyPrint fallback evidence, including the distribution gaps that retired the reduced-scope runtime. |
| `docs/research/lorekeeper-press-requirements.md` | Current owned-renderer requirements/evidence matrix, dependencies, distribution/security boundaries, limitations, and future phases. |
| `docs/research/weasyprint-spike-license-inventory.json` | Generated locked Python source/checksum/license-file inventory for the Windows fallback fixture plus explicit target-native release gaps. |
| `docs/research/weasyprint-spike-build-evidence.json` | Controlled Windows fallback interpreter, archive, font, source, executable, and binary-inventory fingerprints. |
| `docs/research/weasyprint-spike-native-source.json` | Archive-derived per-payload hash manifest tying frozen native inputs to the verified official portable executable. |
| `docs/research/weasyprint-spike-binary-inventory.json` | Source-classified hashes for every binary collected into the frozen fallback, with its release-license gate explicitly closed. |
| `docs/research/weasyprint-spike-verification-evidence.json` | Recorded deterministic-output, containment, parse, text, geometry, and artifact-hash checks for the frozen fallback. |
| `docs/research/press-spike-license-inventory.json` | Generated exact-version/checksum/VCS/license-file inventory for every target-inclusive Rust spike dependency and bundled asset notice. |
| `docs/decisions/0001-reject-typst-as-sole-press-renderer.md` | Accepted no-go decision for the first press renderer candidate and the WeasyPrint 69 fallback gate. |
| `docs/decisions/0002-accept-weasyprint-for-preview-press-runtime.md` | Superseded historical decision for the retired reduced-scope WeasyPrint runtime. |
| `docs/decisions/0003-own-lorekeeper-press-renderer.md` | Accepted decision to own the Rust Press renderer, protocol, validation, and packaged runtime boundary. |
| `docs/schemas/manuscript-v1.schema.json` | Published JSON Schema for canonical structured-manuscript v1 documents, blocks, inline nodes, marks, and semantic style roles. |
| `docs/schemas/manuscript-v2.schema.json` | Historical semantic-manuscript v2 schema used only by migration/import boundaries. |
| `docs/schemas/manuscript-v1.schema.json` / `manuscript-v2.schema.json` / `manuscript-v3.schema.json` | Historical semantic manuscript interchange schemas retained for versioned import documentation. |
| `docs/schemas/manuscript-v4.schema.json` | Current semantic manuscript schema with sparse direct paragraph typography/presentation, geometry-neutral Figures, crop positioning, and Designed Page references. |

## Lorekeeper.Tests/

| File | Description |
|------|-------------|
| `Lorekeeper.Tests.csproj` / `Usings.cs` | xUnit project restricted to startup database and versioned project import/export migration-safety fixtures. |
| `ManuscriptMigrationIntegrationTests.cs` | Actual legacy-schema WAL migration with plain-text audit compatibility, backup/journal/hash validation, confirmation, and restore drills. |
| `ProjectExportCompatibilityTests.cs` | Current v23 manuscript/source/page-setup/composition/Core Book/publication-section/order/PDF-presentation/edition-content/print-product/cover/font fixtures plus isolated older fail-closed import-boundary checks. |
| `SourceEvidenceMigrationTests.cs` | Populated pre-v23 database fixture proving source-evidence property renaming, chapter-link migration, canon selection persistence, and source-delete cascade behavior. |
| `ProjectImportJobIntegrationTests.cs` | Real SQLite import-job round trips for manuscript/image remapping, current and legacy cover-image conversion, and whole-import rollback on late publication conflicts. |
| `PublishConversationMigrationTests.cs` | Populated pre-v13 upgrade fixture proving manuscript, edition, render, artifact hash/bytes, and new Publish transcript persistence survive unchanged. |
| `LorekeeperPressMigrationTests.cs` | Fully populated installed-schema fixture run through the real startup migrator, including no-release Picture Page scene/asset/binding preservation, Press/Core projection equality, recovery cases, and whole-database byte/hash checks. |
| `DatabaseMigrationRecoveryTests.cs` | Shared migration-recovery fixtures proving recovery markers stop startup before opening the shell and protected failed/running backup references survive automatic retention. |

## tools/semantic-editor/

| File | Description |
|------|-------------|
| `package.json` / `package-lock.json` | Exact-pinned ProseMirror and esbuild dependency graph plus the deterministic editor build command. |
| `src/semantic-editor.js` | Owned ProseMirror schema/adapter, disconnect-safe host attachment, focus/persistent-caret and non-text-block gap selection, direct Figure selection, compact grouped formatting controls, authoritative-current stale-save reconciliation, paste diagnostics, outline, counts, and find/replace behavior. |
| `THIRD_PARTY_NOTICES.md` | Runtime/build dependency inventory and MIT notice for the semantic-editor bundle. |

## Lorekeeper.Press/ — Owned native publication renderer

| File | Description |
|------|-------------|
| `Cargo.toml` / `Cargo.lock` / `rust-toolchain.toml` | Rust 1.97.1 crate with exact permissive serialization, shaping, subsetting, line-breaking, hyphenation, image/color, PDF-writing, hashing, and inspection dependencies. |
| `src/main.rs` / `src/lib.rs` | Native `describe`, full conformance and compact browser chapter-layout traces, and bounded protocol-v7 render CLI plus the independently testable library surface. |
| `src/model.rs` | Protocol-v7 image/font/publication-section requests, physical-product/cover-surface descriptors, publication-versus-reading-copy purpose, diagnostics, artifacts, validation evidence, structured page-paint layout, accessibility, and page-map contracts. |
| `src/renderer.rs` | Contained staging, validation, deterministic pagination, flowing Figures, structured page/cover composition with leaf-correct facing-spread splitting, warning-and-clipping authoring traces, warning-tolerant private reading copies, strict publication PDF rendering, barcodes, atomic promotion, and evidence. |
| `src/font.rs` | Bundled/project TTF and TrueType/CFF OTF validation, shaping, subsetting, widths, embedding, multi-codepoint ToUnicode mapping, and glyph outlines for deterministic decorative-effect precomposition. |
| `src/image.rs` | Single-pass bounded PNG/JPEG decoding with reusable validated pixels, alpha flattening, grayscale/registered-profile CMYK conversion, crop-position handling, and total-ink enforcement. |
| `src/pdf.rs` | Owned deterministic PDF 1.7/PDF 1.3 writer for mixed page boxes, collision-safe page-scoped tagged structure, bookmarks/links, ordered vector scenes, KDP/PDF-X compositing, lossless publication-raster compression, trim-to-bleed edge extension, fonts, images, output intent, and barcodes. |
| `src/inspect.rs` | Separate `lopdf` post-write inspection for geometry, fonts, XObject colors, output intent, transparency, encryption, annotations/actions, and tagged-PDF parent-tree/MCID integrity. |
| `assets/` | Approved OFL font notices, registered CGATS21 CRPC1 CMYK profile, and the canonical versioned physical-product registry shared with the app and packaged runtime. |
| `fixtures/negative-cases-v4.json` | Frozen adversarial protocol mutations and expected fail-closed diagnostic codes. |
| `fixtures/invalid-pdf-structures-v3.json` | Frozen malformed raw-PDF cases proving the black-box harness fails closed independently of production preflight. |
| `tests/conformance_v7.rs` / `fixtures/full-model-v7.json` / `tests/fixtures/` | Test-owned CLI harness/assets and raw-PDF/layout assertions for protocol, containment, atomicity, determinism, configurable front-matter placement, spread parity, publication sections, physical-product registry/geometry/artifacts, duplex/case/jacket/cloth covers, paragraph presentation, Figures, compositions, custom fonts, color/bleed, KDP/PDF-X, tagged Digital PDF, and negatives. |

## Lorekeeper/ — Blazor Web App (Interactive Server)

| File | Description |
|------|-------------|
| `Lorekeeper.csproj` | Project file: `net10.0`, nullable + implicit usings, warnings-as-errors, versioned Electron/Electron Builder pins, and app dependencies including EF Core SQLite, Microsoft.Extensions.AI(.OpenAI), OpenAI, sqlite-vec, tokenizers, SkiaSharp, and ingest packages. |
| `Program.cs` | Host setup, hardened Electron binding, installed-Windows auto-updates, conditional macOS/portable release discovery and browser handoff, deterministic data placement, DI, gated-startup registration, and HTTP endpoints. |
| `appsettings.json` / `appsettings.Development.json` | Configuration including `Desktop:*` data placement, startup-splash duration, update interval, and constrained public release API plus provider, persistence, Blazor, ingest, research, embedding, and agent settings. |
| `Properties/launchSettings.json` | Local launch profiles for Electron, HTTP, and HTTPS; HTTP remains pinned to `localhost:1455` for Codex OAuth redirect. |
| `Properties/electron-builder.json` | Electron.NET packaging targets, metadata, updater provider, Windows installer/portable configuration, and ad-hoc-signed DMG-only macOS configuration. |
| `Properties/PublishProfiles/*.pubxml` | Runtime-specific self-contained profiles (`win-x64`, `linux-x64`, `osx-arm64`), with Windows/macOS staging isolated from final artifacts. |

### Components/

| File | Description |
|------|-------------|
| `App.razor` | Root component: `<html>` shell, responsive browser/app icon metadata, startup route gate, head outlet, and scripts. |
| `Routes.razor` | `<Router>` wiring `MainLayout` and `NotFound`. |
| `_Imports.razor` | Shared `@using` directives for all components. |
| `StartupScreen.razor` (+ `.razor.css`) | Branded application-owned startup and failure surface with live database-stage progress, automatic workspace transition, recovery redirect, and safe close action. |
| `ConfirmationDialog.razor` (+ `.razor.css`) | Shared application-owned confirmation dialog for destructive and consequential choices; replaces browser, Electron, and operating-system product prompts. |
| `EntityKnowledgeView.razor` (+ `.razor.css`) | Shared read-only entity knowledge renderer for structured wiki data and source-backed canon markdown used by graph, outline, and context entity detail surfaces. |
| `EntityVisualExamples.razor` (+ `.razor.css`) | Reusable ordered entity visual gallery/editor with library attach, upload, non-destructive cropping, labels, ordering, shared full-size viewing, and detach. |
| `ImageCropModal.razor` (+ `.razor.css`, `.razor.js`) | Shared freeform rectangular crop modal with zoom/pan, canvas selection and preview, crop-specific metadata, and optional entity targets. |
| `ImageViewerModal.razor` (+ `.razor.css`) | App-wide full-size image viewer for project assets, chat visuals, source candidates, and publish images, with shared metadata, dismissal, and optional actions. |
| `ImageEntityAssociations.razor` (+ `.razor.css`) | Image-side attached-entity chips and association editor used by the Images workspace. |
| `EntityVisualTargetPicker.razor` (+ `.razor.css`) | Reusable multi-entity target picker for image generation and editing. |
| `ProjectImageLibraryPickerModal.razor` (+ `.razor.css`) | Project-wide searchable single-image library picker with fixed fully-visible image cards, a bounded vertical result scroller, caller-defined actions, optional media-type filtering, and current-image preselection; used by chats, entity visuals, manuscript Figures, Designed Pages, and covers. |

### Components/Chat/

| File | Description |
|------|-------------|
| `ChatModels.cs` | Shared chat UI view models for persisted/live messages, text/image parts, image-bearing composer submissions, duration-aware tool-call chips, active-context token projection, and transcript helpers. |
| `ChatTranscriptTokenCounter.cs` | Shared model-input token adapter for every `ChatSurface`: applies text-only replay, includes pending/live turns, tracks settled counts, and formats advisory maximum/remaining labels. |
| `ChatSurface.razor` (+ `.razor.css`, `.razor.js`) | Reusable chat shell with transcript/live rendering, image viewing, paste/library image attachments, project/surface-keyed browser-local text drafts, scrolling, and textarea autosizing. |
| `ChatToolChipView.razor` (+ `.razor.css`) | Reusable expandable tool-call card that shows streamed arguments/results/errors, generic progress rows/previews, Editor Revision worker transcript links, and the styled `Chat Compacted` audit chip. |

### ChatTurns/

| File | Description |
|------|-------------|
| `ChatTurnRuntime.cs` | App-wide active-turn coordinator keyed by project and chat surface, with buffered subscriber replay, explicit cancellation, and idle-surface maintenance leases for reset safety across windows. |
| `ChatTurnEngine.cs` | Shared user-facing chat protocol engine for streaming text/tool parsing, default tool invocation, assistant tool envelopes, chat compaction coordination, and common message persistence operations. |
| `ChatContextCompactionService.cs` | Shared 90%-of-active-model-limit compactor that removes completed tool context at full tool-batch boundaries, preserves user/system context, and emits the runtime re-lookup notice. |
| `ChatModelHistory.cs` | Canonical cross-turn replay policy: retains non-empty system/user/assistant text while excluding persisted tool calls, tool results, and model-only attachments. |
| `IChatMessageStore.cs` | Common message persistence boundary implemented by the six existing feature-specific transcript repositories. |
| `ChatImageAttachmentService.cs` | Shared project-image attachment resolver/persistence and current-turn multimodal message builder for all six user-facing chats. |

### Components/Layout/

| File | Description |
|------|-------------|
| `MainLayout.razor` / `.css` | Viewport-locked application shell with the branded Lorekeeper top bar, independently interactive update-control host, route-aware page/workspace padding, and global error notice. |
| `DesktopUpdateControl.razor` (+ `.razor.css`) | Top-bar update UI for automatic download/restart progress or a conditional external-browser Download Update action when manual discovery finds a newer release. |
| `PrintLayout.razor` / `.css` | Minimal no-navigation layout used by print-oriented pages; owns the viewport scroll container while restoring unbounded overflow for printed output. |
| `PageHeader.razor` | Reusable editorial page heading with eyebrow, title, description, and optional actions. |
| `ConfigurationShell.razor` (+ `.razor.css`) | Shared configuration-page wrapper with page heading, Projects return action, and Providers/Embeddings/Search switcher. |
| `ReconnectModal.razor` / `.cs` / `.css` | UI shown when the SignalR circuit drops. |

### Components/Pages/

| File | Description |
|------|-------------|
| `Home.razor` (+ `.razor.css`) | Project-selection hub at `/` with project cards, create/rename/delete-with-confirm, and entry cards for the three application configuration areas. |
| `Error.razor` | Error page rendered by exception handler middleware. |
| `NotFound.razor` | 404 page wired through `UseStatusCodePagesWithReExecute`. |

### Components/Pages/Projects/

| File | Description |
|------|-------------|
| `ProjectLayout.razor` (+ `.razor.css`) | Shared project workspace shell: loads the project, renders the section tabs, condenses Editor navigation into one scrollable header row, and exposes `Project` via `CascadingValue`. |
| `EditorPage.razor` | Editor tab routes (`/projects/{Slug}/editor` and `/projects/{Slug}/editor/{ChapterId:guid}`), wraps `ProjectLayout` + `EditorContent`, and synchronizes in-place chapter selection with its route parameter. |
| `EditorContent.razor` (+ `.razor.css`, `.razor.js`) | Context-aware Edit/Read/Pages/Review chapter workspace with in-place chapter switching/URL history, a stable project chat mount, keyed semantic editor, serialized refresh coordination, reusable project-image and Book Text Style modals, Assistant Memory default reset/refresh routing, and browser-local per-project chapter mode, Read view, and resizable/collapsible Chat/Memory preferences. |
| `ChapterBodyEditor.razor` (+ `.razor.css`) | Shared Editor/Publish ProseMirror host with disposal-safe attachment, visible-mode autofocus, persistent selection caret, content-driven scrolling, 500 ms action grouping, database-backed history controls/shortcuts/selection restoration, revision-aware save/flush with authoritative-current stale-save adoption, compact formatting and Book Text Style controls, modal image selection, Figure controls, and caller-controlled Designed Page insertion. |
| `ChapterReadPreview.razor` (+ `.razor.css`) | Press-backed current-chapter page preview with flush-before-layout, width-fit default, single/facing display, fixed two-leaf spread rows with an optional visible seam, fit/zoom controls, project page setup, exact bundled/imported font-face URLs, images, labels, links, identifier-safe authoring warnings, and retryable failures. |
| `BookTextStylesModal.razor` (+ `.razor.css`) | Shared Editor/Publish modal shell for the revision-aware Book Text Styles manager. |
| `ManuscriptStylesPanel.razor` (+ `.razor.css`) | Compact Book Text Style manager with an on-demand create/edit form, revision-aware paragraph/character typography, spacing, indentation, pagination, and stable semantic roles. |
| `CompositionVisualEditorShell.razor` (+ `.razor.css`) | Shared Pages/Cover visual-editor chrome with a compact view toolbar, canvas-first stage, fixed bottom object/context controls, and a host-supplied on-demand details drawer. |
| `DesignedPageWorkspace.razor` (+ `.razor.css`) | Contextual exact-geometry page/spread editor using the shared visual shell, persisted active-variant and selected-object history, parent state/refresh reporting, assistant-turn control locking without canvas dimming, modal image picking, fitting, direct text editing, compact non-blocking diagnostics, reconciled database-backed undo/redo, and authoritative-current serialized autosave. |
| `CoverCompositionWorkspace.razor` (+ `.razor.css`) | Embedded print-wrap/digital-front editor using the shared visual shell and interaction language, with top-bar return navigation, persistent reconciled aggregate history/selection, assistant-turn control locking without canvas dimming, modal artwork picking, fitting, artwork-behind-copy stacking, canonical copy bindings, safety overlays, on-demand details, and authoritative-current serialized autosave. |
| `ProjectFontManagerModal.razor` | Project font catalog manager for TTF/OTF imports, available-face inspection, embedding-right declarations, and guarded in-use deletion. |
| `EditorChatPanel.razor` (+ `.razor.css`) | Stable project-keyed Editor chat adapter over `ChatSurface`: defers/coalesces transcript hydration without reloading for chapter-only navigation, refreshes only the assembled-prompt token estimate when the active chapter changes, reconciles editor lock state with persistent turns, streams tool/contest updates, routes plain-text changes to Review mode and structured changes to the pending-edits modal, and opens current contests. |
| `ContextItemDetailModal.razor` (+ `.razor.css`) | Shared context detail modal for project material plus editable Project Guidance and structured Book Brief fields; preserves the distinction between user direction and the assembled code-owned system prompt. |
| `RecommendedContextPanel.razor` (+ `.razor.css`) | Editor right-column context recommender: shows semantic/manual/graph-proximity recommendations for entities plus structural references, and adds them to the active chapter's persisted context working set. |
| `ContextFeedPanel.razor` (+ `.razor.css`) | Assistant Memory list with Project Guidance, Book Brief, protected chapter context, persisted include/exclude choices, and a read-only preview of the one assembled system-role prompt. |
| `GraphPage.razor` | Graph tab at `/projects/{Slug}/graph`; wraps the project shell and hosts the interactive graph workspace. |
| `GraphContent.razor` (+ `.razor.css`, `.razor.js`) | Obsidian-inspired full-project graph workspace: loads graph snapshots, filters/searches nodes, hides source provenance and auto mention links by default, bridges to `vis-network`, and coordinates graph refreshes. |
| `GraphDetailsPanel.razor` (+ `.razor.css`) | Selected-node read-only graph overview side panel with node metadata, properties, knowledge/canon details, adjacent relationships honoring the auto-link toggle, node navigation, and edit entrypoints. |
| `GraphEditModal.razor` (+ `.razor.css`) | Full graph editing modal for node create/edit/delete, managed parent assignment, and custom relationship create/edit/delete while managed/provenance/auto links remain read-only. |
| `IngestPage.razor` | Ingest tab at `/projects/{Slug}/ingest`; wraps `ProjectLayout` and hosts `Ingest.IngestContent`. |
| `ResearchPage.razor` | Research tab at `/projects/{Slug}/research`; wraps `ProjectLayout` and hosts `Research.ResearchContent` when an active search provider is configured. |
| `ImportExportPage.razor` | Import / Export tab at `/projects/{Slug}/import-export`; wraps `ProjectLayout` and hosts `ImportExport.ImportExportContent`. |
| `ImagesPage.razor` | Images tab at `/projects/{Slug}/images`; wraps `ProjectLayout` and hosts `Images.ImagesContent`. |
| `PublishPage.razor` | Publish tab at `/projects/{Slug}/publish`; wraps `ProjectLayout` and hosts `Publish.PublishContent`. |
| `OutlinePage.razor` | Outline tab route; wraps `ProjectLayout` + `Outline.OutlineContent`. |
| `WritingSamplePage.razor` | Writing Sample tab at `/projects/{Slug}/writing-sample`; wraps `ProjectLayout` + `WritingSample.WritingSampleContent`. |

### Components/Pages/Projects/Ingest/

| File | Description |
|------|-------------|
| `IngestContent.razor` (+ `.razor.css`) | Functional Ingest tab workspace: creates text/EPUB/PDF/manual-webpage jobs, accepts sequential per-file batches with individual results, preserves single-text edits, manages resume/restart/delete controls, and shows live LLM progress, diagnostics, chunk/finalization reports, and artifact options. |

### Components/Pages/Projects/Research/

| File | Description |
|------|-------------|
| `ResearchContent.razor` (+ `.razor.css`) | Research workspace: hides behind active search-provider readiness, hosts entity-first research chat plus a Research Activity sidebar for touched entities and accessed URLs with detail modals. |
| `ResearchChatPanel.razor` (+ `.razor.css`) | Research chat adapter over `ChatSurface`; keeps transcripts visible, disables LLM controls without a working provider, streams web/search and graph tool calls, and supports review. |

### Components/Pages/Projects/ImportExport/

| File | Description |
|------|-------------|
| `ImportExportContent.razor` (+ `.razor.css`) | Graph-focused Import / Export workspace: downloads Full/Non-structural Lorekeeper graph JSON, uploads export JSON, queues import jobs, subscribes to live job updates, and shows progress/report history. |

### Components/Pages/Projects/Images/

| File | Description |
|------|-------------|
| `ImagesContent.razor` (+ `.razor.css`, `.razor.js`) | Three-pane image workspace with fixed-size library/job cards, chapter-use badges and disabled protected deletion, Images Chat, queued generation/edit jobs, attach-to-chat/entity actions, non-destructive cropping, shared full-size viewing, manual generation, and mask edits. |
| `ImagesChatPanel.razor` (+ `.razor.css`) | Images Chat adapter over `ChatSurface`: loads transcript, streams text/tool updates, manages attached image context chips with shared viewing, renders image visual strips, and refreshes the image grid after mutations. |

### Components/Pages/Projects/Publish/

| File | Description |
|------|-------------|
| `PublishContent.razor` (+ `.razor.css`) | Progressively loaded two-column Core Book/release workspace with persistent assistant, collapsible Front/Main content/Back flow, direct chapter and publication-section inclusion/placement, product-filtered print-release setup, authoritative-current stale-save handling, target-preserving full-height editors, progress, previews, blockers, and downloads. |
| `PublicationPdfPreview.razor` (+ `.razor.css`) | Lorekeeper-owned artifact viewer with height-contained single-page or fixed two-leaf facing rows, transparent parity slots, optional visible page seam, lazy immutable page images, and facing/seam-hidden defaults. |
| `PublicationEpubPreview.razor` (+ `.razor.css`) | Full-height sandboxed reader for the actual prepared EPUB artifact, with spine/contents selection, previous/next navigation, responsive reflow, and fit-to-workspace fixed-layout pages. |
| `PublishChatPanel.razor` (+ `.razor.css`) | Refresh-safe Publish adapter over shared `ChatSurface` with a flush-returned exact turn context, persisted transcript/tool chips, streaming, Stop/Reset, provider/token state, image attachments, active-turn reconnection, and structured workspace mutation callbacks. |

### Components/Pages/Projects/Outline/

| File | Description |
|------|-------------|
| `OutlineContent.razor` (+ `.razor.css`) | Top-level Outline tab orchestrator with serialized authoritative project/act/chapter reloads. Its three independently scrolling panes host chat, outline tree, and the Book Brief/facts/entities side column; successful reloads bump the child-panel refresh signal. |
| `BookBriefPanel.razor` (+ `.razor.css`) | Collapsible Outline-side Book Brief viewer with explicit canonical-ingest-source selection; refreshes persisted direction and source state after Outline mutations. |
| `ProjectFactsPanel.razor` (+ `.razor.css`) | Editable project facts block surfaced at the top of the right side column. Reads/writes `ProjectFact` graph nodes via `IProjectFactService`, keeps the compact collapsible key/value UX, supports add/edit/delete, shows linked graph entities, and autosizes fact textareas via `wwwroot/js/autosizeTextareas.js`. |
| `IngestSourcesPanel.razor` (+ `.razor.css`) | Read-only Outline side panel for structural ingest Source → SourceChunk → SourceBlock graph nodes, showing chunk/block locators and linked entities. |
| `OutlineTree.razor` (+ `.razor.css`) | Hierarchical Acts → Chapters tree with stronger act boundaries and visibly inset chapter rows. Acts are collapsible, drag-reorderable groups with inline-editable title/synopsis and `+ Chapter` / Delete (chapters fall back to Unassigned via `OnDelete.SetNull`). Act/chapter synopsis textareas autosize to their content via `wwwroot/js/autosizeTextareas.js`. Chapters are inline-editable rows with a beats-toggle caret + count badge (renders `ChapterBeats` inline when expanded), stale/failed vector-index badge, drag-reorder within their act bucket, an act-picker `<select>` for cross-act moves, Open link, and delete-with-confirm. Re-fetches per-chapter beat counts via `IEntityService.CountChildrenAsync` whenever `RefreshSignal` bumps. |
| `EntitiesPanel.razor` (+ `.razor.css`) | Registry-driven project-scoped entities side panel. Lists non-structural graph types from `IEntityTypeService`, supports `+ Type`, per-type `+ Add`, clickable entity rows, and a Bootstrap-style modal with editable name/properties plus read-only adjacent graph links via `IEntityService.ListLinksAsync`. Save computes property diffs and calls `UpdateAsync(propertiesToSet, propertiesToRemove)`; modal also exposes Delete-with-confirm. Re-reads on `RefreshSignal` bumps. |
| `ChapterBeats.razor` (+ `.razor.css`) | Inline beats expander rendered inside each chapter row. Loads beats via `IEntityService.ListAsync(projectId, "Event", chapterId)` ordered by `Order`, supports inline-edit (name + autosizing summary textarea), drag-reorder (calls `ReorderAsync`), `+ Add beat` and delete-with-confirm. Re-reads on `RefreshSignal` bumps. |
| `OutlineChatPanel.razor` (+ `.razor.css`) | Outline chat adapter over `ChatSurface`; keeps transcripts visible, disables LLM controls without a working provider, streams text/tool updates, and opens pending-change review. |
| `PendingAiChangesModal.razor` (+ `.razor.css`) | Durable AI change review modal reused by Outline Chat and Editor Chat: groups queued tool changes by resource, shows dependency/cascade/conflict diagnostics, renders text, structure, and visual-block diffs with JSON fallback, and keeps failed items visible for explicit rejection. |

### Components/Pages/Projects/WritingSample/

| File | Description |
|------|-------------|
| `WritingSampleContent.razor` (+ `.razor.css` / `.razor.js`) | Top-level Writing Sample tab orchestrator. Three-pane CSS-grid layout (coach chat | sample editor | sample list), owns its serialized textarea/gutter bridge, autosaves the active sample body, supports title edits, and flushes pending edits before sample switches or coach sends. |
| `WritingCoachPanel.razor` (+ `.razor.css`) | Writing Coach adapter over `ChatSurface`; keeps transcripts visible, disables LLM controls without a working provider, and streams project-level coaching/read-only tool cards. |
| `WritingSampleListPanel.razor` (+ `.razor.css`) | Right-side sample manager with clickable active rows, excerpts, updated timestamps, `New`, and delete-with-confirm. |

### Components/Pages/Settings/

| File | Description |
|------|-------------|
| `Providers.razor` (+ `.razor.css`) | Connection-card LLM configuration UI: Codex OAuth, grouped credential connections and nested models, per-model thinking effort, connection editing/deletion, readiness tests, working-only defaults, and expandable add flows. |
| `Embeddings.razor` (+ `.razor.css`) | Active embedding summary and configuration workflow: test-before-save, dimensions, rebuild confirmation, and an explicit semantic-feature danger zone. |
| `SearchProviders.razor` (+ `.razor.css`) | Card-based SerpApi/Brave configuration for Research: add/edit/test/activate providers, API keys, and confirmed deletion. |
| `DataRecovery.razor` | Structured-manuscript migration journal and local backup recovery UI with explicit two-step restore confirmation. |

### Models/

| File | Description |
|------|-------------|
| `AuthType.cs` | Enum: None, ApiKey, OAuth. |
| `LlmProvider.cs` | EF entity for an LLM endpoint/model row. Supports parent/child credential sharing, per-model reasoning effort, and persisted chat/vision readiness snapshots. |
| `LlmReasoningEffort.cs` | Full model reasoning-effort enum plus exact provider wire-value mapping (`none` through `max`). |
| `EmbeddingConfiguration.cs` | Singleton EF entity for the active embedding setup: top-level provider connection, embedding API kind, model id, dimensions, last-tested snapshot, and timestamps. |
| `OAuthToken.cs` | EF entity holding access/refresh tokens for an OAuth-backed provider. |
| `Project.cs` | EF project root with optional user-owned `ProjectGuidance`, stable slug/settings, one `BookBrief`, and navigation to conversations, images, fonts, jobs, publishing, and graph rows. |
| `BookBrief.cs` | Canonical high-level authorial-direction model, relational selected-canonical-ingest-source mapping, `BookKind` enum, and partial-patch contract whose null values are unchanged and `ClearFields` explicitly removes values. |
| `Act.cs` | EF entity for a top-level outline grouping (Title/Synopsis/Order) under a `Project`. Cascade-deleted with the project. Owned chapters survive act deletion (FK `OnDelete.SetNull`). |
| `Chapter.cs` | EF chapter with canonical manuscript-v4 JSON/revision and computed plain-text/document projections, plus title/synopsis/order, optional act, and vector-index state. |
| `ChapterVisualMode.cs` / `ChapterVisualLayouts.cs` | Isolated legacy import/migration DTOs and original 8.5 × 11 leaf-geometry mapping for interpreting earlier chapter visual records; no current runtime authoring path consumes them. |
| `CompositionModels.cs` | Page composition/variant/stage entities, active authoring-variant ownership, and shared scene, object/style, reading-order, region, and geometry-neutral generation-target contracts. |
| `AuthoringHistoryModels.cs` | Persistent per-document history streams, completed snapshots, durable assistant-turn batches, selection state, origins, cursors, and image/font/composition dependency references. |
| `ProjectPageSetup.cs` | One-to-one project-owned authoring page geometry, margins, body typography, preset, and revision state. |
| `ManuscriptMigrationJournal.cs` | Durable structured-manuscript migration phase, counts, hashes, backup path, timing, and redacted failure state. |
| `ProjectFontFamily.cs` / `ProjectFontFace.cs` | Project-scoped EF entities for imported font families and static face bytes, with weight/italic metadata and project cascade ownership. |
| `ManuscriptStyleDefinition.cs` | Project-owned Book Text Style entity with normalized generated keys, immutable semantic identity, paragraph/character definition JSON, and revision token. |
| `EditorContextPreference.cs` | EF entity for per-chapter Context Feed include/exclude preferences keyed by context item kind + stable item key. |
| `EditorConversation.cs` | EF entity — one persistent multi-turn editor chat per `Project` (unique on `ProjectId`). Owns ordered `EditorMessage`s; cascade-deleted with the project. |
| `EditorMessage.cs` | EF Editor transcript row with role/content/tool metadata plus the bounded included/omitted context-provenance snapshot stored on outgoing user turns. |
| `EditorMessageVisual.cs` | EF entity for Editor Chat visual attachments shown as thumbnails, including project-image references or optional stored bytes. |
| `ChatMessageImageAttachment.cs` | Shared ordered association from a user chat message/surface to a reusable project image asset. |
| `EditorRevisionJob.cs` | EF entity for one prose-only background revision job spawned by Editor Chat; owns per-chapter worker sessions and parent tool-call metadata. |
| `EditorRevisionSession.cs` | EF entity for one chapter worker session: assignment, original manuscript snapshot, provider/model, semantic operation payload, status, timing, and errors; migrated terminal line edits remain versioned audit data. |
| `EditorRevisionMessage.cs` | EF entity for persisted worker transcript rows, including assistant tool-call manifests and read-only tool result rows. |
| `OutlineConversation.cs` | EF entity — one persistent multi-turn collaborative chat per `Project` (unique on `ProjectId`). Owns ordered `OutlineMessage`s; cascade-deleted with the project. |
| `OutlineMessage.cs` | EF entity for a single chat row in an `OutlineConversation`: monotonic `Order`, `OutlineMessageRole` (System/User/Assistant/Tool), text `Content`, JSON `ToolCallsJson` for assistant function-calls, `ToolCallId` + `ToolName` for tool results, `OutlineMessageStatus` (Pending/Completed/Failed/Cancelled), optional `ErrorMessage`. |
| `WritingSample.cs` | EF entity for a project-scoped prose sample used as a future style reference. Stores title/body plus created/updated timestamps. |
| `WritingCoachConversation.cs` | EF entity — one resettable Writing Coach transcript per `Project` (unique on `ProjectId`). Owns ordered `WritingCoachMessage`s; cascade-deleted with the project. |
| `WritingCoachMessage.cs` | EF entity for a single Writing Coach chat row with monotonic `Order`, role (`System`/`User`/`Assistant`/`Tool`), text content, assistant `ToolCallsJson`, tool result metadata, status, optional error, and creation timestamp. |
| `SearchProvider.cs` | EF entity for configured web search providers used by Research Mode. Supports SerpApi and Brave in v1, stores API key/config JSON, and tracks the single active provider. |
| `ResearchConversation.cs` | EF entity — one persistent project research chat per `Project` (unique on `ProjectId`). Owns ordered `ResearchMessage`s; cascade-deleted with the project. |
| `ResearchMessage.cs` | EF entity for a single Research chat row with monotonic `Order`, role (`System`/`User`/`Assistant`/`Tool`), text content, assistant tool-call JSON, tool result metadata, status, optional error, and creation timestamp. |
| `PublishConversation.cs` / `PublishMessage.cs` | One project-scoped persistent Publish conversation and ordered role/status/content/tool/error transcript rows, cascade-owned by the project. |
| `PublishMessageVisual.cs` | Tool-associated Publish canvas-preview attachment with stored PNG bytes, bounded display metadata, and cascade ownership by its transcript row. |
| `AiChangeBatch.cs` | EF entity grouping AI-proposed tool mutations from one assistant turn while they await approval/resolution, including the protected Core/release Editor target. |
| `AiChange.cs` | EF entity for one queued AI tool mutation: tool metadata, before/after/result JSON, dependency metadata, status, rejection/error notes, timestamps. |
| `ContestBatch.cs` | EF entity for one Editor Contest Mode run: captured Core/release target and chapter/body snapshot, operation metadata, status, and model candidates. |
| `ContestCandidate.cs` | EF entity for one model's contest proposal: provider/model labels, validated semantic operations, proposed manuscript, raw response, status, timing, and errors. |
| `IngestSource.cs` | EF entity for one ingested source: full source text, rolling synopsis, source metadata/instructions, content hash, source page/block locators, optional webpage URL/fetch provenance, and independent vector-index state/source id. |
| `IngestSourcePage.cs` | EF entity for PDF page-level provenance: page text, char bounds, extraction method, render/image hash metadata, vision provider/model, and diagnostics. |
| `IngestSourceBlock.cs` | EF entity for source section/page/block locators with kind/title/locator, optional page link, char bounds, and metadata JSON. |
| `IngestSourceChunk.cs` | EF entity for a large logical source chunk used as extraction checkpoint; tracks character bounds, token count metadata, summaries, and structure status. |
| `IngestVectorFragment.cs` | EF entity mapping small retrieval vector fragments back to an ingest source with vector row id, char bounds, and metadata. |
| `IngestJob.cs` | EF entity for durable async ingest job state, progress counters, selected provider/model snapshot, encoding metadata, and source/job relationships. |
| `IngestJobChunk.cs` | EF entity for per-source-chunk ingest processing status, timestamps, errors, created item counters, and persisted LLM token-count metadata. |
| `IngestReportItem.cs` | Legacy EF entity for pre-staging ingest report rows retained for existing database compatibility. |
| `IngestStagingRecord.cs` | EF entity for temporary chunk-pass ingest records that back the Ingest report/history UI and are finalized into canon source properties plus simple graph edges. |
| `IngestJobEvent.cs` | EF entity for ingest progress/debug events such as tool calls and failures. |
| `ProjectImportJob.cs` | EF entity for durable project import job state: uploaded JSON payload, source format metadata, status/progress counters, import counts, warnings, errors, and timestamps. |
| `ProjectImportReportItem.cs` | EF entity for import job report rows covering validation, structural appends, type/entity/relationship merges, indexing warnings, and failures. |
| `WebIngestCandidate.cs` | EF entity for cached webpage/search-result sources used by Research and manual webpage ingest. Stores search/fetch provenance, extracted text/excerpt, cached links JSON, content hash, staging rationale, and queued ingest job id. |
| `PublicationBook.cs` | One revisioned Core Book per project with shared metadata, structure/PDF-presentation defaults, fixed outline content, and reusable front-cover scene. |
| `PublicationEdition.cs` | Optional paperback/hardcover/EPUB/PDF ebook release aggregate with destination, exact registry/product selection or Generic printer template, identifier, status, sparse Core field/section-order overrides, and opt-in edition-content state. |
| `PublicationEditionChapterOverride.cs` | Copy-on-write release chapter snapshot with Core base revision/hash and edition-owned manuscript revision. |
| `PublicationEditionOutlineItem.cs` | Sparse release chapter-inclusion overlays with typed foreign keys; reading order always follows the Core outline and act presentation is controlled by release settings. |
| `PublicationSection.cs` | Core/release publication section aggregate with live content inheritance, explicit inclusion/start-side settings, outline-relative anchors, semantic manuscript content, and optional section-owned Designed Page compositions; release-only order stays separate from content customization. |
| `PublicationMatter.cs` / `PublicationImagePlacement.cs` | Historical persisted shapes retained only for protected startup conversion and versioned pre-v19 import; active publishing never reads or mutates them. |
| `PublishAsset.cs` | EF entity for uploaded/generated/edited/cropped project images with bytes, crop lineage/coordinates, alt text, prompt/source metadata, masks, and placement navigation. |
| `PublicationRender.cs` | Core/release-targeted render jobs, immutable artifact bytes/provenance including private Reading PDFs, statuses, and stable manuscript-block page maps. |
| `PublicationCoverDesign.cs` | Revisioned release cover override with Core-front inheritance, canonical copy/bindings, independent surface scenes, barcode behavior, and acknowledgement state. |
| `PublicationPreparation.cs` | Persisted reconnectable Core/release preparation job, status, progress, blockers, cancellation, and timestamps. |
| `EntityVisualExample.cs` | Ordered labeled many-to-many link between an eligible graph entity and project image, with origin and source provenance. |
| `SourceVisualCandidate.cs` | Cached normalized Research/Ingest image bytes and artifact/web provenance before project-library promotion. |
| `ProjectImageConversation.cs` | EF entity for the separate project-scoped Images Chat transcript. |
| `ProjectImageChatAttachment.cs` | EF entity for project image assets explicitly attached as visible Images Chat context chips. |
| `ProjectImageMessage.cs` | EF entity for Images Chat messages with assistant tool-call manifests, tool result metadata, status, and errors. |
| `ProjectImageMessageVisual.cs` | EF entity for Images Chat visual attachments, including project-image references or optional stored bytes. |
| `ProjectImageGenerationJob.cs` | EF image job with structured brief, compiled prompt, ordered reference manifest, target geometry, provider revised prompts/IDs, outputs, progress, and diagnostics. |
| `ProjectImageMask.cs` | EF entity for validated PNG masks tied to source project images, using transparent pixels as editable regions. |
| `GraphNode.cs` | Generic graph node: `(ProjectId, NodeType, Key)` unique, JSON properties bag. Cascade-deleted with its `Project`. |
| `GraphEdge.cs` | Directed edge between graph nodes with type, JSON properties, optional relationship-specific `SortOrder`, and timestamps. |
| `GraphEntityType.cs` | Lightweight project-scoped graph type registry entry for UI/LLM labels/defaults. Descriptive rather than restrictive; arbitrary node types remain valid. |

### Authoring/

| File | Description |
|------|-------------|
| `AuthoringHistoryService.cs` | Canonical database-backed Undo/Redo streams with compressed baselines/results, 100-action retention, live-snapshot stale-stream reconciliation, Redo branching, assistant-turn finalization/recovery, selection state, dependency retention, and detached-composition pruning. |
| `AuthoringMutationContext.cs` | Scoped assistant mutation provenance and persisted chat-turn batch lifetime, including stopped/failed/cancelled finalization. |
| `AuthoringSnapshotCodec.cs` | Deterministic chapter/section/composition/cover aggregate snapshot capture plus nested image, font, and composition dependency discovery without copying asset binaries. |

### Startup/

| File | Description |
|------|-------------|
| `ApplicationStartupState.cs` | Thread-safe immutable startup snapshots, migration-progress contract, database readiness gate, terminal recovery/failure state, and minimum-splash configuration. |
| `ApplicationStartupWorker.cs` | Background startup owner that yields for the host surface, runs ordered migrations and normal initialization, publishes progress, and releases database workers only after a safe result. |

### Persistence/

| File | Description |
|------|-------------|
| `AppDatabaseOperations.cs` | Per-operation EF context factory, no-tracking read lifetimes, project-aware/process-wide write leases in fixed lock order, and nested-operation sharing for intentionally atomic multi-service work. Short write units opt into tracking and own commit/disposal. |
| `AppDbContext.cs` | EF Core model for projects, page setup, providers, chats, writing, graph, ingest/import, publishing, composition, fonts, and Book Text Styles. Configures relationships/indexes, JSON property bags, revision advancement, fail-closed concurrency reporting, publication-target normalization, and bounded transient SQLite lock retries. |
| `DatabaseMigrationRecoveryService.cs` | Shared protected SQLite backup/restore, recovery-shell, expiring confirmation, backup discovery, and reference-aware pruning boundary for guarded migrations. |
| `DatabaseStartupMigrationService.cs` | Single application-startup schema/data migration orchestrator shared by the real host and installed-database fixtures; applies scheduled restores and honors recovery markers before opening EF, with optional coarse progress reporting. |
| `ProjectMutationCoordinator.cs` | Project-scoped async serialization for manuscript-reference writes and style/image deletion integrity. |
| `PersistenceServiceCollectionExtensions.cs` | `AddLorekeeperPersistence` switch on `Persistence:Provider` (SQLite today; Postgres slot for future); registers the no-tracking `IDbContextFactory`, database-operation factory, and singleton write coordinator. |
| `SqliteConnectionSettings.cs` | Shared SQLite connection-string and startup PRAGMA settings, including project-root development and packaged per-user database-path resolution, busy timeout, WAL journal mode, and normal synchronous mode. |
| `Migrations/` | Immutable EF history plus structured-manuscript, semantic-editor, publication-release, Publish-chat, Press, authoring-page, Core Book, edition-content, physical-product/cover-surface, source-evidence/canonical-source selection, persistent authoring-history, and cleanup migrations with the current model snapshot. |

### Persistence/Repositories/

| File | Description |
|------|-------------|
| `DatabaseRepositories.cs` | Operation-owned repository bundle. Repository contracts stage changes on their context; the enclosing write operation alone commits, so multi-repository mutations share one unit of work. |
| `ILlmProviderRepository.cs` / `LlmProviderRepository.cs` | CRUD + atomic `SetDefaultAsync` for `LlmProvider`. |
| `IEmbeddingConfigurationRepository.cs` / `EmbeddingConfigurationRepository.cs` | Persistence for the singleton active embedding configuration, eager-loading its selected provider connection. |
| `IOAuthTokenRepository.cs` / `OAuthTokenRepository.cs` | Fresh no-tracking latest/valid OAuth token reads plus atomic replace-for-provider persistence. |
| `IProjectRepository.cs` / `ProjectRepository.cs` | Project CRUD plus materialized UI lists/slug reads and an explicit id snapshot; slug uniqueness check; ordered list by `UpdatedAt`. |
| `IGraphNodeRepository.cs` / `GraphNodeRepository.cs` | Node CRUD plus project/type/id-list projections for graph/entity/fact/beat reads. |
| `IGraphEdgeRepository.cs` / `GraphEdgeRepository.cs` | Edge CRUD plus fresh no-tracking directional/project read projections. Defines `EdgeDirection` enum. |
| `IGraphEntityTypeRepository.cs` / `GraphEntityTypeRepository.cs` | Project-scoped CRUD for lightweight graph type registry rows. |
| `IChapterRepository.cs` / `ChapterRepository.cs` | Chapter CRUD ordered by `Order`; max-order and reorder operations are scoped to one act bucket and writes are staged on the owning database operation. |
| `IActRepository.cs` / `ActRepository.cs` | Act CRUD ordered by `Order` per project; `ReorderAsync` rewrites the act ordering in one save. |
| `IOutlineConversationRepository.cs` / `OutlineConversationRepository.cs` | Persistence for `OutlineConversation` + ordered `OutlineMessage`s: `GetByProjectIdAsync`, `LoadMessagesAsync`, `GetMaxOrderAsync`, `AddConversationAsync`, `AddMessageAsync`, `UpdateMessage`, `RemoveConversation`. |
| `IEditorConversationRepository.cs` / `EditorConversationRepository.cs` | Persistence for project-wide Editor Chat: lean model-history messages, metadata-only UI transcript visuals (binary data stays endpoint-loaded), order lookup, and add/update/remove staging. |
| `IEditorRevisionRepository.cs` / `EditorRevisionRepository.cs` | Persistence for Editor Revision jobs, per-chapter worker sessions, and ordered worker transcript/tool-result messages. |
| `IWritingSampleRepository.cs` / `WritingSampleRepository.cs` | Project-scoped writing sample persistence: list by project (newest updated first), get/count, add/update/remove, and save. |
| `IWritingCoachConversationRepository.cs` / `WritingCoachConversationRepository.cs` | Persistence for the resettable project-level Writing Coach conversation + ordered messages, including assistant tool-call manifests and tool result rows. |
| `ISearchProviderRepository.cs` / `SearchProviderRepository.cs` | CRUD plus active-provider selection for Research Mode search providers. |
| `IResearchConversationRepository.cs` / `ResearchConversationRepository.cs` | Persistence for project-wide Research conversation + ordered messages, including assistant tool-call manifests and tool result rows. |
| `IPublishConversationRepository.cs` / `PublishConversationRepository.cs` | Persistence for the unique project-wide Publish conversation and ordered shared-chat message contract. |
| `IProjectImageConversationRepository.cs` / `ProjectImageConversationRepository.cs` | Persistence for project-wide Images Chat conversations, ordered messages, tool result rows, and message visuals. |
| `IAiChangeRepository.cs` / `AiChangeRepository.cs` | Persistence for pending AI change batches and changes, including eager-loaded pending batch listing and change lookup for approval actions. |
| `IContestRepository.cs` / `ContestRepository.cs` | Persistence for Editor Contest Mode batches and candidates, including current/history project batch listing, detail loading, candidate lookup, and status updates. |
| `IEditorContextPreferenceRepository.cs` / `EditorContextPreferenceRepository.cs` | Persistence for active-chapter Context Feed include/exclude preferences, including chapter-wide override removal for default resets. |
| `IIngestRepository.cs` / `IngestRepository.cs` | Persistence for ingest sources, pages/blocks, chunks, vector fragments, jobs, staging records, legacy report items, and events, including chunk-aware report projections, lightweight resume/processor reads, and scalar-only update helpers. |
| `IWebIngestCandidateRepository.cs` / `WebIngestCandidateRepository.cs` | Persistence for project-scoped cached webpage sources discovered by Research Mode or manual URL reading, including staged-page listing for Ingest and URL de-duplication. |
| `IProjectImportRepository.cs` / `ProjectImportRepository.cs` | Persistence for project import jobs and report items, including list/detail UI projections, queued/interrupted job lookup, and delete/save operations. |

### Knowledge/

| File | Description |
|------|-------------|
| `IVectorStore.cs` | Vector storage abstraction over a `scopeKey` namespace (store/search/multi-search/delete) plus maintenance operations for optional dimension-aware initialization/recreation; search results expose row id, metadata, and chunk/vector-fragment index. |
| `SqliteVecVectorStore.cs` | sqlite-vec implementation; tolerates missing vector tables when embeddings are disabled, and recreates `vec_knowledge` for active embedding dimensions during rebuilds. |
| `VectorStoreInitializer.cs` | Creates `knowledge_chunks`, internal FTS5 project-search tables, and optionally `vec_knowledge` outside EF migrations, using active embedding dimensions when configured. |
| `IGraphStore.cs` | High-level graph API: upsert nodes/edges (including optional edge `SortOrder`), neighbors (BFS, depth-bounded), path-finding. Defines `GraphDirection`, `GraphTraversalOptions`, `GraphPath`, `GraphPathHop`. |
| `RelationalGraphStore.cs` | Relational-table implementation backed by node/edge repositories; structural edge upserts preserve/update `SortOrder`; doc-comments describe contract any future backend must honor. |
| `ITextChunker.cs` / `OverlappingTextChunker.cs` | Sliding-window text chunker. Defaults `Embeddings:ChunkSize`=1200, `Embeddings:ChunkOverlap`=200. Prefers paragraph/sentence/whitespace boundaries within ±10% of target. |

### Llm/

| File | Description |
|------|-------------|
| `IEmbeddingService.cs` | Embedding generation/availability abstraction plus embedding settings DTOs for test-before-save and active-config persistence. |
| `EmbeddingClient.cs` | Provider-backed embedding HTTP client supporting Ollama native `/api/embed`, OpenAI-compatible `/v1/embeddings`, and Codex OAuth embedding endpoint routing. |
| `IEmbeddingConfigurationService.cs` / `EmbeddingConfigurationService.cs` | Application service for listing eligible provider connections, testing embedding models, saving/unsetting the singleton active config, auto-configuring Codex defaults when unset, and cancelling/requeueing rebuilds on active model changes. |
| `ProviderEmbeddingService.cs` | Active-config embedding service that resolves the saved provider/model, truncates oversized inputs, batches embedding requests, validates returned dimensions, and reports availability. |
| `EmbeddingRebuildOptions.cs` | Throttling/retry options for bulk project re-embedding, bound from `Embeddings:Rebuild`. |
| `EmbeddingRebuildQueue.cs` | Singleton rebuild coordinator: queues full re-embed requests, versions pending work, and cancels/awaits active rebuilds before embedding config changes. |
| `EmbeddingRebuildWorker.cs` | Hosted worker that drains rebuild requests one at a time, runs scoped rebuilds with coordinator cancellation, and avoids parallel project floods. |
| `EmbeddingRebuildService.cs` | Bulk rebuild service: recreates sqlite-vec dimensions, marks indexes stale, reindexes chapter bodies, ingest source fragments, and context vectors with batch delay and retry backoff. |
| `ILlmProviderService.cs` / `LlmProviderService.cs` | Provider/model CRUD, connection-wide shared-field propagation and grouped deletion, credential resolution, persisted chat/vision readiness, working-default selection, and Codex connection checks. |
| `CodexProvider.cs` | Shared Codex provider name/endpoints/defaults plus OAuth JWT account-id parsing for Codex chat, images, and embeddings. |
| `ICodexAuthService.cs` / `CodexAuthService.cs` | OpenAI Codex PKCE OAuth flow with configured local callback, serialized refresh, non-destructive reconnect handling for rejected sessions, and explicit revoke. |
| `ReasoningContent.cs` | `AIContent` subclass for Codex reasoning summary streaming. |
| `ToolCallStreamingContent.cs` | `AIContent` subclasses for provider-level function-call start and argument-delta streaming. |
| `ToolCallArguments.cs` | Shared parser/normalizer for tool-call argument JSON and SDK argument dictionaries before `AIFunction` invocation. |
| `StreamingToolCallTracker.cs` | Normalizes provider function-call start/delta/final content into app-level started/arguments/ready updates for chat services. |
| `CodexChatClient.cs` | `IChatClient` implementation for Codex Responses API with configured reasoning effort, SSE parsing, multimodal user content, function calling, strict schemas, and tool-argument streaming. |
| `OpenAIChatToolMetadataClient.cs` | OpenAI-compatible client boundary that preserves unknown streamed tool-call extensions and restores them on the correlated assistant/tool-result request. |
| `IChatClientFactory.cs` / `ChatClientFactory.cs` | Constructs Codex or metadata-preserving OpenAI-compatible chat clients, applies exact per-model reasoning effort and Codex timeout, and exposes configured verification probes. |
| `IVisionModelClientFactory.cs` / `VisionModelClientFactory.cs` | Provider-backed image reader for vision probes and PDF transcription, with per-model reasoning effort across Codex Responses and OpenAI-compatible requests. |
| `AssistantWorkflowInstructions.cs` | Current code-owned AI workflow/tool rules for durable narrated work logs, automatic-context-first and canonical-state Outline work, proactive Editor execution, visual-development canon, publication craft/design, exact IDs, compact paging/staging, image generation, validation, and Contest preparation. |
| `SystemPromptComposer.cs` | Central composer for the one actual system-role prompt, including specialized Images concept-art and Publish production charters, tool rules, dynamic guidance, Project Guidance, Book Brief, then working context. |
| `AgentOptions.cs` | Shared agent options bound from `Agents:*`; caps iterative tool-call rounds, configures transient ingest LLM retry attempts/delays, and sets Codex/OAuth request timeout. |
| `SeedSystemPrompt.cs` | Frozen historical seed retained only so legacy migrations can identify and clear untouched seeded guidance; runtime prompts no longer use it. |

### Search/

| File | Description |
|------|-------------|
| `WebSearchModels.cs` | Normalized web-search request/response/result records shared by Research tools and concrete providers. |
| `IWebSearchClient.cs` | Interface implemented by concrete API-backed search providers. |
| `IWebSearchProviderFactory.cs` / `WebSearchProviderFactory.cs` | Resolves the concrete search client for a configured `SearchProviderKind`. |
| `ISearchProviderService.cs` / `SearchProviderService.cs` | Application service for search-provider CRUD, active-provider readiness, provider tests, and active-provider search execution. |
| `SerpApiWebSearchClient.cs` | SerpApi Google-search client; maps `organic_results` into normalized `WebSearchResult`s. |
| `BraveWebSearchClient.cs` | Brave Search API client; maps `web.results` into normalized `WebSearchResult`s. |
| `ProjectSearchModels.cs` | Internal project-search source-type constants plus request/result envelopes with total/completeness metadata and source/read/index chunk records. |
| `ProjectSearchAgentPayload.cs` | Shared model-facing compact discovery envelopes for project searches/source lists, with labeled previews, complete IDs, counts, and exact paginated detail-read arguments. |
| `IProjectSearchIndex.cs` / `SqliteFtsProjectSearchIndex.cs` | FTS5-backed lexical/BM25 index for chapters, acts, entities, ingest sources, and source chunks, including source/container filters and snippets. |
| `IProjectSearchService.cs` / `ProjectSearchService.cs` | App/tool facade for internal project search: returns counted candidate/search envelopes, reads paginated source text, and fuses FTS5 keyword hits with sqlite-vec semantic hits via Reciprocal Rank Fusion. |

### Research/

| File | Description |
|------|-------------|
| `IResearchService.cs` / `ResearchService.cs` | Research adapter over the shared chat engine: builds project context, supplies cache-first web/graph tools, stages Review edits, and derives activity from compact or paginated entity envelopes. |
| `ResearchTools.cs` | Research tools for explicitly paginated entity/link and web-page reads, compact web discoveries, safe image inspection, single-entity canonical-reference import/crop, and staged graph mutations. |
| `ResearchTurnUpdate.cs` | Streaming update records consumed by `ResearchChatPanel`: text/tool updates, pending AI change creation, graph mutation refreshes, assistant completion, and turn errors/cancellation. |
| `ResearchChatTurnRunner.cs` | Background turn runner for Research chat: keeps active turns alive across component disposal and provides buffered update subscriptions. |
| `ResearchActivityModels.cs` | Read models for Research Activity sidebar entity/source summaries and cache-only source detail modals. |
| `WebResearchOptions.cs` | Configurable webpage read limits and polite-fetch defaults: user agent, timeout, max bytes, read-page size, retry timing, max links, robots, throttling, cooldowns, and private-network blocking. |
| `WebPageReader.cs` | Safe webpage/image reader applying URL, robots, private-network, redirect, throttle, byte, and supported-raster checks. |
| `WebFetchCoordinator.cs` | Per-host fetch coordinator for polite web reads: serializes requests, enforces host delay/jitter, and applies cooldowns after blocked or repeated failed responses. |
| `WebHttpFetchClient.cs` | Shared coordinated HTTP GET helper for webpage, robots, and source-adapter reads with byte limits, timeout handling, retryability, and status diagnostics. |
| `WebLinkPolicy.cs` | URL normalization and link hygiene policy for web research: filters navigation/admin/wiki namespace/static links and prioritizes likely content links. |
| `WebPageTextExtractor.cs` | HTML/text extraction for titles, canonical URLs, text, links, normalized `src`/`srcset` images, alt/captions, and decoding. |
| `WebRobotsPolicy.cs` | Lightweight cached `robots.txt` policy reader/parser used before webpage and source-adapter fetches when enabled. |
| `MediaWikiWebPageSourceReader.cs` | MediaWiki source adapter for `/wiki/{title}` pages: reads allowed `api.php` extract/parse endpoints and returns plain text plus namespace-0 article links. |
| `IWebIngestCandidateService.cs` / `WebIngestCandidateService.cs` | Cached webpage source lifecycle with page/link/image metadata, source details, staging, and manual Ingest queueing. |
| `WebIngestCandidateModels.cs` | UI/read helper records for webpage candidate lists and read results. |

### Auth/

| File | Description |
|------|-------------|
| `CodexOAuthEndpoints.cs` | Minimal-API endpoints: `GET /auth/start/{providerId}` and `GET /auth/callback`, including best-effort Codex embedding auto-configuration after OAuth success. |

### Desktop/

| File | Description |
|------|-------------|
| `DesktopUpdateService.cs` | Singleton desktop update state for automatic progress/restart plus guarded manual release-download actions and UI notifications. |
| `DesktopReleaseUpdateChecker.cs` | Public GitHub latest-release client with ETag reuse, constrained release URLs, SemVer comparison, stable-release filtering, and platform/architecture asset checks for manual updates. |

### Projects/

| File | Description |
|------|-------------|
| `IProjectService.cs` / `ProjectService.cs` | Project CRUD and blank optional Project Guidance persistence; creates the Book Brief, syncs graph defaults, preserves stable slugs, and performs indexed-project cleanup on delete. |
| `IBookBriefService.cs` / `BookBriefService.cs` | Get/create, validated partial update, explicit field clearing, stale-write-protected Visual Direction update, canonical-ingest-source list/replace operations, and compact system-prompt formatting for the Book Brief. |

### Writing/

| File | Description |
|------|-------------|
| `IWritingSampleService.cs` / `WritingSampleService.cs` | UI-facing facade for project-scoped writing samples: create/list/get/update/delete, title validation, sample body persistence, and project `UpdatedAt` touches. |
| `IWritingCoachService.cs` / `WritingCoachService.cs` | Resettable Writing Coach adapter over the shared chat engine with coach-specific guidance, current-draft context, and a read-only tool set. |
| `WritingCoachTools.cs` | Read-only Writing Coach tool builder. Exposes `read_current_section` for the latest editor draft and `list_project_facts` for graph-backed ProjectFact context. |
| `WritingCoachTurnUpdate.cs` | Streaming update records consumed by the Writing Coach panel: text deltas, tool-call start/argument/completion updates, assistant completion, and turn errors/cancellation. |
| `WritingCoachTurnRunner.cs` | Background turn runner for Writing Coach: owns per-project active turns, fresh scoped service execution, buffered UI subscription, and Stop-only cancellation. |

### Context/

| File | Description |
|------|-------------|
| `IContextBuilder.cs` / `ContextBuilder.cs` | Turn-aware one-system-message assembly with protected authorial direction, complete active-manuscript/style state, default-on Writing Samples, project page setup and active Designed Page geometry, canonical visuals, bounded retrieval, exclusions, token estimates, and provenance snapshots. |
| `AgentPayloadPaginator.cs` | Shared soft-target model payload paginator with an opt-in compact-object page shape; repeats identity fields, packs logical JSON records, and segments only individually oversized text fields with explicit continuation metadata. |
| `ContextPayloadJson.cs` | Shared compact JSON serializer settings for model-facing automatic context projections. |
| `ContextManuscriptFormatter.cs` | Compact complete active-manuscript and Book Text Style context projections with revisions, stable block IDs, inline marks, roles, and sparse formatting metadata. |
| `ContextEntityPayloadFormatter.cs` | Compact automatic entity projection that preserves meaningful data and canonical visual-reference metadata while omitting graph relationships; direct entity/link tools own paginated relationship reads. |
| `IEditorContextService.cs` | Context facade with Project Guidance/Book Brief keys, explicit per-chapter inclusions/exclusions, default reset, project-image context, and recommendation key sets. |
| `IContextRecommendationService.cs` / `ContextRecommendationService.cs` | Produces active-chapter context recommendations from second-degree graph links, direct context-vector hits, and manual search across entities plus structural references. |
| `IContextIndexingService.cs` / `ContextIndexingService.cs` | Maintains targeted direct vector rows and internal lexical search chunks for addable context items: graph entities, chapters, acts, ingest sources, and ingest source chunks; refreshes source-scoped auto mention links. |
| `VectorIndexWorkCoordinator.cs` | Scoped coordinator that can defer and dedupe expensive chapter/body/context vector index work during review apply, while normal calls run immediately. |
| `IEntityRelationContextService.cs` / `EntityRelationContextService.cs` | Shared bounded graph relation/traversal map builder for Context Feed and agent tool payloads that return entity information. |
| `ChapterFormatting.cs` | `WithLineNumbers` / `SplitLines` / `JoinLines` helpers shared by the editor gutter and line-oriented AI chapter reads. |

### EntityVisuals/

| File | Description |
|------|-------------|
| `EntityVisualModels.cs` | Read/request/change records for visual examples, entity targets, and source candidates. |
| `EntityVisualContextOptions.cs` | Limits for images per entity/turn, model input edge, and source visuals per ingest chunk. |
| `IEntityVisualExampleService.cs` / `EntityVisualExampleService.cs` | Association/candidate reads and mutations, non-throwing entity-target validation, promotion, cleanup, and entity reindexing. |
| `EntityVisualContextService.cs` | Bounded, deduplicated canonical-reference context assembly with explicit association-origin and image-source metadata for multimodal agent turns. |

### Tokens/

| File | Description |
|------|-------------|
| `ChatTokenLimitOptions.cs` | Positive validated advisory chat-token default plus case-insensitive exact model overrides and the shared active-model limit resolver. |
| `ITokenCounter.cs` / `CompositeTokenCounter.cs` | Reusable token counting abstraction; tries exact tiktoken counting first, then falls back to a char-based estimator. |
| `TokenCountRequest.cs` / `TokenCountResult.cs` | Request/result records for model-or-encoding token counting with method/exactness/warning metadata. |
| `TokenCountingOptions.cs` | Configuration for default encoding, model-to-encoding mappings, and char-estimator ratio. |
| `TiktokenTokenCounter.cs` | Exact token counter backed by `Microsoft.ML.Tokenizers` tiktoken encodings. |
| `CharEstimateTokenCounter.cs` | Conservative reusable fallback token counter based on character length. |

### Ingest/

| File | Description |
|------|-------------|
| `IIngestService.cs` / `IngestService.cs` | Application service for ingest job lifecycle/UI reads/provider resolution/artifact preprocessing/queueing/notifications, with edited text taking precedence over original text-artifact bytes; restart/delete subtracts source-scoped graph assertions, removes ingest-owned orphan graph output, and refreshes targeted context vectors. |
| `IngestCreateJobRequest.cs` | Request DTO for creating ingest jobs with source text or uploaded artifact bytes, metadata, model/profile choice, PDF options, encoding/chunk-size override, and optional webpage fetch provenance. |
| `IngestExtractionProfile.cs` | Enum for adaptive ingest profile selection: Auto, Story/Worldbuilding, or Research/Nonfiction. |
| `BookArtifactIngestOptions.cs` | Options for artifact ingestion limits and PDF vision rendering defaults such as max file size/pages, DPI, image pixels, and vision output tokens. |
| `IBookArtifactPreprocessor.cs` / `BookArtifactPreprocessor.cs` | Text/image/EPUB/PDF preprocessor for text, embedded/rendered visual candidates, vision reads, deduplication, and source locators. |
| `IIngestSourceStructureBuilder.cs` / `IngestSourceStructureBuilder.cs` | Splits raw source text into logical source chunks using headings/scene breaks, then merges adjacent sections with configurable target/soft token limits; source chunks are independent from vector fragments. |
| `IngestSourceStructureOptions.cs` | Configurable source sectioning defaults for ingest chunk target tokens, soft max ratio, and small-section merge threshold. |
| `IIngestGraphSync.cs` / `IngestGraphSync.cs` | Projects ingest sources, source synopsis, source chunks, and source blocks into structural graph nodes and ordered `HasChild` edges, including webpage/artifact provenance on source nodes when present. |
| `IIngestGraphCleanup.cs` / `IngestGraphCleanup.cs` | Source-scoped graph cleanup for ingest restart/delete: removes one source's canon metadata/markdown plus legacy assertions/citations, deleting only ingest-owned orphan graph output and returning affected entities for targeted context-vector cleanup. |
| `IIngestVectorIndexingService.cs` / `IngestVectorIndexingService.cs` | Extracted ingest source vector-fragment and lexical-fragment indexer used by ingest jobs and bulk embedding rebuilds; stores source block/page locator metadata, refreshes auto mention links, and marks sources Disabled when embeddings are intentionally unavailable. |
| `IIngestJobQueue.cs` / `IngestJobQueue.cs` | In-process queue plus active-job cancellation registry used to stop jobs and detect stale running records. |
| `IIngestJobNotifier.cs` / `IngestJobNotifier.cs` | In-process pub/sub for ingest job updates, including ephemeral live LLM/text/tool-call progress consumed by Blazor Server components. |
| `IngestUiModels.cs` | Lightweight read-model records for the Ingest tab: job summaries, selected job detail, chunk/finalization progress, staging/report items, events, and bounded source excerpts. |
| `IngestSourceAssertions.cs` | Legacy helper/model for protected source-scoped node/edge assertion JSON, ingest-created graph origin markers, report graph-action payloads, and source-subtraction operations. |
| `IngestWikiSheet.cs` | Shared wiki/evidence helper/models for ingest-managed summaries, aliases, wiki sections, source-backed `sourceEvidence.*` markdown, evidence cleanup, citations, provenance, and search projection helpers. |
| `IngestJobWorker.cs` | Hosted background worker that marks interrupted jobs/chunks stopped at startup, notifies the UI, and drains queued ingest jobs in scoped processors. |
| `IngestJobProcessor.cs` | Runs ingest with streaming staging, bounded source visuals and canonical entity references, final synthesis, relationship promotion, retries, diagnostics, and indexing. |
| `IngestAgentTools.cs` | Ingest tools for identity/observations/relationships, single-entity canonical-reference promotion with optional subject crop, final canon writes, and source progress. |

### ImportExport/

| File | Description |
|------|-------------|
| `ProjectExportModels.cs` | Current v23 portable DTOs with v4 manuscripts, selected canonical ingest sources, page setup, authoring/edition variants, Core Book publication sections/PDF presentation, sparse release ordering, print products/templates, cover surfaces, accessibility data, and custom fonts; retains isolated older input adapters. |
| `IProjectImportExportService.cs` / `ProjectImportExportService.cs` | UI-facing import/export facade: builds Full/Non-structural JSON, includes only selected canonical source bodies/evidence in Full exports, warns on Non-structural omissions, queues jobs, and emits notifications. |
| `ProjectImportUiModels.cs` | Lightweight read-model records for the Import / Export tab job list, detail view, and report rows. |
| `ProjectImportJobQueue.cs` | In-process import job queue used by the hosted worker. |
| `ProjectImportJobNotifier.cs` | In-process pub/sub for live import job updates consumed by the Blazor Import / Export tab. |
| `ProjectImportJobWorker.cs` | Hosted background worker that marks interrupted imports failed at startup and drains queued import jobs. |
| `ProjectImportJobProcessor.cs` | Runs one import job, importing v23 manuscripts, canonical sources, page setup/Core, publication sections/order overlays and compositions, releases/print products/cover surfaces/fonts; remaps source provenance, adapts older formats only at the versioned boundary, then refreshes projections and indexes. |

### Images/

| File | Description |
|------|-------------|
| `ProjectImageModels.cs` | Image and chapter-usage views, normalized crop and entity-target requests, plus persisted jobs, output state, masks, provider progress, and runtime snapshots. |
| `ImagePromptComposer.cs` | Shared structured generation/edit brief compiler with canonical-character reference schema guidance, stable provider-order labels, purposeful-space discipline, exact-aspect moderate layout rasters, reserved regions, rendered-text policy, and safe free-generation defaults. |
| `IProjectImageService.cs` / `ProjectImageService.cs` | Shared project image-library facade over stored assets: metadata and chapter-reference projection, byte reads, upload, deterministic crop/reuse, legacy blocking generation, metadata, thumbnail, deletion guarded against Figures, chapter Designed Pages, publication uses, and confirmed clearing of history-only dependencies. |
| `IProjectImageJobService.cs` / `ProjectImageJobService.cs` | Image job persistence for structured prompt audits, free-standing/layout-target classification, bounded uncropped provider-raster saving with non-blocking geometry warnings, revised prompts/output IDs, lifecycle/state/errors, and PNG/shape-mask validation. |
| `IProjectImageGenerationRuntime.cs` / `ProjectImageGenerationRuntime.cs` | Singleton FIFO image queue with one active job per project, per-job cancellation propagated to providers/retries, partial previews, completion waiters, and state notifications. |
| `ProjectImageGenerationStartupWorker.cs` | Hosted startup worker that marks interrupted running image jobs failed and resumes queued project work. |
| `AgentProjectImageWorkflow.cs` | Canonical assistant generation/edit boundary: creates unattached project images, waits/cancels to readable terminal states, reports requested/actual geometry and effective DPI, returns mismatches visually as warnings, and reconnects without prompt replay. |
| `IProjectImageProvider.cs` / `CodexProjectImageProvider.cs` | Responses image provider for Codex/OpenAI account generation and masked edits with streamed partials, explicit generate/edit actions, continuity-reference generation semantics, and source-canvas edit semantics. |
| `ProjectImageGenerationOptions.cs` | Configurable image model defaults, count/reference and provider-output limits, retry/timeout settings, partial image count, and agent wait timeout. |
| `DataUrl.cs` | Shared data URL parse/format helper for mask and provider payloads. |
| `ProjectImageBinary.cs` | Validates PNG/JPEG/WebP raster input and normalizes WebP library output. |
| `ProjectImageResize.cs` | Shared bounded-edge image resize helper for model and preview delivery. |
| `ProjectImageEndpoints.cs` | Minimal API endpoints for scoped project image bytes, masks, and Images, Editor, and Publish chat visual content, with optional image max-edge thumbnails. |

### ImagesChat/

| File | Description |
|------|-------------|
| `IImagesChatService.cs` / `ImagesChatService.cs` | Concept-art and visual-canon adapter over the shared chat engine with automatic/attached visual context, persisted transcript visuals, approved Visual Direction, and terminal free-standing generation/edit workflows. |
| `ImagesChatTools.cs` | Images tools for grounded read-only project/manuscript context, conditional Visual Direction updates, canonical-reference mutations/crops, free-standing generation/editing, reconnectable jobs, and masks; manuscript, page, and cover mutations are absent. |
| `ImagesChatToolContext.cs` | Per-turn Images Chat tool context carrying provider/vision readiness, cancellation and owned image jobs, current tool metadata, visible/model-only images, and mutation signaling. |
| `ImagesChatTurnUpdate.cs` | Streaming update records consumed by `ImagesChatPanel`: text deltas, tool start/argument/completion with visuals, mutation refresh, assistant completion, and turn errors. |
| `ImagesChatTurnRunner.cs` | Background turn runner for Images Chat: executes scoped chat turns outside component lifetime and replays buffered live updates to reopened panels. |

### Composition/

| File | Description |
|------|-------------|
| `CompositionService.cs` | Revision-aware Designed Page aggregate/variant service with setup-normalized active Core/release geometry, authoritative-current stale-write no-ops, reconciled persistent aggregate history, current-scene release materialization, atomic image/fit placement, measured canvas-backed scene validation/staging, exact fingerprints, and geometry-bound generation descriptors. |
| `LayoutImageSizeResolver.cs` | Deterministic GPT Image 2 flexible-size resolver targeting about 1.57 MP, enforcing provider bounds, and defining the shared aspect-compatibility tolerance for returned rasters. |
| `CompositionImageLayout.cs` | Shared proportional image-layout rules for canvas filling, frame coverage inspection, and consistent Pages/Cover/assistant behavior. |
| `ProjectPageSetupService.cs` | Revision-aware project authoring page setup used by Read preview, Core Book, Designed Pages, Figures, and generation targets; geometry changes transactionally reflow the reusable Core cover and active Designed Page layouts. |
| `ChapterPreviewService.cs` | Cached, cancellable Press layout adapter that accepts staged manuscript/style sources, exposes chapter/block page maps, and rasterizes bounded PNG page-inspection images from compact Press paint and text-run traces. |
| `CompositionCanvasPreviewService.cs` | Shared cached direct scene rasterizer for complete Designed Page and cover surfaces, with clean and annotated transient visual modes and no image-library persistence. |
| `CompositionAgentPayloads.cs` | Lossless bounded assistant reads and compact revision-safe patch envelopes for semantic fragments, scene objects, layers, and styles. |
| `CompositionSceneResolver.cs` | Shared deterministic group flattener and PDF/X overlap validator used by export and geometry-target consumers so group transforms, opacity, visibility, locks, and z-order have runtime meaning. |
| `CoverCompositionFactory.cs` | Seeds and reflows shared structured cover scenes across front-only digital and page-count-derived print-wrap geometry while keeping artwork beneath canonical cover copy. |

### Fonts/

| File | Description |
|------|-------------|
| `IProjectFontService.cs` / `ProjectFontService.cs` | Project font catalog/import/delete/face-resolution service combining bundled OFL families with SQLite-backed custom static faces, live-use deletion guards, and confirmed clearing of history-only dependencies. |
| `PublicationBuiltInFonts.cs` | Pinned OFL Lora/Nunito/Roboto Mono family/face catalog and static asset URLs shared by manuscript, composition, cover, EPUB, and Press. |
| `ProjectFontBinary.cs` | Server-side TTF/OTF extension, signature, table-directory, variable-axis, metadata, size, and Skia decode validation. |
| `ProjectFontEndpoints.cs` | Project-scoped imported font-byte endpoint with content type, ETag, and HTTP range support. |

### Publish/

| File | Description |
|------|-------------|
| `PublishModels.cs` | Core/release targets, effective workspace/readiness, revision-aware sparse mutations, publication-section, projection, cover, preparation, and artifact contracts. |
| `PublicationBookService.cs` | Owning Core Book boundary for seeding, metadata/presentation patches with linked system-page copy propagation, consolidated workspace-detail reads, chapter inclusion, page-setup-normalized reusable covers, persistent cover history, revisions, and source-fingerprint invalidation. |
| `IPublicationEditionService.cs` / `PublicationEditionService.cs` | Owning release lifecycle and sparse-override boundary for presets, collision-safe numbered names, chapter inclusion, edition-content-aware cloning, archive/compare/audit, and effective fingerprints. |
| `PublicationSectionService.cs` / `PublicationSectionOrderCodec.cs` | Revision-safe Core/release publication-section ownership and stable sparse release-order serialization, keeping order changes separate from live content inheritance/customization while preserving persistent prose history, prose/designed authoring, inclusion/start side, bindings, anchoring, validation, and lifecycle history cleanup. |
| `PublicationSectionMigrationService.cs` | Protected, journaled startup conversion from historical matter/placement rows into publication sections, plus fail-closed repair of historical system-page semantic-revision drift. |
| `EditionContentService.cs` | Enables/discards release content, resets individual chapters, reports stable-block differences/Core drift, and diagnoses release Designed Page geometry with Editor deep-link targets. |
| `EditionContentMigrationService.cs` | Protected style/typography materialization into edition snapshots and shared Book Text Styles, proof-row removal, effective-projection validation, artifact Legacy marking, and cleanup handoff. |
| `PublicationReleasePresetService.cs` | Centralized safe paperback/hardcover destination, EPUB ebook, and PDF ebook release defaults backed by the internal physical-product registry and immutable digital profiles. |
| `PrintProductRegistry.cs` | Application-owned loader/validator for the shared offline registry plus exact fixed-point spine, submitted/normalized/reported page-count, product-surface, and Generic-template geometry resolution. |
| `PrintProductMigrationService.cs` | Protected v27/v28 cutover from legacy paper/binding/ink fields to exact products/templates and surface scenes, preserving artifact bytes/hashes and validating the transformed database before cleanup. |
| `PublicationReleaseNaming.cs` | Shared profile-derived release defaults and collision-safe numbered-name allocation used by UI and persistence boundaries. |
| `PublicationPreparationService.cs` | Persisted/recoverable one-action Core/release coordinator that maps native render work into monotonic preparation progress, reuses one preflight for packaging, and owns cancellation, blockers, and retained Core warnings. |
| `PublicationDiagnosticPresentationService.cs` | Resolves preparation diagnostics, including semantic text-range references, within the prepared target to plain-language chapter/page, publication-section, or cover locations and GUID-free fallback messages. |
| `PublicationCoreMigrationService.cs` | Guarded v16 Core Book/sparse-release cutover with protected backup, effective-projection and artifact-byte/hash invariants, journaling, and cleanup handoff. |
| `PublicationEditionMigrationService.cs` | Independent v10 backup/hash/journal cutover plus validated, resumable reconciliation of SQLite's split v14 cover-table rebuild and EF history write. |
| `PublicationMigrationLock.cs` | Database-scoped process and crash-releasing file lease shared by edition recovery and Press schema advancement so the v14 rebuild/history window has one migration owner. |
| `PublicationPressMigrationService.cs` | Guarded v15 Press cutover/reconciliation owner with protected backup, atomic marker, integrity and byte/hash invariants, journal evidence, and recovery-shell fallback. |
| `PublicationActorContext.cs` | Scoped UI/assistant actor attribution carried into immutable publication-edition audit entries. |
| `PublishAssistantTools.cs` | Compact Core/release read/patch, metadata-only publication-section creation plus focused prose mutations, bounded project search/image/font and immutable EPUB spine/text inspection, target-aware section variants and staged page operations, edition links, cover operations, terminal unattached image generation, direct previews, validation, preparation, and artifacts; raw manuscript replacement, chapter mutations, and raw profiles are absent. |
| `PublishChatService.cs` / `PublishChatTurnRunner.cs` / `PublishTurnUpdate.cs` | Project-scoped persisted Publish chat orchestration with full outline and protected visible-workspace context, narrated durable work history, shared Editor page/typography/image-design guidance, persisted user-visible and model-visible direct canvas previews, active-turn streaming/reconnection, and Core/release-targeted mutation notices. |
| `PublicationPressRuntime.cs` | Fail-closed exact-manifest resolver for the packaged native renderer, dynamic capabilities, integrity evidence, and empty controlled child environment with no machine-tool fallback. |
| `PublicationRenderService.cs` | Persisted/recoverable queue, metadata-only artifact listings, canonically ordered protocol-v7 image/font staging, bounded native progress ingestion, hash-verified print/Book PDF artifacts, semantic page maps, renderer/registry staleness, and comparison. |
| `PublicationCoverService.cs` | Revisioned format/product-aware cover aggregate with Core-front materialization, independent outside/inside/case/jacket/cloth surface scenes, one persistent history stream across surfaces, exact stock/page-count geometry reflow, diagnostics, and acknowledgement invalidation. |
| `PublicationPackageService.cs` | Versioned registry-driven preflight and exact physical/digital package assembly with reusable validated reports, manifest, upload map, provenance, and legacy-artifact guards. |
| `IPublishService.cs` / `PublishService.cs` | Read/projection/export facade carrying effective Core/release content into Core TXT/Markdown/Reading PDF and release EPUB/PDF/print output with active-compatible composition-variant selection and product-form guards. |
| `PublicationArtifactPreviewService.cs` | Bounded, cached PDF-to-PNG page rendering for Lorekeeper-owned artifact previews using the already packaged PDFium/Skia runtime. |
| `PublicationEpubPreviewService.cs` | Hash-verified, bounded in-memory EPUB parser/cache with safe manifest/spine/navigation reads, preview-only XHTML/SVG/CSS sanitization, fixed-layout metadata, and bounded assistant text extraction. |
| `PublishEndpoints.cs` | Cacheable/range project-scoped immutable publication artifact viewing/download endpoints plus immutable rendered PDF-page previews and CSP-restricted validated EPUB resources. |
| `IPublishExportFormatter.cs` / `PublishExportFormatters.cs` | TXT/Markdown plus semantic EPUB writer for flowing Figures, non-empty mixed-layout spine segmentation, canvas-faithful real-text Designed Pages/covers, reading order, captions, and alternatives. |

### Graph/

| File | Description |
|------|-------------|
| `IProjectGraphService.cs` | Graph UI application service contract for project-wide snapshots plus guarded node, parent, type, and relationship mutations. |
| `ProjectGraphModels.cs` | DTOs and request records used by the Graph tab service/component boundary, including low-priority auto-link flags. |
| `ProjectGraphService.cs` | Graph UI facade over repositories and domain services; exports whole-project graph snapshots and routes structural edits plus indexable graph relationship/parent mutations through owning domain services while keeping auto mention links read-only. |
| `IGraphAutoLinkService.cs` / `GraphAutoLinkService.cs` | Exact-label/alias auto linker that persists low-priority read-only `AutoMention` edges from containing source nodes to mentioned graph entities. |

### EditorChat/

| File | Description |
|------|-------------|
| `IEditorChatService.cs` | Project-wide editor chat contract plus per-turn context for compact tools, opaque one-use manuscript previews, composition stages, persisted/model-visible visuals, and generation jobs. |
| `EditorChatService.cs` | Editor adapter using current semantic Figure/Designed Page prompt guidance, persisted turn context and per-document assistant history batching, shape-safe post-tool mutation projection, complete composition mutation identities, Review edits, contests, cancellation-safe worker progress, and image-job progress. |
| `EditorChatOptions.cs` | Configuration for editor-chat-specific paginated chapter reads and prose-only revision worker concurrency/iteration limits. |
| `EditorChatTools.cs` | Editor tools for grounded reads, protected current-chapter/current-page history, shared outline/entity mutations, Press page preview plus direct complete-canvas inspection, compact font/style application, manuscript preview/apply, focused Figures/Designed Pages, composition, exact-target generation, canonical visuals/crops, Book Brief updates, revision agents, and Contest preparation. |
| `EditorManuscriptPreviewService.cs` | Turn-local manuscript preview/apply protocol: validates once, returns compact opaque IDs, rejects stale/reused previews, and persists or review-stages the exact projected document. |
| `EditorChatChangeStagingContext.cs` | Editor chat staging helper for chapter-body edits; creates pending `AiChange` rows owned by the editor transcript when Review edits is enabled. |
| `EditorChatTurnUpdate.cs` | `[JsonDerivedType]`-decorated streaming update records consumed by `EditorChatPanel`: text deltas, tool start/argument/end updates with visuals, image-generation progress, pending changes, contest progress/raw JSON, mutation refresh, assistant completion, and turn errors. |
| `EditorChatTurnRunner.cs` | Background turn runner for Editor Chat: preserves active turns across tab changes while leaving explicit Stop as the cancellation path. |
| `EditorContestModels.cs` | DTOs and helper records for Contest Mode settings, start requests, captured chat-context snapshots, model responses, mutation JSON, and streaming contest status/raw-response updates. |
| `IEditorContestService.cs` / `EditorContestService.cs` | Contest Mode application service: persists project settings, starts terminal contest batches, runs selected models without tools, streams raw Candidate JSON, validates JSON chapter-body mutations, builds proposed bodies, and resolves inline candidate review decisions. |
| `EditorRevisionAgentModels.cs` | DTOs for prose-only revision assignments, compact coordinator run results, full job/session details, and transcript projections used by tools and UI. |
| `IEditorRevisionAgentService.cs` / `EditorRevisionAgentService.cs` | Same-turn revision-agent orchestrator: validates chapter assignments, persists full jobs/sessions, runs bounded-parallel workers, and returns only compact status/summary results to the coordinator. |
| `EditorRevisionAgentProcessor.cs` | Per-session worker with paginated grounding and filtered source reads whose terminal tool applies revision-aware semantic manuscript operations, enforces model-facing style/scene-break format guidance, and persists transcript/tool history. |
| `IEditorRevisionJobNotifier.cs` | In-process pub/sub for revision job/session progress updates, matching other local background workflow notifiers. |

### Outline/

| File | Description |
|------|-------------|
| `IActService.cs` / `ActService.cs` | Act CRUD facade. `CreateAsync` auto-orders to the end. `DeleteAsync` lets the FK demote owned chapters to Unassigned (`OnDelete.SetNull`). Touches `Project.UpdatedAt`, keeps Act graph nodes/structural edges synchronized, and updates targeted act context vectors on mutations. |
| `IOutlineCollaborationService.cs` / `OutlineCollaborationService.cs` | Structure-focused Outline adapter whose prompt includes Project Guidance, Book Brief, shared current-outline/entity/source context, standalone canonical-state prose rules, and concise structure-only genre guidance while maintaining staged outline/canon changes. |
| `IOutlineWorkingContextBuilder.cs` / `OutlineWorkingContextBuilder.cs` | Builds the one automatic Outline working context: full outline/beats, chapter/beat entity associations, per-category origin counts/samples, and canonical versus evidentiary source inventory. |
| `OutlineCollaborationTools.cs` | Shared structural/canon tool catalog plus the restricted Outline surface: Book Brief and explicit canon-source selection, full outline, paginated entity/source discovery and reads, chapter-first `RelevantTo` links, facts, canonical references, and explicit appearance generation. Editor imports an intentional structural subset and owns manuscript/composition tools. |
| `BookFormatGuidanceService.cs` | Compact/paginated fiction, nonfiction, picture-book, illustrated-book, poetry, hybrid, audience, extent, accessibility, and constraint recommendations with explicit structure-only and publication-aware scopes. |
| `OutlineMutationPayloads.cs` | Shared compact entity/endpoint envelopes used by direct and staged outline mutation tools without serializing full knowledge or relationship traversals. |
| `OutlineChatTurnRunner.cs` | Background turn runner for Outline chat: owns active turn cancellation/subscription outside the Blazor component lifetime. |
| `IAiChangeApprovalService.cs` / `AiChangeApprovalService.cs` | Applies or rejects queued AI changes from outline/editor/research chat using semantic manuscript concurrency checks; reconnects approved Editor changes to their originating turn history batch, enforces dependency cascading, preserves unresolved conflicts for review, and writes hidden corrections to the owning transcript. |
| `AiChangeReviewDrafts.cs` | Typed helper for persisted pending-change review drafts: reads editable text fields, updates draft payload JSON, validates draft metadata, and resolves effective after-payloads. |
| `OutlineToolStagingContext.cs` | Approval-mode working snapshot that overlays staged edits/reorders/links and mirrors direct-tool compact mutation and paginated read envelopes. |
| `OutlineChangePayloads.cs` | JSON payload records shared by staging and approval application for acts, chapters, entities, links, and reorders. |
| `AiChangeReviewDiffBuilder.cs` | Builds single-change and grouped review diff models from pending AI changes, including manuscript structure/visual summaries, line-review eligibility, fuzzy line alignment, and intraline highlights. |
| `IEntityService.cs` / `EntityService.cs` | Single contract for every story-graph entity (Characters, Locations, Events/beats, ...). Entities persist as `GraphNode`s via `IGraphStore`; create/update/delete, parent moves, and relationship mutations refresh affected context/search indexes and auto mentions. `ListLinksAsync` returns manual links before low-priority read-only auto links. |
| `IEntityTypeService.cs` / `EntityTypeService.cs` | Lightweight graph type registry facade. Seeds structural/default types (`Project`, `Act`, `Chapter`, `ProjectFact`, `Event`, `Character`, `Location`), discovers arbitrary node types, and creates custom non-structural types for the side panel. |
| `IProjectFactService.cs` / `ProjectFactService.cs` | Project-level graph fact facade. Stores one `ProjectFact` graph node per key/value pair, ensures a Project → ProjectFact `HasChild` edge, enforces case-insensitive key upserts, touches `Project.UpdatedAt`, and projects linked graph entities for UI/prompt display. |
| `IOutlineGraphSync.cs` / `OutlineGraphSync.cs` | Synchronizes the EF outline spine into graph nodes and `HasChild` edges: Project → Acts / unassigned Chapters, Act → Chapters, Chapter → Events. Used by project/act/chapter services and startup repair. |
| `OutlineTurnUpdate.cs` | `[JsonDerivedType]`-decorated abstract record for streaming chat updates: text deltas, tool-call start/argument/completion updates, assistant completion, outline mutation refresh, and turn errors. |

### Chapters/

| File | Description |
|------|-------------|
| `IChapterService.cs` / `ChapterService.cs` | Chapter CRUD plus target-aware `IManuscriptService`: Core/release copy-on-write chapters, persistent independent histories, reversible Designed Page detachment/restoration, composition cloning, revision writes, search/vector projection, and graph/auto-mention side effects. |

### Manuscripts/

| File | Description |
|------|-------------|
| `ManuscriptModels.cs` | Manuscript-v4 document/block/inline/mark model with sparse direct paragraph typography/presentation, geometry-neutral Figure presentation/accessibility, Designed Page references, semantic operations, snapshots, and revision conflicts. |
| `ManuscriptSemanticRoles.cs` | Safe semantic-role identifier validation plus deterministic normalization for manuscript-v1 migration and v8 import. |
| `ManuscriptCodec.cs` | Plain-text normalization/projection, deterministic migration IDs, validation, serialization, hashing, and stable-ID reparsing. |
| `ManuscriptRangeResolver.cs` | Validates non-overlapping UTF-16 semantic text ranges, rejects surrogate splits, resolves frame content exactly once, and identifies unplaced composition content. |
| `ChapterSemanticProjectionService.cs` | Expands Designed Page semantic fragments into chapter reading order exactly once for search, context, indexing, and plain-text projections without duplicating storage. |
| `ManuscriptOperations.cs` / `ManuscriptOperationInput.cs` | Transactional insert/replace/delete/move/split/merge/type/style/mark/presentation transformations with GUID-format-independent stable-ID lookup, explicit operation guidance, and assistant-safe DTO conversion. |
| `ManuscriptInspection.cs` | Shared schema validation, normalization diagnostics, and structural block search used by Editor and revision-worker assistants. |
| `ManuscriptSchemaUpgrade.cs` | Strict lossless older-to-v4 document and nested historical-payload upgrader used by startup migration and isolated import adapters. |
| `ManuscriptStyleService.cs` | Revision-checked project-wide Book Text Style ownership, built-in/imported font validation, immutable generated semantic keys, Core/release usage counts, and usage-safe deletion. |
| `EditorContentTarget.cs` | Protected Editor target value identifying Core Book or one enabled publication release. |
| `ManuscriptStyleTemplateExtractor.cs` | Shared paragraph-style capture and compact style-application policy used by the manual editor and Editor assistant. |
| `IManuscriptService.cs` | Canonical target-aware chapter manuscript read/replace/operation and persistent-history contract plus explicit edition Designed Page snapshot creation. |
| `ManuscriptMigrationService.cs` | Cross-process-serialized, WAL-safe Online Backup API migration/recovery owner for resumable schema/data transformation, atomic validation journaling, retention, confirmed restore, and non-downgrading later-migration orchestration. |
| `VisualCompositionMigrationService.cs` | Guarded protected-backup v3/composition cutover and geometry-policy rekey that preserve semantic IDs/text, original Picture Page geometry, visual styles, Figures/pages/covers/pending Outline state, artifacts/hashes, and foreign keys before cleanup. |
| `AuthoringPageMigrationService.cs` | Guarded protected-backup v4 authoring cutover that materializes staged Picture Page scenes into active exact-geometry variants, seeds page setup, normalizes Figure/scene fit and crop state, preserves artifacts/hashes, and validates scene hashes, ownership, counts, references, and foreign keys. |

### wwwroot/

| File | Description |
|------|-------------|
| `app.css` | App-wide Lorekeeper design tokens and shared editorial treatments for typography, controls, cards, status, empty states, navigation, and Bootstrap primitives. |
| `text-select-cursor.svg` | High-contrast outlined I-beam cursor used by editable text surfaces so the pointer remains visible on light and dark backgrounds. |
| `js/autosizeTextareas.js` | Small shared JS module that attaches to `textarea[data-autosize]`, grows each textarea to its `scrollHeight`, refreshes on input/change and width changes, and prevents nested textarea scrollbars. |
| `js/composition-workspace.js` | Shared pointer-capture/measured-stage and source-raster aspect bridge for visual canvases plus live rendered-frame overflow observation, Designed Page contenteditable focus, text-selection offsets, and formatting-selection restoration. |
| `js/semantic-editor.bundle.js` / `semantic-editor.NOTICES.txt` | Deterministic ProseMirror ESM bundle built from `tools/semantic-editor`, plus the shipped runtime dependency/license notice. |
| `js/fileDownloads.js` | Browser download helper used by Import / Export and Publish to save generated graph JSON and publish export files. |
| `fonts/` | Offline pinned OFL publication families, per-family licenses, and source/revision documentation. |
| `branding/` | Lorekeeper vector master plus generated PNG/ICO variants used by the app shell, browser metadata, and Electron release packaging. |
| `site.webmanifest` | Browser install metadata and references to the generated Lorekeeper app icons. |
| `lib/bootstrap/` | Vendored Bootstrap distribution. |
| `lib/vis-network/` | Vendored `vis-network` browser graph renderer assets and license files used by the Graph tab. |

