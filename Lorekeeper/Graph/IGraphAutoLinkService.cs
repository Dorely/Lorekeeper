namespace Lorekeeper.Graph;

public interface IGraphAutoLinkService
{
    Task<IReadOnlyList<GraphAutoMentionLink>> RefreshEntityAsync(
        Guid projectId,
        Guid entityId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GraphAutoMentionLink>> RefreshSourceAsync(
        Guid projectId,
        string sourceType,
        Guid sourceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GraphAutoMentionLink>> ListEntityAutoMentionLinksAsync(
        Guid projectId,
        Guid entityId,
        int maxResults = 12,
        CancellationToken cancellationToken = default);
}

public sealed record GraphAutoMentionLink(
    long EdgeId,
    string Direction,
    Guid SourceId,
    string SourceType,
    string SourceName,
    Guid TargetEntityId,
    string TargetEntityType,
    string TargetEntityName,
    string MatchedLabel,
    string EvidenceExcerpt,
    double Confidence);
