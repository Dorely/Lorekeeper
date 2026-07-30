"""Pre-native-import validation for the frozen font distribution."""

from __future__ import annotations

import hashlib
from collections.abc import Mapping
from pathlib import Path


EXPECTED_FILES = {
    Path("fonts/fonts.conf"): "822f71fffd907de7a445c93c5ea00148e3561107bd1ae390081fee4cb6ea2d06",
    Path("fonts/LiberationSerif-Regular.ttf"): (
        "a82e5f4350d6b66d44d1c77f718d7c658a23ae6097c115748f5ab58c0723af59"
    ),
    Path("fonts/LiberationSerif-Bold.ttf"): (
        "1226cecb60336d5ab5401c8ecb608cd93fe2576df85ec1f2410eac524878323c"
    ),
    Path("licenses/Liberation-Fonts-LICENSE.txt"): (
        "93fed46019c38bbe566b479d22148e2e8a1e85ada614accb0211c37b2c61c19b"
    ),
}


def validate_font_environment(
    executable_path: Path,
    environment: Mapping[str, str],
) -> list[str]:
    distribution = executable_path.resolve().parent
    expected_font_directory = (distribution / "fonts").resolve()
    expected_config = (expected_font_directory / "fonts.conf").resolve()
    errors: list[str] = []

    _require_exact_path(environment, "FONTCONFIG_FILE", expected_config, errors)
    _require_exact_path(environment, "FONTCONFIG_PATH", expected_font_directory, errors)
    if not environment.get("XDG_CACHE_HOME", "").strip():
        errors.append("XDG_CACHE_HOME must identify a writable job-scoped cache.")
    if environment.get("FONTCONFIG_USE_MMAP") != "0":
        errors.append("FONTCONFIG_USE_MMAP must be 0.")

    for relative_path, expected_hash in EXPECTED_FILES.items():
        path = distribution / relative_path
        try:
            actual_hash = _sha256(path)
        except OSError as exception:
            errors.append(f"{relative_path.as_posix()} could not be read: {exception}")
            continue
        if actual_hash != expected_hash:
            errors.append(f"{relative_path.as_posix()} has an unexpected SHA-256 fingerprint.")
    return errors


def _require_exact_path(
    environment: Mapping[str, str],
    name: str,
    expected: Path,
    errors: list[str],
) -> None:
    value = environment.get(name, "").strip()
    if not value:
        errors.append(f"{name} is required.")
        return
    try:
        actual = Path(value).resolve()
    except OSError as exception:
        errors.append(f"{name} could not be resolved: {exception}")
        return
    if actual != expected:
        errors.append(f"{name} must resolve to the controlled sibling font distribution.")


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()
