using System.ComponentModel.DataAnnotations;

namespace Lorekeeper.Models;

public sealed class ManuscriptStyleDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    [MaxLength(80)]
    public required string Name { get; set; }
    [MaxLength(80)]
    public required string NameKey { get; set; }
    public ManuscriptStyleKind Kind { get; set; }
    [MaxLength(80)]
    public required string SemanticRole { get; set; }
    [MaxLength(80)]
    public required string SemanticRoleKey { get; set; }
    public string DefinitionJson { get; set; } = "{}";
    public long Revision { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum ManuscriptStyleKind
{
    Paragraph,
    Character,
}
