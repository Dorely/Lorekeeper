use std::env;
use std::io::{self, Read};
use std::path::PathBuf;
use std::process::ExitCode;

use lorekeeper_press::protocol::RenderResponse;
use lorekeeper_press::render::render;

const MAX_REQUEST_BYTES: u64 = 5 * 1024 * 1024;

fn main() -> ExitCode {
    match run() {
        Ok(response) => {
            if serde_json::to_writer_pretty(io::stdout(), &response).is_err() {
                eprintln!("Failed to write the response.");
                return ExitCode::FAILURE;
            }
            ExitCode::SUCCESS
        }
        Err(message) => {
            let response = RenderResponse::process_failure("PRESS_ENVELOPE_INVALID", message);
            let _ = serde_json::to_writer_pretty(io::stdout(), &response);
            ExitCode::FAILURE
        }
    }
}

fn run() -> Result<RenderResponse, String> {
    let output_root = parse_output_root()?;
    let mut input = Vec::new();
    io::stdin()
        .take(MAX_REQUEST_BYTES + 1)
        .read_to_end(&mut input)
        .map_err(|error| error.to_string())?;
    if input.len() as u64 > MAX_REQUEST_BYTES {
        return Err(format!(
            "The request exceeds the {MAX_REQUEST_BYTES}-byte process limit."
        ));
    }
    let input = String::from_utf8(input).map_err(|error| error.to_string())?;
    let request = serde_json::from_str(&input).map_err(|error| error.to_string())?;
    Ok(render(request, &output_root))
}

fn parse_output_root() -> Result<PathBuf, String> {
    let mut arguments = env::args_os().skip(1);
    match (
        arguments.next().and_then(|value| value.into_string().ok()),
        arguments.next(),
        arguments.next(),
    ) {
        (Some(flag), Some(path), None) if flag == "--output-root" => Ok(PathBuf::from(path)),
        _ => Err("Usage: lorekeeper-press --output-root <directory>".to_owned()),
    }
}
