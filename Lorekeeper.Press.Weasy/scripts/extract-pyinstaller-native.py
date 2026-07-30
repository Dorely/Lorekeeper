"""Extract binary payloads from the verified upstream PyInstaller executable."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

from PyInstaller.archive.readers import CArchiveReader


parser = argparse.ArgumentParser()
parser.add_argument("--executable", required=True, type=Path)
parser.add_argument("--output", required=True, type=Path)
parser.add_argument("--manifest", required=True, type=Path)
arguments = parser.parse_args()

source = arguments.executable.resolve(strict=True)
output = arguments.output.resolve(strict=True)
reader = CArchiveReader(str(source))
entries: list[dict[str, object]] = []

for name, metadata in sorted(reader.toc.items()):
    if metadata[4] != "b":
        continue
    relative = Path(name.replace("\\", "/"))
    if relative.is_absolute() or ".." in relative.parts or ":" in relative.as_posix():
        raise SystemExit(f"Unsafe PyInstaller archive member: {name}")
    destination = (output / relative).resolve()
    try:
        destination.relative_to(output)
    except ValueError as exception:
        raise SystemExit(f"PyInstaller archive member escaped output: {name}") from exception
    data = reader.extract(name)
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(data)
    entries.append(
        {
            "path": relative.as_posix(),
            "byteLength": len(data),
            "sha256": hashlib.sha256(data).hexdigest(),
        }
    )

if not any(entry["path"] == "libpango-1.0-0.dll" for entry in entries):
    raise SystemExit("The verified archive did not contain the expected Pango binary.")

report = {
    "schemaVersion": 1,
    "sourceExecutableSha256": hashlib.sha256(source.read_bytes()).hexdigest(),
    "binaryCount": len(entries),
    "binaries": entries,
}
arguments.manifest.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
