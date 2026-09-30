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
An app reload/open was requested to expose the new workspace. New editor
rendering, manual save/reopen, context sharing, discussion dispatch, and review
buttons remain pending actual-host validation. No claim is made from a simulated
bridge or ordinary browser.

## Limits and next scope

The prototype provides plain-text authoring, linked canon, lexical retrieval,
and reviewed replacements. It does not reproduce desktop semantic editing,
vector/graph indexes, images, publication production, desktop archive import,
Git history UI, or background agents. These omissions are implementation scope,
not evidence that local plugins cannot support them.

ChatGPT controls its model, complete prompt, compaction and conversation
lifetime. The prototype cannot take over those host internals; retrieved context
can inform a turn without becoming an authoritative complete prompt. Other chat
providers are deliberately omitted. Third-party MCP tools do not inherit native
Pages privileges. Public-directory/ChatGPT web compatibility and a stateless
hosted core require separate connectivity, storage-access, and review work.

Project saves are guarded for participating writers and keep local revision
backups. Crash recovery, power-loss durability, arbitrary external-editor races,
automatic backup restoration and collaboration across machines are unverified.
Unsaved drafts are memory-only. Project limits are documented in the
[plugin README](../../Lorekeeper.Plugin/README.md).
