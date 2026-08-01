use serde::{Deserialize, Serialize};
use serde_json::Value;

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Capabilities {
    pub picture_pages: bool,
    pub illustrated_prose: bool,
    pub publication_placements: bool,
    pub dedicated_full_wrap_cover: bool,
    pub english_hyphenation: bool,
    pub font_shaping: bool,
    pub pdf_x_1a_2001: bool,
}

impl Capabilities {
    pub fn all() -> Self {
        Self {
            picture_pages: true,
            illustrated_prose: true,
            publication_placements: true,
            dedicated_full_wrap_cover: true,
            english_hyphenation: true,
            font_shaping: true,
            pdf_x_1a_2001: true,
        }
    }
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RenderRequest {
    pub protocol_version: u32,
    pub job_id: String,
    pub profile: String,
    pub ink: String,
    pub document: Value,
    pub trim: Trim,
    pub cover: Option<Cover>,
    #[serde(default)]
    pub assets: Vec<AssetDeclaration>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Trim {
    pub width_inches: f32,
    pub height_inches: f32,
    pub margin_inches: f32,
    pub body_font_size_points: f32,
    pub body_line_height: f32,
    #[serde(default)]
    pub mirror_margins: bool,
    #[serde(default)]
    pub recto_chapter_starts: bool,
    #[serde(default = "default_two")]
    pub minimum_widow_lines: usize,
    #[serde(default = "default_two")]
    pub minimum_orphan_lines: usize,
}

fn default_two() -> usize {
    2
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Cover {
    pub bleed_inches: f32,
    pub paper_caliper_inches_per_page: f32,
    #[serde(default)]
    pub back_copy: String,
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub subtitle: String,
    #[serde(default)]
    pub author: String,
    #[serde(default)]
    pub spine_text: String,
    #[serde(default)]
    pub background_color: String,
    pub isbn: Option<String>,
    #[serde(default)]
    pub barcode_mode: String,
    pub asset_id: Option<String>,
    #[serde(default = "default_focal")]
    pub image_focal_x_percent: f32,
    #[serde(default = "default_focal")]
    pub image_focal_y_percent: f32,
}

fn default_focal() -> f32 {
    50.0
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AssetDeclaration {
    pub id: String,
    pub relative_path: String,
    pub media_type: String,
    pub byte_length: u64,
    pub sha256: String,
    pub width_pixels: Option<u32>,
    pub height_pixels: Option<u32>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RenderResponse {
    pub protocol_version: u32,
    pub renderer_version: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub job_id: Option<String>,
    pub status: String,
    pub artifacts: Vec<Artifact>,
    pub page_map: Vec<PageMapEntry>,
    pub diagnostics: Vec<Diagnostic>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub evidence: Option<ValidationEvidence>,
}

impl RenderResponse {
    pub fn failed(status: &str, diagnostic: Diagnostic) -> Self {
        Self {
            protocol_version: 3,
            renderer_version: env!("CARGO_PKG_VERSION"),
            job_id: None,
            status: status.to_owned(),
            artifacts: Vec::new(),
            page_map: Vec::new(),
            diagnostics: vec![diagnostic],
            evidence: None,
        }
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Artifact {
    pub kind: String,
    pub relative_path: String,
    pub filename: String,
    pub media_type: String,
    pub byte_length: u64,
    pub sha256: String,
    pub page_count: usize,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PageMapEntry {
    pub chapter_id: String,
    pub block_id: String,
    pub page_number: usize,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Diagnostic {
    pub severity: String,
    pub code: String,
    pub message: String,
}

impl Diagnostic {
    pub fn error(code: &str, message: impl Into<String>) -> Self {
        Self {
            severity: "error".to_owned(),
            code: code.to_owned(),
            message: message.into(),
        }
    }

    pub fn warning(code: &str, message: impl Into<String>) -> Self {
        Self {
            severity: "warning".to_owned(),
            code: code.to_owned(),
            message: message.into(),
        }
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ValidationEvidence {
    pub validation_status: String,
    pub claimed_standard: Option<String>,
    pub declared_standard: Option<String>,
    pub pdf_version: String,
    pub toc_converged: bool,
    pub has_encryption: bool,
    pub has_transparency: bool,
    pub has_forbidden_actions: bool,
    pub annotation_count: usize,
    pub fonts_embedded: bool,
    pub to_unicode_maps_present: bool,
    pub output_intent_count: usize,
    pub maximum_total_ink_percent: f32,
    pub rendered_features: Vec<String>,
    pub cover_width_points: f32,
    pub spine_width_points: f32,
    pub interior_width_points: f32,
    pub interior_height_points: f32,
    pub cover_height_points: f32,
    pub interior_page_boxes_consistent: bool,
    pub cover_page_boxes_consistent: bool,
    pub fonts: Vec<FontEvidence>,
    pub color_spaces: Vec<String>,
    pub interior_image_color_space: Option<String>,
    pub cover_image_color_space: Option<String>,
    pub interior_image_count: usize,
    pub cover_image_count: usize,
    pub image_count: usize,
    pub minimum_effective_dpi: Option<f32>,
    pub images: Vec<ImageEvidence>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ImageEvidence {
    pub asset_id: String,
    pub page_number: usize,
    pub effective_dpi: f32,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FontEvidence {
    pub name: String,
    pub embedded: bool,
    pub subset: bool,
    pub to_unicode: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LayoutPage {
    pub kind: PageKind,
    pub lines: Vec<LayoutLine>,
    pub images: Vec<LayoutImage>,
    pub barcode_modules: Option<Vec<bool>>,
    pub page_label: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LayoutImage {
    pub asset_id: String,
    pub x: f32,
    pub y: f32,
    pub width: f32,
    pub height: f32,
    pub focal_x: f32,
    pub focal_y: f32,
    pub source_left_fraction: f32,
    pub source_width_fraction: f32,
    pub rotation_degrees: f32,
    pub contain: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub enum PageKind {
    Body,
    Picture,
    Blank,
    Cover,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LayoutLine {
    pub text: String,
    pub runs: Vec<LayoutRun>,
    pub size: f32,
    pub x: f32,
    pub y: f32,
    pub word_spacing: f32,
    pub rotation_degrees: f32,
    pub light_text: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LayoutRun {
    pub text: String,
    pub face: FontFace,
    pub underline: bool,
    pub strikethrough: bool,
    pub baseline_shift_em: f32,
    pub size_scale: f32,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Serialize)]
pub enum FontFace {
    SerifRegular,
    SerifItalic,
    SerifBold,
    SerifBoldItalic,
    SansRegular,
    SansItalic,
    SansBold,
    SansBoldItalic,
    MonoRegular,
    MonoItalic,
    MonoBold,
    MonoBoldItalic,
}

impl FontFace {
    pub fn with_emphasis(self, bold: bool, italic: bool) -> Self {
        match (self.family(), bold, italic) {
            (FontFamily::Serif, false, false) => Self::SerifRegular,
            (FontFamily::Serif, false, true) => Self::SerifItalic,
            (FontFamily::Serif, true, false) => Self::SerifBold,
            (FontFamily::Serif, true, true) => Self::SerifBoldItalic,
            (FontFamily::Sans, false, false) => Self::SansRegular,
            (FontFamily::Sans, false, true) => Self::SansItalic,
            (FontFamily::Sans, true, false) => Self::SansBold,
            (FontFamily::Sans, true, true) => Self::SansBoldItalic,
            (FontFamily::Mono, false, false) => Self::MonoRegular,
            (FontFamily::Mono, false, true) => Self::MonoItalic,
            (FontFamily::Mono, true, false) => Self::MonoBold,
            (FontFamily::Mono, true, true) => Self::MonoBoldItalic,
        }
    }

    pub fn family(self) -> FontFamily {
        match self {
            Self::SerifRegular | Self::SerifItalic | Self::SerifBold | Self::SerifBoldItalic => {
                FontFamily::Serif
            }
            Self::SansRegular | Self::SansItalic | Self::SansBold | Self::SansBoldItalic => {
                FontFamily::Sans
            }
            Self::MonoRegular | Self::MonoItalic | Self::MonoBold | Self::MonoBoldItalic => {
                FontFamily::Mono
            }
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FontFamily {
    Serif,
    Sans,
    Mono,
}

#[derive(Debug, Clone)]
pub struct LayoutDocument {
    pub pages: Vec<LayoutPage>,
    pub page_map: Vec<PageMapEntry>,
    pub features: Vec<String>,
    pub toc_converged: bool,
}
