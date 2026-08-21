using Lorekeeper.Projects;

namespace Lorekeeper.Context;

public static class ProjectReferenceManifestFormatter
{
    public static string? Format(IReadOnlyList<ProjectReferenceManifest> references)
    {
        if (references.Count == 0) return null;

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("The active project is the authoritative canon and the only mutation scope for this turn.");
        builder.AppendLine("Direct references are read-only continuity evidence for narrative search and exact source reads. They must never override active-project canon or receive mutations.");
        builder.AppendLine();
        builder.AppendLine("Direct referenced projects:");
        const int maximumManifestScopes = 32;
        foreach (var reference in references.Take(maximumManifestScopes))
        {
            builder.Append("- ").Append(reference.Name)
                .Append(" [projectId: ").Append(reference.ProjectId.ToString("N"))
                .Append(", slug: ").Append(reference.Slug)
                .Append(", updated: ").Append(reference.UpdatedAt.ToString("O"))
                .AppendLine("]");
            builder.Append("  Brief premise: ").AppendLine(Compact(reference.BookBriefPremise));
            builder.Append("  Brief genre: ").AppendLine(Compact(reference.BookBriefGenre));
            builder.Append("  Counts: acts=").Append(reference.ActCount)
                .Append(", chapters=").Append(reference.ChapterCount)
                .Append(", searchableEntities=").Append(reference.SearchableEntityCount)
                .Append(", facts=").Append(reference.FactCount)
                .Append(", writingSamples=").Append(reference.WritingSampleCount)
                .Append(", selectedCanonicalSources=").Append(reference.CanonicalSourceCount)
                .Append(", canonicalVisuals=").AppendLine(reference.CanonicalVisualCount.ToString());
        }
        if (references.Count > maximumManifestScopes)
            builder.Append("- ").Append(references.Count - maximumManifestScopes).AppendLine(" additional direct references omitted from this bounded manifest.");
        return builder.ToString().TrimEnd();
    }

    private static string Compact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(unspecified)";
        var compact = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= 180 ? compact : compact[..180] + "...";
    }
}
