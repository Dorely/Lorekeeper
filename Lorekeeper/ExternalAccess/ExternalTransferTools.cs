namespace Lorekeeper.ExternalAccess;

internal static class ExternalTransferTools
{
    internal static string[] CapturePermissions { get; } = ["transfer.read", "projects.read", "direction.read", "outline.read", "manuscript.read",
        "canon.read", "sources.read", "images.read", "composition.read", "voice.read", "publication.read", "review.read"];

    public static void Add(List<ExternalToolDefinition> tools)
    {
        var surfaces = new[] { "Outline", "Editor", "World", "Images", "Voice", "Publish" };
        Add<ExternalLinkWriteRequest>("create_external_project_link", "Create or discover a named external integration by stable externalKey. It is separate from credentials, Review Edits and Git synchronization.", ["transfer.write"], true, (owner, input, ct) => owner.CreateExternalLinkAsync(input, ct));
        Add<WorkspaceRequest>("list_external_project_links", "Read local integration identities and their last acknowledged transfer baselines.", ["transfer.read"], false, (owner, input, ct) => owner.ListExternalLinksAsync(input, ct));
        Add<ExternalIdentityWriteRequest>("save_external_source_identity", "Bind an external source key to an exact Lorekeeper identity. Existing mappings require expectedVersion and remain available after target removal.", ["transfer.write"], true, (owner, input, ct) => owner.SaveExternalIdentityAsync(input, ct));
        Add<ExternalLinkRequest>("capture_transfer", "Capture an immutable comparison of all current saved creative state, including pending edits. This does not approve changes, checkpoint or push.", CapturePermissions, false, (owner, input, ct) => owner.CaptureTransferAsync(input, ct));
        Add<TransferReadRequest>("read_transfer_changes", "Read every added/modified/removed captured file and identity mapping. Follow pagination; use exact before/after file reads to interpret changes.", CapturePermissions, false, (owner, input, ct) => owner.ReadTransferChangesAsync(input, ct));
        Add<TransferFileRequest>("read_transfer_file", "Read an exact immutable structured creative-state file by manifest index, or obtain a hash-verified binary download resource. No server filesystem path is accepted.", CapturePermissions, false, (owner, input, ct) => owner.ReadTransferFileAsync(input, ct));
        Add<TransferAcknowledgeRequest>("acknowledge_transfer", "Advance only this captured baseline after successful external handling, including deliberate reviewed exclusions. Newer live edits remain outstanding; stale acknowledgements cannot replace a newer baseline.", ["transfer.read", "transfer.write"], true, (owner, input, ct) => owner.AcknowledgeTransferAsync(input, ct));
        Add<BeginImageUploadRequest>("begin_image_upload", "Reserve a bounded image upload. Send original bytes with the installed transfer helper, then commit_image_upload; never generate base64 text.", ["images.write"], true, (owner, input, ct) => owner.BeginImageUploadAsync(input, ct));
        Add<CommitImageUploadRequest>("commit_image_upload", "Validate the staged upload and create an unattached reusable image through the owning image service.", ["images.read", "images.write"], true, (owner, input, ct) => owner.CommitImageUploadAsync(input, ct));

        void Add<T>(string name, string description, string[] permissions, bool mutates,
            Func<ExternalAccessCommands, T, CancellationToken, Task<object>> invoke) =>
            tools.Add(ExternalToolCatalog.Define(name, description, permissions,
                name is "begin_image_upload" or "commit_image_upload" ? ["Images"] : surfaces, mutates, invoke));
    }
}

public sealed record ExternalLinkWriteRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, string Name, string ExternalKey);
public sealed record ExternalLinkRequest(Guid ProjectId, Guid WorkspaceId, Guid LinkId);
public sealed record ExternalIdentityWriteRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, Guid LinkId,
    string ExternalKey, string TargetKind, Guid TargetId, string? ExpectedVersion = null);
public sealed record TransferReadRequest(Guid ProjectId, Guid WorkspaceId, Guid CaptureId, int PageNumber = 1);
public sealed record TransferFileRequest(Guid ProjectId, Guid WorkspaceId, Guid CaptureId, int FileIndex, int PageNumber = 1);
public sealed record TransferAcknowledgeRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, Guid CaptureId);
public sealed record BeginImageUploadRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, string FileName, string ContentType);
public sealed record CommitImageUploadRequest(Guid ProjectId, Guid WorkspaceId, Guid MutationId, Guid TransferId, string AltText);
