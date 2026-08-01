use std::collections::BTreeSet;
use std::fs;
use std::io::Cursor;
use std::path::{Component, Path};

use hypher::{Lang, hyphenate};
use serde_json::Value;
use sha2::{Digest, Sha256};
use unicode_linebreak::linebreaks;

use crate::font::{assert_supported_language, measure_text, subset_for_layout};
use crate::image::prepare_images;
use crate::inspect;
use crate::model::{
    Artifact, Diagnostic, FontEvidence, FontFace, FontFamily, ImageEvidence, LayoutDocument,
    LayoutImage, LayoutLine, LayoutPage, LayoutRun, PageKind, PageMapEntry, RenderRequest,
    RenderResponse, ValidationEvidence,
};
use crate::pdf::{PdfOptions, cover_background_total_ink_percent, write_pdf_cancellable};

const MAX_ASSETS: usize = 512;
const MAX_ASSET_BYTES: u64 = 256 * 1024 * 1024;
const MAX_JOB_BYTES: u64 = 1024 * 1024 * 1024;
const MAX_REQUEST_BYTES: u64 = 64 * 1024 * 1024;
const MAX_PAGES: usize = 10_000;
type RenderResult<T> = Result<T, Box<RenderResponse>>;

pub fn run(job_root: &Path) -> RenderResult<()> {
    let request = load_request(job_root)?;
    let validated_assets = validate_request(&request, job_root)?;
    ensure_output_is_safe(job_root)?;
    ensure_not_cancelled(job_root)?;

    let layout = paginate_with_cancellation(&request, Some(job_root))?;
    if layout.pages.len() > MAX_PAGES {
        return Err(Box::new(RenderResponse::failed(
            "failed",
            Diagnostic::error(
                "PRESS_PAGE_LIMIT",
                format!("The document exceeds {MAX_PAGES} pages."),
            ),
        )));
    }
    let mut cover_width = 0.0;
    let mut spine_width = 0.0;
    let cover_page = if let Some(cover) = request.cover.as_ref() {
        spine_width = layout.pages.len() as f32 * cover.paper_caliper_inches_per_page * 72.0;
        cover_width = request.trim.width_inches * 144.0 + spine_width + cover.bleed_inches * 144.0;
        Some(
            cover_layout(&request, cover_width)
                .map_err(|diagnostic| Box::new(RenderResponse::failed("rejected", diagnostic)))?,
        )
    } else {
        None
    };
    let mut font_pages = layout.pages.clone();
    font_pages.extend(cover_page.iter().cloned());
    let fonts = subset_for_layout(&font_pages)
        .map_err(|diagnostic| Box::new(RenderResponse::failed("rejected", diagnostic)))?;

    let output = job_root.join("output");
    let staging = StagingDirectory::create(job_root.join(".output-staging"))?;
    ensure_not_cancelled(job_root)?;

    let is_pdfx = request.profile == "ingram-paperback-pdfx1a-v1";
    let interior_images = prepare_images(
        &request,
        &validated_assets,
        is_pdfx,
        request.ink == "BlackAndWhite",
    )
    .map_err(|diagnostic| Box::new(RenderResponse::failed("rejected", diagnostic)))?;
    let cover_images = prepare_images(&request, &validated_assets, is_pdfx, false)
        .map_err(|diagnostic| Box::new(RenderResponse::failed("rejected", diagnostic)))?;
    let mut maximum_total_ink_percent = interior_images
        .values()
        .chain(cover_images.values())
        .map(|image| image.maximum_total_ink_percent)
        .fold(0.0_f32, f32::max);
    if is_pdfx && let Some(cover) = request.cover.as_ref() {
        maximum_total_ink_percent = maximum_total_ink_percent
            .max(cover_background_total_ink_percent(&cover.background_color));
    }
    let interior_options = PdfOptions::interior(&request, is_pdfx);
    let interior_bytes = write_pdf_cancellable(
        &layout.pages,
        &fonts,
        &interior_images,
        &interior_options,
        || job_root.join("cancel.requested").exists(),
    )
    .map_err(pdf_failure)?;
    let interior_path = staging.path().join("interior.pdf");
    fs::write(&interior_path, &interior_bytes).map_err(io_failure)?;
    let interior_inspection = inspect::validate(&interior_path, &interior_options)
        .map_err(|diagnostic| Box::new(RenderResponse::failed("failed", diagnostic)))?;

    let mut artifacts = vec![artifact(
        "interior-pdf",
        "output/interior.pdf",
        &interior_bytes,
        layout.pages.len(),
    )];
    if let Some(rendered_cover) = &cover_page {
        let cover_options = PdfOptions::cover(&request, is_pdfx, cover_width, spine_width);
        let cover_bytes = write_pdf_cancellable(
            std::slice::from_ref(rendered_cover),
            &fonts,
            &cover_images,
            &cover_options,
            || job_root.join("cancel.requested").exists(),
        )
        .map_err(pdf_failure)?;
        let cover_path = staging.path().join("cover.pdf");
        fs::write(&cover_path, &cover_bytes).map_err(io_failure)?;
        inspect::validate(&cover_path, &cover_options)
            .map_err(|diagnostic| Box::new(RenderResponse::failed("failed", diagnostic)))?;
        artifacts.push(artifact("cover-pdf", "output/cover.pdf", &cover_bytes, 1));
    }
    ensure_not_cancelled(job_root)?;

    let image_evidence = layout
        .pages
        .iter()
        .chain(cover_page.iter())
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
    let minimum_effective_dpi = image_evidence
        .iter()
        .map(|item| item.effective_dpi)
        .reduce(f32::min);
    let mut diagnostics = Vec::new();
    if minimum_effective_dpi.is_some_and(|dpi| dpi < 300.0) {
        diagnostics.push(Diagnostic::warning(
            "PRESS_IMAGE_DPI_LOW",
            format!(
                "The lowest effective image resolution is {:.1} DPI; inspect the affected pages at proof size.",
                minimum_effective_dpi.unwrap_or_default()
            ),
        ));
    }

    staging.promote(&output)?;
    let response = RenderResponse {
        protocol_version: 3,
        renderer_version: env!("CARGO_PKG_VERSION"),
        job_id: Some(request.job_id.clone()),
        status: "completed".to_owned(),
        artifacts,
        page_map: layout.page_map,
        diagnostics,
        evidence: Some(ValidationEvidence {
            validation_status: "validated".to_owned(),
            claimed_standard: is_pdfx.then(|| "PDF/X-1a:2001".to_owned()),
            declared_standard: is_pdfx.then(|| "PDF/X-1a:2001".to_owned()),
            pdf_version: if is_pdfx { "1.3" } else { "1.7" }.to_owned(),
            toc_converged: layout.toc_converged,
            has_encryption: false,
            has_transparency: false,
            has_forbidden_actions: false,
            annotation_count: 0,
            fonts_embedded: interior_inspection.fonts_embedded,
            to_unicode_maps_present: interior_inspection.to_unicode,
            output_intent_count: if is_pdfx {
                if request.cover.is_some() { 2 } else { 1 }
            } else {
                0
            },
            maximum_total_ink_percent,
            rendered_features: layout.features,
            cover_width_points: cover_width,
            spine_width_points: spine_width,
            interior_width_points: request.trim.width_inches * 72.0,
            interior_height_points: request.trim.height_inches * 72.0,
            cover_height_points: request.cover.as_ref().map_or(0.0, |cover| {
                (request.trim.height_inches + cover.bleed_inches * 2.0) * 72.0
            }),
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
    Ok(())
}

pub fn trace(job_root: &Path) -> RenderResult<()> {
    let request = load_request(job_root)?;
    validate_request(&request, job_root)?;
    let layout = paginate(&request)?;
    let fonts = subset_for_layout(&layout.pages)
        .map_err(|diagnostic| Box::new(RenderResponse::failed("rejected", diagnostic)))?;
    let pages = layout.pages.iter().map(|page| {
        let lines = page.lines.iter().map(|line| {
            let fallback;
            let runs = if line.runs.is_empty() {
                fallback = vec![LayoutRun {
                    text: line.text.clone(), face: FontFace::SerifRegular, underline: false,
                    strikethrough: false, baseline_shift_em: 0.0, size_scale: 1.0,
                }];
                fallback.as_slice()
            } else { line.runs.as_slice() };
            let runs = runs.iter().map(|run| {
                let size = line.size * run.size_scale;
                let glyphs = fonts.get(&run.face).map_or_else(Vec::new, |font| font.shape(&run.text, size));
                serde_json::json!({ "text": run.text, "face": run.face, "glyphs": glyphs })
            }).collect::<Vec<_>>();
            serde_json::json!({ "text": line.text, "size": line.size, "x": line.x, "y": line.y, "runs": runs })
        }).collect::<Vec<_>>();
        let kind = match page.kind { PageKind::Picture => "PicturePage", PageKind::Body => "Body", PageKind::Blank => "Blank", PageKind::Cover => "Cover" };
        serde_json::json!({
            "kind": kind,
            "rotationDegrees": page.images.first().map_or(0.0, |image| image.rotation_degrees),
            "lines": lines,
            "images": page.images,
        })
    }).collect::<Vec<_>>();
    println!(
        "{}",
        serde_json::to_string(&serde_json::json!({
            "protocolVersion": 3,
            "rendererVersion": env!("CARGO_PKG_VERSION"),
            "pages": pages,
            "pageMap": layout.page_map,
            "features": layout.features,
        }))
        .expect("serialize layout trace")
    );
    Ok(())
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
    let (target_width, target_height) = if (placement.rotation_degrees - 90.0).abs() < f32::EPSILON
    {
        (placement.height, placement.width)
    } else {
        (placement.width, placement.height)
    };
    let width_scale = target_width / source_width;
    let height_scale = target_height / image.height as f32;
    let points_per_pixel = if placement.contain {
        width_scale.min(height_scale)
    } else {
        width_scale.max(height_scale)
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
    serde_json::from_slice(&bytes).map_err(|error| {
        Box::new(RenderResponse::failed(
            "rejected",
            Diagnostic::error("PRESS_REQUEST_INVALID", error.to_string()),
        ))
    })
}

fn validate_request(
    request: &RenderRequest,
    job_root: &Path,
) -> RenderResult<std::collections::BTreeMap<String, Vec<u8>>> {
    if request.protocol_version != 3 {
        return reject(
            "PRESS_PROTOCOL_INVALID",
            "Lorekeeper Press requires protocol version 3.",
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
        "generic-paperback-v1" | "kdp-paperback-v1" | "ingram-paperback-pdfx1a-v1"
    ) {
        return reject(
            "PRESS_PROFILE_UNSUPPORTED",
            format!("The profile '{}' is unsupported.", request.profile),
        );
    }
    if !matches!(request.ink.as_str(), "BlackAndWhite" | "Color") {
        return reject(
            "PRESS_INK_UNSUPPORTED",
            format!("The ink intent '{}' is unsupported.", request.ink),
        );
    }
    if !matches!(
        request
            .document
            .get("printPicturePageSpreadMode")
            .and_then(Value::as_str),
        Some("WholeSpread" | "SidewaysWholeSpread" | "SplitLeaves")
    ) {
        return reject(
            "PRESS_PICTURE_PAGE_MODE_INVALID",
            "The print Picture Page mode is missing or unsupported.",
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
    validate_caption_bounds(&request.document, &request.trim)
        .map_err(|diagnostic| Box::new(RenderResponse::failed("rejected", diagnostic)))?;
    if request.assets.len() > MAX_ASSETS {
        return reject(
            "PRESS_ASSET_LIMIT",
            format!("A job may declare at most {MAX_ASSETS} assets."),
        );
    }
    if let Some(cover) = &request.cover {
        if !matches!(
            cover.barcode_mode.as_str(),
            "LorekeeperBarcode" | "VendorOverlay"
        ) || request.profile == "ingram-paperback-pdfx1a-v1"
            && cover.barcode_mode == "VendorOverlay"
        {
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
        if !(0.0..=0.25).contains(&cover.bleed_inches)
            || !(0.001..=0.01).contains(&cover.paper_caliper_inches_per_page)
            || !(0.0..=100.0).contains(&cover.image_focal_x_percent)
            || !(0.0..=100.0).contains(&cover.image_focal_y_percent)
        {
            return reject(
                "PRESS_COVER_GEOMETRY_INVALID",
                "Cover bleed, paper caliper, or focal geometry is outside supported bounds.",
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
    for asset in &request.assets {
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
        if asset.media_type != "image/png" {
            return reject(
                "PRESS_ASSET_FORMAT_UNSUPPORTED",
                format!("Asset '{}' is not a PNG.", asset.id),
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
        validate_png(asset, &bytes)?;
        total = total.saturating_add(bytes.len() as u64);
        validated_assets.insert(asset.id.clone(), bytes);
    }
    if total > MAX_JOB_BYTES {
        return reject(
            "PRESS_JOB_LIMIT",
            "The declared asset bytes exceed the job limit.",
        );
    }
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
            if values
                .get("type")
                .and_then(Value::as_str)
                .is_some_and(|kind| kind.eq_ignore_ascii_case("Language"))
            {
                assert_supported_language(
                    values.get("value").and_then(Value::as_str).unwrap_or(""),
                )?;
            }
            for child in values.values() {
                validate_inline_languages(child)?;
            }
        }
        _ => {}
    }
    Ok(())
}

fn validate_png(asset: &crate::model::AssetDeclaration, bytes: &[u8]) -> RenderResult<()> {
    let decoder = png::Decoder::new(Cursor::new(bytes));
    let reader = decoder.read_info().map_err(|error| {
        Box::new(RenderResponse::failed(
            "rejected",
            Diagnostic::error(
                "PRESS_ASSET_CORRUPT",
                format!("Asset '{}' is corrupt: {error}", asset.id),
            ),
        ))
    })?;
    let info = reader.info();
    if asset.width_pixels.is_some_and(|width| width != info.width)
        || asset
            .height_pixels
            .is_some_and(|height| height != info.height)
    {
        return reject(
            "PRESS_ASSET_DIMENSION_MISMATCH",
            format!("Asset '{}' dimensions changed.", asset.id),
        );
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
        if diagnostic.code == "PRESS_RENDER_CANCELLED" {
            "cancelled"
        } else {
            "failed"
        },
        diagnostic,
    ))
}

fn paginate(request: &RenderRequest) -> RenderResult<LayoutDocument> {
    paginate_with_cancellation(request, None)
}

fn paginate_with_cancellation(
    request: &RenderRequest,
    job_root: Option<&Path>,
) -> RenderResult<LayoutDocument> {
    let trim = &request.trim;
    if !(3.5..=12.0).contains(&trim.width_inches)
        || !(5.0..=15.0).contains(&trim.height_inches)
        || !(0.25..=2.0).contains(&trim.margin_inches)
        || !(7.0..=30.0).contains(&trim.body_font_size_points)
        || !(1.0..=2.5).contains(&trim.body_line_height)
        || trim.minimum_widow_lines == 0
        || trim.minimum_orphan_lines == 0
    {
        return reject(
            "PRESS_LAYOUT_INVALID",
            "Trim, margin, typography, widow, or orphan values are outside supported bounds.",
        );
    }
    let mut pages = Vec::new();
    let mut page_map = Vec::new();
    let mut body_start_page = None;
    let document = &request.document;
    let title = string(document, "title");
    let author = string(document, "author");
    if document
        .get("includeTitlePage")
        .and_then(Value::as_bool)
        .unwrap_or(true)
    {
        pages.push(centered_page(&title, &author, trim));
    }
    let toc_index = document
        .get("includeVisibleTableOfContents")
        .and_then(Value::as_bool)
        .unwrap_or(false)
        .then_some(pages.len());
    if toc_index.is_some() {
        pages.push(centered_page("Contents", "", trim));
    }
    let mut chapter_entries = Vec::new();
    let mut chapter_ordinal = 0usize;
    append_matter(&mut pages, document, "Front", trim);
    let mut features = BTreeSet::new();
    if document
        .get("styles")
        .and_then(Value::as_array)
        .is_some_and(|styles| !styles.is_empty())
    {
        features.insert("named-styles".to_owned());
    }
    if document
        .get("placements")
        .and_then(Value::as_array)
        .is_some_and(|values| !values.is_empty())
    {
        features.insert("publication-placement".to_owned());
    }
    if let Some(sections) = document.get("sections").and_then(Value::as_array) {
        for (section_index, section) in sections.iter().enumerate() {
            check_layout_cancellation(job_root)?;
            append_placement_pages(
                &mut pages,
                document,
                &string(section, "id"),
                &["BeforeAct"],
                trim,
            );
            let section_title = numbered_title(
                &string(section, "title"),
                section_index + 1,
                document
                    .get("numberActs")
                    .and_then(Value::as_bool)
                    .unwrap_or(false),
                "Act",
            );
            if section
                .get("includePage")
                .and_then(Value::as_bool)
                .unwrap_or(false)
            {
                start_recto(&mut pages, trim);
                pages.push(centered_page(
                    &section_title,
                    &string(section, "synopsis"),
                    trim,
                ));
            } else if section
                .get("includeHeading")
                .and_then(Value::as_bool)
                .unwrap_or_else(|| {
                    document
                        .get("includeActHeadings")
                        .and_then(Value::as_bool)
                        .unwrap_or(true)
                })
                && !section_title.is_empty()
            {
                let style = BlockStyle {
                    size: 18.0,
                    line_height: 1.3,
                    indent: 0.0,
                    keep_with_next: true,
                    alignment: "left".to_owned(),
                    face: FontFace::SansBold,
                    small_caps: false,
                    space_before: 12.0,
                    space_after: 6.0,
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
                append_placement_pages(
                    &mut pages,
                    document,
                    &string(chapter, "id"),
                    &["BeforeChapter", "ChapterOpening"],
                    trim,
                );
                start_recto(&mut pages, trim);
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
                let visual_mode = string(chapter, "visualMode");
                if visual_mode == "IllustratedProse" {
                    features.insert("illustrated-prose".to_owned());
                }
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
                    pages.push(text_page(vec![(chapter_title.clone(), 22.0)], trim));
                }
                let chapter_synopsis = string(chapter, "synopsis");
                if !chapter_synopsis.is_empty() {
                    let synopsis_style = BlockStyle {
                        size: trim.body_font_size_points,
                        line_height: trim.body_line_height,
                        indent: 18.0,
                        keep_with_next: true,
                        alignment: "left".to_owned(),
                        face: FontFace::SerifItalic,
                        small_caps: false,
                        space_before: 0.0,
                        space_after: 8.0,
                    };
                    append_styled_text(&mut pages, &chapter_synopsis, trim, &synopsis_style);
                }
                for block in blocks.drain(..) {
                    check_layout_cancellation(job_root)?;
                    let block_id = string(&block, "id");
                    let block_type = string(&block, "type");
                    let text = display_block_text(&block);
                    if block_has_marks(&block) {
                        features.insert("inline-marks".to_owned());
                    }
                    if block_type.eq_ignore_ascii_case("Figure") {
                        features.insert("semantic-figure".to_owned());
                    }
                    features.insert(format!(
                        "semantic-block-{}",
                        block_type.to_ascii_lowercase()
                    ));
                    let style = block_style(document, &block, trim);
                    append_anchored_illustrations(
                        &mut pages,
                        chapter,
                        &block_id,
                        "BeforeParagraph",
                        trim,
                    );
                    let runs = if block_type.eq_ignore_ascii_case("SceneBreak")
                        || block_type.eq_ignore_ascii_case("ListItem")
                    {
                        vec![LayoutRun {
                            text: text.clone(),
                            face: style.face,
                            underline: false,
                            strikethrough: false,
                            baseline_shift_em: 0.0,
                            size_scale: 1.0,
                        }]
                    } else {
                        block_runs(document, &block, &style)
                    };
                    let first_page = append_styled_runs(&mut pages, &text, &runs, trim, &style);
                    if block_type.eq_ignore_ascii_case("Figure") {
                        let asset_id = string(&block, "assetId");
                        if !asset_id.is_empty() {
                            pages.push(picture_page(trim, &string(&block, "caption"), asset_id));
                        }
                    }
                    append_anchored_illustrations(
                        &mut pages,
                        chapter,
                        &block_id,
                        "AfterParagraph",
                        trim,
                    );
                    if !block_id.is_empty() {
                        page_map.push(PageMapEntry {
                            chapter_id: string(chapter, "id"),
                            block_id,
                            page_number: first_page.max(chapter_start),
                        });
                    }
                }
                if chapter
                    .get("picturePage")
                    .is_some_and(|value| !value.is_null())
                {
                    features.insert("picture-page-spread".to_owned());
                    start_recto(&mut pages, trim);
                    let asset_id = chapter
                        .get("picturePage")
                        .map(|value| string(value, "assetId"))
                        .unwrap_or_default();
                    match document
                        .get("printPicturePageSpreadMode")
                        .and_then(Value::as_str)
                        .expect("validated Picture Page mode")
                    {
                        "SplitLeaves" => {
                            pages.push(picture_spread_leaf(
                                trim,
                                "Picture Page — left leaf",
                                asset_id.clone(),
                                false,
                            ));
                            pages.push(picture_spread_leaf(
                                trim,
                                "Picture Page — right leaf",
                                asset_id,
                                true,
                            ));
                        }
                        "SidewaysWholeSpread" => {
                            let mut page = picture_page(
                                trim,
                                "Picture Page — sideways whole spread",
                                asset_id,
                            );
                            if let Some(image) = page.images.first_mut() {
                                image.rotation_degrees = 90.0;
                                image.contain = true;
                            }
                            pages.push(page);
                        }
                        "WholeSpread" => {
                            let mut page =
                                picture_page(trim, "Picture Page — whole spread", asset_id);
                            if let Some(image) = page.images.first_mut() {
                                image.contain = true;
                            }
                            pages.push(page);
                        }
                        _ => unreachable!("validated Picture Page mode"),
                    }
                }
                add_running_heads(&mut pages[chapter_page_index..], &chapter_title, trim);
                append_placement_pages(
                    &mut pages,
                    document,
                    &string(chapter, "id"),
                    &["ChapterEnding", "AfterChapter"],
                    trim,
                );
            }
            append_placement_pages(
                &mut pages,
                document,
                &string(section, "id"),
                &["AfterAct"],
                trim,
            );
        }
    }
    append_matter(&mut pages, document, "Back", trim);
    let mut toc_converged = toc_index.is_none();
    if let Some(index) = toc_index {
        let (replacements, converged) =
            build_toc_pages(&chapter_entries, body_start_page.unwrap_or(1), trim)
                .map_err(|diagnostic| Box::new(RenderResponse::failed("rejected", diagnostic)))?;
        toc_converged = converged;
        let toc_page_count = replacements.len();
        let delta = toc_page_count - 1;
        pages.splice(index..=index, replacements);
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
    while pages.len() < 7 {
        pages.push(LayoutPage {
            kind: PageKind::Blank,
            lines: Vec::new(),
            images: Vec::new(),
            barcode_modules: None,
            page_label: None,
        });
    }
    assign_page_labels(&mut pages, body_start_page.unwrap_or(1));
    Ok(LayoutDocument {
        pages,
        page_map,
        features: features.into_iter().collect(),
        toc_converged,
    })
}

fn append_anchored_illustrations(
    pages: &mut Vec<LayoutPage>,
    chapter: &Value,
    block_id: &str,
    anchor_position: &str,
    trim: &crate::model::Trim,
) {
    for illustration in chapter
        .get("illustrations")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter(|illustration| {
            string(illustration, "anchorBlockId") == block_id
                && string(illustration, "anchorPosition").eq_ignore_ascii_case(anchor_position)
        })
    {
        let start_on_new_page = illustration
            .get("startOnNewPage")
            .and_then(Value::as_bool)
            .unwrap_or(false);
        let caption = string(illustration, "caption");
        let asset_id = string(illustration, "assetId");
        let width_percent = illustration
            .get("widthPercent")
            .and_then(Value::as_f64)
            .unwrap_or(100.0) as f32;
        let focal_x_percent = illustration
            .get("focalXPercent")
            .and_then(Value::as_f64)
            .unwrap_or(50.0) as f32;
        let focal_y_percent = illustration
            .get("focalYPercent")
            .and_then(Value::as_f64)
            .unwrap_or(50.0) as f32;
        let alignment = string(illustration, "alignment");
        if start_on_new_page {
            pages.push(picture_page_with_layout(
                trim,
                &caption,
                asset_id,
                width_percent,
                focal_x_percent,
                focal_y_percent,
                &alignment,
            ));
        } else {
            append_inline_illustration(
                pages,
                trim,
                &caption,
                asset_id,
                width_percent,
                focal_x_percent,
                focal_y_percent,
                &alignment,
            );
        }
    }
}

#[allow(clippy::too_many_arguments)]
fn append_inline_illustration(
    pages: &mut Vec<LayoutPage>,
    trim: &crate::model::Trim,
    caption: &str,
    asset_id: String,
    width_percent: f32,
    focal_x_percent: f32,
    focal_y_percent: f32,
    alignment: &str,
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
    let caption_lines = wrapped_caption(caption, image_width);
    let caption_step = 9.0 * 1.6;
    let caption_extent = if caption_lines.is_empty() {
        0.0
    } else {
        6.0 + 9.0 * 1.12 + (caption_lines.len() - 1) as f32 * caption_step
    };
    let required_height = image_height + caption_extent;
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
    let image_top = page
        .lines
        .last()
        .map_or(page_height - margin, |line| line.y - line.size * 1.6);
    let image_y = image_top - image_height;
    page.images.push(LayoutImage {
        asset_id,
        x: image_x,
        y: image_y,
        width: image_width,
        height: image_height,
        focal_x: (focal_x_percent / 100.0).clamp(0.0, 1.0),
        focal_y: (focal_y_percent / 100.0).clamp(0.0, 1.0),
        source_left_fraction: 0.0,
        source_width_fraction: 1.0,
        rotation_degrees: 0.0,
        contain: false,
    });

    let mut spacer_y = image_top;
    while spacer_y > image_y {
        page.lines.push(LayoutLine {
            text: String::new(),
            runs: Vec::new(),
            size: trim.body_font_size_points,
            x: margin,
            y: spacer_y,
            word_spacing: 0.0,
            rotation_degrees: 0.0,
            light_text: false,
        });
        spacer_y -= line_step;
    }
    let first_caption_y = image_y - 6.0 - 9.0 * 0.82;
    for (index, (text, runs)) in caption_lines.into_iter().enumerate() {
        page.lines.push(LayoutLine {
            text,
            runs,
            size: 9.0,
            x: image_x,
            y: first_caption_y - index as f32 * caption_step,
            word_spacing: 0.0,
            rotation_degrees: 0.0,
            light_text: false,
        });
    }
}

fn append_matter(
    pages: &mut Vec<LayoutPage>,
    document: &Value,
    location: &str,
    trim: &crate::model::Trim,
) {
    for item in document
        .get("matter")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter(|item| string(item, "location").eq_ignore_ascii_case(location))
    {
        pages.push(text_page(vec![(string(item, "title"), 18.0)], trim));
        for block in item
            .get("blocks")
            .and_then(Value::as_array)
            .into_iter()
            .flatten()
        {
            let text = display_block_text(block);
            let style = block_style(document, block, trim);
            let runs = if string(block, "type").eq_ignore_ascii_case("SceneBreak")
                || string(block, "type").eq_ignore_ascii_case("ListItem")
            {
                single_run(&text, style.face)
            } else {
                block_runs(document, block, &style)
            };
            append_styled_runs(pages, &text, &runs, trim, &style);
        }
    }
}

fn centered_page(title: &str, subtitle: &str, trim: &crate::model::Trim) -> LayoutPage {
    let width = trim.width_inches * 72.0;
    let height = trim.height_inches * 72.0;
    let mut lines = Vec::new();
    if !title.is_empty() {
        lines.push(LayoutLine {
            text: title.to_owned(),
            runs: single_run(title, FontFace::SansBold),
            size: 22.0,
            x: width * 0.16,
            y: height * 0.62,
            word_spacing: 0.0,
            rotation_degrees: 0.0,
            light_text: false,
        });
    }
    if !subtitle.is_empty() {
        lines.push(LayoutLine {
            text: subtitle.to_owned(),
            runs: single_run(subtitle, FontFace::SerifRegular),
            size: 11.0,
            x: width * 0.16,
            y: height * 0.54,
            word_spacing: 0.0,
            rotation_degrees: 0.0,
            light_text: false,
        });
    }
    LayoutPage {
        kind: PageKind::Body,
        lines,
        images: Vec::new(),
        barcode_modules: None,
        page_label: None,
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
        page.lines.push(LayoutLine {
            text: title.to_owned(),
            runs: single_run(title, FontFace::SansRegular),
            size: 8.0,
            x,
            y,
            word_spacing: 0.0,
            rotation_degrees: 0.0,
            light_text: false,
        });
    }
}

fn append_placement_pages(
    pages: &mut Vec<LayoutPage>,
    document: &Value,
    target_id: &str,
    kinds: &[&str],
    trim: &crate::model::Trim,
) {
    for placement in document
        .get("placements")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter(|placement| {
            string(placement, "targetId") == target_id
                && kinds.contains(&string(placement, "placementKind").as_str())
        })
    {
        pages.push(picture_page(
            trim,
            &string(placement, "caption"),
            string(placement, "assetId"),
        ));
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
        keep_with_next: true,
        alignment: "left".to_owned(),
        face: FontFace::SerifRegular,
        small_caps: false,
        space_before: 0.0,
        space_after: 0.0,
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
                page.lines.push(LayoutLine {
                    text: line,
                    runs,
                    size: style.size,
                    x: trim.margin_inches * 72.0,
                    y,
                    word_spacing: 0.0,
                    rotation_degrees: 0.0,
                    light_text: false,
                });
            }
        }
        if pages.len().is_multiple_of(2) {
            pages.push(LayoutPage {
                kind: PageKind::Blank,
                lines: Vec::new(),
                images: Vec::new(),
                barcode_modules: None,
                page_label: None,
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
    LayoutPage {
        kind: PageKind::Body,
        lines: vec![LayoutLine {
            text: text.to_owned(),
            runs: single_run(text, FontFace::SansBold),
            size,
            x: trim.margin_inches * 72.0,
            y: trim.height_inches * 72.0 - trim.margin_inches * 72.0 - size * 0.82,
            word_spacing: 0.0,
            rotation_degrees: 0.0,
            light_text: false,
        }],
        images: Vec::new(),
        barcode_modules: None,
        page_label: None,
    }
}

fn wrap_layout_runs(
    text: &str,
    source_runs: &[LayoutRun],
    size: f32,
    available_width: f32,
) -> Vec<(String, Vec<LayoutRun>)> {
    let mut max_chars = (available_width / (size * 0.52)).floor().max(4.0) as usize;
    loop {
        let candidate_lines = wrap(text, max_chars);
        let mut search_offset = 0;
        let candidate_runs = candidate_lines
            .iter()
            .map(|line| runs_for_line(text, source_runs, line, &mut search_offset))
            .collect::<Vec<_>>();
        if max_chars <= 4
            || candidate_runs
                .iter()
                .all(|runs| measured_run_width(runs, size) <= available_width + 0.01)
        {
            return candidate_lines.into_iter().zip(candidate_runs).collect();
        }
        max_chars -= 1;
    }
}

fn wrapped_caption(caption: &str, width: f32) -> Vec<(String, Vec<LayoutRun>)> {
    wrap_layout_runs(
        caption,
        &single_run(caption, FontFace::SerifItalic),
        9.0,
        width,
    )
}

fn validate_caption_bounds(value: &Value, trim: &crate::model::Trim) -> Result<(), Diagnostic> {
    match value {
        Value::Array(values) => {
            for child in values {
                validate_caption_bounds(child, trim)?;
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
                let lines = wrapped_caption(caption, image_width).len();
                let inline = values.contains_key("anchorPosition")
                    && !values
                        .get("startOnNewPage")
                        .and_then(Value::as_bool)
                        .unwrap_or(false);
                let caption_extent = (if inline { 6.0 } else { 8.0 })
                    + 9.0 * 1.12
                    + lines.saturating_sub(1) as f32 * 9.0 * 1.6;
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
                validate_caption_bounds(child, trim)?;
            }
        }
        _ => {}
    }
    Ok(())
}

fn text_page(lines: Vec<(String, f32)>, trim: &crate::model::Trim) -> LayoutPage {
    let height = trim.height_inches * 72.0;
    let x = trim.margin_inches * 72.0;
    let mut y = height - x;
    let lines = lines
        .into_iter()
        .map(|(text, size)| {
            let line = LayoutLine {
                runs: single_run(
                    &text,
                    if size >= 16.0 {
                        FontFace::SansBold
                    } else {
                        FontFace::SerifRegular
                    },
                ),
                text,
                size,
                x,
                y,
                word_spacing: 0.0,
                rotation_degrees: 0.0,
                light_text: false,
            };
            y -= size * 1.6;
            line
        })
        .collect();
    LayoutPage {
        kind: PageKind::Body,
        lines,
        images: Vec::new(),
        barcode_modules: None,
        page_label: None,
    }
}

#[derive(Debug, Clone)]
struct BlockStyle {
    size: f32,
    line_height: f32,
    indent: f32,
    keep_with_next: bool,
    alignment: String,
    face: FontFace,
    small_caps: bool,
    space_before: f32,
    space_after: f32,
}

impl BlockStyle {
    fn body(trim: &crate::model::Trim) -> Self {
        Self {
            size: trim.body_font_size_points,
            line_height: trim.body_line_height,
            indent: 0.0,
            keep_with_next: false,
            alignment: "justify".to_owned(),
            face: FontFace::SerifRegular,
            small_caps: false,
            space_before: 0.0,
            space_after: 0.0,
        }
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
    }];
    append_styled_runs(pages, text, &runs, trim, style)
}

fn append_styled_runs(
    pages: &mut Vec<LayoutPage>,
    text: &str,
    source_runs: &[LayoutRun],
    trim: &crate::model::Trim,
    style: &BlockStyle,
) -> usize {
    if text.is_empty() {
        return pages.len().max(1);
    }
    let available_width = (trim.width_inches - 2.0 * trim.margin_inches) * 72.0 - style.indent;
    let wrapped = wrap_layout_runs(text, source_runs, style.size, available_width);
    let (lines, wrapped_runs): (Vec<_>, Vec<_>) = wrapped.into_iter().unzip();
    let mut offset = 0;
    let mut first_page = None;
    if style.keep_with_next
        && pages.last().is_some_and(|page| {
            page.kind == PageKind::Body && remaining_line_capacity(page, trim, style) < 3
        })
    {
        pages.push(empty_body_page());
    }
    while offset < lines.len() {
        let remaining_capacity = pages.last().map_or(0, |page| {
            if page.kind == PageKind::Body {
                remaining_line_capacity(page, trim, style)
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
        let page_capacity = remaining_line_capacity(pages.last().expect("body page"), trim, style);
        let mut take = remaining_lines.min(page_capacity);
        let following = remaining_lines - take;
        if following > 0 && following < trim.minimum_widow_lines {
            let move_to_next = trim.minimum_widow_lines - following;
            if take.saturating_sub(move_to_next) >= trim.minimum_orphan_lines {
                take -= move_to_next;
            } else {
                pages.push(empty_body_page());
                take = remaining_lines.min(remaining_line_capacity(
                    pages.last().expect("body page"),
                    trim,
                    style,
                ));
            }
        }
        let page_number = pages.len();
        let page = pages.last_mut().expect("body page");
        first_page.get_or_insert(page_number);
        for (relative_index, line) in lines[offset..offset + take].iter().enumerate() {
            let available_width =
                (trim.width_inches - 2.0 * trim.margin_inches) * 72.0 - style.indent;
            let line_runs = wrapped_runs[offset + relative_index].clone();
            let estimated_width = measured_run_width(&line_runs, style.size);
            let spaces = line.chars().filter(|character| *character == ' ').count();
            let is_final_line = offset + relative_index + 1 == lines.len();
            let word_spacing = if style.alignment == "justify"
                && !is_final_line
                && spaces > 0
                && estimated_width < available_width
            {
                ((available_width - estimated_width) / spaces as f32).clamp(0.0, style.size * 0.25)
            } else {
                0.0
            };
            let alignment_offset = match style.alignment.as_str() {
                "center" => ((available_width - estimated_width) / 2.0).max(0.0),
                "right" => (available_width - estimated_width).max(0.0),
                _ => 0.0,
            };
            let y = next_baseline(page, trim, style, offset == 0 && relative_index == 0);
            page.lines.push(LayoutLine {
                text: line.clone(),
                runs: line_runs,
                size: style.size,
                x: trim.margin_inches * 72.0 + style.indent + alignment_offset,
                y,
                word_spacing,
                rotation_degrees: 0.0,
                light_text: false,
            });
        }
        offset += take;
    }
    if style.space_after > 0.0
        && let Some(page) = pages.last_mut()
    {
        let y = page.lines.last().map_or(
            trim.height_inches * 72.0 - trim.margin_inches * 72.0,
            |previous| previous.y - previous.size * 1.6,
        );
        page.lines.push(LayoutLine {
            text: String::new(),
            runs: Vec::new(),
            size: style.space_after / 1.6,
            x: trim.margin_inches * 72.0,
            y,
            word_spacing: 0.0,
            rotation_degrees: 0.0,
            light_text: false,
        });
    }
    first_page.unwrap_or_else(|| pages.len().max(1))
}

fn next_baseline(
    page: &LayoutPage,
    trim: &crate::model::Trim,
    style: &BlockStyle,
    first_block_line: bool,
) -> f32 {
    page.lines.last().map_or(
        trim.height_inches * 72.0
            - trim.margin_inches * 72.0
            - style.size * 0.82
            - if first_block_line {
                style.space_before
            } else {
                0.0
            },
        |previous| {
            previous.y
                - (previous.size * 1.6).max(style.size * style.line_height)
                - if first_block_line {
                    style.space_before
                } else {
                    0.0
                }
        },
    )
}

fn remaining_line_capacity(
    page: &LayoutPage,
    trim: &crate::model::Trim,
    style: &BlockStyle,
) -> usize {
    let bottom = trim.margin_inches * 72.0;
    let step = (style.size * 1.6).max(style.size * style.line_height.max(1.0));
    let first = next_baseline(page, trim, style, true);
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
        });
    }
    output
}

fn empty_body_page() -> LayoutPage {
    LayoutPage {
        kind: PageKind::Body,
        lines: Vec::new(),
        images: Vec::new(),
        barcode_modules: None,
        page_label: None,
    }
}

fn block_style(document: &Value, block: &Value, trim: &crate::model::Trim) -> BlockStyle {
    let block_type = string(block, "type");
    let mut style = BlockStyle::body(trim);
    match block_type.to_ascii_lowercase().as_str() {
        "heading" => {
            let level = block
                .get("headingLevel")
                .and_then(Value::as_u64)
                .unwrap_or(2) as f32;
            style.size = (24.0 - level * 2.0).max(trim.body_font_size_points + 2.0);
            style.keep_with_next = true;
            style.alignment = "left".to_owned();
            style.face = FontFace::SansBold;
        }
        "blockquote" => {
            style.indent = 24.0;
            style.alignment = "left".to_owned();
        }
        "scenebreak" => {
            style.alignment = "center".to_owned();
            style.keep_with_next = true;
        }
        "listitem" => style.alignment = "left".to_owned(),
        _ => {}
    }
    let semantic_role = string(block, "styleRole");
    if let Some(definition) = document
        .get("styles")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .find(|candidate| string(candidate, "semanticRole") == semantic_role)
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
            style.alignment = alignment.to_ascii_lowercase();
        }
        let family = definition
            .get("fontFamilyKey")
            .or_else(|| definition.get("fontFamily"))
            .and_then(Value::as_str)
            .map(font_family)
            .unwrap_or_else(|| style.face.family());
        let bold = definition
            .get("fontWeight")
            .and_then(Value::as_u64)
            .is_some_and(|weight| weight >= 600);
        let italic = definition
            .get("italic")
            .and_then(Value::as_bool)
            .unwrap_or(false);
        style.face = regular_face(family).with_emphasis(bold, italic);
        style.small_caps = definition
            .get("smallCaps")
            .and_then(Value::as_bool)
            .unwrap_or(false);
        style.space_before = definition
            .get("spaceBeforePoints")
            .and_then(Value::as_f64)
            .unwrap_or(0.0) as f32;
        style.space_after = definition
            .get("spaceAfterPoints")
            .and_then(Value::as_f64)
            .unwrap_or(0.0) as f32;
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
            let mut text = span.get("text").and_then(Value::as_str)?.to_owned();
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
            let mut bold = has("Strong")
                || matches!(
                    style.face,
                    FontFace::SerifBold
                        | FontFace::SerifBoldItalic
                        | FontFace::SansBold
                        | FontFace::SansBoldItalic
                        | FontFace::MonoBold
                        | FontFace::MonoBoldItalic
                );
            let mut italic = has("Emphasis")
                || matches!(
                    style.face,
                    FontFace::SerifItalic
                        | FontFace::SerifBoldItalic
                        | FontFace::SansItalic
                        | FontFace::SansBoldItalic
                        | FontFace::MonoItalic
                        | FontFace::MonoBoldItalic
                );
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
                    bold = value >= 600;
                }
                if let Some(value) = definition.get("italic").and_then(Value::as_bool) {
                    italic = value;
                }
            }
            let character_small_caps = character_definition
                .and_then(|definition| definition.get("smallCaps"))
                .and_then(Value::as_bool)
                .unwrap_or(false);
            if style.small_caps || has("SmallCaps") || character_small_caps {
                text = text.to_uppercase();
            }
            let superscript = has("Superscript");
            let subscript = has("Subscript");
            Some(LayoutRun {
                text,
                face: regular_face(family).with_emphasis(bold, italic),
                underline: has("Underline") || has("Link"),
                strikethrough: has("Strikethrough"),
                baseline_shift_em: if superscript {
                    0.35
                } else if subscript {
                    -0.2
                } else {
                    0.0
                },
                size_scale: character_definition
                    .and_then(|definition| definition.get("fontSizePoints"))
                    .and_then(Value::as_f64)
                    .map_or(1.0, |size| size as f32 / style.size)
                    * if superscript || subscript { 0.7 } else { 1.0 },
            })
        })
        .collect()
}

fn font_family(value: &str) -> FontFamily {
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
        })
        .into_iter()
        .collect()
}

fn display_block_text(block: &Value) -> String {
    let text = block_text(block);
    match string(block, "type").to_ascii_lowercase().as_str() {
        "scenebreak" => "* * *".to_owned(),
        "listitem" => format!("• {text}"),
        _ => text,
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

fn picture_page(trim: &crate::model::Trim, label: &str, asset_id: String) -> LayoutPage {
    picture_page_with_focal(trim, label, asset_id, 100.0, 50.0, 50.0)
}

fn picture_page_with_focal(
    trim: &crate::model::Trim,
    label: &str,
    asset_id: String,
    width_percent: f32,
    focal_x_percent: f32,
    focal_y_percent: f32,
) -> LayoutPage {
    picture_page_with_layout(
        trim,
        label,
        asset_id,
        width_percent,
        focal_x_percent,
        focal_y_percent,
        "Center",
    )
}

fn picture_page_with_layout(
    trim: &crate::model::Trim,
    label: &str,
    asset_id: String,
    width_percent: f32,
    focal_x_percent: f32,
    focal_y_percent: f32,
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
    let caption_lines = wrapped_caption(label, image_width);
    let caption_step = 9.0 * 1.6;
    let first_caption_y =
        margin + 9.0 * 0.30 + caption_lines.len().saturating_sub(1) as f32 * caption_step;
    let image_y = if caption_lines.is_empty() {
        margin * 1.5
    } else {
        first_caption_y + 9.0 * 0.82 + 8.0
    };
    LayoutPage {
        kind: PageKind::Picture,
        lines: caption_lines
            .into_iter()
            .enumerate()
            .map(|(index, (text, runs))| LayoutLine {
                text,
                runs,
                size: 9.0,
                x: image_x,
                y: first_caption_y - index as f32 * caption_step,
                word_spacing: 0.0,
                rotation_degrees: 0.0,
                light_text: false,
            })
            .collect(),
        images: (!asset_id.is_empty())
            .then_some(LayoutImage {
                asset_id,
                x: image_x,
                y: image_y,
                width: image_width,
                height: trim.height_inches * 72.0 - margin - image_y,
                focal_x: (focal_x_percent / 100.0).clamp(0.0, 1.0),
                focal_y: (focal_y_percent / 100.0).clamp(0.0, 1.0),
                source_left_fraction: 0.0,
                source_width_fraction: 1.0,
                rotation_degrees: 0.0,
                contain: false,
            })
            .into_iter()
            .collect(),
        barcode_modules: None,
        page_label: None,
    }
}

fn picture_spread_leaf(
    trim: &crate::model::Trim,
    label: &str,
    asset_id: String,
    right_leaf: bool,
) -> LayoutPage {
    let mut page = picture_page_with_layout(trim, label, asset_id, 100.0, 50.0, 50.0, "Center");
    if let Some(image) = page.images.first_mut() {
        image.source_left_fraction = if right_leaf { 0.5 } else { 0.0 };
        image.source_width_fraction = 0.5;
    }
    page
}

fn cover_layout(request: &RenderRequest, width: f32) -> Result<LayoutPage, Diagnostic> {
    let height = (request.trim.height_inches
        + request
            .cover
            .as_ref()
            .map_or(0.0, |cover| cover.bleed_inches * 2.0))
        * 72.0;
    let cover = request.cover.as_ref().expect("cover");
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
        lines.push(LayoutLine {
            text: cover.spine_text.clone(),
            runs: single_run(&cover.spine_text, FontFace::SansBold),
            size: spine_size,
            x: spine_center,
            y: bleed + 36.0,
            word_spacing: 0.0,
            rotation_degrees: 90.0,
            light_text: true,
        });
    }
    if cover.barcode_mode == "LorekeeperBarcode"
        && let Some(isbn) = cover.isbn.as_deref()
    {
        lines.push(LayoutLine {
            text: isbn.to_owned(),
            runs: single_run(isbn, FontFace::MonoRegular),
            size: 8.0,
            x: width * 0.08,
            y: height * 0.10 - 12.0,
            word_spacing: 0.0,
            rotation_degrees: 0.0,
            light_text: false,
        });
    }
    Ok(LayoutPage {
        kind: PageKind::Cover,
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
                focal_x: cover.image_focal_x_percent / 100.0,
                focal_y: cover.image_focal_y_percent / 100.0,
                source_left_fraction: 0.0,
                source_width_fraction: 1.0,
                rotation_degrees: 0.0,
                contain: false,
            })
            .into_iter()
            .collect(),
        barcode_modules: if cover.barcode_mode == "LorekeeperBarcode" {
            cover.isbn.as_deref().and_then(ean13_modules)
        } else {
            None
        },
        page_label: None,
    })
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
        .map(|(index, text)| LayoutLine {
            runs: single_run(&text, face),
            text,
            size,
            x,
            y: start_y - index as f32 * line_height,
            word_spacing: 0.0,
            rotation_degrees: 0.0,
            light_text: true,
        })
        .collect())
}

fn start_recto(pages: &mut Vec<LayoutPage>, trim: &crate::model::Trim) {
    if trim.recto_chapter_starts && (pages.len() + 1).is_multiple_of(2) {
        pages.push(LayoutPage {
            kind: PageKind::Blank,
            lines: Vec::new(),
            images: Vec::new(),
            barcode_modules: None,
            page_label: None,
        });
    }
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
        media_type: "application/pdf".to_owned(),
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
        }];
        let mut pages = Vec::new();
        append_styled_runs(&mut pages, &text, &runs, &trim, &style);
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
    fn pagination_enforces_widow_and_orphan_minimums() {
        let trim = crate::model::Trim {
            width_inches: 3.5,
            height_inches: 5.0,
            margin_inches: 1.0,
            body_font_size_points: 30.0,
            body_line_height: 2.5,
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
            protocol_version: 3,
            job_id: "1".repeat(32),
            profile: "kdp-paperback-v1".to_owned(),
            ink: "BlackAndWhite".to_owned(),
            document: serde_json::json!({
                "title": "Long contents",
                "author": "Author",
                "language": "en",
                "includeVisibleTableOfContents": true,
                "sections": [{ "chapters": chapters }]
            }),
            trim: crate::model::Trim {
                width_inches: 6.0,
                height_inches: 9.0,
                margin_inches: 0.75,
                body_font_size_points: 11.0,
                body_line_height: 1.4,
                mirror_margins: true,
                recto_chapter_starts: true,
                minimum_widow_lines: 2,
                minimum_orphan_lines: 2,
            },
            cover: None,
            assets: Vec::new(),
        };
        let layout = paginate(&request).expect("layout");
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
        assert!(!toc_pages.is_multiple_of(2));
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

        assert_eq!(error.code, "PRESS_TOC_NONCONVERGENT");
    }

    #[test]
    fn named_styles_control_block_layout_without_executing_document_code() {
        let trim = crate::model::Trim {
            width_inches: 6.0,
            height_inches: 9.0,
            margin_inches: 0.75,
            body_font_size_points: 11.0,
            body_line_height: 1.4,
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
    fn mixed_heading_and_body_sizes_advance_the_vertical_cursor_without_overlap() {
        let trim = crate::model::Trim {
            width_inches: 6.0,
            height_inches: 9.0,
            margin_inches: 0.75,
            body_font_size_points: 11.0,
            body_line_height: 1.4,
            mirror_margins: true,
            recto_chapter_starts: true,
            minimum_widow_lines: 2,
            minimum_orphan_lines: 2,
        };
        let mut pages = vec![text_page(vec![("Chapter heading".to_owned(), 22.0)], &trim)];
        let heading = BlockStyle {
            size: 20.0,
            line_height: 1.4,
            indent: 0.0,
            keep_with_next: true,
            alignment: "left".to_owned(),
            face: FontFace::SansBold,
            small_caps: false,
            space_before: 0.0,
            space_after: 0.0,
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
        assert!(
            lines
                .windows(2)
                .all(|pair| { pair[0].y - pair[1].y >= pair[0].size * 1.5 })
        );
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

        assert_eq!(runs.len(), 5);
        assert_eq!(runs[0].face, crate::model::FontFace::SerifRegular);
        assert_eq!(runs[1].face, crate::model::FontFace::SerifBold);
        assert_eq!(runs[2].face, crate::model::FontFace::SerifItalic);
        assert_eq!(runs[3].face, crate::model::FontFace::MonoRegular);
        assert_eq!(runs[4].text, " CAPS");
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
        assert!((runs[0].size_scale - 0.8).abs() < 0.001);
    }

    #[test]
    fn front_and_back_matter_surround_the_body_in_reading_order() {
        let request = request_with_document(serde_json::json!({
            "title": "Ordered matter",
            "author": "Author",
            "language": "en",
            "includeTitlePage": false,
            "printPicturePageSpreadMode": "SplitLeaves",
            "matter": [
                { "location": "Back", "title": "Acknowledgments", "blocks": [] },
                { "location": "Front", "title": "Dedication", "blocks": [] }
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
    fn act_heading_and_numbering_options_apply_without_an_act_divider_page() {
        let request = request_with_document(serde_json::json!({
            "title": "Acts",
            "author": "Author",
            "language": "en",
            "includeTitlePage": false,
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
    }

    #[test]
    fn anchored_illustrations_honor_side_alignment_and_new_page_requests() {
        let request = request_with_document(serde_json::json!({
            "title": "Illustrated",
            "author": "Author",
            "language": "en",
            "includeTitlePage": false,
            "sections": [{
                "chapters": [{
                    "id": "chapter",
                    "title": "Chapter",
                    "visualMode": "IllustratedProse",
                    "illustrations": [{
                        "assetId": "asset",
                        "anchorBlockId": "block",
                        "anchorPosition": "BeforeParagraph",
                        "widthPercent": 40,
                        "alignment": "Right",
                        "startOnNewPage": true
                    }],
                    "blocks": [{ "id": "block", "type": "Paragraph", "content": [{ "text": "Anchored paragraph" }] }]
                }]
            }]
        }));

        let layout = paginate(&request).expect("layout");
        let paragraph_page = page_containing(&layout, "Anchored paragraph");
        let image_page = layout
            .pages
            .iter()
            .position(|page| page.images.iter().any(|image| image.asset_id == "asset"))
            .expect("image page");
        let image = &layout.pages[image_page].images[0];
        let right_edge = request.trim.width_inches * 72.0 - request.trim.margin_inches * 72.0;

        assert!(image_page < paragraph_page);
        assert!((image.x + image.width - right_edge).abs() < 0.01);
        assert_eq!(layout.pages[image_page].lines.len(), 0);
    }

    #[test]
    fn anchored_illustrations_without_a_page_break_flow_with_the_paragraph() {
        let request = request_with_document(serde_json::json!({
            "title": "Illustrated",
            "author": "Author",
            "language": "en",
            "includeTitlePage": false,
            "sections": [{
                "chapters": [{
                    "id": "chapter",
                    "title": "Chapter",
                    "visualMode": "IllustratedProse",
                    "illustrations": [{
                        "assetId": "asset",
                        "anchorBlockId": "block",
                        "anchorPosition": "BeforeParagraph",
                        "caption": "A deliberately long in-flow figure caption that must wrap within a narrow right-aligned image without touching later prose.",
                        "widthPercent": 35,
                        "alignment": "Right",
                        "startOnNewPage": false
                    }],
                    "blocks": [{ "id": "block", "type": "Paragraph", "content": [{ "text": "Anchored paragraph" }] }]
                }]
            }]
        }));

        let layout = paginate(&request).expect("layout");
        let paragraph_page = page_containing(&layout, "Anchored paragraph");
        let image_page = layout
            .pages
            .iter()
            .position(|page| page.images.iter().any(|image| image.asset_id == "asset"))
            .expect("image page");
        let page = &layout.pages[image_page];
        let image = &page.images[0];
        let paragraph = page
            .lines
            .iter()
            .find(|line| line.text == "Anchored paragraph")
            .expect("paragraph line");

        assert_eq!(image_page, paragraph_page);
        assert_eq!(page.kind, PageKind::Body);
        let captions = page
            .lines
            .iter()
            .filter(|line| {
                line.runs
                    .first()
                    .is_some_and(|run| run.face == FontFace::SerifItalic)
            })
            .collect::<Vec<_>>();
        assert!(captions.len() >= 3);
        assert!(captions.iter().all(|line| {
            (line.x - image.x).abs() < 0.01
                && measured_run_width(&line.runs, line.size) <= image.width + 0.01
                && line.y < image.y
        }));
        assert!(paragraph.y < image.y);
        assert!(paragraph.y < captions.last().unwrap().y);
    }

    #[test]
    fn picture_page_spreads_crop_distinct_left_and_right_leaves() {
        let request = request_with_document(serde_json::json!({
            "title": "Spread",
            "author": "Author",
            "language": "en",
            "includeTitlePage": false,
            "printPicturePageSpreadMode": "SplitLeaves",
            "sections": [{
                "chapters": [{
                    "id": "chapter",
                    "title": "Spread",
                    "visualMode": "PicturePage",
                    "pageLayoutKind": "DoublePortrait",
                    "picturePage": { "assetId": "surface", "leafCount": 2 },
                    "blocks": [{ "id": "block", "type": "Paragraph", "content": [{ "text": "Body" }] }]
                }]
            }]
        }));

        let layout = paginate(&request).expect("layout");
        let spread_images = layout
            .pages
            .iter()
            .flat_map(|page| page.images.iter())
            .filter(|image| image.asset_id == "surface")
            .collect::<Vec<_>>();

        assert_eq!(spread_images.len(), 2);
        assert_eq!(spread_images[0].source_left_fraction, 0.0);
        assert_eq!(spread_images[0].source_width_fraction, 0.5);
        assert_eq!(spread_images[1].source_left_fraction, 0.5);
        assert_eq!(spread_images[1].source_width_fraction, 0.5);
    }

    #[test]
    fn picture_page_caption_sits_below_and_aligns_with_the_image() {
        let request = request_with_document(serde_json::json!({
            "title": "Caption",
            "author": "Author",
            "language": "en",
            "sections": []
        }));
        let page = picture_page_with_layout(
            &request.trim,
            "A deliberately long Picture Page caption that wraps safely beneath narrow right-aligned artwork.",
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
            (caption.x - image.x).abs() < 0.01
                && measured_run_width(&caption.runs, caption.size) <= image.width + 0.01
                && caption.y < image.y
                && caption.y - caption.size * 0.30 >= request.trim.margin_inches * 72.0 - 0.01
        }));
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
            paper_caliper_inches_per_page: 0.0025,
            back_copy: "Back copy".to_owned(),
            title: "Front title".to_owned(),
            subtitle: "Subtitle".to_owned(),
            author: "Author".to_owned(),
            spine_text: "Spine title".to_owned(),
            background_color: "#16324f".to_owned(),
            isbn: Some("9780306406157".to_owned()),
            barcode_mode: "LorekeeperBarcode".to_owned(),
            asset_id: None,
            image_focal_x_percent: 50.0,
            image_focal_y_percent: 50.0,
        });
        let spine_width = 1.0 * 72.0;
        let cover_width = request.trim.width_inches * 144.0
            + spine_width
            + request.cover.as_ref().unwrap().bleed_inches * 144.0;
        let page = cover_layout(&request, cover_width).expect("cover layout");
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
            mirror_margins: true,
            recto_chapter_starts: false,
            minimum_widow_lines: 2,
            minimum_orphan_lines: 2,
        }
    }

    fn request_with_document(document: Value) -> RenderRequest {
        RenderRequest {
            protocol_version: 3,
            job_id: "1".repeat(32),
            profile: "kdp-paperback-v1".to_owned(),
            ink: "BlackAndWhite".to_owned(),
            document,
            trim: standard_trim(),
            cover: None,
            assets: Vec::new(),
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
