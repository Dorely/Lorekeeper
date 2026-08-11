namespace Lorekeeper.Models;

public class PublicationEditionChapterOverride
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EditionId { get; set; }
    public PublicationEdition Edition { get; set; } = null!;
    public Guid ChapterId { get; set; }
    public Chapter Chapter { get; set; } = null!;
    public string ManuscriptJson { get; set; } = string.Empty;
    public long Revision { get; set; }
    public long BaseCoreRevision { get; set; }
    public string BaseCoreHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
