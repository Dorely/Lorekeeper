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
| `MainLayout.razor` / `.css` | Top-level page layout with sidebar + main column. |
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
| `ProjectLayout.razor` | Shared shell for project workspace pages: loads project by slug, renders title + horizontal tab strip (Editor / Graph / Ingest / Outline), exposes `Project` via `CascadingValue`. |
| `EditorPage.razor` | Editor tab routes (`/projects/{Slug}/editor` and `/projects/{Slug}/editor/{ChapterId:guid}`). Wraps `ProjectLayout` + `EditorContent`. |
| `EditorContent.razor` (+ `.razor.css`, `.razor.js`) | Functional chapter editor body: chapter-selector header (dropdown menu, edit-title pencil, +new-chapter button), JS-debounced (1s) auto-save textarea that persists via `IChapterService` and triggers `ReindexAsync`. Save/index status surfaced inline. Story Graph / Context Feed / AI Console panels remain placeholder. |
| `GraphPage.razor` | Graph tab at `/projects/{Slug}/graph`. Placeholder. |
| `IngestPage.razor` | Ingest tab at `/projects/{Slug}/ingest`. Placeholder. |
| `OutlinePage.razor` | Outline tab route; wraps `ProjectLayout` + `OutlineContent`. |
| `OutlineContent.razor` (+ `.razor.css`) | Chapter list with create / inline-rename / synopsis / delete-with-confirm and HTML5 drag-reorder via `IChapterService.ReorderAsync`. Surfaces stale/failed vector-index badge. `?focus={id}` highlights a row. |

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
| `Project.cs` | EF entity scoping all narrative data. Stable `Slug` for URLs; static `ScopeKey(Guid)` produces the vector-store partition key (`project:{id:N}`). |
| `Chapter.cs` | EF entity for a chapter (Title/Body/Synopsis/Order) under a `Project`. Tracks `VectorIndexState` (UpToDate/Stale/Failed) + `VectorIndexedAt` + `VectorIndexError`; `VectorSourceId` returns the stable vector-store source id (`Id.ToString("N")`). |
| `GraphNode.cs` | Generic graph node: `(ProjectId, NodeType, Key)` unique, JSON properties bag. Cascade-deleted with its `Project`. |
| `GraphEdge.cs` | Directed edge between graph nodes with type and JSON properties. |

### Persistence/

| File | Description |
|------|-------------|
| `AppDbContext.cs` | EF Core context. `LlmProviders`, `OAuthTokens`, `Projects`, `Chapters`, `GraphNodes`, `GraphEdges`. JSON value converter for property bags. Cascade `Project → GraphNode` and `Project → Chapter`. |
| `PersistenceServiceCollectionExtensions.cs` | `AddLorekeeperPersistence` switch on `Persistence:Provider` (SQLite today; Postgres slot for future). |
| `Migrations/` | EF Core migrations (`InitialSchema`, `AddProjects`, `AddChapters`). |

### Persistence/Repositories/

| File | Description |
|------|-------------|
| `ILlmProviderRepository.cs` / `LlmProviderRepository.cs` | CRUD + atomic `SetDefaultAsync` for `LlmProvider`. |
| `IOAuthTokenRepository.cs` / `OAuthTokenRepository.cs` | Latest/valid token lookup + replace-for-provider. |
| `IProjectRepository.cs` / `ProjectRepository.cs` | Project CRUD; slug uniqueness check; ordered list by `UpdatedAt`. |
| `IGraphNodeRepository.cs` / `GraphNodeRepository.cs` | Node CRUD plus project-scoped `Find(projectId, nodeType, key)`. |
| `IGraphEdgeRepository.cs` / `GraphEdgeRepository.cs` | Edge CRUD plus directional adjacency query. Defines `EdgeDirection` enum. |
| `IChapterRepository.cs` / `ChapterRepository.cs` | Chapter CRUD ordered by `Order`; `ListStaleAsync` for background reindex sweep; `ReorderAsync(projectId, ids)` rewrites `Order` in one save. |

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

### Auth/

| File | Description |
|------|-------------|
| `CodexOAuthEndpoints.cs` | Minimal-API endpoints: `GET /auth/start/{providerId}` and `GET /auth/callback`. |

### Projects/

| File | Description |
|------|-------------|
| `IProjectService.cs` / `ProjectService.cs` | Project CRUD facade. `CreateAsync` slugifies the name (collision-free via `-2`/`-3` suffix). `DeleteAsync` wipes vector chunks (`IVectorStore.DeleteByScopeAsync`) before EF-cascading the project + child graph + chapter rows. Slug stable across renames. |

### Chapters/

| File | Description |
|------|-------------|
| `IChapterService.cs` / `ChapterService.cs` | Chapter CRUD facade. `UpdateAsync` marks `VectorIndexState=Stale` on body changes and notifies `IStaleChapterNotifier`. `ReindexAsync` deletes prior vector chunks (`source_type="chapter"`) and rewrites them via `ITextChunker` + `IEmbeddingService`; updates `VectorIndexState`/`VectorIndexedAt`/`VectorIndexError`. `DeleteAsync` removes vectors before EF delete. |
| `IStaleChapterNotifier.cs` / `StaleChapterNotifier.cs` | In-process unbounded `Channel<Guid>` pub/sub of chapters needing reindex. |
| `StaleChapterReindexer.cs` | `BackgroundService` that sweeps existing stale chapters at startup and drains `IStaleChapterNotifier` thereafter, calling `IChapterService.ReindexAsync` in a fresh DI scope per chapter. |

### wwwroot/

| File | Description |
|------|-------------|
| `app.css` | App-wide CSS. |
| `favicon.png` | Site icon. |
| `lib/bootstrap/` | Vendored Bootstrap distribution. |

