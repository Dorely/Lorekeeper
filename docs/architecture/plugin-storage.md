# Plugin storage investigation

## When to read

Read this chapter completely before changing `Lorekeeper.Plugin/`, the repository
plugin marketplace, plugin manifests or MCP tools, host file-library integration,
file editor entrypoints, or claims about ChatGPT-backed content storage.

## Scope and ownership

This chapter owns the independent local storage-probe subproject. It does not
change the ASP.NET/Blazor/Electron runtime or reuse its SQLite database, provider
tokens, services, project exports, or persistence contracts. The full Lorekeeper
plugin workspace is not implemented.

The future plugin's hard storage constraint is that durable user content stays
in ChatGPT storage or user-owned local files. A future hosted core may process
content transiently but must not persist it in a Lorekeeper-operated database,
object store, content logs, backups, or queues. A local MCP distribution and a
public-directory release have different connectivity/review requirements.

## Current architecture and invariants

The repo marketplace selects a portable Agent Plugins package containing one
skill, a Node stdio MCP server, and a self-contained MCP App editor. Runtime
requires Node 22.12 or newer; bundled SDKs eliminate a runtime npm install. There
is no HTTP service. `mcp.json` resolves the server's working directory `"./"`
inside the installed plugin. Root `plugin.json` is the only manifest.

The server exposes `open_storage_probe` to the model/app and
`validate_probe_project` to the app. Opening registers global, thread, and
`.lkproject` file entrypoints at `ui://lorekeeper/storage-probe.html`. The server
reads the sibling HTML resource; project JSON is validated and hashed in memory,
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

Working editor content, ETags, and resource URIs stay in memory. There is no
autosave, localStorage, filesystem content store, credential store, or content
logging. Closing loses unsaved changes. Reports include capability booleans,
synthetic identities, file IDs, revisions, digests, byte counts, and operation
outcomes; they exclude content, ETags, resource URIs, and download URLs.

Native Pages operations belong to the host agent's built-in tools. Their guarded
editing and library upload/readback results do not establish that a third-party
MCP server or editor has the same capabilities. The dated
[evidence record](../research/chatgpt-storage-probe.md) owns observations,
sample artifacts, blockers, and the storage decision. Custom-editor persistence
and conflict protection remain unverified until exercised in an actual host.

## Key files and file families

| File family | Responsibility |
|---|---|
| [Marketplace](../../.agents/plugins/marketplace.json) | Local repo plugin discovery and install policy. |
| [Plugin subproject](../../Lorekeeper.Plugin/README.md), `plugin.json`, `mcp.json` | Install/development instructions, identity, UI metadata, and contained stdio registration. |
| `Lorekeeper.Plugin/src/server.mjs` | MCP tool/resource registration, synthetic schema validation, and exact UTF-8 SHA-256. |
| `Lorekeeper.Plugin/src/app.ts`, `src/app.html` | Memory-only editor, capability detection, library reads/uploads, conditional writes, stale-save check, and content-free reports. |
| `Lorekeeper.Plugin/skills/storage-probe/SKILL.md` | Actual-host probe workflow and evidence boundaries. |
| `Lorekeeper.Plugin/scripts/build.mjs`, `package*.json`, `tsconfig.json` | Exact-pinned dependency closure, static checking, bundles, and notices generation. |
| `Lorekeeper.Plugin/dist/`, `THIRD-PARTY-NOTICES.md`, `licenses/` | Committed distributable server/HTML and bundled dependency license texts. |
| `Lorekeeper.Plugin/fixtures/`, `.gitattributes`, `.editorconfig` | Synthetic reference bytes and stable LF package formatting. |

## Related chapters

- [Runtime and host](runtime-host.md) owns the separate desktop application.
- [Persistence, migrations, and import](persistence-migrations-import.md) owns
  desktop data, which this probe neither opens nor changes.
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
