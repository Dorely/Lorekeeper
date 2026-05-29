# Lorekeeper — File Map

> **Auto-maintained reference.** Agents and contributors should update this file whenever files are added, removed, or significantly refactored.  
> Read this file at the start of every session to understand the codebase layout.

---

## Root

| File | Description |
|------|-------------|
| `VISION.md` | High-level project vision and success criteria. |
| `README.md` | Project readme (currently a stub). |
| `FILEMAP.md` | This file — concise map of every source file. |
| `Lorekeeper.sln` | Solution file containing the `Lorekeeper` project. |
| `global.json` | Pins the .NET SDK version (`rollForward: latestFeature`). |
| `.editorconfig` | C#/Razor formatting and naming rules. |
| `.gitignore` | Standard .NET ignore patterns; ClickOnce publish output is scoped to the repo-root `/publish/` folder so source folders named `Publish` remain trackable. |
| `.github/copilot-instructions.md` | Project guidelines for AI assistants. |

## Lorekeeper/ — Blazor Web App (Interactive Server)

| File | Description |
|------|-------------|
| `Lorekeeper.csproj` | Project file: `net10.0`, nullable + implicit usings, warnings-as-errors. EF Core SQLite, Microsoft.Extensions.AI(.OpenAI), OpenAI 2.8, sqlite-vec, Microsoft.ML.Tokenizers, SkiaSharp, EPUB/PDF ingestion packages, and patched Microsoft.Bcl.Memory. |
| `Program.cs` | Host setup, Blazor Interactive Server hub sizing, DI for persistence/knowledge/LLM/search/token/ingest/research/import-export/publish/writing/editor-chat/contest/revision-agent services, internal project search, auto graph links, EF migrate at startup, optional sqlite-vec init from the active embedding config, outline graph repair, Codex OAuth endpoints. |
| `appsettings.json` / `appsettings.Development.json` | Configuration: `ConnectionStrings:DefaultConnection`, `Persistence:Provider`, `Blazor:*`, `Ingest:Sectioning:*`, `Research:Web:*`, `Embeddings:*`, `Agents:*`. |
| `Properties/launchSettings.json` | Local launch profiles (HTTP pinned to `localhost:1455` for Codex OAuth redirect). |

### Components/

| File | Description |
|------|-------------|
| `App.razor` | Root component: `<html>` shell, head outlet, scripts. |
| `Routes.razor` | `<Router>` wiring `MainLayout` and `NotFound`. |
| `_Imports.razor` | Shared `@using` directives for all components. |
| `EntityKnowledgeView.razor` (+ `.razor.css`) | Shared read-only entity knowledge renderer for structured wiki data and source-backed canon markdown used by graph, outline, and context entity detail surfaces. |

### Components/Chat/

| File | Description |
|------|-------------|
| `ChatModels.cs` | Shared chat UI view models for persisted/live messages, text parts, duration-aware tool-call chips, and transcript token-count helpers. |
| `ChatTranscriptTokenCounter.cs` | Shared transcript token-count adapter for `ChatSurface` panels: projects domain messages into a common token-count shape, includes pending/live turns, and formats exact/estimated count labels. |
| `ChatSurface.razor` (+ `.razor.css`, `.razor.js`) | Reusable chat shell for transcript/live rendering, grouped adjacent tool-call chips, composer controls, scrolling, and textarea autosize behavior. |
| `ChatToolChipView.razor` (+ `.razor.css`) | Reusable expandable tool-call card that shows streamed arguments/results/errors and opens Editor Revision worker transcripts from `start_revision_agents` chips. |

### Components/Layout/

| File | Description |
|------|-------------|
| `MainLayout.razor` / `.css` | Top-level page layout with sidebar + main column. Locks the app shell to viewport height and gives `article.content` a flex/scroll context so workspace pages can create independently scrolling panes. |
| `PrintLayout.razor` / `.css` | Minimal no-navigation layout used by print-oriented pages such as Publish browser PDF export. |
| `NavMenu.razor` / `.css` | Sidebar navigation (Home, Providers, Embeddings, Search Providers). |
| `ReconnectModal.razor` / `.cs` / `.css` | UI shown when the SignalR circuit drops. |

### Components/Pages/

| File | Description |
|------|-------------|
| `Home.razor` | Project picker at `/` — lists projects, create/rename/delete-with-confirm; deletes cascade to graph + vector chunks via `IProjectService`. |
| `Error.razor` | Error page rendered by exception handler middleware. |
| `NotFound.razor` | 404 page wired through `UseStatusCodePagesWithReExecute`. |

### Components/Pages/Projects/

| File | Description |
|------|-------------|
| `ProjectLayout.razor` (+ `.razor.css`) | Shared shell for project workspace pages: loads project by slug, renders title + horizontal tab strip including Editor, Outline, Writing Sample, Publish, Research, Ingest, Graph, and Import / Export; exposes `Project` via `CascadingValue`. |
| `EditorPage.razor` | Editor tab routes (`/projects/{Slug}/editor` and `/projects/{Slug}/editor/{ChapterId:guid}`). Wraps `ProjectLayout` + `EditorContent`. |
| `EditorContent.razor` (+ `.razor.css`, `.razor.js`) | Context-aware chapter editor with collapsible chat/right panes, Edit/Read/Review modes, inline active-chapter body review controls/scroll markers, and inline Contest Mode candidate review. Persists/reindexes body edits, remembers the selected chapter per browser/project, and refreshes editor/context surfaces after AI turns or approved changes. |
| `EditorChatPanel.razor` (+ `.razor.css`) | Editor chat adapter over `ChatSurface`: loads the transcript, disables LLM controls without a working provider, streams text/tool/contest updates, routes active-chapter body changes and Contest Mode results to inline Review mode, and keeps the universal pending-change review modal for non-editor body changes. |
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
| `PublishPage.razor` | Publish tab at `/projects/{Slug}/publish`; wraps `ProjectLayout` and hosts `Publish.PublishContent`. |
| `ManuscriptPrintPage.razor` (+ `.razor.css`, `.razor.js`) | Print/PDF publish route at `/projects/{Slug}/manuscript/print`; renders the saved publish profile, flattened cover image, TOC, metadata, selected outline, and placed images in the no-chrome print layout. |
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

### Components/Pages/Projects/Publish/

| File | Description |
|------|-------------|
| `PublishContent.razor` (+ `.razor.css`) | Full Publish tab workspace for metadata, outline selection, cover layout, assets/placements, exports, Print/PDF preview, and Codex-connected image generation. |
| `CoverTextEditor.razor` (+ `.razor.css`, `.razor.js`) | Interactive cover text overlay editor: previews the selected cover asset, drags fixed title/subtitle/author layers, and exposes typography/placement controls. |

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
| `Providers.razor` | LLM provider configuration UI: Codex OAuth connect/status messages, parent providers + child models, separate chat/vision readiness test status, working-only default selection, inline edit, and model tests. |
| `Embeddings.razor` | Embedding settings UI at `/settings/embeddings`: selects or unsets one active embedding model from existing provider connections, requires test-before-save, captures dimensions, and warns before re-embedding on model changes. |
| `SearchProviders.razor` | Search provider configuration UI for Research Mode: add/edit/delete SerpApi or Brave providers, save API keys, test connectivity, and choose the single active provider. |

### Models/

| File | Description |
|------|-------------|
| `AuthType.cs` | Enum: None, ApiKey, OAuth. |
| `LlmProvider.cs` | EF entity for an LLM endpoint/model row. Supports parent/child credential sharing plus persisted chat- and vision-readiness test snapshots. |
| `EmbeddingConfiguration.cs` | Singleton EF entity for the active embedding setup: top-level provider connection, embedding API kind, model id, dimensions, last-tested snapshot, and timestamps. |
| `OAuthToken.cs` | EF entity holding access/refresh tokens for an OAuth-backed provider. |
| `Project.cs` | EF entity scoping all narrative data. Stable `Slug` for URLs; owns project settings and child navigation collections including conversations, contests, revision jobs, writing samples, import jobs, publish profiles/assets/selections/placements, and graph rows. |
| `Act.cs` | EF entity for a top-level outline grouping (Title/Synopsis/Order) under a `Project`. Cascade-deleted with the project. Owned chapters survive act deletion (FK `OnDelete.SetNull`). |
| `Chapter.cs` | EF entity for a chapter (Title/Body/Synopsis/Order) under a `Project`, optionally assigned to an `Act` via nullable `ActId`. `Order` is scoped to the chapter's act bucket (or the project-level Unassigned bucket when `ActId` is null). Tracks `VectorIndexState` (UpToDate/Stale/Disabled/Failed) + `VectorIndexedAt` + `VectorIndexError`; `VectorSourceId` returns the stable vector-store source id (`Id.ToString("N")`). |
| `EditorContextPreference.cs` | EF entity for per-chapter Context Feed include/exclude preferences keyed by context item kind + stable item key. |
| `EditorConversation.cs` | EF entity — one persistent multi-turn editor chat per `Project` (unique on `ProjectId`). Owns ordered `EditorMessage`s; cascade-deleted with the project. |
| `EditorMessage.cs` | EF entity for a single row in an `EditorConversation`: monotonic `Order`, role (`System`/`User`/`Assistant`/`Tool`), text content, assistant tool-call JSON, tool result metadata, status, optional error, and creation timestamp. |
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
| `PublishProfile.cs` | EF entity for one saved publish profile per project: book metadata, front/back matter, output options, selected cover asset, and cover text layout JSON. |
| `PublishAsset.cs` | EF entity for uploaded or Codex-generated PNG/JPEG publish assets with bytes, alt text, prompt/source metadata, and cover/placement navigation. |
| `PublishOutlineSelection.cs` | EF entity for per-project act/chapter publish inclusion flags; act selection controls the act page while chapters remain independently selectable. |
| `PublishImagePlacement.cs` | EF entity for cover-independent interior image placements before/after acts or chapters and chapter openings/endings, with captions and ordering. |
| `GraphNode.cs` | Generic graph node: `(ProjectId, NodeType, Key)` unique, JSON properties bag. Cascade-deleted with its `Project`. |
| `GraphEdge.cs` | Directed edge between graph nodes with type, JSON properties, optional relationship-specific `SortOrder`, and timestamps. |
| `GraphEntityType.cs` | Lightweight project-scoped graph type registry entry for UI/LLM labels/defaults. Descriptive rather than restrictive; arbitrary node types remain valid. |

### Persistence/

| File | Description |
|------|-------------|
| `AppDbContext.cs` | EF Core context for projects, provider/embedding/search settings, outline/editor/writing/research chat, editor revision jobs, writing samples, graph, editor context preferences, AI change approval, ingest/import queues, webpage candidates, and publish profiles/assets/layouts. JSON converter shared by graph property bags; configures relationships/indexes and retries transient SQLite lock save failures. |
| `PersistenceServiceCollectionExtensions.cs` | `AddLorekeeperPersistence` switch on `Persistence:Provider` (SQLite today; Postgres slot for future); applies shared SQLite timeout settings. |
| `SqliteConnectionSettings.cs` | Shared SQLite connection-string and startup PRAGMA settings: busy timeout, WAL journal mode, and normal synchronous mode to reduce local lock contention. |
| `Migrations/` | EF Core migrations (`InitialSchema`, project/chapter/outline/graph/ingest/writing/editor-context/import-export/search/research/publish/cover-layout/revision-agent/embedding-config/chat-readiness/adaptive artifact ingest, ingest staging records/canon cleanup, ingest LLM token metadata, and ingest report payload cleanup migrations, `ReplaceAiConsoleWithEditorChat`, `AddContestMode`, Contest Mode cleanup, inline contest review, and web research cache metadata). |

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
| `IEditorConversationRepository.cs` / `EditorConversationRepository.cs` | Persistence for project-wide `EditorConversation` + ordered `EditorMessage`s: get/create support, message loading/order lookup, add/update/remove, and save. |
| `IEditorRevisionRepository.cs` / `EditorRevisionRepository.cs` | Persistence for Editor Revision jobs, per-chapter worker sessions, and ordered worker transcript/tool-result messages. |
| `IWritingSampleRepository.cs` / `WritingSampleRepository.cs` | Project-scoped writing sample persistence: list by project (newest updated first), get/count, add/update/remove, and save. |
| `IWritingCoachConversationRepository.cs` / `WritingCoachConversationRepository.cs` | Persistence for the resettable project-level Writing Coach conversation + ordered messages, including assistant tool-call manifests and tool result rows. |
| `ISearchProviderRepository.cs` / `SearchProviderRepository.cs` | CRUD plus active-provider selection for Research Mode search providers. |
| `IResearchConversationRepository.cs` / `ResearchConversationRepository.cs` | Persistence for project-wide Research conversation + ordered messages, including assistant tool-call manifests and tool result rows. |
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
| `ILlmProviderService.cs` / `LlmProviderService.cs` | CRUD over providers, credential resolution, persisted chat/vision readiness, working-default selection, and Codex connection checks. |
| `CodexProvider.cs` | Shared Codex provider name/endpoints/defaults plus OAuth JWT account-id parsing for Codex chat, images, and embeddings. |
| `ICodexAuthService.cs` / `CodexAuthService.cs` | OpenAI Codex PKCE OAuth flow (start, handle callback returning provider id, refresh, revoke). Uses in-process pending state map. |
| `ReasoningContent.cs` | `AIContent` subclass for Codex reasoning summary streaming. |
| `ToolCallStreamingContent.cs` | `AIContent` subclasses for provider-level function-call start and argument-delta streaming. |
| `ToolCallArguments.cs` | Shared parser/normalizer for tool-call argument JSON and SDK argument dictionaries before `AIFunction` invocation. |
| `StreamingToolCallTracker.cs` | Normalizes provider function-call start/delta/final content into app-level started/arguments/ready updates for chat services. |
| `CodexChatClient.cs` | `IChatClient` implementation for Codex Responses API (SSE parser, function-calling, strict-schema enforcement, reasoning and tool-argument streaming). |
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
| `ResearchTools.cs` | Research LLM tools: cache-first `web_search`, paginated `read_search_result`/`read_webpage`, filtered/throttled `follow_page_links`, read-only entity detail/link tools, and selected graph create/update/link tools reused from outline collaboration. |
| `ResearchTurnUpdate.cs` | Streaming update records consumed by `ResearchChatPanel`: text/tool updates, pending AI change creation, graph mutation refreshes, assistant completion, and turn errors/cancellation. |
| `ResearchActivityModels.cs` | Read models for Research Activity sidebar entity/source summaries and cache-only source detail modals. |
| `WebResearchOptions.cs` | Configurable webpage read limits and polite-fetch defaults: user agent, timeout, max bytes, read-page size, retry timing, max links, robots, throttling, cooldowns, and private-network blocking. |
| `WebPageReader.cs` | Composed webpage reader for Research Mode: validates targets, applies robots/private-network policy, prefers source adapters, falls back to filtered HTML/text extraction, and returns diagnostics. |
| `WebFetchCoordinator.cs` | Per-host fetch coordinator for polite web reads: serializes requests, enforces host delay/jitter, and applies cooldowns after blocked or repeated failed responses. |
| `WebHttpFetchClient.cs` | Shared coordinated HTTP GET helper for webpage, robots, and source-adapter reads with byte limits, timeout handling, retryability, and status diagnostics. |
| `WebLinkPolicy.cs` | URL normalization and link hygiene policy for web research: filters navigation/admin/wiki namespace/static links and prioritizes likely content links. |
| `WebPageTextExtractor.cs` | Shared HTML/text extraction helpers for titles, canonical URLs, main-content text, outgoing links, decoding, and truncation. |
| `WebRobotsPolicy.cs` | Lightweight cached `robots.txt` policy reader/parser used before webpage and source-adapter fetches when enabled. |
| `MediaWikiWebPageSourceReader.cs` | MediaWiki source adapter for `/wiki/{title}` pages: reads allowed `api.php` extract/parse endpoints and returns plain text plus namespace-0 article links. |
| `IWebIngestCandidateService.cs` / `WebIngestCandidateService.cs` | Application service for cached webpage sources: search-result persistence, conversation-aware cache-first URL/page reading with recent-failure cooldown reuse, cache-only source details, and manual Ingest queueing support. |
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

### Context/

| File | Description |
|------|-------------|
| `IContextBuilder.cs` / `ContextBuilder.cs` | Async context assembly for Context Feed/editor chat plus project-level Research context. Builds keyed `ContextItem`s for guidance, outline, facts, current/previous chapters, writing samples, selected/auto-related entities, and structural references; `Assemble()` returns the literal system message. |
| `IEditorContextService.cs` | Editor context facade extending `IContextBuilder`; persists per-chapter context item inclusion and exposes auto/included entity/context key sets for recommendations. |
| `IContextRecommendationService.cs` / `ContextRecommendationService.cs` | Produces active-chapter context recommendations from second-degree graph links, direct context-vector hits, and manual search across entities plus structural references. |
| `IContextIndexingService.cs` / `ContextIndexingService.cs` | Maintains targeted direct vector rows and internal lexical search chunks for addable context items: graph entities, chapters, acts, ingest sources, and ingest source chunks; refreshes source-scoped auto mention links. |
| `VectorIndexWorkCoordinator.cs` | Scoped coordinator that can defer and dedupe expensive chapter/body/context vector index work during review apply, while normal calls run immediately. |
| `IEntityRelationContextService.cs` / `EntityRelationContextService.cs` | Shared bounded graph relation/traversal map builder for Context Feed and agent tool payloads that return entity information. |
| `ChapterFormatting.cs` | `WithLineNumbers` / `SplitLines` / `JoinLines` helpers shared by the editor gutter, Context Feed preview, and AI tool reads so user and LLM see identical line numbers. |

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
| `IBookArtifactPreprocessor.cs` / `BookArtifactPreprocessor.cs` | EPUB/PDF/text artifact preprocessor: extracts EPUB sections, PDF embedded text, vision-read rendered PDF pages, and source page/block locator drafts before job creation. |
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
| `IngestJobProcessor.cs` | Runs ingest jobs with streaming chunk staging, per-entity final canon-source markdown synthesis, simple relationship promotion, compact source/entity memory, retry-aware partial-work context, diagnostics, and indexing updates. |
| `IngestAgentTools.cs` | Ingest LLM tools for compact entity identity indexes, bulk mention resolution, staged entity observations, staged relationship markers, per-entity canon-source markdown writes, and rolling source progress updates. |

### ImportExport/

| File | Description |
|------|-------------|
| `ProjectExportModels.cs` | Portable export DTOs/enums for Lorekeeper graph packages, stable graph node refs, and downloadable export file metadata. |
| `IProjectImportExportService.cs` / `ProjectImportExportService.cs` | UI-facing import/export facade: builds Full/Non-structural graph JSON, queues import jobs, lists/details/deletes import jobs, and emits import notifications. |
| `ProjectImportUiModels.cs` | Lightweight read-model records for the Import / Export tab job list, detail view, and report rows. |
| `ProjectImportJobQueue.cs` | In-process import job queue used by the hosted worker. |
| `ProjectImportJobNotifier.cs` | In-process pub/sub for live import job updates consumed by the Blazor Import / Export tab. |
| `ProjectImportJobWorker.cs` | Hosted background worker that marks interrupted imports failed at startup and drains queued import jobs. |
| `ProjectImportJobProcessor.cs` | Runs one import job: validates export JSON, appends structural Full imports, merges non-structural entities/relationships/provenance, repairs graph links, records report rows, and refreshes indexes best-effort. |

### Publish/

| File | Description |
|------|-------------|
| `PublishModels.cs` | Publish UI/document/export records for profiles, cover text layouts, rendered cover assets, section/chapter selections, assets, image placements, and resolved document projections. |
| `IPublishService.cs` / `PublishService.cs` | Publish facade for profile and cover-layout persistence, outline selection, asset upload/delete, Codex image generation, image placement, flattened-cover document projection, and TXT/Markdown/EPUB export. |
| `IPublishCoverRenderer.cs` / `SkiaPublishCoverRenderer.cs` | SkiaSharp-backed cover compositor that flattens selected cover art plus saved title/subtitle/author layers into a PNG for print and exports. |
| `IPublishExportFormatter.cs` / `PublishExportFormatters.cs` | Publish formatter abstraction plus TXT, Markdown, and dependency-free EPUB implementations with metadata, TOC, flattened cover images, and interior images. |
| `ICodexImageGenerationService.cs` / `CodexImageGenerationService.cs` | Codex OAuth image generation client for `gpt-image-2` through the Codex Responses bridge; sends optional reference image inputs, parses streamed image-generation output, and returns PNG/JPEG bytes. |

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
| `IEditorChatService.cs` | Project-wide editor chat service contract plus per-turn `EditorChatContext` captured by editor tools, staging helpers, and Contest Mode settings/actions. |
| `EditorChatService.cs` | Persistent streaming editor chat: assembles the active Context Feed into the system prompt, resolves the default model, streams text/tool-call arguments, persists user/assistant/tool messages, stages Review edits, emits UI refresh events, and routes Contest Mode terminal tool calls into async candidate generation. |
| `EditorChatOptions.cs` | Configuration for editor-chat-specific tool behavior, including paginated chapter reads, model-facing tool-result cap, and prose-only revision worker concurrency/iteration limits. |
| `EditorChatTools.cs` | Editor chat LLM tools for assembled context, impact scoping, filtered hybrid project search/source reads, chapter reads, facts, entities, normal `edit_chapter`/outline mutations, revision-agent spawning, and Contest preparation. |
| `EditorChatChangeStagingContext.cs` | Editor chat staging helper for chapter-body edits; creates pending `AiChange` rows owned by the editor transcript when Review edits is enabled. |
| `EditorChatTurnUpdate.cs` | `[JsonDerivedType]`-decorated streaming update records consumed by `EditorChatPanel`: text deltas, tool start/argument/end updates, pending changes, contest progress/raw JSON, mutation refresh, assistant completion, and turn errors. |
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
| `OutlineCollaborationTools.cs` | `AIFunction` definitions exposed to the outline LLM, including filtered project source search/read tools. `list_outline` includes `projectFacts`; ProjectFact creation uses generic entity tools and is parented to the Project graph node. Entity create/update payloads include auto mention hints. |
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
| `app.css` | App-wide CSS. |
| `js/autosizeTextareas.js` | Small shared JS module that attaches to `textarea[data-autosize]`, grows each textarea to its `scrollHeight`, refreshes on input/change and width changes, and prevents nested textarea scrollbars. |
| `js/fileDownloads.js` | Browser download helper used by Import / Export and Publish to save generated graph JSON and publish export files. |
| `favicon.png` | Site icon. |
| `lib/bootstrap/` | Vendored Bootstrap distribution. |
| `lib/vis-network/` | Vendored `vis-network` browser graph renderer assets and license files used by the Graph tab. |

