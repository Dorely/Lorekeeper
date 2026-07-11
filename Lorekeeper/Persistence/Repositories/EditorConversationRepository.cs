using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class EditorConversationRepository(AppDbContext db) : IEditorConversationRepository
{
    public Task<EditorConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        db.EditorConversations.FirstOrDefaultAsync(conversation => conversation.ProjectId == projectId, cancellationToken);

    public Task<List<EditorMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        db.EditorMessages
            .AsNoTracking()
            .Where(message => message.ConversationId == conversationId)
            .OrderBy(message => message.Order)
            .ToListAsync(cancellationToken);

    public async Task<List<EditorMessage>> LoadTranscriptMessagesAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var messages = await LoadMessagesAsync(conversationId, cancellationToken);
        if (messages.Count == 0)
            return messages;

        var visuals = await db.EditorMessageVisuals
            .AsNoTracking()
            .Where(visual => visual.Message.ConversationId == conversationId)
            .OrderBy(visual => visual.Message.Order)
            .ThenBy(visual => visual.SortOrder)
            .Select(visual => new EditorMessageVisual
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

        var visualsByMessage = visuals
            .GroupBy(visual => visual.MessageId)
            .ToDictionary(group => group.Key, group => (ICollection<EditorMessageVisual>)group.ToList());
        foreach (var message in messages)
        {
            message.Visuals = visualsByMessage.GetValueOrDefault(message.Id) ?? [];
        }

        return messages;
    }

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

    public async Task AddMessageVisualsAsync(IEnumerable<EditorMessageVisual> visuals, CancellationToken cancellationToken = default) =>
        await db.EditorMessageVisuals.AddRangeAsync(visuals, cancellationToken);

    public void UpdateMessage(EditorMessage message) => db.EditorMessages.Update(message);

    public void RemoveConversation(EditorConversation conversation) => db.EditorConversations.Remove(conversation);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}
