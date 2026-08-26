using System.Text.Json;
using Lorekeeper.Chapters;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lorekeeper.EditorChat;

public sealed class EditorRevisionAgentService(
IAppDatabaseOperationFactory database, IChapterService chapters, IManuscriptService manuscripts, IOptions<EditorChatOptions> options, IServiceScopeFactory scopeFactory, IEditorRevisionJobNotifier notifier, ILogger<EditorRevisionAgentService> logger) : IEditorRevisionAgentService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<EditorRevisionAgentRunResult> RunAsync(
        EditorRevisionAgentRunRequest request,
        CancellationToken cancellationToken = default)
    {
        var assignments = NormalizeAssignments(request.Chapters);
        if (assignments.Count == 0)
            throw new InvalidOperationException("At least one chapter assignment is required.");

        Guid jobId;
        List<Guid> sessionIds;
        await using (var databaseOperation = await database.OpenWriteAsync(cancellationToken))
        {
            databaseOperation.ShareWithNestedOperations();
            var projects = databaseOperation.Repositories.Projects;
            var revisions = databaseOperation.Repositories.EditorRevisions;
            _ = await projects.GetByIdAsync(request.ProjectId, cancellationToken)
                ?? throw new InvalidOperationException($"Project {request.ProjectId} not found.");

            var seenChapterIds = new HashSet<Guid>();
            var chaptersById = new Dictionary<Guid, Chapter>();
            foreach (var assignment in assignments)
            {
                if (!seenChapterIds.Add(assignment.ChapterId))
                    throw new InvalidOperationException($"Chapter {assignment.ChapterId} appears more than once in the revision assignment list.");

                var chapter = await chapters.GetAsync(assignment.ChapterId, cancellationToken)
                    ?? throw new InvalidOperationException($"Chapter {assignment.ChapterId} not found.");
                if (chapter.ProjectId != request.ProjectId)
                    throw new InvalidOperationException($"Chapter {assignment.ChapterId} does not belong to project {request.ProjectId}.");
                chaptersById[assignment.ChapterId] = chapter;
            }

            var job = new EditorRevisionJob
            {
                ProjectId = request.ProjectId,
                ConversationId = request.ConversationId,
                AssistantMessageId = request.AssistantMessageId,
                ToolCallId = request.ToolCallId,
                ArgumentsJson = request.ArgumentsJson,
                ContentTargetKind = request.ContentTarget.Kind.ToString(),
                ContentTargetEditionId = request.ContentTarget.EditionId,
                Status = EditorRevisionJobStatus.Running,
            };
            await revisions.AddJobAsync(job, cancellationToken);
            jobId = job.Id;

            sessionIds = new List<Guid>(assignments.Count);
            for (var i = 0; i < assignments.Count; i++)
            {
                var assignment = assignments[i];
                var chapter = chaptersById[assignment.ChapterId];
                var source = await manuscripts.GetManuscriptAsync(request.ContentTarget, chapter.Id, cancellationToken)
                    ?? throw new InvalidOperationException($"Chapter {chapter.Id} manuscript was not found.");
                var session = new EditorRevisionSession
                {
                    JobId = job.Id,
                    Order = i,
                    ChapterId = chapter.Id,
                    ChapterTitle = chapter.Title,
                    Reason = assignment.Reason,
                    Instructions = assignment.Instructions,
                    OriginalManuscriptJson = ManuscriptCodec.Serialize(source.Document),
                    ProviderId = request.ProviderId,
                    ModelName = request.ModelId,
                    Status = EditorRevisionSessionStatus.Queued,
                };
                await revisions.AddSessionAsync(session, cancellationToken);
                sessionIds.Add(session.Id);
            }

            await databaseOperation.SaveChangesAsync(cancellationToken);
            notifier.Notify(new EditorRevisionJobUpdate(
                request.ProjectId,
                request.ConversationId,
                request.ToolCallId,
                job.Id,
                null,
                EditorRevisionJobUpdateKind.Created,
                DateTime.UtcNow));
        }

        var maxConcurrency = Math.Clamp(options.Value.RevisionAgents.MaxConcurrency, 1, 8);
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var cancelled = false;
        var tasks = sessionIds.Select(sessionId => RunSessionWithSemaphoreAsync(sessionId, semaphore, cancellationToken)).ToList();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }

        var result = await FinalizeJobAsync(jobId, cancelled, CancellationToken.None);
        return result;
    }

    public async Task<EditorRevisionJobDetail?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var revisions = databaseOperation.Repositories.EditorRevisions;
        var job = await revisions.GetJobAsync(jobId, cancellationToken);
        return job is null ? null : ToDetail(job);
    }

    public async Task<IReadOnlyList<EditorRevisionJobDetail>> ListCurrentJobsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var revisions = databaseOperation.Repositories.EditorRevisions;
        return (await revisions.ListCurrentByProjectAsync(projectId, cancellationToken))
                    .Select(ToDetail)
                    .ToList();
    }
    public async Task<EditorRevisionSessionTranscript?> GetSessionTranscriptAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var revisions = databaseOperation.Repositories.EditorRevisions;
        var session = await revisions.GetSessionAsync(sessionId, cancellationToken);
        if (session is null) return null;

        return new EditorRevisionSessionTranscript(
            ToSessionDetail(session),
            session.Messages
                .OrderBy(message => message.Order)
                .Select(message => new EditorRevisionTranscriptMessage(
                    message.Id,
                    message.Role.ToString(),
                    message.Content,
                    message.ToolCallsJson,
                    message.ToolCallId,
                    message.ToolName,
                    message.Status.ToString(),
                    message.ErrorMessage,
                    message.CreatedAt))
                .ToList());
    }

    private async Task RunSessionWithSemaphoreAsync(Guid sessionId, SemaphoreSlim semaphore, CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<EditorRevisionAgentProcessor>();
            await processor.RunSessionAsync(sessionId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Revision worker session {SessionId} failed outside processor handling.", sessionId);
            await MarkSessionFailedAsync(sessionId, ex.Message, CancellationToken.None);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task MarkSessionFailedAsync(Guid sessionId, string error, CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var repo = operation.Repositories.EditorRevisions;
        var session = await repo.GetSessionAsync(sessionId, cancellationToken);
        if (session is null) return;

        session.Status = EditorRevisionSessionStatus.Failed;
        session.ErrorMessage = error;
        session.CompletedAt = DateTime.UtcNow;
        session.UpdatedAt = DateTime.UtcNow;
        repo.UpdateSession(session);
        await operation.SaveChangesAsync(cancellationToken);
        notifier.Notify(new EditorRevisionJobUpdate(
            session.Job.ProjectId,
            session.Job.ConversationId,
            session.Job.ToolCallId,
            session.Job.Id,
            session.Id,
            EditorRevisionJobUpdateKind.SessionCompleted,
            DateTime.UtcNow));
    }

    private async Task<EditorRevisionAgentRunResult> FinalizeJobAsync(Guid jobId, bool cancelled, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        var changes = databaseOperation.Repositories.AiChanges;
        var job = await databaseOperation.Db.EditorRevisionJobs
            .AsTracking()
            .Include(item => item.Sessions.OrderBy(session => session.Order))
            .FirstOrDefaultAsync(item => item.Id == jobId, cancellationToken)
            ?? throw new InvalidOperationException($"Revision job {jobId} not found.");

        if (cancelled)
        {
            foreach (var session in job.Sessions.Where(session =>
                session.Status is EditorRevisionSessionStatus.Queued or EditorRevisionSessionStatus.Running))
            {
                session.Status = EditorRevisionSessionStatus.Cancelled;
                session.ErrorMessage = "Cancelled.";
                session.CompletedAt = DateTime.UtcNow;
                session.UpdatedAt = DateTime.UtcNow;
            }
        }

        var sessions = job.Sessions.OrderBy(session => session.Order).ToList();
        job.Status = cancelled
            ? EditorRevisionJobStatus.Cancelled
            : sessions.All(session => session.Status == EditorRevisionSessionStatus.Completed)
                ? EditorRevisionJobStatus.Completed
                : sessions.Any(session => session.Status == EditorRevisionSessionStatus.Completed)
                    ? EditorRevisionJobStatus.Completed
                    : EditorRevisionJobStatus.Failed;
        job.ErrorMessage = BuildJobErrorMessage(sessions);
        job.CompletedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;
        await databaseOperation.SaveChangesAsync(cancellationToken);

        var updateKind = job.Status switch
        {
            EditorRevisionJobStatus.Completed => EditorRevisionJobUpdateKind.Completed,
            EditorRevisionJobStatus.Cancelled => EditorRevisionJobUpdateKind.Cancelled,
            _ => EditorRevisionJobUpdateKind.Failed,
        };
        notifier.Notify(new EditorRevisionJobUpdate(
            job.ProjectId,
            job.ConversationId,
            job.ToolCallId,
            job.Id,
            null,
            updateKind,
            DateTime.UtcNow));

        var pendingChangeIds = (await changes.ListPendingRevisionWorkerChangesAsync(
                job.ProjectId,
                job.ConversationId,
                job.AssistantMessageId,
                job.ToolCallId,
                cancellationToken))
            .Select(change => change.Id)
            .ToList();
        return ToRunResult(job, pendingChangeIds);
    }

    private static string? BuildJobErrorMessage(IReadOnlyList<EditorRevisionSession> sessions)
    {
        var incomplete = sessions
            .Where(session => session.Status != EditorRevisionSessionStatus.Completed)
            .ToList();
        if (incomplete.Count == 0)
            return null;

        var completedCount = sessions.Count - incomplete.Count;
        var examples = incomplete
            .Take(3)
            .Select(session => $"{session.ChapterTitle} ({session.Status}): {session.ErrorMessage ?? "No detail was recorded."}");
        var remaining = incomplete.Count > 3 ? $"; {incomplete.Count - 3} additional session(s) incomplete" : string.Empty;
        var outcome = completedCount == 0
            ? "No revision worker completed a valid chapter-body edit."
            : $"Revision workers completed {completedCount} of {sessions.Count} chapter edit(s).";
        return $"{outcome} Incomplete sessions: {string.Join("; ", examples)}{remaining}";
    }

    private static List<EditorRevisionAgentAssignmentInput> NormalizeAssignments(IReadOnlyList<EditorRevisionAgentAssignmentInput>? input)
    {
        if (input is null)
            throw new InvalidOperationException("chapters is required.");

        var normalized = new List<EditorRevisionAgentAssignmentInput>(input.Count);
        for (var i = 0; i < input.Count; i++)
        {
            var item = input[i] ?? throw new InvalidOperationException($"chapters[{i}] is required.");
            if (item.ChapterId == Guid.Empty)
                throw new InvalidOperationException($"chapters[{i}].chapterId is required.");
            if (string.IsNullOrWhiteSpace(item.Reason))
                throw new InvalidOperationException($"chapters[{i}].reason is required.");
            if (string.IsNullOrWhiteSpace(item.Instructions))
                throw new InvalidOperationException($"chapters[{i}].instructions is required.");

            normalized.Add(new EditorRevisionAgentAssignmentInput
            {
                ChapterId = item.ChapterId,
                Reason = item.Reason.Trim(),
                Instructions = item.Instructions.Trim(),
            });
        }

        return normalized;
    }

    private static EditorRevisionAgentRunResult ToRunResult(
        EditorRevisionJob job,
        IReadOnlyList<Guid> pendingChangeIds) => new(
        job.Id,
        job.Status,
        job.Sessions
            .OrderBy(session => session.Order)
            .Select(ToSessionResult)
            .ToList(),
        job.ErrorMessage,
        pendingChangeIds);

    private static EditorRevisionSessionResult ToSessionResult(EditorRevisionSession session) => new(
        session.Id,
        session.Order,
        session.ChapterId,
        session.ChapterTitle,
        session.Status,
        session.Summary,
        session.ErrorMessage);

    private static EditorRevisionJobDetail ToDetail(EditorRevisionJob job) => new(
        job.Id,
        job.ProjectId,
        job.ConversationId,
        job.Status,
        job.ErrorMessage,
        job.CreatedAt,
        job.UpdatedAt,
        job.CompletedAt,
        job.Sessions.OrderBy(session => session.Order).Select(ToSessionDetail).ToList());

    private static EditorRevisionSessionDetail ToSessionDetail(EditorRevisionSession session) => new(
        session.Id,
        session.Order,
        session.ChapterId,
        session.ChapterTitle,
        session.Reason,
        session.Instructions,
        session.Status,
        session.Summary,
        session.Rationale,
        session.OperationFormat,
        session.OperationsJson,
        session.Notes,
        session.ErrorMessage,
        session.DurationMs,
        session.CreatedAt,
        session.UpdatedAt,
        session.CompletedAt);

    public static string SerializeRunResult(EditorRevisionAgentRunResult result) =>
        JsonSerializer.Serialize(result, JsonOptions);

    public static EditorRevisionJobProgress ToProgress(EditorRevisionJobDetail job)
    {
        var sessions = job.Sessions
            .OrderBy(session => session.Order)
            .Select(session => new EditorRevisionSessionProgress(
                session.Id,
                session.Order,
                session.ChapterId,
                session.ChapterTitle,
                session.Status,
                session.Summary,
                session.ErrorMessage,
                session.UpdatedAt))
            .ToList();

        return new EditorRevisionJobProgress(
            job.Id,
            job.Status,
            sessions.Count,
            sessions.Count(session => session.Status == EditorRevisionSessionStatus.Queued),
            sessions.Count(session => session.Status == EditorRevisionSessionStatus.Running),
            sessions.Count(session => session.Status == EditorRevisionSessionStatus.Completed),
            sessions.Count(session => session.Status == EditorRevisionSessionStatus.Failed),
            sessions.Count(session => session.Status == EditorRevisionSessionStatus.Invalid),
            sessions.Count(session => session.Status == EditorRevisionSessionStatus.Cancelled),
            job.UpdatedAt,
            sessions);
    }
}
