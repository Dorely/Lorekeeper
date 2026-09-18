# Providers and background work

## When to read

Read this chapter completely when a task changes LLM connections, model
discovery, chat or vision transports, OpenAI account authorization, embeddings, web-search or
web-fetch providers, image-generation transport, provider settings, request
timeouts, retries, queues, hosted workers, notifiers, cancellation, or restart
reconciliation. Read it for a new long-running app-process operation even when
the operation's domain model belongs to another chapter.

Also read [Runtime and host architecture](runtime-host.md) when registration,
configuration binding, endpoints, or startup gating changes. Provider consumers
must read their owning chapter too: for example assistant changes require
[Assistants and chat](assistants-chat.md), ingest changes require
[Narrative context](narrative-context.md), visual-generation changes require
[Composition and media](composition-media.md), and publication worker changes
require [Press production](press-production.md).

## Scope and ownership

This chapter owns configuration and resolution of external AI/search services,
provider-neutral chat, vision, embedding, web, and image transport boundaries,
OpenAI account authorization behavior, wire-compatibility policy, network safety, and the shared
lifecycle rules for app-process queues and workers. It documents how work remains
alive, observable, cancellable, and recoverable without assigning feature-domain
validation to infrastructure.

Provider presets and UI rows configure connections; they do not become separate
runtime implementations. Feature services consume neutral contracts such as
`IChatClientFactory`, `IVisionModelClientFactory`, `IEmbeddingService`,
`IWebSearchClient`, or `IProjectImageProvider`. Provider-specific request shapes,
credentials, error parsing, and retries stay behind those contracts.

Durable job entities and feature services remain owned by their domain chapters.
This chapter owns their execution pattern: in-process queues coordinate live work,
hosted workers wait for database readiness, scoped processors perform bounded
units, persisted checkpoints form the restart/audit boundary, and notifiers carry
ephemeral progress to currently connected Blazor circuits.

## Current architecture and invariants

### LLM connection model

Settings > Providers manages connection cards and nested model rows. Built-in
OpenAI-compatible presets include OpenAI, two Cline connection modes sharing the
same endpoint, Anthropic, Gemini, OpenRouter, Groq, DeepSeek, xAI, Mistral,
Together, Ollama, LM Studio, and a custom endpoint. Presets pre-fill endpoint,
authentication, suggested model, token-budget, and compatibility fields; they do
not bypass the normal persisted provider model or connection tests.

One top-level connection may own shared endpoint/authentication state for multiple
model rows. `LlmConnectionResolver` follows `CredentialSourceId` so chat, vision,
discovery, and embeddings resolve credentials consistently. `ILlmProviderService`
owns provider/model CRUD, grouped deletion, shared-field propagation,
working-default selection, explicit/default model availability, and persisted
chat/vision readiness. A conversation's model selection is a soft provider
reference: deletion can make a selection unavailable without cascading away the
transcript.

OpenAI-account credentials are the exception to provider-row ownership:
`OpenAiAccount` owns the OAuth tokens, while each account-backed `LlmProvider`
remains the stable selectable model identity. Account-backed rows resolve by
`OpenAiAccountId`, never by the historical `openai-codex` row name or by guessing
from token shape. Manual/API-key connection groups continue using
`CredentialSourceId` and provider-owned keys.

`OpenAiAccountModelCatalog` is the versioned metadata authority for bundled
account models. Schema version 1 was validated on 2026-09-16 against the
[official Codex model catalog](https://learn.chatgpt.com/docs/models) and contains
only `gpt-6-astra`, `gpt-5.6-sol`, `gpt-5.6-terra`, and `gpt-5.6-luna`. Sol is
preferred. Defaults are Astra/Medium, Sol/Low, Terra/Medium, and Luna/Medium;
each permits Low, Medium, High, Extra high, and Maximum and declares text plus
image input. Catalog rows retain nullable explicit effort and input-budget
overrides; runtime resolution supplies catalog defaults only when an override is
absent. Unsupported saved effort values fail closed.

OpenAI account model discovery is intentionally absent: Lorekeeper never calls
`/v1/models` for an account-backed chat or embedding connection. The bundled
stable-four manifest is the complete account model list, while Advanced accepts
an exact manual model ID followed by the existing explicit Test workflow.
User-configured OpenAI-compatible connections may still discover models through
`GET /models`, and Ollama connections through `GET /api/tags`; those generic
paths degrade to code-owned suggestions or manual entry when discovery is
unavailable.

Provider verification performs a protocol-safe chat probe without a constrained
output budget and, after chat succeeds, a non-blocking vision probe. Saving is
gated on chat success only. A failed vision probe persists or presents a warning
without converting a working chat connection into a failure. Shared error helpers
extract concise provider detail rather than exposing raw response bodies.
Direct OpenAI presets and OpenAI-account chat, vision, and image-mainline
fallbacks use `gpt-5.6-sol` as the code-owned default; existing persisted model
selections remain authoritative until the user changes them.

Bundled account rows need valid account credentials and catalog-valid settings,
but they do not require a manual chat or vision Test. Manual account rows retain
the explicit Test workflow under Advanced configuration. A new, empty account
receives the stable four and makes Sol the global default only when no default
already exists. An explicit global or conversation selection whose credentials
or eventual provider request fail remains selected and returns an actionable
error; it is never replaced with another model.

### Input-context budgets

Each chat model row carries an optional `MaxInputTokens` advisory input-context
override used only for assistant context compaction decisions and panel token
projection; it never changes the wire request. Bundled account models resolve an
absent override to the catalog's 272,000-token usable input budget. Account
model-list discovery does not supply a competing advertised-context value. Other
connections resolve an absent row value through `ChatTokens:ModelMaxInputTokens`, then
`ChatTokens:DefaultMaxInputTokens`; their interactive discovery picker may still
prefill the editable row value. The resolved budget and catalog metadata are
captured in each turn's model snapshot so a running turn remains stable if
Settings changes. Unknown manual models retain the general 200,000-token fallback
unless a row or deployment override supplies a different value.

### Chat and vision wire compatibility

`IChatClientFactory` creates either the Codex Responses client or an
OpenAI-compatible client and applies the selected reasoning effort, effective
output-token budget, max-token field policy, and configured timeout.
`IVisionModelClientFactory` uses the same connection resolution and error policy
for readiness probes and artifact vision reads. Assistant transcript/tool-loop
semantics are owned by [Assistants and chat](assistants-chat.md).

OpenAI-compatible endpoint behavior is classified by endpoint host in
`LlmWireCompatResolver`; it is not maintained as a per-model compatibility matrix.
The current classifications are:

- OpenAI first-party (`api.openai.com`) uses `max_completion_tokens` and receives
  no automatic output budget.
- Cline (`api.cline.bot`), DeepSeek (`api.deepseek.com`), and Together
  (`api.together.xyz`) use the legacy `max_tokens` field.
- Local Ollama and LM Studio endpoints also use the legacy field but receive no
  automatic budget because local model capability is unknown.
- Other remote OpenAI-compatible endpoints, including OpenRouter and custom
  gateways, use the standard field and an 8,192-token default output budget.

Each provider row can override both `MaxOutputTokens` and `MaxTokensField`.
Readiness probes remain unconstrained and ignore those chat-output settings.
OpenAI-compatible chat requests share a pooled transport with
`Agents:ChatRequestTimeoutSeconds` (600 seconds by default). Codex requests use
the configured Codex timeout.

Codex strict tool-schema preparation hoists local subschema references into
root-level `$defs`, including repeated contributor types in bibliography tools.
Recursive references remain references; definitions receive the same required-field
and object-closure rules as inline schemas. Schema traversal preserves authored
property names such as `title`. Unresolved or external references fail locally
before dispatch rather than producing a provider request with dangling references.

The legacy-field handler rewrites request bodies only where the compatibility
classification requires it. The envelope handler unwraps a non-streaming outer
gateway object such as Cline's `data`/`success` envelope; standard streaming root
payloads pass through. These handlers compose, so supporting one compatibility
quirk cannot silently disable the other. The raw-HTTP vision probe follows the
same envelope policy. Unknown streamed OpenAI-compatible tool-call extensions are
preserved and restored on the correlated assistant/tool-result request.

### OAuth and credential security

OpenAI account authorization is account-oriented and starts directly from the
mounted Providers component; there is no application `/auth/start` redirect.
`OpenAiAuthorizationFlowRegistry` keeps one process-local flow per account with
a 256-bit state, independent 256-bit PKCE verifier, ten-minute expiry, and
terminal status. Starting a replacement cancels the older state, callback claim
is atomic and single-use, and per-account coordination prevents a canceled or
stale callback from replacing credentials. Reconnect preserves the existing
credential row until the replacement exchange commits successfully.

`IExternalAuthorizationLauncher` is the single OpenAI/GitHub handoff boundary.
It accepts only exact allowlisted HTTPS authorization targets. Electron opens the
validated URL through the system browser; browser hosting returns the URL to be
rendered as an explicit user-activated external link. Settings polls only while
its flow is pending, cancels on user cancellation or unmount, and ensures the
local account catalog plus previously unset embedding configuration after success.
The `/auth/callback` endpoint accepts success or denial, consumes the flow, and
renders a standalone no-store Lorekeeper completion page. It never redirects to
Settings or opens another application session.

The configured redirect remains `http://localhost:1455/auth/callback`. Before
Connect is enabled, Lorekeeper validates that this exact loopback origin and
callback path are served by the active host. A port/configuration mismatch is
shown inline; the application never starts or terminates another listener.

LLM and search API keys are persisted on their provider rows. OpenAI-account
access and refresh tokens are stored in dedicated SQLite rows owned by
`OpenAiAccount`; model rows never copy them. This local persistence is not
an operating-system credential vault and does not imply encryption at rest. Never
log API keys, authorization codes, access tokens, refresh tokens, or sensitive
provider payloads, and never copy credentials onto unrelated feature entities or
assistant tool payloads.

Callback commit, disconnect, reconnect start, and token refresh are serialized
per account for the whole process and begin from fresh persistence reads. The
external ChatGPT account identity is read only at the successful authorization
boundary and persisted on `OpenAiAccount`; chat, vision, embeddings, model
requests, and image generation use that persisted identity and never inspect
access-token shape at request time. Account model discovery is not a supported
runtime path. A migrated token without persisted external identity fails closed
as Reauthentication required.

A token endpoint `401`, `invalid_grant`, or `invalid_token` marks the account as
requiring reauthentication while retaining the credential row. Transient
transport/server failures also retain credentials and remain retryable errors.
Only a successful replacement flow swaps credentials; explicit Disconnect
removes them. The Providers surface catches sanitized failures at the card
boundary so one unavailable OAuth endpoint cannot break Settings.

Version-control GitHub access is a separate provider-owned boundary. Its device
authorization and REST client use the shipped public client ID for Lorekeeper's
maintainer-owned OAuth app. Forks and custom deployments can replace it through
`VersionHistory:GitHub:ClientId` or the standard
`VersionHistory__GitHub__ClientId` environment override. The resulting access
token is stored only in `GitHubConnection`, and the service exposes non-secret
account and repository metadata to the History surface. Device authorization,
repository listing/creation, fetch, manual push, and remote checkout are explicit
user-started network actions; loading cached connection or remote status does
not contact GitHub. After explicit remote attachment, successful local
checkpoints enqueue durable automatic push intents for all attached remotes.
The queue is only a process-local wake-up; the startup-gated worker scans the
operation journal, creates fresh scopes, and holds the project mutation lease
through each non-force push attempt. Snapshot manifests, Git metadata, logs,
and assistant payloads never carry the token or device code.

### Embedding configuration and rebuilds

One persisted embedding configuration selects a provider connection, API kind,
model, dimensions, and last-tested state. The settings flow lists eligible
connections, discovers or accepts a model, tests it before save, and may unset the
feature. `ProviderEmbeddingService` resolves the current provider/model, truncates
oversized input, batches requests, validates returned dimensions, and reports
availability. `EmbeddingClient` supports native Ollama `/api/embed`,
OpenAI-compatible `/v1/embeddings`, and OpenAI account authorization routing.

Changing the active embedding model cancels and awaits any current bulk rebuild,
updates configuration, and queues a versioned replacement. `EmbeddingRebuildQueue`
coalesces pending work and prevents competing rebuild generations.
`EmbeddingRebuildWorker` drains one rebuild at a time; the scoped rebuild service
recreates sqlite-vec dimensions, marks indexes stale, and reindexes project
profiles, writing samples, graph data, outline/manuscript sources, and ingest
corpus with configured batching, delay, and retry backoff. Domain-specific
indexing rules belong to [Narrative context](narrative-context.md).

### Web search and guarded fetch

Research web search is selected through a single active `SearchProvider` and a
provider factory. The current clients normalize SerpApi Google results and Brave
Search results into the shared `WebSearchResult` contract. Search-provider CRUD,
activation, readiness tests, and execution belong to `ISearchProviderService`;
the Research surface must not construct provider requests directly.

Page reads use a separate safe-fetch boundary. URL normalization, navigation and
static-link filtering, redirect handling, private-network blocking, response byte
limits, timeouts, supported-raster checks, `robots.txt`, per-host serialization,
delay/jitter, and cooldowns are implemented in research services rather than
assistant prompts. `WebHttpFetchClient` is the coordinated byte-limited HTTP GET
helper; `WebFetchCoordinator` owns host pacing; `WebRobotsPolicy` caches policy;
and source readers/extractors produce normalized text, links, images, and
provenance. Cached webpage candidates retain search/fetch provenance and must be
explicitly queued into ingest before becoming corpus content.

Defaults live under `Research:Web`, including request timeout, 2 MB page limit,
private-network blocking, four-second host delay plus jitter, robots enforcement,
retry/cooldown behavior, and model-visible link/page limits. Relaxing those values
is a security and external-service behavior change, not a UI-only adjustment.

### Image provider transport

Project image generation and editing use `IProjectImageProvider`; the current
Codex implementation supports account-backed Responses image generation,
source-driven edits, optional soft regional guides for narrowly localized or
otherwise hard-to-describe work, streamed partial images, explicit generate/edit
actions, continuity references, and source-image edit semantics. A guide is
prompt guidance rather than a provider-enforced pixel boundary, so the provider
contract never represents it as protecting unmasked pixels. `ImagePromptComposer`
supplies the shared structured prompt contract. Feature-specific use of generated
images, geometry targets, assets, and deletion guards belongs to
[Composition and media](composition-media.md).
The provider transport accepts pixel dimensions, not physical DPI. Lorekeeper
resolves effective-DPI requests against server-owned physical geometry before
dispatch, enforcing the provider's 16-pixel alignment, edge, megapixel, and
aspect limits. An infeasible request fails before any durable job or provider
call; no density metadata is fabricated in the image file.

Regional-guided dispatch revalidates the source, compiled geometry, and mask
immediately before the request. The image runtime decodes a PNG, JPEG, or WebP
source and re-encodes the first input as PNG without resizing, requires a
same-sized binary-alpha PNG mask with at least one editable pixel, and sends
those same-format inputs together. Failure to load, validate, or normalize
either input fails the attempt rather than silently issuing an unmasked edit.
The request uses the source-aspect-bound provider raster resolved by the image
workflow; regional guides cannot accompany layout-bound targets, reserved
regions, or aspect-changing reframes.
`gpt-image-2` receives no
adjustable input-fidelity field because that model processes edit inputs at high
fidelity automatically. Stored source assets and requested output formats are
not rewritten by this transient provider normalization.

Provider output is stored without layout cropping or resizing, apart from
supported-format normalization such as WebP to lossless PNG. Explicit requested
rasters are compared with decoded output dimensions even for free-standing jobs,
and mismatches remain visible in persisted provenance and assistant results;
aspect compatibility does not make a raster mismatch exact. For a requested
minimum DPI, an undersized provider result is retained unattached with
`MINIMUM_DPI_NOT_MET` and is not a compliant publication candidate.
Free-standing generation
and unmasked edit callers send a concrete provider-valid raster derived from the
configured Core Book page by default; layout-bound callers replace it with their
server-owned target raster. Publish derives that raster entirely behind its tool
boundary from the target's internal output expectation; the assistant supplies
only target identity, aspect, and protected geometry. Editor retains its explicit
layout controls; Images and Outline keep moderate defaults unless
their callers explicitly request a minimum against the Core Book physical basis.
When no single provider raster can meet the required DPI, the application-side
print-upscale pipeline dispatches the largest compatible native raster and, after
completion, derives a separate print-upscaled asset with deterministic Lanczos3
sampling; the provider transport is never asked for, or told about, the final
print raster.
For Publish crop-to-fill targets, the resolver may use the nearest supported
provider aspect. It selects aspect-compatible request rasters first, then uses
the highest effective density that remains within provider edge and pixel caps;
if the aligned provider grid has no raster inside the preferred aspect
tolerance, it uses the nearest aligned aspect instead of rejecting the target.
The application then prepares a same-aspect derivative that covers both target
edges. The decoded provider output, rather than an assumed requested raster,
drives the final fill dimensions, and decoded output is required only to have
positive dimensions: provider responses are not incorrectly revalidated against
the divisibility, edge, or pixel rules that apply to outbound requests. These
details stay in provenance and are not projected into Publish tool results.
Same-aspect generative up-resolution preserves the complete framing and asks
for reconstructed detail, while intentional outpainting separately describes
the desired larger framing and direction. The provider owns how source pixels
are reinterpreted in either operation. Provider output has its own
configurable 64 MiB byte cap rather than inheriting the smaller upload limit.
Image options also bound request attempts, timeout, partials, references, output
count, and one parallel provider request by default.

`IProjectImageJobService` persists request/audit state, every valid streamed
partial, and provider output. Partial writes are serialized per output and
awaited before that output becomes terminal; each write uses a fresh scope, so
no database context spans the provider stream. Received partials survive retry,
cancellation, failure, and restart, while invalid or oversized partials fail the
affected attempt through the normal retry/error path.
`IProjectImageGenerationRuntime` is a singleton FIFO runtime with at most one
active job per project, cancellation propagated through providers and retries,
durable partial preview URLs, terminal waiters, and notifications. The startup worker marks
interrupted running jobs failed and resumes queued work after database readiness.

Image jobs normalize the requested model, quality, output format/compression,
and background before dispatch and capture those settings on the durable job.
The default model is Flare; Sunburst is an explicit option for demanding work.
Provider-reported model and quality remain separate result provenance. Background
`transparent` is validated as a requested alpha output and prompt guidance
requests an empty backdrop unless the brief explicitly requires background elements;
`opaque` receives an appropriate plain instruction. Existing jobs are carried
forward with `Background = auto` by a forward schema migration. These defaults
and model aliases require live provider validation before an integration claim.

Image-provider failures are classified from structured error `code`/`type`,
status, and redacted message into stable kinds. `rate_limit`,
`codex_image_input_rate_limit`, `server_error`, and `transport_error` are
retryable; invalid requests, invalid models, authentication/permission,
moderation, quota/billing, and other API errors fail without retry. A
`Retry-After` delta/date is honored when present, otherwise the runtime uses
bounded backoff (with the image-input rate-limit floor). The request timeout
covers the entire provider stream, including partial-image receipt, and is
recorded as `request_timeout`; caller cancellation remains cancellation and is
not converted into timeout or retried. A stream that ends without a final image
is a structured failure. Persisted partials remain available across retries,
timeouts, cancellation, and failure according to the image-job audit contract.

### Queue, worker, and notification contract

Embedding rebuilds, project imports, ingest jobs, image jobs, publication
preparation/renders, editor revision jobs, and user-facing assistant turns use
app-process coordinators appropriate to their lifetime. The common invariants are:

1. Persist the durable job/turn and captured provider/model/input identity before
   relying on ephemeral runtime state.
2. Enqueue a stable identifier; create a fresh DI scope for processing rather
   than retaining a circuit-scoped service or EF context.
3. Wait for `ApplicationStartupState` database readiness before reconciliation or
   queue work.
4. Persist bounded checkpoints in fresh database operations. Do not retain an EF
   context across provider streaming, fetches, render waits, retries, delays, or
   polling.
5. Propagate explicit cancellation through processors and provider calls. Record a
   truthful terminal or resumable state rather than reporting silent success.
6. Treat notifiers and buffered subscriptions as live presentation aids only;
   persisted state—including image partials captured from provider progress—remains
   the audit and restart source of truth.
7. Reconcile interrupted work according to the feature's documented contract.
   Never apply one generic resume policy to unlike jobs.

### Diagnostic logging and redaction boundary

All provider request/response and tool-argument logging flows through
`Lorekeeper/Diagnostics/LogRedaction`, a fail-safe redaction of credential
headers (`authorization`, `api-key`, `x-api-key`, `chatgpt-account-id`, and
`openai-organization`) and token-like JSON fields (`token`, `access_token`,
`refresh_token`, `api_key`, `apikey`, and `session_key`); HTTP failure
exception messages carry redacted bodies. Redaction is structural, not a
per-call convention: no provider payload reaches a log sink without passing
through it.

A development-only diagnostic file logger,
`Lorekeeper/Diagnostics/DevFileLoggerProvider`, writes bounded daily files to
`%LOCALAPPDATA%\Lorekeeper\dev-logs` under a 14-day / 50 MB retention cap, and
is registered only when the host environment is Development; production and
packaged Electron hosts never register it. Render, publication-preparation, and
image-job services log lifecycle and failure details (profile, fingerprint,
scope, and `request.json` path) at Debug/Warning in that layer. Because
terminal job rows — including `RawProviderResponseJson` — are purged on next
use, provider payload diagnosis lives in these Development logs, not in
database rows.

Ingest uses an in-process queue, notifier, hosted worker, and scoped processor.
`ExtractEntities` performs model enrichment; `IndexOnly` and
`ConvertLegacySource` bypass chat-provider admission and use the existing
embedding service after local source preparation. Missing embeddings stop these
jobs with an actionable resume message. Conversion publication is idempotent;
retries after publication only rebuild derived indexes. Entity-extraction
restart cleans source-owned graph contributions; indexing/conversion restart
preserves them. Job deletion removes operational records, preserving retained
source material and evidence. Import stages
and hashes uploads outside SQLite, then uses a durable queued job, notifier,
hosted worker, and atomic creative transaction. Interrupted pre-commit imports
fail closed; `Committed` and `Indexing` imports resume only retryable projection
work and finish with warnings when an index cannot be rebuilt. Terminal import
jobs clean their application-owned staged files. Embedding
rebuild coalesces and cancels generations. Image runtime resumes queued work but
marks interrupted active work failed. Editor revision and assistant turn runners
keep live work independent from a component lifetime. Publication queue and
atomic artifact semantics are detailed in [Press production](press-production.md).

## Key files and file families

| File or family | Architectural role |
|---|---|
| [`Lorekeeper/Llm/LlmProviderCatalog.cs`](../../Lorekeeper/Llm/LlmProviderCatalog.cs), [`OpenAiAccountModelCatalog.cs`](../../Lorekeeper/Llm/OpenAiAccountModelCatalog.cs), [`OpenAiAccountModelCatalogService.cs`](../../Lorekeeper/Llm/OpenAiAccountModelCatalogService.cs), [`ModelCatalogService.cs`](../../Lorekeeper/Llm/ModelCatalogService.cs), [`LlmProviderService.cs`](../../Lorekeeper/Llm/LlmProviderService.cs), and [`LlmConnectionResolver.cs`](../../Lorekeeper/Llm/LlmConnectionResolver.cs) | Generic provider presets/discovery, the static account catalog, persisted connection/model ownership, working-default resolution, and shared credential lookup. Account-backed discovery is rejected. |
| [`Lorekeeper/Llm/ChatClientFactory.cs`](../../Lorekeeper/Llm/ChatClientFactory.cs), [`CodexChatClient.cs`](../../Lorekeeper/Llm/CodexChatClient.cs), and [`VisionModelClientFactory.cs`](../../Lorekeeper/Llm/VisionModelClientFactory.cs) | Provider-neutral chat/vision construction, Codex Responses transport, verification probes, timeouts, and normalized failures. |
| [`Lorekeeper/Llm/WireCompat/`](../../Lorekeeper/Llm/WireCompat/) and [`OpenAICompatEnvelopeHandler.cs`](../../Lorekeeper/Llm/OpenAICompatEnvelopeHandler.cs) | Endpoint-host compatibility classification plus narrowly scoped max-token and response-envelope adaptations. |
| [`Lorekeeper/Authorization/`](../../Lorekeeper/Authorization/), [`Lorekeeper/Llm/CodexProvider.cs`](../../Lorekeeper/Llm/CodexProvider.cs), and [`Lorekeeper/Auth/CodexOAuthEndpoints.cs`](../../Lorekeeper/Auth/CodexOAuthEndpoints.cs) | Account authorization/token contracts, process-local PKCE lifecycle, serialized refresh/credential replacement, allowlisted external launch, callback-origin validation, completion page, Codex endpoint/default constants, and the local callback endpoint. |
| [`Lorekeeper/Llm/EmbeddingClient.cs`](../../Lorekeeper/Llm/EmbeddingClient.cs), [`EmbeddingConfigurationService.cs`](../../Lorekeeper/Llm/EmbeddingConfigurationService.cs), and [`ProviderEmbeddingService.cs`](../../Lorekeeper/Llm/ProviderEmbeddingService.cs) | Embedding transport, test-before-save configuration, active-provider resolution, batching, truncation, and dimension validation. |
| [`Lorekeeper/Llm/EmbeddingRebuild*`](../../Lorekeeper/Llm/) | Versioned/coalesced rebuild queue, hosted worker, scoped bulk index rebuild, throttling, retry, and cancellation. |
| [`ISearchProviderService.cs`](../../Lorekeeper/Search/ISearchProviderService.cs), [`WebSearchProviderFactory.cs`](../../Lorekeeper/Search/WebSearchProviderFactory.cs), [`SerpApiWebSearchClient.cs`](../../Lorekeeper/Search/SerpApiWebSearchClient.cs), and [`BraveWebSearchClient.cs`](../../Lorekeeper/Search/BraveWebSearchClient.cs) | Search-provider persistence/service boundary and normalized external search adapters; project-corpus retrieval remains owned by narrative context. |
| [`WebPageReader.cs`](../../Lorekeeper/Research/WebPageReader.cs), [`WebHttpFetchClient.cs`](../../Lorekeeper/Research/WebHttpFetchClient.cs), [`WebFetchCoordinator.cs`](../../Lorekeeper/Research/WebFetchCoordinator.cs), [`WebRobotsPolicy.cs`](../../Lorekeeper/Research/WebRobotsPolicy.cs), and [`MediaWikiWebPageSourceReader.cs`](../../Lorekeeper/Research/MediaWikiWebPageSourceReader.cs) | Guarded URL/fetch/robots/throttle/extraction pipeline and source-adapter transport. |
| [`Lorekeeper/Images/IProjectImageProvider.cs`](../../Lorekeeper/Images/IProjectImageProvider.cs), [`CodexProjectImageProvider.cs`](../../Lorekeeper/Images/CodexProjectImageProvider.cs), and [`ProjectImageGenerationRuntime.cs`](../../Lorekeeper/Images/ProjectImageGenerationRuntime.cs) | Image provider abstraction/adapter and singleton FIFO execution with regional-guide input normalization, previews, cancellation, and terminal waiters. |
| [`Lorekeeper/Images/ProjectImageModelCatalog.cs`](../../Lorekeeper/Images/ProjectImageModelCatalog.cs) | Code-owned image model IDs, supported quality levels, and request normalization for model, background, output format, and compression. |
| [`Lorekeeper/Ingest/IngestJobWorker.cs`](../../Lorekeeper/Ingest/IngestJobWorker.cs) and [`Lorekeeper/ImportExport/ProjectImportJobWorker.cs`](../../Lorekeeper/ImportExport/ProjectImportJobWorker.cs) | Representative database-gated hosted workers with feature-specific interrupted-work reconciliation. |

## Related chapters

- [Architecture index](../architecture.md) — global invariants and routing.
- [Runtime and host architecture](runtime-host.md) — DI composition, startup gate, endpoints, Electron, and configuration placement.
- [Narrative context](narrative-context.md) — retrieval indexes, ingest corpus, provenance, and graph/index semantics.
- [Assistants and chat](assistants-chat.md) — conversation selection, tool execution, streaming, compaction, and turn runners.
- [Composition and media](composition-media.md) — project assets, generation targets, Figures, Designed Pages, and provider-output placement.
- [Persistence, migrations, and import](persistence-migrations-import.md) — credential/job persistence, database-operation lifetimes, import atomicity, and recovery.
- [Press production](press-production.md) — publication preparation/render queues and native process containment.
- [Version history and synchronization](version-history-sync.md) — GitHub
  device-flow use, explicit remote attachment, durable automatic push transport,
  and snapshot credential boundary.

## Relevant verification

Compile and run the normal HTTP startup smoke check after provider or worker source
changes. Static inspection must confirm registration lifetime, startup-gate use,
fresh scope/database-operation boundaries, cancellation propagation, fail-safe
redaction before any log sink, and no credential logging outside the
Development-only dev-log layer. A successful build is not evidence that OAuth,
provider calls, embeddings, web search, guarded fetching, or image generation
works.

Exercise the exact integration before making such a claim: connect or refresh the
relevant account credentials, run the applicable chat/vision or embedding probe,
run discovery only for a user-configured provider, perform the selected web
provider/fetch flow, or complete and cancel an image job as appropriate. Include
failure-path checks for invalid credentials, timeouts, malformed provider
responses, cancellation, and process restart when those contracts change.
Browser UI validation remains user-authorized only. Exact base commands and test
boundaries are in
[Validation and documentation](validation-documentation.md).
