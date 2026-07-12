using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public class OutlineConversationRepository(AppDbContext db) : IOutlineConversationRepository
{
    public Task<OutlineConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        db.OutlineConversations.FirstOrDefaultAsync(c => c.ProjectId == projectId, cancellationToken);

    public Task<List<OutlineMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        db.OutlineMessages.AsNoTracking().Where(m => m.ConversationId == conversationId)
                          .OrderBy(m => m.Order)
                          .ToListAsync(cancellationToken);

    public Task<bool> ExistsAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        db.OutlineConversations.AnyAsync(conversation => conversation.Id == conversationId, cancellationToken);

    public async Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var any = await db.OutlineMessages.AnyAsync(m => m.ConversationId == conversationId, cancellationToken);
        if (!any) return -1;
        return await db.OutlineMessages.Where(m => m.ConversationId == conversationId).MaxAsync(m => m.Order, cancellationToken);
    }

    public async Task AddConversationAsync(OutlineConversation conversation, CancellationToken cancellationToken = default) =>
        await db.OutlineConversations.AddAsync(conversation, cancellationToken);

    public async Task AddMessageAsync(OutlineMessage message, CancellationToken cancellationToken = default) =>
        await db.OutlineMessages.AddAsync(message, cancellationToken);

    public void UpdateMessage(OutlineMessage message) => db.OutlineMessages.Update(message);

    public void RemoveConversation(OutlineConversation conversation) => db.OutlineConversations.Remove(conversation);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}
