using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lorekeeper.Models;

namespace Lorekeeper.Publish;

[JsonConverter(typeof(JsonStringEnumConverter<PrintBindingConstruction>))]
public enum PrintBindingConstruction
{
    PerfectBound,
    CaseBound,
}

[JsonConverter(typeof(JsonStringEnumConverter<PrintInteriorProcess>))]
public enum PrintInteriorProcess
{
    BlackAndWhite,
    StandardColor,
    PremiumColor,
}

[JsonConverter(typeof(JsonStringEnumConverter<PrintCoverMaterial>))]
public enum PrintCoverMaterial
{
    PrintedCover,
    CaseLaminate,
    DigitalCloth,
    DigitalClothWithJacket,
    JacketedCaseLaminate,
}

public sealed record PrintSpineAnchor(int Pages, decimal Inches);

public sealed record PrintSpineModel(
    string Kind,
    decimal? InchesPerPage = null,
    IReadOnlyList<PrintSpineAnchor>? Anchors = null,
    decimal BaseInches = 0,
    decimal? RoundToIncrementInches = null);

public sealed record PrintArtifactProfile(
    string Key,
    PublicationVendor Vendor,
    PublicationEditionFormat Format,
    PrintBindingConstruction Binding,
    PrintInteriorProcess InteriorProcess,
    int? BasisWeightPounds,
    int? Gsm,
    PrintCoverMaterial CoverMaterial,
    IReadOnlyList<PrintCoverMode> CoverModes,
    IReadOnlyList<string> TrimSizes,
    bool AllowsCustomTrim,
    int MinimumPages,
    int MaximumPages,
    string PdfProfile,
    PrintSpineModel SpineModel,
    int? MinimumSubmittedPages = null,
    int? MaximumSubmittedPages = null,
    IReadOnlyList<PrintProjectUse>? SupportedProjectUses = null,
    PrintProjectUse DefaultProjectUse = PrintProjectUse.ForSale)
{
    public IReadOnlyList<PrintProjectUse> EffectiveSupportedProjectUses => SupportedProjectUses ?? [PrintProjectUse.ForSale];
    public bool RequiresCaseCover => CoverMaterial == PrintCoverMaterial.CaseLaminate
        || (CoverMaterial == PrintCoverMaterial.JacketedCaseLaminate && Vendor != PublicationVendor.BarnesAndNoblePress)
        || (Vendor == PublicationVendor.Generic && Binding == PrintBindingConstruction.CaseBound);
    public bool RequiresDustJacket => CoverMaterial is PrintCoverMaterial.DigitalClothWithJacket
        or PrintCoverMaterial.JacketedCaseLaminate;
    public bool RequiresClothManifest => CoverMaterial is PrintCoverMaterial.DigitalCloth
        or PrintCoverMaterial.DigitalClothWithJacket;
    public bool RequiresPerfectBoundCover => Binding == PrintBindingConstruction.PerfectBound;
}

public sealed record PrintArtifactProfileRegistrySnapshot(
    string RegistryVersion,
    DateTime ReviewedAtUtc,
    IReadOnlyList<string> Sources,
    IReadOnlyList<PrintArtifactProfile> Profiles);

public interface IPrintArtifactProfileRegistry
{
    string Version { get; }
    string Sha256 { get; }
    IReadOnlyList<PrintArtifactProfile> List(PublicationEditionFormat? format = null, PublicationVendor? vendor = null);
    PrintArtifactProfile GetRequired(string key);
    PrintArtifactProfile GetDefault(PublicationEditionFormat format, PublicationVendor vendor);
}

public sealed class PrintArtifactProfileRegistry : IPrintArtifactProfileRegistry
{
    public const string CurrentVersion = "2026.09.2";
    private const string ResourceSuffix = "PrintArtifactProfiles.print-artifact-profiles-v1.json";
    private readonly PrintArtifactProfileRegistrySnapshot _snapshot;
    private readonly IReadOnlyDictionary<string, PrintArtifactProfile> _profiles;

    public PrintArtifactProfileRegistry()
    {
        var assembly = typeof(PrintArtifactProfileRegistry).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The bundled print-artifact profile registry is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var data = buffer.ToArray();
        Sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        _snapshot = JsonSerializer.Deserialize<PrintArtifactProfileRegistrySnapshot>(data, JsonOptions)
            ?? throw new InvalidDataException("The bundled print-artifact profile registry is invalid.");
        if (!string.Equals(_snapshot.RegistryVersion, CurrentVersion, StringComparison.Ordinal))
            throw new InvalidDataException($"The bundled print-artifact profile registry must be version {CurrentVersion}.");
        _profiles = _snapshot.Profiles.ToDictionary(item => item.Key, StringComparer.Ordinal);
        if (_profiles.Count == 0 || _profiles.Count != _snapshot.Profiles.Count)
            throw new InvalidDataException("The bundled print-artifact profile registry is empty or contains duplicate keys.");
        foreach (var profile in _snapshot.Profiles)
        {
            if (profile.Format is not (PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover)
                || string.IsNullOrWhiteSpace(profile.Key)
                || profile.MinimumPages <= 0
                || profile.MaximumPages < profile.MinimumPages
                || (profile.MinimumSubmittedPages ?? profile.MinimumPages) <= 0
                || (profile.MaximumSubmittedPages ?? profile.MaximumPages) < (profile.MinimumSubmittedPages ?? profile.MinimumPages)
                || profile.CoverModes.Count == 0
                || profile.EffectiveSupportedProjectUses.Count == 0
                || !profile.EffectiveSupportedProjectUses.Contains(profile.DefaultProjectUse)
                || profile.EffectiveSupportedProjectUses.Distinct().Count() != profile.EffectiveSupportedProjectUses.Count)
                throw new InvalidDataException($"Print artifact profile '{profile.Key}' has an incomplete identity or availability range.");
            if (profile.SpineModel.Kind == "RoundedCaliper"
                && (profile.SpineModel.InchesPerPage is null or <= 0
                    || profile.SpineModel.RoundToIncrementInches is null or <= 0))
                throw new InvalidDataException($"Print artifact profile '{profile.Key}' has an incomplete rounded-caliper spine model.");
            if (profile.SpineModel.Kind == "FrozenLookup")
            {
                var expectedPages = Enumerable.Range(profile.MinimumPages, profile.MaximumPages - profile.MinimumPages + 1)
                    .Where(page => page % 2 == 0).ToArray();
                var actualPages = (profile.SpineModel.Anchors ?? []).Select(anchor => anchor.Pages).Order().ToArray();
                if (!expectedPages.SequenceEqual(actualPages)
                    || (profile.SpineModel.Anchors ?? []).Any(anchor => anchor.Inches <= 0))
                    throw new InvalidDataException($"Print artifact profile '{profile.Key}' does not contain a complete exact spine table.");
            }
        }
    }

    public string Version => _snapshot.RegistryVersion;
    public string Sha256 { get; }

    public IReadOnlyList<PrintArtifactProfile> List(PublicationEditionFormat? format = null, PublicationVendor? vendor = null) =>
        _snapshot.Profiles
            .Where(item => format is null || item.Format == format)
            .Where(item => vendor is null || item.Vendor == vendor)
            .OrderBy(item => item.Vendor)
            .ThenBy(item => item.CoverMaterial)
            .ThenBy(item => item.InteriorProcess)
            .ThenBy(item => item.BasisWeightPounds)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();

    public PrintArtifactProfile GetRequired(string key) =>
        _profiles.TryGetValue(key, out var profile)
            ? profile
            : throw new InvalidOperationException($"Print artifact profile '{key}' is not present in registry {Version}.");

    public PrintArtifactProfile GetDefault(PublicationEditionFormat format, PublicationVendor vendor)
    {
        var key = (format, vendor) switch
        {
            (PublicationEditionFormat.Paperback, PublicationVendor.AmazonKdp) => "kdp-pb-bw-50-2252",
            (PublicationEditionFormat.Paperback, PublicationVendor.IngramSpark) => "ingram-pb-bw-50-2009",
            (PublicationEditionFormat.Paperback, PublicationVendor.BarnesAndNoblePress) => "bn-pb-bw-50-6x9",
            (PublicationEditionFormat.Paperback, PublicationVendor.Lulu) => "lulu-pb-bw-60-white",
            (PublicationEditionFormat.Paperback, _) => "generic-pb-bw-50-white",
            (PublicationEditionFormat.Hardcover, PublicationVendor.AmazonKdp) => "kdp-hc-bw-50-2252",
            (PublicationEditionFormat.Hardcover, PublicationVendor.IngramSpark) => "ingram-hc-case-bw-50-2009",
            (PublicationEditionFormat.Hardcover, PublicationVendor.BarnesAndNoblePress) => "bn-hc-case-bw-50-6x9",
            (PublicationEditionFormat.Hardcover, PublicationVendor.Lulu) => "lulu-hc-case-bw-80-white",
            (PublicationEditionFormat.Hardcover, _) => "generic-case-bw-50-white",
            _ => throw new InvalidOperationException("Digital releases do not use a print artifact profile."),
        };
        return GetRequired(key);
    }

    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

public sealed record PrintCoverGeometry(
    int SubmittedPageCount,
    int NormalizedCoverPageCount,
    int ReportedProductionPageCount,
    decimal SpineWidthInches,
    decimal SurfaceWidthInches,
    decimal SurfaceHeightInches,
    decimal TrimWidthInches,
    decimal TrimHeightInches,
    decimal BleedInches,
    decimal WrapInches,
    decimal HingeInches,
    decimal GutterInches,
    decimal FlapInches,
    decimal InsideSpineNoInkInches,
    string GeometryFingerprint)
{
    public decimal BackRegionWidthInches { get; init; }
    public decimal FrontRegionWidthInches { get; init; }
    public decimal CoverRegionYInches { get; init; }
    public decimal CoverRegionHeightInches { get; init; }
}

public interface IPrintGeometryService
{
    PrintCoverGeometry Calculate(PublicationEdition edition, int submittedPageCount, string? surfaceRole = null);
}

public sealed class PrintGeometryService(IPrintArtifactProfileRegistry registry) : IPrintGeometryService
{
    public PrintCoverGeometry Calculate(PublicationEdition edition, int submittedPageCount, string? surfaceRole = null)
    {
        var product = registry.GetRequired(edition.PrintArtifactProfileKey);
        var normalizedPages = product.Vendor switch
        {
            PublicationVendor.AmazonKdp => submittedPageCount + (submittedPageCount % 2),
            PublicationVendor.IngramSpark => submittedPageCount + (submittedPageCount % 2),
            PublicationVendor.BarnesAndNoblePress => submittedPageCount + (submittedPageCount % 2),
            PublicationVendor.Lulu => submittedPageCount + (submittedPageCount % 2),
            _ => submittedPageCount,
        };
        if (submittedPageCount < (product.MinimumSubmittedPages ?? product.MinimumPages)
            || submittedPageCount > (product.MaximumSubmittedPages ?? product.MaximumPages))
            throw new InvalidOperationException($"The selected artifact settings support {product.MinimumSubmittedPages ?? product.MinimumPages}-{product.MaximumSubmittedPages ?? product.MaximumPages} submitted pages; this interior has {submittedPageCount}.");
        if (normalizedPages < product.MinimumPages || normalizedPages > product.MaximumPages)
            throw new InvalidOperationException($"The selected artifact settings support {product.MinimumPages}–{product.MaximumPages} pages; this interior has {normalizedPages}.");

        var spine = CalculateSpine(product, normalizedPages);
        var trimWidth = (decimal)edition.PageWidthInches;
        var trimHeight = (decimal)edition.PageHeightInches;
        var (bleed, wrap, hinge, gutter, flap, insideNoInk, surfaceWidth, surfaceHeight) = product.Vendor switch
        {
            PublicationVendor.AmazonKdp when product.Format == PublicationEditionFormat.Hardcover =>
                (0.125m, 0.51m, 0.4m, 0m, 0m, 0m,
                    2 * trimWidth + spine + 2 * 0.51m,
                    trimHeight + 2 * 0.51m),
            PublicationVendor.AmazonKdp =>
                (0.125m, 0m, 0m, 0m, 0m, 0m,
                    2 * trimWidth + spine + 0.25m,
                    trimHeight + 0.25m),
            PublicationVendor.IngramSpark when surfaceRole == "dust-jacket"
                || surfaceRole is null && product.RequiresDustJacket =>
                (0.125m, 0m, 0m, 0.25m, 3.25m, 0m,
                    2 * (trimWidth + 0.4375m) + spine + 2 * 3.25m + 2 * 0.25m + 0.25m,
                    trimHeight + 0.5m),
            PublicationVendor.IngramSpark when surfaceRole == "case-wrap"
                || surfaceRole is null && product.RequiresCaseCover =>
                (0m, 0.625m, 0m, 0.5m, 0m, 0m,
                    2 * (trimWidth - 0.185m) + spine + 2 * 0.625m + 2 * 0.5m,
                    trimHeight + 1.5m),
            PublicationVendor.IngramSpark =>
                (0.125m, 0m, 0m, 0m, 0m, edition.PrintCoverMode == PrintCoverMode.Duplex ? spine + 0.125m : 0m,
                    2 * trimWidth + spine + 0.25m,
                    trimHeight + 0.25m),
            PublicationVendor.BarnesAndNoblePress when product.RequiresDustJacket =>
                (0.25m, 0m, 0m, 0m, 3.375m, 0m,
                    2 * 6.694444444444444444m + spine + 6.75m,
                    trimHeight + 0.5m),
            PublicationVendor.BarnesAndNoblePress when product.RequiresCaseCover =>
                (0.75m, 0.75m, 0m, 0m, 0m, 0m,
                    2 * 6.944444444444444444m + spine,
                    trimHeight + 1.5m),
            PublicationVendor.BarnesAndNoblePress =>
                (0.125m, 0m, 0m, 0m, 0m, 0m,
                    2 * trimWidth + spine + 0.25m,
                    trimHeight + 0.25m),
            PublicationVendor.Lulu =>
                (0.125m, 0m, 0m, 0m, 0m, 0m,
                    2 * trimWidth + spine + 0.25m,
                    trimHeight + 0.25m),
            _ => throw new InvalidOperationException("Other-printer releases require a supported print artifact profile before preparation."),
        };

        var fingerprintSource = FormattableString.Invariant($"{registry.Version}|{product.Key}|{edition.PrintCoverMode}|{trimWidth:0.####}|{trimHeight:0.####}|{normalizedPages}|{spine:0.#####}|{surfaceWidth:0.#####}|{surfaceHeight:0.#####}");
        var fingerprint = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fingerprintSource))).ToLowerInvariant();
        return new(submittedPageCount, normalizedPages, normalizedPages, spine, surfaceWidth, surfaceHeight,
            trimWidth, trimHeight, bleed, wrap, hinge, gutter, flap, insideNoInk, fingerprint)
        {
            BackRegionWidthInches = product.Vendor == PublicationVendor.BarnesAndNoblePress
                ? product.RequiresDustJacket
                    ? 6.694444444444444444m
                    : product.RequiresCaseCover
                        ? 6.944444444444444444m
                        : trimWidth + 0.125m
                : 0,
            FrontRegionWidthInches = product.Vendor == PublicationVendor.BarnesAndNoblePress
                ? product.RequiresDustJacket
                    ? 6.694444444444444444m
                    : product.RequiresCaseCover
                        ? 6.944444444444444444m
                        : trimWidth + 0.125m
                : 0,
            CoverRegionYInches = 0,
            CoverRegionHeightInches = product.Vendor == PublicationVendor.BarnesAndNoblePress ? surfaceHeight : 0,
        };
    }

    private static decimal CalculateSpine(PrintArtifactProfile product, int pages)
    {
        if (product.SpineModel.Kind == "Unsupported")
            throw new InvalidOperationException("Other-printer releases require a supported print artifact profile before preparation.");
        if (product.SpineModel.Kind == "RoundedCaliper"
            && product.SpineModel.InchesPerPage is decimal roundedCaliper
            && product.SpineModel.RoundToIncrementInches is decimal increment)
        {
            var unrounded = roundedCaliper * pages + product.SpineModel.BaseInches;
            return decimal.Round(unrounded / increment, 0, MidpointRounding.AwayFromZero) * increment;
        }
        if (product.SpineModel.Kind == "Caliper" && product.SpineModel.InchesPerPage is decimal inchesPerPage)
            return decimal.Round(inchesPerPage * pages, 5, MidpointRounding.AwayFromZero);
        var anchors = product.SpineModel.Anchors?.OrderBy(item => item.Pages).ToArray() ?? [];
        if (anchors.Length == 0)
            throw new InvalidOperationException($"Print artifact profile '{product.Key}' has no spine evidence.");
        var exact = anchors.FirstOrDefault(item => item.Pages == pages);
        if (exact is not null)
            return exact.Inches;
        throw new InvalidOperationException($"Print artifact profile '{product.Key}' has no verified spine measurement for {pages} normalized pages.");
    }

}
