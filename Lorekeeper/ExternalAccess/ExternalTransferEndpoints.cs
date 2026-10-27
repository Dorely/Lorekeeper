using System.Security.Cryptography;
using System.Text.Json;
using Lorekeeper.Images;
using Lorekeeper.Persistence;
using Lorekeeper.Publish;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Lorekeeper.ExternalAccess;

internal static class ExternalTransferEndpoints
{
    public static void MapExternalTransfers(this WebApplication app)
    {
        var transfers = app.MapGroup("/external-access/transfers").AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context);
            if (result is not FileContentHttpResult file) return result;
            var services = context.HttpContext.RequestServices;
            if (file.FileContents.Length > services.GetRequiredService<IOptions<ExternalAccessOptions>>().Value.MaximumDownloadBytes)
                return Results.Json(new { code = "DOWNLOAD_LIMIT", message = "This file exceeds the configured local transfer limit." }, statusCode: 413);
            var permission = context.HttpContext.GetEndpoint()!.Metadata.GetMetadata<BinaryPermission>()!;
            var query = context.HttpContext.Request.Query;
            var commands = services.GetRequiredService<ExternalAccessCommands>();
            await commands.InitializeAsync(JsonSerializer.SerializeToElement(new
            { projectId = Guid.Parse(query["projectId"].ToString()), workspaceId = Guid.Parse(query["workspaceId"].ToString()) }),
                ExternalToolCatalog.Tools.Single(value => value.Tool.Name == permission.ToolName), context.HttpContext.RequestAborted);
            commands.RequireActiveWork();
            return result;
        });
        transfers.MapGet("/previews/cover", async (Guid projectId, Guid workspaceId, string surfaceRole,
            bool annotated, string sha256, ExternalAccessCommands commands, CancellationToken ct) =>
        {
            await commands.InitializeAsync(JsonSerializer.SerializeToElement(new { projectId, workspaceId }),
                ExternalToolCatalog.Tools.Single(value => value.Tool.Name == "preview_publication_cover"), ct);
            commands.RequireActiveWork();
            var preview = (ExternalImageResult)await commands.PreviewCoverAsync(new(projectId, workspaceId, surfaceRole, annotated), ct);
            return VerifiedPreview(preview, sha256);
        }).WithMetadata(new BinaryPermission("preview_publication_cover"));
        transfers.MapGet("/previews/page", async (Guid projectId, Guid workspaceId, Guid contentId, Guid variantId,
            bool annotated, string sha256, ExternalAccessCommands commands, CancellationToken ct) =>
        {
            await commands.InitializeAsync(JsonSerializer.SerializeToElement(new { projectId, workspaceId }),
                ExternalToolCatalog.Tools.Single(value => value.Tool.Name == "preview_designed_page"), ct);
            commands.RequireActiveWork();
            var preview = (ExternalImageResult)await commands.PreviewPageAsync(new(projectId, workspaceId, contentId, variantId, annotated), ct);
            return VerifiedPreview(preview, sha256);
        }).WithMetadata(new BinaryPermission("preview_designed_page"));
        transfers.MapGet("/exports/{format}", async (string format, Guid projectId, Guid workspaceId,
            ExternalAccessCommands commands, IPublishService publications, CancellationToken ct) =>
        {
            var tool = ExternalToolCatalog.Tools.Single(value => value.Tool.Name == "read_publication_document");
            await commands.InitializeAsync(JsonSerializer.SerializeToElement(new { projectId, workspaceId }), tool, ct);
            commands.RequireActiveWork();
            if (!Enum.TryParse<PublishExportFormat>(format, out var parsed) || !Enum.IsDefined(parsed)) return Results.BadRequest();
            var file = commands.Workspace!.EditionId is Guid edition
                ? await publications.ExportAsync(projectId, edition, parsed, ct) : await publications.ExportCoreAsync(projectId, parsed, ct);
            return Results.File(file.Content, file.ContentType, file.FileName);
        }).WithMetadata(new BinaryPermission("read_publication_document"));
        transfers.MapGet("/artifacts/{id:guid}", async (Guid id, Guid projectId, Guid workspaceId,
            ExternalAccessCommands commands, CancellationToken ct) =>
        {
            var tool = ExternalToolCatalog.Tools.Single(value => value.Tool.Name == "list_publication_artifacts");
            await commands.InitializeAsync(JsonSerializer.SerializeToElement(new { projectId, workspaceId }), tool, ct);
            commands.RequireActiveWork();
            var artifact = await commands.RequireArtifactAsync(projectId, id, ct);
            return Results.File(artifact.Data, artifact.MediaType, artifact.FileName);
        }).WithMetadata(new BinaryPermission("list_publication_artifacts"));
        transfers.MapPut("/uploads/{id:guid}", async (Guid id, HttpContext http,
            McpClientGrantService grants, ExternalTransferStore store, IAppDatabaseOperationFactory database,
            IOptions<ExternalAccessOptions> options, ExternalAccessCommands commands, CancellationToken ct) =>
        {
            var client = await grants.RequireAsync(ExternalAccessHttpGate.ClientId(http), ct);
            ExternalAssetTransfer transfer;
            await using (var read = await database.OpenReadAsync(ct))
                transfer = await read.Db.ExternalAssetTransfers.SingleOrDefaultAsync(value => value.Id == id && value.ClientGrantId == client.Id, ct)
                    ?? throw new BadHttpRequestException("Upload not found.", 404);
            if (!ExternalCapabilities.Has(client, "images.write") || !ExternalCapabilities.AllowsProject(client, transfer.ProjectId)) return Results.StatusCode(403);
            var tool = ExternalToolCatalog.Tools.Single(value => value.Tool.Name == "begin_image_upload");
            var identity = JsonSerializer.SerializeToElement(new { projectId = transfer.ProjectId, workspaceId = transfer.WorkspaceId });
            await commands.InitializeAsync(identity, tool, ct);
            commands.RequireActiveWork();
            if (transfer.ImportedImageId is not null) return Results.Conflict(new { code = "UPLOAD_ALREADY_IMPORTED" });
            var temporary = store.UploadPath(Guid.NewGuid());
            try
            {
                long length = 0;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    var buffer = new byte[65536];
                    int count;
                    while ((count = await http.Request.Body.ReadAsync(buffer, ct)) > 0)
                    {
                        length += count;
                        if (length > options.Value.MaximumTransferBytes) return Results.StatusCode(413);
                        hash.AppendData(buffer, 0, count);
                        await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    }
                }
                if (length == 0) return Results.BadRequest(new { code = "EMPTY_UPLOAD" });
                var sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
                await using var write = await database.OpenWriteAsync(transfer.ProjectId, ct);
                var currentClient = await grants.RequireAsync(client.Id, ct);
                if (!ExternalCapabilities.Has(currentClient, "images.write") || !ExternalCapabilities.AllowsProject(currentClient, transfer.ProjectId)) return Results.StatusCode(403);
                await commands.InitializeAsync(identity, tool, ct);
                commands.RequireActiveWork();
                var current = await write.Db.ExternalAssetTransfers.SingleAsync(value => value.Id == id && value.ClientGrantId == client.Id, ct);
                if (current.ImportedImageId is not null) return Results.Conflict(new { code = "UPLOAD_ALREADY_IMPORTED" });
                if (current.Sha256 is not null && current.Sha256 != sha256) return Results.Conflict(new { code = "UPLOAD_ID_REUSED" });
                File.Move(temporary, store.UploadPath(id), overwrite: true);
                current.Length = length;
                current.Sha256 = sha256;
                await write.SaveChangesAsync(ct);
                return Results.Ok(new { transferId = id, length, sha256 });
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        });

        transfers.MapGet("/images/{id:guid}", async (Guid id, Guid projectId, Guid workspaceId,
            HttpContext http, ExternalAccessCommands commands, IProjectImageService images, CancellationToken ct) =>
        {
            var tool = ExternalToolCatalog.Tools.Single(value => value.Tool.Name == "read_project_image");
            await commands.InitializeAsync(JsonSerializer.SerializeToElement(new { projectId, workspaceId }), tool, ct);
            commands.RequireActiveWork();
            var image = await images.GetDataAsync(projectId, id, cancellationToken: ct);
            return image is null ? Results.NotFound() : Results.File(image.Data, image.ContentType, image.FileName);
        }).WithMetadata(new BinaryPermission("read_project_image"));

        transfers.MapGet("/captures/{id:guid}/files/{index:int}", async (Guid id, int index,
            Guid projectId, Guid workspaceId, ExternalAccessCommands commands, ExternalTransferStore store, CancellationToken ct) =>
        {
            var tool = ExternalToolCatalog.Tools.Single(value => value.Tool.Name == "read_transfer_file");
            await commands.InitializeAsync(JsonSerializer.SerializeToElement(new { projectId, workspaceId }), tool, ct);
            commands.RequireActiveWork();
            var capture = await store.RequireCaptureAsync(projectId, id, ct);
            var bytes = await store.ReadAsync(capture, index, ct);
            var descriptor = store.Manifest(capture).Files[index];
            return Results.File(bytes, "application/octet-stream", Path.GetFileName(descriptor.Path));
        }).WithMetadata(new BinaryPermission("read_transfer_file"));
    }

    private sealed record BinaryPermission(string ToolName);

    private static IResult VerifiedPreview(ExternalImageResult preview, string sha256) =>
        Convert.ToHexStringLower(SHA256.HashData(preview.Data)) == sha256
            ? Results.File(preview.Data, preview.MimeType, "lorekeeper-preview.png")
            : Results.Conflict(new { code = "PREVIEW_CHANGED", message = "Read a fresh visual preview; the saved composition changed." });
}
