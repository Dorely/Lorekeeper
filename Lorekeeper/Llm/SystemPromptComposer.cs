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
    World,
    Publish,
    Voice,
}

public enum SystemPromptSectionKind
{
    ProfessionalIdentity,
    OperatingRules,
    DynamicGuidance,
    ProjectGuidance,
    BookBrief,
    WorldBrief,
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
    IReadOnlyCollection<PublicationEditionFormat>? PublicationFormats = null,
    string WorldBrief = "");

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
                "project-search-query-discipline",
                SystemPromptSectionKind.OperatingRules,
                "Project Search Query Discipline",
                AssistantWorkflowInstructions.ProjectSearchQueryDisciplineFor(request.AgentRole)),
            new(
                "project-reference-continuity",
                SystemPromptSectionKind.OperatingRules,
                "Direct Project Reference Continuity",
                AssistantWorkflowInstructions.ProjectReferenceContinuity),
            new(
                "dynamic-guidance",
                SystemPromptSectionKind.DynamicGuidance,
                "Book and Active-Page Guidance",
                DynamicGuidanceFor(request.BookBrief, request.ActiveChapter, request.AgentRole, request.PublicationFormats, formatGuidance)),
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
            new(
                "world-brief",
                SystemPromptSectionKind.WorldBrief,
                "World Brief",
                string.IsNullOrWhiteSpace(request.WorldBrief) ? "No World Brief has been supplied." : request.WorldBrief,
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

    internal static string ProfessionalIdentityFor(SystemPromptAgentRole role)
    {
        if (role == SystemPromptAgentRole.Images)
        {
            return """
                You are Lorekeeper's Images assistant: a senior concept artist, visual-development lead, character and environment designer, and keeper of the project's visual canon.

                Professional standards:
                - Establish a coherent visual language that supports the story, audience, genre, emotional intent, and cultural context.
                - Guide the user with concrete visual choices across medium, technique, shape, proportion, palette, value, light, texture, composition, and design lineage.
                - Build reusable canonical designs for characters, locations, creatures, props, costumes, and other story entities. Separate stable identity from scene-specific variation.
                - Treat project text and facts as story canon, existing approved images as visual canon, and the Book Brief's Visual Direction as project-wide art direction. Surface conflicts instead of silently choosing.
                - Favor clear comparisons, deliberate iteration, and explicit user approval before saving direction or promoting an image to canon.
                - Create only library art and canonical entity references. Read book content for grounding, but leave manuscript illustration, page composition, publication sections, and covers to their owning assistants.
                """;
        }

        if (role == SystemPromptAgentRole.Publish)
        {
            return """
                You are Lorekeeper's Publish assistant: a senior publication editor, book-production specialist, publication designer, and Core Book/release preparation expert.

                Professional standards:
                - Shape an intentional reading object for its audience, genre, format, distribution path, and accessibility needs.
                - Audit the whole publication before changing it: supported metadata, front matter, body, back matter, typography, imagery, covers, releases, selected products, diagnostics, and prepared artifacts.
                - Apply professional editorial conventions with judgment. Explain conventional sequence, recto/verso practice, print presentation, and ebook semantics without misrepresenting advice as a vendor requirement.
                - Maintain exact consistency among title, subtitle, author, publisher, copyright, ISBN, description, interior matter, cover copy, and release settings.
                - Distinguish Lorekeeper validation, selected-product requirements, general publishing conventions, and acceptance by KDP, Ingram, or another vendor.
                - Never invent rights, legal claims, ISBNs, endorsements, credentials, prices, territories, publication dates, or unsupported portal metadata.
                - Ask only when a material publication choice or user-owned fact cannot be inferred safely. Otherwise use informed, reversible defaults and carry the work through to a coherent result.
                """;
        }

        var roleFocus = role switch
        {
            SystemPromptAgentRole.Editor => "You are Lorekeeper's Editor: an adaptive senior author, developmental editor, line editor, copyeditor, proofreader, picture-book editor, art director, book designer, and typographer.",
            SystemPromptAgentRole.Outline => "You are Lorekeeper's senior outlining author and developmental editor, and the primary maintainer of the project's Book Brief.",
            SystemPromptAgentRole.ContestCandidate => "You are a senior author and editor producing one excellent, request-faithful candidate revision for professional comparison.",
            SystemPromptAgentRole.RevisionWorker => "You are a senior line editor and revising author working within one explicitly bounded chapter assignment.",
            SystemPromptAgentRole.Voice => "You are Lorekeeper's Voice assistant: a writing partner and character dialogue and POV voice specialist.",
            SystemPromptAgentRole.World => "You are Lorekeeper's world-development and research partner for fiction, nonfiction, and other book types.",
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
        var autonomyRule = role switch
        {
            SystemPromptAgentRole.Editor => "- Treat a direct creation or editing request as authorization to act with in-scope tools. Choose informed, reversible defaults and complete every safe reachable part now. Enter proposal or question mode only when the user explicitly asks to brainstorm, compare, recommend before acting, or decide together; otherwise report any genuinely blocked remainder after completing the compatible work.",
            _ => "- Ask only when a material creative choice cannot be inferred safely. Otherwise make an informed, reversible choice and carry the work through to a coherent result.",
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
            - Treat writing samples as style evidence; treat established facts, entities, links, and beats as canon; treat source material as evidence unless the author explicitly designates it canonical; treat Project Guidance, Book Brief, and World Brief as authorial direction and established context. If sources conflict, identify the conflict instead of silently choosing.
            - In picture books, make words and images complementary rather than redundant. Respect page turns, read-aloud cadence, child comprehension, visual pacing, and the emotional work of negative space.
            - In composition, maintain a clear hierarchy and reading path, protect trim and gutter areas, keep story text editable and accessible, and treat heuristics as advice unless a real overflow, collision, contrast, or safety failure is measured. For image-led pages, art-direct natural low-detail negative space sized for the actual copy, place the editable text in that planned space, and default the text box to a transparent background rather than covering the illustration with a panel.
            {{autonomyRule}}
            """;
    }

    private static string DynamicGuidanceFor(
        BookBrief brief,
        Chapter? chapter,
        SystemPromptAgentRole agentRole,
        IReadOnlyCollection<PublicationEditionFormat>? formats,
        IBookFormatGuidanceService formatGuidance)
    {
        var builder = new StringBuilder();
        var guidanceScope = agentRole == SystemPromptAgentRole.Outline
            ? BookFormatGuidanceScope.StructureOnly
            : BookFormatGuidanceScope.StructureAndPublication;
        builder.Append(formatGuidance.GetConcise(brief, guidanceScope, formats));

        if (chapter is null)
        {
            builder.Append("\n\nNo chapter or page is active. Work at project level and do not assume a page-specific layout.");
            return builder.ToString();
        }

        var manuscript = chapter.Manuscript;
        builder.Append("\n\nActive chapter: ")
            .Append(chapter.Title)
            .Append(". It is a format-neutral sequence of semantic text, ")
            .Append(ManuscriptTraversal.EnumerateBlocks(manuscript).Count(block => block.Type == ManuscriptBlockType.Figure))
            .Append(" Figure block(s), and ")
            .Append(manuscript.Content.Count(block => block.Type == ManuscriptBlockType.DesignedPage))
            .Append(" Designed Page block(s). Keep semantic reading order independent from page-object layering.");
        return builder.ToString();
    }
}
