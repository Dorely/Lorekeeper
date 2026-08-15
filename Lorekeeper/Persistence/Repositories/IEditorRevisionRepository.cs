using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IEditorRevisionRepository
{
    Task<EditorRevisionJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<EditorRevisionSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<List<EditorRevisionJob>> ListCurrentByProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<List<EditorRevisionMessage>> LoadSessionMessagesAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<int> GetMaxMessageOrderAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task AddJobAsync(EditorRevisionJob job, CancellationToken cancellationToken = default);
    Task AddSessionAsync(EditorRevisionSession session, CancellationToken cancellationToken = default);
    Task AddMessageAsync(EditorRevisionMessage message, CancellationToken cancellationToken = default);

    void UpdateJob(EditorRevisionJob job);
    void UpdateSession(EditorRevisionSession session);
    void UpdateMessage(EditorRevisionMessage message);
}
