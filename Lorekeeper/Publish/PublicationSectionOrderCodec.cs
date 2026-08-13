using System.Text.Json;

namespace Lorekeeper.Publish;

public static class PublicationSectionOrderCodec
{
    public static Dictionary<Guid, int> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<Guid, int>>(json) ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The publication section order is malformed.", exception);
        }
    }

    public static string Serialize(IReadOnlyDictionary<Guid, int> order) =>
        JsonSerializer.Serialize(order.OrderBy(item => item.Key).ToDictionary(item => item.Key, item => item.Value));
}
