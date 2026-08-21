using System.Text.Json;

namespace Lorekeeper.Authoring;

/// <summary>
/// Scans serialized authoring snapshots without depending on persistence or EF.
/// </summary>
internal static class AuthoringDependencyScanner
{
    public static IReadOnlySet<(AuthoringHistoryDependencyKind Kind, Guid ResourceId)> FindDependencies(
        params string[] snapshots)
    {
        var result = new HashSet<(AuthoringHistoryDependencyKind, Guid)>();
        foreach (var snapshot in snapshots.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            using var document = JsonDocument.Parse(snapshot);
            Visit(document.RootElement, null, result);
        }

        return result;
    }

    private static void Visit(
        JsonElement element,
        string? propertyName,
        HashSet<(AuthoringHistoryDependencyKind, Guid)> result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                Visit(property.Value, property.Name, result);
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                Visit(item, propertyName, result);
            return;
        }

        if (element.ValueKind != JsonValueKind.String)
            return;

        var value = element.GetString() ?? string.Empty;
        if (propertyName?.EndsWith("Json", StringComparison.OrdinalIgnoreCase) == true
            && value.Length > 1
            && value[0] is '{' or '[')
        {
            try
            {
                using var nestedDocument = JsonDocument.Parse(value);
                Visit(nestedDocument.RootElement, null, result);
            }
            catch (JsonException)
            {
                // Ordinary text may happen to begin with a brace. It is not a
                // dependency payload.
            }
        }

        if (string.Equals(propertyName, "fontFamilyKey", StringComparison.OrdinalIgnoreCase)
            && value.StartsWith("project:", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(value["project:".Length..], out var fontId))
            result.Add((AuthoringHistoryDependencyKind.ProjectFont, fontId));
        else if (string.Equals(propertyName, "imageId", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(value, out var imageId))
            result.Add((AuthoringHistoryDependencyKind.ProjectImage, imageId));
        else if ((string.Equals(propertyName, "pageCompositionId", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "compositionId", StringComparison.OrdinalIgnoreCase))
            && Guid.TryParse(value, out var compositionId))
            result.Add((AuthoringHistoryDependencyKind.PageComposition, compositionId));
    }
}
