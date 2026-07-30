"""Fail a frozen build if PyInstaller collected binaries from undeclared roots."""

from __future__ import annotations

import argparse
import ast
import hashlib
import json
from pathlib import Path
from typing import Any


parser = argparse.ArgumentParser()
parser.add_argument("--toc", required=True, type=Path)
parser.add_argument("--output", required=True, type=Path)
parser.add_argument("--allow-root", action="append", required=True)
arguments = parser.parse_args()

allowed_roots: list[tuple[str, Path]] = []
for value in arguments.allow_root:
    label, separator, path_value = value.partition("=")
    if not separator or not label or not path_value:
        parser.error("--allow-root must use label=path.")
    allowed_roots.append((label, Path(path_value).resolve(strict=True)))
toc = ast.literal_eval(arguments.toc.read_text(encoding="utf-8"))


def walk(value: Any):
    if isinstance(value, (list, tuple)):
        if (
            len(value) == 3
            and all(isinstance(item, str) for item in value)
            and value[2] in ("BINARY", "EXTENSION")
        ):
            yield value
        else:
            for item in value:
                yield from walk(item)


def inside(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
        return True
    except ValueError:
        return False


entries = []
rejected = []
for destination, source_name, kind in walk(toc):
    source = Path(source_name).resolve(strict=True)
    matched = next(
        ((label, root) for label, root in allowed_roots if inside(source, root)),
        None,
    )
    data = source.read_bytes()
    entry = {
        "destination": destination.replace("\\", "/"),
        "sourceClass": matched[0] if matched is not None else None,
        "sourceRelativePath": (
            source.relative_to(matched[1]).as_posix() if matched is not None else str(source)
        ),
        "kind": kind,
        "byteLength": len(data),
        "sha256": hashlib.sha256(data).hexdigest(),
        "licenseStatus": "release-review-required",
    }
    entries.append(entry)
    if matched is None:
        rejected.append(entry)

report = {
    "schemaVersion": 1,
    "allowedRootClasses": [label for label, _ in allowed_roots],
    "binaryCount": len(entries),
    "releaseLicenseGatePassed": False,
    "binaries": sorted(
        entries,
        key=lambda item: (item["destination"], item["sourceClass"] or "", item["sourceRelativePath"]),
    ),
}
arguments.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")

if rejected:
    sources = "\n".join(entry["sourceRelativePath"] for entry in rejected)
    raise SystemExit(f"PyInstaller collected binaries outside declared roots:\n{sources}")
