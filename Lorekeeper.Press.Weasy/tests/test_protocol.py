from __future__ import annotations

import copy
import base64
from io import BytesIO
import unittest

from PIL import Image
from lorekeeper_press_weasy.markup import interior_content, style_rules
from lorekeeper_press_weasy.protocol import validate_request


def block(block_id: str, text: str, marks: list[dict[str, str | None]] | None = None) -> dict:
    return {
        "id": block_id,
        "type": "Paragraph",
        "styleRole": "body",
        "headingLevel": None,
        "content": [{"type": "Text", "text": text, "marks": marks or []}],
    }


VALID_REQUEST = {
    "protocolVersion": 2,
    "jobId": "fixture",
    "profile": "kdp-paperback-6x9-preview-v1",
    "document": {
        "title": "Fixture",
        "author": "Lorekeeper",
        "sections": [
            {
                "id": None,
                "title": "Unassigned",
                "synopsis": "",
                "includePage": False,
                "includeHeading": False,
                "chapters": [
                    {
                        "id": "11111111-1111-1111-1111-111111111111",
                        "title": "One",
                        "synopsis": "",
                        "includeHeading": True,
                        "blocks": [block("22222222-2222-2222-2222-222222222222", "A paragraph.")],
                    }
                ],
            }
        ],
        "matter": [],
        "styles": [],
    },
    "trim": {"widthInches": 6, "heightInches": 9},
    "cover": {
        "bleedInches": 0.125,
        "paperCaliperInchesPerPage": 0.0025,
        "backCopy": "Back copy.",
    },
}


class ProtocolTests(unittest.TestCase):
    def test_generated_page_matter_kinds_are_rejected(self) -> None:
        for kind in ("TitlePage", "Copyright", "Contents"):
            with self.subTest(kind=kind):
                request = copy.deepcopy(VALID_REQUEST)
                request["document"]["matter"] = [
                    {
                        "id": "33333333-3333-3333-3333-333333333333",
                        "location": "Front",
                        "kind": kind,
                        "title": kind,
                        "blocks": [block("44444444-4444-4444-4444-444444444444", "Duplicate.")],
                    }
                ]

                parsed, diagnostics = validate_request(request)

                self.assertIsNone(parsed)
                self.assertTrue(any(item.code == "PRESS_MATTER_KIND_INVALID" for item in diagnostics))

    def test_semantic_blocks_are_accepted(self) -> None:
        request = copy.deepcopy(VALID_REQUEST)
        request["document"]["language"] = "en"
        request["document"]["sections"][0]["chapters"][0]["blocks"] = [
            block(
                "22222222-2222-2222-2222-222222222222",
                "Semantic prose.",
                [{"type": "Strong", "value": None}],
            )
        ]

        parsed, diagnostics = validate_request(request)

        self.assertIsNotNone(parsed)
        self.assertEqual([], diagnostics)

    def test_unknown_semantic_block_fields_fail_closed(self) -> None:
        request = copy.deepcopy(VALID_REQUEST)
        invalid = block("22222222-2222-2222-2222-222222222222", "Text.")
        invalid["html"] = "<b>no</b>"
        request["document"]["sections"][0]["chapters"][0]["blocks"] = [invalid]

        parsed, diagnostics = validate_request(request)

        self.assertIsNone(parsed)
        self.assertIn("PRESS_UNKNOWN_FIELD", [diagnostic.code for diagnostic in diagnostics])

    def test_valid_request_is_accepted(self) -> None:
        request, diagnostics = validate_request(copy.deepcopy(VALID_REQUEST))

        self.assertIsNotNone(request)
        self.assertEqual([], diagnostics)

    def test_ordered_semantic_matter_is_accepted(self) -> None:
        request = copy.deepcopy(VALID_REQUEST)
        request["document"]["matter"] = [
            {
                "id": "33333333-3333-3333-3333-333333333333",
                "location": "Back",
                "kind": "AboutAuthor",
                "title": "About the Author",
                "blocks": [block("44444444-4444-4444-4444-444444444444", "Biography.")],
            }
        ]

        parsed, diagnostics = validate_request(request)

        self.assertIsNotNone(parsed)
        self.assertEqual([], diagnostics)

    def test_front_and_back_matter_render_around_chapters(self) -> None:
        request = copy.deepcopy(VALID_REQUEST)
        request["document"]["matter"] = [
            {
                "id": "33333333-3333-3333-3333-333333333333",
                "location": "Front",
                "kind": "Epigraph",
                "title": "Epigraph",
                "blocks": [block("44444444-4444-4444-4444-444444444444", "Before")],
            },
            {
                "id": "55555555-5555-5555-5555-555555555555",
                "location": "Back",
                "kind": "Custom",
                "title": "Appendix",
                "blocks": [block("66666666-6666-6666-6666-666666666666", "After")],
            },
        ]

        rendered = interior_content(request["document"])

        self.assertLess(rendered.index("Before"), rendered.index('class="chapter"'))
        self.assertLess(rendered.index('class="chapter"'), rendered.index("After"))
        self.assertNotIn("lk-block-33333333", rendered)
        self.assertNotIn("lk-block-55555555", rendered)

    def test_sections_semantic_marks_and_edition_styles_affect_markup(self) -> None:
        request = copy.deepcopy(VALID_REQUEST)
        request["document"]["sections"][0].update(
            {
                "id": "77777777-7777-7777-7777-777777777777",
                "title": "Part One",
                "synopsis": "Part synopsis.",
                "includePage": True,
                "includeHeading": True,
            }
        )
        request["document"]["sections"][0]["chapters"][0]["includeHeading"] = False
        request["document"]["sections"][0]["chapters"][0]["blocks"] = [
            block(
                "22222222-2222-2222-2222-222222222222",
                "Styled",
                [{"type": "Strong", "value": None}],
            )
        ]
        request["document"]["styles"] = [
            {
                "name": "Body",
                "kind": "Paragraph",
                "semanticRole": "body",
                "definition": {
                    "fontFamilyKey": "serif",
                    "fontSizePoints": 12,
                    "fontWeight": None,
                    "italic": None,
                    "smallCaps": None,
                    "lineHeight": 1.5,
                    "spaceBeforePoints": None,
                    "spaceAfterPoints": 6,
                    "keepWithNext": None,
                    "textAlign": "justify",
                },
            }
        ]

        parsed, diagnostics = validate_request(request)
        rendered = interior_content(request["document"])
        css = style_rules(request["document"]["styles"])

        self.assertIsNotNone(parsed)
        self.assertEqual([], diagnostics)
        self.assertIn("Part One", rendered)
        self.assertIn("Part synopsis.", rendered)
        self.assertNotIn(">One</h1>", rendered)
        self.assertIn("<strong>Styled</strong>", rendered)
        self.assertIn('data-style-role="body"', rendered)
        self.assertIn('font-size: 12pt', css)

    def test_print_links_are_visible_without_pdf_annotations_and_hard_breaks_survive(self) -> None:
        request = copy.deepcopy(VALID_REQUEST)
        request["document"]["sections"][0]["chapters"][0]["blocks"] = [
            block(
                "22222222-2222-2222-2222-222222222222",
                "Line one\nLine two",
                [{"type": "Link", "value": "https://example.com"}],
            )
        ]

        rendered = interior_content(request["document"])

        self.assertIn('class="print-link"', rendered)
        self.assertIn('data-link-target="https://example.com"', rendered)
        self.assertNotIn("<a ", rendered)
        self.assertIn("Line one<br />Line two", rendered)

    def test_visible_toc_preserves_act_hierarchy(self) -> None:
        request = copy.deepcopy(VALID_REQUEST)
        request["document"]["includeVisibleTableOfContents"] = True
        request["document"]["sections"][0]["title"] = "Act One"
        request["document"]["sections"][0]["includePage"] = True

        rendered = interior_content(request["document"])

        self.assertIn("<li>Act One<ol><li>One</li></ol></li>", rendered)

    def test_cover_image_requires_a_decodable_bounded_png(self) -> None:
        request = copy.deepcopy(VALID_REQUEST)
        buffer = BytesIO()
        Image.new("RGBA", (1, 1), (255, 0, 0, 255)).save(buffer, format="PNG")
        png = buffer.getvalue()
        request["cover"]["imageDataUri"] = (
            "data:image/png;base64," + base64.b64encode(png).decode()
        )
        request["cover"]["imageFocalXPercent"] = 25
        request["cover"]["imageFocalYPercent"] = 75

        parsed, diagnostics = validate_request(request)

        self.assertIsNotNone(parsed)
        self.assertEqual([], diagnostics)

        hostile = copy.deepcopy(request)
        hostile["cover"]["imageDataUri"] = "https://example.com/cover.png"
        parsed, diagnostics = validate_request(hostile)
        self.assertIsNone(parsed)
        self.assertIn("PRESS_COVER_IMAGE_INVALID", [item.code for item in diagnostics])

        bomb = copy.deepcopy(request)
        header = bytearray(png)
        header[16:20] = (5000).to_bytes(4, "big")
        header[20:24] = (5000).to_bytes(4, "big")
        bomb["cover"]["imageDataUri"] = (
            "data:image/png;base64," + base64.b64encode(header).decode()
        )
        parsed, diagnostics = validate_request(bomb)
        self.assertIsNone(parsed)
        self.assertIn("PRESS_COVER_IMAGE_INVALID", [item.code for item in diagnostics])

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
