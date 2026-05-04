using Lorekeeper.Models;

namespace Lorekeeper.Outline;

public interface IEntityTypeService
{
    Task<IReadOnlyList<EntityTypeDefinition>> ListAsync(
        Guid projectId,
        bool includeStructural = false,
        CancellationToken cancellationToken = default);

    Task<EntityTypeDefinition> CreateAsync(
        Guid projectId,
        string labelOrType,
        bool isStructural = false,
        bool isChapterScoped = false,
        CancellationToken cancellationToken = default);

    Task EnsureDefaultsAsync(Guid projectId, CancellationToken cancellationToken = default);
}

public sealed record EntityTypeDefinition(
    string Type,
    string SingularLabel,
    string PluralLabel,
    bool IsStructural,
    bool IsChapterScoped,
    int SortOrder,
    IReadOnlyDictionary<string, string?> DefaultProperties);