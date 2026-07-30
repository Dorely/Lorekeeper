"""Measured artifact geometry checks shared by response evidence and preflight."""

from __future__ import annotations

from typing import Any, Protocol


POINTS_PER_INCH = 72.0


class Inspection(Protocol):
    page_boxes: dict[str, list[float] | None]
    page_boxes_consistent: bool


def geometry_errors(
    request: dict[str, Any],
    interior: Inspection,
    cover: Inspection,
    spine_width_inches: float,
) -> list[str]:
    trim = request["trim"]
    cover_settings = request["cover"]
    expected_interior = (
        trim["widthInches"] * POINTS_PER_INCH,
        trim["heightInches"] * POINTS_PER_INCH,
    )
    expected_cover = (
        (
            trim["widthInches"] * 2
            + spine_width_inches
            + cover_settings["bleedInches"] * 2
        )
        * POINTS_PER_INCH,
        (trim["heightInches"] + cover_settings["bleedInches"] * 2) * POINTS_PER_INCH,
    )

    errors: list[str] = []
    if not interior.page_boxes_consistent:
        errors.append("Interior page boxes are not consistent.")
    if not cover.page_boxes_consistent:
        errors.append("Cover page boxes are not consistent.")
    for name in ("mediaBox", "trimBox", "bleedBox"):
        _compare_box(f"Interior {name}", interior.page_boxes.get(name), expected_interior, errors)
        _compare_box(f"Cover {name}", cover.page_boxes.get(name), expected_cover, errors)
    return errors


def measured_dimensions(
    box: list[float] | None,
    label: str,
) -> tuple[float, float]:
    if box is None or len(box) != 4:
        raise ValueError(f"{label} is missing or malformed.")
    return round(box[2] - box[0], 4), round(box[3] - box[1], 4)


def _compare_box(
    label: str,
    box: list[float] | None,
    expected: tuple[float, float],
    errors: list[str],
) -> None:
    if box is None:
        errors.append(f"{label} is missing.")
        return
    actual = measured_dimensions(box, label)
    if any(abs(actual[index] - expected[index]) > 0.02 for index in (0, 1)):
        errors.append(
            f"{label} is {actual[0]:.4f} × {actual[1]:.4f} pt; "
            f"expected {expected[0]:.4f} × {expected[1]:.4f} pt."
        )
