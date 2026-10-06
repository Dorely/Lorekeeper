using System.Text.Json;
using System.Text.Json.Serialization;
using Lorekeeper.Manuscripts;

namespace Lorekeeper.Authoring;

public static class AuthoringProtocolV1
{
    public const string ProtocolId = "AuthoringBatchProtocolV1";
    public const string JournalId = "AuthoringJournalV1";
    public const int MaxActionsPerTarget = 100;
    public const long MaxProcessHistoryBytes = 128L * 1024 * 1024;
}

public sealed record AuthoringTargetReferenceV1(
    Guid ProjectId,
    string TargetId);

public sealed record AuthoringSessionOpenRequestV1(
    Guid ProjectId,
    Guid SessionId,
    IReadOnlyList<AuthoringSessionTargetV1> Targets,
    string ProtocolId = AuthoringProtocolV1.ProtocolId);

public sealed record AuthoringSessionTargetV1(string TargetId);

public sealed record AuthoringSessionOpenResultV1(
    string ProtocolId,
    string JournalId,
    Guid ProcessIncarnationId,
    Guid SessionId,
    long NextSequence,
    IReadOnlyList<AuthoringCanonicalTargetStateV1> Targets);

public sealed record AuthoringBatchTargetV1(
    int Ordinal,
    string TargetId,
    long ExpectedRevision,
    long ExpectedGeneration,
    string BaseFingerprint);

public sealed record AuthoringInsertAnchorV1(
    string? BeforeBlockId = null,
    string? AfterBlockId = null);

public sealed record AuthoringOrderPreconditionV1(
    string? PreviousBlockId,
    string? PreviousBlockFingerprint,
    string? NextBlockId,
    string? NextBlockFingerprint);

/// <summary>
/// Versioned wire operation. The union is intentionally flattened so the JS
/// adapter can serialize one stable shape without polymorphic CLR metadata.
/// Client-supplied inverse operations are not part of this contract.
/// </summary>
public sealed record AuthoringOperationV1(
    int TargetOrdinal,
    string Kind,
    string? BlockId = null,
    string? SecondBlockId = null,
    int? Index = null,
    string? BlockType = null,
    string? Text = null,
    string? StyleRole = null,
    int? StartOffset = null,
    int? EndOffset = null,
    string? Mark = null,
    bool? Enabled = null,
    string? Value = null,
    Guid? ImageId = null,
    string? AltText = null,
    int? HeadingLevel = null,
    ParagraphPresentation? ParagraphPresentation = null,
    string? PlacementBlockId = null,
    Guid? PageId = null,
    AuthoringInsertAnchorV1? InsertAt = null,
    string? ExpectedElementFingerprint = null,
    string? ExpectedAnchorFingerprint = null,
    ManuscriptBlock? CanonicalBlock = null,
    Guid? VariantId = null,
    Guid? ObjectId = null,
    int? ObjectIndex = null,
    JsonElement? PropertyPatch = null,
    JsonElement? CanonicalObject = null,
    string? ExpectedObjectFingerprint = null,
    string? ExpectedSecondElementFingerprint = null,
    AuthoringOrderPreconditionV1? ExpectedOrder = null,
    bool? Decorative = null,
    FigurePresentation? FigurePresentation = null,
    Guid? DesignedPageId = null,
    string? Language = null,
    FigureAccessibilityRole? AccessibilityRole = null,
    ManuscriptDocument? RichDocument = null,
    string? ExpectedDocumentFingerprint = null,
    ManuscriptPosition? Position = null,
    IReadOnlyList<ManuscriptInline>? InlineContent = null);

public sealed record AuthoringSelectionPointV1(
    string TargetId,
    string? BlockId,
    int Offset,
    string Affinity = "forward");

public sealed record AuthoringSelectionV1(
    string Kind,
    IReadOnlyList<AuthoringSelectionPointV1> Targets);

public sealed record AuthoringSelectionTransitionV1(
    AuthoringSelectionV1? Before,
    AuthoringSelectionV1? After);

public sealed record AuthoringBatchV1(
    Guid ProjectId,
    Guid SessionId,
    Guid BatchId,
    long Sequence,
    string RequestHash,
    string ActionLabel,
    IReadOnlyList<AuthoringBatchTargetV1> Targets,
    IReadOnlyList<AuthoringOperationV1> Operations,
    AuthoringSelectionTransitionV1? Selection = null,
    string ProtocolId = AuthoringProtocolV1.ProtocolId,
    Lorekeeper.Manuscripts.Import.SemanticImportResources? ImportResources = null);

[JsonConverter(typeof(JsonStringEnumConverter<AuthoringBatchStatusV1>))]
public enum AuthoringBatchStatusV1
{
    Committed,
    Replayed,
    Conflict,
}

public sealed record AuthoringCanonicalVersionV1(
    string TargetId,
    long BeforeRevision,
    long AfterRevision,
    long Generation,
    string Fingerprint);

public sealed record AuthoringCanonicalTargetStateV1(
    string TargetId,
    long Revision,
    long Generation,
    string Fingerprint,
    string ManuscriptJson,
    string SelectionJson = "",
    IReadOnlyDictionary<string, string>? ElementFingerprints = null);

public sealed record AuthoringCanonicalInverseV1(
    string Authority,
    IReadOnlyList<AuthoringOperationV1> Operations);

public sealed record AuthoringBatchHistoryV1(
    bool Compound,
    IReadOnlyList<string> TargetIds,
    string UndoUnit,
    bool Undoable,
    AuthoringHistoryState State);

public sealed record AuthoringConflictVariantV1(
    string Side,
    string Fingerprint,
    string ManuscriptJson,
    IReadOnlyList<AuthoringOperationV1> Operations);

public sealed record AuthoringConflictV1(
    string Code,
    string TargetId,
    string Message,
    AuthoringConflictVariantV1 Canonical,
    AuthoringConflictVariantV1 Submitted);

public sealed record AuthoringBatchResultV1(
    AuthoringBatchStatusV1 Status,
    Guid ProcessIncarnationId,
    Guid BatchId,
    Guid? ReceiptId,
    string RequestHash,
    IReadOnlyList<AuthoringCanonicalVersionV1> CanonicalVersions,
    AuthoringCanonicalInverseV1 Inverse,
    AuthoringBatchHistoryV1 History,
    IReadOnlyList<AuthoringConflictV1> Conflicts,
    IReadOnlyList<AuthoringCanonicalTargetStateV1> Targets,
    long Sequence = 0,
    string? HistoryRequestFingerprint = null);

public sealed record AuthoringReceiptAcknowledgementV1(
    Guid ProjectId,
    Guid SessionId,
    Guid BatchId,
    Guid ReceiptId,
    string RequestHash);

public sealed record AuthoringHistoryRequestV1(
    Guid ProjectId,
    Guid SessionId,
    Guid HistoryRequestId,
    string TargetId,
    long ExpectedGeneration);

public sealed record AuthoringHistoryResultV1(
    Guid ProcessIncarnationId,
    string TargetId,
    AuthoringHistoryState State,
    AuthoringBatchResultV1? Batch,
    AuthoringHistoryCursorV1 Cursor);

public sealed record AuthoringHistoryCursorActionV1(
    Guid ActionId,
    string ActionLabel,
    IReadOnlyList<string> TargetIds,
    IReadOnlyDictionary<string, long> Generations,
    IReadOnlyList<AuthoringOperationV1> Forward,
    IReadOnlyList<AuthoringOperationV1> Inverse,
    AuthoringSelectionV1? BeforeSelection,
    AuthoringSelectionV1? AfterSelection);

public sealed record AuthoringHistoryCursorV1(
    int Position,
    IReadOnlyList<AuthoringHistoryCursorActionV1> Actions);

public interface IAuthoringBatchService
{
    Guid ProcessIncarnationId { get; }

    Task<AuthoringSessionOpenResultV1> OpenSessionAsync(
        AuthoringSessionOpenRequestV1 request,
        CancellationToken cancellationToken = default);

    // request is the client payload that batch was deserialized from; its canonical hash must equal batch.RequestHash.
    Task<AuthoringBatchResultV1> ApplyBatchAsync(
        AuthoringBatchV1 batch,
        JsonElement request,
        CancellationToken cancellationToken = default);

    Task AcknowledgeReceiptAsync(
        AuthoringReceiptAcknowledgementV1 acknowledgement,
        CancellationToken cancellationToken = default);

    Task<AuthoringHistoryResultV1> ReadHistoryStateAsync(
        AuthoringTargetReferenceV1 target,
        CancellationToken cancellationToken = default);

    Task<AuthoringHistoryResultV1> UndoAsync(
        AuthoringHistoryRequestV1 request,
        CancellationToken cancellationToken = default);

    Task<AuthoringHistoryResultV1> RedoAsync(
        AuthoringHistoryRequestV1 request,
        CancellationToken cancellationToken = default);
}
