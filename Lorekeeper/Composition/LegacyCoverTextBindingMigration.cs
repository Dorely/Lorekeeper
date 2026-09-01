using System.Text.Json;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;

namespace Lorekeeper.Composition;

/// <summary>
/// Adapts persisted cover scenes created before publication Description became
/// the sole source for descriptive cover copy. This is used only at explicit
/// migration, import, snapshot, and local-history read boundaries.
/// </summary>
public static class LegacyCoverTextBindingMigration
{
    private const string LegacyKey = "backCopy";
    private const string CurrentKey = "description";

    public static CompositionScene Adapt(CompositionScene scene) => scene with
    {
        Objects = scene.Objects.Select(item => item.Kind == CompositionObjectKind.Text
            ? item with { TextBinding = AdaptTemplate(item.TextBinding) }
            : item).ToList(),
    };

    public static string AdaptSceneJson(string sceneJson)
    {
        if (string.IsNullOrWhiteSpace(sceneJson))
            return sceneJson;

        var scene = JsonSerializer.Deserialize<CompositionScene>(sceneJson, ManuscriptCodec.JsonOptions)
            ?? throw new InvalidDataException("A legacy cover scene is empty.");
        return JsonSerializer.Serialize(Adapt(scene), ManuscriptCodec.JsonOptions);
    }

    public static string AdaptSurfaceScenesJson(string surfaceScenesJson)
    {
        if (string.IsNullOrWhiteSpace(surfaceScenesJson))
            return surfaceScenesJson;

        var scenes = JsonSerializer.Deserialize<Dictionary<string, string>>(
                surfaceScenesJson,
                ManuscriptCodec.JsonOptions)
            ?? throw new InvalidDataException("Legacy cover surface scenes are empty.");
        return JsonSerializer.Serialize(
            scenes.ToDictionary(
                item => item.Key,
                item => AdaptSceneJson(item.Value),
                StringComparer.Ordinal),
            ManuscriptCodec.JsonOptions);
    }

    private static string AdaptTemplate(string? template)
    {
        if (string.IsNullOrEmpty(template))
            return template ?? string.Empty;
        if (string.Equals(template, LegacyKey, StringComparison.Ordinal))
            return CurrentKey;
        return template.Replace("{{backCopy}}", "{{description}}", StringComparison.Ordinal);
    }
}
