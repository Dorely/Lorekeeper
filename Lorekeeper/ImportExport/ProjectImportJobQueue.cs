using System.Threading.Channels;

namespace Lorekeeper.ImportExport;

public interface IProjectImportJobQueue
{
    void Enqueue(Guid jobId);
    IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken);
}

public sealed class ProjectImportJobQueue : IProjectImportJobQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    public void Enqueue(Guid jobId) => _channel.Writer.TryWrite(jobId);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
