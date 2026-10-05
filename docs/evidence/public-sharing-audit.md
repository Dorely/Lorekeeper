# Public-sharing preparation audit

Date: 2026-10-05. Status: **Preparation evidence; public clearance remains open.**
No visibility, settings, refs, history, release publication, or Store submission
was changed by this audit. Detailed reports and downloaded material stay private
under ignored `.artifacts/sharing-audit/`. Do not upload that directory.

## Scope and evidence

- A disposable origin mirror contains every advertised remote head/tag and every
  available pull-request head/merge ref. All local refs were also fetched into a
  separate mirror namespace. The refreshed preparation snapshot includes
  candidate `6ba993a5103accef77b5a878903027c15023d17f`: 70 refs and 641
  unique reachable commits.
- Gitleaks **8.30.1** from its official release was checksum-verified. The Windows
  x64 archive SHA-256 is
  `d29144deff3a68aa93ced33dddf84b7fdc26070add4aa0f4513094c8332afc4e`.
  Scans used full redaction, all refs, full history including merge diffs, and
  bounded decoding/archive inspection. That refreshed all-ref scan traversed
  639 diff-bearing commits and approximately 76.01 MB of text with zero findings
  after the 16 individually reviewed false-positive exclusions below.
- The private ref snapshot SHA-256 is
  `801dc36594b7cb1efdf11269f58e5ee5e90290f3123fc686d68f9f47ce4c70a2`.
  This binds that snapshot, not later commits. Recheck exact final refs before
  public visibility; subsequent platform/notice preparation is not yet included.
  An exact committed-source archive of that candidate contains 1,343 files
  (approximately 45.47 MB); its SHA-256 is
  `25d8d83f41ba58d0f35e54b854b352261ec2876596091e3772c0f97789b846cf`.
  Its eight raw generic-key matches were individually reviewed as seven public
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
- Static asset work has produced 101 per-digest reports: 82 text/YAML/blockmap
  assets and 19 DMG assets. All 27 blockmaps' 185 generic-key candidates were
  verified as differential-download checksum arrays under their exact schema.
  The 55 text/YAML objects had no scanner findings. Of the 19 hash-verified DMGs
  (approximately 4.20 GB), ten have no recorded extraction limit and nine retain
  listing/extraction limits requiring retry. Their 379 scanner candidates still
  require complete context triage; no user-data/database/history/credential-file
  path was found in the extracted regular files inspected so far. Sixty-five
  unique assets have no payload report yet, including the Windows installers.
  This is partial inspection, not historical binary clearance. Heavy work is
  paused while the native Linux build needs local resources.
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
The final rerun after retaining the merge fingerprint reported zero findings.

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
all terms and recipient relink instructions. Binary/debug version evidence is
distinguished from Alpine patch/build provenance inferred from the build date;
the supplier build logs are unavailable. A changed-library rebuild/relink/repack
has not been performed. Full notices and prepared source alone do not close
the LGPL source/relink and precise-provenance obligations or the permissive
admission gate. Local packaging for validation does not establish public
AppImage clearance.

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
