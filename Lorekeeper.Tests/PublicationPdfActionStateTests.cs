using System.Text.Json;
using Lorekeeper.Models;
using Lorekeeper.Publish;

namespace Lorekeeper.Tests;

public sealed class PublicationPdfActionStateTests
{
    [Fact]
    public void ResolveCoversMissingActiveFailedStaleAndCurrentRenders()
    {
        Assert.Equal(PublicationPdfActionKind.NotGenerated, PublicationPdfActionState.Resolve([]).Kind);

        var active = Job(PublicationRenderStatus.Rendering);
        Assert.Equal(PublicationPdfActionKind.Rendering, PublicationPdfActionState.Resolve([active]).Kind);

        var failed = Job(PublicationRenderStatus.Failed);
        Assert.Equal(PublicationPdfActionKind.Invalid, PublicationPdfActionState.Resolve([failed]).Kind);

        var stale = Job(
            PublicationRenderStatus.Completed,
            Artifact(PublicationArtifactKind.InteriorPdf, stale: true));
        Assert.Equal(PublicationPdfActionKind.Stale, PublicationPdfActionState.Resolve([stale]).Kind);

        var interior = Artifact(PublicationArtifactKind.InteriorPdf, stale: false, "interior.pdf");
        var cover = Artifact(PublicationArtifactKind.CoverPdf, stale: false, "cover.pdf");
        var current = PublicationPdfActionState.Resolve([
            Job(PublicationRenderStatus.Completed, interior, cover),
        ]);
        Assert.Equal(PublicationPdfActionKind.Validated, current.Kind);
        Assert.Equal(interior.Id, current.Interior?.Id);
        Assert.Equal(cover.Id, current.Cover?.Id);
        Assert.Equal(
            PublicationPdfActionKind.Stale,
            PublicationPdfActionState.Resolve([current.Job!], sourceMayHaveChanged: true).Kind);

        var legacy = Job(
            PublicationRenderStatus.Completed,
            Artifact(PublicationArtifactKind.InteriorPdf, stale: false, isLegacy: true));
        Assert.Equal(PublicationPdfActionKind.Legacy, PublicationPdfActionState.Resolve([legacy]).Kind);
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
        Assert.False(document.RootElement.GetProperty("IsLegacy").GetBoolean());
        Assert.Equal("Current", document.RootElement.GetProperty("State").GetString());
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
        string fileName = "artifact.pdf",
        bool isLegacy = false) =>
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
            isLegacy,
            stale);
}
