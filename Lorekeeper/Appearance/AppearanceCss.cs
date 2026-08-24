using System.Globalization;

namespace Lorekeeper.Appearance;

/// <summary>
/// Builds the custom-palette stylesheet layered after app.css. Only overridden
/// core tokens are emitted; dependent shades (hover, tints, borders, focus
/// ring, translucency, and the Bootstrap bridge variables) are derived with
/// CSS <c>color-mix</c> so the whole token system stays cohesive. Every color
/// is validated as strict <c>#rrggbb</c> hex before it can reach the markup.
/// </summary>
public static class AppearanceCss
{
    // The built-in core-token values, mirrored from wwwroot/app.css so color
    // pickers can display the effective default. Keep in sync when the base
    // palettes in app.css change.
    public static readonly AppearancePalette LightDefaults = new(
        Accent: "#425fce",
        Canvas: "#f6f8fb",
        Surface: "#ffffff",
        Ink: "#172033",
        Muted: "#647086",
        Line: "#dfe4ec");

    public static readonly AppearancePalette DarkDefaults = new(
        Accent: "#7d96f0",
        Canvas: "#14181f",
        Surface: "#1f2631",
        Ink: "#e6eaf2",
        Muted: "#9aa4b5",
        Line: "#2a3242");

    public static bool IsValidHexColor(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(char.IsAsciiHexDigit);

    public static void Validate(AppearanceThemeState state)
    {
        foreach (var color in EnumerateColors(state.Light).Concat(EnumerateColors(state.Dark)))
        {
            if (color is not null && !IsValidHexColor(color))
                throw new ArgumentException($"'{color}' is not a #rrggbb hex color.");
        }
    }

    public static string? Build(AppearanceThemeState state)
    {
        Validate(state);
        var light = BuildScope(":root", state.Light, isDarkTheme: false);
        var dark = BuildScope("html[data-lk-theme=\"dark\"]", state.Dark, isDarkTheme: true);
        if (light is null && dark is null)
            return null;
        return string.Join('\n', new[] { light, dark }.Where(scope => scope is not null));
    }

    private static string? BuildScope(string selector, AppearancePalette palette, bool isDarkTheme)
    {
        if (!palette.HasOverrides)
            return null;

        var declarations = new List<string>();
        if (palette.Ink is { } ink)
        {
            declarations.Add($"--lk-ink: {ink}");
            declarations.Add($"--bs-body-color: {ink}");
            declarations.Add($"--bs-body-color-rgb: {Rgb(ink)}");
        }

        if (palette.Canvas is { } canvas)
        {
            declarations.Add($"--lk-canvas: {canvas}");
            declarations.Add($"--lk-soft: {canvas}");
            declarations.Add($"--lk-canvas-inset: {canvas}");
            declarations.Add($"--lk-surface-subtle: {canvas}");
            declarations.Add($"--lk-inset: color-mix(in srgb, {canvas} 92%, var(--lk-ink))");
            declarations.Add($"--lk-progress-track: color-mix(in srgb, {canvas} 90%, var(--lk-ink))");
            declarations.Add($"--bs-tertiary-bg: {canvas}");
        }

        if (palette.Surface is { } surface)
        {
            declarations.Add($"--lk-surface: {surface}");
            declarations.Add($"--lk-surface-translucent: color-mix(in srgb, {surface} 96%, transparent)");
            declarations.Add($"--bs-body-bg: {surface}");
            declarations.Add($"--bs-body-bg-rgb: {Rgb(surface)}");
        }

        if (palette.Muted is { } muted)
        {
            declarations.Add($"--lk-muted: {muted}");
            declarations.Add($"--lk-text-muted: {muted}");
            declarations.Add($"--bs-secondary-color: {muted}");
        }

        if (palette.Line is { } line)
        {
            declarations.Add($"--lk-line: {line}");
            declarations.Add($"--lk-line-strong: color-mix(in srgb, {line} 78%, var(--lk-ink))");
            declarations.Add($"--lk-border-strong: color-mix(in srgb, {line} 78%, var(--lk-ink))");
            declarations.Add($"--bs-border-color: {line}");
        }

        if (palette.Accent is { } accent)
        {
            // Hover moves toward white on the dark theme and toward black on the
            // light theme, matching the built-in palettes' direction.
            var hoverMix = isDarkTheme ? "white" : "black";
            declarations.Add($"--lk-accent: {accent}");
            declarations.Add($"--lk-accent-hover: color-mix(in srgb, {accent} 85%, {hoverMix})");
            declarations.Add($"--lk-accent-soft: color-mix(in srgb, {accent} 16%, var(--lk-canvas))");
            declarations.Add($"--lk-accent-border: color-mix(in srgb, {accent} 45%, var(--lk-canvas))");
            declarations.Add($"--lk-focus: 0 0 0 0.2rem color-mix(in srgb, {accent} 20%, transparent)");
            declarations.Add($"--bs-primary: {accent}");
            declarations.Add($"--bs-primary-rgb: {Rgb(accent)}");
            declarations.Add($"--bs-link-color: {accent}");
            declarations.Add("--bs-link-hover-color: var(--lk-accent-hover)");
        }

        return $"{selector} {{\n    {string.Join(";\n    ", declarations)};\n}}";
    }

    private static string Rgb(string hexColor)
    {
        var red = int.Parse(hexColor.AsSpan(1, 2), NumberStyles.HexNumber);
        var green = int.Parse(hexColor.AsSpan(3, 2), NumberStyles.HexNumber);
        var blue = int.Parse(hexColor.AsSpan(5, 2), NumberStyles.HexNumber);
        return $"{red}, {green}, {blue}";
    }

    private static IEnumerable<string?> EnumerateColors(AppearancePalette palette) =>
        [palette.Accent, palette.Canvas, palette.Surface, palette.Ink, palette.Muted, palette.Line];
}
