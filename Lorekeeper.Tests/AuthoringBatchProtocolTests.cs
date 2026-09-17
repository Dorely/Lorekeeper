using Lorekeeper.Authoring;
using Lorekeeper.Manuscripts;
using Lorekeeper.Persistence;

namespace Lorekeeper.Tests;

public sealed class AuthoringBatchProtocolTests
{
    [Fact]
    public void BatchHashMatchesCrossLanguageCanonicalGolden()
    {
        var targetId = "chapter:00000000-0000-0000-0000-000000000004";
        var batch = new AuthoringBatchV1(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            Guid.Parse("00000000-0000-0000-0000-000000000003"),
            7,
            "ignored-by-hash",
            "Edit manuscript",
            [new(0, targetId, 2, 3, "base")],
            [new(0, "replaceBlockText", BlockId: "block-1", Text: "Hello")],
            new(
                new("manuscript", [new(targetId, "block-1", 2)]),
                new("manuscript", [new(targetId, "block-1", 5)])));

        Assert.Equal(
            "sha256:ba60f63e638d636aeba2bc9993388490dbdfc2d2cda5bf08130cc513f4ebf5ae",
            AuthoringBatchHash.Compute(batch));
    }

    [Fact]
    public void ReducerDerivesExactCanonicalInverseFromPreMutationState()
    {
        var original = Document(
            Block("a", "Original", new ManuscriptMark { Type = ManuscriptMarkType.Strong }),
            Block("b", "Second"));
        var operation = new AuthoringOperationV1(
            0,
            "replaceBlockText",
            BlockId: "a",
            Text: "Changed",
            ExpectedElementFingerprint: AuthoringBatchReducer.Fingerprint(original.Content[0]));

        var applied = AuthoringBatchReducer.Apply(original, [operation]);
        Assert.Equal("Changed", applied.Document.Content[0].Content[0].Text);
        Assert.Single(applied.CanonicalInverse);
        Assert.Equal("restoreBlock", applied.CanonicalInverse[0].Kind, ignoreCase: true);

        var restored = AuthoringBatchReducer.Apply(
            applied.Document,
            applied.CanonicalInverse,
            allowCanonicalInverseOperations: true).Document;
        Assert.Equal(
            ManuscriptCodec.Serialize(original with { Revision = 0 }),
            ManuscriptCodec.Serialize(restored with { Revision = 0 }));
    }

    [Fact]
    public void ExactPreconditionRebaseRejectsChangedElementAndInsertionAmbiguity()
    {
        var original = Document(Block("a", "Original"));
        var operation = new AuthoringOperationV1(
            0,
            "replaceBlockText",
            BlockId: "a",
            Text: "Changed",
            ExpectedElementFingerprint: AuthoringBatchReducer.Fingerprint(original.Content[0]));
        Assert.True(AuthoringBatchReducer.ExactPreconditionsMatch(original, [operation]));

        var changed = original with { Content = [Block("a", "Concurrent")] };
        Assert.False(AuthoringBatchReducer.ExactPreconditionsMatch(changed, [operation]));
        Assert.False(AuthoringBatchReducer.ExactPreconditionsMatch(
            original,
            [new(0, "insertBlock", BlockId: "new", Index: 1, BlockType: "Paragraph", Text: "New") ]));

        var pair = Document(Block("a", "First"), Block("b", "Second"), Block("c", "Third"));
        var merge = new AuthoringOperationV1(
            0,
            "mergeBlocks",
            BlockId: "a",
            SecondBlockId: "b",
            ExpectedElementFingerprint: AuthoringBatchReducer.Fingerprint(pair.Content[0]),
            ExpectedSecondElementFingerprint: AuthoringBatchReducer.Fingerprint(pair.Content[1]));
        Assert.True(AuthoringBatchReducer.ExactPreconditionsMatch(pair, [merge]));
        Assert.False(AuthoringBatchReducer.ExactPreconditionsMatch(
            pair with { Content = [pair.Content[0], Block("b", "Concurrent"), pair.Content[2]] },
            [merge]));

        var move = new AuthoringOperationV1(
            0,
            "moveBlock",
            BlockId: "a",
            Index: 2,
            ExpectedElementFingerprint: AuthoringBatchReducer.Fingerprint(pair.Content[0]),
            ExpectedOrder: new(
                "c",
                AuthoringBatchReducer.Fingerprint(pair.Content[2]),
                null,
                null));
        Assert.True(AuthoringBatchReducer.ExactPreconditionsMatch(pair, [move]));
        Assert.False(AuthoringBatchReducer.ExactPreconditionsMatch(
            pair with { Content = [pair.Content[0], pair.Content[1], Block("x", "Inserted"), pair.Content[2]] },
            [move]));
    }

    [Fact]
    public void InsertBlockResolvesItsExactAnchorWhenIndexIsOmitted()
    {
        var source = Document(Block("a", "First"), Block("b", "Second"));
        var operation = new AuthoringOperationV1(
            0,
            "insertBlock",
            BlockId: "new",
            BlockType: "Paragraph",
            Text: "Inserted",
            InsertAt: new(BeforeBlockId: "b"),
            ExpectedAnchorFingerprint: AuthoringBatchReducer.Fingerprint(source.Content[1]),
            ExpectedOrder: new(
                "a",
                AuthoringBatchReducer.Fingerprint(source.Content[0]),
                "b",
                AuthoringBatchReducer.Fingerprint(source.Content[1])));

        Assert.True(AuthoringBatchReducer.ExactPreconditionsMatch(source, [operation]));
        var result = AuthoringBatchReducer.Apply(source, [operation]);
        Assert.Equal(["a", "new", "b"], result.Document.Content.Select(item => item.Id));
    }

    [Fact]
    public void CrossContainerPlacementDeltaRoundTripsWithPageIdentity()
    {
        var pageId = Guid.NewGuid();
        var placement = new ManuscriptBlock
        {
            Id = "placement",
            Type = ManuscriptBlockType.DesignedPage,
            StyleRole = ManuscriptStyleRoles.DesignedPage,
            DesignedPageId = pageId,
        };
        var source = Document(Block("before", "Before"), placement);
        var destination = Document(Block("destination", "Destination"));
        var remove = new AuthoringOperationV1(0, "deleteBlock", BlockId: placement.Id);
        var insert = new AuthoringOperationV1(
            1,
            "insertDesignedPagePlacement",
            Index: 1,
            PlacementBlockId: placement.Id,
            PageId: pageId);

        var sourceMutation = AuthoringBatchReducer.Apply(source, [remove]);
        var destinationMutation = AuthoringBatchReducer.Apply(destination, [insert]);
        Assert.Equal(pageId, destinationMutation.Document.Content[1].DesignedPageId);

        var sourceRestored = AuthoringBatchReducer.Apply(
            sourceMutation.Document,
            sourceMutation.CanonicalInverse,
            allowCanonicalInverseOperations: true).Document;
        var destinationRestored = AuthoringBatchReducer.Apply(
            destinationMutation.Document,
            destinationMutation.CanonicalInverse,
            allowCanonicalInverseOperations: true).Document;
        Assert.Equal(
            ManuscriptCodec.Serialize(source with { Revision = 0 }),
            ManuscriptCodec.Serialize(sourceRestored with { Revision = 0 }));
        Assert.Equal(
            ManuscriptCodec.Serialize(destination with { Revision = 0 }),
            ManuscriptCodec.Serialize(destinationRestored with { Revision = 0 }));
    }

    [Fact]
    public void CompoundHistoryUsesParticipantLocalCursorIndexes()
    {
        var history = new AuthoringDeltaHistoryRuntime();
        var generations = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["chapter:a"] = 4,
            ["publication-section:b"] = 2,
        };
        Confirm(history, ["chapter:a"], new Dictionary<string, long> { ["chapter:a"] = 4 }, "Earlier");
        Confirm(history, ["chapter:a", "publication-section:b"], generations, "Move page");

        var undo = history.Undo("publication-section:b");
        Assert.NotNull(undo);
        Assert.Equal("Move page", undo.Action.ActionLabel);
        Assert.True(history.Read("chapter:a").State.CanUndo);
        Assert.True(history.Read("chapter:a").State.CanRedo);
        Assert.False(history.Read("publication-section:b").State.CanUndo);
        Assert.True(history.Read("publication-section:b").State.CanRedo);
        history.ConfirmMove(undo.ReservationId);
    }

    [Fact]
    public void PendingHistoryIsInvisibleUntilConfirmedAndCanBeDiscarded()
    {
        var history = new AuthoringDeltaHistoryRuntime();
        var stage = history.Stage(
            Guid.Empty,
            ["chapter:a"],
            new Dictionary<string, long> { ["chapter:a"] = 0 },
            "Type",
            [new(0, "replaceBlockText", BlockId: "a", Text: "after")],
            [new(0, "replaceBlockText", BlockId: "a", Text: "before")],
            null,
            null);
        Assert.False(history.Read("chapter:a").State.CanUndo);
        Assert.True(stage.Projected.State.CanUndo);
        history.Discard(stage.StageId);
        Assert.False(history.Read("chapter:a").State.CanUndo);

        stage = history.Stage(
            Guid.Empty,
            ["chapter:a"],
            new Dictionary<string, long> { ["chapter:a"] = 0 },
            "Type",
            [new(0, "replaceBlockText", BlockId: "a", Text: "after")],
            [new(0, "replaceBlockText", BlockId: "a", Text: "before")],
            null,
            null);
        _ = history.Confirm(stage.StageId);
        Assert.True(history.Read("chapter:a").State.CanUndo);
    }

    [Fact]
    public void PerTargetActionLimitEvictsOldestConfirmedActions()
    {
        var history = new AuthoringDeltaHistoryRuntime(maxActionsPerTarget: 3, maxProcessHistoryBytes: 1_000_000);
        for (var index = 0; index < 5; index++)
            Confirm(history, ["chapter:a"], new Dictionary<string, long> { ["chapter:a"] = 0 }, $"Edit {index}");

        var cursor = history.Read("chapter:a").Cursor;
        Assert.Equal(3, cursor.Actions.Count);
        Assert.Equal(["Edit 2", "Edit 3", "Edit 4"], cursor.Actions.Select(item => item.ActionLabel));
    }

    [Fact]
    public void BudgetEvictsInactiveLruButNeverActiveOrPendingHistory()
    {
        var probe = new AuthoringDeltaHistoryRuntime(maxProcessHistoryBytes: 1_000_000);
        var probeStage = Stage(probe, "chapter:probe", "Probe", new string('x', 1_000));
        var actionBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            probeStage.Projected.Cursor.Actions.Single(),
            ManuscriptCodec.JsonOptions).LongLength;
        probe.Discard(probeStage.StageId);

        var history = new AuthoringDeltaHistoryRuntime(maxProcessHistoryBytes: actionBytes + 64);
        _ = history.Confirm(Stage(history, "chapter:old", "Old", new string('x', 1_000)).StageId);
        var pending = Stage(history, "chapter:new", "New", new string('x', 1_000));
        Assert.True(pending.Projected.Undoable);
        Assert.True(history.Read("chapter:old").State.CanUndo);
        _ = history.Confirm(pending.StageId);
        Assert.False(history.Read("chapter:old").State.CanUndo);
        Assert.True(history.Read("chapter:new").State.CanUndo);

        history = new AuthoringDeltaHistoryRuntime(maxProcessHistoryBytes: actionBytes + 64);
        _ = history.Confirm(Stage(history, "chapter:active", "Active", new string('x', 1_000)).StageId);
        history.SetActive("chapter:active", active: true);
        var rejected = Stage(history, "chapter:new", "New", new string('x', 1_000));
        Assert.False(rejected.Projected.Undoable);
        Assert.True(history.Read("chapter:active").State.CanUndo);
        _ = history.Confirm(rejected.StageId);
        Assert.False(history.Read("chapter:new").State.CanUndo);
        Assert.True(history.Read("chapter:active").State.CanUndo);

        history.Clear("chapter:active");
        _ = history.Confirm(Stage(history, "chapter:active", "Active after clear", new string('x', 1_000)).StageId);
        var rejectedAfterClear = Stage(history, "chapter:other", "Other", new string('x', 1_000));
        Assert.False(rejectedAfterClear.Projected.Undoable);
        Assert.True(history.Read("chapter:active").State.CanUndo);
        history.Discard(rejectedAfterClear.StageId);
    }

    [Fact]
    public void OversizedActionCommitsAsNonUndoableAndClearsItsTarget()
    {
        var history = new AuthoringDeltaHistoryRuntime(maxProcessHistoryBytes: 256);
        var stage = Stage(history, "chapter:a", "Too large", new string('x', 1_000));
        Assert.False(stage.Projected.Undoable);
        var confirmed = history.Confirm(stage.StageId);
        Assert.False(confirmed.Undoable);
        Assert.False(history.Read("chapter:a").State.CanUndo);
    }

    [Fact]
    public void DesignedPageTargetsIndexTheirOwnPageDependency()
    {
        var history = new AuthoringDeltaHistoryRuntime();
        var projectId = Guid.NewGuid();
        var pageId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var coreTarget = $"designed-page-content:{pageId:D}";
        var releaseTarget = $"release:{editionId:D}:designed-page-content:{pageId:D}";
        foreach (var target in new[] { coreTarget, releaseTarget })
        {
            var stage = history.Stage(
                projectId,
                [target],
                new Dictionary<string, long> { [target] = 0 },
                "Edit page",
                [new(0, "insertCanvasObject", ObjectId: Guid.NewGuid(), ObjectIndex: 0)],
                [new(0, "removeCanvasObject", ObjectId: Guid.NewGuid())],
                null,
                null);
            _ = history.Confirm(stage.StageId);
        }

        Assert.Equal(
            [coreTarget, releaseTarget],
            history.FindDependentTargets(
                projectId,
                AuthoringHistoryDependencyKind.DesignedPage,
                pageId));
    }

    [Fact]
    public async Task WriterLeaseIsExclusiveAndDirtyUnreachableWriterBlocksFence()
    {
        var projectId = Guid.NewGuid();
        var target = new AuthoringTargetReferenceV1(projectId, $"chapter:{Guid.NewGuid():D}");
        var sessionId = Guid.NewGuid();
        var history = new AuthoringDeltaHistoryRuntime();
        var fence = new AuthoringMutationFence(null!, null!, history);
        var registration = new AuthoringWriterRegistration(
            target,
            sessionId,
            Guid.NewGuid(),
            (sequence, _) => Task.FromResult(new AuthoringWriterFlushResult(sequence, false, true, true)),
            _ => Task.CompletedTask);

        await using var lease = await fence.RegisterWriterAsync(registration);
        var duplicate = await Assert.ThrowsAsync<AuthoringMutationFenceException>(async () =>
            await fence.RegisterWriterAsync(registration));
        Assert.Equal("AUTHORING_WRITER_ACTIVE", duplicate.Code);

        fence.UpdateWriterState(new(
            target,
            sessionId,
            registration.WriterId,
            Generation: 0,
            HighestLocalSequence: 4,
            LastDispatchedSequence: 3,
            LastAcknowledgedSequence: 2,
            IsDirty: true,
            IsRecoverable: true,
            IsReachable: false));
        var blocked = await Assert.ThrowsAsync<AuthoringMutationFenceException>(() => fence.ExecuteAsync(
            new(projectId, [target.TargetId], "test"),
            (_, _) => Task.FromResult(true)));
        Assert.Equal("AUTHORING_CLIENT_UNREACHABLE", blocked.Code);
    }

    [Fact]
    public async Task OverlappingFenceCannotFreezeUntilThePriorResumeCompletes()
    {
        var projectId = Guid.NewGuid();
        var target = new AuthoringTargetReferenceV1(projectId, $"chapter:{Guid.NewGuid():D}");
        var resumeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var freezeCalls = 0;
        var resumeCalls = 0;
        var fence = new AuthoringMutationFence(null!, null!, new AuthoringDeltaHistoryRuntime());
        var registration = new AuthoringWriterRegistration(
            target,
            Guid.NewGuid(),
            Guid.NewGuid(),
            (sequence, _) =>
            {
                Interlocked.Increment(ref freezeCalls);
                return Task.FromResult(new AuthoringWriterFlushResult(sequence, false, true, true));
            },
            async _ =>
            {
                if (Interlocked.Increment(ref resumeCalls) != 1)
                    return;
                resumeStarted.TrySetResult();
                await releaseResume.Task;
            });
        await using var lease = await fence.RegisterWriterAsync(registration);

        var first = fence.ExecuteAsync(
            new(projectId, [target.TargetId], "first"),
            (_, _) => Task.FromResult(true));
        await resumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        using var cancellation = new CancellationTokenSource();
        var overlapping = fence.ExecuteAsync(
            new(projectId, [target.TargetId], "overlapping"),
            (_, _) => Task.FromResult(true),
            cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await overlapping);
        Assert.Equal(1, Volatile.Read(ref freezeCalls));

        releaseResume.TrySetResult();
        await Assert.ThrowsAsync<NullReferenceException>(async () => await first);
        Assert.Equal(1, Volatile.Read(ref resumeCalls));

        await Assert.ThrowsAsync<NullReferenceException>(() => fence.ExecuteAsync(
            new(projectId, [target.TargetId], "after resume"),
            (_, _) => Task.FromResult(true)));
        Assert.Equal(2, Volatile.Read(ref freezeCalls));
        Assert.Equal(2, Volatile.Read(ref resumeCalls));
    }

    [Fact]
    public async Task ProjectMutationLeaseCanBeExplicitlySharedWithSameProjectOnly()
    {
        var projectId = Guid.NewGuid();
        var coordinator = new ProjectMutationCoordinator();
        await using var outer = await coordinator.AcquireAsync(projectId);
        using var shared = coordinator.ShareWithNestedOperations(projectId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await using var nested = await coordinator.AcquireAsync(projectId, timeout.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await coordinator.AcquireAsync(Guid.NewGuid(), timeout.Token));
    }

    private static void Confirm(
        AuthoringDeltaHistoryRuntime history,
        IReadOnlyList<string> targets,
        IReadOnlyDictionary<string, long> generations,
        string label)
    {
        var stage = history.Stage(
            Guid.Empty,
            targets,
            generations,
            label,
            [new(0, "replaceBlockText", BlockId: "a", Text: "after")],
            [new(0, "replaceBlockText", BlockId: "a", Text: "before")],
            null,
            null);
        _ = history.Confirm(stage.StageId);
    }

    private static AuthoringDeltaHistoryStage Stage(
        AuthoringDeltaHistoryRuntime history,
        string target,
        string label,
        string text) => history.Stage(
            Guid.Empty,
            [target],
            new Dictionary<string, long> { [target] = 0 },
            label,
            [new(0, "replaceBlockText", BlockId: "a", Text: text)],
            [new(0, "replaceBlockText", BlockId: "a", Text: "before")],
            null,
            null);

    private static ManuscriptDocument Document(params ManuscriptBlock[] blocks) => new()
    {
        ManuscriptId = Guid.Parse("10000000-0000-0000-0000-000000000001"),
        Revision = 5,
        Content = blocks.ToList(),
    };

    private static ManuscriptBlock Block(string id, string text, params ManuscriptMark[] marks) => new()
    {
        Id = id,
        Type = ManuscriptBlockType.Paragraph,
        StyleRole = ManuscriptStyleRoles.Body,
        Content = [new ManuscriptInline { Text = text, Marks = marks.ToList() }],
    };
}
