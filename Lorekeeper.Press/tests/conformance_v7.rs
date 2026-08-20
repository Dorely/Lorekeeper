#![recursion_limit = "256"]

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
const CUSTOM_FONT: &[u8] =
    include_bytes!("../../Lorekeeper/wwwroot/fonts/nunito/Nunito-Regular.ttf");
const CUSTOM_CFF_FONT: &[u8] = include_bytes!("fixtures/lorekeeper-test-cff.otf");

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

    assert_eq!(value["protocolVersion"], 7);
    assert_eq!(value["rendererVersion"], "2.1.3");
    assert_eq!(
        value["profiles"],
        json!([
            "generic-print-v2",
            "generic-digital-pdf-v1",
            "ingram-print-pdfx1a-v2",
            "kdp-paperback-v2",
            "kdp-hardcover-v1"
        ])
    );
    assert_eq!(value["machineRuntimeDependencies"], json!([]));
    assert_eq!(value["capabilities"]["designedPages"], true);
    assert_eq!(value["capabilities"]["flowFigures"], true);
    assert_eq!(value["capabilities"]["digitalBookPdf"], true);
    assert_eq!(value["capabilities"]["taggedPdf"], true);
    assert_eq!(value["capabilities"]["mixedPageGeometry"], true);
    assert_eq!(value["capabilities"]["publicationSections"], true);
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
    assert_eq!(response["protocolVersion"], 7);
    assert_eq!(response["rendererVersion"], "2.1.3");
    assert_eq!(response["status"], "completed");
    assert_eq!(response["evidence"]["validationStatus"], "validated");
    assert_eq!(response["evidence"]["pdfVersion"], "1.7");
    assert_eq!(response["evidence"]["tocConverged"], true);
    assert_eq!(response["evidence"]["hasEncryption"], false);
    assert_eq!(response["evidence"]["hasTransparency"], false);
    assert_eq!(response["evidence"]["annotationCount"], 0);
    assert_eq!(response["evidence"]["fontsEmbedded"], true);
    assert_eq!(response["evidence"]["toUnicodeMapsPresent"], true);
    let progress: Value = serde_json::from_slice(
        &fs::read(job.root.path().join("progress.json")).expect("render progress sidecar"),
    )
    .expect("render progress JSON");
    assert_eq!(progress["jobId"], response["jobId"]);
    assert_eq!(progress["percent"], 100);
    assert_eq!(progress["message"], "Render complete");
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
        "flow-figure",
        "designed-page",
        "publication-section",
        "dedicated-cover",
        "inline-marks",
    ] {
        assert!(features.iter().any(|value| value == feature), "{feature}");
    }

    let interior = job.artifact(&response, "interior-pdf");
    let inspection = inspect(&interior);
    assert_eq!(inspection.version, "1.7");
    assert!(inspection.page_count > 0);
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
fn publication_sections_render_in_anchor_order_with_dynamic_contents() {
    let mut job = PreparedJob::new("kdp-paperback-v1");
    job.request["document"]["publicationSections"] = json!([
        {
            "id": "21000000-0000-0000-0000-000000000001",
            "title": "Title page",
            "kind": "TitlePage",
            "systemRole": "Title",
            "anchor": "Front",
            "localOrder": 0,
            "blocks": [{
                "id": "31000000-0000-0000-0000-000000000001",
                "type": "Heading",
                "styleRole": "chapter-title",
                "content": [{ "type": "Text", "text": "The Cartographer's Lantern", "marks": [] }]
            }],
            "pageCompositions": []
        },
        {
            "id": "21000000-0000-0000-0000-000000000002",
            "title": "Contents",
            "kind": "Contents",
            "systemRole": "Contents",
            "anchor": "Front",
            "localOrder": 1,
            "blocks": [],
            "pageCompositions": []
        },
        {
            "id": "21000000-0000-0000-0000-000000000003",
            "title": "About the author",
            "kind": "AboutAuthor",
            "systemRole": "None",
            "anchor": "Back",
            "localOrder": 0,
            "blocks": [{
                "id": "31000000-0000-0000-0000-000000000003",
                "type": "Paragraph",
                "styleRole": "body",
                "content": [{ "type": "Text", "text": "Mara Vale charts imaginary borders.", "marks": [] }]
            }],
            "pageCompositions": []
        }
    ]);
    job.write_request();

    let output = job.render();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    let rendered = response(&output);
    assert_eq!(rendered["status"], "completed");
    let features = rendered["evidence"]["renderedFeatures"]
        .as_array()
        .expect("rendered features");
    assert!(features.iter().any(|value| value == "publication-section"));
    let page_map = rendered["pageMap"].as_array().expect("page map");
    assert!(
        page_map
            .iter()
            .any(|entry| entry["blockId"] == "31000000-0000-0000-0000-000000000001")
    );
    assert!(
        page_map
            .iter()
            .any(|entry| entry["blockId"] == "31000000-0000-0000-0000-000000000003")
    );
    assert!(inspect(&job.artifact(&rendered, "interior-pdf")).page_count >= 4);
}

#[test]
fn print_front_matter_preserves_configured_order_and_sides_with_advisory_warnings() {
    let mut job = PreparedJob::new("kdp-paperback-v1");
    job.request["document"]["sections"] = json!([]);
    // Authored front matter is not silently rewritten to match an optional convention.
    job.request["document"]["publicationSections"] = json!([
        {
            "id": "21000000-0000-0000-0000-000000000012",
            "title": "Copyright",
            "kind": "Copyright",
            "systemRole": "Copyright",
            "anchor": "Front",
            "localOrder": 0,
            "startSide": "Recto",
            "blocks": [{
                "id": "31000000-0000-0000-0000-000000000012",
                "type": "Paragraph",
                "styleRole": "body",
                "content": [{ "type": "Text", "text": "Copyright 2026 Mara Vale", "marks": [] }]
            }],
            "pageCompositions": []
        },
        {
            "id": "21000000-0000-0000-0000-000000000011",
            "title": "Title page",
            "kind": "TitlePage",
            "systemRole": "Title",
            "anchor": "Front",
            "localOrder": 1,
            "startSide": "Verso",
            "blocks": [{
                "id": "31000000-0000-0000-0000-000000000011",
                "type": "Heading",
                "styleRole": "chapter-title",
                "content": [{ "type": "Text", "text": "The Cartographer's Lantern", "marks": [] }]
            }],
            "pageCompositions": []
        }
    ]);
    job.write_request();

    let output = job.render();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    let rendered = response(&output);
    assert_eq!(rendered["status"], "completed");
    let page_map = rendered["pageMap"].as_array().expect("page map");
    let title_page = page_map
        .iter()
        .find(|entry| entry["blockId"] == "31000000-0000-0000-0000-000000000011")
        .and_then(|entry| entry["pageNumber"].as_u64())
        .expect("title page map");
    let copyright_page = page_map
        .iter()
        .find(|entry| entry["blockId"] == "31000000-0000-0000-0000-000000000012")
        .and_then(|entry| entry["pageNumber"].as_u64())
        .expect("copyright page map");
    assert_eq!(
        copyright_page, 1,
        "configured copyright placement must be preserved"
    );
    assert_eq!(
        title_page, 2,
        "configured title placement must be preserved"
    );
    assert!(has_diagnostic(
        &rendered,
        "PRESS_FRONT_MATTER_ORDER_RECOMMENDATION"
    ));
    assert!(has_diagnostic(
        &rendered,
        "PRESS_FRONT_MATTER_SIDE_RECOMMENDATION"
    ));
}

#[test]
fn print_facing_designed_pages_begin_on_a_verso_leaf() {
    let mut job = PreparedJob::new("kdp-paperback-v1");
    let mut designed_chapter = job.request["document"]["sections"][0]["chapters"][1].clone();
    designed_chapter["includeHeading"] = json!(false);
    designed_chapter["synopsis"] = json!("");
    job.request["document"]["publicationSections"] = json!([]);
    job.request["document"]["sections"] = json!([{
        "id": "40000000-0000-0000-0000-000000000010",
        "title": "Picture Book",
        "includePage": false,
        "includeHeading": false,
        "chapters": [designed_chapter]
    }]);
    job.write_request();

    let output = job.render();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    let rendered = response(&output);
    assert_eq!(rendered["status"], "completed");
    let first_leaf = rendered["pageMap"]
        .as_array()
        .expect("page map")
        .iter()
        .find(|entry| entry["blockId"] == "60000000-0000-0000-0000-000000000005")
        .and_then(|entry| entry["pageNumber"].as_u64())
        .expect("Designed Page map");
    assert_eq!(
        first_leaf, 2,
        "a facing spread must begin on the left/verso leaf"
    );
}

#[test]
fn print_copyright_can_fill_recto_before_a_title_spread() {
    let mut job = PreparedJob::new("kdp-paperback-v1");
    let composition =
        job.request["document"]["sections"][0]["chapters"][1]["pageCompositions"][0].clone();
    let composition_id = composition["id"].as_str().expect("composition ID");
    job.request["document"]["sections"] = json!([]);
    job.request["document"]["publicationSections"] = json!([
        {
            "id": "21000000-0000-0000-0000-000000000022",
            "title": "Copyright",
            "kind": "Copyright",
            "systemRole": "Copyright",
            "anchor": "Front",
            "localOrder": 0,
            "startSide": "Recto",
            "blocks": [{
                "id": "31000000-0000-0000-0000-000000000022",
                "type": "Paragraph",
                "styleRole": "body",
                "content": [{ "type": "Text", "text": "Copyright 2026 Mara Vale", "marks": [] }]
            }],
            "pageCompositions": []
        },
        {
            "id": "21000000-0000-0000-0000-000000000021",
            "title": "Title page",
            "kind": "TitlePage",
            "systemRole": "Title",
            "anchor": "Front",
            "localOrder": 1,
            "startSide": "Next",
            "blocks": [{
                "id": "31000000-0000-0000-0000-000000000021",
                "type": "DesignedPage",
                "styleRole": "designed-page",
                "pageCompositionId": composition_id,
                "content": []
            }],
            "pageCompositions": [composition]
        }
    ]);
    job.write_request();

    let output = job.render();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    let rendered = response(&output);
    let page_map = rendered["pageMap"].as_array().expect("page map");
    let title_spread = page_map
        .iter()
        .find(|entry| entry["blockId"] == "31000000-0000-0000-0000-000000000021")
        .and_then(|entry| entry["pageNumber"].as_u64())
        .expect("title spread page map");
    let copyright = page_map
        .iter()
        .find(|entry| entry["blockId"] == "31000000-0000-0000-0000-000000000022")
        .and_then(|entry| entry["pageNumber"].as_u64())
        .expect("copyright page map");
    assert_eq!(copyright, 1, "copyright can occupy the opening recto leaf");
    assert_eq!(
        title_spread, 2,
        "the following title spread begins on a verso leaf"
    );
}

#[test]
fn kdp_pdf_17_flattens_composition_opacity_without_pdf_transparency() {
    let mut job = PreparedJob::new("kdp-paperback-v1");
    job.request["document"]["sections"][0]["chapters"][1]["pageCompositions"][0]["variants"][0]["scene"]
        ["objects"][0]["opacity"] = json!(0.5);
    job.write_request();

    let output = job.render();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    let rendered = response(&output);
    assert_eq!(rendered["status"], "completed");
    assert_eq!(rendered["evidence"]["pdfVersion"], "1.7");
    assert_eq!(rendered["evidence"]["hasTransparency"], false);
    assert!(!inspect(&job.artifact(&rendered, "interior-pdf")).transparency);
}

#[test]
fn kdp_flattens_translucent_text_background_into_lower_page_art() {
    let mut job = PreparedJob::new("kdp-paperback-v1");
    job.request["ink"] = json!("Color");
    let artwork = rgb_png(2, 1, &[255, 0, 0, 255, 0, 0]);
    fs::write(job.root.path().join("input/assets/pixel.png"), &artwork).expect("page artwork");
    job.request["assets"][0]["byteLength"] = json!(artwork.len());
    job.request["assets"][0]["sha256"] = json!(hex_hash(&artwork));
    job.request["assets"][0]["widthPixels"] = json!(2);
    let scene = &mut job.request["document"]["sections"][0]["chapters"][1]["pageCompositions"][0]["variants"]
        [0]["scene"];
    scene["surface"] = json!({
        "kind": "IndependentPage", "outputPageMode": "SingleSurface",
        "widthPoints": 432, "heightPoints": 648, "bleedPoints": 0,
        "safeInsetPoints": 36, "allowIndependentPdfPage": false
    });
    scene["objects"][0]["imageFit"] = json!("Stretch");
    let text = &mut job.request["document"]["sections"][0]["chapters"][1]["pageCompositions"][0]["variants"]
        [0]["scene"]["objects"][1];
    text["bounds"] =
        json!({ "xPercent": 0, "yPercent": 0, "widthPercent": 50, "heightPercent": 100 });
    text["fillColor"] = json!("#000000");
    text["backgroundColor"] = json!("#ffffff");
    text["backgroundOpacity"] = json!(0.5);
    job.write_request();

    let rendered = response(&job.render());
    assert_eq!(rendered["status"], "completed");
    assert_eq!(rendered["evidence"]["hasTransparency"], false);
    let pdf = Document::load(job.artifact(&rendered, "interior-pdf")).expect("KDP PDF");
    let flattened_samples = pdf
        .objects
        .values()
        .filter_map(|object| {
            let stream = object.as_stream().ok()?;
            matches!(stream.dict.get(b"Subtype"), Ok(Object::Name(name)) if name == b"Image")
                .then(|| stream.decompressed_content().expect("image samples"))
        })
        .collect::<Vec<_>>();
    assert!(
        flattened_samples
            .into_iter()
            .any(|samples| samples == [255, 128, 128, 255, 0, 0]),
        "the translucent white text background must be baked into the red page artwork"
    );
    assert!(!inspect(&job.artifact(&rendered, "interior-pdf")).transparency);
}

#[test]
fn kdp_omits_a_fully_transparent_text_background() {
    let mut job = PreparedJob::new("kdp-paperback-v1");
    job.request["ink"] = json!("Color");
    let scene = &mut job.request["document"]["sections"][0]["chapters"][1]["pageCompositions"][0]["variants"]
        [0]["scene"];
    scene["surface"] = json!({
        "kind": "IndependentPage", "outputPageMode": "SingleSurface",
        "widthPoints": 432, "heightPoints": 648, "bleedPoints": 0,
        "safeInsetPoints": 36, "allowIndependentPdfPage": false
    });
    scene["objects"][0]["imageFit"] = json!("Contain");
    let text = &mut scene["objects"][1];
    text["fillColor"] = json!("#000000");
    text["backgroundColor"] = json!("#ffffff");
    text["backgroundOpacity"] = json!(0);
    job.write_request();

    let rendered = response(&job.render());
    assert_eq!(rendered["status"], "completed");
    assert_eq!(rendered["evidence"]["hasTransparency"], false);
    let page_number = rendered["pageMap"]
        .as_array()
        .expect("page map")
        .iter()
        .find(|entry| entry["blockId"] == "60000000-0000-0000-0000-000000000005")
        .and_then(|entry| entry["pageNumber"].as_u64())
        .expect("designed page number") as u32;
    let pdf = Document::load(job.artifact(&rendered, "interior-pdf")).expect("KDP PDF");
    let page_id = pdf.get_pages()[&page_number];
    let page_content = pdf.get_page_content(page_id);
    let content = String::from_utf8_lossy(&page_content);
    assert!(
        !content.contains("1 1 1 rg"),
        "a zero-opacity white background must not become an opaque white rectangle"
    );
}

#[test]
fn ingram_flattens_composition_opacity_without_pdf_transparency() {
    let mut job = PreparedJob::new("ingram-paperback-pdfx1a-v1");
    job.request["document"]["sections"][0]["chapters"][1]["pageCompositions"][0]["variants"][0]["scene"]
        ["objects"][0]["opacity"] = json!(0.5);
    job.write_request();

    let output = job.render();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    let rendered = response(&output);
    assert_eq!(rendered["status"], "completed");
    assert_eq!(rendered["evidence"]["pdfVersion"], "1.3");
    assert_eq!(rendered["evidence"]["hasTransparency"], false);
    let inspection = inspect(&job.artifact(&rendered, "interior-pdf"));
    assert!(inspection.pdfx_1a);
    assert!(!inspection.transparency);
}

#[test]
fn ingram_flattens_translucent_shape_into_lower_page_art() {
    let mut job = PreparedJob::new("ingram-paperback-pdfx1a-v1");
    let artwork = rgb_png(256, 256, &vec![192; 256 * 256 * 3]);
    fs::write(job.root.path().join("input/assets/pixel.png"), &artwork).expect("page artwork");
    job.request["assets"][0]["byteLength"] = json!(artwork.len());
    job.request["assets"][0]["sha256"] = json!(hex_hash(&artwork));
    job.request["assets"][0]["widthPixels"] = json!(256);
    job.request["assets"][0]["heightPixels"] = json!(256);
    let objects =
        job.request["document"]["sections"][0]["chapters"][1]["pageCompositions"][0]["variants"][0]
            ["scene"]["objects"]
            .as_array_mut()
            .expect("composition objects");
    objects.push(json!({
        "id": "74000000-0000-0000-0000-000000000099",
        "layerId": "73000000-0000-0000-0000-000000000001",
        "kind": "Rectangle",
        "bounds": { "xPercent": 10, "yPercent": 10, "widthPercent": 25, "heightPercent": 25 },
        "fillColor": "#ff0000",
        "strokeColor": "transparent",
        "strokeWidthPoints": 0,
        "opacity": 0.5,
        "semanticRole": "Artifact",
        "zIndex": 4
    }));
    job.write_request();

    let output = job.render();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    let rendered = response(&output);
    assert_eq!(rendered["status"], "completed");
    assert_eq!(rendered["evidence"]["hasTransparency"], false);
    assert!(!inspect(&job.artifact(&rendered, "interior-pdf")).transparency);
}

#[test]
fn ingram_flattens_decorative_text_shadow_into_lower_page_art() {
    let mut job = PreparedJob::new("ingram-paperback-pdfx1a-v1");
    let artwork = rgb_png(256, 256, &vec![192; 256 * 256 * 3]);
    fs::write(job.root.path().join("input/assets/pixel.png"), &artwork).expect("page artwork");
    job.request["assets"][0]["byteLength"] = json!(artwork.len());
    job.request["assets"][0]["sha256"] = json!(hex_hash(&artwork));
    job.request["assets"][0]["widthPixels"] = json!(256);
    job.request["assets"][0]["heightPixels"] = json!(256);
    let text = &mut job.request["document"]["sections"][0]["chapters"][1]["pageCompositions"][0]["variants"]
        [0]["scene"]["objects"][1];
    text["textShadow"] = json!("Soft");
    text["backgroundOpacity"] = json!(0);
    job.write_request();

    let output = job.render();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    let rendered = response(&output);
    assert_eq!(rendered["status"], "completed");
    assert_eq!(rendered["evidence"]["hasTransparency"], false);
    let interior_path = job.artifact(&rendered, "interior-pdf");
    assert!(!inspect(&interior_path).transparency);
    let pdf = Document::load(&interior_path).expect("Ingram PDF");
    let page_numbers = pdf.get_pages().keys().copied().collect::<Vec<_>>();
    assert!(
        pdf.extract_text(&page_numbers)
            .expect("selectable semantic text")
            .contains("The sea occupied both leaves."),
        "decorative-shadow flattening must retain the semantic text"
    );
    assert!(
        pdf.objects
            .values()
            .filter_map(|object| {
                let stream = object.as_stream().ok()?;
                matches!(stream.dict.get(b"Subtype"), Ok(Object::Name(name)) if name == b"Image")
                    .then(|| stream.decompressed_content().expect("image samples"))
            })
            .any(|samples| samples.windows(2).any(|pair| pair[0] != pair[1])),
        "the decorative shadow must be baked into the otherwise uniform page artwork"
    );
}

#[test]
fn ingram_rejects_translucent_semantic_text_over_lower_page_art() {
    let mut job = PreparedJob::new("ingram-paperback-pdfx1a-v1");
    let text = &mut job.request["document"]["sections"][0]["chapters"][1]["pageCompositions"][0]["variants"]
        [0]["scene"]["objects"][1];
    text["opacity"] = json!(0.5);
    text["backgroundOpacity"] = json!(0);
    job.write_request();

    let rendered = response(&job.render());
    assert_eq!(rendered["status"], "failed");
    assert!(has_diagnostic(&rendered, "PRESS_PDFX_TRANSPARENCY_OVERLAP"));
    assert!(rendered["artifacts"].as_array().unwrap().is_empty());
}

#[test]
fn digital_pdf_is_one_tagged_book_with_the_front_cover_as_page_one() {
    let job = PreparedJob::new("generic-digital-pdf-v1");
    let output = job.render();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    let response = response(&output);
    assert_eq!(response["status"], "completed");
    let artifacts = response["artifacts"].as_array().expect("artifacts");
    assert_eq!(artifacts.len(), 1);
    assert_eq!(artifacts[0]["kind"], "book-pdf");
    let inspection = inspect(&job.artifact(&response, "book-pdf"));
    assert!(inspection.page_count > 1);
    assert!(
        inspection.tagged,
        "Digital PDF must expose a structure tree"
    );
    assert!(
        inspection.document_language,
        "Digital PDF must declare its language"
    );
    assert!(
        response["pageMap"]
            .as_array()
            .expect("page map")
            .iter()
            .all(|entry| entry["pageNumber"].as_u64().is_some_and(|page| page >= 2))
    );
}

#[test]
fn short_digital_pdf_does_not_receive_artificial_blank_pages() {
    let mut job = PreparedJob::new("generic-digital-pdf-v1");
    job.request["document"]["includeTableOfContents"] = Value::Bool(false);
    job.request["document"]["publicationSections"] = json!([]);
    job.request["document"]["sections"] = json!([{
        "id": "act", "title": "", "includePage": false, "includeHeading": false,
        "chapters": [{
            "id": "chapter", "title": "", "includeHeading": false,
            "blocks": [{"id":"block","type":"Paragraph","content":[{"type":"Text","text":"Short book.","marks":[]}]}]
        }]
    }]);
    job.write_request();

    let response = response(&job.render());
    let inspection = inspect(&job.artifact(&response, "book-pdf"));
    assert_eq!(
        inspection.page_count, 2,
        "front cover plus one semantic body page"
    );
}

#[test]
fn digital_pdf_exposes_semantic_roles_alt_text_bookmarks_and_internal_links() {
    let mut job = PreparedJob::new("generic-digital-pdf-v1");
    let long_paragraph = (0..80)
        .map(|index| format!("Semantic paragraph phrase {index}."))
        .collect::<Vec<_>>()
        .join(" ");
    job.request["document"]["sections"][0]["chapters"][0]["blocks"] = json!([
        { "id": "heading", "type": "Heading", "styleRole": "heading", "headingLevel": 2,
          "content": [{ "type": "Text", "text": "A semantic heading", "marks": [] }] },
        { "id": "figure", "type": "Figure", "styleRole": "figure-caption",
          "assetId": "90000000-0000-0000-0000-000000000001", "caption": "An accessible caption",
          "altText": "A black square used as a conformance illustration", "decorative": false,
          "presentation": { "placement": "Centered", "widthPercent": 60, "alignment": "Center",
            "textWrap": "None", "fit": "Contain", "cropXPercent": 50, "cropYPercent": 50,
            "spacingBeforePoints": 6, "spacingAfterPoints": 6, "startOnNewPage": false,
            "keepWithCaption": true, "captionPlacement": "Below" },
          "content": [{ "type": "Text", "text": "An accessible caption", "marks": [] }] },
        { "id": "body", "type": "Paragraph", "styleRole": "body",
          "content": [{ "type": "Text", "text": long_paragraph, "marks": [] }] },
        { "id": "list-1", "type": "ListItem", "styleRole": "list-item",
          "content": [{ "type": "Text", "text": "First list item", "marks": [] }] },
        { "id": "list-2", "type": "ListItem", "styleRole": "list-item",
          "content": [{ "type": "Text", "text": "Second list item", "marks": [] }] },
        { "id": "separator", "type": "Paragraph", "styleRole": "body",
          "content": [{ "type": "Text", "text": "A paragraph separates the lists.", "marks": [] }] },
        { "id": "list-3", "type": "ListItem", "styleRole": "list-item",
          "content": [{ "type": "Text", "text": "A separate list item", "marks": [] }] }
    ]);
    job.write_request();
    let output = job.render();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    let rendered = response(&output);
    let pdf = Document::load(job.artifact(&rendered, "book-pdf")).expect("Digital PDF");
    let catalog = pdf.catalog().expect("catalog");
    assert!(
        catalog.has(b"Outlines"),
        "chapter bookmarks must be present"
    );
    let roles = pdf
        .objects
        .values()
        .filter_map(|object| object.as_dict().ok())
        .filter_map(|dictionary| dictionary.get(b"S").ok())
        .filter_map(|value| value.as_name().ok())
        .map(|name| name.to_vec())
        .collect::<Vec<_>>();
    for role in [b"Document".as_slice(), b"H2", b"P", b"Figure", b"Caption"] {
        assert!(
            roles.iter().any(|candidate| candidate == role),
            "missing structure role {}",
            String::from_utf8_lossy(role)
        );
    }
    assert!(
        roles.iter().any(|role| role.as_slice() == b"Link"),
        "TOC annotations require Link structure elements"
    );
    assert!(
        roles.iter().any(|role| role.as_slice() == b"TOCI"),
        "TOC links require TOCI structure parents"
    );
    let structure_dictionaries = pdf
        .objects
        .values()
        .filter_map(|object| object.as_dict().ok())
        .collect::<Vec<_>>();
    assert!(
        structure_dictionaries.iter().any(|dictionary| {
            dictionary
                .get(b"S")
                .ok()
                .and_then(|value| value.as_name().ok())
                == Some(b"P")
                && dictionary
                    .get(b"K")
                    .ok()
                    .and_then(|value| value.as_array().ok())
                    .is_some_and(|children| children.len() > 2)
        }),
        "wrapped lines from one manuscript paragraph must share one semantic P element"
    );
    assert_eq!(
        roles.iter().filter(|role| role.as_slice() == b"L").count(),
        2,
        "separate adjacent-list runs must have distinct L containers"
    );
    assert_eq!(
        roles.iter().filter(|role| role.as_slice() == b"LI").count(),
        3,
        "each manuscript list item must retain its own LI element"
    );
    assert!(
        pdf.objects
            .values()
            .filter_map(|object| object.as_dict().ok())
            .any(|dictionary| dictionary
                .get(b"Alt")
                .ok()
                .and_then(|value| value.as_str().ok())
                .is_some_and(|value| String::from_utf8_lossy(value).contains("black square"))),
        "Figure structure element must carry alternative text"
    );
    let figure_id = pdf
        .objects
        .iter()
        .find_map(|(id, object)| {
            let dictionary = object.as_dict().ok()?;
            (dictionary
                .get(b"S")
                .ok()
                .and_then(|value| value.as_name().ok())
                == Some(b"Figure")
                && dictionary
                    .get(b"Alt")
                    .ok()
                    .and_then(|value| value.as_str().ok())
                    .is_some_and(|value| String::from_utf8_lossy(value).contains("black square")))
            .then_some(*id)
        })
        .expect("Figure structure element");
    assert!(
        pdf.objects.values().any(|object| {
            let Ok(dictionary) = object.as_dict() else {
                return false;
            };
            dictionary
                .get(b"S")
                .ok()
                .and_then(|value| value.as_name().ok())
                == Some(b"Caption")
                && dictionary
                    .get(b"P")
                    .ok()
                    .and_then(|value| value.as_reference().ok())
                    == Some(figure_id)
        }),
        "the caption must be a semantic child of its Figure"
    );
    assert!(
        pdf.get_pages()
            .values()
            .filter_map(|id| pdf.get_dictionary(*id).ok())
            .any(|page| page.get(b"Annots").is_ok()),
        "TOC must expose internal link annotations"
    );
    let expected_page_number = rendered["pageMap"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|entry| entry["chapterId"] == "50000000-0000-0000-0000-000000000001")
        .filter_map(|entry| entry["pageNumber"].as_u64())
        .min()
        .expect("first chapter page") as u32;
    let expected_page_id = *pdf
        .get_pages()
        .get(&expected_page_number)
        .expect("mapped chapter page");
    let link_target = pdf
        .get_pages()
        .values()
        .filter_map(|id| pdf.get_dictionary(*id).ok())
        .filter_map(|page| page.get(b"Annots").ok())
        .filter_map(|value| dereference(&pdf, value))
        .filter_map(|value| value.as_array().ok())
        .flatten()
        .filter_map(|value| dereference(&pdf, value))
        .filter_map(|value| value.as_dict().ok())
        .filter_map(|annotation| annotation.get(b"A").ok())
        .filter_map(|value| dereference(&pdf, value))
        .filter_map(|value| value.as_dict().ok())
        .filter_map(|action| action.get(b"D").ok())
        .filter_map(|value| dereference(&pdf, value))
        .filter_map(|value| value.as_array().ok())
        .filter_map(|destination| destination.first())
        .filter_map(|value| value.as_reference().ok())
        .next()
        .expect("TOC destination page");
    assert_eq!(
        link_target, expected_page_id,
        "Digital cover insertion must shift internal TOC destinations with the page map"
    );
}

#[test]
fn declared_project_font_is_shaped_subsetted_and_embedded_without_fallback() {
    let mut job = PreparedJob::new("generic-digital-pdf-v1");
    fs::create_dir_all(job.root.path().join("input/fonts")).expect("font directory");
    fs::write(
        job.root.path().join("input/fonts/project-regular.ttf"),
        CUSTOM_FONT,
    )
    .expect("project font");
    job.request["fonts"] = json!([{
        "id": "project-regular",
        "familyKey": "project:fixture",
        "weight": 400,
        "italic": false,
        "relativePath": "fonts/project-regular.ttf",
        "mediaType": "font/ttf",
        "byteLength": CUSTOM_FONT.len(),
        "sha256": hex_hash(CUSTOM_FONT),
        "embeddingRightsConfirmed": true
    }]);
    job.request["document"]["styles"][0]["definition"]["fontFamilyKey"] = json!("project:fixture");
    job.write_request();

    let layout = job.layout_trace();
    let run_faces = layout["pages"]
        .as_array()
        .expect("layout pages")
        .iter()
        .flat_map(|page| page["lines"].as_array().expect("layout lines"))
        .flat_map(|line| line["runs"].as_array().expect("layout runs"))
        .map(|run| run["face"].as_str().expect("stable string face identity"))
        .collect::<Vec<_>>();
    assert!(run_faces.contains(&"Custom0"));

    let output = job.render();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    let rendered = response(&output);
    let inspection = inspect(&job.artifact(&rendered, "book-pdf"));
    assert!(
        inspection
            .font_names
            .iter()
            .any(|name| name.contains("LorekeeperCustom-project-fixture-400-Roman")),
        "the declared project face must be embedded under its own deterministic identity: {:?}",
        inspection.font_names
    );
}

#[test]
fn undeclared_font_family_is_rejected_instead_of_silently_substituted() {
    let mut job = PreparedJob::new("generic-digital-pdf-v1");
    job.request["document"]["styles"][0]["definition"]["fontFamilyKey"] =
        json!("builtin:unavailable-family");
    job.write_request();

    let output = job.render();
    assert!(!output.status.success());
    let rendered = response(&output);
    assert!(has_diagnostic(&rendered, "PRESS_FONT_FAMILY_UNSUPPORTED"));
    assert!(rendered["artifacts"].as_array().is_none_or(Vec::is_empty));
}

#[test]
fn declared_cff_otf_uses_cidfont_type0_and_an_opentype_fontfile3_stream() {
    let mut job = PreparedJob::new("generic-digital-pdf-v1");
    fs::create_dir_all(job.root.path().join("input/fonts")).expect("font directory");
    fs::write(
        job.root.path().join("input/fonts/project-cff.otf"),
        CUSTOM_CFF_FONT,
    )
    .expect("project CFF font");
    job.request["fonts"] = json!([{
        "id": "project-cff",
        "familyKey": "project:cff-fixture",
        "weight": 400,
        "italic": false,
        "relativePath": "fonts/project-cff.otf",
        "mediaType": "font/otf",
        "byteLength": CUSTOM_CFF_FONT.len(),
        "sha256": hex_hash(CUSTOM_CFF_FONT),
        "embeddingRightsConfirmed": true
    }]);
    job.request["document"]["styles"][0]["definition"]["fontFamilyKey"] =
        json!("project:cff-fixture");
    job.request["document"]["sections"][0]["chapters"][0]["blocks"] = json!([{
        "id": "cff-body",
        "type": "Paragraph",
        "styleRole": "body",
        "content": [{ "type": "Text", "text": "Custom OTF", "marks": [] }]
    }]);
    job.write_request();

    let output = job.render();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    let rendered = response(&output);
    let pdf = Document::load(job.artifact(&rendered, "book-pdf")).expect("Digital PDF");
    let cff_descendant = pdf
        .objects
        .values()
        .filter_map(|object| object.as_dict().ok())
        .find(|dictionary| {
            dictionary
                .get(b"Subtype")
                .ok()
                .and_then(|value| value.as_name().ok())
                == Some(b"CIDFontType0")
        })
        .expect("CIDFontType0 descendant");
    let descriptor = cff_descendant
        .get(b"FontDescriptor")
        .ok()
        .and_then(|value| dereference(&pdf, value))
        .and_then(|value| value.as_dict().ok())
        .expect("CFF font descriptor");
    let font_file = descriptor
        .get(b"FontFile3")
        .ok()
        .and_then(|value| dereference(&pdf, value))
        .and_then(|value| value.as_stream().ok())
        .expect("FontFile3 stream");
    assert_eq!(
        font_file
            .dict
            .get(b"Subtype")
            .ok()
            .and_then(|value| value.as_name().ok()),
        Some(b"OpenType".as_slice())
    );
}

#[test]
fn protocol_v7_renders_paragraph_presentation_and_structured_page_preview_data() {
    let mut job = PreparedJob::new("generic-digital-pdf-v1");
    let chapter_id = "50000000-0000-0000-0000-000000000001";
    let figure_id = "60000000-0000-0000-0000-000000000001";
    let designed_id = "60000000-0000-0000-0000-000000000002";
    let composition_id = "70000000-0000-0000-0000-000000000001";
    job.request["document"]["sections"] = json!([{
        "id": "act", "title": "", "includePage": false, "includeHeading": false,
        "chapters": [{
            "id": chapter_id, "title": "Visual chapter", "includeHeading": false,
            "blocks": [{
                "id": figure_id, "type": "Figure", "styleRole": "figure-caption",
                "assetId": "90000000-0000-0000-0000-000000000001", "caption": "A flowing caption", "altText": "A test illustration",
                "decorative": false,
                "presentation": { "placement": "Float", "widthPercent": 45, "alignment": "End",
                    "textWrap": "Start", "fit": "Cover", "cropXPercent": 25, "cropYPercent": 75,
                    "spacingBeforePoints": 4, "spacingAfterPoints": 8, "startOnNewPage": false,
                    "keepWithCaption": true, "captionPlacement": "Below" },
                "content": [{ "type": "Text", "text": "A flowing caption", "marks": [] }]
            }, {
                "id": designed_id, "type": "DesignedPage", "styleRole": "designed-page",
                "pageCompositionId": composition_id, "content": []
            }],
            "pageCompositions": [{
                "id": composition_id, "name": "Map spread", "revision": 1,
                "semanticBlocks": [{ "id": "semantic-heading", "type": "Heading", "styleRole": "heading",
                    "headingLevel": 2, "content": [
                        { "type": "Text", "text": "Map ", "marks": [] },
                        { "type": "Text", "text": "legend", "marks": [{ "type": "Strong" }] }
                    ] }],
                "variants": [{ "id": "variant", "geometryKey": "custom-wide", "revision": 1,
                    "scene": { "schemaVersion": 1,
                        "surface": { "kind": "IndependentPage", "outputPageMode": "SingleSurface",
                            "widthPoints": 792, "heightPoints": 432, "bleedPoints": 0,
                            "safeInsetPoints": 24, "allowIndependentPdfPage": true },
                        "layers": [{ "id": "80000000-0000-0000-0000-000000000001", "name": "Content", "order": 0 }],
                        "objects": [{ "id": "90000000-0000-0000-0000-000000000010",
                            "layerId": "80000000-0000-0000-0000-000000000001", "kind": "Rectangle",
                            "bounds": { "xPercent": 8, "yPercent": 8, "widthPercent": 84, "heightPercent": 84 },
                            "fillColor": "#f0e8d8", "strokeColor": "#19324d", "strokeWidthPoints": 2,
                            "opacity": 1, "semanticRole": "Artifact", "zIndex": -2 },
                        { "id": "90000000-0000-0000-0000-000000000011",
                            "layerId": "80000000-0000-0000-0000-000000000001", "kind": "Ellipse",
                            "bounds": { "xPercent": 35, "yPercent": 35, "widthPercent": 30, "heightPercent": 20 },
                            "fillColor": "#d7b45a", "strokeColor": "transparent", "strokeWidthPoints": 0,
                            "opacity": 0.5, "semanticRole": "Artifact", "zIndex": -1 },
                        { "id": "90000000-0000-0000-0000-000000000012",
                            "layerId": "80000000-0000-0000-0000-000000000001", "kind": "Line",
                            "bounds": { "xPercent": 12, "yPercent": 50, "widthPercent": 76, "heightPercent": 1 },
                            "strokeColor": "#19324d", "strokeWidthPoints": 3,
                            "opacity": 1, "semanticRole": "Artifact", "zIndex": 0 },
                        { "id": "90000000-0000-0000-0000-000000000013",
                            "layerId": "80000000-0000-0000-0000-000000000001", "kind": "Image", "visible": false,
                            "bounds": { "xPercent": 0, "yPercent": 0, "widthPercent": 10, "heightPercent": 10 },
                            "imageId": "ffffffff-ffff-ffff-ffff-ffffffffffff", "decorative": true,
                            "semanticRole": "Artifact", "zIndex": 0 },
                        { "id": "90000000-0000-0000-0000-000000000001",
                            "layerId": "80000000-0000-0000-0000-000000000001", "kind": "Image",
                            "bounds": { "xPercent": 0, "yPercent": 0, "widthPercent": 100, "heightPercent": 100 },
                            "imageId": "90000000-0000-0000-0000-000000000001", "imageFit": "Cover", "rotationDegrees": 30,
                            "altText": "A map", "decorative": false,
                            "semanticRole": "Figure", "readingOrder": 1, "zIndex": 0 },
                        { "id": "90000000-0000-0000-0000-000000000014",
                            "layerId": "80000000-0000-0000-0000-000000000001", "kind": "Text",
                            "bounds": { "xPercent": 10, "yPercent": 10, "widthPercent": 35, "heightPercent": 15 },
                            "contentReferences": [{ "blockId": "semantic-heading" }], "fontFamilyKey": "sans",
                            "fontSizePoints": 18, "letterSpacingEm": 0.1, "textAlignment": "Center",
                            "verticalAlignment": "Bottom", "backgroundColor": "#fff1cc", "backgroundOpacity": 0.8,
                            "textShadow": "Soft", "rotationDegrees": 30,
                            "semanticRole": "Heading2", "readingOrder": 2, "zIndex": 2 }]
                    }
                }]
            }]
        }]
    }]);
    job.request["document"]["allowDesignedPageOverrides"] = Value::Bool(true);
    job.request["cover"]["backgroundColor"] = Value::String("#cc3311".to_owned());
    job.write_request();

    let output = job.render();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    let response = response(&output);
    let features = response["evidence"]["renderedFeatures"]
        .as_array()
        .expect("features");
    assert!(features.iter().any(|value| value == "flow-figure"));
    assert!(features.iter().any(|value| value == "designed-page"));
    assert_eq!(response["evidence"]["hasTransparency"], true);
    let map = response["pageMap"].as_array().expect("page map");
    assert!(map.iter().any(|entry| entry["blockId"] == figure_id));
    assert!(map.iter().any(|entry| entry["blockId"] == designed_id));
    let trace = job.layout_trace();
    let designed_page = trace["pages"]
        .as_array()
        .expect("trace pages")
        .iter()
        .find(|page| {
            page["lines"]
                .as_array()
                .is_some_and(|lines| lines.iter().any(|line| line["text"] == "Map legend"))
        })
        .expect("designed page trace");
    let heading = designed_page["lines"]
        .as_array()
        .expect("designed lines")
        .iter()
        .rfind(|line| line["text"] == "Map legend")
        .expect("semantic designed heading");
    assert!((heading["characterSpacing"].as_f64().unwrap() - 1.8).abs() < 0.01);
    assert!(
        heading["runs"].as_array().unwrap().iter().any(|run| {
            run["text"] == "legend"
                && run["face"]
                    .as_str()
                    .is_some_and(|face| face.contains("Bold"))
        }),
        "Designed Page references must preserve semantic inline marks rather than flattening text"
    );
    assert!(
        heading["x"].as_f64().unwrap() > 79.2,
        "center alignment must offset the line within its frame"
    );
    assert!(
        designed_page["shapes"].as_array().unwrap().len() >= 4,
        "the text frame background must remain a vector shape"
    );
    assert!(
        designed_page["lines"]
            .as_array()
            .unwrap()
            .iter()
            .any(|line| line["artifact"] == true && line["text"] == "Map legend"),
        "text shadow paint must be an artifact rather than duplicate semantic content"
    );
    assert!(
        designed_page["images"]
            .as_array()
            .unwrap()
            .iter()
            .any(|image| {
                image["fit"] == "Cover"
                    && (image["rotationDegrees"].as_f64().unwrap() - 30.0).abs() < 0.01
            }),
        "Cover fit and arbitrary image rotation must survive structured layout"
    );
    let pdf = Document::load(job.artifact(&response, "book-pdf")).expect("Digital PDF");
    let first_page_id = *pdf.get_pages().values().next().expect("digital cover page");
    let cover_operations = lopdf::content::Content::decode(&pdf.get_page_content(first_page_id))
        .expect("digital cover content")
        .operations;
    assert!(
        cover_operations.iter().any(|operation| {
            operation.operator == "rg"
                && operation.operands.len() == 3
                && (number(&operation.operands[0]) - 0.8).abs() < 0.001
                && (number(&operation.operands[1]) - 0.2).abs() < 0.001
                && (number(&operation.operands[2]) - (17.0 / 255.0)).abs() < 0.001
        }),
        "the single-file Digital PDF cover must preserve its configured background color"
    );
    let has_wide_page = pdf.get_pages().values().any(|page_id| {
        let page = pdf.get_dictionary(*page_id).expect("page dictionary");
        inherited(&pdf, page, b"MediaBox")
            .and_then(|value| dereference(&pdf, value))
            .and_then(|value| value.as_array().ok())
            .is_some_and(|media_box| {
                (number(&media_box[2]) - number(&media_box[0]) - 792.0).abs() < 0.01
                    && (number(&media_box[3]) - number(&media_box[1]) - 432.0).abs() < 0.01
            })
    });
    assert!(
        has_wide_page,
        "Digital PDF must preserve the designed page's explicit page box"
    );
    let rendered_pages = pdf.get_pages();
    let wide_page_id = rendered_pages
        .values()
        .find(|page_id| {
            pdf.get_dictionary(**page_id)
                .ok()
                .and_then(|page| inherited(&pdf, page, b"MediaBox"))
                .and_then(|value| dereference(&pdf, value))
                .and_then(|value| value.as_array().ok())
                .is_some_and(|box_values| (number(&box_values[2]) - 792.0).abs() < 0.01)
        })
        .expect("wide designed page");
    let operations = lopdf::content::Content::decode(&pdf.get_page_content(*wide_page_id))
        .expect("designed page content")
        .operations;
    assert!(
        operations
            .iter()
            .filter(|operation| operation.operator == "re")
            .count()
            >= 2,
        "structured rectangles must remain vector paths"
    );
    assert!(
        operations.iter().any(|operation| operation.operator == "c"),
        "structured ellipses must remain vector Bezier paths"
    );
    assert!(
        operations.iter().any(|operation| operation.operator == "l"),
        "structured lines must remain vector paths"
    );
    assert!(
        operations
            .iter()
            .any(|operation| operation.operator == "gs"),
        "Digital composition opacity must be emitted through a graphics state"
    );
    assert!(
        operations
            .iter()
            .filter(|operation| operation.operator == "cm")
            .any(|operation| {
                operation.operands.len() == 6
                    && (number(&operation.operands[0]) - 30f64.to_radians().cos()).abs() < 0.001
                    && (number(&operation.operands[1]) - 0.5).abs() < 0.001
            }),
        "image and text objects must emit a general center-based rotation matrix"
    );
    let image_paint = operations
        .iter()
        .position(|operation| operation.operator == "Do")
        .expect("scene image paint");
    let line_paint = operations
        .iter()
        .enumerate()
        .skip(image_paint + 1)
        .find(|(_, operation)| operation.operator == "l")
        .map(|(index, _)| index)
        .expect("line painted above scene image");
    assert!(
        image_paint < line_paint,
        "scene layer/z order must survive PDF serialization"
    );
}

#[test]
fn digital_layout_splits_facing_spreads_authored_as_edition_leaves_when_page_overrides_are_enabled()
{
    let mut job = PreparedJob::new("generic-digital-pdf-v1");
    job.request["document"]["allowDesignedPageOverrides"] = Value::Bool(true);
    job.request["document"]["sections"][0]["chapters"][0]["blocks"] = json!([{
        "id": "designed-spread", "type": "DesignedPage", "styleRole": "designed-page",
        "pageCompositionId": "spread-composition", "content": []
    }]);
    job.request["document"]["sections"][0]["chapters"][0]["pageCompositions"] = json!([{
        "id": "spread-composition", "name": "Facing artwork", "revision": 1,
        "semanticBlocks": [],
        "variants": [{ "id": "spread-variant", "geometryKey": "facing", "revision": 1,
            "scene": { "schemaVersion": 1,
                "surface": { "kind": "FacingSpread", "outputPageMode": "EditionLeaves",
                    "widthPoints": 864, "heightPoints": 648, "bleedPoints": 0,
                    "safeInsetPoints": 24, "allowIndependentPdfPage": false },
                "layers": [{ "id": "spread-layer", "name": "Artwork", "order": 0 }],
                "objects": [{ "id": "spread-image", "layerId": "spread-layer", "kind": "Image",
                    "bounds": { "xPercent": 0, "yPercent": 0, "widthPercent": 100, "heightPercent": 100 },
                    "imageId": "90000000-0000-0000-0000-000000000001", "imageFit": "Cover",
                    "altText": "Facing split sentinel", "decorative": false,
                    "semanticRole": "Figure", "readingOrder": 1, "zIndex": 0 }]
            }
        }]
    }]);
    job.write_request();

    let trace = job.layout_trace();
    let designed_pages = trace["pages"]
        .as_array()
        .expect("trace pages")
        .iter()
        .filter(|page| {
            page["images"].as_array().is_some_and(|images| {
                images
                    .iter()
                    .any(|image| image["altText"] == "Facing split sentinel")
            })
        })
        .collect::<Vec<_>>();

    assert_eq!(
        designed_pages.len(),
        2,
        "an EditionLeaves facing spread must occupy two sequential leaf pages"
    );
    for page in designed_pages {
        assert!((page["widthPoints"].as_f64().unwrap() - 432.0).abs() < 0.01);
        assert!((page["heightPoints"].as_f64().unwrap() - 648.0).abs() < 0.01);
        assert_eq!(page["images"].as_array().unwrap().len(), 1);
    }
}

#[test]
fn publication_sections_honor_flow_caption_and_accessibility_presentation() {
    let mut job = PreparedJob::new("generic-digital-pdf-v1");
    job.request["document"]["publicationSections"] = json!([{
        "id": "70000000-0000-0000-0000-000000000001",
        "anchor": "BeforeChapter",
        "targetId": "50000000-0000-0000-0000-000000000001",
        "kind": "ImagePage",
        "systemRole": "None",
        "localOrder": 0,
        "title": "Compass rose",
        "blocks": [{
            "id": "71000000-0000-0000-0000-000000000001",
            "type": "Figure",
            "styleRole": "figure-caption",
            "assetId": "90000000-0000-0000-0000-000000000001",
            "caption": "This caption is intentionally hidden.",
            "altText": "A navigational compass rose",
            "decorative": false,
            "language": "en-US",
            "accessibilityRole": "Diagram",
            "presentation": {
                "placement": "Float", "widthPercent": 42, "alignment": "End", "textWrap": "Start",
                "fit": "Contain", "cropXPercent": 25, "cropYPercent": 70,
                "spacingBeforePoints": 4, "spacingAfterPoints": 9, "startOnNewPage": false,
                "keepWithCaption": true, "captionPlacement": "Hidden"
            },
            "content": [{ "type": "Text", "text": "This caption is intentionally hidden.", "marks": [] }]
        }],
        "pageCompositions": []
    }]);
    job.write_request();

    let trace = job.layout_trace();
    let pages = trace["pages"].as_array().unwrap();
    let publication_page_index = pages
        .iter()
        .position(|page| {
            page["images"].as_array().is_some_and(|images| {
                images
                    .iter()
                    .any(|image| image["altText"] == "A navigational compass rose")
            })
        })
        .expect("flowing publication section page");
    let placement_page = &pages[publication_page_index];
    let image = placement_page["images"]
        .as_array()
        .unwrap()
        .iter()
        .find(|image| image["altText"] == "A navigational compass rose")
        .unwrap();
    assert_eq!(image["textWrap"], "Start");
    assert_eq!(image["fit"], "Contain");
    assert_eq!(image["accessibilityRole"], "Diagram");
    assert_eq!(image["language"], "en-US");
    let chapter_page_index = pages
        .iter()
        .position(|page| {
            page["lines"].as_array().is_some_and(|lines| {
                lines.iter().any(|line| {
                    line["text"]
                        .as_str()
                        .is_some_and(|text| text.contains("First Coordinate"))
                })
            })
        })
        .expect("target chapter page");
    assert!(
        publication_page_index < chapter_page_index,
        "BeforeChapter publication content must appear directly before its target chapter"
    );
    assert!(
        !pages
            .iter()
            .flat_map(|page| page["lines"].as_array().into_iter().flatten())
            .any(|line| line["text"] == "This caption is intentionally hidden.")
    );
}

#[test]
fn floated_figure_wraps_following_prose_on_the_requested_side() {
    let mut job = PreparedJob::new("generic-digital-pdf-v1");
    job.request["document"]["sections"][0]["chapters"][0]["blocks"] = json!([{
        "id": "float-figure", "type": "Figure", "styleRole": "figure-caption",
        "assetId": "90000000-0000-0000-0000-000000000001", "caption": "Float caption",
        "altText": "A test illustration", "decorative": false,
        "presentation": { "placement": "Float", "widthPercent": 40, "alignment": "End",
            "textWrap": "Start", "fit": "Contain", "cropXPercent": 30, "cropYPercent": 70,
            "spacingBeforePoints": 6, "spacingAfterPoints": 8, "startOnNewPage": false,
            "keepWithCaption": true, "captionPlacement": "Below" },
        "content": [{ "type": "Text", "text": "Float caption", "marks": [] }]
    }, {
        "id": "wrapped-prose", "type": "Paragraph", "styleRole": "body",
        "content": [{ "type": "Text", "text": "Wrapped prose remains beside the illustration while preserving a readable measure on the requested start side.", "marks": [] }]
    }]);
    job.write_request();

    let trace = job.layout_trace();
    let page = trace["pages"]
        .as_array()
        .expect("pages")
        .iter()
        .find(|page| {
            page["lines"]
                .as_array()
                .is_some_and(|lines| lines.iter().any(|line| line["text"] == "Float caption"))
        })
        .expect("figure page");
    let image = &page["images"][0];
    let image_left = image["x"].as_f64().expect("image x");
    let image_bottom = image["y"].as_f64().expect("image y");
    let image_top = image_bottom + image["height"].as_f64().expect("image height");
    let prose = page["lines"]
        .as_array()
        .expect("lines")
        .iter()
        .find(|line| {
            line["text"]
                .as_str()
                .is_some_and(|text| text.starts_with("Wrapped"))
        })
        .expect("wrapped prose line");
    let prose_x = prose["x"].as_f64().expect("prose x");
    let prose_y = prose["y"].as_f64().expect("prose y");
    assert!(
        prose_y > image_bottom && prose_y < image_top,
        "prose must share the figure's vertical band"
    );
    assert!(
        prose_x < image_left,
        "Start-side wrap must place prose before an end-aligned figure"
    );
}

#[test]
fn figure_fit_crop_position_and_above_caption_are_preserved_in_layout() {
    let mut job = PreparedJob::new("generic-digital-pdf-v1");
    job.request["document"]["sections"][0]["chapters"][0]["blocks"] = json!([{
        "id": "presented-figure", "type": "Figure", "styleRole": "figure-caption",
        "assetId": "90000000-0000-0000-0000-000000000001", "caption": "Caption above",
        "altText": "A test illustration", "decorative": false,
        "presentation": { "placement": "Centered", "widthPercent": 55, "alignment": "Center",
            "textWrap": "None", "fit": "Contain", "cropXPercent": 20, "cropYPercent": 80,
            "spacingBeforePoints": 10, "spacingAfterPoints": 12, "startOnNewPage": false,
            "keepWithCaption": true, "captionPlacement": "Above" },
        "content": [{ "type": "Text", "text": "Caption above", "marks": [] }]
    }]);
    job.write_request();

    let trace = job.layout_trace();
    let page = trace["pages"]
        .as_array()
        .expect("pages")
        .iter()
        .find(|page| {
            page["lines"]
                .as_array()
                .is_some_and(|lines| lines.iter().any(|line| line["text"] == "Caption above"))
        })
        .expect("figure page");
    let image = &page["images"][0];
    assert_eq!(image["fit"], "Contain");
    assert!((image["cropX"].as_f64().unwrap() - 0.2).abs() < 0.001);
    assert!((image["cropY"].as_f64().unwrap() - 0.8).abs() < 0.001);
    let image_top = image["y"].as_f64().unwrap() + image["height"].as_f64().unwrap();
    let caption_y = page["lines"]
        .as_array()
        .unwrap()
        .iter()
        .find(|line| line["text"] == "Caption above")
        .unwrap()["y"]
        .as_f64()
        .unwrap();
    assert!(
        caption_y > image_top,
        "Above captions must be laid out above the image frame"
    );
}

#[test]
fn start_on_new_page_keeps_a_centered_figure_in_normal_flow() {
    let mut job = PreparedJob::new("generic-digital-pdf-v1");
    job.request["document"]["sections"][0]["chapters"][0]["blocks"] = json!([{
        "id": "lead", "type": "Paragraph", "styleRole": "body",
        "content": [{ "type": "Text", "text": "Lead prose", "marks": [] }]
    }, {
        "id": "new-page-flow", "type": "Figure", "styleRole": "figure-caption",
        "assetId": "90000000-0000-0000-0000-000000000001", "caption": "Flow caption",
        "altText": "A normal-flow illustration", "decorative": false,
        "presentation": { "placement": "Centered", "widthPercent": 45, "alignment": "Center",
            "textWrap": "None", "fit": "Contain", "cropXPercent": 50, "cropYPercent": 50,
            "spacingBeforePoints": 6, "spacingAfterPoints": 6, "startOnNewPage": true,
            "keepWithCaption": true, "captionPlacement": "Below" },
        "content": [{ "type": "Text", "text": "Flow caption", "marks": [] }]
    }]);
    job.write_request();

    let trace = job.layout_trace();
    let page = trace["pages"]
        .as_array()
        .unwrap()
        .iter()
        .find(|page| {
            page["images"].as_array().is_some_and(|images| {
                images
                    .iter()
                    .any(|image| image["altText"] == "A normal-flow illustration")
            })
        })
        .expect("normal-flow figure page");
    let image = page["images"]
        .as_array()
        .unwrap()
        .iter()
        .find(|image| image["altText"] == "A normal-flow illustration")
        .unwrap();
    let page_height = job.request["trim"]["heightInches"].as_f64().unwrap() * 72.0;
    assert!(
        image["height"].as_f64().unwrap() < page_height * 0.5,
        "startOnNewPage must not imply a dedicated-page figure"
    );
}

#[test]
fn publication_section_figures_render_with_presentation_and_accessibility() {
    let mut job = PreparedJob::new("generic-digital-pdf-v1");
    job.request["document"]["publicationSections"] = json!([{
        "id": "figure-section", "anchor": "Front", "kind": "Preface", "systemRole": "None",
        "localOrder": 0, "title": "Illustrated preface", "blocks": [{
            "id": "section-figure", "type": "Figure", "styleRole": "figure-caption",
            "assetId": "90000000-0000-0000-0000-000000000001", "caption": "Matter caption",
            "altText": "Accessible section art", "decorative": false, "language": "en",
            "accessibilityRole": "Illustration",
            "presentation": { "placement": "Centered", "widthPercent": 52, "alignment": "Center",
                "textWrap": "None", "fit": "Contain", "cropXPercent": 25, "cropYPercent": 75,
                "spacingBeforePoints": 4, "spacingAfterPoints": 4, "startOnNewPage": false,
                "keepWithCaption": true, "captionPlacement": "Above" },
            "content": [{ "type": "Text", "text": "Matter caption", "marks": [] }]
        }], "pageCompositions": []
    }]);
    job.write_request();

    let trace = job.layout_trace();
    let image = trace["pages"]
        .as_array()
        .unwrap()
        .iter()
        .flat_map(|page| page["images"].as_array().into_iter().flatten())
        .find(|image| image["altText"] == "Accessible section art")
        .expect("publication section figure image");
    assert_eq!(image["fit"], "Contain");
    assert!((image["cropX"].as_f64().unwrap() - 0.25).abs() < 0.001);
    assert!((image["cropY"].as_f64().unwrap() - 0.75).abs() < 0.001);
}

#[test]
fn overlay_figure_caption_is_rendered_inside_the_image_frame() {
    let mut job = PreparedJob::new("generic-digital-pdf-v1");
    job.request["document"]["sections"][0]["chapters"][0]["blocks"] = json!([{
        "id": "overlay-figure", "type": "Figure", "styleRole": "figure-caption",
        "assetId": "90000000-0000-0000-0000-000000000001", "caption": "Overlay caption",
        "altText": "A test illustration", "decorative": false,
        "presentation": { "placement": "Centered", "widthPercent": 70, "alignment": "Center",
            "textWrap": "None", "fit": "Cover", "cropXPercent": 50, "cropYPercent": 50,
            "spacingBeforePoints": 0, "spacingAfterPoints": 0, "startOnNewPage": false,
            "keepWithCaption": true, "captionPlacement": "Overlay" },
        "content": [{ "type": "Text", "text": "Overlay caption", "marks": [] }]
    }]);
    job.write_request();

    let trace = job.layout_trace();
    let page = trace["pages"]
        .as_array()
        .expect("pages")
        .iter()
        .find(|page| {
            page["lines"]
                .as_array()
                .is_some_and(|lines| lines.iter().any(|line| line["text"] == "Overlay caption"))
        })
        .expect("overlay-caption page");
    let image = &page["images"][0];
    let image_bottom = image["y"].as_f64().unwrap();
    let image_top = image_bottom + image["height"].as_f64().unwrap();
    let caption = page["lines"]
        .as_array()
        .unwrap()
        .iter()
        .find(|line| line["text"] == "Overlay caption")
        .unwrap();
    let caption_y = caption["y"].as_f64().unwrap();
    assert!(caption_y > image_bottom && caption_y < image_top);
    assert_eq!(
        caption["lightText"], true,
        "overlay copy must remain legible over artwork"
    );
}

#[test]
fn full_bleed_figure_occupies_the_complete_physical_leaf() {
    let mut job = PreparedJob::new("kdp-paperback-v1");
    job.request["document"]["sections"][0]["chapters"][0]["blocks"] = json!([{
        "id": "bleed-figure", "type": "Figure", "styleRole": "figure-caption",
        "assetId": "90000000-0000-0000-0000-000000000001", "caption": "", "altText": "Bleed illustration",
        "decorative": false,
        "presentation": { "placement": "FullBleed", "widthPercent": 100, "alignment": "Center",
            "textWrap": "None", "fit": "Cover", "cropXPercent": 50, "cropYPercent": 50,
            "spacingBeforePoints": 0, "spacingAfterPoints": 0, "startOnNewPage": true,
            "keepWithCaption": true, "captionPlacement": "Hidden" }, "content": []
    }]);
    job.write_request();

    let trace = job.layout_trace();
    let page = trace["pages"]
        .as_array()
        .unwrap()
        .iter()
        .find(|page| {
            page["images"].as_array().is_some_and(|images| {
                images
                    .iter()
                    .any(|image| image["altText"] == "Bleed illustration")
            })
        })
        .expect("full-bleed page");
    let image = page["images"]
        .as_array()
        .unwrap()
        .iter()
        .find(|image| image["altText"] == "Bleed illustration")
        .unwrap();
    let expected_width = job.request["trim"]["widthInches"].as_f64().unwrap() * 72.0;
    let expected_height = job.request["trim"]["heightInches"].as_f64().unwrap() * 72.0;
    assert!(image["x"].as_f64().unwrap().abs() < 0.01);
    assert!(image["y"].as_f64().unwrap().abs() < 0.01);
    assert!((image["width"].as_f64().unwrap() - expected_width).abs() < 0.01);
    assert!((image["height"].as_f64().unwrap() - expected_height).abs() < 0.01);
}

#[test]
fn reusable_composition_style_controls_structured_text_output() {
    let mut job = PreparedJob::new("generic-digital-pdf-v1");
    job.request["ink"] = Value::String("Color".to_owned());
    let style_id = "81000000-0000-0000-0000-000000000001";
    job.request["document"]["sections"][0]["chapters"][0]["blocks"] = json!([{
        "id": "designed", "type": "DesignedPage", "styleRole": "designed-page",
        "pageCompositionId": "71000000-0000-0000-0000-000000000001", "content": []
    }]);
    job.request["document"]["sections"][0]["chapters"][0]["pageCompositions"] = json!([{
        "id": "71000000-0000-0000-0000-000000000001", "name": "Styled page", "revision": 1,
        "semanticBlocks": [{ "id": "styled-copy", "type": "Paragraph", "styleRole": "body",
            "content": [{ "type": "Text", "text": "Reusable style sentinel", "marks": [] }] }],
        "variants": [{ "id": "variant", "geometryKey": "digital", "revision": 1,
            "scene": { "schemaVersion": 1,
                "surface": { "kind": "SinglePage", "outputPageMode": "EditionLeaves", "widthPoints": 432,
                    "heightPoints": 648, "bleedPoints": 0, "safeInsetPoints": 24, "allowIndependentPdfPage": false },
                "layers": [{ "id": "82000000-0000-0000-0000-000000000001", "name": "Content", "order": 0 }],
                "styles": [{ "id": style_id, "name": "Colored display", "fontFamilyKey": "builtin:nunito",
                    "fontWeight": 700, "italic": false, "fontSizePoints": 24, "lineHeight": 1.1,
                    "fillColor": "#cc3366", "strokeColor": "transparent", "strokeWidthPoints": 0 }],
                "objects": [{ "id": "83000000-0000-0000-0000-000000000001",
                    "layerId": "82000000-0000-0000-0000-000000000001", "kind": "Text", "styleId": style_id,
                    "bounds": { "xPercent": 10, "yPercent": 10, "widthPercent": 80, "heightPercent": 25 },
                    "contentReferences": [{ "blockId": "styled-copy" }], "fontFamilyKey": "builtin:lora",
                    "fontWeight": 400, "fontSizePoints": 8, "fillColor": "#000000",
                    "semanticRole": "Paragraph", "readingOrder": 1, "zIndex": 1 }]
            }
        }]
    }]);
    job.write_request();

    let trace = job.layout_trace();
    let line = trace["pages"]
        .as_array()
        .unwrap()
        .iter()
        .flat_map(|page| page["lines"].as_array().unwrap())
        .find(|line| {
            line["text"]
                .as_str()
                .is_some_and(|text| text.contains("Reusable style sentinel"))
        })
        .expect("styled line");
    assert!((line["size"].as_f64().unwrap() - 24.0).abs() < 0.01);
    let fill = line["fillRgb"].as_array().expect("text fill RGB");
    assert!((fill[0].as_f64().unwrap() - 0.8).abs() < 0.001);
    assert!((fill[1].as_f64().unwrap() - 0.2).abs() < 0.001);
    assert!((fill[2].as_f64().unwrap() - 0.4).abs() < 0.001);

    let rendered = response(&job.render());
    let pdf = Document::load(job.artifact(&rendered, "book-pdf")).expect("Digital PDF");
    let has_requested_color = pdf.get_pages().values().any(|page_id| {
        lopdf::content::Content::decode(&pdf.get_page_content(*page_id))
            .ok()
            .is_some_and(|content| {
                content.operations.iter().any(|operation| {
                    operation.operator == "rg"
                        && operation.operands.len() == 3
                        && (number(&operation.operands[0]) - 0.8).abs() < 0.001
                        && (number(&operation.operands[1]) - 0.2).abs() < 0.001
                        && (number(&operation.operands[2]) - 0.4).abs() < 0.001
                })
            })
    });
    assert!(
        has_requested_color,
        "structured text must retain its requested RGB color"
    );
}

#[test]
fn print_full_bleed_uses_vendor_leaf_and_trim_boxes() {
    let mut job = PreparedJob::new("kdp-paperback-v1");
    job.request["trim"]["bleedInches"] = json!(0.125);
    job.request["document"]["sections"][0]["chapters"][0]["blocks"] = json!([{
        "id": "bleed-box-figure", "type": "Figure", "styleRole": "figure-caption",
        "assetId": "90000000-0000-0000-0000-000000000001", "caption": "", "altText": "Bleed box illustration",
        "decorative": false, "presentation": { "placement": "FullBleed", "widthPercent": 100,
            "alignment": "Center", "textWrap": "None", "fit": "Cover", "cropXPercent": 50,
            "cropYPercent": 50, "spacingBeforePoints": 0, "spacingAfterPoints": 0,
            "startOnNewPage": true, "keepWithCaption": true, "captionPlacement": "Hidden" }, "content": []
    }]);
    job.write_request();

    let output = job.render();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    let response = response(&output);
    let page_number = response["pageMap"]
        .as_array()
        .unwrap()
        .iter()
        .find(|entry| entry["blockId"] == "bleed-box-figure")
        .unwrap()["pageNumber"]
        .as_u64()
        .unwrap() as u32;
    let pdf = Document::load(job.artifact(&response, "interior-pdf")).unwrap();
    let page_id = pdf.get_pages()[&page_number];
    let page = pdf.get_dictionary(page_id).unwrap();
    let media = inherited(&pdf, page, b"MediaBox")
        .and_then(|value| dereference(&pdf, value))
        .and_then(|value| value.as_array().ok())
        .unwrap();
    let trim = inherited(&pdf, page, b"TrimBox")
        .and_then(|value| dereference(&pdf, value))
        .and_then(|value| value.as_array().ok())
        .unwrap();
    assert!((number(&media[2]) - number(&media[0]) - 6.125 * 72.0).abs() < 0.01);
    assert!((number(&media[3]) - number(&media[1]) - 9.25 * 72.0).abs() < 0.01);
    assert!((number(&trim[2]) - number(&trim[0]) - 6.0 * 72.0).abs() < 0.01);
    assert!((number(&trim[3]) - number(&trim[1]) - 9.0 * 72.0).abs() < 0.01);
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
    assert!(
        pages
            .iter()
            .all(|page| page["widthPoints"].as_f64().is_some())
    );
    assert!(
        pages
            .iter()
            .all(|page| page["heightPoints"].as_f64().is_some())
    );
    assert!(pages.iter().all(|page| page["paintOrder"].is_array()));
    assert!(
        pages
            .iter()
            .flat_map(|page| page["paintOrder"].as_array().into_iter().flatten())
            .all(|paint| paint["kind"].as_str().is_some() && paint["index"].as_u64().is_some())
    );
    assert!(pages.iter().all(|page| page.get("pageLabel").is_some()));
    assert!(pages.iter().all(|page| page.get("bookmark").is_some()));
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
        assert!(line["rotationDegrees"].as_f64().is_some());
        assert!(line["opacity"].as_f64().is_some());
        assert!(
            line["runs"]
                .as_array()
                .is_some_and(|runs| runs.iter().all(|run| run["face"].as_str().is_some()
                    && run["sizeScale"].as_f64().is_some()
                    && run["baselineShiftEm"].as_f64().is_some()
                    && run["underline"].as_bool().is_some()
                    && run["strikethrough"].as_bool().is_some()))
        );
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
fn layout_trace_applies_sparse_paragraph_presentation_over_book_text_style() {
    let mut job = PreparedJob::new("kdp-paperback-v1");
    let block = &mut job.request["document"]["sections"][0]["chapters"][0]["blocks"][1];
    block["content"][0]["text"] = Value::String("Indented sentinel paragraph.".to_owned());
    block["paragraphPresentation"] = json!({
        "alignment": "Start",
        "leftIndentEm": 2.0,
        "rightIndentEm": 1.0,
        "firstLineIndentEm": 1.5,
        "spacingBeforePoints": 6.0,
        "spacingAfterPoints": 4.0,
        "keepWithNext": false,
        "startOnNewPage": false
    });
    job.write_request();

    let trace = job.layout_trace();
    let line = trace["pages"]
        .as_array()
        .unwrap()
        .iter()
        .flat_map(|page| page["lines"].as_array().into_iter().flatten())
        .find(|line| {
            line["text"]
                .as_str()
                .is_some_and(|text| text.contains("Indented sentinel"))
        })
        .expect("indented line");
    let margin = job.request["trim"]["marginInches"].as_f64().unwrap() * 72.0;
    let body_size = job.request["trim"]["bodyFontSizePoints"].as_f64().unwrap();
    let expected_x = margin + body_size * (2.0 + 1.5);
    assert!((line["x"].as_f64().unwrap() - expected_x).abs() < 0.1);
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
fn browser_preview_reports_pending_image_accessibility_without_weakening_render_validation() {
    let mut job = PreparedJob::new("generic-digital-pdf-v1");
    job.request["layoutTraceMode"] = json!("browser-preview");
    let image = &mut job.request["document"]["sections"][0]["chapters"][1]["pageCompositions"][0]["variants"]
        [0]["scene"]["objects"][0];
    image["accessibilityDecisionPending"] = json!(true);
    image["altText"] = Value::Null;
    image["decorative"] = json!(false);
    job.write_request();

    let layout = job.layout();
    assert!(
        layout.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&layout.stdout),
        stderr(&layout)
    );
    let layout_response = response(&layout);
    assert!(has_diagnostic(
        &layout_response,
        "PRESS_ALT_DECISION_REQUIRED"
    ));
    assert_eq!(layout_response["diagnostics"][0]["severity"], "warning");

    let render = job.render();
    assert!(!render.status.success());
    assert!(has_diagnostic(
        &response(&render),
        "PRESS_ALT_DECISION_REQUIRED"
    ));
}

#[test]
fn browser_preview_clips_overflowing_composition_text_without_weakening_render_validation() {
    let mut job = PreparedJob::new("generic-digital-pdf-v1");
    job.request["layoutTraceMode"] = json!("browser-preview");
    let composition =
        &mut job.request["document"]["sections"][0]["chapters"][1]["pageCompositions"][0];
    composition["semanticBlocks"][0]["content"][0]["text"] =
        Value::String("Overflow sentinel ".repeat(40));
    composition["variants"][0]["scene"]["objects"][1]["bounds"]["heightPercent"] = json!(3);
    job.write_request();

    let layout = job.layout();
    assert!(
        layout.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&layout.stdout),
        stderr(&layout)
    );
    let layout_response = response(&layout);
    let warning = layout_response["diagnostics"]
        .as_array()
        .expect("diagnostics")
        .iter()
        .find(|diagnostic| diagnostic["code"] == "PRESS_COMPOSITION_TEXT_OVERFLOW")
        .expect("overflow warning");
    assert_eq!(warning["severity"], "warning");
    let visible_sentinels = layout_response["pages"]
        .as_array()
        .expect("pages")
        .iter()
        .flat_map(|page| page["lines"].as_array().into_iter().flatten())
        .filter(|line| {
            line["text"]
                .as_str()
                .is_some_and(|text| text.contains("Overflow sentinel"))
        })
        .count();
    assert!(
        visible_sentinels < 40,
        "preview must omit text hidden below the frame"
    );

    let render = job.render();
    assert!(!render.status.success());
    let rendered = response(&render);
    assert!(has_diagnostic(&rendered, "PRESS_COMPOSITION_TEXT_OVERFLOW"));
    assert_eq!(rendered["diagnostics"][0]["severity"], "error");
}

#[test]
fn composition_objects_may_extend_beyond_the_surface_and_are_clipped() {
    let mut job = PreparedJob::new("generic-digital-pdf-v1");
    let text = &mut job.request["document"]["sections"][0]["chapters"][1]["pageCompositions"][0]["variants"]
        [0]["scene"]["objects"][1];
    text["bounds"]["xPercent"] = json!(-2);
    text["bounds"]["widthPercent"] = json!(34);
    job.write_request();

    let output = job.render();
    assert!(
        output.status.success(),
        "objects that overlap a page edge must be clipped, not rejected: stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    assert_eq!(response(&output)["status"], "completed");
}

#[test]
fn reading_copy_reports_pending_image_accessibility_without_blocking_the_pdf() {
    let mut job = PreparedJob::new("generic-digital-pdf-v1");
    job.request["outputPurpose"] = json!("reading-copy");
    let image = &mut job.request["document"]["sections"][0]["chapters"][1]["pageCompositions"][0]["variants"]
        [0]["scene"]["objects"][0];
    image["accessibilityDecisionPending"] = json!(true);
    image["altText"] = Value::Null;
    image["decorative"] = json!(false);
    job.request["cover"]["scene"] = json!({
        "schemaVersion": 1,
        "surface": {
            "kind": "SinglePage",
            "outputPageMode": "EditionLeaves",
            "widthPoints": 432.0,
            "heightPoints": 648.0,
            "bleedPoints": 0.0,
            "safeInsetPoints": 18.0,
            "allowIndependentPdfPage": false
        },
        "layers": [{ "id": "cover-layer", "name": "Cover", "order": 0 }],
        "objects": [{
            "id": "cover-art",
            "layerId": "cover-layer",
            "kind": "Image",
            "bounds": { "xPercent": 0, "yPercent": 0, "widthPercent": 100, "heightPercent": 100 },
            "imageId": "90000000-0000-0000-0000-000000000001",
            "imageFit": "Cover",
            "altText": null,
            "decorative": false,
            "accessibilityDecisionPending": true,
            "semanticRole": "Figure",
            "readingOrder": 1,
            "zIndex": 0
        }]
    });
    job.write_request();

    let output = job.render();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    let rendered = response(&output);
    assert_eq!(rendered["status"], "completed");
    let warnings = rendered["diagnostics"]
        .as_array()
        .expect("diagnostics")
        .iter()
        .filter(|diagnostic| diagnostic["code"] == "PRESS_ALT_DECISION_REQUIRED")
        .collect::<Vec<_>>();
    assert_eq!(warnings.len(), 2);
    assert!(
        warnings
            .iter()
            .all(|warning| warning["severity"] == "warning")
    );
    assert_eq!(
        rendered["artifacts"].as_array().expect("artifacts")[0]["kind"],
        "book-pdf"
    );
}

#[test]
fn reading_copy_purpose_is_rejected_for_publication_profiles() {
    let mut job = PreparedJob::new("kdp-paperback-v1");
    job.request["outputPurpose"] = json!("reading-copy");
    job.write_request();

    let output = job.render();
    assert!(!output.status.success());
    assert!(has_diagnostic(
        &response(&output),
        "PRESS_OUTPUT_PURPOSE_INVALID"
    ));
}

#[test]
fn browser_preview_layout_trace_omits_unused_glyph_payloads() {
    let mut job = PreparedJob::new("generic-digital-pdf-v1");
    job.request["layoutTraceMode"] = Value::String("browser-preview".to_owned());
    job.write_request();

    let trace = job.layout_trace();
    let runs = trace["pages"]
        .as_array()
        .unwrap()
        .iter()
        .flat_map(|page| page["lines"].as_array().into_iter().flatten())
        .flat_map(|line| line["runs"].as_array().into_iter().flatten())
        .collect::<Vec<_>>();
    assert!(!runs.is_empty());
    assert!(runs.iter().all(|run| run.get("glyphs").is_none()));
}

#[test]
fn browser_preview_reports_deterministic_utf16_source_ranges_across_wrapping_and_pages() {
    let mut job = PreparedJob::new("generic-digital-pdf-v1");
    job.request["layoutTraceMode"] = json!("browser-preview");
    job.request["trim"]["widthInches"] = json!(4.0);
    job.request["trim"]["heightInches"] = json!(5.0);
    job.request["trim"]["marginInches"] = json!(0.75);
    let source = "repeat 😀 repeat ".repeat(180);
    job.request["document"]["includeActHeadings"] = json!(false);
    job.request["document"]["includeChapterHeadings"] = json!(false);
    job.request["document"]["publicationSections"] = json!([]);
    job.request["document"]["sections"] = json!([{
        "id": "act", "title": "", "includePage": false, "includeHeading": false,
        "chapters": [{
            "id": "chapter", "title": "", "includeHeading": false,
            "blocks": [{
                "id": "utf16-block", "type": "Paragraph", "styleRole": "body",
                "content": [
                    { "type": "Text", "text": &source[..source.len() / 2], "marks": [{"type": "Strong"}] },
                    { "type": "Text", "text": &source[source.len() / 2..], "marks": [] }
                ]
            }]
        }]
    }]);
    job.write_request();

    let trace = job.layout_trace();
    let pages = trace["pages"].as_array().expect("pages");
    let lines = pages
        .iter()
        .flat_map(|page| page["lines"].as_array().into_iter().flatten())
        .filter(|line| line["semanticId"] == "utf16-block")
        .collect::<Vec<_>>();
    assert!(pages.len() > 1, "fixture must cross a page boundary");
    assert!(lines.len() > 2, "fixture must wrap into multiple lines");
    let source_utf16_len = source.encode_utf16().count() as u64;
    let mut previous_end = 0;
    for line in lines {
        let start = line["sourceStartUtf16"].as_u64().expect("source start");
        let end = line["sourceEndUtf16"].as_u64().expect("source end");
        assert!(start >= previous_end);
        assert!(end >= start);
        assert!(end <= source_utf16_len);
        previous_end = end;
    }
    assert!(previous_end > source_utf16_len / 2);
}

#[test]
fn emitted_pdf_text_matrices_match_harfrust_layout_positions() {
    let mut job = PreparedJob::new("kdp-paperback-v1");
    job.request["cover"] = Value::Null;
    job.request["document"]["includeActHeadings"] = Value::Bool(false);
    job.request["document"]["includeChapterHeadings"] = Value::Bool(false);
    job.request["document"]["publicationSections"] = json!([]);
    job.request["document"]["sections"] = json!([{
        "id": "act", "title": "", "includePage": false, "includeHeading": false,
        "chapters": [{
            "id": "chapter", "title": "", "includeHeading": false,
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
    let expected_spine = long_pages as f64 * 0.002252 * 72.0;
    let actual_spine = long_response["evidence"]["spineWidthPoints"]
        .as_f64()
        .unwrap();
    assert!((actual_spine - expected_spine).abs() < 0.001);
}

#[test]
fn cover_scene_region_constraints_reflow_surface_bounds_without_double_mapping() {
    let mut job = PreparedJob::new("kdp-paperback-v1");
    job.request["cover"]["title"] = json!("X");
    job.request["cover"]["scene"] = json!({
        "schemaVersion": 1,
        "surface": {
            "kind": "FacingSpread",
            "outputPageMode": "EditionLeaves",
            "widthPoints": 900.0,
            "heightPoints": 666.0,
            "bleedPoints": 9.0,
            "safeInsetPoints": 18.0,
            "allowIndependentPdfPage": false
        },
        "layers": [{ "id": "cover-layer", "name": "Cover", "order": 0 }],
        "objects": [{
            "id": "cover-title",
            "layerId": "cover-layer",
            "kind": "Text",
            "name": "Title",
            "bounds": { "xPercent": 60.0, "yPercent": 12.0, "widthPercent": 30.0, "heightPercent": 12.0 },
            "textBinding": "title",
            "fontFamilyKey": "builtin:nunito",
            "fontWeight": 700,
            "fontSizePoints": 28.0,
            "fillColor": "#ffffff",
            "regionConstraint": "Front",
            "semanticRole": "Heading1",
            "readingOrder": 1,
            "zIndex": 0
        }]
    });
    job.write_request();

    let output = job.render();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    let rendered = response(&output);
    let pdf = Document::load(job.artifact(&rendered, "cover-pdf")).expect("cover PDF");
    let page_id = *pdf.get_pages().values().next().expect("cover page");
    let width = inspect(&job.artifact(&rendered, "cover-pdf")).page_width;
    let operations = lopdf::content::Content::decode(&pdf.get_page_content(page_id))
        .expect("cover content")
        .operations;
    let text_x = operations
        .iter()
        .find(|operation| operation.operator == "Tm")
        .and_then(|operation| operation.operands.get(4))
        .map(number)
        .expect("title text matrix");

    let old_width = 900.0;
    let bleed = 9.0;
    let panel = 432.0;
    let old_spine = old_width - bleed * 2.0 - panel * 2.0;
    let local_x = (old_width * 0.60 - (bleed + panel + old_spine)) / panel;
    let new_spine = width - bleed * 2.0 - panel * 2.0;
    let expected_x = bleed + panel + new_spine + local_x * panel;
    assert!(
        (text_x - expected_x).abs() < 0.05,
        "region-bound surface coordinate was remapped twice: expected {expected_x}, got {text_x}"
    );
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
fn blank_optional_languages_inherit_the_document_language() {
    let mut job = PreparedJob::new("generic-digital-pdf-v1");
    job.request["document"]["sections"][0]["chapters"][0]["blocks"][0]["language"] =
        Value::String(String::new());
    job.request["document"]["sections"][0]["chapters"][0]["blocks"][1]["content"][0]["marks"] =
        json!([{ "type": "Language", "value": "" }]);
    job.write_request();

    let output = job.render();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    assert_eq!(response(&output)["status"], "completed");
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
        serde_json::from_slice(include_bytes!("../fixtures/negative-cases-v4.json"))
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

#[test]
fn kdp_hardcover_uses_case_laminate_geometry_from_the_resolved_product() {
    let mut job = PreparedJob::new("kdp-hardcover-v1");
    job.configure_physical((
        "kdp-hc-bw-white",
        "AmazonKdp",
        "Hardcover",
        "CaseLaminate",
        &["case-wrap"],
    ));
    let output = job.render();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    let response = response(&output);
    let pages = response["artifacts"].as_array().expect("artifacts");
    assert!(pages.iter().any(|item| item["kind"] == "case-cover-pdf"));
    let interior_pages = artifact_value(&response, "interior-pdf")["pageCount"]
        .as_u64()
        .expect("page count") as f64;
    let normalized = if (interior_pages as usize).is_multiple_of(2) {
        interior_pages
    } else {
        interior_pages + 1.0
    };
    let expected = (12.0 + normalized * 0.002252 + 1.02) * 72.0;
    let actual = response["evidence"]["coverWidthPoints"]
        .as_f64()
        .expect("cover width");
    assert!(
        (actual - expected).abs() < 0.01,
        "actual={actual} expected={expected}"
    );
}

#[test]
fn every_specific_frozen_spine_table_has_an_exact_even_page_measurement() {
    let registry: Value =
        serde_json::from_slice(include_bytes!("../assets/print-products-v1.json"))
            .expect("print registry");
    for product in registry["products"].as_array().expect("products") {
        if product["vendor"] == "Generic" || product["spineModel"]["kind"] != "FrozenLookup" {
            continue;
        }
        let minimum = product["minimumPages"].as_u64().expect("minimum") as usize;
        let maximum = product["maximumPages"].as_u64().expect("maximum") as usize;
        let anchors = product["spineModel"]["anchors"]
            .as_array()
            .expect("frozen anchors");
        let pages: Vec<_> = anchors
            .iter()
            .map(|anchor| anchor["pages"].as_u64().expect("anchor page") as usize)
            .collect();
        assert_eq!(pages.first(), Some(&minimum), "{}", product["key"]);
        assert_eq!(pages.last(), Some(&maximum), "{}", product["key"]);
        assert_eq!(
            pages.len(),
            (maximum - minimum) / 2 + 1,
            "{}",
            product["key"]
        );
        assert!(
            pages.windows(2).all(|pair| pair[1] == pair[0] + 2),
            "{} has a missing normalized page measurement",
            product["key"]
        );
        assert!(
            anchors
                .iter()
                .all(|anchor| anchor["inches"].as_f64().is_some_and(|value| value > 0.0))
        );
    }
}

#[test]
fn generic_print_requires_and_honors_complete_printer_declared_geometry() {
    let mut job = PreparedJob::new("generic-paperback-v1");
    job.configure_physical((
        "generic-perfectbound-template",
        "Generic",
        "Paperback",
        "PrintedCover",
        &["perfect-bound-outside"],
    ));
    job.request["physicalProduct"]["spineModel"] =
        json!({ "kind": "Caliper", "inchesPerPage": 0.0023, "anchors": [] });
    job.request["physicalProduct"]["genericTemplate"] = json!({
        "trimWidthInches": 6.0,
        "trimHeightInches": 9.0,
        "bleedInches": 0.125,
        "safeInches": 0.25,
        "wrapInches": 0.0,
        "hingeInches": 0.0,
        "gutterInches": 0.0,
        "flapInches": 0.0,
        "barcodeWidthInches": 2.0,
        "barcodeHeightInches": 1.2,
        "inchesPerPage": 0.0023,
        "minimumPages": 2,
        "maximumPages": 10000,
        "pdfStandard": "Printer-declared PDF 1.7"
    });
    job.write_request();
    let output = job.render();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );

    let mut mismatched = job.request.clone();
    mismatched["physicalProduct"]["genericTemplate"]["trimWidthInches"] = json!(5.5);
    fs::write(
        job.root.path().join("input/request.json"),
        serde_json::to_vec_pretty(&mismatched).expect("request JSON"),
    )
    .expect("write request");
    fs::remove_dir_all(job.root.path().join("output")).expect("remove prior immutable output");
    let rejected = job.render();
    assert!(!rejected.status.success());
    assert!(has_diagnostic(
        &response(&rejected),
        "PRESS_PRINT_PRODUCT_MISMATCH"
    ));
}

#[test]
fn ingram_duplex_cover_is_one_two_page_pdf_outside_then_blank_inside_by_default() {
    let mut job = PreparedJob::new("ingram-paperback-pdfx1a-v1");
    job.configure_physical((
        "ingram-pb-bw-white50",
        "IngramSpark",
        "Paperback",
        "PrintedCover",
        &["perfect-bound-outside", "perfect-bound-inside"],
    ));
    job.request["physicalProduct"]["coverMode"] = json!("Duplex");
    job.request["cover"]["barcodeMode"] = json!("LorekeeperBarcode");
    job.write_request();
    let output = job.render();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    let response = response(&output);
    assert_eq!(
        artifact_value(&response, "perfect-bound-cover-pdf")["pageCount"],
        2
    );
    let document =
        Document::load(job.artifact(&response, "perfect-bound-cover-pdf")).expect("cover PDF");
    let pages = document.get_pages();
    assert_eq!(pages.len(), 2);
    let inside_content = document.get_page_content(*pages.values().nth(1).expect("inside page"));
    let inside_operators = String::from_utf8_lossy(&inside_content);
    assert!(
        !inside_operators.contains(" Tf") && !inside_operators.contains(" Do"),
        "the default duplex inside surface must contain no text or image ink"
    );
}

#[test]
fn ingram_duplex_cover_renders_independent_inside_art_and_rejects_the_spine_no_ink_region() {
    let mut job = PreparedJob::new("ingram-paperback-pdfx1a-v1");
    job.configure_physical((
        "ingram-pb-bw-white50",
        "IngramSpark",
        "Paperback",
        "PrintedCover",
        &["perfect-bound-outside", "perfect-bound-inside"],
    ));
    job.request["physicalProduct"]["coverMode"] = json!("Duplex");
    job.request["cover"]["barcodeMode"] = json!("LorekeeperBarcode");
    job.request["cover"]["scenes"]["perfect-bound-inside"] = json!({
        "schemaVersion": 1,
        "surface": { "kind": "FacingSpread", "outputPageMode": "SingleSurface",
            "widthPoints": 900, "heightPoints": 666, "bleedPoints": 9,
            "safeInsetPoints": 36, "allowIndependentPdfPage": false },
        "layers": [{ "id": "91000000-0000-0000-0000-000000000001", "name": "Inside", "order": 0 }],
        "objects": [{ "id": "92000000-0000-0000-0000-000000000001",
            "layerId": "91000000-0000-0000-0000-000000000001", "kind": "Rectangle",
            "bounds": { "xPercent": 5, "yPercent": 10, "widthPercent": 35, "heightPercent": 80 },
            "fillColor": "#777777", "strokeColor": "transparent", "strokeWidthPoints": 0,
            "opacity": 1, "semanticRole": "Artifact", "zIndex": 0 }]
    });
    job.write_request();
    let output = job.render();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    let rendered = response(&output);
    let document =
        Document::load(job.artifact(&rendered, "perfect-bound-cover-pdf")).expect("cover PDF");
    let pages = document.get_pages();
    let inside_content = document.get_page_content(*pages.values().nth(1).expect("inside page"));
    assert!(
        String::from_utf8_lossy(&inside_content).contains(" re"),
        "the independent inside scene must be painted on cover page two"
    );

    let mut invalid = PreparedJob::new("ingram-paperback-pdfx1a-v1");
    invalid.configure_physical((
        "ingram-pb-bw-white50",
        "IngramSpark",
        "Paperback",
        "PrintedCover",
        &["perfect-bound-outside", "perfect-bound-inside"],
    ));
    invalid.request["physicalProduct"]["coverMode"] = json!("Duplex");
    invalid.request["cover"]["barcodeMode"] = json!("LorekeeperBarcode");
    invalid.request["cover"]["scenes"]["perfect-bound-inside"] = json!({
        "schemaVersion": 1,
        "surface": { "kind": "FacingSpread", "outputPageMode": "SingleSurface",
            "widthPoints": 900, "heightPoints": 666, "bleedPoints": 9,
            "safeInsetPoints": 36, "allowIndependentPdfPage": false },
        "layers": [{ "id": "93000000-0000-0000-0000-000000000001", "name": "Inside", "order": 0 }],
        "objects": [{ "id": "94000000-0000-0000-0000-000000000001",
            "layerId": "93000000-0000-0000-0000-000000000001", "kind": "Rectangle",
            "bounds": { "xPercent": 49, "yPercent": 10, "widthPercent": 2, "heightPercent": 80 },
            "fillColor": "#777777", "strokeColor": "transparent", "strokeWidthPoints": 0,
            "opacity": 1, "semanticRole": "Artifact", "zIndex": 0 }]
    });
    invalid.write_request();
    let invalid_output = invalid.render();
    assert!(!invalid_output.status.success());
    assert!(has_diagnostic(
        &response(&invalid_output),
        "PRESS_DUPLEX_INSIDE_SPINE_INK"
    ));
}

#[test]
fn ingram_jacketed_case_emits_independent_case_and_jacket_artifacts() {
    let mut job = PreparedJob::new("ingram-paperback-pdfx1a-v1");
    job.configure_physical((
        "ingram-hc-jacketed-case-premium70",
        "IngramSpark",
        "Hardcover",
        "JacketedCaseLaminate",
        &["case-wrap", "dust-jacket"],
    ));
    job.request["ink"] = json!("PremiumColor");
    job.write_request();
    let output = job.render();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    let response = response(&output);
    assert_eq!(artifact_value(&response, "case-cover-pdf")["pageCount"], 1);
    assert_eq!(artifact_value(&response, "dust-jacket-pdf")["pageCount"], 1);
    let case_width = inspect(&job.artifact(&response, "case-cover-pdf")).page_width;
    let jacket_width = inspect(&job.artifact(&response, "dust-jacket-pdf")).page_width;
    assert!(jacket_width > case_width);
    let spine_width = response["evidence"]["spineWidthPoints"]
        .as_f64()
        .expect("spine width evidence");
    let expected_jacket_width = 2.0 * (6.0 + 0.4375) * 72.0 + spine_width + 7.25 * 72.0;
    assert!((jacket_width - expected_jacket_width).abs() < 0.25);
}

#[test]
fn ingram_digital_cloth_without_jacket_emits_setup_manifest_not_cover_pdf() {
    let mut job = PreparedJob::new("ingram-paperback-pdfx1a-v1");
    job.configure_physical((
        "ingram-hc-cloth-blue",
        "IngramSpark",
        "Hardcover",
        "DigitalClothBlue",
        &["digital-cloth-setup"],
    ));
    let output = job.render();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        stderr(&output)
    );
    let response = response(&output);
    assert!(
        response["artifacts"]
            .as_array()
            .expect("artifacts")
            .iter()
            .any(|item| item["kind"] == "print-setup-manifest")
    );
    let setup_manifest = response["artifacts"]
        .as_array()
        .expect("artifacts")
        .iter()
        .find(|item| item["kind"] == "print-setup-manifest")
        .expect("cloth setup manifest");
    assert_eq!(setup_manifest["mediaType"], "application/json");
    assert!(
        !response["artifacts"]
            .as_array()
            .expect("artifacts")
            .iter()
            .any(|item| item["kind"]
                .as_str()
                .is_some_and(|kind| kind.ends_with("cover-pdf")))
    );
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
            serde_json::from_slice(include_bytes!("../fixtures/full-model-v7.json"))
                .expect("canonical request");
        request["protocolVersion"] = json!(7);
        let profile = match profile {
            "generic-paperback-v1" => "generic-print-v2",
            "kdp-paperback-v1" => "kdp-paperback-v2",
            "ingram-paperback-pdfx1a-v1" => "ingram-print-pdfx1a-v2",
            other => other,
        };
        request["profile"] = Value::String(profile.to_owned());
        if profile == "generic-digital-pdf-v1" {
            request["cover"]["barcodeMode"] = json!("None");
            request["physicalProduct"] = Value::Null;
            request["cover"]["surfaces"] = json!([]);
        } else {
            let registry: Value =
                serde_json::from_slice(include_bytes!("../assets/print-products-v1.json"))
                    .expect("print registry");
            let product_key = if profile == "ingram-print-pdfx1a-v2" {
                "ingram-pb-bw-white50"
            } else {
                "kdp-pb-bw-white"
            };
            let catalog = registry["products"]
                .as_array()
                .expect("products")
                .iter()
                .find(|item| item["key"] == product_key)
                .expect("catalog product");
            request["cover"]["surfaces"] = json!(["perfect-bound-outside"]);
            request["physicalProduct"] = json!({
                "registryVersion": "2026.08.1",
                "registrySha256": hex_hash(include_bytes!("../assets/print-products-v1.json")),
                "productKey": product_key,
                "vendor": catalog["vendor"],
                "format": catalog["format"],
                "binding": catalog["binding"],
                "interiorProcess": catalog["interiorProcess"],
                "paperName": catalog["paperName"],
                "basisWeightPounds": catalog["basisWeightPounds"],
                "gsm": catalog["gsm"],
                "coverMaterial": catalog["coverMaterial"],
                "finish": "Matte",
                "coverMode": "Simplex",
                "minimumPages": catalog["minimumPages"],
                "maximumPages": catalog["maximumPages"],
                "minimumSubmittedPages": catalog["minimumSubmittedPages"],
                "maximumSubmittedPages": catalog["maximumSubmittedPages"],
                "spineModel": catalog["spineModel"],
                "requiredCoverSurfaces": ["perfect-bound-outside"]
            });
        }
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

    fn configure_physical(&mut self, config: (&str, &str, &str, &str, &[&str])) {
        let (product_key, vendor, format, cover_material, surfaces) = config;
        let registry: Value =
            serde_json::from_slice(include_bytes!("../assets/print-products-v1.json"))
                .expect("print registry");
        let catalog = registry["products"]
            .as_array()
            .expect("products")
            .iter()
            .find(|item| item["key"] == product_key)
            .expect("catalog product");
        self.request["physicalProduct"] = json!({
            "registryVersion": "2026.08.1",
            "registrySha256": hex_hash(include_bytes!("../assets/print-products-v1.json")),
            "productKey": product_key,
            "vendor": vendor,
            "format": format,
            "binding": catalog["binding"],
            "interiorProcess": catalog["interiorProcess"],
            "paperName": catalog["paperName"],
            "basisWeightPounds": catalog["basisWeightPounds"],
            "gsm": catalog["gsm"],
            "coverMaterial": cover_material,
            "finish": catalog["finishes"][0],
            "coverMode": if surfaces.contains(&"perfect-bound-inside") { "Duplex" } else { "Simplex" },
            "minimumPages": catalog["minimumPages"],
            "maximumPages": catalog["maximumPages"],
            "minimumSubmittedPages": catalog["minimumSubmittedPages"],
            "maximumSubmittedPages": catalog["maximumSubmittedPages"],
            "spineModel": catalog["spineModel"],
            "requiredCoverSurfaces": surfaces,
        });
        self.request["cover"]["surfaces"] = json!(surfaces);
        self.write_request();
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
    let kind = if kind == "cover-pdf" {
        "perfect-bound-cover-pdf"
    } else {
        kind
    };
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

fn rgb_png(width: u32, height: u32, samples: &[u8]) -> Vec<u8> {
    let mut bytes = Vec::new();
    {
        let mut encoder = png::Encoder::new(&mut bytes, width, height);
        encoder.set_color(png::ColorType::Rgb);
        encoder.set_depth(png::BitDepth::Eight);
        let mut writer = encoder.write_header().expect("PNG header");
        writer.write_image_data(samples).expect("PNG samples");
    }
    bytes
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
    tagged: bool,
    document_language: bool,
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
    let tagged = catalog.has(b"StructTreeRoot") && catalog.has(b"MarkInfo");
    let document_language = catalog.has(b"Lang");
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
        tagged,
        document_language,
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
