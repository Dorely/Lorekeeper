using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public class WritingCoachConversationRepository(AppDbContext db) : IWritingCoachConversationRepository
{
    public Task<WritingCoachConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        db.WritingCoachConversations.FirstOrDefaultAsync(conversation => conversation.ProjectId == projectId, cancellationToken);

    public Task<List<WritingCoachMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        db.WritingCoachMessages.Where(message => message.ConversationId == conversationId)
                               .OrderBy(message => message.Order)
                               .ToListAsync(cancellationToken);

    public async Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var any = await db.WritingCoachMessages.AnyAsync(message => message.ConversationId == conversationId, cancellationToken);
        if (!any) return -1;

        return await db.WritingCoachMessages.Where(message => message.ConversationId == conversationId)
                                            .MaxAsync(message => message.Order, cancellationToken);
    }

    public async Task AddConversationAsync(WritingCoachConversation conversation, CancellationToken cancellationToken = default) =>
        await db.WritingCoachConversations.AddAsync(conversation, cancellationToken);

    public async Task AddMessageAsync(WritingCoachMessage message, CancellationToken cancellationToken = default) =>
        await db.WritingCoachMessages.AddAsync(message, cancellationToken);

    public void UpdateMessage(WritingCoachMessage message) => db.WritingCoachMessages.Update(message);

    public void RemoveConversation(WritingCoachConversation conversation) => db.WritingCoachConversations.Remove(conversation);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}