using Lorekeeper.Authoring;

namespace Lorekeeper.Publish;

public interface IPublicationActorContext
{
    string Actor { get; set; }
}

public sealed class PublicationActorContext(IAuthoringMutationContextAccessor? mutations = null) : IPublicationActorContext
{
    private string _actor = "user";
    public string Actor
    {
        get => mutations?.Current?.Actor is { Kind: "ExternalClient" } external
            ? $"external:{external.ClientGrantId:N}:work:{external.WorkId:N}" : _actor;
        set => _actor = value;
    }
}
