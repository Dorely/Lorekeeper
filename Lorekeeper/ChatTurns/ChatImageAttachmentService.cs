using Lorekeeper.Images;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Lorekeeper.ChatTurns;

public sealed record ChatTurnImageAttachment(
    Guid ImageId,
    string FileName,
    string ContentType,
    string PreviewUrl,
    string FullUrl,
    string AltText);

public interface IChatImageAttachmentService
{
    Task<IReadOnlyList<ChatTurnImageAttachment>> ResolveAsync(
        Guid projectId,
        IReadOnlyList<Guid> imageIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, IReadOnlyList<ChatTurnImageAttachment>>> LoadForSurfaceAsync(
        Guid projectId,
        ChatTurnSurface surface,
        CancellationToken cancellationToken = default);

    Task PersistAsync(
        Guid projectId,
        ChatTurnSurface surface,
        Guid messageId,
        IReadOnlyList<Guid> imageIds,
        CancellationToken cancellationToken = default);

    Task<ChatMessage> BuildUserMessageAsync(
        Guid projectId,
        string userText,
        IReadOnlyList<Guid> imageIds,
        string? guidance = null,
        CancellationToken cancellationToken = default);

    Task ClearSurfaceAsync(
        Guid projectId,
        ChatTurnSurface surface,
        CancellationToken cancellationToken = default);
}

public sealed class ChatImageAttachmentService(
    AppDbContext db,
    IProjectImageService images) : IChatImageAttachmentService
{
    public async Task<IReadOnlyList<ChatTurnImageAttachment>> ResolveAsync(
        Guid projectId,
        IReadOnlyList<Guid> imageIds,
        CancellationToken cancellationToken = default)
    {
        var orderedIds = NormalizeIds(imageIds);
        if (orderedIds.Count == 0)
            return [];

        var resolved = await images.ListByIdsAsync(projectId, orderedIds, cancellationToken);
        var byId = resolved.ToDictionary(image => image.Id);
        if (byId.Count != orderedIds.Count)
            throw new InvalidOperationException("One or more attached images no longer exist in this project.");

        return orderedIds.Select(imageId => ToAttachment(projectId, byId[imageId])).ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<ChatTurnImageAttachment>>> LoadForSurfaceAsync(
        Guid projectId,
        ChatTurnSurface surface,
        CancellationToken cancellationToken = default)
    {
        var rows = await db.ChatMessageImageAttachments
            .AsNoTracking()
            .Include(attachment => attachment.Image)
            .Where(attachment => attachment.ProjectId == projectId && attachment.Surface == surface)
            .OrderBy(attachment => attachment.MessageId)
            .ThenBy(attachment => attachment.SortOrder)
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(attachment => attachment.MessageId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ChatTurnImageAttachment>)group
                    .Select(attachment => ToAttachment(projectId, attachment.Image))
                    .ToList());
    }

    public async Task PersistAsync(
        Guid projectId,
        ChatTurnSurface surface,
        Guid messageId,
        IReadOnlyList<Guid> imageIds,
        CancellationToken cancellationToken = default)
    {
        var orderedIds = NormalizeIds(imageIds);
        if (orderedIds.Count == 0)
            return;

        await ResolveAsync(projectId, orderedIds, cancellationToken);
        var existing = await db.ChatMessageImageAttachments
            .Where(attachment => attachment.ProjectId == projectId
                && attachment.Surface == surface
                && attachment.MessageId == messageId)
            .ToListAsync(cancellationToken);
        if (existing.Count > 0)
            db.ChatMessageImageAttachments.RemoveRange(existing);

        await db.ChatMessageImageAttachments.AddRangeAsync(
            orderedIds.Select((imageId, index) => new ChatMessageImageAttachment
            {
                ProjectId = projectId,
                Surface = surface,
                MessageId = messageId,
                ImageId = imageId,
                SortOrder = index,
            }),
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ChatMessage> BuildUserMessageAsync(
        Guid projectId,
        string userText,
        IReadOnlyList<Guid> imageIds,
        string? guidance = null,
        CancellationToken cancellationToken = default)
    {
        var attachments = await ResolveAsync(projectId, imageIds, cancellationToken);
        var contents = new List<AIContent> { new TextContent(userText) };
        if (!string.IsNullOrWhiteSpace(guidance))
            contents.Add(new TextContent($"\n{guidance.Trim()}"));

        foreach (var attachment in attachments)
        {
            var data = await images.GetDataAsync(projectId, attachment.ImageId, cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException($"Attached image {attachment.FileName} is no longer available.");
            contents.Add(new TextContent($"\nAttached project image {attachment.ImageId:N}: {attachment.FileName}"));
            contents.Add(new DataContent(data.Data, data.ContentType) { Name = data.FileName });
        }

        return new ChatMessage(ChatRole.User, contents);
    }

    public async Task ClearSurfaceAsync(
        Guid projectId,
        ChatTurnSurface surface,
        CancellationToken cancellationToken = default)
    {
        var attachments = await db.ChatMessageImageAttachments
            .Where(attachment => attachment.ProjectId == projectId && attachment.Surface == surface)
            .ToListAsync(cancellationToken);
        if (attachments.Count == 0)
            return;
        db.ChatMessageImageAttachments.RemoveRange(attachments);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static List<Guid> NormalizeIds(IReadOnlyList<Guid> imageIds)
    {
        var result = imageIds.Where(imageId => imageId != Guid.Empty).Distinct().ToList();
        if (result.Count > 4)
            throw new InvalidOperationException("Attach no more than four images to one message.");
        return result;
    }

    private static ChatTurnImageAttachment ToAttachment(Guid projectId, ProjectImageView image) => new(
        image.Id,
        image.FileName,
        image.ContentType,
        image.PreviewUrl,
        $"/projects/{projectId:N}/images/{image.Id:N}/content",
        image.AltText);

    private static ChatTurnImageAttachment ToAttachment(Guid projectId, PublishAsset image) => new(
        image.Id,
        image.FileName,
        image.ContentType,
        $"/projects/{projectId:N}/images/{image.Id:N}/content?maxEdge=640",
        $"/projects/{projectId:N}/images/{image.Id:N}/content",
        image.AltText);
}
