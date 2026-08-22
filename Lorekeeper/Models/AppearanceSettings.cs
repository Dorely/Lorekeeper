namespace Lorekeeper.Models;

/// <summary>
/// Application-wide theme palette overrides for the light and dark themes. A
/// single row (<see cref="SingletonId"/>) stores only the core tokens the user
/// has customized, as JSON maps of token name to hex color; absent tokens fall
/// back to the built-in palette in app.css.
/// </summary>
public class AppearanceSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public string LightPaletteJson { get; set; } = "{}";

    public string DarkPaletteJson { get; set; } = "{}";
}
