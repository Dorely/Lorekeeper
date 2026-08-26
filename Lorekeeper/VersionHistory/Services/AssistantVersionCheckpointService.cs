using Lorekeeper.ChatTurns;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.VersionHistory.Services;

public interface IAssistantVersionCheckpointService
{
    Task TryCheckpointAsync(
        Guid projectId,
        ChatTurnSurface surface,
        CancellationToken cancellationToken = default);
}

public sealed class AssistantVersionCheckpointService(
    IProjectVersionHistoryService history,
    IAppDatabaseOperationFactory database,
    ILogger<AssistantVersionCheckpointService> logger) : IAssistantVersionCheckpointService
{
    public async Task TryCheckpointAsync(
        Guid projectId,
        ChatTurnSurface surface,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
            var reviewEditsEnabled = await databaseOperation.Db.Projects
                .AsNoTracking()
                .Where(project => project.Id == projectId)
                .Select(project => (bool?)project.ReviewEditsEnabled)
                .SingleOrDefaultAsync(cancellationToken);
            if (reviewEditsEnabled is null || reviewEditsEnabled.Value)
                return;

            await history.CreateCheckpointAsync(
                projectId,
                ProjectVersionCheckpointKind.Assistant,
                $"Assistant updated the {SurfaceLabel(surface)} workspace.",
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(
                "Assistant checkpoint was canceled for project {ProjectId} after a {Surface} turn.",
                projectId,
                surface);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Assistant checkpoint failed for project {ProjectId} after a {Surface} turn; the completed response remains available.",
                projectId,
                surface);
        }
    }

    private static string SurfaceLabel(ChatTurnSurface surface) => surface switch
    {
        ChatTurnSurface.Editor => "editor",
        ChatTurnSurface.Outline => "outline",
        ChatTurnSurface.WritingCoach => "writing sample",
        ChatTurnSurface.Research => "research",
        ChatTurnSurface.Images => "images",
        ChatTurnSurface.Publish => "publishing",
        _ => "project",
    };
}
