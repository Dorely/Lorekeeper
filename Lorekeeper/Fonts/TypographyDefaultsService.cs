using System.Text.Json;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;

namespace Lorekeeper.Fonts;

public interface ITypographyDefaultsService
{
    TypographyDefaults Defaults { get; }
    EditorTypographyConfiguration CreateEditorConfiguration(ProjectPageSetup pageSetup);
}

public sealed class TypographyDefaultsService : ITypographyDefaultsService
{
    private const string ResourceSuffix = "Typography.manuscript-typography-v2.json";

    public TypographyDefaultsService()
    {
        var assembly = typeof(TypographyDefaultsService).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The bundled manuscript typography defaults are missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var data = buffer.ToArray();
        Defaults = JsonSerializer.Deserialize<TypographyDefaults>(data, ManuscriptCodec.JsonOptions)
            ?? throw new InvalidDataException("The bundled manuscript typography defaults are invalid.");
        Validate(Defaults);
    }

    public TypographyDefaults Defaults { get; }

    public EditorTypographyConfiguration CreateEditorConfiguration(ProjectPageSetup pageSetup) =>
        new(
            Defaults.SchemaVersion,
            Defaults.FontFamilyKeys,
            Defaults.Body with
            {
                FontSizePoints = pageSetup.BodyFontSizePoints,
                LineHeight = pageSetup.BodyLineHeight,
            },
            Defaults.ChapterHeading,
            Defaults.Headings,
            Defaults.Blockquote with
            {
                FontSizePoints = pageSetup.BodyFontSizePoints,
                LineHeight = pageSetup.BodyLineHeight,
            },
            Defaults.ListItem,
            Defaults.SceneBreak,
            Defaults.Caption,
            Defaults.Figure,
            Defaults.Inline,
            pageSetup.Revision,
            TextAreaHeightToWidth(pageSetup));

    // The editor sizes Figure frames against its own line width, so it needs the page text
    // area's shape rather than its physical size.
    private static double TextAreaHeightToWidth(ProjectPageSetup pageSetup)
    {
        var width = pageSetup.PageWidthInches - pageSetup.PageMarginInches * 2;
        var height = pageSetup.PageHeightInches - pageSetup.PageMarginInches * 2;
        return width > 0 && height > 0 ? height / width : 1;
    }

    private static void Validate(TypographyDefaults defaults)
    {
        var blockDefaults = defaults.Headings.Values
            .Append(defaults.Body)
            .Append(defaults.ChapterHeading)
            .Append(defaults.Blockquote)
            .Append(defaults.Caption);
        if (defaults.SchemaVersion != 2
            || string.IsNullOrWhiteSpace(defaults.FontFamilyKeys.Serif)
            || string.IsNullOrWhiteSpace(defaults.FontFamilyKeys.Sans)
            || string.IsNullOrWhiteSpace(defaults.FontFamilyKeys.Mono)
            || defaults.Headings.Count != 6
            || Enumerable.Range(1, 6).Any(level => !defaults.Headings.ContainsKey(level.ToString()))
            || string.IsNullOrWhiteSpace(defaults.ListItem.Bullet)
            || string.IsNullOrWhiteSpace(defaults.SceneBreak.Text)
            || defaults.Blockquote.Decoration is not { RuleWidthEm: > 0, RuleGapEm: >= 0 } blockquoteDecoration
            || !IsValidRgb(defaults.Blockquote.TextColorRgb)
            || !IsValidRgb(blockquoteDecoration.RuleColorRgb)
            || defaults.Caption.Overlay is not { BackgroundOpacity: >= 0 and <= 1, PaddingVerticalEm: >= 0, PaddingHorizontalEm: >= 0 } captionOverlay
            || !IsValidRgb(defaults.Caption.TextColorRgb)
            || !IsValidRgb(captionOverlay.BackgroundColorRgb)
            || !IsValidRgb(captionOverlay.TextColorRgb)
            || blockDefaults.Any(block => !IsValidTextAlign(block.TextAlign))
            || !IsValidTextAlign(defaults.ListItem.TextAlign)
            || !IsValidTextAlign(defaults.SceneBreak.TextAlign)
            || defaults.Figure is not
            {
                MinimumWidthPercent: > 0 and <= 100,
                FlowFrameMaxTextAreaHeightFraction: > 0 and <= 1,
                FlowFrameMaxHeightToWidth: > 0,
                DedicatedFrameTextAreaHeightFraction: > 0 and <= 1,
                WrapGutterPoints: >= 0,
                MinimumWrapSideFraction: > 0 and < 1,
            })
        {
            throw new InvalidDataException("The bundled manuscript typography defaults are incomplete.");
        }
    }

    private static bool IsValidTextAlign(string? value) => value is "left" or "right" or "center" or "justify";

    private static bool IsValidRgb(double[]? value) =>
        value is { Length: 3 } && value.All(channel => channel is >= 0 and <= 1);
}

public sealed record TypographyDefaults(
    int SchemaVersion,
    TypographyFontFamilyKeys FontFamilyKeys,
    TypographyBlockDefaults Body,
    TypographyBlockDefaults ChapterHeading,
    IReadOnlyDictionary<string, TypographyBlockDefaults> Headings,
    TypographyBlockDefaults Blockquote,
    TypographyListDefaults ListItem,
    TypographySceneBreakDefaults SceneBreak,
    TypographyBlockDefaults Caption,
    TypographyFigureDefaults Figure,
    TypographyInlineDefaults Inline);

public sealed record TypographyFontFamilyKeys(
    string Serif,
    string Sans,
    string Mono);

public sealed record TypographyBlockDefaults(
    string FontFamilyKey,
    double FontSizePoints,
    double LineHeight,
    int FontWeight,
    bool Italic,
    string TextAlign,
    double SpaceBeforePoints,
    double SpaceAfterPoints,
    double? LeftIndentEm = null,
    double? RightIndentEm = null,
    double? FirstLineIndentEm = null,
    double[]? TextColorRgb = null,
    TypographyBlockquoteDecoration? Decoration = null,
    TypographyCaptionOverlayDefaults? Overlay = null);

public sealed record TypographyBlockquoteDecoration(
    double RuleWidthEm,
    double RuleGapEm,
    double[] RuleColorRgb);

public sealed record TypographyCaptionOverlayDefaults(
    double[] BackgroundColorRgb,
    double BackgroundOpacity,
    double PaddingVerticalEm,
    double PaddingHorizontalEm,
    double[] TextColorRgb);

public sealed record TypographyListDefaults(
    string Bullet,
    double LeftIndentEm,
    double HangingIndentEm,
    string TextAlign,
    double SpaceBeforePoints,
    double SpaceAfterPoints);

public sealed record TypographySceneBreakDefaults(
    string Text,
    string TextAlign,
    double SpaceBeforePoints,
    double SpaceAfterPoints);

public sealed record TypographyFigureDefaults(
    double MinimumWidthPercent,
    double FlowFrameMaxTextAreaHeightFraction,
    double FlowFrameMaxHeightToWidth,
    double DedicatedFrameTextAreaHeightFraction,
    double WrapGutterPoints,
    double MinimumWrapSideFraction);

public sealed record TypographyInlineDefaults(
    TypographyInlineMarkDefaults Superscript,
    TypographyInlineMarkDefaults Subscript,
    TypographySmallCapsDefaults SmallCaps);

public sealed record TypographyInlineMarkDefaults(
    double SizeScale,
    double BaselineShiftEm);

public sealed record TypographySmallCapsDefaults(
    double LowercaseScale);

public sealed record EditorTypographyConfiguration(
    int SchemaVersion,
    TypographyFontFamilyKeys FontFamilyKeys,
    TypographyBlockDefaults Body,
    TypographyBlockDefaults ChapterHeading,
    IReadOnlyDictionary<string, TypographyBlockDefaults> Headings,
    TypographyBlockDefaults Blockquote,
    TypographyListDefaults ListItem,
    TypographySceneBreakDefaults SceneBreak,
    TypographyBlockDefaults Caption,
    TypographyFigureDefaults Figure,
    TypographyInlineDefaults Inline,
    long PageSetupRevision,
    double TextAreaHeightToWidth);
