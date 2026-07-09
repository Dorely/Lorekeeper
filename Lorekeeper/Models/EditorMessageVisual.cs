namespace Lorekeeper.Models;

public class EditorMessageVisual
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid MessageId { get; set; }
    public EditorMessage Message { get; set; } = null!;

    public int SortOrder { get; set; }

    public string? ToolCallId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Caption { get; set; } = string.Empty;

    public string SourceKind { get; set; } = string.Empty;

    public Guid? SourceRefId { get; set; }

    public string ContentType { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public int? Width { get; set; }

    public int? Height { get; set; }

    public byte[]? Data { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
