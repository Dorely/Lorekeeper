using System.Text.Json;

namespace Lorekeeper.ExternalAccess;

public sealed record ExternalCapability(string Id, string Label);

public static class ExternalCapabilities
{
    public static IReadOnlyList<ExternalCapability> All { get; } =
    [
        new("projects.read", "View projects"), new("projects.create", "Create projects"),
        new("direction.read", "Read guidance and briefs"), new("direction.write", "Edit guidance and briefs"),
        new("outline.read", "Read outline"), new("outline.write", "Edit outline"),
        new("manuscript.read", "Read manuscripts and styles"), new("manuscript.write", "Edit manuscripts and styles"),
        new("review.read", "Inspect review and history"), new("review.write", "Manage review annotations"),
        new("canon.read", "Read canon and relationships"), new("canon.write", "Edit canon and relationships"),
        new("sources.read", "Search and read sources"), new("sources.write", "Retain research sources"),
        new("research.web", "Research the web using configured services"),
        new("images.read", "View and download images"), new("images.write", "Upload and edit image assets and associations"),
        new("images.generate", "Generate and edit images using the configured provider"),
        new("composition.read", "Read layouts and previews"), new("composition.write", "Edit Figures and Designed Pages"),
        new("voice.read", "Read samples and voice profiles"), new("voice.write", "Edit samples and voice profiles"),
        new("publication.read", "Inspect Core Book and releases"), new("publication.write", "Edit publication intent and covers"),
        new("publication.prepare", "Prepare and download publication artifacts"),
        new("transfer.read", "Compare saved creative state for external handoff"),
        new("transfer.write", "Manage external source identities and acknowledge handoffs"),
        new("content.delete", "Remove content through owning services"),
    ];

    public static string[] Preset(string name) => name switch
    {
        "Read only" => All.Where(value => value.Id.EndsWith(".read", StringComparison.Ordinal)).Select(value => value.Id).ToArray(),
        "Authoring" => All.Where(value => value.Id is not ("content.delete" or "images.generate" or "research.web" or "publication.prepare"))
            .Select(value => value.Id).ToArray(),
        "Full control" => All.Select(value => value.Id).ToArray(),
        _ => [],
    };

    public static bool Has(McpClientGrant grant, params string[] capabilities)
    {
        var permissions = JsonSerializer.Deserialize<string[]>(grant.PermissionsJson) ?? [];
        return grant.Enabled && capabilities.All(value => permissions.Contains(value, StringComparer.Ordinal));
    }

    public static bool AllowsProject(McpClientGrant grant, Guid projectId) => grant.Enabled
        && (grant.ProjectIdsJson is null || (JsonSerializer.Deserialize<Guid[]>(grant.ProjectIdsJson) ?? []).Contains(projectId));
}

public sealed class ExternalAccessException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed class ExternalAccessOptions
{
    public int MaximumRequestBytes { get; set; } = 2 * 1024 * 1024;
    public int MaximumResultBytes { get; set; } = 1024 * 1024;
    public int MaximumTransferBytes { get; set; } = 32 * 1024 * 1024;
    public long MaximumDownloadBytes { get; set; } = 512L * 1024 * 1024;
}
