# ChatGPT-backed storage probe

Observed September 29, 2026 (America/Los_Angeles; cloud receipts dated September
30 UTC), using Codex desktop 26.928.20755 on Windows, bundled Codex CLI 0.159.0,
Node 22.18.0, and the authenticated personal ChatGPT account. Only synthetic
content was used. This record describes evidence, not a production storage
guarantee or public-plugin compatibility claim.

## Decision so far

Native Page editing is a viable agent-mediated storage option. The built-in
Pages tools can also upload and read back separate JSON project snapshots in
ChatGPT's file library. Neither proves custom-editor autosave or conditional
replacement through our plugin. Keep the preferred plugin storage backend
undecided until the real file bridge passes the editor checks below.

The local plugin is implemented, installed, and enabled. Its MCP server and
bundled resource work through a real SDK client. The initial desktop **Try it**
attempt loaded the skill but reported that `open_storage_probe` was missing;
no editor opened. Inspection of the official portable config parser found that
the initial `cwd: "."` was invalid. It was corrected to `"./"` and the installed
copy was refreshed. Actual desktop editor capability/save checks remain pending
after that correction; the original failure does not establish that local MCP
Apps are unsupported.

A fresh bundled Codex app-server was then initialized for a read-only
`mcpServerStatus/list` request restricted to `lorekeeper_storage`. It recognized
`lorekeeper-storage-probe@lorekeeper-local`, returned server version 0.1.0, and
discovered `open_storage_probe` and `validate_probe_project`. No registration
error for the probe appeared. This confirms the corrected registration loads in
a fresh Codex runtime. No conversation or inference turn was created, and this
manually started runtime was terminated afterwards.

## Native Pages evidence

The private [Lorekeeper Storage Probe Page](https://chatgpt.com/space/page_07f86ae777208191bd8bd8e20323b050)
and its attached samples are preserved for inspection. Creation metadata put it
in personal storage with no parent or workspace. The sharing read returned no
direct shares and complete inherited access with no sources. No sharing policy
was changed.

The Page was created, read, edited, and reread through the built-in Pages plugin.
Its project ID is `85d88cf9-79a9-4cb6-8fba-a541c707833b`; chapter ID is
`291527bb-6c30-40cb-9dca-5cff8c6950fa`. Revision advanced from 1 to 2 while these
identities stayed fixed. The Unicode sample changed from:

```text
The café’s keeper wrote: “こんにちは — hello, 🌿.”
```

to:

```text
The café’s keeper revised the note: “こんにちは — hello again, 🌿.”
```

A deliberately outdated paragraph block hash was then submitted with the
replacement `STALE WRITE MUST NOT APPEAR`. The edit returned `edit_conflict`
(`CONFLICT` in the tool error). A subsequent read retained revision 2 and the
newer Unicode paragraph; the stale marker was absent. This proves guarded native
Page editing for this sample, not file-resource conflict handling.

## Native file evidence

The Page contains three attached artifacts: the original `.lkproject` fixture
and revision 1/revision 2 JSON copies. Uploads used the built-in Pages
`write_page_reference` tool, followed by fresh Page reads/guarded insertion of
the returned library links. Reads used `read_page_reference`. These were
agent-mediated operations, not our iframe's `window.openai` file APIs.

| Snapshot | MIME type | UTF-8 bytes | Upload/readback SHA-256 | Result |
|---|---|---:|---|---|
| Revision 1 `.lkproject` | `application/octet-stream` | 446 | `7c9d985fb00c60b3cd61431c45e39d2179a72de21df874a65dd26615c973acba` | Upload/access receipt succeeded; the Page reference reader rejected this binary MIME type. |
| Revision 1 `.json` | `application/json` | 446 | `7c9d985fb00c60b3cd61431c45e39d2179a72de21df874a65dd26615c973acba` | Readback matched exact uploaded bytes, stable IDs, revision, and Unicode. |
| Revision 2 `.json` | `application/json` | 463 | `046d3e41a91d592c19b854f434cd1662b82f3da1d0b2e9de9c6c409afbb263f2` | Readback matched the revised paragraph/revision with the same identities. Revision 1 was reread and remained unchanged. |

The two JSON uploads returned distinct persistent library references. They
establish explicit new-version artifacts through the native upload path. No
native file replacement or conditional file update was exercised. Files were
not opened/closed in a custom editor, so these readbacks do not satisfy that
editor lifecycle check. Upload-time MIME recognition of `.lkproject` by the
Pages tool also does not predict the iframe API, which supplies
`application/json` explicitly.

## Reusable plugin evidence and remaining checks

The implementation is in [Lorekeeper.Plugin](../../Lorekeeper.Plugin/README.md).
The repo marketplace installs `lorekeeper-storage-probe@lorekeeper-local`,
version 0.1.0. CLI install/list confirmed the plugin is installed and enabled.
The installed `mcp.json` uses `"./"` and matches the corrected repo config.

Authorized manual SDK checks established a stdio handshake, discovery of both
tools, successful `open_storage_probe`, exact fixture validation/digest, rejection
of malformed `{}` input, and reading the `text/html;profile=mcp-app` resource.
An isolated directory containing only the generated server and HTML also passed
these checks. It required no `node_modules` in that runtime directory. This
establishes the packaged server/resource boundary, not iframe rendering or host
file capabilities. Manually owned MCP processes are closed after the checks.

| Real host check | Current evidence |
|---|---|
| Editor renders/connects | Initial Try it failed before the editor opened; corrected registration awaiting host exercise. |
| Library upload/picker/download APIs | Not observed in our editor. |
| Host file entrypoint/resource read | Not exercised in our editor. |
| Writable metadata and ETag | Not observed. |
| Editor save, close, reopen with matching bytes | Not exercised. |
| Editor paragraph update, save, close, reopen | Not exercised. |
| Same-file update vs new artifact | Not established through our editor. |
| Stale `ifMatch` save preserves newer bytes | Not exercised. Native Page block conflict is separate evidence. |

Final static/repository verification passed: plugin `npm ci --ignore-scripts`,
TypeScript/server syntax checks, bundle/notices generation, and npm audit with
zero vulnerabilities; `dotnet build Lorekeeper.sln` with zero warnings/errors;
and `dotnet test Lorekeeper.Tests\Lorekeeper.Tests.csproj` with 167 passed,
zero failed/skipped. The staged diff and working diff passed whitespace checks.
Generated SDK template-string whitespace is preserved under the package's
explicit attributes. No new automated tests were added. Desktop startup,
provider inference, Press-specific tests, and release packaging were not run;
their owning runtimes were not changed by this probe.

No ordinary browser page, mocked host API, simulated persistence bridge, real
manuscript, provider inference, or hosted Lorekeeper content store was used.
The app's existing database/export format and provider contracts are unchanged.

## Reproduction and decision rules

Follow the plugin README's actual-host sequence, preserving file IDs and exact
digests at each save/reopen. The official local packaging workflow requires
refreshing/restarting the client and testing in a new chat after install/update.
If the tool is still absent after the corrected manifest is loaded, capture the
server registration/startup failure before calling it a file API limitation.

- Reliable conditional update, reopen, and stale-save rejection through the real
  file bridge can support choosing ChatGPT project files as the preferred backend.
- Upload/reopen without reliable replacement supports explicit **Save new
  version**, with the limitation visible to users.
- Native Page editing alone supports an agent-mediated option; it does not
  establish custom-editor autosave.
- Missing host capabilities remain a precise blocker. Do not infer an API is
  present or safe from SDK types, metadata declarations, or native Pages access.

Any eventual hosted application core must still respect the hard constraint:
durable user content in ChatGPT or user-owned local files, with no Lorekeeper
content database, object store, content logs, backups, or queues. Public MCP
submission currently requires an HTTPS endpoint unless local MCP support is
arranged through OpenAI; this local proof is not public-release evidence.

## Sources

- [Plugin packaging/local workflow](https://developers.openai.com/plugins/build/plugins)
- [Plugin file APIs](https://developers.openai.com/plugins/reference#file-apis)
- [File editor extensions](https://developers.openai.com/plugins/build/extensions)
- [OpenAI MCP extensions SDK/spec](https://github.com/openai/mcp-extensions)
- [Official Codex portable MCP config parser](https://github.com/openai/codex/blob/main/codex-rs/codex-mcp/src/agent_plugin_config.rs)

The linked SDK/parser sources were inspected during the probe; their mutable
main branches are supporting implementation references, not pinned host-version
guarantees. Package versions used by this probe are pinned in its lockfile.
