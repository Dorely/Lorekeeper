using Lorekeeper.Models;

namespace Lorekeeper.Publish;

public enum PublicationPdfActionKind
{
    NotGenerated,
    Rendering,
    Invalid,
    Validated,
    Stale,
    Legacy,
}

public sealed record PublicationPdfActionState(
    PublicationPdfActionKind Kind,
    PublicationRenderJobView? Job,
    PublicationArtifactView? Interior,
    PublicationArtifactView? Cover)
{
    public static PublicationPdfActionState Resolve(
        IReadOnlyList<PublicationRenderJobView> jobs,
        bool sourceMayHaveChanged = false)
    {
        var active = jobs.FirstOrDefault(job =>
            job.Status is PublicationRenderStatus.Queued or PublicationRenderStatus.Rendering);
        if (active is not null)
            return new(PublicationPdfActionKind.Rendering, active, null, null);

        if (sourceMayHaveChanged && jobs.Any(job =>
                job.Artifacts.Any(artifact =>
                    artifact.Kind == PublicationArtifactKind.InteriorPdf && !artifact.IsLegacy)))
        {
            return new(PublicationPdfActionKind.Stale, jobs.FirstOrDefault(), null, null);
        }

        var currentJob = jobs.FirstOrDefault(job =>
            job.Status == PublicationRenderStatus.Completed
            && job.Artifacts.Any(artifact =>
                artifact.Kind == PublicationArtifactKind.InteriorPdf
                    && !artifact.IsLegacy
                    && !artifact.IsStale));
        if (currentJob is not null)
        {
            return new(
                PublicationPdfActionKind.Validated,
                currentJob,
                currentJob.Artifacts.First(artifact =>
                    artifact.Kind == PublicationArtifactKind.InteriorPdf
                        && !artifact.IsLegacy
                        && !artifact.IsStale),
                currentJob.Artifacts.FirstOrDefault(artifact =>
                    artifact.Kind == PublicationArtifactKind.CoverPdf
                        && !artifact.IsLegacy
                        && !artifact.IsStale));
        }

        var latest = jobs.FirstOrDefault();
        if (latest?.Status == PublicationRenderStatus.Failed)
            return new(PublicationPdfActionKind.Invalid, latest, null, null);

        var staleJob = jobs.FirstOrDefault(job =>
            job.Artifacts.Any(artifact =>
                artifact.Kind == PublicationArtifactKind.InteriorPdf
                    && !artifact.IsLegacy
                    && artifact.IsStale));
        if (staleJob is not null)
            return new(PublicationPdfActionKind.Stale, staleJob, null, null);

        var legacyJob = jobs.FirstOrDefault(job => job.Artifacts.Any(artifact =>
            artifact.Kind == PublicationArtifactKind.InteriorPdf && artifact.IsLegacy));
        if (legacyJob is not null)
            return new(PublicationPdfActionKind.Legacy, legacyJob, null, null);

        return new(PublicationPdfActionKind.NotGenerated, latest, null, null);
    }
}
