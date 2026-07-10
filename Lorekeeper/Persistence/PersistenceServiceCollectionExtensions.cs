using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence;

/// <summary>
/// Wires up <see cref="AppDbContext"/> against a database backend chosen by configuration.
/// Today only SQLite is implemented; Postgres (or other backends) drops in as another
/// case here without touching the rest of the app.
/// </summary>
public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddLorekeeperPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        bool usePerUserDataDirectory = false)
    {
        var providerName = configuration["Persistence:Provider"] ?? "Sqlite";
        var connectionString = SqliteConnectionSettings.BuildConnectionString(
            configuration,
            usePerUserDataDirectory);

        services.AddDbContext<AppDbContext>(options =>
        {
            switch (providerName)
            {
                case "Sqlite":
                    options.UseSqlite(connectionString, sqlite => sqlite.CommandTimeout(SqliteConnectionSettings.BusyTimeoutSeconds));
                    break;
                // case "Postgres":
                //     options.UseNpgsql(connectionString);
                //     break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported persistence provider '{providerName}'. " +
                        "Supported values: Sqlite.");
            }
        });

        return services;
    }
}
