# Persistence, migrations, and import architecture

## When to read

Read this chapter when a change touches SQLite, `AppDbContext`, EF migrations,
repositories, write leases, revision tokens, project mutation coordination,
backup/recovery, startup migration order, import/export, durable jobs,
artifact bytes or hashes, local data placement, credentials/tokens, or the
authorized application-test boundaries. Read it with
`publishing-model.md` or `press-production.md` for publication rows, render and
package freshness, and native artifact semantics. Read it with
`manuscript-authoring.md` for manuscript/schema/history data, and with
`composition-media.md` for image/font/composition ownership and migration
relationships. Read it with [`version-history-sync.md`](version-history-sync.md)
for the separate deterministic Git snapshot, checkpoint, restore, and remote
transport boundary.

This chapter is the persistence and data-safety authority. Runtime services
must use its operation and repository boundaries rather than circuit-scoped
EF state, ad hoc SQL writers, or direct database paths. Historical migrations
are immutable schema history; current runtime compatibility belongs in forward
migrations and guarded transformation services, not in retained obsolete
runtime branches.

The frequently used browser-development database is `Lorekeeper/lorekeeper.db`
relative to the repository root (for example,
`C:\Users\jonth\Source\repos\Lorekeeper\Lorekeeper\lorekeeper.db` in the
primary development checkout). It contains the Falanaras project and other
developer working data. Migration validation must use a copied database and
must never mutate this developer-owned original.

New Contest batches store a versioned immutable task/target/context envelope in
the existing `ContestBatch.ContextSnapshotJson` field. The envelope records the
coordinator-established prose task, ordered stable block IDs, expected source
revision, and captured evidence without requiring a schema migration. Older bare
context snapshots remain valid historical audit payloads; unresolved legacy rows
are never reinterpreted as new prose-runner input after restart.

## Scope and ownership

`AppDbContext` owns the EF model and relationships for projects, Book Briefs,
outline, graph, manuscripts, annotations, page setup, styles, fonts, images,
chats, ingest/import, publishing, composition, jobs, and audit state.
`IAppDatabaseOperationFactory` and `AppDatabaseOperations` own short
no-tracking read and tracking write lifetimes. `DatabaseRepositories` owns the
operation-scoped repository bundle; repositories stage changes but never
commit. `ProjectMutationCoordinator` owns project-scoped serialization for
manuscript-reference writes and asset/style deletion integrity.

`IDatabaseMigrationRecoveryService` owns provider-specific backup paths,
owner-only permissions, SQLite Online Backup creation, restore confirmations,
scheduled restore markers, recovery-shell creation, backup discovery, and
protected pruning. `IDatabaseStartupMigrationService` is the one ordered
schema/data migration orchestrator used by the real host and migration-safety
fixtures. It applies scheduled restores and recovery markers before ordinary
EF operations or background workers begin.

`ManuscriptMigrationService`, `VisualCompositionMigrationService`,
`AuthoringPageMigrationService`, `PublicationEditionMigrationService`,
`PublicationCoreMigrationService`, `PublicationPressMigrationService`,
`PrintArtifactProfileMigrationService`, `PublicationSectionMigrationService`, and
other feature migration owners each own their guarded transformation contract.
They create protected backups, validate pre/post invariants, journal progress,
and delegate failure to the shared recovery service. Applied EF migration files
remain immutable; these owners do not make historical files into runtime
compatibility code.

`IProjectImportExportService` and `ProjectImportExportService` own the portable
project document boundary. `ProjectImportJobProcessor` owns transactional
application of a validated versioned document, ID remapping, reference/source
scope rules, report rows, and post-commit index refresh. Import jobs and other
background work use durable records, queues, and workers; a Blazor circuit or
renderer process never owns their lifetime.

Version history is a separate transformation boundary. SQLite remains the live
authority; `VersionHistorySnapshotWriter` emits only the deterministic canonical
creative areas documented in [`version-history-sync.md`](version-history-sync.md)
to an app-managed bare Git repository. It never serializes the SQLite file,
provider credentials, chats, jobs, derived projections, or generated
publication artifacts. Restore/import uses the same project mutation lease,
database write lease, and transaction ordering as other guarded transformations
and repairs projections only after canonical rows commit.

The `ProjectVersionOperation` journal is also the durable boundary for automatic
checkpoint pushes. A successful checkpoint inserts deduplicated remote/target
intent rows in the same database write transaction. A startup-gated hosted
worker reclaims pending or interrupted intents through fresh scopes; its
process-local queue is only a wake-up and never replaces the journal or a
database migration.

Credential/API-key rows, OpenAI account/token rows, and provider configuration
remain within the provider persistence boundary. `OpenAiAccount` owns OpenAI
OAuth tokens; account-backed `LlmProvider` rows retain their stable IDs for
conversation and job selection. The account-ownership migration re-parents the
legacy root and direct credential-sharing children atomically, preserves token
rows and duplicate model IDs, and fails closed on ambiguous legacy ownership.
Account-backed model rows persist catalog/manual origin, while bundled effort,
capability, and usable-budget metadata remains versioned code-owned catalog data.
The static-catalog migration removes the earlier availability, refresh-error, and
advertised-context columns; account model lists are not discovered at runtime.
`OpenAiAccount.ExternalAccountId` is the persisted request identity populated only
after a successful authorization exchange. `RequiresReauthenticationAt` and the
sanitized last-authentication error distinguish terminal refresh rejection or a
migrated credential lacking that identity from transient refresh failure. A
reconnect does not overwrite the current `OAuthToken` row until its single-use
flow commits; terminal refresh failures retain the row, while explicit
disconnect removes it.
The local database is not an
operating-system credential vault or an encryption-at-rest claim. Secrets,
authorization codes, tokens, and sensitive payloads must never be copied into
unrelated feature entities, project exports, assistant tool payloads, or logs.

### Accepted M3-M4 persistence contract

This accepted next-contract guidance governs implementation, not current schema
claims. Authoring sessions and batch receipts are durable rows. One SQLite
transaction validates a multi-target batch, applies all targets, advances its
session sequence, and inserts the request-hash receipt; same identity returns
the receipt only when the hash matches. Receipts remain until client
reconciliation acknowledgment. Checkpoint, restore/sync, publishing, assistant,
and similar consumers invoke the authoring mutation fence rather than reading
through a dirty registered client.

New portable output is streamed `.lorekeeper`; legacy JSON v1-v31 is
import-only. Capture freezes canonical project mutation, reads bounded
no-tracking descriptors, and releases the lease before compression or transport.
Import validates paths, duplicate entries, links/reparse points, compressed and
expanded limits, hashes, JSON/XML limits, and reference closure before one
transactional application. Job state is Uploaded, Staged, Validated, Applying,
Committed, Indexing, Completed, CompletedWithWarnings, Failed, or Cancelled.
Cancellation ends at Applying; a committed import cannot later be failed or
cancelled, while indexing may be retried and add warnings.

## Current architecture and invariants

The application stores local state in SQLite through `AppDbContext`, including
provider/OAuth configuration, projects, Book Briefs, outline/graph state,
transcripts, review/contest state, writing samples, ingest/import jobs, images,
masks, entity visual links, fonts, publication state, composition, and binary
assets. Image-generation partials are job-owned binary artifacts until
explicit promotion moves one into the ordinary project image library; SQLite
deletes unpromoted partials associated with a final image when that image is
deleted. Job history is disposable operational state and follows a
terminal-sibling purge on next use: creating an image-generation, ingest,
editor-revision, import, or publication-preparation job first deletes terminal
sibling jobs for the same project/edition, with their child rows removed by
cascade, and startup recovery sweeps unpromoted partials left by terminal jobs.
The applied `PurgeTerminalJobsWithChildren` migration removed orphaned child
rows once; migrations are immutable applied history, not active code paths.
SQLite startup uses a busy
timeout and WAL journal mode. sqlite-vec and
internal FTS5 structures are initialized outside ordinary EF migrations and are
regenerable indexes, not authoritative project data.

Runtime code receives `IAppDatabaseOperationFactory`, never a circuit-scoped
`AppDbContext`. `OpenReadAsync` creates a no-tracking context for one database
block; callers fully materialize entities/DTOs before disposal. `OpenWriteAsync`
acquires the singleton process-wide write lease and creates a short tracking
context whose operation alone owns `SaveChanges` and disposal. Project-scoped
writes acquire the project mutation lease first. The required lock order is:

1. project mutation lease;
2. database write lease;
3. SQLite transaction.

Nested helpers borrow the active operation instead of opening another writer.
Raw FTS5 and sqlite-vec mutations use the same write coordinator. No EF context
remains alive across model streaming, provider/network calls, render waits,
retry delays, or background polling. Repositories attach only intended root
entries for detached mutations; they do not attach detached navigation graphs.

Application-managed revision tokens are the concurrency contract. Explicit
revision predicates treat zero affected rows as conflicts, while EF concurrency
misses identify stale work without graph-wide merging. Interactive editors
reread and adopt the authoritative snapshot; out-of-order autosaves are
ignored rather than replacing the editing surface with an error. Revision-checked
assistant applies, guarded imports, and atomic server workflows reject stale
mutations before partial writes. The only bounded ordinary save retry is for
transient SQLite locks. Protected migrations may intentionally retain a
tracking context across explicit backup/schema/validation phases, and guarded
imports may share one transaction, but neither leaks tracker state after
rollback.

Every guarded migration creates a protected SQLite Online Backup, performs
integrity checks, transforms data transactionally, validates row counts,
foreign keys, hashes, ownership, artifacts, packages, and unaffected data, then
journals completion. Failures preserve the original backup and open a
projectless recovery shell. A scheduled restore is applied during the next
startup before normal migrations/workers. Recovery UI exposes only redacted
state and requires a short-lived explicit confirmation. Backup pruning never
removes a backup referenced by a running/failed journal, scheduled restore,
active recovery state, or protected transform.

Recovery-shell schema replay removes temporary startup compatibility columns
from the restored working copy before their owning EF migrations run, including
Review Edits, section start/order, recto chapter starts, and print artifact
profiles. This uses the same guarded removal helpers as normal startup and
leaves the protected source backup unchanged, so duplicate-column errors do not
replace the original migration failure or prevent access to Data Recovery.
Startup also provisions print-setting compatibility columns after the Press
schema boundary creates publication tables on fresh or sufficiently old
databases, before the composition migration reads them with the current model.
The composition geometry repair can remain pending after later schema upgrades.
Its edition read supplies defaults only for absent Core columns, retaining every
stored edition field, and its cover read omits retired cover copy.
These reads support both historical and current schemas without duplicating
column names or changing stored release overrides and cover content.
The Core Book data transform also supports schema cleanup that completed before
its journal: once release typography is removed, it reads the project page setup
that now owns typography. After default-release cleanup it uses the existing
deterministic release ordering instead of querying the retired default flag.
Missing page setup fails closed; projection checks still verify that release
content and effective settings survive the deferred transform.

The guarded startup order matters. The migration owner targets a historical EF
schema only when that schema migration is pending; a later unrelated EF
migration must not downgrade current application tables. Publication/Press
schema advancement shares a database-scoped process-local and crash-releasing
cross-process lease with the edition recovery handoff, preventing two startups
from entering the known SQLite table-rebuild/history-write window together.
Expected publication tables must exist before a cutover proceeds. Malformed
markers are quarantined: pending work restarts from a fresh protected snapshot,
while an already-applied cutover restores the newest protected source backup in
the projectless recovery shell.

The current persisted manuscript document is v5. Older v1-v4 documents and
historical/review payloads are upgraded only at guarded startup or isolated
versioned import boundaries. `ManuscriptDocument.CurrentSchemaVersion` and
`docs/schemas/manuscript-v5.schema.json` are the current-format authorities;
names such as `SchemaV3MigrationName` or “manuscript-v3 import” identify
historical transforms and must not be renamed merely to make prose look
current. Malformed current manuscripts, structurally invalid documents, and
malformed non-result audit payloads fail closed into protected recovery. Legacy
plain text is permitted only in its documented historical audit boundary and
is preserved byte-for-byte.

The manuscript migration preflight validates contest source snapshots
independently of the retired `AcceptedManuscriptJson` column, which is absent
from the current schema. Non-empty candidate proposals and drafts must be
structured v1-v5 documents; failed, invalid, pending, and running candidates
may legitimately retain empty proposal/draft fields, while completed and
selected candidates require a proposal. Legacy v1 discovery and upgrade also
includes `DraftManuscriptJson` when that optional column exists. Empty candidate
proposals remain empty during legacy transforms so an unsuccessful historical
runner is not rewritten as an empty manuscript.

The structured-manuscript cutover validates legacy paragraph counts, bounds,
anchor hashes, projection equality, normalized-text hashes, and visual anchor
mapping before commit. Legacy illustrated-prose layouts use runtime-authoritative
paragraph indices; stale advisory hashes may be recorded, but malformed hashes,
invalid indices, ambiguous mappings, and projection mismatches fail closed.
The Review Edits transition is an ordered, protected forward migration. It
renames the project workflow column to `ReviewEditsEnabled`, adds contest source
revision/hash, selected-candidate, and candidate-draft fields, and then
materializes any unresolved legacy `AiChange`/`AiChangeBatch` effective draft
into the owning live state in dependency order. Only after identities,
revisions, hashes, ownership, and relationships validate does cleanup remove
the obsolete staging/baseline tables and runtime contracts. The old names are
accepted only inside this guarded migration boundary; they are not current
workflow state. An unresolved legacy contest is converted to isolated durable
candidate drafts, its live target is restored to the captured original when
needed, and the project-wide Editor lock is enabled. Missing or conflicting
originals/drafts fail closed into the existing recovery boundary with the
protected backup retained. `DetachedAt` remains current schema; startup removes
history-only detached composition remnants because process-memory Undo/Redo is
necessarily empty after launch.

The composition, authoring-page, Core Book, edition-content, print-artifact-profile,
and publication-section cutovers each preserve semantic IDs, assets, foreign
keys, artifacts, hashes, packages, audits, and unaffected rows while removing
obsolete runtime columns/contracts only after validation. Existing artifacts
retain bytes/hashes and are marked Legacy when a new source/provenance model
means regeneration is required.

The configurable chapter-start migration adds default-false columns to Core
Books and releases. Because historical owner migrations rebuild those tables
through immutable models, startup supplies temporary compatibility columns at
each such boundary, removes them immediately before the current EF boundary,
and then lets the forward migration own the durable columns.

Project export v31 is the final JSON portable writer. It retains the durable
print registry/profile fields, removes finish, and accepts v20-v26 legacy
`printRegistryVersion`, `printProductKey`, and ignored `printFinish` fields only
through the import adapter. Full exports include the
current v5 manuscript model, Core/release annotations, selected canonical
ingest-source bodies/evidence and mappings, page setup, independent Designed
Pages with Core/release content and authored variants,
Core Book including its paginated chapter-start policy, sparse release overlays
including an explicit policy override when present, edition snapshots,
publication sections, cover surfaces, Book Text Styles, visual references, and
project-owned font families/faces with binary hashes. Version 26 adds print
project use, identifier mode, cover submission mode, provider-neutral template
evidence, and spine direction. Pre-v26 imports preserve existing behavior with
for-sale, user-supplied ISBN, measured full-wrap, and top-to-bottom defaults.
Version 30 removes the cover-owned back-copy value and makes the visible effective
publication Description the sole descriptive-cover source. Current exports write
only `description`/`{{description}}` bindings. The v1-v29 adapter rewrites the
retired binding in primary and exact-surface scenes and discards its hidden value;
it never overwrites the imported Book or release Description. Version 31
replaces chapter/section-owned page compositions with reusable project pages;
versions 1-30 are adapted only while importing. Import validates
current token names and rejects unknown `{{token}}` references while continuing
to accept exact bare canonical bindings as shorthand.
Non-structural exports omit source bodies,
selections, and evidence and include a warning. Jobs, operational review rows, temporary
visual candidates, unselected source bodies/provenance, assistant transcripts,
model selections, unpromoted image partials, and other operational review state
remain working-database data and are excluded. The current export writes only
`ReviewEditsEnabled`; a versioned legacy import may read the old preference
field at its input boundary, but current exports never write that alias. Manual
Undo/Redo is process memory only and therefore is also absent from every export
without adding database rows.

`PublicationInteriorPagination` caches a derived interior page count together
with its pagination fingerprint, Press renderer version, and producing profile
ID. This snapshot exists only to give print-cover geometry an authoritative spine
before a full interior artifact is prepared. It does not advance the edition
revision and is reused while the renderer and pagination-affecting interior state
still match; the profile ID is retained as provenance rather than a cache key.
It remains operational state:
project export/import and version-history snapshots omit it and regenerate it
when cover editing is next attempted.

Direct `ProjectReference` rows are deliberately omitted from both export kinds.
When outgoing links exist, the serialized document warning and returned file
warning must contain the same explicit omission text. Imports never infer links
from names or slugs. Full imports include only selected canonical source
bodies/evidence; unselected sources remain absent. Source and child IDs are
remapped inside graph evidence/citations, selected-canon mappings are restored,
and lexical/context indexes rebuild without rerunning extraction.

An import is one coherent transaction across project direction, images, fonts,
styles, structure, editions, publication sections, graph state, and supported
artifact metadata. Validation failure rolls back the destination mutation before
the failed job/report is recorded. The Completed job state commits in the same
transaction. Context/vector index work is deferred until the transaction
boundary and then rebuilt from committed state; post-commit indexing may add a
warning but cannot relabel committed imported data as a failed import. Older
formats are handled by isolated transformers only, and unsupported or
foreign custom-font keys fail closed rather than persisting an unsafe reference.

Durable job rows are the restart/reconciliation boundary for project imports,
ingest, image generation, embedding rebuilds, editor revision, publication
rendering, and preparation. Startup marks interrupted work according to each
feature’s contract; a queue or component must not assume in-memory progress is
durable. Migration fixtures use the same startup orchestrator and real import
path as the application rather than a parallel test-only migration sequence.

Desktop development stores SQLite in the repository by default. Packaged builds
resolve per-user application-data storage so installed, portable, and mounted
DMG locations remain disposable. Database files, verification databases,
temporary migration backups, and publish output are local ignored state. Port
and OAuth redirect configuration remain local-host-only unless an explicit
security architecture change expands exposure. Version-history repositories use
the separate development `History/` root (ignored by the source repository) or
packaged `%LocalAppData%/Lorekeeper/History/<repository-id>.git`; they are not a
SQLite backup or a portable v26 export.

Image-upscale lineage is durable portable state. V29 and later preserve parent identity
and structured upscale metadata, remap parents on import, and fail closed when
a v29 derivative names a missing parent. A non-structural export that includes an
upscale includes omitted ancestors transitively so the package remains valid.
Older inputs remain accepted through the versioned adapter; historical
`Resized` assets whose metadata identifies `print-resample` are reclassified as
`Upscaled` without changing their bytes or provenance.

The forward database migration adds the persisted preparation-summary JSON.
Database image sources are stored by enum name, so existing `Imported` rows
remain unchanged when `Imported = 5` and `Upscaled = 6` become the current code
values. The migration reclassifies only stored `Resized` rows whose historical
metadata proves they were print resamples. Existing preparation jobs and
unrelated imported/resized assets are preserved; older portable JSON's numeric
source value is handled separately by its versioned import adapter.

Image-generation jobs now persist normalized model, quality, output format,
compression, and `Background` settings before provider dispatch. The forward
background migration assigns existing jobs `auto` and preserves their audit
history; provider-reported model and quality remain separate result provenance.

The cover-description forward migration rewrites persisted primary and
exact-surface scene bindings to `description`, then drops the obsolete
`PublicationCoverDesigns.BackCopy` column. The downgrade recreates the column
from each release's Description and rewrites the bindings back so the historical
runtime remains internally consistent.

The scoped-publication forward migration adds render scope and trusted cover
page-count dependency to render jobs and replaces each preparation's single
render link with Book, Interior, and Cover links. Existing artifact bytes,
hashes, rows, and digital/Core Book history are preserved. Previous combined
physical render jobs and their artifacts are marked legacy so they remain
downloadable historical data but cannot satisfy scoped production preflight;
non-physical preparation links move to the Book slot. Interrupted legacy
physical jobs are not recovered into the v12 queue.

## Key files and file families

| Path or family | Primary responsibility |
|---|---|
| `Lorekeeper/Persistence/AppDbContext.cs` | EF Core model, relationships, indexes, JSON property bags, concurrency, target normalization, and transient SQLite lock handling. |
| `Lorekeeper/Persistence/AppDatabaseOperations.cs` | Operation-owned read/write contexts, process/project leases, lock ordering, nested operation sharing, and commit/disposal. |
| `Lorekeeper/Persistence/Repositories/DatabaseRepositories.cs` | Operation-scoped repository bundle and stage-only repository contract. |
| `Lorekeeper/Persistence/Repositories/` | Feature repository implementations that stage rows and never commit independently. |
| `Lorekeeper/Persistence/ProjectMutationCoordinator.cs` | Project-scoped serialization for manuscript-reference and asset/style deletion integrity. |
| `Lorekeeper/Persistence/SqliteConnectionSettings.cs` | Local SQLite paths, PRAGMAs, WAL, busy timeout, and packaged per-user placement. |
| `Lorekeeper/Persistence/DatabaseMigrationRecoveryService.cs` | Protected backup/restore, markers, recovery shell, confirmation, discovery, and pruning. |
| `Lorekeeper/Persistence/DatabaseStartupMigrationService.cs` | Ordered startup migration/recovery orchestration and readiness-boundary integration. |
| `Lorekeeper/Persistence/Migrations/` | Immutable EF schema history and current model snapshot; never edit applied files. |
| `Lorekeeper/Manuscripts/ManuscriptMigrationService.cs` | WAL-safe structured-manuscript migration, recovery, validation, journaling, and current v5 upgrade. |
| `Lorekeeper/Manuscripts/VisualCompositionMigrationService.cs` / `AuthoringPageMigrationService.cs` | Guarded visual/composition and authoring-page cutovers with protected invariants. |
| `Lorekeeper/Publish/Publication*MigrationService.cs` | Core, edition, Press, section, print-artifact-profile, and edition-content transformations. |
| `Lorekeeper/ImportExport/ProjectExportModels.cs` | Final JSON v31 portable DTOs and isolated older input adapters. |
| `Lorekeeper/ImportExport/ProjectImportExportService.cs` | UI-facing Full/Non-structural export, warnings, queueing, and import job lifecycle. |
| `Lorekeeper/ImportExport/ProjectImportJobProcessor.cs` | Transactional v26 import, ID remapping, rollback/report behavior, legacy conversion, and post-commit indexing. |
| `Lorekeeper/ImportExport/ProjectImportJobQueue.cs` / `ProjectImportJobNotifier.cs` | Import job dispatch and ephemeral live UI updates; the provider/background chapter owns hosted worker execution. |
| `Lorekeeper/VersionHistory/Snapshots/`, `Git/`, `Services/`, `Restore/`, and `Sync/` | Deterministic creative snapshot trees, bare Git/checkpoint metadata, guarded restore/import, explicit remote attachment, and durable automatic transport; detailed ownership is in `version-history-sync.md`. |
| `Lorekeeper.Tests/DatabaseMigrationRecoveryTests.cs` | Recovery markers, protected backup retention, and fail-closed startup behavior. |
| `Lorekeeper.Tests/ManuscriptMigrationIntegrationTests.cs` | Real legacy WAL migration, backup/journal/hash validation, restore, and audit compatibility. |
| `Lorekeeper.Tests/ProjectExportCompatibilityTests.cs` / `ProjectImportJobIntegrationTests.cs` | v26 export/import preservation, warnings, remapping, rollback, and legacy adapters. |
| `Lorekeeper.Tests/LorekeeperPressMigrationTests.cs` | Installed-schema Press/Core projection, migration preservation, recovery, and byte/hash invariants. |
| `Lorekeeper.Tests/ScopedPublicationRenderingMigrationTests.cs` | Combined-physical legacy classification, artifact byte/hash preservation, and unaffected Digital PDF Book-link migration. |
| `Lorekeeper/Models/OpenAiAccount.cs`, `OAuthToken.cs`, `LlmProvider.cs`, `SearchProvider.cs`, `Lorekeeper/Llm/OpenAiAccountModelCatalog.cs` | Credential/configuration persistence and the versioned account-model authority; OpenAI tokens are account-owned, while API/search secrets remain in their owning provider rows. |
| `.gitignore` | Ignored local databases, migration backups, verification databases, temporary output, and repository-root publish artifacts. |

## Related chapters

- [`manuscript-authoring.md`](./manuscript-authoring.md) owns manuscript,
  styles, annotations, in-process authoring history, Git-backed Review Edits,
  and semantic editor contracts.
- [`composition-media.md`](./composition-media.md) owns image/font/composition
  relationships, deletion guards, page setup, variants, and canvas previews.
- [`publishing-model.md`](./publishing-model.md) owns Core/release/section
  semantics, effective configuration, Publish UI/tools, and projections.
- [`press-production.md`](./press-production.md) owns native Press, products,
  geometry, covers, rendering, validation, artifacts, and packages.
- [`assistants-chat.md`](./assistants-chat.md) owns transcript/runtime state;
  assistant conversations and model selections are intentionally excluded from
  project export/import.
- [`version-history-sync.md`](./version-history-sync.md) owns the Git-backed
  snapshot, checkpoint, restore, clone, and synchronization boundary; SQLite
  rows remain the live state and Git history is not a database backup.

## Relevant verification

Persistence and import/migration changes require the solution build and the
approved data-safety tests:

```powershell
dotnet build Lorekeeper.sln
dotnet test Lorekeeper.Tests\Lorekeeper.Tests.csproj
dotnet test Lorekeeper.Tests\Lorekeeper.Tests.csproj --no-restore -c Release --filter "FullyQualifiedName~ProjectReferenceMigrationTests|FullyQualifiedName~V25ExportPreservesRectoSettingsWarnsAndDoesNotInferProjectReferences"
```

For startup behavior, run the explicit HTTP profile, confirm no startup
exception, and terminate the host:

```powershell
dotnet run --project Lorekeeper --launch-profile http
```

Review migration changes against protected-backup, journal, lock-order,
foreign-key, row-count, projection-hash, artifact-byte/hash, rollback, and
recovery-shell invariants. Search for obsolete runtime names, old schema fields,
deleted migration paths, and accidental compatibility shims. Confirm applied
EF migrations are unchanged and that every new schema change has a forward
migration. Do not run destructive restore/delete operations as part of routine
documentation or source verification.

The repository’s approved .NET tests include startup database migration and
versioned project import/export safety plus the deterministic, headless v1
contract regressions enumerated by the validation chapter. Do not add broad UI,
assistant, provider, packaging, or runtime-behavior suites under this boundary.
Provider calls, OAuth, embeddings, search, image generation, publication output,
packaging, and OS-specific behavior require explicit integration exercise before
claiming they work.
