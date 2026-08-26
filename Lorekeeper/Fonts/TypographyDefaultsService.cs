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
    private const string ResourceSuffix = "Typography.manuscript-typography-v1.json";

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
            Defaults.Blockquote,
            Defaults.ListItem,
            Defaults.SceneBreak,
            Defaults.Caption,
            Defaults.Inline,
            pageSetup.Revision);

    private static void Validate(TypographyDefaults defaults)
    {
        var blockDefaults = defaults.Headings.Values
            .Append(defaults.Body)
            .Append(defaults.ChapterHeading)
            .Append(defaults.Blockquote)
            .Append(defaults.Caption);
        if (defaults.SchemaVersion != 1
            || string.IsNullOrWhiteSpace(defaults.FontFamilyKeys.Serif)
            || string.IsNullOrWhiteSpace(defaults.FontFamilyKeys.Sans)
            || string.IsNullOrWhiteSpace(defaults.FontFamilyKeys.Mono)
            || defaults.Headings.Count != 6
            || Enumerable.Range(1, 6).Any(level => !defaults.Headings.ContainsKey(level.ToString()))
            || string.IsNullOrWhiteSpace(defaults.ListItem.Bullet)
            || string.IsNullOrWhiteSpace(defaults.SceneBreak.Text)
            || blockDefaults.Any(block => !IsValidTextAlign(block.TextAlign))
            || !IsValidTextAlign(defaults.ListItem.TextAlign)
            || !IsValidTextAlign(defaults.SceneBreak.TextAlign))
        {
            throw new InvalidDataException("The bundled manuscript typography defaults are incomplete.");
        }
    }

    private static bool IsValidTextAlign(string? value) => value is "left" or "right" or "center" or "justify";
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
    double? FirstLineIndentEm = null);

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
    TypographyInlineDefaults Inline,
    long PageSetupRevision);
