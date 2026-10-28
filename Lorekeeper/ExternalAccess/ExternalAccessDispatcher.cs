using System.Text.Json;
using System.Text.Json.Nodes;
using Lorekeeper.Authoring;
using Lorekeeper.Context;
using Lorekeeper.Persistence;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Composition;
using Lorekeeper.Images;
using System.Security.Cryptography;
using Lorekeeper.VersionHistory.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace Lorekeeper.ExternalAccess;

internal sealed class ExternalAccessDispatcher(ExternalAccessCommands commands, McpClientGrantService grants,
    IAppDatabaseOperationFactory database, IAuthoringMutationFence fence, IVectorIndexWorkCoordinator indexWork,
    IAuthoringMutationContextAccessor actor, ProjectVersionHistoryUiEvents events,
    IHttpContextAccessor http, IOptions<ExternalAccessOptions> options, ILogger<ExternalAccessDispatcher> logger,
    ExternalWorkspaceUiEvents workspaceEvents)
{
    public async Task<ListToolsResult> ListAsync(CancellationToken ct)
    {
        var grant = await grants.RequireAsync(ExternalAccessHttpGate.ClientId(http.HttpContext!), ct);
        return new ListToolsResult { Tools = ExternalToolCatalog.Tools.Where(value => ExternalCapabilities.Has(grant, value.Capabilities)).Select(value => value.Tool).ToList() };
    }

    public async Task<CallToolResult> CallAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        try
        {
            var definition = ExternalToolCatalog.Tools.SingleOrDefault(value => value.Tool.Name == name)
                ?? throw new ExternalAccessException("OPERATION_UNAVAILABLE", "This operation is unavailable. Refresh tool discovery.");
            await commands.InitializeAsync(arguments, definition, ct);
            object result;
            if (definition.Mutates)
            {
                if (!arguments.TryGetProperty("mutationId", out var mutation) || !mutation.TryGetGuid(out var mutationId) || mutationId == Guid.Empty)
                    throw new ArgumentException("A nonempty mutationId is required; reuse it after an uncertain response.");
                var requestHash = McpClientGrantService.Hash(name + "\n" + Canonical(arguments));
                await using (var read = await database.OpenReadAsync(ct))
                {
                    var receipt = await read.Db.ExternalMutationReceipts.SingleOrDefaultAsync(value => value.ClientGrantId == commands.Client.Id && value.MutationId == mutationId, ct);
                    if (receipt is not null)
                    {
                        commands.RequireReceiptPermission(receipt);
                        if (receipt.RequestHash != requestHash) throw new ExternalAccessException("MUTATION_ID_REUSED", "Use the original payload for this mutationId.");
                        return new CallToolResult { Content = [new TextContentBlock { Text = receipt.ResultJson }] };
                    }
                }
                commands.RequireActiveWork();
                await commands.PrepareMutationAsync(name, arguments, ct);
                var projectId = arguments.TryGetProperty("projectId", out var project) ? project.GetGuid() : (Guid?)null;
                async Task<object> Apply(CancellationToken token)
                {
                    await using var deferral = indexWork.BeginDeferral();
                    using var effects = MutationCommitEffects.Begin();
                    object saved;
                    await using (var operation = projectId is Guid id ? await database.OpenWriteAsync(id, token) : await database.OpenWriteAsync(token))
                    {
                        operation.ShareWithNestedOperations();
                        var grant = await grants.RequireAsync(commands.Client.Id, token);
                        if (!ExternalCapabilities.Has(grant, definition.Capabilities)
                            || projectId is Guid requestedProject && !ExternalCapabilities.AllowsProject(grant, requestedProject))
                            throw new ExternalAccessException("ACCESS_DENIED", "The connection's permissions changed. Refresh discovery.");
                        var previous = await operation.Db.ExternalMutationReceipts.SingleOrDefaultAsync(value => value.ClientGrantId == grant.Id && value.MutationId == mutationId, token);
                        if (previous is not null)
                        {
                            commands.RequireReceiptPermission(previous);
                            if (previous.RequestHash != requestHash) throw new ExternalAccessException("MUTATION_ID_REUSED", "This mutationId already belongs to a different request. Use the original payload or a new identity.");
                            return JsonSerializer.Deserialize<JsonElement>(previous.ResultJson);
                        }
                        await commands.InitializeAsync(arguments, definition, token);
                        commands.RequireActiveWork();
                        await using var transaction = await operation.Db.Database.BeginTransactionAsync(token);
                        using var actorScope = actor.BeginExternalWork(grant.Id, commands.Work?.Id, grant.DisplayName);
                        saved = new { mutationId, committed = true, repairStatus = "pending", result = await definition.Invoke(commands, arguments, token) };
                        var resultJson = JsonSerializer.Serialize(saved, ExternalToolCatalog.Json);
                        if (System.Text.Encoding.UTF8.GetByteCount(resultJson) > options.Value.MaximumResultBytes)
                            throw new ExternalAccessException("RESULT_REQUIRES_PAGING", "This mutation would return too much data. Use a smaller batch.");
                        operation.Db.ExternalMutationReceipts.Add(new ExternalMutationReceipt
                        {
                            ClientGrantId = grant.Id, WorkId = commands.Work?.Id,
                            ProjectId = projectId ?? (name == "create_project" ? JsonSerializer.SerializeToElement(saved, ExternalToolCatalog.Json).GetProperty("result").GetProperty("id").GetGuid() : null),
                            MutationId = mutationId, Operation = name, RequestHash = requestHash,
                            ResultJson = resultJson,
                            RepairTargetsJson = JsonSerializer.Serialize(deferral.PendingTargets, ExternalToolCatalog.Json),
                        });
                        if (commands.Work is not null && !definition.Capabilities.Contains("transfer.write") && name != "begin_image_upload")
                        {
                            var work = await operation.Db.ExternalWorks.SingleAsync(value => value.Id == commands.Work.Id, token);
                            work.HasMutations = true;
                            work.UpdatedAt = DateTime.UtcNow;
                        }
                        await operation.SaveChangesAsync(token);
                        await transaction.CommitAsync(token);
                    }
                    try
                    {
                        await effects.CompleteAsync(CancellationToken.None);
                        await deferral.FlushAsync(CancellationToken.None);
                        if (deferral.HasFailures) throw new InvalidOperationException("Derived projection repair remains pending.");
                        var repaired = JsonSerializer.SerializeToNode(saved, ExternalToolCatalog.Json)!.AsObject();
                        repaired["repairStatus"] = "complete";
                        await using var update = await database.OpenWriteAsync(CancellationToken.None);
                        var receipt = await update.Db.ExternalMutationReceipts.SingleAsync(value => value.ClientGrantId == commands.Client.Id && value.MutationId == mutationId);
                        receipt.ResultJson = repaired.ToJsonString(ExternalToolCatalog.Json);
                        receipt.RepairPending = false;
                        await update.SaveChangesAsync(CancellationToken.None);
                        saved = JsonSerializer.Deserialize<JsonElement>(receipt.ResultJson);
                    }
                    catch (Exception)
                    {
                        logger.LogWarning("External mutation committed; derived-state repair remains pending for {Operation}.", name);
                    }
                    if (projectId is Guid changedProject)
                    {
                        events.PublishReviewStateChanged(changedProject);
                        workspaceEvents.Publish(changedProject);
                    }
                    return saved;
                }
                result = projectId is Guid fencedProject
                    ? await fence.ExecuteAsync(new AuthoringFenceRequest(fencedProject, [], $"external client {name}"), (_, token) => Apply(token), ct)
                    : await Apply(ct);
            }
            else
            {
                commands.RequireActiveWork();
                if (name is "finish_work" or "abandon_work")
                {
                    var projectId = arguments.GetProperty("projectId").GetGuid();
                    result = await fence.ExecuteAsync(new(projectId, [], name), async (_, token) =>
                    {
                        await commands.InitializeAsync(arguments, definition, token);
                        return await definition.Invoke(commands, arguments, token);
                    }, ct);
                }
                else result = await definition.Invoke(commands, arguments, ct);
            }
            if (result is ExternalImageResult image)
            {
                var maximumImageBytes = (options.Value.MaximumResultBytes - 65536) * 3 / 4;
                var pixels = image.Data;
                foreach (var edge in new[] { 1200, 900, 640, 480, 320, 128 })
                {
                    if (pixels.Length <= maximumImageBytes) break;
                    pixels = ProjectImageResize.Resize(image.Data, image.MimeType, edge);
                }
                if (pixels.Length > maximumImageBytes)
                    throw new ExternalAccessException("PREVIEW_UNAVAILABLE", "The image could not be reduced to a bounded preview. Retrieve its original bytes with the binary transfer helper.");
                var metadata = JsonSerializer.SerializeToNode(image.Metadata, ExternalToolCatalog.Json)!.AsObject();
                metadata["delivery"] = JsonSerializer.SerializeToNode(new { previewScaled = pixels.Length != image.Data.Length,
                    renderedByteLength = image.Data.Length, renderedSha256 = Convert.ToHexStringLower(SHA256.HashData(image.Data)),
                    binaryResource = image.DownloadResource }, ExternalToolCatalog.Json);
                var description = metadata.ToJsonString(ExternalToolCatalog.Json);
                if (System.Text.Encoding.UTF8.GetByteCount(description) > 65536)
                    description = JsonSerializer.Serialize(new { metadataRequiresPaging = true,
                        guidance = "Read exact image metadata through list_project_images; this response supplies the bounded visual preview.",
                        delivery = metadata["delivery"] }, ExternalToolCatalog.Json);
                return new CallToolResult { Content = [new TextContentBlock { Text = description }, ImageContentBlock.FromBytes(pixels, image.MimeType)] };
            }
            var json = JsonSerializer.SerializeToElement(result, ExternalToolCatalog.Json);
            var text = json.GetRawText();
            if (System.Text.Encoding.UTF8.GetByteCount(text) > options.Value.MaximumResultBytes)
                throw new ExternalAccessException("RESULT_REQUIRES_PAGING", "Read this result through the paginated exact-read tool.");
            return new CallToolResult { Content = [new TextContentBlock { Text = text }] };
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException or InvalidDataException or JsonException or DbUpdateException)
        {
            var code = exception switch
            {
                ExternalAccessException access => access.Code,
                ContentVersionConflictException or ContentRevisionConflictException or ManuscriptRevisionConflictException
                    or DesignedPageRevisionConflictException or ManuscriptStyleConflictException => "REVISION_CONFLICT",
                JsonException or ArgumentException => "INVALID_ARGUMENTS",
                AuthoringMutationFenceException authoring => authoring.Code,
                _ => "REQUEST_NOT_APPLIED",
            };
            // Log only operation identity. Exception messages may contain paths, provider data, or authored text.
            logger.LogInformation("External operation {Operation} returned {Code}.", name, code);
            logger.LogDebug("External boundary diagnostic {ExceptionType}: {Stack}", exception.GetType().Name, exception.StackTrace);
            var safeMessage = exception is ExternalAccessException or CorrectableInputException or ContentVersionConflictException or ContentRevisionConflictException or ManuscriptRevisionConflictException or AuthoringMutationFenceException
                ? exception.Message : "The request could not be applied. Refresh the exact target and its revision, check the supplied fields, and reassess the operation.";
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { code, message = safeMessage,
                currentVersion = (exception as ContentVersionConflictException)?.CurrentVersion,
                currentRevision = exception switch
                {
                    ContentRevisionConflictException revision => (long?)revision.CurrentRevision,
                    ManuscriptRevisionConflictException revision => revision.ActualRevision,
                    DesignedPageRevisionConflictException revision => revision.ActualRevision,
                    ManuscriptStyleConflictException revision => revision.ActualRevision,
                    _ => null,
                }, committed = false }) }] };
        }
    }

    private static string Canonical(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => JsonSerializer.Serialize(item.Name) + ":" + Canonical(item.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(Canonical)) + "]",
        _ => value.GetRawText(),
    };
}
