use std::collections::BTreeMap;
use std::io::Write;

use flate2::Compression;
use flate2::write::ZlibEncoder;
use pdf_writer::types::{
    ActionType, AnnotationType, CidFontType, FontFlags, OutputIntentSubtype, StructRole,
    SystemInfo, UnicodeCmap,
};
use pdf_writer::writers::StructTreeRoot;
use pdf_writer::{Content, Filter, Finish, Name, Pdf, Rect, Ref, Str, TextStr};

use crate::font::{EmbeddedFont, OutlineEdge};
use crate::image::EmbeddedImage;
use crate::model::{
    Diagnostic, FontFace, LayoutImageFit, LayoutLine, LayoutPage, LayoutPaint, LayoutRun,
    LayoutSemanticRole, LayoutShapeKind, PageKind, RenderRequest,
};

#[derive(Debug)]
struct PageTags {
    image_mcids: Vec<Option<i32>>,
    line_mcids: Vec<Option<i32>>,
    elements: Vec<TagElement>,
}

#[derive(Debug, Clone)]
struct TagElement {
    reference: Ref,
    mcids: Vec<(usize, i32)>,
    role: StructRole,
    alt_text: Option<String>,
    language: Option<String>,
    reading_order: i32,
    semantic_id: String,
    parent_semantic_id: Option<String>,
    annotation_ref: Option<(usize, Ref)>,
    parent: Option<Ref>,
}

const ICC_PROFILE: &[u8] = include_bytes!("../assets/profiles/CGATS21_CRPC1.icc");
const PAGE_TAG_OBJECT_BASE: i32 = 100_000;
const PAGE_TAG_OBJECT_STRIDE: i32 = 4_000;
const PAGE_TAG_ITEM_LIMIT: usize = 1_000;
const LIST_PARENT_OFFSET: i32 = 1_000;
const TOC_PARENT_OFFSET: i32 = 2_000;
const ANNOTATION_OFFSET: i32 = 3_000;

#[derive(Debug, Clone)]
pub struct PdfOptions {
    pub pdf_x: bool,
    pub pdf_a: bool,
    pub flatten_transparency: bool,
    pub width: f32,
    pub height: f32,
    pub trim: Rect,
    pub bleed: Rect,
    pub title: String,
    pub author: String,
    pub background_rgb: Option<[f32; 3]>,
    pub expected_image_color_space: ImageColorSpace,
    pub tagged: bool,
    pub language: String,
    pub allow_mixed_page_boxes: bool,
    pub trim_width: f32,
    pub trim_height: f32,
    pub interior_bleed: f32,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ImageColorSpace {
    Gray,
    Rgb,
    Cmyk,
}

impl PdfOptions {
    pub fn interior(request: &RenderRequest, pdf_x: bool, pdf_a: bool) -> Self {
        let trim_width = request.trim.width_inches * 72.0;
        let trim_height = request.trim.height_inches * 72.0;
        let interior_bleed = request.trim.bleed_inches * 72.0;
        let width = trim_width + interior_bleed;
        let height = trim_height + interior_bleed * 2.0;
        Self {
            pdf_x,
            pdf_a,
            flatten_transparency: pdf_x
                || pdf_a
                || request.profile.starts_with("kdp-")
                || request.profile == "lulu-print-v1",
            width,
            height,
            trim: Rect::new(0.0, 0.0, width, height),
            bleed: Rect::new(0.0, 0.0, width, height),
            title: request
                .document
                .get("title")
                .and_then(serde_json::Value::as_str)
                .unwrap_or("")
                .to_owned(),
            author: request
                .document
                .get("author")
                .and_then(serde_json::Value::as_str)
                .unwrap_or("")
                .to_owned(),
            background_rgb: (request.profile == "generic-digital-pdf-v1")
                .then_some(request.cover.as_ref())
                .flatten()
                .and_then(|cover| parse_hex_color(&cover.background_color)),
            expected_image_color_space: if request.ink == "BlackAndWhite" {
                ImageColorSpace::Gray
            } else if pdf_x {
                ImageColorSpace::Cmyk
            } else {
                ImageColorSpace::Rgb
            },
            tagged: request.profile == "generic-digital-pdf-v1",
            language: request
                .document
                .get("language")
                .and_then(serde_json::Value::as_str)
                .unwrap_or("en")
                .to_owned(),
            allow_mixed_page_boxes: request.profile == "generic-digital-pdf-v1"
                && request
                    .document
                    .get("allowDesignedPageOverrides")
                    .and_then(serde_json::Value::as_bool)
                    .unwrap_or(false),
            trim_width,
            trim_height,
            interior_bleed,
        }
    }

    pub fn cover(
        request: &RenderRequest,
        pdf_x: bool,
        pdf_a: bool,
        width: f32,
        height: f32,
        _spine_width: f32,
    ) -> Self {
        let bleed_points = request
            .cover
            .as_ref()
            .map_or(0.0, |cover| cover.bleed_inches * 72.0);
        Self {
            pdf_x,
            pdf_a,
            flatten_transparency: pdf_x
                || pdf_a
                || request.profile.starts_with("kdp-")
                || request.profile == "lulu-print-v1",
            width,
            height,
            trim: Rect::new(
                bleed_points,
                bleed_points,
                width - bleed_points,
                height - bleed_points,
            ),
            bleed: Rect::new(0.0, 0.0, width, height),
            title: request
                .cover
                .as_ref()
                .map_or_else(String::new, |cover| cover.title.clone()),
            author: request
                .cover
                .as_ref()
                .map_or_else(String::new, |cover| cover.author.clone()),
            background_rgb: request
                .cover
                .as_ref()
                .and_then(|cover| parse_hex_color(&cover.background_color)),
            expected_image_color_space: if pdf_x {
                ImageColorSpace::Cmyk
            } else {
                ImageColorSpace::Rgb
            },
            tagged: false,
            language: request
                .document
                .get("language")
                .and_then(serde_json::Value::as_str)
                .unwrap_or("en")
                .to_owned(),
            allow_mixed_page_boxes: false,
            trim_width: width,
            trim_height: height,
            interior_bleed: 0.0,
        }
    }
}

pub fn write_pdf(
    pages: &[LayoutPage],
    fonts: &BTreeMap<FontFace, EmbeddedFont>,
    images: &BTreeMap<String, EmbeddedImage>,
    options: &PdfOptions,
) -> Result<Vec<u8>, Diagnostic> {
    write_pdf_cancellable(pages, fonts, images, options, || false)
}

pub fn write_pdf_cancellable<F>(
    pages: &[LayoutPage],
    fonts: &BTreeMap<FontFace, EmbeddedFont>,
    images: &BTreeMap<String, EmbeddedImage>,
    options: &PdfOptions,
    cancelled: F,
) -> Result<Vec<u8>, Diagnostic>
where
    F: Fn() -> bool,
{
    write_pdf_cancellable_with_progress(pages, fonts, images, options, cancelled, |_, _| {})
}

pub fn write_pdf_cancellable_with_progress<F, G>(
    pages: &[LayoutPage],
    fonts: &BTreeMap<FontFace, EmbeddedFont>,
    images: &BTreeMap<String, EmbeddedImage>,
    options: &PdfOptions,
    cancelled: F,
    mut progress: G,
) -> Result<Vec<u8>, Diagnostic>
where
    F: Fn() -> bool,
    G: FnMut(usize, usize),
{
    if pages.is_empty() {
        return Err(Diagnostic::error(
            "PRESS_LAYOUT_EMPTY",
            "A PDF must contain at least one page.",
        ));
    }
    let mut pages = pages.to_vec();
    let mut images = images.clone();
    extend_edge_art_into_print_bleed(&mut pages, options);
    if options.flatten_transparency {
        flatten_pdfx_opacity(&mut pages, fonts, &mut images, options, options.pdf_x)?;
    }
    let pages = pages.as_slice();
    let images = &images;
    if options.tagged
        && pages.iter().any(|page| {
            page.images.len().saturating_add(page.lines.len()) >= PAGE_TAG_ITEM_LIMIT
                || page.lines.len() >= PAGE_TAG_ITEM_LIMIT
        })
    {
        return Err(Diagnostic::error(
            "PRESS_PAGE_OBJECT_LIMIT",
            "A page contains too many semantic or linked objects for deterministic PDF tagging.",
        ));
    }
    let mut pdf = Pdf::new();
    pdf.set_version(
        1,
        if options.pdf_x {
            3
        } else if options.pdf_a {
            4
        } else {
            7
        },
    );
    let catalog_id = Ref::new(1);
    let pages_id = Ref::new(2);
    let info_id = Ref::new(3);
    let icc_id = Ref::new(4);
    let metadata_id = Ref::new(5);
    let struct_root_id = Ref::new(90);
    let document_struct_id = Ref::new(91);
    let outline_id = Ref::new(92);
    let bookmarks = pages
        .iter()
        .enumerate()
        .filter_map(|(index, page)| page.bookmark.as_ref().map(|title| (index, title)))
        .collect::<Vec<_>>();

    {
        let mut catalog = pdf.catalog(catalog_id);
        catalog.pages(pages_id);
        if options.tagged {
            catalog.pair(Name(b"StructTreeRoot"), struct_root_id);
            catalog.mark_info().marked(true).suspects(false);
            catalog.lang(TextStr(&options.language));
        }
        if options.pdf_a {
            catalog.metadata(metadata_id);
        }
        if !bookmarks.is_empty() {
            catalog.outlines(outline_id);
        }
        if options.pdf_x {
            catalog
                .output_intents()
                .push()
                .subtype(OutputIntentSubtype::PDFX)
                .output_condition(TextStr("CGATS TR 006 / CRPC1 240% TAC"))
                .output_condition_identifier(TextStr("CGATS21_CRPC1"))
                .registry_name(TextStr("https://registry.color.org"))
                .info(TextStr("CGATS21 CRPC1 CMYK 240% total area coverage"))
                .dest_output_profile(icc_id);
        } else if options.pdf_a {
            catalog
                .output_intents()
                .push()
                .subtype(OutputIntentSubtype::PDFA)
                .output_condition(TextStr("CGATS TR 006 / CRPC1 240% TAC"))
                .output_condition_identifier(TextStr("CGATS21_CRPC1"))
                .registry_name(TextStr("https://registry.color.org"))
                .info(TextStr("CGATS21 CRPC1 CMYK 240% total area coverage"))
                .dest_output_profile(icc_id);
        }
    }

    if options.pdf_a {
        let title = xml_escape(&options.title);
        let author = xml_escape(&options.author);
        let xmp = format!(
            r#"<?xpacket begin="﻿" id="W5M0MpCehiHzreSzNTczkc9d"?>
<x:xmpmeta xmlns:x="adobe:ns:meta/">
 <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
  <rdf:Description rdf:about="" xmlns:pdfaid="http://www.aiim.org/pdfa/ns/id/" pdfaid:part="1" pdfaid:conformance="B"/>
  <rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/">
   <dc:title><rdf:Alt><rdf:li xml:lang="x-default">{title}</rdf:li></rdf:Alt></dc:title>
   <dc:creator><rdf:Seq><rdf:li>{author}</rdf:li></rdf:Seq></dc:creator>
  </rdf:Description>
  <rdf:Description rdf:about="" xmlns:pdf="http://ns.adobe.com/pdf/1.3/" pdf:Producer="Lorekeeper Press"/>
 </rdf:RDF>
</x:xmpmeta>
<?xpacket end="w"?>"#
        );
        pdf.metadata(metadata_id, xmp.as_bytes());
    }
    {
        let mut info = pdf.document_info(info_id);
        info.title(TextStr(&options.title));
        info.author(TextStr(&options.author));
        info.creator(TextStr(concat!(
            "Lorekeeper Press ",
            env!("CARGO_PKG_VERSION")
        )));
        info.producer(TextStr(concat!(
            "Lorekeeper Press ",
            env!("CARGO_PKG_VERSION")
        )));
        if options.pdf_x {
            info.pair(Name(b"GTS_PDFXVersion"), TextStr("PDF/X-1a:2001"));
            info.pair(Name(b"GTS_PDFXConformance"), TextStr("PDF/X-1a:2001"));
            info.pair(Name(b"Trapped"), Name(b"False"));
        }
    }

    let image_references = images
        .keys()
        .enumerate()
        .map(|(index, id)| (id.clone(), Ref::new(200 + index as i32)))
        .collect::<BTreeMap<_, _>>();
    let font_references = fonts
        .keys()
        .enumerate()
        .map(|(index, face)| (*face, Ref::new(10 + index as i32 * 5)))
        .collect::<BTreeMap<_, _>>();
    let page_ids = (0..pages.len())
        .map(|index| Ref::new(2_000 + index as i32 * 2))
        .collect::<Vec<_>>();
    let opacity_values = pages
        .iter()
        .flat_map(|page| {
            page.shapes
                .iter()
                .map(|shape| shape.opacity)
                .chain(page.images.iter().map(|image| image.opacity))
                .chain(page.lines.iter().map(|line| line.opacity))
        })
        .map(opacity_key)
        .filter(|opacity| *opacity < 1_000)
        .collect::<std::collections::BTreeSet<_>>();
    let opacity_references = opacity_values
        .into_iter()
        .enumerate()
        .map(|(index, opacity)| {
            (
                opacity,
                Ref::new(
                    PAGE_TAG_OBJECT_BASE
                        + pages.len() as i32 * PAGE_TAG_OBJECT_STRIDE
                        + index as i32,
                ),
            )
        })
        .collect::<BTreeMap<_, _>>();
    for (opacity, reference) in &opacity_references {
        let alpha = f32::from(*opacity) / 1_000.0;
        pdf.ext_graphics(*reference)
            .stroking_alpha(alpha)
            .non_stroking_alpha(alpha);
    }

    if !bookmarks.is_empty() {
        let item_ids = (0..bookmarks.len())
            .map(|index| Ref::new(30_000 + index as i32))
            .collect::<Vec<_>>();
        let mut outline = pdf.outline(outline_id);
        outline
            .first(item_ids[0])
            .last(*item_ids.last().expect("bookmark"))
            .count(item_ids.len() as i32);
        outline.finish();
        for (position, ((page_index, title), item_id)) in
            bookmarks.iter().zip(&item_ids).enumerate()
        {
            let mut item = pdf.outline_item(*item_id);
            item.title(TextStr(title)).parent(outline_id);
            if position > 0 {
                item.prev(item_ids[position - 1]);
            }
            if position + 1 < item_ids.len() {
                item.next(item_ids[position + 1]);
            }
            item.dest().page(page_ids[*page_index]).fit();
        }
    }

    let tag_maps = pages
        .iter()
        .enumerate()
        .map(|(page_index, page)| page_tags(page_index, page))
        .collect::<Vec<_>>();
    let tag_elements = consolidate_tags(&tag_maps);
    if options.tagged {
        let parent_array_ids = (0..pages.len())
            .map(|index| Ref::new(20_000 + index as i32))
            .collect::<Vec<_>>();
        for (page_index, array_id) in parent_array_ids.iter().enumerate() {
            let mut array = pdf.indirect(*array_id).array();
            let mut by_mcid = tag_elements
                .iter()
                .flat_map(|element| {
                    element
                        .mcids
                        .iter()
                        .filter(move |(content_page_index, _)| *content_page_index == page_index)
                        .map(move |(_, mcid)| (*mcid, element.reference))
                })
                .collect::<Vec<_>>();
            by_mcid.sort_by_key(|(mcid, _)| *mcid);
            for (_, reference) in by_mcid {
                array.item(reference);
            }
        }
        let mut root = pdf.indirect(struct_root_id).start::<StructTreeRoot>();
        root.child(document_struct_id);
        {
            let mut parent_tree = root.parent_tree();
            let mut nums = parent_tree.nums();
            for (index, array_id) in parent_array_ids.iter().enumerate() {
                nums.insert(index as i32, *array_id);
            }
            for element in &tag_elements {
                if let Some((page_index, annotation_ref)) = element.annotation_ref {
                    nums.insert(
                        annotation_parent_key(pages.len(), page_index, annotation_ref),
                        element.reference,
                    );
                }
            }
        }
        root.parent_tree_next_key(
            pages.len() as i32 + pages.len() as i32 * PAGE_TAG_ITEM_LIMIT as i32,
        );
        root.finish();
        let mut document = pdf.struct_element(document_struct_id);
        document.kind(StructRole::Document).parent(struct_root_id);
        {
            let mut children = document.children();
            let mut logical = tag_elements.iter().collect::<Vec<_>>();
            logical.sort_by_key(|element| (element.reading_order, element.reference.get()));
            for element in logical {
                if element.parent.is_none() {
                    children.item(element.reference);
                }
            }
        }
        document.finish();
        for element in &tag_elements {
            let element_page_index = element
                .mcids
                .first()
                .map(|(page_index, _)| *page_index)
                .or_else(|| element.annotation_ref.map(|(page_index, _)| page_index))
                .or_else(|| {
                    tag_elements.iter().find_map(|child| {
                        (child.parent == Some(element.reference))
                            .then(|| child.mcids.first().map(|(page_index, _)| *page_index))
                            .flatten()
                    })
                })
                .unwrap_or_default();
            let mut structure = pdf.struct_element(element.reference);
            structure
                .kind(element.role)
                .parent(element.parent.unwrap_or(document_struct_id))
                .page(page_ids[element_page_index]);
            if let Some(alt) = &element.alt_text {
                structure.alt(TextStr(alt));
            }
            if let Some(language) = &element.language {
                structure.lang(TextStr(language));
            }
            {
                let mut children = structure.children();
                for (page_index, mcid) in &element.mcids {
                    children
                        .marked_content_ref()
                        .page(page_ids[*page_index])
                        .marked_content_id(*mcid);
                }
                if let Some((page_index, annotation_ref)) = element.annotation_ref {
                    children
                        .object_ref()
                        .page(page_ids[page_index])
                        .object(annotation_ref);
                }
                for child in tag_elements
                    .iter()
                    .filter(|child| child.parent == Some(element.reference))
                {
                    children.item(child.reference);
                }
            }
            structure.finish();
        }
    }
    pdf.pages(pages_id)
        .kids(page_ids.iter().copied())
        .count(pages.len() as i32);
    let progress_total = pages.len().saturating_add(images.len());
    let mut progress_completed = 0usize;
    for (index, page_model) in pages.iter().enumerate() {
        if cancelled() {
            return Err(Diagnostic::error(
                "PRESS_RENDER_CANCELLED",
                "Rendering was cancelled during PDF composition.",
            ));
        }
        let page_id = page_ids[index];
        let content_id = Ref::new(page_id.get() + 1);
        let page_width = page_model.width_points.unwrap_or(options.width);
        let page_height = page_model.height_points.unwrap_or(options.height);
        let page_box = Rect::new(0.0, 0.0, page_width, page_height);
        let uses_print_bleed = options.interior_bleed > 0.0
            && page_model.width_points.is_none()
            && page_model.kind != PageKind::Cover;
        let content_offset_x = if uses_print_bleed && index % 2 == 1 {
            options.interior_bleed
        } else {
            0.0
        };
        let content_offset_y = if uses_print_bleed {
            options.interior_bleed
        } else {
            0.0
        };
        let trim_box = if uses_print_bleed {
            Rect::new(
                content_offset_x,
                content_offset_y,
                content_offset_x + options.trim_width,
                content_offset_y + options.trim_height,
            )
        } else {
            options.trim
        };
        let mut page = pdf.page(page_id);
        page.parent(pages_id);
        page.media_box(page_box);
        let uses_independent_page_box =
            page_model.width_points.is_some() && page_model.kind != PageKind::Cover;
        page.crop_box(if uses_independent_page_box {
            page_box
        } else {
            options.bleed
        });
        page.bleed_box(if uses_independent_page_box {
            page_box
        } else {
            options.bleed
        });
        page.trim_box(if uses_independent_page_box {
            page_box
        } else {
            trim_box
        });
        page.contents(content_id);
        if options.tagged {
            page.struct_parents(index as i32);
            let annotations = page_model
                .lines
                .iter()
                .enumerate()
                .filter(|(_, line)| line.link_page.is_some())
                .map(|(line_index, _)| annotation_ref(index, line_index))
                .collect::<Vec<_>>();
            if !annotations.is_empty() {
                page.annotations(annotations);
            }
        }
        {
            let mut resources = page.resources();
            let mut font_resources = resources.fonts();
            for (face, font_ref) in &font_references {
                let name = font_resource_name(*face);
                font_resources.pair(Name(name.as_bytes()), *font_ref);
            }
            font_resources.finish();
            let mut x_objects = resources.x_objects();
            for image in &page_model.images {
                let image_ref = image_references.get(&image.asset_id).ok_or_else(|| {
                    Diagnostic::error(
                        "PRESS_ASSET_REFERENCE_MISSING",
                        format!("Layout references undeclared asset '{}'.", image.asset_id),
                    )
                })?;
                let name = format!("Im{}", image_ref.get());
                x_objects.pair(Name(name.as_bytes()), *image_ref);
            }
            x_objects.finish();
            let page_opacities = page_model
                .shapes
                .iter()
                .map(|shape| shape.opacity)
                .chain(page_model.images.iter().map(|image| image.opacity))
                .chain(page_model.lines.iter().map(|line| line.opacity))
                .map(opacity_key)
                .filter(|opacity| *opacity < 1_000)
                .collect::<std::collections::BTreeSet<_>>();
            if !page_opacities.is_empty() {
                let mut states = resources.ext_g_states();
                for opacity in page_opacities {
                    states.pair(
                        Name(opacity_name(opacity).as_bytes()),
                        opacity_references[&opacity],
                    );
                }
            }
        }
        page.finish();

        if options.tagged {
            for (line_index, line) in page_model.lines.iter().enumerate() {
                let Some(target_page) = line.link_page else {
                    continue;
                };
                let Some(target_id) = target_page
                    .checked_sub(1)
                    .and_then(|value| page_ids.get(value))
                    .copied()
                else {
                    return Err(Diagnostic::error(
                        "PRESS_INTERNAL_LINK_INVALID",
                        "A table-of-contents link targets a missing page.",
                    ));
                };
                let annotation_id = annotation_ref(index, line_index);
                let mut annotation = pdf.annotation(annotation_id);
                annotation
                    .subtype(AnnotationType::Link)
                    .rect(Rect::new(
                        line.x + content_offset_x,
                        line.y + content_offset_y - line.size * 0.35,
                        page_width - line.x,
                        line.y + content_offset_y + line.size,
                    ))
                    .page(page_id)
                    .border(0.0, 0.0, 0.0, None)
                    .struct_parent(annotation_parent_key(pages.len(), index, annotation_id))
                    .contents(TextStr(&line.text));
                let mut action = annotation.action();
                action.action_type(ActionType::GoTo);
                action.destination().page(target_id).fit();
            }
        }

        let mut content = Content::new();
        if page_model.kind == PageKind::Cover {
            let rgb = options.background_rgb.unwrap_or([0.086, 0.196, 0.31]);
            if options.pdf_x {
                let [cyan, magenta, yellow, black] = rgb_to_bounded_cmyk(rgb);
                content.set_fill_cmyk(cyan, magenta, yellow, black);
            } else {
                content.set_fill_rgb(rgb[0], rgb[1], rgb[2]);
            }
            content
                .rect(0.0, 0.0, page_width, page_height)
                .fill_nonzero();
        }
        if content_offset_x != 0.0 || content_offset_y != 0.0 {
            content.save_state();
            content.transform([1.0, 0.0, 0.0, 1.0, content_offset_x, content_offset_y]);
        }
        if options.pdf_x {
            content.set_fill_cmyk(0.0, 0.0, 0.0, 1.0);
        } else {
            content.set_fill_gray(if page_model.kind == PageKind::Cover {
                1.0
            } else {
                0.0
            });
        }
        macro_rules! paint_shape {
            ($shape:expr) => {{
                let shape = $shape;
                if options.tagged {
                    content.begin_marked_content(Name(b"Artifact"));
                }
                content.save_state();
                apply_opacity(&mut content, shape.opacity);
                if shape.rotation_degrees.abs() > f32::EPSILON {
                    let radians = -shape.rotation_degrees.to_radians();
                    let (sin, cos) = radians.sin_cos();
                    let center_x = shape.x + shape.width / 2.0;
                    let center_y = shape.y + shape.height / 2.0;
                    content.transform([
                        cos,
                        sin,
                        -sin,
                        cos,
                        center_x - cos * center_x + sin * center_y,
                        center_y - sin * center_x - cos * center_y,
                    ]);
                }
                if let Some(rgb) = shape.fill_rgb {
                    if options.expected_image_color_space == ImageColorSpace::Gray {
                        content.set_fill_gray(rgb_luminance(rgb));
                    } else if options.pdf_x {
                        let [c, m, y, k] = rgb_to_bounded_cmyk(rgb);
                        content.set_fill_cmyk(c, m, y, k);
                    } else {
                        content.set_fill_rgb(rgb[0], rgb[1], rgb[2]);
                    }
                }
                if let Some(rgb) = shape.stroke_rgb {
                    if options.expected_image_color_space == ImageColorSpace::Gray {
                        content.set_stroke_gray(rgb_luminance(rgb));
                    } else if options.pdf_x {
                        let [c, m, y, k] = rgb_to_bounded_cmyk(rgb);
                        content.set_stroke_cmyk(c, m, y, k);
                    } else {
                        content.set_stroke_rgb(rgb[0], rgb[1], rgb[2]);
                    }
                    content.set_line_width(shape.stroke_width.max(0.1));
                }
                match shape.kind {
                    LayoutShapeKind::Rectangle => {
                        content.rect(shape.x, shape.y, shape.width, shape.height);
                    }
                    LayoutShapeKind::Line => {
                        content
                            .move_to(shape.x, shape.y + shape.height / 2.0)
                            .line_to(shape.x + shape.width, shape.y + shape.height / 2.0);
                    }
                    LayoutShapeKind::Ellipse => {
                        let kappa = 0.552_284_8;
                        let rx = shape.width / 2.0;
                        let ry = shape.height / 2.0;
                        let cx = shape.x + rx;
                        let cy = shape.y + ry;
                        content
                            .move_to(cx + rx, cy)
                            .cubic_to(
                                cx + rx,
                                cy + ry * kappa,
                                cx + rx * kappa,
                                cy + ry,
                                cx,
                                cy + ry,
                            )
                            .cubic_to(
                                cx - rx * kappa,
                                cy + ry,
                                cx - rx,
                                cy + ry * kappa,
                                cx - rx,
                                cy,
                            )
                            .cubic_to(
                                cx - rx,
                                cy - ry * kappa,
                                cx - rx * kappa,
                                cy - ry,
                                cx,
                                cy - ry,
                            )
                            .cubic_to(
                                cx + rx * kappa,
                                cy - ry,
                                cx + rx,
                                cy - ry * kappa,
                                cx + rx,
                                cy,
                            );
                    }
                }
                match (shape.fill_rgb.is_some(), shape.stroke_rgb.is_some()) {
                    (true, true) => {
                        content.fill_nonzero_and_stroke();
                    }
                    (true, false) => {
                        content.fill_nonzero();
                    }
                    (false, true) => {
                        content.stroke();
                    }
                    (false, false) => {
                        content.end_path();
                    }
                }
                content.restore_state();
                if options.tagged {
                    content.end_marked_content();
                }
            }};
        }
        if page_model.paint_order.is_empty() {
            for shape in &page_model.shapes {
                paint_shape!(shape);
            }
        }
        macro_rules! paint_image {
            ($image_index:expr, $image:expr) => {{
                let image_index = $image_index;
                let image = $image;
                let image_marked = if options.tagged {
                    if let Some(mcid) = tag_maps[index].image_mcids[image_index] {
                        let mut marked =
                            content.begin_marked_content_with_properties(Name(b"Figure"));
                        marked.properties().identify(mcid);
                        marked.finish();
                    } else {
                        content.begin_marked_content(Name(b"Artifact"));
                    }
                    true
                } else {
                    false
                };
                let image_ref = image_references
                    .get(&image.asset_id)
                    .expect("validated image reference");
                let source = &images[&image.asset_id];
                let source_width =
                    source.width as f32 * image.source_width_fraction.clamp(0.01, 1.0);
                let source_fraction = image.source_width_fraction.clamp(0.01, 1.0);
                let source_left = image.source_left_fraction.clamp(0.0, 1.0);
                let width_scale = image.width / source_width;
                let height_scale = image.height / source.height as f32;
                let (drawn_width, drawn_height, visible_width) = match image.fit {
                    LayoutImageFit::Contain => {
                        let scale = width_scale.min(height_scale);
                        (
                            source.width as f32 * scale,
                            source.height as f32 * scale,
                            source_width * scale,
                        )
                    }
                    LayoutImageFit::Cover => {
                        let scale = width_scale.max(height_scale);
                        (
                            source.width as f32 * scale,
                            source.height as f32 * scale,
                            source_width * scale,
                        )
                    }
                    LayoutImageFit::Stretch => {
                        (image.width / source_fraction, image.height, image.width)
                    }
                };
                let drawn_x = if source_fraction < 1.0 {
                    -image.width / 2.0 - drawn_width * source_left
                } else {
                    -image.width / 2.0
                        + (image.width - visible_width) * image.focal_x.clamp(0.0, 1.0)
                };
                let drawn_y = -image.height / 2.0
                    + (image.height - drawn_height) * (1.0 - image.focal_y.clamp(0.0, 1.0));
                let name = format!("Im{}", image_ref.get());
                content.save_state();
                apply_opacity(&mut content, image.opacity);
                let angle = -image.rotation_degrees.to_radians();
                content.transform([
                    angle.cos(),
                    angle.sin(),
                    -angle.sin(),
                    angle.cos(),
                    image.x + image.width / 2.0,
                    image.y + image.height / 2.0,
                ]);
                content
                    .rect(
                        -image.width / 2.0,
                        -image.height / 2.0,
                        image.width,
                        image.height,
                    )
                    .clip_nonzero()
                    .end_path();
                content.transform([drawn_width, 0.0, 0.0, drawn_height, drawn_x, drawn_y]);
                content.x_object(Name(name.as_bytes()));
                content.restore_state();
                if image_marked {
                    content.end_marked_content();
                }
            }};
        }
        if page_model.paint_order.is_empty() {
            for (image_index, image) in page_model.images.iter().enumerate() {
                paint_image!(image_index, image);
            }
        }
        if let Some(modules) = &page_model.barcode_modules {
            if options.tagged {
                content.begin_marked_content(Name(b"Artifact"));
            }
            let module_width = 1.15;
            let barcode_width = modules.len() as f32 * module_width + 18.0;
            let barcode_height = 66.0;
            let [reserve_x, reserve_y, reserve_width, reserve_height] =
                page_model.barcode_bounds.unwrap_or([
                    page_width * 0.08 - 9.0,
                    page_height * 0.90 - 48.0,
                    barcode_width,
                    barcode_height,
                ]);
            let origin_x = reserve_x + ((reserve_width - barcode_width) / 2.0).max(0.0) + 9.0;
            let reserve_bottom = page_height - reserve_y - reserve_height;
            let origin_y =
                reserve_bottom + ((reserve_height - barcode_height) / 2.0).max(0.0) + 18.0;
            if options.pdf_x {
                content.set_fill_cmyk(0.0, 0.0, 0.0, 0.0);
            } else {
                content.set_fill_gray(1.0);
            }
            content
                .rect(
                    origin_x - 9.0,
                    origin_y - 18.0,
                    barcode_width,
                    barcode_height,
                )
                .fill_nonzero();
            if options.pdf_x {
                content.set_fill_cmyk(0.0, 0.0, 0.0, 1.0);
            } else {
                content.set_fill_gray(0.0);
            }
            for (index, dark) in modules.iter().enumerate() {
                if *dark {
                    let is_guard = index <= 2 || (45..=49).contains(&index) || index >= 92;
                    content
                        .rect(
                            origin_x + index as f32 * module_width,
                            origin_y - if is_guard { 4.0 } else { 0.0 },
                            module_width,
                            if is_guard { 46.0 } else { 42.0 },
                        )
                        .fill_nonzero();
                }
            }
            if options.tagged {
                content.end_marked_content();
            }
        }
        macro_rules! paint_line {
            ($line_index:expr, $line:expr) => {{
                let line_index = $line_index;
                let line = $line;
                let line_marked = if options.tagged {
                    if let Some(mcid) = tag_maps[index].line_mcids[line_index] {
                        let mut marked = content.begin_marked_content_with_properties(
                            struct_role(line.semantic_role).to_name(),
                        );
                        marked.properties().identify(mcid);
                        marked.finish();
                    } else {
                        content.begin_marked_content(Name(b"Artifact"));
                    }
                    true
                } else {
                    false
                };
                content.save_state();
                apply_opacity(&mut content, line.opacity);
                if options.expected_image_color_space == ImageColorSpace::Gray {
                    let gray = line.fill_rgb.map_or(
                        if line.light_text { 1.0 } else { 0.0 },
                        |[red, green, blue]| red * 0.2126 + green * 0.7152 + blue * 0.0722,
                    );
                    content.set_fill_gray(gray);
                } else if options.pdf_x {
                    let [cyan, magenta, yellow, black] = line
                        .fill_rgb
                        .map(rgb_to_bounded_cmyk)
                        .unwrap_or([0.0, 0.0, 0.0, if line.light_text { 0.0 } else { 1.0 }]);
                    content.set_fill_cmyk(cyan, magenta, yellow, black);
                } else if let Some([red, green, blue]) = line.fill_rgb {
                    content.set_fill_rgb(red, green, blue);
                } else {
                    content.set_fill_gray(if line.light_text { 1.0 } else { 0.0 });
                }
                let fallback;
                let runs = if line.runs.is_empty() {
                    fallback = vec![LayoutRun {
                        note_reference_id: None,
                        text: line.text.clone(),
                        face: FontFace::SerifRegular,
                        underline: false,
                        strikethrough: false,
                        baseline_shift_em: 0.0,
                        size_scale: 1.0,
                        language: None,
                    }];
                    fallback.as_slice()
                } else {
                    line.runs.as_slice()
                };
                let mut cursor_x = line.x;
                if line.rotation_degrees.abs() > f32::EPSILON {
                    let angle = -line.rotation_degrees.to_radians();
                    let origin_x = line.rotation_origin_x.unwrap_or(line.x);
                    let origin_y = line.rotation_origin_y.unwrap_or(line.y);
                    content.transform([
                        angle.cos(),
                        angle.sin(),
                        -angle.sin(),
                        angle.cos(),
                        origin_x - angle.cos() * origin_x + angle.sin() * origin_y,
                        origin_y - angle.sin() * origin_x - angle.cos() * origin_y,
                    ]);
                }
                for run in runs {
                    let font = fonts.get(&run.face).ok_or_else(|| {
                        Diagnostic::error(
                            "PRESS_FONT_REFERENCE_MISSING",
                            format!("Layout references an unavailable font face {:?}.", run.face),
                        )
                    })?;
                    let run_size = line.size * run.size_scale;
                    let baseline = line.y + run_size * run.baseline_shift_em;
                    content.begin_text();
                    content.set_font(Name(font_resource_name(run.face).as_bytes()), run_size);
                    let mut shaped_advance = 0.0;
                    let shaped = font.shape(&run.text, run_size);
                    let glyph_count = shaped.len();
                    for (glyph_index, glyph) in shaped.into_iter().enumerate() {
                        let encoded = glyph.cid.to_be_bytes();
                        let matrix = [
                            1.0,
                            0.0,
                            0.0,
                            1.0,
                            cursor_x + shaped_advance + glyph.x_offset,
                            baseline + glyph.y_offset,
                        ];
                        content.set_text_matrix(matrix);
                        content.show(Str(&encoded));
                        shaped_advance += glyph.x_advance;
                        if glyph_index + 1 < glyph_count {
                            shaped_advance += line.character_spacing;
                        }
                        if run
                            .text
                            .get(glyph.cluster..)
                            .and_then(|text| text.chars().next())
                            .is_some_and(char::is_whitespace)
                        {
                            shaped_advance += line.word_spacing;
                        }
                    }
                    content.end_text();
                    let advance = shaped_advance;
                    if run.underline {
                        content
                            .rect(
                                cursor_x,
                                baseline - run_size * 0.12,
                                advance,
                                (run_size * 0.045).max(0.4),
                            )
                            .fill_nonzero();
                    }
                    if run.strikethrough {
                        content
                            .rect(
                                cursor_x,
                                baseline + run_size * 0.28,
                                advance,
                                (run_size * 0.045).max(0.4),
                            )
                            .fill_nonzero();
                    }
                    cursor_x += advance;
                }
                if line_marked {
                    content.end_marked_content();
                }
                content.restore_state();
            }};
        }
        if page_model.paint_order.is_empty() {
            for (line_index, line) in page_model.lines.iter().enumerate() {
                paint_line!(line_index, line);
            }
        } else {
            for paint in &page_model.paint_order {
                match *paint {
                    LayoutPaint::Shape(shape_index) => {
                        paint_shape!(&page_model.shapes[shape_index]);
                    }
                    LayoutPaint::Image(image_index) => {
                        paint_image!(image_index, &page_model.images[image_index]);
                    }
                    LayoutPaint::Line(line_index) => {
                        paint_line!(line_index, &page_model.lines[line_index]);
                    }
                }
            }
        }
        if let Some(label) = &page_model.page_label {
            if options.tagged {
                content.begin_marked_content(Name(b"Artifact"));
            }
            if options.pdf_x {
                content.set_fill_cmyk(0.0, 0.0, 0.0, 1.0);
            } else {
                content.set_fill_gray(0.0);
            }
            let font = fonts.get(&FontFace::SerifRegular).ok_or_else(|| {
                Diagnostic::error(
                    "PRESS_FONT_REFERENCE_MISSING",
                    "Page labels require the serif font.",
                )
            })?;
            let encoded = font.encode(label);
            content.begin_text();
            content.set_font(
                Name(font_resource_name(FontFace::SerifRegular).as_bytes()),
                8.0,
            );
            content.set_text_matrix([1.0, 0.0, 0.0, 1.0, page_width / 2.0, 18.0]);
            content.show(Str(&encoded));
            content.end_text();
            if options.tagged {
                content.end_marked_content();
            }
        }
        if content_offset_x != 0.0 || content_offset_y != 0.0 {
            content.restore_state();
        }
        pdf.stream(content_id, &content.finish());
        progress_completed += 1;
        progress(progress_completed, progress_total);
    }

    for image in images.values() {
        if cancelled() {
            return Err(Diagnostic::error(
                "PRESS_RENDER_CANCELLED",
                "Rendering was cancelled during image serialization.",
            ));
        }
        let image_ref = image_references[&image.id];
        let compressed = compress_image(&image.samples)?;
        let mut object = pdf.image_xobject(image_ref, &compressed);
        object.filter(Filter::FlateDecode);
        object.width(image.width as i32);
        object.height(image.height as i32);
        if image.cmyk {
            object.color_space().device_cmyk();
        } else if image.grayscale {
            object.color_space().device_gray();
        } else {
            object.color_space().device_rgb();
        }
        object.bits_per_component(8);
        progress_completed += 1;
        progress(progress_completed, progress_total);
    }

    for (face, font) in fonts {
        if cancelled() {
            return Err(Diagnostic::error(
                "PRESS_RENDER_CANCELLED",
                "Rendering was cancelled during font serialization.",
            ));
        }
        let font_id = font_references[face];
        let cid_font_id = Ref::new(font_id.get() + 1);
        let descriptor_id = Ref::new(font_id.get() + 2);
        let font_file_id = Ref::new(font_id.get() + 3);
        let to_unicode_id = Ref::new(font_id.get() + 4);
        let system_info = SystemInfo {
            registry: Str(b"Adobe"),
            ordering: Str(b"Identity"),
            supplement: 0,
        };
        pdf.type0_font(font_id)
            .base_font(Name(font.postscript_name.as_bytes()))
            .encoding_predefined(Name(b"Identity-H"))
            .descendant_font(cid_font_id)
            .to_unicode(to_unicode_id);
        {
            let mut cid = pdf.cid_font(cid_font_id);
            cid.subtype(if font.open_type {
                CidFontType::Type0
            } else {
                CidFontType::Type2
            })
            .base_font(Name(font.postscript_name.as_bytes()))
            .system_info(system_info)
            .font_descriptor(descriptor_id)
            .default_width(500.0);
            if !font.open_type {
                cid.cid_to_gid_map_predefined(Name(b"Identity"));
            }
            let maximum = font.widths.keys().copied().max().unwrap_or(0);
            let widths = (0..=maximum).map(|cid| font.widths.get(&cid).copied().unwrap_or(500.0));
            cid.widths().consecutive(0, widths);
        }
        let mut flags = FontFlags::NON_SYMBOLIC;
        if matches!(face.family(), crate::model::FontFamily::Serif) {
            flags |= FontFlags::SERIF;
        }
        let is_italic = crate::font::is_italic(*face);
        if is_italic {
            flags |= FontFlags::ITALIC;
        }
        {
            let mut descriptor = pdf.font_descriptor(descriptor_id);
            descriptor
                .name(Name(font.postscript_name.as_bytes()))
                .flags(flags)
                .bbox(Rect::new(-600.0, -300.0, 1400.0, 1100.0))
                .italic_angle(if is_italic { -12.0 } else { 0.0 })
                .ascent(1000.0)
                .descent(-300.0)
                .cap_height(700.0)
                .stem_v(80.0);
            if font.open_type {
                descriptor.font_file3(font_file_id);
            } else {
                descriptor.font_file2(font_file_id);
            }
        }
        let compressed_font = compress(&font.bytes)?;
        let mut font_stream = pdf.stream(font_file_id, &compressed_font);
        font_stream.filter(Filter::FlateDecode);
        if font.open_type {
            font_stream.pair(Name(b"Subtype"), Name(b"OpenType"));
        }
        font_stream.finish();
        let mut unicode = UnicodeCmap::new(Name(b"LorekeeperUnicode"), system_info);
        for (cid, characters) in &font.unicode_sequences {
            unicode.pair_with_multiple(*cid, characters.iter().copied());
        }
        let unicode_bytes = unicode.finish();
        pdf.cmap(to_unicode_id, unicode_bytes.as_slice());
    }
    if options.pdf_x || options.pdf_a {
        let compressed_icc = compress(ICC_PROFILE)?;
        let mut profile = pdf.icc_profile(icc_id, &compressed_icc);
        profile.n(4);
        profile.alternate().device_cmyk();
        profile.filter(Filter::FlateDecode);
    }
    Ok(pdf.finish())
}

fn xml_escape(value: &str) -> String {
    value
        .replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
        .replace('\'', "&apos;")
}

fn font_resource_name(face: FontFace) -> String {
    match face {
        FontFace::SerifRegular => "F1".to_owned(),
        FontFace::SerifItalic => "F2".to_owned(),
        FontFace::SerifBold => "F3".to_owned(),
        FontFace::SerifBoldItalic => "F4".to_owned(),
        FontFace::SansRegular => "F5".to_owned(),
        FontFace::SansItalic => "F6".to_owned(),
        FontFace::SansBold => "F7".to_owned(),
        FontFace::SansBoldItalic => "F8".to_owned(),
        FontFace::MonoRegular => "F9".to_owned(),
        FontFace::MonoItalic => "F10".to_owned(),
        FontFace::MonoBold => "F11".to_owned(),
        FontFace::MonoBoldItalic => "F12".to_owned(),
        FontFace::Custom(index) => format!("FC{}", index + 1),
    }
}

fn page_tags(page_index: usize, page: &LayoutPage) -> PageTags {
    let mut next_mcid = 0;
    let mut elements = Vec::new();
    let image_mcids = page
        .images
        .iter()
        .enumerate()
        .map(|(index, image)| {
            if image.decorative {
                return None;
            }
            let mcid = next_mcid;
            next_mcid += 1;
            elements.push(TagElement {
                reference: tag_ref(page_index, mcid),
                mcids: vec![(page_index, mcid)],
                role: image_struct_role(image.accessibility_role.as_deref()),
                alt_text: image.alt_text.clone(),
                language: image.language.clone(),
                reading_order: image.reading_order.unwrap_or(10_000 + mcid),
                semantic_id: image
                    .semantic_id
                    .clone()
                    .unwrap_or_else(|| format!("page-{page_index}-image-{index}")),
                parent_semantic_id: image.semantic_parent_id.clone(),
                annotation_ref: None,
                parent: None,
            });
            Some(mcid)
        })
        .collect();
    let line_mcids = page
        .lines
        .iter()
        .enumerate()
        .map(|(index, line)| {
            if line.artifact || line.text.trim().is_empty() {
                return None;
            }
            let mcid = next_mcid;
            next_mcid += 1;
            let reading_order = line.reading_order.unwrap_or(10_000 + mcid);
            let annotation_ref = line.link_page.map(|_| annotation_ref(page_index, index));
            let source_semantic_id = line
                .semantic_id
                .clone()
                .unwrap_or_else(|| format!("page-{page_index}-line-{index}"));
            let role = if annotation_ref.is_some() {
                StructRole::Link
            } else {
                struct_role(line.semantic_role)
            };
            let semantic_id = if annotation_ref.is_some() {
                format!("{source_semantic_id}:link")
            } else {
                source_semantic_id.clone()
            };
            let parent_semantic_id = if annotation_ref.is_some() {
                Some(format!("{source_semantic_id}:toc-item"))
            } else {
                line.semantic_parent_id.clone()
            };
            let can_join = annotation_ref
                .is_none()
                .then(|| {
                    elements.iter().position(|element| {
                        element.role == role
                            && element.semantic_id == semantic_id
                            && element.parent_semantic_id == line.semantic_parent_id
                            && element.language == line.language
                            && element.annotation_ref.is_none()
                    })
                })
                .flatten();
            if let Some(element_index) = can_join {
                elements[element_index].mcids.push((page_index, mcid));
            } else {
                elements.push(TagElement {
                    reference: tag_ref(page_index, mcid),
                    mcids: vec![(page_index, mcid)],
                    role,
                    alt_text: None,
                    language: line.language.clone(),
                    reading_order,
                    semantic_id,
                    parent_semantic_id,
                    annotation_ref: annotation_ref.map(|reference| (page_index, reference)),
                    parent: None,
                });
            }
            Some(mcid)
        })
        .collect();
    let list_parent_ids = elements
        .iter()
        .filter(|element| element.role == StructRole::LI)
        .filter_map(|element| element.parent_semantic_id.clone())
        .collect::<Vec<_>>();
    let mut unique_list_parent_ids = Vec::new();
    for parent_id in list_parent_ids {
        if !unique_list_parent_ids.contains(&parent_id) {
            unique_list_parent_ids.push(parent_id);
        }
    }
    for (parent_index, parent_id) in unique_list_parent_ids.into_iter().enumerate() {
        let reading_order = elements
            .iter()
            .filter(|element| element.parent_semantic_id.as_deref() == Some(parent_id.as_str()))
            .map(|element| element.reading_order)
            .min()
            .unwrap_or_default();
        elements.push(TagElement {
            reference: page_scoped_ref(page_index, LIST_PARENT_OFFSET, parent_index),
            mcids: Vec::new(),
            role: StructRole::L,
            alt_text: None,
            language: None,
            reading_order,
            semantic_id: parent_id,
            parent_semantic_id: None,
            annotation_ref: None,
            parent: None,
        });
    }
    let mut toc_parent_ids = Vec::new();
    for parent_id in elements
        .iter()
        .filter(|element| element.role == StructRole::Link)
        .filter_map(|element| element.parent_semantic_id.clone())
    {
        if !toc_parent_ids.contains(&parent_id) {
            toc_parent_ids.push(parent_id);
        }
    }
    for (parent_index, parent_id) in toc_parent_ids.into_iter().enumerate() {
        let reading_order = elements
            .iter()
            .filter(|element| element.parent_semantic_id.as_deref() == Some(parent_id.as_str()))
            .map(|element| element.reading_order)
            .min()
            .unwrap_or_default();
        elements.push(TagElement {
            reference: page_scoped_ref(page_index, TOC_PARENT_OFFSET, parent_index),
            mcids: Vec::new(),
            role: StructRole::TOCI,
            alt_text: None,
            language: None,
            reading_order,
            semantic_id: parent_id,
            parent_semantic_id: None,
            annotation_ref: None,
            parent: None,
        });
    }
    PageTags {
        image_mcids,
        line_mcids,
        elements,
    }
}

fn consolidate_tags(tag_maps: &[PageTags]) -> Vec<TagElement> {
    let mut result: Vec<TagElement> = Vec::new();
    for element in tag_maps.iter().flat_map(|tags| &tags.elements) {
        let existing = element
            .annotation_ref
            .is_none()
            .then(|| {
                result.iter().position(|candidate| {
                    candidate.annotation_ref.is_none()
                        && candidate.semantic_id == element.semantic_id
                        && candidate.role == element.role
                        && candidate.parent_semantic_id == element.parent_semantic_id
                        && candidate.language == element.language
                        && candidate.alt_text == element.alt_text
                })
            })
            .flatten();
        if let Some(index) = existing {
            result[index].mcids.extend(element.mcids.iter().copied());
            result[index].reading_order = result[index].reading_order.min(element.reading_order);
        } else {
            result.push(element.clone());
        }
    }
    let semantic_references = result
        .iter()
        .map(|element| (element.semantic_id.clone(), element.reference))
        .collect::<BTreeMap<_, _>>();
    for element in &mut result {
        element.parent = element
            .parent_semantic_id
            .as_ref()
            .and_then(|parent_id| semantic_references.get(parent_id).copied());
    }
    result
}

fn image_struct_role(role: Option<&str>) -> StructRole {
    let _ = role;
    StructRole::Figure
}

fn annotation_parent_key(page_count: usize, page_index: usize, annotation_reference: Ref) -> i32 {
    page_count as i32
        + page_index as i32 * PAGE_TAG_ITEM_LIMIT as i32
        + (annotation_reference.get() - annotation_ref(page_index, 0).get())
}

fn struct_role(role: LayoutSemanticRole) -> StructRole {
    match role {
        LayoutSemanticRole::Heading1 => StructRole::H1,
        LayoutSemanticRole::Heading2 => StructRole::H2,
        LayoutSemanticRole::Heading3 => StructRole::H3,
        LayoutSemanticRole::Heading4 => StructRole::H4,
        LayoutSemanticRole::Heading5 => StructRole::H5,
        LayoutSemanticRole::Heading6 => StructRole::H6,
        LayoutSemanticRole::ListItem => StructRole::LI,
        LayoutSemanticRole::Caption => StructRole::Caption,
        LayoutSemanticRole::Toc => StructRole::TOCI,
        LayoutSemanticRole::Credit | LayoutSemanticRole::Paragraph => StructRole::P,
    }
}

fn tag_ref(page_index: usize, mcid: i32) -> Ref {
    page_scoped_ref(page_index, 0, mcid as usize)
}

fn annotation_ref(page_index: usize, line_index: usize) -> Ref {
    page_scoped_ref(page_index, ANNOTATION_OFFSET, line_index)
}

fn page_scoped_ref(page_index: usize, offset: i32, item_index: usize) -> Ref {
    Ref::new(
        PAGE_TAG_OBJECT_BASE
            + page_index as i32 * PAGE_TAG_OBJECT_STRIDE
            + offset
            + item_index as i32,
    )
}

fn opacity_key(opacity: f32) -> u16 {
    (opacity.clamp(0.0, 1.0) * 1_000.0).round() as u16
}

fn opacity_name(opacity: u16) -> String {
    format!("GS{opacity}")
}

fn apply_opacity(content: &mut Content, opacity: f32) {
    let key = opacity_key(opacity);
    if key < 1_000 {
        content.set_parameters(Name(opacity_name(key).as_bytes()));
    }
}

fn extend_edge_art_into_print_bleed(pages: &mut [LayoutPage], options: &PdfOptions) {
    let bleed = options.interior_bleed;
    if bleed <= f32::EPSILON {
        return;
    }
    const EDGE_EPSILON: f32 = 0.25;
    for (page_index, page) in pages.iter_mut().enumerate() {
        if page.width_points.is_some() || page.kind == PageKind::Cover {
            continue;
        }
        let outer_left = page_index % 2 == 1;
        for image in &mut page.images {
            if image.rotation_degrees.abs() > f32::EPSILON {
                continue;
            }
            if outer_left && image.x <= EDGE_EPSILON {
                image.x -= bleed;
                image.width += bleed;
            } else if !outer_left && image.x + image.width >= options.trim_width - EDGE_EPSILON {
                image.width += bleed;
            }
            if image.y <= EDGE_EPSILON {
                image.y -= bleed;
                image.height += bleed;
            }
            if image.y + image.height >= options.trim_height - EDGE_EPSILON {
                image.height += bleed;
            }
        }
        for shape in &mut page.shapes {
            if shape.rotation_degrees.abs() > f32::EPSILON {
                continue;
            }
            if outer_left && shape.x <= EDGE_EPSILON {
                shape.x -= bleed;
                shape.width += bleed;
            } else if !outer_left && shape.x + shape.width >= options.trim_width - EDGE_EPSILON {
                shape.width += bleed;
            }
            if shape.y <= EDGE_EPSILON {
                shape.y -= bleed;
                shape.height += bleed;
            }
            if shape.y + shape.height >= options.trim_height - EDGE_EPSILON {
                shape.height += bleed;
            }
        }
    }
}

fn flatten_pdfx_opacity(
    pages: &mut [LayoutPage],
    fonts: &BTreeMap<FontFace, EmbeddedFont>,
    images: &mut BTreeMap<String, EmbeddedImage>,
    options: &PdfOptions,
    reject_overlaps: bool,
) -> Result<(), Diagnostic> {
    let mut flattened_images = BTreeMap::<(String, u16, [u8; 3]), String>::new();
    for (page_index, page) in pages.iter_mut().enumerate() {
        precompose_translucent_images_into_lower_art(page, images, page_index)?;
        flatten_translucent_artifacts_into_lower_art(
            page,
            fonts,
            images,
            page_index,
            options.width,
            options.height,
        )?;
        if reject_overlaps {
            reject_overlapping_pdfx_transparency(page, fonts, page_index)?;
        }
        let substrate = if page.kind == PageKind::Cover {
            options.background_rgb.unwrap_or([0.086, 0.196, 0.31])
        } else {
            [1.0, 1.0, 1.0]
        };
        for shape in &mut page.shapes {
            let alpha = shape.opacity.clamp(0.0, 1.0);
            if opacity_key(alpha) == 0 {
                shape.fill_rgb = None;
                shape.stroke_rgb = None;
                shape.opacity = 1.0;
                continue;
            }
            if alpha < 1.0 {
                shape.fill_rgb = shape
                    .fill_rgb
                    .map(|color| blend_rgb(color, substrate, alpha));
                shape.stroke_rgb = shape
                    .stroke_rgb
                    .map(|color| blend_rgb(color, substrate, alpha));
                shape.opacity = 1.0;
            }
        }
        for line in &mut page.lines {
            let alpha = line.opacity.clamp(0.0, 1.0);
            if alpha < 1.0 {
                let color =
                    line.fill_rgb
                        .unwrap_or(if line.light_text { [1.0; 3] } else { [0.0; 3] });
                line.fill_rgb = Some(blend_rgb(color, substrate, alpha));
                line.light_text = false;
                line.opacity = 1.0;
            }
        }
        for image in &mut page.images {
            let opacity = opacity_key(image.opacity);
            let has_source_alpha = images
                .get(&image.asset_id)
                .is_some_and(|source| source.alpha.is_some());
            if opacity >= 1_000 && !has_source_alpha {
                continue;
            }
            let substrate_key = substrate.map(|channel| (channel * 255.0).round() as u8);
            let cache_key = (image.asset_id.clone(), opacity, substrate_key);
            let flattened_id = if let Some(id) = flattened_images.get(&cache_key) {
                id.clone()
            } else {
                let source = images.get(&image.asset_id).ok_or_else(|| {
                    Diagnostic::error(
                        "PRESS_ASSET_READ_FAILED",
                        "An image selected for PDF/X opacity flattening was unavailable.",
                    )
                })?;
                let mut flattened = source.clone();
                let object_alpha = f32::from(opacity) / 1_000.0;
                if flattened.cmyk {
                    let background = rgb_to_bounded_cmyk(substrate);
                    for (pixel_index, pixel) in flattened.samples.chunks_exact_mut(4).enumerate() {
                        let alpha = object_alpha
                            * flattened
                                .alpha
                                .as_ref()
                                .map_or(1.0, |values| f32::from(values[pixel_index]) / 255.0);
                        for channel in 0..4 {
                            pixel[channel] =
                                blend_sample(pixel[channel], background[channel], alpha);
                        }
                    }
                    flattened.maximum_total_ink_percent = flattened
                        .samples
                        .chunks_exact(4)
                        .map(|pixel| {
                            pixel.iter().map(|channel| u32::from(*channel)).sum::<u32>() as f32
                                * 100.0
                                / 255.0
                        })
                        .fold(0.0_f32, f32::max);
                    if flattened.maximum_total_ink_percent > 240.001 {
                        return Err(Diagnostic::error(
                            "PRESS_TOTAL_INK_EXCEEDED",
                            "Flattened image content exceeds the 240% total-ink limit.",
                        ));
                    }
                } else if flattened.grayscale {
                    let background =
                        substrate[0] * 0.2126 + substrate[1] * 0.7152 + substrate[2] * 0.0722;
                    for (pixel_index, sample) in flattened.samples.iter_mut().enumerate() {
                        let alpha = object_alpha
                            * flattened
                                .alpha
                                .as_ref()
                                .map_or(1.0, |values| f32::from(values[pixel_index]) / 255.0);
                        *sample = blend_sample(*sample, background, alpha);
                    }
                } else {
                    for (pixel_index, pixel) in flattened.samples.chunks_exact_mut(3).enumerate() {
                        let alpha = object_alpha
                            * flattened
                                .alpha
                                .as_ref()
                                .map_or(1.0, |values| f32::from(values[pixel_index]) / 255.0);
                        for channel in 0..3 {
                            pixel[channel] =
                                blend_sample(pixel[channel], substrate[channel], alpha);
                        }
                    }
                }
                flattened.alpha = None;
                let id = format!(
                    "{}-pdfx-flat-{opacity}-{:02x}{:02x}{:02x}",
                    image.asset_id, substrate_key[0], substrate_key[1], substrate_key[2]
                );
                flattened.id = id.clone();
                images.insert(id.clone(), flattened);
                flattened_images.insert(cache_key, id.clone());
                id
            };
            image.asset_id = flattened_id;
            image.opacity = 1.0;
        }
    }
    Ok(())
}

fn precompose_translucent_images_into_lower_art(
    page: &mut LayoutPage,
    images: &mut BTreeMap<String, EmbeddedImage>,
    page_index: usize,
) -> Result<(), Diagnostic> {
    let paint_order = page.paint_order.clone();
    let mut baked_images = std::collections::BTreeSet::new();
    for (paint_position, paint) in paint_order.iter().enumerate() {
        let LayoutPaint::Image(foreground_index) = *paint else {
            continue;
        };
        let foreground_placement = &page.images[foreground_index];
        let foreground_has_alpha = images
            .get(&foreground_placement.asset_id)
            .is_some_and(|source| source.alpha.is_some());
        if opacity_key(foreground_placement.opacity) >= 1_000 && !foreground_has_alpha {
            continue;
        }
        let Some(LayoutPaint::Image(background_index)) = paint_order[..paint_position]
            .iter()
            .rev()
            .find(
                |paint| !matches!(paint, LayoutPaint::Image(index) if baked_images.contains(index)),
            )
            .copied()
        else {
            continue;
        };
        let background_placement = &page.images[background_index];
        let foreground_bounds = rotated_bounds(
            foreground_placement.x,
            foreground_placement.y,
            foreground_placement.width,
            foreground_placement.height,
            foreground_placement.rotation_degrees,
        );
        if background_placement.rotation_degrees.abs() > f32::EPSILON
            || opacity_key(background_placement.opacity) < 1_000
            || foreground_bounds[0] < background_placement.x - 0.01
            || foreground_bounds[1] < background_placement.y - 0.01
            || foreground_bounds[0] + foreground_bounds[2]
                > background_placement.x + background_placement.width + 0.01
            || foreground_bounds[1] + foreground_bounds[3]
                > background_placement.y + background_placement.height + 0.01
        {
            continue;
        }
        let Some(background_source) = images.get(&background_placement.asset_id).cloned() else {
            continue;
        };
        let Some(foreground_source) = images.get(&foreground_placement.asset_id) else {
            continue;
        };
        if background_source.alpha.is_some()
            || background_source.cmyk != foreground_source.cmyk
            || background_source.grayscale != foreground_source.grayscale
        {
            continue;
        }
        let mut flattened = background_source;
        if opacity_key(foreground_placement.opacity) > 0
            && !blend_image_into_image(
                &mut flattened,
                background_placement,
                foreground_source,
                foreground_placement,
            )
        {
            continue;
        }
        if flattened.maximum_total_ink_percent > 240.001 {
            return Err(Diagnostic::error(
                "PRESS_TOTAL_INK_EXCEEDED",
                "Precomposed image content exceeds the 240% total-ink limit.",
            ));
        }
        flattened.id = format!(
            "{}-page-{page_index}-image-{foreground_index}-flat",
            flattened.id
        );
        page.images[background_index]
            .asset_id
            .clone_from(&flattened.id);
        images.insert(flattened.id.clone(), flattened);
        baked_images.insert(foreground_index);
    }
    page.paint_order.retain(
        |paint| !matches!(paint, LayoutPaint::Image(index) if baked_images.contains(index)),
    );
    Ok(())
}

fn blend_image_into_image(
    background: &mut EmbeddedImage,
    background_placement: &crate::model::LayoutImage,
    foreground: &EmbeddedImage,
    foreground_placement: &crate::model::LayoutImage,
) -> bool {
    let Some(background_geometry) = image_draw_geometry(background_placement, background) else {
        return false;
    };
    let Some(foreground_geometry) = image_draw_geometry(foreground_placement, foreground) else {
        return false;
    };
    let channels = if background.cmyk {
        4
    } else if background.grayscale {
        1
    } else {
        3
    };
    let angle = foreground_placement.rotation_degrees.to_radians();
    let (sin, cos) = angle.sin_cos();
    let foreground_center_x = foreground_placement.x + foreground_placement.width / 2.0;
    let foreground_center_y = foreground_placement.y + foreground_placement.height / 2.0;
    let mut sampled = false;
    for row in 0..background.height {
        let page_y = background_geometry.origin_y
            + (1.0 - (row as f32 + 0.5) / background.height as f32)
                * background_geometry.drawn_height;
        if page_y < background_placement.y
            || page_y > background_placement.y + background_placement.height
        {
            continue;
        }
        for column in 0..background.width {
            let page_x = background_geometry.origin_x
                + (column as f32 + 0.5) / background.width as f32 * background_geometry.drawn_width;
            if page_x < background_placement.x
                || page_x > background_placement.x + background_placement.width
            {
                continue;
            }
            // Composition angles are clockwise in the editor's top-down coordinate system.
            // PDF page coordinates are bottom-up, so the final paint uses the inverse angle;
            // apply the corresponding inverse transform here to sample the unrotated frame.
            let delta_x = page_x - foreground_center_x;
            let delta_y = page_y - foreground_center_y;
            let local_x = foreground_center_x + delta_x * cos - delta_y * sin;
            let local_y = foreground_center_y + delta_x * sin + delta_y * cos;
            if local_x < foreground_placement.x
                || local_x > foreground_placement.x + foreground_placement.width
                || local_y < foreground_placement.y
                || local_y > foreground_placement.y + foreground_placement.height
            {
                continue;
            }
            let source_x = (local_x - foreground_geometry.origin_x)
                / foreground_geometry.drawn_width
                * foreground.width as f32
                - 0.5;
            let source_y = (1.0
                - (local_y - foreground_geometry.origin_y) / foreground_geometry.drawn_height)
                * foreground.height as f32
                - 0.5;
            if source_x < -0.5
                || source_y < -0.5
                || source_x > foreground.width as f32 - 0.5
                || source_y > foreground.height as f32 - 0.5
            {
                continue;
            }
            sampled = true;
            let source_alpha = foreground.alpha.as_ref().map_or(1.0, |alpha| {
                f32::from(sample_channel_bilinear(
                    alpha,
                    foreground.width,
                    foreground.height,
                    1,
                    0,
                    source_x,
                    source_y,
                )) / 255.0
            });
            let alpha = foreground_placement.opacity.clamp(0.0, 1.0) * source_alpha;
            if alpha <= 0.0 {
                continue;
            }
            let target_offset =
                (row as usize * background.width as usize + column as usize) * channels;
            for channel in 0..channels {
                let foreground_sample = sample_channel_bilinear(
                    &foreground.samples,
                    foreground.width,
                    foreground.height,
                    channels,
                    channel,
                    source_x,
                    source_y,
                );
                background.samples[target_offset + channel] = blend_sample(
                    foreground_sample,
                    f32::from(background.samples[target_offset + channel]) / 255.0,
                    alpha,
                );
            }
        }
    }
    if background.cmyk {
        background.maximum_total_ink_percent = background
            .samples
            .chunks_exact(4)
            .map(|pixel| {
                pixel.iter().map(|channel| u32::from(*channel)).sum::<u32>() as f32 * 100.0 / 255.0
            })
            .fold(0.0_f32, f32::max);
    }
    sampled
}

#[derive(Clone, Copy)]
struct ImageDrawGeometry {
    origin_x: f32,
    origin_y: f32,
    drawn_width: f32,
    drawn_height: f32,
}

fn image_draw_geometry(
    placement: &crate::model::LayoutImage,
    source: &EmbeddedImage,
) -> Option<ImageDrawGeometry> {
    if source.width == 0 || source.height == 0 {
        return None;
    }
    let source_fraction = placement.source_width_fraction.clamp(0.01, 1.0);
    let source_width = source.width as f32 * source_fraction;
    let width_scale = placement.width / source_width;
    let height_scale = placement.height / source.height as f32;
    let (drawn_width, drawn_height, visible_width) = match placement.fit {
        LayoutImageFit::Contain => {
            let scale = width_scale.min(height_scale);
            (
                source.width as f32 * scale,
                source.height as f32 * scale,
                source_width * scale,
            )
        }
        LayoutImageFit::Cover => {
            let scale = width_scale.max(height_scale);
            (
                source.width as f32 * scale,
                source.height as f32 * scale,
                source_width * scale,
            )
        }
        LayoutImageFit::Stretch => (
            placement.width / source_fraction,
            placement.height,
            placement.width,
        ),
    };
    let drawn_x = if source_fraction < 1.0 {
        -placement.width / 2.0 - drawn_width * placement.source_left_fraction.clamp(0.0, 1.0)
    } else {
        -placement.width / 2.0
            + (placement.width - visible_width) * placement.focal_x.clamp(0.0, 1.0)
    };
    let drawn_y = -placement.height / 2.0
        + (placement.height - drawn_height) * (1.0 - placement.focal_y.clamp(0.0, 1.0));
    Some(ImageDrawGeometry {
        origin_x: placement.x + placement.width / 2.0 + drawn_x,
        origin_y: placement.y + placement.height / 2.0 + drawn_y,
        drawn_width,
        drawn_height,
    })
}

#[allow(clippy::too_many_arguments)]
fn sample_channel_bilinear(
    samples: &[u8],
    width: u32,
    height: u32,
    channels: usize,
    channel: usize,
    x: f32,
    y: f32,
) -> u8 {
    let x = x.clamp(0.0, width.saturating_sub(1) as f32);
    let y = y.clamp(0.0, height.saturating_sub(1) as f32);
    let x0 = x.floor() as u32;
    let y0 = y.floor() as u32;
    let x1 = (x0 + 1).min(width - 1);
    let y1 = (y0 + 1).min(height - 1);
    let tx = x - x0 as f32;
    let ty = y - y0 as f32;
    let sample = |column: u32, row: u32| {
        f32::from(samples[(row as usize * width as usize + column as usize) * channels + channel])
    };
    let top = sample(x0, y0) * (1.0 - tx) + sample(x1, y0) * tx;
    let bottom = sample(x0, y1) * (1.0 - tx) + sample(x1, y1) * tx;
    (top * (1.0 - ty) + bottom * ty).round().clamp(0.0, 255.0) as u8
}

fn flatten_translucent_artifacts_into_lower_art(
    page: &mut LayoutPage,
    fonts: &BTreeMap<FontFace, EmbeddedFont>,
    images: &mut BTreeMap<String, EmbeddedImage>,
    page_index: usize,
    page_width: f32,
    page_height: f32,
) -> Result<(), Diagnostic> {
    let paint_order = page.paint_order.clone();
    let mut baked_shapes = std::collections::BTreeSet::new();
    let mut baked_lines = std::collections::BTreeSet::new();
    for (paint_position, paint) in paint_order.iter().enumerate() {
        let LayoutPaint::Shape(shape_index) = *paint else {
            continue;
        };
        let shape = &page.shapes[shape_index];
        if opacity_key(shape.opacity) >= 1_000
            || shape.fill_rgb.is_none()
            || shape.stroke_rgb.is_some()
            || shape.rotation_degrees.abs() > f32::EPSILON
        {
            continue;
        }
        let Some(image_index) = paint_order[..paint_position]
            .iter()
            .rev()
            .find_map(|paint| match *paint {
                LayoutPaint::Image(index) => Some(index),
                _ => None,
            })
        else {
            continue;
        };
        let image = &page.images[image_index];
        let visible_shape_left = shape.x.max(0.0);
        let visible_shape_bottom = shape.y.max(0.0);
        let visible_shape_right = (shape.x + shape.width).min(page_width);
        let visible_shape_top = (shape.y + shape.height).min(page_height);
        if image.rotation_degrees.abs() > f32::EPSILON
            || opacity_key(image.opacity) < 1_000
            || !matches!(image.fit, LayoutImageFit::Cover | LayoutImageFit::Stretch)
            || visible_shape_left < image.x - 0.01
            || visible_shape_bottom < image.y - 0.01
            || visible_shape_right > image.x + image.width + 0.01
            || visible_shape_top > image.y + image.height + 0.01
        {
            continue;
        }
        let Some(source) = images.get(&image.asset_id).cloned() else {
            continue;
        };
        let mut flattened = source;
        if !blend_shape_into_image(&mut flattened, image, shape) {
            continue;
        }
        flattened.id = format!(
            "{}-page-{page_index}-shape-{shape_index}-flat",
            flattened.id
        );
        page.images[image_index].asset_id.clone_from(&flattened.id);
        images.insert(flattened.id.clone(), flattened);
        baked_shapes.insert(shape_index);
    }

    for (paint_position, paint) in paint_order.iter().enumerate() {
        let LayoutPaint::Line(line_index) = *paint else {
            continue;
        };
        let line = &page.lines[line_index];
        if opacity_key(line.opacity) >= 1_000
            || !line.artifact
            || line.rotation_degrees.abs() > f32::EPSILON
        {
            continue;
        }
        let Some(image_index) = paint_order[..paint_position]
            .iter()
            .rev()
            .find_map(|paint| match *paint {
                LayoutPaint::Image(index) => Some(index),
                _ => None,
            })
        else {
            continue;
        };
        let image = &page.images[image_index];
        if image.rotation_degrees.abs() > f32::EPSILON
            || opacity_key(image.opacity) < 1_000
            || !matches!(image.fit, LayoutImageFit::Cover | LayoutImageFit::Stretch)
        {
            continue;
        }
        let Some(source) = images.get(&image.asset_id).cloned() else {
            continue;
        };
        let mut flattened = source;
        if !blend_line_into_image(&mut flattened, image, line, fonts)? {
            continue;
        }
        flattened.id = format!("{}-page-{page_index}-line-{line_index}-flat", flattened.id);
        page.images[image_index].asset_id.clone_from(&flattened.id);
        images.insert(flattened.id.clone(), flattened);
        baked_lines.insert(line_index);
    }
    page.paint_order.retain(|paint| match *paint {
        LayoutPaint::Shape(index) => !baked_shapes.contains(&index),
        LayoutPaint::Line(index) => !baked_lines.contains(&index),
        LayoutPaint::Image(_) => true,
    });
    Ok(())
}

fn blend_shape_into_image(
    source: &mut EmbeddedImage,
    image: &crate::model::LayoutImage,
    shape: &crate::model::LayoutShape,
) -> bool {
    let source_fraction = image.source_width_fraction.clamp(0.01, 1.0);
    let source_width = source.width as f32 * source_fraction;
    let width_scale = image.width / source_width;
    let height_scale = image.height / source.height as f32;
    let (drawn_width, drawn_height) = match image.fit {
        LayoutImageFit::Cover => {
            let scale = width_scale.max(height_scale);
            (source.width as f32 * scale, source.height as f32 * scale)
        }
        LayoutImageFit::Stretch => (image.width / source_fraction, image.height),
        LayoutImageFit::Contain => return false,
    };
    let visible_width = source_width * drawn_width / source.width as f32;
    let drawn_x = if source_fraction < 1.0 {
        -image.width / 2.0 - drawn_width * image.source_left_fraction.clamp(0.0, 1.0)
    } else {
        -image.width / 2.0 + (image.width - visible_width) * image.focal_x.clamp(0.0, 1.0)
    };
    let drawn_y =
        -image.height / 2.0 + (image.height - drawn_height) * (1.0 - image.focal_y.clamp(0.0, 1.0));
    let origin_x = image.x + image.width / 2.0 + drawn_x;
    let origin_y = image.y + image.height / 2.0 + drawn_y;
    let fill = shape.fill_rgb.expect("validated shape fill");
    let alpha = shape.opacity.clamp(0.0, 1.0);
    let channels = if source.cmyk {
        4
    } else if source.grayscale {
        1
    } else {
        3
    };
    let mut changed = false;
    for row in 0..source.height {
        let page_y = origin_y + (1.0 - (row as f32 + 0.5) / source.height as f32) * drawn_height;
        if page_y < image.y
            || page_y > image.y + image.height
            || page_y < shape.y
            || page_y > shape.y + shape.height
        {
            continue;
        }
        for column in 0..source.width {
            let page_x = origin_x + (column as f32 + 0.5) / source.width as f32 * drawn_width;
            if page_x < image.x
                || page_x > image.x + image.width
                || page_x < shape.x
                || page_x > shape.x + shape.width
            {
                continue;
            }
            let offset = (row as usize * source.width as usize + column as usize) * channels;
            if source.cmyk {
                let foreground = rgb_to_bounded_cmyk(fill);
                for (channel, foreground_channel) in foreground.iter().enumerate() {
                    source.samples[offset + channel] = blend_sample(
                        (*foreground_channel * 255.0).round() as u8,
                        f32::from(source.samples[offset + channel]) / 255.0,
                        alpha,
                    );
                }
            } else if source.grayscale {
                let foreground = fill[0] * 0.2126 + fill[1] * 0.7152 + fill[2] * 0.0722;
                source.samples[offset] = blend_sample(
                    (foreground * 255.0).round() as u8,
                    f32::from(source.samples[offset]) / 255.0,
                    alpha,
                );
            } else {
                for (channel, fill_channel) in fill.iter().enumerate() {
                    source.samples[offset + channel] = blend_sample(
                        (*fill_channel * 255.0).round() as u8,
                        f32::from(source.samples[offset + channel]) / 255.0,
                        alpha,
                    );
                }
            }
            changed = true;
        }
    }
    if source.cmyk {
        source.maximum_total_ink_percent = source
            .samples
            .chunks_exact(4)
            .map(|pixel| {
                pixel.iter().map(|channel| u32::from(*channel)).sum::<u32>() as f32 * 100.0 / 255.0
            })
            .fold(0.0_f32, f32::max);
    }
    changed
}

fn blend_line_into_image(
    source: &mut EmbeddedImage,
    image: &crate::model::LayoutImage,
    line: &LayoutLine,
    fonts: &BTreeMap<FontFace, EmbeddedFont>,
) -> Result<bool, Diagnostic> {
    let edges = line_outline_edges(line, fonts)?;
    if edges.is_empty() {
        return Ok(false);
    }
    let left = edges
        .iter()
        .flat_map(|edge| [edge.start[0], edge.end[0]])
        .fold(f32::INFINITY, f32::min);
    let right = edges
        .iter()
        .flat_map(|edge| [edge.start[0], edge.end[0]])
        .fold(f32::NEG_INFINITY, f32::max);
    let bottom = edges
        .iter()
        .flat_map(|edge| [edge.start[1], edge.end[1]])
        .fold(f32::INFINITY, f32::min);
    let top = edges
        .iter()
        .flat_map(|edge| [edge.start[1], edge.end[1]])
        .fold(f32::NEG_INFINITY, f32::max);
    let source_fraction = image.source_width_fraction.clamp(0.01, 1.0);
    let source_width = source.width as f32 * source_fraction;
    let width_scale = image.width / source_width;
    let height_scale = image.height / source.height as f32;
    let (drawn_width, drawn_height) = match image.fit {
        LayoutImageFit::Cover => {
            let scale = width_scale.max(height_scale);
            (source.width as f32 * scale, source.height as f32 * scale)
        }
        LayoutImageFit::Stretch => (image.width / source_fraction, image.height),
        LayoutImageFit::Contain => return Ok(false),
    };
    let visible_width = source_width * drawn_width / source.width as f32;
    let drawn_x = if source_fraction < 1.0 {
        -image.width / 2.0 - drawn_width * image.source_left_fraction.clamp(0.0, 1.0)
    } else {
        -image.width / 2.0 + (image.width - visible_width) * image.focal_x.clamp(0.0, 1.0)
    };
    let drawn_y =
        -image.height / 2.0 + (image.height - drawn_height) * (1.0 - image.focal_y.clamp(0.0, 1.0));
    let origin_x = image.x + image.width / 2.0 + drawn_x;
    let origin_y = image.y + image.height / 2.0 + drawn_y;
    let pixel_width = drawn_width / source.width as f32;
    let pixel_height = drawn_height / source.height as f32;
    let fill = line.fill_rgb.unwrap_or([0.0; 3]);
    let channels = if source.cmyk {
        4
    } else if source.grayscale {
        1
    } else {
        3
    };
    let mut changed = false;
    for row in 0..source.height {
        let page_y = origin_y + (1.0 - (row as f32 + 0.5) / source.height as f32) * drawn_height;
        if page_y < image.y || page_y > image.y + image.height || page_y < bottom || page_y > top {
            continue;
        }
        for column in 0..source.width {
            let page_x = origin_x + (column as f32 + 0.5) / source.width as f32 * drawn_width;
            if page_x < image.x || page_x > image.x + image.width || page_x < left || page_x > right
            {
                continue;
            }
            let coverage = outline_coverage(&edges, page_x, page_y, pixel_width, pixel_height);
            let alpha = line.opacity.clamp(0.0, 1.0) * coverage;
            if alpha <= 0.0 {
                continue;
            }
            let offset = (row as usize * source.width as usize + column as usize) * channels;
            if source.cmyk {
                let foreground = rgb_to_bounded_cmyk(fill);
                for (channel, foreground_channel) in foreground.iter().enumerate() {
                    source.samples[offset + channel] = blend_sample(
                        (*foreground_channel * 255.0).round() as u8,
                        f32::from(source.samples[offset + channel]) / 255.0,
                        alpha,
                    );
                }
            } else if source.grayscale {
                let foreground = fill[0] * 0.2126 + fill[1] * 0.7152 + fill[2] * 0.0722;
                source.samples[offset] = blend_sample(
                    (foreground * 255.0).round() as u8,
                    f32::from(source.samples[offset]) / 255.0,
                    alpha,
                );
            } else {
                for (channel, fill_channel) in fill.iter().enumerate() {
                    source.samples[offset + channel] = blend_sample(
                        (*fill_channel * 255.0).round() as u8,
                        f32::from(source.samples[offset + channel]) / 255.0,
                        alpha,
                    );
                }
            }
            changed = true;
        }
    }
    if source.cmyk {
        source.maximum_total_ink_percent = source
            .samples
            .chunks_exact(4)
            .map(|pixel| {
                pixel.iter().map(|channel| u32::from(*channel)).sum::<u32>() as f32 * 100.0 / 255.0
            })
            .fold(0.0_f32, f32::max);
        if source.maximum_total_ink_percent > 240.001 {
            return Err(Diagnostic::error(
                "PRESS_TOTAL_INK_EXCEEDED",
                "Flattened image content exceeds the 240% total-ink limit.",
            ));
        }
    }
    Ok(changed)
}

fn line_outline_edges(
    line: &LayoutLine,
    fonts: &BTreeMap<FontFace, EmbeddedFont>,
) -> Result<Vec<OutlineEdge>, Diagnostic> {
    let fallback;
    let runs = if line.runs.is_empty() {
        fallback = vec![LayoutRun {
            note_reference_id: None,
            text: line.text.clone(),
            face: FontFace::SerifRegular,
            underline: false,
            strikethrough: false,
            baseline_shift_em: 0.0,
            size_scale: 1.0,
            language: None,
        }];
        fallback.as_slice()
    } else {
        line.runs.as_slice()
    };
    let mut cursor_x = line.x;
    let mut edges = Vec::new();
    for run in runs {
        let font = fonts.get(&run.face).ok_or_else(|| {
            Diagnostic::error(
                "PRESS_FONT_REFERENCE_MISSING",
                format!("Layout references an unavailable font face {:?}.", run.face),
            )
        })?;
        let run_size = line.size * run.size_scale;
        let (run_edges, advance) = font.outline_edges(
            &run.text,
            run_size,
            cursor_x,
            line.y + run_size * run.baseline_shift_em,
            line.character_spacing,
            line.word_spacing,
        )?;
        edges.extend(run_edges);
        cursor_x += advance;
    }
    Ok(edges)
}

fn outline_coverage(
    edges: &[OutlineEdge],
    x: f32,
    y: f32,
    pixel_width: f32,
    pixel_height: f32,
) -> f32 {
    let samples = [(-0.25, -0.25), (0.25, -0.25), (-0.25, 0.25), (0.25, 0.25)];
    samples
        .iter()
        .filter(|(offset_x, offset_y)| {
            outline_contains(
                edges,
                x + offset_x * pixel_width,
                y + offset_y * pixel_height,
            )
        })
        .count() as f32
        / samples.len() as f32
}

fn outline_contains(edges: &[OutlineEdge], x: f32, y: f32) -> bool {
    let mut winding = 0_i32;
    for edge in edges {
        let [x0, y0] = edge.start;
        let [x1, y1] = edge.end;
        if (y0 <= y && y1 > y) || (y1 <= y && y0 > y) {
            let crossing_x = x0 + (y - y0) * (x1 - x0) / (y1 - y0);
            if crossing_x > x {
                winding += if y1 > y0 { 1 } else { -1 };
            }
        }
    }
    winding != 0
}

fn reject_overlapping_pdfx_transparency(
    page: &LayoutPage,
    fonts: &BTreeMap<FontFace, EmbeddedFont>,
    page_index: usize,
) -> Result<(), Diagnostic> {
    let paint = page_paint_bounds(page, fonts);
    let mut lower_bounds = Vec::<[f32; 4]>::new();
    for (paint_index, (bounds, opacity)) in paint.into_iter().enumerate() {
        if opacity_key(opacity) < 1_000
            && lower_bounds
                .iter()
                .any(|lower| bounds_overlap(bounds, *lower))
        {
            let mut diagnostic = Diagnostic::error(
                "PRESS_PDFX_TRANSPARENCY_OVERLAP",
                "PDF/X-1a cannot preserve this overlapping transparency while keeping semantic text and vector objects intact. Make the object opaque or combine the visual artwork into one image.",
            )
            .with_page(page_index + 1);
            if let Some(LayoutPaint::Line(line_index)) = page.paint_order.get(paint_index)
                && let Some(source_id) = &page.lines[*line_index].semantic_id
            {
                diagnostic = diagnostic.with_source("composition-object", source_id.clone());
            }
            return Err(diagnostic);
        }
        if opacity_key(opacity) > 0 {
            lower_bounds.push(bounds);
        }
    }
    Ok(())
}

fn page_paint_bounds(
    page: &LayoutPage,
    fonts: &BTreeMap<FontFace, EmbeddedFont>,
) -> Vec<([f32; 4], f32)> {
    let shape = |index: usize| {
        let item = &page.shapes[index];
        (
            rotated_bounds(
                item.x,
                item.y,
                item.width,
                item.height.max(item.stroke_width),
                item.rotation_degrees,
            ),
            item.opacity,
        )
    };
    let image = |index: usize| {
        let item = &page.images[index];
        (
            rotated_bounds(
                item.x,
                item.y,
                item.width,
                item.height,
                item.rotation_degrees,
            ),
            item.opacity,
        )
    };
    let line = |index: usize| {
        let item = &page.lines[index];
        let shaped_width = if item.runs.is_empty() {
            fonts[&FontFace::SerifRegular].advance_width(&item.text, item.size)
        } else {
            item.runs
                .iter()
                .map(|run| fonts[&run.face].advance_width(&run.text, item.size * run.size_scale))
                .sum::<f32>()
        };
        let width = shaped_width
            + item.word_spacing
                * item
                    .text
                    .chars()
                    .filter(|character| character.is_whitespace())
                    .count() as f32
            + item.character_spacing * item.text.chars().count().saturating_sub(1) as f32;
        let height = item.size * 1.2;
        (
            rotated_line_bounds(item, width.max(item.size * 0.25), height),
            item.opacity,
        )
    };
    if page.paint_order.is_empty() {
        return (0..page.shapes.len())
            .map(shape)
            .chain((0..page.images.len()).map(image))
            .chain((0..page.lines.len()).map(line))
            .collect();
    }
    page.paint_order
        .iter()
        .map(|item| match *item {
            LayoutPaint::Shape(index) => shape(index),
            LayoutPaint::Image(index) => image(index),
            LayoutPaint::Line(index) => line(index),
        })
        .collect()
}

fn rotated_line_bounds(line: &LayoutLine, width: f32, height: f32) -> [f32; 4] {
    if line.rotation_degrees.abs() <= f32::EPSILON {
        return [line.x, line.y - height, width, height];
    }
    let origin_x = line.rotation_origin_x.unwrap_or(line.x);
    let origin_y = line.rotation_origin_y.unwrap_or(line.y);
    let radians = line.rotation_degrees.to_radians();
    let (sin, cos) = radians.sin_cos();
    let mut minimum_x = f32::INFINITY;
    let mut minimum_y = f32::INFINITY;
    let mut maximum_x = f32::NEG_INFINITY;
    let mut maximum_y = f32::NEG_INFINITY;
    for (x, y) in [
        (line.x, line.y - height),
        (line.x + width, line.y - height),
        (line.x, line.y),
        (line.x + width, line.y),
    ] {
        let rotated_x = origin_x + (x - origin_x) * cos - (y - origin_y) * sin;
        let rotated_y = origin_y + (x - origin_x) * sin + (y - origin_y) * cos;
        minimum_x = minimum_x.min(rotated_x);
        minimum_y = minimum_y.min(rotated_y);
        maximum_x = maximum_x.max(rotated_x);
        maximum_y = maximum_y.max(rotated_y);
    }
    [
        minimum_x,
        minimum_y,
        maximum_x - minimum_x,
        maximum_y - minimum_y,
    ]
}

fn rotated_bounds(x: f32, y: f32, width: f32, height: f32, degrees: f32) -> [f32; 4] {
    if degrees.abs() <= f32::EPSILON {
        return [x, y, width, height];
    }
    let radians = degrees.to_radians();
    let rotated_width = width * radians.cos().abs() + height * radians.sin().abs();
    let rotated_height = width * radians.sin().abs() + height * radians.cos().abs();
    [
        x + (width - rotated_width) / 2.0,
        y + (height - rotated_height) / 2.0,
        rotated_width,
        rotated_height,
    ]
}

fn bounds_overlap(left: [f32; 4], right: [f32; 4]) -> bool {
    left[0] < right[0] + right[2]
        && right[0] < left[0] + left[2]
        && left[1] < right[1] + right[3]
        && right[1] < left[1] + left[3]
}

fn blend_rgb(foreground: [f32; 3], background: [f32; 3], alpha: f32) -> [f32; 3] {
    [
        foreground[0] * alpha + background[0] * (1.0 - alpha),
        foreground[1] * alpha + background[1] * (1.0 - alpha),
        foreground[2] * alpha + background[2] * (1.0 - alpha),
    ]
}

fn blend_sample(foreground: u8, background: f32, alpha: f32) -> u8 {
    (f32::from(foreground) * alpha + background.clamp(0.0, 1.0) * 255.0 * (1.0 - alpha))
        .round()
        .clamp(0.0, 255.0) as u8
}

fn parse_hex_color(value: &str) -> Option<[f32; 3]> {
    let value = value.strip_prefix('#')?;
    if value.len() != 6 {
        return None;
    }
    let red = u8::from_str_radix(&value[0..2], 16).ok()?;
    let green = u8::from_str_radix(&value[2..4], 16).ok()?;
    let blue = u8::from_str_radix(&value[4..6], 16).ok()?;
    Some([
        red as f32 / 255.0,
        green as f32 / 255.0,
        blue as f32 / 255.0,
    ])
}

fn rgb_to_bounded_cmyk([red, green, blue]: [f32; 3]) -> [f32; 4] {
    let black = 1.0 - red.max(green).max(blue);
    if black >= 0.999 {
        return [0.0, 0.0, 0.0, 1.0];
    }
    let denominator = 1.0 - black;
    let mut values = [
        (1.0 - red - black) / denominator,
        (1.0 - green - black) / denominator,
        (1.0 - blue - black) / denominator,
        black,
    ];
    let total = values.iter().sum::<f32>();
    if total > 2.4 {
        let scale = 2.4 / total;
        for value in &mut values {
            *value *= scale;
        }
    }
    values
}

fn rgb_luminance([red, green, blue]: [f32; 3]) -> f32 {
    red * 0.2126 + green * 0.7152 + blue * 0.0722
}

pub fn cover_background_total_ink_percent(value: &str) -> f32 {
    parse_hex_color(value)
        .map(rgb_to_bounded_cmyk)
        .map_or(0.0, |channels| channels.iter().sum::<f32>() * 100.0)
}

fn compress(bytes: &[u8]) -> Result<Vec<u8>, Diagnostic> {
    let mut encoder = ZlibEncoder::new(Vec::new(), Compression::best());
    encoder
        .write_all(bytes)
        .map_err(|error| Diagnostic::error("PRESS_COMPRESSION_FAILED", error.to_string()))?;
    encoder
        .finish()
        .map_err(|error| Diagnostic::error("PRESS_COMPRESSION_FAILED", error.to_string()))
}

fn compress_image(bytes: &[u8]) -> Result<Vec<u8>, Diagnostic> {
    // Publication rasters dominate render time and package size. Level 3 keeps
    // the PDF stream fully lossless and deterministic while avoiding the very
    // high CPU cost of DEFLATE level 9 on every full-page image.
    let mut encoder = ZlibEncoder::new(Vec::new(), Compression::new(3));
    encoder
        .write_all(bytes)
        .map_err(|error| Diagnostic::error("PRESS_COMPRESSION_FAILED", error.to_string()))?;
    encoder
        .finish()
        .map_err(|error| Diagnostic::error("PRESS_COMPRESSION_FAILED", error.to_string()))
}

#[cfg(test)]
mod tests {
    use std::collections::BTreeSet;

    use super::*;

    #[test]
    fn page_scoped_pdf_object_references_remain_unique_across_long_books() {
        let page_count = 300;
        let mut references = BTreeSet::new();
        for page_index in 0..page_count {
            for item_index in 0..100 {
                assert!(references.insert(tag_ref(page_index, item_index).get()));
                assert!(references.insert(
                    page_scoped_ref(page_index, LIST_PARENT_OFFSET, item_index as usize).get()
                ));
                assert!(references.insert(
                    page_scoped_ref(page_index, TOC_PARENT_OFFSET, item_index as usize).get()
                ));
                assert!(references.insert(annotation_ref(page_index, item_index as usize).get()));
            }
        }
        let opacity_base = PAGE_TAG_OBJECT_BASE + page_count as i32 * PAGE_TAG_OBJECT_STRIDE;
        assert!(references.iter().all(|reference| *reference < opacity_base));
    }
}
