using System.Globalization;
using System.Text.Json.Serialization;
using Lorekeeper.Models;
using Lorekeeper.Manuscripts;
using Lorekeeper.Citations;

namespace Lorekeeper.Publish;

public static class PublicationLanguage
{
    private static readonly IReadOnlyDictionary<string, string> KnownNames = BuildKnownNames();

    /// <summary>Schema guidance for every language field; keep it in step with <see cref="SupportedOptions"/>.</summary>
    public const string FieldDescription =
        "Language tag. Use exactly one of: en (English), en-US (English (United States)), en-GB (English (United Kingdom)). Describe spelling and usage conventions in Book Brief House style instead.";

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
        return Resolve(candidate) ?? candidate;
    }

    public static bool IsPressSupported(string? value) => SupportedTag(value) is not null;

    /// <summary>True when the value is already stored exactly as a supported tag.</summary>
    public static bool IsCanonical(string? value) => value is not null && SupportedTag(value) == value;

    public static string SupportedOptionsText { get; } =
        string.Join(", ", SupportedOptions.Select(item => $"{item.Tag} ({item.Label})"));

    /// <summary>States the fix when Press rejects a language anywhere in a render request.</summary>
    public static string PressUnsupportedGuidance { get; } =
        $"Change the book language, release language, and any language marks or figure languages to one of: {SupportedOptionsText}.";

    /// <summary>Returns the canonical supported tag, or rejects the value naming the field and its valid options.</summary>
    public static string RequirePressSupported(string? value, string field, string? guidance = null) =>
        SupportedTag(value) ?? throw new CorrectableInputException(
            $"{field} must be one of: {SupportedOptionsText}." + (guidance is null ? string.Empty : $" {guidance}"));

    /// <summary>
    /// Repairs a stored or imported language that predates entry validation. Free-form wording such as
    /// "English (UK spelling: colour)" names a language before any qualifier and an optional region first
    /// inside parentheses. Note carries the original wording whenever the tag alone does not preserve it.
    /// </summary>
    public static (string? Tag, string? Note) Repair(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (null, null);
        if (SupportedTag(value) is { } exact)
            return (exact, null);

        var original = value.Trim();
        var candidate = original.Replace('_', '-');
        var qualifier = candidate.IndexOfAny(['(', ',', ';', ':']);
        if (qualifier <= 0)
            return (null, original);
        var language = candidate[..qualifier].Trim();
        if (candidate[qualifier] == '(')
        {
            var detail = candidate[(qualifier + 1)..].Split([')', ',', ';', ':'], 2)[0].Trim();
            if ((SupportedTag($"{language} ({detail})") ?? SupportedTag($"{language} ({detail.Split(' ', 2)[0]})")) is { } regional)
                return (regional, original);
        }

        return (SupportedTag(language), original);
    }

    /// <summary>Repairs an unsupported Book Brief language, keeping its original wording in House style.</summary>
    public static bool RepairStored(BookBrief brief)
    {
        if (brief.LanguageLocale.Length == 0 || IsCanonical(brief.LanguageLocale))
            return false;
        var (tag, note) = Repair(brief.LanguageLocale);
        brief.LanguageLocale = tag ?? string.Empty;
        brief.HouseStyle = WithLanguageNote(brief.HouseStyle, note);
        return true;
    }

    /// <summary>Repairs an unsupported Core Book or release language; values that name no supported language become English.</summary>
    public static bool RepairStored(PublicationBook book) => RepairStored(book.Language, value => book.Language = value);

    public static bool RepairStored(PublicationEdition edition) => RepairStored(edition.Language, value => edition.Language = value);

    /// <summary>Appends an unsupported language description to House style so repair never drops authored wording.</summary>
    public static string WithLanguageNote(string houseStyle, string? note)
    {
        if (string.IsNullOrWhiteSpace(note) || houseStyle.Contains(note, StringComparison.Ordinal))
            return houseStyle;
        var line = $"Language notes: {note}";
        return string.IsNullOrWhiteSpace(houseStyle) ? line : $"{houseStyle.TrimEnd()}\n\n{line}";
    }

    /// <summary>The Core Book or release language a stored or imported value repairs to.</summary>
    public static string Repaired(string? value) => Repair(value).Tag ?? "en";

    private static bool RepairStored(string value, Action<string> assign)
    {
        if (IsCanonical(value))
            return false;
        assign(Repaired(value));
        return true;
    }

    private static string? SupportedTag(string? value)
    {
        var normalized = NormalizeOptional(value);
        return SupportedOptions.FirstOrDefault(item => string.Equals(item.Tag, normalized, StringComparison.OrdinalIgnoreCase))?.Tag;
    }

    private static string? Resolve(string candidate)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(candidate);
            if (!string.IsNullOrWhiteSpace(culture.Name))
                return culture.Name;
        }
        catch (CultureNotFoundException)
        {
        }

        return KnownNames.GetValueOrDefault(candidate);
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
    Docx,
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
    public string PrintArtifactProfileKey { get; init; } = string.Empty;
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
    string PrintArtifactRegistryVersion,
    string PrintArtifactProfileKey,
    PrintCoverMode PrintCoverMode,
    bool Bleed,
    bool AllowDesignedPageOverrides,
    bool RectoChapterStarts)
{
    public bool InheritsCoreCover { get; init; }
    public bool EditionSpecificContentEnabled { get; init; }
    public PrintProjectUse PrintProjectUse { get; init; } = PrintProjectUse.ForSale;
    public PrintIdentifierMode PrintIdentifierMode { get; init; } = PrintIdentifierMode.UserSuppliedIsbn;
    public PrintCoverSubmissionMode PrintCoverSubmissionMode { get; init; } = PrintCoverSubmissionMode.FullWrapMeasured;
    public CitationStyle CitationStyle { get; init; } = CitationStyle.Chicago18NotesBibliography;
    public PrinterDimensions? PrinterDimensions { get; init; }
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
    string? PrintArtifactProfileKey = null,
    PrintCoverMode? PrintCoverMode = null,
    bool? AllowDesignedPageOverrides = null,
    bool? RectoChapterStarts = null,
    string? Title = null,
    string? Subtitle = null,
    string? Author = null,
    [property: System.ComponentModel.Description(PublicationLanguage.FieldDescription)] string? Language = null,
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
    PrintCoverSubmissionMode? PrintCoverSubmissionMode = null,
    CitationStyle? CitationStyle = null,
    PrinterDimensions? PrinterDimensions = null);

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
    public CitationStyle CitationStyle { get; init; } = CitationStyle.Chicago18NotesBibliography;
    public IReadOnlyList<CitationRecord> BibliographicRecords { get; init; } = [];
    public CitationFormattingResult Citations { get; init; } = new(
        ICitationFormatter.FormatterIdentity,
        CitationStyle.Chicago18NotesBibliography,
        [],
        "Bibliography",
        [],
        []);
    public IReadOnlyDictionary<string, CompositionScene> CoverSurfaceScenes { get; init; } =
        new Dictionary<string, CompositionScene>();

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
    string Description,
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
    IReadOnlyList<PublishDesignedPageDocument> DesignedPages);

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
    public double BleedInches { get; init; }
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
    IReadOnlyList<PublishDesignedPageDocument> DesignedPages);

public sealed record PublishDesignedPageDocument(
    Guid Id,
    string Name,
    ManuscriptDocument SemanticManuscript,
    string AccessibilityDescription,
    long Revision,
    IReadOnlyList<PublishDesignedPageVariantDocument> Variants);

public sealed record PublishDesignedPageVariantDocument(
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
