using System.Text;
using Lorekeeper.Models;

namespace Lorekeeper.Composition;

public sealed record PublicationTextBindingDefinition(
    string Key,
    string Token,
    string Label,
    PublicationBoundField? PublicationField = null,
    bool RequiresPrintCover = false);

public static class PublicationTextBindings
{
    public static IReadOnlyList<PublicationTextBindingDefinition> CanonicalDefinitions { get; } =
    [
        new("title", "{{title}}", "Title", PublicationBoundField.Title),
        new("subtitle", "{{subtitle}}", "Subtitle", PublicationBoundField.Subtitle),
        new("author", "{{author}}", "Author", PublicationBoundField.Author),
        new("publisher", "{{publisher}}", "Publisher", PublicationBoundField.Publisher),
        new("copyright", "{{copyright}}", "Copyright", PublicationBoundField.Copyright),
        new("description", "{{description}}", "Description", PublicationBoundField.Description),
        new("isbn", "{{isbn}}", "ISBN", PublicationBoundField.Isbn),
    ];

    public static IReadOnlyList<PublicationTextBindingDefinition> Definitions { get; } =
    [
        .. CanonicalDefinitions,
        new("spineText", "{{spineText}}", "Spine text", RequiresPrintCover: true),
    ];

    private static readonly IReadOnlySet<string> Keys = Definitions
        .Select(item => item.Key)
        .ToHashSet(StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, string> Bindings(
        string title,
        string subtitle,
        string author,
        string publisher,
        string copyright,
        string description,
        string isbn,
        string spineText) => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["title"] = title,
            ["subtitle"] = subtitle,
            ["author"] = author,
            ["publisher"] = publisher,
            ["copyright"] = copyright,
            ["description"] = description,
            ["isbn"] = isbn,
            ["spineText"] = spineText,
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
            Objects = scene.Objects.Select(item =>
            {
                if (item.Kind != CompositionObjectKind.Text)
                    return item;
                var text = Resolve(item.TextBinding, bindings);
                // An empty optional field such as a subtitle hides its frame, as the canvas preview does;
                // Press rejects a visible text frame with no text or content references.
                return item with
                {
                    TextBinding = text,
                    Visible = item.Visible && (string.IsNullOrWhiteSpace(item.TextBinding) || !string.IsNullOrWhiteSpace(text)),
                };
            }).ToList(),
        };

    public static bool UsesBinding(string? template, string key) =>
        string.Equals(template, key, StringComparison.Ordinal)
        || template?.Contains($"{{{{{key}}}}}", StringComparison.Ordinal) == true;

    public static string NormalizeSemanticText(string value)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var builder = new StringBuilder(normalized.Length);
        for (var index = 0; index < normalized.Length; index++)
        {
            if (normalized[index] != '\n')
            {
                builder.Append(normalized[index]);
                continue;
            }

            builder.Append('\n');
            var next = index + 1;
            while (true)
            {
                var whitespaceEnd = next;
                while (whitespaceEnd < normalized.Length && normalized[whitespaceEnd] is ' ' or '\t')
                    whitespaceEnd++;
                if (whitespaceEnd >= normalized.Length || normalized[whitespaceEnd] != '\n')
                {
                    index = next - 1;
                    break;
                }

                next = whitespaceEnd + 1;
            }
        }

        return builder.ToString();
    }

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
