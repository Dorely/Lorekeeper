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

The repository has intentionally narrow automated-test boundaries. Do not add an
automated UI, assistant, ordinary service, provider, authentication, editor,
packaging, or general runtime test merely because a feature would normally invite
one. Existing policy permits application tests only for startup database migration
and versioned project import/export transformation safety, plus Press conformance
tests that map to the native renderer evidence matrix.

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
4. [`README.md`](../../README.md) describes user-facing capabilities, setup, run,
   packaging, release/update behavior, and local-data expectations.
5. [`docs/publishing-roadmap.md`](../publishing-roadmap.md) sequences researched
   delivery and verification gates. Roadmap status does not override current-code
   or architecture claims.
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

Before new work, inspect the branch, working tree, index, remotes, and upstream.
Never edit, commit, or push directly on `main`. Feature work starts on a focused
non-`main` branch created from freshly fetched `origin/main`, with a clean tree
whose index matches `HEAD`. When an upstream exists, fetch it with pruning and
ensure there are no unintegrated commits from `origin/main` or the branch's own
remote tracking branch. Integrate shared-branch changes without rewriting
published history and stop for direction if histories diverge. Without a
remote/upstream, require a clean local `HEAD` and report that synchronization
could not be checked.

Existing changes belong to the user unless proved otherwise. Inspect every diff;
never discard, hide, overwrite, or mix unrelated unfinished work into a feature.
Completed work includes current documentation, removal of obsolete runtime paths,
the relevant verification, full-diff inspection, a focused commit, a clean
post-commit working tree, a pushed work branch, and a pull request targeting
`main`. The change becomes repository-integrated only after its required status
check and independent approval pass, every conversation is resolved, and GitHub
reports the pull request merged. Applied EF migrations remain immutable history.

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

Normal source changes require:

```powershell
dotnet build Lorekeeper.sln
dotnet run --project Lorekeeper --launch-profile http
```

After the successful build, confirm the local host starts without startup
exceptions and terminate it. The explicit HTTP profile serves
`http://localhost:1455` for browser-oriented development and smoke validation;
Electron remains the primary/default debug target. Always plan how to terminate a
host, Electron shell, browser helper, or background process before starting it.

Do not run Playwright, screenshots, browser UI checks, Electron window checks, or
manual UI validation unless the user explicitly requests them. When Electron
startup itself must be checked, run:

```powershell
dotnet run --project Lorekeeper --launch-profile electron
```

Confirm startup and terminate the application. An HTTP startup does not validate
the Electron bridge, update integration, packaged data paths, or native window
behavior.

### Authorized .NET migration/import tests

[`Lorekeeper.Tests`](../../Lorekeeper.Tests/) exists only to prove data
preservation and fail-closed behavior across application-startup database
migrations and versioned project import/export transformations. Its fixtures must
exercise the same production migration/import boundaries as the app; do not create
a parallel migration sequence for test convenience.

Run the full authorized suite with:

```powershell
dotnet test Lorekeeper.Tests\Lorekeeper.Tests.csproj
```

Focused filters are appropriate during an edit loop, but final verification must
cover the affected preservation boundary. For example, project-reference
compatibility uses populated pre-reference migration fixtures and the real v24
export/queued-import path:

```powershell
dotnet test Lorekeeper.Tests\Lorekeeper.Tests.csproj --no-restore -c Release --filter "FullyQualifiedName~ProjectReferenceMigrationTests|FullyQualifiedName~V24ExportWarnsAndImportDoesNotInferProjectReferences"
```

Tests in this project cover manuscript/page/composition/Core/release migrations,
annotation and chat preservation, source-evidence and project-reference cutovers,
protected backup/recovery, current/legacy import adaptation, identifier remapping,
warning behavior, foreign keys, and whole-import rollback. They are not a home for
assistant, UI, ordinary service, or provider behavior tests.

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
physical-product registry identity, and verifies installer/updater artifacts and
checksums. The shared dependency policy permits only the two exact known
`image-size@1.2.1` parser advisories while Electron.NET's generated call remains
provably confined to an unconfigured splash-image path. A package version, call
site, splash configuration, or advisory change fails closed.

The native Apple Silicon builder is
[`scripts/build-macos-release.ps1`](../../scripts/build-macos-release.ps1). It must
run on macOS arm64, performs the corresponding dependency/editor/native-runtime
checks, creates an ad-hoc-signed DMG, verifies architectures/signatures, mounts and
smoke-tests the application, and writes a checksum. Do not substitute a
cross-compiled artifact for that native evidence.

Prepare every version change through a reviewed pull request. Publish Windows-only
or coordinated Windows plus Apple Silicon releases from a fresh clean Windows
orchestration branch whose `HEAD` exactly matches `origin/main`, supplying the
merged release-preparation pull request that produced that commit:

```powershell
.\scripts\publish-release.ps1 -Version <version> -MergedPullRequest <number> -WindowsOnly
.\scripts\publish-release.ps1 -Version <version> -MergedPullRequest <number>
```

The publisher rejects `main`, an unmerged or non-current release-preparation pull
request, and any source commit other than fetched `origin/main`. It lists and
stops for open pull requests targeting `main`; `-ConfirmOpenPullRequests` is an
explicit human-approved override for unrelated open work, never for unmerged
release preparation. The cross-platform path dispatches one correlated macOS
arm64 workflow, builds Windows locally, verifies one requested artifact set,
creates matching draft releases in the private source and public release
repositories, verifies both asset sets, and only then publishes both. On a failed
dual publication it removes the exact releases/tags created by that run when
GitHub permits, and reports any resource requiring manual inspection. Temporary
Actions artifacts are deleted only after successful publication. The public
release repository is the automatic and manual update authority.

Packaging and updater claims require execution on the relevant operating system.
Never claim OAuth, provider calls, embeddings, web search, image generation,
publishing output, packaging, automatic updates, or platform-specific Electron
behavior solely from compilation or static inspection.

## Key files and file families

| File or family | Architectural role |
|---|---|
| [`AGENTS.md`](../../AGENTS.md), [`CLAUDE.md`](../../CLAUDE.md), and [Copilot instructions](../../.github/copilot-instructions.md) | Authoritative workflow rules and compatibility entry points for required routed reading, repository safety, verification, branches, pull requests, documentation, and commits. |
| [`CONTRIBUTING.md`](../../CONTRIBUTING.md), [pull-request template](../../.github/pull_request_template.md), [pull-request validation](../../.github/workflows/pull-request-validation.yml), and [GitHub settings checklist](../github-repository-settings.md) | Human contribution workflow, review evidence, exact repository commit gate, and maintainer-owned server protection settings. |
| [`VISION.md`](../../VISION.md), [architecture index](../architecture.md), and [`README.md`](../../README.md) | Product direction, current technical routing/contracts, and user-facing behavior/setup respectively. |
| [`docs/publishing-roadmap.md`](../publishing-roadmap.md), [`docs/research/`](../research/), [`docs/decisions/`](../decisions/), and [`docs/plans/`](../plans/) | Delivery gates, sourced evidence, historical architectural decisions, and deferred plans; none supersedes current code. |
| [`Lorekeeper.sln`](../../Lorekeeper.sln), [`global.json`](../../global.json), and [`.editorconfig`](../../.editorconfig) | Solution boundary, pinned .NET SDK, and source formatting/naming authority. |
| [`Lorekeeper.Tests/Lorekeeper.Tests.csproj`](../../Lorekeeper.Tests/Lorekeeper.Tests.csproj) and [`Usings.cs`](../../Lorekeeper.Tests/Usings.cs) | Authorized test-project boundary for startup-migration and versioned import/export preservation/fail-closed fixtures only. |
| [`eng/ReleaseDependencyAudit.ps1`](../../eng/ReleaseDependencyAudit.ps1) | Shared fail-closed shipped Electron dependency policy and its narrowly bounded dormant-splash advisory exception. |
| [`scripts/build-windows-release.ps1`](../../scripts/build-windows-release.ps1), [`scripts/build-macos-release.ps1`](../../scripts/build-macos-release.ps1), and [`scripts/publish-release.ps1`](../../scripts/publish-release.ps1) | Target-native builders and the clean-tree, dual-repository release orchestrator. |
| [`.github/workflows/build-macos-release.yml`](../../.github/workflows/build-macos-release.yml) | Dispatch-only native macOS arm64 build used by the Windows release orchestrator. |
| [`.codex/config.toml`](../../.codex/config.toml) | Project-only optional Roslynk configuration with a read-only tool allowlist; not an application dependency or final-verification substitute. |

## Related chapters

- [Architecture index](../architecture.md) — global invariants and routing table.
- [Runtime and host architecture](runtime-host.md) — launch profiles, startup
  gating, Electron behavior, and shared UI validation boundary.
- [Providers and background work](providers-background.md) — integration checks
  that cannot be inferred from builds.
- [Persistence, migrations, and import](persistence-migrations-import.md) — the
  only application behaviors eligible for `Lorekeeper.Tests` alongside versioned
  imports.
- [Manuscript authoring](manuscript-authoring.md) — semantic-editor source and
  current manuscript contract.
- [Press production](press-production.md) — native conformance evidence and
  truthful artifact claims.

## Relevant verification

For a documentation-only refactor, verify local links and paths, configuration
keys against `appsettings*.json` and bound option names, launch profiles against
`launchSettings.json`, and commands against the repository scripts/projects. Search
for stale source names and conflicting current-state claims, inspect the complete
diff, and run `git diff --check`. Run the solution build, authorized test project,
and HTTP startup smoke check when the documentation asserts those commands and
current contracts, terminating the host afterward.

For contributor-workflow changes, parse PowerShell scripts, inspect workflow YAML
and permissions, compare the workflow steps to the exact repository commit gate,
and confirm every documented command and link. After the workflow reaches
`main`, validate the protected-branch settings and exercise them with a disposable
pull request as described in the GitHub settings checklist.

For source work, combine the baseline build/startup check with the owning
chapter's focused checks. Add the semantic-editor, migration/import, Press,
Electron, packaging, or target-native release checks only when their boundaries
are affected. In the completion report, state exact commands and results, manual
or integration checks performed, checks intentionally not run, final commit, and
working-tree status.
