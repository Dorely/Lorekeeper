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
| `Lorekeeper.csproj` | Project file: `net10.0`, nullable + implicit usings, warnings-as-errors. EF Core SQLite, Microsoft.Extensions.AI(.OpenAI), OpenAI 2.8, sqlite-vec. |
| `Program.cs` | Host setup, DI for persistence/knowledge/LLM services, EF migrate at startup, sqlite-vec init, Codex OAuth endpoints. |
| `appsettings.json` / `appsettings.Development.json` | Configuration: `ConnectionStrings:DefaultConnection`, `Persistence:Provider`, `Embeddings:*`. |
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
| `ProjectLayout.razor` (+ `.razor.css`) | Shared shell for project workspace pages: loads project by slug, renders title + horizontal tab strip (Editor / Graph / Ingest / Outline), exposes `Project` via `CascadingValue`, and wraps routed page content in a flex-bounded `.project-body` so pages like Outline can own their internal scroll regions. |
| `EditorPage.razor` | Editor tab routes (`/projects/{Slug}/editor` and `/projects/{Slug}/editor/{ChapterId:guid}`). Wraps `ProjectLayout` + `EditorContent`. |
| `EditorContent.razor` (+ `.razor.css`, `.razor.js`) | Functional chapter editor body: chapter-selector header (dropdown menu, edit-title pencil, +new-chapter button), JS-debounced (1s) auto-save textarea with line-number gutter that persists via `IChapterService` and triggers `ReindexAsync`. Save/index status surfaced inline. Hosts `ContextFeedPanel` and `AiConsolePanel`; locks/dims the textarea while an AI turn is running and refreshes the body when the AI completes. |
| `ContextFeedPanel.razor` (+ `.razor.css`) | Editable Context Feed: collapsible cards per `ContextItem` (system prompt, current chapter). Inline textarea edits the project's system prompt (rejects empty); checkbox toggles `Project.IncludeCurrentChapterInContext`. Calls `IProjectService` for persistence. |
| `AiConsolePanel.razor` (+ `.razor.css`) | Stateless AI command console: input + Send/Cancel button; raises `OnAiTurnStarting`/`OnAiTurnCompleted` so the editor can flush + lock + refresh. Shows last response below the input and a History modal listing persisted `AiConsoleEntry` rows with full system prompt snapshot, tool-call timeline, and final response. |
| `GraphPage.razor` | Graph tab at `/projects/{Slug}/graph`. Placeholder. |
| `IngestPage.razor` | Ingest tab at `/projects/{Slug}/ingest`. Placeholder. |
| `OutlinePage.razor` | Outline tab route; wraps `ProjectLayout` + `Outline.OutlineContent`. |

### Components/Pages/Projects/Outline/

| File | Description |
|------|-------------|
| `OutlineContent.razor` (+ `.razor.css`) | Top-level Outline tab orchestrator. Three-pane CSS-grid layout (chat | outline tree | metadata+entities side column) inside a fixed-height grid; each direct column wrapper is an independent `overflow-y: auto` scroll container, with horizontal overflow rather than responsive stacking when space is tight. Bumps a `_refreshSignal` int on every reload that child panels watch to re-read after tool turns. Re-fetches the project from `IProjectRepository` on every reload. |
| `OutlineMetadataPanel.razor` (+ `.razor.css`) | Editable project-metadata block surfaced at the top of the right side column. Whole block collapsible via header chevron; each metadata row is individually collapsible with a one-line preview when collapsed. Renders all `Project.Metadata` keys (sorted with `outline.*` first), inline-edits values via `IProjectService.UpdateMetadataAsync`, supports add + delete. Re-reads when the parent passes a refreshed `Project` (detects metadata content changes via a hash signature) so tool-driven edits show up automatically. |
| `OutlineTree.razor` (+ `.razor.css`) | Hierarchical Acts → Chapters tree. Acts are collapsible, drag-reorderable groups with inline-editable title/synopsis and `+ Chapter` / Delete (chapters fall back to Unassigned via `OnDelete.SetNull`). Chapters are inline-editable rows with a beats-toggle caret + count badge (renders `ChapterBeats` inline when expanded), stale/failed vector-index badge, drag-reorder within their act bucket, an act-picker `<select>` for cross-act moves, Open link, and delete-with-confirm. Re-fetches per-chapter beat counts via `IEntityService.CountChildrenAsync` whenever `RefreshSignal` bumps. |
| `EntitiesPanel.razor` (+ `.razor.css`) | Project-scoped entities side panel (Characters, Locations). Collapsible per-type sections with `+ Add` and a list of clickable entity rows. Clicking a row opens a Bootstrap-style modal (CSS-only, follows the `AiConsolePanel` pattern) with editable name + per-property textarea grid plus a read-only Links section listing every adjacent graph edge (both directions, all edge types including `HasChild`) via `IEntityService.ListLinksAsync`. Save computes property diffs and calls `UpdateAsync(propertiesToSet, propertiesToRemove)`; modal also exposes Delete-with-confirm. Re-reads on `RefreshSignal` bumps. |
| `ChapterBeats.razor` (+ `.razor.css`) | Inline beats expander rendered inside each chapter row. Loads beats via `IEntityService.ListAsync(projectId, "Event", chapterId)` ordered by `Order`, supports inline-edit (name + summary), drag-reorder (calls `ReorderAsync`), `+ Add beat` and delete-with-confirm. Re-reads on `RefreshSignal` bumps. |
| `OutlineChatPanel.razor` (+ `.razor.css`, `.razor.js`) | Multi-turn collaborative chat UI for Outline. Loads/creates the project's `OutlineConversation` via `IOutlineCollaborationService`, renders user/assistant bubbles with tool-call chips folded inline (expandable args/result/error), streams text via `IAsyncEnumerable<OutlineTurnUpdate>` with a typing cursor, and forwards `OutlineMutated` updates to `OnOutlineChanged`. Composer textarea auto-grows with input (JS `attachAutoSize`/`resetAutoSize`, capped at ~50% of the chat frame, internal scroll past the cap, shrinks back after Send). Send/Stop, auto-scroll-only-if-near-bottom JS interop, and a Reset button that clears history and reseeds the greeting. |

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
| `Project.cs` | EF entity scoping all narrative data. Stable `Slug` for URLs; static `ScopeKey(Guid)` produces the vector-store partition key (`project:{id:N}`). Owns `SystemPrompt` (seeded from `SeedSystemPrompt.Default`), `IncludeCurrentChapterInContext` toggle for the Context Feed, and a free-form `Metadata` JSON dictionary used by the Outline tab to persist wizard inputs. |
| `Act.cs` | EF entity for a top-level outline grouping (Title/Synopsis/Order) under a `Project`. Cascade-deleted with the project. Owned chapters survive act deletion (FK `OnDelete.SetNull`). |
| `Chapter.cs` | EF entity for a chapter (Title/Body/Synopsis/Order) under a `Project`, optionally assigned to an `Act` via nullable `ActId`. `Order` is scoped to the chapter's act bucket (or the project-level Unassigned bucket when `ActId` is null). Tracks `VectorIndexState` (UpToDate/Stale/Failed) + `VectorIndexedAt` + `VectorIndexError`; `VectorSourceId` returns the stable vector-store source id (`Id.ToString("N")`). |
| `AiConsoleEntry.cs` | EF entity for one AI Console turn: command, system-prompt snapshot, JSON tool-call timeline, response text, status (`Pending`/`Completed`/`Failed`/`Cancelled`). Cascade-deleted with its `Project`. |
| `OutlineConversation.cs` | EF entity — one persistent multi-turn collaborative chat per `Project` (unique on `ProjectId`). Owns ordered `OutlineMessage`s; cascade-deleted with the project. |
| `OutlineMessage.cs` | EF entity for a single chat row in an `OutlineConversation`: monotonic `Order`, `OutlineMessageRole` (System/User/Assistant/Tool), text `Content`, JSON `ToolCallsJson` for assistant function-calls, `ToolCallId` + `ToolName` for tool results, `OutlineMessageStatus` (Pending/Completed/Failed/Cancelled), optional `ErrorMessage`. |
| `GraphNode.cs` | Generic graph node: `(ProjectId, NodeType, Key)` unique, JSON properties bag. Cascade-deleted with its `Project`. |
| `GraphEdge.cs` | Directed edge between graph nodes with type and JSON properties. |

### Persistence/

| File | Description |
|------|-------------|
| `AppDbContext.cs` | EF Core context: `LlmProviders`, `OAuthTokens`, `Projects`, `Acts`, `Chapters`, `GraphNodes`, `GraphEdges`, `AiConsoleEntries`, `OutlineConversations`, `OutlineMessages`. JSON value converter shared by `Project.Metadata`, `GraphNode.Properties`, `GraphEdge.Properties`. Enum-to-string conversions for `OutlineMessage.Role`/`Status`. Cascade `Project → {Act, Chapter, GraphNode, AiConsoleEntry, OutlineConversation → OutlineMessage}`; `Chapter.ActId` FK uses `OnDelete.SetNull`. Unique index on `OutlineConversation.ProjectId`; composite index on `(OutlineMessage.ConversationId, Order)`. |
| `PersistenceServiceCollectionExtensions.cs` | `AddLorekeeperPersistence` switch on `Persistence:Provider` (SQLite today; Postgres slot for future). |
| `Migrations/` | EF Core migrations (`InitialSchema`, `AddProjects`, `AddChapters`, `AddSystemPromptAndAiConsole`, `AddOutline`, `AddOutlineConversations`). |

### Persistence/Repositories/

| File | Description |
|------|-------------|
| `ILlmProviderRepository.cs` / `LlmProviderRepository.cs` | CRUD + atomic `SetDefaultAsync` for `LlmProvider`. |
| `IOAuthTokenRepository.cs` / `OAuthTokenRepository.cs` | Latest/valid token lookup + replace-for-provider. |
| `IProjectRepository.cs` / `ProjectRepository.cs` | Project CRUD; slug uniqueness check; ordered list by `UpdatedAt`. |
| `IGraphNodeRepository.cs` / `GraphNodeRepository.cs` | Node CRUD plus project-scoped `Find(projectId, nodeType, key)`, type-agnostic `FindByKeyAsync(projectId, key)`, and `ListByTypeAsync(projectId, nodeType)` (ordered by Label/Key). |
| `IGraphEdgeRepository.cs` / `GraphEdgeRepository.cs` | Edge CRUD plus directional adjacency query. Defines `EdgeDirection` enum. |
| `IChapterRepository.cs` / `ChapterRepository.cs` | Chapter CRUD ordered by `Order`; `ListStaleAsync` for background reindex sweep; `GetMaxOrderAsync(projectId, actId)` and `ReorderAsync(projectId, actId, ids)` are scoped to a single act bucket (pass `actId == null` for the unassigned bucket). |
| `IActRepository.cs` / `ActRepository.cs` | Act CRUD ordered by `Order` per project; `ReorderAsync` rewrites the act ordering in one save. |
| `IOutlineConversationRepository.cs` / `OutlineConversationRepository.cs` | Persistence for `OutlineConversation` + ordered `OutlineMessage`s: `GetByProjectIdAsync`, `LoadMessagesAsync`, `GetMaxOrderAsync`, `AddConversationAsync`, `AddMessageAsync`, `UpdateMessage`, `RemoveConversation`. |

### Knowledge/

| File | Description |
|------|-------------|
| `IVectorStore.cs` | Vector storage abstraction over a `scopeKey` namespace (store/search/multi-search/delete). |
| `SqliteVecVectorStore.cs` | sqlite-vec implementation; uses `Embeddings:Dimensions` (768 for nomic-embed-text). |
| `VectorStoreInitializer.cs` | Creates `knowledge_chunks` + `vec_knowledge` virtual table outside EF migrations to keep abstraction portable. |
| `IGraphStore.cs` | High-level graph API: upsert nodes/edges, neighbors (BFS, depth-bounded), path-finding. Defines `GraphDirection`, `GraphTraversalOptions`, `GraphPath`, `GraphPathHop`. |
| `RelationalGraphStore.cs` | Relational-table implementation backed by node/edge repositories; doc-comments describe contract any future backend must honor. |
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
| `SeedSystemPrompt.cs` | Hardcoded default system prompt seeded into every newly-created `Project`. |

### Auth/

| File | Description |
|------|-------------|
| `CodexOAuthEndpoints.cs` | Minimal-API endpoints: `GET /auth/start/{providerId}` and `GET /auth/callback`. |

### Projects/

| File | Description |
|------|-------------| and seeds `SystemPrompt` from `SeedSystemPrompt.Default`. `UpdateSystemPromptAsync` (rejects empty) and `SetIncludeCurrentChapterAsync` back the Context Feed edits. `DeleteAsync` wipes vector chunks (`IVectorStore.DeleteByScopeAsync`) before EF-cascading the project + child rows. |

### Context/

| File | Description |
|------|-------------|
| `IContextBuilder.cs` / `ContextBuilder.cs` | Builds `ContextAssembly` (ordered `ContextItem`s + `Assemble()` concatenator) for the Context Feed. The Feed *is* the preview — `Assemble()` joins every checked block with labeled section headers and is the literal system message sent to the LLM. |
| `ChapterFormatting.cs` | `WithLineNumbers` / `SplitLines` / `JoinLines` helpers shared by the editor gutter, Context Feed preview, and AI tool reads so user and LLM see identical line numbers. |

### AiConsole/

| File | Description |
|------|-------------|
| `AiConsoleOptions.cs` | Bound from the `AiConsole` config section. `MaxToolIterations` caps the tool-call loop. |
| `IAiConsoleService.cs` / `AiConsoleService.cs` | Stateless one-shot AI turn: assembles system prompt via `IContextBuilder`, resolves default `LlmProvider`, runs a tool-call loop with cancellation support, and persists an `AiConsoleEntry` capturing the full timeline. Defines `AiConsoleContext` and `AiToolCallRecord`. |
| `IAiConsoleHistoryService.cs` / `AiConsoleHistoryService.cs` | Project-scoped read API for persisted `AiConsoleEntry` rows. |
| `AiConsoleTools.cs` | `AIFunction` definitions exposed to the LLM: `vector_search`, `list_chapters`, `read_chapter`, `edit_chapter` (line-based: full overwrite / insert before line / replace inclusive range). Closures capture per-request `AiConsoleContext`
| `IProjectService.cs` / `ProjectService.cs` | Project CRUD facade. `CreateAsync` slugifies the name (collision-free via `-2`/`-3` suffix). `DeleteAsync` wipes vector chunks (`IVectorStore.DeleteByScopeAsync`) before EF-cascading the project + child graph + chapter rows. `UpdateMetadataAsync` merges/replaces the free-form `Project.Metadata` bag (used by the Outline wizard). Slug stable across renames. |

### Outline/

| File | Description |
|------|-------------|
| `IActService.cs` / `ActService.cs` | Act CRUD facade. `CreateAsync` auto-orders to the end. `DeleteAsync` lets the FK demote owned chapters to Unassigned (`OnDelete.SetNull`). Touches `Project.UpdatedAt` on every mutation. |
| `IOutlineCollaborationService.cs` / `OutlineCollaborationService.cs` | Multi-turn collaborative outline chat. `GetOrCreateAsync` ensures a project conversation seeded with system prompt + assistant greeting. `SendAsync` returns an `IAsyncEnumerable<OutlineTurnUpdate>` driving a streaming tool-call loop: persists user turn, replays full history into the project's default `IChatClient` with `OutlineCollaborationTools` registered, accumulates streamed text into a pre-created Pending assistant row, invokes any function calls (persisting Tool rows + emitting `ToolCallStarted/Completed/OutlineMutated`), and loops up to `AiConsoleOptions.MaxToolIterations`. `ResetAsync` wipes history and reseeds. |
| `OutlineCollaborationTools.cs` | `AIFunction` definitions exposed to the outline LLM (closures capture `OutlineCollaborationContext(ProjectId, OnMutated)`): `list_outline` (now includes per-chapter `beatCount`), `create_act`, `update_act`, `delete_act`, `create_chapter`, `update_chapter`, `delete_chapter`, `reorder_acts`, `reorder_chapters`, `set_project_metadata`, `vector_search`, plus the generic entity tools `list_entities`, `create_entity`, `update_entity`, `delete_entity`, `reorder_entities`, `link_entities` (entity type passed as a string; `Event` = chapter-scoped beats requires a chapter `parentId`). Helper `ResolveActAsync` parses `string?` act ids accepting an `"unassigned"` sentinel and validates project ownership. Mutating tools fire `OnMutated()` to surface `OutlineMutated` updates. |
| `IEntityService.cs` / `EntityService.cs` | Single contract for every story-graph entity (Characters, Locations, Events/beats, ...). Entities persist as `GraphNode`s via `IGraphStore` with `NodeType=type`, `Key=Guid.NewGuid().ToString("N")`, `Label=name`, free-form `Properties`. When `parentId` is set, the entity gets an integer `order` property and a `HasChild` edge from the parent (chapter parent nodes are upserted lazily as `NodeType="Chapter"`). `UpdateAsync` mutates the loaded node in place via the node repo to avoid `IGraphStore.UpsertNodeAsync`'s replace-merge semantics. `ListLinksAsync` returns all adjacent edges (both directions, every edge type incl. `HasChild`) projected as `EntityLink(EdgeType, Direction, OtherEntityId, OtherEntityName, OtherEntityType)` for read-only display in the entity modal. Returns the public `StoryEntity` projection. |
| `OutlineTurnUpdate.cs` | `[JsonDerivedType]`-decorated abstract record for streaming chat updates: `TextDelta`, `ToolCallStarted`, `ToolCallCompleted`, `AssistantMessageCompleted`, `OutlineMutated`, `TurnError`. |

### Chapters/

| File | Description |
|------|-------------|
| `IChapterService.cs` / `ChapterService.cs` | Chapter CRUD facade. `CreateAsync(projectId, actId?, ...)` stamps a new chapter into a chosen act bucket (or Unassigned). `UpdateAsync` accepts an optional `ChapterActAssignment` wrapper to MOVE the chapter between act buckets (appended to the destination); marks `VectorIndexState=Stale` on body changes and notifies `IStaleChapterNotifier`. `ReorderAsync(projectId, actId?, ids)` reorders within a single bucket. `ReindexAsync` deletes prior vector chunks (`source_type="chapter"`) and rewrites them via `ITextChunker` + `IEmbeddingService`. `DeleteAsync` removes vectors AND removes the chapter's graph node + any `HasChild` entity children before EF delete (keeps `IGraphStore` consistent without depending on `IEntityService`). |
| `IStaleChapterNotifier.cs` / `StaleChapterNotifier.cs` | In-process unbounded `Channel<Guid>` pub/sub of chapters needing reindex. |
| `StaleChapterReindexer.cs` | `BackgroundService` that sweeps existing stale chapters at startup and drains `IStaleChapterNotifier` thereafter, calling `IChapterService.ReindexAsync` in a fresh DI scope per chapter. |

### wwwroot/

| File | Description |
|------|-------------|
| `app.css` | App-wide CSS. |
| `favicon.png` | Site icon. |
| `lib/bootstrap/` | Vendored Bootstrap distribution. |

