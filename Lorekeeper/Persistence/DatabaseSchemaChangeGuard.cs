using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence;

public interface IDatabaseSchemaChangeGuard
{
    Task EnsureSchemaUnchangedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Opt-in development guard for running a build against a database owned by an
/// installed release. When enabled, startup stops before any recovery, repair,
/// or migration write unless the database's applied EF migrations exactly
/// match this build's.
/// </summary>
public sealed class DatabaseSchemaChangeGuard(
    IAppDatabaseOperationFactory database,
    bool blockSchemaMigrations) : IDatabaseSchemaChangeGuard
{
    public const string ConfigurationKey = "Persistence:BlockSchemaMigrations";

    public async Task EnsureSchemaUnchangedAsync(CancellationToken cancellationToken = default)
    {
        if (!blockSchemaMigrations)
            return;

        await using var read = await database.OpenReadAsync(cancellationToken);
        var known = read.Db.Database.GetMigrations().ToList();
        var applied = (await read.Db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
        var pending = known.Except(applied, StringComparer.Ordinal).ToList();
        var unknown = applied.Except(known, StringComparer.Ordinal).ToList();
        if (pending.Count == 0 && unknown.Count == 0)
            return;

        var differences = new List<string>(2);
        if (pending.Count > 0)
            differences.Add($"{pending.Count} migration(s) this build would apply ({string.Join(", ", pending)})");
        if (unknown.Count > 0)
            differences.Add($"{unknown.Count} applied migration(s) this build does not know ({string.Join(", ", unknown)})");

        throw new InvalidOperationException(
            $"Schema migrations are blocked by {ConfigurationKey} and the database at "
            + $"'{read.Db.Database.GetDbConnection().DataSource}' differs from this build: "
            + $"{string.Join("; ", differences)}. Nothing was changed. Test schema changes against the development database.");
    }
}
