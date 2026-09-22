using System.Globalization;
using Lorekeeper.Models;

namespace Lorekeeper.Context;

internal static class ProjectProfileFormatter
{
    public static string Build(Project project, BookBrief? brief, string worldBrief = "")
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("# ").AppendLine(project.Name);
        builder.Append("Slug: ").AppendLine(project.Slug);
        builder.AppendLine("Project guidance:");
        builder.AppendLine(string.IsNullOrWhiteSpace(project.ProjectGuidance) ? "(none)" : project.ProjectGuidance.Trim());
        builder.AppendLine();
        builder.AppendLine("Book Brief:");
        builder.AppendLine(brief is null ? "(not configured)" : FormatBookBrief(brief));
        builder.AppendLine();
        builder.AppendLine("World Brief:");
        builder.AppendLine(string.IsNullOrWhiteSpace(worldBrief) ? "(not configured)" : worldBrief);
        return builder.ToString().TrimEnd();
    }

    public static string FormatBookBrief(BookBrief brief) => string.Join('\n',
        $"Book kind: {brief.BookKind}",
        Field("Premise", brief.Premise),
        Field("Genre", brief.Genre),
        Field("Primary themes", brief.PrimaryThemes),
        Field("Purpose", brief.Purpose),
        Field("Non-negotiable creative constraints", brief.CreativeConstraints),
        Field("Target audience", brief.TargetAudience),
        $"Reader age range: {AgeRange(brief.MinimumReaderAge, brief.MaximumReaderAge)}",
        Field("Reading-level guidance", brief.ReadingLevelGuidance),
        $"Target word count: {brief.TargetWordCount?.ToString("N0", CultureInfo.InvariantCulture) ?? "Unspecified"}",
        Field("POV", brief.PointOfView),
        Field("Tense", brief.Tense),
        Field("Voice/tone", brief.VoiceAndTone),
        Field("Language/locale", brief.LanguageLocale),
        Field("House style", brief.HouseStyle),
        $"Read-aloud priority: {NullableBoolean(brief.ReadAloudPriority)}",
        Field("Accessibility goals", brief.AccessibilityGoals),
        Field("Visual direction", brief.VisualDirection));

    private static string Field(string label, string? value) =>
        $"{label}: {(string.IsNullOrWhiteSpace(value) ? "Unspecified" : value.Trim())}";

    private static string AgeRange(int? minimum, int? maximum) => (minimum, maximum) switch
    {
        ({ } min, { } max) => $"{min}-{max}",
        ({ } min, null) => $"{min}+",
        (null, { } max) => $"Up to {max}",
        _ => "Unspecified",
    };

    private static string NullableBoolean(bool? value) => value switch
    {
        true => "Yes",
        false => "No",
        null => "Unspecified",
    };
}
