using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Lorekeeper.Authoring;

public enum AuthoringHistoryDocumentKind
{
    CoreChapter,
    EditionChapter,
    PublicationSection,
    DesignedPageContent,
    CoreCover,
    ReleaseCover
}

public enum AuthoringHistoryDependencyKind
{
    ProjectImage,
    ProjectFont,
    DesignedPage
}

public sealed record AuthoringHistoryTarget(
    Guid ProjectId,
    AuthoringHistoryDocumentKind Kind,
    Guid DocumentId,
    Guid? EditionId = null)
{
    public string StreamKey => Kind switch
    {
        AuthoringHistoryDocumentKind.EditionChapter when EditionId is Guid editionId =>
            $"release:{editionId:D}:chapter:{DocumentId:D}",
        AuthoringHistoryDocumentKind.EditionChapter =>
            throw new ArgumentException("A release chapter history target requires an edition ID.", nameof(EditionId)),
        AuthoringHistoryDocumentKind.CoreChapter => $"core:chapter:{DocumentId:D}",
        AuthoringHistoryDocumentKind.PublicationSection when EditionId is Guid editionId =>
            $"release:{editionId:D}:section:{DocumentId:D}",
        AuthoringHistoryDocumentKind.PublicationSection => $"publication-section:{DocumentId:D}",
        AuthoringHistoryDocumentKind.DesignedPageContent => $"designed-page-content:{DocumentId:D}",
        AuthoringHistoryDocumentKind.CoreCover => $"core-cover:{DocumentId:D}",
        AuthoringHistoryDocumentKind.ReleaseCover when EditionId is Guid editionId =>
            $"release:{editionId:D}:cover:{DocumentId:D}",
        AuthoringHistoryDocumentKind.ReleaseCover =>
            throw new ArgumentException("A release cover history target requires an edition ID.", nameof(EditionId)),
        _ => throw new ArgumentOutOfRangeException(nameof(Kind))
    };

    internal string RegistryKey => $"{ProjectId:D}|{StreamKey}";
}

public sealed record AuthoringHistoryState(
    bool CanUndo,
    bool CanRedo,
    string? UndoLabel,
    string? RedoLabel);

public sealed record AuthoringHistoryMutation(
    AuthoringHistoryState State,
    string ActionLabel,
    string Snapshot,
    string SelectionJson)
{
    public IReadOnlyList<AuthoringHistoryTarget> AffectedTargets { get; init; } = [];
}

public sealed record AuthoringHistorySnapshotTransition(
    AuthoringHistoryTarget Target,
    string BeforeSnapshot,
    string AfterSnapshot,
    string SelectionJson = "");

public sealed record AuthoringHistorySnapshotRestore(
    AuthoringHistoryTarget Target,
    string Snapshot,
    string ExpectedCurrentSnapshot);

/// <summary>
/// The durable Review Edits implementation owns its own baseline. This DTO is
/// retained as the common value shape, but it is intentionally not part of the
/// manual Undo/Redo runtime contract.
/// </summary>
public sealed record LatestAssistantReviewSnapshot(
    string BeforeManuscriptJson,
    string BeforeHash,
    Guid? AssistantTurnId,
    string ActionLabel,
    DateTime CapturedAt);

public interface IAuthoringHistoryRuntime
{
    Task<AuthoringHistoryState> ReadStateAsync(
        AuthoringHistoryTarget target,
        CancellationToken cancellationToken = default);

    Task<AuthoringHistoryState> RecordManualActionAsync(
        AuthoringHistoryTarget target,
        string beforeSnapshot,
        string afterSnapshot,
        string actionLabel,
        string selectionJson = "",
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, AuthoringHistoryState>> RecordCompoundManualActionAsync(
        IReadOnlyList<AuthoringHistorySnapshotTransition> transitions,
        string actionLabel,
        CancellationToken cancellationToken = default);

    Task<AuthoringHistoryState> ResetToCurrentAsync(
        AuthoringHistoryTarget target,
        string currentSnapshot,
        CancellationToken cancellationToken = default);

    Task<AuthoringHistoryState> InvalidateAsync(
        AuthoringHistoryTarget target,
        string currentSnapshot,
        CancellationToken cancellationToken = default);

    Task<AuthoringHistoryMutation> UndoAsync(
        AuthoringHistoryTarget target,
        string currentSnapshot,
        Func<string, CancellationToken, Task> applySnapshot,
        CancellationToken cancellationToken = default);

    Task<AuthoringHistoryMutation> RedoAsync(
        AuthoringHistoryTarget target,
        string currentSnapshot,
        Func<string, CancellationToken, Task> applySnapshot,
        CancellationToken cancellationToken = default);

    Task<AuthoringHistoryMutation> UndoCompoundAwareAsync(
        AuthoringHistoryTarget target,
        string currentSnapshot,
        Func<IReadOnlyList<AuthoringHistorySnapshotRestore>, CancellationToken, Task> applySnapshots,
        CancellationToken cancellationToken = default);

    Task<AuthoringHistoryMutation> RedoCompoundAwareAsync(
        AuthoringHistoryTarget target,
        string currentSnapshot,
        Func<IReadOnlyList<AuthoringHistorySnapshotRestore>, CancellationToken, Task> applySnapshots,
        CancellationToken cancellationToken = default);

    Task ClearAsync(
        AuthoringHistoryTarget target,
        CancellationToken cancellationToken = default);

    Task DeleteDocumentHistoryAsync(
        Guid projectId,
        AuthoringHistoryDocumentKind kind,
        Guid documentId,
        Guid? editionId = null,
        CancellationToken cancellationToken = default);

    Task ClearProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuthoringHistoryTarget>> FindDependentStreamsAsync(
        Guid projectId,
        AuthoringHistoryDependencyKind kind,
        Guid resourceId,
        CancellationToken cancellationToken = default);

    Task ClearDependentStreamsAsync(
        Guid projectId,
        AuthoringHistoryDependencyKind kind,
        Guid resourceId,
        CancellationToken cancellationToken = default);

    Task UpdateSelectionAsync(
        AuthoringHistoryTarget target,
        string selectionJson,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Process-lifetime manual authoring history. This type deliberately has no
/// persistence dependency: a host restart clears all streams by design.
/// </summary>
public sealed class AuthoringHistoryRuntime : IAuthoringHistoryRuntime
{
    private const int MaxActionsPerStream = 100;
    private const long MaxCompressedBytes = 128L * 1024 * 1024;
    private static readonly TimeSpan TypingGroupingWindow = TimeSpan.FromMilliseconds(500);

    private readonly object _registryLock = new();
    private readonly Dictionary<string, HistoryStream> _streams = new(StringComparer.Ordinal);
    private readonly Dictionary<DependencyKey, HashSet<string>> _dependencyIndex = [];
    private long _compressedBytes;

    public async Task<AuthoringHistoryState> ReadStateAsync(
        AuthoringHistoryTarget target,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireAsync(target, create: false, cancellationToken);
        if (lease is null)
            return EmptyState;

        cancellationToken.ThrowIfCancellationRequested();
        return BuildState(lease.Stream);
    }

    public async Task<AuthoringHistoryState> RecordManualActionAsync(
        AuthoringHistoryTarget target,
        string beforeSnapshot,
        string afterSnapshot,
        string actionLabel,
        string selectionJson = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beforeSnapshot);
        ArgumentNullException.ThrowIfNull(afterSnapshot);
        await using var lease = await AcquireAsync(target, create: true, cancellationToken);
        var stream = lease!.Stream;
        cancellationToken.ThrowIfCancellationRequested();

        if (HashesEqual(beforeSnapshot, afterSnapshot))
            return BuildState(stream);

        if (!stream.HasBaseline || !string.Equals(Hash(beforeSnapshot), CurrentHash(stream), StringComparison.Ordinal))
            ResetStreamLocked(stream, beforeSnapshot);

        var label = NormalizeLabel(actionLabel, "Edit document");
        var now = DateTime.UtcNow;
        var canGroup = stream.Cursor == stream.Entries.Count
            && !stream.LastWasCursorMove
            && stream.Entries.Count > 0
            && now - stream.Entries[^1].RecordedAt <= TypingGroupingWindow
            && string.Equals(stream.Entries[^1].ActionLabel, label, StringComparison.Ordinal)
            && HashesEqual(beforeSnapshot, Decompress(stream.Entries[^1].Snapshot));

        lock (_registryLock)
        {
            if (canGroup)
            {
                ReplaceEntrySnapshotLocked(stream, stream.Entries[^1], afterSnapshot);
                stream.Entries[^1].SelectionJson = selectionJson;
                stream.Entries[^1].RecordedAt = now;
            }
            else
            {
                RemoveRedoBranchLocked(stream);
                var entry = new HistoryEntry(
                    Compress(afterSnapshot),
                    Hash(afterSnapshot),
                    label,
                    selectionJson,
                    now);
                stream.Entries.Add(entry);
                _compressedBytes += entry.Snapshot.Data.Length;
                stream.Cursor = stream.Entries.Count;
                RebuildDependenciesLocked(stream);
                PruneLocked(stream);
            }

            stream.LastAccessUtc = now;
            stream.LastWasCursorMove = false;
            stream.Revision++;
            EnforceBudgetLocked(stream);
        }

        return BuildState(stream);
    }

    public async Task<IReadOnlyDictionary<string, AuthoringHistoryState>> RecordCompoundManualActionAsync(
        IReadOnlyList<AuthoringHistorySnapshotTransition> transitions,
        string actionLabel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transitions);
        if (transitions.Count < 2)
            throw new ArgumentException("A compound history action requires at least two targets.", nameof(transitions));
        if (transitions.Select(item => item.Target.RegistryKey).Distinct(StringComparer.Ordinal).Count() != transitions.Count)
            throw new ArgumentException("A compound history action cannot contain the same target twice.", nameof(transitions));
        if (transitions.Select(item => item.Target.ProjectId).Distinct().Count() != 1)
            throw new ArgumentException("A compound history action cannot span projects.", nameof(transitions));

        var ordered = transitions.OrderBy(item => item.Target.RegistryKey, StringComparer.Ordinal).ToList();
        var leases = new List<StreamLease>(ordered.Count);
        try
        {
            foreach (var transition in ordered)
            {
                ArgumentNullException.ThrowIfNull(transition.BeforeSnapshot);
                ArgumentNullException.ThrowIfNull(transition.AfterSnapshot);
                var lease = await AcquireAsync(transition.Target, create: true, cancellationToken)
                    ?? throw new InvalidOperationException("Could not acquire a compound history target.");
                leases.Add(lease);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var compoundId = Guid.NewGuid();
            var participantKeys = ordered.Select(item => item.Target.RegistryKey).ToArray();
            var label = NormalizeLabel(actionLabel, "Move Designed Page placement");
            var now = DateTime.UtcNow;
            lock (_registryLock)
            {
                foreach (var (transition, lease) in ordered.Zip(leases))
                {
                    var stream = lease.Stream;
                    if (!stream.HasBaseline
                        || !string.Equals(Hash(transition.BeforeSnapshot), CurrentHash(stream), StringComparison.Ordinal))
                    {
                        ResetStreamLocked(stream, transition.BeforeSnapshot);
                    }

                    RemoveRedoBranchLocked(stream);
                    var entry = new HistoryEntry(
                        Compress(transition.AfterSnapshot),
                        Hash(transition.AfterSnapshot),
                        label,
                        transition.SelectionJson,
                        now,
                        compoundId,
                        participantKeys);
                    stream.Entries.Add(entry);
                    _compressedBytes += entry.Snapshot.Data.Length;
                    stream.Cursor = stream.Entries.Count;
                    stream.LastAccessUtc = now;
                    stream.LastWasCursorMove = false;
                    stream.Revision++;
                    RebuildDependenciesLocked(stream);
                    PruneLocked(stream);
                }
                EnforceBudgetLocked();

                return ordered.ToDictionary(
                    item => item.Target.RegistryKey,
                    item => BuildState(_streams[item.Target.RegistryKey]),
                    StringComparer.Ordinal);
            }
        }
        finally
        {
            for (var index = leases.Count - 1; index >= 0; index--)
                await leases[index].DisposeAsync();
        }
    }

    public Task<AuthoringHistoryState> ResetToCurrentAsync(
        AuthoringHistoryTarget target,
        string currentSnapshot,
        CancellationToken cancellationToken = default) =>
        ResetToCurrentCoreAsync(target, currentSnapshot, cancellationToken);

    public Task<AuthoringHistoryState> InvalidateAsync(
        AuthoringHistoryTarget target,
        string currentSnapshot,
        CancellationToken cancellationToken = default) =>
        ResetToCurrentCoreAsync(target, currentSnapshot, cancellationToken);

    private async Task<AuthoringHistoryState> ResetToCurrentCoreAsync(
        AuthoringHistoryTarget target,
        string currentSnapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        await using var lease = await AcquireAsync(target, create: true, cancellationToken);
        var stream = lease!.Stream;
        cancellationToken.ThrowIfCancellationRequested();
        lock (_registryLock)
        {
            ResetStreamLocked(stream, currentSnapshot);
            stream.LastAccessUtc = DateTime.UtcNow;
            stream.Revision++;
            EnforceBudgetLocked(stream);
        }

        return EmptyState;
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

    public Task<AuthoringHistoryMutation> UndoCompoundAwareAsync(
        AuthoringHistoryTarget target,
        string currentSnapshot,
        Func<IReadOnlyList<AuthoringHistorySnapshotRestore>, CancellationToken, Task> applySnapshots,
        CancellationToken cancellationToken = default) =>
        MoveCompoundAwareCursorAsync(target, currentSnapshot, moveForward: false, applySnapshots, cancellationToken);

    public Task<AuthoringHistoryMutation> RedoCompoundAwareAsync(
        AuthoringHistoryTarget target,
        string currentSnapshot,
        Func<IReadOnlyList<AuthoringHistorySnapshotRestore>, CancellationToken, Task> applySnapshots,
        CancellationToken cancellationToken = default) =>
        MoveCompoundAwareCursorAsync(target, currentSnapshot, moveForward: true, applySnapshots, cancellationToken);

    private async Task<AuthoringHistoryMutation> MoveCompoundAwareCursorAsync(
        AuthoringHistoryTarget target,
        string currentSnapshot,
        bool moveForward,
        Func<IReadOnlyList<AuthoringHistorySnapshotRestore>, CancellationToken, Task> applySnapshots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        ArgumentNullException.ThrowIfNull(applySnapshots);
        var initialLease = await AcquireAsync(target, create: false, cancellationToken);
        if (initialLease is null)
            throw new InvalidOperationException(moveForward ? "Nothing is available to redo." : "Nothing is available to undo.");
        IReadOnlyList<string> participantKeys;
        try
        {
            lock (_registryLock)
            {
                var requested = initialLease.Stream;
                if (!requested.HasBaseline || !string.Equals(Hash(currentSnapshot), CurrentHash(requested), StringComparison.Ordinal))
                {
                    ResetStreamLocked(requested, currentSnapshot);
                    requested.LastAccessUtc = DateTime.UtcNow;
                    requested.Revision++;
                    EnforceBudgetLocked(requested);
                    return new AuthoringHistoryMutation(EmptyState, string.Empty, currentSnapshot, string.Empty);
                }
                if (moveForward && requested.Cursor >= requested.Entries.Count)
                    throw new InvalidOperationException("Nothing is available to redo.");
                if (!moveForward && requested.Cursor == 0)
                    throw new InvalidOperationException("Nothing is available to undo.");
                var entry = requested.Entries[moveForward ? requested.Cursor : requested.Cursor - 1];
                participantKeys = entry.CompoundActionId is null
                    ? [requested.RegistryKey]
                    : entry.CompoundParticipantKeys;
            }
        }
        finally
        {
            await initialLease.DisposeAsync();
        }

        var leases = new List<StreamLease>(participantKeys.Count);
        try
        {
            foreach (var participantKey in participantKeys.Order(StringComparer.Ordinal))
            {
                AuthoringHistoryTarget participantTarget;
                lock (_registryLock)
                {
                    if (!_streams.TryGetValue(participantKey, out var stream))
                        throw new InvalidOperationException("This compound Undo action is no longer complete and cannot be applied.");
                    participantTarget = stream.Target;
                }
                var acquired = await AcquireAsync(participantTarget, create: false, cancellationToken)
                    ?? throw new InvalidOperationException("This compound Undo action is no longer complete and cannot be applied.");
                leases.Add(acquired);
            }

            List<AuthoringHistorySnapshotRestore> restores;
            List<HistoryStream> affectedStreams;
            Guid? compoundId;
            string label;
            string selection;
            HistoryStream requestedStream;
            lock (_registryLock)
            {
                requestedStream = leases.Select(item => item.Stream)
                    .Single(item => item.RegistryKey == target.RegistryKey);
                if (!requestedStream.HasBaseline
                    || !string.Equals(Hash(currentSnapshot), CurrentHash(requestedStream), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The requested Undo target changed while its compound action was being acquired.");
                }
                if (moveForward && requestedStream.Cursor >= requestedStream.Entries.Count
                    || !moveForward && requestedStream.Cursor == 0)
                {
                    throw new InvalidOperationException("The requested Undo cursor changed while its compound action was being acquired.");
                }

                var requestedEntry = requestedStream.Entries[
                    moveForward ? requestedStream.Cursor : requestedStream.Cursor - 1];
                compoundId = requestedEntry.CompoundActionId;
                if (!participantKeys.SequenceEqual(
                        compoundId is null ? [requestedStream.RegistryKey] : requestedEntry.CompoundParticipantKeys,
                        StringComparer.Ordinal))
                {
                    throw new InvalidOperationException("The requested compound Undo action changed while its participants were being acquired.");
                }

                affectedStreams = [];
                restores = [];
                foreach (var participantKey in participantKeys)
                {
                    var stream = leases.Select(item => item.Stream)
                        .Single(item => item.RegistryKey == participantKey);
                    if (moveForward && stream.Cursor >= stream.Entries.Count
                        || !moveForward && stream.Cursor == 0)
                    {
                        throw new InvalidOperationException("The manuscripts in this compound Undo action no longer share one cursor.");
                    }
                    var entry = stream.Entries[moveForward ? stream.Cursor : stream.Cursor - 1];
                    if (entry.CompoundActionId != compoundId)
                        throw new InvalidOperationException("The manuscripts in this compound Undo action no longer share one cursor.");
                    var desired = moveForward
                        ? Decompress(entry.Snapshot)
                        : stream.Cursor == 1
                            ? Decompress(stream.BaselineSnapshot!)
                            : Decompress(stream.Entries[stream.Cursor - 2].Snapshot);
                    var expectedCurrent = Decompress(stream.Cursor == 0
                        ? stream.BaselineSnapshot!
                        : stream.Entries[stream.Cursor - 1].Snapshot);
                    affectedStreams.Add(stream);
                    restores.Add(new AuthoringHistorySnapshotRestore(stream.Target, desired, expectedCurrent));
                }
                label = requestedEntry.ActionLabel;
                selection = moveForward
                    ? requestedEntry.SelectionJson
                    : requestedStream.Cursor <= 1
                        ? string.Empty
                        : requestedStream.Entries[requestedStream.Cursor - 2].SelectionJson;
            }

            // Hold every participant gate while the owner revalidates and
            // commits all snapshots, then advance every cursor together.
            await applySnapshots(restores, cancellationToken);

            lock (_registryLock)
            {
                foreach (var stream in affectedStreams)
                {
                    if (!_streams.TryGetValue(stream.RegistryKey, out var current) || !ReferenceEquals(current, stream))
                        throw new InvalidOperationException("A compound Undo target changed while its mutation was committing.");
                    var entry = stream.Entries[moveForward ? stream.Cursor : stream.Cursor - 1];
                    if (entry.CompoundActionId != compoundId)
                        throw new InvalidOperationException("A compound Undo cursor changed while its mutation was committing.");
                }
                foreach (var stream in affectedStreams)
                {
                    stream.Cursor += moveForward ? 1 : -1;
                    stream.LastWasCursorMove = true;
                    stream.LastAccessUtc = DateTime.UtcNow;
                    stream.Revision++;
                }
                return new AuthoringHistoryMutation(
                    BuildState(requestedStream),
                    label,
                    restores.Single(item => item.Target.RegistryKey == target.RegistryKey).Snapshot,
                    selection)
                {
                    AffectedTargets = affectedStreams.Select(item => item.Target).ToList(),
                };
            }
        }
        finally
        {
            for (var index = leases.Count - 1; index >= 0; index--)
                await leases[index].DisposeAsync();
        }
    }

    private async Task<AuthoringHistoryMutation> MoveCursorAsync(
        AuthoringHistoryTarget target,
        string currentSnapshot,
        bool moveForward,
        Func<string, CancellationToken, Task> applySnapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        ArgumentNullException.ThrowIfNull(applySnapshot);
        await using var lease = await AcquireAsync(target, create: false, cancellationToken);
        if (lease is null)
            throw new InvalidOperationException(moveForward ? "Nothing is available to redo." : "Nothing is available to undo.");

        var stream = lease.Stream;
        cancellationToken.ThrowIfCancellationRequested();
        lock (_registryLock)
        {
            if (!stream.HasBaseline || !string.Equals(Hash(currentSnapshot), CurrentHash(stream), StringComparison.Ordinal))
            {
                ResetStreamLocked(stream, currentSnapshot);
                stream.LastAccessUtc = DateTime.UtcNow;
                stream.Revision++;
                EnforceBudgetLocked(stream);
                return new AuthoringHistoryMutation(EmptyState, string.Empty, currentSnapshot, string.Empty);
            }

            if (moveForward && stream.Cursor >= stream.Entries.Count)
                throw new InvalidOperationException("Nothing is available to redo.");
            if (!moveForward && stream.Cursor == 0)
                throw new InvalidOperationException("Nothing is available to undo.");
            var entry = stream.Entries[moveForward ? stream.Cursor : stream.Cursor - 1];
            if (entry.CompoundActionId is not null)
                throw new InvalidOperationException("This action spans multiple manuscripts and requires the compound-aware history boundary.");
        }

        string snapshot;
        string selection;
        string label;
        lock (_registryLock)
        {
            if (moveForward)
            {
                var action = stream.Entries[stream.Cursor];
                snapshot = Decompress(action.Snapshot);
                selection = action.SelectionJson;
                label = action.ActionLabel;
            }
            else
            {
                var action = stream.Entries[stream.Cursor - 1];
                snapshot = stream.Cursor == 1
                    ? Decompress(stream.BaselineSnapshot!)
                    : Decompress(stream.Entries[stream.Cursor - 2].Snapshot);
                selection = stream.Cursor <= 1 ? string.Empty : stream.Entries[stream.Cursor - 2].SelectionJson;
                label = action.ActionLabel;
            }
        }

        // The owning domain service commits the callback before the runtime
        // advances its cursor. A failed callback therefore leaves history intact.
        await applySnapshot(snapshot, cancellationToken);

        lock (_registryLock)
        {
            stream.Cursor += moveForward ? 1 : -1;
            stream.LastWasCursorMove = true;
            stream.LastAccessUtc = DateTime.UtcNow;
            stream.Revision++;
            var state = BuildState(stream);
            return new AuthoringHistoryMutation(state, label, snapshot, selection);
        }
    }

    public async Task ClearAsync(
        AuthoringHistoryTarget target,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireAsync(target, create: false, cancellationToken);
        if (lease is null)
            return;

        cancellationToken.ThrowIfCancellationRequested();
        lock (_registryLock)
        {
            var stream = lease.Stream;
            stream.Retiring = true;
            _streams.Remove(stream.RegistryKey);
            ClearStreamLocked(stream);
        }
    }

    public Task DeleteDocumentHistoryAsync(
        Guid projectId,
        AuthoringHistoryDocumentKind kind,
        Guid documentId,
        Guid? editionId = null,
        CancellationToken cancellationToken = default) =>
        ClearAsync(new AuthoringHistoryTarget(projectId, kind, documentId, editionId), cancellationToken);

    public async Task ClearProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AuthoringHistoryTarget> targets;
        lock (_registryLock)
        {
            targets = _streams.Values
                .Where(stream => stream.Target.ProjectId == projectId)
                .Select(stream => stream.Target)
                .ToList();
        }

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ClearAsync(target, cancellationToken);
        }
    }

    public Task<IReadOnlyList<AuthoringHistoryTarget>> FindDependentStreamsAsync(
        Guid projectId,
        AuthoringHistoryDependencyKind kind,
        Guid resourceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_registryLock)
        {
            var key = new DependencyKey(projectId, kind, resourceId);
            if (!_dependencyIndex.TryGetValue(key, out var streamKeys))
                return Task.FromResult<IReadOnlyList<AuthoringHistoryTarget>>([]);

            var result = streamKeys
                .Select(streamKey => _streams.TryGetValue(streamKey, out var stream) ? stream.Target : null)
                .Where(target => target is not null)
                .Cast<AuthoringHistoryTarget>()
                .OrderBy(target => target.RegistryKey, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult<IReadOnlyList<AuthoringHistoryTarget>>(result);
        }
    }

    public async Task ClearDependentStreamsAsync(
        Guid projectId,
        AuthoringHistoryDependencyKind kind,
        Guid resourceId,
        CancellationToken cancellationToken = default)
    {
        var targets = await FindDependentStreamsAsync(projectId, kind, resourceId, cancellationToken);
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ClearAsync(target, cancellationToken);
        }
    }

    public async Task UpdateSelectionAsync(
        AuthoringHistoryTarget target,
        string selectionJson,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireAsync(target, create: false, cancellationToken);
        if (lease is null)
            return;

        cancellationToken.ThrowIfCancellationRequested();
        lock (_registryLock)
        {
            if (lease.Stream.Cursor > 0)
                lease.Stream.Entries[lease.Stream.Cursor - 1].SelectionJson = selectionJson;
            lease.Stream.LastAccessUtc = DateTime.UtcNow;
        }
    }

    private async Task<StreamLease?> AcquireAsync(
        AuthoringHistoryTarget target,
        bool create,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var key = target.RegistryKey;
        while (true)
        {
            HistoryStream? stream;
            lock (_registryLock)
            {
                if (!_streams.TryGetValue(key, out stream))
                {
                    if (!create)
                        return null;

                    stream = new HistoryStream(target);
                    _streams.Add(key, stream);
                }

                if (!stream.Retiring)
                {
                    stream.ActiveOperations++;
                    stream.LastAccessUtc = DateTime.UtcNow;
                }
            }

            if (stream is null)
                continue;

            if (stream.Retiring)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                continue;
            }

            try
            {
                await stream.Gate.WaitAsync(cancellationToken);
                lock (_registryLock)
                {
                    if (!stream.Retiring)
                        return new StreamLease(this, stream);
                }

                // A clear/delete retired this stream while this operation was
                // waiting. Release the gate and reservation, then retry against
                // the replacement stream.
                Release(stream);
            }
            catch
            {
                ReleaseReservation(stream);
                throw;
            }
        }
    }

    private void ReleaseReservation(HistoryStream stream)
    {
        lock (_registryLock)
        {
            stream.ActiveOperations--;
            EnforceBudgetLocked();
        }
    }

    private void Release(HistoryStream stream)
    {
        stream.Gate.Release();
        lock (_registryLock)
        {
            stream.ActiveOperations--;
            stream.LastAccessUtc = DateTime.UtcNow;
            EnforceBudgetLocked();
        }
    }

    private static readonly AuthoringHistoryState EmptyState = new(false, false, null, null);

    private static AuthoringHistoryState BuildState(HistoryStream stream)
    {
        var canUndo = stream.Cursor > 0;
        var canRedo = stream.Cursor < stream.Entries.Count;
        return new AuthoringHistoryState(
            canUndo,
            canRedo,
            canUndo ? stream.Entries[stream.Cursor - 1].ActionLabel : null,
            canRedo ? stream.Entries[stream.Cursor].ActionLabel : null);
    }

    private static string CurrentHash(HistoryStream stream) =>
        stream.Cursor == 0
            ? stream.BaselineHash
            : stream.Entries[stream.Cursor - 1].Hash;

    private void ResetStreamLocked(HistoryStream stream, string snapshot)
    {
        lock (_registryLock)
        {
            ClearSnapshotsLocked(stream);
            stream.BaselineSnapshot = Compress(snapshot);
            _compressedBytes += stream.BaselineSnapshot.Data.Length;
            stream.BaselineHash = Hash(snapshot);
            stream.HasBaseline = true;
            stream.Cursor = 0;
            stream.LastWasCursorMove = false;
            RebuildDependenciesLocked(stream);
            EnforceBudgetLocked(stream);
        }
    }

    private void ClearStreamLocked(HistoryStream stream)
    {
        ClearSnapshotsLocked(stream);
        stream.BaselineSnapshot = null;
        stream.BaselineHash = string.Empty;
        stream.HasBaseline = false;
        stream.Cursor = 0;
        stream.LastWasCursorMove = false;
        RebuildDependenciesLocked(stream);
    }

    private void ClearSnapshotsLocked(HistoryStream stream)
    {
        if (stream.BaselineSnapshot is not null)
            _compressedBytes -= stream.BaselineSnapshot.Data.Length;
        foreach (var entry in stream.Entries)
            _compressedBytes -= entry.Snapshot.Data.Length;
        stream.BaselineSnapshot = null;
        stream.Entries.Clear();
        stream.Cursor = 0;
        stream.LastWasCursorMove = false;
    }

    private void RemoveRedoBranchLocked(HistoryStream stream)
    {
        if (stream.Cursor >= stream.Entries.Count)
            return;

        foreach (var entry in stream.Entries.Skip(stream.Cursor))
            _compressedBytes -= entry.Snapshot.Data.Length;
        stream.Entries.RemoveRange(stream.Cursor, stream.Entries.Count - stream.Cursor);
        RebuildDependenciesLocked(stream);
    }

    private void ReplaceEntrySnapshotLocked(HistoryStream stream, HistoryEntry entry, string snapshot)
    {
        _compressedBytes -= entry.Snapshot.Data.Length;
        entry.Snapshot = Compress(snapshot);
        entry.Hash = Hash(snapshot);
        _compressedBytes += entry.Snapshot.Data.Length;
        RebuildDependenciesLocked(stream);
    }

    private void PruneLocked(HistoryStream stream)
    {
        while (stream.Entries.Count > MaxActionsPerStream)
        {
            var removed = stream.Entries[0];
            if (stream.BaselineSnapshot is not null)
                _compressedBytes -= stream.BaselineSnapshot.Data.Length;
            stream.Entries.RemoveAt(0);
            stream.BaselineSnapshot = removed.Snapshot;
            stream.BaselineHash = removed.Hash;
            stream.HasBaseline = true;
            stream.Cursor = Math.Max(0, stream.Cursor - 1);
        }

        RebuildDependenciesLocked(stream);
    }

    private void RebuildDependenciesLocked(HistoryStream stream)
    {
        foreach (var key in stream.Dependencies)
        {
            if (_dependencyIndex.TryGetValue(key, out var streams))
            {
                streams.Remove(stream.RegistryKey);
                if (streams.Count == 0)
                    _dependencyIndex.Remove(key);
            }
        }

        stream.Dependencies.Clear();
        if (!stream.HasBaseline)
            return;

        var snapshots = new List<string>();
        if (stream.Entries.Count > 0)
        {
            snapshots.Add(Decompress(stream.BaselineSnapshot!));
            snapshots.AddRange(stream.Entries.Select(entry => Decompress(entry.Snapshot)));
        }

        // A baseline by itself is the live document, not retained Undo/Redo
        // history. Only streams with at least one action retain dependencies.
        if (stream.Entries.Count == 0)
            return;

        foreach (var dependency in AuthoringDependencyScanner.FindDependencies([.. snapshots]))
            stream.Dependencies.Add(new DependencyKey(stream.Target.ProjectId, dependency.Kind, dependency.ResourceId));
        if (stream.Target.Kind == AuthoringHistoryDocumentKind.DesignedPageContent)
        {
            stream.Dependencies.Add(new DependencyKey(
                stream.Target.ProjectId,
                AuthoringHistoryDependencyKind.DesignedPage,
                stream.Target.DocumentId));
        }

        foreach (var key in stream.Dependencies)
        {
            if (!_dependencyIndex.TryGetValue(key, out var streams))
            {
                streams = new HashSet<string>(StringComparer.Ordinal);
                _dependencyIndex.Add(key, streams);
            }

            streams.Add(stream.RegistryKey);
        }
    }

    private void EnforceBudgetLocked(HistoryStream? preferred = null)
    {
        if (_compressedBytes <= MaxCompressedBytes)
            return;

        foreach (var stream in _streams.Values
                     .Where(stream => stream != preferred && stream.ActiveOperations == 0)
                     .OrderBy(stream => stream.LastAccessUtc)
                     .ToList())
        {
            if (_compressedBytes <= MaxCompressedBytes)
                break;

            _streams.Remove(stream.RegistryKey);
            ClearSnapshotsLocked(stream);
            RebuildDependenciesLocked(stream);
        }
    }

    private static string NormalizeLabel(string value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return normalized[..Math.Min(normalized.Length, 180)];
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool HashesEqual(string left, string right) =>
        string.Equals(Hash(left), Hash(right), StringComparison.Ordinal);

    private static SnapshotBlob Compress(string value)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            brotli.Write(bytes);
        }

        return new SnapshotBlob(output.ToArray());
    }

    private static string Decompress(SnapshotBlob snapshot)
    {
        using var input = new MemoryStream(snapshot.Data, writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(brotli, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed class HistoryStream(AuthoringHistoryTarget target)
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly List<HistoryEntry> Entries = [];
        public readonly HashSet<DependencyKey> Dependencies = [];
        public readonly AuthoringHistoryTarget Target = target;
        public SnapshotBlob? BaselineSnapshot;
        public string BaselineHash = string.Empty;
        public bool HasBaseline;
        public int Cursor;
        public bool LastWasCursorMove;
        public int ActiveOperations;
        public long Revision;
        public DateTime LastAccessUtc = DateTime.UtcNow;
        public string RegistryKey => Target.RegistryKey;
        public bool Retiring;
    }

    private sealed class HistoryEntry(
        SnapshotBlob snapshot,
        string hash,
        string actionLabel,
        string selectionJson,
        DateTime recordedAt,
        Guid? compoundActionId = null,
        IReadOnlyList<string>? compoundParticipantKeys = null)
    {
        public SnapshotBlob Snapshot { get; set; } = snapshot;
        public string Hash { get; set; } = hash;
        public string ActionLabel { get; } = actionLabel;
        public string SelectionJson { get; set; } = selectionJson;
        public DateTime RecordedAt { get; set; } = recordedAt;
        public Guid? CompoundActionId { get; } = compoundActionId;
        public IReadOnlyList<string> CompoundParticipantKeys { get; } = compoundParticipantKeys ?? [];
    }

    private sealed record SnapshotBlob(byte[] Data);

    private readonly record struct DependencyKey(
        Guid ProjectId,
        AuthoringHistoryDependencyKind Kind,
        Guid ResourceId);

    private sealed class StreamLease(AuthoringHistoryRuntime owner, HistoryStream stream) : IAsyncDisposable
    {
        private int _disposed;
        public HistoryStream Stream => stream;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Release(stream);
            return ValueTask.CompletedTask;
        }
    }
}
