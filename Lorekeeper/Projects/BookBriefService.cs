using System.Globalization;
using System.Text;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Projects;

public sealed class BookBriefService(IAppDatabaseOperationFactory database) : IBookBriefService
{
    public async Task<BookBrief> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
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
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        ArgumentNullException.ThrowIfNull(patch);
        var existing = await db.BookBriefs
            .SingleOrDefaultAsync(candidate => candidate.ProjectId == projectId, cancellationToken);
        var isNew = existing is null;
        if (isNew && !await db.Projects.AnyAsync(project => project.Id == projectId, cancellationToken))
            throw new InvalidOperationException($"Project {projectId} not found.");
        var brief = existing ?? new BookBrief { ProjectId = projectId };

        var clearFields = NormalizeClearFields(patch.ClearFields);

        ApplyClearFields(brief, clearFields);
        ApplyPatch(brief, patch, clearFields);
        Validate(brief);

        brief.UpdatedAt = DateTime.UtcNow;
        if (isNew)
            db.BookBriefs.Add(brief);
        await db.SaveChangesAsync(cancellationToken);
        return brief;
    }

    public async Task<BookBrief> UpdateVisualDirectionAsync(
        Guid projectId,
        string expectedCurrentVisualDirection,
        string visualDirection,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        ArgumentNullException.ThrowIfNull(expectedCurrentVisualDirection);
        ArgumentNullException.ThrowIfNull(visualDirection);

        _ = await GetOrCreateAsync(projectId, cancellationToken);
        var nextVisualDirection = visualDirection.Trim();
        var updatedAt = DateTime.UtcNow;
        var affected = await db.BookBriefs
            .Where(brief => brief.ProjectId == projectId
                && brief.VisualDirection == expectedCurrentVisualDirection)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(brief => brief.VisualDirection, nextVisualDirection)
                .SetProperty(brief => brief.UpdatedAt, updatedAt), cancellationToken);

        if (affected == 0)
        {
            var actualVisualDirection = await db.BookBriefs
                .AsNoTracking()
                .Where(brief => brief.ProjectId == projectId)
                .Select(brief => brief.VisualDirection)
                .SingleAsync(cancellationToken);
            throw new BookBriefVisualDirectionConflictException(actualVisualDirection);
        }

        var updated = await db.BookBriefs
            .AsNoTracking()
            .SingleAsync(brief => brief.ProjectId == projectId, cancellationToken);
        var trackedEntry = db.ChangeTracker
            .Entries<BookBrief>()
            .SingleOrDefault(entry => entry.Entity.ProjectId == projectId);
        if (trackedEntry is not null)
        {
            trackedEntry.CurrentValues.SetValues(updated);
            trackedEntry.State = EntityState.Unchanged;
            return trackedEntry.Entity;
        }

        return updated;
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

    private static HashSet<BookBriefField> NormalizeClearFields(IReadOnlyList<BookBriefField>? values)
    {
        var result = new HashSet<BookBriefField>();
        foreach (var value in values ?? [])
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentException($"Unknown Book Brief clear field '{value}'.", nameof(values));
            result.Add(value);
        }
        return result;
    }

    private static void ApplyPatch(BookBrief brief, BookBriefPatch patch, IReadOnlySet<BookBriefField> cleared)
    {
        if (patch.BookKind is { } bookKind && !cleared.Contains(BookBriefField.BookKind)) brief.BookKind = bookKind;
        SetString(value => brief.Premise = value, patch.Premise, BookBriefField.Premise, cleared);
        SetString(value => brief.Genre = value, patch.Genre, BookBriefField.Genre, cleared);
        SetString(value => brief.PrimaryThemes = value, patch.PrimaryThemes, BookBriefField.PrimaryThemes, cleared);
        SetString(value => brief.Purpose = value, patch.Purpose, BookBriefField.Purpose, cleared);
        SetString(value => brief.CreativeConstraints = value, patch.CreativeConstraints, BookBriefField.CreativeConstraints, cleared);
        SetString(value => brief.TargetAudience = value, patch.TargetAudience, BookBriefField.TargetAudience, cleared);
        if (patch.MinimumReaderAge is { } minimumReaderAge && !cleared.Contains(BookBriefField.MinimumReaderAge)) brief.MinimumReaderAge = minimumReaderAge;
        if (patch.MaximumReaderAge is { } maximumReaderAge && !cleared.Contains(BookBriefField.MaximumReaderAge)) brief.MaximumReaderAge = maximumReaderAge;
        SetString(value => brief.ReadingLevelGuidance = value, patch.ReadingLevelGuidance, BookBriefField.ReadingLevelGuidance, cleared);
        if (patch.TargetWordCount is { } targetWordCount && !cleared.Contains(BookBriefField.TargetWordCount)) brief.TargetWordCount = targetWordCount;
        SetString(value => brief.PointOfView = value, patch.PointOfView, BookBriefField.PointOfView, cleared);
        SetString(value => brief.Tense = value, patch.Tense, BookBriefField.Tense, cleared);
        SetString(value => brief.VoiceAndTone = value, patch.VoiceAndTone, BookBriefField.VoiceAndTone, cleared);
        SetString(value => brief.LanguageLocale = value, patch.LanguageLocale, BookBriefField.LanguageLocale, cleared);
        SetString(value => brief.HouseStyle = value, patch.HouseStyle, BookBriefField.HouseStyle, cleared);
        if (patch.ReadAloudPriority is { } readAloudPriority && !cleared.Contains(BookBriefField.ReadAloudPriority)) brief.ReadAloudPriority = readAloudPriority;
        SetString(value => brief.AccessibilityGoals = value, patch.AccessibilityGoals, BookBriefField.AccessibilityGoals, cleared);
        SetString(value => brief.VisualDirection = value, patch.VisualDirection, BookBriefField.VisualDirection, cleared);
    }

    private static void ApplyClearFields(BookBrief brief, IEnumerable<BookBriefField> fields)
    {
        foreach (var field in fields)
        {
            switch (field)
            {
                case BookBriefField.BookKind: brief.BookKind = BookKind.Unspecified; break;
                case BookBriefField.Premise: brief.Premise = string.Empty; break;
                case BookBriefField.Genre: brief.Genre = string.Empty; break;
                case BookBriefField.PrimaryThemes: brief.PrimaryThemes = string.Empty; break;
                case BookBriefField.Purpose: brief.Purpose = string.Empty; break;
                case BookBriefField.CreativeConstraints: brief.CreativeConstraints = string.Empty; break;
                case BookBriefField.TargetAudience: brief.TargetAudience = string.Empty; break;
                case BookBriefField.MinimumReaderAge: brief.MinimumReaderAge = null; break;
                case BookBriefField.MaximumReaderAge: brief.MaximumReaderAge = null; break;
                case BookBriefField.ReadingLevelGuidance: brief.ReadingLevelGuidance = string.Empty; break;
                case BookBriefField.TargetWordCount: brief.TargetWordCount = null; break;
                case BookBriefField.PointOfView: brief.PointOfView = string.Empty; break;
                case BookBriefField.Tense: brief.Tense = string.Empty; break;
                case BookBriefField.VoiceAndTone: brief.VoiceAndTone = string.Empty; break;
                case BookBriefField.LanguageLocale: brief.LanguageLocale = string.Empty; break;
                case BookBriefField.HouseStyle: brief.HouseStyle = string.Empty; break;
                case BookBriefField.ReadAloudPriority: brief.ReadAloudPriority = null; break;
                case BookBriefField.AccessibilityGoals: brief.AccessibilityGoals = string.Empty; break;
                case BookBriefField.VisualDirection: brief.VisualDirection = string.Empty; break;
                default: throw new ArgumentOutOfRangeException(nameof(field));
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

    private static void SetString(Action<string> setter, string? value, BookBriefField field, IReadOnlySet<BookBriefField> cleared)
    {
        if (value is not null && !cleared.Contains(field))
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
