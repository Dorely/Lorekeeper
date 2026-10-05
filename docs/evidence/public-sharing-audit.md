# Public-sharing preparation audit

Date: 2026-10-05. Status: **Preparation evidence; public clearance remains open.**
No visibility, settings, refs, history, release publication, or Store submission
was changed by this audit. Detailed reports and downloaded material stay private
under ignored `.artifacts/sharing-audit/`. Do not upload that directory.

## Scope and evidence

- A disposable origin mirror contains every advertised remote head/tag and every
  available pull-request head/merge ref. All local refs were also fetched into a
  separate mirror namespace. The refreshed preparation snapshot includes
  candidate `9eec7b825c0052299beccb844c544aea875490c3`: 70 refs and 644
  unique reachable commits.
- Gitleaks **8.30.1** from its official release was checksum-verified. The Windows
  x64 archive SHA-256 is
  `d29144deff3a68aa93ced33dddf84b7fdc26070add4aa0f4513094c8332afc4e`.
  Scans used full redaction, all refs, full history including merge diffs, and
  bounded decoding/archive inspection. That refreshed all-ref scan traversed
  642 diff-bearing commits and 76,365,898 text bytes with zero findings
  after the 16 individually reviewed false-positive exclusions below.
- The private ref snapshot SHA-256 is
  `17eb39d6d50f44a1a8cf4865242ff8d74cf41bf525751a5bab6d5d5a368baf56`.
  This binds that snapshot, not later commits. Recheck exact final refs before
  public visibility; subsequent commits are not yet included. An exact
  committed-source ZIP of that candidate, forced to LF with Git's
  `core.autocrlf=false` and `core.eol=lf`, contains 1,519 regular files and
  **59,227,766 uncompressed file bytes**. The ZIP itself is 15,399,068 bytes;
  its SHA-256 is
  `204d1a3274baf324e80894ea49d2dcb0f3cff54236c08cdb8ed7f649096ba622`.
  Gitleaks inspected **44,952,715 text bytes**, a different metric. The earlier
  report's approximately 45.47 MB described inspected text, not total source
  file bytes. Like-for-like LF archives of `6ba993a` and `441c96e` contain
  58,885,004 and 59,209,172 uncompressed bytes respectively.
  The candidate's eight raw generic-key matches were reviewed as seven public
  print-profile identifiers and the bundled vis-network constant. The current
  library also matches the exact npm source after line-ending normalization.
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
  Historical executable/image downloads and payload inspection are still in
  progress. Matching advertised digests will only deduplicate an asset after the
  downloaded bytes are independently verified. No released executable is run.
- The historical inspection checkpoint before the resumed sequential retries
  contains **135 per-digest reports**:
  82 text/YAML/blockmap assets, 28 DMGs, and 25 EXEs. All 27 blockmaps' 185
  generic-key candidates were verified as differential-download checksum arrays
  under their exact schema. The 55 text/YAML objects had no scanner findings.
  Of the 28 hash-verified DMGs (5,939,693,183 bytes), ten have completed
  payload/string scans and 18 retain
  HFS link/listing/extraction limits requiring retry. All 25 hash-verified EXEs
  (4,689,572,499 bytes) have completed payload/string scans. Thirty-one EXEs have
  no completed payload report. The 35 completed binary/image scans contain 955
  raw candidates: 160 public Apple CMS `cdhashes` values, 490 current public
  print-profile identifiers, and 125 exact historical public product/profile
  identifiers have been reviewed. Apple's
  [codesigning source](https://github.com/apple-oss-distributions/Security/blob/main/OSX/libsecurity_codesigning/lib/signer.cpp)
  constructs `cdhashes` from the signing hash list; the inspected values decode
  to 20-byte CodeDirectory hashes. **180 vis-network matches remain unreviewed**
  because earlier bounded gzip/minified contexts were inadequately aligned with
  decoded scanner coordinates. No user-data/database/history/credential-file
  path or persistent credential has been confirmed in the extracted regular
  files inspected so far. This does not cover missing payloads, limited DMGs,
  unresolved contexts, or expired logs. This is partial inspection, not
  historical binary clearance. Further work is serialized to one extractor and
  scanner at a time around local native-build resource requirements.
- Sixteen legacy private records labelled `download-failure` were local
  read-only-directory cleanup errors (`WinError 5`), caught under an inaccurate
  pipeline label. They do not establish network failures. Later successful
  verified payload reports supersede those records; original limits remain
  retained privately. New failures record the actual stage. Acquisition
  diagnostics retain timing, HTTP status, sanitized hostname, length and hashes,
  without authorization headers, signed redirect queries or bearer URLs.
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

**Two unmerged remote hosting/speed-reading branches contain contributions from
another author whose permission to offer that code under the selected dual
terms has not been established.** That author's commits are not in `main`.
Public visibility exposes those retained refs too, so owner review of those
contribution rights remains a concrete clearance item. No branch was deleted,
hidden, relicensed by assumption, or rewritten.

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
satisfies full notice retention only. A reviewable approximately 24 MB source
preparation bundle remains ignored and private: it includes original archives,
patched preferred libfuse source, supplier build inputs, Alpine recipes/patches,
all terms and corrected recipient relink instructions. The archive contains
1,884 files, is 24,409,771 bytes, and has SHA-256
`0cb245b2f9cb21253a2440eba5de2960c3b1599f6247b391354c442d32ef0693`.
Binary/debug version evidence is distinguished from Alpine patch/build
provenance inferred from the build date;
the supplier build logs are unavailable. Debug line tables expose source paths
and some compiler producer options, but no exact source-file checksum witness
has been established. A changed-library rebuild/relink/repack
has not been performed. Full notices and prepared source alone do not close
the LGPL source/relink and precise-provenance obligations or the permissive
admission gate. Local packaging for validation does not establish public
AppImage clearance. `eng/appimage-publication.json` binds the reviewed runtime,
toolset and required source-preparation release asset while remaining explicitly
blocked; it is a fail-closed publication boundary, not clearance.

## Remaining clearance

Finish static inspection of every unique historical binary/image asset, including
archive paths and nested payloads, excluded user-data/database/history/config
content, and credential scanning of extracted text/config and executable strings.
Retain unsupported-format, download, extraction, or scan limits per asset privately
and summarize any unresolved blockers here. Available Actions logs are reviewed
as described above; the 32 expired archives cannot be cleared by run metadata.

Obtain the owner's decision on the two unmerged contribution branches, complete
AppImage/native third-party evidence, and rerun secret/current-file checks against
the final committed source and exact remote refs. Retain only sanitized results.
Automatic history rewriting, branch deletion, visibility changes, publication,
settings changes, and archival are outside preparation. Live UI/provider/platform,
installed update handoff, Store certification, and trailer captures are recorded
separately in [v1 QA evidence](v1-qa.md); none follows from this audit.
