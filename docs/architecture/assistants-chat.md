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
compaction, image attachments, mutation refresh signals, Git-backed Review Edits,
contests, and prose revision workers.

Feature adapters choose the charter, automatic context, and intentional subset
of domain tools for their surface. They do not reimplement domain validation or
persistence. Every mutation crosses the same owning application service used by
manual UI actions and updates live SQLite immediately. Domain chapters remain
authoritative for the state being read or changed; this chapter is authoritative
for how assistants reach those boundaries and how the user reviews their work
against Git HEAD.

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
with a self-contained account of completed work, decisions,
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

Chat data is the retained exception to the disposable job-history rule applied
elsewhere: transcripts, tool-call rows, and their visual BLOBs persist until
the author presses Reset, which permanently deletes them through the owning
cascades. Compaction is in-memory only and never rewrites persisted rows.

The composer stores unsent text in unencrypted browser/Electron local storage,
keyed by project and surface. Drafts survive remounts, navigation, and circuit
reloads without crossing project or assistant boundaries and are deleted on
send. They are outside SQLite, backups, and project export; clearing site data
removes them. Temporary selected attachments are not part of the draft, while
pasted or uploaded images are normal project assets and message associations.

Images Chat builds one authoritative snapshot of the union of current-message
and persistent conversation attachments at the start of each turn. The provider
receives an ordered attachment manifest containing each image's complete project-
library metadata and, when the selected provider is vision-ready, the full
image bytes. That same snapshot is available to the Images tool context:
`list_project_images` excludes its IDs and `read_project_image` short-circuits
them without another repository read or another visual delivery. Because
cross-turn replay remains text-only and providers do not retain Lorekeeper's
binary context, a later turn must resolve and resubmit its persistent attachment
bytes once; this is distinct from redundant tool-driven rereads within a turn.

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
recovery. A turn captures provider ID, model ID, display label, resolved input
budget, and resolved account/catalog metadata: account identity, origin,
supported efforts, capabilities, catalog version/source/date, and effective
effort. Editing settings during a turn cannot
switch those values halfway through client creation, compaction, or tool
execution. Contest candidates, revision workers, ingest jobs, embeddings, image
generation, and Press rendering keep their separate selection contracts.
For OpenAI account models, client creation resolves the account-owned access token
and persisted external account identity together. An unavailable credential or
reauthentication requirement fails the selected provider explicitly; assistants
never infer account identity from token shape or substitute a different model.

All six surfaces use the active model's resolved input-token limit (explicit
provider-row value, then the bundled account catalog when applicable, then the
`ChatTokens` configuration mapping and configured default) and shared token
counter. Account-backed model discovery never participates in this resolution.
After a complete tool-call batch, reaching
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
references, compact paging and owning-service mutations, durable work logs, and honest
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

Large or sensitive mutations use bounded, revision-safe owning-service writes.
Editor manuscript changes validate and apply one complete operation set in a
single call against the exact source revision. Review Edits changes checkpoint
policy only: when enabled, the mutation is written to live SQLite and remains
uncheckpointed for Git review; when disabled, the completed mutating turn may
checkpoint the complete live project.
The operation contract distinguishes additive insertion from revision:
`InsertBlock` never supersedes existing prose, single-block revisions preserve
identity with `ReplaceBlockText`, and multi-block rewrites must replace or
delete every superseded source block in the atomic batch. Text/structure
mutation results include operation and block counts, diagnostics, a full source
hash, and bounded readback ranges. Editor must read every returned range from
the live source before continuing; insertion-only text against a
non-empty manuscript remains legal but returns the non-blocking
`MANUSCRIPT_INSERT_WITHOUT_REPLACEMENT` warning.
Page and cover scenes use persisted, hashed, expiring, project/conversation-
scoped stages that cannot be replayed. Image generation creates an unattached
durable image job; another explicit mutation places or associates the completed
asset. Free-standing generation and unmasked editing default to a provider-valid
raster with the configured Core Book page aspect; a concrete layout target
replaces that default when the composition must honor a Figure, page, frame, or
cover. Publish exposes only target identity, aspect, protected regions, and exact
surface bounds to the model; its tool schema has no pixel-size or density controls,
and its results return the application-prepared placement image ID without native
or production raster diagnostics. The application selects the target-specific
native request and any required derivative. Editor, Images, and
Outline retain the moderate concept-art default unless their tools explicitly
request `minimumDpi` and an optional custom aspect. Free-standing DPI uses the
exact Core Book page or its largest fitting custom-aspect rectangle. An
infeasible explicit minimum outside Publish fails before job creation; a concrete
size cannot bypass that explicit minimum.
The Images workspace refreshes its library and job projections only for an
explicit image-mutation update; ordinary tool completion, assistant completion,
and turn errors remain local chat updates and must not reload the workspace.
Same-aspect up-resolution uses the ordinary source-driven edit operation: the
latest accepted image is supplied directly, the complete framing and content
are preserved, and the prompt restates the requested change and constraints
while asking for credible reconstructed detail without outward extension or
cropping. Unmasked edits make only the requested change plus directly dependent
adaptations, preserving named invariants, unrelated scene details, and existing
open space unless the request explicitly changes them. Intentional expansion is a distinct outpainting
request whose prompt describes the larger framing, including left-and-right or
above-and-below surroundings as appropriate. Regional guides are reserved for
genuinely localized or otherwise hard-to-describe edits. They are soft visual
guidance, not pixel protection, and are unavailable for broad restyling,
reframing, resizing, layout-bound targets, or reserved-region work. Assistant
tool catalogs expose no standalone mask-creation tool and no reusable mask ID or
label. `edit_project_image` accepts only optional inline
`regionalGuideShapes`; the owning workflow validates and persists the resulting
guide atomically with that edit. Every generated or edited result is inspected
in full before the assistant presents, promotes, associates, or places it,
including the area outside any guide. Deterministic resize creates a new
unattached source-linked asset and adds no visual detail; it is never evidence
of publication-quality enhancement.

Images assistants use Flare by default and choose Sunburst explicitly for
demanding detail, exact raster text, or difficult composition. xhigh/max
quality is reserved for a concrete unmet need. Requested model, background,
quality, output format, and compression are captured per job; provider-reported
values remain separate audit evidence. Transparent output keeps an empty alpha
backdrop unless the brief explicitly requests background elements, while opaque
output receives an appropriate plain or scene-integrated background. User-grounded
exclusions are allowed; assistants do not invent blanket exclusions.

Publish target-bound generation always uses crop-to-fill. For unusually narrow
or wide targets, the application chooses the closest supported native aspect,
keeps important content inside the centered target window, and prepares a
same-aspect derivative large enough to cover the physical target. The assistant
does not receive or manage a panel plan. Release covers still prefer server-owned
Back, Spine, and Front regions and require annotated plus clean whole-wrap
inspection.
Failed, cancelled, or stale calls must not create partial destination state. A completed assistant mutation may be captured by the separate
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
visual verification gates for pagination and composition work. When Review Edits
is enabled, Editor also receives a protected compact summary of pending targets
and read-only `list_pending_review_changes` and `read_pending_review_diff` tools.
Those tools are project-scoped, revision-bound evidence only; they do not
approve, reject, or mutate changes and are not exposed to Contest Preparation or
revision-worker turns.

Writing Coach owns project-level coaching around editable writing samples. Its
tools are read-only for project facts, the current sample, direct-reference
narrative evidence, and bounded canonical visuals. It does not mutate canon,
outline, manuscript, or publishing state.

Research combines bounded project/direct-reference reads with configured web
search, safe cached page reads, image/source promotion, and direct graph
mutations through Research services. Search and fetch security remains in
Research services. Its activity
view derives from touched entities and accessed cached sources rather than a
separate assistant-authored log.

Images is concept art and visual canon. It can read narrative context for
grounding and can mutate project images, canonical entity associations, and the
user-approved Book Brief Visual Direction. It may provide an inline regional
guide only as part of the edit that consumes it; it cannot create or reuse masks
independently. It cannot author Figures, Designed Pages, covers, or publication
placements. Visual Direction uses an exact previously-read value so a stale turn
cannot overwrite newer direction.

Publish is the Core Book/release production assistant. Each turn receives the
complete outline and the protected visible Publish surface—overview, cover,
prose section, or designed section—with exact revisions and selection. It can
mutate Core/release metadata and settings, publication sections, covers, page
setup/styles where allowed, and preparation workflows. It cannot mutate chapter
manuscript or reorder the project outline. Low-level renderer invocation, raw
profile versions, ISBN invention, and vendor-acceptance claims are unavailable.
When it creates an ordinary custom single-page publication section without a
user-directed side, it uses the next available leaf rather than inventing a
parity blank. Contents retains its authored recto start, so any required numbered
parity leaf sits immediately before Contents instead of before preceding custom
matter.
For a full-wrap cover it uses the exact region tools to read and preview Back,
Spine, or Front before editing, then fills only the selected region or changes
the persisted spine direction with an expected revision. A narrow spine
generation target reports its aspect, protected geometry, fingerprint, and a
reusable target token; Lorekeeper prepares the crop-to-fill asset internally.
Generation remains unattached and never stretches to fit. Spine
art contains no baked-in words, title/author remain real
text above the art, US/English defaults to top-to-bottom unless the user asks
otherwise, and successful mutations are followed by annotated spine and
whole-wrap inspection plus clean final previews.
Bindable cover copy uses the token catalog returned by the cover read. The
assistant can place `{{title}}`, `{{subtitle}}`, `{{author}}`, `{{spineText}}`,
or `{{description}}` inside any text frame, including repeated author or title
frames on the spine, and may combine tokens with literal copy. Large cover-scene
stages must name the same exact surface role that was read and previewed; apply
is surface-bound and must not target an implicit default. `{{description}}`
means the effective Description returned from the visible Book details; the
assistant must not invent or maintain separate back-cover metadata.
Every successful durable Core or release cover mutation, including an
exact-surface patch or placement, emits a cover-specific workspace update as
soon as that tool completes. An open cover canvas reloads from that update
during the turn and preserves a returned object selection; it does not wait for
the assistant's final message before reflecting the persisted scene.

### Review Edits and approval

Review Edits is a project workflow preference, not a second manuscript or
overlay store. The top bar places the Review Edits toggle beside Checkpoint and
shows Pending changes with its count. Enabling the toggle keeps existing dirty
work and lets assistant tools mutate live SQLite without checkpointing. When it
is disabled, a completed mutating assistant turn checkpoints the complete live
project; read-only and no-op turns do not checkpoint. The shared checkpoint
adapter checks the preference itself, so image and Publish callers cannot bypass
the policy. A checkpoint failure stays visible and leaves the project dirty and
reviewable.

Pending Review compares Git HEAD with the current live state. Its chooser lists
affected `(chapter, Core|edition)` targets and an Other changes aggregate rather
than individual assistant batches. The
Review page is the full manuscript review surface: it groups semantic text edits
by stable block ID, supports inline text
editing and Approve/Undo, and keeps insertions, deletions, moves, formatting,
Figures, and Designed Pages semantic. Figure metadata includes editable
captions; Designed Pages expose visual before/after previews. Partial approval
creates a `ReviewApproval` checkpoint for selected live semantic groups; Approve
All checkpoints the complete live snapshot. Operations reject stale Git HEAD,
live-manuscript, or contest tokens.

A clean chapter/target opens Last approved mode. The service finds the newest
affecting approved commit (cached by HEAD SHA, chapter, and target) and compares
that commit with its parent. Historical Undo writes the parent value into live
SQLite and therefore creates a normal pending reversal. Manuscript groups and
chapter-owned Designed Pages support historical undo; composition restore also
repairs or refuses coupled manuscript references so it cannot orphan a Designed
Page block. Other changes display only current pending work. There is no
semantic-manuscript warning or plain-text normalization path.

Repository review operations use the project mutation and database write
boundaries, retain concurrency tokens, and leave any failed or stale operation
visible for recovery. Approved work advances Git; unapproved local work remains
dirty and blocks push or checkout until it is approved or undone.

While Review Edits is enabled, the normal Editor assistant's pending-review inspector rereads the
current review for every request and emits a bounded compact summary only when
pending work exists. `list_pending_review_changes` returns deterministic
outline-ordered chapter Core/release targets followed by entity, relationship,
and Other entries; `read_pending_review_diff` returns semantic manuscript
hunks/rows, Designed Page details, or bounded comparer text for one exact target.
Both envelopes carry an opaque `reviewRevision` through every continuation and
return `REVIEW_STALE` when live state or approved history changes. The assistant
must relist after that error, keep unrelated pre-existing pending work separate,
and use owning mutation/readback or visual-verification tools for any change.

### Contest Mode and revision workers

Contest Mode is a one-chapter, exact-scope prose review context with one durable
candidate draft per configured provider/model. The coordinating Editor assistant
first uses its read-only context tools to establish a concise standalone task,
the current manuscript revision, and two stable boundary anchors for an exact
replacement span anywhere in the chapter. A null before anchor means document
start, a null after anchor means document end, and both null means the full
chapter. The replacement begins after the before anchor and ends before the after
anchor, so the anchors and every block outside the span remain immutable. The
interior may cross scene breaks and include Figures, Designed Pages, headings,
lists, or other semantic structure; all such interior blocks are intentionally
replaced because the coordinator selected that span. The contest service validates
the source fence and boundary order before launch; empty spans at either document
edge, between adjacent anchors, or in an empty full chapter are valid insertion
targets. Each candidate receives an
immutable context branch plus the explicit standalone writing brief and exact
source manuscript, runs with tool use explicitly disabled, and returns natural
Markdown prose rather than JSON or semantic operations. The service parses any
nonzero prose into fresh semantic Paragraph and SceneBreak blocks, normalizing
ATX headings, blockquotes, list prefixes, links/images, emphasis, and inline code
into paragraph text. Standalone `***`, `###`, `---`, and `___` become scene
breaks. Empty, machine-readable, stale, or invalid-boundary results fail closed;
apart from those scene separators, structural output is never interpreted as a
manuscript operation.

The Review page shows an
equal-width, keyboard-accessible selector containing only configured slots
(`Contest 1 | Contest 2 | Contest 3`); running candidates show progress, failed
candidates are disabled with their error, and completed candidates are
selectable. The selected candidate persists, defaulting to the first completed
candidate. The page renders only that candidate's semantic diff against the
captured contest original.

Each completed candidate has an independent draft. Inline text edits, rejected
text rows, Figure caption edits, and supported semantic formatting stay in that
candidate; switching candidates never mixes changes or writes directly to the
live manuscript. Structural operations remain atomic, and Reset candidate
restores the generated proposal. Resolve with selected result is available only
after every configured candidate is completed, failed, or invalid. Resolution
checks the captured original revision/hash, applies the selected draft
atomically, marks all candidates resolved, releases the project-wide Editor
lock, and leaves the result as a normal pending Git-backed change. Discard keeps
the original live manuscript. A running contest must be cancelled through an
application-owned confirmation first, and Discard remains available when every
candidate fails.

While a contest is running or awaiting resolution, the entire Editor is locked:
manual manuscript, layout, Figure, Designed Page, assistant, revision-worker,
and new-contest mutations are rejected by owning services. Read-only navigation
remains available with a persistent link back to the contested chapter's Review
page, and the user's chat and Assistant Memory pane state is preserved. Starting
or updating a contest only refreshes its lock and candidate state; it does not
open Review, change chapter or content target, collapse panes, or change the
current mode. The initiating Editor turn switches only its current mode to Edit
when it completes with an unresolved contest, leaving that Edit surface
read-only. Candidate editing and resolution use the explicit contest-authorized
path, and the completed contest is shown in the normal Review page only when the
user opens Review or follows the contest review/lock-notice action. Contest
configuration and provider controls remain unavailable while candidates exist.
The lock is restored at startup whenever an unresolved contest exists.

New contests persist a versioned immutable context envelope containing the
coordinator's explicit task/target fence and its captured text/tool evidence.
Candidate prompting omits the coordinator system prompt so its tool instructions
cannot become contestant instructions; captured user, assistant, and tool
material is quoted as evidence. Each candidate also receives the exact
batch-source `agent-manuscript-v1` projection once, generated from the canonical
manuscript stored on the batch. Canonical `OriginalManuscriptJson` remains the
durable validation/audit source. Legacy context snapshots remain inert audit data
for already persisted contests and are not replayed through the new runner path.

Editor revision agents are same-turn, prose-only worker sessions assigned to
specific chapters. For three or more verified semantic prose chapters, the
coordinator makes one call containing the complete set; one or two chapters use
direct manuscript tools by default unless the user explicitly requests one
two-chapter worker call. The coordinator validates assignments, persists the job
and session records, runs bounded parallel workers, and receives only compact
IDs, statuses, summaries, errors, and live-mutation summaries. Full prompts,
operations, proposals, raw responses, and worker transcripts remain in durable
session detail and never inflate the parent model result. This classification
happens before any manuscript mutation, so the coordinator can assign verified
targets without depending on a review overlay.

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
session alongside the full session details. Completed worker mutations are
reviewed as part of the Git HEAD-to-live comparison rather than adopted through
a pending-worker query. Cancellation durably
finalizes the job and sessions, returns that Cancelled result to the shared tool
boundary, and lets the invocation layer stop the parent turn without a second
service-level cancellation exception.

Each worker uses paginated grounding and filtered source reads, then terminates
through the semantic manuscript operation boundary. Workers receive semantic
manuscript tools only, may edit text and Figures, preserve Designed Page
references, and do not perform composition, layout, or page-scene work; the
coordinator owns those operations. Figures are a semantic exception only within
an assigned prose revision and do not independently trigger delegation. Its
automatic manuscript context plus read/inspect tools use the same sparse
projection and preserve absolute indexes, revision, source hash, and stable IDs.
With Review Edits enabled, its live mutation remains uncheckpointed; with the
preference disabled, the runner checkpoints the complete project. Core and
release workers build automatic context, refresh, validate staleness, and apply
against the same protected `EditorContentTarget` captured by the job; Core
chapter JSON is never used as the concurrency check for a release worker.
Because the worker mutation is terminal, the worker audits source-block
disposition before submission and the parent Editor rereads the affected live
manuscript before reporting completion. The coordinator
cancels and awaits any outstanding progress read
before disposing the async enumerator. Completion, cancellation, and failure
must leave durable terminal state and no concurrent-disposal error.

Publish preparation results include the persisted image-preparation summary:
active threshold, created and reused derivatives, replaced references, and
actionable failures. The assistant reports this existing job evidence; it does
not choose alternate DPI rules, perform provider generation, or reproduce the
managed publication model's layout math. Permanent replacements are system-owned publication mutations and
invalidate Review Edits/manual history through their content owners just like
other direct assistant-visible changes.

### Accepted M2-M5 assistant contract

This is accepted implementation guidance, not a claim that these next-version
tools are already live. Assistant reads and mutations of Designed Pages use
page identity plus protected Core/release content/variant revisions; a placement
does not grant write ownership of shared page content. Assistant state-consuming
operations use the same authoring mutation fence as manual consumers and must
stop on a dirty client that cannot flush. Assistant mutations are never manual
Undo actions and invalidate affected authoring generations only after a
successful committed batch.

Source links in assistant-visible output carry validated durable `SourceLocation`
data and preserve unavailable/outdated/ambiguous status rather than guessing.
Manuscript projections and tools preserve `ManuscriptPosition`, table/note
identities, citation clusters, and Designed Page citation/note atoms. An
assistant evidence link never silently creates a manuscript citation.

## Key files and file families

| File or family | Architectural role |
|---|---|
| [`Lorekeeper/ChatTurns/`](../../Lorekeeper/ChatTurns/) | Shared surface identity, active-turn lifetime, protocol engine, text-only replay, compaction, message-store boundary, and image attachments. |
| [`Lorekeeper/Components/Chat/`](../../Lorekeeper/Components/Chat/) | Shared chat shell, model picker, transcript models/token projection, tool chips, and composer behavior. |
| [`Lorekeeper/Llm/SystemPromptComposer.cs`](../../Lorekeeper/Llm/SystemPromptComposer.cs) and [`AssistantWorkflowInstructions.cs`](../../Lorekeeper/Llm/AssistantWorkflowInstructions.cs) | One system-role prompt pipeline and code-owned cross-surface workflow/tool rules. |
| [`Lorekeeper/Outline/OutlineCollaborationService.cs`](../../Lorekeeper/Outline/OutlineCollaborationService.cs), [`OutlineCollaborationTools.cs`](../../Lorekeeper/Outline/OutlineCollaborationTools.cs), and [`OutlineChatTurnRunner.cs`](../../Lorekeeper/Outline/OutlineChatTurnRunner.cs) | Outline assistant adapter, automatic context, direct structural/canon mutations, and turn updates. |
| [`Lorekeeper/EditorChat/`](../../Lorekeeper/EditorChat/) | Editor adapter/tools, one-step revision-safe manuscript apply, Git-backed Review page, contests, revision jobs/workers, and active-turn updates. |
| [`Lorekeeper/Writing/`](../../Lorekeeper/Writing/) | Writing Coach service, read-only tool catalog, runner, and writing-sample application boundary. |
| [`Lorekeeper/Research/ResearchService.cs`](../../Lorekeeper/Research/ResearchService.cs), [`ResearchTools.cs`](../../Lorekeeper/Research/ResearchTools.cs), [`ResearchChatTurnRunner.cs`](../../Lorekeeper/Research/ResearchChatTurnRunner.cs), and [`ResearchTurnUpdate.cs`](../../Lorekeeper/Research/ResearchTurnUpdate.cs) | Research chat adapter/tools, streaming, and turn lifetime; guarded fetch and cached-source ownership remain in providers and narrative context. |
| [`Lorekeeper/ImagesChat/`](../../Lorekeeper/ImagesChat/) | Images assistant, turn context, visual-canon tools, job reconnection, and streaming updates. |
| [`Lorekeeper/Publish/PublishChatService.cs`](../../Lorekeeper/Publish/PublishChatService.cs), [`PublishChatTurnRunner.cs`](../../Lorekeeper/Publish/PublishChatTurnRunner.cs), and [`PublishTurnUpdate.cs`](../../Lorekeeper/Publish/PublishTurnUpdate.cs) | Publish conversation, protected visible-target context, turn lifetime, and refresh updates; the publication chapter owns its domain tool catalog. |
| Conversation/message, `Contest*`, and `EditorRevision*` models and repositories | Durable transcript, model-selection, independent contest drafts, and worker audit boundaries; Git owns approved and pending creative review state. |
| Assistant `*ChatPanel.razor` components under `Lorekeeper/Components/Pages/Projects/` | Thin adapters that hydrate transcripts, subscribe to active turns, render review/progress state, and route mutation notices to owning workspaces. |

## Related chapters

- [narrative-context.md](narrative-context.md) owns Project Guidance, Book
  Brief, canon, outline, graph, retrieval, direct references, ingest evidence,
  and automatic context projections.
- [manuscript-authoring.md](manuscript-authoring.md) owns manuscript operations,
  editor targets, annotations, Book Text Styles, process-lifetime manual
  Undo/Redo, and Git-backed Review Edits. Successful assistant mutations
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
  notices, owning-service revision checks, persistence, UI refresh consumers, and the
  owning manual service path together.
- For manuscript text/structure mutations, confirm insertion-only work warns
  without being rejected, returned readback ranges include adjacent context and
  stay within the 100-block read limit, live reads match the returned
  revision/hash, and corrections use a fresh operation batch.
- For Review/Contest/revision changes, confirm Git HEAD-to-live and historical
  modes, semantic grouping, stale-token rejection, exact target ownership,
  durable candidate drafts, project-wide Editor locking, terminal state,
  resolution/discard behavior, and cancellation cleanup.
- Do not add broad assistant, UI, editor, or service automated-test suites. The
  validation chapter permits deterministic, headless authoring-save and
  assistant-concurrency contract regressions; they must use production contracts
  and cannot simulate a provider or browser. Use compilation, static inspection,
  and explicitly authorized manual integration checks for all other assistant
  behavior.
- Run `dotnet build Lorekeeper.sln` and the HTTP startup smoke check for normal
  source changes. Provider calls, model-specific tool behavior, browser UI,
  image generation, and publishing flows require explicit integration exercise
  before claiming they work.
