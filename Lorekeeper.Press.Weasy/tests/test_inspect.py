from __future__ import annotations

from io import BytesIO
import unittest

from pypdf import PdfWriter
from pypdf.generic import (
    ArrayObject,
    DictionaryObject,
    DecodedStreamObject,
    NameObject,
    NumberObject,
)

from lorekeeper_press_weasy.inspect import inspect_pdf, pdfx_2001_errors


class PdfInspectionTests(unittest.TestCase):
    def test_plain_pdf_cannot_pass_pdfx_checks(self) -> None:
        writer = PdfWriter()
        writer.add_blank_page(width=432, height=648)
        stream = BytesIO()
        writer.write(stream)

        inspection = inspect_pdf(stream.getvalue())
        errors = pdfx_2001_errors(inspection)

        self.assertTrue(errors)
        self.assertTrue(any("GTS_PDFXVersion" in error for error in errors))
        self.assertTrue(any("output intent" in error for error in errors))

    def test_empty_pdf_is_rejected(self) -> None:
        writer = PdfWriter()
        stream = BytesIO()
        writer.write(stream)

        with self.assertRaisesRegex(ValueError, "no pages"):
            inspect_pdf(stream.getvalue())

    def test_operator_names_inside_text_are_not_treated_as_colors(self) -> None:
        writer = PdfWriter()
        page = writer.add_blank_page(width=432, height=648)
        content = DecodedStreamObject()
        content.set_data(b"BT (rg RG k K g G) Tj ET")
        page[NameObject("/Contents")] = writer._add_object(content)
        stream = BytesIO()
        writer.write(stream)

        inspection = inspect_pdf(stream.getvalue())

        self.assertEqual(set(), inspection.color_spaces)

    def test_device_rgb_operator_is_detected(self) -> None:
        writer = PdfWriter()
        page = writer.add_blank_page(width=432, height=648)
        content = DecodedStreamObject()
        content.set_data(b"1 0 0 rg 0 0 10 10 re f")
        page[NameObject("/Contents")] = writer._add_object(content)
        stream = BytesIO()
        writer.write(stream)

        inspection = inspect_pdf(stream.getvalue())

        self.assertIn("DeviceRGB", inspection.color_spaces)

    def test_rgb_icc_based_color_space_is_rejected(self) -> None:
        writer = PdfWriter()
        page = writer.add_blank_page(width=432, height=648)
        profile = DecodedStreamObject()
        profile[NameObject("/N")] = NumberObject(3)
        profile.set_data(b"not-a-real-profile")
        page[NameObject("/Resources")] = DictionaryObject(
            {
                NameObject("/ColorSpace"): DictionaryObject(
                    {
                        NameObject("/CS1"): ArrayObject(
                            [
                                NameObject("/ICCBased"),
                                writer._add_object(profile),
                            ]
                        )
                    }
                )
            }
        )
        stream = BytesIO()
        writer.write(stream)

        inspection = inspect_pdf(stream.getvalue())
        errors = pdfx_2001_errors(inspection)

        self.assertEqual([3], inspection.icc_profile_components)
        self.assertTrue(any("forbidden component counts" in error for error in errors))

    def test_non_normal_blend_mode_is_transparency(self) -> None:
        writer = PdfWriter()
        page = writer.add_blank_page(width=432, height=648)
        graphics_state = DictionaryObject(
            {
                NameObject("/Type"): NameObject("/ExtGState"),
                NameObject("/BM"): NameObject("/Multiply"),
            }
        )
        page[NameObject("/Resources")] = DictionaryObject(
            {
                NameObject("/ExtGState"): DictionaryObject(
                    {NameObject("/GS1"): writer._add_object(graphics_state)}
                )
            }
        )
        content = DecodedStreamObject()
        content.set_data(b"/GS1 gs 0 0 10 10 re f")
        page[NameObject("/Contents")] = writer._add_object(content)
        stream = BytesIO()
        writer.write(stream)

        inspection = inspect_pdf(stream.getvalue())

        self.assertTrue(inspection.has_transparency)

    def test_image_soft_mask_is_transparency(self) -> None:
        writer = PdfWriter()
        page = writer.add_blank_page(width=432, height=648)
        soft_mask = DecodedStreamObject()
        soft_mask.update(
            {
                NameObject("/Type"): NameObject("/XObject"),
                NameObject("/Subtype"): NameObject("/Image"),
                NameObject("/Width"): NumberObject(1),
                NameObject("/Height"): NumberObject(1),
                NameObject("/ColorSpace"): NameObject("/DeviceGray"),
                NameObject("/BitsPerComponent"): NumberObject(8),
            }
        )
        soft_mask.set_data(b"\xff")
        image = DecodedStreamObject()
        image.update(
            {
                NameObject("/Type"): NameObject("/XObject"),
                NameObject("/Subtype"): NameObject("/Image"),
                NameObject("/Width"): NumberObject(1),
                NameObject("/Height"): NumberObject(1),
                NameObject("/ColorSpace"): NameObject("/DeviceRGB"),
                NameObject("/BitsPerComponent"): NumberObject(8),
                NameObject("/SMask"): writer._add_object(soft_mask),
            }
        )
        image.set_data(b"\x00\x00\x00")
        page[NameObject("/Resources")] = DictionaryObject(
            {
                NameObject("/XObject"): DictionaryObject(
                    {NameObject("/Image1"): writer._add_object(image)}
                )
            }
        )
        content = DecodedStreamObject()
        content.set_data(b"q 1 0 0 1 0 0 cm /Image1 Do Q")
        page[NameObject("/Contents")] = writer._add_object(content)
        stream = BytesIO()
        writer.write(stream)

        inspection = inspect_pdf(stream.getvalue())

        self.assertTrue(inspection.has_transparency)

    def test_nested_form_transparency_group_is_detected(self) -> None:
        writer = PdfWriter()
        page = writer.add_blank_page(width=432, height=648)
        form = DecodedStreamObject()
        form.update(
            {
                NameObject("/Type"): NameObject("/XObject"),
                NameObject("/Subtype"): NameObject("/Form"),
                NameObject("/BBox"): ArrayObject(
                    [NumberObject(0), NumberObject(0), NumberObject(10), NumberObject(10)]
                ),
                NameObject("/Group"): DictionaryObject(
                    {NameObject("/S"): NameObject("/Transparency")}
                ),
                NameObject("/Resources"): DictionaryObject(),
            }
        )
        form.set_data(b"0 0 10 10 re f")
        page[NameObject("/Resources")] = DictionaryObject(
            {
                NameObject("/XObject"): DictionaryObject(
                    {NameObject("/Form1"): writer._add_object(form)}
                )
            }
        )
        content = DecodedStreamObject()
        content.set_data(b"/Form1 Do")
        page[NameObject("/Contents")] = writer._add_object(content)
        stream = BytesIO()
        writer.write(stream)

        inspection = inspect_pdf(stream.getvalue())

        self.assertTrue(inspection.has_transparency)


if __name__ == "__main__":
    unittest.main()
