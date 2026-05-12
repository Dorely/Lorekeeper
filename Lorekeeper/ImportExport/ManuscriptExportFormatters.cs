using System.Text;
using Lorekeeper.Models;

namespace Lorekeeper.ImportExport;

public interface IManuscriptExportFormatter
{
    ManuscriptExportFormat Format { get; }
    string FileExtension { get; }
    string ContentType { get; }
    string Render(Project project, IReadOnlyList<Act> acts, IReadOnlyList<Chapter> chapters);
}

public sealed class PlainTextManuscriptFormatter : IManuscriptExportFormatter
{
    public ManuscriptExportFormat Format => ManuscriptExportFormat.PlainText;
    public string FileExtension => ".txt";
    public string ContentType => "text/plain; charset=utf-8";

    public string Render(Project project, IReadOnlyList<Act> acts, IReadOnlyList<Chapter> chapters)
    {
        var sb = new StringBuilder();
        foreach (var chapter in OrderedChapters(acts, chapters))
        {
            if (sb.Length > 0) sb.AppendLine().AppendLine();
            sb.AppendLine(chapter.Title.Trim());
            sb.AppendLine(new string('=', Math.Max(3, chapter.Title.Trim().Length)));
            sb.AppendLine();
            sb.AppendLine(chapter.Body.TrimEnd());
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    internal static IReadOnlyList<Chapter> OrderedChapters(IReadOnlyList<Act> acts, IReadOnlyList<Chapter> chapters)
    {
        var result = new List<Chapter>();
        foreach (var act in acts.OrderBy(act => act.Order))
        {
            result.AddRange(chapters
                .Where(chapter => chapter.ActId == act.Id)
                .OrderBy(chapter => chapter.Order));
        }

        result.AddRange(chapters
            .Where(chapter => chapter.ActId is null)
            .OrderBy(chapter => chapter.Order));
        return result;
    }
}

public sealed class MarkdownManuscriptFormatter : IManuscriptExportFormatter
{
    public ManuscriptExportFormat Format => ManuscriptExportFormat.Markdown;
    public string FileExtension => ".md";
    public string ContentType => "text/markdown; charset=utf-8";

    public string Render(Project project, IReadOnlyList<Act> acts, IReadOnlyList<Chapter> chapters)
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(EscapeHeading(project.Name));

        foreach (var act in acts.OrderBy(act => act.Order))
        {
            var actChapters = chapters
                .Where(chapter => chapter.ActId == act.Id)
                .OrderBy(chapter => chapter.Order)
                .ToList();
            if (actChapters.Count == 0) continue;

            sb.AppendLine().Append("## ").AppendLine(EscapeHeading(act.Title));
            foreach (var chapter in actChapters)
                AppendChapter(sb, chapter);
        }

        var unassigned = chapters
            .Where(chapter => chapter.ActId is null)
            .OrderBy(chapter => chapter.Order)
            .ToList();
        if (unassigned.Count > 0)
        {
            sb.AppendLine().AppendLine("## Unassigned");
            foreach (var chapter in unassigned)
                AppendChapter(sb, chapter);
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void AppendChapter(StringBuilder sb, Chapter chapter)
    {
        sb.AppendLine().Append("### ").AppendLine(EscapeHeading(chapter.Title));
        sb.AppendLine();
        sb.AppendLine(chapter.Body.TrimEnd());
    }

    private static string EscapeHeading(string heading) =>
        heading.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
}
