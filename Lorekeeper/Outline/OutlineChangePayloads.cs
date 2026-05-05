namespace Lorekeeper.Outline;

public sealed record OutlineActChange(Guid Id, int Order, string Title, string Synopsis);

public sealed record OutlineChapterChange(Guid Id, Guid? ActId, int Order, string Title, string Synopsis);

public sealed record OutlineEntityChange(
    Guid Id,
    string Type,
    string Name,
    int? Order,
    Guid? ParentId,
    Dictionary<string, string?> Properties);

public sealed record OutlineEntityLinkChange(
    Guid FromId,
    Guid ToId,
    string EdgeType,
    Dictionary<string, string?> Properties);

public sealed record OutlineReorderChange(Guid? ParentId, List<Guid> OrderedIds);

public sealed record OutlineEntityReorderChange(string Type, Guid ParentId, List<Guid> OrderedIds);
