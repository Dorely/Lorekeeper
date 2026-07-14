namespace Lorekeeper.EditorChat;

public sealed class EditorChatOptions
{
    public const string SectionName = "Agents:EditorChat";

    public int ReadChapterPageMaxChars { get; set; } = 6000;

    public EditorRevisionAgentOptions RevisionAgents { get; set; } = new();
}

public sealed class EditorRevisionAgentOptions
{
    public int MaxConcurrency { get; set; } = 2;

    public int MaxToolIterations { get; set; } = 12;
}
