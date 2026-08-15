using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class PublishConversationRepository(AppDatabaseReadOperation operation) : IPublishConversationRepository
{
    public Task<PublishConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        operation.Db.PublishConversations.FirstOrDefaultAsync(conversation => conversation.ProjectId == projectId, cancellationToken);

    public async Task<List<PublishMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var messages = await operation.Db.PublishMessages.AsNoTracking()
            .Where(message => message.ConversationId == conversationId)
            .OrderBy(message => message.Order)
            .ToListAsync(cancellationToken);
        if (messages.Count == 0)
            return messages;

        var visuals = await operation.Db.PublishMessageVisuals.AsNoTracking()
            .Where(visual => visual.Message.ConversationId == conversationId)
            .OrderBy(visual => visual.Message.Order)
            .ThenBy(visual => visual.SortOrder)
            .Select(visual => new PublishMessageVisual
            {
                Id = visual.Id,
                MessageId = visual.MessageId,
                SortOrder = visual.SortOrder,
                ToolCallId = visual.ToolCallId,
                Title = visual.Title,
                Caption = visual.Caption,
                SourceKind = visual.SourceKind,
                SourceRefId = visual.SourceRefId,
                ContentType = visual.ContentType,
                FileName = visual.FileName,
                Width = visual.Width,
                Height = visual.Height,
                CreatedAt = visual.CreatedAt,
            })
            .ToListAsync(cancellationToken);
        var byMessage = visuals
            .GroupBy(visual => visual.MessageId)
            .ToDictionary(group => group.Key, group => (ICollection<PublishMessageVisual>)group.ToList());
        foreach (var message in messages)
            message.Visuals = byMessage.GetValueOrDefault(message.Id, []);
        return messages;
    }

    public async Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var any = await operation.Db.PublishMessages.AnyAsync(message => message.ConversationId == conversationId, cancellationToken);
        return any
            ? await operation.Db.PublishMessages.Where(message => message.ConversationId == conversationId)
                .MaxAsync(message => message.Order, cancellationToken)
            : -1;
    }

    public async Task AddConversationAsync(PublishConversation conversation, CancellationToken cancellationToken = default) =>
        await operation.Db.PublishConversations.AddAsync(conversation, cancellationToken);

    public async Task AddMessageAsync(PublishMessage message, CancellationToken cancellationToken = default)
    {
        TouchConversation(message.ConversationId);
        await operation.Db.PublishMessages.AddAsync(message, cancellationToken);
    }

    private void TouchConversation(Guid conversationId)
    {
        var conversation = operation.Db.PublishConversations.Local.FirstOrDefault(item => item.Id == conversationId);
        if (conversation is null)
        {
            conversation = new PublishConversation { Id = conversationId };
            operation.Db.Attach(conversation);
            operation.Db.Entry(conversation).Property(item => item.UpdatedAt).IsModified = true;
        }

        conversation.UpdatedAt = DateTime.UtcNow;
    }

    public async Task AddMessageVisualsAsync(IEnumerable<PublishMessageVisual> visuals, CancellationToken cancellationToken = default) =>
        await operation.Db.PublishMessageVisuals.AddRangeAsync(visuals, cancellationToken);

    public void UpdateMessage(PublishMessage message) => operation.Db.MarkModified(message);
    public void RemoveConversation(PublishConversation conversation) => operation.Db.MarkDeleted(conversation);
}
