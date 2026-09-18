using System.ComponentModel;

namespace Lorekeeper.Manuscripts;

public sealed record ManuscriptOperationInput(
    [property: Description("Canonical operation name. Use InsertBlock only for genuinely additive content; it never replaces or removes an existing block. Use ReplaceBlockText to revise an existing block while preserving its stable ID. Multi-block rewrites must replace or delete every superseded source block in the same batch before inserting any additional replacement blocks.")]
    string Operation,
    [property: Description("Exact stable block ID from the current manuscript. Required for every operation except InsertBlock; supply a new unique ID for InsertBlock only when another operation in the same batch must target that inserted block.")]
    string? BlockId = null,
    [property: Description("Exact second stable block ID. Required only by MergeBlocks and must identify the adjacent block immediately after blockId.")]
    string? SecondBlockId = null,
    [property: Description("Zero-based document position. InsertBlock adds a new block at this position without removing anything; MoveBlock relocates the existing block to this position.")]
    int? Index = null,
    [property: Description("Semantic block type used by InsertBlock or SetBlockType: Paragraph, Heading, SceneBreak, BlockQuote, ListItem, Figure, or DesignedPage where the owning tool permits it.")]
    string? BlockType = null,
    [property: Description("Exact complete block text. For ReplaceBlockText this replaces the selected block's text while preserving its ID; for InsertBlock this is net-new text and leaves every existing block untouched.")]
    string? Text = null,
    [property: Description("Semantic style role used by InsertBlock, SetBlockType, or SetBlockStyle. Use the exact current or saved style role; do not use this field as a substitute for a Book Text Style ID where a focused style tool is available.")]
    string? StyleRole = null,
    [property: Description("Zero-based UTF-16 offset used by SplitBlock or as the inclusive start of a SetInlineMark range. It must be a valid boundary in the current exact block text.")]
    int? StartOffset = null,
    [property: Description("Zero-based UTF-16 exclusive end offset used only by SetInlineMark. It must be a valid boundary in the current exact block text.")]
    int? EndOffset = null,
    [property: Description("Inline mark name used only by SetInlineMark.")]
    string? Mark = null,
    [property: Description("Whether SetInlineMark adds/enables the mark; false removes it from the exact range.")]
    bool? Enabled = null,
    [property: Description("Optional value used by valued inline marks such as Link, Language, or CharacterStyle.")]
    string? Value = null,
    [property: Description("Existing project image ID used only when inserting or converting a Figure through a tool that permits Figure mutations.")]
    Guid? ImageId = null,
    [property: Description("Alternative text for a meaningful Figure. Purely decorative Figures must use the focused Figure contract's decorative decision and carry no alternative text.")]
    string? AltText = null,
    [property: Description("Heading level 1-6 used only by Heading insertions or type changes; it is independent from styleRole.")]
    int? HeadingLevel = null,
    [property: Description("Direct paragraph formatting used only by SetParagraphPresentation. To format a new block in one batch, give InsertBlock an explicit blockId and follow it with SetParagraphPresentation for that ID.")]
    ParagraphPresentation? ParagraphPresentation = null,
    [property: Description("Exact full position from positionsUtf16 plus manuscriptId for ReplaceInlineContent. Offset must be zero; retain the complete document/table/cell or notes path.")]
    ManuscriptPosition? Position = null,
    [property: Description("Complete inline content of one existing leaf block for ReplaceInlineContent. Preserve unrelated text, marks, note references, and citation atoms with their stable IDs. Citation items use bibliography IDs read from the project bibliography; source locations are optional verified evidence. At most 1024 inlines and 65536 text characters.")]
    IReadOnlyList<ManuscriptInline>? InlineContent = null)
{
    public const string ToolOperationGuidance =
        "Canonical operations are InsertBlock, ReplaceBlockText, ReplaceInlineContent, DeleteBlock, MoveBlock, SplitBlock, MergeBlocks, SetBlockType, SetBlockStyle, SetInlineMark, and SetParagraphPresentation. " +
        "Use ReplaceInlineContent at an exact full position to insert, edit, or remove citation atoms or edit nested table/note text. Retain every unrelated inline atom, mark, and stable ID. Plain text replacement/split/merge/mark operations reject blocks containing citation or note atoms to prevent losing them. " +
        "InsertBlock is additive: it creates a new block and never replaces or removes existing prose. Use ReplaceBlockText as the default for revising one existing block because it preserves that block's stable ID. " +
        "For a multi-block rewrite, account for every source block in the intended range: retain it deliberately, replace its text, or delete it in the same atomic batch; insert only genuinely additional replacement blocks and never append a rewritten section while leaving its superseded source in place. " +
        "Use SetBlockStyle—not SetStyleRole—to apply a Book Text Style. For direct formatting on a new block, give InsertBlock an explicit blockId and follow it in the same batch with SetParagraphPresentation for that ID.";

    public static IReadOnlyList<ManuscriptOperation> ToOperations(
        IReadOnlyList<ManuscriptOperationInput> operations) =>
        operations.Select<ManuscriptOperationInput, ManuscriptOperation>(
            operation => operation.Operation.Trim().ToLowerInvariant() switch
            {
                "insertblock" => InsertBlock(operation),
                "replaceblocktext" => new ReplaceManuscriptBlockText(
                    Required(operation.BlockId, "blockId"),
                    operation.Text ?? string.Empty),
                "replaceinlinecontent" => ReplaceInlineContent(operation),
                "deleteblock" => new DeleteManuscriptBlock(Required(operation.BlockId, "blockId")),
                "moveblock" => new MoveManuscriptBlock(
                    Required(operation.BlockId, "blockId"),
                    Required(operation.Index, "index")),
                "splitblock" => new SplitManuscriptBlock(
                    Required(operation.BlockId, "blockId"),
                    Required(operation.StartOffset, "startOffset")),
                "mergeblocks" => new MergeManuscriptBlocks(
                    Required(operation.BlockId, "blockId"),
                    Required(operation.SecondBlockId, "secondBlockId")),
                "setblocktype" => SetBlockType(operation),
                "setblockstyle" => new SetManuscriptBlockStyle(
                    Required(operation.BlockId, "blockId"),
                    NormalizeStyleRole(Required(operation.StyleRole, "styleRole"))!),
                "setinlinemark" => new SetManuscriptInlineMark(
                    Required(operation.BlockId, "blockId"),
                    Required(operation.StartOffset, "startOffset"),
                    Required(operation.EndOffset, "endOffset"),
                    ParseEnum<ManuscriptMarkType>(operation.Mark, "mark"),
                    operation.Enabled ?? true,
                    operation.Value),
                "setparagraphpresentation" => new SetParagraphPresentation(
                    Required(operation.BlockId, "blockId"),
                    operation.ParagraphPresentation),
                _ => throw new ArgumentException(
                    $"Unsupported manuscript operation '{operation.Operation}'. {ToolOperationGuidance}"),
            })
            .ToList();

    private static ReplaceManuscriptInlineContent ReplaceInlineContent(ManuscriptOperationInput operation)
    {
        var position = operation.Position ?? throw new ArgumentException("position is required.");
        var content = operation.InlineContent ?? throw new ArgumentException("inlineContent is required.");
        if (position.Offset != 0 || content.Count > 1024 || content.Sum(inline => (long)inline.Text.Length) > 65536)
            throw new ArgumentException("ReplaceInlineContent requires offset zero and at most 1024 inlines / 65536 text characters.");
        return new(position, content);
    }

    private static InsertManuscriptBlock InsertBlock(ManuscriptOperationInput operation)
    {
        var blockType = ParseEnum<ManuscriptBlockType>(operation.BlockType, "blockType");
        return new InsertManuscriptBlock(
            Required(operation.Index, "index"),
            blockType,
            NormalizeBlockText(blockType, operation.Text),
            NormalizeStyleRole(operation.StyleRole),
            operation.ImageId,
            operation.AltText,
            operation.HeadingLevel,
            BlockId: operation.BlockId);
    }

    private static SetManuscriptBlockType SetBlockType(ManuscriptOperationInput operation)
    {
        var blockType = ParseEnum<ManuscriptBlockType>(operation.BlockType, "blockType");
        return new SetManuscriptBlockType(
            Required(operation.BlockId, "blockId"),
            blockType,
            NormalizeStyleRole(operation.StyleRole),
            operation.ImageId,
            operation.AltText,
            operation.HeadingLevel);
    }

    private static string NormalizeBlockText(ManuscriptBlockType blockType, string? text)
    {
        var value = text ?? string.Empty;
        return blockType == ManuscriptBlockType.SceneBreak
            && (string.IsNullOrWhiteSpace(value)
                || string.Equals(value.Trim(), "***", StringComparison.Ordinal))
            ? string.Empty
            : value;
    }

    private static string? NormalizeStyleRole(string? styleRole)
    {
        if (string.IsNullOrWhiteSpace(styleRole))
            return styleRole;

        var trimmed = styleRole.Trim();
        if (ManuscriptSemanticRoles.IsValid(trimmed))
            return trimmed;

        return trimmed.ToLowerInvariant() switch
        {
            "body" => ManuscriptStyleRoles.Body,
            "heading" => ManuscriptStyleRoles.Heading,
            "chapterheading" or "chapter-heading" => ManuscriptStyleRoles.ChapterHeading,
            "subheading" => ManuscriptStyleRoles.Subheading,
            "scenebreak" or "scene-break" => ManuscriptStyleRoles.SceneBreak,
            "blockquote" or "block-quote" => ManuscriptStyleRoles.BlockQuote,
            "listitem" or "list-item" => ManuscriptStyleRoles.ListItem,
            "figurecaption" or "figure-caption" => ManuscriptStyleRoles.FigureCaption,
            _ => trimmed,
        };
    }

    private static T Required<T>(T? value, string name) where T : struct =>
        value ?? throw new ArgumentException($"{name} is required.");

    private static string Required(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"{name} is required.");

    private static T ParseEnum<T>(string? value, string name) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new ArgumentException($"{name} must be one of: {string.Join(", ", Enum.GetNames<T>())}.");
}
