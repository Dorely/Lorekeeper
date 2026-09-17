namespace Lorekeeper.Models;

[Flags]
public enum LlmModelCapabilities
{
    None = 0,
    TextInput = 1,
    ImageInput = 2,
}
