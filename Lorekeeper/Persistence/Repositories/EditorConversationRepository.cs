using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class EditorConversationRepository(AppDatabaseReadOperation operation) : IEditorConversationRepository
{
    public Task<EditorConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        operation.Db.EditorConversations.FirstOrDefaultAsync(conversation => conversation.ProjectId == projectId, cancellationToken);

    public Task<List<EditorMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        operation.Db.EditorMessages
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

        var visuals = await operation.Db.EditorMessageVisuals
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
        operation.Db.EditorConversations.AnyAsync(conversation => conversation.Id == conversationId, cancellationToken);

    public async Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var any = await operation.Db.EditorMessages.AnyAsync(message => message.ConversationId == conversationId, cancellationToken);
        if (!any) return -1;
        return await operation.Db.EditorMessages.Where(message => message.ConversationId == conversationId).MaxAsync(message => message.Order, cancellationToken);
    }

    public async Task AddConversationAsync(EditorConversation conversation, CancellationToken cancellationToken = default) =>
        await operation.Db.EditorConversations.AddAsync(conversation, cancellationToken);

    public async Task AddMessageAsync(EditorMessage message, CancellationToken cancellationToken = default)
    {
        TouchConversation(message.ConversationId);
        await operation.Db.EditorMessages.AddAsync(message, cancellationToken);
    }

    private void TouchConversation(Guid conversationId)
    {
        var conversation = operation.Db.EditorConversations.Local.FirstOrDefault(item => item.Id == conversationId);
        if (conversation is null)
        {
            conversation = new EditorConversation { Id = conversationId };
            operation.Db.Attach(conversation);
            operation.Db.Entry(conversation).Property(item => item.UpdatedAt).IsModified = true;
        }

        conversation.UpdatedAt = DateTime.UtcNow;
    }

    public async Task AddMessageVisualsAsync(IEnumerable<EditorMessageVisual> visuals, CancellationToken cancellationToken = default) =>
        await operation.Db.EditorMessageVisuals.AddRangeAsync(visuals, cancellationToken);

    public void UpdateMessage(EditorMessage message) => operation.Db.MarkModified(message);

    public void RemoveConversation(EditorConversation conversation) => operation.Db.MarkDeleted(conversation);
}
