using Lorekeeper.VersionHistory.Restore;

namespace Lorekeeper.VersionHistory.Sync;

public enum ProjectVersionCloneImportErrorCode
{
    InvalidRequest,
    CloneFailed,
    RestoreFailed,
    CleanupFailed,
    Canceled,
}

public sealed class ProjectVersionCloneImportException(
    ProjectVersionCloneImportErrorCode code,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public ProjectVersionCloneImportErrorCode Code { get; } = code;
}

public sealed record VersionHistoryCloneImportRequest(
    VersionHistoryImportCloneRequest CloneRequest);

public sealed record VersionHistoryCloneRemoteAttachment(
    bool Succeeded,
    ProjectVersionSyncStatus? Status,
    string Message,
    string? ErrorCode = null);

public sealed record VersionHistoryCloneImportResult(
    VersionHistoryImportCloneResult CloneResult,
    VersionHistoryImportResult Import,
    VersionHistoryCloneRemoteAttachment RemoteAttachment,
    IReadOnlyList<string> Warnings);

public interface IProjectVersionCloneImportService
{
    Task<VersionHistoryCloneImportResult> ImportCloneAsync(
        VersionHistoryCloneImportRequest request,
        CancellationToken cancellationToken = default);
}
