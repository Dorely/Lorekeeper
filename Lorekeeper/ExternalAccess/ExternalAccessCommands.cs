using System.Text.Json;
using System.Text.Json.Nodes;
using Lorekeeper.Authoring;
using Lorekeeper.Chapters;
using Lorekeeper.Context;
using Lorekeeper.Llm;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Projects;
using Lorekeeper.VersionHistory.Services;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.ExternalAccess;

internal sealed partial class ExternalAccessCommands(IServiceProvider services, IAppDatabaseOperationFactory database,
    McpClientGrantService grants, IHttpContextAccessor http)
{
    internal McpClientGrant Client { get; private set; } = null!;
    internal ExternalWorkspace? Workspace { get; private set; }
    internal ExternalWork? Work { get; private set; }
    internal EditorContentTarget Target => Workspace?.EditionId is Guid id ? EditorContentTarget.ForEdition(id) : EditorContentTarget.Core;
    private T Service<T>() where T : notnull => services.GetRequiredService<T>();

    internal async Task InitializeAsync(JsonElement arguments, ExternalToolDefinition definition, CancellationToken ct)
    {
        Workspace = null;
        Work = null;
        Client = await grants.RequireAsync(ExternalAccessHttpGate.ClientId(http.HttpContext!), ct);
        if (!ExternalCapabilities.Has(Client, definition.Capabilities))
            throw new ExternalAccessException("ACCESS_DENIED", "This operation is not enabled for this client. Review External Access permissions in Lorekeeper.");
        if (arguments.TryGetProperty("projectId", out var projectValue))
        {
            if (!projectValue.TryGetGuid(out var projectId) || !ExternalCapabilities.AllowsProject(Client, projectId))
                throw new ExternalAccessException("PROJECT_ACCESS_DENIED", "This project is not available to this connection.");
        }
        if (definition.Surfaces.Length == 0 && !arguments.TryGetProperty("workspaceId", out _)) return;
        var request = arguments.Deserialize<WorkspaceRequest>(ExternalToolCatalog.Json)!;
        await using var read = await database.OpenReadAsync(ct);
        Workspace = await read.Db.ExternalWorkspaces.SingleOrDefaultAsync(value => value.Id == request.WorkspaceId, ct)
            ?? throw new ExternalAccessException("ONBOARDING_REQUIRED", "Call open_workspace before using this surface's tools.");
        Work = await read.Db.ExternalWorks.SingleOrDefaultAsync(value => value.Id == Workspace.WorkId
            && value.ProjectId == request.ProjectId && value.ClientGrantId == Client.Id, ct)
            ?? throw new ExternalAccessException("WORK_NOT_ACTIVE", "Resume or open a workspace for this project and client.");
        if (definition.Surfaces.Length > 0 && !definition.Surfaces.Contains(Workspace.Surface, StringComparer.Ordinal))
            throw new ExternalAccessException("SURFACE_REQUIRED", $"Open one of these surfaces first: {string.Join(", ", definition.Surfaces)}.");
    }

    internal void RequireActiveWork()
    {
        if (Work is { Status: not "Active" })
            throw new ExternalAccessException("WORK_NOT_ACTIVE", "This task is no longer active. Open a new workspace to continue; saved mutations can still be retrieved with their original mutation IDs.");
    }

    internal void RequireReceiptPermission(ExternalMutationReceipt receipt)
    {
        var definition = ExternalToolCatalog.Tools.SingleOrDefault(value => value.Tool.Name == receipt.Operation);
        if (receipt.ClientGrantId != Client.Id || definition is null || !ExternalCapabilities.Has(Client, definition.Capabilities)
            || receipt.ProjectId is Guid projectId && !ExternalCapabilities.AllowsProject(Client, projectId))
            throw new ExternalAccessException("ACCESS_DENIED", "This saved mutation is outside the connection's current permissions.");
    }

    internal async Task<object> ReadReceiptAsync(MutationReceiptRequest input, CancellationToken ct)
    {
        await using var read = await database.OpenReadAsync(ct);
        var receipt = await read.Db.ExternalMutationReceipts.SingleOrDefaultAsync(value => value.ClientGrantId == Client.Id && value.MutationId == input.MutationId, ct);
        if (receipt is null) return new { input.MutationId, committed = false, known = false,
            guidance = "No committed receipt is visible yet. Retry the original payload and mutationId; a still-running request may commit later." };
        RequireReceiptPermission(receipt);
        return JsonSerializer.Deserialize<JsonElement>(receipt.ResultJson);
    }

    internal async Task<object> ListProjectsAsync(DiscoveryRequest input, CancellationToken ct) =>
        Page((await Service<IProjectService>().ListAsync(ct)).Where(value => ExternalCapabilities.AllowsProject(Client, value.Id))
            .Select(value => new { value.Id, value.Name, value.Slug, value.UpdatedAt }).ToArray(), input, "list_projects");

    internal async Task<object> CreateProjectAsync(CreateProjectRequest input, CancellationToken ct)
    {
        var project = await Service<IProjectService>().CreateAsync(input.Name, ct);
        if (Client.ProjectIdsJson is not null)
        {
            var ids = JsonSerializer.Deserialize<Guid[]>(Client.ProjectIdsJson) ?? [];
            await using var operation = await database.OpenWriteAsync(ct);
            var grant = await operation.Db.McpClientGrants.SingleAsync(value => value.Id == Client.Id, ct);
            grant.ProjectIdsJson = JsonSerializer.Serialize(ids.Append(project.Id).Distinct());
            await operation.SaveChangesAsync(ct);
        }
        return new { project.Id, project.Name, project.Slug };
    }

    internal async Task<object> ListWorkMutationsAsync(WorkspaceRequest input, CancellationToken ct)
    {
        await using var read = await database.OpenReadAsync(ct);
        var receipts = await read.Db.ExternalMutationReceipts.Where(value => value.WorkId == Work!.Id && value.ClientGrantId == Client.Id)
            .OrderBy(value => value.CreatedAt).ToArrayAsync(ct);
        return Page(receipts.Where(value => ExternalToolCatalog.Tools.Any(tool => tool.Tool.Name == value.Operation
                && ExternalCapabilities.Has(Client, tool.Capabilities)))
            .Select(value => new { value.MutationId, value.Operation, value.CreatedAt, value.RepairPending }), input, "list_work_mutations");
    }

    internal async Task<object> OpenWorkspaceAsync(OpenWorkspaceRequest input, CancellationToken ct)
    {
        var surface = new[] { "Outline", "Editor", "World", "Images", "Voice", "Publish" }
            .SingleOrDefault(value => value.Equals(input.Surface, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Choose Outline, Editor, World, Images, Voice or Publish.");
        if (string.IsNullOrWhiteSpace(input.Task) || input.Task.Length > 8000) throw new ArgumentException("Describe the task in up to 8000 characters.");
        if (input.EditionId is not null && surface is not ("Editor" or "Images" or "Publish"))
            throw new ExternalAccessException("CORE_OWNER_REQUIRED", "Open this surface on Core; only Editor, Images and Publish use a release target.");
        _ = await Service<IProjectService>().GetByIdAsync(input.ProjectId, ct) ?? throw new KeyNotFoundException("Project not found.");
        if (input.ChapterId is Guid chapterId)
        {
            var chapter = await Service<IChapterService>().GetAsync(chapterId, ct);
            if (chapter?.ProjectId != input.ProjectId) throw new KeyNotFoundException("Chapter not found in this project.");
        }
        if (input.EditionId is Guid editionId)
            _ = (await Service<Lorekeeper.Publish.IPublicationEditionService>().ListAsync(input.ProjectId, ct))
                .SingleOrDefault(value => value.Id == editionId) ?? throw new KeyNotFoundException("Release not found in this project.");
        await using (var write = await database.OpenWriteAsync(input.ProjectId, ct))
        {
            Work = input.WorkId is Guid workId
                ? await write.Db.ExternalWorks.SingleOrDefaultAsync(value => value.Id == workId && value.ClientGrantId == Client.Id && value.ProjectId == input.ProjectId && value.Status == "Active", ct)
                    ?? throw new ExternalAccessException("WORK_NOT_ACTIVE", "This work is not active for this client and project.")
                : new ExternalWork { ClientGrantId = Client.Id, ProjectId = input.ProjectId, Task = input.Task.Trim() };
            if (input.WorkId is null) write.Db.ExternalWorks.Add(Work);
            Work.Task = input.Task.Trim();
            Work.UpdatedAt = DateTime.UtcNow;
            Workspace = new ExternalWorkspace { WorkId = Work.Id, Surface = surface, ChapterId = input.ChapterId, EditionId = input.EditionId };
            write.Db.ExternalWorkspaces.Add(Workspace);
            await write.SaveChangesAsync(ct);
        }
        return await ContextAsync(new(input.ProjectId, Workspace.Id, input.PageNumber), ct);
    }

    internal Task<object> ContextAsync(WorkspaceContextRequest input, CancellationToken ct) =>
        Service<IAuthoringMutationFence>().ExecuteAsync(new(input.ProjectId, [], "refresh external workspace context"),
            (_, token) => ContextCoreAsync(input, token), ct);

    private async Task<object> ContextCoreAsync(WorkspaceContextRequest input, CancellationToken ct)
    {
        await InitializeAsync(JsonSerializer.SerializeToElement(input, ExternalToolCatalog.Json),
            ExternalToolCatalog.Tools.Single(value => value.Tool.Name == "get_workspace_context"), ct);
        RequireActiveWork();
        var project = await Service<IProjectService>().GetByIdAsync(input.ProjectId, ct) ?? throw new KeyNotFoundException("Project not found.");
        var workspace = Workspace ?? throw new ExternalAccessException("ONBOARDING_REQUIRED", "Call open_workspace first.");
        var work = Work!;
        Chapter? chapter = workspace.ChapterId is Guid id ? await Service<IChapterService>().GetAsync(id, ct) : null;
        IReadOnlyList<ContextItem> items;
        if (workspace.Surface == "Outline")
        {
            var outline = await Service<IOutlineWorkingContextBuilder>().BuildAsync(input.ProjectId, ct);
            var prompt = Service<ISystemPromptComposer>().Compose(new(project,
                await Service<IBookBriefService>().GetOrCreateAsync(input.ProjectId, ct), SystemPromptAgentRole.Outline,
                "Work through the advertised Lorekeeper tools. Discover exact IDs, preserve authored content, and finish the external task explicitly.", WorkingContext: outline, ExternalWorkspace: true));
            items = prompt.Sections.Select(ContextBuilder.ToContextItem).ToArray();
        }
        else
        {
            var purpose = Enum.Parse<ContextBuildPurpose>(workspace.Surface);
            items = (await Service<IContextBuilder>().BuildAsync(new(project, chapter, work.Task, purpose,
                "You are operating Lorekeeper as an external agent. Use the advertised tools and this exact workspace target. Read current revisions before writes. Follow required manuscript readback and visual verification. Call finish_work only when the entire task is done.", Target, ExternalWorkspace: true), ct)).Items;
        }
        var permitted = items.Where(value => value.IsEnabled && ContextPermitted(value)).Select(value => new
        {
            value.Key, value.Label, value.Body, value.Origin, value.IsProtected,
        }).ToArray();
        var payload = JsonSerializer.SerializeToNode(new { workspaceId = workspace.Id, workId = work.Id, projectId = project.Id, workspace.Surface,
            workspace.ChapterId, workspace.EditionId, work.Task, project.ReviewEditsEnabled,
            binaryTransfer = ExternalClientSetupService.TransferInstructions(Client.Id),
            instructions = "Reacquire current context after reconnect/compaction. Search is discovery; use exact reads and revision-checked writes. Do not write raw files or SQLite. Workspaces never select the user's UI. Finish the whole task explicitly.",
            context = permitted,
            tools = ExternalToolCatalog.Tools.Where(value => ExternalCapabilities.Has(Client, value.Capabilities)
                && (value.Surfaces.Length == 0 || value.Surfaces.Contains(workspace.Surface))).Select(value => value.Tool.Name),
        }, ExternalToolCatalog.Json)!.AsObject();
        var version = ContentVersion.For(payload);
        var changed = input.PageNumber > 1 && input.ExpectedContextVersion is not null && input.ExpectedContextVersion != version;
        payload["contextVersion"] = version;
        payload["contextChanged"] = changed;
        return Page(payload, input with { ExpectedContextVersion = version, PageNumber = changed ? 1 : input.PageNumber }, "get_workspace_context");
    }

    private bool ContextPermitted(ContextItem item)
    {
        if (item.Key.StartsWith("project:", StringComparison.Ordinal)
            && (!Guid.TryParse(item.Key[8..].Split('|')[0], out var origin) || !ExternalCapabilities.AllowsProject(Client, origin))) return false;
        return item.Kind switch
    {
        ContextItemKind.SystemInstructions or ContextItemKind.AssistantWorkflow => true,
        ContextItemKind.DynamicGuidance => ExternalCapabilities.Has(Client, "outline.read", "manuscript.read", "composition.read"),
        ContextItemKind.ProjectGuidance or ContextItemKind.BookBrief or ContextItemKind.WorldBrief or ContextItemKind.WorldWorkingNotes or ContextItemKind.BookFormatGuidance => ExternalCapabilities.Has(Client, "direction.read"),
        ContextItemKind.CurrentChapter or ContextItemKind.ManuscriptStyles => ExternalCapabilities.Has(Client, "manuscript.read"),
        ContextItemKind.ProjectOutline => ExternalCapabilities.Has(Client, "outline.read", "canon.read", "direction.read", "sources.read"),
        ContextItemKind.ProjectFacts or ContextItemKind.EntityInventory => ExternalCapabilities.Has(Client, "canon.read"),
        ContextItemKind.Entity => ExternalCapabilities.Has(Client, "canon.read", "voice.read", "images.read", "sources.read"),
        ContextItemKind.ManuscriptAnnotations or ContextItemKind.PendingReviewChanges => ExternalCapabilities.Has(Client, "review.read", "manuscript.read", "canon.read", "direction.read", "images.read", "publication.read", "composition.read", "sources.read", "voice.read", "outline.read"),
        ContextItemKind.WritingSample => ExternalCapabilities.Has(Client, "voice.read"),
        ContextItemKind.ProjectImage or ContextItemKind.EntityVisualReferences => ExternalCapabilities.Has(Client, "images.read", "canon.read"),
        ContextItemKind.ChapterVisualLayout or ContextItemKind.ProjectPageSetup => ExternalCapabilities.Has(Client, "composition.read"),
        ContextItemKind.ProjectReferences => Client.ProjectIdsJson is null && ExternalCapabilities.Has(Client, "sources.read", "direction.read"),
        ContextItemKind.ChapterReference => Client.ProjectIdsJson is null && ExternalCapabilities.Has(Client, "manuscript.read", "sources.read"),
        ContextItemKind.ActReference => Client.ProjectIdsJson is null && ExternalCapabilities.Has(Client, "outline.read", "sources.read"),
        _ => ExternalCapabilities.Has(Client, "sources.read", "manuscript.read", "canon.read", "direction.read", "composition.read", "voice.read") && Client.ProjectIdsJson is null,
    };
    }

    internal async Task<object> ListWorkAsync(DiscoveryRequest input, CancellationToken ct)
    {
        await using var read = await database.OpenReadAsync(ct);
        var works = await read.Db.ExternalWorks.Where(value => value.ClientGrantId == Client.Id && (value.Status == "Active" || value.Status == "Finishing")).ToArrayAsync(ct);
        var ids = works.Where(value => ExternalCapabilities.AllowsProject(Client, value.ProjectId)).Select(value => value.Id).ToArray();
        return Page(new { works = works.Where(value => ids.Contains(value.Id)), workspaces = await read.Db.ExternalWorkspaces.Where(value => ids.Contains(value.WorkId)).ToArrayAsync(ct) }, input, "list_unfinished_work");
    }

    internal async Task<object> FinishWorkAsync(FinishWorkRequest input, CancellationToken ct)
    {
        ExternalWork work;
        await using (var read = await database.OpenReadAsync(ct))
            work = await read.Db.ExternalWorks.SingleOrDefaultAsync(value => value.Id == input.WorkId && value.ClientGrantId == Client.Id && value.ProjectId == input.ProjectId, ct)
                ?? throw new KeyNotFoundException("Work not found.");
        if (work.Status == "Completed") return new { work.Id, work.Status };
        if (work.Status is not ("Active" or "Finishing")) throw new ExternalAccessException("WORK_NOT_ACTIVE", "This task has been abandoned.");
        await using (var read = await database.OpenReadAsync(ct))
        {
            if (await read.Db.ProjectImageGenerationJobs.AnyAsync(value => value.ProjectId == input.ProjectId
                && (value.Status == ProjectImageGenerationJobStatus.Queued || value.Status == ProjectImageGenerationJobStatus.Running), ct)
                || await read.Db.PublicationPreparationJobs.AnyAsync(value => value.ProjectId == input.ProjectId
                    && (value.Status == Lorekeeper.Models.PublicationPreparationStatus.Queued || value.Status == Lorekeeper.Models.PublicationPreparationStatus.Preparing), ct))
                throw new ExternalAccessException("JOBS_ACTIVE", "Project image or publication work is still running. Wait for completion or cancel it, inspect its output, then finish the task.");
        }
        var project = await Service<IProjectService>().GetByIdAsync(input.ProjectId, ct) ?? throw new KeyNotFoundException("Project not found.");
        // Persist the completion intent before checkpoint I/O. A lost response or restart cannot
        // let more task writes race an already captured, idempotently keyed checkpoint.
        await SetWorkStatusAsync(work.Id, "Finishing", ct);
        if (work.HasMutations && !project.ReviewEditsEnabled)
        {
            await Service<IProjectVersionHistoryService>().CreateCheckpointAsync(input.ProjectId,
                ProjectVersionCheckpointKind.Assistant, $"External client {Client.DisplayName}: {input.Summary}",
                requestKey: $"external-work:{work.Id:N}", cancellationToken: ct);
        }
        await SetWorkStatusAsync(work.Id, "Completed", ct);
        return new { work.Id, status = "Completed", checkpointed = work.HasMutations && !project.ReviewEditsEnabled, pendingReview = work.HasMutations && project.ReviewEditsEnabled };
    }

    internal async Task<object> AbandonWorkAsync(FinishWorkRequest input, CancellationToken ct)
    {
        await using var read = await database.OpenReadAsync(ct);
        var work = await read.Db.ExternalWorks.SingleOrDefaultAsync(value => value.Id == input.WorkId && value.ProjectId == input.ProjectId && value.ClientGrantId == Client.Id, ct)
            ?? throw new KeyNotFoundException("Work not found.");
        if (work.Status is "Active" or "Finishing") await SetWorkStatusAsync(work.Id, "Abandoned", ct);
        return new { work.Id, status = work.Status == "Completed" ? "Completed" : "Abandoned", savedWorkRetained = true };
    }

    private async Task SetWorkStatusAsync(Guid id, string status, CancellationToken ct)
    {
        await using var write = await database.OpenWriteAsync(ct);
        var work = await write.Db.ExternalWorks.SingleAsync(value => value.Id == id, ct);
        work.Status = status;
        work.UpdatedAt = DateTime.UtcNow;
        await write.SaveChangesAsync(ct);
    }

    private static JsonElement Page(object? result, object input, string tool)
    {
        var arguments = JsonSerializer.SerializeToNode(input, ExternalToolCatalog.Json)!.AsObject();
        var page = arguments["pageNumber"]?.GetValue<int>() ?? 1;
        return JsonSerializer.Deserialize<JsonElement>(AgentPayloadPaginator.SerializePage(
            new JsonObject { ["projectId"] = arguments["projectId"]?.DeepClone(), ["workspaceId"] = arguments["workspaceId"]?.DeepClone() },
            JsonSerializer.SerializeToNode(result, ExternalToolCatalog.Json), tool, arguments, page));
    }
}
