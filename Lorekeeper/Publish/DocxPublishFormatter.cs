using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using System.Security.Cryptography;
using System.Text;
using Lorekeeper.Citations;
using Lorekeeper.Composition;
using Lorekeeper.Fonts;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Lorekeeper.Publish;

/// <summary>
/// Produces an editable semantic DOCX. Lorekeeper intentionally reports this
/// as a structural interchange format rather than a Word-pagination promise.
/// </summary>
public sealed class DocxPublishFormatter(ICompositionCanvasPreviewService previews) : IPublishExportFormatter
{
    public PublishExportFormat Format => PublishExportFormat.Docx;
    public string FileExtension => ".docx";
    public string ContentType => "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public byte[] Render(PublishDocument document) =>
        throw new NotSupportedException("DOCX export requires the asynchronous artwork renderer.");

    public async Task<byte[]> RenderAsync(PublishDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var artwork = await RenderDesignedPagesAsync(document, cancellationToken);
        return await Task.Run(() => RenderPackage(document, artwork, cancellationToken), cancellationToken);
    }

    private static byte[] RenderPackage(PublishDocument document,
        IReadOnlyDictionary<(string Document, string Placement), DesignedPageArtwork> artwork,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new MemoryStream();
        using (var package = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, autoSave: true))
        {
            var main = package.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body());
            AddStyles(main, document);
            AddNumbering(main);
            var context = new BuildContext(main, document, artwork, cancellationToken);
            context.AppendPublication();
            main.Document.Save();
            ValidateRelationships(main);

            var errors = new OpenXmlValidator().Validate(package).Take(12).ToList();
            if (errors.Count > 0)
            {
                throw new InvalidDataException(
                    "Generated DOCX validation failed: "
                    + string.Join("; ", errors.Select(error => error.Description)));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return stream.ToArray();
    }

    private static void ValidateRelationships(MainDocumentPart main)
    {
        var parts = new List<(OpenXmlPart Part, OpenXmlElement Root)> { (main, main.Document!) };
        if (main.FootnotesPart?.Footnotes is { } footnotes) parts.Add((main.FootnotesPart, footnotes));
        var bookmarks = parts.SelectMany(part => part.Root.Descendants<W.BookmarkStart>()).ToList();
        var names = bookmarks.Select(bookmark => bookmark.Name!.Value!).ToHashSet(StringComparer.Ordinal);
        if (names.Count != bookmarks.Count || bookmarks.Select(bookmark => bookmark.Id!.Value).Distinct().Count() != bookmarks.Count)
            throw new InvalidDataException("Generated DOCX contains duplicate bookmark identities.");
        foreach (var (part, root) in parts)
        {
            foreach (var image in root.Descendants<A.Blip>())
                if (image.Embed?.Value is not { } id || !part.TryGetPartById(id, out var target) || target is not ImagePart)
                    throw new InvalidDataException("Generated DOCX contains an unresolved image relationship.");
            foreach (var link in root.Descendants<W.Hyperlink>())
            {
                if (link.Id?.Value is { } id && !part.HyperlinkRelationships.Any(relationship => relationship.Id == id))
                    throw new InvalidDataException("Generated DOCX contains an unresolved hyperlink relationship.");
                if (link.Anchor?.Value is { } anchor && !names.Contains(anchor))
                    throw new InvalidDataException("Generated DOCX contains an unresolved internal reference.");
            }
        }
        var noteIds = main.FootnotesPart?.Footnotes?.Elements<W.Footnote>().Select(note => note.Id!.Value).ToHashSet() ?? [];
        if (main.Document!.Descendants<W.FootnoteReference>().Any(reference => !noteIds.Contains(reference.Id!.Value)))
            throw new InvalidDataException("Generated DOCX contains an unresolved footnote reference.");
    }

    private async Task<IReadOnlyDictionary<(string Document, string Placement), DesignedPageArtwork>> RenderDesignedPagesAsync(
        PublishDocument document,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<(string, string), DesignedPageArtwork>();
        var citations = new PublicationCitationResolver(document.Citations.Occurrences);
        foreach (var container in PublicationSemanticDocuments.Enumerate(document))
        {
            var notes = PublicationNotes.Create(document, container);
            foreach (var placement in container.Manuscript.Content.Where(block => block.Type == ManuscriptBlockType.DesignedPage))
            {
                var page = container.DesignedPages.Single(page => page.Id == placement.DesignedPageId);
                cancellationToken.ThrowIfCancellationRequested();
                var variant = page.Variants.FirstOrDefault()
                    ?? throw new InvalidOperationException($"Designed Page '{page.Name}' has no active layout variant for DOCX export.");
                // Resolve ranges before substituting display text: atoms occupy no stored
                // text offsets, and a formatted citation must not shift frame boundaries.
                var semantic = notes.Placements[placement.Id];
                var blocks = new List<ManuscriptBlock>();
                var objects = variant.Scene.Objects.Select(item =>
                {
                    if (item.ContentReferences.Count == 0) return item;
                    var bound = ManuscriptRangeResolver.ResolveBlocks(semantic, item.ContentReferences);
                    var references = new List<ManuscriptRangeReference>();
                    foreach (var block in bound)
                    {
                        var id = $"artwork-{item.Id:N}-{blocks.Count}";
                        var inlines = block.Content.SelectMany(inline =>
                        {
                            if (inline.Type == ManuscriptInlineType.NoteReference)
                                return new[] { new ManuscriptInline { Text = $"[{notes.Numbers[inline.NoteId!]}]" } };
                            if (inline.Type != ManuscriptInlineType.Citation) return [inline];
                            var citation = citations.Resolve(inline.Id!, container.Id, [$"placement:{placement.Id}"]);
                            return citation.InlineRuns.Select(run => new ManuscriptInline
                            {
                                Text = run.Text,
                                Marks = run.Italic ? [new() { Type = ManuscriptMarkType.Emphasis }] : [],
                            });
                        }).ToList();
                        blocks.Add(block with { Id = id, Content = inlines });
                        references.Add(new(id));
                    }
                    return item with { ContentReferences = references };
                }).ToList();
                var rendered = await previews.RenderSceneAtResolutionAsync(
                    document.ProjectId,
                    page.Id,
                    page.Revision,
                    variant.Scene with { Objects = objects },
                    semantic with { Content = blocks, Notes = [] },
                    CompositionCanvasPreviewMode.Clean,
                    1_600,
                    cancellationToken);
                result[(container.Id, placement.Id)] = new(
                    rendered.Data,
                    rendered.PixelWidth,
                    rendered.PixelHeight,
                    string.IsNullOrWhiteSpace(page.AccessibilityDescription)
                        ? page.Name
                        : page.AccessibilityDescription.Trim());
            }
        }
        return result;
    }

    private static void AddStyles(MainDocumentPart main, PublishDocument document)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        part.Styles = new W.Styles(
            Style("Normal", "Normal", true),
            Style("Title", "Title"),
            Style("Heading1", "heading 1"),
            Style("Heading2", "heading 2"),
            Style("Heading3", "heading 3"),
            Style("Heading4", "heading 4"),
            Style("Heading5", "heading 5"),
            Style("Heading6", "heading 6"),
            Style("Quote", "Quote"),
            Style("Bibliography", "Bibliography"));
        var normal = part.Styles.Elements<W.Style>().Single(style => style.StyleId == "Normal");
        var normalRuns = TypographyProperties(document, "serif", document.Profile.BodyFontSizePoints, null, null, null);
        normal.StyleRunProperties = new W.StyleRunProperties(normalRuns.ChildElements.Select(element => element.CloneNode(true)));
        normal.StyleParagraphProperties = new W.StyleParagraphProperties(new W.SpacingBetweenLines
        {
            Line = Math.Round(document.Profile.BodyLineHeight * 240).ToString(System.Globalization.CultureInfo.InvariantCulture),
            LineRule = W.LineSpacingRuleValues.Auto,
        });
        foreach (var named in document.NamedStyles)
        {
            var definition = named.Definition;
            var runs = TypographyProperties(document, definition.FontFamilyKey, definition.FontSizePoints,
                definition.FontWeight, definition.Italic, definition.SmallCaps);
            var style = new W.Style
            {
                Type = named.Kind == ManuscriptStyleKind.Character ? W.StyleValues.Character : W.StyleValues.Paragraph,
                StyleId = NamedStyleId(named.Kind, named.SemanticRole),
                CustomStyle = true,
                StyleName = new W.StyleName { Val = named.Name },
                StyleRunProperties = new W.StyleRunProperties(runs.ChildElements.Select(element => element.CloneNode(true))),
            };
            if (named.Kind == ManuscriptStyleKind.Paragraph)
            {
                style.BasedOn = new W.BasedOn { Val = "Normal" };
                var paragraph = PresentationProperties(new()
                {
                    FontSizePoints = definition.FontSizePoints,
                    Alignment = definition.TextAlign?.ToLowerInvariant() switch
                    {
                        "center" => ParagraphAlignment.Center,
                        "right" or "end" => ParagraphAlignment.End,
                        "justify" => ParagraphAlignment.Justify,
                        "left" or "start" => ParagraphAlignment.Start,
                        _ => null,
                    },
                    LineHeight = definition.LineHeight,
                    LeftIndentEm = definition.LeftIndentEm,
                    RightIndentEm = definition.RightIndentEm,
                    FirstLineIndentEm = definition.FirstLineIndentEm,
                    SpacingBeforePoints = definition.SpaceBeforePoints,
                    SpacingAfterPoints = definition.SpaceAfterPoints,
                    KeepWithNext = definition.KeepWithNext,
                    StartOnNewPage = definition.StartOnNewPage,
                }, document.Profile.BodyFontSizePoints);
                style.StyleParagraphProperties = new W.StyleParagraphProperties(paragraph.ChildElements.Select(element => element.CloneNode(true)));
            }
            part.Styles.Append(style);
        }
        part.Styles.Save();

        static W.Style Style(string id, string name, bool isDefault = false) => new()
        {
            Type = W.StyleValues.Paragraph,
            StyleId = id,
            Default = isDefault,
            CustomStyle = id == "Bibliography",
            StyleName = new W.StyleName { Val = name },
            StyleParagraphProperties = id.StartsWith("Heading", StringComparison.Ordinal)
                ? new W.StyleParagraphProperties(new W.OutlineLevel
                {
                    Val = int.Parse(id.AsSpan(7), System.Globalization.CultureInfo.InvariantCulture) - 1,
                })
                : null,
        };
    }

    private static string NamedStyleId(ManuscriptStyleKind kind, string role) => "LK" + kind + "_"
        + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role.ToLowerInvariant())))[..20];

    private static W.RunProperties TypographyProperties(PublishDocument document, string? familyKey, double? size, int? weight, bool? italic, bool? smallCaps)
    {
        var properties = new W.RunProperties();
        if (familyKey is not null)
        {
            var family = document.Fonts.FirstOrDefault(font => string.Equals(font.FamilyKey, familyKey, StringComparison.OrdinalIgnoreCase))?.FamilyName
                ?? PublicationBuiltInFonts.Find(familyKey)?.Name ?? familyKey switch
                {
                    "serif" => "Georgia",
                    "sans" => "Arial",
                    "mono" => "Consolas",
                    _ => familyKey,
                };
            properties.RunFonts = new W.RunFonts { Ascii = family, HighAnsi = family, ComplexScript = family, EastAsia = family };
        }
        if (size is double points) properties.FontSize = new W.FontSize { Val = Math.Round(points * 2).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (weight is int fontWeight) properties.Bold = new W.Bold { Val = fontWeight >= 600 };
        if (italic is bool isItalic) properties.Italic = new W.Italic { Val = isItalic };
        if (smallCaps is bool isSmallCaps) properties.SmallCaps = new W.SmallCaps { Val = isSmallCaps };
        return properties;
    }

    private static W.ParagraphProperties PresentationProperties(ParagraphPresentation presentation, double defaultSize)
    {
        var properties = new W.ParagraphProperties();
        if (presentation.Alignment is { } alignment)
            properties.Justification = new W.Justification
            {
                Val = alignment switch
                {
                    ParagraphAlignment.Center => W.JustificationValues.Center,
                    ParagraphAlignment.End => W.JustificationValues.Right,
                    ParagraphAlignment.Justify => W.JustificationValues.Both,
                    _ => W.JustificationValues.Left,
                }
            };
        var size = presentation.FontSizePoints ?? defaultSize;
        static string Twips(double points) => Math.Round(points * 20).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var firstLine = presentation.FirstLineIndentEm * size;
        if (presentation.LeftIndentEm is not null || presentation.RightIndentEm is not null || firstLine is not null)
        {
            var indentation = new W.Indentation();
            if (presentation.LeftIndentEm is double left) indentation.Left = Twips(left * size);
            if (presentation.RightIndentEm is double right) indentation.Right = Twips(right * size);
            if (firstLine is double first)
            {
                if (first >= 0) indentation.FirstLine = Twips(first);
                else indentation.Hanging = Twips(-first);
            }
            properties.Indentation = indentation;
        }
        if (presentation.SpacingBeforePoints is not null || presentation.SpacingAfterPoints is not null || presentation.LineHeight is not null)
        {
            var spacing = new W.SpacingBetweenLines();
            if (presentation.SpacingBeforePoints is double before) spacing.Before = Twips(before);
            if (presentation.SpacingAfterPoints is double after) spacing.After = Twips(after);
            if (presentation.LineHeight is double lineHeight)
            {
                spacing.Line = Math.Round(lineHeight * 240).ToString(System.Globalization.CultureInfo.InvariantCulture);
                spacing.LineRule = W.LineSpacingRuleValues.Auto;
            }
            properties.SpacingBetweenLines = spacing;
        }
        if (presentation.KeepWithNext is bool keep) properties.KeepNext = new W.KeepNext { Val = keep };
        if (presentation.StartOnNewPage is bool pageBreak) properties.PageBreakBefore = new W.PageBreakBefore { Val = pageBreak };
        return properties;
    }

    private static void AddNumbering(MainDocumentPart main)
    {
        var part = main.AddNewPart<NumberingDefinitionsPart>();
        part.Numbering = new W.Numbering(
            new W.AbstractNum(
                new W.Level(
                    new W.NumberingFormat { Val = W.NumberFormatValues.Bullet },
                    new W.LevelText { Val = "•" },
                    new W.LevelJustification { Val = W.LevelJustificationValues.Left },
                    new W.PreviousParagraphProperties(new W.Indentation { Left = "720", Hanging = "360" }))
                { LevelIndex = 0 })
            { AbstractNumberId = 1 },
            new W.NumberingInstance(new W.AbstractNumId { Val = 1 }) { NumberID = 1 });
        part.Numbering.Save();
    }

    private sealed class BuildContext(
        MainDocumentPart main,
        PublishDocument document,
        IReadOnlyDictionary<(string Document, string Placement), DesignedPageArtwork> artwork,
        CancellationToken cancellationToken)
    {
        private readonly W.Body _body = main.Document?.Body
            ?? throw new InvalidOperationException("DOCX body was not initialized.");
        private readonly PublicationCitationResolver _citations = new(document.Citations.Occurrences);
        private readonly List<PendingEndnote> _endnotes = [];
        private readonly Dictionary<string, string> _documentTitles = new(StringComparer.Ordinal);
        private int _nextFootnoteId = 1;
        private int _nextBookmarkId;
        private uint _nextDrawingId = 1;
        private readonly Dictionary<(string Scope, string List, bool Ordered), ListNumbering> _lists = [];
        private int _nextNumberingId = 2;
        private OpenXmlPartContainer _relationshipOwner = main;

        private sealed record ListNumbering(int NumberId, W.AbstractNum Definition, Dictionary<int, int> Counters);

        private W.NumberingProperties ListProperties(ManuscriptBlock block, NoteContext notes)
        {
            var list = block.List;
            if (list is null)
                return new(new W.NumberingLevelReference { Val = 0 }, new W.NumberingId { Val = 1 });
            var key = (notes.OccurrencePath, list.Id, list.Ordered);
            var numbering = main.NumberingDefinitionsPart!.Numbering!;
            if (!_lists.TryGetValue(key, out var state))
            {
                var abstractId = _nextNumberingId++;
                var definition = new W.AbstractNum { AbstractNumberId = abstractId };
                foreach (var level in Enumerable.Range(0, 9))
                    definition.Append(new W.Level(
                        new W.StartNumberingValue { Val = 1 },
                        new W.NumberingFormat { Val = W.NumberFormatValues.Bullet },
                        new W.LevelText { Val = "•" },
                        new W.PreviousParagraphProperties(new W.Indentation { Left = ((level + 1) * 720).ToString(), Hanging = "360" }))
                    { LevelIndex = level });
                numbering.InsertBefore(definition, numbering.Elements<W.NumberingInstance>().First());
                state = new(abstractId, definition, []);
                numbering.Append(new W.NumberingInstance(new W.AbstractNumId { Val = abstractId }) { NumberID = abstractId });
                _lists.Add(key, state);
            }
            var selectedLevel = state.Definition.Elements<W.Level>().Single(level => level.LevelIndex!.Value == list.Level);
            selectedLevel.NumberingFormat = new W.NumberingFormat { Val = list.Ordered ? W.NumberFormatValues.Decimal : W.NumberFormatValues.Bullet };
            selectedLevel.LevelText = new W.LevelText { Val = list.Ordered ? $"%{list.Level + 1}." : "•" };
            foreach (var nested in state.Counters.Keys.Where(level => level > list.Level).ToList()) state.Counters.Remove(nested);
            if (list.Ordered)
            {
                var number = list.Start ?? 1;
                if (number != state.Counters.GetValueOrDefault(list.Level) + 1)
                {
                    var instanceId = _nextNumberingId++;
                    numbering.Append(new W.NumberingInstance(
                        new W.AbstractNumId { Val = state.Definition.AbstractNumberId!.Value },
                        new W.LevelOverride(new W.StartOverrideNumberingValue { Val = number }) { LevelIndex = list.Level })
                    { NumberID = instanceId });
                    state = state with { NumberId = instanceId };
                    _lists[key] = state;
                }
                state.Counters[list.Level] = number;
            }
            return new(new W.NumberingLevelReference { Val = list.Level }, new W.NumberingId { Val = state.NumberId });
        }

        public void AppendPublication()
        {
            if (document.Profile.IncludeTitlePage)
            {
                AppendHeading(document.DisplayTitle, 0);
                if (!string.IsNullOrWhiteSpace(document.Profile.Subtitle))
                    AppendParagraph(document.Profile.Subtitle, "Subtitle");
                if (!string.IsNullOrWhiteSpace(document.Profile.Author))
                    AppendParagraph(document.Profile.Author, "Author");
                AppendPageBreak();
            }

            AppendPublicationSections(PublicationSectionAnchor.Front, null, null);
            foreach (var section in document.Sections)
            {
                AppendPublicationSections(PublicationSectionAnchor.BeforeAct, PublishOutlineTargetKind.Act, section.ActId);
                if (section.IncludePage && section.IncludeHeading)
                    AppendHeading(section.Title, 1);
                if (document.Profile.IncludeActSynopses && !string.IsNullOrWhiteSpace(section.Synopsis))
                    AppendParagraph(section.Synopsis, "Quote");

                foreach (var chapter in section.Chapters)
                {
                    AppendPublicationSections(PublicationSectionAnchor.BeforeChapter, PublishOutlineTargetKind.Chapter, chapter.Id);
                    if (chapter.IncludeHeading)
                        AppendHeading(chapter.Title, 1);
                    if (document.Profile.IncludeChapterSynopses && !string.IsNullOrWhiteSpace(chapter.Synopsis))
                        AppendParagraph(chapter.Synopsis, "Quote");
                    AppendManuscript($"chapter:{chapter.Id:D}", chapter.Title, chapter.Manuscript, chapter.DesignedPages);
                    AppendSectionBreak();
                    AppendPublicationSections(PublicationSectionAnchor.AfterChapter, PublishOutlineTargetKind.Chapter, chapter.Id);
                }
                AppendPublicationSections(PublicationSectionAnchor.AfterAct, PublishOutlineTargetKind.Act, section.ActId);
            }
            AppendPublicationSections(PublicationSectionAnchor.Back, null, null);
            AppendEndnotes();
            AppendBibliography();
            _body.Append(SectionProperties());
        }

        private void AppendPublicationSections(
            PublicationSectionAnchor anchor,
            PublishOutlineTargetKind? targetKind,
            Guid? targetId)
        {
            foreach (var section in document.PublicationSections
                .Where(item => item.Anchor == anchor && item.TargetKind == targetKind && item.TargetId == targetId)
                .OrderBy(item => item.LocalOrder))
            {
                AppendHeading(section.Title, 1);
                if (section.SystemRole == PublicationSectionSystemRole.Contents)
                {
                    foreach (var act in document.Sections)
                    {
                        if (act.IncludeHeading) AppendParagraph(act.Title);
                        foreach (var chapter in act.Chapters.Where(chapter => chapter.IncludeHeading))
                            AppendParagraph(chapter.Title, indentationTwips: 360);
                    }
                }
                else
                {
                    AppendManuscript($"publication-section:{section.Id:D}", section.Title, section.Manuscript, section.DesignedPages);
                }
                AppendSectionBreak();
            }
        }

        private void AppendManuscript(
            string topLevelId,
            string topLevelTitle,
            ManuscriptDocument manuscript,
            IReadOnlyList<PublishDesignedPageDocument> designedPages)
        {
            _documentTitles.Add(topLevelId, topLevelTitle);
            var projected = PublicationNotes.Create(document, new(topLevelId, topLevelTitle, manuscript, designedPages));
            manuscript = projected.Manuscript;
            var footnoteIds = manuscript.Notes
                .Where(note => note.Kind == ManuscriptNoteKind.Footnote)
                .ToDictionary(note => note.Id, _ => _nextFootnoteId++, StringComparer.Ordinal);
            var endnoteNumbers = manuscript.Notes.Where(note => note.Kind == ManuscriptNoteKind.Endnote)
                .ToDictionary(note => note.Id, note => projected.Numbers[note.Id], StringComparer.Ordinal);
            var notes = new NoteContext(topLevelTitle, topLevelId, topLevelId, manuscript, footnoteIds, endnoteNumbers);
            foreach (var block in manuscript.Content)
            {
                if (block.Type == ManuscriptBlockType.DesignedPage
                    && block.DesignedPageId is Guid pageId
                    && designedPages.SingleOrDefault(page => page.Id == pageId) is { } page)
                {
                    var placed = projected.Placements[block.Id];
                    var numbers = placed.Notes.Where(note => note.Kind == ManuscriptNoteKind.Endnote)
                        .ToDictionary(note => note.Id, note => projected.Numbers[note.Id], StringComparer.Ordinal);
                    AppendDesignedPage(page with { SemanticManuscript = placed }, block.Id, numbers, notes);
                }
                else
                {
                    AppendBlock(_body, block, notes);
                }
            }

            foreach (var note in manuscript.Notes.Where(note => note.Kind == ManuscriptNoteKind.Footnote))
                AddFootnote(note, notes);
            foreach (var note in manuscript.Notes.Where(note => note.Kind == ManuscriptNoteKind.Endnote))
                _endnotes.Add(new(topLevelTitle, endnoteNumbers[note.Id], note, notes));
        }

        private void AppendDesignedPage(PublishDesignedPageDocument page, string placementId,
            IReadOnlyDictionary<string, int> pageEndnoteNumbers, NoteContext containingNotes)
        {
            if (!artwork.TryGetValue((containingNotes.TopLevelId, placementId), out var image))
                throw new InvalidOperationException($"Designed Page '{page.Name}' artwork is unavailable.");
            var paragraph = new W.Paragraph(
                new W.ParagraphProperties
                {
                    Justification = new W.Justification { Val = W.JustificationValues.Center },
                    PageBreakBefore = new W.PageBreakBefore(),
                },
                new W.Run(ImageDrawing(image.Data, image.Width, image.Height, page.Name, image.Description)));
            _body.Append(paragraph);

            // The artwork is authoritative visually. Consuming semantic notes
            // and citations here preserves occurrence-specific references and
            // keeps the DOCX accessible to non-visual readers.
            var pageFootnoteIds = page.SemanticManuscript.Notes
                .Where(note => note.Kind == ManuscriptNoteKind.Footnote)
                .ToDictionary(note => note.Id, _ => _nextFootnoteId++, StringComparer.Ordinal);
            var pageNotes = new NoteContext(containingNotes.TopLevelTitle, containingNotes.TopLevelId,
                $"{containingNotes.OccurrencePath}/placement:{placementId}", page.SemanticManuscript, pageFootnoteIds, pageEndnoteNumbers)
            { PlacementPath = [.. containingNotes.PlacementPath, $"placement:{placementId}"] };
            var supplement = new W.Paragraph();
            foreach (var occurrence in ManuscriptTraversal.EnumerateCitations(page.SemanticManuscript with { Notes = [] }))
                AppendCitation(supplement, occurrence.CitationAtomId, pageNotes);
            foreach (var note in page.SemanticManuscript.Notes)
            {
                var inline = new ManuscriptInline
                {
                    Id = $"{page.Id:D}:note:{note.Id}",
                    Type = ManuscriptInlineType.NoteReference,
                    NoteId = note.Id,
                };
                AppendInline(supplement, inline, pageNotes);
            }
            if (supplement.ChildElements.Count > 0)
                _body.Append(supplement);
            foreach (var note in page.SemanticManuscript.Notes.Where(note => note.Kind == ManuscriptNoteKind.Footnote))
                AddFootnote(note, pageNotes);
            foreach (var note in page.SemanticManuscript.Notes.Where(note => note.Kind == ManuscriptNoteKind.Endnote))
                _endnotes.Add(new(containingNotes.TopLevelTitle, pageEndnoteNumbers[note.Id], note, pageNotes));
        }

        private void AppendBlock(OpenXmlCompositeElement parent, ManuscriptBlock block, NoteContext notes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (block.Type == ManuscriptBlockType.Table)
            {
                parent.Append(Table(block.Table!, notes));
                return;
            }
            if (block.Type == ManuscriptBlockType.Figure)
            {
                AppendFigure(parent, block, notes);
                return;
            }
            if (block.Type == ManuscriptBlockType.SceneBreak)
            {
                parent.Append(new W.Paragraph(
                    new W.ParagraphProperties(new W.Justification { Val = W.JustificationValues.Center }),
                    new W.Run(new W.Text("* * *"))));
                return;
            }

            var properties = ParagraphProperties(block, notes);
            var paragraph = new W.Paragraph(properties);
            foreach (var inline in block.Content)
                AppendInline(paragraph, inline, notes);
            if (block.ParagraphPresentation is { } direct)
            {
                var typography = TypographyProperties(document, direct.FontFamilyKey, direct.FontSizePoints,
                    direct.FontWeight, direct.Italic, direct.SmallCaps);
                foreach (var run in paragraph.Descendants<W.Run>())
                {
                    var runProperties = run.RunProperties ??= new W.RunProperties();
                    runProperties.RunFonts ??= (W.RunFonts?)typography.RunFonts?.CloneNode(true);
                    runProperties.FontSize ??= (W.FontSize?)typography.FontSize?.CloneNode(true);
                    runProperties.Bold ??= (W.Bold?)typography.Bold?.CloneNode(true);
                    runProperties.Italic ??= (W.Italic?)typography.Italic?.CloneNode(true);
                    runProperties.SmallCaps ??= (W.SmallCaps?)typography.SmallCaps?.CloneNode(true);
                }
            }
            parent.Append(paragraph);
        }

        private W.ParagraphProperties ParagraphProperties(ManuscriptBlock block, NoteContext notes)
        {
            var properties = block.ParagraphPresentation is { } presentation
                ? PresentationProperties(presentation, document.NamedStyles.FirstOrDefault(style => style.Kind == ManuscriptStyleKind.Paragraph
                    && string.Equals(style.SemanticRole, block.StyleRole, StringComparison.OrdinalIgnoreCase))?.Definition.FontSizePoints ?? document.Profile.BodyFontSizePoints)
                : new W.ParagraphProperties();
            if (document.NamedStyles.Any(style => style.Kind == ManuscriptStyleKind.Paragraph && string.Equals(style.SemanticRole, block.StyleRole, StringComparison.OrdinalIgnoreCase)))
                properties.ParagraphStyleId = new W.ParagraphStyleId { Val = NamedStyleId(ManuscriptStyleKind.Paragraph, block.StyleRole) };
            else if (block.Type == ManuscriptBlockType.Heading)
                properties.ParagraphStyleId = new W.ParagraphStyleId { Val = $"Heading{Math.Clamp(block.HeadingLevel ?? 2, 1, 6)}" };
            else if (block.Type == ManuscriptBlockType.BlockQuote)
                properties.ParagraphStyleId = new W.ParagraphStyleId { Val = "Quote" };
            if (block.Type == ManuscriptBlockType.Heading)
                properties.OutlineLevel = new W.OutlineLevel { Val = Math.Clamp(block.HeadingLevel ?? 2, 1, 6) - 1 };
            if (block.Type == ManuscriptBlockType.ListItem)
                properties.NumberingProperties = ListProperties(block, notes);
            return properties;
        }

        private void AppendInline(W.Paragraph paragraph, ManuscriptInline inline, NoteContext notes)
        {
            if (inline.Type == ManuscriptInlineType.NoteReference)
            {
                if (notes.FootnoteIds.TryGetValue(inline.NoteId!, out var footnoteId))
                {
                    paragraph.Append(new W.Run(
                        new W.RunProperties(new W.VerticalTextAlignment { Val = W.VerticalPositionValues.Superscript }),
                        new W.FootnoteReference { Id = footnoteId }));
                }
                else if (notes.EndnoteNumbers.TryGetValue(inline.NoteId!, out var endnoteNumber))
                {
                    var anchor = EndnoteAnchor(notes.OccurrencePath, inline.NoteId!);
                    paragraph.Append(new W.Hyperlink(
                        new W.Run(
                            new W.RunProperties(new W.VerticalTextAlignment { Val = W.VerticalPositionValues.Superscript }),
                            new W.Text(endnoteNumber.ToString())))
                    { Anchor = anchor });
                }
                return;
            }
            if (inline.Type == ManuscriptInlineType.Citation)
            {
                AppendCitation(paragraph, inline.Id!, notes);
                return;
            }

            OpenXmlElement content = new W.Run(RunProperties(inline), new W.Text(inline.Text) { Space = SpaceProcessingModeValues.Preserve });
            var link = inline.Marks.FirstOrDefault(mark => mark.Type == ManuscriptMarkType.Link)?.Value;
            if (!string.IsNullOrWhiteSpace(link)
                && Uri.TryCreate(link, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https" or "mailto")
            {
                var relationship = _relationshipOwner.AddHyperlinkRelationship(uri, true);
                content = new W.Hyperlink(content) { Id = relationship.Id, History = OnOffValue.FromBoolean(true) };
            }
            paragraph.Append(content);
        }

        private void AppendCitation(W.Paragraph paragraph, string atomId, NoteContext notes)
        {
            var citation = _citations.Resolve(atomId, notes.TopLevelId, notes.PlacementPath);
            if (citation.NoteText is null)
            {
                AppendCitationRuns(paragraph, citation.InlineRuns);
                return;
            }

            var anchor = PublicationCitationResolver.Anchor(citation);
            var referenceId = NextBookmarkId();
            paragraph.Append(new W.BookmarkStart { Name = "citation_ref_" + anchor, Id = referenceId });
            paragraph.Append(new W.Hyperlink(new W.Run(
                new W.RunProperties(new W.VerticalTextAlignment { Val = W.VerticalPositionValues.Superscript }),
                new W.Text(citation.InlineText)))
            { Anchor = "citation_" + anchor });
            paragraph.Append(new W.BookmarkEnd { Id = referenceId });
        }

        private static void AppendCitationRuns(W.Paragraph paragraph, IReadOnlyList<CitationRun> runs)
        {
            foreach (var run in runs)
                paragraph.Append(new W.Run(
                    new W.RunProperties(new W.Italic { Val = run.Italic }),
                    new W.Text(run.Text) { Space = SpaceProcessingModeValues.Preserve }));
        }

        private W.RunProperties RunProperties(ManuscriptInline inline)
        {
            var properties = new W.RunProperties();
            foreach (var mark in inline.Marks)
            {
                switch (mark.Type)
                {
                    case ManuscriptMarkType.Strong: properties.Bold = new W.Bold(); break;
                    case ManuscriptMarkType.Emphasis: properties.Italic = new W.Italic(); break;
                    case ManuscriptMarkType.Underline: properties.Underline = new W.Underline { Val = W.UnderlineValues.Single }; break;
                    case ManuscriptMarkType.Strikethrough: properties.Strike = new W.Strike(); break;
                    case ManuscriptMarkType.Code: properties.RunFonts = new W.RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" }; break;
                    case ManuscriptMarkType.SmallCaps: properties.SmallCaps = new W.SmallCaps(); break;
                    case ManuscriptMarkType.Superscript: properties.VerticalTextAlignment = new W.VerticalTextAlignment { Val = W.VerticalPositionValues.Superscript }; break;
                    case ManuscriptMarkType.Subscript: properties.VerticalTextAlignment = new W.VerticalTextAlignment { Val = W.VerticalPositionValues.Subscript }; break;
                    case ManuscriptMarkType.Language: properties.Languages = new W.Languages { Val = mark.Value }; break;
                    case ManuscriptMarkType.CharacterStyle:
                        if (!document.NamedStyles.Any(style => style.Kind == ManuscriptStyleKind.Character && string.Equals(style.SemanticRole, mark.Value, StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidDataException($"Character style '{mark.Value}' is missing from the effective publication.");
                        properties.RunStyle = new W.RunStyle { Val = NamedStyleId(ManuscriptStyleKind.Character, mark.Value!) };
                        break;
                }
            }
            return properties;
        }

        private W.Table Table(ManuscriptTable table, NoteContext notes)
        {
            var result = new W.Table(new W.TableProperties(
                new W.TableWidth { Width = "5000", Type = W.TableWidthUnitValues.Pct },
                new W.TableBorders(
                    new W.TopBorder { Val = W.BorderValues.Single, Size = 4 },
                    new W.LeftBorder { Val = W.BorderValues.Single, Size = 4 },
                    new W.BottomBorder { Val = W.BorderValues.Single, Size = 4 },
                    new W.RightBorder { Val = W.BorderValues.Single, Size = 4 },
                    new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4 },
                    new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4 })));
            result.Append(new W.TableGrid(table.ColumnWidthWeights.Select(weight =>
                new W.GridColumn { Width = Math.Max(1, weight * 1_000).ToString() })));
            var verticalSpans = new Dictionary<int, VerticalSpan>();
            for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rowProperties = new W.TableRowProperties(new W.CantSplit());
                if (rowIndex < table.HeaderRowCount) rowProperties.Append(new W.TableHeader());
                var row = new W.TableRow(rowProperties);
                var nextVerticalSpans = new Dictionary<int, VerticalSpan>();
                var sourceCellIndex = 0;
                var column = 0;
                while (column < table.ColumnWidthWeights.Count)
                {
                    if (verticalSpans.TryGetValue(column, out var continuation))
                    {
                        var continuationProperties = new W.TableCellProperties(
                            new W.VerticalMerge { Val = W.MergedCellValues.Continue });
                        if (continuation.ColumnSpan > 1)
                            continuationProperties.Append(new W.GridSpan { Val = continuation.ColumnSpan });
                        row.Append(new W.TableCell(continuationProperties, new W.Paragraph()));
                        if (continuation.RemainingRows > 1)
                            nextVerticalSpans[column] = continuation with { RemainingRows = continuation.RemainingRows - 1 };
                        column += continuation.ColumnSpan;
                        continue;
                    }

                    if (sourceCellIndex >= table.Rows[rowIndex].Cells.Count)
                        throw new InvalidDataException($"Table '{table.Id}' row '{table.Rows[rowIndex].Id}' does not cover the declared column grid.");
                    var cell = table.Rows[rowIndex].Cells[sourceCellIndex++];
                    var properties = new W.TableCellProperties();
                    if (cell.ColumnSpan > 1) properties.Append(new W.GridSpan { Val = cell.ColumnSpan });
                    if (cell.RowSpan > 1)
                    {
                        properties.Append(new W.VerticalMerge { Val = W.MergedCellValues.Restart });
                        nextVerticalSpans[column] = new(cell.RowSpan - 1, cell.ColumnSpan);
                    }
                    if (rowIndex < table.HeaderRowCount)
                        properties.Append(new W.Shading { Fill = "E8E8E8", Val = W.ShadingPatternValues.Clear });
                    var wordCell = new W.TableCell(properties);
                    var cellNotes = notes with { AvailableWidthFraction = notes.AvailableWidthFraction
                        * table.ColumnWidthWeights.Skip(column).Take(cell.ColumnSpan).Sum() / table.ColumnWidthWeights.Sum() };
                    foreach (var block in cell.Content)
                        AppendBlock(wordCell, block, cellNotes);
                    if (!wordCell.Elements<W.Paragraph>().Any()) wordCell.Append(new W.Paragraph());
                    row.Append(wordCell);
                    column += cell.ColumnSpan;
                }
                if (sourceCellIndex != table.Rows[rowIndex].Cells.Count)
                    throw new InvalidDataException($"Table '{table.Id}' row '{table.Rows[rowIndex].Id}' contains cells beyond the declared column grid.");
                result.Append(row);
                verticalSpans = nextVerticalSpans;
            }
            return result;
        }

        private void AppendFigure(OpenXmlCompositeElement parent, ManuscriptBlock block, NoteContext notes)
        {
            var asset = document.Assets.SingleOrDefault(candidate => candidate.Id == block.ImageId)
                ?? throw new InvalidOperationException($"Figure image {block.ImageId:D} is missing from the publication.");
            using var imageStream = new MemoryStream(asset.Data, writable: false);
            using var codec = SkiaSharp.SKCodec.Create(imageStream)
                ?? throw new InvalidDataException($"Figure image {asset.Id:D} cannot be decoded.");
            var drawing = ImageDrawing(asset.Data, codec.Info.Width, codec.Info.Height, asset.FileName, block.Decorative ? string.Empty : block.AltText ?? asset.AltText,
                (block.FigurePresentation?.WidthPercent ?? 100) * notes.AvailableWidthFraction);
            var imageParagraph = new W.Paragraph(
                new W.ParagraphProperties(new W.Justification { Val = block.FigurePresentation?.Alignment switch
                { FigureAlignment.Start => W.JustificationValues.Left, FigureAlignment.End => W.JustificationValues.Right, _ => W.JustificationValues.Center } }),
                new W.Run(drawing));
            if (block.FigurePresentation?.CaptionPlacement == FigureCaptionPlacement.Above)
                parent.Append(Caption(block, notes));
            parent.Append(imageParagraph);
            if (block.FigurePresentation?.CaptionPlacement is not (FigureCaptionPlacement.Above or FigureCaptionPlacement.Hidden))
                parent.Append(Caption(block, notes));
        }

        private W.Paragraph Caption(ManuscriptBlock block, NoteContext notes)
        {
            var paragraph = new W.Paragraph(new W.ParagraphProperties(new W.Justification { Val = W.JustificationValues.Center }));
            foreach (var inline in block.Content) AppendInline(paragraph, inline, notes);
            return paragraph;
        }

        private W.Drawing ImageDrawing(byte[] data, int width, int height, string name, string description, double widthPercent = 100)
        {
            var type = data.Length >= 4 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
                ? ImagePartType.Png
                : ImagePartType.Jpeg;
            var part = _relationshipOwner switch
            {
                MainDocumentPart documentPart => documentPart.AddImagePart(type),
                FootnotesPart footnotesPart => footnotesPart.AddImagePart(type),
                _ => throw new InvalidOperationException("Unsupported DOCX image relationship owner."),
            };
            using (var input = new MemoryStream(data, writable: false)) part.FeedData(input);
            var relationshipId = _relationshipOwner.GetIdOfPart(part);
            var maximumWidth = Math.Max(1, (document.Profile.PageWidthInches - 2 * document.Profile.PageMarginInches) * 914_400 * widthPercent / 100);
            var maximumHeight = Math.Max(1, (document.Profile.PageHeightInches - 2 * document.Profile.PageMarginInches) * 914_400);
            var safeWidth = Math.Max(1, width);
            var safeHeight = Math.Max(1, height);
            var scale = Math.Min(maximumWidth / safeWidth, maximumHeight / safeHeight);
            var cx = Math.Max(1, (long)Math.Round(safeWidth * scale));
            var cy = Math.Max(1, (long)Math.Round(safeHeight * scale));
            var picture = new PIC.Picture(
                new PIC.NonVisualPictureProperties(
                    new PIC.NonVisualDrawingProperties { Id = 0U, Name = name, Description = description },
                    new PIC.NonVisualPictureDrawingProperties()),
                new PIC.BlipFill(
                    new A.Blip { Embed = relationshipId },
                    new A.Stretch(new A.FillRectangle())),
                new PIC.ShapeProperties(
                    new A.Transform2D(
                        new A.Offset { X = 0, Y = 0 },
                        new A.Extents { Cx = cx, Cy = cy }),
                    new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));
            var graphicData = new A.GraphicData(picture)
            {
                Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture",
            };
            var inline = new DW.Inline(
                new DW.Extent { Cx = cx, Cy = cy },
                new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
                new DW.DocProperties { Id = _nextDrawingId++, Name = name, Description = description },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(graphicData))
            {
                DistanceFromTop = 0U,
                DistanceFromBottom = 0U,
                DistanceFromLeft = 0U,
                DistanceFromRight = 0U,
            };
            return new W.Drawing(inline);
        }

        private void AddFootnote(ManuscriptNote note, NoteContext notes)
        {
            var part = EnsureFootnotesPart();
            var footnote = new W.Footnote { Id = notes.FootnoteIds[note.Id] };
            var previousOwner = _relationshipOwner;
            _relationshipOwner = part;
            try
            {
                foreach (var block in note.Content)
                    AppendBlock(footnote, block, notes);
                var paragraph = footnote.Elements<W.Paragraph>().FirstOrDefault();
                if (paragraph is null) { paragraph = new W.Paragraph(); footnote.PrependChild(paragraph); }
                var reference = new W.Run(new W.RunProperties(new W.VerticalTextAlignment { Val = W.VerticalPositionValues.Superscript }),
                    new W.FootnoteReferenceMark());
                if (paragraph.ParagraphProperties is { } properties) paragraph.InsertAfter(reference, properties);
                else paragraph.PrependChild(reference);
                part.Footnotes!.Append(footnote);
            }
            finally { _relationshipOwner = previousOwner; }
        }

        private FootnotesPart EnsureFootnotesPart()
        {
            if (main.FootnotesPart is { } existing) return existing;
            var part = main.AddNewPart<FootnotesPart>();
            part.Footnotes = new W.Footnotes(
                new W.Footnote(
                    new W.Paragraph(new W.Run(new W.SeparatorMark())))
                { Type = W.FootnoteEndnoteValues.Separator, Id = -1 },
                new W.Footnote(
                    new W.Paragraph(new W.Run(new W.ContinuationSeparatorMark())))
                { Type = W.FootnoteEndnoteValues.ContinuationSeparator, Id = 0 });
            return part;
        }

        private void AppendEndnotes()
        {
            var citations = document.Citations.Occurrences.Where(citation => citation.NoteRuns is not null).ToList();
            if (_endnotes.Count == 0 && citations.Count == 0) return;
            AppendHeading("Endnotes", 1);
            foreach (var (topLevelId, title) in _documentTitles)
            {
                var authored = _endnotes.Where(note => note.Context.TopLevelId == topLevelId).OrderBy(note => note.Number).ToList();
                var cited = citations.Where(citation => citation.Identity.TopLevelContainer == topLevelId).ToList();
                if (authored.Count == 0 && cited.Count == 0) continue;
                AppendHeading(title, 2);
                if (authored.Count > 0 && cited.Count > 0) AppendHeading("Author notes", 3);
                foreach (var pending in authored)
                {
                    var id = NextBookmarkId();
                    var paragraph = new W.Paragraph(
                        new W.BookmarkStart { Name = EndnoteAnchor(pending.Context.OccurrencePath, pending.Note.Id), Id = id },
                        new W.Run(new W.Text($"{pending.Number}. ")));
                    _body.Append(paragraph);
                    foreach (var block in pending.Note.Content)
                    {
                        if (block.Type == ManuscriptBlockType.Table)
                            throw new InvalidDataException("Tables are not permitted in semantic notes.");
                        AppendBlock(_body, block, pending.Context);
                    }
                    _body.Append(new W.Paragraph(new W.BookmarkEnd { Id = id }));
                }
                if (authored.Count > 0 && cited.Count > 0) AppendHeading("Citations", 3);
                foreach (var citation in cited)
                {
                    var id = NextBookmarkId();
                    var paragraph = new W.Paragraph(
                        new W.BookmarkStart { Name = "citation_" + PublicationCitationResolver.Anchor(citation), Id = id },
                        new W.Run(new W.Text($"{citation.NoteNumber}. ") { Space = SpaceProcessingModeValues.Preserve }));
                    AppendCitationRuns(paragraph, citation.NoteRuns!);
                    paragraph.Append(new W.Hyperlink(new W.Run(new W.Text(" ↩") { Space = SpaceProcessingModeValues.Preserve }))
                    { Anchor = "citation_ref_" + PublicationCitationResolver.Anchor(citation) });
                    paragraph.Append(new W.BookmarkEnd { Id = id });
                    _body.Append(paragraph);
                }
            }
        }

        private string NextBookmarkId() => (++_nextBookmarkId).ToString(System.Globalization.CultureInfo.InvariantCulture);

        private void AppendBibliography()
        {
            if (document.Citations.Bibliography.Count == 0) return;
            AppendHeading(document.Citations.BibliographyTitle, 1);
            foreach (var entry in document.Citations.BibliographyEntries)
            {
                var paragraph = new W.Paragraph(new W.ParagraphProperties(
                    new W.ParagraphStyleId { Val = "Bibliography" },
                    new W.Indentation { Left = "720", Hanging = "720" }));
                AppendCitationRuns(paragraph, entry.Runs);
                _body.Append(paragraph);
            }
        }

        private void AppendHeading(string text, int level)
        {
            var style = level == 0 ? "Title" : $"Heading{Math.Clamp(level, 1, 6)}";
            AppendParagraph(text, style);
        }

        private void AppendParagraph(string text, string? style = null, int? indentationTwips = null)
        {
            var properties = new W.ParagraphProperties();
            if (!string.IsNullOrWhiteSpace(style)) properties.Append(new W.ParagraphStyleId { Val = style });
            if (indentationTwips is int indentation) properties.Append(new W.Indentation { Left = indentation.ToString() });
            _body.Append(new W.Paragraph(properties, new W.Run(new W.Text(text) { Space = SpaceProcessingModeValues.Preserve })));
        }

        private void AppendPageBreak() => _body.Append(new W.Paragraph(new W.Run(new W.Break { Type = W.BreakValues.Page })));

        private void AppendSectionBreak()
        {
            _body.Append(new W.Paragraph(
                new W.ParagraphProperties(
                    SectionProperties())));
        }

        private W.SectionProperties SectionProperties()
        {
            var margin = (uint)Math.Round(document.Profile.PageMarginInches * 1440);
            return new W.SectionProperties(
                new W.FootnoteProperties(new W.NumberingRestart { Val = W.RestartNumberValues.EachSection }),
                new W.PageSize { Width = (uint)Math.Round(document.Profile.PageWidthInches * 1440), Height = (uint)Math.Round(document.Profile.PageHeightInches * 1440) },
                new W.PageMargin { Top = (int)margin, Bottom = (int)margin, Left = margin, Right = margin, Header = margin / 2, Footer = margin / 2, Gutter = 0 });
        }

        private static string EndnoteAnchor(string occurrencePath, string noteId)
        {
            var source = $"{occurrencePath}|{noteId}";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant()[..32];
            return $"endnote_{hash}";
        }
    }

    private sealed record DesignedPageArtwork(byte[] Data, int Width, int Height, string Description);
    private sealed record NoteContext(
        string TopLevelTitle,
        string TopLevelId,
        string OccurrencePath,
        ManuscriptDocument Manuscript,
        IReadOnlyDictionary<string, int> FootnoteIds,
        IReadOnlyDictionary<string, int> EndnoteNumbers)
    {
        public IReadOnlyList<string> PlacementPath { get; init; } = [];
        public double AvailableWidthFraction { get; init; } = 1;
    }
    private sealed record PendingEndnote(
        string TopLevelTitle,
        int Number,
        ManuscriptNote Note,
        NoteContext Context);
    private sealed record VerticalSpan(int RemainingRows, int ColumnSpan);
}
