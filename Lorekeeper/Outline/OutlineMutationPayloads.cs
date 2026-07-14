namespace Lorekeeper.Outline;

/// <summary>Small model-facing acknowledgements for outline/entity mutations.</summary>
public static class OutlineMutationPayloads
{
    public static object Entity(
        Guid id,
        string type,
        string name,
        int? order,
        Guid? parentId,
        IReadOnlyDictionary<string, string?>? properties = null) => new
        {
            id,
            type,
            name,
            order,
            parentId,
            properties = properties ?? new Dictionary<string, string?>(),
        };

    public static object Endpoint(Guid id, string type, string name) => new
    {
        id,
        type,
        name,
    };
}
