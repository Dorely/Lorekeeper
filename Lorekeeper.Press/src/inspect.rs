use std::collections::{BTreeMap, BTreeSet};

use lopdf::content::Content;
use lopdf::{Dictionary, Document, Object, ObjectId, Stream};

use crate::protocol::{FontEvidence, PageBoxEvidence};

#[derive(Debug)]
pub struct PdfInspection {
    pub version: String,
    pub page_count: usize,
    pub page_width_points: f64,
    pub page_height_points: f64,
    pub page_boxes: PageBoxEvidence,
    pub page_box_mismatch_pages: Vec<usize>,
    pub fonts: Vec<FontEvidence>,
    pub color_spaces: Vec<String>,
    pub image_count: usize,
    pub annotation_count: usize,
    pub output_intent_count: usize,
    pub has_transparency: bool,
    pub has_encryption: bool,
    pub has_forbidden_actions: bool,
}

pub fn inspect_pdf(bytes: &[u8]) -> Result<PdfInspection, String> {
    let document = Document::load_mem(bytes).map_err(|error| error.to_string())?;
    let pages = document.get_pages();
    let (_, first_page_id) = pages
        .first_key_value()
        .ok_or_else(|| "PDF has no pages.".to_owned())?;
    let page = document
        .get_dictionary(*first_page_id)
        .map_err(|error| error.to_string())?;
    let page_boxes = page_boxes_for(&document, page);
    let media_box = page_boxes
        .media_box
        .ok_or_else(|| "First page has no MediaBox.".to_owned())?;
    let (width, height) = box_dimensions(&media_box);

    let mut fonts = BTreeMap::<String, bool>::new();
    let mut color_spaces = BTreeSet::<String>::new();
    let mut image_count = 0;
    let mut annotation_count = 0;
    let mut has_transparency = false;
    let mut page_box_mismatch_pages = Vec::new();
    let mut visited_x_objects = BTreeSet::new();

    for (page_number, page_id) in &pages {
        let page = document
            .get_dictionary(*page_id)
            .map_err(|error| error.to_string())?;
        if page_boxes_for(&document, page) != page_boxes {
            page_box_mismatch_pages.push(*page_number as usize);
        }
        annotation_count += page
            .get(b"Annots")
            .ok()
            .and_then(|value| dereference(&document, value).ok())
            .and_then(|value| value.as_array().ok())
            .map_or(0, Vec::len);
        inspect_page_resources(
            &document,
            *page_id,
            &mut fonts,
            &mut color_spaces,
            &mut image_count,
            &mut has_transparency,
            &mut visited_x_objects,
        )?;
        let content = document.get_page_content(*page_id);
        inspect_content(&content, &mut color_spaces)?;
    }

    let catalog = document.catalog().map_err(|error| error.to_string())?;
    let output_intent_count = catalog
        .get(b"OutputIntents")
        .ok()
        .and_then(|value| dereference(&document, value).ok())
        .and_then(|value| value.as_array().ok())
        .map_or(0, Vec::len);

    let mut visited_actions = BTreeSet::new();
    let has_forbidden_actions = document.objects.iter().any(|(id, object)| {
        visited_actions.insert(*id);
        object_has_forbidden_action(&document, object, &mut visited_actions)
    });

    Ok(PdfInspection {
        version: document.version,
        page_count: pages.len(),
        page_width_points: width,
        page_height_points: height,
        page_boxes,
        page_box_mismatch_pages,
        fonts: fonts
            .into_iter()
            .map(|(name, embedded)| FontEvidence { name, embedded })
            .collect(),
        color_spaces: color_spaces.into_iter().collect(),
        image_count,
        annotation_count,
        output_intent_count,
        has_transparency,
        has_encryption: document.trailer.has(b"Encrypt"),
        has_forbidden_actions,
    })
}

fn inspect_page_resources(
    document: &Document,
    page_id: ObjectId,
    fonts: &mut BTreeMap<String, bool>,
    color_spaces: &mut BTreeSet<String>,
    image_count: &mut usize,
    has_transparency: &mut bool,
    visited_x_objects: &mut BTreeSet<ObjectId>,
) -> Result<(), String> {
    let page = document
        .get_dictionary(page_id)
        .map_err(|error| error.to_string())?;
    let Some(resources) = inherited_object(document, page, b"Resources") else {
        return Ok(());
    };
    let resources = dereference(document, resources)
        .map_err(|error| error.to_string())?
        .as_dict()
        .map_err(|error| error.to_string())?;

    inspect_resources(
        document,
        resources,
        fonts,
        color_spaces,
        image_count,
        has_transparency,
        visited_x_objects,
    )
}

fn inspect_resources(
    document: &Document,
    resources: &Dictionary,
    fonts: &mut BTreeMap<String, bool>,
    color_spaces: &mut BTreeSet<String>,
    image_count: &mut usize,
    has_transparency: &mut bool,
    visited_x_objects: &mut BTreeSet<ObjectId>,
) -> Result<(), String> {
    if let Ok(font_dictionary) = resources.get(b"Font") {
        let font_dictionary = dereference(document, font_dictionary)
            .map_err(|error| error.to_string())?
            .as_dict()
            .map_err(|error| error.to_string())?;
        for value in font_dictionary.iter().map(|(_, value)| value) {
            let dictionary = dereference(document, value)
                .map_err(|error| error.to_string())?
                .as_dict()
                .map_err(|error| error.to_string())?;
            let name = dictionary
                .get(b"BaseFont")
                .ok()
                .and_then(|value| value.as_name().ok())
                .map(|name| String::from_utf8_lossy(name).into_owned())
                .unwrap_or_else(|| "Unknown".to_owned());
            fonts
                .entry(name)
                .and_modify(|embedded| *embedded &= font_is_embedded(document, dictionary))
                .or_insert_with(|| font_is_embedded(document, dictionary));
        }
    }

    if let Ok(color_space_dictionary) = resources.get(b"ColorSpace") {
        let dictionary = dereference(document, color_space_dictionary)
            .map_err(|error| error.to_string())?
            .as_dict()
            .map_err(|error| error.to_string())?;
        for value in dictionary.iter().map(|(_, value)| value) {
            collect_color_space(document, value, color_spaces);
        }
    }

    inspect_x_objects(
        document,
        resources,
        fonts,
        color_spaces,
        image_count,
        has_transparency,
        visited_x_objects,
    )?;

    if let Ok(ext_g_state) = resources.get(b"ExtGState") {
        let dictionary = dereference(document, ext_g_state)
            .map_err(|error| error.to_string())?
            .as_dict()
            .map_err(|error| error.to_string())?;
        for value in dictionary.iter().map(|(_, value)| value) {
            if let Ok(state) = dereference(document, value).and_then(Object::as_dict)
                && (state.get(b"SMask").ok().is_some_and(is_non_none_name)
                    || number_is_less_than_one(state.get(b"ca").ok())
                    || number_is_less_than_one(state.get(b"CA").ok())
                    || state.get(b"BM").ok().is_some_and(is_non_normal_blend_mode))
            {
                *has_transparency = true;
            }
        }
    }

    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn inspect_x_objects(
    document: &Document,
    resources: &Dictionary,
    fonts: &mut BTreeMap<String, bool>,
    color_spaces: &mut BTreeSet<String>,
    image_count: &mut usize,
    has_transparency: &mut bool,
    visited_x_objects: &mut BTreeSet<ObjectId>,
) -> Result<(), String> {
    let Ok(x_objects) = resources.get(b"XObject") else {
        return Ok(());
    };
    let dictionary = dereference(document, x_objects)
        .map_err(|error| error.to_string())?
        .as_dict()
        .map_err(|error| error.to_string())?;

    for value in dictionary.iter().map(|(_, value)| value) {
        if let Object::Reference(id) = value
            && !visited_x_objects.insert(*id)
        {
            continue;
        }
        let object = dereference(document, value).map_err(|error| error.to_string())?;
        let Ok(stream) = object.as_stream() else {
            continue;
        };
        match stream
            .dict
            .get(b"Subtype")
            .ok()
            .and_then(|value| value.as_name().ok())
        {
            Some(b"Image") => {
                *image_count += 1;
                if let Ok(color_space) = stream.dict.get(b"ColorSpace") {
                    collect_color_space(document, color_space, color_spaces);
                }
                if stream.dict.has(b"SMask") {
                    *has_transparency = true;
                }
            }
            Some(b"Form") => {
                inspect_transparency_group(document, stream, has_transparency);
                let content = stream
                    .get_plain_content_with_limit(32 * 1024 * 1024)
                    .map_err(|error| error.to_string())?;
                inspect_content(&content, color_spaces)?;
                if let Ok(form_resources) = stream.dict.get(b"Resources") {
                    let form_resources = dereference(document, form_resources)
                        .map_err(|error| error.to_string())?
                        .as_dict()
                        .map_err(|error| error.to_string())?;
                    inspect_resources(
                        document,
                        form_resources,
                        fonts,
                        color_spaces,
                        image_count,
                        has_transparency,
                        visited_x_objects,
                    )?;
                }
            }
            _ => {}
        }
    }

    Ok(())
}

fn inspect_transparency_group(document: &Document, stream: &Stream, has_transparency: &mut bool) {
    if let Ok(group) = stream.dict.get(b"Group")
        && let Ok(group) = dereference(document, group).and_then(Object::as_dict)
        && group.get(b"S").ok().and_then(|value| value.as_name().ok()) == Some(b"Transparency")
    {
        *has_transparency = true;
    }
}

fn inspect_content(bytes: &[u8], color_spaces: &mut BTreeSet<String>) -> Result<(), String> {
    let content = Content::decode(bytes).map_err(|error| error.to_string())?;
    for operation in content.operations {
        match operation.operator.as_str() {
            "rg" | "RG" => {
                color_spaces.insert("DeviceRGB".to_owned());
            }
            "k" | "K" => {
                color_spaces.insert("DeviceCMYK".to_owned());
            }
            "g" | "G" => {
                color_spaces.insert("DeviceGray".to_owned());
            }
            "cs" | "CS" => {
                if let Some(Object::Name(name)) = operation.operands.first()
                    && matches!(
                        name.as_slice(),
                        b"DeviceRGB" | b"DeviceCMYK" | b"DeviceGray"
                    )
                {
                    color_spaces.insert(String::from_utf8_lossy(name).into_owned());
                }
            }
            "gs" => {}
            _ => {}
        }
    }
    Ok(())
}

fn object_has_forbidden_action(
    document: &Document,
    object: &Object,
    visited: &mut BTreeSet<ObjectId>,
) -> bool {
    match object {
        Object::Reference(id) => {
            if !visited.insert(*id) {
                return false;
            }
            document
                .get_object(*id)
                .is_ok_and(|object| object_has_forbidden_action(document, object, visited))
        }
        Object::Array(values) => values
            .iter()
            .any(|value| object_has_forbidden_action(document, value, visited)),
        Object::Dictionary(dictionary) => {
            dictionary_has_forbidden_action(document, dictionary, visited)
        }
        Object::Stream(stream) => dictionary_has_forbidden_action(document, &stream.dict, visited),
        _ => false,
    }
}

fn dictionary_has_forbidden_action(
    document: &Document,
    dictionary: &Dictionary,
    visited: &mut BTreeSet<ObjectId>,
) -> bool {
    if dictionary_has_any(dictionary, &[b"OpenAction", b"AA"]) {
        return true;
    }
    if dictionary
        .get(b"S")
        .ok()
        .and_then(|value| value.as_name().ok())
        .is_some_and(|name| matches!(name, b"JavaScript" | b"Launch"))
    {
        return true;
    }
    dictionary
        .iter()
        .any(|(_, value)| object_has_forbidden_action(document, value, visited))
}

fn font_is_embedded(document: &Document, dictionary: &Dictionary) -> bool {
    if let Ok(descendant_fonts) = dictionary.get(b"DescendantFonts")
        && let Ok(array) = dereference(document, descendant_fonts).and_then(Object::as_array)
    {
        return array.iter().any(|font| {
            dereference(document, font)
                .and_then(Object::as_dict)
                .is_ok_and(|font| font_descriptor_is_embedded(document, font))
        });
    }

    font_descriptor_is_embedded(document, dictionary)
}

fn font_descriptor_is_embedded(document: &Document, dictionary: &Dictionary) -> bool {
    dictionary
        .get(b"FontDescriptor")
        .ok()
        .and_then(|value| dereference(document, value).ok())
        .and_then(|value| value.as_dict().ok())
        .is_some_and(|descriptor| {
            dictionary_has_any(descriptor, &[b"FontFile", b"FontFile2", b"FontFile3"])
        })
}

fn collect_color_space(document: &Document, value: &Object, found: &mut BTreeSet<String>) {
    let Ok(value) = dereference(document, value) else {
        return;
    };

    match value {
        Object::Name(name) => {
            found.insert(String::from_utf8_lossy(name).into_owned());
        }
        Object::Array(items) => {
            if let Some(Object::Name(name)) = items.first() {
                found.insert(String::from_utf8_lossy(name).into_owned());
            }
        }
        _ => {}
    }
}

fn inherited_object<'a>(
    document: &'a Document,
    dictionary: &'a Dictionary,
    key: &[u8],
) -> Option<&'a Object> {
    if let Ok(value) = dictionary.get(key) {
        return Some(value);
    }

    let parent = dictionary.get(b"Parent").ok()?;
    let parent = dereference(document, parent).ok()?.as_dict().ok()?;
    inherited_object(document, parent, key)
}

fn inherited_box(document: &Document, dictionary: &Dictionary, key: &[u8]) -> Option<[f64; 4]> {
    inherited_object(document, dictionary, key)
        .and_then(|value| dereference(document, value).ok())
        .and_then(|value| value.as_array().ok())
        .and_then(|values| {
            let numbers = values
                .iter()
                .map(object_number)
                .collect::<Result<Vec<_>, _>>()
                .ok()?;
            numbers.try_into().ok()
        })
}

fn page_boxes_for(document: &Document, page: &Dictionary) -> PageBoxEvidence {
    PageBoxEvidence {
        media_box: inherited_box(document, page, b"MediaBox"),
        crop_box: inherited_box(document, page, b"CropBox"),
        bleed_box: inherited_box(document, page, b"BleedBox"),
        trim_box: inherited_box(document, page, b"TrimBox"),
        art_box: inherited_box(document, page, b"ArtBox"),
    }
}

fn dereference<'a>(document: &'a Document, value: &'a Object) -> lopdf::Result<&'a Object> {
    match value {
        Object::Reference(id) => document.get_object(*id),
        _ => Ok(value),
    }
}

fn box_dimensions(values: &[f64; 4]) -> (f64, f64) {
    (values[2] - values[0], values[3] - values[1])
}

fn object_number(value: &Object) -> Result<f64, String> {
    match value {
        Object::Integer(value) => Ok(*value as f64),
        Object::Real(value) => Ok(*value as f64),
        _ => Err("Page box contains a non-numeric value.".to_owned()),
    }
}

fn number_is_less_than_one(value: Option<&Object>) -> bool {
    value
        .and_then(|value| object_number(value).ok())
        .is_some_and(|value| value < 1.0)
}

fn is_non_none_name(value: &Object) -> bool {
    !matches!(value.as_name(), Ok(name) if name == b"None")
}

fn is_non_normal_blend_mode(value: &Object) -> bool {
    match value {
        Object::Name(name) => !matches!(name.as_slice(), b"Normal" | b"Compatible"),
        Object::Array(values) => values.iter().any(is_non_normal_blend_mode),
        _ => true,
    }
}

fn dictionary_has_any(dictionary: &Dictionary, keys: &[&[u8]]) -> bool {
    keys.iter().any(|key| dictionary.has(key))
}
