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
    string? Value = null)
{
    public static IReadOnlyList<ManuscriptOperation> ToOperations(
        IReadOnlyList<ManuscriptOperationInput> operations) =>
        operations.Select<ManuscriptOperationInput, ManuscriptOperation>(
            operation => operation.Operation.Trim().ToLowerInvariant() switch
            {
                "insertblock" => new InsertManuscriptBlock(
                    Required(operation.Index, "index"),
                    ParseEnum<ManuscriptBlockType>(operation.BlockType, "blockType"),
                    operation.Text ?? string.Empty,
                    operation.StyleRole),
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
                "setblockstyle" => new SetManuscriptBlockStyle(
                    Required(operation.BlockId, "blockId"),
                    Required(operation.StyleRole, "styleRole")),
                "setinlinemark" => new SetManuscriptInlineMark(
                    Required(operation.BlockId, "blockId"),
                    Required(operation.StartOffset, "startOffset"),
                    Required(operation.EndOffset, "endOffset"),
                    ParseEnum<ManuscriptMarkType>(operation.Mark, "mark"),
                    operation.Enabled ?? true,
                    operation.Value),
                _ => throw new ArgumentException($"Unsupported manuscript operation '{operation.Operation}'."),
            })
            .ToList();

    private static T Required<T>(T? value, string name) where T : struct =>
        value ?? throw new ArgumentException($"{name} is required.");

    private static string Required(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"{name} is required.");

    private static T ParseEnum<T>(string? value, string name) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentException($"{name} must be one of: {string.Join(", ", Enum.GetNames<T>())}.");
}
