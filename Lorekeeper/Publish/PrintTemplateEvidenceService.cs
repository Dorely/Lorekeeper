using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UglyToad.PdfPig;

namespace Lorekeeper.Publish;

public sealed record PrintTemplateImportFile(string FileName, byte[] Data);

public interface IPrintTemplateEvidenceService
{
    string ImportBarnesAndNoble(
        PrintProductDefinition product,
        string trim,
        int pageCount,
        IReadOnlyList<PrintTemplateImportFile> files);
}

public sealed class PrintTemplateEvidenceService : IPrintTemplateEvidenceService
{
    private const int MaximumTemplateBytes = 64 * 1024 * 1024;

    public string ImportBarnesAndNoble(
        PrintProductDefinition product,
        string trim,
        int pageCount,
        IReadOnlyList<PrintTemplateImportFile> files)
    {
        if (product.Vendor != Models.PublicationVendor.BarnesAndNoblePress)
            throw new InvalidOperationException("B&N template import requires a B&N Press print product.");
        if (!product.TrimSizes.Contains(trim, StringComparer.Ordinal))
            throw new InvalidOperationException("The selected trim is not supported by this B&N Press product.");
        if (pageCount <= 0)
            throw new InvalidOperationException("Enter the page count used to generate the B&N Press template.");
        if (!pageCount.IsEven()) pageCount++;
        if (pageCount < product.MinimumPages || pageCount > product.MaximumPages)
            throw new InvalidOperationException($"This product supports {product.MinimumPages}-{product.MaximumPages} pages.");

        var pdfs = Expand(files);
        if (pdfs.Count == 0)
            throw new InvalidOperationException("Choose the B&N Press template ZIP or one or more template PDFs.");
        var measured = pdfs.Select(ReadPdf).ToList();
        var full = measured
            .Where(item => !IsPanelName(item.Name))
            .OrderByDescending(item => item.WidthInches)
            .FirstOrDefault()
            ?? measured.OrderByDescending(item => item.WidthInches).First();
        var front = measured.FirstOrDefault(item => ContainsRole(item.Name, "front"));
        var back = measured.FirstOrDefault(item => ContainsRole(item.Name, "back"));
        if (front is null || back is null)
            throw new InvalidOperationException("The B&N Press template package must include identifiable front and back PDFs as well as the measured full-cover PDF.");
        var trimParts = trim.Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (trimParts.Length != 2
            || !decimal.TryParse(trimParts[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var trimWidth)
            || !decimal.TryParse(trimParts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var trimHeight))
            throw new InvalidOperationException("The selected trim size is invalid.");
        const decimal bleed = 0.125m;
        var frontWidth = front.WidthInches;
        var frontHeight = front.HeightInches;
        var backWidth = back.WidthInches;
        var backHeight = back.HeightInches;
        var spineWidth = full.WidthInches - frontWidth - backWidth;
        if (spineWidth <= 0 || Math.Abs(full.HeightInches - frontHeight) > 0.02m || Math.Abs(full.HeightInches - backHeight) > 0.02m)
            throw new InvalidOperationException("The selected templates do not form a measurable [BACK][SPINE][FRONT] cover topology.");
        var safe = new[]
        {
            (frontWidth - trimWidth) / 2,
            (frontHeight - trimHeight) / 2,
            (backWidth - trimWidth) / 2,
            (backHeight - trimHeight) / 2,
        }.Max();
        if (safe < 0)
            throw new InvalidOperationException("The selected front or back template is smaller than the selected trim size.");

        var fingerprintSource = string.Join('|', new[]
        {
            product.Key,
            trim,
            pageCount.ToString(CultureInfo.InvariantCulture),
            DecimalText(full.WidthInches),
            DecimalText(full.HeightInches),
            DecimalText(frontWidth),
            DecimalText(frontHeight),
            DecimalText(backWidth),
            DecimalText(backHeight),
            DecimalText(spineWidth),
            full.Sha256,
            front.Sha256,
            back.Sha256,
        });
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource)));
        var evidence = new PrintTemplateEvidence(
            trimWidth,
            trimHeight,
            bleed,
            safe,
            0,
            0,
            0,
            0,
            2,
            1.2m,
            null,
            product.MinimumPages,
            product.MaximumPages,
            "PDF/A-1b")
        {
            Provider = "BarnesAndNoblePress",
            ProductKey = product.Key,
            PageCount = pageCount,
            GeometryFingerprint = fingerprint,
            SpineWidthInches = spineWidth,
            FullCoverWidthInches = full.WidthInches,
            FullCoverHeightInches = full.HeightInches,
            FrontCoverWidthInches = frontWidth,
            FrontCoverHeightInches = frontHeight,
            BackCoverWidthInches = backWidth,
            BackCoverHeightInches = backHeight,
            FullCoverTemplateSha256 = full.Sha256,
            FrontCoverTemplateSha256 = front.Sha256,
            BackCoverTemplateSha256 = back.Sha256,
        };
        return JsonSerializer.Serialize(evidence, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        });
    }

    private static List<PrintTemplateImportFile> Expand(IReadOnlyList<PrintTemplateImportFile> files)
    {
        var result = new List<PrintTemplateImportFile>();
        foreach (var file in files)
        {
            if (file.Data.Length is 0 or > MaximumTemplateBytes)
                throw new InvalidOperationException($"Template file '{file.FileName}' is empty or exceeds 64 MB.");
            if (Path.GetExtension(file.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(file);
                continue;
            }
            if (!Path.GetExtension(file.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                continue;
            using var stream = new MemoryStream(file.Data, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries.Where(entry => Path.GetExtension(entry.Name).Equals(".pdf", StringComparison.OrdinalIgnoreCase)))
            {
                if (entry.Length is <= 0 or > MaximumTemplateBytes)
                    throw new InvalidOperationException($"Template entry '{entry.FullName}' is empty or exceeds 64 MB.");
                using var source = entry.Open();
                using var target = new MemoryStream((int)entry.Length);
                source.CopyTo(target);
                result.Add(new PrintTemplateImportFile(entry.Name, target.ToArray()));
            }
        }
        return result;
    }

    private static MeasuredPdf ReadPdf(PrintTemplateImportFile file)
    {
        using var document = PdfDocument.Open(file.Data);
        if (document.NumberOfPages != 1)
            throw new InvalidOperationException($"Template PDF '{file.FileName}' must contain exactly one page.");
        var page = document.GetPage(1);
        return new MeasuredPdf(
            file.FileName,
            (decimal)page.Width / 72m,
            (decimal)page.Height / 72m,
            Convert.ToHexStringLower(SHA256.HashData(file.Data)));
    }

    private static bool IsPanelName(string value) => ContainsRole(value, "front") || ContainsRole(value, "back");
    private static bool ContainsRole(string value, string role) => value.Contains(role, StringComparison.OrdinalIgnoreCase);
    private static string DecimalText(decimal value) => value.ToString("0.#####", CultureInfo.InvariantCulture);
    private sealed record MeasuredPdf(string Name, decimal WidthInches, decimal HeightInches, string Sha256);
}

file static class IntegerExtensions
{
    public static bool IsEven(this int value) => value % 2 == 0;
}
