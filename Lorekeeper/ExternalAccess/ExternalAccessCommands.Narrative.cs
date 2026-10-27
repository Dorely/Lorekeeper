using System.Text.Json;
using Lorekeeper.Chapters;
using Lorekeeper.Context;
using Lorekeeper.Manuscripts;
using Lorekeeper.Outline;
using Lorekeeper.Projects;
using Lorekeeper.Persistence;
using Lorekeeper.Search;
using Lorekeeper.Writing;

namespace Lorekeeper.ExternalAccess;

internal sealed partial class ExternalAccessCommands
{
    internal async Task<object> GetProjectAsync(WorkspaceRequest input, CancellationToken ct)
    {
        var project = await Service<IProjectService>().GetByIdAsync(input.ProjectId, ct) ?? throw new KeyNotFoundException("Project not found.");
        var brief = await Service<IBookBriefService>().GetOrCreateAsync(input.ProjectId, ct);
        return Page(new { project.Id, project.Name, project.Slug, project.ProjectGuidance,
            guidanceVersion = ContentVersion.For(project.ProjectGuidance), bookBrief = brief,
            bookBriefVersion = brief.UpdatedAt, project.ReviewEditsEnabled }, input, "get_project");
    }

    internal async Task<object> ListOutlineAsync(WorkspaceRequest input, CancellationToken ct) => Page(new
    {
        acts = (await Service<IActService>().ListAsync(input.ProjectId, ct)).Select(value => new { value.Id, value.Title, value.Synopsis, value.Order, value.UpdatedAt, metadataVersion = ContentVersion.For(OutlineMetadata(value)) }),
        actOrderVersion = ContentVersion.For((await Service<IActService>().ListAsync(input.ProjectId, ct)).Select(value => value.Id).ToArray()),
        chapters = (await Service<IChapterService>().ListAsync(input.ProjectId, ct)).Select(value => new { value.Id, value.ActId, value.Title, value.Synopsis, value.Order, value.ManuscriptRevision, value.UpdatedAt, metadataVersion = ContentVersion.For(OutlineMetadata(value)) }),
        chapterOrderVersions = (await Service<IChapterService>().ListAsync(input.ProjectId, ct)).GroupBy(value => value.ActId)
            .Select(group => new { actId = group.Key, orderVersion = ContentVersion.For(group.Select(value => value.Id).ToArray()) }),
    }, input, "list_outline");

    internal async Task<object> ReadManuscriptAsync(ManuscriptReadRequest input, CancellationToken ct)
    {
        var chapter = await Service<IChapterService>().GetAsync(input.ChapterId, ct);
        if (chapter?.ProjectId != input.ProjectId) throw new KeyNotFoundException("Chapter not found in this project.");
        if (Workspace!.ChapterId is Guid selected && selected != chapter.Id)
            throw new ExternalAccessException("TARGET_CHANGED", "Open an Editor workspace for this chapter before using its manuscript.");
        var snapshot = await Service<IManuscriptService>().GetManuscriptAsync(Target, chapter.Id, ct) ?? throw new KeyNotFoundException("Manuscript not found.");
        return JsonSerializer.Deserialize<JsonElement>(AgentManuscriptProjection.SerializeDocument(snapshot.Document, "persisted",
            Math.Max(0, input.StartBlock), Math.Clamp(input.BlockCount, 1, 100), chapter.Id, chapter.Title, snapshot.SourceHash));
    }

    internal async Task<object> ReadEntityAsync(EntityReadRequest input, CancellationToken ct)
    {
        var entity = await Service<IEntityService>().GetAsync(input.ProjectId, input.EntityId, ct) ?? throw new KeyNotFoundException("Entity not found.");
        return Page(new { entity = ReadableEntity(entity), entityVersion = ContentVersion.For(entity), links = (await Service<IEntityService>().ListLinksAsync(input.ProjectId, input.EntityId, ct)).Select(value => new { link = value, linkVersion = ContentVersion.For(value) }) },
            input, "read_entity");
    }

    internal async Task<object> ListEntitiesAsync(EntityListRequest input, CancellationToken ct) =>
        Page((await Service<IEntityService>().ListAsync(input.ProjectId, input.Type, input.ParentId, ct)).Select(ReadableEntity),
            input, "list_entities");

    internal async Task<object> ReadWorldAsync(WorkspaceRequest input, CancellationToken ct)
    {
        var brief = await Service<IWorldBriefService>().GetAsync(input.ProjectId, ct);
        return Page(new { brief.Content, brief.Revision,
            workingNotes = Workspace!.Surface == "World" ? brief.WorkingNotes : null,
            workingNotesRevision = Workspace.Surface == "World" ? (long?)brief.WorkingNotesRevision : null }, input, "read_world_brief");
    }

    internal async Task<object> CreateOutlineAsync(CreateOutlineRequest input, CancellationToken ct)
    {
        if (input.Kind.Equals("Act", StringComparison.OrdinalIgnoreCase))
        {
            var act = await Service<IActService>().CreateAsync(input.ProjectId, input.Title, input.Synopsis, cancellationToken: ct);
            return new { act.Id, act.Title, act.Order };
        }
        if (!input.Kind.Equals("Chapter", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Kind must be Act or Chapter.");
        if (input.ActId is Guid actId && (await Service<IActService>().GetAsync(actId, ct))?.ProjectId != input.ProjectId)
            throw new KeyNotFoundException("Act not found in this project.");
        var chapter = await Service<IChapterService>().CreateAsync(input.ProjectId, input.ActId, input.Title, input.Synopsis, cancellationToken: ct);
        return new { chapter.Id, chapter.Title, chapter.Order, chapter.ManuscriptRevision };
    }

    internal async Task<object> SaveGuidanceAsync(GuidanceWriteRequest input, CancellationToken ct)
    {
        var value = await Service<IProjectService>().UpdateProjectGuidanceAsync(input.ProjectId, input.Content, ct, input.ExpectedVersion);
        return new { value.Id, guidanceVersion = ContentVersion.For(value.ProjectGuidance) };
    }

    internal async Task<object> SaveBriefAsync(BookBriefWriteRequest input, CancellationToken ct)
    {
        var value = await Service<IBookBriefService>().UpdateAsync(input.ProjectId, input.Patch, ct, input.ExpectedUpdatedAt);
        return new { value.ProjectId, bookBriefVersion = value.UpdatedAt };
    }

    internal async Task<object> SaveWorldAsync(WorldWriteRequest input, CancellationToken ct)
    {
        var value = input.WorkingNotes
            ? await Service<IWorldBriefService>().UpdateWorkingNotesAsync(input.ProjectId, input.ExpectedRevision, input.Content, ct)
            : await Service<IWorldBriefService>().UpdateAsync(input.ProjectId, input.ExpectedRevision, input.Content, ct);
        return new { value.ProjectId, value.Revision, value.WorkingNotesRevision };
    }

    internal async Task<object> ListEntityTypesAsync(WorkspaceRequest input, CancellationToken ct) =>
        await Service<IEntityTypeService>().ListAsync(input.ProjectId, cancellationToken: ct);

    internal async Task<object> CreateEntityTypeAsync(CreateEntityTypeRequest input, CancellationToken ct) =>
        await Service<IEntityTypeService>().CreateAsync(input.ProjectId, input.Type, cancellationToken: ct);

    internal async Task<object> CreateEntityAsync(CreateEntityRequest input, CancellationToken ct)
    {
        RequireVoicePropertyPermission(input.Properties?.Keys);
        var type = (await Service<IEntityTypeService>().ListAsync(input.ProjectId, includeStructural: true, cancellationToken: ct))
            .SingleOrDefault(value => value.Type == input.Type && (!value.IsStructural || value.Type == "Event"))
            ?? throw new ArgumentException("Use an existing non-structural type, or Event for a chapter-owned beat.");
        var value = await Service<IEntityService>().CreateAsync(input.ProjectId, type.Type, input.Name, input.Properties, input.ParentId, cancellationToken: ct);
        return new { value.Id, value.Name, entityVersion = ContentVersion.For(value) };
    }

    internal async Task<object> SaveEntityAsync(EntityWriteRequest input, CancellationToken ct)
    {
        RequireVoicePropertyPermission(input.Properties?.Keys.Concat(input.RemoveProperties ?? []) ?? input.RemoveProperties);
        if (!Target.IsCore) throw new ExternalAccessException("CORE_OWNER_REQUIRED", "Edit project canon from a Core workspace.");
        var value = await Service<IEntityService>().UpdateAsync(input.ProjectId, input.EntityId, input.Name, input.Properties,
            input.RemoveProperties, ct, input.ExpectedVersion);
        return new { value.Id, entityVersion = ContentVersion.For(value) };
    }

    internal async Task<object> LinkEntitiesAsync(LinkEntitiesRequest input, CancellationToken ct)
    {
        var existing = await Service<IEntityService>().ListLinksAsync(input.ProjectId, input.FromEntityId, ct);
        if (existing.Any(value => value.Direction == EntityLinkDirection.Outgoing && value.OtherEntityId == input.ToEntityId && value.EdgeType == input.EdgeType))
            throw new ExternalAccessException("RELATIONSHIP_EXISTS", "This relationship already exists. Read its current state before changing it.");
        await Service<IEntityService>().LinkAsync(input.ProjectId, input.FromEntityId, input.ToEntityId, input.EdgeType, input.Properties, ct);
        return new { linked = true, input.FromEntityId, input.ToEntityId, input.EdgeType };
    }

    internal async Task<object> ListFactsAsync(WorkspaceRequest input, CancellationToken ct) => Page(new
    {
        emptyFactVersion = ContentVersion.For<ProjectFact?>(null),
        facts = (await Service<IProjectFactService>().ListAsync(input.ProjectId, ct)).Select(value => new { fact = value, factVersion = ContentVersion.For(value) }),
    }, input, "list_project_facts");

    internal async Task<object> SaveFactAsync(FactWriteRequest input, CancellationToken ct)
    {
        var value = await Service<IProjectFactService>().UpsertAsync(input.ProjectId, input.Key, input.Value, cancellationToken: ct, expectedVersion: input.ExpectedVersion);
        return new { fact = value, factVersion = ContentVersion.For(value) };
    }

    internal async Task<object> ApplyManuscriptAsync(ManuscriptWriteRequest input, CancellationToken ct)
    {
        if (Workspace!.ChapterId != input.ChapterId) throw new ExternalAccessException("TARGET_CHANGED", "Open Editor on this exact chapter first.");
        if (input.Operations.Length is < 1 or > 256) throw new ArgumentException("Use between one and 256 semantic operations per batch.");
        var snapshot = await Service<IManuscriptService>().GetManuscriptAsync(Target, input.ChapterId, ct)
            ?? throw new KeyNotFoundException("Manuscript not found.");
        RequireOperationPermissions(snapshot.Document, input.Operations);
        return await Service<ManuscriptOperationCommand>().ApplyAsync(input.ProjectId, Target, input.ChapterId, input.ExpectedRevision, input.Operations, ct);
    }

    internal async Task<object> ListStylesAsync(WorkspaceRequest input, CancellationToken ct) =>
        Page(await Service<IManuscriptStyleService>().ListAsync(input.ProjectId, ct), input, "list_manuscript_styles");

    internal async Task<object> SaveStyleAsync(StyleWriteRequest input, CancellationToken ct)
    {
        if (!Target.IsCore && input.Style.Id is not null) throw new ExternalAccessException("CORE_OWNER_REQUIRED", "A release can create a style copy but cannot change an existing shared style.");
        return await Service<IManuscriptStyleService>().UpsertAsync(input.ProjectId, input.Style, ct);
    }

    private string[] ReadableSourceTypes() => ProjectSearchSourceTypes.All.Where(type => type switch
    {
        ProjectSearchSourceTypes.Chapter or ProjectSearchSourceTypes.ContextChapter or ProjectSearchSourceTypes.EditionChapter => ExternalCapabilities.Has(Client, "manuscript.read"),
        ProjectSearchSourceTypes.Entity => ExternalCapabilities.Has(Client, "canon.read", "voice.read", "images.read"),
        ProjectSearchSourceTypes.Act => ExternalCapabilities.Has(Client, "outline.read"),
        ProjectSearchSourceTypes.ProjectProfile => ExternalCapabilities.Has(Client, "direction.read"),
        ProjectSearchSourceTypes.WritingSample => ExternalCapabilities.Has(Client, "voice.read"),
        ProjectSearchSourceTypes.DesignedPage => ExternalCapabilities.Has(Client, "composition.read"),
        _ => ExternalCapabilities.Has(Client, "sources.read"),
    }).ToArray();

    internal async Task<object> SearchProjectAsync(SearchProjectRequest input, CancellationToken ct)
    {
        var readable = ReadableSourceTypes();
        var types = input.SourceTypes is null ? readable : input.SourceTypes.Select(ProjectSearchSourceTypes.Normalize).Intersect(readable, StringComparer.OrdinalIgnoreCase).ToArray();
        if (types.Length == 0) return new { results = Array.Empty<object>(), complete = true };
        var references = input.IncludeReferencedProjects && Client.ProjectIdsJson is null;
        var response = await Service<IProjectSearchService>().SearchAsync(new(input.ProjectId, input.Query, Math.Clamp(input.Limit, 1, 30), types,
            LexicalOnly: input.LexicalOnly, IncludeReferencedProjects: references), ct);
        return JsonSerializer.Deserialize<JsonElement>(ProjectSearchAgentPayload.SerializeResults(input.Query, response));
    }

    internal async Task<object> ReadSourceAsync(ReadSourceRequest input, CancellationToken ct)
    {
        if (!ReadableSourceTypes().Contains(ProjectSearchSourceTypes.Normalize(input.SourceType), StringComparer.OrdinalIgnoreCase)
            || input.OriginProjectId is Guid origin && !ExternalCapabilities.AllowsProject(Client, origin))
            throw new ExternalAccessException("ACCESS_DENIED", "This source is outside the connection's read permissions.");
        var result = await Service<IProjectSearchService>().ReadSourceAsync(input.ProjectId, input.SourceType, input.SourceId, input.PageNumber, ct, input.OriginProjectId)
            ?? throw new KeyNotFoundException("Source not found in this project's permitted scope.");
        return result;
    }

    private void RequireOperationPermissions(ManuscriptDocument source, IReadOnlyList<ManuscriptOperationInput> operations)
    {
        if (operations.Count is < 1 or > 256) throw new ArgumentException("Use between one and 256 semantic operations per batch.");
        var visualIds = ManuscriptTraversal.EnumerateBlocks(source).Where(value => value.Type is ManuscriptBlockType.Figure or ManuscriptBlockType.DesignedPage)
            .Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
        if (operations.Any(value => value.BlockType?.Equals("Figure", StringComparison.OrdinalIgnoreCase) == true
            || value.BlockType?.Equals("DesignedPage", StringComparison.OrdinalIgnoreCase) == true
            || value.BlockId is not null && visualIds.Contains(value.BlockId)
            || value.SecondBlockId is not null && visualIds.Contains(value.SecondBlockId))
            && !ExternalCapabilities.Has(Client, "composition.read", "composition.write", "images.read"))
            throw new ExternalAccessException("ACCESS_DENIED", "Every operation touching Figures or Designed Pages requires visual read and composition permissions.");
        if (operations.Any(value => value.Operation.Equals("DeleteBlock", StringComparison.OrdinalIgnoreCase))
            && !ExternalCapabilities.Has(Client, "content.delete"))
            throw new ExternalAccessException("ACCESS_DENIED", "Removing manuscript blocks requires the content removal capability.");
    }

    private StoryEntity ReadableEntity(StoryEntity entity) => ExternalCapabilities.Has(Client, "voice.read") ? entity
        : entity with { Properties = entity.Properties.Where(value => !value.Key.Equals(VoiceProfileService.PropertyName, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase) };

    private void RequireVoicePropertyPermission(IEnumerable<string>? properties)
    {
        if (properties?.Any(value => value.Trim().Equals(VoiceProfileService.PropertyName, StringComparison.OrdinalIgnoreCase)) == true
            && !ExternalCapabilities.Has(Client, "voice.write"))
            throw new ExternalAccessException("ACCESS_DENIED", "Character voice profiles require voice editing permission.");
    }
}
