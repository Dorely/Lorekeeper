# WeasyPrint PDF/X fallback spike

> Historical evidence only. ADR 0003 superseded this reduced-scope decision on
> 2026-07-31; WeasyPrint is not part of Lorekeeper's current runtime.

Last reviewed: 2026-07-30

## Decision

WeasyPrint 69.0 is accepted as the Phase 1 press-sidecar foundation for a
reduced `Preview` scope. It can generate deterministic prose interiors and
full-wrap covers, and a pinned adapter can emit the 2001 PDF/X declaration and
PDF 1.3 shape required by Ingram's current guide. It is not yet accepted as an
independently conforming PDF/X implementation or as KDP/Ingram-compatible.

This decision unblocks the structured manuscript work. It does not waive the
remaining release and external gates. Phase 1 can only become `Verified` after
the production integration owns pinned native binaries and notices and its
representative artifacts pass the independent Acrobat, vendor-upload, and
physical-proof checks in Feature 7.

The architecture decision is recorded in
[`0002-accept-weasyprint-for-preview-press-runtime.md`](../decisions/0002-accept-weasyprint-for-preview-press-runtime.md).

## Why the stock PDF/X-1a option is insufficient

WeasyPrint 69's public API exposes PDF/X-1a, PDF/X-3, PDF/X-4, custom ICC output
intents, and device CMYK. Its own documentation says generated standards files
are not guaranteed to be valid and must be checked. More importantly, the
pinned source registers the stock `pdf/x-1a` name as PDF/X-1a:2003 with PDF
1.4. Ingram's May 2026 guide instead requires PDF/X-1a:2001 or PDF/X-3:2002.

The spike therefore registers a deliberately narrow `pdf/x-1a:2001` adapter
using WeasyPrint's pinned PDF/X finisher with:

- `variant="a:2001"`;
- PDF version 1.3;
- one named CMYK output intent;
- fixed creation/modification metadata;
- embedded full fonts; and
- no user-authored HTML, CSS, URI, or filesystem input.

That adapter uses a non-public WeasyPrint module. This is acceptable only behind
an exact 69.0 pin and locked structural fixtures. A WeasyPrint upgrade is a
renderer-version change that must rerun pagination, binary, license, internal
inspection, independent validation, and vendor acceptance gates.

Sources:

- [WeasyPrint 69 API and supported PDF variants](https://doc.courtbouillon.org/weasyprint/stable/api_reference.html)
- [Pinned WeasyPrint 69 PDF/X implementation](https://raw.githubusercontent.com/Kozea/WeasyPrint/v69.0/weasyprint/pdf/pdfx.py)
- [WeasyPrint 69 security release](https://github.com/Kozea/WeasyPrint/releases/tag/v69.0)
- [IngramSpark May 2026 File Creation Guide](https://www.ingramspark.com/hubfs/downloads/file-creation-guide.pdf)

## Executed fixture

`Lorekeeper.Press.Weasy` is a disposable Python 3.13+/WeasyPrint 69 harness. It
retains protocol version 1 from the rejected Rust candidate so the two
implementations can be compared without changing the application.

The Windows x64 frozen process generated:

| Evidence | Result |
|---|---|
| Ingram-named interior | PDF 1.3, 1 page, 432 × 648 pt |
| Ingram-named cover | PDF 1.3, 1 page, 882.18 × 666 pt |
| Ingram-named spine | 0.18 pt from one page × 0.0025 in/page |
| KDP-named fixture | PDF 1.7, 3-page interior and 882.54 × 666 pt cover |
| PDF/X metadata | `GTS_PDFXVersion` and conformance declare `PDF/X-1a:2001` |
| Output intents | One four-component profile in each artifact |
| Fonts | Embedded; full-font mode removes subset nondeterminism |
| Colors | ICCBased/Lab resources; no DeviceRGB operators found |
| Forbidden features | No transparency, encryption, annotations, or actions found |
| Input isolation | Generated/escaped HTML only; fetcher accepts the fingerprinted ICC URI only |
| Output isolation | Validated child job ID, exclusive immutable directory, staged atomic publish |
| Frozen binary | 30,753,243 bytes; SHA-256 `BE7E6918FEAA19ABC3B4F2EE7C203B34E8200E1D8A0FD0E4F8E6A0167D439CE7` |
| Binary audit | 105 binaries, zero uncontrolled-source entries, release-license gate closed |
| Upstream native provenance | 204 binary payloads extracted directly from the fingerprinted portable executable |
| Fresh whole-job time | 1.4–2.2 seconds in the recorded controlled Windows runs |

The process returns `declaredStandard = "PDF/X-1a:2001"` but deliberately leaves
both `independentlyValidatedStandard` and `claimedStandard` empty. The internal
parser is defense in depth, not a standards validator.

The harness promotes every WeasyPrint warning to failure, rejects unknown
protocol fields, caps request/profile sizes, prevents path traversal, never
loads project HTML, and writes no partial final job directory. A repeated job ID
fails because published spike artifacts are immutable.

## Pagination and composition comparison

The fallback can express the Phase 1 prose controls through generated paged CSS:

- fixed trim and mirrored margins;
- recto chapter starts;
- intentional blank pages produced by recto starts;
- running page counters and named page rules;
- widow/orphan thresholds;
- justification, first-line indents, and language-aware hyphenation;
- full-wrap cover width derived from final interior page count; and
- device-CMYK cover fills.

The spike does not yet prove all production typography. Running heads,
front-matter Roman numerals, deterministic pinned font assets, the full
hyphenation locale matrix, long-book stress fixtures, and block-to-page maps
belong to Feature 5. WeasyPrint also documents right-to-left and bidirectional
text as unsupported, consistent with Phase 1's explicit Latin-script,
left-to-right boundary.

## CMYK profile selection

The selected engineering fixture is
`ISOcoated_v2_300_bas.ICC` from
`icc-profiles-basiccolor-printing2009-1.2.0`:

| Item | Recorded value |
|---|---|
| Source archive | `icc-profiles-basiccolor-printing2009-1.2.0.tar.bz2` |
| Archive SHA-256 | `0D1AB5CB8A72AB76A02C67F07708E94A5794397EEAC0ACDB7503E2C11B515707` |
| Profile size | 1,052,612 bytes |
| Profile SHA-256 | `B424C77F40C3423C925536F8AE08634985CCD0FE80EB253D5D229197DED7E886` |
| License | zlib/libpng; basICColor GmbH, 2007–2010 |
| Profile class/components | CMYK output profile, four components |
| Nominal TAC | 300% |

The archive's license permits commercial use and redistribution while requiring
the notice to remain. OpenICC lists this package as the source for its default
printing profiles. The profile may be redistributed with its notice, but the
300% profile limit does not satisfy Ingram's 240% content-density guidance by
itself. The fixture uses explicit CMYK values at or below 240%; Feature 7 must
inspect every emitted fill/image and block any pixel or vector content above the
versioned vendor threshold.

The newer PSO Coated v3 ECI profile was also examined and rejected for bundling
because its embedded terms do not grant unrestricted redistribution. A small
CC0 CMYK compatibility profile was rejected because it lacks the output
conversion behavior required for a production press transform.

Sources:

- [OpenICC profile package source](https://sourceforge.net/p/openicc/icc-profiles-openicc/ci/master/tree/)
- [OpenICC recommended profile packages](https://wiki.freedesktop.org/www/OpenIcc/ProfilePackages/)

The fixture profile is an engineering default, not a claim that Ingram prints
to ISO Coated v2. Feature 7 must pair output intent, paper, ink, and vendor
profile policy explicitly and preserve the user's chosen/recommended condition
in the manifest.

## Dependency, packaging, and license disposition

Python dependencies are exact direct pins in `pyproject.toml` and exact
transitive versions/hashes in `uv.lock`. The generated
[`weasyprint-spike-license-inventory.json`](weasyprint-spike-license-inventory.json)
joins the active Windows fixture packages to their metadata and hashes every
installed license/copying/notice file it discovers. Conditional target packages
remain visibly incomplete rather than receiving inferred license data. The
critical direct pins are:

- WeasyPrint 69.0 (BSD-3-Clause);
- pypdf 6.14.2 (BSD-3-Clause);
- cffi 2.0.0 (MIT); and
- PyInstaller 6.21.0 for the disposable build.

The frozen Windows proof used the official WeasyPrint 69 portable native stack.
The downloaded archive SHA-256 was
`330101FF3EA50EBDE4ABF805283B6D703D5F3D71C77C983DB94357EC4524A3EF`.
Its Pango/Fontconfig/HarfBuzz and related native libraries are technically
loadable inside the one-file process. The distribution carries exact Liberation
Serif regular/bold files, their license, and a minimal Fontconfig configuration
next to the executable. The owning application process must set
`FONTCONFIG_FILE`, `FONTCONFIG_PATH`, and `XDG_CACHE_HOME` to that controlled
directory before starting the native process; setting them inside frozen Python
is too late because Fontconfig may initialize before Python code runs. The
launcher validates those variables and the exact config, font, and license
fingerprints before importing WeasyPrint, failing with a structured diagnostic
when any input is missing, hostile, or changed.

The controlled build records the Python interpreter, native archive, font,
source, executable, and binary-inventory hashes. Its binary inventory classifies
all 105 collected native/runtime binaries and rejects any source outside the
explicit native, Python, build-environment, or Windows-system roots. It extracts
the 204 native binary payloads directly from `dist/weasyprint.exe` inside the
verified upstream archive into a fresh build-owned directory and records that
archive-derived manifest, rather than trusting a caller-supplied DLL directory.
The exact records are:

- [`weasyprint-spike-build-evidence.json`](weasyprint-spike-build-evidence.json);
- [`weasyprint-spike-native-source.json`](weasyprint-spike-native-source.json);
- [`weasyprint-spike-binary-inventory.json`](weasyprint-spike-binary-inventory.json);
  and
- [`weasyprint-spike-verification-evidence.json`](weasyprint-spike-verification-evidence.json).

The upstream portable archive does not itself provide a complete,
release-ready third-party notice inventory for every native library. Therefore
the spike does **not** approve copying that binary bundle into Lorekeeper.
Feature 5 must assemble every target from controlled packages, record each
native library/version/source/license/hash, reproduce required LGPL and other
notices, provide relinking/source obligations where applicable, scan the final
bundle, and exercise Windows x64 plus macOS arm64.

## Remaining external evidence

The following were unavailable or unauthorized in this environment and remain
open:

- Acrobat Pro Preflight using the named PDF/X-1a:2001 profile, including
  profile/version/fingerprint and deliberately invalid fixtures;
- authenticated KDP Print Previewer upload;
- authenticated IngramSpark upload preflight;
- physical proof inspection;
- macOS arm64 binary builds and runtime snapshots; and
- final release-level native dependency notices.

Accordingly, the accepted scope is `Preview`. Product language must never call
these files PDF/X-conformant, print-ready, or vendor-compatible until those
specific evidence records exist for the exact artifact hashes.

## Reproduction

Run source-level protocol and parser fixtures:

```powershell
cd Lorekeeper.Press.Weasy
uv run --locked python -m unittest discover -s tests -v
uv run --locked python scripts/generate-license-inventory.py
```

Build a frozen Windows spike from an explicitly supplied Python/native stack:

```powershell
.\scripts\build-windows-spike.ps1 `
  -PythonPath <python-3.13-executable> `
  -NativeArchivePath <weasyprint-windows-archive> `
  -FontRegularPath <LiberationSerif-Regular.ttf> `
  -FontBoldPath <LiberationSerif-Bold.ttf> `
  -FontLicensePath <Liberation-Fonts-LICENSE>
```

Verify the frozen artifact and fingerprinted profile:

```powershell
.\scripts\verify-spike.ps1 `
  -ExecutablePath <lorekeeper-press-weasy.exe> `
  -CmykProfilePath <ISOcoated_v2_300_bas.ICC>
```
