use std::collections::{BTreeMap, BTreeSet};
use std::sync::{OnceLock, RwLock};

use harfrust::{FontRef, ShapeOptions, ShaperData, UnicodeBuffer};
use skrifa::{
    FontRef as OutlineFontRef, GlyphId, MetadataProvider,
    instance::{LocationRef, Size},
    outline::{DrawSettings, OutlinePen},
};
use subsetter::GlyphRemapper;

use crate::model::{Diagnostic, FontDeclaration, FontFace, LayoutPage};

pub const BODY_FONT: &[u8] = include_bytes!("../../Lorekeeper/wwwroot/fonts/lora/Lora-Regular.ttf");
const LORA_ITALIC: &[u8] = include_bytes!("../../Lorekeeper/wwwroot/fonts/lora/Lora-Italic.ttf");
const LORA_BOLD: &[u8] = include_bytes!("../../Lorekeeper/wwwroot/fonts/lora/Lora-Bold.ttf");
const LORA_BOLD_ITALIC: &[u8] =
    include_bytes!("../../Lorekeeper/wwwroot/fonts/lora/Lora-BoldItalic.ttf");
const NUNITO_REGULAR: &[u8] =
    include_bytes!("../../Lorekeeper/wwwroot/fonts/nunito/Nunito-Regular.ttf");
pub const HEADING_FONT: &[u8] =
    include_bytes!("../../Lorekeeper/wwwroot/fonts/nunito/Nunito-Bold.ttf");
const NUNITO_ITALIC: &[u8] =
    include_bytes!("../../Lorekeeper/wwwroot/fonts/nunito/Nunito-Italic.ttf");
const NUNITO_BOLD_ITALIC: &[u8] =
    include_bytes!("../../Lorekeeper/wwwroot/fonts/nunito/Nunito-BoldItalic.ttf");
pub const MONO_FONT: &[u8] =
    include_bytes!("../../Lorekeeper/wwwroot/fonts/roboto-mono/RobotoMono-Regular.ttf");
const MONO_ITALIC: &[u8] =
    include_bytes!("../../Lorekeeper/wwwroot/fonts/roboto-mono/RobotoMono-Italic.ttf");
const MONO_BOLD: &[u8] =
    include_bytes!("../../Lorekeeper/wwwroot/fonts/roboto-mono/RobotoMono-Bold.ttf");
const MONO_BOLD_ITALIC: &[u8] =
    include_bytes!("../../Lorekeeper/wwwroot/fonts/roboto-mono/RobotoMono-BoldItalic.ttf");

#[derive(Debug, Clone)]
pub struct EmbeddedFont {
    pub face: FontFace,
    pub postscript_name: &'static str,
    pub open_type: bool,
    pub bytes: Vec<u8>,
    pub character_ids: BTreeMap<char, u16>,
    pub unicode_sequences: BTreeMap<u16, Vec<char>>,
    pub widths: BTreeMap<u16, f32>,
    glyph_ids: BTreeMap<u16, u16>,
    source: &'static [u8],
    units_per_em: f32,
}

#[derive(Debug, Clone)]
struct CustomFont {
    family_key: String,
    weight: u16,
    italic: bool,
    source: &'static [u8],
    postscript_name: &'static str,
    open_type: bool,
}

static CUSTOM_FONTS: OnceLock<RwLock<Vec<CustomFont>>> = OnceLock::new();

fn custom_fonts() -> &'static RwLock<Vec<CustomFont>> {
    CUSTOM_FONTS.get_or_init(|| RwLock::new(Vec::new()))
}

pub fn configure_custom_fonts(
    declarations: &[FontDeclaration],
    sources: &BTreeMap<String, Vec<u8>>,
) -> Result<(), Diagnostic> {
    let mut configured = Vec::with_capacity(declarations.len());
    for declaration in declarations {
        let bytes = sources.get(&declaration.id).ok_or_else(|| {
            Diagnostic::error(
                "PRESS_FONT_MISSING",
                format!("Declared font '{}' was not staged.", declaration.id),
            )
        })?;
        let leaked_source = Box::leak(bytes.clone().into_boxed_slice());
        let safe_family = declaration
            .family_key
            .chars()
            .map(|character| {
                if character.is_ascii_alphanumeric() {
                    character
                } else {
                    '-'
                }
            })
            .collect::<String>();
        let style = if declaration.italic {
            "Italic"
        } else {
            "Roman"
        };
        let name = format!(
            "LorekeeperCustom-{safe_family}-{}-{style}",
            declaration.weight
        );
        let leaked_name = Box::leak(name.into_boxed_str());
        configured.push(CustomFont {
            family_key: declaration.family_key.clone(),
            weight: declaration.weight,
            italic: declaration.italic,
            source: leaked_source,
            postscript_name: leaked_name,
            open_type: declaration.media_type == "font/otf",
        });
    }
    *custom_fonts()
        .write()
        .expect("custom font registry poisoned") = configured;
    Ok(())
}

pub fn custom_family(value: &str) -> Option<u16> {
    custom_fonts()
        .read()
        .expect("custom font registry poisoned")
        .iter()
        .position(|font| font.family_key.eq_ignore_ascii_case(value))
        .map(|index| index as u16)
}

pub fn custom_face(index: u16, weight: u16, italic: bool) -> FontFace {
    let fonts = custom_fonts()
        .read()
        .expect("custom font registry poisoned");
    let Some(base) = fonts.get(index as usize) else {
        return FontFace::SerifRegular;
    };
    fonts
        .iter()
        .enumerate()
        .filter(|(_, candidate)| candidate.family_key.eq_ignore_ascii_case(&base.family_key))
        .min_by_key(|(_, candidate)| {
            (
                candidate.italic != italic,
                candidate.weight.abs_diff(weight),
                candidate.weight,
            )
        })
        .map_or(FontFace::Custom(index), |(candidate, _)| {
            FontFace::Custom(candidate as u16)
        })
}

pub fn is_italic(face: FontFace) -> bool {
    match face {
        FontFace::SerifItalic
        | FontFace::SerifBoldItalic
        | FontFace::SansItalic
        | FontFace::SansBoldItalic
        | FontFace::MonoItalic
        | FontFace::MonoBoldItalic => true,
        FontFace::Custom(index) => custom_fonts()
            .read()
            .expect("custom font registry poisoned")
            .get(index as usize)
            .is_some_and(|font| font.italic),
        _ => false,
    }
}

pub fn is_bold(face: FontFace) -> bool {
    match face {
        FontFace::SerifBold
        | FontFace::SerifBoldItalic
        | FontFace::SansBold
        | FontFace::SansBoldItalic
        | FontFace::MonoBold
        | FontFace::MonoBoldItalic => true,
        FontFace::Custom(index) => custom_fonts()
            .read()
            .expect("custom font registry poisoned")
            .get(index as usize)
            .is_some_and(|font| font.weight >= 600),
        _ => false,
    }
}

/// Returns the positive distance from a face's baseline to its descender edge.
/// The result is already scaled to the requested point size.
pub fn descent_points(face: FontFace, size: f32) -> f32 {
    let Ok(font) = OutlineFontRef::new(font_source(face)) else {
        return size * 0.2;
    };
    (-font
        .metrics(Size::new(size), LocationRef::default())
        .descent)
        .max(0.0)
}

#[derive(Debug, Clone, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PositionedGlyph {
    pub cid: u16,
    pub cluster: usize,
    pub x_advance: f32,
    pub y_advance: f32,
    pub x_offset: f32,
    pub y_offset: f32,
}

#[derive(Debug, Clone, Copy)]
pub struct OutlineEdge {
    pub start: [f32; 2],
    pub end: [f32; 2],
}

struct EdgePen {
    edges: Vec<OutlineEdge>,
    first: Option<[f32; 2]>,
    current: Option<[f32; 2]>,
    offset: [f32; 2],
}

impl EdgePen {
    fn new(offset: [f32; 2]) -> Self {
        Self {
            edges: Vec::new(),
            first: None,
            current: None,
            offset,
        }
    }

    fn point(&self, x: f32, y: f32) -> [f32; 2] {
        [x + self.offset[0], y + self.offset[1]]
    }

    fn add_edge(&mut self, end: [f32; 2]) {
        if let Some(start) = self.current {
            self.edges.push(OutlineEdge { start, end });
        }
        self.current = Some(end);
    }
}

impl OutlinePen for EdgePen {
    fn move_to(&mut self, x: f32, y: f32) {
        let point = self.point(x, y);
        self.first = Some(point);
        self.current = Some(point);
    }

    fn line_to(&mut self, x: f32, y: f32) {
        self.add_edge(self.point(x, y));
    }

    fn quad_to(&mut self, cx: f32, cy: f32, x: f32, y: f32) {
        let Some(start) = self.current else {
            return;
        };
        let control = self.point(cx, cy);
        let end = self.point(x, y);
        for step in 1..=8 {
            let t = step as f32 / 8.0;
            let inverse = 1.0 - t;
            self.add_edge([
                inverse * inverse * start[0] + 2.0 * inverse * t * control[0] + t * t * end[0],
                inverse * inverse * start[1] + 2.0 * inverse * t * control[1] + t * t * end[1],
            ]);
        }
    }

    fn curve_to(&mut self, cx0: f32, cy0: f32, cx1: f32, cy1: f32, x: f32, y: f32) {
        let Some(start) = self.current else {
            return;
        };
        let control0 = self.point(cx0, cy0);
        let control1 = self.point(cx1, cy1);
        let end = self.point(x, y);
        for step in 1..=12 {
            let t = step as f32 / 12.0;
            let inverse = 1.0 - t;
            self.add_edge([
                inverse.powi(3) * start[0]
                    + 3.0 * inverse * inverse * t * control0[0]
                    + 3.0 * inverse * t * t * control1[0]
                    + t.powi(3) * end[0],
                inverse.powi(3) * start[1]
                    + 3.0 * inverse * inverse * t * control0[1]
                    + 3.0 * inverse * t * t * control1[1]
                    + t.powi(3) * end[1],
            ]);
        }
    }

    fn close(&mut self) {
        if let Some(first) = self.first {
            self.add_edge(first);
        }
        self.first = None;
        self.current = None;
    }
}

impl EmbeddedFont {
    pub fn encode(&self, text: &str) -> Vec<u8> {
        let font = FontRef::new(self.source).expect("bundled font was validated");
        let data = ShaperData::new(&font);
        let shaper = data.shaper(&font).build();
        let mut buffer = UnicodeBuffer::new();
        buffer.push_str(text);
        buffer.guess_segment_properties();
        let shaped = shaper.shape(buffer, ShapeOptions::default());
        let mut output = Vec::with_capacity(shaped.glyph_infos().len() * 2);
        for info in shaped.glyph_infos() {
            let cid = self
                .glyph_ids
                .get(&(info.glyph_id as u16))
                .copied()
                .unwrap_or(0);
            output.extend_from_slice(&cid.to_be_bytes());
        }
        output
    }

    pub fn advance_width(&self, text: &str, size: f32) -> f32 {
        let font = FontRef::new(self.source).expect("bundled font was validated");
        let data = ShaperData::new(&font);
        let shaper = data.shaper(&font).build();
        let mut buffer = UnicodeBuffer::new();
        buffer.push_str(text);
        buffer.guess_segment_properties();
        let shaped = shaper.shape(buffer, ShapeOptions::default());
        shaped
            .glyph_positions()
            .iter()
            .map(|position| position.x_advance as f32 * size / self.units_per_em)
            .sum()
    }

    pub fn shape(&self, text: &str, size: f32) -> Vec<PositionedGlyph> {
        let font = FontRef::new(self.source).expect("bundled font was validated");
        let data = ShaperData::new(&font);
        let shaper = data.shaper(&font).build();
        let mut buffer = UnicodeBuffer::new();
        buffer.push_str(text);
        buffer.guess_segment_properties();
        let shaped = shaper.shape(buffer, ShapeOptions::default());
        shaped
            .glyph_infos()
            .iter()
            .zip(shaped.glyph_positions())
            .map(|(info, position)| {
                let scale = size / self.units_per_em;
                PositionedGlyph {
                    cid: self
                        .glyph_ids
                        .get(&(info.glyph_id as u16))
                        .copied()
                        .unwrap_or(0),
                    cluster: info.cluster as usize,
                    x_advance: position.x_advance as f32 * scale,
                    y_advance: position.y_advance as f32 * scale,
                    x_offset: position.x_offset as f32 * scale,
                    y_offset: position.y_offset as f32 * scale,
                }
            })
            .collect()
    }

    pub fn outline_edges(
        &self,
        text: &str,
        size: f32,
        origin_x: f32,
        baseline_y: f32,
        character_spacing: f32,
        word_spacing: f32,
    ) -> Result<(Vec<OutlineEdge>, f32), Diagnostic> {
        let shaping_font = FontRef::new(self.source).expect("validated font source");
        let data = ShaperData::new(&shaping_font);
        let shaper = data.shaper(&shaping_font).build();
        let mut buffer = UnicodeBuffer::new();
        buffer.push_str(text);
        buffer.guess_segment_properties();
        let shaped = shaper.shape(buffer, ShapeOptions::default());
        let outline_font = OutlineFontRef::new(self.source).map_err(|_| {
            Diagnostic::error(
                "PRESS_FONT_INVALID",
                "A font outline could not be read for opacity flattening.",
            )
        })?;
        let outlines = outline_font.outline_glyphs();
        let scale = size / self.units_per_em;
        let mut cursor_x = origin_x;
        let mut edges = Vec::new();
        for (index, (info, position)) in shaped
            .glyph_infos()
            .iter()
            .zip(shaped.glyph_positions())
            .enumerate()
        {
            if let Some(glyph) = outlines.get(GlyphId::new(info.glyph_id)) {
                let mut pen = EdgePen::new([
                    cursor_x + position.x_offset as f32 * scale,
                    baseline_y + position.y_offset as f32 * scale,
                ]);
                glyph
                    .draw(
                        DrawSettings::unhinted(Size::new(size), LocationRef::default()),
                        &mut pen,
                    )
                    .map_err(|_| {
                        Diagnostic::error(
                            "PRESS_FONT_INVALID",
                            "A font outline could not be drawn for opacity flattening.",
                        )
                    })?;
                edges.extend(pen.edges);
            }
            cursor_x += position.x_advance as f32 * scale;
            if index + 1 < shaped.glyph_infos().len() {
                cursor_x += character_spacing;
            }
            if text
                .get(info.cluster as usize..)
                .and_then(|remaining| remaining.chars().next())
                .is_some_and(char::is_whitespace)
            {
                cursor_x += word_spacing;
            }
        }
        Ok((edges, cursor_x - origin_x))
    }
}

pub fn subset_for_text(text: &str) -> Result<EmbeddedFont, Diagnostic> {
    subset_for_face(FontFace::SerifRegular, text)
}

pub fn measure_text(face: FontFace, text: &str, size: f32) -> f32 {
    let source = font_source(face);
    let font = FontRef::new(source).expect("bundled font was validated");
    let data = ShaperData::new(&font);
    let shaper = data.shaper(&font).build();
    let units_per_em = shaper.units_per_em() as f32;
    let mut buffer = UnicodeBuffer::new();
    buffer.push_str(text);
    buffer.guess_segment_properties();
    let shaped = shaper.shape(buffer, ShapeOptions::default());
    shaped
        .glyph_positions()
        .iter()
        .map(|position| position.x_advance as f32 * size / units_per_em)
        .sum()
}

pub fn subset_for_layout(
    pages: &[LayoutPage],
) -> Result<BTreeMap<FontFace, EmbeddedFont>, Diagnostic> {
    let mut text_by_face = BTreeMap::<FontFace, String>::new();
    for page in pages {
        for line in &page.lines {
            if line.runs.is_empty() {
                text_by_face
                    .entry(FontFace::SerifRegular)
                    .or_default()
                    .push_str(&line.text);
            } else {
                for run in &line.runs {
                    text_by_face
                        .entry(run.face)
                        .or_default()
                        .push_str(&run.text);
                }
            }
        }
        if let Some(label) = page.page_label.as_deref() {
            text_by_face
                .entry(FontFace::SerifRegular)
                .or_default()
                .push_str(label);
        }
    }
    if text_by_face.is_empty() {
        text_by_face.insert(FontFace::SerifRegular, " ".to_owned());
    }
    text_by_face
        .into_iter()
        .map(|(face, text)| subset_for_face(face, &text).map(|font| (face, font)))
        .collect()
}

fn subset_for_face(face: FontFace, text: &str) -> Result<EmbeddedFont, Diagnostic> {
    let source = font_source(face);
    let font = FontRef::new(source).map_err(|_| {
        Diagnostic::error(
            "PRESS_FONT_INVALID",
            format!(
                "The bundled {} font could not be parsed.",
                postscript_name(face)
            ),
        )
    })?;
    let data = ShaperData::new(&font);
    let shaper = data.shaper(&font).build();
    let units_per_em = shaper.units_per_em() as f32;
    let mut runs = text
        .lines()
        .filter(|line| !line.is_empty())
        .map(str::to_owned)
        .collect::<Vec<_>>();
    runs.push(" ".to_owned());
    let mut old_glyphs = BTreeSet::new();
    let mut old_widths = BTreeMap::new();
    let mut old_unicode = BTreeMap::<u16, Vec<char>>::new();
    for run in runs {
        let mut buffer = UnicodeBuffer::new();
        buffer.push_str(&run);
        buffer.guess_segment_properties();
        let shaped = shaper.shape(buffer, ShapeOptions::default());
        let infos = shaped.glyph_infos();
        let positions = shaped.glyph_positions();
        for (index, info) in infos.iter().enumerate() {
            let cluster = info.cluster as usize;
            let next_cluster = infos[index + 1..]
                .iter()
                .map(|next| next.cluster as usize)
                .find(|next| *next > cluster)
                .unwrap_or(run.len());
            let characters = run
                .get(cluster..next_cluster)
                .unwrap_or("")
                .chars()
                .collect::<Vec<_>>();
            if info.glyph_id == 0 {
                let missing = characters.first().copied().unwrap_or('\0');
                return Err(Diagnostic::error(
                    "PRESS_GLYPH_MISSING",
                    format!("The bundled fonts do not cover U+{:04X}.", missing as u32),
                ));
            }
            let old_gid = info.glyph_id as u16;
            old_glyphs.insert(old_gid);
            old_widths.entry(old_gid).or_insert_with(|| {
                positions
                    .get(index)
                    .map_or(units_per_em as i32 / 2, |position| position.x_advance)
                    as f32
            });
            if !characters.is_empty() {
                old_unicode.entry(old_gid).or_insert(characters);
            }
        }
    }

    let old_glyphs = old_glyphs.into_iter().collect::<Vec<_>>();
    let mapper = GlyphRemapper::new_from_glyphs_sorted(&old_glyphs);
    let subset = subsetter::subset(source, 0, &mapper).map_err(|error| {
        Diagnostic::error(
            "PRESS_FONT_SUBSET_FAILED",
            format!("Font subsetting failed: {error:?}"),
        )
    })?;
    let mut character_ids = BTreeMap::new();
    let mut unicode_sequences = BTreeMap::new();
    let mut widths = BTreeMap::new();
    let mut glyph_ids = BTreeMap::new();
    for old_gid in old_glyphs {
        let cid = mapper.get(old_gid).ok_or_else(|| {
            Diagnostic::error(
                "PRESS_FONT_SUBSET_FAILED",
                "A shaped glyph was omitted from the subset.",
            )
        })?;
        glyph_ids.insert(old_gid, cid);
        let sequence = old_unicode.remove(&old_gid).unwrap_or_default();
        if sequence.len() == 1 {
            character_ids.insert(sequence[0], cid);
        }
        if !sequence.is_empty() {
            unicode_sequences.insert(cid, sequence);
        }
        let advance = nominal_advance(source, old_gid).unwrap_or(units_per_em / 2.0);
        widths.insert(cid, advance * 1000.0 / units_per_em);
    }

    Ok(EmbeddedFont {
        face,
        postscript_name: postscript_name(face),
        open_type: matches!(face, FontFace::Custom(index) if custom_fonts()
            .read()
            .expect("custom font registry poisoned")
            .get(index as usize)
            .is_some_and(|font| font.open_type)),
        bytes: subset,
        character_ids,
        unicode_sequences,
        widths,
        glyph_ids,
        source,
        units_per_em,
    })
}

fn nominal_advance(source: &[u8], glyph_id: u16) -> Option<f32> {
    let table_count = u16::from_be_bytes(source.get(4..6)?.try_into().ok()?) as usize;
    let table = |tag: &[u8; 4]| -> Option<&[u8]> {
        (0..table_count).find_map(|index| {
            let start = 12 + index * 16;
            (source.get(start..start + 4)? == tag).then(|| {
                let offset =
                    u32::from_be_bytes(source[start + 8..start + 12].try_into().ok()?) as usize;
                let length =
                    u32::from_be_bytes(source[start + 12..start + 16].try_into().ok()?) as usize;
                source.get(offset..offset + length)
            })?
        })
    };
    let hhea = table(b"hhea")?;
    let hmtx = table(b"hmtx")?;
    let metric_count = u16::from_be_bytes(hhea.get(34..36)?.try_into().ok()?) as usize;
    if metric_count == 0 {
        return None;
    }
    let metric_index = (glyph_id as usize).min(metric_count - 1);
    Some(u16::from_be_bytes(
        hmtx.get(metric_index * 4..metric_index * 4 + 2)?
            .try_into()
            .ok()?,
    ) as f32)
}

fn font_source(face: FontFace) -> &'static [u8] {
    match face {
        FontFace::SerifRegular => BODY_FONT,
        FontFace::SerifItalic => LORA_ITALIC,
        FontFace::SerifBold => LORA_BOLD,
        FontFace::SerifBoldItalic => LORA_BOLD_ITALIC,
        FontFace::SansRegular => NUNITO_REGULAR,
        FontFace::SansItalic => NUNITO_ITALIC,
        FontFace::SansBold => HEADING_FONT,
        FontFace::SansBoldItalic => NUNITO_BOLD_ITALIC,
        FontFace::MonoRegular => MONO_FONT,
        FontFace::MonoItalic => MONO_ITALIC,
        FontFace::MonoBold => MONO_BOLD,
        FontFace::MonoBoldItalic => MONO_BOLD_ITALIC,
        FontFace::Custom(index) => custom_fonts()
            .read()
            .expect("custom font registry poisoned")
            .get(index as usize)
            .map_or(BODY_FONT, |font| font.source),
    }
}

fn postscript_name(face: FontFace) -> &'static str {
    match face {
        FontFace::SerifRegular => "LKLRAR+Lora-Regular",
        FontFace::SerifItalic => "LKLITA+Lora-Italic",
        FontFace::SerifBold => "LKLBOL+Lora-Bold",
        FontFace::SerifBoldItalic => "LKLZBI+Lora-BoldItalic",
        FontFace::SansRegular => "LKNRAR+Nunito-Regular",
        FontFace::SansItalic => "LKNITA+Nunito-Italic",
        FontFace::SansBold => "LKNBOL+Nunito-Bold",
        FontFace::SansBoldItalic => "LKNZBI+Nunito-BoldItalic",
        FontFace::MonoRegular => "LKMRAR+RobotoMono-Regular",
        FontFace::MonoItalic => "LKMITA+RobotoMono-Italic",
        FontFace::MonoBold => "LKMBOL+RobotoMono-Bold",
        FontFace::MonoBoldItalic => "LKMZBI+RobotoMono-BoldItalic",
        FontFace::Custom(index) => custom_fonts()
            .read()
            .expect("custom font registry poisoned")
            .get(index as usize)
            .map_or("LKLRAR+Lora-Regular", |font| font.postscript_name),
    }
}

pub fn assert_supported_language(language: &str) -> Result<(), Diagnostic> {
    if !matches!(
        language.to_ascii_lowercase().as_str(),
        "en" | "en-us" | "en-gb"
    ) {
        return Err(Diagnostic::error(
            "PRESS_LANGUAGE_UNSUPPORTED",
            format!(
                "Lorekeeper Press currently supports English/Latin left-to-right text; '{language}' is unsupported."
            ),
        ));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn shaping_subsets_and_maps_latin_text() {
        let font = subset_for_text("Lantern—light office").expect("subset");
        assert!(font.bytes.len() < BODY_FONT.len());
        assert_eq!(font.encode("Lantern").len(), 14);
        assert!(
            font.encode("office").len() < 12,
            "the ffi ligature must be shaped as a run"
        );
        assert!(font.character_ids.values().all(|value| *value > 0));
    }

    #[test]
    fn staged_faces_inherit_requested_weight_and_italic_variant() {
        let declarations = [
            FontDeclaration {
                id: "regular".to_owned(),
                family_key: "Project:Fixture".to_owned(),
                weight: 400,
                italic: false,
                relative_path: "regular.ttf".to_owned(),
                media_type: "font/ttf".to_owned(),
                byte_length: BODY_FONT.len() as u64,
                sha256: String::new(),
                embedding_rights_confirmed: true,
            },
            FontDeclaration {
                id: "medium".to_owned(),
                family_key: "project:fixture".to_owned(),
                weight: 500,
                italic: false,
                relative_path: "medium.ttf".to_owned(),
                media_type: "font/ttf".to_owned(),
                byte_length: BODY_FONT.len() as u64,
                sha256: String::new(),
                embedding_rights_confirmed: true,
            },
            FontDeclaration {
                id: "extra-bold".to_owned(),
                family_key: "project:fixture".to_owned(),
                weight: 800,
                italic: false,
                relative_path: "extra-bold.ttf".to_owned(),
                media_type: "font/ttf".to_owned(),
                byte_length: BODY_FONT.len() as u64,
                sha256: String::new(),
                embedding_rights_confirmed: true,
            },
            FontDeclaration {
                id: "bold".to_owned(),
                family_key: "project:fixture".to_owned(),
                weight: 700,
                italic: false,
                relative_path: "bold.ttf".to_owned(),
                media_type: "font/ttf".to_owned(),
                byte_length: BODY_FONT.len() as u64,
                sha256: String::new(),
                embedding_rights_confirmed: true,
            },
            FontDeclaration {
                id: "italic".to_owned(),
                family_key: "PROJECT:FIXTURE".to_owned(),
                weight: 400,
                italic: true,
                relative_path: "italic.ttf".to_owned(),
                media_type: "font/ttf".to_owned(),
                byte_length: BODY_FONT.len() as u64,
                sha256: String::new(),
                embedding_rights_confirmed: true,
            },
        ];
        let sources = BTreeMap::from([
            ("regular".to_owned(), BODY_FONT.to_vec()),
            ("medium".to_owned(), BODY_FONT.to_vec()),
            ("extra-bold".to_owned(), BODY_FONT.to_vec()),
            ("bold".to_owned(), BODY_FONT.to_vec()),
            ("italic".to_owned(), BODY_FONT.to_vec()),
        ]);
        configure_custom_fonts(&declarations, &sources).expect("staged faces");

        let regular = FontFace::Custom(custom_family("project:fixture").expect("family"));
        let medium = regular.with_weight(500, false);
        let extra_bold = regular.with_weight(800, false);
        let bold = regular.with_weight(700, false);
        let italic = regular.with_weight(400, true);
        assert_eq!(medium, FontFace::Custom(1));
        assert_eq!(extra_bold, FontFace::Custom(2));
        assert_eq!(bold, FontFace::Custom(3));
        assert!(!is_italic(bold));
        assert_eq!(italic, FontFace::Custom(4));
        assert!(is_italic(italic));
    }
}
