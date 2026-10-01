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
and validation remained pending at that point.

## Installed chat correction, September 30, 2026

The owner's failed embedded messages reported `turn/start` code -32600. Inspection
confirmed that the installed 0.3.0 server still sent `additionalContext` as an
array, while the committed source already used the correct keyed map. Its editor
also lacked the final Disconnect control. This was a stale distributable;
successful source/development checks had not delivered the correction to that
installed copy.

Version 0.3.1 packages the correction under a distinct cache directory.
`codex.exe plugin add lorekeeper-storage-probe@lorekeeper-local --json` succeeded
without restarting Codex or editing the cache. The editor visibly shows the
manifest version, also used for MCP and app-server client identity. Sanitized
invalid-request errors now explain protocol incompatibility without implying
that these codes establish an authentication failure.

| Bundle | Repository and installed 0.3.1 SHA-256 |
|---|---|
| Server | `618534c5d13c68c018ecd42559e68791ab0c8a19b87115a89da586647f739605` |
| Workspace | `d71488123abf27c2d025a7b7901fc9167dc3dcef1ab8a7fab248a8c95b1ec5f3` |

A manual official-SDK client launched that exact installed server with the
separate synthetic development-project folder. Its handshake reported 0.3.1;
account/model discovery succeeded. A real GPT-6.1 Sol/Low turn completed,
`read_lorekeeper_project` completed, and the reply described how the two saved
chapters could develop the Book Brief's forgiveness direction. No edits were
proposed; the project hash remained unchanged. The saved turn is
`775cec77-94ba-4dd5-8d70-1355da9718cc` in the existing synthetic project's chat
sidecar. The client explicitly disconnected and closed its owned processes.

This establishes live chat through the installed package, including project
context, a dynamic tool, and saved output. It does not establish that an already
open Codex view or retained MCP connection refreshed: the actual MCP Apps
automation inventory exposed no tabs from the owner's view. Old failed turns
remain saved, and no message is automatically replayed.

A later same-version reinstall to refresh documentation was refused by Windows
while backing up the cache. The installed runtime hashes still matched the
verified repository bundles. This reinforces using a new version for changed
distributables; it does not undo the earlier successful 0.3.1 installation or
its completed live turn. No host-owned process was terminated to bypass the lock.

Clean npm installation, type/syntax checks, generated-module build and audit
passed with zero dependency vulnerabilities. The solution build succeeded with
zero warnings/errors and all 167 existing data-safety tests passed. No plugin
automated tests or harness were added.

## Full local workspace 0.4.0, September 30, 2026

The owner explicitly authorized the full-workspace implementation and synthetic
acceptance checks. The historical sections above describe their dated versions;
proposal-only editing and memory-only drafts are superseded in 0.4.0. Manual
checks used an interactive official MCP SDK client and the independent official
MCP Apps development host with the actual bundled server. Browser interaction
used the computer-use surface. No automated plugin suite, assertion harness,
mock bridge or hosted content service was introduced.

The main retained sample is
`.artifacts/plugin-development/projects/story-95e9e582.lorekeeper.json`,
**0.4 Acceptance — 测试 🌿**, project ID
`75ed90ab-40a7-426b-9d03-da13f062dea3`, final revision 33. Its chapter, entities,
conversations, composers, command receipts and history remain alongside it.
Other fixtures below share that ignored development-project folder. The owner's
Documents/Lorekeeper Projects/Test Project was not changed.

### Storage, migration and commands

| Check | Observed result |
|---|---|
| Schema-v1 migration | `migration-04.lorekeeper.json` upgraded with original IDs, chapter order, Unicode prose, canon and links retained. Brief text became Premise verbatim; chapters became Unassigned. Pending proposals stayed unapplied historical drafts. |
| Original bytes | Raw project backup SHA-256 was `5600e48023ccff09ee51332a5ad749fe7c31568d9bf6cc48bee7143a4511458a`. The original chat backup was retained separately. |
| Old conversations | The migrated Editor conversation retained its old messages and Unicode. Outline ownership was separate. |
| Autosave and reload | Manual fields saved after inactivity. A Unicode manuscript append was flushed through Reload runtime and reopened at the next revision with the same text and identity. |
| Exact command retry | Request `6fef6270-0da5-4ff6-940b-d1e9500a2266` returned the same receipt/revision/timestamp twice; changed input under that ID failed with `REQUEST_ID_REUSED`. |
| Two views, overlapping edits | Both act-title variants remained available. An application-owned conflict dialog allowed explicit application of the retained draft as a new guarded transaction. |
| Two views, unrelated edits | An act synopsis and chapter synopsis reconciled, preserving both. A focused clean title field refreshed an external saved value while retaining focus and caret. |
| Invalid properties | `{broken JSON` remained visible as an invalid field draft while canonical entity properties stayed unchanged. Corrected text properties and an entity relationship saved successfully. |
| Prepared transaction recovery | `recovery-prepared-04.lorekeeper.json` recovered only with matching before/after evidence and a verified abandoned lock; revision 2 and its commit marker remained, with temporary/pending files removed. |
| Uncertain recovery | Deliberately mismatching evidence in `recovery-uncertain-04.lorekeeper.json` preserved canonical revision 1, the pending journal and temporary bytes, and opened the recovery surface. |
| Comparison and rollback | History separated grouped manual typing and AI turns. Restoring an older prose edit after a later overlapping edit failed without replacement. Restoring an unrelated synopsis preserved the later manuscript text. |

### Outline, Editor and context

| Check | Observed result |
|---|---|
| Hierarchy | Created, renamed and moved chapters between acts. Deleting an act moved chapters to Unassigned without losing prose or beats. |
| Chapter deletion | The confirmation described prose, beats and links. Deletion removed those dependent items while retaining entities; guarded history restoration restored the chapter, its prose, beats and association. |
| AI structural editing | A real Outline turn moved a chapter and renamed a beat to **The bell answers — 海**; chapter prose remained unchanged. The open Outline refreshed automatically. |
| Editor | Edit/Read/Changes, selection and word count were exercised. Bounded Undo/Redo changed 42 → 41 → 42 words. Native browser undo had failed the earlier check and was replaced with the explicit chapter history implementation. |
| Entity fields | Custom type Organization, aliases, Unicode text properties and From/To relationship fields saved. Source selection opened the owning brief/entity fields on the right. |
| Inclusion versus pins | Explicit inclusion and pinning displayed distinct reasons and controls. Reset removed preferences while retaining manuscript content. |
| Search-to-context | Lexical search found chapter, synopsis and entity sources; opening and pinning complete sources worked. |
| Assembled request | Preview included actual instructions, schemas, quoted historical messages and keyed context. The complete active chapter appeared once; source identity, revision, reason, completeness and cost were shown. |
| Required overflow | A complete outline pin plus the active chapter exceeded the conservative budget at 32,410 / 32,000 estimated tokens. The overflow was visible and required content was not shortened. Reset retained all 2,500 chapter words. |
| Rolling conversation | `long-chat-04.lorekeeper.json` retained all 35 completed synthetic turns. Earlier messages loaded beyond the recent 20. Turns 1–31 visibly left model replay, with the assembled request estimated at 31,854 / 32,000 tokens. |
| Substantial book | `large-04.lorekeeper.json` retained three acts, 60 chapters, 300 beats, 400 entities and approximately 150,000 words. Lossless outline tables brought complete Outline context within the initial budget; Editor chapter 60 retained all 2,500 words with continuity and linked entities. |
| Expanded layout | All 60 chapter cards and 300 beats rendered. Desktop workspace client/scroll widths were both 753 pixels; narrow widths were both 375 pixels inside a 390-pixel viewport. The bottom pane switcher remained reachable. |

The [desktop capture](../../.artifacts/plugin-development/workspace-desktop-0.4.jpg)
and [narrow capture](../../.artifacts/plugin-development/workspace-narrow-0.4.jpg)
show the final development host. They do not establish rendering in an already
open native Codex view. Theme and responsive behavior are implemented; native
IME composition was source-inspected rather than manually exercised. Unicode
typing/paste was exercised.

### Embedded conversation and responsiveness

Real 6.1 Sol/Medium Editor inference used an exact target read and a guarded
command to append **A silver bell rang twice. 海 🌿.** The already-open editor
updated from 28 to 35 words without Reopen or another tab. A later Outline turn
used structural tools. Failed invalid tool arguments left content unchanged and
returned schema errors before a valid read/command succeeded; list-target
instructions were clarified afterward.

Stop during a real streamed reply retained approximately 600 words as an
interrupted turn; reload preserved that partial reply and completed changes.
No inference was resent. Shift+Enter inserted a newline; the composer survived
reload, and Enter sent a subsequent request. A selected Low effort persisted.
Separate Outline/Editor conversations and model discovery were exercised.

Measured connection startup was 430–634 ms, with a warm retained connection
action at 27 ms. Ordinary sampled local actions were 32–36 ms. Captured streamed
event-to-UI delivery was 47 ms; request preparation, first model output and tool
execution were displayed separately without content logging. First model output
in the exercised turns ranged from roughly 2.8 to 27.5 seconds. These samples
meet the local-action/UI-delivery targets; they are not a latency guarantee for
all operations or model processing.

Development reload flushed manual/composer drafts before replacing its owned
MCP generation, retained partial output, and reused the same browser tab. The
final build, independent host and owned app-server processes were closed after
validation. Synthetic project/history files and captures were retained. Automatic
approval review rejected both recursive and explicit-file cleanup of four
disposable runtime bundle copies. Those ignored directories remain preserved;
none of their owned server processes or preview listeners remains active.

### Final installed package

`codex.exe plugin add lorekeeper-storage-probe@lorekeeper-local --json` succeeded.
The CLI reported enabled version 0.4.0 at
`C:/Users/jonth/.codex/plugins/cache/lorekeeper-local/lorekeeper-storage-probe/0.4.0`.

| Bundle | Initially matching repository and installed 0.4.0 SHA-256 |
|---|---|
| Server | `d5db2f263d729e74f2115e2211a897ac57548ab884788966418698fede692474` |
| Workspace | `4931b296cd3cb512f66c8bc0f6c160de2c309466c7ea4ea92571e7c7b9828eea` |
| Unchanged storage diagnostic | `4e76ac1baac68f5e1f86802ce7091c93ae39d503fee7232c9841e9ba1a6bd2b4` |

A manual official-SDK connection launched this exact installed server. Handshake
reported 0.4.0, 30 tools were listed, and only the two explicit workspace/probe
openers owned UI resources. The original synthetic diagnostic validation passed
with digest `7c9d985fb00c60b3cd61431c45e39d2179a72de21df874a65dd26615c973acba`.
Sign-in/model discovery returned eight models. Installed-package turn
`1f779e80-3325-4ebc-8d30-bb39c69c23a5` used 6.1 Sol/Medium, completed
`read_lorekeeper_target`, and replied **The Lantern Archive — VERIFIED**.
Preparation was 10 ms, tool execution 1 ms, first model output 7,202 ms. The
project stayed at revision 33 with its content hash unchanged. The SDK client
explicitly disconnected and closed its owned processes.

Final review corrected restoration of an explicitly saved dark theme when the
host theme is light. The rebuilt final workspace SHA-256 is
`d7e4e884508c5d1bed40003f5d90f2b855cea2acfd6e7ce8b349da4e7cd187ac`.
The installed server and diagnostic remain identical to the final source.
A supported same-version reinstall was refused while backing up the cache with
Windows **Access is denied**. The installed editor therefore still has the earlier
hash above and lacks only this final theme-restoration correction. No cache files
were edited and no user-owned Codex process was stopped to bypass the lock.
The final repository distributable retains version 0.4.0; refreshing that exact
editor into the user's active cache remains a delivery limitation.

The final package was then installed through the same supported CLI into the
isolated profile `.artifacts/plugin-package-verification/codex-home`, preserving
the marketplace/plugin identity and version. Only that child CLI process used
the separate profile; no account credentials were copied or user configuration
changed. All three installed bundle hashes matched the final repository. A real
SDK connection initialized that exact server as 0.4.0, listed 30 tools, read the
final workspace resource with digest `d7e4e884508c5d1bed40003f5d90f2b855cea2acfd6e7ce8b349da4e7cd187ac`,
and passed synthetic diagnostic validation with the unchanged fixture digest.
The owned SDK/server process was closed afterward. This establishes final-package
installation and protocol behavior separately from the active user's cache.

The final source/distributable ZIP is retained at
`.artifacts/plugin-packages/lorekeeper-local-0.4.0-20260930.zip`, SHA-256
`18c5e32ff338459eeab3b4e61363b54fa5141bf13ae0473b94eba3109dabf13d`.
Its 35 entries contain manifest version 0.4.0, matching final bundles, source,
skills, fixtures and license notices, without node_modules or synthetic user data.

An already-open Codex connection can still retain an older package. Installed
protocol/inference and development-host UI are verified separately; automatic
refresh of the owner's existing native view is not established. The iframe
cannot control surrounding host tabs. Routine tools have no UI association,
and repeated development actions used the existing workspace rather than
creating extra workspace tabs.

### Final repository verification

Clean `npm ci --ignore-scripts`, TypeScript/MJS checks, the self-contained bundle
build and exact inline-module syntax checks passed. Final `npm audit` reported
zero vulnerabilities. `dotnet build Lorekeeper.sln` succeeded with zero warnings
or errors, and `dotnet test Lorekeeper.Tests/Lorekeeper.Tests.csproj` passed all
167 existing data-safety tests after the final source correction. `git diff
--check` passed. Source, bundles, tool registrations and documentation were
reviewed; obsolete proposal/accept/reject/whole-project-save runtime paths were
absent. No Press source changed, and no automated plugin tests were added.

## Current limits and next scope

Version 0.4.0 provides complete Outline organization, plain-text Editor authoring,
structured direction/entities, persistent context preferences, guarded direct
changes, compact comparison/rollback, autosave journals and rolling conversations.
World and Voices wait for v2. Sources ingestion, Images, Publish, rich manuscript
features, desktop archive imports, other providers and ChatGPT-backed editor
storage remain outside this version. Vector/graph indexes and desktop compaction
are not reproduced. These are scope boundaries, not evidence of plugin impossibility.

The embedded client controls model/effort, context assembly and local conversation
lifetime, while Codex owns processing within each turn. Experimental dynamic
tools/history injection and the exercised CLI version remain integration
dependencies. Fresh OAuth, native installed-host UI refresh, native IME input,
other platforms and public deployment were not exercised.

Synthetic interruption and recovery checks establish their exact cases, not
power-loss durability, arbitrary external-editor races, automatic backup
restoration or cross-machine collaboration. Unknown recovery evidence stays
preserved. Project limits and operational instructions are in the
[plugin README](../../Lorekeeper.Plugin/README.md).
