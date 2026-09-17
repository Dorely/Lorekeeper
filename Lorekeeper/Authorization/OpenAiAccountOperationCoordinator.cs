using System.Collections.Concurrent;

namespace Lorekeeper.Authorization;

public sealed class OpenAiAccountOperationCoordinator
{
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _accountGates = new();

    public async Task<T> RunAsync<T>(
        int accountId,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        var gate = _accountGates.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await action(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task RunAsync(
        int accountId,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            accountId,
            async token =>
            {
                await action(token);
                return true;
            },
            cancellationToken);
}
