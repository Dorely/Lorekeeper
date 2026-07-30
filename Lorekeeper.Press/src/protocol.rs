use serde::{Deserialize, Serialize};

pub const PROTOCOL_VERSION: u32 = 1;
pub const RENDERER_VERSION: &str = env!("CARGO_PKG_VERSION");

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct RenderRequest {
    pub protocol_version: u32,
    pub job_id: String,
    pub profile: PressProfile,
    pub document: BookDocument,
    pub trim: TrimSize,
    pub cover: CoverSettings,
}

#[derive(Clone, Copy, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub enum PressProfile {
    #[serde(rename = "kdp-paperback-6x9-spike-v1")]
    KdpPaperback6x9SpikeV1,
    #[serde(rename = "ingram-pdf-x1a-experimental-v1")]
    IngramPdfX1aExperimentalV1,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct BookDocument {
    pub title: String,
    pub author: String,
    pub chapters: Vec<BookChapter>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct BookChapter {
    pub title: String,
    pub body: String,
}

#[derive(Clone, Copy, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct TrimSize {
    pub width_inches: f64,
    pub height_inches: f64,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CoverSettings {
    pub bleed_inches: f64,
    pub paper_caliper_inches_per_page: f64,
    pub back_copy: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RenderResponse {
    pub protocol_version: u32,
    pub renderer_version: String,
    pub job_id: String,
    pub status: RenderStatus,
    pub artifacts: Vec<Artifact>,
    pub diagnostics: Vec<Diagnostic>,
    pub evidence: RenderEvidence,
}

#[derive(Clone, Copy, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "kebab-case")]
pub enum RenderStatus {
    Completed,
    Rejected,
    Failed,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Artifact {
    pub kind: ArtifactKind,
    pub relative_path: String,
    pub media_type: String,
    pub sha256: String,
    pub byte_length: u64,
    pub page_count: usize,
}

#[derive(Clone, Copy, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "kebab-case")]
pub enum ArtifactKind {
    InteriorPdf,
    CoverPdf,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Diagnostic {
    pub severity: DiagnosticSeverity,
    pub code: String,
    pub message: String,
    pub artifact_kind: Option<ArtifactKind>,
    pub page: Option<usize>,
}

#[derive(Clone, Copy, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum DiagnosticSeverity {
    Info,
    Warning,
    Error,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RenderEvidence {
    pub pdf_version: Option<String>,
    pub interior_width_points: Option<f64>,
    pub interior_height_points: Option<f64>,
    pub cover_width_points: Option<f64>,
    pub cover_height_points: Option<f64>,
    pub spine_width_points: Option<f64>,
    pub interior_page_boxes: PageBoxEvidence,
    pub cover_page_boxes: PageBoxEvidence,
    pub interior_page_boxes_consistent: bool,
    pub cover_page_boxes_consistent: bool,
    pub fonts: Vec<FontEvidence>,
    pub color_spaces: Vec<String>,
    pub image_count: usize,
    pub annotation_count: usize,
    pub output_intent_count: usize,
    pub has_transparency: bool,
    pub has_encryption: bool,
    pub has_forbidden_actions: bool,
    pub moxcms_srgb_round_trip_verified: bool,
    pub claimed_standard: Option<String>,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct PageBoxEvidence {
    pub media_box: Option<[f64; 4]>,
    pub crop_box: Option<[f64; 4]>,
    pub bleed_box: Option<[f64; 4]>,
    pub trim_box: Option<[f64; 4]>,
    pub art_box: Option<[f64; 4]>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FontEvidence {
    pub name: String,
    pub embedded: bool,
}

impl Diagnostic {
    pub fn error(code: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            severity: DiagnosticSeverity::Error,
            code: code.into(),
            message: message.into(),
            artifact_kind: None,
            page: None,
        }
    }

    pub fn warning(code: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            severity: DiagnosticSeverity::Warning,
            code: code.into(),
            message: message.into(),
            artifact_kind: None,
            page: None,
        }
    }

    pub fn info(code: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            severity: DiagnosticSeverity::Info,
            code: code.into(),
            message: message.into(),
            artifact_kind: None,
            page: None,
        }
    }
}

impl RenderResponse {
    pub fn process_failure(code: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            protocol_version: PROTOCOL_VERSION,
            renderer_version: RENDERER_VERSION.to_owned(),
            job_id: String::new(),
            status: RenderStatus::Failed,
            artifacts: Vec::new(),
            diagnostics: vec![Diagnostic::error(code, message)],
            evidence: RenderEvidence::default(),
        }
    }
}
