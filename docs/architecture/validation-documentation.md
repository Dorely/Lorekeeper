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
5. [`docs/research/`](../research/) keeps sourced requirements that still
   govern current behavior. [`docs/decisions/`](../decisions/) keeps decisions
   that still shape the current runtime, and [`docs/evidence/`](../evidence/)
   keeps the trailer QA and sharing audit. None of them is proof of
   current implementation.

The repository does not keep roadmaps, plans, handoffs, superseded decisions, or
retired spike evidence; Git history retains them.

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
product-direction or scope change. Applied EF migration files are immutable.
Delete research, decisions, and evidence once they no longer describe the
current product rather than keeping them as history. Remove stale examples,
comments, settings, and instructions in the same change that makes them obsolete.

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

Windows uses `scripts/build-windows-release.ps1`; Linux uses
`scripts/build-linux-release-wsl.ps1` on Windows or
`scripts/build-linux-release.ps1` natively on Ubuntu 24.04 x64.
The release, packaging, MSIX, and distribution scripts require PowerShell 7;
they use .NET APIs and operators that Windows PowerShell 5.1 lacks.
The builders use exact dependency/editor/Press inputs, check immutable channel
metadata and notice hashes, probe the packaged renderer contract, and verify
artifact checksums. Windows outputs NSIS/portable executables; Linux outputs
`Lorekeeper-<version>-x86_64.AppImage` and
`Lorekeeper-<version>-amd64.deb` with source/archive provenance. macOS
electron-builder keeps only `Electron.app` from the runtime zip, so the builder
copies Electron's `LICENSE` and `LICENSES.chromium.html` from that release zip, verified
against the installed `electron` package checksums, into
`Contents/Resources`, then re-signs ad hoc and rebuilds the DMG. WSL packaging
uses an exact committed Git archive in isolated ext4 storage and local compute.
Its archive command sets `core.autocrlf=false` and `core.eol=lf` only for that
invocation, so Windows Git cannot change committed text bytes before the native
build. The provenance records those archive settings; retained license evidence
keeps its `-text` attributes. The editor gate still requires an exact byte hash
match with the committed bundle.
Linux reads the npm lock as a dictionary, preserving its empty root-package key;
declared, locked and installed Electron versions must still match.
Linux verification passively reads the actual `resources/app.asar` manifest in
the unpacked payload and extracted AppImage/DEB through installed pinned
`@electron/asar` 3.4.1. The reader accepts only a regular packed `package.json`
of at most one MiB and parses JSON without executing packaged application code.
It checks application/version/desktop identity and identical ASAR/manifest hashes
across all three copies. Each package must contain exactly one expected desktop
entry with matching name, class, icon and executable command; existing launcher
hash/mode and sandbox checks remain. Required key spelling and duplicate-key
checks use ordinal comparison, as required by the
[Desktop Entry format](https://specifications.freedesktop.org/desktop-entry/latest/basic-format.html).
Provenance retains the actual manifest and
desktop-entry hashes. These static checks do not validate installed launch,
window/icon association, Wayland/X11 behavior, or AppArmor acceptance.
Release builds pin .NET/ASP.NET runtime 10.0.12 in the project. Builders restore
with the target Release publish profile and self-contained settings before
publishing without restore, so every platform selects the reviewed runtime.
Retained runtime notices cover the current Debug and Release dependency graphs;
the packaged runtime configuration must match an exact retained identity.
Ubuntu dependency metadata includes the official .NET self-contained prerequisites
as well as Electron/Skia dependencies. Native validation rejects unresolved
libraries except the exact CoreCLR 10.0.12 optional tracing provider's
`liblttng-ust.so.0` dependency: its managed-root path, runtime version and original
provider SHA-256 must all match, and every other missing library remains fatal.
The [pinned runtime source](https://github.com/dotnet/dotnet/blob/95017c711e6afc1085133d440e42b4bd78155701/src/runtime/src/coreclr/pal/src/misc/tracepointprovider.cpp#L55-L114)
tolerates that module's load failure. Ubuntu's newer LTTng ABI is not aliased;
provenance records optional LTTng tracing as unavailable rather than working.
The pinned Linux launcher in `eng/linux/AppRun.sh` must retain its exact source
hash and executable mode and never add `--no-sandbox`; native desktop/AppArmor
acceptance remains separate evidence.
The supported toolset override selects SHA-pinned AppImage tools 1.0.3 and the
official 20251108 static runtime, excludes old optional compatibility libraries,
and checks the final runtime prefix and empty compatibility-library directory.
The retained notice manifest supplies the download URL, hashes, release and
source commit; the builder checks field syntax, official supplier path families,
the pinned configuration, and matching runtime/libfuse component versions.
Seven exact AppImage component source/version records, full terms and per-file
musl copyright/license blocks are retained under `licenses/appimage-runtime/`,
with the original modified-libfuse patch/date notice. The notice generator checks
their hashes and every package retains that evidence. The static runtime's
modified LGPL libfuse is satisfied by publishing a corresponding-source archive
beside every AppImage: modified libfuse and runtime sources, build scripts,
recipient instructions, and a recorded modified-libfuse relink and AppImage
repack. The distribution inventory admits the runtime only while that record is
approved.

`eng/appimage-publication.json` is the explicit, approved public AppImage
boundary. The shared release helper binds its runtime/source/toolset
identity and notice-manifest hash to the selected evidence. An approved record
requires reviewed archive, source-manifest, recipient relink/repack log and output
hashes. The archive contains unique nonempty regular `sources.json`,
`recipient-validation/relink.log` and `recipient-validation/repack.log` members;
the helper reads and verifies them without extracting archive paths. Source
metadata is limited to one MiB and must bind the selected runtime release,
source commit and hash, toolset version and archive hash, and modified-libfuse
version and patch hash. Source review and approval remain manual attestations,
not proof created by the helper.
The normal driver checks before a version commit/push; the publisher checks
before builds/Actions and again against the staged archive and actual runtime
provenance before draft creation. That source archive is mandatory in both v1
Linux asset sets and their checksum/download verification. If the record is
set back to blocked, `-CheckOnly` reports blocked publication while validating
local build readiness; local builders do not require public clearance. No bypass
is provided.

The dependency audit permits only the two exact known
`image-size@1.2.1` advisories while Electron.NET's optional splash call is
unreachable. Changed package, call, splash configuration, or advisories fail
closed. `Export-ThirdPartyNotices.ps1 -CheckOnly` verifies the exact NuGet package
set/content identities, retained full upstream texts, and deterministic root
notice. Refresh evidence deliberately after package changes and review the
result. First-party dual PolyForm terms are an owner decision separate from the
permissive third-party gate. libgit2 is admitted only for the exact unchanged
compiled library linked into Lorekeeper under its full retained unlimited
linking exception; modified or standalone library distribution is not admitted.

For Store preparation:

```powershell
.\scripts\build-windows-release.ps1 -KeepUnpacked -DistributionChannel Store
.\tools\msix\Build-WindowsMsix.ps1 -IdentityPath .\tools\msix\store-identity.json
.\tools\msix\Build-WindowsMsix.ps1 -LocalValidation -CheckOnly
```

The production identity comes from Partner Center and the production package is
unsigned for Store signing. `-LocalValidation` uses its separate identity and
an ephemeral signer, verifies CMS integrity, and deletes certificate and private
key without trusting or installing the package. `-CheckOnly` performs neither
package nor certificate operations. Output stays under ignored
`.artifacts/msix/store/` or `.artifacts/msix/local-validation/`.
Channel and informational-version inspection uses bounded passive PE metadata,
never loads the application assembly, and closes the reader and stream before
any later package build in the same PowerShell process. Malformed metadata or
unexpected framework attribute constructors fail closed.
MakeAppx runs semantic validation without `/nv`. The manifest deliberately
uses the full-trust desktop model; it does not imply AppContainer confinement.
Source SemVer maps to MSIX `major+1.minor.patch.0`.
Install, upgrade, data retention, Windows 11, OAuth/print/file behavior,
Store submission, and certification require separately authorized target evidence.
The free price and BYO-provider charges are documented in the MSIX runbook.

The native macOS arm64 builder performs dependency/editor/Press checks and
creates an ad-hoc-signed DMG. Its app startup exercise requires explicit manual
authorization. The startup check refuses an occupied TCP port 1455, gives only
its child process absolute temporary SQLite/history paths and separate Electron
user data, and captures stdout/stderr in private temporary files that are never
echoed or uploaded. It resolves the macOS temporary-directory symlink prefix,
terminates the owned process tree, and deletes the isolated files afterward.
These controls require native Mac validation before making platform claims.
The dispatch-only macOS workflow supplies native Mac compute;
general CI, hosted validation, and hosted Linux compute remain excluded.
Mac App Store feasibility still lacks a signed MAS runtime, entitlements,
publisher/profile inputs, and real-device acceptance. Direct-DMG evidence is not
MAS or notarization evidence.

The stable driver `scripts/release.ps1` owns clean-main version selection, the
version-only commit, preflight before that commit and again on its exact source,
normal push, native packaging, and publication. `-CheckOnly` previews without
editing, committing, pushing, packaging, or publishing. `-WindowsOnly` omits
Linux and macOS; `-LinuxDistribution` selects the local WSL distribution.
Completed feature files must already be committed. Divergence, unrelated dirty
work, altered verification source, staged/concurrent version changes, and
partial publication require inspection; no reset, force push, or tag overwrite
is performed.

The publisher requires clean source at fetched `origin/main`, matching project
version, and `-AllowDirectMainPush`. Exact v1.0.0 creates the final bridge in both
repositories; all later releases use `Dorely/Lorekeeper` only, and this is the sole
runtime update authority. At v1 launch, the additional explicit
`-MakeSourcePublicAfterV1` flag publishes the verified asset sets first, changes
source visibility second, then performs fresh credential-free metadata and full
asset-download/hash checks. Private main is allowed during v1 preview/preparation.
Failed publication retains all drafts, finalized releases and tags for manual
inspection against the staged assets. There is no automatic release/tag deletion,
so a concurrent draft-to-published transition cannot lose the old-feed bridge.
Preparation never runs this publishing path,
changes visibility, removes branches, rewrites history, or archives the old feed.
An installed v0.3.x handoff remains required before that feed becomes immutable.

Raw mirror/scanner/GitHub evidence remains ignored and private; only sanitized
scope, fingerprints, decisions, counts, and limits belong in
`docs/evidence/public-sharing-audit.md`. Recheck final exact refs before public
visibility and resolve contribution rights or content findings. A successful
build never substitutes for historical artifact, platform, UI, provider,
Store, or update evidence.

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

## Key files and file families

| File or family | Architectural role |
|---|---|
| [`AGENTS.md`](../../AGENTS.md), [`CLAUDE.md`](../../CLAUDE.md), and [Copilot instructions](../../.github/copilot-instructions.md) | Authoritative workflow rules and compatibility entry points for required routed reading, repository safety, verification, documentation, and commits. |
| [`VISION.md`](../../VISION.md), [architecture index](../architecture.md), and [`README.md`](../../README.md) | Product direction, current technical routing/contracts, and user-facing behavior/setup respectively. |
| [`version-history-sync.md`](version-history-sync.md) | Current deterministic snapshot, local Git, restore/import, remote-sync, and version-control credential boundary. |
| [`docs/research/`](../research/), [`docs/decisions/`](../decisions/), and [`docs/evidence/`](../evidence/) | Current sourced requirements, the owned-renderer decision, and the trailer QA and sharing audit; none supersedes current code. |
| [`Lorekeeper.sln`](../../Lorekeeper.sln), [`global.json`](../../global.json), and [`.editorconfig`](../../.editorconfig) | Solution boundary, pinned .NET SDK, and source formatting/naming authority. |
| [`Lorekeeper.Tests/Lorekeeper.Tests.csproj`](../../Lorekeeper.Tests/Lorekeeper.Tests.csproj) and [`Usings.cs`](../../Lorekeeper.Tests/Usings.cs) | Authorized application test-project boundary: the data-safety core for migration, import/export/archive/history preservation and fail-closed behavior, and recovery. |
| [`Lorekeeper.Tests/OpenAiAccountOwnershipMigrationTests.cs`](../../Lorekeeper.Tests/OpenAiAccountOwnershipMigrationTests.cs) | Account/token migration rollback and obsolete discovery-column removal without external OAuth/provider simulation. |
| [`eng/ReleaseDependencyAudit.ps1`](../../eng/ReleaseDependencyAudit.ps1) | Shared fail-closed shipped Electron dependency policy and its narrowly bounded dormant-splash advisory exception. |
| [`scripts/release.ps1`](../../scripts/release.ps1), [`eng/ReleaseWorkflow.ps1`](../../eng/ReleaseWorkflow.ps1), and [`.vscode/tasks.json`](../../.vscode/tasks.json) | Stable release preparation/preview driver, shared build-and-test preflight/version/tag checks, and explicit editor entry points. |
| [`scripts/build-windows-release.ps1`](../../scripts/build-windows-release.ps1), [`scripts/build-macos-release.ps1`](../../scripts/build-macos-release.ps1), and [`scripts/publish-release.ps1`](../../scripts/publish-release.ps1) | Target-native builders and the clean-tree main-repository publisher, with the final v1 old-feed handoff. |
| [`scripts/build-linux-release.ps1`](../../scripts/build-linux-release.ps1), [`scripts/build-linux-release-wsl.ps1`](../../scripts/build-linux-release-wsl.ps1), and [`eng/linux/AppRun.sh`](../../eng/linux/AppRun.sh) | Local native/WSL exact-source Linux package and provenance checks; source-owned launcher sandbox behavior is owned by runtime-host.md. |
| [`tools/msix/Build-WindowsMsix.ps1`](../../tools/msix/Build-WindowsMsix.ps1), [runbook](../../tools/msix/README.md), and [listing inputs](../../tools/msix/store-listing.md) | Exact Partner Center identity, unsigned production MSIX, separate ephemeral local validation, free listing, and pending installed/certification boundary. |
| [`.github/workflows/build-macos-release.yml`](../../.github/workflows/build-macos-release.yml) | Dispatch-only native macOS arm64 build used by the Windows release orchestrator. |
| [`tools/distribution/Export-ThirdPartyNotices.ps1`](../../tools/distribution/Export-ThirdPartyNotices.ps1), [`licenses/third-party/sources.json`](../../licenses/third-party/sources.json), [`licenses/appimage-runtime/sources.json`](../../licenses/appimage-runtime/sources.json), and [`THIRD-PARTY-NOTICES.txt`](../../THIRD-PARTY-NOTICES.txt) | Exact package/runtime source/full-text evidence retention and offline deterministic notice checks; dependency changes require deliberate refresh/review. AppImage source/relink clearance is separate from notice inclusion. |
| [`tools/distribution/Export-M0DistributionInventory.ps1`](../../tools/distribution/Export-M0DistributionInventory.ps1), [inventory](../research/m0.4-distribution-inventory.md), and [public-sharing audit](../evidence/public-sharing-audit.md) | Local dependency/notice and platform-prerequisite inventory plus sanitized all-ref/GitHub clearance; detailed findings stay ignored/private and no publication is performed. |
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

The dependency inventory began with M0.4 and now records the retained v1 license
and notice decisions. Confirm generated Windows directories are disposable before
building an unpacked closure. Run
`tools/distribution/Export-M0DistributionInventory.ps1` twice and compare the
JSON and Markdown SHA-256 hashes. Inspect resolved obligations and remaining
platform, rights, and account findings against actual evidence. Inventory generation
does not scan history or modify credentials or external distribution state;
the separately authorized sharing audit has its own scope and private evidence.

For printing changes, syntax-check the print browser modules and desktop hook,
then use explicitly authorized preview checks for source orientation, fit/crop,
margins, custom fonts, backgrounds/transparency, pending saves, PDF page/range
selection, errors, and cancellation. Check that the isolated document has one
sheet per selected surface and excludes editor chrome. Verify the custom host
hook is copied into `.electron/ElectronHostHook/index.js`. Open and cancel the
final printer dialog only when authorized; never infer physical output or
another operating system's driver behavior from a preview or successful build.
