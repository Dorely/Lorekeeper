use std::collections::BTreeMap;
use std::io::Cursor;

use moxcms::{ColorProfile, Layout, TransformOptions};
use png::{ColorType, Transformations};

use crate::model::{Diagnostic, RenderRequest};

const ICC_PROFILE: &[u8] = include_bytes!("../assets/profiles/CGATS21_CRPC1.icc");

#[derive(Debug, Clone)]
pub struct EmbeddedImage {
    pub id: String,
    pub width: u32,
    pub height: u32,
    pub samples: Vec<u8>,
    pub cmyk: bool,
    pub grayscale: bool,
    pub maximum_total_ink_percent: f32,
}

pub fn prepare_images(
    request: &RenderRequest,
    validated_assets: &BTreeMap<String, Vec<u8>>,
    pdf_x: bool,
    black_and_white: bool,
) -> Result<BTreeMap<String, EmbeddedImage>, Diagnostic> {
    let mut result = BTreeMap::new();
    for declaration in &request.assets {
        let bytes = validated_assets.get(&declaration.id).ok_or_else(|| {
            Diagnostic::error(
                "PRESS_ASSET_READ_FAILED",
                "Validated asset bytes were unavailable.",
            )
        })?;
        let (width, height, rgb) = decode_png(&declaration.id, bytes)?;
        let (samples, cmyk, grayscale, maximum_total_ink_percent) = if black_and_white {
            let gray = rgb
                .chunks_exact(3)
                .map(|pixel| {
                    ((pixel[0] as u32 * 2126
                        + pixel[1] as u32 * 7152
                        + pixel[2] as u32 * 722
                        + 5000)
                        / 10000) as u8
                })
                .collect();
            (gray, false, true, 0.0)
        } else if pdf_x {
            let cmyk = convert_to_cmyk(&rgb)?;
            let maximum = cmyk
                .chunks_exact(4)
                .map(|pixel| {
                    pixel.iter().map(|channel| *channel as u32).sum::<u32>() as f32 * 100.0 / 255.0
                })
                .fold(0.0_f32, f32::max);
            if maximum > 240.001 {
                return Err(Diagnostic::error(
                    "PRESS_TOTAL_INK_EXCEEDED",
                    format!(
                        "Asset '{}' exceeds the 240% total-ink limit ({maximum:.1}%).",
                        declaration.id
                    ),
                ));
            }
            (cmyk, true, false, maximum)
        } else {
            (rgb, false, false, 0.0)
        };
        result.insert(
            declaration.id.clone(),
            EmbeddedImage {
                id: declaration.id.clone(),
                width,
                height,
                samples,
                cmyk,
                grayscale,
                maximum_total_ink_percent,
            },
        );
    }
    Ok(result)
}

fn decode_png(id: &str, bytes: &[u8]) -> Result<(u32, u32, Vec<u8>), Diagnostic> {
    let mut decoder = png::Decoder::new(Cursor::new(bytes));
    decoder.set_transformations(Transformations::EXPAND | Transformations::STRIP_16);
    let mut reader = decoder.read_info().map_err(|error| {
        Diagnostic::error(
            "PRESS_ASSET_CORRUPT",
            format!("Asset '{id}' is corrupt: {error}"),
        )
    })?;
    let mut buffer = vec![0; reader.output_buffer_size().unwrap_or(0)];
    let output = reader.next_frame(&mut buffer).map_err(|error| {
        Diagnostic::error(
            "PRESS_ASSET_CORRUPT",
            format!("Asset '{id}' is corrupt: {error}"),
        )
    })?;
    let source = &buffer[..output.buffer_size()];
    let mut rgb = Vec::with_capacity(output.width as usize * output.height as usize * 3);
    match output.color_type {
        ColorType::Rgb => rgb.extend_from_slice(source),
        ColorType::Grayscale => {
            for value in source {
                rgb.extend_from_slice(&[*value, *value, *value]);
            }
        }
        ColorType::Rgba => {
            for pixel in source.chunks_exact(4) {
                let alpha = pixel[3] as u16;
                for channel in &pixel[..3] {
                    rgb.push(((*channel as u16 * alpha + 255 * (255 - alpha) + 127) / 255) as u8);
                }
            }
        }
        ColorType::GrayscaleAlpha => {
            for pixel in source.chunks_exact(2) {
                let alpha = pixel[1] as u16;
                let value = ((pixel[0] as u16 * alpha + 255 * (255 - alpha) + 127) / 255) as u8;
                rgb.extend_from_slice(&[value, value, value]);
            }
        }
        ColorType::Indexed => {
            return Err(Diagnostic::error(
                "PRESS_ASSET_CORRUPT",
                format!("Asset '{id}' could not be expanded to pixels."),
            ));
        }
    }
    Ok((output.width, output.height, rgb))
}

fn convert_to_cmyk(rgb: &[u8]) -> Result<Vec<u8>, Diagnostic> {
    let source = ColorProfile::new_srgb();
    let destination = ColorProfile::new_from_slice(ICC_PROFILE).map_err(|error| {
        Diagnostic::error(
            "PRESS_ICC_INVALID",
            format!("The bundled CMYK profile is invalid: {error}"),
        )
    })?;
    let transform = source
        .create_transform_8bit(
            Layout::Rgb,
            &destination,
            Layout::Rgba,
            TransformOptions::default(),
        )
        .map_err(|error| Diagnostic::error("PRESS_ICC_TRANSFORM_FAILED", error.to_string()))?;
    // moxcms documents CMYK8 as the four-channel Rgba layout; with a CMYK
    // destination profile these bytes are C, M, Y, and K, not RGB plus alpha.
    let mut converted = vec![0; rgb.len() / 3 * 4];
    transform
        .transform(rgb, &mut converted)
        .map_err(|error| Diagnostic::error("PRESS_ICC_TRANSFORM_FAILED", error.to_string()))?;
    let mut cmyk = Vec::with_capacity(rgb.len() / 3 * 4);
    for pixel in converted.chunks_exact(4) {
        let sum = pixel.iter().map(|channel| *channel as u32).sum::<u32>();
        if sum <= 612 {
            cmyk.extend_from_slice(pixel);
            continue;
        }
        let scale = 612.0 / sum as f32;
        cmyk.extend(
            pixel
                .iter()
                .map(|channel| (*channel as f32 * scale).round() as u8),
        );
    }
    Ok(cmyk)
}
