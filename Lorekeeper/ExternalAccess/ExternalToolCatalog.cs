using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Protocol;

namespace Lorekeeper.ExternalAccess;

internal sealed record ExternalToolDefinition(Tool Tool, string[] Capabilities, string[] Surfaces,
    bool Mutates, Func<ExternalAccessCommands, JsonElement, CancellationToken, Task<object>> Invoke);

internal static class ExternalToolCatalog
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Converters = { new JsonStringEnumConverter() },
    };

    public static IReadOnlyList<ExternalToolDefinition> Tools { get; } = Build();

    private static IReadOnlyList<ExternalToolDefinition> Build()
    {
        var tools = new List<ExternalToolDefinition>();
        Add<DiscoveryRequest>("list_projects", "Discover explicitly accessible project IDs.", ["projects.read"], [], false, (owner, input, ct) => owner.ListProjectsAsync(input, ct));
        Add<CreateProjectRequest>("create_project", "Create an editable project. Reuse mutationId when retrying an uncertain response.", ["projects.create"], [], true, (owner, input, ct) => owner.CreateProjectAsync(input, ct));
        Add<OpenWorkspaceRequest>("open_workspace", "Start or resume a task on Outline, Editor, World, Images, Voice or Publish. Read the returned context pages before surface tools. Targets are explicit and independent of the UI.", ["projects.read"], [], false, (owner, input, ct) => owner.OpenWorkspaceAsync(input, ct));
        Add<WorkspaceContextRequest>("get_workspace_context", "Rebuild current application context for this exact workspace. Use after reconnect, compaction, a new task direction, or a conflict. Preserve expectedContextVersion in continuations; a changed context restarts at page one.", ["projects.read"], [], false, (owner, input, ct) => owner.ContextAsync(input, ct));
        Add<DiscoveryRequest>("list_unfinished_work", "Recover task and workspace identities after reconnect; then refresh context.", ["projects.read"], [], false, (owner, input, ct) => owner.ListWorkAsync(input, ct));
        Add<MutationReceiptRequest>("read_mutation_receipt", "Recover the committed outcome and repair status of an uncertain mutation without repeating it. Current project and operation permissions still apply.", ["projects.read"], [], false, (owner, input, ct) => owner.ReadReceiptAsync(input, ct));
        Add<WorkspaceRequest>("list_work_mutations", "Recover mutation IDs and operation identities for this task after reconnect or lost context. Each receipt is filtered by current permissions.", ["projects.read"], [], false, (owner, input, ct) => owner.ListWorkMutationsAsync(input, ct));
        Add<FinishWorkRequest>("finish_work", "Finish the complete external task. Honors Review Edits; otherwise checkpoints complete live state and inherits configured automatic remote sync. Pending background jobs must finish first.", ["projects.read"], [], false, (owner, input, ct) => owner.FinishWorkAsync(input, ct));
        Add<FinishWorkRequest>("abandon_work", "Stop a task without rolling back its already saved work or creating a completion checkpoint.", ["projects.read"], [], false, (owner, input, ct) => owner.AbandonWorkAsync(input, ct));
        AddDomainTools(tools);
        return tools;

        void Add<T>(string name, string description, string[] capabilities, string[] surfaces, bool mutates,
            Func<ExternalAccessCommands, T, CancellationToken, Task<object>> invoke) => tools.Add(Define(name, description, capabilities, surfaces, mutates, invoke));
    }

    internal static ExternalToolDefinition Define<T>(string name, string description, string[] capabilities,
        string[] surfaces, bool mutates, Func<ExternalAccessCommands, T, CancellationToken, Task<object>> invoke) => new(
            new Tool
            {
                Name = name,
                Description = description,
                InputSchema = InputSchema<T>(),
                Annotations = new ToolAnnotations { ReadOnlyHint = !mutates, DestructiveHint = capabilities.Contains("content.delete"), IdempotentHint = mutates },
            }, capabilities, surfaces, mutates,
            (owner, input, ct) => invoke(owner, input.Deserialize<T>(Json) ?? throw new ArgumentException("Tool arguments are required."), ct));

    private static JsonElement InputSchema<T>()
    {
        var schema = Json.GetJsonSchemaAsNode(typeof(T)).AsObject();
        // MCP arguments are always a JSON object; the exporter also allows null for reference types.
        schema["type"] = "object";
        return JsonSerializer.SerializeToElement(schema);
    }

    private static void AddDomainTools(List<ExternalToolDefinition> tools) => ExternalDomainTools.Add(tools);
}

public sealed record DiscoveryRequest(int PageNumber = 1);
public sealed record MutationReceiptRequest(Guid MutationId);
public sealed record CreateProjectRequest(Guid MutationId, string Name);
public sealed record OpenWorkspaceRequest(Guid ProjectId, string Surface, string Task,
    Guid? ChapterId = null, Guid? EditionId = null, Guid? WorkId = null, int PageNumber = 1);
public record WorkspaceRequest(Guid ProjectId, Guid WorkspaceId, int PageNumber = 1);
public sealed record WorkspaceContextRequest(Guid ProjectId, Guid WorkspaceId, int PageNumber = 1, string? ExpectedContextVersion = null);
public sealed record FinishWorkRequest(Guid ProjectId, Guid WorkId, string Summary);
