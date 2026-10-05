# Lorekeeper v1 acceptance evidence

This record covers preparation of app version **1.0.0**. Publication, repository
visibility changes, Store submission, external media uploads, and archival of the
old release repository are separate launch actions.

## Impact and acceptance plan

1. Resolve source licensing, redistribution notices, branding provenance, public
   documentation, funding, and a sanitized full-history/GitHub clearance report.
2. Prepare main-only runtime update discovery, the final dual-repository v1
   publication boundary, unsigned Store submission and separate local validation,
   and exact-source local WSL Linux packaging. Preserve the Mac release workflow.
3. Exercise recent authoring, World/Voice, Designed Pages, Sources, rich Word
   insertion, archive/history/recovery changes, then a representative synthetic
   create → write → design → review → export → import/restore workflow. Reproduce
   and fix regressions through their owning services; retest affected scenarios.
4. Exercise live connections using synthetic QA projects in the current app.
   Use `gpt-6-luna` for OpenAI and record each actual configured model separately.
   Keep credential-free offline/recovery/package projects in isolated databases.
5. Capture the accepted application build in a separate synthetic trailer project;
   create the 60-second MP4, captions, poster, original audio, and export recipe.
6. Run final build/data-safety/native checks, review complete diffs and notice
   closure, commit coherent preparation changes, and record exact commits and
   artifacts. Do not infer platform or provider acceptance from compilation.

## Required scenario ledger

| Area | Scenarios | Result |
|---|---|---|
| World and Voice | Autosave, rapid selection/navigation, exact-revision conflicts and retry, relevant chapter context, checkpoints and portable-state preservation | Pending |
| Designed Pages | Library insertion, repeated placements, move/remove, rapid save/navigation, writer disposal, independent Core/release page overrides | Pending |
| Authoring | Immediate Undo/Redo, in-flight saves, reload/restart recovery, truthful save status, dependent-operation fence while recovery is incomplete | Pending |
| Sources | Full PDF and explicit page cap, index-only mode, retained-original recovery, missing embeddings, resume/cancellation, interrupted legacy conversion | Pending |
| Rich manuscripts | DOCX/Word insertion, tables, notes, citations, warnings, Undo, Read preview, EPUB/PDF/DOCX output | Pending |
| Data safety | Full/non-structural archives, checkpoints and restore, assets/evidence preservation, disposable migration and recovery | Pending |
| Providers | Ordinary reply, tool read/mutation, follow-up, cancellation, model selection, replay and supported reasoning for each tested connection/model | Pending |
| Windows | Free installer/portable closure, isolated data/history, MSIX local validation, uninstall preservation, Store update suppression | Packaging/local MSIX passed on the recorded candidate; installed acceptance pending |
| Linux | Exact committed source, Ubuntu 24.04 x64 WSL build, native dependencies, DEB/AppImage closure and checksums, desktop install/launch | Build/tests and package verification passed on the recorded candidate; desktop acceptance pending |
| Mac | Existing native build and package evidence | Unperformed; no local Mac host |
| Update handoff | Old install discovers v1, both draft sets match, anonymous main downloads and installed v1 main-feed transition | Launch-time check; not performed during preparation |

## Current environment constraints

- The original user-owned debug host was preserved and then closed by the user.
  QA-owned hosts started afterward and were terminated. The new current-data
  host answered HTTP requests, but native capture repeatedly timed out and the
  browser controller remained unavailable after reconnection. No provider
  calls or synthetic current-app projects were created through those attempts.
- WSL Ubuntu intermittently reports `ERROR_NO_SYSTEM_RESOURCES`. The retry of
  candidate `6ba993a5103accef77b5a878903027c15023d17f` started Ubuntu 24.04
  successfully, exported exact source into isolated ext4 storage, and passed
  npm installation/audit. Packaging then failed closed because the rebuilt
  semantic-editor bundle differed from the committed bytes. No Linux package
  was produced by that attempt. The canonical archive fix passed the exact
  bundle gate on candidate `441c96e3ac208b67ec0a0bb85f6be6e3fa73011d`.
  That candidate passed the Linux build and all permitted tests and produced
  both packages, then stopped before payload verification because its npm-lock
  parser rejected the empty root-package key. No artifacts were copied out.
  The parser correction and native graphical acceptance require a retry.
  The corrected parser passed on candidate
  `f05aca9166250a689784ff2bdd286d9ed69d0385`; final validation then rejected the
  pinned runtime's optional LTTng tracing provider, whose old `so.0` dependency
  is absent on Ubuntu 24.04. The exact supplier source tolerates that load
  failure. A hash/version/path-bound optional-dependency report is implemented;
  mandatory and unknown library failures remain fatal. Candidate `9eec7b8`
  subsequently passed final package validation and verified output transfer.
- The user subsequently stopped Computer Use with the physical Escape key.
  Desktop interaction stopped immediately; no further UI/provider/trailer
  acceptance is inferred or performed in that turn.
- Production MSIX requires owner-supplied Partner Center identities. Store
  certification is external acceptance and remains pending.
- No automated test suites will be added outside the existing data-safety and
  Press conformance boundaries. Manual/UI QA was explicitly authorized.

## Preparation fixes found during review

- Read-only code/caller analysis established three World/Voice regressions:
  same-project Voice `character` query changes retained the previous character,
  and a failed writer registration left every recovery action disabled even
  after the competing writer or fence ended; disposal also released a writer
  after a failed save and could unblock dependent work. The focused correction routes
  query changes through the existing save-before-selection guard and allows the
  existing retry action to reacquire its exact target while input remains
  disabled until ownership succeeds. It preserves dirty revision/content tokens,
  serializes acquisition, and drains acquisition/saves before disposal. Disposal
  releases only a clean writer; a failed draft remains an unreachable fence
  blocker, and late reload callbacks cannot clear it. Text drafts have no local
  recovery journal. Hard shutdown/circuit destruction does not promise draft
  recovery; restart clears the blocker without recovering failed text.
  Affected UI retesting remains pending after the physical Escape pause. The
  prior `9eec7b8` package evidence is not functional acceptance of this corrected
  source. Designed Page acquisition/disposal and journal recovery were also
  reviewed in code; rapid navigation and restart acceptance remain pending.
- Stable update discovery now rejects prerelease SemVer tags even when GitHub's
  release flag says stable. Main is the sole allowed update feed, and Linux
  discovery requires the exact AppImage and DEB names.
- Release notice checks use the actual isolated restore output rather than a
  potentially stale repository `obj` directory. Retained license bytes are
  excluded from Git newline conversion.
- SDK runtime licenses and notices are retained under distinct paths to avoid
  case-insensitive collisions with the first-party root files. Package checks
  bind their exact identities to `includedFrameworks` and the packaged RID.
- Release builds pin .NET/ASP.NET 10.0.12 and restore with the publish profile and
  self-contained settings. The previous Windows package used 10.0.0 while the
  prepared WSL SDK selected 10.0.12. Earlier package checks must be repeated on
  this final runtime and notice closure.
- Linux source export now sets Git's line-ending configuration per archive
  command. Windows archive conversion had inserted 25 CR bytes into the editor
  bundle; the Linux rebuild matched the committed LF blob exactly. The exact-byte
  reproducibility gate remains unchanged.
- Mac packaged startup validation now keeps upstream Electron authentication
  output in private temporary files, uses disposable SQLite/history/Electron
  state, and refuses an occupied port. Static checks passed; native Mac evidence
  remains unperformed.
- Linux parses npm's lockfile as a dictionary so its standard empty root key
  survives. The declared, locked and installed Electron identity checks remain.
- Ubuntu DEB metadata now includes the official .NET runtime prerequisites and
  uses the Ubuntu 24.04 GTK/AT-SPI package names. Build prerequisites and final
  DEB metadata are checked against the same declared inventory. The optional
  LTTng exception requires the exact reviewed provider bytes and runtime;
  unavailable tracing is recorded explicitly without an ABI alias.
- Failed publication retains all drafts, releases and tags for inspection. The
  former draft-check/delete sequence could delete a release finalized by another
  actor between those calls; the publisher no longer performs release deletion.
- Public AppImage publication now has a separate, blocked source/relink clearance
  record. A later approved corresponding-source archive must be a verified Linux
  release asset; current private preparation cannot qualify without actual
  modified-library relink/repack and exact provenance review.

## Verification and artifact identity

Preparation checks on the current v1 worktree:

- The focused World/Voice corrections were committed as
  `176bd2430d44ee001417166cf845335d93110268`. The final shared
  `Invoke-ReleasePreflight` passed with zero build warnings/errors, **167
  data-safety tests**, and **47 Press unit plus 104 conformance checks**.
  The ignored log is `.artifacts/v1-worldvoice-fix-final-preflight.log`, SHA-256
  `ae5adf0c85b163ef3455bc1894dbfcac0fb1eba749e7dd29894eb90ce6e1b5d6`.
  This verifies the corrected source build and permitted tests. Affected manual
  retesting remains pending; it does not declare an accepted application build
  or extend the prior `9eec7b8` package evidence to the corrected source.
- `dotnet build Lorekeeper.sln --artifacts-path .artifacts/v1-sharing/build`:
  passed, zero warnings/errors. Isolated outputs avoid altering the user's
  original debug output. A first attempt during notice generation failed only
  because the required notice file was not yet generated; the completed closure
  build passed. A later parallel build collided with the Press conformance
  process over its executable; the sequential rerun passed. The final shared
  `Invoke-ReleasePreflight` also passed after the release-runtime corrections:
  zero build warnings/errors and **167 data-safety tests passed**.
- `dotnet test Lorekeeper.Tests/Lorekeeper.Tests.csproj --artifacts-path
  .artifacts/v1-sharing/build --no-build`: **167 passed**, none failed/skipped.
- `cargo test --locked` in `Lorekeeper.Press`: **47 unit and 104 conformance
  checks passed**; doc tests had no cases.
- Exact notice freshness check: the final preflight verified all **71 retained
  package identities** and their exact notice bytes, including the two active
  framework-asset versions. Windows Store and Free package builders previously
  passed their own managed, semantic-editor, dependency, native Press, and
  packaged notice checks on the recorded candidate; later source candidates
  require corresponding rebuilds. The final
  shared preflight also passed **47 Press unit and 104 Press conformance checks**
  with the locked dependency graph.
- Local MSIX validation passed semantic MakeAppx pack/unpack, embedded CMS
  integrity and exact ephemeral signer checks, and certificate/key cleanup. No
  trusted root entry or installation was performed. Source 1.0.0 mapped to
  2.0.0.0. Mode/path/identity guards and rejection of a Free-channel input passed.
- Disposable credential-free database startup on port 1466 completed migration
  and returned HTTP 200 with the Projects surface. This is startup evidence only.
- A QA-owned Electron host was started after port 1455 became free. Its HTTP
  surface responded, but window capture failed twice with `window capture timed
  out: timed out waiting on channel`. The separate browser control session also
  timed out after reconnecting. Both hosts were terminated and their ports freed.
  The direct Debug Electron invocation did not establish a correctly styled
  packaged UI; it must be repeated against the Free release package when UI
  control is restored. No UI scenario in the ledger is marked passed from HTTP.

Record the final verified commit and artifact hashes after native packaging.
Trailer preparation currently includes title, 60-second English captions and a
gated export recipe and an original 60-second stereo audio preview under ignored
`.artifacts/trailer/v1/audio/`; no real footage, poster, or finished MP4 is claimed.
Capture must wait for functional acceptance, using synthetic content only.

### Candidate package checks

Package validation source: `9eec7b825c0052299beccb844c544aea875490c3`.
Source tree: `33bb4bf61a797b490408f9b47cbdee939fd3f67b`.
This identifies package preparation, not functional acceptance.

- Final Windows Store and Free builds passed with zero managed warnings/errors,
  actual 1.0.0/candidate assembly metadata and the correct immutable channel,
  .NET/ASP.NET 10.0.12, native Press and retained notice closure.
- Free installer SHA-256:
  `b9f99d5348be882a2c1d6cb7b96352db43be11dd50da7607615273cac16be0af`.
  Portable SHA-256:
  `fd0ec088baff1769245158cc09e63693ac2ee76fc382bc45b23c05af975777fc`.
  Actual Authenticode inspection found those artifacts and app/Press executables
  unsigned. The bundled Microsoft `createdump.exe` retains its valid signature.
- Local MSIX SHA-256:
  `8f690541ed4154e920b8548fc838bbacebe95e4723216ca83a746a3bd00fddbc`.
  Semantic MakeAppx pack/unpack, CMS integrity/exact signer, certificate absence,
  and unchanged private-key-file inventory passed on Windows 10 build 19045 with
  SDK 10.0.26100. No installation or trust-chain acceptance was performed.
- Detailed logs, identity/hash evidence, and the pinned DEB installation-template
  review remain in ignored `.artifacts/v1-sharing/windows-final-9eec7b8/`.
  The upstream DEB template provides a scoped AppArmor user-namespace profile;
  AppImage desktop startup on restrictive Ubuntu remains unverified.
- Linux on that candidate passed zero-warning/error Release compilation,
  **167 data-safety**, **47 Press unit** and **104 Press conformance** checks.
  Unpacked, AppImage-extracted and DEB-extracted closures passed editor, native
  ELF/dependency, Press and notice checks. The AppRun hash/mode, sandbox flags,
  exact original runtime prefix, omitted legacy libraries and declared DEB
  dependencies passed. The wrapper verified transferred bytes and cleaned its
  isolated source; local packages, checksums, provenance and unpacked closure
  remain under ignored `publish/linux-x64/`.
- AppImage SHA-256:
  `3a438833501c08ae5e5fcb43c9ef6485310926718c18c58f914ec7a2565ddbc6`.
  DEB SHA-256:
  `94f1a4ea85f20e9dacacbfb590f386b88f2531de5dc0d93ed8b5b754ec7dd599`.
  Native run log SHA-256:
  `bb9cd93b8ff6b4c5cd4c10611ce4fecfc884bf38fc48cd52f89ed884444c4880`.
  Provenance SHA-256:
  `c0bfe231a8dcf30c49dd29670cedcc0a1d4b024eb15de63d3c49b6d179e5a31f`.
- The native provenance explicitly records optional CoreCLR LTTng tracing as
  unavailable, with integration validation false. The builder also warned that
  desktop window association lacks `desktopName`; this is a minor metadata
  finding pending correction and desktop verification. No Linux application,
  desktop installer, provider, printing or update workflow was launched.
- Standard `scripts/release.ps1 -Version 1.0.0 -CheckOnly` passed: both repository
  permissions/tag checks, retained Mac workflow readiness, WSL prerequisites,
  final build/tests/notices and unchanged clean HEAD. The blocked AppImage
  publication warning was expected; local readiness checks continued. No version
  commit, push, package build, release creation or visibility change was performed
  by preview.
  Preview log SHA-256:
  `98bdf593c9b18c4b734a1370622f6cd802bab52edb07057b8c65a3a7d5d3263a`.

This preparation is **not signed off** while any data-loss, credential exposure,
broken core workflow, installation, or update blocker remains unresolved.
