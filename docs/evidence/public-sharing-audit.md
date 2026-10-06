# Public-sharing preparation audit

Date: 2026-10-05, updated 2026-10-06. Status: **Contribution rights and AppImage
source/relink clearance are resolved; exact final refs need a recheck before
visibility changes.**
No visibility, settings, refs, history, release publication, or Store submission
was changed by this audit. Detailed reports and downloaded material stay private
under ignored `.artifacts/sharing-audit/`. Do not upload that directory.

## Scope and evidence

- A disposable origin mirror contains every advertised remote head/tag and every
  available pull-request head/merge ref. All local refs were also fetched into a
  separate mirror namespace. The refreshed preparation snapshot includes
  candidate `182cf1e7f77ee747733f400a604d8206ebd62923`: 70 refs and 648
  unique reachable commits.
- Gitleaks **8.30.1** from its official release was checksum-verified. The Windows
  x64 archive SHA-256 is
  `d29144deff3a68aa93ced33dddf84b7fdc26070add4aa0f4513094c8332afc4e`.
  Scans used full redaction, all refs, full history including merge diffs, and
  bounded decoding/archive inspection. That refreshed all-ref scan traversed
  646 diff-bearing commits and 76,398,951 text bytes with zero findings
  after the 16 individually reviewed false-positive exclusions below.
- The private ref snapshot SHA-256 is
  `053eefe7bf4a087ce8652e37f2dbf7b4a45e9d50f8df8f2fb58ec85c37c1788e`.
  This binds that snapshot, not later commits. Recheck exact final refs before
  public visibility; subsequent commits are not yet included. An exact
  committed-source ZIP of that candidate, forced to LF with Git's
  `core.autocrlf=false` and `core.eol=lf`, contains 1,519 regular files and
  **59,248,150 uncompressed file bytes**. The ZIP itself is 15,405,222 bytes;
  its SHA-256 is
  `e18aea0a55d085738277d55f65393fd4d3d70435c9530e45b05bae6870acb459`.
  Gitleaks inspected **44,973,099 text bytes**, a different metric. The earlier
  report's approximately 45.47 MB described inspected text, not total source
  file bytes. Like-for-like LF archives of `6ba993a` and `441c96e` contain
  58,885,004 and 59,209,172 uncompressed bytes respectively.
  The candidate's eight raw generic-key matches were reviewed as seven public
  print-profile identifiers and the bundled vis-network constant. The current
  library also matches the exact npm source after line-ending normalization.
  A separate scan of commit messages, author/committer metadata, tag contents,
  and ref names had zero findings; no author details are reproduced here.
- No LFS entries were present across the mirror's available refs. The repository
  has no wiki or Discussions enabled, no retained Actions artifacts, and no
  commit/issue/PR review comments returned by the corresponding paginated APIs.
- GitHub metadata covers all nine closed, merged, owner-authored pull requests
  (the issue endpoint contains those same nine PRs), 40 workflow runs, repository
  variables/environment and secret-name metadata, and all source release records.
  Secret values cannot be read through this metadata API and were never queried,
  copied, or verified against a service. All 37 source-release text/checksum/update
  assets were downloaded and scanned. This metadata/text scan had zero findings.
- Both repositories' release inventories were collected privately. The source
  has 19 release records and 117 assets; the historical download feed has 163
  assets. Their advertised SHA-256 digests identify **166 unique assets**, about
  **16.12 GB**: 56 EXE, 28 DMG, 27 blockmap, 28 text, and 27 YAML objects.
  Downloaded bytes were independently verified before identical advertised
  digests deduplicated inspection. No released executable was run.
- All **166 unique historical assets** now independently match the advertised
  SHA-256 and length: **16,124,425,432 bytes**, including all 56 EXEs, 28 DMGs,
  27 blockmaps, 28 text objects and 27 YAML objects. Each has a completed bounded
  static report with no current terminal extraction or scanner limit. Inspection
  covers regular primary payloads, nested archives/ASAR, text/config, original
  installer and PE/Mach-O/ELF strings, and archive link metadata; no released
  program was executed. The separate selected-member pass retained all 84
  binary/image ASAR headers (48,522 paths) and 420 archive link targets without
  following filesystem links. The broader user-data/database/history/browser
  store/credential/key/env/backup path review found no match in the retained
  archive and ASAR metadata. Archive traversal is bounded to five nested levels;
  scanner decoding/archive inspection is bounded to three.
- All 27 blockmaps' 185 generic-key candidates were verified as public download
  checksum arrays under their exact schema. The 55 text/YAML objects had zero
  raw scanner candidates. All **1,805** binary/image candidates are reviewed:
  451 Apple CMS `cdhashes` values, 565 current public print-profile identifiers,
  357 exact historical public product/profile identifiers, 420 vis-network
  property constants, and 12 Chromium extension public-key matches. Apple's
  [codesigning source](https://github.com/apple-oss-distributions/Security/blob/main/OSX/libsecurity_codesigning/lib/signer.cpp)
  constructs `cdhashes` from its signing hash list; inspected values decode to
  20-byte CodeDirectory hashes. The 180 earlier vis context gaps were resolved
  by independent selection, gzip/Brotli decoding and exact full-source equality
  after line-ending normalization. The 12 matches in two old installers'
  identical `resources.pak` decode to three RSA SubjectPublicKeyInfo records per
  resource file, containing only modulus/public exponent and no private-key
  components. Chrome's [manifest key documentation](https://developer.chrome.com/docs/extensions/reference/manifest/key)
  identifies that field as the public key used to preserve an extension ID.
  No scanner candidate remains unreviewed and no persistent account credential
  or working user-data file has been confirmed within the inspected scope.
  These bounded static results are separate from contribution rights, current
  distribution licensing and live integration evidence.
- Sixteen legacy private records labelled `download-failure` were local
  read-only-directory cleanup errors (`WinError 5`), caught under an inaccurate
  pipeline label. They do not establish network failures. Later successful
  verified payload reports supersede those records; original limits remain
  retained privately. New failures record the actual stage. Acquisition
  diagnostics retain timing, HTTP status, sanitized hostname, length and hashes,
  without authorization headers, signed redirect queries or bearer URLs.
  Later selected-member retries also resolved two local reader issues: HFS
  member paths require their volume prefix, and APFS stdout selection needs
  `-sns-` to avoid concatenating alternate streams after primary data. The
  original per-asset limits and failed reader attempts remain private; current
  successful byte/header proofs supersede them. No reader error is presented
  as a corrupt download or a failure of the historical application.
- Eight of 40 Actions run log archives were still available and were scanned;
  32 returned HTTP 410 after retention expiry. Source/context review identified
  six expired, process-scoped Electron loopback authorization values from three
  successful Mac runs and both architectures. Each appears in three startup
  message formats (18 occurrences); the scanner caught only eight occurrences.
  These are transient app-process values, with no persistent provider/GitHub
  account credential finding. No values are reproduced here. The current Mac
  packaging startup check now captures stdout/stderr privately and isolates
  synthetic DB/history paths; native Mac execution remains unperformed.

## Findings and decisions

The initial history scan reported 15 exact generic-key matches. Fourteen are
print-product/profile identities or conformance data; the last is a constant in
the bundled vis-network 9.1.9 UMD library. The historical library blob was compared
byte-for-byte with `package/standalone/umd/vis-network.min.js` from the public
9.1.9 npm tarball and matched. Full merge/local-ref inspection added one unique
profile-identity fingerprint. These **16 reviewed false-positive fingerprints**
are narrowly retained in `.gitleaksignore`; no whole file, directory, key rule,
or credential provider is suppressed. No persistent credential finding has been
confirmed; transient Actions-log exposure is described separately above.
The final all-ref rerun after retaining the merge fingerprint reported zero
findings; current-source raw candidates are accounted for separately above.

Tracked settings contain the documented public GitHub OAuth application client
ID, which is an identifier rather than a secret. API keys and tokens remain in
ignored local SQLite/provider storage. Ignore rules also cover local databases,
WAL/SHM companions, environment variants, keys/certificates, history, backups,
artifacts, and real local Store identity input. Current documentation no longer
names a developer's absolute database path or working project. Historical Git
metadata and historical documentation still contain author identities and local
path/project references; this audit has not rewritten them or treated them as
secrets.

Two unmerged remote hosting/speed-reading branches contain contributions from
another author. That author's commits are not in `main`. On 2026-10-06 the owner
confirmed having that contributor's permission to offer the code under the
selected dual terms, so the retained branches may become public with the
repository. No branch was deleted, hidden, or rewritten.

First-party source/documentation/branding now have the owner's selected unchanged
PolyForm Noncommercial or Internal Use 1.0.0 alternatives. Branding ownership
and generated variants are recorded in `Lorekeeper/wwwroot/branding/SOURCES.md`.
The contribution, support, security, privacy, funding, build, and release docs are
prepared. The app is described as source available, with commercial bookmaking
and internal use permitted and commercial software redistribution outside the
grants. Creative output does not acquire the software license merely through use.

The root third-party notice manifest retains full exact-package/upstream texts,
copyrights, source commits/tags, package content identities, and SHA-256 hashes,
including native PDFium, Skia, .NET, SQLite, and libgit2 obligations. libgit2's
GPLv2 text includes the reviewed unlimited linking exception; its admission is
limited to the unchanged compiled library linked into Lorekeeper. Fonts, ICC,
editor, Press, Electron/Chromium, and runtime notices have independent retained
boundaries. SDK runtime packs are separate from the ordinary NuGet library graph:
their full original .NET and ASP.NET Core licenses and third-party notices are
retained under `licenses/runtime-notices/` with exact version, package SHA-512,
source commit, and file hashes. Distinct paths prevent first-party root notice
names from replacing those runtime texts on Windows. Packaged framework versions
must match retained evidence on each target. Both current Debug 10.0.0 and pinned
Release 10.0.12 pack sets are retained. The modern AppImage runtime contains
statically linked modified libfuse 3.15.0 under LGPL-2.1, alongside permissive
musl, squashfuse, zstd, zlib, and mimalloc components. Seven exact source/version
records and full component terms, including musl's per-file copyright/license
blocks, are now retained under `licenses/appimage-runtime/`. The modified-libfuse
notice records the original supplier change date and exact patch/hash. This
satisfies full notice retention.

LGPL 2.1 requires the modified libfuse source and enough material for a
recipient to modify the library and relink the program that uses it. On
2026-10-06 the owner approved publication with
`Lorekeeper-AppImage-Runtime-Sources-20251108-x86_64.tar.gz`
(SHA-256 `5096369393ea6eab9d7a86cc69439f99030e4b4314b7e7bf456878c4ca1cf5a6`,
1,900 files) as a release asset beside every AppImage. It contains the original
archives, the complete patched preferred libfuse source with its dated
modification notice, the MIT runtime source and supplier build scripts, all
component terms, recipient instructions, and `recipient-validation/`. That
directory records a real exercise run offline in an isolated Alpine 3.21 root
from digest-locked signed packages. It changed libfuse `fuse_log.c`, relinked the
type2 runtime from commit `dd6cebed` sources (runtime SHA-256 `9bad996f…`), and
repacked a Lorekeeper AppImage with `appimagetool --runtime-file`. All 1,133
payload entries matched. Local workspace paths in the published scripts and
logs are replaced with placeholders.

The supplier's own build logs have expired, so the exact Alpine package
revisions behind the vendor binary are inferred from branch state at its build
date. Those packages (musl, zstd, zlib, mimalloc, compiler startup objects) are
permissively licensed and do not limit a recipient's ability to modify libfuse
and relink, so bit-for-bit reproduction of the vendor binary is not required.
`eng/appimage-publication.json` is now approved and binds the archive, its
manifest, relink/repack logs and outputs to the selected runtime and toolset.

## Remaining clearance

Static inspection and candidate triage are complete for the 166 available unique
release assets within the recorded bounds. Detailed evidence, prior failures and
superseding successful checks remain private. Available Actions logs are reviewed
as described above; the **32 expired archives cannot be cleared by run metadata**.

Rerun secret/current-file checks against the final committed source and exact
remote refs before the visibility change. Retain only sanitized results.
Automatic history rewriting, branch deletion, visibility changes, publication,
settings changes, and archival are outside preparation. Live UI/provider/platform,
installed update handoff, Store certification, and trailer captures are recorded
separately in [v1 QA evidence](v1-qa.md); none follows from this audit.
