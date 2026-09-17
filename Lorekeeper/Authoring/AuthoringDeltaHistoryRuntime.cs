using System.Text.Json;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;

namespace Lorekeeper.Authoring;

public sealed record AuthoringDeltaHistoryRecordResult(
    bool Undoable,
    AuthoringHistoryState State,
    AuthoringHistoryCursorV1 Cursor);

public sealed record AuthoringDeltaHistoryMove(
    Guid ReservationId,
    AuthoringHistoryCursorActionV1 Action,
    bool IsUndo,
    AuthoringHistoryState State,
    AuthoringHistoryCursorV1 Cursor);

public sealed record AuthoringDeltaHistoryStage(
    Guid StageId,
    AuthoringDeltaHistoryRecordResult Projected);

public interface IAuthoringDeltaHistoryRuntime
{
    AuthoringDeltaHistoryStage Stage(
        Guid projectId,
        IReadOnlyList<string> targetIds,
        IReadOnlyDictionary<string, long> generations,
        string actionLabel,
        IReadOnlyList<AuthoringOperationV1> forward,
        IReadOnlyList<AuthoringOperationV1> inverse,
        AuthoringSelectionV1? beforeSelection,
        AuthoringSelectionV1? afterSelection);

    AuthoringDeltaHistoryRecordResult Confirm(Guid stageId);
    void Discard(Guid stageId);

    (AuthoringHistoryState State, AuthoringHistoryCursorV1 Cursor) Read(string targetId);
    AuthoringDeltaHistoryMove? Undo(string targetId);
    AuthoringDeltaHistoryMove? Redo(string targetId);
    void ConfirmMove(Guid reservationId);
    void DiscardMove(Guid reservationId);
    void Clear(string targetId);
    void ClearProject(Guid projectId);
    IReadOnlyList<string> FindDependentTargets(
        Guid projectId,
        AuthoringHistoryDependencyKind kind,
        Guid resourceId);
    void ClearDependentTargets(
        Guid projectId,
        AuthoringHistoryDependencyKind kind,
        Guid resourceId);
    void SetActive(string targetId, bool active);
}

internal sealed class AuthoringDeltaHistoryRuntime : IAuthoringDeltaHistoryRuntime
{
    private readonly int _maxActionsPerTarget;
    private readonly long _maxProcessHistoryBytes;
    private readonly object _gate = new();
    private readonly Dictionary<string, Stream> _streams = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, StoredAction> _actions = [];
    private readonly Dictionary<Guid, StoredAction> _pending = [];
    private readonly HashSet<Guid> _reservedEvictions = [];
    private readonly Dictionary<DependencyKey, HashSet<Guid>> _dependencyActions = [];
    private readonly Dictionary<Guid, MoveReservation> _moveReservations = [];
    private long _bytes;
    private long _pendingBytes;

    internal AuthoringDeltaHistoryRuntime(
        int maxActionsPerTarget = AuthoringProtocolV1.MaxActionsPerTarget,
        long maxProcessHistoryBytes = AuthoringProtocolV1.MaxProcessHistoryBytes)
    {
        if (maxActionsPerTarget <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxActionsPerTarget));
        if (maxProcessHistoryBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxProcessHistoryBytes));
        _maxActionsPerTarget = maxActionsPerTarget;
        _maxProcessHistoryBytes = maxProcessHistoryBytes;
    }

    public AuthoringDeltaHistoryStage Stage(
        Guid projectId,
        IReadOnlyList<string> targetIds,
        IReadOnlyDictionary<string, long> generations,
        string actionLabel,
        IReadOnlyList<AuthoringOperationV1> forward,
        IReadOnlyList<AuthoringOperationV1> inverse,
        AuthoringSelectionV1? beforeSelection,
        AuthoringSelectionV1? afterSelection)
    {
        var distinctTargets = targetIds.Distinct(StringComparer.Ordinal).ToList();
        if (distinctTargets.Count == 0)
            throw new ArgumentException("A history action requires at least one target.", nameof(targetIds));
        var projection = new AuthoringHistoryCursorActionV1(
            Guid.NewGuid(),
            string.IsNullOrWhiteSpace(actionLabel) ? "Edit document" : actionLabel.Trim(),
            distinctTargets,
            generations,
            forward.ToList(),
            inverse.ToList(),
            beforeSelection,
            afterSelection);
        var size = JsonSerializer.SerializeToUtf8Bytes(projection, ManuscriptCodec.JsonOptions).LongLength;
        lock (_gate)
        {
            var stageId = Guid.NewGuid();
            var stored = new StoredAction(
                projectId,
                projection,
                size,
                DateTime.UtcNow,
                CollectDependencies(projectId, distinctTargets, forward, inverse));
            _pending.Add(stageId, stored);
            if (size > _maxProcessHistoryBytes || !ReservePendingCapacityLocked(stored))
            {
                stored.ProjectedUndoable = false;
                return new(stageId, new(false, EmptyState, EmptyCursor));
            }
            stored.Reserved = true;
            stored.ProjectedUndoable = true;
            var primary = GetOrCreate(distinctTargets[0]);
            var projectedActions = Cursor(primary).Actions.ToList();
            projectedActions.RemoveRange(primary.Position, projectedActions.Count - primary.Position);
            projectedActions.Add(projection);
            return new(stageId, new(
                true,
                new(true, false, projection.ActionLabel, null),
                new(projectedActions.Count, projectedActions)));
        }
    }

    public AuthoringDeltaHistoryRecordResult Confirm(Guid stageId)
    {
        lock (_gate)
        {
            if (!_pending.Remove(stageId, out var stored))
                throw new InvalidOperationException("The pending authoring history stage was not found.");
            var distinctTargets = stored.Projection.TargetIds;
            if (stored.Reserved)
            {
                _pendingBytes -= stored.SizeBytes;
                foreach (var actionId in stored.PlannedEvictions)
                {
                    _reservedEvictions.Remove(actionId);
                    RemoveActionLocked(actionId);
                }
            }
            if (!stored.ProjectedUndoable)
            {
                foreach (var targetId in distinctTargets)
                    ClearLocked(targetId);
                return new(false, EmptyState, EmptyCursor);
            }
            foreach (var targetId in distinctTargets)
                RemoveRedoLocked(GetOrCreate(targetId));
            _actions.Add(stored.Projection.ActionId, stored);
            _bytes += stored.SizeBytes;
            foreach (var dependency in stored.Dependencies)
            {
                if (!_dependencyActions.TryGetValue(dependency, out var actionIds))
                {
                    actionIds = [];
                    _dependencyActions.Add(dependency, actionIds);
                }
                actionIds.Add(stored.Projection.ActionId);
            }
            foreach (var targetId in distinctTargets)
            {
                var stream = GetOrCreate(targetId);
                stream.ActionIds.Add(stored.Projection.ActionId);
                stream.Position = stream.ActionIds.Count;
                stream.LastAccessUtc = stored.LastAccessUtc;
                while (stream.ActionIds.Count > _maxActionsPerTarget)
                    RemoveActionLocked(stream.ActionIds[0]);
            }
            if (!EnforceBudgetLocked())
            {
                RemoveActionLocked(stored.Projection.ActionId);
                foreach (var targetId in distinctTargets)
                    ClearLocked(targetId);
                return new(false, EmptyState, EmptyCursor);
            }
            var primary = GetOrCreate(distinctTargets[0]);
            return new(true, State(primary), Cursor(primary));
        }
    }

    public void Discard(Guid stageId)
    {
        lock (_gate)
        {
            if (_pending.Remove(stageId, out var stored) && stored.Reserved)
            {
                _pendingBytes -= stored.SizeBytes;
                foreach (var actionId in stored.PlannedEvictions)
                    _reservedEvictions.Remove(actionId);
            }
        }
    }

    public (AuthoringHistoryState State, AuthoringHistoryCursorV1 Cursor) Read(string targetId)
    {
        lock (_gate)
        {
            if (!_streams.TryGetValue(targetId, out var stream))
                return (EmptyState, EmptyCursor);
            stream.LastAccessUtc = DateTime.UtcNow;
            return (State(stream), Cursor(stream));
        }
    }

    public AuthoringDeltaHistoryMove? Undo(string targetId) => Move(targetId, undo: true);
    public AuthoringDeltaHistoryMove? Redo(string targetId) => Move(targetId, undo: false);

    public void Clear(string targetId)
    {
        lock (_gate)
            ClearLocked(targetId);
    }

    public void ClearProject(Guid projectId)
    {
        lock (_gate)
        {
            foreach (var actionId in _actions.Values
                         .Where(item => item.ProjectId == projectId)
                         .Select(item => item.Projection.ActionId)
                         .ToList())
                RemoveActionLocked(actionId);
        }
    }

    public IReadOnlyList<string> FindDependentTargets(
        Guid projectId,
        AuthoringHistoryDependencyKind kind,
        Guid resourceId)
    {
        lock (_gate)
        {
            var key = new DependencyKey(projectId, kind, resourceId);
            return !_dependencyActions.TryGetValue(key, out var actionIds)
                ? []
                : actionIds.SelectMany(actionId => _actions.TryGetValue(actionId, out var action)
                        ? action.Projection.TargetIds
                        : [])
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToList();
        }
    }

    public void ClearDependentTargets(
        Guid projectId,
        AuthoringHistoryDependencyKind kind,
        Guid resourceId)
    {
        lock (_gate)
        {
            var key = new DependencyKey(projectId, kind, resourceId);
            if (!_dependencyActions.TryGetValue(key, out var actionIds))
                return;
            var targetIds = actionIds.SelectMany(actionId => _actions.TryGetValue(actionId, out var action)
                    ? action.Projection.TargetIds
                    : [])
                .Distinct(StringComparer.Ordinal)
                .ToList();
            foreach (var targetId in targetIds)
                ClearLocked(targetId);
        }
    }

    public void SetActive(string targetId, bool active)
    {
        lock (_gate)
        {
            var stream = GetOrCreate(targetId);
            stream.ActiveCount = Math.Max(0, stream.ActiveCount + (active ? 1 : -1));
        }
    }

    private AuthoringDeltaHistoryMove? Move(string targetId, bool undo)
    {
        lock (_gate)
        {
            if (!_streams.TryGetValue(targetId, out var stream))
                return null;
            if (stream.ReservedMoveId is not null)
                return null;
            var index = undo ? stream.Position - 1 : stream.Position;
            if (index < 0 || index >= stream.ActionIds.Count)
                return null;
            var action = _actions[stream.ActionIds[index]];
            var participantIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var participant in action.Projection.TargetIds)
            {
                if (!_streams.TryGetValue(participant, out var participantStream)
                    || participantStream.ReservedMoveId is not null
                    || (index = participantStream.ActionIds.IndexOf(action.Projection.ActionId)) < 0
                    || (undo ? participantStream.Position != index + 1 : participantStream.Position != index))
                {
                    foreach (var affected in action.Projection.TargetIds)
                        ClearLocked(affected);
                    return null;
                }
                participantIndexes.Add(participant, index);
            }
            var reservationId = Guid.NewGuid();
            var originalPositions = action.Projection.TargetIds.ToDictionary(
                participant => participant,
                participant => _streams[participant].Position,
                StringComparer.Ordinal);
            foreach (var participant in action.Projection.TargetIds)
            {
                var participantStream = _streams[participant];
                participantStream.Position = participantIndexes[participant] + (undo ? 0 : 1);
                participantStream.LastAccessUtc = DateTime.UtcNow;
                participantStream.ReservedMoveId = reservationId;
            }
            _moveReservations.Add(reservationId, new(action.Projection.TargetIds, originalPositions));
            action.LastAccessUtc = DateTime.UtcNow;
            return new(reservationId, action.Projection, undo, State(stream), Cursor(stream));
        }
    }

    public void ConfirmMove(Guid reservationId)
    {
        lock (_gate)
        {
            if (!_moveReservations.Remove(reservationId, out var reservation))
                throw new InvalidOperationException("The authoring history move reservation was not found.");
            foreach (var targetId in reservation.TargetIds)
            {
                if (_streams.TryGetValue(targetId, out var stream)
                    && stream.ReservedMoveId == reservationId)
                    stream.ReservedMoveId = null;
            }
        }
    }

    public void DiscardMove(Guid reservationId)
    {
        lock (_gate)
        {
            if (!_moveReservations.Remove(reservationId, out var reservation))
                return;
            foreach (var (targetId, position) in reservation.OriginalPositions)
            {
                if (!_streams.TryGetValue(targetId, out var stream)
                    || stream.ReservedMoveId != reservationId)
                    continue;
                stream.Position = position;
                stream.ReservedMoveId = null;
            }
        }
    }

    private bool EnforceBudgetLocked()
    {
        while (_bytes > _maxProcessHistoryBytes)
        {
            var candidate = _actions.Values
                .Where(item => item.Projection.TargetIds.All(target =>
                    !_streams.TryGetValue(target, out var stream) || stream.ActiveCount == 0))
                .OrderBy(item => item.LastAccessUtc)
                .FirstOrDefault();
            if (candidate is null)
                return false;
            RemoveActionLocked(candidate.Projection.ActionId);
        }
        return true;
    }

    private bool ReservePendingCapacityLocked(StoredAction pending)
    {
        _pendingBytes += pending.SizeBytes;
        while (EffectiveReservedBytesLocked() > _maxProcessHistoryBytes)
        {
            var candidate = _actions.Values
                .Where(item => !_reservedEvictions.Contains(item.Projection.ActionId))
                .Where(item => item.Projection.TargetIds.All(target =>
                    !_streams.TryGetValue(target, out var stream) || stream.ActiveCount == 0))
                .OrderBy(item => item.LastAccessUtc)
                .FirstOrDefault();
            if (candidate is null)
            {
                _pendingBytes -= pending.SizeBytes;
                foreach (var actionId in pending.PlannedEvictions)
                    _reservedEvictions.Remove(actionId);
                pending.PlannedEvictions.Clear();
                return false;
            }
            pending.PlannedEvictions.Add(candidate.Projection.ActionId);
            _reservedEvictions.Add(candidate.Projection.ActionId);
        }
        return true;
    }

    private long EffectiveReservedBytesLocked() =>
        _bytes + _pendingBytes - _reservedEvictions.Sum(actionId =>
            _actions.TryGetValue(actionId, out var action) ? action.SizeBytes : 0);

    private void RemoveRedoLocked(Stream stream)
    {
        while (stream.Position < stream.ActionIds.Count)
            RemoveActionLocked(stream.ActionIds[^1]);
    }

    private void ClearLocked(string targetId)
    {
        if (!_streams.TryGetValue(targetId, out var stream))
            return;
        foreach (var actionId in stream.ActionIds.ToList())
            RemoveActionLocked(actionId);
        stream.Position = 0;
        stream.ReservedMoveId = null;
        if (stream.ActiveCount == 0)
            _streams.Remove(targetId);
    }

    private void RemoveActionLocked(Guid actionId)
    {
        if (!_actions.Remove(actionId, out var action))
            return;
        _bytes -= action.SizeBytes;
        foreach (var dependency in action.Dependencies)
        {
            if (!_dependencyActions.TryGetValue(dependency, out var actionIds))
                continue;
            actionIds.Remove(actionId);
            if (actionIds.Count == 0)
                _dependencyActions.Remove(dependency);
        }
        foreach (var targetId in action.Projection.TargetIds)
        {
            if (!_streams.TryGetValue(targetId, out var stream))
                continue;
            var index = stream.ActionIds.IndexOf(actionId);
            if (index < 0)
                continue;
            stream.ActionIds.RemoveAt(index);
            if (stream.Position > index)
                stream.Position--;
        }
    }

    private Stream GetOrCreate(string targetId)
    {
        if (!_streams.TryGetValue(targetId, out var stream))
        {
            stream = new Stream();
            _streams.Add(targetId, stream);
        }
        return stream;
    }

    private AuthoringHistoryCursorV1 Cursor(Stream stream) => new(
        stream.Position,
        stream.ActionIds.Select(id => _actions[id].Projection).ToList());

    private AuthoringHistoryState State(Stream stream) => new(
        stream.Position > 0,
        stream.Position < stream.ActionIds.Count,
        stream.Position > 0 ? _actions[stream.ActionIds[stream.Position - 1]].Projection.ActionLabel : null,
        stream.Position < stream.ActionIds.Count ? _actions[stream.ActionIds[stream.Position]].Projection.ActionLabel : null);

    private static AuthoringHistoryState EmptyState { get; } = new(false, false, null, null);
    private static AuthoringHistoryCursorV1 EmptyCursor { get; } = new(0, []);

    private sealed class Stream
    {
        public List<Guid> ActionIds { get; } = [];
        public int Position { get; set; }
        public DateTime LastAccessUtc { get; set; }
        public int ActiveCount { get; set; }
        public Guid? ReservedMoveId { get; set; }
    }

    private sealed class StoredAction(
        Guid projectId,
        AuthoringHistoryCursorActionV1 projection,
        long sizeBytes,
        DateTime lastAccessUtc,
        IReadOnlySet<DependencyKey> dependencies)
    {
        public Guid ProjectId { get; } = projectId;
        public AuthoringHistoryCursorActionV1 Projection { get; } = projection;
        public long SizeBytes { get; } = sizeBytes;
        public DateTime LastAccessUtc { get; set; } = lastAccessUtc;
        public bool Reserved { get; set; }
        public bool ProjectedUndoable { get; set; }
        public List<Guid> PlannedEvictions { get; } = [];
        public IReadOnlySet<DependencyKey> Dependencies { get; } = dependencies;
    }

    private readonly record struct DependencyKey(
        Guid ProjectId,
        AuthoringHistoryDependencyKind Kind,
        Guid ResourceId);
    private sealed record MoveReservation(
        IReadOnlyList<string> TargetIds,
        IReadOnlyDictionary<string, int> OriginalPositions);
    private static IReadOnlySet<DependencyKey> CollectDependencies(
        Guid projectId,
        IReadOnlyList<string> targetIds,
        IReadOnlyList<AuthoringOperationV1> forward,
        IReadOnlyList<AuthoringOperationV1> inverse)
    {
        var result = new HashSet<DependencyKey>();
        foreach (var targetId in targetIds)
        {
            ParsedAuthoringTarget parsed;
            try
            {
                parsed = AuthoringPersistence.ParseTarget(projectId, targetId);
            }
            catch (ArgumentException)
            {
                continue;
            }
            if (parsed.HistoryTarget.Kind == AuthoringHistoryDocumentKind.DesignedPageContent)
            {
                result.Add(new(
                    projectId,
                    AuthoringHistoryDependencyKind.DesignedPage,
                    parsed.HistoryTarget.DocumentId));
            }
        }
        foreach (var operation in forward.Concat(inverse))
        {
            Add(AuthoringHistoryDependencyKind.ProjectImage, operation.ImageId);
            Add(AuthoringHistoryDependencyKind.DesignedPage, operation.PageId);
            Add(AuthoringHistoryDependencyKind.ProjectImage, operation.CanonicalBlock?.ImageId);
            Add(AuthoringHistoryDependencyKind.DesignedPage, operation.CanonicalBlock?.DesignedPageId);
            AddProjectFont(operation.ParagraphPresentation?.FontFamilyKey);
            AddProjectFont(operation.CanonicalBlock?.ParagraphPresentation?.FontFamilyKey);
            if (operation.CanonicalObject is { ValueKind: JsonValueKind.Object } canonicalObject)
            {
                if (canonicalObject.TryGetProperty("imageId", out var image)
                    && image.ValueKind == JsonValueKind.String
                    && image.TryGetGuid(out var imageId))
                    Add(AuthoringHistoryDependencyKind.ProjectImage, imageId);
                if (canonicalObject.TryGetProperty("fontFamilyKey", out var font))
                    AddProjectFont(font.GetString());
            }
            if (operation.PropertyPatch is { ValueKind: JsonValueKind.Object } patch)
            {
                if (patch.TryGetProperty("imageId", out var image)
                    && image.ValueKind == JsonValueKind.String
                    && image.TryGetGuid(out var imageId))
                    Add(AuthoringHistoryDependencyKind.ProjectImage, imageId);
                if (patch.TryGetProperty("fontFamilyKey", out var font))
                    AddProjectFont(font.GetString());
            }
        }
        return result;

        void Add(AuthoringHistoryDependencyKind kind, Guid? id)
        {
            if (id is Guid value && value != Guid.Empty)
                result.Add(new(projectId, kind, value));
        }

        void AddProjectFont(string? key)
        {
            const string prefix = "project:";
            if (key?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
                && Guid.TryParse(key[prefix.Length..], out var id))
                Add(AuthoringHistoryDependencyKind.ProjectFont, id);
        }
    }

}
