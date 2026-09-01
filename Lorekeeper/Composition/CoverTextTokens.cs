using Lorekeeper.Models;

namespace Lorekeeper.Composition;

public sealed record CoverTextTokenDefinition(string Key, string Token, string Label);

public static class CoverTextTokens
{
    public static IReadOnlyList<CoverTextTokenDefinition> Definitions { get; } =
    [
        new("title", "{{title}}", "Title"),
        new("subtitle", "{{subtitle}}", "Subtitle"),
        new("author", "{{author}}", "Author"),
        new("spineText", "{{spineText}}", "Spine text"),
        new("description", "{{description}}", "Description"),
    ];

    private static readonly IReadOnlySet<string> Keys = Definitions
        .Select(item => item.Key)
        .ToHashSet(StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, string> Bindings(
        string title,
        string subtitle,
        string author,
        string spineText,
        string description) => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["title"] = title,
            ["subtitle"] = subtitle,
            ["author"] = author,
            ["spineText"] = spineText,
            ["description"] = description,
        };

    public static string Resolve(
        string? template,
        IReadOnlyDictionary<string, string>? bindings)
    {
        if (string.IsNullOrEmpty(template) || bindings is null)
            return template ?? string.Empty;
        if (Keys.Contains(template) && bindings.TryGetValue(template, out var boundValue))
            return boundValue;

        var result = template;
        foreach (var definition in Definitions)
        {
            if (bindings.TryGetValue(definition.Key, out var value))
                result = result.Replace(definition.Token, value, StringComparison.Ordinal);
        }
        return result;
    }

    public static CompositionScene ResolveScene(
        CompositionScene scene,
        IReadOnlyDictionary<string, string> bindings) => scene with
        {
            Objects = scene.Objects.Select(item => item.Kind == CompositionObjectKind.Text
                ? item with { TextBinding = Resolve(item.TextBinding, bindings) }
                : item).ToList(),
        };

    public static bool UsesBinding(string? template, string key) =>
        string.Equals(template, key, StringComparison.Ordinal)
        || template?.Contains($"{{{{{key}}}}}", StringComparison.Ordinal) == true;

    public static IReadOnlyList<string> UnknownTokens(string? template)
    {
        if (string.IsNullOrEmpty(template))
            return [];

        var unknown = new HashSet<string>(StringComparer.Ordinal);
        var start = 0;
        while ((start = template.IndexOf("{{", start, StringComparison.Ordinal)) >= 0)
        {
            var end = template.IndexOf("}}", start + 2, StringComparison.Ordinal);
            if (end < 0)
                break;
            var key = template[(start + 2)..end];
            if (!Keys.Contains(key))
                unknown.Add($"{{{{{key}}}}}");
            start = end + 2;
        }
        return unknown.Order(StringComparer.Ordinal).ToList();
    }
}
