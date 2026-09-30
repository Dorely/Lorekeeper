# Lorekeeper Storage Probe

A local plugin that checks whether an actual ChatGPT/Codex host can persist a
small synthetic Lorekeeper project. This is a storage investigation, not the
Lorekeeper narrative workspace. It does not open the desktop app's database or
call model providers.

Working snapshots stay in editor/process memory. The MCP server reads only its
bundled HTML; it validates incoming synthetic JSON without writing or retaining
project content. The committed fixture is artificial reference data. There is
no content database, authoritative local project copy, credential store, or
content logging.

The [evidence record](../docs/research/chatgpt-storage-probe.md) separates native
Pages operations from checks performed through this plugin. Native Page editing
and agent-mediated JSON uploads/readback have passed. Version 0.1.1's editor
connected, revised, and validated the sample in Codex desktop, but that host
exposed no library file APIs or resource bridge. Custom editor storage remains
unproven; do not choose a production storage backend from those results.

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

The repo marketplace appears as **Lorekeeper Local**. Open **Lorekeeper Storage
Probe** from Plugins or mention it in a chat. After installation or refresh,
restart the desktop client and test in a new chat as described in the
[official packaging guide](https://developers.openai.com/plugins/build/plugins).
If the skill appears but `open_storage_probe` is missing, the editor has not
opened: inspect server registration/startup before claiming host capabilities.

Codex loads an installed copy under its plugin cache. After changing this
subproject, rebuild and refresh the installation with `codex plugin add` before
retesting. Close any manually started server using that cache first; Windows
may prevent replacement while a process has it as its working directory.

`mcp.json` uses the portable working directory `"./"`, which resolves inside the
installed plugin. A plain `"."` is rejected by the portable config parser. This
package has one portable manifest and MCP configuration; there is no duplicate
legacy registration.

## Perform the storage checks

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

This probe implements no narrative context retrieval, custom model selection,
other chat providers, rich manuscript editor, background agents, images,
publication renderer, or project import from the desktop application. It cannot
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
