using System.Text.Json;
using System.Text.Json.Nodes;
using Lorekeeper.Models;
using Lorekeeper.Publish;
using Lorekeeper.VersionHistory.Git;
using Lorekeeper.VersionHistory.Snapshots;

namespace Lorekeeper.VersionHistory.Services;

/// <summary>
/// Values a checkpoint could not carry because an older Lorekeeper wrote it on
/// top of history from a newer one. Each value comes from the newest ancestor
/// whose schema carries that field.
/// </summary>
public sealed record VersionHistoryForwardFieldValues(
    string? WorldBrief,
    string? WorldWorkingNotes,
    IReadOnlyDictionary<Guid, PrinterDimensions> PrinterDimensions)
{
    public bool IsEmpty =>
        string.IsNullOrEmpty(WorldBrief)
        && string.IsNullOrEmpty(WorldWorkingNotes)
        && PrinterDimensions.Count == 0;
}

/// <summary>
/// Recovers fields introduced after a checkpoint's schema from its ancestors.
/// An older app cannot represent these fields, so it drops them from the
/// checkpoints it writes; the newer content still exists earlier in history and
/// is carried forward instead of being lost. Each schema bump that adds a field
/// registers it here.
/// </summary>
public static class VersionHistoryForwardFields
{
    public const string CheckpointMessage = "Carried forward content saved by a newer Lorekeeper";

    private const int WorldBriefSchemaVersion = 11;
    private const int PrinterDimensionsSchemaVersion = 12;
    private const int WorldWorkingNotesSchemaVersion = 13;
    private const string NarrativePath = "narrative/narrative.json";
    private const string PublicationPath = "publication/publication.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static VersionHistoryForwardFieldValues? Read(
        IGitRepositoryStore git,
        Guid repositoryId,
        string commitSha,
        int schemaVersion,
        CancellationToken cancellationToken = default)
    {
        var pending = new HashSet<int>(
            new[] { WorldBriefSchemaVersion, PrinterDimensionsSchemaVersion, WorldWorkingNotesSchemaVersion }
                .Where(introducedIn => schemaVersion < introducedIn));
        if (pending.Count == 0)
            return null;

        string? worldBrief = null;
        string? workingNotes = null;
        var dimensions = new Dictionary<Guid, PrinterDimensions>();
        git.VisitAncestorFiles(
            repositoryId,
            commitSha,
            [VersionHistorySnapshotContract.ManifestFileName, NarrativePath, PublicationPath],
            files =>
            {
                var ancestorSchema = ReadSchemaVersion(files);
                // The newest ancestor that carries a field decides its value,
                // even when that value is empty.
                foreach (var introducedIn in pending.Where(field => ancestorSchema >= field).ToList())
                {
                    pending.Remove(introducedIn);
                    switch (introducedIn)
                    {
                        case WorldBriefSchemaVersion:
                            worldBrief = ReadString(files, NarrativePath, "worldBrief");
                            break;
                        case WorldWorkingNotesSchemaVersion:
                            workingNotes = ReadString(files, NarrativePath, "worldWorkingNotes");
                            break;
                        case PrinterDimensionsSchemaVersion:
                            ReadPrinterDimensions(files, dimensions);
                            break;
                    }
                }

                return pending.Count > 0;
            },
            cancellationToken);

        var values = new VersionHistoryForwardFieldValues(worldBrief, workingNotes, dimensions);
        return values.IsEmpty ? null : values;
    }

    /// <summary>
    /// Fills fields the payload's schema could not carry. Returns the payload
    /// unchanged when nothing applies.
    /// </summary>
    public static VersionHistorySnapshotPayload Apply(
        VersionHistorySnapshotPayload payload,
        VersionHistoryForwardFieldValues? values)
    {
        if (values is null || values.IsEmpty)
            return payload;

        var narrative = payload.Narrative;
        if (!string.IsNullOrEmpty(values.WorldBrief) && string.IsNullOrEmpty(narrative.WorldBrief))
            narrative = narrative with { WorldBrief = values.WorldBrief };
        if (!string.IsNullOrEmpty(values.WorldWorkingNotes) && string.IsNullOrEmpty(narrative.WorldWorkingNotes))
            narrative = narrative with { WorldWorkingNotes = values.WorldWorkingNotes };

        var editions = payload.Publication.PublicationEditions
            .Select(edition => edition.PrinterDimensions is null
                && values.PrinterDimensions.TryGetValue(edition.Id, out var dimensions)
                && AcceptsPrinterDimensions(edition.Vendor, edition.Format, dimensions)
                    ? edition with { PrinterDimensions = dimensions }
                    : edition)
            .ToList();
        if (ReferenceEquals(narrative, payload.Narrative)
            && editions.SequenceEqual(payload.Publication.PublicationEditions, ReferenceEqualityComparer.Instance))
            return payload;

        return payload with
        {
            Narrative = narrative,
            Publication = payload.Publication with { PublicationEditions = editions },
        };
    }

    public static bool AcceptsPrinterDimensions(
        PublicationVendor vendor,
        PublicationEditionFormat format,
        PrinterDimensions dimensions) =>
        vendor == PublicationVendor.Generic
        && format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover
        && PrintGeometryService.InvalidPrinterDimensions(dimensions) is null;

    private static int ReadSchemaVersion(IReadOnlyDictionary<string, byte[]> files)
    {
        var manifest = ParseObject(files, VersionHistorySnapshotContract.ManifestFileName);
        return manifest?["schemaVersion"] is JsonValue value && value.TryGetValue<int>(out var schema) ? schema : 0;
    }

    private static string? ReadString(IReadOnlyDictionary<string, byte[]> files, string path, string property) =>
        ParseObject(files, path)?[property] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static void ReadPrinterDimensions(
        IReadOnlyDictionary<string, byte[]> files,
        Dictionary<Guid, PrinterDimensions> dimensions)
    {
        if (ParseObject(files, PublicationPath)?["publicationEditions"] is not JsonArray editions)
            return;

        foreach (var edition in editions.OfType<JsonObject>())
        {
            if (edition["id"] is not JsonValue id
                || !id.TryGetValue<string>(out var idText)
                || !Guid.TryParse(idText, out var editionId)
                || edition["printerDimensions"] is not JsonObject node)
                continue;

            try
            {
                if (node.Deserialize<PrinterDimensions>(JsonOptions) is { } value)
                    dimensions[editionId] = value;
            }
            catch (JsonException)
            {
                // An unreadable historical value is skipped; the current one stays.
            }
        }
    }

    private static JsonObject? ParseObject(IReadOnlyDictionary<string, byte[]> files, string path)
    {
        if (!files.TryGetValue(path, out var bytes))
            return null;
        try
        {
            return JsonNode.Parse(bytes) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
