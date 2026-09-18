using System.Security.Cryptography;
using System.Text;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Authoring;

internal sealed record ParsedAuthoringTarget(
    AuthoringHistoryTarget HistoryTarget,
    EditorContentTarget ContentTarget);

internal static class AuthoringPersistence
{
    public static ParsedAuthoringTarget ParseTarget(Guid projectId, string targetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var parts = targetId.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts is ["chapter", var chapter] && Guid.TryParse(chapter, out var chapterId))
            return new(new(projectId, AuthoringHistoryDocumentKind.CoreChapter, chapterId), EditorContentTarget.Core);
        if (parts is ["publication-section", var section] && Guid.TryParse(section, out var sectionId))
            return new(new(projectId, AuthoringHistoryDocumentKind.PublicationSection, sectionId), EditorContentTarget.Core);
        if (parts is ["designed-page-content", var content] && Guid.TryParse(content, out var contentId))
            return new(new(projectId, AuthoringHistoryDocumentKind.DesignedPageContent, contentId), EditorContentTarget.Core);
        if (parts is ["core-cover", var cover] && Guid.TryParse(cover, out var coverId))
            return new(new(projectId, AuthoringHistoryDocumentKind.CoreCover, coverId), EditorContentTarget.Core);
        if (parts is ["release", var edition, "chapter", var releaseChapter]
            && Guid.TryParse(edition, out var editionId)
            && Guid.TryParse(releaseChapter, out var releaseChapterId))
        {
            return new(
                new(projectId, AuthoringHistoryDocumentKind.EditionChapter, releaseChapterId, editionId),
                EditorContentTarget.ForEdition(editionId));
        }
        if (parts is ["release", var release, "section", var releaseSection]
            && Guid.TryParse(release, out var releaseId)
            && Guid.TryParse(releaseSection, out var releaseSectionId))
        {
            return new(
                new(projectId, AuthoringHistoryDocumentKind.PublicationSection, releaseSectionId, releaseId),
                EditorContentTarget.ForEdition(releaseId));
        }
        if (parts is ["release", var pageRelease, "designed-page-content", var releaseContent]
            && Guid.TryParse(pageRelease, out var pageReleaseId)
            && Guid.TryParse(releaseContent, out var releaseContentId))
        {
            return new(
                new(projectId, AuthoringHistoryDocumentKind.DesignedPageContent, releaseContentId, pageReleaseId),
                EditorContentTarget.ForEdition(pageReleaseId));
        }
        if (parts is ["release", var coverRelease, "cover", var releaseCover]
            && Guid.TryParse(coverRelease, out var coverReleaseId)
            && Guid.TryParse(releaseCover, out var releaseCoverId))
        {
            return new(
                new(projectId, AuthoringHistoryDocumentKind.ReleaseCover, releaseCoverId, coverReleaseId),
                EditorContentTarget.ForEdition(coverReleaseId));
        }

        throw new ArgumentException($"Unsupported authoring target ID '{targetId}'.", nameof(targetId));
    }

    public static async Task<AuthoringCanonicalTargetStateV1> ReadTargetAsync(
        AppDbContext db,
        Guid projectId,
        string targetId,
        string selectionJson,
        CancellationToken cancellationToken)
    {
        var parsed = ParseTarget(projectId, targetId);
        ManuscriptDocument document;
        switch (parsed.HistoryTarget.Kind)
        {
            case AuthoringHistoryDocumentKind.CoreChapter:
            {
                var chapter = await db.Chapters.AsNoTracking().SingleOrDefaultAsync(
                    item => item.ProjectId == projectId && item.Id == parsed.HistoryTarget.DocumentId,
                    cancellationToken) ?? throw new KeyNotFoundException("The authoring chapter was not found.");
                document = ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision);
                break;
            }
            case AuthoringHistoryDocumentKind.EditionChapter when parsed.HistoryTarget.EditionId is Guid editionId:
            {
                var chapter = await db.Chapters.AsNoTracking().SingleOrDefaultAsync(
                    item => item.ProjectId == projectId && item.Id == parsed.HistoryTarget.DocumentId,
                    cancellationToken) ?? throw new KeyNotFoundException("The authoring chapter was not found.");
                var chapterOverride = await db.PublicationEditionChapterOverrides.AsNoTracking().SingleOrDefaultAsync(
                    item => item.EditionId == editionId && item.ChapterId == chapter.Id,
                    cancellationToken);
                document = chapterOverride is null
                    ? ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision)
                    : ManuscriptCodec.Deserialize(chapterOverride.ManuscriptJson, chapter.Id, chapterOverride.Revision);
                break;
            }
            case AuthoringHistoryDocumentKind.PublicationSection:
            {
                var section = await db.PublicationSections.AsNoTracking().SingleOrDefaultAsync(
                    item => item.ProjectId == projectId
                        && item.Id == parsed.HistoryTarget.DocumentId
                        && item.EditionId == parsed.HistoryTarget.EditionId,
                    cancellationToken) ?? throw new KeyNotFoundException("The authoring publication section was not found.");
                document = ManuscriptCodec.Deserialize(section.ManuscriptJson, section.Id, section.Revision);
                break;
            }
            case AuthoringHistoryDocumentKind.DesignedPageContent:
            {
                var content = await db.DesignedPageContents.AsNoTracking().SingleOrDefaultAsync(
                    item => item.ProjectId == projectId
                        && item.Id == parsed.HistoryTarget.DocumentId
                        && item.EditionId == parsed.HistoryTarget.EditionId,
                    cancellationToken) ?? throw new KeyNotFoundException("The authoring Designed Page content was not found.");
                document = ManuscriptCodec.Deserialize(content.SemanticManuscriptJson, content.Id, content.Revision);
                break;
            }
            default:
                throw new InvalidOperationException("The requested authoring target is not a manuscript target.");
        }

        var manuscriptJson = ManuscriptCodec.Serialize(document);
        var generation = await db.AuthoringTargetGenerations.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.TargetId == targetId)
            .Select(item => (long?)item.Generation)
            .SingleOrDefaultAsync(cancellationToken) ?? 0;
        return new(
            targetId,
            document.Revision,
            generation,
            Fingerprint(manuscriptJson),
            manuscriptJson,
            selectionJson,
            ManuscriptTraversal.EnumerateBlocks(document).ToDictionary(
                item => item.Id,
                AuthoringBatchReducer.Fingerprint,
                StringComparer.Ordinal));
    }

    public static async Task<Dictionary<string, long>> ReadGenerationsAsync(
        AppDbContext db,
        Guid projectId,
        IReadOnlyCollection<string> targetIds,
        CancellationToken cancellationToken)
    {
        var values = await db.AuthoringTargetGenerations.AsNoTracking()
            .Where(item => item.ProjectId == projectId && targetIds.Contains(item.TargetId))
            .ToDictionaryAsync(item => item.TargetId, item => item.Generation, StringComparer.Ordinal, cancellationToken);
        foreach (var targetId in targetIds)
            values.TryAdd(targetId, 0);
        return values;
    }

    public static async Task<long> IncrementGenerationAsync(
        AppDbContext db,
        Guid projectId,
        string targetId,
        CancellationToken cancellationToken)
    {
        var row = await db.AuthoringTargetGenerations.SingleOrDefaultAsync(
            item => item.ProjectId == projectId && item.TargetId == targetId,
            cancellationToken);
        if (row is null)
        {
            row = new AuthoringTargetGeneration { ProjectId = projectId, TargetId = targetId, Generation = 1 };
            db.AuthoringTargetGenerations.Add(row);
        }
        else
        {
            row.Generation = checked(row.Generation + 1);
            row.UpdatedAt = DateTime.UtcNow;
        }
        return row.Generation;
    }

    public static string Fingerprint(string canonicalJson) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant();
}
