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
- Five persistent assistant surfaces for outline collaboration, chapter editing,
  writing coaching, research, and project images, including streaming tools,
  reviewable changes, visual context, and background revision agents.
- Versioned structured chapter manuscripts with stable block anchors,
  revision-aware manual and assistant operations, plain-text reading projections,
  and semantic Markdown/EPUB publication projections.
- Schema-driven semantic chapter editing with persistent heading levels 1-6,
  intentional line breaks, scene
  breaks, quotations, list items, project-image figures with alt text and
  captions, named paragraph/character styles, rich inline marks, undo/redo,
  normalized paste, find/replace, and outline navigation. Manual edits and AI
  assistants share one revision-checked manuscript boundary; HTML is not
  authoritative.
- Text, EPUB, PDF, image, and webpage ingest with structured source provenance,
  canon extraction, graph synchronization, and combined lexical/semantic
  retrieval.
- Project image generation and editing, canonical entity visual references,
  illustrated prose, Picture Page composition, font management, and layout
  diagnostics.
- Independent paperback/EPUB publication editions with product/vendor settings,
  identifiers, included content, semantic matter, named-style mappings, image
  placements, Picture Page cover sources, clone/archive/compare/audit workflows,
  deterministic staleness fingerprints, and matching Publish assistant tools.
- Preview paperback press jobs with cancellation/restart recovery, immutable
  SHA-256-verified interior and cover PDFs, actual in-app PDF viewing, semantic
  block-to-page maps, render comparisons, and matching assistant controls.
- Edition-aware full-wrap cover design with page-count/paper-caliper geometry,
  copy and background controls, template acknowledgement, ISBN-13/EAN-13
  barcode or KDP overlay-reserve behavior, cover PDF output, and assistant parity.
- Versioned Preview publication preflight with renderer-evidence checks,
  deterministic EPUB 3/package assembly, downloadable manifests and reports,
  exact-package digital/physical proof records, and matching assistant
  preflight/package controls. Independent EPUBCheck, Acrobat, vendor-upload, and
  physical-production validation remain release gates.
- Versioned project import/export (current v11 manuscripts/styles/editions/covers,
  isolated v8-v10 adapters, and v1-v7 text adapters) plus TXT,
  Markdown, mixed-layout EPUB, and a
  browser/operating-system print-preview workflow. Generated press PDFs are
  explicitly Preview artifacts, not PDF/X conformance or vendor-preflight claims.
- Configurable Codex/OpenAI-compatible chat and embedding providers, configurable
  web search, and local SQLite persistence.

## Requirements

- .NET 10 SDK
- Node.js 22.12 or later for Electron.NET desktop builds

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

Picture Page and Illustrated Prose layouts also carry revisions. If another
editor, manuscript save, or image deletion changes the active layout, a stale
visual save is rejected and the viewer reloads the current layout with a visible
notice instead of reintroducing removed images or overwriting newer anchors.
