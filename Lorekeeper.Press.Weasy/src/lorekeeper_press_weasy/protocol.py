"""Versioned process contracts shared with the first renderer candidate."""

from __future__ import annotations

import base64
import binascii
from io import BytesIO
from dataclasses import dataclass, field
from typing import Any
from uuid import UUID

from PIL import Image
from . import __version__


PROTOCOL_VERSION = 2
KDP_PROFILE = "kdp-paperback-6x9-preview-v1"
INGRAM_PROFILE = "ingram-pdf-x1a-preview-v1"
SUPPORTED_PROFILES = frozenset((KDP_PROFILE, INGRAM_PROFILE))
MAX_DOCUMENT_CHARACTERS = 10_000_000
MAX_COVER_IMAGE_BYTES = 20_000_000
MAX_COVER_IMAGE_DATA_URI_CHARACTERS = 26_666_691


@dataclass(slots=True)
class Diagnostic:
    severity: str
    code: str
    message: str
    artifact_kind: str | None = None
    page: int | None = None

    def to_dict(self) -> dict[str, Any]:
        return {
            "severity": self.severity,
            "code": self.code,
            "message": self.message,
            "artifactKind": self.artifact_kind,
            "page": self.page,
        }


@dataclass(slots=True)
class RenderEvidence:
    pdf_version: str | None = None
    interior_width_points: float | None = None
    interior_height_points: float | None = None
    cover_width_points: float | None = None
    cover_height_points: float | None = None
    spine_width_points: float | None = None
    interior_page_boxes: dict[str, list[float] | None] = field(default_factory=dict)
    cover_page_boxes: dict[str, list[float] | None] = field(default_factory=dict)
    interior_page_boxes_consistent: bool = False
    cover_page_boxes_consistent: bool = False
    fonts: list[dict[str, Any]] = field(default_factory=list)
    color_spaces: list[str] = field(default_factory=list)
    image_count: int = 0
    annotation_count: int = 0
    output_intent_count: int = 0
    has_transparency: bool = False
    has_encryption: bool = False
    has_forbidden_actions: bool = False
    moxcms_srgb_round_trip_verified: bool = False
    declared_standard: str | None = None
    independently_validated_standard: str | None = None
    claimed_standard: str | None = None

    def to_dict(self) -> dict[str, Any]:
        return {
            "pdfVersion": self.pdf_version,
            "interiorWidthPoints": self.interior_width_points,
            "interiorHeightPoints": self.interior_height_points,
            "coverWidthPoints": self.cover_width_points,
            "coverHeightPoints": self.cover_height_points,
            "spineWidthPoints": self.spine_width_points,
            "interiorPageBoxes": self.interior_page_boxes,
            "coverPageBoxes": self.cover_page_boxes,
            "interiorPageBoxesConsistent": self.interior_page_boxes_consistent,
            "coverPageBoxesConsistent": self.cover_page_boxes_consistent,
            "fonts": self.fonts,
            "colorSpaces": self.color_spaces,
            "imageCount": self.image_count,
            "annotationCount": self.annotation_count,
            "outputIntentCount": self.output_intent_count,
            "hasTransparency": self.has_transparency,
            "hasEncryption": self.has_encryption,
            "hasForbiddenActions": self.has_forbidden_actions,
            "moxcmsSrgbRoundTripVerified": self.moxcms_srgb_round_trip_verified,
            "declaredStandard": self.declared_standard,
            "independentlyValidatedStandard": self.independently_validated_standard,
            "claimedStandard": self.claimed_standard,
        }


def response(
    job_id: str,
    status: str,
    diagnostics: list[Diagnostic],
    *,
    artifacts: list[dict[str, Any]] | None = None,
    evidence: RenderEvidence | None = None,
    page_map: list[dict[str, Any]] | None = None,
) -> dict[str, Any]:
    return {
        "protocolVersion": PROTOCOL_VERSION,
        "rendererVersion": __version__,
        "jobId": job_id,
        "status": status,
        "artifacts": artifacts or [],
        "diagnostics": [diagnostic.to_dict() for diagnostic in diagnostics],
        "evidence": (evidence or RenderEvidence()).to_dict(),
        "pageMap": page_map or [],
    }


def validate_request(value: Any) -> tuple[dict[str, Any] | None, list[Diagnostic]]:
    diagnostics: list[Diagnostic] = []
    if not isinstance(value, dict):
        return None, [_error("PRESS_REQUEST_OBJECT_REQUIRED", "The request must be a JSON object.")]

    _exact_keys(
        value,
        {"protocolVersion", "jobId", "profile", "document", "trim", "cover"},
        "request",
        diagnostics,
    )
    if value.get("protocolVersion") != PROTOCOL_VERSION:
        diagnostics.append(_error("PRESS_PROTOCOL_UNSUPPORTED", f"Only protocol version {PROTOCOL_VERSION} is supported."))

    job_id = value.get("jobId")
    if (
        not isinstance(job_id, str)
        or not 1 <= len(job_id) <= 80
        or any(character not in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_" for character in job_id)
    ):
        diagnostics.append(
            _error("PRESS_JOB_ID_INVALID", "jobId must contain 1-80 ASCII letters, digits, hyphens, or underscores.")
        )

    if value.get("profile") not in SUPPORTED_PROFILES:
        diagnostics.append(_error("PRESS_PROFILE_UNSUPPORTED", "The requested press profile is unsupported."))

    document = value.get("document")
    if isinstance(document, dict):
        _keys(
            document,
            {"title", "author", "sections", "matter", "styles"},
            {
                "language",
                "subtitle",
                "publisher",
                "copyright",
                "includeTitlePage",
                "includeVisibleTableOfContents",
            },
            "document",
            diagnostics,
        )
        _bounded_text(document.get("title"), "document.title", 1, 500, diagnostics)
        _bounded_text(document.get("author"), "document.author", 1, 500, diagnostics)
        if "language" in document:
            _bounded_text(document.get("language"), "document.language", 2, 40, diagnostics)
        for field_name in ("subtitle", "publisher", "copyright"):
            if field_name in document:
                _bounded_text(document.get(field_name), f"document.{field_name}", 0, 100_000, diagnostics)
        for field_name in ("includeTitlePage", "includeVisibleTableOfContents"):
            if field_name in document and not isinstance(document.get(field_name), bool):
                diagnostics.append(_error("PRESS_BOOLEAN_INVALID", f"document.{field_name} must be a boolean."))
        total_characters = sum(
            len(value)
            for value in (
                document.get("title"),
                document.get("author"),
                document.get("language"),
                document.get("subtitle"),
                document.get("publisher"),
                document.get("copyright"),
            )
            if isinstance(value, str)
        )
        total_blocks = 0
        total_chapters = 0
        sections = document.get("sections")
        if not isinstance(sections, list) or not 1 <= len(sections) <= 500:
            diagnostics.append(_error("PRESS_SECTIONS_INVALID", "document.sections must contain 1-500 sections."))
        else:
            for section_index, section in enumerate(sections):
                section_path = f"document.sections[{section_index}]"
                if not isinstance(section, dict):
                    diagnostics.append(_error("PRESS_SECTION_INVALID", f"{section_path} must be an object."))
                    continue
                _exact_keys(
                    section,
                    {"id", "title", "synopsis", "includePage", "includeHeading", "chapters"},
                    section_path,
                    diagnostics,
                )
                if section.get("id") is not None:
                    _uuid(section.get("id"), f"{section_path}.id", diagnostics)
                _bounded_text(section.get("title"), f"{section_path}.title", 1, 500, diagnostics)
                _bounded_text(section.get("synopsis"), f"{section_path}.synopsis", 0, 2_000_000, diagnostics)
                total_characters += _text_length(section.get("title")) + _text_length(section.get("synopsis"))
                for field_name in ("includePage", "includeHeading"):
                    if not isinstance(section.get(field_name), bool):
                        diagnostics.append(_error("PRESS_BOOLEAN_INVALID", f"{section_path}.{field_name} must be a boolean."))
                chapters = section.get("chapters")
                if not isinstance(chapters, list) or len(chapters) > 500:
                    diagnostics.append(_error("PRESS_CHAPTERS_INVALID", f"{section_path}.chapters must be a list."))
                    continue
                total_chapters += len(chapters)
                for chapter_index, chapter in enumerate(chapters):
                    chapter_path = f"{section_path}.chapters[{chapter_index}]"
                    if not isinstance(chapter, dict):
                        diagnostics.append(_error("PRESS_CHAPTER_INVALID", f"{chapter_path} must be an object."))
                        continue
                    _exact_keys(
                        chapter,
                        {"id", "title", "synopsis", "includeHeading", "blocks"},
                        chapter_path,
                        diagnostics,
                    )
                    _uuid(chapter.get("id"), f"{chapter_path}.id", diagnostics)
                    _bounded_text(chapter.get("title"), f"{chapter_path}.title", 1, 500, diagnostics)
                    _bounded_text(chapter.get("synopsis"), f"{chapter_path}.synopsis", 0, 2_000_000, diagnostics)
                    total_characters += _text_length(chapter.get("title")) + _text_length(chapter.get("synopsis"))
                    if not isinstance(chapter.get("includeHeading"), bool):
                        diagnostics.append(_error("PRESS_BOOLEAN_INVALID", f"{chapter_path}.includeHeading must be a boolean."))
                    characters, blocks = _validate_blocks(chapter.get("blocks"), f"{chapter_path}.blocks", diagnostics)
                    total_characters += characters
                    total_blocks += blocks
        if total_chapters == 0 or total_chapters > 500:
            diagnostics.append(_error("PRESS_CHAPTERS_INVALID", "document.sections must contain 1-500 chapters in total."))
        matter = document.get("matter")
        if not isinstance(matter, list) or len(matter) > 500:
            diagnostics.append(_error("PRESS_MATTER_INVALID", "document.matter must be a list with at most 500 items."))
        else:
            for index, item in enumerate(matter):
                path = f"document.matter[{index}]"
                if not isinstance(item, dict):
                    diagnostics.append(_error("PRESS_MATTER_INVALID", f"{path} must be an object."))
                    continue
                _exact_keys(item, {"id", "location", "kind", "title", "blocks"}, path, diagnostics)
                _bounded_text(item.get("id"), f"{path}.id", 1, 80, diagnostics)
                _uuid(item.get("id"), f"{path}.id", diagnostics)
                if item.get("location") not in {"Front", "Back"}:
                    diagnostics.append(_error("PRESS_MATTER_LOCATION_INVALID", f"{path}.location is invalid."))
                if item.get("kind") not in {
                    "Dedication",
                    "Epigraph",
                    "Acknowledgments",
                    "AboutAuthor",
                    "AlsoBy",
                    "References",
                    "Custom",
                }:
                    diagnostics.append(_error("PRESS_MATTER_KIND_INVALID", f"{path}.kind is invalid."))
                _bounded_text(item.get("title"), f"{path}.title", 1, 500, diagnostics)
                total_characters += _text_length(item.get("title"))
                characters, blocks = _validate_blocks(item.get("blocks"), f"{path}.blocks", diagnostics)
                total_characters += characters
                total_blocks += blocks
        _validate_styles(document.get("styles"), diagnostics)
        if total_characters > MAX_DOCUMENT_CHARACTERS or total_blocks > 100_000:
            diagnostics.append(
                _error(
                    "PRESS_DOCUMENT_TOO_LARGE",
                    "Combined chapter and matter content exceeds the press-runtime limits.",
                )
            )
    else:
        diagnostics.append(_error("PRESS_DOCUMENT_INVALID", "document must be an object."))

    trim = value.get("trim")
    if isinstance(trim, dict):
        _keys(
            trim,
            {"widthInches", "heightInches"},
            {"marginInches", "bodyFontSizePoints", "bodyLineHeight"},
            "trim",
            diagnostics,
        )
        _bounded_number(trim.get("widthInches"), "trim.widthInches", 4.0, 12.0, diagnostics)
        _bounded_number(trim.get("heightInches"), "trim.heightInches", 6.0, 15.0, diagnostics)
        if "marginInches" in trim:
            _bounded_number(trim.get("marginInches"), "trim.marginInches", 0.25, 2.0, diagnostics)
        if "bodyFontSizePoints" in trim:
            _bounded_number(trim.get("bodyFontSizePoints"), "trim.bodyFontSizePoints", 6.0, 36.0, diagnostics)
        if "bodyLineHeight" in trim:
            _bounded_number(trim.get("bodyLineHeight"), "trim.bodyLineHeight", 0.8, 3.0, diagnostics)
        if (
            isinstance(trim.get("widthInches"), (int, float))
            and not isinstance(trim.get("widthInches"), bool)
            and isinstance(trim.get("heightInches"), (int, float))
            and not isinstance(trim.get("heightInches"), bool)
            and (
                float(trim["widthInches"]) != 6.0
                or float(trim["heightInches"]) != 9.0
            )
        ):
            diagnostics.append(
                _error(
                    "PRESS_PROFILE_TRIM_MISMATCH",
                    "The initial paperback profiles are restricted to exactly 6 × 9 inches.",
                )
            )
    else:
        diagnostics.append(_error("PRESS_TRIM_INVALID", "trim must be an object."))

    cover = value.get("cover")
    if isinstance(cover, dict):
        _keys(
            cover,
            {"bleedInches", "paperCaliperInchesPerPage", "backCopy"},
            {
                "title", "subtitle", "author", "spineText", "backgroundColor", "isbn",
                "barcodeMode", "imageDataUri", "imageFocalXPercent", "imageFocalYPercent",
            },
            "cover",
            diagnostics,
        )
        _bounded_number(cover.get("bleedInches"), "cover.bleedInches", 0.0, 0.5, diagnostics)
        _bounded_number(
            cover.get("paperCaliperInchesPerPage"),
            "cover.paperCaliperInchesPerPage",
            0.001,
            0.01,
            diagnostics,
        )
        _bounded_text(cover.get("backCopy"), "cover.backCopy", 0, 1_800, diagnostics)
        for field_name in ("title", "subtitle", "author", "spineText", "backgroundColor", "isbn", "barcodeMode"):
            if field_name in cover:
                maximum = {
                    "title": 160,
                    "subtitle": 240,
                    "author": 160,
                    "spineText": 120,
                }.get(field_name, 500)
                _bounded_text(cover.get(field_name), f"cover.{field_name}", 0, maximum, diagnostics)
        if "imageDataUri" in cover:
            _bounded_text(
                cover.get("imageDataUri"),
                "cover.imageDataUri",
                0,
                MAX_COVER_IMAGE_DATA_URI_CHARACTERS,
                diagnostics,
            )
            image_uri = cover.get("imageDataUri")
            if image_uri:
                if not isinstance(image_uri, str) or not image_uri.startswith("data:image/png;base64,"):
                    diagnostics.append(_error("PRESS_COVER_IMAGE_INVALID", "cover.imageDataUri must be a base64 PNG data URI."))
                else:
                    try:
                        image_data = base64.b64decode(image_uri.partition(",")[2], validate=True)
                        width = int.from_bytes(image_data[16:20], "big") if len(image_data) >= 24 else 0
                        height = int.from_bytes(image_data[20:24], "big") if len(image_data) >= 24 else 0
                        if (
                            len(image_data) > MAX_COVER_IMAGE_BYTES
                            or image_data[:8] != b"\x89PNG\r\n\x1a\n"
                            or image_data[12:16] != b"IHDR"
                            or image_data[24] != 8
                            or image_data[25] not in (2, 6)
                            or image_data[28] not in (0, 1)
                            or width < 1
                            or height < 1
                            or width * height > 16_000_000
                        ):
                            diagnostics.append(_error("PRESS_COVER_IMAGE_INVALID", "cover image PNG data or dimensions are invalid."))
                        else:
                            with Image.open(BytesIO(image_data)) as decoded:
                                decoded.verify()
                    except (binascii.Error, ValueError):
                        diagnostics.append(_error("PRESS_COVER_IMAGE_INVALID", "cover image base64 is invalid."))
                    except Exception:
                        diagnostics.append(_error("PRESS_COVER_IMAGE_INVALID", "cover image PNG could not be decoded safely."))
                if value.get("profile") == INGRAM_PROFILE:
                    diagnostics.append(_error(
                        "PRESS_COVER_IMAGE_CMYK_UNSUPPORTED",
                        "Selected cover images are not yet supported by the contained Ingram CMYK Preview profile.",
                    ))
        for field_name in ("imageFocalXPercent", "imageFocalYPercent"):
            if field_name in cover:
                _bounded_number(cover.get(field_name), f"cover.{field_name}", 0, 100, diagnostics)
    else:
        diagnostics.append(_error("PRESS_COVER_INVALID", "cover must be an object."))

    return (value if not diagnostics else None), diagnostics


def _text_length(value: Any) -> int:
    return len(value) if isinstance(value, str) else 0


def _validate_blocks(
    blocks: Any,
    path: str,
    diagnostics: list[Diagnostic],
) -> tuple[int, int]:
    if not isinstance(blocks, list) or len(blocks) > 100_000:
        diagnostics.append(_error("PRESS_BLOCKS_INVALID", f"{path} must be a list."))
        return 0, 0
    characters = 0
    allowed_block_types = {"Paragraph", "Heading", "SceneBreak", "BlockQuote", "ListItem", "Figure"}
    allowed_mark_types = {
        "Emphasis",
        "Strong",
        "Underline",
        "Strikethrough",
        "Code",
        "Link",
        "Language",
        "SmallCaps",
        "Superscript",
        "Subscript",
        "CharacterStyle",
    }
    for block_index, block in enumerate(blocks):
        block_path = f"{path}[{block_index}]"
        if not isinstance(block, dict):
            diagnostics.append(_error("PRESS_BLOCK_INVALID", f"{block_path} must be an object."))
            continue
        _exact_keys(block, {"id", "type", "styleRole", "headingLevel", "content"}, block_path, diagnostics)
        _bounded_text(block.get("id"), f"{block_path}.id", 1, 80, diagnostics)
        _uuid(block.get("id"), f"{block_path}.id", diagnostics)
        if block.get("type") not in allowed_block_types:
            diagnostics.append(_error("PRESS_BLOCK_TYPE_INVALID", f"{block_path}.type is invalid."))
        _bounded_text(block.get("styleRole"), f"{block_path}.styleRole", 1, 80, diagnostics)
        heading_level = block.get("headingLevel")
        if heading_level is not None and (
            not isinstance(heading_level, int)
            or isinstance(heading_level, bool)
            or not 1 <= heading_level <= 6
        ):
            diagnostics.append(_error("PRESS_HEADING_LEVEL_INVALID", f"{block_path}.headingLevel is invalid."))
        content = block.get("content")
        if not isinstance(content, list) or len(content) > 100_000:
            diagnostics.append(_error("PRESS_INLINE_INVALID", f"{block_path}.content must be a list."))
            continue
        for inline_index, inline in enumerate(content):
            inline_path = f"{block_path}.content[{inline_index}]"
            if not isinstance(inline, dict):
                diagnostics.append(_error("PRESS_INLINE_INVALID", f"{inline_path} must be an object."))
                continue
            _exact_keys(inline, {"type", "text", "marks"}, inline_path, diagnostics)
            if inline.get("type") != "Text":
                diagnostics.append(_error("PRESS_INLINE_TYPE_INVALID", f"{inline_path}.type is invalid."))
            _bounded_text(inline.get("text"), f"{inline_path}.text", 0, 2_000_000, diagnostics)
            if isinstance(inline.get("text"), str):
                characters += len(inline["text"])
            marks = inline.get("marks")
            if not isinstance(marks, list) or len(marks) > 100:
                diagnostics.append(_error("PRESS_MARKS_INVALID", f"{inline_path}.marks must be a list."))
                continue
            for mark_index, mark in enumerate(marks):
                mark_path = f"{inline_path}.marks[{mark_index}]"
                if not isinstance(mark, dict):
                    diagnostics.append(_error("PRESS_MARK_INVALID", f"{mark_path} must be an object."))
                    continue
                _exact_keys(mark, {"type", "value"}, mark_path, diagnostics)
                if mark.get("type") not in allowed_mark_types:
                    diagnostics.append(_error("PRESS_MARK_TYPE_INVALID", f"{mark_path}.type is invalid."))
                value = mark.get("value")
                if value is not None:
                    _bounded_text(value, f"{mark_path}.value", 1, 2_000, diagnostics)
    return characters, len(blocks)


def _validate_styles(styles: Any, diagnostics: list[Diagnostic]) -> None:
    if not isinstance(styles, list) or len(styles) > 500:
        diagnostics.append(_error("PRESS_STYLES_INVALID", "document.styles must be a list with at most 500 items."))
        return
    definition_keys = {
        "fontFamilyKey",
        "fontSizePoints",
        "fontWeight",
        "italic",
        "smallCaps",
        "lineHeight",
        "spaceBeforePoints",
        "spaceAfterPoints",
        "keepWithNext",
        "textAlign",
    }
    for index, style in enumerate(styles):
        path = f"document.styles[{index}]"
        if not isinstance(style, dict):
            diagnostics.append(_error("PRESS_STYLE_INVALID", f"{path} must be an object."))
            continue
        _exact_keys(style, {"name", "kind", "semanticRole", "definition"}, path, diagnostics)
        _bounded_text(style.get("name"), f"{path}.name", 1, 80, diagnostics)
        if style.get("kind") not in {"Paragraph", "Character"}:
            diagnostics.append(_error("PRESS_STYLE_KIND_INVALID", f"{path}.kind is invalid."))
        _bounded_text(style.get("semanticRole"), f"{path}.semanticRole", 1, 80, diagnostics)
        definition = style.get("definition")
        if not isinstance(definition, dict):
            diagnostics.append(_error("PRESS_STYLE_INVALID", f"{path}.definition must be an object."))
            continue
        _exact_keys(definition, definition_keys, f"{path}.definition", diagnostics)
        for key in ("italic", "smallCaps", "keepWithNext"):
            if definition.get(key) is not None and not isinstance(definition.get(key), bool):
                diagnostics.append(_error("PRESS_STYLE_INVALID", f"{path}.definition.{key} must be boolean or null."))
        for key in ("fontSizePoints", "lineHeight", "spaceBeforePoints", "spaceAfterPoints"):
            if definition.get(key) is not None:
                _bounded_number(definition.get(key), f"{path}.definition.{key}", 0.0, 288.0, diagnostics)
        if definition.get("fontWeight") is not None:
            weight = definition["fontWeight"]
            if not isinstance(weight, int) or isinstance(weight, bool) or weight < 100 or weight > 900 or weight % 100:
                diagnostics.append(_error("PRESS_STYLE_INVALID", f"{path}.definition.fontWeight is invalid."))
        if definition.get("fontFamilyKey") not in {None, "serif", "sans", "mono"}:
            diagnostics.append(_error("PRESS_STYLE_INVALID", f"{path}.definition.fontFamilyKey is invalid."))
        if definition.get("textAlign") not in {None, "left", "right", "center", "justify"}:
            diagnostics.append(_error("PRESS_STYLE_INVALID", f"{path}.definition.textAlign is invalid."))


def _keys(
    value: dict[str, Any],
    required: set[str],
    optional: set[str],
    path: str,
    diagnostics: list[Diagnostic],
) -> None:
    unknown = sorted(set(value) - required - optional)
    missing = sorted(required - set(value))
    if unknown:
        diagnostics.append(_error("PRESS_UNKNOWN_FIELD", f"{path} contains unknown fields: {', '.join(unknown)}."))
    if missing:
        diagnostics.append(_error("PRESS_REQUIRED_FIELD", f"{path} is missing fields: {', '.join(missing)}."))


def _exact_keys(
    value: dict[str, Any],
    expected: set[str],
    path: str,
    diagnostics: list[Diagnostic],
) -> None:
    unknown = sorted(set(value) - expected)
    missing = sorted(expected - set(value))
    if unknown:
        diagnostics.append(_error("PRESS_UNKNOWN_FIELD", f"{path} contains unknown fields: {', '.join(unknown)}."))
    if missing:
        diagnostics.append(_error("PRESS_REQUIRED_FIELD", f"{path} is missing fields: {', '.join(missing)}."))


def _bounded_text(
    value: Any,
    path: str,
    minimum: int,
    maximum: int,
    diagnostics: list[Diagnostic],
) -> None:
    if not isinstance(value, str) or not minimum <= len(value) <= maximum:
        diagnostics.append(
            _error("PRESS_TEXT_INVALID", f"{path} must contain {minimum}-{maximum} characters.")
        )


def _uuid(value: Any, path: str, diagnostics: list[Diagnostic]) -> None:
    try:
        UUID(str(value))
    except (ValueError, AttributeError, TypeError):
        diagnostics.append(_error("PRESS_UUID_INVALID", f"{path} must be a UUID."))


def _bounded_number(
    value: Any,
    path: str,
    minimum: float,
    maximum: float,
    diagnostics: list[Diagnostic],
) -> None:
    if (
        isinstance(value, bool)
        or not isinstance(value, (int, float))
        or not minimum <= float(value) <= maximum
    ):
        diagnostics.append(
            _error("PRESS_NUMBER_INVALID", f"{path} must be between {minimum} and {maximum}.")
        )


def _error(code: str, message: str) -> Diagnostic:
    return Diagnostic("error", code, message)
