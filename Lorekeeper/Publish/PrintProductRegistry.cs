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
    Declared,
    BlackAndWhite,
    StandardColor,
    PremiumColor,
}

[JsonConverter(typeof(JsonStringEnumConverter<PrintCoverMaterial>))]
public enum PrintCoverMaterial
{
    Declared,
    PrintedCover,
    CaseLaminate,
    DigitalClothBlue,
    DigitalClothGray,
    DigitalClothBlueWithJacket,
    DigitalClothGrayWithJacket,
    JacketedCaseLaminate,
}

public sealed record PrintSpineAnchor(int Pages, decimal Inches);

public sealed record PrintSpineModel(
    string Kind,
    decimal? InchesPerPage = null,
    IReadOnlyList<PrintSpineAnchor>? Anchors = null);

public sealed record PrintProductDefinition(
    string Key,
    PublicationVendor Vendor,
    PublicationEditionFormat Format,
    string DisplayName,
    PrintBindingConstruction Binding,
    PrintInteriorProcess InteriorProcess,
    string PaperName,
    int? BasisWeightPounds,
    int? Gsm,
    PrintCoverMaterial CoverMaterial,
    IReadOnlyList<PrintFinish> Finishes,
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
    public bool RequiresDustJacket => CoverMaterial is PrintCoverMaterial.DigitalClothBlueWithJacket
        or PrintCoverMaterial.DigitalClothGrayWithJacket
        or PrintCoverMaterial.JacketedCaseLaminate;
    public bool RequiresClothManifest => CoverMaterial is PrintCoverMaterial.DigitalClothBlue
        or PrintCoverMaterial.DigitalClothGray
        or PrintCoverMaterial.DigitalClothBlueWithJacket
        or PrintCoverMaterial.DigitalClothGrayWithJacket;
    public bool RequiresPerfectBoundCover => Binding == PrintBindingConstruction.PerfectBound;
}

public sealed record PrintProductRegistrySnapshot(
    string RegistryVersion,
    DateTime ReviewedAtUtc,
    IReadOnlyList<string> Sources,
    IReadOnlyList<PrintProductDefinition> Products);

public interface IPrintProductRegistry
{
    string Version { get; }
    string Sha256 { get; }
    IReadOnlyList<PrintProductDefinition> List(PublicationEditionFormat? format = null, PublicationVendor? vendor = null);
    PrintProductDefinition GetRequired(string key);
    PrintProductDefinition GetDefault(PublicationEditionFormat format, PublicationVendor vendor);
}

public sealed class PrintProductRegistry : IPrintProductRegistry
{
    private const string ResourceSuffix = "PrintProducts.print-products-v1.json";
    private readonly PrintProductRegistrySnapshot _snapshot;
    private readonly IReadOnlyDictionary<string, PrintProductDefinition> _products;

    public PrintProductRegistry()
    {
        var assembly = typeof(PrintProductRegistry).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The bundled print-product registry is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var data = buffer.ToArray();
        Sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        _snapshot = JsonSerializer.Deserialize<PrintProductRegistrySnapshot>(data, JsonOptions)
            ?? throw new InvalidDataException("The bundled print-product registry is invalid.");
        _products = _snapshot.Products.ToDictionary(item => item.Key, StringComparer.Ordinal);
        if (_products.Count == 0 || _products.Count != _snapshot.Products.Count)
            throw new InvalidDataException("The bundled print-product registry is empty or contains duplicate keys.");
        foreach (var product in _snapshot.Products)
        {
            if (product.Format is not (PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover)
                || string.IsNullOrWhiteSpace(product.Key)
                || string.IsNullOrWhiteSpace(product.PaperName)
                || product.MinimumPages <= 0
                || product.MaximumPages < product.MinimumPages
                || (product.MinimumSubmittedPages ?? product.MinimumPages) <= 0
                || (product.MaximumSubmittedPages ?? product.MaximumPages) < (product.MinimumSubmittedPages ?? product.MinimumPages)
                || product.Finishes.Count == 0
                || product.CoverModes.Count == 0
                || product.EffectiveSupportedProjectUses.Count == 0
                || !product.EffectiveSupportedProjectUses.Contains(product.DefaultProjectUse)
                || product.EffectiveSupportedProjectUses.Distinct().Count() != product.EffectiveSupportedProjectUses.Count)
                throw new InvalidDataException($"Print product '{product.Key}' has an incomplete identity or availability range.");
            if (product.Vendor is not (PublicationVendor.Generic or PublicationVendor.BarnesAndNoblePress)
                && product.SpineModel.Kind == "TemplateRequired")
                throw new InvalidDataException($"Specific print product '{product.Key}' cannot use generic printer geometry.");
            if (product.SpineModel.Kind == "FrozenLookup")
            {
                var expectedPages = Enumerable.Range(product.MinimumPages, product.MaximumPages - product.MinimumPages + 1)
                    .Where(page => page % 2 == 0).ToArray();
                var actualPages = (product.SpineModel.Anchors ?? []).Select(anchor => anchor.Pages).Order().ToArray();
                if (!expectedPages.SequenceEqual(actualPages)
                    || (product.SpineModel.Anchors ?? []).Any(anchor => anchor.Inches <= 0))
                    throw new InvalidDataException($"Print product '{product.Key}' does not contain a complete exact spine table.");
            }
        }
    }

    public string Version => _snapshot.RegistryVersion;
    public string Sha256 { get; }

    public IReadOnlyList<PrintProductDefinition> List(PublicationEditionFormat? format = null, PublicationVendor? vendor = null) =>
        _snapshot.Products
            .Where(item => format is null || item.Format == format)
            .Where(item => vendor is null || item.Vendor == vendor)
            .OrderBy(item => item.Vendor)
            .ThenBy(item => item.DisplayName, StringComparer.Ordinal)
            .ToArray();

    public PrintProductDefinition GetRequired(string key) =>
        _products.TryGetValue(key, out var product)
            ? product
            : throw new InvalidOperationException($"Print product '{key}' is not present in registry {Version}.");

    public PrintProductDefinition GetDefault(PublicationEditionFormat format, PublicationVendor vendor)
    {
        var key = (format, vendor) switch
        {
            (PublicationEditionFormat.Paperback, PublicationVendor.AmazonKdp) => "kdp-pb-bw-white",
            (PublicationEditionFormat.Paperback, PublicationVendor.IngramSpark) => "ingram-pb-bw-white50",
            (PublicationEditionFormat.Paperback, PublicationVendor.BarnesAndNoblePress) => "bn-pb-bw-cream50-6x9",
            (PublicationEditionFormat.Paperback, _) => "generic-perfectbound-template",
            (PublicationEditionFormat.Hardcover, PublicationVendor.AmazonKdp) => "kdp-hc-bw-white",
            (PublicationEditionFormat.Hardcover, PublicationVendor.IngramSpark) => "ingram-hc-case-bw-white50",
            (PublicationEditionFormat.Hardcover, PublicationVendor.BarnesAndNoblePress) => "bn-hc-case-bw-cream50-6x9",
            (PublicationEditionFormat.Hardcover, _) => "generic-casebound-template",
            _ => throw new InvalidOperationException("Digital releases do not use a physical print product."),
        };
        return GetRequired(key);
    }

    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

public sealed record PrintTemplateEvidence(
    decimal TrimWidthInches,
    decimal TrimHeightInches,
    decimal BleedInches,
    decimal SafeInches,
    decimal WrapInches,
    decimal HingeInches,
    decimal GutterInches,
    decimal FlapInches,
    decimal BarcodeWidthInches,
    decimal BarcodeHeightInches,
    decimal? InchesPerPage,
    int MinimumPages,
    int MaximumPages,
    string PdfStandard,
    string? UnderlayAssetId = null,
    string Provider = "",
    string ProductKey = "",
    int PageCount = 0,
    string GeometryFingerprint = "",
    decimal? SpineWidthInches = null,
    decimal? FullCoverWidthInches = null,
    decimal? FullCoverHeightInches = null,
    decimal? FrontCoverWidthInches = null,
    decimal? FrontCoverHeightInches = null,
    decimal? BackCoverWidthInches = null,
    decimal? BackCoverHeightInches = null,
    string FullCoverTemplateSha256 = "",
    string FrontCoverTemplateSha256 = "",
    string BackCoverTemplateSha256 = "");

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

public sealed class PrintGeometryService(IPrintProductRegistry registry) : IPrintGeometryService
{
    public PrintCoverGeometry Calculate(PublicationEdition edition, int submittedPageCount, string? surfaceRole = null)
    {
        var product = registry.GetRequired(edition.PrintProductKey);
        var normalizedPages = product.Vendor switch
        {
            PublicationVendor.AmazonKdp => submittedPageCount + (submittedPageCount % 2),
            PublicationVendor.IngramSpark => submittedPageCount + (submittedPageCount % 2),
            PublicationVendor.BarnesAndNoblePress => submittedPageCount + (submittedPageCount % 2),
            _ => submittedPageCount,
        };
        if (submittedPageCount < (product.MinimumSubmittedPages ?? product.MinimumPages)
            || submittedPageCount > (product.MaximumSubmittedPages ?? product.MaximumPages))
            throw new InvalidOperationException($"{product.DisplayName} supports {product.MinimumSubmittedPages ?? product.MinimumPages}-{product.MaximumSubmittedPages ?? product.MaximumPages} submitted pages; this interior has {submittedPageCount}.");
        if (normalizedPages < product.MinimumPages || normalizedPages > product.MaximumPages)
            throw new InvalidOperationException($"{product.DisplayName} supports {product.MinimumPages}–{product.MaximumPages} pages; this interior has {normalizedPages}.");

        var template = ReadTemplateEvidence(edition, product);
        if (template is not null)
        {
            if (Math.Abs(template.TrimWidthInches - (decimal)edition.PageWidthInches) > 0.0001m
                || Math.Abs(template.TrimHeightInches - (decimal)edition.PageHeightInches) > 0.0001m)
                throw new InvalidOperationException("The imported print template trim does not match this release.");
            if (normalizedPages < template.MinimumPages || normalizedPages > template.MaximumPages)
                throw new InvalidOperationException($"The imported print template supports {template.MinimumPages}-{template.MaximumPages} pages; this interior has {normalizedPages}.");
            if (string.IsNullOrWhiteSpace(template.PdfStandard))
                throw new InvalidOperationException("The imported print template must declare its required PDF standard.");
            if (product.Vendor == PublicationVendor.BarnesAndNoblePress
                && (template.PageCount != normalizedPages
                    || template.SpineWidthInches is null or <= 0
                    || template.FullCoverWidthInches is null or <= 0
                    || template.FullCoverHeightInches is null or <= 0
                    || template.FrontCoverWidthInches is null or <= 0
                    || template.FrontCoverHeightInches is null or <= 0
                    || template.BackCoverWidthInches is null or <= 0
                    || template.BackCoverHeightInches is null or <= 0))
                throw new InvalidOperationException("The B&N Press template evidence must exactly match this interior page count and declare measured full, front, and back geometry.");
        }
        var spine = CalculateSpine(product, template, normalizedPages);
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
            PublicationVendor.BarnesAndNoblePress when template is not null =>
                (template.BleedInches, template.WrapInches, template.HingeInches, template.GutterInches, template.FlapInches, 0m,
                    template.FullCoverWidthInches!.Value,
                    template.FullCoverHeightInches!.Value),
            _ when template is not null =>
                (template.BleedInches, template.WrapInches, template.HingeInches, template.GutterInches, template.FlapInches, 0m,
                    2 * trimWidth + spine + 2 * template.BleedInches + 2 * template.WrapInches + 2 * template.GutterInches + 2 * template.FlapInches,
                    trimHeight + 2 * template.BleedInches + 2 * template.WrapInches),
            _ => throw new InvalidOperationException("Generic print products require complete printer geometry before preparation."),
        };

        var fingerprintSource = FormattableString.Invariant($"{registry.Version}|{product.Key}|{edition.PrintFinish}|{edition.PrintCoverMode}|{trimWidth:0.####}|{trimHeight:0.####}|{normalizedPages}|{spine:0.#####}|{surfaceWidth:0.#####}|{surfaceHeight:0.#####}");
        var fingerprint = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fingerprintSource))).ToLowerInvariant();
        return new(submittedPageCount, normalizedPages, normalizedPages, spine, surfaceWidth, surfaceHeight,
            trimWidth, trimHeight, bleed, wrap, hinge, gutter, flap, insideNoInk, fingerprint)
        {
            BackRegionWidthInches = product.Vendor == PublicationVendor.BarnesAndNoblePress
                ? template?.BackCoverWidthInches ?? 0
                : 0,
            FrontRegionWidthInches = product.Vendor == PublicationVendor.BarnesAndNoblePress
                ? template?.FrontCoverWidthInches ?? 0
                : 0,
            CoverRegionYInches = product.Vendor == PublicationVendor.BarnesAndNoblePress
                ? Math.Max(0, (surfaceHeight - (template?.FrontCoverHeightInches ?? surfaceHeight)) / 2)
                : 0,
            CoverRegionHeightInches = product.Vendor == PublicationVendor.BarnesAndNoblePress
                ? template?.FrontCoverHeightInches ?? 0
                : 0,
        };
    }

    private static decimal CalculateSpine(PrintProductDefinition product, PrintTemplateEvidence? template, int pages)
    {
        if (product.SpineModel.Kind == "TemplateRequired")
        {
            if (product.Vendor == PublicationVendor.BarnesAndNoblePress)
            {
                if (template?.PageCount != pages || template.SpineWidthInches is not decimal measuredSpine || measuredSpine <= 0)
                    throw new InvalidOperationException("The B&N Press template evidence does not contain an exact spine measurement for this page count.");
                return measuredSpine;
            }
            if (template?.InchesPerPage is not decimal caliper)
                throw new InvalidOperationException("The imported print template must declare its spine model.");
            return decimal.Round(caliper * pages, 5, MidpointRounding.AwayFromZero);
        }
        if (product.SpineModel.Kind == "Caliper" && product.SpineModel.InchesPerPage is decimal inchesPerPage)
            return decimal.Round(inchesPerPage * pages, 5, MidpointRounding.AwayFromZero);
        var anchors = product.SpineModel.Anchors?.OrderBy(item => item.Pages).ToArray() ?? [];
        if (anchors.Length == 0)
            throw new InvalidOperationException($"Print product '{product.Key}' has no spine evidence.");
        var exact = anchors.FirstOrDefault(item => item.Pages == pages);
        if (exact is not null)
            return exact.Inches;
        throw new InvalidOperationException($"Print product '{product.Key}' has no verified spine measurement for {pages} normalized pages.");
    }

    private static PrintTemplateEvidence? ReadTemplateEvidence(PublicationEdition edition, PrintProductDefinition product)
    {
        if (product.SpineModel.Kind != "TemplateRequired")
            return null;
        if (string.IsNullOrWhiteSpace(edition.PrintTemplateEvidenceJson))
            return null;
        try
        {
            var evidence = JsonSerializer.Deserialize<PrintTemplateEvidence>(edition.PrintTemplateEvidenceJson)
                ?? throw new InvalidOperationException("The imported print template evidence is empty.");
            if (!string.IsNullOrWhiteSpace(evidence.ProductKey)
                && !string.Equals(evidence.ProductKey, product.Key, StringComparison.Ordinal))
                throw new InvalidOperationException("The imported print template belongs to a different print product.");
            return evidence;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The imported print template evidence is invalid.", exception);
        }
    }
}
