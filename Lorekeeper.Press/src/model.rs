use serde::{Deserialize, Serialize, Serializer};
use std::collections::BTreeMap;

use serde_json::Value;

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Capabilities {
    pub designed_pages: bool,
    pub flow_figures: bool,
    pub digital_book_pdf: bool,
    pub tagged_pdf: bool,
    pub mixed_page_geometry: bool,
    pub publication_sections: bool,
    pub dedicated_full_wrap_cover: bool,
    pub english_hyphenation: bool,
    pub font_shaping: bool,
    pub project_fonts: bool,
    pub pdf_x_1a_2001: bool,
}

impl Capabilities {
    pub fn all() -> Self {
        Self {
            designed_pages: true,
            flow_figures: true,
            digital_book_pdf: true,
            tagged_pdf: true,
            mixed_page_geometry: true,
            publication_sections: true,
            dedicated_full_wrap_cover: true,
            english_hyphenation: true,
            font_shaping: true,
            project_fonts: true,
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
    pub physical_product: Option<PhysicalProduct>,
    #[serde(default)]
    pub output_purpose: OutputPurpose,
    #[serde(default)]
    pub layout_trace_mode: Option<String>,
    pub document: Value,
    pub trim: Trim,
    pub cover: Option<Cover>,
    #[serde(default)]
    pub assets: Vec<AssetDeclaration>,
    #[serde(default)]
    pub fonts: Vec<FontDeclaration>,
}

#[derive(Debug, Clone, Copy, Default, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "kebab-case")]
pub enum OutputPurpose {
    #[default]
    Publication,
    ReadingCopy,
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
    pub bleed_inches: f32,
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
    pub image_crop_x_percent: f32,
    #[serde(default = "default_focal")]
    pub image_crop_y_percent: f32,
    pub scene: Option<Value>,
    #[serde(default)]
    pub scenes: BTreeMap<String, Value>,
    #[serde(default)]
    pub surfaces: Vec<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PhysicalProduct {
    pub registry_version: String,
    pub registry_sha256: String,
    pub product_key: String,
    pub vendor: String,
    pub format: String,
    pub binding: String,
    pub interior_process: String,
    pub paper_name: String,
    pub basis_weight_pounds: Option<u32>,
    pub gsm: Option<u32>,
    pub cover_material: String,
    pub finish: String,
    pub cover_mode: String,
    pub minimum_pages: usize,
    pub maximum_pages: usize,
    #[serde(default)]
    pub minimum_submitted_pages: Option<usize>,
    #[serde(default)]
    pub maximum_submitted_pages: Option<usize>,
    pub spine_model: PhysicalSpineModel,
    pub generic_template: Option<GenericPrintTemplate>,
    #[serde(default)]
    pub required_cover_surfaces: Vec<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GenericPrintTemplate {
    pub trim_width_inches: f32,
    pub trim_height_inches: f32,
    pub bleed_inches: f32,
    pub safe_inches: f32,
    pub wrap_inches: f32,
    pub hinge_inches: f32,
    pub gutter_inches: f32,
    pub flap_inches: f32,
    pub barcode_width_inches: f32,
    pub barcode_height_inches: f32,
    pub inches_per_page: Option<f32>,
    pub minimum_pages: usize,
    pub maximum_pages: usize,
    pub pdf_standard: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PhysicalSpineModel {
    pub kind: String,
    pub inches_per_page: Option<f32>,
    #[serde(default)]
    pub anchors: Vec<PhysicalSpineAnchor>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PhysicalSpineAnchor {
    pub pages: usize,
    pub inches: f32,
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

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FontDeclaration {
    pub id: String,
    pub family_key: String,
    pub weight: u16,
    pub italic: bool,
    pub relative_path: String,
    pub media_type: String,
    pub byte_length: u64,
    pub sha256: String,
    pub embedding_rights_confirmed: bool,
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
            protocol_version: 8,
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
    pub severity: Box<str>,
    pub code: Box<str>,
    pub message: Box<str>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub source_kind: Option<Box<str>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub source_id: Option<Box<str>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub page: Option<usize>,
}

impl Diagnostic {
    pub fn error(code: &str, message: impl Into<String>) -> Self {
        Self {
            severity: "error".into(),
            code: code.into(),
            message: message.into().into_boxed_str(),
            source_kind: None,
            source_id: None,
            page: None,
        }
    }

    pub fn warning(code: &str, message: impl Into<String>) -> Self {
        Self {
            severity: "warning".into(),
            code: code.into(),
            message: message.into().into_boxed_str(),
            source_kind: None,
            source_id: None,
            page: None,
        }
    }

    pub fn with_source(mut self, kind: &str, id: impl Into<String>) -> Self {
        self.source_kind = Some(kind.into());
        self.source_id = Some(id.into().into_boxed_str());
        self
    }

    pub fn with_page(mut self, page: usize) -> Self {
        self.page = Some(page);
        self
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
    pub cover_surfaces: Vec<CoverSurfaceEvidence>,
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
pub struct CoverSurfaceEvidence {
    pub role: String,
    pub width_points: f32,
    pub height_points: f32,
    pub page_count: usize,
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
    pub width_points: Option<f32>,
    pub height_points: Option<f32>,
    pub lines: Vec<LayoutLine>,
    pub images: Vec<LayoutImage>,
    pub shapes: Vec<LayoutShape>,
    pub paint_order: Vec<LayoutPaint>,
    pub barcode_modules: Option<Vec<bool>>,
    pub page_label: Option<String>,
    pub bookmark: Option<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub enum LayoutPaint {
    Shape(usize),
    Image(usize),
    Line(usize),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub enum LayoutShapeKind {
    Rectangle,
    Ellipse,
    Line,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LayoutShape {
    pub kind: LayoutShapeKind,
    pub x: f32,
    pub y: f32,
    pub width: f32,
    pub height: f32,
    pub fill_rgb: Option<[f32; 3]>,
    pub stroke_rgb: Option<[f32; 3]>,
    pub stroke_width: f32,
    pub opacity: f32,
    pub rotation_degrees: f32,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LayoutImage {
    pub asset_id: String,
    pub x: f32,
    pub y: f32,
    pub width: f32,
    pub height: f32,
    #[serde(rename = "cropX")]
    pub focal_x: f32,
    #[serde(rename = "cropY")]
    pub focal_y: f32,
    pub source_left_fraction: f32,
    pub source_width_fraction: f32,
    pub rotation_degrees: f32,
    pub opacity: f32,
    pub fit: LayoutImageFit,
    pub alt_text: Option<String>,
    pub decorative: bool,
    pub language: Option<String>,
    pub reading_order: Option<i32>,
    pub semantic_id: Option<String>,
    pub semantic_parent_id: Option<String>,
    pub text_wrap: Option<String>,
    pub accessibility_role: Option<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub enum PageKind {
    Body,
    Designed,
    Blank,
    Cover,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub enum LayoutImageFit {
    Contain,
    Cover,
    Stretch,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LayoutLine {
    pub text: String,
    pub runs: Vec<LayoutRun>,
    pub size: f32,
    pub x: f32,
    pub y: f32,
    pub baseline_offset_points: f32,
    pub word_spacing: f32,
    pub character_spacing: f32,
    pub rotation_degrees: f32,
    pub rotation_origin_x: Option<f32>,
    pub rotation_origin_y: Option<f32>,
    pub opacity: f32,
    pub light_text: bool,
    pub fill_rgb: Option<[f32; 3]>,
    pub semantic_role: LayoutSemanticRole,
    pub artifact: bool,
    pub language: Option<String>,
    pub reading_order: Option<i32>,
    pub semantic_id: Option<String>,
    pub semantic_parent_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub source_start_utf16: Option<usize>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub source_end_utf16: Option<usize>,
    pub link_page: Option<usize>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub enum LayoutSemanticRole {
    Paragraph,
    Heading1,
    Heading2,
    Heading3,
    ListItem,
    Caption,
    Credit,
    Toc,
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
    #[serde(skip_serializing_if = "Option::is_none")]
    pub language: Option<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
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
    Custom(u16),
}

impl Serialize for FontFace {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        let name = match self {
            Self::SerifRegular => "SerifRegular".to_owned(),
            Self::SerifItalic => "SerifItalic".to_owned(),
            Self::SerifBold => "SerifBold".to_owned(),
            Self::SerifBoldItalic => "SerifBoldItalic".to_owned(),
            Self::SansRegular => "SansRegular".to_owned(),
            Self::SansItalic => "SansItalic".to_owned(),
            Self::SansBold => "SansBold".to_owned(),
            Self::SansBoldItalic => "SansBoldItalic".to_owned(),
            Self::MonoRegular => "MonoRegular".to_owned(),
            Self::MonoItalic => "MonoItalic".to_owned(),
            Self::MonoBold => "MonoBold".to_owned(),
            Self::MonoBoldItalic => "MonoBoldItalic".to_owned(),
            Self::Custom(index) => format!("Custom{index}"),
        };
        serializer.serialize_str(&name)
    }
}

impl FontFace {
    pub fn with_weight(self, weight: u16, italic: bool) -> Self {
        match self.family() {
            FontFamily::Custom(index) => crate::font::custom_face(index, weight, italic),
            _ => self.with_emphasis(weight >= 600, italic),
        }
    }

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
            (FontFamily::Custom(index), bold, italic) => {
                crate::font::custom_face(index, if bold { 700 } else { 400 }, italic)
            }
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
            Self::Custom(index) => FontFamily::Custom(index),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FontFamily {
    Serif,
    Sans,
    Mono,
    Custom(u16),
}

#[derive(Debug, Clone)]
pub struct LayoutDocument {
    pub pages: Vec<LayoutPage>,
    pub page_map: Vec<PageMapEntry>,
    pub features: Vec<String>,
    pub diagnostics: Vec<Diagnostic>,
    pub toc_converged: bool,
}
