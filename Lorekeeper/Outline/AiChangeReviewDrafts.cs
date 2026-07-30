using System.Text.Json;
using Lorekeeper.Models;

namespace Lorekeeper.Outline;

public static class AiChangeReviewDrafts
{
    private const string PropertiesPrefix = "properties.";

    private static readonly JsonSerializerOptions ChangePayloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string EffectiveAfterJson(AiChange change) =>
        string.IsNullOrWhiteSpace(change.DraftAfterJson) ? change.AfterJson : change.DraftAfterJson;

    public static bool HasDraft(AiChange change) => !string.IsNullOrWhiteSpace(change.DraftAfterJson);

    public static bool TryGetEditablePayload(AiChange change, out AiChangeReviewEditablePayload payload)
    {
        payload = null!;
        var kind = GetKind(change);
        switch (kind)
        {
            case ReviewDraftPayloadKind.Act:
            {
                var before = ReadOptional<OutlineActChange>(change.BeforeJson);
                var after = ReadOptional<OutlineActChange>(EffectiveAfterJson(change));
                if (after is null) return false;

                payload = new AiChangeReviewEditablePayload(
                    string.Equals(change.ToolName, "create_act", StringComparison.OrdinalIgnoreCase) ? "Act create" : "Act edit",
                    after.Title,
                    [
                        new AiChangeReviewEditableField("Title", "Title", before?.Title ?? string.Empty, after.Title, before is not null, NewExists: true),
                        new AiChangeReviewEditableField("Synopsis", "Synopsis", before?.Synopsis ?? string.Empty, after.Synopsis, before is not null, NewExists: true),
                    ]);
                return true;
            }
            case ReviewDraftPayloadKind.Chapter:
            {
                var before = ReadOptional<OutlineChapterChange>(change.BeforeJson);
                var after = ReadOptional<OutlineChapterChange>(EffectiveAfterJson(change));
                if (after is null) return false;

                payload = new AiChangeReviewEditablePayload(
                    string.Equals(change.ToolName, "create_chapter", StringComparison.OrdinalIgnoreCase) ? "Chapter create" : "Chapter edit",
                    after.Title,
                    [
                        new AiChangeReviewEditableField("Title", "Title", before?.Title ?? string.Empty, after.Title, before is not null, NewExists: true),
                        new AiChangeReviewEditableField("Synopsis", "Synopsis", before?.Synopsis ?? string.Empty, after.Synopsis, before is not null, NewExists: true),
                    ]);
                return true;
            }
            case ReviewDraftPayloadKind.Entity:
            {
                var before = ReadOptional<OutlineEntityChange>(change.BeforeJson);
                var after = ReadOptional<OutlineEntityChange>(EffectiveAfterJson(change));
                if (after is null) return false;

                var fields = new List<AiChangeReviewEditableField>
                {
                    new("Name", "Name", before?.Name ?? string.Empty, after.Name, before is not null, NewExists: true),
                };

                var propertyNames = (before?.Properties.Keys ?? Enumerable.Empty<string>())
                    .Concat(after.Properties.Keys)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(propertyName => propertyName, StringComparer.OrdinalIgnoreCase);

                foreach (var propertyName in propertyNames)
                {
                    var beforeValue = GetPropertyValue(before?.Properties, propertyName, out var beforeExists);
                    var afterValue = GetPropertyValue(after.Properties, propertyName, out var afterExists);
                    fields.Add(new AiChangeReviewEditableField(
                        $"{PropertiesPrefix}{propertyName}",
                        $"properties.{propertyName}",
                        beforeExists ? beforeValue : string.Empty,
                        afterExists ? afterValue : string.Empty,
                        beforeExists,
                        afterExists));
                }

                payload = new AiChangeReviewEditablePayload(
                    string.Equals(change.ToolName, "create_entity", StringComparison.OrdinalIgnoreCase) ? $"{after.Type} create" : $"{after.Type} edit",
                    after.Name,
                    fields);
                return true;
            }
            default:
                return false;
        }
    }

    public static bool TryUpdateField(AiChange change, string fieldKey, string newText, out string draftAfterJson, out string? error)
    {
        draftAfterJson = string.Empty;
        error = null;

        var kind = GetKind(change);
        switch (kind)
        {
            case ReviewDraftPayloadKind.Act:
            {
                if (!TryReadChange<OutlineActChange>(EffectiveAfterJson(change), out var after))
                    return Fail("Could not read the act draft.", out draftAfterJson, out error);

                if (FieldKeyEquals(fieldKey, "Title"))
                    draftAfterJson = Serialize(after with { Title = newText });
                else if (FieldKeyEquals(fieldKey, "Synopsis"))
                    draftAfterJson = Serialize(after with { Synopsis = newText });
                else
                    return Fail($"Act changes do not have a '{fieldKey}' field.", out draftAfterJson, out error);
                return true;
            }
            case ReviewDraftPayloadKind.Chapter:
            {
                if (!TryReadChange<OutlineChapterChange>(EffectiveAfterJson(change), out var after))
                    return Fail("Could not read the chapter draft.", out draftAfterJson, out error);

                if (FieldKeyEquals(fieldKey, "Title"))
                    draftAfterJson = Serialize(after with { Title = newText });
                else if (FieldKeyEquals(fieldKey, "Synopsis"))
                    draftAfterJson = Serialize(after with { Synopsis = newText });
                else
                    return Fail($"Chapter changes do not have a '{fieldKey}' field.", out draftAfterJson, out error);
                return true;
            }
            case ReviewDraftPayloadKind.Entity:
            {
                if (!TryReadChange<OutlineEntityChange>(EffectiveAfterJson(change), out var after))
                    return Fail("Could not read the entity draft.", out draftAfterJson, out error);

                if (FieldKeyEquals(fieldKey, "Name"))
                {
                    draftAfterJson = Serialize(after with { Name = newText });
                    return true;
                }

                if (!TryGetPropertyName(fieldKey, out var propertyName))
                    return Fail($"Entity changes do not have a '{fieldKey}' field.", out draftAfterJson, out error);

                var properties = new Dictionary<string, string?>(after.Properties, StringComparer.OrdinalIgnoreCase);
                var before = ReadOptional<OutlineEntityChange>(change.BeforeJson);
                var existedBefore = before?.Properties.ContainsKey(propertyName) == true;
                if (!existedBefore && string.IsNullOrEmpty(newText))
                    properties.Remove(propertyName);
                else
                    properties[propertyName] = newText;

                draftAfterJson = Serialize(after with { Properties = properties });
                return true;
            }
            default:
                return Fail($"AI change '{change.ToolName}' cannot be edited in review.", out draftAfterJson, out error);
        }
    }

    public static bool TryResetFieldToOriginalProposal(AiChange change, string fieldKey, out string draftAfterJson, out string? error)
    {
        var originalProposal = new AiChange
        {
            ToolName = change.ToolName,
            ResourceKind = change.ResourceKind,
            BeforeJson = change.BeforeJson,
            AfterJson = change.AfterJson,
            DraftAfterJson = null,
        };

        if (!TryGetEditablePayload(originalProposal, out var originalPayload))
            return Fail("Could not read the original AI proposal.", out draftAfterJson, out error);

        var field = originalPayload.Fields.FirstOrDefault(candidate => FieldKeyEquals(candidate.Key, fieldKey));
        if (field is null)
            return Fail($"The original proposal does not have a '{fieldKey}' field.", out draftAfterJson, out error);

        return TryUpdateField(change, fieldKey, field.NewText, out draftAfterJson, out error);
    }

    public static bool TryValidateDraftAfterJson(AiChange change, string draftAfterJson, out string? error)
    {
        error = null;
        var kind = GetKind(change);
        switch (kind)
        {
            case ReviewDraftPayloadKind.Act:
                return ValidateAct(change, draftAfterJson, out error);
            case ReviewDraftPayloadKind.Chapter:
                return ValidateChapter(change, draftAfterJson, out error);
            case ReviewDraftPayloadKind.Entity:
                return ValidateEntity(change, draftAfterJson, out error);
            default:
                error = $"AI change '{change.ToolName}' does not support review drafts.";
                return false;
        }
    }

    private static bool ValidateAct(AiChange change, string draftAfterJson, out string? error)
    {
        error = null;
        if (!TryReadChange<OutlineActChange>(change.AfterJson, out var original)
            || !TryReadChange<OutlineActChange>(draftAfterJson, out var draft))
        {
            error = "Could not read the act draft.";
            return false;
        }

        if (draft.Id != original.Id || draft.Order != original.Order)
        {
            error = "The act draft changed immutable metadata.";
            return false;
        }

        return true;
    }

    private static bool ValidateChapter(AiChange change, string draftAfterJson, out string? error)
    {
        error = null;
        if (!TryReadChange<OutlineChapterChange>(change.AfterJson, out var original)
            || !TryReadChange<OutlineChapterChange>(draftAfterJson, out var draft))
        {
            error = "Could not read the chapter draft.";
            return false;
        }

        if (draft.Id != original.Id
            || draft.Order != original.Order
            || draft.ActId != original.ActId
            || draft.VisualMode != original.VisualMode
            || draft.PageLayoutKind != original.PageLayoutKind)
        {
            error = "The chapter draft changed immutable metadata.";
            return false;
        }

        return true;
    }

    private static bool ValidateEntity(AiChange change, string draftAfterJson, out string? error)
    {
        error = null;
        if (!TryReadChange<OutlineEntityChange>(change.AfterJson, out var original)
            || !TryReadChange<OutlineEntityChange>(draftAfterJson, out var draft))
        {
            error = "Could not read the entity draft.";
            return false;
        }

        if (draft.Id != original.Id
            || draft.Order != original.Order
            || draft.ParentId != original.ParentId
            || !string.Equals(draft.Type, original.Type, StringComparison.Ordinal))
        {
            error = "The entity draft changed immutable metadata.";
            return false;
        }

        return true;
    }

    private static ReviewDraftPayloadKind GetKind(AiChange change)
    {
        if (IsTool(change, "create_act", "update_act")) return ReviewDraftPayloadKind.Act;
        if (IsTool(change, "create_chapter", "update_chapter")) return ReviewDraftPayloadKind.Chapter;
        if (IsTool(change, "create_entity", "update_entity")) return ReviewDraftPayloadKind.Entity;
        return ReviewDraftPayloadKind.Unsupported;
    }

    private static bool IsTool(AiChange change, params string[] toolNames) =>
        toolNames.Any(toolName => string.Equals(change.ToolName, toolName, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetPropertyName(string fieldKey, out string propertyName)
    {
        propertyName = string.Empty;
        if (!fieldKey.StartsWith(PropertiesPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        propertyName = fieldKey[PropertiesPrefix.Length..];
        return !string.IsNullOrWhiteSpace(propertyName);
    }

    private static bool FieldKeyEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string GetPropertyValue(Dictionary<string, string?>? properties, string propertyName, out bool exists)
    {
        if (properties is not null && properties.TryGetValue(propertyName, out var value))
        {
            exists = true;
            return value ?? string.Empty;
        }

        exists = false;
        return string.Empty;
    }

    private static T? ReadOptional<T>(string json)
        where T : class =>
        string.IsNullOrWhiteSpace(json) || json == "null"
            ? default
            : JsonSerializer.Deserialize<T>(json, ChangePayloadJsonOptions);

    private static bool TryReadChange<T>(string json, out T value)
        where T : class
    {
        value = null!;
        if (string.IsNullOrWhiteSpace(json) || json == "null") return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<T>(json, ChangePayloadJsonOptions);
            if (parsed is null) return false;
            value = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Serialize(object value) =>
        JsonSerializer.Serialize(value, JsonSerializerOptions.Default);

    private static bool Fail(string message, out string draftAfterJson, out string? error)
    {
        draftAfterJson = string.Empty;
        error = message;
        return false;
    }

    private enum ReviewDraftPayloadKind
    {
        Unsupported,
        Act,
        Chapter,
        Entity,
    }
}

public sealed record AiChangeReviewEditablePayload(
    string Title,
    string? Subtitle,
    IReadOnlyList<AiChangeReviewEditableField> Fields);

public sealed record AiChangeReviewEditableField(
    string Key,
    string Label,
    string OldText,
    string NewText,
    bool OldExists,
    bool NewExists);
