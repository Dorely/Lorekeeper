using Lorekeeper.Models;
using Lorekeeper.Llm;

namespace Lorekeeper.ImagesChat;

public interface IImagesChatService
{
    Task<ProjectImageConversation> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectImageMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<ChatProviderAvailability> GetChatProviderAvailabilityAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task SetSelectedProviderAsync(Guid projectId, int? providerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectImageChatAttachmentView>> ListAttachmentsAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ProjectImageChatAttachmentView> AttachImageAsync(Guid projectId, Guid imageId, CancellationToken cancellationToken = default);
    Task RemoveAttachmentAsync(Guid projectId, Guid attachmentId, CancellationToken cancellationToken = default);
    Task ClearAttachmentsAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<string> GetSystemPromptAsync(Guid projectId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ImagesChatTurnUpdate> SendAsync(Guid projectId, string userText, IReadOnlyList<Guid> imageIds, int providerId, CancellationToken cancellationToken = default);
    Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default);
}

public sealed record ProjectImageChatAttachmentView(
    Guid Id,
    Guid ImageId,
    string Label,
    string FileName,
    string AltText,
    string ContentType,
    string PreviewImageUrl,
    string FullImageUrl,
    int SortOrder,
    DateTime CreatedAt,
    DateTime UpdatedAt);
