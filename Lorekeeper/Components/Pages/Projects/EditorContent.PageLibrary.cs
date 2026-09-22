using Lorekeeper.Composition;
using Lorekeeper.Manuscripts;

namespace Lorekeeper.Components.Pages.Projects;

public partial class EditorContent
{
    private sealed record PageInsertion(Guid ProjectId, Guid ChapterId, EditorContentTarget Target, int Index, long Revision, string Label);
    private PageInsertion? _pageInsertion;
    private bool _pageLibraryOpening;

    private async Task OpenPageLibraryAsync()
    {
        if (_pageLibraryOpening || _pageInsertion is not null || Project is null || _current is null)
            return;
        var projectId = Project.Id;
        var chapterId = _current.Id;
        var target = _contentTarget;
        var mode = _mode;
        _pageLibraryOpening = true;
        try
        {
            if (!await FlushAsync()) return;
            var snapshot = await Manuscripts.GetManuscriptAsync(target, chapterId)
                ?? throw new InvalidOperationException("The chapter is no longer available.");
            var index = snapshot.Document.Content.Count;
            var label = "Insert at the end of this chapter";
            if (mode == EditorMode.Edit && _bodyEditor is not null)
            {
                var position = await _bodyEditor.CapturePageInsertionAsync();
                if (position.Revision != snapshot.Revision)
                    throw new InvalidOperationException("The chapter changed. Refresh the manuscript before inserting a page.");
                index = position.Index;
                label = "Insert at the saved manuscript position";
            }
            else if (mode == EditorMode.Pages && _openPlacementBlockId is { } placementId)
            {
                var position = snapshot.Document.Content.ToList().FindIndex(item => item.Id == placementId);
                if (position < 0) throw new InvalidOperationException("The selected placement changed. Reopen the page before inserting.");
                index = position + 1;
                label = "Insert after the selected page";
            }
            if (Project?.Id != projectId || _activeChapterId != chapterId || _contentTarget != target || _mode != mode) return;
            _pageInsertion = new(projectId, chapterId, target, index, snapshot.Revision, label);
            await ApplyEditorReadOnlyAsync(true);
        }
        catch (Exception exception) { _saveError = exception.Message; _saveState = SaveState.SaveFailed; }
        finally { _pageLibraryOpening = false; }
    }

    private async Task InsertLibraryPageAsync(Guid pageId)
    {
        var insertion = _pageInsertion ?? throw new InvalidOperationException("Reopen Insert Page to select a destination.");
        EnsurePageLibraryTarget(insertion);
        var result = await PageLibraryFence.ExecuteAsync(new(insertion.ProjectId, [], "Insert page into chapter"), async (_, token) =>
        {
            EnsurePageLibraryTarget(insertion);
            await PageLibraryContestGuard.EnsureMutationAllowedAsync(insertion.ProjectId, token);
            return await DesignedPages.PlaceAsync(insertion.Target, insertion.ProjectId, pageId,
                DesignedPageContainer.Chapter(insertion.ChapterId), insertion.Index, insertion.Revision, cancellationToken: token);
        });
        // Keep the exact occurrence returned by the mutation, even for repeated shared pages.
        _openPageId = pageId;
        _openPlacementBlockId = result.PlacementId;
        await RefreshPageLibraryConsumersAsync();
        _pageInsertion = null;
        await SetModeAsync(EditorMode.Pages);
    }

    private void EnsurePageLibraryTarget(PageInsertion insertion)
    {
        if (EditorMutationLocked || Project?.Id != insertion.ProjectId || _activeChapterId != insertion.ChapterId || _contentTarget != insertion.Target)
            throw new InvalidOperationException("The Editor target is no longer writable. Close and reopen the page library.");
    }

    private async Task RefreshPageLibraryConsumersAsync()
    {
        await ReconcileCurrentChapterAsync(refreshContextSurfaces: true);
        if (Project is not null)
            HistoryEvents.PublishReviewStateChanged(Project.Id);
        _pageWorkspaceRefreshSignal++;
        _previewRefreshSignal++;
        if (_mode == EditorMode.Pages)
        {
            SelectOpenDesignedPagePlacement();
            if (DesignedPagePlacements.Count == 0) await SetModeAsync(EditorMode.Edit);
        }
    }

    private async Task OpenLibraryPlacementAsync(DesignedPageLibraryModal.LibraryPlacement placement)
    {
        var insertion = _pageInsertion;
        if (insertion is null || !await FlushAsync()) return;
        if (Project?.Id != insertion.ProjectId || _contentTarget != insertion.Target) return;
        await ClosePageLibraryAsync();
        var edition = insertion.Target.EditionId is { } id ? $"&edition={id:D}" : string.Empty;
        Nav.NavigateTo($"/projects/{Project.Slug}/editor/{placement.Container.Id:D}?mode=Pages&page={placement.PageId:D}&placement={Uri.EscapeDataString(placement.BlockId)}{edition}");
    }

    private async Task ClosePageLibraryAsync()
    {
        _pageInsertion = null;
        await ApplyEditorReadOnlyAsync(EditorReadOnly);
    }
}
