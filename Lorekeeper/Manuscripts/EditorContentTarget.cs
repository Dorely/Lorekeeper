namespace Lorekeeper.Manuscripts;

public enum EditorContentTargetKind
{
    Core,
    Edition,
}

public readonly record struct EditorContentTarget
{
    private EditorContentTarget(EditorContentTargetKind kind, Guid? editionId)
    {
        Kind = kind;
        EditionId = editionId;
    }

    public EditorContentTargetKind Kind { get; }
    public Guid? EditionId { get; }
    public bool IsCore => Kind == EditorContentTargetKind.Core;
    public string StorageKey => IsCore ? "core" : $"edition:{EditionId:N}";

    public static EditorContentTarget Core => new(EditorContentTargetKind.Core, null);

    public static EditorContentTarget ForEdition(Guid editionId)
    {
        if (editionId == Guid.Empty)
            throw new ArgumentException("An edition content target requires an edition ID.", nameof(editionId));
        return new EditorContentTarget(EditorContentTargetKind.Edition, editionId);
    }

    public static EditorContentTarget From(EditorContentTargetKind kind, Guid? editionId) => kind switch
    {
        EditorContentTargetKind.Core when editionId is null => Core,
        EditorContentTargetKind.Edition when editionId is Guid id && id != Guid.Empty => ForEdition(id),
        _ => throw new ArgumentException("The editor content target is invalid."),
    };
}
