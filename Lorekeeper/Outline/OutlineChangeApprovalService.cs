using System.Text;
using System.Text.Json;
using Lorekeeper.Chapters;
using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Outline;

public sealed class OutlineChangeApprovalService(
    IAiChangeRepository changes,
    IOutlineConversationRepository conversations,
    IActService acts,
    IChapterService chapters,
    IEntityService entities,
    ILogger<OutlineChangeApprovalService> logger) : IOutlineChangeApprovalService
{
    public async Task<IReadOnlyList<AiChangeBatch>> ListPendingBatchesAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await changes.ListPendingBatchesAsync(projectId, cancellationToken);

    public Task<AiChangeBatch?> GetBatchAsync(Guid batchId, CancellationToken cancellationToken = default) =>
        changes.GetBatchAsync(batchId, cancellationToken);

    public async Task ApplyBatchAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        var batch = await changes.GetBatchAsync(batchId, cancellationToken)
            ?? throw new InvalidOperationException($"AI change batch {batchId} not found.");

        try
        {
            foreach (var pendingChange in batch.Changes.OrderBy(changeItem => changeItem.Order).Where(changeItem => changeItem.Status == AiChangeStatus.Pending))
                await ApplyChangeCoreAsync(batch, pendingChange, cancellationToken);
        }
        finally
        {
            UpdateBatchStatus(batch);
            await changes.SaveChangesAsync(CancellationToken.None);
        }
    }

    public async Task ApplyChangeAsync(Guid changeId, CancellationToken cancellationToken = default)
    {
        var change = await changes.GetChangeAsync(changeId, cancellationToken)
            ?? throw new InvalidOperationException($"AI change {changeId} not found.");

        try
        {
            await ApplyChangeCoreAsync(change.Batch, change, cancellationToken);
        }
        finally
        {
            UpdateBatchStatus(change.Batch);
            await changes.SaveChangesAsync(CancellationToken.None);
        }
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
        switch (change.ToolName)
        {
            case "create_act":
            {
                var after = ReadRequired<OutlineActChange>(change.AfterJson);
                await acts.CreateAsync(projectId, after.Title, after.Synopsis, after.Id, cancellationToken);
                break;
            }
            case "update_act":
            {
                var after = ReadRequired<OutlineActChange>(change.AfterJson);
                await acts.UpdateAsync(after.Id, after.Title, after.Synopsis, cancellationToken);
                break;
            }
            case "delete_act":
                await acts.DeleteAsync(ParseResourceGuid(change), cancellationToken);
                break;
            case "reorder_acts":
            {
                var after = ReadRequired<OutlineReorderChange>(change.AfterJson);
                await acts.ReorderAsync(projectId, after.OrderedIds, cancellationToken);
                break;
            }
            case "create_chapter":
            {
                var after = ReadRequired<OutlineChapterChange>(change.AfterJson);
                await chapters.CreateAsync(projectId, after.ActId, after.Title, after.Synopsis, after.Id, cancellationToken);
                break;
            }
            case "update_chapter":
            {
                var after = ReadRequired<OutlineChapterChange>(change.AfterJson);
                await chapters.UpdateAsync(after.Id, after.Title, body: null, after.Synopsis, new ChapterActAssignment(after.ActId), cancellationToken);
                break;
            }
            case "edit_chapter":
            {
                var after = ReadRequired<ChapterBodyChange>(change.AfterJson);
                await chapters.UpdateAsync(after.Id, body: after.Body, cancellationToken: cancellationToken);
                break;
            }
            case "delete_chapter":
                await chapters.DeleteAsync(ParseResourceGuid(change), cancellationToken);
                break;
            case "reorder_chapters":
            {
                var after = ReadRequired<OutlineReorderChange>(change.AfterJson);
                await chapters.ReorderAsync(projectId, after.ParentId, after.OrderedIds, cancellationToken);
                break;
            }
            case "create_entity":
            {
                var after = ReadRequired<OutlineEntityChange>(change.AfterJson);
                await entities.CreateAsync(projectId, after.Type, after.Name, after.Properties, after.ParentId, after.Order, after.Id, cancellationToken);
                break;
            }
            case "update_entity":
            {
                var before = ReadOptional<OutlineEntityChange>(change.BeforeJson);
                var after = ReadRequired<OutlineEntityChange>(change.AfterJson);
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
                var after = ReadRequired<OutlineEntityReorderChange>(change.AfterJson);
                await entities.ReorderAsync(projectId, after.Type, after.ParentId, after.OrderedIds, cancellationToken);
                break;
            }
            case "link_entities":
            {
                var after = ReadRequired<OutlineEntityLinkChange>(change.AfterJson);
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

        var order = await conversations.GetMaxOrderAsync(batch.ConversationId, cancellationToken) + 1;
        await conversations.AddMessageAsync(new OutlineMessage
        {
            ConversationId = batch.ConversationId,
            Order = order,
            Role = OutlineMessageRole.System,
            Content = builder.ToString().TrimEnd(),
            Status = OutlineMessageStatus.Completed,
        }, cancellationToken);
    }

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
        JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Default)
        ?? throw new InvalidOperationException($"Could not deserialize {typeof(T).Name} payload.");

    private static T? ReadOptional<T>(string json) =>
        string.IsNullOrWhiteSpace(json) || json == "null"
            ? default
            : JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Default);

    private static IReadOnlyList<Guid> ReadGuidList(string json) =>
        string.IsNullOrWhiteSpace(json) || json == "[]"
            ? []
            : JsonSerializer.Deserialize<List<Guid>>(json, JsonSerializerOptions.Default) ?? [];

}
