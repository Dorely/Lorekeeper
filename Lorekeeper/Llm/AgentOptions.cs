namespace Lorekeeper.Llm;

public sealed class AgentOptions
{
    public const string SectionName = "Agents";

    public int MaxToolIterations { get; set; } = 100;
}