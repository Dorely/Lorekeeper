using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class ResearchConversationRepository(AppDatabaseReadOperation operation) : IResearchConversationRepository
{
    public Task<ResearchConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        operation.Db.ResearchConversations.FirstOrDefaultAsync(conversation => conversation.ProjectId == projectId, cancellationToken);

    public Task<List<ResearchMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        operation.Db.ResearchMessages.AsNoTracking().Where(message => message.ConversationId == conversationId)
                           .OrderBy(message => message.Order)
                           .ToListAsync(cancellationToken);

    public Task<bool> ExistsAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        operation.Db.ResearchConversations.AnyAsync(conversation => conversation.Id == conversationId, cancellationToken);

    public async Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var any = await operation.Db.ResearchMessages.AnyAsync(message => message.ConversationId == conversationId, cancellationToken);
        if (!any) return -1;

        return await operation.Db.ResearchMessages.Where(message => message.ConversationId == conversationId)
                                        .MaxAsync(message => message.Order, cancellationToken);
    }

    public async Task AddConversationAsync(ResearchConversation conversation, CancellationToken cancellationToken = default) =>
        await operation.Db.ResearchConversations.AddAsync(conversation, cancellationToken);

    public void UpdateSelectedProvider(ResearchConversation conversation) => operation.Db.MarkModified(conversation);

    public async Task AddMessageAsync(ResearchMessage message, CancellationToken cancellationToken = default)
    {
        TouchConversation(message.ConversationId);
        await operation.Db.ResearchMessages.AddAsync(message, cancellationToken);
    }

    private void TouchConversation(Guid conversationId)
    {
        var conversation = operation.Db.ResearchConversations.Local.FirstOrDefault(item => item.Id == conversationId);
        if (conversation is null)
        {
            conversation = new ResearchConversation { Id = conversationId };
            operation.Db.Attach(conversation);
            operation.Db.Entry(conversation).Property(item => item.UpdatedAt).IsModified = true;
        }

        conversation.UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateMessage(ResearchMessage message) => operation.Db.MarkModified(message);

    public void RemoveConversation(ResearchConversation conversation) => operation.Db.MarkDeleted(conversation);
}
