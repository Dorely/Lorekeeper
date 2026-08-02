using System.Text;
using Lorekeeper.Knowledge;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Search;

namespace Lorekeeper.Projects;

public class ProjectService(
    IProjectRepository repo,
    IVectorStore vectors,
    IProjectSearchIndex projectSearch,
    IOutlineGraphSync outlineGraphSync,
    IBookBriefService bookBriefs) : IProjectService
{
    public async Task<IReadOnlyList<Project>> ListAsync(CancellationToken cancellationToken = default) =>
        await repo.ListAsync(cancellationToken);

    public Task<Project?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
        repo.GetBySlugAsync(slug, cancellationToken);

    public async Task<Project> CreateAsync(string name, CancellationToken cancellationToken = default)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("Project name is required.", nameof(name));

        var slug = await GenerateUniqueSlugAsync(trimmed, cancellationToken);
        var project = new Project
        {
            Name = trimmed,
            Slug = slug,
            ProjectGuidance = string.Empty,
            PageSetup = new ProjectPageSetup(),
        };
        await repo.AddAsync(project, cancellationToken);
        await repo.SaveChangesAsync(cancellationToken);
        project.BookBrief = await bookBriefs.GetOrCreateAsync(project.Id, cancellationToken);
        await outlineGraphSync.EnsureProjectAsync(project, cancellationToken);
        return project;
    }

    public async Task<Project> RenameAsync(Guid id, string newName, CancellationToken cancellationToken = default)
    {
        var trimmed = (newName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("Project name is required.", nameof(newName));

        var project = await repo.GetByIdAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Project {id} not found.");

        project.Name = trimmed;
        project.UpdatedAt = DateTime.UtcNow;
        repo.Update(project);
        await repo.SaveChangesAsync(cancellationToken);
        await outlineGraphSync.EnsureProjectAsync(project, cancellationToken);
        return project;
    }

    public async Task<Project> UpdateProjectGuidanceAsync(Guid id, string projectGuidance, CancellationToken cancellationToken = default)
    {
        var project = await repo.GetByIdAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Project {id} not found.");

        project.ProjectGuidance = (projectGuidance ?? string.Empty).Trim();
        project.UpdatedAt = DateTime.UtcNow;
        repo.Update(project);
        await repo.SaveChangesAsync(cancellationToken);
        return project;
    }

    public async Task<Project> SetIncludeCurrentChapterAsync(Guid id, bool include, CancellationToken cancellationToken = default)
    {
        var project = await repo.GetByIdAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Project {id} not found.");

        if (project.IncludeCurrentChapterInContext != include)
        {
            project.IncludeCurrentChapterInContext = include;
            project.UpdatedAt = DateTime.UtcNow;
            repo.Update(project);
            await repo.SaveChangesAsync(cancellationToken);
        }
        return project;
    }

    public async Task<Project> SetAiChangeApprovalAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        var project = await repo.GetByIdAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Project {id} not found.");

        if (project.AiChangeApprovalEnabled != enabled)
        {
            project.AiChangeApprovalEnabled = enabled;
            project.UpdatedAt = DateTime.UtcNow;
            repo.Update(project);
            await repo.SaveChangesAsync(cancellationToken);
        }
        return project;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var project = await repo.GetByIdAsync(id, cancellationToken);
        if (project is null) return;

        // Wipe vector chunks first; if this fails we'd rather leave the project row in place
        // than orphan vectors with no owning scope.
        var scopeKey = Project.ScopeKey(id);
        await vectors.DeleteByScopeAsync(scopeKey, cancellationToken);
        await projectSearch.DeleteByScopeAsync(scopeKey, cancellationToken);

        repo.Remove(project);
        await repo.SaveChangesAsync(cancellationToken);
    }

    private async Task<string> GenerateUniqueSlugAsync(string name, CancellationToken cancellationToken)
    {
        var baseSlug = Slugify(name);
        if (baseSlug.Length == 0) baseSlug = "project";

        var slug = baseSlug;
        var suffix = 2;
        while (await repo.SlugExistsAsync(slug, cancellationToken))
        {
            slug = $"{baseSlug}-{suffix++}";
        }
        return slug;
    }

    internal static string Slugify(string input)
    {
        var sb = new StringBuilder(input.Length);
        var lastWasDash = false;
        foreach (var ch in input.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastWasDash = false;
            }
            else if (!lastWasDash && sb.Length > 0)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }
        if (sb.Length > 0 && sb[^1] == '-') sb.Length--;
        return sb.ToString();
    }
}
