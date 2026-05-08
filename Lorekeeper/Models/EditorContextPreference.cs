namespace Lorekeeper.Models;

/// <summary>
/// Per-chapter user preference for whether a context item is included in the editor
/// Context Feed. Absence means the item's default inclusion rule applies.
/// </summary>
public class EditorContextPreference
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid ChapterId { get; set; }
    public Chapter Chapter { get; set; } = null!;

    public required string Kind { get; set; }

    public required string Key { get; set; }

    public bool IsIncluded { get; set; }

    public int? SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}