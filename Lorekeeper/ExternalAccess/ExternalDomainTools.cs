namespace Lorekeeper.ExternalAccess;

internal static class ExternalDomainTools
{
    private static readonly string[] Narrative = ["Outline", "Editor", "World", "Images", "Voice", "Publish"];

    public static void Add(List<ExternalToolDefinition> tools)
    {
        tools.Add(ExternalToolCatalog.Define<WorkspaceRequest>("get_project", "Read project identity, direction and current revision tokens without changing the UI.", ["projects.read", "direction.read"], Narrative, false, (owner, input, ct) => owner.GetProjectAsync(input, ct)));
        tools.Add(ExternalToolCatalog.Define<WorkspaceRequest>("list_outline", "Read acts and ordered chapter metadata; manuscript content has a separate exact read.", ["outline.read"], Narrative, false, (owner, input, ct) => owner.ListOutlineAsync(input, ct)));
        tools.Add(ExternalToolCatalog.Define<ManuscriptReadRequest>("read_manuscript", "Read agent-manuscript-v4 with stable block IDs, full revision/hash and semantic structure. Follow pagination and required readback ranges.", ["manuscript.read"], Narrative, false, (owner, input, ct) => owner.ReadManuscriptAsync(input, ct)));
        tools.Add(ExternalToolCatalog.Define<EntityReadRequest>("read_entity", "Read exact canon, properties and manual/managed relationships before changing it.", ["canon.read"], Narrative, false, (owner, input, ct) => owner.ReadEntityAsync(input, ct)));
        tools.Add(ExternalToolCatalog.Define<EntityListRequest>("list_entities", "List entities of an exact project type; use exact reads for mutations.", ["canon.read"], Narrative, false, (owner, input, ct) => owner.ListEntitiesAsync(input, ct)));
        tools.Add(ExternalToolCatalog.Define<WorkspaceRequest>("read_world_brief", "Read World Brief and its independent revision. Working notes are returned only on the World surface.", ["direction.read"], Narrative, false, (owner, input, ct) => owner.ReadWorldAsync(input, ct)));
        Add<CreateOutlineRequest>("create_outline_item", "Create an act or chapter. Chapters receive empty semantic manuscripts. Reuse mutationId on retries.", ["outline.write"], ["Outline"], true, (owner, input, ct) => owner.CreateOutlineAsync(input, ct));
        Add<GuidanceWriteRequest>("update_project_guidance", "Save Project Guidance using the exact guidanceVersion from get_project.", ["direction.read", "direction.write"], ["Outline", "Editor", "World"], true, (owner, input, ct) => owner.SaveGuidanceAsync(input, ct));
        Add<BookBriefWriteRequest>("update_book_brief", "Partially update Book Brief using its exact bookBriefVersion. Null fields remain unchanged; clearFields explicitly clears.", ["direction.read", "direction.write"], ["Outline", "World"], true, (owner, input, ct) => owner.SaveBriefAsync(input, ct));
        Add<WorldWriteRequest>("update_world_brief", "Save canonical World Brief or noncanonical Working Notes with the independent revision read from read_world_brief.", ["direction.read", "direction.write"], ["World"], true, (owner, input, ct) => owner.SaveWorldAsync(input, ct));
        Add<CreateEntityRequest>("create_entity", "Create canon or a chapter-owned Event/beat with explicit type, properties and parent. Discover existing entities first.", ["canon.read", "canon.write"], ["Outline", "World"], true, (owner, input, ct) => owner.CreateEntityAsync(input, ct));
        Add<EntityWriteRequest>("update_entity", "Update canon using the exact entityVersion from read_entity. Managed source-evidence properties remain protected.", ["canon.read", "canon.write"], ["Outline", "Editor", "World"], true, (owner, input, ct) => owner.SaveEntityAsync(input, ct));
        Add<WorkspaceRequest>("list_entity_types", "Read the project's canonical entity type registry.", ["canon.read"], Narrative, false, (owner, input, ct) => owner.ListEntityTypesAsync(input, ct));
        Add<CreateEntityTypeRequest>("create_entity_type", "Create a project-specific non-structural canon type.", ["canon.write"], ["Outline", "World"], true, (owner, input, ct) => owner.CreateEntityTypeAsync(input, ct));
        Add<LinkEntitiesRequest>("link_entities", "Create a focused manual relationship. Use RelevantTo for material chapter associations, and AppearsIn only for Character-to-Event links.", ["canon.read", "canon.write"], ["Outline", "Editor", "World"], true, (owner, input, ct) => owner.LinkEntitiesAsync(input, ct));
        Add<WorkspaceRequest>("list_project_facts", "Read project facts and exact factVersion tokens; use the returned emptyFactVersion for a new key.", ["canon.read"], Narrative, false, (owner, input, ct) => owner.ListFactsAsync(input, ct));
        Add<FactWriteRequest>("save_project_fact", "Save a project fact at its exact factVersion, or emptyFactVersion for a previously absent key.", ["canon.read", "canon.write"], ["Outline", "World"], true, (owner, input, ct) => owner.SaveFactAsync(input, ct));
        Add<ManuscriptWriteRequest>("apply_manuscript_operations", "Apply one atomic semantic operation batch at expectedRevision. Preserve stable IDs, inline atoms, formatting, Figures and Designed Page references. Read every returned readback range before finishing.", ["manuscript.read", "manuscript.write"], ["Editor"], true, (owner, input, ct) => owner.ApplyManuscriptAsync(input, ct));
        Add<WorkspaceRequest>("list_manuscript_styles", "Read reusable Book Text Styles and revisions.", ["manuscript.read"], ["Editor", "Publish"], false, (owner, input, ct) => owner.ListStylesAsync(input, ct));
        Add<StyleWriteRequest>("save_manuscript_style", "Save a reusable style at its expectedRevision. A release workspace cannot mutate an existing shared style.", ["manuscript.read", "manuscript.write"], ["Editor"], true, (owner, input, ct) => owner.SaveStyleAsync(input, ct));
        Add<SearchProjectRequest>("search_project", "Search permitted source types for one concise content facet. Search is discovery: read exact sources before writing. lexicalOnly avoids an embedding-provider call.", ["sources.read"], Narrative, false, (owner, input, ct) => owner.SearchProjectAsync(input, ct));
        Add<ReadSourceRequest>("read_project_source", "Read an exact project or permitted one-hop referenced source. Follow every page; referenced evidence is read-only.", ["sources.read"], Narrative, false, (owner, input, ct) => owner.ReadSourceAsync(input, ct));
        ExternalSurfaceTools.Add(tools);
        ExternalTransferTools.Add(tools);
        ExternalAdvancedTools.Add(tools);

        void Add<T>(string name, string description, string[] capabilities, string[] surfaces, bool mutates,
            Func<ExternalAccessCommands, T, CancellationToken, Task<object>> invoke) =>
            tools.Add(ExternalToolCatalog.Define(name, description, capabilities, surfaces, mutates, invoke));
    }
}

public sealed record ManuscriptReadRequest(Guid ProjectId, Guid WorkspaceId, Guid ChapterId, int StartBlock = 0, int BlockCount = 50, int PageNumber = 1);
public sealed record EntityReadRequest(Guid ProjectId, Guid WorkspaceId, Guid EntityId, int PageNumber = 1);
public sealed record EntityListRequest(Guid ProjectId, Guid WorkspaceId, string Type, Guid? ParentId = null, int PageNumber = 1);
public sealed record CreateOutlineRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, string Kind, string Title, string Synopsis = "", Guid? ActId = null);
public sealed record GuidanceWriteRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, string ExpectedVersion, string Content);
public sealed record BookBriefWriteRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, DateTime ExpectedUpdatedAt, Lorekeeper.Models.BookBriefPatch Patch);
public sealed record WorldWriteRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, long ExpectedRevision, string Content, bool WorkingNotes = false);
public sealed record CreateEntityRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, string Type, string Name, Dictionary<string, string?>? Properties = null, Guid? ParentId = null);
public sealed record EntityWriteRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, Guid EntityId, string ExpectedVersion, string? Name = null, Dictionary<string, string?>? Properties = null, string[]? RemoveProperties = null);
public sealed record CreateEntityTypeRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, string Type);
public sealed record LinkEntitiesRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, Guid FromEntityId, Guid ToEntityId, string EdgeType, Dictionary<string, string?>? Properties = null);
public sealed record FactWriteRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, string Key, string Value, string ExpectedVersion);
public sealed record ManuscriptWriteRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, Guid ChapterId, long ExpectedRevision, Lorekeeper.Manuscripts.ManuscriptOperationInput[] Operations);
public sealed record StyleWriteRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, Lorekeeper.Manuscripts.ManuscriptStyleInput Style);
public sealed record SearchProjectRequest(Guid ProjectId, Guid WorkspaceId, string Query, string[]? SourceTypes = null, bool LexicalOnly = false, bool IncludeReferencedProjects = false, int Limit = 20);
public sealed record ReadSourceRequest(Guid ProjectId, Guid WorkspaceId, string SourceType, Guid SourceId, Guid? OriginProjectId = null, int PageNumber = 1);
