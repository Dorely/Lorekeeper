using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Lorekeeper.Authoring;

public enum AuthoringHistoryDocumentKind
{
    CoreChapter,
    EditionChapter,
    PublicationSection,
    PageComposition,
    CoreCover,
    ReleaseCover
}

public enum AuthoringHistoryDependencyKind
{
    ProjectImage,
    ProjectFont,
    PageComposition
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
        AuthoringHistoryDocumentKind.PageComposition => $"page-composition:{DocumentId:D}",
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
    string SelectionJson);

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
        if (stream.Target.Kind == AuthoringHistoryDocumentKind.PageComposition)
        {
            stream.Dependencies.Add(new DependencyKey(
                stream.Target.ProjectId,
                AuthoringHistoryDependencyKind.PageComposition,
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
        DateTime recordedAt)
    {
        public SnapshotBlob Snapshot { get; set; } = snapshot;
        public string Hash { get; set; } = hash;
        public string ActionLabel { get; } = actionLabel;
        public string SelectionJson { get; set; } = selectionJson;
        public DateTime RecordedAt { get; set; } = recordedAt;
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
