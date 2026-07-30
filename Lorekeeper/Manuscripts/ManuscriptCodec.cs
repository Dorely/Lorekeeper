using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Lorekeeper.Manuscripts;

public static partial class ManuscriptCodec
{
    private const string MigrationNamespace = "lorekeeper-manuscript-v1";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        WriteIndented = false,
    };
    private static readonly JsonSerializerOptions StrictManuscriptJsonOptions = new(JsonOptions)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static ManuscriptDocument CreateEmpty(Guid manuscriptId, long revision = 0) =>
        new()
        {
            ManuscriptId = manuscriptId,
            Revision = revision,
        };

    public static ManuscriptDocument FromPlainText(
        Guid manuscriptId,
        string? plainText,
        long revision = 1,
        bool deterministicIds = false)
    {
        var normalized = NormalizePlainText(plainText);
        var segments = string.IsNullOrEmpty(normalized)
            ? []
            : BlankLineRegex().Split(normalized).ToList();
        var blocks = new List<ManuscriptBlock>(segments.Count);
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var isSceneBreak = IsSceneBreak(segment);
            var type = isSceneBreak ? ManuscriptBlockType.SceneBreak : ManuscriptBlockType.Paragraph;
            blocks.Add(new ManuscriptBlock
            {
                Id = deterministicIds
                    ? DeterministicBlockId(manuscriptId, index, type, segment)
                    : Guid.NewGuid().ToString("N"),
                Type = type,
                StyleRole = isSceneBreak ? ManuscriptStyleRoles.SceneBreak : ManuscriptStyleRoles.Body,
                Content = isSceneBreak
                    ? []
                    : [new ManuscriptInline { Text = segment }],
            });
        }

        return new ManuscriptDocument
        {
            ManuscriptId = manuscriptId,
            Revision = revision,
            Content = blocks,
        };
    }

    public static ManuscriptDocument ReparsePreservingBlockIds(
        ManuscriptDocument source,
        string? plainText)
    {
        var parsed = FromPlainText(source.ManuscriptId, plainText, checked(source.Revision + 1));
        var matches = LongestCommonSubsequence(
            source.Content.Select(BlockSignature).ToList(),
            parsed.Content.Select(BlockSignature).ToList());
        foreach (var (sourceIndex, parsedIndex) in matches)
            parsed.Content[parsedIndex] = source.Content[sourceIndex];

        PreserveChangedBlockIds(source.Content, parsed.Content, matches);
        Validate(parsed, source.ManuscriptId, parsed.Revision);
        return parsed;
    }

    public static string Serialize(ManuscriptDocument document)
    {
        Validate(document, document.ManuscriptId, document.Revision);
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    public static ManuscriptDocument Deserialize(
        string json,
        Guid expectedManuscriptId,
        long expectedRevision)
    {
        var document = Deserialize(json);
        Validate(document, expectedManuscriptId, expectedRevision);
        return document;
    }

    public static ManuscriptDocument Deserialize(string json)
    {
        ManuscriptDocument document;
        try
        {
            document = JsonSerializer.Deserialize<ManuscriptDocument>(json, StrictManuscriptJsonOptions)
                ?? throw new InvalidDataException("The manuscript document is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The manuscript document is malformed.", exception);
        }

        Validate(document, document.ManuscriptId, document.Revision);
        return document;
    }

    public static void Validate(
        ManuscriptDocument document,
        Guid expectedManuscriptId,
        long expectedRevision)
    {
        if (document.SchemaVersion != ManuscriptDocument.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported manuscript schema version {document.SchemaVersion}.");
        if (document.ManuscriptId != expectedManuscriptId)
            throw new InvalidDataException("The manuscript ID does not match its owning chapter.");
        if (document.Revision < 0)
            throw new InvalidDataException("The manuscript revision cannot be negative.");
        if (document.Revision != expectedRevision)
            throw new InvalidDataException("The manuscript revision does not match its owning chapter.");
        if (document.Content is null)
            throw new InvalidDataException("The manuscript content collection is required.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in document.Content)
        {
            if (block is null)
                throw new InvalidDataException("The manuscript cannot contain a null block.");
            if (string.IsNullOrWhiteSpace(block.Id) || !ids.Add(block.Id))
                throw new InvalidDataException("Manuscript block IDs must be non-empty and unique.");
            if (string.IsNullOrWhiteSpace(block.StyleRole))
                throw new InvalidDataException($"Block {block.Id} has no semantic style role.");
            if (block.Content is null)
                throw new InvalidDataException($"Block {block.Id} has no inline-content collection.");
            if (block.Type == ManuscriptBlockType.SceneBreak && block.Content.Count != 0)
                throw new InvalidDataException($"Scene-break block {block.Id} cannot contain inline text.");
            if (block.Type != ManuscriptBlockType.SceneBreak
                && block.Content.Any(inline => inline.Type != ManuscriptInlineType.Text))
            {
                throw new InvalidDataException($"Block {block.Id} contains an unsupported inline node.");
            }
            foreach (var inline in block.Content)
            {
                if (inline is null || inline.Text is null || inline.Marks is null)
                    throw new InvalidDataException($"Block {block.Id} contains an incomplete inline node.");
                if (ContainsBlockDelimiter(inline.Text))
                    throw new InvalidDataException($"Block {block.Id} contains a paragraph delimiter.");
                if (inline.Marks.Any(mark => mark is null))
                    throw new InvalidDataException($"Block {block.Id} contains a null inline mark.");
                if (inline.Marks.GroupBy(mark => (mark.Type, mark.Value)).Any(group => group.Count() > 1))
                    throw new InvalidDataException($"Block {block.Id} contains duplicate inline marks.");
            }
        }
    }

    public static string ProjectPlainText(ManuscriptDocument document) =>
        string.Join(
            "\n\n",
            document.Content.Select(block => block.Type == ManuscriptBlockType.SceneBreak
                ? "***"
                : string.Concat(block.Content.Select(inline => inline.Text))));

    public static string ProjectPlainText(string json, Guid manuscriptId, long revision) =>
        ProjectPlainText(Deserialize(json, manuscriptId, revision));

    public static string NormalizePlainText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim('\n');
        return BlankRunRegex().Replace(normalized, "\n\n");
    }

    public static string HashPlainText(string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(NormalizePlainText(value));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static string CanonicalizePlainText(string? value) =>
        ProjectPlainText(FromPlainText(Guid.Empty, value, revision: 0, deterministicIds: true));

    internal static bool ContainsBlockDelimiter(string value) =>
        BlankLineRegex().IsMatch(value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));

    public static string Text(ManuscriptBlock block) =>
        block.Type == ManuscriptBlockType.SceneBreak
            ? "***"
            : string.Concat(block.Content.Select(inline => inline.Text));

    public static bool ContentEquals(ManuscriptDocument left, ManuscriptDocument right) =>
        string.Equals(
            JsonSerializer.Serialize(left.Content, JsonOptions),
            JsonSerializer.Serialize(right.Content, JsonOptions),
            StringComparison.Ordinal);

    private static bool IsSceneBreak(string value)
    {
        var compact = string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
        return compact is "***" or "###";
    }

    private static string DeterministicBlockId(
        Guid manuscriptId,
        int index,
        ManuscriptBlockType type,
        string text)
    {
        var source = $"{MigrationNamespace}\n{manuscriptId:N}\n{index}\n{type}\n{text}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..32];
    }

    private static string BlockSignature(ManuscriptBlock block) =>
        $"{block.Type}\0{Text(block)}";

    private static void PreserveChangedBlockIds(
        IReadOnlyList<ManuscriptBlock> source,
        IList<ManuscriptBlock> parsed,
        IReadOnlyList<(int SourceIndex, int ParsedIndex)> matches)
    {
        var anchors = new List<(int SourceIndex, int ParsedIndex)>
        {
            (-1, -1),
        };
        anchors.AddRange(matches);
        anchors.Add((source.Count, parsed.Count));

        for (var anchorIndex = 0; anchorIndex < anchors.Count - 1; anchorIndex++)
        {
            var current = anchors[anchorIndex];
            var next = anchors[anchorIndex + 1];
            var sourceCount = next.SourceIndex - current.SourceIndex - 1;
            var parsedCount = next.ParsedIndex - current.ParsedIndex - 1;
            if (sourceCount != parsedCount)
                continue;

            for (var offset = 1; offset <= sourceCount; offset++)
            {
                var sourceBlock = source[current.SourceIndex + offset];
                var parsedIndex = current.ParsedIndex + offset;
                var parsedBlock = parsed[parsedIndex];
                if (sourceBlock.Type != parsedBlock.Type)
                    continue;

                parsed[parsedIndex] = parsedBlock with
                {
                    Id = sourceBlock.Id,
                    StyleRole = sourceBlock.StyleRole,
                    Content = sourceBlock.Type == ManuscriptBlockType.SceneBreak
                        ? parsedBlock.Content
                        : TransferInlineMarks(sourceBlock, parsedBlock),
                };
            }
        }
    }

    private static List<ManuscriptInline> TransferInlineMarks(
        ManuscriptBlock source,
        ManuscriptBlock parsed)
    {
        var sourceText = Text(source);
        var updatedText = Text(parsed);
        var sourceMarks = ExpandMarks(source, sourceText.Length);
        var updatedMarks = new IReadOnlyList<ManuscriptMark>?[updatedText.Length];
        foreach (var (sourceIndex, updatedIndex) in LongestCommonCharacterSubsequence(sourceText, updatedText))
            updatedMarks[updatedIndex] = sourceMarks[sourceIndex];

        for (var index = 0; index < updatedMarks.Length;)
        {
            if (updatedMarks[index] is not null)
            {
                index++;
                continue;
            }

            var start = index;
            while (index < updatedMarks.Length && updatedMarks[index] is null)
                index++;
            var left = start > 0 ? updatedMarks[start - 1] : null;
            var right = index < updatedMarks.Length ? updatedMarks[index] : null;
            IReadOnlyList<ManuscriptMark>? inherited = null;
            if (left is not null && right is not null && MarksEqual(left, right))
                inherited = left;
            else if (left is not null && right is null)
                inherited = left;
            else if (left is null && right is not null)
                inherited = right;
            if (inherited is null)
                continue;
            for (var fill = start; fill < index; fill++)
                updatedMarks[fill] = inherited;
        }

        var result = new List<ManuscriptInline>();
        for (var index = 0; index < updatedText.Length;)
        {
            var marks = updatedMarks[index] ?? [];
            var end = index + 1;
            while (end < updatedText.Length && MarksEqual(marks, updatedMarks[end] ?? []))
                end++;
            result.Add(new ManuscriptInline
            {
                Text = updatedText[index..end],
                Marks = marks.Select(mark => mark with { }).ToList(),
            });
            index = end;
        }

        return result;
    }

    private static IReadOnlyList<ManuscriptMark>[] ExpandMarks(
        ManuscriptBlock source,
        int length)
    {
        var marks = new IReadOnlyList<ManuscriptMark>[length];
        var cursor = 0;
        foreach (var inline in source.Content)
        {
            var cloned = inline.Marks.Select(mark => mark with { }).ToList();
            for (var offset = 0; offset < inline.Text.Length; offset++)
                marks[cursor++] = cloned;
        }
        return marks;
    }

    private static bool MarksEqual(
        IReadOnlyList<ManuscriptMark> left,
        IReadOnlyList<ManuscriptMark> right) =>
        left.Count == right.Count
        && left.Zip(right).All(pair => pair.First == pair.Second);

    private static IReadOnlyList<(int SourceIndex, int UpdatedIndex)> LongestCommonCharacterSubsequence(
        string source,
        string updated)
    {
        if ((long)source.Length * updated.Length > 4_000_000)
            return CommonCharacterEdges(source, updated);

        var lengths = new int[source.Length + 1, updated.Length + 1];
        for (var sourceIndex = source.Length - 1; sourceIndex >= 0; sourceIndex--)
        {
            for (var updatedIndex = updated.Length - 1; updatedIndex >= 0; updatedIndex--)
            {
                lengths[sourceIndex, updatedIndex] = source[sourceIndex] == updated[updatedIndex]
                    ? lengths[sourceIndex + 1, updatedIndex + 1] + 1
                    : Math.Max(lengths[sourceIndex + 1, updatedIndex], lengths[sourceIndex, updatedIndex + 1]);
            }
        }

        var matches = new List<(int SourceIndex, int UpdatedIndex)>();
        var sourceCursor = 0;
        var updatedCursor = 0;
        while (sourceCursor < source.Length && updatedCursor < updated.Length)
        {
            if (source[sourceCursor] == updated[updatedCursor])
            {
                matches.Add((sourceCursor++, updatedCursor++));
            }
            else if (lengths[sourceCursor + 1, updatedCursor] >= lengths[sourceCursor, updatedCursor + 1])
            {
                sourceCursor++;
            }
            else
            {
                updatedCursor++;
            }
        }
        return matches;
    }

    private static IReadOnlyList<(int SourceIndex, int UpdatedIndex)> CommonCharacterEdges(
        string source,
        string updated)
    {
        var matches = new List<(int SourceIndex, int UpdatedIndex)>();
        var prefix = 0;
        while (prefix < source.Length
            && prefix < updated.Length
            && source[prefix] == updated[prefix])
        {
            matches.Add((prefix, prefix));
            prefix++;
        }

        var sourceSuffix = source.Length - 1;
        var updatedSuffix = updated.Length - 1;
        var suffix = new List<(int SourceIndex, int UpdatedIndex)>();
        while (sourceSuffix >= prefix
            && updatedSuffix >= prefix
            && source[sourceSuffix] == updated[updatedSuffix])
        {
            suffix.Add((sourceSuffix--, updatedSuffix--));
        }
        suffix.Reverse();
        matches.AddRange(suffix);
        return matches;
    }

    private static IReadOnlyList<(int SourceIndex, int ParsedIndex)> LongestCommonSubsequence(
        IReadOnlyList<string> source,
        IReadOnlyList<string> parsed)
    {
        var lengths = new int[source.Count + 1, parsed.Count + 1];
        for (var sourceIndex = source.Count - 1; sourceIndex >= 0; sourceIndex--)
        {
            for (var parsedIndex = parsed.Count - 1; parsedIndex >= 0; parsedIndex--)
            {
                lengths[sourceIndex, parsedIndex] = string.Equals(
                    source[sourceIndex],
                    parsed[parsedIndex],
                    StringComparison.Ordinal)
                    ? lengths[sourceIndex + 1, parsedIndex + 1] + 1
                    : Math.Max(lengths[sourceIndex + 1, parsedIndex], lengths[sourceIndex, parsedIndex + 1]);
            }
        }

        var matches = new List<(int SourceIndex, int ParsedIndex)>();
        var sourceCursor = 0;
        var parsedCursor = 0;
        while (sourceCursor < source.Count && parsedCursor < parsed.Count)
        {
            if (string.Equals(source[sourceCursor], parsed[parsedCursor], StringComparison.Ordinal))
            {
                matches.Add((sourceCursor++, parsedCursor++));
            }
            else if (lengths[sourceCursor + 1, parsedCursor] >= lengths[sourceCursor, parsedCursor + 1])
            {
                sourceCursor++;
            }
            else
            {
                parsedCursor++;
            }
        }

        return matches;
    }

    [GeneratedRegex(@"\n[ \t]*\n(?:[ \t]*\n)*", RegexOptions.CultureInvariant)]
    private static partial Regex BlankLineRegex();

    [GeneratedRegex(@"\n[ \t]*\n(?:[ \t]*\n)*", RegexOptions.CultureInvariant)]
    private static partial Regex BlankRunRegex();
}
