using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Lorekeeper.Authoring;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;

namespace Lorekeeper.Composition;

public sealed record CompositionCoverProperties(
    string Title,
    string Subtitle,
    string Author,
    string SpineText,
    string BackgroundColor,
    string BarcodeMode,
    string SpineReadingDirection);

/// <summary>
/// Converts immutable canvas objects into the small, canonical operations kept by
/// the process-wide authoring history. The adapter deliberately never accepts an
/// inverse from a caller: it derives it from the persisted scene supplied as
/// <paramref name="before"/>.
/// </summary>
public static class CompositionCanvasDeltaAdapter
{
    public static (IReadOnlyList<AuthoringOperationV1> Forward, IReadOnlyList<AuthoringOperationV1> Inverse)
        CreateCoverProperties(CompositionCoverProperties before, CompositionCoverProperties after, int targetOrdinal = 0)
    {
        var forward = Difference(JsonSerializer.SerializeToElement(before, ManuscriptCodec.JsonOptions), JsonSerializer.SerializeToElement(after, ManuscriptCodec.JsonOptions));
        if (forward.ValueKind == JsonValueKind.Undefined)
            return ([], []);
        var inverse = Difference(JsonSerializer.SerializeToElement(after, ManuscriptCodec.JsonOptions), JsonSerializer.SerializeToElement(before, ManuscriptCodec.JsonOptions));
        return (
            [new(targetOrdinal, "setCanvasCoverProperties", PropertyPatch: forward)],
            [new(targetOrdinal, "setCanvasCoverProperties", PropertyPatch: inverse)]);
    }

    public static CompositionCoverProperties ApplyCoverProperties(
        CompositionCoverProperties source,
        IReadOnlyList<AuthoringOperationV1> operations)
    {
        var current = JsonSerializer.SerializeToElement(source, ManuscriptCodec.JsonOptions);
        foreach (var operation in operations.Where(item => item.Kind.Equals("setCanvasCoverProperties", StringComparison.OrdinalIgnoreCase)))
        {
            if (operation.PropertyPatch is not { ValueKind: JsonValueKind.Object } patch)
                throw new InvalidDataException("Canvas cover history properties are malformed.");
            current = Merge(current, patch);
        }
        return current.Deserialize<CompositionCoverProperties>(ManuscriptCodec.JsonOptions)
            ?? throw new InvalidDataException("Canvas cover history properties are malformed.");
    }
    public static (IReadOnlyList<AuthoringOperationV1> Forward, IReadOnlyList<AuthoringOperationV1> Inverse)
        Create(CompositionScene before, CompositionScene after, int targetOrdinal = 0)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var forward = new List<AuthoringOperationV1>();
        var inverse = new List<AuthoringOperationV1>();
        AppendDelta(before.Layers, after.Layers, "CanvasLayer", targetOrdinal, forward, inverse,
            item => item.Id, ToElement, Fingerprint);
        AppendDelta(before.Styles, after.Styles, "CanvasStyle", targetOrdinal, forward, inverse,
            item => item.Id, ToElement, Fingerprint);
        var beforeById = before.Objects.ToDictionary(item => item.Id);
        var afterById = after.Objects.ToDictionary(item => item.Id);

        for (var index = 0; index < before.Objects.Count; index++)
        {
            var previous = before.Objects[index];
            if (!afterById.ContainsKey(previous.Id))
            {
                forward.Add(new(targetOrdinal, "removeCanvasObject", ObjectId: previous.Id,
                    ExpectedObjectFingerprint: Fingerprint(previous)));
                inverse.Insert(0, new(targetOrdinal, "insertCanvasObject", ObjectId: previous.Id,
                    ObjectIndex: index, CanonicalObject: ToElement(previous)));
            }
        }

        for (var index = 0; index < after.Objects.Count; index++)
        {
            var current = after.Objects[index];
            if (!beforeById.TryGetValue(current.Id, out var previous))
            {
                forward.Add(new(targetOrdinal, "insertCanvasObject", ObjectId: current.Id,
                    ObjectIndex: index, CanonicalObject: ToElement(current)));
                inverse.Insert(0, new(targetOrdinal, "removeCanvasObject", ObjectId: current.Id,
                    ExpectedObjectFingerprint: Fingerprint(current)));
                continue;
            }

            var oldIndex = Enumerable.Range(0, before.Objects.Count)
                .Single(candidate => before.Objects[candidate].Id == previous.Id);
            if (oldIndex != index)
            {
                forward.Add(new(targetOrdinal, "moveCanvasObject", ObjectId: current.Id,
                    ObjectIndex: index, ExpectedObjectFingerprint: Fingerprint(previous)));
                inverse.Insert(0, new(targetOrdinal, "moveCanvasObject", ObjectId: previous.Id,
                    ObjectIndex: oldIndex, ExpectedObjectFingerprint: Fingerprint(previous)));
            }

            var changed = Difference(previous, current);
            if (changed.ValueKind != JsonValueKind.Undefined)
            {
                forward.Add(new(targetOrdinal, "setCanvasObjectProperties", ObjectId: current.Id,
                    PropertyPatch: changed, ExpectedObjectFingerprint: Fingerprint(previous)));
                inverse.Insert(0, new(targetOrdinal, "setCanvasObjectProperties", ObjectId: previous.Id,
                    PropertyPatch: Difference(current, previous), ExpectedObjectFingerprint: Fingerprint(current)));
            }
        }
        return (forward, inverse);
    }

    public static CompositionScene Apply(
        CompositionScene source,
        IReadOnlyList<AuthoringOperationV1> operations)
    {
        var objects = source.Objects.ToList();
        var layers = source.Layers.ToList();
        var styles = source.Styles.ToList();
        foreach (var operation in operations)
        {
            var kind = operation.Kind.Trim().ToLowerInvariant();
            // Cover metadata shares the same atomic history action as the
            // scene, but is applied by ApplyCoverProperties below.
            if (kind == "setcanvascoverproperties")
                continue;
            if (kind.Contains("canvaslayer", StringComparison.Ordinal))
            {
                ApplyList(layers, operation, "CanvasLayer", item => item.Id,
                    element => element.Deserialize<CompositionLayer>(ManuscriptCodec.JsonOptions)
                        ?? throw new InvalidDataException("Canvas history layer is malformed."));
                continue;
            }
            if (kind.Contains("canvasstyle", StringComparison.Ordinal))
            {
                ApplyList(styles, operation, "CanvasStyle", item => item.Id,
                    element => element.Deserialize<CompositionObjectStyle>(ManuscriptCodec.JsonOptions)
                        ?? throw new InvalidDataException("Canvas history style is malformed."));
                continue;
            }
            var id = operation.ObjectId ?? throw new ArgumentException("Canvas operation requires objectId.");
            var index = objects.FindIndex(item => item.Id == id);
            switch (kind)
            {
                case "insertcanvasobject":
                {
                    if (operation.CanonicalObject is not { } canonical)
                        throw new ArgumentException("InsertCanvasObject is reserved for canonical history operations.");
                    var inserted = canonical.Deserialize<CompositionObject>(ManuscriptCodec.JsonOptions)
                        ?? throw new InvalidDataException("Canvas history object is malformed.");
                    if (inserted.Id != id)
                        throw new InvalidDataException("Canvas history object ID does not match its operation.");
                    if (index >= 0)
                        objects.RemoveAt(index);
                    objects.Insert(Math.Clamp(operation.ObjectIndex ?? objects.Count, 0, objects.Count), inserted);
                    break;
                }
                case "removecanvasobject":
                    RequireObject(objects, index, operation).RemoveAt(index);
                    break;
                case "movecanvasobject":
                {
                    var item = RequireObject(objects, index, operation)[index];
                    objects.RemoveAt(index);
                    objects.Insert(Math.Clamp(operation.ObjectIndex ?? objects.Count, 0, objects.Count), item);
                    break;
                }
                case "setcanvasobjectproperties":
                {
                    var item = RequireObject(objects, index, operation)[index];
                    if (operation.PropertyPatch is not { } patch || patch.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("SetCanvasObjectProperties requires an object property patch.");
                    var merged = Merge(ToElement(item), patch);
                    var updated = merged.Deserialize<CompositionObject>(ManuscriptCodec.JsonOptions)
                        ?? throw new InvalidDataException("Canvas history property patch is malformed.");
                    if (updated.Id != item.Id)
                        throw new InvalidDataException("Canvas history cannot change an object ID.");
                    objects[index] = updated;
                    break;
                }
                default:
                    throw new ArgumentException($"Unsupported canvas history operation '{operation.Kind}'.");
            }
        }
        return source with { Layers = layers, Styles = styles, Objects = objects };
    }

    public static string Fingerprint(CompositionObject item) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(item, ManuscriptCodec.JsonOptions)))).ToLowerInvariant();

    public static string Fingerprint(CompositionLayer item) => FingerprintElement(ToElement(item));
    public static string Fingerprint(CompositionObjectStyle item) => FingerprintElement(ToElement(item));

    private static void AppendDelta<T>(
        IReadOnlyList<T> before,
        IReadOnlyList<T> after,
        string name,
        int targetOrdinal,
        List<AuthoringOperationV1> forward,
        List<AuthoringOperationV1> inverse,
        Func<T, Guid> id,
        Func<T, JsonElement> serialize,
        Func<T, string> fingerprint)
    {
        var beforeById = before.ToDictionary(id);
        var afterById = after.ToDictionary(id);
        for (var index = 0; index < before.Count; index++)
        {
            var previous = before[index];
            if (!afterById.ContainsKey(id(previous)))
            {
                forward.Add(new(targetOrdinal, $"remove{name}", ObjectId: id(previous), ExpectedObjectFingerprint: fingerprint(previous)));
                inverse.Insert(0, new(targetOrdinal, $"insert{name}", ObjectId: id(previous), ObjectIndex: index, CanonicalObject: serialize(previous)));
            }
        }
        for (var index = 0; index < after.Count; index++)
        {
            var current = after[index];
            if (!beforeById.TryGetValue(id(current), out var previous))
            {
                forward.Add(new(targetOrdinal, $"insert{name}", ObjectId: id(current), ObjectIndex: index, CanonicalObject: serialize(current)));
                inverse.Insert(0, new(targetOrdinal, $"remove{name}", ObjectId: id(current), ExpectedObjectFingerprint: fingerprint(current)));
                continue;
            }
            var oldIndex = Enumerable.Range(0, before.Count).Single(candidate => id(before[candidate]) == id(previous));
            if (oldIndex != index)
            {
                forward.Add(new(targetOrdinal, $"move{name}", ObjectId: id(current), ObjectIndex: index, ExpectedObjectFingerprint: fingerprint(previous)));
                inverse.Insert(0, new(targetOrdinal, $"move{name}", ObjectId: id(previous), ObjectIndex: oldIndex, ExpectedObjectFingerprint: fingerprint(previous)));
            }
            var changed = Difference(serialize(previous), serialize(current));
            if (changed.ValueKind != JsonValueKind.Undefined)
            {
                forward.Add(new(targetOrdinal, $"set{name}Properties", ObjectId: id(current), PropertyPatch: changed, ExpectedObjectFingerprint: fingerprint(previous)));
                inverse.Insert(0, new(targetOrdinal, $"set{name}Properties", ObjectId: id(previous), PropertyPatch: Difference(serialize(current), serialize(previous)), ExpectedObjectFingerprint: fingerprint(current)));
            }
        }
    }

    private static void ApplyList<T>(
        List<T> items,
        AuthoringOperationV1 operation,
        string name,
        Func<T, Guid> id,
        Func<JsonElement, T> deserialize)
    {
        var operationName = operation.Kind.Trim().ToLowerInvariant();
        var objectId = operation.ObjectId ?? throw new ArgumentException($"{name} operation requires objectId.");
        var index = items.FindIndex(item => id(item) == objectId);
        if (operationName == $"insert{name}".ToLowerInvariant())
        {
            if (operation.CanonicalObject is not { } canonical)
                throw new ArgumentException($"Insert{name} is reserved for canonical history operations.");
            var inserted = deserialize(canonical);
            if (id(inserted) != objectId)
                throw new InvalidDataException($"Canvas history {name} ID does not match its operation.");
            if (index >= 0) items.RemoveAt(index);
            items.Insert(Math.Clamp(operation.ObjectIndex ?? items.Count, 0, items.Count), inserted);
            return;
        }
        if (index < 0)
            throw new KeyNotFoundException($"The canvas history {name} no longer exists.");
        var fingerprint = FingerprintElement(JsonSerializer.SerializeToElement(items[index], ManuscriptCodec.JsonOptions));
        if (!string.IsNullOrWhiteSpace(operation.ExpectedObjectFingerprint)
            && !string.Equals(operation.ExpectedObjectFingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The canvas history {name} changed outside this history action.");
        }
        if (operationName == $"remove{name}".ToLowerInvariant())
        {
            items.RemoveAt(index);
            return;
        }
        if (operationName == $"move{name}".ToLowerInvariant())
        {
            var item = items[index];
            items.RemoveAt(index);
            items.Insert(Math.Clamp(operation.ObjectIndex ?? items.Count, 0, items.Count), item);
            return;
        }
        if (operationName != $"set{name}properties".ToLowerInvariant() || operation.PropertyPatch is not { } patch)
            throw new ArgumentException($"Unsupported canvas history operation '{operation.Kind}'.");
        var merged = Merge(JsonSerializer.SerializeToElement(items[index], ManuscriptCodec.JsonOptions), patch);
        var updated = deserialize(merged);
        if (id(updated) != objectId)
            throw new InvalidDataException($"Canvas history cannot change a {name} ID.");
        items[index] = updated;
    }

    private static List<CompositionObject> RequireObject(
        List<CompositionObject> objects,
        int index,
        AuthoringOperationV1 operation)
    {
        if (index < 0)
            throw new KeyNotFoundException("The canvas history object no longer exists.");
        if (!string.IsNullOrWhiteSpace(operation.ExpectedObjectFingerprint)
            && !string.Equals(operation.ExpectedObjectFingerprint, Fingerprint(objects[index]), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The canvas history object changed outside this history action.");
        }
        return objects;
    }

    private static JsonElement Difference(CompositionObject before, CompositionObject after)
    {
        return Difference(ToElement(before), ToElement(after));
    }

    private static JsonElement Difference(JsonElement before, JsonElement after)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in after.EnumerateObject())
        {
            if (property.NameEquals("id"))
                continue;
            if (!before.TryGetProperty(property.Name, out var old)
                || !JsonElement.DeepEquals(old, property.Value))
            {
                values[property.Name] = property.Value.Clone();
            }
        }
        return values.Count == 0
            ? default
            : JsonSerializer.SerializeToElement(values, ManuscriptCodec.JsonOptions);
    }

    private static JsonElement ToElement(CompositionObject item) =>
        JsonSerializer.SerializeToElement(item, ManuscriptCodec.JsonOptions);
    private static JsonElement ToElement(CompositionLayer item) =>
        JsonSerializer.SerializeToElement(item, ManuscriptCodec.JsonOptions);
    private static JsonElement ToElement(CompositionObjectStyle item) =>
        JsonSerializer.SerializeToElement(item, ManuscriptCodec.JsonOptions);

    private static string FingerprintElement(JsonElement value) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.GetRawText()))).ToLowerInvariant();

    private static JsonElement Merge(JsonElement current, JsonElement patch)
    {
        var values = current.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.Clone(),
            StringComparer.Ordinal);
        foreach (var property in patch.EnumerateObject())
            values[property.Name] = property.Value.Clone();
        return JsonSerializer.SerializeToElement(values, ManuscriptCodec.JsonOptions);
    }
}
