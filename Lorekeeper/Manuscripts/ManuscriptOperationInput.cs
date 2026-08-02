namespace Lorekeeper.Manuscripts;

public sealed record ManuscriptOperationInput(
    string Operation,
    string? BlockId = null,
    string? SecondBlockId = null,
    int? Index = null,
    string? BlockType = null,
    string? Text = null,
    string? StyleRole = null,
    int? StartOffset = null,
    int? EndOffset = null,
    string? Mark = null,
    bool? Enabled = null,
    string? Value = null,
    Guid? ImageId = null,
    string? AltText = null,
    int? HeadingLevel = null,
    ParagraphPresentation? ParagraphPresentation = null)
{
    public static IReadOnlyList<ManuscriptOperation> ToOperations(
        IReadOnlyList<ManuscriptOperationInput> operations) =>
        operations.Select<ManuscriptOperationInput, ManuscriptOperation>(
            operation => operation.Operation.Trim().ToLowerInvariant() switch
            {
                "insertblock" => InsertBlock(operation),
                "replaceblocktext" => new ReplaceManuscriptBlockText(
                    Required(operation.BlockId, "blockId"),
                    operation.Text ?? string.Empty),
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
                _ => throw new ArgumentException($"Unsupported manuscript operation '{operation.Operation}'."),
            })
            .ToList();

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
            operation.HeadingLevel);
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
