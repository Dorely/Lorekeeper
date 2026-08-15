using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class WebIngestCandidateRepository(AppDatabaseReadOperation operation) : IWebIngestCandidateRepository
{
    public Task<List<WebIngestCandidate>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        operation.Db.WebIngestCandidates
            .AsNoTracking()
            .Where(candidate => candidate.ProjectId == projectId)
            .OrderByDescending(candidate => candidate.UpdatedAt)
            .ThenByDescending(candidate => candidate.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<List<WebIngestCandidate>> ListResearchByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        operation.Db.WebIngestCandidates
            .AsNoTracking()
            .Where(candidate => candidate.ProjectId == projectId && candidate.ResearchConversationId != null)
            .OrderByDescending(candidate => candidate.UpdatedAt)
            .ThenByDescending(candidate => candidate.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<List<WebIngestCandidate>> ListStagedByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        operation.Db.WebIngestCandidates
            .AsNoTracking()
            .Where(candidate => candidate.ProjectId == projectId && candidate.Status == WebIngestCandidateStatus.Staged)
            .OrderByDescending(candidate => candidate.StagedAt)
            .ThenBy(candidate => candidate.Title)
            .ToListAsync(cancellationToken);

    public Task<List<WebIngestCandidate>> ListStagedByProjectAsync(Guid projectId, Guid? researchConversationId, CancellationToken cancellationToken = default) =>
        operation.Db.WebIngestCandidates
            .AsNoTracking()
            .Where(candidate => candidate.ProjectId == projectId
                && candidate.ResearchConversationId == researchConversationId
                && candidate.Status == WebIngestCandidateStatus.Staged)
            .OrderByDescending(candidate => candidate.StagedAt)
            .ThenBy(candidate => candidate.Title)
            .ToListAsync(cancellationToken);

    public Task<List<WebIngestCandidate>> ListByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
        ids.Count == 0
            ? Task.FromResult(new List<WebIngestCandidate>())
            : operation.Db.WebIngestCandidates
                .AsNoTracking()
                .Where(candidate => ids.Contains(candidate.Id))
                .OrderBy(candidate => candidate.CrawlDepth)
                .ThenBy(candidate => candidate.SearchRank)
                .ThenBy(candidate => candidate.Title)
                .ToListAsync(cancellationToken);

    public Task<WebIngestCandidate?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        operation.Db.WebIngestCandidates.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

    public Task<WebIngestCandidate?> FindByUrlAsync(Guid projectId, string url, CancellationToken cancellationToken = default) =>
        operation.Db.WebIngestCandidates.FirstOrDefaultAsync(
            candidate => candidate.ProjectId == projectId
                && (candidate.Url == url || candidate.FinalUrl == url || candidate.CanonicalUrl == url),
            cancellationToken);

    public async Task AddAsync(WebIngestCandidate candidate, CancellationToken cancellationToken = default) =>
        await operation.Db.WebIngestCandidates.AddAsync(candidate, cancellationToken);

    public void Update(WebIngestCandidate candidate) => operation.Db.MarkModified(candidate);

    public void Remove(WebIngestCandidate candidate) => operation.Db.MarkDeleted(candidate);
}
