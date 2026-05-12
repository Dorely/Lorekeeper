using System.Text.Json;
using Lorekeeper.Context;
using Lorekeeper.Outline;
using Microsoft.Extensions.AI;

namespace Lorekeeper.Writing;

public sealed record WritingCoachContext(
    Guid ProjectId,
    string? CurrentSampleTitle,
    string? CurrentSampleBody);

public sealed class WritingCoachTools(IProjectFactService projectFacts, IEntityRelationContextService entityRelations)
{
    private static readonly EntityRelationContextOptions EntityRelationOptions = new()
    {
        Depth = 2,
        MaxDirectLinks = 8,
        MaxTraversalPaths = 10,
        MaxLinksPerNode = 8,
    };

    public IList<AITool> Build(WritingCoachContext context)
    {
        return new List<AITool>
        {
            AIFunctionFactory.Create(
                method: () => ReadCurrentSection(context),
                name: "read_current_section",
                description: "Read the latest current writing sample section from the editor as JSON. This is read-only and cannot modify stored samples."),

            AIFunctionFactory.Create(
                method: () => ListProjectFactsAsync(context),
                name: "list_project_facts",
                description: "Read project-level facts as JSON so coaching can stay grounded in the project's premise, tone, constraints, and other established truths. This is read-only."),
        };
    }

    private static string ReadCurrentSection(WritingCoachContext context)
    {
        var title = string.IsNullOrWhiteSpace(context.CurrentSampleTitle)
            ? "Untitled writing sample"
            : context.CurrentSampleTitle.Trim();
        var body = context.CurrentSampleBody ?? string.Empty;

        return JsonSerializer.Serialize(new
        {
            title,
            body,
            isEmpty = string.IsNullOrWhiteSpace(body),
        });
    }

    private async Task<string> ListProjectFactsAsync(WritingCoachContext context)
    {
        var facts = await projectFacts.ListAsync(context.ProjectId);
        var payload = new List<object>();
        foreach (var fact in facts)
        {
            var linkedEntities = new List<object>();
            foreach (var link in fact.LinkedEntities)
            {
                linkedEntities.Add(new
                {
                    link.EdgeType,
                    direction = link.Direction.ToString(),
                    link.EntityId,
                    link.EntityName,
                    link.EntityType,
                    relationContext = await entityRelations.BuildForEntityAsync(context.ProjectId, link.EntityId, EntityRelationOptions),
                });
            }

            payload.Add(new
            {
                fact.Id,
                fact.Key,
                fact.Name,
                fact.Value,
                linkedEntities,
            });
        }

        return JsonSerializer.Serialize(payload);
    }
}
