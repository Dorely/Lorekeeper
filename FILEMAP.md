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
| `.gitignore` | Standard .NET ignore patterns. |
| `.github/copilot-instructions.md` | Project guidelines for AI assistants. |

## Lorekeeper/ — Blazor Web App (Interactive Server)

| File | Description |
|------|-------------|
| `Lorekeeper.csproj` | Project file: `net10.0`, nullable + implicit usings, warnings-as-errors. EF Core SQLite, Microsoft.Extensions.AI(.OpenAI), OpenAI 2.8, sqlite-vec, Microsoft.ML.Tokenizers, and patched Microsoft.Bcl.Memory. |
| `Program.cs` | Host setup, Blazor Interactive Server hub sizing, DI for persistence/knowledge/LLM/token/ingest/writing services, EF migrate at startup, sqlite-vec init, outline graph repair, Codex OAuth endpoints. |
| `appsettings.json` / `appsettings.Development.json` | Configuration: `ConnectionStrings:DefaultConnection`, `Persistence:Provider`, `Blazor:*`, `Embeddings:*`. |
| `Properties/launchSettings.json` | Local launch profiles (HTTP pinned to `localhost:1455` for Codex OAuth redirect). |

### Components/

| File | Description |
|------|-------------|
| `App.razor` | Root component: `<html>` shell, head outlet, scripts. |
| `Routes.razor` | `<Router>` wiring `MainLayout` and `NotFound`. |
| `_Imports.razor` | Shared `@using` directives for all components. |

### Components/Layout/

| File | Description |
|------|-------------|
| `MainLayout.razor` / `.css` | Top-level page layout with sidebar + main column. Locks the app shell to viewport height and gives `article.content` a flex/scroll context so workspace pages can create independently scrolling panes. |
| `NavMenu.razor` / `.css` | Sidebar navigation (Home, Providers). |
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
| `ProjectLayout.razor` (+ `.razor.css`) | Shared shell for project workspace pages: loads project by slug, renders title + horizontal tab strip (Editor / Graph / Ingest / Outline / Writing Sample), exposes `Project` via `CascadingValue`, and wraps routed page content in a flex-bounded `.project-body` so pages like Outline can own their internal scroll regions. |
| `EditorPage.razor` | Editor tab routes (`/projects/{Slug}/editor` and `/projects/{Slug}/editor/{ChapterId:guid}`). Wraps `ProjectLayout` + `EditorContent`. |
| `EditorContent.razor` (+ `.razor.css`, `.razor.js`) | Three-column context-aware chapter editor: recommended context left, JS-debounced line-numbered editor + AI console center, Context Feed right. Persists/reindexes body edits, locks while AI runs, and refreshes editor/context/recommendations after AI turns or approved changes. |
| `RecommendedContextPanel.razor` (+ `.razor.css`) | Editor left-panel context recommender: shows semantic/manual/graph-proximity recommendations for entities plus structural references, and adds them to the active chapter's persisted context working set. |
| `ContextFeedPanel.razor` (+ `.razor.css`) | Editable Context Feed: async collapsible cards for project guidance, current chapter, outline, facts, selectable writing samples, selected entities, and selected structural references. Uses stable item keys and `IEditorContextService` to persist per-chapter include/exclude choices. |
| `AiConsolePanel.razor` (+ `.razor.css`) | AI command console: input + Send/Cancel/History, Review edits toggle, pending-change review modal, and callbacks so the editor can flush/lock/refresh. History lists persisted `AiConsoleEntry` rows with prompt snapshots, tool calls, and final response. |
| `GraphPage.razor` | Graph tab at `/projects/{Slug}/graph`; wraps the project shell and hosts the interactive graph workspace. |
| `GraphContent.razor` (+ `.razor.css`, `.razor.js`) | Obsidian-inspired full-project graph workspace: loads graph snapshots, filters/searches nodes, bridges to the local `vis-network` renderer for pan/zoom/drag/select, and coordinates graph refreshes. |
| `GraphDetailsPanel.razor` (+ `.razor.css`) | Selected-node graph editor side panel: create/edit/delete nodes, edit safe parent assignments, and create/edit/delete custom relationships while managed links stay protected. |
| `IngestPage.razor` | Ingest tab at `/projects/{Slug}/ingest`; wraps `ProjectLayout` and hosts `Ingest.IngestContent`. |
| `OutlinePage.razor` | Outline tab route; wraps `ProjectLayout` + `Outline.OutlineContent`. |
| `WritingSamplePage.razor` | Writing Sample tab at `/projects/{Slug}/writing-sample`; wraps `ProjectLayout` + `WritingSample.WritingSampleContent`. |

### Components/Pages/Projects/Ingest/

| File | Description |
|------|-------------|
| `IngestContent.razor` (+ `.razor.css`) | Functional Ingest tab workspace: creates text/file ingest jobs with configured-model selection and optional instructions, shows start-readiness feedback, subscribes to live job updates, and supports stop/resume-with-model-change/restart/delete. |

### Components/Pages/Projects/Outline/

| File | Description |
|------|-------------|
| `OutlineContent.razor` (+ `.razor.css`) | Top-level Outline tab orchestrator. Three-pane CSS-grid layout (chat | outline tree | facts+entities side column) inside a fixed-height grid; each direct column wrapper is an independent `overflow-y: auto` scroll container, with horizontal overflow rather than responsive stacking when space is tight. Bumps a `_refreshSignal` int on every reload that child panels watch to re-read after tool turns. Re-fetches the project from `IProjectRepository` on every reload. |
| `ProjectFactsPanel.razor` (+ `.razor.css`) | Editable project facts block surfaced at the top of the right side column. Reads/writes `ProjectFact` graph nodes via `IProjectFactService`, keeps the compact collapsible key/value UX, supports add/edit/delete, shows linked graph entities, and autosizes fact textareas via `wwwroot/js/autosizeTextareas.js`. |
| `IngestSourcesPanel.razor` (+ `.razor.css`) | Read-only Outline side panel for structural ingest Source → SourceChunk graph nodes, showing chunk summaries/notes and entities linked by `ExtractedFrom` provenance edges. |
| `OutlineTree.razor` (+ `.razor.css`) | Hierarchical Acts → Chapters tree. Acts are collapsible, drag-reorderable groups with inline-editable title/synopsis and `+ Chapter` / Delete (chapters fall back to Unassigned via `OnDelete.SetNull`). Act/chapter synopsis textareas autosize to their content via `wwwroot/js/autosizeTextareas.js`. Chapters are inline-editable rows with a beats-toggle caret + count badge (renders `ChapterBeats` inline when expanded), stale/failed vector-index badge, drag-reorder within their act bucket, an act-picker `<select>` for cross-act moves, Open link, and delete-with-confirm. Re-fetches per-chapter beat counts via `IEntityService.CountChildrenAsync` whenever `RefreshSignal` bumps. |
| `EntitiesPanel.razor` (+ `.razor.css`) | Registry-driven project-scoped entities side panel. Lists non-structural graph types from `IEntityTypeService`, supports `+ Type`, per-type `+ Add`, clickable entity rows, and a Bootstrap-style modal with editable name/properties plus read-only adjacent graph links via `IEntityService.ListLinksAsync`. Save computes property diffs and calls `UpdateAsync(propertiesToSet, propertiesToRemove)`; modal also exposes Delete-with-confirm. Re-reads on `RefreshSignal` bumps. |
| `ChapterBeats.razor` (+ `.razor.css`) | Inline beats expander rendered inside each chapter row. Loads beats via `IEntityService.ListAsync(projectId, "Event", chapterId)` ordered by `Order`, supports inline-edit (name + autosizing summary textarea), drag-reorder (calls `ReorderAsync`), `+ Add beat` and delete-with-confirm. Re-reads on `RefreshSignal` bumps. |
| `OutlineChatPanel.razor` (+ `.razor.css`, `.razor.js`) | Multi-turn collaborative chat UI for Outline. Loads/creates the project's `OutlineConversation`, renders bubbles/tool chips, streams turn updates, exposes a persisted `Review edits` toggle, and opens `PendingAiChangesModal` for queued AI changes. |
| `PendingAiChangesModal.razor` (+ `.razor.css`) | Durable AI change review modal reused by Outline Chat and AI Console: groups queued tool changes by resource, shows dependency/cascade warnings, renders inline or side-by-side PR-style diffs with JSON fallback, and supports Keep/Reject per group or batch with a rejection note. |

### Components/Pages/Projects/WritingSample/

| File | Description |
|------|-------------|
| `WritingSampleContent.razor` (+ `.razor.css`) | Top-level Writing Sample tab orchestrator. Three-pane CSS-grid layout (coach chat | sample editor | sample list), loads project-scoped samples, autosaves the active sample body through the shared editor JS bridge, supports title edits, and flushes pending edits before sample switches or coach sends. |
| `WritingCoachPanel.razor` (+ `.razor.css`, `.razor.js`) | Resettable project-level Writing Coach chat UI. Mirrors the Outline chat bubble/tool-chip rendering for read-only coach tools while exposing no mutation/review controls. |
| `WritingSampleListPanel.razor` (+ `.razor.css`) | Right-side sample manager with clickable active rows, excerpts, updated timestamps, `New`, and delete-with-confirm. |

### Components/Pages/Settings/

| File | Description |
|------|-------------|
| `Providers.razor` | LLM provider configuration UI: Codex OAuth connect, parent providers + child models, set default, inline edit, model test. |

### Models/

| File | Description |
|------|-------------|
| `AuthType.cs` | Enum: None, ApiKey, OAuth. |
| `LlmProvider.cs` | EF entity for an LLM endpoint/model row. Supports parent/child credential sharing via `CredentialSourceId`. |
| `OAuthToken.cs` | EF entity holding access/refresh tokens for an OAuth-backed provider. |
| `Project.cs` | EF entity scoping all narrative data. Stable `Slug` for URLs; owns `SystemPrompt`, `IncludeCurrentChapterInContext`, persisted `AiChangeApprovalEnabled`, and child navigation collections including writing samples/coach conversations. Project-level story facts live in `ProjectFact` graph nodes. |
| `Act.cs` | EF entity for a top-level outline grouping (Title/Synopsis/Order) under a `Project`. Cascade-deleted with the project. Owned chapters survive act deletion (FK `OnDelete.SetNull`). |
| `Chapter.cs` | EF entity for a chapter (Title/Body/Synopsis/Order) under a `Project`, optionally assigned to an `Act` via nullable `ActId`. `Order` is scoped to the chapter's act bucket (or the project-level Unassigned bucket when `ActId` is null). Tracks `VectorIndexState` (UpToDate/Stale/Failed) + `VectorIndexedAt` + `VectorIndexError`; `VectorSourceId` returns the stable vector-store source id (`Id.ToString("N")`). |
| `AiConsoleEntry.cs` | EF entity for one AI Console turn: command, system-prompt snapshot, JSON tool-call timeline, response text, status (`Pending`/`Completed`/`Failed`/`Cancelled`). Cascade-deleted with its `Project`. |
| `EditorContextPreference.cs` | EF entity for per-chapter Context Feed include/exclude preferences keyed by context item kind + stable item key. |
| `OutlineConversation.cs` | EF entity — one persistent multi-turn collaborative chat per `Project` (unique on `ProjectId`). Owns ordered `OutlineMessage`s; cascade-deleted with the project. |
| `OutlineMessage.cs` | EF entity for a single chat row in an `OutlineConversation`: monotonic `Order`, `OutlineMessageRole` (System/User/Assistant/Tool), text `Content`, JSON `ToolCallsJson` for assistant function-calls, `ToolCallId` + `ToolName` for tool results, `OutlineMessageStatus` (Pending/Completed/Failed/Cancelled), optional `ErrorMessage`. |
| `WritingSample.cs` | EF entity for a project-scoped prose sample used as a future style reference. Stores title/body plus created/updated timestamps. |
| `WritingCoachConversation.cs` | EF entity — one resettable Writing Coach transcript per `Project` (unique on `ProjectId`). Owns ordered `WritingCoachMessage`s; cascade-deleted with the project. |
| `WritingCoachMessage.cs` | EF entity for a single Writing Coach chat row with monotonic `Order`, role (`System`/`User`/`Assistant`/`Tool`), text content, assistant `ToolCallsJson`, tool result metadata, status, optional error, and creation timestamp. |
| `AiChangeBatch.cs` | EF entity grouping AI-proposed tool mutations from one assistant turn while they await approval/resolution. |
| `AiChange.cs` | EF entity for one queued AI tool mutation: tool metadata, before/after/result JSON, dependency metadata, status, rejection/error notes, timestamps. |
| `IngestSource.cs` | EF entity for one ingested source: full source text, source metadata/instructions, content hash, and independent vector-index state/source id. |
| `IngestSourceChunk.cs` | EF entity for a large logical source chunk used as extraction checkpoint; tracks character bounds, token count metadata, summaries, and structure status. |
| `IngestVectorFragment.cs` | EF entity mapping small retrieval vector fragments back to an ingest source with vector row id, char bounds, and metadata. |
| `IngestJob.cs` | EF entity for durable async ingest job state, progress counters, selected provider/model snapshot, encoding metadata, and source/job relationships. |
| `IngestJobChunk.cs` | EF entity for per-source-chunk ingest processing status, timestamps, errors, and created item counters. |
| `IngestReportItem.cs` | EF entity for the live/final ingest report: created/updated entities, relationships, source-chunk notes, evidence, graph ids, status, and payload JSON. |
| `IngestJobEvent.cs` | EF entity for ingest progress/debug events such as tool calls and failures. |
| `GraphNode.cs` | Generic graph node: `(ProjectId, NodeType, Key)` unique, JSON properties bag. Cascade-deleted with its `Project`. |
| `GraphEdge.cs` | Directed edge between graph nodes with type, JSON properties, optional relationship-specific `SortOrder`, and timestamps. |
| `GraphEntityType.cs` | Lightweight project-scoped graph type registry entry for UI/LLM labels/defaults. Descriptive rather than restrictive; arbitrary node types remain valid. |

### Persistence/

| File | Description |
|------|-------------|
| `AppDbContext.cs` | EF Core context for projects, outline/chat, writing samples/coach chat, graph, AI console, editor context preferences, AI change approval, and ingest queues. JSON value converter shared by graph property bags; configures relationships/indexes and retries transient SQLite lock save failures. |
| `PersistenceServiceCollectionExtensions.cs` | `AddLorekeeperPersistence` switch on `Persistence:Provider` (SQLite today; Postgres slot for future); applies shared SQLite timeout settings. |
| `SqliteConnectionSettings.cs` | Shared SQLite connection-string and startup PRAGMA settings: busy timeout, WAL journal mode, and normal synchronous mode to reduce local lock contention. |
| `Migrations/` | EF Core migrations (`InitialSchema`, `AddProjects`, `AddChapters`, `AddSystemPromptAndAiConsole`, `AddOutline`, `AddOutlineConversations`, graph/entity and AI approval migrations, `MoveProjectMetadataToProjectFacts`, `AddIngest`, `AddIngestJobProvider`, `AddWritingSamples`, `AddWritingCoachToolMessages`, `AddEditorContextPreferences`). |

### Persistence/Repositories/

| File | Description |
|------|-------------|
| `ILlmProviderRepository.cs` / `LlmProviderRepository.cs` | CRUD + atomic `SetDefaultAsync` for `LlmProvider`. |
| `IOAuthTokenRepository.cs` / `OAuthTokenRepository.cs` | Latest/valid token lookup + replace-for-provider. |
| `IProjectRepository.cs` / `ProjectRepository.cs` | Project CRUD; slug uniqueness check; ordered list by `UpdatedAt`. |
| `IGraphNodeRepository.cs` / `GraphNodeRepository.cs` | Node CRUD plus project-scoped `Find(projectId, nodeType, key)`, type-agnostic `FindByKeyAsync(projectId, key)`, and `ListByTypeAsync(projectId, nodeType)` (ordered by Label/Key). |
| `IGraphEdgeRepository.cs` / `GraphEdgeRepository.cs` | Edge CRUD plus directional adjacency query. Defines `EdgeDirection` enum. |
| `IGraphEntityTypeRepository.cs` / `GraphEntityTypeRepository.cs` | Project-scoped CRUD for lightweight graph type registry rows. |
| `IChapterRepository.cs` / `ChapterRepository.cs` | Chapter CRUD ordered by `Order`; `ListStaleAsync` for background reindex sweep; `GetMaxOrderAsync(projectId, actId)` and `ReorderAsync(projectId, actId, ids)` are scoped to a single act bucket (pass `actId == null` for the unassigned bucket). |
| `IActRepository.cs` / `ActRepository.cs` | Act CRUD ordered by `Order` per project; `ReorderAsync` rewrites the act ordering in one save. |
| `IOutlineConversationRepository.cs` / `OutlineConversationRepository.cs` | Persistence for `OutlineConversation` + ordered `OutlineMessage`s: `GetByProjectIdAsync`, `LoadMessagesAsync`, `GetMaxOrderAsync`, `AddConversationAsync`, `AddMessageAsync`, `UpdateMessage`, `RemoveConversation`. |
| `IWritingSampleRepository.cs` / `WritingSampleRepository.cs` | Project-scoped writing sample persistence: list by project (newest updated first), get/count, add/update/remove, and save. |
| `IWritingCoachConversationRepository.cs` / `WritingCoachConversationRepository.cs` | Persistence for the resettable project-level Writing Coach conversation + ordered messages, including assistant tool-call manifests and tool result rows. |
| `IAiChangeRepository.cs` / `AiChangeRepository.cs` | Persistence for pending AI change batches and changes, including eager-loaded pending batch listing and change lookup for approval actions. |
| `IEditorContextPreferenceRepository.cs` / `EditorContextPreferenceRepository.cs` | Persistence for active-chapter Context Feed include/exclude preferences, scoped by project, chapter, item kind, and item key. |
| `IIngestRepository.cs` / `IngestRepository.cs` | Persistence for ingest sources, source chunks, vector fragments, jobs, job chunks, report items, and job events, including project source listing, tracked processor reads, and lightweight no-tracking UI projections/excerpts. |

### Knowledge/

| File | Description |
|------|-------------|
| `IVectorStore.cs` | Vector storage abstraction over a `scopeKey` namespace (store/search/multi-search/delete); search results expose row id, metadata, and chunk/vector-fragment index. |
| `SqliteVecVectorStore.cs` | sqlite-vec implementation; uses `Embeddings:Dimensions` (768 for nomic-embed-text) and returns vector row/provenance metadata. |
| `VectorStoreInitializer.cs` | Creates `knowledge_chunks` + `vec_knowledge` virtual table outside EF migrations to keep abstraction portable. |
| `IGraphStore.cs` | High-level graph API: upsert nodes/edges (including optional edge `SortOrder`), neighbors (BFS, depth-bounded), path-finding. Defines `GraphDirection`, `GraphTraversalOptions`, `GraphPath`, `GraphPathHop`. |
| `RelationalGraphStore.cs` | Relational-table implementation backed by node/edge repositories; structural edge upserts preserve/update `SortOrder`; doc-comments describe contract any future backend must honor. |
| `ITextChunker.cs` / `OverlappingTextChunker.cs` | Sliding-window text chunker. Defaults `Embeddings:ChunkSize`=1200, `Embeddings:ChunkOverlap`=200. Prefers paragraph/sentence/whitespace boundaries within ±10% of target. |

### Llm/

| File | Description |
|------|-------------|
| `IEmbeddingService.cs` / `OllamaEmbeddingService.cs` | Embedding abstraction; Ollama `/api/embed`. Truncate-and-warn at `MaxChunkChars` (chunker TODO). |
| `ILlmProviderService.cs` / `LlmProviderService.cs` | CRUD over providers + `GetEffectiveApiKeyAsync` that walks `CredentialSourceId` and resolves OAuth tokens. |
| `ICodexAuthService.cs` / `CodexAuthService.cs` | OpenAI Codex PKCE OAuth flow (start, handle callback, refresh, revoke). Uses in-process pending state map. |
| `ReasoningContent.cs` | `AIContent` subclass for Codex reasoning summary streaming. |
| `CodexChatClient.cs` | `IChatClient` implementation for Codex Responses API (SSE parser, function-calling, strict-schema enforcement). |
| `IChatClientFactory.cs` / `ChatClientFactory.cs` | Constructs an `IChatClient` per provider (Codex vs OpenAI-compatible) and exposes `TestModelAsync`. |
| `AssistantWorkflowInstructions.cs` | Code-owned, non-editable AI workflow/tool-use instructions appended to project guidance and reused by Outline chat. |
| `SeedSystemPrompt.cs` | Hardcoded default system prompt seeded into every newly-created `Project`. |

### Auth/

| File | Description |
|------|-------------|
| `CodexOAuthEndpoints.cs` | Minimal-API endpoints: `GET /auth/start/{providerId}` and `GET /auth/callback`. |

### Projects/

| File | Description |
|------|-------------|
| `IProjectService.cs` / `ProjectService.cs` | Project CRUD facade. `CreateAsync` slugifies the name (collision-free via `-2`/`-3` suffix), seeds `SystemPrompt` from `SeedSystemPrompt.Default`, and syncs the Project graph node/type defaults. `RenameAsync` updates the graph projection. `UpdateSystemPromptAsync` (rejects empty) and `SetIncludeCurrentChapterAsync` back the Context Feed edits. `DeleteAsync` wipes vector chunks (`IVectorStore.DeleteByScopeAsync`) before EF-cascading the project + child graph + chapter rows. Slug stable across renames. |

### Writing/

| File | Description |
|------|-------------|
| `IWritingSampleService.cs` / `WritingSampleService.cs` | UI-facing facade for project-scoped writing samples: create/list/get/update/delete, title validation, sample body persistence, and project `UpdatedAt` touches. |
| `IWritingCoachService.cs` / `WritingCoachService.cs` | Resettable project-level Writing Coach chat service. Mirrors the Outline chat streaming/tool-call loop with a coach prompt and read-only tool set; persists assistant tool calls plus tool-result rows. |
| `WritingCoachTools.cs` | Read-only Writing Coach tool builder. Exposes `read_current_section` for the latest editor draft and `list_project_facts` for graph-backed ProjectFact context. |
| `WritingCoachTurnUpdate.cs` | Streaming update records consumed by the Writing Coach panel: text deltas, tool-call start/completion, assistant completion, and turn errors/cancellation. |

### Context/

| File | Description |
|------|-------------|
| `IContextBuilder.cs` / `ContextBuilder.cs` | Async context assembly for the Context Feed and AI console prompt. Builds keyed `ContextItem`s for guidance, current chapter, outline, facts, writing samples, selected/auto-related entities, and selected structural references; `Assemble()` remains the literal system message sent to the LLM. |
| `IEditorContextService.cs` | Editor context facade extending `IContextBuilder`; persists per-chapter context item inclusion and exposes auto/included entity/context key sets for recommendations. |
| `IContextRecommendationService.cs` / `ContextRecommendationService.cs` | Produces active-chapter context recommendations from second-degree graph links, direct context-vector hits, and manual search across entities plus structural references. |
| `IContextIndexingService.cs` / `ContextIndexingService.cs` | Maintains direct vector rows for addable context items: graph entities, chapters, acts, ingest sources, and ingest source chunks. |
| `ContextIndexBackfillWorker.cs` | One-shot startup worker that backfills context vectors for existing projects while logging embedding failures without blocking app startup. |
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
| `IIngestService.cs` / `IngestService.cs` | Application service for ingest job lifecycle/UI reads/provider resolution/queueing/notifications; restart/delete subtracts source-scoped graph assertions and only removes ingest-owned orphan graph output. |
| `IIngestSourceStructureBuilder.cs` / `IngestSourceStructureBuilder.cs` | Splits raw source text into logical source chunks using headings/scene breaks, then merges adjacent small sections up to the reusable token budget; source chunks are independent from vector fragments. |
| `IIngestGraphSync.cs` / `IngestGraphSync.cs` | Projects ingest sources and source chunks into structural graph nodes and ordered `HasChild` edges. |
| `IIngestGraphCleanup.cs` / `IngestGraphCleanup.cs` | Source-scoped graph cleanup for ingest restart/delete: subtracts one source's node/edge assertions and provenance, deleting only ingest-owned orphan output. |
| `IIngestJobQueue.cs` / `IngestJobQueue.cs` | In-process queue plus cancellation registry for durable ingest jobs. |
| `IIngestJobNotifier.cs` / `IngestJobNotifier.cs` | In-process pub/sub for live ingest job update signals consumed by Blazor Server components over the existing SignalR circuit. |
| `IngestUiModels.cs` | Lightweight read-model records for the Ingest tab: job summaries, selected job detail, chunk progress, report items, events, and bounded source excerpts. |
| `IngestSourceAssertions.cs` | Shared helper/model for protected source-scoped node/edge assertion JSON, ingest-created graph origin markers, report graph-action payloads, and source-subtraction operations. |
| `IngestJobWorker.cs` | Hosted background worker that marks interrupted jobs stopped at startup and drains queued ingest jobs in scoped processors. |
| `IngestJobProcessor.cs` | Runs one ingest job with the job-selected provider: vectorizes the full source into independent retrieval fragments, processes each source chunk with the LLM, invokes ingest tools, records progress/tool warning events, and notifies live UI listeners. |
| `IngestAgentTools.cs` | Ingest LLM tools for project entity candidate search, source-scoped observations on new/existing entities, source-scoped relationship assertions, and source-chunk notes/provenance. |

### Graph/

| File | Description |
|------|-------------|
| `IProjectGraphService.cs` | Graph UI application service contract for project-wide snapshots plus guarded node, parent, type, and relationship mutations. |
| `ProjectGraphModels.cs` | DTOs and request records used by the Graph tab service/component boundary. |
| `ProjectGraphService.cs` | Graph UI facade over repositories and domain services; exports whole-project graph snapshots and routes structural edits through Project/Act/Chapter/ProjectFact services. |

### AiConsole/

| File | Description |
|------|-------------|
| `AiConsoleOptions.cs` | Bound from the `AiConsole` config section. `MaxToolIterations` caps the tool-call loop. |
| `IAiConsoleService.cs` / `AiConsoleService.cs` | Stateless one-shot AI turn: assembles system prompt via `IContextBuilder`, resolves default `LlmProvider`, creates approval staging contexts when Review edits is enabled, runs a tool-call loop with cancellation support, and persists an `AiConsoleEntry` timeline. Defines `AiConsoleContext` and `AiToolCallRecord`. |
| `IAiConsoleHistoryService.cs` / `AiConsoleHistoryService.cs` | Project-scoped read API for persisted `AiConsoleEntry` rows. |
| `AiConsoleTools.cs` | `AIFunction` definitions exposed to the LLM: editor context, semantic search, outline/entity/fact reads, graph neighbor traversal, chapter reads, staged-or-direct line-based `edit_chapter`, and reused outline/entity mutation tools. Closures capture per-request `AiConsoleContext`. |
| `AiConsoleChangeStagingContext.cs` | AI Console staging helper for chapter-body edits; creates pending `AiChange` rows when Review edits is enabled so approved changes apply through the shared approval service. |

### Outline/

| File | Description |
|------|-------------|
| `IActService.cs` / `ActService.cs` | Act CRUD facade. `CreateAsync` auto-orders to the end. `DeleteAsync` lets the FK demote owned chapters to Unassigned (`OnDelete.SetNull`). Touches `Project.UpdatedAt` on every mutation and keeps Act graph nodes/structural edges synchronized. |
| `IOutlineCollaborationService.cs` / `OutlineCollaborationService.cs` | Multi-turn collaborative outline chat. Streams LLM text/tool updates, persists chat history, stages mutating tool calls when project approval is enabled, blocks new turns while pending changes remain, and instructs the LLM to persist project-level truths as `ProjectFact` graph nodes. |
| `OutlineCollaborationTools.cs` | `AIFunction` definitions exposed to the outline LLM. `list_outline` includes `projectFacts`; ProjectFact creation uses generic entity tools and is parented to the Project graph node. Read/mutating tools either operate directly or route through `OutlineToolStagingContext` so approval-mode turns see staged changes as current state. |
| `IOutlineChangeApprovalService.cs` / `OutlineChangeApprovalService.cs` | Applies or rejects queued AI changes, including outline/entity mutations and AI Console chapter-body edits; enforces dependency application/rejection cascading and writes hidden system messages for rejected changes. |
| `OutlineToolStagingContext.cs` | Per-turn working snapshot for approval mode: overlays staged acts, chapters, ProjectFact/entities, reorders, and links; persists `AiChange` rows with dependency metadata. |
| `OutlineChangePayloads.cs` | JSON payload records shared by staging and approval application for acts, chapters, entities, links, and reorders. |
| `AiChangeReviewDiffBuilder.cs` | Builds single-change and grouped review diff models from pending AI changes, including fuzzy line alignment and intraline highlights for the pending-change modal. |
| `IEntityService.cs` / `EntityService.cs` | Single contract for every story-graph entity (Characters, Locations, Events/beats, ...). Entities persist as `GraphNode`s via `IGraphStore` with `NodeType=type`, `Key=Guid.NewGuid().ToString("N")`, `Label=name`, free-form `Properties`. When `parentId` is set, a `HasChild` edge from the parent stores relationship-specific `SortOrder` as the sole child-order source. `UpdateAsync` mutates the loaded node in place via the node repo to avoid `IGraphStore.UpsertNodeAsync`'s replace-merge semantics. `ListLinksAsync` returns all adjacent edges (both directions, every edge type incl. `HasChild`) with edge id, sort order, properties, and other endpoint metadata. Returns the public `StoryEntity` projection. |
| `IEntityTypeService.cs` / `EntityTypeService.cs` | Lightweight graph type registry facade. Seeds structural/default types (`Project`, `Act`, `Chapter`, `ProjectFact`, `Event`, `Character`, `Location`), discovers arbitrary node types, and creates custom non-structural types for the side panel. |
| `IProjectFactService.cs` / `ProjectFactService.cs` | Project-level graph fact facade. Stores one `ProjectFact` graph node per key/value pair, ensures a Project → ProjectFact `HasChild` edge, enforces case-insensitive key upserts, touches `Project.UpdatedAt`, and projects linked graph entities for UI/prompt display. |
| `IOutlineGraphSync.cs` / `OutlineGraphSync.cs` | Synchronizes the EF outline spine into graph nodes and `HasChild` edges: Project → Acts / unassigned Chapters, Act → Chapters, Chapter → Events. Used by project/act/chapter services and startup repair. |
| `OutlineTurnUpdate.cs` | `[JsonDerivedType]`-decorated abstract record for streaming chat updates: `TextDelta`, `ToolCallStarted`, `ToolCallCompleted`, `AssistantMessageCompleted`, `OutlineMutated`, `TurnError`. |

### Chapters/

| File | Description |
|------|-------------|
| `IChapterService.cs` / `ChapterService.cs` | Chapter CRUD facade. `CreateAsync(projectId, actId?, ...)` stamps a new chapter into a chosen act bucket (or Unassigned) and syncs the Chapter graph node/edge. `UpdateAsync` accepts an optional `ChapterActAssignment` wrapper to MOVE the chapter between act buckets (appended to the destination), marks `VectorIndexState=Stale` on body changes, notifies `IStaleChapterNotifier`, and updates the graph projection. `ReorderAsync(projectId, actId?, ids)` reorders within a single bucket and repairs structural graph edge order. `ReindexAsync` deletes prior vector chunks (`source_type="chapter"`) and rewrites them via `ITextChunker` + `IEmbeddingService`. `DeleteAsync` removes vectors AND removes the chapter's graph node + any `HasChild` entity children before EF delete (keeps `IGraphStore` consistent without depending on `IEntityService`). |
| `IStaleChapterNotifier.cs` / `StaleChapterNotifier.cs` | In-process unbounded `Channel<Guid>` pub/sub of chapters needing reindex. |
| `StaleChapterReindexer.cs` | `BackgroundService` that sweeps existing stale chapters at startup and drains `IStaleChapterNotifier` thereafter, calling `IChapterService.ReindexAsync` in a fresh DI scope per chapter. |

### wwwroot/

| File | Description |
|------|-------------|
| `app.css` | App-wide CSS. |
| `js/autosizeTextareas.js` | Small shared JS module that attaches to `textarea[data-autosize]`, grows each textarea to its `scrollHeight`, refreshes on input/change and width changes, and prevents nested textarea scrollbars. |
| `favicon.png` | Site icon. |
| `lib/bootstrap/` | Vendored Bootstrap distribution. |
| `lib/vis-network/` | Vendored `vis-network` browser graph renderer assets and license files used by the Graph tab. |

