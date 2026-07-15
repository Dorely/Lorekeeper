using System.Text.RegularExpressions;
using Lorekeeper.Tokens;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Ingest;

public sealed partial class IngestSourceStructureBuilder(
    ITokenCounter tokenCounter,
    IOptions<IngestSourceStructureOptions> options) : IIngestSourceStructureBuilder
{
    public IReadOnlyList<IngestSourceChunkDraft> Build(IngestSourceStructureRequest request)
    {
        var sourceText = request.SourceText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourceText)) return [];

        var targetTokens = Math.Max(1, request.SourceTextTargetTokens ?? options.Value.TargetTokens);
        var softMaxTokens = ResolveSoftMaxTokens(targetTokens);
        var smallSectionTokens = ResolveSmallSectionTokens(targetTokens);
        var blocks = SplitIntoBlocks(sourceText);
        if (blocks.Count == 0) return [];

        var drafts = new List<IngestSourceChunkDraft>();
        foreach (var section in BuildNaturalSections(blocks))
            AddSectionDrafts(drafts, sourceText, section, targetTokens, softMaxTokens, request.TokenCountRequest);

        return MergeAdjacentDrafts(drafts, sourceText, targetTokens, softMaxTokens, smallSectionTokens, request.TokenCountRequest);
    }

    private int ResolveSoftMaxTokens(int targetTokens)
    {
        var ratio = double.IsFinite(options.Value.SoftMaxRatio)
            ? Math.Max(1.0, options.Value.SoftMaxRatio)
            : 1.5;
        var estimated = Math.Ceiling(targetTokens * ratio);
        var softMaxTokens = estimated >= int.MaxValue ? int.MaxValue : (int)estimated;
        return Math.Max(targetTokens, softMaxTokens);
    }

    private int ResolveSmallSectionTokens(int targetTokens)
    {
        var ratio = double.IsFinite(options.Value.SmallSectionRatio)
            ? Math.Clamp(options.Value.SmallSectionRatio, 0.0, 1.0)
            : 0.15;
        var estimated = Math.Ceiling(targetTokens * ratio);
        var smallSectionTokens = estimated >= int.MaxValue ? int.MaxValue : (int)estimated;
        return Math.Clamp(smallSectionTokens, 1, targetTokens);
    }

    private static IReadOnlyList<SourceSection> BuildNaturalSections(IReadOnlyList<TextBlock> blocks)
    {
        var sections = new List<SourceSection>();
        var currentStart = -1;
        var currentEnd = -1;
        var currentTitle = string.Empty;
        var currentHeadingPath = string.Empty;

        foreach (var block in blocks)
        {
            if (block.IsHeading)
            {
                FlushSection();
                currentStart = block.Start;
                currentEnd = block.End;
                currentTitle = CleanHeading(block.Text);
                currentHeadingPath = currentTitle;
                continue;
            }

            if (currentStart < 0)
            {
                currentStart = block.Start;
                currentTitle = string.Empty;
                currentHeadingPath = string.Empty;
            }

            currentEnd = block.End;
        }

        FlushSection();
        return sections;

        void FlushSection()
        {
            if (currentStart >= 0 && currentEnd > currentStart)
                sections.Add(new SourceSection(currentStart, currentEnd, currentTitle, currentHeadingPath));

            currentStart = -1;
            currentEnd = -1;
            currentTitle = string.Empty;
            currentHeadingPath = string.Empty;
        }
    }

    private void AddSectionDrafts(
        List<IngestSourceChunkDraft> drafts,
        string sourceText,
        SourceSection section,
        int targetTokens,
        int softMaxTokens,
        TokenCountRequest tokenCountRequest)
    {
        var sectionTokens = tokenCounter.Count(sourceText[section.StartChar..section.EndChar], tokenCountRequest).TokenCount;
        if (sectionTokens <= softMaxTokens)
        {
            AddDraft(drafts, sourceText, section.StartChar, section.EndChar, section.Title, section.HeadingPath, tokenCountRequest);
            return;
        }

        var currentStart = section.StartChar;
        var firstDraft = true;

        while (currentStart < section.EndChar)
        {
            var remainingTokens = tokenCounter.Count(sourceText[currentStart..section.EndChar], tokenCountRequest).TokenCount;
            var title = firstDraft ? section.Title : string.Empty;
            var headingPath = firstDraft ? section.HeadingPath : string.Empty;

            if (remainingTokens <= softMaxTokens)
            {
                AddDraft(drafts, sourceText, currentStart, section.EndChar, title, headingPath, tokenCountRequest);
                break;
            }

            var splitEnd = FindSplitEnd(sourceText, currentStart, section.EndChar, targetTokens, tokenCountRequest);
            AddDraft(drafts, sourceText, currentStart, splitEnd, title, headingPath, tokenCountRequest);
            currentStart = SkipWhitespace(sourceText, splitEnd, section.EndChar);
            firstDraft = false;
        }
    }

    private IReadOnlyList<IngestSourceChunkDraft> MergeAdjacentDrafts(
        IReadOnlyList<IngestSourceChunkDraft> drafts,
        string sourceText,
        int targetTokens,
        int softMaxTokens,
        int smallSectionTokens,
        TokenCountRequest tokenCountRequest)
    {
        if (drafts.Count <= 1) return drafts;

        var merged = new List<IngestSourceChunkDraft>();
        var pending = new List<IngestSourceChunkDraft> { drafts[0] };

        foreach (var next in drafts.Skip(1))
        {
            var candidateStart = pending[0].StartChar;
            var candidateEnd = next.EndChar;
            var candidateTokens = tokenCounter.Count(sourceText[candidateStart..candidateEnd], tokenCountRequest).TokenCount;
            if (ShouldMergePending(next, candidateTokens))
            {
                pending.Add(next);
                continue;
            }

            FlushPending();
            pending.Add(next);
        }

        FlushPending();
        return merged;

        bool ShouldMergePending(IngestSourceChunkDraft next, int candidateTokens)
        {
            if (candidateTokens <= targetTokens) return true;
            if (candidateTokens > softMaxTokens) return false;

            var pendingTokens = tokenCounter.Count(sourceText[pending[0].StartChar..pending[^1].EndChar], tokenCountRequest).TokenCount;
            return pendingTokens <= smallSectionTokens || next.TokenCount.TokenCount <= smallSectionTokens;
        }

        void FlushPending()
        {
            if (pending.Count == 0) return;

            var first = pending[0];
            var last = pending[^1];
            var start = first.StartChar;
            var end = last.EndChar;
            var title = pending.Count == 1 ? first.Title : $"{first.Title} + {pending.Count - 1} sections";
            var headingPath = pending.Count == 1 ? first.HeadingPath : $"{first.HeadingPath} ... {last.HeadingPath}";

            merged.Add(new IngestSourceChunkDraft(
                merged.Count,
                title,
                headingPath,
                start,
                end,
                tokenCounter.Count(sourceText[start..end], tokenCountRequest)));
            pending.Clear();
        }
    }

    private void AddDraft(
        List<IngestSourceChunkDraft> drafts,
        string sourceText,
        int start,
        int end,
        string title,
        string headingPath,
        TokenCountRequest tokenCountRequest)
    {
        start = Math.Clamp(start, 0, sourceText.Length);
        end = Math.Clamp(end, start, sourceText.Length);
        if (end <= start) return;

        var content = sourceText[start..end].Trim();
        if (content.Length == 0) return;

        var resolvedTitle = string.IsNullOrWhiteSpace(title)
            ? $"Part {drafts.Count + 1}"
            : title.Trim();

        drafts.Add(new IngestSourceChunkDraft(
            drafts.Count,
            resolvedTitle,
            string.IsNullOrWhiteSpace(headingPath) ? resolvedTitle : headingPath.Trim(),
            start,
            end,
            tokenCounter.Count(sourceText[start..end], tokenCountRequest)));
    }

    private int FindSplitEnd(string sourceText, int start, int end, int targetTokens, TokenCountRequest tokenCountRequest)
    {
        var low = start + 1;
        var high = end;
        var best = low;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var count = tokenCounter.Count(sourceText[start..middle], tokenCountRequest).TokenCount;
            if (count <= targetTokens)
            {
                best = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        var preferred = FindPreferredBoundary(sourceText, start, best);
        return preferred > start ? preferred : best;
    }

    private static int FindPreferredBoundary(string sourceText, int start, int best)
    {
        var lowerBound = start + Math.Max(1, (best - start) / 2);
        for (var index = best - 1; index > lowerBound; index--)
        {
            var current = sourceText[index];
            if (current is '\n' or '\r') return index + 1;
            if (current is '.' or '!' or '?' or ';') return index + 1;
            if (char.IsWhiteSpace(current)) return index + 1;
        }

        return best;
    }

    private static int SkipWhitespace(string sourceText, int start, int end)
    {
        var cursor = start;
        while (cursor < end && char.IsWhiteSpace(sourceText[cursor])) cursor++;
        return cursor;
    }

    private static IReadOnlyList<TextBlock> SplitIntoBlocks(string sourceText)
    {
        var blocks = new List<TextBlock>();
        var blockStart = -1;
        var blockEnd = -1;

        foreach (var line in EnumerateLines(sourceText))
        {
            var lineText = sourceText[line.Start..line.End].Trim();
            if (lineText.Length == 0)
            {
                FlushBlock();
                continue;
            }

            var isHeading = IsHeading(lineText);
            if (isHeading)
            {
                FlushBlock();
                blocks.Add(new TextBlock(line.Start, line.NextStart, sourceText[line.Start..line.NextStart], IsHeading: true));
                continue;
            }

            if (blockStart < 0) blockStart = line.Start;
            blockEnd = line.NextStart;
        }

        FlushBlock();
        return blocks;

        void FlushBlock()
        {
            if (blockStart < 0 || blockEnd <= blockStart) return;
            blocks.Add(new TextBlock(blockStart, blockEnd, sourceText[blockStart..blockEnd], IsHeading: false));
            blockStart = -1;
            blockEnd = -1;
        }
    }

    private static IEnumerable<TextLine> EnumerateLines(string sourceText)
    {
        var cursor = 0;
        while (cursor < sourceText.Length)
        {
            var start = cursor;
            while (cursor < sourceText.Length && sourceText[cursor] is not '\r' and not '\n') cursor++;
            var end = cursor;
            if (cursor < sourceText.Length && sourceText[cursor] == '\r') cursor++;
            if (cursor < sourceText.Length && sourceText[cursor] == '\n') cursor++;
            yield return new TextLine(start, end, cursor);
        }
    }

    private static bool IsHeading(string lineText) =>
        lineText.StartsWith('#')
        || ChapterHeadingRegex().IsMatch(lineText)
        || NumberedHeadingRegex().IsMatch(lineText)
        || SceneBreakRegex().IsMatch(lineText);

    private static string CleanHeading(string text)
    {
        var line = text.Trim().Trim('#', '*', '-', '_').Trim();
        return line.Length == 0 ? "Section" : line;
    }

    [GeneratedRegex(@"^(chapter|part|book|section|appendix|prologue|epilogue)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChapterHeadingRegex();

    [GeneratedRegex(@"^\d+(\.\d+)*[\).:-]?\s+\S", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedHeadingRegex();

    [GeneratedRegex(@"^([-*_]\s*){3,}$", RegexOptions.CultureInvariant)]
    private static partial Regex SceneBreakRegex();

    private sealed record TextLine(int Start, int End, int NextStart);
    private sealed record TextBlock(int Start, int End, string Text, bool IsHeading);
    private sealed record SourceSection(int StartChar, int EndChar, string Title, string HeadingPath);
}
