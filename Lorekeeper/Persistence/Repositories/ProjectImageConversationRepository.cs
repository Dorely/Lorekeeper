using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class ProjectImageConversationRepository(AppDbContext db) : IProjectImageConversationRepository
{
    public Task<ProjectImageConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        db.ProjectImageConversations.FirstOrDefaultAsync(conversation => conversation.ProjectId == projectId, cancellationToken);

    public Task<List<ProjectImageMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        db.ProjectImageMessages
            .AsNoTracking()
            .Include(message => message.Visuals)
            .Where(message => message.ConversationId == conversationId)
            .OrderBy(message => message.Order)
            .ToListAsync(cancellationToken);

    public async Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        await db.ProjectImageMessages
            .Where(message => message.ConversationId == conversationId)
            .Select(message => (int?)message.Order)
            .MaxAsync(cancellationToken) ?? -1;

    public async Task AddConversationAsync(ProjectImageConversation conversation, CancellationToken cancellationToken = default) =>
        await db.ProjectImageConversations.AddAsync(conversation, cancellationToken);

    public async Task AddMessageAsync(ProjectImageMessage message, CancellationToken cancellationToken = default) =>
        await db.ProjectImageMessages.AddAsync(message, cancellationToken);

    public async Task AddMessageVisualsAsync(IEnumerable<ProjectImageMessageVisual> visuals, CancellationToken cancellationToken = default) =>
        await db.ProjectImageMessageVisuals.AddRangeAsync(visuals, cancellationToken);

    public void UpdateMessage(ProjectImageMessage message) => db.ProjectImageMessages.Update(message);

    public void RemoveConversation(ProjectImageConversation conversation) =>
        db.ProjectImageConversations.Remove(conversation);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}
