using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Publish;

namespace Lorekeeper.Tests;

public sealed class SemanticPublishFormattingTests
{
    [Fact]
    public void MarkdownAndHtmlPreserveSemanticBlocksMarksAndFigures()
    {
        var imageId = Guid.NewGuid();
        var document = new ManuscriptDocument
        {
            ManuscriptId = Guid.NewGuid(),
            Revision = 1,
            Content =
            [
                new ManuscriptBlock
                {
                    Id = "heading",
                    Type = ManuscriptBlockType.Heading,
                    StyleRole = ManuscriptStyleRoles.ChapterHeading,
                    HeadingLevel = 1,
                    Content = [new ManuscriptInline { Text = "Arrival" }],
                },
                new ManuscriptBlock
                {
                    Id = "body",
                    Type = ManuscriptBlockType.Paragraph,
                    StyleRole = "body",
                    Content =
                    [
                        new ManuscriptInline
                        {
                            Text = "Marked",
                            Marks =
                            [
                                new ManuscriptMark { Type = ManuscriptMarkType.Strong },
                                new ManuscriptMark { Type = ManuscriptMarkType.CharacterStyle, Value = "lead-in" },
                            ],
                        },
                    ],
                },
                new ManuscriptBlock
                {
                    Id = "figure",
                    Type = ManuscriptBlockType.Figure,
                    StyleRole = ManuscriptStyleRoles.FigureCaption,
                    ImageId = imageId,
                    AltText = "A city map",
                    Content = [new ManuscriptInline { Text = "The old city" }],
                },
            ],
        };
        var asset = new PublishAssetDocument(
            imageId,
            "map.png",
            "image/png",
            [1, 2, 3],
            "A city map");

        var markdown = SemanticPublishFormatting.Markdown(
            document,
            id => id == imageId ? asset : null);
        var html = SemanticPublishFormatting.Html(
            document,
            id => id == imageId ? "images/map.png" : null);

        Assert.Contains("# Arrival", markdown, StringComparison.Ordinal);
        Assert.Contains("data-character-style=\"lead-in\"", markdown, StringComparison.Ordinal);
        Assert.Contains("![A city map]", markdown, StringComparison.Ordinal);
        Assert.Contains("<h1", html, StringComparison.Ordinal);
        Assert.Contains("<strong>Marked</strong>", html, StringComparison.Ordinal);
        Assert.Contains("<figure", html, StringComparison.Ordinal);
        Assert.Contains("alt=\"A city map\"", html, StringComparison.Ordinal);
        Assert.Contains("<figcaption>The old city</figcaption>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownEncodesRawHtmlAndPreservesCodeCombinedWithOtherMarks()
    {
        var document = new ManuscriptDocument
        {
            ManuscriptId = Guid.NewGuid(),
            Revision = 1,
            Content =
            [
                new ManuscriptBlock
                {
                    Id = "hostile",
                    Type = ManuscriptBlockType.Paragraph,
                    StyleRole = ManuscriptStyleRoles.Body,
                    Content =
                    [
                        new ManuscriptInline
                        {
                            Text = "`value` <script>alert(1)</script>",
                            Marks =
                            [
                                new ManuscriptMark { Type = ManuscriptMarkType.Strong },
                                new ManuscriptMark { Type = ManuscriptMarkType.Code },
                            ],
                        },
                    ],
                },
            ],
        };

        var markdown = SemanticPublishFormatting.Markdown(document, _ => null);

        Assert.Contains("**<code>", markdown, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", markdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrintStyleCssCannotTerminateTheContainingStyleElement()
    {
        var css = EpubPublishFormatter.RenderNamedStyleRules(
        [
            new PublishManuscriptStyleDocument(
                "Hostile",
                ManuscriptStyleKind.Paragraph,
                "</style><script>alert(1)</script>",
                new ManuscriptStyleProperties(FontSizePoints: 12)),
        ]);

        Assert.DoesNotContain("</style>", css, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\\3c ", css, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownEscapesLiteralBlockSyntaxAndCarriesCustomParagraphRole()
    {
        var document = new ManuscriptDocument
        {
            ManuscriptId = Guid.NewGuid(),
            Revision = 1,
            Content =
            [
                new ManuscriptBlock
                {
                    Id = "literal",
                    Type = ManuscriptBlockType.Paragraph,
                    StyleRole = ManuscriptStyleRoles.Body,
                    Content = [new ManuscriptInline { Text = "# literal > - 1. ` ~~" }],
                },
                new ManuscriptBlock
                {
                    Id = "styled",
                    Type = ManuscriptBlockType.Paragraph,
                    StyleRole = "opening-paragraph",
                    Content = [new ManuscriptInline { Text = "Styled" }],
                },
            ],
        };

        var markdown = SemanticPublishFormatting.Markdown(document, _ => null);

        Assert.Contains("\\# literal &gt; \\- 1\\. \\` \\~\\~", markdown, StringComparison.Ordinal);
        Assert.Contains("data-style-role=\"opening-paragraph\"", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlCarriesCustomSceneBreakRole()
    {
        var document = new ManuscriptDocument
        {
            ManuscriptId = Guid.NewGuid(),
            Revision = 1,
            Content =
            [
                new ManuscriptBlock
                {
                    Id = "ornamental-break",
                    Type = ManuscriptBlockType.SceneBreak,
                    StyleRole = "ornamental-break",
                },
            ],
        };

        var html = SemanticPublishFormatting.Html(document, _ => null);

        Assert.Contains(
            "<hr class=\"scene-break\" data-style-role=\"ornamental-break\" />",
            html,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownAndHtmlPreserveIntentionalSingleLineBreaks()
    {
        var document = new ManuscriptDocument
        {
            ManuscriptId = Guid.NewGuid(),
            Revision = 1,
            Content =
            [
                new ManuscriptBlock
                {
                    Id = "verse",
                    Type = ManuscriptBlockType.Paragraph,
                    StyleRole = ManuscriptStyleRoles.Body,
                    Content =
                    [
                        new ManuscriptInline
                        {
                            Text = "First line\nSecond line",
                            Marks = [new ManuscriptMark { Type = ManuscriptMarkType.Emphasis }],
                        },
                    ],
                },
            ],
        };

        var markdown = SemanticPublishFormatting.Markdown(document, _ => null);
        var html = SemanticPublishFormatting.Html(document, _ => null);

        Assert.Contains("*First line<br />", markdown, StringComparison.Ordinal);
        Assert.Contains("<em>First line<br />Second line</em>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownPreservesUnicodeAndEntitiesWhileBlockingRawHtml()
    {
        var escaped = SemanticPublishFormatting.EscapeMarkdownLiteral(
            "It's café © & <script>");

        Assert.Equal("It\\'s café © &amp; &lt;script&gt;", escaped);
        Assert.DoesNotContain("&\\#", escaped, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineSmallCapsHasMarkupAndBasePublicationCss()
    {
        var document = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "Small caps", revision: 1);
        document.Content[0] = document.Content[0] with
        {
            Content =
            [
                new ManuscriptInline
                {
                    Text = "Small caps",
                    Marks = [new ManuscriptMark { Type = ManuscriptMarkType.SmallCaps }],
                },
            ],
        };

        var html = SemanticPublishFormatting.Html(document, _ => null);
        var css = EpubPublishFormatter.RenderStylesheet(MinimalPublishDocument());
        var printCss = EpubPublishFormatter.RenderSemanticInlineRules();

        Assert.Contains("<span class=\"small-caps\">Small caps</span>", html, StringComparison.Ordinal);
        Assert.Contains(".small-caps", css, StringComparison.Ordinal);
        Assert.Contains("font-variant-caps: small-caps", css, StringComparison.Ordinal);
        Assert.Contains(".small-caps", printCss, StringComparison.Ordinal);
        Assert.Contains("font-variant-caps: small-caps", printCss, StringComparison.Ordinal);
    }

    private static PublishDocument MinimalPublishDocument() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Book",
            "book",
            DateTime.UtcNow,
            new PublishDocumentProfile(
                TitleOverride: string.Empty,
                Subtitle: string.Empty,
                Author: string.Empty,
                Language: "en",
                Publisher: string.Empty,
                Copyright: string.Empty,
                Isbn: string.Empty,
                Description: string.Empty,
                Dedication: string.Empty,
                Acknowledgments: string.Empty,
                References: string.Empty,
                IncludeTableOfContents: true,
                IncludeVisibleTableOfContents: true,
                IncludeActSynopses: false,
                IncludeChapterSynopses: false,
                IncludeActHeadings: true,
                IncludeChapterHeadings: true,
                NumberActs: false,
                NumberChapters: false,
                IncludeTitlePage: true,
                PrintPicturePageSpreadMode: PrintPicturePageSpreadMode.WholeSpread,
                EpubPicturePageSpreadMode: EpubPicturePageSpreadMode.RequestLandscape,
                PageWidthInches: 6,
                PageHeightInches: 9,
                PageMarginInches: 0.75,
                BodyFontSizePoints: 11,
                BodyLineHeight: 1.4),
            CoverAsset: null,
            Sections: [],
            Assets: [],
            Placements: []);
}
