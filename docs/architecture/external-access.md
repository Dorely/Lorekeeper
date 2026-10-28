# External agent access

## When to read

Read this chapter for the local MCP server, external client setup, grants,
workspace onboarding, external mutations, authenticated binary transfers,
external-project links, and transfer baselines. Read the owning domain chapters
before changing a surfaced command. Read persistence for transaction and recovery
changes, assistants for context composition, and version history for snapshot or
checkpoint behavior.

## Scope and ownership

External Access is an adapter over application commands and queries. It does not
instantiate assistant tool classes, create assistant turns, operate the user's
current selection, or expose a generic service invocation, filesystem, or SQL API.
The application remains the canonical creative owner. External agents own their
other project's file mapping and integration decisions.

## Current architecture and invariants

### Local connection and grants

The official `ModelContextProtocol.AspNetCore` SDK is pinned to 2.3.0. Streamable
HTTP runs at `/mcp` in the existing ASP.NET Core process, with stateless transport
connections and durable logical work. External access starts disabled. The HTTP
gate precedes the browser error pipeline and requires startup Ready, a loopback
peer and Host, the actual listening port, and a loopback/same-port HTTP Origin
when Origin is supplied. MCP and transfer responses never pass through the
browser's HTML status-page reexecution or HTTPS redirect middleware.

Each configured client has its own opaque bearer credential, enabled state,
explicit capability IDs, selected-project IDs or explicit all-local-project
scope, and last-used timestamp. Creating a project through a restricted grant
adds that new project to that grant. Presets are expanded only when selected and
saved; an upgrade does not add new capabilities to existing grants. Discovery,
resource templates, context, searches, exact reads, binary responses, and command
execution use the saved grant. Authorization is checked again under the writer
before a mutation commits. Revoke takes effect on subsequent requests without
requiring a transport restart.

Credentials live only in installation security state. SQLite stores their hash
and a Data Protection protected setup copy. Windows protects the key ring with
DPAPI; other hosts restrict its directory and files to the current user. The
client's private configuration contains the header required by that client.
Credentials are absent from skill instructions, project content, snapshots,
receipts, and logs. Setup uses the user's client configuration, never a shared
repository MCP configuration.

External Access settings provide enablement, capability and tool previews,
project scope, client setup, reconnect/rotation, explicit private manual
configuration, last use, and application-owned revocation confirmation. Codex
setup probes Streamable HTTP CLI features and can find the compatible desktop
runtime without replacing an older PATH CLI. Its documented user TOML header
configuration is updated only for the grant's generated server entry. Claude
Code setup probes supported transport/header options and uses its user-scope
MCP commands. Unrelated client entries are retained. A private local connection
file lets the bundled noninteractive transfer helper authenticate without
printing a token or embedding it in a skill.

### Workspaces and context

`list_projects`, `create_project`, and `list_unfinished_work` establish explicit
identities. `open_workspace(projectId, surface, task, chapterId?, editionId?,
workId?)` returns a workspace ID, work ID, explicit target, application-built
context, current Review Edits behavior, and available tools. The supported
surfaces are Outline, Editor, World, Images, Voice, and Publish. Outline, World,
and Voice have Core ownership; Editor, Images, and Publish can target a release.
One work ID can span several workspaces. Opening or switching a workspace never
navigates the author's UI.

Outline uses the existing outline working-context builder and shared prompt
composer. Other roles use the existing typed context builder. The external
audience replaces in-app turn/reply assumptions with workspace refresh rules.
Context includes only enabled, permitted sections; mixed sections are omitted
when the grant cannot read all their constituent categories. Referenced-project
origins remain explicit and are checked against project scope. Exact owning
reads remain available when a mixed context section is omitted. Voice profiles
are filtered from canon reads without voice permission and cannot be changed
through property patches without voice editing permission.

Every surface tool requires a matching active workspace and validates project,
child, and Core/release identity. `get_workspace_context` rebuilds current state
after reconnect, compaction, or a changed task. No transport session or remembered
assistant conversation is the authority. Large structured results use the shared
exact JSON-path paginator and preserve all target arguments in continuations.

### Commands, receipts, and completion

The catalog defines typed request schemas, required capabilities, allowed
surfaces, and whether a command changes creative state. Surface operations call
owning services for outline and canon; semantic manuscripts, styles and notes;
World briefs and guarded research; image assets and durable jobs; voice samples
and profiles; and Core/release publication, covers, sections, readiness and
artifacts. Provider administration, credentials, arbitrary files/SQL, migrations,
remote-history commands, project deletion and history restoration are excluded.

Mutations require a UUID mutation ID. The authoring fence flushes registered
writers before the project lease and database writer. Nested owning services
borrow that operation and use savepoints when they need a transaction. Current
authorization and target state are rechecked within this boundary. Content,
safe client/work provenance, request hash and outcome receipt commit atomically.
The external actor also identifies publication audit entries. Revision checks,
Contest guards, semantic reference validation, annotation rebasing, history
invalidation, and index maintenance remain owned by the application services.

Failed tool results carry a generic message unless the exception is known to be
safe. `ExternalAccessException`, revision conflicts, authoring fences, and
`CorrectableInputException` pass their message through, so a rejected value
reaches the client with the field and its valid options.

An uncertain response is recovered by repeating the same operation and payload
with the original mutation ID, or by `read_mutation_receipt`. `list_work_mutations`
recovers forgotten mutation IDs; `list_image_jobs` recovers recent image jobs.
Reusing an ID with
a different payload is rejected. Receipts remain readable after task completion
subject to current permissions. Successful responses distinguish committed
content from projection repair status. Captured index targets are durable local
operational metadata; the repair worker retries them without replaying content
writes. Queue wakeups, history invalidation and UI notifications occur after the
outer commit. A visible project receives a refresh notice; its Refresh action
fences visible authoring work before reloading the workspace.

`finish_work` waits for active project image/publication jobs to finish or be
cancelled. It first persists a Finishing intent, preventing additional task
writes from racing completion. Review Edits on leaves saved changes pending.
Review Edits off checkpoints the complete live project through existing history
services with an idempotent work-based request key, inheriting already configured
automatic remote synchronization. A restart can recover unfinished/Finishing
work and repeat completion. Abandonment preserves already committed work.

### Binary delivery and visual inspection

Image uploads use a reservation, bounded authenticated byte upload, SHA-256
verification and an idempotent import command through the image owner. Callers
provide a plain filename and MIME type, not a server path or generated base64.
Inspection returns native MCP image content. Oversized visual previews are
scaled to fit the result bound; saved canvas previews also return authenticated,
hash-checked full-size download resources. Original images, captured assets,
publication exports and actual artifacts use authenticated transfer endpoints.
The helper authenticates bounded local uploads/downloads with a current-workspace,
grant-specific connection file. Workspace instructions take
precedence over a skill's installation example when several grants are configured.
Downloads use a temporary file followed by a non-overwriting move. It reports length/hash and
never prints credentials. Composition and publication evidence rules still
require readback, clean/annotated inspection and actual artifact diagnostics.

### External-project transfer baselines

An external-project link is a stable source key and name associated with a
Lorekeeper project. Source-key mappings retain exact Lorekeeper identities.
They do not prescribe a game engine, file layout or serialization format.

`capture_transfer` fences the project and calls the canonical snapshot writer
against current saved creative state, including pending edits. It creates no
checkpoint, approval or push. Each capture retains its own creative files,
verified manifest, source mappings, previous acknowledged baseline and expected
link revision. Reading a comparison reports added, modified and removed files,
plus record-level area summaries from the existing canonical snapshot comparer.
These include stable node/edge identities, ordering metadata and image
associations. Exact paginated before/after file reads remain authoritative.
Binary entries expose manifest indexes, hashes and authenticated downloads.
Captured content survives subsequent live changes or deletion.

`acknowledge_transfer` advances only the captured baseline after successful
external handling. It is idempotent and checks the prior baseline and link
revision before replacing them. An older comparison cannot replace a newer
acknowledged baseline. Changes after capture remain outstanding. Workspaces,
grants, receipts, links, mappings, captures and baseline pointers remain local
operational state outside creative exports and Git snapshots.

## Key files and file families

| File or family | Role |
|---|---|
| `Lorekeeper/ExternalAccess/ExternalAccessRegistration.cs`, `ExternalAccessHttpGate.cs` | SDK registration, transport, middleware and security boundary. |
| `ExternalCapabilities.cs`, `McpClientGrantService.cs`, `ExternalAccessModels.cs` | Explicit capabilities, installation grants and operational persistence contracts. |
| `ExternalToolCatalog.cs`, `ExternalDomainTools.cs`, `ExternalSurfaceTools.cs`, `ExternalAdvancedTools.cs`, `ExternalTransferTools.cs` | Typed tool schemas and explicit operation/capability/surface routing. |
| `ExternalAccessDispatcher.cs`, `ExternalAccessCommands*.cs`, `ExternalProjectionRepairWorker.cs` | Authorized application queries/commands, atomic receipts, lifecycle and recovery. |
| `ExternalResources.cs` | Permission-filtered resources and exact-read templates. |
| `ExternalTransferStore.cs`, `ExternalTransferEndpoints.cs`, `ExternalTransferCommand.cs` | Retained snapshot captures and contained, authenticated binary delivery. |
| `ExternalClientSetupService.cs`, `AgentSkill/SKILL.md`, `Components/Pages/Settings/ExternalAccess.razor` | Guided client configuration, versioned skill installation and settings UI. |
| `ExternalWorkspaceUiEvents.cs` | Saved external-work notification without implicit UI selection. |

## Related chapters

See [persistence](persistence-migrations-import.md), [assistant context](assistants-chat.md),
[manuscripts](manuscript-authoring.md), [composition](composition-media.md),
[publishing](publishing-model.md), [providers](providers-background.md),
[version history](version-history-sync.md), and [runtime](runtime-host.md).

## Relevant verification

Build the solution and run the permitted data-safety tests on the final state.
Migration and atomic content/receipt preservation belong in that suite. Use real
MCP clients and authorized disposable application content for protocol, client
setup, skill discovery, role and provider acceptance; do not create a separate
general MCP test harness. Record exact clients, host modes, providers, artifacts,
round-trip captures, permission/revocation results and any unperformed checks.
Terminate every validation host, shell and helper started for acceptance, and
remove acceptance-only client configuration without changing unrelated entries.
