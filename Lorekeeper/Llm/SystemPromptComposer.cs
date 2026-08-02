using System.Text;
using Lorekeeper.Models;
using Lorekeeper.Projects;
using Lorekeeper.Outline;
using Lorekeeper.Manuscripts;

namespace Lorekeeper.Llm;

public enum SystemPromptAgentRole
{
    Editor,
    Outline,
    ContestCandidate,
    RevisionWorker,
    Images,
    Research,
    Publish,
}

public enum SystemPromptSectionKind
{
    ProfessionalIdentity,
    OperatingRules,
    DynamicGuidance,
    ProjectGuidance,
    BookBrief,
    WorkingContext,
}

public sealed record SystemPromptSourceSection(
    string Key,
    string Label,
    string Body);

public sealed record SystemPromptSection(
    string Key,
    SystemPromptSectionKind Kind,
    string Label,
    string Body,
    bool IsUserOwned = false);

public sealed record SystemPromptComposeRequest(
    Project Project,
    BookBrief BookBrief,
    SystemPromptAgentRole AgentRole,
    string OperatingRules,
    Chapter? ActiveChapter = null,
    IReadOnlyList<SystemPromptSourceSection>? WorkingContext = null,
    IReadOnlyCollection<PublicationEditionFormat>? PublicationFormats = null);

public sealed record SystemPromptComposition(
    string Prompt,
    IReadOnlyList<SystemPromptSection> Sections);

public interface ISystemPromptComposer
{
    SystemPromptComposition Compose(SystemPromptComposeRequest request);
}

/// <summary>
/// Owns the actual system-role instructions. Project Guidance is deliberately only one
/// user-authored section inside the assembled prompt.
/// </summary>
public sealed class SystemPromptComposer(
    IBookBriefService bookBriefs,
    IBookFormatGuidanceService formatGuidance) : ISystemPromptComposer
{
    public SystemPromptComposition Compose(SystemPromptComposeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sections = new List<SystemPromptSection>
        {
            new(
                "professional-identity",
                SystemPromptSectionKind.ProfessionalIdentity,
                "Professional Identity and Quality Charter",
                ProfessionalIdentityFor(request.AgentRole)),
            new(
                "operating-rules",
                SystemPromptSectionKind.OperatingRules,
                "Operating and Tool Rules",
                request.OperatingRules.Trim()),
            new(
                "dynamic-guidance",
                SystemPromptSectionKind.DynamicGuidance,
                "Book and Active-Page Guidance",
                DynamicGuidanceFor(request.BookBrief, request.ActiveChapter, request.PublicationFormats, formatGuidance)),
            new(
                "project-guidance",
                SystemPromptSectionKind.ProjectGuidance,
                "Project Guidance (user authored)",
                string.IsNullOrWhiteSpace(request.Project.ProjectGuidance)
                    ? "No additional Project Guidance has been supplied."
                    : request.Project.ProjectGuidance.Trim(),
                IsUserOwned: true),
            new(
                "book-brief",
                SystemPromptSectionKind.BookBrief,
                "Book Brief",
                bookBriefs.FormatForPrompt(request.BookBrief),
                IsUserOwned: true),
        };

        foreach (var source in request.WorkingContext ?? [])
        {
            if (string.IsNullOrWhiteSpace(source.Body))
                continue;
            sections.Add(new(
                source.Key,
                SystemPromptSectionKind.WorkingContext,
                source.Label,
                source.Body.Trim()));
        }

        return new SystemPromptComposition(Assemble(sections), sections);
    }

    private static string Assemble(IEnumerable<SystemPromptSection> sections)
    {
        var builder = new StringBuilder();
        foreach (var section in sections)
        {
            if (builder.Length > 0)
                builder.Append("\n\n");
            builder.Append("## ").Append(section.Label).Append('\n').Append(section.Body);
        }
        return builder.ToString();
    }

    private static string ProfessionalIdentityFor(SystemPromptAgentRole role)
    {
        var roleFocus = role switch
        {
            SystemPromptAgentRole.Editor => "You are Lorekeeper's Editor: an adaptive senior author, developmental editor, line editor, copyeditor, proofreader, picture-book editor, art director, book designer, and typographer.",
            SystemPromptAgentRole.Outline => "You are Lorekeeper's senior outlining author and developmental editor, and the primary maintainer of the project's Book Brief.",
            SystemPromptAgentRole.ContestCandidate => "You are a senior author and editor producing one excellent, request-faithful candidate revision for professional comparison.",
            SystemPromptAgentRole.RevisionWorker => "You are a senior line editor and revising author working within one explicitly bounded chapter assignment.",
            SystemPromptAgentRole.Images => "You are Lorekeeper's senior picture-book art director, visual-development editor, illustrator brief writer, and book designer.",
            SystemPromptAgentRole.Research => "You are Lorekeeper's rigorous book researcher and editorial fact-development partner.",
            SystemPromptAgentRole.Publish => "You are Lorekeeper's senior book-production collaborator, publication designer, and edition-preparation specialist.",
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

        return $$"""
            {{roleFocus}}

            Select the discipline needed for the user's present request. Do not run every editorial pass at once. Distinguish drafting, critique, developmental editing, line editing, copyediting, proofreading, image creation, and page composition, and work at the requested level.

            Professional standards:
            - Optimize for the intended audience, purpose, medium, desired effect, and the author's stated direction.
            - Preserve meaning, authorial voice, point of view, tense, continuity, and unrelated prose unless the request requires changing them. Make the smallest coherent change that fully accomplishes the goal.
            - For developmental work, evaluate premise and structure, causality, stakes, character agency, scene purpose, pacing, transitions, point of view, and continuity at the appropriate scale.
            - For line work, improve clarity, specificity, rhythm, emphasis, dialogue, paragraph movement, and sentence craft without flattening the voice.
            - For copyediting and proofreading, correct grammar, usage, consistency, spelling, punctuation, factual contradictions, and production errors while avoiding unrequested rewrites.
            - Treat writing samples as style evidence; treat structured facts, entities, links, beats, and directly read source material as canon; treat Project Guidance and the Book Brief as authorial direction. If sources conflict, identify the conflict instead of silently choosing.
            - In picture books, make words and images complementary rather than redundant. Respect page turns, read-aloud cadence, child comprehension, visual pacing, and the emotional work of negative space.
            - In composition, maintain a clear hierarchy and reading path, protect trim and gutter areas, keep story text editable and accessible, and treat heuristics as advice unless a real overflow, collision, contrast, or safety failure is measured. For image-led pages, art-direct natural low-detail negative space sized for the actual copy, place the editable text in that planned space, and default the text box to a transparent background rather than covering the illustration with a panel.
            - Ask only when a material creative choice cannot be inferred safely. Otherwise make an informed, reversible choice and carry the work through to a coherent result.
            """;
    }

    private static string DynamicGuidanceFor(
        BookBrief brief,
        Chapter? chapter,
        IReadOnlyCollection<PublicationEditionFormat>? formats,
        IBookFormatGuidanceService formatGuidance)
    {
        var builder = new StringBuilder();
        builder.Append(formatGuidance.GetConcise(brief, formats));

        if (chapter is null)
        {
            builder.Append("\n\nNo chapter or page is active. Work at project level and do not assume a page-specific layout.");
            return builder.ToString();
        }

        var manuscript = chapter.Manuscript;
        builder.Append("\n\nActive chapter: ")
            .Append(chapter.Title)
            .Append(". It is a format-neutral sequence of semantic text, ")
            .Append(manuscript.Content.Count(block => block.Type == ManuscriptBlockType.Figure))
            .Append(" Figure block(s), and ")
            .Append(manuscript.Content.Count(block => block.Type == ManuscriptBlockType.DesignedPage))
            .Append(" Designed Page block(s). Keep semantic reading order independent from page-object layering.");
        return builder.ToString();
    }
}
