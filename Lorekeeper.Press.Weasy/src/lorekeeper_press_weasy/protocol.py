"""Versioned process contracts shared with the first renderer candidate."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any
from uuid import UUID

from . import __version__


PROTOCOL_VERSION = 1
KDP_PROFILE = "kdp-paperback-6x9-preview-v1"
INGRAM_PROFILE = "ingram-pdf-x1a-preview-v1"
SUPPORTED_PROFILES = frozenset((KDP_PROFILE, INGRAM_PROFILE))


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
        diagnostics.append(_error("PRESS_PROTOCOL_UNSUPPORTED", "Only protocol version 1 is supported."))

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
            {"title", "author", "chapters"},
            {
                "language",
                "subtitle",
                "publisher",
                "copyright",
                "dedication",
                "acknowledgments",
                "references",
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
        for field_name in ("subtitle", "publisher", "copyright", "dedication", "acknowledgments", "references"):
            if field_name in document:
                _bounded_text(document.get(field_name), f"document.{field_name}", 0, 100_000, diagnostics)
        for field_name in ("includeTitlePage", "includeVisibleTableOfContents"):
            if field_name in document and not isinstance(document.get(field_name), bool):
                diagnostics.append(_error("PRESS_BOOLEAN_INVALID", f"document.{field_name} must be a boolean."))
        chapters = document.get("chapters")
        if not isinstance(chapters, list) or not 1 <= len(chapters) <= 500:
            diagnostics.append(_error("PRESS_CHAPTERS_INVALID", "document.chapters must contain 1-500 chapters."))
        else:
            total_characters = 0
            for index, chapter in enumerate(chapters):
                if not isinstance(chapter, dict):
                    diagnostics.append(_error("PRESS_CHAPTER_INVALID", f"document.chapters[{index}] must be an object."))
                    continue
                _keys(
                    chapter,
                    {"title", "body"},
                    {"id", "blocks"},
                    f"document.chapters[{index}]",
                    diagnostics,
                )
                _bounded_text(chapter.get("title"), f"document.chapters[{index}].title", 1, 500, diagnostics)
                body = chapter.get("body")
                _bounded_text(body, f"document.chapters[{index}].body", 1, 2_000_000, diagnostics)
                if isinstance(body, str):
                    total_characters += len(body)
                if "id" in chapter:
                    _bounded_text(chapter.get("id"), f"document.chapters[{index}].id", 1, 80, diagnostics)
                    _uuid(chapter.get("id"), f"document.chapters[{index}].id", diagnostics)
                blocks = chapter.get("blocks")
                if blocks is not None:
                    if not isinstance(blocks, list) or len(blocks) > 100_000:
                        diagnostics.append(_error("PRESS_BLOCKS_INVALID", f"document.chapters[{index}].blocks must be a list."))
                    else:
                        for block_index, block in enumerate(blocks):
                            if not isinstance(block, dict):
                                diagnostics.append(_error("PRESS_BLOCK_INVALID", f"document.chapters[{index}].blocks[{block_index}] must be an object."))
                                continue
                            _exact_keys(
                                block,
                                {"id", "type", "text"},
                                f"document.chapters[{index}].blocks[{block_index}]",
                                diagnostics,
                            )
                            _bounded_text(block.get("id"), f"document.chapters[{index}].blocks[{block_index}].id", 1, 80, diagnostics)
                            _uuid(block.get("id"), f"document.chapters[{index}].blocks[{block_index}].id", diagnostics)
                            _bounded_text(block.get("type"), f"document.chapters[{index}].blocks[{block_index}].type", 1, 40, diagnostics)
                            _bounded_text(block.get("text"), f"document.chapters[{index}].blocks[{block_index}].text", 0, 2_000_000, diagnostics)
            if total_characters > 10_000_000:
                diagnostics.append(_error("PRESS_DOCUMENT_TOO_LARGE", "Chapter content exceeds 10,000,000 characters."))
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
        _exact_keys(
            cover,
            {"bleedInches", "paperCaliperInchesPerPage", "backCopy"},
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
        _bounded_text(cover.get("backCopy"), "cover.backCopy", 0, 10_000, diagnostics)
    else:
        diagnostics.append(_error("PRESS_COVER_INVALID", "cover must be an object."))

    return (value if not diagnostics else None), diagnostics


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
