use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Command, Output, Stdio};

use lopdf::{Document, Object};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use tempfile::TempDir;

const PIXEL_PNG: &[u8] = &[
    137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0,
    0, 0, 144, 119, 83, 222, 0, 0, 0, 12, 73, 68, 65, 84, 8, 215, 99, 248, 207, 192, 0, 0, 3, 1, 1,
    0, 24, 221, 141, 176, 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130,
];

#[test]
fn describe_exposes_the_owned_versioned_capability_contract() {
    let output = run(&["describe", "--json"]);
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    let value: Value = serde_json::from_slice(&output.stdout).expect("describe JSON");

    assert_eq!(value["protocolVersion"], 3);
    assert_eq!(value["rendererVersion"], "1.0.1");
    assert_eq!(
        value["profiles"],
        json!([
            "generic-paperback-v1",
            "ingram-paperback-pdfx1a-v1",
            "kdp-paperback-v1"
        ])
    );
    assert_eq!(value["machineRuntimeDependencies"], json!([]));
    assert_eq!(value["capabilities"]["picturePages"], true);
    assert_eq!(value["capabilities"]["illustratedProse"], true);
    assert_eq!(value["capabilities"]["publicationPlacements"], true);
    assert_eq!(value["capabilities"]["dedicatedFullWrapCover"], true);
}

#[test]
fn kdp_fixture_renders_pdf_17_with_complete_semantic_evidence() {
    let job = PreparedJob::new("kdp-paperback-v1");
    let output = job.render();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    let response = response(&output);
    assert_eq!(response["protocolVersion"], 3);
    assert_eq!(response["rendererVersion"], "1.0.1");
    assert_eq!(response["status"], "completed");
    assert_eq!(response["evidence"]["validationStatus"], "validated");
    assert_eq!(response["evidence"]["pdfVersion"], "1.7");
    assert_eq!(response["evidence"]["tocConverged"], true);
    assert_eq!(response["evidence"]["hasEncryption"], false);
    assert_eq!(response["evidence"]["hasTransparency"], false);
    assert_eq!(response["evidence"]["annotationCount"], 0);
    assert_eq!(response["evidence"]["fontsEmbedded"], true);
    assert_eq!(response["evidence"]["toUnicodeMapsPresent"], true);
    assert!(
        response["evidence"]["minimumEffectiveDpi"]
            .as_f64()
            .is_some_and(|dpi| dpi > 0.0)
    );
    assert!(has_diagnostic(&response, "PRESS_IMAGE_DPI_LOW"));

    let page_map = response["pageMap"].as_array().expect("page map");
    for block in 1..=8 {
        let block_id = format!("60000000-0000-0000-0000-{block:012}");
        assert!(page_map.iter().any(|entry| entry["blockId"] == block_id));
    }
    let features = response["evidence"]["renderedFeatures"]
        .as_array()
        .expect("rendered features");
    for feature in [
        "semantic-figure",
        "illustrated-prose",
        "picture-page-spread",
        "publication-placement",
        "dedicated-cover",
        "inline-marks",
    ] {
        assert!(features.iter().any(|value| value == feature), "{feature}");
    }

    let interior = job.artifact(&response, "interior-pdf");
    let inspection = inspect(&interior);
    assert_eq!(inspection.version, "1.7");
    assert!(inspection.page_count >= 7);
    assert!(inspection.all_fonts_embedded);
    assert!(inspection.all_fonts_have_to_unicode);
    assert!(
        inspection.font_count >= 3,
        "mixed serif, emphasis, and heading typography must use distinct embedded subsets"
    );
    assert!(!inspection.encrypted);
    assert_eq!(inspection.annotations, 0);
    assert!(inspection.image_xobjects > 0);

    let cover = job.artifact(&response, "cover-pdf");
    let cover_inspection = inspect(&cover);
    assert_eq!(cover_inspection.page_count, 1);
    assert!(cover_inspection.page_width > 12.0 * 72.0);
    assert!(cover_inspection.all_fonts_embedded);
}

#[test]
fn ingram_fixture_is_internally_validated_pdfx_1a() {
    let mut job = PreparedJob::new("ingram-paperback-pdfx1a-v1");
    job.request["ink"] = Value::String("Color".to_owned());
    job.write_request();
    let output = job.render();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    let response = response(&output);
    assert_eq!(response["status"], "completed");
    assert_eq!(response["evidence"]["validationStatus"], "validated");
    assert_eq!(response["evidence"]["claimedStandard"], "PDF/X-1a:2001");
    assert_eq!(response["evidence"]["pdfVersion"], "1.3");
    assert_eq!(response["evidence"]["outputIntentCount"], 2);
    assert!(
        response["evidence"]["maximumTotalInkPercent"]
            .as_f64()
            .unwrap()
            <= 240.0
    );

    for kind in ["interior-pdf", "cover-pdf"] {
        let pdf = job.artifact(&response, kind);
        let inspection = inspect(&pdf);
        assert_eq!(inspection.version, "1.3");
        assert!(inspection.pdfx_1a);
        assert!(inspection.output_intents >= 1);
        assert!(inspection.all_fonts_embedded);
        assert!(inspection.all_fonts_have_to_unicode);
        assert!(!inspection.device_rgb);
        assert!(inspection.device_cmyk);
        assert!(inspection.image_xobjects > 0);
        assert!(!inspection.transparency);
        assert!(!inspection.encrypted);
        assert_eq!(inspection.annotations, 0);
    }
}

#[test]
fn invalid_ean_13_is_rejected_before_cover_output() {
    let mut job = PreparedJob::new("kdp-paperback-v1");
    job.request["cover"]["barcodeMode"] = Value::String("LorekeeperBarcode".to_owned());
    job.request["cover"]["isbn"] = Value::String("9780306406158".to_owned());
    job.write_request();

    let output = job.render();
    assert!(!output.status.success());
    let response = response(&output);
    assert_eq!(response["status"], "rejected");
    assert_eq!(
        response["jobId"], job.request["jobId"],
        "a parsed request rejection must remain bound to its originating job"
    );
    assert!(has_diagnostic(&response, "PRESS_EAN13_INVALID"));
    assert!(!job.root.path().join("output/cover.pdf").exists());
}

#[test]
fn utf8_bom_request_is_accepted_and_remains_job_bound() {
    let job = PreparedJob::new("kdp-paperback-v1");
    let mut bytes = vec![0xef, 0xbb, 0xbf];
    bytes.extend(serde_json::to_vec_pretty(&job.request).expect("request JSON"));
    fs::write(job.root.path().join("input/request.json"), bytes).expect("BOM request");

    let output = job.render();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    let response = response(&output);
    assert_eq!(response["status"], "completed");
    assert_eq!(response["jobId"], job.request["jobId"]);
}

#[test]
fn parsed_layout_rejections_are_bound_but_unparseable_requests_remain_unbound() {
    let mut parsed = PreparedJob::new("kdp-paperback-v1");
    parsed.request["cover"]["barcodeMode"] = Value::String("LorekeeperBarcode".to_owned());
    parsed.request["cover"]["isbn"] = Value::String("9780306406158".to_owned());
    parsed.write_request();
    let parsed_response = response(&parsed.layout());
    assert_eq!(parsed_response["status"], "rejected");
    assert_eq!(parsed_response["jobId"], parsed.request["jobId"]);
    assert!(has_diagnostic(&parsed_response, "PRESS_EAN13_INVALID"));

    for command in ["render", "layout"] {
        let malformed = PreparedJob::new("kdp-paperback-v1");
        fs::write(
            malformed.root.path().join("input/request.json"),
            b"{malformed",
        )
        .expect("malformed request");
        let output = if command == "render" {
            malformed.render()
        } else {
            malformed.layout()
        };
        let malformed_response = response(&output);
        assert_eq!(malformed_response["status"], "rejected");
        assert!(malformed_response.get("jobId").is_none());
        assert!(has_diagnostic(&malformed_response, "PRESS_REQUEST_INVALID"));
    }
}

#[test]
fn cover_copy_that_cannot_fit_the_safe_region_is_rejected() {
    let mut job = PreparedJob::new("kdp-paperback-v1");
    job.request["cover"]["backCopy"] = Value::String("Bounded cover copy. ".repeat(100));
    job.write_request();

    let output = job.render();
    assert!(!output.status.success());
    let response = response(&output);
    assert_eq!(response["status"], "rejected");
    assert!(has_diagnostic(&response, "PRESS_COVER_TEXT_OVERFLOW"));
    assert!(!job.root.path().join("output/cover.pdf").exists());
}

#[test]
fn cancellation_corrupt_assets_and_undeclared_files_fail_atomically() {
    let cancelled = PreparedJob::new("kdp-paperback-v1");
    fs::write(cancelled.root.path().join("cancel.requested"), b"").expect("cancel marker");
    let cancelled_response = response(&cancelled.render());
    assert_eq!(cancelled_response["status"], "cancelled");
    assert!(has_diagnostic(
        &cancelled_response,
        "PRESS_RENDER_CANCELLED"
    ));

    let mut corrupt = PreparedJob::new("kdp-paperback-v1");
    let corrupt_bytes = b"not a png";
    fs::write(
        corrupt.root.path().join("input/assets/pixel.png"),
        corrupt_bytes,
    )
    .expect("corrupt asset");
    corrupt.request["assets"][0]["byteLength"] = json!(corrupt_bytes.len());
    corrupt.request["assets"][0]["sha256"] = Value::String(hex_hash(corrupt_bytes));
    corrupt.write_request();
    let corrupt_response = response(&corrupt.render());
    assert_eq!(corrupt_response["status"], "rejected");
    assert!(has_diagnostic(&corrupt_response, "PRESS_ASSET_CORRUPT"));

    let unexpected = PreparedJob::new("kdp-paperback-v1");
    fs::write(
        unexpected.root.path().join("input/assets/unexpected.png"),
        PIXEL_PNG,
    )
    .expect("unexpected asset");
    let unexpected_response = response(&unexpected.render());
    assert_eq!(unexpected_response["status"], "rejected");
    assert!(has_diagnostic(
        &unexpected_response,
        "PRESS_ASSET_UNDECLARED"
    ));

    let rogue_input = PreparedJob::new("kdp-paperback-v1");
    fs::write(
        rogue_input.root.path().join("input/undeclared.bin"),
        b"undeclared",
    )
    .expect("rogue input");
    let rogue_response = response(&rogue_input.render());
    assert_eq!(rogue_response["status"], "rejected");
    assert!(has_diagnostic(&rogue_response, "PRESS_ASSET_UNDECLARED"));

    for job in [&cancelled, &corrupt, &unexpected, &rogue_input] {
        assert!(!job.root.path().join("output").exists());
    }
}

#[test]
fn cancellation_during_render_discards_staging_and_never_promotes_output() {
    let mut job = PreparedJob::new("kdp-paperback-v1");
    job.request["document"]["sections"][0]["chapters"][0]["blocks"][1]["content"][0]["text"] =
        Value::String("A deliberately long cancellable paragraph. ".repeat(10_000));
    job.write_request();
    let child = Command::new(env!("CARGO_BIN_EXE_lorekeeper-press"))
        .args(["render", "--job-root", job.root.path().to_str().unwrap()])
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .expect("start cancellable render");
    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(20);
    while !job.root.path().join(".output-staging").exists() && std::time::Instant::now() < deadline
    {
        std::thread::sleep(std::time::Duration::from_millis(5));
    }
    assert!(
        job.root.path().join(".output-staging").exists(),
        "renderer never reached staging"
    );
    fs::write(job.root.path().join("cancel.requested"), b"").expect("cancel marker");
    let cancellation_started = std::time::Instant::now();
    let output = child.wait_with_output().expect("cancelled response");
    assert!(
        cancellation_started.elapsed() < std::time::Duration::from_secs(2),
        "renderer did not honor cancellation within the two-second bound"
    );
    let response = response(&output);
    assert_eq!(response["status"], "cancelled");
    assert!(has_diagnostic(&response, "PRESS_RENDER_CANCELLED"));
    assert!(!job.root.path().join("output").exists());
    assert!(!job.root.path().join(".output-staging").exists());
}

#[test]
fn identical_jobs_produce_identical_artifact_bytes() {
    let first = PreparedJob::new("kdp-paperback-v1");
    let second = PreparedJob::new("kdp-paperback-v1");
    let first_response = response(&first.render());
    let second_response = response(&second.render());

    for kind in ["interior-pdf", "cover-pdf"] {
        assert_eq!(
            fs::read(first.artifact(&first_response, kind)).expect("first artifact"),
            fs::read(second.artifact(&second_response, kind)).expect("second artifact")
        );
    }
}

#[test]
fn layout_trace_keeps_every_line_inside_the_content_box_and_preserves_text() {
    let mut job = PreparedJob::new("kdp-paperback-v1");
    let source = (0..240)
        .map(|index| format!("Retained line sentinel {index:03}."))
        .collect::<Vec<_>>()
        .join(" ");
    job.request["document"]["sections"][0]["chapters"][0]["blocks"][1]["content"][0]["text"] =
        Value::String(source.clone());
    job.write_request();

    let trace = job.layout_trace();
    let pages = trace["pages"].as_array().expect("trace pages");
    let bottom = job.request["trim"]["marginInches"].as_f64().unwrap() * 72.0;
    let top = (job.request["trim"]["heightInches"].as_f64().unwrap()
        - job.request["trim"]["marginInches"].as_f64().unwrap())
        * 72.0;
    let body_lines = pages
        .iter()
        .filter(|page| page["kind"] == "Body")
        .flat_map(|page| page["lines"].as_array().into_iter().flatten())
        .collect::<Vec<_>>();
    for line in body_lines.iter().filter(|line| {
        line["text"]
            .as_str()
            .is_some_and(|text| text.contains("Retained") || text.contains("sentinel"))
    }) {
        let baseline = line["y"].as_f64().expect("baseline");
        let size = line["size"].as_f64().expect("size");
        assert!(
            baseline - size * 0.3 >= bottom - 0.01,
            "line below content box: {line}"
        );
        assert!(
            baseline + size * 0.82 <= top + 0.01,
            "line above content box: {line}"
        );
    }
    let rendered = body_lines
        .iter()
        .map(|line| line["text"].as_str().unwrap_or_default().to_owned())
        .collect::<Vec<_>>()
        .join(" ");
    for index in 0..240 {
        assert!(
            rendered.contains(&format!("sentinel {index:03}")),
            "lost sentinel {index:03}"
        );
    }
}

#[test]
fn layout_trace_preserves_shaped_advances_offsets_and_global_chapter_numbering() {
    let mut job = PreparedJob::new("kdp-paperback-v1");
    job.request["document"]["sections"][0]["chapters"][0]["blocks"][1]["content"][0]["text"] =
        Value::String("AVATAR office x\u{301}".to_owned());
    let second_act = job.request["document"]["sections"][0].clone();
    job.request["document"]["sections"]
        .as_array_mut()
        .unwrap()
        .push(second_act);
    job.write_request();

    let trace = job.layout_trace();
    let glyphs = trace["pages"]
        .as_array()
        .unwrap()
        .iter()
        .flat_map(|page| page["lines"].as_array().into_iter().flatten())
        .flat_map(|line| line["runs"].as_array().into_iter().flatten())
        .flat_map(|run| run["glyphs"].as_array().into_iter().flatten())
        .collect::<Vec<_>>();
    assert!(
        glyphs
            .iter()
            .any(|glyph| glyph["xAdvance"].as_f64().is_some())
    );
    assert!(
        glyphs.iter().any(|glyph| {
            glyph["xOffset"].as_f64().unwrap_or_default() != 0.0
                || glyph["yOffset"].as_f64().unwrap_or_default() != 0.0
                || glyph["xAdvance"].as_f64().unwrap_or_default() == 0.0
        }),
        "combining-mark positioning must survive shaping"
    );
    let text = trace.to_string();
    assert!(text.contains("Chapter 1"));
    assert!(text.contains("Chapter 2"));
}

#[test]
fn emitted_pdf_text_matrices_match_harfrust_layout_positions() {
    let mut job = PreparedJob::new("kdp-paperback-v1");
    job.request["cover"] = Value::Null;
    job.request["document"]["includeTitlePage"] = Value::Bool(false);
    job.request["document"]["includeVisibleTableOfContents"] = Value::Bool(false);
    job.request["document"]["includeActHeadings"] = Value::Bool(false);
    job.request["document"]["includeChapterHeadings"] = Value::Bool(false);
    job.request["document"]["matter"] = json!([]);
    job.request["document"]["placements"] = json!([]);
    job.request["document"]["sections"] = json!([{
        "id": "act", "title": "", "includePage": false, "includeHeading": false,
        "chapters": [{
            "id": "chapter", "title": "", "includeHeading": false,
            "visualMode": "Standard", "illustrations": [], "picturePage": null,
            "blocks": [{
                "id": "block", "type": "Paragraph", "styleRole": "body",
                "content": [{ "type": "Text", "text": "AVATAR office x\u{301}", "marks": [] }]
            }]
        }]
    }]);
    job.write_request();

    let trace = job.layout_trace();
    let line = trace["pages"]
        .as_array()
        .unwrap()
        .iter()
        .flat_map(|page| page["lines"].as_array().into_iter().flatten())
        .find(|line| line["text"] == "AVATAR office x\u{301}")
        .expect("shaped trace line");
    let glyphs = line["runs"][0]["glyphs"].as_array().unwrap();
    let rendered = response(&job.render());
    let document = Document::load(job.artifact(&rendered, "interior-pdf")).expect("PDF");
    let page_id = *document.get_pages().values().next().unwrap();
    let operations = lopdf::content::Content::decode(&document.get_page_content(page_id))
        .unwrap()
        .operations;
    let matrices = operations
        .iter()
        .filter(|operation| operation.operator == "Tm")
        .take(glyphs.len())
        .collect::<Vec<_>>();
    assert_eq!(matrices.len(), glyphs.len());
    let mut advance = 0.0;
    for (matrix, glyph) in matrices.iter().zip(glyphs) {
        let actual_x = number(&matrix.operands[4]);
        let actual_y = number(&matrix.operands[5]);
        let expected_x = line["x"].as_f64().unwrap() + advance + glyph["xOffset"].as_f64().unwrap();
        let expected_y = line["y"].as_f64().unwrap() + glyph["yOffset"].as_f64().unwrap();
        assert!(
            (actual_x - expected_x).abs() < 0.02,
            "x placement differs: {matrix:?}"
        );
        assert!(
            (actual_y - expected_y).abs() < 0.02,
            "y placement differs: {matrix:?}"
        );
        advance += glyph["xAdvance"].as_f64().unwrap();
    }
}

#[test]
fn every_print_picture_page_mode_has_distinct_validated_geometry() {
    let expectations = [
        ("WholeSpread", 1usize, 0.0f64),
        ("SidewaysWholeSpread", 1usize, 90.0f64),
        ("SplitLeaves", 2usize, 0.0f64),
    ];
    for (mode, leaf_count, rotation) in expectations {
        let mut job = PreparedJob::new("kdp-paperback-v1");
        job.replace_pixel_with_landscape_png(200, 100);
        job.request["document"]["printPicturePageSpreadMode"] = Value::String(mode.to_owned());
        job.write_request();
        let trace = job.layout_trace();
        let picture_pages = trace["pages"]
            .as_array()
            .unwrap()
            .iter()
            .filter(|page| {
                page["kind"] == "PicturePage" && page.to_string().contains("Picture Page")
            })
            .collect::<Vec<_>>();
        assert_eq!(picture_pages.len(), leaf_count, "mode {mode}");
        assert!(
            picture_pages
                .iter()
                .all(|page| page["rotationDegrees"] == rotation),
            "mode {mode}"
        );
        assert!(picture_pages.iter().all(|page| {
            page["images"].as_array().is_some_and(|images| {
                images.iter().all(|image| {
                    image["contain"] == (mode != "SplitLeaves")
                        && (mode != "SplitLeaves"
                            || (image["sourceWidthFraction"].as_f64().unwrap() - 0.5).abs() < 0.001)
                })
            })
        }));

        let rendered = response(&job.render());
        let document = Document::load(job.artifact(&rendered, "interior-pdf")).expect("PDF");
        let page_ids = document.get_pages().into_values().collect::<Vec<_>>();
        for page in picture_pages {
            let page_index = trace["pages"]
                .as_array()
                .unwrap()
                .iter()
                .position(|candidate| std::ptr::eq(candidate, page))
                .expect("picture page index");
            let operations =
                lopdf::content::Content::decode(&document.get_page_content(page_ids[page_index]))
                    .expect("page content")
                    .operations;
            let matrix = operations
                .iter()
                .find(|operation| operation.operator == "cm")
                .expect("image transform");
            let visible_width = if rotation == 90.0 {
                number(&matrix.operands[2]).abs()
            } else {
                number(&matrix.operands[0]).abs()
            };
            let visible_height = if rotation == 90.0 {
                number(&matrix.operands[1]).abs()
            } else {
                number(&matrix.operands[3]).abs()
            };
            let image = &page["images"][0];
            if mode != "SplitLeaves" {
                assert!(visible_width <= image["width"].as_f64().unwrap() + 0.02);
                assert!(visible_height <= image["height"].as_f64().unwrap() + 0.02);
                assert!(
                    (visible_width - image["width"].as_f64().unwrap()).abs() < 0.02
                        || (visible_height - image["height"].as_f64().unwrap()).abs() < 0.02
                );
            }
        }
    }
}

#[test]
fn black_and_white_intent_outputs_gray_images_but_keeps_color_cover_independent() {
    let mut job = PreparedJob::new("kdp-paperback-v1");
    job.request["ink"] = Value::String("BlackAndWhite".to_owned());
    job.write_request();
    let rendered = response(&job.render());
    let interior = inspect(&job.artifact(&rendered, "interior-pdf"));
    assert!(!interior.device_rgb);
    assert!(!interior.device_cmyk);
    assert!(interior.device_gray);
    let cover = inspect(&job.artifact(&rendered, "cover-pdf"));
    assert!(cover.device_rgb);
    assert!(!cover.device_cmyk);

    let mut ingram = PreparedJob::new("ingram-paperback-pdfx1a-v1");
    ingram.request["ink"] = Value::String("BlackAndWhite".to_owned());
    ingram.write_request();
    let ingram_response = response(&ingram.render());
    let ingram_interior = inspect(&ingram.artifact(&ingram_response, "interior-pdf"));
    assert!(ingram_interior.device_gray && !ingram_interior.device_rgb);
    let ingram_cover = inspect(&ingram.artifact(&ingram_response, "cover-pdf"));
    assert!(ingram_cover.device_cmyk && !ingram_cover.device_rgb);
}

#[test]
fn cropped_picture_dpi_uses_the_consumed_source_rectangle() {
    let mut job = PreparedJob::new("kdp-paperback-v1");
    job.request["document"]["printPicturePageSpreadMode"] = Value::String("SplitLeaves".to_owned());
    job.write_request();
    let response = response(&job.render());
    let reported = response["evidence"]["minimumEffectiveDpi"]
        .as_f64()
        .unwrap();
    let placed_width_points = (6.0 - 1.5) * 72.0;
    let expected_horizontal = 0.5 / (placed_width_points / 72.0);
    assert!(
        reported <= expected_horizontal + 0.01,
        "reported={reported} expected<={expected_horizontal}"
    );
}

#[test]
fn long_spine_copy_is_rejected_and_subset_tags_are_standard_six_letter_tags() {
    let mut overflow = PreparedJob::new("kdp-paperback-v1");
    overflow.request["cover"]["spineText"] = Value::String("W".repeat(240));
    overflow.write_request();
    let rejected = response(&overflow.render());
    assert_eq!(rejected["status"], "rejected");
    assert!(has_diagnostic(&rejected, "PRESS_COVER_TEXT_OVERFLOW"));

    let job = PreparedJob::new("kdp-paperback-v1");
    let rendered = response(&job.render());
    for name in inspect(&job.artifact(&rendered, "interior-pdf")).font_names {
        let bytes = name.as_bytes();
        assert!(
            bytes.len() > 7 && bytes[0..6].iter().all(u8::is_ascii_uppercase) && bytes[6] == b'+',
            "invalid subset name {name}"
        );
    }
}

#[test]
fn cover_width_is_derived_from_the_final_interior_page_count() {
    let short = PreparedJob::new("kdp-paperback-v1");
    let mut long = PreparedJob::new("kdp-paperback-v1");
    long.request["document"]["sections"][0]["chapters"][0]["blocks"][1]["content"][0]["text"] =
        Value::String("A measured sentence fills the page. ".repeat(2_000));
    long.write_request();

    let short_response = response(&short.render());
    let long_response = response(&long.render());
    let short_pages = artifact_value(&short_response, "interior-pdf")["pageCount"]
        .as_u64()
        .unwrap();
    let long_pages = artifact_value(&long_response, "interior-pdf")["pageCount"]
        .as_u64()
        .unwrap();
    assert!(long_pages > short_pages);
    assert!(
        long_response["evidence"]["coverWidthPoints"]
            .as_f64()
            .unwrap()
            > short_response["evidence"]["coverWidthPoints"]
                .as_f64()
                .unwrap()
    );
    let expected_spine = long_pages as f64 * 0.0025 * 72.0;
    let actual_spine = long_response["evidence"]["spineWidthPoints"]
        .as_f64()
        .unwrap();
    assert!((actual_spine - expected_spine).abs() < 0.001);
}

#[test]
fn unsupported_script_fails_without_artifacts() {
    let mut job = PreparedJob::new("kdp-paperback-v1");
    job.request["document"]["language"] = Value::String("ar".to_owned());
    job.request["document"]["title"] = Value::String("كتاب".to_owned());
    job.write_request();

    let output = job.render();
    assert!(!output.status.success());
    let response = response(&output);
    assert_eq!(response["status"], "rejected");
    assert!(response["artifacts"].as_array().unwrap().is_empty());
    assert!(has_diagnostic(&response, "PRESS_LANGUAGE_UNSUPPORTED"));
    assert!(!job.root.path().join("output/interior.pdf").exists());
}

#[test]
fn unsupported_inline_language_fails_without_artifacts() {
    let mut job = PreparedJob::new("kdp-paperback-v1");
    job.request["document"]["sections"][0]["chapters"][0]["blocks"][1]["content"][0]["marks"] =
        json!([{ "type": "Language", "value": "fr" }]);
    job.write_request();

    let output = job.render();
    assert!(!output.status.success());
    let response = response(&output);
    assert_eq!(response["status"], "rejected");
    assert!(has_diagnostic(&response, "PRESS_LANGUAGE_UNSUPPORTED"));
    assert!(!job.root.path().join("output/interior.pdf").exists());
}

#[test]
fn traversal_and_hash_mismatch_are_rejected_before_output() {
    let mut traversal = PreparedJob::new("kdp-paperback-v1");
    traversal.request["assets"][0]["relativePath"] = Value::String("../outside.png".to_owned());
    traversal.write_request();
    let traversal_response = response(&traversal.render());
    assert_eq!(traversal_response["status"], "rejected");
    assert!(has_diagnostic(
        &traversal_response,
        "PRESS_ASSET_PATH_UNSAFE"
    ));

    let mut modified = PreparedJob::new("kdp-paperback-v1");
    modified.request["assets"][0]["sha256"] = Value::String("0".repeat(64));
    modified.write_request();
    let response = response(&modified.render());
    assert_eq!(response["status"], "rejected");
    assert!(has_diagnostic(&response, "PRESS_ASSET_HASH_MISMATCH"));
    assert!(!modified.root.path().join("output/interior.pdf").exists());
}

#[test]
fn linked_or_reparse_input_directories_are_rejected_before_asset_use() {
    let job = PreparedJob::new("kdp-paperback-v1");
    let linked = job.root.path().join("input/assets");
    let target = job.root.path().join("outside-assets");
    fs::create_dir_all(&target).expect("outside assets");
    fs::copy(linked.join("pixel.png"), target.join("pixel.png")).expect("copy fixture");
    fs::remove_dir_all(&linked).expect("remove ordinary asset directory");
    create_directory_link(&target, &linked);

    let output = job.render();
    let rendered = response(&output);
    assert!(!output.status.success());
    assert!(has_diagnostic(&rendered, "PRESS_ASSET_PATH_UNSAFE"));
    assert!(!job.root.path().join("output").exists());
}

#[cfg(unix)]
fn create_directory_link(target: &Path, linked: &Path) {
    std::os::unix::fs::symlink(target, linked).expect("directory symlink");
}

#[cfg(windows)]
fn create_directory_link(target: &Path, linked: &Path) {
    let output = Command::new("powershell")
        .env("LOREKEEPER_TEST_LINK", linked)
        .env("LOREKEEPER_TEST_TARGET", target)
        .args([
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            "New-Item -ItemType Junction -Path $env:LOREKEEPER_TEST_LINK -Target $env:LOREKEEPER_TEST_TARGET | Out-Null",
        ])
        .output()
        .expect("create directory junction");
    assert!(output.status.success(), "{}", stderr(&output));
}

#[test]
fn undeclared_semantic_asset_references_are_rejected_before_output() {
    let mut job = PreparedJob::new("kdp-paperback-v1");
    job.request["cover"]["assetId"] = Value::String("missing-asset".to_owned());
    job.write_request();

    let output = job.render();
    assert!(!output.status.success());
    let response = response(&output);
    assert_eq!(response["status"], "rejected");
    assert!(has_diagnostic(&response, "PRESS_ASSET_REFERENCE_MISSING"));
    assert!(!job.root.path().join("output").exists());
    assert!(!job.root.path().join(".output-staging").exists());
}

#[test]
fn an_existing_output_is_never_overwritten() {
    let job = PreparedJob::new("kdp-paperback-v1");
    fs::create_dir_all(job.root.path().join("output")).expect("output directory");
    fs::write(job.root.path().join("output/interior.pdf"), b"preserve me").expect("sentinel");

    let output = job.render();
    assert!(!output.status.success());
    let response = response(&output);
    assert_eq!(response["status"], "failed");
    assert!(has_diagnostic(&response, "PRESS_OUTPUT_UNSAFE"));
    assert_eq!(
        fs::read(job.root.path().join("output/interior.pdf")).expect("sentinel"),
        b"preserve me"
    );
}

#[test]
fn immutable_negative_case_matrix_rejects_each_declared_violation() {
    let fixture: Value =
        serde_json::from_slice(include_bytes!("../fixtures/negative-cases-v3.json"))
            .expect("negative fixture matrix");
    for case in fixture["cases"].as_array().expect("cases") {
        let mut job = PreparedJob::new("kdp-paperback-v1");
        let pointer = case["pointer"].as_str().expect("pointer");
        *job.request.pointer_mut(pointer).expect("fixture pointer") = case["value"].clone();
        job.write_request();

        let output = job.render();
        let response = response(&output);
        let diagnostic = case["diagnostic"].as_str().expect("diagnostic");
        assert!(
            !output.status.success() && has_diagnostic(&response, diagnostic),
            "case={} response={response}",
            case["name"]
        );
        assert!(!job.root.path().join("output").exists());
    }
}

#[test]
fn independent_harness_rejects_every_immutable_invalid_pdf_structure() {
    let fixture: Value =
        serde_json::from_slice(include_bytes!("../fixtures/invalid-pdf-structures-v3.json"))
            .expect("invalid PDF fixture matrix");
    for case in fixture["cases"].as_array().expect("cases") {
        let directory = TempDir::new().expect("invalid PDF directory");
        let path = directory.path().join("invalid.pdf");
        fs::write(&path, case["bytes"].as_str().expect("fixture bytes")).expect("invalid PDF");
        assert!(
            Document::load(&path).is_err(),
            "independent parser accepted invalid case {}",
            case["name"]
        );
    }
}

fn run(arguments: &[&str]) -> Output {
    Command::new(env!("CARGO_BIN_EXE_lorekeeper-press"))
        .args(arguments)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .output()
        .expect("start press")
}

struct PreparedJob {
    root: TempDir,
    request: Value,
}

impl PreparedJob {
    fn new(profile: &str) -> Self {
        let root = TempDir::new().expect("job root");
        fs::create_dir_all(root.path().join("input/assets")).expect("input assets");
        fs::write(root.path().join("input/assets/pixel.png"), PIXEL_PNG).expect("pixel PNG");
        let mut request: Value =
            serde_json::from_slice(include_bytes!("../fixtures/full-model-v3.json"))
                .expect("canonical request");
        request["profile"] = Value::String(profile.to_owned());
        request["assets"][0]["byteLength"] = json!(PIXEL_PNG.len());
        request["assets"][0]["sha256"] = Value::String(hex_hash(PIXEL_PNG));
        let job = Self { root, request };
        job.write_request();
        job
    }

    fn write_request(&self) {
        fs::create_dir_all(self.root.path().join("input")).expect("input directory");
        fs::write(
            self.root.path().join("input/request.json"),
            serde_json::to_vec_pretty(&self.request).expect("request JSON"),
        )
        .expect("write request");
    }

    fn replace_pixel_with_landscape_png(&mut self, width: u32, height: u32) {
        let mut bytes = Vec::new();
        {
            let mut encoder = png::Encoder::new(&mut bytes, width, height);
            encoder.set_color(png::ColorType::Rgb);
            encoder.set_depth(png::BitDepth::Eight);
            let mut writer = encoder.write_header().expect("PNG header");
            let pixels = (0..height)
                .flat_map(|y| {
                    (0..width).flat_map(move |x| {
                        [(x % 256) as u8, (y % 256) as u8, ((x + y) % 256) as u8]
                    })
                })
                .collect::<Vec<_>>();
            writer.write_image_data(&pixels).expect("PNG pixels");
        }
        fs::write(self.root.path().join("input/assets/pixel.png"), &bytes).expect("landscape PNG");
        self.request["assets"][0]["byteLength"] = json!(bytes.len());
        self.request["assets"][0]["sha256"] = Value::String(hex_hash(&bytes));
        self.request["assets"][0]["widthPixels"] = json!(width);
        self.request["assets"][0]["heightPixels"] = json!(height);
    }

    fn render(&self) -> Output {
        let child = Command::new(env!("CARGO_BIN_EXE_lorekeeper-press"))
            .args([
                "render",
                "--job-root",
                self.root.path().to_str().expect("UTF-8 path"),
            ])
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .expect("start render");
        child.wait_with_output().expect("render response")
    }

    fn layout(&self) -> Output {
        run(&[
            "layout",
            "--job-root",
            self.root.path().to_str().expect("UTF-8 path"),
        ])
    }

    fn layout_trace(&self) -> Value {
        let output = self.layout();
        assert!(
            output.status.success(),
            "stdout={} stderr={}",
            String::from_utf8_lossy(&output.stdout),
            stderr(&output)
        );
        response(&output)
    }

    fn artifact(&self, response: &Value, kind: &str) -> PathBuf {
        let relative = artifact_value(response, kind)["relativePath"]
            .as_str()
            .expect("artifact relative path");
        self.root
            .path()
            .join(relative.replace('/', std::path::MAIN_SEPARATOR_STR))
    }
}

fn response(output: &Output) -> Value {
    serde_json::from_slice(&output.stdout).unwrap_or_else(|error| {
        panic!(
            "invalid response: {error}; stdout={}; stderr={}",
            String::from_utf8_lossy(&output.stdout),
            stderr(output)
        )
    })
}

fn artifact_value<'a>(response: &'a Value, kind: &str) -> &'a Value {
    response["artifacts"]
        .as_array()
        .expect("artifact array")
        .iter()
        .find(|artifact| artifact["kind"] == kind)
        .expect("artifact kind")
}

fn has_diagnostic(response: &Value, code: &str) -> bool {
    response["diagnostics"]
        .as_array()
        .is_some_and(|items| items.iter().any(|item| item["code"] == code))
}

fn stderr(output: &Output) -> String {
    String::from_utf8_lossy(&output.stderr).into_owned()
}

fn hex_hash(bytes: &[u8]) -> String {
    Sha256::digest(bytes)
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect()
}

struct PdfInspection {
    version: String,
    page_count: usize,
    page_width: f64,
    all_fonts_embedded: bool,
    all_fonts_have_to_unicode: bool,
    font_count: usize,
    output_intents: usize,
    annotations: usize,
    encrypted: bool,
    transparency: bool,
    device_rgb: bool,
    device_cmyk: bool,
    device_gray: bool,
    image_xobjects: usize,
    pdfx_1a: bool,
    font_names: Vec<String>,
}

fn inspect(path: &Path) -> PdfInspection {
    let document = Document::load(path).expect("independent PDF parse");
    let pages = document.get_pages();
    let first_page = document
        .get_dictionary(*pages.values().next().expect("PDF page"))
        .expect("page dictionary");
    let media_box = inherited(&document, first_page, b"MediaBox")
        .and_then(|value| dereference(&document, value))
        .and_then(|value| value.as_array().ok())
        .expect("MediaBox");
    let width = number(&media_box[2]) - number(&media_box[0]);
    let catalog = document.catalog().expect("catalog");
    let output_intents = catalog
        .get(b"OutputIntents")
        .ok()
        .and_then(|value| dereference(&document, value))
        .and_then(|value| value.as_array().ok())
        .map_or(0, Vec::len);
    let info = document
        .trailer
        .get(b"Info")
        .ok()
        .and_then(|value| dereference(&document, value))
        .and_then(|value| value.as_dict().ok());
    let pdfx_1a = info
        .and_then(|dictionary| dictionary.get(b"GTS_PDFXVersion").ok())
        .is_some_and(|value| match value {
            Object::String(bytes, _) => String::from_utf8_lossy(bytes).contains("PDF/X-1a:2001"),
            _ => false,
        });
    let mut fonts = Vec::new();
    let mut annotations = 0;
    let mut device_rgb = false;
    let mut transparency = false;
    let mut device_cmyk = false;
    let mut device_gray = false;
    let mut image_xobjects = 0;
    for page_id in pages.values() {
        let page = document.get_dictionary(*page_id).expect("page");
        annotations += page
            .get(b"Annots")
            .ok()
            .and_then(|value| dereference(&document, value))
            .and_then(|value| value.as_array().ok())
            .map_or(0, Vec::len);
        if let Some(resources) = inherited(&document, page, b"Resources")
            .and_then(|value| dereference(&document, value))
            .and_then(|value| value.as_dict().ok())
        {
            collect_fonts(&document, resources, &mut fonts);
            let rendered = format!("{resources:?}");
            device_rgb |= rendered.contains("DeviceRGB");
            device_cmyk |= rendered.contains("DeviceCMYK");
            transparency |= rendered.contains("ExtGState") || rendered.contains("SMask");
            if let Some(x_objects) = resources
                .get(b"XObject")
                .ok()
                .and_then(|value| dereference(&document, value))
                .and_then(|value| value.as_dict().ok())
            {
                image_xobjects += x_objects.len();
                for (_, value) in x_objects.iter() {
                    let Some(object) =
                        dereference(&document, value).and_then(|value| value.as_stream().ok())
                    else {
                        continue;
                    };
                    let color_space = object.dict.get(b"ColorSpace").ok();
                    device_rgb |=
                        matches!(color_space, Some(Object::Name(name)) if name == b"DeviceRGB");
                    device_cmyk |=
                        matches!(color_space, Some(Object::Name(name)) if name == b"DeviceCMYK");
                    device_gray |=
                        matches!(color_space, Some(Object::Name(name)) if name == b"DeviceGray");
                    transparency |= object.dict.has(b"SMask");
                }
            }
        }
        let content = document.get_page_content(*page_id);
        let rendered = String::from_utf8_lossy(&content);
        device_rgb |= rendered.contains(" rg") || rendered.contains(" RG");
    }

    PdfInspection {
        version: document.version.clone(),
        page_count: pages.len(),
        page_width: width,
        all_fonts_embedded: !fonts.is_empty() && fonts.iter().all(|font| font.1),
        all_fonts_have_to_unicode: !fonts.is_empty() && fonts.iter().all(|font| font.2),
        font_count: fonts
            .iter()
            .map(|font| font.0.as_str())
            .collect::<std::collections::BTreeSet<_>>()
            .len(),
        output_intents,
        annotations,
        encrypted: document.trailer.has(b"Encrypt"),
        transparency,
        device_rgb,
        device_cmyk,
        device_gray,
        image_xobjects,
        pdfx_1a,
        font_names: fonts.iter().map(|font| font.0.clone()).collect(),
    }
}

fn collect_fonts(
    document: &Document,
    resources: &lopdf::Dictionary,
    found: &mut Vec<(String, bool, bool)>,
) {
    let Some(fonts) = resources
        .get(b"Font")
        .ok()
        .and_then(|value| dereference(document, value))
        .and_then(|value| value.as_dict().ok())
    else {
        return;
    };
    for (_, value) in fonts.iter() {
        let Some(font) = dereference(document, value).and_then(|value| value.as_dict().ok()) else {
            continue;
        };
        let to_unicode = font.has(b"ToUnicode");
        let name = font
            .get(b"BaseFont")
            .ok()
            .and_then(|value| value.as_name().ok())
            .map_or_else(
                || "unknown".to_owned(),
                |value| String::from_utf8_lossy(value).into_owned(),
            );
        let embedded = font
            .get(b"DescendantFonts")
            .ok()
            .and_then(|value| dereference(document, value))
            .and_then(|value| value.as_array().ok())
            .is_some_and(|descendants| {
                descendants.iter().any(|value| {
                    dereference(document, value)
                        .and_then(|value| value.as_dict().ok())
                        .and_then(|font| font.get(b"FontDescriptor").ok())
                        .and_then(|value| dereference(document, value))
                        .and_then(|value| value.as_dict().ok())
                        .is_some_and(|descriptor| {
                            descriptor.has(b"FontFile")
                                || descriptor.has(b"FontFile2")
                                || descriptor.has(b"FontFile3")
                        })
                })
            });
        found.push((name, embedded, to_unicode));
    }
}

fn inherited<'a>(
    document: &'a Document,
    dictionary: &'a lopdf::Dictionary,
    key: &[u8],
) -> Option<&'a Object> {
    if let Ok(value) = dictionary.get(key) {
        return Some(value);
    }
    dictionary
        .get(b"Parent")
        .ok()
        .and_then(|value| dereference(document, value))
        .and_then(|value| value.as_dict().ok())
        .and_then(|parent| inherited(document, parent, key))
}

fn dereference<'a>(document: &'a Document, value: &'a Object) -> Option<&'a Object> {
    match value {
        Object::Reference(id) => document.get_object(*id).ok(),
        _ => Some(value),
    }
}

fn number(value: &Object) -> f64 {
    match value {
        Object::Integer(value) => *value as f64,
        Object::Real(value) => *value as f64,
        _ => panic!("expected number"),
    }
}
