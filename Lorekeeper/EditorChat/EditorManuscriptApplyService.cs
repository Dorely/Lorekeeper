using System.Text.Json;
using Lorekeeper.Authoring;
using Lorekeeper.Manuscripts;

namespace Lorekeeper.EditorChat;

public sealed class EditorManuscriptApplyService(ManuscriptOperationCommand command)
{
    public async Task<string> ApplyAsync(EditorChatContext context, Guid chapterId,
        long expectedRevision, ManuscriptOperationInput[] operations)
    {
        try
        {
            var result = await command.ApplyAsync(context.ProjectId, context.ContentTarget, chapterId,
                expectedRevision, operations, context.TurnCancellationToken);
            if (result.ChangedBlockCount > 0) context.OnMutated();
            return JsonSerializer.Serialize(result, ManuscriptCodec.JsonOptions);
        }
        catch (AuthoringMutationFenceException exception)
        {
            return $"Error: {exception.Code}: {exception.Message}";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException or InvalidDataException)
        {
            return $"Error: {exception.Message}";
        }
    }
}
