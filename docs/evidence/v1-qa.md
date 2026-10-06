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
   Use `gpt-6.1-sol` for OpenAI except compatibility showcases, and record each
   actual configured model separately.
   Keep credential-free offline/recovery/package projects in isolated databases.
5. Capture the accepted application build in a separate synthetic trailer project;
   create the 60-second MP4, captions, poster, original audio, and export recipe.
6. Run final build/data-safety/native checks, review complete diffs and notice
   closure, commit coherent preparation changes, and record exact commits and
   artifacts. Do not infer platform or provider acceptance from compilation.

## Tested source

Tested source commit: d092bd696e46086e6cefd7cb9bfe50e6ef295f03

Functional QA ran against the Debug Blazor Server host built from the commits
listed under "Regressions fixed during QA", ending at the commit above. Each
fix was retested in the scenario that exposed it. The recorded Windows and
Linux packages below were built from `182cf1e` and predate these fixes; they
must be rebuilt from the tested commit before installed-platform acceptance.

Two databases were used, both holding only synthetic, clearly named QA
projects:

- **Isolated QA database** (port 1466, a fresh disposable SQLite file): the
  create → write → design → review → export → import/restore workflow in
  "The Cartographer's Tide" (synthetic), the archive-restore projects, the
  trailer project's original, and a local Ollama connection. This database had
  no credentials copied into it.
- **The owner's development database** (port 1455), used with the owner's
  permission because OpenAI account sign-in tokens live in the database and the
  OAuth callback is fixed to that port. Only the projects "QA Live Providers
  (synthetic)" and "QA v1 Trailer — The Lantern Atlas" were created or changed.
  Provider and credential settings screens were never opened, and no
  credential was read, copied or moved. Existing projects were not opened.

## Required scenario ledger

| Area | Scenarios | Result |
|---|---|---|
| World and Voice | Brief autosave with Unicode, Graph node creation, character Voice text, writing sample, Voice assistant tool flow | Passed |
| Designed Pages | Library creation, insertion, text frame editing, overflow observer, image frame, move, Undo/Redo, reload and host-restart persistence, Print preview, editor preview card after load/undo | Passed after fixes 04b6f3c, c9b1e44, a616966 |
| Authoring | Typing and autosave, Undo/Redo, truthful save status, journal replay after host restart, lost-window recovery without a wedged project | Passed after fixes f6ab262, a415f5a, 973cfbb, a616966, d092bd6 |
| Sources | Bibliographic record, pending extraction publication, citation with locator, Read-mode note rendering | Passed after fix 46e6253 |
| Rich manuscripts | DOCX insertion (heading, emphasis, table, footnote), editor tables, footnotes/endnotes, citations, Undo, Read preview; EPUB, reading PDF and DOCX output | Passed after fixes 70e278c, e9ff227, 02bb97f, 1adde9e, a37e568, 10415de, 9cd9693, 1fa6ac3 |
| Review and history | Review Edits on: tracked insertion, Keep single change, Keep All, Reject, header clearing; checkpoint, compare, restore | Passed after fix 99ec3f6 |
| Publishing | Core cover artwork, Fill canvas/Fit width, Undo, quick preview; EPUB Ebook and PDF Ebook releases, Prepare files, EPUB preview (10 locations incl. fixed-layout plate) | Passed after fixes a40889a, 4aa2e68, 93d85eb, 2068577 |
| Data safety | Full archive download (18 entries), import into a fresh project with text, tables, notes, citation, sources and styles identical; import performance | Passed after fixes e9e9386, cd73cb8 |
| Providers | Ordinary reply, tool read/mutation, follow-up, cancellation, model selection, replay and supported reasoning for each tested connection/model | Passed (see "Live providers") |
| Windows | Free installer/portable closure, isolated data/history, MSIX local validation, uninstall preservation, Store update suppression | Packaging/local MSIX passed on `182cf1e`; rebuild from the tested commit and installed acceptance not performed |
| Linux | Exact committed source, Ubuntu 24.04 x64 WSL build, native dependencies, DEB/AppImage closure and checksums, desktop install/launch | Build/tests and package verification passed on `182cf1e`; desktop acceptance not performed |
| Mac | Existing native build and package evidence | Not performed; no local Mac host |
| Update handoff | Old install discovers v1, both draft sets match, anonymous main downloads and installed v1 main-feed transition | Launch-time check; not performed during preparation |

## Live providers

All live calls used synthetic prompts in "QA Live Providers (synthetic)" on the
development database, except the trailer reply, which ran in "QA v1 Trailer —
The Lantern Atlas". Responses came from the real services; none were simulated.

- **OpenAI account, `gpt-6.1-sol`** (the default model for every check that is
  not a compatibility showcase):
  - Editor chat tool edit: `apply_manuscript_operations` followed by
    `read_manuscript`/`read_chapter` readback in about 18 s; the inserted
    sentence persisted in the chapter.
  - Thinking indicator shown while reasoning.
  - Follow-up quoted the inserted sentence exactly (about 8 s).
  - Cancellation during reasoning, and mid-stream after 390 characters: both
    ended `CANCELLED`, keeping partial text.
  - Transcript replay after reload, including tool chips.
  - Voice assistant (about 30 s): `list_characters`, `create_character`
    ("Pell Arden (QA 6.1)"), `read_voice_profile`, `save_voice_profile` with
    `expectedContent`, then readback confirmed.
  - Trailer writing clip: a one-sentence suggestion with no edits, in 7–8 s.
- **OpenAI account, `gpt-6-luna`** (earlier pass, the app's default): the same
  editor tool edit, reply, follow-up, mid-stream cancellation, replay and Voice
  assistant checks passed.
- **Compatibility showcase:**
  - OpenRouter `xiaomi/mimo-v2.6-flash`: tool edit with readback, reasoning
    shown, exact follow-up quote, cancellation during reasoning.
  - CommandCode `xiaomi/mimo-v2.6-flash`: tool edit with readback, reasoning,
    follow-up, cancellation after two items.
- **Model selection:** each chat's chosen model persisted across a host
  restart.
- **Local Ollama `qwen3`** (isolated database only, RTX 4070 with partial CPU
  offload): connection and replies worked. With a 34k-token chapter context,
  each step took about 3.5 minutes. The UI showed "Thinking..." with no
  streamed reasoning. The model ignored a "do not edit" instruction and
  inserted a paragraph after several failed tool calls. This is
  model-dependent behaviour, recorded as a finding, not as a defect.

## Regressions fixed during QA

Each fix is a separate reviewed commit:

- `f6ab262`: Authoring batch hashes now match the editor's string escaping.
  Edits containing punctuation or Unicode had been rejected.
- `46e6253`: Pending source extractions now publish their content.
- `973cfbb`: Chapter word and token counts now refresh after each save.
- `4c144a7`: The system theme now survives in-app navigation.
- `70e278c`: Paragraph footnotes and endnotes are now kept when block IDs are
  assigned.
- `a415f5a`: The server now hashes client batches exactly as sent. Note
  batches had been rejected because of a default `decorative:false` field.
- `e9ff227`: Editor tables are usable: the caret enters new tables, Tab and
  Shift+Tab move between cells, and column widths and styles apply.
- `02bb97f`: Cite with a note marker selected now inserts the citation after
  the marker. It had replaced the marker, deleting the endnote and its text.
- `1adde9e`: Import DOCX and List… are back on the toolbar, and Import DOCX
  no longer receives the click event as its view.
- `a37e568`: Word's separator space after imported note marks is dropped.
- `a40889a`: Cover text frames whose optional binding is empty are now
  hidden. PDF output had failed with `PRESS_COMPOSITION_TEXT_UNBOUND`.
- `10415de`: Footnotes are now drawn in PDF output, and a conformance
  assertion covers this.
- `9cd9693`: Press now measures and positions table cells by column. Tables
  had been ragged, padded text.
- `1fa6ac3`: DOCX table grid columns are scaled to the text width. Weights had
  been written as twips.
- `4aa2e68`: Release creation on SQLite now works for projects with Designed
  Pages.
- `e9e9386`: Complete archives now stream to HTTP downloads. They had been
  truncated after the first entry.
- `cd73cb8`: Profile indexing is deferred inside import transactions. Import
  took 2 min 11 s and left the profile unindexed; it now takes 1.6 s.
- `99ec3f6`: Review Edits reject, restore and inline edits are now fenced.
  This fixes a save deadlock, editor initialisation during background fences,
  and a phantom pending chapter after a full reject.
- `04b6f3c`: Designed Pages fixes:
  - A circuit crash opening Pages after reload.
  - A frame stuck following the mouse after a double-click.
  - Frame text lost when switching tabs mid-edit.
  - A literal `insertion.Label` in the Insert Page footer.
- `93d85eb`: Closing the cover editor after an edit no longer leaves an
  unreachable writer that blocks every project fence until restart.
- `5ae0a67`: Chat reasoning is readable in the dark theme (reported by the
  owner during live QA).
- `2068577`: EPUB and PDF ebook releases inheriting the Core cover now prepare
  with a trim-only digital template, covered by a regression test.
- `c9b1e44`: Designed Page cards in the editor keep their previews after load
  and undo.
- `a616966`: Display-only fields no longer leak into the editor's saved
  baseline. Saves had been left showing "saving".
- `d092bd6`: Writer resume and flush callbacks are now bounded, so a window
  closed mid-fence can no longer wedge the project until restart. Two
  regression tests were verified failing first.

## Minor findings (not fixed)

- **Lost window with unsent edits:** the chapter stays blocked by design
  ("An authoring client with unsaved state is unreachable") until the same
  session reattaches or the host restarts. Meanwhile the header shows Review
  Edits as off although it is on. Clean writers self-heal after circuit
  eviction.
- `DesignedPageWorkspace.DisposeAsync` logs an unhandled
  `JSDisconnectedException` when its circuit is disposed.
- **Chat formatting:** replies are plain text, so models' Markdown appears
  literally.
- **Adjacent markers:** note and citation markers run together ("¹¹"). The
  editor shows a citation as `[cite]`, while Read mode renders a superscript
  note.
- **DOCX import:**
  - The header row is not marked as a table header.
  - Heading bold arrives as a direct strong mark plus a character style.
- **Undo:**
  - A table-cell delete and paragraph typing coalesced into one step.
  - A rapid burst of seven Ctrl+Z presses once lost focus.
- The Sources header shows kind and type with no separator.
- **Image alt text:**
  - Images placed into Designed Page frames or added as cover artwork do not
    prefill alternative text. EPUB preflight then correctly blocks Prepare
    files until alt text is set.
  - The cover editor's alt text field is inside the collapsed "Cover details"
    panel.
- The chapter word count was not refreshed after undoing removal of a
  Designed Page placement.
- **Claude desktop browser pane only:** the sandboxed EPUB preview frame could
  not load styles, images or fonts. The same preview loads fully in Chromium.

## Not performed during preparation

- Partner Center identities, Store certification and production MSIX signing.
- Mac build and package acceptance (no local Mac host).
- Installed-platform acceptance (Windows installer/portable/MSIX install,
  Linux desktop launch, AppArmor) and package rebuilds from the tested commit.
- The launch-time update handoff and every publication, visibility change,
  Store submission or upload.

## Packaging and preparation fixes

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
  The affected World/Voice and Designed Page flows were retested on the tested
  source commit (see the ledger above).
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
- Passive inspection of the existing `9eec7b8` Linux unpacked/AppImage/DEB
  manifests found identical ordinary packed JSON with no `desktopName`. Both
  desktop files were named `com.lorekeeper.app.desktop` but declared
  `StartupWMClass=Lorekeeper`; their icon and executable commands matched the
  intended package paths. The focused metadata correction supplies the matching
  desktop name/class and adds passive ASAR/desktop hash checks to packaging.
  Exact-source Windows/Linux rebuilds on `182cf1e` passed; all three Linux ASAR
  copies and both desktop entries agree on the intended identities. Native
  install, window/icon, and AppArmor acceptance remain unperformed, and no
  accepted-build claim follows from these package checks.
- Ubuntu DEB metadata now includes the official .NET runtime prerequisites and
  uses the Ubuntu 24.04 GTK/AT-SPI package names. Build prerequisites and final
  DEB metadata are checked against the same declared inventory. The optional
  LTTng exception requires the exact reviewed provider bytes and runtime;
  unavailable tracing is recorded explicitly without an ABI alias.
- Failed publication retains all drafts, releases and tags for inspection. The
  former draft-check/delete sequence could delete a release finalized by another
  actor between those calls; the publisher no longer performs release deletion.
- Chaining Store packaging, local MSIX validation and a Free rebuild exposed a
  packaging-script regression: loading the application assembly kept its DLL
  mapped in the PowerShell process, so subsequent cleanup failed. A single
  bounded passive PE metadata reader replaces both assembly loads and disposes
  its reader and stream on success and failure. The immutable Store channel and
  exactly-one/current informational-version checks remain fail closed.
- Public AppImage publication was approved in `608a0e8`: every AppImage ships
  beside `Lorekeeper-AppImage-Runtime-Sources-20251108-x86_64.tar.gz` with the
  modified libfuse and type2-runtime sources, build scripts, relink
  instructions and the recorded modified-libfuse relink and repack.
  `eng/appimage-publication.json` records the approved hashes.

## Verification and artifact identity

Preparation checks on the current v1 worktree:

- Final source build after the passive MSIX metadata correction passed with
  zero warnings/errors; the existing data-safety suite passed **167 tests**,
  none failed or skipped. Exact notice freshness verified **71 identities**.
  The private log is `.artifacts/v1-final-source-build-tests.log`, SHA-256
  `4568cda50207327ed9bb106cf6a16f785537bae6d18883095427c4b51a896db7`.
  Press sources were unchanged after the successful native package checks
  recorded below. No UI/provider acceptance or new suite follows from this.
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
- Functional QA after the fixes above: `dotnet build` passed with zero
  warnings/errors and `dotnet test Lorekeeper.Tests/Lorekeeper.Tests.csproj`
  passed **176 tests** on the tested source commit.

### Trailer

The 60-second trailer was recorded from real UI footage of "QA v1 Trailer —
The Lantern Atlas", a synthetic project built in the isolated database. Its
full archive was imported into the development database so the writing clip
could use a live `gpt-6.1-sol` reply. Headless Chromium screencasts (1280x720
at 1.5 device scale, dark theme) were upscaled to 1920x1080 at 30 fps. Frames
show only the app's own pages for that project. No settings, connections,
credentials, desktop chrome, notifications or file paths appear. The audio is
the export recipe's original sine score. Clip hashes and the export provenance
are kept in ignored `.artifacts/trailer/v1/`. The committed deliverables are
in `media/trailer/`.

### Candidate package checks

Current package preparation source:
`182cf1e7f77ee747733f400a604d8206ebd62923`, tree
`6af9e15a34a38d10108061f7ffaeeb83a6d21dc5`.
Both actual Windows app DLLs report `1.0.0+182cf1e7f77ee747733f400a604d8206ebd62923`.
These local artifacts include the World/Voice corrections; their functional
retesting remains pending. Later MSIX tooling and evidence edits do not change
that packaged runtime source identity.

| Local artifact | Bytes | SHA-256 |
|---|---:|---|
| Free Windows installer | 193,666,820 | `6714980ecc1b231587ac598755d5865aa3398947d8540ea5271a23f7df87f567` |
| Free Windows portable | 193,449,209 | `8fc5b8a96cfcdfd4766443612b484c67deed8896787467fd2773fd0c3a5e3036` |
| Local-validation MSIX | 280,060,533 | `66174b9855ab9b7d17bd2efefb82777f7b4ef8bb3538bd8ccc9c4bc9aae2baaa` |
| Ubuntu x64 AppImage | 211,240,503 | `9706700775ae7b34253a35ffe16b16c35090ab298b2aec7ebb25c8cc27438c6f` |
| Ubuntu amd64 DEB | 176,968,928 | `2396895ca59ceaa77b763f9ac52825db22e85555b617227e26b69c212d3ff05e` |

- Windows Store and Free builders passed zero-warning/error compilation,
  exact channel/runtime/editor/dependency/Press/notices and checksum checks.
  Actual Authenticode inspection found the Free installer, portable, app,
  Electron, elevation helper and Press executables unsigned; Microsoft's
  `createdump.exe` retains its valid signature. Store-channel input survives
  in the local MSIX closure, while the regular Windows output contains Free.
  The first chained Free attempt stopped at the DLL lock described above.
  The standalone retry completed; the surrounding command incorrectly treated
  the accepted npm audit's stale native exit code as a builder failure.
  Its complete log and verified artifacts establish the successful retry.
- Corrected MSIX tooling passed `-CheckOnly` and full semantic pack/unpack and
  CMS/exact-signer validation in one PowerShell session. Exclusive DLL opens
  succeeded after both modes and after rejection of the actual Free closure.
  Fresh current-user certificate and CNG private-key-file inventories were
  unchanged afterward. No trust or install operation occurred. The private
  receipt is `.artifacts/v1-sharing/windows-final-182cf1e/msix-validation.json`,
  SHA-256 `abc44b4e23069545a5bb341baab44c7cb46caea9a98d091266be2c52a87043ea`.
  The generated input copy under `publish/msix-store-validation-182cf1e/`
  remains ignored because automatic approval review rejected its deletion with
  `blocked by policy`; it is not a running host or a release asset.
- Ubuntu 24.04 x64 passed zero-warning/error Release compilation, **167
  data-safety**, **47 Press unit** and **104 conformance** checks. All three
  actual ASAR manifests and both desktop entries passed matching application,
  version, desktop name/class, icon, executable and hash checks. Native closure,
  ELF/dependency, notices, Press, exact launcher/runtime, sandbox and DEB
  metadata checks passed. The wrapper verified transferred hashes, stopped its
  owned processes and removed its ext4 source/staging. Kept unpacked outputs
  are intentional. Optional LTTng tracing remains unavailable; native graphical
  association validation remains false.
- Linux provenance SHA-256:
  `f70fb3afa7da5922c768a2ae808a722dd580782331840bab3f2fe715bd38d770`.
  Exact LF source archive SHA-256:
  `09de03e317795f4435563204d065a4ba8d03d2e43f50cd4727eb7560cb55aab5`.
  Native run log SHA-256:
  `eed4d3875c8de28bc8a40d7e078fdd13f9119c6dfc4cacac67798c253bd9186c`.
- A separate ignored pinned replacement-runtime experiment successfully built
  a baseline and genuinely modified libfuse, relinked and repacked the retained
  prior application payload, preserving all 1,133 file/mode/link entries.
  This is a different native runtime identity from the selected vendor binary.
  Its source-only review candidate is 280,350,720 bytes with 366 regular members,
  SHA-256 `dbc71e8b913d3dc35ac5978e303731fba457b847446d9c0c7e89569e7677118d`.
  Outer member inspection excludes toolchain, APK/rootfs, app and native-object
  binaries. Portable recipes, compatible recipient terms, dated preferred-source
  notice, a fresh adopted baseline, native source review and production identity
  integration remain open. The separate private toolchain aggregate is not a
  redistributable source asset. Neither experiment nor candidate clears the
  selected runtime or changes the blocked public AppImage gate.

### Retained earlier package baseline

Package validation source: `9eec7b825c0052299beccb844c544aea875490c3`.
Source tree: `33bb4bf61a797b490408f9b47cbdee939fd3f67b`.
These earlier artifacts are retained privately; they precede the World/Voice and
desktop metadata corrections and do not establish current functional acceptance.

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

Functional QA found no remaining data-loss, credential-exposure or broken
core-workflow blocker on the tested source commit. Sign-off of installers and
the update handoff waits on the items under "Not performed during preparation".
