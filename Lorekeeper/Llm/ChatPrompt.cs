namespace Lorekeeper.Llm;

/// <summary>Semantic lifetimes, independent of any provider's cache protocol.</summary>
public enum PromptLifetime
{
    StableApplication,
    StableProject,
    TurnEphemeral,
}

public sealed record ChatPrompt(string Application, string Project, string Turn)
{
    public string Text => string.Join("\n\n", new[] { Application, Project, Turn }.Where(text => !string.IsNullOrWhiteSpace(text)));

    public ChatPrompt WithTurnContext(string text) => this with { Turn = Turn + "\n\n" + text };
}
