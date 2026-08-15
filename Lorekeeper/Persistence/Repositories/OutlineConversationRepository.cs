using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public class OutlineConversationRepository(AppDatabaseReadOperation operation) : IOutlineConversationRepository
{
    public Task<OutlineConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        operation.Db.OutlineConversations.FirstOrDefaultAsync(c => c.ProjectId == projectId, cancellationToken);

    public Task<List<OutlineMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        operation.Db.OutlineMessages.AsNoTracking().Where(m => m.ConversationId == conversationId)
                          .OrderBy(m => m.Order)
                          .ToListAsync(cancellationToken);

    public Task<bool> ExistsAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        operation.Db.OutlineConversations.AnyAsync(conversation => conversation.Id == conversationId, cancellationToken);

    public async Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var any = await operation.Db.OutlineMessages.AnyAsync(m => m.ConversationId == conversationId, cancellationToken);
        if (!any) return -1;
        return await operation.Db.OutlineMessages.Where(m => m.ConversationId == conversationId).MaxAsync(m => m.Order, cancellationToken);
    }

    public async Task AddConversationAsync(OutlineConversation conversation, CancellationToken cancellationToken = default) =>
        await operation.Db.OutlineConversations.AddAsync(conversation, cancellationToken);

    public async Task AddMessageAsync(OutlineMessage message, CancellationToken cancellationToken = default)
    {
        TouchConversation(message.ConversationId);
        await operation.Db.OutlineMessages.AddAsync(message, cancellationToken);
    }

    private void TouchConversation(Guid conversationId)
    {
        var conversation = operation.Db.OutlineConversations.Local.FirstOrDefault(item => item.Id == conversationId);
        if (conversation is null)
        {
            conversation = new OutlineConversation { Id = conversationId };
            operation.Db.Attach(conversation);
            operation.Db.Entry(conversation).Property(item => item.UpdatedAt).IsModified = true;
        }

        conversation.UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateMessage(OutlineMessage message) => operation.Db.MarkModified(message);

    public void RemoveConversation(OutlineConversation conversation) => operation.Db.MarkDeleted(conversation);
}
