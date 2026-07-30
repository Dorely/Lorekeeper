"""Independent structural checks for the generated PDF/X candidate."""

from __future__ import annotations

import hashlib
from dataclasses import dataclass, field
from io import BytesIO
from typing import Any, Iterable

from pypdf import PdfReader
from pypdf.generic import (
    ArrayObject,
    ContentStream,
    DictionaryObject,
    IndirectObject,
    NameObject,
)


@dataclass(slots=True)
class PdfInspection:
    pdf_version: str
    page_count: int
    page_boxes: dict[str, list[float] | None]
    page_boxes_consistent: bool
    fonts: dict[str, bool] = field(default_factory=dict)
    color_spaces: set[str] = field(default_factory=set)
    image_count: int = 0
    annotation_count: int = 0
    output_intent_count: int = 0
    output_profile_components: list[int] = field(default_factory=list)
    output_profile_sha256: list[str] = field(default_factory=list)
    output_intent_subtypes: list[str] = field(default_factory=list)
    icc_profile_components: list[int] = field(default_factory=list)
    has_transparency: bool = False
    has_encryption: bool = False
    has_forbidden_actions: bool = False
    gts_pdfx_version: str | None = None


def inspect_pdf(data: bytes) -> PdfInspection:
    reader = PdfReader(BytesIO(data), strict=True)
    pages = list(reader.pages)
    if not pages:
        raise ValueError("PDF contains no pages.")

    boxes = [_page_boxes(page) for page in pages]
    result = PdfInspection(
        pdf_version=reader.pdf_header.removeprefix("%PDF-"),
        page_count=len(pages),
        page_boxes=boxes[0],
        page_boxes_consistent=all(box == boxes[0] for box in boxes),
        has_encryption=reader.is_encrypted,
    )
    metadata = _resolve(reader.trailer.get("/Info"))
    if isinstance(metadata, DictionaryObject):
        version = metadata.get("/GTS_PDFXVersion")
        result.gts_pdfx_version = str(version) if version is not None else None

    root = _resolve(reader.trailer.get("/Root"))
    if isinstance(root, DictionaryObject):
        intents = _resolve(root.get("/OutputIntents"))
        if isinstance(intents, ArrayObject):
            result.output_intent_count = len(intents)
            for intent_value in intents:
                intent = _resolve(intent_value)
                if not isinstance(intent, DictionaryObject):
                    continue
                result.output_intent_subtypes.append(str(intent.get("/S", "")))
                profile = _resolve(intent.get("/DestOutputProfile"))
                if isinstance(profile, DictionaryObject):
                    components = profile.get("/N")
                    if isinstance(components, int):
                        result.output_profile_components.append(components)
                    try:
                        result.output_profile_sha256.append(
                            hashlib.sha256(profile.get_data()).hexdigest()
                        )
                    except (AttributeError, OSError, ValueError):
                        result.output_profile_sha256.append("")
        result.has_forbidden_actions |= _contains_forbidden_action(root)

    seen_forms: set[tuple[int, int]] = set()
    for page in pages:
        annotations = _resolve(page.get("/Annots"))
        if isinstance(annotations, ArrayObject):
            result.annotation_count += len(annotations)
        result.has_forbidden_actions |= _contains_forbidden_action(page)
        _inspect_resources(_resolve(page.get("/Resources")), result, seen_forms, reader)
        _inspect_content(page.get_contents(), result, reader)
    return result


def pdfx_2001_errors(inspection: PdfInspection) -> list[str]:
    errors: list[str] = []
    if inspection.pdf_version != "1.3":
        errors.append(f"PDF header is {inspection.pdf_version}, expected 1.3.")
    if inspection.gts_pdfx_version != "PDF/X-1a:2001":
        errors.append(
            f"GTS_PDFXVersion is {inspection.gts_pdfx_version!r}, expected 'PDF/X-1a:2001'."
        )
    if inspection.output_intent_count != 1:
        errors.append(f"Expected exactly one output intent, found {inspection.output_intent_count}.")
    if inspection.output_profile_components != [4]:
        errors.append(
            f"Expected one four-component CMYK output profile, found {inspection.output_profile_components}."
        )
    if inspection.output_intent_subtypes != ["/GTS_PDFX"]:
        errors.append(
            f"Expected one GTS_PDFX output intent, found {inspection.output_intent_subtypes}."
        )
    if inspection.output_profile_sha256 != [
        "b424c77f40c3423c925536f8ae08634985ccd0fe80eb253d5d229197ded7e886"
    ]:
        errors.append("The embedded output profile does not match the pinned CMYK profile.")
    if any(components not in (1, 4) for components in inspection.icc_profile_components):
        errors.append(
            f"An ICCBased content color space has forbidden component counts: "
            f"{inspection.icc_profile_components}."
        )
    if not inspection.page_boxes_consistent:
        errors.append("Page boxes are inconsistent.")
    if any(not embedded for embedded in inspection.fonts.values()):
        errors.append("At least one font is not embedded.")
    if "DeviceRGB" in inspection.color_spaces:
        errors.append("DeviceRGB content is forbidden by the declared PDF/X-1a scope.")
    if inspection.has_transparency:
        errors.append("Transparency is forbidden by the declared PDF/X-1a scope.")
    if inspection.has_encryption:
        errors.append("Encrypted PDFs are forbidden.")
    if inspection.has_forbidden_actions:
        errors.append("The PDF contains a forbidden action.")
    if inspection.annotation_count:
        errors.append("The press artifact contains annotations.")
    return errors


def _page_boxes(page: DictionaryObject) -> dict[str, list[float] | None]:
    return {
        "mediaBox": _box(page.get("/MediaBox")),
        "cropBox": _box(page.get("/CropBox")),
        "bleedBox": _box(page.get("/BleedBox")),
        "trimBox": _box(page.get("/TrimBox")),
        "artBox": _box(page.get("/ArtBox")),
    }


def _box(value: Any) -> list[float] | None:
    resolved = _resolve(value)
    if not isinstance(resolved, (ArrayObject, list)) or len(resolved) != 4:
        return None
    return [round(float(item), 4) for item in resolved]


def _inspect_resources(
    resources_value: Any,
    result: PdfInspection,
    seen_forms: set[tuple[int, int]],
    reader: PdfReader,
) -> None:
    resources = _resolve(resources_value)
    if not isinstance(resources, DictionaryObject):
        return

    fonts = _resolve(resources.get("/Font"))
    if isinstance(fonts, DictionaryObject):
        for font_value in fonts.values():
            font = _resolve(font_value)
            if not isinstance(font, DictionaryObject):
                continue
            name = str(font.get("/BaseFont", "<unnamed>")).removeprefix("/")
            embedded = _font_embedded(font)
            result.fonts[name] = result.fonts.get(name, True) and embedded

    color_spaces = _resolve(resources.get("/ColorSpace"))
    if isinstance(color_spaces, DictionaryObject):
        for color_space in color_spaces.values():
            _record_color_space(color_space, result)

    ext_states = _resolve(resources.get("/ExtGState"))
    if isinstance(ext_states, DictionaryObject):
        for state_value in ext_states.values():
            state = _resolve(state_value)
            if not isinstance(state, DictionaryObject):
                continue
            if float(state.get("/ca", 1.0)) < 1.0 or float(state.get("/CA", 1.0)) < 1.0:
                result.has_transparency = True
            if str(state.get("/SMask", "/None")) != "/None":
                result.has_transparency = True
            blend_mode = _resolve(state.get("/BM", "/Normal"))
            blend_modes = blend_mode if isinstance(blend_mode, ArrayObject) else [blend_mode]
            if any(str(mode) not in ("/Normal", "/Compatible") for mode in blend_modes):
                result.has_transparency = True

    xobjects = _resolve(resources.get("/XObject"))
    if not isinstance(xobjects, DictionaryObject):
        return
    for xobject_value in xobjects.values():
        identity = _identity(xobject_value)
        xobject = _resolve(xobject_value)
        if not isinstance(xobject, DictionaryObject):
            continue
        subtype = str(xobject.get("/Subtype"))
        if subtype == "/Image":
            result.image_count += 1
            _record_color_space(xobject.get("/ColorSpace"), result)
            if (
                xobject.get("/SMask") is not None
                or xobject.get("/Mask") is not None
                or int(xobject.get("/SMaskInData", 0)) != 0
            ):
                result.has_transparency = True
        elif subtype == "/Form":
            if identity is not None and identity in seen_forms:
                continue
            if identity is not None:
                seen_forms.add(identity)
            group = _resolve(xobject.get("/Group"))
            if isinstance(group, DictionaryObject) and str(group.get("/S")) == "/Transparency":
                result.has_transparency = True
            _inspect_resources(xobject.get("/Resources"), result, seen_forms, reader)
            _inspect_content(xobject, result, reader)


def _inspect_content(content: Any, result: PdfInspection, reader: PdfReader) -> None:
    if content is None:
        return
    stream = content if isinstance(content, ContentStream) else ContentStream(content, reader)
    for operands, operator in stream.operations:
        if operator in (b"rg", b"RG"):
            result.color_spaces.add("DeviceRGB")
        elif operator in (b"k", b"K"):
            result.color_spaces.add("DeviceCMYK")
        elif operator in (b"g", b"G"):
            result.color_spaces.add("DeviceGray")
        elif operator in (b"cs", b"CS") and operands:
            result.color_spaces.add(str(operands[-1]).removeprefix("/"))


def _record_color_space(value: Any, result: PdfInspection) -> None:
    resolved = _resolve(value)
    if isinstance(resolved, NameObject):
        result.color_spaces.add(str(resolved).removeprefix("/"))
    elif isinstance(resolved, ArrayObject) and resolved:
        family = str(resolved[0]).removeprefix("/")
        result.color_spaces.add(family)
        if family == "ICCBased" and len(resolved) > 1:
            profile = _resolve(resolved[1])
            if isinstance(profile, DictionaryObject):
                components = profile.get("/N")
                if isinstance(components, int):
                    result.icc_profile_components.append(components)
                alternate = profile.get("/Alternate")
                if alternate is not None:
                    _record_color_space(alternate, result)
        elif family in ("Indexed", "Separation", "DeviceN"):
            for item in resolved[1:]:
                if isinstance(_resolve(item), (NameObject, ArrayObject)):
                    _record_color_space(item, result)


def _font_embedded(font: DictionaryObject) -> bool:
    descriptor = _resolve(font.get("/FontDescriptor"))
    if descriptor is None:
        descendants = _resolve(font.get("/DescendantFonts"))
        if isinstance(descendants, ArrayObject):
            return bool(descendants) and all(
                isinstance((descendant := _resolve(item)), DictionaryObject)
                and _font_embedded(descendant)
                for item in descendants
            )
        return False
    if not isinstance(descriptor, DictionaryObject):
        return False
    return any(descriptor.get(key) is not None for key in ("/FontFile", "/FontFile2", "/FontFile3"))


def _contains_forbidden_action(value: Any, seen: set[tuple[int, int]] | None = None) -> bool:
    seen = seen or set()
    identity = _identity(value)
    if identity is not None:
        if identity in seen:
            return False
        seen.add(identity)
    resolved = _resolve(value)
    if isinstance(resolved, DictionaryObject):
        if any(key in resolved for key in ("/OpenAction", "/AA", "/JavaScript", "/JS", "/Launch")):
            return True
        if str(resolved.get("/S")) in ("/JavaScript", "/Launch"):
            return True
        return any(_contains_forbidden_action(item, seen) for item in resolved.values())
    if isinstance(resolved, (ArrayObject, list, tuple)):
        return any(_contains_forbidden_action(item, seen) for item in resolved)
    return False


def _identity(value: Any) -> tuple[int, int] | None:
    if isinstance(value, IndirectObject):
        return value.idnum, value.generation
    return None


def _resolve(value: Any) -> Any:
    while isinstance(value, IndirectObject):
        value = value.get_object()
    return value
