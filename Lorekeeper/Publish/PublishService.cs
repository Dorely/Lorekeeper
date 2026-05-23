using System.Text.Json;
using Lorekeeper.ImportExport;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Publish;

public sealed class PublishService(
    AppDbContext db,
    ICodexImageGenerationService codexImages,
    IEnumerable<IPublishExportFormatter> formatters) : IPublishService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<PublishWorkspaceView> GetWorkspaceAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        var profile = await EnsureProfileAsync(project, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var acts = await db.Acts.Where(act => act.ProjectId == projectId).OrderBy(act => act.Order).ToListAsync(cancellationToken);
        var chapters = await db.Chapters.Where(chapter => chapter.ProjectId == projectId).OrderBy(chapter => chapter.Order).ToListAsync(cancellationToken);
        var selections = await db.PublishOutlineSelections.Where(selection => selection.ProjectId == projectId).ToListAsync(cancellationToken);
        var assets = await db.PublishAssets.Where(asset => asset.ProjectId == projectId).OrderByDescending(asset => asset.CreatedAt).ToListAsync(cancellationToken);
        var placements = await db.PublishImagePlacements
            .Include(placement => placement.Asset)
            .Where(placement => placement.ProjectId == projectId)
            .OrderBy(placement => placement.TargetKind)
            .ThenBy(placement => placement.SortOrder)
            .ToListAsync(cancellationToken);

        return new PublishWorkspaceView(
            ProfileView(project, profile),
            SectionViews(acts, chapters, selections),
            assets.Select(AssetView).ToList(),
            placements.Select(placement => PlacementView(placement, acts, chapters)).ToList());
    }

    public async Task SaveProfileAsync(Guid projectId, PublishProfileUpdate update, CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        var profile = await EnsureProfileAsync(project, cancellationToken);
        profile.TitleOverride = Clean(update.TitleOverride);
        profile.Subtitle = Clean(update.Subtitle);
        profile.Author = Clean(update.Author);
        profile.Language = string.IsNullOrWhiteSpace(update.Language) ? "en" : Clean(update.Language);
        profile.Publisher = Clean(update.Publisher);
        profile.Copyright = Clean(update.Copyright);
        profile.Isbn = Clean(update.Isbn);
        profile.Description = Clean(update.Description);
        profile.Dedication = Clean(update.Dedication);
        profile.Acknowledgments = Clean(update.Acknowledgments);
        profile.References = Clean(update.References);
        profile.IncludeTableOfContents = update.IncludeTableOfContents;
        profile.IncludeVisibleTableOfContents = update.IncludeVisibleTableOfContents;
        profile.IncludeActSynopses = update.IncludeActSynopses;
        profile.IncludeChapterSynopses = update.IncludeChapterSynopses;
        profile.IncludeActHeadings = update.IncludeActHeadings;
        profile.IncludeChapterHeadings = update.IncludeChapterHeadings;
        profile.NumberActs = update.NumberActs;
        profile.NumberChapters = update.NumberChapters;
        Touch(profile, project);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetCoverAssetAsync(Guid projectId, Guid? assetId, CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        var profile = await EnsureProfileAsync(project, cancellationToken);
        if (assetId is Guid id && !await db.PublishAssets.AnyAsync(asset => asset.ProjectId == projectId && asset.Id == id, cancellationToken))
            throw new InvalidOperationException("Cover asset was not found.");

        profile.SelectedCoverAssetId = assetId;
        Touch(profile, project);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetOutlineSelectionAsync(
        Guid projectId,
        PublishOutlineTargetKind targetKind,
        Guid targetId,
        bool isIncluded,
        CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        await EnsureTargetExistsAsync(projectId, targetKind, targetId, cancellationToken);
        var selection = await db.PublishOutlineSelections
            .FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId
                && candidate.TargetKind == targetKind
                && candidate.TargetId == targetId, cancellationToken);

        if (selection is null)
        {
            selection = new PublishOutlineSelection
            {
                ProjectId = projectId,
                TargetKind = targetKind,
                TargetId = targetId,
                IsIncluded = isIncluded,
            };
            await db.PublishOutlineSelections.AddAsync(selection, cancellationToken);
        }
        else
        {
            selection.IsIncluded = isIncluded;
            selection.UpdatedAt = DateTime.UtcNow;
        }

        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PublishAssetView> UploadAssetAsync(Guid projectId, PublishAssetUpload upload, CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        var contentType = NormalizeImageContentType(upload.ContentType);
        if (contentType is null)
            throw new InvalidOperationException("Only PNG and JPEG images can be used as publish assets.");
        if (upload.Data.Length == 0)
            throw new InvalidOperationException("Image file is empty.");

        var asset = new PublishAsset
        {
            ProjectId = projectId,
            Source = PublishAssetSource.Uploaded,
            FileName = string.IsNullOrWhiteSpace(upload.FileName) ? $"asset-{DateTime.UtcNow:yyyyMMddHHmmss}.{ExtensionForContentType(contentType)}" : upload.FileName.Trim(),
            ContentType = contentType,
            Data = upload.Data,
            AltText = Clean(upload.AltText),
        };

        await db.PublishAssets.AddAsync(asset, cancellationToken);
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return AssetView(asset);
    }

    public async Task<PublishAssetView> GenerateImageAsync(
        Guid projectId,
        PublishImageGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new InvalidOperationException("Image prompt is required.");

        var generated = await codexImages.GenerateAsync(new CodexImageGenerationOptions(
            request.Prompt.Trim(),
            string.IsNullOrWhiteSpace(request.Size) ? "auto" : request.Size.Trim(),
            string.IsNullOrWhiteSpace(request.Quality) ? "auto" : request.Quality.Trim(),
            string.IsNullOrWhiteSpace(request.OutputFormat) ? "png" : request.OutputFormat.Trim(),
            request.OutputCompression), cancellationToken);

        var fileName = $"generated-{DateTime.UtcNow:yyyyMMddHHmmss}.{ExtensionForContentType(generated.ContentType)}";
        var asset = new PublishAsset
        {
            ProjectId = projectId,
            Source = PublishAssetSource.Generated,
            FileName = fileName,
            ContentType = generated.ContentType,
            Data = generated.Data,
            AltText = Clean(request.AltText),
            Prompt = request.Prompt.Trim(),
            GenerationModel = generated.ImageModel,
            SourceMetadataJson = JsonSerializer.Serialize(new
            {
                generated.MainlineModel,
                generated.ImageModel,
                generated.OutputFormat,
                generated.RevisedPrompt,
                generated.ResponseId,
                generated.CallId,
            }, JsonOptions),
        };

        await db.PublishAssets.AddAsync(asset, cancellationToken);
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return AssetView(asset);
    }

    public async Task DeleteAssetAsync(Guid projectId, Guid assetId, CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        var asset = await db.PublishAssets.FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == assetId, cancellationToken);
        if (asset is null) return;

        foreach (var profile in await db.PublishProfiles.Where(profile => profile.ProjectId == projectId && profile.SelectedCoverAssetId == assetId).ToListAsync(cancellationToken))
            profile.SelectedCoverAssetId = null;

        db.PublishAssets.Remove(asset);
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddImagePlacementAsync(Guid projectId, PublishImagePlacementCreate request, CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        if (!await db.PublishAssets.AnyAsync(asset => asset.ProjectId == projectId && asset.Id == request.AssetId, cancellationToken))
            throw new InvalidOperationException("Publish asset was not found.");
        await EnsureTargetExistsAsync(projectId, request.TargetKind, request.TargetId, cancellationToken);
        ValidatePlacementKind(request.TargetKind, request.PlacementKind);

        var nextOrder = await db.PublishImagePlacements
            .Where(placement => placement.ProjectId == projectId
                && placement.TargetKind == request.TargetKind
                && placement.TargetId == request.TargetId
                && placement.PlacementKind == request.PlacementKind)
            .Select(placement => (int?)placement.SortOrder)
            .MaxAsync(cancellationToken) ?? -1;

        await db.PublishImagePlacements.AddAsync(new PublishImagePlacement
        {
            ProjectId = projectId,
            AssetId = request.AssetId,
            TargetKind = request.TargetKind,
            TargetId = request.TargetId,
            PlacementKind = request.PlacementKind,
            SortOrder = nextOrder + 1,
            Caption = Clean(request.Caption),
        }, cancellationToken);

        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteImagePlacementAsync(Guid projectId, Guid placementId, CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        var placement = await db.PublishImagePlacements.FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == placementId, cancellationToken);
        if (placement is null) return;

        db.PublishImagePlacements.Remove(placement);
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ProjectExportFile> ExportAsync(Guid projectId, PublishExportFormat format, CancellationToken cancellationToken = default)
    {
        var formatter = formatters.FirstOrDefault(candidate => candidate.Format == format)
            ?? throw new InvalidOperationException($"No publish formatter is registered for {format}.");
        var document = await GetDocumentAsync(projectId, cancellationToken);

        return new ProjectExportFile(
            FileName: $"{SafeFileName(document.ProjectSlug)}-publish{formatter.FileExtension}",
            ContentType: formatter.ContentType,
            Content: formatter.Render(document));
    }

    public async Task<PublishDocument> GetDocumentAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        var profile = await EnsureProfileAsync(project, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var acts = await db.Acts.Where(act => act.ProjectId == projectId).OrderBy(act => act.Order).ToListAsync(cancellationToken);
        var chapters = await db.Chapters.Where(chapter => chapter.ProjectId == projectId).OrderBy(chapter => chapter.Order).ToListAsync(cancellationToken);
        var selections = await db.PublishOutlineSelections.Where(selection => selection.ProjectId == projectId).ToListAsync(cancellationToken);
        var assets = await db.PublishAssets.Where(asset => asset.ProjectId == projectId).ToDictionaryAsync(asset => asset.Id, cancellationToken);
        var placements = await db.PublishImagePlacements
            .Where(placement => placement.ProjectId == projectId)
            .OrderBy(placement => placement.SortOrder)
            .ToListAsync(cancellationToken);

        var sections = new List<PublishSectionDocument>();
        var actIndex = 0;
        foreach (var act in acts.OrderBy(act => act.Order))
        {
            var actChapters = chapters
                .Where(chapter => chapter.ActId == act.Id && IsIncluded(selections, PublishOutlineTargetKind.Chapter, chapter.Id))
                .OrderBy(chapter => chapter.Order)
                .Select(chapter => ChapterDocument(chapter, profile))
                .ToList();
            if (actChapters.Count == 0) continue;

            actIndex++;
            var includeActPage = IsIncluded(selections, PublishOutlineTargetKind.Act, act.Id);
            var title = profile.NumberActs ? $"Act {actIndex}: {act.Title}" : act.Title;
            sections.Add(new PublishSectionDocument(
                act.Id,
                title,
                act.Synopsis,
                IsUnassigned: false,
                IncludePage: includeActPage,
                IncludeHeading: includeActPage && profile.IncludeActHeadings,
                act.Order,
                actChapters));
        }

        var unassigned = chapters
            .Where(chapter => chapter.ActId is null && IsIncluded(selections, PublishOutlineTargetKind.Chapter, chapter.Id))
            .OrderBy(chapter => chapter.Order)
            .Select(chapter => ChapterDocument(chapter, profile))
            .ToList();
        if (unassigned.Count > 0)
        {
            sections.Add(new PublishSectionDocument(
                null,
                "Unassigned",
                string.Empty,
                IsUnassigned: true,
                IncludePage: false,
                IncludeHeading: false,
                int.MaxValue,
                unassigned));
        }

        var placementDocuments = placements
            .Where(placement => assets.ContainsKey(placement.AssetId)
                && TargetIncluded(sections, placement.TargetKind, placement.TargetId))
            .Select(placement => new PublishImagePlacementDocument(
                placement.Id,
                AssetDocument(assets[placement.AssetId]),
                placement.TargetKind,
                placement.TargetId,
                placement.PlacementKind,
                placement.Caption,
                placement.SortOrder))
            .ToList();

        var cover = profile.SelectedCoverAssetId is Guid coverId && assets.TryGetValue(coverId, out var coverAsset)
            ? AssetDocument(coverAsset)
            : null;

        return new PublishDocument(
            project.Id,
            project.Name,
            project.Slug,
            DateTime.UtcNow,
            ProfileDocument(profile),
            cover,
            sections,
            placementDocuments);
    }

    private async Task<Project> GetProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
        await db.Projects.FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken)
        ?? throw new InvalidOperationException($"Project {projectId} not found.");

    private async Task<PublishProfile> EnsureProfileAsync(Project project, CancellationToken cancellationToken)
    {
        var profile = await db.PublishProfiles.FirstOrDefaultAsync(candidate => candidate.ProjectId == project.Id, cancellationToken);
        if (profile is not null)
            return profile;

        profile = new PublishProfile
        {
            ProjectId = project.Id,
            TitleOverride = project.Name,
            Language = "en",
        };
        await db.PublishProfiles.AddAsync(profile, cancellationToken);
        return profile;
    }

    private static PublishProfileView ProfileView(Project project, PublishProfile profile) =>
        new(
            profile.Id,
            project.Name,
            project.Slug,
            profile.TitleOverride,
            profile.Subtitle,
            profile.Author,
            profile.Language,
            profile.Publisher,
            profile.Copyright,
            profile.Isbn,
            profile.Description,
            profile.Dedication,
            profile.Acknowledgments,
            profile.References,
            profile.IncludeTableOfContents,
            profile.IncludeVisibleTableOfContents,
            profile.IncludeActSynopses,
            profile.IncludeChapterSynopses,
            profile.IncludeActHeadings,
            profile.IncludeChapterHeadings,
            profile.NumberActs,
            profile.NumberChapters,
            profile.SelectedCoverAssetId);

    private static PublishDocumentProfile ProfileDocument(PublishProfile profile) =>
        new(
            profile.TitleOverride,
            profile.Subtitle,
            profile.Author,
            profile.Language,
            profile.Publisher,
            profile.Copyright,
            profile.Isbn,
            profile.Description,
            profile.Dedication,
            profile.Acknowledgments,
            profile.References,
            profile.IncludeTableOfContents,
            profile.IncludeVisibleTableOfContents,
            profile.IncludeActSynopses,
            profile.IncludeChapterSynopses,
            profile.IncludeActHeadings,
            profile.IncludeChapterHeadings,
            profile.NumberActs,
            profile.NumberChapters);

    private static List<PublishSectionView> SectionViews(
        IReadOnlyList<Act> acts,
        IReadOnlyList<Chapter> chapters,
        IReadOnlyList<PublishOutlineSelection> selections)
    {
        var result = new List<PublishSectionView>();
        foreach (var act in acts.OrderBy(act => act.Order))
        {
            var actChapters = chapters
                .Where(chapter => chapter.ActId == act.Id)
                .OrderBy(chapter => chapter.Order)
                .Select(chapter => new PublishChapterView(
                    chapter.Id,
                    chapter.ActId,
                    chapter.Title,
                    IsIncluded(selections, PublishOutlineTargetKind.Chapter, chapter.Id)))
                .ToList();

            result.Add(new PublishSectionView(
                act.Id,
                act.Title,
                IsUnassigned: false,
                IsIncluded(selections, PublishOutlineTargetKind.Act, act.Id),
                actChapters));
        }

        var unassigned = chapters
            .Where(chapter => chapter.ActId is null)
            .OrderBy(chapter => chapter.Order)
            .Select(chapter => new PublishChapterView(
                chapter.Id,
                chapter.ActId,
                chapter.Title,
                IsIncluded(selections, PublishOutlineTargetKind.Chapter, chapter.Id)))
            .ToList();

        if (unassigned.Count > 0)
            result.Add(new PublishSectionView(null, "Unassigned", IsUnassigned: true, IsIncluded: true, unassigned));

        return result;
    }

    private static PublishAssetView AssetView(PublishAsset asset) =>
        new(
            asset.Id,
            asset.FileName,
            asset.ContentType,
            $"data:{asset.ContentType};base64,{Convert.ToBase64String(asset.Data)}",
            asset.AltText,
            asset.Source,
            asset.Prompt,
            asset.GenerationModel,
            asset.CreatedAt,
            asset.Data.LongLength);

    private static PublishImagePlacementView PlacementView(PublishImagePlacement placement, IReadOnlyList<Act> acts, IReadOnlyList<Chapter> chapters) =>
        new(
            placement.Id,
            placement.AssetId,
            placement.Asset.FileName,
            placement.TargetKind,
            placement.TargetId,
            TargetTitle(placement.TargetKind, placement.TargetId, acts, chapters),
            placement.PlacementKind,
            placement.Caption);

    private static string TargetTitle(PublishOutlineTargetKind kind, Guid targetId, IReadOnlyList<Act> acts, IReadOnlyList<Chapter> chapters) =>
        kind == PublishOutlineTargetKind.Act
            ? acts.FirstOrDefault(act => act.Id == targetId)?.Title ?? "Act"
            : chapters.FirstOrDefault(chapter => chapter.Id == targetId)?.Title ?? "Chapter";

    private static PublishChapterDocument ChapterDocument(Chapter chapter, PublishProfile profile) =>
        new(
            chapter.Id,
            chapter.ActId,
            profile.NumberChapters ? $"Chapter {chapter.Order + 1}: {chapter.Title}" : chapter.Title,
            chapter.Body,
            chapter.Synopsis,
            chapter.Order,
            profile.IncludeChapterHeadings);

    private static PublishAssetDocument AssetDocument(PublishAsset asset) =>
        new(asset.Id, asset.FileName, asset.ContentType, asset.Data, asset.AltText);

    private static bool IsIncluded(IReadOnlyList<PublishOutlineSelection> selections, PublishOutlineTargetKind kind, Guid targetId) =>
        selections.FirstOrDefault(selection => selection.TargetKind == kind && selection.TargetId == targetId)?.IsIncluded ?? true;

    private static bool TargetIncluded(IReadOnlyList<PublishSectionDocument> sections, PublishOutlineTargetKind kind, Guid targetId) =>
        kind == PublishOutlineTargetKind.Act
            ? sections.Any(section => section.ActId == targetId && section.IncludePage)
            : sections.SelectMany(section => section.Chapters).Any(chapter => chapter.Id == targetId);

    private async Task EnsureTargetExistsAsync(Guid projectId, PublishOutlineTargetKind targetKind, Guid targetId, CancellationToken cancellationToken)
    {
        var exists = targetKind == PublishOutlineTargetKind.Act
            ? await db.Acts.AnyAsync(act => act.ProjectId == projectId && act.Id == targetId, cancellationToken)
            : await db.Chapters.AnyAsync(chapter => chapter.ProjectId == projectId && chapter.Id == targetId, cancellationToken);

        if (!exists)
            throw new InvalidOperationException($"{targetKind} was not found.");
    }

    private static void ValidatePlacementKind(PublishOutlineTargetKind targetKind, PublishImagePlacementKind placementKind)
    {
        var valid = targetKind == PublishOutlineTargetKind.Act
            ? placementKind is PublishImagePlacementKind.BeforeAct or PublishImagePlacementKind.AfterAct
            : placementKind is PublishImagePlacementKind.BeforeChapter
                or PublishImagePlacementKind.ChapterOpening
                or PublishImagePlacementKind.ChapterEnding
                or PublishImagePlacementKind.AfterChapter;

        if (!valid)
            throw new InvalidOperationException($"{placementKind} cannot be used with a {targetKind} target.");
    }

    private static string? NormalizeImageContentType(string contentType) =>
        contentType.Trim().ToLowerInvariant() switch
        {
            "image/png" => "image/png",
            "image/jpeg" => "image/jpeg",
            "image/jpg" => "image/jpeg",
            _ => null,
        };

    private static string ExtensionForContentType(string contentType) =>
        contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ? "jpg" : "png";

    private static string Clean(string value) => value.Trim();

    private static string SafeFileName(string input)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var chars = input.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray();
        var result = new string(chars).Trim('-', ' ', '.');
        return string.IsNullOrWhiteSpace(result) ? "project" : result;
    }

    private static void Touch(PublishProfile profile, Project project)
    {
        var now = DateTime.UtcNow;
        profile.UpdatedAt = now;
        project.UpdatedAt = now;
    }
}
