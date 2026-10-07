# Microsoft Store submission materials

These materials prepare a free Windows desktop MSIX listing. They are reviewable
copy and a submission checklist, not a published listing or certification result.
Do not upload packages, reserve an identity, accept account terms, or submit for
certification until the owner authorizes those external steps.

## Listing copy

**Product name:** Use the name reserved by the owner in Partner Center. Use
Lorekeeper if it is available; copy that exact value to `displayName` in the
identity file.

**Short description:** A local-first studio for planning, writing, and producing books.

**Description:**

Bring your book's outline, manuscript, research, artwork, and publication design
together in one desktop workspace. Lorekeeper helps authors organize characters,
places, events, and project facts, write and revise chapters, manage sources, and
prepare book releases.

Write manually or connect your own AI provider for assistance with outlining,
editing, research, images, and publishing. Review changes while keeping your
manuscript and project history under your control.

Design reusable pages and covers, manage book styles and figures, and produce
EPUB, PDF ebook, and print-ready paperback and hardcover files from the same
project, with built-in profiles for Amazon KDP, IngramSpark, Barnes & Noble
Press, and Lulu, or your own printer's measurements. Keep local checkpoints, export a
portable project archive, and optionally connect your own GitHub repository for
version-history synchronization.

Lorekeeper is free to install. AI services are optional and may require your own
account, credentials, local model server, or paid provider usage. No AI credits
are included. Microsoft Store manages updates for this Windows package.

The application is source available under the license choices documented in the
repository. Your manuscripts, books, and other creative work retain their own
rights; the application license does not prevent you from selling your books.

**Feature bullets:**

- Plan outlines, chapters, canon, and project facts together.
- Write and revise a structured manuscript with styles, figures, notes, and citations.
- Retain research sources and manage their references.
- Review assistant changes and preserve local project checkpoints.
- Design pages and covers and prepare EPUB, PDF, and print-ready paperback and hardcover files.
- Carry projects through portable archives and optional GitHub history sync.

**Category:** Productivity. **Price:** Free. **Language:** English (United States).
**Device family:** Windows desktop. **Architecture:** x64.
The current manifest requires Windows 10 build 19041 or later; do not claim a
particular Windows version, memory limit, or performance result has been tested
until the installed package has been exercised there.

**Support URL:** `https://github.com/Dorely/Lorekeeper/issues`, after the repository
is public. **Privacy URL:**
`https://github.com/Dorely/Lorekeeper/blob/main/PRIVACY.md`, after confirming it is
readable without authentication. The owner must choose supported
markets and complete the IARC rating questionnaire from the actual application.

## Capability explanations for certification

**runFullTrust:** Lorekeeper is an Electron desktop application backed by a bundled
ASP.NET Core process. It launches its bundled Lorekeeper Press executable for PDF
production, persists user projects in SQLite and local Git history, and supports
user-directed file import/export and printing. These operations run as the
current user; the application does not require administrator privileges. The
manifest declares the packaged desktop model, not AppContainer isolation.

**unvirtualizedResources:** Authored project data and history in
`%LocalAppData%\Lorekeeper` must survive application uninstall and reinstall.
Windows 11 uses a scoped filesystem virtualization exclusion for that directory.
Windows 10 uses the filesystem-virtualization-disabled fallback because scoped
exclusions are unavailable there. Registry virtualization remains enabled.
Installed-package validation must confirm these preservation boundaries before
submission; the manifest alone is not that evidence.

## Review and upload procedure

1. Complete verified Partner Center enrollment and reserve the product name.
   Copy the exact identity name, publisher subject, publisher display name, and
   reserved display name into a private/local copy of
   `store-identity.example.json`. Do not invent or reuse the local validation
   identity.
2. Build and inspect the Store channel using the commands in the
   [repository README](../../README.md).
   Production output is unsigned. Microsoft signs accepted MSIX packages;
   EXE/MSI Store submissions would still require publisher signing.
3. Validate installation, launch, loopback binding, child processes, native Press,
   project/history paths, import/export, printing cancellation, update behavior,
   and update/uninstall/reinstall preservation in an authorized disposable Windows
   profile. Run the Windows App Certification Kit. Remove the test package and
   any deliberately trusted test certificate afterward.
4. Prepare at least four desktop screenshots from an isolated demonstration
   project with no personal material or credentials. PNGs must be at least
   1366x768 and no larger than 50 MB. These screenshots are separate from trailer
   images and must show actual supported behavior.
5. Review the trailer, its captions, source permissions, and the listing copy
   against the final build. Confirm the support/privacy URLs work publicly.
6. With the owner's authorization, upload the production MSIX, images, trailer,
   privacy link, free pricing, age ratings, and capability explanations to the
   submission draft. Give reviewers an exact local/manual walkthrough and explain
   that AI requires an optional user-owned provider. Never include a personal API
   key or OAuth token in the package or certification notes.
7. Let the owner review the complete draft before submitting it for certification.
   Add the real Microsoft Store badge/link to the repository only when the product
   ID and published listing exist. Future updates retain the same Store identity
   and permanent `(source major + 1).minor.patch.0` package-version mapping.

## Trailer deliverables

- Store MP4: 1920x1080, 30 fps, H.264/AVC1 High progressive, 4:2:0, CABAC,
  two B frames, closed GOP of 15 frames, 50 Mbps; AAC-LC stereo, 48 kHz,
  384 kbps. Keep the MP4 fast-start and omit edit lists. The prepared v2 master
  (`.artifacts/trailer/v2/export/lorekeeper-v2-store.mp4`) runs 90 seconds, longer
  than Microsoft's recommended 60 seconds but well under the 2 GB file cap.
- Matching 1920x1080 PNG thumbnail (`media/trailer/lorekeeper-v2-poster.png`)
  and a title no longer than 255 characters (`media/trailer/lorekeeper-v2.title.txt`).
- English WebVTT captions below 50 MB (`media/trailer/lorekeeper-v2.en.vtt`);
  optional MP3 audio description below 500 MB.
- A separate 1920x1080 hero PNG, without text or app UI, for Store presentation.
  None is prepared yet.
- A compact H.264 README export of at most 9,500,000 bytes, below GitHub's free-plan
  10 MB attachment cap (`media/trailer/lorekeeper-v2-github.mp4`); use GitHub's
  native video attachment mechanism after upload is authorized.

Do not put embedded age-rating graphics in the Store trailer. If a separate
public trailer requires rating information under the assigned authority's rules,
use the appropriate export for that destination. Keep large video masters and
recordings out of Git history and retain only source instructions, captions,
thumbnails, and rights evidence there.

## Official references

- [Store onboarding and signing](https://learn.microsoft.com/en-us/windows/apps/publish/get-started)
- [Product identity details](https://learn.microsoft.com/en-us/windows/apps/publish/view-app-identity-details)
- [MSIX package requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements)
- [Flexible virtualization](https://learn.microsoft.com/en-us/windows/msix/desktop/flexible-virtualization)
- [Capability declarations](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations)
- [Listing image and trailer requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/screenshots-and-images)
- [Store policies](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies)
