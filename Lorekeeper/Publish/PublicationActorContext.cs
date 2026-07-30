namespace Lorekeeper.Publish;

public interface IPublicationActorContext
{
    string Actor { get; set; }
}

public sealed class PublicationActorContext : IPublicationActorContext
{
    public string Actor { get; set; } = "user";
}
