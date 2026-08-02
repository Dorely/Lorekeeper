namespace Lorekeeper.Models;

public sealed class ProjectPageSetup
{
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public double PageWidthInches { get; set; } = 6;
    public double PageHeightInches { get; set; } = 9;
    public double PageMarginInches { get; set; } = 0.75;
    public double BodyFontSizePoints { get; set; } = 12;
    public double BodyLineHeight { get; set; } = 1.55;
    public long Revision { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
