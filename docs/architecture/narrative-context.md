# Narrative Context, Canon, and Retrieval

## When to read

Read this chapter completely before changing projects, Project Guidance, Book
Briefs, direct project references, acts, chapters as outline items, beats,
entities, facts, graph relationships, context selection, retrieval, indexing,
source ingest, or the narrative evidence exposed to any assistant. Also read it
when a persistence, import/export, image, or assistant change alters which
material is authoritative, searchable, canonical, or available across project
boundaries.

Pair this chapter with [assistants-chat.md](assistants-chat.md) for prompt/tool
behavior, [manuscript-authoring.md](manuscript-authoring.md) for chapter body
content, and [persistence-migrations-import.md](persistence-migrations-import.md)
for schema, transaction, migration, or portable-project changes.

## Scope and ownership

This chapter owns the narrative-information model from the project root through
outline structure, graph memory, ingested evidence, direct-reference scope, and
the lexical/vector retrieval projections built from that state. It defines what
is canon, what is evidence, how provenance survives retrieval, and which service
boundaries must synchronize graph and indexes.

The project and graph services own durable narrative mutations. Context and
search services own bounded projections of that state; they never become a
second source of truth. Ingest owns source extraction, provenance, staging, and
source-derived graph/index output. Assistant adapters consume these boundaries
but do not own them. Chapter manuscript structure is owned by
[manuscript-authoring.md](manuscript-authoring.md), even though its searchable
projection participates here.

## Current architecture and invariants

### Project direction and outline spine

A `Project` is the ownership root for Project Guidance, one structured Book
Brief, acts, chapters, writing samples, conversations, images, fonts, jobs,
publishing state, and graph rows. Project Guidance is optional user-authored
direction. The Book Brief is canonical high-level direction with validated
partial updates: `null` means unchanged, while an explicit clear list removes a
field. Code-owned professional instructions must never be copied into either
user-owned field.

Acts and chapters form the editable outline spine. Act deletion demotes its
chapters to Unassigned through `OnDelete.SetNull`; chapter content is not
deleted merely because its grouping disappears. Structural mutations touch the
project timestamp, keep graph structure synchronized, and refresh targeted
context/search projections. Events or beats are graph entities parented to
chapters rather than a separate manuscript block type.

`IOutlineGraphSync` projects Project, Act, Chapter, and Event structure into
graph nodes and ordered `HasChild` edges. That projection is derived, but it is
maintained through the owning project, act, chapter, and entity services. Do not
write equivalent graph rows directly from UI components, assistants, or import
helpers.

### Graph-backed story memory

The relational graph is authoritative for structured story memory. Structural
types include `Project`, `Act`, `Chapter`, `ProjectFact`, and `Event`; default
narrative types include `Character` and `Location`, while the project-scoped
type registry permits arbitrary non-structural types. `IEntityService` owns
entity create/update/delete, parent moves, relationship changes, and the
associated context-index and auto-link refresh. `IProjectFactService` stores one
indexed `ProjectFact` node per key/value pair and maintains its link from the
project root.

Manual entity-to-chapter context uses a `RelevantTo` edge. Every canonical
entity that appears, affects, constrains, or otherwise matters to a chapter
needs that chapter-level link; beat links may add precision but do not replace
it. `AppearsIn` is reserved for Character-to-Event/beat relationships. Existing
or incoming `AppearsIn` links aimed at a Chapter are normalized to
`RelevantTo`. Editor context traverses the active chapter and its beats, making
these explicit chapter links the primary outline-to-drafting handoff.

`GraphAutoLinkService` derives low-priority, read-only `AutoMention` edges from
exact labels and aliases. They are useful discovery evidence, not user-owned
relationships. Graph UI and entity tools return manual links before automatic
ones and refuse to edit managed structural, provenance, or auto-mention edges.
Changes to names, aliases, source text, or searchable narrative content must
refresh affected auto links through the owning services.

### Retrieval and context assembly

`IProjectSearchService` fuses SQLite FTS5/BM25 and sqlite-vec results. Search
results carry source kind, stable source/container identity, snippet or bounded
content, and owning-project provenance. `IContextIndexingService`, chapter and
writing-sample services, ingest indexing, and the embedding rebuild worker keep
lexical and vector projections current. A change to any retrievable model or
stored text must be traced through both index paths; updating only vectors or
only FTS leaves inconsistent assistant behavior.

Assistant-facing project search is discovery, not a complete-source read. The
shared prompt discipline asks each call to represent one source-content facet
with 2–6 high-signal terms, putting rare canonical names, aliases, exact
events, objects, and distinctive wording first. Conceptual searches use the
default FTS5/BM25 plus sqlite-vec hybrid; exact wording, proper names, or a
known source use filtered lexical-only search. Independent facets are separate
queries whose candidates are unioned and deduplicated by stable source or
chapter identity. Callers honor counts, completeness, and pagination, then
use exact source/chapter/entity reads before relying on a detail. They continue
until focused evidence stops changing the decision, with one reformulation
grounded in current names or returned terms before reporting no match. External
`web_search` is outside this internal project-search contract. The lexical
index currently tokenizes only the first twelve query terms, so Editor impact
facets deliberately keep their combined query and keywords below ten
high-signal terms.

`find_impacted_chapters` remains an evidence-map helper rather than an authority
on revision scope. Its Editor-facing contract resolves exact directly affected
IDs first, reserves `anchorChapterId` for forward propagation, and omits the
anchor for backward, whole-book, bidirectional, or direction-neutral audits.
Optional keywords must be exact terms expected to coexist in the same passage;
alternatives and independent concepts become separate facet calls. Candidate
chapters are unioned across facets and must survive focused project search plus
exact chapter reads before revision assignment; anchor proximity, generic
substring matches, and unexplained aggregate scores are insufficient evidence.

`ContextBuilder` owns the Editor's bounded automatic working context. It starts
from protected Project Guidance and Book Brief direction, then adds relevant
prior chapter material, explicit per-chapter include/exclude preferences, graph
relationships, project-search results, named manuscript styles, page setup,
annotations, and canonical entity visuals. The active chapter is loaded once as
a complete `agent-manuscript-v4` snapshot with source/completeness metadata,
revision, source hash, stable block IDs, exact text, sparse UTF-16 marks,
canonical positions, recursive tables and notes, interned direct paragraph
formatting, Figures, Designed Pages, and publication bindings. Block rows use absolute indexes as compact overlay
cross-references while stable IDs remain the only mutation identities. Named
styles arrive separately as versioned definitions; direct formatting overrides
named styles, which override built-in defaults. Ordinary active-chapter editing
therefore should not begin with a redundant manuscript read.

Entity context uses a separate compact projection that preserves meaningful
properties, knowledge, and canonical visual metadata while omitting empty
fields, graph adjacency, and internal provenance. Complete paginated entity
reads own relationship traversal. Tool-result adjacency is current-turn context
and must be reacquired after compaction or on a later turn.

`EditorContextPreference` rows are overrides over automatic defaults, not a
copy of the underlying material. Reset removes all overrides for the active
chapter and rebuilds context; it never mutates canon, source data, manuscript
content, or relationships. Writing Samples are default-on style references, so
resetting an explicit preference returns them to included. The chapter header's
word/token value measures chapter plain text only; Assistant Memory measures
the full enabled prompt context and is intentionally larger.

Model-facing structured payloads use the shared compact serializer and
`AgentPayloadPaginator`. Pagination repeats identity fields, keeps logical JSON
records intact where possible, and segments only an individually oversized text
field with continuation metadata. Assistant tools must not create parallel,
unbounded response shapes. Manuscript context is the narrower exception owned
by `AgentManuscriptProjection`: its paged and filtered results preserve absolute
document indexes and omit empty semantic overlays rather than using a generic
object paginator.

### Project creation and profile indexing

Creating a project persists its empty Book Brief and page setup with the project
before seeding local graph defaults. The creation path never waits for an
embedding provider: the derived semantic project-profile index is populated by
the first substantive profile mutation or a full embedding rebuild. Direct
project-profile reads and lexical search continue to use the canonical project
and Book Brief rows.

### Direct project references and provenance

`ProjectReference` is a direct, read-only continuity link from an active
referencing project to another project. Its stable target identity is the pair
`ReferencedRepositoryId` and `ReferencedProjectId`; local resolution is a
nullable convenience, not the identity. The composite identity prevents
duplicates, self-links fail only when both repository and project identity
match, and reciprocal links are independent. Reference scope is exactly one
hop. Arbitrary foreign project IDs and transitive references fail closed.

Deleting a referencing project cascades its outgoing links. Deleting a
referenced project is restricted, so `IProjectService.DeleteAsync` rechecks
incoming dependencies in a global write operation and requires an explicit
detach decision. Adding or removing a link touches only the referencing
project's update timestamp. Direct-reference rows are deliberately excluded
from portable project exports and are never inferred from names or slugs during
import. Version-history snapshots preserve outgoing reference repository/project
identity and cached labels; restore relinks only to an exact local identity and
otherwise leaves the reference unresolved.

Referenced narrative scope is deliberately narrower than the full project. It
contains the project profile, acts, Core chapters/manuscripts, entities, facts,
writing samples, and only Book Brief-selected canonical ingest sources. Release
chapter variants, publication sections and settings, unselected sources, and
general image-library assets are excluded. Canonical visuals are readable only
through `EntityVisualExample` associations owned by the referenced project.

Reference-aware search performs independent owning-scope searches and globally
fuses them without copying referenced data into the active project's index.
Every discovery and exact-read result retains origin project ID, name, slug, and
referenced status. The active project's current canon and explicit user
direction take precedence; foreign material is continuity evidence and
conflicts must be surfaced rather than silently merged. All six assistants can
receive the same bounded manifest and origin-qualified list/search/read tools,
but every mutation remains active-project-only.

### Ingest sources and evidence

Ingest accepts text, Markdown, EPUB, PDF, image, and webpage material. The UI
accepts at most 50 selected files, reads at most 100 MiB per file, and creates
one durable job per file sequentially so mixed batches retain independent
results. A single text/Markdown upload remains editable before submission, and
that edited text is authoritative over its original bytes.

New-source UI defaults to `IndexOnly`; callers that do not supply a mode retain
the existing `ExtractEntities` behavior. Index-only mode hides entity-extraction
instructions/profile and hides the model selector for text-only inputs; switching
back restores their selections. PDF/image selections retain the model selector
for vision reading, and PDFs retain their page-limit and vision/DPI controls.
Index-only upload and webpage batches
use artifact preprocessing, immutable originals/extractions, bounded text reading
blocks, and the durable ingest queue for graph structure plus lexical/vector
indexing. They never resolve an enrichment chat provider, create entity-extraction
checkpoints, or run entity/relationship extraction. The explicitly selected model
can perform vision preprocessing for images or explicitly enabled PDF vision
reads through the existing readiness checks. With PDF vision unchecked, only
embedded text is read: blank/short pages never trigger a model call. Empty pages
remain represented with diagnostics; a PDF with no readable text fails with an
actionable vision message rather than indexing page markers as source content.
If embeddings are unavailable, local
reading and lexical search remain available and the job stops with a resumable
configuration message.

Preprocessing creates durable `IngestSource` records, page- and block-level
locators, large logical source chunks, visual candidates, and small lexical and
vector fragments. Logical chunks are extraction checkpoints; retrieval
fragments are separate and map back to source character ranges and locator
metadata. The graph projection is Source to SourceChunk to SourceBlock through
ordered structural edges, with web/artifact provenance retained on the source.

The ingest processor performs bounded source analysis, stages records, promotes
simple relationships and canonical knowledge, persists reports/events, and
indexes final source projections. Entity-extraction restart subtracts only that
source's evidence, citations, legacy assertions, and ingest-owned orphan graph
output, then refreshes affected entity and retrieval projections. It must not
erase independently authored canon that happens to mention the same entity.
Job deletion removes operational audit state without deleting the source.

`BookBriefCanonSource` is a relational selection of an actual ingest source;
source deletion cascades the selection. Selected sources are canonical
grounding. Unselected sources remain searchable evidence and never become canon
without a user decision. Provenance fields use the current `sourceEvidence.*`
terminology. Historical `canonSource.*` payloads are translated only at the
versioned import boundary.

Research uses a configured web search provider, cache-first webpage candidates,
safe page/image reading, and reviewable graph changes. URL normalization,
robots handling, private-network rejection, redirect and byte limits,
per-host throttling, cooldowns, and fetch provenance belong to research
services. A prompt or Razor component must not weaken those controls. A cached
web candidate can be promoted into manual ingest, preserving its URL, hash,
extraction, and discovery provenance.

Background ingest and embedding work is app-process-owned. Durable job and
checkpoint rows are the restart/audit boundary; Blazor circuits only subscribe
to notifier updates. Changing queue behavior must preserve interrupted-job
reconciliation and must not leave network/model work holding an EF context or a
database write lease.

Visible source inventories and activity panels are read projections over those
same durable records. They may summarize progress and provenance, but they must
not introduce a second canon-selection, relationship, or job-state authority.

### Retained-source contract

Sources are immutable project-owned material: `SourceOriginal`
records name, media type, length, SHA-256, and ordered 8 MiB-or-smaller content
addressed chunks; `SourceExtractionVersion` records extractor/version/options,
content hash, status/diagnostics, and bounded reading blocks. `SourceLocation`
is durable evidence with source, extraction, block/page, range, locator, quote,
and verification hash. Re-extraction verifies an available retained original,
normalizes it locally before a short atomic publish, creates another immutable
version with new child identities, and never retargets existing evidence. Successful
local re-extraction atomically queues an index-only job with the new active version.
When no active extraction exists, Sources displays the latest pending/failed
attempt, its diagnostics, and original-download/re-extraction actions rather than
the unselected-reader placeholder. Only genuinely unselected sources show that
placeholder. Preprocessing diagnostics survive publication into the reader.

Every legacy source migrates to an immutable legacy extraction preserving its
source/chunk/block/page identities, normalized text, evidence, and hashes, with
original state `OriginalUnavailable`. Extracted text must never be presented as
a reconstructed original. The explicit `ConvertLegacySource` job pins the active
legacy version, structures its saved text outside write locks, and atomically
publishes a new ready extraction with fresh blocks/chunks. Its job ID identifies
the published extraction so interrupted indexing can retry without repeating
conversion. Original state, old versions, source identity, and existing evidence
anchors remain unchanged. Foreign-project requests, changed active versions,
and stopped jobs fail before publication. Re-extraction rejects active source
jobs. The same job then rebuilds search indexes without entity extraction;
restart never subtracts existing graph evidence for either non-extraction mode.
Sources exposes conversion from a legacy reader and the shared job controls.
Bibliographic records are project-owned and may exist
without a source. Bibliography saves acquire the project write lease and edits
require the `UpdatedAt` value read by the editor, rejecting stale writes.
Detaching preserves bibliographic metadata and atomically clears dependent
manuscript evidence links through the owning authoring services. Affected history
and outstanding assistant generations are invalidated so they cannot restore
detached evidence; the Sources surface explains evidence availability.
Source deletion reports bibliography, manuscript, graph,
assistant-transcript, and job usage before an explicit resolution.
Its final usage check runs under the deletion write lease, preventing a newly
created reference from racing the delete.

The project Sources workspace owns upload, contents navigation, bounded
normalized reading and lexical search, exact evidence-state display, original
download, bounded PDF page rendering, deletion impact, and local re-extraction.
PDF, EPUB, DOCX, text/Markdown, image, and saved-webpage inputs enter through the
same retained-source boundary. DOCX extraction rejects executable or unsafe
external relationships and does not infer Word pagination.
Its package guard is shared with semantic Word authoring import; Sources retains
its separate extraction budgets and never treats authoring insertion as source
ingestion. [Manuscript authoring](manuscript-authoring.md) owns the shared guard
and semantic conversion implementation.

## Key files and file families

| File or family | Architectural role |
|---|---|
| [`Lorekeeper/Models/Project.cs`](../../Lorekeeper/Models/Project.cs), [`BookBrief.cs`](../../Lorekeeper/Models/BookBrief.cs), and outline/graph/source models | Canonical project direction, outline, direct-reference, graph, ingest-source, and context-preference persistence shapes. |
| [`Lorekeeper/Projects/`](../../Lorekeeper/Projects/) | Project lifecycle, Book Brief mutation, deletion impact, and validated direct-reference scope. |
| [`Lorekeeper/Outline/ActService.cs`](../../Lorekeeper/Outline/ActService.cs), [`EntityService.cs`](../../Lorekeeper/Outline/EntityService.cs), [`EntityTypeService.cs`](../../Lorekeeper/Outline/EntityTypeService.cs), [`ProjectFactService.cs`](../../Lorekeeper/Outline/ProjectFactService.cs), and [`OutlineGraphSync.cs`](../../Lorekeeper/Outline/OutlineGraphSync.cs) | Outline-domain lifecycle, graph entity/type/fact ownership, and outline-to-graph synchronization; assistant adapters remain owned by the assistant chapter. |
| [`Lorekeeper/Graph/`](../../Lorekeeper/Graph/) and [`Lorekeeper/Knowledge/`](../../Lorekeeper/Knowledge/) | Graph UI facade, automatic mention links, graph-store abstraction, relational implementation, and vector-store primitives. |
| [`Lorekeeper/Search/ProjectSearchModels.cs`](../../Lorekeeper/Search/ProjectSearchModels.cs), [`ProjectSearchService.cs`](../../Lorekeeper/Search/ProjectSearchService.cs), [`ProjectSearchAgentPayload.cs`](../../Lorekeeper/Search/ProjectSearchAgentPayload.cs), and [`SqliteFtsProjectSearchIndex.cs`](../../Lorekeeper/Search/SqliteFtsProjectSearchIndex.cs) | Origin-aware lexical/vector discovery, compact assistant projections, exact narrative reads, and the project FTS5 index; external search adapters belong to providers. |
| [`Lorekeeper/Context/`](../../Lorekeeper/Context/) | Editor context assembly, recommendations, compact projections, pagination, indexing, and direct-reference manifests. |
| [`Lorekeeper/Ingest/IngestService.cs`](../../Lorekeeper/Ingest/IngestService.cs), [`BookArtifactPreprocessor.cs`](../../Lorekeeper/Ingest/BookArtifactPreprocessor.cs), [`IngestSourceStructureBuilder.cs`](../../Lorekeeper/Ingest/IngestSourceStructureBuilder.cs), graph/evidence/index services, and [`IngestJobProcessor.cs`](../../Lorekeeper/Ingest/IngestJobProcessor.cs) | Ingest lifecycle, artifact preprocessing, source structure, graph/evidence ownership, retrieval projections, and scoped processing; queue/worker execution belongs to providers. |
| [`Lorekeeper/Sources/`](../../Lorekeeper/Sources/) | Retained-source workspace projection, bounded normalized reading/search/evidence display, streamed original delivery, bounded PDF-page rendering, bibliography detachment, and guarded handoffs to ingest-owned deletion and local-only re-extraction. |
| [`Lorekeeper/Research/WebIngestCandidateService.cs`](../../Lorekeeper/Research/WebIngestCandidateService.cs), [`WebIngestCandidateModels.cs`](../../Lorekeeper/Research/WebIngestCandidateModels.cs), and [`ResearchActivityModels.cs`](../../Lorekeeper/Research/ResearchActivityModels.cs) | Cached source provenance, promotion into ingest, and read models; provider fetch policy and Research chat adapters remain in their owning chapters. |
| [`Lorekeeper/Components/Pages/Projects/Sources/`](../../Lorekeeper/Components/Pages/Projects/Sources/), [`SourcesPage.razor`](../../Lorekeeper/Components/Pages/Projects/SourcesPage.razor), [`Outline/`](../../Lorekeeper/Components/Pages/Projects/Outline/), and graph/context project components | Application-owned source library/upload entry, outline canon-source, graph, reference, and Assistant Memory interaction surfaces. |

## Related chapters

- [assistants-chat.md](assistants-chat.md) owns the shared conversation runtime,
  system-prompt composition, surface charters, tools, review changes, contests,
  and revision workers.
- [manuscript-authoring.md](manuscript-authoring.md) owns manuscript v7, chapter
  body mutations, editor behavior, annotations, styles, and authoring history.
- [providers-background.md](providers-background.md) owns LLM/search provider
  configuration and the shared rules for provider-backed background execution.
- [composition-media.md](composition-media.md) owns image assets and canonical
  entity visual associations; this chapter owns how those associations are
  selected and projected as narrative evidence.
- [persistence-migrations-import.md](persistence-migrations-import.md) owns EF
  mappings, write coordination, schema migrations, and portable import/export.
- [version-history-sync.md](version-history-sync.md) owns deterministic project
  snapshots and restore/sync transport; this chapter owns the canonical graph,
  outline, ingest, reference, and retrieval state they capture or rebuild.

## Relevant verification

- Search every changed narrative concept across services, graph relationships,
  FTS and vector indexing, context builders, assistant tools, import/export,
  and UI consumers.
- Confirm graph mutations flow through owning services and that `RelevantTo`,
  `AppearsIn`, managed structural links, and read-only `AutoMention` behavior
  remain distinct.
- For retrieval changes, inspect both FTS5 and sqlite-vec maintenance, disabled
  embedding behavior, rebuild behavior, origin provenance, pagination, and
  exact-read scope validation.
- For reference changes, verify one-hop scope, foreign-ID rejection, active-only
  mutation targets, incoming deletion handling, canonical-visual restrictions,
  and export omission warnings.
- For ingest/import, source-evidence, archive, or history changes, add or update
  tests only where they prove approved migration/import safety or the
  deterministic portable archive/history closure and restoration contracts. The
  validation chapter owns the full permitted-test boundary; ordinary search,
  graph, context, Research, and assistant behavior is verified through builds
  and static inspection.
- Run `dotnet build Lorekeeper.sln` and the documented HTTP startup smoke check
  for normal source changes. Exercise provider calls, web access, embeddings,
  ingest models, or browser UI only when that integration is explicitly in
  scope, and report anything not exercised.
