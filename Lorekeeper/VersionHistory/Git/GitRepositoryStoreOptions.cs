namespace Lorekeeper.VersionHistory.Git;

/// <summary>
/// Configuration for the local, app-managed bare repositories.
/// </summary>
public sealed class GitRepositoryStoreOptions
{
    /// <summary>
    /// Explicit root for local history repositories. Relative values are
    /// resolved against the process base directory.
    /// </summary>
    public string? HistoryRoot { get; init; }

    /// <summary>
    /// Selects the packaged default under the current user's local application
    /// data directory. Development defaults remain adjacent to the app data
    /// base, matching the local-first development layout.
    /// </summary>
    public bool IsPackaged { get; init; }

    /// <summary>
    /// Optional development app-data base. When omitted, AppContext.BaseDirectory
    /// is used, so the default is a History directory adjacent to local app data.
    /// </summary>
    public string? DevelopmentDataRoot { get; init; }

    public string ResolveHistoryRoot()
    {
        if (!string.IsNullOrWhiteSpace(HistoryRoot))
            return Path.GetFullPath(HistoryRoot);

        if (IsPackaged)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                throw new InvalidOperationException("The per-user local application data directory is unavailable.");

            return Path.GetFullPath(Path.Combine(localAppData, "Lorekeeper", "History"));
        }

        var developmentRoot = string.IsNullOrWhiteSpace(DevelopmentDataRoot)
            ? AppContext.BaseDirectory
            : DevelopmentDataRoot;
        return Path.GetFullPath(Path.Combine(developmentRoot, "History"));
    }
}
