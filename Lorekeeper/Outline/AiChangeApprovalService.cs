using System.Text;
using System.Text.Json;
using System.Runtime.ExceptionServices;
using Lorekeeper.Chapters;
using Lorekeeper.Context;
using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Outline;

public sealed class AiChangeApprovalService(
    IAiChangeRepository changes,
    IOutlineConversationRepository outlineConversations,
    IEditorConversationRepository editorConversations,
    IResearchConversationRepository researchConversations,
    IActService acts,
    IChapterService chapters,
    IEntityService entities,
    IVectorIndexWorkCoordinator indexWork,
    ILogger<AiChangeApprovalService> logger) : IAiChangeApprovalService
{
    private static readonly JsonSerializerOptions ChangePayloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<IReadOnlyList<AiChangeBatch>> ListPendingBatchesAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await changes.ListPendingBatchesAsync(projectId, cancellationToken);

    public Task<AiChangeBatch?> GetBatchAsync(Guid batchId, CancellationToken cancellationToken = default) =>
        changes.GetBatchAsync(batchId, cancellationToken);

    public async Task SaveReviewDraftAsync(Guid changeId, string? draftAfterJson, string? reviewStateJson, CancellationToken cancellationToken = default)
    {
        var change = await changes.GetChangeAsync(changeId, cancellationToken)
            ?? throw new InvalidOperationException($"AI change {changeId} not found.");
        if (change.Status != AiChangeStatus.Pending)
            throw new InvalidOperationException("Only pending AI changes can be edited in review.");

        if (string.IsNullOrWhiteSpace(draftAfterJson))
        {
            change.DraftAfterJson = null;
            change.ReviewStateJson = null;
        }
        else
        {
            if (!AiChangeReviewDrafts.TryValidateDraftAfterJson(change, draftAfterJson, out var error))
                throw new InvalidOperationException(error ?? "The review draft is not valid for this AI change.");

            change.DraftAfterJson = draftAfterJson;
            change.ReviewStateJson = string.IsNullOrWhiteSpace(reviewStateJson) ? null : reviewStateJson;
        }

        change.UpdatedAt = DateTime.UtcNow;
        changes.UpdateChange(change);
        await changes.SaveChangesAsync(cancellationToken);
    }

    public Task ClearReviewDraftAsync(Guid changeId, CancellationToken cancellationToken = default) =>
        SaveReviewDraftAsync(changeId, draftAfterJson: null, reviewStateJson: null, cancellationToken);

    public async Task ApplyBatchAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        var batch = await changes.GetBatchAsync(batchId, cancellationToken)
            ?? throw new InvalidOperationException($"AI change batch {batchId} not found.");

        await using var indexDeferral = indexWork.BeginDeferral();
        ExceptionDispatchInfo? capturedException = null;
        try
        {
            foreach (var pendingChange in batch.Changes.OrderBy(changeItem => changeItem.Order).Where(changeItem => changeItem.Status == AiChangeStatus.Pending))
                await ApplyChangeCoreAsync(batch, pendingChange, cancellationToken);
        }
        catch (Exception ex)
        {
            capturedException = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            UpdateBatchStatus(batch);
            await changes.SaveChangesAsync(CancellationToken.None);
            await indexDeferral.FlushAsync(CancellationToken.None);
        }

        capturedException?.Throw();
    }

    public async Task ApplyChangeAsync(Guid changeId, CancellationToken cancellationToken = default)
    {
        var change = await changes.GetChangeAsync(changeId, cancellationToken)
            ?? throw new InvalidOperationException($"AI change {changeId} not found.");

        await using var indexDeferral = indexWork.BeginDeferral();
        ExceptionDispatchInfo? capturedException = null;
        try
        {
            await ApplyChangeCoreAsync(change.Batch, change, cancellationToken);
        }
        catch (Exception ex)
        {
            capturedException = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            UpdateBatchStatus(change.Batch);
            await changes.SaveChangesAsync(CancellationToken.None);
            await indexDeferral.FlushAsync(CancellationToken.None);
        }

        capturedException?.Throw();
    }

    public async Task ApplyChangesAsync(IReadOnlyCollection<Guid> changeIds, CancellationToken cancellationToken = default)
    {
        if (changeIds.Count == 0) return;

        var selectedChanges = new List<AiChange>();
        foreach (var changeId in changeIds.Distinct())
        {
            var change = await changes.GetChangeAsync(changeId, cancellationToken)
                ?? throw new InvalidOperationException($"AI change {changeId} not found.");
            selectedChanges.Add(change);
        }

        if (selectedChanges.Count == 0) return;

        var touchedBatches = selectedChanges
            .Select(change => change.Batch)
            .GroupBy(batch => batch.Id)
            .Select(group => group.First())
            .ToList();

        await using var indexDeferral = indexWork.BeginDeferral();
        ExceptionDispatchInfo? capturedException = null;
        try
        {
            foreach (var change in selectedChanges
                .OrderBy(change => change.Batch.CreatedAt)
                .ThenBy(change => change.Order))
            {
                await ApplyChangeCoreAsync(change.Batch, change, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            capturedException = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            foreach (var batch in touchedBatches)
                UpdateBatchStatus(batch);
            await changes.SaveChangesAsync(CancellationToken.None);
            await indexDeferral.FlushAsync(CancellationToken.None);
        }

        capturedException?.Throw();
    }

    public async Task RejectBatchAsync(Guid batchId, string? message, CancellationToken cancellationToken = default)
    {
        var batch = await changes.GetBatchAsync(batchId, cancellationToken)
            ?? throw new InvalidOperationException($"AI change batch {batchId} not found.");

        var rejected = batch.Changes
            .Where(changeItem => changeItem.Status == AiChangeStatus.Pending)
            .OrderBy(changeItem => changeItem.Order)
            .ToList();
        foreach (var changeItem in rejected)
            MarkRejected(changeItem, message);

        if (rejected.Count > 0)
            await AppendRejectionSystemMessageAsync(batch, rejected, message, cancellationToken);

        UpdateBatchStatus(batch);
        await changes.SaveChangesAsync(cancellationToken);
    }

    public async Task RejectChangeAsync(Guid changeId, string? message, CancellationToken cancellationToken = default)
    {
        var change = await changes.GetChangeAsync(changeId, cancellationToken)
            ?? throw new InvalidOperationException($"AI change {changeId} not found.");
        if (change.Status != AiChangeStatus.Pending)
            return;

        var rejected = CollectDependentPendingChanges(change.Batch, change.Id);
        foreach (var changeItem in rejected)
            MarkRejected(changeItem, message);

        await AppendRejectionSystemMessageAsync(change.Batch, rejected, message, cancellationToken);
        UpdateBatchStatus(change.Batch);
        await changes.SaveChangesAsync(cancellationToken);
    }

    private async Task ApplyChangeCoreAsync(AiChangeBatch batch, AiChange change, CancellationToken cancellationToken)
    {
        if (change.Status == AiChangeStatus.Applied) return;
        if (change.Status == AiChangeStatus.Rejected)
            throw new InvalidOperationException($"AI change {change.Id} was rejected and cannot be applied.");
        if (change.Status == AiChangeStatus.Conflict)
            throw new InvalidOperationException($"AI change {change.Id} is conflicted and cannot be applied.");

        foreach (var dependencyId in ReadGuidList(change.DependsOnChangeIdsJson))
        {
            var dependency = batch.Changes.FirstOrDefault(changeItem => changeItem.Id == dependencyId)
                ?? throw new InvalidOperationException($"AI change dependency {dependencyId} was not found.");
            await ApplyChangeCoreAsync(batch, dependency, cancellationToken);
        }

        try
        {
            await ApplyStoredToolChangeAsync(batch.ProjectId, change, cancellationToken);
            change.Status = AiChangeStatus.Applied;
            change.UpdatedAt = DateTime.UtcNow;
            change.ResolvedAt = DateTime.UtcNow;
            changes.UpdateChange(change);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI change {ChangeId} could not be applied", change.Id);
            change.Status = AiChangeStatus.Conflict;
            change.ErrorMessage = ex.Message;
            change.UpdatedAt = DateTime.UtcNow;
            changes.UpdateChange(change);
            throw;
        }
    }

    private async Task ApplyStoredToolChangeAsync(Guid projectId, AiChange change, CancellationToken cancellationToken)
    {
        var afterJson = AiChangeReviewDrafts.EffectiveAfterJson(change);
        switch (change.ToolName)
        {
            case "create_act":
            {
                var after = ReadRequired<OutlineActChange>(afterJson);
                await acts.CreateAsync(projectId, after.Title, after.Synopsis, after.Id, cancellationToken);
                break;
            }
            case "update_act":
            {
                var after = ReadRequired<OutlineActChange>(afterJson);
                await acts.UpdateAsync(after.Id, after.Title, after.Synopsis, cancellationToken);
                break;
            }
            case "delete_act":
                await acts.DeleteAsync(ParseResourceGuid(change), cancellationToken);
                break;
            case "reorder_acts":
            {
                var after = ReadRequired<OutlineReorderChange>(afterJson);
                await acts.ReorderAsync(projectId, after.OrderedIds, cancellationToken);
                break;
            }
            case "create_chapter":
            {
                var after = ReadRequired<OutlineChapterChange>(afterJson);
                await chapters.CreateAsync(projectId, after.ActId, after.Title, after.Synopsis, after.Id, cancellationToken);
                break;
            }
            case "update_chapter":
            {
                var after = ReadRequired<OutlineChapterChange>(afterJson);
                await chapters.UpdateAsync(after.Id, after.Title, body: null, after.Synopsis, new ChapterActAssignment(after.ActId), cancellationToken);
                break;
            }
            case "edit_chapter":
            {
                var after = ReadRequired<ChapterBodyChange>(afterJson);
                await chapters.UpdateAsync(after.Id, body: after.Body, cancellationToken: cancellationToken);
                break;
            }
            case "delete_chapter":
                await chapters.DeleteAsync(ParseResourceGuid(change), cancellationToken);
                break;
            case "reorder_chapters":
            {
                var after = ReadRequired<OutlineReorderChange>(afterJson);
                await chapters.ReorderAsync(projectId, after.ParentId, after.OrderedIds, cancellationToken);
                break;
            }
            case "create_entity":
            {
                var after = ReadRequired<OutlineEntityChange>(afterJson);
                await entities.CreateAsync(projectId, after.Type, after.Name, after.Properties, after.ParentId, after.Order, after.Id, cancellationToken);
                break;
            }
            case "update_entity":
            {
                var before = ReadOptional<OutlineEntityChange>(change.BeforeJson);
                var after = ReadRequired<OutlineEntityChange>(afterJson);
                var propertiesToRemove = before?.Properties.Keys
                    .Where(key => !after.Properties.ContainsKey(key))
                    .ToArray();
                await entities.UpdateAsync(projectId, after.Id, after.Name, after.Properties, propertiesToRemove, cancellationToken);
                break;
            }
            case "delete_entity":
                await entities.DeleteAsync(projectId, ParseResourceGuid(change), cancellationToken);
                break;
            case "reorder_entities":
            {
                var after = ReadRequired<OutlineEntityReorderChange>(afterJson);
                await entities.ReorderAsync(projectId, after.Type, after.ParentId, after.OrderedIds, cancellationToken);
                break;
            }
            case "link_entities":
            {
                var after = ReadRequired<OutlineEntityLinkChange>(afterJson);
                await entities.LinkAsync(projectId, after.FromId, after.ToId, after.EdgeType, after.Properties, cancellationToken);
                break;
            }
            default:
                throw new InvalidOperationException($"Unsupported AI change tool '{change.ToolName}'.");
        }
    }

    private static List<AiChange> CollectDependentPendingChanges(AiChangeBatch batch, Guid rejectedRootId)
    {
        var rejectedIds = new HashSet<Guid> { rejectedRootId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var candidate in batch.Changes.Where(changeItem => changeItem.Status == AiChangeStatus.Pending))
            {
                if (rejectedIds.Contains(candidate.Id)) continue;
                var dependencies = ReadGuidList(candidate.DependsOnChangeIdsJson);
                if (dependencies.Any(rejectedIds.Contains))
                {
                    rejectedIds.Add(candidate.Id);
                    changed = true;
                }
            }
        }

        return batch.Changes
            .Where(changeItem => rejectedIds.Contains(changeItem.Id) && changeItem.Status == AiChangeStatus.Pending)
            .OrderBy(changeItem => changeItem.Order)
            .ToList();
    }

    private static void MarkRejected(AiChange change, string? message)
    {
        change.Status = AiChangeStatus.Rejected;
        change.RejectionMessage = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        change.UpdatedAt = DateTime.UtcNow;
        change.ResolvedAt = DateTime.UtcNow;
    }

    private async Task AppendRejectionSystemMessageAsync(
        AiChangeBatch batch,
        IReadOnlyList<AiChange> rejected,
        string? message,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine("The user rejected one or more AI tool changes from the prior turn. Update your understanding to match this correction.");
        if (!string.IsNullOrWhiteSpace(message))
            builder.AppendLine($"User rejection note: {message.Trim()}");
        builder.AppendLine("Rejected changes:");
        foreach (var changeItem in rejected)
        {
            builder.Append("- ").Append(changeItem.ToolName);
            if (!string.IsNullOrWhiteSpace(changeItem.Summary))
                builder.Append(": ").Append(changeItem.Summary);
            if (changeItem.Id != rejected[0].Id)
                builder.Append(" (also rejected because it depended on an earlier rejected change)");
            builder.AppendLine();
        }

        var correction = builder.ToString().TrimEnd();
        switch (batch.ConversationKind)
        {
            case AiChangeConversationKind.Outline:
            {
                if (await outlineConversations.ExistsAsync(batch.ConversationId, cancellationToken))
                {
                    await AddOutlineRejectionMessageAsync(batch.ConversationId, correction, cancellationToken);
                    break;
                }

                if (await editorConversations.ExistsAsync(batch.ConversationId, cancellationToken))
                {
                    logger.LogWarning(
                        "AI change batch {BatchId} was marked as Outline but conversation {ConversationId} is an editor conversation; routing rejection feedback to editor chat.",
                        batch.Id,
                        batch.ConversationId);
                    batch.ConversationKind = AiChangeConversationKind.Editor;
                    changes.UpdateBatch(batch);
                    await AddEditorRejectionMessageAsync(batch.ConversationId, correction, cancellationToken);
                    break;
                }

                LogMissingConversation(batch);
                break;
            }
            case AiChangeConversationKind.Editor:
            {
                if (await editorConversations.ExistsAsync(batch.ConversationId, cancellationToken))
                {
                    await AddEditorRejectionMessageAsync(batch.ConversationId, correction, cancellationToken);
                    break;
                }

                if (await outlineConversations.ExistsAsync(batch.ConversationId, cancellationToken))
                {
                    logger.LogWarning(
                        "AI change batch {BatchId} was marked as Editor but conversation {ConversationId} is an outline conversation; routing rejection feedback to outline chat.",
                        batch.Id,
                        batch.ConversationId);
                    batch.ConversationKind = AiChangeConversationKind.Outline;
                    changes.UpdateBatch(batch);
                    await AddOutlineRejectionMessageAsync(batch.ConversationId, correction, cancellationToken);
                    break;
                }

                LogMissingConversation(batch);
                break;
            }
            case AiChangeConversationKind.Research:
            {
                if (await researchConversations.ExistsAsync(batch.ConversationId, cancellationToken))
                {
                    await AddResearchRejectionMessageAsync(batch.ConversationId, correction, cancellationToken);
                    break;
                }

                LogMissingConversation(batch);
                break;
            }
            default:
                throw new InvalidOperationException($"Unsupported AI change conversation kind '{batch.ConversationKind}'.");
        }
    }

    private async Task AddOutlineRejectionMessageAsync(
        Guid conversationId,
        string correction,
        CancellationToken cancellationToken)
    {
        var order = await outlineConversations.GetMaxOrderAsync(conversationId, cancellationToken) + 1;
        await outlineConversations.AddMessageAsync(new OutlineMessage
        {
            ConversationId = conversationId,
            Order = order,
            Role = OutlineMessageRole.System,
            Content = correction,
            Status = OutlineMessageStatus.Completed,
        }, cancellationToken);
    }

    private async Task AddEditorRejectionMessageAsync(
        Guid conversationId,
        string correction,
        CancellationToken cancellationToken)
    {
        var order = await editorConversations.GetMaxOrderAsync(conversationId, cancellationToken) + 1;
        await editorConversations.AddMessageAsync(new EditorMessage
        {
            ConversationId = conversationId,
            Order = order,
            Role = EditorMessageRole.System,
            Content = correction,
            Status = EditorMessageStatus.Completed,
        }, cancellationToken);
    }

    private async Task AddResearchRejectionMessageAsync(
        Guid conversationId,
        string correction,
        CancellationToken cancellationToken)
    {
        var order = await researchConversations.GetMaxOrderAsync(conversationId, cancellationToken) + 1;
        await researchConversations.AddMessageAsync(new ResearchMessage
        {
            ConversationId = conversationId,
            Order = order,
            Role = ResearchMessageRole.System,
            Content = correction,
            Status = ResearchMessageStatus.Completed,
        }, cancellationToken);
    }

    private void LogMissingConversation(AiChangeBatch batch) =>
        logger.LogWarning(
            "Skipped rejection feedback message for AI change batch {BatchId} because conversation {ConversationId} ({ConversationKind}) no longer exists. Rejection status will still be saved.",
            batch.Id,
            batch.ConversationId,
            batch.ConversationKind);

    private void UpdateBatchStatus(AiChangeBatch batch)
    {
        if (batch.Changes.All(changeItem => changeItem.Status != AiChangeStatus.Pending))
        {
            batch.Status = AiChangeBatchStatus.Resolved;
            batch.ResolvedAt = DateTime.UtcNow;
        }

        batch.UpdatedAt = DateTime.UtcNow;
        changes.UpdateBatch(batch);
    }

    private static Guid ParseResourceGuid(AiChange change)
    {
        var parts = change.ResourceId.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var guidText = parts.Length == 0 ? change.ResourceId : parts[^1];
        if (!Guid.TryParse(guidText, out var result))
            throw new InvalidOperationException($"AI change {change.Id} does not have a Guid resource id.");
        return result;
    }

    private static T ReadRequired<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, ChangePayloadJsonOptions)
        ?? throw new InvalidOperationException($"Could not deserialize {typeof(T).Name} payload.");

    private static T? ReadOptional<T>(string json) =>
        string.IsNullOrWhiteSpace(json) || json == "null"
            ? default
            : JsonSerializer.Deserialize<T>(json, ChangePayloadJsonOptions);

    private static IReadOnlyList<Guid> ReadGuidList(string json) =>
        string.IsNullOrWhiteSpace(json) || json == "[]"
            ? []
            : JsonSerializer.Deserialize<List<Guid>>(json, JsonSerializerOptions.Default) ?? [];
}
