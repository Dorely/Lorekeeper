using Lorekeeper.Ingest;

namespace Lorekeeper.Graph;

public sealed record ProjectGraphSnapshot(
    IReadOnlyList<ProjectGraphNode> Nodes,
    IReadOnlyList<ProjectGraphEdge> Edges,
    IReadOnlyList<ProjectGraphNodeType> NodeTypes,
    IReadOnlyList<string> EdgeTypes);

public sealed record ProjectGraphNode(
    long NodeId,
    string Type,
    string Key,
    string Label,
    IReadOnlyDictionary<string, string?> Properties,
    long? ParentNodeId,
    bool IsStructural,
    bool CanRename,
    bool CanEditProperties,
    bool AllowsCustomProperties,
    IReadOnlyList<string> EditablePropertyKeys,
    bool CanChangeParent,
    bool CanDelete,
    int Degree,
    string Color,
    string Summary,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<IngestWikiSection> WikiSections,
    IReadOnlyList<IngestCanonSource> CanonSources,
    bool IsIngestCreated,
    int CanonSourceCount);

public sealed record ProjectGraphEdge(
    long EdgeId,
    long FromNodeId,
    long ToNodeId,
    string EdgeType,
    IReadOnlyDictionary<string, string?> Properties,
    int? SortOrder,
    bool IsManaged,
    bool CanEdit,
    bool CanDelete,
    string Summary,
    IReadOnlyList<IngestWikiCitation> Citations,
    bool IsIngestCreated,
    int CanonSourceCount,
    bool IsAutoLink);

public sealed record ProjectGraphNodeType(
    string Type,
    string SingularLabel,
    string PluralLabel,
    bool IsStructural,
    bool IsChapterScoped,
    bool CanCreate,
    string Color,
    int SortOrder,
    IReadOnlyDictionary<string, string?> DefaultProperties);

public sealed record ProjectGraphNodeCreateRequest(
    string Type,
    string Name,
    IReadOnlyDictionary<string, string?>? Properties = null,
    long? ParentNodeId = null,
    string? NewTypeName = null);

public sealed record ProjectGraphNodeUpdateRequest(
    long NodeId,
    string? Label = null,
    IReadOnlyDictionary<string, string?>? Properties = null);

public sealed record ProjectGraphMoveParentRequest(
    long NodeId,
    long? ParentNodeId);

public sealed record ProjectGraphRelationshipCreateRequest(
    long FromNodeId,
    long ToNodeId,
    string EdgeType,
    IReadOnlyDictionary<string, string?>? Properties = null);

public sealed record ProjectGraphRelationshipUpdateRequest(
    long EdgeId,
    string EdgeType,
    IReadOnlyDictionary<string, string?>? Properties = null);
