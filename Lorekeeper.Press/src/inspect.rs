use std::path::Path;

use lopdf::content::Content;
use lopdf::{Dictionary, Document, Object};

use crate::model::Diagnostic;
use crate::pdf::PdfOptions;

#[derive(Debug, Clone, Default)]
pub struct InspectionEvidence {
    pub fonts_embedded: bool,
    pub to_unicode: bool,
    pub has_transparency: bool,
    pub annotation_count: usize,
    pub tagged: bool,
}

pub fn validate(path: &Path, expected: &PdfOptions) -> Result<InspectionEvidence, Diagnostic> {
    let document = Document::load(path).map_err(|error| {
        Diagnostic::error(
            "PRESS_PDF_INVALID",
            format!("The written PDF cannot be parsed: {error}"),
        )
    })?;
    let expected_version = if expected.pdf_x {
        "1.3"
    } else if expected.pdf_a {
        "1.4"
    } else {
        "1.7"
    };
    if document.version != expected_version {
        return Err(Diagnostic::error(
            "PRESS_PDF_VERSION_INVALID",
            format!(
                "Expected PDF {expected_version}, found PDF {}.",
                document.version
            ),
        ));
    }
    if document.trailer.has(b"Encrypt") {
        return Err(Diagnostic::error(
            "PRESS_PDF_ENCRYPTED",
            "Publication PDFs must not be encrypted.",
        ));
    }
    let catalog = document.catalog().map_err(|_| {
        Diagnostic::error("PRESS_PDF_CATALOG_INVALID", "The catalog is unreadable.")
    })?;
    if catalog.has(b"OpenAction") || catalog.has(b"AA") || catalog.has(b"AcroForm") {
        return Err(Diagnostic::error(
            "PRESS_PDF_ACTIONS_FORBIDDEN",
            "Publication PDFs cannot contain actions or interactive forms.",
        ));
    }
    if catalog
        .get(b"Names")
        .ok()
        .and_then(|value| dereference(&document, value))
        .and_then(|value| value.as_dict().ok())
        .is_some_and(|names| names.has(b"JavaScript") || names.has(b"EmbeddedFiles"))
    {
        return Err(Diagnostic::error(
            "PRESS_PDF_ACTIONS_FORBIDDEN",
            "Publication PDFs cannot contain JavaScript or embedded files.",
        ));
    }
    let pages = document.get_pages();
    if pages.is_empty() {
        return Err(Diagnostic::error(
            "PRESS_PDF_PAGE_TREE_INVALID",
            "The PDF has no pages.",
        ));
    }
    let mut saw_font = false;
    let mut fonts_embedded = true;
    let mut to_unicode = true;
    let mut has_transparency = false;
    let mut annotation_count = 0;
    for (page_index, page_id) in pages.values().enumerate() {
        let page = document.get_dictionary(*page_id).map_err(|_| {
            Diagnostic::error(
                "PRESS_PDF_PAGE_TREE_INVALID",
                "A page dictionary is unreadable.",
            )
        })?;
        let trim_x = if expected.interior_bleed > 0.0 && page_index % 2 == 1 {
            expected.interior_bleed
        } else {
            expected.trim.x1
        };
        for (key, expected_box) in [
            (
                b"MediaBox".as_slice(),
                [0.0, 0.0, expected.width, expected.height],
            ),
            (
                b"TrimBox".as_slice(),
                [
                    trim_x,
                    if expected.interior_bleed > 0.0 {
                        expected.interior_bleed
                    } else {
                        expected.trim.y1
                    },
                    if expected.interior_bleed > 0.0 {
                        trim_x + expected.trim_width
                    } else {
                        expected.trim.x2
                    },
                    if expected.interior_bleed > 0.0 {
                        expected.interior_bleed + expected.trim_height
                    } else {
                        expected.trim.y2
                    },
                ],
            ),
            (
                b"BleedBox".as_slice(),
                [
                    expected.bleed.x1,
                    expected.bleed.y1,
                    expected.bleed.x2,
                    expected.bleed.y2,
                ],
            ),
        ] {
            let actual = inherited(&document, page, key).and_then(object_rect);
            let valid_mixed_box = expected.allow_mixed_page_boxes
                && actual.is_some_and(|actual| {
                    actual[0].abs() < 0.01
                        && actual[1].abs() < 0.01
                        && actual[2] > 72.0
                        && actual[3] > 72.0
                        && actual[2] <= 2_880.0
                        && actual[3] <= 2_880.0
                });
            if !valid_mixed_box && actual.is_none_or(|actual| !rect_matches(actual, expected_box)) {
                return Err(Diagnostic::error(
                    "PRESS_PDF_PAGE_BOX_INVALID",
                    format!(
                        "A page has an invalid {} for the requested geometry.",
                        String::from_utf8_lossy(key)
                    ),
                ));
            }
        }
        if page.has(b"AA") {
            return Err(Diagnostic::error(
                "PRESS_PDF_ANNOTATIONS_FORBIDDEN",
                "Publication PDFs cannot contain page-level additional actions.",
            ));
        }
        if page.has(b"Annots") && (!expected.tagged || !valid_internal_links(&document, page)) {
            return Err(Diagnostic::error(
                "PRESS_PDF_ANNOTATIONS_FORBIDDEN",
                "Only bounded internal GoTo links are allowed in Digital PDFs.",
            ));
        }
        annotation_count += page
            .get(b"Annots")
            .ok()
            .and_then(|value| dereference(&document, value))
            .and_then(|value| value.as_array().ok())
            .map_or(0, Vec::len);
        if let Some(resources) = inherited(&document, page, b"Resources")
            .and_then(|value| dereference(&document, value))
            .and_then(|value| value.as_dict().ok())
        {
            has_transparency |= validate_resources(
                &document,
                resources,
                expected.pdf_x,
                expected.expected_image_color_space,
            )?;
            if let Some(fonts) = resources
                .get(b"Font")
                .ok()
                .and_then(|value| dereference(&document, value))
                .and_then(|value| value.as_dict().ok())
            {
                for (_, value) in fonts.iter() {
                    let Some(font) =
                        dereference(&document, value).and_then(|value| value.as_dict().ok())
                    else {
                        continue;
                    };
                    saw_font = true;
                    to_unicode &= font.has(b"ToUnicode");
                    fonts_embedded &= descendant_is_embedded(&document, font);
                }
            }
        }
        if expected.pdf_x {
            let content = document.get_page_content(*page_id);
            let text = String::from_utf8_lossy(&content);
            if text.contains(" rg") || text.contains(" RG") {
                return Err(Diagnostic::error(
                    "PRESS_PDFX_RGB_FORBIDDEN",
                    "PDF/X-1a content uses an RGB operator.",
                ));
            }
            let operations = Content::decode(&content).map_err(|_| {
                Diagnostic::error(
                    "PRESS_PDF_CONTENT_INVALID",
                    "A PDF page content stream could not be parsed.",
                )
            })?;
            if operations.operations.iter().any(|operation| {
                matches!(operation.operator.as_str(), "k" | "K")
                    && operation.operands.len() == 4
                    && operation
                        .operands
                        .iter()
                        .filter_map(|value| value.as_float().ok())
                        .sum::<f32>()
                        > 2.400_01
            }) {
                return Err(Diagnostic::error(
                    "PRESS_TOTAL_INK_EXCEEDED",
                    "A PDF/X-1a page-paint color exceeds the 240% total-ink ceiling.",
                ));
            }
        }
    }
    if expected.tagged {
        validate_tagged_pdf(&document, catalog, &pages)?;
    }
    if !saw_font || !fonts_embedded || !to_unicode {
        return Err(Diagnostic::error(
            "PRESS_PDF_FONT_INVALID",
            "Every used font must be embedded and expose a ToUnicode map.",
        ));
    }
    if expected.pdf_x {
        let output_intents = catalog
            .get(b"OutputIntents")
            .ok()
            .and_then(|value| dereference(&document, value))
            .and_then(|value| value.as_array().ok());
        if output_intents.is_none_or(Vec::is_empty) {
            return Err(Diagnostic::error(
                "PRESS_PDFX_OUTPUT_INTENT_MISSING",
                "PDF/X-1a requires an embedded CMYK output intent.",
            ));
        }
        let valid_cmyk_intent = output_intents.is_some_and(|intents| {
            intents.iter().any(|value| {
                dereference(&document, value)
                    .and_then(|value| value.as_dict().ok())
                    .filter(|intent| {
                        intent.get(b"S").ok().and_then(|value| value.as_name().ok())
                            == Some(b"GTS_PDFX")
                    })
                    .and_then(|intent| intent.get(b"DestOutputProfile").ok())
                    .and_then(|value| dereference(&document, value))
                    .and_then(|value| value.as_stream().ok())
                    .is_some_and(|profile| {
                        profile
                            .dict
                            .get(b"N")
                            .ok()
                            .and_then(|value| value.as_i64().ok())
                            == Some(4)
                            && !profile.content.is_empty()
                    })
            })
        });
        if !valid_cmyk_intent {
            return Err(Diagnostic::error(
                "PRESS_PDFX_OUTPUT_INTENT_INVALID",
                "PDF/X-1a requires an embedded four-channel CMYK output profile.",
            ));
        }
        let pdfx = document.trailer.get(b"Info").ok()
            .and_then(|value| dereference(&document, value))
            .and_then(|value| value.as_dict().ok())
            .and_then(|info| info.get(b"GTS_PDFXVersion").ok())
            .is_some_and(|value| matches!(value, Object::String(bytes, _) if String::from_utf8_lossy(bytes).contains("PDF/X-1a:2001")));
        if !pdfx {
            return Err(Diagnostic::error(
                "PRESS_PDFX_METADATA_INVALID",
                "PDF/X-1a identification metadata is missing.",
            ));
        }
    }
    if expected.pdf_a {
        validate_pdfa(&document, catalog)?;
    }
    Ok(InspectionEvidence {
        fonts_embedded,
        to_unicode,
        has_transparency,
        annotation_count,
        tagged: expected.tagged,
    })
}

fn validate_pdfa(document: &Document, catalog: &Dictionary) -> Result<(), Diagnostic> {
    let metadata = catalog
        .get(b"Metadata")
        .ok()
        .and_then(|value| dereference(document, value))
        .and_then(|value| value.as_stream().ok())
        .map(|stream| String::from_utf8_lossy(&stream.content));
    if metadata.as_ref().is_none_or(|value| {
        !value.contains("pdfaid:part=\"1\"") || !value.contains("pdfaid:conformance=\"B\"")
    }) {
        return Err(Diagnostic::error(
            "PRESS_PDFA_METADATA_INVALID",
            "PDF/A-1b identification metadata is missing.",
        ));
    }
    let valid_intent = catalog
        .get(b"OutputIntents")
        .ok()
        .and_then(|value| dereference(document, value))
        .and_then(|value| value.as_array().ok())
        .is_some_and(|intents| {
            intents.iter().any(|value| {
                dereference(document, value)
                    .and_then(|value| value.as_dict().ok())
                    .filter(|intent| {
                        intent.get(b"S").ok().and_then(|value| value.as_name().ok())
                            == Some(b"GTS_PDFA1")
                    })
                    .and_then(|intent| intent.get(b"DestOutputProfile").ok())
                    .and_then(|value| dereference(document, value))
                    .and_then(|value| value.as_stream().ok())
                    .is_some_and(|profile| !profile.content.is_empty())
            })
        });
    if !valid_intent {
        return Err(Diagnostic::error(
            "PRESS_PDFA_OUTPUT_INTENT_INVALID",
            "PDF/A-1b requires an embedded output intent.",
        ));
    }
    Ok(())
}

fn valid_internal_links(document: &Document, page: &Dictionary) -> bool {
    let Some(annotations) = page
        .get(b"Annots")
        .ok()
        .and_then(|value| dereference(document, value))
        .and_then(|value| value.as_array().ok())
    else {
        return false;
    };
    !annotations.is_empty()
        && annotations.iter().all(|value| {
            let Some(annotation) =
                dereference(document, value).and_then(|value| value.as_dict().ok())
            else {
                return false;
            };
            if annotation
                .get(b"Subtype")
                .ok()
                .and_then(|value| value.as_name().ok())
                != Some(b"Link".as_slice())
            {
                return false;
            }
            let Some(action) = annotation
                .get(b"A")
                .ok()
                .and_then(|value| dereference(document, value))
                .and_then(|value| value.as_dict().ok())
            else {
                return false;
            };
            action.get(b"S").ok().and_then(|value| value.as_name().ok()) == Some(b"GoTo".as_slice())
                && action.has(b"D")
                && !action.has(b"URI")
                && annotation
                    .get(b"StructParent")
                    .ok()
                    .and_then(|value| value.as_i64().ok())
                    .is_some()
        })
}

fn validate_resources(
    document: &Document,
    resources: &Dictionary,
    pdf_x: bool,
    expected_image_color_space: crate::pdf::ImageColorSpace,
) -> Result<bool, Diagnostic> {
    let mut has_transparency = resources.has(b"ExtGState");
    if pdf_x && has_transparency {
        return Err(Diagnostic::error(
            "PRESS_PDF_TRANSPARENCY_FORBIDDEN",
            "Transparency is forbidden in PDF/X-1a output.",
        ));
    }
    let Some(xobjects) = resources
        .get(b"XObject")
        .ok()
        .and_then(|value| dereference(document, value))
        .and_then(|value| value.as_dict().ok())
    else {
        return Ok(has_transparency);
    };
    for (_, value) in xobjects.iter() {
        let Some(stream) = dereference(document, value).and_then(|value| value.as_stream().ok())
        else {
            continue;
        };
        if stream.dict.has(b"SMask") || stream.dict.has(b"Mask") || stream.dict.has(b"Group") {
            return Err(Diagnostic::error(
                "PRESS_PDF_TRANSPARENCY_FORBIDDEN",
                "Publication PDF XObjects cannot contain transparency masks or groups.",
            ));
        }
        if stream
            .dict
            .get(b"Subtype")
            .ok()
            .and_then(|value| value.as_name().ok())
            == Some(b"Image")
        {
            let actual = stream
                .dict
                .get(b"ColorSpace")
                .ok()
                .and_then(|value| value.as_name().ok());
            let expected = match expected_image_color_space {
                crate::pdf::ImageColorSpace::Gray => b"DeviceGray".as_slice(),
                crate::pdf::ImageColorSpace::Rgb => b"DeviceRGB".as_slice(),
                crate::pdf::ImageColorSpace::Cmyk => b"DeviceCMYK".as_slice(),
            };
            if actual != Some(expected) && !(pdf_x && actual == Some(b"DeviceRGB")) {
                return Err(Diagnostic::error(
                    "PRESS_IMAGE_COLOR_SPACE_INVALID",
                    "A rendered image does not match the edition ink and artifact color intent.",
                ));
            }
        }
        if pdf_x
            && stream
                .dict
                .get(b"ColorSpace")
                .ok()
                .is_some_and(|value| color_space_is_rgb(document, value))
        {
            return Err(Diagnostic::error(
                "PRESS_PDFX_RGB_FORBIDDEN",
                "PDF/X-1a contains an RGB image or form XObject.",
            ));
        }
        if pdf_x
            && stream
                .dict
                .get(b"Subtype")
                .ok()
                .and_then(|value| value.as_name().ok())
                == Some(b"Image")
            && stream
                .dict
                .get(b"ColorSpace")
                .ok()
                .is_some_and(|value| color_space_is_cmyk(document, value))
            && stream
                .dict
                .get(b"BitsPerComponent")
                .ok()
                .and_then(|value| value.as_i64().ok())
                == Some(8)
        {
            let samples = stream
                .decompressed_content_with_limit(256 * 1024 * 1024)
                .map_err(|_| {
                    Diagnostic::error(
                        "PRESS_PDF_IMAGE_INVALID",
                        "A CMYK image stream could not be decoded safely.",
                    )
                })?;
            if samples
                .chunks_exact(4)
                .any(|pixel| pixel.iter().map(|channel| *channel as u32).sum::<u32>() > 612)
            {
                return Err(Diagnostic::error(
                    "PRESS_TOTAL_INK_EXCEEDED",
                    "A PDF/X-1a image exceeds the 240% total-ink ceiling.",
                ));
            }
        }
        if let Ok(nested) = stream.dict.get(b"Resources")
            && let Some(nested) =
                dereference(document, nested).and_then(|value| value.as_dict().ok())
        {
            has_transparency |=
                validate_resources(document, nested, pdf_x, expected_image_color_space)?;
        }
    }
    Ok(has_transparency)
}

fn validate_tagged_pdf(
    document: &Document,
    catalog: &Dictionary,
    pages: &std::collections::BTreeMap<u32, lopdf::ObjectId>,
) -> Result<(), Diagnostic> {
    let marked = catalog
        .get(b"MarkInfo")
        .ok()
        .and_then(|value| dereference(document, value))
        .and_then(|value| value.as_dict().ok())
        .and_then(|value| value.get(b"Marked").ok())
        .and_then(|value| value.as_bool().ok())
        == Some(true);
    let has_language = catalog
        .get(b"Lang")
        .ok()
        .and_then(|value| value.as_str().ok())
        .is_some_and(|value| !value.is_empty());
    let Some(root) = catalog
        .get(b"StructTreeRoot")
        .ok()
        .and_then(|value| dereference(document, value))
        .and_then(|value| value.as_dict().ok())
    else {
        return Err(Diagnostic::error(
            "PRESS_TAG_TREE_MISSING",
            "A tagged Digital PDF requires a readable StructTreeRoot.",
        ));
    };
    if !marked || !has_language || !root.has(b"ParentTree") || !root.has(b"K") {
        return Err(Diagnostic::error(
            "PRESS_TAG_TREE_INVALID",
            "The tagged PDF catalog, language, structure root, or parent tree is incomplete.",
        ));
    }
    let parent_tree = root
        .get(b"ParentTree")
        .ok()
        .and_then(|value| dereference(document, value))
        .and_then(|value| value.as_dict().ok())
        .ok_or_else(|| {
            Diagnostic::error(
                "PRESS_TAG_PARENT_TREE_INVALID",
                "The tagged PDF parent tree is unreadable.",
            )
        })?;
    let nums = parent_tree
        .get(b"Nums")
        .ok()
        .and_then(|value| dereference(document, value))
        .and_then(|value| value.as_array().ok())
        .ok_or_else(|| {
            Diagnostic::error(
                "PRESS_TAG_PARENT_TREE_INVALID",
                "The tagged PDF parent tree has no number tree entries.",
            )
        })?;
    for (page_index, page_id) in pages.values().enumerate() {
        let page = document.get_dictionary(*page_id).map_err(|_| {
            Diagnostic::error("PRESS_TAG_PAGE_INVALID", "A tagged page is unreadable.")
        })?;
        if page
            .get(b"StructParents")
            .ok()
            .and_then(|value| value.as_i64().ok())
            != Some(page_index as i64)
        {
            return Err(Diagnostic::error(
                "PRESS_TAG_PARENT_TREE_INVALID",
                "A tagged page is not connected to its parent-tree entry.",
            ));
        }
        let parent_array = nums
            .chunks_exact(2)
            .find(|pair| pair[0].as_i64().ok() == Some(page_index as i64))
            .and_then(|pair| dereference(document, &pair[1]))
            .and_then(|value| value.as_array().ok())
            .ok_or_else(|| {
                Diagnostic::error(
                    "PRESS_TAG_PARENT_TREE_INVALID",
                    "A tagged page has no matching parent-tree array.",
                )
            })?;
        let content = document.get_page_content(*page_id);
        let operations = Content::decode(&content).map_err(|_| {
            Diagnostic::error(
                "PRESS_TAG_CONTENT_INVALID",
                "A tagged page content stream could not be parsed.",
            )
        })?;
        let mut mcids = std::collections::BTreeSet::new();
        for operation in &operations.operations {
            if operation.operator == "BDC"
                && let Some(properties) = operation.operands.get(1)
                && let Some(dictionary) =
                    dereference(document, properties).and_then(|value| value.as_dict().ok())
                && let Some(mcid) = dictionary
                    .get(b"MCID")
                    .ok()
                    .and_then(|value| value.as_i64().ok())
            {
                mcids.insert(mcid as usize);
            }
        }
        for mcid in mcids {
            let Some(element) = parent_array
                .get(mcid)
                .and_then(|value| dereference(document, value))
                .and_then(|value| value.as_dict().ok())
            else {
                return Err(Diagnostic::error(
                    "PRESS_TAG_PARENT_TREE_INVALID",
                    "A marked-content ID has no structure element.",
                ));
            };
            let role = element
                .get(b"S")
                .ok()
                .and_then(|value| value.as_name().ok());
            if role.is_none() || (role == Some(b"Figure") && !element.has(b"Alt")) {
                return Err(Diagnostic::error(
                    "PRESS_TAG_ELEMENT_INVALID",
                    format!(
                        "Page {} MCID {} has no semantic role or a Figure lacks alternative text (role={}, alt={}).",
                        page_index + 1,
                        mcid,
                        role.map_or("missing".to_owned(), |value| String::from_utf8_lossy(value)
                            .into_owned()),
                        element.has(b"Alt")
                    ),
                ));
            }
        }
        if let Some(annotations) = page
            .get(b"Annots")
            .ok()
            .and_then(|value| dereference(document, value))
            .and_then(|value| value.as_array().ok())
        {
            for annotation_reference in annotations {
                let annotation_id = annotation_reference.as_reference().map_err(|_| {
                    Diagnostic::error(
                        "PRESS_TAG_ANNOTATION_INVALID",
                        "A tagged link annotation must be an indirect object.",
                    )
                })?;
                let annotation = document
                    .get_object(annotation_id)
                    .ok()
                    .and_then(|value| value.as_dict().ok())
                    .ok_or_else(|| {
                        Diagnostic::error(
                            "PRESS_TAG_ANNOTATION_INVALID",
                            "A tagged link annotation is unreadable.",
                        )
                    })?;
                let parent_key = annotation
                    .get(b"StructParent")
                    .ok()
                    .and_then(|value| value.as_i64().ok())
                    .ok_or_else(|| {
                        Diagnostic::error(
                            "PRESS_TAG_ANNOTATION_INVALID",
                            "A tagged link annotation has no StructParent key.",
                        )
                    })?;
                let structure_element = nums
                    .chunks_exact(2)
                    .find(|pair| pair[0].as_i64().ok() == Some(parent_key))
                    .and_then(|pair| dereference(document, &pair[1]))
                    .and_then(|value| value.as_dict().ok())
                    .ok_or_else(|| {
                        Diagnostic::error(
                            "PRESS_TAG_PARENT_TREE_INVALID",
                            "A tagged link annotation has no parent-tree structure element.",
                        )
                    })?;
                if !structure_element_references_annotation(
                    document,
                    structure_element,
                    annotation_id,
                ) {
                    return Err(Diagnostic::error(
                        "PRESS_TAG_ANNOTATION_INVALID",
                        "A tagged link annotation is not represented by an OBJR structure child.",
                    ));
                }
            }
        }
    }
    Ok(())
}

fn structure_element_references_annotation(
    document: &Document,
    structure_element: &Dictionary,
    annotation_id: lopdf::ObjectId,
) -> bool {
    structure_element
        .get(b"K")
        .ok()
        .is_some_and(|value| object_references_annotation(document, value, annotation_id))
}

fn object_references_annotation(
    document: &Document,
    value: &Object,
    annotation_id: lopdf::ObjectId,
) -> bool {
    let Some(value) = dereference(document, value) else {
        return false;
    };
    match value {
        Object::Array(values) => values
            .iter()
            .any(|value| object_references_annotation(document, value, annotation_id)),
        Object::Dictionary(dictionary) => {
            dictionary
                .get(b"Type")
                .ok()
                .and_then(|value| value.as_name().ok())
                == Some(b"OBJR".as_slice())
                && dictionary
                    .get(b"Obj")
                    .ok()
                    .and_then(|value| value.as_reference().ok())
                    == Some(annotation_id)
        }
        _ => false,
    }
}

fn color_space_is_rgb(document: &Document, value: &Object) -> bool {
    let Some(value) = dereference(document, value) else {
        return false;
    };
    match value {
        Object::Name(name) => name == b"DeviceRGB" || name == b"CalRGB",
        Object::Array(values) => values
            .iter()
            .any(|value| color_space_is_rgb(document, value)),
        _ => false,
    }
}

fn color_space_is_cmyk(document: &Document, value: &Object) -> bool {
    dereference(document, value)
        .is_some_and(|value| matches!(value, Object::Name(name) if name == b"DeviceCMYK"))
}

fn object_rect(value: &Object) -> Option<[f32; 4]> {
    let values = value.as_array().ok()?;
    if values.len() != 4 {
        return None;
    }
    Some([
        values[0].as_float().ok()?,
        values[1].as_float().ok()?,
        values[2].as_float().ok()?,
        values[3].as_float().ok()?,
    ])
}

fn rect_matches(actual: [f32; 4], expected: [f32; 4]) -> bool {
    actual
        .into_iter()
        .zip(expected)
        .all(|(actual, expected)| (actual - expected).abs() < 0.01)
}

fn descendant_is_embedded(document: &Document, font: &lopdf::Dictionary) -> bool {
    font.get(b"DescendantFonts")
        .ok()
        .and_then(|value| dereference(document, value))
        .and_then(|value| value.as_array().ok())
        .is_some_and(|descendants| {
            descendants.iter().any(|value| {
                dereference(document, value)
                    .and_then(|value| value.as_dict().ok())
                    .and_then(|descendant| descendant.get(b"FontDescriptor").ok())
                    .and_then(|value| dereference(document, value))
                    .and_then(|value| value.as_dict().ok())
                    .is_some_and(|descriptor| {
                        descriptor.has(b"FontFile2") || descriptor.has(b"FontFile3")
                    })
            })
        })
}

fn inherited<'a>(
    document: &'a Document,
    dictionary: &'a lopdf::Dictionary,
    key: &[u8],
) -> Option<&'a Object> {
    if let Ok(value) = dictionary.get(key) {
        return Some(value);
    }
    dictionary
        .get(b"Parent")
        .ok()
        .and_then(|value| dereference(document, value))
        .and_then(|value| value.as_dict().ok())
        .and_then(|parent| inherited(document, parent, key))
}

fn dereference<'a>(document: &'a Document, value: &'a Object) -> Option<&'a Object> {
    match value {
        Object::Reference(id) => document.get_object(*id).ok(),
        _ => Some(value),
    }
}

#[cfg(test)]
mod tests {
    use std::collections::BTreeMap;
    use std::fs;
    use std::path::PathBuf;

    use lopdf::{Dictionary, Stream};
    use pdf_writer::Rect;
    use tempfile::TempDir;

    use super::*;
    use crate::font::subset_for_text;
    use crate::model::{FontFace, LayoutLine, LayoutPage, PageKind};
    use crate::pdf::write_pdf;

    #[test]
    fn post_write_inspector_rejects_wrong_geometry_and_actions() {
        let (root, path, options) = valid_pdf(false);
        let mut document = Document::load(&path).expect("load");
        let page_id = *document.get_pages().values().next().expect("page");
        document
            .get_dictionary_mut(page_id)
            .expect("page dictionary")
            .set("MediaBox", vec![0.into(), 0.into(), 100.into(), 100.into()]);
        document.save(&path).expect("save bad box");
        assert_eq!(
            validate(&path, &options).unwrap_err().code.as_ref(),
            "PRESS_PDF_PAGE_BOX_INVALID"
        );

        let (_action_root, action_path, action_options) = valid_pdf(false);
        let mut document = Document::load(&action_path).expect("load");
        document
            .catalog_mut()
            .expect("catalog")
            .set("OpenAction", Object::Null);
        document.save(&action_path).expect("save action");
        assert_eq!(
            validate(&action_path, &action_options)
                .unwrap_err()
                .code
                .as_ref(),
            "PRESS_PDF_ACTIONS_FORBIDDEN"
        );
        drop(root);
    }

    #[test]
    fn post_write_inspector_rejects_rgb_and_excessive_ink_xobjects() {
        let (_rgb_root, rgb_path, options) = valid_pdf(true);
        inject_image(&rgb_path, b"DeviceRGB", vec![255, 0, 0]);
        assert_eq!(
            validate(&rgb_path, &options).unwrap_err().code.as_ref(),
            "PRESS_PDFX_RGB_FORBIDDEN"
        );

        let (_ink_root, ink_path, options) = valid_pdf(true);
        inject_image(&ink_path, b"DeviceCMYK", vec![255, 255, 255, 255]);
        assert_eq!(
            validate(&ink_path, &options).unwrap_err().code.as_ref(),
            "PRESS_TOTAL_INK_EXCEEDED"
        );

        let (_content_root, content_path, options) = valid_pdf(true);
        append_page_content(&content_path, b"\n1 1 1 1 k\n");
        assert_eq!(
            validate(&content_path, &options).unwrap_err().code.as_ref(),
            "PRESS_TOTAL_INK_EXCEEDED"
        );
    }

    fn valid_pdf(pdf_x: bool) -> (TempDir, PathBuf, PdfOptions) {
        let root = tempfile::tempdir().expect("temporary directory");
        let path = root.path().join("candidate.pdf");
        let options = PdfOptions {
            pdf_x,
            pdf_a: false,
            flatten_transparency: pdf_x,
            width: 432.0,
            height: 648.0,
            trim: Rect::new(0.0, 0.0, 432.0, 648.0),
            bleed: Rect::new(0.0, 0.0, 432.0, 648.0),
            title: "Inspector fixture".to_owned(),
            author: "Lorekeeper".to_owned(),
            background_rgb: None,
            tagged: false,
            language: "en".to_owned(),
            allow_mixed_page_boxes: false,
            trim_width: 432.0,
            trim_height: 648.0,
            interior_bleed: 0.0,
            expected_image_color_space: if pdf_x {
                crate::pdf::ImageColorSpace::Cmyk
            } else {
                crate::pdf::ImageColorSpace::Rgb
            },
        };
        let font = subset_for_text("Inspector fixture").expect("font");
        let fonts = BTreeMap::from([(FontFace::SerifRegular, font)]);
        let page = LayoutPage {
            kind: PageKind::Body,
            width_points: None,
            height_points: None,
            lines: vec![LayoutLine {
                text: "Inspector fixture".to_owned(),
                runs: Vec::new(),
                size: 11.0,
                x: 54.0,
                y: 594.0,
                baseline_offset_points: crate::font::descent_points(FontFace::SerifRegular, 11.0),
                word_spacing: 0.0,
                character_spacing: 0.0,
                rotation_degrees: 0.0,
                rotation_origin_x: None,
                rotation_origin_y: None,
                opacity: 1.0,
                light_text: false,
                fill_rgb: None,
                semantic_role: crate::model::LayoutSemanticRole::Paragraph,
                artifact: false,
                language: None,
                reading_order: None,
                semantic_id: None,
                semantic_parent_id: None,
                source_start_utf16: None,
                source_end_utf16: None,
                link_page: None,
            }],
            images: Vec::new(),
            shapes: Vec::new(),
            paint_order: Vec::new(),
            barcode_modules: None,
            page_label: Some("1".to_owned()),
            bookmark: None,
        };
        let bytes = write_pdf(&[page], &fonts, &BTreeMap::new(), &options).expect("write");
        fs::write(&path, bytes).expect("fixture PDF");
        (root, path, options)
    }

    fn inject_image(path: &Path, color_space: &[u8], samples: Vec<u8>) {
        let mut document = Document::load(path).expect("load");
        let image_id = document.new_object_id();
        let mut dictionary = Dictionary::new();
        dictionary.set("Type", Object::Name(b"XObject".to_vec()));
        dictionary.set("Subtype", Object::Name(b"Image".to_vec()));
        dictionary.set("Width", 1);
        dictionary.set("Height", 1);
        dictionary.set("ColorSpace", Object::Name(color_space.to_vec()));
        dictionary.set("BitsPerComponent", 8);
        document
            .objects
            .insert(image_id, Object::Stream(Stream::new(dictionary, samples)));
        let page_id = *document.get_pages().values().next().expect("page");
        let page = document
            .get_dictionary_mut(page_id)
            .expect("page dictionary");
        let resources = page
            .get_mut(b"Resources")
            .expect("resources")
            .as_dict_mut()
            .expect("resource dictionary");
        resources
            .get_mut(b"XObject")
            .expect("xobjects")
            .as_dict_mut()
            .expect("xobject dictionary")
            .set("Injected", Object::Reference(image_id));
        document.save(path).expect("save malformed PDF");
    }

    fn append_page_content(path: &Path, suffix: &[u8]) {
        let mut document = Document::load(path).expect("load");
        let page_id = *document.get_pages().values().next().expect("page");
        let content_id = document
            .get_dictionary(page_id)
            .expect("page")
            .get(b"Contents")
            .expect("contents")
            .as_reference()
            .expect("content reference");
        let stream = document
            .get_object_mut(content_id)
            .expect("content")
            .as_stream_mut()
            .expect("content stream");
        let mut bytes = stream.decompressed_content().expect("decode content");
        bytes.extend_from_slice(suffix);
        stream.set_plain_content(bytes);
        document.save(path).expect("save content");
    }
}
