namespace Lorekeeper.Publish;

public interface IPublishExportFormatter
{
    PublishExportFormat Format { get; }
    string FileExtension { get; }
    string ContentType { get; }
    byte[] Render(PublishDocument document);
}
