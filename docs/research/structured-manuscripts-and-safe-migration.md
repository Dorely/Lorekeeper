# Structured manuscripts and safe migration

Last reviewed: 2026-07-30
Research access date: 2026-07-29
Decision state: Phase 1 structured-manuscript contract and application cutover
implemented; the required-evidence checklist remains an open release gate.

## Executive conclusion

Before this cutover, `Chapter.Body` was a plain string used by editing,
assistant review, search/indexing, graph auto-linking, image/page composition,
import/export, contests, revision agents, and publishing. Replacing it in place
would have risked both data loss and silent behavioral divergence.

Phase 1 uses an expand–migrate–validate–contract transition:

1. create a recoverable SQLite backup;
2. apply the forward rename/add migration while the backup retains the
   recoverable legacy representation;
3. convert every live and historical body-bearing record through one codec;
4. verify counts, normalized text, hashes, references, and consumers;
5. switch all runtime reads/writes atomically to the new services;
6. remove obsolete runtime columns and paths in the same completed feature;
7. retain the backup and a readable migration report.

There must be no indefinite dual-write compatibility mode.

## Why structure is required

A professional book system needs to address and transform objects smaller and
more meaningful than an entire chapter string:

- paragraphs, headings, scene breaks, lists, quotations, figures, and matter;
- inline emphasis, language, links, character-style intent, and references;
- stable anchors for comments, suggestions, assistant changes, captions, and
  cross-references;
- style roles that can map differently in paperback and EPUB editions;
- deterministic import/export without scraping presentation HTML;
- revisions that can detect stale edits and remap positions.

ProseMirror demonstrates the relevant model: a schema constrains valid nodes and
marks, documents serialize to JSON, and immutable transactions record the steps
from one state to another. Lorekeeper will use those principles while owning its
schema and persistence contract.

Source: [ProseMirror guide](https://prosemirror.net/docs/guide/).

## Canonical manuscript representation

The proposed persisted root contains:

- `schemaVersion`;
- a stable manuscript/chapter ID;
- a monotonically increasing revision;
- ordered semantic block nodes with stable IDs;
- inline content and marks;
- explicit attributes for semantic roles and reference targets;
- no visual CSS, generated pagination, or vendor settings.

Illustrative shape:

```json
{
  "schemaVersion": 1,
  "revision": 42,
  "content": [
    {
      "id": "block-id",
      "type": "paragraph",
      "attrs": { "style": "body" },
      "content": [
        { "type": "text", "text": "Example", "marks": ["emphasis"] }
      ]
    }
  ]
}
```

This is explanatory, not the final schema. The implementation feature must
publish the actual JSON Schema and version-transition rules.

### Source versus projections

Authoritative:

- structured manuscript JSON and revision;
- stable block/reference IDs;
- semantic style definitions;
- assets and their semantic associations.

Derived and rebuildable:

- plain-text projection;
- editor HTML;
- normalized publication document;
- FTS/vector fragments and graph auto-mentions;
- page maps, previews, EPUB, PDFs, and preflight reports.

Derived projections store their source revision/hash so stale data is
detectable.

## Safe SQLite migration protocol

SQLite's WAL file is part of the persistent database state; copying only the
main database while a connection is active can lose committed transactions or
corrupt the copy. SQLite provides an Online Backup API that creates a consistent
snapshot. Lorekeeper must use a connection-aware backup mechanism, not a naive
file copy.

Sources: [SQLite Online Backup API](https://www.sqlite.org/backup.html) and
[SQLite WAL documentation](https://www.sqlite.org/wal.html).

### Stage A — preflight and backup

- acquire the application migration lock before background workers start;
- record application, schema, export-format, and migration versions;
- run `PRAGMA quick_check` and stop on failure;
- create a timestamped backup through the SQLite backup API;
- validate the backup independently with `quick_check`;
- record original row counts and per-record hashes;
- ensure sufficient disk space and make the backup path visible to the user.

The existing database remains untouched if backup or preflight fails.

Because the database also contains provider keys and OAuth tokens, migration
backups are sensitive:

- store them only under protected per-user application data, never temporary,
  project-export, publish-package, or downloads locations;
- preserve restrictive owner-only filesystem permissions where the operating
  system supports them;
- exclude them from project export, artifact packaging, diagnostics uploads,
  and logs;
- redact secrets and credential-bearing payloads from migration reports;
- define a retention limit and age policy, expose user-controlled cleanup, and
  never delete the last known-good backup without explicit confirmation;
- require explicit confirmation before restore or deletion, and create a
  protected diagnostic backup before replacing a failed database.

### Stage B — expand

Add:

- structured manuscript storage and revision columns/entities;
- versioned Picture Page and illustrated-prose references to stable manuscript
  blocks/ranges, plus a place for incompatible legacy-layout snapshots;
- migration journal with phase, source/target version, started/completed times,
  counts, hashes, and error detail;
- temporary source identifiers only where they are required to prove mapping.

Implementation note: the forward migration renames legacy prose columns into
their canonical manuscript JSON columns, then the application-owned migration
service transforms and validates the rows before startup continues. A failure
preserves the complete pre-migration database and opens a current-schema,
projectless recovery shell. The user can schedule that backup for offline
replacement at the next startup, so the runtime never enters an indefinite
dual-write or partially migrated state.

### Stage C — transform

Use one deterministic codec for all current and historical prose:

- normalize line endings to LF;
- preserve Unicode text exactly apart from documented normalization;
- map blank-line-separated paragraphs and established scene-break conventions
  using explicit, tested rules;
- treat whitespace-only blank lines as paragraph separators and canonicalize
  `***`, `* * *`, and `###` scene-break aliases to `***`;
- preserve leading/trailing intent where it is meaningful;
- produce stable block IDs from a recorded migration namespace and source ID;
- convert live chapters, accepted/original contest bodies, contest candidates,
  revision-session originals/results, chapter-body AI change payloads in every
  status, `Chapter.PageLayoutJson`, `Chapter.IllustrationLayoutJson`, and any
  other stored body-bearing or body-anchor data discovered by the final impact
  search.

Unknown or malformed data stops migration. It is never silently discarded or
replaced with an empty document.

### Stage D — validate

For every converted record:

- serialize and parse through the production codec;
- compare normalized plain-text projection with the source;
- compare SHA-256 hashes of the normalized text;
- verify block IDs are unique and schema-valid;
- verify all chapter/project relationships;
- verify pending changes and historical comparisons still identify their
  subject;
- verify every migrated visual element/image anchor still resolves, and compare
  element counts, IDs, images, geometry, typography, z-order, reading order,
  captions, alt text, and source hashes;
- rebuild and compare required search/index counts;
- generate a migration report with totals and exceptions.

Then open the migrated database through the new repositories and execute
representative reads before committing the transition.

### Stage E — cut over and contract

- change all application consumers to the manuscript service and projections;
- use the schema-driven ProseMirror UI adapter that reads/writes canonical
  manuscript documents through revision-aware service commands; it never
  persists DOM/HTML or reads/writes `Chapter.Body`;
- change assistant tools to stable block/range operations with revision tokens;
- update project export to a new version containing structured manuscripts and
  their stable references;
- keep importers for historical export versions isolated at the import boundary;
- stop dual writes;
- add a forward migration that drops obsolete runtime body columns once
  every consumer is gone;
- drop the legacy text-bearing/page-index visual-layout fields once all visual
  services and imports use stable manuscript references;
- search the repository for old names and direct fields;
- leave historical EF migration files intact.

The final runtime has one canonical path. Backward compatibility exists only in
versioned import adapters, not in active domain behavior.

### Historical edit and audit records

Migration must explicitly classify every status and field rather than only
converting pending work:

- all `AiChange` statuses (`Pending`, `Applied`, `Rejected`, `Conflict`,
  `Superseded`, and `Resolved`) and their `ArgumentsJson`, `BeforeJson`,
  `AfterJson`, `DraftAfterJson`, `ReviewStateJson`, and `ResultJson`;
- every `ContestBatch` status (`Running`, `Completed`, `Failed`, `Cancelled`,
  and `Finished`) and its `OriginalChapterBody` and `AcceptedChapterBody`;
- each `ContestCandidate.ProposedBody`, `MutationsJson`, and `ReviewStateJson`,
  plus `RawResponse`, in every status (`Pending`, `Running`, `Completed`,
  `Failed`, `Invalid`, `Selected`, and `Rejected`);
- each `EditorRevisionSession.OriginalChapterBody`, `MutationKind`, `StartLine`,
  `EndLine`, `ReplacementText`, `ProposalJson`, and `RawResponse`, in every
  status (`Queued`, `Running`, `Completed`, `Failed`, `Invalid`, and
  `Cancelled`);
- persisted revision/contest messages or raw responses whose display semantics
  depend on numbered lines.

Pending AI review changes that remain safely applicable are converted to
semantic manuscripts and stable anchors. In-flight contest and revision-worker
jobs cannot resume across this application migration, so their batches,
sessions, candidates, and parent jobs are terminalized with an explicit
migration error and completion timestamp. Resolved, applied, rejected, failed,
or otherwise terminal records remain immutable audit evidence when replay is
neither safe nor required, wrapped in an explicit version-labeled legacy
snapshot that preserves original JSON/text and renders read-only. No terminal
audit record is silently rewritten into an operation with different meaning.

Malformed JSON, stale line ranges, malformed illustration hashes, invalid
illustration indices, or ambiguous paragraph mappings fail the transition and
are named in the migration report. Fixtures cover every status, finished
contests, completed revision sessions, line-anchor remapping, and legacy
read-only rendering.

### Chapter visual-layout records

`Chapter.PageLayoutJson` is not merely a rebuildable rendering cache:
`PicturePageTextElement.Text` can hold the chapter's prose in multiple
independently positioned boxes, and reading order projects those boxes back to
the chapter body. `Chapter.IllustrationLayoutJson` also anchors images by
`ParagraphIndex` and `ParagraphHash`. Both therefore migrate within the
manuscript feature, before `Chapter.Body` can be removed.

For each Picture Page:

- parse the legacy layout strictly instead of using a fallback empty layout;
- preserve every text and image element ID, geometry, fitting, opacity,
  typography, role, z-order, and reading order;
- preserve text-box boundaries by mapping each text element to one or more
  stable manuscript block/range IDs in reading order;
- compare the legacy projected body and the new manuscript projection after the
  same documented whitespace normalization;
- replace duplicated authoritative text in the runtime layout with versioned
  stable content references only after equivalence passes.

For each illustrated-prose image:

- resolve the runtime-authoritative `ParagraphIndex`, validate its bounds and
  the `ParagraphHash` shape, and record a well-formed stale hash instead of
  blocking migration because the legacy normalizer did not refresh non-empty
  hashes after ordinary chapter edits;
- remap the anchor to a stable manuscript block ID plus the semantic before/
  after/within position needed by the current `AnchorPosition`;
- preserve image ID, element ID, dimensions, alignment, caption, alt text,
  sort order, and page-break intent;
- reject missing, ambiguous, or hash-mismatched anchors rather than guessing.

If a legacy layout cannot be mapped without changing meaning, migration stops
and identifies the chapter and element. A deliberately approved exception may
preserve the entire original JSON and body as a version-labeled, read-only
legacy snapshot; it must not collapse multiple text boxes, discard geometry, or
pretend the layout is editable. The v8 export contains the new stable layout
references or the explicit legacy snapshot, never an unmarked mixture.

### Publication edition migration is separate

The structured manuscript feature does not create or contract publication
editions. After that feature is complete, the publication-edition feature runs
its own backup, journal, expand, transform, validate, and contract transition.
It converts the optional zero-or-one `PublishProfile` and project-scoped publish
selections, placements, and cover references.

For a project with no profile:

- create a default edition if publish selections, placements, or a cover
  reference exist;
- otherwise defer edition creation until the user or Publish assistant creates
  one;
- attach every migrated publishing row to exactly one edition;
- verify pre/post row counts, foreign keys, ownership, and cover materialization.

The manuscript feature introduces project export v8. The semantic-editor feature
introduces v9 for named style definitions. The later edition feature introduces
v10 for editions and edition-style mappings. Each version has its own isolated
older-version import adapter and recovery evidence.

### Failure and recovery

If any step before cutover fails:

- roll back the active transaction;
- preserve the old database as a protected backup and start a projectless
  current-schema recovery shell;
- retain journal/error detail and the validated backup;
- show the exact backup path and failure phase;
- do not start workers or permit edits against the preserved legacy database.

The recovery screen can schedule the backup after explicit user confirmation.
Replacement happens at the start of the next process, before workers start, and
first backs up the failed database for diagnosis.

## EF Core implementation rules

EF Core migrations incrementally update schema while preserving data, but
generated migrations must be inspected because a rename can otherwise appear as
a destructive drop/add. The Lorekeeper transition needs explicit forward
migrations plus application-owned transformation/validation code where SQL is
not sufficient.

Sources: [EF Core migrations overview](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/),
[applying migrations](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying),
and [custom migration operations](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/operations).

Implementation requirements:

- never edit applied historical migrations;
- inspect generated SQL and data-loss warnings;
- make restart/re-entry behavior explicit and tested;
- ensure only one process can run the transition;
- do not perform a long, opaque transform inside a Razor circuit;
- do not call normal application services against a half-expanded model;
- do not mark the journal complete until post-migration reads and hashes pass.

## Import/export compatibility

The manuscript feature makes the next project export format version 8 and stores
structured manuscripts and their stable references. The semantic-editor feature
produces version 9 with named semantic style definitions. The later publication-
edition feature produces version 10 with editions and edition-style mappings.

Import policy:

- v8 round-trips structured manuscripts losslessly;
- v9 round-trips manuscripts and named semantic style definitions losslessly
  after the semantic editor is delivered;
- v10 round-trips manuscripts, style definitions, editions, and edition-style
  mappings losslessly after the edition feature is delivered;
- v1–v7 use isolated adapters that produce the current canonical model;
- legacy `Body` strings are converted with the same migration codec;
- import produces a report of normalization and unsupported constructs;
- old version DTOs do not leak into current services;
- v8 and later exports never write the legacy body representation; v10 and
  later never write the legacy profile representation.

## Assistant migration and parity

Whole-chapter replacement tools become obsolete. New tools operate on the same
commands as the editor:

- read document outline or bounded blocks;
- insert, replace, delete, move, split, and merge blocks;
- apply/remove inline marks;
- set semantic style roles;
- manage figures, notes, tables, and references as their phases arrive;
- request a semantic diff before mutation;
- include the expected manuscript revision;
- return changed IDs and the new revision.

The service rejects stale revisions and invalid schemas. Reviewable AI changes
store semantic operations and before/after snapshots sufficient for human
inspection and recovery. Applying a change updates text projections, search,
graph mentions, Picture Page synchronization, and artifact-staleness state in
one owning workflow.

The Editor assistant can also read migration preflight, journal, validation,
backup, and recovery state and explain safe retry options. It may prepare a
restore request, but replacing a database or deleting a backup requires the
same explicit user confirmation as the UI.

## Required test evidence

After the explicit user authorization required by `AGENTS.md`, the migration
feature is incomplete without:

- fixture databases representing every historical schema/export version still
  supported;
- edge cases for empty text, mixed line endings, Unicode, scene breaks, very
  large chapters, malformed pending changes, and interrupted journals;
- exact normalized-text/hash comparisons;
- backup creation and restore drills in WAL mode, including owner-only
  permissions, retention cleanup, explicit delete/restore confirmation, and
  proof that backups never enter project exports, publish packages, diagnostics,
  or logs;
- failure injection before, during, and after transform;
- migration restart/idempotence tests;
- every status and body-bearing/anchor-bearing field for `AiChange`, contest
  batches/candidates, revision sessions, and persisted revision messages;
- semantic conversion fixtures for active records and versioned read-only
  legacy-audit rendering fixtures for terminal records;
- v1–v7 import and v8 manuscript round-trip tests;
- v8-to-v9 semantic-style import/round-trip tests;
- separate v9-to-v10 edition migration tests, including projects with no
  publishing data, profileless publishing rows, and one-profile projects;
- v10 manuscript/style/edition round-trip tests;
- index/graph/Picture Page projection verification;
- multi-text-box Picture Page fixtures proving element identity, geometry,
  typography, z-order, reading order, and body-projection equivalence;
- illustrated-prose fixtures proving paragraph hashes remap to the intended
  stable block IDs and mismatches fail closed;
- assertions that obsolete runtime fields and registrations are gone.

## Go/no-go gate

No user database is migrated until a copied production-like database passes the
full procedure and restore drill. No old field is dropped until every consumer
has moved and a repository-wide search confirms the obsolete runtime path is
unused.

## Sources

- ProseMirror, [Guide](https://prosemirror.net/docs/guide/).
- SQLite, [Online Backup API](https://www.sqlite.org/backup.html) and
  [Write-Ahead Logging](https://www.sqlite.org/wal.html).
- Microsoft, [EF Core migrations overview](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/),
  [applying migrations](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying),
  and [custom operations](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/operations).
