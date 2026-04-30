using System.ComponentModel;
using System.Text;
using Lorekeeper.Chapters;
using Lorekeeper.Context;
using Lorekeeper.Knowledge;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Microsoft.Extensions.AI;

namespace Lorekeeper.AiConsole;

/// <summary>
/// Builds the set of <see cref="AIFunction"/>s exposed to the LLM during a single
/// AI Console turn. Each tool closure captures the per-request <see cref="AiConsoleContext"/>,
/// keeping all behavior project-scoped without ambient state.
/// </summary>
public sealed class AiConsoleTools(
    IChapterService chapters,
    IVectorStore vectors,
    IEmbeddingService embeddings)
{
    public IList<AITool> Build(AiConsoleContext context)
    {
        return new List<AITool>
        {
            AIFunctionFactory.Create(
                method: (string query, int topK) => VectorSearchAsync(context, query, topK),
                name: "vector_search",
                description: "Semantic search over indexed chapters and lore for the current project. Returns the top matching snippets with their source metadata."),

            AIFunctionFactory.Create(
                method: () => ListChaptersAsync(context),
                name: "list_chapters",
                description: "List every chapter in the current project (id, order, title, synopsis)."),

            AIFunctionFactory.Create(
                method: (Guid chapterId) => ReadChapterAsync(context, chapterId),
                name: "read_chapter",
                description: "Read a chapter's full body with line numbers (0001: ...). Use list_chapters to discover ids."),

            AIFunctionFactory.Create(
                method: (Guid chapterId, string content, int? startLine, int? endLine) =>
                    EditChapterAsync(context, chapterId, content, startLine, endLine),
                name: "edit_chapter",
                description:
                    "Edit a chapter using line-based semantics. " +
                    "If both startLine and endLine are null: replace the entire body with `content`. " +
                    "If only startLine is provided: insert `content` BEFORE that line (1-based). " +
                    "If both startLine and endLine are provided: replace the inclusive line range with `content`. " +
                    "Lines are 1-based and match the numbering shown by read_chapter and the editor gutter. " +
                    "Returns the new line-numbered body and a short change summary."),
        };
    }

    private async Task<string> VectorSearchAsync(
        AiConsoleContext ctx,
        [Description("Natural-language query to embed and search.")] string query,
        [Description("Maximum number of results to return (1-20).")] int topK)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Error: query is required.";
        topK = Math.Clamp(topK, 1, 20);

        var embedding = await embeddings.GenerateEmbeddingAsync(query);
        var results = await vectors.SearchAsync(embedding, Project.ScopeKey(ctx.ProjectId), topK);

        if (results.Count == 0) return "No matches.";

        var sb = new StringBuilder();
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            sb.Append('[').Append(i + 1).Append("] ")
              .Append(r.SourceType).Append('/').Append(r.SourceId ?? "?")
              .Append(" (distance ").Append(r.Distance.ToString("F4")).Append(")\n");
            sb.Append(r.Content).Append("\n\n");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> ListChaptersAsync(AiConsoleContext ctx)
    {
        var list = await chapters.ListAsync(ctx.ProjectId);
        if (list.Count == 0) return "No chapters in this project.";

        var sb = new StringBuilder();
        foreach (var c in list)
        {
            sb.Append(c.Order + 1).Append(". ").Append(c.Title)
              .Append(" — id=").Append(c.Id);
            if (!string.IsNullOrWhiteSpace(c.Synopsis))
                sb.Append(" — ").Append(c.Synopsis);
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> ReadChapterAsync(AiConsoleContext ctx, Guid chapterId)
    {
        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";

        var numbered = ChapterFormatting.WithLineNumbers(chapter.Body);
        return $"# {chapter.Title}\n\n{(numbered.Length == 0 ? "(empty)" : numbered)}";
    }

    private async Task<string> EditChapterAsync(
        AiConsoleContext ctx,
        Guid chapterId,
        string content,
        int? startLine,
        int? endLine)
    {
        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";

        content ??= string.Empty;
        var existingLines = ChapterFormatting.SplitLines(chapter.Body);
        var contentLines = ChapterFormatting.SplitLines(content);

        string newBody;
        string summary;

        if (startLine is null && endLine is null)
        {
            newBody = content;
            summary = $"Full overwrite ({existingLines.Count} → {contentLines.Count} lines).";
        }
        else if (startLine is int s && endLine is null)
        {
            // Insert before startLine. startLine == existingLines.Count + 1 means append.
            if (s < 1 || s > existingLines.Count + 1)
                return $"Error: startLine {s} out of range (1..{existingLines.Count + 1}).";

            var merged = new List<string>(existingLines.Count + contentLines.Count);
            merged.AddRange(existingLines.Take(s - 1));
            merged.AddRange(contentLines);
            merged.AddRange(existingLines.Skip(s - 1));
            newBody = ChapterFormatting.JoinLines(merged);
            summary = $"Inserted {contentLines.Count} line(s) before line {s}.";
        }
        else if (startLine is int s2 && endLine is int e)
        {
            if (s2 < 1 || s2 > existingLines.Count)
                return $"Error: startLine {s2} out of range (1..{existingLines.Count}).";
            if (e < s2 || e > existingLines.Count)
                return $"Error: endLine {e} out of range ({s2}..{existingLines.Count}).";

            var replacedCount = e - s2 + 1;
            var merged = new List<string>(existingLines.Count - replacedCount + contentLines.Count);
            merged.AddRange(existingLines.Take(s2 - 1));
            merged.AddRange(contentLines);
            merged.AddRange(existingLines.Skip(e));
            newBody = ChapterFormatting.JoinLines(merged);
            summary = $"Replaced lines {s2}-{e} ({replacedCount} → {contentLines.Count} lines).";
        }
        else
        {
            return "Error: endLine provided without startLine.";
        }

        await chapters.UpdateAsync(chapterId, body: newBody);
        var newNumbered = ChapterFormatting.WithLineNumbers(newBody);
        return $"OK. {summary}\n\nNew body:\n{(newNumbered.Length == 0 ? "(empty)" : newNumbered)}";
    }
}
