using System.Text.Json;
using Lorekeeper.Models;
using Lorekeeper.Publish;

namespace Lorekeeper.Tests;

public sealed class PublicationPdfActionStateTests
{
    [Fact]
    public void ResolveCoversMissingActiveFailedStaleAndCurrentRenders()
    {
        Assert.Equal(PublicationPdfActionKind.Generate, PublicationPdfActionState.Resolve([]).Kind);

        var active = Job(PublicationRenderStatus.Rendering);
        Assert.Equal(PublicationPdfActionKind.Active, PublicationPdfActionState.Resolve([active]).Kind);

        var failed = Job(PublicationRenderStatus.Failed);
        Assert.Equal(PublicationPdfActionKind.Failed, PublicationPdfActionState.Resolve([failed]).Kind);

        var stale = Job(
            PublicationRenderStatus.Completed,
            Artifact(PublicationArtifactKind.InteriorPdf, stale: true));
        Assert.Equal(PublicationPdfActionKind.Regenerate, PublicationPdfActionState.Resolve([stale]).Kind);

        var interior = Artifact(PublicationArtifactKind.InteriorPdf, stale: false, "interior.pdf");
        var cover = Artifact(PublicationArtifactKind.CoverPdf, stale: false, "cover.pdf");
        var current = PublicationPdfActionState.Resolve([
            Job(PublicationRenderStatus.Completed, interior, cover),
        ]);
        Assert.Equal(PublicationPdfActionKind.Current, current.Kind);
        Assert.Equal(interior.Id, current.Interior?.Id);
        Assert.Equal(cover.Id, current.Cover?.Id);
        Assert.Equal(
            PublicationPdfActionKind.Regenerate,
            PublicationPdfActionState.Resolve([current.Job!], sourceMayHaveChanged: true).Kind);
    }

    [Fact]
    public void AssistantArtifactMetadataExposesImmutableViewAndDownloadUrls()
    {
        var projectId = Guid.NewGuid();
        var artifact = Artifact(PublicationArtifactKind.InteriorPdf, stale: false, "book-interior.pdf");
        var json = JsonSerializer.Serialize(PublishAssistantTools.DownloadView(
            new PublishAssistantContext(projectId),
            artifact));
        using var document = JsonDocument.Parse(json);

        Assert.Equal("book-interior.pdf", document.RootElement.GetProperty("FileName").GetString());
        Assert.False(document.RootElement.GetProperty("IsStale").GetBoolean());
        Assert.Equal(
            $"/projects/{projectId:N}/publish/artifacts/{artifact.Id:N}",
            document.RootElement.GetProperty("ViewUrl").GetString());
        Assert.Equal(
            $"/projects/{projectId:N}/publish/artifacts/{artifact.Id:N}/download",
            document.RootElement.GetProperty("DownloadUrl").GetString());
    }

    private static PublicationRenderJobView Job(
        PublicationRenderStatus status,
        params PublicationArtifactView[] artifacts) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            status,
            "fingerprint",
            "renderer",
            "preview",
            status == PublicationRenderStatus.Completed ? 100 : 25,
            status.ToString(),
            false,
            [],
            artifacts,
            DateTime.UtcNow,
            null,
            status is PublicationRenderStatus.Completed or PublicationRenderStatus.Failed ? DateTime.UtcNow : null);

    private static PublicationArtifactView Artifact(
        PublicationArtifactKind kind,
        bool stale,
        string fileName = "artifact.pdf") =>
        new(
            Guid.NewGuid(),
            kind,
            fileName,
            "application/pdf",
            new string('a', 64),
            128,
            1,
            "fingerprint",
            "renderer",
            "preview",
            DateTime.UtcNow,
            stale);
}
