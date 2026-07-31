using System.Security.Cryptography;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Publish;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class PublicationRenderTests
{
    [Fact]
    public async Task CorruptedStoredArtifactCannotCrossTheDownloadBoundary()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
        await db.Database.EnsureCreatedAsync();
        var project = new Project { Name = "Book", Slug = $"book-{Guid.NewGuid():N}" };
        var edition = new PublicationEdition { ProjectId = project.Id, Name = "Paperback" };
        var validData = "%PDF-valid"u8.ToArray();
        var valid = new PublicationArtifact
        {
            EditionId = edition.Id,
            Kind = PublicationArtifactKind.InteriorPdf,
            FileName = "valid.pdf",
            MediaType = "application/pdf",
            Data = validData,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(validData)),
            ByteLength = validData.Length,
        };
        var corrupt = new PublicationArtifact
        {
            EditionId = edition.Id,
            Kind = PublicationArtifactKind.CoverPdf,
            FileName = "corrupt.pdf",
            MediaType = "application/pdf",
            Data = "%PDF-corrupt"u8.ToArray(),
            Sha256 = valid.Sha256,
            ByteLength = valid.ByteLength,
        };
        db.AddRange(project, edition, valid, corrupt);
        await db.SaveChangesAsync();
        var service = new PublicationRenderService(db, null!, null!, null!, null!);

        Assert.NotNull(await service.GetArtifactAsync(project.Id, valid.Id));
        Assert.Null(await service.GetArtifactAsync(project.Id, corrupt.Id));
        Assert.Null(await service.GetArtifactAsync(Guid.NewGuid(), valid.Id));
    }

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
