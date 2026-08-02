using System.Text;
using System.Text.Json;
using System.Runtime.ExceptionServices;
using Lorekeeper.Chapters;
using Lorekeeper.Composition;
using Lorekeeper.Context;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Images;
using Lorekeeper.Manuscripts;
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
    IManuscriptService manuscripts,
    ICompositionService compositions,
    IManuscriptStyleService manuscriptStyles,
    IEntityService entities,
    IVectorIndexWorkCoordinator indexWork,
    IEntityVisualExampleService entityVisualExamples,
    IProjectImageService projectImages,
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

    public async Task PrepareChapterBodyLineReviewAsync(Guid projectId, Guid chapterId, CancellationToken cancellationToken = default)
    {
        var chapter = await chapters.GetAsync(chapterId, cancellationToken)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");
        if (chapter.ProjectId != projectId)
            throw new InvalidOperationException("The selected chapter does not belong to this project.");

        var pendingBatches = await changes.ListPendingBatchesAsync(projectId, cancellationToken);
        var chapterBodyChanges = CurrentPendingChapterManuscriptChanges(pendingBatches, chapterId);
        if (chapterBodyChanges.Count == 0) return;

        EnsureNoExternalPendingDependencies(chapterBodyChanges, pendingBatches);

        var aggregate = chapterBodyChanges[^1];
        var finalAfter = ReadOptional<ChapterManuscriptChange>(AiChangeReviewDrafts.EffectiveAfterJson(aggregate))
            ?? throw new InvalidOperationException("The active chapter body review change no longer has a proposed body.");
        if (!ManuscriptCodec.IsPlainTextOnly(chapter.Manuscript)
            || !ManuscriptCodec.IsPlainTextOnly(finalAfter.Manuscript))
        {
            throw new InvalidOperationException(
                "Line-by-line review is unavailable for semantically formatted manuscripts. "
                + "Use the pending changes review to keep or reject the complete structured manuscript change.");
        }

        var now = DateTime.UtcNow;
        foreach (var folded in chapterBodyChanges.Take(chapterBodyChanges.Count - 1))
        {
            folded.Status = AiChangeStatus.Superseded;
            folded.DraftAfterJson = null;
            folded.ReviewStateJson = null;
            folded.UpdatedAt = now;
            folded.ResolvedAt = now;
            changes.UpdateChange(folded);
        }

        var rebasedBefore = Change(chapter);
        var proposedDocument = finalAfter.Manuscript with
        {
            ManuscriptId = chapter.Id,
            Revision = checked(chapter.ManuscriptRevision + 1),
        };
        var rebasedAfter = finalAfter with
        {
            Id = chapter.Id,
            Title = chapter.Title,
            Revision = proposedDocument.Revision,
            ManuscriptJson = ManuscriptCodec.Serialize(proposedDocument),
        };

        aggregate.BeforeJson = Serialize(rebasedBefore);
        aggregate.AfterJson = Serialize(rebasedAfter);
        aggregate.DraftAfterJson = null;
        aggregate.ReviewStateJson = BuildReviewStateJson();
        aggregate.DependsOnChangeIdsJson = "[]";
        aggregate.Status = ManuscriptCodec.ContentEquals(chapter.Manuscript, rebasedAfter.Manuscript)
            ? AiChangeStatus.Resolved
            : AiChangeStatus.Pending;
        aggregate.UpdatedAt = now;
        aggregate.ResolvedAt = aggregate.Status == AiChangeStatus.Resolved ? now : null;
        changes.UpdateChange(aggregate);

        foreach (var batch in chapterBodyChanges.Select(change => change.Batch).DistinctBy(batch => batch.Id))
            UpdateBatchStatus(batch);

        await changes.SaveChangesAsync(cancellationToken);
    }

    public async Task ResolveChapterBodyReviewLineAsync(
        Guid projectId,
        Guid chapterId,
        ChapterBodyReviewLineResolution request,
        CancellationToken cancellationToken = default)
    {
        await PrepareChapterBodyLineReviewAsync(projectId, chapterId, cancellationToken);

        var pendingBatches = await changes.ListPendingBatchesAsync(projectId, cancellationToken);
        var chapterBodyChanges = CurrentPendingChapterManuscriptChanges(pendingBatches, chapterId);
        if (chapterBodyChanges.Count == 0)
            throw new InvalidOperationException("There are no pending chapter-body lines left to review.");

        if (chapterBodyChanges.Count > 1)
            throw new InvalidOperationException("The active chapter body review could not be normalized. Open the pending changes modal to review these changes.");

        var aggregate = chapterBodyChanges[0];
        if (aggregate.Id != request.ChangeId)
            throw new InvalidOperationException("The chapter-body review changed. Refresh Review mode and try again.");

        var chapter = await chapters.GetAsync(chapterId, cancellationToken)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");
        if (chapter.ProjectId != projectId)
            throw new InvalidOperationException("The selected chapter does not belong to this project.");

        var proposed = ReadOptional<ChapterManuscriptChange>(AiChangeReviewDrafts.EffectiveAfterJson(aggregate))
            ?? throw new InvalidOperationException("The active chapter body review change no longer has a proposed body.");
        if (!ManuscriptCodec.IsPlainTextOnly(chapter.Manuscript)
            || !ManuscriptCodec.IsPlainTextOnly(proposed.Manuscript))
        {
            throw new InvalidOperationException(
                "Line-by-line review is unavailable for semantically formatted manuscripts. "
                + "Keep or reject the complete structured manuscript change.");
        }

        if (!AiChangeReviewDiffBuilder.TryBuild(aggregate, out var diff))
            throw new InvalidOperationException("The active chapter body review no longer contains a text diff.");

        var target = FindChapterBodyReviewLineTarget(aggregate.Id, diff, request)
            ?? throw new InvalidOperationException("This review line changed. Refresh Review mode and try again.");

        var currentLines = ChapterFormatting.SplitLines(chapter.PlainText);
        var proposedLines = ChapterFormatting.SplitLines(proposed.PlainText);
        var editedText = request.Action == ChapterBodyReviewLineAction.Edit
            ? NormalizeSingleLineEditText(request.EditedText)
            : null;

        ResolveLineTarget(target, request.Action, editedText, currentLines, proposedLines);

        var newCurrentBody = ChapterFormatting.JoinLines(currentLines);
        var newProposedPlainText = ChapterFormatting.JoinLines(proposedLines);

        if (!string.Equals(newCurrentBody, chapter.PlainText, StringComparison.Ordinal))
        {
            await manuscripts.ReplaceDocumentAsync(
                chapter.Id,
                chapter.ManuscriptRevision,
                ManuscriptCodec.ReparsePreservingBlockIds(chapter.Manuscript, newCurrentBody),
                cancellationToken);
            chapter = await chapters.ReloadFromStoreAsync(chapter.Id, cancellationToken)
                ?? throw new InvalidOperationException($"Chapter {chapter.Id} not found after review save.");
        }

        aggregate.BeforeJson = Serialize(Change(chapter));
        var reparsedProposal = ManuscriptCodec.ReparsePreservingBlockIds(proposed.Manuscript, newProposedPlainText) with
        {
            ManuscriptId = chapter.Id,
            Revision = checked(chapter.ManuscriptRevision + 1),
        };
        aggregate.AfterJson = Serialize(proposed with
        {
            Id = chapter.Id,
            Title = chapter.Title,
            Revision = reparsedProposal.Revision,
            ManuscriptJson = ManuscriptCodec.Serialize(reparsedProposal),
        });
        aggregate.DraftAfterJson = null;
        aggregate.ReviewStateJson = BuildReviewStateJson();
        aggregate.UpdatedAt = DateTime.UtcNow;
        aggregate.ResolvedAt = null;

        if (ManuscriptCodec.ContentEquals(chapter.Manuscript, reparsedProposal))
        {
            aggregate.Status = AiChangeStatus.Resolved;
            aggregate.ResolvedAt = DateTime.UtcNow;
        }
        else
        {
            aggregate.Status = AiChangeStatus.Pending;
        }

        changes.UpdateChange(aggregate);

        if (request.Action == ChapterBodyReviewLineAction.Reject && !string.IsNullOrWhiteSpace(request.RejectionMessage))
            await AppendLineRejectionSystemMessageAsync(aggregate, chapter, target, request.RejectionMessage, cancellationToken);

        UpdateBatchStatus(aggregate.Batch);
        await changes.SaveChangesAsync(cancellationToken);
    }

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
        if (change.Status is AiChangeStatus.Applied or AiChangeStatus.Superseded or AiChangeStatus.Resolved) return;
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
                await chapters.UpdateAsync(after.Id, after.Title, after.Synopsis, new ChapterActAssignment(after.ActId), cancellationToken);
                break;
            }
            case "insert_outline_designed_page":
            {
                var before = ReadRequired<ChapterManuscriptChange>(change.BeforeJson);
                var after = ReadRequired<ChapterManuscriptChange>(afterJson);
                var current = await chapters.GetAsync(after.Id, cancellationToken)
                    ?? throw new InvalidOperationException($"Chapter {after.Id} not found.");
                if (current.ManuscriptRevision != before.Revision
                    || !string.Equals(current.ManuscriptJson, before.ManuscriptJson, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The chapter body changed after this Designed Page was staged. Reject this change and rerun it against the current manuscript.");
                }

                var arguments = ReadRequired<DesignedPageToolArguments>(change.ArgumentsJson);
                if (arguments.ChapterId != after.Id
                    || arguments.ExpectedRevision != before.Revision)
                {
                    throw new InvalidOperationException("The staged Designed Page arguments do not match the reviewed manuscript revision.");
                }

                var beforeIds = before.Manuscript.Content.Select(block => block.Id).ToHashSet(StringComparer.Ordinal);
                var addedBlocks = after.Manuscript.Content
                    .Where(block => !beforeIds.Contains(block.Id))
                    .ToList();
                if (addedBlocks is not [var added]
                    || added.Type != ManuscriptBlockType.DesignedPage
                    || added.PageCompositionId is not Guid compositionId)
                {
                    throw new InvalidOperationException("The reviewed change must add exactly one valid Designed Page block.");
                }

                var projected = ManuscriptOperations.Apply(
                    before.Manuscript,
                    [new InsertManuscriptBlock(
                        arguments.BlockIndex,
                        ManuscriptBlockType.DesignedPage,
                        string.Empty,
                        ManuscriptStyleRoles.DesignedPage,
                        PageCompositionId: compositionId,
                        BlockId: added.Id)]).Document;
                if (projected.Revision != after.Revision
                    || !ManuscriptCodec.ContentEquals(projected, after.Manuscript))
                {
                    throw new InvalidOperationException("The reviewed Designed Page structure no longer matches its staged creation request.");
                }

                await compositions.CreateDesignedPageAsync(
                    projectId,
                    after.Id,
                    arguments.BlockIndex,
                    arguments.Name,
                    arguments.EditionId,
                    before.Revision,
                    new DesignedPageIdentity(compositionId, added.Id),
                    cancellationToken);
                break;
            }
            case "apply_manuscript_operations":
            case "apply_assigned_manuscript_operations":
            case "insert_manuscript_figure":
            case "patch_manuscript_figure":
            case "insert_outline_figure":
            case "patch_outline_figure":
            {
                var before = ReadOptional<ChapterManuscriptChange>(change.BeforeJson);
                var after = ReadRequired<ChapterManuscriptChange>(afterJson);
                if (before is not null)
                {
                    var current = await chapters.GetAsync(after.Id, cancellationToken)
                        ?? throw new InvalidOperationException($"Chapter {after.Id} not found.");
                    if (current.ManuscriptRevision != before.Revision
                        || !string.Equals(current.ManuscriptJson, before.ManuscriptJson, StringComparison.Ordinal))
                        throw new InvalidOperationException("The chapter body changed after this AI edit was staged. Reject this change and rerun the edit against the current chapter text.");
                }
                await manuscripts.ReplaceDocumentAsync(
                    after.Id,
                    before?.Revision ?? checked(after.Revision - 1),
                    after.Manuscript,
                    cancellationToken);
                break;
            }
            case "upsert_manuscript_style":
            {
                var staged = ReadRequired<ManuscriptStyleChange>(afterJson);
                var input = staged.After
                    ?? throw new InvalidOperationException("The staged named-style update has no target state.");
                await manuscriptStyles.UpsertAsync(projectId, input, cancellationToken);
                break;
            }
            case "delete_manuscript_style":
            {
                var staged = ReadRequired<ManuscriptStyleChange>(afterJson);
                var before = staged.Before
                    ?? throw new InvalidOperationException("The staged named-style deletion has no source state.");
                await manuscriptStyles.DeleteAsync(
                    projectId,
                    before.Id,
                    before.Revision,
                    cancellationToken);
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
            case "attach_entity_canonical_reference":
            case "crop_project_image":
            {
                var after = ReadRequired<EntityVisualChange>(afterJson);
                await entityVisualExamples.AttachAsync(projectId, after.EntityId!.Value, after.ImageId!.Value, after.Label, EntityVisualExampleOrigin.Agent, cancellationToken: cancellationToken);
                break;
            }
            case "update_entity_canonical_reference":
            {
                var after = ReadRequired<EntityVisualChange>(afterJson);
                await entityVisualExamples.UpdateAsync(projectId, after.ExampleId!.Value, after.Label, after.SortOrder, cancellationToken: cancellationToken);
                break;
            }
            case "detach_entity_canonical_reference":
            {
                var before = ReadRequired<EntityVisualChange>(change.BeforeJson);
                await entityVisualExamples.DetachAsync(projectId, before.ExampleId!.Value, cancellationToken);
                break;
            }
            case "import_web_image_as_entity_reference":
            {
                var after = ReadRequired<EntityVisualChange>(afterJson);
                var sourceImage = await entityVisualExamples.PromoteCandidateAsync(projectId, after.CandidateId!.Value, cancellationToken);
                var referenceImage = after.Crop is null
                    ? sourceImage
                    : await projectImages.CropAsync(projectId, sourceImage.Id, new ProjectImageCropRequest(
                        after.Crop,
                        after.CropFileName,
                        after.CropAltText), cancellationToken);
                foreach (var target in after.Targets ?? [])
                    await entityVisualExamples.AttachAsync(
                        projectId,
                        target.EntityId,
                        referenceImage.Id,
                        target.Label,
                        EntityVisualExampleOrigin.Research,
                        after.CandidateId,
                        cancellationToken);
                break;
            }
            default:
                throw new InvalidOperationException($"Unsupported AI change tool '{change.ToolName}'.");
        }
    }

    private static IReadOnlyList<AiChange> CurrentPendingChapterManuscriptChanges(IReadOnlyList<AiChangeBatch> batches, Guid chapterId) =>
        batches
            .SelectMany(batch => batch.Changes)
            .Where(change => change.Status == AiChangeStatus.Pending && IsChapterManuscriptChangeFor(change, chapterId))
            .OrderBy(change => change.Batch.CreatedAt)
            .ThenBy(change => change.Order)
            .ToList();

    private static bool IsChapterManuscriptChangeFor(AiChange change, Guid chapterId)
    {
        if (!string.Equals(change.ResourceKind, "ChapterManuscript", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TryReadChapterManuscriptChange(change.BeforeJson)?.Id == chapterId
            || TryReadChapterManuscriptChange(AiChangeReviewDrafts.EffectiveAfterJson(change))?.Id == chapterId;
    }

    private static ChapterManuscriptChange? TryReadChapterManuscriptChange(string json)
    {
        try
        {
            return ReadOptional<ChapterManuscriptChange>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void EnsureNoExternalPendingDependencies(IReadOnlyList<AiChange> chapterBodyChanges, IReadOnlyList<AiChangeBatch> pendingBatches)
    {
        var chapterBodyChangeIds = chapterBodyChanges.Select(change => change.Id).ToHashSet();
        var pendingChanges = pendingBatches
            .SelectMany(batch => batch.Changes)
            .Where(change => change.Status == AiChangeStatus.Pending)
            .ToList();
        var pendingById = pendingChanges.ToDictionary(change => change.Id);

        foreach (var change in chapterBodyChanges)
        {
            foreach (var dependencyId in ReadGuidList(change.DependsOnChangeIdsJson))
            {
                if (chapterBodyChangeIds.Contains(dependencyId)) continue;
                if (!pendingById.TryGetValue(dependencyId, out var dependency)) continue;

                var dependencyName = string.IsNullOrWhiteSpace(dependency.Summary)
                    ? dependency.ToolName
                    : dependency.Summary;
                throw new InvalidOperationException(
                    $"This chapter body review depends on pending non-body change '{dependencyName}'. Open the pending changes modal and resolve that dependency first.");
            }
        }

        foreach (var dependent in pendingChanges.Where(change => !chapterBodyChangeIds.Contains(change.Id)))
        {
            if (!ReadGuidList(dependent.DependsOnChangeIdsJson).Any(chapterBodyChangeIds.Contains))
                continue;

            var dependentName = string.IsNullOrWhiteSpace(dependent.Summary)
                ? dependent.ToolName
                : dependent.Summary;
            throw new InvalidOperationException(
                $"Pending non-body change '{dependentName}' depends on this chapter body review. Open the pending changes modal and resolve the dependent changes together.");
        }
    }

    private static ChapterBodyReviewLineTarget? FindChapterBodyReviewLineTarget(
        Guid changeId,
        ReviewDiff diff,
        ChapterBodyReviewLineResolution request) =>
        EnumerateChapterBodyReviewLineTargets(changeId, diff)
            .FirstOrDefault(target =>
                target.ChangeId == request.ChangeId
                && target.PairId == request.PairId
                && target.OldLineNumber == request.OldLineNumber
                && target.NewLineNumber == request.NewLineNumber
                && string.Equals(target.OldText, request.OldText, StringComparison.Ordinal)
                && string.Equals(target.NewText, request.NewText, StringComparison.Ordinal));

    private static IEnumerable<ChapterBodyReviewLineTarget> EnumerateChapterBodyReviewLineTargets(Guid changeId, ReviewDiff diff)
    {
        foreach (var section in diff.Sections.Where(section => string.Equals(section.Key, "Body", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var hunk in section.Hunks)
            {
                var consumedPairIds = new HashSet<int>();
                for (var rowIndex = 0; rowIndex < hunk.Rows.Count; rowIndex++)
                {
                    var row = hunk.Rows[rowIndex];
                    if (row.Kind == DiffRowKind.Context)
                        continue;

                    if (row.PairId is int pairId)
                    {
                        if (!consumedPairIds.Add(pairId))
                            continue;

                        var oldRowIndex = -1;
                        var newRowIndex = -1;
                        DiffRow? oldRow = null;
                        DiffRow? newRow = null;
                        for (var pairIndex = 0; pairIndex < hunk.Rows.Count; pairIndex++)
                        {
                            var candidate = hunk.Rows[pairIndex];
                            if (candidate.PairId != pairId) continue;
                            if (candidate.Kind == DiffRowKind.Removed)
                            {
                                oldRow = candidate;
                                oldRowIndex = pairIndex;
                            }
                            else if (candidate.Kind == DiffRowKind.Added)
                            {
                                newRow = candidate;
                                newRowIndex = pairIndex;
                            }
                        }

                        var targetIndex = newRowIndex >= 0 ? newRowIndex : oldRowIndex;
                        if (targetIndex >= 0)
                        {
                            yield return new ChapterBodyReviewLineTarget(
                                changeId,
                                pairId,
                                oldRow?.OldLineNumber,
                                newRow?.NewLineNumber,
                                oldRow?.Text,
                                newRow?.Text,
                                hunk,
                                targetIndex,
                                oldRow,
                                newRow);
                        }

                        continue;
                    }

                    yield return row.Kind == DiffRowKind.Added
                        ? new ChapterBodyReviewLineTarget(
                            changeId,
                            PairId: null,
                            OldLineNumber: null,
                            row.NewLineNumber,
                            OldText: null,
                            row.Text,
                            hunk,
                            rowIndex,
                            OldRow: null,
                            row)
                        : new ChapterBodyReviewLineTarget(
                            changeId,
                            PairId: null,
                            row.OldLineNumber,
                            NewLineNumber: null,
                            row.Text,
                            NewText: null,
                            hunk,
                            rowIndex,
                            row,
                            NewRow: null);
                }
            }
        }
    }

    private static void ResolveLineTarget(
        ChapterBodyReviewLineTarget target,
        ChapterBodyReviewLineAction action,
        string? editedText,
        List<string> currentLines,
        List<string> proposedLines)
    {
        switch (action)
        {
            case ChapterBodyReviewLineAction.Keep:
                ResolveKeep(target, currentLines, proposedLines);
                break;
            case ChapterBodyReviewLineAction.Reject:
                ResolveReject(target, currentLines, proposedLines);
                break;
            case ChapterBodyReviewLineAction.Edit:
                if (editedText is null)
                    throw new InvalidOperationException("Enter the edited line before saving.");
                ResolveEdit(target, editedText, currentLines, proposedLines);
                break;
            default:
                throw new InvalidOperationException($"Unsupported chapter body review action '{action}'.");
        }
    }

    private static void ResolveKeep(ChapterBodyReviewLineTarget target, List<string> currentLines, List<string> proposedLines)
    {
        if (target.OldRow is not null && target.NewRow is not null)
        {
            currentLines[RequiredOldIndex(target, currentLines)] = target.NewText ?? string.Empty;
            return;
        }

        if (target.NewRow is not null)
        {
            _ = RequiredNewIndex(target, proposedLines);
            currentLines.Insert(FindCurrentInsertionIndex(target, currentLines.Count), target.NewText ?? string.Empty);
            return;
        }

        if (target.OldRow is not null)
        {
            currentLines.RemoveAt(RequiredOldIndex(target, currentLines));
            return;
        }

        throw new InvalidOperationException("The review line no longer maps to a chapter body edit.");
    }

    private static void ResolveReject(ChapterBodyReviewLineTarget target, List<string> currentLines, List<string> proposedLines)
    {
        if (target.OldRow is not null && target.NewRow is not null)
        {
            _ = RequiredOldIndex(target, currentLines);
            proposedLines[RequiredNewIndex(target, proposedLines)] = target.OldText ?? string.Empty;
            return;
        }

        if (target.NewRow is not null)
        {
            proposedLines.RemoveAt(RequiredNewIndex(target, proposedLines));
            return;
        }

        if (target.OldRow is not null)
        {
            _ = RequiredOldIndex(target, currentLines);
            proposedLines.Insert(FindProposedInsertionIndex(target, proposedLines.Count), target.OldText ?? string.Empty);
            return;
        }

        throw new InvalidOperationException("The review line no longer maps to a chapter body edit.");
    }

    private static void ResolveEdit(
        ChapterBodyReviewLineTarget target,
        string editedText,
        List<string> currentLines,
        List<string> proposedLines)
    {
        if (target.OldRow is not null && target.NewRow is not null)
        {
            currentLines[RequiredOldIndex(target, currentLines)] = editedText;
            proposedLines[RequiredNewIndex(target, proposedLines)] = editedText;
            return;
        }

        if (target.NewRow is not null)
        {
            proposedLines[RequiredNewIndex(target, proposedLines)] = editedText;
            currentLines.Insert(FindCurrentInsertionIndex(target, currentLines.Count), editedText);
            return;
        }

        if (target.OldRow is not null)
        {
            currentLines[RequiredOldIndex(target, currentLines)] = editedText;
            proposedLines.Insert(FindProposedInsertionIndex(target, proposedLines.Count), editedText);
            return;
        }

        throw new InvalidOperationException("The review line no longer maps to a chapter body edit.");
    }

    private static int RequiredOldIndex(ChapterBodyReviewLineTarget target, IReadOnlyList<string> currentLines)
    {
        if (target.OldLineNumber is not int oldLineNumber || target.OldText is null)
            throw new InvalidOperationException("This review line no longer has an original chapter line.");

        var index = oldLineNumber - 1;
        if (index < 0 || index >= currentLines.Count || !string.Equals(currentLines[index], target.OldText, StringComparison.Ordinal))
            throw new InvalidOperationException("This review line is stale because the chapter body changed. Refresh Review mode and try again.");

        return index;
    }

    private static int RequiredNewIndex(ChapterBodyReviewLineTarget target, IReadOnlyList<string> proposedLines)
    {
        if (target.NewLineNumber is not int newLineNumber || target.NewText is null)
            throw new InvalidOperationException("This review line no longer has a proposed chapter line.");

        var index = newLineNumber - 1;
        if (index < 0 || index >= proposedLines.Count || !string.Equals(proposedLines[index], target.NewText, StringComparison.Ordinal))
            throw new InvalidOperationException("This review line is stale because the proposed body changed. Refresh Review mode and try again.");

        return index;
    }

    private static int FindCurrentInsertionIndex(ChapterBodyReviewLineTarget target, int currentLineCount) =>
        FindInsertionIndex(target.Hunk.Rows, target.RowIndex, currentLineCount, useOldLineNumbers: true);

    private static int FindProposedInsertionIndex(ChapterBodyReviewLineTarget target, int proposedLineCount) =>
        FindInsertionIndex(target.Hunk.Rows, target.RowIndex, proposedLineCount, useOldLineNumbers: false);

    private static int FindInsertionIndex(IReadOnlyList<DiffRow> rows, int rowIndex, int lineCount, bool useOldLineNumbers)
    {
        for (var index = rowIndex - 1; index >= 0; index--)
        {
            var lineNumber = useOldLineNumbers ? rows[index].OldLineNumber : rows[index].NewLineNumber;
            if (lineNumber is int previousLine)
                return Math.Clamp(previousLine, 0, lineCount);
        }

        for (var index = rowIndex + 1; index < rows.Count; index++)
        {
            var lineNumber = useOldLineNumbers ? rows[index].OldLineNumber : rows[index].NewLineNumber;
            if (lineNumber is int nextLine)
                return Math.Clamp(nextLine - 1, 0, lineCount);
        }

        return lineCount;
    }

    private static string NormalizeSingleLineEditText(string? text)
    {
        var normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        if (normalized.Contains('\n'))
            throw new InvalidOperationException("Line edits must stay on one line.");
        return normalized;
    }

    private static bool BodiesEqualByLines(string left, string right) =>
        ChapterFormatting.SplitLines(left).SequenceEqual(ChapterFormatting.SplitLines(right), StringComparer.Ordinal);

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
        await AppendCorrectionSystemMessageAsync(batch, correction, cancellationToken);
    }

    private async Task AppendLineRejectionSystemMessageAsync(
        AiChange change,
        Chapter chapter,
        ChapterBodyReviewLineTarget target,
        string message,
        CancellationToken cancellationToken)
    {
        var trimmed = message.Trim();
        if (trimmed.Length == 0) return;

        var builder = new StringBuilder();
        builder.AppendLine("The user rejected one proposed chapter-body line during inline review. Update future chapter edits to respect this correction.");
        builder.AppendLine($"Chapter: {chapter.Title}");
        if (!string.IsNullOrEmpty(target.OldText))
            builder.AppendLine($"Current line kept by the user: {target.OldText}");
        if (!string.IsNullOrEmpty(target.NewText))
            builder.AppendLine($"Rejected proposed line: {target.NewText}");
        builder.AppendLine($"User rejection note: {trimmed}");

        await AppendCorrectionSystemMessageAsync(change.Batch, builder.ToString().TrimEnd(), cancellationToken);
    }

    private async Task AppendCorrectionSystemMessageAsync(
        AiChangeBatch batch,
        string correction,
        CancellationToken cancellationToken)
    {
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

    private static string BuildReviewStateJson() =>
        JsonSerializer.Serialize(new ReviewDraftState(DateTime.UtcNow), JsonSerializerOptions.Default);

    private static string Serialize(object value) =>
        JsonSerializer.Serialize(value, JsonSerializerOptions.Default);

    private static ChapterManuscriptChange Change(Chapter chapter) =>
        new(chapter.Id, chapter.Title, chapter.ManuscriptRevision, chapter.ManuscriptJson);

    private sealed record DesignedPageToolArguments(
        Guid ChapterId,
        int BlockIndex,
        string Name,
        Guid? EditionId,
        long ExpectedRevision);

    private sealed record ChapterBodyReviewLineTarget(
        Guid ChangeId,
        int? PairId,
        int? OldLineNumber,
        int? NewLineNumber,
        string? OldText,
        string? NewText,
        DiffHunk Hunk,
        int RowIndex,
        DiffRow? OldRow,
        DiffRow? NewRow);

    private sealed record ReviewDraftState(DateTime UpdatedAt);
}
