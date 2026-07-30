use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};

use moxcms::{ColorProfile, Layout};
use sha2::{Digest, Sha256};
use typst::diag::Warned;
use typst::foundations::Smart;
use typst_layout::PagedDocument;
use typst_pdf::{PdfOptions, PdfStandards};

use crate::inspect::{PdfInspection, inspect_pdf};
use crate::protocol::{
    Artifact, ArtifactKind, Diagnostic, DiagnosticSeverity, PROTOCOL_VERSION, PressProfile,
    RENDERER_VERSION, RenderEvidence, RenderRequest, RenderResponse, RenderStatus,
};
use crate::world::InMemoryWorld;

const POINTS_PER_INCH: f64 = 72.0;
const TRIM_WIDTH_INCHES: f64 = 6.0;
const TRIM_HEIGHT_INCHES: f64 = 9.0;

pub fn render(request: RenderRequest, output_root: &Path) -> RenderResponse {
    let mut response = empty_response(&request);
    let validation = validate_request(&request);
    if !validation.is_empty() {
        response.status = RenderStatus::Rejected;
        response.diagnostics = validation;
        return response;
    }

    if request.profile == PressProfile::IngramPdfX1aExperimentalV1 {
        response.status = RenderStatus::Rejected;
        response.diagnostics.push(Diagnostic::error(
            "PRESS_PDFX_UNAVAILABLE",
            "Typst 0.15.1 and its krilla PDF backend expose PDF 1.4–2.0, PDF/A, and PDF/UA profiles, but no PDF/X-1a:2001 conformance mode.",
        ));
        response.diagnostics.push(Diagnostic::error(
            "PRESS_CMYK_PROFILE_REQUIRED",
            "moxcms can execute ICC transforms but Lorekeeper has no reviewed, redistributable CMYK press profile to embed as an output intent.",
        ));
        response.diagnostics.push(Diagnostic::info(
            "PRESS_NO_STANDARD_CLAIM",
            "No PDF artifact was emitted and the response makes no PDF/X or Ingram compatibility claim.",
        ));
        return response;
    }

    match render_kdp_spike(&request, output_root) {
        Ok(result) => result,
        Err(diagnostics) => {
            response.status = RenderStatus::Failed;
            response.diagnostics = diagnostics;
            response
        }
    }
}

fn render_kdp_spike(
    request: &RenderRequest,
    output_root: &Path,
) -> Result<RenderResponse, Vec<Diagnostic>> {
    let interior_source = interior_source(request);
    let (interior_pdf, interior_page_count) = compile_pdf(&interior_source)?;
    let spine_width_inches =
        interior_page_count as f64 * request.cover.paper_caliper_inches_per_page;
    let cover_source = cover_source(request, spine_width_inches);
    let (cover_pdf, cover_page_count) = compile_pdf(&cover_source)?;

    let moxcms_verified = verify_moxcms_srgb_round_trip()
        .map_err(|message| single_error("PRESS_COLOR_ENGINE_FAILED", message))?;
    let interior_inspection = inspect_pdf(&interior_pdf)
        .map_err(|message| single_error("PRESS_PDF_INSPECTION_FAILED", message))?;
    let cover_inspection = inspect_pdf(&cover_pdf)
        .map_err(|message| single_error("PRESS_PDF_INSPECTION_FAILED", message))?;

    let (canonical_output_root, job_directory) = create_job_directory(output_root, &request.job_id)
        .map_err(|message| single_error("PRESS_OUTPUT_UNSAFE", message))?;
    let interior_path = job_directory.join("interior.pdf");
    let cover_path = job_directory.join("cover.pdf");
    write_new(&interior_path, &interior_pdf)
        .map_err(|message| single_error("PRESS_OUTPUT_FAILED", message))?;
    write_new(&cover_path, &cover_pdf)
        .map_err(|message| single_error("PRESS_OUTPUT_FAILED", message))?;

    let mut response = empty_response(request);
    response.status = RenderStatus::Completed;
    response.artifacts = vec![
        artifact(
            ArtifactKind::InteriorPdf,
            &interior_path,
            &canonical_output_root,
            &interior_pdf,
            interior_page_count,
        )
        .map_err(|message| single_error("PRESS_OUTPUT_FAILED", message))?,
        artifact(
            ArtifactKind::CoverPdf,
            &cover_path,
            &canonical_output_root,
            &cover_pdf,
            cover_page_count,
        )
        .map_err(|message| single_error("PRESS_OUTPUT_FAILED", message))?,
    ];
    response.evidence = combine_evidence(
        &interior_inspection,
        &cover_inspection,
        spine_width_inches,
        moxcms_verified,
    );
    response.diagnostics = preflight(
        request,
        &interior_inspection,
        &cover_inspection,
        spine_width_inches,
    );
    if response
        .diagnostics
        .iter()
        .any(|diagnostic| diagnostic.severity == DiagnosticSeverity::Error)
    {
        response.status = RenderStatus::Failed;
    }

    Ok(response)
}

fn validate_request(request: &RenderRequest) -> Vec<Diagnostic> {
    let mut diagnostics = Vec::new();

    if request.protocol_version != PROTOCOL_VERSION {
        diagnostics.push(Diagnostic::error(
            "PRESS_PROTOCOL_UNSUPPORTED",
            format!(
                "Protocol version {} is unsupported; expected {}.",
                request.protocol_version, PROTOCOL_VERSION
            ),
        ));
    }

    if request.job_id.is_empty()
        || request.job_id.len() > 80
        || !request
            .job_id
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_'))
    {
        diagnostics.push(Diagnostic::error(
            "PRESS_JOB_ID_INVALID",
            "Job IDs must contain 1–80 ASCII letters, digits, hyphens, or underscores.",
        ));
    }

    if !approximately(request.trim.width_inches, TRIM_WIDTH_INCHES)
        || !approximately(request.trim.height_inches, TRIM_HEIGHT_INCHES)
    {
        diagnostics.push(Diagnostic::error(
            "PRESS_TRIM_UNSUPPORTED",
            "The spike supports only a 6 × 9 inch trim.",
        ));
    }

    if request.document.title.trim().is_empty()
        || request.document.author.trim().is_empty()
        || request.document.chapters.is_empty()
    {
        diagnostics.push(Diagnostic::error(
            "PRESS_DOCUMENT_INCOMPLETE",
            "A title, author, and at least one chapter are required.",
        ));
    }

    let document_bytes = request.document.title.len()
        + request.document.author.len()
        + request.cover.back_copy.len()
        + request
            .document
            .chapters
            .iter()
            .map(|chapter| chapter.title.len() + chapter.body.len())
            .sum::<usize>();
    if request.document.chapters.len() > 1_000 || document_bytes > 4 * 1024 * 1024 {
        diagnostics.push(Diagnostic::error(
            "PRESS_DOCUMENT_TOO_LARGE",
            "The spike accepts at most 1,000 chapters and 4 MiB of UTF-8 book content.",
        ));
    }

    if !(0.0..=0.25).contains(&request.cover.bleed_inches)
        || !(0.001..=0.01).contains(&request.cover.paper_caliper_inches_per_page)
    {
        diagnostics.push(Diagnostic::error(
            "PRESS_COVER_GEOMETRY_INVALID",
            "Bleed must be 0–0.25 inches and paper caliper must be 0.001–0.01 inches per page.",
        ));
    }

    diagnostics
}

pub(crate) fn compile_pdf(source: &str) -> Result<(Vec<u8>, usize), Vec<Diagnostic>> {
    let world = InMemoryWorld::new(source.to_owned());
    let Warned { output, warnings } = typst::compile::<PagedDocument>(&world);
    if !warnings.is_empty() {
        return Err(warnings
            .iter()
            .map(|warning| {
                Diagnostic::error(
                    "PRESS_TYPEST_WARNING",
                    format!(
                        "Typst warning was treated as a render failure: {}",
                        warning.message
                    ),
                )
            })
            .collect());
    }
    let document = output.map_err(|errors| {
        errors
            .iter()
            .map(|error| Diagnostic::error("PRESS_TYPEST_COMPILE_FAILED", error.message.as_str()))
            .collect::<Vec<_>>()
    })?;
    let page_count = document.pages().len();
    let options = PdfOptions {
        ident: Smart::Custom("lorekeeper-press-spike-v1".to_owned()),
        creator: Smart::Custom(Some(format!("Lorekeeper Press Spike {RENDERER_VERSION}"))),
        timestamp: None,
        page_ranges: None,
        standards: PdfStandards::default(),
        tagged: true,
        pretty: false,
    };
    let pdf = typst_pdf::pdf(&document, &options).map_err(|errors| {
        errors
            .iter()
            .map(|error| Diagnostic::error("PRESS_TYPEST_PDF_FAILED", error.message.as_str()))
            .collect::<Vec<_>>()
    })?;
    Ok((pdf, page_count))
}

fn interior_source(request: &RenderRequest) -> String {
    let mut source = format!(
        r#"#set page(width: {width}in, height: {height}in, margin: (left: 0.75in, right: 0.625in, top: 0.7in, bottom: 0.75in), numbering: "1")
#set text(font: "Libertinus Serif", size: 10.5pt, lang: "en")
#set par(justify: true, leading: 0.58em, first-line-indent: 1.25em)
#align(center + horizon)[
  #text(size: 22pt, weight: "bold")[{title}]
  #v(1.5em)
  #text(size: 12pt)[by {author}]
]
#pagebreak()
"#,
        width = request.trim.width_inches,
        height = request.trim.height_inches,
        title = typst_content(&request.document.title),
        author = typst_content(&request.document.author),
    );

    for (index, chapter) in request.document.chapters.iter().enumerate() {
        if index > 0 {
            source.push_str("#pagebreak(to: \"odd\")\n");
        }
        source.push_str(&format!(
            "#align(center)[#text(size: 16pt, weight: \"bold\")[{}]]\n#v(2em)\n",
            typst_content(&chapter.title)
        ));
        for paragraph in chapter.body.split("\n\n").filter(|part| !part.is_empty()) {
            source.push_str(&format!("#text[{}]\n\n", typst_content(paragraph)));
        }
    }

    source
}

fn cover_source(request: &RenderRequest, spine_width_inches: f64) -> String {
    let bleed = request.cover.bleed_inches;
    let total_width = request.trim.width_inches * 2.0 + spine_width_inches + bleed * 2.0;
    let total_height = request.trim.height_inches + bleed * 2.0;
    let panel_width = request.trim.width_inches;
    format!(
        r##"#set page(width: {total_width}in, height: {total_height}in, margin: 0pt, fill: rgb("#1b2433"))
#set text(font: "Libertinus Serif", fill: white)
#place(left + top, dx: {bleed}in, dy: {bleed}in, rect(width: {panel_width}in, height: {trim_height}in, fill: rgb("#273a52"), inset: 0.65in)[
  #align(center + horizon)[#text(size: 11pt)[{back_copy}]]
])
#place(left + top, dx: {front_x}in, dy: {bleed}in, rect(width: {panel_width}in, height: {trim_height}in, fill: rgb("#8c3f52"), inset: 0.65in)[
  #align(center + horizon)[
    #text(size: 24pt, weight: "bold")[{title}]
    #v(1.4em)
    #text(size: 12pt)[{author}]
  ]
])
"##,
        total_width = total_width,
        total_height = total_height,
        bleed = bleed,
        panel_width = panel_width,
        trim_height = request.trim.height_inches,
        front_x = bleed + panel_width + spine_width_inches,
        back_copy = typst_content(&request.cover.back_copy),
        title = typst_content(&request.document.title),
        author = typst_content(&request.document.author),
    )
}

fn typst_content(value: &str) -> String {
    value
        .replace('\\', "\\\\")
        .replace('#', "\\#")
        .replace('[', "\\[")
        .replace(']', "\\]")
        .replace('*', "\\*")
        .replace('_', "\\_")
        .replace('@', "\\@")
        .replace('$', "\\$")
        .replace('<', "\\<")
        .replace('>', "\\>")
}

fn verify_moxcms_srgb_round_trip() -> Result<bool, String> {
    let source_profile = ColorProfile::new_srgb();
    let destination_profile = ColorProfile::new_srgb();
    let transform = source_profile
        .create_transform_8bit(
            Layout::Rgb,
            &destination_profile,
            Layout::Rgb,
            Default::default(),
        )
        .map_err(|error| error.to_string())?;
    let source = [17_u8, 91, 203, 240, 64, 2];
    let mut destination = [0_u8; 6];
    transform
        .transform(&source, &mut destination)
        .map_err(|error| error.to_string())?;
    Ok(source
        .iter()
        .zip(destination)
        .all(|(source, destination)| source.abs_diff(destination) <= 1))
}

fn preflight(
    request: &RenderRequest,
    interior: &PdfInspection,
    cover: &PdfInspection,
    spine_width_inches: f64,
) -> Vec<Diagnostic> {
    let mut diagnostics = Vec::new();
    let expected_interior_width = request.trim.width_inches * POINTS_PER_INCH;
    let expected_interior_height = request.trim.height_inches * POINTS_PER_INCH;
    let expected_cover_width =
        (request.trim.width_inches * 2.0 + request.cover.bleed_inches * 2.0 + spine_width_inches)
            * POINTS_PER_INCH;
    let expected_cover_height =
        (request.trim.height_inches + request.cover.bleed_inches * 2.0) * POINTS_PER_INCH;

    check_pdf(
        ArtifactKind::InteriorPdf,
        interior,
        expected_interior_width,
        expected_interior_height,
        &mut diagnostics,
    );
    check_pdf(
        ArtifactKind::CoverPdf,
        cover,
        expected_cover_width,
        expected_cover_height,
        &mut diagnostics,
    );

    if cover.page_boxes.trim_box.is_none() || cover.page_boxes.bleed_box.is_none() {
        let mut diagnostic = Diagnostic::warning(
            "PRESS_PAGE_BOXES_MINIMAL",
            "The cover records its full bleed extent in MediaBox but has no explicit TrimBox or BleedBox.",
        );
        diagnostic.artifact_kind = Some(ArtifactKind::CoverPdf);
        diagnostics.push(diagnostic);
    }
    diagnostics.push(Diagnostic::warning(
        "PRESS_VENDOR_VALIDATION_NOT_RUN",
        "This artifact has not passed KDP upload preflight and is evidence for the renderer spike only.",
    ));
    diagnostics.push(Diagnostic::warning(
        "PRESS_PDFX_NOT_CLAIMED",
        "The output is PDF 1.7; it is not PDF/X and no Ingram compatibility claim is made.",
    ));
    diagnostics
}

fn check_pdf(
    kind: ArtifactKind,
    inspection: &PdfInspection,
    expected_width: f64,
    expected_height: f64,
    diagnostics: &mut Vec<Diagnostic>,
) {
    let mut emit = |code: &str, message: String| {
        let mut diagnostic = Diagnostic::error(code, message);
        diagnostic.artifact_kind = Some(kind);
        diagnostics.push(diagnostic);
    };

    if inspection.version != "1.7" {
        emit(
            "PRESS_PDF_VERSION_INVALID",
            format!("Expected PDF 1.7; found PDF {}.", inspection.version),
        );
    }
    if !approximately(inspection.page_width_points, expected_width)
        || !approximately(inspection.page_height_points, expected_height)
    {
        emit(
            "PRESS_PAGE_BOX_INVALID",
            format!(
                "MediaBox is {:.3} × {:.3} pt; expected {:.3} × {:.3} pt.",
                inspection.page_width_points,
                inspection.page_height_points,
                expected_width,
                expected_height
            ),
        );
    }
    if !inspection.page_box_mismatch_pages.is_empty() {
        emit(
            "PRESS_PAGE_BOX_INCONSISTENT",
            format!(
                "Page boxes differ from page 1 on pages {:?}.",
                inspection.page_box_mismatch_pages
            ),
        );
    }
    if inspection.fonts.is_empty() || inspection.fonts.iter().any(|font| !font.embedded) {
        emit(
            "PRESS_FONT_NOT_EMBEDDED",
            "Every used font must be embedded.".to_owned(),
        );
    }
    if inspection.has_encryption {
        emit(
            "PRESS_ENCRYPTION_FORBIDDEN",
            "Print artifacts must not be encrypted.".to_owned(),
        );
    }
    if inspection.has_forbidden_actions {
        emit(
            "PRESS_ACTION_FORBIDDEN",
            "Print artifacts must not contain JavaScript, launch, or automatic actions.".to_owned(),
        );
    }
    if inspection.annotation_count > 0 {
        emit(
            "PRESS_ANNOTATION_FORBIDDEN",
            format!(
                "Print artifacts must not contain annotations; found {}.",
                inspection.annotation_count
            ),
        );
    }
}

fn combine_evidence(
    interior: &PdfInspection,
    cover: &PdfInspection,
    spine_width_inches: f64,
    moxcms_verified: bool,
) -> RenderEvidence {
    let mut fonts = interior.fonts.clone();
    for font in &cover.fonts {
        if !fonts.iter().any(|existing| existing.name == font.name) {
            fonts.push(font.clone());
        }
    }
    let mut color_spaces = interior.color_spaces.clone();
    for color_space in &cover.color_spaces {
        if !color_spaces.contains(color_space) {
            color_spaces.push(color_space.clone());
        }
    }

    RenderEvidence {
        pdf_version: Some(interior.version.clone()),
        interior_width_points: Some(interior.page_width_points),
        interior_height_points: Some(interior.page_height_points),
        cover_width_points: Some(cover.page_width_points),
        cover_height_points: Some(cover.page_height_points),
        spine_width_points: Some(spine_width_inches * POINTS_PER_INCH),
        interior_page_boxes: interior.page_boxes.clone(),
        cover_page_boxes: cover.page_boxes.clone(),
        interior_page_boxes_consistent: interior.page_box_mismatch_pages.is_empty(),
        cover_page_boxes_consistent: cover.page_box_mismatch_pages.is_empty(),
        fonts,
        color_spaces,
        image_count: interior.image_count + cover.image_count,
        annotation_count: interior.annotation_count + cover.annotation_count,
        output_intent_count: interior.output_intent_count + cover.output_intent_count,
        has_transparency: interior.has_transparency || cover.has_transparency,
        has_encryption: interior.has_encryption || cover.has_encryption,
        has_forbidden_actions: interior.has_forbidden_actions || cover.has_forbidden_actions,
        moxcms_srgb_round_trip_verified: moxcms_verified,
        claimed_standard: None,
    }
}

fn artifact(
    kind: ArtifactKind,
    path: &Path,
    output_root: &Path,
    bytes: &[u8],
    page_count: usize,
) -> Result<Artifact, String> {
    let relative_path = path
        .strip_prefix(output_root)
        .map_err(|error| error.to_string())?
        .to_string_lossy()
        .replace('\\', "/");
    Ok(Artifact {
        kind,
        relative_path,
        media_type: "application/pdf".to_owned(),
        sha256: hex_hash(bytes),
        byte_length: bytes.len() as u64,
        page_count,
    })
}

fn empty_response(request: &RenderRequest) -> RenderResponse {
    RenderResponse {
        protocol_version: PROTOCOL_VERSION,
        renderer_version: RENDERER_VERSION.to_owned(),
        job_id: request.job_id.clone(),
        status: RenderStatus::Failed,
        artifacts: Vec::new(),
        diagnostics: Vec::new(),
        evidence: RenderEvidence::default(),
    }
}

fn hex_hash(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}

fn approximately(left: f64, right: f64) -> bool {
    (left - right).abs() <= 0.01
}

fn single_error(code: &str, message: impl Into<String>) -> Vec<Diagnostic> {
    vec![Diagnostic::error(code, message)]
}

fn create_job_directory(output_root: &Path, job_id: &str) -> Result<(PathBuf, PathBuf), String> {
    fs::create_dir_all(output_root).map_err(|error| error.to_string())?;
    let canonical_root = fs::canonicalize(output_root).map_err(|error| error.to_string())?;
    let job_directory = canonical_root.join(job_id);
    fs::create_dir(&job_directory).map_err(|error| {
        format!(
            "A new job directory could not be reserved at '{}': {error}",
            job_directory.display()
        )
    })?;
    let canonical_job = fs::canonicalize(&job_directory).map_err(|error| error.to_string())?;
    if canonical_job.parent() != Some(canonical_root.as_path()) {
        return Err("The resolved job directory is outside the requested output root.".to_owned());
    }
    Ok((canonical_root, canonical_job))
}

fn write_new(path: &Path, bytes: &[u8]) -> Result<(), String> {
    let mut file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(path)
        .map_err(|error| error.to_string())?;
    file.write_all(bytes).map_err(|error| error.to_string())?;
    file.sync_all().map_err(|error| error.to_string())
}
