# Version history and synchronization

## When to read

Read this chapter when a change touches the local Git repository, deterministic
project snapshots, checkpoint timelines, semantic comparison, restore/import,
remote synchronization, GitHub account or remote attachment, repository clone,
project-history deletion, or the History and Version control surfaces. Read it
with the owning domain chapter for every captured area and with
[`persistence-migrations-import.md`](persistence-migrations-import.md) for
SQLite operations, migrations, and portable project export.

## Scope and ownership

The `Lorekeeper/VersionHistory/` family owns the versioned-project boundary:
the local bare Git repository, snapshot schema and deterministic serialization,
checkpoint metadata, comparison, restore, remote transport, GitHub connection
transport, and clone import. `ProjectVersionHistoryService` coordinates a
validated snapshot with the local Git store and records the SQLite checkpoint
row. `ProjectVersionRestoreService` owns validated whole or selective
application to the live project. `VersionHistorySnapshotComparer` is pure and
returns UI-ready semantic differences; it never reads Git or SQLite.

SQLite and the current domain models remain the live project authority. Git is a
durable, reviewable history of selected canonical creative state, not a second
live database and not a working checkout. Remote synchronization is an explicit
transport action; it does not silently merge, rebase, force-push, or resolve
conflicts by discarding either history.

## Current architecture and invariants

### Local repository and storage

Lorekeeper creates one app-managed bare repository per
`ProjectVersionRepository.Id`. The repository contains `refs/heads/main` and
Git objects only; the application does not expose a checked-out working tree.
The repository directory is named with the lowercase, hyphen-free repository
GUID:

| Runtime | Default local history root |
|---|---|
| Development | `<process base directory>/History/<repository-id>.git` (the repository-local `History/` directory during normal development; it is ignored by the source repository) |
| Packaged | `%LocalAppData%/Lorekeeper/History/<repository-id>.git` |

`VersionHistory:HistoryRoot` can explicitly override the root. The Git store
validates that repository paths stay inside that root and rejects reparse-point
or path-traversal escapes. Project deletion stages and removes only the
app-managed local repository after the database delete succeeds; a failure can
be rolled back from its local deletion tombstone. Deleting local history never
deletes or changes a GitHub repository.

### Snapshot contract

Schema v1 uses format ID `lorekeeper.version-history-snapshot`. Every checkpoint
tree has this stable layout:

```text
manifest.json
project/project.json
narrative/narrative.json
graph/graph.json
sources/sources.json
assets/assets.json
assets/images/<image-id>/content.<extension>
assets/fonts/<family-id>/faces/<face-id>/content.<extension>
manuscript/manuscript.json
composition/composition.json
publication/publication.json
```

JSON is UTF-8 without a BOM, indented by the canonical serializer, and
recursively ordinal-sorted by property name. Arrays
are ordered by stable identity or the area's semantic key. Embedded properties
whose names end in `Json` are parsed, recursively canonicalized, and emitted as
canonical JSON strings; malformed non-empty values and embedded credential,
token, secret, password, API-key, or authorization-code properties fail closed.
Operational timestamps, warnings, diagnostics, provider IDs, fetch metadata,
and other operational fields are omitted. GUID path components use lowercase
`N` format. Readers require the exact canonical JSON bytes and exact schema-v1
file set; undeclared files and noncanonical paths or encodings fail closed.

The manifest records repository/project identity, schema and included areas,
the sorted path/length/SHA-256 list, a content hash over path/length/bytes, and
the manifest hash. Image and font metadata contains a relative blob path, hash,
and byte length. Their bytes are ordinary Git blobs in the same tree: version
history does not use Git LFS, an external asset store, or a database pointer
that is unavailable to a clone.

The captured canonical areas are:

- `project`: project settings, page setup, contest-mode setting, and outgoing
  references;
- `narrative`: Book Brief and canonical-source selections, entity types, acts,
  chapters, writing samples, editor context preferences, and manuscript
  annotations;
- `graph`: canonical graph nodes and edges, excluding structural and derived
  projection edges;
- `sources`: ingest sources and their source chunks, pages, and blocks, with
  fetch/job timestamps and provider diagnostics removed;
- `assets`: project images, entity visual examples, and imported font families;
- `manuscript`: project Book Text Styles;
- `composition`: page compositions, variants, and scene data; and
- `publication`: Core Book, editions, and publication sections.

Chats, conversations, messages and composer drafts; provider connections,
OAuth tokens and credentials; AI changes, review baselines, contests, revision
jobs, ingest/image/publication jobs and staging; FTS5, sqlite-vec, context,
auto-link and other projections; visual candidates; render artifacts, page
maps, packages, audits and migration journals; process-lifetime Undo/Redo; and
other operational or derived state are deliberately excluded. Restore rebuilds
the derived projections through their owning services and leaves render
artifacts absent. Portable export v24 remains a separate boundary with its own
scope and warnings.

### Checkpoints and restore

The SQLite timeline stores immutable semantic metadata (kind, source, commit,
parent, manifest/content hashes, revision and message); Git stores the snapshot
tree. Initial, manual, assistant, imported, and restored checkpoint kinds are
used by the history and restore services. Repeated content is deduplicated at
the Git commit boundary, while the timeline can retain the semantic operation
record.

The History workspace provides the message-bearing checkpoint form. The shared
layout also renders `ProjectCheckpointControl` beside the theme control: it
resolves project routes, ensures the local repository exists, refreshes dirty
state when approached, and creates a manual checkpoint with the fixed semantic
message `Checkpoint current work`. It remains visible but disabled outside a
project, while busy, for a clean initialized project, or when repository health
is unavailable; an uninitialized project can create its first checkpoint.

Restore validates manifest identity, every file hash and blob length, the full
referential graph, required assets/styles/compositions/publication rows, and
repository/project identity before acquiring mutation state. It creates a
pre-restore safety checkpoint, then applies changes under the project mutation
lease and one database transaction. Whole-project restore replaces all captured
areas. Major-area restore replaces only selected areas from `project`,
`narrative`, `graph`, `sources`, `assets`, `manuscript`, `composition`, and
`publication`. Selected-chapter restore replaces/adds/removes only the chosen
stable chapter IDs; missing required acts are added only when safe. Annotation
handling is explicit: whole/narrative restore includes it, while selected
chapters can include selected-chapter annotations or exclude them.

Unresolved outgoing references are preserved as unresolved restore output and
are never guessed from a slug or name. A restore refuses queued/running work or
unsafe cross-area dependencies and clears stale operational rows that could
point at replaced canonical rows. After commit it attempts outline/ingest graph,
FTS/context, auto-link, and vector freshness repair through existing contracts,
then records a `Restored` checkpoint. Work after the database commit completes
with non-cancelable cleanup semantics; projection failures are returned as
warnings rather than leaving a caller with a falsely canceled half-result.

The exact-head checkout path is for synchronization: after transport has
advanced local `main`, it validates the supplied commit/tree and current cached
head, applies the snapshot to an existing project without creating another Git
commit, records remote/imported checkpoint metadata when needed, and rebuilds
projections. It fails closed on a head mismatch.

### Remote synchronization

Fetching a remote is a network action and updates only the remote-tracking ref;
cached status and local reads do not contact GitHub. Applying a fetched remote
requires a clean project, creates a deduplicated safety checkpoint, and uses
fast-forward-only compare-and-swap before exact-head checkout. Local `main` is
advanced only when the remote tip is a descendant. The final clean-state check,
ref update, cache advancement, and SQLite checkout share one project mutation
lease so an editor/checkpoint cannot interleave them. Pushing likewise requires a
clean project and an existing local checkpoint; it permits only a non-force
fast-forward refspec when the remote is empty or an ancestor of local `main`.

Diverged and unrelated histories remain visible with their tracking refs and
are not merged, rebased, force-pushed, overwritten, or silently discarded.
Removing a local remote attachment preserves local history and tracking refs;
it removes only the project attachment and Git remote configuration.

GitHub account setup uses device authorization. The OAuth app client ID is
configured as `VersionHistory:GitHub:ClientId` (or the standard environment
override `VersionHistory__GitHub__ClientId`). Access tokens remain in the
provider-owned `GitHubConnection` row and are never written into manifests,
snapshots, remote metadata, logs, or UI payloads. Repository listing, creation,
fetch, push, and device authorization happen only after the user explicitly
starts the corresponding action; selecting a remote also requires an explicit
acknowledgement that creative material may be uploaded.
Credentialed Git transport accepts only absolute `https://github.com/...` clone
URLs matching the selected owner and repository; alternate hosts fail closed.

### Clone import and identity

A validated clone reads the selected branch's head tree, validates the manifest,
all paths, hashes, identities, and dependencies, and only then installs the
bare repository and atomically creates the local `Project` and
`ProjectVersionRepository` from the manifest identities. Project ID,
repository ID, and slug collisions fail closed. The imported head/checkpoint is
recorded and the selected GitHub remote can be attached as `origin`. If import
or attachment fails, only a repository installed by that attempt and not yet
adopted by matching database identity may be removed.

`ProjectReference` stores the referenced repository ID and project ID as
persistent identity, with nullable local resolution. A local target is linked
only when both identities match; a same project GUID in another repository is
not treated as self-reference. Missing targets remain represented with their
stable identity and cached name/slug, never inferred from a slug. Nullable local
resolution state is operational and is not serialized into a snapshot.
The forward identifier-normalization migration preserves EF Core SQLite Guid
lookups for repository and reference identities created by SQL backfill during
the cutover.

## Key files and file families

| Path or family | Architectural role |
|---|---|
| `Lorekeeper/VersionHistory/Snapshots/` | Schema-v1 payloads, canonical JSON, deterministic writer, strict reader, and manifest/blob validation. |
| `Lorekeeper/VersionHistory/Git/` | Bare-repository paths, Git object/ref operations, history relation, and safe deletion staging. |
| `Lorekeeper/VersionHistory/Services/` | Checkpoint timeline, dirty-state reconciliation, operation journal, and assistant checkpoint adapter. |
| `Lorekeeper/VersionHistory/Compare/` | Pure semantic area summaries, detailed entries, and restore-selection contract. |
| `Lorekeeper/VersionHistory/Restore/` | Whole/selective restore, clone import application, exact-head checkout, dependency validation, and projection repair. |
| `Lorekeeper/VersionHistory/Sync/` | GitHub remote attachment, fetch/fast-forward/push policy, remote checkout coordination, and clone transport. |
| `Lorekeeper/VersionHistory/GitHub/` | Device authorization, GitHub API transport, connection validation, and non-secret remote views. |
| `Lorekeeper/Models/ProjectVersion*.cs`, `ProjectGitRemote.cs`, `GitHubConnection.cs` | SQLite identity, checkpoint, operation, remote metadata, and provider credential rows. |
| `Lorekeeper/Components/Pages/Projects/History/`, `Components/Layout/ProjectCheckpointControl.razor`, and `Settings/VersionControl.razor` | Checkpoint/compare/restore UI, global project checkpoint shortcut, explicit sync controls, rights acknowledgement, and GitHub account setup. |

## Related chapters

- [`persistence-migrations-import.md`](persistence-migrations-import.md) owns
  SQLite authority, operation lock order, migrations, recovery, and portable
  import/export.
- [`narrative-context.md`](narrative-context.md) owns outline, graph, ingest,
  references, and retrieval projections that restore repairs.
- [`manuscript-authoring.md`](manuscript-authoring.md) owns manuscript v4,
  styles, annotations, and process-lifetime Undo/Redo.
- [`composition-media.md`](composition-media.md) owns images, fonts,
  compositions, and scene semantics captured by snapshots.
- [`publishing-model.md`](publishing-model.md) owns Core/release publication
  state; [`press-production.md`](press-production.md) owns generated artifacts
  that snapshots intentionally omit.
- [`assistants-chat.md`](assistants-chat.md) owns chat state and assistant turn
  lifecycle; chat state is not versioned creative snapshot data.
- [`providers-background.md`](providers-background.md) owns OAuth credential
  storage and background workers; [`runtime-host.md`](runtime-host.md) owns
  configuration, DI, startup, and local-data placement.
- [`validation-documentation.md`](validation-documentation.md) owns evidence
  and documentation claim boundaries.

## Relevant verification

Snapshot changes require deterministic serialization and reader/writer hash,
path-containment, malformed-JSON, secret-rejection, blob-integrity, restore
referential-integrity, and approved versioned-transformation preservation
checks. Sync changes additionally require explicit clean-workspace,
fast-forward-only, divergence-preservation, credential-redaction, cancellation,
and exact-head checkout evidence. A build or local static inspection is not
evidence that GitHub OAuth, GitHub network transport, a real remote repository,
packaged paths, or manual History UI behavior works; those claims require the
corresponding authorized exercise described by the validation chapter.
