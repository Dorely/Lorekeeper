using System.Text.Json;
using Lorekeeper.Composition;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;

namespace Lorekeeper.Publish;

public sealed record PublicationCoverPreview(PublicationCoverDesignView Cover, CompositionCanvasPreviewResult Preview);

/// <summary>Builds the same explicit, fully bound cover preview for every agent adapter.</summary>
public sealed class PublicationCoverPreviewService(IPublicationBookService books, IPublicationCoverService covers,
    ICompositionCanvasPreviewService canvases, IPublicationEditionService editions, IPublicationPaginationService pagination,
    IPublicationEffectiveConfigurationResolver effective)
{
    public async Task EnsureGeometryAsync(Guid projectId, Guid editionId, CancellationToken ct = default)
    {
        var release = (await editions.ListAsync(projectId, ct)).SingleOrDefault(value => value.Id == editionId)
            ?? throw new KeyNotFoundException("Publication release not found.");
        if (release.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover)
            await pagination.EnsureCurrentAsync(projectId, editionId, ct);
    }

    public async Task<PublicationCoverDesignView> ReadAsync(Guid projectId, Guid? editionId, string? surfaceRole, CancellationToken ct = default)
    {
        if (editionId is Guid id)
        {
            await EnsureGeometryAsync(projectId, id, ct);
            return string.IsNullOrWhiteSpace(surfaceRole) ? await covers.GetAsync(projectId, id, ct)
                : await covers.GetSurfaceAsync(projectId, id, surfaceRole, ct);
        }
        if (!string.IsNullOrWhiteSpace(surfaceRole) && surfaceRole != "front") throw new ArgumentException("Core has a front cover; open a release for other surfaces.");
        return await books.GetCoverAsync(projectId, ct);
    }

    public async Task<PublicationCoverPreview> RenderAsync(Guid projectId, Guid? editionId, string? surfaceRole,
        CompositionCanvasPreviewMode mode, CancellationToken ct = default)
    {
        var cover = await ReadAsync(projectId, editionId, surfaceRole, ct);
        var scene = JsonSerializer.Deserialize<CompositionScene>(cover.CompositionSceneJson, ManuscriptCodec.JsonOptions)
            ?? throw new InvalidDataException("The cover composition is empty.");
        string publisher, copyright, isbn;
        if (editionId is Guid release)
        {
            var profile = (await effective.ResolveReleaseAsync(projectId, release, ct)).Edition;
            publisher = profile.Publisher; copyright = profile.Copyright; isbn = profile.Isbn;
        }
        else
        {
            var book = await books.GetOrCreateAsync(projectId, ct);
            publisher = book.Publisher; copyright = book.Copyright; isbn = "";
        }
        var target = editionId ?? projectId;
        var preview = await canvases.RenderSceneAtResolutionAsync(projectId, target, cover.Revision, scene,
            ManuscriptCodec.CreateEmpty(target), mode, 1600, ct,
            PublicationTextBindings.Bindings(cover.Title, cover.Subtitle, cover.Author, publisher, copyright, cover.Description, isbn, cover.SpineText),
            cover.BackgroundColor);
        return new(cover, preview);
    }
}
