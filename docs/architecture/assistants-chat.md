# Assistants, Chat Runtime, and Review Workflows

## When to read

Read this chapter completely before changing any user-facing assistant, shared
chat UI/runtime, prompt composition, model selection, tool schema or payload,
tool streaming, transcript persistence, context compaction, Review Edits,
Contest Mode, or Editor revision workers. Also read it when changing an owning
domain service in a way that affects assistant parity, mutation notices,
revision checks, or post-tool refresh behavior.

Pair it with the chapter for every domain a tool reads or mutates. In
particular, use [narrative-context.md](narrative-context.md) for canon and
retrieval, [manuscript-authoring.md](manuscript-authoring.md) for manuscript and
annotation behavior, and [composition-media.md](composition-media.md) or
[publishing-model.md](publishing-model.md) for visual and publication tools.

## Scope and ownership

This chapter owns the six interactive assistant surfaces—Outline, Editor,
Writing Coach, Research, Images, and Publish—and their shared conversation
protocol. It covers active-turn lifetime, transcript replay, model selection,
tool invocation and streaming, prompt assembly, token accounting and
compaction, image attachments, mutation refresh signals, durable review
proposals, contests, and prose revision workers.

Feature adapters choose the charter, automatic context, and intentional subset
of domain tools for their surface. They do not reimplement domain validation or
persistence. Every mutation must cross the same owning application service used
by manual UI actions. Domain chapters remain authoritative for the state being
read or changed; this chapter is authoritative for how assistants reach those
boundaries and how the user reviews their work.

## Current architecture and invariants

### Six surfaces over one shared protocol

`ChatTurnSurface` has exactly six user-facing values: `Outline`, `Editor`,
`Research`, `Images`, `WritingCoach`, and `Publish`. Each surface owns one
project-scoped persisted conversation and ordered messages, including a nullable
selected-provider override. Feature services adapt their repository, prompt
context, tools, and typed streaming updates to the common `ChatTurnEngine`.
`ChatSurface` provides the shared transcript, composer, model picker, image
attachments, tool chips, scrolling, and textarea behavior.

The shared engine owns the provider-facing loop: persist the outgoing user
message, stream assistant text, reasoning, and function-call argument deltas,
invoke the registered application tools, return correlated results, persist the
completed visible transcript, and emit typed updates. Tool rounds retain the
provider's `FunctionCallContent` only until its correlated tool result has been
submitted. OpenAI-compatible clients must also preserve unknown immediate-round
tool-call metadata such as Gemini thought signatures. Cross-turn replay is
intentionally text-only: non-empty system, user, and assistant prose is retained;
tool calls, tool results, reasoning, and model-only visual attachments are not
replayed.

Reasoning deltas (`TextReasoningContent`) stream into a collapsed-by-default,
expandable transcript section per assistant message and are persisted on the
message row. Within a turn, completed-round reasoning is echoed back to the
provider in the next round's assistant message; transports without a reasoning
field drop it harmlessly. A round that ends with no visible text and no tool
calls fails visibly ("empty response" or "reasoning without an answer") instead
of persisting a silently empty completed message; failed rounds keep any
reasoning they received.

Because protocol metadata disappears on later turns, assistant prose is the
durable work log. A tool-using assistant narrates meaningful phases and closes
with a self-contained account of completed or staged work, decisions,
verification, diagnostics, and remaining actions. That prose supports historical
continuity but never replaces fresh context or focused rereads for mutable IDs,
revisions, or project state.

`ChatTurnRuntime` is a singleton coordinator keyed by project and surface.
Feature turn runners execute scoped work outside the Razor component lifetime,
buffer updates for reopened panels, and preserve explicit Stop as the
cancellation path. Active-turn ownership must not move into a component or
SignalR circuit. Surface-scoped maintenance leases make reset atomic against an
active or newly starting turn across windows.

Reset keeps the existing conversation root and its exact `SelectedProviderId`,
including a soft reference to a deleted or unavailable provider. It clears the
surface's message-owned image attachments, replaces all transcript messages with
that surface's normal initial greeting in one owning database write, and leaves
the root available for the panel's existing full reload path. The maintenance
lease prevents an active or newly starting turn from racing this replacement.

The composer stores unsent text in unencrypted browser/Electron local storage,
keyed by project and surface. Drafts survive remounts, navigation, and circuit
reloads without crossing project or assistant boundaries and are deleted on
send. They are outside SQLite, backups, and project export; clearing site data
removes them. Temporary selected attachments are not part of the draft, while
pasted or uploaded images are normal project assets and message associations.

### Model selection, tokens, and compaction

Each conversation's nullable `SelectedProviderId` is a soft reference. `null`
follows the current working global default. An explicit selection is usable only
while that connection/model passes chat readiness. If it is deleted or becomes
unavailable, the stored selection remains visible and the surface fails closed;
there is no silent fallback. Choosing the global default clears the override,
and resetting a conversation clears only its transcript and attachments; the
selection remains sticky until the author changes it.

The shared picker groups working models by connection, labels options by model,
marks the global default, and retains unavailable explicit selections for
recovery. A turn captures provider ID, model ID, display label, vision
readiness, and token limits in its runtime snapshot. Editing settings during a
turn cannot switch the provider halfway through client creation, compaction, or
tool execution. Contest candidates, revision workers, ingest jobs, embeddings,
image generation, and Press rendering keep their separate selection contracts.

All six surfaces use the active model's resolved input-token limit (explicit
provider-row value, then `ChatTokens` configuration mapping, then configured
default) and shared token counter. After a complete tool-call batch, reaching
90% of that limit tombstones completed function results oldest-first, one result
at a time, using
the exact structural tombstone marker and payload. The original
`FunctionCallContent`, call ID, name, arguments, result row, and audit rendering
remain intact; only the active in-memory result payload is replaced. Each
replacement is recounted, so newer results are preserved when an older result is
sufficient. Reasoning and marked tool-derived visual messages are removed only
after every result in that tool round is tombstoned. Reasoning is included in
server token accounting, and repeated compaction is idempotent.

Each turn emits the shared trim state (original/final token counts, token limit,
newly tombstoned call IDs, completed-round call IDs whose transient context was
removed, and limit-exceeded state) through its typed service, runner, and panel
update path. Live chips remain visible with their original audit result and use
`Active` or `ResultTombstoned` state for token projection.
When every eligible result is tombstoned and usage remains at or above 90%, the
current assistant row is persisted as failed, an actionable reset or
larger-context-model error is emitted, and no further provider request is made.
There is no summarization call, full-prune fallback, runtime warning, or new
synthetic `Chat Compacted` row/chip. Historical compaction rows remain inert and
renderable. Background ingest, contests, revision workers, image jobs, and
publication workers do not use interactive chat compaction.

### Prompts, context, and tool boundaries

`SystemPromptComposer` owns the single system-role prompt shape. It combines the
surface charter and code-owned workflow/tool rules with dynamic guidance,
protected Project Guidance and Book Brief, the direct-reference manifest, and
the feature's current working context. User-owned direction remains distinguishable
from code-owned rules and must not be persisted as seeded guidance. The shared
workflow instructions enforce active-canon precedence, read-only foreign
references, compact paging and staging, durable work logs, and honest
verification.

`SystemPromptComposer` adds the shared
`AssistantWorkflowInstructions.ProjectSearchQueryDiscipline` to every
composed assistant role; Writing Coach's intentionally separate static prompt
appends the same block. Its tool-scope sentence is role-specific: Editor
also has `find_impacted_chapters`, revision workers do not, Contest Candidate
has no project-search tools, and Research's external `web_search` remains a
separate contract. Project-search facets are concise 2–6-term source-content
queries with rare canonical names, aliases, exact events, objects, or other
distinctive terms first. Conceptual facets use hybrid `search_project`; exact
wording or known-source facets use filtered `lexicalOnly=true` search.
Independent facets are split into separate calls, then unioned and deduped by
stable IDs before paginated exact reads. Search continues until focused work
stops adding relevant evidence or resolving ambiguity, rather than using a
fixed call cap; one grounded reformulation is required before reporting no
project evidence. This discipline explicitly excludes external `web_search`.

Editor impact mapping narrows the same contract further. Each
`find_impacted_chapters` call carries one 2–6-term facet, with query and optional
coexisting exact keywords together staying below ten high-signal terms. Exact
chapter, entity, and event IDs are resolved first and limited to directly
affected anchors. `anchorChapterId` is used only for forward propagation from a
known origin; backward, whole-book, bidirectional, and direction-neutral audits
omit it. The Editor unions and deduplicates candidates across facets, verifies
each with focused `search_project` and `read_chapter` evidence, and rejects
proximity-only, generic-match-only, or unexplained-score-only candidates before
assigning revision workers. The complete requested continuity change belongs in
the later chapter-specific revision instructions, not in an impact query. An
exact downstream read that demonstrates the requested consequence is sufficient
even without a lexical hit, and explicit whole-book or multi-chapter scope must
not be silently narrowed because one facet is weak. Mapping and exact
verification precede mutation. For three or more verified semantic prose
chapters, Editor makes exactly one `start_revision_agents` call containing the
complete set. For one or two chapters, direct manuscript tools are the default,
unless the user explicitly requests one worker call for two genuinely distributed
chapters. The direct-versus-worker choice is made before mutation; discovery,
exact reads, and warranted canon updates finish before a separate assistant
round and tool batch for the worker call. For a set classified as one or two
chapters, direct manuscript mutations are limited to those verified targets.
Editor cannot edit the first two targets and delegate the remainder. Pure canon,
reusable style, typography, Designed Page, layout, and page-scene work remains
with the coordinator. In a mixed request, qualifying prose is delegated first
and the coordinator then applies its remaining owned work to the resulting
projected manuscript.
Workers receive semantic manuscript tools, may edit text and Figures, preserve
Designed Page references, and do not own composition or layout. Related Figure
operations may accompany an assigned prose revision, but Figure-only work does
not count toward the three-prose-chapter threshold.

`AssistantWorkflowInstructions.EditorContinuityMemory`, appended by
`EditorChatFor` to both vector-enabled and vector-disabled Editor prompts,
keeps Editor continuity memory narrower than ordinary drafting. Continuity-
sensitive drafting or revision requires focused project searches and relevant
current-record reads for grounding; search does not itself authorize
persistence, and a normal prose draft does not trigger a post-draft entity
reconciliation pass. Stable facts may be persisted only when user-directed or
corroborated by multiple current/canonical passages and durable beyond the
scene. Explicit canon changes route to the narrowest owner. Ordinary drafting
may add a metadata-free `RelevantTo` link for a materially relevant entity, but
a mere mention does not qualify. Chapter-involvement recaps, choreography,
temporary state, dialogue/quote archives, and assistant-invented drafting
texture remain outside entity memory. Voice memory is limited to compact
abstract traits supported by multiple passages or explicit direction; exact
dialogue is evidence rather than automatically reusable phrasing and is not
auto-stored. The examples in this guidance illustrate routing and are not a
whitelist.

All assistants receive a bounded one-hop direct-reference manifest and
origin-qualified list/search/read tools. Foreign IDs are valid only through the
active project's direct links; referenced evidence is read-only and never a
placement or mutation target. Tools return compact, paged envelopes with stable
identity and provenance. After compaction or across turns, the assistant must
reacquire any exact IDs, revisions, or values it needs.

Feature adapters must accept any valid JSON shape returned by a read-only tool.
Only object envelopes that can carry mutation notices are inspected for
workspace refresh metadata; arrays and scalars continue normally. Tool schemas,
prompts, persistence behavior, mutation notices, and UI consumers must evolve
together. Results should return changed identities, revisions, counts,
diagnostics, and recovery guidance—not entire unchanged documents or binary
payloads.

Large or sensitive mutations use bounded, revision-safe staging. Editor
manuscript changes validate and apply one complete operation set in a single
call against the exact source revision; when Review Edits is enabled, that same
call creates the in-memory projected overlay and pending change for approval.
The operation contract distinguishes additive insertion from revision:
`InsertBlock` never supersedes existing prose, single-block revisions preserve
identity with `ReplaceBlockText`, and multi-block rewrites must replace or
delete every superseded source block in the atomic batch. Text/structure
mutation results include operation and block counts, diagnostics, a full source
hash, and bounded readback ranges. Editor must read every returned range from
the persisted or staged source before continuing; insertion-only text against a
non-empty manuscript remains legal but returns the non-blocking
`MANUSCRIPT_INSERT_WITHOUT_REPLACEMENT` warning.
Page and cover scenes use persisted, hashed, expiring, project/conversation-
scoped stages that cannot be replayed. Image generation creates an unattached
durable image job; another explicit mutation places or associates the completed
asset. Failed, cancelled, or stale calls must not create partial destination
state. A completed assistant mutation may be captured by the separate
version-history checkpoint service; that durable Git snapshot history is not
the process-lifetime Undo/Redo history described by the manuscript chapter.

### Surface charters

Outline is structural planning and canon. Its automatic snapshot includes
Project Guidance, Book Brief, structure-only format guidance, acts, chapters,
synopses, beats, facts, chapter/beat entity associations, entity/source
inventories, and canonical-source distinctions. It mutates Book Brief, outline,
entities, relationships, and facts; it may read chapter bodies only for focused
reconciliation. It has no manuscript, Figure, Designed Page, page-setup, or
publication geometry mutation tools. Its only image workflow creates a
geometry-free canonical appearance candidate and explicitly associates the
inspected result with an entity.

Editor is the complete Core/release authoring assistant. It receives the active
chapter's full `agent-manuscript-v1` projection and versioned named-style
definitions automatically and can read
or mutate the outline, canon, manuscript, annotations, Figures, Designed Pages,
styles, composition, page setup, and image workflows appropriate to the
protected `EditorContentTarget`. The conversation is project-scoped, not
chapter-scoped. In release-content mode the target is fixed; outline/canon and
unsafe shared-style mutations are omitted. Page and canvas previews are the
visual verification gates for pagination and composition work.

Writing Coach owns project-level coaching around editable writing samples. Its
tools are read-only for project facts, the current sample, direct-reference
narrative evidence, and bounded canonical visuals. It does not mutate canon,
outline, manuscript, or publishing state.

Research combines bounded project/direct-reference reads with configured web
search, safe cached page reads, image/source promotion, and staged graph
mutations. Search and fetch security remains in Research services. Its activity
view derives from touched entities and accessed cached sources rather than a
separate assistant-authored log.

Images is concept art and visual canon. It can read narrative context for
grounding and can mutate project images, masks, canonical entity associations,
and the user-approved Book Brief Visual Direction. It cannot author Figures,
Designed Pages, covers, or publication placements. Visual Direction uses an
exact previously-read value so a stale turn cannot overwrite newer direction.

Publish is the Core Book/release production assistant. Each turn receives the
complete outline and the protected visible Publish surface—overview, cover,
prose section, or designed section—with exact revisions and selection. It can
mutate Core/release metadata and settings, publication sections, covers, page
setup/styles where allowed, and preparation workflows. It cannot mutate chapter
manuscript or reorder the project outline. Low-level renderer invocation, raw
profile versions, ISBN invention, and vendor-acceptance claims are unavailable.

### Review Edits and approval

Reviewable tool mutations persist as `AiChangeBatch` and `AiChange` rows.
Outline and Research stage canon/structure changes through their feature staging
contexts. Editor manuscript changes can write directly or enter an in-memory
projected overlay when Review Edits is enabled. `IAiChangeApprovalService` is
the single approval/rejection boundary; it enforces dependencies, revision and
semantic concurrency, reconnects approved Editor changes to their originating
authoring-history turn batch, and leaves genuine conflicts visible and
rejectable.

Only plain paragraph/scene-break changes use the line-oriented Editor Review
tab. Figure, Designed Page, inline formatting, named-style, semantic-structure,
or other visual changes remain in the pending-edits modal with distinct text,
structure, and visual diffs. A staged Designed Page preallocates its composition
and block IDs, and acceptance creates its manuscript reference, composition, and
exact authoring variant atomically. Artwork placement remains a separate
revision-checked mutation using an already-completed image.

Repository updates for review rows attach or update only the intended root.
Detached `Batch.Changes` graphs must never be attached during status changes.
Reads are no-tracking; mutations reuse the locally tracked root inside one short
write operation and dispose it immediately after commit.

### Contest Mode and revision workers

Contest Mode remains a one-chapter terminal context and exact target/chapter
manuscript snapshot, then runs independent selected models without tools; it does
not expose the multi-chapter revision-worker workflow. Candidate raw responses
and validated semantic operation proposals persist independently. The
Review workspace is reachable while the batch runs, streams candidate status,
and allows explicit per-candidate resolution. Keeping the chat component mounted
while its pane is hidden preserves the live subscription.

The captured Contest system transcript excludes the automatic Current Chapter
context item. Each candidate instead receives the exact batch-source
`agent-manuscript-v1` projection once, generated from the canonical manuscript
stored on the batch. Canonical `OriginalManuscriptJson` remains the durable
validation/audit source and is never appended raw to a candidate prompt.

Editor revision agents are same-turn, prose-only worker sessions assigned to
specific chapters. For three or more verified semantic prose chapters, the
coordinator makes one call containing the complete set; one or two chapters use
direct manuscript tools by default unless the user explicitly requests one
two-chapter worker call. The coordinator validates assignments, persists the job
and session records, runs bounded parallel workers, and receives only compact
IDs, statuses, summaries, errors, and pending-change IDs. Full prompts,
operations, proposals, raw responses, and worker transcripts remain in durable
session detail and never inflate the parent model result. This classification
happens before any manuscript mutation, so Review Edits cannot stage a direct
change that later blocks delegation for the same chapter.

Each session captures the active Editor turn's exact provider/model row before
workers start. A worker resolves that explicit provider ID through normal chat
model selection and fails closed if it is unavailable or its captured model has
changed; it never falls back to the global default. Job setup disposes its write
operation before launching parallel scoped workers, and finalization reloads
the job in a separate tracked write operation so no worker shares an ambient
database context or duplicates its tracked job graph. A worker that returns no
tool call receives one corrective retry with its prior response and an explicit
terminal-apply instruction; it may still perform needed read/search grounding.
If its second response also omits tools, the session is Invalid with distinct
empty-output or text-only detail. A partially successful job remains Completed
when valid edits succeeded, but returns an error summary for every incomplete
session alongside the full session details.

Each worker uses paginated grounding and filtered source reads, then terminates
through the semantic manuscript operation boundary. Workers receive semantic
manuscript tools only, may edit text and Figures, preserve Designed Page
references, and do not perform composition, layout, or page-scene work; the
coordinator owns those operations. Figures are a semantic exception only within
an assigned prose revision and do not independently trigger delegation. Its
automatic manuscript context plus read/inspect tools use the same sparse
projection and preserve absolute indexes, revision, source hash, and stable IDs.
With Review Edits enabled,
its pending change is correlated to the parent tool call and adopted into the
active Editor overlay, while the stored chapter remains unchanged until
approval. Core and release workers build automatic context, refresh, validate
staleness, and apply against the same protected `EditorContentTarget` captured
by the job; Core chapter JSON is never used as the concurrency check for a
release worker. Because the worker mutation is terminal, the worker audits
source-block disposition before submission and the parent Editor rereads the
affected projected manuscript before reporting completion. The coordinator
cancels and awaits any outstanding progress read
before disposing the async enumerator. Completion, cancellation, and failure
must leave durable terminal state and no concurrent-disposal error.

## Key files and file families

| File or family | Architectural role |
|---|---|
| [`Lorekeeper/ChatTurns/`](../../Lorekeeper/ChatTurns/) | Shared surface identity, active-turn lifetime, protocol engine, text-only replay, compaction, message-store boundary, and image attachments. |
| [`Lorekeeper/Components/Chat/`](../../Lorekeeper/Components/Chat/) | Shared chat shell, model picker, transcript models/token projection, tool chips, and composer behavior. |
| [`Lorekeeper/Llm/SystemPromptComposer.cs`](../../Lorekeeper/Llm/SystemPromptComposer.cs) and [`AssistantWorkflowInstructions.cs`](../../Lorekeeper/Llm/AssistantWorkflowInstructions.cs) | One system-role prompt pipeline and code-owned cross-surface workflow/tool rules. |
| [`Lorekeeper/Outline/OutlineCollaborationService.cs`](../../Lorekeeper/Outline/OutlineCollaborationService.cs), [`OutlineCollaborationTools.cs`](../../Lorekeeper/Outline/OutlineCollaborationTools.cs), working-context/staging/approval helpers, and [`OutlineChatTurnRunner.cs`](../../Lorekeeper/Outline/OutlineChatTurnRunner.cs) | Outline assistant adapter, automatic context, structural/canon tools, review staging/approval, diffs, and turn updates. |
| [`Lorekeeper/EditorChat/`](../../Lorekeeper/EditorChat/) | Editor adapter/tools, one-step revision-safe manuscript apply, Review staging, contests, revision jobs/workers, and active-turn updates. |
| [`Lorekeeper/Writing/`](../../Lorekeeper/Writing/) | Writing Coach service, read-only tool catalog, runner, and writing-sample application boundary. |
| [`Lorekeeper/Research/ResearchService.cs`](../../Lorekeeper/Research/ResearchService.cs), [`ResearchTools.cs`](../../Lorekeeper/Research/ResearchTools.cs), [`ResearchChatTurnRunner.cs`](../../Lorekeeper/Research/ResearchChatTurnRunner.cs), and [`ResearchTurnUpdate.cs`](../../Lorekeeper/Research/ResearchTurnUpdate.cs) | Research chat adapter/tools, streaming, and turn lifetime; guarded fetch and cached-source ownership remain in providers and narrative context. |
| [`Lorekeeper/ImagesChat/`](../../Lorekeeper/ImagesChat/) | Images assistant, turn context, visual-canon tools, job reconnection, and streaming updates. |
| [`Lorekeeper/Publish/PublishChatService.cs`](../../Lorekeeper/Publish/PublishChatService.cs), [`PublishChatTurnRunner.cs`](../../Lorekeeper/Publish/PublishChatTurnRunner.cs), and [`PublishTurnUpdate.cs`](../../Lorekeeper/Publish/PublishTurnUpdate.cs) | Publish conversation, protected visible-target context, turn lifetime, and refresh updates; the publication chapter owns its domain tool catalog. |
| Conversation/message, `AiChange*`, `Contest*`, and `EditorRevision*` models and repositories | Durable transcript, model-selection, review, contest, and worker audit boundaries for all six surfaces. |
| Assistant `*ChatPanel.razor` components under `Lorekeeper/Components/Pages/Projects/` | Thin adapters that hydrate transcripts, subscribe to active turns, render review/progress state, and route mutation notices to owning workspaces. |

## Related chapters

- [narrative-context.md](narrative-context.md) owns Project Guidance, Book
  Brief, canon, outline, graph, retrieval, direct references, ingest evidence,
  and automatic context projections.
- [manuscript-authoring.md](manuscript-authoring.md) owns manuscript operations,
  editor targets, annotations, Book Text Styles, process-lifetime manual
  Undo/Redo, and durable Review Edits baselines. Successful assistant mutations
  invalidate affected manual history and are not themselves undoable.
- [providers-background.md](providers-background.md) owns provider resolution,
  OAuth, wire compatibility, request limits, and non-chat background queues.
- [composition-media.md](composition-media.md) owns project images, generation
  jobs, Figures' assets, Designed Page/canvas semantics, and visual previews.
- [publishing-model.md](publishing-model.md) and
  [press-production.md](press-production.md) own the publication state and
  truthful validation/rendering claims exposed through Publish tools.
- [version-history-sync.md](version-history-sync.md) owns deterministic
  creative snapshots and assistant checkpoint capture; chat transcripts,
  messages, and composer state remain outside those snapshots.

## Relevant verification

- Search all six adapters, their repositories/models, turn runners, panel
  components, prompt composer, shared engine, and affected domain services when
  changing a shared contract.
- Confirm active turns survive component disposal, Stop cancels explicitly,
  reset acquires maintenance ownership, and no DB context or write lease spans a
  provider stream or background wait.
- Verify provider/model snapshots remain stable for the entire turn and that an
  unavailable explicit selection fails closed without erasing the transcript.
- Exercise token accounting and result tombstoning at complete tool-batch
  boundaries; confirm reasoning is counted, oldest results trim first, pairing
  and audit rows remain intact, replay stays text-only, and exhausted eligible
  results fail closed before another provider request.
- For tool changes, inspect schemas, prompt guidance, result shapes, mutation
  notices, staging/revision checks, persistence, UI refresh consumers, and the
  owning manual service path together.
- For manuscript text/structure mutations, confirm insertion-only work warns
  without being rejected, returned readback ranges include adjacent context and
  stay within the 100-block read limit, staged and persisted reads match the
  returned revision/hash, and corrections use a fresh operation batch.
- For Review/Contest/revision changes, confirm dependency handling, exact target
  ownership, durable terminal state, visible unresolved conflicts, projected
  overlays, authoring-history correlation, and cancellation cleanup.
- Do not add ordinary assistant, UI, editor, or service automated tests. Use
  compilation, static inspection, and explicitly authorized manual integration
  checks; migration-only persistence changes may use the approved migration
  safety fixtures.
- Run `dotnet build Lorekeeper.sln` and the HTTP startup smoke check for normal
  source changes. Provider calls, model-specific tool behavior, browser UI,
  image generation, and publishing flows require explicit integration exercise
  before claiming they work.
