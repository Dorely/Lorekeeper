using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class EditorConversationRepository(AppDbContext db) : IEditorConversationRepository
{
    public Task<EditorConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        db.EditorConversations.FirstOrDefaultAsync(conversation => conversation.ProjectId == projectId, cancellationToken);

    public Task<List<EditorMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        db.EditorMessages.Where(message => message.ConversationId == conversationId)
                         .OrderBy(message => message.Order)
                         .ToListAsync(cancellationToken);

    public Task<bool> ExistsAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        db.EditorConversations.AnyAsync(conversation => conversation.Id == conversationId, cancellationToken);

    public async Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var any = await db.EditorMessages.AnyAsync(message => message.ConversationId == conversationId, cancellationToken);
        if (!any) return -1;
        return await db.EditorMessages.Where(message => message.ConversationId == conversationId).MaxAsync(message => message.Order, cancellationToken);
    }

    public async Task AddConversationAsync(EditorConversation conversation, CancellationToken cancellationToken = default) =>
        await db.EditorConversations.AddAsync(conversation, cancellationToken);

    public async Task AddMessageAsync(EditorMessage message, CancellationToken cancellationToken = default) =>
        await db.EditorMessages.AddAsync(message, cancellationToken);

    public void UpdateMessage(EditorMessage message) => db.EditorMessages.Update(message);

    public void RemoveConversation(EditorConversation conversation) => db.EditorConversations.Remove(conversation);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}