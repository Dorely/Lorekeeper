using Lorekeeper.Models;

namespace Lorekeeper.Outline;

public interface IBookFormatGuidanceService
{
    string GetConcise(
        BookBrief brief,
        BookFormatGuidanceScope scope = BookFormatGuidanceScope.StructureOnly,
        IReadOnlyCollection<PublicationEditionFormat>? formats = null);
    IReadOnlyList<BookFormatGuidanceSection> Read(
        BookBrief brief,
        int offset,
        int limit,
        BookFormatGuidanceScope scope = BookFormatGuidanceScope.StructureOnly,
        IReadOnlyCollection<PublicationEditionFormat>? formats = null);
}

public sealed record BookFormatGuidanceSection(string Key, string Title, string Guidance);

public enum BookFormatGuidanceScope
{
    StructureOnly,
    StructureAndPublication,
}

public sealed class BookFormatGuidanceService : IBookFormatGuidanceService
{
    public string GetConcise(
        BookBrief brief,
        BookFormatGuidanceScope scope = BookFormatGuidanceScope.StructureOnly,
        IReadOnlyCollection<PublicationEditionFormat>? formats = null)
    {
        var selected = Select(brief, scope, formats).Take(4).ToList();
        return string.Join("\n", selected.Select(item => $"- {item.Title}: {item.Guidance}"));
    }

    public IReadOnlyList<BookFormatGuidanceSection> Read(
        BookBrief brief,
        int offset,
        int limit,
        BookFormatGuidanceScope scope = BookFormatGuidanceScope.StructureOnly,
        IReadOnlyCollection<PublicationEditionFormat>? formats = null) =>
        Select(brief, scope, formats).Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 8)).ToList();

    private static IEnumerable<BookFormatGuidanceSection> Select(
        BookBrief brief,
        BookFormatGuidanceScope scope,
        IReadOnlyCollection<PublicationEditionFormat>? formats)
    {
        yield return brief.BookKind switch
        {
            BookKind.Novel or BookKind.Novella or BookKind.ShortStory or BookKind.StoryCollection =>
                new("fiction", "Fiction structure", "Use acts, chapters, scenes, pacing, section breaks, illustrated moments, maps, and ornamental matter only where they serve the narrative. Preserve viewpoint and causal progression."),
            BookKind.NarrativeNonfiction =>
                new("narrative-nonfiction", "Narrative nonfiction", "Coordinate chronology, evidence flow, sidebars, figures, captions, source notes, and image credits. Keep claims traceable and distinguish scene reconstruction from sourced fact."),
            BookKind.GeneralNonfiction =>
                new("reference-nonfiction", "General and reference nonfiction", "Plan heading hierarchy, navigation, lists, diagrams, callouts, indexes, and accessible reading order around the reader's questions and tasks."),
            BookKind.PictureBook =>
                new("picture-book", "Picture-book pacing", "Plan page turns, facing spreads, read-aloud cadence, text-image counterpoint, quiet copy areas, gutter risk, and total page extent. Let images carry action that prose need not repeat."),
            BookKind.IllustratedBook =>
                new("illustrated-book", "Illustrated-book rhythm", scope == BookFormatGuidanceScope.StructureAndPublication
                    ? "Balance flowing Figures, plates, captions, color and bleed strategy, Designed Pages, spreads, and a viable reflow adaptation for EPUB."
                    : "Balance prose and images through planned illustrated moments, plates, captions, page turns, and spreads. Record the intended visual treatment in synopses or beats without prescribing physical geometry."),
            BookKind.Poetry =>
                new("poetry", "Poetry integrity", "Preserve poem boundaries, stanza and intentional line breaks, whitespace, recto starts, ornamental pages, and an explicit logical reading order."),
            _ => new("hybrid", "Hybrid format", "Combine the conventions supported by the Book Brief. A broadly compatible treatment keeps flowing content adaptable while reserving designed structure for moments whose spatial relationship is intrinsic."),
        };
        if (brief.BookKind is BookKind.Unspecified or BookKind.Other && string.IsNullOrWhiteSpace(brief.Genre))
            yield return new("format-uncertainty", "Format uncertainty", "Prefer reversible structure that adapts across likely genres. Identify only uncertainties that materially change structure or visual treatment, and do not force a template.");
        else if (!string.IsNullOrWhiteSpace(brief.Genre))
            yield return new("genre", "Genre and subgenre", $"Use the stated genre/subgenre '{Compact(brief.Genre)}' as supporting context, not a stereotype. Prefer the Book Brief and explicit direction where conventions conflict.");
        if (!string.IsNullOrWhiteSpace(brief.TargetAudience)
            || brief.MinimumReaderAge is not null
            || brief.MaximumReaderAge is not null
            || !string.IsNullOrWhiteSpace(brief.ReadingLevelGuidance))
        {
            var ages = (brief.MinimumReaderAge, brief.MaximumReaderAge) switch
            {
                ({ } minimum, { } maximum) => $" ages {minimum}-{maximum}",
                ({ } minimum, null) => $" age {minimum}+",
                (null, { } maximum) => $" through age {maximum}",
                _ => string.Empty,
            };
            yield return new("audience", "Audience and reading level", $"Calibrate vocabulary, copy density, navigation, type hierarchy, illustration frequency, and page-turn cadence for {Compact(brief.TargetAudience, "the stated audience")}{ages}{Suffix(brief.ReadingLevelGuidance)}.");
        }
        if (brief.TargetWordCount is > 0)
            yield return new("extent", "Target extent", $"Treat {brief.TargetWordCount:N0} words as a planning target, not a quota. Balance section count, chapter rhythm, illustration cadence, and likely page extent without inventing physical dimensions.");
        if (brief.ReadAloudPriority == true)
            yield return new("read-aloud", "Read-aloud", "Protect cadence, breath, page-turn suspense, repetition, and copy density appropriate to the intended reader and listener.");
        if (!string.IsNullOrWhiteSpace(brief.AccessibilityGoals))
            yield return new("accessibility", "Accessibility", "Require alternative text or an intentional decorative decision, preserve semantic text, and specify logical reading order independently from visual layering.");
        if (!string.IsNullOrWhiteSpace(brief.VisualDirection))
            yield return new("visual-direction", "Visual direction", scope == BookFormatGuidanceScope.StructureAndPublication
                ? "Treat the Book Brief's visual direction as the controlling art-direction constraint while testing each visual decision across print, Digital PDF, and EPUB."
                : "Treat the Book Brief's visual direction as the controlling art-direction constraint when planning illustrated moments, page turns, and visual beats.");
        if (!string.IsNullOrWhiteSpace(brief.HouseStyle) || !string.IsNullOrWhiteSpace(brief.CreativeConstraints))
            yield return new("constraints", "House style and constraints", $"Preserve the stated house style and creative constraints as controlling direction: {Compact(string.Join("; ", new[] { brief.HouseStyle, brief.CreativeConstraints }.Where(value => !string.IsNullOrWhiteSpace(value))))}.");
        if (scope == BookFormatGuidanceScope.StructureAndPublication && formats is { Count: > 0 })
        {
            var formatNames = string.Join(", ", formats.Distinct().Order().Select(FormatName));
            yield return new("formats", "Selected publication formats", $"Plan adaptations for the project's current {formatNames} edition(s). Keep flowing content reflowable for EPUB, preserve designed intent in Digital PDF, and require uniform physical leaves for print.");
        }
        if (scope == BookFormatGuidanceScope.StructureAndPublication)
        {
            yield return new("layout-choice", "Layout choice", "Use a flowing Figure when art belongs to nearby prose, a Designed Page when composition is intrinsic, and a facing spread only when the cross-gutter relationship is worth its print and digital adaptation costs.");
            yield return new("geometry", "Geometry discipline", "Read a concrete edition geometry before physical layout or geometry-specific image generation. Reusable and flowing art may remain free-standing; use Lorekeeper targets only when composition must honor a specific page, frame, or cover region.");
        }
    }

    private static string Compact(string value, string fallback = "the stated direction")
    {
        var compact = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length == 0 ? fallback : compact.Length <= 180 ? compact : compact[..177] + "...";
    }

    private static string Suffix(string value) => string.IsNullOrWhiteSpace(value) ? string.Empty : $", with reading-level guidance: {Compact(value)}";
    private static string FormatName(PublicationEditionFormat format) => format switch
    {
        PublicationEditionFormat.DigitalPdf => "Digital PDF",
        PublicationEditionFormat.Epub => "EPUB",
        _ => "paperback",
    };
}
