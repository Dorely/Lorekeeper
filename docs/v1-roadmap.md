# Lorekeeper v1: technical roadmap and release specification

Status: **Approved plan; M0 execution in progress**

Approved: 2026-09-16

Last updated: 2026-09-16

Planning baseline: v0.3.17, commit `d119615`

This is the maintained execution plan for the v1 application release. Work through
it in bounded steps across sessions; approval of this document does not mean its
features, platform claims, or release gates are implemented or verified.

[VISION.md](../VISION.md) remains the product direction. The
[architecture map](architecture.md) and its routed chapters describe current
contracts; code and recorded verification establish actual behavior. The
[publication artifact roadmap](publishing-roadmap.md) retains the broader
book-production roadmap. This document owns v1 application scope, sequencing,
release decisions, and progress, and supersedes conflicting v1 scope in the
[historical DOCX plan](plans/deferred-docx-interchange.md).

## 1. Current handoff

Start here when resuming work, then read the relevant specification below.

| Field | Current value |
|---|---|
| Active milestone | M0 - Establish release foundations (In progress; M0.3 local baseline measurement is blocked) |
| Next bounded step | M0.3 - Restore a local native-Electron input surface, then execute the approved five-warm-up/30-sample measurement protocol using the existing isolated Release package runner |
| Completed implementation | M0.1 - Aligned the approved focused, deterministic regression-test policy across repository instructions and current validation guidance; M0.2 is superseded by the owner’s local-validation decision |
| Next investigation | Resolve the missing native-app control surface or use an explicitly authorized local operator for M0.3; do not substitute browser automation, simulated UI input, or hosted CI for the packaged Electron measurement |
| Known external dependencies | Legal/dependency review, Store accounts/publisher identity, hosted Mac builds and real-device testers, Buy Me a Coffee URL, final Store price |
| Scope boundary for the next session | Complete the blocked M0.3 measurement, update its evidence, and stop before beginning M0.4 unless the user expands that session's scope |
| Validation evidence | M0.1 (2026-09-16) aligned the policy documents and this handoff; its focused documentation commit records local-reference checks, stale-policy search, full commit-gate, and HTTP-startup-smoke evidence. M0.3 tooling generated deterministic fixtures, built and started an isolated unsigned Windows Release package, and captured package/host/memory artifacts, but no UI timing sample was recorded because this environment exposed no native-app control target. No performance baseline, Store feasibility, or external integration acceptance is established |
| Workspace/branch | Rediscover with Git; do not treat the planning baseline as current HEAD or infer a clean checkout from this document |

The user explicitly approved expanding focused regression coverage for
DOCX/citations, Undo/concurrency, provider configuration, and release-channel
logic, while avoiding broad brittle UI suites. The accepted specification also
requires shared-page and portability regressions. M0.1 records those precise
boundaries in the owning policy documents before adding such tests. This is an
already approved scope change, not a reason to ask for the same approval again.
It does not authorize browser sessions, paid provider calls, purchases, repository
publication, Store submission, or outreach merely because those appear later in
the plan; follow the user's current task and the applicable execution rules.

### Session handoff procedure

1. Follow [AGENTS.md](../AGENTS.md), read VISION and the architecture map, and
   perform repository readiness checks. Preserve unrelated work and reuse the
   existing work branch according to repository policy.
2. Read this handoff, the milestone ledger, and relevant specification. Inspect
   current code, then read all routed architecture chapters for that impact area.
3. State the selected milestone/substep, concrete deliverable, dependencies, and
   acceptance checks. Resume an active step before starting a new one. If the
   user names a different step, verify its prerequisites and update sequencing.
4. Break a large milestone into coherent substeps here before implementation.
   Keep each substep end-to-end and compatible with the repository's feature
   completion/commit rules; do not leave an obsolete runtime or a half-applied
   schema transition between commits.
5. Implement and verify only the agreed session scope. Update current architecture
   and user documentation with resulting behavior, and this plan with progress.
6. Before handoff, record exact completed work, remaining work, evidence, unrun
   checks, blockers, changed decisions, and one next executable substep. Never
   reduce the handoff to "continue where we left off."
7. Run the required gate on the final worktree, inspect and commit the coherent
   change, and report the commit and working-tree state. Do not open a PR, push a
   release, or publish the repository solely because a milestone is complete.

### How to maintain the plan

- Update this document in the same change as implementation or an accepted design
  change; it is a working ledger rather than a frozen proposal.
- Use milestone IDs M0-M8 and substep IDs such as M0.1 consistently. Mark status
  `Not started`, `In progress`, `Blocked`, `Complete`, or `Superseded`.
- `Complete` requires the entire milestone's exit criteria and linked evidence.
  A passing build is not provider, UI, platform, migration, or Store evidence.
  Completed substeps do not automatically complete their parent milestone.
- A blocker names the missing evidence/input, its effect, the next action, and
  which independent work can proceed. Waiting on Mac must not silently block a
  separately ready Windows release.
- Keep the specification below describing the latest accepted target design.
  When it changes, edit the affected text and add a dated decision-log entry with
  the old decision, new decision, reason, scope impact, and approval/evidence.
  Preserve the historical record without leaving contradictory active guidance.
- Routine implementation refinements may be made from code evidence. Changes to
  accepted product scope, licensing, platform support, author data guarantees,
  paid/free behavior, or external publication require a user decision; do not
  quietly cut features to hit a date.
- Record actual schema/protocol/export versions when their changes land. Allocate
  from current code, not the original baseline, and never reuse a version for
  distinct shipped formats. Preserve applied EF migrations.
- Link detailed implementation/research documents when needed; do not duplicate
  current architecture or create a competing status ledger in those documents.
- Record command, date, platform, fixture, outcome, and tested revision/worktree
  for evidence. For evidence in the same commit, say "this change" with its date
  and commit subject; record the SHA in the final handoff or a later update rather
  than amend a commit merely to write its own hash into itself.
- Recheck time-sensitive model metadata, dependencies, licenses, Store policies,
  fees, and community rules at implementation/submission time; keep sources and
  the adopted versions or review dates with the resulting work.

### Progress and evidence ledger

| ID | Status | Deliverable | Exit condition | Evidence / remaining work |
|---|---|---|---|---|
| M0 | In progress | Test policy, local performance fixtures, licensing inventory, Windows MSIX and Mac sandbox feasibility | Major packaging constraints identified; baseline measurements recorded | M0.1 complete; M0.2 superseded; M0.3 measurement blocked pending a native local Electron input surface |
| M1 | Not started | Preconfigured OpenAI account models and external-browser OAuth | Fresh connection reaches usable chat without manual model setup or Test clicks | No implementation/integration evidence |
| M2 | Not started | Project page library, shared placements, release overrides, safe migration | Existing content survives; shared editing and release isolation work | No implementation/migration evidence |
| M3 | Not started | Immediate manual Undo/Redo, recoverable save queue, incremental history | Latency and failure-recovery gates pass | Measure baseline first |
| M4 | Not started | Full source readers, retained originals, stable evidence links, scalable portability | Dozens of books remain readable, searchable, exportable, and restorable | No scale/portability evidence |
| M5 | Not started | Rich manuscript, citations/bibliographies, Word paste/file import, DOCX export, output parity | Compatibility fixtures pass across editor, DOCX, EPUB, and Press | Depends on M2-M4 contracts |
| M6 | Not started | Public repository, donations, onboarding, beta, targeted cleanup | Beta exit criteria pass without unresolved data-loss or release-blocking defects | Publication still requires its explicit execution step |
| M7 | Not started | Free Windows downloads and paid Microsoft Store edition | Clean install, upgrade, migration, and Store update evidence passes | Requires M0-M6 and Windows platform gates |
| M8 | Not started | Notarized free Mac download and paid Mac App Store edition | Real-device and sandboxed Store validation passes | Cloud-only Mac access initially; recruit device testers |

Initial M0 substeps (split further only when current-code findings justify it):

- [x] **M0.1 - Test-policy alignment (complete 2026-09-16).** Updated the owning
  instructions and validation guidance to the approved focused-regression scope,
  reconciled companion references, retained the full gate, and did not grant
  blanket UI or external-service execution permission.
- [x] **M0.2 - Superseded (2026-09-16; owner declined general hosted CI).** Do not
  add GitHub CI, PR-validation workflows, or non-macOS GitHub compute. The
  dispatch-only macOS release builder remains the sole hosted workflow; local
  commit-gate evidence and approved focused regressions remain required until an
  owner decision changes this boundary.
- [ ] **M0.3 - Local performance fixtures and baseline (blocked).** The local-only
  fixture generator, trace, Release-package runner, and reference-environment
  report are implemented. The first isolated Windows Release launch succeeded,
  but its timestamp-only trace has no samples because the available control
  surface exposes no native app target. Resume with a local native Electron
  input surface; load fixtures only through the production import UI, execute
  five warm-ups and 30 samples per metric, then update the evidence before
  marking this step complete.
- [ ] **M0.4 - License and distribution inventory.** Inventory code/assets and
  dependency obligations, publisher/account prerequisites, and public-history
  review work. Do not adopt the candidate license or publish anything merely to
  complete the inventory.
- [ ] **M0.5 - Windows MSIX feasibility.** Validate the actual packaged process
  tree, data paths, OAuth/printing/import/export boundaries, and update-channel
  separation before relying on Store distribution.
- [ ] **M0.6 - Mac sandbox feasibility.** Build/test the actual Electron/.NET/
  Press process tree under the MAS constraints; record entitlement/signing
  findings and real-device evidence still required. An unavailable account or
  runner is a named blocker, not a successful feasibility result.

M0.4-M0.6 may proceed alongside independent work after their prerequisites are
met. They remain explicit release gates; do not fabricate evidence or skip them
because the feature milestones are ready.

### Decision and change log

| Date | Decision/change | Reason and effect | Authority/evidence |
|---|---|---|---|
| 2026-09-16 | Approved the v1 scope and specification recorded below | All six feature areas precede v1; staged Windows/Mac launches; public beta first | Owner approval in the v1 planning conversation |
| 2026-09-16 | Recorded stepwise handoff and maintenance workflow; marked the older DOCX proposal superseded | Preserve the accepted plan across sessions without competing implementation instructions | Owner requested a maintained repository plan and a separate starting prompt |
| 2026-09-16 | Superseded M0.2 and declined general hosted CI | Keep general repository gates and focused regressions local; retain only the dispatch-only macOS release builder because it provides required native Mac compute | Owner decision |
| 2026-09-16 | Defined M0.3 as a local Windows Release reference baseline, not a hardware minimum | The documented 32 GiB Windows machine is a comparison environment only; latency targets remain v1 goals and do not establish macOS, Store, updater, provider, or large-library performance | Owner-approved M0.3 scope; [local evidence](evidence/m0.3-local-performance-baseline.md) |

## 2. Release definition and settled scope

**V1 means all six feature areas are complete, existing projects migrate safely,
normal authoring is responsive, and the supported distribution passes its
release gates.** Quality and scope determine the date.

Windows may launch first. Mac follows when its packaging, sandbox, and Store
validation succeed. The six requested areas are DOCX, sources/citations,
independent Designed Pages, Undo/Redo, OpenAI configuration, and external OAuth.

| Area | V1 decision |
|---|---|
| Windows | Windows 10 22H2 and Windows 11, x64 |
| macOS | Apple Silicon, macOS 14+, including a paid Mac App Store edition |
| Free distribution | GitHub downloads; update notifications and manual installation |
| Paid distribution | One-time Store purchase; Store-managed updates; identical writing features |
| Price | Owner decision before submission; approximately $10 remains the starting point |
| License | Apache-2.0 plus Commons Clause candidate, subject to legal/dependency review |
| Public access | Public source and free beta before v1 |
| Language | English UI and the current Latin-script, left-to-right publishing scope |
| Testing | Expand focused regression coverage beyond the current migration/Press restriction |
| Excluded | Additional premium features, subscriptions, license servers, Intel Mac, Windows ARM, Linux release support, new OCR, arbitrary Word layout replication |

Describe Lorekeeper as **source available**, not open source, if Commons Clause
is adopted. Its restrictions concern selling software-derived offerings; the
license must not impose restrictions on manuscripts and other author-created
content. Commons Clause is broader than a simple prohibition on reselling forks,
so its exact fit needs review before publication. See the
[license and FAQ](https://commonsclause.com/).

## 3. Sequencing and integration

Follow M0-M8 in the ledger, with packaging feasibility running early alongside
independent feature work. Open the public beta after M5 and repository-readiness
checks; recruit testers earlier. Windows and Mac have independent final gates.

Each milestone produces coherent, verified commits on the reusable work branch.
Structural formats advance separately when needed; do not assign the same schema
version to two independently shipped changes. Preserve all consumers at each
boundary rather than letting editor, renderer, and stored forms drift.

The initial code baseline uses manuscript v4, project export v30, history
snapshot v6, Press protocol v12, and agent-manuscript v1. These are historical
planning facts, not reserved version numbers for future work. M2 and M5 must
coordinate their shared schema/renderer/projection changes without assuming
they will land in one version bump.

Read the matching chapters through the architecture routing table for every
step. In particular, source/page/manuscript changes also reach persistence,
portable import/export, version history, assistants, indexing, and publishing;
release tooling reaches runtime and validation. Existing data preservation and
all owning-service mutation boundaries remain mandatory.

## 4. Technical specification

### A. OpenAI configuration and external OAuth (M1)

#### Account ownership

Introduce an `OpenAiAccount` credential owner. Keep existing `LlmProvider` IDs as
selectable model identities so conversations and jobs retain their selections.

- Account-backed model rows reference the account instead of copying credentials
  or depending on the `openai-codex` name.
- Catalog models and manually added models remain distinguishable.
- Preserve API-key, custom, and local-provider configuration.
- Migrate tokens, credential-sharing relationships, model selections, and explicit
  overrides atomically. Do not collapse distinct saved configurations merely
  because their model IDs match.

#### Preconfigured catalog

Ship a versioned catalog containing model IDs, names, supported/default efforts,
capabilities, and effective context budgets. Record its source and validation date.

- Use metadata verified for the **Codex account transport**; API context-window
  documentation alone is insufficient.
- Refresh account metadata automatically after connection and reconcile it with
  the bundled catalog.
- Preserve a usable bundled/last-known catalog when discovery fails; show
  availability failures clearly without demanding manual configuration.
- Keep the existing preferred default where supported. Never silently substitute
  a more expensive model for an unavailable explicit selection.
- Validate effort choices against each model's supported values.
- Distinguish advertised context capacity from Lorekeeper's usable input budget.
  Capture the resolved budget and model configuration at turn start.
- Catalog models require no manual Test action. Connection state and actual model
  entitlement remain separate; authorization failures must remain actionable.
- Manual model entry stays available under advanced configuration.

#### OAuth handoff

Add one `IExternalAuthorizationLauncher` used by OpenAI and GitHub.

- Electron opens generated, validated authorization URLs through the system browser.
- Browser hosting uses an explicit user-activated external link.
- Keep Lorekeeper's settings surface mounted throughout authentication.
- Preserve PKCE, cryptographically random state, single-use callbacks, expiration,
  cancellation, and serialized token refresh.
- The callback browser page reports completion and tells the user to return to
  Lorekeeper; it does not open another app session.
- Settings observes completion and refreshes automatically.
- Keep GitHub device authorization, but explicitly open its verification page externally.
- Port conflicts produce an application-owned explanation; never terminate another process.

Acceptance includes fresh connection, cancellation, denial, expiration,
reconnect, refresh failure, unavailable models, and simultaneous stale callbacks
on both supported platforms.

### B. Independent Designed Pages (M2)

#### Ownership model

Replace chapter/section ownership with:

| Entity | Responsibility |
|---|---|
| `DesignedPage` | Project identity, name, optional release-only scope |
| `DesignedPageContent` | Semantic content, accessibility description, revision, Core or release override |
| `DesignedPageVariant` | Revisioned layout for a particular geometry |
| Manuscript Designed Page block | One placement, identified by its stable block ID |

A shared page has one Core content record and at most one override per release.
Release-only pages remain scoped to their release.

#### Placement behavior

- Place again: new placement ID, same page.
- Move: preserve placement and page identity.
- Duplicate page: create independent content and layout identities.
- Remove placement: retain the page.
- Unplaced pages persist across navigation, restart, chapter deletion, and section deletion.
- Explicit deletion is blocked by live placements. History-only dependencies
  require the existing application confirmation and coherent history cleanup.

Maintain a derived placement-reference index for reverse lookup. Manuscript
documents remain authoritative; rebuild and verify the index during migration,
import, and restore.

#### Release behavior

The first page edit in a release creates one page-level override. Every placement
in that release uses it.

- Editing page content does not materialize a chapter override.
- Chapter customization retains shared page IDs.
- Resetting a page override returns every placement in that release to Core.
- Resetting one chapter does not delete page overrides used elsewhere.
- Release cloning copies overrides and remaps release-local pages.
- Core geometry changes must not overwrite release-specific authored layouts.

#### Consumers and history

- Page edits invalidate every affected placement's preview, search projection,
  context, and publication fingerprint.
- Unplaced page edits do not stale publication output; standalone pages are searchable.
- Repeated placements expand into reading order repeatedly, with occurrence-specific anchors.
- EPUB spine IDs and Press mappings use placement identity, avoiding duplicate IDs.
- Chapter Undo stores placement changes only; page Undo owns shared page content.
- Review displays a shared page change once, with links to its placements.
- Cross-container moves are atomic compound history operations.

Add a project Pages workspace with Core/release selection, placement counts,
unplaced status, duplicate/place/reset commands, and the existing canvas editor.
Existing chapter Pages mode becomes a filtered entrance to that workspace.

#### Migration

Preserve existing page, block, scene, and variant identities where possible.
Every existing release clone becomes an explicit frozen override, even when
currently identical to Core. Never merge divergent legacy clones; preserve them
as independent release-local pages.

### C. Fast manual Undo/Redo and reliable saving (M3)

Manual history is already in memory at the planning baseline. The redesign
removes whole-document work and server waits from ordinary Undo.

#### Client-first operations

Use a shared batch contract containing target identity, base revision/fingerprint,
client session and batch IDs, ordered semantic operations, and selection information.

- ProseMirror applies Undo/Redo immediately using local inverse operations.
- Canvas history uses object/property deltas.
- A serialized background queue persists accepted changes.
- Save acknowledgments advance the confirmed revision without replacing the visible document.
- Typing coalesces; paste, formatting, and structural actions create explicit boundaries.
- Rare complex edits may use bounded snapshot entries; ordinary typing must not.

The server validates and atomically applies batches through existing owning
services. Maintain idempotency receipts so a lost acknowledgment cannot apply
an operation twice.

#### Recovery

- Store unacknowledged work in a target-scoped local recovery journal.
- A network/database failure pauses saving while retaining the local document.
- Reload recovers pending work; confirmed history remains process-lifetime history.
- A stale save must never silently replace dirty local content.
- Automatically rebase only provably non-conflicting operations. Structural overlap
  or ambiguous changes retain both versions and open an application-owned recovery surface.
- Assistant changes remain outside manual Undo and invalidate affected history generations.
- Preserve the existing 100-action/128 MiB history bounds; account for delta
  dependencies incrementally.

Navigation, publishing, checkpoints, and assistant mutations flush or explicitly
resolve pending edits before consuming their state. Failed flushes stop the
dependent action.

### D. Source documents, evidence, and bibliography records (M4)

#### Library and extraction

Extend existing ingest ownership rather than creating a competing corpus.

Support PDF, EPUB, DOCX, text/Markdown, and saved webpages. Retain immutable
originals and versioned normalized extraction.

- Stage and hash uploads outside database write locks.
- Keep the existing 100 MiB per-input limit.
- Store original bytes in ordered, content-addressed 8 MiB chunks.
- No small aggregate project quota: design and verify for roughly 50 books and a few GB.
- Retain the original before extraction; show separate retained/extracting/ready/failed states.
- Local extraction and lexical indexing require no AI connection.
- AI summaries, graph enrichment, and embeddings are separately queued optional work.
- Scanned documents remain available in original-page view; do not claim searchable
  text without extraction/OCR evidence.
- Re-extraction creates a new version rather than changing existing citation anchors.

#### Reader

Provide a dedicated Sources workspace with formatted reading, contents navigation,
search, exact evidence highlighting, and original download.

For PDFs, add original-page viewing through an application-owned surface using
bounded page rendering. Exact text highlights belong to the normalized reading
view; original-page links navigate to the relevant page.

A shared `SourceLocation` carries source ID, extraction version, block/page
identity, normalized range, locator, and quote evidence. Use it in search
results, graph provenance, assistant citations, and manuscript citations.

Assistant links resolve through validated source locations. Persist citation
link metadata with the visible response so links survive transcript reload.
Missing or ambiguous evidence displays an unavailable/outdated state rather
than guessing a passage.

#### Bibliographic identity

Add project-owned `BibliographicRecord` entities independent of source files.
Authors can cite a book without uploading it.

V1 supports books, chapters, journal/magazine/newspaper articles, webpages,
reports, and theses, with editable bibliographic fields.

- A record may link to a retained source.
- Manuscript citation occurrences carry locators and optional source evidence.
- Source deletion shows usages and blocks until resolved or explicitly detached.
- Detaching retains bibliographic metadata and visibly marks evidence unavailable.
- Source documents remain read-only; metadata corrections do not rewrite evidence.

M4 establishes bibliographic identity and source evidence. The manuscript atoms,
formatted citations, and generated bibliography land with M5, using these same
records rather than introducing another source authority.

#### Portability

Replace new project exports with a streamed, versioned `.lorekeeper` archive
containing manifests, structured records, and binary chunks.

- Full export includes all project sources.
- Non-structural exports preserve dependencies of included content and report
  omitted source evidence.
- Export/import runs as reconnectable jobs using controlled temporary files and
  streaming HTTP transfer, not Base64 through SignalR.
- Validate paths, declared lengths, hashes, archive expansion, duplicate entries,
  and references before admitting imported state.
- Retain old JSON formats only as versioned import boundaries.

Version history gains small source indexes, per-source manifests, and bounded
original/text blobs. Comparison reads metadata lazily; restore streams and
validates content. Show new/reused bytes and large-sync feedback. Chunking avoids
oversized individual blobs but does not remove hosting limits or the cost of
multi-GB history.

### E. Rich manuscript, citations, and DOCX (M5)

#### Canonical manuscript additions

Extend the semantic manuscript with:

- Tables containing stable rows/cells, spans, and block content.
- Document-owned footnotes/endnotes.
- Inline note-reference and citation atoms.
- Citation clusters containing one or more bibliographic references.

Table cells support paragraphs, lists, and figures. Nested tables and Designed
Pages inside cells are outside v1; import reports them explicitly.

All nested blocks retain stable identities. Define one shared position-mapping
contract for editor selections, annotations, atoms, table cells, and notes;
displayed citation numbering must not change stored anchor positions.

Update validation, operations, assistant projections, search, Undo, release
customization, import/export, history, and rendering together.

#### Citation formatting

Support Chicago notes/bibliography, APA, and MLA through one offline CSL formatter.

Use pinned citeproc-js in constrained Jint, with bundled styles/locales and
deterministic conformance fixtures. Disable network, filesystem, and general CLR
access. Arbitrary user-supplied CSL styles are excluded.

The selected processor's CPAL obligations, including covered-source availability
and notices, must pass dependency review before distribution. See
[processor license metadata](https://raw.githubusercontent.com/Juris-M/citeproc-js/master/package.json)
and [CPAL terms](https://opensource.org/license/cpal-1.0).

Core owns the citation style; releases may override it. Generate numbering and
bibliography from effective included content, deduplicating works. Missing
required metadata produces actionable diagnostics. Inserting an assistant
evidence link does not automatically create a manuscript citation.

#### DOCX input

Use the [Open XML SDK](https://learn.microsoft.com/en-us/office/open-xml/open-xml-sdk)
for file import/export and a Word-aware HTML clipboard adapter feeding the same
semantic import-fragment contract.

- Support Word desktop paste on Windows and Mac.
- Preserve supported headings, paragraph formatting, lists, tables, images,
  links, notes, and citation data.
- Direct file import inserts at an explicit manuscript location; it does not
  silently replace a chapter or infer chapter splits.
- Import final revised text. Omit Word comments and revision history, with a visible report.
- Convert imported images into project assets atomically with insertion.
- Map styles to existing equivalents or create distinct imported styles without
  overwriting existing definitions.
- Preserve unresolved Word citation display text and report missing metadata.
- Do not automatically add an imported manuscript to Sources.

Clipboard fidelity is limited by the data Word supplies. When important content
is missing from the clipboard, direct DOCX import is the recovery path.

Bound ZIP/XML expansion and reject unsafe relationships, executable content, or
malformed packages. Unsupported layout features produce a reviewable import
report rather than silent omission.

#### DOCX output

Add DOCX to the existing effective-publication export boundary.

- Export Core or the selected release, including publication sections and effective content.
- Prose, tables, lists, figures, notes, and bibliography remain editable Word structures.
- Designed Pages become clean page-artwork images with accessibility descriptions.
- Preserve semantic styles and image captions.
- Validate the generated package and relationships.
- Report layout limitations; do not label DOCX print-ready or promise identical Word pagination.

#### Existing outputs

Tables, notes, and citations must also work in Read, EPUB, PDF, and text projections.

Extend Press with table pagination, repeated headers, note layout, endnotes,
and appropriate tagged structure. Unplaceable content yields a named diagnostic.
EPUB gets semantic tables and linked notes. DOCX support is incomplete until
these existing paths preserve the richer manuscript.

### F. Engineering cleanup (throughout; closure in M6)

Concentrate cleanup on measured problems and changed boundaries:

- Remove chapter/page ownership assumptions and duplicated projection logic.
- Remove whole-library loading, Base64 exports, and whole-history rescans.
- Keep parsing, formatting, and persistence out of Razor components.
- Standardize cancellation, stale-result protection, save-state presentation,
  and background-job recovery.
- Audit loopback endpoints, untrusted document parsing, log redaction,
  temporary-file cleanup, and dependency notices.
- Improve keyboard navigation, focus restoration, empty/error states, and
  consistent terminology across the affected surfaces.
- Keep diagnostics local; support bundles require explicit export and exclude
  credentials and manuscript content by default.

## 5. Verification and v1 acceptance

M0.1 updates repository instructions to permit focused regression tests for these
contracts. Retain the full repository commit gate and Press evidence requirements.
Use the current commands from [AGENTS.md](../AGENTS.md) and
[validation guidance](architecture/validation-documentation.md); do not copy a
second gate into this plan that can drift.

### Data preservation

Every structural migration must prove preservation of manuscript text, stable
IDs, page scenes, release isolation, source evidence, and assets. Test
rollback/recovery with malformed and interrupted input.

Use copies of working databases. Never use the developer's original database as
a migration test target.

### Required regression coverage

- DOCX fixtures covering styles, lists, merged multi-paragraph cells, images,
  links, notes, citations, and discarded Word review markup.
- Citation golden outputs, missing metadata, repeated references,
  release-specific bibliography, and offline operation.
- Shared-page placement, duplication, release override/reset, deletion,
  indexing, artifact invalidation, and historical restore.
- Undo inverse correctness, grouping, failed persistence, duplicate delivery,
  reload recovery, and assistant concurrency.
- Catalog reconciliation, effort/context validation, credential migration,
  and zero-manual-test readiness.
- Full/non-structural package closure, legacy import, chunk integrity,
  and streaming restoration.
- Store/free channel selection and updater behavior.

### Performance gates

Measure production builds using a fully documented local reference environment
and fixed fixtures. The current M0.3 reference is the local Windows 10 Pro
10.0.19045 (build 19045), AMD Ryzen 7 9700X, 16-logical-processor, 31.6 GiB
machine; it is a comparison environment, not a minimum hardware requirement.

| Workflow | Target |
|---|---|
| Ordinary prose Undo/Redo | p95 <=100 ms from command to visible document/selection update |
| Normal typing | No database wait or full-document serialization on the input path |
| Warm chapter/source navigation | p95 <=1 second to usable content |
| Local source-search results | p95 <=1 second on the 50-book fixture |
| Normal autosave | p95 <=1 second to acknowledgment without competing heavy work |
| Large import/export/history | Bounded streaming memory, progress and cancellation; no allocation proportional to the complete library |

Include a 250,000-word project, a 1 MiB/10,000-block editor stress document,
shared/release pages, and a 50-source/few-GB library. These are acceptance
targets, not current measurements. M0.3 supplies the two v30 project fixtures
and an opt-in manifest-defined 50-source/3.125 GiB library, but the present
64 MiB full-memory import UI cannot establish few-GB source-library, streaming
import/export, history, or M2 shared-page results; those remain M2/M4 work.
Record hardware, OS, build, sample counts, warm/cold conditions, background
activity, and measurement method with results.

### Beta exit

Require:

- No unresolved data-loss, migration, credential-exposure, or installation/update blockers.
- Successful clean install and upgrade from v0.3.17.
- Successful full-project export/import and history restore.
- Representative author workflows completed by beta testers.
- At least two weeks on the release candidate without a new critical defect;
  a critical fix restarts that observation period.
- Exact-platform evidence for OAuth, Word compatibility, publishing, packaging,
  and updates. Windows evidence does not establish Mac readiness.
- Current documentation and a reviewed complete diff.

Run the local commit gate and approved focused contract regressions; use targeted
human/UI validation for actual desktop and external integrations. General hosted
CI is owner-declined; record unperformed checks explicitly.

## 6. Distribution, public repository, and launch (M6-M8)

### Windows

Build a Microsoft Store MSIX edition and separate GitHub installer/portable
artifacts. MSIX provides the Store signing, commerce, and update route the paid
edition needs. An EXE listing would leave those responsibilities with the
publisher. See [Microsoft's distribution comparison](https://learn.microsoft.com/en-us/windows/apps/distribute-through-store/how-to-distribute-your-win32-app-through-microsoft-store).

- Store builds use Store updates exclusively.
- Free Windows artifacts remain unsigned, as selected.
- Free builds notify and link to GitHub but never download/install updates automatically.
- Encode distribution channel in build metadata, not a user-editable premium flag.
- Keep development, beta, free stable, and Store update behavior explicit.

### Mac

Maintain separate Developer ID and Mac App Store packaging.

- Free DMG: Developer ID signing, hardened runtime, notarization, and stapling.
- Store package: MAS Electron build, App Sandbox, correct entitlements,
  provisioning, and nested executable signing.
- Validate the .NET host, Press, SQLite/native libraries, loopback callback,
  printing, imports, exports, and network access inside the sandbox.
- Freeze native binaries before generating integrity manifests; sign the final
  outer bundle afterward.
- Use hosted Mac builds initially, plus recruited real-device testers.
- Transfer between direct and sandboxed installations through explicit project
  export/import; never silently move user data.

The MAS feasibility milestone must demonstrate the actual process tree under
sandboxing. If it fails, stop that channel for a revised design; Windows remains
independently releasable. See [Electron MAS requirements](https://www.electronjs.org/docs/latest/tutorial/mac-app-store-submission-guide)
and [Apple direct-distribution guidance](https://developer.apple.com/developer-id/).

Budget Apple Developer membership at the US$99/year listed during planning,
plus hosted build/test costs; recheck before enrollment. Exact Store pricing
remains an owner-supplied release input. See [Apple enrollment](https://developer.apple.com/programs/enroll/).

### Retire Lorekeeper-Releases safely

1. Prepare and make the main repository public.
2. Establish releases and update metadata there.
3. Publish one final bridge release through the old feed so existing installations
   can reach the new distribution behavior.
4. Test the transition from an installed v0.3.17.
5. Stop dual publication and archive the old repository, retaining historical
   assets and a migration notice.

Do not delete the old feed while installed versions still depend on its final bridge.

### Public repository and donations

Before publication:

- Audit tracked files and Git history for secrets, private data, and redistribution rights.
- Resolve findings before changing visibility; do not automatically rewrite history.
- Add the approved license, third-party notices, contribution terms covering
  official commercial distribution, security reporting, issue templates,
  support policy, and build instructions.
- Keep repository-gate and focused-regression evidence local; do not add general
  GitHub CI or PR validation unless the owner explicitly revisits the decision.
- Configure GitHub's funding button and README donation link using the owner's
  verified Buy Me a Coffee URL.
- Keep donations optional and unrelated to feature access.
- Put donation links on GitHub and the project website; avoid introducing an
  external payment workflow into the Mac Store app.

### Small launch campaign

Recruit fiction and research-heavy nonfiction writers with one short demo:
import Word prose, follow a source citation, revise, and export.

Start with the current **r/WritingWithAI Weekly Tool Thread**, which permits
launches, demos, and feedback requests there. Disclose authorship and link the
free beta. The [thread inspected during planning](https://www.reddit.com/r/WritingWithAI/comments/1vrfkr7/weekly_tool_thread_promote_share_discover_and_ask/)
is evidence of the venue's rules, not the thread to post in indefinitely.

Use the **AI Forums promotion board** as a secondary venue after satisfying its
participation requirements. Recheck rules immediately before posting. See
[promotion guidelines](https://aiforums.co/threads/promotion-rules-guidelines-read-before-posting.37/).

Gather feedback through GitHub issues/discussions, publish concise beta change
logs, and announce Windows and Mac separately when each passes its gates. No
outreach or posts are sent without explicit authorization.

### Owner-supplied publication inputs

- Final Store price.
- Publisher/legal identity and developer accounts.
- Verified Buy Me a Coffee URL.
- Approval of reviewed license and contribution terms.
- Real-device Mac testers and required Store/signing access.

These inputs are publication dependencies, not excuses to stop independent
engineering work or ask again about already settled product decisions.
