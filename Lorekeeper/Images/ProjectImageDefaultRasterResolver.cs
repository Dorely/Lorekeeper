using Lorekeeper.Composition;

namespace Lorekeeper.Images;

public interface IProjectImageDefaultRasterResolver
{
    Task<LayoutImageSize> ResolveAsync(Guid projectId, CancellationToken cancellationToken = default);
}

public sealed class ProjectImageDefaultRasterResolver(
    IProjectPageSetupService pageSetups) : IProjectImageDefaultRasterResolver
{
    public async Task<LayoutImageSize> ResolveAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var setup = await pageSetups.GetOrCreateAsync(projectId, cancellationToken);
        return LayoutImageSizeResolver.Resolve(setup.PageWidthInches, setup.PageHeightInches);
    }
}
