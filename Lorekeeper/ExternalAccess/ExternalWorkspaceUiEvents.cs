namespace Lorekeeper.ExternalAccess;

/// <summary>Signals saved external work without selecting or replacing a user's active surface.</summary>
public sealed class ExternalWorkspaceUiEvents(ILogger<ExternalWorkspaceUiEvents> logger)
{
    public event Action<Guid>? ProjectChanged;

    internal void Publish(Guid projectId)
    {
        foreach (var handler in ProjectChanged?.GetInvocationList() ?? [])
        {
            try { ((Action<Guid>)handler)(projectId); }
            catch (Exception) { logger.LogDebug("An external workspace notification subscriber disconnected."); }
        }
    }
}
