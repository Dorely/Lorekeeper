using System.Text.Json;
using Lorekeeper.Citations;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Sources;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Lorekeeper.Manuscripts.Import;

public sealed partial class SemanticImportService : ISemanticImportService
{
    public Task<SemanticImportFragment> ReadDocxAsync(byte[] bytes, CancellationToken cancellationToken = default) =>
        Task.Run(() => new DocxReader(bytes, cancellationToken).Read(), cancellationToken);

    public Task<SemanticImportFragment> ReadWordHtmlAsync(string html,
        IReadOnlyList<SemanticImportImage>? clipboardImages = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => new WordHtmlReader(html, clipboardImages ?? [], cancellationToken).ReadAsync(), cancellationToken);

    public async Task AdmitAsync(AppDbContext db, Guid projectId, SemanticImportResources resources,
        IReadOnlyList<ManuscriptDocument> documents, CancellationToken cancellationToken)
    {
        var imageIds = resources.Images.Select(item => item.Id).ToList();
        var styleIds = resources.Styles.Select(item => item.Id).ToList();
        var recordIds = resources.Bibliography.Select(item => item.Id).ToList();
        if (await db.PublishAssets.AnyAsync(item => imageIds.Contains(item.Id), cancellationToken)
            || await db.ManuscriptStyleDefinitions.AnyAsync(item => styleIds.Contains(item.Id), cancellationToken)
            || await db.BibliographicRecords.AnyAsync(item => recordIds.Contains(item.Id), cancellationToken))
            throw new InvalidDataException("An imported resource identity already exists. Retry the original authoring receipt.");
        var blocks = documents.SelectMany(ManuscriptTraversal.EnumerateBlocks).ToList();
        if (imageIds.Any(id => !blocks.Any(block => block.ImageId == id))
            || resources.Styles.Any(style => !blocks.Any(block => block.StyleRole == style.Role
                || block.Content.Any(inline => inline.Marks.Any(mark => mark.Type == ManuscriptMarkType.CharacterStyle && mark.Value == style.Role))))
            || recordIds.Any(id => !documents.SelectMany(ManuscriptTraversal.EnumerateCitations)
                .Any(citation => citation.Cluster.Items.Any(item => item.BibliographicRecordId == id))))
            throw new InvalidDataException("Imported resources must belong to the inserted manuscript content.");
        foreach (var image in resources.Images)
            db.PublishAssets.Add(new PublishAsset
            {
                Id = image.Id, ProjectId = projectId, Source = PublishAssetSource.Imported,
                FileName = image.FileName, ContentType = image.ContentType, Data = image.Data,
            });
        foreach (var style in resources.Styles)
            db.ManuscriptStyleDefinitions.Add(new ManuscriptStyleDefinition
            {
                Id = style.Id, ProjectId = projectId, Name = style.Name, NameKey = style.Name.ToLowerInvariant(),
                Kind = style.Kind, SemanticRole = style.Role, SemanticRoleKey = style.Role.ToLowerInvariant(),
                DefinitionJson = JsonSerializer.Serialize(style.Definition, ManuscriptCodec.JsonOptions),
            });
        foreach (var item in resources.Bibliography)
            db.BibliographicRecords.Add(new BibliographicRecord
            {
                Id = item.Id, ProjectId = projectId, Kind = item.Kind, Title = item.Title,
                ContainerTitle = item.ContainerTitle, AuthorsJson = JsonSerializer.Serialize(item.Authors, ManuscriptCodec.JsonOptions),
                EditorsJson = JsonSerializer.Serialize(item.Editors, ManuscriptCodec.JsonOptions),
                TranslatorsJson = JsonSerializer.Serialize(item.Translators, ManuscriptCodec.JsonOptions),
                IssuedYear = item.Issued.Year, IssuedMonth = item.Issued.Month, IssuedDay = item.Issued.Day,
                AccessedYear = item.Accessed.Year, AccessedMonth = item.Accessed.Month, AccessedDay = item.Accessed.Day,
                Edition = item.Edition, Publisher = item.Publisher, PublisherPlace = item.PublisherPlace,
                Institution = item.Institution, ThesisType = item.ThesisType, Volume = item.Volume, Issue = item.Issue,
                Pages = item.Pages, Doi = item.Doi, Url = item.Url, Isbn = item.Isbn,
            });
        // The authoring owner has already opened the transaction. Make resources visible to
        // its normal style/image/citation validators; any later failure rolls this back too.
        await db.SaveChangesAsync(cancellationToken);
    }

    internal static void ValidateResources(SemanticImportResources resources)
    {
        if (resources.Images.Count > SemanticImportLimits.MaximumImages
            || resources.Styles.Count > SemanticImportLimits.MaximumStyles
            || resources.Bibliography.Count > SemanticImportLimits.MaximumBibliography
            || resources.Images.Sum(item => (long)item.Data.Length) > SemanticImportLimits.MaximumInputBytes)
            throw new InvalidDataException("The imported resource budget was exceeded.");
        var ids = resources.Images.Select(item => item.Id).Concat(resources.Styles.Select(item => item.Id))
            .Concat(resources.Bibliography.Select(item => item.Id)).ToList();
        if (ids.Contains(Guid.Empty) || ids.Distinct().Count() != ids.Count)
            throw new InvalidDataException("Imported resource identities must be non-empty and unique.");
        foreach (var image in resources.Images)
        {
            if (image.ContentType is not ("image/png" or "image/jpeg") || image.FileName.Length is 0 or > 200)
                throw new InvalidDataException("An imported image has unsupported metadata.");
            using var codec = SKCodec.Create(new SKMemoryStream(image.Data));
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0
                || (long)codec.Info.Width * codec.Info.Height > SemanticImportLimits.MaximumImagePixels
                || (codec.EncodedFormat == SKEncodedImageFormat.Png ? "image/png"
                    : codec.EncodedFormat == SKEncodedImageFormat.Jpeg ? "image/jpeg" : "") != image.ContentType)
                throw new InvalidDataException("An imported image is invalid or exceeds the pixel limit.");
            using var decoded = SKBitmap.Decode(image.Data) ?? throw new InvalidDataException("An imported image is incomplete.");
        }
        foreach (var style in resources.Styles)
        {
            ManuscriptStyleService.ValidateInput(new(style.Id, style.Name, style.Kind, style.Role, style.Definition));
            if (style.Definition.FontFamilyKey?.StartsWith("project:", StringComparison.OrdinalIgnoreCase) == true)
                throw new InvalidDataException("Word imports cannot introduce project-font references.");
        }
        foreach (var item in resources.Bibliography)
            ProjectSourcesService.ValidateBibliographicRecord(new(item.Id, null, item.Kind, item.Title, item.ContainerTitle,
                item.Authors, item.Editors, item.Translators, item.Issued, item.Accessed, item.Edition, item.Publisher,
                item.PublisherPlace, item.Institution, item.ThesisType, item.Volume, item.Issue, item.Pages,
                item.Doi, item.Url, item.Isbn, ""));
    }

    private sealed class ImportBuilder(CancellationToken cancellationToken)
    {
        public List<SemanticImportImage> Images { get; } = [];
        public List<SemanticImportStyle> Styles { get; } = [];
        public List<CitationRecord> Bibliography { get; } = [];
        public List<ManuscriptNote> Notes { get; } = [];
        public Dictionary<string, Guid> SourceMappings { get; } = new(StringComparer.Ordinal);
        private readonly HashSet<string> _report = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _styleRoles = new(StringComparer.Ordinal);
        private int _nodes;

        public void Check()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++_nodes > SemanticImportLimits.MaximumNodes)
                throw new InvalidDataException("The Word document exceeds the semantic node limit.");
        }

        public void Warn(string message) => _report.Add(message);
        public static string Id() => Guid.NewGuid().ToString("N");

        public string Style(string name, ManuscriptStyleKind kind, ManuscriptStyleProperties properties)
        {
            properties = ManuscriptStyleService.NormalizeDefinition(properties);
            var key = kind + "|" + name + "|" + JsonSerializer.Serialize(properties, ManuscriptCodec.JsonOptions);
            if (_styleRoles.TryGetValue(key, out var existing)) return existing;
            if (Styles.Count >= SemanticImportLimits.MaximumStyles) throw new InvalidDataException("Too many imported styles.");
            var id = Guid.NewGuid();
            var role = "imported-" + id.ToString("N");
            var label = string.IsNullOrWhiteSpace(name) ? "Word formatting" : name.Trim();
            label = label[..Math.Min(label.Length, 50)] + $" (Word {id.ToString("N")[..8]})";
            Styles.Add(new(id, label, kind, role, properties));
            _styleRoles[key] = role;
            return role;
        }

        public SemanticImportImage Image(byte[] data, string name)
        {
            Check();
            if (Images.Count >= SemanticImportLimits.MaximumImages
                || Images.Sum(image => (long)image.Data.Length) + data.Length > SemanticImportLimits.MaximumInputBytes)
                throw new InvalidDataException("Imported images exceed the bounded resource budget.");
            using var codec = SKCodec.Create(new SKMemoryStream(data));
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0
                || (long)codec.Info.Width * codec.Info.Height > SemanticImportLimits.MaximumImagePixels)
                throw new InvalidDataException("An imported image is invalid or exceeds the pixel limit.");
            using var bitmap = SKBitmap.Decode(data) ?? throw new InvalidDataException("An imported image could not be decoded.");
            var jpeg = codec.EncodedFormat == SKEncodedImageFormat.Jpeg;
            using var encoded = bitmap.Encode(jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, 100);
            var image = new SemanticImportImage(Guid.NewGuid(),
                Path.GetFileNameWithoutExtension(name)[..Math.Min(Path.GetFileNameWithoutExtension(name).Length, 150)] + (jpeg ? ".jpg" : ".png"),
                jpeg ? "image/jpeg" : "image/png", codec.EncodedFormat is SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png ? data : encoded.ToArray());
            Images.Add(image);
            return image;
        }

        public SemanticImportFragment Finish(List<ManuscriptBlock> blocks)
        {
            if (blocks.Count == 0) throw new InvalidDataException("The Word content contains no supported manuscript blocks.");
            var document = new ManuscriptDocument { ManuscriptId = Guid.NewGuid(), Content = blocks, Notes = Notes };
            ManuscriptCodec.Validate(document, document.ManuscriptId, document.Revision);
            var allBlocks = ManuscriptTraversal.EnumerateBlocks(document);
            if (allBlocks.Sum(block => block.Content.Sum(inline => (long)inline.Text.Length)) > 8 * 1024 * 1024)
                throw new InvalidDataException("The imported manuscript exceeds the 8 MiB text limit.");
            var resources = new SemanticImportResources(Images,
                Styles.Where(style => allBlocks.Any(block => block.StyleRole == style.Role
                    || block.Content.Any(inline => inline.Marks.Any(mark => mark.Type == ManuscriptMarkType.CharacterStyle && mark.Value == style.Role)))).ToList(),
                Bibliography.Where(record => ManuscriptTraversal.EnumerateCitations(document)
                    .Any(citation => citation.Cluster.Items.Any(item => item.BibliographicRecordId == record.Id))).ToList());
            ValidateResources(resources);
            var recordIds = resources.Bibliography.Select(record => record.Id).ToHashSet();
            var fragment = new SemanticImportFragment(document, resources, _report.ToList(), SourceMappings.Where(item => recordIds.Contains(item.Value))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
            if (JsonSerializer.SerializeToUtf8Bytes(fragment, ManuscriptCodec.JsonOptions).Length > SemanticImportLimits.MaximumFragmentBytes)
                throw new InvalidDataException("The converted Word fragment exceeds the 24 MiB recoverable authoring limit. Import a smaller selection.");
            return fragment;
        }
    }
}
