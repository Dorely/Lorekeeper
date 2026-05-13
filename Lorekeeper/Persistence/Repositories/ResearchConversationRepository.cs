using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class ResearchConversationRepository(AppDbContext db) : IResearchConversationRepository
{
    public Task<ResearchConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        db.ResearchConversations.FirstOrDefaultAsync(conversation => conversation.ProjectId == projectId, cancellationToken);

    public Task<List<ResearchMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        db.ResearchMessages.Where(message => message.ConversationId == conversationId)
                           .OrderBy(message => message.Order)
                           .ToListAsync(cancellationToken);

    public async Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var any = await db.ResearchMessages.AnyAsync(message => message.ConversationId == conversationId, cancellationToken);
        if (!any) return -1;

        return await db.ResearchMessages.Where(message => message.ConversationId == conversationId)
                                        .MaxAsync(message => message.Order, cancellationToken);
    }

    public async Task AddConversationAsync(ResearchConversation conversation, CancellationToken cancellationToken = default) =>
        await db.ResearchConversations.AddAsync(conversation, cancellationToken);

    public async Task AddMessageAsync(ResearchMessage message, CancellationToken cancellationToken = default) =>
        await db.ResearchMessages.AddAsync(message, cancellationToken);

    public void UpdateMessage(ResearchMessage message) => db.ResearchMessages.Update(message);

    public void RemoveConversation(ResearchConversation conversation) => db.ResearchConversations.Remove(conversation);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}