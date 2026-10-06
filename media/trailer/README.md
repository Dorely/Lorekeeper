# Lorekeeper v1 feature trailer

The trailer is finished: real footage of the QA-tested build
(`d092bd696e46086e6cefd7cb9bfe50e6ef295f03`) in the synthetic project
"QA v1 Trailer — The Lantern Atlas", recorded under the
[v1 QA evidence](../../docs/evidence/v1-qa.md). This directory holds the
720p GitHub version (`lorekeeper-v1-github.mp4`), the 1920×1080 poster
(`lorekeeper-v1-poster.png`), the English captions and the export provenance
(`lorekeeper-v1-provenance.json`). The 1080p Store master stays outside Git.
Uploading or publishing the trailer is a later launch action.

## Capture

Use a 1920×1080, 30 fps application-only capture. Keep private projects,
credentials, connections/settings screens, desktop chrome, notifications, and
filesystem paths out of every frame. Record the actual application behavior,
including successful EPUB/PDF output. Do not simulate provider results. Capture
optional AI collaboration using only synthetic content after that connection's
functional check succeeds.

Place raw clips and `captures.json` under ignored `.artifacts/trailer/v1/raw/`.
The capture manifest identifies the accepted source commit, synthetic project,
successful QA evidence and its SHA-256, reviewed footage, and each clip's SHA-256. The clips are:

| File | Seconds | Demonstrated behavior |
|---|---:|---|
| `intro.mp4` | 5 | Lorekeeper and the synthetic book |
| `outline.mp4` | 8 | Outline, World, and connected story information |
| `writing.mp4` | 12 | Writing, revision, and optional AI collaboration |
| `sources.mp4` | 9 | Sources and citations |
| `design.mp4` | 11 | Designed Pages and covers |
| `export.mp4` | 10 | Review, history, EPUB, and PDF |
| `outro.mp4` | 5 | Final ownership message over real book footage |

All clips must be individually reviewed for privacy and truthful behavior before
setting `reviewed: true`. The script accepts only a full commit SHA and a
`qaAccepted: true` manifest. These fields are review attestations, not automated
proof of functional acceptance.

The committed acceptance record must contain `Tested source commit: ` followed
by that accepted application's full commit SHA, set only after functional QA
passes. The application commit must be an ancestor of the export checkout, and
runtime/packaging inputs must match it without staged, unstaged, or untracked
changes. Later QA/media evidence commits are permitted. This avoids making the
acceptance document refer to its own commit.

Example manifest shape (replace with actual accepted evidence):

```json
{
  "sourceCommit": "FULL_ACCEPTED_40_CHARACTER_SHA",
  "syntheticProject": "QA v1 Trailer — The Lantern Atlas",
  "qaEvidence": "docs/evidence/v1-qa.md",
  "qaEvidenceSha256": "ACTUAL_QA_EVIDENCE_SHA256",
  "qaAccepted": false,
  "reviewed": false,
  "clips": [
    { "file": "intro.mp4", "sha256": "ACTUAL_SHA256" }
  ]
}
```

## Export

```powershell
pwsh -NoProfile -File scripts/export-feature-trailer.ps1
```

Windows PowerShell 5.1 (`powershell -NoProfile -File ...`) also works.

The recipe trims and joins seven real clips into a 60-second edit, burns in
the matching captions, and synthesizes a quiet original instrumental bed from
mathematical oscillators. It uses no stock audio, samples, external media, or
voice. Output provenance records the source commit, capture hashes, captions,
audio recipe, exact FFmpeg version, output hashes, and sizes. FFmpeg must include
the libx264 encoder and the libass subtitles filter. Exported codec, frame count,
progressive frame rate, audio, thumbnail, and size metadata are checked before
provenance is written; playback and certification still require later review.

The large Store master stays in `.artifacts/trailer/v1/export/`. The recipe
produces a 1920×1080 AVC1 High Profile progressive MP4 with YUV420P, CABAC,
closed 15-frame GOP, two B frames, 50 Mbps video, AAC-LC stereo at 48 kHz and
384 kbps, Fast Start, and no edit lists. The matching PNG thumbnail is an actual
frame from the completed trailer. Store certification remains a later external
check. These settings follow the current
[Microsoft trailer requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/screenshots-and-images).

The GitHub export uses 720p H.264, two-pass video at 1.0 Mbps, and 128 kbps AAC,
and must be at most **9,500,000 bytes**. After reviewing both outputs, copy only
`lorekeeper-v1-github.mp4`, `lorekeeper-v1-poster.png`, and
`lorekeeper-v1-provenance.json` into this directory. Keep raw footage, audio,
intermediates, and the large master outside Git. External uploads require the
later launch approval.
