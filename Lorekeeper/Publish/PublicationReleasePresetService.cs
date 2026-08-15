using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Publish;

public sealed record PublicationReleasePreset(
    PublicationEditionFormat Format,
    PublicationVendor Vendor,
    string ProfileId,
    string RegistryVersion,
    string? ProductKey,
    PrintFinish Finish,
    PrintCoverMode CoverMode,
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

public sealed class PublicationReleasePresetService(IAppDatabaseOperationFactory database, IPrintProductRegistry printProducts) : IPublicationReleasePresetService
{
    public async Task<PublicationReleasePreset> ResolveAsync(
        Guid projectId,
        PublicationEditionFormat format,
        PublicationVendor destination,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        if (!Enum.IsDefined(format) || !Enum.IsDefined(destination))
            throw new ArgumentException("Release type or destination is invalid.");
        var isPrint = format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover;
        if (!isPrint)
            destination = PublicationVendor.Generic;
        await db.Projects.AsNoTracking().Where(project => project.Id == projectId)
            .Select(project => project.Id).SingleAsync(cancellationToken);
        var product = isPrint ? printProducts.GetDefault(format, destination) : null;
        return new PublicationReleasePreset(
            format,
            destination,
            product?.PdfProfile ?? PublicationEditionService.DefaultProfile(format, destination),
            printProducts.Version,
            product?.Key,
            PrintFinish.Matte,
            PrintCoverMode.Simplex,
            // Print cover profiles require bleed even when the interior has no
            // edge-to-edge artwork. Keeping the release bleed-enabled also lets
            // future full-bleed figures flow into the release without rebuilding
            // its basic product configuration.
            isPrint,
            false);
    }

}
