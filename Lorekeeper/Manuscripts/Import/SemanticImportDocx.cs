using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using Lorekeeper.Citations;
using Lorekeeper.Models;

namespace Lorekeeper.Manuscripts.Import;

public sealed partial class SemanticImportService
{
    private sealed class DocxReader(byte[] bytes, CancellationToken cancellationToken)
    {
        private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private readonly ImportBuilder _builder = new(cancellationToken);
        private readonly Dictionary<string, XElement> _styles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CitationRecord> _sources = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ManuscriptCitationCluster> _semanticCitations = new(StringComparer.Ordinal);
        private readonly HashSet<string> _usedNotes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _listCounts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _listIds = new(StringComparer.Ordinal);
        private readonly Dictionary<OpenXmlPart, XElement> _noteParts = [];
        private readonly Dictionary<string, XElement> _semanticEndnotes = new(StringComparer.Ordinal);
        private MainDocumentPart _main = null!;
        private XElement? _numbering;
        private double _availableWidthEmu = 6.5 * 914400;

        public SemanticImportFragment Read()
        {
            DocxPackageSafety.Validate(bytes, cancellationToken);
            using var stream = new MemoryStream(bytes, writable: false);
            using var package = WordprocessingDocument.Open(stream, false, new OpenSettings { MaxCharactersInPart = 32 * 1024 * 1024 });
            _main = package.MainDocumentPart ?? throw new InvalidDataException("The DOCX has no main document.");
            var body = _main.Document?.Body ?? throw new InvalidDataException("The DOCX has no document body.");
            if (_main.StyleDefinitionsPart?.Styles is { } styles)
                foreach (var style in XElement.Parse(styles.OuterXml).Elements(W + "style"))
                    if (!_styles.TryAdd(Attr(style, "styleId"), style)) throw new InvalidDataException("Duplicate Word style identity.");
            if (_main.NumberingDefinitionsPart?.Numbering is { } numbering)
                _numbering = XElement.Parse(numbering.OuterXml);
            foreach (var part in _main.CustomXmlParts)
            {
                using var xml = part.GetStream();
                using var reader = XmlReader.Create(xml, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
                var customRoot = XElement.Load(reader);
                if (customRoot.Name == XName.Get("citations", SemanticWordCitationMetadata.Namespace)) ReadSemanticCitations(customRoot);
                else ReadSources(customRoot, _builder, _sources);
            }
            var root = XElement.Parse(body.OuterXml);
            foreach (var control in root.Descendants(W + "sdt"))
            {
                var tag = Value(control.Element(W + "sdtPr"), "tag");
                if (tag.StartsWith("lorekeeper-endnote:", StringComparison.Ordinal)
                    && (!_semanticEndnotes.TryAdd(tag["lorekeeper-endnote:".Length..],
                        control.Element(W + "sdtContent") ?? throw new InvalidDataException("An exported endnote has no content."))))
                    throw new InvalidDataException("Duplicate exported endnote identity.");
            }
            var section = root.Elements(W + "sectPr").LastOrDefault();
            var pageWidth = Number(Attr(section?.Element(W + "pgSz"), "w")) ?? 12240;
            var margins = section?.Element(W + "pgMar");
            var textWidth = pageWidth - (Number(Attr(margins, "left")) ?? 1440) - (Number(Attr(margins, "right")) ?? 1440);
            if (textWidth > 0) _availableWidthEmu = textWidth * 635d;
            if (root.Descendants().Any(item => item.Name == W + "del" || item.Name == W + "ins"
                || item.Name == W + "moveFrom" || item.Name == W + "moveTo"))
                _builder.Warn("Imported final revised text; Word revision history was discarded.");
            if (_main.WordprocessingCommentsPart is not null)
                _builder.Warn("Word comments were discarded; manuscript text was preserved.");
            if (_main.HeaderParts.Any() || _main.FooterParts.Any() || root.Descendants(W + "sectPr").Any())
                _builder.Warn("Word headers, footers, columns, and section page geometry are not imported; use Lorekeeper page setup.");
            var blocks = Blocks(root, _main, allowTable: true, inNote: false);
            if (_semanticEndnotes.Keys.Any(anchor => !_usedNotes.Contains("semantic:" + anchor)))
                throw new InvalidDataException("An exported endnote has lost its manuscript reference. Repair its reference in Word before importing.");
            return _builder.Finish(blocks);
        }

        private List<ManuscriptBlock> Blocks(XElement parent, OpenXmlPart owner, bool allowTable, bool inNote)
        {
            var result = new List<ManuscriptBlock>();
            foreach (var element in parent.Elements())
            {
                _builder.Check();
                if (element.Name == W + "del" || element.Name == W + "moveFrom") continue;
                if (element.Name == W + "p")
                {
                    var paragraphs = Paragraph(element, owner, inNote);
                    if (!allowTable)
                        paragraphs = paragraphs.Select(block => block.Type == ManuscriptBlockType.Heading
                            ? block with { Type = ManuscriptBlockType.Paragraph, HeadingLevel = null } : block).ToList();
                    if (result.LastOrDefault() is { Type: ManuscriptBlockType.Figure } figure
                        && Value(element.Element(W + "pPr"), "pStyle").Equals("Caption", StringComparison.OrdinalIgnoreCase)
                        && paragraphs.Count == 1)
                        result[^1] = figure with { Content = paragraphs[0].Content, StyleRole = paragraphs[0].StyleRole };
                    else result.AddRange(paragraphs);
                }
                else if (element.Name == W + "tbl")
                {
                    if (allowTable) result.Add(Table(element, owner));
                    else
                    {
                        _builder.Warn("A nested or note table was flattened into paragraphs; nested tables are not supported.");
                        foreach (var cell in element.Elements(W + "tr").SelectMany(row => row.Elements(W + "tc")))
                            result.AddRange(Blocks(cell, owner, false, inNote));
                    }
                }
                else if (element.Name == W + "sdt")
                {
                    if (Value(element.Element(W + "sdtPr"), "tag") == "lorekeeper-generated-endnotes") continue;
                    if (element.Element(W + "sdtContent") is { } content) result.AddRange(Blocks(content, owner, allowTable, inNote));
                }
                else if (element.Name == W + "ins" || element.Name == W + "moveTo" || element.Name.LocalName == "customXml")
                    result.AddRange(Blocks(element, owner, allowTable, inNote));
            }
            return result;
        }

        private List<ManuscriptBlock> Paragraph(XElement paragraph, OpenXmlPart owner, bool inNote)
        {
            var direct = paragraph.Element(W + "pPr");
            var styleId = Value(direct, "pStyle");
            var properties = Merge(StyleProperties(styleId, "pPr"), direct);
            var runDefaults = Merge(StyleProperties(styleId, "rPr"), direct?.Element(W + "rPr"));
            var definition = Definition(properties, runDefaults);
            var styleName = _styles.TryGetValue(styleId, out var style) ? Value(style, "name") : "Word paragraph";
            var role = _builder.Style(styleName, ManuscriptStyleKind.Paragraph, definition);
            var heading = Number(Value(properties, "outlineLvl")) is int outline && outline is >= 0 and <= 5 ? outline + 1
                : Regex.Match(styleId, "^Heading([1-6])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) is { Success: true } match
                    ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : (int?)null;
            if (inNote && heading is not null)
            {
                _builder.Warn("Headings inside Word notes were retained as styled paragraphs.");
                heading = null;
            }
            ManuscriptListItem? list = null;
            if (properties.Element(W + "numPr") is { } numPr && Value(numPr, "numId") is { Length: > 0 } numId && numId != "0")
            {
                var level = Math.Clamp(Number(Value(numPr, "ilvl")) ?? 0, 0, 8);
                var instance = _numbering?.Elements(W + "num").SingleOrDefault(item => Attr(item, "numId") == numId);
                var abstractId = Value(instance, "abstractNumId");
                var levelElement = _numbering?.Elements(W + "abstractNum").SingleOrDefault(item => Attr(item, "abstractNumId") == abstractId)
                    ?.Elements(W + "lvl").SingleOrDefault(item => Attr(item, "ilvl") == level.ToString(CultureInfo.InvariantCulture));
                var format = Value(levelElement, "numFmt");
                if (format is not ("bullet" or "decimal")) _builder.Warn("Non-decimal Word list markers use Lorekeeper decimal numbering.");
                var start = Number(Value(instance?.Elements(W + "lvlOverride").FirstOrDefault(item => Attr(item, "ilvl") == level.ToString(CultureInfo.InvariantCulture)), "startOverride"))
                    ?? Number(Value(levelElement, "start")) ?? 1;
                var key = numId + ":" + level;
                var first = !_listCounts.ContainsKey(key);
                _listCounts[key] = 1;
                if (!_listIds.TryGetValue(numId, out var listId)) _listIds[numId] = listId = ImportBuilder.Id();
                list = new() { Id = listId, Ordered = format != "bullet", Level = level, Start = first && format != "bullet" ? Math.Clamp(start, 1, 1_000_000) : null };
            }
            var content = new List<ManuscriptInline>();
            var figures = new List<ManuscriptBlock>();
            var fields = new Stack<Field>();
            void Append(ManuscriptInline inline)
            {
                if (fields.TryPeek(out var field)) { if (field.Results) field.Display.Add(inline); }
                else content.Add(inline);
            }
            void Visit(XElement element, XElement inherited)
            {
                _builder.Check();
                var name = element.Name.LocalName;
                if (element.Name.Namespace == W && name is "pPr" or "rPr" or "del" or "moveFrom") return;
                if (element.Name == W + "r")
                {
                    var directRun = element.Element(W + "rPr");
                    var run = Merge(Merge(inherited, StyleProperties(Value(directRun, "rStyle"), "rPr")), directRun);
                    foreach (var child in element.Elements()) Visit(child, run);
                    return;
                }
                if (element.Name == W + "sdt" && Value(element.Element(W + "sdtPr"), "tag") is { } tag
                    && tag.StartsWith(SemanticWordCitationMetadata.TagPrefix, StringComparison.Ordinal))
                {
                    if (_semanticCitations.TryGetValue(tag, out var cluster))
                    {
                        Append(new() { Id = ImportBuilder.Id(), Type = ManuscriptInlineType.Citation, Citation = cluster });
                        return;
                    }
                    _builder.Warn("A Lorekeeper citation lost its DOCX metadata. Display text was preserved; repair it in Bibliography.");
                }
                if (element.Name == W + "fldChar")
                {
                    switch (Attr(element, "fldCharType"))
                    {
                        case "begin": fields.Push(new()); break;
                        case "separate":
                            if (!fields.TryPeek(out var field)) throw new InvalidDataException("Malformed Word field ownership.");
                            field.Results = true; break;
                        case "end":
                            if (!fields.TryPop(out var completed)) throw new InvalidDataException("Malformed Word field ownership.");
                            foreach (var inline in ResolveField(completed.Code.ToString(), completed.Display, _builder, _sources)) Append(inline);
                            break;
                    }
                    return;
                }
                if (element.Name == W + "instrText")
                {
                    if (fields.TryPeek(out var field)) field.Code.Append(element.Value);
                    return;
                }
                if (element.Name == W + "fldSimple")
                {
                    var field = new Field { Results = true };
                    field.Code.Append(Attr(element, "instr")); fields.Push(field);
                    foreach (var child in element.Elements()) Visit(child, inherited);
                    fields.Pop();
                    foreach (var inline in ResolveField(field.Code.ToString(), field.Display, _builder, _sources)) Append(inline);
                    return;
                }
                if (element.Name == W + "t" || element.Name == W + "tab" || element.Name == W + "br" || element.Name == W + "cr")
                {
                    Append(new() { Text = name is "br" or "cr" ? "\n" : name == "tab" ? "\t" : element.Value, Marks = Marks(inherited) });
                    return;
                }
                if (element.Name == W + "footnoteReference" || element.Name == W + "endnoteReference")
                {
                    if (inNote) throw new InvalidDataException("Word notes cannot contain note references.");
                    Append(Note(Attr(element, "id"), name == "endnoteReference")); return;
                }
                if (element.Name == W + "hyperlink")
                {
                    var anchor = Attr(element, "anchor");
                    if (_semanticEndnotes.TryGetValue(anchor, out var semanticNote))
                    {
                        if (inNote || !_usedNotes.Add("semantic:" + anchor)) throw new InvalidDataException("Malformed exported endnote ownership.");
                        var id = ImportBuilder.Id();
                        var noteBlocks = Blocks(semanticNote, _main, false, true);
                        if (noteBlocks.Count == 0) noteBlocks.Add(new() { Id = ImportBuilder.Id() });
                        _builder.Notes.Add(new() { Id = id, Kind = ManuscriptNoteKind.Endnote, Content = noteBlocks });
                        Append(new() { Id = ImportBuilder.Id(), Type = ManuscriptInlineType.NoteReference, NoteId = id });
                        return;
                    }
                    var relationship = owner.HyperlinkRelationships.SingleOrDefault(item => item.Id == (string?)element.Attribute(R + "id"));
                    var link = relationship?.Uri.ToString();
                    var before = fields.TryPeek(out var field) ? field.Display.Count : content.Count;
                    foreach (var child in element.Elements()) Visit(child, inherited);
                    var destination = fields.TryPeek(out field) ? field.Display : content;
                    if (link is not null && SafeLink(link))
                        for (var index = before; index < destination.Count; index++)
                            if (destination[index].Type == ManuscriptInlineType.Text)
                                destination[index] = destination[index] with { Marks = [.. destination[index].Marks, new() { Type = ManuscriptMarkType.Link, Value = link }] };
                    else _builder.Warn("An internal or unsafe Word hyperlink was retained as text without its link target.");
                    return;
                }
                if (element.Name == W + "drawing" || element.Name == W + "pict")
                {
                    var blip = element.Descendants().FirstOrDefault(item => item.Name.LocalName == "blip");
                    var relationshipId = (string?)blip?.Attribute(R + "embed")
                        ?? (string?)element.Descendants().FirstOrDefault(item => item.Name.LocalName == "imagedata")?.Attribute(R + "id");
                    if (relationshipId is null || owner.GetPartById(relationshipId) is not ImagePart imagePart)
                    { _builder.Warn("Unsupported Word artwork was omitted. Supply a PNG or JPEG image in the original file."); return; }
                    using var imageStream = imagePart.GetStream();
                    using var buffer = new MemoryStream(); imageStream.CopyTo(buffer);
                    var image = _builder.Image(buffer.ToArray(), Path.GetFileName(imagePart.Uri.ToString()));
                    var description = element.Descendants().FirstOrDefault(item => item.Name.LocalName == "docPr");
                    var extent = element.Descendants().FirstOrDefault(item => item.Name.LocalName == "extent");
                    var width = double.TryParse((string?)extent?.Attribute("cx"), NumberStyles.Float, CultureInfo.InvariantCulture, out var cx)
                        && double.IsFinite(cx) && cx > 0 ? Math.Clamp(cx / _availableWidthEmu * 100, 1, 100) : 100;
                    figures.Add(new() { Id = ImportBuilder.Id(), Type = ManuscriptBlockType.Figure,
                        StyleRole = ManuscriptStyleRoles.FigureCaption, ImageId = image.Id,
                        AltText = (string?)description?.Attribute("descr") ?? (string?)description?.Attribute("title") ?? "",
                        FigurePresentation = new() { Placement = FigurePlacementIntent.Inline, WidthPercent = width } });
                    _builder.Warn("Word drawings are imported as flowing Figures after their containing paragraph; unsupported wrapping and positioning are discarded.");
                    return;
                }
                if (element.Name == W + "object" || element.Name == W + "altChunk")
                    throw new InvalidDataException("Executable or embedded Word objects are not supported.");
                foreach (var child in element.Elements()) Visit(child, inherited);
            }
            foreach (var child in paragraph.Elements()) Visit(child, runDefaults);
            if (fields.Count != 0) throw new InvalidDataException("A Word field is missing its end marker.");
            var block = new ManuscriptBlock { Id = ImportBuilder.Id(), Type = list is not null ? ManuscriptBlockType.ListItem
                : heading is not null ? ManuscriptBlockType.Heading : ManuscriptBlockType.Paragraph,
                HeadingLevel = list is null ? heading : null, StyleRole = role, List = list, Content = content };
            return content.Count > 0 || figures.Count == 0 ? [block, .. figures] : figures;
        }

        private ManuscriptBlock Table(XElement table, OpenXmlPart owner)
        {
            var widths = table.Element(W + "tblGrid")?.Elements(W + "gridCol")
                .Select(item => Math.Clamp(Number(Attr(item, "w")) ?? 1, 1, 100_000)).ToList() ?? [];
            if (widths.Count == 0)
                widths = Enumerable.Repeat(1, table.Elements(W + "tr").Select(row => row.Elements(W + "tc")
                    .Sum(cell => Number(Value(cell.Element(W + "tcPr"), "gridSpan")) ?? 1)).DefaultIfEmpty(0).Max()).ToList();
            var rows = new List<ManuscriptTableRow>();
            var origins = new Dictionary<int, (int Row, int Cell, int Span)>();
            var headers = 0;
            foreach (var row in table.Elements(W + "tr"))
            {
                if (rows.Count == headers && row.Element(W + "trPr")?.Element(W + "tblHeader") is not null) headers++;
                var cells = new List<ManuscriptTableCell>();
                var nextOrigins = new Dictionary<int, (int Row, int Cell, int Span)>();
                var column = 0;
                foreach (var cell in row.Elements(W + "tc"))
                {
                    _builder.Check();
                    var properties = cell.Element(W + "tcPr");
                    var span = Number(Value(properties, "gridSpan")) ?? 1;
                    if (span < 1 || column + span > widths.Count) throw new InvalidDataException("A Word table has an invalid column span.");
                    var merge = properties?.Element(W + "vMerge");
                    if (merge is not null && Attr(merge, "val") != "restart")
                    {
                        if (!origins.TryGetValue(column, out var origin) || origin.Span != span)
                            throw new InvalidDataException("A Word table has a dangling vertical merge.");
                        var original = rows[origin.Row].Cells[origin.Cell];
                        rows[origin.Row].Cells[origin.Cell] = original with { RowSpan = original.RowSpan + 1 };
                        nextOrigins[column] = origin;
                        if (cell.Descendants(W + "t").Any(item => item.Value.Length > 0))
                            throw new InvalidDataException("A Word continuation cell contains text that would be lost by merging.");
                    }
                    else
                    {
                        var blocks = Blocks(cell, owner, false, false);
                        if (blocks.Count == 0) blocks.Add(new() { Id = ImportBuilder.Id() });
                        cells.Add(new() { Id = ImportBuilder.Id(), ColumnSpan = span, Content = blocks });
                        if (merge is not null) nextOrigins[column] = (rows.Count, cells.Count - 1, span);
                    }
                    column += span;
                }
                if (column != widths.Count) throw new InvalidDataException("A Word table row does not cover its declared grid.");
                rows.Add(new() { Id = ImportBuilder.Id(), Cells = cells }); origins = nextOrigins;
            }
            if (table.Descendants().Any(item => item.Name.LocalName is "shd" or "tcBorders" or "tblBorders"))
                _builder.Warn("Table content, merges, widths, and header rows were preserved; Word-specific borders and shading use Lorekeeper table styling.");
            return new() { Id = ImportBuilder.Id(), Type = ManuscriptBlockType.Table, StyleRole = ManuscriptStyleRoles.Table,
                Table = new() { Id = ImportBuilder.Id(), ColumnWidthWeights = widths, HeaderRowCount = headers, Rows = rows } };
        }

        private ManuscriptInline Note(string id, bool endnote)
        {
            var key = (endnote ? "endnote:" : "footnote:") + id;
            if (!_usedNotes.Add(key)) throw new InvalidDataException("A Word note is referenced more than once.");
            OpenXmlPart? part = endnote ? _main.EndnotesPart : _main.FootnotesPart;
            if (part is null) throw new InvalidDataException("A Word note reference has no owning note part.");
            if (!_noteParts.TryGetValue(part, out var root))
            {
                using var stream = part.GetStream();
                _noteParts[part] = root = XElement.Load(stream);
            }
            var note = root.Elements(W + (endnote ? "endnote" : "footnote")).SingleOrDefault(item => Attr(item, "id") == id)
                ?? throw new InvalidDataException("A Word note reference has no matching content.");
            var blocks = Blocks(note, part, false, true);
            if (blocks.Count == 0) blocks.Add(new() { Id = ImportBuilder.Id() });
            var noteId = ImportBuilder.Id();
            _builder.Notes.Add(new() { Id = noteId, Kind = endnote ? ManuscriptNoteKind.Endnote : ManuscriptNoteKind.Footnote, Content = blocks });
            return new() { Id = ImportBuilder.Id(), Type = ManuscriptInlineType.NoteReference, NoteId = noteId };
        }

        private XElement StyleProperties(string id, string kind, HashSet<string>? visited = null)
        {
            if (id.Length == 0 || !_styles.TryGetValue(id, out var style)) return new(W + kind);
            visited ??= new(StringComparer.Ordinal);
            if (!visited.Add(id) || visited.Count > SemanticImportLimits.MaximumDepth)
                throw new InvalidDataException("Word styles contain a circular or excessive inheritance chain.");
            return Merge(StyleProperties(Value(style, "basedOn"), kind, visited), style.Element(W + kind));
        }

        private static XElement Merge(XElement inherited, XElement? direct)
        {
            var result = new XElement(inherited);
            if (direct is null) return result;
            foreach (var child in direct.Elements()) { result.Elements(child.Name).Remove(); result.Add(new XElement(child)); }
            return result;
        }

        private ManuscriptStyleProperties Definition(XElement? paragraph, XElement run)
        {
            var font = run.Element(W + "rFonts");
            var fontName = (string?)font?.Attribute(W + "ascii") ?? (string?)font?.Attribute(W + "hAnsi");
            var size = Number(Value(run, "sz")) / 2d;
            var indent = paragraph?.Element(W + "ind");
            var spacing = paragraph?.Element(W + "spacing");
            var line = Number(Attr(spacing, "line"));
            if (line is not null && Attr(spacing, "lineRule") is "exact" or "atLeast")
                _builder.Warn("Exact Word line heights use proportional Lorekeeper line spacing.");
            var emTwips = (size ?? 12) * 20;
            return new(FontFamilyKey: FontKey(fontName), FontSizePoints: Clamp(size, 1, 288),
                FontWeight: Flag(run, "b") is bool bold ? bold ? 700 : 400 : null,
                Italic: Flag(run, "i"), SmallCaps: Flag(run, "smallCaps"),
                LineHeight: line is null ? null : Math.Clamp(Attr(spacing, "lineRule") is "exact" or "atLeast" ? line.Value / emTwips : line.Value / 240d, 0.1, 5),
                SpaceBeforePoints: Clamp(Number(Attr(spacing, "before")) / 20d, 0, 288),
                SpaceAfterPoints: Clamp(Number(Attr(spacing, "after")) / 20d, 0, 288),
                KeepWithNext: Flag(paragraph, "keepNext"), TextAlign: Value(paragraph, "jc") switch
                { "center" => "center", "right" or "end" => "right", "both" or "distribute" => "justify", "left" or "start" => "left", _ => null },
                LeftIndentEm: Clamp(Number(Attr(indent, "left")) / emTwips, 0, 12),
                RightIndentEm: Clamp(Number(Attr(indent, "right")) / emTwips, 0, 12),
                FirstLineIndentEm: Clamp((Number(Attr(indent, "firstLine")) ?? -Number(Attr(indent, "hanging"))) / emTwips, -12, 12),
                StartOnNewPage: Flag(paragraph, "pageBreakBefore"));
        }

        private List<ManuscriptMark> Marks(XElement run)
        {
            var result = new List<ManuscriptMark>();
            foreach (var (name, type) in new[] { ("b", ManuscriptMarkType.Strong), ("i", ManuscriptMarkType.Emphasis),
                ("strike", ManuscriptMarkType.Strikethrough), ("smallCaps", ManuscriptMarkType.SmallCaps) })
                if (Flag(run, name) == true) result.Add(new() { Type = type });
            if (run.Element(W + "u") is { } underline && Attr(underline, "val") != "none") result.Add(new() { Type = ManuscriptMarkType.Underline });
            var vertical = Value(run, "vertAlign");
            if (vertical is "superscript" or "subscript")
                result.Add(new() { Type = vertical == "superscript" ? ManuscriptMarkType.Superscript : ManuscriptMarkType.Subscript });
            if (Value(run, "lang") is { Length: > 0 } language) result.Add(new() { Type = ManuscriptMarkType.Language, Value = language });
            var definition = Definition(null, run);
            if (definition != new ManuscriptStyleProperties())
                result.Add(new() { Type = ManuscriptMarkType.CharacterStyle, Value = _builder.Style("Word character", ManuscriptStyleKind.Character, definition) });
            return result;
        }

        private string? FontKey(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            _builder.Warn($"Font '{name}' uses a bundled fallback; original font files are not imported.");
            return name.Contains("Courier", StringComparison.OrdinalIgnoreCase) || name.Contains("Mono", StringComparison.OrdinalIgnoreCase) ? "mono"
                : name.Contains("Arial", StringComparison.OrdinalIgnoreCase) || name.Contains("Calibri", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Helvetica", StringComparison.OrdinalIgnoreCase) || name.Contains("Sans", StringComparison.OrdinalIgnoreCase) ? "sans" : "serif";
        }

        private void ReadSemanticCitations(XElement root)
        {
            var metadata = JsonSerializer.Deserialize<SemanticWordCitationMetadata>(root.Value, ManuscriptCodec.JsonOptions)
                ?? throw new InvalidDataException("DOCX citation metadata is missing.");
            if (metadata.Version != 1 || metadata.Records.Count > SemanticImportLimits.MaximumBibliography
                || metadata.Clusters.Count > SemanticImportLimits.MaximumNodes)
                throw new InvalidDataException("Unsupported or oversized DOCX citation metadata.");
            var ids = new Dictionary<Guid, Guid>();
            foreach (var record in metadata.Records)
            {
                var id = Guid.NewGuid();
                if (!ids.TryAdd(record.Id, id)) throw new InvalidDataException("Duplicate DOCX bibliography identity.");
                _builder.Bibliography.Add(record with { Id = id });
                _builder.SourceMappings[record.Id.ToString("D")] = id;
            }
            foreach (var pair in metadata.Clusters)
            {
                if (!pair.Key.StartsWith(SemanticWordCitationMetadata.TagPrefix, StringComparison.Ordinal)
                    || pair.Value.Items.Any(item => !ids.ContainsKey(item.BibliographicRecordId)))
                    throw new InvalidDataException("DOCX citation metadata contains a dangling reference.");
                var cluster = pair.Value with { Items = pair.Value.Items.Select(item => item with
                { BibliographicRecordId = ids[item.BibliographicRecordId], SourceLocationId = null }).ToList() };
                if (!_semanticCitations.TryAdd(pair.Key, cluster)) throw new InvalidDataException("Duplicate DOCX citation tag.");
            }
        }

        public static void ReadSources(XElement root, ImportBuilder builder, Dictionary<string, CitationRecord> sources)
        {
            XNamespace bibliography = "http://schemas.openxmlformats.org/officeDocument/2006/bibliography";
            foreach (var source in root.DescendantsAndSelf(bibliography + "Source"))
            {
                builder.Check();
                string Text(string name) => source.Element(bibliography + name)?.Value.Trim() ?? "";
                var tag = Text("Tag");
                if (tag.Length == 0 || Text("Title").Length == 0) continue;
                List<CitationPerson> People(string role)
                {
                    var element = source.Element(bibliography + "Author")?.Element(bibliography + role);
                    if (element?.Element(bibliography + "Corporate") is { } corporate) return [new(Literal: corporate.Value)];
                    return element?.Descendants(bibliography + "Person").Select(person => new CitationPerson(
                        person.Element(bibliography + "Last")?.Value ?? "", person.Element(bibliography + "First")?.Value ?? ""))
                        .Where(person => person.Family.Length > 0).ToList() ?? [];
                }
                var kind = Text("SourceType") switch
                { "BookSection" => BibliographicRecordKind.BookChapter, "JournalArticle" => BibliographicRecordKind.JournalArticle,
                    "ArticleInAPeriodical" => BibliographicRecordKind.MagazineArticle, "DocumentFromInternetSite" or "InternetSite" => BibliographicRecordKind.WebPage,
                    "Report" => BibliographicRecordKind.Report, "Book" => BibliographicRecordKind.Book, _ => (BibliographicRecordKind?)null };
                if (kind is null)
                {
                    builder.Warn("An unsupported Word bibliography type was omitted. Its citation display text is retained; choose a supported record type in Bibliography.");
                    continue;
                }
                var record = new CitationRecord(Guid.NewGuid(), kind.Value, Text("Title"), Text("BookTitle") is { Length: > 0 } book ? book : Text("JournalName"),
                    People("Author"), People("Editor"), People("Translator"), new(Number(Text("Year")), Number(Text("Month")), Number(Text("Day"))),
                    new(Number(Text("YearAccessed")), Number(Text("MonthAccessed")), Number(Text("DayAccessed"))), Text("Edition"), Text("Publisher"), Text("City"),
                    Text("Institution"), Text("ThesisType"), Text("Volume"), Text("Issue"), Text("Pages"), Text("DOI"), Text("URL"), Text("StandardNumber"));
                if (!sources.TryAdd(tag, record)) throw new InvalidDataException("Duplicate Word bibliography tag.");
                builder.Bibliography.Add(record); builder.SourceMappings[tag] = record.Id;
            }
        }

        public static IReadOnlyList<ManuscriptInline> ResolveField(string instruction, List<ManuscriptInline> display,
            ImportBuilder builder, IReadOnlyDictionary<string, CitationRecord> sources)
        {
            var tokens = Regex.Matches(instruction, "\"[^\"]*\"|\\S+", RegexOptions.CultureInvariant)
                .Select(match => match.Value.Trim('"')).ToList();
            if (tokens.Count == 0 || !tokens[0].Equals("CITATION", StringComparison.OrdinalIgnoreCase))
            { builder.Warn("Word fields were imported as their displayed text; automatic field updates are not retained."); return display; }
            var tags = tokens.Count > 1 ? new List<string> { tokens[1] } : [];
            var locator = "";
            for (var index = 2; index + 1 < tokens.Count; index++)
            {
                if (tokens[index] == "\\m") tags.Add(tokens[++index]);
                else if (tokens[index] == "\\p") locator = tokens[++index];
            }
            if (tags.Count == 0 || tags.Any(tag => !sources.ContainsKey(tag)))
            { builder.Warn("A Word citation has missing bibliography metadata. Its display text was preserved; add or repair its record in Bibliography."); return display; }
            return [new() { Id = ImportBuilder.Id(), Type = ManuscriptInlineType.Citation, Citation = new()
            { Items = tags.Select(tag => new ManuscriptCitationItem { BibliographicRecordId = sources[tag].Id, LocatorValue = locator }).ToList() } }];
        }

        private sealed class Field
        {
            public StringBuilder Code { get; } = new();
            public List<ManuscriptInline> Display { get; } = [];
            public bool Results { get; set; }
        }
        private static string Attr(XElement? element, string name) => (string?)element?.Attribute(W + name) ?? "";
        private static string Value(XElement? parent, string name) => Attr(parent?.Element(W + name), "val");
        private static int? Number(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
        private static double? Clamp(double? value, double min, double max) => value is double number ? Math.Clamp(number, min, max) : null;
        private static bool? Flag(XElement? parent, string name) => parent?.Element(W + name) is { } element ? Attr(element, "val") is not ("0" or "false" or "off") : null;
    }

    private static bool SafeLink(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" or "mailto";
}
