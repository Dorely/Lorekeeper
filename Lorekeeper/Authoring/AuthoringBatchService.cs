using System.Text.Json;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Authoring;

public sealed class AuthoringIdempotencyException(string message) : InvalidOperationException(message);

internal sealed class AuthoringBatchService(
    IAppDatabaseOperationFactory database,
    IAuthoringTargetMutationService targetMutations,
    IAuthoringMutationContextAccessor mutationContext,
    IAuthoringDeltaHistoryRuntime history,
    IAuthoringMutationFence fence,
    ILogger<AuthoringBatchService> logger,
    Lorekeeper.Manuscripts.Import.ISemanticImportService semanticImports) : IAuthoringBatchService
{
    private static readonly JsonSerializerOptions JsonOptions = ManuscriptCodec.JsonOptions;

    public Guid ProcessIncarnationId => fence.ProcessIncarnationId;

    public async Task<AuthoringSessionOpenResultV1> OpenSessionAsync(
        AuthoringSessionOpenRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        ValidateProtocol(request.ProtocolId);
        if (request.SessionId == Guid.Empty || request.ProjectId == Guid.Empty)
            throw new ArgumentException("Project and authoring session IDs are required.");
        var targetIds = request.Targets.Select(item => item.TargetId).Distinct(StringComparer.Ordinal).ToList();
        if (targetIds.Count != request.Targets.Count || targetIds.Count == 0)
            throw new ArgumentException("An authoring session requires unique targets.");

        await using var operation = await database.OpenWriteAsync(request.ProjectId, cancellationToken);
        operation.ShareWithNestedOperations();
        var db = operation.Db;
        var session = await db.AuthoringSessions.SingleOrDefaultAsync(item => item.Id == request.SessionId, cancellationToken);
        if (session is null)
        {
            session = new AuthoringSession
            {
                Id = request.SessionId,
                ProjectId = request.ProjectId,
                ProcessIncarnationId = ProcessIncarnationId,
            };
            db.AuthoringSessions.Add(session);
        }
        else if (session.ProjectId != request.ProjectId)
        {
            throw new AuthoringIdempotencyException("An authoring session cannot be reused for another project.");
        }
        else
        {
            session.ProcessIncarnationId = ProcessIncarnationId;
            session.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
        var targets = new List<AuthoringCanonicalTargetStateV1>(targetIds.Count);
        foreach (var targetId in targetIds)
            targets.Add(await AuthoringPersistence.ReadTargetAsync(db, request.ProjectId, targetId, "", cancellationToken));
        return new(
            AuthoringProtocolV1.ProtocolId,
            AuthoringProtocolV1.JournalId,
            ProcessIncarnationId,
            request.SessionId,
            checked(session.LastSequence + 1),
            targets);
    }

    public Task<AuthoringBatchResultV1> ApplyBatchAsync(
        AuthoringBatchV1 batch,
        CancellationToken cancellationToken = default) =>
        ApplyBatchCoreAsync(
            batch,
            recordHistory: true,
            allowCanonicalInverseOperations: false,
            historyRequestFingerprint: null,
            cancellationToken);

    public async Task AcknowledgeReceiptAsync(
        AuthoringReceiptAcknowledgementV1 acknowledgement,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenWriteAsync(acknowledgement.ProjectId, cancellationToken);
        var receipt = await operation.Db.AuthoringBatchReceipts.SingleOrDefaultAsync(
            item => item.Id == acknowledgement.ReceiptId
                && item.BatchId == acknowledgement.BatchId
                && item.SessionId == acknowledgement.SessionId,
            cancellationToken) ?? throw new KeyNotFoundException("The authoring receipt was not found.");
        if (!string.Equals(receipt.RequestHash, acknowledgement.RequestHash, StringComparison.Ordinal))
            throw new AuthoringIdempotencyException("The authoring receipt hash does not match the committed batch.");
        receipt.AcknowledgedAt ??= DateTime.UtcNow;
        await operation.SaveChangesAsync(cancellationToken);
    }

    public Task<AuthoringHistoryResultV1> ReadHistoryStateAsync(
        AuthoringTargetReferenceV1 target,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = AuthoringPersistence.ParseTarget(target.ProjectId, target.TargetId);
        var current = history.Read(target.TargetId);
        return Task.FromResult(new AuthoringHistoryResultV1(
            ProcessIncarnationId,
            target.TargetId,
            current.State,
            null,
            current.Cursor));
    }

    public Task<AuthoringHistoryResultV1> UndoAsync(
        AuthoringHistoryRequestV1 request,
        CancellationToken cancellationToken = default) =>
        MoveHistoryAsync(request, undo: true, cancellationToken);

    public Task<AuthoringHistoryResultV1> RedoAsync(
        AuthoringHistoryRequestV1 request,
        CancellationToken cancellationToken = default) =>
        MoveHistoryAsync(request, undo: false, cancellationToken);

    private async Task<AuthoringHistoryResultV1> MoveHistoryAsync(
        AuthoringHistoryRequestV1 request,
        bool undo,
        CancellationToken cancellationToken)
    {
        if (request.HistoryRequestId == Guid.Empty)
            throw new ArgumentException("A historyRequestId is required.", nameof(request));
        var requestFingerprint = $"{(undo ? "undo" : "redo")}|{request.ProjectId:D}|{request.TargetId}|{request.ExpectedGeneration}";
        await using (var replayRead = await database.OpenReadAsync(cancellationToken))
        {
            var receipt = await replayRead.Db.AuthoringBatchReceipts.AsNoTracking().SingleOrDefaultAsync(
                item => item.BatchId == request.HistoryRequestId,
                cancellationToken);
            if (receipt is not null)
            {
                if (receipt.ProjectId != request.ProjectId || receipt.SessionId != request.SessionId)
                    throw new AuthoringIdempotencyException("The history request identity belongs to another authoring session.");
                var batchResult = JsonSerializer.Deserialize<AuthoringBatchResultV1>(receipt.ResultJson, JsonOptions)
                    ?? throw new InvalidDataException("The committed history receipt is malformed.");
                if (batchResult.BatchId != receipt.BatchId
                    || batchResult.Sequence != receipt.Sequence
                    || !string.Equals(batchResult.RequestHash, receipt.RequestHash, StringComparison.Ordinal)
                    || !string.Equals(batchResult.HistoryRequestFingerprint, requestFingerprint, StringComparison.Ordinal)
                    || batchResult.Targets.All(item => !string.Equals(item.TargetId, request.TargetId, StringComparison.Ordinal))
                    || batchResult.Targets.First(item => string.Equals(item.TargetId, request.TargetId, StringComparison.Ordinal)).Generation
                        != request.ExpectedGeneration)
                {
                    throw new AuthoringIdempotencyException("The history request identity was already used for a different cursor move.");
                }
                var replayHistory = history.Read(request.TargetId);
                return new(ProcessIncarnationId, request.TargetId, replayHistory.State, batchResult with
                {
                    Status = AuthoringBatchStatusV1.Replayed
                }, replayHistory.Cursor);
            }
        }
        var move = undo ? history.Undo(request.TargetId) : history.Redo(request.TargetId);
        if (move is null)
        {
            var empty = history.Read(request.TargetId);
            return new(ProcessIncarnationId, request.TargetId, empty.State, null, empty.Cursor);
        }

        var mutationCommitted = false;
        try
        {
            await using var read = await database.OpenReadAsync(cancellationToken);
            var session = await read.Db.AuthoringSessions.AsNoTracking().SingleOrDefaultAsync(
                item => item.Id == request.SessionId && item.ProjectId == request.ProjectId,
                cancellationToken) ?? throw new KeyNotFoundException("The authoring session was not found.");
            var states = new List<AuthoringCanonicalTargetStateV1>(move.Action.TargetIds.Count);
            foreach (var targetId in move.Action.TargetIds)
                states.Add(await AuthoringPersistence.ReadTargetAsync(read.Db, request.ProjectId, targetId, "", cancellationToken));
            if (states.First(item => string.Equals(item.TargetId, request.TargetId, StringComparison.Ordinal)).Generation
                != request.ExpectedGeneration)
                throw new InvalidOperationException("The authoring generation changed; Undo/Redo history is no longer valid.");
            if (states.Any(item => !move.Action.Generations.TryGetValue(item.TargetId, out var expected)
                    || item.Generation != expected))
                throw new InvalidOperationException("An authoring target generation changed; the compound Undo/Redo action is no longer valid.");
            var targets = states.Select((item, ordinal) => new AuthoringBatchTargetV1(
                ordinal,
                item.TargetId,
                item.Revision,
                item.Generation,
                item.Fingerprint)).ToList();
            var operations = (undo ? move.Action.Inverse : move.Action.Forward).ToList();
            var batch = new AuthoringBatchV1(
                request.ProjectId,
                request.SessionId,
                request.HistoryRequestId,
                checked(session.LastSequence + 1),
                string.Empty,
                (undo ? "Undo " : "Redo ") + move.Action.ActionLabel,
                targets,
                operations,
                new(
                    undo ? move.Action.AfterSelection : move.Action.BeforeSelection,
                    undo ? move.Action.BeforeSelection : move.Action.AfterSelection));
            batch = batch with { RequestHash = AuthoringBatchHash.Compute(batch) };
            var result = await ApplyBatchCoreAsync(
                batch,
                recordHistory: false,
                allowCanonicalInverseOperations: true,
                historyRequestFingerprint: requestFingerprint,
                cancellationToken);
            if (result.Status == AuthoringBatchStatusV1.Conflict)
            {
                history.DiscardMove(move.ReservationId);
                var conflicted = history.Read(request.TargetId);
                return new(
                    ProcessIncarnationId,
                    request.TargetId,
                    conflicted.State,
                    result,
                    conflicted.Cursor);
            }
            mutationCommitted = result.Status is AuthoringBatchStatusV1.Committed or AuthoringBatchStatusV1.Replayed;
            try
            {
                history.ConfirmMove(move.ReservationId);
            }
            catch
            {
                foreach (var targetId in move.Action.TargetIds)
                    history.Clear(targetId);
            }
            var current = history.Read(request.TargetId);
            var response = new AuthoringHistoryResultV1(
                ProcessIncarnationId,
                request.TargetId,
                current.State,
                result,
                current.Cursor);
            return response;
        }
        catch
        {
            if (!mutationCommitted)
                history.DiscardMove(move.ReservationId);
            throw;
        }
    }

    private async Task<AuthoringBatchResultV1> ApplyBatchCoreAsync(
        AuthoringBatchV1 batch,
        bool recordHistory,
        bool allowCanonicalInverseOperations,
        string? historyRequestFingerprint,
        CancellationToken cancellationToken)
    {
        ValidateBatch(batch);
        await using var operation = await database.OpenWriteAsync(batch.ProjectId, cancellationToken);
        operation.ShareWithNestedOperations();
        var db = operation.Db;

        var existing = await db.AuthoringBatchReceipts.AsNoTracking().SingleOrDefaultAsync(
            item => item.BatchId == batch.BatchId,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.ProjectId != batch.ProjectId
                || existing.SessionId != batch.SessionId
                || existing.Sequence != batch.Sequence
                || !string.Equals(existing.RequestHash, batch.RequestHash, StringComparison.Ordinal))
                throw new AuthoringIdempotencyException("The batch identity was already used with different content.");
            var replay = JsonSerializer.Deserialize<AuthoringBatchResultV1>(existing.ResultJson, JsonOptions)
                ?? throw new InvalidDataException("The stored authoring receipt is malformed.");
            await operation.DisposeAsync();
            await RefreshCommittedTargetsSafelyAsync(batch.ProjectId, replay.Targets.Select(item => item.TargetId));
            return replay with { Status = AuthoringBatchStatusV1.Replayed };
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var session = await db.AuthoringSessions.SingleOrDefaultAsync(
            item => item.Id == batch.SessionId && item.ProjectId == batch.ProjectId,
            cancellationToken) ?? throw new KeyNotFoundException("The authoring session was not found.");
        if (batch.Sequence != checked(session.LastSequence + 1))
            throw new AuthoringIdempotencyException($"Authoring sequence {batch.Sequence} is not the next expected sequence {session.LastSequence + 1}.");

        var orderedTargets = batch.Targets.OrderBy(item => item.Ordinal).ToList();
        var beforeStates = new List<AuthoringCanonicalTargetStateV1>(orderedTargets.Count);
        var reductions = new Dictionary<int, AuthoringReductionResultV1>();
        var conflicts = new List<AuthoringConflictV1>();
        foreach (var target in orderedTargets)
        {
            var state = await AuthoringPersistence.ReadTargetAsync(db, batch.ProjectId, target.TargetId, "", cancellationToken);
            beforeStates.Add(state);
            var targetOperations = batch.Operations.Where(item => item.TargetOrdinal == target.Ordinal).ToList();
            var current = ManuscriptCodec.Deserialize(state.ManuscriptJson);
            var stale = state.Revision != target.ExpectedRevision
                || state.Generation != target.ExpectedGeneration
                || !string.Equals(state.Fingerprint, target.BaseFingerprint, StringComparison.Ordinal);
            if (state.Generation != target.ExpectedGeneration
                || stale && (targetOperations.Any(item => item.Kind.Equals("insertSemanticFragment", StringComparison.OrdinalIgnoreCase))
                    || !AuthoringBatchReducer.ExactPreconditionsMatch(current, targetOperations)))
            {
                conflicts.Add(new(
                    state.Generation != target.ExpectedGeneration ? "GENERATION_CHANGED" : "PRECONDITION_CHANGED",
                    target.TargetId,
                    "The canonical target changed and the submitted operations cannot be rebased without ambiguity.",
                    new("canonical", state.Fingerprint, state.ManuscriptJson, []),
                    new("submitted", target.BaseFingerprint, string.Empty, targetOperations)));
                continue;
            }
            reductions[target.Ordinal] = AuthoringBatchReducer.Apply(
                current,
                targetOperations,
                allowCanonicalInverseOperations);
        }

        if (conflicts.Count > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            var primary = history.Read(orderedTargets[0].TargetId);
            return new(
                AuthoringBatchStatusV1.Conflict,
                ProcessIncarnationId,
                batch.BatchId,
                null,
                batch.RequestHash,
                [],
                new("server", []),
                new(orderedTargets.Count > 1, orderedTargets.Select(item => item.TargetId).ToList(), "one-indivisible-action", false, primary.State),
                conflicts,
                beforeStates,
                batch.Sequence,
                historyRequestFingerprint);
        }

        if (batch.ImportResources is { } importedResources)
            await semanticImports.AdmitAsync(db, batch.ProjectId, importedResources,
                reductions.Values.Select(item => item.Document).ToList(), cancellationToken);

        using (mutationContext.SuppressHistory())
        {
            foreach (var target in orderedTargets)
            {
                var before = beforeStates[target.Ordinal];
                var targetOperations = batch.Operations.Where(item => item.TargetOrdinal == target.Ordinal).ToList();
                if (allowCanonicalInverseOperations && targetOperations.Any(item => item.Kind.Equals("restoreBlock", StringComparison.OrdinalIgnoreCase)))
                {
                    await targetMutations.ReplaceAsync(batch.ProjectId, target.TargetId, before.Revision, reductions[target.Ordinal].Document, cancellationToken);
                    continue;
                }
                var operations = AuthoringBatchReducer.ToManuscriptOperations(
                    ManuscriptCodec.Deserialize(before.ManuscriptJson),
                    targetOperations,
                    allowCanonicalInverseOperations);
                await targetMutations.ApplyAsync(batch.ProjectId, target.TargetId, before.Revision, operations, cancellationToken);
            }
        }

        var afterStates = new List<AuthoringCanonicalTargetStateV1>(orderedTargets.Count);
        foreach (var target in orderedTargets)
            afterStates.Add(await AuthoringPersistence.ReadTargetAsync(db, batch.ProjectId, target.TargetId, SelectionJson(batch.Selection?.After, target.TargetId), cancellationToken));
        session.LastSequence = batch.Sequence;
        session.ProcessIncarnationId = ProcessIncarnationId;
        session.UpdatedAt = DateTime.UtcNow;

        var inverse = orderedTargets
            .SelectMany(item => reductions[item.Ordinal].CanonicalInverse)
            .ToList();
        AuthoringDeltaHistoryStage? historyStage = null;
        if (recordHistory)
        {
            historyStage = history.Stage(
                batch.ProjectId,
                orderedTargets.Select(item => item.TargetId).ToList(),
                afterStates.ToDictionary(item => item.TargetId, item => item.Generation, StringComparer.Ordinal),
                batch.ActionLabel,
                batch.Operations,
                inverse,
                batch.Selection?.Before,
                batch.Selection?.After);
        }
        var primaryHistory = historyStage?.Projected ?? new AuthoringDeltaHistoryRecordResult(
            true,
            history.Read(orderedTargets[0].TargetId).State,
            history.Read(orderedTargets[0].TargetId).Cursor);
        var receiptId = Guid.NewGuid();
        var result = new AuthoringBatchResultV1(
            AuthoringBatchStatusV1.Committed,
            ProcessIncarnationId,
            batch.BatchId,
            receiptId,
            batch.RequestHash,
            orderedTargets.Select(target =>
            {
                var before = beforeStates[target.Ordinal];
                var after = afterStates[target.Ordinal];
                return new AuthoringCanonicalVersionV1(
                    target.TargetId,
                    before.Revision,
                    after.Revision,
                    after.Generation,
                    after.Fingerprint);
            }).ToList(),
            new("server", inverse),
            new(
                orderedTargets.Count > 1,
                orderedTargets.Select(item => item.TargetId).ToList(),
                "one-indivisible-action",
                primaryHistory.Undoable,
                primaryHistory.State),
            [],
            afterStates,
            batch.Sequence,
            historyRequestFingerprint);
        db.AuthoringBatchReceipts.Add(new AuthoringBatchReceipt
        {
            Id = receiptId,
            ProjectId = batch.ProjectId,
            SessionId = batch.SessionId,
            BatchId = batch.BatchId,
            Sequence = batch.Sequence,
            RequestHash = batch.RequestHash,
            ResultJson = JsonSerializer.Serialize(result, JsonOptions),
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (historyStage is not null)
                history.Discard(historyStage.StageId);
            throw;
        }
        if (historyStage is not null)
        {
            try
            {
                _ = history.Confirm(historyStage.StageId);
            }
            catch
            {
                foreach (var target in orderedTargets)
                    history.Clear(target.TargetId);
            }
        }
        await transaction.DisposeAsync();
        await operation.DisposeAsync();
        await RefreshCommittedTargetsSafelyAsync(
            batch.ProjectId,
            orderedTargets.Select(item => item.TargetId));
        return result;
    }

    private async Task RefreshCommittedTargetsSafelyAsync(
        Guid projectId,
        IEnumerable<string> targetIds)
    {
        foreach (var targetId in targetIds.Distinct(StringComparer.Ordinal))
        {
            try
            {
                await targetMutations.RefreshAsync(projectId, targetId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Committed authoring target {TargetId} could not refresh its derived state.",
                    targetId);
            }
        }
    }

    private static string SelectionJson(AuthoringSelectionV1? selection, string targetId)
    {
        if (selection is null)
            return string.Empty;
        var target = selection.Targets.Where(item => string.Equals(item.TargetId, targetId, StringComparison.Ordinal)).ToList();
        return target.Count == 0 ? string.Empty : JsonSerializer.Serialize(selection with { Targets = target }, JsonOptions);
    }

    private static void ValidateProtocol(string protocolId)
    {
        if (!string.Equals(protocolId, AuthoringProtocolV1.ProtocolId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported authoring protocol '{protocolId}'.");
    }

    private static void ValidateBatch(AuthoringBatchV1 batch)
    {
        ValidateProtocol(batch.ProtocolId);
        if (batch.ProjectId == Guid.Empty || batch.SessionId == Guid.Empty || batch.BatchId == Guid.Empty)
            throw new ArgumentException("Project, session, and batch IDs are required.");
        if (batch.Sequence <= 0)
            throw new ArgumentOutOfRangeException(nameof(batch), "Authoring batch sequence must be positive.");
        if (batch.Targets.Count == 0 || batch.Operations.Count == 0)
            throw new ArgumentException("An authoring batch requires targets and operations.");
        var ordinals = batch.Targets.Select(item => item.Ordinal).Order().ToArray();
        if (!ordinals.SequenceEqual(Enumerable.Range(0, ordinals.Length)))
            throw new ArgumentException("Authoring batch target ordinals must be unique, ordered, and contiguous from zero.");
        if (batch.Targets.Select(item => item.TargetId).Distinct(StringComparer.Ordinal).Count() != batch.Targets.Count)
            throw new ArgumentException("An authoring batch cannot name the same target twice.");
        if (batch.Operations.Any(item => item.TargetOrdinal < 0 || item.TargetOrdinal >= batch.Targets.Count))
            throw new ArgumentException("An authoring operation names an unknown target ordinal.");
        if (batch.ImportResources is { } resources)
        {
            if (batch.Targets.Count != 1 || batch.Operations.Count != 1
                || !batch.Operations[0].Kind.Equals("insertSemanticFragment", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Imported resources require one explicit semantic-fragment insertion.");
            if (JsonSerializer.SerializeToUtf8Bytes(new { resources, batch.Operations }, JsonOptions).Length
                > Lorekeeper.Manuscripts.Import.SemanticImportLimits.MaximumFragmentBytes)
                throw new ArgumentException("The imported authoring payload exceeds the recoverable size limit.");
            Lorekeeper.Manuscripts.Import.SemanticImportService.ValidateResources(resources);
        }
        var computed = AuthoringBatchHash.Compute(batch);
        if (!string.Equals(computed, batch.RequestHash, StringComparison.Ordinal))
            throw new AuthoringIdempotencyException("The authoring request hash does not match the canonical batch content.");
    }
}
