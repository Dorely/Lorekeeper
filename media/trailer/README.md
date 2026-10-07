# Lorekeeper feature trailer

## From premise to published book

The 90-second trailer shows one live, end-to-end agentic run in the synthetic
project "QA v2 Trailer — The Unquiet Oath": a premise becomes a Book Brief and
outline, web research becomes a cited World Brief, Editor Chat drafts a chapter,
a review note is revised and kept, and the Publish Assistant generates cover
art, creates paperback, EPUB and PDF releases and prepares their files. Every
agent turn used a real provider; nothing is simulated. The accepted application
is `71dfa6394f982fa700db59f3f243d7e64dd9357d`, recorded under the
[trailer evidence](../../docs/evidence/v2-trailer-qa.md), which also lists each
clip and the defects the run found.

This directory holds the 720p GitHub version (`lorekeeper-v2-github.mp4`), the
1920×1080 poster (`lorekeeper-v2-poster.png`), the English captions
(`lorekeeper-v2.en.vtt`), the Store title (`lorekeeper-v2.title.txt`) and the
export provenance (`lorekeeper-v2-provenance.json`). The 1080p Store master
stays outside Git in `.artifacts/trailer/v2/export/`. Uploading or publishing
the trailer is a launch action that needs the owner's approval.

## Capture

The footage was captured from the app's page in headless Chromium at
1920×1080 and 30 fps, so no desktop chrome, notifications or filesystem paths
appear. Keep private projects, credentials, and connection or settings screens
out of every frame, and do not simulate provider results. Long agent turns
were sped up in the edit; every sped-up stretch carries an on-screen label
giving the speed, and every cut or follow-up turn carries an on-screen note.

Raw clips and `captures.json` live under ignored `.artifacts/trailer/v2/raw/`.
The manifest names the accepted source commit, the synthetic project, the
committed QA evidence and its SHA-256, and each clip's file, SHA-256 and whole
`seconds` in edit order; the clips must total at most 90 seconds. Review every
clip for privacy and truthful behavior before setting `reviewed: true` and
`qaAccepted: true`. These fields are review attestations, not automated proof.

The QA evidence must contain `Tested source commit: ` followed by the accepted
application's full SHA. That commit must be an ancestor of the export checkout,
and runtime and packaging inputs must match it with no staged, unstaged or
untracked changes.

## Export

```powershell
pwsh -NoProfile -File scripts/export-feature-trailer.ps1 -Version v2
```

Windows PowerShell 5.1 (`powershell -NoProfile -File ...`) also works. FFmpeg
must include the libx264 encoder and the libass subtitles filter.

The recipe joins the clips, burns in the captions, and synthesizes a quiet
original instrumental bed from mathematical oscillators; it uses no stock
audio, samples, external media or voice. Provenance records the source commit,
capture hashes, captions, audio recipe, exact FFmpeg version, output hashes and
sizes after the codec, frame count, frame rate, audio, thumbnail and size
metadata are checked.

The Store master is 1920×1080 AVC1 High Profile progressive MP4 with YUV420P,
CABAC, a closed 15-frame GOP, two B frames, 50 Mbps video, AAC-LC stereo at
48 kHz and 384 kbps, Fast Start and no edit lists, following the current
[Microsoft trailer requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/screenshots-and-images).
The poster is a real frame from the finished trailer. The GitHub export is
720p H.264 with 128 kbps AAC; its video bitrate is derived from the duration so
the file stays at or below 9,500,000 bytes. Copy only the GitHub MP4, poster
and provenance into this directory, and keep raw footage, intermediates and the
Store master out of Git.
