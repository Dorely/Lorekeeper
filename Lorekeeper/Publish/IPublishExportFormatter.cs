namespace Lorekeeper.Publish;

public interface IPublishExportFormatter
{
    PublishExportFormat Format { get; }
    string FileExtension { get; }
    string ContentType { get; }
    byte[] Render(PublishDocument document);
    Task<byte[]> RenderAsync(PublishDocument document, CancellationToken cancellationToken = default) =>
        Task.FromResult(Render(document));
}
