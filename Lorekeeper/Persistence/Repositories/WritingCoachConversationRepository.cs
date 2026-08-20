using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public class WritingCoachConversationRepository(AppDatabaseReadOperation operation) : IWritingCoachConversationRepository
{
    public Task<WritingCoachConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        operation.Db.WritingCoachConversations.FirstOrDefaultAsync(conversation => conversation.ProjectId == projectId, cancellationToken);

    public Task<List<WritingCoachMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        operation.Db.WritingCoachMessages.AsNoTracking().Where(message => message.ConversationId == conversationId)
                               .OrderBy(message => message.Order)
                               .ToListAsync(cancellationToken);

    public async Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var any = await operation.Db.WritingCoachMessages.AnyAsync(message => message.ConversationId == conversationId, cancellationToken);
        if (!any) return -1;

        return await operation.Db.WritingCoachMessages.Where(message => message.ConversationId == conversationId)
                                            .MaxAsync(message => message.Order, cancellationToken);
    }

    public async Task AddConversationAsync(WritingCoachConversation conversation, CancellationToken cancellationToken = default) =>
        await operation.Db.WritingCoachConversations.AddAsync(conversation, cancellationToken);

    public void UpdateSelectedProvider(WritingCoachConversation conversation) => operation.Db.MarkModified(conversation);

    public async Task AddMessageAsync(WritingCoachMessage message, CancellationToken cancellationToken = default)
    {
        TouchConversation(message.ConversationId);
        await operation.Db.WritingCoachMessages.AddAsync(message, cancellationToken);
    }

    private void TouchConversation(Guid conversationId)
    {
        var conversation = operation.Db.WritingCoachConversations.Local.FirstOrDefault(item => item.Id == conversationId);
        if (conversation is null)
        {
            conversation = new WritingCoachConversation { Id = conversationId };
            operation.Db.Attach(conversation);
            operation.Db.Entry(conversation).Property(item => item.UpdatedAt).IsModified = true;
        }

        conversation.UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateMessage(WritingCoachMessage message) => operation.Db.MarkModified(message);

    public void RemoveConversation(WritingCoachConversation conversation) => operation.Db.MarkDeleted(conversation);
}
