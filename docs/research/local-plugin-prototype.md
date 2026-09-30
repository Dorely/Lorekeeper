# Local Lorekeeper plugin prototype evidence

## Scope and storage decision

This record covers the independent local plugin, not the desktop application.
The owner authorized autonomous implementation and synthetic validation of a
working prototype. The [prior storage probe](chatgpt-storage-probe.md) answered
the host question: native Pages and agent-mediated files work, while the tested
Codex MCP App lacks library file APIs and the writable resource bridge. Local
project files are the prototype's durable store. No Lorekeeper-hosted content
database or service is introduced.

## Protocol and persistence checks, September 30, 2026

Manual calls used the real official MCP SDK client and bundled stdio server in
an interactive Node REPL. There is no automated plugin test suite, saved test
harness, mock bridge, or live provider inference.

The synthetic project is retained at
`.artifacts/plugin-workspace/projects/prototype-validation.lorekeeper.json`.
Its stable project ID is `f1c51f28-1d50-4378-bcec-f77b10695cfd`. The artifact and
its `.history/` files are ignored local evidence, not packaged user content.

| Check | Observed result |
|---|---|
| Initialize/list tools | All 12 workspace/diagnostic tools registered. |
| Create synthetic project | Revision 1, two chapters, two canon entries, fresh stable UUIDs. |
| Unicode target read | `こんにちは — hello, 🌿` preserved with exact source hash. |
| Chapter-linked retrieval | Brief, linked canon, chapter/outline and lexical matches; 747 characters within a 1,000-character budget, provenance/reasons returned. |
| Propose | Revision 2 with a pending proposal; manuscript unchanged. |
| Exact retry | Same proposal UUID/arguments returned revision 2, without duplication. |
| Accept | Revision 3; exact proposed text applied and status accepted. |
| Stale manual save | Original revision-1 hash rejected with `CONFLICT`. |
| New proposal, then manual edit | Proposal saved at revision 4; author's alternative saved at revision 5. |
| Accept against changed target | Rejected with `CONFLICT`; newer author text preserved. |
| Reject | Revision 6; proposal rejected and author text unchanged. |
| Close/restart/reopen | Fresh SDK connection read same project/chapter identities, revision 6, saved hash and Unicode; five prior revision backups present. |
| Filename traversal | `../...` rejected with `INVALID_FILENAME`. |
| Existing filename | Creation rejected with `FILE_EXISTS`; existing content preserved. |
| Reuse proposal UUID for different text | Rejected with `PROPOSAL_ID_REUSED`. |
| Installed 0.2.1 package | Bundled server initialized without runtime npm dependencies; both workspace/diagnostic UI resources listed. |
| Paged exact target | 100-character first page and returned continuation shared revision/hash; page boundary was valid. |
| No-op manual save | Remained revision 6. |
| Invalid existing revision backup | Save failed with `INVALID_PROJECT`; current revision 6 preserved. Synthetic broken backup removed afterward. |

Version 0.2.1 is installed through `codex plugin add` under the existing local
marketplace identity, displayed as **Lorekeeper Local**. Source type/syntax and
exact embedded-module checks passed; a clean npm install/audit reported zero
vulnerabilities. Solution build passed with zero warnings/errors; all 167 existing
data-safety tests passed. Installation/protocol checks alone do not establish
host editor behavior.

## Actual host integration

The current chat initially retained the old probe tools after installation.
After reload, the owner exercised 0.2.1 in Codex: created **Test Project**, saved
its brief at revision 2, used Side Chat to request an opening, and saw a pending
proposal in Review edits at revision 3 after manually reopening. Screenshots
show the real editor, saved status, and before/after review. The assistant also
read the saved project through the actual plugin tools. This establishes the
authoring/Side Chat/proposal path; accept/reject buttons, context sharing, and
discussion dispatch were not independently exercised in that host.

That run exposed two integration bugs: every data tool was associated with the
workspace UI and opened another tab, while the original editor missed changes
made through another call. Version 0.2.2 removes UI resource associations from
data/app-only tools, reserves opening for explicit display requests, and adds
read-only hash-based synchronization to existing editors with dirty-draft and
new-project-form protection.

Manual real-SDK checks of the installed 0.2.2 package observed:

- Only `open_lorekeeper_workspace` and the separate `open_storage_probe` have
  UI resource/template metadata; ordinary data tools do not.
- `sync_lorekeeper_project` is app-only and read-only. An unchanged hash returns
  no snapshot. A saved proposal returns revision 2 and one pending edit in a
  private snapshot; manuscript prose remains unchanged. Repeating with the
  observed hash returns no snapshot again.
- Retrieval still returned seven context sources.

The separate synthetic sample is retained as
`Documents/Lorekeeper Projects/refresh-validation-00bf14b1.lorekeeper.json`
under the local user's home. Its ID is `f207b42f-70c1-41a4-b4ed-277eca845ce6`.
No changes were made to the owner's Test Project during these checks.
The 0.2.2 editor's no-extra-tab and automatic-refresh behavior await a refreshed
actual-host session. A final explicit opener still reported version 0.2.1 in
the current chat, confirming that its running connection had not refreshed.
Version 0.2.2 clean npm install/check/build/audit passed with zero vulnerabilities;
solution build had zero warnings/errors and all 167 existing tests passed.
No claim is made from a simulated bridge or ordinary browser. The manually owned
SDK client/server and interactive REPL were closed after validation.

## Independent reloadable development host, September 30, 2026

The owner requested an autonomous development loop without repeated Codex
restarts. The installed CLI exposes app-server proxy/daemon tooling, and the
[official API](https://learn.chatgpt.com/docs/app-server#api-overview) includes
`config/mcpServer/reload`. The current desktop launched its server over stdio;
the CLI proxy's default control socket was not connectable. No undocumented IPC,
installed-cache edits, broad process kills, or second Codex instance were used.

The independent preview uses the official MCP Apps `AppBridge` and a real SDK
stdio connection to the same plugin bundles. At that stage it had no model/chat or ChatGPT
file APIs. This is evidence of plugin UI/runtime behavior in our development
host, not proof of the existing Codex connection being refreshed.

Manual browser interactions and the preview's manual tool console observed:

| Check | Observed result |
|---|---|
| Create, edit, save | Synthetic local project created; title/Unicode brief saved at revision 2. |
| Separate proposal | A host tool call created revision 3; the already-open Review view automatically displayed it and the pending count without Reopen. |
| Dirty draft | An external revision 4 left the unsaved Unicode draft and loaded revision 3 intact, with a newer-state notice. |
| Stale editor save | Failed without replacing current content; draft and copy surface remained available. |
| New-project form | Typed fields stayed present across polling after another external proposal. |
| Rebuilt UI | A temporary visible marker appeared after Reload runtime; the editor hash changed from `af438c056aee` to `ee08ec27873e`. Marker was then removed and the original hash restored. |
| Rebuilt MCP server | A temporary server-version marker was loaded, then rebuilt while that server stayed running. Reload replaced PID 40316 with 38448 and changed the displayed version/hash to the final `0.2.2` / `4e04f0085224`. |
| Saved persistence after reload | Reopened the same synthetic project ID, revision and Unicode; proposals remained pending and authored text unchanged. |
| Loopback API guard | A request without the preview's origin/session authorization returned HTTP 403. |
| Shutdown | The owned MCP PID and development host exited; the runtime-copy directory was empty and project/history files remained. |

The first direct-from-repo development process exposed a Windows bundle-write
failure during a rebuild. Each generation now runs from an owned disposable copy
instead. A subsequent rebuild succeeded while that copied server was running.
Temporary source markers were removed; the installed plugin's bundles are
unchanged by the development tooling feature.

The retained synthetic file is
`.artifacts/plugin-development/projects/development-reload.lorekeeper.json`,
project ID `95857d06-f777-4db0-bea9-497fe8d9d489`. Its local revision files remain
with it. The owner's Test Project was not modified. There is no automated plugin
test suite, replay harness, mock storage bridge, or live inference in this check.
Final static checks/build/test and owned-process cleanup are recorded with the
development tooling commit.

## Embedded Codex conversation, September 30, 2026

Version 0.3.0 adds an actual writing conversation inside the custom UI, backed by
an owned local Codex app-server over stdio. It uses Codex-owned account sign-in,
not copied desktop OAuth tokens or a hosted Lorekeeper service. The official
[app-server API and authentication guidance](https://learn.chatgpt.com/docs/app-server)
and the installed CLI's generated experimental schemas informed the implementation.
CLI 0.159.2 was exercised. Local/open-source authentication is distinct from
commercial/hosted deployment, which requires Sign in with ChatGPT.

Manual UI checks used the official MCP Apps development host, real bundled MCP
server, and real app-server/OpenAI inference. This is not a simulated bridge or
proof that the existing installed Codex connection refreshed. The retained sample
is `.artifacts/plugin-development/projects/embedded-chat-validation.lorekeeper.json`,
project ID `305a1ab1-087b-4666-9b91-f952e6e5af08`. Its chapter/canon identities,
chat sidecar and local revision backups remain available. The owner's Test Project
was not touched. The [captured editor/chat view](../../.artifacts/plugin-development/embedded-chat.png)
shows the pending reviewed replacement beside the embedded pane.

| Check | Observed result |
|---|---|
| Account/model discovery | Signed-in ChatGPT account; seven models initially, eight on a later reconnect, with their advertised efforts and default. GPT-6.1 Sol was selected without a maintained catalogue. |
| Context preview | Complete Book Brief plus seven sourced excerpts, linked canon and Unicode, within the requested retrieval budget. |
| Live drafting turn | Streamed a canon-grounded reply, read exact canon/chapter targets, and saved a 50-word proposal. The writing remained unchanged. |
| Automatic workspace refresh | Existing Review edits count/view displayed the pending proposal without Reopen or another tab. |
| Later turn | A new ephemeral thread recalled the earlier proposed paragraph from replayed prose and used the newly saved forgiveness direction. No new proposal. |
| Native usage | Input/output counts were displayed; the exercised model reported a 258,400-token context window. This does not alter the desktop catalogue. |
| Stop during startup | Turn saved as interrupted without an answer or a proposal. |
| Stop during streaming | A running reply was interrupted and 960 characters of partial assistant prose persisted. Completed proposals stayed pending. |
| Close/restart/reopen | Earlier completed/interrupted messages, tool outcomes, selections, context snapshots and pending project proposal survived fully closing and restarting the development runtime. No message was replayed. |
| Identical completed-message retry | Real MCP call returned the existing turn while offline; sidecar revision remained 14 and no inference ran. |
| Unsaved manuscript guard | Send refused an unsaved Book Brief; both draft and composer text remained intact. No turn was created. |
| Final overview tool | `read_lorekeeper_project` completed inside the chat; reply correctly reported two chapters and the forgiveness direction. |
| Visibility/packaging | All eight chat control/read tools were app-only with no UI resource association. Bundled SDK server initialized as 0.3.0. |
| Long-transcript layout | Found and fixed composer displacement; 1,440-pixel layout constrains chat scrolling, 640-pixel layout stacks the pane below the workspace with accessible composer/controls. |

An early additional-context request used the wrong protocol shape and failed
before inference. The current CLI requires a keyed map of typed context values;
the failed turn is retained for inspection. A later model supplied invalid
overview arguments, then succeeded with retrieval; schema errors now identify
invalid field paths/codes without echoing argument values. The final overview
call with the declared schema passed.

Full desktop token-reserve/tombstoning behavior is not ported. The client rebuilds
fresh project context, replays bounded conversation prose, excludes historical
tool/reasoning payloads, and never silently trims history. Dynamic tools/history
injection remain experimental app-server APIs. Fresh OAuth, process-crash recovery,
power-loss durability, other platforms/CLI versions, and public hosted deployment
remain unverified. No new plugin automated tests or harness were added.

Final source/type/syntax, generated-module build, clean npm install and audit
passed with zero dependency vulnerabilities. The solution build had zero warnings
or errors and all 167 existing data-safety tests passed. Owned development MCP and
app-server processes and the temporary browser tab were closed; runtime copies
were removed while synthetic project/chat/history files were preserved.

Refreshing the installed package through `codex.exe plugin add` failed while
backing up its cache entry with Windows access denied. The CLI listing reported
0.3.0, but cached server/editor digests differed from the final repository bundles;
that version label is not proof of a successful refresh. No installed-cache files
or user-owned Codex processes were manually changed to bypass the lock. The final
code was exercised through the reloadable development host; installed-host upgrade
and validation remain pending.

## Limits and next scope

The prototype provides plain-text authoring, linked canon, lexical retrieval,
and reviewed replacements. It does not reproduce desktop semantic editing,
vector/graph indexes, images, publication production, desktop archive import,
Git history UI, or background agents. These omissions are implementation scope,
not evidence that local plugins cannot support them.

The embedded client controls its own model/effort, context assembly and local
conversation lifetime, while app-server owns native inference internals. It does
not take over the host ChatGPT conversation or reproduce the desktop's exact
automatic compaction policy. Other chat providers are deliberately omitted.
Third-party MCP tools do not inherit native
Pages privileges. Public-directory/ChatGPT web compatibility and a stateless
hosted core require separate connectivity, storage-access, and review work.

Project saves are guarded for participating writers and keep local revision
backups. Crash recovery, power-loss durability, arbitrary external-editor races,
automatic backup restoration and collaboration across machines are unverified.
Unsaved drafts are memory-only. Project limits are documented in the
[plugin README](../../Lorekeeper.Plugin/README.md).
