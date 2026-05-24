namespace Lorekeeper.Publish;

public interface IPublishCoverRenderer
{
    PublishAssetDocument Render(
        PublishAssetDocument cover,
        PublishCoverLayoutView layout,
        string title,
        string subtitle,
        string author);
}
