using System.Security.Cryptography;
using System.Text.Json;
using Lorekeeper.Authoring;
using Lorekeeper.Persistence;
using Lorekeeper.VersionHistory.Snapshots;
using Lorekeeper.VersionHistory.Compare;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.ExternalAccess;

internal sealed class ExternalTransferStore(IConfiguration configuration, IAppDatabaseOperationFactory database,
    IVersionHistorySnapshotWriter snapshots, IVersionHistorySnapshotReader reader,
    IVersionHistorySnapshotComparer comparer, IAuthoringMutationFence fence)
{
    private string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lorekeeper", "ExternalAccess", McpClientGrantService.Hash(configuration.GetConnectionString("DefaultConnection") ?? "default")[..20]);

    internal string UploadPath(Guid id) => SafePath("uploads", id.ToString("N"));

    private string SafePath(params string[] parts)
    {
        var path = Path.GetFullPath(Path.Combine(new[] { Root }.Concat(parts).ToArray()));
        if (!path.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Transfer path is outside owned storage.");
        ExternalClientSetupService.EnsureSafeDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Transfer content is linked outside owned storage.");
        return path;
    }

    internal Task<ExternalTransferCapture> CaptureAsync(ExternalProjectLink link, CancellationToken ct) =>
        fence.ExecuteAsync(new AuthoringFenceRequest(link.ProjectId, [], "capture current saved state for external handoff"),
            (_, token) => CaptureUnderFenceAsync(link.ProjectId, link.Id, token), ct);

    private async Task<ExternalTransferCapture> CaptureUnderFenceAsync(Guid projectId, Guid linkId, CancellationToken ct)
    {
        var link = await RequireLinkAsync(projectId, linkId, ct);
        var captureId = Guid.NewGuid();
        var root = SafePath("captures", captureId.ToString("N"), "manifest.json");
        root = Path.GetDirectoryName(root)!;
        var artifact = await snapshots.WriteAsync(link.Id, link.ProjectId, root, ct);
        var capture = new ExternalTransferCapture
        {
            Id = captureId, LinkId = link.Id, PreviousBaselineId = link.BaselineCaptureId,
            ExpectedLinkRevision = link.Revision,
            ManifestJson = JsonSerializer.Serialize(artifact.Manifest, ExternalToolCatalog.Json),
        };
        await using var write = await database.OpenWriteAsync(link.ProjectId, ct);
        capture.IdentitiesJson = JsonSerializer.Serialize(await write.Db.ExternalSourceIdentities.AsNoTracking()
            .Where(value => value.LinkId == link.Id).OrderBy(value => value.ExternalKey).ToArrayAsync(ct), ExternalToolCatalog.Json);
        write.Db.ExternalTransferCaptures.Add(capture);
        await write.SaveChangesAsync(ct);
        return capture;
    }

    internal VersionHistorySnapshotManifest Manifest(ExternalTransferCapture capture) =>
        JsonSerializer.Deserialize<VersionHistorySnapshotManifest>(capture.ManifestJson, ExternalToolCatalog.Json)
        ?? throw new InvalidDataException("The transfer manifest is unavailable.");

    internal IReadOnlyList<VersionHistorySnapshotAreaComparison> Compare(ExternalTransferCapture before, ExternalTransferCapture after, CancellationToken ct)
    {
        VersionHistorySnapshotPayload Read(ExternalTransferCapture capture)
        {
            var manifest = Manifest(capture);
            var root = Path.GetDirectoryName(SafePath("captures", capture.Id.ToString("N"), "manifest.json"))!;
            return reader.Read(root, manifest.RepositoryId, manifest.ProjectId,
                new() { IncludeAssetData = false, IncludeSourceDetails = false, CancellationToken = ct }).Payload;
        }
        return comparer.Compare(Read(before), Read(after), new() { IncludeBoundedReadableText = false }).Areas;
    }

    internal async Task<byte[]> ReadAsync(ExternalTransferCapture capture, int fileIndex, CancellationToken ct)
    {
        var files = Manifest(capture).Files;
        if (fileIndex < 0 || fileIndex >= files.Count) throw new KeyNotFoundException("Transfer file not found.");
        var descriptor = files[fileIndex];
        // A caller chooses only an index from the captured manifest, never a filesystem path.
        if (descriptor.Path.Contains('\\') || descriptor.Path.StartsWith('/') || descriptor.Path.Split('/').Any(value => value is ".." or "." or ""))
            throw new InvalidDataException("The transfer manifest contains an unsafe path.");
        var path = SafePath("captures", capture.Id.ToString("N"), descriptor.Path.Replace('/', Path.DirectorySeparatorChar));
        var bytes = await File.ReadAllBytesAsync(path, ct);
        if (bytes.LongLength != descriptor.Length || Convert.ToHexStringLower(SHA256.HashData(bytes)) != descriptor.Sha256)
            throw new InvalidDataException("Transfer content could not be verified. Capture a new transfer; existing project content has been retained.");
        return bytes;
    }

    internal async Task<ExternalProjectLink> RequireLinkAsync(Guid projectId, Guid linkId, CancellationToken ct)
    {
        await using var read = await database.OpenReadAsync(ct);
        return await read.Db.ExternalProjectLinks.SingleOrDefaultAsync(value => value.Id == linkId && value.ProjectId == projectId, ct)
            ?? throw new KeyNotFoundException("External project link not found.");
    }

    internal async Task<ExternalTransferCapture> RequireCaptureAsync(Guid projectId, Guid id, CancellationToken ct)
    {
        await using var read = await database.OpenReadAsync(ct);
        return await read.Db.ExternalTransferCaptures.SingleOrDefaultAsync(value => value.Id == id
            && read.Db.ExternalProjectLinks.Any(link => link.Id == value.LinkId && link.ProjectId == projectId), ct)
            ?? throw new KeyNotFoundException("Transfer capture not found.");
    }
}
