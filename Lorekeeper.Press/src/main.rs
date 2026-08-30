use std::env;
use std::path::PathBuf;

use lorekeeper_press::model::{Capabilities, Diagnostic, RenderResponse};
use lorekeeper_press::renderer;
use serde_json::json;
use sha2::{Digest, Sha256};

fn main() {
    let registry_hash = Sha256::digest(include_bytes!("../assets/print-artifact-profiles-v1.json"))
        .iter()
        .map(|value| format!("{value:02x}"))
        .collect::<String>();
    let arguments: Vec<String> = env::args().skip(1).collect();
    let result = match arguments.as_slice() {
        [command, flag] if command == "describe" && flag == "--json" => {
            println!(
                "{}",
                serde_json::to_string(&json!({
                    "protocolVersion": 10,
                    "rendererVersion": env!("CARGO_PKG_VERSION"),
                    "printArtifactProfileRegistryVersion": "2026.08.3",
                    "printArtifactProfileRegistrySha256": registry_hash,
                    "profiles": [
                        "generic-print-v2",
                        "generic-digital-pdf-v1",
                        "ingram-print-pdfx1a-v2",
                        "kdp-paperback-v2",
                        "kdp-hardcover-v1",
                        "bn-print-pdfa1b-v1"
                    ],
                    "machineRuntimeDependencies": [],
                    "limits": {
                        "maximumAssets": 512,
                        "maximumAssetBytes": 268435456u64,
                        "maximumJobBytes": 1073741824u64,
                        "maximumPages": 10000
                    },
                    "capabilities": Capabilities::all()
                }))
                .expect("serialize capability contract")
            );
            Ok::<(), Box<RenderResponse>>(())
        }
        [command, root_flag, root] if command == "render" && root_flag == "--job-root" => {
            renderer::run(&PathBuf::from(root))
        }
        [command, root_flag, root] if command == "layout" && root_flag == "--job-root" => {
            renderer::trace(&PathBuf::from(root))
        }
        _ => Err(Box::new(RenderResponse::failed(
            "rejected",
            Diagnostic::error(
                "PRESS_PROTOCOL_INVALID",
                "Use 'describe --json', 'layout --job-root <bounded-job-directory>', or 'render --job-root <bounded-job-directory>'.",
            ),
        ))),
    };

    if let Err(response) = result {
        println!(
            "{}",
            serde_json::to_string(&response).expect("serialize failure response")
        );
        std::process::exit(2);
    }
}
