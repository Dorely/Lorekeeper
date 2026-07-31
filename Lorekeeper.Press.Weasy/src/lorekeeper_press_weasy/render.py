"""Deterministic, contained WeasyPrint press fixture renderer."""

from __future__ import annotations

import base64
import hashlib
import html
import logging
from pathlib import Path
from typing import Any

from weasyprint import HTML
from weasyprint.logger import LOGGER

from .geometry import POINTS_PER_INCH, geometry_errors, measured_dimensions
from .inspect import PdfInspection, inspect_pdf, pdfx_2001_errors
from .markup import interior_content, style_rules
from .pdfx import DECLARED_STANDARD, PROFILE_NAME, register_pdfx_2001_profile
from .protocol import (
    INGRAM_PROFILE,
    Diagnostic,
    RenderEvidence,
    response,
    validate_request,
)
from .storage import publish_job


MAX_PROFILE_BYTES = 8 * 1024 * 1024
EXPECTED_PROFILE_SHA256 = "b424c77f40c3423c925536f8ae08634985ccd0fe80eb253d5d229197ded7e886"


class WarningCapture(logging.Handler):
    def __init__(self) -> None:
        super().__init__(logging.WARNING)
        self.messages: list[str] = []

    def emit(self, record: logging.LogRecord) -> None:
        self.messages.append(self.format(record))


def render_request(
    value: Any,
    output_root: Path,
    profile_path: Path | None,
) -> dict[str, Any]:
    request, diagnostics = validate_request(value)
    job_id = value.get("jobId", "") if isinstance(value, dict) else ""
    if request is None:
        return response(job_id if isinstance(job_id, str) else "", "rejected", diagnostics)

    profile_data: bytes | None = None
    if request["profile"] == INGRAM_PROFILE:
        if profile_path is None:
            return response(
                request["jobId"],
                "rejected",
                [
                    Diagnostic(
                        "error",
                        "PRESS_CMYK_PROFILE_REQUIRED",
                        "The Ingram-named profile requires the fingerprinted CMYK profile.",
                    )
                ],
            )
        profile_data, profile_diagnostic = _read_profile(profile_path)
        if profile_diagnostic is not None:
            return response(request["jobId"], "rejected", [profile_diagnostic])
        assert profile_data is not None

    try:
        interior_html = _interior_html(request, profile_path)
        interior_data, interior_warnings, page_map = _compile(
            interior_html,
            profile_path,
            profile_data,
            request["profile"],
        )
        if interior_warnings:
            raise ValueError("WeasyPrint warnings: " + " | ".join(interior_warnings))
        interior_inspection = inspect_pdf(interior_data)

        page_count = interior_inspection.page_count
        cover_html, spine_width = _cover_html(request, profile_path, page_count)
        cover_data, cover_warnings, _ = _compile(
            cover_html,
            profile_path,
            profile_data,
            request["profile"],
            request["cover"].get("imageDataUri") or None,
            validate_cover_layout=True,
        )
        cover_layout_errors = [
            warning.removeprefix("COVER_LAYOUT: ")
            for warning in cover_warnings
            if warning.startswith("COVER_LAYOUT: ")
        ]
        if cover_layout_errors:
            return response(
                request["jobId"],
                "rejected",
                [
                    Diagnostic("error", "PRESS_COVER_COPY_OVERFLOW", message, "cover-pdf")
                    for message in cover_layout_errors
                ],
            )
        if cover_warnings:
            raise ValueError("WeasyPrint warnings: " + " | ".join(cover_warnings))
        cover_inspection = inspect_pdf(cover_data)
        if cover_inspection.page_count != 1:
            raise ValueError("The full-wrap cover must render as exactly one PDF page.")

        artifact_geometry_errors = geometry_errors(
            request,
            interior_inspection,
            cover_inspection,
            spine_width,
        )
        if artifact_geometry_errors:
            return response(
                request["jobId"],
                "rejected",
                [
                    Diagnostic("error", "PRESS_ARTIFACT_GEOMETRY_REJECTED", message)
                    for message in artifact_geometry_errors
                ],
            )

        pdfx_errors: list[str] = []
        if request["profile"] == INGRAM_PROFILE:
            pdfx_errors.extend(pdfx_2001_errors(interior_inspection))
            pdfx_errors.extend(f"Cover: {error}" for error in pdfx_2001_errors(cover_inspection))
        if pdfx_errors:
            return response(
                request["jobId"],
                "rejected",
                [
                    Diagnostic("error", "PRESS_INTERNAL_PDFX_REJECTED", message)
                    for message in pdfx_errors
                ],
            )

        job_directory = publish_job(
            output_root,
            request["jobId"],
            {"interior.pdf": interior_data, "cover.pdf": cover_data},
        )
        interior_path = job_directory / "interior.pdf"
        cover_path = job_directory / "cover.pdf"

        evidence = _evidence(request, interior_inspection, cover_inspection, spine_width)
        completion_diagnostics = [
            Diagnostic(
                "warning",
                "PRESS_EXTERNAL_VALIDATION_REQUIRED",
                "The PDF declares PDF/X-1a:2001 and passed Lorekeeper's structural checks, "
                "but no independent Acrobat/vendor validation has been recorded.",
            )
        ] if request["profile"] == INGRAM_PROFILE else [
            Diagnostic(
                "warning",
                "PRESS_VENDOR_PREFLIGHT_NOT_RUN",
                "The KDP-named fixture has not been uploaded to a vendor preflight.",
            )
        ]
        artifacts = [
            _artifact("interior-pdf", request["jobId"], interior_path, interior_data, interior_inspection.page_count),
            _artifact("cover-pdf", request["jobId"], cover_path, cover_data, cover_inspection.page_count),
        ]
        return response(
            request["jobId"],
            "completed",
            completion_diagnostics,
            artifacts=artifacts,
            evidence=evidence,
            page_map=page_map,
        )
    except Exception as exception:
        return response(
            request["jobId"],
            "failed",
            [Diagnostic("error", "PRESS_RENDER_FAILED", str(exception))],
        )


def _compile(
    source: str,
    profile_path: Path | None,
    profile_data: bytes | None,
    press_profile: str,
    allowed_image_uri: str | None = None,
    validate_cover_layout: bool = False,
) -> tuple[bytes, list[str], list[dict[str, Any]]]:
    register_pdfx_2001_profile()
    profile_uri = profile_path.resolve().as_uri() if profile_path is not None else None

    def fetch(url: str, *_args: Any, **_kwargs: Any) -> dict[str, Any]:
        if allowed_image_uri is not None and url == allowed_image_uri:
            return {
                "string": base64.b64decode(allowed_image_uri.partition(",")[2], validate=True),
                "mime_type": "image/png",
                "redirected_url": allowed_image_uri,
            }
        if profile_uri is None or profile_data is None or url != profile_uri:
            raise ValueError(f"External resource access is blocked: {url}")
        return {
            "string": profile_data,
            "mime_type": "application/vnd.iccprofile",
            "redirected_url": profile_uri,
        }

    capture = WarningCapture()
    LOGGER.addHandler(capture)
    try:
        options: dict[str, Any] = {
            "full_fonts": True,
            "presentational_hints": False,
            "pdf_identifier": b"lorekeeper-press-preview-v1",
        }
        if press_profile == INGRAM_PROFILE:
            options.update(
                pdf_variant=PROFILE_NAME,
                output_intent="--lorekeeper-press",
                pdf_version="1.3",
            )
        else:
            options.update(pdf_version="1.7", output_intent="srgb")
        document = HTML(string=source, url_fetcher=fetch).render()
        if validate_cover_layout:
            capture.messages.extend(
                f"COVER_LAYOUT: {message}"
                for message in _cover_layout_errors(document)
            )
        page_map = []
        for page_number, page in enumerate(document.pages, start=1):
            for anchor in page.anchors:
                if not anchor.startswith("lk-block-"):
                    continue
                encoded = anchor.removeprefix("lk-block-")
                if len(encoded) == 64:
                    page_map.append(
                        {
                            "chapterId": encoded[:32],
                            "blockId": encoded[32:],
                            "pageNumber": page_number,
                        }
                    )
        data = document.write_pdf(**options)
        return data, capture.messages, page_map
    finally:
        LOGGER.removeHandler(capture)


def _cover_layout_errors(document: Any) -> list[str]:
    errors: list[str] = []
    if len(document.pages) != 1:
        return ["Cover copy does not fit on the single full-wrap cover page."]
    descendants = list(document.pages[0]._page_box.descendants())

    def class_box(class_name: str) -> Any | None:
        return next(
            (
                box
                for box in descendants
                if getattr(box, "element", None) is not None
                and class_name in (box.element.get("class") or "").split()
            ),
            None,
        )

    back_copy = class_box("back-copy")
    barcode = class_box("barcode")
    if back_copy is None or barcode is None:
        return errors
    copy_descendants = list(back_copy.descendants())
    content_bottom = max(
        (
            box.position_y + box.height
            for box in copy_descendants
            if getattr(box, "element_tag", None) in {"p", "div"}
        ),
        default=back_copy.position_y + back_copy.height,
    )
    if content_bottom > barcode.position_y - 1:
        errors.append(
            "Back-cover copy enters the protected barcode reserve; shorten the copy or reduce its typography."
        )
    return errors


def _interior_html(request: dict[str, Any], profile_path: Path | None) -> str:
    document = request["document"]
    return _html_document(
        request,
        profile_path,
        f"""
        @page {{
          size: {request["trim"]["widthInches"]}in {request["trim"]["heightInches"]}in;
          margin: {request["trim"].get("marginInches", 0.75)}in;
          @bottom-center {{ content: counter(page); font-family: "Liberation Serif", serif; font-size: 9pt; }}
        }}
        @page :left {{ margin-left: {request["trim"].get("marginInches", 0.75)}in; margin-right: {request["trim"].get("marginInches", 0.75)}in; }}
        @page :right {{ margin-left: {request["trim"].get("marginInches", 0.75)}in; margin-right: {request["trim"].get("marginInches", 0.75)}in; }}
        @page :left {{ @top-center {{ content: string(chapter-title); font-size: 8.5pt; }} }}
        @page :right {{ @top-center {{ content: "{_css_string(document["title"])}"; font-size: 8.5pt; }} }}
        @page front {{ @bottom-center {{ content: counter(page, lower-roman); }} }}
        @page chapter:first {{ @top-center {{ content: none; }} }}
        body {{ font-family: "Liberation Serif", serif; font-size: {request["trim"].get("bodyFontSizePoints", 11)}pt; line-height: {request["trim"].get("bodyLineHeight", 1.32)}; }}
        .part {{ page: chapter; break-before: right; }}
        .chapter {{ page: chapter; break-before: right; }}
        .front {{ page: front; break-after: page; }}
        .matter {{ break-before: page; break-after: page; }}
        .back {{ page: chapter; }}
        .title-page {{ align-items: center; display: flex; flex-direction: column; justify-content: center; text-align: center; }}
        .title-page h1 {{ font-size: 28pt; margin-bottom: 0.2in; }}
        .copyright-page {{ display: flex; flex-direction: column; justify-content: end; font-size: 9pt; }}
        .dedication {{ display: flex; align-items: center; justify-content: center; text-align: center; }}
        .contents li {{ margin-bottom: 0.12in; }}
        .underline {{ text-decoration: underline; }}
        .small-caps {{ font-variant-caps: small-caps; }}
        .print-link {{ text-decoration: underline; }}
        h1 {{ string-set: chapter-title content(); text-align: center; margin: 1.25in 0 0.55in; }}
        p {{ margin: 0; text-align: justify; text-indent: 1.25em; hyphens: auto; orphans: 3; widows: 3; }}
        h1 + p {{ text-indent: 0; }}
        """ + style_rules(document["styles"]),
        interior_content(document),
    )


def _cover_html(
    request: dict[str, Any],
    profile_path: Path | None,
    page_count: int,
) -> tuple[str, float]:
    trim = request["trim"]
    cover = request["cover"]
    spine_width = page_count * cover["paperCaliperInchesPerPage"]
    spine_text = cover.get("spineText", "") if spine_width >= 0.24 else ""
    width = trim["widthInches"] * 2 + spine_width + cover["bleedInches"] * 2
    height = trim["heightInches"] + cover["bleedInches"] * 2
    image = (
        f'<img class="front-image" src="{html.escape(cover["imageDataUri"], quote=True)}" alt="" />'
        if cover.get("imageDataUri")
        else ""
    )
    barcode = _barcode_html(cover, request["profile"] == INGRAM_PROFILE)
    back_class = "back has-barcode" if barcode else "back"
    body = f"""
      <main class="cover">
        <section class="{back_class}"><div class="back-copy">{_paragraph_html(cover["backCopy"])}</div>{barcode}</section>
        <section class="spine"><span>{html.escape(spine_text)}</span></section>
        <section class="front">
          {image}
          <div class="front-copy">
            <h1>{html.escape(cover.get("title") or request["document"]["title"])}</h1>
            <h2>{html.escape(cover.get("subtitle", ""))}</h2>
            <p>{html.escape(cover.get("author") or request["document"]["author"])}</p>
          </div>
        </section>
      </main>
    """
    if request["profile"] == INGRAM_PROFILE:
        panel_color = _hex_to_device_cmyk(cover.get("backgroundColor", "#5c7ca5"))
        spine_color = panel_color
        text_color = "device-cmyk(0 0 0 0)"
        barcode_black = "device-cmyk(0 0 0 1)"
        barcode_white = "device-cmyk(0 0 0 0)"
        copy_background = "device-cmyk(0 0 0 1)"
    else:
        panel_color = cover.get("backgroundColor", "#5c7ca5")
        spine_color = panel_color
        text_color = "#ffffff"
        barcode_black = "#000000"
        barcode_white = "#ffffff"
        copy_background = "#000000"
    css = f"""
      @page {{ size: {width}in {height}in; margin: 0; }}
      html, body {{ margin: 0; width: 100%; height: 100%; font-family: "Liberation Serif", serif; }}
      .cover {{ display: grid; grid-template-columns: {trim["widthInches"] + cover["bleedInches"]}in {spine_width}in {trim["widthInches"] + cover["bleedInches"]}in; width: 100%; height: 100%; }}
      .back, .front {{ box-sizing: border-box; padding: 0.75in; background: {panel_color}; color: {text_color}; }}
      .spine {{ background: {spine_color}; }}
      .spine {{ align-items: center; color: {text_color}; display: flex; justify-content: center; overflow: hidden; }}
      .spine span {{ font-size: 9pt; transform: rotate(90deg); white-space: nowrap; }}
      .front {{ display: flex; flex-direction: column; justify-content: center; overflow: hidden; position: relative; text-align: center; }}
      .front-image {{ height: 100%; inset: 0; object-fit: cover; object-position: {cover.get("imageFocalXPercent", 50)}% {cover.get("imageFocalYPercent", 50)}%; position: absolute; width: 100%; }}
      .front-copy {{ background: {copy_background}; box-sizing: border-box; padding: 0.25in; position: relative; z-index: 1; }}
      .front h1 {{ font-size: 28pt; }}
      .back {{ display: flex; align-items: center; font-size: 12pt; line-height: 1.4; }}
      .back.has-barcode {{ align-items: stretch; display: grid; grid-template-rows: minmax(0, 1fr) 1.575in; }}
      .back.has-barcode .back-copy {{ align-self: center; grid-row: 1; }}
      .back-copy p {{ margin: 0 0 0.8em; text-indent: 0; }}
      .barcode {{ align-self: end; background: {barcode_white}; box-sizing: border-box; color: {barcode_black}; font: 8pt sans-serif; grid-row: 2; height: 1.2in; justify-self: end; padding: 0.12in; text-align: center; width: 2in; }}
      .barcode svg {{ display: block; height: 0.82in; width: 100%; }}
    """
    return _html_document(request, profile_path, css, body), spine_width


def _hex_to_device_cmyk(value: str) -> str:
    if len(value) != 7 or not value.startswith("#"):
        return "device-cmyk(0.55 0.35 0 0.05)"
    red, green, blue = (int(value[index:index + 2], 16) / 255 for index in (1, 3, 5))
    black = 1 - max(red, green, blue)
    if black >= 0.999999:
        return "device-cmyk(0 0 0 1)"
    cyan = (1 - red - black) / (1 - black)
    magenta = (1 - green - black) / (1 - black)
    yellow = (1 - blue - black) / (1 - black)
    return f"device-cmyk({cyan:.6f} {magenta:.6f} {yellow:.6f} {black:.6f})"


def _css_string(value: str) -> str:
    return (
        value.replace("\\", "\\\\")
        .replace('"', '\\"')
        .replace("\r\n", "\\A ")
        .replace("\r", "\\A ")
        .replace("\n", "\\A ")
    )


def _barcode_html(cover: dict[str, Any], cmyk: bool) -> str:
    digits = "".join(character for character in cover.get("isbn", "") if character.isdigit())
    if cover.get("barcodeMode") == "VendorOverlay":
        return '<div class="barcode" aria-hidden="true"></div>'
    if len(digits) != 13:
        return ""
    left_odd = ("0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011")
    left_even = ("0100111", "0110011", "0011011", "0100001", "0011101", "0111001", "0000101", "0010001", "0001001", "0010111")
    right = ("1110010", "1100110", "1101100", "1000010", "1011100", "1001110", "1010000", "1000100", "1001000", "1110100")
    parity = ("OOOOOO", "OOEOEE", "OOEEOE", "OOEEEO", "OEOOEE", "OEEOOE", "OEEEOO", "OEOEOE", "OEOEEO", "OEEOEO")
    bits = "101"
    for index, digit in enumerate(digits[1:7]):
        bits += (left_odd if parity[int(digits[0])][index] == "O" else left_even)[int(digit)]
    bits += "01010"
    bits += "".join(right[int(digit)] for digit in digits[7:])
    bits += "101"
    black = "device-cmyk(0 0 0 1)" if cmyk else "#000000"
    white = "device-cmyk(0 0 0 0)" if cmyk else "#ffffff"
    bars = "".join(
        f'<rect x="{index}" y="0" width="1" height="72" fill="{black}"/>'
        for index, bit in enumerate(bits)
        if bit == "1"
    )
    svg = f'<svg viewBox="-11 0 113 82" role="img" aria-label="ISBN {digits} barcode"><rect x="-11" width="113" height="82" fill="{white}"/>{bars}</svg>'
    return f'<div class="barcode">{svg}<span>{html.escape(digits)}</span></div>'


def _paragraph_html(value: str) -> str:
    normalized = value.replace("\r\n", "\n").replace("\r", "\n")
    return "".join(
        f"<p>{html.escape(paragraph).replace(chr(10), '<br />')}</p>"
        for paragraph in normalized.split("\n\n")
        if paragraph
    )


def _html_document(
    request: dict[str, Any],
    profile_path: Path | None,
    css: str,
    body: str,
) -> str:
    if request["profile"] == INGRAM_PROFILE:
        if profile_path is None:
            raise ValueError("The CMYK profile path is required for press HTML.")
        profile_uri = profile_path.resolve().as_uri()
        press_css = f"""
      @color-profile --lorekeeper-press {{
        src: url("{profile_uri}");
        components: c, m, y, k;
      }}
      html, body {{ color: device-cmyk(0 0 0 1); background: device-cmyk(0 0 0 0); }}
    """
    else:
        press_css = """
      html, body { color: #000000; background: #ffffff; }
    """
    return f"""<!doctype html>
<html lang="{html.escape(request["document"].get("language", "en"), quote=True)}">
<head>
  <meta charset="utf-8">
  <meta name="author" content="{html.escape(request["document"]["author"], quote=True)}">
  <meta name="dcterms.created" content="2000-01-01T00:00:00Z">
  <meta name="dcterms.modified" content="2000-01-01T00:00:00Z">
  <title>{html.escape(request["document"]["title"])}</title>
  <style>{press_css}{css}</style>
</head>
<body>{body}</body>
</html>"""


def _read_profile(profile_path: Path) -> tuple[bytes | None, Diagnostic | None]:
    try:
        with profile_path.open("rb") as stream:
            profile_data = stream.read(MAX_PROFILE_BYTES + 1)
        if len(profile_data) > MAX_PROFILE_BYTES:
            raise ValueError("The CMYK profile exceeds 8 MiB.")
        digest = hashlib.sha256(profile_data).hexdigest()
        if digest != EXPECTED_PROFILE_SHA256:
            raise ValueError(
                f"The CMYK profile hash is {digest}; expected {EXPECTED_PROFILE_SHA256}."
            )
    except (OSError, ValueError) as exception:
        return None, Diagnostic("error", "PRESS_CMYK_PROFILE_REJECTED", str(exception))
    return profile_data, None


def _artifact(
    kind: str,
    job_id: str,
    path: Path,
    data: bytes,
    page_count: int,
) -> dict[str, Any]:
    return {
        "kind": kind,
        "relativePath": f"{job_id}/{path.name}",
        "mediaType": "application/pdf",
        "sha256": hashlib.sha256(data).hexdigest(),
        "byteLength": len(data),
        "pageCount": page_count,
    }


def _evidence(
    request: dict[str, Any],
    interior: PdfInspection,
    cover: PdfInspection,
    spine_width_inches: float,
) -> RenderEvidence:
    font_names = interior.fonts.keys() | cover.fonts.keys()
    fonts = [
        {
            "name": name,
            "embedded": interior.fonts.get(name, True) and cover.fonts.get(name, True),
        }
        for name in sorted(font_names)
    ]
    declared = DECLARED_STANDARD if request["profile"] == INGRAM_PROFILE else None
    interior_width, interior_height = measured_dimensions(
        interior.page_boxes["mediaBox"],
        "interior MediaBox",
    )
    cover_width, cover_height = measured_dimensions(
        cover.page_boxes["mediaBox"],
        "cover MediaBox",
    )
    return RenderEvidence(
        pdf_version=interior.pdf_version,
        interior_width_points=interior_width,
        interior_height_points=interior_height,
        cover_width_points=cover_width,
        cover_height_points=cover_height,
        spine_width_points=spine_width_inches * POINTS_PER_INCH,
        interior_page_boxes=interior.page_boxes,
        cover_page_boxes=cover.page_boxes,
        interior_page_boxes_consistent=interior.page_boxes_consistent,
        cover_page_boxes_consistent=cover.page_boxes_consistent,
        fonts=fonts,
        color_spaces=sorted(interior.color_spaces | cover.color_spaces),
        image_count=interior.image_count + cover.image_count,
        annotation_count=interior.annotation_count + cover.annotation_count,
        output_intent_count=interior.output_intent_count + cover.output_intent_count,
        has_transparency=interior.has_transparency or cover.has_transparency,
        has_encryption=interior.has_encryption or cover.has_encryption,
        has_forbidden_actions=interior.has_forbidden_actions or cover.has_forbidden_actions,
        declared_standard=declared,
        independently_validated_standard=None,
        claimed_standard=None,
    )
