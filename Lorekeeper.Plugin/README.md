# Lorekeeper Local

A local writing prototype for Codex: plan a book, write chapters, record canon,
preview focused context, and chat with an embedded Codex-backed assistant whose
proposed changes await review. The MCP server and editor share project operations.
It runs from this repo as a subproject,
independently of the desktop workbench's database and provider credentials.

Projects are portable `.lorekeeper.json` files on the user's computer. There is
no hosted Lorekeeper service, content database, copied credential, or content
logging. OpenAI receives messages, conversation prose, selected project context,
and requested tool results when a message is sent. Saved conversations and their
audit records stay in local files; they are separate from the host chat.

The original synthetic storage probe remains available as a diagnostic. Only
that probe keeps all working content in memory and never writes local projects.

The [evidence record](../docs/research/chatgpt-storage-probe.md) separates native
Pages operations from checks performed through this plugin. Native Page editing
and agent-mediated JSON uploads/readback have passed. Version 0.1.1's editor
connected, revised, and validated the sample in Codex desktop, but that host
exposed no library file APIs or resource bridge. The workspace therefore uses
local files. This does not establish that every future host will lack the
documented optional bridge.

## Use the workspace

1. Open **Lorekeeper Local**, choose **New project**, and create a blank project
   or the artificial **The Lantern Archive** sample.
2. Edit the **Book Brief**, chapter intent/order in **Outline**, prose in
   **Chapters**, and chapter-linked characters/places/facts in **Canon**.
3. Choose **Save changes**. Saves are explicit; closing loses unsaved drafts.
   **Reopen** rereads the saved file. Switching projects or reopening an edited
   project asks before discarding its draft.
4. Use **Context** to preview relevant saved content and share it with the
   conversation. Retrieval prioritizes the brief and chapter-linked canon,
   followed by lexical matches, within a visible character budget. It includes
   source identities, relevance reasons, and excerpt completeness.
5. In **Writing conversation**, choose **Connect Codex**, then select an available
   model and reasoning effort. Type a message and choose **Send** (Ctrl/Cmd+Enter).
   **Context for the next message** previews the complete Book Brief plus bounded
   retrieval; choose a chapter or the whole project. **Stop** preserves partial
output and completed proposals. **New chat** preserves earlier conversations.
6. Ask Lorekeeper to revise a saved chapter, brief, outline synopsis, or canon
   entry. Its tools read exact text and submit a proposal with a source hash and
   revision. Inspect the before/after text in **Review edits**, then accept or
   reject. **Try the review workflow** supplies an example without a model call.

The embedded pane owns its conversation and context assembly through a separate
local Codex app-server. **Discuss this chapter** focuses that pane. Host Side Chat
can still use the project data tools, but has a separate transcript and context.
Ordinary read/retrieval/proposal tools do not launch editor tabs; only an explicit
Open action does. An open, visible workspace checks saved changes every 2.5
seconds, preserves the selected view, and updates the review count. Clean drafts
refresh automatically. Dirty drafts and new-project forms remain untouched;
copy/save a draft before explicitly reopening newer saved state.

The default folder is `Documents/Lorekeeper Projects` under the user's home
directory. Set the absolute `LOREKEEPER_PROJECTS_DIR` environment variable before
starting Codex to use another user-owned folder. The server accepts simple
filenames in that folder, never arbitrary paths. Copy a valid project file into
the folder to open it; this prototype has no desktop archive importer.

Every changed save increments the revision, preserves the prior exact bytes in
the folder's `.history/`, and replaces the project through a sibling temporary
file. Loaded hashes reject stale saves; proposal acceptance also checks the
original target text. Failure preserves the editor draft, with a copyable JSON
snapshot. Participating writers use per-file locks. Avoid simultaneously editing
these files outside the plugin: a filesystem rename is not an atomic comparison
against an uncooperative external writer. An interrupted process can leave a
lock requiring manual inspection/removal after all writers are closed. Initial
creation is exclusive and never overwrites a file; crash/power-loss durability
and automatic recovery are not established.

Prototype bounds: 4 MiB UTF-8 project files, 100 chapters, 300 canon entries,
1,000 retained proposals, and a 100-file project inventory. Chapter prose is
plain text. Context defaults to 12,000 characters with up to 12 sources; this
budget limits retrieved excerpts, in addition to the complete protected brief.

### Embedded chat and local context

Codex owns sign-in and provider transport. The plugin starts `codex app-server`
only on **Connect Codex** or **Sign in**; sending uses the signed-in local account.
It discovers models and efforts through `model/list`, preserves conversation
selections, and rejects unavailable choices instead of switching silently.
**Disconnect** stops owned turns and closes that connection without signing out.
If needed, **Sign in** opens the official OpenAI authorization URL; alternatively
run `codex login`, then reconnect. Fresh browser sign-in has not been exercised.

Each message starts a fresh ephemeral Codex thread with Lorekeeper's instructions,
the complete current Book Brief, bounded saved-project retrieval, and the earlier
user/assistant prose. Historical prose is explicitly quoted as a work log;
historical tool results and reasoning summaries remain audit-only. The assistant
can read the current project, retrieve context, read exact targets, and propose
edits. These four tools are bound to the selected file; it cannot accept edits.
Per-thread configuration disables inherited MCP servers, plugins, shell, web
search, hooks, memory, and delegation, with read-only sandbox and no approvals.
The iframe makes no direct model/network calls and never receives credentials.

The pane shows streaming plain text, tool outcomes, reasoning summaries when
provided, and Codex's reported input/output tokens and context-window size.
Conversations persist in `PROJECT.lorekeeper.json.chat.json` beside the project,
with guarded saves and exact revision backups in `.history/`. Outgoing messages
are saved before inference. A chat file stays locked for the turn; competing
writers fail closed. Stable message UUIDs prevent identical retries from creating
another turn. Uncertain turns are never automatically resent. On a graceful
disconnect/reload, partial transcripts are saved; a crash may leave a running
record and lock requiring manual inspection. There is no automatic recovery.
Already-started local tool operations finish before their final audit is saved;
stopping does not undo a proposal that has reached the guarded local write.

Chat bounds are 8 MiB per file, 30 conversations, 100 turns per conversation,
10,000 characters per message, and 120,000 characters of replayed conversation
prose. At a limit, start a new conversation (or a different project at the file
limit); earlier transcripts are retained. History is never silently trimmed.
The desktop app's exact token-reserve/tombstoning policy is not ported. Fresh
threads and a high native auto-compaction threshold keep context assembly local,
but app-server still owns inference internals and may reject oversized input.
Dynamic tools and history injection use experimental app-server APIs; CLI
0.159.2 was exercised. Other CLI versions and platforms remain unverified.

See the [prototype evidence](../docs/research/local-plugin-prototype.md) for
actual validation and remaining integration limits.

## Install locally

Requirements: Node.js 22.12 or newer and a local client supporting portable
plugins, bundled stdio MCP servers, and MCP Apps. Embedded chat also requires
Codex CLI on PATH and a local sign-in. Set an absolute `LOREKEEPER_CODEX_PATH`
to select its executable if necessary; an existing `CODEX_HOME` is inherited.
No external Lorekeeper server or API key is needed. The self-contained `dist/`
files and dependency notices are committed;
recipients do not need an npm install to run the plugin.

From the repository root, using the current Codex CLI:

```powershell
codex.exe plugin marketplace add .
codex.exe plugin add lorekeeper-storage-probe@lorekeeper-local
codex.exe plugin list --marketplace lorekeeper-local --json
```

The repo marketplace and installed UI appear as **Lorekeeper Local**. The package
identity remains `lorekeeper-storage-probe` so existing installs upgrade.
On other platforms use `codex`; on Windows `codex.exe` avoids an older npm
PowerShell shim taking precedence over the desktop's current CLI. Open
Lorekeeper Local from Plugins or ask to open its workspace. An active desktop
connection can retain the previous version after CLI installation. Use the host's
refresh flow for final installed-plugin validation; the independent development
preview below avoids repeating that step during source development. The
[official packaging guide](https://developers.openai.com/plugins/build/plugins)
describes installation and host discovery.
If the skill appears but `open_lorekeeper_workspace` is missing, the editor has not
opened: inspect server registration/startup before claiming host capabilities.

Codex loads an installed copy under its plugin cache. After changing this
subproject, rebuild and refresh the installation with `codex plugin add` before
retesting. Give each changed distributable a new plugin/package version: Windows
can prevent replacing a version whose cache is a running process's working
directory. Version 0.3.1 installs alongside the stale 0.3.0 copy without restarting
Codex. Close the old workspace view and open Lorekeeper Local again; the header
must show **Local prototype · 0.3.1**. A CLI listing alone does not prove an existing
view or MCP connection refreshed. If the host keeps its old connection, that
connection still needs the host's refresh flow. Never edit installed cache files.

The earlier cached 0.3.0 chat sent `additionalContext` as an array, while CLI
0.159.2 requires a keyed map. This caused `turn/start` error -32600 before
inference. The 0.3.1 distributable contains the corrected request and labels
protocol errors separately from sign-in advice. Failed messages remain in their
saved transcript and are never resent automatically.

`mcp.json` uses the portable working directory `"./"`, which resolves inside the
installed plugin. A plain `"."` is rejected by the portable config parser. This
package has one portable manifest and MCP configuration; there is no duplicate
legacy registration.

## Perform the separate ChatGPT storage checks

Use synthetic samples only. The editor accepts the fixed project/chapter IDs
in `fixtures/storage-probe-r1.lkproject`, schema version 1, and at most 64 KiB
of UTF-8 JSON. This probe format is separate from desktop Lorekeeper exports.

1. Open the editor in an actual MCP App host and inspect its capability report.
   Unavailable host APIs leave the relevant actions disabled. An ordinary
   browser or simulated bridge is not evidence.
2. Choose **New synthetic project**, then **Validate**. Save with **Save new
   version to ChatGPT library** if available. Record the digest and file ID.
3. Close the editor, reopen it, and use **Open from ChatGPT library** to select
   the saved sample. Compare project ID, chapter ID, revision, Unicode paragraph,
   byte count, and SHA-256 digest. Do not count an upload receipt as persistence.
4. Choose **Revise sample paragraph**, save another version, close/reopen, and
   verify revision 2. Verify revision 1 remains unchanged. Library uploads make
   separate artifacts; they do not establish replacement of an existing file.
5. Open a `.lkproject` sample through ChatGPT's file viewer extension entrypoint.
   Only a host-supplied resource URI, `writable: true`, and an ETag enable **Save
   opened file**. Change the paragraph, save, then close/reopen and compare bytes.
6. In the same editor session after a successful conditional update, choose
   **Try stale save on sample**. It sends the initial snapshot with the initial
   ETag and rereads the latest bytes after a conflict. A conflict with an
   unchanged newer digest passes. Any other outcome disables further writes
   and must be recorded as unsafe or unproven.

Open/reload actions refuse to replace an edited saved snapshot. **New synthetic
project** explicitly resets the working sample. Save failures retain the editor
contents. No autosave or local recovery copy is provided: closing loses unsaved
working data.

**Show report in chat** shares observed capabilities, IDs, revisions, digests,
and outcomes through MCP App context. It excludes project text, resource URIs,
ETags, signed download URLs, and raw host errors. Reports are scoped to the
current editor instance and are not a durable verification ledger.

## Develop and verify

```powershell
cd Lorekeeper.Plugin
npm ci --ignore-scripts
npm run check
npm run build
npm audit
```

### Reloadable development preview

After the build, run `npm run dev` and open the loopback URL printed in the
terminal. This is an independent MCP Apps host using the official `AppBridge`
and a real SDK connection to this plugin's bundled stdio server. It opens the
same workspace editor. **Connect Codex** starts only the plugin's separate
app-server process, without another Codex desktop window.

Leave that development host running while editing. Run `npm run check` and
`npm run build`, then choose **Reload runtime** and **Reload now** in the preview.
An agent can operate these controls through browser automation. Reload closes
the embedded chat connection (saving interrupted turns), closes only the
development host's MCP process, discovers the rebuilt tools, rereads
the UI resource, and mounts a fresh editor in the same tab. Its toolbar shows
server version, process ID, generation, and server/editor bundle hashes. Save
or copy drafts first: reload deliberately discards the editor's in-memory draft.
It refuses to restart during an active tool operation and never replays a write.

Each generation runs from a disposable copy below
`.artifacts/plugin-development/runtimes`, so the running Node process cannot
lock the repository's next Windows build. Closed generations are removed. The
default project folder is `.artifacts/plugin-development/projects`, separate
from normal projects. Set an absolute `LOREKEEPER_DEV_PROJECTS_DIR` before
starting the development host to deliberately select another folder. Stopping
the host removes runtime copies, not project files or their revision history.
Use Ctrl+C to stop the host and its owned MCP/app-server processes after manual
validation. Explicitly sending messages performs real inference against your
account and consumes its normal usage allowance. The preview's official sign-in
link requires an intentional click in its header.

**Development tools and results** exposes manual JSON calls to model-visible
data tools for exercising changes made outside the editor. Calls and responses
stay in browser memory; there is no automated test runner or saved content log.
The HTTP listener binds only to `127.0.0.1` on an available port. API calls require
the exact host/origin and a per-process session token. The sandboxed editor uses
the MCP bridge and cannot access that token or make direct network requests.

This preview exercises local editor/server and embedded chat behavior. It has no
ChatGPT storage APIs and does not prove Codex's installed-plugin cache, tab
lifecycle, or Side Chat integration. Those still need the actual target host.
The documented [`config/mcpServer/reload` API](https://learn.chatgpt.com/docs/app-server#api-overview)
queues refreshes in app-server, but the tested desktop stdio session exposed no
connectable control socket. The development workflow therefore does not claim
to reload that existing Codex connection. Changes to the preview host itself
require restarting `npm run dev`; changes to the plugin use **Reload runtime**.

The build bundles the official MCP/MCP Apps/OpenAI extensions SDKs into the
server and inline HTML, checks the exact inline module's JavaScript syntax before
writing the editor bundle, and regenerates `THIRD-PARTY-NOTICES.md`. A replacement
callback preserves JavaScript replacement tokens inside the SDK bundle.
Version-pinned dependencies come from `package-lock.json`. LF checkout rules keep
fixture bytes and generated artifacts stable across operating systems. Bundled SDK template
strings preserve upstream whitespace; the package's Git/editor rules exclude
that generated whitespace from trimming and diff warnings. The vendored MIT text
under `licenses/` supplies the upstream license omitted from the
`@cfworker/json-schema` npm package.

Run the repository's required solution build and existing data-safety tests on
the final state as well. Do not add a separate test suite or simulate host
storage. Manual protocol/UI/integration checks require explicit user
authorization under `AGENTS.md`; the approved storage-probe request authorizes
these synthetic checks.

## Capability boundaries

The prototype implements brief/outline/canon/prose editing, bounded lexical
retrieval, and reviewed text replacements. It does not yet implement the rich
semantic manuscript editor, graph/vector indexes, image workflows, publication
renderer, desktop import/export, Git history UI, or background revision agents.
These are omitted from this prototype, not proven impossible in a local plugin.

It does not call other chat providers. Its embedded conversation controls local
context assembly, model/effort selection, and durable transcripts. It cannot take
over the host ChatGPT conversation, its prompt, history, or compaction.
Built-in Pages tools callable by an agent are not automatically callable by this
MCP server or iframe.

Local stdio installation is a development/distribution route, not evidence of
public-directory or ChatGPT web compatibility. Public submission with MCP
currently calls for a public HTTPS endpoint, subject to the documented local-MCP
exception process. Any eventual hosted Lorekeeper core must keep durable user
content in user-controlled ChatGPT storage or local files, not a Lorekeeper
content database, object store, logs, backups, or queues. Existing app-server
authentication is documented for local/open-source apps; a commercial or hosted
release must use Sign in with ChatGPT. The local transport/authentication in this
prototype is not a public hosted deployment design.

References: [plugin file APIs](https://developers.openai.com/plugins/reference#file-apis),
[Codex app-server and authentication](https://learn.chatgpt.com/docs/app-server),
[file editor extensions](https://developers.openai.com/plugins/build/extensions),
[OpenAI MCP extensions SDK/spec](https://github.com/openai/mcp-extensions),
[public MCP packaging requirements](https://developers.openai.com/plugins/build/plugins#bundled-mcp-servers-and-lifecycle-hooks).
