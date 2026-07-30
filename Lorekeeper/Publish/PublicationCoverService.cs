using System.Security.Cryptography;
using System.Text;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Publish;

public sealed record PublicationCoverDesignView(
    Guid Id,
    Guid EditionId,
    string Title,
    string Subtitle,
    string Author,
    string SpineText,
    string BackCopy,
    string BackgroundColor,
    PublicationBarcodeMode BarcodeMode,
    double ImageFocalXPercent,
    double ImageFocalYPercent,
    long Revision,
    PublicationCoverTemplate Template,
    IReadOnlyList<string> Diagnostics);

public sealed record PublicationCoverTemplate(
    int PageCount,
    double TrimWidthInches,
    double TrimHeightInches,
    double BleedInches,
    double SpineWidthInches,
    double FullWidthInches,
    double FullHeightInches,
    double SafetyInches,
    double BarcodeWidthInches,
    double BarcodeHeightInches,
    string Fingerprint,
    bool IsAcknowledged);

public sealed record PublicationCoverDesignUpdate(
    string Title,
    string Subtitle,
    string Author,
    string SpineText,
    string BackCopy,
    string BackgroundColor,
    PublicationBarcodeMode BarcodeMode,
    double ImageFocalXPercent,
    double ImageFocalYPercent,
    long ExpectedRevision,
    bool AcknowledgeTemplate);

public interface IPublicationCoverService
{
    Task<PublicationCoverDesignView> GetAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default);
    Task<PublicationCoverDesignView> UpdateAsync(Guid projectId, Guid editionId, PublicationCoverDesignUpdate update, CancellationToken cancellationToken = default);
}

public sealed class PublicationCoverService(
    AppDbContext db,
    IProjectMutationCoordinator projectMutations) : IPublicationCoverService
{
    public async Task<PublicationCoverDesignView> GetAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken = default)
    {
        var edition = await GetEditionAsync(projectId, editionId, cancellationToken);
        var design = await db.PublicationCoverDesigns.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.EditionId == editionId, cancellationToken)
            ?? Default(edition);
        return await ViewAsync(edition, design, cancellationToken);
    }

    public async Task<PublicationCoverDesignView> UpdateAsync(
        Guid projectId,
        Guid editionId,
        PublicationCoverDesignUpdate update,
        CancellationToken cancellationToken = default)
    {
        Validate(update);
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var edition = await GetEditionAsync(projectId, editionId, cancellationToken);
        var design = await db.PublicationCoverDesigns
            .FirstOrDefaultAsync(candidate => candidate.EditionId == editionId, cancellationToken);
        if (design is null)
        {
            if (update.ExpectedRevision != 0)
                throw new DbUpdateConcurrencyException("The cover design changed.");
            design = Default(edition);
            db.PublicationCoverDesigns.Add(design);
        }
        else if (design.Revision != update.ExpectedRevision)
        {
            throw new DbUpdateConcurrencyException("The cover design changed.");
        }
        var template = await TemplateAsync(edition, design, cancellationToken);
        design.Title = update.Title.Trim();
        design.Subtitle = update.Subtitle.Trim();
        design.Author = update.Author.Trim();
        design.SpineText = update.SpineText.Trim();
        design.BackCopy = update.BackCopy.Trim();
        design.BackgroundColor = update.BackgroundColor.Trim().ToLowerInvariant();
        design.BarcodeMode = update.BarcodeMode;
        design.ImageFocalXPercent = update.ImageFocalXPercent;
        design.ImageFocalYPercent = update.ImageFocalYPercent;
        design.AcknowledgedTemplateFingerprint = update.AcknowledgeTemplate
            ? template.Fingerprint
            : design.AcknowledgedTemplateFingerprint;
        design.Revision++;
        design.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await ViewAsync(edition, design, cancellationToken);
    }

    private async Task<PublicationCoverDesignView> ViewAsync(
        PublicationEdition edition,
        PublicationCoverDesign design,
        CancellationToken cancellationToken)
    {
        var template = await TemplateAsync(edition, design, cancellationToken);
        var diagnostics = new List<string>();
        if (design.BarcodeMode == PublicationBarcodeMode.LorekeeperBarcode
            && !PublicationIsbn.IsValidIsbn13(edition.Isbn))
            diagnostics.Add("Lorekeeper barcode output requires a valid ISBN-13.");
        if (edition.Vendor == PublicationVendor.IngramSpark
            && !PublicationIsbn.IsValidIsbn13(edition.Isbn))
            diagnostics.Add("Ingram Preview cover output requires a valid ISBN-13 barcode.");
        if (edition.Vendor == PublicationVendor.IngramSpark
            && design.BarcodeMode == PublicationBarcodeMode.VendorOverlay)
            diagnostics.Add("Ingram covers must contain Lorekeeper's ISBN-13 barcode.");
        if (template.SpineWidthInches < 0.24 && !string.IsNullOrWhiteSpace(design.SpineText))
            diagnostics.Add("Spine text is disabled below the initial 0.24-inch safety threshold.");
        if (!template.IsAcknowledged)
            diagnostics.Add("Cover geometry changed; acknowledge the current template before proof approval.");
        return new(
            design.Id,
            edition.Id,
            design.Title,
            design.Subtitle,
            design.Author,
            design.SpineText,
            design.BackCopy,
            design.BackgroundColor,
            design.BarcodeMode,
            design.ImageFocalXPercent,
            design.ImageFocalYPercent,
            design.Revision,
            template,
            diagnostics);
    }

    private async Task<PublicationCoverTemplate> TemplateAsync(
        PublicationEdition edition,
        PublicationCoverDesign design,
        CancellationToken cancellationToken)
    {
        var pages = await db.PublicationArtifacts.AsNoTracking()
            .Where(artifact => artifact.EditionId == edition.Id
                && artifact.Kind == PublicationArtifactKind.InteriorPdf
                && artifact.SourceFingerprint != string.Empty)
            .OrderByDescending(artifact => artifact.CreatedAt)
            .Select(artifact => artifact.PageCount)
            .FirstOrDefaultAsync(cancellationToken) ?? 0;
        var bleed = edition.Bleed ? 0.125 : 0;
        var caliper = edition.Paper == PublicationPaper.Cream ? 0.0025 : 0.002252;
        var spine = pages * caliper;
        var width = edition.PageWidthInches * 2 + spine + bleed * 2;
        var height = edition.PageHeightInches + bleed * 2;
        var source = $"{edition.Vendor}|{edition.VendorProfileVersion}|{pages}|{edition.PageWidthInches:R}|{edition.PageHeightInches:R}|{bleed:R}|{caliper:R}";
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        return new(pages, edition.PageWidthInches, edition.PageHeightInches, bleed, spine, width, height, 0.25, 2, 1.2, fingerprint,
            string.Equals(fingerprint, design.AcknowledgedTemplateFingerprint, StringComparison.Ordinal));
    }

    private async Task<PublicationEdition> GetEditionAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken) =>
        await db.PublicationEditions.AsNoTracking().FirstOrDefaultAsync(
            edition => edition.Id == editionId && edition.ProjectId == projectId,
            cancellationToken) ?? throw new KeyNotFoundException("Publication edition not found.");

    private static PublicationCoverDesign Default(PublicationEdition edition) => new()
    {
        EditionId = edition.Id,
        Title = edition.TitleOverride,
        Subtitle = edition.Subtitle,
        Author = edition.Author,
        SpineText = edition.TitleOverride,
        BackCopy = edition.Description,
    };

    private static void Validate(PublicationCoverDesignUpdate update)
    {
        if (!Enum.IsDefined(update.BarcodeMode))
            throw new ArgumentException("Barcode mode is invalid.");
        if (update.Title.Trim().Length is < 1 or > 500
            || update.Subtitle.Trim().Length > 500
            || update.Author.Trim().Length > 500
            || update.SpineText.Trim().Length > 500
            || update.BackCopy.Trim().Length > 10_000)
            throw new ArgumentException("Cover copy exceeds its allowed length.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(update.BackgroundColor, "^#[0-9a-fA-F]{6}$"))
            throw new ArgumentException("Background color must be a six-digit hex color.");
        if (!double.IsFinite(update.ImageFocalXPercent)
            || !double.IsFinite(update.ImageFocalYPercent)
            || update.ImageFocalXPercent is < 0 or > 100
            || update.ImageFocalYPercent is < 0 or > 100)
            throw new ArgumentException("Image focal points must be between 0 and 100 percent.");
    }
}

public static class PublicationIsbn
{
    public static bool IsValidIsbn13(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length != 13)
            return false;
        var sum = digits.Take(12).Select((digit, index) => (digit - '0') * (index % 2 == 0 ? 1 : 3)).Sum();
        return (10 - sum % 10) % 10 == digits[12] - '0';
    }
}
