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
live database and not a working checkout. Remote attachment is explicit opt-in.
Once attached, each successful local checkpoint durably records one automatic
push intent per attachment; a startup-gated worker later processes those intents
without delaying or failing the local checkpoint. User-started fetch, checkout,
and manual push remain explicit transport actions. No path silently merges,
rebases, force-pushes, or resolves conflicts by discarding either history.

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
narrative/chapters/<chapter-id>/chapter.json
narrative/chapters/<chapter-id>/manuscript.json
graph/graph.json
sources/sources.json
assets/assets.json
assets/images/<image-id>/content.<extension>
assets/fonts/<family-id>/faces/<face-id>/content.<extension>
manuscript/styles.json
composition/composition.json
publication/publication.json
```

JSON is UTF-8 without a BOM, indented by the canonical serializer, and
recursively ordinal-sorted by property name. Arrays
are ordered by stable identity or the area's semantic key. Each chapter has one
stable lowercase `N`-format GUID directory: `chapter.json` contains its metadata
and manuscript revision, while `manuscript.json` contains the structured
manuscript as direct canonical JSON rather than an escaped JSON string. The
reader requires exactly both files for every chapter and rejects orphaned,
duplicated, or path/identity-mismatched chapter files. Other embedded properties
whose names end in `Json` are parsed, recursively canonicalized, and emitted as
canonical JSON strings; malformed non-empty values and embedded credential,
token, secret, password, API-key, or authorization-code properties fail closed.
Operational timestamps, warnings, diagnostics, provider IDs, fetch metadata,
and other operational fields are omitted. GUID path components use lowercase
`N` format. Writers emit the exact canonical schema-v3 file set; undeclared
files and noncanonical paths or encodings fail closed. Readers retain a bounded
schema-v1/v2 adapters for historical publication payloads, applying the same
for-sale, user-supplied ISBN, measured full-wrap, and top-to-bottom defaults as
pre-v27 project import.

The manifest records repository/project identity, schema and included areas,
the sorted path/length/SHA-256 list, a content hash over path/length/bytes, and
the manifest hash. Image and font metadata contains a relative blob path, hash,
and byte length. Their bytes are ordinary Git blobs in the same tree: version
history does not use Git LFS, an external asset store, or a database pointer
that is unavailable to a clone.

The legacy nullable `ProjectExportChapter.Body` property is compatibility data,
not canonical manuscript state. The structured `ManuscriptJson` and its
revision are authoritative; semantic chapter/full-snapshot comparisons and
dirty-state decisions ignore `Body`. New synthesized chapter checkpoints write
`Body` as null, and the Other-change synthesis path never carries a historical
legacy body forward. Raw manifest content hashes still remain in checkpoint
metadata and review concurrency tokens, so they continue to detect stale
operations even when the semantic comparison is identical.

The captured canonical areas are:

- `project`: project settings, page setup, contest-mode setting, and outgoing
  references;
- `narrative`: Book Brief and canonical-source selections, entity types, acts,
  per-chapter metadata and directly reviewable structured manuscript files,
  writing samples, editor context preferences, and manuscript annotations;
- `graph`: canonical graph nodes and edges, excluding structural and derived
  projection edges;
- `sources`: ingest sources and their source chunks, pages, and blocks, with
  fetch/job timestamps and provider diagnostics removed;
- `assets`: project images, entity visual examples, and imported font families;
- `manuscript`: project Book Text Styles in `manuscript/styles.json`;
- `composition`: page compositions, variants, and scene data; and
- `publication`: Core Book, editions, publication sections, print project use,
  identifier and cover-submission modes, and cover spine direction.

Chats, conversations, messages and composer drafts; provider connections,
OAuth tokens and credentials; the Review Edits workflow toggle, contests,
candidate drafts, revision jobs, ingest/image/publication jobs and other
operational staging; FTS5, sqlite-vec, context,
auto-link and other projections; visual candidates; render artifacts, page
maps, packages, audits and migration journals; process-lifetime Undo/Redo; and
other operational or derived state are deliberately excluded. Restore rebuilds
the derived projections through their owning services and leaves render
artifacts absent. Portable export v28 remains a separate boundary with its own
scope and warnings.

### Checkpoints and restore

The SQLite timeline stores immutable semantic metadata (kind, source, commit,
parent, manifest/content hashes, revision and message); Git stores the snapshot
tree. Initial, manual, assistant, imported, restored, and `ReviewApproval`
checkpoint kinds are used by the history and restore services. Repeated content
is deduplicated at the Git commit boundary, while the timeline can retain the
semantic operation record.

Review Edits is controlled from the project top bar beside Checkpoint, with a
Pending changes button and count beside it. The toggle is excluded from Git
snapshots. When enabled, direct manual and assistant mutations remain in live
SQLite and are compared as `HEAD → live`; when disabled, completed mutating
assistant turns checkpoint the complete live project. Existing dirty work is
included when the preference is enabled. Disabling it while differences are
pending presents an application confirmation; Proceed validates the displayed
review token, checkpoints the one captured complete live snapshot through a
`ReviewApproval`, and disables the preference under the same project operation,
while Cancel leaves Review Edits enabled. Successful manual manuscript, chapter,
and publication-section mutations invalidate the top-bar projection after their
durable save or transaction commit; true no-ops do not. Those invalidations
retain the last controls while a coalesced review refresh runs outside the
Blazor circuit so autosave never forces an Editor reload or top-bar flicker.

The Review service returns affected chapter/target summaries, semantic
manuscript groups, non-manuscript aggregates, dependency groups, contest state,
selected candidate draft, compared commit metadata, and concurrency tokens.
Review loading captures the live project at most once per request and uses the
snapshot writer's validated artifact directly; it must not reread the emitted
temporary tree merely to recover its hash or payload. Immutable approved Git
artifacts are reused through an application-level bounded cache keyed by
repository and commit SHA, while historical scans reuse adjacent commit
snapshots locally and retain a bounded `(HEAD SHA, chapter, target, maxCommits)`
result cache across short-lived dependency scopes. Live SQLite snapshots are
never cached. Initial checkpoint status, pending-target discovery, manuscript
Review projection, historical lookup, and Designed Page preview work starts
after the owning surface renders and runs in a fresh dependency scope outside
the Blazor circuit; cancellation and project/chapter/target generations prevent
stale results from reaching the UI. The top-bar pending result also supplies the
checkpoint control's live dirty status, so project entry does not export the
same live snapshot twice. These caches never replace an authoritative live
snapshot/token check for a mutation, and they are naturally replaced when HEAD
advances rather than becoming a live-state TTL cache. The shared checkpoint
control does not refresh full project status from hover/focus or same-project
chapter/mode/query navigation; history events and project changes remain
explicit refresh boundaries.
Chapter targets are created for manuscript or chapter-attached visual changes;
chapter metadata remains in the non-manuscript dependency groups so it can be
reviewed without pretending it is a text edit. Designed Page changes retain
their semantic composition payload and may render transient before/after
canvas previews on the owning chapter target.

The Editor pending-review inspector is a scoped, stateless read adapter over
that Review service. It is available only to normal Editor turns while Review
Edits is enabled and validates the persisted preference before and after each
review read. Its compact automatic context is protected and non-removable. The
`list_pending_review_changes` and `read_pending_review_diff` envelopes carry an
opaque revision derived from the repository, approved commit, and current live
review token; page/detail continuation arguments repeat that revision and stale
requests return `REVIEW_STALE`. Live snapshots are reread for every request;
there is no live-state TTL cache. Chapter targets retain exact Core/release
identity and outline order. Other entries remain associated with their atomic
dependency-group metadata. Semantic manuscript reads emit only bounded
Body/Structure/Visual hunks and rows, while entity/relationship/Other reads use
bounded comparer text. These read tools never approve, reject, or mutate state
and are not exposed to Contest Preparation or revision-worker turns.

Review context is evidence rather than mutation authority. After a mutation the
Editor reacquires the current list once and reads only affected targets as an
additional self-check; it keeps unrelated pending work separate and follows
the normal owning-service readback and visual-verification contracts.
Pending mode reviews the Git HEAD-to-live difference. A clean target uses Last
approved mode, comparing the newest affecting approved commit with its parent;
historical lookup is cached by `(HEAD SHA, chapter, target, maxCommits)` so a
broader or narrower bounded history request cannot reuse an incomplete scan.
Historical Undo restores the parent value into live state and therefore creates
a normal pending reversal. Chapter-owned Designed Page restores validate and
repair coupled manuscript references atomically, failing closed when target
isolation or dependency ownership is ambiguous. Partial approval commits only
selected live semantic groups through a `ReviewApproval` checkpoint. Keep All
on a chapter Review commits that target's complete semantic manuscript and
chapter-owned Designed Pages while preserving every other chapter, target, and
Other change as pending. A project-wide approval checkpoints the complete live
snapshot.

The History workspace provides the message-bearing checkpoint form. Its timeline
can compare two checkpoints in chronological order with bounded, readable before/after panes derived
from semantic manuscript content; raw semantic JSON and binary assets are never
rendered. Failed operation notices remain durable journal rows for reconciliation,
but the user can acknowledge and clear them from the sidebar without deleting
their error or recovery data. Opening Restore focuses the controlled-restore card,
where whole-project, major-area, and selected-chapter scopes are explicit. The shared
layout also renders `ProjectCheckpointControl` beside the theme control: it
resolves project routes, ensures the local repository exists, and creates a
manual checkpoint with the fixed semantic message `Checkpoint current work`.
It remains visible but disabled outside a
project, while any project-history operation is active, for a clean initialized
project, or when repository health is unavailable; an uninitialized project can
create its first checkpoint. It refreshes from explicit history events and
project-route changes rather than pointer hover or same-project chapter/mode
navigation. Process-local, project-scoped operation leases keep
the independently rendered workspace and layout control synchronized. Checkpoint
refreshes that arrive during restore or synchronization run after the owning
operation becomes idle. Unapproved live work remains local and blocks push or
checkout; checkpoint failures remain durable and leave the project dirty and
reviewable.

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

Fetching a remote is a network action and updates only the attachment-specific
remote-tracking ref; cached status and local reads do not contact GitHub. A
missing/deleted branch or empty repository clears that active ref, so status
returns to attached/unknown rather than retaining an old synchronized tip.
Applying a fetched remote
requires a clean project, creates a deduplicated safety checkpoint, and uses
fast-forward-only compare-and-swap before exact-head checkout. Local `main` is
advanced only when the remote tip is a descendant. The final clean-state check,
ref update, cache advancement, and SQLite checkout share one project mutation
lease so an editor/checkpoint cannot interleave them. Pushing likewise requires a
clean project and an existing local checkpoint; it permits only a non-force
fast-forward refspec when the remote is empty or an ancestor of local `main`.
Remote checkout preflight retains both the semantic cleanliness result and the
fresh live raw content hash from its initial status. It uses the raw live hash
only to detect a stale workspace between those serialized checks, while Git
head commit/content hashes remain the repository compare-and-swap tokens; a
legacy `Body`-only difference therefore cannot block a semantically clean
checkout or weaken mutation protection.
Empty GitHub repositories use `main` for their first push. Manual and automatic
pushes share this transport policy, while automatic attempts use their existing
durable operation row and remain retryable after network or history failures.
Lorekeeper does not treat libgit2 transport completion as remote success: ref-level
push errors fail the operation, a second authenticated fetch must observe the
exact requested commit, and GitHub's API must independently report that same
branch head before the operation can become synchronized or succeeded.
The OAuth token remains inside the GitHub provider boundary and reaches the
embedded libgit2 HTTPS transport through both its credential callback and a
repository-bound preemptive in-memory authorization header; no system Git
executable is invoked. A missing configured branch in a non-empty repository
fails closed rather than being recreated automatically. A queued automatic push
superseded by a newer local checkpoint is canceled as non-resumable; only the
intent whose target was actually observed on GitHub can succeed.

Diverged and unrelated histories remain visible with their tracking refs and
are not merged, rebased, force-pushed, overwritten, or silently discarded.
Removing a local remote attachment preserves local history and tracking refs;
it removes only the project attachment and Git remote configuration.

`ProjectVersionOperation` is also the automatic-push durable boundary. Its
nullable remote-attachment and exact target-commit fields deduplicate intents;
the process-local queue only wakes a startup-gated hosted worker. The worker
creates fresh scopes, scans pending/interrupted rows, and holds the project
mutation lease across dirty-state checks, fetch, compare, push, and
tracking-ref updates. It never retains scoped database contexts across network
calls. Startup reconciliation also creates a missing intent for the current head
of each existing attachment, so upgrading an already attached project does not
require an extra checkpoint before its first automatic delivery.

GitHub account setup uses device authorization. The distributable ships the
public client ID for Lorekeeper's maintainer-owned OAuth app. Forks and custom
deployments can replace it through `VersionHistory:GitHub:ClientId` (or the
standard environment override `VersionHistory__GitHub__ClientId`). Access
tokens remain in the provider-owned `GitHubConnection` row and are never written
into manifests, snapshots, remote metadata, logs, or UI payloads. Repository
listing, creation, fetch, manual push, and device authorization happen only after
the user explicitly starts the corresponding action. Automatic push begins only
after explicit attachment and a successful local checkpoint. Attaching a remote requires an
explicit acknowledgement that creative material may be uploaded. After attachment,
the consent and repository-selection controls collapse into the attached remote
status and actions; removing the remote makes setup available again.
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

Snapshot schema v6 removes the duplicate cover-owned back-copy value and stores
cover layouts against the effective publication Description. The v1-v5 reader
rewrites retired bindings in primary and exact-surface cover scenes while keeping
the visible saved Description authoritative. Schema v5 introduced image parent
links and upscale provenance alongside the canonical asset bytes. Restore validates and remaps those links before
creating rows, adapts older print-resample metadata to the current Upscaled source,
and fails closed when the current schema references an absent ancestor. Generated
publication artifacts remain excluded, but permanent image derivatives and the
authored reference replacements are creative project state and therefore belong
in deterministic checkpoints, comparisons, restore, and clone import.

## Key files and file families

| Path or family | Architectural role |
|---|---|
| `Lorekeeper/VersionHistory/Snapshots/` | Schema-v1-through-v6 payloads, canonical JSON, deterministic writer, strict reader, compatibility adapters, and manifest/blob validation. |
| `Lorekeeper/VersionHistory/Git/` | Bare-repository paths, Git object/ref operations, history relation, and safe deletion staging. |
| `Lorekeeper/VersionHistory/Services/` | Checkpoint timeline, Git HEAD/live dirty-state reconciliation, pending and historical Review modes, operation journal, and assistant checkpoint adapter. |
| `Lorekeeper/VersionHistory/Compare/` | Pure semantic area summaries, bounded readable before/after text, detailed entries, and restore-selection contract. |
| `Lorekeeper/VersionHistory/Restore/` | Whole/selective restore, clone import application, exact-head checkout, dependency validation, and projection repair. |
| `Lorekeeper/VersionHistory/Sync/` | GitHub remote attachment, fetch/fast-forward/push policy, remote checkout coordination, and clone transport. |
| `Lorekeeper/VersionHistory/GitHub/` | Device authorization, GitHub API transport, connection validation, and non-secret remote views. |
| `Lorekeeper/Models/ProjectVersion*.cs`, `ProjectGitRemote.cs`, `GitHubConnection.cs` | SQLite identity, checkpoint, operation, remote metadata, and provider credential rows. |
| `Lorekeeper/Components/Pages/Projects/History/`, `Components/Pages/Projects/EditorContent.razor`, `Components/Layout/ProjectCheckpointControl.razor`, `Components/Layout/ProjectReviewControls.razor`, and `Settings/VersionControl.razor` | Checkpoint/compare/restore UI, chapter Review modes, top-bar Review Edits/Pending controls, explicit sync controls, rights acknowledgement, and GitHub account setup. |

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
