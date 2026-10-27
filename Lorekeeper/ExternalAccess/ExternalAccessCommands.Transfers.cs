using System.Security.Cryptography;
using System.Text.Json;
using Lorekeeper.Chapters;
using Lorekeeper.Images;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.ExternalAccess;

internal sealed partial class ExternalAccessCommands
{
    internal async Task<object> CreateExternalLinkAsync(ExternalLinkWriteRequest input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.ExternalKey) || input.ExternalKey.Length > 512 || string.IsNullOrWhiteSpace(input.Name))
            throw new ArgumentException("Provide an external source key and a display name.");
        await using var write = await database.OpenWriteAsync(input.ProjectId, ct);
        var link = await write.Db.ExternalProjectLinks.SingleOrDefaultAsync(value => value.ProjectId == input.ProjectId && value.ExternalKey == input.ExternalKey, ct);
        if (link is null)
        {
            link = new ExternalProjectLink { ProjectId = input.ProjectId, ExternalKey = input.ExternalKey, Name = input.Name };
            write.Db.ExternalProjectLinks.Add(link);
            await write.SaveChangesAsync(ct);
        }
        return link;
    }

    internal async Task<object> ListExternalLinksAsync(WorkspaceRequest input, CancellationToken ct)
    {
        await using var read = await database.OpenReadAsync(ct);
        return Page(await read.Db.ExternalProjectLinks.Where(value => value.ProjectId == input.ProjectId).OrderBy(value => value.Name).ToArrayAsync(ct), input, "list_external_project_links");
    }

    internal async Task<object> SaveExternalIdentityAsync(ExternalIdentityWriteRequest input, CancellationToken ct)
    {
        _ = await Service<ExternalTransferStore>().RequireLinkAsync(input.ProjectId, input.LinkId, ct);
        if (string.IsNullOrWhiteSpace(input.ExternalKey) || input.ExternalKey.Length > 1024) throw new ArgumentException("Provide a stable external source key.");
        var exists = input.TargetKind switch
        {
            "Entity" => ExternalCapabilities.Has(Client, "canon.read") && await Service<IEntityService>().GetAsync(input.ProjectId, input.TargetId, ct) is not null,
            "Chapter" => ExternalCapabilities.Has(Client, "outline.read") && (await Service<IChapterService>().GetAsync(input.TargetId, ct))?.ProjectId == input.ProjectId,
            "Act" => ExternalCapabilities.Has(Client, "outline.read") && (await Service<IActService>().GetAsync(input.TargetId, ct))?.ProjectId == input.ProjectId,
            "Image" => ExternalCapabilities.Has(Client, "images.read") && await Service<IProjectImageService>().GetAsync(input.ProjectId, input.TargetId, ct) is not null,
            "Project" => input.TargetId == input.ProjectId,
            _ => false,
        };
        if (!exists) throw new KeyNotFoundException("The requested target kind/identity is unavailable in this project.");
        await using var write = await database.OpenWriteAsync(input.ProjectId, ct);
        var identity = await write.Db.ExternalSourceIdentities.SingleOrDefaultAsync(value => value.LinkId == input.LinkId && value.ExternalKey == input.ExternalKey, ct);
        if (identity is not null)
        {
            if (input.ExpectedVersion is null) throw new ContentVersionConflictException(ContentVersion.For(identity));
            ContentVersion.Require(input.ExpectedVersion, identity);
            identity.TargetKind = input.TargetKind;
            identity.TargetId = input.TargetId.ToString("N");
        }
        else write.Db.ExternalSourceIdentities.Add(identity = new ExternalSourceIdentity
        {
            LinkId = input.LinkId, ExternalKey = input.ExternalKey, TargetKind = input.TargetKind, TargetId = input.TargetId.ToString("N"),
        });
        await write.SaveChangesAsync(ct);
        return new { identity, version = ContentVersion.For(identity) };
    }

    internal async Task<object> CaptureTransferAsync(ExternalLinkRequest input, CancellationToken ct)
    {
        var store = Service<ExternalTransferStore>();
        var link = await store.RequireLinkAsync(input.ProjectId, input.LinkId, ct);
        var capture = await store.CaptureAsync(link, ct);
        return new { captureId = capture.Id, linkId = link.Id, capture.PreviousBaselineId, capture.ExpectedLinkRevision,
            source = "current-saved-state", includesPendingEdits = true, checkpointCreated = false,
            next = new { tool = "read_transfer_changes", projectId = input.ProjectId, workspaceId = input.WorkspaceId, captureId = capture.Id } };
    }

    internal async Task<object> ReadTransferChangesAsync(TransferReadRequest input, CancellationToken ct)
    {
        var store = Service<ExternalTransferStore>();
        var capture = await store.RequireCaptureAsync(input.ProjectId, input.CaptureId, ct);
        var current = store.Manifest(capture);
        var previous = capture.PreviousBaselineId is Guid previousId ? await store.RequireCaptureAsync(input.ProjectId, previousId, ct) : null;
        var before = previous is null ? [] : store.Manifest(previous).Files;
        var paths = before.Select(value => value.Path).Concat(current.Files.Select(value => value.Path)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var changes = paths.Select(path =>
        {
            var leftIndex = before.ToList().FindIndex(value => value.Path == path);
            var rightIndex = current.Files.ToList().FindIndex(value => value.Path == path);
            var left = leftIndex >= 0 ? before[leftIndex] : null;
            var right = rightIndex >= 0 ? current.Files[rightIndex] : null;
            return new { path, kind = left is null ? "Added" : right is null ? "Removed" : "Modified",
                changed = left?.Sha256 != right?.Sha256,
                before = left is null ? null : new { captureId = previous!.Id, fileIndex = leftIndex, left.Sha256, left.Length },
                after = right is null ? null : new { captureId = capture.Id, fileIndex = rightIndex, right.Sha256, right.Length } };
        }).Where(value => value.changed).ToArray();
        var identities = JsonSerializer.Deserialize<ExternalSourceIdentity[]>(capture.IdentitiesJson, ExternalToolCatalog.Json) ?? [];
        return Page(new { capture.Id, capture.PreviousBaselineId, current.ContentHash, completeInventory = true, changes,
            recordChanges = previous is null ? null : store.Compare(previous, capture, ct),
            identities = identities.Select(value => new { identity = value, version = ContentVersion.For(value) }) }, input, "read_transfer_changes");
    }

    internal async Task<object> ReadTransferFileAsync(TransferFileRequest input, CancellationToken ct)
    {
        var store = Service<ExternalTransferStore>();
        var capture = await store.RequireCaptureAsync(input.ProjectId, input.CaptureId, ct);
        var files = store.Manifest(capture).Files;
        if (input.FileIndex < 0 || input.FileIndex >= files.Count) throw new KeyNotFoundException("Captured file not found.");
        var descriptor = files[input.FileIndex];
        if (!descriptor.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return new
        {
            descriptor.Length, descriptor.Sha256,
            resource = $"/external-access/transfers/captures/{capture.Id:N}/files/{input.FileIndex}?projectId={input.ProjectId:N}&workspaceId={input.WorkspaceId:N}",
        };
        var bytes = await store.ReadAsync(capture, input.FileIndex, ct);
        return Page(new { descriptor.Path, descriptor.Sha256, descriptor.Length, content = JsonSerializer.Deserialize<JsonElement>(bytes) }, input, "read_transfer_file");
    }

    internal async Task<object> AcknowledgeTransferAsync(TransferAcknowledgeRequest input, CancellationToken ct)
    {
        var capture = await Service<ExternalTransferStore>().RequireCaptureAsync(input.ProjectId, input.CaptureId, ct);
        await using var write = await database.OpenWriteAsync(input.ProjectId, ct);
        var link = await write.Db.ExternalProjectLinks.SingleAsync(value => value.Id == capture.LinkId && value.ProjectId == input.ProjectId, ct);
        if (link.BaselineCaptureId == capture.Id) return new { acknowledged = true, captureId = capture.Id, link.Revision };
        if (link.Revision != capture.ExpectedLinkRevision || link.BaselineCaptureId != capture.PreviousBaselineId)
            throw new ExternalAccessException("BASELINE_CHANGED", "A later transfer has already advanced this integration. Capture a fresh comparison; its current baseline is preserved.");
        link.BaselineCaptureId = capture.Id;
        link.Revision++;
        var tracked = await write.Db.ExternalTransferCaptures.SingleAsync(value => value.Id == capture.Id, ct);
        tracked.AcknowledgedAt = DateTime.UtcNow;
        await write.SaveChangesAsync(ct);
        return new { acknowledged = true, captureId = capture.Id, link.Revision, newerLiveEditsRemainOutstanding = true };
    }

    internal async Task<object> BeginImageUploadAsync(BeginImageUploadRequest input, CancellationToken ct)
    {
        if (Path.GetFileName(input.FileName) != input.FileName || input.FileName.Length is < 1 or > 200
            || input.FileName.Contains('\\') || input.FileName.Contains('/')
            || input.ContentType is not ("image/png" or "image/jpeg" or "image/webp"))
            throw new ArgumentException("Supply a plain filename and PNG, JPEG or WebP content type.");
        await using var write = await database.OpenWriteAsync(input.ProjectId, ct);
        var transfer = new ExternalAssetTransfer { ProjectId = input.ProjectId, WorkspaceId = input.WorkspaceId, ClientGrantId = Client.Id,
            FileName = input.FileName, ContentType = input.ContentType };
        write.Db.ExternalAssetTransfers.Add(transfer);
        await write.SaveChangesAsync(ct);
        return new { transferId = transfer.Id, uploadResource = $"/external-access/transfers/uploads/{transfer.Id:N}" };
    }

    internal async Task<object> CommitImageUploadAsync(CommitImageUploadRequest input, CancellationToken ct)
    {
        await using var write = await database.OpenWriteAsync(input.ProjectId, ct);
        var transfer = await write.Db.ExternalAssetTransfers.SingleOrDefaultAsync(value => value.Id == input.TransferId
            && value.ProjectId == input.ProjectId && value.ClientGrantId == Client.Id && value.WorkspaceId == input.WorkspaceId, ct)
            ?? throw new KeyNotFoundException("Image upload not found.");
        if (transfer.ImportedImageId is Guid existing) return new { imageId = existing, reused = true };
        if (transfer.Sha256 is null) throw new ExternalAccessException("UPLOAD_REQUIRED", "Send the original image bytes with the transfer helper, then retry this commit.");
        var bytes = await File.ReadAllBytesAsync(Service<ExternalTransferStore>().UploadPath(transfer.Id), ct);
        if (bytes.LongLength != transfer.Length || Convert.ToHexStringLower(SHA256.HashData(bytes)) != transfer.Sha256)
            throw new InvalidDataException("Upload integrity verification failed.");
        var image = await Service<IProjectImageService>().UploadAsync(input.ProjectId, new(transfer.FileName, transfer.ContentType, bytes, input.AltText), ct);
        transfer.ImportedImageId = image.Id;
        await write.SaveChangesAsync(ct);
        return new { imageId = image.Id, image.Width, image.Height, unattached = true };
    }
}
