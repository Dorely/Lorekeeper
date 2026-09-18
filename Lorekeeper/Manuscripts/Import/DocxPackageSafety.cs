using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Lorekeeper.Manuscripts.Import;

internal static class DocxPackageSafety
{
    public static void Validate(byte[] bytes, CancellationToken cancellationToken,
        int maximumInputBytes = SemanticImportLimits.MaximumInputBytes,
        long maximumExpandedBytes = 128 * 1024 * 1024, long maximumPartBytes = 32 * 1024 * 1024)
    {
        if (bytes.Length == 0 || bytes.Length > maximumInputBytes)
            throw new InvalidDataException($"Choose a DOCX file no larger than {maximumInputBytes / (1024 * 1024)} MiB.");
        using var input = new MemoryStream(bytes, writable: false);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        if (zip.Entries.Count is 0 or > 10_000)
            throw new InvalidDataException("The DOCX package has an invalid entry count.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        var buffer = new byte[81920];
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Uri.UnescapeDataString(entry.FullName);
            if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':')
                || path.StartsWith('/') || path.Split('/').Any(segment => segment is "." or "..")
                || !paths.Add(path.Normalize(NormalizationForm.FormC))
                || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                throw new InvalidDataException("The DOCX package contains an unsafe or duplicate path.");
            if (entry.Length > maximumPartBytes || (total = checked(total + entry.Length)) > maximumExpandedBytes)
                throw new InvalidDataException("The DOCX package exceeds the bounded expansion limit.");
            if (path.Contains("/embeddings/", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/activeX/", StringComparison.OrdinalIgnoreCase)
                || path.Contains("vbaProject", StringComparison.OrdinalIgnoreCase)
                || new[] { ".exe", ".dll", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".js" }
                    .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("The DOCX package contains executable or embedded object content.");
            // Drain every entry, including unused parts, rather than trusting ZIP lengths alone.
            using (var source = entry.Open())
            {
                long read = 0;
                int count;
                while ((count = source.Read(buffer)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((read += count) > entry.Length)
                        throw new InvalidDataException("A DOCX entry expands beyond its declared length.");
                }
                if (read != entry.Length) throw new InvalidDataException("A DOCX entry is truncated.");
            }
            if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) continue;
            using var xml = entry.Open();
            using var reader = XmlReader.Create(xml, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = maximumPartBytes,
            });
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.Depth > SemanticImportLimits.MaximumDepth)
                    throw new InvalidDataException("The DOCX XML nesting limit was exceeded.");
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.LocalName == "Relationship")
                {
                    var type = reader.GetAttribute("Type") ?? "";
                    if (type.Contains("vba", StringComparison.OrdinalIgnoreCase)
                        || type.Contains("oleObject", StringComparison.OrdinalIgnoreCase)
                        || type.Contains("aFChunk", StringComparison.OrdinalIgnoreCase)
                        || (reader.GetAttribute("TargetMode")?.Equals("External", StringComparison.OrdinalIgnoreCase) == true
                            && !type.EndsWith("/hyperlink", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("The DOCX package contains an unsafe relationship.");
                }
                if (reader.LocalName is "Override" or "Default"
                    && (reader.GetAttribute("ContentType") ?? "").Contains("macroEnabled", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Macro-enabled documents are not supported.");
            }
        }
    }
}
