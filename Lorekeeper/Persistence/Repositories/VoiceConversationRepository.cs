using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public class VoiceConversationRepository(AppDatabaseReadOperation operation) : IVoiceConversationRepository
{
    public Task<VoiceConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        operation.Db.VoiceConversations.FirstOrDefaultAsync(conversation => conversation.ProjectId == projectId, cancellationToken);

    public Task<List<VoiceMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        operation.Db.VoiceMessages.AsNoTracking().Where(message => message.ConversationId == conversationId)
                               .OrderBy(message => message.Order)
                               .ToListAsync(cancellationToken);

    public async Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var any = await operation.Db.VoiceMessages.AnyAsync(message => message.ConversationId == conversationId, cancellationToken);
        if (!any) return -1;

        return await operation.Db.VoiceMessages.Where(message => message.ConversationId == conversationId)
                                            .MaxAsync(message => message.Order, cancellationToken);
    }

    public async Task AddConversationAsync(VoiceConversation conversation, CancellationToken cancellationToken = default) =>
        await operation.Db.VoiceConversations.AddAsync(conversation, cancellationToken);

    public async Task ResetMessagesAsync(
        VoiceConversation conversation,
        VoiceMessage greeting,
        CancellationToken cancellationToken = default)
    {
        var messages = await operation.Db.VoiceMessages
            .Where(message => message.ConversationId == conversation.Id)
            .ToListAsync(cancellationToken);
        operation.Db.VoiceMessages.RemoveRange(messages);
        conversation.UpdatedAt = DateTime.UtcNow;
        greeting.ConversationId = conversation.Id;
        await operation.Db.VoiceMessages.AddAsync(greeting, cancellationToken);
    }

    public void UpdateSelectedProvider(VoiceConversation conversation) => operation.Db.MarkModified(conversation);

    public async Task AddMessageAsync(VoiceMessage message, CancellationToken cancellationToken = default)
    {
        TouchConversation(message.ConversationId);
        await operation.Db.VoiceMessages.AddAsync(message, cancellationToken);
    }

    private void TouchConversation(Guid conversationId)
    {
        var conversation = operation.Db.VoiceConversations.Local.FirstOrDefault(item => item.Id == conversationId);
        if (conversation is null)
        {
            conversation = new VoiceConversation { Id = conversationId };
            operation.Db.Attach(conversation);
            operation.Db.Entry(conversation).Property(item => item.UpdatedAt).IsModified = true;
        }

        conversation.UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateMessage(VoiceMessage message) => operation.Db.MarkModified(message);

}
