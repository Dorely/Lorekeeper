namespace Lorekeeper.Components.Chat;

/// <summary>
/// Applies one batch of streamed turn updates to a chat panel with a single transcript token recount and a
/// single render. Recounting the whole transcript and system prompt and re-rendering the growing tool
/// arguments for every streamed fragment saturates the shared circuit dispatcher; the authoring flush that a
/// manuscript mutation sends to the open editor then queues behind that backlog until its response times out.
/// </summary>
public sealed class ChatTurnUpdateBatch
{
    private int _depth;
    private bool _tokenRecountPending;

    /// <summary>True while a batch is being applied; the panel's <c>ShouldRender</c> returns false meanwhile.</summary>
    public bool IsApplying => _depth > 0;

    /// <summary>
    /// Records a recount request made during a batch so it runs once when the batch ends. Returns false when no
    /// batch is applying, so the caller recounts immediately.
    /// </summary>
    public bool DeferTokenRecount()
    {
        if (_depth == 0)
            return false;
        _tokenRecountPending = true;
        return true;
    }

    public async Task ApplyAsync<TUpdate>(
        IReadOnlyList<TUpdate> updates,
        Func<TUpdate, Task> apply,
        Action recountTokens,
        Action render)
    {
        _depth++;
        try
        {
            foreach (var update in updates)
                await apply(update);
        }
        finally
        {
            if (--_depth == 0)
            {
                if (_tokenRecountPending)
                {
                    _tokenRecountPending = false;
                    recountTokens();
                }
                render();
            }
        }
    }
}
