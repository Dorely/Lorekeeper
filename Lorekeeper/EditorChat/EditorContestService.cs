using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Lorekeeper.Chapters;
using Lorekeeper.Context;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence.Repositories;
using Microsoft.Extensions.AI;

namespace Lorekeeper.EditorChat;

public sealed class EditorContestService(
    IProjectRepository projects,
    IChapterService chapters,
    ILlmProviderService providerService,
    IChatClientFactory chatClientFactory,
    IContestRepository contests,
    IAiChangeRepository changes,
    ILogger<EditorContestService> logger) : IEditorContestService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
    private static readonly TimeSpan CandidateRawResponseSaveInterval = TimeSpan.FromMilliseconds(750);
    private const int CandidateRawResponseSaveChars = 512;

    public async Task<EditorContestSettings> GetSettingsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");

        return new EditorContestSettings(
            project.ContestModeEnabled,
            project.ContestProviderSlot1Id,
            project.ContestProviderSlot2Id,
            project.ContestProviderSlot3Id);
    }

    public async Task SetContestModeEnabledAsync(Guid projectId, bool enabled, CancellationToken cancellationToken = default)
    {
        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");
        if (project.ContestModeEnabled == enabled) return;

        project.ContestModeEnabled = enabled;
        project.UpdatedAt = DateTime.UtcNow;
        projects.Update(project);
        await projects.SaveChangesAsync(cancellationToken);
    }

    public async Task SetContestProviderAsync(Guid projectId, int slot, int? providerId, CancellationToken cancellationToken = default)
    {
        if (slot is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(slot), "Contest provider slot must be 1, 2, or 3.");

        if (providerId is { } id && await providerService.GetByIdAsync(id, cancellationToken) is null)
            throw new InvalidOperationException($"Provider {id} not found.");

        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");

        var changed = slot switch
        {
            1 when project.ContestProviderSlot1Id != providerId => SetSlot(project, 1, providerId),
            2 when project.ContestProviderSlot2Id != providerId => SetSlot(project, 2, providerId),
            3 when project.ContestProviderSlot3Id != providerId => SetSlot(project, 3, providerId),
            _ => false,
        };

        if (!changed) return;
        project.UpdatedAt = DateTime.UtcNow;
        projects.Update(project);
        await projects.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ContestBatch>> ListContestBatchesAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await contests.ListActiveByProjectAsync(projectId, cancellationToken);

    public async IAsyncEnumerable<EditorContestRunUpdate> StartContestAsync(
        Guid projectId,
        Guid conversationId,
        Guid? assistantMessageId,
        EditorContestStartRequest request,
        ContestTurnSnapshot snapshot,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");
        var chapter = await chapters.GetAsync(request.ChapterId, cancellationToken)
            ?? throw new InvalidOperationException($"Chapter {request.ChapterId} not found.");
        if (chapter.ProjectId != projectId)
            throw new InvalidOperationException($"Chapter {request.ChapterId} does not belong to project {projectId}.");

        var providers = await ResolveContestProvidersAsync(project, cancellationToken);
        if (providers.Count == 0)
            throw new InvalidOperationException("Choose at least one Contest Mode model before starting a contest.");

        var batch = new ContestBatch
        {
            ProjectId = projectId,
            ConversationId = conversationId,
            AssistantMessageId = assistantMessageId,
            ChapterId = chapter.Id,
            ChapterTitle = chapter.Title,
            OriginalChapterBody = chapter.Body,
            OperationKind = request.OperationKind.Trim(),
            UserGoal = request.UserGoal.Trim(),
            MutationInstructions = request.MutationInstructions.Trim(),
            TargetRangesJson = string.IsNullOrWhiteSpace(request.TargetRangesJson) ? "[]" : request.TargetRangesJson.Trim(),
            ContextSnapshotJson = JsonSerializer.Serialize(snapshot, JsonOptions),
            Status = ContestBatchStatus.Running,
        };
        await contests.AddBatchAsync(batch, cancellationToken);

        var candidateRows = providers.Select((provider, index) => new ContestCandidate
        {
            BatchId = batch.Id,
            Order = index,
            ProviderId = provider.Id,
            ProviderName = provider.Name,
            ModelName = provider.ModelName,
            Status = ContestCandidateStatus.Pending,
        }).ToList();
        foreach (var candidate in candidateRows)
            await contests.AddCandidateAsync(candidate, cancellationToken);
        await contests.SaveChangesAsync(cancellationToken);

        yield return new EditorContestStarted(batch.Id);

        var progressChannel = Channel.CreateUnbounded<ContestCandidateRawProgress>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        var candidatesById = candidateRows.ToDictionary(candidate => candidate.Id);
        var rawResponseBuffers = candidateRows.ToDictionary(candidate => candidate.Id, _ => new StringBuilder());
        var lastRawSaveAt = candidateRows.ToDictionary(candidate => candidate.Id, _ => DateTime.UtcNow);
        var lastRawSaveLength = candidateRows.ToDictionary(candidate => candidate.Id, _ => 0);
        var tasks = new Dictionary<Task<ContestCandidateResult>, ContestCandidate>();
        foreach (var candidate in candidateRows)
        {
            IChatClient? chat = null;
            string? setupError = null;
            try
            {
                chat = await chatClientFactory.CreateChatClientAsync(candidate.ProviderId, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Contest candidate setup failed for provider {ProviderId}", candidate.ProviderId);
                setupError = ex.Message;
            }

            if (setupError is not null || chat is null)
            {
                MarkCandidateFailed(candidate, setupError ?? "Could not create chat client.", invalid: false);
                contests.UpdateCandidate(candidate);
                await contests.SaveChangesAsync(cancellationToken);
                yield return new EditorContestCandidateUpdated(batch.Id, candidate.Id, candidate.Status);
                continue;
            }

            candidate.Status = ContestCandidateStatus.Running;
            candidate.UpdatedAt = DateTime.UtcNow;
            contests.UpdateCandidate(candidate);
            await contests.SaveChangesAsync(cancellationToken);
            yield return new EditorContestCandidateUpdated(batch.Id, candidate.Id, candidate.Status);

            tasks[RunCandidateAsync(chat, batch, candidate, snapshot, request, progressChannel.Writer, cancellationToken)] = candidate;
        }

        Task<ContestCandidateRawProgress>? progressTask = null;
        while (tasks.Count > 0)
        {
            progressTask ??= progressChannel.Reader.ReadAsync().AsTask();
            var completedAny = await Task.WhenAny(tasks.Keys.Cast<Task>().Append(progressTask));
            if (completedAny == progressTask)
            {
                var progress = await progressTask;
                progressTask = null;

                if (!candidatesById.TryGetValue(progress.CandidateId, out var progressCandidate))
                    continue;

                var rawBuffer = rawResponseBuffers[progress.CandidateId];
                rawBuffer.Append(progress.Delta);
                progressCandidate.RawResponse = rawBuffer.ToString();
                progressCandidate.UpdatedAt = DateTime.UtcNow;

                yield return new EditorContestCandidateRawResponseDelta(
                    batch.Id,
                    progress.CandidateId,
                    progress.Delta,
                    progressCandidate.RawResponse);

                var saveDue = progressCandidate.RawResponse.Length - lastRawSaveLength[progress.CandidateId] >= CandidateRawResponseSaveChars
                    || DateTime.UtcNow - lastRawSaveAt[progress.CandidateId] >= CandidateRawResponseSaveInterval;
                if (saveDue)
                {
                    contests.UpdateCandidate(progressCandidate);
                    await contests.SaveChangesAsync(CancellationToken.None);
                    lastRawSaveLength[progress.CandidateId] = progressCandidate.RawResponse.Length;
                    lastRawSaveAt[progress.CandidateId] = DateTime.UtcNow;
                }

                continue;
            }

            var completedTask = (Task<ContestCandidateResult>)completedAny;
            var candidate = tasks[completedTask];
            tasks.Remove(completedTask);

            try
            {
                var result = await completedTask;
                candidate.RawResponse = result.RawResponse;
                rawResponseBuffers[candidate.Id].Clear().Append(result.RawResponse);
                candidate.Summary = result.Response.Summary.Trim();
                candidate.Notes = string.IsNullOrWhiteSpace(result.Response.Notes) ? null : result.Response.Notes.Trim();
                candidate.MutationsJson = JsonSerializer.Serialize(result.Response.Mutations, JsonOptions);
                candidate.ProposedBody = result.ProposedBody;
                candidate.DurationMs = result.Duration.TotalMilliseconds;
                candidate.Status = ContestCandidateStatus.Completed;
                candidate.CompletedAt = DateTime.UtcNow;
                candidate.UpdatedAt = DateTime.UtcNow;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                candidate.Status = ContestCandidateStatus.Failed;
                candidate.ErrorMessage = "Cancelled.";
                candidate.UpdatedAt = DateTime.UtcNow;
            }
            catch (ContestCandidateInvalidException ex)
            {
                logger.LogWarning(ex, "Contest candidate {CandidateId} returned invalid output", candidate.Id);
                candidate.RawResponse = ex.RawResponse;
                rawResponseBuffers[candidate.Id].Clear().Append(ex.RawResponse);
                MarkCandidateFailed(candidate, ex.Message, invalid: true);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Contest candidate {CandidateId} failed", candidate.Id);
                MarkCandidateFailed(candidate, ex.Message, invalid: false);
            }

            contests.UpdateCandidate(candidate);
            await contests.SaveChangesAsync(CancellationToken.None);
            yield return new EditorContestCandidateUpdated(batch.Id, candidate.Id, candidate.Status);
        }

        batch.Status = candidateRows.Any(candidate => candidate.Status == ContestCandidateStatus.Completed)
            ? ContestBatchStatus.Completed
            : ContestBatchStatus.Failed;
        batch.CompletedAt = DateTime.UtcNow;
        batch.UpdatedAt = DateTime.UtcNow;
        if (batch.Status == ContestBatchStatus.Failed)
            batch.ErrorMessage = "No contest candidate completed successfully.";
        contests.UpdateBatch(batch);
        await contests.SaveChangesAsync(CancellationToken.None);
        yield return new EditorContestCompleted(batch.Id, batch.Status);
    }

    public async Task StageCandidateAsync(Guid candidateId, CancellationToken cancellationToken = default)
    {
        var candidate = await contests.GetCandidateAsync(candidateId, cancellationToken)
            ?? throw new InvalidOperationException($"Contest candidate {candidateId} not found.");
        if (candidate.Status != ContestCandidateStatus.Completed)
            throw new InvalidOperationException("Only completed contest candidates can be staged.");

        var batch = candidate.Batch;
        var chapter = await chapters.GetAsync(batch.ChapterId, cancellationToken)
            ?? throw new InvalidOperationException($"Chapter {batch.ChapterId} not found.");
        if (!string.Equals(chapter.Body, batch.OriginalChapterBody, StringComparison.Ordinal))
            throw new InvalidOperationException("The chapter changed after this contest started. Start a new contest before staging a candidate.");

        var changeBatch = new AiChangeBatch
        {
            ProjectId = batch.ProjectId,
            ConversationKind = AiChangeConversationKind.Editor,
            ConversationId = batch.ConversationId,
            AssistantMessageId = batch.AssistantMessageId,
        };
        await changes.AddBatchAsync(changeBatch, cancellationToken);

        var before = new ChapterBodyChange(chapter.Id, chapter.Title, chapter.Body);
        var after = new ChapterBodyChange(chapter.Id, chapter.Title, candidate.ProposedBody);
        await changes.AddChangeAsync(new AiChange
        {
            BatchId = changeBatch.Id,
            Order = 0,
            ToolCallId = $"contest:{candidate.Id:N}",
            ToolName = "edit_chapter",
            ArgumentsJson = JsonSerializer.Serialize(new
            {
                contestBatchId = batch.Id,
                candidateId = candidate.Id,
                mutations = ReadCandidateMutations(candidate.MutationsJson),
            }, JsonOptions),
            Summary = $"Contest winner from {candidate.ProviderName} ({candidate.ModelName}): {candidate.Summary}",
            BeforeJson = JsonSerializer.Serialize(before, JsonSerializerOptions.Default),
            AfterJson = JsonSerializer.Serialize(after, JsonSerializerOptions.Default),
            ResultJson = candidate.RawResponse,
            ResourceKind = "ChapterBody",
            ResourceId = $"Chapter:{chapter.Id:N}",
            ReferencedResourceIdsJson = JsonSerializer.Serialize(new[] { $"Chapter:{chapter.Id:N}" }, JsonSerializerOptions.Default),
        }, cancellationToken);

        foreach (var batchCandidate in batch.Candidates)
        {
            batchCandidate.Status = batchCandidate.Id == candidate.Id
                ? ContestCandidateStatus.Selected
                : batchCandidate.Status == ContestCandidateStatus.Completed
                    ? ContestCandidateStatus.Rejected
                    : batchCandidate.Status;
            batchCandidate.UpdatedAt = DateTime.UtcNow;
            contests.UpdateCandidate(batchCandidate);
        }

        batch.Status = ContestBatchStatus.Staged;
        batch.UpdatedAt = DateTime.UtcNow;
        contests.UpdateBatch(batch);
        await changes.SaveChangesAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<ContestCandidateProvider>> ResolveContestProvidersAsync(Project project, CancellationToken cancellationToken)
    {
        var ids = new[]
            {
                project.ContestProviderSlot1Id,
                project.ContestProviderSlot2Id,
                project.ContestProviderSlot3Id,
            }
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .Take(3)
            .ToList();

        var result = new List<ContestCandidateProvider>();
        foreach (var id in ids)
        {
            var provider = await providerService.GetByIdAsync(id, cancellationToken);
            if (provider is null) continue;
            result.Add(new ContestCandidateProvider(id, provider.DisplayName ?? provider.Name, provider.ModelId));
        }

        return result;
    }

    private async Task<ContestCandidateResult> RunCandidateAsync(
        IChatClient chat,
        ContestBatch batch,
        ContestCandidate candidate,
        ContestTurnSnapshot snapshot,
        EditorContestStartRequest request,
        ChannelWriter<ContestCandidateRawProgress> progressWriter,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var responseText = new StringBuilder();
        var pendingProgress = new StringBuilder();
        var lastProgressFlush = Stopwatch.StartNew();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, BuildContestSystemPrompt()),
            new(ChatRole.User, BuildContestUserPrompt(batch, candidate, snapshot, request)),
        };

        await foreach (var update in chat.GetStreamingResponseAsync(messages, new ChatOptions(), cancellationToken))
        {
            foreach (var content in update.Contents)
            {
                if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                {
                    responseText.Append(textContent.Text);
                    pendingProgress.Append(textContent.Text);
                    if (pendingProgress.Length >= 128 || lastProgressFlush.ElapsedMilliseconds >= 150)
                    {
                        progressWriter.TryWrite(new ContestCandidateRawProgress(candidate.Id, pendingProgress.ToString()));
                        pendingProgress.Clear();
                        lastProgressFlush.Restart();
                    }
                }
            }
        }

        if (pendingProgress.Length > 0)
            progressWriter.TryWrite(new ContestCandidateRawProgress(candidate.Id, pendingProgress.ToString()));

        stopwatch.Stop();
        var raw = responseText.ToString().Trim();
        var response = ParseCandidateResponse(raw);
        var proposedBody = ApplyMutations(batch.OriginalChapterBody, response.Mutations);
        return new ContestCandidateResult(raw, response, proposedBody, stopwatch.Elapsed);
    }

    private static string BuildContestSystemPrompt() =>
        """
        You are a Lorekeeper Contest Mode candidate writer.

        You do not have tools. You cannot mutate project state. Your only job is to propose chapter-body mutations from the supplied contest brief.

        Return only valid JSON with this exact shape:
        {
          "summary": "short summary of the proposed edit",
          "mutations": [
            {
              "mutationKind": "replace_whole_body | replace_range | insert_before_line | insert_after_line",
              "startLine": 1,
              "endLine": 1,
              "replacementText": "prose to place into the chapter",
              "rationale": "brief reason this mutation satisfies the goal"
            }
          ],
          "notes": "optional short note"
        }

        Rules:
        - Output JSON only. Do not wrap it in Markdown.
        - Use replace_whole_body for full-chapter drafts or full-body rewrites.
        - Use replace_range for exact inclusive line ranges.
        - Use insert_before_line or insert_after_line for insertions.
        - Use only chapter-body mutations. Do not propose outline, fact, entity, or relationship changes.
        - Preserve unrelated prose unless the operation explicitly asks for a full rewrite.
        - Respect the context feed and read-only tool results as authoritative story evidence.
        """;

    private static string BuildContestUserPrompt(
        ContestBatch batch,
        ContestCandidate candidate,
        ContestTurnSnapshot snapshot,
        EditorContestStartRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Contest Task");
        sb.AppendLine($"Candidate model: {candidate.ProviderName} / {candidate.ModelName}");
        sb.AppendLine($"Operation kind: {request.OperationKind}");
        sb.AppendLine($"User goal: {request.UserGoal}");
        if (!string.IsNullOrWhiteSpace(request.MutationInstructions))
        {
            sb.AppendLine("Mutation instructions:");
            sb.AppendLine(request.MutationInstructions);
        }
        if (!string.IsNullOrWhiteSpace(request.TargetRangesJson) && request.TargetRangesJson != "[]")
        {
            sb.AppendLine("Target ranges JSON:");
            sb.AppendLine(request.TargetRangesJson);
        }

        sb.AppendLine();
        sb.AppendLine("# Original User Message");
        sb.AppendLine(snapshot.UserMessage);

        if (!string.IsNullOrWhiteSpace(snapshot.AssistantText))
        {
            sb.AppendLine();
            sb.AppendLine("# Main Agent Notes From This Turn");
            sb.AppendLine(snapshot.AssistantText.Trim());
        }

        sb.AppendLine();
        sb.AppendLine("# Context Feed Items");
        foreach (var item in snapshot.ContextItems)
        {
            if (string.Equals(item.Kind, "AssistantWorkflow", StringComparison.OrdinalIgnoreCase)) continue;
            sb.AppendLine($"## {item.Label}");
            sb.AppendLine(item.Body);
            sb.AppendLine();
        }

        if (snapshot.ToolTraces.Count > 0)
        {
            sb.AppendLine("# Read-Only Tool Results From This Turn");
            foreach (var trace in snapshot.ToolTraces)
            {
                sb.AppendLine($"## {trace.ToolName} ({trace.CallId})");
                sb.AppendLine("Arguments:");
                sb.AppendLine(trace.ArgumentsJson);
                sb.AppendLine(trace.Error is null ? "Result:" : "Error:");
                sb.AppendLine(trace.Error ?? trace.Result ?? string.Empty);
                sb.AppendLine();
            }
        }

        sb.AppendLine("# Current Chapter Body With Line Numbers");
        sb.AppendLine($"Chapter: {batch.ChapterTitle}");
        sb.AppendLine(string.IsNullOrWhiteSpace(batch.OriginalChapterBody)
            ? "(empty)"
            : ChapterFormatting.WithLineNumbers(batch.OriginalChapterBody));

        return sb.ToString();
    }

    private static ContestCandidateResponse ParseCandidateResponse(string raw)
    {
        var json = ExtractJson(raw);
        try
        {
            var response = JsonSerializer.Deserialize<ContestCandidateResponse>(json, JsonOptions);
            if (response is null)
                throw new ContestCandidateInvalidException("Candidate returned empty JSON.", raw);
            if (string.IsNullOrWhiteSpace(response.Summary))
                throw new ContestCandidateInvalidException("Candidate JSON is missing summary.", raw);
            if (response.Mutations.Count == 0)
                throw new ContestCandidateInvalidException("Candidate JSON has no mutations.", raw);
            return response;
        }
        catch (JsonException ex)
        {
            throw new ContestCandidateInvalidException($"Candidate did not return valid JSON: {ex.Message}", raw, ex);
        }
    }

    private static string ApplyMutations(string originalBody, IReadOnlyList<ContestChapterMutation> mutations)
    {
        var wholeBody = mutations.Where(IsWholeBodyMutation).ToList();
        if (wholeBody.Count > 0)
        {
            if (mutations.Count != 1)
                throw new ContestCandidateInvalidException("replace_whole_body cannot be combined with other mutations.", string.Empty);
            return wholeBody[0].ReplacementText ?? string.Empty;
        }

        var lines = ChapterFormatting.SplitLines(originalBody).ToList();
        var operations = mutations
            .Select(NormalizeMutation)
            .OrderByDescending(operation => operation.StartLine)
            .ToList();

        var lastStart = int.MaxValue;
        foreach (var operation in operations)
        {
            if (operation.EndLine >= lastStart)
                throw new ContestCandidateInvalidException("Candidate returned overlapping mutations.", string.Empty);
            lastStart = operation.StartLine;

            var replacementLines = ChapterFormatting.SplitLines(operation.ReplacementText ?? string.Empty);
            switch (operation.Kind)
            {
                case "replace_range":
                    if (operation.StartLine < 1 || operation.EndLine > lines.Count || operation.EndLine < operation.StartLine)
                        throw new ContestCandidateInvalidException($"Invalid replace_range lines {operation.StartLine}-{operation.EndLine}.", string.Empty);
                    lines.RemoveRange(operation.StartLine - 1, operation.EndLine - operation.StartLine + 1);
                    lines.InsertRange(operation.StartLine - 1, replacementLines);
                    break;
                case "insert_before_line":
                    if (operation.StartLine < 1 || operation.StartLine > lines.Count + 1)
                        throw new ContestCandidateInvalidException($"Invalid insert_before_line target {operation.StartLine}.", string.Empty);
                    lines.InsertRange(operation.StartLine - 1, replacementLines);
                    break;
                case "insert_after_line":
                    if (operation.StartLine < 0 || operation.StartLine > lines.Count)
                        throw new ContestCandidateInvalidException($"Invalid insert_after_line target {operation.StartLine}.", string.Empty);
                    lines.InsertRange(operation.StartLine, replacementLines);
                    break;
            }
        }

        return ChapterFormatting.JoinLines(lines);
    }

    private static NormalizedMutation NormalizeMutation(ContestChapterMutation mutation)
    {
        var kind = NormalizeKind(mutation.MutationKind);
        var start = mutation.StartLine ?? throw new ContestCandidateInvalidException($"{mutation.MutationKind} requires startLine.", string.Empty);
        var end = mutation.EndLine ?? start;
        return kind switch
        {
            "replace_range" => new NormalizedMutation(kind, start, end, mutation.ReplacementText),
            "insert_before_line" => new NormalizedMutation(kind, start, start, mutation.ReplacementText),
            "insert_after_line" => new NormalizedMutation(kind, start, start, mutation.ReplacementText),
            _ => throw new ContestCandidateInvalidException($"Unsupported mutationKind '{mutation.MutationKind}'.", string.Empty),
        };
    }

    private static bool IsWholeBodyMutation(ContestChapterMutation mutation) =>
        string.Equals(NormalizeKind(mutation.MutationKind), "replace_whole_body", StringComparison.Ordinal);

    private static string NormalizeKind(string? kind) =>
        (kind ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_');

    private static string ExtractJson(string raw)
    {
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;

        var firstNewline = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (firstNewline < 0 || lastFence <= firstNewline) return trimmed;
        return trimmed[(firstNewline + 1)..lastFence].Trim();
    }

    private static IReadOnlyList<ContestChapterMutation> ReadCandidateMutations(string mutationsJson) =>
        JsonSerializer.Deserialize<List<ContestChapterMutation>>(mutationsJson, JsonOptions) ?? [];

    private static bool SetSlot(Project project, int slot, int? providerId)
    {
        switch (slot)
        {
            case 1:
                project.ContestProviderSlot1Id = providerId;
                return true;
            case 2:
                project.ContestProviderSlot2Id = providerId;
                return true;
            case 3:
                project.ContestProviderSlot3Id = providerId;
                return true;
            default:
                return false;
        }
    }

    private static void MarkCandidateFailed(ContestCandidate candidate, string message, bool invalid)
    {
        candidate.Status = invalid ? ContestCandidateStatus.Invalid : ContestCandidateStatus.Failed;
        candidate.ErrorMessage = message;
        candidate.CompletedAt = DateTime.UtcNow;
        candidate.UpdatedAt = DateTime.UtcNow;
    }

    private sealed record ContestCandidateResult(
        string RawResponse,
        ContestCandidateResponse Response,
        string ProposedBody,
        TimeSpan Duration);

    private sealed record ContestCandidateRawProgress(Guid CandidateId, string Delta);

    private sealed record NormalizedMutation(string Kind, int StartLine, int EndLine, string ReplacementText);

    private sealed class ContestCandidateInvalidException : Exception
    {
        public ContestCandidateInvalidException(string message, string rawResponse, Exception? innerException = null)
            : base(message, innerException)
        {
            RawResponse = rawResponse;
        }

        public string RawResponse { get; }
    }
}
