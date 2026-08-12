# Lorekeeper

Lorekeeper is a local Blazor Server proof-of-concept for AI-assisted long-form
story planning, drafting, research, ingest, and publishing.

See [VISION.md](VISION.md) for the product direction and
[docs/architecture.md](docs/architecture.md) for the current technical
boundaries, ownership model, and validation guidance. The researched,
status-labeled path from the current workbench to end-to-end book production is
in [docs/publishing-roadmap.md](docs/publishing-roadmap.md).

## Current Capabilities

- Project-scoped outline, Book Brief, story-graph, project-fact, writing-sample,
  and chapter workspaces.
- Six persistent assistant surfaces for outline collaboration, chapter editing,
  writing coaching, research, project images, and publishing, including streaming tools,
  reviewable changes, visual context, background revision agents, and project/surface-
  scoped composer drafts that survive navigation and reloads until sent.
- Editor chapter selection loads the next manuscript in place, updates the address
  bar without remounting or flashing the project-level Editor Chat, and refreshes
  the chat token estimate for the newly assembled chapter context.
- Long-running interactive chat turns automatically compact completed tool context at
  90% of the active model input limit, preserve an audit chip in the transcript, keep
  the live token counter aligned with the reduced in-progress context, and tell the
  assistant to look up IDs and details again before relying on them.
- Versioned structured chapter manuscripts with stable block anchors,
  revision-aware manual and assistant operations, plain-text reading projections,
  and semantic Markdown/EPUB publication projections. Editor Chat validates and
  stages each operation payload once, then applies it by an opaque one-use preview
  ID instead of retransmitting or echoing the manuscript.
- Schema-driven semantic chapter editing with persistent heading levels 1-6,
  intentional line breaks, scene
  breaks, quotations, list items, project-image figures with alt text and
  captions, direct font/size/line-spacing and paragraph controls, Book Text Styles
  with reusable typography, alignment, indentation, spacing, and pagination,
  one-click paragraph/chapter application, capture-from-paragraph,
  compact assistant style tools, sparse per-paragraph overrides, rich inline marks, undo/redo,
  normalized paste, find/replace, and
  outline navigation. Manual edits and AI
  assistants share one revision-checked manuscript boundary; HTML is not
  authoritative.
- Outline treats chapters as format-neutral containers and derives concise,
  non-prescriptive genre-format guidance from the Book Brief. Outline, Editor,
  Images, and Publish share revision-safe Figure/composition/cover tools,
  server-owned image-generation geometry, compact paginated reads, and one-use
  persisted stages for large scenes so payloads are not repeated.
- Text, EPUB, PDF, image, and webpage ingest with structured source provenance,
  canon extraction, graph synchronization, and combined lexical/semantic
  retrieval.
- Project-owned authoring page setup, Press-backed current-chapter Read preview,
  and contextual Edit/Read/Pages/Review modes. Read shows actual pagination,
  line breaks, images, captions, and Designed Pages with single/facing and zoom
  controls; it does not depend on a publication release. Designed Page text
  lines are fitted from the selected font's real metrics; genuine overflow is
  clipped to its authored frame and reported as a preview warning
  instead of preventing the chapter from opening. Editor Chat can also
  request a Press-rendered page image by stable paragraph block or chapter-local
  typeset page to visually verify applied typography and typesetting.
- Project image generation and editing, canonical entity visual references,
  permissive source-image geometry, semantic flowing Figures, contextual
  Designed Page/spread composition, project font management, and format-aware
  dedicated cover composition through one canvas-first editor: construction and
  selection controls stay in a fixed bottom strip while accessibility and exact
  layout details open only when requested. Designed Page text frames are edited
  directly on the canvas; selected text uses the same semantic inline marks as
  ordinary manuscript paragraphs, without exposing content bindings or offsets.
  Exact target-bound generation,
  accessibility state, and layout diagnostics remain available. Image frames
  retain the raster's aspect ratio by default, can fill the largest proportional
  canvas area in one action, and permit deliberate stretching only when the user
  disables that constraint. Legacy cropped placement remains repositionable.
  Every assistant generation/edit waits for a terminal result and produces an
  unattached reusable project image; Figure, page, cover, and canonical-reference
  placement is a separate revision-safe step using that image ID.
- An always-present Core Book for shared title/author/language metadata, fixed
  outline order, presentation, publication sections, project typography, and
  reusable front-cover design. A publication section is either prose with
  optional Figures or a dedicated Designed Page canvas, and opens in its own
  full-height editor before, after, or around the Core outline.
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
  be resolved. Paperback output includes a configurable Generic profile plus
  built-in Specific profiles for Amazon KDP and IngramSpark;
  EPUB and PDF ebook workspaces expose only relevant controls. The full-height
  conversational Publish assistant has compact Core/release tools, persistent
  streaming history, image attachments, complete outline context, bounded
  project search, geometry-aware image creation, Stop/Reset, and reconnectable
  jobs. Cover design is embedded beside the assistant with a live scene canvas
  and one controls column instead of opening a modal.
  Core PDF presentation can preserve a Designed Page as one wide or custom-sized
  PDF page; PDF ebook releases inherit that choice until explicitly customized.
- Lorekeeper-owned paperback and Digital PDF press jobs with cancellation/restart recovery,
  immutable SHA-256-verified interior and full-wrap cover PDFs, actual in-app
  PDF viewing, semantic block-to-page maps, render comparisons, and matching
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
  paperback wraps until explicitly customized. Print releases add
  page-count/paper-caliper back/spine/front geometry, safe zones, and
  ISBN-13/EAN-13 barcode or KDP reserve behavior.
- Versioned Lorekeeper validation with independent post-write inspection,
  deterministic EPUB 3/package assembly, downloadable manifests and reports,
  and matching assistant preflight/package controls. The owned KDP
  profile emits PDF 1.7, and the owned Ingram profile emits restricted PDF 1.3
  with PDF/X-1a:2001 identification, embedded CMYK output intent, CMYK/gray
  content, flattened raster alpha and non-overlapping scene opacity, no transparent PDF objects, embedded subset fonts, ToUnicode maps, and a
  240% total-ink ceiling. These checks establish only the exact named
  Lorekeeper profile; they do not claim that a vendor accepted an upload.
- Versioned project import/export (current v19 manuscript-v4/page-setup/
  composition model, Core Book, sparse release overlays, edition chapter
  snapshots/compositions, covers, custom-font binaries,
  and isolated older structured/text adapters) plus TXT, Markdown,
  semantic mixed-layout EPUB, artifact-backed Generate/Regenerate, separate
  paperback interior/cover saves, and one Digital PDF Book save. EPUB export is
  restricted to EPUB editions.
- Configurable Codex/OpenAI-compatible chat and embedding providers, configurable
  web search, and local SQLite persistence.

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

## Desktop Packaging

Build Windows packages without publishing them:

```powershell
.\scripts\build-windows-release.ps1 -Version 0.2.0
```

Omit `-Version` to use the version in `Lorekeeper.csproj`. This build-only
script verifies the solution, audits NuGet and the shipped npm/Electron runtime,
and produces the installer and portable executable under `publish/win-x64/`.

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
.\scripts\publish-release.ps1 -Version 0.2.1 -WindowsOnly
```

The Windows-only path builds locally, does not dispatch the macOS workflow, and
publishes the installer, portable executable, updater metadata, blockmap, and a
checksum file. The release is still marked latest so installed Windows builds
and Windows portable builds can discover it. Manual-update packages containing
this change ignore a newer release that lacks an artifact for their platform and
architecture.

To build and publish Windows plus Apple Silicon and Intel macOS packages as one
release, run the same command without `-WindowsOnly`:

```powershell
gh auth login
.\scripts\publish-release.ps1 -Version 0.2.0
```

The publisher requires a clean local `main` that exactly matches `origin/main`.
Unless `-WindowsOnly` is used, it dispatches
`.github/workflows/build-macos-release.yml` for both Mac architectures, builds
Windows locally at the same time, waits for the correlated Actions run,
downloads the verified DMGs, and publishes every artifact together only if all
builds succeeded. The workflow must already be committed and pushed to `main`;
it uses the source repository's read-only `GITHUB_TOKEN` and never publishes a
release itself.

The completed release contains the Windows installer, portable executable,
updater metadata and blockmap, `Lorekeeper-<version>-arm64.dmg`,
`Lorekeeper-<version>-x64.dmg`, and one checksum file covering every asset. Add
`-Notes "..."` or `-NotesFile .\release-notes.md` for custom notes. SemVer
prereleases such as `0.2.0-beta.1` are published as GitHub prereleases.
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
or notarized. Download the DMG matching the Mac (`arm64` for Apple Silicon, `x64`
for Intel), drag Lorekeeper into Applications, then use **Open Anyway** in
System Settings > Privacy & Security if Gatekeeper blocks the first launch.
These packages are intended for trusted testers.

The `/publish/` directory is git-ignored. Release binaries live on the public
[`Dorely/Lorekeeper-Releases`](https://github.com/Dorely/Lorekeeper-Releases)
repository rather than in source control. The private source repository consumes
GitHub Actions minutes for its two hosted macOS jobs.

## Local Data

SQLite databases, API keys, OAuth tokens, temporary verification databases, and
publish output are local state and are ignored by git.

When an older database first adopts structured manuscripts, Lorekeeper creates a
WAL-consistent backup in `.migration-backups/manuscripts`, validates the
conversion, and records a migration journal. **Settings > Data Recovery** shows
the available backups and requires an explicit two-step confirmation before
scheduling a restore. The selected backup is applied during the next startup,
before normal app workers begin. Keep those backups with your other local-data
backups; they are not included in project exports.

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
publication releases. A failure
opens Lorekeeper's projectless recovery shell and leaves the original database
available under **Settings > Data Recovery**. Existing generated artifacts keep
their exact bytes and hashes but are labeled Legacy until regenerated through
the current renderer.
