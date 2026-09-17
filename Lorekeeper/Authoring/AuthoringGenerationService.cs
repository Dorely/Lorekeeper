using Lorekeeper.Persistence;

namespace Lorekeeper.Authoring;

public interface IAuthoringGenerationService
{
    Task<IReadOnlyDictionary<string, long>> ReadAsync(
        Guid projectId,
        IReadOnlyCollection<string> targetIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, long>> InvalidateAsync(
        Guid projectId,
        IReadOnlyCollection<string> targetIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, long>> StageInvalidationAsync(
        AppDbContext db,
        Guid projectId,
        IReadOnlyCollection<string> targetIds,
        CancellationToken cancellationToken = default);

    void CompleteInvalidation(IReadOnlyCollection<string> targetIds);
}

internal sealed class AuthoringGenerationService(
    IAppDatabaseOperationFactory database,
    IAuthoringDeltaHistoryRuntime history) : IAuthoringGenerationService
{
    public async Task<IReadOnlyDictionary<string, long>> ReadAsync(
        Guid projectId,
        IReadOnlyCollection<string> targetIds,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        return await AuthoringPersistence.ReadGenerationsAsync(
            operation.Db,
            projectId,
            targetIds,
            cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, long>> InvalidateAsync(
        Guid projectId,
        IReadOnlyCollection<string> targetIds,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenWriteAsync(projectId, cancellationToken);
        var distinct = targetIds.Distinct(StringComparer.Ordinal).ToList();
        var result = await StageInvalidationAsync(
            operation.Db,
            projectId,
            distinct,
            cancellationToken);
        await operation.SaveChangesAsync(cancellationToken);
        CompleteInvalidation(distinct);
        return result;
    }

    public async Task<IReadOnlyDictionary<string, long>> StageInvalidationAsync(
        AppDbContext db,
        Guid projectId,
        IReadOnlyCollection<string> targetIds,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var targetId in targetIds.Distinct(StringComparer.Ordinal))
        {
            _ = AuthoringPersistence.ParseTarget(projectId, targetId);
            result[targetId] = await AuthoringPersistence.IncrementGenerationAsync(
                db,
                projectId,
                targetId,
                cancellationToken);
        }
        return result;
    }

    public void CompleteInvalidation(IReadOnlyCollection<string> targetIds)
    {
        foreach (var targetId in targetIds.Distinct(StringComparer.Ordinal))
            history.Clear(targetId);
    }
}
