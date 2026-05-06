namespace Lorekeeper.Models;

public class IngestJobEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid JobId { get; set; }
    public IngestJob Job { get; set; } = null!;

    public IngestJobEventLevel Level { get; set; } = IngestJobEventLevel.Info;
    public string EventType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum IngestJobEventLevel
{
    Debug,
    Info,
    Warning,
    Error,
}