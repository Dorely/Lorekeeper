namespace Lorekeeper.Models;

public class ProjectImageGenerationJob
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public ProjectImageGenerationJobKind Kind { get; set; } = ProjectImageGenerationJobKind.Generate;

    public ProjectImageGenerationJobStatus Status { get; set; } = ProjectImageGenerationJobStatus.Queued;

    public string Label { get; set; } = string.Empty;

    public string Prompt { get; set; } = string.Empty;

    public string BriefJson { get; set; } = "{}";

    public string ReferenceManifestJson { get; set; } = "[]";

    public string TargetGeometryJson { get; set; } = "{}";

    public string ProviderRevisedPromptsJson { get; set; } = "[]";

    public string Size { get; set; } = "auto";

    public string Quality { get; set; } = "auto";

    public string OutputFormat { get; set; } = "png";

    public int? OutputCompression { get; set; }

    public int Count { get; set; } = 1;

    public string AltText { get; set; } = string.Empty;

    public Guid? SourceImageId { get; set; }

    public Guid? MaskId { get; set; }

    public string ReferenceImageIdsJson { get; set; } = "[]";

    public string EntityVisualTargetsJson { get; set; } = "[]";

    public bool InheritSourceEntityTargets { get; set; } = true;

    public string OutputImageIdsJson { get; set; } = "[]";

    public string OutputStatesJson { get; set; } = "[]";

    public string OutputErrorsJson { get; set; } = "[]";

    public string Provider { get; set; } = string.Empty;

    public string MainlineModel { get; set; } = string.Empty;

    public string ImageModel { get; set; } = string.Empty;

    public string RawProviderResponseJson { get; set; } = string.Empty;

    public string Error { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public ICollection<ProjectImagePartial> Partials { get; set; } = [];
}

public enum ProjectImageGenerationJobKind
{
    Generate,
    Edit,
}

public enum ProjectImageGenerationJobStatus
{
    Queued,
    Running,
    Succeeded,
    CompletedWithErrors,
    Failed,
    Cancelled,
}
