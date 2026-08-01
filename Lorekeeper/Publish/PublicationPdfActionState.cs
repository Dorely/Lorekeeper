using Lorekeeper.Models;

namespace Lorekeeper.Publish;

public enum PublicationPdfActionKind
{
    Generate,
    Active,
    Failed,
    Regenerate,
    Current,
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
            return new(PublicationPdfActionKind.Active, active, null, null);

        if (sourceMayHaveChanged && jobs.Any(job =>
                job.Artifacts.Any(artifact => artifact.Kind == PublicationArtifactKind.InteriorPdf)))
        {
            return new(PublicationPdfActionKind.Regenerate, jobs.FirstOrDefault(), null, null);
        }

        var currentJob = jobs.FirstOrDefault(job =>
            job.Status == PublicationRenderStatus.Completed
            && job.Artifacts.Any(artifact =>
                artifact.Kind == PublicationArtifactKind.InteriorPdf && !artifact.IsStale));
        if (currentJob is not null)
        {
            return new(
                PublicationPdfActionKind.Current,
                currentJob,
                currentJob.Artifacts.First(artifact =>
                    artifact.Kind == PublicationArtifactKind.InteriorPdf && !artifact.IsStale),
                currentJob.Artifacts.FirstOrDefault(artifact =>
                    artifact.Kind == PublicationArtifactKind.CoverPdf && !artifact.IsStale));
        }

        var latest = jobs.FirstOrDefault();
        if (latest?.Status == PublicationRenderStatus.Failed)
            return new(PublicationPdfActionKind.Failed, latest, null, null);

        var staleJob = jobs.FirstOrDefault(job =>
            job.Artifacts.Any(artifact =>
                artifact.Kind == PublicationArtifactKind.InteriorPdf && artifact.IsStale));
        if (staleJob is not null)
            return new(PublicationPdfActionKind.Regenerate, staleJob, null, null);

        return new(PublicationPdfActionKind.Generate, latest, null, null);
    }
}
