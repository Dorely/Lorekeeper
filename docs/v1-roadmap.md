# Lorekeeper v1: technical roadmap and release specification

Status: **Approved plan; M0, M1, and M3 execution in progress; M2 complete**

Approved: 2026-09-16

Last updated: 2026-09-17

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
| Active milestone | M5 - rich manuscript, citations, bibliography, Word interchange, and output parity; M0, M1, M3, and M4 remain in progress only for their recorded native, installed-package, OAuth, performance, scale, and Mac evidence |
| Next bounded step | Finish verification and preservation/output review of the unfinished M5B citation and DOCX-export work, then implement semantic DOCX/Word import and complete the remaining feature cleanup before manual acceptance |
| Completed implementation | M0.1 aligned focused regression policy; M0.2 is superseded by the owner’s local-validation decision; M0.4 recorded the local distribution inventory and future M6 history-audit procedure; M0.5 added the deterministic update-channel policy and local full-trust MSIX preflight. M1.1 introduces account-owned OpenAI credentials with a guarded forward migration. M1.2 adds a fully local catalog schema v1 for Astra/Sol/Terra/Luna, rejects account-backed model discovery, resolves catalog efforts/capabilities/usable budgets, provides zero-Test catalog readiness, and preserves fail-closed explicit selections. M1.3 replaces redirect-based authorization with account services, a single-use process-local flow registry, serialized refresh/credential replacement, callback-origin validation, a shared allowlisted OpenAI/GitHub external launcher, mounted Settings polling, and a standalone callback page. A focused responsiveness correction also removes provider-backed semantic indexing from blank project creation and gives the Create action immediate busy feedback. M2 replaces chapter/section-owned compositions with reusable Designed Pages, Core/release content and authored layouts, scoped placement references, release override isolation, a project Pages workspace, independent review/history, guarded migration, and current adapters across search, assistants, portability, EPUB, and Press. M3 adds versioned multi-target authoring batches and journals, transactional sessions and durable receipts, exact-precondition conflicts, process-owned bounded delta history, client-first Undo/Redo, recoverable IndexedDB queues, single-writer leases, mutation fences, and delta-based canvas/page/cover editing. M4 adds immutable retained originals, versioned extractions, durable evidence and bibliography identity, the Sources workspace, DOCX source extraction, streamed `.lorekeeper` archives, staged transactional import, policy-scoped dependency traversal, and history schema v8 source manifests and blobs. M5A adds manuscript v6 recursive tables and document-owned notes, UTF-16 positions, rich editor/history replacement, agent projection v3, archive record 2, history schema 9, EPUB exporter v5, and Press protocol 14 table/note pagination |
| Next investigation | Complete semantic Word file/paste conversion and atomic insertion, then close remaining bounded archive/history/source loading and feature usability gaps. Current M5B code includes managed citations, editable DOCX export, and reference-aware Press footnote reservation; acceptance remains outstanding. Retain M1 live integration, M3 latency, M4 native scale, and platform-specific evidence for the later authorized validation pass |
| Known external dependencies | Owner source-license decision, unresolved dependency/asset provenance, Store accounts/publisher identity, hosted Mac builds and real-device testers, Buy Me a Coffee URL, final Store price |
| Scope boundary for the next session | Implement deterministic M5B code, migrations, independently authored citation/DOCX fixtures, approved conversion/output regressions, and documentation. Do not claim Word pagination/fidelity without Word desktop evidence; do not collect native performance evidence, use live OAuth/provider calls, install an MSIX, create a Store submission, create MAS signing assets, or alter the dispatch-only macOS workflow without separate authorization |
| Validation evidence | M0.1 (2026-09-16) aligned policy documents and this handoff. M0.3 tooling generated deterministic fixtures and launched an isolated unsigned Windows Release package but recorded no UI timing sample because this environment exposed no native-app control target. M0.4 generated deterministic JSON/Markdown inventory from restored dependencies and a locally built unsigned Windows closure; it recorded license and provenance gaps without selecting terms, scanning history, signing, publishing, or exercising a Store integration. M0.5 policy tests and a local Store-channel full-trust MSIX preflight establish build metadata, package contents, and CMS signature integrity only. M1.1-M1.3 add deterministic migration rollback, static catalog manifest and account-discovery rejection, zero-Test readiness, catalog-owned usable budget, fail-closed selection, authorization cancellation/denial/expiry/single-use/concurrency, refresh serialization/classification, allowlist, callback-origin, and completion-page regressions plus build/startup smoke evidence; they do not establish live OAuth, provider acceptance, browser UI, or Electron behavior. The project-creation responsiveness correction passes the same-change repository gate and browser-host startup smoke, but has no browser/Electron interaction timing because UI automation was not authorized. M2 (2026-09-17) adds guarded/idempotent migration and recovery coverage, release clone/isolation, scoped placement-index restoration, compound cross-container history, JSON v31/manuscript v5/history v7/Press v13 compatibility, semantic-editor build evidence, the full repository gate, and HTTP startup smoke. M3 (2026-09-17) adds deterministic batch hashing/reduction, transactional receipt replay and rollback, multi-target Undo/Redo, recovery-journal, exact-conflict, writer/fence, migration, semantic-editor JavaScript, full-gate, and HTTP-startup evidence. M4 (2026-09-17) adds retained-source migration and re-extraction coverage, archive closure and adversarial validation, streamed staging, commit/index recovery, history-v8 preservation and restore, DOCX source-extraction safety, full-gate, and HTTP-startup evidence; it does not establish native 50-source navigation or multi-GB portability performance. M5A (this 2026-09-17 change) adds v5-to-v6 and direct legacy migration coverage, recursive table/note validation, exact rich authoring/inverse behavior, output semantics, EPUB v5, and Press v14 conformance. Exact reference-page footnote reservation remains open; no browser interaction or native latency/platform evidence is claimed. M0.6 has documented prerequisites only: no MAS runtime, entitlement, signing, device, or workflow evidence exists |
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
| M0 | In progress | Test policy, local performance fixtures, licensing inventory, Windows MSIX and Mac sandbox feasibility | Documented local package constraints and target-specific evidence | M0.1/M0.4 complete; M0.2 superseded; M0.3 remains blocked pending a native local Electron input surface; M0.5 remains blocked pending authorized installed-package/OAuth evidence; M0.6 requires Apple account, signing, and host inputs |
| M1 | In progress | Preconfigured OpenAI account models and external-browser OAuth | Fresh connection reaches usable chat without manual model setup or Test clicks | M1.1-M1.3 deterministic implementation is complete; authorized live OAuth/provider plus browser-hosted and Electron evidence remains |
| M2 | Complete | Project page library, shared placements, release overrides, safe migration | Existing content survives; shared editing and release isolation work | Guarded migration/recovery, shared-placement, release-isolation, history/restore, import/export, Press, full-gate, and startup evidence in the 2026-09-17 implementation change; browser interaction was not exercised |
| M3 | In progress | Immediate manual Undo/Redo, recoverable save queue, incremental history | Latency and failure-recovery gates pass | Deterministic implementation and headless recovery regressions are complete; native latency evidence remains blocked on an authorized Electron input surface |
| M4 | In progress | Full source readers, retained originals, stable evidence links, scalable portability | Dozens of books remain readable, searchable, exportable, and restorable | Deterministic implementation and headless archive/history/import regressions are complete; native 50-source navigation and multi-GB portability evidence remains |
| M5 | In progress | Rich manuscript, citations/bibliographies, Word paste/file import, DOCX export, output parity | Compatibility fixtures pass across editor, DOCX, EPUB, and Press | Citations, bibliography, editable DOCX export, and reference-aware Press footnotes are implemented in the unfinished M5B worktree; semantic Word import/paste, final preservation/output review, and acceptance remain |
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
- [x] **M0.4 - Local license and distribution inventory (complete 2026-09-16).**
  [`tools/distribution/Export-M0DistributionInventory.ps1`](../tools/distribution/Export-M0DistributionInventory.ps1)
  deterministically inventories restored managed, Cargo, and semantic-editor
  dependency metadata; shipped web/font/Press assets; the unsigned Windows
  `win-unpacked` closure; and account prerequisites. Its committed
  [JSON](research/m0.4-distribution-inventory.json) and
  [report](research/m0.4-distribution-inventory.md) record the absence of a
  root license/notice, Bootstrap attribution work, branding provenance, and
  native macOS closure as explicit blockers. It also defines the future M6
  mirror-and-review history audit without performing it. No terms were adopted,
  no history was scanned, and no repository, Store, signing, or publishing action
  occurred.
- [ ] **M0.5 - Windows MSIX feasibility (blocked after local preflight, 2026-09-16).**
  `DistributionChannelPolicy` makes Development update-disabled, defaults Release
  to Free manual GitHub-release/browser handoff only, and makes explicit Store
  builds Store-managed with no GitHub checker, browser handoff, or Electron
  updater. The focused deterministic production-policy tests pass. The local
  full-trust `MakeAppx` preflight packages the Store `win-unpacked` closure,
  validates required contents and its embedded CMS signer, and removes its exact
  ephemeral current-user certificate. See
  [M0.5 evidence](evidence/m0.5-windows-msix-feasibility.md). It does not install
  a package or establish Windows trust-chain, Store, process-tree, data-path,
  import/export, print, OAuth, provider, or updater evidence. Resume only with
  separately authorized disposable-profile installation and safe OAuth-test
  authority; do not simulate those boundaries.
- [ ] **M0.6 - Mac App Store sandbox feasibility (blocked, 2026-09-16).** The
  owner must first supply an Apple Developer Team ID, MAS development
  certificate/profile, test App ID, and Apple-silicon test Mac, then explicitly
  select the signed build host. Until then, there is no `mas-dev` flavor, MAS
  Electron runtime, entitlement file, Apple credential, signing configuration,
  artifact, or workflow change. The current dispatch-only macOS workflow and
  direct-DMG path remain unchanged.

  Once unblocked, add a separate `mas-dev` flavor that uses Electron’s MAS
  runtime rather than the ordinary macOS Electron build. Give the app and each
  helper separate entitlements, initially limited to App Sandbox, loopback/network
  client and server access, user-selected read/write files, and printing; include
  no embedded credential, broad filesystem entitlement, App Group, or
  library-validation exception unless a specific signed-device failure proves it
  necessary. Build only on the selected signed Apple-silicon host and capture
  sanitized signing/entitlement, Electron/.NET/Press process-tree, loopback,
  sandboxed SQLite/history, file-picker import/export, print-handoff, and Store
  updater-suppression evidence. OAuth and external services remain separately
  authorized. If Press, .NET, SQLite, printing, or file access cannot work within
  that minimum sandbox, stop the Store channel and record the evidence rather
  than weakening the sandbox speculatively. See
  [M0.6 evidence and prerequisites](evidence/m0.6-mac-app-store-feasibility.md).

M0.4-M0.6 may proceed alongside independent work after their prerequisites are
met. They remain explicit release gates; do not fabricate evidence or skip them
because the feature milestones are ready.

M1 implementation substeps:

- [x] **M1.1 - Account ownership migration (complete 2026-09-16).** Added the
  account credential owner, guarded forward migration, stable provider/model
  references, account-routed runtime paths, and rollback-on-ambiguity evidence.
- [x] **M1.2 - Versioned account catalog (complete 2026-09-16).** Added catalog
  schema v1 for Astra/Sol/Terra/Luna, Sol preference, effort/capability/budget
  resolution, zero-Test bundled readiness, manual-row preservation, fail-closed
  explicit selection, and a hard boundary that rejects account-backed model-list
  discovery before any provider request.
- [x] **M1.3 - External authorization implementation (complete 2026-09-16).**
  Added account authorization/token services, single-use expiring process-local
  flow state, serialized refresh/reconnect/disconnect, persisted external account
  identity, exact URL allowlisting, active-host callback validation, mounted
  Settings polling, standalone completion response, and shared GitHub device-page
  launch. Deterministic headless tests cover the internal contracts without
  simulating OAuth/provider responses. M1 remains **In progress** until separately
  authorized live browser-hosted and Electron evidence exercises the acceptance
  cases.

### Decision and change log

| Date | Decision/change | Reason and effect | Authority/evidence |
|---|---|---|---|
| 2026-09-16 | Approved the v1 scope and specification recorded below | All six feature areas precede v1; staged Windows/Mac launches; public beta first | Owner approval in the v1 planning conversation |
| 2026-09-16 | Recorded stepwise handoff and maintenance workflow; marked the older DOCX proposal superseded | Preserve the accepted plan across sessions without competing implementation instructions | Owner requested a maintained repository plan and a separate starting prompt |
| 2026-09-16 | Superseded M0.2 and declined general hosted CI | Keep general repository gates and focused regressions local; retain only the dispatch-only macOS release builder because it provides required native Mac compute | Owner decision |
| 2026-09-16 | Defined M0.3 as a local Windows Release reference baseline, not a hardware minimum | The documented 32 GiB Windows machine is a comparison environment only; latency targets remain v1 goals and do not establish macOS, Store, updater, provider, or large-library performance | Owner-approved M0.3 scope; [local evidence](evidence/m0.3-local-performance-baseline.md) |
| 2026-09-16 | Completed M0.4 local distribution inventory without choosing terms | Preserve a reproducible inventory of current local release inputs and explicit license/account/macOS blockers while deferring license adoption, public-history scanning, publication, and Store actions | [M0.4 inventory](research/m0.4-distribution-inventory.md) |
| 2026-09-16 | Added M0.5 local update-channel policy and full-trust MSIX preflight | Free builds use user-initiated GitHub release notification/browser download only; Store builds suppress GitHub, browser, and Electron updater paths. The local package preflight proves the Store closure, contents, and embedded CMS signer without installation, trusted-store mutation, Store submission, or OAuth | [M0.5 evidence](evidence/m0.5-windows-msix-feasibility.md) |
| 2026-09-16 | Recorded M0.6 as blocked pending owner-supplied Apple signing and Apple-silicon host inputs | Preserve the direct-DMG/macOS workflow while defining the minimum MAS sandbox acceptance boundary; no MAS build flavor, entitlement, certificate, device test, signing, notarization, Store submission, or GitHub secret was added | [M0.6 prerequisite record](evidence/m0.6-mac-app-store-feasibility.md) |
| 2026-09-16 | Replaced account entitlement discovery with a fully static OpenAI account catalog | The bundled stable-four list is authoritative and must not call `/v1/models`; Settings no longer refreshes account model availability, manual account IDs remain an explicit Advanced/Test workflow, and generic provider discovery is unchanged | Owner correction during M1 implementation |
| 2026-09-16 | Removed provider-backed indexing from blank project creation | Persist the project, page setup, and empty Book Brief together, seed only local graph defaults before navigation, and prevent duplicate Create submissions while work is pending; semantic profile indexing begins with substantive profile changes or a full rebuild | Owner-reported roughly 30-second unresponsive Create action; source trace showed the creation path awaited the configured embedding provider |
| 2026-09-17 | Accepted the M2-M5 contract revision | Replaces the prospective CSL/Jint formatter with managed `lorekeeper-citations-v1` for Chicago 18, APA 7, and MLA 9; fixes Designed Page ownership, multi-target authoring/recovery, retained-source migration, policy-scoped portable traversal, and rich table/note defaults before implementation | Owner-approved Astra-reviewed revision |
| 2026-09-17 | Replaced prospective legal review with a fail-closed engineering dependency gate | Admit only dependencies with verified policy-compatible redistribution terms; resolve, remove, or replace everything else. Do not adopt Commons Clause or another Lorekeeper source license without a later owner decision | Owner decision during remaining-v1 planning |

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
| License | Owner decision before publication; no source license is currently adopted |
| Public access | Public source and free beta before v1 |
| Language | English UI and the current Latin-script, left-to-right publishing scope |
| Testing | Expand focused regression coverage beyond the current migration/Press restriction |
| Excluded | Additional premium features, subscriptions, license servers, Intel Mac, Windows ARM, Linux release support, new OCR, arbitrary Word layout replication |

The prior Apache-2.0 plus Commons Clause candidate is not adopted. Do not describe
Lorekeeper as open source or source available until the owner selects final
terms. Any future source license must leave manuscripts and other author-created
content unrestricted; selecting those terms is a publication gate, not an
engineering dependency gate.

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

The following allocation is the accepted planning target. Before each format
commit, inspect checked-in constants; if an upstream change consumed a number,
use the next unused number and record it. Never reuse a shipped format number.

| Boundary | M2 | M4 | M5A | M5B |
|---|---:|---:|---:|---:|
| Manuscript schema | 5 | 5 | 6 | 7 |
| Legacy JSON project export | 31 | Import-only | Import-only | Import-only |
| `.lorekeeper` archive envelope | — | 1 | 1 | 1 |
| Archive record schema | — | 1 | 2 | 3 |
| History snapshot schema | 7 | 8 | 9 | 10 |
| Press protocol | 13 | 13 | 14 | 15 |
| Agent-manuscript projection | 2 | 2 | 3 | 4 |
| EPUB exporter | 4 | 4 | 5 | 6 |
| Authoring batch/journal | — | V1 from M3 | V1 | V1 |
| Citation formatter | — | — | — | V1 |
| Composition scene | 1 | 1 | 1 | 1 |

Every committed predecessor needs an explicit reader adapter and fixture.
History adapters validate the predecessor's original manifest and hashes before
transformation. Migration coverage includes direct v0.3.17-to-final upgrade and
each intermediate committed database schema.

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
- Materialize the bundled catalog locally after account creation or connection;
  never fetch or reconcile an account-backed model list at runtime.
- Keep the existing preferred default where supported. Never silently substitute
  another model when an explicit selection cannot be used.
- Validate effort choices against each model's supported values.
- Capture the resolved usable budget and model configuration at turn start; no
  discovered account-context value changes compaction behavior.
- Catalog models require no manual Test action. Connection and provider-call
  failures remain actionable at their actual boundaries.
- Manual model entry stays available under advanced configuration with an
  explicit Test action and no dynamic account model picker.

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
reconnect, token-refresh failure, provider rejection of an explicitly selected
catalog model, and simultaneous stale callbacks on both supported platforms.

### B. Independent Designed Pages (M2)

#### Ownership model

Replace `PageComposition` chapter/section ownership with:

| Entity | Responsibility |
|---|---|
| `DesignedPage` | Project identity, name, optional release-only scope |
| `DesignedPageContent` | Semantic content, accessibility description, revision, Core or release override |
| `DesignedPageVariant` | Authored layout owned by one exact content identity and geometry |
| Manuscript Designed Page block | One placement, identified by its stable block ID |

A shared page has one Core content record and at most one override per release.
Release-only pages remain scoped to their release. `DesignedPagePlacementReference`
is a derived reverse index only: manuscripts remain authoritative. Authored
variants are distinct from disposable preview/reflow caches. The first release
edit clones the complete effective semantic-content and authored-layout layer,
so later Core geometry edits cannot overwrite release work.

`IDesignedPageService` is the only service boundary for creating, duplicating,
placing, moving, removing, deleting, reading effective content, creating/resetting
overrides, and cloning releases. It preserves archived-release immutability and
the inherited/customized distinction through lookup, projection, and artifact
invalidation.

#### Placement behavior

- Place again: new placement ID, same page.
- Move: preserve placement and page identity; cross-container moves acquire one
  project mutation lease and commit both manuscripts and the derived index in one
  transaction, receipt, and compound history action.
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
- Review assigns shared-page/content changes a separate dependency group. A
  placement approval neither approves nor restores page content; selective
  restore brings only required valid dependencies and reports ambiguous current
  shared content as a conflict rather than overwriting it.
- Review displays a shared page change once, with links to its placements.

Add a project Pages workspace with Core/release selection, placement counts,
unplaced status, duplicate/place/reset commands, and the existing canvas editor.
Existing chapter Pages mode becomes a filtered entrance to that workspace.

#### Migration

Preserve existing page, block, scene, and variant identities where possible.
Core composition IDs become shared page IDs; valid release clones become frozen
overrides on that page; ambiguous or divergent clones become independent
release-only pages. Advance every live manuscript-bearing payload together.
Every existing release clone becomes an explicit frozen override, even when
currently identical to Core. Never merge divergent legacy clones.

### C. Fast manual Undo/Redo and reliable saving (M3)

Manual history is already in memory at the planning baseline. The redesign
removes whole-document work and server waits from ordinary Undo. It introduces
`AuthoringBatchProtocolV1` and `AuthoringJournalV1`: an `AuthoringBatch` names
an ordered target set, each target's expected version and generation, client
session, batch identity and sequence, action label, operations, and selections.
Results carry a receipt, canonical before/after versions, generation, and
structured conflict data.

#### Client-first operations

Use the versioned shared batch contract for one or more targets. A cross-container
page move is one indivisible multi-target commit and Undo action.

- ProseMirror applies Undo/Redo immediately using local inverse operations.
- Canvas history uses object/property deltas.
- A serialized background queue durably appends unacknowledged batches and base/
  checkpoint data to IndexedDB before dispatch. The journal contains recovery
  data only; it is not a second confirmed-history authority.
- Save acknowledgments advance the confirmed revision without replacing the visible document.
- Typing coalesces; paste, formatting, and structural actions create explicit boundaries.
- Rare complex edits may use bounded snapshot entries; ordinary typing must not.

The server validates and atomically applies batches through existing owning
services. Session sequence advancement, mutation, and receipt insertion occur
in one SQLite transaction. The same identity plus request hash returns the
original receipt; the same identity with different content fails closed.
Receipts remain until reconciliation is durably acknowledged. The server derives
canonical inverses from validated pre-mutation state and never trusts a
client-supplied inverse.

#### Recovery

- One process-wide delta-history owner, identified by a process-incarnation ID,
  retains at most 100 confirmed actions per target and 128 MiB across the
  process. It LRU-evicts only oldest inactive confirmed entries; pending work is
  never evicted. An individually oversized action saves but clears that target's
  Undo history and reports that it cannot be undone.
- ProseMirror history is an adapter to that process-owned cursor, not a second
  authority. Remount/browser reload rehydrates confirmed history in the same
  process; a new process drops confirmed Undo while recovering pending work.
- Only one interactive writable lease per target may exist in the process;
  other windows are read-only until the active lease flushes and hands off.
- A network/database failure pauses dispatch while retaining the local document.
  IndexedDB quota/write failure retains visible edits, marks them not locally
  recoverable, and prevents dependent actions from claiming the work is saved.
- A stale save must never silently replace dirty local content.
- Rebase only with exact preconditions. Same-element edits, moved/removed anchors,
  overlapping properties, or ordering ambiguity retain both variants and open a
  Lorekeeper-owned conflict surface.
- Assistant changes remain outside manual Undo and invalidate affected history generations.
- Preserve the existing 100-action/128 MiB history bounds; account for delta
  dependencies incrementally.

Checkpoints, restore/sync, publishing, assistants, and every other state consumer
use a project/target mutation fence: freeze affected edits, flush through a
captured sequence, acquire the project mutation lease, revalidate every target
generation, consume state, then release/resume. An unreachable registered dirty
client blocks that dependent operation until recovery. Failed flushes stop it.

Deterministic M3 implementation is complete as of 2026-09-17. Native Electron
latency and interaction evidence remains outstanding, so the milestone remains
**In progress** rather than claiming its full exit criteria.

### D. Source documents, evidence, and bibliography records (M4)

#### Library and extraction

Extend existing ingest ownership rather than creating a competing corpus.

Support PDF, EPUB, DOCX, text/Markdown, and saved webpages. Retain immutable
originals and versioned normalized extraction. `SourceOriginal` records name,
media type, length, SHA-256, and ordered chunk references; chunks are SHA-256
addressed and at most 8 MiB. `SourceExtractionVersion` records extractor,
version, options, content hash, terminal status, diagnostics, and bounded
reading blocks.

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
- Migrate every existing source into a legacy immutable extraction while
  preserving source/chunk/block/page IDs, normalized text, evidence, and hashes.
  Set its original state to `OriginalUnavailable`; never synthesize a file from
  extracted text. Terminal ingest-job cleanup must not delete retained source
  material.

#### Reader

Provide a dedicated Sources workspace with formatted reading, contents navigation,
search, exact evidence highlighting, and original download.

For PDFs, add original-page viewing through an application-owned surface using
bounded page rendering. Exact text highlights belong to the normalized reading
view; original-page links navigate to the relevant page.

A durable `SourceLocation` carries source ID, extraction version, block/page
identity, normalized range, locator, quote, and a verification hash. Use it in search
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
containing manifests, structured records, and binary chunks. JSON v1-v31 remain
import-only boundary adapters.

- One dependency traversal engine supports distinct `FullArchive`,
  `NonStructuralArchive`, and `HistorySnapshot` policies. Each policy retains
  its own inclusion, external-project-reference, workflow-setting, and warning
  behavior; shared traversal must not merge these scopes.
- Full export includes all project sources. Non-structural export preserves
  dependencies of included content and reports omitted source evidence.
- Export/import runs as reconnectable jobs using controlled temporary files and
  streaming HTTP transfer, not Base64 through SignalR.
- Capture freezes canonical mutation, reads a stable no-tracking snapshot into
  bounded temporary descriptors, then releases the project lease before
  compression, download, or Git transport. Archive, history, Git, restore, and
  HTTP contracts use declared files/streams rather than byte arrays.
- Import jobs progress through `Uploaded`, `Staged`, `Validated`, `Applying`,
  `Committed`, `Indexing`, `Completed`, `CompletedWithWarnings`, `Failed`, or
  `Cancelled`. Cancellation is honored only through Applying; after the database
  transaction commits, indexing is retryable and may only add warnings.
- Validate paths, duplicate entries, symlinks/reparse points, declared and
  expanded lengths, hashes, JSON/XML limits, and reference closure before
  admitting imported state.
- Retain old JSON formats only as versioned import boundaries.

Version history gains small source indexes, per-source manifests, and bounded
original/text blobs. Comparison reads metadata lazily; restore streams and
validates content. Show new/reused bytes and large-sync feedback. Chunking avoids
oversized individual blobs but does not remove hosting limits or the cost of
multi-GB history.

Deterministic M4 implementation is complete as of 2026-09-17. Native
50-source navigation and multi-GB archive/history performance evidence remains
outstanding, so the milestone remains **In progress** rather than claiming its
full exit criteria.

### E. Rich manuscript, citations, and DOCX (M5)

#### Canonical manuscript additions

Extend the semantic manuscript with:

- Tables containing stable rows/cells, spans, and block content.
- Document-owned footnotes/endnotes.
- Inline note-reference and citation atoms.
- Citation clusters containing one or more bibliographic references.

Table identities are recursive and stable. Column-width weights are positive
integers normalized to the available width; header rows are contiguous and
leading; row/column spans must cover without overlap or gaps. Cells support
paragraphs, ordered/unordered lists, and Figures only. Nested tables and
Designed Pages inside cells are rejected with explicit import diagnostics.

All nested blocks retain stable identities. `ManuscriptPosition` uses UTF-16 and
contains document, container path, block/atom identity, offset, and affinity;
the same traversal drives editor mapping, annotations, operations, assistant
projection, search, archive/history, and Press page maps. Displayed citation
numbering must not change stored anchor positions.

Notes are owned by one semantic document and have exactly one reference. Their
content supports paragraphs, lists, Figures, citations, and character formatting,
but not tables, Designed Pages, or recursive notes. Copying across documents
clones the note and nested atom IDs; removing the reference removes its note in
the same reversible operation. Orphaned/multiply referenced import notes fail
closed. Footnote/endnote numbering restarts per top-level chapter or publication
section. Designed Page occurrences participate in their containing document;
standalone previews use their own sequence. Footnotes reserve at most 40% of a
page body, continuing with a marker when needed and keeping a reference with at
least two note lines where possible; unplaceable content has named diagnostics.

Update validation, operations, assistant projections, search, Undo, release
customization, import/export, history, and rendering together.

#### Citation formatting

Use a managed C# `ICitationFormatter` with formatter identity
`lorekeeper-citations-v1`; do not embed a CSL engine. Freeze v1 styles to Chicago
Manual of Style 18 notes-and-bibliography, APA 7, and MLA 9. Arbitrary
user-supplied styles are excluded. The formatter preserves author-entered lexical
capitalization and controls punctuation, ordering, labels, quotation, italics,
and name inversion without guessing proper nouns.

Citations are atoms and multi-item clusters. Each item references one M4
`BibliographicRecord` and carries prefix, suffix, locator label/value, and an
optional `SourceLocation`. They and note atoms are allowed in Designed Page
semantic content. Occurrences are identified by publication target, top-level
container, full placement path, citation atom ID, and cluster-item ordinal, so a
repeated Designed Page has distinct publication occurrences without changed
stored offsets.

Chicago uses a full first note and later shortened notes, never `Ibid.`; APA
uses publication-wide author-year `a`/`b` disambiguation; MLA uses author or
short-title locators. Commit an explicit missing-field matrix and actionable
diagnostics. Bibliography/Works Cited order and deduplication are publication-wide;
chapter Read derives its format from complete effective publication context.
Golden outputs are independently authored fixtures, never generated snapshots.

Core owns the citation style; releases may override it. Generate final back
matter with endnotes first (grouped by top-level document and occurrence-backed),
then Bibliography, References, or Works Cited. Omit empty generated sections.
Missing required metadata produces actionable diagnostics. Inserting an assistant
evidence link does not automatically create a manuscript citation.

#### DOCX input

Use pinned, dependency-admitted [Open XML SDK](https://learn.microsoft.com/en-us/office/open-xml/open-xml-sdk)
for file import/export and a Word-aware HTML clipboard adapter feeding one
`SemanticImportFragment` (blocks, notes, citations, staged assets, styles,
bibliography records, source mappings, and a review report).

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

Parse outside write locks, then atomically commit assets, styles, bibliography,
and manuscript at the explicit insertion point. Bound ZIP/XML expansion, never
fetch remote resources, and reject unsafe relationships, executable content, or
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

Extend Press with table pagination: repeat headers after a break, do not split a
normal row, split an over-page row only at semantic block boundaries, never split
a row-span group, and emit `UnplaceableTableRowGroup` when the atomic group cannot
fit. Endnotes are final back matter with occurrence-specific backlinks.
`UnplaceableFootnoteContent` is likewise a named diagnostic. EPUB gets semantic
tables and linked notes.
DOCX support is incomplete until
these existing paths preserve the richer manuscript.

M5 remains **In progress**. The M5B implementation uses manuscript/archive/history/
agent/EPUB/Press contracts 7/3/10/4/6/15. Citation styles, bibliography identities,
source-location references, and nested manuscript content cross archive/history
boundaries with explicit remapping and ownership validation. Non-structural
archives retain cited bibliography metadata while reporting omitted source
evidence. Output fingerprints include cited metadata.

The managed formatter emits semantic runs for the three fixed styles and eight
supported record kinds. Publication-wide context controls disambiguation,
bibliography ordering, repeated Chicago references, and occurrence identities.
Chapter Read uses that same context. Manual cluster editing preserves atom IDs;
Editor and Publish expose bounded shared bibliography operations. Bibliography
edits use stale-write checks and project coordination. Evidence detachment rewrites
affected manuscripts atomically, retaining metadata and locators.

Shared output projections distinguish repeated Designed Page notes across TXT,
Markdown, EPUB, DOCX, and Press. DOCX retains native footnotes, final endnotes,
named styles/direct typography, heading levels, image proportions, captions,
merged tables, and distinct bookmarks for duplicate titles and placements.
Designed Page artwork resolves placement-specific labels after stored frame
ranges. Ordered/unordered lists retain nesting and restarts across editor JSON,
authoring recovery, Word numbering, semantic text/HTML, and Press.

Press reserves reference-aware footnote regions before body flow, bounded to
40% of usable height, with two lines beside the reference where possible and
explicit continuation. Table references retain formatted runs, fitting note
artwork is supported, and oversized atomic content has a named diagnostic.
Repeated Designed Pages use available space below artwork without changing
their stored frame offsets. Final authored notes and citation notes share one
Endnotes section before the applicable bibliography.

Ordinary rich-document typing and citation/note-body edits use nested
`replaceInlineContent` operations with block fingerprints and complete positions.
Structural changes retain their exact document fallback. Archive input/expanded
defaults are configurable at 8/16 GiB, with separate per-entry, manifest, and
legacy JSON bounds. Streaming export and staged input enforce those bounds;
archive import avoids aggregate image/font hydration and EF binary tracking.

Deterministic evidence covers nested citation remapping and ownership,
selective-history closure, rollback after a later mutation fails, receipt replay,
canonical inverses and Undo/Redo, contributor and missing-field formatting,
repeated page/backlink identity, DOCX structure, footnote reservation/continuation,
and poorly compressible archive inputs. These implementation checks do not
establish manual acceptance, Word desktop compatibility, or native latency.

Semantic DOCX and Word-paste insertion now use `SemanticImportFragment`, bounded
parsing outside write locks, exact cursor/revision checks, atomic resource
admission, durable replay, and canonical Undo/Redo. DOCX citation and authored
endnote metadata round-trip through editable output. Deterministic checks cover
conversion, malformed input, rollback, replay, and resource preservation. Word
desktop and clipboard fidelity still require manual acceptance.

Rich note interaction now supports paragraph/list/Figure content, formatting,
citations, and Word insertion through the parent manuscript's save and Undo
boundary. Headless checks cover nested deltas and ownership; keyboard/focus and
visual interaction remain manual acceptance items.

Remaining work is final output/preservation review, remaining aggregate
history/source paths, and affected usability/documentation cleanup. Full automated
gates, diff review, and isolated HTTP startup precede the clean committed
implementation checkpoint. Manual acceptance comes next; broader UI, Word
desktop, live OAuth/provider, and native performance checks await the owner's
go-ahead. Launch and distribution remain deferred.

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
- Publisher identity and developer accounts.
- Verified Buy Me a Coffee URL.
- Final owner-selected source-license and contribution terms.
- Real-device Mac testers and required Store/signing access.

These inputs are publication dependencies, not excuses to stop independent
engineering work or ask again about already settled product decisions.
