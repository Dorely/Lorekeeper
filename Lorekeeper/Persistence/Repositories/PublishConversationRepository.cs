using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class PublishConversationRepository(AppDbContext db) : IPublishConversationRepository
{
    public Task<PublishConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        db.PublishConversations.FirstOrDefaultAsync(conversation => conversation.ProjectId == projectId, cancellationToken);

    public async Task<List<PublishMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var messages = await db.PublishMessages.AsNoTracking()
            .Where(message => message.ConversationId == conversationId)
            .OrderBy(message => message.Order)
            .ToListAsync(cancellationToken);
        if (messages.Count == 0)
            return messages;

        var visuals = await db.PublishMessageVisuals.AsNoTracking()
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

    public async Task AddMessageVisualsAsync(IEnumerable<PublishMessageVisual> visuals, CancellationToken cancellationToken = default) =>
        await db.PublishMessageVisuals.AddRangeAsync(visuals, cancellationToken);

    public void UpdateMessage(PublishMessage message) => db.PublishMessages.Update(message);
    public void RemoveConversation(PublishConversation conversation) => db.PublishConversations.Remove(conversation);
    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => db.SaveChangesAsync(cancellationToken);
}
