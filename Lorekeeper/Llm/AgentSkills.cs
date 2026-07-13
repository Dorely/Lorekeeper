using Microsoft.Extensions.AI;

namespace Lorekeeper.Llm;

public static class AgentSkillIds
{
    public const string ImageGeneration = "image-generation";
    public const string PicturePageDesign = "picture-page-design";
}

public sealed record AgentSkillDefinition(
    string Id,
    string Description,
    string LoadWhen,
    string Instructions);

public interface IAgentSkillRegistry
{
    IReadOnlyList<AgentSkillDefinition> List();
    AgentSkillDefinition? Find(string skillName);
    string BuildCatalogInstructions();
}

public sealed class BuiltInAgentSkillRegistry : IAgentSkillRegistry
{
    private static readonly IReadOnlyList<AgentSkillDefinition> _skills =
    [
        new(
            AgentSkillIds.ImageGeneration,
            "Construct image-generation and image-edit briefs, use visual references safely, and write result-only alt text.",
            "Load before composing arguments for image generation or editing, including continuity-sensitive illustrations.",
            """
            # Image generation and editing

            Apply this skill before composing image tool arguments. The image model receives the prompt and supplied images, not the surrounding chat. Write for that boundary.

            ## Choose generation or editing
            - Use generation for a new composition. Describe the complete desired result as a standalone image brief.
            - Use editing only when the supplied source image is the canvas to change. State exactly what changes and what remains invariant; use a mask for localized changes when available.
            - Do not smuggle an edit request into generation by asking for the current image to be different, redone, or recreated.

            ## Generation brief
            - Include the use or asset type, subject identity and appearance, concrete action, expression, gaze, pose and body language, environment and era, style and medium, camera/framing, composition, lighting and mood, and output constraints that matter.
            - Every clause must describe visible content or an actionable rendering constraint. The result must stand alone without knowledge of the conversation, current page, previous output, or user correction.
            - Do not use words such as "new", "fresh", "distinct", "different", "same", "current", "previous", "redo", "from scratch", or "not a recreation" as workflow-state comparisons. Translate that intent into observable differences: a specific viewpoint, crop, pose, expression, gaze, action, subject arrangement, setting, time, lighting, or composition.
            - Descriptive uses that name a visible quality are fine, but never rely on them to communicate that the composition should differ from another image.
            - Avoid text, logos, and watermarks unless the user explicitly requests rendered text.

            ## Reference images
            - Generation references are continuity inputs, never implicit edit sources. For each supplied reference, state its role and the exact traits to preserve. Also state the pose, expression, gaze, action, framing, background, layout, lighting, and other shot-specific traits that must come from the target brief rather than the reference.
            - Preserve only traits supported by the approved reference and project context. Do not inherit composition, camera, pose, action, expression, background, or props merely because they appear in a reference.
            - Use approved, grounded reference image ids only. Never use rejected or superseded references.
            - Prefer a tight subject-only crop for character or object identity. Do not use a full scene as an identity reference when the subject can be isolated first.
            - Crops and source images remain independent library assets. Cropping does not inherit, replace, prioritize, suppress, or detach source associations.
            - Ordinary scene outputs and prospective designs remain unattached. Pass entityTargets only for a user-approved or explicitly requested purpose-built reference, and use only grounded entity ids from current context or tool results.
            - Never attach decorative, typographic, mask, layout-only, background, ambiguous, or weakly resembling art as an entity visual example.
            - If the user rejects or replaces a design, detach its entity visual associations, stop using it and its derivatives as references, and continue only from still-approved references. Keep the library image unless deletion is explicitly requested.
            - If the user says "from scratch" and approved identity references are available, ask whether identity-only references should remain before generating. Do not silently keep or remove them. If the user already answered, follow that answer.
            - If the active provider cannot receive images, continue from grounded labels, alt text, prompts, captions, and provenance rather than inventing visual details.

            ## Editing brief
            - Identify the source-image elements or masked region to change and describe their final visible state.
            - Explicitly list important elements outside the edit that must remain unchanged.
            - Additional references preserve only their stated roles; they do not replace the source composition unless explicitly requested.

            ## Alt text
            - Describe only the resulting image: subject, action, setting, and composition needed for accessibility.
            - Do not call the result new, fresh, revised, different, recreated, or otherwise describe its relationship to another image or the conversation.
            """),
        new(
            AgentSkillIds.PicturePageDesign,
            "Design and verify Picture Page composition, typography, image placement, safe areas, reading flow, and rendered fit.",
            "Load before changing a Picture Page mode, text box, image element, placement, or full-page illustration target.",
            """
            # Picture Page design and typesetting

            Apply this skill before mutating Picture Page structure, typography, placement, or page-targeted art.

            ## Composition
            - Treat copy, text geometry, and illustration as one composition. Establish provisional text boxes before generating a full-page background so the image request receives concrete quiet regions.
            - Prefer one clear text landing zone. Use multiple boxes only for deliberate narrative beats, and preserve an obvious language-appropriate reading path.
            - Default multiline prose to left/top alignment. Reserve centered or display treatment for short passages that support it.
            - Keep text at least 0.375 inches from trim edges and from both sides of a spread gutter; prefer 0.5 inches. Keep it away from faces, hands, focal objects, important action, and highly detailed backgrounds.
            - For a new full-page background, generate against the page target, place it as Background, and inspect the newest rendered snapshot. Adjust text geometry or backing panels before regenerating art unless the composition is fundamentally incompatible or the user requests new art.
            - Freeform placement keeps normal centered geometry. Background fills 0,0,100,100 with Cover behind other elements. ReplaceElement preserves the target image element's geometry and layer.

            ## Typography
            - Use an adaptive picture-book baseline: clear type and predictable reading flow by default, larger and simpler treatment for early readers, and expressive display treatment only for short art-led passages.
            - Break lines at natural spoken or syntactic pauses. Avoid widows, orphaned words, dense lines, excessive all-caps, and long italic passages.
            - Use no more than two font families per spread and keep body/accent choices coherent across the book.
            - Target at least 4.5:1 text contrast, or 3:1 only for genuinely large display type. Prefer a translucent solid backing panel when art cannot maintain contrast; shadows and halos are secondary aids.
            - When the user asks text to fill its safe area, do not stop at the first size that fits. Render, inspect heightUtilizationPercent, enlarge in sensible increments until the next increase would overflow, then keep the largest passing size. Aim for roughly 85-95% vertical utilization while also inspecting width, line breaks, hierarchy, and readability in the snapshot.

            ## Verification
            - After every corrective Picture Page mutation, call read_chapter_visual_layout again and inspect the newest rendered snapshot rather than the turn-start image.
            - Do not claim success until textFit.allTextFits is true, all error-level layout diagnostics are clear unless the user explicitly accepts an exception, advisory warnings have been reviewed, and the snapshot has been checked for contrast, hierarchy, focal conflicts, gutter safety, and reading flow.
            - Structural mutation results confirm storage only; they do not prove that the page looks correct.
            """),
    ];

    public IReadOnlyList<AgentSkillDefinition> List() => _skills;

    public AgentSkillDefinition? Find(string skillName)
    {
        var normalized = skillName?.Trim();
        return _skills.FirstOrDefault(skill => string.Equals(skill.Id, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public string BuildCatalogInstructions()
    {
        var lines = new List<string>
        {
            "Specialized skills:",
            "- Detailed specialized workflow rules are loaded on demand with read_skill and are not repeated in this system prompt.",
            "- Call read_skill in a completed tool round before composing arguments for a workflow that requires it. Skills are loaded only for the current user turn.",
            "Available skills:",
        };
        lines.AddRange(_skills.Select(skill => $"- {skill.Id}: {skill.Description} {skill.LoadWhen}"));
        return string.Join(Environment.NewLine, lines);
    }
}

public sealed class AgentSkillSession
{
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);

    public void LoadForNextRound(string skillId) => _pending.Add(skillId);

    public void ActivatePending()
    {
        _active.UnionWith(_pending);
        _pending.Clear();
    }

    public bool IsActive(string skillId) => _active.Contains(skillId);

    public string? Require(params string[] skillIds)
    {
        var missing = skillIds.Where(skillId => !_active.Contains(skillId)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count == 0)
            return null;

        return $"Error: Required skill{(missing.Count == 1 ? string.Empty : "s")} not loaded for this turn: {string.Join(", ", missing)}. Call read_skill for each missing skill in a completed tool round, then retry. No mutation or image job was started.";
    }
}

public sealed class AgentSkillTools(IAgentSkillRegistry registry)
{
    public AITool BuildReadSkillTool(AgentSkillSession session) =>
        AIFunctionFactory.Create(
            method: (string skillName) => ReadSkill(session, skillName),
            name: "read_skill",
            description: "Load one built-in workflow skill for the current user turn. Available skill names are image-generation and picture-page-design. Call this in a completed tool round before composing arguments for tools governed by that skill.");

    private string ReadSkill(AgentSkillSession session, string skillName)
    {
        var skill = registry.Find(skillName);
        if (skill is null)
        {
            var available = string.Join(", ", registry.List().Select(candidate => candidate.Id));
            return $"Error: Unknown skill '{skillName}'. Available skills: {available}.";
        }

        session.LoadForNextRound(skill.Id);
        return $"# Loaded skill: {skill.Id}\n\n{skill.Instructions}\n\nThe skill becomes active after this tool round completes.";
    }
}
