from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from lorekeeper_press_weasy.startup import EXPECTED_FILES, validate_font_environment


class StartupValidationTests(unittest.TestCase):
    def test_missing_environment_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            executable = Path(directory) / "press.exe"
            errors = validate_font_environment(executable, {})

        self.assertTrue(any("FONTCONFIG_FILE is required" in error for error in errors))
        self.assertTrue(any("XDG_CACHE_HOME" in error for error in errors))

    def test_wrong_font_hash_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            distribution = Path(directory)
            executable = distribution / "press.exe"
            for relative_path in EXPECTED_FILES:
                path = distribution / relative_path
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"wrong")
            environment = {
                "FONTCONFIG_FILE": str(distribution / "fonts/fonts.conf"),
                "FONTCONFIG_PATH": str(distribution / "fonts"),
                "XDG_CACHE_HOME": str(distribution / "cache"),
                "FONTCONFIG_USE_MMAP": "0",
            }

            errors = validate_font_environment(executable, environment)

        self.assertTrue(any("unexpected SHA-256" in error for error in errors))
