using Lorekeeper.Llm;
using Microsoft.Extensions.AI;

namespace Lorekeeper.Publish;

public interface IPublishAssistantService
{
    Task<string> AskAsync(
        Guid projectId,
        Guid editionId,
        string prompt,
        CancellationToken cancellationToken = default);
}

public sealed class PublishAssistantService(
    ILlmProviderService providers,
    IChatClientFactory clients,
    PublishAssistantTools tools,
    IPublicationActorContext actorContext) : IPublishAssistantService
{
    public async Task<string> AskAsync(
        Guid projectId,
        Guid editionId,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("Enter a request for the Publish assistant.");
        var availability = await providers.GetDefaultChatProviderAvailabilityAsync(cancellationToken);
        if (!availability.IsAvailable || availability.Provider is null)
            throw new InvalidOperationException(availability.Message);
        actorContext.Actor = "assistant";
        using var chat = (await clients.CreateChatClientAsync(
                availability.Provider.Id,
                cancellationToken))
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();
        var aiTools = await tools.BuildAsync(new PublishAssistantContext(projectId), cancellationToken);
        var messages = new[]
        {
            new ChatMessage(
                ChatRole.System,
                """
                You are Lorekeeper's dedicated Publish assistant. Use the provided tools for every
                edition read or mutation. Copy stable IDs exactly, honor revision tokens, report
                validation failures honestly, and never claim an operation succeeded unless its
                tool result says so. The currently selected edition ID is supplied by the user
                message. Keep the answer concise and identify resulting revisions or diagnostics.
                """),
            new ChatMessage(ChatRole.User, $"Selected edition: {editionId:D}\n\n{prompt.Trim()}"),
        };
        var response = await chat.GetResponseAsync(
            messages,
            new ChatOptions { Tools = aiTools, ToolMode = ChatToolMode.Auto },
            cancellationToken);
        return string.IsNullOrWhiteSpace(response.Text)
            ? "The Publish assistant completed the tool work without a text summary."
            : response.Text;
    }
}
