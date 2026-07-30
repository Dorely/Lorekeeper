using System.Text.Json;
using Lorekeeper.EditorChat;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Tests;

public sealed class EditorChatStagingTests
{
    [Fact]
    public async Task SequentialReviewOperationsBuildOneStructuredOverlay()
    {
        var repository = new MemoryAiChangeRepository();
        var context = new EditorChatChangeStagingContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            repository);
        context.BeginToolCall(Guid.NewGuid(), "call-1", "apply_manuscript_operations", "{}");
        var source = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "Text", revision: 4);
        var chapter = new Chapter
        {
            Id = source.ManuscriptId,
            ProjectId = Guid.NewGuid(),
            Title = "Chapter",
            ManuscriptJson = ManuscriptCodec.Serialize(source),
            ManuscriptRevision = source.Revision,
        };
        var (marked, _) = ManuscriptOperations.Apply(
            source,
            [new SetManuscriptInlineMark(source.Content[0].Id, 0, 4, ManuscriptMarkType.Emphasis, true)]);
        await context.StageChapterManuscriptEditAsync(chapter, source, marked, "Mark", "{}");

        context.BeginToolCall(Guid.NewGuid(), "call-2", "apply_manuscript_operations", "{}");
        Assert.True(context.TryGetChapterManuscriptDraft(chapter.Id, out var firstOverlay));
        var (styled, _) = ManuscriptOperations.Apply(
            firstOverlay,
            [new SetManuscriptBlockStyle(source.Content[0].Id, ManuscriptStyleRoles.BlockQuote)]);
        await context.StageChapterManuscriptEditAsync(chapter, firstOverlay, styled, "Style", "{}");

        Assert.True(context.TryGetChapterManuscriptDraft(chapter.Id, out var finalOverlay));
        Assert.Equal(6, finalOverlay.Revision);
        Assert.Equal(ManuscriptStyleRoles.BlockQuote, finalOverlay.Content[0].StyleRole);
        Assert.Contains(
            finalOverlay.Content[0].Content[0].Marks,
            mark => mark.Type == ManuscriptMarkType.Emphasis);
        Assert.Equal(2, repository.Changes.Count);
        var secondBefore = JsonSerializer.Deserialize<ChapterManuscriptChange>(
            repository.Changes[1].BeforeJson)!;
        Assert.True(ManuscriptCodec.ContentEquals(marked, secondBefore.Manuscript));
    }

    [Fact]
    public async Task NamedStyleMutationStagesReviewableBeforeAndAfterPayloads()
    {
        var repository = new MemoryAiChangeRepository();
        var context = new EditorChatChangeStagingContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            repository);
        context.BeginToolCall(Guid.NewGuid(), "style-call", "upsert_manuscript_style", "{}");
        var styleId = Guid.NewGuid();
        var input = new ManuscriptStyleInput(
            styleId,
            "Body",
            ManuscriptStyleKind.Paragraph,
            "body",
            new ManuscriptStyleProperties(FontSizePoints: 11));

        await context.StageManuscriptStyleChangeAsync(
            null,
            input,
            new ManuscriptStyleView(
                styleId,
                input.Name,
                input.Kind,
                input.SemanticRole,
                input.Definition,
                1),
            "Create body style",
            "{}");

        var change = Assert.Single(repository.Changes);
        Assert.Equal("ManuscriptStyle", change.ResourceKind);
        Assert.Equal("upsert_manuscript_style", change.ToolName);
        var payload = JsonSerializer.Deserialize<ManuscriptStyleChange>(
            change.AfterJson)!;
        Assert.Null(payload.Before);
        Assert.Equal("body", payload.After!.SemanticRole);
    }

    [Fact]
    public async Task NamedStyleDeletionStagesADistinctApprovalPayload()
    {
        var repository = new MemoryAiChangeRepository();
        var context = new EditorChatChangeStagingContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            repository);
        context.BeginToolCall(Guid.NewGuid(), "delete-call", "delete_manuscript_style", "{}");
        var before = new ManuscriptStyleView(
            Guid.NewGuid(),
            "Opening",
            ManuscriptStyleKind.Paragraph,
            "opening",
            new ManuscriptStyleProperties(FontSizePoints: 12),
            4);

        await context.StageManuscriptStyleChangeAsync(
            before,
            after: null,
            preview: null,
            "Delete opening style",
            "{}");

        var change = Assert.Single(repository.Changes);
        Assert.Equal("delete_manuscript_style", change.ToolName);
        var beforePayload = JsonSerializer.Deserialize<ManuscriptStyleChange>(change.BeforeJson)!;
        var afterPayload = JsonSerializer.Deserialize<ManuscriptStyleChange>(change.AfterJson)!;
        Assert.NotNull(beforePayload.After);
        Assert.Equal(before.Id, afterPayload.Before!.Id);
        Assert.Null(afterPayload.After);
        Assert.NotEqual(change.BeforeJson, change.AfterJson);
    }

    private sealed class MemoryAiChangeRepository : IAiChangeRepository
    {
        public List<AiChange> Changes { get; } = [];

        public Task<List<AiChangeBatch>> ListPendingBatchesAsync(
            Guid projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<AiChangeBatch>());

        public Task<AiChangeBatch?> GetBatchAsync(
            Guid batchId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AiChangeBatch?>(null);

        public Task<AiChange?> GetChangeAsync(
            Guid changeId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AiChange?>(Changes.FirstOrDefault(change => change.Id == changeId));

        public Task AddBatchAsync(
            AiChangeBatch batch,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task AddChangeAsync(
            AiChange change,
            CancellationToken cancellationToken = default)
        {
            Changes.Add(change);
            return Task.CompletedTask;
        }

        public void UpdateBatch(AiChangeBatch batch)
        {
        }

        public void UpdateChange(AiChange change)
        {
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
