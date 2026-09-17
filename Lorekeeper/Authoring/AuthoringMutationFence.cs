using Lorekeeper.Persistence;

namespace Lorekeeper.Authoring;

public sealed record AuthoringWriterState(
    AuthoringTargetReferenceV1 Target,
    Guid SessionId,
    Guid WriterId,
    long Generation,
    long HighestLocalSequence,
    long LastDispatchedSequence,
    long LastAcknowledgedSequence,
    bool IsDirty,
    bool IsRecoverable,
    bool IsReachable = true);

public sealed record AuthoringWriterFlushResult(
    long FlushedSequence,
    bool IsDirty,
    bool IsRecoverable,
    bool IsReachable,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record AuthoringWriterRegistration(
    AuthoringTargetReferenceV1 Target,
    Guid SessionId,
    Guid WriterId,
    Func<long, CancellationToken, Task<AuthoringWriterFlushResult>> FreezeAndFlushAsync,
    Func<CancellationToken, Task> ResumeAsync);

public sealed record AuthoringFenceRequest(
    Guid ProjectId,
    IReadOnlyList<string> TargetIds,
    string Purpose,
    IReadOnlyDictionary<string, long>? ExpectedGenerations = null);

public sealed record AuthoringFenceTargetState(
    string TargetId,
    Guid? SessionId,
    long CapturedSequence,
    long Generation);

public sealed record AuthoringFenceContext(
    Guid ProcessIncarnationId,
    IReadOnlyList<AuthoringFenceTargetState> Targets);

public sealed class AuthoringMutationFenceException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public interface IAuthoringMutationFence
{
    Guid ProcessIncarnationId { get; }

    ValueTask<IAsyncDisposable> RegisterWriterAsync(
        AuthoringWriterRegistration registration,
        CancellationToken cancellationToken = default);

    void UpdateWriterState(AuthoringWriterState state);

    Task<T> ExecuteAsync<T>(
        AuthoringFenceRequest request,
        Func<AuthoringFenceContext, CancellationToken, Task<T>> consume,
        CancellationToken cancellationToken = default);
}

internal sealed class AuthoringMutationFence(
    IAppDatabaseOperationFactory database,
    IProjectMutationCoordinator projectMutations,
    IAuthoringDeltaHistoryRuntime history) : IAuthoringMutationFence
{
    private static readonly AsyncLocal<FenceScope?> Ambient = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, RegisteredWriter> _writers = new(StringComparer.Ordinal);
    private readonly List<ActiveFence> _activeFences = [];

    public Guid ProcessIncarnationId { get; } = Guid.NewGuid();

    public ValueTask<IAsyncDisposable> RegisterWriterAsync(
        AuthoringWriterRegistration registration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(registration.FreezeAndFlushAsync);
        ArgumentNullException.ThrowIfNull(registration.ResumeAsync);
        var key = Key(registration.Target);
        lock (_gate)
        {
            if (_activeFences.Any(item => item.ProjectId == registration.Target.ProjectId
                    && (item.TargetIds.Count == 0 || item.TargetIds.Contains(registration.Target.TargetId))))
            {
                throw new AuthoringMutationFenceException(
                    "AUTHORING_FENCE_ACTIVE",
                    "This authoring target cannot become writable while a dependent operation is consuming its state.");
            }
            if (_writers.TryGetValue(key, out var existing))
            {
                if (existing.Registration.SessionId == registration.SessionId
                    && !existing.State.IsReachable
                    && existing.FenceCount == 0)
                {
                    var replacement = new RegisteredWriter(registration) { State = existing.State };
                    _writers[key] = replacement;
                    return ValueTask.FromResult<IAsyncDisposable>(new WriterLease(this, key, replacement));
                }
                throw new AuthoringMutationFenceException("AUTHORING_WRITER_ACTIVE", "This authoring target is already writable in another window.");
            }

            var writer = new RegisteredWriter(registration);
            _writers.Add(key, writer);
            history.SetActive(registration.Target.TargetId, active: true);
            return ValueTask.FromResult<IAsyncDisposable>(new WriterLease(this, key, writer));
        }
    }

    public void UpdateWriterState(AuthoringWriterState state)
    {
        lock (_gate)
        {
            if (!_writers.TryGetValue(Key(state.Target), out var writer)
                || writer.Registration.SessionId != state.SessionId
                || writer.Registration.WriterId != state.WriterId)
            {
                throw new AuthoringMutationFenceException("AUTHORING_WRITER_STALE", "The writable authoring lease is no longer active.");
            }

            writer.State = state;
        }
    }

    public async Task<T> ExecuteAsync<T>(
        AuthoringFenceRequest request,
        Func<AuthoringFenceContext, CancellationToken, Task<T>> consume,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consume);
        if (Ambient.Value is { } ambient)
        {
            if (ambient.ProjectId != request.ProjectId)
                throw new AuthoringMutationFenceException("AUTHORING_FENCE_PROJECT_NESTING", "A nested authoring fence cannot switch projects.");
            var nestedTargetIds = request.TargetIds.Distinct(StringComparer.Ordinal).ToList();
            if (ambient.TargetIds.Count > 0
                && (nestedTargetIds.Count == 0
                    || nestedTargetIds.Any(targetId => !ambient.TargetIds.Contains(targetId))))
                throw new AuthoringMutationFenceException("AUTHORING_FENCE_SCOPE_EXPANSION", "A nested authoring fence cannot add targets that were not frozen by its outer fence.");
            var nestedStates = await ValidateGenerationsAsync(
                request,
                SnapshotWriters(request),
                nestedTargetIds,
                cancellationToken);
            return await consume(new AuthoringFenceContext(ProcessIncarnationId, nestedStates), cancellationToken);
        }

        var activated = ActivateFence(request);
        var writers = activated.Writers;
        var frozen = new List<RegisteredWriter>(writers.Count);
        try
        {
            foreach (var writer in writers)
            {
                await WaitForPendingResumeAsync(writer, cancellationToken);
                AuthoringWriterState state;
                lock (_gate)
                    state = writer.State;
                if (!state.IsReachable)
                    throw new AuthoringMutationFenceException("AUTHORING_CLIENT_UNREACHABLE", "An authoring client with unsaved state is unreachable.");
                if (state.IsDirty && !state.IsRecoverable)
                    throw new AuthoringMutationFenceException("AUTHORING_CLIENT_NOT_RECOVERABLE", "Visible authoring changes are not durably recoverable and must be resolved first.");

                AuthoringWriterFlushResult flush;
                try
                {
                    flush = await FreezeAndFlushAsync(writer, state.HighestLocalSequence, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw new AuthoringMutationFenceException("AUTHORING_FLUSH_FAILED", exception.Message);
                }
                frozen.Add(writer);
                if (!flush.IsReachable)
                    throw new AuthoringMutationFenceException("AUTHORING_CLIENT_UNREACHABLE", flush.ErrorMessage ?? "An authoring client became unreachable while flushing.");
                if (flush.IsDirty || flush.FlushedSequence < state.HighestLocalSequence)
                    throw new AuthoringMutationFenceException(flush.ErrorCode ?? "AUTHORING_FLUSH_INCOMPLETE", flush.ErrorMessage ?? "Authoring changes did not finish saving.");
                if (!flush.IsRecoverable)
                    throw new AuthoringMutationFenceException("AUTHORING_CLIENT_NOT_RECOVERABLE", flush.ErrorMessage ?? "The flushed authoring state is not durably recoverable.");
            }

            await using var projectLease = await projectMutations.AcquireAsync(request.ProjectId, cancellationToken);
            using var sharedProjectLease = projectMutations.ShareWithNestedOperations(request.ProjectId);
            var targetIds = request.TargetIds.Count == 0
                ? writers.Select(item => item.Registration.Target.TargetId).Distinct(StringComparer.Ordinal).ToList()
                : request.TargetIds.Distinct(StringComparer.Ordinal).ToList();
            var states = await ValidateGenerationsAsync(request, writers, targetIds, cancellationToken);
            var previous = Ambient.Value;
            Ambient.Value = new FenceScope(
                request.ProjectId,
                request.TargetIds.ToHashSet(StringComparer.Ordinal));
            try
            {
                return await consume(new AuthoringFenceContext(ProcessIncarnationId, states), cancellationToken);
            }
            finally
            {
                Ambient.Value = previous;
            }
        }
        finally
        {
            var resume = EndFence(activated.Fence, writers);
            for (var index = resume.Count - 1; index >= 0; index--)
            {
                Exception? failure = null;
                try
                {
                    await resume[index].Writer.Registration.ResumeAsync(CancellationToken.None);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    CompleteResume(resume[index], failure);
                }
            }
        }
    }

    private async Task<AuthoringWriterFlushResult> FreezeAndFlushAsync(
        RegisteredWriter writer,
        long capturedSequence,
        CancellationToken cancellationToken)
    {
        await writer.FreezeGate.WaitAsync(cancellationToken);
        try
        {
            return await writer.Registration.FreezeAndFlushAsync(capturedSequence, cancellationToken);
        }
        finally
        {
            writer.FreezeGate.Release();
        }
    }

    private async Task WaitForPendingResumeAsync(
        RegisteredWriter writer,
        CancellationToken cancellationToken)
    {
        Task? pendingResume;
        lock (_gate)
            pendingResume = writer.ResumeCompletion?.Task;
        if (pendingResume is not null)
            await pendingResume.WaitAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<AuthoringFenceTargetState>> ValidateGenerationsAsync(
        AuthoringFenceRequest request,
        IReadOnlyList<RegisteredWriter> writers,
        IReadOnlyList<string> targetIds,
        CancellationToken cancellationToken)
    {
        Dictionary<string, long> generations;
        await using (var operation = await database.OpenReadAsync(cancellationToken))
        {
            generations = await AuthoringPersistence.ReadGenerationsAsync(
                operation.Db,
                request.ProjectId,
                targetIds,
                cancellationToken);
        }
        return targetIds.Select(targetId =>
        {
            var writer = writers.SingleOrDefault(item => string.Equals(item.Registration.Target.TargetId, targetId, StringComparison.Ordinal));
            var actualGeneration = generations.GetValueOrDefault(targetId);
            var expectedGeneration = request.ExpectedGenerations?.GetValueOrDefault(targetId)
                ?? writer?.State.Generation;
            if (expectedGeneration is long expected && expected != actualGeneration)
            {
                throw new AuthoringMutationFenceException(
                    "AUTHORING_GENERATION_CHANGED",
                    $"Authoring target {targetId} changed generation while the mutation fence was being acquired.");
            }
            return new AuthoringFenceTargetState(
                targetId,
                writer?.Registration.SessionId,
                writer?.State.HighestLocalSequence ?? 0,
                actualGeneration);
        }).ToList();
    }

    private List<RegisteredWriter> SnapshotWriters(AuthoringFenceRequest request)
    {
        lock (_gate)
        {
            var targetIds = request.TargetIds.ToHashSet(StringComparer.Ordinal);
            return _writers.Values
                .Where(item => item.Registration.Target.ProjectId == request.ProjectId
                    && (targetIds.Count == 0 || targetIds.Contains(item.Registration.Target.TargetId)))
                .ToList();
        }
    }

    private (ActiveFence Fence, List<RegisteredWriter> Writers) ActivateFence(AuthoringFenceRequest request)
    {
        lock (_gate)
        {
            var fence = new ActiveFence(
                request.ProjectId,
                request.TargetIds.ToHashSet(StringComparer.Ordinal));
            _activeFences.Add(fence);
            var writers = _writers.Values
                .Where(item => item.Registration.Target.ProjectId == request.ProjectId
                    && (fence.TargetIds.Count == 0 || fence.TargetIds.Contains(item.Registration.Target.TargetId)))
                .ToList();
            foreach (var writer in writers)
                writer.FenceCount++;
            return (fence, writers);
        }
    }

    private List<ResumeTransition> EndFence(ActiveFence fence, IReadOnlyList<RegisteredWriter> writers)
    {
        lock (_gate)
        {
            var resume = new List<ResumeTransition>();
            _activeFences.Remove(fence);
            foreach (var writer in writers)
            {
                writer.FenceCount = Math.Max(0, writer.FenceCount - 1);
                if (writer.FenceCount != 0 || writer.ResumeCompletion is not null)
                    continue;
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                writer.ResumeCompletion = completion;
                resume.Add(new(writer, completion));
            }
            return resume;
        }
    }

    private void CompleteResume(ResumeTransition transition, Exception? failure)
    {
        lock (_gate)
        {
            var writer = transition.Writer;
            if (ReferenceEquals(writer.ResumeCompletion, transition.Completion))
            {
                if (failure is not null)
                    writer.State = writer.State with { IsReachable = false };
                writer.ResumeCompletion = null;
                if (writer.PendingRelease && writer.FenceCount == 0)
                    RemoveWriterLocked(Key(writer.Registration.Target), writer);
            }
        }
        transition.Completion.TrySetResult();
    }

    private void Release(string key, RegisteredWriter writer)
    {
        lock (_gate)
        {
            if (_writers.TryGetValue(key, out var current) && ReferenceEquals(current, writer))
            {
                if (writer.FenceCount > 0 || writer.ResumeCompletion is not null)
                    writer.PendingRelease = true;
                else
                    RemoveWriterLocked(key, writer);
            }
        }
    }

    private void RemoveWriterLocked(string key, RegisteredWriter writer)
    {
        _writers.Remove(key);
        history.SetActive(writer.Registration.Target.TargetId, active: false);
    }

    private static string Key(AuthoringTargetReferenceV1 target) => $"{target.ProjectId:D}|{target.TargetId}";

    private sealed record FenceScope(Guid ProjectId, IReadOnlySet<string> TargetIds);
    private sealed record ActiveFence(Guid ProjectId, IReadOnlySet<string> TargetIds);
    private sealed record ResumeTransition(
        RegisteredWriter Writer,
        TaskCompletionSource Completion);

    private sealed class RegisteredWriter(AuthoringWriterRegistration registration)
    {
        public AuthoringWriterRegistration Registration { get; } = registration;
        public AuthoringWriterState State { get; set; } = new(
            registration.Target,
            registration.SessionId,
            registration.WriterId,
            0,
            0,
            0,
            0,
            IsDirty: false,
            IsRecoverable: true);
        public int FenceCount { get; set; }
        public bool PendingRelease { get; set; }
        public SemaphoreSlim FreezeGate { get; } = new(1, 1);
        public TaskCompletionSource? ResumeCompletion { get; set; }
    }

    private sealed class WriterLease(
        AuthoringMutationFence owner,
        string key,
        RegisteredWriter writer) : IAsyncDisposable
    {
        private bool _disposed;

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                owner.Release(key, writer);
            }
            return ValueTask.CompletedTask;
        }
    }
}
