use std::collections::BTreeMap;
use std::io::Cursor;

use moxcms::{ColorProfile, Layout, TransformOptions};
use png::{ColorType, Transformations};
use zune_core::bytestream::ZCursor;
use zune_core::colorspace::ColorSpace;
use zune_core::options::DecoderOptions;
use zune_jpeg::JpegDecoder;

use crate::model::{Diagnostic, RenderRequest};

const ICC_PROFILE: &[u8] = include_bytes!("../assets/profiles/CGATS21_CRPC1.icc");

#[derive(Debug, Clone)]
pub struct EmbeddedImage {
    pub id: String,
    pub width: u32,
    pub height: u32,
    pub samples: Vec<u8>,
    pub alpha: Option<Vec<u8>>,
    pub cmyk: bool,
    pub grayscale: bool,
    pub maximum_total_ink_percent: f32,
}

#[derive(Debug)]
pub struct DecodedImage {
    pub width: u32,
    pub height: u32,
    pub rgb: Vec<u8>,
    pub alpha: Option<Vec<u8>>,
}

struct DecodedPng {
    width: u32,
    height: u32,
    rgb: Vec<u8>,
    alpha: Option<Vec<u8>>,
}

pub fn prepare_images<F>(
    request: &RenderRequest,
    decoded_assets: &BTreeMap<String, DecodedImage>,
    selected_ids: &std::collections::BTreeSet<String>,
    pdf_x: bool,
    black_and_white: bool,
    mut progress: F,
) -> Result<BTreeMap<String, EmbeddedImage>, Diagnostic>
where
    F: FnMut(usize, usize),
{
    let mut result = BTreeMap::new();
    let declarations = request
        .assets
        .iter()
        .filter(|declaration| selected_ids.contains(&declaration.id))
        .collect::<Vec<_>>();
    let total = declarations.len();
    for (index, declaration) in declarations.into_iter().enumerate() {
        let decoded = decoded_assets.get(&declaration.id).ok_or_else(|| {
            Diagnostic::error(
                "PRESS_ASSET_READ_FAILED",
                "Validated image pixels were unavailable.",
            )
        })?;
        let preserve_alpha = matches!(
            request.profile.as_str(),
            "kdp-paperback-v2"
                | "kdp-hardcover-v1"
                | "ingram-print-pdfx1a-v2"
                | "bn-print-pdfa1b-v1"
                | "lulu-print-v1"
        );
        let (source_rgb, alpha) = if preserve_alpha {
            (decoded.rgb.clone(), decoded.alpha.clone())
        } else {
            (
                flatten_rgb_alpha_against_white(&decoded.rgb, decoded.alpha.as_deref()),
                None,
            )
        };
        let (samples, cmyk, grayscale, maximum_total_ink_percent) = if black_and_white {
            let gray = source_rgb
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
            let cmyk = convert_to_cmyk(&source_rgb)?;
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
            (source_rgb, false, false, 0.0)
        };
        result.insert(
            declaration.id.clone(),
            EmbeddedImage {
                id: declaration.id.clone(),
                width: decoded.width,
                height: decoded.height,
                samples,
                alpha,
                cmyk,
                grayscale,
                maximum_total_ink_percent,
            },
        );
        progress(index + 1, total);
    }
    Ok(result)
}

fn decode_jpeg(id: &str, bytes: &[u8]) -> Result<(u32, u32, Vec<u8>), Diagnostic> {
    let options = DecoderOptions::default()
        .set_strict_mode(true)
        .set_max_width(16_000)
        .set_max_height(16_000)
        .jpeg_set_out_colorspace(ColorSpace::RGB);
    let mut decoder = JpegDecoder::new_with_options(ZCursor::new(bytes), options);
    let pixels = decoder.decode().map_err(|error| {
        Diagnostic::error(
            "PRESS_ASSET_CORRUPT",
            format!("Asset '{id}' is corrupt: {error}"),
        )
    })?;
    let (width, height) = decoder.dimensions().ok_or_else(|| {
        Diagnostic::error(
            "PRESS_ASSET_CORRUPT",
            format!("Asset '{id}' has no dimensions."),
        )
    })?;
    let width = u32::try_from(width).map_err(|_| {
        Diagnostic::error("PRESS_ASSET_LIMIT", "JPEG width exceeds renderer limits.")
    })?;
    let height = u32::try_from(height).map_err(|_| {
        Diagnostic::error("PRESS_ASSET_LIMIT", "JPEG height exceeds renderer limits.")
    })?;
    if pixels.len() != width as usize * height as usize * 3 {
        return Err(Diagnostic::error(
            "PRESS_ASSET_CORRUPT",
            format!("Asset '{id}' did not decode to RGB pixels."),
        ));
    }
    Ok((width, height, pixels))
}

pub fn decode_declared_image(
    declaration: &crate::model::AssetDeclaration,
    bytes: &[u8],
) -> Result<DecodedImage, Diagnostic> {
    let (width, height, rgb, alpha) = match declaration.media_type.as_str() {
        "image/png" => {
            let decoded = decode_png(&declaration.id, bytes)?;
            (decoded.width, decoded.height, decoded.rgb, decoded.alpha)
        }
        "image/jpeg" => {
            let (width, height, rgb) = decode_jpeg(&declaration.id, bytes)?;
            (width, height, rgb, None)
        }
        _ => {
            return Err(Diagnostic::error(
                "PRESS_ASSET_FORMAT_UNSUPPORTED",
                format!("Asset '{}' must be PNG or JPEG.", declaration.id),
            ));
        }
    };
    if declaration.width_pixels != Some(width) || declaration.height_pixels != Some(height) {
        return Err(Diagnostic::error(
            "PRESS_ASSET_DIMENSION_MISMATCH",
            format!(
                "Asset '{}' dimensions do not match its declaration.",
                declaration.id
            ),
        ));
    }
    Ok(DecodedImage {
        width,
        height,
        rgb,
        alpha,
    })
}

fn decode_png(id: &str, bytes: &[u8]) -> Result<DecodedPng, Diagnostic> {
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
    let mut alpha = None;
    match output.color_type {
        ColorType::Rgb => rgb.extend_from_slice(source),
        ColorType::Grayscale => {
            for value in source {
                rgb.extend_from_slice(&[*value, *value, *value]);
            }
        }
        ColorType::Rgba => {
            let mut values = Vec::with_capacity(output.width as usize * output.height as usize);
            for pixel in source.chunks_exact(4) {
                rgb.extend_from_slice(&pixel[..3]);
                values.push(pixel[3]);
            }
            alpha = values.iter().any(|value| *value < 255).then_some(values);
        }
        ColorType::GrayscaleAlpha => {
            let mut values = Vec::with_capacity(output.width as usize * output.height as usize);
            for pixel in source.chunks_exact(2) {
                rgb.extend_from_slice(&[pixel[0], pixel[0], pixel[0]]);
                values.push(pixel[1]);
            }
            alpha = values.iter().any(|value| *value < 255).then_some(values);
        }
        ColorType::Indexed => {
            return Err(Diagnostic::error(
                "PRESS_ASSET_CORRUPT",
                format!("Asset '{id}' could not be expanded to pixels."),
            ));
        }
    }
    Ok(DecodedPng {
        width: output.width,
        height: output.height,
        rgb,
        alpha,
    })
}

fn flatten_rgb_alpha_against_white(rgb: &[u8], alpha: Option<&[u8]>) -> Vec<u8> {
    let Some(alpha) = alpha else {
        return rgb.to_vec();
    };
    let mut flattened = Vec::with_capacity(rgb.len());
    for (pixel, alpha) in rgb.chunks_exact(3).zip(alpha) {
        let alpha = u16::from(*alpha);
        for channel in pixel {
            flattened.push(((u16::from(*channel) * alpha + 255 * (255 - alpha) + 127) / 255) as u8);
        }
    }
    flattened
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
