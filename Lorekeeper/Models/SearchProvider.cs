namespace Lorekeeper.Models;

public class SearchProvider
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public string? DisplayName { get; set; }

    public SearchProviderKind ProviderKind { get; set; }

    public string? ApiKey { get; set; }

    public bool IsActive { get; set; }

    public string ConfigurationJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum SearchProviderKind
{
    SerpApi,
    Brave,
}