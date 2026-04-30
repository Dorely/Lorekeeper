using System.Threading.Channels;

namespace Lorekeeper.Chapters;

public class StaleChapterNotifier : IStaleChapterNotifier
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    public void Notify(Guid chapterId) => _channel.Writer.TryWrite(chapterId);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
