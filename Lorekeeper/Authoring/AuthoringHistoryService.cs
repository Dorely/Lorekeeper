using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Authoring;

public sealed record AuthoringHistoryTarget(
    Guid ProjectId,
    AuthoringHistoryDocumentKind Kind,
    Guid DocumentId,
    Guid? EditionId = null)
{
    public string StreamKey => Kind switch
    {
        AuthoringHistoryDocumentKind.EditionChapter => $"edition:{EditionId:D}:chapter:{DocumentId:D}",
        AuthoringHistoryDocumentKind.CoreChapter => $"core:chapter:{DocumentId:D}",
        AuthoringHistoryDocumentKind.PublicationSection => $"publication-section:{DocumentId:D}",
        AuthoringHistoryDocumentKind.PageComposition => $"page-composition:{DocumentId:D}",
        AuthoringHistoryDocumentKind.CoreCover => $"core-cover:{DocumentId:D}",
        AuthoringHistoryDocumentKind.ReleaseCover => $"release-cover:{DocumentId:D}",
        _ => throw new ArgumentOutOfRangeException(nameof(Kind))
    };
}

public sealed record AuthoringHistoryState(
    long Revision,
    bool CanUndo,
    bool CanRedo,
    string? UndoLabel,
    string? RedoLabel,
    long Cursor,
    int RetainedActions);

public sealed record AuthoringHistoryMutation(
    AuthoringHistoryState State,
    string ActionLabel,
    string Snapshot,
    string SelectionJson);

public interface IAuthoringHistoryService
{
    Task<AuthoringHistoryState> ReadStateAsync(AuthoringHistoryTarget target, CancellationToken cancellationToken = default);
    Task<AuthoringHistoryState> RecordManualActionAsync(AuthoringHistoryTarget target, string beforeSnapshot, string afterSnapshot, string actionLabel, string selectionJson = "", CancellationToken cancellationToken = default);
    Task<AuthoringHistoryState> UpdateAssistantTurnBatchAsync(AuthoringHistoryTarget target, Guid turnId, string beforeSnapshot, string afterSnapshot, string actionLabel, string selectionJson = "", CancellationToken cancellationToken = default);
    Task<AuthoringHistoryState> FinalizeAssistantTurnBatchAsync(AuthoringHistoryTarget target, Guid turnId, AuthoringTurnHistoryBatchStatus status, CancellationToken cancellationToken = default);
    Task FinalizeAssistantTurnAsync(Guid turnId, AuthoringTurnHistoryBatchStatus status, CancellationToken cancellationToken = default);
    Task FinalizeAbandonedBatchesAsync(CancellationToken cancellationToken = default);
    Task<AuthoringHistoryMutation> UndoAsync(AuthoringHistoryTarget target, string currentSnapshot, Func<string, CancellationToken, Task> applySnapshot, CancellationToken cancellationToken = default);
    Task<AuthoringHistoryMutation> RedoAsync(AuthoringHistoryTarget target, string currentSnapshot, Func<string, CancellationToken, Task> applySnapshot, CancellationToken cancellationToken = default);
    Task ClearAsync(AuthoringHistoryTarget target, CancellationToken cancellationToken = default);
    Task DeleteDocumentHistoryAsync(Guid projectId, AuthoringHistoryDocumentKind kind, Guid documentId, Guid? editionId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AuthoringHistoryTarget>> FindDependentStreamsAsync(Guid projectId, AuthoringHistoryDependencyKind kind, Guid resourceId, CancellationToken cancellationToken = default);
    Task ClearDependentStreamsAsync(Guid projectId, AuthoringHistoryDependencyKind kind, Guid resourceId, CancellationToken cancellationToken = default);
    Task UpdateSelectionAsync(AuthoringHistoryTarget target, string selectionJson, CancellationToken cancellationToken = default);
}

public sealed class AuthoringHistoryService(IAppDatabaseOperationFactory database) : IAuthoringHistoryService
{
    private const int MaxActions = 100;

    public async Task<AuthoringHistoryState> ReadStateAsync(AuthoringHistoryTarget target, CancellationToken cancellationToken = default)
    {
        AuthoringHistoryStream? stream;
        await using (var databaseOperation = await database.OpenReadAsync(cancellationToken))
        {
            stream = await QueryStream(databaseOperation.Db, target)
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken);
        }

        return stream is null
            ? EmptyState()
            : await BuildStateAsync(stream, cancellationToken);
    }

    public Task<AuthoringHistoryState> RecordManualActionAsync(
        AuthoringHistoryTarget target,
        string beforeSnapshot,
        string afterSnapshot,
        string actionLabel,
        string selectionJson = "",
        CancellationToken cancellationToken = default) =>
        RecordCompletedActionAsync(target, beforeSnapshot, afterSnapshot, actionLabel, AuthoringHistoryOrigin.Manual, null, selectionJson, cancellationToken);

    public async Task<AuthoringHistoryState> UpdateAssistantTurnBatchAsync(
        AuthoringHistoryTarget target,
        Guid turnId,
        string beforeSnapshot,
        string afterSnapshot,
        string actionLabel,
        string selectionJson = "",
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        if (turnId == Guid.Empty)
            throw new ArgumentException("An assistant turn ID is required.", nameof(turnId));

        var stream = await GetOrCreateStreamAsync(target, beforeSnapshot, cancellationToken);
        var batch = await db.AuthoringTurnHistoryBatches
            .Include(item => item.Dependencies)
            .SingleOrDefaultAsync(item => item.StreamId == stream.Id && item.AssistantTurnId == turnId, cancellationToken);
        var beforeHash = Hash(beforeSnapshot);
        var currentHash = await CurrentResultHashAsync(stream, cancellationToken);
        if (!FixedEquals(beforeHash, currentHash)
            || batch is { Status: AuthoringTurnHistoryBatchStatus.Open }
                && !FixedEquals(batch.AfterHash, beforeHash))
        {
            await ResetStreamAsync(db, stream, beforeSnapshot, cancellationToken);
            batch = null;
        }
        var now = DateTime.UtcNow;
        if (batch is null)
        {
            batch = new AuthoringTurnHistoryBatch
            {
                StreamId = stream.Id,
                AssistantTurnId = turnId,
                ActionLabel = NormalizeLabel(actionLabel, "Assistant change"),
                BeforeSnapshot = Compress(beforeSnapshot),
                BeforeHash = Hash(beforeSnapshot),
                AfterSnapshot = Compress(afterSnapshot),
                AfterHash = Hash(afterSnapshot),
                SelectionJson = selectionJson,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.AuthoringTurnHistoryBatches.Add(batch);
            ReplaceDependencies(batch, beforeSnapshot, afterSnapshot);
            AddStreamDependency(stream, batch);
        }
        else
        {
            if (batch.Status != AuthoringTurnHistoryBatchStatus.Open)
            {
                var completedEntry = await db.AuthoringHistoryEntries
                    .Include(item => item.Dependencies)
                    .SingleOrDefaultAsync(item => item.StreamId == stream.Id
                        && item.AssistantTurnId == turnId, cancellationToken)
                    ?? throw new InvalidOperationException("The completed assistant history entry is missing.");
                completedEntry.ResultSnapshot = Compress(afterSnapshot);
                completedEntry.ResultHash = Hash(afterSnapshot);
                completedEntry.SelectionJson = selectionJson;
                completedEntry.ActionLabel = NormalizeLabel(actionLabel, completedEntry.ActionLabel);
                ReplaceDependencies(completedEntry, Decompress(stream.BaselineSnapshot), afterSnapshot);
                AddStreamDependency(stream, completedEntry);
                batch.UpdatedAt = now;
                stream.UpdatedAt = now;
                stream.Revision++;
                await db.SaveChangesAsync(cancellationToken);
                await CleanupDetachedCompositionsAsync(target.ProjectId, cancellationToken);
                return await BuildStateAsync(stream, cancellationToken);
            }
            batch.AfterSnapshot = Compress(afterSnapshot);
            batch.AfterHash = Hash(afterSnapshot);
            batch.SelectionJson = selectionJson;
            batch.ActionLabel = NormalizeLabel(actionLabel, batch.ActionLabel);
            batch.UpdatedAt = now;
            ReplaceDependencies(batch, Decompress(batch.BeforeSnapshot), afterSnapshot);
            AddStreamDependency(stream, batch);
        }

        stream.UpdatedAt = now;
        stream.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        await CleanupDetachedCompositionsAsync(target.ProjectId, cancellationToken);
        return await BuildStateAsync(stream, cancellationToken);
    }

    public async Task<AuthoringHistoryState> FinalizeAssistantTurnBatchAsync(
        AuthoringHistoryTarget target,
        Guid turnId,
        AuthoringTurnHistoryBatchStatus status,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        if (status == AuthoringTurnHistoryBatchStatus.Open)
            throw new ArgumentOutOfRangeException(nameof(status));

        var stream = await QueryStream(db, target).SingleOrDefaultAsync(cancellationToken);
        if (stream is null)
            return EmptyState();
        var batch = await db.AuthoringTurnHistoryBatches
            .Include(item => item.Dependencies)
            .SingleOrDefaultAsync(item => item.StreamId == stream.Id && item.AssistantTurnId == turnId, cancellationToken);
        if (batch is null || batch.Status != AuthoringTurnHistoryBatchStatus.Open)
            return await BuildStateAsync(stream, cancellationToken);

        await FinalizeBatchAsync(stream, batch, status, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await CleanupDetachedCompositionsAsync(target.ProjectId, cancellationToken);
        return await BuildStateAsync(stream, cancellationToken);
    }

    public async Task FinalizeAssistantTurnAsync(Guid turnId, AuthoringTurnHistoryBatchStatus status, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        if (status == AuthoringTurnHistoryBatchStatus.Open)
            throw new ArgumentOutOfRangeException(nameof(status));
        var batches = await db.AuthoringTurnHistoryBatches
            .Include(item => item.Stream)
            .Include(item => item.Dependencies)
            .Where(item => item.AssistantTurnId == turnId && item.Status == AuthoringTurnHistoryBatchStatus.Open)
            .ToListAsync(cancellationToken);
        foreach (var batch in batches)
            await FinalizeBatchAsync(batch.Stream, batch, status, cancellationToken);
        if (batches.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            foreach (var projectId in batches.Select(item => item.Stream.ProjectId).Distinct())
                await CleanupDetachedCompositionsAsync(projectId, cancellationToken);
        }
    }

    public async Task FinalizeAbandonedBatchesAsync(CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var batches = await db.AuthoringTurnHistoryBatches
            .Include(item => item.Stream)
            .Include(item => item.Dependencies)
            .Where(item => item.Status == AuthoringTurnHistoryBatchStatus.Open)
            .OrderBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);
        foreach (var batch in batches)
            await FinalizeBatchAsync(batch.Stream, batch, AuthoringTurnHistoryBatchStatus.Recovered, cancellationToken);
        if (batches.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            foreach (var projectId in batches.Select(item => item.Stream.ProjectId).Distinct())
                await CleanupDetachedCompositionsAsync(projectId, cancellationToken);
        }
    }

    public Task<AuthoringHistoryMutation> UndoAsync(
        AuthoringHistoryTarget target,
        string currentSnapshot,
        Func<string, CancellationToken, Task> applySnapshot,
        CancellationToken cancellationToken = default) =>
        MoveCursorAsync(target, currentSnapshot, moveForward: false, applySnapshot, cancellationToken);

    public Task<AuthoringHistoryMutation> RedoAsync(
        AuthoringHistoryTarget target,
        string currentSnapshot,
        Func<string, CancellationToken, Task> applySnapshot,
        CancellationToken cancellationToken = default) =>
        MoveCursorAsync(target, currentSnapshot, moveForward: true, applySnapshot, cancellationToken);

    public async Task ClearAsync(AuthoringHistoryTarget target, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var stream = await QueryStream(db, target).SingleOrDefaultAsync(cancellationToken);
        if (stream is null)
            return;
        db.AuthoringHistoryStreams.Remove(stream);
        await db.SaveChangesAsync(cancellationToken);
        await CleanupDetachedCompositionsAsync(target.ProjectId, cancellationToken);
    }

    public Task DeleteDocumentHistoryAsync(
        Guid projectId,
        AuthoringHistoryDocumentKind kind,
        Guid documentId,
        Guid? editionId = null,
        CancellationToken cancellationToken = default) =>
        ClearAsync(new AuthoringHistoryTarget(projectId, kind, documentId, editionId), cancellationToken);

    public async Task<IReadOnlyList<AuthoringHistoryTarget>> FindDependentStreamsAsync(
        Guid projectId,
        AuthoringHistoryDependencyKind kind,
        Guid resourceId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var entryStreams = db.AuthoringHistoryDependencies.AsNoTracking()
            .Where(item => item.Kind == kind && item.ResourceId == resourceId && item.EntryId != null
                && item.Entry!.Stream.ProjectId == projectId)
            .Select(item => item.Entry!.Stream);
        var batchStreams = db.AuthoringHistoryDependencies.AsNoTracking()
            .Where(item => item.Kind == kind && item.ResourceId == resourceId && item.TurnBatchId != null
                && item.TurnBatch!.Stream.ProjectId == projectId)
            .Select(item => item.TurnBatch!.Stream);
        return (await entryStreams.Concat(batchStreams)
                .Select(item => new { item.ProjectId, item.DocumentKind, item.DocumentId, item.EditionId })
                .Distinct()
                .ToListAsync(cancellationToken))
            .Select(item => new AuthoringHistoryTarget(item.ProjectId, item.DocumentKind, item.DocumentId, item.EditionId))
            .ToList();
    }

    public async Task ClearDependentStreamsAsync(
        Guid projectId,
        AuthoringHistoryDependencyKind kind,
        Guid resourceId,
        CancellationToken cancellationToken = default)
    {
        foreach (var target in await FindDependentStreamsAsync(projectId, kind, resourceId, cancellationToken))
            await ClearAsync(target, cancellationToken);
    }

    public async Task UpdateSelectionAsync(
        AuthoringHistoryTarget target,
        string selectionJson,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var stream = await QueryStream(db, target).SingleOrDefaultAsync(cancellationToken);
        if (stream is null)
            return;
        var batch = await db.AuthoringTurnHistoryBatches
            .Where(item => item.StreamId == stream.Id && item.Status == AuthoringTurnHistoryBatchStatus.Open)
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (batch is not null)
            batch.SelectionJson = selectionJson;
        else if (stream.CursorSequence >= stream.FirstSequence)
        {
            var entry = await db.AuthoringHistoryEntries.SingleOrDefaultAsync(
                item => item.StreamId == stream.Id && item.Sequence == stream.CursorSequence,
                cancellationToken);
            if (entry is not null)
                entry.SelectionJson = selectionJson;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<AuthoringHistoryState> RecordCompletedActionAsync(
        AuthoringHistoryTarget target,
        string beforeSnapshot,
        string afterSnapshot,
        string actionLabel,
        AuthoringHistoryOrigin origin,
        Guid? assistantTurnId,
        string selectionJson,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        if (FixedEquals(Hash(beforeSnapshot), Hash(afterSnapshot)))
            return await ReadStateAsync(target, cancellationToken);

        var stream = await GetOrCreateStreamAsync(target, beforeSnapshot, cancellationToken);
        if (!FixedEquals(Hash(beforeSnapshot), await CurrentResultHashAsync(stream, cancellationToken)))
            await ResetStreamAsync(db, stream, beforeSnapshot, cancellationToken);
        await AppendEntryAsync(stream, afterSnapshot, actionLabel, origin, assistantTurnId, selectionJson, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await CleanupDetachedCompositionsAsync(target.ProjectId, cancellationToken);
        return await BuildStateAsync(stream, cancellationToken);
    }

    private async Task<AuthoringHistoryMutation> MoveCursorAsync(
        AuthoringHistoryTarget target,
        string currentSnapshot,
        bool moveForward,
        Func<string, CancellationToken, Task> applySnapshot,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(target.ProjectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var stream = await QueryStream(db, target).SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException(moveForward ? "Nothing is available to redo." : "Nothing is available to undo.");
        if (await db.AuthoringTurnHistoryBatches.AnyAsync(item => item.StreamId == stream.Id && item.Status == AuthoringTurnHistoryBatchStatus.Open, cancellationToken))
            throw new InvalidOperationException("Finish or stop the active assistant turn before using history.");

        if (!FixedEquals(Hash(currentSnapshot), await CurrentResultHashAsync(stream, cancellationToken)))
        {
            await ResetStreamAsync(db, stream, currentSnapshot, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return new AuthoringHistoryMutation(
                await BuildStateAsync(stream, cancellationToken),
                string.Empty,
                currentSnapshot,
                string.Empty);
        }
        var entries = await db.AuthoringHistoryEntries
            .Where(item => item.StreamId == stream.Id)
            .OrderBy(item => item.Sequence)
            .ToListAsync(cancellationToken);
        AuthoringHistoryEntry action;
        string targetSnapshot;
        string targetSelection;
        long targetCursor;
        if (moveForward)
        {
            action = entries.FirstOrDefault(item => item.Sequence > stream.CursorSequence)
                ?? throw new InvalidOperationException("Nothing is available to redo.");
            targetSnapshot = Decompress(action.ResultSnapshot);
            targetSelection = action.SelectionJson;
            targetCursor = action.Sequence;
        }
        else
        {
            action = entries.LastOrDefault(item => item.Sequence <= stream.CursorSequence)
                ?? throw new InvalidOperationException("Nothing is available to undo.");
            var previous = entries.LastOrDefault(item => item.Sequence < action.Sequence);
            targetSnapshot = previous is null ? Decompress(stream.BaselineSnapshot) : Decompress(previous.ResultSnapshot);
            targetSelection = previous?.SelectionJson ?? string.Empty;
            targetCursor = previous?.Sequence ?? stream.FirstSequence - 1;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await applySnapshot(targetSnapshot, cancellationToken);
        stream.CursorSequence = targetCursor;
        stream.Revision++;
        stream.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AuthoringHistoryMutation(await BuildStateAsync(stream, cancellationToken), action.ActionLabel, targetSnapshot, targetSelection);
    }

    private async Task<AuthoringHistoryStream> GetOrCreateStreamAsync(AuthoringHistoryTarget target, string baseline, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var stream = await QueryStream(db, target).SingleOrDefaultAsync(cancellationToken);
        if (stream is not null)
            return stream;
        var now = DateTime.UtcNow;
        stream = new AuthoringHistoryStream
        {
            ProjectId = target.ProjectId,
            StreamKey = target.StreamKey,
            DocumentKind = target.Kind,
            DocumentId = target.DocumentId,
            EditionId = target.EditionId,
            BaselineSnapshot = Compress(baseline),
            BaselineHash = Hash(baseline),
            FirstSequence = 1,
            CursorSequence = 0,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.AuthoringHistoryStreams.Add(stream);
        return stream;
    }

    private static IQueryable<AuthoringHistoryStream> QueryStream(
        AppDbContext db,
        AuthoringHistoryTarget target) =>
        db.AuthoringHistoryStreams.Where(item =>
            item.ProjectId == target.ProjectId
            && item.StreamKey == target.StreamKey);
    private async Task AppendEntryAsync(
        AuthoringHistoryStream stream,
        string afterSnapshot,
        string actionLabel,
        AuthoringHistoryOrigin origin,
        Guid? assistantTurnId,
        string selectionJson,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var redo = await db.AuthoringHistoryEntries
            .Where(item => item.StreamId == stream.Id && item.Sequence > stream.CursorSequence)
            .ToListAsync(cancellationToken);
        db.AuthoringHistoryEntries.RemoveRange(redo);

        var lastSequence = await db.AuthoringHistoryEntries
            .Where(item => item.StreamId == stream.Id && item.Sequence <= stream.CursorSequence)
            .Select(item => (long?)item.Sequence)
            .MaxAsync(cancellationToken) ?? stream.FirstSequence - 1;
        var sequence = Math.Max(stream.LastSequence, lastSequence) + 1;
        var entry = new AuthoringHistoryEntry
        {
            StreamId = stream.Id,
            Sequence = sequence,
            ActionLabel = NormalizeLabel(actionLabel, "Edit document"),
            Origin = origin,
            AssistantTurnId = assistantTurnId,
            ResultSnapshot = Compress(afterSnapshot),
            ResultHash = Hash(afterSnapshot),
            SelectionJson = selectionJson
        };
        db.AuthoringHistoryEntries.Add(entry);
        ReplaceDependencies(entry, Decompress(stream.BaselineSnapshot), afterSnapshot);
        AddStreamDependency(stream, entry);
        stream.LastSequence = sequence;
        stream.CursorSequence = sequence;
        stream.Revision++;
        stream.UpdatedAt = DateTime.UtcNow;
        await PruneAsync(stream, cancellationToken);
    }

    private async Task FinalizeBatchAsync(
        AuthoringHistoryStream stream,
        AuthoringTurnHistoryBatch batch,
        AuthoringTurnHistoryBatchStatus status,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        if (!FixedEquals(batch.BeforeHash, batch.AfterHash))
            await AppendEntryAsync(stream, Decompress(batch.AfterSnapshot), batch.ActionLabel, AuthoringHistoryOrigin.Assistant, batch.AssistantTurnId, batch.SelectionJson, cancellationToken);
        db.AuthoringHistoryDependencies.RemoveRange(batch.Dependencies);
        batch.Dependencies.Clear();
        batch.BeforeSnapshot = [];
        batch.AfterSnapshot = [];
        batch.Status = status;
        batch.FinalizedAt = DateTime.UtcNow;
        batch.UpdatedAt = batch.FinalizedAt.Value;
    }

    private async Task PruneAsync(AuthoringHistoryStream stream, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var entries = await db.AuthoringHistoryEntries
            .Include(item => item.Dependencies)
            .Where(item => item.StreamId == stream.Id)
            .OrderBy(item => item.Sequence)
            .ToListAsync(cancellationToken);
        var overflow = entries.Count + db.ChangeTracker.Entries<AuthoringHistoryEntry>().Count(item => item.State == EntityState.Added && item.Entity.StreamId == stream.Id) - MaxActions;
        if (overflow <= 0)
            return;
        foreach (var entry in entries.Take(overflow))
        {
            stream.BaselineSnapshot = entry.ResultSnapshot;
            stream.BaselineHash = entry.ResultHash;
            stream.FirstSequence = entry.Sequence + 1;
            db.AuthoringHistoryEntries.Remove(entry);
        }
        var firstRetained = entries.Skip(overflow).FirstOrDefault();
        if (firstRetained is not null)
        {
            ReplaceDependencies(
                firstRetained,
                Decompress(stream.BaselineSnapshot),
                Decompress(firstRetained.ResultSnapshot));
            AddStreamDependency(stream, firstRetained);
        }
    }

    private async Task<string> CurrentResultHashAsync(AuthoringHistoryStream stream, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        if (stream.CursorSequence < stream.FirstSequence)
            return stream.BaselineHash;
        return await db.AuthoringHistoryEntries
            .Where(item => item.StreamId == stream.Id && item.Sequence == stream.CursorSequence)
            .Select(item => item.ResultHash)
            .SingleAsync(cancellationToken);
    }

    private async Task<AuthoringHistoryState> BuildStateAsync(AuthoringHistoryStream stream, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var entries = await db.AuthoringHistoryEntries
            .AsNoTracking()
            .Where(item => item.StreamId == stream.Id)
            .OrderBy(item => item.Sequence)
            .Select(item => new { item.Sequence, item.ActionLabel })
            .ToListAsync(cancellationToken);
        var undo = entries.LastOrDefault(item => item.Sequence <= stream.CursorSequence);
        var redo = entries.FirstOrDefault(item => item.Sequence > stream.CursorSequence);
        return new AuthoringHistoryState(stream.Revision, undo is not null, redo is not null, undo?.ActionLabel, redo?.ActionLabel, stream.CursorSequence, entries.Count);
    }

    private static AuthoringHistoryState EmptyState() => new(0, false, false, null, null, 0, 0);

    private static async Task ResetStreamAsync(
        AppDbContext db,
        AuthoringHistoryStream stream,
        string currentSnapshot,
        CancellationToken cancellationToken)
    {
        var entries = await db.AuthoringHistoryEntries
            .Where(item => item.StreamId == stream.Id)
            .ToListAsync(cancellationToken);
        var batches = await db.AuthoringTurnHistoryBatches
            .Where(item => item.StreamId == stream.Id)
            .ToListAsync(cancellationToken);
        db.AuthoringHistoryEntries.RemoveRange(entries);
        db.AuthoringTurnHistoryBatches.RemoveRange(batches);
        stream.BaselineSnapshot = Compress(currentSnapshot);
        stream.BaselineHash = Hash(currentSnapshot);
        stream.FirstSequence = 1;
        stream.LastSequence = 0;
        stream.CursorSequence = 0;
        stream.Revision++;
        stream.UpdatedAt = DateTime.UtcNow;
    }

    private static string NormalizeLabel(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim()[..Math.Min(value.Trim().Length, 180)];

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));

    private static byte[] Compress(string value)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var writer = new StreamWriter(brotli, Encoding.UTF8))
            writer.Write(value);
        return output.ToArray();
    }

    private static string Decompress(byte[] value)
    {
        using var input = new MemoryStream(value);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(brotli, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private void ReplaceDependencies(AuthoringHistoryEntry entry, params string[] snapshots)
    {
        using var databaseOperation = database.OpenWrite();
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        db.AuthoringHistoryDependencies.RemoveRange(entry.Dependencies);
        entry.Dependencies.Clear();
        foreach (var dependency in AuthoringSnapshotCodec.FindDependencies(snapshots))
            entry.Dependencies.Add(new AuthoringHistoryDependency
            {
                Entry = entry,
                EntryId = entry.Id,
                Kind = dependency.Kind,
                ResourceId = dependency.ResourceId
            });
    }

    private void ReplaceDependencies(AuthoringTurnHistoryBatch batch, params string[] snapshots)
    {
        using var databaseOperation = database.OpenWrite();
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        db.AuthoringHistoryDependencies.RemoveRange(batch.Dependencies);
        batch.Dependencies.Clear();
        foreach (var dependency in AuthoringSnapshotCodec.FindDependencies(snapshots))
            batch.Dependencies.Add(new AuthoringHistoryDependency
            {
                TurnBatch = batch,
                TurnBatchId = batch.Id,
                Kind = dependency.Kind,
                ResourceId = dependency.ResourceId
            });
    }

    private static void AddStreamDependency(AuthoringHistoryStream stream, AuthoringHistoryEntry entry)
    {
        if (stream.DocumentKind != AuthoringHistoryDocumentKind.PageComposition
            || entry.Dependencies.Any(item => item.Kind == AuthoringHistoryDependencyKind.PageComposition
                && item.ResourceId == stream.DocumentId))
            return;
        entry.Dependencies.Add(new AuthoringHistoryDependency
        {
            Entry = entry,
            EntryId = entry.Id,
            Kind = AuthoringHistoryDependencyKind.PageComposition,
            ResourceId = stream.DocumentId
        });
    }

    private static void AddStreamDependency(AuthoringHistoryStream stream, AuthoringTurnHistoryBatch batch)
    {
        if (stream.DocumentKind != AuthoringHistoryDocumentKind.PageComposition
            || batch.Dependencies.Any(item => item.Kind == AuthoringHistoryDependencyKind.PageComposition
                && item.ResourceId == stream.DocumentId))
            return;
        batch.Dependencies.Add(new AuthoringHistoryDependency
        {
            TurnBatch = batch,
            TurnBatchId = batch.Id,
            Kind = AuthoringHistoryDependencyKind.PageComposition,
            ResourceId = stream.DocumentId
        });
    }

    private async Task CleanupDetachedCompositionsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var retainedIds = await db.AuthoringHistoryDependencies.AsNoTracking()
            .Where(item => item.Kind == AuthoringHistoryDependencyKind.PageComposition)
            .Select(item => item.ResourceId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var obsolete = await db.PageCompositions.IgnoreQueryFilters()
            .Where(item => item.ProjectId == projectId && item.DetachedAt != null && !retainedIds.Contains(item.Id))
            .ToListAsync(cancellationToken);
        if (obsolete.Count == 0)
            return;
        var obsoleteIds = obsolete.Select(item => item.Id).ToList();
        var streams = await db.AuthoringHistoryStreams
            .Where(item => item.ProjectId == projectId
                && item.DocumentKind == AuthoringHistoryDocumentKind.PageComposition
                && obsoleteIds.Contains(item.DocumentId))
            .ToListAsync(cancellationToken);
        db.AuthoringHistoryStreams.RemoveRange(streams);
        db.PageCompositions.RemoveRange(obsolete);
        await db.SaveChangesAsync(cancellationToken);
    }
}
