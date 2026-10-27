using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Lorekeeper.ExternalAccess;

internal sealed class ExternalResources(ExternalAccessDispatcher dispatcher, McpClientGrantService grants, IHttpContextAccessor http)
{
    private static readonly (string Name, string Uri, string Tool)[] Templates =
    [
        ("Project", "lorekeeper://projects/{projectId}{?workspaceId,pageNumber}", "get_project"),
        ("Outline", "lorekeeper://projects/{projectId}/chapters{?workspaceId,pageNumber}", "list_outline"),
        ("Manuscript", "lorekeeper://projects/{projectId}/chapters/{chapterId}/manuscript{?workspaceId,startBlock,blockCount}", "read_manuscript"),
        ("Entity", "lorekeeper://projects/{projectId}/entities/{entityId}{?workspaceId,pageNumber}", "read_entity"),
        ("World Brief", "lorekeeper://projects/{projectId}/world-brief{?workspaceId,pageNumber}", "read_world_brief"),
        ("Workspace Context", "lorekeeper://projects/{projectId}/workspaces/{workspaceId}/context{?pageNumber}", "get_workspace_context"),
    ];

    public async Task<ListResourcesResult> ListAsync(CancellationToken ct)
    {
        var grant = await grants.RequireAsync(ExternalAccessHttpGate.ClientId(http.HttpContext!), ct);
        return new ListResourcesResult { Resources = ExternalCapabilities.Has(grant, "projects.read")
            ? [new Resource { Uri = "lorekeeper://projects", Name = "Accessible projects", MimeType = "application/json" }] : [] };
    }

    public async Task<ListResourceTemplatesResult> TemplatesAsync(CancellationToken ct)
    {
        var grant = await grants.RequireAsync(ExternalAccessHttpGate.ClientId(http.HttpContext!), ct);
        return new ListResourceTemplatesResult
        {
            ResourceTemplates = Templates.Where(value => ExternalCapabilities.Has(grant,
                ExternalToolCatalog.Tools.Single(tool => tool.Tool.Name == value.Tool).Capabilities))
                .Select(value => new ResourceTemplate { Name = value.Name, UriTemplate = value.Uri, MimeType = "application/json" }).ToList(),
        };
    }

    public async Task<ReadResourceResult> ReadAsync(string resource, CancellationToken ct)
    {
        if (!Uri.TryCreate(resource, UriKind.Absolute, out var uri) || uri.Scheme != "lorekeeper" || uri.Host != "projects")
            throw new McpException("Unknown Lorekeeper resource.");
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string tool;
        var arguments = new Dictionary<string, object>();
        foreach (var pair in QueryHelpers.ParseQuery(uri.Query))
        {
            if (pair.Key is "pageNumber" or "startBlock" or "blockCount" && int.TryParse(pair.Value, out var number)) arguments[pair.Key] = number;
            else if (pair.Key == "workspaceId" && Guid.TryParse(pair.Value, out var workspace)) arguments[pair.Key] = workspace;
        }
        if (segments.Length == 0) tool = "list_projects";
        else
        {
            if (!Guid.TryParse(segments[0], out var projectId)) throw new McpException("An exact project ID is required.");
            arguments["projectId"] = projectId;
            tool = segments.Length switch
            {
                1 => "get_project",
                2 when segments[1] == "chapters" => "list_outline",
                2 when segments[1] == "world-brief" => "read_world_brief",
                3 when segments[1] == "entities" => "read_entity",
                4 when segments[1] == "chapters" && segments[3] == "manuscript" => "read_manuscript",
                4 when segments[1] == "workspaces" && segments[3] == "context" => "get_workspace_context",
                _ => throw new McpException("Unknown Lorekeeper resource."),
            };
            if (segments.Length > 2)
            {
                if (!Guid.TryParse(segments[2], out var target)) throw new McpException("An exact target ID is required.");
                arguments[segments[1] == "entities" ? "entityId" : segments[1] == "workspaces" ? "workspaceId" : "chapterId"] = target;
            }
        }
        var result = await dispatcher.CallAsync(tool, JsonSerializer.SerializeToElement(arguments, ExternalToolCatalog.Json), ct);
        if (result.IsError == true) throw new McpException(result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "Resource unavailable.");
        return new ReadResourceResult { Contents = [new TextResourceContents { Uri = resource, MimeType = "application/json",
            Text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(value => value.Text)) }] };
    }
}
