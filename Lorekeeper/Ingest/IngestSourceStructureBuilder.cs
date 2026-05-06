using System.Text.RegularExpressions;
using Lorekeeper.Tokens;

namespace Lorekeeper.Ingest;

public sealed partial class IngestSourceStructureBuilder(
    ITokenCounter tokenCounter,
    ITokenBudgetPlanner budgetPlanner) : IIngestSourceStructureBuilder
{
    public IReadOnlyList<IngestSourceChunkDraft> Build(IngestSourceStructureRequest request)
    {
        var sourceText = request.SourceText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourceText)) return [];

        var budget = budgetPlanner.Plan(request.BudgetRequest);
        var targetTokens = Math.Max(1, request.SourceTextTargetTokens ?? budget.SourceTextTargetTokens);
        var blocks = SplitIntoBlocks(sourceText);
        if (blocks.Count == 0) return [];

        var drafts = new List<IngestSourceChunkDraft>();
        var currentStart = -1;
        var currentEnd = -1;
        var currentTitle = string.Empty;
        var currentHeadingPath = string.Empty;

        foreach (var block in blocks)
        {
            if (block.IsHeading && currentStart >= 0 && currentEnd > currentStart)
            {
                AddDraft(drafts, sourceText, currentStart, currentEnd, currentTitle, currentHeadingPath, request.TokenCountRequest);
                currentStart = -1;
                currentEnd = -1;
                currentTitle = string.Empty;
                currentHeadingPath = string.Empty;
            }

            if (currentStart < 0)
            {
                currentStart = block.Start;
                currentTitle = block.IsHeading ? CleanHeading(block.Text) : string.Empty;
                currentHeadingPath = currentTitle;
            }
            else if (string.IsNullOrWhiteSpace(currentTitle) && block.IsHeading)
            {
                currentTitle = CleanHeading(block.Text);
                currentHeadingPath = currentTitle;
            }

            var candidateEnd = block.End;
            var candidateText = sourceText[currentStart..candidateEnd];
            var candidateTokens = tokenCounter.Count(candidateText, request.TokenCountRequest).TokenCount;

            if (candidateTokens > targetTokens && currentEnd > currentStart)
            {
                AddDraft(drafts, sourceText, currentStart, currentEnd, currentTitle, currentHeadingPath, request.TokenCountRequest);
                currentStart = block.Start;
                currentEnd = block.End;
                currentTitle = block.IsHeading ? CleanHeading(block.Text) : string.Empty;
                currentHeadingPath = currentTitle;
            }
            else
            {
                currentEnd = candidateEnd;
            }

            while (currentStart >= 0)
            {
                var currentText = sourceText[currentStart..currentEnd];
                var currentTokens = tokenCounter.Count(currentText, request.TokenCountRequest).TokenCount;
                if (currentTokens <= targetTokens) break;

                var splitEnd = FindSplitEnd(sourceText, currentStart, currentEnd, targetTokens, request.TokenCountRequest);
                AddDraft(drafts, sourceText, currentStart, splitEnd, currentTitle, currentHeadingPath, request.TokenCountRequest);
                currentStart = SkipWhitespace(sourceText, splitEnd, currentEnd);
                currentTitle = string.Empty;
                currentHeadingPath = string.Empty;
                if (currentStart >= currentEnd)
                {
                    currentStart = -1;
                    currentEnd = -1;
                    break;
                }
            }
        }

        if (currentStart >= 0 && currentEnd > currentStart)
            AddDraft(drafts, sourceText, currentStart, currentEnd, currentTitle, currentHeadingPath, request.TokenCountRequest);

        return MergeAdjacentDrafts(drafts, sourceText, targetTokens, request.TokenCountRequest);
    }

    private IReadOnlyList<IngestSourceChunkDraft> MergeAdjacentDrafts(
        IReadOnlyList<IngestSourceChunkDraft> drafts,
        string sourceText,
        int targetTokens,
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
            if (candidateTokens <= targetTokens)
            {
                pending.Add(next);
                continue;
            }

            FlushPending();
            pending.Add(next);
        }

        FlushPending();
        return merged;

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
}