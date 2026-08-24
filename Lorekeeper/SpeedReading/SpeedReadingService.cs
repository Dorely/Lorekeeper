using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Startup;

namespace Lorekeeper.SpeedReading;

/// <summary>
/// The validated application-wide speed-reading preferences. Style is one of
/// <see cref="SpeedReadingSettings.ChunkStyle"/> or
/// <see cref="SpeedReadingSettings.RsvpStyle"/>, and both per-style presets are
/// clamped to the supported words-per-minute range.
/// </summary>
public sealed record SpeedReadingState(
    string Style,
    int ChunkWordsPerMinute,
    int RsvpWordsPerMinute,
    bool AutoAdvance)
{
    public static readonly SpeedReadingState Default = new(
        SpeedReadingSettings.ChunkStyle,
        SpeedReadingSettings.DefaultChunkWordsPerMinute,
        SpeedReadingSettings.DefaultRsvpWordsPerMinute,
        AutoAdvance: false);

    public int ActiveWordsPerMinute =>
        Style == SpeedReadingSettings.RsvpStyle ? RsvpWordsPerMinute : ChunkWordsPerMinute;

    public SpeedReadingState Normalized()
    {
        var style = Style == SpeedReadingSettings.RsvpStyle
            ? SpeedReadingSettings.RsvpStyle
            : SpeedReadingSettings.ChunkStyle;
        return new SpeedReadingState(
            style,
            ClampWordsPerMinute(ChunkWordsPerMinute, SpeedReadingSettings.DefaultChunkWordsPerMinute),
            ClampWordsPerMinute(RsvpWordsPerMinute, SpeedReadingSettings.DefaultRsvpWordsPerMinute),
            AutoAdvance);
    }

    private static int ClampWordsPerMinute(int value, int fallback)
    {
        if (value == 0)
            return fallback;
        return Math.Clamp(
            value,
            SpeedReadingSettings.MinimumWordsPerMinute,
            SpeedReadingSettings.MaximumWordsPerMinute);
    }
}

public interface ISpeedReadingService
{
    Task<SpeedReadingState> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(SpeedReadingState state, CancellationToken cancellationToken = default);
}

public sealed class SpeedReadingService(
    IAppDatabaseOperationFactory database,
    IApplicationStartupState startup) : ISpeedReadingService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SpeedReadingState? _cachedState;

    public async Task<SpeedReadingState> GetAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        return _cachedState ?? SpeedReadingState.Default;
    }

    public async Task SaveAsync(SpeedReadingState state, CancellationToken cancellationToken = default)
    {
        var normalized = state.Normalized();
        await using (var operation = await database.OpenWriteAsync(cancellationToken))
        {
            var existing = await operation.Repositories.SpeedReadingSettings.GetAsync(cancellationToken);
            if (existing is null)
            {
                existing = new SpeedReadingSettings();
                await operation.Repositories.SpeedReadingSettings.AddAsync(existing, cancellationToken);
            }
            else
            {
                operation.Repositories.SpeedReadingSettings.Update(existing);
            }

            existing.Style = normalized.Style;
            existing.ChunkWordsPerMinute = normalized.ChunkWordsPerMinute;
            existing.RsvpWordsPerMinute = normalized.RsvpWordsPerMinute;
            existing.AutoAdvance = normalized.AutoAdvance;
            await operation.SaveChangesAsync(cancellationToken);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _cachedState = normalized;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_cachedState is not null || !startup.Current.CanUseDatabase)
            return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cachedState is not null)
                return;

            SpeedReadingSettings? row;
            await using (var operation = await database.OpenReadAsync(cancellationToken))
                row = await operation.Repositories.SpeedReadingSettings.GetAsync(cancellationToken);

            _cachedState = row is null
                ? SpeedReadingState.Default
                : new SpeedReadingState(
                    row.Style,
                    row.ChunkWordsPerMinute,
                    row.RsvpWordsPerMinute,
                    row.AutoAdvance).Normalized();
        }
        finally
        {
            _gate.Release();
        }
    }
}
