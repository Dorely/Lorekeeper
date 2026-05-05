using System.Text;
using Lorekeeper.Knowledge;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Projects;

public class ProjectService(
    IProjectRepository repo,
    IVectorStore vectors,
    IOutlineGraphSync outlineGraphSync) : IProjectService
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
            SystemPrompt = SeedSystemPrompt.Default,
        };
        await repo.AddAsync(project, cancellationToken);
        await repo.SaveChangesAsync(cancellationToken);
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

    public async Task<Project> UpdateSystemPromptAsync(Guid id, string systemPrompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(systemPrompt))
            throw new ArgumentException("System prompt cannot be empty.", nameof(systemPrompt));

        var project = await repo.GetByIdAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Project {id} not found.");

        project.SystemPrompt = systemPrompt;
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

    public async Task<Project> UpdateMetadataAsync(Guid id, IDictionary<string, object?> metadata, bool merge = true, CancellationToken cancellationToken = default)
    {
        var project = await repo.GetByIdAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Project {id} not found.");

        if (merge)
        {
            foreach (var kvp in metadata)
                project.Metadata[kvp.Key] = kvp.Value;
        }
        else
        {
            project.Metadata = new Dictionary<string, object?>(metadata);
        }

        project.UpdatedAt = DateTime.UtcNow;
        repo.Update(project);
        await repo.SaveChangesAsync(cancellationToken);
        return project;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var project = await repo.GetByIdAsync(id, cancellationToken);
        if (project is null) return;

        // Wipe vector chunks first; if this fails we'd rather leave the project row in place
        // than orphan vectors with no owning scope.
        await vectors.DeleteByScopeAsync(Project.ScopeKey(id), cancellationToken);

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
