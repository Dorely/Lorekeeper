namespace Lorekeeper.Components.Chat;

/// <summary>
/// Provider-neutral display data for the per-chat model picker.
/// </summary>
public sealed record ChatModelPickerOption(
    int ProviderId,
    string ConnectionLabel,
    string ModelLabel,
    bool IsDefault);
