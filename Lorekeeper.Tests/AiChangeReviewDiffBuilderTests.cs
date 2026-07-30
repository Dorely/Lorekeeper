using System.Text.Json;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Outline;

namespace Lorekeeper.Tests;

public sealed class AiChangeReviewDiffBuilderTests
{
    [Fact]
    public void MarkOnlyManuscriptChangeProducesTruthfulReviewSection()
    {
        var before = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "Marked text", revision: 3);
        var (after, _) = ManuscriptOperations.Apply(
            before,
            [
                new SetManuscriptInlineMark(
                    before.Content[0].Id,
                    0,
                    6,
                    ManuscriptMarkType.Emphasis,
                    Enabled: true),
            ]);

        AssertSemanticReview(before, after, "Emphasis");
    }

    [Fact]
    public void StyleOnlyManuscriptChangeProducesTruthfulReviewSection()
    {
        var before = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "Styled text", revision: 3);
        var (after, _) = ManuscriptOperations.Apply(
            before,
            [new SetManuscriptBlockStyle(before.Content[0].Id, "custom-opening")]);

        AssertSemanticReview(before, after, "custom-opening");
    }

    [Fact]
    public void GroupedStyleCreationShowsImmutableKindAndSemanticRole()
    {
        var document = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "Styled text", revision: 3);
        var (styled, _) = ManuscriptOperations.Apply(
            document,
            [new SetManuscriptBlockStyle(document.Content[0].Id, "opening-paragraph")]);
        var batch = new AiChangeBatch { CreatedAt = DateTime.UtcNow };
        var styleId = Guid.NewGuid();
        var style = new ManuscriptStyleInput(
            styleId,
            "Opening paragraph",
            ManuscriptStyleKind.Paragraph,
            "opening-paragraph",
            new ManuscriptStyleProperties(FontSizePoints: 12));
        var changes = new[]
        {
            new AiChange
            {
                Batch = batch,
                Order = 0,
                ResourceKind = "ManuscriptStyle",
                ToolName = "upsert_manuscript_style",
                BeforeJson = JsonSerializer.Serialize(new ManuscriptStyleChange(null, null)),
                AfterJson = JsonSerializer.Serialize(new ManuscriptStyleChange(null, style)),
            },
            new AiChange
            {
                Batch = batch,
                Order = 1,
                ResourceKind = "ChapterManuscript",
                ToolName = "apply_manuscript_operations",
                BeforeJson = JsonSerializer.Serialize(Change(document)),
                AfterJson = JsonSerializer.Serialize(Change(styled)),
            },
        };

        Assert.True(AiChangeReviewDiffBuilder.TryBuild(changes, out var diff));
        Assert.Contains(
            diff.Sections,
            section => section.Label == $"Style {styleId}: Kind"
                && section.NewText == ManuscriptStyleKind.Paragraph.ToString());
        Assert.Contains(
            diff.Sections,
            section => section.Label == $"Style {styleId}: Semantic role"
                && section.NewText == "opening-paragraph");
    }

    private static void AssertSemanticReview(
        ManuscriptDocument before,
        ManuscriptDocument after,
        string expectedDetail)
    {
        var change = new AiChange
        {
            ResourceKind = "ChapterManuscript",
            ToolName = "apply_manuscript_operations",
            BeforeJson = JsonSerializer.Serialize(Change(before)),
            AfterJson = JsonSerializer.Serialize(Change(after)),
            Status = AiChangeStatus.Pending,
        };

        Assert.True(AiChangeReviewDiffBuilder.TryBuild(change, out var diff));
        var section = Assert.Single(diff.Sections);
        Assert.Equal("Structure and formatting", section.Label);
        Assert.Contains(expectedDetail, section.NewText);
        Assert.True(section.Additions > 0);
        Assert.True(section.Deletions > 0);
    }

    private static ChapterManuscriptChange Change(ManuscriptDocument document) =>
        new(
            document.ManuscriptId,
            "Chapter",
            document.Revision,
            ManuscriptCodec.Serialize(document));
}
