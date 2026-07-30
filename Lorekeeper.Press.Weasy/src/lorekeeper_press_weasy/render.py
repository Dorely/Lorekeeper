"""Deterministic, contained WeasyPrint press fixture renderer."""

from __future__ import annotations

import hashlib
import html
import logging
from pathlib import Path
from typing import Any

from weasyprint import HTML
from weasyprint.logger import LOGGER

from .geometry import POINTS_PER_INCH, geometry_errors, measured_dimensions
from .inspect import PdfInspection, inspect_pdf, pdfx_2001_errors
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
        )
        if cover_warnings:
            raise ValueError("WeasyPrint warnings: " + " | ".join(cover_warnings))
        cover_inspection = inspect_pdf(cover_data)

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
) -> tuple[bytes, list[str], list[dict[str, Any]]]:
    register_pdfx_2001_profile()
    profile_uri = profile_path.resolve().as_uri() if profile_path is not None else None

    def fetch(url: str, *_args: Any, **_kwargs: Any) -> dict[str, Any]:
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


def _interior_html(request: dict[str, Any], profile_path: Path | None) -> str:
    document = request["document"]
    front_matter = []
    if document.get("includeTitlePage", False):
        subtitle = (
            f"<p class=\"subtitle\">{html.escape(document.get('subtitle', ''))}</p>"
            if document.get("subtitle")
            else ""
        )
        front_matter.append(
            f'<section class="front title-page"><h1>{html.escape(document["title"])}</h1>'
            f'{subtitle}<p>{html.escape(document["author"])}</p></section>'
        )
    if document.get("copyright") or document.get("publisher"):
        front_matter.append(
            f'<section class="front copyright-page"><p>{html.escape(document.get("copyright", ""))}</p>'
            f'<p>{html.escape(document.get("publisher", ""))}</p></section>'
        )
    if document.get("dedication"):
        front_matter.append(
            f'<section class="front dedication"><p>{html.escape(document["dedication"])}</p></section>'
        )
    if document.get("includeVisibleTableOfContents", False):
        items = "".join(
            f"<li>{html.escape(chapter['title'])}</li>"
            for chapter in document["chapters"]
        )
        front_matter.append(f'<section class="front contents"><h1>Contents</h1><ol>{items}</ol></section>')
    chapters = []
    for chapter in document["chapters"]:
        chapter_id = chapter.get("id", "")
        if chapter.get("blocks") is not None:
            paragraphs = "".join(
                _block_html(chapter_id, block)
                for block in chapter["blocks"]
            )
        else:
            paragraphs = "".join(
                f"<p>{html.escape(paragraph)}</p>"
                for paragraph in chapter["body"].split("\n\n")
                if paragraph
            )
        chapters.append(
            f'<section class="chapter"><h1>{html.escape(chapter["title"])}</h1>{paragraphs}</section>'
        )
    return _html_document(
        request,
        profile_path,
        f"""
        @page {{
          size: {request["trim"]["widthInches"]}in {request["trim"]["heightInches"]}in;
          margin: {request["trim"].get("marginInches", 0.75)}in;
          @bottom-center {{ content: counter(page); font-family: "Liberation Serif", serif; font-size: 9pt; }}
        }}
        @page :left {{ margin-left: 0.625in; margin-right: 0.75in; }}
        @page :right {{ margin-left: 0.75in; margin-right: 0.625in; }}
        @page :left {{ @top-center {{ content: string(chapter-title); font-size: 8.5pt; }} }}
        @page :right {{ @top-center {{ content: "{html.escape(document["title"])}"; font-size: 8.5pt; }} }}
        @page front {{ @bottom-center {{ content: counter(page, lower-roman); }} }}
        @page chapter:first {{ @top-center {{ content: none; }} }}
        body {{ font-family: "Liberation Serif", serif; font-size: {request["trim"].get("bodyFontSizePoints", 11)}pt; line-height: {request["trim"].get("bodyLineHeight", 1.32)}; }}
        .chapter {{ page: chapter; break-before: right; }}
        .front {{ page: front; break-after: page; }}
        .title-page {{ align-items: center; display: flex; flex-direction: column; justify-content: center; text-align: center; }}
        .title-page h1 {{ font-size: 28pt; margin-bottom: 0.2in; }}
        .copyright-page {{ display: flex; flex-direction: column; justify-content: end; font-size: 9pt; }}
        .dedication {{ display: flex; align-items: center; justify-content: center; text-align: center; }}
        .contents li {{ margin-bottom: 0.12in; }}
        h1 {{ string-set: chapter-title content(); text-align: center; margin: 1.25in 0 0.55in; }}
        p {{ margin: 0; text-align: justify; text-indent: 1.25em; hyphens: auto; orphans: 3; widows: 3; }}
        h1 + p {{ text-indent: 0; }}
        """,
        "".join(front_matter) + "".join(chapters),
    )


def _block_html(chapter_id: str, block: dict[str, Any]) -> str:
    anchor = f"lk-block-{chapter_id.replace('-', '')}{block['id'].replace('-', '')}"
    text = html.escape(block["text"])
    block_type = block["type"]
    if block_type == "SceneBreak":
        return f'<p id="{anchor}" class="scene-break">* * *</p>'
    if block_type == "Heading":
        return f'<h2 id="{anchor}">{text}</h2>'
    if block_type == "BlockQuote":
        return f'<blockquote id="{anchor}">{text}</blockquote>'
    if block_type == "ListItem":
        return f'<p id="{anchor}" class="list-item">â€¢ {text}</p>'
    if block_type == "Figure":
        return f'<p id="{anchor}" class="figure-caption">{text}</p>'
    return f'<p id="{anchor}">{text}</p>'


def _cover_html(
    request: dict[str, Any],
    profile_path: Path | None,
    page_count: int,
) -> tuple[str, float]:
    trim = request["trim"]
    cover = request["cover"]
    spine_width = page_count * cover["paperCaliperInchesPerPage"]
    width = trim["widthInches"] * 2 + spine_width + cover["bleedInches"] * 2
    height = trim["heightInches"] + cover["bleedInches"] * 2
    body = f"""
      <main class="cover">
        <section class="back"><p>{html.escape(cover["backCopy"])}</p></section>
        <section class="spine"></section>
        <section class="front">
          <h1>{html.escape(request["document"]["title"])}</h1>
          <p>{html.escape(request["document"]["author"])}</p>
        </section>
      </main>
    """
    if request["profile"] == INGRAM_PROFILE:
        panel_color = "device-cmyk(0.55 0.35 0 0.05)"
        spine_color = "device-cmyk(0.6 0.4 0 0.1)"
        text_color = "device-cmyk(0 0 0 0)"
    else:
        panel_color = "#5c7ca5"
        spine_color = "#526f94"
        text_color = "#ffffff"
    css = f"""
      @page {{ size: {width}in {height}in; margin: 0; }}
      html, body {{ margin: 0; width: 100%; height: 100%; font-family: "Liberation Serif", serif; }}
      .cover {{ display: grid; grid-template-columns: {trim["widthInches"] + cover["bleedInches"]}in {spine_width}in {trim["widthInches"] + cover["bleedInches"]}in; width: 100%; height: 100%; }}
      .back, .front {{ box-sizing: border-box; padding: 0.75in; background: {panel_color}; color: {text_color}; }}
      .spine {{ background: {spine_color}; }}
      .front {{ display: flex; flex-direction: column; justify-content: center; text-align: center; }}
      .front h1 {{ font-size: 28pt; }}
      .back {{ display: flex; align-items: center; font-size: 12pt; line-height: 1.4; }}
    """
    return _html_document(request, profile_path, css, body), spine_width


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
