use std::collections::BTreeMap;
use std::sync::OnceLock;

use serde::Deserialize;

const DEFAULTS_JSON: &str = include_str!("../assets/manuscript-typography-v2.json");

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TypographyDefaults {
    pub schema_version: u32,
    pub font_family_keys: FontFamilyKeys,
    pub body: BlockDefaults,
    pub chapter_heading: BlockDefaults,
    pub headings: BTreeMap<String, BlockDefaults>,
    pub blockquote: BlockDefaults,
    pub list_item: ListItemDefaults,
    pub scene_break: SceneBreakDefaults,
    pub caption: BlockDefaults,
    pub inline: InlineDefaults,
}

#[derive(Debug, Deserialize)]
pub struct FontFamilyKeys {
    pub serif: String,
    pub sans: String,
    pub mono: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BlockDefaults {
    pub font_family_key: String,
    pub font_size_points: f32,
    pub line_height: f32,
    pub font_weight: u16,
    pub italic: bool,
    pub text_align: String,
    pub space_before_points: f32,
    pub space_after_points: f32,
    #[serde(default)]
    pub left_indent_em: f32,
    #[serde(default)]
    pub text_color_rgb: Option<[f32; 3]>,
    #[serde(default)]
    pub decoration: Option<BlockquoteDecoration>,
    #[serde(default)]
    pub overlay: Option<CaptionOverlayDefaults>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BlockquoteDecoration {
    pub rule_width_em: f32,
    pub rule_gap_em: f32,
    pub rule_color_rgb: [f32; 3],
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CaptionOverlayDefaults {
    pub background_color_rgb: [f32; 3],
    pub background_opacity: f32,
    pub padding_vertical_em: f32,
    pub padding_horizontal_em: f32,
    pub text_color_rgb: [f32; 3],
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ListItemDefaults {
    pub bullet: String,
    pub left_indent_em: f32,
    pub hanging_indent_em: f32,
    pub text_align: String,
    pub space_before_points: f32,
    pub space_after_points: f32,
}

#[derive(Debug, Deserialize)]
pub struct SceneBreakDefaults {
    pub text: String,
    #[serde(rename = "textAlign")]
    pub text_align: String,
    #[serde(rename = "spaceBeforePoints")]
    pub space_before_points: f32,
    #[serde(rename = "spaceAfterPoints")]
    pub space_after_points: f32,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InlineDefaults {
    pub superscript: InlineMarkDefaults,
    pub subscript: InlineMarkDefaults,
    pub small_caps: SmallCapsDefaults,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InlineMarkDefaults {
    pub size_scale: f32,
    pub baseline_shift_em: f32,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SmallCapsDefaults {
    pub lowercase_scale: f32,
}

pub fn defaults() -> &'static TypographyDefaults {
    static DEFAULTS: OnceLock<TypographyDefaults> = OnceLock::new();
    DEFAULTS.get_or_init(|| {
        let defaults: TypographyDefaults =
            serde_json::from_str(DEFAULTS_JSON).expect("valid manuscript typography defaults");
        assert_eq!(defaults.schema_version, 2);
        defaults
    })
}

pub fn heading(level: u8) -> &'static BlockDefaults {
    defaults()
        .headings
        .get(&level.to_string())
        .unwrap_or(&defaults().chapter_heading)
}
