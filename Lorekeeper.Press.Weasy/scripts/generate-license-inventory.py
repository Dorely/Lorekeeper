"""Generate deterministic license evidence for the active locked spike environment."""

from __future__ import annotations

import hashlib
import importlib.metadata
import json
from pathlib import Path
import re
import tomllib


PROJECT_ROOT = Path(__file__).resolve().parent.parent
REPOSITORY_ROOT = PROJECT_ROOT.parent
OUTPUT_PATH = REPOSITORY_ROOT / "docs" / "research" / "weasyprint-spike-license-inventory.json"
LICENSE_NAME = re.compile(r"(^|/)(licen[cs]e|copying|notice|authors)([._/-]|$)", re.IGNORECASE)


def normalize(name: str) -> str:
    return re.sub(r"[-_.]+", "-", name).lower()


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


lock = tomllib.loads((PROJECT_ROOT / "uv.lock").read_text(encoding="utf-8"))
installed = {
    normalize(distribution.metadata["Name"]): distribution
    for distribution in importlib.metadata.distributions()
    if distribution.metadata.get("Name")
}

packages = []
for package in sorted(lock["package"], key=lambda item: (normalize(item["name"]), item["version"])):
    if package.get("source", {}).get("editable") == ".":
        continue
    distribution = installed.get(normalize(package["name"]))
    license_files = []
    if distribution is not None:
        for relative_path in sorted(distribution.files or (), key=lambda item: str(item).lower()):
            relative_name = str(relative_path).replace("\\", "/")
            if not LICENSE_NAME.search(relative_name):
                continue
            absolute_path = distribution.locate_file(relative_path)
            if not absolute_path.is_file():
                continue
            data = absolute_path.read_bytes()
            license_files.append(
                {
                    "path": relative_name,
                    "sha256": sha256(data),
                    "byteLength": len(data),
                }
            )
    metadata = distribution.metadata if distribution is not None else None
    source = package.get("source", {})
    sdist = package.get("sdist")
    packages.append(
        {
            "name": package["name"],
            "version": package["version"],
            "installedInWindowsFixture": distribution is not None,
            "source": source,
            "sdist": sdist,
            "licenseExpression": metadata.get("License-Expression") if metadata else None,
            "legacyLicense": metadata.get("License") if metadata else None,
            "projectUrl": metadata.get("Home-page") if metadata else None,
            "projectUrls": sorted(metadata.get_all("Project-URL") or []) if metadata else [],
            "licenseFiles": license_files,
        }
    )

inventory = {
    "schemaVersion": 1,
    "generatedFrom": "Lorekeeper.Press.Weasy/uv.lock",
    "scope": (
        "Locked Python packages and license files installed for the Windows x64 "
        "fallback fixture; conditional non-Windows packages remain listed but require "
        "target-native release inventory."
    ),
    "legalAdvice": False,
    "packages": packages,
    "assets": [
        {
            "name": "ISOcoated_v2_300_bas.ICC",
            "version": "icc-profiles-basiccolor-printing2009-1.2.0",
            "sha256": "b424c77f40c3423c925536f8ae08634985ccd0fe80eb253d5d229197ded7e886",
            "archiveSha256": "0d1ab5cb8a72ab76a02c67f07708e94a5794397eeac0acdb7503e2c11b515707",
            "license": "Zlib",
            "copyright": "Copyright (c) 2007-2010, basICColor GmbH",
            "redistributionStatus": "Accepted with preserved notice",
        }
    ],
    "openReleaseObligations": [
        "Inventory each native Pango, Fontconfig, HarfBuzz, GLib, Cairo, and transitively shipped library per target.",
        "Assemble and verify the distributable third-party notice/source-offer bundle.",
        "Repeat the inventory on Windows x64, macOS x64, and macOS arm64 final artifacts.",
    ],
}

OUTPUT_PATH.write_text(
    json.dumps(inventory, ensure_ascii=False, indent=2) + "\n",
    encoding="utf-8",
)
print(OUTPUT_PATH)
