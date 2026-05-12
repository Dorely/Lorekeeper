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
| `Program.cs` | Host setup, Blazor Interactive Server hub sizing, DI for persistence/knowledge/LLM/token/ingest/import-export/writing/editor-chat/contest services, EF migrate at startup, sqlite-vec init, outline graph repair, Codex OAuth endpoints. |
| `appsettings.json` / `appsettings.Development.json` | Configuration: `ConnectionStrings:DefaultConnection`, `Persistence:Provider`, `Blazor:*`, `Ingest:Sectioning:*`, `Embeddings:*`, `Agents:*`. |
| `Properties/launchSettings.json` | Local launch profiles (HTTP pinned to `localhost:1455` for Codex OAuth redirect). |

### Components/

| File | Description |
|------|-------------|
| `App.razor` | Root component: `<html>` shell, head outlet, scripts. |
| `Routes.razor` | `<Router>` wiring `MainLayout` and `NotFound`. |
| `_Imports.razor` | Shared `@using` directives for all components. |

### Components/Chat/

| File | Description |
|------|-------------|
| `ChatModels.cs` | Shared chat UI view models for persisted/live messages, text parts, tool-call chips, and transcript token-count helpers. |
| `ChatSurface.razor` (+ `.razor.css`, `.razor.js`) | Reusable chat shell for transcript rendering, live-turn rendering, composer controls, scrolling, and textarea autosize behavior. |
| `ChatToolChipView.razor` (+ `.razor.css`) | Reusable expandable tool-call card that shows streamed arguments, result/error details, and in-progress state. |

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
| `EditorContent.razor` (+ `.razor.css`, `.razor.js`) | Three-column context-aware chapter editor: project-wide editor chat left, full-height JS-debounced line-numbered editor center, and right-column Recommended Context/Context Feed. Persists/reindexes body edits, remembers the selected chapter per browser/project, locks while AI runs, and refreshes editor/context/recommendations after AI turns or approved changes. |
| `EditorChatPanel.razor` (+ `.razor.css`) | Editor chat domain adapter over shared `ChatSurface`: loads the project's `EditorConversation`, streams text/tool/contest updates, exposes Review edits plus Contest Mode controls, and opens review modals for queued AI changes or candidates. |
| `ContestReviewModal.razor` (+ `.razor.css`) | Editor Contest Mode comparison modal: shows mutually-exclusive model candidates with chapter-body diffs, streams status/raw Candidate JSON from contest batches, and stages the selected candidate into the normal pending AI change review flow. |
| `RecommendedContextPanel.razor` (+ `.razor.css`) | Editor right-column context recommender: shows semantic/manual/graph-proximity recommendations for entities plus structural references, and adds them to the active chapter's persisted context working set. |
| `ContextFeedPanel.razor` (+ `.razor.css`) | Editable Context Feed: async collapsible cards for project guidance, current chapter, outline, facts, selectable writing samples, selected entities, and selected structural references. Uses stable item keys and `IEditorContextService` to persist per-chapter include/exclude choices. |
| `GraphPage.razor` | Graph tab at `/projects/{Slug}/graph`; wraps the project shell and hosts the interactive graph workspace. |
| `GraphContent.razor` (+ `.razor.css`, `.razor.js`) | Obsidian-inspired full-project graph workspace: loads graph snapshots, filters/searches nodes, bridges to the local `vis-network` renderer for pan/zoom/drag/select, and coordinates graph refreshes. |
| `GraphDetailsPanel.razor` (+ `.razor.css`) | Selected-node graph editor side panel: create/edit/delete nodes, edit safe parent assignments, and create/edit/delete custom relationships while managed links stay protected. |
| `IngestPage.razor` | Ingest tab at `/projects/{Slug}/ingest`; wraps `ProjectLayout` and hosts `Ingest.IngestContent`. |
| `ImportExportPage.razor` | Import / Export tab at `/projects/{Slug}/import-export`; wraps `ProjectLayout` and hosts `ImportExport.ImportExportContent`. |
| `OutlinePage.razor` | Outline tab route; wraps `ProjectLayout` + `Outline.OutlineContent`. |
| `WritingSamplePage.razor` | Writing Sample tab at `/projects/{Slug}/writing-sample`; wraps `ProjectLayout` + `WritingSample.WritingSampleContent`. |

### Components/Pages/Projects/Ingest/

| File | Description |
|------|-------------|
| `IngestContent.razor` (+ `.razor.css`) | Functional Ingest tab workspace: creates text/file ingest jobs with configured-model selection and optional instructions, shows start-readiness feedback, subscribes to live job updates, and supports stop/resume-with-model-change/restart/delete. |

### Components/Pages/Projects/ImportExport/

| File | Description |
|------|-------------|
| `ImportExportContent.razor` (+ `.razor.css`) | Combined project Import / Export workspace: downloads Full/Non-structural graph JSON and TXT/Markdown manuscript exports; uploads Lorekeeper export JSON, queues import jobs, subscribes to live job updates, and shows progress/report history. |

### Components/Pages/Projects/Outline/

| File | Description |
|------|-------------|
| `OutlineContent.razor` (+ `.razor.css`) | Top-level Outline tab orchestrator. Three-pane CSS-grid layout (chat | outline tree | facts+entities side column) inside a fixed-height grid; each direct column wrapper is an independent `overflow-y: auto` scroll container, with horizontal overflow rather than responsive stacking when space is tight. Bumps a `_refreshSignal` int on every reload that child panels watch to re-read after tool turns. Re-fetches the project from `IProjectRepository` on every reload. |
| `ProjectFactsPanel.razor` (+ `.razor.css`) | Editable project facts block surfaced at the top of the right side column. Reads/writes `ProjectFact` graph nodes via `IProjectFactService`, keeps the compact collapsible key/value UX, supports add/edit/delete, shows linked graph entities, and autosizes fact textareas via `wwwroot/js/autosizeTextareas.js`. |
| `IngestSourcesPanel.razor` (+ `.razor.css`) | Read-only Outline side panel for structural ingest Source → SourceChunk graph nodes, showing chunk summaries/notes and entities linked by `ExtractedFrom` provenance edges. |
| `OutlineTree.razor` (+ `.razor.css`) | Hierarchical Acts → Chapters tree. Acts are collapsible, drag-reorderable groups with inline-editable title/synopsis and `+ Chapter` / Delete (chapters fall back to Unassigned via `OnDelete.SetNull`). Act/chapter synopsis textareas autosize to their content via `wwwroot/js/autosizeTextareas.js`. Chapters are inline-editable rows with a beats-toggle caret + count badge (renders `ChapterBeats` inline when expanded), stale/failed vector-index badge, drag-reorder within their act bucket, an act-picker `<select>` for cross-act moves, Open link, and delete-with-confirm. Re-fetches per-chapter beat counts via `IEntityService.CountChildrenAsync` whenever `RefreshSignal` bumps. |
| `EntitiesPanel.razor` (+ `.razor.css`) | Registry-driven project-scoped entities side panel. Lists non-structural graph types from `IEntityTypeService`, supports `+ Type`, per-type `+ Add`, clickable entity rows, and a Bootstrap-style modal with editable name/properties plus read-only adjacent graph links via `IEntityService.ListLinksAsync`. Save computes property diffs and calls `UpdateAsync(propertiesToSet, propertiesToRemove)`; modal also exposes Delete-with-confirm. Re-reads on `RefreshSignal` bumps. |
| `ChapterBeats.razor` (+ `.razor.css`) | Inline beats expander rendered inside each chapter row. Loads beats via `IEntityService.ListAsync(projectId, "Event", chapterId)` ordered by `Order`, supports inline-edit (name + autosizing summary textarea), drag-reorder (calls `ReorderAsync`), `+ Add beat` and delete-with-confirm. Re-reads on `RefreshSignal` bumps. |
| `OutlineChatPanel.razor` (+ `.razor.css`) | Outline chat domain adapter over shared `ChatSurface`. Loads/creates the project's `OutlineConversation`, streams text/tool updates, exposes a persisted Review edits toggle, and opens `PendingAiChangesModal` for queued AI changes. |
| `PendingAiChangesModal.razor` (+ `.razor.css`) | Durable AI change review modal reused by Outline Chat and Editor Chat: groups queued tool changes by resource, shows dependency/cascade warnings, renders inline or side-by-side PR-style diffs with JSON fallback, and supports Keep/Reject per group or batch with a rejection note. |

### Components/Pages/Projects/WritingSample/

| File | Description |
|------|-------------|
| `WritingSampleContent.razor` (+ `.razor.css`) | Top-level Writing Sample tab orchestrator. Three-pane CSS-grid layout (coach chat | sample editor | sample list), loads project-scoped samples, autosaves the active sample body through the shared editor JS bridge, supports title edits, and flushes pending edits before sample switches or coach sends. |
| `WritingCoachPanel.razor` (+ `.razor.css`) | Writing Coach domain adapter over shared `ChatSurface`. Streams resettable project-level coaching text and read-only tool cards while exposing no mutation/review controls. |
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
| `Project.cs` | EF entity scoping all narrative data. Stable `Slug` for URLs; owns `SystemPrompt`, `IncludeCurrentChapterInContext`, persisted `AiChangeApprovalEnabled`, Contest Mode toggle/model slots, and child navigation collections including conversations, contests, and writing samples. Project-level story facts live in `ProjectFact` graph nodes. |
| `Act.cs` | EF entity for a top-level outline grouping (Title/Synopsis/Order) under a `Project`. Cascade-deleted with the project. Owned chapters survive act deletion (FK `OnDelete.SetNull`). |
| `Chapter.cs` | EF entity for a chapter (Title/Body/Synopsis/Order) under a `Project`, optionally assigned to an `Act` via nullable `ActId`. `Order` is scoped to the chapter's act bucket (or the project-level Unassigned bucket when `ActId` is null). Tracks `VectorIndexState` (UpToDate/Stale/Failed) + `VectorIndexedAt` + `VectorIndexError`; `VectorSourceId` returns the stable vector-store source id (`Id.ToString("N")`). |
| `EditorContextPreference.cs` | EF entity for per-chapter Context Feed include/exclude preferences keyed by context item kind + stable item key. |
| `EditorConversation.cs` | EF entity — one persistent multi-turn editor chat per `Project` (unique on `ProjectId`). Owns ordered `EditorMessage`s; cascade-deleted with the project. |
| `EditorMessage.cs` | EF entity for a single row in an `EditorConversation`: monotonic `Order`, role (`System`/`User`/`Assistant`/`Tool`), text content, assistant tool-call JSON, tool result metadata, status, optional error, and creation timestamp. |
| `OutlineConversation.cs` | EF entity — one persistent multi-turn collaborative chat per `Project` (unique on `ProjectId`). Owns ordered `OutlineMessage`s; cascade-deleted with the project. |
| `OutlineMessage.cs` | EF entity for a single chat row in an `OutlineConversation`: monotonic `Order`, `OutlineMessageRole` (System/User/Assistant/Tool), text `Content`, JSON `ToolCallsJson` for assistant function-calls, `ToolCallId` + `ToolName` for tool results, `OutlineMessageStatus` (Pending/Completed/Failed/Cancelled), optional `ErrorMessage`. |
| `WritingSample.cs` | EF entity for a project-scoped prose sample used as a future style reference. Stores title/body plus created/updated timestamps. |
| `WritingCoachConversation.cs` | EF entity — one resettable Writing Coach transcript per `Project` (unique on `ProjectId`). Owns ordered `WritingCoachMessage`s; cascade-deleted with the project. |
| `WritingCoachMessage.cs` | EF entity for a single Writing Coach chat row with monotonic `Order`, role (`System`/`User`/`Assistant`/`Tool`), text content, assistant `ToolCallsJson`, tool result metadata, status, optional error, and creation timestamp. |
| `AiChangeBatch.cs` | EF entity grouping AI-proposed tool mutations from one assistant turn while they await approval/resolution. Tracks whether the owning transcript is Outline or Editor chat. |
| `AiChange.cs` | EF entity for one queued AI tool mutation: tool metadata, before/after/result JSON, dependency metadata, status, rejection/error notes, timestamps. |
| `ContestBatch.cs` | EF entity for one Editor Contest Mode run: captured turn/context snapshot, target chapter/body snapshot, operation metadata, status, and model candidates. |
| `ContestCandidate.cs` | EF entity for one model's contest proposal: provider/model labels, validated mutation JSON, proposed chapter body, raw response, status, timing, and errors. |
| `IngestSource.cs` | EF entity for one ingested source: full source text, source metadata/instructions, content hash, and independent vector-index state/source id. |
| `IngestSourceChunk.cs` | EF entity for a large logical source chunk used as extraction checkpoint; tracks character bounds, token count metadata, summaries, and structure status. |
| `IngestVectorFragment.cs` | EF entity mapping small retrieval vector fragments back to an ingest source with vector row id, char bounds, and metadata. |
| `IngestJob.cs` | EF entity for durable async ingest job state, progress counters, selected provider/model snapshot, encoding metadata, and source/job relationships. |
| `IngestJobChunk.cs` | EF entity for per-source-chunk ingest processing status, timestamps, errors, and created item counters. |
| `IngestReportItem.cs` | EF entity for the live/final ingest report: created/updated entities, relationships, source-chunk notes, evidence, graph ids, status, and payload JSON. |
| `IngestJobEvent.cs` | EF entity for ingest progress/debug events such as tool calls and failures. |
| `ProjectImportJob.cs` | EF entity for durable project import job state: uploaded JSON payload, source format metadata, status/progress counters, import counts, warnings, errors, and timestamps. |
| `ProjectImportReportItem.cs` | EF entity for import job report rows covering validation, structural appends, type/entity/relationship merges, indexing warnings, and failures. |
| `GraphNode.cs` | Generic graph node: `(ProjectId, NodeType, Key)` unique, JSON properties bag. Cascade-deleted with its `Project`. |
| `GraphEdge.cs` | Directed edge between graph nodes with type, JSON properties, optional relationship-specific `SortOrder`, and timestamps. |
| `GraphEntityType.cs` | Lightweight project-scoped graph type registry entry for UI/LLM labels/defaults. Descriptive rather than restrictive; arbitrary node types remain valid. |

### Persistence/

| File | Description |
|------|-------------|
| `AppDbContext.cs` | EF Core context for projects, outline/editor/writing chat, writing samples, graph, editor context preferences, AI change approval, and ingest queues. JSON value converter shared by graph property bags; configures relationships/indexes and retries transient SQLite lock save failures. |
| `PersistenceServiceCollectionExtensions.cs` | `AddLorekeeperPersistence` switch on `Persistence:Provider` (SQLite today; Postgres slot for future); applies shared SQLite timeout settings. |
| `SqliteConnectionSettings.cs` | Shared SQLite connection-string and startup PRAGMA settings: busy timeout, WAL journal mode, and normal synchronous mode to reduce local lock contention. |
| `Migrations/` | EF Core migrations (`InitialSchema`, project/chapter/outline/graph/ingest/writing/editor-context/import-export migrations, `ReplaceAiConsoleWithEditorChat`, `AddContestMode`, and `RemoveContestBriefFields` for Contest Mode schema cleanup). |

### Persistence/Repositories/

| File | Description |
|------|-------------|
| `ILlmProviderRepository.cs` / `LlmProviderRepository.cs` | CRUD + atomic `SetDefaultAsync` for `LlmProvider`. |
| `IOAuthTokenRepository.cs` / `OAuthTokenRepository.cs` | Latest/valid token lookup + replace-for-provider. |
| `IProjectRepository.cs` / `ProjectRepository.cs` | Project CRUD; slug uniqueness check; ordered list by `UpdatedAt`. |
| `IGraphNodeRepository.cs` / `GraphNodeRepository.cs` | Node CRUD plus project-scoped `Find(projectId, nodeType, key)`, type-agnostic `FindByKeyAsync(projectId, key)`, and `ListByTypeAsync(projectId, nodeType)` (ordered by Label/Key). |
| `IGraphEdgeRepository.cs` / `GraphEdgeRepository.cs` | Edge CRUD plus directional adjacency query. Defines `EdgeDirection` enum. |
| `IGraphEntityTypeRepository.cs` / `GraphEntityTypeRepository.cs` | Project-scoped CRUD for lightweight graph type registry rows. |
| `IChapterRepository.cs` / `ChapterRepository.cs` | Chapter CRUD ordered by `Order`; `GetMaxOrderAsync(projectId, actId)` and `ReorderAsync(projectId, actId, ids)` are scoped to a single act bucket (pass `actId == null` for the unassigned bucket). |
| `IActRepository.cs` / `ActRepository.cs` | Act CRUD ordered by `Order` per project; `ReorderAsync` rewrites the act ordering in one save. |
| `IOutlineConversationRepository.cs` / `OutlineConversationRepository.cs` | Persistence for `OutlineConversation` + ordered `OutlineMessage`s: `GetByProjectIdAsync`, `LoadMessagesAsync`, `GetMaxOrderAsync`, `AddConversationAsync`, `AddMessageAsync`, `UpdateMessage`, `RemoveConversation`. |
| `IEditorConversationRepository.cs` / `EditorConversationRepository.cs` | Persistence for project-wide `EditorConversation` + ordered `EditorMessage`s: get/create support, message loading/order lookup, add/update/remove, and save. |
| `IWritingSampleRepository.cs` / `WritingSampleRepository.cs` | Project-scoped writing sample persistence: list by project (newest updated first), get/count, add/update/remove, and save. |
| `IWritingCoachConversationRepository.cs` / `WritingCoachConversationRepository.cs` | Persistence for the resettable project-level Writing Coach conversation + ordered messages, including assistant tool-call manifests and tool result rows. |
| `IAiChangeRepository.cs` / `AiChangeRepository.cs` | Persistence for pending AI change batches and changes, including eager-loaded pending batch listing and change lookup for approval actions. |
| `IContestRepository.cs` / `ContestRepository.cs` | Persistence for Editor Contest Mode batches and candidates, including current/history project batch listing, detail loading, candidate lookup, and status updates. |
| `IEditorContextPreferenceRepository.cs` / `EditorContextPreferenceRepository.cs` | Persistence for active-chapter Context Feed include/exclude preferences, scoped by project, chapter, item kind, and item key. |
| `IIngestRepository.cs` / `IngestRepository.cs` | Persistence for ingest sources, source chunks, vector fragments, jobs, job chunks, report items, and job events, including project source listing, tracked processor reads, and lightweight no-tracking UI projections/excerpts. |
| `IProjectImportRepository.cs` / `ProjectImportRepository.cs` | Persistence for project import jobs and report items, including list/detail UI projections, queued/interrupted job lookup, and delete/save operations. |

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
| `ToolCallStreamingContent.cs` | `AIContent` subclasses for provider-level function-call start and argument-delta streaming. |
| `StreamingToolCallTracker.cs` | Normalizes provider function-call start/delta/final content into app-level started/arguments/ready updates for chat services. |
| `CodexChatClient.cs` | `IChatClient` implementation for Codex Responses API (SSE parser, function-calling, strict-schema enforcement, reasoning and tool-argument streaming). |
| `IChatClientFactory.cs` / `ChatClientFactory.cs` | Constructs an `IChatClient` per provider (Codex vs OpenAI-compatible) and exposes `TestModelAsync`. |
| `AssistantWorkflowInstructions.cs` | Code-owned, non-editable AI workflow/tool-use instructions appended to project guidance and reused by editor/outline chat, including Contest Mode preparation rules. |
| `AgentOptions.cs` | Shared tool-loop options bound from `Agents:*`; `MaxToolIterations` caps iterative tool-call rounds for chat, writing coach, and ingest agents. |
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
| `IWritingCoachService.cs` / `WritingCoachService.cs` | Resettable project-level Writing Coach chat service. Mirrors the Outline chat streaming/tool-call loop with a coach prompt and read-only tool set; streams tool arguments and persists assistant tool calls plus tool-result rows. |
| `WritingCoachTools.cs` | Read-only Writing Coach tool builder. Exposes `read_current_section` for the latest editor draft and `list_project_facts` for graph-backed ProjectFact context. |
| `WritingCoachTurnUpdate.cs` | Streaming update records consumed by the Writing Coach panel: text deltas, tool-call start/argument/completion updates, assistant completion, and turn errors/cancellation. |

### Context/

| File | Description |
|------|-------------|
| `IContextBuilder.cs` / `ContextBuilder.cs` | Async context assembly for the Context Feed and editor chat prompt. Builds keyed `ContextItem`s for guidance, current chapter, outline, facts, graph traversal map, writing samples, selected/auto-related entities, and selected structural references; `Assemble()` returns the literal system message and can override the assistant workflow block for modes like Contest preparation. |
| `IEditorContextService.cs` | Editor context facade extending `IContextBuilder`; persists per-chapter context item inclusion and exposes auto/included entity/context key sets for recommendations. |
| `IContextRecommendationService.cs` / `ContextRecommendationService.cs` | Produces active-chapter context recommendations from second-degree graph links, direct context-vector hits, and manual search across entities plus structural references. |
| `IContextIndexingService.cs` / `ContextIndexingService.cs` | Maintains targeted direct vector rows for addable context items: graph entities, chapters, acts, ingest sources, and ingest source chunks. |
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
| `IIngestService.cs` / `IngestService.cs` | Application service for ingest job lifecycle/UI reads/provider resolution/queueing/notifications; restart/delete subtracts source-scoped graph assertions, removes ingest-owned orphan graph output, and refreshes targeted context vectors. |
| `IIngestSourceStructureBuilder.cs` / `IngestSourceStructureBuilder.cs` | Splits raw source text into logical source chunks using headings/scene breaks, then merges adjacent sections with configurable target/soft token limits; source chunks are independent from vector fragments. |
| `IngestSourceStructureOptions.cs` | Configurable source sectioning defaults for ingest chunk target tokens, soft max ratio, and small-section merge threshold. |
| `IIngestGraphSync.cs` / `IngestGraphSync.cs` | Projects ingest sources and source chunks into structural graph nodes and ordered `HasChild` edges. |
| `IIngestGraphCleanup.cs` / `IngestGraphCleanup.cs` | Source-scoped graph cleanup for ingest restart/delete: subtracts one source's node/edge assertions and provenance, deleting only ingest-owned orphan output and returning affected entities for targeted context-vector cleanup. |
| `IIngestJobQueue.cs` / `IngestJobQueue.cs` | In-process queue plus cancellation registry for durable ingest jobs. |
| `IIngestJobNotifier.cs` / `IngestJobNotifier.cs` | In-process pub/sub for live ingest job update signals consumed by Blazor Server components over the existing SignalR circuit. |
| `IngestUiModels.cs` | Lightweight read-model records for the Ingest tab: job summaries, selected job detail, chunk progress, report items, events, and bounded source excerpts. |
| `IngestSourceAssertions.cs` | Shared helper/model for protected source-scoped node/edge assertion JSON, ingest-created graph origin markers, report graph-action payloads, and source-subtraction operations. |
| `IngestJobWorker.cs` | Hosted background worker that marks interrupted jobs stopped at startup and drains queued ingest jobs in scoped processors. |
| `IngestJobProcessor.cs` | Runs one ingest job with the job-selected provider: vectorizes the full source into independent retrieval fragments, processes each source chunk with the LLM, invokes ingest tools, keeps source-chunk context vectors current, records progress/tool warning events, and notifies live UI listeners. |
| `IngestAgentTools.cs` | Ingest LLM tools for project entity candidate search, source-scoped observations on new/existing entities, source-scoped relationship assertions, and source-chunk notes/provenance. |

### ImportExport/

| File | Description |
|------|-------------|
| `ProjectExportModels.cs` | Portable export DTOs/enums for Lorekeeper graph packages, stable graph node refs, and manuscript export file metadata. |
| `IProjectImportExportService.cs` / `ProjectImportExportService.cs` | UI-facing import/export facade: builds Full/Non-structural graph JSON, TXT/Markdown manuscript downloads, queues import jobs, lists/details/deletes import jobs, and emits import notifications. |
| `ManuscriptExportFormatters.cs` | Extensible manuscript formatter abstraction with TXT and Markdown implementations; preserves outline order and keeps future formats pluggable. |
| `ProjectImportUiModels.cs` | Lightweight read-model records for the Import / Export tab job list, detail view, and report rows. |
| `ProjectImportJobQueue.cs` | In-process import job queue used by the hosted worker. |
| `ProjectImportJobNotifier.cs` | In-process pub/sub for live import job updates consumed by the Blazor Import / Export tab. |
| `ProjectImportJobWorker.cs` | Hosted background worker that marks interrupted imports failed at startup and drains queued import jobs. |
| `ProjectImportJobProcessor.cs` | Runs one import job: validates export JSON, appends structural Full imports, merges non-structural entities/relationships/provenance, repairs graph links, records report rows, and refreshes indexes best-effort. |

### Graph/

| File | Description |
|------|-------------|
| `IProjectGraphService.cs` | Graph UI application service contract for project-wide snapshots plus guarded node, parent, type, and relationship mutations. |
| `ProjectGraphModels.cs` | DTOs and request records used by the Graph tab service/component boundary. |
| `ProjectGraphService.cs` | Graph UI facade over repositories and domain services; exports whole-project graph snapshots and routes structural edits plus indexable graph relationship/parent mutations through owning domain services. |

### EditorChat/

| File | Description |
|------|-------------|
| `IEditorChatService.cs` | Project-wide editor chat service contract plus per-turn `EditorChatContext` captured by editor tools, staging helpers, and Contest Mode settings/actions. |
| `EditorChatService.cs` | Persistent streaming editor chat: assembles the active Context Feed into the system prompt, resolves the default model, streams text/tool-call arguments, persists user/assistant/tool messages, stages Review edits, emits UI refresh events, and routes Contest Mode terminal tool calls into async candidate generation. |
| `EditorChatTools.cs` | Editor chat LLM tools for assembled context, semantic search, chapters, facts, entities, graph neighbors, chapter reads, normal line-based `edit_chapter`/outline mutations, and read-only Contest preparation with terminal `start_contest`. |
| `EditorChatChangeStagingContext.cs` | Editor chat staging helper for chapter-body edits; creates pending `AiChange` rows owned by the editor transcript when Review edits is enabled. |
| `EditorChatTurnUpdate.cs` | `[JsonDerivedType]`-decorated streaming update records consumed by `EditorChatPanel`: text deltas, tool start/argument/end updates, pending changes, contest progress/raw JSON, mutation refresh, assistant completion, and turn errors. |
| `EditorContestModels.cs` | DTOs and helper records for Contest Mode settings, start requests, captured chat-context snapshots, model responses, mutation JSON, and streaming contest status/raw-response updates. |
| `IEditorContestService.cs` / `EditorContestService.cs` | Contest Mode application service: persists project settings, starts terminal contest batches, runs selected models without tools, streams raw Candidate JSON, validates JSON chapter-body mutations, builds proposed bodies, and stages the selected candidate as a normal `edit_chapter` pending change. |

### Outline/

| File | Description |
|------|-------------|
| `IActService.cs` / `ActService.cs` | Act CRUD facade. `CreateAsync` auto-orders to the end. `DeleteAsync` lets the FK demote owned chapters to Unassigned (`OnDelete.SetNull`). Touches `Project.UpdatedAt`, keeps Act graph nodes/structural edges synchronized, and updates targeted act context vectors on mutations. |
| `IOutlineCollaborationService.cs` / `OutlineCollaborationService.cs` | Multi-turn collaborative outline chat. Streams LLM text/tool updates, persists chat history, stages mutating tool calls when project approval is enabled, blocks new turns while pending changes remain, and instructs the LLM to persist project-level truths as `ProjectFact` graph nodes. |
| `OutlineCollaborationTools.cs` | `AIFunction` definitions exposed to the outline LLM. `list_outline` includes `projectFacts`; ProjectFact creation uses generic entity tools and is parented to the Project graph node. Read/mutating tools either operate directly or route through `OutlineToolStagingContext` so approval-mode turns see staged changes as current state. |
| `IAiChangeApprovalService.cs` / `AiChangeApprovalService.cs` | Applies or rejects queued AI changes from outline/editor chat, including outline/entity mutations and editor chapter-body edits; enforces dependency application/rejection cascading and writes hidden correction messages to the owning transcript. |
| `OutlineToolStagingContext.cs` | Per-turn working snapshot for approval mode: overlays staged acts, chapters, ProjectFact/entities, reorders, and links; persists `AiChange` rows with dependency metadata. |
| `OutlineChangePayloads.cs` | JSON payload records shared by staging and approval application for acts, chapters, entities, links, and reorders. |
| `AiChangeReviewDiffBuilder.cs` | Builds single-change and grouped review diff models from pending AI changes, including fuzzy line alignment and intraline highlights for the pending-change modal. |
| `IEntityService.cs` / `EntityService.cs` | Single contract for every story-graph entity (Characters, Locations, Events/beats, ...). Entities persist as `GraphNode`s via `IGraphStore`; create/update/delete, parent moves, and relationship mutations refresh affected context vectors. `ListLinksAsync` returns all adjacent edges (both directions, every edge type incl. `HasChild`) with edge id, sort order, properties, and other endpoint metadata. Returns the public `StoryEntity` projection. |
| `IEntityTypeService.cs` / `EntityTypeService.cs` | Lightweight graph type registry facade. Seeds structural/default types (`Project`, `Act`, `Chapter`, `ProjectFact`, `Event`, `Character`, `Location`), discovers arbitrary node types, and creates custom non-structural types for the side panel. |
| `IProjectFactService.cs` / `ProjectFactService.cs` | Project-level graph fact facade. Stores one `ProjectFact` graph node per key/value pair, ensures a Project → ProjectFact `HasChild` edge, enforces case-insensitive key upserts, touches `Project.UpdatedAt`, and projects linked graph entities for UI/prompt display. |
| `IOutlineGraphSync.cs` / `OutlineGraphSync.cs` | Synchronizes the EF outline spine into graph nodes and `HasChild` edges: Project → Acts / unassigned Chapters, Act → Chapters, Chapter → Events. Used by project/act/chapter services and startup repair. |
| `OutlineTurnUpdate.cs` | `[JsonDerivedType]`-decorated abstract record for streaming chat updates: text deltas, tool-call start/argument/completion updates, assistant completion, outline mutation refresh, and turn errors. |

### Chapters/

| File | Description |
|------|-------------|
| `IChapterService.cs` / `ChapterService.cs` | Chapter CRUD facade. `CreateAsync(projectId, actId?, ...)` stamps a new chapter into a chosen act bucket (or Unassigned), syncs the Chapter graph node/edge, and writes chapter context vectors. `UpdateAsync` accepts an optional `ChapterActAssignment` wrapper to MOVE the chapter between act buckets, persists body/title/synopsis changes, and updates chapter/body vectors inside the service. `ReorderAsync(projectId, actId?, ids)` reorders within a single bucket, repairs structural graph edge order, and refreshes only the affected act vector. `DeleteAsync` removes vectors AND removes the chapter's graph node + any `HasChild` entity children before EF delete (keeps `IGraphStore` consistent without depending on `IEntityService`). |

### wwwroot/

| File | Description |
|------|-------------|
| `app.css` | App-wide CSS. |
| `js/autosizeTextareas.js` | Small shared JS module that attaches to `textarea[data-autosize]`, grows each textarea to its `scrollHeight`, refreshes on input/change and width changes, and prevents nested textarea scrollbars. |
| `js/fileDownloads.js` | Browser download helper used by Import / Export to save generated graph JSON and manuscript files. |
| `favicon.png` | Site icon. |
| `lib/bootstrap/` | Vendored Bootstrap distribution. |
| `lib/vis-network/` | Vendored `vis-network` browser graph renderer assets and license files used by the Graph tab. |

