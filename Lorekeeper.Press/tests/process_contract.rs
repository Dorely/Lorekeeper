use std::io::Write;
use std::process::{Command, Output, Stdio};

use lorekeeper_press::protocol::{RenderResponse, RenderStatus};
use tempfile::TempDir;

#[test]
fn malformed_json_uses_the_complete_response_contract() {
    let output_root = TempDir::new().expect("output root");
    let output = run_process(
        &[
            "--output-root",
            output_root.path().to_str().expect("UTF-8 path"),
        ],
        b"{not-json",
    );

    assert!(!output.status.success());
    let response = parse_response(&output);
    assert_eq!(response.status, RenderStatus::Failed);
    assert_eq!(response.protocol_version, 1);
    assert_eq!(response.renderer_version, "0.1.0");
    assert!(response.job_id.is_empty());
    assert!(response.artifacts.is_empty());
    assert_eq!(response.diagnostics[0].code, "PRESS_ENVELOPE_INVALID");
}

#[test]
fn missing_arguments_use_the_complete_response_contract() {
    let output = run_process(&[], b"{}");

    assert!(!output.status.success());
    let response = parse_response(&output);
    assert_eq!(response.status, RenderStatus::Failed);
    assert!(response.evidence.claimed_standard.is_none());
    assert_eq!(response.diagnostics[0].code, "PRESS_ENVELOPE_INVALID");
}

#[test]
fn unknown_request_fields_are_rejected_at_the_process_boundary() {
    let output_root = TempDir::new().expect("output root");
    let input = include_bytes!("../fixtures/representative.json");
    let mut value: serde_json::Value = serde_json::from_slice(input).expect("fixture JSON");
    value["unexpected"] = serde_json::Value::Bool(true);
    let input = serde_json::to_vec(&value).expect("modified JSON");

    let output = run_process(
        &[
            "--output-root",
            output_root.path().to_str().expect("UTF-8 path"),
        ],
        &input,
    );

    assert!(!output.status.success());
    let response = parse_response(&output);
    assert_eq!(response.status, RenderStatus::Failed);
    assert!(response.diagnostics[0].message.contains("unknown field"));
}

fn run_process(arguments: &[&str], input: &[u8]) -> Output {
    let mut child = Command::new(env!("CARGO_BIN_EXE_lorekeeper-press"))
        .args(arguments)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .expect("start press process");
    child
        .stdin
        .take()
        .expect("child stdin")
        .write_all(input)
        .expect("write request");
    child.wait_with_output().expect("process output")
}

fn parse_response(output: &Output) -> RenderResponse {
    serde_json::from_slice(&output.stdout).unwrap_or_else(|error| {
        panic!(
            "response did not match RenderResponse: {error}; stdout={}; stderr={}",
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        )
    })
}
