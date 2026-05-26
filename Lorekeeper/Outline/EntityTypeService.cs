using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Outline;

public sealed class EntityTypeService(
    IGraphEntityTypeRepository entityTypes,
    IGraphNodeRepository nodes) : IEntityTypeService
{
    public const string ProjectNodeType = "Project";
    public const string ActNodeType = "Act";
    public const string ChapterNodeType = "Chapter";
    public const string EventNodeType = "Event";
    public const string ProjectFactNodeType = "ProjectFact";
    public const string SourceNodeType = "Source";
    public const string SourceChunkNodeType = "SourceChunk";
    public const string SourceBlockNodeType = "SourceBlock";

    private static readonly EntityTypeSeed[] Defaults =
    [
        new(ProjectNodeType, "Project", "Projects", true, false, -400),
        new(ActNodeType, "Act", "Acts", true, false, -300,
            DefaultProperties: new Dictionary<string, object?> { ["synopsis"] = string.Empty }),
        new(ChapterNodeType, "Chapter", "Chapters", true, false, -200,
            DefaultProperties: new Dictionary<string, object?> { ["synopsis"] = string.Empty }),
        new(ProjectFactNodeType, "Project fact", "Project facts", true, false, -150,
            DefaultProperties: new Dictionary<string, object?> { ["key"] = string.Empty, ["value"] = string.Empty }),
        new(SourceNodeType, "Source", "Sources", true, false, -140,
            DefaultProperties: new Dictionary<string, object?> { ["kind"] = string.Empty, ["description"] = string.Empty }),
        new(SourceChunkNodeType, "Source chunk", "Source chunks", true, false, -130,
            DefaultProperties: new Dictionary<string, object?> { ["summary"] = string.Empty, ["notes"] = string.Empty }),
        new(SourceBlockNodeType, "Source block", "Source blocks", true, false, -120,
            DefaultProperties: new Dictionary<string, object?> { ["locator"] = string.Empty, ["kind"] = string.Empty }),
        new(EventNodeType, "Beat", "Beats", true, true, -100,
            DefaultProperties: new Dictionary<string, object?> { ["summary"] = string.Empty }),
        new("Character", "Character", "Characters", false, false, 100,
            DefaultProperties: new Dictionary<string, object?> { ["role"] = string.Empty, ["description"] = string.Empty }),
        new("Location", "Location", "Locations", false, false, 200,
            DefaultProperties: new Dictionary<string, object?> { ["description"] = string.Empty }),
    ];

    public async Task<IReadOnlyList<EntityTypeDefinition>> ListAsync(
        Guid projectId,
        bool includeStructural = false,
        CancellationToken cancellationToken = default)
    {
        await EnsureDefaultsAsync(projectId, cancellationToken);
        await EnsureDiscoveredTypesAsync(projectId, cancellationToken);

        var list = await entityTypes.ListByProjectAsync(projectId, cancellationToken);
        return list
            .Where(t => includeStructural || !t.IsStructural)
            .Select(Project)
            .ToList();
    }

    public async Task<EntityTypeDefinition> CreateAsync(
        Guid projectId,
        string labelOrType,
        bool isStructural = false,
        bool isChapterScoped = false,
        CancellationToken cancellationToken = default)
    {
        var type = NormalizeTypeKey(labelOrType);
        var existing = await entityTypes.FindAsync(projectId, type, cancellationToken);
        if (existing is not null)
            return Project(existing);

        var singular = HumanizeType(type);
        var entityType = new GraphEntityType
        {
            ProjectId = projectId,
            Type = type,
            SingularLabel = singular,
            PluralLabel = Pluralize(singular),
            IsStructural = isStructural,
            IsChapterScoped = isChapterScoped,
            SortOrder = await NextSortOrderAsync(projectId, cancellationToken),
        };

        await entityTypes.AddAsync(entityType, cancellationToken);
        await entityTypes.SaveChangesAsync(cancellationToken);
        return Project(entityType);
    }

    public async Task EnsureDefaultsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        foreach (var seed in Defaults)
        {
            var existing = await entityTypes.FindAsync(projectId, seed.Type, cancellationToken);
            if (existing is null)
            {
                await entityTypes.AddAsync(new GraphEntityType
                {
                    ProjectId = projectId,
                    Type = seed.Type,
                    SingularLabel = seed.SingularLabel,
                    PluralLabel = seed.PluralLabel,
                    IsStructural = seed.IsStructural,
                    IsChapterScoped = seed.IsChapterScoped,
                    SortOrder = seed.SortOrder,
                    DefaultProperties = new Dictionary<string, object?>(seed.DefaultProperties),
                }, cancellationToken);
            }
        }

        await entityTypes.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureDiscoveredTypesAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var registered = (await entityTypes.ListByProjectAsync(projectId, cancellationToken))
            .Select(t => t.Type)
            .ToHashSet(StringComparer.Ordinal);
        var discovered = await nodes.ListTypesAsync(projectId, cancellationToken);

        foreach (var type in discovered)
        {
            if (registered.Contains(type)) continue;

            var defaultSeed = Defaults.FirstOrDefault(d => d.Type == type);
            if (defaultSeed is not null)
            {
                await entityTypes.AddAsync(new GraphEntityType
                {
                    ProjectId = projectId,
                    Type = defaultSeed.Type,
                    SingularLabel = defaultSeed.SingularLabel,
                    PluralLabel = defaultSeed.PluralLabel,
                    IsStructural = defaultSeed.IsStructural,
                    IsChapterScoped = defaultSeed.IsChapterScoped,
                    SortOrder = defaultSeed.SortOrder,
                    DefaultProperties = new Dictionary<string, object?>(defaultSeed.DefaultProperties),
                }, cancellationToken);
                continue;
            }

            var singular = HumanizeType(type);
            await entityTypes.AddAsync(new GraphEntityType
            {
                ProjectId = projectId,
                Type = type,
                SingularLabel = singular,
                PluralLabel = Pluralize(singular),
                SortOrder = await NextSortOrderAsync(projectId, cancellationToken),
            }, cancellationToken);
        }

        await entityTypes.SaveChangesAsync(cancellationToken);
    }

    private async Task<int> NextSortOrderAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var list = await entityTypes.ListByProjectAsync(projectId, cancellationToken);
        var nonStructural = list.Where(t => !t.IsStructural).ToList();
        return nonStructural.Count == 0 ? 100 : nonStructural.Max(t => t.SortOrder) + 100;
    }

    private static EntityTypeDefinition Project(GraphEntityType entityType)
    {
        var defaults = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in entityType.DefaultProperties)
            defaults[kv.Key] = kv.Value?.ToString();

        return new EntityTypeDefinition(
            entityType.Type,
            entityType.SingularLabel,
            entityType.PluralLabel,
            entityType.IsStructural,
            entityType.IsChapterScoped,
            entityType.SortOrder,
            defaults);
    }

    private static string NormalizeTypeKey(string input)
    {
        var parts = new List<string>();
        var current = new List<char>();
        foreach (var ch in (input ?? string.Empty).Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Add(ch);
            }
            else if (current.Count > 0)
            {
                parts.Add(new string(current.ToArray()));
                current.Clear();
            }
        }

        if (current.Count > 0)
            parts.Add(new string(current.ToArray()));
        if (parts.Count == 0)
            throw new ArgumentException("Entity type is required.", nameof(input));

        return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static string HumanizeType(string type)
    {
        if (string.IsNullOrWhiteSpace(type)) return "Entity";
        var chars = new List<char> { type[0] };
        for (var i = 1; i < type.Length; i++)
        {
            var ch = type[i];
            if (char.IsUpper(ch) && !char.IsWhiteSpace(type[i - 1]))
                chars.Add(' ');
            chars.Add(ch);
        }
        return new string(chars.ToArray());
    }

    private static string Pluralize(string singular)
    {
        if (singular.EndsWith("y", StringComparison.OrdinalIgnoreCase) && singular.Length > 1)
            return singular[..^1] + "ies";
        if (singular.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            return singular;
        return singular + "s";
    }

    private sealed record EntityTypeSeed(
        string Type,
        string SingularLabel,
        string PluralLabel,
        bool IsStructural,
        bool IsChapterScoped,
        int SortOrder,
        IReadOnlyDictionary<string, object?> DefaultProperties)
    {
        public EntityTypeSeed(
            string type,
            string singularLabel,
            string pluralLabel,
            bool isStructural,
            bool isChapterScoped,
            int sortOrder)
            : this(type, singularLabel, pluralLabel, isStructural, isChapterScoped, sortOrder, new Dictionary<string, object?>())
        {
        }
    }
}
