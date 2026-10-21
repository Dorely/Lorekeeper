using Lorekeeper.Composition;

namespace Lorekeeper.Images;

public interface IProjectImageDefaultRasterResolver
{
    /// <summary>The raster within <paramref name="constraints"/> nearest the project's Core Book page.</summary>
    Task<LayoutImageSize> ResolveAsync(
        Guid projectId,
        ImageRasterConstraints constraints,
        CancellationToken cancellationToken = default);
}

public sealed class ProjectImageDefaultRasterResolver(
    IProjectPageSetupService pageSetups) : IProjectImageDefaultRasterResolver
{
    public async Task<LayoutImageSize> ResolveAsync(
        Guid projectId,
        ImageRasterConstraints constraints,
        CancellationToken cancellationToken = default)
    {
        var setup = await pageSetups.GetOrCreateAsync(projectId, cancellationToken);
        return LayoutImageSizeResolver.Resolve(constraints, setup.PageWidthInches, setup.PageHeightInches);
    }
}
