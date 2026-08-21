using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Authoring;

/// <summary>
/// Durable storage for the latest before-state used by Review Edits. It has no
/// relationship to the process-lifetime Undo/Redo runtime.
/// </summary>
public interface IAssistantReviewBaselineService
{
    Task<LatestAssistantReviewSnapshot?> ReadLatestAsync(
        Guid projectId,
        Guid chapterId,
        EditorContentTarget target,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the first baseline for a turn on the caller's write context. The
    /// caller commits it with the manuscript mutation through the same save.
    /// </summary>
    Task<LatestAssistantReviewSnapshot> CaptureFirstCommittedAsync(
        AppDbContext db,
        Guid projectId,
        Guid chapterId,
        EditorContentTarget target,
        Guid assistantTurnId,
        string beforeManuscriptJson,
        string actionLabel,
        CancellationToken cancellationToken = default);

}

public sealed class AssistantReviewBaselineService(
    IAppDatabaseOperationFactory database) : IAssistantReviewBaselineService
{
    public async Task<LatestAssistantReviewSnapshot?> ReadLatestAsync(
        Guid projectId,
        Guid chapterId,
        EditorContentTarget target,
        CancellationToken cancellationToken = default)
    {
        var targetKey = TargetKey(target);
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var baseline = await operation.Db.AssistantReviewBaselines
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProjectId == projectId
                && item.ChapterId == chapterId
                && item.TargetKey == targetKey, cancellationToken);
        return baseline is null ? null : TryToSnapshot(baseline, chapterId, target);
    }

    public async Task<LatestAssistantReviewSnapshot> CaptureFirstCommittedAsync(
        AppDbContext db,
        Guid projectId,
        Guid chapterId,
        EditorContentTarget target,
        Guid assistantTurnId,
        string beforeManuscriptJson,
        string actionLabel,
        CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("A project ID is required.", nameof(projectId));
        if (chapterId == Guid.Empty)
            throw new ArgumentException("A chapter ID is required.", nameof(chapterId));
        if (assistantTurnId == Guid.Empty)
            throw new ArgumentException("An assistant turn ID is required.", nameof(assistantTurnId));

        var validated = ValidateManuscript(chapterId, beforeManuscriptJson);
        var targetKey = TargetKey(target);
        var existing = await db.AssistantReviewBaselines
            .SingleOrDefaultAsync(item => item.ProjectId == projectId
                && item.ChapterId == chapterId
                && item.TargetKey == targetKey, cancellationToken);

        if (existing is not null && existing.AssistantTurnId == assistantTurnId)
            return TryToSnapshot(existing, chapterId, target)
                ?? throw new InvalidDataException("The stored assistant review baseline is invalid.");

        var now = DateTime.UtcNow;
        if (existing is null)
        {
            existing = new AssistantReviewBaseline
            {
                ProjectId = projectId,
                ChapterId = chapterId,
                TargetKind = target.Kind,
                EditionId = target.EditionId,
                TargetKey = targetKey,
            };
            db.AssistantReviewBaselines.Add(existing);
        }

        existing.TargetKind = target.Kind;
        existing.EditionId = target.EditionId;
        existing.TargetKey = targetKey;
        existing.BeforeManuscriptJson = validated.Json;
        existing.BeforeHash = Hash(validated.Json);
        existing.AssistantTurnId = assistantTurnId;
        existing.ActionLabel = NormalizeLabel(actionLabel);
        existing.CapturedAt = now;
        return ToSnapshot(existing);
    }

    private static (string Json, ManuscriptDocument Document) ValidateManuscript(
        Guid chapterId,
        string manuscriptJson)
    {
        if (string.IsNullOrWhiteSpace(manuscriptJson))
            throw new InvalidDataException("The assistant review baseline manuscript is required.");
        var document = ManuscriptCodec.Deserialize(manuscriptJson);
        if (document.ManuscriptId != chapterId)
        {
            throw new InvalidDataException(
                $"The assistant review baseline manuscript ID does not match chapter {chapterId:D}.");
        }

        // Re-serialization provides one canonical JSON representation and
        // prevents hashes from depending on insignificant input formatting.
        return (ManuscriptCodec.Serialize(document), document);
    }

    private static string TargetKey(EditorContentTarget target) => target switch
    {
        { Kind: EditorContentTargetKind.Core, EditionId: null } => "core",
        { Kind: EditorContentTargetKind.Edition, EditionId: Guid editionId } when editionId != Guid.Empty
            => $"edition:{editionId:N}",
        _ => throw new ArgumentException("The editor content target is invalid.", nameof(target)),
    };

    private static LatestAssistantReviewSnapshot ToSnapshot(AssistantReviewBaseline baseline) =>
        new(
            baseline.BeforeManuscriptJson,
            baseline.BeforeHash,
            baseline.AssistantTurnId,
            baseline.ActionLabel,
            baseline.CapturedAt);

    private static LatestAssistantReviewSnapshot? TryToSnapshot(
        AssistantReviewBaseline baseline,
        Guid chapterId,
        EditorContentTarget target)
    {
        try
        {
            var document = ManuscriptCodec.Deserialize(baseline.BeforeManuscriptJson);
            if (document.ManuscriptId != chapterId
                || baseline.TargetKind != target.Kind
                || baseline.EditionId != target.EditionId
                || !string.Equals(baseline.TargetKey, TargetKey(target), StringComparison.Ordinal)
                || !string.Equals(Hash(baseline.BeforeManuscriptJson), baseline.BeforeHash, StringComparison.OrdinalIgnoreCase))
                return null;
            return ToSnapshot(baseline);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or JsonException)
        {
            return null;
        }
    }

    private static string NormalizeLabel(string? actionLabel) =>
        string.IsNullOrWhiteSpace(actionLabel)
            ? "Assistant change"
            : actionLabel.Trim();

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
