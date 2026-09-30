# Lorekeeper Local

A local writing prototype for Codex: plan a book, write chapters, record canon,
preview focused context, and review ChatGPT's proposed changes. The MCP server
and editor share project operations. It runs from this repo as a subproject,
independently of the desktop workbench's database and provider credentials.

Projects are portable `.lorekeeper.json` files on the user's computer. There is
no hosted service, content database, API key, or content logging. ChatGPT receives
the content requested through tools or explicitly shared from the editor.

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
5. Ask ChatGPT to revise a saved chapter, brief, outline synopsis, or canon
   entry. Its tools read exact text and submit a proposal with a source hash and
   revision. Inspect the before/after text in **Review edits**, then accept or
   reject. **Try the review workflow** supplies an example without a model call.

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
is a retrieval budget, not control over ChatGPT's full prompt or token budget.

See the [prototype evidence](../docs/research/local-plugin-prototype.md) for
actual validation and remaining integration limits.

## Install locally

Requirements: Node.js 22.12 or newer and a local client supporting portable
plugins, bundled stdio MCP servers, and MCP Apps. No external server or API key
is needed. The self-contained `dist/` files and dependency notices are committed;
recipients do not need an npm install to run the plugin.

From the repository root, using the current Codex CLI:

```powershell
codex plugin marketplace add .
codex plugin add lorekeeper-storage-probe@lorekeeper-local
codex plugin list --marketplace lorekeeper-local --json
```

The repo marketplace and installed UI appear as **Lorekeeper Local**. The package
identity remains `lorekeeper-storage-probe` so existing installs upgrade. Open
Lorekeeper Local from Plugins or ask to open its workspace. After refresh,
restart the desktop client and test in a new chat as described in the
[official packaging guide](https://developers.openai.com/plugins/build/plugins).
If the skill appears but `open_lorekeeper_workspace` is missing, the editor has not
opened: inspect server registration/startup before claiming host capabilities.

Codex loads an installed copy under its plugin cache. After changing this
subproject, rebuild and refresh the installation with `codex plugin add` before
retesting. Close any manually started server using that cache first; Windows
may prevent replacement while a process has it as its working directory.

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

It does not call other chat providers or offer custom model selection. It cannot
control ChatGPT's full prompt, token budget, compaction, or durable chat history.
Built-in Pages tools callable by an agent are not automatically callable by this
MCP server or iframe.

Local stdio installation is a development/distribution route, not evidence of
public-directory or ChatGPT web compatibility. Public submission with MCP
currently calls for a public HTTPS endpoint, subject to the documented local-MCP
exception process. Any eventual hosted Lorekeeper core must keep durable user
content in user-controlled ChatGPT storage or local files, not a Lorekeeper
content database, object store, logs, backups, or queues.

References: [plugin file APIs](https://developers.openai.com/plugins/reference#file-apis),
[file editor extensions](https://developers.openai.com/plugins/build/extensions),
[OpenAI MCP extensions SDK/spec](https://github.com/openai/mcp-extensions),
[public MCP packaging requirements](https://developers.openai.com/plugins/build/plugins#bundled-mcp-servers-and-lifecycle-hooks).
