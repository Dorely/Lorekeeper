using System.Globalization;
using Lorekeeper.Models;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Images;

public sealed class ProjectImageGenerationRuntime(
    IServiceScopeFactory scopeFactory,
    IOptions<ProjectImageGenerationOptions> imageOptions,
    ILogger<ProjectImageGenerationRuntime> logger) : IProjectImageGenerationRuntime
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, ProjectImageGenerationJobRuntimeView> _jobs = [];
    private readonly Dictionary<Guid, TaskCompletionSource<bool>> _jobCompletions = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _jobCancellations = [];
    private readonly Dictionary<Guid, Guid> _cancelledJobs = [];
    private readonly HashSet<Guid> _runningProjects = [];
    private readonly HashSet<Guid> _scheduledProjects = [];

    public event EventHandler? StateChanged;

    public ProjectImageGenerationRuntimeSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new ProjectImageGenerationRuntimeSnapshot(_jobs.Values.ToList());
        }
    }

    public Task EnqueueProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty)
            return Task.CompletedTask;

        var shouldStart = false;
        lock (_lock)
        {
            if (!_runningProjects.Contains(projectId) && _scheduledProjects.Add(projectId))
                shouldStart = true;
        }

        if (shouldStart)
            _ = Task.Run(() => DrainProjectQueueAsync(projectId), CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task<bool> WaitForJobCompletionAsync(
        Guid jobId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<bool>? completion = null;
        lock (_lock)
        {
            if (_jobs.TryGetValue(jobId, out var job) && job.IsRunning)
            {
                if (!_jobCompletions.TryGetValue(jobId, out completion))
                {
                    completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _jobCompletions[jobId] = completion;
                }
            }
        }

        if (completion is null)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var jobs = scope.ServiceProvider.GetRequiredService<IProjectImageJobService>();
            var persisted = await jobs.GetJobAsync(jobId, cancellationToken);
            if (persisted is null)
                return true;

            if (IsFinal(persisted.Status))
                return true;

            lock (_lock)
            {
                if (!_jobCompletions.TryGetValue(jobId, out completion))
                {
                    completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _jobCompletions[jobId] = completion;
                }
            }
        }

        try
        {
            await completion.Task.WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public async Task CancelJobAsync(Guid projectId, Guid jobId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _cancelledJobs[jobId] = projectId;
            if (_jobCancellations.TryGetValue(jobId, out var activeCancellation))
                activeCancellation.Cancel();
        }
        CancelRuntimeJob(projectId, jobId);
        CompleteJobWaiter(jobId);

        await using var scope = scopeFactory.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IProjectImageJobService>();
        await jobs.CancelJobAsync(projectId, jobId, cancellationToken);
        NotifyStateChanged();
    }

    public async Task ReconcileInterruptedJobsAsync(CancellationToken cancellationToken = default)
    {
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IProjectImageJobService>();
            await jobs.MarkInterruptedRunningJobsFailedAsync(cancellationToken);
            foreach (var projectId in await jobs.ListProjectsWithQueuedJobsAsync(cancellationToken))
                await EnqueueProjectAsync(projectId, cancellationToken);
        }

        NotifyStateChanged();
    }

    private async Task DrainProjectQueueAsync(Guid projectId)
    {
        lock (_lock)
        {
            _scheduledProjects.Remove(projectId);
            if (!_runningProjects.Add(projectId))
                return;
        }

        try
        {
            while (true)
            {
                ProjectImageGenerationWorkItem? workItem;
                await using (var scope = scopeFactory.CreateAsyncScope())
                {
                    var jobs = scope.ServiceProvider.GetRequiredService<IProjectImageJobService>();
                    workItem = await jobs.TryStartNextQueuedJobAsync(projectId, CancellationToken.None);
                }

                if (workItem is null)
                    break;

                var jobCancellation = RegisterStartedJob(workItem);
                try
                {
                    await RunJobAsync(workItem, jobCancellation.Token);
                }
                finally
                {
                    MarkJobNotRunning(workItem.JobId);
                    CompleteJobWaiter(workItem.JobId);
                    ReleaseJobCancellation(workItem.JobId, jobCancellation);
                    NotifyStateChanged();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Project image generation queue failed unexpectedly for project {ProjectId}", projectId);
        }
        finally
        {
            lock (_lock)
            {
                _runningProjects.Remove(projectId);
                foreach (var jobId in _cancelledJobs.Where(item => item.Value == projectId).Select(item => item.Key).ToList())
                    _cancelledJobs.Remove(jobId);
            }

            bool shouldRestart;
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var jobs = scope.ServiceProvider.GetRequiredService<IProjectImageJobService>();
                shouldRestart = (await jobs.ListProjectsWithQueuedJobsAsync(CancellationToken.None)).Contains(projectId);
            }

            if (shouldRestart)
                await EnqueueProjectAsync(projectId, CancellationToken.None);
        }
    }

    private async Task RunJobAsync(ProjectImageGenerationWorkItem workItem, CancellationToken cancellationToken)
    {
        var parallelLimit = Math.Clamp(imageOptions.Value.MaxParallelRequests, 1, Math.Max(1, workItem.Count));
        using var throttler = new SemaphoreSlim(parallelLimit, parallelLimit);
        var tasks = Enumerable.Range(0, workItem.Count)
            .Select(outputIndex => RunOutputAsync(workItem, outputIndex, throttler, cancellationToken))
            .ToArray();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Project image generation job failed unexpectedly: projectId={ProjectId}, jobId={JobId}",
                workItem.ProjectId,
                workItem.JobId);
        }
        finally
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var jobs = scope.ServiceProvider.GetRequiredService<IProjectImageJobService>();
            await jobs.CompleteJobAsync(workItem.ProjectId, workItem.JobId, CancellationToken.None);
            if (cancellationToken.IsCancellationRequested)
                await jobs.CancelJobAsync(workItem.ProjectId, workItem.JobId, CancellationToken.None);
        }
    }

    private async Task RunOutputAsync(
        ProjectImageGenerationWorkItem workItem,
        int outputIndex,
        SemaphoreSlim throttler,
        CancellationToken cancellationToken)
    {
        await throttler.WaitAsync(cancellationToken);
        try
        {
            var finalError = await GenerateOutputWithRetriesAsync(workItem, outputIndex, cancellationToken);
            if (finalError is null)
                return;

            await PersistOutputFailureAsync(workItem.ProjectId, workItem.JobId, outputIndex, finalError);
        }
        finally
        {
            throttler.Release();
        }
    }

    private async Task<Exception?> GenerateOutputWithRetriesAsync(
        ProjectImageGenerationWorkItem workItem,
        int outputIndex,
        CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Clamp(imageOptions.Value.MaxRequestAttempts, 1, 10);
        Exception? finalError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await PersistOutputStateAsync(workItem.ProjectId, workItem.JobId, new ProjectImageOutputStateView(
                    outputIndex,
                    ProjectImageOutputStatus.Running,
                    attempt,
                    $"Starting image request attempt {attempt} of {maxAttempts}.",
                    UpdatedAt: DateTime.UtcNow,
                    StartedAt: DateTime.UtcNow));
                UpdateRuntimeOutput(workItem.JobId, outputIndex, ProjectImageOutputStatus.Running, attempt, $"Starting image request attempt {attempt} of {maxAttempts}.");

                var partialQueue = new PartialPersistenceQueue(
                    scopeFactory,
                    workItem.ProjectId,
                    workItem.JobId,
                    outputIndex,
                    attempt,
                    partial =>
                    {
                        if (!UpdateRuntimeOutput(
                            workItem.JobId,
                            outputIndex,
                            ProjectImageOutputStatus.Generating,
                            attempt,
                            $"Received partial image {partial.PartialImageIndex + 1}.",
                            partialImageUrl: partial.PreviewUrl))
                        {
                            NotifyStateChanged();
                        }
                    });
                var progress = new ActionProgress(update =>
                {
                    HandleProviderProgress(workItem.ProjectId, workItem.JobId, outputIndex, attempt, update);
                    if (update.Kind == ProjectImageProviderProgressKind.PartialImage)
                        partialQueue.Enqueue(update);
                });
                ProjectImageProviderResult? result = null;
                Exception? requestError = null;
                await using (var scope = scopeFactory.CreateAsyncScope())
                {
                    var provider = scope.ServiceProvider.GetRequiredService<IProjectImageProvider>();
                    var images = scope.ServiceProvider.GetRequiredService<IProjectImageService>();
                    var jobs = scope.ServiceProvider.GetRequiredService<IProjectImageJobService>();
                    try
                    {
                        result = workItem.Kind == ProjectImageGenerationJobKind.Edit
                            ? await provider.EditAsync(await BuildEditRequestAsync(workItem, images, jobs, cancellationToken), cancellationToken, progress)
                            : await provider.GenerateAsync(await BuildGenerateRequestAsync(workItem, images, cancellationToken), cancellationToken, progress);
                    }
                    catch (Exception ex)
                    {
                        requestError = ex;
                    }

                    try
                    {
                        await partialQueue.CompleteAsync();
                    }
                    catch (Exception ex)
                    {
                        requestError ??= ex;
                    }

                    if (requestError is not null)
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(requestError).Throw();

                    if (result is null || result.Images.Count == 0)
                        throw new ProjectImageProviderException("Image request completed without an image.", "missing_image");

                    cancellationToken.ThrowIfCancellationRequested();
                    await jobs.SaveGeneratedOutputAsync(workItem.ProjectId, workItem.JobId, outputIndex, result, result.Images[0], cancellationToken);
                }

                UpdateRuntimeOutput(workItem.JobId, outputIndex, ProjectImageOutputStatus.Succeeded, attempt, "Image saved.", partialImageUrl: null);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                finalError = ex;
                if (attempt >= maxAttempts || !IsTransientImageGenerationError(ex))
                    break;

                var message = $"Retrying after provider error, attempt {attempt + 1} of {maxAttempts}.";
                await PersistOutputStateAsync(workItem.ProjectId, workItem.JobId, new ProjectImageOutputStateView(
                    outputIndex,
                    ProjectImageOutputStatus.Running,
                    attempt,
                    message,
                    ex.Message,
                    ErrorKind: TryReadErrorKind(ex),
                    UpdatedAt: DateTime.UtcNow));
                UpdateRuntimeOutput(workItem.JobId, outputIndex, ProjectImageOutputStatus.Running, attempt, message, ex.Message, TryReadErrorKind(ex));
                await Task.Delay(ImageGenerationRetryDelay(ex, attempt), cancellationToken);
            }
        }

        return finalError;
    }

    private async Task<ProjectImageProviderGenerateRequest> BuildGenerateRequestAsync(
        ProjectImageGenerationWorkItem workItem,
        IProjectImageService images,
        CancellationToken cancellationToken)
    {
        var references = new List<ProjectImageProviderReference>();
        foreach (var id in workItem.ReferenceImageIds.Take(Math.Max(0, imageOptions.Value.MaxReferenceImages)))
        {
            var data = await images.GetDataAsync(workItem.ProjectId, id, cancellationToken: cancellationToken);
            if (data is not null)
                references.Add(new ProjectImageProviderReference(data.FileName, data.ContentType, data.Data));
        }

        return new ProjectImageProviderGenerateRequest(
            workItem.Prompt,
            workItem.Size,
            Count: 1,
            workItem.MainlineModel,
            workItem.ImageModel,
            references,
            workItem.OutputFormat,
            workItem.Quality,
            workItem.OutputCompression,
            workItem.Background);
    }

    private async Task<ProjectImageProviderEditRequest> BuildEditRequestAsync(
        ProjectImageGenerationWorkItem workItem,
        IProjectImageService images,
        IProjectImageJobService jobs,
        CancellationToken cancellationToken)
    {
        if (workItem.SourceImageId is not { } sourceImageId)
            throw new InvalidOperationException("Image edit job is missing a source image.");

        var source = await images.GetDataAsync(workItem.ProjectId, sourceImageId, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Image edit source could not be found.");

        var sourceReference = new ProjectImageProviderReference(source.FileName, source.ContentType, source.Data);
        ProjectImageProviderReference? mask = null;
        if (workItem.MaskId is { } maskId)
        {
            var maskData = await jobs.GetMaskDataAsync(workItem.ProjectId, maskId, cancellationToken)
                ?? throw new InvalidOperationException("Regional edit guide could not be found.");
            var normalizedSource = ProjectImageBinary.EncodePng(source.Data, source.FileName);
            var normalizedMask = ProjectImageBinary.ValidateBinaryPngMask(maskData.Data, maskData.ContentType, maskData.FileName);
            if (normalizedMask.Width != normalizedSource.Width || normalizedMask.Height != normalizedSource.Height)
            {
                throw new InvalidOperationException(
                    $"Regional edit guide dimensions {normalizedMask.Width}x{normalizedMask.Height} do not match the source image dimensions {normalizedSource.Width}x{normalizedSource.Height}.");
            }

            ProjectImageRegionalGuide.ValidateTargetGeometry(
                workItem.TargetGeometryJson,
                normalizedSource.Width,
                normalizedSource.Height,
                workItem.Size);
            ProjectImageRegionalGuide.ValidateOutputSize(normalizedSource.Width, normalizedSource.Height, workItem.Size);
            sourceReference = new ProjectImageProviderReference(
                normalizedSource.FileName,
                normalizedSource.ContentType,
                normalizedSource.Data);
            mask = new ProjectImageProviderReference(
                normalizedMask.FileName,
                normalizedMask.ContentType,
                normalizedMask.Data);
        }

        var references = new List<ProjectImageProviderReference>();
        foreach (var id in workItem.ReferenceImageIds.Take(Math.Max(0, imageOptions.Value.MaxReferenceImages)))
        {
            var data = await images.GetDataAsync(workItem.ProjectId, id, cancellationToken: cancellationToken);
            if (data is not null)
                references.Add(new ProjectImageProviderReference(data.FileName, data.ContentType, data.Data));
        }

        return new ProjectImageProviderEditRequest(
            workItem.Prompt,
            workItem.Size,
            Count: 1,
            workItem.MainlineModel,
            workItem.ImageModel,
            sourceReference,
            mask,
            references,
            workItem.OutputFormat,
            workItem.Quality,
            workItem.OutputCompression,
            workItem.Background);
    }

    private void HandleProviderProgress(Guid projectId, Guid jobId, int outputIndex, int attempt, ProjectImageProviderProgress update)
    {
        var status = update.Kind switch
        {
            ProjectImageProviderProgressKind.Generating or ProjectImageProviderProgressKind.PartialImage => ProjectImageOutputStatus.Generating,
            ProjectImageProviderProgressKind.Failed or ProjectImageProviderProgressKind.StreamEndedWithoutImage => ProjectImageOutputStatus.Failed,
            _ => ProjectImageOutputStatus.Running,
        };
        var message = string.IsNullOrWhiteSpace(update.Message) ? StatusMessage(status) : update.Message;
        UpdateRuntimeOutput(
            jobId,
            outputIndex,
            status,
            attempt,
            message,
            error: status == ProjectImageOutputStatus.Failed ? update.Message : null,
            errorKind: update.ErrorKind,
            requestId: update.RequestId,
            responseId: update.ResponseId,
            callId: update.CallId,
            lastEventType: update.LastEventType,
            eventCount: update.EventCount,
            partialImageUrl: null);

        if (update.Kind != ProjectImageProviderProgressKind.PartialImage)
        {
            PersistOutputStateFireAndForget(projectId, jobId, new ProjectImageOutputStateView(
                outputIndex,
                status,
                attempt,
                message,
                status == ProjectImageOutputStatus.Failed ? update.Message : "",
                update.ErrorKind,
                update.RequestId,
                update.ResponseId,
                update.CallId,
                update.LastEventType,
                update.EventCount,
                UpdatedAt: DateTime.UtcNow,
                CompletedAt: status == ProjectImageOutputStatus.Failed ? DateTime.UtcNow : null));
        }
    }

    private async Task PersistOutputFailureAsync(Guid projectId, Guid jobId, int outputIndex, Exception exception)
    {
        var outputError = exception is ProjectImageProviderException providerException
            ? new ProjectImageOutputErrorView(
                outputIndex,
                providerException.Message,
                providerException.ErrorKind,
                providerException.RequestId,
                providerException.ResponseId,
                providerException.CallId,
                providerException.StatusCode,
                providerException.LastEventType,
                providerException.EventCount)
            : new ProjectImageOutputErrorView(outputIndex, exception.Message);

        await using var scope = scopeFactory.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IProjectImageJobService>();
        await jobs.MarkOutputFailedAsync(projectId, jobId, outputError, CancellationToken.None);
        UpdateRuntimeOutput(jobId, outputIndex, ProjectImageOutputStatus.Failed, attempt: 0, "Image request failed.", error: outputError.Error, errorKind: outputError.ErrorKind);
    }

    private async Task PersistOutputStateAsync(Guid projectId, Guid jobId, ProjectImageOutputStateView state)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IProjectImageJobService>();
        await jobs.MarkOutputStateAsync(projectId, jobId, state, CancellationToken.None);
    }

    private void PersistOutputStateFireAndForget(Guid projectId, Guid jobId, ProjectImageOutputStateView state)
    {
        _ = PersistOutputStateAsync(projectId, jobId, state).ContinueWith(
            task => logger.LogWarning(
                task.Exception,
                "Project image generation runtime could not persist output state: projectId={ProjectId}, jobId={JobId}, outputIndex={OutputIndex}",
                projectId,
                jobId,
                state.OutputIndex),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private CancellationTokenSource RegisterStartedJob(ProjectImageGenerationWorkItem workItem)
    {
        var outputs = Enumerable.Range(0, workItem.Count)
            .Select(index => new ProjectImageOutputRuntimeView(
                index,
                ProjectImageOutputStatus.Queued,
                Attempt: 0,
                Message: "Waiting for image request.",
                Error: string.Empty,
                ErrorKind: null,
                RequestId: null,
                ResponseId: null,
                CallId: null,
                LastEventType: null,
                EventCount: 0,
                PartialImageUrl: null))
            .ToList();
        var runtimeJob = new ProjectImageGenerationJobRuntimeView(workItem.ProjectId, workItem.JobId, IsRunning: true, outputs);
        var cancellation = new CancellationTokenSource();
        lock (_lock)
        {
            _jobs[workItem.JobId] = runtimeJob;
            _jobCancellations[workItem.JobId] = cancellation;
            if (_cancelledJobs.ContainsKey(workItem.JobId))
                cancellation.Cancel();
            if (!_jobCompletions.ContainsKey(workItem.JobId))
                _jobCompletions[workItem.JobId] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        NotifyStateChanged();
        return cancellation;
    }

    private bool UpdateRuntimeOutput(
        Guid jobId,
        int outputIndex,
        ProjectImageOutputStatus status,
        int attempt,
        string message,
        string? error = null,
        string? errorKind = null,
        string? requestId = null,
        string? responseId = null,
        string? callId = null,
        string? lastEventType = null,
        int eventCount = 0,
        string? partialImageUrl = null)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
                return false;

            var previous = job.Outputs.FirstOrDefault(output => output.OutputIndex == outputIndex);
            if (previous?.Status == ProjectImageOutputStatus.Cancelled && status != ProjectImageOutputStatus.Cancelled)
                return false;
            var outputs = job.Outputs
                .Where(output => output.OutputIndex != outputIndex)
                .Append(new ProjectImageOutputRuntimeView(
                    outputIndex,
                    status,
                    attempt,
                    message,
                    error ?? string.Empty,
                    errorKind,
                    requestId,
                    responseId,
                    callId,
                    lastEventType,
                    eventCount,
                    partialImageUrl ?? previous?.PartialImageUrl))
                .OrderBy(output => output.OutputIndex)
                .ToList();
            _jobs[jobId] = job with { Outputs = outputs };
        }

        NotifyStateChanged();
        return true;
    }

    private void MarkJobNotRunning(Guid jobId)
    {
        lock (_lock)
        {
            if (_jobs.TryGetValue(jobId, out var job))
                _jobs[jobId] = job with { IsRunning = false };
        }
    }

    private void CancelRuntimeJob(Guid projectId, Guid jobId)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(jobId, out var job) || job.ProjectId != projectId)
                return;

            var outputs = job.Outputs
                .Select(output => output.Status is ProjectImageOutputStatus.Succeeded
                    or ProjectImageOutputStatus.Failed
                    or ProjectImageOutputStatus.Cancelled
                        ? output
                        : output with
                        {
                            Status = ProjectImageOutputStatus.Cancelled,
                            Message = "Image request cancelled.",
                            Error = string.Empty,
                            PartialImageUrl = null,
                        })
                .ToList();
            _jobs[jobId] = job with { IsRunning = false, Outputs = outputs };
        }
    }

    private void ReleaseJobCancellation(Guid jobId, CancellationTokenSource cancellation)
    {
        lock (_lock)
        {
            if (_jobCancellations.TryGetValue(jobId, out var active) && ReferenceEquals(active, cancellation))
                _jobCancellations.Remove(jobId);
            _cancelledJobs.Remove(jobId);
        }

        cancellation.Dispose();
    }

    private void CompleteJobWaiter(Guid jobId)
    {
        TaskCompletionSource<bool>? completion;
        lock (_lock)
        {
            if (!_jobCompletions.Remove(jobId, out completion))
                return;
        }

        completion.TrySetResult(true);
    }

    private void NotifyStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private static bool IsFinal(ProjectImageGenerationJobStatus status) =>
        status is ProjectImageGenerationJobStatus.Succeeded
            or ProjectImageGenerationJobStatus.CompletedWithErrors
            or ProjectImageGenerationJobStatus.Failed
            or ProjectImageGenerationJobStatus.Cancelled;

    private static string StatusMessage(ProjectImageOutputStatus status) =>
        status switch
        {
            ProjectImageOutputStatus.Generating => "Generating image.",
            ProjectImageOutputStatus.Running => "Image request running.",
            ProjectImageOutputStatus.Failed => "Image request failed.",
            ProjectImageOutputStatus.Succeeded => "Image saved.",
            _ => "Waiting for earlier image requests.",
        };

    private static string? TryReadErrorKind(Exception exception) =>
        exception is ProjectImageProviderException providerException ? providerException.ErrorKind : null;

    private static bool IsTransientImageGenerationError(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is ProjectImageProviderException provider)
                return provider.ErrorKind is "rate_limit" or "codex_image_input_rate_limit" or "server_error" or "transport_error";
        }

        return false;
    }

    private static TimeSpan ImageGenerationRetryDelay(Exception exception, int failedAttempt)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is ProjectImageProviderException { RetryAfter: { } retryAfter } && retryAfter > TimeSpan.Zero)
                return retryAfter;
            var providerDelay = TryReadProviderRetryDelay(current.Message);
            if (providerDelay is { } delay && delay > TimeSpan.Zero)
            {
                var minimumDelay = IsImagePerMinuteRateLimit(current.Message)
                    ? TimeSpan.FromSeconds(10)
                    : TimeSpan.FromMilliseconds(500);
                return delay < minimumDelay ? minimumDelay : delay;
            }
        }

        if (ContainsImagePerMinuteRateLimit(exception))
            return TimeSpan.FromSeconds(Math.Min(30, 10 * failedAttempt));

        var backoffMilliseconds = Math.Min(5000, 250 * Math.Pow(2, Math.Max(0, failedAttempt - 1)));
        return TimeSpan.FromMilliseconds(backoffMilliseconds);
    }

    private static TimeSpan? TryReadProviderRetryDelay(string message)
    {
        const string marker = "Please try again in ";
        var markerIndex = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return null;

        var valueStart = markerIndex + marker.Length;
        while (valueStart < message.Length && char.IsWhiteSpace(message[valueStart]))
            valueStart++;

        var valueEnd = valueStart;
        while (valueEnd < message.Length && (char.IsDigit(message[valueEnd]) || message[valueEnd] == '.'))
            valueEnd++;

        if (valueEnd == valueStart
            || !double.TryParse(message[valueStart..valueEnd], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var unit = message[valueEnd..].TrimStart();
        if (unit.StartsWith("ms", StringComparison.OrdinalIgnoreCase)
            || unit.StartsWith("millisecond", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromMilliseconds(value);
        }

        if (unit.StartsWith("s", StringComparison.OrdinalIgnoreCase)
            || unit.StartsWith("second", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromSeconds(value);
        }

        return null;
    }

    private static bool ContainsImagePerMinuteRateLimit(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (IsImagePerMinuteRateLimit(current.Message))
                return true;
        }

        return false;
    }

    private static bool IsImagePerMinuteRateLimit(string message) =>
        message.Contains("per min", StringComparison.OrdinalIgnoreCase)
        && (message.Contains("gpt-image", StringComparison.OrdinalIgnoreCase)
            || message.Contains("input-images", StringComparison.OrdinalIgnoreCase)
            || message.Contains("image", StringComparison.OrdinalIgnoreCase));

    private sealed class ActionProgress(Action<ProjectImageProviderProgress> report) : IProgress<ProjectImageProviderProgress>
    {
        public void Report(ProjectImageProviderProgress value) => report(value);
    }

    private sealed class PartialPersistenceQueue(
        IServiceScopeFactory scopeFactory,
        Guid projectId,
        Guid jobId,
        int outputIndex,
        int attempt,
        Action<ProjectImagePartialView> onPersisted)
    {
        private readonly object _lock = new();
        private Task _tail = Task.CompletedTask;
        private int _nextPartialIndex;

        public void Enqueue(ProjectImageProviderProgress progress)
        {
            lock (_lock)
            {
                var partialIndex = progress.PartialImageIndex is >= 0
                    ? progress.PartialImageIndex.Value
                    : _nextPartialIndex;
                _nextPartialIndex = Math.Max(_nextPartialIndex, partialIndex + 1);
                _tail = PersistAfterAsync(_tail, progress with { PartialImageIndex = partialIndex });
            }
        }

        public async Task CompleteAsync()
        {
            Task tail;
            lock (_lock)
                tail = _tail;
            await tail;
        }

        private async Task PersistAfterAsync(Task previous, ProjectImageProviderProgress progress)
        {
            await previous;
            await using var scope = scopeFactory.CreateAsyncScope();
            var jobs = scope.ServiceProvider.GetRequiredService<IProjectImageJobService>();
            var partial = await jobs.SavePartialAsync(
                projectId,
                jobId,
                outputIndex,
                attempt,
                progress,
                CancellationToken.None);
            onPersisted(partial);
        }
    }
}
