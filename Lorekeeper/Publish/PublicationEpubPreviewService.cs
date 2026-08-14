using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Lorekeeper.Models;

namespace Lorekeeper.Publish;

public sealed record PublicationEpubPreviewLocation(
    int Index,
    string Id,
    string Title,
    string ResourcePath,
    bool IsFixedLayout,
    int? ViewportWidth,
    int? ViewportHeight);

public sealed record PublicationEpubPreviewDocument(
    Guid ArtifactId,
    string ArtifactSha256,
    DateTime CreatedAt,
    string FileName,
    string Title,
    IReadOnlyList<PublicationEpubPreviewLocation> Locations);

public sealed record PublicationEpubPreviewText(
    PublicationEpubPreviewLocation Location,
    string Text,
    int Start,
    int Count,
    int TotalCharacters,
    bool HasMore);

public sealed record PublicationEpubPreviewResource(
    byte[] Data,
    string MediaType,
    string ArtifactSha256,
    string ResourceSha256,
    DateTime CreatedAt);

public interface IPublicationEpubPreviewService
{
    Task<PublicationEpubPreviewDocument?> ReadAsync(
        Guid projectId,
        Guid artifactId,
        CancellationToken cancellationToken = default);

    Task<PublicationEpubPreviewText?> ReadLocationTextAsync(
        Guid projectId,
        Guid artifactId,
        int locationIndex,
        int start,
        int count,
        CancellationToken cancellationToken = default);

    Task<PublicationEpubPreviewResource?> ReadResourceAsync(
        Guid projectId,
        Guid artifactId,
        string resourcePath,
        CancellationToken cancellationToken = default);
}

public sealed class PublicationEpubPreviewCache
{
    private const int MaximumEntries = 32;
    private readonly ConcurrentDictionary<string, ParsedEpub> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _insertionOrder = new();

    internal bool TryGet(string hash, out ParsedEpub? parsed) => _entries.TryGetValue(hash, out parsed);

    internal void Set(string hash, ParsedEpub parsed)
    {
        if (_entries.TryAdd(hash, parsed))
            _insertionOrder.Enqueue(hash);

        while (_entries.Count > MaximumEntries && _insertionOrder.TryDequeue(out var oldest))
            _entries.TryRemove(oldest, out _);
    }
}

internal sealed record ParsedEpub(
    string Title,
    IReadOnlyList<PublicationEpubPreviewLocation> Locations,
    IReadOnlyDictionary<string, string> ResourceMediaTypes);

public sealed partial class PublicationEpubPreviewService(
    IPublicationRenderService renders,
    PublicationEpubPreviewCache cache) : IPublicationEpubPreviewService
{
    private const long MaximumArtifactBytes = 128L * 1024 * 1024;
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private const long MaximumEntryBytes = 64L * 1024 * 1024;
    private const int MaximumEntries = 5000;
    private const int MaximumTextRead = 12000;
    private static readonly XNamespace ContainerNamespace = "urn:oasis:names:tc:opendocument:xmlns:container";
    private static readonly XNamespace OpfNamespace = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace DcNamespace = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace XhtmlNamespace = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace EpubNamespace = "http://www.idpf.org/2007/ops";
    private static readonly HashSet<string> AllowedMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/xhtml+xml",
        "text/css",
        "image/jpeg",
        "image/png",
        "image/gif",
        "image/svg+xml",
        "font/ttf",
        "font/otf",
        "application/vnd.ms-opentype",
    };
    private static readonly HashSet<string> RemovedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "iframe", "frame", "frameset", "object", "embed", "applet", "base",
        "form", "input", "button", "textarea", "select", "option", "video", "audio",
    };

    public async Task<PublicationEpubPreviewDocument?> ReadAsync(
        Guid projectId,
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {
        var artifact = await ReadArtifactAsync(projectId, artifactId, cancellationToken);
        if (artifact is null)
            return null;

        var parsed = Parse(artifact);
        return new(
            artifact.Id,
            artifact.Sha256,
            artifact.CreatedAt,
            artifact.FileName,
            parsed.Title,
            parsed.Locations);
    }

    public async Task<PublicationEpubPreviewText?> ReadLocationTextAsync(
        Guid projectId,
        Guid artifactId,
        int locationIndex,
        int start,
        int count,
        CancellationToken cancellationToken = default)
    {
        var artifact = await ReadArtifactAsync(projectId, artifactId, cancellationToken);
        if (artifact is null)
            return null;

        var parsed = Parse(artifact);
        if (locationIndex < 0 || locationIndex >= parsed.Locations.Count)
            return null;

        var location = parsed.Locations[locationIndex];
        var data = SanitizeXmlResource(
            ReadEntry(artifact.Data, location.ResourcePath),
            location.ResourcePath,
            parsed.ResourceMediaTypes);
        var document = LoadXml(data, "EPUB spine document");
        var body = document.Root?.Elements().FirstOrDefault(element => element.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase));
        var text = NormalizeText(string.Join(
            ' ',
            (body?.DescendantNodes().OfType<XText>() ?? [])
                .Where(item => !item.Ancestors().Any(element => element.Name.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase)))
                .Select(item => item.Value)));
        var boundedStart = Math.Clamp(start, 0, text.Length);
        var boundedCount = Math.Clamp(count, 1, MaximumTextRead);
        var actualCount = Math.Min(boundedCount, text.Length - boundedStart);
        return new(
            location,
            text.Substring(boundedStart, actualCount),
            boundedStart,
            actualCount,
            text.Length,
            boundedStart + actualCount < text.Length);
    }

    public async Task<PublicationEpubPreviewResource?> ReadResourceAsync(
        Guid projectId,
        Guid artifactId,
        string resourcePath,
        CancellationToken cancellationToken = default)
    {
        var artifact = await ReadArtifactAsync(projectId, artifactId, cancellationToken);
        if (artifact is null)
            return null;

        var parsed = Parse(artifact);
        var path = NormalizeArchivePath(Uri.UnescapeDataString(resourcePath));
        if (!parsed.ResourceMediaTypes.TryGetValue(path, out var mediaType))
            return null;

        var data = ReadEntry(artifact.Data, path);
        data = mediaType.ToLowerInvariant() switch
        {
            "application/xhtml+xml" or "image/svg+xml" => SanitizeXmlResource(data, path, parsed.ResourceMediaTypes),
            "text/css" => SanitizeCss(data, path, parsed.ResourceMediaTypes),
            _ => data,
        };
        return new(
            data,
            mediaType,
            artifact.Sha256,
            Convert.ToHexStringLower(SHA256.HashData(data)),
            artifact.CreatedAt);
    }

    private async Task<PublicationArtifact?> ReadArtifactAsync(
        Guid projectId,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        var artifact = await renders.GetArtifactAsync(projectId, artifactId, cancellationToken);
        if (artifact is null
            || artifact.Kind != PublicationArtifactKind.Epub
            || !artifact.MediaType.Equals("application/epub+zip", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (artifact.ByteLength <= 0 || artifact.ByteLength > MaximumArtifactBytes)
            throw new InvalidDataException("The EPUB artifact is outside the supported preview size.");
        if (artifact.ByteLength != artifact.Data.LongLength)
            throw new InvalidDataException("The EPUB artifact length does not match its immutable record.");
        if (!string.Equals(
                artifact.Sha256,
                Convert.ToHexStringLower(SHA256.HashData(artifact.Data)),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The EPUB artifact hash does not match its immutable record.");
        }

        return artifact;
    }

    private ParsedEpub Parse(PublicationArtifact artifact)
    {
        if (cache.TryGet(artifact.Sha256, out var cached) && cached is not null)
            return cached;

        using var stream = new MemoryStream(artifact.Data, writable: false);
        using var archive = OpenArchive(stream);
        var entries = ValidateEntries(archive);
        ValidateMimetype(archive, entries);

        var container = LoadXml(ReadEntry(entries, "META-INF/container.xml"), "EPUB container");
        var rootfiles = container.Root?
            .Element(ContainerNamespace + "rootfiles")?
            .Elements(ContainerNamespace + "rootfile")
            .ToList() ?? [];
        if (rootfiles.Count != 1)
            throw new InvalidDataException("The EPUB container must declare exactly one package document.");

        var packagePath = NormalizeArchivePath((string?)rootfiles[0].Attribute("full-path") ?? string.Empty);
        var package = LoadXml(ReadEntry(entries, packagePath), "EPUB package");
        if (package.Root?.Name != OpfNamespace + "package"
            || !string.Equals((string?)package.Root.Attribute("version"), "3.0", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The EPUB preview requires an EPUB 3 package document.");
        }

        var packageDirectory = ArchiveDirectory(packagePath);
        var manifestElement = package.Root.Element(OpfNamespace + "manifest")
            ?? throw new InvalidDataException("The EPUB package has no manifest.");
        var manifestById = new Dictionary<string, ManifestItem>(StringComparer.Ordinal);
        var resources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifestElement.Elements(OpfNamespace + "item"))
        {
            var id = ((string?)item.Attribute("id"))?.Trim();
            var href = ((string?)item.Attribute("href"))?.Trim();
            var mediaType = ((string?)item.Attribute("media-type"))?.Trim();
            var properties = ((string?)item.Attribute("properties"))?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(mediaType))
                throw new InvalidDataException("The EPUB manifest contains an incomplete item.");
            if (!AllowedMediaTypes.Contains(mediaType))
                throw new InvalidDataException($"The EPUB manifest media type '{mediaType}' is not safe for in-app preview.");
            if (properties.Contains("scripted", StringComparer.OrdinalIgnoreCase) || properties.Contains("remote-resources", StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("The EPUB contains scripted or remote resources that cannot be previewed safely.");

            var path = ResolveArchivePath(packageDirectory, href);
            if (!entries.ContainsKey(path))
                throw new InvalidDataException($"The EPUB manifest resource '{path}' is missing.");
            if (!manifestById.TryAdd(id, new(id, path, mediaType, properties)))
                throw new InvalidDataException($"The EPUB manifest ID '{id}' is duplicated.");
            if (!resources.TryAdd(path, mediaType))
                throw new InvalidDataException($"The EPUB resource '{path}' is declared more than once.");
        }

        var declared = new HashSet<string>(resources.Keys, StringComparer.OrdinalIgnoreCase)
        {
            "mimetype",
            "META-INF/container.xml",
            packagePath,
        };
        var unexpected = entries.Keys.FirstOrDefault(path => !declared.Contains(path));
        if (unexpected is not null)
            throw new InvalidDataException($"The EPUB contains undeclared resource '{unexpected}'.");
        foreach (var (path, mediaType) in resources)
        {
            if (!mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)
                && !mediaType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase)
                && !mediaType.Equals("text/css", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var data = ReadEntry(entries, path);
            _ = mediaType.ToLowerInvariant() switch
            {
                "application/xhtml+xml" or "image/svg+xml" => SanitizeXmlResource(data, path, resources),
                "text/css" => SanitizeCss(data, path, resources),
                _ => data,
            };
        }

        var packageFixed = package.Root
            .Element(OpfNamespace + "metadata")?
            .Elements(OpfNamespace + "meta")
            .Any(item => string.Equals((string?)item.Attribute("property"), "rendition:layout", StringComparison.Ordinal)
                && string.Equals(item.Value.Trim(), "pre-paginated", StringComparison.OrdinalIgnoreCase)) == true;
        var spine = package.Root.Element(OpfNamespace + "spine")
            ?? throw new InvalidDataException("The EPUB package has no spine.");
        var spineItems = new List<(ManifestItem Item, bool Fixed)>();
        foreach (var itemref in spine.Elements(OpfNamespace + "itemref"))
        {
            var idref = ((string?)itemref.Attribute("idref"))?.Trim();
            if (string.IsNullOrWhiteSpace(idref) || !manifestById.TryGetValue(idref, out var item))
                throw new InvalidDataException("The EPUB spine references an unknown manifest item.");
            if (!item.MediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Every EPUB spine item must be an XHTML document.");
            var properties = ((string?)itemref.Attribute("properties"))?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
            var fixedLayout = packageFixed || properties.Contains("rendition:layout-pre-paginated", StringComparer.OrdinalIgnoreCase);
            spineItems.Add((item, fixedLayout));
        }
        if (spineItems.Count == 0)
            throw new InvalidDataException("The EPUB spine is empty.");

        var navigationTitles = ReadNavigationTitles(entries, manifestById.Values);
        var locations = new List<PublicationEpubPreviewLocation>(spineItems.Count);
        for (var index = 0; index < spineItems.Count; index++)
        {
            var (item, fixedLayout) = spineItems[index];
            var xhtml = LoadXml(ReadEntry(entries, item.Path), "EPUB spine document");
            var documentTitle = xhtml.Root?
                .Element(XhtmlNamespace + "head")?
                .Element(XhtmlNamespace + "title")?
                .Value.Trim();
            var title = navigationTitles.GetValueOrDefault(item.Path)
                ?? documentTitle
                ?? $"Location {index + 1}";
            var (viewportWidth, viewportHeight) = ReadViewport(xhtml);
            fixedLayout |= viewportWidth.HasValue && viewportHeight.HasValue;
            locations.Add(new(index, item.Id, title, item.Path, fixedLayout, viewportWidth, viewportHeight));
        }
        DisambiguateLocationTitles(locations);

        var titleMetadata = package.Root
            .Element(OpfNamespace + "metadata")?
            .Elements(DcNamespace + "title")
            .Select(item => item.Value.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var parsed = new ParsedEpub(titleMetadata ?? Path.GetFileNameWithoutExtension(artifact.FileName), locations, resources);
        cache.Set(artifact.Sha256, parsed);
        return parsed;
    }

    private static void DisambiguateLocationTitles(List<PublicationEpubPreviewLocation> locations)
    {
        var duplicateTitles = locations
            .GroupBy(location => location.Title, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < locations.Count; index++)
        {
            var location = locations[index];
            if (!duplicateTitles.Contains(location.Title))
                continue;
            var layout = location.IsFixedLayout ? "fixed layout" : "reflowable";
            locations[index] = location with { Title = $"{location.Title} — {layout}" };
        }
    }

    private static Dictionary<string, ZipArchiveEntry> ValidateEntries(ZipArchive archive)
    {
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaximumEntries)
            throw new InvalidDataException("The EPUB archive contains an invalid number of entries.");

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;
            var path = NormalizeArchivePath(entry.FullName);
            if (entry.Length < 0 || entry.Length > MaximumEntryBytes)
                throw new InvalidDataException($"The EPUB resource '{path}' is outside the supported preview size.");
            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > MaximumExpandedBytes)
                throw new InvalidDataException("The expanded EPUB is outside the supported preview size.");
            if (!entries.TryAdd(path, entry))
                throw new InvalidDataException($"The EPUB contains a duplicate resource path '{path}'.");
        }
        return entries;
    }

    private static void ValidateMimetype(ZipArchive archive, IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        if (!entries.TryGetValue("mimetype", out var mimetype)
            || !ReferenceEquals(archive.Entries.FirstOrDefault(entry => !string.IsNullOrEmpty(entry.Name)), mimetype)
            || mimetype.CompressedLength != mimetype.Length
            || !Encoding.ASCII.GetString(ReadEntry(mimetype)).Equals("application/epub+zip", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The EPUB mimetype entry is invalid.");
        }
    }

    private static Dictionary<string, string> ReadNavigationTitles(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IEnumerable<ManifestItem> manifest)
    {
        var nav = manifest.FirstOrDefault(item => item.Properties.Contains("nav", StringComparer.OrdinalIgnoreCase));
        if (nav is null)
            return new(StringComparer.OrdinalIgnoreCase);

        var document = LoadXml(ReadEntry(entries, nav.Path), "EPUB navigation document");
        var toc = document.Descendants(XhtmlNamespace + "nav")
            .FirstOrDefault(item => ((string?)item.Attribute(EpubNamespace + "type"))?
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains("toc", StringComparer.OrdinalIgnoreCase) == true);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (toc is null)
            return result;
        foreach (var link in toc.Descendants(XhtmlNamespace + "a"))
        {
            var href = ((string?)link.Attribute("href"))?.Trim();
            var title = NormalizeText(link.Value);
            if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(title))
                continue;
            var path = ResolveArchivePath(ArchiveDirectory(nav.Path), href);
            result.TryAdd(path, title);
        }
        return result;
    }

    private static (int? Width, int? Height) ReadViewport(XDocument document)
    {
        var content = document.Root?
            .Element(XhtmlNamespace + "head")?
            .Elements(XhtmlNamespace + "meta")
            .FirstOrDefault(item => string.Equals((string?)item.Attribute("name"), "viewport", StringComparison.OrdinalIgnoreCase))?
            .Attribute("content")?
            .Value;
        if (string.IsNullOrWhiteSpace(content))
            return (null, null);
        var match = ViewportRegex().Match(content);
        return match.Success
            && int.TryParse(match.Groups["width"].Value, out var width)
            && int.TryParse(match.Groups["height"].Value, out var height)
            && width > 0
            && height > 0
                ? (width, height)
                : (null, null);
    }

    private static byte[] SanitizeXmlResource(
        byte[] data,
        string path,
        IReadOnlyDictionary<string, string> declaredResources)
    {
        var document = LoadXml(data, path);
        foreach (var element in document.Descendants().Where(item => RemovedElements.Contains(item.Name.LocalName)).ToList())
            element.Remove();
        foreach (var element in document.Descendants()
            .Where(item => item.Name.LocalName.Equals("meta", StringComparison.OrdinalIgnoreCase)
                && item.Attributes().Any(attribute => attribute.Name.LocalName.Equals("http-equiv", StringComparison.OrdinalIgnoreCase)))
            .ToList())
        {
            element.Remove();
        }

        foreach (var element in document.DescendantsAndSelf())
        {
            if (element.Name.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase))
                ValidateCssText(element.Value, path, declaredResources);

            foreach (var attribute in element.Attributes().ToList())
            {
                if (attribute.IsNamespaceDeclaration)
                    continue;
                if (attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                    || attribute.Name.LocalName.Equals("srcdoc", StringComparison.OrdinalIgnoreCase))
                {
                    attribute.Remove();
                    continue;
                }
                if (attribute.Name.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase))
                {
                    ValidateCssText(attribute.Value, path, declaredResources);
                    continue;
                }
                if (attribute.Name.LocalName is not ("href" or "src" or "poster" or "data"))
                    continue;

                if (IsUnsafeReference(attribute.Value))
                {
                    attribute.Remove();
                    continue;
                }

                ValidateDeclaredReference(path, attribute.Value, declaredResources);
            }
        }

        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            OmitXmlDeclaration = false,
        }))
        {
            document.Save(writer);
        }
        return output.ToArray();
    }

    private static byte[] SanitizeCss(
        byte[] data,
        string path,
        IReadOnlyDictionary<string, string> declaredResources)
    {
        var css = Encoding.UTF8.GetString(data);
        ValidateCssText(css, path, declaredResources);
        return new UTF8Encoding(false).GetBytes(css);
    }

    private static void ValidateCssText(
        string css,
        string path,
        IReadOnlyDictionary<string, string> declaredResources)
    {
        if (UnsafeCssRegex().IsMatch(css))
            throw new InvalidDataException($"The EPUB stylesheet '{path}' contains unsafe external or executable content.");
        foreach (Match match in CssUrlRegex().Matches(css))
            ValidateDeclaredReference(path, match.Groups["value"].Value, declaredResources);
    }

    private static void ValidateDeclaredReference(
        string sourcePath,
        string reference,
        IReadOnlyDictionary<string, string> declaredResources)
    {
        var trimmed = reference.Trim().Trim('\'', '"');
        if (string.IsNullOrWhiteSpace(trimmed)
            || trimmed.StartsWith('#')
            || trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var resolved = ResolveArchivePath(ArchiveDirectory(sourcePath), trimmed);
        if (!declaredResources.ContainsKey(resolved))
            throw new InvalidDataException($"The EPUB resource '{sourcePath}' references undeclared content '{resolved}'.");
    }

    private static bool IsUnsafeReference(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
            return false;
        if (trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            return false;
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
            return true;
        return Uri.TryCreate(trimmed, UriKind.Absolute, out _);
    }

    private static XDocument LoadXml(byte[] data, string description)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumEntryBytes,
                MaxCharactersFromEntities = 0,
                IgnoreComments = true,
            });
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            throw new InvalidDataException($"The {description} is malformed.", exception);
        }
    }

    private static byte[] ReadEntry(byte[] archiveBytes, string path)
    {
        using var stream = new MemoryStream(archiveBytes, writable: false);
        using var archive = OpenArchive(stream);
        var entries = ValidateEntries(archive);
        return ReadEntry(entries, path);
    }

    private static ZipArchive OpenArchive(Stream stream)
    {
        try
        {
            return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException("The stored EPUB archive is malformed.", exception);
        }
    }

    private static byte[] ReadEntry(IReadOnlyDictionary<string, ZipArchiveEntry> entries, string path) =>
        entries.TryGetValue(path, out var entry)
            ? ReadEntry(entry)
            : throw new InvalidDataException($"The EPUB resource '{path}' is missing.");

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var output = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;
                if (output.Length + read > entry.Length || output.Length + read > MaximumEntryBytes)
                    throw new InvalidDataException($"The EPUB resource '{entry.FullName}' exceeds its declared length.");
                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        if (output.Length != entry.Length)
            throw new InvalidDataException($"The EPUB resource '{entry.FullName}' did not expand to its declared length.");
        return output.ToArray();
    }

    private static string ResolveArchivePath(string directory, string href)
    {
        var withoutFragment = href.Split('#', 2)[0].Split('?', 2)[0];
        if (string.IsNullOrWhiteSpace(withoutFragment))
            throw new InvalidDataException("The EPUB contains an empty resource reference.");
        return NormalizeArchivePath(
            string.IsNullOrEmpty(directory) ? withoutFragment : $"{directory}/{withoutFragment}",
            allowBoundedParentSegments: true);
    }

    private static string NormalizeArchivePath(string path, bool allowBoundedParentSegments = false)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Contains('\0', StringComparison.Ordinal)
            || path.StartsWith("/", StringComparison.Ordinal)
            || path.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidDataException("The EPUB contains an unsafe resource path.");
        }

        var parts = new List<string>();
        foreach (var rawPart in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = Uri.UnescapeDataString(rawPart);
            if (part.Contains('/') || part.Contains('\\'))
                throw new InvalidDataException("The EPUB contains a resource path that escapes the archive.");
            if (part == ".")
            {
                if (!allowBoundedParentSegments)
                    throw new InvalidDataException("The EPUB contains a resource path that escapes the archive.");
                continue;
            }
            if (part == "..")
            {
                if (!allowBoundedParentSegments || parts.Count == 0)
                    throw new InvalidDataException("The EPUB contains a resource path that escapes the archive.");
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        if (parts.Count == 0)
            throw new InvalidDataException("The EPUB contains an empty resource path.");
        return string.Join('/', parts);
    }

    private static string ArchiveDirectory(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? string.Empty : path[..index];
    }

    private static string NormalizeText(string value) => WhitespaceRegex().Replace(value, " ").Trim();

    private sealed record ManifestItem(string Id, string Path, string MediaType, IReadOnlyList<string> Properties);

    [GeneratedRegex(@"width\s*=\s*(?<width>\d+)\s*,\s*height\s*=\s*(?<height>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ViewportRegex();

    [GeneratedRegex("""@import|expression\s*\(|javascript\s*:|behavior\s*:|-moz-binding\s*:|url\s*\(\s*['"]?\s*(?:https?:|//|data:)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeCssRegex();

    [GeneratedRegex("""url\s*\(\s*['"]?(?<value>[^)'"]+)['"]?\s*\)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CssUrlRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}

internal static class XDocumentExtensions
{
    public static IEnumerable<XElement> DescendantsAndSelf(this XDocument document) =>
        document.Root is null ? [] : document.Root.DescendantsAndSelf();
}
