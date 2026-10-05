# Public-sharing preparation audit

Date: 2026-10-05. Status: **Preparation evidence; public clearance remains open.**
No visibility, settings, refs, history, release publication, or Store submission
was changed by this audit. Detailed reports and downloaded material stay private
under ignored `.artifacts/sharing-audit/`. Do not upload that directory.

## Scope and evidence

- A disposable origin mirror contains every advertised remote head/tag and every
  available pull-request head/merge ref. All local refs were also fetched into a
  separate mirror namespace: 70 refs and 640 unique reachable commits.
- Gitleaks **8.30.1** from its official release was checksum-verified. The Windows
  x64 archive SHA-256 is
  `d29144deff3a68aa93ced33dddf84b7fdc26070add4aa0f4513094c8332afc4e`.
  Scans used full redaction, all refs, full history including merge diffs, and
  bounded decoding/archive inspection. The final all-ref scan traversed 638
  diff-bearing commits and approximately 73.70 MB of text.
- The private ref snapshot SHA-256 is
  `784c53607c9364f234babc46e089ef642eefa820524ec5711c885f1e1d962bcb`.
  This binds the snapshot, not later commits. Recheck exact final refs before
  public visibility; the preparation commit is not yet included.
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

## Findings and decisions

The initial history scan reported 15 exact generic-key matches. Fourteen are
print-product/profile identities or conformance data; the last is a constant in
the bundled vis-network 9.1.9 UMD library. The historical library blob was compared
byte-for-byte with `package/standalone/umd/vis-network.min.js` from the public
9.1.9 npm tarball and matched. Full merge/local-ref inspection added one unique
profile-identity fingerprint. These **16 reviewed false-positive fingerprints**
are narrowly retained in `.gitleaksignore`; no whole file, directory, key rule,
or credential provider is suppressed. No credential finding has been confirmed.
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
musl, squashfuse, zstd, zlib, and mimalloc components. Full notices alone do not
close its LGPL source and recipient relinking obligations: corresponding
libfuse source, the supplier patch, runtime build/relink material, and precise
dependency provenance remain required before Linux distribution clearance.
Local packaging for validation does not establish that clearance.

## Remaining clearance

Finish static inspection of every unique historical binary/image asset, including
archive paths and nested payloads, excluded user-data/database/history/config
content, and credential scanning of extracted text/config and executable strings.
Retain unsupported-format, download, extraction, or scan limits per asset privately
and summarize any unresolved blockers here. Review Actions logs if still available;
run metadata alone does not establish their content clearance.

Obtain the owner's decision on the two unmerged contribution branches, complete
AppImage/native third-party evidence, and rerun secret/current-file checks against
the final committed source and exact remote refs. Retain only sanitized results.
Automatic history rewriting, branch deletion, visibility changes, publication,
settings changes, and archival are outside preparation. Live UI/provider/platform,
installed update handoff, Store certification, and trailer captures are recorded
separately in [v1 QA evidence](v1-qa.md); none follows from this audit.
