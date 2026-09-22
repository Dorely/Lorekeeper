namespace Lorekeeper.Components;

/// <summary>One editor's text and the exact saved state its next save must replace.</summary>
public sealed record NarrativeTextSnapshot(string Title, string Body, long Revision, string ExpectedContent);
