from __future__ import annotations

import copy
import unittest

from lorekeeper_press_weasy.protocol import validate_request


VALID_REQUEST = {
    "protocolVersion": 1,
    "jobId": "fixture",
    "profile": "kdp-paperback-6x9-preview-v1",
    "document": {
        "title": "Fixture",
        "author": "Lorekeeper",
        "chapters": [{"title": "One", "body": "A paragraph."}],
    },
    "trim": {"widthInches": 6, "heightInches": 9},
    "cover": {
        "bleedInches": 0.125,
        "paperCaliperInchesPerPage": 0.0025,
        "backCopy": "Back copy.",
    },
}


class ProtocolTests(unittest.TestCase):
    def test_semantic_blocks_are_accepted(self) -> None:
        request = copy.deepcopy(VALID_REQUEST)
        request["document"]["language"] = "en"
        request["document"]["chapters"][0]["id"] = "11111111-1111-1111-1111-111111111111"
        request["document"]["chapters"][0]["blocks"] = [
            {"id": "22222222-2222-2222-2222-222222222222", "type": "Paragraph", "text": "Semantic prose."}
        ]

        parsed, diagnostics = validate_request(request)

        self.assertIsNotNone(parsed)
        self.assertEqual([], diagnostics)

    def test_unknown_semantic_block_fields_fail_closed(self) -> None:
        request = copy.deepcopy(VALID_REQUEST)
        request["document"]["chapters"][0]["blocks"] = [
            {"id": "22222222-2222-2222-2222-222222222222", "type": "Paragraph", "text": "Text.", "html": "<b>no</b>"}
        ]

        parsed, diagnostics = validate_request(request)

        self.assertIsNone(parsed)
        self.assertIn("PRESS_UNKNOWN_FIELD", [diagnostic.code for diagnostic in diagnostics])

    def test_valid_request_is_accepted(self) -> None:
        request, diagnostics = validate_request(copy.deepcopy(VALID_REQUEST))

        self.assertIsNotNone(request)
        self.assertEqual([], diagnostics)

    def test_unknown_fields_fail_closed(self) -> None:
        value = copy.deepcopy(VALID_REQUEST)
        value["untrusted"] = "ignored?"

        request, diagnostics = validate_request(value)

        self.assertIsNone(request)
        self.assertIn("PRESS_UNKNOWN_FIELD", {item.code for item in diagnostics})

    def test_job_id_cannot_escape_the_output_root(self) -> None:
        value = copy.deepcopy(VALID_REQUEST)
        value["jobId"] = "../escape"

        request, diagnostics = validate_request(value)

        self.assertIsNone(request)
        self.assertIn("PRESS_JOB_ID_INVALID", {item.code for item in diagnostics})

    def test_boolean_is_not_accepted_as_geometry(self) -> None:
        value = copy.deepcopy(VALID_REQUEST)
        value["trim"]["widthInches"] = True

        request, diagnostics = validate_request(value)

        self.assertIsNone(request)
        self.assertIn("PRESS_NUMBER_INVALID", {item.code for item in diagnostics})

    def test_named_profiles_are_restricted_to_six_by_nine(self) -> None:
        value = copy.deepcopy(VALID_REQUEST)
        value["trim"] = {"widthInches": 5.5, "heightInches": 8.5}

        request, diagnostics = validate_request(value)

        self.assertIsNone(request)
        self.assertIn("PRESS_PROFILE_TRIM_MISMATCH", {item.code for item in diagnostics})


if __name__ == "__main__":
    unittest.main()
