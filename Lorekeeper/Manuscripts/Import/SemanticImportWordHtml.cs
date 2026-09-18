using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Lorekeeper.Models;
using Lorekeeper.Citations;

namespace Lorekeeper.Manuscripts.Import;

public sealed partial class SemanticImportService
{
    private sealed class WordHtmlReader(string html, IReadOnlyList<SemanticImportImage> clipboardImages, CancellationToken cancellationToken)
    {
        private readonly ImportBuilder _builder = new(cancellationToken);
        private readonly Dictionary<string, Dictionary<string, string>> _rules = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IElement> _notes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _usedNotes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SemanticImportImage> _images = new(StringComparer.Ordinal);
        private readonly HashSet<string> _startedLists = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _listIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CitationRecord> _sources = new(StringComparer.Ordinal);

        public async Task<SemanticImportFragment> ReadAsync()
        {
            if (html.Length is 0 or > SemanticImportLimits.MaximumHtmlCharacters)
                throw new InvalidDataException("Word clipboard HTML exceeds the 4 MiB character limit.");
            if (clipboardImages.Count > SemanticImportLimits.MaximumImages
                || clipboardImages.Sum(image => (long)image.Data.Length) > SemanticImportLimits.MaximumInputBytes)
                throw new InvalidDataException("Clipboard images exceed the import limit.");
            // A standalone parser has no resource loader or script engine. Input is never rendered.
            using var document = await new HtmlParser().ParseDocumentAsync(html, cancellationToken);
            var pending = new Stack<(INode Node, int Depth)>(); pending.Push((document, 0));
            while (pending.TryPop(out var entry))
            {
                _builder.Check();
                if (entry.Depth > SemanticImportLimits.MaximumDepth) throw new InvalidDataException("Clipboard HTML nesting is too deep.");
                if (entry.Node is IElement element)
                {
                    if (element.LocalName is "script" or "iframe" or "object" or "embed" or "svg" or "math"
                        || element.Attributes.Any(attribute => attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("Clipboard HTML contains executable or unsupported active content.");
                    if (element.LocalName == "style") ReadRules(element.TextContent);
                    if (element.Id is { Length: > 0 } && Css(element).GetValueOrDefault("mso-element") is "footnote" or "endnote")
                        if (!_notes.TryAdd(element.Id, element)) throw new InvalidDataException("Duplicate clipboard note identity.");
                }
                foreach (var child in entry.Node.ChildNodes) pending.Push((child, entry.Depth + 1));
            }
            XNamespace bibliography = "http://schemas.openxmlformats.org/officeDocument/2006/bibliography";
            XElement SourceXml(IElement element) => new(bibliography + element.LocalName.Split(':')[^1],
                element.ChildNodes.Select<INode, object?>(node => node is IElement child ? SourceXml(child) : node is IText text ? text.Data : null));
            foreach (var source in document.All.Where(element => element.LocalName.Equals("b:source", StringComparison.OrdinalIgnoreCase)))
            {
                // HTML lowercases Office element names; restore the bibliography vocabulary.
                var xml = SourceXml(source);
                var names = new[] { "Source", "Tag", "SourceType", "Title", "BookTitle", "JournalName", "Author", "Editor", "Translator", "Corporate", "NameList", "Person", "Last", "First",
                    "Year", "Month", "Day", "YearAccessed", "MonthAccessed", "DayAccessed", "Edition", "Publisher", "City", "Institution", "ThesisType", "Volume", "Issue", "Pages", "DOI", "URL", "StandardNumber" };
                foreach (var element in xml.DescendantsAndSelf())
                    element.Name = bibliography + (names.FirstOrDefault(name => name.Equals(element.Name.LocalName, StringComparison.OrdinalIgnoreCase)) ?? element.Name.LocalName);
                DocxReader.ReadSources(xml, _builder, _sources);
            }
            _builder.Warn("Clipboard fidelity depends on Word's supplied HTML. Use Import DOCX if pictures, citations, or notes are missing.");
            return _builder.Finish(Blocks(document.Body ?? throw new InvalidDataException("Clipboard HTML has no body."), false, false));
        }

        private void ReadRules(string css)
        {
            foreach (Match match in Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}", RegexOptions.NonBacktracking))
            {
                var declarations = Declarations(match.Groups[2].Value);
                foreach (var selector in match.Groups[1].Value.Split(','))
                {
                    var key = selector.Trim();
                    if (Regex.IsMatch(key, @"^(?:[a-zA-Z][\w-]*)?(?:\.[\w-]+)?$", RegexOptions.NonBacktracking))
                        _rules[key] = declarations;
                    else if (key.StartsWith("@list ", StringComparison.OrdinalIgnoreCase))
                        _rules[key] = declarations;
                }
            }
        }

        private static Dictionary<string, string> Declarations(string text)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var declaration in text.Split(';'))
            {
                var separator = declaration.IndexOf(':');
                if (separator > 0) result[declaration[..separator].Trim()] = declaration[(separator + 1)..].Trim().Trim('"', '\'');
            }
            return result;
        }

        private Dictionary<string, string> Css(IElement element)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Add(Dictionary<string, string>? values) { if (values is not null) foreach (var item in values) result[item.Key] = item.Value; }
            Add(_rules.GetValueOrDefault(element.LocalName));
            foreach (var name in element.ClassList)
            { Add(_rules.GetValueOrDefault("." + name)); Add(_rules.GetValueOrDefault(element.LocalName + "." + name)); }
            Add(Declarations(element.GetAttribute("style") ?? ""));
            return result;
        }

        private List<ManuscriptBlock> Blocks(IElement parent, bool inCell, bool inNote, int listLevel = 0, string? listId = null, bool ordered = false)
        {
            var result = new List<ManuscriptBlock>();
            var loose = new List<INode>();
            void Flush()
            {
                if (loose.Count == 0) return;
                result.AddRange(Paragraph(parent, loose, inNote, null)); loose.Clear();
            }
            foreach (var node in parent.ChildNodes)
            {
                _builder.Check();
                if (node is not IElement element) { if (node is IText text && !string.IsNullOrWhiteSpace(text.Data)) loose.Add(node); continue; }
                var css = Css(element);
                if (element.LocalName is "style" or "meta" or "link" or "title" or "xml") continue;
                if (css.GetValueOrDefault("mso-element") is "footnote-list" or "endnote-list" or "footnote" or "endnote") continue;
                if (element.LocalName is "del" || css.GetValueOrDefault("mso-element") == "comment")
                { _builder.Warn("Imported final text; clipboard comments and deleted revisions were discarded."); continue; }
                if (element.LocalName is "ol" or "ul")
                {
                    Flush(); result.AddRange(Blocks(element, inCell, inNote, listLevel + (listId is null ? 0 : 1), ImportBuilder.Id(), element.LocalName == "ol")); continue;
                }
                if (element.LocalName == "table")
                {
                    Flush();
                    if (!inCell && !inNote) result.Add(Table(element));
                    else
                    {
                        _builder.Warn("A nested or note table was flattened into paragraphs.");
                        foreach (var cell in element.QuerySelectorAll("td,th").Where(cell => cell.Closest("table") == element))
                            result.AddRange(Blocks(cell, true, inNote));
                    }
                    continue;
                }
                if (element.LocalName is "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "blockquote" or "li")
                {
                    Flush();
                    ManuscriptListItem? list = null;
                    if (element.LocalName == "li")
                    {
                        var id = listId ?? ImportBuilder.Id();
                        list = new() { Id = id, Ordered = ordered, Level = Math.Clamp(listLevel, 0, 8),
                            Start = ordered ? Integer(element.GetAttribute("value")) ?? (_startedLists.Add(id) ? Integer(parent.GetAttribute("start")) ?? 1 : null) : null };
                    }
                    else if (css.TryGetValue("mso-list", out var msoList))
                    {
                        var match = Regex.Match(msoList, @"(l\d+)\s+level(\d+)\s+(lfo\d+)", RegexOptions.NonBacktracking);
                        if (match.Success)
                        {
                            var sourceId = match.Groups[3].Value;
                            if (!_listIds.TryGetValue(sourceId, out var id)) _listIds[sourceId] = id = ImportBuilder.Id();
                            var level = Math.Clamp(int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) - 1, 0, 8);
                            var rule = _rules.GetValueOrDefault("@list " + match.Groups[1].Value + ":level" + match.Groups[2].Value);
                            list = new() { Id = id, Level = level, Ordered = rule?.GetValueOrDefault("mso-level-number-format") != "bullet",
                                Start = rule?.GetValueOrDefault("mso-level-number-format") != "bullet" && _startedLists.Add(id + ":" + level)
                                    ? Integer(rule?.GetValueOrDefault("mso-level-start-at")) ?? 1 : null };
                        }
                    }
                    result.AddRange(Paragraph(element, element.ChildNodes.Where(child => child is not IElement nested || nested.LocalName is not ("ol" or "ul")).ToList(), inNote, list));
                    foreach (var nested in element.Children.Where(child => child.LocalName is "ol" or "ul"))
                        result.AddRange(Blocks(nested, inCell, inNote, Math.Min(listLevel + 1, 8), ImportBuilder.Id(), nested.LocalName == "ol"));
                    continue;
                }
                if (element.LocalName is "div" or "section" or "article" or "main")
                { Flush(); result.AddRange(Blocks(element, inCell, inNote, listLevel, listId, ordered)); }
                else loose.Add(element);
            }
            Flush();
            if (inCell)
                result = result.Select(block => block.Type is ManuscriptBlockType.Heading or ManuscriptBlockType.BlockQuote
                    ? block with { Type = ManuscriptBlockType.Paragraph, HeadingLevel = null } : block).ToList();
            return result;
        }

        private List<ManuscriptBlock> Paragraph(IElement element, IEnumerable<INode> children, bool inNote, ManuscriptListItem? list)
        {
            var css = Css(element);
            var content = new List<ManuscriptInline>();
            var figures = new List<ManuscriptBlock>();
            void Visit(INode node, List<ManuscriptMark> marks)
            {
                _builder.Check();
                if (node is IComment comment && comment.Data.Contains("CITATION", StringComparison.OrdinalIgnoreCase))
                {
                    _builder.Warn("A Word clipboard field was retained as displayed text because its field boundaries are unavailable. Import DOCX to recover the citation, or add its record in Bibliography.");
                    return;
                }
                if (node is IText text) { if (text.Data.Length > 0) content.Add(new() { Text = text.Data, Marks = marks }); return; }
                if (node is not IElement inline) return;
                var format = Css(inline);
                if (inline.LocalName is "del" || format.GetValueOrDefault("mso-list")?.Equals("Ignore", StringComparison.OrdinalIgnoreCase) == true) return;
                if (inline.LocalName == "br") { content.Add(new() { Text = "\n" }); return; }
                if (inline.LocalName == "img")
                {
                    var source = inline.GetAttribute("src") ?? "";
                    if (!_images.TryGetValue(source, out var image))
                    {
                        byte[]? data = null;
                        if (source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                        {
                            var separator = source.IndexOf(',');
                            if (separator < 0 || !source[..separator].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("Clipboard images must use bounded base64 image data.");
                            data = Convert.FromBase64String(source[(separator + 1)..]);
                        }
                        else if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                            throw new InvalidDataException("Remote clipboard images are not fetched. Use a DOCX with embedded images.");
                        else
                        {
                            var fileName = Path.GetFileName(source.Replace('\\', '/'));
                            data = clipboardImages.FirstOrDefault(candidate => candidate.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase))?.Data;
                            if (data is null && clipboardImages.Count == 1 && _images.Count == 0) data = clipboardImages[0].Data;
                        }
                        if (data is null)
                        { _builder.Warn("A clipboard picture had no image data. Use Import DOCX to preserve this picture."); return; }
                        image = _builder.Image(data, "Word clipboard image"); _images[source] = image;
                    }
                    figures.Add(new() { Id = ImportBuilder.Id(), Type = ManuscriptBlockType.Figure, StyleRole = ManuscriptStyleRoles.FigureCaption,
                        ImageId = image.Id, AltText = inline.GetAttribute("alt") ?? "", FigurePresentation = new() });
                    return;
                }
                var next = marks.ToList();
                void Mark(ManuscriptMarkType type, string? value = null) { next.RemoveAll(mark => mark.Type == type); next.Add(new() { Type = type, Value = value }); }
                if (inline.LocalName is "b" or "strong" || format.GetValueOrDefault("font-weight") is "bold" or "700" or "800" or "900") Mark(ManuscriptMarkType.Strong);
                if (inline.LocalName is "i" or "em" || format.GetValueOrDefault("font-style") == "italic") Mark(ManuscriptMarkType.Emphasis);
                if (inline.LocalName == "u" || format.GetValueOrDefault("text-decoration")?.Contains("underline", StringComparison.OrdinalIgnoreCase) == true) Mark(ManuscriptMarkType.Underline);
                if (inline.LocalName is "s" or "strike") Mark(ManuscriptMarkType.Strikethrough);
                if (inline.LocalName == "sup") Mark(ManuscriptMarkType.Superscript);
                if (inline.LocalName == "sub") Mark(ManuscriptMarkType.Subscript);
                if (inline.GetAttribute("lang") is { Length: > 0 } language) Mark(ManuscriptMarkType.Language, language);
                if (inline.LocalName == "a" && inline.GetAttribute("href") is { } href)
                {
                    if (href.StartsWith('#') && _notes.TryGetValue(href[1..], out var note))
                    {
                        if (inNote || !_usedNotes.Add(href)) throw new InvalidDataException("Malformed clipboard note ownership.");
                        var noteId = ImportBuilder.Id();
                        var blocks = Blocks(note, false, true);
                        if (blocks.Count == 0) blocks.Add(new() { Id = ImportBuilder.Id() });
                        _builder.Notes.Add(new() { Id = noteId, Kind = Css(note).GetValueOrDefault("mso-element") == "endnote" ? ManuscriptNoteKind.Endnote : ManuscriptNoteKind.Footnote, Content = blocks });
                        content.Add(new() { Id = ImportBuilder.Id(), Type = ManuscriptInlineType.NoteReference, NoteId = noteId }); return;
                    }
                    if (inNote && href.Contains("ftnref", StringComparison.OrdinalIgnoreCase)) return;
                    if (href.Contains("ftn", StringComparison.OrdinalIgnoreCase) && href.StartsWith('#'))
                        throw new InvalidDataException("A clipboard note reference has no supplied note content. Use Import DOCX.");
                    if (SafeLink(href)) Mark(ManuscriptMarkType.Link, href);
                    else _builder.Warn("An unsafe or internal clipboard hyperlink was retained as text without its target.");
                }
                if (format.TryGetValue("mso-field-code", out var instruction))
                {
                    var start = content.Count;
                    foreach (var child in inline.ChildNodes) Visit(child, next);
                    var display = content.Skip(start).ToList(); content.RemoveRange(start, content.Count - start);
                    content.AddRange(DocxReader.ResolveField(instruction, display, _builder, _sources));
                    return;
                }
                if (inline.ClassList.Any(name => name.Contains("citation", StringComparison.OrdinalIgnoreCase)))
                    _builder.Warn("A clipboard citation was retained as displayed text. Import DOCX with bibliography metadata, or add its record in Bibliography.");
                var definition = Definition(format, character: true);
                if (definition != new ManuscriptStyleProperties()) Mark(ManuscriptMarkType.CharacterStyle, _builder.Style("Word character", ManuscriptStyleKind.Character, definition));
                foreach (var child in inline.ChildNodes) Visit(child, next);
            }
            foreach (var child in children) Visit(child, []);
            int? heading = element.LocalName.Length == 2 && element.LocalName[0] == 'h' && element.LocalName[1] is >= '1' and <= '6' ? element.LocalName[1] - '0' : null;
            if (inNote && (heading is not null || element.LocalName == "blockquote"))
            {
                _builder.Warn("Headings and block quotes inside Word notes were retained as styled paragraphs.");
                heading = null;
            }
            var role = _builder.Style(element.ClassList.FirstOrDefault() ?? "Word paragraph", ManuscriptStyleKind.Paragraph, Definition(css, false));
            var block = new ManuscriptBlock { Id = ImportBuilder.Id(), Content = content, StyleRole = role, List = list,
                Type = list is not null ? ManuscriptBlockType.ListItem : heading is not null ? ManuscriptBlockType.Heading
                    : !inNote && element.LocalName == "blockquote" ? ManuscriptBlockType.BlockQuote : ManuscriptBlockType.Paragraph,
                HeadingLevel = list is null ? heading : null };
            return content.Count > 0 || figures.Count == 0 ? [block, .. figures] : figures;
        }

        private ManuscriptBlock Table(IElement table)
        {
            var rows = new List<ManuscriptTableRow>();
            var occupied = new Dictionary<int, int>();
            var columns = 0;
            var widths = new Dictionary<int, double>();
            var headerCount = 0;
            foreach (var row in table.QuerySelectorAll("tr").Where(row => row.Closest("table") == table))
            {
                var cells = new List<ManuscriptTableCell>();
                var column = 0;
                var elements = row.Children.Where(cell => cell.LocalName is "td" or "th").ToList();
                if (rows.Count == headerCount && elements.Count > 0 && elements.All(cell => cell.LocalName == "th")) headerCount++;
                foreach (var cell in elements)
                {
                    while (occupied.GetValueOrDefault(column) > rows.Count) column++;
                    var rowSpan = Integer(cell.GetAttribute("rowspan")) ?? 1;
                    var span = Integer(cell.GetAttribute("colspan")) ?? 1;
                    if (rowSpan is < 1 or > 1000 || span is < 1 or > 100)
                        throw new InvalidDataException("Clipboard table spans exceed the supported bounds.");
                    for (var index = column; index < column + span; index++)
                    {
                        if (occupied.GetValueOrDefault(index) > rows.Count) throw new InvalidDataException("Clipboard table cells overlap.");
                        occupied[index] = rows.Count + rowSpan;
                        if (Points(Css(cell).GetValueOrDefault("width") ?? cell.GetAttribute("width")) is double width && width > 0)
                            widths.TryAdd(index, width / span);
                    }
                    var blocks = Blocks(cell, true, false);
                    if (blocks.Count == 0) blocks.Add(new() { Id = ImportBuilder.Id() });
                    cells.Add(new() { Id = ImportBuilder.Id(), RowSpan = rowSpan, ColumnSpan = span, Content = blocks }); column += span;
                }
                columns = Math.Max(columns, occupied.Keys.DefaultIfEmpty(-1).Max() + 1);
                rows.Add(new() { Id = ImportBuilder.Id(), Cells = cells });
            }
            return new() { Id = ImportBuilder.Id(), Type = ManuscriptBlockType.Table, StyleRole = ManuscriptStyleRoles.Table,
                Table = new() { Id = ImportBuilder.Id(), ColumnWidthWeights = Enumerable.Range(0, columns)
                    .Select(column => (int)Math.Clamp(Math.Round(widths.GetValueOrDefault(column, 72) * 20), 1, 100000)).ToList(), HeaderRowCount = headerCount, Rows = rows } };
        }

        private ManuscriptStyleProperties Definition(Dictionary<string, string> css, bool character)
        {
            var font = css.GetValueOrDefault("font-family");
            string? key = null;
            if (font is not null)
            {
                _builder.Warn($"Font '{font}' uses a bundled fallback; clipboard font files are not imported.");
                key = font.Contains("mono", StringComparison.OrdinalIgnoreCase) || font.Contains("Courier", StringComparison.OrdinalIgnoreCase) ? "mono"
                    : font.Contains("sans", StringComparison.OrdinalIgnoreCase) || font.Contains("Arial", StringComparison.OrdinalIgnoreCase) || font.Contains("Calibri", StringComparison.OrdinalIgnoreCase) ? "sans" : "serif";
            }
            var size = Points(css.GetValueOrDefault("font-size"));
            var em = size ?? 12;
            var line = css.GetValueOrDefault("line-height");
            double? lineHeight = double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var multiplier) ? multiplier
                : line?.EndsWith('%') == true && double.TryParse(line[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) ? percent / 100
                : Points(line) is double linePoints ? linePoints / em : null;
            double? Indent(string property) => Points(css.GetValueOrDefault(property)) is double points ? Math.Clamp(points / em, -12, 12) : null;
            return new(FontFamilyKey: key, FontSizePoints: size is double present ? Math.Clamp(present, 1, 288) : null,
                FontWeight: css.GetValueOrDefault("font-weight") switch { "bold" => 700, "normal" => 400, var value => Integer(value) is int weight ? Math.Clamp((weight / 100) * 100, 100, 900) : null },
                Italic: css.GetValueOrDefault("font-style") switch { "italic" => true, "normal" => false, _ => null },
                SmallCaps: css.GetValueOrDefault("font-variant") == "small-caps" ? true : null,
                LineHeight: character || lineHeight is not > 0 ? null : Math.Clamp(lineHeight.Value, .1, 5),
                SpaceBeforePoints: character ? null : BoundedPoints(css.GetValueOrDefault("margin-top")),
                SpaceAfterPoints: character ? null : BoundedPoints(css.GetValueOrDefault("margin-bottom")),
                KeepWithNext: character ? null : css.GetValueOrDefault("page-break-after") == "avoid" ? true : null,
                TextAlign: character ? null : css.GetValueOrDefault("text-align") is "left" or "right" or "center" or "justify" ? css["text-align"] : null,
                LeftIndentEm: character ? null : Indent("margin-left") is double left ? Math.Max(0, left) : null,
                RightIndentEm: character ? null : Indent("margin-right") is double right ? Math.Max(0, right) : null,
                FirstLineIndentEm: character ? null : Indent("text-indent"),
                StartOnNewPage: character ? null : css.GetValueOrDefault("page-break-before") == "always" ? true : null);
        }
        private static int? Integer(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
        private static double? BoundedPoints(string? value) => Points(value) is double points ? Math.Clamp(points, 0, 288) : null;
        private static double? Points(string? value)
        {
            if (value is null) return null;
            var match = Regex.Match(value, @"^(-?\d+(?:\.\d+)?)(pt|px|in|cm|mm)?$", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);
            if (!match.Success) return null;
            var number = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            return number * (match.Groups[2].Value.ToLowerInvariant() switch { "px" => .75, "in" => 72, "cm" => 72 / 2.54, "mm" => 72 / 25.4, _ => 1 });
        }
    }
}
