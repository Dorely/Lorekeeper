using System.Text.Json;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Startup;

namespace Lorekeeper.Appearance;

/// <summary>
/// Core-token overrides for one theme. Null means "use the built-in app.css
/// value"; set values are strict <c>#rrggbb</c> hex colors.
/// </summary>
public sealed record AppearancePalette(
    string? Accent = null,
    string? Canvas = null,
    string? Surface = null,
    string? Ink = null,
    string? Muted = null,
    string? Line = null)
{
    public static readonly AppearancePalette Empty = new();

    public bool HasOverrides =>
        Accent is not null || Canvas is not null || Surface is not null
        || Ink is not null || Muted is not null || Line is not null;
}

public sealed record AppearanceThemeState(AppearancePalette Light, AppearancePalette Dark)
{
    public static readonly AppearanceThemeState Empty = new(AppearancePalette.Empty, AppearancePalette.Empty);
}

public interface IAppearanceService
{
    Task<AppearanceThemeState> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppearanceThemeState state, CancellationToken cancellationToken = default);

    /// <summary>
    /// The custom-palette stylesheet for the document head, or null when nothing
    /// is customized or the database is not ready yet. Cached process-wide.
    /// </summary>
    Task<string?> GetCustomCssAsync(CancellationToken cancellationToken = default);
}

public sealed class AppearanceService(
    IAppDatabaseOperationFactory database,
    IApplicationStartupState startup) : IAppearanceService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppearanceThemeState? _cachedState;
    private string? _cachedCss;

    public async Task<AppearanceThemeState> GetAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        return _cachedState ?? AppearanceThemeState.Empty;
    }

    public async Task SaveAsync(AppearanceThemeState state, CancellationToken cancellationToken = default)
    {
        AppearanceCss.Validate(state);
        await using (var operation = await database.OpenWriteAsync(cancellationToken))
        {
            var existing = await operation.Repositories.AppearanceSettings.GetAsync(cancellationToken);
            if (existing is null)
            {
                existing = new AppearanceSettings();
                await operation.Repositories.AppearanceSettings.AddAsync(existing, cancellationToken);
            }
            else
            {
                operation.Repositories.AppearanceSettings.Update(existing);
            }

            existing.LightPaletteJson = Serialize(state.Light);
            existing.DarkPaletteJson = Serialize(state.Dark);
            await operation.SaveChangesAsync(cancellationToken);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _cachedState = state;
            _cachedCss = AppearanceCss.Build(state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetCustomCssAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        return _cachedCss;
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

            AppearanceSettings? row;
            await using (var operation = await database.OpenReadAsync(cancellationToken))
                row = await operation.Repositories.AppearanceSettings.GetAsync(cancellationToken);

            var state = row is null
                ? AppearanceThemeState.Empty
                : new AppearanceThemeState(Deserialize(row.LightPaletteJson), Deserialize(row.DarkPaletteJson));
            _cachedCss = AppearanceCss.Build(state);
            _cachedState = state;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Serialize(AppearancePalette palette)
    {
        var values = new Dictionary<string, string>();
        Add(values, "accent", palette.Accent);
        Add(values, "canvas", palette.Canvas);
        Add(values, "surface", palette.Surface);
        Add(values, "ink", palette.Ink);
        Add(values, "muted", palette.Muted);
        Add(values, "line", palette.Line);
        return JsonSerializer.Serialize(values);

        static void Add(Dictionary<string, string> values, string key, string? value)
        {
            if (value is not null)
                values[key] = value;
        }
    }

    private static AppearancePalette Deserialize(string json)
    {
        Dictionary<string, string>? values;
        try
        {
            values = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (JsonException)
        {
            return AppearancePalette.Empty;
        }

        if (values is null)
            return AppearancePalette.Empty;
        return new AppearancePalette(
            Read(values, "accent"),
            Read(values, "canvas"),
            Read(values, "surface"),
            Read(values, "ink"),
            Read(values, "muted"),
            Read(values, "line"));

        static string? Read(Dictionary<string, string> values, string key) =>
            values.TryGetValue(key, out var value) && AppearanceCss.IsValidHexColor(value) ? value : null;
    }
}
