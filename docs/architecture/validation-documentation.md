# Validation and documentation

## When to read

Read this chapter completely before selecting verification for a code,
configuration, persistence, editor, renderer, packaging, or documentation change.
Read it whenever a task changes build tooling, test projects, launch profiles,
release scripts, native dependencies, public claims, architecture chapters,
research briefs, decisions, or contributor/agent instructions.

This is the verification-policy owner. Other chapters name feature-specific risks
and route here for the commands and claim boundaries. When a task touches a
feature, read its owning chapter first; this chapter does not replace impact
analysis or feature contracts.

## Scope and ownership

This chapter owns the repository's validation levels, permitted automated-test
scope, startup and desktop smoke checks, native Press checks, release/package
verification, integration-claim rules, and documentation source-of-truth
hierarchy. It also owns how the routed architecture map is maintained and how
research and architecture decisions relate to current code.

Validation must be proportional to impact and must use the relevant checks below.
A successful check establishes only its stated boundary. Compilation is not an
OAuth test; an HTTP startup is not an Electron/package test; and renderer unit
tests are not vendor acceptance. Report unperformed integrations instead of
promoting assumptions to verified behavior.

The repository has intentionally narrow automated-test boundaries. Press
conformance tests map to the native renderer evidence matrix and verify PDF-spec
parity. `Lorekeeper.Tests` is limited to the data-safety core: startup database
migration, versioned project import/export, archive, and history preservation
and fail-closed behavior, and recovery. **Automated tests outside those two
scopes are forbidden.** Do not create new test projects, suites, or one-off test
harnesses anywhere else merely because a feature would normally invite one; a
request to add tests does not broaden this boundary unless the user explicitly
changes it. Areas outside the boundary are validated by the build, static
inspection, and user-authorized manual checks.

Manual and UI validation — startup smoke checks, Electron checks, browser/UI
checks, screenshots, and interactive provider or Word exercises — requires the
user's explicit authorization before it is performed. Do not claim an external
provider or rendered UI was exercised unless that authorized integration check
actually ran.

## Current architecture and invariants

### Documentation hierarchy and routed reading

Documentation is authoritative by responsibility:

1. [`VISION.md`](../../VISION.md) defines product direction and success criteria.
   It is not evidence that planned capabilities are implemented.
2. [`docs/architecture.md`](../architecture.md) is the compact mandatory index. It
   states current status, global invariants, source-of-truth rules, and routes a
   task's concepts and paths to the detailed chapters in this directory.
3. Each architecture chapter owns current contracts and key file families for one
   coherent responsibility. Relevant chapters are conditionally mandatory, not
   optional background reading.
   The version-history owner chapter covers deterministic Git snapshots,
   checkpoint/restore, clone import, and explicit remote synchronization.
4. [`README.md`](../../README.md) describes user-facing capabilities, setup, run,
   packaging, release/update behavior, and local-data expectations.
5. [`docs/publishing-roadmap.md`](../publishing-roadmap.md) sequences researched
   delivery and verification gates. Roadmap status does not override current-code
   or architecture claims. [`docs/v1-roadmap.md`](../v1-roadmap.md) owns the
   approved application-v1 scope, milestone/evidence ledger, release decisions,
   and maintained session handoff. Planned policy changes there become current
   repository guidance through their explicit implementation steps.
6. [`docs/research/`](../research/) records sourced evidence and proposals.
   [`docs/decisions/`](../decisions/) records architectural decisions, including
   superseded historical choices. Neither is proof of current implementation.
7. [`docs/plans/`](../plans/) contains deferred or decision-complete plans. A plan
   becomes current behavior only after code, current architecture, and validation
   are updated.

At session start, read `VISION.md` and the architecture index completely. Inspect
the repository to establish the actual impact area, then read every routed chapter
completely before substantive planning or editing. If investigation expands the
impact area, read the newly implicated chapters before proceeding. Search source
and inspect callers/consumers even when the architecture map names an entry point;
the map explains ownership and navigation but is not an exhaustive file inventory
or proof that a feature exists.

A changed file does not automatically require a new documentation row. Update the
owning chapter when a change adds, removes, renames, or materially repurposes an
architectural entry point or file family, or changes that chapter's contract,
responsibility, data flow, security posture, platform behavior, or verification.
Keep one primary owner for each file/family. Other chapters should link to that
owner and name only the dependency necessary to understand their own contract.
This prevents duplicate descriptions from drifting.

Update README for user-facing capability, requirement, setup, run, package,
release, update, or local-data changes. Update `VISION.md` only for an intentional
product-direction or scope change. Preserve historical research, accepted or
superseded decisions, and applied EF migration files except for link repairs or
clearly stale statements presented as current. Remove stale examples, comments,
settings, and instructions in the same change that makes them obsolete.

Documentation-only work must validate every referenced local path, configuration
key, launch profile, and command. The repository intentionally has no separate
documentation manifest or validation program; use targeted searches, direct path
checks, command help/dry inspection where safe, and diff review.

### Repository readiness and completion

Work on whatever branch is already checked out. Before beginning new work, if
the current branch is not `main`, merge the latest `main` into it first.

Existing changes belong to the user unless proved otherwise. Inspect every diff;
never discard, hide, overwrite, or mix unrelated unfinished work into a feature.
If existing changes form coherent prior work, finish their verification and
documentation, then commit them before beginning a new feature. Completed work
includes current documentation, removal of obsolete runtime paths, the relevant
verification, full-diff inspection, one focused commit per completed feature,
and a clean post-commit working tree. Applied EF migrations remain immutable
history.

Before completion, search again for obsolete names and paths, inspect all callers
of changed contracts, run `git diff --check`, and compare documentation claims to
the resulting code. Stage only the feature's files. Do not amend, squash,
force-push, or otherwise rewrite history unless explicitly requested.

### Baseline toolchain and normal source verification

The source toolchain is:

- .NET 10 SDK, pinned by [`global.json`](../../global.json);
- Node.js 22.12 or later for Electron.NET desktop builds and packaging; and
- Rust 1.97.1, pinned by
  [`Lorekeeper.Press/rust-toolchain.toml`](../../Lorekeeper.Press/rust-toolchain.toml),
  for source builds of the native renderer.

Completion requires a successful `dotnet build Lorekeeper.sln` and passing
remaining tests — `dotnet test Lorekeeper.Tests\Lorekeeper.Tests.csproj`, and
`cargo test` in `Lorekeeper.Press` when Press sources change. There is no
automated commit gate; this completion rule applies to the final state of the
change.

Manual and UI validation — startup smoke checks, Electron checks, browser/UI
checks, screenshots, and interactive provider or Word exercises — requires the
user's explicit authorization before it is performed. When authorized, use the
explicit `http` profile (`http://localhost:1455`) for browser checks or the
`electron` profile for the desktop shell, always terminate what you start, and
do not start a competing host on a port a user-owned debug instance occupies.
Electron remains the primary/default debug target. An HTTP startup does not
validate the Electron bridge, update integration, packaged data paths, or
native window behavior.

### Data-safety tests

[`Lorekeeper.Tests`](../../Lorekeeper.Tests/) is the automated-test boundary for
application data safety: startup database migration, versioned project
import/export, archive, and history preservation and fail-closed behavior, and
recovery. Fixtures must exercise the same production boundaries as the app; do
not create a parallel migration sequence or a generic unit-test suite for test
convenience.

Run the kept suite with:

```powershell
dotnet test Lorekeeper.Tests\Lorekeeper.Tests.csproj
```

It covers manuscript/page/composition/Core/release migrations, annotation and
chat preservation, source-evidence and project-reference cutovers, protected
backup/recovery, current/legacy import adaptation, identifier remapping, warning
behavior, foreign keys, whole-import rollback, guarded OpenAI credential
migration, retained-source conversion, streamed archive/history closure and
restoration, and citation preservation with fail-closed import rejection. It is
not a home for assistant, UI, ordinary service, provider behavior, or formatting
suites, and no permitted test may substitute a simulated external integration
for target-specific evidence. Tests outside this boundary and the Press
conformance suite are forbidden.

### Semantic editor verification

The ProseMirror editor is an exact-pinned JavaScript build. When its source,
dependencies, schema adapter, shipped bundle, notices, or release integration
changes, run:

```powershell
npm ci --prefix tools/semantic-editor
npm run build --prefix tools/semantic-editor
```

Inspect the resulting committed bundle and notice changes. Windows and macOS
release builders repeat a locked install, audit, and deterministic rebuild; they
fail if the committed bundle is stale or if anything other than one matching
bundle and notice reaches staging.

### Press conformance verification

`Lorekeeper.Press` owns the paperback/PDF production runtime and its evidence
suite. Tests may be added only when they map to requirements in the Press
conformance evidence matrix, including protocol, containment, atomicity,
cancellation, determinism, typography, layout, raw-PDF, and adversarial evidence.
Run:

```powershell
Set-Location Lorekeeper.Press
cargo fmt --check
cargo clippy --all-targets -- -D warnings
cargo test --locked
```

`dotnet build Lorekeeper.sln` also builds and packages the exact locked native
runtime through the MSBuild integration. The app verifies its owned manifest at
runtime. Press conformance establishes Lorekeeper's declared checks; it does not
by itself establish acceptance by a vendor or independent PDF/UA certification.
See [Press production](press-production.md) for artifact-level claims.

### Desktop packaging and release verification

Build current Windows artifacts on Windows with:

```powershell
.\scripts\build-windows-release.ps1
```

The builder validates tool versions and SemVer, audits managed and shipped
npm/Electron dependencies, rebuilds isolated staging/output, rebuilds and checks
the semantic editor, builds the locked Press runtime, probes packaged renderer and
print-artifact profile registry identity, and verifies release artifacts and
checksums. The shared dependency policy permits only the two exact known
`image-size@1.2.1` parser advisories while Electron.NET's generated call remains
provably confined to an unconfigured splash-image path. A package version, call
site, splash configuration, or advisory change fails closed.

Release builds default to the `Free` distribution channel. For the local Windows
Store-channel package preflight only, first build an unpacked Store closure and
then run the MSIX script:

```powershell
.\scripts\build-windows-release.ps1 -KeepUnpacked -DistributionChannel Store
.\tools\msix\Build-M0MsixFeasibility.ps1
```

The script accepts only that local `win-unpacked` closure, validates its immutable
Store metadata, packs a full-trust desktop MSIX with `MakeAppx` (without `/nv`),
unpacks and checks required contents, then uses an ephemeral current-user test
certificate to sign the package and verify its embedded CMS integrity and exact
signer. All package output stays under ignored `.artifacts/m0.5-msix/`; the exact
certificate is removed before the script returns. It never writes a trusted-root
entry, installs a package, or contacts Store, GitHub, providers, or an updater.
Windows trust-chain acceptance and installation in a disposable Windows profile,
including packaged process/data/import/export/print boundaries, require separate
owner authorization. This preflight does not establish Store acceptance or
app-container confinement: the manifest intentionally uses the documented
full-trust desktop model.

The native Apple Silicon builder is
[`scripts/build-macos-release.ps1`](../../scripts/build-macos-release.ps1). It must
run on macOS arm64, performs the corresponding dependency/editor/native-runtime
checks, creates an ad-hoc-signed DMG, verifies architectures/signatures, mounts and
smoke-tests the application, and writes a checksum. Do not substitute a
cross-compiled artifact for that native evidence.

M0.6 Mac App Store feasibility is blocked, not implemented, until the owner
supplies an Apple Developer Team ID, MAS development certificate/profile, test App
ID, an Apple-silicon test Mac, and selects the signed build host. The existing
direct-DMG builder is not a MAS builder and must remain unchanged. Once unblocked,
the acceptance build uses Electron’s MAS runtime with App Sandbox and distinct app/
helper entitlement files. Begin only with evidenced sandbox, loopback/network
client/server, user-selected read/write-file, and printing entitlements; do not
add broad filesystem access, App Groups, or a library-validation exception without
a reproducible signed-device failure. Capture sanitized signing/entitlement,
process-tree, loopback, SQLite/history, file-picker, print-handoff, and Store
updater-suppression evidence on the selected device. Stop the Store channel rather
than weaken the sandbox speculatively if a required native boundary fails. Do not
upload, notarize, publish, submit to App Store Connect, add GitHub signing secrets,
or change the macOS workflow without a new explicit authorization.

The Windows stable-release entry point is `scripts/release.ps1`, also exposed by
the **Release Lorekeeper** VS Code task. Invoking it authorizes preparation and
direct publication from `main`. It requires a clean worktree/index, the intended
GitHub origin and write access to both repositories, the SDK/Node/Rust tools, and
the macOS workflow when applicable. It fetches with pruning, fast-forwards a
behind-only `main` and reloads itself, accepts verified local commits ahead of
the remote, and stops for divergence. An exclusive local file handle prevents
two drivers from running concurrently in this checkout.

The driver reads the sole application version from `Lorekeeper.csproj`, compares
both repositories' highest stable releases and matching uploaded asset digests,
requires published source ancestry, and rejects candidate release/tag collisions.
It reuses a higher unpublished project version or increments the patch version;
an already published exact source is a no-op. `-Bump Minor`, `-Bump Major`, and
`-Version <major.minor.patch>` select deliberate alternatives. Prereleases use the
lower-level publisher. Documentation uses version-independent examples.

Only the project version is edited and committed. The driver runs the
build-and-test preflight through `eng/ReleaseWorkflow.ps1` before that commit
and again on the committed source before pushing and publishing. .NET preflight
outputs use the isolated artifacts directory and the previous environment is
restored.
Verification rejects unexpected source/index changes. On failure it restores
only its exact unstaged version edit; committed preparation remains available
for a retry. Staged/concurrent edits and partial publication need inspection.
There is no reset, force push, tag overwrite, dependency upgrade, or automatic
commit of feature work.

`-CheckOnly` (the **Preview Lorekeeper release** task) performs preflight and
reports the selected version/platforms without source edits, builds, commits,
pushes, or publication. It still fetches metadata, requires a clean checkout,
and reports a behind checkout instead of fast-forwarding it. `-WindowsOnly`
omits the macOS build. Preview is the release-driver validation surface; do not
publish a new version merely to exercise tooling changes.

The lower-level publisher runs from a clean tree at fetched `origin/main`:

```powershell
.\scripts\release.ps1
.\scripts\release.ps1 -CheckOnly
.\scripts\publish-release.ps1 -Version <version> -AllowDirectMainPush
```

The publisher rejects detached or dirty source, a version different from the
project, and any commit other than fetched `origin/main`. It requires the
explicit `-AllowDirectMainPush` authorization and does not query pull requests.
It rechecks worktree and local/remote source identity after packaging.
The cross-platform path dispatches one correlated macOS
arm64 workflow, builds Windows locally, verifies one requested artifact set,
creates matching draft releases in the private source and public release
repositories, verifies both asset sets, and only then publishes both. On a failed
dual publication it removes the exact releases/tags created by that run when
GitHub permits, and reports any resource requiring manual inspection. Temporary
Actions artifacts are deleted only after successful publication. The public
release repository is the automatic and manual update authority.

The owner has declined general hosted CI, hosted validation workflows, and
non-macOS GitHub compute for v1. The dispatch-only macOS arm64 release builder
remains the sole hosted workflow because native Mac packaging needs a Mac host.
GitHub Actions stays restricted to that builder with read-only repository
workflow permissions. Every normal build-and-test completion run stays local,
with evidence recorded in the commit message or work
item; do not turn the release builder into a general validation lane without a
new owner decision.

Packaging and updater claims require execution on the relevant operating system.
Never claim OAuth, provider calls, embeddings, web search, image generation,
publishing output, packaging, automatic updates, or platform-specific Electron
behavior solely from compilation or static inspection.

Static managed acceptance inspection covers flowing Figures, Designed Pages,
publication sections, selected covers, and structured physical cover scenes;
contain/cover/stretch geometry; 180/300-DPI thresholds; and aggregation to the
maximum required proportional raster. Press conformance remains unchanged because
the application passes the prepared assets through its existing request contract.
Approved migration/import fixtures cover historical
print-resample reclassification, preparation-job preservation, v31 full and
non-structural round trips, parent remapping/missing-parent rejection, legacy
cover-description adaptation, and snapshot-schema-v7 restore preservation. Ordinary publication
service or UI tests remain outside the automated-test boundary; actual package
and update behavior requires target-specific, user-authorized evidence.

### Milestone contract evidence

Earlier milestone contract evidence (M2-M5) is recorded in the
[`docs/v1-roadmap.md`](../v1-roadmap.md) milestone ledger. Those entries are
historical records of what was implemented and verified at the time. The current
automated-test boundary is the data-safety core plus the Press conformance
suite.

## Key files and file families

| File or family | Architectural role |
|---|---|
| [`AGENTS.md`](../../AGENTS.md), [`CLAUDE.md`](../../CLAUDE.md), and [Copilot instructions](../../.github/copilot-instructions.md) | Authoritative workflow rules and compatibility entry points for required routed reading, repository safety, verification, documentation, and commits. |
| [`VISION.md`](../../VISION.md), [architecture index](../architecture.md), and [`README.md`](../../README.md) | Product direction, current technical routing/contracts, and user-facing behavior/setup respectively. |
| [`version-history-sync.md`](version-history-sync.md) | Current deterministic snapshot, local Git, restore/import, remote-sync, and version-control credential boundary. |
| [`docs/publishing-roadmap.md`](../publishing-roadmap.md), [`docs/research/`](../research/), [`docs/decisions/`](../decisions/), and [`docs/plans/`](../plans/) | Delivery gates, sourced evidence, historical architectural decisions, and deferred plans; none supersedes current code. |
| [`Lorekeeper.sln`](../../Lorekeeper.sln), [`global.json`](../../global.json), and [`.editorconfig`](../../.editorconfig) | Solution boundary, pinned .NET SDK, and source formatting/naming authority. |
| [`Lorekeeper.Tests/Lorekeeper.Tests.csproj`](../../Lorekeeper.Tests/Lorekeeper.Tests.csproj) and [`Usings.cs`](../../Lorekeeper.Tests/Usings.cs) | Authorized application test-project boundary: the data-safety core for migration, import/export/archive/history preservation and fail-closed behavior, and recovery. |
| [`Lorekeeper.Tests/OpenAiAccountOwnershipMigrationTests.cs`](../../Lorekeeper.Tests/OpenAiAccountOwnershipMigrationTests.cs) | Account/token migration rollback and obsolete discovery-column removal without external OAuth/provider simulation. |
| [`eng/ReleaseDependencyAudit.ps1`](../../eng/ReleaseDependencyAudit.ps1) | Shared fail-closed shipped Electron dependency policy and its narrowly bounded dormant-splash advisory exception. |
| [`scripts/release.ps1`](../../scripts/release.ps1), [`eng/ReleaseWorkflow.ps1`](../../eng/ReleaseWorkflow.ps1), and [`.vscode/tasks.json`](../../.vscode/tasks.json) | Stable release preparation/preview driver, shared build-and-test preflight/version/tag checks, and explicit editor entry points. |
| [`scripts/build-windows-release.ps1`](../../scripts/build-windows-release.ps1), [`scripts/build-macos-release.ps1`](../../scripts/build-macos-release.ps1), and [`scripts/publish-release.ps1`](../../scripts/publish-release.ps1) | Target-native builders and the clean-tree, dual-repository release orchestrator. |
| [`.github/workflows/build-macos-release.yml`](../../.github/workflows/build-macos-release.yml) | Dispatch-only native macOS arm64 build used by the Windows release orchestrator. |
| [`tools/performance/`](../../tools/performance/) and [M0.3 local evidence](../evidence/m0.3-local-performance-baseline.md) | Deterministic, sanitized local fixture generator plus opt-in Release-Electron memory/timing sampler. Generated data, traces, isolated databases, and package manifests remain under ignored `.artifacts/performance/`; the committed evidence report states the reference machine and unsupported workloads. |
| [`tools/distribution/Export-M0DistributionInventory.ps1`](../../tools/distribution/Export-M0DistributionInventory.ps1) and [M0.4 inventory](../research/m0.4-distribution-inventory.md) | Deterministic local release-input inventory. It reads restored managed/Cargo/npm metadata, shipped assets, and the unsigned Windows `win-unpacked` closure; it writes only the committed research JSON/Markdown and records unresolved obligations rather than selecting a license, credential, account, or publication action. |
| [`.codex/config.toml`](../../.codex/config.toml) | Project-only optional Roslynk configuration with a read-only tool allowlist; not an application dependency or final-verification substitute. |
| [`.mcp.json`](../../.mcp.json) and [`.commandcode/settings.json`](../../.commandcode/settings.json) | Project-only optional Command Code Roslynk configuration mirroring the Codex allowlist: the same stdio server with permission rules that expose the read-only tools and deny the mutating tools plus `reload_solution`; not an application dependency or final-verification substitute. |

## Related chapters

- [Architecture index](../architecture.md) — global invariants and routing table.
- [Runtime and host architecture](runtime-host.md) — launch profiles, startup
  gating, Electron behavior, and shared UI validation boundary.
- [Providers and background work](providers-background.md) — integration checks
  that cannot be inferred from builds.
- [Persistence, migrations, and import](persistence-migrations-import.md) —
  data-safety boundaries and portable transformations; this chapter owns the
  complete permitted-test policy.
- [Manuscript authoring](manuscript-authoring.md) — semantic-editor source and
  current manuscript contract.
- [Press production](press-production.md) — native conformance evidence and
  truthful artifact claims.
- [Version history and synchronization](version-history-sync.md) — deterministic
  snapshot, restore, clone, and remote-sync claim boundaries.

## Relevant verification

For a documentation-only refactor, verify local links and paths, configuration
keys against `appsettings*.json` and bound option names, launch profiles against
`launchSettings.json`, and commands against the repository scripts/projects. Search
for stale source names and conflicting current-state claims, inspect the complete
diff, and run `git diff --check`.

For contributor-workflow changes, parse affected PowerShell scripts, inspect any
affected workflow YAML and permissions, compare automated steps to the release
preflight, and confirm every documented command and link.

For source work, apply the completion rule — build and remaining tests — with
the owning chapter's focused checks. Add the semantic-editor, migration/import,
Press, Electron, packaging, or target-native release checks only when their
boundaries are affected; manual and UI validation runs only with the user's
explicit authorization. In the completion report, state exact commands and
results, manual or integration checks performed, checks intentionally not run,
final commit, and working-tree status.

For the M0.3 local performance baseline, build the unsigned Windows Release
package only after confirming `publish/win-x64-stage` and `publish/win-x64` are
disposable ignored outputs. Run the packaged Electron app only with the sampler's
isolated database/history directories and Development environment, which disables
desktop update behavior. The trace path is valid only below
`.artifacts/performance/` and records timestamp, metric, duration, and sequence
only—never manuscript text, credentials, or network traffic. Capture package
inputs, host configuration, process-tree working/private memory every 250 ms,
raw trace, five warm-ups plus 30 samples per metric, and nearest-rank p95. This
is an explicitly authorized local UI session, not an automated browser/UI suite;
terminate the app and retain only the summarized committed evidence.

For M0.4, first confirm `publish/win-x64-stage` and `publish/win-x64` are
disposable ignored outputs, then run
`scripts/build-windows-release.ps1 -KeepUnpacked` without signing or publishing. Run
`tools/distribution/Export-M0DistributionInventory.ps1` twice and compare the
JSON and Markdown SHA-256 hashes. Inspect the generated source-controlled report
for the required missing root license/notice, Bootstrap, branding, macOS-closure,
and account blockers. The inventory may define a future history-audit procedure,
but it must not scan history, add CI, adopt license terms, create credentials,
or modify external distribution state.

For printing changes, syntax-check the print browser modules and desktop hook,
then use explicitly authorized preview checks for source orientation, fit/crop,
margins, custom fonts, backgrounds/transparency, pending saves, PDF page/range
selection, errors, and cancellation. Check that the isolated document has one
sheet per selected surface and excludes editor chrome. Verify the custom host
hook is copied into `.electron/ElectronHostHook/index.js`. Open and cancel the
final printer dialog only when authorized; never infer physical output or
another operating system's driver behavior from a preview or successful build.
