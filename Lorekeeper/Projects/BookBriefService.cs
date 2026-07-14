using System.Globalization;
using System.Text;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Projects;

public sealed class BookBriefService(AppDbContext db) : IBookBriefService
{
    private static readonly HashSet<string> FieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(BookBrief.BookKind),
        nameof(BookBrief.Premise),
        nameof(BookBrief.Genre),
        nameof(BookBrief.PrimaryThemes),
        nameof(BookBrief.Purpose),
        nameof(BookBrief.CreativeConstraints),
        nameof(BookBrief.TargetAudience),
        nameof(BookBrief.MinimumReaderAge),
        nameof(BookBrief.MaximumReaderAge),
        nameof(BookBrief.ReadingLevelGuidance),
        nameof(BookBrief.TargetWordCount),
        nameof(BookBrief.PointOfView),
        nameof(BookBrief.Tense),
        nameof(BookBrief.VoiceAndTone),
        nameof(BookBrief.LanguageLocale),
        nameof(BookBrief.HouseStyle),
        nameof(BookBrief.ReadAloudPriority),
        nameof(BookBrief.AccessibilityGoals),
        nameof(BookBrief.VisualDirection),
    };

    public async Task<BookBrief> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var existing = await db.BookBriefs
            .SingleOrDefaultAsync(brief => brief.ProjectId == projectId, cancellationToken);
        if (existing is not null)
            return existing;

        if (!await db.Projects.AnyAsync(project => project.Id == projectId, cancellationToken))
            throw new InvalidOperationException($"Project {projectId} not found.");

        var brief = new BookBrief { ProjectId = projectId };
        db.BookBriefs.Add(brief);
        await db.SaveChangesAsync(cancellationToken);
        return brief;
    }

    public async Task<BookBrief> UpdateAsync(
        Guid projectId,
        BookBriefPatch patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var brief = await GetOrCreateAsync(projectId, cancellationToken);
        var clearFields = NormalizeClearFields(patch.ClearFields);

        ApplyClearFields(brief, clearFields);
        ApplyPatch(brief, patch, clearFields);
        Validate(brief);

        brief.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return brief;
    }

    public string FormatForPrompt(BookBrief brief)
    {
        ArgumentNullException.ThrowIfNull(brief);
        var lines = new List<string>
        {
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
            Field("Visual direction", brief.VisualDirection),
        };

        return string.Join('\n', lines);
    }

    private static HashSet<string> NormalizeClearFields(IReadOnlyList<string>? values)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values ?? [])
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;
            if (!FieldNames.Contains(trimmed))
                throw new ArgumentException($"Unknown Book Brief clear field '{trimmed}'.", nameof(values));
            result.Add(trimmed);
        }
        return result;
    }

    private static void ApplyPatch(BookBrief brief, BookBriefPatch patch, IReadOnlySet<string> cleared)
    {
        if (patch.BookKind is { } bookKind && !cleared.Contains(nameof(BookBrief.BookKind))) brief.BookKind = bookKind;
        SetString(value => brief.Premise = value, patch.Premise, nameof(BookBrief.Premise), cleared);
        SetString(value => brief.Genre = value, patch.Genre, nameof(BookBrief.Genre), cleared);
        SetString(value => brief.PrimaryThemes = value, patch.PrimaryThemes, nameof(BookBrief.PrimaryThemes), cleared);
        SetString(value => brief.Purpose = value, patch.Purpose, nameof(BookBrief.Purpose), cleared);
        SetString(value => brief.CreativeConstraints = value, patch.CreativeConstraints, nameof(BookBrief.CreativeConstraints), cleared);
        SetString(value => brief.TargetAudience = value, patch.TargetAudience, nameof(BookBrief.TargetAudience), cleared);
        if (patch.MinimumReaderAge is { } minimumReaderAge && !cleared.Contains(nameof(BookBrief.MinimumReaderAge))) brief.MinimumReaderAge = minimumReaderAge;
        if (patch.MaximumReaderAge is { } maximumReaderAge && !cleared.Contains(nameof(BookBrief.MaximumReaderAge))) brief.MaximumReaderAge = maximumReaderAge;
        SetString(value => brief.ReadingLevelGuidance = value, patch.ReadingLevelGuidance, nameof(BookBrief.ReadingLevelGuidance), cleared);
        if (patch.TargetWordCount is { } targetWordCount && !cleared.Contains(nameof(BookBrief.TargetWordCount))) brief.TargetWordCount = targetWordCount;
        SetString(value => brief.PointOfView = value, patch.PointOfView, nameof(BookBrief.PointOfView), cleared);
        SetString(value => brief.Tense = value, patch.Tense, nameof(BookBrief.Tense), cleared);
        SetString(value => brief.VoiceAndTone = value, patch.VoiceAndTone, nameof(BookBrief.VoiceAndTone), cleared);
        SetString(value => brief.LanguageLocale = value, patch.LanguageLocale, nameof(BookBrief.LanguageLocale), cleared);
        SetString(value => brief.HouseStyle = value, patch.HouseStyle, nameof(BookBrief.HouseStyle), cleared);
        if (patch.ReadAloudPriority is { } readAloudPriority && !cleared.Contains(nameof(BookBrief.ReadAloudPriority))) brief.ReadAloudPriority = readAloudPriority;
        SetString(value => brief.AccessibilityGoals = value, patch.AccessibilityGoals, nameof(BookBrief.AccessibilityGoals), cleared);
        SetString(value => brief.VisualDirection = value, patch.VisualDirection, nameof(BookBrief.VisualDirection), cleared);
    }

    private static void ApplyClearFields(BookBrief brief, IEnumerable<string> fields)
    {
        foreach (var field in fields)
        {
            switch (field.ToUpperInvariant())
            {
                case "BOOKKIND": brief.BookKind = BookKind.Unspecified; break;
                case "PREMISE": brief.Premise = string.Empty; break;
                case "GENRE": brief.Genre = string.Empty; break;
                case "PRIMARYTHEMES": brief.PrimaryThemes = string.Empty; break;
                case "PURPOSE": brief.Purpose = string.Empty; break;
                case "CREATIVECONSTRAINTS": brief.CreativeConstraints = string.Empty; break;
                case "TARGETAUDIENCE": brief.TargetAudience = string.Empty; break;
                case "MINIMUMREADERAGE": brief.MinimumReaderAge = null; break;
                case "MAXIMUMREADERAGE": brief.MaximumReaderAge = null; break;
                case "READINGLEVELGUIDANCE": brief.ReadingLevelGuidance = string.Empty; break;
                case "TARGETWORDCOUNT": brief.TargetWordCount = null; break;
                case "POINTOFVIEW": brief.PointOfView = string.Empty; break;
                case "TENSE": brief.Tense = string.Empty; break;
                case "VOICEANDTONE": brief.VoiceAndTone = string.Empty; break;
                case "LANGUAGELOCALE": brief.LanguageLocale = string.Empty; break;
                case "HOUSESTYLE": brief.HouseStyle = string.Empty; break;
                case "READALOUDPRIORITY": brief.ReadAloudPriority = null; break;
                case "ACCESSIBILITYGOALS": brief.AccessibilityGoals = string.Empty; break;
                case "VISUALDIRECTION": brief.VisualDirection = string.Empty; break;
            }
        }
    }

    private static void Validate(BookBrief brief)
    {
        if (!Enum.IsDefined(brief.BookKind))
            throw new ArgumentOutOfRangeException(nameof(brief.BookKind));
        if (brief.MinimumReaderAge is < 0 or > 120)
            throw new ArgumentOutOfRangeException(nameof(brief.MinimumReaderAge), "Minimum reader age must be between 0 and 120.");
        if (brief.MaximumReaderAge is < 0 or > 120)
            throw new ArgumentOutOfRangeException(nameof(brief.MaximumReaderAge), "Maximum reader age must be between 0 and 120.");
        if (brief.MinimumReaderAge is { } minimum && brief.MaximumReaderAge is { } maximum && minimum > maximum)
            throw new ArgumentException("Minimum reader age cannot be greater than maximum reader age.");
        if (brief.TargetWordCount is <= 0)
            throw new ArgumentOutOfRangeException(nameof(brief.TargetWordCount), "Target word count must be greater than zero.");
    }

    private static void SetString(Action<string> setter, string? value, string fieldName, IReadOnlySet<string> cleared)
    {
        if (value is not null && !cleared.Contains(fieldName))
            setter(value.Trim());
    }

    private static string Field(string label, string value) =>
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
