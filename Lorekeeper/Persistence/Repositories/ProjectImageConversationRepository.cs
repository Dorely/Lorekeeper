using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class ProjectImageConversationRepository(AppDatabaseReadOperation operation) : IProjectImageConversationRepository
{
    public Task<ProjectImageConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        operation.Db.ProjectImageConversations.FirstOrDefaultAsync(conversation => conversation.ProjectId == projectId, cancellationToken);

    public Task<List<ProjectImageMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        operation.Db.ProjectImageMessages
            .AsNoTracking()
            .Include(message => message.Visuals)
            .Where(message => message.ConversationId == conversationId)
            .OrderBy(message => message.Order)
            .ToListAsync(cancellationToken);

    public async Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        await operation.Db.ProjectImageMessages
            .Where(message => message.ConversationId == conversationId)
            .Select(message => (int?)message.Order)
            .MaxAsync(cancellationToken) ?? -1;

    public async Task AddConversationAsync(ProjectImageConversation conversation, CancellationToken cancellationToken = default) =>
        await operation.Db.ProjectImageConversations.AddAsync(conversation, cancellationToken);

    public void UpdateSelectedProvider(ProjectImageConversation conversation) => operation.Db.MarkModified(conversation);

    public async Task AddMessageAsync(ProjectImageMessage message, CancellationToken cancellationToken = default)
    {
        TouchConversation(message.ConversationId);
        await operation.Db.ProjectImageMessages.AddAsync(message, cancellationToken);
    }

    private void TouchConversation(Guid conversationId)
    {
        var conversation = operation.Db.ProjectImageConversations.Local.FirstOrDefault(item => item.Id == conversationId);
        if (conversation is null)
        {
            conversation = new ProjectImageConversation { Id = conversationId };
            operation.Db.Attach(conversation);
            operation.Db.Entry(conversation).Property(item => item.UpdatedAt).IsModified = true;
        }

        conversation.UpdatedAt = DateTime.UtcNow;
    }

    public async Task AddMessageVisualsAsync(IEnumerable<ProjectImageMessageVisual> visuals, CancellationToken cancellationToken = default) =>
        await operation.Db.ProjectImageMessageVisuals.AddRangeAsync(visuals, cancellationToken);

    public void UpdateMessage(ProjectImageMessage message) => operation.Db.MarkModified(message);

    public void RemoveConversation(ProjectImageConversation conversation) =>
        operation.Db.MarkDeleted(conversation);
}
