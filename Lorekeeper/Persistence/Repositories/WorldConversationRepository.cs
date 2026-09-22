using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class WorldConversationRepository(AppDatabaseReadOperation operation) : IWorldConversationRepository
{
    public Task<WorldConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        operation.Db.WorldConversations.FirstOrDefaultAsync(conversation => conversation.ProjectId == projectId, cancellationToken);

    public Task<List<WorldMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        operation.Db.WorldMessages.AsNoTracking().Where(message => message.ConversationId == conversationId)
                           .OrderBy(message => message.Order)
                           .ToListAsync(cancellationToken);

    public Task<bool> ExistsAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        operation.Db.WorldConversations.AnyAsync(conversation => conversation.Id == conversationId, cancellationToken);

    public async Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var any = await operation.Db.WorldMessages.AnyAsync(message => message.ConversationId == conversationId, cancellationToken);
        if (!any) return -1;

        return await operation.Db.WorldMessages.Where(message => message.ConversationId == conversationId)
                                        .MaxAsync(message => message.Order, cancellationToken);
    }

    public async Task AddConversationAsync(WorldConversation conversation, CancellationToken cancellationToken = default) =>
        await operation.Db.WorldConversations.AddAsync(conversation, cancellationToken);

    public async Task ResetMessagesAsync(
        WorldConversation conversation,
        WorldMessage greeting,
        CancellationToken cancellationToken = default)
    {
        var messages = await operation.Db.WorldMessages
            .Where(message => message.ConversationId == conversation.Id)
            .ToListAsync(cancellationToken);
        operation.Db.WorldMessages.RemoveRange(messages);
        conversation.UpdatedAt = DateTime.UtcNow;
        greeting.ConversationId = conversation.Id;
        await operation.Db.WorldMessages.AddAsync(greeting, cancellationToken);
    }

    public void UpdateSelectedProvider(WorldConversation conversation) => operation.Db.MarkModified(conversation);

    public async Task AddMessageAsync(WorldMessage message, CancellationToken cancellationToken = default)
    {
        TouchConversation(message.ConversationId);
        await operation.Db.WorldMessages.AddAsync(message, cancellationToken);
    }

    private void TouchConversation(Guid conversationId)
    {
        var conversation = operation.Db.WorldConversations.Local.FirstOrDefault(item => item.Id == conversationId);
        if (conversation is null)
        {
            conversation = new WorldConversation { Id = conversationId };
            operation.Db.Attach(conversation);
            operation.Db.Entry(conversation).Property(item => item.UpdatedAt).IsModified = true;
        }

        conversation.UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateMessage(WorldMessage message) => operation.Db.MarkModified(message);

}
