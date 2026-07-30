"""Independent artifact hash, geometry, metadata, and text extraction checks."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
import sys

from pypdf import PdfReader


EXPECTED_PROFILE_SHA256 = "b424c77f40c3423c925536f8ae08634985ccd0fe80eb253d5d229197ded7e886"


def fail(message: str) -> None:
    raise SystemExit(message)


input_path = Path(sys.argv[1]).resolve(strict=True)
payload = json.loads(input_path.read_text(encoding="utf-8"))
output_root = Path(payload["outputRoot"]).resolve(strict=True)
reports: list[dict[str, object]] = []
artifact_bytes: dict[tuple[str, str], bytes] = {}

for run_name in ("pdfxRunA", "pdfxRunB", "kdpRun"):
    run = payload[run_name]
    for artifact in run["artifacts"]:
        path = (output_root / artifact["relativePath"]).resolve(strict=True)
        if path.parent.parent != output_root:
            fail(f"Artifact escaped the output root: {path}")
        data = path.read_bytes()
        digest = hashlib.sha256(data).hexdigest()
        if digest != artifact["sha256"] or len(data) != artifact["byteLength"]:
            fail(f"Artifact bytes do not match the response: {path}")

        reader = PdfReader(path, strict=True)
        if len(reader.pages) != artifact["pageCount"]:
            fail(f"Artifact page count does not match the response: {path}")
        if not reader.pages:
            fail(f"Artifact contains no pages: {path}")
        text = "\n".join(page.extract_text() or "" for page in reader.pages)
        normalized_text = " ".join(text.split())
        if run_name.startswith("pdfx"):
            expected_text = (
                "Only Chapter"
                if artifact["kind"] == "interior-pdf"
                else "A Deliberate Rejection"
            )
        else:
            expected_text = (
                "Chapter One"
                if artifact["kind"] == "interior-pdf"
                else "The Cartographer's Lantern"
            )
        if expected_text not in normalized_text:
            fail(f"Expected text could not be extracted from {path}")

        box = reader.pages[0].mediabox
        measured = (round(float(box.width), 4), round(float(box.height), 4))
        evidence = run["evidence"]
        expected = (
            round(
                evidence[
                    "interiorWidthPoints"
                    if artifact["kind"] == "interior-pdf"
                    else "coverWidthPoints"
                ],
                4,
            ),
            round(
                evidence[
                    "interiorHeightPoints"
                    if artifact["kind"] == "interior-pdf"
                    else "coverHeightPoints"
                ],
                4,
            ),
        )
        if measured != expected:
            fail(f"Measured geometry does not match response evidence: {path}")

        metadata = reader.metadata or {}
        if run_name.startswith("pdfx"):
            if reader.pdf_header != "%PDF-1.3":
                fail(f"PDF/X-named artifact is not PDF 1.3: {path}")
            if metadata.get("/GTS_PDFXVersion") != "PDF/X-1a:2001":
                fail(f"PDF/X declaration is missing: {path}")
            intents = reader.trailer["/Root"].get("/OutputIntents")
            if intents is None or len(intents) != 1:
                fail(f"PDF/X artifact does not contain exactly one output intent: {path}")
            intent = intents[0].get_object()
            if intent.get("/S") != "/GTS_PDFX":
                fail(f"PDF/X artifact has the wrong output-intent subtype: {path}")
            profile = intent.get("/DestOutputProfile")
            if profile is None:
                fail(f"PDF/X artifact has no output profile: {path}")
            profile = profile.get_object()
            if profile.get("/N") != 4:
                fail(f"PDF/X artifact output profile is not CMYK: {path}")
            if hashlib.sha256(profile.get_data()).hexdigest() != EXPECTED_PROFILE_SHA256:
                fail(f"PDF/X artifact does not embed the pinned output profile: {path}")
        elif reader.pdf_header != "%PDF-1.7":
            fail(f"KDP-named artifact is not PDF 1.7: {path}")

        artifact_bytes[(run_name, artifact["kind"])] = data
        reports.append(
            {
                "run": run_name,
                "kind": artifact["kind"],
                "sha256": digest,
                "byteLength": len(data),
                "pageCount": len(reader.pages),
                "widthPoints": measured[0],
                "heightPoints": measured[1],
                "textExtracted": True,
            }
        )

for kind in ("interior-pdf", "cover-pdf"):
    if artifact_bytes[("pdfxRunA", kind)] != artifact_bytes[("pdfxRunB", kind)]:
        fail(f"Fresh deterministic fixtures differ: {kind}")

print(json.dumps({"artifacts": reports}, separators=(",", ":")))
