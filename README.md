# Lorekeeper

Lorekeeper is a local Blazor Server proof-of-concept for AI-assisted long-form
story planning, drafting, research, ingest, and publishing.

See [VISION.md](VISION.md) for the product direction and
[docs/architecture.md](docs/architecture.md) for the compact technical map and
task-routed architecture chapters covering current boundaries, ownership, and
validation guidance. The researched,
status-labeled path from the current workbench to end-to-end book production is
in [docs/publishing-roadmap.md](docs/publishing-roadmap.md).

## Current Capabilities

- Project-scoped outline, Book Brief, story-graph, project-fact, writing-sample,
  and chapter workspaces.
- Projects can directly reference other projects for read-only continuity evidence,
  such as a sequel reading its predecessor. References are one-hop and live: the
  active project's canon and user direction win conflicts, while referenced projects
  remain intact and independently editable.
- Six persistent assistant surfaces for outline collaboration, chapter editing,
  writing coaching, research, project images, and publishing, including streaming tools,
  reviewable changes, visual context, background revision agents, and project/surface-
  scoped composer drafts that survive navigation and reloads until sent.
- Each of the six assistant chats has a compact per-conversation model picker.
  It groups working models under their saved connection and shows model-only
  option labels; an
  unselected conversation follows the current working global default, while an
  explicit choice remains sticky across navigation and restarts. Deleted or
  otherwise unavailable explicit choices fail closed until the author chooses a
  working model. Reset clears the transcript and its image attachments, restores
  the surface's initial greeting, and retains the exact conversation choice,
  including an unavailable provider ID. These chat transcripts and model choices
  remain local rather than entering project import/export.
- Editor chapter selection loads the next manuscript in place, updates the address
  bar without remounting or flashing the project-level Editor Chat, and refreshes
  the chat token estimate for the newly assembled chapter context.
- Each chapter's Assistant Memory can be reset from its panel header, clearing
  manual additions and exclusions so the current default context is rebuilt,
  including default-on Writing Samples.
- Long-running interactive chat turns automatically compact completed tool context at
  90% of the active model input limit, preserve an audit chip in the transcript, keep
  the live token counter aligned with the reduced in-progress context, and tell the
  assistant to look up IDs and details again before relying on them.
- Versioned structured chapter manuscripts with stable block anchors,
  revision-aware manual and assistant operations, plain-text reading projections,
  and semantic Markdown/EPUB publication projections. Editor Chat validates and
  applies each operation payload once through the revision-checked manuscript
  boundary without retransmitting or echoing the manuscript.
- Schema-driven semantic chapter editing with persistent heading levels 1-6,
  intentional line breaks, scene
  breaks, quotations, list items, project-image figures with alt text and
  captions, direct font/size/line-spacing and paragraph controls, Book Text Styles
  with reusable typography, alignment, indentation, spacing, and pagination,
  one-click paragraph/chapter application, capture-from-paragraph,
  compact assistant style tools, sparse per-paragraph overrides, rich inline marks,
  fast process-lifetime Undo/Redo retained across navigation and page reloads,
  normalized paste, find/replace, and
  outline navigation. Manual edits and AI
  assistants share one revision-checked manuscript boundary; HTML is not
  authoritative.
- In-process authoring history retains up to 100 manual actions independently for
  Core/release chapters, publication prose sections, Designed Pages, and Core/release
  covers. Typing is grouped naturally, canvas gestures remain single actions, and
  successful assistant changes clear the affected document's manual history instead
  of becoming Undo actions. Undo can restore a deleted Designed Page with its original semantic
  content, scene, variants, IDs, and asset references. History is independent of
  the working database, intentionally clears when Lorekeeper exits, and is excluded from
  project exports. The latest assistant comparison used by Review Edits remains
  durable and independent.
- Outline treats chapters as format-neutral containers and derives concise,
  non-prescriptive genre-format guidance from the Book Brief. Editor owns
  chapter Figure and Designed Page work; Publish owns publication sections and
  covers. Images is a concept-art workspace for style discovery, free-standing
  library generation, user-approved Visual Direction, and canonical character,
  location, prop, creature, and custom-entity references. All assistants retain
  compact grounded reads, while mutation tools stay within their owning surface.
- Outline automatically receives the complete current outline, chapter-level
  entity associations, category inventories, and a compact ingested-source
  inventory. The Book Brief lets the author select canonical sources; other
  sources remain searchable evidence. Chapter `RelevantTo` links are preferred
  over beat-only links because they feed Editor's automatic entity context.
  Automatic entity summaries omit graph relationships to conserve context;
  assistants retrieve complete paginated relationship data through direct
  entity/link reads when needed.
  Reworked outline fields are written as standalone current canon without
  language that compares them with an earlier draft.
- Text, EPUB, PDF, image, and webpage ingest with structured source provenance,
  canon extraction, graph synchronization, and combined lexical/semantic
  retrieval. File ingest accepts up to 50 files together, creates one durable
  job per file sequentially, and keeps per-file success or failure visible.
- Full project export format v24 preserves manuscript review annotations and only the Book Brief's selected
  canonical ingest sources (source text, chunks, pages, blocks, metadata, and
  selection mapping), remaps their provenance on import, and rebuilds retrieval
  indexes without rerunning extraction. Project-reference links are intentionally
  omitted from v24 exports; exports with outgoing links warn that imports never infer
  links. Non-structural exports omit source bodies, selections, and evidence and
  report that omission.
- Project-owned authoring page setup, Press-backed current-chapter Read preview,
  and contextual Edit/Read/Pages/Review modes. Read shows actual pagination,
  line breaks, images, captions, and Designed Pages with single/facing and zoom
  controls; it does not depend on a publication release. Designed Page text
  lines are fitted from the selected font's real metrics; the Pages editor
  measures its rendered text frames before showing an overflow warning, and
  genuine output overflow is clipped to its authored frame and reported
  instead of preventing the chapter from opening. Editor Chat can also
  request a Press-rendered page image by stable paragraph block or chapter-local
  typeset page to visually verify applied typography and typesetting.
- Switching between Edit, Read, and Review carries the current chapter position;
  Edit restores a semantic caret or node, while Read and Review restore the
  corresponding manuscript viewport without changing Review expansion state.
- Review remains populated after an assistant manuscript pass is applied: it
  compares the live Current chapter with the saved Before snapshot from before
  that pass, so later manual edits remain visible. Pending and Contest review
  actions retain priority, while the applied comparison is read-only and covers
  body, structure/formatting, Figures, and Designed Page references. Applied
  changes use the same collapsed line markers, inline diff cards, and dot rail as
  active Review, with separate text, formatting, and visual reveal controls.
- Exact-target review highlights and notes in Edit and Read, with a collapsible
  margin rail, deterministic outdated-anchor handling, assistant context and
  completion tools, and no effect on manuscript formatting or publication output.
- Project image generation and editing, canonical entity visual references,
  permissive source-image geometry, semantic flowing Figures, contextual
  Designed Page/spread composition, project font management, and format-aware
  dedicated cover composition through one canvas-first editor: construction and
  selection controls stay in a fixed bottom strip while accessibility and exact
  layout details open only when requested. Designed Page text frames are edited
  directly on the canvas; selected text uses the same semantic inline marks as
  ordinary manuscript paragraphs, without exposing content bindings or offsets.
  Exact target-aspect generation treats a proportional provider raster as
  compatible even when its pixel dimensions differ from the request.
  Target-bound generation, accessibility state, and layout diagnostics remain
  available. Image frames
  retain the raster's aspect ratio by default, can fill the largest proportional
  canvas area in one action, and permit deliberate stretching only when the user
  disables that constraint. Legacy cropped placement remains repositionable.
  Every assistant generation/edit waits for a terminal result and produces an
  unattached reusable project image; Figure, page, cover, and canonical-reference
  placement is a separate revision-safe step using that image ID. Generation
  and inspection reserve quiet space for actual copy regions while requiring
  the rest of the frame to contribute purposeful visual information or atmosphere.
  The Images library keeps every card at one bounded size, marks assets used by
  chapters, and disables deletion until every semantic Figure and chapter-owned
  Designed Page reference has been removed or replaced; the image service
  enforces the same rule against stale UI state.
- An always-present Core Book for shared title/author/language metadata, fixed
  outline order, presentation, publication sections, project typography, and
  reusable front-cover design. A publication section is either prose with
  optional Figures or a dedicated Designed Page canvas, and opens in its own
  full-height editor before, after, or around the Core outline.
  Section inclusion, order, and next/right/left starting page are explicit Core
  or release settings. A release can override section order without copying or
  claiming customization of inherited section content; optional KDP/common front-matter conventions produce
  guidance rather than silently rewriting the book. The Publish overview presents
  this as one collapsible Front/Main content/Back flow with direct Include/Omit
  chapter controls and correctly scoped prose or Designed Page creation.
  Optional paperback, EPUB ebook, and PDF ebook
  releases inherit Core values live and store only explicit field or collection
  overrides; ISBN, destination, package, and product settings remain
  release-specific. No release or ISBN is created automatically.
- Opt-in edition-specific manuscript editing in Editor. Untouched chapters
  inherit Core live; the first release edit freezes a chapter snapshot and its
  Designed Pages, while reset returns that chapter to Core. Saved Book Text
  Styles and project images remain reusable across Core and every release.
- An in-app preview and immutable download for the Core reading PDF used for
  private review and sharing, plus one-action preparation
  jobs that compile, render, validate, and store or package the selected Core or
  release target. Unresolved image accessibility choices remain visible warnings
  on the private Core copy, while publication releases require those choices to
  be resolved. Paperback and hardcover output use a checked-in, versioned
  physical-product registry with configurable Generic templates plus built-in
  Specific products for Amazon KDP and IngramSpark;
  EPUB and PDF ebook workspaces expose only relevant controls. The full-height
  conversational Publish assistant acts as a publication editor and production
  expert, with compact Core/release tools, persistent streaming history, image
  attachments, complete outline context, bounded project search,
  geometry-aware image creation, matter and metadata guidance, Stop/Reset, and
  reconnectable jobs. Cover design is embedded beside the assistant with a live
  scene canvas and one controls column instead of opening a modal.
  Prepared EPUB releases open in a full-height Lorekeeper reader that displays
  the immutable artifact's cover, reflowable chapters, Figures, and canvas-faithful
  fixed-layout Designed Pages without synthetic blank spine locations, with spine
  navigation and direct EPUB/front-cover downloads.
  The sandboxed reader is visual artifact inspection; Lorekeeper's preparation
  checks provide the app's declared structural-validation scope, while actual
  behavior across third-party readers remains an external compatibility check.
  Core PDF presentation can preserve a Designed Page as one wide or custom-sized
  PDF page; PDF ebook releases inherit that choice until explicitly customized.
- Lorekeeper-owned paperback, hardcover, and Digital PDF press jobs with cancellation/restart recovery,
  immutable SHA-256-verified interior and product-specific cover PDFs, in-app
  single/facing-page PDF viewing with an optional page seam and transparent
  alignment slots, semantic block-to-page
  maps, render comparisons, and matching
  assistant controls. The exact-pinned Rust renderer is built and packaged in
  both Debug and Release; the running app invokes only that integrity-checked
  native executable and never uses machine-installed PDF software.
  Black-and-white editions produce grayscale interior imagery while full-wrap
  cover color remains independently preserved. Digital PDF produces one tagged
  Book PDF with its front cover as page one, searchable/selectable text,
  bookmarks, internal links, semantic structure, and logical reading order.
  Renderer/profile upgrades make older owned PDFs stale until regenerated.
- Core/release-aware structured cover design with shared image/text/shape/layer/style
  tools: the Core front scene flows into digital releases and the front panel of
  print surfaces until explicitly customized. Print releases select exact
  paper stock/weight, process, construction, finish, and cover mode. The final
  interior page count resolves stock-specific spine geometry and the required
  outside, inside, case, jacket, or cloth setup surfaces, safe regions, and
  ISBN-13/EAN-13 barcode behavior. Ingram duplex paperback produces outside
  then inside cover pages with the required no-ink spine region.
- Versioned Lorekeeper validation with independent post-write inspection,
  deterministic EPUB 3/package assembly, downloadable manifests and reports,
  and matching assistant preflight/package controls. The owned KDP
  products emit PDF 1.7, and the owned Ingram products emit restricted PDF 1.3
  with PDF/X-1a:2001 identification, embedded CMYK output intent, CMYK/gray
  content, flattened raster alpha and non-overlapping scene opacity, no transparent PDF objects, embedded subset fonts, ToUnicode maps, and a
  240% total-ink ceiling. These checks establish only the exact named
  Lorekeeper profile; they do not claim that a vendor accepted an upload.
- Versioned project import/export (current v24 manuscript-v4/page-setup/
  composition model, Core Book, sparse release overlays, edition chapter
  snapshots/compositions, exact-target review annotations, selected canonical source bodies/evidence, covers, custom-font binaries,
  and isolated older structured/text adapters) plus TXT, Markdown,
  semantic mixed-layout EPUB, artifact-backed Generate/Regenerate, separate
  paperback interior/cover saves, and one Digital PDF Book save. EPUB export is
  restricted to EPUB editions.
- Configurable Codex/OpenAI-compatible chat and embedding providers — with provider presets, model discovery, combined chat+vision verification, per-model reasoning effort, output-token budget, and endpoint-aware wire compatibility — configurable web search, and local SQLite persistence.

## Requirements

- .NET 10 SDK
- Node.js 22.12 or later for Electron.NET desktop builds
- Rust 1.97.1 for source builds; the packaged app has no Rust or Cargo runtime requirement

Every Debug and Release build produces the app-owned `press-runtime` bundle
automatically from `Cargo.lock`. It contains only the native executable, approved
fonts, the registered CMYK profile, notices, an SBOM, and a complete hash
manifest. At runtime, a missing, modified, linked, or unexpected file disables
PDF generation before a job can be queued. Browser and Electron hosts never
invoke Cargo, Python, uv, Typst, WeasyPrint, Chromium, or machine PDF tools.

## Web Development

Run the normal browser-hosted app:

```bash
dotnet run --project Lorekeeper --launch-profile http
```

The HTTP launch profile is pinned to `http://localhost:1455` for the Codex OAuth
callback. Use this explicit profile for browser-driven UI validation; the Electron
profile intentionally remains the default development target.

### Secured browser hosting

Browser hosting can require a single-user sign-in with a username, password, and
authenticator code. Generate the credentials once:

```bash
dotnet run --project Lorekeeper -- auth-setup --username <name>
```

Set the printed `Auth__SingleUser__*` environment variables before starting the
host and add the printed `otpauth://` URI to an authenticator app. Every page and
endpoint then requires the sign-in; without those variables the host remains a
local, unauthenticated development server. `Server__EnableHttpsRedirection=false`
permits deliberate plain-HTTP hosting on a trusted network or behind a reverse
proxy, and `Server__DataProtectionKeysDirectory` keeps sign-in cookies valid
across restarts.

### Container and Kubernetes hosting

The repository `Dockerfile` builds a self-hosted server image, including the
contained Press PDF runtime, that stores the database and session keys on a
`/data` volume and requires the single-user sign-in. The manifests and
step-by-step instructions in
[`deploy/kubernetes/`](deploy/kubernetes/README.md) deploy it as a
single-replica Kubernetes workload with persistent storage, secret-provided
credentials, and health probes. The database stays SQLite on the mounted
volume — exactly one replica, and the volume is the backup boundary.

## Desktop Development

Run the Electron.NET desktop shell:

```bash
dotnet run --project Lorekeeper --launch-profile electron
```

Desktop binding is configured in `Lorekeeper/appsettings.json` under
`Desktop:BindHost` and `Desktop:HttpPort`. Override the port in PowerShell with:

```powershell
$env:Desktop__HttpPort = '1456'
dotnet run --project Lorekeeper --launch-profile electron
```

Codex OAuth uses `Auth:Codex:RedirectUri`, which defaults to
`http://localhost:1455/auth/callback`. Changing the desktop port can break Codex
OAuth unless that redirect URI is also accepted by the OAuth provider.

## Contributing

All changes use non-`main` branches and reviewed pull requests. See
[`CONTRIBUTING.md`](CONTRIBUTING.md) for branch synchronization, the mandatory
repository gate, review requirements, and the maintainer release workflow.
Repository owners can apply and verify the matching GitHub controls with
[`docs/github-repository-settings.md`](docs/github-repository-settings.md).

## Desktop Packaging

Build the current Windows packages without publishing them:

```powershell
.\scripts\build-windows-release.ps1
```

The project version is currently `0.3.9`. Pass `-Version <version>` only when
validating a different future SemVer. This build-only script verifies the
solution, audits NuGet and the shipped npm/Electron runtime, probes the packaged
Press protocol/registry contract, and produces the installer and portable
executable under `publish/win-x64/`. Electron.NET currently ships
`image-size@1.2.1` solely for its optional splash-image path. Lorekeeper has no
splash image; the release audit accepts only the two exact known advisories when
that path remains unreachable and fails for any changed package, usage,
configuration, or advisory.

The underlying packaging command is:

```bash
dotnet publish Lorekeeper/Lorekeeper.csproj -c Release /p:PublishProfile=win-x64
```

The build produces a per-user NSIS installer and a portable executable in
`publish/win-x64/`. The installer is the recommended file to share. It installs
without administrator rights, and recipients do not need .NET or Node.js. Share
`publish/win-x64/Lorekeeper-Setup-<version>-x64.exe` with testers.

To publish only the Windows packages without consuming GitHub Actions minutes,
install and authenticate [GitHub CLI](https://cli.github.com/), then run:

```powershell
gh auth login
git fetch --prune origin
git switch -c release/publish-0.3.9 origin/main
.\scripts\publish-release.ps1 -Version 0.3.9 -MergedPullRequest 123 -WindowsOnly
```

The Windows-only path builds locally, does not dispatch the macOS workflow, and
publishes the installer, portable executable, updater metadata, blockmap, and a
checksum file. The release is still marked latest so installed Windows builds
and Windows portable builds can discover it. Manual-update packages containing
this change ignore a newer release that lacks an artifact for their platform and
architecture.

To build and publish Windows plus Apple Silicon macOS packages as one release,
run the same command without `-WindowsOnly`:

```powershell
gh auth login
git fetch --prune origin
git switch -c release/publish-0.3.9 origin/main
.\scripts\publish-release.ps1 -Version 0.3.9 -MergedPullRequest 123
```

The publisher requires a clean, named, non-`main` orchestration branch whose
`HEAD` exactly matches freshly fetched `origin/main`. `-MergedPullRequest` must
identify the merged release-preparation pull request that produced that exact
commit. If any other pull request targeting `main` remains open, the publisher
stops and lists it. A maintainer may rerun with `-ConfirmOpenPullRequests` only
after explicitly reviewing the open work and confirming publication should
continue. Unless `-WindowsOnly` is used, the publisher dispatches
`.github/workflows/build-macos-release.yml` for the Apple Silicon package,
builds Windows locally at the same time, waits for the correlated Actions run,
downloads the verified DMG, and publishes every artifact together only if all
builds succeeded. The workflow must already be committed and pushed to `main`;
it uses the source repository's read-only `GITHUB_TOKEN` and never publishes a
release itself. The orchestrator uploads the same verified artifact set and
release notes to draft releases in both the private `Dorely/Lorekeeper` source
repository and the public `Dorely/Lorekeeper-Releases` repository. It verifies
both asset sets before publishing either release and removes releases/tags it
created if dual publication fails. It warns if GitHub prevents compensating
cleanup. The public repository remains the updater and user-download authority.

Each completed release contains the Windows installer, portable executable,
updater metadata and blockmap, `Lorekeeper-<version>-arm64.dmg`, and one checksum
file covering every asset. Add
`-Notes "..."` or `-NotesFile .\release-notes.md` for custom notes. SemVer
prereleases such as `0.3.9-beta.1` are published as GitHub prereleases.
Published versions are immutable; fixes require a higher version.

Installed Windows builds use automatic updates and require the Setup executable,
its `.blockmap`, and `latest.yml` to remain together. macOS and Windows portable
builds instead query the public stable release API at startup and every 15
minutes. They show **Download Update** only when the latest stable release is
newer, and open that release in the operating system's default browser. Change
the interval with `Desktop:UpdateCheckIntervalMinutes`.

Release builds store the SQLite database in per-user application data, outside
the installed application, mounted DMG, and portable executable's extraction
directory. Desktop development continues to use the repository-local database.

Windows packages are unsigned, so Windows may show an unknown-publisher or
SmartScreen warning. macOS packages are ad-hoc signed but not Developer ID signed
or notarized. On Apple Silicon, download the `arm64` DMG, drag Lorekeeper into
Applications, then use **Open Anyway** in System Settings > Privacy & Security if
Gatekeeper blocks the first launch. Lorekeeper 0.3.6 is the final Intel macOS
package; its `x64` DMG remains available, but later releases do not provide Intel
artifacts. These packages are intended for trusted testers.

The `/publish/` directory is git-ignored. Release binaries are mirrored as
GitHub Release assets in the private source repository and the public
[`Dorely/Lorekeeper-Releases`](https://github.com/Dorely/Lorekeeper-Releases)
repository rather than committed to source control. The private source
repository also consumes GitHub Actions minutes for its hosted macOS job.

## Local Data

SQLite databases, API keys, OAuth tokens, temporary verification databases, and
publish output are local state and are ignored by git.

Every launch begins with Lorekeeper's application-owned startup screen. It
shows ordinary workspace initialization and names each database compatibility
stage when migrations are being checked or applied. Project, ingest, import,
image, embedding, and publication workers remain paused until database startup
finishes. A successful start opens the requested workspace; a protected
migration failure opens **Settings > Data Recovery**, while an unexpected
bootstrap failure remains on the startup screen with a safe close action so the
application can be restarted after the cause is addressed.
On a recovery start, Lorekeeper applies an explicitly scheduled restore first;
otherwise it honors the existing recovery marker before opening the projectless
database or running any normal migration service.

The Projects hub provides a References action for each project. It manages direct
read-only continuity links with in-surface validation and keeps current-project-only
behavior when no links exist. Deleting a project uses an application-owned
confirmation; if other projects depend on it, the confirmation lists them and makes
clear that confirmation detaches those links without deleting the dependent projects.

When an older database first adopts structured manuscripts, Lorekeeper creates a
WAL-consistent backup in `.migration-backups/manuscripts`, validates the
conversion, and records a migration journal. **Settings > Data Recovery** shows
the available backups and requires an explicit two-step confirmation before
scheduling a restore. The selected backup is applied during the next startup,
before normal app workers begin. Keep those backups with your other local-data
backups; they are not included in project exports.
Legacy illustrated-prose images retain the paragraph position the old runtime
actually displayed even when a later chapter edit left their advisory paragraph
hash stale. The migration records those stale hashes while continuing to reject
malformed anchors or ambiguous paragraph mappings.

If a semantic-editor save collides with a newer chapter revision, Lorekeeper
places the unsaved manuscript JSON in browser/Electron local storage under a
chapter-specific conflict key and locks that editor. This recovery copy is
unencrypted local manuscript content outside SQLite. It survives a page/circuit
reload, can be downloaded from the conflict banner, and is removed only when
the user explicitly loads the current saved version. Clearing browser/site data
removes it; it is not included in database backups or project exports.

Unsent text in each of the six assistant composers is also stored in
browser/Electron local storage, keyed by project and assistant surface. It is
unencrypted local text outside SQLite, survives navigation and page/circuit
reloads, and is removed when that message is sent. Clearing browser/site data
removes these drafts; they are not included in database backups or project
exports. Attached composer images remain project-library assets, but the
temporary attachment selection itself is not restored with the text draft.

The guarded manuscript/composition and authoring-page migrations create a
protected SQLite backup before transforming Figure presentation, page-layout
chapters, cover scenes, page setup, authoring variants, and pending Outline
changes. They verify semantic text and stable IDs, scene/image ownership and
geometry, staged Picture Page hashes, active authoring layouts, protected row
counts, foreign keys, artifacts, hashes, packages, and audits before
removing obsolete visual state. Every Picture Page retains its original 8.5 × 11
inch leaf geometry (including 17 × 11 facing spreads) until the authoring
migration materializes it as the active Designed Page layout, independently of
publication releases. A legacy text frame that contains several paragraphs
retains those same-role semantic blocks in its original frame; directly editing
that frame materializes its body text into one editable block. A failure
opens Lorekeeper's projectless recovery shell and leaves the original database
available under **Settings > Data Recovery**. Existing generated artifacts keep
their exact bytes and hashes but are labeled Legacy until regenerated through
the current renderer.
