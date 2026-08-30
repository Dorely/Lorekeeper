using System.Globalization;
using System.Text.Json.Serialization;
using Lorekeeper.Models;
using Lorekeeper.Manuscripts;

namespace Lorekeeper.Publish;

public static class PublicationLanguage
{
    private static readonly IReadOnlyDictionary<string, string> KnownNames = BuildKnownNames();

    public static IReadOnlyList<PublicationLanguageOption> SupportedOptions { get; } =
    [
        new("en", "English"),
        new("en-US", "English (United States)"),
        new("en-GB", "English (United Kingdom)"),
    ];

    public static string Normalize(string? value, string fallback = "en") =>
        NormalizeOptional(value) ?? fallback;

    public static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var candidate = value.Trim().Replace('_', '-');
        try
        {
            var culture = CultureInfo.GetCultureInfo(candidate);
            if (!string.IsNullOrWhiteSpace(culture.Name))
                return culture.Name;
        }
        catch (CultureNotFoundException)
        {
        }

        return KnownNames.GetValueOrDefault(candidate, candidate);
    }

    public static bool IsPressSupported(string? value)
    {
        var normalized = NormalizeOptional(value);
        return SupportedOptions.Any(item => string.Equals(item.Tag, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyDictionary<string, string> BuildKnownNames()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures | CultureTypes.SpecificCultures)
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .OrderBy(item => item.IsNeutralCulture ? 0 : 1)
            .ThenBy(item => item.Name, StringComparer.Ordinal))
        {
            result.TryAdd(culture.Name, culture.Name);
            result.TryAdd(culture.IetfLanguageTag, culture.Name);
            result.TryAdd(culture.EnglishName, culture.Name);
            if (culture.IsNeutralCulture)
                result.TryAdd(culture.TwoLetterISOLanguageName, culture.Name);
        }

        result["American English"] = "en-US";
        result["British English"] = "en-GB";
        result["English (US)"] = "en-US";
        result["English (UK)"] = "en-GB";
        return result;
    }
}

public sealed record PublicationLanguageOption(string Tag, string Label);

[JsonConverter(typeof(JsonStringEnumConverter<PublishExportFormat>))]
public enum PublishExportFormat
{
    PlainText,
    Markdown,
    Epub,
}

public sealed record PublishWorkspaceView(
    PublicationEditionView Edition,
    IReadOnlyList<PublicationEditionSummary> Editions,
    IReadOnlyList<PublishSectionView> Sections,
    string SourceFingerprint)
{
    public IReadOnlySet<PublicationEditionOverrideField> OverrideFields { get; init; } = new HashSet<PublicationEditionOverrideField>();
    public bool HasContentOverrides { get; init; }
    public IReadOnlyList<PublicationSectionView> PublicationSections { get; init; } = [];
}

public sealed record PublicationEditionSummary(
    Guid Id,
    string Name,
    PublicationEditionFormat Format,
    PublicationVendor Vendor,
    PublicationEditionStatus Status,
    long Revision,
    double PageWidthInches,
    double PageHeightInches,
    double PageMarginInches,
    bool Bleed,
    bool AllowDesignedPageOverrides,
    bool RectoChapterStarts)
{
    public string PrintProductKey { get; init; } = string.Empty;
    public PrintCoverMode PrintCoverMode { get; init; }
    public PrintProjectUse PrintProjectUse { get; init; } = PrintProjectUse.ForSale;
}

public sealed record PublicationEditionView(
    Guid Id,
    string Name,
    PublicationEditionFormat Format,
    PublicationVendor Vendor,
    string VendorProfileVersion,
    PublicationEditionStatus Status,
    long Revision,
    string ProjectName,
    string ProjectSlug,
    string TitleOverride,
    string Subtitle,
    string Author,
    string Language,
    string Publisher,
    string Copyright,
    string Isbn,
    string Description,
    bool IncludeTableOfContents,
    bool IncludeVisibleTableOfContents,
    bool IncludeActSynopses,
    bool IncludeChapterSynopses,
    bool IncludeActHeadings,
    bool IncludeChapterHeadings,
    bool NumberActs,
    bool NumberChapters,
    PublishTitlePageMode TitlePageMode,
    double PageWidthInches,
    double PageHeightInches,
    double PageMarginInches,
    double BodyFontSizePoints,
    double BodyLineHeight,
    Guid? SelectedCoverImageId,
    string PrintRegistryVersion,
    string PrintProductKey,
    PrintFinish PrintFinish,
    PrintCoverMode PrintCoverMode,
    string PrintTemplateEvidenceJson,
    bool Bleed,
    bool AllowDesignedPageOverrides,
    bool RectoChapterStarts)
{
    public bool InheritsCoreCover { get; init; }
    public bool EditionSpecificContentEnabled { get; init; }
    public PrintProjectUse PrintProjectUse { get; init; } = PrintProjectUse.ForSale;
    public PrintIdentifierMode PrintIdentifierMode { get; init; } = PrintIdentifierMode.UserSuppliedIsbn;
    public PrintCoverSubmissionMode PrintCoverSubmissionMode { get; init; } = PrintCoverSubmissionMode.FullWrapMeasured;
}

public sealed record PublicationEditionCreate(
    string Name,
    PublicationEditionFormat Format,
    PublicationVendor Vendor = PublicationVendor.Generic);

public sealed record PublicationReleaseOverridePatch(
    long ExpectedRevision,
    string? Name = null,
    PublicationVendor? Destination = null,
    string? Isbn = null,
    string? PrintProductKey = null,
    PrintFinish? PrintFinish = null,
    PrintCoverMode? PrintCoverMode = null,
    string? PrintTemplateEvidenceJson = null,
    bool? AllowDesignedPageOverrides = null,
    bool? RectoChapterStarts = null,
    string? Title = null,
    string? Subtitle = null,
    string? Author = null,
    string? Language = null,
    string? Publisher = null,
    string? Copyright = null,
    string? Description = null,
    bool? IncludeTableOfContents = null,
    bool? IncludeVisibleTableOfContents = null,
    bool? IncludeActSynopses = null,
    bool? IncludeChapterSynopses = null,
    bool? IncludeActHeadings = null,
    bool? IncludeChapterHeadings = null,
    bool? NumberActs = null,
    bool? NumberChapters = null,
    PublishTitlePageMode? TitlePageMode = null,
    double? PageWidthInches = null,
    double? PageHeightInches = null,
    double? PageMarginInches = null,
    IReadOnlyList<PublicationEditionOverrideField>? ResetFields = null,
    PrintProjectUse? PrintProjectUse = null,
    PrintIdentifierMode? PrintIdentifierMode = null,
    PrintCoverSubmissionMode? PrintCoverSubmissionMode = null);

public sealed record PublicationEditionCompareView(
    PublicationEditionSummary Left,
    PublicationEditionSummary Right,
    IReadOnlyList<string> Differences);

public sealed record PublicationEditionAuditView(
    Guid Id,
    string Action,
    string Actor,
    string BeforeHash,
    string AfterHash,
    string DetailJson,
    DateTime CreatedAt);

public sealed record PublishSectionView(
    Guid? ActId,
    string Title,
    bool IsUnassigned,
    bool IsIncluded,
    IReadOnlyList<PublishChapterView> Chapters);

public sealed record PublishChapterView(
    Guid Id,
    Guid? ActId,
    string Title,
    bool IsIncluded,
    int FigureCount,
    int DesignedPageCount,
    int LayoutDiagnosticCount);

public sealed record PublicationEditionOutlineItemUpdate(
    PublishOutlineTargetKind TargetKind,
    Guid TargetId,
    bool IsIncluded);

public sealed record PublishDocument(
    Guid EditionId,
    Guid ProjectId,
    string ProjectName,
    string ProjectSlug,
    DateTime ExportedAtUtc,
    PublishDocumentProfile Profile,
    PublishAssetDocument? CoverAsset,
    IReadOnlyList<PublishSectionDocument> Sections,
    IReadOnlyList<PublishAssetDocument> Assets)
{
    public string SourceFingerprint { get; init; } = string.Empty;
    public IReadOnlyList<PublishManuscriptStyleDocument> NamedStyles { get; init; } = [];
    public IReadOnlyList<PublishFontDocument> Fonts { get; init; } = [];
    public IReadOnlyList<PublishPublicationSectionDocument> PublicationSections { get; init; } = [];
    public PublishCoverDocument? Cover { get; init; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Profile.TitleOverride)
        ? ProjectName
        : Profile.TitleOverride.Trim();
}

public sealed record PublishFontDocument(
    string FamilyKey,
    Guid FamilyId,
    string FamilyName,
    Guid FaceId,
    string FileName,
    string ContentType,
    int Weight,
    bool Italic,
    byte[] Data);

public sealed record PublishCoverDocument(
    string Title,
    string Subtitle,
    string Author,
    string SpineText,
    string BackCopy,
    string BackgroundColor,
    CompositionScene Scene);

public sealed record PublishPublicationSectionDocument(
    Guid Id,
    Guid? CoreSectionId,
    string Title,
    PublicationSectionKind Kind,
    PublicationSectionSystemRole SystemRole,
    PublicationSectionAnchor Anchor,
    PublishOutlineTargetKind? TargetKind,
    Guid? TargetId,
    int LocalOrder,
    PublicationSectionStartSide StartSide,
    ManuscriptDocument Manuscript,
    IReadOnlyList<PublishPageCompositionDocument> PageCompositions);

public sealed record PublishManuscriptStyleDocument(
    string Name,
    ManuscriptStyleKind Kind,
    string SemanticRole,
    ManuscriptStyleProperties Definition);

public sealed record PublishDocumentProfile(
    string TitleOverride,
    string Subtitle,
    string Author,
    string Language,
    string Publisher,
    string Copyright,
    string Isbn,
    string Description,
    bool IncludeTableOfContents,
    bool IncludeVisibleTableOfContents,
    bool IncludeActSynopses,
    bool IncludeChapterSynopses,
    bool IncludeActHeadings,
    bool IncludeChapterHeadings,
    bool NumberActs,
    bool NumberChapters,
    bool IncludeTitlePage,
    double PageWidthInches,
    double PageHeightInches,
    double PageMarginInches,
    double BodyFontSizePoints,
    double BodyLineHeight)
{
    public bool AllowDesignedPageOverrides { get; init; }
    public bool RectoChapterStarts { get; init; }
}

public sealed record PublishSectionDocument(
    Guid? ActId,
    string Title,
    string Synopsis,
    bool IsUnassigned,
    bool IncludePage,
    bool IncludeHeading,
    int Order,
    IReadOnlyList<PublishChapterDocument> Chapters);

public sealed record PublishChapterDocument(
    Guid Id,
    Guid? ActId,
    string Title,
    string PlainText,
    string Synopsis,
    int Order,
    bool IncludeHeading,
    ManuscriptDocument Manuscript,
    IReadOnlyList<PublishPageCompositionDocument> PageCompositions);

public sealed record PublishPageCompositionDocument(
    Guid Id,
    string Name,
    ManuscriptDocument SemanticManuscript,
    long Revision,
    IReadOnlyList<PublishPageCompositionVariantDocument> Variants);

public sealed record PublishPageCompositionVariantDocument(
    Guid Id,
    string GeometryKey,
    CompositionScene Scene,
    long Revision);

public sealed record PublishAssetDocument(
    Guid Id,
    string FileName,
    string ContentType,
    byte[] Data,
    string AltText);
