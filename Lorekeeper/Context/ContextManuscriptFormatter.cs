using Lorekeeper.Manuscripts;
using Lorekeeper.Models;

namespace Lorekeeper.Context;

internal static class ContextManuscriptFormatter
{
    public static string SerializeCurrentChapter(Chapter chapter, ManuscriptSnapshot? snapshot)
        => AgentManuscriptProjection.SerializeCurrentChapter(chapter, snapshot);

    public static string SerializeStyles(
        IReadOnlyList<ManuscriptStyleView> styles)
        => AgentManuscriptProjection.SerializeStyles(styles);
}
