namespace Lorekeeper.EditorChat;

public interface IEditorRevisionAgentService
{
    Task<EditorRevisionAgentRunResult> RunAsync(EditorRevisionAgentRunRequest request, CancellationToken cancellationToken = default);
    Task<EditorRevisionJobDetail?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EditorRevisionJobDetail>> ListCurrentJobsAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<EditorRevisionSessionTranscript?> GetSessionTranscriptAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
