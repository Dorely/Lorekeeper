from __future__ import annotations

import unittest

from lorekeeper_press_weasy.geometry import geometry_errors
from lorekeeper_press_weasy.inspect import PdfInspection


REQUEST = {
    "trim": {"widthInches": 6.0, "heightInches": 9.0},
    "cover": {"bleedInches": 0.125},
}


def inspection(width: float, height: float) -> PdfInspection:
    box = [0.0, 0.0, width, height]
    return PdfInspection(
        pdf_version="1.3",
        page_count=1,
        page_boxes={
            "mediaBox": box,
            "cropBox": None,
            "bleedBox": box,
            "trimBox": box,
            "artBox": None,
        },
        page_boxes_consistent=True,
    )


class GeometryTests(unittest.TestCase):
    def test_expected_measured_geometry_passes(self) -> None:
        errors = geometry_errors(
            REQUEST,
            inspection(432.0, 648.0),
            inspection(882.18, 666.0),
            0.0025,
        )

        self.assertEqual([], errors)

    def test_uniformly_wrong_artifact_geometry_fails(self) -> None:
        errors = geometry_errors(
            REQUEST,
            inspection(431.0, 648.0),
            inspection(882.18, 665.0),
            0.0025,
        )

        self.assertTrue(any("Interior mediaBox" in error for error in errors))
        self.assertTrue(any("Cover mediaBox" in error for error in errors))


if __name__ == "__main__":
    unittest.main()
