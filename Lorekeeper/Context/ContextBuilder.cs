using Lorekeeper.Models;

namespace Lorekeeper.Context;

public sealed class ContextBuilder : IContextBuilder
{
    public ContextAssembly Build(Project project, Chapter? currentChapter)
    {
        var items = new List<ContextItem>
        {
            new(
                Kind: ContextItemKind.SystemPrompt,
                Label: "System Prompt",
                Body: project.SystemPrompt,
                IsEnabled: true,
                IsRemovable: false),
        };

        if (currentChapter is not null)
        {
            items.Add(new ContextItem(
                Kind: ContextItemKind.CurrentChapter,
                Label: $"Current Chapter — {currentChapter.Title} (line-numbered)",
                Body: ChapterFormatting.WithLineNumbers(currentChapter.Body),
                IsEnabled: project.IncludeCurrentChapterInContext,
                IsRemovable: false));
        }

        return new ContextAssembly(items);
    }
}
