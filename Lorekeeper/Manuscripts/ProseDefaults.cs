namespace Lorekeeper.Manuscripts;

public static class ProseDefaults
{
    public const string DefaultBodyFontFamilyKey = "builtin:lora";
    public const double DefaultBodyFontSizePoints = 12;
    public const double DefaultBodyLineHeight = 1.55;
    public const double DefaultSpacingBeforePoints = 0;
    public const double DefaultSpacingAfterPoints = 8;
    public const string DefaultAlignment = "left";

    public static ParagraphAlignment DefaultParagraphAlignment => ParagraphAlignment.Start;
}
