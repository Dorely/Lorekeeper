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
    int? MaximumSubmittedPages = null)
{
    public bool RequiresCaseCover => CoverMaterial is PrintCoverMaterial.CaseLaminate or PrintCoverMaterial.JacketedCaseLaminate
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
                || product.CoverModes.Count == 0)
                throw new InvalidDataException($"Print product '{product.Key}' has an incomplete identity or availability range.");
            if (product.Vendor != PublicationVendor.Generic && product.SpineModel.Kind == "TemplateRequired")
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
            (PublicationEditionFormat.Paperback, _) => "generic-perfectbound-template",
            (PublicationEditionFormat.Hardcover, PublicationVendor.AmazonKdp) => "kdp-hc-bw-white",
            (PublicationEditionFormat.Hardcover, PublicationVendor.IngramSpark) => "ingram-hc-case-bw-white50",
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

public sealed record GenericPrintTemplate(
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
    string? UnderlayAssetId = null);

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
    string GeometryFingerprint);

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
            _ => submittedPageCount,
        };
        if (submittedPageCount < (product.MinimumSubmittedPages ?? product.MinimumPages)
            || submittedPageCount > (product.MaximumSubmittedPages ?? product.MaximumPages))
            throw new InvalidOperationException($"{product.DisplayName} supports {product.MinimumSubmittedPages ?? product.MinimumPages}-{product.MaximumSubmittedPages ?? product.MaximumPages} submitted pages; this interior has {submittedPageCount}.");
        if (normalizedPages < product.MinimumPages || normalizedPages > product.MaximumPages)
            throw new InvalidOperationException($"{product.DisplayName} supports {product.MinimumPages}–{product.MaximumPages} pages; this interior has {normalizedPages}.");

        var template = ReadGenericTemplate(edition, product);
        if (template is not null)
        {
            if (Math.Abs(template.TrimWidthInches - (decimal)edition.PageWidthInches) > 0.0001m
                || Math.Abs(template.TrimHeightInches - (decimal)edition.PageHeightInches) > 0.0001m)
                throw new InvalidOperationException("The generic printer template trim does not match this release.");
            if (normalizedPages < template.MinimumPages || normalizedPages > template.MaximumPages)
                throw new InvalidOperationException($"The generic printer template supports {template.MinimumPages}-{template.MaximumPages} pages; this interior has {normalizedPages}.");
            if (string.IsNullOrWhiteSpace(template.PdfStandard))
                throw new InvalidOperationException("The generic printer template must declare its required PDF standard.");
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
            _ when template is not null =>
                (template.BleedInches, template.WrapInches, template.HingeInches, template.GutterInches, template.FlapInches, 0m,
                    2 * trimWidth + spine + 2 * template.BleedInches + 2 * template.WrapInches + 2 * template.GutterInches + 2 * template.FlapInches,
                    trimHeight + 2 * template.BleedInches + 2 * template.WrapInches),
            _ => throw new InvalidOperationException("Generic print products require complete printer geometry before preparation."),
        };

        var fingerprintSource = FormattableString.Invariant($"{registry.Version}|{product.Key}|{edition.PrintFinish}|{edition.PrintCoverMode}|{trimWidth:0.####}|{trimHeight:0.####}|{normalizedPages}|{spine:0.#####}|{surfaceWidth:0.#####}|{surfaceHeight:0.#####}");
        var fingerprint = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fingerprintSource))).ToLowerInvariant();
        return new(submittedPageCount, normalizedPages, normalizedPages, spine, surfaceWidth, surfaceHeight,
            trimWidth, trimHeight, bleed, wrap, hinge, gutter, flap, insideNoInk, fingerprint);
    }

    private static decimal CalculateSpine(PrintProductDefinition product, GenericPrintTemplate? template, int pages)
    {
        if (product.SpineModel.Kind == "TemplateRequired")
        {
            if (template?.InchesPerPage is not decimal caliper)
                throw new InvalidOperationException("The generic print template must declare its spine model.");
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

    private static GenericPrintTemplate? ReadGenericTemplate(PublicationEdition edition, PrintProductDefinition product)
    {
        if (product.Vendor != PublicationVendor.Generic)
            return null;
        if (string.IsNullOrWhiteSpace(edition.GenericPrintTemplateJson))
            return null;
        try
        {
            return JsonSerializer.Deserialize<GenericPrintTemplate>(edition.GenericPrintTemplateJson);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The generic printer template is invalid.", exception);
        }
    }
}
