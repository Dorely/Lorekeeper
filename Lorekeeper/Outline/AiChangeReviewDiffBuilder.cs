using System.Globalization;
using System.Text;
using System.Text.Json;
using Lorekeeper.Context;
using Lorekeeper.Fonts;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;

namespace Lorekeeper.Outline;

public static class AiChangeReviewDiffBuilder
{
    private const int MaxLineDiffCells = 1_000_000;
    private const int MaxFuzzyPairCells = 200_000;
    private const int MaxTokenDiffCells = 80_000;
    private static readonly JsonSerializerOptions ChangePayloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static bool IsChapterManuscriptChangeFor(AiChange change, Guid chapterId)
    {
        if (!string.Equals(change.ResourceKind, "ChapterManuscript", StringComparison.OrdinalIgnoreCase))
            return false;

        return ReadChapterManuscriptChange(change.BeforeJson)?.Id == chapterId
            || ReadChapterManuscriptChange(AiChangeReviewDrafts.EffectiveAfterJson(change))?.Id == chapterId;
    }

    public static bool CanUseChapterLineReview(AiChange change)
    {
        if (!string.Equals(change.ResourceKind, "ChapterManuscript", StringComparison.OrdinalIgnoreCase))
            return false;

        var before = ReadChapterManuscriptChange(change.BeforeJson);
        var after = ReadChapterManuscriptChange(AiChangeReviewDrafts.EffectiveAfterJson(change));
        return before is not null
            && after is not null
            && before.Id == after.Id
            && ManuscriptCodec.IsPlainTextOnly(before.Manuscript)
            && ManuscriptCodec.IsPlainTextOnly(after.Manuscript);
    }

    public static bool TryBuild(IReadOnlyList<AiChange> changes, out ReviewDiff diff)
    {
        diff = null!;
        if (changes.Count == 0) return false;

        var orderedChanges = changes
            .OrderBy(change => change.Batch.CreatedAt)
            .ThenBy(change => change.Order)
            .ToList();
        if (orderedChanges.Count == 1 && TryBuild(orderedChanges[0], out diff))
            return true;

        if (!TryBuildGrouped(orderedChanges, out diff))
            return false;

        if (orderedChanges.Count > 1)
        {
            diff = diff with
            {
                Subtitle = string.IsNullOrWhiteSpace(diff.Subtitle)
                    ? $"{orderedChanges.Count} tool changes"
                    : $"{diff.Subtitle} ({orderedChanges.Count} tool changes)",
            };
        }
        return true;
    }

    public static bool TryBuild(AiChange change, out ReviewDiff diff)
    {
        diff = null!;

        if (AiChangeReviewDrafts.TryGetEditablePayload(change, out var editablePayload))
        {
            var fields = editablePayload.Fields
                .Select(field => new DiffFieldInput(field.Key, field.Label, field.OldText, field.NewText, EditableOwnerChangeId(change, hasAfterPayload: true)))
                .ToList();

            diff = Build(
                editablePayload.Title,
                editablePayload.Subtitle,
                fields,
                showSingleFieldLabel: editablePayload.Fields.Count != 1
                    || !string.Equals(editablePayload.Fields[0].Key, "Body", StringComparison.OrdinalIgnoreCase));
            return true;
        }

        if (string.Equals(change.ResourceKind, "LegacyChapterBodyAudit", StringComparison.OrdinalIgnoreCase)
            && TryReadLegacyAudit(change.BeforeJson, out var legacyBefore)
            && TryReadLegacyAudit(change.AfterJson, out var legacyAfter))
        {
            diff = Build(
                "Legacy chapter-body audit",
                legacyAfter.Title,
                [new DiffFieldInput("Body", "Body", legacyBefore.Body, legacyAfter.Body, OwnerChangeId: null)],
                showSingleFieldLabel: false);
            return true;
        }

        if (string.Equals(change.ResourceKind, "ChapterManuscript", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryReadChange<ChapterManuscriptChange>(change.BeforeJson, out var before)
                || !TryReadChange<ChapterManuscriptChange>(AiChangeReviewDrafts.EffectiveAfterJson(change), out var after))
            {
                return false;
            }

            diff = Build(
                "Chapter body",
                after.Title,
                BuildChapterManuscriptFields(
                    before,
                    after,
                    EditableOwnerChangeId(change, hasAfterPayload: true)),
                showSingleFieldLabel: true);
            return true;
        }

        if (string.Equals(change.ResourceKind, "ManuscriptStyle", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryReadChange<ManuscriptStyleChange>(change.BeforeJson, out var beforePayload)
                || !TryReadChange<ManuscriptStyleChange>(
                    AiChangeReviewDrafts.EffectiveAfterJson(change),
                    out var afterPayload))
            {
                return false;
            }

            var before = beforePayload.Before;
            var input = afterPayload.After;
            diff = Build(
                input is null ? "Delete Book Text Style" : before is null ? "Create Book Text Style" : "Update Book Text Style",
                input?.Name ?? before?.Name,
                [
                    new DiffFieldInput("Name", "Name", before?.Name ?? "(not set)", input?.Name ?? "(deleted)", null),
                    new DiffFieldInput("Kind", "Kind", before?.Kind.ToString() ?? "(not set)", input?.Kind.ToString() ?? "(deleted)", null),
                    new DiffFieldInput("SemanticRole", "Semantic role", before?.SemanticRole ?? "(not set)", input?.SemanticRole ?? "(deleted)", null),
                    new DiffFieldInput(
                        "Definition",
                        "Definition",
                        FormatStyleDefinition(before?.Definition),
                        FormatStyleDefinition(input?.Definition),
                        null),
                ]);
            return true;
        }

        if (string.Equals(change.ToolName, "update_act", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryReadChange<OutlineActChange>(change.BeforeJson, out var before)
                || !TryReadChange<OutlineActChange>(AiChangeReviewDrafts.EffectiveAfterJson(change), out var after))
            {
                return false;
            }

            diff = Build(
                "Act edit",
                after.Title,
                [
                    new DiffFieldInput("Title", before.Title, after.Title, EditableOwnerChangeId(change, hasAfterPayload: true)),
                    new DiffFieldInput("Synopsis", before.Synopsis, after.Synopsis, EditableOwnerChangeId(change, hasAfterPayload: true)),
                ]);
            return true;
        }

        if (string.Equals(change.ToolName, "update_chapter", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryReadChange<OutlineChapterChange>(change.BeforeJson, out var before)
                || !TryReadChange<OutlineChapterChange>(AiChangeReviewDrafts.EffectiveAfterJson(change), out var after))
            {
                return false;
            }

            diff = Build(
                "Chapter edit",
                after.Title,
                [
                    new DiffFieldInput("Title", before.Title, after.Title, EditableOwnerChangeId(change, hasAfterPayload: true)),
                    new DiffFieldInput("Synopsis", before.Synopsis, after.Synopsis, EditableOwnerChangeId(change, hasAfterPayload: true)),
                ]);
            return true;
        }

        if (string.Equals(change.ToolName, "update_entity", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryReadChange<OutlineEntityChange>(change.BeforeJson, out var before)
                || !TryReadChange<OutlineEntityChange>(AiChangeReviewDrafts.EffectiveAfterJson(change), out var after))
            {
                return false;
            }

            var fields = new List<DiffFieldInput>
            {
                new("Name", before.Name, after.Name, EditableOwnerChangeId(change, hasAfterPayload: true)),
            };
            var propertyNames = before.Properties.Keys
                .Concat(after.Properties.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(propertyName => propertyName, StringComparer.OrdinalIgnoreCase);
            foreach (var propertyName in propertyNames)
            {
                var beforeExists = before.Properties.TryGetValue(propertyName, out var beforeValue);
                var afterExists = after.Properties.TryGetValue(propertyName, out var afterValue);
                if (beforeExists == afterExists && string.Equals(beforeValue, afterValue, StringComparison.Ordinal))
                    continue;

                fields.Add(new DiffFieldInput(
                    $"properties.{propertyName}",
                    beforeExists ? beforeValue ?? string.Empty : "(not set)",
                    afterExists ? afterValue ?? string.Empty : "(not set)",
                    EditableOwnerChangeId(change, hasAfterPayload: true)));
            }

            diff = Build("Entity edit", after.Name, fields);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Builds the same semantic manuscript diff used by pending Review changes
    /// from a durable pre-assistant document and the live current document.
    /// The resulting diff is intentionally read-only and has no owning change.
    /// </summary>
    public static bool TryBuild(
        ManuscriptDocument before,
        ManuscriptDocument current,
        string title,
        out ReviewDiff diff)
    {
        var beforePayload = new ChapterManuscriptChange(
            before.ManuscriptId,
            title,
            before.Revision,
            ManuscriptCodec.Serialize(before));
        var currentPayload = new ChapterManuscriptChange(
            current.ManuscriptId,
            title,
            current.Revision,
            ManuscriptCodec.Serialize(current));
        var change = new AiChange
        {
            ResourceKind = "ChapterManuscript",
            BeforeJson = JsonSerializer.Serialize(beforePayload, ChangePayloadJsonOptions),
            AfterJson = JsonSerializer.Serialize(currentPayload, ChangePayloadJsonOptions),
            Status = AiChangeStatus.Resolved,
        };
        return TryBuild(change, out diff);
    }

    private static bool TryBuildGrouped(IReadOnlyList<AiChange> changes, out ReviewDiff diff)
    {
        diff = null!;
        var title = string.Empty;
        var subtitle = string.Empty;
        var fields = new Dictionary<string, DiffFieldInput>(StringComparer.OrdinalIgnoreCase);

        foreach (var change in changes)
        {
            if (!TryAddChangeFields(change, fields, ref title, ref subtitle))
                return false;
        }

        if (fields.Count == 0) return false;

        diff = Build(
            string.IsNullOrWhiteSpace(title) ? "Grouped changes" : title,
            string.IsNullOrWhiteSpace(subtitle) ? null : subtitle,
            fields.Values.ToList());
        return true;
    }

    private static bool TryAddChangeFields(
        AiChange change,
        Dictionary<string, DiffFieldInput> fields,
        ref string title,
        ref string subtitle)
    {
        if (string.Equals(change.ResourceKind, "ChapterManuscript", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryReadChange<ChapterManuscriptChange>(change.BeforeJson, out var before)
                || !TryReadChange<ChapterManuscriptChange>(AiChangeReviewDrafts.EffectiveAfterJson(change), out var after))
            {
                return false;
            }

            SetTitle(ref title, "Chapter changes");
            SetSubtitle(ref subtitle, after.Title);
            AddOrUpdateField(fields, "Body", before.PlainText, after.PlainText, EditableOwnerChangeId(change, hasAfterPayload: true));
            AddOrUpdateField(
                fields,
                "Structure and formatting",
                FormatManuscriptStructure(before.Manuscript, after.Manuscript, isCurrent: false),
                FormatManuscriptStructure(after.Manuscript, before.Manuscript, isCurrent: true),
                ownerChangeId: null);
            AddOrUpdateField(
                fields,
                "Figures and designed pages",
                FormatManuscriptVisualBlocks(before.Manuscript, after.Manuscript, isCurrent: false),
                FormatManuscriptVisualBlocks(after.Manuscript, before.Manuscript, isCurrent: true),
                ownerChangeId: null);
            return true;
        }

        if (IsTool(change, "create_act", "update_act", "delete_act"))
        {
            var before = ReadOptional<OutlineActChange>(change.BeforeJson);
            var after = ReadOptional<OutlineActChange>(AiChangeReviewDrafts.EffectiveAfterJson(change));
            if (before is null && after is null) return false;

            SetTitle(ref title, "Act changes");
            SetSubtitle(ref subtitle, after?.Title ?? before?.Title ?? string.Empty);
            var ownerChangeId = EditableOwnerChangeId(change, after is not null);
            AddOrUpdateField(fields, "Title", before?.Title ?? string.Empty, after?.Title ?? string.Empty, ownerChangeId);
            AddOrUpdateField(fields, "Synopsis", before?.Synopsis ?? string.Empty, after?.Synopsis ?? string.Empty, ownerChangeId);
            return true;
        }

        if (IsTool(change, "create_chapter", "update_chapter", "delete_chapter"))
        {
            var before = ReadOptional<OutlineChapterChange>(change.BeforeJson);
            var after = ReadOptional<OutlineChapterChange>(AiChangeReviewDrafts.EffectiveAfterJson(change));
            if (before is null && after is null) return false;

            SetTitle(ref title, "Chapter changes");
            SetSubtitle(ref subtitle, after?.Title ?? before?.Title ?? string.Empty);
            var ownerChangeId = EditableOwnerChangeId(change, after is not null);
            AddOrUpdateField(fields, "Title", before?.Title ?? string.Empty, after?.Title ?? string.Empty, ownerChangeId);
            AddOrUpdateField(fields, "Synopsis", before?.Synopsis ?? string.Empty, after?.Synopsis ?? string.Empty, ownerChangeId);
            return true;
        }

        if (string.Equals(change.ResourceKind, "ManuscriptStyle", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryReadChange<ManuscriptStyleChange>(change.BeforeJson, out var beforePayload)
                || !TryReadChange<ManuscriptStyleChange>(
                    AiChangeReviewDrafts.EffectiveAfterJson(change),
                    out var afterPayload))
            {
                return false;
            }

            var before = beforePayload.Before;
            var input = afterPayload.After;
            SetTitle(ref title, "Book Text Style changes");
            SetSubtitle(ref subtitle, input?.Name ?? before?.Name ?? string.Empty);
            var prefix = $"Style {input?.Id ?? before?.Id}: ";
            AddOrUpdateField(
                fields,
                prefix + "Name",
                before?.Name ?? "(not set)",
                input?.Name ?? "(deleted)",
                null);
            AddOrUpdateField(
                fields,
                prefix + "Kind",
                before?.Kind.ToString() ?? "(not set)",
                input?.Kind.ToString() ?? "(deleted)",
                null);
            AddOrUpdateField(
                fields,
                prefix + "Semantic role",
                before?.SemanticRole ?? "(not set)",
                input?.SemanticRole ?? "(deleted)",
                null);
            AddOrUpdateField(
                fields,
                prefix + "Definition",
                FormatStyleDefinition(before?.Definition),
                FormatStyleDefinition(input?.Definition),
                null);
            return true;
        }

        if (IsTool(change, "create_entity", "update_entity", "delete_entity"))
        {
            var before = ReadOptional<OutlineEntityChange>(change.BeforeJson);
            var after = ReadOptional<OutlineEntityChange>(AiChangeReviewDrafts.EffectiveAfterJson(change));
            if (before is null && after is null) return false;

            var type = after?.Type ?? before?.Type ?? "Entity";
            SetTitle(ref title, $"{type} changes");
            SetSubtitle(ref subtitle, after?.Name ?? before?.Name ?? string.Empty);
            var ownerChangeId = EditableOwnerChangeId(change, after is not null);
            AddOrUpdateField(fields, "Name", before?.Name ?? string.Empty, after?.Name ?? string.Empty, ownerChangeId);

            var beforePropertyNames = before?.Properties.Keys ?? Enumerable.Empty<string>();
            var afterPropertyNames = after?.Properties.Keys ?? Enumerable.Empty<string>();
            var propertyNames = beforePropertyNames
                .Concat(afterPropertyNames)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(propertyName => propertyName, StringComparer.OrdinalIgnoreCase);
            foreach (var propertyName in propertyNames)
            {
                var beforeValue = GetPropertyValue(before?.Properties, propertyName, out var beforeExists);
                var afterValue = GetPropertyValue(after?.Properties, propertyName, out var afterExists);
                AddOrUpdateField(
                    fields,
                    $"properties.{propertyName}",
                    beforeExists ? beforeValue : "(not set)",
                    afterExists ? afterValue : "(not set)",
                    ownerChangeId);
            }

            return true;
        }

        return false;
    }

    private static bool IsTool(AiChange change, params string[] toolNames) =>
        toolNames.Any(toolName => string.Equals(change.ToolName, toolName, StringComparison.OrdinalIgnoreCase));

    private static void SetTitle(ref string title, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = candidate;
            return;
        }

        if (!string.Equals(title, candidate, StringComparison.OrdinalIgnoreCase))
            title = "Grouped changes";
    }

    private static void SetSubtitle(ref string subtitle, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return;
        subtitle = candidate;
    }

    private static Guid? EditableOwnerChangeId(AiChange change, bool hasAfterPayload) =>
        hasAfterPayload
        && change.Status == AiChangeStatus.Pending
        && AiChangeReviewDrafts.TryGetEditablePayload(change, out _)
            ? change.Id
            : null;

    private static void AddOrUpdateField(Dictionary<string, DiffFieldInput> fields, string key, string oldText, string newText, Guid? ownerChangeId)
    {
        if (fields.TryGetValue(key, out var existing))
        {
            fields[key] = existing with { NewText = newText, OwnerChangeId = ownerChangeId };
            return;
        }

        fields[key] = new DiffFieldInput(key, oldText, newText, ownerChangeId);
    }

    private static IReadOnlyList<DiffFieldInput> BuildChapterManuscriptFields(
        ChapterManuscriptChange before,
        ChapterManuscriptChange after,
        Guid? editableOwnerChangeId) =>
        [
            new DiffFieldInput(
                "Body",
                "Body",
                before.PlainText,
                after.PlainText,
                editableOwnerChangeId),
            new DiffFieldInput(
                "Structure",
                "Structure and formatting",
                FormatManuscriptStructure(before.Manuscript, after.Manuscript, isCurrent: false),
                FormatManuscriptStructure(after.Manuscript, before.Manuscript, isCurrent: true),
                OwnerChangeId: null),
            new DiffFieldInput(
                "Visuals",
                "Figures and designed pages",
                FormatManuscriptVisualBlocks(before.Manuscript, after.Manuscript, isCurrent: false),
                FormatManuscriptVisualBlocks(after.Manuscript, before.Manuscript, isCurrent: true),
                OwnerChangeId: null),
        ];

    private static string FormatManuscriptStructure(
        ManuscriptDocument manuscript,
        ManuscriptDocument? counterpart,
        bool isCurrent)
    {
        var builder = new StringBuilder();
        for (var blockIndex = 0; blockIndex < manuscript.Content.Count; blockIndex++)
        {
            var block = manuscript.Content[blockIndex];
            var counterpartBlock = FindCounterpartBlock(block, blockIndex, counterpart);
            builder
                .Append("Block ")
                .Append(blockIndex + 1)
                .Append(": ")
                .Append(ReadableBlockType(block.Type))
                .AppendLine();
            AppendDisplayProperty(
                builder,
                "Style",
                ReadableStyleRole(block.StyleRole, counterpartBlock?.StyleRole, isCurrent));
            if (block.HeadingLevel is not null)
                AppendDisplayProperty(builder, "Heading level", block.HeadingLevel.Value);
            if (block.AltText is not null)
                AppendDisplayProperty(builder, "Alternative text", JsonSerializer.Serialize(block.AltText));
            AppendParagraphPresentation(builder, block, counterpartBlock, isCurrent);

            for (var inlineIndex = 0; inlineIndex < block.Content.Count; inlineIndex++)
            {
                var inline = block.Content[inlineIndex];
                var counterpartInline = counterpartBlock?.Content.ElementAtOrDefault(inlineIndex);
                builder
                    .Append("  Text span ")
                    .Append(inlineIndex + 1)
                    .Append(" formatting: ");
                builder.Append(inline.Marks.Count == 0
                    ? "none"
                    : string.Join(", ", inline.Marks.Select(mark => ReadableInlineMark(
                        mark,
                        counterpartInline?.Marks.FirstOrDefault(item => item.Type == mark.Type),
                        isCurrent))));
                builder.AppendLine();
            }
        }
        return builder.ToString().TrimEnd();
    }

    private static string FormatManuscriptVisualBlocks(
        ManuscriptDocument manuscript,
        ManuscriptDocument? counterpart,
        bool isCurrent)
    {
        var builder = new StringBuilder();
        for (var blockIndex = 0; blockIndex < manuscript.Content.Count; blockIndex++)
        {
            var block = manuscript.Content[blockIndex];
            var counterpartBlock = FindCounterpartBlock(block, blockIndex, counterpart);
            if (block.Type == ManuscriptBlockType.Figure)
            {
                builder
                    .Append("Block ")
                    .Append(blockIndex + 1)
                    .Append(": Figure")
                    .AppendLine();
                AppendDisplayProperty(builder, "Caption", JsonSerializer.Serialize(ManuscriptCodec.Text(block)));
                AppendDisplayProperty(builder, "Image", ReadableFigureReference(block.ImageId, counterpartBlock, isCurrent));
                AppendDisplayProperty(builder, "Decorative", block.Decorative ? "Yes" : "No");
                AppendDisplayProperty(builder, "Alternative text", JsonSerializer.Serialize(block.AltText));
                AppendDisplayProperty(builder, "Language", JsonSerializer.Serialize(block.Language));
                AppendDisplayProperty(
                    builder,
                    "Accessibility role",
                    block.AccessibilityRole is { } accessibilityRole
                        ? DisplayEnumValue(accessibilityRole)
                        : "Not specified");
                AppendFigurePresentation(builder, block.FigurePresentation);
            }
            else if (block.Type == ManuscriptBlockType.DesignedPage)
            {
                builder
                    .Append("Block ")
                    .Append(blockIndex + 1)
                    .Append(": Designed Page")
                    .AppendLine();
                AppendDisplayProperty(
                    builder,
                    "Page design",
                    ReadablePageDesignReference(block.PageCompositionId, counterpartBlock, isCurrent));
            }
        }

        return builder.Length == 0 ? "(none)" : builder.ToString().TrimEnd();
    }

    private static ManuscriptBlock? FindCounterpartBlock(
        ManuscriptBlock block,
        int blockIndex,
        ManuscriptDocument? counterpart)
    {
        if (counterpart is null)
            return null;

        return counterpart.Content.FirstOrDefault(item => string.Equals(item.Id, block.Id, StringComparison.Ordinal))
            ?? (blockIndex < counterpart.Content.Count ? counterpart.Content[blockIndex] : null);
    }

    private static void AppendDisplayProperty(StringBuilder builder, string label, object? value) =>
        builder.Append("  ").Append(label).Append(": ").Append(value).AppendLine();

    private static void AppendParagraphPresentation(
        StringBuilder builder,
        ManuscriptBlock block,
        ManuscriptBlock? counterpart,
        bool isCurrent)
    {
        var presentation = block.ParagraphPresentation;
        if (presentation is null)
            return;

        if (presentation.FontFamilyKey is not null)
        {
            AppendDisplayProperty(
                builder,
                "Paragraph font family",
                ReadableFontFamily(presentation.FontFamilyKey, counterpart?.ParagraphPresentation?.FontFamilyKey, isCurrent));
        }
        if (presentation.FontSizePoints is { } fontSize)
            AppendDisplayProperty(builder, "Paragraph font size", $"{fontSize.ToString("0.##", CultureInfo.InvariantCulture)} pt");
        if (presentation.FontWeight is { } fontWeight)
            AppendDisplayProperty(builder, "Paragraph font weight", fontWeight.ToString(CultureInfo.InvariantCulture));
        if (presentation.Italic is { } italic)
            AppendDisplayProperty(builder, "Paragraph italic", italic ? "On" : "Off");
        if (presentation.SmallCaps is { } smallCaps)
            AppendDisplayProperty(builder, "Paragraph small caps", smallCaps ? "On" : "Off");
        if (presentation.LineHeight is { } lineHeight)
            AppendDisplayProperty(builder, "Line height", lineHeight.ToString("0.##", CultureInfo.InvariantCulture));
        if (presentation.Alignment is { } alignment)
            AppendDisplayProperty(builder, "Paragraph alignment", DisplayEnumValue(alignment));
        if (presentation.LeftIndentEm is { } leftIndent)
            AppendDisplayProperty(builder, "Left indent", $"{leftIndent.ToString("0.##", CultureInfo.InvariantCulture)} em");
        if (presentation.RightIndentEm is { } rightIndent)
            AppendDisplayProperty(builder, "Right indent", $"{rightIndent.ToString("0.##", CultureInfo.InvariantCulture)} em");
        if (presentation.FirstLineIndentEm is { } firstLineIndent)
            AppendDisplayProperty(builder, "First-line indent", $"{firstLineIndent.ToString("0.##", CultureInfo.InvariantCulture)} em");
        if (presentation.SpacingBeforePoints is { } spacingBefore)
            AppendDisplayProperty(builder, "Spacing before", $"{spacingBefore.ToString("0.##", CultureInfo.InvariantCulture)} pt");
        if (presentation.SpacingAfterPoints is { } spacingAfter)
            AppendDisplayProperty(builder, "Spacing after", $"{spacingAfter.ToString("0.##", CultureInfo.InvariantCulture)} pt");
        if (presentation.KeepWithNext is { } keepWithNext)
            AppendDisplayProperty(builder, "Keep with next", keepWithNext ? "On" : "Off");
        if (presentation.StartOnNewPage is { } startOnNewPage)
            AppendDisplayProperty(builder, "Start on new page", startOnNewPage ? "On" : "Off");
    }

    private static void AppendFigurePresentation(StringBuilder builder, FigurePresentation? presentation)
    {
        if (presentation is null)
            return;

        AppendDisplayProperty(builder, "Placement", DisplayEnumValue(presentation.Placement));
        AppendDisplayProperty(builder, "Width", $"{presentation.WidthPercent.ToString("0.##", CultureInfo.InvariantCulture)}%");
        AppendDisplayProperty(builder, "Alignment", DisplayEnumValue(presentation.Alignment));
        AppendDisplayProperty(builder, "Text wrap", DisplayEnumValue(presentation.TextWrap));
        AppendDisplayProperty(builder, "Image fit", DisplayEnumValue(presentation.Fit));
        AppendDisplayProperty(builder, "Horizontal crop", $"{presentation.CropXPercent.ToString("0.##", CultureInfo.InvariantCulture)}%");
        AppendDisplayProperty(builder, "Vertical crop", $"{presentation.CropYPercent.ToString("0.##", CultureInfo.InvariantCulture)}%");
        AppendDisplayProperty(builder, "Spacing before", $"{presentation.SpacingBeforePoints.ToString("0.##", CultureInfo.InvariantCulture)} pt");
        AppendDisplayProperty(builder, "Spacing after", $"{presentation.SpacingAfterPoints.ToString("0.##", CultureInfo.InvariantCulture)} pt");
        AppendDisplayProperty(builder, "Start on new page", presentation.StartOnNewPage ? "Yes" : "No");
        AppendDisplayProperty(builder, "Keep with caption", presentation.KeepWithCaption ? "Yes" : "No");
        AppendDisplayProperty(builder, "Caption placement", DisplayEnumValue(presentation.CaptionPlacement));
    }

    private static string DisplayEnumValue<T>(T value)
        where T : struct, Enum
    {
        var source = value.ToString();
        var builder = new StringBuilder(source.Length + 4);
        for (var index = 0; index < source.Length; index++)
        {
            if (index > 0 && char.IsUpper(source[index]) && !char.IsUpper(source[index - 1]))
                builder.Append(' ');
            builder.Append(index == 0 ? source[index] : char.ToLowerInvariant(source[index]));
        }
        return builder.ToString();
    }

    private static string ReadableFontFamily(string key, string? counterpartKey, bool isCurrent)
    {
        if (PublicationBuiltInFonts.Find(key) is { } builtIn)
            return builtIn.Name;

        var changed = counterpartKey is not null
            && !string.Equals(key, counterpartKey, StringComparison.Ordinal);
        if (key.StartsWith("project:", StringComparison.OrdinalIgnoreCase))
            return changed ? isCurrent ? "Current imported font" : "Previous imported font" : "Imported font";

        return changed ? isCurrent ? "Current custom font" : "Previous custom font" : "Custom font";
    }

    private static string ReadableInlineMark(ManuscriptMark mark, ManuscriptMark? counterpart, bool isCurrent)
    {
        var label = mark.Type switch
        {
            ManuscriptMarkType.Emphasis => "Italic",
            ManuscriptMarkType.Strong => "Bold",
            ManuscriptMarkType.Underline => "Underline",
            ManuscriptMarkType.Strikethrough => "Strikethrough",
            ManuscriptMarkType.Code => "Code",
            ManuscriptMarkType.Link => "Link",
            ManuscriptMarkType.Language => "Language",
            ManuscriptMarkType.SmallCaps => "Small caps",
            ManuscriptMarkType.Superscript => "Superscript",
            ManuscriptMarkType.Subscript => "Subscript",
            ManuscriptMarkType.CharacterStyle => "Character style",
            _ => "Formatting",
        };
        return counterpart is not null
            && !string.Equals(mark.Value, counterpart.Value, StringComparison.Ordinal)
            ? isCurrent ? $"Current {label.ToLowerInvariant()}" : $"Previous {label.ToLowerInvariant()}"
            : label;
    }

    private static string ReadableFigureReference(Guid? reference, ManuscriptBlock? counterpart, bool isCurrent) =>
        counterpart is not null && reference != counterpart.ImageId
            ? reference is null ? "No image" : isCurrent ? "Current image" : "Previous image"
            : reference is null ? "No image" : "Image";

    private static string ReadablePageDesignReference(Guid? reference, ManuscriptBlock? counterpart, bool isCurrent) =>
        counterpart is not null && reference != counterpart.PageCompositionId
            ? reference is null ? "No page design" : isCurrent ? "Current page design" : "Previous page design"
            : reference is null ? "No page design" : "Page design";

    private static string ReadableBlockType(ManuscriptBlockType type) => type switch
    {
        ManuscriptBlockType.Paragraph => "Paragraph",
        ManuscriptBlockType.Heading => "Heading",
        ManuscriptBlockType.SceneBreak => "Scene break",
        ManuscriptBlockType.BlockQuote => "Block quote",
        ManuscriptBlockType.ListItem => "List item",
        ManuscriptBlockType.Figure => "Figure",
        ManuscriptBlockType.DesignedPage => "Designed Page",
        _ => "Block",
    };

    private static string ReadableStyleRole(string styleRole, string? counterpartRole, bool isCurrent) => styleRole switch
    {
        ManuscriptStyleRoles.Body => "Body",
        ManuscriptStyleRoles.ChapterHeading => "Chapter heading",
        ManuscriptStyleRoles.Subheading => "Subheading",
        ManuscriptStyleRoles.SceneBreak => "Scene break",
        ManuscriptStyleRoles.BlockQuote => "Block quote",
        ManuscriptStyleRoles.ListItem => "List item",
        ManuscriptStyleRoles.FigureCaption => "Figure caption",
        ManuscriptStyleRoles.DesignedPage => "Designed Page",
        _ when counterpartRole is not null
            && !string.Equals(styleRole, counterpartRole, StringComparison.Ordinal)
            => isCurrent ? "Current custom style" : "Previous custom style",
        _ => "Custom style",
    };

    private static string GetPropertyValue(Dictionary<string, string?>? properties, string propertyName, out bool exists)
    {
        if (properties is not null && properties.TryGetValue(propertyName, out var value))
        {
            exists = true;
            return value ?? string.Empty;
        }

        exists = false;
        return string.Empty;
    }

    private static T? ReadOptional<T>(string json)
        where T : class =>
        TryReadChange<T>(json, out var value) ? value : null;

    private static ChapterManuscriptChange? ReadChapterManuscriptChange(string json)
    {
        if (!TryReadChange<ChapterManuscriptChange>(json, out var change)) return null;
        try
        {
            _ = change.Manuscript;
            return change;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    private static bool TryReadChange<T>(string json, out T value)
        where T : class
    {
        value = null!;
        if (string.IsNullOrWhiteSpace(json) || json == "null") return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<T>(json, ChangePayloadJsonOptions);
            if (parsed is null) return false;
            value = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadLegacyAudit(string json, out LegacyChapterBody payload)
    {
        payload = null!;
        try
        {
            var snapshot = JsonSerializer.Deserialize<LegacyAuditSnapshot>(json, ChangePayloadJsonOptions);
            if (snapshot is null
                || !string.Equals(snapshot.Format, "legacy-chapter-body-v7", StringComparison.Ordinal)
                || snapshot.Payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            payload = snapshot.Payload.Deserialize<LegacyChapterBody>(ChangePayloadJsonOptions)!;
            return payload is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ReviewDiff Build(string title, string? subtitle, IReadOnlyList<DiffFieldInput> fields, bool showSingleFieldLabel = true)
    {
        var changedFields = fields
            .Where(field => !FieldTextEquals(field.OldText, field.NewText))
            .ToList();
        var showLabels = showSingleFieldLabel || changedFields.Count > 1;
        var sections = changedFields
            .Select(field => new DiffSection(
                field.Key,
                showLabels ? field.Label : string.Empty,
                field.OldText,
                field.NewText,
                BuildDiffHunks(BuildDiffRows(field.OldText, field.NewText)),
                field.OwnerChangeId))
            .Where(section => section.Hunks.Count > 0)
            .ToList();

        return new ReviewDiff(title, subtitle, sections);
    }

    private static string FormatStyleDefinition(ManuscriptStyleProperties? definition) =>
        definition is null
            ? "(not set)"
            : JsonSerializer.Serialize(
                definition,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

    private static bool FieldTextEquals(string oldText, string newText)
    {
        var oldLines = ChapterFormatting.SplitLines(oldText);
        var newLines = ChapterFormatting.SplitLines(newText);
        return oldLines.SequenceEqual(newLines, StringComparer.Ordinal);
    }

    private static IReadOnlyList<DiffRow> BuildDiffRows(string oldText, string newText)
    {
        var oldLines = ChapterFormatting.SplitLines(oldText);
        var newLines = ChapterFormatting.SplitLines(newText);
        var rows = new List<DiffRow>();
        var nextPairId = 1;
        AppendRange(oldLines, newLines, 0, oldLines.Count, 0, newLines.Count, rows, ref nextPairId);
        return rows;
    }

    private static void AppendRange(
        IReadOnlyList<string> oldLines,
        IReadOnlyList<string> newLines,
        int oldStart,
        int oldEnd,
        int newStart,
        int newEnd,
        List<DiffRow> rows,
        ref int nextPairId)
    {
        while (oldStart < oldEnd && newStart < newEnd && oldLines[oldStart] == newLines[newStart])
        {
            rows.Add(ContextRow(oldStart + 1, newStart + 1, oldLines[oldStart]));
            oldStart++;
            newStart++;
        }

        var suffixRows = new List<DiffRow>();
        while (oldStart < oldEnd && newStart < newEnd && oldLines[oldEnd - 1] == newLines[newEnd - 1])
        {
            oldEnd--;
            newEnd--;
            suffixRows.Add(ContextRow(oldEnd + 1, newEnd + 1, oldLines[oldEnd]));
        }

        if (oldStart == oldEnd)
        {
            AppendAddedRows(newLines, newStart, newEnd, rows);
            AppendSuffixRows(suffixRows, rows);
            return;
        }

        if (newStart == newEnd)
        {
            AppendRemovedRows(oldLines, oldStart, oldEnd, rows);
            AppendSuffixRows(suffixRows, rows);
            return;
        }

        var anchors = FindUniqueAnchors(oldLines, newLines, oldStart, oldEnd, newStart, newEnd);
        if (anchors.Count > 0)
        {
            foreach (var anchor in anchors)
            {
                AppendRange(oldLines, newLines, oldStart, anchor.OldIndex, newStart, anchor.NewIndex, rows, ref nextPairId);
                if (oldLines[anchor.OldIndex] == newLines[anchor.NewIndex])
                {
                    rows.Add(ContextRow(anchor.OldIndex + 1, anchor.NewIndex + 1, oldLines[anchor.OldIndex]));
                }
                else
                {
                    AppendModifiedRows(oldLines[anchor.OldIndex], newLines[anchor.NewIndex], anchor.OldIndex + 1, anchor.NewIndex + 1, rows, ref nextPairId);
                }

                oldStart = anchor.OldIndex + 1;
                newStart = anchor.NewIndex + 1;
            }

            AppendRange(oldLines, newLines, oldStart, oldEnd, newStart, newEnd, rows, ref nextPairId);
            AppendSuffixRows(suffixRows, rows);
            return;
        }

        AppendUnanchoredRange(oldLines, newLines, oldStart, oldEnd, newStart, newEnd, rows, ref nextPairId);
        AppendSuffixRows(suffixRows, rows);
    }

    private static void AppendSuffixRows(IReadOnlyList<DiffRow> suffixRows, List<DiffRow> rows)
    {
        for (var index = suffixRows.Count - 1; index >= 0; index--)
            rows.Add(suffixRows[index]);
    }

    private static IReadOnlyList<Anchor> FindUniqueAnchors(
        IReadOnlyList<string> oldLines,
        IReadOnlyList<string> newLines,
        int oldStart,
        int oldEnd,
        int newStart,
        int newEnd)
    {
        var oldOccurrences = BuildLineOccurrences(oldLines, oldStart, oldEnd);
        var newOccurrences = BuildLineOccurrences(newLines, newStart, newEnd);
        var candidates = new List<Anchor>();
        foreach (var (key, oldIndexes) in oldOccurrences)
        {
            if (oldIndexes.Count != 1) continue;
            if (!newOccurrences.TryGetValue(key, out var newIndexes) || newIndexes.Count != 1) continue;
            candidates.Add(new Anchor(oldIndexes[0], newIndexes[0]));
        }

        return SelectIncreasingAnchors(candidates
            .OrderBy(anchor => anchor.OldIndex)
            .ThenBy(anchor => anchor.NewIndex)
            .ToList());
    }

    private static Dictionary<string, List<int>> BuildLineOccurrences(IReadOnlyList<string> lines, int start, int end)
    {
        var occurrences = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var index = start; index < end; index++)
        {
            var key = NormalizeLineKey(lines[index]);
            if (key is null) continue;
            if (!occurrences.TryGetValue(key, out var indexes))
            {
                indexes = [];
                occurrences[key] = indexes;
            }

            indexes.Add(index);
        }

        return occurrences;
    }

    private static string? NormalizeLineKey(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return null;

        var builder = new StringBuilder(trimmed.Length);
        var inWhitespace = false;
        foreach (var character in trimmed)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!inWhitespace)
                {
                    builder.Append(' ');
                    inWhitespace = true;
                }
            }
            else
            {
                builder.Append(char.ToUpperInvariant(character));
                inWhitespace = false;
            }
        }

        return builder.ToString();
    }

    private static IReadOnlyList<Anchor> SelectIncreasingAnchors(IReadOnlyList<Anchor> candidates)
    {
        if (candidates.Count <= 1) return candidates;

        var lengths = Enumerable.Repeat(1, candidates.Count).ToArray();
        var previous = Enumerable.Repeat(-1, candidates.Count).ToArray();
        var bestIndex = 0;
        for (var index = 0; index < candidates.Count; index++)
        {
            for (var prior = 0; prior < index; prior++)
            {
                if (candidates[prior].NewIndex >= candidates[index].NewIndex) continue;
                if (lengths[prior] + 1 <= lengths[index]) continue;

                lengths[index] = lengths[prior] + 1;
                previous[index] = prior;
            }

            if (lengths[index] > lengths[bestIndex])
                bestIndex = index;
        }

        var selected = new List<Anchor>();
        for (var index = bestIndex; index >= 0; index = previous[index])
        {
            selected.Add(candidates[index]);
            if (previous[index] < 0) break;
        }

        selected.Reverse();
        return selected;
    }

    private static void AppendUnanchoredRange(
        IReadOnlyList<string> oldLines,
        IReadOnlyList<string> newLines,
        int oldStart,
        int oldEnd,
        int newStart,
        int newEnd,
        List<DiffRow> rows,
        ref int nextPairId)
    {
        var oldLength = oldEnd - oldStart;
        var newLength = newEnd - newStart;
        if ((long)oldLength * newLength > MaxLineDiffCells)
        {
            AppendRemovedRows(oldLines, oldStart, oldEnd, rows);
            AppendAddedRows(newLines, newStart, newEnd, rows);
            return;
        }

        var table = new int[oldLength + 1, newLength + 1];
        for (var oldIndex = oldLength - 1; oldIndex >= 0; oldIndex--)
        {
            for (var newIndex = newLength - 1; newIndex >= 0; newIndex--)
            {
                table[oldIndex, newIndex] = oldLines[oldStart + oldIndex] == newLines[newStart + newIndex]
                    ? table[oldIndex + 1, newIndex + 1] + 1
                    : Math.Max(table[oldIndex + 1, newIndex], table[oldIndex, newIndex + 1]);
            }
        }

        var edits = new List<LineEdit>();
        var oldCursor = 0;
        var newCursor = 0;
        while (oldCursor < oldLength && newCursor < newLength)
        {
            if (oldLines[oldStart + oldCursor] == newLines[newStart + newCursor])
            {
                edits.Add(new LineEdit(LineEditKind.Context, oldStart + oldCursor, newStart + newCursor));
                oldCursor++;
                newCursor++;
            }
            else if (table[oldCursor + 1, newCursor] >= table[oldCursor, newCursor + 1])
            {
                edits.Add(new LineEdit(LineEditKind.Removed, oldStart + oldCursor, null));
                oldCursor++;
            }
            else
            {
                edits.Add(new LineEdit(LineEditKind.Added, null, newStart + newCursor));
                newCursor++;
            }
        }

        while (oldCursor < oldLength)
        {
            edits.Add(new LineEdit(LineEditKind.Removed, oldStart + oldCursor, null));
            oldCursor++;
        }

        while (newCursor < newLength)
        {
            edits.Add(new LineEdit(LineEditKind.Added, null, newStart + newCursor));
            newCursor++;
        }

        AppendLineEdits(oldLines, newLines, edits, rows, ref nextPairId);
    }

    private static void AppendLineEdits(
        IReadOnlyList<string> oldLines,
        IReadOnlyList<string> newLines,
        IReadOnlyList<LineEdit> edits,
        List<DiffRow> rows,
        ref int nextPairId)
    {
        for (var index = 0; index < edits.Count; index++)
        {
            var edit = edits[index];
            if (edit.Kind == LineEditKind.Context)
            {
                rows.Add(ContextRow(edit.OldIndex!.Value + 1, edit.NewIndex!.Value + 1, oldLines[edit.OldIndex.Value]));
                continue;
            }

            var removedLines = new List<LineRef>();
            var addedLines = new List<LineRef>();
            while (index < edits.Count && edits[index].Kind != LineEditKind.Context)
            {
                var changedEdit = edits[index];
                if (changedEdit.Kind == LineEditKind.Removed)
                {
                    var oldIndex = changedEdit.OldIndex!.Value;
                    removedLines.Add(new LineRef(oldIndex, oldLines[oldIndex]));
                }
                else
                {
                    var newIndex = changedEdit.NewIndex!.Value;
                    addedLines.Add(new LineRef(newIndex, newLines[newIndex]));
                }

                index++;
            }

            index--;
            AppendFuzzyPairedRows(removedLines, addedLines, rows, ref nextPairId);
        }
    }

    private static void AppendFuzzyPairedRows(
        IReadOnlyList<LineRef> removedLines,
        IReadOnlyList<LineRef> addedLines,
        List<DiffRow> rows,
        ref int nextPairId)
    {
        if (removedLines.Count == 0)
        {
            foreach (var addedLine in addedLines)
                rows.Add(AddedRow(addedLine.Index + 1, addedLine.Text));
            return;
        }

        if (addedLines.Count == 0)
        {
            foreach (var removedLine in removedLines)
                rows.Add(RemovedRow(removedLine.Index + 1, removedLine.Text));
            return;
        }

        if ((long)removedLines.Count * addedLines.Count > MaxFuzzyPairCells)
        {
            foreach (var removedLine in removedLines)
                rows.Add(RemovedRow(removedLine.Index + 1, removedLine.Text));
            foreach (var addedLine in addedLines)
                rows.Add(AddedRow(addedLine.Index + 1, addedLine.Text));
            return;
        }

        var similarities = new double[removedLines.Count, addedLines.Count];
        for (var oldIndex = 0; oldIndex < removedLines.Count; oldIndex++)
        {
            for (var newIndex = 0; newIndex < addedLines.Count; newIndex++)
            {
                var similarity = LineSimilarity(removedLines[oldIndex].Text, addedLines[newIndex].Text);
                similarities[oldIndex, newIndex] = similarity >= LinePairThreshold(removedLines[oldIndex].Text, addedLines[newIndex].Text)
                    ? similarity
                    : 0;
            }
        }

        var scores = new double[removedLines.Count + 1, addedLines.Count + 1];
        var choices = new PairChoice[removedLines.Count + 1, addedLines.Count + 1];
        for (var oldIndex = removedLines.Count; oldIndex >= 0; oldIndex--)
        {
            for (var newIndex = addedLines.Count; newIndex >= 0; newIndex--)
            {
                if (oldIndex == removedLines.Count && newIndex == addedLines.Count)
                    continue;

                var bestScore = double.NegativeInfinity;
                var bestChoice = PairChoice.None;

                if (oldIndex < removedLines.Count && newIndex < addedLines.Count && similarities[oldIndex, newIndex] > 0)
                {
                    bestScore = similarities[oldIndex, newIndex] + 0.15 + scores[oldIndex + 1, newIndex + 1];
                    bestChoice = PairChoice.Match;
                }

                if (oldIndex < removedLines.Count && scores[oldIndex + 1, newIndex] > bestScore)
                {
                    bestScore = scores[oldIndex + 1, newIndex];
                    bestChoice = PairChoice.Remove;
                }

                if (newIndex < addedLines.Count && scores[oldIndex, newIndex + 1] > bestScore)
                {
                    bestScore = scores[oldIndex, newIndex + 1];
                    bestChoice = PairChoice.Add;
                }

                scores[oldIndex, newIndex] = bestScore;
                choices[oldIndex, newIndex] = bestChoice;
            }
        }

        var oldCursor = 0;
        var newCursor = 0;
        while (oldCursor < removedLines.Count || newCursor < addedLines.Count)
        {
            var choice = choices[oldCursor, newCursor];
            if (choice == PairChoice.Match)
            {
                var removedLine = removedLines[oldCursor];
                var addedLine = addedLines[newCursor];
                AppendModifiedRows(removedLine.Text, addedLine.Text, removedLine.Index + 1, addedLine.Index + 1, rows, ref nextPairId);
                oldCursor++;
                newCursor++;
            }
            else if (choice == PairChoice.Remove || newCursor >= addedLines.Count)
            {
                var removedLine = removedLines[oldCursor];
                rows.Add(RemovedRow(removedLine.Index + 1, removedLine.Text));
                oldCursor++;
            }
            else
            {
                var addedLine = addedLines[newCursor];
                rows.Add(AddedRow(addedLine.Index + 1, addedLine.Text));
                newCursor++;
            }
        }
    }

    private static double LineSimilarity(string oldLine, string newLine)
    {
        if (string.Equals(oldLine, newLine, StringComparison.Ordinal)) return 1;

        var oldWords = TokenizeComparableWords(oldLine);
        var newWords = TokenizeComparableWords(newLine);
        if (oldWords.Count > 0 && newWords.Count > 0)
        {
            var lcsRatio = LcsLength(oldWords, newWords, StringComparer.OrdinalIgnoreCase) / (double)Math.Max(oldWords.Count, newWords.Count);
            var diceRatio = MultisetDice(oldWords, newWords);
            return Math.Max(lcsRatio, diceRatio);
        }

        var oldKey = NormalizeLineKey(oldLine) ?? string.Empty;
        var newKey = NormalizeLineKey(newLine) ?? string.Empty;
        if (oldKey.Length == 0 || newKey.Length == 0) return 0;
        return BigramDice(oldKey, newKey);
    }

    private static double LinePairThreshold(string oldLine, string newLine)
    {
        var tokenCount = Math.Max(TokenizeComparableWords(oldLine).Count, TokenizeComparableWords(newLine).Count);
        var length = Math.Max(oldLine.Trim().Length, newLine.Trim().Length);
        if (length < 8) return 0.85;
        if (tokenCount <= 2) return 0.72;
        if (tokenCount <= 5) return 0.6;
        return 0.45;
    }

    private static double MultisetDice(IReadOnlyList<string> oldWords, IReadOnlyList<string> newWords)
    {
        var oldCounts = CountWords(oldWords);
        var newCounts = CountWords(newWords);
        var overlap = 0;
        foreach (var (word, oldCount) in oldCounts)
        {
            if (newCounts.TryGetValue(word, out var newCount))
                overlap += Math.Min(oldCount, newCount);
        }

        return (2.0 * overlap) / (oldWords.Count + newWords.Count);
    }

    private static Dictionary<string, int> CountWords(IReadOnlyList<string> words)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in words)
            counts[word] = counts.TryGetValue(word, out var count) ? count + 1 : 1;
        return counts;
    }

    private static double BigramDice(string oldText, string newText)
    {
        if (oldText == newText) return 1;
        if (oldText.Length < 2 || newText.Length < 2) return 0;

        var oldBigrams = CountBigrams(oldText);
        var newBigrams = CountBigrams(newText);
        var overlap = 0;
        foreach (var (bigram, oldCount) in oldBigrams)
        {
            if (newBigrams.TryGetValue(bigram, out var newCount))
                overlap += Math.Min(oldCount, newCount);
        }

        return (2.0 * overlap) / (oldText.Length - 1 + newText.Length - 1);
    }

    private static Dictionary<string, int> CountBigrams(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < text.Length - 1; index++)
        {
            var bigram = text.Substring(index, 2);
            counts[bigram] = counts.TryGetValue(bigram, out var count) ? count + 1 : 1;
        }

        return counts;
    }

    private static IReadOnlyList<string> TokenizeComparableWords(string line)
    {
        var words = new List<string>();
        var index = 0;
        while (index < line.Length)
        {
            if (!IsWordCharacter(line[index]))
            {
                index++;
                continue;
            }

            var start = index;
            while (index < line.Length && IsWordCharacter(line[index]))
                index++;
            words.Add(line[start..index]);
        }

        return words;
    }

    private static void AppendAddedRows(IReadOnlyList<string> newLines, int newStart, int newEnd, List<DiffRow> rows)
    {
        for (var index = newStart; index < newEnd; index++)
            rows.Add(AddedRow(index + 1, newLines[index]));
    }

    private static void AppendRemovedRows(IReadOnlyList<string> oldLines, int oldStart, int oldEnd, List<DiffRow> rows)
    {
        for (var index = oldStart; index < oldEnd; index++)
            rows.Add(RemovedRow(index + 1, oldLines[index]));
    }

    private static void AppendModifiedRows(string oldText, string newText, int oldLineNumber, int newLineNumber, List<DiffRow> rows, ref int nextPairId)
    {
        var pairId = nextPairId++;
        var segments = BuildModifiedSegments(oldText, newText);
        rows.Add(new DiffRow(oldLineNumber, null, "-", oldText, DiffRowKind.Removed, segments.OldSegments, pairId));
        rows.Add(new DiffRow(null, newLineNumber, "+", newText, DiffRowKind.Added, segments.NewSegments, pairId));
    }

    private static ModifiedSegments BuildModifiedSegments(string oldText, string newText)
    {
        var oldTokens = TokenizeDisplayText(oldText);
        var newTokens = TokenizeDisplayText(newText);
        if (oldTokens.Count == 0 || newTokens.Count == 0 || (long)oldTokens.Count * newTokens.Count > MaxTokenDiffCells)
        {
            return new ModifiedSegments(
                SegmentWholeText(oldText, DiffSegmentKind.Removed),
                SegmentWholeText(newText, DiffSegmentKind.Added));
        }

        var table = new int[oldTokens.Count + 1, newTokens.Count + 1];
        for (var oldIndex = oldTokens.Count - 1; oldIndex >= 0; oldIndex--)
        {
            for (var newIndex = newTokens.Count - 1; newIndex >= 0; newIndex--)
            {
                table[oldIndex, newIndex] = TokensEquivalent(oldTokens[oldIndex], newTokens[newIndex])
                    ? table[oldIndex + 1, newIndex + 1] + 1
                    : Math.Max(table[oldIndex + 1, newIndex], table[oldIndex, newIndex + 1]);
            }
        }

        var oldSegments = new List<DiffSegment>();
        var newSegments = new List<DiffSegment>();
        var oldCursor = 0;
        var newCursor = 0;
        while (oldCursor < oldTokens.Count && newCursor < newTokens.Count)
        {
            if (TokensEquivalent(oldTokens[oldCursor], newTokens[newCursor]))
            {
                AddSegment(oldSegments, oldTokens[oldCursor].Text, DiffSegmentKind.Unchanged);
                AddSegment(newSegments, newTokens[newCursor].Text, DiffSegmentKind.Unchanged);
                oldCursor++;
                newCursor++;
            }
            else if (table[oldCursor + 1, newCursor] >= table[oldCursor, newCursor + 1])
            {
                AddSegment(oldSegments, oldTokens[oldCursor].Text, DiffSegmentKind.Removed);
                oldCursor++;
            }
            else
            {
                AddSegment(newSegments, newTokens[newCursor].Text, DiffSegmentKind.Added);
                newCursor++;
            }
        }

        while (oldCursor < oldTokens.Count)
        {
            AddSegment(oldSegments, oldTokens[oldCursor].Text, DiffSegmentKind.Removed);
            oldCursor++;
        }

        while (newCursor < newTokens.Count)
        {
            AddSegment(newSegments, newTokens[newCursor].Text, DiffSegmentKind.Added);
            newCursor++;
        }

        return new ModifiedSegments(oldSegments, newSegments);
    }

    private static IReadOnlyList<DiffSegment> SegmentWholeText(string text, DiffSegmentKind kind) =>
        string.IsNullOrEmpty(text) ? [] : [new DiffSegment(text, kind)];

    private static DiffRow ContextRow(int oldLineNumber, int newLineNumber, string text) =>
        new(oldLineNumber, newLineNumber, " ", text, DiffRowKind.Context, UnchangedSegments(text), PairId: null);

    private static DiffRow AddedRow(int newLineNumber, string text) =>
        new(null, newLineNumber, "+", text, DiffRowKind.Added, UnchangedSegments(text), PairId: null);

    private static DiffRow RemovedRow(int oldLineNumber, string text) =>
        new(oldLineNumber, null, "-", text, DiffRowKind.Removed, UnchangedSegments(text), PairId: null);

    private static IReadOnlyList<DiffSegment> UnchangedSegments(string text) =>
        string.IsNullOrEmpty(text) ? [] : [new DiffSegment(text, DiffSegmentKind.Unchanged)];

    private static IReadOnlyList<DiffToken> TokenizeDisplayText(string text)
    {
        var tokens = new List<DiffToken>();
        var index = 0;
        while (index < text.Length)
        {
            var start = index;
            var kind = TokenKind(text[index]);
            index++;
            while (index < text.Length && TokenKind(text[index]) == kind)
                index++;

            var tokenText = text[start..index];
            var key = kind == DiffTokenKind.Word
                ? tokenText.ToUpperInvariant()
                : tokenText;
            tokens.Add(new DiffToken(tokenText, key, kind));
        }

        return tokens;
    }

    private static DiffTokenKind TokenKind(char character)
    {
        if (char.IsWhiteSpace(character)) return DiffTokenKind.Whitespace;
        return IsWordCharacter(character) ? DiffTokenKind.Word : DiffTokenKind.Punctuation;
    }

    private static bool TokensEquivalent(DiffToken oldToken, DiffToken newToken)
    {
        if (oldToken.Kind != newToken.Kind) return false;
        return oldToken.Kind == DiffTokenKind.Word
            ? string.Equals(oldToken.Key, newToken.Key, StringComparison.Ordinal)
            : string.Equals(oldToken.Text, newToken.Text, StringComparison.Ordinal);
    }

    private static bool IsWordCharacter(char character) =>
        char.IsLetterOrDigit(character) || character == '_' || character == '\'';

    private static int LcsLength(IReadOnlyList<string> oldTokens, IReadOnlyList<string> newTokens, StringComparer comparer)
    {
        if (oldTokens.Count == 0 || newTokens.Count == 0) return 0;
        var table = new int[oldTokens.Count + 1, newTokens.Count + 1];
        for (var oldIndex = oldTokens.Count - 1; oldIndex >= 0; oldIndex--)
        {
            for (var newIndex = newTokens.Count - 1; newIndex >= 0; newIndex--)
            {
                table[oldIndex, newIndex] = comparer.Equals(oldTokens[oldIndex], newTokens[newIndex])
                    ? table[oldIndex + 1, newIndex + 1] + 1
                    : Math.Max(table[oldIndex + 1, newIndex], table[oldIndex, newIndex + 1]);
            }
        }

        return table[0, 0];
    }

    private static void AddSegment(List<DiffSegment> segments, string text, DiffSegmentKind kind)
    {
        if (text.Length == 0) return;
        if (segments.Count > 0 && segments[^1].Kind == kind)
        {
            segments[^1] = segments[^1] with { Text = segments[^1].Text + text };
            return;
        }

        segments.Add(new DiffSegment(text, kind));
    }

    private static IReadOnlyList<DiffHunk> BuildDiffHunks(IReadOnlyList<DiffRow> rows)
    {
        const int contextLines = 3;
        var changedIndexes = rows
            .Select((row, index) => (row, index))
            .Where(item => item.row.Kind != DiffRowKind.Context)
            .Select(item => item.index)
            .ToList();
        if (changedIndexes.Count == 0) return [];

        var ranges = new List<(int Start, int End)>();
        foreach (var changedIndex in changedIndexes)
        {
            var start = Math.Max(0, changedIndex - contextLines);
            var end = Math.Min(rows.Count - 1, changedIndex + contextLines);
            if (ranges.Count == 0 || start > ranges[^1].End + 1)
            {
                ranges.Add((start, end));
            }
            else
            {
                var last = ranges[^1];
                ranges[^1] = (last.Start, Math.Max(last.End, end));
            }
        }

        return ranges
            .Select(range => BuildHunk(rows, range.Start, range.End))
            .ToList();
    }

    private static DiffHunk BuildHunk(IReadOnlyList<DiffRow> rows, int start, int end)
    {
        var hunkRows = rows.Skip(start).Take(end - start + 1).ToList();
        var oldLength = hunkRows.Count(row => row.OldLineNumber.HasValue);
        var newLength = hunkRows.Count(row => row.NewLineNumber.HasValue);
        return new DiffHunk(
            FindHunkStart(rows, start, oldLine: true),
            oldLength,
            FindHunkStart(rows, start, oldLine: false),
            newLength,
            hunkRows);
    }

    private static int FindHunkStart(IReadOnlyList<DiffRow> rows, int start, bool oldLine)
    {
        for (var index = start; index < rows.Count; index++)
        {
            var lineNumber = oldLine ? rows[index].OldLineNumber : rows[index].NewLineNumber;
            if (lineNumber.HasValue) return lineNumber.Value;
        }

        for (var index = start - 1; index >= 0; index--)
        {
            var lineNumber = oldLine ? rows[index].OldLineNumber : rows[index].NewLineNumber;
            if (lineNumber.HasValue) return lineNumber.Value + 1;
        }

        return 1;
    }

    private sealed record Anchor(int OldIndex, int NewIndex);
    private sealed record LineEdit(LineEditKind Kind, int? OldIndex, int? NewIndex);
    private sealed record LineRef(int Index, string Text);
    private sealed record DiffToken(string Text, string Key, DiffTokenKind Kind);
    private sealed record ModifiedSegments(IReadOnlyList<DiffSegment> OldSegments, IReadOnlyList<DiffSegment> NewSegments);
    private sealed record LegacyAuditSnapshot(string Format, string Field, JsonElement Payload);
    private sealed record LegacyChapterBody(Guid Id, string Title, string Body);

    private enum LineEditKind
    {
        Context,
        Added,
        Removed,
    }

    private enum PairChoice
    {
        None,
        Match,
        Add,
        Remove,
    }

    private enum DiffTokenKind
    {
        Word,
        Whitespace,
        Punctuation,
    }
}

public sealed record ReviewDiff(string Title, string? Subtitle, IReadOnlyList<DiffSection> Sections)
{
    public int Additions => Sections.Sum(section => section.Additions);
    public int Deletions => Sections.Sum(section => section.Deletions);
}

public sealed record DiffSection(
    string Key,
    string Label,
    string OldText,
    string NewText,
    IReadOnlyList<DiffHunk> Hunks,
    Guid? OwnerChangeId = null)
{
    public int Additions => Hunks.Sum(hunk => hunk.Additions);
    public int Deletions => Hunks.Sum(hunk => hunk.Deletions);
}

public sealed record DiffHunk(int OldStart, int OldLength, int NewStart, int NewLength, IReadOnlyList<DiffRow> Rows)
{
    public int Additions => Rows.Count(row => row.Kind == DiffRowKind.Added);
    public int Deletions => Rows.Count(row => row.Kind == DiffRowKind.Removed);
}

public sealed record DiffRow(
    int? OldLineNumber,
    int? NewLineNumber,
    string Marker,
    string Text,
    DiffRowKind Kind,
    IReadOnlyList<DiffSegment> Segments,
    int? PairId);

public sealed record DiffSegment(string Text, DiffSegmentKind Kind);

public sealed record DiffFieldInput(string Key, string Label, string OldText, string NewText, Guid? OwnerChangeId = null)
{
    public DiffFieldInput(string label, string oldText, string newText, Guid? ownerChangeId = null)
        : this(label, label, oldText, newText, ownerChangeId)
    {
    }
}

public enum DiffRowKind
{
    Context,
    Added,
    Removed,
}

public enum DiffSegmentKind
{
    Unchanged,
    Added,
    Removed,
}
