using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class PublishConversationRepository(AppDbContext db) : IPublishConversationRepository
{
    public Task<PublishConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        db.PublishConversations.FirstOrDefaultAsync(conversation => conversation.ProjectId == projectId, cancellationToken);

    public Task<List<PublishMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        db.PublishMessages.AsNoTracking()
            .Where(message => message.ConversationId == conversationId)
            .OrderBy(message => message.Order)
            .ToListAsync(cancellationToken);

    public async Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var any = await db.PublishMessages.AnyAsync(message => message.ConversationId == conversationId, cancellationToken);
        return any
            ? await db.PublishMessages.Where(message => message.ConversationId == conversationId)
                .MaxAsync(message => message.Order, cancellationToken)
            : -1;
    }

    public async Task AddConversationAsync(PublishConversation conversation, CancellationToken cancellationToken = default) =>
        await db.PublishConversations.AddAsync(conversation, cancellationToken);

    public async Task AddMessageAsync(PublishMessage message, CancellationToken cancellationToken = default) =>
        await db.PublishMessages.AddAsync(message, cancellationToken);

    public void UpdateMessage(PublishMessage message) => db.PublishMessages.Update(message);
    public void RemoveConversation(PublishConversation conversation) => db.PublishConversations.Remove(conversation);
    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => db.SaveChangesAsync(cancellationToken);
}
