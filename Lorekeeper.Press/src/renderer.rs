use std::collections::BTreeSet;
use std::fs;
use std::path::{Component, Path};

use hypher::{Lang, hyphenate};
use serde_json::Value;
use sha2::{Digest, Sha256};
use unicode_linebreak::linebreaks;

use crate::font::{
    assert_supported_language, configure_custom_fonts, custom_family, is_italic, measure_text,
    subset_for_layout,
};
use crate::image::{DecodedImage, EmbeddedImage, prepare_images};
use crate::inspect;
use crate::model::{
    Artifact, CoverSurfaceEvidence, Diagnostic, FontEvidence, FontFace, FontFamily, ImageEvidence,
    LayoutDocument, LayoutImage, LayoutImageFit, LayoutLine, LayoutPage, LayoutPaint, LayoutRun,
    LayoutSemanticRole, LayoutShape, LayoutShapeKind, OutputPurpose, PageKind, PageMapEntry,
    RenderRequest, RenderResponse, ValidationEvidence,
};
use crate::pdf::{
    PdfOptions, cover_background_total_ink_percent, write_pdf_cancellable_with_progress,
};
use crate::typography;

const MAX_ASSETS: usize = 512;
const MAX_ASSET_BYTES: u64 = 256 * 1024 * 1024;
const MAX_JOB_BYTES: u64 = 1024 * 1024 * 1024;
const MAX_REQUEST_BYTES: u64 = 64 * 1024 * 1024;
const MAX_PAGES: usize = 10_000;
type RenderResult<T> = Result<T, Box<RenderResponse>>;

fn registry_sha256() -> String {
    Sha256::digest(include_bytes!("../assets/print-artifact-profiles-v1.json"))
        .iter()
        .map(|value| format!("{value:02x}"))
        .collect()
}

fn validate_registry_profile(
    product: &crate::model::PrintArtifactProfile,
    request: &RenderRequest,
) -> Result<(), Diagnostic> {
    let registry: Value =
        serde_json::from_slice(include_bytes!("../assets/print-artifact-profiles-v1.json"))
            .map_err(|_| {
                Diagnostic::error(
                    "PRESS_PRINT_REGISTRY_INVALID",
                    "The bundled print-artifact profile registry is invalid.",
                )
            })?;
    let catalog = registry["profiles"]
        .as_array()
        .and_then(|items| {
            items
                .iter()
                .find(|item| item["key"].as_str() == Some(&product.artifact_profile_key))
        })
        .ok_or_else(|| {
            Diagnostic::error(
                "PRESS_PRINT_ARTIFACT_PROFILE_UNKNOWN",
                "The selected artifact profile is absent from the bundled registry.",
            )
        })?;
    let matches_scalar = registry["registryVersion"].as_str() == Some(&product.registry_version)
        && catalog["vendor"].as_str() == Some(&product.vendor)
        && catalog["format"].as_str() == Some(&product.format)
        && catalog["binding"].as_str() == Some(&product.binding)
        && catalog["interiorProcess"].as_str() == Some(&product.interior_process)
        && catalog["basisWeightPounds"].as_u64() == product.basis_weight_pounds.map(u64::from)
        && catalog["gsm"].as_u64() == product.gsm.map(u64::from)
        && catalog["coverMaterial"].as_str() == Some(&product.cover_material)
        && catalog["minimumPages"].as_u64() == Some(product.minimum_pages as u64)
        && catalog["maximumPages"].as_u64() == Some(product.maximum_pages as u64)
        && catalog["minimumSubmittedPages"].as_u64()
            == product.minimum_submitted_pages.map(|value| value as u64)
        && catalog["maximumSubmittedPages"].as_u64()
            == product.maximum_submitted_pages.map(|value| value as u64)
        && catalog["coverModes"].as_array().is_some_and(|items| {
            items
                .iter()
                .any(|item| item.as_str() == Some(&product.cover_mode))
        })
        && catalog["supportedProjectUses"].as_array().map_or(
            product.project_use == "ForSale",
            |items| {
                items
                    .iter()
                    .any(|item| item.as_str() == Some(&product.project_use))
            },
        )
        && catalog["pdfProfile"].as_str() == Some(&request.profile);
    let trim_matches = catalog["trimSizes"].as_array().is_some_and(|items| {
        items.iter().any(|item| {
            item.as_str().is_some_and(|value| {
                let Some((width, height)) = value.split_once('x') else {
                    return false;
                };
                width.parse::<f32>().is_ok_and(|width| {
                    height.parse::<f32>().is_ok_and(|height| {
                        (width - request.trim.width_inches).abs() < 0.000_1
                            && (height - request.trim.height_inches).abs() < 0.000_1
                    })
                })
            })
        })
    }) || catalog["allowsCustomTrim"].as_bool() == Some(true);
    let catalog_spine = &catalog["spineModel"];
    let anchors_match = match catalog_spine["anchors"].as_array() {
        None => product.spine_model.anchors.is_empty(),
        Some(items) => {
            items.len() == product.spine_model.anchors.len()
                && items
                    .iter()
                    .zip(&product.spine_model.anchors)
                    .all(|(expected, actual)| {
                        expected["pages"].as_u64() == Some(actual.pages as u64)
                            && expected["inches"].as_f64().is_some_and(|value| {
                                (value as f32 - actual.inches).abs() < 0.000_001
                            })
                    })
        }
    };
    let spine_matches = (catalog_spine["kind"].as_str() == Some(&product.spine_model.kind)
        && match (
            catalog_spine["inchesPerPage"].as_f64(),
            product.spine_model.inches_per_page,
        ) {
            (None, None) => true,
            (Some(expected), Some(actual)) => (expected as f32 - actual).abs() < 0.000_001,
            _ => false,
        }
        && anchors_match)
        || (product.vendor == "Generic"
            && catalog_spine["kind"].as_str() == Some("TemplateRequired")
            && product.spine_model.kind == "Caliper"
            && product.spine_model.inches_per_page.is_some()
            && product.print_template_evidence.is_some());
    let expected_surfaces: Vec<&str> = match product.cover_material.as_str() {
        "PrintedCover" if product.cover_mode == "Duplex" => {
            vec!["perfect-bound-outside", "perfect-bound-inside"]
        }
        "PrintedCover" => vec!["perfect-bound-outside"],
        "CaseLaminate" => vec!["case-wrap"],
        "DigitalCloth" => vec!["digital-cloth-setup"],
        "DigitalClothWithJacket" => {
            vec!["dust-jacket", "digital-cloth-setup"]
        }
        "JacketedCaseLaminate" if product.vendor == "BarnesAndNoblePress" => {
            vec!["dust-jacket"]
        }
        "JacketedCaseLaminate" => vec!["case-wrap", "dust-jacket"],
        "Declared" if product.binding == "PerfectBound" => vec!["perfect-bound-outside"],
        "Declared" if product.binding == "CaseBound" => vec!["case-wrap"],
        _ => Vec::new(),
    };
    let surfaces_match = expected_surfaces.len() == product.required_cover_surfaces.len()
        && expected_surfaces.iter().all(|surface| {
            product
                .required_cover_surfaces
                .iter()
                .any(|actual| actual == surface)
        });
    let print_template_evidence_valid =
        !matches!(product.vendor.as_str(), "Generic" | "BarnesAndNoblePress")
            || product
                .print_template_evidence
                .as_ref()
                .is_some_and(|template| {
                    let dimensions_valid = [
                        template.trim_width_inches,
                        template.trim_height_inches,
                        template.bleed_inches,
                        template.safe_inches,
                        template.wrap_inches,
                        template.hinge_inches,
                        template.gutter_inches,
                        template.flap_inches,
                        template.barcode_width_inches,
                        template.barcode_height_inches,
                    ]
                    .iter()
                    .all(|value| value.is_finite() && *value >= 0.0);
                    let common_valid = dimensions_valid
                        && (template.trim_width_inches - request.trim.width_inches).abs() < 0.000_1
                        && (template.trim_height_inches - request.trim.height_inches).abs()
                            < 0.000_1
                        && template.minimum_pages > 0
                        && template.maximum_pages >= template.minimum_pages
                        && !template.pdf_standard.trim().is_empty();
                    common_valid
                        && if product.vendor == "BarnesAndNoblePress" {
                            template.provider == "BarnesAndNoblePress"
                                && template.artifact_profile_key == product.artifact_profile_key
                                && template.page_count > 0
                                && !template.geometry_fingerprint.trim().is_empty()
                                && template
                                    .spine_width_inches
                                    .is_some_and(|value| value.is_finite() && value > 0.0)
                                && template
                                    .full_cover_width_inches
                                    .is_some_and(|value| value.is_finite() && value > 0.0)
                                && template
                                    .full_cover_height_inches
                                    .is_some_and(|value| value.is_finite() && value > 0.0)
                                && template
                                    .front_cover_width_inches
                                    .is_some_and(|value| value.is_finite() && value > 0.0)
                                && template
                                    .front_cover_height_inches
                                    .is_some_and(|value| value.is_finite() && value > 0.0)
                                && template
                                    .back_cover_width_inches
                                    .is_some_and(|value| value.is_finite() && value > 0.0)
                                && template
                                    .back_cover_height_inches
                                    .is_some_and(|value| value.is_finite() && value > 0.0)
                                && !template.full_cover_template_sha256.trim().is_empty()
                                && !template.front_cover_template_sha256.trim().is_empty()
                                && !template.back_cover_template_sha256.trim().is_empty()
                                && (template.full_cover_width_inches.unwrap_or_default()
                                    - template.front_cover_width_inches.unwrap_or_default()
                                    - template.back_cover_width_inches.unwrap_or_default()
                                    - template.spine_width_inches.unwrap_or_default())
                                .abs()
                                    < 0.02
                                && (template.full_cover_height_inches.unwrap_or_default()
                                    - template.front_cover_height_inches.unwrap_or_default())
                                .abs()
                                    < 0.02
                                && (template.full_cover_height_inches.unwrap_or_default()
                                    - template.back_cover_height_inches.unwrap_or_default())
                                .abs()
                                    < 0.02
                        } else {
                            template
                                .inches_per_page
                                .is_some_and(|value| value.is_finite() && value > 0.0)
                        }
                });
    if !matches_scalar
        || !trim_matches
        || !spine_matches
        || !surfaces_match
        || !print_template_evidence_valid
    {
        return Err(Diagnostic::error(
            "PRESS_PRINT_ARTIFACT_PROFILE_MISMATCH",
            format!(
                "The resolved print artifact profile differs from the bundled registry entry (identity={matches_scalar}, trim={trim_matches}, spine={spine_matches}, surfaces={surfaces_match}, printTemplateEvidence={print_template_evidence_valid})."
            ),
        ));
    }
    Ok(())
}

#[derive(Clone, Copy, Default)]
struct LayoutTolerance {
    allow_pending_accessibility: bool,
    clip_composition_text_overflow: bool,
}

#[derive(Clone)]
struct PhysicalCoverSurface {
    role: String,
    width_points: f32,
    height_points: f32,
    spine_points: f32,
    inside_spine_no_ink_points: f32,
}

fn normalized_vendor_pages(request: &RenderRequest, submitted: usize) -> usize {
    if request.print_artifact_profile.is_some() && !submitted.is_multiple_of(2) {
        submitted + 1
    } else {
        submitted
    }
}

fn product_spine_inches(request: &RenderRequest, pages: usize) -> Result<f32, Diagnostic> {
    let product = request.print_artifact_profile.as_ref().ok_or_else(|| {
        Diagnostic::error(
            "PRESS_PRINT_ARTIFACT_PROFILE_REQUIRED",
            "Print artifact settings are required.",
        )
    })?;
    let submitted_pages = pages;
    let minimum_submitted = product
        .minimum_submitted_pages
        .unwrap_or(product.minimum_pages);
    let maximum_submitted = product
        .maximum_submitted_pages
        .unwrap_or(product.maximum_pages);
    if submitted_pages < minimum_submitted || submitted_pages > maximum_submitted {
        return Err(Diagnostic::error(
            "PRESS_PRODUCT_SUBMITTED_PAGE_COUNT",
            format!(
                "{} supports {minimum_submitted}-{maximum_submitted} submitted pages; the interior has {submitted_pages}.",
                product.artifact_profile_key
            ),
        ));
    }
    let pages = normalized_vendor_pages(request, submitted_pages);
    if pages < product.minimum_pages || pages > product.maximum_pages {
        return Err(Diagnostic::error(
            "PRESS_PRODUCT_PAGE_COUNT",
            format!(
                "{} supports {}–{} pages; the normalized interior has {pages}.",
                product.artifact_profile_key, product.minimum_pages, product.maximum_pages
            ),
        ));
    }
    if let Some(template) = product.print_template_evidence.as_ref()
        && (pages < template.minimum_pages || pages > template.maximum_pages)
    {
        return Err(Diagnostic::error(
            "PRESS_TEMPLATE_PAGE_COUNT",
            format!(
                "The imported printer template supports {}-{} pages; the normalized interior has {pages}.",
                template.minimum_pages, template.maximum_pages
            ),
        ));
    }
    if product.spine_model.kind == "TemplateRequired" && product.vendor == "BarnesAndNoblePress" {
        return product
            .print_template_evidence
            .as_ref()
            .filter(|template| template.page_count == pages)
            .and_then(|template| template.spine_width_inches)
            .filter(|value| value.is_finite() && *value > 0.0)
            .ok_or_else(|| {
                Diagnostic::error(
                    "PRESS_SPINE_MEASUREMENT_MISSING",
                    "The B&N Press template evidence does not match this page count or lacks a measured spine.",
                )
            });
    }
    if product.spine_model.kind == "Caliper" {
        return product
            .spine_model
            .inches_per_page
            .map(|caliper| caliper * pages as f32)
            .ok_or_else(|| {
                Diagnostic::error(
                    "PRESS_SPINE_MODEL_INVALID",
                    "The product caliper is missing.",
                )
            });
    }
    if product.spine_model.kind == "FrozenLookup" {
        let mut anchors = product.spine_model.anchors.clone();
        anchors.sort_by_key(|anchor| anchor.pages);
        if let Some(exact) = anchors.iter().find(|anchor| anchor.pages == pages) {
            return Ok(exact.inches);
        }
        return Err(Diagnostic::error(
            "PRESS_SPINE_MEASUREMENT_MISSING",
            format!(
                "{} has no verified spine measurement for {pages} normalized pages.",
                product.artifact_profile_key
            ),
        ));
    }
    Err(Diagnostic::error(
        "PRESS_SPINE_MODEL_INVALID",
        "The product has no usable spine evidence.",
    ))
}

fn physical_cover_surfaces(
    request: &RenderRequest,
    pages: usize,
) -> Result<Vec<PhysicalCoverSurface>, Diagnostic> {
    let product = request.print_artifact_profile.as_ref().ok_or_else(|| {
        Diagnostic::error(
            "PRESS_PRINT_ARTIFACT_PROFILE_REQUIRED",
            "Print artifact settings are required.",
        )
    })?;
    let spine = product_spine_inches(request, pages)?;
    let trim_width = request.trim.width_inches;
    let trim_height = request.trim.height_inches;
    product
        .required_cover_surfaces
        .iter()
        .filter_map(|role| {
            let geometry = match role.as_str() {
                "perfect-bound-outside" | "perfect-bound-inside" if product.vendor == "Generic" => {
                    let template = product.print_template_evidence.as_ref().ok_or_else(|| {
                        Diagnostic::error(
                            "PRESS_GENERIC_TEMPLATE_REQUIRED",
                            "Generic print artifact settings require a complete printer geometry template.",
                        )
                    });
                    match template {
                        Ok(template) => (
                            2.0 * trim_width
                                + spine
                                + 2.0
                                    * (template.bleed_inches
                                        + template.wrap_inches
                                        + template.gutter_inches
                                        + template.flap_inches),
                            trim_height + 2.0 * (template.bleed_inches + template.wrap_inches),
                        ),
                        Err(diagnostic) => return Some(Err(diagnostic)),
                    }
                }
                "perfect-bound-outside" | "perfect-bound-inside"
                    if product.vendor == "BarnesAndNoblePress" =>
                {
                    let template = product.print_template_evidence.as_ref().ok_or_else(|| {
                        Diagnostic::error(
                            "PRESS_TEMPLATE_REQUIRED",
                            "B&N Press artifact settings require imported measured template evidence.",
                        )
                    });
                    match template {
                        Ok(template) => (
                            template.full_cover_width_inches.unwrap_or_default(),
                            template.full_cover_height_inches.unwrap_or_default(),
                        ),
                        Err(diagnostic) => return Some(Err(diagnostic)),
                    }
                }
                "perfect-bound-outside" | "perfect-bound-inside" => {
                    (2.0 * trim_width + spine + 0.25, trim_height + 0.25)
                }
                "case-wrap" if product.vendor == "Generic" => {
                    let template = product.print_template_evidence.as_ref().ok_or_else(|| {
                        Diagnostic::error(
                            "PRESS_GENERIC_TEMPLATE_REQUIRED",
                            "Generic print artifact settings require a complete printer geometry template.",
                        )
                    });
                    match template {
                        Ok(template) => (
                            2.0 * trim_width
                                + spine
                                + 2.0
                                    * (template.bleed_inches
                                        + template.wrap_inches
                                        + template.gutter_inches
                                        + template.flap_inches),
                            trim_height + 2.0 * (template.bleed_inches + template.wrap_inches),
                        ),
                        Err(diagnostic) => return Some(Err(diagnostic)),
                    }
                }
                "case-wrap" | "dust-jacket" if product.vendor == "BarnesAndNoblePress" => {
                    let template = product.print_template_evidence.as_ref().ok_or_else(|| {
                        Diagnostic::error(
                            "PRESS_TEMPLATE_REQUIRED",
                            "B&N Press artifact settings require imported measured template evidence.",
                        )
                    });
                    match template {
                        Ok(template) => (
                            template.full_cover_width_inches.unwrap_or_default(),
                            template.full_cover_height_inches.unwrap_or_default(),
                        ),
                        Err(diagnostic) => return Some(Err(diagnostic)),
                    }
                }
                "case-wrap" if product.vendor == "AmazonKdp" => {
                    (2.0 * trim_width + spine + 1.02, trim_height + 1.02)
                }
                "case-wrap" => (2.0 * (trim_width - 0.185) + spine + 2.25, trim_height + 1.5),
                "dust-jacket" => (
                    2.0 * (trim_width + 0.4375) + spine + 7.25,
                    trim_height + 0.5,
                ),
                "digital-cloth-setup" => return None,
                _ => {
                    return Some(Err(Diagnostic::error(
                        "PRESS_COVER_SURFACE_INVALID",
                        format!("Cover surface '{role}' is unsupported."),
                    )));
                }
            };
            Some(Ok(PhysicalCoverSurface {
                role: role.clone(),
                width_points: geometry.0 * 72.0,
                height_points: geometry.1 * 72.0,
                spine_points: spine * 72.0,
                inside_spine_no_ink_points: if role == "perfect-bound-inside"
                    && product.vendor == "IngramSpark"
                {
                    (spine + 0.125) * 72.0
                } else {
                    0.0
                },
            }))
        })
        .collect()
}

fn crop_cover_panel(
    page: &LayoutPage,
    left_points: f32,
    top_points: f32,
    width_points: f32,
    height_points: f32,
) -> LayoutPage {
    let mut panel = page.clone();
    panel.width_points = Some(width_points);
    panel.height_points = Some(height_points);
    panel.page_label = None;
    panel.bookmark = None;
    for line in &mut panel.lines {
        line.x -= left_points;
        line.y -= top_points;
        line.rotation_origin_x = line.rotation_origin_x.map(|value| value - left_points);
        line.rotation_origin_y = line.rotation_origin_y.map(|value| value - top_points);
    }
    for image in &mut panel.images {
        image.x -= left_points;
        image.y -= top_points;
    }
    for shape in &mut panel.shapes {
        shape.x -= left_points;
        shape.y -= top_points;
    }
    panel
}

impl LayoutTolerance {
    fn authoring_preview() -> Self {
        Self {
            allow_pending_accessibility: true,
            clip_composition_text_overflow: true,
        }
    }
}

pub fn run(job_root: &Path) -> RenderResult<()> {
    let request = load_request(job_root)?;
    bind_job_id(run_parsed(job_root, &request), &request.job_id)
}

fn report_progress(job_root: &Path, request: &RenderRequest, percent: usize, message: &str) {
    let payload = serde_json::json!({
        "jobId": request.job_id,
        "percent": percent.min(100),
        "message": message,
    });
    if let Ok(bytes) = serde_json::to_vec(&payload) {
        let _ = fs::write(job_root.join("progress.json"), bytes);
    }
}

fn report_fraction(
    job_root: &Path,
    request: &RenderRequest,
    start: usize,
    end: usize,
    completed: usize,
    total: usize,
    message: &str,
) {
    let percent = (end.saturating_sub(start) * completed.min(total))
        .checked_div(total)
        .map_or(end, |value| start + value);
    report_progress(job_root, request, percent, message);
}

fn partition_images(
    mut images: std::collections::BTreeMap<String, EmbeddedImage>,
    interior_ids: &BTreeSet<String>,
    cover_ids: &BTreeSet<String>,
) -> (
    std::collections::BTreeMap<String, EmbeddedImage>,
    std::collections::BTreeMap<String, EmbeddedImage>,
) {
    let mut interior = std::collections::BTreeMap::new();
    let mut cover = std::collections::BTreeMap::new();
    for id in interior_ids.union(cover_ids) {
        let Some(image) = images.remove(id) else {
            continue;
        };
        if interior_ids.contains(id) && cover_ids.contains(id) {
            interior.insert(id.clone(), image.clone());
            cover.insert(id.clone(), image);
        } else if interior_ids.contains(id) {
            interior.insert(id.clone(), image);
        } else {
            cover.insert(id.clone(), image);
        }
    }
    (interior, cover)
}

fn run_parsed(job_root: &Path, request: &RenderRequest) -> RenderResult<()> {
    report_progress(job_root, request, 1, "Validating input");
    let mut validation_progress = |completed: usize, total: usize| {
        report_fraction(
            job_root,
            request,
            2,
            20,
            completed,
            total,
            "Validating images",
        );
    };
    let decoded_assets = validate_request(request, job_root, &mut validation_progress)?;
    ensure_output_is_safe(job_root)?;
    ensure_not_cancelled(job_root)?;
    let output = job_root.join("output");
    let staging = StagingDirectory::create(job_root.join(".output-staging"))?;

    let tolerance = LayoutTolerance {
        allow_pending_accessibility: request.output_purpose == OutputPurpose::ReadingCopy,
        ..LayoutTolerance::default()
    };
    report_progress(job_root, request, 22, "Paginating book");
    let mut layout = paginate_with_cancellation(request, Some(job_root), tolerance)?;
    if let Some(product) = request.print_artifact_profile.as_ref() {
        while layout.pages.len() < product.minimum_pages {
            let mut manufacturing_page = empty_body_page();
            manufacturing_page.kind = PageKind::Blank;
            layout.pages.push(manufacturing_page);
        }
        if !layout.pages.len().is_multiple_of(2) {
            let mut manufacturing_page = empty_body_page();
            manufacturing_page.kind = PageKind::Blank;
            layout.pages.push(manufacturing_page);
        }
    }
    if layout.pages.len() > MAX_PAGES {
        return Err(Box::new(RenderResponse::failed(
            "failed",
            Diagnostic::error(
                "PRESS_PAGE_LIMIT",
                format!("The document exceeds {MAX_PAGES} pages."),
            ),
        )));
    }
    report_progress(job_root, request, 38, "Composing cover surfaces");
    let is_digital_pdf = request.profile == "generic-digital-pdf-v1";
    let mut cover_width = 0.0;
    let mut spine_width = 0.0;
    let mut physical_covers = Vec::<(PhysicalCoverSurface, LayoutPage)>::new();
    let cover_page = if request.cover.is_some() {
        if is_digital_pdf {
            cover_width = request.trim.width_inches * 72.0;
            Some(
                digital_cover_layout(
                    request,
                    tolerance.allow_pending_accessibility,
                    &mut layout.diagnostics,
                )
                .map_err(|diagnostic| Box::new(RenderResponse::failed("rejected", diagnostic)))?,
            )
        } else {
            for surface in physical_cover_surfaces(request, layout.pages.len())
                .map_err(|diagnostic| Box::new(RenderResponse::failed("rejected", diagnostic)))?
            {
                let surface_scene = request
                    .cover
                    .as_ref()
                    .and_then(|cover| cover.scenes.get(&surface.role));
                if let Some(scene) = surface_scene {
                    validate_inside_spine_no_ink(scene, &surface).map_err(|diagnostic| {
                        Box::new(RenderResponse::failed("rejected", diagnostic))
                    })?;
                }
                let page = if surface.role == "perfect-bound-inside" && surface_scene.is_none() {
                    blank_cover_layout(surface.width_points, surface.height_points)
                } else {
                    cover_layout(
                        request,
                        surface.width_points,
                        surface.height_points,
                        surface_scene,
                    )
                    .map_err(|diagnostic| {
                        Box::new(RenderResponse::failed("rejected", diagnostic))
                    })?
                };
                physical_covers.push((surface, page));
            }
            if let Some((surface, _)) = physical_covers.first() {
                cover_width = surface.width_points;
                spine_width = surface.spine_points;
            }
            None
        }
    } else {
        None
    };
    let mut font_pages = layout.pages.clone();
    font_pages.extend(cover_page.iter().cloned());
    font_pages.extend(physical_covers.iter().map(|(_, page)| page.clone()));
    let fonts = subset_for_layout(&font_pages)
        .map_err(|diagnostic| Box::new(RenderResponse::failed("rejected", diagnostic)))?;

    ensure_not_cancelled(job_root)?;
    report_progress(job_root, request, 48, "Preparing publication images");

    let is_pdfx = request.profile == "ingram-print-pdfx1a-v2";
    let is_pdfa = request.profile == "bn-print-pdfa1b-v1";
    let interior_image_ids = layout
        .pages
        .iter()
        .chain(cover_page.iter())
        .flat_map(|page| page.images.iter().map(|image| image.asset_id.clone()))
        .collect::<BTreeSet<_>>();
    let cover_image_ids = physical_covers
        .iter()
        .flat_map(|(_, page)| page.images.iter().map(|image| image.asset_id.clone()))
        .collect::<BTreeSet<_>>();
    let black_and_white = request.ink == "BlackAndWhite";
    let (interior_images, cover_images) = if black_and_white {
        let mut interior_progress = |completed: usize, total: usize| {
            report_fraction(
                job_root,
                request,
                48,
                58,
                completed,
                total,
                "Preparing interior images",
            );
        };
        let interior = prepare_images(
            request,
            &decoded_assets,
            &interior_image_ids,
            is_pdfx,
            true,
            &mut interior_progress,
        )
        .map_err(|diagnostic| Box::new(RenderResponse::failed("rejected", diagnostic)))?;
        let mut cover_progress = |completed: usize, total: usize| {
            report_fraction(
                job_root,
                request,
                58,
                68,
                completed,
                total,
                "Preparing cover images",
            );
        };
        let cover = prepare_images(
            request,
            &decoded_assets,
            &cover_image_ids,
            is_pdfx,
            false,
            &mut cover_progress,
        )
        .map_err(|diagnostic| Box::new(RenderResponse::failed("rejected", diagnostic)))?;
        (interior, cover)
    } else {
        let all_image_ids = interior_image_ids
            .union(&cover_image_ids)
            .cloned()
            .collect::<BTreeSet<_>>();
        let mut image_progress = |completed: usize, total: usize| {
            report_fraction(
                job_root,
                request,
                48,
                68,
                completed,
                total,
                "Preparing publication images",
            );
        };
        let prepared = prepare_images(
            request,
            &decoded_assets,
            &all_image_ids,
            is_pdfx,
            false,
            &mut image_progress,
        )
        .map_err(|diagnostic| Box::new(RenderResponse::failed("rejected", diagnostic)))?;
        partition_images(prepared, &interior_image_ids, &cover_image_ids)
    };
    drop(decoded_assets);
    let mut maximum_total_ink_percent = interior_images
        .values()
        .chain(cover_images.values())
        .map(|image| image.maximum_total_ink_percent)
        .fold(0.0_f32, f32::max);
    if is_pdfx && let Some(cover) = request.cover.as_ref() {
        maximum_total_ink_percent = maximum_total_ink_percent
            .max(cover_background_total_ink_percent(&cover.background_color));
    }
    let interior_options = PdfOptions::interior(request, is_pdfx, is_pdfa);
    let (mut artifacts, interior_inspection) = if is_digital_pdf {
        let cover = cover_page.as_ref().ok_or_else(|| {
            Box::new(RenderResponse::failed(
                "rejected",
                Diagnostic::error(
                    "PRESS_DIGITAL_COVER_REQUIRED",
                    "Digital PDF requires a front cover.",
                ),
            ))
        })?;
        layout.pages.insert(0, cover.clone());
        for entry in &mut layout.page_map {
            entry.page_number += 1;
        }
        for page in &mut layout.pages {
            for line in &mut page.lines {
                if let Some(target) = &mut line.link_page {
                    *target += 1;
                }
            }
        }
        let book_bytes = write_pdf_cancellable_with_progress(
            &layout.pages,
            &fonts,
            &interior_images,
            &interior_options,
            || job_root.join("cancel.requested").exists(),
            |completed, total| {
                report_fraction(
                    job_root,
                    request,
                    70,
                    88,
                    completed,
                    total,
                    "Writing book PDF",
                );
            },
        )
        .map_err(pdf_failure)?;
        let book_path = staging.path().join("book.pdf");
        fs::write(&book_path, &book_bytes).map_err(io_failure)?;
        let inspection = inspect::validate(&book_path, &interior_options)
            .map_err(|diagnostic| Box::new(RenderResponse::failed("failed", diagnostic)))?;
        (
            vec![artifact(
                "book-pdf",
                "output/book.pdf",
                &book_bytes,
                layout.pages.len(),
            )],
            inspection,
        )
    } else {
        let interior_bytes = write_pdf_cancellable_with_progress(
            &layout.pages,
            &fonts,
            &interior_images,
            &interior_options,
            || job_root.join("cancel.requested").exists(),
            |completed, total| {
                report_fraction(
                    job_root,
                    request,
                    70,
                    88,
                    completed,
                    total,
                    "Writing interior PDF",
                );
            },
        )
        .map_err(pdf_failure)?;
        let interior_path = staging.path().join("interior.pdf");
        fs::write(&interior_path, &interior_bytes).map_err(io_failure)?;
        let inspection = inspect::validate(&interior_path, &interior_options)
            .map_err(|diagnostic| Box::new(RenderResponse::failed("failed", diagnostic)))?;
        (
            vec![artifact(
                "interior-pdf",
                "output/interior.pdf",
                &interior_bytes,
                layout.pages.len(),
            )],
            inspection,
        )
    };
    let mut cover_inspections = Vec::new();
    if !is_digital_pdf {
        for (index, (surface, rendered_cover)) in physical_covers.iter().enumerate() {
            if surface.role == "perfect-bound-inside" {
                continue;
            }
            if request
                .print_artifact_profile
                .as_ref()
                .is_some_and(|product| {
                    product.vendor == "BarnesAndNoblePress"
                        && product.cover_submission_mode == "SeparatePanelsVendorSpine"
                })
            {
                let product = request
                    .print_artifact_profile
                    .as_ref()
                    .expect("print artifact profile");
                let template = product
                    .print_template_evidence
                    .as_ref()
                    .expect("validated B&N template evidence");
                let front_width = template.front_cover_width_inches.unwrap_or_default() * 72.0;
                let front_height = template.front_cover_height_inches.unwrap_or_default() * 72.0;
                let back_width = template.back_cover_width_inches.unwrap_or_default() * 72.0;
                let back_height = template.back_cover_height_inches.unwrap_or_default() * 72.0;
                let front_top = ((surface.height_points - front_height) / 2.0).max(0.0);
                let back_top = ((surface.height_points - back_height) / 2.0).max(0.0);
                let panels = [
                    (
                        "back-cover-pdf",
                        "back-cover.pdf",
                        crop_cover_panel(rendered_cover, 0.0, back_top, back_width, back_height),
                    ),
                    (
                        "front-cover-pdf",
                        "front-cover.pdf",
                        crop_cover_panel(
                            rendered_cover,
                            surface.width_points - front_width,
                            front_top,
                            front_width,
                            front_height,
                        ),
                    ),
                ];
                for (kind, filename, panel) in panels {
                    let options = PdfOptions::cover(
                        request,
                        is_pdfx,
                        is_pdfa,
                        panel.width_points.unwrap_or_default(),
                        panel.height_points.unwrap_or_default(),
                        0.0,
                    );
                    let bytes = write_pdf_cancellable_with_progress(
                        &[panel],
                        &fonts,
                        &cover_images,
                        &options,
                        || job_root.join("cancel.requested").exists(),
                        |_, _| {},
                    )
                    .map_err(pdf_failure)?;
                    let path = staging.path().join(filename);
                    fs::write(&path, &bytes).map_err(io_failure)?;
                    cover_inspections.push(inspect::validate(&path, &options).map_err(
                        |diagnostic| Box::new(RenderResponse::failed("failed", diagnostic)),
                    )?);
                    artifacts.push(artifact(kind, &format!("output/{filename}"), &bytes, 1));
                }
                cover_width = surface.width_points;
                spine_width = surface.spine_points;
                continue;
            }
            let (kind, filename, pages) = match surface.role.as_str() {
                "perfect-bound-outside" => {
                    let mut pages = vec![rendered_cover.clone()];
                    if let Some((_, inside)) = physical_covers
                        .iter()
                        .find(|(item, _)| item.role == "perfect-bound-inside")
                    {
                        pages.push(inside.clone());
                    }
                    ("perfect-bound-cover-pdf", "perfect-bound-cover.pdf", pages)
                }
                "case-wrap" => (
                    "case-cover-pdf",
                    "case-cover.pdf",
                    vec![rendered_cover.clone()],
                ),
                "dust-jacket" => (
                    "dust-jacket-pdf",
                    "dust-jacket.pdf",
                    vec![rendered_cover.clone()],
                ),
                _ => continue,
            };
            let cover_options = PdfOptions::cover(
                request,
                is_pdfx,
                is_pdfa,
                surface.width_points,
                surface.height_points,
                surface.spine_points,
            );
            let cover_start = 88 + index * 5 / physical_covers.len().max(1);
            let cover_end = 88 + (index + 1) * 5 / physical_covers.len().max(1);
            let cover_bytes = write_pdf_cancellable_with_progress(
                &pages,
                &fonts,
                &cover_images,
                &cover_options,
                || job_root.join("cancel.requested").exists(),
                |completed, total| {
                    report_fraction(
                        job_root,
                        request,
                        cover_start,
                        cover_end,
                        completed,
                        total,
                        "Writing cover PDF",
                    );
                },
            )
            .map_err(pdf_failure)?;
            let cover_path = staging.path().join(filename);
            fs::write(&cover_path, &cover_bytes).map_err(io_failure)?;
            cover_inspections.push(
                inspect::validate(&cover_path, &cover_options)
                    .map_err(|diagnostic| Box::new(RenderResponse::failed("failed", diagnostic)))?,
            );
            artifacts.push(artifact(
                kind,
                &format!("output/{filename}"),
                &cover_bytes,
                pages.len(),
            ));
            if index == 0 {
                cover_width = surface.width_points;
                spine_width = surface.spine_points;
            }
        }
        if request
            .print_artifact_profile
            .as_ref()
            .is_some_and(|product| {
                product
                    .required_cover_surfaces
                    .iter()
                    .any(|surface| surface == "digital-cloth-setup")
            })
        {
            let product = request
                .print_artifact_profile
                .as_ref()
                .expect("print artifact profile");
            let manifest = serde_json::to_vec_pretty(&serde_json::json!({
                "registryVersion": product.registry_version,
                "artifactProfileKey": product.artifact_profile_key,
                "coverMaterial": product.cover_material,
                "spineWidthPoints": spine_width,
                "spineCopy": request.cover.as_ref().map(|cover| cover.spine_text.as_str()).unwrap_or(""),
                "constraint": "Ingram Digital Cloth spine copy uses the vendor constrained gold-stamp treatment."
            })).map_err(|error| io_failure(std::io::Error::other(error)))?;
            fs::write(staging.path().join("cloth-setup.json"), &manifest).map_err(io_failure)?;
            artifacts.push(artifact(
                "print-setup-manifest",
                "output/cloth-setup.json",
                &manifest,
                0,
            ));
        }
        if request
            .print_artifact_profile
            .as_ref()
            .is_some_and(|product| product.vendor == "BarnesAndNoblePress")
        {
            let product = request
                .print_artifact_profile
                .as_ref()
                .expect("print artifact profile");
            let template = product
                .print_template_evidence
                .as_ref()
                .expect("validated B&N template evidence");
            let cover = request.cover.as_ref().expect("validated cover");
            let identifier = match product.identifier_mode.as_str() {
                "VendorSku" => serde_json::json!({ "mode": "VendorSku", "value": null }),
                "VendorAssignedIsbn" => {
                    serde_json::json!({ "mode": "VendorAssignedIsbn", "value": null })
                }
                _ => serde_json::json!({ "mode": "UserSuppliedIsbn", "value": cover.isbn }),
            };
            let artifact_mapping = if product.cover_submission_mode == "SeparatePanelsVendorSpine" {
                serde_json::json!({
                    "interior": "interior-pdf",
                    "frontCover": "front-cover-pdf",
                    "backCover": "back-cover-pdf",
                    "spine": "B&N-generated in the upload wizard"
                })
            } else {
                serde_json::json!({
                    "interior": "interior-pdf",
                    "fullCover": if product.binding == "JacketedCaseLaminate" { "dust-jacket-pdf" } else if product.binding == "CaseLaminate" { "case-cover-pdf" } else { "perfect-bound-cover-pdf" },
                    "spine": "included in measured full-wrap PDF"
                })
            };
            let handoff_actions = if product.project_use == "PersonalUse" {
                serde_json::json!([
                    "Choose Print for Personal Use in B&N Press.",
                    "Allow B&N Press to assign its vendor SKU and overlay a no-price barcode."
                ])
            } else {
                let identifier_action = if product.identifier_mode == "VendorAssignedIsbn" {
                    "Request B&N Press's free ISBN in the vendor wizard."
                } else {
                    "Enter the supplied ISBN in the vendor wizard and confirm it matches the publication metadata."
                };
                serde_json::json!([
                    "Choose Print for Sale in B&N Press.",
                    identifier_action,
                    "Allow B&N Press to overlay the ISBN barcode and price."
                ])
            };
            let manifest = serde_json::to_vec_pretty(&serde_json::json!({
                "schemaVersion": 1,
                "provider": "BarnesAndNoblePress",
                "registryVersion": product.registry_version,
                "artifactProfileKey": product.artifact_profile_key,
                "profile": request.profile,
                "projectUse": product.project_use,
                "identifier": identifier,
                "coverSubmissionMode": product.cover_submission_mode,
                "spineReadingDirection": cover.spine_reading_direction,
                "template": {
                    "geometryFingerprint": template.geometry_fingerprint,
                    "pageCount": template.page_count,
                    "fullCoverTemplateSha256": template.full_cover_template_sha256,
                    "frontCoverTemplateSha256": template.front_cover_template_sha256,
                    "backCoverTemplateSha256": template.back_cover_template_sha256
                },
                "geometry": {
                    "trimWidthInches": template.trim_width_inches,
                    "trimHeightInches": template.trim_height_inches,
                    "bleedInches": template.bleed_inches,
                    "safeInches": template.safe_inches,
                    "spineWidthInches": template.spine_width_inches,
                    "fullCoverWidthInches": template.full_cover_width_inches,
                    "fullCoverHeightInches": template.full_cover_height_inches,
                    "frontCoverWidthInches": template.front_cover_width_inches,
                    "frontCoverHeightInches": template.front_cover_height_inches,
                    "backCoverWidthInches": template.back_cover_width_inches,
                    "backCoverHeightInches": template.back_cover_height_inches,
                    "renderedCoverWidthPoints": cover_width,
                    "renderedSpineWidthPoints": spine_width
                },
                "barcode": {
                    "mode": "VendorOverlay",
                    "reserve": "bottom-right back-cover safe region",
                    "instruction": "Do not upload a baked-in barcode; B&N Press generates the SKU or ISBN barcode."
                },
                "artifactToUpload": artifact_mapping,
                "externalActions": handoff_actions
            }))
            .map_err(|error| io_failure(std::io::Error::other(error)))?;
            fs::write(staging.path().join("print-setup.json"), &manifest).map_err(io_failure)?;
            artifacts.push(artifact(
                "print-setup-manifest",
                "output/print-setup.json",
                &manifest,
                0,
            ));
        }
    }
    ensure_not_cancelled(job_root)?;
    report_progress(job_root, request, 95, "Inspecting finished PDFs");

    let image_evidence = layout
        .pages
        .iter()
        .chain(physical_covers.iter().map(|(_, page)| page))
        .enumerate()
        .flat_map(|(page_index, page)| {
            let page_images = if page.kind == PageKind::Cover {
                &cover_images
            } else {
                &interior_images
            };
            page.images.iter().filter_map(move |placement| {
                placement_dpi(placement, page_images).map(|effective_dpi| ImageEvidence {
                    asset_id: placement.asset_id.clone(),
                    page_number: page_index + 1,
                    effective_dpi,
                })
            })
        })
        .collect::<Vec<_>>();
    let lowest_resolution_image = image_evidence
        .iter()
        .min_by(|left, right| left.effective_dpi.total_cmp(&right.effective_dpi));
    let minimum_effective_dpi = lowest_resolution_image.map(|item| item.effective_dpi);
    let mut diagnostics = layout.diagnostics.clone();
    let required_dpi = required_effective_dpi(&request.profile);
    if minimum_effective_dpi.is_some_and(|dpi| dpi < required_dpi) {
        let evidence = lowest_resolution_image.expect("minimum DPI came from image evidence");
        diagnostics.push(
            Diagnostic::warning(
                "PRESS_IMAGE_DPI_LOW",
                format!(
                    "The lowest effective image resolution is {:.1} DPI; this profile expects {:.0} DPI. Inspect the affected page at full size.",
                    evidence.effective_dpi, required_dpi
                ),
            )
            .with_source("asset", evidence.asset_id.clone())
            .with_page(evidence.page_number),
        );
    }

    report_progress(job_root, request, 98, "Promoting validated artifacts");
    staging.promote(&output)?;
    let response = RenderResponse {
        protocol_version: 10,
        renderer_version: env!("CARGO_PKG_VERSION"),
        job_id: Some(request.job_id.clone()),
        status: "completed".to_owned(),
        artifacts,
        page_map: layout.page_map,
        diagnostics,
        evidence: Some(ValidationEvidence {
            validation_status: "validated".to_owned(),
            claimed_standard: if is_pdfx {
                Some("PDF/X-1a:2001".to_owned())
            } else if is_pdfa {
                Some("PDF/A-1b".to_owned())
            } else {
                None
            },
            declared_standard: if is_pdfx {
                Some("PDF/X-1a:2001".to_owned())
            } else if is_pdfa {
                Some("PDF/A-1b".to_owned())
            } else {
                None
            },
            pdf_version: if is_pdfx {
                "1.3"
            } else if is_pdfa {
                "1.4"
            } else {
                "1.7"
            }
            .to_owned(),
            toc_converged: layout.toc_converged,
            has_encryption: false,
            has_transparency: interior_inspection.has_transparency
                || cover_inspections
                    .iter()
                    .any(|inspection| inspection.has_transparency),
            has_forbidden_actions: false,
            annotation_count: interior_inspection.annotation_count
                + cover_inspections
                    .iter()
                    .map(|inspection| inspection.annotation_count)
                    .sum::<usize>(),
            fonts_embedded: interior_inspection.fonts_embedded,
            to_unicode_maps_present: interior_inspection.to_unicode,
            output_intent_count: if is_pdfx || is_pdfa {
                1 + cover_inspections.len()
            } else {
                0
            },
            maximum_total_ink_percent,
            rendered_features: layout.features,
            cover_width_points: cover_width,
            spine_width_points: spine_width,
            interior_width_points: request.trim.width_inches * 72.0,
            interior_height_points: request.trim.height_inches * 72.0,
            cover_height_points: physical_covers.first().map_or_else(
                || {
                    request.cover.as_ref().map_or(0.0, |cover| {
                        (request.trim.height_inches + cover.bleed_inches * 2.0) * 72.0
                    })
                },
                |(surface, _)| surface.height_points,
            ),
            cover_surfaces: physical_covers
                .iter()
                .filter(|(surface, _)| surface.role != "perfect-bound-inside")
                .map(|(surface, _)| CoverSurfaceEvidence {
                    role: surface.role.clone(),
                    width_points: surface.width_points,
                    height_points: surface.height_points,
                    page_count: if surface.role == "perfect-bound-outside"
                        && physical_covers
                            .iter()
                            .any(|(candidate, _)| candidate.role == "perfect-bound-inside")
                    {
                        2
                    } else {
                        1
                    },
                })
                .collect(),
            interior_page_boxes_consistent: true,
            cover_page_boxes_consistent: true,
            fonts: fonts
                .values()
                .map(|font| FontEvidence {
                    name: font.postscript_name.to_owned(),
                    embedded: true,
                    subset: true,
                    to_unicode: true,
                })
                .collect(),
            color_spaces: if is_pdfx {
                vec![
                    "DeviceCMYK".to_owned(),
                    "DeviceGray".to_owned(),
                    "ICCBased".to_owned(),
                ]
            } else {
                let mut spaces = vec!["DeviceGray".to_owned()];
                if !interior_images.is_empty() && request.ink == "Color" {
                    spaces.push("DeviceRGB".to_owned());
                }
                spaces
            },
            interior_image_color_space: layout
                .pages
                .iter()
                .any(|page| !page.images.is_empty())
                .then(|| {
                    (if request.ink == "BlackAndWhite" {
                        "DeviceGray"
                    } else if is_pdfx {
                        "DeviceCMYK"
                    } else {
                        "DeviceRGB"
                    })
                    .to_owned()
                }),
            cover_image_color_space: cover_page
                .as_ref()
                .is_some_and(|page| !page.images.is_empty())
                .then(|| (if is_pdfx { "DeviceCMYK" } else { "DeviceRGB" }).to_owned()),
            interior_image_count: layout.pages.iter().map(|page| page.images.len()).sum(),
            cover_image_count: cover_page.as_ref().map_or(0, |page| page.images.len()),
            image_count: request.assets.len(),
            minimum_effective_dpi,
            images: image_evidence,
        }),
    };
    println!(
        "{}",
        serde_json::to_string(&response).expect("serialize render response")
    );
    report_progress(job_root, request, 100, "Render complete");
    Ok(())
}

fn validate_inside_spine_no_ink(
    scene: &Value,
    surface: &PhysicalCoverSurface,
) -> Result<(), Diagnostic> {
    if surface.inside_spine_no_ink_points <= 0.0 {
        return Ok(());
    }
    let center = surface.width_points / 2.0;
    let start = center - surface.inside_spine_no_ink_points / 2.0;
    let end = center + surface.inside_spine_no_ink_points / 2.0;
    let intersects = scene
        .get("objects")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter(|object| {
            object
                .get("visible")
                .and_then(Value::as_bool)
                .unwrap_or(true)
        })
        .any(|object| {
            let bounds = object.get("bounds").unwrap_or(&Value::Null);
            let x = bounds
                .get("xPercent")
                .and_then(Value::as_f64)
                .unwrap_or_default() as f32
                / 100.0
                * surface.width_points;
            let width = bounds
                .get("widthPercent")
                .and_then(Value::as_f64)
                .unwrap_or_default() as f32
                / 100.0
                * surface.width_points;
            x < end && x + width > start
        });
    if intersects {
        return Err(Diagnostic::error(
            "PRESS_DUPLEX_INSIDE_SPINE_INK",
            "The duplex inside cover contains artwork in Ingram's required spine no-ink region.",
        ));
    }
    Ok(())
}

fn required_effective_dpi(profile: &str) -> f32 {
    if profile == "generic-digital-pdf-v1" {
        180.0
    } else {
        300.0
    }
}

pub fn trace(job_root: &Path) -> RenderResult<()> {
    let request = load_request(job_root)?;
    bind_job_id(trace_parsed(job_root, &request), &request.job_id)
}

fn trace_parsed(job_root: &Path, request: &RenderRequest) -> RenderResult<()> {
    validate_request(request, job_root, &mut |_, _| {})?;
    let browser_preview = request.layout_trace_mode.as_deref() == Some("browser-preview");
    let tolerance = if browser_preview {
        LayoutTolerance::authoring_preview()
    } else {
        LayoutTolerance::default()
    };
    let layout = paginate_with_cancellation(request, None, tolerance)?;
    let fonts = if browser_preview {
        None
    } else {
        Some(
            subset_for_layout(&layout.pages)
                .map_err(|diagnostic| Box::new(RenderResponse::failed("rejected", diagnostic)))?,
        )
    };
    let pages = layout
        .pages
        .iter()
        .map(|page| {
            let lines = page.lines.iter().map(|line| {
            let fallback;
            let runs = if line.runs.is_empty() {
                fallback = vec![LayoutRun {
                    text: line.text.clone(), face: FontFace::SerifRegular, underline: false,
                    strikethrough: false, baseline_shift_em: 0.0, size_scale: 1.0,
                    language: None,
                }];
                fallback.as_slice()
            } else { line.runs.as_slice() };
            let runs = runs.iter().map(|run| {
                let mut value = serde_json::json!({
                    "text": run.text,
                    "face": run.face,
                    "underline": run.underline,
                    "strikethrough": run.strikethrough,
                    "baselineShiftEm": run.baseline_shift_em,
                    "sizeScale": run.size_scale,
                    "language": run.language,
                });
                if let Some(fonts) = fonts.as_ref() {
                    let size = line.size * run.size_scale;
                    let glyphs = fonts
                        .get(&run.face)
                        .map_or_else(Vec::new, |font| font.shape(&run.text, size));
                    value["glyphs"] = serde_json::json!(glyphs);
                }
                value
            }).collect::<Vec<_>>();
            serde_json::json!({ "text": line.text, "size": line.size, "x": line.x, "y": line.y,
                "wordSpacing": line.word_spacing, "characterSpacing": line.character_spacing,
                "rotationDegrees": line.rotation_degrees,
                "rotationOriginX": line.rotation_origin_x,
                "rotationOriginY": line.rotation_origin_y,
                "opacity": line.opacity,
                "baselineOffsetPoints": line.baseline_offset_points,
                "lightText": line.light_text, "fillRgb": line.fill_rgb,
                "semanticRole": line.semantic_role, "artifact": line.artifact,
                "language": line.language, "readingOrder": line.reading_order,
                "semanticId": line.semantic_id, "semanticParentId": line.semantic_parent_id,
                "sourceStartUtf16": line.source_start_utf16,
                "sourceEndUtf16": line.source_end_utf16,
                "linkPage": line.link_page, "runs": runs })
        }).collect::<Vec<_>>();
            let kind = match page.kind {
                PageKind::Designed => "DesignedPage",
                PageKind::Body => "Body",
                PageKind::Blank => "Blank",
                PageKind::Cover => "Cover",
            };
            let paint_order = page
                .paint_order
                .iter()
                .map(|paint| match paint {
                    LayoutPaint::Shape(index) => {
                        serde_json::json!({ "kind": "shape", "index": index })
                    }
                    LayoutPaint::Image(index) => {
                        serde_json::json!({ "kind": "image", "index": index })
                    }
                    LayoutPaint::Line(index) => {
                        serde_json::json!({ "kind": "line", "index": index })
                    }
                })
                .collect::<Vec<_>>();
            serde_json::json!({
                "kind": kind,
                "widthPoints": page.width_points.unwrap_or(request.trim.width_inches * 72.0),
                "heightPoints": page.height_points.unwrap_or(request.trim.height_inches * 72.0),
                "pageLabel": page.page_label,
                "bookmark": page.bookmark,
                "paintOrder": paint_order,
                "lines": lines,
                "images": page.images,
                "shapes": page.shapes,
            })
        })
        .collect::<Vec<_>>();
    println!(
        "{}",
        serde_json::to_string(&serde_json::json!({
            "protocolVersion": 10,
            "rendererVersion": env!("CARGO_PKG_VERSION"),
            "jobId": request.job_id,
            "pages": pages,
            "pageMap": layout.page_map,
            "features": layout.features,
            "diagnostics": layout.diagnostics,
        }))
        .expect("serialize layout trace")
    );
    Ok(())
}

fn bind_job_id<T>(result: RenderResult<T>, job_id: &str) -> RenderResult<T> {
    result.map_err(|mut response| {
        response.job_id = Some(job_id.to_owned());
        response
    })
}

struct StagingDirectory {
    path: std::path::PathBuf,
    promoted: bool,
}

impl StagingDirectory {
    fn create(path: std::path::PathBuf) -> RenderResult<Self> {
        fs::create_dir(&path).map_err(io_failure)?;
        Ok(Self {
            path,
            promoted: false,
        })
    }

    fn path(&self) -> &Path {
        &self.path
    }

    fn promote(mut self, output: &Path) -> RenderResult<()> {
        fs::rename(&self.path, output).map_err(io_failure)?;
        self.promoted = true;
        Ok(())
    }
}

impl Drop for StagingDirectory {
    fn drop(&mut self) {
        if !self.promoted {
            let _ = fs::remove_dir_all(&self.path);
        }
    }
}

fn placement_dpi(
    placement: &LayoutImage,
    images: &std::collections::BTreeMap<String, crate::image::EmbeddedImage>,
) -> Option<f32> {
    let image = images.get(&placement.asset_id)?;
    if placement.width <= 0.0 || placement.height <= 0.0 {
        return None;
    }
    let source_width = image.width as f32 * placement.source_width_fraction.clamp(0.01, 1.0);
    let width_scale = placement.width / source_width;
    let height_scale = placement.height / image.height as f32;
    let points_per_pixel = match placement.fit {
        LayoutImageFit::Contain => width_scale.min(height_scale),
        LayoutImageFit::Cover => width_scale.max(height_scale),
        LayoutImageFit::Stretch => width_scale.max(height_scale),
    };
    Some(72.0 / points_per_pixel)
}

fn load_request(job_root: &Path) -> RenderResult<RenderRequest> {
    if !job_root.is_absolute() || !job_root.is_dir() || is_link_or_reparse(job_root) {
        return Err(Box::new(RenderResponse::failed(
            "rejected",
            Diagnostic::error(
                "PRESS_JOB_ROOT_UNSAFE",
                "The job root must be an existing absolute, non-linked directory.",
            ),
        )));
    }
    let request_path = job_root.join("input/request.json");
    if is_link_or_reparse(&request_path)
        || fs::metadata(&request_path)
            .map(|metadata| metadata.len() > MAX_REQUEST_BYTES)
            .unwrap_or(true)
    {
        return Err(Box::new(RenderResponse::failed(
            "rejected",
            Diagnostic::error(
                "PRESS_REQUEST_INVALID",
                "The request must be a regular non-linked file within the request-size limit.",
            ),
        )));
    }
    let bytes = fs::read(&request_path).map_err(|error| {
        Box::new(RenderResponse::failed(
            "rejected",
            Diagnostic::error("PRESS_REQUEST_MISSING", error.to_string()),
        ))
    })?;
    let json = bytes.strip_prefix(&[0xef, 0xbb, 0xbf]).unwrap_or(&bytes);
    serde_json::from_slice(json).map_err(|error| {
        Box::new(RenderResponse::failed(
            "rejected",
            Diagnostic::error("PRESS_REQUEST_INVALID", error.to_string()),
        ))
    })
}

fn validate_request(
    request: &RenderRequest,
    job_root: &Path,
    progress: &mut dyn FnMut(usize, usize),
) -> RenderResult<std::collections::BTreeMap<String, DecodedImage>> {
    if request.protocol_version != 10 {
        return reject(
            "PRESS_PROTOCOL_INVALID",
            "Lorekeeper Press requires protocol version 10.",
        );
    }
    if request.job_id.len() != 32 || !request.job_id.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return reject(
            "PRESS_JOB_ID_INVALID",
            "The job ID must contain 32 hexadecimal characters.",
        );
    }
    if !matches!(
        request.profile.as_str(),
        "generic-print-v2"
            | "generic-digital-pdf-v1"
            | "kdp-paperback-v2"
            | "kdp-hardcover-v1"
            | "ingram-print-pdfx1a-v2"
            | "bn-print-pdfa1b-v1"
    ) {
        return reject(
            "PRESS_PROFILE_UNSUPPORTED",
            format!("The profile '{}' is unsupported.", request.profile),
        );
    }
    if request.output_purpose == OutputPurpose::ReadingCopy
        && request.profile != "generic-digital-pdf-v1"
    {
        return reject(
            "PRESS_OUTPUT_PURPOSE_INVALID",
            "Reading-copy output is supported only by the generic Digital PDF profile.",
        );
    }
    if !matches!(
        request.ink.as_str(),
        "BlackAndWhite" | "Color" | "StandardColor" | "PremiumColor"
    ) {
        return reject(
            "PRESS_INK_UNSUPPORTED",
            format!("The ink intent '{}' is unsupported.", request.ink),
        );
    }
    if request.profile != "generic-digital-pdf-v1" {
        let product = request.print_artifact_profile.as_ref().ok_or_else(|| {
            Box::new(RenderResponse::failed(
                "rejected",
                Diagnostic::error(
                    "PRESS_PRINT_ARTIFACT_PROFILE_REQUIRED",
                    "Print profiles require resolved artifact settings.",
                ),
            ))
        })?;
        if product.registry_sha256 != registry_sha256() {
            return reject(
                "PRESS_PRINT_REGISTRY_MISMATCH",
                "The artifact profile does not match the renderer's bundled registry.",
            );
        }
        validate_registry_profile(product, request)
            .map_err(|diagnostic| Box::new(RenderResponse::failed("rejected", diagnostic)))?;
        if product.minimum_pages == 0
            || product.maximum_pages < product.minimum_pages
            || product.artifact_profile_key.is_empty()
            || request.cover.as_ref().is_some_and(|cover| {
                product
                    .required_cover_surfaces
                    .iter()
                    .any(|surface| !cover.surfaces.contains(surface))
            })
        {
            return reject(
                "PRESS_PRINT_ARTIFACT_PROFILE_INVALID",
                "The artifact profile or required cover surfaces are incomplete.",
            );
        }
    }
    if request
        .layout_trace_mode
        .as_deref()
        .is_some_and(|mode| mode != "browser-preview")
    {
        return reject(
            "PRESS_LAYOUT_TRACE_MODE_UNSUPPORTED",
            "The requested layout trace mode is unsupported.",
        );
    }
    let language = request
        .document
        .get("language")
        .and_then(Value::as_str)
        .unwrap_or("");
    assert_supported_language(language)
        .map_err(|diagnostic| Box::new(RenderResponse::failed("rejected", diagnostic)))?;
    validate_inline_languages(&request.document)
        .map_err(|diagnostic| Box::new(RenderResponse::failed("rejected", diagnostic)))?;
    validate_caption_bounds(&request.document, &request.document, &request.trim)
        .map_err(|diagnostic| Box::new(RenderResponse::failed("rejected", diagnostic)))?;
    if request.assets.len() > MAX_ASSETS {
        return reject(
            "PRESS_ASSET_LIMIT",
            format!("A job may declare at most {MAX_ASSETS} assets."),
        );
    }
    if let Some(cover) = &request.cover {
        if !matches!(
            cover.spine_reading_direction.as_str(),
            "TopToBottom" | "BottomToTop" | "Horizontal"
        ) {
            return reject(
                "PRESS_SPINE_DIRECTION_INVALID",
                "Spine reading direction must be TopToBottom, BottomToTop, or Horizontal.",
            );
        }
        if request
            .print_artifact_profile
            .as_ref()
            .is_some_and(|product| {
                product.vendor == "BarnesAndNoblePress"
                    && (cover.barcode_mode != "VendorOverlay"
                        || product.project_use == "PersonalUse"
                            && product.identifier_mode != "VendorSku"
                        || product.project_use == "ForSale"
                            && product.identifier_mode == "VendorSku")
            })
        {
            return reject(
                "PRESS_BN_IDENTIFIER_BARCODE_INVALID",
                "B&N project use, identifier mode, and vendor-overlay barcode behavior are inconsistent.",
            );
        }
        if request
            .print_artifact_profile
            .as_ref()
            .is_some_and(|product| {
                product.vendor == "BarnesAndNoblePress"
                    && product
                        .print_template_evidence
                        .as_ref()
                        .is_some_and(|template| template.page_count <= 50)
                    && !cover.spine_text.trim().is_empty()
            })
        {
            return reject(
                "PRESS_BN_SPINE_TEXT_INELIGIBLE",
                "B&N covers cannot contain spine text at 50 pages or fewer.",
            );
        }
        let barcode_mode_is_valid = if request.profile == "generic-digital-pdf-v1" {
            cover.barcode_mode == "None"
        } else {
            matches!(
                cover.barcode_mode.as_str(),
                "LorekeeperBarcode" | "VendorOverlay"
            ) && !(request.profile == "ingram-print-pdfx1a-v2"
                && cover.barcode_mode == "VendorOverlay")
        };
        if !barcode_mode_is_valid {
            return reject(
                "PRESS_BARCODE_MODE_INVALID",
                "The barcode mode is unsupported for the selected profile.",
            );
        }
        if cover.barcode_mode == "LorekeeperBarcode"
            && cover.isbn.as_deref().and_then(ean13_modules).is_none()
        {
            return reject(
                "PRESS_EAN13_INVALID",
                "Lorekeeper barcode generation requires a valid ISBN/EAN-13 checksum.",
            );
        }
        if !(0.0..=0.625).contains(&cover.bleed_inches)
            || !(0.0..=100.0).contains(&cover.image_crop_x_percent)
            || !(0.0..=100.0).contains(&cover.image_crop_y_percent)
        {
            return reject(
                "PRESS_COVER_GEOMETRY_INVALID",
                "Cover bleed or crop geometry is outside supported bounds.",
            );
        }
        if cover.title.chars().count() > 240
            || cover.subtitle.chars().count() > 400
            || cover.author.chars().count() > 240
            || cover.spine_text.chars().count() > 240
            || cover.back_copy.chars().count() > 4_000
        {
            return reject(
                "PRESS_COVER_TEXT_OVERFLOW",
                "Cover copy exceeds the bounded full-wrap template capacity.",
            );
        }
        if !is_hex_color(&cover.background_color) {
            return reject(
                "PRESS_COVER_COLOR_INVALID",
                "Cover background colors must use six-digit hexadecimal notation.",
            );
        }
    }
    let input_root = job_root.join("input");
    let mut total = 0_u64;
    let mut declared = BTreeSet::new();
    let mut declared_ids = BTreeSet::new();
    let mut validated_assets = std::collections::BTreeMap::new();
    let asset_total = request.assets.len();
    for (asset_index, asset) in request.assets.iter().enumerate() {
        let relative = Path::new(&asset.relative_path);
        if relative.is_absolute()
            || relative
                .components()
                .any(|part| !matches!(part, Component::Normal(_)))
            || !asset
                .relative_path
                .replace('\\', "/")
                .starts_with("assets/")
        {
            return reject(
                "PRESS_ASSET_PATH_UNSAFE",
                format!("Asset '{}' has an unsafe path.", asset.id),
            );
        }
        if !matches!(asset.media_type.as_str(), "image/png" | "image/jpeg") {
            return reject(
                "PRESS_ASSET_FORMAT_UNSUPPORTED",
                format!("Asset '{}' must be PNG or JPEG.", asset.id),
            );
        }
        if asset.width_pixels.is_none()
            || asset.height_pixels.is_none()
            || !declared_ids.insert(asset.id.clone())
            || !declared.insert(normalized_relative(relative))
        {
            return reject(
                "PRESS_ASSET_DECLARATION_INVALID",
                format!(
                    "Asset '{}' must have unique identity/path and declared pixel dimensions.",
                    asset.id
                ),
            );
        }
        let path = input_root.join(relative);
        if !path.is_file() || is_link_or_reparse(&path) {
            return reject(
                "PRESS_ASSET_PATH_UNSAFE",
                format!("Asset '{}' is missing or linked.", asset.id),
            );
        }
        let bytes = fs::read(&path).map_err(io_failure)?;
        if bytes.len() as u64 != asset.byte_length {
            return reject(
                "PRESS_ASSET_SIZE_MISMATCH",
                format!("Asset '{}' changed after declaration.", asset.id),
            );
        }
        if bytes.len() as u64 > MAX_ASSET_BYTES {
            return reject(
                "PRESS_ASSET_LIMIT",
                format!("Asset '{}' exceeds the byte limit.", asset.id),
            );
        }
        let hash = hex_hash(&bytes);
        if !hash.eq_ignore_ascii_case(&asset.sha256) {
            return reject(
                "PRESS_ASSET_HASH_MISMATCH",
                format!("Asset '{}' changed after declaration.", asset.id),
            );
        }
        let decoded = crate::image::decode_declared_image(asset, &bytes)
            .map_err(|diagnostic| Box::new(RenderResponse::failed("rejected", diagnostic)))?;
        total = total.saturating_add(bytes.len() as u64);
        validated_assets.insert(asset.id.clone(), decoded);
        progress(asset_index + 1, asset_total);
    }
    let mut validated_fonts = std::collections::BTreeMap::new();
    let mut declared_font_faces = BTreeSet::new();
    for font in &request.fonts {
        let relative = Path::new(&font.relative_path);
        if relative.is_absolute()
            || relative
                .components()
                .any(|part| !matches!(part, Component::Normal(_)))
            || !font.relative_path.replace('\\', "/").starts_with("fonts/")
        {
            return reject(
                "PRESS_FONT_PATH_UNSAFE",
                format!("Font '{}' has an unsafe path.", font.id),
            );
        }
        if !matches!(font.media_type.as_str(), "font/ttf" | "font/otf")
            || !font.embedding_rights_confirmed
            || font.family_key.trim().is_empty()
            || font.family_key.len() > 120
            || !matches!(font.weight, 100..=900)
            || !declared_font_faces.insert((
                font.family_key.to_ascii_lowercase(),
                font.weight,
                font.italic,
            ))
            || !declared.insert(normalized_relative(relative))
        {
            return reject(
                "PRESS_FONT_DECLARATION_INVALID",
                format!(
                    "Font '{}' requires a unique family/style, supported format, and confirmed embedding rights.",
                    font.id
                ),
            );
        }
        let path = input_root.join(relative);
        if !path.is_file() || is_link_or_reparse(&path) {
            return reject(
                "PRESS_FONT_PATH_UNSAFE",
                format!("Font '{}' is missing or linked.", font.id),
            );
        }
        let bytes = fs::read(&path).map_err(io_failure)?;
        if bytes.len() as u64 != font.byte_length
            || bytes.len() as u64 > MAX_ASSET_BYTES
            || !hex_hash(&bytes).eq_ignore_ascii_case(&font.sha256)
        {
            return reject(
                "PRESS_FONT_HASH_MISMATCH",
                format!("Font '{}' changed after declaration.", font.id),
            );
        }
        validate_font_embedding(font, &bytes)?;
        total = total.saturating_add(bytes.len() as u64);
        validated_fonts.insert(font.id.clone(), bytes);
    }
    if total > MAX_JOB_BYTES {
        return reject(
            "PRESS_JOB_LIMIT",
            "The declared asset bytes exceed the job limit.",
        );
    }
    configure_custom_fonts(&request.fonts, &validated_fonts)
        .map_err(|diagnostic| Box::new(RenderResponse::failed("rejected", diagnostic)))?;
    let declared_families = request
        .fonts
        .iter()
        .map(|font| font.family_key.to_ascii_lowercase())
        .collect::<BTreeSet<_>>();
    validate_font_family_references(&request.document, &declared_families)?;
    validate_asset_references(&request.document, &declared_ids)?;
    if let Some(asset_id) = request
        .cover
        .as_ref()
        .and_then(|cover| cover.asset_id.as_deref())
        && !asset_id.is_empty()
        && !declared_ids.contains(asset_id)
    {
        return reject(
            "PRESS_ASSET_REFERENCE_MISSING",
            format!("Cover references undeclared asset '{asset_id}'."),
        );
    }
    let mut actual = BTreeSet::new();
    collect_files(&input_root, &input_root, &mut actual)?;
    actual.remove("request.json");
    if actual != declared {
        return reject(
            "PRESS_ASSET_UNDECLARED",
            "The staged input directory does not exactly match the request and asset declarations.",
        );
    }
    Ok(validated_assets)
}

fn validate_font_embedding(font: &crate::model::FontDeclaration, bytes: &[u8]) -> RenderResult<()> {
    if bytes.len() < 12 || !matches!(&bytes[..4], b"\0\x01\0\0" | b"OTTO") {
        return reject(
            "PRESS_FONT_INVALID",
            format!("Font '{}' is not a supported OpenType font.", font.id),
        );
    }
    let table_count = u16::from_be_bytes([bytes[4], bytes[5]]) as usize;
    let os2 = (0..table_count).find_map(|index| {
        let start = 12 + index * 16;
        if bytes.get(start..start + 4)? != b"OS/2" {
            return None;
        }
        let offset =
            u32::from_be_bytes(bytes.get(start + 8..start + 12)?.try_into().ok()?) as usize;
        let length =
            u32::from_be_bytes(bytes.get(start + 12..start + 16)?.try_into().ok()?) as usize;
        bytes.get(offset..offset + length)
    });
    let Some(os2) = os2 else {
        return reject(
            "PRESS_FONT_EMBEDDING_UNKNOWN",
            format!("Font '{}' does not declare embedding permissions.", font.id),
        );
    };
    let fs_type = os2
        .get(8..10)
        .map(|value| u16::from_be_bytes([value[0], value[1]]))
        .unwrap_or(0x0002);
    if fs_type & (0x0002 | 0x0100 | 0x0200) != 0 {
        return reject(
            "PRESS_FONT_EMBEDDING_RESTRICTED",
            format!(
                "Font '{}' prohibits embedding, subsetting, or outline use.",
                font.id
            ),
        );
    }
    Ok(())
}

fn is_hex_color(value: &str) -> bool {
    value.strip_prefix('#').is_some_and(|digits| {
        digits.len() == 6 && digits.bytes().all(|byte| byte.is_ascii_hexdigit())
    })
}

fn validate_asset_references(value: &Value, declared_ids: &BTreeSet<String>) -> RenderResult<()> {
    match value {
        Value::Array(values) => {
            for child in values {
                validate_asset_references(child, declared_ids)?;
            }
        }
        Value::Object(values) => {
            if let Some(asset_id) = values.get("assetId").and_then(Value::as_str)
                && !asset_id.is_empty()
                && !declared_ids.contains(asset_id)
            {
                return reject(
                    "PRESS_ASSET_REFERENCE_MISSING",
                    format!("Publication content references undeclared asset '{asset_id}'."),
                );
            }
            for child in values.values() {
                validate_asset_references(child, declared_ids)?;
            }
        }
        _ => {}
    }
    Ok(())
}

fn validate_inline_languages(value: &Value) -> Result<(), Diagnostic> {
    match value {
        Value::Array(values) => {
            for child in values {
                validate_inline_languages(child)?;
            }
        }
        Value::Object(values) => {
            if let Some(language) = values.get("language").and_then(Value::as_str)
                && !language.trim().is_empty()
            {
                assert_supported_language(language)?;
            }
            if values
                .get("type")
                .and_then(Value::as_str)
                .is_some_and(|kind| kind.eq_ignore_ascii_case("Language"))
            {
                let language = values.get("value").and_then(Value::as_str).unwrap_or("");
                if !language.trim().is_empty() {
                    assert_supported_language(language)?;
                }
            }
            for child in values.values() {
                validate_inline_languages(child)?;
            }
        }
        _ => {}
    }
    Ok(())
}

fn validate_font_family_references(value: &Value, declared: &BTreeSet<String>) -> RenderResult<()> {
    match value {
        Value::Array(values) => {
            for child in values {
                validate_font_family_references(child, declared)?;
            }
        }
        Value::Object(values) => {
            if let Some(key) = values
                .get("fontFamilyKey")
                .and_then(Value::as_str)
                .filter(|key| !key.trim().is_empty())
            {
                let normalized = key.to_ascii_lowercase();
                if !matches!(
                    normalized.as_str(),
                    "serif"
                        | "sans"
                        | "mono"
                        | "builtin:lora"
                        | "builtin:nunito"
                        | "builtin:roboto-mono"
                ) && !declared.contains(&normalized)
                {
                    return reject(
                        "PRESS_FONT_FAMILY_UNSUPPORTED",
                        format!("Publication content references unsupported font family '{key}'."),
                    );
                }
            }
            for child in values.values() {
                validate_font_family_references(child, declared)?;
            }
        }
        _ => {}
    }
    Ok(())
}

fn collect_files(
    input_root: &Path,
    current: &Path,
    output: &mut BTreeSet<String>,
) -> RenderResult<()> {
    for entry in fs::read_dir(current).map_err(io_failure)? {
        let entry = entry.map_err(io_failure)?;
        let path = entry.path();
        if is_link_or_reparse(&path) {
            return reject(
                "PRESS_ASSET_PATH_UNSAFE",
                "Links and reparse points are forbidden in job input.",
            );
        }
        if path.is_dir() {
            collect_files(input_root, &path, output)?;
        } else {
            let relative = path.strip_prefix(input_root).map_err(|_| {
                Box::new(RenderResponse::failed(
                    "rejected",
                    Diagnostic::error(
                        "PRESS_ASSET_PATH_UNSAFE",
                        "An asset escaped the input root.",
                    ),
                ))
            })?;
            output.insert(normalized_relative(relative));
        }
    }
    Ok(())
}

fn ensure_output_is_safe(job_root: &Path) -> RenderResult<()> {
    if job_root.join("output").exists() || job_root.join(".output-staging").exists() {
        return Err(Box::new(RenderResponse::failed(
            "failed",
            Diagnostic::error(
                "PRESS_OUTPUT_UNSAFE",
                "The renderer never overwrites output or staging directories.",
            ),
        )));
    }
    Ok(())
}

fn ensure_not_cancelled(job_root: &Path) -> RenderResult<()> {
    if job_root.join("cancel.requested").exists() {
        return Err(Box::new(RenderResponse::failed(
            "cancelled",
            Diagnostic::error(
                "PRESS_RENDER_CANCELLED",
                "Rendering was cancelled before artifact promotion.",
            ),
        )));
    }
    Ok(())
}

fn check_layout_cancellation(job_root: Option<&Path>) -> RenderResult<()> {
    match job_root {
        Some(root) => ensure_not_cancelled(root),
        None => Ok(()),
    }
}

fn pdf_failure(diagnostic: Diagnostic) -> Box<RenderResponse> {
    Box::new(RenderResponse::failed(
        if diagnostic.code.as_ref() == "PRESS_RENDER_CANCELLED" {
            "cancelled"
        } else {
            "failed"
        },
        diagnostic,
    ))
}

#[cfg(test)]
fn paginate(request: &RenderRequest) -> RenderResult<LayoutDocument> {
    paginate_with_cancellation(request, None, LayoutTolerance::default())
}

fn paginate_with_cancellation(
    request: &RenderRequest,
    job_root: Option<&Path>,
    tolerance: LayoutTolerance,
) -> RenderResult<LayoutDocument> {
    let browser_preview = request.layout_trace_mode.as_deref() == Some("browser-preview");
    let leading_page_count =
        usize::from(request.profile == "generic-digital-pdf-v1" && request.cover.is_some());
    let trim = &request.trim;
    if !(3.5..=12.0).contains(&trim.width_inches)
        || !(5.0..=15.0).contains(&trim.height_inches)
        || !(0.25..=2.0).contains(&trim.margin_inches)
        || !(7.0..=30.0).contains(&trim.body_font_size_points)
        || !(1.0..=2.5).contains(&trim.body_line_height)
        || !(0.0..=0.25).contains(&trim.bleed_inches)
        || trim.minimum_widow_lines == 0
        || trim.minimum_orphan_lines == 0
    {
        return reject(
            "PRESS_LAYOUT_INVALID",
            "Trim, margin, typography, widow, or orphan values are outside supported bounds.",
        );
    }
    let mut pages = Vec::new();
    let mut diagnostics = Vec::new();
    let mut page_map = Vec::new();
    let mut body_start_page = None;
    let document = &request.document;
    let mut chapter_entries = Vec::new();
    let mut chapter_ordinal = 0usize;
    let mut semantic_order = 0i32;
    let toc_index = append_publication_sections(
        &mut pages,
        document,
        "Front",
        None,
        trim,
        request.profile == "generic-digital-pdf-v1",
        tolerance,
        &mut diagnostics,
        &mut page_map,
        &mut semantic_order,
    )?;
    let mut features = BTreeSet::new();
    if document
        .get("styles")
        .and_then(Value::as_array)
        .is_some_and(|styles| !styles.is_empty())
    {
        features.insert("named-styles".to_owned());
    }
    if document
        .get("publicationSections")
        .and_then(Value::as_array)
        .is_some_and(|sections| !sections.is_empty())
    {
        features.insert("publication-section".to_owned());
    }
    if let Some(sections) = document.get("sections").and_then(Value::as_array) {
        for (section_index, section) in sections.iter().enumerate() {
            check_layout_cancellation(job_root)?;
            append_publication_sections(
                &mut pages,
                document,
                "BeforeAct",
                Some(&string(section, "id")),
                trim,
                request.profile == "generic-digital-pdf-v1",
                tolerance,
                &mut diagnostics,
                &mut page_map,
                &mut semantic_order,
            )?;
            let section_title = numbered_title(
                &string(section, "title"),
                section_index + 1,
                document
                    .get("numberActs")
                    .and_then(Value::as_bool)
                    .unwrap_or(false),
                "Act",
            );
            let include_section_heading = section
                .get("includeHeading")
                .and_then(Value::as_bool)
                .unwrap_or_else(|| {
                    document
                        .get("includeActHeadings")
                        .and_then(Value::as_bool)
                        .unwrap_or(true)
                });
            if section
                .get("includePage")
                .and_then(Value::as_bool)
                .unwrap_or(false)
            {
                start_recto(&mut pages, trim, leading_page_count);
                pages.push(centered_page(
                    if include_section_heading {
                        &section_title
                    } else {
                        ""
                    },
                    &string(section, "synopsis"),
                    trim,
                ));
            } else if include_section_heading && !section_title.is_empty() {
                let style = BlockStyle {
                    size: 18.0,
                    line_height: 1.3,
                    indent: 0.0,
                    right_indent: 0.0,
                    first_line_indent: 0.0,
                    page_break_before: false,
                    keep_with_next: true,
                    alignment: "left".to_owned(),
                    face: FontFace::SansBold,
                    font_weight: 700,
                    small_caps: false,
                    space_before: 12.0,
                    space_after: 6.0,
                    semantic_role: LayoutSemanticRole::Heading1,
                };
                append_styled_text(&mut pages, &section_title, trim, &style);
            }
            for chapter in section
                .get("chapters")
                .and_then(Value::as_array)
                .into_iter()
                .flatten()
            {
                check_layout_cancellation(job_root)?;
                chapter_ordinal += 1;
                append_publication_sections(
                    &mut pages,
                    document,
                    "BeforeChapter",
                    Some(&string(chapter, "id")),
                    trim,
                    request.profile == "generic-digital-pdf-v1",
                    tolerance,
                    &mut diagnostics,
                    &mut page_map,
                    &mut semantic_order,
                )?;
                if request.profile != "generic-digital-pdf-v1"
                    && chapter_begins_with_facing_spread(chapter)
                {
                    ensure_next_leaf(&mut pages, LeafSide::Verso);
                } else if !is_designed_page_only_chapter(chapter) {
                    start_recto(&mut pages, trim, leading_page_count);
                }
                let chapter_start = pages.len() + 1;
                body_start_page.get_or_insert(chapter_start);
                let chapter_page_index = pages.len();
                let chapter_title = numbered_title(
                    &string(chapter, "title"),
                    chapter_ordinal,
                    document
                        .get("numberChapters")
                        .and_then(Value::as_bool)
                        .unwrap_or(false),
                    "Chapter",
                );
                chapter_entries.push((chapter_title.clone(), chapter_start));
                let mut blocks = chapter
                    .get("blocks")
                    .and_then(Value::as_array)
                    .cloned()
                    .unwrap_or_default();
                let include_chapter_heading = chapter
                    .get("includeHeading")
                    .and_then(Value::as_bool)
                    .unwrap_or_else(|| {
                        document
                            .get("includeChapterHeadings")
                            .and_then(Value::as_bool)
                            .unwrap_or(true)
                    });
                if include_chapter_heading && !chapter_title.is_empty() {
                    let chapter_heading_style = BlockStyle::chapter_heading(trim);
                    pages.push(empty_body_page());
                    append_styled_text(&mut pages, &chapter_title, trim, &chapter_heading_style);
                }
                let chapter_synopsis = string(chapter, "synopsis");
                if !chapter_synopsis.is_empty() {
                    let synopsis_style = BlockStyle {
                        size: trim.body_font_size_points,
                        line_height: trim.body_line_height,
                        indent: 18.0,
                        right_indent: 0.0,
                        first_line_indent: 0.0,
                        page_break_before: false,
                        keep_with_next: true,
                        alignment: "left".to_owned(),
                        face: FontFace::SerifItalic,
                        font_weight: 400,
                        small_caps: false,
                        space_before: 0.0,
                        space_after: 8.0,
                        semantic_role: LayoutSemanticRole::Paragraph,
                    };
                    append_styled_text(&mut pages, &chapter_synopsis, trim, &synopsis_style);
                }
                let mut active_list_id: Option<String> = None;
                let mut previous_space_after: f32 = 0.0;
                for block in blocks.drain(..) {
                    check_layout_cancellation(job_root)?;
                    semantic_order += 1;
                    let semantic_snapshot = pages
                        .iter()
                        .map(|page| (page.lines.len(), page.images.len()))
                        .collect::<Vec<_>>();
                    let block_id = string(&block, "id");
                    let block_type = string(&block, "type");
                    let semantic_id = if block_id.is_empty() {
                        format!("chapter-{chapter_ordinal}-block-{semantic_order}")
                    } else {
                        block_id.clone()
                    };
                    let semantic_parent_id = if block_type.eq_ignore_ascii_case("ListItem") {
                        Some(
                            active_list_id
                                .get_or_insert_with(|| format!("list-{semantic_id}"))
                                .clone(),
                        )
                    } else {
                        active_list_id = None;
                        None
                    };
                    let text = display_block_text(&block);
                    if block_has_marks(&block) {
                        features.insert("inline-marks".to_owned());
                    }
                    if block_type.eq_ignore_ascii_case("Figure") {
                        features.insert("flow-figure".to_owned());
                    }
                    if block_type.eq_ignore_ascii_case("DesignedPage") {
                        features.insert("designed-page".to_owned());
                    }
                    features.insert(format!(
                        "semantic-block-{}",
                        block_type.to_ascii_lowercase()
                    ));
                    if block_type.eq_ignore_ascii_case("DesignedPage") {
                        let composition_id = string(&block, "pageCompositionId");
                        let composition = chapter
                            .get("pageCompositions")
                            .and_then(Value::as_array)
                            .into_iter()
                            .flatten()
                            .find(|item| string(item, "id") == composition_id)
                            .ok_or_else(|| Box::new(RenderResponse::failed(
                                "rejected",
                                Diagnostic::error(
                                    "PRESS_COMPOSITION_MISSING",
                                    format!("Designed Page {block_id} references a missing composition."),
                                ),
                            )))?;
                        let rendered = designed_pages(
                            composition,
                            document,
                            trim,
                            request.profile == "generic-digital-pdf-v1",
                            document
                                .get("allowDesignedPageOverrides")
                                .and_then(Value::as_bool)
                                .unwrap_or(false),
                            tolerance,
                            &mut diagnostics,
                        )
                        .map_err(|diagnostic| {
                            Box::new(RenderResponse::failed("rejected", diagnostic))
                        })?;
                        if request.profile != "generic-digital-pdf-v1" && rendered.len() == 2 {
                            ensure_next_leaf(&mut pages, LeafSide::Verso);
                        }
                        let first_page = pages.len() + 1;
                        pages.extend(rendered);
                        assign_semantic_order_since(
                            &mut pages,
                            &semantic_snapshot,
                            semantic_order,
                            &semantic_id,
                            semantic_parent_id.as_deref(),
                            None,
                        );
                        if !block_id.is_empty() {
                            page_map.push(PageMapEntry {
                                chapter_id: string(chapter, "id"),
                                block_id,
                                page_number: first_page.max(chapter_start),
                            });
                        }
                        previous_space_after = 0.0;
                        continue;
                    }
                    if block_type.eq_ignore_ascii_case("Figure") {
                        let caption_style = block_style(document, &block, trim);
                        let caption_runs = block_runs(document, &block, &caption_style);
                        let caption_runs = if caption_runs.is_empty() {
                            single_run(&string(&block, "caption"), caption_style.face)
                        } else {
                            caption_runs
                        };
                        let presentation = block.get("presentation").unwrap_or(&Value::Null);
                        let placement = string(presentation, "placement");
                        let start_on_new_page = presentation
                            .get("startOnNewPage")
                            .and_then(Value::as_bool)
                            .unwrap_or(false);
                        let dedicated_page =
                            matches!(placement.as_str(), "DedicatedPage" | "FullBleed");
                        let width_percent =
                            if matches!(placement.as_str(), "FullWidth" | "FullBleed") {
                                100.0
                            } else {
                                presentation
                                    .get("widthPercent")
                                    .and_then(Value::as_f64)
                                    .unwrap_or(100.0) as f32
                            };
                        let alignment = match string(presentation, "alignment").as_str() {
                            "Start" => "left",
                            "End" => "right",
                            _ => "center",
                        };
                        let focal_x = presentation
                            .get("cropXPercent")
                            .and_then(Value::as_f64)
                            .unwrap_or(50.0) as f32;
                        let focal_y = presentation
                            .get("cropYPercent")
                            .and_then(Value::as_f64)
                            .unwrap_or(50.0) as f32;
                        let text_wrap = string(presentation, "textWrap");
                        let image_fit = layout_image_fit(&string(presentation, "fit"));
                        let spacing_before = presentation
                            .get("spacingBeforePoints")
                            .and_then(Value::as_f64)
                            .unwrap_or(6.0) as f32;
                        let spacing_after = presentation
                            .get("spacingAfterPoints")
                            .and_then(Value::as_f64)
                            .unwrap_or(6.0) as f32;
                        let caption_placement = string(presentation, "captionPlacement");
                        let caption_step = caption_style.size * caption_style.line_height.max(1.0);
                        let decorative = block
                            .get("decorative")
                            .and_then(Value::as_bool)
                            .unwrap_or(false);
                        let alt_text = block
                            .get("altText")
                            .and_then(Value::as_str)
                            .map(str::to_owned);
                        let language = block
                            .get("language")
                            .and_then(Value::as_str)
                            .map(str::to_owned);
                        let accessibility_role = block
                            .get("accessibilityRole")
                            .and_then(Value::as_str)
                            .map(str::to_owned);
                        let first_page;
                        if start_on_new_page && !dedicated_page {
                            pages.push(empty_body_page());
                        }
                        if dedicated_page {
                            let mut page = dedicated_figure_page_with_layout(
                                trim,
                                &string(&block, "caption"),
                                &caption_runs,
                                &caption_style,
                                string(&block, "assetId"),
                                width_percent,
                                focal_x,
                                focal_y,
                                alignment,
                            );
                            if let Some(image) = page.images.first_mut() {
                                image.alt_text.clone_from(&alt_text);
                                image.decorative = decorative;
                                image.language.clone_from(&language);
                                image.fit = image_fit;
                                image.accessibility_role.clone_from(&accessibility_role);
                                if placement == "FullBleed" {
                                    let bleed = trim.bleed_inches * 72.0;
                                    image.x = -bleed;
                                    image.y = -bleed;
                                    image.width = trim.width_inches * 72.0 + bleed * 2.0;
                                    image.height = trim.height_inches * 72.0 + bleed * 2.0;
                                }
                            }
                            if caption_placement == "Hidden" {
                                page.lines.clear();
                            } else if caption_placement == "Above" {
                                let top = trim.height_inches * 72.0
                                    - trim.margin_inches * 72.0
                                    - caption_style.space_before.max(0.0);
                                for (index, line) in page.lines.iter_mut().enumerate() {
                                    line.y = top - index as f32 * caption_step;
                                }
                            } else if caption_placement == "Overlay"
                                && let Some(image) = page.images.first()
                            {
                                let first_y = image.y
                                    + caption_style.size
                                    + page.lines.len().saturating_sub(1) as f32 * caption_step;
                                for (index, line) in page.lines.iter_mut().enumerate() {
                                    line.y = first_y - index as f32 * caption_step;
                                    line.light_text = true;
                                }
                            }
                            for line in &mut page.lines {
                                line.language.clone_from(&language);
                                line.artifact = false;
                                line.fill_rgb = if caption_placement == "Overlay" {
                                    Some(
                                        typography::defaults()
                                            .caption
                                            .overlay
                                            .as_ref()
                                            .expect("caption overlay defaults")
                                            .text_color_rgb,
                                    )
                                } else {
                                    typography::defaults().caption.text_color_rgb
                                };
                            }
                            if caption_placement == "Overlay" {
                                add_overlay_caption_background(
                                    &mut page,
                                    &block_id,
                                    &caption_style,
                                );
                            }
                            pages.push(page);
                            first_page = pages.len();
                        } else {
                            append_inline_illustration(
                                &mut pages,
                                trim,
                                &block_id,
                                &string(&block, "caption"),
                                &caption_runs,
                                &caption_style,
                                string(&block, "assetId"),
                                width_percent,
                                focal_x,
                                focal_y,
                                alignment,
                                &text_wrap,
                                image_fit,
                                spacing_before,
                                spacing_after,
                                &caption_placement,
                                presentation
                                    .get("keepWithCaption")
                                    .and_then(Value::as_bool)
                                    .unwrap_or(true),
                            );
                            if let Some(image) =
                                pages.last_mut().and_then(|page| page.images.last_mut())
                            {
                                image.alt_text.clone_from(&alt_text);
                                image.decorative = decorative;
                                image.language.clone_from(&language);
                                image.accessibility_role.clone_from(&accessibility_role);
                            }
                            if let Some(page) = pages.last_mut() {
                                for line in page.lines.iter_mut().filter(|line| {
                                    line.semantic_role == LayoutSemanticRole::Caption
                                }) {
                                    line.language.clone_from(&language);
                                    line.artifact = false;
                                }
                            }
                            first_page = pages.len();
                        }
                        if !block_id.is_empty() {
                            page_map.push(PageMapEntry {
                                chapter_id: string(chapter, "id"),
                                block_id,
                                page_number: first_page.max(chapter_start),
                            });
                        }
                        assign_semantic_order_since(
                            &mut pages,
                            &semantic_snapshot,
                            semantic_order,
                            &semantic_id,
                            semantic_parent_id.as_deref(),
                            browser_preview
                                .then(|| string(&block, "caption"))
                                .as_deref(),
                        );
                        previous_space_after = spacing_after;
                        continue;
                    }
                    let style = block_style(document, &block, trim);
                    if style.page_break_before
                        && pages.last().is_some_and(|page| {
                            page.kind == PageKind::Body && !page.lines.is_empty()
                        })
                    {
                        // page break reset collapsed gap
                        previous_space_after = 0.0;
                    }
                    let gap_before = if text.trim().is_empty()
                        && !block_type.eq_ignore_ascii_case("SceneBreak")
                    {
                        0.0
                    } else {
                        previous_space_after
                    };
                    let first_page = if text.trim().is_empty()
                        && !block_type.eq_ignore_ascii_case("SceneBreak")
                    {
                        pages.len().max(1)
                    } else {
                        let language = block.get("language").and_then(Value::as_str);
                        if block_type.eq_ignore_ascii_case("ListItem") {
                            let content_runs = block_runs(document, &block, &style);
                            append_list_item_with_gap(
                                &mut pages,
                                &text,
                                &content_runs,
                                trim,
                                &style,
                                language,
                                gap_before,
                            )
                        } else {
                            let runs = display_block_runs(document, &block, &style, &text);
                            append_styled_runs_with_gap(
                                &mut pages, &text, &runs, trim, &style, language, gap_before,
                            )
                        }
                    };
                    if block_type.eq_ignore_ascii_case("blockquote") || is_blockquote_role(&block) {
                        let defaults = &typography::defaults().blockquote;
                        set_line_color_since(
                            &mut pages,
                            &semantic_snapshot,
                            defaults.text_color_rgb,
                        );
                        append_blockquote_decorations(
                            &mut pages,
                            &semantic_snapshot,
                            trim,
                            &style,
                            &semantic_id,
                        );
                    }
                    previous_space_after = style.space_after;
                    assign_semantic_order_since(
                        &mut pages,
                        &semantic_snapshot,
                        semantic_order,
                        &semantic_id,
                        semantic_parent_id.as_deref(),
                        browser_preview.then(|| block_text(&block)).as_deref(),
                    );
                    if !block_id.is_empty() {
                        page_map.push(PageMapEntry {
                            chapter_id: string(chapter, "id"),
                            block_id,
                            page_number: first_page.max(chapter_start),
                        });
                    }
                }
                if let Some(page) = pages.get_mut(chapter_page_index) {
                    page.bookmark = (!chapter_title.is_empty()).then_some(chapter_title.clone());
                }
                add_running_heads(&mut pages[chapter_page_index..], &chapter_title, trim);
                append_publication_sections(
                    &mut pages,
                    document,
                    "AfterChapter",
                    Some(&string(chapter, "id")),
                    trim,
                    request.profile == "generic-digital-pdf-v1",
                    tolerance,
                    &mut diagnostics,
                    &mut page_map,
                    &mut semantic_order,
                )?;
            }
            append_publication_sections(
                &mut pages,
                document,
                "AfterAct",
                Some(&string(section, "id")),
                trim,
                request.profile == "generic-digital-pdf-v1",
                tolerance,
                &mut diagnostics,
                &mut page_map,
                &mut semantic_order,
            )?;
        }
    }
    append_publication_sections(
        &mut pages,
        document,
        "Back",
        None,
        trim,
        request.profile == "generic-digital-pdf-v1",
        tolerance,
        &mut diagnostics,
        &mut page_map,
        &mut semantic_order,
    )?;
    let mut toc_converged = toc_index.is_none();
    if let Some(index) = toc_index {
        let (replacements, converged) =
            build_toc_pages(&chapter_entries, body_start_page.unwrap_or(1), trim)
                .map_err(|diagnostic| Box::new(RenderResponse::failed("rejected", diagnostic)))?;
        toc_converged = converged;
        let toc_page_count = replacements.len();
        let delta = toc_page_count - 1;
        pages.splice(index..=index, replacements);
        for page in &mut pages[index..index + toc_page_count] {
            for line in &mut page.lines {
                if let Some(target) = &mut line.link_page {
                    *target += delta;
                }
            }
        }
        for entry in &mut page_map {
            entry.page_number += delta;
        }
        if let Some(page) = &mut body_start_page {
            *page += delta;
        }
    }
    if request.cover.is_some() {
        features.insert("dedicated-cover".to_owned());
    }
    for role in pages
        .iter()
        .flat_map(|page| &page.images)
        .filter_map(|image| image.accessibility_role.as_deref())
        .filter(|role| !role.is_empty())
    {
        features.insert(format!("accessibility-role-{}", role.to_ascii_lowercase()));
    }
    normalize_paint_orders(&mut pages);
    assign_page_labels(&mut pages, body_start_page.unwrap_or(1));
    Ok(LayoutDocument {
        pages,
        page_map,
        features: features.into_iter().collect(),
        diagnostics,
        toc_converged,
    })
}

#[allow(clippy::too_many_arguments)]
fn append_inline_illustration(
    pages: &mut Vec<LayoutPage>,
    trim: &crate::model::Trim,
    semantic_id: &str,
    caption: &str,
    caption_runs: &[LayoutRun],
    caption_style: &BlockStyle,
    asset_id: String,
    width_percent: f32,
    crop_x_percent: f32,
    crop_y_percent: f32,
    alignment: &str,
    text_wrap: &str,
    fit: LayoutImageFit,
    spacing_before: f32,
    spacing_after: f32,
    caption_placement: &str,
    keep_with_caption: bool,
) {
    let margin = trim.margin_inches * 72.0;
    let page_height = trim.height_inches * 72.0;
    let available_width = trim.width_inches * 72.0 - margin * 2.0;
    let available_height = page_height - margin * 2.0;
    let image_width = available_width * (width_percent / 100.0).clamp(0.1, 1.0);
    let image_height = (available_height * 0.34).min(image_width * 1.25);
    let image_x = match alignment.to_ascii_lowercase().as_str() {
        "left" => margin,
        "right" => margin + available_width - image_width,
        _ => margin + (available_width - image_width) / 2.0,
    };
    let line_step = trim.body_font_size_points * trim.body_line_height;
    let caption_size = caption_style.size;
    let caption_line_height = caption_style.line_height.max(1.0);
    let caption_space_before = caption_style.space_before.max(0.0);
    let caption_space_after = caption_style.space_after.max(0.0);
    let overlay = typography::defaults()
        .caption
        .overlay
        .as_ref()
        .expect("caption overlay defaults");
    let overlay_padding_vertical = overlay.padding_vertical_em * caption_size;
    let overlay_padding_horizontal = overlay.padding_horizontal_em * caption_size;
    let overlay_horizontal = if caption_placement == "Overlay" {
        overlay_padding_horizontal
    } else {
        0.0
    };
    let (caption_origin_x, caption_width) =
        caption_content_geometry(image_x, image_width, caption_style, overlay_horizontal);
    let caption_lines = if caption_placement == "Hidden" {
        Vec::new()
    } else {
        wrapped_caption_with_runs(caption, caption_runs, caption_style, caption_width)
    };
    let caption_step = caption_size * caption_line_height;
    let caption_extent = if caption_lines.is_empty() || caption_placement == "Overlay" {
        0.0
    } else {
        caption_space_before
            + caption_size * 1.12
            + (caption_lines.len() - 1) as f32 * caption_step
            + caption_space_after
    };
    let required_height = image_height
        + if keep_with_caption {
            caption_extent
        } else {
            0.0
        }
        + spacing_before.max(0.0)
        + spacing_after.max(0.0);
    let remaining_height = pages.last().map_or(0.0, |page| {
        if page.kind != PageKind::Body {
            return 0.0;
        }
        page.lines
            .last()
            .map_or(available_height, |line| line.y - line.size * 1.6 - margin)
    });
    if remaining_height < required_height {
        pages.push(empty_body_page());
    }
    if pages.last().is_none_or(|page| page.kind != PageKind::Body) {
        pages.push(empty_body_page());
    }
    let page = pages.last_mut().expect("body page");
    let content_top = page
        .lines
        .iter()
        .rev()
        .find(|line| line.semantic_role != LayoutSemanticRole::Caption)
        .map_or(page_height - margin, |line| line.y - line.size * 1.6);
    let content_top = content_top - spacing_before.max(0.0);
    let image_top = if caption_placement == "Above" {
        content_top - caption_extent
    } else {
        content_top
    };
    let image_y = image_top - image_height;
    page.images.push(LayoutImage {
        asset_id,
        x: image_x,
        y: image_y,
        width: image_width,
        height: image_height,
        focal_x: (crop_x_percent / 100.0).clamp(0.0, 1.0),
        focal_y: (crop_y_percent / 100.0).clamp(0.0, 1.0),
        source_left_fraction: 0.0,
        source_width_fraction: 1.0,
        rotation_degrees: 0.0,
        opacity: 1.0,
        fit,
        alt_text: None,
        decorative: true,
        language: None,
        reading_order: None,
        semantic_id: None,
        semantic_parent_id: None,
        text_wrap: (!text_wrap.is_empty() && text_wrap != "None").then(|| text_wrap.to_owned()),
        accessibility_role: None,
    });

    let first_caption_y = match caption_placement {
        "Above" => content_top - caption_space_before - caption_size * 0.82,
        "Overlay" => {
            image_y + caption_size + caption_lines.len().saturating_sub(1) as f32 * caption_step
        }
        _ => image_y - caption_space_before - caption_size * 0.82,
    };
    let caption_bottom = if caption_lines.is_empty() {
        image_y
    } else {
        first_caption_y
            - caption_lines.len().saturating_sub(1) as f32 * caption_step
            - caption_size * (caption_line_height - 0.82)
    };
    if caption_placement == "Overlay" && !caption_lines.is_empty() {
        let top = first_caption_y + caption_size * 0.82 + overlay_padding_vertical;
        let bottom = caption_bottom - overlay_padding_vertical;
        let shape_index = page.shapes.len();
        page.shapes.push(LayoutShape {
            kind: LayoutShapeKind::Rectangle,
            x: image_x,
            y: bottom,
            width: image_width,
            height: (top - bottom).max(0.0),
            fill_rgb: Some(overlay.background_color_rgb),
            stroke_rgb: None,
            stroke_width: 0.0,
            opacity: overlay.background_opacity,
            rotation_degrees: 0.0,
            semantic_id: Some(format!("{semantic_id}:caption-background")),
            semantic_parent_id: Some(semantic_id.to_owned()),
        });
        page.paint_order.push(LayoutPaint::Shape(shape_index));
    }
    if text_wrap.is_empty() || text_wrap == "None" {
        let flow_bottom = if caption_placement == "Below" {
            caption_bottom - caption_space_after
        } else {
            image_y
        } - spacing_after.max(0.0);
        let mut spacer_y = content_top;
        while spacer_y > flow_bottom {
            page.lines.push(LayoutLine {
                text: String::new(),
                runs: Vec::new(),
                size: trim.body_font_size_points,
                x: margin,
                y: spacer_y,
                baseline_offset_points: line_baseline_offset_points(
                    trim.body_font_size_points,
                    BlockStyle::body(trim).face,
                    &[],
                ),
                word_spacing: 0.0,
                character_spacing: 0.0,
                rotation_degrees: 0.0,
                rotation_origin_x: None,
                rotation_origin_y: None,
                opacity: 1.0,
                light_text: false,
                fill_rgb: None,
                semantic_role: LayoutSemanticRole::Paragraph,
                artifact: true,
                language: None,
                reading_order: None,
                semantic_id: None,
                semantic_parent_id: None,
                source_start_utf16: None,
                source_end_utf16: None,
                link_page: None,
            });
            spacer_y -= line_step;
        }
    }
    let detach_caption = !keep_with_caption
        && caption_placement == "Below"
        && !caption_lines.is_empty()
        && caption_bottom < margin;
    if !detach_caption {
        for (index, (text, runs)) in caption_lines.iter().cloned().enumerate() {
            let baseline_offset_points =
                line_baseline_offset_points(caption_size, caption_style.face, &runs);
            let first_line_indent = if index == 0 {
                caption_style.first_line_indent
            } else {
                0.0
            };
            let caption_x = aligned_caption_x(
                caption_origin_x + first_line_indent,
                (caption_width - first_line_indent.max(0.0)).max(0.0),
                caption_style,
                &runs,
            );
            page.lines.push(LayoutLine {
                text,
                runs,
                size: caption_size,
                x: caption_x,
                y: first_caption_y - index as f32 * caption_step,
                baseline_offset_points,
                word_spacing: 0.0,
                character_spacing: 0.0,
                rotation_degrees: 0.0,
                rotation_origin_x: None,
                rotation_origin_y: None,
                opacity: 1.0,
                light_text: caption_placement == "Overlay",
                fill_rgb: if caption_placement == "Overlay" {
                    Some(overlay.text_color_rgb)
                } else {
                    typography::defaults().caption.text_color_rgb
                },
                semantic_role: LayoutSemanticRole::Caption,
                artifact: true,
                language: None,
                reading_order: None,
                semantic_id: None,
                semantic_parent_id: None,
                source_start_utf16: None,
                source_end_utf16: None,
                link_page: None,
            });
        }
    }
    if detach_caption {
        pages.push(empty_body_page());
        let caption_page = pages.last_mut().expect("caption page");
        let first_y = page_height - margin - caption_size * 0.82;
        for (index, (text, runs)) in caption_lines.into_iter().enumerate() {
            let baseline_offset_points =
                line_baseline_offset_points(caption_size, caption_style.face, &runs);
            let (caption_origin_x, caption_width) =
                caption_content_geometry(margin, available_width, caption_style, 0.0);
            let first_line_indent = if index == 0 {
                caption_style.first_line_indent
            } else {
                0.0
            };
            let caption_x = aligned_caption_x(
                caption_origin_x + first_line_indent,
                (caption_width - first_line_indent.max(0.0)).max(0.0),
                caption_style,
                &runs,
            );
            caption_page.lines.push(LayoutLine {
                text,
                runs,
                size: caption_size,
                x: caption_x,
                y: first_y - index as f32 * caption_step,
                baseline_offset_points,
                word_spacing: 0.0,
                character_spacing: 0.0,
                rotation_degrees: 0.0,
                rotation_origin_x: None,
                rotation_origin_y: None,
                opacity: 1.0,
                light_text: false,
                fill_rgb: typography::defaults().caption.text_color_rgb,
                semantic_role: LayoutSemanticRole::Caption,
                artifact: true,
                language: None,
                reading_order: None,
                semantic_id: None,
                semantic_parent_id: None,
                source_start_utf16: None,
                source_end_utf16: None,
                link_page: None,
            });
        }
    }
}

fn add_overlay_caption_background(
    page: &mut LayoutPage,
    semantic_id: &str,
    caption_style: &BlockStyle,
) {
    let Some(image) = page.images.first() else {
        return;
    };
    let caption_lines = page
        .lines
        .iter()
        .filter(|line| line.semantic_role == LayoutSemanticRole::Caption && !line.artifact)
        .collect::<Vec<_>>();
    if caption_lines.is_empty() {
        return;
    }
    let overlay = typography::defaults()
        .caption
        .overlay
        .as_ref()
        .expect("caption overlay defaults");
    let padding_vertical = overlay.padding_vertical_em * caption_style.size;
    let top = caption_lines
        .iter()
        .map(|line| line.y + line.size * 0.82)
        .fold(f32::NEG_INFINITY, f32::max)
        + padding_vertical;
    let bottom = caption_lines
        .iter()
        .map(|line| line.y - line.size * (caption_style.line_height.max(1.0) - 0.82))
        .fold(f32::INFINITY, f32::min)
        - padding_vertical;
    let shape_index = page.shapes.len();
    page.shapes.push(LayoutShape {
        kind: LayoutShapeKind::Rectangle,
        x: image.x,
        y: bottom,
        width: image.width,
        height: (top - bottom).max(0.0),
        fill_rgb: Some(overlay.background_color_rgb),
        stroke_rgb: None,
        stroke_width: 0.0,
        opacity: overlay.background_opacity,
        rotation_degrees: 0.0,
        semantic_id: Some(format!("{semantic_id}:caption-background")),
        semantic_parent_id: Some(semantic_id.to_owned()),
    });
    page.paint_order.push(LayoutPaint::Shape(shape_index));
}

fn assign_semantic_order_since(
    pages: &mut [LayoutPage],
    snapshot: &[(usize, usize)],
    block_order: i32,
    semantic_id: &str,
    semantic_parent_id: Option<&str>,
    source_text: Option<&str>,
) {
    let mut source_cursor = 0usize;
    for (page_index, page) in pages.iter_mut().enumerate() {
        let (line_start, image_start) = snapshot.get(page_index).copied().unwrap_or_default();
        let has_new_image = image_start < page.images.len();
        for line in page.lines.iter_mut().skip(line_start) {
            let local_order = line.reading_order.unwrap_or_default().clamp(0, 999);
            line.reading_order = Some(block_order * 1_000 + local_order);
            if line.semantic_id.is_none() {
                if line.semantic_role == LayoutSemanticRole::Caption && has_new_image {
                    line.semantic_id = Some(format!("{semantic_id}:caption"));
                    line.semantic_parent_id = Some(semantic_id.to_owned());
                } else {
                    line.semantic_id = Some(semantic_id.to_owned());
                    line.semantic_parent_id = semantic_parent_id.map(str::to_owned);
                }
            }
            if !line.artifact
                && let Some(source) = source_text
                && let Some((start, end)) =
                    source_utf16_range(source, &mut source_cursor, &line.text)
            {
                line.source_start_utf16 = Some(start);
                line.source_end_utf16 = Some(end);
            }
        }
        for image in page.images.iter_mut().skip(image_start) {
            let local_order = image.reading_order.unwrap_or_default().clamp(0, 999);
            image.reading_order = Some(block_order * 1_000 + local_order);
            image
                .semantic_id
                .get_or_insert_with(|| semantic_id.to_owned());
            if image.semantic_parent_id.is_none() {
                image.semantic_parent_id = semantic_parent_id.map(str::to_owned);
            }
        }
    }
}

fn source_utf16_range(
    source: &str,
    cursor: &mut usize,
    rendered_line: &str,
) -> Option<(usize, usize)> {
    let searchable = rendered_line.strip_suffix('-').unwrap_or(rendered_line);
    if searchable.is_empty() {
        let offset = source.get(..*cursor)?.encode_utf16().count();
        return Some((offset, offset));
    }
    let relative = source.get(*cursor..)?.find(searchable)?;
    let start_byte = *cursor + relative;
    let end_byte = start_byte + searchable.len();
    *cursor = end_byte;
    Some((
        source.get(..start_byte)?.encode_utf16().count(),
        source.get(..end_byte)?.encode_utf16().count(),
    ))
}

fn first_changed_page(pages: &[LayoutPage], snapshot: &[(usize, usize)]) -> usize {
    pages
        .iter()
        .enumerate()
        .find(|(index, page)| {
            let (line_count, image_count) = snapshot.get(*index).copied().unwrap_or_default();
            page.lines.len() > line_count || page.images.len() > image_count
        })
        .map_or_else(|| pages.len().max(1), |(index, _)| index + 1)
}

fn designed_page(
    composition: &Value,
    document: &Value,
    trim: &crate::model::Trim,
    tolerance: LayoutTolerance,
    diagnostics: &mut Vec<Diagnostic>,
) -> Result<LayoutPage, Diagnostic> {
    let variant = composition
        .get("variants")
        .and_then(Value::as_array)
        .and_then(|variants| variants.first())
        .ok_or_else(|| {
            Diagnostic::error(
                "PRESS_COMPOSITION_VARIANT_MISSING",
                format!(
                    "Designed Page '{}' has no geometry variant.",
                    string(composition, "name")
                ),
            )
        })?;
    let scene = variant.get("scene").unwrap_or(&Value::Null);
    let surface = scene.get("surface").unwrap_or(&Value::Null);
    let scene_width = surface
        .get("widthPoints")
        .and_then(Value::as_f64)
        .unwrap_or(trim.width_inches as f64 * 72.0) as f32;
    let scene_height = surface
        .get("heightPoints")
        .and_then(Value::as_f64)
        .unwrap_or(trim.height_inches as f64 * 72.0) as f32;
    if !(72.0..=2_880.0).contains(&scene_width) || !(72.0..=2_880.0).contains(&scene_height) {
        return Err(Diagnostic::error(
            "PRESS_COMPOSITION_GEOMETRY_INVALID",
            "Designed Page geometry must be between 1 and 40 inches per side.",
        ));
    }
    let mut page = LayoutPage {
        kind: PageKind::Designed,
        width_points: Some(scene_width),
        height_points: Some(scene_height),
        lines: Vec::new(),
        images: Vec::new(),
        shapes: Vec::new(),
        paint_order: Vec::new(),
        barcode_modules: None,
        page_label: None,
        bookmark: None,
    };
    let semantic_blocks = composition
        .get("semanticBlocks")
        .and_then(Value::as_array)
        .cloned()
        .unwrap_or_default();
    let flattened_objects = flatten_composition_objects(scene)?;
    let visible_layers = scene
        .get("layers")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter(|layer| {
            layer
                .get("visible")
                .and_then(Value::as_bool)
                .unwrap_or(true)
        })
        .map(|layer| string(layer, "id"))
        .collect::<std::collections::HashSet<_>>();
    let content_references = flattened_objects
        .iter()
        .filter(|item| {
            string(item, "kind") == "Text"
                && item.get("visible").and_then(Value::as_bool).unwrap_or(true)
                && visible_layers.contains(&string(item, "layerId"))
        })
        .flat_map(|item| {
            item.get("contentReferences")
                .and_then(Value::as_array)
                .into_iter()
                .flatten()
        })
        .cloned()
        .collect::<Vec<_>>();
    validate_semantic_coverage(&semantic_blocks, &content_references)?;
    let layer_order = scene
        .get("layers")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .map(|layer| {
            (
                string(layer, "id"),
                layer
                    .get("order")
                    .and_then(Value::as_i64)
                    .unwrap_or_default(),
            )
        })
        .collect::<std::collections::HashMap<_, _>>();
    let mut objects = flattened_objects;
    objects.sort_by_key(|item| {
        (
            layer_order
                .get(&string(item, "layerId"))
                .copied()
                .unwrap_or_default(),
            item.get("zIndex")
                .and_then(Value::as_i64)
                .unwrap_or_default(),
            string(item, "id"),
        )
    });
    for mut item in objects {
        if !item.get("visible").and_then(Value::as_bool).unwrap_or(true)
            || !visible_layers.contains(&string(&item, "layerId"))
        {
            continue;
        }
        let bounds = item.get("bounds").unwrap_or(&Value::Null);
        let x = bounds
            .get("xPercent")
            .and_then(Value::as_f64)
            .unwrap_or_default() as f32
            / 100.0;
        let y = bounds
            .get("yPercent")
            .and_then(Value::as_f64)
            .unwrap_or_default() as f32
            / 100.0;
        let width = bounds
            .get("widthPercent")
            .and_then(Value::as_f64)
            .unwrap_or(100.0) as f32
            / 100.0;
        let height = bounds
            .get("heightPercent")
            .and_then(Value::as_f64)
            .unwrap_or(100.0) as f32
            / 100.0;
        let kind = string(&item, "kind");
        if !x.is_finite()
            || !y.is_finite()
            || !width.is_finite()
            || !height.is_finite()
            || width <= 0.0
            || height <= 0.0
            || width > 4.0
            || height > 4.0
            || !(-4.0..=4.0).contains(&x)
            || !(-4.0..=4.0).contains(&y)
        {
            return Err(Diagnostic::error(
                "PRESS_COMPOSITION_BOUNDS_INVALID",
                format!(
                    "Composition object '{}' lies outside its surface.",
                    string(&item, "id")
                ),
            ));
        }
        if x < 0.0 || y < 0.0 || x + width > 1.0001 || y + height > 1.0001 {
            diagnostics.push(Diagnostic::warning(
                "PRESS_COMPOSITION_OBJECT_CLIPPED",
                format!(
                    "Composition object '{}' extends beyond its surface and will be clipped to the page.",
                    string(&item, "id")
                ),
            ));
        }
        match kind.as_str() {
            "Image" => {
                let accessibility_decision_pending = item
                    .get("accessibilityDecisionPending")
                    .and_then(Value::as_bool)
                    .unwrap_or(false);
                if accessibility_decision_pending {
                    let message = format!(
                        "Image object '{}' requires alternative text or an explicit decorative decision before publishing.",
                        string(&item, "id")
                    );
                    if tolerance.allow_pending_accessibility {
                        diagnostics
                            .push(Diagnostic::warning("PRESS_ALT_DECISION_REQUIRED", message));
                    } else {
                        return Err(Diagnostic::error("PRESS_ALT_DECISION_REQUIRED", message));
                    }
                }
                let asset_id = string(&item, "imageId");
                if !asset_id.is_empty() {
                    let image_index = page.images.len();
                    page.images.push(LayoutImage {
                        asset_id,
                        x: x * scene_width,
                        y: scene_height - (y + height) * scene_height,
                        width: width * scene_width,
                        height: height * scene_height,
                        focal_x: item
                            .get("cropXPercent")
                            .and_then(Value::as_f64)
                            .unwrap_or(50.0) as f32
                            / 100.0,
                        focal_y: item
                            .get("cropYPercent")
                            .and_then(Value::as_f64)
                            .unwrap_or(50.0) as f32
                            / 100.0,
                        source_left_fraction: 0.0,
                        source_width_fraction: 1.0,
                        rotation_degrees: item
                            .get("rotationDegrees")
                            .and_then(Value::as_f64)
                            .unwrap_or_default() as f32,
                        opacity: item.get("opacity").and_then(Value::as_f64).unwrap_or(1.0) as f32,
                        fit: layout_image_fit(&string(&item, "imageFit")),
                        alt_text: item
                            .get("altText")
                            .and_then(Value::as_str)
                            .map(str::to_owned),
                        // A private reading copy can render before the author makes the
                        // accessibility decision. Keep that unresolved image out of the
                        // tagged reading order and retain the warning for the user.
                        decorative: item
                            .get("decorative")
                            .and_then(Value::as_bool)
                            .unwrap_or(false)
                            || (tolerance.allow_pending_accessibility
                                && accessibility_decision_pending),
                        language: item
                            .get("language")
                            .and_then(Value::as_str)
                            .map(str::to_owned),
                        reading_order: item
                            .get("readingOrder")
                            .and_then(Value::as_i64)
                            .map(|value| value as i32),
                        semantic_id: Some(string(&item, "id")),
                        semantic_parent_id: None,
                        text_wrap: None,
                        accessibility_role: item
                            .get("accessibilityRole")
                            .and_then(Value::as_str)
                            .map(str::to_owned),
                    });
                    page.paint_order.push(LayoutPaint::Image(image_index));
                }
            }
            "Text" => {
                let size = scene_style_value(scene, &item, "fontSizePoints")
                    .and_then(Value::as_f64)
                    .unwrap_or(12.0) as f32;
                let font_family_key = scene_style_value(scene, &item, "fontFamilyKey")
                    .and_then(Value::as_str)
                    .unwrap_or("builtin:nunito");
                let font_weight = scene_style_value(scene, &item, "fontWeight")
                    .and_then(Value::as_u64)
                    .unwrap_or(400)
                    .clamp(100, 900) as u16;
                let italic = scene_style_value(scene, &item, "italic")
                    .and_then(Value::as_bool)
                    .unwrap_or(false);
                let face =
                    regular_face(font_family(font_family_key)).with_weight(font_weight, italic);
                let mut text = string(&item, "textBinding");
                let source_runs = if text.is_empty() {
                    let style = BlockStyle {
                        size,
                        line_height: 1.0,
                        indent: 0.0,
                        right_indent: 0.0,
                        first_line_indent: 0.0,
                        page_break_before: false,
                        keep_with_next: false,
                        alignment: "left".to_owned(),
                        face,
                        font_weight,
                        small_caps: false,
                        space_before: 0.0,
                        space_after: 0.0,
                        semantic_role: LayoutSemanticRole::Paragraph,
                    };
                    let resolved = item
                        .get("contentReferences")
                        .and_then(Value::as_array)
                        .into_iter()
                        .flatten()
                        .map(|reference| {
                            resolve_content_reference_runs(
                                document,
                                &semantic_blocks,
                                reference,
                                &style,
                            )
                        })
                        .collect::<Result<Vec<_>, _>>()?;
                    let references = item
                        .get("contentReferences")
                        .and_then(Value::as_array)
                        .cloned()
                        .unwrap_or_default();
                    let separators = references
                        .windows(2)
                        .map(|pair| {
                            if string(&pair[0], "blockId") == string(&pair[1], "blockId") {
                                ""
                            } else {
                                "\n"
                            }
                        })
                        .collect::<Vec<_>>();
                    text = resolved.iter().enumerate().fold(
                        String::new(),
                        |mut output, (index, (value, _))| {
                            if index > 0 {
                                output.push_str(separators[index - 1]);
                            }
                            output.push_str(value);
                            output
                        },
                    );
                    let mut runs = Vec::new();
                    for (index, (_, reference_runs)) in resolved.into_iter().enumerate() {
                        if index > 0 {
                            runs.push(LayoutRun {
                                text: separators[index - 1].to_owned(),
                                face,
                                underline: false,
                                strikethrough: false,
                                baseline_shift_em: 0.0,
                                size_scale: 1.0,
                                language: None,
                            });
                        }
                        runs.extend(reference_runs);
                    }
                    runs
                } else {
                    single_run(&text, face)
                };
                let has_content_references = item
                    .get("contentReferences")
                    .and_then(Value::as_array)
                    .is_some_and(|references| !references.is_empty());
                if text.trim().is_empty() {
                    if !has_content_references {
                        return Err(Diagnostic::error(
                            "PRESS_COMPOSITION_TEXT_UNBOUND",
                            format!(
                                "Text frame '{}' is not bound to semantic content.",
                                string(&item, "id")
                            ),
                        ));
                    }
                    item["visible"] = Value::Bool(false);
                    continue;
                }
                let wrapped = wrap_layout_runs(&text, &source_runs, size, width * scene_width);
                let line_height = size
                    * scene_style_value(scene, &item, "lineHeight")
                        .and_then(Value::as_f64)
                        .unwrap_or(1.2) as f32;
                let character_spacing = scene_style_value(scene, &item, "letterSpacingEm")
                    .and_then(Value::as_f64)
                    .unwrap_or_default() as f32
                    * size;
                let fill_rgb = scene_style_value(scene, &item, "fillColor")
                    .and_then(Value::as_str)
                    .and_then(|value| parse_hex_color(Some(value)));
                let light_text = fill_rgb.is_some_and(|[red, green, blue]| {
                    red * 0.2126 + green * 0.7152 + blue * 0.0722 >= 0.6
                });
                let text_height = wrapped.len() as f32 * line_height;
                let text_overflows_vertically = text_height > height * scene_height + 0.01;
                let mut overflow_reported = false;
                if text_overflows_vertically {
                    let message =
                        format!("Text frame '{}' overflows its bounds.", string(&item, "id"));
                    if tolerance.clip_composition_text_overflow {
                        diagnostics.push(Diagnostic::warning(
                            "PRESS_COMPOSITION_TEXT_OVERFLOW",
                            format!("{message} Excess text is hidden in this authoring preview."),
                        ));
                        overflow_reported = true;
                    } else {
                        return Err(Diagnostic::error(
                            "PRESS_COMPOSITION_TEXT_OVERFLOW",
                            message,
                        ));
                    }
                }
                let object_opacity =
                    item.get("opacity").and_then(Value::as_f64).unwrap_or(1.0) as f32;
                if let Some(background_rgb) = scene_style_value(scene, &item, "backgroundColor")
                    .and_then(Value::as_str)
                    .and_then(|value| parse_hex_color(Some(value)))
                {
                    let shape_index = page.shapes.len();
                    page.shapes.push(LayoutShape {
                        kind: LayoutShapeKind::Rectangle,
                        x: x * scene_width,
                        y: scene_height - (y + height) * scene_height,
                        width: width * scene_width,
                        height: height * scene_height,
                        fill_rgb: Some(background_rgb),
                        stroke_rgb: None,
                        stroke_width: 0.0,
                        opacity: object_opacity
                            * scene_style_value(scene, &item, "backgroundOpacity")
                                .and_then(Value::as_f64)
                                .unwrap_or(1.0) as f32,
                        rotation_degrees: item
                            .get("rotationDegrees")
                            .and_then(Value::as_f64)
                            .unwrap_or_default() as f32,
                        semantic_id: None,
                        semantic_parent_id: None,
                    });
                    page.paint_order.push(LayoutPaint::Shape(shape_index));
                }
                let stroke_width = scene_style_value(scene, &item, "strokeWidthPoints")
                    .and_then(Value::as_f64)
                    .unwrap_or_default() as f32;
                let stroke_rgb = scene_style_value(scene, &item, "strokeColor")
                    .and_then(Value::as_str)
                    .and_then(|value| parse_hex_color(Some(value)));
                if stroke_width > 0.0 && stroke_rgb.is_some() {
                    let shape_index = page.shapes.len();
                    page.shapes.push(LayoutShape {
                        kind: LayoutShapeKind::Rectangle,
                        x: x * scene_width,
                        y: scene_height - (y + height) * scene_height,
                        width: width * scene_width,
                        height: height * scene_height,
                        fill_rgb: None,
                        stroke_rgb,
                        stroke_width,
                        opacity: object_opacity,
                        rotation_degrees: item
                            .get("rotationDegrees")
                            .and_then(Value::as_f64)
                            .unwrap_or_default() as f32,
                        semantic_id: None,
                        semantic_parent_id: None,
                    });
                    page.paint_order.push(LayoutPaint::Shape(shape_index));
                }
                let vertical_offset = if text_overflows_vertically {
                    0.0
                } else {
                    match scene_style_value(scene, &item, "verticalAlignment")
                        .and_then(Value::as_str)
                        .unwrap_or("Top")
                    {
                        "Center" => (height * scene_height - text_height) / 2.0,
                        "Bottom" => height * scene_height - text_height,
                        _ => 0.0,
                    }
                };
                let frame_bottom = scene_height - (y + height) * scene_height;
                for (line_index, (mut line_text, mut runs)) in wrapped.into_iter().enumerate() {
                    let line_y = scene_height
                        - y * scene_height
                        - vertical_offset
                        - size
                        - line_index as f32 * line_height;
                    if tolerance.clip_composition_text_overflow
                        && line_y - size * 0.3 < frame_bottom - 0.01
                    {
                        continue;
                    }
                    let mut measured_width = measured_run_width(&runs, size)
                        + character_spacing * line_text.chars().count().saturating_sub(1) as f32;
                    if measured_width > width * scene_width + 0.01 {
                        let message = format!(
                            "Text frame '{}' overflows its bounds after letter spacing.",
                            string(&item, "id")
                        );
                        if tolerance.clip_composition_text_overflow {
                            if !overflow_reported {
                                diagnostics.push(Diagnostic::warning(
                                    "PRESS_COMPOSITION_TEXT_OVERFLOW",
                                    format!(
                                        "{message} Excess text is hidden in this authoring preview."
                                    ),
                                ));
                                overflow_reported = true;
                            }
                            (line_text, runs) = clip_layout_runs_to_width(
                                &runs,
                                size,
                                character_spacing,
                                width * scene_width,
                            );
                            if line_text.is_empty() {
                                continue;
                            }
                            measured_width = measured_run_width(&runs, size)
                                + character_spacing
                                    * line_text.chars().count().saturating_sub(1) as f32;
                        } else {
                            return Err(Diagnostic::error(
                                "PRESS_COMPOSITION_TEXT_OVERFLOW",
                                message,
                            ));
                        }
                    }
                    let line_x = x * scene_width
                        + match scene_style_value(scene, &item, "textAlignment")
                            .and_then(Value::as_str)
                            .unwrap_or("Start")
                        {
                            "Center" => (width * scene_width - measured_width) / 2.0,
                            "End" => width * scene_width - measured_width,
                            _ => 0.0,
                        };
                    let shadow = scene_style_value(scene, &item, "textShadow")
                        .and_then(Value::as_str)
                        .unwrap_or("None");
                    let baseline_offset_points = line_baseline_offset_points(size, face, &runs);
                    if shadow != "None" {
                        let shadow_index = page.lines.len();
                        let shadow_offset = if shadow == "Strong" { 2.0 } else { 1.0 };
                        page.lines.push(LayoutLine {
                            text: line_text.clone(),
                            runs: runs.clone(),
                            size,
                            x: line_x + shadow_offset,
                            y: line_y - shadow_offset,
                            baseline_offset_points,
                            word_spacing: 0.0,
                            character_spacing,
                            rotation_degrees: item
                                .get("rotationDegrees")
                                .and_then(Value::as_f64)
                                .unwrap_or_default()
                                as f32,
                            rotation_origin_x: Some((x + width / 2.0) * scene_width),
                            rotation_origin_y: Some(
                                scene_height - (y + height / 2.0) * scene_height,
                            ),
                            opacity: item.get("opacity").and_then(Value::as_f64).unwrap_or(1.0)
                                as f32
                                * if shadow == "Glow" { 0.35 } else { 0.55 },
                            light_text: false,
                            fill_rgb: Some([0.0, 0.0, 0.0]),
                            semantic_role: LayoutSemanticRole::Paragraph,
                            artifact: true,
                            language: None,
                            reading_order: None,
                            semantic_id: None,
                            semantic_parent_id: None,
                            source_start_utf16: None,
                            source_end_utf16: None,
                            link_page: None,
                        });
                        page.paint_order.push(LayoutPaint::Line(shadow_index));
                    }
                    let paint_index = page.lines.len();
                    page.lines.push(LayoutLine {
                        text: line_text,
                        runs,
                        size,
                        x: line_x,
                        y: line_y,
                        baseline_offset_points,
                        word_spacing: 0.0,
                        character_spacing,
                        rotation_degrees: item
                            .get("rotationDegrees")
                            .and_then(Value::as_f64)
                            .unwrap_or_default() as f32,
                        rotation_origin_x: Some((x + width / 2.0) * scene_width),
                        rotation_origin_y: Some(scene_height - (y + height / 2.0) * scene_height),
                        opacity: item.get("opacity").and_then(Value::as_f64).unwrap_or(1.0) as f32,
                        light_text,
                        fill_rgb,
                        semantic_role: layout_semantic_role(&string(&item, "semanticRole")),
                        artifact: string(&item, "semanticRole") == "Artifact",
                        language: item
                            .get("language")
                            .and_then(Value::as_str)
                            .map(str::to_owned),
                        reading_order: item
                            .get("readingOrder")
                            .and_then(Value::as_i64)
                            .map(|value| value as i32),
                        semantic_id: Some(string(&item, "id")),
                        semantic_parent_id: None,
                        source_start_utf16: None,
                        source_end_utf16: None,
                        link_page: None,
                    });
                    page.paint_order.push(LayoutPaint::Line(paint_index));
                }
            }
            kind @ ("Rectangle" | "Ellipse" | "Line") => {
                let shape_index = page.shapes.len();
                page.shapes.push(LayoutShape {
                    kind: match kind {
                        "Ellipse" => LayoutShapeKind::Ellipse,
                        "Line" => LayoutShapeKind::Line,
                        _ => LayoutShapeKind::Rectangle,
                    },
                    x: x * scene_width,
                    y: scene_height - (y + height) * scene_height,
                    width: width * scene_width,
                    height: height * scene_height,
                    fill_rgb: parse_hex_color(
                        scene_style_value(scene, &item, "fillColor").and_then(Value::as_str),
                    ),
                    stroke_rgb: parse_hex_color(
                        scene_style_value(scene, &item, "strokeColor").and_then(Value::as_str),
                    ),
                    stroke_width: scene_style_value(scene, &item, "strokeWidthPoints")
                        .and_then(Value::as_f64)
                        .unwrap_or_default() as f32,
                    opacity: item
                        .get("opacity")
                        .and_then(Value::as_f64)
                        .unwrap_or(1.0)
                        .clamp(0.0, 1.0) as f32,
                    rotation_degrees: item
                        .get("rotationDegrees")
                        .and_then(Value::as_f64)
                        .unwrap_or_default() as f32,
                    semantic_id: None,
                    semantic_parent_id: None,
                });
                page.paint_order.push(LayoutPaint::Shape(shape_index));
            }
            _ => {}
        }
    }
    Ok(page)
}

fn flatten_composition_objects(scene: &Value) -> Result<Vec<Value>, Diagnostic> {
    let source = scene
        .get("objects")
        .and_then(Value::as_array)
        .cloned()
        .unwrap_or_default();
    let groups = source
        .iter()
        .filter(|item| string(item, "kind") == "Group")
        .map(|item| (string(item, "id"), item.clone()))
        .collect::<std::collections::HashMap<_, _>>();
    let mut result = Vec::new();
    for mut item in source {
        if string(&item, "kind") == "Group" {
            continue;
        }
        let group_id = string(&item, "groupId");
        if !group_id.is_empty() {
            let group = groups.get(&group_id).ok_or_else(|| {
                Diagnostic::error(
                    "PRESS_COMPOSITION_GROUP_INVALID",
                    format!(
                        "Composition object '{}' references a missing group.",
                        string(&item, "id")
                    ),
                )
            })?;
            let group_bounds = group.get("bounds").unwrap_or(&Value::Null);
            let child_bounds = item.get("bounds").unwrap_or(&Value::Null);
            let gx = group_bounds
                .get("xPercent")
                .and_then(Value::as_f64)
                .unwrap_or_default();
            let gy = group_bounds
                .get("yPercent")
                .and_then(Value::as_f64)
                .unwrap_or_default();
            let gw = group_bounds
                .get("widthPercent")
                .and_then(Value::as_f64)
                .unwrap_or(100.0);
            let gh = group_bounds
                .get("heightPercent")
                .and_then(Value::as_f64)
                .unwrap_or(100.0);
            let cx = child_bounds
                .get("xPercent")
                .and_then(Value::as_f64)
                .unwrap_or_default();
            let cy = child_bounds
                .get("yPercent")
                .and_then(Value::as_f64)
                .unwrap_or_default();
            let cw = child_bounds
                .get("widthPercent")
                .and_then(Value::as_f64)
                .unwrap_or(100.0);
            let ch = child_bounds
                .get("heightPercent")
                .and_then(Value::as_f64)
                .unwrap_or(100.0);
            let group_rotation = group
                .get("rotationDegrees")
                .and_then(Value::as_f64)
                .unwrap_or_default();
            let rotation = item
                .get("rotationDegrees")
                .and_then(Value::as_f64)
                .unwrap_or_default()
                + group_rotation;
            let surface = scene.get("surface").unwrap_or(&Value::Null);
            let surface_width = surface
                .get("widthPoints")
                .and_then(Value::as_f64)
                .filter(|value| *value > 0.0)
                .unwrap_or(100.0);
            let surface_height = surface
                .get("heightPoints")
                .and_then(Value::as_f64)
                .filter(|value| *value > 0.0)
                .unwrap_or(100.0);
            let width = cw / 100.0 * gw;
            let height = ch / 100.0 * gh;
            let child_center_x = (gx + (cx + cw / 2.0) / 100.0 * gw) / 100.0 * surface_width;
            let child_center_y = (gy + (cy + ch / 2.0) / 100.0 * gh) / 100.0 * surface_height;
            let group_center_x = (gx + gw / 2.0) / 100.0 * surface_width;
            let group_center_y = (gy + gh / 2.0) / 100.0 * surface_height;
            let angle = group_rotation.to_radians();
            let delta_x = child_center_x - group_center_x;
            let delta_y = child_center_y - group_center_y;
            let rotated_center_x = group_center_x + delta_x * angle.cos() - delta_y * angle.sin();
            let rotated_center_y = group_center_y + delta_x * angle.sin() + delta_y * angle.cos();
            let x = (rotated_center_x - width / 200.0 * surface_width) / surface_width * 100.0;
            let y = (rotated_center_y - height / 200.0 * surface_height) / surface_height * 100.0;
            let opacity = item.get("opacity").and_then(Value::as_f64).unwrap_or(1.0)
                * group.get("opacity").and_then(Value::as_f64).unwrap_or(1.0);
            let visible = item.get("visible").and_then(Value::as_bool).unwrap_or(true)
                && group
                    .get("visible")
                    .and_then(Value::as_bool)
                    .unwrap_or(true);
            let z_index = item
                .get("zIndex")
                .and_then(Value::as_i64)
                .unwrap_or_default()
                + group
                    .get("zIndex")
                    .and_then(Value::as_i64)
                    .unwrap_or_default();
            let object = item.as_object_mut().ok_or_else(|| {
                Diagnostic::error(
                    "PRESS_COMPOSITION_OBJECT_INVALID",
                    "Composition object must be an object.",
                )
            })?;
            object.insert(
                "bounds".to_owned(),
                serde_json::json!({
                    "xPercent": x,
                    "yPercent": y,
                    "widthPercent": width,
                    "heightPercent": height,
                }),
            );
            object.insert("rotationDegrees".to_owned(), Value::from(rotation));
            object.insert("opacity".to_owned(), Value::from(opacity));
            object.insert("visible".to_owned(), Value::from(visible));
            object.insert("zIndex".to_owned(), Value::from(z_index));
        }
        result.push(item);
    }
    Ok(result)
}

fn resolve_content_reference(
    semantic_blocks: &[Value],
    reference: &Value,
) -> Result<String, Diagnostic> {
    let block_id = string(reference, "blockId");
    let block = semantic_blocks
        .iter()
        .find(|block| string(block, "id") == block_id)
        .ok_or_else(|| {
            Diagnostic::error(
                "PRESS_COMPOSITION_REFERENCE_MISSING",
                format!("Composition content reference '{block_id}' does not exist."),
            )
        })?;
    let text = display_block_text(block);
    let utf16_len = text.encode_utf16().count();
    let start = reference
        .get("startOffset")
        .and_then(Value::as_u64)
        .map(|value| value as usize)
        .unwrap_or(0);
    let end = reference
        .get("endOffset")
        .and_then(Value::as_u64)
        .map(|value| value as usize)
        .unwrap_or(utf16_len);
    if end < start || end > utf16_len {
        return Err(Diagnostic::error(
            "PRESS_COMPOSITION_RANGE_INVALID",
            format!("Composition content reference '{block_id}' has an invalid range."),
        ));
    }
    let start_byte = utf16_offset_to_byte(&text, start).ok_or_else(|| {
        Diagnostic::error(
            "PRESS_COMPOSITION_RANGE_INVALID",
            format!("Composition content reference '{block_id}' splits a Unicode character."),
        )
    })?;
    let end_byte = utf16_offset_to_byte(&text, end).ok_or_else(|| {
        Diagnostic::error(
            "PRESS_COMPOSITION_RANGE_INVALID",
            format!("Composition content reference '{block_id}' splits a Unicode character."),
        )
    })?;
    Ok(text[start_byte..end_byte].to_owned())
}

fn resolve_content_reference_runs(
    document: &Value,
    semantic_blocks: &[Value],
    reference: &Value,
    style: &BlockStyle,
) -> Result<(String, Vec<LayoutRun>), Diagnostic> {
    let text = resolve_content_reference(semantic_blocks, reference)?;
    let block_id = string(reference, "blockId");
    let block = semantic_blocks
        .iter()
        .find(|block| string(block, "id") == block_id)
        .ok_or_else(|| {
            Diagnostic::error(
                "PRESS_COMPOSITION_REFERENCE_MISSING",
                format!("Composition content reference '{block_id}' does not exist."),
            )
        })?;
    let full_text = display_block_text(block);
    let start = reference
        .get("startOffset")
        .and_then(Value::as_u64)
        .map(|value| value as usize)
        .unwrap_or(0);
    let end = reference
        .get("endOffset")
        .and_then(Value::as_u64)
        .map(|value| value as usize)
        .unwrap_or_else(|| full_text.encode_utf16().count());
    let mut cursor = 0usize;
    let mut sliced_spans = Vec::new();
    for span in block
        .get("content")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
    {
        let source = span.get("text").and_then(Value::as_str).unwrap_or("");
        let span_len = source.encode_utf16().count();
        let span_start = cursor;
        let span_end = cursor + span_len;
        cursor = span_end;
        let slice_start = start.max(span_start);
        let slice_end = end.min(span_end);
        if slice_end <= slice_start {
            continue;
        }
        let relative_start = slice_start - span_start;
        let relative_end = slice_end - span_start;
        let start_byte = utf16_offset_to_byte(source, relative_start).ok_or_else(|| {
            Diagnostic::error(
                "PRESS_COMPOSITION_RANGE_INVALID",
                format!("Composition content reference '{block_id}' splits a Unicode character."),
            )
        })?;
        let end_byte = utf16_offset_to_byte(source, relative_end).ok_or_else(|| {
            Diagnostic::error(
                "PRESS_COMPOSITION_RANGE_INVALID",
                format!("Composition content reference '{block_id}' splits a Unicode character."),
            )
        })?;
        let mut sliced = span.clone();
        sliced["text"] = Value::String(source[start_byte..end_byte].to_owned());
        sliced_spans.push(sliced);
    }
    let mut sliced_block = block.clone();
    sliced_block["content"] = Value::Array(sliced_spans);
    Ok((text, block_runs(document, &sliced_block, style)))
}

fn validate_semantic_coverage(
    semantic_blocks: &[Value],
    references: &[Value],
) -> Result<(), Diagnostic> {
    let mut ranges = std::collections::BTreeMap::<String, Vec<(usize, usize)>>::new();
    for reference in references {
        let block_id = string(reference, "blockId");
        let block = semantic_blocks
            .iter()
            .find(|block| string(block, "id") == block_id)
            .ok_or_else(|| {
                Diagnostic::error(
                    "PRESS_COMPOSITION_REFERENCE_MISSING",
                    format!("Composition content reference '{block_id}' does not exist."),
                )
            })?;
        let text = display_block_text(block);
        let utf16_len = text.encode_utf16().count();
        let start = reference
            .get("startOffset")
            .and_then(Value::as_u64)
            .map(|value| value as usize)
            .unwrap_or(0);
        let end = reference
            .get("endOffset")
            .and_then(Value::as_u64)
            .map(|value| value as usize)
            .unwrap_or(utf16_len);
        resolve_content_reference(semantic_blocks, reference)?;
        let entry = ranges.entry(block_id.clone()).or_default();
        if entry
            .iter()
            .any(|(existing_start, existing_end)| start < *existing_end && *existing_start < end)
        {
            return Err(Diagnostic::error(
                "PRESS_COMPOSITION_RANGE_OVERLAP",
                format!("Composition content reference '{block_id}' overlaps another frame."),
            ));
        }
        entry.push((start, end));
    }
    let mut unplaced = 0;
    for block in semantic_blocks {
        let text = display_block_text(block);
        if text.trim().is_empty() {
            continue;
        }
        let block_id = string(block, "id");
        let mut block_ranges = ranges.remove(&block_id).unwrap_or_default();
        block_ranges.sort_unstable();
        let mut cursor = 0;
        let mut has_gap = false;
        for (start, end) in block_ranges {
            let start_byte = utf16_offset_to_byte(&text, start).expect("validated reference start");
            let cursor_byte = utf16_offset_to_byte(&text, cursor).expect("validated cursor");
            if !text[cursor_byte..start_byte].trim().is_empty() {
                has_gap = true;
                break;
            }
            cursor = end;
        }
        let cursor_byte = utf16_offset_to_byte(&text, cursor).expect("validated cursor");
        if has_gap || !text[cursor_byte..].trim().is_empty() {
            unplaced += 1;
        }
    }
    if unplaced > 0 {
        return Err(Diagnostic::error(
            "PRESS_COMPOSITION_CONTENT_UNPLACED",
            format!("Designed Page contains {unplaced} unplaced semantic content block(s)."),
        ));
    }
    Ok(())
}

fn utf16_offset_to_byte(text: &str, offset: usize) -> Option<usize> {
    if offset == 0 {
        return Some(0);
    }
    let mut utf16_offset = 0;
    for (byte_index, character) in text.char_indices() {
        if utf16_offset == offset {
            return Some(byte_index);
        }
        utf16_offset += character.len_utf16();
        if utf16_offset > offset {
            return None;
        }
    }
    (utf16_offset == offset).then_some(text.len())
}

fn scene_style_value<'a>(scene: &'a Value, item: &'a Value, key: &str) -> Option<&'a Value> {
    item.get("styleId")
        .and_then(Value::as_str)
        .and_then(|style_id| {
            scene
                .get("styles")
                .and_then(Value::as_array)
                .and_then(|styles| styles.iter().find(|style| string(style, "id") == style_id))
        })
        .and_then(|style| style.get(key))
        .or_else(|| item.get(key))
}

fn parse_hex_color(value: Option<&str>) -> Option<[f32; 3]> {
    let value = value?.trim();
    if value.eq_ignore_ascii_case("transparent") {
        return None;
    }
    let hex = value.strip_prefix('#')?;
    if hex.len() != 6 {
        return None;
    }
    let red = u8::from_str_radix(&hex[0..2], 16).ok()?;
    let green = u8::from_str_radix(&hex[2..4], 16).ok()?;
    let blue = u8::from_str_radix(&hex[4..6], 16).ok()?;
    Some([
        red as f32 / 255.0,
        green as f32 / 255.0,
        blue as f32 / 255.0,
    ])
}

fn designed_pages(
    composition: &Value,
    document: &Value,
    trim: &crate::model::Trim,
    digital: bool,
    allow_independent_page: bool,
    tolerance: LayoutTolerance,
    diagnostics: &mut Vec<Diagnostic>,
) -> Result<Vec<LayoutPage>, Diagnostic> {
    let page = designed_page(composition, document, trim, tolerance, diagnostics)?;
    let trim_width = trim.width_inches * 72.0;
    let trim_height = trim.height_inches * 72.0;
    let width = page.width_points.unwrap_or(trim_width);
    let height = page.height_points.unwrap_or(trim_height);
    let variant = composition
        .get("variants")
        .and_then(Value::as_array)
        .and_then(|items| items.first());
    let surface = variant
        .and_then(|item| item.get("scene"))
        .and_then(|scene| scene.get("surface"));
    let surface_kind = surface
        .map(|surface| string(surface, "kind"))
        .unwrap_or_default();
    let output_page_mode = surface
        .map(|surface| string(surface, "outputPageMode"))
        .unwrap_or_default();
    let facing_edition_leaves = surface_kind == "FacingSpread"
        && (output_page_mode.is_empty() || output_page_mode == "EditionLeaves");
    if digital {
        if facing_edition_leaves {
            let leaf_width = width / 2.0;
            let uses_trim_geometry =
                (leaf_width - trim_width).abs() <= 0.02 && (height - trim_height).abs() <= 0.02;
            if !allow_independent_page && !uses_trim_geometry {
                return Err(Diagnostic::error(
                    "PRESS_DIGITAL_PAGE_OVERRIDE_DISABLED",
                    "This Digital PDF edition uses uniform page geometry; enable Designed Page overrides for an independent page box.",
                ));
            }
            validate_facing_spread_text(&page, leaf_width)?;
            let mut leaves = split_facing_spread(page, leaf_width);
            if allow_independent_page && !uses_trim_geometry {
                for leaf in &mut leaves {
                    leaf.width_points = Some(leaf_width);
                    leaf.height_points = Some(height);
                }
            }
            return Ok(leaves);
        }
        if !allow_independent_page
            && ((width - trim_width).abs() > 0.02 || (height - trim_height).abs() > 0.02)
        {
            return Err(Diagnostic::error(
                "PRESS_DIGITAL_PAGE_OVERRIDE_DISABLED",
                "This Digital PDF edition uses uniform page geometry; enable Designed Page overrides for an independent page box.",
            ));
        }
        return Ok(vec![page]);
    }
    if (width - trim_width).abs() <= 0.02 && (height - trim_height).abs() <= 0.02 {
        return Ok(vec![LayoutPage {
            width_points: None,
            height_points: None,
            ..page
        }]);
    }
    if facing_edition_leaves
        && (width - trim_width * 2.0).abs() <= 0.02
        && (height - trim_height).abs() <= 0.02
    {
        validate_facing_spread_text(&page, trim_width)?;
        return Ok(split_facing_spread(page, trim_width));
    }
    Err(Diagnostic::error(
        "PRESS_PRINT_PAGE_GEOMETRY_INCONSISTENT",
        "Print Designed Pages must be one trim-sized leaf or a two-leaf facing spread.",
    ))
}

fn split_facing_spread(page: LayoutPage, leaf_width: f32) -> Vec<LayoutPage> {
    [0.0, leaf_width]
        .into_iter()
        .map(|leaf_start| {
            let mut line_map = vec![None; page.lines.len()];
            let lines = page
                .lines
                .iter()
                .enumerate()
                .filter(|(_, line)| line.x >= leaf_start && line.x < leaf_start + leaf_width)
                .map(|(index, line)| {
                    let mapped = line_map.iter().flatten().count();
                    line_map[index] = Some(mapped);
                    LayoutLine {
                        x: line.x - leaf_start,
                        rotation_origin_x: line.rotation_origin_x.map(|value| value - leaf_start),
                        ..line.clone()
                    }
                })
                .collect();
            let mut shape_map = vec![None; page.shapes.len()];
            let shapes = page
                .shapes
                .iter()
                .enumerate()
                .filter(|(_, shape)| {
                    shape.x < leaf_start + leaf_width && shape.x + shape.width > leaf_start
                })
                .map(|(index, shape)| {
                    let mapped = shape_map.iter().flatten().count();
                    shape_map[index] = Some(mapped);
                    LayoutShape {
                        x: shape.x - leaf_start,
                        ..shape.clone()
                    }
                })
                .collect();
            let mut image_map = vec![None; page.images.len()];
            let mut leaf = LayoutPage {
                kind: page.kind,
                width_points: None,
                height_points: None,
                lines,
                images: Vec::new(),
                shapes,
                paint_order: Vec::new(),
                barcode_modules: None,
                page_label: None,
                bookmark: None,
            };
            for (index, image) in page.images.iter().enumerate() {
                let left = image.x.max(leaf_start);
                let right = (image.x + image.width).min(leaf_start + leaf_width);
                if right <= left {
                    continue;
                }
                let consumed_start = (left - image.x) / image.width;
                let consumed_width = (right - left) / image.width;
                image_map[index] = Some(leaf.images.len());
                leaf.images.push(LayoutImage {
                    x: left - leaf_start,
                    width: right - left,
                    source_left_fraction: image.source_left_fraction
                        + consumed_start * image.source_width_fraction,
                    source_width_fraction: consumed_width * image.source_width_fraction,
                    ..image.clone()
                });
            }
            leaf.paint_order = page
                .paint_order
                .iter()
                .filter_map(|paint| match *paint {
                    LayoutPaint::Shape(index) => shape_map[index].map(LayoutPaint::Shape),
                    LayoutPaint::Image(index) => image_map[index].map(LayoutPaint::Image),
                    LayoutPaint::Line(index) => line_map[index].map(LayoutPaint::Line),
                })
                .collect();
            leaf
        })
        .collect()
}

fn validate_facing_spread_text(page: &LayoutPage, gutter_x: f32) -> Result<(), Diagnostic> {
    if page.lines.iter().filter(|line| !line.artifact).any(|line| {
        let width = measured_run_width(&line.runs, line.size)
            + line.word_spacing
                * line
                    .text
                    .chars()
                    .filter(|character| character.is_whitespace())
                    .count() as f32
            + line.character_spacing * line.text.chars().count().saturating_sub(1) as f32;
        let height = line.size * 1.2;
        let origin_x = line.rotation_origin_x.unwrap_or(line.x);
        let origin_y = line.rotation_origin_y.unwrap_or(line.y);
        let radians = line.rotation_degrees.to_radians();
        let (sin, cos) = radians.sin_cos();
        let mut minimum_x = f32::INFINITY;
        let mut maximum_x = f32::NEG_INFINITY;
        for (x, y) in [
            (line.x, line.y - height),
            (line.x + width, line.y - height),
            (line.x, line.y),
            (line.x + width, line.y),
        ] {
            let rotated_x = origin_x + (x - origin_x) * cos - (y - origin_y) * sin;
            minimum_x = minimum_x.min(rotated_x);
            maximum_x = maximum_x.max(rotated_x);
        }
        minimum_x < gutter_x - 0.01 && maximum_x > gutter_x + 0.01
    }) {
        return Err(Diagnostic::error(
            "PRESS_FACING_SPREAD_TEXT_CROSSES_GUTTER",
            "A semantic text line crosses the facing-spread gutter. Move or resize the text frame so each line belongs to one leaf.",
        ));
    }
    Ok(())
}

fn layout_semantic_role(value: &str) -> LayoutSemanticRole {
    match value {
        "Heading1" => LayoutSemanticRole::Heading1,
        "Heading2" => LayoutSemanticRole::Heading2,
        "Heading3" => LayoutSemanticRole::Heading3,
        "Caption" => LayoutSemanticRole::Caption,
        "Credit" => LayoutSemanticRole::Credit,
        _ => LayoutSemanticRole::Paragraph,
    }
}

fn layout_image_fit(value: &str) -> LayoutImageFit {
    match value {
        "Contain" => LayoutImageFit::Contain,
        "Cover" => LayoutImageFit::Cover,
        "Stretch" => LayoutImageFit::Stretch,
        _ => LayoutImageFit::Contain,
    }
}

#[derive(Clone, Copy)]
enum LeafSide {
    Recto,
    Verso,
}

fn blank_page() -> LayoutPage {
    LayoutPage {
        kind: PageKind::Blank,
        ..empty_body_page()
    }
}

fn ensure_next_leaf(pages: &mut Vec<LayoutPage>, side: LeafSide) {
    let next_page_is_recto = (pages.len() + 1) % 2 == 1;
    let needs_blank = match side {
        LeafSide::Recto => !next_page_is_recto,
        LeafSide::Verso => next_page_is_recto,
    };
    if needs_blank {
        pages.push(blank_page());
    }
}

fn chapter_begins_with_facing_spread(chapter: &Value) -> bool {
    let Some(block) = chapter
        .get("blocks")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .find(|block| {
            let block_type = string(block, "type");
            !block_type.eq_ignore_ascii_case("Paragraph")
                || !display_block_text(block).trim().is_empty()
        })
    else {
        return false;
    };
    if !string(block, "type").eq_ignore_ascii_case("DesignedPage") {
        return false;
    }
    let composition_id = string(block, "pageCompositionId");
    chapter
        .get("pageCompositions")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .find(|composition| string(composition, "id") == composition_id)
        .and_then(|composition| composition.get("variants"))
        .and_then(Value::as_array)
        .and_then(|variants| variants.first())
        .and_then(|variant| variant.get("scene"))
        .and_then(|scene| scene.get("surface"))
        .is_some_and(|surface| {
            string(surface, "kind") == "FacingSpread"
                && matches!(
                    string(surface, "outputPageMode").as_str(),
                    "" | "EditionLeaves"
                )
        })
}

fn ordered_publication_sections<'a>(
    document: &'a Value,
    anchor: &str,
    target_id: Option<&str>,
) -> Vec<&'a Value> {
    document
        .get("publicationSections")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter(|section| {
            string(section, "anchor").eq_ignore_ascii_case(anchor)
                && target_id.is_none_or(|id| string(section, "targetId") == id)
        })
        .collect::<Vec<_>>()
}

fn publication_section_leaf_side(section: &Value) -> Option<LeafSide> {
    match string(section, "startSide").as_str() {
        "Recto" => Some(LeafSide::Recto),
        "Verso" => Some(LeafSide::Verso),
        _ => None,
    }
}

fn warn_front_matter_order(
    document: &Value,
    sections: &[&Value],
    diagnostics: &mut Vec<Diagnostic>,
) {
    let title = sections
        .iter()
        .position(|section| string(section, "systemRole").eq_ignore_ascii_case("Title"));
    let copyright = sections
        .iter()
        .position(|section| string(section, "systemRole").eq_ignore_ascii_case("Copyright"));
    if !matches!((title, copyright), (Some(title), Some(copyright)) if copyright < title) {
        return;
    }
    let vendor = string(document, "printVendor");
    let message = if vendor.eq_ignore_ascii_case("AmazonKdp") {
        "KDP's optional front-matter guidance recommends placing the title page before the copyright page. Lorekeeper preserved the configured order."
    } else {
        "Common left-to-right book-design practice places the title page before the copyright page. Lorekeeper preserved the configured order."
    };
    diagnostics.push(
        Diagnostic::warning("PRESS_FRONT_MATTER_ORDER_RECOMMENDATION", message).with_source(
            "publication-section",
            string(sections[copyright.unwrap()], "id"),
        ),
    );
}

fn warn_front_matter_side(
    document: &Value,
    section: &Value,
    page: usize,
    diagnostics: &mut Vec<Diagnostic>,
) {
    let role = string(section, "systemRole");
    let recommended = match role.as_str() {
        "Title" => LeafSide::Recto,
        "Copyright" => LeafSide::Verso,
        _ => return,
    };
    let is_recto = page % 2 == 1;
    if matches!(recommended, LeafSide::Recto) == is_recto {
        return;
    }
    let vendor = string(document, "printVendor");
    let side = if matches!(recommended, LeafSide::Recto) {
        "right-hand (recto)"
    } else {
        "left-hand (verso)"
    };
    let message = if vendor.eq_ignore_ascii_case("AmazonKdp") {
        format!(
            "KDP's optional front-matter guidance recommends the {} page on a {side} page. Lorekeeper preserved the configured placement.",
            role.to_lowercase()
        )
    } else {
        format!(
            "Common left-to-right book-design practice places the {} page on a {side} page. Lorekeeper preserved the configured placement.",
            role.to_lowercase()
        )
    };
    diagnostics.push(
        Diagnostic::warning("PRESS_FRONT_MATTER_SIDE_RECOMMENDATION", message)
            .with_source("publication-section", string(section, "id"))
            .with_page(page),
    );
}

#[allow(clippy::too_many_arguments)]
fn append_publication_sections(
    pages: &mut Vec<LayoutPage>,
    document: &Value,
    anchor: &str,
    target_id: Option<&str>,
    trim: &crate::model::Trim,
    is_digital_pdf: bool,
    tolerance: LayoutTolerance,
    diagnostics: &mut Vec<Diagnostic>,
    page_map: &mut Vec<PageMapEntry>,
    semantic_order: &mut i32,
) -> RenderResult<Option<usize>> {
    let mut toc_index = None;
    let ordered_sections = ordered_publication_sections(document, anchor, target_id);
    if !is_digital_pdf && anchor.eq_ignore_ascii_case("Front") && target_id.is_none() {
        warn_front_matter_order(document, &ordered_sections, diagnostics);
    }
    for section in ordered_sections {
        let section_id = string(section, "id");
        let leaf_side = (!is_digital_pdf)
            .then(|| publication_section_leaf_side(section))
            .flatten();
        if string(section, "systemRole").eq_ignore_ascii_case("Contents") {
            if let Some(side) = leaf_side {
                ensure_next_leaf(pages, side);
            }
            toc_index = Some(pages.len());
            if !is_digital_pdf {
                warn_front_matter_side(document, section, pages.len() + 1, diagnostics);
            }
            pages.push(centered_page("Contents", "", trim));
            continue;
        }
        let blocks = section
            .get("blocks")
            .and_then(Value::as_array)
            .into_iter()
            .flatten()
            .collect::<Vec<_>>();
        let begins_with_flowing_content = blocks
            .first()
            .is_some_and(|block| !string(block, "type").eq_ignore_ascii_case("DesignedPage"));
        if begins_with_flowing_content {
            if let Some(side) = leaf_side {
                ensure_next_leaf(pages, side);
            }
            if !is_digital_pdf {
                warn_front_matter_side(document, section, pages.len() + 1, diagnostics);
            }
            pages.push(empty_body_page());
        }
        let mut previous_was_designed_page = false;
        let mut previous_space_after = 0.0f32;
        for (block_index, block) in blocks.into_iter().enumerate() {
            *semantic_order += 1;
            let block_id = string(block, "id");
            let block_type = string(block, "type");
            if previous_was_designed_page && !block_type.eq_ignore_ascii_case("DesignedPage") {
                pages.push(empty_body_page());
                previous_space_after = 0.0;
            }
            if block_type.eq_ignore_ascii_case("DesignedPage") {
                let composition_id = string(block, "pageCompositionId");
                let composition = section
                    .get("pageCompositions")
                    .and_then(Value::as_array)
                    .into_iter()
                    .flatten()
                    .find(|item| string(item, "id") == composition_id)
                    .ok_or_else(|| {
                        Box::new(RenderResponse::failed(
                            "rejected",
                            Diagnostic::error(
                                "PRESS_COMPOSITION_MISSING",
                                format!(
                                    "Publication section '{}' references a missing Designed Page composition.",
                                    string(section, "title")
                                ),
                            )
                            .with_source("publication-section", section_id.clone()),
                        ))
                    })?;
                let rendered = designed_pages(
                    composition,
                    document,
                    trim,
                    is_digital_pdf,
                    document
                        .get("allowDesignedPageOverrides")
                        .and_then(Value::as_bool)
                        .unwrap_or(false),
                    tolerance,
                    diagnostics,
                )
                .map_err(|diagnostic| {
                    Box::new(RenderResponse::failed(
                        "rejected",
                        diagnostic.with_source("publication-section", section_id.clone()),
                    ))
                })?;
                if !is_digital_pdf {
                    if rendered.len() == 2 {
                        ensure_next_leaf(pages, LeafSide::Verso);
                    } else if block_index == 0
                        && let Some(side) = leaf_side
                    {
                        ensure_next_leaf(pages, side);
                    }
                }
                let semantic_snapshot = pages
                    .iter()
                    .map(|page| (page.lines.len(), page.images.len()))
                    .collect::<Vec<_>>();
                let first_page = pages.len() + 1;
                if !is_digital_pdf && block_index == 0 {
                    warn_front_matter_side(document, section, first_page, diagnostics);
                }
                pages.extend(rendered);
                assign_semantic_order_since(
                    pages,
                    &semantic_snapshot,
                    *semantic_order,
                    &block_id,
                    None,
                    None,
                );
                if !block_id.is_empty() {
                    page_map.push(PageMapEntry {
                        chapter_id: section_id.clone(),
                        block_id,
                        page_number: first_page,
                    });
                }
                previous_was_designed_page = true;
                previous_space_after = 0.0;
                continue;
            }
            let semantic_snapshot = pages
                .iter()
                .map(|page| (page.lines.len(), page.images.len()))
                .collect::<Vec<_>>();
            previous_was_designed_page = false;
            if block_type.eq_ignore_ascii_case("Figure") {
                let caption_style = block_style(document, block, trim);
                let caption_runs = block_runs(document, block, &caption_style);
                let caption_runs = if caption_runs.is_empty() {
                    single_run(&string(block, "caption"), caption_style.face)
                } else {
                    caption_runs
                };
                let presentation = block.get("presentation").unwrap_or(&Value::Null);
                let placement = string(presentation, "placement");
                let width_percent = if matches!(placement.as_str(), "FullWidth" | "FullBleed") {
                    100.0
                } else {
                    presentation
                        .get("widthPercent")
                        .and_then(Value::as_f64)
                        .unwrap_or(100.0) as f32
                };
                let focal_x = presentation
                    .get("cropXPercent")
                    .and_then(Value::as_f64)
                    .unwrap_or(50.0) as f32;
                let focal_y = presentation
                    .get("cropYPercent")
                    .and_then(Value::as_f64)
                    .unwrap_or(50.0) as f32;
                let alignment = match string(presentation, "alignment").as_str() {
                    "Start" => "left",
                    "End" => "right",
                    _ => "center",
                };
                let caption_placement = string(presentation, "captionPlacement");
                let caption_step = caption_style.size * caption_style.line_height.max(1.0);
                let dedicated = matches!(placement.as_str(), "DedicatedPage" | "FullBleed");
                if presentation
                    .get("startOnNewPage")
                    .and_then(Value::as_bool)
                    .unwrap_or(false)
                    && !dedicated
                {
                    pages.push(empty_body_page());
                }
                if dedicated {
                    let mut page = dedicated_figure_page_with_layout(
                        trim,
                        &string(block, "caption"),
                        &caption_runs,
                        &caption_style,
                        string(block, "assetId"),
                        width_percent,
                        focal_x,
                        focal_y,
                        alignment,
                    );
                    if let Some(image) = page.images.first_mut() {
                        image.alt_text = block
                            .get("altText")
                            .and_then(Value::as_str)
                            .map(str::to_owned);
                        image.decorative = block
                            .get("decorative")
                            .and_then(Value::as_bool)
                            .unwrap_or(false);
                        image.language = block
                            .get("language")
                            .and_then(Value::as_str)
                            .map(str::to_owned);
                        image.fit = layout_image_fit(&string(presentation, "fit"));
                        image.accessibility_role = block
                            .get("accessibilityRole")
                            .and_then(Value::as_str)
                            .map(str::to_owned);
                        if placement == "FullBleed" {
                            let bleed = trim.bleed_inches * 72.0;
                            image.x = -bleed;
                            image.y = -bleed;
                            image.width = trim.width_inches * 72.0 + bleed * 2.0;
                            image.height = trim.height_inches * 72.0 + bleed * 2.0;
                        }
                    }
                    if caption_placement == "Hidden" {
                        page.lines.clear();
                    } else if caption_placement == "Above" {
                        let top = trim.height_inches * 72.0
                            - trim.margin_inches * 72.0
                            - caption_style.space_before.max(0.0);
                        for (index, line) in page.lines.iter_mut().enumerate() {
                            line.y = top - index as f32 * caption_step;
                        }
                    } else if caption_placement == "Overlay"
                        && let Some(image) = page.images.first()
                    {
                        let first_y = image.y
                            + caption_style.size
                            + page.lines.len().saturating_sub(1) as f32 * caption_step;
                        for (index, line) in page.lines.iter_mut().enumerate() {
                            line.y = first_y - index as f32 * caption_step;
                            line.light_text = true;
                        }
                    }
                    for line in &mut page.lines {
                        line.language = block
                            .get("language")
                            .and_then(Value::as_str)
                            .map(str::to_owned);
                        line.artifact = false;
                        line.fill_rgb = if caption_placement == "Overlay" {
                            Some(
                                typography::defaults()
                                    .caption
                                    .overlay
                                    .as_ref()
                                    .expect("caption overlay defaults")
                                    .text_color_rgb,
                            )
                        } else {
                            typography::defaults().caption.text_color_rgb
                        };
                    }
                    if caption_placement == "Overlay" {
                        add_overlay_caption_background(&mut page, &block_id, &caption_style);
                    }
                    pages.push(page);
                } else {
                    append_inline_illustration(
                        pages,
                        trim,
                        &block_id,
                        &string(block, "caption"),
                        &caption_runs,
                        &caption_style,
                        string(block, "assetId"),
                        width_percent,
                        focal_x,
                        focal_y,
                        alignment,
                        &string(presentation, "textWrap"),
                        layout_image_fit(&string(presentation, "fit")),
                        presentation
                            .get("spacingBeforePoints")
                            .and_then(Value::as_f64)
                            .unwrap_or(6.0) as f32,
                        presentation
                            .get("spacingAfterPoints")
                            .and_then(Value::as_f64)
                            .unwrap_or(6.0) as f32,
                        &caption_placement,
                        presentation
                            .get("keepWithCaption")
                            .and_then(Value::as_bool)
                            .unwrap_or(true),
                    );
                    if let Some(image) = pages.last_mut().and_then(|page| page.images.last_mut()) {
                        image.alt_text = block
                            .get("altText")
                            .and_then(Value::as_str)
                            .map(str::to_owned);
                        image.decorative = block
                            .get("decorative")
                            .and_then(Value::as_bool)
                            .unwrap_or(false);
                        image.language = block
                            .get("language")
                            .and_then(Value::as_str)
                            .map(str::to_owned);
                        image.accessibility_role = block
                            .get("accessibilityRole")
                            .and_then(Value::as_str)
                            .map(str::to_owned);
                    }
                    if let Some(page) = pages.last_mut() {
                        for line in page
                            .lines
                            .iter_mut()
                            .filter(|line| line.semantic_role == LayoutSemanticRole::Caption)
                        {
                            line.language = block
                                .get("language")
                                .and_then(Value::as_str)
                                .map(str::to_owned);
                            line.artifact = false;
                        }
                    }
                }
                assign_semantic_order_since(
                    pages,
                    &semantic_snapshot,
                    *semantic_order,
                    &block_id,
                    None,
                    None,
                );
                if !block_id.is_empty() {
                    page_map.push(PageMapEntry {
                        chapter_id: section_id.clone(),
                        block_id,
                        page_number: first_changed_page(pages, &semantic_snapshot),
                    });
                }
                previous_space_after = 0.0;
                continue;
            }
            let text = display_block_text(block);
            if text.trim().is_empty() && !block_type.eq_ignore_ascii_case("SceneBreak") {
                continue;
            }
            let style = block_style(document, block, trim);
            if style.page_break_before
                && pages
                    .last()
                    .is_some_and(|page| page.kind == PageKind::Body && !page.lines.is_empty())
            {
                previous_space_after = 0.0;
            }
            let language = block.get("language").and_then(Value::as_str);
            if block_type.eq_ignore_ascii_case("ListItem") {
                let content_runs = block_runs(document, block, &style);
                append_list_item_with_gap(
                    pages,
                    &text,
                    &content_runs,
                    trim,
                    &style,
                    language,
                    previous_space_after,
                );
            } else {
                let runs = display_block_runs(document, block, &style, &text);
                append_styled_runs_with_gap(
                    pages,
                    &text,
                    &runs,
                    trim,
                    &style,
                    language,
                    previous_space_after,
                );
            }
            previous_space_after = style.space_after;
            assign_semantic_order_since(
                pages,
                &semantic_snapshot,
                *semantic_order,
                &block_id,
                None,
                None,
            );
            if !block_id.is_empty() {
                page_map.push(PageMapEntry {
                    chapter_id: section_id.clone(),
                    block_id,
                    page_number: first_changed_page(pages, &semantic_snapshot),
                });
            }
        }
    }
    Ok(toc_index)
}

fn centered_page(title: &str, subtitle: &str, trim: &crate::model::Trim) -> LayoutPage {
    let width = trim.width_inches * 72.0;
    let height = trim.height_inches * 72.0;
    let mut lines = Vec::new();
    if !title.is_empty() {
        let runs = single_run(title, FontFace::SansBold);
        lines.push(LayoutLine {
            text: title.to_owned(),
            runs: runs.clone(),
            size: 22.0,
            x: width * 0.16,
            y: height * 0.62,
            baseline_offset_points: line_baseline_offset_points(22.0, FontFace::SansBold, &runs),
            word_spacing: 0.0,
            character_spacing: 0.0,
            rotation_degrees: 0.0,
            rotation_origin_x: None,
            rotation_origin_y: None,
            opacity: 1.0,
            light_text: false,
            fill_rgb: None,
            semantic_role: LayoutSemanticRole::Paragraph,
            artifact: true,
            language: None,
            reading_order: None,
            semantic_id: None,
            semantic_parent_id: None,
            source_start_utf16: None,
            source_end_utf16: None,
            link_page: None,
        });
    }
    if !subtitle.is_empty() {
        let runs = single_run(subtitle, FontFace::SerifRegular);
        lines.push(LayoutLine {
            text: subtitle.to_owned(),
            runs: runs.clone(),
            size: 11.0,
            x: width * 0.16,
            y: height * 0.54,
            baseline_offset_points: line_baseline_offset_points(
                11.0,
                FontFace::SerifRegular,
                &runs,
            ),
            word_spacing: 0.0,
            character_spacing: 0.0,
            rotation_degrees: 0.0,
            rotation_origin_x: None,
            rotation_origin_y: None,
            opacity: 1.0,
            light_text: false,
            fill_rgb: None,
            semantic_role: LayoutSemanticRole::Paragraph,
            artifact: false,
            language: None,
            reading_order: None,
            semantic_id: None,
            semantic_parent_id: None,
            source_start_utf16: None,
            source_end_utf16: None,
            link_page: None,
        });
    }
    LayoutPage {
        kind: PageKind::Body,
        width_points: None,
        height_points: None,
        lines,
        images: Vec::new(),
        shapes: Vec::new(),
        paint_order: Vec::new(),
        barcode_modules: None,
        page_label: None,
        bookmark: None,
    }
}

fn add_running_heads(pages: &mut [LayoutPage], title: &str, trim: &crate::model::Trim) {
    if title.is_empty() {
        return;
    }
    let x = trim.margin_inches * 72.0;
    let y = trim.height_inches * 72.0 - trim.margin_inches * 36.0;
    for page in pages
        .iter_mut()
        .skip(1)
        .filter(|page| page.kind == PageKind::Body)
    {
        let runs = single_run(title, FontFace::SansRegular);
        page.lines.push(LayoutLine {
            text: title.to_owned(),
            runs: runs.clone(),
            size: 8.0,
            x,
            y,
            baseline_offset_points: line_baseline_offset_points(8.0, FontFace::SansRegular, &runs),
            word_spacing: 0.0,
            character_spacing: 0.0,
            rotation_degrees: 0.0,
            rotation_origin_x: None,
            rotation_origin_y: None,
            opacity: 1.0,
            light_text: false,
            fill_rgb: None,
            semantic_role: LayoutSemanticRole::Paragraph,
            artifact: true,
            language: None,
            reading_order: None,
            semantic_id: None,
            semantic_parent_id: None,
            source_start_utf16: None,
            source_end_utf16: None,
            link_page: None,
        });
    }
}

fn build_toc_pages(
    entries: &[(String, usize)],
    body_start_page: usize,
    trim: &crate::model::Trim,
) -> Result<(Vec<LayoutPage>, bool), Diagnostic> {
    build_toc_pages_with_limit(entries, body_start_page, trim, 8)
}

fn build_toc_pages_with_limit(
    entries: &[(String, usize)],
    body_start_page: usize,
    trim: &crate::model::Trim,
    maximum_passes: usize,
) -> Result<(Vec<LayoutPage>, bool), Diagnostic> {
    let style = BlockStyle {
        size: trim.body_font_size_points,
        line_height: trim.body_line_height,
        indent: 0.0,
        right_indent: 0.0,
        first_line_indent: 0.0,
        page_break_before: false,
        keep_with_next: true,
        alignment: "left".to_owned(),
        face: FontFace::SerifRegular,
        font_weight: 400,
        small_caps: false,
        space_before: 0.0,
        space_after: 0.0,
        semantic_role: LayoutSemanticRole::Toc,
    };
    let available_width = (trim.width_inches - trim.margin_inches * 2.0) * 72.0;
    let mut previous_page_count = None;
    for _ in 0..maximum_passes {
        let mut pages = vec![toc_page(false, trim)];
        for (title, chapter_page) in entries {
            let folio = chapter_page.saturating_sub(body_start_page) + 1;
            let text = format!("{title}  ·  {folio}");
            let wrapped = wrap_layout_runs(
                &text,
                &single_run(&text, style.face),
                style.size,
                available_width,
            );
            let full_page_capacity = remaining_line_capacity(&toc_page(true, trim), trim, &style);
            if wrapped.len() > full_page_capacity {
                return Err(Diagnostic::error(
                    "PRESS_TOC_ENTRY_OVERFLOW",
                    format!("The contents entry '{title}' cannot fit within one page."),
                ));
            }
            if remaining_line_capacity(pages.last().expect("TOC page"), trim, &style)
                < wrapped.len()
            {
                pages.push(toc_page(true, trim));
            }
            for (line, runs) in wrapped {
                if remaining_line_capacity(pages.last().expect("TOC page"), trim, &style) == 0 {
                    pages.push(toc_page(true, trim));
                }
                let page = pages.last_mut().expect("TOC page");
                let y = next_baseline(page, trim, &style, false);
                let baseline_offset_points =
                    line_baseline_offset_points(style.size, style.face, &runs);
                page.lines.push(LayoutLine {
                    text: line,
                    runs,
                    size: style.size,
                    x: trim.margin_inches * 72.0,
                    y,
                    baseline_offset_points,
                    word_spacing: 0.0,
                    character_spacing: 0.0,
                    rotation_degrees: 0.0,
                    rotation_origin_x: None,
                    rotation_origin_y: None,
                    opacity: 1.0,
                    light_text: false,
                    fill_rgb: None,
                    semantic_role: LayoutSemanticRole::Toc,
                    artifact: false,
                    language: None,
                    reading_order: None,
                    semantic_id: None,
                    semantic_parent_id: None,
                    source_start_utf16: None,
                    source_end_utf16: None,
                    link_page: Some(*chapter_page),
                });
            }
        }
        if pages.len().is_multiple_of(2) {
            pages.push(LayoutPage {
                kind: PageKind::Blank,
                width_points: None,
                height_points: None,
                lines: Vec::new(),
                images: Vec::new(),
                shapes: Vec::new(),
                paint_order: Vec::new(),
                barcode_modules: None,
                page_label: None,
                bookmark: None,
            });
        }
        if previous_page_count == Some(pages.len()) {
            return Ok((pages, true));
        }
        previous_page_count = Some(pages.len());
    }
    Err(Diagnostic::error(
        "PRESS_TOC_NONCONVERGENT",
        "The table of contents did not converge after eight layout passes.",
    ))
}

fn toc_page(continued: bool, trim: &crate::model::Trim) -> LayoutPage {
    let size = 18.0;
    let text = if continued {
        "Contents (continued)"
    } else {
        "Contents"
    };
    let runs = single_run(text, FontFace::SansBold);
    LayoutPage {
        kind: PageKind::Body,
        width_points: None,
        height_points: None,
        lines: vec![LayoutLine {
            text: text.to_owned(),
            runs: runs.clone(),
            size,
            x: trim.margin_inches * 72.0,
            y: trim.height_inches * 72.0 - trim.margin_inches * 72.0 - size * 0.82,
            baseline_offset_points: line_baseline_offset_points(size, FontFace::SansBold, &runs),
            word_spacing: 0.0,
            character_spacing: 0.0,
            rotation_degrees: 0.0,
            rotation_origin_x: None,
            rotation_origin_y: None,
            opacity: 1.0,
            light_text: false,
            fill_rgb: None,
            semantic_role: LayoutSemanticRole::Paragraph,
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
        page_label: None,
        bookmark: None,
    }
}

fn wrap_layout_runs(
    text: &str,
    source_runs: &[LayoutRun],
    size: f32,
    available_width: f32,
) -> Vec<(String, Vec<LayoutRun>)> {
    let mut lines = Vec::new();
    let mut paragraph_offset = 0usize;
    for paragraph in text.split('\n') {
        if paragraph.is_empty() {
            lines.push(String::new());
            paragraph_offset = paragraph_offset.saturating_add(1);
            continue;
        }

        let maximum = paragraph.chars().count().max(4);
        let mut minimum = 4usize;
        let mut maximum_candidate = maximum;
        let mut fitted = wrap(paragraph, minimum);
        // Character averages are too conservative for the display fonts used by
        // Designed Pages. Find the widest Unicode-safe wrapping whose shaped
        // runs actually fit the authored frame instead.
        while minimum <= maximum_candidate {
            let candidate_limit = minimum + (maximum_candidate - minimum) / 2;
            let candidate = wrap(paragraph, candidate_limit);
            let mut candidate_offset = paragraph_offset;
            let fits = candidate.iter().all(|line| {
                let runs = runs_for_line(text, source_runs, line, &mut candidate_offset);
                measured_run_width(&runs, size) <= available_width + 0.01
            });
            if fits {
                fitted = candidate;
                minimum = candidate_limit.saturating_add(1);
            } else {
                maximum_candidate = candidate_limit - 1;
            }
        }
        lines.extend(fitted);
        paragraph_offset = paragraph_offset.saturating_add(paragraph.len() + 1);
    }

    let mut search_offset = 0;
    let runs = lines
        .iter()
        .map(|line| runs_for_line(text, source_runs, line, &mut search_offset))
        .collect::<Vec<_>>();
    lines.into_iter().zip(runs).collect()
}

fn wrapped_caption_with_runs(
    caption: &str,
    source_runs: &[LayoutRun],
    caption_style: &BlockStyle,
    width: f32,
) -> Vec<(String, Vec<LayoutRun>)> {
    let fallback_runs;
    let runs = if source_runs.is_empty() {
        fallback_runs = single_run(caption, caption_style.face);
        fallback_runs.as_slice()
    } else {
        source_runs
    };
    wrap_layout_runs(caption, runs, caption_style.size, width)
}

fn aligned_caption_x(origin_x: f32, width: f32, style: &BlockStyle, runs: &[LayoutRun]) -> f32 {
    let measured = measured_run_width(runs, style.size);
    origin_x
        + match style.alignment.as_str() {
            "center" => ((width - measured) / 2.0).max(0.0),
            "right" | "end" => (width - measured).max(0.0),
            _ => 0.0,
        }
}

fn caption_content_geometry(
    origin_x: f32,
    width: f32,
    style: &BlockStyle,
    horizontal_padding: f32,
) -> (f32, f32) {
    let left = style.indent.max(0.0);
    let right = style.right_indent.max(0.0);
    (
        origin_x + left + horizontal_padding,
        (width - left - right - horizontal_padding * 2.0).max(0.0),
    )
}

fn validate_caption_bounds(
    document: &Value,
    value: &Value,
    trim: &crate::model::Trim,
) -> Result<(), Diagnostic> {
    match value {
        Value::Array(values) => {
            for child in values {
                validate_caption_bounds(document, child, trim)?;
            }
        }
        Value::Object(values) => {
            if let Some(caption) = values.get("caption").and_then(Value::as_str)
                && !caption.is_empty()
                && trim.width_inches > trim.margin_inches * 2.0
                && trim.height_inches > trim.margin_inches * 2.0
            {
                let margin = trim.margin_inches * 72.0;
                let available_width = trim.width_inches * 72.0 - margin * 2.0;
                let available_height = trim.height_inches * 72.0 - margin * 2.0;
                let width_percent = values
                    .get("widthPercent")
                    .and_then(Value::as_f64)
                    .unwrap_or(100.0) as f32;
                let image_width = available_width * (width_percent / 100.0).clamp(0.1, 1.0);
                let caption_style = block_style(document, value, trim);
                let caption_runs = block_runs(document, value, &caption_style);
                let (_, caption_width) =
                    caption_content_geometry(0.0, image_width, &caption_style, 0.0);
                let lines = wrapped_caption_with_runs(
                    caption,
                    &caption_runs,
                    &caption_style,
                    caption_width,
                )
                .len();
                let caption_size = caption_style.size;
                let caption_step = caption_size * caption_style.line_height.max(1.0);
                let inline = values.contains_key("anchorPosition")
                    && !values
                        .get("startOnNewPage")
                        .and_then(Value::as_bool)
                        .unwrap_or(false);
                let caption_extent = (if inline { 6.0 } else { 8.0 })
                    + caption_style.space_before.max(0.0)
                    + caption_size * 1.12
                    + lines.saturating_sub(1) as f32 * caption_step
                    + caption_style.space_after.max(0.0);
                let fits = if inline {
                    let image_height = (available_height * 0.34).min(image_width * 1.25);
                    image_height + caption_extent <= available_height
                } else {
                    caption_extent + 72.0 <= available_height
                };
                if !fits {
                    return Err(Diagnostic::error(
                        "PRESS_CAPTION_OVERFLOW",
                        "An illustration caption cannot fit within its configured image width and page bounds.",
                    ));
                }
            }
            for child in values.values() {
                validate_caption_bounds(document, child, trim)?;
            }
        }
        _ => {}
    }
    Ok(())
}

#[derive(Debug, Clone)]
struct BlockStyle {
    size: f32,
    line_height: f32,
    indent: f32,
    right_indent: f32,
    first_line_indent: f32,
    page_break_before: bool,
    keep_with_next: bool,
    alignment: String,
    face: FontFace,
    font_weight: u16,
    small_caps: bool,
    space_before: f32,
    space_after: f32,
    semantic_role: LayoutSemanticRole,
}

impl BlockStyle {
    fn body(trim: &crate::model::Trim) -> Self {
        let defaults = &typography::defaults().body;
        Self {
            size: trim.body_font_size_points,
            line_height: trim.body_line_height,
            indent: 0.0,
            right_indent: 0.0,
            first_line_indent: 0.0,
            page_break_before: false,
            keep_with_next: false,
            alignment: normalized_alignment(&defaults.text_align),
            face: regular_face(font_family(&defaults.font_family_key))
                .with_weight(defaults.font_weight, defaults.italic),
            font_weight: defaults.font_weight,
            small_caps: false,
            space_before: 0.0,
            space_after: defaults.space_after_points,
            semantic_role: LayoutSemanticRole::Paragraph,
        }
    }

    fn caption(_trim: &crate::model::Trim) -> Self {
        let defaults = &typography::defaults().caption;
        Self {
            size: defaults.font_size_points,
            line_height: defaults.line_height,
            indent: 0.0,
            right_indent: 0.0,
            first_line_indent: 0.0,
            page_break_before: false,
            keep_with_next: false,
            alignment: normalized_alignment(&defaults.text_align),
            face: regular_face(font_family(&defaults.font_family_key))
                .with_weight(defaults.font_weight, defaults.italic),
            font_weight: defaults.font_weight,
            small_caps: false,
            space_before: defaults.space_before_points,
            space_after: defaults.space_after_points,
            semantic_role: LayoutSemanticRole::Caption,
        }
    }

    fn chapter_heading(_trim: &crate::model::Trim) -> Self {
        let defaults = &typography::defaults().chapter_heading;
        Self {
            size: defaults.font_size_points,
            line_height: defaults.line_height,
            indent: 0.0,
            right_indent: 0.0,
            first_line_indent: 0.0,
            page_break_before: false,
            keep_with_next: true,
            alignment: normalized_alignment(&defaults.text_align),
            face: regular_face(font_family(&defaults.font_family_key))
                .with_weight(defaults.font_weight, defaults.italic),
            font_weight: defaults.font_weight,
            small_caps: false,
            space_before: defaults.space_before_points,
            space_after: defaults.space_after_points,
            semantic_role: LayoutSemanticRole::Heading1,
        }
    }
}

fn update_style_face(
    style: &mut BlockStyle,
    family: Option<FontFamily>,
    weight: Option<u16>,
    italic: Option<bool>,
) {
    let current = style.face;
    style.font_weight = weight.unwrap_or(style.font_weight);
    style.face = regular_face(family.unwrap_or_else(|| current.family())).with_weight(
        style.font_weight,
        italic.unwrap_or_else(|| is_italic(current)),
    );
}

fn normalized_alignment(value: &str) -> String {
    match value.to_ascii_lowercase().as_str() {
        "start" => "left".to_owned(),
        "end" => "right".to_owned(),
        normalized => normalized.to_owned(),
    }
}

fn append_styled_text(
    pages: &mut Vec<LayoutPage>,
    text: &str,
    trim: &crate::model::Trim,
    style: &BlockStyle,
) -> usize {
    let runs = vec![LayoutRun {
        text: text.to_owned(),
        face: style.face,
        underline: false,
        strikethrough: false,
        baseline_shift_em: 0.0,
        size_scale: 1.0,
        language: None,
    }];
    append_styled_runs(pages, text, &runs, trim, style, None)
}

fn append_styled_runs(
    pages: &mut Vec<LayoutPage>,
    text: &str,
    source_runs: &[LayoutRun],
    trim: &crate::model::Trim,
    style: &BlockStyle,
    language: Option<&str>,
) -> usize {
    append_styled_runs_with_gap(pages, text, source_runs, trim, style, language, 0.0)
}

fn append_list_item_with_gap(
    pages: &mut Vec<LayoutPage>,
    display_text: &str,
    content_runs: &[LayoutRun],
    trim: &crate::model::Trim,
    style: &BlockStyle,
    language: Option<&str>,
    previous_space_after: f32,
) -> usize {
    let bullet = typography::defaults().list_item.bullet.as_str();
    let content_text = display_text.strip_prefix(bullet).unwrap_or(display_text);
    let mut content_style = style.clone();
    content_style.first_line_indent = 0.0;
    let snapshot = pages
        .iter()
        .map(|page| page.lines.len())
        .collect::<Vec<_>>();
    let first_page = append_styled_runs_with_gap(
        pages,
        content_text,
        content_runs,
        trim,
        &content_style,
        language,
        previous_space_after,
    );
    let page_index = first_page.saturating_sub(1);
    let Some(page) = pages.get_mut(page_index) else {
        return first_page;
    };
    let line_start = snapshot.get(page_index).copied().unwrap_or_default();
    let Some(content_index) = page
        .lines
        .iter()
        .enumerate()
        .skip(line_start)
        .find_map(|(index, line)| (!line.artifact).then_some(index))
    else {
        return first_page;
    };
    let content_line = &page.lines[content_index];
    let marker_runs = single_run(bullet, style.face);
    let marker_line = LayoutLine {
        text: bullet.to_owned(),
        runs: marker_runs.clone(),
        size: style.size,
        x: content_line.x + style.first_line_indent,
        y: content_line.y,
        baseline_offset_points: line_baseline_offset_points(style.size, style.face, &marker_runs),
        word_spacing: 0.0,
        character_spacing: 0.0,
        rotation_degrees: 0.0,
        rotation_origin_x: None,
        rotation_origin_y: None,
        opacity: 1.0,
        light_text: false,
        fill_rgb: None,
        semantic_role: LayoutSemanticRole::ListItem,
        artifact: true,
        language: None,
        reading_order: None,
        semantic_id: None,
        semantic_parent_id: None,
        source_start_utf16: None,
        source_end_utf16: None,
        link_page: None,
    };
    page.lines.insert(content_index, marker_line);
    first_page
}

fn line_baseline_offset_points(size: f32, fallback_face: FontFace, runs: &[LayoutRun]) -> f32 {
    if runs.is_empty() {
        return crate::font::descent_points(fallback_face, size);
    }
    runs.iter().fold(0.0, |maximum, run| {
        let effective_size = size * run.size_scale;
        let effective_descent = crate::font::descent_points(run.face, effective_size)
            - effective_size * run.baseline_shift_em;
        maximum.max(effective_descent)
    })
}

fn append_styled_runs_with_gap(
    pages: &mut Vec<LayoutPage>,
    text: &str,
    source_runs: &[LayoutRun],
    trim: &crate::model::Trim,
    style: &BlockStyle,
    language: Option<&str>,
    previous_space_after: f32,
) -> usize {
    if text.is_empty() {
        return pages.len().max(1);
    }
    let default_x = trim.margin_inches * 72.0 + style.indent;
    let default_width =
        (trim.width_inches - 2.0 * trim.margin_inches) * 72.0 - style.indent - style.right_indent;
    let flow_region = pages
        .last()
        .and_then(|page| active_float_region(page, trim, style, previous_space_after));
    let available_width = flow_region.map_or(default_width, |region| region.1);
    let flow_x = flow_region.map_or(default_x, |region| region.0);
    let mut wrapped = wrap_layout_runs(text, source_runs, style.size, available_width);
    let mut float_line_count = 0usize;
    if let Some((_, _, float_bottom)) = flow_region {
        let baseline = pages.last().map_or(0.0, |page| {
            next_flow_baseline_with_gap(page, trim, style, true, previous_space_after)
        });
        let step = style.size * style.line_height.max(1.0);
        let capacity =
            (((baseline - float_bottom) / step).ceil().max(0.0) as usize).min(wrapped.len());
        if capacity < wrapped.len() {
            let mut consumed = 0usize;
            for (line, _) in wrapped.iter().take(capacity) {
                consumed = consumed_text_offset(text, consumed, line);
            }
            let remainder = text.get(consumed..).unwrap_or("").trim_start();
            let skipped = text
                .get(consumed..)
                .map_or(0, |tail| tail.len() - tail.trim_start().len());
            consumed += skipped;
            let remainder_runs = slice_layout_runs(source_runs, consumed);
            let mut full_width =
                wrap_layout_runs(remainder, &remainder_runs, style.size, default_width);
            wrapped.truncate(capacity);
            wrapped.append(&mut full_width);
        }
        float_line_count = capacity;
    }
    let (lines, wrapped_runs): (Vec<_>, Vec<_>) = wrapped.into_iter().unzip();
    let mut offset = 0;
    let mut first_page = None;
    if style.page_break_before
        && pages
            .last()
            .is_some_and(|page| page.kind == PageKind::Body && !page.lines.is_empty())
    {
        pages.push(empty_body_page());
    }
    if style.keep_with_next
        && pages.last().is_some_and(|page| {
            page.kind == PageKind::Body && remaining_line_capacity(page, trim, style) < 3
        })
    {
        pages.push(empty_body_page());
    }
    while offset < lines.len() {
        let gap_for_first = if offset == 0 {
            previous_space_after
        } else {
            0.0
        };
        let remaining_capacity = pages.last().map_or(0, |page| {
            if page.kind == PageKind::Body {
                remaining_line_capacity_with_gap(page, trim, style, gap_for_first)
            } else {
                0
            }
        });
        let remaining_lines = lines.len() - offset;
        if remaining_capacity == 0
            || (remaining_lines > remaining_capacity
                && remaining_capacity < trim.minimum_orphan_lines)
        {
            pages.push(empty_body_page());
        }
        let page_capacity = remaining_line_capacity_with_gap(
            pages.last().expect("body page"),
            trim,
            style,
            gap_for_first,
        );
        let mut take = remaining_lines.min(page_capacity);
        let following = remaining_lines - take;
        if following > 0 && following < trim.minimum_widow_lines {
            let move_to_next = trim.minimum_widow_lines - following;
            if take.saturating_sub(move_to_next) >= trim.minimum_orphan_lines {
                take -= move_to_next;
            } else {
                pages.push(empty_body_page());
                take = remaining_line_capacity_with_gap(
                    pages.last().expect("body page"),
                    trim,
                    style,
                    gap_for_first,
                )
                .min(remaining_lines);
            }
        }
        let page_number = pages.len();
        let page = pages.last_mut().expect("body page");
        first_page.get_or_insert(page_number);
        for (relative_index, line) in lines[offset..offset + take].iter().enumerate() {
            let line_runs = wrapped_runs[offset + relative_index].clone();
            let uses_float = offset + relative_index < float_line_count
                && page_number == first_page.unwrap_or(page_number);
            let line_width = if uses_float {
                available_width
            } else {
                default_width
            };
            let mut line_x = if uses_float { flow_x } else { default_x };
            if offset == 0 && relative_index == 0 && !uses_float {
                line_x += style.first_line_indent;
            }
            let estimated_width = measured_run_width(&line_runs, style.size);
            let spaces = line.chars().filter(|character| *character == ' ').count();
            let is_final_line = offset + relative_index + 1 == lines.len();
            let word_spacing = if style.alignment == "justify"
                && !is_final_line
                && spaces > 0
                && estimated_width < line_width
            {
                ((line_width - estimated_width) / spaces as f32).clamp(0.0, style.size * 0.25)
            } else {
                0.0
            };
            let alignment_offset = match style.alignment.as_str() {
                "center" => ((line_width - estimated_width) / 2.0).max(0.0),
                "right" => (line_width - estimated_width).max(0.0),
                _ => 0.0,
            };
            let y = if offset == 0 && relative_index == 0 {
                next_flow_baseline_with_gap(page, trim, style, true, previous_space_after)
            } else {
                next_baseline(page, trim, style, false)
            };
            let baseline_offset_points =
                line_baseline_offset_points(style.size, style.face, &line_runs);
            page.lines.push(LayoutLine {
                text: line.clone(),
                runs: line_runs,
                size: style.size,
                x: line_x + alignment_offset,
                y,
                baseline_offset_points,
                word_spacing,
                character_spacing: 0.0,
                rotation_degrees: 0.0,
                rotation_origin_x: None,
                rotation_origin_y: None,
                opacity: 1.0,
                light_text: false,
                fill_rgb: None,
                semantic_role: style.semantic_role,
                artifact: false,
                language: language.map(str::to_owned),
                reading_order: None,
                semantic_id: None,
                semantic_parent_id: None,
                source_start_utf16: None,
                source_end_utf16: None,
                link_page: None,
            });
        }
        offset += take;
    }
    // Paragraph after-spacing is applied as baseline advance on the next block
    // via next_flow_baseline/space_before, not as a synthetic artifact line.

    first_page.unwrap_or_else(|| pages.len().max(1))
}

fn active_float_region(
    page: &LayoutPage,
    trim: &crate::model::Trim,
    style: &BlockStyle,
    previous_space_after: f32,
) -> Option<(f32, f32, f32)> {
    let baseline = next_flow_baseline_with_gap(page, trim, style, true, previous_space_after);
    let margin = trim.margin_inches * 72.0;
    let right = trim.width_inches * 72.0 - margin;
    let gutter = 8.0;
    page.images.iter().rev().find_map(|image| {
        let wrap = image.text_wrap.as_deref()?;
        if baseline <= image.y || baseline >= image.y + image.height {
            return None;
        }
        match wrap {
            "Start" if image.x - gutter > margin => Some((
                margin + style.indent,
                image.x - gutter - margin - style.indent,
                image.y,
            )),
            "End" if image.x + image.width + gutter < right => Some((
                image.x + image.width + gutter,
                right - image.x - image.width - gutter,
                image.y,
            )),
            _ => None,
        }
    })
}

fn consumed_text_offset(full_text: &str, search_offset: usize, line: &str) -> usize {
    let searchable = line.strip_suffix('-').unwrap_or(line);
    full_text
        .get(search_offset..)
        .and_then(|remaining| {
            remaining
                .find(searchable)
                .map(|relative| search_offset + relative + searchable.len())
        })
        .unwrap_or(search_offset)
}

fn slice_layout_runs(source_runs: &[LayoutRun], offset: usize) -> Vec<LayoutRun> {
    let mut cursor = 0usize;
    let mut output = Vec::new();
    for run in source_runs {
        let end = cursor + run.text.len();
        if end > offset {
            let start = offset.saturating_sub(cursor);
            if let Some(text) = run.text.get(start..) {
                output.push(LayoutRun {
                    text: text.to_owned(),
                    ..run.clone()
                });
            }
        }
        cursor = end;
    }
    output
}

fn next_flow_baseline(
    page: &LayoutPage,
    trim: &crate::model::Trim,
    style: &BlockStyle,
    first_block_line: bool,
) -> f32 {
    next_flow_baseline_with_gap(page, trim, style, first_block_line, 0.0)
}

fn next_flow_baseline_with_gap(
    page: &LayoutPage,
    trim: &crate::model::Trim,
    style: &BlockStyle,
    first_block_line: bool,
    previous_space_after: f32,
) -> f32 {
    let has_content = page
        .lines
        .iter()
        .any(|line| !line.artifact && line.semantic_role != LayoutSemanticRole::Caption);
    let gap = if first_block_line && has_content {
        previous_space_after.max(style.space_before)
    } else {
        0.0
    };
    let previous = page
        .lines
        .iter()
        .rev()
        .find(|line| line.semantic_role != LayoutSemanticRole::Caption);
    previous.map_or(
        trim.height_inches * 72.0 - trim.margin_inches * 72.0 - style.size * 0.82 - gap,
        |line| {
            let step = (line.size * style.line_height.max(1.0))
                .max(style.size * style.line_height.max(1.0));
            line.y - step - gap
        },
    )
}

fn next_baseline(
    page: &LayoutPage,
    trim: &crate::model::Trim,
    style: &BlockStyle,
    first_block_line: bool,
) -> f32 {
    next_flow_baseline(page, trim, style, first_block_line)
}

fn remaining_line_capacity(
    page: &LayoutPage,
    trim: &crate::model::Trim,
    style: &BlockStyle,
) -> usize {
    remaining_line_capacity_with_gap(page, trim, style, 0.0)
}

fn remaining_line_capacity_with_gap(
    page: &LayoutPage,
    trim: &crate::model::Trim,
    style: &BlockStyle,
    previous_space_after: f32,
) -> usize {
    let bottom = trim.margin_inches * 72.0;
    let step = style.size * style.line_height.max(1.0);
    let first = next_flow_baseline_with_gap(page, trim, style, true, previous_space_after);
    if first - style.size * 0.30 < bottom {
        return 0;
    }
    (((first - style.size * 0.30 - bottom) / step).floor() as usize).saturating_add(1)
}

fn measured_run_width(runs: &[LayoutRun], size: f32) -> f32 {
    runs.iter()
        .map(|run| measure_text(run.face, &run.text, size * run.size_scale))
        .sum()
}

fn clip_layout_runs_to_width(
    runs: &[LayoutRun],
    size: f32,
    character_spacing: f32,
    maximum_width: f32,
) -> (String, Vec<LayoutRun>) {
    let mut output = Vec::new();
    'runs: for source in runs {
        let mut clipped = source.clone();
        clipped.text.clear();
        for character in source.text.chars() {
            clipped.text.push(character);
            let character_count = output
                .iter()
                .map(|run: &LayoutRun| run.text.chars().count())
                .sum::<usize>()
                + clipped.text.chars().count();
            let width = measured_run_width(&output, size)
                + measure_text(clipped.face, &clipped.text, size * clipped.size_scale)
                + character_spacing * character_count.saturating_sub(1) as f32;
            if width > maximum_width + 0.01 {
                clipped.text.pop();
                if !clipped.text.is_empty() {
                    output.push(clipped);
                }
                break 'runs;
            }
        }
        if !clipped.text.is_empty() {
            output.push(clipped);
        }
    }
    let text = output.iter().map(|run| run.text.as_str()).collect();
    (text, output)
}

fn runs_for_line(
    full_text: &str,
    source_runs: &[LayoutRun],
    line: &str,
    search_offset: &mut usize,
) -> Vec<LayoutRun> {
    let searchable = line.strip_suffix('-').unwrap_or(line);
    let relative = full_text
        .get(*search_offset..)
        .and_then(|remaining| remaining.find(searchable))
        .unwrap_or(0);
    let line_start = *search_offset + relative;
    let line_end = line_start + searchable.len();
    *search_offset = line_end;
    let mut output = Vec::new();
    let mut run_start = 0;
    for run in source_runs {
        let run_end = run_start + run.text.len();
        let start = line_start.max(run_start);
        let end = line_end.min(run_end);
        if start < end
            && let Some(fragment) = run.text.get(start - run_start..end - run_start)
        {
            let mut fragment = fragment.to_owned();
            if line.ends_with('-') && end == line_end {
                fragment.push('-');
            }
            output.push(LayoutRun {
                text: fragment,
                ..run.clone()
            });
        }
        run_start = run_end;
    }
    if output.is_empty() && !line.is_empty() {
        output.push(LayoutRun {
            text: line.to_owned(),
            face: FontFace::SerifRegular,
            underline: false,
            strikethrough: false,
            baseline_shift_em: 0.0,
            size_scale: 1.0,
            language: None,
        });
    }
    output
}

fn empty_body_page() -> LayoutPage {
    LayoutPage {
        kind: PageKind::Body,
        width_points: None,
        height_points: None,
        lines: Vec::new(),
        images: Vec::new(),
        shapes: Vec::new(),
        paint_order: Vec::new(),
        barcode_modules: None,
        page_label: None,
        bookmark: None,
    }
}

fn is_blockquote_role(block: &Value) -> bool {
    string(block, "styleRole").eq_ignore_ascii_case("block-quote")
}

fn normalize_paint_orders(pages: &mut [LayoutPage]) {
    for page in pages {
        let has_manuscript_decoration = page.shapes.iter().any(|shape| shape.semantic_id.is_some());
        if !page.paint_order.is_empty() && !has_manuscript_decoration {
            continue;
        }
        let overlay_shapes = page
            .shapes
            .iter()
            .enumerate()
            .filter(|(_, shape)| {
                shape
                    .semantic_id
                    .as_deref()
                    .is_some_and(|id| id.ends_with(":caption-background"))
            })
            .map(|(index, _)| LayoutPaint::Shape(index));
        let other_shapes = page
            .shapes
            .iter()
            .enumerate()
            .filter(|(_, shape)| {
                !shape
                    .semantic_id
                    .as_deref()
                    .is_some_and(|id| id.ends_with(":caption-background"))
            })
            .map(|(index, _)| LayoutPaint::Shape(index));
        let images = page
            .images
            .iter()
            .enumerate()
            .map(|(index, _)| LayoutPaint::Image(index));
        let lines = page
            .lines
            .iter()
            .enumerate()
            .map(|(index, _)| LayoutPaint::Line(index));
        page.paint_order = if page.shapes.iter().any(|shape| {
            shape
                .semantic_id
                .as_deref()
                .is_some_and(|id| id.ends_with(":caption-background"))
        }) {
            images
                .chain(overlay_shapes)
                .chain(other_shapes)
                .chain(lines)
                .collect()
        } else {
            other_shapes.chain(images).chain(lines).collect()
        };
    }
}

fn set_line_color_since(
    pages: &mut [LayoutPage],
    snapshot: &[(usize, usize)],
    color: Option<[f32; 3]>,
) {
    for (page_index, page) in pages.iter_mut().enumerate() {
        let line_start = snapshot.get(page_index).map_or(0, |entry| entry.0);
        for line in page.lines.iter_mut().skip(line_start) {
            if !line.artifact {
                line.fill_rgb = color;
            }
        }
    }
}

fn append_blockquote_decorations(
    pages: &mut [LayoutPage],
    snapshot: &[(usize, usize)],
    trim: &crate::model::Trim,
    style: &BlockStyle,
    semantic_id: &str,
) {
    let defaults = &typography::defaults().blockquote;
    let decoration = defaults
        .decoration
        .as_ref()
        .expect("blockquote decoration defaults");
    let rule_width = decoration.rule_width_em * style.size;
    let rule_gap = decoration.rule_gap_em * style.size;
    let outer_indent = (style.indent - rule_width - rule_gap).max(0.0);
    let rule_x = trim.margin_inches * 72.0 + outer_indent;
    for (page_index, page) in pages.iter_mut().enumerate() {
        let line_start = snapshot.get(page_index).map_or(0, |entry| entry.0);
        let quote_lines = page
            .lines
            .iter()
            .skip(line_start)
            .filter(|line| !line.artifact)
            .collect::<Vec<_>>();
        if quote_lines.is_empty() {
            continue;
        }
        let top = quote_lines
            .iter()
            .map(|line| line.y + line.size * 0.82)
            .fold(f32::NEG_INFINITY, f32::max);
        let bottom = quote_lines
            .iter()
            .map(|line| line.y - line.size * (style.line_height.max(1.0) - 0.82))
            .fold(f32::INFINITY, f32::min);
        let shape_index = page.shapes.len();
        page.shapes.push(LayoutShape {
            kind: LayoutShapeKind::Rectangle,
            x: rule_x,
            y: bottom,
            width: rule_width,
            height: (top - bottom).max(rule_width),
            fill_rgb: Some(decoration.rule_color_rgb),
            stroke_rgb: None,
            stroke_width: 0.0,
            opacity: 1.0,
            rotation_degrees: 0.0,
            semantic_id: Some(format!("{semantic_id}:rule")),
            semantic_parent_id: Some(semantic_id.to_owned()),
        });
        page.paint_order.push(LayoutPaint::Shape(shape_index));
    }
}

fn block_style(document: &Value, block: &Value, trim: &crate::model::Trim) -> BlockStyle {
    let block_type = string(block, "type");
    let style_role = string(block, "styleRole");
    let is_blockquote_role = style_role.eq_ignore_ascii_case("block-quote");
    let mut blockquote_left_indent_em = None;
    let mut style = if block_type.eq_ignore_ascii_case("Figure") {
        BlockStyle::caption(trim)
    } else {
        BlockStyle::body(trim)
    };
    match block_type.to_ascii_lowercase().as_str() {
        "heading" => {
            let level = block
                .get("headingLevel")
                .and_then(Value::as_u64)
                .unwrap_or(2)
                .clamp(1, 6) as u8;
            let defaults = typography::heading(level);
            style.size = defaults.font_size_points;
            style.line_height = defaults.line_height;
            style.keep_with_next = true;
            style.alignment = normalized_alignment(&defaults.text_align);
            style.face = regular_face(font_family(&defaults.font_family_key))
                .with_weight(defaults.font_weight, defaults.italic);
            style.font_weight = defaults.font_weight;
            style.space_before = defaults.space_before_points;
            style.space_after = defaults.space_after_points;
            style.semantic_role = match block
                .get("headingLevel")
                .and_then(Value::as_u64)
                .unwrap_or(2)
            {
                1 => LayoutSemanticRole::Heading1,
                2 => LayoutSemanticRole::Heading2,
                3 => LayoutSemanticRole::Heading3,
                4 => LayoutSemanticRole::Heading4,
                5 => LayoutSemanticRole::Heading5,
                _ => LayoutSemanticRole::Heading6,
            };
        }
        "blockquote" => {
            let defaults = &typography::defaults().blockquote;
            style.size = trim.body_font_size_points;
            style.line_height = trim.body_line_height;
            style.face = regular_face(font_family(&defaults.font_family_key))
                .with_weight(defaults.font_weight, defaults.italic);
            style.font_weight = defaults.font_weight;
            blockquote_left_indent_em = Some(defaults.left_indent_em);
            style.indent = defaults.left_indent_em * style.size;
            style.alignment = normalized_alignment(&defaults.text_align);
            style.space_before = defaults.space_before_points;
            style.space_after = defaults.space_after_points;
        }
        "scenebreak" => {
            let defaults = &typography::defaults().scene_break;
            style.alignment = normalized_alignment(&defaults.text_align);
            style.keep_with_next = true;
            style.space_before = defaults.space_before_points;
            style.space_after = defaults.space_after_points;
        }
        "listitem" => {
            let defaults = &typography::defaults().list_item;
            style.alignment = normalized_alignment(&defaults.text_align);
            style.semantic_role = LayoutSemanticRole::ListItem;
            style.indent = defaults.left_indent_em * style.size;
            style.first_line_indent = -defaults.hanging_indent_em * style.size;
            style.space_before = defaults.space_before_points;
            style.space_after = defaults.space_after_points;
        }
        _ => {}
    }
    if is_blockquote_role {
        let defaults = &typography::defaults().blockquote;
        blockquote_left_indent_em = Some(defaults.left_indent_em);
        style.size = trim.body_font_size_points;
        style.line_height = trim.body_line_height;
        style.face = regular_face(font_family(&defaults.font_family_key))
            .with_weight(defaults.font_weight, defaults.italic);
        style.font_weight = defaults.font_weight;
        style.alignment = normalized_alignment(&defaults.text_align);
        style.space_before = defaults.space_before_points;
        style.space_after = defaults.space_after_points;
        style.indent = defaults.left_indent_em * style.size;
    }
    if style_role.eq_ignore_ascii_case("chapter-heading") {
        let defaults = &typography::defaults().chapter_heading;
        style.size = defaults.font_size_points;
        style.line_height = defaults.line_height;
        style.face = regular_face(font_family(&defaults.font_family_key))
            .with_weight(defaults.font_weight, defaults.italic);
        style.font_weight = defaults.font_weight;
        style.alignment = normalized_alignment(&defaults.text_align);
        style.space_before = defaults.space_before_points;
        style.space_after = defaults.space_after_points;
        style.keep_with_next = true;
    }
    if let Some(definition) = document
        .get("styles")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .find(|candidate| string(candidate, "semanticRole") == style_role)
        .and_then(|candidate| candidate.get("definition"))
    {
        if let Some(size) = definition.get("fontSizePoints").and_then(Value::as_f64) {
            style.size = size as f32;
        }
        if let Some(line_height) = definition.get("lineHeight").and_then(Value::as_f64) {
            style.line_height = line_height as f32;
        }
        if let Some(keep) = definition.get("keepWithNext").and_then(Value::as_bool) {
            style.keep_with_next = keep;
        }
        if let Some(alignment) = definition.get("textAlign").and_then(Value::as_str) {
            style.alignment = normalized_alignment(alignment);
        }
        let family = definition
            .get("fontFamilyKey")
            .or_else(|| definition.get("fontFamily"))
            .and_then(Value::as_str)
            .map(font_family);
        let weight = definition
            .get("fontWeight")
            .and_then(Value::as_u64)
            .map(|weight| weight.clamp(100, 900) as u16);
        let italic = definition.get("italic").and_then(Value::as_bool);
        update_style_face(&mut style, family, weight, italic);
        if let Some(small_caps) = definition.get("smallCaps").and_then(Value::as_bool) {
            style.small_caps = small_caps;
        }
        if let Some(value) = definition.get("spaceBeforePoints").and_then(Value::as_f64) {
            style.space_before = value as f32;
        }
        if let Some(value) = definition.get("spaceAfterPoints").and_then(Value::as_f64) {
            style.space_after = value as f32;
        }
        if let Some(value) = definition.get("leftIndentEm").and_then(Value::as_f64) {
            if block_type.eq_ignore_ascii_case("blockquote") || is_blockquote_role {
                blockquote_left_indent_em = Some(value as f32);
            } else {
                style.indent = value as f32 * style.size;
            }
        }
        style.right_indent = definition
            .get("rightIndentEm")
            .and_then(Value::as_f64)
            .map_or(style.right_indent, |value| value as f32 * style.size);
        style.first_line_indent = definition
            .get("firstLineIndentEm")
            .and_then(Value::as_f64)
            .map_or(style.first_line_indent, |value| value as f32 * style.size);
        style.page_break_before = definition
            .get("startOnNewPage")
            .and_then(Value::as_bool)
            .unwrap_or(style.page_break_before);
    }
    if let Some(presentation) = block
        .get("paragraphPresentation")
        .filter(|value| !value.is_null())
    {
        if let Some(size) = presentation.get("fontSizePoints").and_then(Value::as_f64) {
            style.size = size as f32;
        }
        if let Some(line_height) = presentation.get("lineHeight").and_then(Value::as_f64) {
            style.line_height = line_height as f32;
        }
        let direct_family = presentation
            .get("fontFamilyKey")
            .and_then(Value::as_str)
            .map(font_family);
        let direct_weight = presentation
            .get("fontWeight")
            .and_then(Value::as_u64)
            .map(|weight| weight.clamp(100, 900) as u16);
        let direct_italic = presentation.get("italic").and_then(Value::as_bool);
        if direct_family.is_some() || direct_weight.is_some() || direct_italic.is_some() {
            update_style_face(&mut style, direct_family, direct_weight, direct_italic);
        }
        if let Some(small_caps) = presentation.get("smallCaps").and_then(Value::as_bool) {
            style.small_caps = small_caps;
        }
        if let Some(alignment) = presentation.get("alignment").and_then(Value::as_str) {
            style.alignment = match alignment.to_ascii_lowercase().as_str() {
                "start" => "left",
                "end" => "right",
                other => other,
            }
            .to_owned();
        }
        if let Some(value) = presentation.get("leftIndentEm").and_then(Value::as_f64) {
            if block_type.eq_ignore_ascii_case("blockquote") || is_blockquote_role {
                blockquote_left_indent_em = Some(value as f32);
            } else {
                style.indent = value as f32 * style.size;
            }
        }
        style.right_indent = presentation
            .get("rightIndentEm")
            .and_then(Value::as_f64)
            .map_or(style.right_indent, |value| value as f32 * style.size);
        style.first_line_indent = presentation
            .get("firstLineIndentEm")
            .and_then(Value::as_f64)
            .map_or(style.first_line_indent, |value| value as f32 * style.size);
        style.space_before = presentation
            .get("spacingBeforePoints")
            .and_then(Value::as_f64)
            .unwrap_or(style.space_before as f64) as f32;
        style.space_after = presentation
            .get("spacingAfterPoints")
            .and_then(Value::as_f64)
            .unwrap_or(style.space_after as f64) as f32;
        style.keep_with_next = presentation
            .get("keepWithNext")
            .and_then(Value::as_bool)
            .unwrap_or(style.keep_with_next);
        style.page_break_before = presentation
            .get("startOnNewPage")
            .and_then(Value::as_bool)
            .unwrap_or(style.page_break_before);
    }
    if let Some(left_indent_em) = blockquote_left_indent_em {
        let decoration = typography::defaults()
            .blockquote
            .decoration
            .as_ref()
            .expect("blockquote decoration defaults");
        style.indent =
            left_indent_em.max(decoration.rule_width_em + decoration.rule_gap_em) * style.size;
    }
    style
}

fn block_runs(document: &Value, block: &Value, style: &BlockStyle) -> Vec<LayoutRun> {
    block
        .get("content")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter_map(|span| {
            let text = span.get("text").and_then(Value::as_str)?.to_owned();
            let marks = span
                .get("marks")
                .and_then(Value::as_array)
                .cloned()
                .unwrap_or_default();
            let has = |kind: &str| {
                marks
                    .iter()
                    .any(|mark| string(mark, "type").eq_ignore_ascii_case(kind))
            };
            let is_code = has("Code");
            let mut weight = if has("Strong") {
                700
            } else {
                style.font_weight
            };
            let mut italic = has("Emphasis") || is_italic(style.face);
            let mut family = if is_code {
                FontFamily::Mono
            } else {
                style.face.family()
            };
            let character_definition = marks
                .iter()
                .find(|mark| string(mark, "type").eq_ignore_ascii_case("CharacterStyle"))
                .and_then(|mark| mark.get("value").and_then(Value::as_str))
                .and_then(|role| {
                    document
                        .get("styles")
                        .and_then(Value::as_array)
                        .into_iter()
                        .flatten()
                        .find(|candidate| {
                            string(candidate, "kind").eq_ignore_ascii_case("Character")
                                && string(candidate, "semanticRole").eq_ignore_ascii_case(role)
                        })
                        .and_then(|candidate| candidate.get("definition"))
                });
            if let Some(definition) = character_definition {
                if let Some(value) = definition
                    .get("fontFamilyKey")
                    .or_else(|| definition.get("fontFamily"))
                    .and_then(Value::as_str)
                {
                    family = font_family(value);
                }
                if let Some(value) = definition.get("fontWeight").and_then(Value::as_u64) {
                    weight = value.clamp(100, 900) as u16;
                }
                if let Some(value) = definition.get("italic").and_then(Value::as_bool) {
                    italic = value;
                }
            }
            let character_small_caps = character_definition
                .and_then(|definition| definition.get("smallCaps"))
                .and_then(Value::as_bool)
                .unwrap_or(false);
            let small_caps = style.small_caps || has("SmallCaps") || character_small_caps;
            let superscript = has("Superscript");
            let subscript = has("Subscript");
            let language = marks
                .iter()
                .find(|mark| string(mark, "type").eq_ignore_ascii_case("Language"))
                .and_then(|mark| mark.get("value").and_then(Value::as_str))
                .map(str::to_owned);
            let inline_defaults = &typography::defaults().inline;
            let (baseline_shift_em, inline_scale) = if superscript {
                (
                    inline_defaults.superscript.baseline_shift_em,
                    inline_defaults.superscript.size_scale,
                )
            } else if subscript {
                (
                    inline_defaults.subscript.baseline_shift_em,
                    inline_defaults.subscript.size_scale,
                )
            } else {
                (0.0, 1.0)
            };
            let run = LayoutRun {
                text,
                face: regular_face(family).with_weight(weight, italic),
                underline: has("Underline") || has("Link"),
                strikethrough: has("Strikethrough"),
                baseline_shift_em,
                size_scale: character_definition
                    .and_then(|definition| definition.get("fontSizePoints"))
                    .and_then(Value::as_f64)
                    .map_or(1.0, |size| size as f32 / style.size)
                    * inline_scale,
                language,
            };
            if small_caps {
                Some(synthetic_small_caps(
                    run,
                    typography::defaults().inline.small_caps.lowercase_scale,
                ))
            } else {
                Some(vec![run])
            }
        })
        .flatten()
        .collect()
}

fn synthetic_small_caps(run: LayoutRun, lowercase_scale: f32) -> Vec<LayoutRun> {
    let mut output: Vec<LayoutRun> = Vec::new();
    for character in run.text.chars() {
        let is_lowercase = character.is_lowercase();
        let text = if is_lowercase {
            character.to_uppercase().collect::<String>()
        } else {
            character.to_string()
        };
        let size_scale = run.size_scale * if is_lowercase { lowercase_scale } else { 1.0 };
        if let Some(previous) = output.last_mut()
            && previous.face == run.face
            && previous.underline == run.underline
            && previous.strikethrough == run.strikethrough
            && previous.baseline_shift_em == run.baseline_shift_em
            && previous.size_scale == size_scale
            && previous.language == run.language
        {
            previous.text.push_str(&text);
        } else {
            output.push(LayoutRun {
                text,
                size_scale,
                ..run.clone()
            });
        }
    }
    output
}

fn font_family(value: &str) -> FontFamily {
    if let Some(index) = custom_family(value) {
        return FontFamily::Custom(index);
    }
    let normalized = value.to_ascii_lowercase();
    if normalized.contains("mono") || normalized.contains("code") {
        FontFamily::Mono
    } else if normalized.contains("sans")
        || normalized.contains("nunito")
        || normalized.contains("lexend")
        || normalized.contains("fredoka")
        || normalized.contains("atkinson")
    {
        FontFamily::Sans
    } else {
        FontFamily::Serif
    }
}

fn regular_face(family: FontFamily) -> FontFace {
    match family {
        FontFamily::Serif => FontFace::SerifRegular,
        FontFamily::Sans => FontFace::SansRegular,
        FontFamily::Mono => FontFace::MonoRegular,
        FontFamily::Custom(index) => FontFace::Custom(index),
    }
}

fn single_run(text: &str, face: FontFace) -> Vec<LayoutRun> {
    (!text.is_empty())
        .then(|| LayoutRun {
            text: text.to_owned(),
            face,
            underline: false,
            strikethrough: false,
            baseline_shift_em: 0.0,
            size_scale: 1.0,
            language: None,
        })
        .into_iter()
        .collect()
}

fn display_block_text(block: &Value) -> String {
    let text = block_text(block);
    match string(block, "type").to_ascii_lowercase().as_str() {
        "scenebreak" => typography::defaults().scene_break.text.clone(),
        "listitem" => format!("{}{text}", typography::defaults().list_item.bullet),
        _ => text,
    }
}

fn display_block_runs(
    document: &Value,
    block: &Value,
    style: &BlockStyle,
    display_text: &str,
) -> Vec<LayoutRun> {
    match string(block, "type").to_ascii_lowercase().as_str() {
        "scenebreak" => single_run(display_text, style.face),
        _ => block_runs(document, block, style),
    }
}

fn block_has_marks(block: &Value) -> bool {
    block
        .get("content")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .any(|span| {
            span.get("marks")
                .and_then(Value::as_array)
                .is_some_and(|marks| !marks.is_empty())
        })
}

fn wrap(text: &str, limit: usize) -> Vec<String> {
    if text.is_empty() {
        return Vec::new();
    }
    let opportunities: BTreeSet<usize> = linebreaks(text).map(|entry| entry.0).collect();
    let mut lines = Vec::new();
    let mut start = 0;
    while start < text.len() {
        let target = text[start..]
            .char_indices()
            .nth(limit)
            .map_or(text.len(), |(offset, _)| start + offset);
        let allowed = opportunities
            .range((start + 1)..=target)
            .next_back()
            .copied();
        let (end, line) = if let Some(end) = allowed {
            (end, text[start..end].trim().to_owned())
        } else {
            let natural_end = opportunities
                .range((target + 1)..)
                .next()
                .copied()
                .unwrap_or(text.len());
            let word = text[start..natural_end].trim();
            let syllables = hyphenate(word, Lang::English).collect::<Vec<_>>();
            let mut prefix = String::new();
            for syllable in syllables.iter().take(syllables.len().saturating_sub(1)) {
                if prefix.chars().count() + syllable.chars().count() + 1 > limit {
                    break;
                }
                prefix.push_str(syllable);
            }
            if prefix.is_empty() {
                (target, text[start..target].trim().to_owned())
            } else {
                (start + prefix.len(), format!("{prefix}-"))
            }
        };
        if !line.is_empty() {
            lines.push(line);
        }
        start = end;
        while start < text.len() && text.as_bytes()[start].is_ascii_whitespace() {
            start += 1;
        }
    }
    lines
}

#[allow(clippy::too_many_arguments)]
fn dedicated_figure_page_with_layout(
    trim: &crate::model::Trim,
    label: &str,
    caption_runs: &[LayoutRun],
    caption_style: &BlockStyle,
    asset_id: String,
    width_percent: f32,
    crop_x_percent: f32,
    crop_y_percent: f32,
    alignment: &str,
) -> LayoutPage {
    let margin = trim.margin_inches * 72.0;
    let available_width = trim.width_inches * 72.0 - margin * 2.0;
    let image_width = available_width * (width_percent / 100.0).clamp(0.1, 1.0);
    let image_x = match alignment.to_ascii_lowercase().as_str() {
        "left" => margin,
        "right" => margin + available_width - image_width,
        _ => margin + (available_width - image_width) / 2.0,
    };
    let (caption_origin_x, caption_width) =
        caption_content_geometry(image_x, image_width, caption_style, 0.0);
    let caption_lines =
        wrapped_caption_with_runs(label, caption_runs, caption_style, caption_width);
    let caption_size = caption_style.size;
    let caption_step = caption_size * caption_style.line_height.max(1.0);
    let first_caption_y = margin
        + caption_style.space_before.max(0.0)
        + caption_size * 0.30
        + caption_lines.len().saturating_sub(1) as f32 * caption_step;
    let available_height = trim.height_inches * 72.0 - margin * 2.0;
    let image_y = margin + available_height * 0.2 + caption_style.space_after.max(0.0);
    let image_height = available_height * 0.75;
    LayoutPage {
        kind: PageKind::Designed,
        width_points: None,
        height_points: None,
        lines: caption_lines
            .into_iter()
            .enumerate()
            .map(|(index, (text, runs))| {
                let first_line_indent = if index == 0 {
                    caption_style.first_line_indent
                } else {
                    0.0
                };
                let x = aligned_caption_x(
                    caption_origin_x + first_line_indent,
                    (caption_width - first_line_indent.max(0.0)).max(0.0),
                    caption_style,
                    &runs,
                );
                let baseline_offset_points =
                    line_baseline_offset_points(caption_size, caption_style.face, &runs);
                LayoutLine {
                    text,
                    baseline_offset_points,
                    runs,
                    size: caption_size,
                    x,
                    y: first_caption_y - index as f32 * caption_step,
                    word_spacing: 0.0,
                    character_spacing: 0.0,
                    rotation_degrees: 0.0,
                    rotation_origin_x: None,
                    rotation_origin_y: None,
                    opacity: 1.0,
                    light_text: false,
                    fill_rgb: None,
                    semantic_role: LayoutSemanticRole::Caption,
                    artifact: false,
                    language: None,
                    reading_order: None,
                    semantic_id: None,
                    semantic_parent_id: None,
                    source_start_utf16: None,
                    source_end_utf16: None,
                    link_page: None,
                }
            })
            .collect(),
        images: (!asset_id.is_empty())
            .then_some(LayoutImage {
                asset_id,
                x: image_x,
                y: image_y,
                width: image_width,
                height: image_height,
                focal_x: (crop_x_percent / 100.0).clamp(0.0, 1.0),
                focal_y: (crop_y_percent / 100.0).clamp(0.0, 1.0),
                source_left_fraction: 0.0,
                source_width_fraction: 1.0,
                rotation_degrees: 0.0,
                opacity: 1.0,
                fit: LayoutImageFit::Cover,
                alt_text: None,
                decorative: true,
                language: None,
                reading_order: None,
                semantic_id: None,
                semantic_parent_id: None,
                text_wrap: None,
                accessibility_role: None,
            })
            .into_iter()
            .collect(),
        shapes: Vec::new(),
        paint_order: Vec::new(),
        barcode_modules: None,
        page_label: None,
        bookmark: None,
    }
}

fn blank_cover_layout(width: f32, height: f32) -> LayoutPage {
    LayoutPage {
        kind: PageKind::Cover,
        width_points: Some(width),
        height_points: Some(height),
        lines: Vec::new(),
        images: Vec::new(),
        shapes: Vec::new(),
        paint_order: Vec::new(),
        barcode_modules: None,
        page_label: None,
        bookmark: None,
    }
}

fn cover_layout(
    request: &RenderRequest,
    width: f32,
    height: f32,
    scene: Option<&Value>,
) -> Result<LayoutPage, Diagnostic> {
    let cover = request.cover.as_ref().expect("cover");
    if scene.is_some() || cover.scene.is_some() {
        return cover_scene_layout(request, width, height, false, false, &mut Vec::new(), scene);
    }
    let bleed = cover.bleed_inches * 72.0;
    let spine_width = (width - bleed * 2.0 - request.trim.width_inches * 144.0).max(0.0);
    let spine_center = bleed + request.trim.width_inches * 72.0 + spine_width / 2.0;
    let panel_width = request.trim.width_inches * 72.0;
    let safe_margin = 36.0;
    let safe_width = panel_width - safe_margin * 2.0;
    let front_x = bleed + panel_width + spine_width + safe_margin;
    let back_x = bleed + safe_margin;
    let mut lines = cover_text_lines(
        &cover.title,
        FontFace::SansBold,
        24.0,
        front_x,
        height - bleed - 72.0,
        safe_width,
        3,
        31.0,
    )?;
    lines.extend(cover_text_lines(
        &cover.subtitle,
        FontFace::SerifItalic,
        12.0,
        front_x,
        height - bleed - 174.0,
        safe_width,
        3,
        17.0,
    )?);
    lines.extend(cover_text_lines(
        &cover.author,
        FontFace::SansRegular,
        12.0,
        front_x,
        bleed + 72.0,
        safe_width,
        2,
        17.0,
    )?);
    lines.extend(cover_text_lines(
        &cover.back_copy,
        FontFace::SerifRegular,
        9.0,
        back_x,
        height - bleed - 72.0,
        safe_width,
        14,
        13.0,
    )?);
    if !cover.spine_text.is_empty() {
        if spine_width < 18.0 {
            return Err(Diagnostic::error(
                "PRESS_COVER_TEXT_OVERFLOW",
                "The spine is too narrow for spine copy.",
            ));
        }
        let spine_size = (spine_width * 0.5).clamp(7.0, 12.0);
        let spine_safe_height = height - 2.0 * (bleed + 36.0);
        if measure_text(FontFace::SansBold, &cover.spine_text, spine_size) > spine_safe_height {
            return Err(Diagnostic::error(
                "PRESS_COVER_TEXT_OVERFLOW",
                "Spine copy does not fit the full-wrap cover safe region.",
            ));
        }
        let runs = single_run(&cover.spine_text, FontFace::SansBold);
        lines.push(LayoutLine {
            text: cover.spine_text.clone(),
            runs: runs.clone(),
            size: spine_size,
            x: spine_center,
            y: bleed + 36.0,
            baseline_offset_points: line_baseline_offset_points(
                spine_size,
                FontFace::SansBold,
                &runs,
            ),
            word_spacing: 0.0,
            character_spacing: 0.0,
            rotation_degrees: 90.0,
            rotation_origin_x: None,
            rotation_origin_y: None,
            opacity: 1.0,
            light_text: true,
            fill_rgb: None,
            semantic_role: LayoutSemanticRole::Paragraph,
            artifact: true,
            language: None,
            reading_order: None,
            semantic_id: None,
            semantic_parent_id: None,
            source_start_utf16: None,
            source_end_utf16: None,
            link_page: None,
        });
    }
    if cover.barcode_mode == "LorekeeperBarcode"
        && let Some(isbn) = cover.isbn.as_deref()
    {
        let runs = single_run(isbn, FontFace::MonoRegular);
        lines.push(LayoutLine {
            text: isbn.to_owned(),
            runs: runs.clone(),
            size: 8.0,
            x: width * 0.08,
            y: height * 0.10 - 12.0,
            baseline_offset_points: line_baseline_offset_points(8.0, FontFace::MonoRegular, &runs),
            word_spacing: 0.0,
            character_spacing: 0.0,
            rotation_degrees: 0.0,
            rotation_origin_x: None,
            rotation_origin_y: None,
            opacity: 1.0,
            light_text: false,
            fill_rgb: None,
            semantic_role: LayoutSemanticRole::Paragraph,
            artifact: false,
            language: None,
            reading_order: None,
            semantic_id: None,
            semantic_parent_id: None,
            source_start_utf16: None,
            source_end_utf16: None,
            link_page: None,
        });
    }
    Ok(LayoutPage {
        kind: PageKind::Cover,
        width_points: Some(width),
        height_points: Some(height),
        lines,
        images: cover
            .asset_id
            .as_ref()
            .map(|asset_id| LayoutImage {
                asset_id: asset_id.clone(),
                x: bleed + panel_width + spine_width,
                y: bleed,
                width: panel_width,
                height: request.trim.height_inches * 72.0,
                focal_x: cover.image_crop_x_percent / 100.0,
                focal_y: cover.image_crop_y_percent / 100.0,
                source_left_fraction: 0.0,
                source_width_fraction: 1.0,
                rotation_degrees: 0.0,
                opacity: 1.0,
                fit: LayoutImageFit::Cover,
                alt_text: None,
                decorative: true,
                language: None,
                reading_order: None,
                semantic_id: None,
                semantic_parent_id: None,
                text_wrap: None,
                accessibility_role: None,
            })
            .into_iter()
            .collect(),
        shapes: Vec::new(),
        paint_order: Vec::new(),
        barcode_modules: if cover.barcode_mode == "LorekeeperBarcode" {
            cover.isbn.as_deref().and_then(ean13_modules)
        } else {
            None
        },
        page_label: None,
        bookmark: None,
    })
}

fn digital_cover_layout(
    request: &RenderRequest,
    allow_pending_accessibility: bool,
    diagnostics: &mut Vec<Diagnostic>,
) -> Result<LayoutPage, Diagnostic> {
    let cover = request.cover.as_ref().expect("cover");
    let width = request.trim.width_inches * 72.0;
    let height = request.trim.height_inches * 72.0;
    if cover.scene.is_some() {
        return cover_scene_layout(
            request,
            width,
            height,
            true,
            allow_pending_accessibility,
            diagnostics,
            None,
        );
    }
    let margin = (request.trim.margin_inches * 72.0).max(24.0);
    let safe_width = width - margin * 2.0;
    let mut lines = cover_text_lines(
        &cover.title,
        FontFace::SansBold,
        28.0,
        margin,
        height - margin - 54.0,
        safe_width,
        4,
        35.0,
    )?;
    lines.extend(cover_text_lines(
        &cover.subtitle,
        FontFace::SerifItalic,
        14.0,
        margin,
        height - margin - 180.0,
        safe_width,
        4,
        20.0,
    )?);
    lines.extend(cover_text_lines(
        &cover.author,
        FontFace::SansRegular,
        14.0,
        margin,
        margin + 36.0,
        safe_width,
        2,
        20.0,
    )?);
    Ok(LayoutPage {
        kind: PageKind::Cover,
        width_points: None,
        height_points: None,
        lines,
        images: cover
            .asset_id
            .as_ref()
            .map(|asset_id| LayoutImage {
                asset_id: asset_id.clone(),
                x: 0.0,
                y: 0.0,
                width,
                height,
                focal_x: (cover.image_crop_x_percent / 100.0).clamp(0.0, 1.0),
                focal_y: (cover.image_crop_y_percent / 100.0).clamp(0.0, 1.0),
                source_left_fraction: 0.0,
                source_width_fraction: 1.0,
                rotation_degrees: 0.0,
                opacity: 1.0,
                fit: LayoutImageFit::Cover,
                alt_text: None,
                decorative: true,
                language: None,
                reading_order: None,
                semantic_id: None,
                semantic_parent_id: None,
                text_wrap: None,
                accessibility_role: None,
            })
            .into_iter()
            .collect(),
        shapes: Vec::new(),
        paint_order: Vec::new(),
        barcode_modules: None,
        page_label: Some("Cover".to_owned()),
        bookmark: None,
    })
}

fn cover_scene_layout(
    request: &RenderRequest,
    width: f32,
    height: f32,
    digital: bool,
    allow_pending_accessibility: bool,
    diagnostics: &mut Vec<Diagnostic>,
    scene_override: Option<&Value>,
) -> Result<LayoutPage, Diagnostic> {
    let cover = request.cover.as_ref().expect("cover");
    let mut scene = scene_override
        .cloned()
        .or_else(|| cover.scene.clone())
        .expect("cover scene");
    let old_width = scene["surface"]["widthPoints"]
        .as_f64()
        .unwrap_or(width as f64) as f32;
    let old_height = scene["surface"]["heightPoints"]
        .as_f64()
        .unwrap_or(height as f64) as f32;
    let bleed = if digital {
        0.0
    } else {
        cover.bleed_inches * 72.0
    };
    let panel = request.trim.width_inches * 72.0;
    let measured_template = request.print_artifact_profile.as_ref().and_then(|product| {
        (product.vendor == "BarnesAndNoblePress")
            .then_some(product.print_template_evidence.as_ref())
            .flatten()
    });
    let new_regions = if let Some(template) = measured_template {
        let back_width = template.back_cover_width_inches.unwrap_or_default() * 72.0;
        let front_width = template.front_cover_width_inches.unwrap_or_default() * 72.0;
        let region_height = template.front_cover_height_inches.unwrap_or_default() * 72.0;
        CoverRegionGeometry {
            back_x: 0.0,
            back_width,
            front_width,
            spine_width: template.spine_width_inches.unwrap_or_default() * 72.0,
            y: ((height - region_height) / 2.0).max(0.0),
            height: region_height,
            bleed,
        }
    } else {
        CoverRegionGeometry {
            back_x: if digital { 0.0 } else { bleed },
            back_width: panel,
            front_width: panel,
            spine_width: if digital {
                0.0
            } else {
                (width - bleed * 2.0 - panel * 2.0).max(0.0)
            },
            y: bleed,
            height: if digital {
                height
            } else {
                height - bleed * 2.0
            },
            bleed,
        }
    };
    let surface = &scene["surface"];
    let old_regions = CoverRegionGeometry {
        back_x: if surface["backRegionWidthPoints"]
            .as_f64()
            .unwrap_or_default()
            > 0.0
        {
            0.0
        } else {
            bleed
        },
        back_width: surface["backRegionWidthPoints"]
            .as_f64()
            .unwrap_or(new_regions.back_width as f64) as f32,
        front_width: surface["frontRegionWidthPoints"]
            .as_f64()
            .unwrap_or(new_regions.front_width as f64) as f32,
        spine_width: surface["spineWidthPoints"]
            .as_f64()
            .unwrap_or(new_regions.spine_width as f64) as f32,
        y: surface["coverRegionYPoints"]
            .as_f64()
            .unwrap_or(new_regions.y as f64) as f32,
        height: surface["coverRegionHeightPoints"]
            .as_f64()
            .filter(|value| *value > 0.0)
            .unwrap_or(new_regions.height as f64) as f32,
        bleed,
    };
    scene["surface"]["widthPoints"] = Value::from(width);
    scene["surface"]["heightPoints"] = Value::from(height);
    scene["surface"]["backRegionWidthPoints"] = Value::from(new_regions.back_width);
    scene["surface"]["frontRegionWidthPoints"] = Value::from(new_regions.front_width);
    scene["surface"]["spineWidthPoints"] = Value::from(new_regions.spine_width);
    scene["surface"]["coverRegionYPoints"] = Value::from(new_regions.y);
    scene["surface"]["coverRegionHeightPoints"] = Value::from(new_regions.height);
    let objects = scene
        .get_mut("objects")
        .and_then(Value::as_array_mut)
        .ok_or_else(|| {
            Diagnostic::error(
                "PRESS_COVER_SCENE_INVALID",
                "Cover scene objects are missing.",
            )
        })?;
    for item in objects {
        if string(item, "kind") == "Image" && item.get("decorative").is_none() {
            item["decorative"] = Value::Bool(true);
        }
        if string(item, "kind") == "Text" {
            let binding = string(item, "textBinding");
            let resolved = match binding.as_str() {
                "title" => Some(&cover.title),
                "subtitle" => Some(&cover.subtitle),
                "author" => Some(&cover.author),
                "spineText" => Some(&cover.spine_text),
                "backCopy" => Some(&cover.back_copy),
                _ => None,
            };
            if let Some(resolved) = resolved {
                if resolved.trim().is_empty() {
                    item["visible"] = Value::Bool(false);
                    continue;
                }
                item["textBinding"] = Value::String(resolved.clone());
            }
        }
        // Group children are stored in parent-local percentages. Their group is
        // the surface-space object that participates in cover-region reflow.
        if !string(item, "groupId").is_empty() {
            continue;
        }
        let region = string(item, "regionConstraint");
        let old_region = cover_region(&region, old_width, old_height, &old_regions, digital);
        let new_region = cover_region(&region, width, height, &new_regions, digital);
        let bounds = item.get("bounds").cloned().unwrap_or(Value::Null);
        let surface_x = bounds
            .get("xPercent")
            .and_then(Value::as_f64)
            .unwrap_or(0.0) as f32
            / 100.0
            * old_width;
        let surface_y = bounds
            .get("yPercent")
            .and_then(Value::as_f64)
            .unwrap_or(0.0) as f32
            / 100.0
            * old_height;
        let surface_width = bounds
            .get("widthPercent")
            .and_then(Value::as_f64)
            .unwrap_or(100.0) as f32
            / 100.0
            * old_width;
        let surface_height = bounds
            .get("heightPercent")
            .and_then(Value::as_f64)
            .unwrap_or(100.0) as f32
            / 100.0
            * old_height;
        let (x, y, object_width, object_height) = if region == "Page" {
            (surface_x, surface_y, surface_width, surface_height)
        } else {
            let local_x = (surface_x - old_region.0) / old_region.2.max(0.01);
            let local_y = (surface_y - old_region.1) / old_region.3.max(0.01);
            let local_width = surface_width / old_region.2.max(0.01);
            let local_height = surface_height / old_region.3.max(0.01);
            (
                new_region.0 + local_x * new_region.2,
                new_region.1 + local_y * new_region.3,
                local_width * new_region.2,
                local_height * new_region.3,
            )
        };
        item["bounds"] = serde_json::json!({
            "xPercent": x / width * 100.0,
            "yPercent": y / height * 100.0,
            "widthPercent": object_width / width * 100.0,
            "heightPercent": object_height / height * 100.0
        });
    }
    let composition = serde_json::json!({
        "id": "cover",
        "name": "Cover",
        "semanticBlocks": [],
        "variants": [{ "scene": scene }]
    });
    let mut page = designed_page(
        &composition,
        &request.document,
        &request.trim,
        LayoutTolerance {
            allow_pending_accessibility,
            ..LayoutTolerance::default()
        },
        diagnostics,
    )?;
    page.kind = PageKind::Cover;
    page.width_points = Some(width);
    page.height_points = Some(height);
    page.page_label = digital.then(|| "Cover".to_owned());
    if !digital && cover.barcode_mode == "LorekeeperBarcode" {
        page.barcode_modules = cover.isbn.as_deref().and_then(ean13_modules);
    }
    Ok(page)
}

#[derive(Clone, Copy)]
struct CoverRegionGeometry {
    back_x: f32,
    back_width: f32,
    front_width: f32,
    spine_width: f32,
    y: f32,
    height: f32,
    bleed: f32,
}

fn cover_region(
    region: &str,
    width: f32,
    height: f32,
    geometry: &CoverRegionGeometry,
    digital: bool,
) -> (f32, f32, f32, f32) {
    if digital {
        return (0.0, 0.0, width, height);
    }
    match region {
        "Back" => (
            geometry.back_x,
            geometry.y,
            geometry.back_width,
            geometry.height,
        ),
        "Spine" | "Gutter" => (
            geometry.back_x + geometry.back_width,
            geometry.y,
            geometry.spine_width.max(0.01),
            geometry.height,
        ),
        "Front" => (
            width - geometry.front_width,
            geometry.y,
            geometry.front_width,
            geometry.height,
        ),
        "SafeArea" => (
            geometry.bleed + 18.0,
            geometry.bleed + 18.0,
            width - geometry.bleed * 2.0 - 36.0,
            height - geometry.bleed * 2.0 - 36.0,
        ),
        "BarcodeReserve" => (
            geometry.bleed + 18.0,
            height - geometry.bleed - 104.4,
            144.0,
            86.4,
        ),
        _ => (0.0, 0.0, width, height),
    }
}

#[allow(clippy::too_many_arguments)]
fn cover_text_lines(
    text: &str,
    face: FontFace,
    size: f32,
    x: f32,
    start_y: f32,
    maximum_width: f32,
    maximum_lines: usize,
    line_height: f32,
) -> Result<Vec<LayoutLine>, Diagnostic> {
    if text.is_empty() {
        return Ok(Vec::new());
    }
    let mut limit = (maximum_width / (size * 0.52)).floor().max(4.0) as usize;
    let lines = loop {
        let candidate = wrap(text, limit);
        if limit <= 4
            || candidate
                .iter()
                .all(|line| measure_text(face, line, size) <= maximum_width + 0.01)
        {
            break candidate;
        }
        limit -= 1;
    };
    if lines.len() > maximum_lines {
        return Err(Diagnostic::error(
            "PRESS_COVER_TEXT_OVERFLOW",
            "Cover copy does not fit inside the bounded safe-region template.",
        ));
    }
    Ok(lines
        .into_iter()
        .enumerate()
        .map(|(index, text)| {
            let runs = single_run(&text, face);
            LayoutLine {
                baseline_offset_points: line_baseline_offset_points(size, face, &runs),
                runs,
                text,
                size,
                x,
                y: start_y - index as f32 * line_height,
                word_spacing: 0.0,
                character_spacing: 0.0,
                rotation_degrees: 0.0,
                rotation_origin_x: None,
                rotation_origin_y: None,
                opacity: 1.0,
                light_text: true,
                fill_rgb: None,
                semantic_role: LayoutSemanticRole::Paragraph,
                artifact: false,
                language: None,
                reading_order: None,
                semantic_id: None,
                semantic_parent_id: None,
                source_start_utf16: None,
                source_end_utf16: None,
                link_page: None,
            }
        })
        .collect())
}

fn start_recto(pages: &mut Vec<LayoutPage>, trim: &crate::model::Trim, leading_page_count: usize) {
    if trim.recto_chapter_starts && (leading_page_count + pages.len() + 1).is_multiple_of(2) {
        pages.push(LayoutPage {
            kind: PageKind::Blank,
            width_points: None,
            height_points: None,
            lines: Vec::new(),
            images: Vec::new(),
            shapes: Vec::new(),
            paint_order: Vec::new(),
            barcode_modules: None,
            page_label: None,
            bookmark: None,
        });
    }
}

fn is_designed_page_only_chapter(chapter: &Value) -> bool {
    let Some(blocks) = chapter.get("blocks").and_then(Value::as_array) else {
        return false;
    };
    let mut has_designed_page = false;
    for block in blocks {
        let block_type = string(block, "type");
        if block_type.eq_ignore_ascii_case("DesignedPage") {
            has_designed_page = true;
            continue;
        }
        if block_type.eq_ignore_ascii_case("Paragraph")
            && display_block_text(block).trim().is_empty()
        {
            continue;
        }
        return false;
    }
    has_designed_page
}

fn assign_page_labels(pages: &mut [LayoutPage], body_start: usize) {
    let mut front_number = 0;
    for (index, page) in pages.iter_mut().enumerate() {
        let physical = index + 1;
        if page.kind == PageKind::Blank || page.kind == PageKind::Cover || physical == 1 {
            page.page_label = None;
        } else if physical < body_start {
            front_number += 1;
            page.page_label = Some(roman(front_number));
        } else {
            page.page_label = Some((physical - body_start + 1).to_string());
        }
    }
}

fn roman(mut value: usize) -> String {
    let mut output = String::new();
    for (number, numeral) in [
        (1000, "m"),
        (900, "cm"),
        (500, "d"),
        (400, "cd"),
        (100, "c"),
        (90, "xc"),
        (50, "l"),
        (40, "xl"),
        (10, "x"),
        (9, "ix"),
        (5, "v"),
        (4, "iv"),
        (1, "i"),
    ] {
        while value >= number {
            output.push_str(numeral);
            value -= number;
        }
    }
    output
}

fn block_text(block: &Value) -> String {
    block
        .get("content")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter_map(|span| span.get("text").and_then(Value::as_str))
        .collect::<String>()
}

fn ean13_modules(value: &str) -> Option<Vec<bool>> {
    let digits = value
        .chars()
        .filter(|character| character.is_ascii_digit())
        .map(|character| character.to_digit(10).unwrap() as usize)
        .collect::<Vec<_>>();
    if digits.len() != 13 {
        return None;
    }
    let checksum = digits[..12]
        .iter()
        .enumerate()
        .map(|(index, digit)| {
            if index.is_multiple_of(2) {
                *digit
            } else {
                digit * 3
            }
        })
        .sum::<usize>();
    if (10 - checksum % 10) % 10 != digits[12] {
        return None;
    }
    const L: [&str; 10] = [
        "0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011",
        "0110111", "0001011",
    ];
    const G: [&str; 10] = [
        "0100111", "0110011", "0011011", "0100001", "0011101", "0111001", "0000101", "0010001",
        "0001001", "0010111",
    ];
    const R: [&str; 10] = [
        "1110010", "1100110", "1101100", "1000010", "1011100", "1001110", "1010000", "1000100",
        "1001000", "1110100",
    ];
    const PARITY: [&str; 10] = [
        "LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG", "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL",
        "LGGLGL",
    ];
    let mut encoded = String::from("101");
    for (offset, parity) in PARITY[digits[0]].bytes().enumerate() {
        encoded.push_str(if parity == b'L' {
            L[digits[offset + 1]]
        } else {
            G[digits[offset + 1]]
        });
    }
    encoded.push_str("01010");
    for digit in &digits[7..] {
        encoded.push_str(R[*digit]);
    }
    encoded.push_str("101");
    Some(encoded.bytes().map(|module| module == b'1').collect())
}

fn string(value: &Value, key: &str) -> String {
    value
        .get(key)
        .and_then(Value::as_str)
        .unwrap_or("")
        .to_owned()
}

fn numbered_title(title: &str, ordinal: usize, numbered: bool, label: &str) -> String {
    if !numbered {
        return title.to_owned();
    }
    if title.is_empty() {
        format!("{label} {ordinal}")
    } else {
        format!("{label} {ordinal}: {title}")
    }
}

fn artifact(kind: &str, relative_path: &str, bytes: &[u8], page_count: usize) -> Artifact {
    Artifact {
        kind: kind.to_owned(),
        relative_path: relative_path.to_owned(),
        filename: Path::new(relative_path)
            .file_name()
            .expect("filename")
            .to_string_lossy()
            .into_owned(),
        media_type: if kind == "print-setup-manifest" {
            "application/json"
        } else {
            "application/pdf"
        }
        .to_owned(),
        byte_length: bytes.len() as u64,
        sha256: hex_hash(bytes),
        page_count,
    }
}

fn reject<T>(code: &str, message: impl Into<String>) -> RenderResult<T> {
    Err(Box::new(RenderResponse::failed(
        "rejected",
        Diagnostic::error(code, message),
    )))
}

fn io_failure(error: std::io::Error) -> Box<RenderResponse> {
    Box::new(RenderResponse::failed(
        "failed",
        Diagnostic::error("PRESS_IO_FAILED", error.to_string()),
    ))
}

fn normalized_relative(path: &Path) -> String {
    path.components()
        .map(|part| part.as_os_str().to_string_lossy())
        .collect::<Vec<_>>()
        .join("/")
}

fn hex_hash(bytes: &[u8]) -> String {
    Sha256::digest(bytes)
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect()
}

fn is_link_or_reparse(path: &Path) -> bool {
    let Ok(metadata) = fs::symlink_metadata(path) else {
        return true;
    };
    if metadata.file_type().is_symlink() {
        return true;
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        if metadata.file_attributes() & 0x400 != 0 {
            return true;
        }
    }
    false
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn digital_and_print_profiles_apply_their_declared_dpi_thresholds() {
        assert_eq!(required_effective_dpi("generic-digital-pdf-v1"), 180.0);
        assert_eq!(required_effective_dpi("kdp-paperback-v2"), 300.0);
        assert_eq!(required_effective_dpi("ingram-print-pdfx1a-v2"), 300.0);
    }

    #[test]
    fn wraps_only_at_unicode_boundaries() {
        let lines = wrap("A measured café sentence breaks safely.", 12);
        assert!(lines.len() > 1);
        assert!(lines.iter().all(|line| line.is_char_boundary(line.len())));
    }

    #[test]
    fn long_english_words_break_only_at_hyphenation_points() {
        let lines = wrap("characteristically", 10);
        assert!(lines.len() > 1);
        assert!(
            lines[..lines.len() - 1]
                .iter()
                .all(|line| line.ends_with('-'))
        );
        assert_eq!(lines.join("").replace('-', ""), "characteristically");
    }

    #[test]
    fn shaped_line_widths_remain_inside_the_text_area() {
        let trim = standard_trim();
        let style = BlockStyle::body(&trim);
        let text = format!(
            "{} measured words follow the adversarial unbroken run.",
            "W".repeat(80)
        );
        let runs = vec![LayoutRun {
            text: text.clone(),
            face: FontFace::SerifRegular,
            underline: false,
            strikethrough: false,
            baseline_shift_em: 0.0,
            size_scale: 1.0,
            language: None,
        }];
        let mut pages = Vec::new();
        append_styled_runs(&mut pages, &text, &runs, &trim, &style, None);
        let available = (trim.width_inches - 2.0 * trim.margin_inches) * 72.0;

        for line in pages.iter().flat_map(|page| page.lines.iter()) {
            let measured = line
                .runs
                .iter()
                .map(|run| {
                    crate::font::measure_text(run.face, &run.text, line.size * run.size_scale)
                })
                .sum::<f32>();
            assert!(
                measured <= available + 0.01,
                "{} > {} for {}",
                measured,
                available,
                line.text
            );
        }
    }

    #[test]
    fn paragraph_releases_float_exclusion_below_image_and_on_later_pages() {
        let trim = standard_trim();
        let mut pages = vec![empty_body_page()];
        append_inline_illustration(
            &mut pages,
            &trim,
            "figure-id",
            "",
            &[],
            &BlockStyle::caption(&trim),
            "asset".to_owned(),
            42.0,
            50.0,
            50.0,
            "left",
            "End",
            LayoutImageFit::Contain,
            0.0,
            0.0,
            "Hidden",
            true,
        );
        let margin = trim.margin_inches * 72.0;
        let text = "Measured words continue beside the illustration and then reclaim the complete text measure below it. ".repeat(120);
        append_styled_text(&mut pages, &text, &trim, &BlockStyle::body(&trim));
        let body_lines = pages
            .iter()
            .flat_map(|page| page.lines.iter())
            .filter(|line| !line.artifact)
            .collect::<Vec<_>>();
        assert!(
            body_lines.iter().any(|line| line.x > margin + 1.0),
            "some lines must flow beside the float"
        );
        assert!(
            body_lines.iter().any(|line| (line.x - margin).abs() < 0.01),
            "lines below the float must return to the full measure"
        );
        assert!(
            pages
                .iter()
                .skip(1)
                .flat_map(|page| &page.lines)
                .filter(|line| !line.artifact)
                .all(|line| (line.x - margin).abs() < 0.01),
            "the float must never constrain later pages"
        );
    }

    #[test]
    fn pagination_enforces_widow_and_orphan_minimums() {
        let trim = crate::model::Trim {
            width_inches: 3.5,
            height_inches: 5.0,
            margin_inches: 1.0,
            body_font_size_points: 30.0,
            body_line_height: 2.5,
            bleed_inches: 0.0,
            mirror_margins: false,
            recto_chapter_starts: false,
            minimum_widow_lines: 2,
            minimum_orphan_lines: 2,
        };
        let mut pages = Vec::new();
        append_styled_text(
            &mut pages,
            &"A measured sentence fills a narrow page safely. ".repeat(20),
            &trim,
            &BlockStyle::body(&trim),
        );
        assert!(pages.len() > 1);
        assert!(pages.iter().all(|page| page.lines.len() >= 2));
    }

    #[test]
    fn ean_13_encoding_has_guards_and_exact_module_count() {
        let modules = ean13_modules("9780306406157").expect("valid EAN-13");
        assert_eq!(modules.len(), 95);
        assert_eq!(&modules[..3], &[true, false, true]);
        assert!(ean13_modules("9780306406158").is_none());
    }

    #[test]
    fn long_toc_converges_without_changing_recto_chapter_starts() {
        let chapters = (0..60)
            .map(|index| {
                serde_json::json!({
                    "id": format!("chapter-{index}"),
                    "title": format!("Chapter {index}: The deliberately long cartographic record of a coast that refuses to remain still"),
                    "blocks": [{
                        "id": format!("block-{index}"),
                        "type": "Paragraph",
                        "content": [{ "text": "A measured line." }]
                    }]
                })
            })
            .collect::<Vec<_>>();
        let request = RenderRequest {
            protocol_version: 10,
            job_id: "1".repeat(32),
            profile: "kdp-paperback-v2".to_owned(),
            ink: "BlackAndWhite".to_owned(),
            print_artifact_profile: None,
            output_purpose: OutputPurpose::Publication,
            layout_trace_mode: None,
            document: serde_json::json!({
                "title": "Long contents",
                "author": "Author",
                "language": "en",
                "publicationSections": [{
                    "id": "contents",
                    "anchor": "Front",
                    "systemRole": "Contents",
                    "blocks": []
                }],
                "sections": [{ "chapters": chapters }]
            }),
            trim: crate::model::Trim {
                width_inches: 6.0,
                height_inches: 9.0,
                margin_inches: 0.75,
                body_font_size_points: 11.0,
                body_line_height: 1.4,
                bleed_inches: 0.0,
                mirror_margins: true,
                recto_chapter_starts: true,
                minimum_widow_lines: 2,
                minimum_orphan_lines: 2,
            },
            cover: None,
            assets: Vec::new(),
            fonts: Vec::new(),
        };
        let layout = paginate(&request).expect("layout");
        assert!(layout.toc_converged);
        let toc_pages = layout
            .pages
            .iter()
            .filter(|page| {
                page.lines
                    .first()
                    .is_some_and(|line| line.text.starts_with("Contents"))
            })
            .count();
        assert!(toc_pages >= 3);
        // Replacement may add blank padding to preserve the already-paginated
        // body's recto starts; only content pages carry a Contents heading.
        let top = request.trim.height_inches * 72.0 - request.trim.margin_inches * 72.0;
        let bottom = request.trim.margin_inches * 72.0;
        let width = (request.trim.width_inches - request.trim.margin_inches * 2.0) * 72.0;
        for page in layout.pages.iter().filter(|page| {
            page.lines
                .first()
                .is_some_and(|line| line.text.starts_with("Contents"))
        }) {
            for line in &page.lines {
                assert!(line.y + line.size * 0.82 <= top + 0.01);
                assert!(line.y - line.size * 0.30 >= bottom - 0.01);
                assert!(measured_run_width(&line.runs, line.size) <= width + 0.01);
            }
        }
        assert!(
            layout
                .page_map
                .iter()
                .all(|entry| !entry.page_number.is_multiple_of(2))
        );
        assert!(layout.pages.iter().any(|page| {
            page.lines
                .iter()
                .any(|line| line.text.starts_with("Chapter 59"))
        }));
        assert!(
            layout
                .pages
                .iter()
                .any(|page| { page.lines.iter().any(|line| line.text.contains("·  1")) })
        );
        assert_eq!(layout.pages[0].page_label, None);
        assert_eq!(layout.pages[1].page_label.as_deref(), Some("i"));
        let first_body = layout
            .page_map
            .iter()
            .map(|entry| entry.page_number)
            .min()
            .unwrap();
        assert_eq!(
            layout.pages[first_body - 1].page_label.as_deref(),
            Some("1")
        );
    }

    #[test]
    fn toc_fails_honestly_when_the_convergence_budget_is_exhausted() {
        let error =
            build_toc_pages_with_limit(&[("A chapter".to_owned(), 3)], 3, &standard_trim(), 1)
                .expect_err("one pass cannot establish a stable page count");

        assert_eq!(error.code.as_ref(), "PRESS_TOC_NONCONVERGENT");
    }

    #[test]
    fn named_styles_control_block_layout_without_executing_document_code() {
        let trim = crate::model::Trim {
            width_inches: 6.0,
            height_inches: 9.0,
            margin_inches: 0.75,
            body_font_size_points: 11.0,
            body_line_height: 1.4,
            bleed_inches: 0.0,
            mirror_margins: true,
            recto_chapter_starts: true,
            minimum_widow_lines: 2,
            minimum_orphan_lines: 2,
        };
        let document = serde_json::json!({
            "styles": [{
                "semanticRole": "epigraph",
                "definition": {
                    "fontSizePoints": 9.0,
                    "lineHeight": 1.2,
                    "keepWithNext": true,
                    "textAlign": "center"
                }
            }]
        });
        let block = serde_json::json!({
            "type": "Blockquote",
            "styleRole": "epigraph"
        });
        let style = block_style(&document, &block, &trim);
        assert!((style.size - 9.0).abs() < f32::EPSILON);
        assert!((style.line_height - 1.2).abs() < f32::EPSILON);
        assert!(style.keep_with_next);
        assert_eq!(style.alignment, "center");
    }

    #[test]
    fn sparse_named_heading_styles_inherit_generated_heading_emphasis() {
        let document = serde_json::json!({
            "styles": [{
                "semanticRole": "heading",
                "definition": { "fontSizePoints": 19.0 }
            }]
        });
        let block = serde_json::json!({
            "type": "Heading",
            "headingLevel": 2,
            "styleRole": "heading"
        });
        let style = block_style(&document, &block, &standard_trim());
        assert_eq!(style.face, FontFace::SansBold);
        assert!((style.size - 19.0).abs() < f32::EPSILON);
    }

    #[test]
    fn direct_false_typography_overrides_named_style_emphasis() {
        let document = serde_json::json!({
            "styles": [{
                "semanticRole": "epigraph",
                "definition": { "italic": true, "smallCaps": true }
            }]
        });
        let block = serde_json::json!({
            "type": "Paragraph",
            "styleRole": "epigraph",
            "paragraphPresentation": { "italic": false, "smallCaps": false }
        });

        let style = block_style(&document, &block, &standard_trim());

        assert_eq!(style.face, FontFace::SerifRegular);
        assert!(!style.small_caps);
    }

    #[test]
    fn list_items_use_the_shared_hanging_indent() {
        let trim = standard_trim();
        let block = serde_json::json!({
            "type": "ListItem",
            "styleRole": "list-item"
        });
        let style = block_style(&Value::Null, &block, &trim);
        let defaults = &typography::defaults().list_item;
        assert!((style.indent - defaults.left_indent_em * style.size).abs() < f32::EPSILON);
        assert!(
            (style.first_line_indent + defaults.hanging_indent_em * style.size).abs()
                < f32::EPSILON
        );
    }

    #[test]
    fn list_markers_are_separate_artifacts_for_every_content_alignment() {
        let trim = crate::model::Trim {
            width_inches: 3.5,
            height_inches: 5.0,
            margin_inches: 0.75,
            body_font_size_points: 11.0,
            body_line_height: 1.4,
            bleed_inches: 0.0,
            mirror_margins: false,
            recto_chapter_starts: false,
            minimum_widow_lines: 1,
            minimum_orphan_lines: 1,
        };
        let source = "The authored list item wraps across enough lines to verify marker geometry and source ranges.";
        let block = serde_json::json!({
            "type": "ListItem",
            "content": [{ "text": source, "marks": [] }]
        });
        let marker = typography::defaults().list_item.bullet.as_str();

        for alignment in ["left", "center", "right", "justify"] {
            let mut style = block_style(&Value::Null, &block, &trim);
            style.alignment = alignment.to_owned();
            let display_text = display_block_text(&block);
            let content_runs = block_runs(&Value::Null, &block, &style);
            let mut pages = vec![empty_body_page()];
            let snapshot = vec![(0usize, 0usize)];
            append_list_item_with_gap(
                &mut pages,
                &display_text,
                &content_runs,
                &trim,
                &style,
                None,
                0.0,
            );
            assign_semantic_order_since(&mut pages, &snapshot, 7, "list-id", None, Some(source));

            let lines = pages
                .iter()
                .flat_map(|page| page.lines.iter())
                .collect::<Vec<_>>();
            let marker_line = lines
                .iter()
                .find(|line| line.artifact && line.text == marker)
                .expect("separate list marker");
            let content_lines = lines
                .iter()
                .filter(|line| !line.artifact && line.semantic_role == LayoutSemanticRole::ListItem)
                .collect::<Vec<_>>();
            assert!(
                content_lines.len() > 1,
                "the fixture must wrap for {alignment}"
            );

            let authored_x = trim.margin_inches * 72.0 + style.indent;
            assert_eq!(marker_line.word_spacing, 0.0);
            assert!(marker_line.source_start_utf16.is_none());
            assert_eq!(marker_line.semantic_id.as_deref(), Some("list-id"));
            assert!((marker_line.y - content_lines[0].y).abs() < 0.01);

            let available_width =
                (trim.width_inches - 2.0 * trim.margin_inches) * 72.0 - style.indent;
            for line in &content_lines {
                let measured = measured_run_width(&line.runs, line.size);
                let alignment_offset = match alignment {
                    "center" => ((available_width - measured) / 2.0).max(0.0),
                    "right" => (available_width - measured).max(0.0),
                    _ => 0.0,
                };
                assert!((line.x - (authored_x + alignment_offset)).abs() < 0.01);
                assert_eq!(line.semantic_id.as_deref(), Some("list-id"));
                assert!(line.source_start_utf16.is_some());
                assert!(!line.text.starts_with(marker));
            }
            assert!((marker_line.x - (content_lines[0].x + style.first_line_indent)).abs() < 0.01);
        }
    }

    #[test]
    fn generated_chapter_heading_uses_the_shared_heading_defaults() {
        let title = "Generated heading";
        let layout = paginate(&request_with_document(serde_json::json!({
            "title": "Test",
            "sections": [{
                "chapters": [{ "title": title, "blocks": [] }]
            }]
        })))
        .expect("generated chapter heading layout");
        let line = layout
            .pages
            .iter()
            .flat_map(|page| page.lines.iter())
            .find(|line| line.text == title)
            .expect("generated heading line");
        let defaults = &typography::defaults().chapter_heading;
        assert_eq!(line.semantic_role, LayoutSemanticRole::Heading1);
        assert!((line.size - defaults.font_size_points).abs() < 0.001);
        assert_eq!(
            line.runs[0].face,
            regular_face(font_family(&defaults.font_family_key))
                .with_weight(defaults.font_weight, defaults.italic)
        );
        let width = measure_text(line.runs[0].face, title, line.size);
        let content_width =
            (standard_trim().width_inches - 2.0 * standard_trim().margin_inches) * 72.0;
        let expected_x = standard_trim().margin_inches * 72.0 + (content_width - width) / 2.0;
        assert!((line.x - expected_x).abs() < 0.01);
    }

    #[test]
    fn mixed_heading_and_body_sizes_advance_the_vertical_cursor_without_overlap() {
        let trim = crate::model::Trim {
            width_inches: 6.0,
            height_inches: 9.0,
            margin_inches: 0.75,
            body_font_size_points: 11.0,
            body_line_height: 1.4,
            bleed_inches: 0.0,
            mirror_margins: true,
            recto_chapter_starts: true,
            minimum_widow_lines: 2,
            minimum_orphan_lines: 2,
        };
        let mut pages = vec![empty_body_page()];
        let chapter_heading = BlockStyle::chapter_heading(&trim);
        append_styled_text(&mut pages, "Chapter heading", &trim, &chapter_heading);
        let heading = BlockStyle {
            size: 20.0,
            line_height: 1.4,
            indent: 0.0,
            right_indent: 0.0,
            first_line_indent: 0.0,
            page_break_before: false,
            keep_with_next: true,
            alignment: "left".to_owned(),
            face: FontFace::SansBold,
            font_weight: 700,
            small_caps: false,
            space_before: 0.0,
            space_after: 0.0,
            semantic_role: LayoutSemanticRole::Heading2,
        };
        append_styled_text(&mut pages, "Section heading", &trim, &heading);
        append_styled_text(
            &mut pages,
            "The opening paragraph must sit below both headings.",
            &trim,
            &BlockStyle::body(&trim),
        );
        let lines = &pages[0].lines;
        assert_eq!(lines.len(), 3);
        assert!(lines.windows(2).all(|pair| {
            let upper_line_bottom = pair[0].y - pair[0].size * 0.30;
            let lower_line_top = pair[1].y + pair[1].size * 0.82;
            upper_line_bottom >= lower_line_top - 0.01
        }));
    }

    #[test]
    fn inline_marks_are_preserved_as_distinct_typographic_runs() {
        let block = serde_json::json!({
            "type": "Paragraph",
            "content": [
                { "text": "plain ", "marks": [] },
                { "text": "strong", "marks": [{ "type": "Strong" }] },
                { "text": " emphasis", "marks": [{ "type": "Emphasis" }] },
                { "text": " code", "marks": [{ "type": "Code" }] },
                { "text": " caps", "marks": [{ "type": "SmallCaps" }] }
            ]
        });

        let runs = block_runs(&Value::Null, &block, &BlockStyle::body(&standard_trim()));

        assert_eq!(runs.len(), 6);
        assert_eq!(runs[0].face, crate::model::FontFace::SerifRegular);
        assert_eq!(runs[1].face, crate::model::FontFace::SerifBold);
        assert_eq!(runs[2].face, crate::model::FontFace::SerifItalic);
        assert_eq!(runs[3].face, crate::model::FontFace::MonoRegular);
        assert_eq!(runs[4].text, " ");
        assert_eq!(runs[5].text, "CAPS");
        assert!((runs[5].size_scale - 0.8).abs() < 0.001);
    }

    #[test]
    fn small_caps_keep_authored_capitals_full_size_and_scale_lowercase_segments() {
        let block = serde_json::json!({
            "type": "Paragraph",
            "content": [{
                "text": "ABcd",
                "marks": [{ "type": "SmallCaps" }]
            }]
        });
        let runs = block_runs(&Value::Null, &block, &BlockStyle::body(&standard_trim()));
        assert_eq!(
            runs.iter().map(|run| run.text.as_str()).collect::<String>(),
            "ABCD"
        );
        assert_eq!(runs[0].text, "AB");
        assert!((runs[0].size_scale - 1.0).abs() < f32::EPSILON);
        assert_eq!(runs[1].text, "CD");
        assert!((runs[1].size_scale - 0.8).abs() < f32::EPSILON);
    }

    #[test]
    fn baseline_offset_uses_face_metrics_size_scaling_and_shift() {
        let trim = standard_trim();
        let regular_face = FontFace::SerifRegular;
        let regular_runs = single_run("body", regular_face);
        let regular =
            line_baseline_offset_points(trim.body_font_size_points, regular_face, &regular_runs);
        let expected_regular =
            crate::font::descent_points(regular_face, trim.body_font_size_points);
        assert!((regular - expected_regular).abs() < 0.0001);

        let scaled_runs = vec![LayoutRun {
            text: "large".to_owned(),
            face: FontFace::SansRegular,
            underline: false,
            strikethrough: false,
            baseline_shift_em: 0.0,
            size_scale: 1.5,
            language: None,
        }];
        let scaled =
            line_baseline_offset_points(trim.body_font_size_points, regular_face, &scaled_runs);
        let expected_scaled =
            crate::font::descent_points(FontFace::SansRegular, trim.body_font_size_points * 1.5);
        assert!((scaled - expected_scaled).abs() < 0.0001);
        assert_ne!(regular.to_bits(), scaled.to_bits());

        let mixed_runs = vec![
            LayoutRun {
                text: "base".to_owned(),
                face: regular_face,
                underline: false,
                strikethrough: false,
                baseline_shift_em: 0.0,
                size_scale: 1.0,
                language: None,
            },
            LayoutRun {
                text: "subscript".to_owned(),
                face: FontFace::SansRegular,
                underline: false,
                strikethrough: false,
                baseline_shift_em: -0.2,
                size_scale: 0.7,
                language: None,
            },
        ];
        let mixed =
            line_baseline_offset_points(trim.body_font_size_points, regular_face, &mixed_runs);
        let expected_subscript =
            crate::font::descent_points(FontFace::SansRegular, trim.body_font_size_points * 0.7)
                + trim.body_font_size_points * 0.7 * 0.2;
        assert!((mixed - expected_subscript.max(expected_regular)).abs() < 0.0001);
        assert_eq!(
            mixed.to_bits(),
            line_baseline_offset_points(trim.body_font_size_points, regular_face, &mixed_runs)
                .to_bits()
        );
    }

    #[test]
    fn named_character_styles_apply_to_marked_runs() {
        let document = serde_json::json!({
            "styles": [{
                "kind": "Character",
                "semanticRole": "quiet-code",
                "definition": {
                    "fontFamilyKey": "mono",
                    "fontWeight": 700,
                    "italic": true,
                    "smallCaps": true,
                    "fontSizePoints": 8.8
                }
            }]
        });
        let block = serde_json::json!({
            "type": "Paragraph",
            "content": [{
                "text": "command",
                "marks": [{ "type": "CharacterStyle", "value": "quiet-code" }]
            }]
        });
        let runs = block_runs(&document, &block, &BlockStyle::body(&standard_trim()));

        assert_eq!(runs[0].face, FontFace::MonoBoldItalic);
        assert_eq!(runs[0].text, "COMMAND");
        assert!((runs[0].size_scale - 0.64).abs() < 0.001);
    }

    #[test]
    fn rotated_group_moves_child_around_the_group_center_on_a_non_square_surface() {
        let scene = serde_json::json!({
            "surface": { "widthPoints": 200.0, "heightPoints": 100.0 },
            "objects": [
                {
                    "id": "group",
                    "kind": "Group",
                    "rotationDegrees": 90.0,
                    "bounds": { "xPercent": 20.0, "yPercent": 20.0, "widthPercent": 40.0, "heightPercent": 40.0 }
                },
                {
                    "id": "child",
                    "kind": "Rectangle",
                    "groupId": "group",
                    "bounds": { "xPercent": 0.0, "yPercent": 0.0, "widthPercent": 20.0, "heightPercent": 20.0 }
                }
            ]
        });

        let flattened = flatten_composition_objects(&scene).expect("flattened scene");
        let bounds = &flattened[0]["bounds"];

        assert!((bounds["xPercent"].as_f64().unwrap() - 44.0).abs() < 0.001);
        assert!((bounds["yPercent"].as_f64().unwrap() - 4.0).abs() < 0.001);
        assert_eq!(flattened[0]["rotationDegrees"], 90.0);
    }

    #[test]
    fn cover_reflow_transforms_group_once_and_preserves_child_local_geometry() {
        let mut request = request_with_document(serde_json::json!({}));
        let panel = request.trim.width_inches * 72.0;
        let old_width = panel * 2.0 + 12.0;
        let height = request.trim.height_inches * 72.0;
        request.cover = Some(crate::model::Cover {
            bleed_inches: 0.0,
            back_copy: String::new(),
            title: "Grouped title".to_owned(),
            subtitle: String::new(),
            author: String::new(),
            spine_text: String::new(),
            spine_reading_direction: "TopToBottom".to_owned(),
            background_color: "#ffffff".to_owned(),
            isbn: None,
            barcode_mode: "None".to_owned(),
            asset_id: None,
            image_crop_x_percent: 50.0,
            image_crop_y_percent: 50.0,
            scene: Some(serde_json::json!({
                "surface": { "widthPoints": old_width, "heightPoints": height },
                "layers": [{ "id": "layer", "name": "Content", "order": 0 }],
                "objects": [
                    { "id": "group", "layerId": "layer", "kind": "Group", "regionConstraint": "Front", "bounds": { "xPercent": 55.0, "yPercent": 10.0, "widthPercent": 35.0, "heightPercent": 30.0 } },
                    { "id": "child", "layerId": "layer", "kind": "Text", "groupId": "group", "textBinding": "title", "fontSizePoints": 12.0, "semanticRole": "Paragraph", "readingOrder": 1, "bounds": { "xPercent": 10.0, "yPercent": 20.0, "widthPercent": 50.0, "heightPercent": 40.0 } }
                ]
            })),
            scenes: Default::default(),
            surfaces: Vec::new(),
        });
        let unchanged = cover_scene_layout(
            &request,
            old_width,
            height,
            false,
            false,
            &mut Vec::new(),
            None,
        )
        .expect("same geometry");
        let expanded = cover_scene_layout(
            &request,
            old_width + 18.0,
            height,
            false,
            false,
            &mut Vec::new(),
            None,
        )
        .expect("changed spine");
        assert_eq!(unchanged.lines.len(), 1);
        assert_eq!(expanded.lines.len(), 1);
        let local_width = unchanged.lines[0].x - old_width * 0.55;
        assert!(
            (local_width - old_width * 0.35 * 0.10).abs() < 0.05,
            "child local offset must be applied exactly once"
        );
        assert!(
            expanded.lines[0].x > unchanged.lines[0].x,
            "front-bound group must move with an expanded spine"
        );
    }

    #[test]
    fn empty_bound_text_frames_are_skipped_but_unbound_frames_are_rejected() {
        let composition = serde_json::json!({
            "id": "composition",
            "name": "Empty optional copy",
            "semanticBlocks": [{ "id": "empty", "type": "Paragraph", "content": [] }],
            "variants": [{ "scene": {
                "surface": { "kind": "SinglePage", "widthPoints": 432, "heightPoints": 648 },
                "layers": [{ "id": "layer", "order": 0, "visible": true }],
                "objects": [{
                    "id": "bound-empty",
                    "layerId": "layer",
                    "kind": "Text",
                    "bounds": { "xPercent": 10, "yPercent": 10, "widthPercent": 80, "heightPercent": 12 },
                    "contentReferences": [{ "blockId": "empty" }],
                    "fontFamilyKey": "sans",
                    "fontSizePoints": 12,
                    "semanticRole": "Paragraph",
                    "readingOrder": 1
                }]
            }}]
        });
        let page = designed_page(
            &composition,
            &serde_json::json!({}),
            &standard_trim(),
            LayoutTolerance::default(),
            &mut Vec::new(),
        )
        .expect("a valid empty semantic reference should be skipped");
        assert!(page.lines.is_empty());

        let mut unbound = composition.clone();
        unbound["variants"][0]["scene"]["objects"][0]["contentReferences"] = serde_json::json!([]);
        let error = designed_page(
            &unbound,
            &serde_json::json!({}),
            &standard_trim(),
            LayoutTolerance::default(),
            &mut Vec::new(),
        )
        .expect_err("a text frame with neither binding nor reference must be rejected");
        assert_eq!(error.code.as_ref(), "PRESS_COMPOSITION_TEXT_UNBOUND");
    }

    #[test]
    fn empty_optional_cover_binding_is_not_rendered_or_rejected() {
        let mut request = request_with_document(serde_json::json!({
            "title": "Cover",
            "author": "Author",
            "language": "en",
            "sections": []
        }));
        request.cover = Some(crate::model::Cover {
            bleed_inches: 0.0,
            back_copy: String::new(),
            title: "Cover".to_owned(),
            subtitle: String::new(),
            author: "Author".to_owned(),
            spine_text: String::new(),
            spine_reading_direction: "TopToBottom".to_owned(),
            background_color: "#ffffff".to_owned(),
            isbn: None,
            barcode_mode: "None".to_owned(),
            asset_id: None,
            image_crop_x_percent: 50.0,
            image_crop_y_percent: 50.0,
            scene: Some(serde_json::json!({
                "surface": { "kind": "SinglePage", "widthPoints": 432, "heightPoints": 648 },
                "layers": [{ "id": "layer", "order": 0, "visible": true }],
                "objects": [{
                    "id": "optional-subtitle",
                    "layerId": "layer",
                    "kind": "Text",
                    "bounds": { "xPercent": 10, "yPercent": 10, "widthPercent": 80, "heightPercent": 12 },
                    "textBinding": "subtitle",
                    "fontFamilyKey": "sans",
                    "fontSizePoints": 12,
                    "semanticRole": "Heading2",
                    "readingOrder": 1
                }]
            })),
            scenes: Default::default(),
            surfaces: Vec::new(),
        });
        let page = cover_layout(
            &request,
            request.trim.width_inches * 72.0,
            request.trim.height_inches * 72.0,
            None,
        )
        .expect("empty optional cover binding should be omitted");
        assert!(page.lines.is_empty());
    }

    #[test]
    fn front_and_back_publication_sections_surround_the_body_in_reading_order() {
        let request = request_with_document(serde_json::json!({
            "title": "Ordered publication sections",
            "author": "Author",
            "language": "en",
            "publicationSections": [
                { "id": "back", "anchor": "Back", "title": "Acknowledgments", "blocks": [
                    { "id": "back-copy", "type": "Heading", "content": [{ "text": "Acknowledgments" }] }
                ] },
                { "id": "front", "anchor": "Front", "title": "Dedication", "blocks": [
                    { "id": "front-copy", "type": "Heading", "content": [{ "text": "Dedication" }] }
                ] }
            ],
            "sections": [{
                "chapters": [{
                    "id": "chapter",
                    "title": "Body chapter",
                    "blocks": [{ "id": "block", "type": "Paragraph", "content": [{ "text": "Body text" }] }]
                }]
            }]
        }));

        let layout = paginate(&request).expect("layout");
        let dedication = page_containing(&layout, "Dedication");
        let body = page_containing(&layout, "Body text");
        let acknowledgments = page_containing(&layout, "Acknowledgments");

        assert!(dedication < body);
        assert!(body < acknowledgments);
    }

    #[test]
    fn act_heading_options_apply_to_inline_and_divider_page_layouts() {
        let request = request_with_document(serde_json::json!({
            "title": "Acts",
            "author": "Author",
            "language": "en",
            "includeActHeadings": true,
            "numberActs": true,
            "sections": [{
                "id": "act",
                "title": "Discovery",
                "includePage": false,
                "includeHeading": true,
                "chapters": [{
                    "id": "chapter",
                    "title": "Chapter",
                    "includeHeading": false,
                    "blocks": [{ "id": "block", "type": "Paragraph", "content": [{ "text": "Body" }] }]
                }]
            }]
        }));

        let layout = paginate(&request).expect("layout");
        assert!(layout.pages.iter().any(|page| {
            page.lines
                .iter()
                .any(|line| line.text == "Act 1: Discovery")
        }));
        assert!(
            !layout
                .pages
                .iter()
                .any(|page| page.lines.iter().any(|line| line.text == "Chapter"))
        );

        let hidden_divider_heading = request_with_document(serde_json::json!({
            "title": "Acts",
            "author": "Author",
            "language": "en",
            "includeActHeadings": false,
            "numberActs": true,
            "sections": [{
                "id": "act",
                "title": "Discovery",
                "includePage": true,
                "includeHeading": false,
                "chapters": [{
                    "id": "chapter",
                    "title": "Chapter",
                    "includeHeading": false,
                    "blocks": [{ "id": "block", "type": "Paragraph", "content": [{ "text": "Body" }] }]
                }]
            }]
        }));
        let hidden_layout = paginate(&hidden_divider_heading).expect("layout");
        assert!(!hidden_layout.pages.iter().any(|page| {
            page.lines
                .iter()
                .any(|line| line.text.contains("Discovery"))
        }));
    }

    #[test]
    fn designed_page_caption_sits_below_and_aligns_with_the_image() {
        let request = request_with_document(serde_json::json!({
            "title": "Caption",
            "author": "Author",
            "language": "en",
            "sections": []
        }));
        let page = dedicated_figure_page_with_layout(
            &request.trim,
            "A deliberately long Designed Page caption that wraps safely beneath narrow right-aligned artwork.",
            &[],
            &BlockStyle::caption(&request.trim),
            "surface".to_owned(),
            30.0,
            50.0,
            50.0,
            "Right",
        );
        let image = page.images.first().expect("image");
        let right_edge = request.trim.width_inches * 72.0 - request.trim.margin_inches * 72.0;

        assert!(page.lines.len() >= 3);
        assert!((image.x + image.width - right_edge).abs() < 0.01);
        assert!(page.lines.iter().all(|caption| {
            let measured = measured_run_width(&caption.runs, caption.size);
            (caption.x - (image.x + (image.width - measured).max(0.0) / 2.0)).abs() < 0.01
                && measured <= image.width + 0.01
                && caption.y < image.y
                && caption.y - caption.size * 0.30 >= request.trim.margin_inches * 72.0 - 0.01
        }));
    }

    #[test]
    fn composition_references_resolve_disjoint_utf16_ranges_without_repeating_blocks() {
        let blocks = vec![serde_json::json!({
            "id": "copy",
            "type": "Paragraph",
            "content": [{ "text": "Alpha 😀 Omega" }]
        })];
        let first = serde_json::json!({
            "blockId": "copy",
            "startOffset": 0,
            "endOffset": 5
        });
        let second = serde_json::json!({
            "blockId": "copy",
            "startOffset": 9,
            "endOffset": 14
        });

        assert_eq!(resolve_content_reference(&blocks, &first).unwrap(), "Alpha");
        assert_eq!(
            resolve_content_reference(&blocks, &second).unwrap(),
            "Omega"
        );
        assert!(validate_semantic_coverage(&blocks, &[first, second]).is_err());
    }

    #[test]
    fn composition_references_reject_surrogate_splits_and_overlap() {
        let blocks = vec![serde_json::json!({
            "id": "copy",
            "type": "Paragraph",
            "content": [{ "text": "A😀B" }]
        })];
        let split = serde_json::json!({ "blockId": "copy", "startOffset": 1, "endOffset": 2 });
        assert!(resolve_content_reference(&blocks, &split).is_err());
        let references = vec![
            serde_json::json!({ "blockId": "copy", "startOffset": 0, "endOffset": 3 }),
            serde_json::json!({ "blockId": "copy", "startOffset": 2, "endOffset": 4 }),
        ];
        assert!(validate_semantic_coverage(&blocks, &references).is_err());
    }

    #[test]
    fn composition_references_allow_empty_bound_publication_fields() {
        let blocks = vec![serde_json::json!({
            "id": "section-text-publisher",
            "type": "Paragraph",
            "content": [{ "text": "" }]
        })];
        let reference = serde_json::json!({ "blockId": "section-text-publisher" });

        assert_eq!(resolve_content_reference(&blocks, &reference).unwrap(), "");
        assert!(validate_semantic_coverage(&blocks, &[reference]).is_ok());
    }

    #[test]
    fn designed_page_preserves_layer_then_z_paint_order_and_object_opacity() {
        let composition = serde_json::json!({
            "id": "composition",
            "name": "Layered",
            "semanticBlocks": [{"id":"copy","type":"Paragraph","content":[{"text":"Copy"}]}],
            "variants": [{"scene": {
                "surface": {"widthPoints":432,"heightPoints":648},
                "layers": [
                    {"id":"lower","order":0,"visible":true},
                    {"id":"upper","order":1,"visible":true}
                ],
                "objects": [
                    {"id":"text","layerId":"upper","kind":"Text","zIndex":0,"opacity":0.75,"bounds":{"xPercent":10,"yPercent":10,"widthPercent":80,"heightPercent":20},"contentReferences":[{"blockId":"copy"}],"semanticRole":"Paragraph","readingOrder":1},
                    {"id":"shape","layerId":"lower","kind":"Rectangle","zIndex":9,"opacity":0.5,"bounds":{"xPercent":0,"yPercent":0,"widthPercent":100,"heightPercent":100},"fillColor":"#ff0000","semanticRole":"Artifact"}
                ]
            }}]
        });
        let page = designed_page(
            &composition,
            &serde_json::json!({}),
            &standard_trim(),
            LayoutTolerance::default(),
            &mut Vec::new(),
        )
        .expect("designed page");
        assert_eq!(
            page.paint_order,
            vec![LayoutPaint::Shape(0), LayoutPaint::Line(0)]
        );
        assert!((page.shapes[0].opacity - 0.5).abs() < 0.001);
        assert!((page.lines[0].opacity - 0.75).abs() < 0.001);
    }

    #[test]
    fn facing_spread_split_preserves_authored_paint_order_on_each_leaf() {
        let composition = serde_json::json!({
            "id": "composition",
            "name": "Spread",
            "semanticBlocks": [{"id":"copy","type":"Paragraph","content":[{"text":"Copy"}]}],
            "variants": [{"scene": {
                "surface": {"kind":"FacingSpread","widthPoints":864,"heightPoints":648},
                "layers": [{"id":"layer","order":0,"visible":true}],
                "objects": [
                    {"id":"shape","layerId":"layer","kind":"Rectangle","zIndex":0,"bounds":{"xPercent":0,"yPercent":0,"widthPercent":100,"heightPercent":100},"fillColor":"#ffffff","semanticRole":"Artifact"},
                    {"id":"image","layerId":"layer","kind":"Image","zIndex":1,"bounds":{"xPercent":0,"yPercent":0,"widthPercent":100,"heightPercent":100},"imageId":"asset","decorative":true,"semanticRole":"Artifact"},
                    {"id":"text","layerId":"layer","kind":"Text","zIndex":2,"bounds":{"xPercent":5,"yPercent":10,"widthPercent":30,"heightPercent":20},"contentReferences":[{"blockId":"copy"}],"semanticRole":"Paragraph","readingOrder":1}
                ]
            }}]
        });
        let page = designed_page(
            &composition,
            &serde_json::json!({}),
            &standard_trim(),
            LayoutTolerance::default(),
            &mut Vec::new(),
        )
        .expect("spread page");
        let leaves = split_facing_spread(page, 432.0);

        assert_eq!(
            leaves[0].paint_order,
            vec![
                LayoutPaint::Shape(0),
                LayoutPaint::Image(0),
                LayoutPaint::Line(0)
            ]
        );
        assert_eq!(
            leaves[1].paint_order,
            vec![LayoutPaint::Shape(0), LayoutPaint::Image(0)]
        );
    }

    #[test]
    fn facing_spread_rejects_semantic_text_that_crosses_the_gutter() {
        let composition = serde_json::json!({
            "id": "composition",
            "name": "Spread",
            "semanticBlocks": [{"id":"copy","type":"Paragraph","content":[{"text":"This semantic line crosses the gutter"}]}],
            "variants": [{"scene": {
                "surface": {"kind":"FacingSpread","widthPoints":864,"heightPoints":648},
                "layers": [{"id":"layer","order":0,"visible":true}],
                "objects": [{"id":"text","layerId":"layer","kind":"Text","bounds":{"xPercent":49,"yPercent":10,"widthPercent":45,"heightPercent":20},"contentReferences":[{"blockId":"copy"}],"fontSizePoints":18,"semanticRole":"Paragraph","readingOrder":1}]
            }}]
        });

        let error = designed_pages(
            &composition,
            &serde_json::json!({}),
            &standard_trim(),
            false,
            false,
            LayoutTolerance::default(),
            &mut Vec::new(),
        )
        .expect_err("gutter-crossing text must be blocked");
        assert_eq!(
            error.code.as_ref(),
            "PRESS_FACING_SPREAD_TEXT_CROSSES_GUTTER"
        );
    }

    #[test]
    fn full_wrap_cover_places_rotated_spine_copy_in_the_calculated_spine() {
        let mut request = request_with_document(serde_json::json!({
            "title": "Cover",
            "author": "Author",
            "language": "en",
            "sections": []
        }));
        request.cover = Some(crate::model::Cover {
            bleed_inches: 0.125,
            back_copy: "Back copy".to_owned(),
            title: "Front title".to_owned(),
            subtitle: "Subtitle".to_owned(),
            author: "Author".to_owned(),
            spine_text: "Spine title".to_owned(),
            spine_reading_direction: "TopToBottom".to_owned(),
            background_color: "#16324f".to_owned(),
            isbn: Some("9780306406157".to_owned()),
            barcode_mode: "LorekeeperBarcode".to_owned(),
            asset_id: None,
            image_crop_x_percent: 50.0,
            image_crop_y_percent: 50.0,
            scene: None,
            scenes: Default::default(),
            surfaces: Vec::new(),
        });
        let spine_width = 1.0 * 72.0;
        let cover_width = request.trim.width_inches * 144.0
            + spine_width
            + request.cover.as_ref().unwrap().bleed_inches * 144.0;
        let page = cover_layout(
            &request,
            cover_width,
            request.trim.height_inches * 72.0 + 18.0,
            None,
        )
        .expect("cover layout");
        let spine = page
            .lines
            .iter()
            .find(|line| line.text == "Spine title")
            .expect("spine text");
        let expected_center = request.cover.as_ref().unwrap().bleed_inches * 72.0
            + request.trim.width_inches * 72.0
            + spine_width / 2.0;

        assert_eq!(spine.rotation_degrees, 90.0);
        assert!((spine.x - expected_center).abs() < 0.01);
    }

    fn standard_trim() -> crate::model::Trim {
        crate::model::Trim {
            width_inches: 6.0,
            height_inches: 9.0,
            margin_inches: 0.75,
            body_font_size_points: 11.0,
            body_line_height: 1.4,
            bleed_inches: 0.0,
            mirror_margins: true,
            recto_chapter_starts: false,
            minimum_widow_lines: 2,
            minimum_orphan_lines: 2,
        }
    }

    fn request_with_document(document: Value) -> RenderRequest {
        RenderRequest {
            protocol_version: 10,
            job_id: "1".repeat(32),
            profile: "kdp-paperback-v2".to_owned(),
            ink: "BlackAndWhite".to_owned(),
            print_artifact_profile: None,
            output_purpose: OutputPurpose::Publication,
            layout_trace_mode: None,
            document,
            trim: standard_trim(),
            cover: None,
            assets: Vec::new(),
            fonts: Vec::new(),
        }
    }

    fn page_containing(layout: &LayoutDocument, needle: &str) -> usize {
        layout
            .pages
            .iter()
            .position(|page| page.lines.iter().any(|line| line.text.contains(needle)))
            .unwrap_or_else(|| panic!("missing page containing {needle}"))
    }
}
