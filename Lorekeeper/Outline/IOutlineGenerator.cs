namespace Lorekeeper.Outline;

/// <summary>
/// Inputs for an AI outline generation pass. All fields except <see cref="Premise"/> are optional.
/// </summary>
public sealed record OutlineBrief(
    string Premise,
    string? Tone = null,
    string? Scope = null,
    IReadOnlyList<string>? MainCharacters = null,
    string? CoreConflict = null,
    string? Setting = null);

/// <summary>An AI-proposed outline. Transient — nothing is persisted until <c>ApplyAsync</c>.</summary>
public sealed record GeneratedOutline(IReadOnlyList<GeneratedAct> Acts);

public sealed record GeneratedAct(string Title, string Synopsis, IReadOnlyList<GeneratedChapter> Chapters);

public sealed record GeneratedChapter(string Title, string Synopsis);

public interface IOutlineGenerator
{
    /// <summary>
    /// One-shot structured-output call against the project's default LLM provider.
    /// Returns a transient outline — call <see cref="ApplyAsync"/> to persist.
    /// </summary>
    Task<GeneratedOutline> GenerateAsync(Guid projectId, OutlineBrief brief, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically persists a generated outline as <c>Act</c> rows + chapter stubs in order.
    /// Appends to the existing outline (does not clear). Returns the created act ids in order.
    /// </summary>
    Task<IReadOnlyList<Guid>> ApplyAsync(Guid projectId, GeneratedOutline outline, CancellationToken cancellationToken = default);
}
