namespace Lorekeeper.VersionHistory.Services;

internal static class VersionHistoryTemporaryPaths
{
    public static string GetParentDirectory()
    {
        // The OS temporary directory can have linked ancestors (macOS /var,
        // for example). Resolve only that system-owned prefix; callers still
        // reject links in Lorekeeper's staging directories and their contents.
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var root = Path.GetPathRoot(temporaryRoot)!;
        var resolved = root;
        foreach (var segment in temporaryRoot[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(resolved, segment));
            resolved = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? directory.FullName;
        }

        return Path.Combine(resolved, "Lorekeeper", "version-history");
    }
}
