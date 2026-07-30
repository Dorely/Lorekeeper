using Lorekeeper.Models;
using Lorekeeper.Publish;

namespace Lorekeeper.Tests;

public sealed class PublicationRenderTests
{
    [Fact]
    public void ArtifactStalenessUsesTheCurrentEditionFingerprint()
    {
        var job = new PublicationRenderJob
        {
            EditionId = Guid.NewGuid(),
            SourceFingerprint = "rendered-source",
            Status = PublicationRenderStatus.Completed,
        };
        var artifact = new PublicationArtifact
        {
            EditionId = job.EditionId,
            RenderJobId = job.Id,
            Kind = PublicationArtifactKind.InteriorPdf,
            FileName = "interior.pdf",
            MediaType = "application/pdf",
            Sha256 = new string('a', 64),
            ByteLength = 42,
            SourceFingerprint = job.SourceFingerprint,
        };

        var current = PublicationRenderService.View(job, [artifact], "rendered-source");
        var stale = PublicationRenderService.View(job, [artifact], "changed-source");

        Assert.False(Assert.Single(current.Artifacts).IsStale);
        Assert.True(Assert.Single(stale.Artifacts).IsStale);
    }

    [Fact]
    public void InvalidStoredDiagnosticsFailClosed()
    {
        var job = new PublicationRenderJob
        {
            EditionId = Guid.NewGuid(),
            DiagnosticsJson = "{not-json",
        };

        var view = PublicationRenderService.View(job, [], job.SourceFingerprint);

        var diagnostic = Assert.Single(view.Diagnostics);
        Assert.Equal("PRESS_DIAGNOSTICS_INVALID", diagnostic.Code);
        Assert.Equal("error", diagnostic.Severity);
    }
}
