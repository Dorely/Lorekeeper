use std::collections::BTreeMap;
use std::io::Write;

use flate2::Compression;
use flate2::write::ZlibEncoder;
use pdf_writer::types::{CidFontType, FontFlags, OutputIntentSubtype, SystemInfo, UnicodeCmap};
use pdf_writer::{Content, Filter, Finish, Name, Pdf, Rect, Ref, Str, TextStr};

use crate::font::EmbeddedFont;
use crate::image::EmbeddedImage;
use crate::model::{Diagnostic, FontFace, LayoutPage, LayoutRun, PageKind, RenderRequest};

const ICC_PROFILE: &[u8] = include_bytes!("../assets/profiles/CGATS21_CRPC1.icc");

#[derive(Debug, Clone)]
pub struct PdfOptions {
    pub pdf_x: bool,
    pub width: f32,
    pub height: f32,
    pub trim: Rect,
    pub bleed: Rect,
    pub title: String,
    pub author: String,
    pub background_rgb: Option<[f32; 3]>,
    pub expected_image_color_space: ImageColorSpace,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ImageColorSpace {
    Gray,
    Rgb,
    Cmyk,
}

impl PdfOptions {
    pub fn interior(request: &RenderRequest, pdf_x: bool) -> Self {
        let width = request.trim.width_inches * 72.0;
        let height = request.trim.height_inches * 72.0;
        Self {
            pdf_x,
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
            background_rgb: None,
            expected_image_color_space: if request.ink == "BlackAndWhite" {
                ImageColorSpace::Gray
            } else if pdf_x {
                ImageColorSpace::Cmyk
            } else {
                ImageColorSpace::Rgb
            },
        }
    }

    pub fn cover(request: &RenderRequest, pdf_x: bool, width: f32, _spine_width: f32) -> Self {
        let bleed_points = request
            .cover
            .as_ref()
            .map_or(0.0, |cover| cover.bleed_inches * 72.0);
        let height = request.trim.height_inches * 72.0 + bleed_points * 2.0;
        Self {
            pdf_x,
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
    if pages.is_empty() {
        return Err(Diagnostic::error(
            "PRESS_LAYOUT_EMPTY",
            "A PDF must contain at least one page.",
        ));
    }
    let mut pdf = Pdf::new();
    pdf.set_version(1, if options.pdf_x { 3 } else { 7 });
    let catalog_id = Ref::new(1);
    let pages_id = Ref::new(2);
    let info_id = Ref::new(3);
    let icc_id = Ref::new(4);

    {
        let mut catalog = pdf.catalog(catalog_id);
        catalog.pages(pages_id);
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
        }
    }
    {
        let mut info = pdf.document_info(info_id);
        info.title(TextStr(&options.title));
        info.author(TextStr(&options.author));
        info.creator(TextStr("Lorekeeper Press 1.0"));
        info.producer(TextStr("Lorekeeper Press 1.0"));
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
    let page_ids: Vec<Ref> = (0..pages.len())
        .map(|index| Ref::new(2_000 + index as i32 * 2))
        .collect();
    pdf.pages(pages_id)
        .kids(page_ids.iter().copied())
        .count(pages.len() as i32);
    for (index, page_model) in pages.iter().enumerate() {
        if cancelled() {
            return Err(Diagnostic::error(
                "PRESS_RENDER_CANCELLED",
                "Rendering was cancelled during PDF composition.",
            ));
        }
        let page_id = page_ids[index];
        let content_id = Ref::new(page_id.get() + 1);
        let mut page = pdf.page(page_id);
        page.parent(pages_id);
        page.media_box(Rect::new(0.0, 0.0, options.width, options.height));
        page.crop_box(options.bleed);
        page.bleed_box(options.bleed);
        page.trim_box(options.trim);
        page.contents(content_id);
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
        }
        page.finish();

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
                .rect(0.0, 0.0, options.width, options.height)
                .fill_nonzero();
        } else if page_model.kind == PageKind::Picture {
            if options.pdf_x {
                content.set_fill_cmyk(0.18, 0.08, 0.0, 0.06);
            } else {
                content.set_fill_gray(0.88);
            }
            content
                .rect(18.0, 18.0, options.width - 36.0, options.height - 36.0)
                .fill_nonzero();
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
        for image in &page_model.images {
            let image_ref = image_references
                .get(&image.asset_id)
                .expect("validated image reference");
            let source = &images[&image.asset_id];
            let source_width = source.width as f32 * image.source_width_fraction.clamp(0.01, 1.0);
            let rotated = (image.rotation_degrees - 90.0).abs() < f32::EPSILON;
            let target_source_width = if rotated {
                source.height as f32
            } else {
                source_width
            };
            let target_source_height = if rotated {
                source_width
            } else {
                source.height as f32
            };
            let width_scale = image.width / target_source_width;
            let height_scale = image.height / target_source_height;
            let scale = if image.contain {
                width_scale.min(height_scale)
            } else {
                width_scale.max(height_scale)
            };
            let drawn_width = source_width * scale;
            let drawn_height = source.height as f32 * scale;
            let visible_width = if rotated { drawn_height } else { drawn_width };
            let visible_height = if rotated { drawn_width } else { drawn_height };
            let drawn_x = if image.source_width_fraction < 1.0 {
                image.x - source.width as f32 * image.source_left_fraction.clamp(0.0, 1.0) * scale
            } else {
                image.x + (image.width - visible_width) * image.focal_x.clamp(0.0, 1.0)
            };
            let drawn_y =
                image.y + (image.height - visible_height) * (1.0 - image.focal_y.clamp(0.0, 1.0));
            let name = format!("Im{}", image_ref.get());
            content.save_state();
            content
                .rect(image.x, image.y, image.width, image.height)
                .clip_nonzero()
                .end_path();
            if rotated {
                content.transform([
                    0.0,
                    drawn_width,
                    -drawn_height,
                    0.0,
                    drawn_x + drawn_height,
                    drawn_y,
                ]);
            } else {
                content.transform([drawn_width, 0.0, 0.0, drawn_height, drawn_x, drawn_y]);
            }
            content.x_object(Name(name.as_bytes()));
            content.restore_state();
        }
        if let Some(modules) = &page_model.barcode_modules {
            let module_width = 1.15;
            let origin_x = options.width * 0.08;
            let origin_y = options.height * 0.10;
            if options.pdf_x {
                content.set_fill_cmyk(0.0, 0.0, 0.0, 0.0);
            } else {
                content.set_fill_gray(1.0);
            }
            content
                .rect(
                    origin_x - 9.0,
                    origin_y - 18.0,
                    modules.len() as f32 * module_width + 18.0,
                    66.0,
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
        }
        for line in &page_model.lines {
            if options.pdf_x {
                content.set_fill_cmyk(0.0, 0.0, 0.0, if line.light_text { 0.0 } else { 1.0 });
            } else {
                content.set_fill_gray(if line.light_text { 1.0 } else { 0.0 });
            }
            let fallback;
            let runs = if line.runs.is_empty() {
                fallback = vec![LayoutRun {
                    text: line.text.clone(),
                    face: FontFace::SerifRegular,
                    underline: false,
                    strikethrough: false,
                    baseline_shift_em: 0.0,
                    size_scale: 1.0,
                }];
                fallback.as_slice()
            } else {
                line.runs.as_slice()
            };
            let mut cursor_x = line.x;
            for run in runs {
                let font = fonts.get(&run.face).ok_or_else(|| {
                    Diagnostic::error(
                        "PRESS_FONT_REFERENCE_MISSING",
                        format!("Layout references an unavailable font face {:?}.", run.face),
                    )
                })?;
                let run_size = line.size * run.size_scale;
                let baseline = line.y + line.size * run.baseline_shift_em;
                content.begin_text();
                content.set_font(Name(font_resource_name(run.face).as_bytes()), run_size);
                let mut shaped_advance = 0.0;
                for glyph in font.shape(&run.text, run_size) {
                    let encoded = glyph.cid.to_be_bytes();
                    let matrix = if (line.rotation_degrees - 90.0).abs() < f32::EPSILON {
                        [
                            0.0,
                            1.0,
                            -1.0,
                            0.0,
                            cursor_x - glyph.y_offset,
                            baseline + shaped_advance + glyph.x_offset,
                        ]
                    } else {
                        [
                            1.0,
                            0.0,
                            0.0,
                            1.0,
                            cursor_x + shaped_advance + glyph.x_offset,
                            baseline + glyph.y_offset,
                        ]
                    };
                    content.set_text_matrix(matrix);
                    content.show(Str(&encoded));
                    shaped_advance += glyph.x_advance;
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
        }
        if let Some(label) = &page_model.page_label {
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
            content.set_text_matrix([1.0, 0.0, 0.0, 1.0, options.width / 2.0, 18.0]);
            content.show(Str(&encoded));
            content.end_text();
        }
        pdf.stream(content_id, &content.finish());
    }

    for image in images.values() {
        if cancelled() {
            return Err(Diagnostic::error(
                "PRESS_RENDER_CANCELLED",
                "Rendering was cancelled during image serialization.",
            ));
        }
        let image_ref = image_references[&image.id];
        let compressed = compress(&image.samples)?;
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
            cid.subtype(CidFontType::Type2)
                .base_font(Name(font.postscript_name.as_bytes()))
                .system_info(system_info)
                .font_descriptor(descriptor_id)
                .default_width(500.0)
                .cid_to_gid_map_predefined(Name(b"Identity"));
            let maximum = font.widths.keys().copied().max().unwrap_or(0);
            let widths = (0..=maximum).map(|cid| font.widths.get(&cid).copied().unwrap_or(500.0));
            cid.widths().consecutive(0, widths);
        }
        let mut flags = FontFlags::NON_SYMBOLIC;
        if matches!(face.family(), crate::model::FontFamily::Serif) {
            flags |= FontFlags::SERIF;
        }
        let is_italic = matches!(
            face,
            FontFace::SerifItalic
                | FontFace::SerifBoldItalic
                | FontFace::SansItalic
                | FontFace::SansBoldItalic
                | FontFace::MonoItalic
                | FontFace::MonoBoldItalic
        );
        if is_italic {
            flags |= FontFlags::ITALIC;
        }
        pdf.font_descriptor(descriptor_id)
            .name(Name(font.postscript_name.as_bytes()))
            .flags(flags)
            .bbox(Rect::new(-600.0, -300.0, 1400.0, 1100.0))
            .italic_angle(if is_italic { -12.0 } else { 0.0 })
            .ascent(1000.0)
            .descent(-300.0)
            .cap_height(700.0)
            .stem_v(80.0)
            .font_file2(font_file_id);
        let compressed_font = compress(&font.bytes)?;
        pdf.stream(font_file_id, &compressed_font)
            .filter(Filter::FlateDecode);
        let mut unicode = UnicodeCmap::new(Name(b"LorekeeperUnicode"), system_info);
        for (cid, characters) in &font.unicode_sequences {
            unicode.pair_with_multiple(*cid, characters.iter().copied());
        }
        let unicode_bytes = unicode.finish();
        pdf.cmap(to_unicode_id, unicode_bytes.as_slice());
    }
    if options.pdf_x {
        let compressed_icc = compress(ICC_PROFILE)?;
        let mut profile = pdf.icc_profile(icc_id, &compressed_icc);
        profile.n(4);
        profile.alternate().device_cmyk();
        profile.filter(Filter::FlateDecode);
    }
    Ok(pdf.finish())
}

fn font_resource_name(face: FontFace) -> String {
    format!("F{}", face as u8 + 1)
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
