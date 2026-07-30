use std::fs;
use std::path::Path;

use lopdf::content::{Content, Operation};
use lopdf::{Document, Object, Stream, dictionary};
use tempfile::TempDir;

use crate::inspect::inspect_pdf;
use crate::protocol::{
    ArtifactKind, DiagnosticSeverity, PressProfile, RenderRequest, RenderStatus,
};
use crate::render::{compile_pdf, render};

#[test]
fn representative_fixture_renders_parseable_pdf_with_extractable_text() {
    let output = TempDir::new().expect("temporary output directory");
    let response = render(representative_request(), output.path());

    assert_eq!(response.status, RenderStatus::Completed);
    assert_eq!(response.artifacts.len(), 2);
    assert!(response.evidence.moxcms_srgb_round_trip_verified);
    assert_eq!(response.evidence.pdf_version.as_deref(), Some("1.7"));
    assert!(response.evidence.fonts.iter().all(|font| font.embedded));
    assert!(!response.evidence.has_encryption);
    assert!(!response.evidence.has_forbidden_actions);
    assert_eq!(response.evidence.annotation_count, 0);
    assert_eq!(
        response.evidence.interior_page_boxes.media_box,
        Some([0.0, 0.0, 432.0, 648.0])
    );
    assert!(response.evidence.interior_page_boxes.trim_box.is_none());
    assert!(response.evidence.cover_page_boxes.bleed_box.is_none());
    assert!(response.evidence.claimed_standard.is_none());

    let interior = artifact_path(output.path(), &response, ArtifactKind::InteriorPdf);
    let bytes = fs::read(&interior).expect("rendered interior");
    let inspection = inspect_pdf(&bytes).expect("independent structural parse");
    assert_eq!(inspection.page_count, 3);
    assert_eq!(inspection.page_width_points, 432.0);
    assert_eq!(inspection.page_height_points, 648.0);

    let document = Document::load(&interior).expect("independent lopdf parse");
    let pages = document.get_pages().keys().copied().collect::<Vec<_>>();
    let text = document
        .extract_text(&pages)
        .expect("independent text extraction");
    assert!(text.contains("Cartographer"));
    assert!(text.contains("Mara"));
}

#[test]
fn same_request_produces_identical_artifact_hashes() {
    let first_output = TempDir::new().expect("first output directory");
    let second_output = TempDir::new().expect("second output directory");
    let request = representative_request();

    let first = render(request.clone(), first_output.path());
    let second = render(request, second_output.path());

    assert_eq!(first.status, RenderStatus::Completed);
    assert_eq!(second.status, RenderStatus::Completed);
    assert_eq!(
        first
            .artifacts
            .iter()
            .map(|artifact| (&artifact.kind, &artifact.sha256))
            .collect::<Vec<_>>(),
        second
            .artifacts
            .iter()
            .map(|artifact| (&artifact.kind, &artifact.sha256))
            .collect::<Vec<_>>()
    );
}

#[test]
fn cover_width_is_derived_from_rendered_page_count() {
    let short_output = TempDir::new().expect("short output directory");
    let long_output = TempDir::new().expect("long output directory");
    let short_request = representative_request();
    let mut long_request = short_request.clone();
    long_request.job_id = "long-novel".to_owned();
    long_request.document.chapters[0].body =
        "A measured sentence fills the representative page. ".repeat(700);

    let short = render(short_request, short_output.path());
    let long = render(long_request, long_output.path());

    assert_eq!(short.status, RenderStatus::Completed);
    assert_eq!(long.status, RenderStatus::Completed);
    let short_pages = artifact(&short, ArtifactKind::InteriorPdf).page_count;
    let long_pages = artifact(&long, ArtifactKind::InteriorPdf).page_count;
    assert!(long_pages > short_pages);
    assert!(
        long.evidence.cover_width_points.expect("long cover width")
            > short
                .evidence
                .cover_width_points
                .expect("short cover width")
    );
    assert_eq!(
        long.evidence.spine_width_points.expect("long spine"),
        long_pages as f64 * 0.0025 * 72.0
    );
}

#[test]
fn pdfx_request_fails_closed_without_artifacts_or_claim() {
    let output = TempDir::new().expect("temporary output directory");
    let request = fixture("invalid-pdfx-request.json");
    let response = render(request, output.path());

    assert_eq!(response.status, RenderStatus::Rejected);
    assert!(response.artifacts.is_empty());
    assert!(response.evidence.claimed_standard.is_none());
    assert!(
        response
            .diagnostics
            .iter()
            .any(|diagnostic| diagnostic.code == "PRESS_PDFX_UNAVAILABLE")
    );
    assert!(
        response
            .diagnostics
            .iter()
            .any(|diagnostic| diagnostic.code == "PRESS_CMYK_PROFILE_REQUIRED")
    );
    assert!(!output.path().join("invalid-pdfx-claim").exists());
}

#[test]
fn invalid_envelope_values_are_rejected_before_file_access() {
    let output = TempDir::new().expect("temporary output directory");
    let mut request = representative_request();
    request.protocol_version = 999;
    request.job_id = "../outside".to_owned();
    request.trim.width_inches = 8.5;

    let response = render(request, output.path());

    assert_eq!(response.status, RenderStatus::Rejected);
    assert!(response.artifacts.is_empty());
    assert_eq!(
        response
            .diagnostics
            .iter()
            .filter(|diagnostic| diagnostic.severity == DiagnosticSeverity::Error)
            .count(),
        3
    );
    assert!(!output.path().join("outside").exists());
}

#[test]
fn profile_names_round_trip_without_aliases() {
    let request = representative_request();
    let json = serde_json::to_string(&request).expect("serialize request");
    assert!(json.contains("\"profile\":\"kdp-paperback-6x9-spike-v1\""));
    let parsed: RenderRequest = serde_json::from_str(&json).expect("deserialize request");
    assert_eq!(parsed.profile, PressProfile::KdpPaperback6x9SpikeV1);
}

#[test]
fn independent_inspector_detects_nested_and_later_page_violations() {
    let bytes = adversarial_pdf();
    let inspection = inspect_pdf(&bytes).expect("adversarial PDF parses");

    assert_eq!(inspection.page_box_mismatch_pages, vec![2]);
    assert!(
        inspection
            .fonts
            .iter()
            .any(|font| font.name == "Helvetica" && !font.embedded)
    );
    assert!(inspection.color_spaces.contains(&"DeviceRGB".to_owned()));
    assert!(inspection.has_transparency);
    assert_eq!(inspection.annotation_count, 1);
    assert!(inspection.has_forbidden_actions);
}

#[test]
fn typst_warnings_fail_the_render_with_structured_diagnostics() {
    let diagnostics =
        compile_pdf("#set text(font: \"A Font That Does Not Exist\")\nWarning fixture.")
            .expect_err("missing font warning must fail");
    assert!(
        diagnostics
            .iter()
            .any(|diagnostic| diagnostic.code == "PRESS_TYPEST_WARNING")
    );
}

#[test]
fn existing_job_directory_is_rejected_without_overwriting_artifacts() {
    let output = TempDir::new().expect("temporary output directory");
    let request = representative_request();
    let first = render(request.clone(), output.path());
    let interior_path = artifact_path(output.path(), &first, ArtifactKind::InteriorPdf);
    let original = fs::read(&interior_path).expect("first interior");

    let second = render(request, output.path());

    assert_eq!(second.status, RenderStatus::Failed);
    assert!(second.artifacts.is_empty());
    assert!(
        second
            .diagnostics
            .iter()
            .any(|diagnostic| diagnostic.code == "PRESS_OUTPUT_UNSAFE")
    );
    assert_eq!(
        fs::read(interior_path).expect("preserved interior"),
        original
    );
}

#[test]
fn output_symlink_is_rejected_when_the_platform_allows_creating_one() {
    let output = TempDir::new().expect("temporary output directory");
    let outside = TempDir::new().expect("outside directory");
    let link = output.path().join("representative-novel");
    if create_directory_symlink(outside.path(), &link).is_err() {
        return;
    }

    let response = render(representative_request(), output.path());

    assert_eq!(response.status, RenderStatus::Failed);
    assert!(
        response
            .diagnostics
            .iter()
            .any(|diagnostic| diagnostic.code == "PRESS_OUTPUT_UNSAFE")
    );
    assert!(!outside.path().join("interior.pdf").exists());
    assert!(!outside.path().join("cover.pdf").exists());
}

fn representative_request() -> RenderRequest {
    fixture("representative.json")
}

fn fixture(name: &str) -> RenderRequest {
    let path = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("fixtures")
        .join(name);
    let json = fs::read_to_string(path).expect("fixture JSON");
    serde_json::from_str(&json).expect("valid fixture")
}

fn artifact(
    response: &crate::protocol::RenderResponse,
    kind: ArtifactKind,
) -> &crate::protocol::Artifact {
    response
        .artifacts
        .iter()
        .find(|artifact| artifact.kind == kind)
        .expect("requested artifact")
}

fn artifact_path(
    output_root: &Path,
    response: &crate::protocol::RenderResponse,
    kind: ArtifactKind,
) -> std::path::PathBuf {
    output_root.join(&artifact(response, kind).relative_path)
}

fn adversarial_pdf() -> Vec<u8> {
    let mut document = Document::with_version("1.7");
    let pages_id = document.new_object_id();

    let font_id = document.add_object(dictionary! {
        "Type" => "Font",
        "Subtype" => "Type1",
        "BaseFont" => "Helvetica",
    });
    let embedded_font_bytes = document.add_object(Stream::new(dictionary! {}, vec![0_u8, 1, 2, 3]));
    let embedded_font_descriptor = document.add_object(dictionary! {
        "Type" => "FontDescriptor",
        "FontName" => "Helvetica",
        "FontFile2" => embedded_font_bytes,
    });
    let embedded_font_id = document.add_object(dictionary! {
        "Type" => "Font",
        "Subtype" => "Type1",
        "BaseFont" => "Helvetica",
        "FontDescriptor" => embedded_font_descriptor,
    });
    let graphics_state_id = document.add_object(dictionary! {
        "Type" => "ExtGState",
        "ca" => 0.5,
        "BM" => "Multiply",
    });
    let form_content = Content {
        operations: vec![
            Operation::new("rg", vec![1.into(), 0.into(), 0.into()]),
            Operation::new("gs", vec!["GS1".into()]),
        ],
    }
    .encode()
    .expect("form content");
    let form_id = document.add_object(Stream::new(
        dictionary! {
            "Type" => "XObject",
            "Subtype" => "Form",
            "BBox" => vec![0.into(), 0.into(), 100.into(), 100.into()],
            "Group" => dictionary! {
                "S" => "Transparency",
            },
            "Resources" => dictionary! {
                "Font" => dictionary! {
                    "F1" => font_id,
                    "F2" => embedded_font_id,
                },
                "ExtGState" => dictionary! {
                    "GS1" => graphics_state_id,
                },
            },
        },
        form_content,
    ));
    let resources_id = document.add_object(dictionary! {
        "XObject" => dictionary! {
            "Fm1" => form_id,
        },
    });
    let page_content = Content {
        operations: vec![Operation::new("Do", vec!["Fm1".into()])],
    }
    .encode()
    .expect("page content");
    let page_content_id = document.add_object(Stream::new(dictionary! {}, page_content));
    let annotation_id = document.add_object(dictionary! {
        "Type" => "Annot",
        "Subtype" => "Link",
        "Rect" => vec![0.into(), 0.into(), 10.into(), 10.into()],
        "A" => dictionary! {
            "S" => "JavaScript",
            "JS" => Object::string_literal("app.alert('no')"),
        },
    });
    let first_page_id = document.add_object(dictionary! {
        "Type" => "Page",
        "Parent" => pages_id,
        "Resources" => resources_id,
        "Contents" => page_content_id,
        "MediaBox" => vec![0.into(), 0.into(), 432.into(), 648.into()],
        "Annots" => vec![annotation_id.into()],
    });
    let second_page_id = document.add_object(dictionary! {
        "Type" => "Page",
        "Parent" => pages_id,
        "Resources" => resources_id,
        "Contents" => page_content_id,
        "MediaBox" => vec![0.into(), 0.into(), 500.into(), 648.into()],
    });
    document.objects.insert(
        pages_id,
        Object::Dictionary(dictionary! {
            "Type" => "Pages",
            "Kids" => vec![first_page_id.into(), second_page_id.into()],
            "Count" => 2,
        }),
    );
    let catalog_id = document.add_object(dictionary! {
        "Type" => "Catalog",
        "Pages" => pages_id,
    });
    document.trailer.set("Root", catalog_id);
    let mut bytes = Vec::new();
    document.save_to(&mut bytes).expect("serialize PDF");
    bytes
}

#[cfg(unix)]
fn create_directory_symlink(target: &Path, link: &Path) -> std::io::Result<()> {
    std::os::unix::fs::symlink(target, link)
}

#[cfg(windows)]
fn create_directory_symlink(target: &Path, link: &Path) -> std::io::Result<()> {
    std::os::windows::fs::symlink_dir(target, link)
}
