namespace Lorekeeper.EditorChat;

public sealed class EditorChatOptions
{
    public const string SectionName = "Agents:EditorChat";

    public int ReadChapterPageMaxChars { get; set; } = 6000;

    public int MaxToolResultCharsForModel { get; set; } = 12000;
}
