from __future__ import annotations

import base64
import copy
from io import BytesIO, StringIO, TextIOWrapper
import importlib
import json
import sys
import types
import unittest
from unittest.mock import patch

from PIL import Image

from lorekeeper_press_weasy.protocol import response, validate_request
from test_protocol import VALID_REQUEST

render_stub = types.ModuleType("lorekeeper_press_weasy.render")
render_stub.render_request = lambda *_arguments: None
with patch.dict(sys.modules, {"lorekeeper_press_weasy.render": render_stub}):
    entry = importlib.import_module("lorekeeper_press_weasy.main")


class MainTests(unittest.TestCase):
    def test_stdin_accepts_maximum_cover_with_sizable_validated_manuscript(self) -> None:
        request = copy.deepcopy(VALID_REQUEST)
        image_buffer = BytesIO()
        Image.new("RGB", (1, 1), (255, 0, 0)).save(image_buffer, format="PNG")
        png = image_buffer.getvalue()
        png += b"\0" * (20_000_000 - len(png))
        request["cover"]["imageDataUri"] = (
            "data:image/png;base64," + base64.b64encode(png).decode()
        )
        request["document"]["sections"][0]["chapters"][0]["blocks"][0]["content"][0][
            "text"
        ] = "M" * 2_000_000
        encoded = json.dumps(request, ensure_ascii=False, separators=(",", ":")).encode()
        stdin = TextIOWrapper(BytesIO(encoded), encoding="utf-8")
        stdout = StringIO()

        def validate_without_render(value, _output_root, _cmyk_profile):
            parsed, diagnostics = validate_request(value)
            return response(
                value.get("jobId", ""),
                "completed" if parsed is not None else "failed",
                diagnostics,
            )

        with (
            patch.object(sys, "stdin", stdin),
            patch.object(sys, "stdout", stdout),
            patch.object(entry, "render_request", side_effect=validate_without_render),
            patch.object(sys, "argv", ["lorekeeper-press-weasy", "--output-root", "."]),
        ):
            result_code = entry.main()

        self.assertEqual(0, result_code)
        result = json.loads(stdout.getvalue())
        self.assertEqual("completed", result["status"])
        self.assertLess(len(encoded), entry.MAX_REQUEST_BYTES)

    def test_unsupported_protocol_diagnostic_reports_current_version(self) -> None:
        request = copy.deepcopy(VALID_REQUEST)
        request["protocolVersion"] = 1

        parsed, diagnostics = validate_request(request)

        self.assertIsNone(parsed)
        self.assertIn(
            "Only protocol version 2 is supported.",
            [diagnostic.message for diagnostic in diagnostics],
        )


if __name__ == "__main__":
    unittest.main()
