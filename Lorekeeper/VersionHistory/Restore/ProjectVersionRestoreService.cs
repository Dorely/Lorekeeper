using System.Text.Json;
using Lorekeeper.Context;
using Lorekeeper.Graph;
using Lorekeeper.ImportExport;
using Lorekeeper.Ingest;
using Lorekeeper.Knowledge;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Publish;
using Lorekeeper.Search;
using Lorekeeper.VersionHistory.Compare;
using Lorekeeper.VersionHistory.Git;
using Lorekeeper.VersionHistory.Services;
using Lorekeeper.VersionHistory.Snapshots;
using Lorekeeper.VersionHistory.Sync;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.VersionHistory.Restore;

/// <summary>
/// Applies validated snapshots with their original stable IDs. Selective
/// restore is implemented as a deterministic payload merge followed by the
/// same whole-project applicator, so dependency validation remains centralized
/// and partial restore cannot orphan creative state.
/// </summary>
public sealed class ProjectVersionRestoreService(
    IProjectVersionHistoryService history,
    IAppDatabaseOperationFactory database,
    IProjectMutationCoordinator projectMutations,
    IOutlineGraphSync outlineGraphSync,
    IContextIndexingService contextIndexing,
    IIngestGraphSync ingestGraphSync,
    IGraphStore graph,
    IGraphAutoLinkService autoLinks,
    IProjectSearchIndex searchIndex,
    IGitRepositoryStore? git = null,
    ProjectVersionHistoryService? historyService = null,
    ProjectVersionHistoryUiEvents? historyEvents = null) : IProjectVersionRestoreService
{
    public async Task RestoreReviewOtherAsync(
        Guid projectId,
        ProjectVersionReviewConcurrencyToken expectedToken,
        CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("A project ID is required.", nameof(projectId));
        ArgumentNullException.ThrowIfNull(expectedToken);

        var warnings = new List<string>();
        var unresolved = new List<VersionHistoryUnresolvedReference>();
        VersionHistorySnapshotPayload restorePayload;
        var historyCoordinator = historyService ?? history as ProjectVersionHistoryService
            ?? throw new InvalidOperationException("Scoped review restore requires the project version-history service implementation.");
        await using (var mutationLease = await projectMutations.AcquireAsync(projectId, cancellationToken))
        {
            var context = await historyCoordinator.LoadReviewSnapshotUnderLeaseAsync(projectId, cancellationToken)
                ?? throw new InvalidOperationException($"Project {projectId} has no version-history repository.");
            EnsureReviewTokenMatches(context.Status, expectedToken);

            var approved = context.Approved;
            var current = context.Current;
            ValidateWholePayload(approved.Payload);
            ValidateWholePayload(current.Payload);
            restorePayload = SynthesizeOtherReviewRestore(approved.Payload, current.Payload);
            ValidateWholePayload(restorePayload);
            warnings.Add("Restored non-manuscript project state to the approved review head; live manuscript targets were preserved.");

            await using var operation = await database.OpenWriteAsync(cancellationToken);
            await using var transaction = await operation.Db.Database.BeginTransactionAsync(cancellationToken);
            var db = operation.Db;
            await ValidateDatabaseIdentityAsync(db, restorePayload, cancellationToken);
            await RefuseQueuedWorkAsync(db, projectId, cancellationToken);
            await ClearOperationalStateAsync(db, projectId, cancellationToken);
            await ReplaceCanonicalStateAsync(db, restorePayload, unresolved, warnings, cancellationToken);
            await operation.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await RebuildProjectionsAsync(projectId, restorePayload, warnings, CancellationToken.None);
        (historyEvents ?? throw new InvalidOperationException("Scoped review restore requires the review event publisher.")).PublishReviewStateChanged(projectId);
    }

    public Task RestoreReviewCompositionAsync(
        Guid projectId,
        ProjectVersionReviewTarget target,
        Guid compositionId,
        ProjectVersionReviewConcurrencyToken expectedToken,
        CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("A project ID is required.", nameof(projectId));
        ArgumentNullException.ThrowIfNull(target);
        ValidateReviewTarget(target);
        if (compositionId == Guid.Empty)
            throw new ArgumentException("A composition ID is required.", nameof(compositionId));
        ArgumentNullException.ThrowIfNull(expectedToken);

        return RestoreCompositionAsync(
            projectId,
            target,
            compositionId,
            expectedToken,
            historicalCommitSha: null,
            cancellationToken);
    }

    public Task RestoreHistoricalCompositionAsync(
        Guid projectId,
        ProjectVersionReviewTarget target,
        Guid compositionId,
        string historicalCommitSha,
        ProjectVersionReviewConcurrencyToken expectedToken,
        CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("A project ID is required.", nameof(projectId));
        ArgumentNullException.ThrowIfNull(target);
        ValidateReviewTarget(target);
        if (compositionId == Guid.Empty)
            throw new ArgumentException("A composition ID is required.", nameof(compositionId));
        if (string.IsNullOrWhiteSpace(historicalCommitSha))
            throw new ArgumentException("A historical parent commit SHA is required.", nameof(historicalCommitSha));
        ArgumentNullException.ThrowIfNull(expectedToken);

        return RestoreCompositionAsync(
            projectId,
            target,
            compositionId,
            expectedToken,
            historicalCommitSha,
            cancellationToken);
    }

    private async Task RestoreCompositionAsync(
        Guid projectId,
        ProjectVersionReviewTarget target,
        Guid compositionId,
        ProjectVersionReviewConcurrencyToken expectedToken,
        string? historicalCommitSha,
        CancellationToken cancellationToken)
    {

        var warnings = new List<string>();
        var unresolved = new List<VersionHistoryUnresolvedReference>();
        VersionHistorySnapshotPayload restorePayload;
        var historyCoordinator = historyService ?? history as ProjectVersionHistoryService
            ?? throw new InvalidOperationException("Scoped review restore requires the project version-history service implementation.");

        await using (var mutationLease = await projectMutations.AcquireAsync(projectId, cancellationToken))
        {
            var context = await historyCoordinator.LoadReviewSnapshotUnderLeaseAsync(projectId, cancellationToken)
                ?? throw new InvalidOperationException($"Project {projectId} has no version-history repository.");
            EnsureReviewTokenMatches(context.Status, expectedToken);

            var approved = context.Approved;
            var current = context.Current;
            ValidateWholePayload(approved.Payload);
            ValidateWholePayload(current.Payload);
            VersionHistorySnapshotPayload sourcePayload;
            if (historicalCommitSha is null)
            {
                sourcePayload = approved.Payload;
            }
            else
            {
                sourcePayload = historyCoordinator.LoadCheckpointUnderLease(
                    context.Status.Repository.RepositoryId,
                    historicalCommitSha,
                    projectId,
                    cancellationToken).Payload;
                ValidateWholePayload(sourcePayload);
            }

            ValidateManuscriptCompositionReferences(approved.Payload);
            ValidateManuscriptCompositionReferences(current.Payload);
            ValidateManuscriptCompositionReferences(sourcePayload);
            restorePayload = SynthesizeCompositionRestore(
                sourcePayload,
                current.Payload,
                target,
                compositionId);
            ValidateWholePayload(restorePayload);
            ValidateManuscriptCompositionReferences(restorePayload);

            warnings.Add(historicalCommitSha is null
                ? "Restored the selected Designed Page to the approved review head; all other live project state was preserved."
                : "Restored the selected Designed Page to its historical parent snapshot; all other live project state was preserved.");

            await using var operation = await database.OpenWriteAsync(cancellationToken);
            await using var transaction = await operation.Db.Database.BeginTransactionAsync(cancellationToken);
            var db = operation.Db;
            await ValidateDatabaseIdentityAsync(db, restorePayload, cancellationToken);
            await RefuseQueuedWorkAsync(db, projectId, cancellationToken);
            await ClearOperationalStateAsync(db, projectId, cancellationToken);
            await ReplaceCanonicalStateAsync(db, restorePayload, unresolved, warnings, cancellationToken);
            await operation.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await RebuildProjectionsAsync(projectId, restorePayload, warnings, CancellationToken.None);
        (historyEvents ?? throw new InvalidOperationException("Scoped review restore requires the review event publisher.")).PublishReviewStateChanged(projectId);
    }

    public async Task<VersionHistoryRestoreResult> RestoreAsync(
        Guid projectId,
        string targetCommitSha,
        VersionHistoryRestoreSelection selection,
        string? safetyMessage = null,
        CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("A project ID is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(targetCommitSha))
            throw new ArgumentException("A target checkpoint commit SHA is required.", nameof(targetCommitSha));
        ArgumentNullException.ThrowIfNull(selection);
        var target = await history.LoadCheckpointAsync(projectId, targetCommitSha, cancellationToken);
        if (target.Manifest.ProjectId != projectId
            || target.Payload.ProjectId != projectId
            || target.Manifest.RepositoryId != target.Payload.RepositoryId)
            throw new VersionHistoryRestoreException("ProjectIdentityMismatch", "The target checkpoint belongs to another project.");
        ValidateWholePayload(target.Payload);
        var preRestore = await history.CreateCheckpointAsync(
            projectId,
            ProjectVersionCheckpointKind.Manual,
            string.IsNullOrWhiteSpace(safetyMessage) ? "Pre-restore safety checkpoint" : safetyMessage,
            cancellationToken: cancellationToken);

        var status = await history.GetStatusAsync(projectId, includeCurrentSnapshotHash: false, cancellationToken);
        if (status?.Repository.HeadCommitSha is null)
            throw new VersionHistoryRestoreException("MissingCurrentHead", "The current project has no version-history head to restore from.");
        var current = await history.LoadCheckpointAsync(projectId, status.Repository.HeadCommitSha, cancellationToken);
        if (current.Payload.ProjectId != projectId
            || current.Manifest.ProjectId != projectId
            || current.Manifest.RepositoryId != current.Payload.RepositoryId
            || current.Manifest.RepositoryId != target.Manifest.RepositoryId)
            throw new VersionHistoryRestoreException("ProjectIdentityMismatch", "The current checkpoint belongs to another project.");
        var restorePayload = MergePayload(current.Payload, target.Payload, selection);
        ValidateWholePayload(restorePayload);
        var warnings = new List<string>();
        var unresolved = new List<VersionHistoryUnresolvedReference>();
        if (selection.Scope != VersionHistoryRestoreScope.WholeProject)
            warnings.Add($"Selective restore applied scope '{selection.Scope}' from checkpoint {targetCommitSha}.");
        await using (var mutationLease = await projectMutations.AcquireAsync(projectId, cancellationToken))
        await using (var operation = await database.OpenWriteAsync(cancellationToken))
        await using (var transaction = await operation.Db.Database.BeginTransactionAsync(cancellationToken))
        {
            var db = operation.Db;
            await ValidateDatabaseIdentityAsync(db, restorePayload, cancellationToken);
            await RefuseQueuedWorkAsync(db, projectId, cancellationToken);
            await ClearOperationalStateAsync(db, projectId, cancellationToken);
            await ReplaceCanonicalStateAsync(db, restorePayload, unresolved, warnings, cancellationToken);
            await operation.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await RebuildProjectionsAsync(projectId, restorePayload, warnings, CancellationToken.None);
        var postRestore = await history.CreateCheckpointAsync(
            projectId,
            ProjectVersionCheckpointKind.Restored,
            $"Restored checkpoint {targetCommitSha}",
            cancellationToken: CancellationToken.None);
        return new(
            projectId,
            targetCommitSha,
            preRestore,
            postRestore,
            selection,
            warnings,
            unresolved);
    }

    public async Task<VersionHistoryImportResult> ImportCloneAsync(
        VersionHistoryImportCloneResult clone,
        VersionHistorySnapshotArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clone);
        ArgumentNullException.ThrowIfNull(artifact);
        if (!ManifestIdentityEquals(clone.Manifest, artifact.Manifest)
            || clone.RepositoryId != artifact.Manifest.RepositoryId
            || clone.ProjectId != artifact.Manifest.ProjectId)
        {
            throw new VersionHistoryRestoreException(
                "ImportIdentityMismatch",
                "The clone metadata and validated snapshot artifact do not share repository and project identity.");
        }

        return await ImportValidatedSnapshotAsync(
            new VersionHistoryValidatedSnapshotImport(
                artifact,
                clone.HeadCommitSha,
                clone.HeadTreeSha),
            cancellationToken);
    }

    public async Task<VersionHistoryImportResult> ImportValidatedSnapshotAsync(
        VersionHistoryValidatedSnapshotImport import,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(import);
        ArgumentNullException.ThrowIfNull(import.Artifact);
        ValidateImport(import);

        var payload = import.Artifact.Payload;
        var warnings = new List<string>();
        var unresolved = new List<VersionHistoryUnresolvedReference>();
        VersionHistoryImportResult result;
        await using (var mutationLease = await projectMutations.AcquireAsync(payload.ProjectId, cancellationToken))
        await using (var operation = await database.OpenWriteAsync(cancellationToken))
        await using (var transaction = await operation.Db.Database.BeginTransactionAsync(cancellationToken))
        {
            var db = operation.Db;
            await ValidateImportDatabaseIdentityAsync(db, payload, cancellationToken);

            var now = DateTime.UtcNow;
            var project = new Project
            {
                Id = payload.ProjectId,
                Name = payload.Project.Project.Name,
                Slug = payload.Project.Project.Slug,
                ProjectGuidance = payload.Project.Project.ProjectGuidance,
                IncludeCurrentChapterInContext = payload.Project.Project.IncludeCurrentChapterInContext,
                // Workflow policy is local state and is deliberately not
                // restored from either current or legacy project snapshots.
                ReviewEditsEnabled = false,
                ContestModeEnabled = payload.Project.ContestModeEnabled,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var repository = new ProjectVersionRepository
            {
                Id = payload.RepositoryId,
                ProjectId = payload.ProjectId,
                Project = project,
                CreativeRevision = 1,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Projects.Add(project);
            db.ProjectVersionRepositories.Add(repository);
            await operation.SaveChangesAsync(cancellationToken);

            await ReplaceCanonicalStateAsync(db, payload, unresolved, warnings, cancellationToken);
            repository.CreativeRevision = 1;
            repository.LastCheckpointRevision = 1;
            repository.LastCheckpointContentHash = import.Artifact.Manifest.ContentHash;
            repository.HeadCommitSha = import.HeadCommitSha;
            repository.HeadContentHash = import.Artifact.Manifest.ContentHash;
            repository.LastCheckpointAt = now;
            repository.UpdatedAt = now;

            var checkpoint = new ProjectVersionCheckpoint
            {
                ProjectVersionRepositoryId = repository.Id,
                ManifestSchemaVersion = import.Artifact.Manifest.SchemaVersion,
                CreativeRevision = 1,
                ContentHash = import.Artifact.Manifest.ContentHash,
                ManifestHash = import.Artifact.Manifest.ManifestHash,
                CommitSha = import.HeadCommitSha,
                ParentCommitSha = import.ParentCommitSha,
                Kind = ProjectVersionCheckpointKind.Imported,
                Source = ProjectVersionCheckpointSource.Remote,
                Message = string.IsNullOrWhiteSpace(import.Message)
                    ? $"Imported version-history head {import.HeadCommitSha}"
                    : import.Message,
                CreatedAt = now,
            };
            db.ProjectVersionCheckpoints.Add(checkpoint);
            await operation.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            result = new VersionHistoryImportResult(
                payload.ProjectId,
                payload.RepositoryId,
                import.HeadCommitSha,
                import.HeadTreeSha,
                ToCheckpointView(checkpoint),
                warnings,
                unresolved);
        }

        await RebuildProjectionsAsync(payload.ProjectId, payload, warnings, CancellationToken.None);
        return result;
    }

    public async Task<VersionHistoryCheckoutResult> CheckoutValidatedSnapshotAsync(
        VersionHistoryValidatedProjectCheckout checkout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkout);
        ArgumentNullException.ThrowIfNull(checkout.Artifact);
        if (checkout.ProjectId == Guid.Empty)
            throw new VersionHistoryRestoreException("InvalidProjectIdentity", "A project identity is required for a version-history checkout.");

        ValidateImport(new VersionHistoryValidatedSnapshotImport(
            checkout.Artifact,
            checkout.HeadCommitSha,
            checkout.HeadTreeSha,
            checkout.ParentCommitSha,
            checkout.Message));
        var payload = checkout.Artifact.Payload;
        if (payload.ProjectId != checkout.ProjectId)
            throw new VersionHistoryRestoreException("ProjectIdentityMismatch", "The checkout artifact belongs to another project.");

        ProjectVersionLoadedCheckpoint localHead;
        try
        {
            localHead = await history.LoadCheckpointAsync(
                checkout.ProjectId,
                checkout.HeadCommitSha,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new VersionHistoryRestoreException(
                "CheckoutHeadMismatch",
                "The requested checkout head is not available as a validated local Git checkpoint.");
        }

        if (!string.Equals(localHead.Commit.Sha, checkout.HeadCommitSha, StringComparison.Ordinal)
            || !string.Equals(localHead.Commit.TreeSha, checkout.HeadTreeSha, StringComparison.Ordinal)
            || !ManifestIdentityEquals(localHead.Manifest, checkout.Artifact.Manifest))
            throw new VersionHistoryRestoreException(
                "CheckoutHeadMismatch",
                "The supplied checkout snapshot does not match the validated local Git commit and tree.");

        await using var mutationLease = await projectMutations.AcquireAsync(checkout.ProjectId, cancellationToken);
        return await CheckoutValidatedSnapshotUnderLeaseAsync(
            checkout,
            localHead.Commit,
            cancellationToken);
    }

    internal async Task<VersionHistoryCheckoutResult> CheckoutValidatedSnapshotUnderLeaseAsync(
        VersionHistoryValidatedProjectCheckout checkout,
        GitCommitMetadata localHead,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkout);
        ArgumentNullException.ThrowIfNull(checkout.Artifact);
        ArgumentNullException.ThrowIfNull(localHead);
        ValidateImport(new VersionHistoryValidatedSnapshotImport(
            checkout.Artifact,
            checkout.HeadCommitSha,
            checkout.HeadTreeSha,
            checkout.ParentCommitSha,
            checkout.Message));
        var payload = checkout.Artifact.Payload;
        if (payload.ProjectId != checkout.ProjectId
            || !string.Equals(localHead.Sha, checkout.HeadCommitSha, StringComparison.Ordinal)
            || !string.Equals(localHead.TreeSha, checkout.HeadTreeSha, StringComparison.Ordinal))
        {
            throw new VersionHistoryRestoreException(
                "CheckoutHeadMismatch",
                "The supplied checkout snapshot does not match the validated local Git commit and tree.");
        }

        if (git is not null
            && !string.Equals(git.GetHead(payload.RepositoryId).CommitSha, checkout.HeadCommitSha, StringComparison.Ordinal))
        {
            throw new VersionHistoryRestoreException(
                "CheckoutHeadMismatch",
                "The local Git main head changed or does not match the requested checkout head.");
        }

        var warnings = new List<string>();
        var unresolved = new List<VersionHistoryUnresolvedReference>();
        VersionHistoryCheckoutResult result;
        await using (var operation = await database.OpenWriteAsync(cancellationToken))
        await using (var transaction = await operation.Db.Database.BeginTransactionAsync(cancellationToken))
        {
            var db = operation.Db;
            await ValidateDatabaseIdentityAsync(db, payload, cancellationToken);
            var repository = await db.ProjectVersionRepositories
                .SingleOrDefaultAsync(item => item.ProjectId == checkout.ProjectId, cancellationToken)
                ?? throw new VersionHistoryRestoreException(
                    "RepositoryNotFound",
                    "The project has no local version-history repository for checkout.");
            if (repository.Id != payload.RepositoryId)
                throw new VersionHistoryRestoreException(
                    "RepositoryIdentityMismatch",
                    "The checkout repository identity does not match the project's local repository.");
            if (!string.Equals(repository.HeadCommitSha, checkout.HeadCommitSha, StringComparison.Ordinal)
                || !string.Equals(repository.HeadContentHash, checkout.Artifact.Manifest.ContentHash, StringComparison.Ordinal))
            {
                throw new VersionHistoryRestoreException(
                    "CheckoutHeadMismatch",
                    "The cached local history head changed or does not match the requested checkout snapshot.");
            }

            await RefuseQueuedWorkAsync(db, checkout.ProjectId, cancellationToken);
            await ClearOperationalStateAsync(db, checkout.ProjectId, cancellationToken);
            await ReplaceCanonicalStateAsync(db, payload, unresolved, warnings, cancellationToken);

            var checkpoint = await db.ProjectVersionCheckpoints
                .Where(item => item.ProjectVersionRepositoryId == repository.Id)
                .Where(item => item.CommitSha == checkout.HeadCommitSha)
                .SingleOrDefaultAsync(cancellationToken);
            var checkpointCreated = checkpoint is null;
            if (checkpoint is null)
            {
                repository.CreativeRevision = Math.Max(repository.CreativeRevision + 1, 1);
                checkpoint = new ProjectVersionCheckpoint
                {
                    ProjectVersionRepositoryId = repository.Id,
                    ManifestSchemaVersion = checkout.Artifact.Manifest.SchemaVersion,
                    CreativeRevision = repository.CreativeRevision,
                    ContentHash = checkout.Artifact.Manifest.ContentHash,
                    ManifestHash = checkout.Artifact.Manifest.ManifestHash,
                    CommitSha = checkout.HeadCommitSha,
                    ParentCommitSha = checkout.ParentCommitSha ?? localHead.ParentShas.FirstOrDefault(),
                    Kind = ProjectVersionCheckpointKind.Imported,
                    Source = ProjectVersionCheckpointSource.Remote,
                    Message = string.IsNullOrWhiteSpace(checkout.Message)
                        ? $"Aligned to remote version-history head {checkout.HeadCommitSha}"
                        : checkout.Message,
                    CreatedAt = DateTime.UtcNow,
                };
                db.ProjectVersionCheckpoints.Add(checkpoint);
            }
            else if (!string.Equals(checkpoint.ContentHash, checkout.Artifact.Manifest.ContentHash, StringComparison.Ordinal)
                || !string.Equals(checkpoint.ManifestHash, checkout.Artifact.Manifest.ManifestHash, StringComparison.Ordinal))
            {
                throw new VersionHistoryRestoreException(
                    "CheckpointIdentityMismatch",
                    "The existing checkpoint commit has different snapshot hashes.");
            }

            repository.HeadCommitSha = checkout.HeadCommitSha;
            repository.HeadContentHash = checkout.Artifact.Manifest.ContentHash;
            repository.LastCheckpointRevision = checkpoint.CreativeRevision;
            repository.LastCheckpointContentHash = checkpoint.ContentHash;
            repository.LastCheckpointAt = checkpoint.CreatedAt;
            repository.UpdatedAt = DateTime.UtcNow;
            await operation.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            result = new VersionHistoryCheckoutResult(
                checkout.ProjectId,
                payload.RepositoryId,
                checkout.HeadCommitSha,
                checkout.HeadTreeSha,
                ToCheckpointView(checkpoint),
                checkpointCreated,
                warnings,
                unresolved);
        }

        await RebuildProjectionsAsync(checkout.ProjectId, payload, warnings, CancellationToken.None);
        return result;
    }

    private static void ValidateImport(VersionHistoryValidatedSnapshotImport import)
    {
        var manifest = import.Artifact.Manifest;
        var payload = import.Artifact.Payload;
        if (!string.Equals(manifest.FormatId, VersionHistorySnapshotContract.FormatId, StringComparison.Ordinal)
            || !VersionHistorySnapshotContract.CanReadSchema(manifest.SchemaVersion)
            || !manifest.IncludedAreas.SequenceEqual(VersionHistorySnapshotContract.IncludedAreas, StringComparer.Ordinal)
            || !IsSha256(manifest.ContentHash)
            || !IsSha256(manifest.ManifestHash)
            || manifest.RepositoryId == Guid.Empty
            || manifest.ProjectId == Guid.Empty
            || manifest.RepositoryId != payload.RepositoryId
            || manifest.ProjectId != payload.ProjectId)
        {
            throw new VersionHistoryRestoreException(
                "ImportIdentityMismatch",
                "The imported manifest and payload do not contain matching repository and project identities.");
        }
        if (string.IsNullOrWhiteSpace(import.HeadCommitSha)
            || string.IsNullOrWhiteSpace(import.HeadTreeSha))
            throw new VersionHistoryRestoreException(
                "InvalidImportHead",
                "An imported snapshot must provide both a Git head commit and tree identity.");
        if (string.IsNullOrWhiteSpace(payload.Project.Project.Name)
            || string.IsNullOrWhiteSpace(payload.Project.Project.Slug))
            throw new VersionHistoryRestoreException(
                "InvalidProjectIdentity",
                "An imported snapshot must provide a non-empty project name and slug.");

        ValidateWholePayload(payload);
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool ManifestIdentityEquals(
        VersionHistorySnapshotManifest left,
        VersionHistorySnapshotManifest right)
    {
        return left.FormatId == right.FormatId
            && left.SchemaVersion == right.SchemaVersion
            && left.RepositoryId == right.RepositoryId
            && left.ProjectId == right.ProjectId
            && left.ContentHash == right.ContentHash
            && left.ManifestHash == right.ManifestHash
            && left.IncludedAreas.SequenceEqual(right.IncludedAreas, StringComparer.Ordinal)
            && left.Files.Count == right.Files.Count
            && left.Files.Zip(right.Files).All(pair =>
                pair.First.Path == pair.Second.Path
                && pair.First.Length == pair.Second.Length
                && pair.First.Sha256 == pair.Second.Sha256);
    }

    private static VersionHistorySnapshotPayload MergePayload(
        VersionHistorySnapshotPayload current,
        VersionHistorySnapshotPayload target,
        VersionHistoryRestoreSelection selection)
    {
        if (current.RepositoryId != target.RepositoryId || current.ProjectId != target.ProjectId)
            throw new VersionHistoryRestoreException("ProjectIdentityMismatch", "The current and target snapshots do not share project identity.");
        if (selection.Scope == VersionHistoryRestoreScope.WholeProject)
            return target;

        if (selection.Scope == VersionHistoryRestoreScope.SelectedChapters)
        {
            if (selection.ChapterIds.Count == 0)
                throw new VersionHistoryRestoreException("InvalidRestoreSelection", "At least one chapter is required for a selected-chapter restore.");
            var narrative = MergeSelectedChapters(current.Narrative, target.Narrative, selection);
            return new VersionHistorySnapshotPayload(
                current.RepositoryId,
                current.ProjectId,
                current.Project,
                narrative,
                current.Graph,
                current.Sources,
                current.Assets,
                current.Manuscript,
                current.Composition,
                current.Publication)
            {
                ImageData = current.ImageData,
                FontFaceData = current.FontFaceData,
            };
        }

        if (selection.Scope != VersionHistoryRestoreScope.MajorAreas)
            throw new VersionHistoryRestoreException("InvalidRestoreSelection", $"Restore scope '{selection.Scope}' is not supported.");

        var selectedAreas = selection.Areas.ToHashSet(StringComparer.Ordinal);
        var unknownArea = selectedAreas.Except(VersionHistorySnapshotContract.IncludedAreas, StringComparer.Ordinal).FirstOrDefault();
        if (unknownArea is not null || selectedAreas.Count == 0)
            throw new VersionHistoryRestoreException("InvalidRestoreSelection", "Major-area restore selection contains no valid areas.");

        var assets = selectedAreas.Contains("assets", StringComparer.Ordinal) ? target.Assets : current.Assets;
        return new VersionHistorySnapshotPayload(
            current.RepositoryId,
            current.ProjectId,
            selectedAreas.Contains("project", StringComparer.Ordinal) ? target.Project : current.Project,
            selectedAreas.Contains("narrative", StringComparer.Ordinal) ? target.Narrative : current.Narrative,
            selectedAreas.Contains("graph", StringComparer.Ordinal) ? target.Graph : current.Graph,
            selectedAreas.Contains("sources", StringComparer.Ordinal) ? target.Sources : current.Sources,
            assets,
            selectedAreas.Contains("manuscript", StringComparer.Ordinal) ? target.Manuscript : current.Manuscript,
            selectedAreas.Contains("composition", StringComparer.Ordinal) ? target.Composition : current.Composition,
            selectedAreas.Contains("publication", StringComparer.Ordinal) ? target.Publication : current.Publication)
        {
            ImageData = selectedAreas.Contains("assets", StringComparer.Ordinal) ? target.ImageData : current.ImageData,
            FontFaceData = selectedAreas.Contains("assets", StringComparer.Ordinal) ? target.FontFaceData : current.FontFaceData,
        };
    }

    private static VersionHistorySnapshotPayload SynthesizeOtherReviewRestore(
        VersionHistorySnapshotPayload approved,
        VersionHistorySnapshotPayload current)
    {
        if (approved.RepositoryId != current.RepositoryId || approved.ProjectId != current.ProjectId)
            throw new VersionHistoryRestoreException(
                "ProjectIdentityMismatch",
                "The approved and live review snapshots do not share project identity.");

        var approvedEditions = approved.Publication.PublicationEditions
            .ToDictionary(edition => edition.Id);
        var currentEditions = current.Publication.PublicationEditions
            .ToDictionary(edition => edition.Id);
        var editions = approved.Publication.PublicationEditions
            .Select(approvedEdition => currentEditions.TryGetValue(approvedEdition.Id, out var liveEdition)
                ? approvedEdition with { ChapterOverrides = liveEdition.ChapterOverrides.ToList() }
                : approvedEdition)
            // A newly-created edition has no approved metadata to restore to.
            // Keep it intact so its live manuscript overrides are not silently
            // discarded by an Other-only undo.
            .Concat(current.Publication.PublicationEditions
                .Where(edition => !approvedEditions.ContainsKey(edition.Id)))
            .ToList();

        return approved with
        {
            Narrative = approved.Narrative with
            {
                Chapters = current.Narrative.Chapters.ToList(),
            },
            Publication = approved.Publication with
            {
                PublicationEditions = editions,
            },
        };
    }

    private static VersionHistorySnapshotPayload SynthesizeCompositionRestore(
        VersionHistorySnapshotPayload source,
        VersionHistorySnapshotPayload current,
        ProjectVersionReviewTarget target,
        Guid compositionId)
    {
        if (source.RepositoryId != current.RepositoryId || source.ProjectId != current.ProjectId)
            throw new VersionHistoryRestoreException(
                "ProjectIdentityMismatch",
                "The composition restore source and live snapshot do not share project identity.");

        var currentComposition = FindUniqueComposition(current, compositionId, "live");
        var sourceComposition = FindUniqueComposition(source, compositionId, "source");
        if (currentComposition is null && sourceComposition is null)
            throw new InvalidOperationException("The Designed Page no longer exists in either the live project or restore source.");
        if (currentComposition is not null && !CompositionMatchesReviewTarget(currentComposition, target)
            || sourceComposition is not null && !CompositionMatchesReviewTarget(sourceComposition, target))
        {
            throw new InvalidOperationException(
                "The Designed Page does not belong to the reviewed chapter and content target.");
        }
        if (ProjectVersionReviewComposition.SemanticallyEquals(sourceComposition, currentComposition))
            throw new InvalidOperationException("The Designed Page is already at the requested restore state.");

        var liveCompositions = current.Composition.PageCompositions
            .Where(item => item.Id != compositionId)
            .ToList();
        if (sourceComposition is not null)
        {
            var liveIndex = -1;
            for (var index = 0; index < current.Composition.PageCompositions.Count; index++)
            {
                if (current.Composition.PageCompositions[index].Id == compositionId)
                {
                    liveIndex = index;
                    break;
                }
            }

            if (currentComposition is null || liveIndex < 0 || liveIndex > liveCompositions.Count)
                liveCompositions.Add(sourceComposition);
            else
                liveCompositions.Insert(liveIndex, sourceComposition);
        }

        var restored = current with
        {
            Composition = current.Composition with
            {
                PageCompositions = liveCompositions,
            },
        };
        if (sourceComposition is null)
        {
            var references = FindCompositionReferences(current, compositionId);
            if (references.Any(reference => reference.Target != target))
            {
                throw new VersionHistoryRestoreException(
                    "CompositionDependencyOutsideTarget",
                    "The Designed Page is referenced by another manuscript target; restore was refused to preserve target isolation.");
            }

            if (references.SingleOrDefault() is { } reference)
            {
                var sourceDocument = FindSnapshotManuscriptDocument(source, target)
                    ?? throw new VersionHistoryRestoreException(
                        "CompositionDependencyTargetUnavailable",
                        "The restore source does not contain the reviewed manuscript target needed to remove the Designed Page reference.");
                var sourceBlocks = sourceDocument.Content.ToDictionary(block => block.Id, StringComparer.Ordinal);
                var restoredBlocks = new List<ManuscriptBlock>(reference.Document.Content.Count);
                foreach (var block in reference.Document.Content)
                {
                    if (block.Type == ManuscriptBlockType.DesignedPage
                        && block.PageCompositionId == compositionId)
                    {
                        if (sourceBlocks.TryGetValue(block.Id, out var sourceBlock))
                        {
                            if (sourceBlock.Type == ManuscriptBlockType.DesignedPage
                                && sourceBlock.PageCompositionId == compositionId)
                            {
                                throw new VersionHistoryRestoreException(
                                    "AmbiguousCompositionDependency",
                                    "The restore source still contains the selected Designed Page reference, so the coupled manuscript change is ambiguous.");
                            }

                            restoredBlocks.Add(sourceBlock);
                        }

                        continue;
                    }

                    restoredBlocks.Add(block);
                }

                var restoredDocument = reference.Document with
                {
                    Revision = checked(reference.Document.Revision + 1),
                    Content = restoredBlocks,
                };
                restored = ReplaceSnapshotManuscriptDocument(
                    restored,
                    target,
                    restoredDocument);
            }
        }

        return restored;
    }

    private static ProjectExportPageComposition? FindUniqueComposition(
        VersionHistorySnapshotPayload payload,
        Guid compositionId,
        string snapshotLabel)
    {
        var matches = payload.Composition.PageCompositions
            .Where(item => item.Id == compositionId)
            .ToList();
        if (matches.Count > 1)
        {
            throw new VersionHistoryRestoreException(
                "DuplicateCompositionIdentity",
                $"The {snapshotLabel} snapshot contains duplicate identity for the selected Designed Page.");
        }

        return matches.SingleOrDefault();
    }

    private static IReadOnlyList<SnapshotManuscriptDocument> FindCompositionReferences(
        VersionHistorySnapshotPayload payload,
        Guid compositionId) =>
        EnumerateSnapshotManuscriptDocuments(payload)
            .Where(item => item.Document.Content.Any(block =>
                block.Type == ManuscriptBlockType.DesignedPage
                && block.PageCompositionId == compositionId))
            .ToList();

    private static ManuscriptDocument? FindSnapshotManuscriptDocument(
        VersionHistorySnapshotPayload payload,
        ProjectVersionReviewTarget target)
    {
        if (target.ContentTarget.IsCore)
        {
            var chapter = payload.Narrative.Chapters.SingleOrDefault(item => item.Id == target.ChapterId);
            return chapter is null
                ? null
                : ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision);
        }

        var edition = payload.Publication.PublicationEditions
            .SingleOrDefault(item => item.Id == target.ContentTarget.EditionId);
        if (edition is null)
            return null;

        var chapterOverride = edition.ChapterOverrides
            .SingleOrDefault(item => item.ChapterId == target.ChapterId);
        if (chapterOverride is not null)
        {
            return ManuscriptCodec.Deserialize(
                chapterOverride.ManuscriptJson,
                chapterOverride.ChapterId,
                chapterOverride.Revision);
        }

        var coreChapter = payload.Narrative.Chapters.SingleOrDefault(item => item.Id == target.ChapterId);
        return coreChapter is null
            ? null
            : ManuscriptCodec.Deserialize(coreChapter.ManuscriptJson, coreChapter.Id, coreChapter.ManuscriptRevision);
    }

    private static VersionHistorySnapshotPayload ReplaceSnapshotManuscriptDocument(
        VersionHistorySnapshotPayload payload,
        ProjectVersionReviewTarget target,
        ManuscriptDocument document)
    {
        var serialized = ManuscriptCodec.Serialize(document);
        if (target.ContentTarget.IsCore)
        {
            var chapters = payload.Narrative.Chapters
                .Select(chapter => chapter.Id == target.ChapterId
                    ? chapter with
                    {
                        ManuscriptJson = serialized,
                        ManuscriptRevision = document.Revision,
                    }
                    : chapter)
                .ToList();
            return payload with
            {
                Narrative = payload.Narrative with { Chapters = chapters },
            };
        }

        var editions = payload.Publication.PublicationEditions
            .Select(edition => edition.Id == target.ContentTarget.EditionId
                ? edition with
                {
                    ChapterOverrides = edition.ChapterOverrides
                        .Select(chapterOverride => chapterOverride.ChapterId == target.ChapterId
                            ? chapterOverride with
                            {
                                ManuscriptJson = serialized,
                                Revision = document.Revision,
                            }
                            : chapterOverride)
                        .ToList(),
                }
                : edition)
            .ToList();
        return payload with
        {
            Publication = payload.Publication with { PublicationEditions = editions },
        };
    }

    private static void ValidateManuscriptCompositionReferences(VersionHistorySnapshotPayload payload)
    {
        var compositionIds = payload.Composition.PageCompositions
            .Select(item => item.Id)
            .ToHashSet();
        foreach (var manuscript in EnumerateSnapshotManuscriptDocuments(payload))
        {
            foreach (var block in manuscript.Document.Content)
            {
                if (block.Type == ManuscriptBlockType.DesignedPage
                    && block.PageCompositionId is Guid compositionId
                    && !compositionIds.Contains(compositionId))
                {
                    throw new VersionHistoryRestoreException(
                        "MissingCompositionDependency",
                        $"Manuscript target '{manuscript.Target.ContentTarget.StorageKey}' references missing Designed Page composition '{compositionId:N}'.");
                }
            }
        }
    }

    private static IEnumerable<SnapshotManuscriptDocument> EnumerateSnapshotManuscriptDocuments(
        VersionHistorySnapshotPayload payload)
    {
        foreach (var chapter in payload.Narrative.Chapters)
        {
            yield return new SnapshotManuscriptDocument(
                new ProjectVersionReviewTarget(chapter.Id, EditorContentTarget.Core),
                ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision));
        }

        foreach (var edition in payload.Publication.PublicationEditions)
        {
            foreach (var chapterOverride in edition.ChapterOverrides)
            {
                yield return new SnapshotManuscriptDocument(
                    new ProjectVersionReviewTarget(
                        chapterOverride.ChapterId,
                        EditorContentTarget.ForEdition(edition.Id)),
                    ManuscriptCodec.Deserialize(
                        chapterOverride.ManuscriptJson,
                        chapterOverride.ChapterId,
                        chapterOverride.Revision));
            }
        }
    }

    private sealed record SnapshotManuscriptDocument(
        ProjectVersionReviewTarget Target,
        ManuscriptDocument Document);

    private static void EnsureReviewTokenMatches(
        ProjectVersionStatusView status,
        ProjectVersionReviewConcurrencyToken expectedToken)
    {
        if (status.Repository.RepositoryId != expectedToken.RepositoryId
            || !string.Equals(status.Repository.HeadCommitSha, expectedToken.HeadCommitSha, StringComparison.Ordinal)
            || !string.Equals(status.Repository.HeadContentHash, expectedToken.HeadContentHash, StringComparison.Ordinal)
            || !string.Equals(status.CurrentContentHash, expectedToken.CurrentContentHash, StringComparison.Ordinal))
        {
            throw new ProjectVersionReviewConcurrencyException(
                "The project changed after this review was loaded. Reload the review before continuing.");
        }
    }

    private static void ValidateReviewTarget(ProjectVersionReviewTarget target)
    {
        if (target.ChapterId == Guid.Empty)
            throw new ArgumentException("A review target must identify a chapter.", nameof(target));
        if (!Enum.IsDefined(target.ContentTarget.Kind))
            throw new ArgumentException("The review content target is invalid.", nameof(target));
        if (target.ContentTarget.IsCore)
        {
            if (target.ContentTarget.EditionId is not null)
                throw new ArgumentException("The Core review target cannot include an edition ID.", nameof(target));
            return;
        }

        if (target.ContentTarget.EditionId is not Guid editionId || editionId == Guid.Empty)
            throw new ArgumentException("An edition review target requires an edition ID.", nameof(target));
    }

    private static bool CompositionMatchesReviewTarget(
        ProjectExportPageComposition composition,
        ProjectVersionReviewTarget target) =>
        composition.ChapterId == target.ChapterId
        && (target.ContentTarget.IsCore
            ? composition.EditionId is null
            : composition.EditionId == target.ContentTarget.EditionId);

    private static VersionHistorySnapshotNarrativeArea MergeSelectedChapters(
        VersionHistorySnapshotNarrativeArea current,
        VersionHistorySnapshotNarrativeArea target,
        VersionHistoryRestoreSelection selection)
    {
        var selectedIds = selection.ChapterIds.ToHashSet();
        var currentChapters = current.Chapters.ToDictionary(chapter => chapter.Id);
        var targetChapters = target.Chapters.ToDictionary(chapter => chapter.Id);
        if (!selectedIds.Any(id => currentChapters.ContainsKey(id) || targetChapters.ContainsKey(id)))
            throw new VersionHistoryRestoreException("SelectedChapterNotFound", "None of the selected chapter IDs exists in the current or target snapshot.");

        var chapters = current.Chapters
            .Where(chapter => !selectedIds.Contains(chapter.Id))
            .Concat(target.Chapters.Where(chapter => selectedIds.Contains(chapter.Id)))
            .OrderBy(chapter => chapter.Id)
            .ToList();
        var acts = current.Acts.ToDictionary(act => act.Id);
        foreach (var chapter in target.Chapters.Where(chapter => selectedIds.Contains(chapter.Id)))
        {
            if (chapter.ActId is Guid actId && !acts.ContainsKey(actId))
            {
                var targetAct = target.Acts.FirstOrDefault(act => act.Id == actId)
                    ?? throw new VersionHistoryRestoreException("MissingActDependency", $"Selected chapter {chapter.Id:N} requires an act absent from the target snapshot.");
                acts.Add(targetAct.Id, targetAct);
            }
        }

        var preferences = current.ContextPreferences
            .Where(item => !selectedIds.Contains(item.ChapterId))
            .Concat(target.ContextPreferences.Where(item => selectedIds.Contains(item.ChapterId)))
            .OrderBy(item => item.Id)
            .ToList();
        var annotations = selection.AnnotationMode switch
        {
            VersionHistoryAnnotationRestoreMode.Exclude => current.Annotations,
            VersionHistoryAnnotationRestoreMode.SelectedChapterAnnotations => current.Annotations
                .Where(item => !selectedIds.Contains(item.ChapterId))
                .Concat(target.Annotations.Where(item => selectedIds.Contains(item.ChapterId)))
                .OrderBy(item => item.Id)
                .ToList(),
            VersionHistoryAnnotationRestoreMode.AllAnnotations => throw new VersionHistoryRestoreException(
                "InvalidRestoreSelection",
                "Selected-chapter restore cannot request all annotations."),
            _ => throw new VersionHistoryRestoreException("InvalidRestoreSelection", "Unknown annotation restore mode."),
        };

        return new VersionHistorySnapshotNarrativeArea(
            current.BookBrief,
            current.BookBriefCanonSourceIds,
            current.EntityTypes,
            acts.Values.OrderBy(act => act.Id).ToList(),
            chapters,
            current.WritingSamples,
            preferences,
            annotations);
    }

    private static void ValidateWholePayload(VersionHistorySnapshotPayload payload)
    {
        if (payload.Project.Project.Id != payload.ProjectId)
            throw new VersionHistoryRestoreException("ProjectIdentityMismatch", "The snapshot project payload does not match its manifest.");

        var actIds = payload.Narrative.Acts.Select(item => item.Id).ToHashSet();
        var chapterIds = payload.Narrative.Chapters.Select(item => item.Id).ToHashSet();
        var imageIds = payload.Assets.Images.Select(item => item.Id).ToHashSet();
        var sourceIds = payload.Sources.Sources.Select(item => item.Id).ToHashSet();
        var editionIds = payload.Publication.PublicationEditions.Select(item => item.Id).ToHashSet();
        var sectionIds = payload.Publication.PublicationSections.Select(item => item.Id).ToHashSet();
        var compositionIds = payload.Composition.PageCompositions.Select(item => item.Id).ToHashSet();
        var fontFaceIds = payload.Assets.FontFamilies.SelectMany(item => item.Faces).Select(item => item.Id).ToHashSet();
        if (payload.Project.References.Any(reference => reference.ReferenceId == Guid.Empty))
            throw new VersionHistoryRestoreException("InvalidReferenceIdentity", "The target snapshot contains a project reference without a stable identity.");
        if (payload.Project.References.GroupBy(reference => (reference.ReferencedRepositoryId, reference.ReferencedProjectId)).Any(group => group.Count() != 1))
            throw new VersionHistoryRestoreException("DuplicateReferenceIdentity", "The target snapshot contains duplicate project references.");
        if (payload.Project.References.GroupBy(reference => reference.ReferenceId).Any(group => group.Count() != 1))
            throw new VersionHistoryRestoreException("DuplicateReferenceIdentity", "The target snapshot contains duplicate project reference identities.");
        if (payload.Project.References.Any(reference =>
                reference.ReferencingProjectId != payload.ProjectId
                || reference.ReferencedRepositoryId == payload.RepositoryId
                    && reference.ReferencedProjectId == payload.ProjectId))
            throw new VersionHistoryRestoreException("InvalidReferenceIdentity", "The target snapshot contains a project reference with an invalid owner or self-reference.");
        if (actIds.Count != payload.Narrative.Acts.Count || chapterIds.Count != payload.Narrative.Chapters.Count)
            throw new VersionHistoryRestoreException("DuplicateStructuralIdentity", "The target snapshot contains duplicate act or chapter IDs.");
        if (payload.Narrative.Chapters.Any(chapter => chapter.ActId is Guid actId && !actIds.Contains(actId)))
            throw new VersionHistoryRestoreException("MissingActDependency", "A target chapter references an act absent from the target snapshot.");
        if (payload.Narrative.WritingSamples.GroupBy(item => item.Id).Any(group => group.Count() != 1)
            || payload.Narrative.ContextPreferences.GroupBy(item => item.Id).Any(group => group.Count() != 1)
            || payload.Narrative.Annotations.GroupBy(item => item.Id).Any(group => group.Count() != 1))
            throw new VersionHistoryRestoreException("DuplicateNarrativeIdentity", "The target snapshot contains duplicate writing sample, context preference, or annotation IDs.");
        if (payload.Narrative.ContextPreferences.Any(item => !chapterIds.Contains(item.ChapterId)))
            throw new VersionHistoryRestoreException("MissingPreferenceChapter", "A target context preference references a missing chapter.");
        if (payload.Narrative.Annotations.Any(item => !chapterIds.Contains(item.ChapterId)
            || item.EditionId is Guid editionId && !editionIds.Contains(editionId)))
            throw new VersionHistoryRestoreException("MissingAnnotationDependency", "A target annotation references a missing chapter or edition.");
        if (payload.Narrative.BookBriefCanonSourceIds.Any(id => !sourceIds.Contains(id)))
            throw new VersionHistoryRestoreException("MissingCanonicalSource", "The target Book Brief selection references a missing source.");
        if (payload.Sources.Sources.GroupBy(item => item.Id).Any(group => group.Count() != 1)
            || payload.Sources.Sources.Any(source => source.Chunks.GroupBy(item => item.Id).Any(group => group.Count() != 1)
                || source.Pages.GroupBy(item => item.Id).Any(group => group.Count() != 1)
                || source.Blocks.GroupBy(item => item.Id).Any(group => group.Count() != 1)
                || source.Blocks.Any(block => block.SourcePageId is Guid pageId && !source.Pages.Any(page => page.Id == pageId))))
            throw new VersionHistoryRestoreException("DuplicateSourceIdentity", "The target snapshot contains duplicate or dangling source-child identities.");
        if (payload.Sources.Sources.SelectMany(source => source.Chunks).GroupBy(item => item.Id).Any(group => group.Count() != 1)
            || payload.Sources.Sources.SelectMany(source => source.Pages).GroupBy(item => item.Id).Any(group => group.Count() != 1)
            || payload.Sources.Sources.SelectMany(source => source.Blocks).GroupBy(item => item.Id).Any(group => group.Count() != 1))
            throw new VersionHistoryRestoreException("DuplicateSourceIdentity", "The target snapshot contains duplicate source-child IDs.");
        if (payload.Assets.EntityVisualExamples.Any(item => !imageIds.Contains(item.ImageId)))
            throw new VersionHistoryRestoreException("MissingVisualAsset", "A target visual example references a missing image.");
        if (payload.Publication.PublicationSections.GroupBy(item => item.Id).Any(group => group.Count() != 1)
            || payload.Publication.PublicationSections.Any(item => item.EditionId is Guid editionId && !editionIds.Contains(editionId)))
            throw new VersionHistoryRestoreException("MissingPublicationDependency", "A target publication section references a missing edition.");
        if (payload.Publication.PublicationSections.Any(item => item.CoreSectionId is Guid coreId && !sectionIds.Contains(coreId)))
            throw new VersionHistoryRestoreException("MissingPublicationDependency", "A target publication section references a missing core section.");
        if (payload.Publication.PublicationSections.Any(item => !TargetExists(item.TargetKind, item.TargetId, actIds, chapterIds)))
            throw new VersionHistoryRestoreException("MissingPublicationDependency", "A target publication section references a missing act or chapter.");
        if (payload.Publication.PublicationEditions.Any(edition => edition.SelectedCoverImageId is Guid imageId && !imageIds.Contains(imageId)
            || edition.SelectedCoverChapterId is Guid chapterId && !chapterIds.Contains(chapterId)
            || edition.ChapterOverrides.Any(item => !chapterIds.Contains(item.ChapterId))
            || edition.Matter?.Any(item => item.CoreMatterId is Guid coreMatterId && !GuidSetContains(payload.Publication.PublicationBook?.Matter, coreMatterId)) == true
            || edition.ImagePlacements?.Any(item => !imageIds.Contains(item.AssetId)
                || item.CorePlacementId is Guid corePlacementId && !GuidSetContains(payload.Publication.PublicationBook?.ImagePlacements, corePlacementId)) == true))
            throw new VersionHistoryRestoreException("MissingPublicationDependency", "A target publication edition references a missing chapter, image, or core publication item.");
        if (payload.Publication.PublicationBook is { } book
            && (book.OutlineItems.GroupBy(item => item.Id).Any(group => group.Count() != 1)
                || book.OutlineItems.Any(item => !TargetExists(item.TargetKind, item.TargetId, actIds, chapterIds))
                || book.Matter?.GroupBy(item => item.Id).Any(group => group.Count() != 1) == true
                || book.ImagePlacements?.GroupBy(item => item.Id).Any(group => group.Count() != 1) == true
                || book.ImagePlacements?.Any(item => !imageIds.Contains(item.AssetId) || !TargetExists(item.TargetKind, item.TargetId, actIds, chapterIds)) == true))
            throw new VersionHistoryRestoreException("MissingPublicationDependency", "The target publication book references a missing asset, act, or chapter.");
        if (payload.Publication.PublicationEditions.Any(edition =>
            edition.OutlineItems.GroupBy(item => item.Id).Any(group => group.Count() != 1)
            || edition.OutlineItems.Any(item => !TargetExists(item.TargetKind, item.TargetId, actIds, chapterIds))
            || edition.ImagePlacements?.GroupBy(item => item.Id).Any(group => group.Count() != 1) == true
            || edition.ChapterOverrides.GroupBy(item => item.Id).Any(group => group.Count() != 1)
            || edition.PublicationSectionOrder.Keys.Any(id => !sectionIds.Contains(id))))
            throw new VersionHistoryRestoreException("DuplicatePublicationIdentity", "The target snapshot contains duplicate or dangling publication-child identities.");
        if (payload.Assets.Images.Any(item => item.DerivedFromImageId is Guid sourceImageId && !imageIds.Contains(sourceImageId)))
            throw new VersionHistoryRestoreException("MissingImageDependency", "A target image references a missing source image.");
        if (payload.Assets.Images.GroupBy(item => item.Id).Any(group => group.Count() != 1)
            || payload.Assets.EntityVisualExamples.GroupBy(item => $"{item.Entity.StableKey}/{item.ImageId:N}", StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new VersionHistoryRestoreException("DuplicateAssetIdentity", "The target snapshot contains duplicate asset identities.");
        if (payload.Assets.FontFamilies.Any(family => family.Faces.GroupBy(face => face.Id).Any(group => group.Count() != 1)))
            throw new VersionHistoryRestoreException("DuplicateFontIdentity", "The target snapshot contains duplicate font-face IDs.");
        if (payload.Manuscript.Styles.GroupBy(style => style.Id).Any(group => group.Count() != 1)
            || payload.Assets.FontFamilies.GroupBy(family => family.Id).Any(group => group.Count() != 1)
            || fontFaceIds.Count != payload.Assets.FontFamilies.SelectMany(family => family.Faces).Count())
            throw new VersionHistoryRestoreException("DuplicateCreativeIdentity", "The target snapshot contains duplicate style or font identities.");
        if (payload.Composition.PageCompositions.SelectMany(item => item.Variants).GroupBy(item => item.Id).Any(group => group.Count() != 1))
            throw new VersionHistoryRestoreException("DuplicateCompositionIdentity", "The target snapshot contains duplicate composition variant IDs.");
        if (payload.Composition.PageCompositions.Any(item => item.ChapterId is Guid chapterId && !chapterIds.Contains(chapterId)
            || item.EditionId is Guid editionId && !editionIds.Contains(editionId)
            || item.PublicationSectionId is Guid sectionId && !sectionIds.Contains(sectionId)
            || item.SourceCompositionId is Guid sourceId && !compositionIds.Contains(sourceId)
            || item.ActiveAuthoringVariantId is Guid variantId && !item.Variants.Any(variant => variant.Id == variantId)))
            throw new VersionHistoryRestoreException("MissingCompositionDependency", "A target composition references a missing creative row.");
        ValidateBlobRecords(payload);
        if (payload.Publication.PublicationEditions.Any(edition => edition.StyleMappings is { Count: > 0 }))
            throw new VersionHistoryRestoreException(
                "UnsupportedPublicationStyleMappings",
                "The current publication import boundary does not expose stable edition style-mapping identities for exact restore.");
        var knownGraphKeys = payload.Graph.Nodes
            .Select(node => node.NodeType + "/" + node.Key)
            .Concat([EntityTypeService.ProjectNodeType + "/" + payload.ProjectId.ToString("N")])
            .Concat(payload.Narrative.Acts.Select(act => EntityTypeService.ActNodeType + "/" + act.Id.ToString("N")))
            .Concat(payload.Narrative.Chapters.Select(chapter => EntityTypeService.ChapterNodeType + "/" + chapter.Id.ToString("N")))
            .Concat(payload.Sources.Sources.Select(source => EntityTypeService.SourceNodeType + "/" + source.Id.ToString("N")))
            .Concat(payload.Sources.Sources.SelectMany(source => source.Chunks.Select(chunk => EntityTypeService.SourceChunkNodeType + "/" + chunk.Id.ToString("N"))))
            .ToHashSet(StringComparer.Ordinal);
        if (payload.Graph.Nodes.GroupBy(node => node.NodeType + "/" + node.Key, StringComparer.Ordinal).Any(group => group.Count() != 1)
            || payload.Graph.Edges.Any(edge => !knownGraphKeys.Contains(edge.From.StableKey) || !knownGraphKeys.Contains(edge.To.StableKey)))
            throw new VersionHistoryRestoreException("MissingGraphDependency", "The target graph contains a node or edge with a missing endpoint.");
        if (payload.Assets.EntityVisualExamples.Any(example => !knownGraphKeys.Contains(example.Entity.StableKey)))
            throw new VersionHistoryRestoreException("MissingVisualEntity", "A target visual example references a missing graph entity.");

        static bool TargetExists(PublishOutlineTargetKind? kind, Guid? id, HashSet<Guid> acts, HashSet<Guid> chapters) =>
            kind switch
            {
                null => true,
                PublishOutlineTargetKind.Act => id is Guid actId && acts.Contains(actId),
                PublishOutlineTargetKind.Chapter => id is Guid chapterId && chapters.Contains(chapterId),
                _ => false,
            };

        static bool GuidSetContains<T>(IEnumerable<T>? values, Guid id) where T : notnull =>
            values is not null && values.Any(value => value switch
            {
                ProjectExportPublicationMatter matter => matter.Id == id,
                ProjectExportPublicationImagePlacement placement => placement.Id == id,
                _ => false,
            });

        static void ValidateBlobRecords(VersionHistorySnapshotPayload snapshot)
        {
            foreach (var image in snapshot.Assets.Images)
            {
                if (!snapshot.ImageData.TryGetValue(image.Id, out var data)
                    || data.LongLength != image.ByteLength
                    || !string.Equals(VersionHistoryCanonicalJson.Sha256Hex(data), image.Sha256, StringComparison.Ordinal))
                    throw new VersionHistoryRestoreException("InvalidImageBlob", $"Image blob metadata is missing or invalid for '{image.FileName}'.");
            }

            foreach (var face in snapshot.Assets.FontFamilies.SelectMany(family => family.Faces))
            {
                if (!snapshot.FontFaceData.TryGetValue(face.Id, out var data)
                    || data.LongLength != face.ByteLength
                    || !string.Equals(VersionHistoryCanonicalJson.Sha256Hex(data), face.Sha256, StringComparison.Ordinal))
                    throw new VersionHistoryRestoreException("InvalidFontBlob", $"Font blob metadata is missing or invalid for '{face.FileName}'.");
            }
        }
    }

    private static async Task RefuseQueuedWorkAsync(
        AppDbContext db,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        if (await db.IngestJobs.AnyAsync(item => item.ProjectId == projectId
            && (item.Status == IngestJobStatus.Queued || item.Status == IngestJobStatus.Running || item.Status == IngestJobStatus.StopRequested), cancellationToken)
            || await db.ProjectImportJobs.AnyAsync(item => item.ProjectId == projectId
                && (item.Status == ProjectImportJobStatus.Queued || item.Status == ProjectImportJobStatus.Running), cancellationToken)
            || await db.EditorRevisionJobs.AnyAsync(item => item.ProjectId == projectId
                && (item.Status == EditorRevisionJobStatus.Queued || item.Status == EditorRevisionJobStatus.Running), cancellationToken)
            || await db.ContestBatches.AnyAsync(item => item.ProjectId == projectId
                && (item.Status == ContestBatchStatus.Running
                    || item.Status == ContestBatchStatus.Completed
                    || item.Status == ContestBatchStatus.Failed), cancellationToken)
            || await db.ProjectImageGenerationJobs.AnyAsync(item => item.ProjectId == projectId
                && (item.Status == ProjectImageGenerationJobStatus.Queued || item.Status == ProjectImageGenerationJobStatus.Running), cancellationToken)
            || await db.PublicationPreparationJobs.AnyAsync(item => item.ProjectId == projectId
                && (item.Status == PublicationPreparationStatus.Queued || item.Status == PublicationPreparationStatus.Preparing), cancellationToken)
            || await db.PublicationRenderJobs.AnyAsync(item => item.ProjectId == projectId
                && (item.Status == PublicationRenderStatus.Queued || item.Status == PublicationRenderStatus.Rendering), cancellationToken)
            || await db.ProjectVersionRepositories.AnyAsync(item => item.ProjectId == projectId
                && item.Operations.Any(operation => operation.Status == ProjectVersionOperationStatus.Running), cancellationToken))
        {
            throw new VersionHistoryRestoreException(
                "WorkInProgress",
                "Restore is refused while queued or running project work exists.");
        }
    }

    private static async Task ValidateDatabaseIdentityAsync(
        AppDbContext db,
        VersionHistorySnapshotPayload payload,
        CancellationToken cancellationToken)
    {
        if (!await db.Projects.AnyAsync(item => item.Id == payload.ProjectId, cancellationToken))
            throw new VersionHistoryRestoreException("ProjectNotFound", "The project to restore no longer exists.");
        if (await db.Projects.AnyAsync(item => item.Id != payload.ProjectId && item.Slug == payload.Project.Project.Slug, cancellationToken))
            throw new VersionHistoryRestoreException("ProjectSlugConflict", "The target project slug is already used by another project.");
    }

    private static async Task ValidateImportDatabaseIdentityAsync(
        AppDbContext db,
        VersionHistorySnapshotPayload payload,
        CancellationToken cancellationToken)
    {
        if (await db.Projects.AnyAsync(item => item.Id == payload.ProjectId, cancellationToken))
            throw new VersionHistoryRestoreException(
                "ProjectIdentityCollision",
                "The imported project identity already exists locally; import will not overwrite it.");
        if (await db.ProjectVersionRepositories.AnyAsync(item => item.Id == payload.RepositoryId, cancellationToken))
            throw new VersionHistoryRestoreException(
                "RepositoryIdentityCollision",
                "The imported repository identity already exists locally; import will not overwrite it.");
        if (await db.Projects.AnyAsync(item => item.Slug == payload.Project.Project.Slug, cancellationToken))
            throw new VersionHistoryRestoreException(
                "ProjectSlugConflict",
                "The imported project slug is already used by another local project.");
        if (await db.ProjectVersionRepositories.AnyAsync(item => item.ProjectId == payload.ProjectId, cancellationToken))
            throw new VersionHistoryRestoreException(
                "RepositoryIdentityCollision",
                "A local version-history repository already belongs to the imported project identity.");
    }

    private static async Task ClearOperationalStateAsync(
        AppDbContext db,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        await db.ChatMessageImageAttachments.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.ProjectImageChatAttachments.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.EditorMessageVisuals.Where(item => item.Message.Conversation.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.PublishMessageVisuals.Where(item => item.Message.Conversation.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.ProjectImageMessageVisuals.Where(item => item.Message.Conversation.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.SourceVisualCandidates.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.ProjectImageMasks.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.ProjectImageGenerationJobs.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.ContestBatches.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.EditorRevisionJobs.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.ProjectImportJobs.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.CompositionMutationStages.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.PublicationPreparationJobs.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.PublicationRenderJobs.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.PublicationArtifacts.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.PublicationPageMapEntries.Where(item => item.RenderJob.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.IngestJobs.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.WebIngestCandidates.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.IngestStagingRecords.Where(item => item.Source.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.IngestVectorFragments.Where(item => item.Source.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
    }

    private static async Task ReplaceCanonicalStateAsync(
        AppDbContext db,
        VersionHistorySnapshotPayload payload,
        ICollection<VersionHistoryUnresolvedReference> unresolved,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var projectId = payload.ProjectId;
        await db.ProjectReferences.Where(item => item.ReferencingProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.ProjectPageSetups.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.BookBriefCanonSources.Where(item => item.BookBrief.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.ManuscriptAnnotations.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.EditorContextPreferences.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.EntityVisualExamples.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.GraphEdges.Where(item => item.FromNode.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.GraphNodes.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.GraphEntityTypes.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.PageCompositionVariants.Where(item => item.Composition.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.PageCompositions.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.PublicationEditionChapterOverrides.Where(item => item.Edition.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.PublicationMatter.Where(item => item.Edition.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.PublicationImagePlacements.Where(item => item.Edition.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.PublicationEditionOutlineItems.Where(item => item.Edition.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.PublicationEditionAuditEntries.Where(item => item.Edition.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.PublicationCoverDesigns.Where(item => item.Edition.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.PublicationSections.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.PublicationEditions.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.PublicationBookMatter.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.PublicationBookImagePlacements.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.PublicationBookOutlineItems.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.PublicationBookCoverDesigns.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.PublicationBookPdfPresentations.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.PublicationBooks.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.ProjectFontFaces.Where(item => item.Family.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.ProjectFontFamilies.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.ManuscriptStyleDefinitions.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.WritingSamples.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.Chapters.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.Acts.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.BookBriefs.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.IngestSources.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await db.PublishAssets.Where(item => item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);

        var project = await db.Projects.SingleAsync(item => item.Id == projectId, cancellationToken);
        project.Name = payload.Project.Project.Name;
        project.Slug = payload.Project.Project.Slug;
        project.ProjectGuidance = payload.Project.Project.ProjectGuidance;
        project.IncludeCurrentChapterInContext = payload.Project.Project.IncludeCurrentChapterInContext;
        // Workflow policy is local state. Keep the existing project's value;
        // schema-v1 snapshots may still contain the legacy input field, but it
        // must never change this setting during restore.
        project.ContestModeEnabled = payload.Project.ContestModeEnabled;
        project.UpdatedAt = DateTime.UtcNow;
        if (payload.Project.PageSetup is { } setup)
            db.ProjectPageSetups.Add(new ProjectPageSetup
            {
                ProjectId = projectId,
                PageWidthInches = setup.PageWidthInches,
                PageHeightInches = setup.PageHeightInches,
                PageMarginInches = setup.PageMarginInches,
                BodyFontSizePoints = setup.BodyFontSizePoints,
                BodyLineHeight = setup.BodyLineHeight,
            });

        AddBookBrief(db, projectId, payload.Narrative.BookBrief, payload.Narrative.BookBriefCanonSourceIds);
        foreach (var entityType in payload.Narrative.EntityTypes)
            db.GraphEntityTypes.Add(new GraphEntityType
            {
                ProjectId = projectId,
                Type = entityType.Type,
                SingularLabel = entityType.SingularLabel,
                PluralLabel = entityType.PluralLabel,
                Color = entityType.Color,
                Icon = entityType.Icon,
                IsStructural = entityType.IsStructural,
                IsChapterScoped = entityType.IsChapterScoped,
                SortOrder = entityType.SortOrder,
                DefaultProperties = entityType.DefaultProperties,
            });
        foreach (var act in payload.Narrative.Acts)
            db.Acts.Add(new Act { Id = act.Id, ProjectId = projectId, Title = act.Title, Synopsis = act.Synopsis, Order = act.Order });
        foreach (var chapter in payload.Narrative.Chapters)
            db.Chapters.Add(new Chapter
            {
                Id = chapter.Id,
                ProjectId = projectId,
                ActId = chapter.ActId,
                Title = chapter.Title,
                ManuscriptJson = chapter.ManuscriptJson,
                ManuscriptRevision = chapter.ManuscriptRevision,
                Synopsis = chapter.Synopsis,
                Order = chapter.Order,
                VectorIndexState = VectorIndexState.Stale,
            });
        foreach (var sample in payload.Narrative.WritingSamples)
            db.WritingSamples.Add(new WritingSample { Id = sample.Id, ProjectId = projectId, Title = sample.Title, Body = sample.Body });
        foreach (var preference in payload.Narrative.ContextPreferences)
            db.EditorContextPreferences.Add(new EditorContextPreference
            {
                Id = preference.Id,
                ProjectId = projectId,
                ChapterId = preference.ChapterId,
                Kind = preference.Kind,
                Key = preference.Key,
                IsIncluded = preference.IsIncluded,
                SortOrder = preference.SortOrder,
            });
        foreach (var annotation in payload.Narrative.Annotations)
            db.ManuscriptAnnotations.Add(new ManuscriptAnnotation
            {
                Id = annotation.Id,
                ProjectId = projectId,
                ChapterId = annotation.ChapterId,
                EditionId = annotation.EditionId,
                Kind = annotation.Kind,
                NoteText = annotation.NoteText,
                Revision = annotation.Revision,
                AnchorManuscriptRevision = annotation.AnchorManuscriptRevision,
                AnchorState = annotation.AnchorState,
                StartBlockId = annotation.StartBlockId,
                StartOffset = annotation.StartOffset,
                EndBlockId = annotation.EndBlockId,
                EndOffset = annotation.EndOffset,
                OriginalQuote = annotation.OriginalQuote,
                ContextBefore = annotation.ContextBefore,
                ContextAfter = annotation.ContextAfter,
            });
        AddSources(db, projectId, payload.Sources.Sources);
        AddAssets(db, projectId, payload);
        foreach (var style in payload.Manuscript.Styles)
            db.ManuscriptStyleDefinitions.Add(new ManuscriptStyleDefinition
            {
                Id = style.Id,
                ProjectId = projectId,
                Name = style.Name,
                NameKey = style.Name.ToUpperInvariant(),
                Kind = style.Kind,
                SemanticRole = style.SemanticRole,
                SemanticRoleKey = style.SemanticRole.ToUpperInvariant(),
                DefinitionJson = JsonSerializer.Serialize(style.Definition, ManuscriptCodec.JsonOptions),
                Revision = style.Revision,
            });
        AddCompositions(db, projectId, payload.Composition.PageCompositions);
        AddPublication(db, projectId, payload.Publication);
        await db.SaveChangesAsync(cancellationToken);
        await AddGraphAsync(db, projectId, payload.Graph, cancellationToken);
        await AddVisualExamplesAsync(db, projectId, payload.Assets.EntityVisualExamples, cancellationToken);
        if (payload.Assets.EntityVisualExamples.Count > 0)
            warnings.Add("Entity visual examples were restored with new local row identities because the snapshot export boundary does not expose their database IDs or source-candidate links.");
        await db.SaveChangesAsync(cancellationToken);
        await RelinkReferencesAsync(db, projectId, payload.Project.References, unresolved, warnings, cancellationToken);
    }

    private static void AddBookBrief(
        AppDbContext db,
        Guid projectId,
        ProjectExportBookBrief? source,
        IReadOnlyList<Guid> canonicalSourceIds)
    {
        if (source is null) return;
        var brief = new BookBrief
        {
            ProjectId = projectId,
            BookKind = source.BookKind,
            Premise = source.Premise,
            Genre = source.Genre,
            PrimaryThemes = source.PrimaryThemes,
            Purpose = source.Purpose,
            CreativeConstraints = source.CreativeConstraints,
            TargetAudience = source.TargetAudience,
            MinimumReaderAge = source.MinimumReaderAge,
            MaximumReaderAge = source.MaximumReaderAge,
            ReadingLevelGuidance = source.ReadingLevelGuidance,
            TargetWordCount = source.TargetWordCount,
            PointOfView = source.PointOfView,
            Tense = source.Tense,
            VoiceAndTone = source.VoiceAndTone,
            LanguageLocale = source.LanguageLocale,
            HouseStyle = source.HouseStyle,
            ReadAloudPriority = source.ReadAloudPriority,
            AccessibilityGoals = source.AccessibilityGoals,
            VisualDirection = source.VisualDirection,
        };
        brief.CanonSources = canonicalSourceIds.Select(id => new BookBriefCanonSource { IngestSourceId = id }).ToList();
        db.BookBriefs.Add(brief);
    }

    private static void AddSources(AppDbContext db, Guid projectId, IReadOnlyList<ProjectExportIngestSource> sources)
    {
        foreach (var source in sources)
        {
            var entity = new IngestSource
            {
                Id = source.Id,
                ProjectId = projectId,
                Title = source.Title,
                SourceKind = source.SourceKind,
                Description = source.Description,
                Synopsis = source.Synopsis,
                UserInstructions = source.UserInstructions,
                SourceText = source.SourceText,
                SourceHash = source.SourceHash,
                SourceUrl = source.SourceUrl,
                FinalUrl = source.FinalUrl,
                CanonicalUrl = source.CanonicalUrl,
                // Fetch timing is operational provenance, not versioned
                // creative state. It is intentionally reset on restore/import.
                FetchedAt = null,
                ContentType = source.ContentType,
                SourceMetadataJson = source.SourceMetadataJson,
                VectorIndexState = VectorIndexState.Stale,
            };
            entity.SourceChunks = source.Chunks.Select(chunk => new IngestSourceChunk
            {
                Id = chunk.Id,
                Source = entity,
                Index = chunk.Index,
                Title = chunk.Title,
                HeadingPath = chunk.HeadingPath,
                StartChar = chunk.StartChar,
                EndChar = chunk.EndChar,
                EstimatedTokenCount = chunk.EstimatedTokenCount,
                TokenCountMethod = chunk.TokenCountMethod,
                TokenEncodingName = chunk.TokenEncodingName,
                TokenCountIsExact = chunk.TokenCountIsExact,
                Summary = chunk.Summary,
                AgentNotes = chunk.AgentNotes,
                StructureStatus = chunk.StructureStatus,
            }).ToList();
            entity.SourcePages = source.Pages.Select(page => new IngestSourcePage
            {
                Id = page.Id,
                Source = entity,
                PageNumber = page.PageNumber,
                Text = page.Text,
                StartChar = page.StartChar,
                EndChar = page.EndChar,
                ExtractionMethod = page.ExtractionMethod,
                Width = page.Width,
                Height = page.Height,
                ImageHash = page.ImageHash,
                RenderSettingsJson = page.RenderSettingsJson,
                VisionModelName = page.VisionModelName,
                Diagnostics = page.Diagnostics,
            }).ToList();
            entity.SourceBlocks = source.Blocks.Select(block => new IngestSourceBlock
            {
                Id = block.Id,
                Source = entity,
                SourcePageId = block.SourcePageId,
                Index = block.Index,
                Kind = block.Kind,
                Title = block.Title,
                Locator = block.Locator,
                PageNumber = block.PageNumber,
                StartChar = block.StartChar,
                EndChar = block.EndChar,
                MetadataJson = block.MetadataJson,
            }).ToList();
            db.IngestSources.Add(entity);
        }
    }

    private static void AddAssets(AppDbContext db, Guid projectId, VersionHistorySnapshotPayload payload)
    {
        foreach (var image in payload.Assets.Images)
        {
            if (!payload.ImageData.TryGetValue(image.Id, out var imageData))
                throw new VersionHistoryRestoreException("MissingImageBlob", $"Image blob is missing for '{image.FileName}'.");
            db.PublishAssets.Add(new PublishAsset
            {
                Id = image.Id,
                ProjectId = projectId,
                FileName = image.FileName,
                ContentType = image.ContentType,
                Data = imageData,
                AltText = image.AltText,
                Source = image.Source,
                Prompt = image.Prompt,
                GenerationModel = image.GenerationModel,
                SourceMetadataJson = image.SourceMetadataJson,
                DerivedFromImageId = image.DerivedFromImageId,
                CropXPercent = image.CropXPercent,
                CropYPercent = image.CropYPercent,
                CropWidthPercent = image.CropWidthPercent,
                CropHeightPercent = image.CropHeightPercent,
            });
        }
        foreach (var family in payload.Assets.FontFamilies)
        {
            var entity = new ProjectFontFamily
            {
                Id = family.Id,
                ProjectId = projectId,
                Name = family.Name,
                EmbeddingRightsConfirmed = family.EmbeddingRightsConfirmed,
                RightsDeclaration = family.RightsDeclaration,
            };
            entity.Faces = family.Faces.Select(face =>
            {
                if (!payload.FontFaceData.TryGetValue(face.Id, out var faceData))
                    throw new VersionHistoryRestoreException("MissingFontBlob", $"Font blob is missing for '{face.FileName}'.");
                return new ProjectFontFace
                {
                    Id = face.Id,
                    Family = entity,
                    SubfamilyName = face.SubfamilyName,
                    FileName = face.FileName,
                    ContentType = face.ContentType,
                    Weight = face.Weight,
                    Italic = face.Italic,
                    Data = faceData,
                };
            }).ToList();
            db.ProjectFontFamilies.Add(entity);
        }
    }

    private static void AddCompositions(AppDbContext db, Guid projectId, IReadOnlyList<ProjectExportPageComposition> compositions)
    {
        foreach (var composition in compositions)
        {
            var entity = new PageComposition
            {
                Id = composition.Id,
                ProjectId = projectId,
                ChapterId = composition.ChapterId,
                PublicationSectionId = composition.PublicationSectionId,
                EditionId = composition.EditionId,
                SourceCompositionId = composition.SourceCompositionId,
                Name = composition.Name,
                SemanticManuscriptJson = composition.SemanticManuscriptJson,
                Revision = composition.Revision,
                ActiveAuthoringVariantId = composition.ActiveAuthoringVariantId,
            };
            entity.Variants = composition.Variants.Select(variant => new PageCompositionVariant
            {
                Id = variant.Id,
                Composition = entity,
                GeometryKey = variant.GeometryKey,
                SceneJson = variant.SceneJson,
                Revision = variant.Revision,
            }).ToList();
            db.PageCompositions.Add(entity);
        }
    }

    private static void AddPublication(AppDbContext db, Guid projectId, VersionHistorySnapshotPublicationArea publication)
    {
        if (publication.PublicationBook is { } source)
        {
            var book = new PublicationBook
            {
                ProjectId = projectId,
                Revision = source.Revision,
                Title = source.Title,
                Subtitle = source.Subtitle,
                Author = source.Author,
                Language = source.Language,
                Publisher = source.Publisher,
                Copyright = source.Copyright,
                Description = source.Description,
                IncludeTableOfContents = source.IncludeTableOfContents,
                IncludeVisibleTableOfContents = source.IncludeVisibleTableOfContents,
                IncludeActSynopses = source.IncludeActSynopses,
                IncludeChapterSynopses = source.IncludeChapterSynopses,
                IncludeActHeadings = source.IncludeActHeadings,
                IncludeChapterHeadings = source.IncludeChapterHeadings,
                NumberActs = source.NumberActs,
                NumberChapters = source.NumberChapters,
                TitlePageMode = source.TitlePageMode,
                RectoChapterStarts = source.RectoChapterStarts,
                OutlineItems = source.OutlineItems.Select(item => new PublicationBookOutlineItem
                {
                    Id = item.Id,
                    ProjectId = projectId,
                    TargetKind = item.TargetKind,
                    TargetId = item.TargetId,
                    ActId = item.TargetKind == PublishOutlineTargetKind.Act ? item.TargetId : null,
                    ChapterId = item.TargetKind == PublishOutlineTargetKind.Chapter ? item.TargetId : null,
                    IsIncluded = item.IsIncluded,
                    SortOrder = item.SortOrder,
                }).ToList(),
                Matter = source.Matter?.Select(item => new PublicationBookMatter
                {
                    Id = item.Id,
                    ProjectId = projectId,
                    Location = item.Location,
                    Kind = item.Kind,
                    Title = item.Title,
                    ManuscriptJson = item.ManuscriptJson,
                    Revision = item.Revision,
                    IsIncluded = item.IsIncluded,
                    SortOrder = item.SortOrder,
                }).ToList() ?? [],
                ImagePlacements = source.ImagePlacements?.Select(item => new PublicationBookImagePlacement
                {
                    Id = item.Id,
                    ProjectId = projectId,
                    AssetId = item.AssetId,
                    TargetKind = item.TargetKind,
                    TargetId = item.TargetId,
                    ActId = item.TargetKind == PublishOutlineTargetKind.Act ? item.TargetId : null,
                    ChapterId = item.TargetKind == PublishOutlineTargetKind.Chapter ? item.TargetId : null,
                    PlacementKind = item.PlacementKind,
                    SortOrder = item.SortOrder,
                    Caption = item.Caption,
                    PresentationJson = JsonSerializer.Serialize(item.Presentation ?? new FigurePresentation(), ManuscriptCodec.JsonOptions),
                    AltText = item.AltText,
                    Decorative = item.Decorative,
                    Language = item.Language,
                    AccessibilityRole = item.AccessibilityRole,
                }).ToList() ?? [],
            };
            if (source.CoverDesign is { } cover)
                book.CoverDesign = new PublicationBookCoverDesign
                {
                    ProjectId = projectId,
                    BackgroundColor = cover.BackgroundColor,
                    CompositionSceneJson = cover.CompositionSceneJson,
                    Revision = cover.Revision,
                };
            book.PdfPresentation = new PublicationBookPdfPresentation
            {
                ProjectId = projectId,
                AllowDesignedPageOverrides = source.AllowDesignedPageOverrides,
            };
            db.PublicationBooks.Add(book);
        }

        foreach (var editionData in publication.PublicationEditions)
        {
            var edition = new PublicationEdition
            {
                Id = editionData.Id,
                ProjectId = projectId,
                Name = editionData.Name,
                Format = editionData.Format,
                Vendor = editionData.Vendor,
                VendorProfileVersion = editionData.VendorProfileVersion,
                Status = editionData.Status,
                Revision = editionData.Revision,
                TitleOverride = editionData.TitleOverride,
                Subtitle = editionData.Subtitle,
                Author = editionData.Author,
                Language = editionData.Language,
                Publisher = editionData.Publisher,
                Copyright = editionData.Copyright,
                Isbn = editionData.Isbn,
                Description = editionData.Description,
                IncludeTableOfContents = editionData.IncludeTableOfContents,
                IncludeVisibleTableOfContents = editionData.IncludeVisibleTableOfContents,
                IncludeActSynopses = editionData.IncludeActSynopses,
                IncludeChapterSynopses = editionData.IncludeChapterSynopses,
                IncludeActHeadings = editionData.IncludeActHeadings,
                IncludeChapterHeadings = editionData.IncludeChapterHeadings,
                NumberActs = editionData.NumberActs,
                NumberChapters = editionData.NumberChapters,
                TitlePageMode = editionData.TitlePageMode,
                PageWidthInches = editionData.PageWidthInches,
                PageHeightInches = editionData.PageHeightInches,
                PageMarginInches = editionData.PageMarginInches,
                SelectedCoverImageId = editionData.SelectedCoverImageId,
                PrintArtifactRegistryVersion = editionData.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover
                    ? PrintArtifactProfileRegistry.CurrentVersion
                    : string.Empty,
                PrintArtifactProfileKey = RestoredPrintArtifactProfileKey(editionData),
                PrintCoverMode = editionData.PrintCoverMode,
                PrintProjectUse = editionData.PrintProjectUse,
                PrintIdentifierMode = editionData.PrintIdentifierMode,
                PrintCoverSubmissionMode = editionData.PrintCoverSubmissionMode,
                PrintTemplateEvidenceJson = NormalizePrintTemplateEvidenceJson(
                    string.IsNullOrWhiteSpace(editionData.PrintTemplateEvidenceJson)
                        ? editionData.LegacyGenericPrintTemplateJson ?? string.Empty
                        : editionData.PrintTemplateEvidenceJson),
                Bleed = editionData.Bleed,
                AllowDesignedPageOverrides = editionData.AllowDesignedPageOverrides,
                RectoChapterStarts = editionData.RectoChapterStarts,
                InheritsCoreCover = editionData.InheritsCoreCover,
                EditionSpecificContentEnabled = editionData.EditionSpecificContentEnabled,
                OverrideFieldsJson = JsonSerializer.Serialize(editionData.OverrideFields),
                PublicationSectionOrderJson = JsonSerializer.Serialize(editionData.PublicationSectionOrder, ManuscriptCodec.JsonOptions),
            };
            edition.OutlineItems = editionData.OutlineItems.Select(item => new PublicationEditionOutlineItem
            {
                Id = item.Id,
                Edition = edition,
                TargetKind = item.TargetKind,
                TargetId = item.TargetId,
                ActId = item.TargetKind == PublishOutlineTargetKind.Act ? item.TargetId : null,
                ChapterId = item.TargetKind == PublishOutlineTargetKind.Chapter ? item.TargetId : null,
                IsIncluded = item.IsIncluded,
                SortOrder = item.SortOrder,
            }).ToList();
            edition.Matter = editionData.Matter?.Select(item => new PublicationMatter
            {
                Id = item.Id,
                Edition = edition,
                CoreMatterId = item.CoreMatterId,
                IsExcluded = item.IsExcluded,
                Location = item.Location,
                Kind = item.Kind,
                Title = item.Title,
                ManuscriptJson = item.ManuscriptJson,
                Revision = item.Revision,
                IsIncluded = item.IsIncluded,
                SortOrder = item.SortOrder,
            }).ToList() ?? [];
            edition.ImagePlacements = editionData.ImagePlacements?.Select(item => new PublicationImagePlacement
            {
                Id = item.Id,
                Edition = edition,
                CorePlacementId = item.CorePlacementId,
                IsExcluded = item.IsExcluded,
                AssetId = item.AssetId,
                TargetKind = item.TargetKind,
                TargetId = item.TargetId,
                    ActId = item.TargetKind == PublishOutlineTargetKind.Act ? item.TargetId : null,
                    ChapterId = item.TargetKind == PublishOutlineTargetKind.Chapter ? item.TargetId : null,
                PlacementKind = item.PlacementKind,
                SortOrder = item.SortOrder,
                Caption = item.Caption,
                PresentationJson = JsonSerializer.Serialize(item.Presentation ?? new FigurePresentation(), ManuscriptCodec.JsonOptions),
                AltText = item.AltText,
                Decorative = item.Decorative,
                Language = item.Language,
                AccessibilityRole = item.AccessibilityRole,
            }).ToList() ?? [];
            edition.ChapterOverrides = editionData.ChapterOverrides.Select(item => new PublicationEditionChapterOverride
            {
                Id = item.Id,
                Edition = edition,
                ChapterId = item.ChapterId,
                ManuscriptJson = item.ManuscriptJson,
                Revision = item.Revision,
                BaseCoreRevision = item.BaseCoreRevision,
                BaseCoreHash = item.BaseCoreHash,
            }).ToList();
            if (editionData.CoverDesign is { } cover)
                edition.CoverDesign = new PublicationCoverDesign
                {
                    Edition = edition,
                    InheritsCoreFront = editionData.InheritsCoreCover,
                    Title = cover.Title,
                    Subtitle = cover.Subtitle,
                    Author = cover.Author,
                    SpineText = cover.SpineText,
                    BackCopy = cover.BackCopy,
                    BackgroundColor = cover.BackgroundColor,
                    BarcodeMode = cover.BarcodeMode,
                    ImageCropXPercent = cover.ImageCropXPercent,
                    ImageCropYPercent = cover.ImageCropYPercent,
                    CompositionSceneJson = cover.CompositionSceneJson,
                    SurfaceScenesJson = cover.SurfaceScenesJson,
                    Revision = cover.Revision,
                };
            db.PublicationEditions.Add(edition);
        }

        foreach (var sectionData in publication.PublicationSections)
            db.PublicationSections.Add(new PublicationSection
            {
                Id = sectionData.Id,
                ProjectId = projectId,
                EditionId = sectionData.EditionId,
                CoreSectionId = sectionData.CoreSectionId,
                IsExcluded = sectionData.IsExcluded,
                Title = sectionData.Title,
                Kind = sectionData.Kind,
                SystemRole = sectionData.SystemRole,
                Anchor = sectionData.Anchor,
                TargetKind = sectionData.TargetKind,
                TargetId = sectionData.TargetId,
                ActId = sectionData.TargetKind == PublishOutlineTargetKind.Act ? sectionData.TargetId : null,
                ChapterId = sectionData.TargetKind == PublishOutlineTargetKind.Chapter ? sectionData.TargetId : null,
                InclusionMode = sectionData.InclusionMode,
                StartSide = sectionData.StartSide,
                LocalOrder = sectionData.LocalOrder,
                ManuscriptJson = sectionData.ManuscriptJson,
                Revision = sectionData.Revision,
            });
    }

    private static async Task AddGraphAsync(
        AppDbContext db,
        Guid projectId,
        VersionHistorySnapshotGraphArea source,
        CancellationToken cancellationToken)
    {
        foreach (var node in source.Nodes)
            db.GraphNodes.Add(new GraphNode
            {
                ProjectId = projectId,
                NodeType = node.NodeType,
                Key = node.Key,
                Label = node.Label,
                Properties = node.Properties,
            });
        await db.SaveChangesAsync(cancellationToken);
        var nodes = await db.GraphNodes.Where(item => item.ProjectId == projectId).ToDictionaryAsync(item => $"{item.NodeType}/{item.Key}", StringComparer.Ordinal, cancellationToken);
        foreach (var edge in source.Edges)
        {
            if (!nodes.TryGetValue(edge.From.StableKey, out var from) || !nodes.TryGetValue(edge.To.StableKey, out var to))
                continue;
            db.GraphEdges.Add(new GraphEdge
            {
                FromNodeId = from.Id,
                ToNodeId = to.Id,
                EdgeType = edge.EdgeType,
                Properties = edge.Properties,
                SortOrder = edge.SortOrder,
            });
        }
    }

    private static async Task AddVisualExamplesAsync(
        AppDbContext db,
        Guid projectId,
        IReadOnlyList<ProjectExportEntityVisualExample> examples,
        CancellationToken cancellationToken)
    {
        if (examples.Count == 0) return;

        var keys = examples
            .Select(example => example.Entity.StableKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var nodes = (await db.GraphNodes
            .Where(node => node.ProjectId == projectId)
            .ToListAsync(cancellationToken))
            .Where(node => keys.Contains(node.NodeType + "/" + node.Key, StringComparer.Ordinal))
            .ToDictionary(node => node.NodeType + "/" + node.Key, StringComparer.Ordinal);
        foreach (var example in examples)
        {
            if (!nodes.TryGetValue(example.Entity.StableKey, out var node))
                throw new VersionHistoryRestoreException("MissingVisualEntity", $"Visual example entity '{example.Entity.StableKey}' was not restored.");
            db.EntityVisualExamples.Add(new EntityVisualExample
            {
                ProjectId = projectId,
                GraphNodeId = node.Id,
                ImageId = example.ImageId,
                Label = example.Label,
                SortOrder = example.SortOrder,
                Origin = example.Origin,
            });
        }
    }

    private static async Task RelinkReferencesAsync(
        AppDbContext db,
        Guid projectId,
        IReadOnlyList<VersionHistoryProjectReference> references,
        ICollection<VersionHistoryUnresolvedReference> unresolved,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var repositoryIds = references
            .Where(item => item.ReferencedRepositoryId is Guid repositoryId && repositoryId != Guid.Empty)
            .Select(item => item.ReferencedRepositoryId!.Value)
            .ToList();
        var projects = await db.ProjectVersionRepositories
            .Where(item => repositoryIds.Contains(item.Id))
            .Select(item => new { item.Id, item.ProjectId })
            .ToDictionaryAsync(item => item.Id, item => item.ProjectId, cancellationToken);
        foreach (var reference in references
            .OrderBy(item => item.ReferencedRepositoryId ?? Guid.Empty)
            .ThenBy(item => item.ReferencedProjectId))
        {
            if (reference.ReferencedRepositoryId is not Guid repositoryId || repositoryId == Guid.Empty)
            {
                unresolved.Add(new VersionHistoryUnresolvedReference(
                    reference.ReferencedProjectId,
                    reference.ReferencedRepositoryId,
                    reference.ReferencedProjectName,
                    reference.ReferencedProjectSlug,
                    "The snapshot does not contain a target repository identity."));
                warnings.Add($"Project reference '{reference.ReferencedProjectName}' was not restored because it has no repository identity.");
                continue;
            }

            var hasLocalRepository = projects.TryGetValue(repositoryId, out var resolvedProjectId);
            var isLocal = hasLocalRepository && resolvedProjectId == reference.ReferencedProjectId;
            db.ProjectReferences.Add(new ProjectReference
            {
                Id = reference.ReferenceId,
                ReferencingProjectId = projectId,
                ReferencedRepositoryId = repositoryId,
                ReferencedProjectId = reference.ReferencedProjectId,
                ResolvedProjectId = isLocal ? resolvedProjectId : null,
                ReferencedProjectName = reference.ReferencedProjectName,
                ReferencedProjectSlug = reference.ReferencedProjectSlug,
                ResolvedAt = isLocal ? DateTime.UtcNow : null,
            });
            if (!isLocal)
            {
                unresolved.Add(new VersionHistoryUnresolvedReference(
                    reference.ReferencedProjectId,
                    reference.ReferencedRepositoryId,
                    reference.ReferencedProjectName,
                    reference.ReferencedProjectSlug,
                    hasLocalRepository
                        ? "The local repository identity resolved to a different project identity."
                        : "No local project matched the recorded repository identity."));
                warnings.Add(hasLocalRepository
                    ? $"Project reference '{reference.ReferencedProjectName}' remains unresolved because its repository resolves to a different project identity."
                    : $"Project reference '{reference.ReferencedProjectName}' remains unresolved because its repository identity is not local.");
            }
        }
    }

    private async Task RebuildProjectionsAsync(
        Guid projectId,
        VersionHistorySnapshotPayload payload,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            await outlineGraphSync.RepairProjectAsync(projectId, cancellationToken);
            await searchIndex.DeleteByScopeAsync(Project.ScopeKey(projectId), cancellationToken);
            foreach (var act in payload.Narrative.Acts)
                await contextIndexing.ReindexActAsync(act.Id, cancellationToken);
            foreach (var chapter in payload.Narrative.Chapters)
                await contextIndexing.ReindexChapterAsync(chapter.Id, cancellationToken);
            List<IngestSource> sourceEntities;
            await using (var sourceRead = await database.OpenReadAsync(cancellationToken))
            {
                sourceEntities = await sourceRead.Db.IngestSources
                    .AsNoTracking()
                    .Include(source => source.SourceChunks)
                    .Include(source => source.SourceBlocks)
                    .Where(source => source.ProjectId == projectId)
                    .ToListAsync(cancellationToken);
            }
            foreach (var source in sourceEntities)
            {
                await ingestGraphSync.EnsureSourceAsync(source, source.SourceChunks.ToList(), source.SourceBlocks.ToList(), cancellationToken);
                await contextIndexing.ReindexIngestSourceAsync(source.Id, cancellationToken);
            }
            foreach (var node in payload.Graph.Nodes)
                await graph.UpsertNodeAsync(
                    projectId,
                    node.NodeType,
                    node.Key,
                    node.Label,
                    node.Properties,
                    cancellationToken);
            foreach (var edge in payload.Graph.Edges)
            {
                var from = await graph.FindNodeAsync(projectId, edge.From.NodeType, edge.From.Key, cancellationToken)
                    ?? throw new VersionHistoryRestoreException("MissingGraphDependency", $"Graph node '{edge.From.StableKey}' was not rebuilt.");
                var to = await graph.FindNodeAsync(projectId, edge.To.NodeType, edge.To.Key, cancellationToken)
                    ?? throw new VersionHistoryRestoreException("MissingGraphDependency", $"Graph node '{edge.To.StableKey}' was not rebuilt.");
                await graph.UpsertEdgeAsync(from.Id, to.Id, edge.EdgeType, edge.Properties, edge.SortOrder, cancellationToken);
            }
            foreach (var act in payload.Narrative.Acts)
                await autoLinks.RefreshSourceAsync(projectId, ProjectSearchSourceTypes.Act, act.Id, cancellationToken);
            foreach (var chapter in payload.Narrative.Chapters)
                await autoLinks.RefreshSourceAsync(projectId, ProjectSearchSourceTypes.Chapter, chapter.Id, cancellationToken);
            foreach (var source in payload.Sources.Sources)
                await autoLinks.RefreshSourceAsync(projectId, ProjectSearchSourceTypes.RawIngestSource, source.Id, cancellationToken);
            foreach (var node in payload.Graph.Nodes)
            {
                if (Guid.TryParseExact(node.Key, "N", out var entityId))
                    await contextIndexing.ReindexEntityAsync(projectId, entityId, cancellationToken);
            }
            foreach (var sample in payload.Narrative.WritingSamples)
                await contextIndexing.ReindexWritingSampleAsync(sample.Id, cancellationToken);
            await contextIndexing.ReindexProjectProfileAsync(projectId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            warnings.Add($"Canonical versioned data was applied, but derived projection rebuild was incomplete; retry the restore or run projection repair before relying on search/context results. Details: {exception.Message}");
        }
    }

    private static ProjectVersionCheckpointView ToCheckpointView(ProjectVersionCheckpoint checkpoint) => new(
        checkpoint.Id,
        checkpoint.ProjectVersionRepositoryId,
        checkpoint.ManifestSchemaVersion,
        checkpoint.CreativeRevision,
        checkpoint.ContentHash,
        checkpoint.ManifestHash,
        checkpoint.CommitSha,
        checkpoint.ParentCommitSha,
        checkpoint.Kind,
        checkpoint.Source,
        checkpoint.Message,
        checkpoint.CreatedAt);

    private static string RestoredPrintArtifactProfileKey(ProjectExportPublicationEdition edition)
    {
        var key = edition.ImportedPrintArtifactProfileKey;
        if (!string.IsNullOrWhiteSpace(key))
        {
            return key switch
            {
                "kdp-pb-bw-white" => "kdp-pb-bw-50-2252",
                "kdp-pb-bw-cream" => "kdp-pb-bw-50-2500",
                "kdp-pb-bw-groundwood" => "kdp-pb-bw-45-2350",
                "kdp-hc-bw-white" => "kdp-hc-bw-50-2252",
                "kdp-hc-bw-cream" => "kdp-hc-bw-50-2500",
                "ingram-pb-bw-white50" => "ingram-pb-bw-50-2009",
                "ingram-pb-bw-cream50" => "ingram-pb-bw-50-2225",
                "ingram-pb-bw-groundwood38" => "ingram-pb-bw-38-2550",
                "ingram-hc-case-bw-white50" => "ingram-hc-case-bw-50-2009",
                "ingram-hc-case-bw-cream50" => "ingram-hc-case-bw-50-2224",
                "ingram-hc-cloth-blue" or "ingram-hc-cloth-gray" => "ingram-hc-cloth-bw-50-2009",
                "ingram-hc-cloth-blue-jacket" or "ingram-hc-cloth-gray-jacket" => "ingram-hc-cloth-jacket-bw-50-2009",
                "bn-pb-bw-cream50-6x9" => "bn-pb-bw-50-6x9",
                "bn-hc-case-bw-cream50-6x9" => "bn-hc-case-bw-50-6x9",
                "bn-hc-jacket-bw-cream50-6x9" => "bn-hc-jacket-bw-50-6x9",
                _ => key,
            };
        }

        return (edition.Format, edition.Vendor, edition.Paper, edition.Ink) switch
        {
            (PublicationEditionFormat.Paperback, PublicationVendor.AmazonKdp, LegacyPublicationPaper.Cream, _) => "kdp-pb-bw-50-2500",
            (PublicationEditionFormat.Paperback, PublicationVendor.AmazonKdp, _, LegacyPublicationInk.Color) => "kdp-pb-premium-color",
            (PublicationEditionFormat.Paperback, PublicationVendor.AmazonKdp, _, _) => "kdp-pb-bw-50-2252",
            (PublicationEditionFormat.Paperback, PublicationVendor.IngramSpark, LegacyPublicationPaper.Cream, _) => "ingram-pb-bw-50-2225",
            (PublicationEditionFormat.Paperback, PublicationVendor.IngramSpark, _, LegacyPublicationInk.Color) => "ingram-pb-premium70",
            (PublicationEditionFormat.Paperback, PublicationVendor.IngramSpark, _, _) => "ingram-pb-bw-50-2009",
            (PublicationEditionFormat.Paperback, _, _, _) => "generic-perfectbound-template",
            (PublicationEditionFormat.Hardcover, PublicationVendor.AmazonKdp, LegacyPublicationPaper.Cream, _) => "kdp-hc-bw-50-2500",
            (PublicationEditionFormat.Hardcover, PublicationVendor.AmazonKdp, _, LegacyPublicationInk.Color) => "kdp-hc-premium-color",
            (PublicationEditionFormat.Hardcover, PublicationVendor.AmazonKdp, _, _) => "kdp-hc-bw-50-2252",
            (PublicationEditionFormat.Hardcover, PublicationVendor.IngramSpark, LegacyPublicationPaper.Cream, _) => "ingram-hc-case-bw-50-2224",
            (PublicationEditionFormat.Hardcover, PublicationVendor.IngramSpark, _, LegacyPublicationInk.Color) => "ingram-hc-case-premium70",
            (PublicationEditionFormat.Hardcover, PublicationVendor.IngramSpark, _, _) => "ingram-hc-case-bw-50-2009",
            (PublicationEditionFormat.Hardcover, _, _, _) => "generic-casebound-template",
            _ => string.Empty,
        };
    }

    private static string NormalizePrintTemplateEvidenceJson(string json) => string.IsNullOrWhiteSpace(json)
        ? string.Empty
        : json.Replace("\"productKey\":", "\"artifactProfileKey\":", StringComparison.Ordinal)
            .Replace("ingram-hc-cloth-blue-jacket", "ingram-hc-cloth-jacket-bw-50-2009", StringComparison.Ordinal)
            .Replace("ingram-hc-cloth-gray-jacket", "ingram-hc-cloth-jacket-bw-50-2009", StringComparison.Ordinal)
            .Replace("ingram-hc-cloth-blue", "ingram-hc-cloth-bw-50-2009", StringComparison.Ordinal)
            .Replace("ingram-hc-cloth-gray", "ingram-hc-cloth-bw-50-2009", StringComparison.Ordinal)
            .Replace("bn-pb-bw-cream50-6x9", "bn-pb-bw-50-6x9", StringComparison.Ordinal)
            .Replace("bn-hc-case-bw-cream50-6x9", "bn-hc-case-bw-50-6x9", StringComparison.Ordinal)
            .Replace("bn-hc-jacket-bw-cream50-6x9", "bn-hc-jacket-bw-50-6x9", StringComparison.Ordinal);
}
