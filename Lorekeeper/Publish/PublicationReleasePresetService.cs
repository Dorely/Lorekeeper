using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Publish;

public sealed record PublicationReleasePreset(
    PublicationEditionFormat Format,
    PublicationVendor Vendor,
    string ProfileId,
    PublicationBinding Binding,
    PublicationPaper Paper,
    PublicationInk Ink,
    bool Bleed,
    bool AllowDesignedPageOverrides);

public interface IPublicationReleasePresetService
{
    Task<PublicationReleasePreset> ResolveAsync(
        Guid projectId,
        PublicationEditionFormat format,
        PublicationVendor destination,
        CancellationToken cancellationToken = default);
}

public sealed class PublicationReleasePresetService(AppDbContext db) : IPublicationReleasePresetService
{
    public async Task<PublicationReleasePreset> ResolveAsync(
        Guid projectId,
        PublicationEditionFormat format,
        PublicationVendor destination,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(format) || !Enum.IsDefined(destination))
            throw new ArgumentException("Release type or destination is invalid.");
        if (format != PublicationEditionFormat.Paperback)
            destination = PublicationVendor.Generic;
        var hasArtwork = await db.PublishAssets.AsNoTracking().AnyAsync(
            asset => asset.ProjectId == projectId && asset.ContentType.StartsWith("image/"),
            cancellationToken);
        return new PublicationReleasePreset(
            format,
            destination,
            PublicationEditionService.DefaultProfile(format, destination),
            format == PublicationEditionFormat.Paperback ? PublicationBinding.PerfectBound : PublicationBinding.Digital,
            format == PublicationEditionFormat.Paperback ? PublicationPaper.White : PublicationPaper.Digital,
            format == PublicationEditionFormat.Paperback
                ? hasArtwork ? PublicationInk.Color : PublicationInk.BlackAndWhite
                : PublicationInk.Digital,
            // Print cover profiles require bleed even when the interior has no
            // edge-to-edge artwork. Keeping the release bleed-enabled also lets
            // future full-bleed figures flow into the release without rebuilding
            // its basic product configuration.
            format == PublicationEditionFormat.Paperback,
            false);
    }
}
