# Local plugin workspace and storage

## When to read

Read this chapter completely before changing `Lorekeeper.Plugin/`, the repository
plugin marketplace, plugin manifests or MCP tools, host file-library integration,
file editor entrypoints, or claims about ChatGPT-backed content storage.

## Scope and ownership

This chapter owns the independent local writing prototype and its separate
storage diagnostic. They do not change the ASP.NET/Blazor/Electron runtime or
reuse its SQLite database, provider tokens, services, project exports, or
persistence contracts. The prototype covers brief, outline, chapter prose,
canon, focused retrieval, reviewed text replacements, and an embedded local
Codex conversation with independently assembled context and saved transcripts.

The plugin's hard storage constraint is that durable user content stays
in ChatGPT storage or user-owned local files. A future hosted core may process
content transiently but must not persist it in a Lorekeeper-operated database,
object store, content logs, backups, or queues. A local MCP distribution and a
public-directory release have different connectivity/review requirements.

## Current architecture and invariants

The repo marketplace selects a portable Agent Plugins package containing two
skills, a Node stdio MCP server, and two self-contained MCP App editors. Runtime
requires Node 22.12 or newer; embedded chat additionally needs the Codex executable
and local account sign-in. Bundled SDKs eliminate a runtime npm install. The
installed plugin has no HTTP service. `mcp.json` resolves its working directory `"./"`
inside the installed plugin. Root `plugin.json` is the only manifest. The stable
package identity remains `lorekeeper-storage-probe`; its display name is
**Lorekeeper Local**. Source changes are rebuilt and refreshed through supported
plugin installation, never by editing the installed cache.

### Local narrative workspace

`open_lorekeeper_workspace` owns global/thread entrypoints and the fullscreen
`ui://lorekeeper/workspace.html` resource. Its CSP permits no external connections
or resource origins. The HTML editor uses official MCP Apps calls to the local
server; project content is not sent to a Lorekeeper service.

`LocalProjectStore` owns portable schema-v1 `.lorekeeper.json` files under
`Documents/Lorekeeper Projects` in the user's home directory, overridable with
an absolute `LOREKEEPER_PROJECTS_DIR`. Tool inputs accept filenames, never
arbitrary paths. Reads reject linked/non-regular files, invalid UTF-8, invalid
schema/identities/links, and files over 4 MiB. Projects have stable UUIDs for
project, chapters, canon, and proposals, with revision/timestamps. This format is
independent of desktop manuscript v7 and desktop import/export.

Changed saves acquire an exclusive per-file lock, compare the loaded SHA-256,
validate the next state, preserve exact prior bytes in `.history/`, flush a
sibling temporary file, recheck current bytes, and rename it over the project.
Existing backups must themselves be safe valid files matching the expected
digest. No-op saves do not increment revisions. Creation is exclusive and never
replaces existing files. Participating writers serialize; arbitrary external
editors are not part of that lock contract, and read-then-rename is not an OS
atomic compare-and-swap. Crash/power-loss durability and automatic stale-lock
recovery are not established. A crash lock fails closed until manually inspected
after all writers are closed. Local revision files are user-owned backups, not
a hosted content store.

The editor keeps a draft and loaded snapshot/hash in memory. Explicit **Save
changes** persists manual edits. Failure retains the draft and provides a JSON
copy surface. Reopening/switching asks before discarding unsaved changes. There
is no autosave, localStorage, authoritative browser copy, or recovery journal;
closing loses unsaved work. Proposal tool results never overwrite a dirty draft.

The model-visible operations list/create projects, read a paged overview, read
exact targets, retrieve bounded context, and propose edits. Exact target pages
carry project revision and the whole-target hash; the skill requires consistent
pages before a complete replacement. Hidden tool metadata carries the full
saved snapshot to the editor; write responses give the model bounded summaries.
Workspace-state loading, manual saves, and accept/reject tools are app-only.
All tools share project validation and storage operations.

Only the explicit workspace opener links to the UI resource. Data and app-only
tools declare visibility without a resource URI, so routine model reads,
retrieval, proposals, and editor calls do not create tabs. The skill forbids using
the opener as a refresh. Host Side Chat remains an alternative caller of the
data tools; the custom UI owns a separate embedded writing conversation.

The app-only `sync_lorekeeper_project` compares the observed exact file hash and
returns the private saved snapshot only when it changes. A visible editor checks
every 2.5 seconds and on focus/visibility changes. At most one synchronization
request runs at a time; reads pause during foreground actions, a new-project
form, or a discard dialog. Responses are discarded after a project switch or a
foreground snapshot change. The editor captures inputs again after awaiting a
response, so typing during a read is preserved. Clean drafts adopt current saved
state without changing the view; dirty drafts keep their loaded hash and text,
remember the observed hash to avoid repeat payloads, and show a newer-state
notice. Polling uses read-only local tools and neither saves nor invokes a model.

Proposals persist before/after text, target identity, source revision, reason,
status, and timestamps. Creating one never changes the writing. Exact source
revision/hash guard creation; the proposal UUID provides idempotent retries only
for identical arguments. Human acceptance compares both the loaded file hash and
the target's original text. A changed target fails without applying stale prose;
rejection preserves current writing. Reviewed proposal history stays in the
project. AI tools/skills must not bypass approval by directly writing files.

Retrieval is local lexical selection: project brief, explicitly chapter-linked
canon, selected chapter/outline, then query matches. It returns stable source
identities, reasons, revision, completeness, and a bounded excerpt budget. Default
is 12,000 UTF-16 characters, up to 12 sources, with surrogate-safe excerpt
boundaries. No embeddings/provider calls occur. The editor can preview/share
selected context with the host. Chapter discussion focuses the embedded composer.
Retrieval does not guarantee completeness. Author content is data, not executable
tool instructions.

### Embedded conversation and context ownership

`LorekeeperChat` owns project-bound conversations, context assembly, turn lifecycle,
and four dynamic project tools. `CodexAppServer` lazily launches an owned stdio
`codex app-server` child, negotiates the experimental API, correlates JSONL RPCs,
streams notifications, and answers only project tool requests. It drains stderr
without logging it, sanitizes protocol failures, and closes only its child.
`LOREKEEPER_CODEX_PATH` can select an absolute executable; existing `CODEX_HOME`
and authentication remain Codex-owned. No credentials are read/copied by the
plugin, stored with projects, or exposed to the iframe. Connect performs account
and model discovery without inference; Send performs inference using that account.
The pane can intentionally open only the official OpenAI sign-in URL. Disconnect
does not log out of Codex. Fresh OAuth remains unverified.

`model/list` supplies available choices and supported/default efforts. Conversation
selections persist; unavailable selections fail without automatic fallback. Each
turn records its actual requested model/effort. No desktop provider catalogue,
OAuth persistence, or transport adapter is reused or changed.

Each turn uses a new ephemeral thread with code-owned base/developer instructions,
the complete protected Book Brief, current bounded lexical retrieval and its
identities/completeness, and all prior visible user/assistant prose. Historical
prose is injected as explicitly quoted work-log evidence; prior tool payloads
and reasoning are audit-only and are not replayed. Project context is supplied
as untrusted additional context. Exact file hashes reject a changed project
before inference; tools reacquire current state before proposals. The UI refuses
to send with an unsaved manuscript draft. Users can preview context, choose the
chapter/budget, switch conversations, select model/effort, stream replies, and Stop.
Codex's native token usage and context-window reports appear with each turn.

The dynamic tool schemas omit filenames and bind all operations to the turn's
project: overview, exact target, focused retrieval, and proposal. Manual-save and
accept/reject tools remain app-only and are not exposed to this model. The child
uses a contained `.codex-workspace` working directory, read-only sandbox, no
approval escalation, and per-thread overrides disabling inherited MCP servers,
plugins/apps/hooks, shell/web/browser/image/computer tools, memory, and delegation.
Overrides do not rewrite user settings. Host chat data tools retain their existing
contracts, and no routine chat operation is associated with a UI resource.

`LocalDocumentStore` owns shared containment, exact hashes, locks, guarded
replacement, and revision backups; project/chat codecs retain separate schemas
and limits. A schema-v1 `PROJECT.lorekeeper.json.chat.json` file holds project UUID,
active conversation, selections, messages, reasoning summaries, tool outcomes,
context snapshots, status, and usage. Files are limited to 8 MiB, 30 conversations,
100 turns per conversation, and 120,000 replayed prose characters. Reaching a
bound fails without silent history trimming. Turn IDs are stable caller UUIDs;
identical retries return the existing turn, different input with that ID fails.
The outgoing message persists before inference under a lease held for the whole
turn. Project writes/proposals use a separate lock. Partial output stays in memory
while streaming and is saved on completion, interruption, or graceful close.
Accepted local tool operations drain before the final audit is persisted; Stop
does not roll back a proposal already executing its guarded local write.
Uncertain requests are never resent automatically. A process crash can leave a
running record/lock requiring inspection; power-loss durability is not established.
If final persistence fails, buffered output remains in memory and further sends
are blocked; preserve it before closing. Revision history is local user content.

Fresh threads plus a high native auto-compaction threshold let Lorekeeper rebuild
context between turns. This is not the desktop chat's exact token-reserve and
tombstoning implementation. App-server owns native inference/within-turn context
handling and can reject oversized requests. History injection and dynamic tools
are experimental, exercised with CLI 0.159.2; no unstable `thread/resume.history`
path or encrypted reasoning reconstruction is used. The host ChatGPT conversation
remains separate. Official app-server guidance permits existing authentication for
local/open-source apps; commercial/hosted apps must use Sign in with ChatGPT.

Other chat providers, rich semantic editing, graph/vector
indexes, images, publishing/Press, desktop archive imports, Git history UI, and
background revision agents are outside this prototype. The content storage
constraint also applies to a possible future hosted core; local stdio success
does not establish public-directory or ChatGPT web compatibility.

### Independent development host

`npm run dev` starts an optional development-only HTTP host on an available
`127.0.0.1` port. It uses official `AppBridge`/`PostMessageTransport` in an opaque
sandboxed iframe and an official SDK stdio client, rather than a simulated MCP
bridge. Host/origin checks, a per-process API token, no-store responses, bounded
request bodies, and a restrictive CSP contain the loopback API. Server-side
audience checks preserve app-only tools. The embedded pane exercises the same
owned Codex app-server connection and live inference as the installed package;
the preview does not supply host Side Chat, library APIs, or the resource bridge.
The official sign-in URL appears as an explicit clickable link in the parent.

Each MCP generation is copied from the three repository bundles into an owned
temporary directory under `.artifacts/plugin-development/runtimes`. Its exact
copied server/editor hashes, version, PID, and generation are displayed. This
avoids Windows locking the next repository build. Explicit **Reload runtime**
asks before discarding the in-memory editor, refuses to interrupt active tool
operations, stops owned chat turns and persists partial transcripts, closes only
its own app-server and stdio client/process, cleans that generation,
starts a fresh one, rediscovers tools/resources, and mounts the editor again.
No failed write is automatically retried. Shutdown drains active operations and
removes only validated immediate temporary children of that runtime directory.

Development projects default to the separate ignored
`.artifacts/plugin-development/projects` folder; an absolute
`LOREKEEPER_DEV_PROJECTS_DIR` deliberately changes it. The same `LocalProjectStore`
and guarded writes own these files. Runtime cleanup never deletes projects.
The manual tool console exposes model-visible data operations; content remains
in the browser's memory and user-owned project files, without a content log,
automation/assertions, or a second content store. The helper is contributor
tooling and is not part of the installed MCP server's runtime closure.

This host permits autonomous rebuild/UI checks without restarting Codex, but
its results do not establish the actual Codex tab/cache/chat lifecycle. The
documented app-server `config/mcpServer/reload` method queues thread refreshes;
the tested desktop stdio session did not expose a connectable control socket.
Installed-plugin refresh remains a separate actual-host check. The preview
helper itself is loaded at startup; editing it requires restarting the helper.

### Separate ChatGPT storage diagnostic

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
| `Lorekeeper.Plugin/src/project.mjs`, `src/store.mjs` | Project schema, identities, lexical retrieval, proposal lifecycle, contained local files, guarded writes and revision backups. |
| `Lorekeeper.Plugin/src/workspace.ts`, `src/workspace.html` | Authoring, context preview/share, draft/save state, review, application dialogs. |
| `Lorekeeper.Plugin/src/chat.mjs`, `src/app-server.mjs` | Project-bound conversations/context, guarded local transcripts, scoped dynamic tools, and owned Codex stdio lifecycle. |
| `Lorekeeper.Plugin/src/chat-pane.ts` | Embedded composer/transcript, conversation/model/effort selection, context preview, streaming polling, and interruption. |
| `Lorekeeper.Plugin/skills/lorekeeper-workspace/SKILL.md` | Grounded reads/retrieval, proposal-only model edits, human review, retry/conflict boundaries. |
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
