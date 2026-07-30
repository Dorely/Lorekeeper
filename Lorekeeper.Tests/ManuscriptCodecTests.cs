using Lorekeeper.Manuscripts;

namespace Lorekeeper.Tests;

public sealed class ManuscriptCodecTests
{
    [Fact]
    public void PlainTextCodecNormalizesLineEndingsAndPreservesUnicode()
    {
        var id = Guid.NewGuid();
        const string input = "Café\r\nline two\r\n\r\n\r\n###\r\n\r\nParagraph Ω";

        var document = ManuscriptCodec.FromPlainText(id, input, deterministicIds: true);

        Assert.Equal("Café\nline two\n\n***\n\nParagraph Ω", ManuscriptCodec.ProjectPlainText(document));
        Assert.Equal(3, document.Content.Count);
        Assert.Equal(ManuscriptBlockType.SceneBreak, document.Content[1].Type);
        Assert.Equal(document.Content.Select(block => block.Id).Distinct().Count(), document.Content.Count);
        Assert.Equal(
            document.Content.Select(block => block.Id),
            ManuscriptCodec.FromPlainText(id, input, deterministicIds: true).Content.Select(block => block.Id));
        var json = ManuscriptCodec.Serialize(document);
        Assert.Contains("\"type\":\"paragraph\"", json);
        Assert.Contains("\"type\":\"sceneBreak\"", json);
        Assert.DoesNotContain("\"type\":0", json);
    }

    [Fact]
    public void ReparsePreservesStableBlockIdsAndUnchangedMarks()
    {
        var id = Guid.NewGuid();
        var source = ManuscriptCodec.FromPlainText(id, "First\n\nSecond", revision: 4);
        source.Content[0] = source.Content[0] with
        {
            Content =
            [
                new ManuscriptInline
                {
                    Text = "First",
                    Marks = [new ManuscriptMark { Type = ManuscriptMarkType.Emphasis }],
                },
            ],
        };

        var reparsed = ManuscriptCodec.ReparsePreservingBlockIds(source, "First\n\nChanged");

        Assert.Equal(5, reparsed.Revision);
        Assert.Equal(source.Content[0].Id, reparsed.Content[0].Id);
        Assert.Equal(ManuscriptMarkType.Emphasis, Assert.Single(reparsed.Content[0].Content).Marks.Single().Type);
        Assert.Equal(source.Content[1].Id, reparsed.Content[1].Id);
        Assert.Equal("Changed", ManuscriptCodec.Text(reparsed.Content[1]));
    }

    [Fact]
    public void ReparseMapsInlineMarksAcrossInsertionsAndReplacements()
    {
        var source = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "abcd", revision: 2);
        source.Content[0] = source.Content[0] with
        {
            Content =
            [
                new ManuscriptInline { Text = "ab" },
                new ManuscriptInline
                {
                    Text = "cd",
                    Marks = [new ManuscriptMark { Type = ManuscriptMarkType.Strong }],
                },
            ],
        };

        var inserted = ManuscriptCodec.ReparsePreservingBlockIds(source, "Xabcd");
        var replaced = ManuscriptCodec.ReparsePreservingBlockIds(source, "xbcd");

        Assert.Equal("cd", Assert.Single(
            inserted.Content[0].Content,
            inline => inline.Marks.Any(mark => mark.Type == ManuscriptMarkType.Strong)).Text);
        Assert.Equal("cd", Assert.Single(
            replaced.Content[0].Content,
            inline => inline.Marks.Any(mark => mark.Type == ManuscriptMarkType.Strong)).Text);
    }

    [Fact]
    public void ReparseKeepsInsertedTextMarkedInsideAUniformMarkedRun()
    {
        var source = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "abcd", revision: 2);
        source.Content[0] = source.Content[0] with
        {
            Content =
            [
                new ManuscriptInline
                {
                    Text = "abcd",
                    Marks = [new ManuscriptMark { Type = ManuscriptMarkType.Emphasis }],
                },
            ],
        };

        var reparsed = ManuscriptCodec.ReparsePreservingBlockIds(source, "abXcd");

        Assert.Equal("abXcd", Assert.Single(reparsed.Content[0].Content).Text);
        Assert.Contains(
            reparsed.Content[0].Content[0].Marks,
            mark => mark.Type == ManuscriptMarkType.Emphasis);
    }

    [Fact]
    public void PlainTextCodecAcceptsWhitespaceSeparatorsAndSceneBreakAliases()
    {
        var document = ManuscriptCodec.FromPlainText(
            Guid.NewGuid(),
            "First\n \t\n* * *\n\n###\n\nLast");

        Assert.Equal("First\n\n***\n\n***\n\nLast", ManuscriptCodec.ProjectPlainText(document));
        Assert.Equal(
            [ManuscriptBlockType.Paragraph, ManuscriptBlockType.SceneBreak, ManuscriptBlockType.SceneBreak, ManuscriptBlockType.Paragraph],
            document.Content.Select(block => block.Type));
    }

    [Fact]
    public void ManuscriptValidationRejectsUnknownFieldsAndNegativeRevisions()
    {
        var document = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "Text", revision: 1);
        var json = ManuscriptCodec.Serialize(document);

        Assert.Throws<InvalidDataException>(() =>
            ManuscriptCodec.Deserialize(json.Replace(
                $"\"schemaVersion\":{ManuscriptDocument.CurrentSchemaVersion}",
                $"\"schemaVersion\":{ManuscriptDocument.CurrentSchemaVersion},\"unknown\":true",
                StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() =>
            ManuscriptCodec.Deserialize(
                json.Replace("\"revision\":1", "\"revision\":-1", StringComparison.Ordinal)));
    }

    [Fact]
    public void OperationsRejectBlockDelimitersAndUnicodeSplitOffsets()
    {
        var source = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "A😀B", revision: 1);
        var blockId = source.Content[0].Id;

        Assert.Throws<ArgumentException>(() =>
            ManuscriptOperations.Apply(
                source,
                [new ReplaceManuscriptBlockText(blockId, "First\n\nSecond")]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ManuscriptOperations.Apply(
                source,
                [new SplitManuscriptBlock(blockId, 2)]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ManuscriptOperations.Apply(
                source,
                [new SetManuscriptInlineMark(
                    blockId,
                    1,
                    2,
                    ManuscriptMarkType.Emphasis,
                    Enabled: true)]));
    }

    [Fact]
    public void FlagMarksRejectValuesThatTheEditorCannotRoundTrip()
    {
        var document = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "Marked", revision: 1);
        document.Content[0].Content[0].Marks.Add(new ManuscriptMark
        {
            Type = ManuscriptMarkType.Emphasis,
            Value = "not-semantic",
        });

        Assert.Throws<InvalidDataException>(() => ManuscriptCodec.Serialize(document));
        var source = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "Marked", revision: 1);
        Assert.Throws<InvalidDataException>(() =>
            ManuscriptOperations.Apply(
                source,
                [
                    new SetManuscriptInlineMark(
                        source.Content[0].Id,
                        0,
                        6,
                        ManuscriptMarkType.Strong,
                        Enabled: true,
                        Value: "not-semantic"),
                ]));
    }

    [Fact]
    public void SemanticEditorMarksAndStructuralInspectionShareTheDomainSchema()
    {
        var source = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "Chapter\n\nBody", revision: 4);
        var (styled, _) = ManuscriptOperations.Apply(
            source,
            [
                new SetManuscriptBlockStyle(source.Content[0].Id, ManuscriptStyleRoles.ChapterHeading),
                new SetManuscriptInlineMark(
                    source.Content[1].Id,
                    0,
                    4,
                    ManuscriptMarkType.SmallCaps,
                    Enabled: true),
                new SetManuscriptInlineMark(
                    source.Content[1].Id,
                    0,
                    4,
                    ManuscriptMarkType.CharacterStyle,
                    Enabled: true,
                    Value: "lead-in"),
            ]);

        var inspection = ManuscriptInspection.Inspect(
            styled,
            query: "Chapter",
            blockType: nameof(ManuscriptBlockType.Paragraph),
            styleRole: ManuscriptStyleRoles.ChapterHeading);
        var roundTrip = ManuscriptCodec.Deserialize(
            ManuscriptCodec.Serialize(styled),
            styled.ManuscriptId,
            styled.Revision);

        Assert.True(inspection.IsValid);
        Assert.Equal(source.Content[0].Id, Assert.Single(inspection.Matches).Id);
        Assert.Contains(
            roundTrip.Content[1].Content.SelectMany(inline => inline.Marks),
            mark => mark.Type == ManuscriptMarkType.SmallCaps);
        Assert.Contains(
            roundTrip.Content[1].Content.SelectMany(inline => inline.Marks),
            mark => mark.Type == ManuscriptMarkType.CharacterStyle && mark.Value == "lead-in");
    }

    [Fact]
    public void ManuscriptValidationRejectsUnsafeRichEditorLinks()
    {
        var source = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "Link", revision: 1);
        source.Content[0] = source.Content[0] with
        {
            Content =
            [
                new ManuscriptInline
                {
                    Text = "Link",
                    Marks =
                    [
                        new ManuscriptMark
                        {
                            Type = ManuscriptMarkType.Link,
                            Value = "javascript:alert(1)",
                        },
                    ],
                },
            ],
        };

        Assert.Throws<InvalidDataException>(() => ManuscriptCodec.Serialize(source));
    }

    [Fact]
    public void ManuscriptValidationRejectsXmlForbiddenControlCharacters()
    {
        var source = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "Valid", revision: 1);
        source.Content[0] = source.Content[0] with
        {
            Content = [new ManuscriptInline { Text = "Invalid\u0001text" }],
        };

        var error = Assert.Throws<InvalidDataException>(() => ManuscriptCodec.Serialize(source));

        Assert.Contains("XML-forbidden", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationsAdvanceOneRevisionAndReturnStableChangedIds()
    {
        var source = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "One\n\nTwo", revision: 8);
        var firstId = source.Content[0].Id;

        var (result, changed) = ManuscriptOperations.Apply(
            source,
            [
                new ReplaceManuscriptBlockText(firstId, "One revised"),
                new SetManuscriptInlineMark(firstId, 0, 3, ManuscriptMarkType.Strong, Enabled: true),
            ]);

        Assert.Equal(9, result.Revision);
        Assert.Equal([firstId], changed);
        Assert.Equal("One revised\n\nTwo", ManuscriptCodec.ProjectPlainText(result));
        Assert.Equal(ManuscriptMarkType.Strong, result.Content[0].Content[0].Marks.Single().Type);
    }

    [Fact]
    public void BlockTypeChangesPreserveStableIdsAndContent()
    {
        var source = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "Heading", revision: 2);
        var blockId = source.Content[0].Id;

        var (result, changed) = ManuscriptOperations.Apply(
            source,
            [
                new SetManuscriptBlockType(
                    blockId,
                    ManuscriptBlockType.Heading,
                    ManuscriptStyleRoles.ChapterHeading),
            ]);

        Assert.Equal(blockId, Assert.Single(result.Content).Id);
        Assert.Equal(ManuscriptBlockType.Heading, result.Content[0].Type);
        Assert.Equal(ManuscriptStyleRoles.ChapterHeading, result.Content[0].StyleRole);
        Assert.Equal("Heading", ManuscriptCodec.Text(result.Content[0]));
        Assert.Equal([blockId], changed);
    }

    [Fact]
    public void SameHeadingTypeMetadataEditPreservesNamedParagraphStyle()
    {
        var source = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "Heading", revision: 2);
        var blockId = source.Content[0].Id;
        source.Content[0] = source.Content[0] with
        {
            Type = ManuscriptBlockType.Heading,
            StyleRole = "custom-heading",
            HeadingLevel = 2,
        };

        var (result, _) = ManuscriptOperations.Apply(
            source,
            [new SetManuscriptBlockType(blockId, ManuscriptBlockType.Heading, HeadingLevel: 3)]);

        Assert.Equal("custom-heading", result.Content[0].StyleRole);
        Assert.Equal(3, result.Content[0].HeadingLevel);
    }

    [Fact]
    public void SameFigureTypeMetadataEditPreservesNamedStyleAndExistingImage()
    {
        var source = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "Caption", revision: 2);
        var blockId = source.Content[0].Id;
        var imageId = Guid.NewGuid();
        source.Content[0] = source.Content[0] with
        {
            Type = ManuscriptBlockType.Figure,
            StyleRole = "custom-figure",
            ImageId = imageId,
            AltText = "Old alternative text",
        };

        var (result, _) = ManuscriptOperations.Apply(
            source,
            [
                new SetManuscriptBlockType(
                    blockId,
                    ManuscriptBlockType.Figure,
                    AltText: "Updated alternative text"),
            ]);

        Assert.Equal("custom-figure", result.Content[0].StyleRole);
        Assert.Equal(imageId, result.Content[0].ImageId);
        Assert.Equal("Updated alternative text", result.Content[0].AltText);
    }

    [Fact]
    public void InlineMarkMutationPreservesUnrelatedMarksAndTextSegments()
    {
        var source = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "abcdef", revision: 2);
        source.Content[0] = source.Content[0] with
        {
            Content =
            [
                new ManuscriptInline
                {
                    Text = "abcdef",
                    Marks = [new ManuscriptMark { Type = ManuscriptMarkType.Language, Value = "en-US" }],
                },
            ],
        };

        var (result, _) = ManuscriptOperations.Apply(
            source,
            [new SetManuscriptInlineMark(source.Content[0].Id, 2, 4, ManuscriptMarkType.Emphasis, Enabled: true)]);

        Assert.Equal("abcdef", ManuscriptCodec.ProjectPlainText(result));
        Assert.Equal(["ab", "cd", "ef"], result.Content[0].Content.Select(inline => inline.Text));
        Assert.All(
            result.Content[0].Content,
            inline => Assert.Contains(inline.Marks, mark => mark.Type == ManuscriptMarkType.Language));
        Assert.Contains(result.Content[0].Content[1].Marks, mark => mark.Type == ManuscriptMarkType.Emphasis);
    }

    [Fact]
    public void SplitAndMergePreserveInlineMarks()
    {
        var source = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "abcdef", revision: 2);
        var blockId = source.Content[0].Id;
        source.Content[0] = source.Content[0] with
        {
            Content =
            [
                new ManuscriptInline
                {
                    Text = "abc",
                    Marks = [new ManuscriptMark { Type = ManuscriptMarkType.Emphasis }],
                },
                new ManuscriptInline
                {
                    Text = "def",
                    Marks = [new ManuscriptMark { Type = ManuscriptMarkType.Strong }],
                },
            ],
        };

        var (split, _) = ManuscriptOperations.Apply(
            source,
            [new SplitManuscriptBlock(blockId, 4)]);
        var tailId = split.Content[1].Id;

        Assert.Equal("abcd\n\nef", ManuscriptCodec.ProjectPlainText(split));
        Assert.Contains(split.Content[0].Content[0].Marks, mark => mark.Type == ManuscriptMarkType.Emphasis);
        Assert.Contains(split.Content[0].Content[^1].Marks, mark => mark.Type == ManuscriptMarkType.Strong);
        Assert.Contains(split.Content[1].Content[0].Marks, mark => mark.Type == ManuscriptMarkType.Strong);

        var (merged, _) = ManuscriptOperations.Apply(
            split,
            [new MergeManuscriptBlocks(blockId, tailId)]);

        Assert.Equal("abcdef", ManuscriptCodec.ProjectPlainText(merged));
        Assert.Contains(merged.Content[0].Content[0].Marks, mark => mark.Type == ManuscriptMarkType.Emphasis);
        Assert.Contains(merged.Content[0].Content[^1].Marks, mark => mark.Type == ManuscriptMarkType.Strong);
    }
}
