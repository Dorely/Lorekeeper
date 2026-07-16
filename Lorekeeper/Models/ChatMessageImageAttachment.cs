using Lorekeeper.ChatTurns;

namespace Lorekeeper.Models;

public sealed class ChatMessageImageAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    public ChatTurnSurface Surface { get; set; }

    public Guid MessageId { get; set; }

    public Guid ImageId { get; set; }
    public PublishAsset Image { get; set; } = null!;

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
