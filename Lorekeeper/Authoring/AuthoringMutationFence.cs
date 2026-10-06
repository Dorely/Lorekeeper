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
    IAuthoringDeltaHistoryRuntime history,
    TimeSpan? writerResponseTimeout = null) : IAuthoringMutationFence
{
    private static readonly TimeSpan RegistrationFenceWait = TimeSpan.FromSeconds(30);
    // A writer's client can vanish mid-fence (a closed or reloaded window leaves its circuit unable to answer
    // JS interop), so every flush/resume callback is bounded; otherwise one lost answer wedges every later
    // fence and writer registration for the project until restart.
    private readonly TimeSpan _writerResponseTimeout = writerResponseTimeout ?? TimeSpan.FromSeconds(30);
    private static readonly AsyncLocal<FenceScope?> Ambient = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, RegisteredWriter> _writers = new(StringComparer.Ordinal);
    private readonly List<ActiveFence> _activeFences = [];

    public Guid ProcessIncarnationId { get; } = Guid.NewGuid();

    public async ValueTask<IAsyncDisposable> RegisterWriterAsync(
        AuthoringWriterRegistration registration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(registration.FreezeAndFlushAsync);
        ArgumentNullException.ThrowIfNull(registration.ResumeAsync);
        var key = Key(registration.Target);
        // Dependent operations such as background Review Edits reads fence the project briefly and often, so an
        // opening editor waits for them to end; only one it cannot outlast (or is running inside) fails it.
        using var fenceWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        fenceWait.CancelAfter(RegistrationFenceWait);
        while (true)
        {
            Task fenceEnded;
            lock (_gate)
            {
                var blocking = _activeFences.FirstOrDefault(item => item.ProjectId == registration.Target.ProjectId
                    && (item.TargetIds.Count == 0 || item.TargetIds.Contains(registration.Target.TargetId)));
                if (blocking is null)
                    return RegisterUnfencedWriterLocked(registration, key);
                fenceEnded = blocking.Ended.Task;
            }

            if (Ambient.Value?.ProjectId == registration.Target.ProjectId)
                throw FenceActiveException();
            try
            {
                await fenceEnded.WaitAsync(fenceWait.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw FenceActiveException();
            }
        }
    }

    private static AuthoringMutationFenceException FenceActiveException() => new(
        "AUTHORING_FENCE_ACTIVE",
        "This authoring target cannot become writable while a dependent operation is consuming its state.");

    private WriterLease RegisterUnfencedWriterLocked(AuthoringWriterRegistration registration, string key)
    {
        if (_writers.TryGetValue(key, out var existing))
        {
            if (existing.Registration.SessionId == registration.SessionId
                && !existing.State.IsReachable
                && existing.FenceCount == 0)
            {
                var replacement = new RegisteredWriter(registration)
                {
                    State = existing.State,
                    FencedWriteGeneration = existing.FencedWriteGeneration,
                };
                _writers[key] = replacement;
                return new WriterLease(this, key, replacement);
            }
            throw new AuthoringMutationFenceException("AUTHORING_WRITER_ACTIVE", "This authoring target is already writable in another window.");
        }

        var writer = new RegisteredWriter(registration);
        _writers.Add(key, writer);
        history.SetActive(registration.Target.TargetId, active: true);
        return new WriterLease(this, key, writer);
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
                var result = await consume(new AuthoringFenceContext(ProcessIncarnationId, states), cancellationToken);
                await RecordFencedWritesAsync(request, writers, states);
                return result;
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
                    var registration = resume[index].Writer.Registration;
                    await CallWriterAsync(
                        async token =>
                        {
                            await registration.ResumeAsync(token);
                            return true;
                        },
                        CancellationToken.None);
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
            return await CallWriterAsync(
                token => writer.Registration.FreezeAndFlushAsync(capturedSequence, token),
                cancellationToken);
        }
        catch (TimeoutException)
        {
            // Later fences fail fast on an unreachable writer instead of each waiting out the timeout.
            lock (_gate)
                writer.State = writer.State with { IsReachable = false };
            throw;
        }
        finally
        {
            writer.FreezeGate.Release();
        }
    }

    private async Task<T> CallWriterAsync<T>(
        Func<CancellationToken, Task<T>> call,
        CancellationToken cancellationToken)
    {
        using var response = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        response.CancelAfter(_writerResponseTimeout);
        try
        {
            // WaitAsync also bounds callbacks that ignore their token.
            return await call(response.Token).WaitAsync(response.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The authoring client did not respond.");
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
            // A writer has not adopted a generation this fence's own consumers wrote while it was frozen.
            if (expectedGeneration is long expected
                && expected != actualGeneration
                && (request.ExpectedGenerations?.ContainsKey(targetId) == true
                    || writer?.FencedWriteGeneration != actualGeneration))
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

    private async Task RecordFencedWritesAsync(
        AuthoringFenceRequest request,
        IReadOnlyList<RegisteredWriter> writers,
        IReadOnlyList<AuthoringFenceTargetState> states)
    {
        if (writers.Count == 0)
            return;
        // The consumer has committed; record its write even if the caller has since cancelled.
        var targetIds = writers.Select(item => item.Registration.Target.TargetId).Distinct(StringComparer.Ordinal).ToList();
        Dictionary<string, long> generations;
        await using (var operation = await database.OpenReadAsync(CancellationToken.None))
        {
            generations = await AuthoringPersistence.ReadGenerationsAsync(
                operation.Db,
                request.ProjectId,
                targetIds,
                CancellationToken.None);
        }
        lock (_gate)
        {
            foreach (var writer in writers)
            {
                var targetId = writer.Registration.Target.TargetId;
                var before = states.FirstOrDefault(item => string.Equals(item.TargetId, targetId, StringComparison.Ordinal));
                var after = generations.GetValueOrDefault(targetId);
                if (before is not null && before.Generation != after)
                    writer.FencedWriteGeneration = after;
            }
        }
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
            fence.Ended.TrySetResult();
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
    private sealed record ActiveFence(Guid ProjectId, IReadOnlySet<string> TargetIds)
    {
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
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
        // The generation this fence's consumers wrote while the writer was frozen; the writer adopts it on reload.
        public long? FencedWriteGeneration { get; set; }
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
