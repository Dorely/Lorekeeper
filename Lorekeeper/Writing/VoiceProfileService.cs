using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.VersionHistory.Services;

namespace Lorekeeper.Writing;

public sealed class VoiceProfileService(IEntityService entities, IAppDatabaseOperationFactory database, ProjectVersionHistoryUiEvents? historyEvents = null)
{
    public const string PropertyName = "voiceProfile";

    public static string Read(StoryEntity entity) => entity.Properties.GetValueOrDefault(PropertyName) ?? string.Empty;

    public async Task<StoryEntity> SaveAsync(Guid projectId, Guid characterId, string expectedContent, string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedContent);
        ArgumentNullException.ThrowIfNull(content);
        await using var operation = await database.OpenWriteAsync(projectId, cancellationToken);
        operation.ShareWithNestedOperations();
        var character = await entities.GetAsync(projectId, characterId, cancellationToken)
            ?? throw new InvalidOperationException("Character not found in this project.");
        if (character.Type != "Character") throw new InvalidOperationException("Voice profiles belong to Character entities.");
        if (Read(character) != expectedContent) throw new InvalidOperationException("The voice profile changed. Read it again before saving; your draft has been retained.");
        if (content == expectedContent) return character;
        var updated = await entities.UpdateAsync(projectId, characterId,
            propertiesToSet: content.Length == 0 ? null : new Dictionary<string, string?> { [PropertyName] = content },
            propertiesToRemove: content.Length == 0 ? [PropertyName] : null, cancellationToken: cancellationToken);
        await operation.DisposeAsync();
        historyEvents?.PublishReviewStateChanged(projectId);
        return updated;
    }
}
