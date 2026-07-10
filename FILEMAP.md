# Lorekeeper — File Map

> **Auto-maintained reference.** Agents and contributors should update this file whenever files are added, removed, or significantly refactored.  
> Read this file at the start of every session to understand the codebase layout.

---

## Root

| File | Description |
|------|-------------|
| `VISION.md` | High-level project vision and success criteria. |
| `README.md` | Project readme with web/Electron development, Windows packaging and install guidance, and local-data notes. |
| `FILEMAP.md` | This file — concise map of every source file. |
| `Lorekeeper.sln` | Solution file containing the `Lorekeeper` project. |
| `global.json` | Pins the .NET SDK version (`rollForward: latestFeature`). |
| `.editorconfig` | C#/Razor formatting and naming rules. |
| `.gitignore` | Standard .NET ignore patterns plus Lorekeeper local SQLite/temp data; publish output is scoped to the repo-root `/publish/` folder so source folders named `Publish` remain trackable. |
| `.vscode/launch.json` | VS Code debug configurations; default F5 entry launches the Electron desktop shell, with a secondary web-hosted profile. |
| `.vscode/tasks.json` | VS Code build task used by debug launch configurations. |
| `.github/copilot-instructions.md` | Project guidelines for AI assistants. |
| `scripts/build-windows-release.ps1` | Clean Windows x64 release builder: validates tooling/version, audits NuGet plus shipped npm/Electron dependencies, rebuilds isolated staging/output, verifies artifacts, and writes GitHub Release checksums. |

## Lorekeeper/ — Blazor Web App (Interactive Server)

| File | Description |
|------|-------------|
| `Lorekeeper.csproj` | Project file: `net10.0`, nullable + implicit usings, warnings-as-errors, versioned Electron/Electron Builder pins, and app dependencies including EF Core SQLite, Microsoft.Extensions.AI(.OpenAI), OpenAI, sqlite-vec, tokenizers, SkiaSharp, and ingest packages. |
| `Program.cs` | Host setup, hardened optional Electron renderer binding, Blazor Interactive Server hub sizing, DI for persistence/knowledge/LLM/search/token/ingest/research/import-export/images/chapter-visuals/publish/writing/editor-chat/contest/revision-agent services, startup migration/index repair, and image/publish endpoints. |
| `appsettings.json` / `appsettings.Development.json` | Configuration: `Desktop:*` including packaged per-user data placement, `Auth:Codex:*`, `ConnectionStrings:DefaultConnection`, `Persistence:Provider`, `Blazor:*`, `Ingest:Sectioning:*`, `Research:Web:*`, `Embeddings:*`, `Agents:*`. |
| `Properties/launchSettings.json` | Local launch profiles for Electron, HTTP, and HTTPS; HTTP remains pinned to `localhost:1455` for Codex OAuth redirect. |
| `Properties/electron-builder.json` | Electron.NET/electron-builder packaging targets, app metadata, and payload exclusions for Windows, Linux, and macOS desktop artifacts. |
| `Properties/PublishProfiles/*.pubxml` | Runtime-specific self-contained publish profiles used by Electron.NET packaging (`win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`); Windows isolates staging from final artifacts to prevent recursive packaging. |

### Components/

| File | Description |
|------|-------------|
| `App.razor` | Root component: `<html>` shell, head outlet, scripts. |
| `Routes.razor` | `<Router>` wiring `MainLayout` and `NotFound`. |
| `_Imports.razor` | Shared `@using` directives for all components. |
| `EntityKnowledgeView.razor` (+ `.razor.css`) | Shared read-only entity knowledge renderer for structured wiki data and source-backed canon markdown used by graph, outline, and context entity detail surfaces. |
| `EntityVisualExamples.razor` (+ `.razor.css`) | Reusable ordered entity visual gallery/editor with library attach, upload, non-destructive cropping, labels, ordering, shared full-size viewing, and detach. |
| `ImageCropModal.razor` (+ `.razor.css`, `.razor.js`) | Shared freeform rectangular crop modal with zoom/pan, canvas selection and preview, crop-specific metadata, and optional entity targets. |
| `ImageViewerModal.razor` (+ `.razor.css`) | App-wide full-size image viewer for project assets, chat visuals, source candidates, and publish images, with shared metadata, dismissal, and optional actions. |
| `ImageEntityAssociations.razor` (+ `.razor.css`) | Image-side attached-entity chips and association editor used by the Images workspace. |
| `EntityVisualTargetPicker.razor` (+ `.razor.css`) | Reusable multi-entity target picker for image generation and editing. |

### Components/Chat/

| File | Description |
|------|-------------|
| `ChatModels.cs` | Shared chat UI view models for persisted/live messages, text/image parts, duration-aware tool-call chips with visual strips, generic progress rows/previews, and transcript token-count helpers. |
| `ChatTranscriptTokenCounter.cs` | Shared transcript token-count adapter for `ChatSurface` panels: projects domain messages into a common token-count shape, includes pending/live turns, and formats exact/estimated count labels. |
| `ChatSurface.razor` (+ `.razor.css`, `.razor.js`) | Reusable chat shell for transcript/live rendering, grouped adjacent tool-call chips, image visual strips with shared full-size viewing, composer controls, scrolling, and textarea autosize behavior. |
| `ChatToolChipView.razor` (+ `.razor.css`) | Reusable expandable tool-call card that shows streamed arguments/results/errors, generic progress rows/previews, and Editor Revision worker transcript links. |

### ChatTurns/

| File | Description |
|------|-------------|
| `ChatTurnRuntime.cs` | Shared singleton-friendly active-turn runtime for chat surfaces: one running turn per project, buffered subscriber replay, and explicit cancellation separate from component disposal. |

### Components/Layout/

| File | Description |
|------|-------------|
| `MainLayout.razor` / `.css` | Viewport-locked application shell with the slim Lorekeeper top bar, route-aware page/workspace padding, and global error notice. |
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
| `EditorPage.razor` | Editor tab routes (`/projects/{Slug}/editor` and `/projects/{Slug}/editor/{ChapterId:guid}`). Wraps `ProjectLayout` + `EditorContent`. |
| `EditorContent.razor` (+ `.razor.css`, `.razor.js`) | Context-aware chapter editor with per-project resizable/collapsible Chat and Memory columns shared across Edit/Read/Layout/Review modes, chapter visual controls, image-library actions, and inline AI/Contest review. Persists/reindexes body edits, remembers the selected chapter, and refreshes editor/context surfaces after AI turns or approved changes. |
| `PagedChapterViewer.razor` (+ `.razor.css`, `.razor.js`) | Simulated page viewer/editor for Prose, IllustratedProse, and PicturePage chapters, including paginated spreads, anchored illustrations, container-fitted PicturePage layouts, and wrapping drag-resize/layer/text controls shared by Read/Layout rendering. |
| `ProjectImagePickerModal.razor` (+ `.razor.css`) | Editor image-library modal for selecting current project images and adding them either to the active chapter layout or explicit chapter context. |
| `EditorChatPanel.razor` (+ `.razor.css`) | Editor chat adapter over `ChatSurface`: loads transcript/tool visuals, streams text/tool/image-generation/contest updates, routes active-chapter body changes and Contest Mode results to inline Review mode, and keeps pending-change review. |
| `ContextItemDetailModal.razor` (+ `.razor.css`) | Shared editor context detail modal for recommendation and Context Feed items; loads entities, chapters, acts, ingest sources/chunks, and supports Context Feed project-guidance/entity edits. |
| `RecommendedContextPanel.razor` (+ `.razor.css`) | Editor right-column context recommender: shows semantic/manual/graph-proximity recommendations for entities plus structural references, and adds them to the active chapter's persisted context working set. |
| `ContextFeedPanel.razor` (+ `.razor.css`) | Editable Assistant Memory list for project guidance, current chapter, outline, facts, writing samples, selected entities, and structural references; opens `ContextItemDetailModal` and persists include/exclude choices via `IEditorContextService`. |
| `GraphPage.razor` | Graph tab at `/projects/{Slug}/graph`; wraps the project shell and hosts the interactive graph workspace. |
| `GraphContent.razor` (+ `.razor.css`, `.razor.js`) | Obsidian-inspired full-project graph workspace: loads graph snapshots, filters/searches nodes, hides source provenance and auto mention links by default, bridges to `vis-network`, and coordinates graph refreshes. |
| `GraphDetailsPanel.razor` (+ `.razor.css`) | Selected-node read-only graph overview side panel with node metadata, properties, knowledge/canon details, adjacent relationships honoring the auto-link toggle, node navigation, and edit entrypoints. |
| `GraphEditModal.razor` (+ `.razor.css`) | Full graph editing modal for node create/edit/delete, managed parent assignment, and custom relationship create/edit/delete while managed/provenance/auto links remain read-only. |
| `IngestPage.razor` | Ingest tab at `/projects/{Slug}/ingest`; wraps `ProjectLayout` and hosts `Ingest.IngestContent`. |
| `ResearchPage.razor` | Research tab at `/projects/{Slug}/research`; wraps `ProjectLayout` and hosts `Research.ResearchContent` when an active search provider is configured. |
| `ImportExportPage.razor` | Import / Export tab at `/projects/{Slug}/import-export`; wraps `ProjectLayout` and hosts `ImportExport.ImportExportContent`. |
| `ImagesPage.razor` | Images tab at `/projects/{Slug}/images`; wraps `ProjectLayout` and hosts `Images.ImagesContent`. |
| `PublishPage.razor` | Publish tab at `/projects/{Slug}/publish`; wraps `ProjectLayout` and hosts `Publish.PublishContent`. |
| `ManuscriptPrintPage.razor` (+ `.razor.css`, `.razor.js`) | Scrollable embedded preview and Print/PDF document at `/projects/{Slug}/manuscript/print`; contains unrotated or sideways spreads on portrait Letter pages, supports split leaves, and waits for decoded assets before printing. |
| `OutlinePage.razor` | Outline tab route; wraps `ProjectLayout` + `Outline.OutlineContent`. |
| `WritingSamplePage.razor` | Writing Sample tab at `/projects/{Slug}/writing-sample`; wraps `ProjectLayout` + `WritingSample.WritingSampleContent`. |

### Components/Pages/Projects/Ingest/

| File | Description |
|------|-------------|
| `IngestContent.razor` (+ `.razor.css`) | Functional Ingest tab workspace: creates text/EPUB/PDF/manual-webpage jobs, manages resume/restart/delete controls, shows live ingest LLM progress, diagnostics, chunk/finalization reports, and artifact options. |

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
| `ImagesContent.razor` (+ `.razor.css`, `.razor.js`) | Three-pane image workspace with Images Chat, queued generation/edit job cards, attach-to-chat/entity actions, non-destructive library cropping, shared full-size viewing, manual queued generation, and mask edit modal. |
| `ImagesChatPanel.razor` (+ `.razor.css`) | Images Chat adapter over `ChatSurface`: loads transcript, streams text/tool updates, manages attached image context chips with shared viewing, renders image visual strips, and refreshes the image grid after mutations. |

### Components/Pages/Projects/Publish/

| File | Description |
|------|-------------|
| `PublishContent.razor` (+ `.razor.css`) | Responsive Publish workspace for autosaved metadata, cover-aware title pages, independent PDF/EPUB spread presentation, cover/outline/placement choices, near-fullscreen modal preview/print, and TXT/Markdown/EPUB exports. |

### Components/Pages/Projects/Outline/

| File | Description |
|------|-------------|
| `OutlineContent.razor` (+ `.razor.css`) | Top-level Outline tab orchestrator. Three-pane CSS-grid layout (chat | outline tree | facts+entities side column) inside a fixed-height grid; each direct column wrapper is an independent `overflow-y: auto` scroll container, with horizontal overflow rather than responsive stacking when space is tight. Bumps a `_refreshSignal` int on every reload that child panels watch to re-read after tool turns. Re-fetches the project from `IProjectRepository` on every reload. |
| `ProjectFactsPanel.razor` (+ `.razor.css`) | Editable project facts block surfaced at the top of the right side column. Reads/writes `ProjectFact` graph nodes via `IProjectFactService`, keeps the compact collapsible key/value UX, supports add/edit/delete, shows linked graph entities, and autosizes fact textareas via `wwwroot/js/autosizeTextareas.js`. |
| `IngestSourcesPanel.razor` (+ `.razor.css`) | Read-only Outline side panel for structural ingest Source → SourceChunk → SourceBlock graph nodes, showing chunk/block locators and linked entities. |
| `OutlineTree.razor` (+ `.razor.css`) | Hierarchical Acts → Chapters tree. Acts are collapsible, drag-reorderable groups with inline-editable title/synopsis and `+ Chapter` / Delete (chapters fall back to Unassigned via `OnDelete.SetNull`). Act/chapter synopsis textareas autosize to their content via `wwwroot/js/autosizeTextareas.js`. Chapters are inline-editable rows with a beats-toggle caret + count badge (renders `ChapterBeats` inline when expanded), stale/failed vector-index badge, drag-reorder within their act bucket, an act-picker `<select>` for cross-act moves, Open link, and delete-with-confirm. Re-fetches per-chapter beat counts via `IEntityService.CountChildrenAsync` whenever `RefreshSignal` bumps. |
| `EntitiesPanel.razor` (+ `.razor.css`) | Registry-driven project-scoped entities side panel. Lists non-structural graph types from `IEntityTypeService`, supports `+ Type`, per-type `+ Add`, clickable entity rows, and a Bootstrap-style modal with editable name/properties plus read-only adjacent graph links via `IEntityService.ListLinksAsync`. Save computes property diffs and calls `UpdateAsync(propertiesToSet, propertiesToRemove)`; modal also exposes Delete-with-confirm. Re-reads on `RefreshSignal` bumps. |
| `ChapterBeats.razor` (+ `.razor.css`) | Inline beats expander rendered inside each chapter row. Loads beats via `IEntityService.ListAsync(projectId, "Event", chapterId)` ordered by `Order`, supports inline-edit (name + autosizing summary textarea), drag-reorder (calls `ReorderAsync`), `+ Add beat` and delete-with-confirm. Re-reads on `RefreshSignal` bumps. |
| `OutlineChatPanel.razor` (+ `.razor.css`) | Outline chat adapter over `ChatSurface`; keeps transcripts visible, disables LLM controls without a working provider, streams text/tool updates, and opens pending-change review. |
| `PendingAiChangesModal.razor` (+ `.razor.css`) | Durable AI change review modal reused by Outline Chat and Editor Chat: groups queued tool changes by resource, shows dependency/cascade warnings, renders inline or side-by-side PR-style diffs with JSON fallback, and supports Keep/Reject per group or batch with a rejection note. |

### Components/Pages/Projects/WritingSample/

| File | Description |
|------|-------------|
| `WritingSampleContent.razor` (+ `.razor.css`) | Top-level Writing Sample tab orchestrator. Three-pane CSS-grid layout (coach chat | sample editor | sample list), loads project-scoped samples, autosaves the active sample body through the shared editor JS bridge, supports title edits, and flushes pending edits before sample switches or coach sends. |
| `WritingCoachPanel.razor` (+ `.razor.css`) | Writing Coach adapter over `ChatSurface`; keeps transcripts visible, disables LLM controls without a working provider, and streams project-level coaching/read-only tool cards. |
| `WritingSampleListPanel.razor` (+ `.razor.css`) | Right-side sample manager with clickable active rows, excerpts, updated timestamps, `New`, and delete-with-confirm. |

### Components/Pages/Settings/

| File | Description |
|------|-------------|
| `Providers.razor` (+ `.razor.css`) | Connection-card LLM configuration UI: Codex OAuth, grouped credential connections and nested models, connection editing/deletion, visible chat/vision readiness, working-only defaults, and expandable add flows. |
| `Embeddings.razor` (+ `.razor.css`) | Active embedding summary and configuration workflow: test-before-save, dimensions, rebuild confirmation, and an explicit semantic-feature danger zone. |
| `SearchProviders.razor` (+ `.razor.css`) | Card-based SerpApi/Brave configuration for Research: add/edit/test/activate providers, API keys, and confirmed deletion. |

### Models/

| File | Description |
|------|-------------|
| `AuthType.cs` | Enum: None, ApiKey, OAuth. |
| `LlmProvider.cs` | EF entity for an LLM endpoint/model row. Supports parent/child credential sharing plus persisted chat- and vision-readiness test snapshots. |
| `EmbeddingConfiguration.cs` | Singleton EF entity for the active embedding setup: top-level provider connection, embedding API kind, model id, dimensions, last-tested snapshot, and timestamps. |
| `OAuthToken.cs` | EF entity holding access/refresh tokens for an OAuth-backed provider. |
| `Project.cs` | EF entity scoping all narrative data. Stable `Slug` for URLs; owns project settings and child navigation collections including conversations, image chats/attachments/jobs/masks, contests, revision jobs, writing samples, import jobs, publish profiles/assets/selections/placements, and graph rows. |
| `Act.cs` | EF entity for a top-level outline grouping (Title/Synopsis/Order) under a `Project`. Cascade-deleted with the project. Owned chapters survive act deletion (FK `OnDelete.SetNull`). |
| `Chapter.cs` | EF entity for a chapter (Title/Body/Synopsis/Order) under a `Project`, optionally assigned to an `Act`; stores visual mode, page layout kind, and visual layout JSON for illustrated prose/picture pages. Tracks vector-index state and exposes `VectorSourceId`. |
| `ChapterVisualMode.cs` | Enums for chapter visual modes, page layout kinds, and reusable image/text layout choices such as image fit, alignment, anchor position, and text vertical alignment. |
| `ChapterVisualLayouts.cs` | Serializable layout records and typography enums for IllustratedProse anchored image blocks and PicturePage freeform image/text elements. |
| `EditorContextPreference.cs` | EF entity for per-chapter Context Feed include/exclude preferences keyed by context item kind + stable item key. |
| `EditorConversation.cs` | EF entity — one persistent multi-turn editor chat per `Project` (unique on `ProjectId`). Owns ordered `EditorMessage`s; cascade-deleted with the project. |
| `EditorMessage.cs` | EF entity for a single row in an `EditorConversation`: monotonic `Order`, role (`System`/`User`/`Assistant`/`Tool`), text content, assistant tool-call JSON, tool result metadata, status, optional error, and creation timestamp. |
| `EditorMessageVisual.cs` | EF entity for Editor Chat visual attachments shown as thumbnails, including project-image references or optional stored bytes. |
| `EditorRevisionJob.cs` | EF entity for one prose-only background revision job spawned by Editor Chat; owns per-chapter worker sessions and parent tool-call metadata. |
| `EditorRevisionSession.cs` | EF entity for one chapter worker session: assignment, original body snapshot, provider/model, chapter-body edit payload, status, timing, and errors. |
| `EditorRevisionMessage.cs` | EF entity for persisted worker transcript rows, including assistant tool-call manifests and read-only tool result rows. |
| `OutlineConversation.cs` | EF entity — one persistent multi-turn collaborative chat per `Project` (unique on `ProjectId`). Owns ordered `OutlineMessage`s; cascade-deleted with the project. |
| `OutlineMessage.cs` | EF entity for a single chat row in an `OutlineConversation`: monotonic `Order`, `OutlineMessageRole` (System/User/Assistant/Tool), text `Content`, JSON `ToolCallsJson` for assistant function-calls, `ToolCallId` + `ToolName` for tool results, `OutlineMessageStatus` (Pending/Completed/Failed/Cancelled), optional `ErrorMessage`. |
| `WritingSample.cs` | EF entity for a project-scoped prose sample used as a future style reference. Stores title/body plus created/updated timestamps. |
| `WritingCoachConversation.cs` | EF entity — one resettable Writing Coach transcript per `Project` (unique on `ProjectId`). Owns ordered `WritingCoachMessage`s; cascade-deleted with the project. |
| `WritingCoachMessage.cs` | EF entity for a single Writing Coach chat row with monotonic `Order`, role (`System`/`User`/`Assistant`/`Tool`), text content, assistant `ToolCallsJson`, tool result metadata, status, optional error, and creation timestamp. |
| `SearchProvider.cs` | EF entity for configured web search providers used by Research Mode. Supports SerpApi and Brave in v1, stores API key/config JSON, and tracks the single active provider. |
| `ResearchConversation.cs` | EF entity — one persistent project research chat per `Project` (unique on `ProjectId`). Owns ordered `ResearchMessage`s; cascade-deleted with the project. |
| `ResearchMessage.cs` | EF entity for a single Research chat row with monotonic `Order`, role (`System`/`User`/`Assistant`/`Tool`), text content, assistant tool-call JSON, tool result metadata, status, optional error, and creation timestamp. |
| `AiChangeBatch.cs` | EF entity grouping AI-proposed tool mutations from one assistant turn while they await approval/resolution. Tracks whether the owning transcript is Outline, Editor, or Research chat. |
| `AiChange.cs` | EF entity for one queued AI tool mutation: tool metadata, before/after/result JSON, dependency metadata, status, rejection/error notes, timestamps. |
| `ContestBatch.cs` | EF entity for one Editor Contest Mode run: captured turn/context snapshot, target chapter/body snapshot, operation metadata, status, and model candidates. |
| `ContestCandidate.cs` | EF entity for one model's contest proposal: provider/model labels, validated mutation JSON, proposed chapter body, raw response, status, timing, and errors. |
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
| `PublishProfile.cs` | EF entity and enums for one saved publish profile per project: metadata, matter, title-page behavior, independent PDF/EPUB spread modes, prose pagination, and the optional Picture Page cover. |
| `PublishAsset.cs` | EF entity for uploaded/generated/edited/cropped project images with bytes, crop lineage/coordinates, alt text, prompt/source metadata, masks, and placement navigation. |
| `EntityVisualExample.cs` | Ordered labeled many-to-many link between an eligible graph entity and project image, with origin and source provenance. |
| `SourceVisualCandidate.cs` | Cached normalized Research/Ingest image bytes and artifact/web provenance before project-library promotion. |
| `ProjectImageConversation.cs` | EF entity for the separate project-scoped Images Chat transcript. |
| `ProjectImageChatAttachment.cs` | EF entity for project image assets explicitly attached as visible Images Chat context chips. |
| `ProjectImageMessage.cs` | EF entity for Images Chat messages with assistant tool-call manifests, tool result metadata, status, and errors. |
| `ProjectImageMessageVisual.cs` | EF entity for Images Chat visual attachments, including project-image references or optional stored bytes. |
| `ProjectImageGenerationJob.cs` | EF entity for queued/running/final image jobs with references, entity visual targets/inheritance, outputs, progress, and diagnostics. |
| `ProjectImageMask.cs` | EF entity for validated PNG masks tied to source project images, using transparent pixels as editable regions. |
| `PublishOutlineSelection.cs` | EF entity for per-project act/chapter publish inclusion flags; act selection controls the act page while chapters remain independently selectable. |
| `PublishImagePlacement.cs` | EF entity for cover-independent interior image placements before/after acts or chapters and chapter openings/endings, with captions and ordering. |
| `GraphNode.cs` | Generic graph node: `(ProjectId, NodeType, Key)` unique, JSON properties bag. Cascade-deleted with its `Project`. |
| `GraphEdge.cs` | Directed edge between graph nodes with type, JSON properties, optional relationship-specific `SortOrder`, and timestamps. |
| `GraphEntityType.cs` | Lightweight project-scoped graph type registry entry for UI/LLM labels/defaults. Descriptive rather than restrictive; arbitrary node types remain valid. |

### Persistence/

| File | Description |
|------|-------------|
| `AppDbContext.cs` | EF Core context for projects, provider/embedding/search settings, outline/editor/writing/research chat, editor chat visuals, editor revision jobs, writing samples, graph, editor context preferences, AI change approval, ingest/import queues, webpage candidates, publish profiles/assets/layouts, and chapter visual-mode fields. JSON converter shared by graph property bags; configures relationships/indexes and retries transient SQLite lock save failures. |
| `PersistenceServiceCollectionExtensions.cs` | `AddLorekeeperPersistence` switch on `Persistence:Provider` (SQLite today; Postgres slot for future); applies shared SQLite timeout settings. |
| `SqliteConnectionSettings.cs` | Shared SQLite connection-string and startup PRAGMA settings, including packaged per-user database-path resolution, busy timeout, WAL journal mode, and normal synchronous mode. |
| `Migrations/` | EF Core migrations through `AddPublishPageOptions`, including entity-image links, visual caches/jobs/crops, the Picture Page cover relationship, and saved title/PDF/EPUB page-presentation modes. |

### Persistence/Repositories/

| File | Description |
|------|-------------|
| `ILlmProviderRepository.cs` / `LlmProviderRepository.cs` | CRUD + atomic `SetDefaultAsync` for `LlmProvider`. |
| `IEmbeddingConfigurationRepository.cs` / `EmbeddingConfigurationRepository.cs` | Persistence for the singleton active embedding configuration, eager-loading its selected provider connection. |
| `IOAuthTokenRepository.cs` / `OAuthTokenRepository.cs` | Latest/valid token lookup + replace-for-provider. |
| `IProjectRepository.cs` / `ProjectRepository.cs` | Project CRUD; slug uniqueness check; ordered list by `UpdatedAt`. |
| `IGraphNodeRepository.cs` / `GraphNodeRepository.cs` | Node CRUD plus project-scoped `Find(projectId, nodeType, key)`, type-agnostic `FindByKeyAsync(projectId, key)`, and `ListByTypeAsync(projectId, nodeType)` (ordered by Label/Key). |
| `IGraphEdgeRepository.cs` / `GraphEdgeRepository.cs` | Edge CRUD plus directional adjacency query. Defines `EdgeDirection` enum. |
| `IGraphEntityTypeRepository.cs` / `GraphEntityTypeRepository.cs` | Project-scoped CRUD for lightweight graph type registry rows. |
| `IChapterRepository.cs` / `ChapterRepository.cs` | Chapter CRUD ordered by `Order`; `GetMaxOrderAsync(projectId, actId)` and `ReorderAsync(projectId, actId, ids)` are scoped to a single act bucket (pass `actId == null` for the unassigned bucket). |
| `IActRepository.cs` / `ActRepository.cs` | Act CRUD ordered by `Order` per project; `ReorderAsync` rewrites the act ordering in one save. |
| `IOutlineConversationRepository.cs` / `OutlineConversationRepository.cs` | Persistence for `OutlineConversation` + ordered `OutlineMessage`s: `GetByProjectIdAsync`, `LoadMessagesAsync`, `GetMaxOrderAsync`, `AddConversationAsync`, `AddMessageAsync`, `UpdateMessage`, `RemoveConversation`. |
| `IEditorConversationRepository.cs` / `EditorConversationRepository.cs` | Persistence for project-wide `EditorConversation` + ordered `EditorMessage`s and editor chat visuals: get/create support, message loading/order lookup, add/update/remove, and save. |
| `IEditorRevisionRepository.cs` / `EditorRevisionRepository.cs` | Persistence for Editor Revision jobs, per-chapter worker sessions, and ordered worker transcript/tool-result messages. |
| `IWritingSampleRepository.cs` / `WritingSampleRepository.cs` | Project-scoped writing sample persistence: list by project (newest updated first), get/count, add/update/remove, and save. |
| `IWritingCoachConversationRepository.cs` / `WritingCoachConversationRepository.cs` | Persistence for the resettable project-level Writing Coach conversation + ordered messages, including assistant tool-call manifests and tool result rows. |
| `ISearchProviderRepository.cs` / `SearchProviderRepository.cs` | CRUD plus active-provider selection for Research Mode search providers. |
| `IResearchConversationRepository.cs` / `ResearchConversationRepository.cs` | Persistence for project-wide Research conversation + ordered messages, including assistant tool-call manifests and tool result rows. |
| `IProjectImageConversationRepository.cs` / `ProjectImageConversationRepository.cs` | Persistence for project-wide Images Chat conversations, ordered messages, tool result rows, and message visuals. |
| `IAiChangeRepository.cs` / `AiChangeRepository.cs` | Persistence for pending AI change batches and changes, including eager-loaded pending batch listing and change lookup for approval actions. |
| `IContestRepository.cs` / `ContestRepository.cs` | Persistence for Editor Contest Mode batches and candidates, including current/history project batch listing, detail loading, candidate lookup, and status updates. |
| `IEditorContextPreferenceRepository.cs` / `EditorContextPreferenceRepository.cs` | Persistence for active-chapter Context Feed include/exclude preferences, scoped by project, chapter, item kind, and item key. |
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
| `ICodexAuthService.cs` / `CodexAuthService.cs` | OpenAI Codex PKCE OAuth flow (start, configured local redirect callback, refresh, revoke). Uses in-process pending state map. |
| `ReasoningContent.cs` | `AIContent` subclass for Codex reasoning summary streaming. |
| `ToolCallStreamingContent.cs` | `AIContent` subclasses for provider-level function-call start and argument-delta streaming. |
| `ToolCallArguments.cs` | Shared parser/normalizer for tool-call argument JSON and SDK argument dictionaries before `AIFunction` invocation. |
| `StreamingToolCallTracker.cs` | Normalizes provider function-call start/delta/final content into app-level started/arguments/ready updates for chat services. |
| `CodexChatClient.cs` | `IChatClient` implementation for Codex Responses API (SSE parser, multimodal user content, function-calling, strict-schema enforcement, reasoning and tool-argument streaming). |
| `IChatClientFactory.cs` / `ChatClientFactory.cs` | Constructs an `IChatClient` per provider (Codex vs OpenAI-compatible), applies configured Codex/OAuth request timeout, and exposes `TestModelAsync`. |
| `IVisionModelClientFactory.cs` / `VisionModelClientFactory.cs` | Provider-backed image-reading client for vision probes and PDF page transcription; supports Codex Responses and OpenAI-compatible multimodal chat requests. |
| `AssistantWorkflowInstructions.cs` | Code-owned, non-editable AI workflow/tool-use instructions appended to project guidance and reused by editor/outline/revision-worker chat, including Contest Mode preparation rules. |
| `AgentOptions.cs` | Shared agent options bound from `Agents:*`; caps iterative tool-call rounds, configures transient ingest LLM retry attempts/delays, and sets Codex/OAuth request timeout. |
| `SeedSystemPrompt.cs` | Hardcoded default system prompt seeded into every newly-created `Project`. |

### Search/

| File | Description |
|------|-------------|
| `WebSearchModels.cs` | Normalized web-search request/response/result records shared by Research tools and concrete providers. |
| `IWebSearchClient.cs` | Interface implemented by concrete API-backed search providers. |
| `IWebSearchProviderFactory.cs` / `WebSearchProviderFactory.cs` | Resolves the concrete search client for a configured `SearchProviderKind`. |
| `ISearchProviderService.cs` / `SearchProviderService.cs` | Application service for search-provider CRUD, active-provider readiness, provider tests, and active-provider search execution. |
| `SerpApiWebSearchClient.cs` | SerpApi Google-search client; maps `organic_results` into normalized `WebSearchResult`s. |
| `BraveWebSearchClient.cs` | Brave Search API client; maps `web.results` into normalized `WebSearchResult`s. |
| `ProjectSearchModels.cs` | Internal project-search source-type constants plus request/result/source/read/index chunk records for filtered lexical/hybrid retrieval. |
| `IProjectSearchIndex.cs` / `SqliteFtsProjectSearchIndex.cs` | FTS5-backed lexical/BM25 index for chapters, acts, entities, ingest sources, and source chunks, including source/container filters and snippets. |
| `IProjectSearchService.cs` / `ProjectSearchService.cs` | App/tool facade for internal project search: lists candidate sources, reads paginated source text, and fuses FTS5 keyword hits with sqlite-vec semantic hits via Reciprocal Rank Fusion. |

### Research/

| File | Description |
|------|-------------|
| `IResearchService.cs` / `ResearchService.cs` | Persistent streaming Research chat: builds project-level guidance/facts/outline context, replays text-only history to the model, streams cache-first web + graph tool calls, stages Review edits, and derives current-conversation activity. |
| `ResearchTools.cs` | Research tools for cache-first web reads, safe webpage-image inspection, confirmed import/crop/entity attachment, entity visuals, and staged graph mutations. |
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

### Projects/

| File | Description |
|------|-------------|
| `IProjectService.cs` / `ProjectService.cs` | Project CRUD facade. `CreateAsync` slugifies the name (collision-free via `-2`/`-3` suffix), seeds `SystemPrompt` from `SeedSystemPrompt.Default`, and syncs the Project graph node/type defaults. `RenameAsync` updates the graph projection. `UpdateSystemPromptAsync` (rejects empty) and `SetIncludeCurrentChapterAsync` back the Context Feed edits. `DeleteAsync` wipes vector chunks and internal search rows before EF-cascading the project + child graph + chapter rows. Slug stable across renames. |

### Writing/

| File | Description |
|------|-------------|
| `IWritingSampleService.cs` / `WritingSampleService.cs` | UI-facing facade for project-scoped writing samples: create/list/get/update/delete, title validation, sample body persistence, and project `UpdatedAt` touches. |
| `IWritingCoachService.cs` / `WritingCoachService.cs` | Resettable project-level Writing Coach chat service. Mirrors the Outline chat streaming/tool-call loop with a coach prompt and read-only tool set; streams tool arguments and persists assistant tool calls plus tool-result rows. |
| `WritingCoachTools.cs` | Read-only Writing Coach tool builder. Exposes `read_current_section` for the latest editor draft and `list_project_facts` for graph-backed ProjectFact context. |
| `WritingCoachTurnUpdate.cs` | Streaming update records consumed by the Writing Coach panel: text deltas, tool-call start/argument/completion updates, assistant completion, and turn errors/cancellation. |
| `WritingCoachTurnRunner.cs` | Background turn runner for Writing Coach: owns per-project active turns, fresh scoped service execution, buffered UI subscription, and Stop-only cancellation. |

### Context/

| File | Description |
|------|-------------|
| `IContextBuilder.cs` / `ContextBuilder.cs` | Async context assembly for Context Feed/editor chat plus project-level Research context. Builds keyed `ContextItem`s for guidance, outline, facts, current/previous chapters, visual layout manifests, project image references, writing samples, selected/auto-related entities, and structural references; `Assemble()` returns the literal system message. |
| `IEditorContextService.cs` | Editor context facade extending `IContextBuilder`; persists per-chapter context item inclusion, including explicit project-image context keys, and exposes auto/included entity/context key sets for recommendations. |
| `IContextRecommendationService.cs` / `ContextRecommendationService.cs` | Produces active-chapter context recommendations from second-degree graph links, direct context-vector hits, and manual search across entities plus structural references. |
| `IContextIndexingService.cs` / `ContextIndexingService.cs` | Maintains targeted direct vector rows and internal lexical search chunks for addable context items: graph entities, chapters, acts, ingest sources, and ingest source chunks; refreshes source-scoped auto mention links. |
| `VectorIndexWorkCoordinator.cs` | Scoped coordinator that can defer and dedupe expensive chapter/body/context vector index work during review apply, while normal calls run immediately. |
| `IEntityRelationContextService.cs` / `EntityRelationContextService.cs` | Shared bounded graph relation/traversal map builder for Context Feed and agent tool payloads that return entity information. |
| `ChapterFormatting.cs` | `WithLineNumbers` / `SplitLines` / `JoinLines` helpers shared by the editor gutter, Context Feed preview, and AI tool reads so user and LLM see identical line numbers. |

### EntityVisuals/

| File | Description |
|------|-------------|
| `EntityVisualModels.cs` | Read/request/change records for visual examples, entity targets, and source candidates. |
| `EntityVisualContextOptions.cs` | Limits for images per entity/turn, model input edge, and source visuals per ingest chunk. |
| `IEntityVisualExampleService.cs` / `EntityVisualExampleService.cs` | Association/candidate reads and mutations, promotion, cleanup, and entity reindexing. |
| `EntityVisualContextService.cs` | Bounded, deduplicated multimodal entity context assembly for agent turns. |

### Tokens/

| File | Description |
|------|-------------|
| `ITokenCounter.cs` / `CompositeTokenCounter.cs` | Reusable token counting abstraction; tries exact tiktoken counting first, then falls back to a char-based estimator. |
| `TokenCountRequest.cs` / `TokenCountResult.cs` | Request/result records for model-or-encoding token counting with method/exactness/warning metadata. |
| `TokenCountingOptions.cs` | Configuration for default encoding, model-to-encoding mappings, and char-estimator ratio. |
| `TiktokenTokenCounter.cs` | Exact token counter backed by `Microsoft.ML.Tokenizers` tiktoken encodings. |
| `CharEstimateTokenCounter.cs` | Conservative reusable fallback token counter based on character length. |
| `TokenBudgetOptions.cs` / `TokenBudgetPlanner.cs` | Reusable prompt/source-token budget planner for large-context workflows such as ingest. |

### Ingest/

| File | Description |
|------|-------------|
| `IIngestService.cs` / `IngestService.cs` | Application service for ingest job lifecycle/UI reads/provider resolution/artifact preprocessing/queueing/notifications; restart/delete subtracts source-scoped graph assertions, removes ingest-owned orphan graph output, and refreshes targeted context vectors. |
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
| `IngestWikiSheet.cs` | Shared wiki/canon helper/models for ingest-managed summaries, aliases, wiki sections, source-backed `canonSource.*` markdown, canon metadata cleanup, citations, and search projection helpers. |
| `IngestJobWorker.cs` | Hosted background worker that marks interrupted jobs/chunks stopped at startup, notifies the UI, and drains queued ingest jobs in scoped processors. |
| `IngestJobProcessor.cs` | Runs ingest with streaming staging, bounded source/entity visuals, final synthesis, relationship promotion, retries, diagnostics, and indexing. |
| `IngestAgentTools.cs` | Ingest tools for identity/observations/relationships, source-visual promotion with optional crop/entity attachment, final canon writes, and source progress. |

### ImportExport/

| File | Description |
|------|-------------|
| `ProjectExportModels.cs` | v6 portable export DTOs for stable graph refs, entity visuals/associations, publish title/spread settings and cover identity, chapter visuals, image context, and crop lineage. |
| `IProjectImportExportService.cs` / `ProjectImportExportService.cs` | UI-facing import/export facade: builds Full/Non-structural JSON including visual/image data for Full exports, queues import jobs, lists/details/deletes import jobs, and emits import notifications. |
| `ProjectImportUiModels.cs` | Lightweight read-model records for the Import / Export tab job list, detail view, and report rows. |
| `ProjectImportJobQueue.cs` | In-process import job queue used by the hosted worker. |
| `ProjectImportJobNotifier.cs` | In-process pub/sub for live import job updates consumed by the Blazor Import / Export tab. |
| `ProjectImportJobWorker.cs` | Hosted background worker that marks interrupted imports failed at startup and drains queued import jobs. |
| `ProjectImportJobProcessor.cs` | Runs one import job: validates export JSON, imports images with remapped crop lineage/page settings, appends Full structure/visuals, restores v6 publish presentation and cover choices, merges non-structural graph data, records reports, and refreshes indexes best-effort. |

### Images/

| File | Description |
|------|-------------|
| `ProjectImageModels.cs` | Image, normalized crop, and entity-target requests/views plus persisted jobs, output state, masks, provider progress, and runtime snapshots. |
| `IProjectImageService.cs` / `ProjectImageService.cs` | Shared project image-library facade over stored image assets: metadata-only listing with endpoint URLs, byte reads, upload, deterministic local crop/reuse, legacy blocking generation, metadata, delete, thumbnail, and reference scrubbing. |
| `IProjectImageJobService.cs` / `ProjectImageJobService.cs` | Persistence-facing image job service for create/list/start/complete jobs, output state/errors, saving generated assets, and validating PNG/shape masks. |
| `IProjectImageGenerationRuntime.cs` / `ProjectImageGenerationRuntime.cs` | Singleton FIFO image generation queue with one active job per project, retries, runtime partial previews, completion waiters, and state notifications. |
| `ProjectImageGenerationStartupWorker.cs` | Hosted startup worker that marks interrupted running image jobs failed and resumes queued project work. |
| `IProjectImageProvider.cs` / `CodexProjectImageProvider.cs` | Responses image provider adapted from PixelChat for Codex/OpenAI account generation and masked edits with streamed partial images. |
| `ProjectImageGenerationOptions.cs` | Configurable image model defaults, count/reference limits, retry/timeout settings, partial image count, and agent wait timeout. |
| `DataUrl.cs` | Shared data URL parse/format helper for mask and provider payloads. |
| `ProjectImageBinary.cs` | Validates PNG/JPEG/WebP raster input and normalizes WebP library output. |
| `ProjectImageResize.cs` | Shared bounded-edge image resize helper for model and preview delivery. |
| `ProjectImageEndpoints.cs` | Minimal API endpoints for scoped project image bytes, masks, Images Chat visuals, and Editor Chat visual content, with optional image max-edge thumbnails. |

### ImagesChat/

| File | Description |
|------|-------------|
| `IImagesChatService.cs` / `ImagesChatService.cs` | Separate project-scoped Images Chat service with project context, attached image context, tool streaming, persisted transcript/visuals, queued generation/edit tools, and model-only image context for vision-ready turns. |
| `ImagesChatTools.cs` | Images Chat LLM tools for project search/source reads, chapters, image library reads/crops with visual chips, visual layout manifests, rendered snapshot inspection, shape masks, queued generation/editing, and chapter image placement/context. |
| `ImagesChatToolContext.cs` | Per-turn Images Chat tool context carrying provider/vision readiness, current tool metadata, visible visual attachments, model-only generated images, and mutation signaling. |
| `ImagesChatTurnUpdate.cs` | Streaming update records consumed by `ImagesChatPanel`: text deltas, tool start/argument/completion with visuals, mutation refresh, assistant completion, and turn errors. |
| `ImagesChatTurnRunner.cs` | Background turn runner for Images Chat: executes scoped chat turns outside component lifetime and replays buffered live updates to reopened panels. |

### ChapterVisuals/

| File | Description |
|------|-------------|
| `ChapterVisualModels.cs` | UI/service records for chapter visual state, page layout mode updates, and image placement results. |
| `IChapterVisualService.cs` / `ChapterVisualService.cs` | Chapter visual-layout facade for mutations, cleanup, manifests, editor snapshots, and batched guide-free publish surfaces with explicit leaf/output geometry and optional final clockwise rotation. |
| `PicturePageImageGenerationGuidance.cs` | Shared PicturePage image-generation guidance helper: layout-native target sizes, slot-size recommendations, manifest lines, and prompt appendix text for image tools. |

### Publish/

| File | Description |
|------|-------------|
| `PublishModels.cs` | Publish workspace/document/export records for profile presentation modes, covers, outline selections, placements, and rendered Picture Page surfaces with physical and output geometry. |
| `IPublishService.cs` / `PublishService.cs` | Publish facade for profile/cover/outline/placement persistence and exports; resolves cover-aware title behavior and enriches EPUBs with native or sideways composited Picture Page surfaces. |
| `PublishEndpoints.cs` | Cacheable HTTP endpoints for validated guide-free cover previews and native or clockwise-rotated high-resolution interior Picture Page surfaces, avoiding large Blazor payloads. |
| `IPublishExportFormatter.cs` / `PublishExportFormatters.cs` | TXT/Markdown formatters plus a dependency-free mixed-layout EPUB writer with reflowable prose and one accessible fixed item per cover/Picture Page, including landscape-request and sideways spread modes. |

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
| `IEditorChatService.cs` | Project-wide editor chat service contract plus per-turn `EditorChatContext` for editor tools, staging helpers, persisted visual chips, project-image/raw-snapshot model context, and Contest Mode settings/actions. |
| `EditorChatService.cs` | Persistent streaming editor chat: assembles Context Feed and automatic visual snapshots, streams text/tool/image-generation progress, persists chat/tool visual rows, feeds tool-loaded project images and rendered snapshots back to vision-ready models, stages Review edits, emits UI refreshes, and routes Contest Mode terminal tool calls. |
| `EditorChatOptions.cs` | Configuration for editor-chat-specific tool behavior, including paginated chapter reads, model-facing tool-result cap, and prose-only revision worker concurrency/iteration limits. |
| `EditorChatTools.cs` | Editor chat LLM tools for assembled context, impact scoping, project search/source/chapter reads, facts, entities with visible visual chips, explicit project-image reads/crops, rendered layout/text-fit inspection, queued image generation, visual-layout operations, `edit_chapter`/outline mutations, revision-agent spawning, and Contest preparation. |
| `EditorChatChangeStagingContext.cs` | Editor chat staging helper for chapter-body edits; creates pending `AiChange` rows owned by the editor transcript when Review edits is enabled. |
| `EditorChatTurnUpdate.cs` | `[JsonDerivedType]`-decorated streaming update records consumed by `EditorChatPanel`: text deltas, tool start/argument/end updates with visuals, image-generation progress, pending changes, contest progress/raw JSON, mutation refresh, assistant completion, and turn errors. |
| `EditorChatTurnRunner.cs` | Background turn runner for Editor Chat: preserves active turns across tab changes while leaving explicit Stop as the cancellation path. |
| `EditorContestModels.cs` | DTOs and helper records for Contest Mode settings, start requests, captured chat-context snapshots, model responses, mutation JSON, and streaming contest status/raw-response updates. |
| `IEditorContestService.cs` / `EditorContestService.cs` | Contest Mode application service: persists project settings, starts terminal contest batches, runs selected models without tools, streams raw Candidate JSON, validates JSON chapter-body mutations, builds proposed bodies, and resolves inline candidate review decisions. |
| `EditorRevisionAgentModels.cs` | DTOs for prose-only revision assignments, run results, job/session details, and transcript projections used by tools and UI. |
| `IEditorRevisionAgentService.cs` / `EditorRevisionAgentService.cs` | Same-turn revision-agent orchestrator: validates chapter assignments, persists jobs/sessions, runs bounded-parallel workers, and returns completed/staged chapter-body edits to the coordinator. |
| `EditorRevisionAgentProcessor.cs` | Per-session worker runner with read-only grounding tools, filtered hybrid project search/source reads, and terminal assigned-chapter body editing; persists worker transcript/tool history. |
| `IEditorRevisionJobNotifier.cs` | In-process pub/sub for revision job/session progress updates, matching other local background workflow notifiers. |

### Outline/

| File | Description |
|------|-------------|
| `IActService.cs` / `ActService.cs` | Act CRUD facade. `CreateAsync` auto-orders to the end. `DeleteAsync` lets the FK demote owned chapters to Unassigned (`OnDelete.SetNull`). Touches `Project.UpdatedAt`, keeps Act graph nodes/structural edges synchronized, and updates targeted act context vectors on mutations. |
| `IOutlineCollaborationService.cs` / `OutlineCollaborationService.cs` | Multi-turn collaborative outline chat. Streams LLM text/tool updates, persists chat history, stages mutating tool calls when project approval is enabled, blocks new turns while pending changes remain, and instructs the LLM to persist project-level truths as `ProjectFact` graph nodes. |
| `OutlineCollaborationTools.cs` | `AIFunction` definitions exposed to the outline LLM, including filtered project source reads and project-image crop/entity-attachment tools. `list_outline` includes `projectFacts`; ProjectFact creation uses generic entity tools and is parented to the Project graph node. |
| `OutlineChatTurnRunner.cs` | Background turn runner for Outline chat: owns active turn cancellation/subscription outside the Blazor component lifetime. |
| `IAiChangeApprovalService.cs` / `AiChangeApprovalService.cs` | Applies or rejects queued AI changes from outline/editor/research chat, including outline/entity mutations and editor chapter-body edits; enforces dependency application/rejection cascading and writes hidden correction messages to the owning transcript. |
| `AiChangeReviewDrafts.cs` | Typed helper for persisted pending-change review drafts: reads editable text fields, updates draft payload JSON, validates draft metadata, and resolves effective after-payloads. |
| `OutlineToolStagingContext.cs` | Per-turn working snapshot for approval mode: applies new acts/chapters/entities directly, overlays staged existing-data edits/reorders/links, keeps auto links low-priority in read payloads, and persists `AiChange` rows with dependency metadata. |
| `OutlineChangePayloads.cs` | JSON payload records shared by staging and approval application for acts, chapters, entities, links, and reorders. |
| `AiChangeReviewDiffBuilder.cs` | Builds single-change and grouped review diff models from pending AI changes, including fuzzy line alignment and intraline highlights for the pending-change modal. |
| `IEntityService.cs` / `EntityService.cs` | Single contract for every story-graph entity (Characters, Locations, Events/beats, ...). Entities persist as `GraphNode`s via `IGraphStore`; create/update/delete, parent moves, and relationship mutations refresh affected context/search indexes and auto mentions. `ListLinksAsync` returns manual links before low-priority read-only auto links. |
| `IEntityTypeService.cs` / `EntityTypeService.cs` | Lightweight graph type registry facade. Seeds structural/default types (`Project`, `Act`, `Chapter`, `ProjectFact`, `Event`, `Character`, `Location`), discovers arbitrary node types, and creates custom non-structural types for the side panel. |
| `IProjectFactService.cs` / `ProjectFactService.cs` | Project-level graph fact facade. Stores one `ProjectFact` graph node per key/value pair, ensures a Project → ProjectFact `HasChild` edge, enforces case-insensitive key upserts, touches `Project.UpdatedAt`, and projects linked graph entities for UI/prompt display. |
| `IOutlineGraphSync.cs` / `OutlineGraphSync.cs` | Synchronizes the EF outline spine into graph nodes and `HasChild` edges: Project → Acts / unassigned Chapters, Act → Chapters, Chapter → Events. Used by project/act/chapter services and startup repair. |
| `OutlineTurnUpdate.cs` | `[JsonDerivedType]`-decorated abstract record for streaming chat updates: text deltas, tool-call start/argument/completion updates, assistant completion, outline mutation refresh, and turn errors. |

### Chapters/

| File | Description |
|------|-------------|
| `IChapterService.cs` / `ChapterService.cs` | Chapter CRUD facade. `CreateAsync(projectId, actId?, ...)` stamps a new chapter into a chosen act bucket (or Unassigned), syncs the Chapter graph node/edge, and writes chapter context vectors/search chunks. `UpdateAsync` accepts an optional `ChapterActAssignment` wrapper to MOVE the chapter between act buckets, persists body/title/synopsis changes, and updates chapter/body vectors, lexical search, and auto mentions inside the service. `ReorderAsync(projectId, actId?, ids)` reorders within a single bucket, repairs structural graph edge order, and refreshes only the affected act vector. `DeleteAsync` removes vectors/search rows AND removes the chapter's graph node + any `HasChild` entity children before EF delete (keeps `IGraphStore` consistent without depending on `IEntityService`). |

### wwwroot/

| File | Description |
|------|-------------|
| `app.css` | App-wide Lorekeeper design tokens and shared editorial treatments for typography, controls, cards, status, empty states, navigation, and Bootstrap primitives. |
| `js/autosizeTextareas.js` | Small shared JS module that attaches to `textarea[data-autosize]`, grows each textarea to its `scrollHeight`, refreshes on input/change and width changes, and prevents nested textarea scrollbars. |
| `js/fileDownloads.js` | Browser download helper used by Import / Export and Publish to save generated graph JSON and publish export files. |
| `favicon.png` | Site icon. |
| `lib/bootstrap/` | Vendored Bootstrap distribution. |
| `lib/vis-network/` | Vendored `vis-network` browser graph renderer assets and license files used by the Graph tab. |

