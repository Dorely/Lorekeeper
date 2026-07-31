using System.Text.Json;
using Lorekeeper.Chapters;
using Lorekeeper.EditorChat;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Tests;

public sealed class EditorManuscriptPreviewServiceTests
{
    [Fact]
    public async Task PreviewIsCompactAndApplyPersistsTheExactProjectedDocumentOnce()
    {
        var projectId = Guid.NewGuid();
        var chapter = Chapter(projectId);
        var source = ManuscriptCodec.FromPlainText(chapter.Id, string.Empty, revision: 0);
        var manuscripts = new MemoryManuscriptService(source);
        var service = Service(chapter, manuscripts);
        var mutated = false;
        var context = Context(projectId, () => mutated = true);
        var insertedText = new string('x', 10_000);

        var previewJson = await service.PreviewAsync(
            context,
            chapter.Id,
            source.Revision,
            [new ManuscriptOperationInput(
                "insertBlock",
                Index: 0,
                BlockType: "paragraph",
                Text: insertedText)]);

        Assert.True(previewJson.Length < 2_000);
        Assert.DoesNotContain(insertedText, previewJson, StringComparison.Ordinal);
        using var previewDocument = JsonDocument.Parse(previewJson);
        var previewId = previewDocument.RootElement.GetProperty("previewId").GetGuid();
        var changedBlockId = Assert.Single(previewDocument.RootElement
            .GetProperty("changedBlockIds")
            .EnumerateArray())
            .GetString();

        var applyJson = await service.ApplyAsync(context, previewId);

        Assert.True(mutated);
        Assert.DoesNotContain(insertedText, applyJson, StringComparison.Ordinal);
        Assert.NotNull(manuscripts.ReplacedDocument);
        var inserted = Assert.Single(manuscripts.ReplacedDocument.Content);
        Assert.Equal(insertedText, ManuscriptCodec.Text(inserted));
        Assert.Equal(changedBlockId, inserted.Id);
        var reused = await service.ApplyAsync(context, previewId);
        Assert.StartsWith("Error:", reused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyConsumesAndRejectsAPreviewWhenTheSourceChanged()
    {
        var projectId = Guid.NewGuid();
        var chapter = Chapter(projectId);
        var source = ManuscriptCodec.FromPlainText(chapter.Id, "Before", revision: 3);
        var manuscripts = new MemoryManuscriptService(source);
        var service = Service(chapter, manuscripts);
        var context = Context(projectId);
        var previewJson = await service.PreviewAsync(
            context,
            chapter.Id,
            source.Revision,
            [new ManuscriptOperationInput(
                "replaceBlockText",
                BlockId: source.Content[0].Id,
                Text: "After")]);
        using var previewDocument = JsonDocument.Parse(previewJson);
        var previewId = previewDocument.RootElement.GetProperty("previewId").GetGuid();
        manuscripts.CurrentDocument = ManuscriptCodec.FromPlainText(
            chapter.Id,
            "Concurrent edit",
            revision: source.Revision);

        var result = await service.ApplyAsync(context, previewId);

        Assert.Contains("is stale", result, StringComparison.Ordinal);
        Assert.Null(manuscripts.ReplacedDocument);
        Assert.Contains(
            "already applied",
            await service.ApplyAsync(context, previewId),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewModeStagesTheExactPreviewWithCompactApprovalArguments()
    {
        var projectId = Guid.NewGuid();
        var chapter = Chapter(projectId);
        var source = ManuscriptCodec.FromPlainText(chapter.Id, "Before", revision: 6);
        var manuscripts = new MemoryManuscriptService(source);
        var repository = new MemoryAiChangeRepository();
        var staging = new EditorChatChangeStagingContext(projectId, Guid.NewGuid(), repository);
        var service = Service(chapter, manuscripts);
        var context = Context(projectId, reviewEdits: true, editorStaging: staging);
        var previewJson = await service.PreviewAsync(
            context,
            chapter.Id,
            source.Revision,
            [new ManuscriptOperationInput(
                "replaceBlockText",
                BlockId: source.Content[0].Id,
                Text: "After")]);
        using var previewDocument = JsonDocument.Parse(previewJson);
        var previewId = previewDocument.RootElement.GetProperty("previewId").GetGuid();
        var argumentsJson = JsonSerializer.Serialize(new { previewId });
        context.BeginToolCall(
            Guid.NewGuid(),
            "apply-call",
            "apply_manuscript_operations",
            argumentsJson);

        var result = await service.ApplyAsync(context, previewId);

        Assert.Contains("\"staged\":true", result, StringComparison.Ordinal);
        Assert.Null(manuscripts.ReplacedDocument);
        Assert.True(staging.TryGetChapterManuscriptDraft(chapter.Id, out var staged));
        Assert.Equal("After", ManuscriptCodec.ProjectPlainText(staged));
        var change = Assert.Single(repository.Changes);
        Assert.Equal(argumentsJson, change.ArgumentsJson);
        var after = JsonSerializer.Deserialize<ChapterManuscriptChange>(change.AfterJson);
        Assert.NotNull(after);
        Assert.True(ManuscriptCodec.ContentEquals(staged, after.Manuscript));
    }

    private static EditorManuscriptPreviewService Service(
        Chapter chapter,
        MemoryManuscriptService manuscripts) =>
        new(
            new MemoryChapterService(chapter),
            manuscripts,
            new MemoryManuscriptStyleService());

    private static Chapter Chapter(Guid projectId) => new()
    {
        ProjectId = projectId,
        Title = "Chapter",
    };

    private static EditorChatContext Context(
        Guid projectId,
        Action? onMutated = null,
        bool reviewEdits = false,
        EditorChatChangeStagingContext? editorStaging = null) =>
        new(
            projectId,
            Guid.NewGuid(),
            currentChapterId: null,
            providerId: 1,
            visionReady: false,
            onMutated ?? (() => { }),
            reviewEdits,
            autoPinReadEntities: false,
            outlineStaging: null,
            editorStaging,
            CancellationToken.None);

    private sealed class MemoryChapterService(Chapter chapter) : IChapterService
    {
        public Task<Chapter?> GetAsync(Guid chapterId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Chapter?>(chapterId == chapter.Id ? chapter : null);

        public Task<IReadOnlyList<Chapter>> ListAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Chapter>>(projectId == chapter.ProjectId ? [chapter] : []);

        public Task<Chapter?> ReloadFromStoreAsync(Guid chapterId, CancellationToken cancellationToken = default) =>
            GetAsync(chapterId, cancellationToken);

        public Task<Chapter> CreateAsync(Guid projectId, Guid? actId = null, string? title = null, string? synopsis = null, Guid? id = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Chapter> UpdateAsync(Guid chapterId, string? title = null, string? synopsis = null, ChapterActAssignment? actId = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(Guid chapterId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReorderAsync(Guid projectId, Guid? actId, IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReindexAsync(Guid chapterId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class MemoryManuscriptService(ManuscriptDocument document) : IManuscriptService
    {
        public ManuscriptDocument CurrentDocument { get; set; } = document;
        public ManuscriptDocument? ReplacedDocument { get; private set; }

        public Task<ManuscriptSnapshot?> GetManuscriptAsync(Guid chapterId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ManuscriptSnapshot?>(Snapshot(CurrentDocument));

        public Task<ManuscriptMutationResult> ReplaceDocumentAsync(Guid chapterId, long expectedRevision, ManuscriptDocument replacement, CancellationToken cancellationToken = default)
        {
            if (CurrentDocument.Revision != expectedRevision)
                throw new ManuscriptRevisionConflictException(expectedRevision, CurrentDocument.Revision);
            ReplacedDocument = replacement with { Revision = expectedRevision + 1 };
            CurrentDocument = ReplacedDocument;
            return Task.FromResult(new ManuscriptMutationResult(
                Snapshot(CurrentDocument),
                CurrentDocument.Content.Select(block => block.Id).ToList()));
        }

        public Task<ManuscriptMutationResult> ApplyAsync(Guid chapterId, long expectedRevision, IReadOnlyList<ManuscriptOperation> operations, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ManuscriptMutationResult> ApplyUnderProjectMutationLeaseAsync(Guid chapterId, long expectedRevision, IReadOnlyList<ManuscriptOperation> operations, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ValidateDocumentReferencesAsync(Guid chapterId, ManuscriptDocument document, IReadOnlyList<ManuscriptStyleView>? styleCatalog = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        private static ManuscriptSnapshot Snapshot(ManuscriptDocument current) => new(
            current.ManuscriptId,
            current.Revision,
            ManuscriptCodec.HashPlainText(ManuscriptCodec.ProjectPlainText(current)),
            ManuscriptCodec.ProjectPlainText(current),
            current);
    }

    private sealed class MemoryManuscriptStyleService : IManuscriptStyleService
    {
        public Task<IReadOnlyList<ManuscriptStyleView>> ListAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ManuscriptStyleView>>([]);

        public Task<ManuscriptStyleView> UpsertAsync(Guid projectId, ManuscriptStyleInput input, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ManuscriptStyleView> PreviewUpsertAsync(Guid projectId, ManuscriptStyleInput input, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(Guid projectId, Guid styleId, long expectedRevision, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ValidateDeleteAsync(Guid projectId, Guid styleId, long expectedRevision, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class MemoryAiChangeRepository : IAiChangeRepository
    {
        public List<AiChange> Changes { get; } = [];

        public Task<List<AiChangeBatch>> ListPendingBatchesAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<AiChangeBatch>());

        public Task<AiChangeBatch?> GetBatchAsync(Guid batchId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AiChangeBatch?>(null);

        public Task<AiChange?> GetChangeAsync(Guid changeId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AiChange?>(Changes.FirstOrDefault(change => change.Id == changeId));

        public Task AddBatchAsync(AiChangeBatch batch, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task AddChangeAsync(AiChange change, CancellationToken cancellationToken = default)
        {
            Changes.Add(change);
            return Task.CompletedTask;
        }

        public void UpdateBatch(AiChangeBatch batch) { }
        public void UpdateChange(AiChange change) { }
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
