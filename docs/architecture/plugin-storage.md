# Local plugin workspace and storage

## When to read

Read this chapter completely before changing Lorekeeper.Plugin, local marketplace
or manifests, MCP tools, file-library integration, editor entrypoints, or claims
about ChatGPT-backed content storage.

## Scope and invariants

Lorekeeper Local 0.4.0 is an independent Node/TypeScript local MCP subproject.
It neither opens the desktop database/manuscript archives nor copies desktop
provider credentials. Stable package identity is lorekeeper-storage-probe;
the display name is Lorekeeper Local. Root plugin.json is the sole manifest.
Portable mcp.json uses "./" inside the installed package. Node 22.12+ is required;
chat additionally needs local Codex CLI and sign-in. Bundled dependencies eliminate
runtime npm installation. The installed server has no HTTP listener.

Durable content belongs in user-owned local files or user-controlled ChatGPT
storage. A possible future hosted core must not persist content in a Lorekeeper
database, object store, logs, backups or queues. This local release uses files;
native Pages privileges do not transfer to its MCP server or iframe.

## Project model and command service

Schema-v2 .lorekeeper.json files hold stable UUIDs, revision/timestamps, acts,
chapters, beats, structured Book Brief, Project Guidance, custom entity types,
entities/aliases/text properties, relationships, facts, associations, context
preferences and read-only legacy draft comparisons. Chapters have nullable act
parents; beats require chapters. Chapter deletion removes prose/beats/links but
keeps entities. Act deletion demotes chapters to Unassigned. The format is
independent of the desktop manuscript and export formats.

LocalDocumentStore owns contained filenames, strict UTF-8, regular-file/no-link
checks, exact hashes, cache invalidation, locks, bounded reads and replacement.
Default root is Documents/Lorekeeper Projects; LOREKEEPER_PROJECTS_DIR must be
absolute. Project bound is 32 MiB; inventories expose at most 100 files. Schema
bounds support 1,000 chapters, 10,000 beats and 5,000 entities.

WorkspaceService owns schema upgrade, commands, compact history, recovery
journals and guarded rollback. Schema-v1 is supported only at the explicit reader
migration boundary. Upgrade preserves exact original bytes, IDs, chapter order,
prose, canon and links; old brief text becomes Premise verbatim, chapters become
Unassigned, and old proposals remain historical/unapplied. No pending draft is
applied. Original chat files are backed up and old conversations assigned Editor.

Manual and AI operations use the same command service. Stable request IDs,
expected revisions and exact canonical field/item/list hashes guard mutations.
Removal additionally guards affected dependencies. Future revisions reject;
older revisions may reconcile unrelated fields. Inserts cannot replace an existing
identity. Identical retries return the original receipt; conflicting identities
fail. No-op receipts are durable without advancing project revision.

A command validates all operations on a clone before changing canonical content.
Compact before/after records and a flushed sibling temporary file precede a
prepared transaction journal. The canonical file is rechecked and atomically
renamed, then a commit marker confirms the receipt. Long text history retains
changed ranges with exact full-value hashes rather than whole-book copies.
Existing backups remain. Rollback reverses the group's records as a new guarded
transaction, checking affected after-values and relationship validity first.
Unrelated edits are preserved; overlapping edits fail before replacement.

Recovery completes only a matching prepared transaction whose current bytes equal
the recorded before or after hash. Other evidence remains preserved and fails
closed. A lock with a verified dead PID may recover under a separate exclusive
recovery claim; unknown/empty/live locks are never broken. Participating writers
serialize. This is not OS compare-and-swap against arbitrary external editors,
and power-loss durability is not established.

## UI and synchronization

Desktop theme tokens, Inter/system typography and controls follow the desktop
app. Chat is left, workspace middle, context/editable details right. Panes resize,
collapse and scroll independently. Narrow layouts switch Chat/Workspace/Details
without changing conversation ownership. Local layout preferences retain pane
widths/theme, selected project/chapter, expanded hierarchy, section states, view
scroll (including surface-scoped chat scroll) and manuscript selection. Only
layout metadata is stored there. Explicit Details/source actions open their
owning fields and reveal the Details pane on narrow or collapsed layouts.

Outline supports acts/chapters/beats, inline fields, ordering, drag handles and
keyboard/touch controls, plus app-owned deletion/move dialogs. Editor supports
plain-text editing, chapter navigation, selection, word count, bounded local Undo/Redo
and Edit/Read/Changes. Formatting tools are explicitly deferred. Structured brief,
guidance, facts, grouped entities, properties, aliases, relationships and links
are editable on the right. World and Voices wait for v2; Sources, Images, Publish,
rich manuscript features and desktop archive imports are deferred.
ManuscriptHistory groups nearby typing edits, retains at most eight chapter stacks
and bounds their snapshots. External saved changes reset the affected local stack;
cross-session restoration remains in guarded project History.

WorkspaceState holds canonical state plus dirty field overlays. Manual input
autosaves after 500 ms inactivity. A durable local draft journal retains each
submitted batch and original request identity before canonical save. Send,
navigation and development teardown flush changes first. Failed/overlapping
variants remain in an app-owned conflict dialog; another view cannot replace a
dirty overlay. Mutation responses identify their baseline and affected items.
Unknown/evicted baselines are privately reread rather than applied to stale state.

Read-only hash synchronization polls every 450 ms, with cached project reads and
bounded snapshots/deltas. Chat polls its version cursor every 250 ms and returns
changed active-turn data, not the complete transcript. Keyed DOM reconciliation
preserves focused controls, IME composition, editor history and scroll containers.
Only open_lorekeeper_workspace owns its workspace UI resource. Routine data,
mutation, chat and app-only operations do not create tabs. The iframe cannot
manage the host's surrounding tabs.

## Chat and context ownership

LorekeeperChat owns project-scoped Outline and Editor conversations, context,
rolling replay and turn lifecycle. Changing chapter keeps the Editor conversation.
CodexAppServer retains its owned stdio connection across messages/surfaces.
Authentication and credentials remain Codex-owned. The iframe makes no direct
model/network call and receives no credential. Connect discovers account/models
without inference; Send runs inference. Disconnect closes only owned processes.
Sign in is an intentional handoff to the official OpenAI URL.

model/list supplies model/effort choices. New conversations prefer 6.1 Sol/Medium
when available; existing unavailable selections fail visibly without fallback.
Explicit model/effort selections persist on the surface's active conversation
before sending; active-turn settings cannot be changed until it finishes/stops.
Each message uses an ephemeral thread with code-owned instructions, keyed untrusted
project context and quoted historical user/assistant prose. Earlier reasoning and
tool results remain audit-only. Dynamic tools bind the captured project and surface;
AI writes bind the turn's history group. Outline cannot mutate manuscript prose.
Shell, inherited MCP/plugins/apps/hooks, external services, memories, browser,
computer, images and delegation are disabled through per-thread overrides.

Book Brief and Project Guidance are protected. Outline uses lossless tables with
each UUID encoded once to include complete organized structure/associations.
Editor includes the complete active chapter once, its outline, linked entities,
preceding-chapter continuity and relevant lexical retrieval. Persistent chapter
include/exclude overrides and complete pins affect assembly. Search-to-context,
Reset and preview show identity, revision, reason, completeness and token estimate.
Reset changes preferences only. There are no embeddings or provider retrieval calls.
Inclusion overrides are distinct from pins; only pins force complete required
content independently of relevance and the optional-source budget.
Protected direction and mandatory active chapter/outline sources cannot be
excluded. Prompt preview includes the same instructions, tool schemas, quoted
history, message and keyed project packet used to construct the native request.

The complete estimated request accounts for instructions/tool schemas, encoded
sources/metadata, current prose and historical replay. Before Codex reports the
selected model's window, input is capped at 32,000 estimated tokens. After a report,
20% is reserved for generation/tool work; author preferences may impose a lower
context budget. Oldest turns leave replay visibly while complete transcripts stay
saved. Required content is never silently shortened; overflow blocks Send pending
an explicit adjustment. UTF-8 estimates are conservative approximations.
Codex controls native processing inside a turn; desktop compaction is not reproduced
exactly. History injection and dynamic tools remain experimental APIs.

Schema-v2 chat sidecars retain the complete transcript up to a 64 MiB safety bound;
there is no 100-turn/120,000-character replay cap. Pages expose recent 20 turns
and earlier messages. Durable composer files also retain uncertain send identities.
Outgoing messages persist before inference under a conversation-file lease.
Partial output is journaled every second, then saved terminally after accepted
tool tasks drain. Stop/reload preserve partial prose and completed changes.
Interrupted running records recover as interrupted without inference replay.
Final-save failure retains buffered output and blocks sends. No uncertain inference
is automatically resent. Connection startup, request preparation, first output,
tool duration and UI delivery are observed separately without content logging.

Other providers, ChatGPT-backed editor storage and public hosted deployment are
outside this release. Responsive UI does not make local stdio phone/web-accessible.

## Independent development host

npm run dev uses official AppBridge/PostMessageTransport in an opaque iframe and
a real SDK stdio client on an available 127.0.0.1 port. Host/origin/session guards,
no-store responses, an 8 MiB request bound and restrictive CSP contain the API.
It supplies no ChatGPT storage privileges or host Side Chat. Development content
defaults to ignored .artifacts/plugin-development/projects; an absolute
LOREKEEPER_DEV_PROJECTS_DIR deliberately changes it.

Each MCP generation is copied to an owned temporary child of the runtime folder,
so Windows cannot lock the next repository build. Reload first requests editor
teardown to flush manual/composer drafts; a failed flush prevents restart. It
refuses active bridge calls, gracefully stops owned inference, retains partial
transcripts, closes only its processes and mounts rebuilt tools/UI in the same
tab. Cleanup deletes only validated owned runtime copies, never projects/history.
The manual tool console uses real operations, without an automated plugin harness.

This validates editor/server behavior without restarting Codex. Installed-package
digests, handshake and real-host rendering remain separate checks. A retained
Codex MCP connection/view may still need host refresh after installation. Never
edit installed cache files. Changes to the preview helper require restarting
that helper; plugin rebuilds use Reload runtime.

## Separate ChatGPT storage diagnostic

The server exposes `open_storage_probe` to the model/app and
`validate_probe_project` to the app. Its `.lkproject` file entrypoint and explicit
opener use `ui://lorekeeper/storage-probe.html`. The server
reads the sibling HTML resource; synthetic JSON is validated and hashed in memory,
then discarded. The 64 KiB synthetic schema fixes project/chapter identities and
limits revisions, chapter/paragraph counts, and strings. It is not a desktop
import/export format.

The iframe connects using the official MCP Apps and OpenAI extensions SDKs. It
feature-detects host `uploadFile`, `selectFiles`, and `getFileDownloadUrl` APIs
and the OpenAI resource bridge. No mock host is supplied. Host availability and
successful save/reopen are separate observations.

Library saves explicitly upload new versions. Selected library files are read
through host-issued download URLs bounded to 64 KiB and decoded as strict UTF-8.
Requests omit credentials, reject redirects, and allow only the declared ChatGPT
and OpenAI file-content origins. Signed URLs and raw host errors never enter
reports or model context.

Same-file writes require a URI supplied by a host file entrypoint, writable
resource metadata, and a nonempty ETag. Every write uses `ifMatch`; a conflict
keeps the unsaved snapshot. The initial ETag/snapshot supports a deliberate
synthetic stale save after a successful update, followed by digest verification
of the newer saved bytes. A non-conflict stale result or changed newer bytes
disables subsequent writes. There is no unconditional replacement fallback.

The diagnostic's working content, ETags, and resource URIs stay in memory. There is no
autosave, localStorage, filesystem content store, credential store, or content
logging. Closing loses unsaved changes. Reports include capability booleans,
synthetic identities, file IDs, revisions, digests, byte counts, and operation
outcomes; they exclude content, ETags, resource URIs, and download URLs.

Native Pages operations belong to the host agent's built-in tools. Their guarded
editing and library upload/readback results do not establish that a third-party
MCP server or editor has the same capabilities. The dated
[evidence record](../research/chatgpt-storage-probe.md) owns observations,
sample artifacts, blockers, and the storage decision. Custom-editor persistence
and conflict protection through that ChatGPT bridge remain unverified until
exercised in an actual host. The separate
[local prototype evidence](../research/local-plugin-prototype.md) records local
file, protocol, and workspace observations.

## Key files and file families

| File family | Responsibility |
|---|---|
| [Marketplace](../../.agents/plugins/marketplace.json) | Local repo plugin discovery and install policy. |
| [Plugin subproject](../../Lorekeeper.Plugin/README.md), `plugin.json`, `mcp.json` | Install/development instructions, identity, UI metadata, and contained stdio registration. |
| `Lorekeeper.Plugin/src/server.mjs` | Workspace/diagnostic MCP tools and resources, visibility, bounded responses, safe errors. |
| `Lorekeeper.Plugin/src/project.mjs`, `src/store.mjs` | Project schema, explicit migration reader, containment, cached reads, locks and atomic replacement. |
| `Lorekeeper.Plugin/src/workspace-service.mjs` | Shared commands, compact receipts/history, draft journals, transaction recovery and guarded rollback. |
| `Lorekeeper.Plugin/src/context.mjs` | Lossless outline tables, protected sources, lexical retrieval, overrides, pins and token estimates. |
| `Lorekeeper.Plugin/src/workspace.ts`, `src/workspace.html`, `src/workspace-panels.ts`, `src/workspace-state.ts` | Three-pane authoring, shared field overlays/autosave, keyed reconciliation, responsive layout, conflict/recovery/history dialogs and local layout state. |
| `Lorekeeper.Plugin/src/manuscript-history.ts` | Bounded chapter editing Undo/Redo; external saved changes reset the affected stack. |
| `Lorekeeper.Plugin/src/chat.mjs`, `src/app-server.mjs` | Surface conversations, rolling replay, journals, scoped dynamic tools and retained Codex stdio lifecycle. |
| `Lorekeeper.Plugin/src/chat-pane.ts` | Embedded composer/transcript, conversation/model/effort selection, context preview, streaming polling, and interruption. |
| `Lorekeeper.Plugin/skills/lorekeeper-workspace/SKILL.md` | Grounded exact reads, direct guarded changes, dependency/list hashes and retry/conflict boundaries. |
| `Lorekeeper.Plugin/src/app.ts`, `src/app.html` | Memory-only editor, capability detection, library reads/uploads, conditional writes, stale-save check, and content-free reports. |
| `Lorekeeper.Plugin/skills/storage-probe/SKILL.md` | Actual-host probe workflow and evidence boundaries. |
| `Lorekeeper.Plugin/scripts/build.mjs`, `package*.json`, `tsconfig.json` | Exact-pinned dependency closure, static checking, bundles, and notices generation. |
| `Lorekeeper.Plugin/scripts/dev.mjs`, `scripts/preview.ts`, `scripts/preview.html` | Loopback development host, owned stdio generations, official MCP Apps bridge, explicit reload, and manual tool console. |
| `Lorekeeper.Plugin/dist/`, `THIRD-PARTY-NOTICES.md`, `licenses/` | Committed distributable server/HTML and bundled dependency license texts. |
| `Lorekeeper.Plugin/fixtures/`, `.gitattributes`, `.editorconfig` | Synthetic reference bytes and stable LF package formatting. |

## Related chapters

- [Runtime and host](runtime-host.md) owns the separate desktop application.
- [Persistence, migrations, and import](persistence-migrations-import.md) owns
  desktop data, which this plugin neither opens nor changes.
- [Validation and documentation](validation-documentation.md) owns build/test
  requirements, manual authorization, and integration claim boundaries.

## Relevant verification

Run plugin `npm ci --ignore-scripts`, `npm run check`, `npm run build`, and
`npm audit`, then the required solution build and existing data-safety tests on
the final state. Inspect both source and regenerated distributables/notices.
Preserve exact fixture bytes and document digests separately from semantic IDs.
No plugin test suite or one-off automated harness is permitted by repo policy.

The build inserts JavaScript with a replacement callback so SDK replacement
tokens remain literal, then syntax-checks the exact inline module extracted from
the generated HTML before writing it. Compiling the TypeScript source alone
does not verify the HTML embedding step.

With explicit authorization, verify installed and bundle-only MCP handshakes,
tools/resources, and synthetic validation through a real SDK client. This proves
packaging/protocol behavior only. Editor capabilities, upload/reopen, same-file
update/reopen, and stale save protection require the real host; simulated bridges
or ordinary browser pages do not establish them. Close manually owned processes
afterwards. Desktop startup/Press/provider checks are selected only when their
owning runtime changes.
