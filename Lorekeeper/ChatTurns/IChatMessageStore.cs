namespace Lorekeeper.ChatTurns;

/// <summary>Common persistence boundary implemented by each existing transcript repository.</summary>
public interface IChatMessageStore<TMessage>
{
    Task AddMessageAsync(TMessage message, CancellationToken cancellationToken = default);
    void UpdateMessage(TMessage message);
}
