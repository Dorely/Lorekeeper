# Lorekeeper Local

Version 0.4.0 is a local Outline and plain-text Editor workspace for Codex.
Chat is on the left, writing in the middle, and context/details on the right.
It uses the desktop app's theme tokens and light/dark themes. Narrow layouts
switch between Chat, Workspace and Details; they do not make the local MCP
server reachable from ChatGPT web or a phone.

Projects, conversations, composer drafts, recovery journals and change history
stay in user-owned local files. There is no hosted Lorekeeper content store or
copied credential. Codex owns sign-in and model transport. Sending a message
shares its prose, selected project context and tool results with OpenAI.

## Use the workspace

1. Create a blank or synthetic sample project from **New**.
2. In **Outline**, organize acts, chapters and beats. Edit titles/synopses inline,
   use arrows or drag handles to reorder, and **Move to act…** for a chapter's
   parent. Deleting an act moves its chapters to Unassigned. Chapter deletion
   confirms removal of prose, beats and links; entities remain.
3. Use the right pane for the structured **Book Brief**, protected **Project
   Guidance**, facts and grouped entities. Entities support custom types,
   aliases, text properties, relationships and chapter/beat associations.
   Properties currently use a validated JSON object of text values.
4. In **Editor**, select a chapter and write plain text. Edit/Read/Changes,
   selection, word count and bounded Undo/Redo are available. Formatting controls
   are deliberately labelled as future work.
   Undo/Redo groups recent typing. External saved changes reset that chapter's
   local stack; persistent restoration is available through Changes / History.
5. Manual fields autosave after 500 ms of inactivity. Saving/Saved/Failed or
   Conflict status is visible. Navigation, Send and development reload flush
   edits first. Invalid JSON stays in its field; a failed submitted batch stays
   in a local recovery journal. Inspect both variants in the conflict dialog.
6. The embedded conversation connects to the owned local Codex app-server on
   first project opening. **Settings** provides Connect, Disconnect and official
   Sign in. Models and reasoning efforts come from model/list. New conversations
   prefer 6.1 Sol/Medium when offered; existing unavailable choices show an error.
   Enter sends, Shift+Enter adds a line, and IME composition does not send.
7. Outline and Editor have separate project-scoped conversations. Selecting
   another chapter keeps the Editor conversation. Earlier messages remain saved
   and can be loaded through **Earlier messages**.
8. Requested AI changes apply directly. **Changes / History** groups them by
   turn and groups manual typing. Compare before/after, then **Restore affected
   items** to create a guarded rollback. Later overlapping edits block rollback;
   unrelated fields remain intact. Earlier proposals are read-only historical
   comparisons or unapplied drafts, never auto-applied by migration.

Only an explicit workspace opener owns a UI resource. Reads, autosave, chat
and mutation tools refresh the existing workspace without opening more tabs.
The iframe cannot close or manage Codex's surrounding tabs.

## Context and saved data

Book Brief and Project Guidance are protected. Outline receives the complete
organized hierarchy and associations through lossless tables. Editor receives
its complete active chapter once, its outline, linked entities, preceding-chapter
continuity and relevant lexical matches. Chapter preferences persist inclusion,
   exclusion and explicit pins. Search can open source fields or pin complete content.
**Reset** changes preferences only. **Prompt** shows authoring instructions, tool
schemas, the current message, project sources and quoted saved prose; source identity, revision, reason, completeness
and estimated token cost appear in Details.

The complete request budget includes instructions, tool schemas, source metadata,
selected prose and replayed messages. Before a native window report, estimated
input is capped at 32,000 tokens. After a report, 20% of that window is reserved
for generation/tools; the author's context budget can be smaller. Oldest turns
leave rolling context visibly, while the complete transcript stays saved.
Required direction, current chapter or pins are never silently shortened.
Overflow needs an explicit adjustment. Tokens are conservative UTF-8 estimates,
not a tokenizer guarantee. Codex still owns processing inside a running turn.

The default folder is Documents/Lorekeeper Projects in the user's home.
Set absolute LOREKEEPER_PROJECTS_DIR before starting the client to change it.
Only contained simple filenames are accepted. Projects are independent schema-v2
.lorekeeper.json files; desktop databases/manuscript archives are not opened.

Supported schema-v1 files upgrade on opening. Original bytes are retained in
.history; IDs, order, prose, canon, links, proposal records and conversations
survive. The old brief becomes Premise verbatim and chapters begin Unassigned.
Old conversations become Editor conversations.

Exact affected-field/list/dependency hashes guard one shared command service.
Stable request IDs make identical retries idempotent; different input with the
same ID fails. Compact durable before/after records precede project replacement.
Long text changes retain only the changed range, not another full book.
Participating writers serialize. Unrelated fields reconcile; an overlapping
dirty draft is preserved for the application-owned conflict dialog.

Unambiguous prepared transactions and demonstrably dead-PID locks can recover.
Unknown locks, mismatching evidence and uncertain transactions fail closed,
preserving files for inspection. Previous backups are retained. Filesystem
rename is not an atomic compare-and-swap against an uncooperative external
editor; power-loss durability is not established. Preserve the project,
.history, transaction and lock files before resolving uncertain recovery.

Project safety bounds are 32 MiB, 1,000 chapters, 10,000 beats, 5,000 entities
and 100 inventory files. Conversations have a 64 MiB per-project file bound;
the full transcript is retained rather than truncated at that bound. Partial
replies are journaled while running and preserved on Stop/graceful reload.
Uncertain inference is never automatically resent; its send identity is retained.

World and Voices wait for v2. Sources ingestion, Images, Publish, rich manuscript
features, desktop archive imports, other providers and ChatGPT-backed editor
storage are outside this version. The original storage diagnostic remains
separate and unchanged.

See the [evidence record](../docs/research/local-plugin-prototype.md) for exercised
behavior and limitations, and the [storage evidence](../docs/research/chatgpt-storage-probe.md)
for the distinction between native Pages privileges and plugin file APIs.

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
directory. Version 0.4.0 uses a distinct cache directory. Close the old view and open
Lorekeeper Local again; its header must show **Local · 0.4.0**. A CLI listing
alone does not establish that an existing view/connection refreshed. Never edit
installed cache files. Installed-package verification is separate from the preview.

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
server version, process ID, generation, and server/editor bundle hashes. Manual
edits are flushed before closing; failed saves prevent reload. Composer drafts and
partial replies are preserved.
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
these synthetic checks. The approved full-workspace plan also authorizes its
synthetic UI, inference, recovery and installed-package acceptance checks.

## Capability boundaries

Local stdio success does not establish a public hosted deployment or ChatGPT web
compatibility. A future public core needs separate connectivity, authentication,
distribution and review work. Durable content must remain on users' machines or
in user-controlled ChatGPT storage; a Lorekeeper-hosted content database is outside
the agreed direction.

References: [plugin file APIs](https://developers.openai.com/plugins/reference#file-apis),
[Codex app-server](https://learn.chatgpt.com/docs/app-server),
[file editor extensions](https://developers.openai.com/plugins/build/extensions),
[MCP extensions](https://github.com/openai/mcp-extensions).
