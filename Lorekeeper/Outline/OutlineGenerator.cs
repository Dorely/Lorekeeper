using System.Text;
using System.Text.Json;
using Lorekeeper.Chapters;
using Lorekeeper.Llm;
using Lorekeeper.Persistence.Repositories;
using Microsoft.Extensions.AI;

namespace Lorekeeper.Outline;

public class OutlineGenerator(
    IProjectRepository projects,
    ILlmProviderService providerService,
    IChatClientFactory chatClientFactory,
    IActService acts,
    IChapterService chapters,
    ILogger<OutlineGenerator> logger) : IOutlineGenerator
{
    private const string SystemPrompt = """
        You are a story-structure assistant. Given a story brief, produce a high-level outline
        organised into Acts, where each Act contains a small ordered list of Chapters.

        Rules:
        - Output STRICT JSON ONLY. No prose, no markdown, no code fences.
        - Match the schema exactly. Every field is required.
        - Keep titles short (≤ 8 words). Synopses are 1–2 sentences each.
        - Number of acts and chapters should match the requested scope:
            • short  → 2–3 acts, 2–4 chapters per act
            • medium → 3 acts, 4–6 chapters per act
            • long   → 4–5 acts, 5–8 chapters per act
          Default to "medium" when scope is unspecified.

        Schema:
        {
          "acts": [
            {
              "title":   "string",
              "synopsis":"string",
              "chapters":[
                { "title":"string", "synopsis":"string" }
              ]
            }
          ]
        }
        """;

    public async Task<GeneratedOutline> GenerateAsync(Guid projectId, OutlineBrief brief, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(brief.Premise))
            throw new ArgumentException("Premise is required.", nameof(brief));

        _ = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");

        var defaultProvider = await providerService.GetDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("No default LLM provider configured.");
        var chat = await chatClientFactory.CreateChatClientAsync(defaultProvider.Id, cancellationToken);

        var userPrompt = BuildUserPrompt(brief);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, userPrompt),
        };

        var response = await chat.GetResponseAsync(messages, options: null, cancellationToken);
        var text = response.Text ?? string.Empty;

        if (TryParseOutline(text, out var outline, out var parseError))
            return outline;

        // One repair attempt: feed the bad output back and ask for a strict-JSON re-emit.
        logger.LogWarning("Outline JSON parse failed (will retry once): {Error}. Raw: {Raw}", parseError, Truncate(text, 400));
        messages.Add(new ChatMessage(ChatRole.Assistant, text));
        messages.Add(new ChatMessage(ChatRole.User,
            $"Your previous response was not valid JSON ({parseError}). Re-emit the same outline, JSON only, no prose, matching the schema exactly."));

        var retry = await chat.GetResponseAsync(messages, options: null, cancellationToken);
        var retryText = retry.Text ?? string.Empty;
        if (TryParseOutline(retryText, out outline, out var retryError))
            return outline;

        throw new InvalidOperationException($"LLM did not return valid outline JSON after retry: {retryError}. Raw: {Truncate(retryText, 400)}");
    }

    public async Task<IReadOnlyList<Guid>> ApplyAsync(Guid projectId, GeneratedOutline outline, CancellationToken cancellationToken = default)
    {
        var createdActIds = new List<Guid>(outline.Acts.Count);
        foreach (var actSpec in outline.Acts)
        {
            var act = await acts.CreateAsync(projectId, title: actSpec.Title, synopsis: actSpec.Synopsis, cancellationToken: cancellationToken);
            createdActIds.Add(act.Id);
            foreach (var chSpec in actSpec.Chapters)
            {
                await chapters.CreateAsync(projectId, actId: act.Id, title: chSpec.Title, synopsis: chSpec.Synopsis, cancellationToken: cancellationToken);
            }
        }
        return createdActIds;
    }

    private static string BuildUserPrompt(OutlineBrief brief)
    {
        var sb = new StringBuilder();
        sb.Append("Premise: ").AppendLine(brief.Premise.Trim());
        if (!string.IsNullOrWhiteSpace(brief.Tone)) sb.Append("Tone: ").AppendLine(brief.Tone.Trim());
        if (!string.IsNullOrWhiteSpace(brief.Scope)) sb.Append("Scope: ").AppendLine(brief.Scope.Trim());
        if (brief.MainCharacters is { Count: > 0 } chars)
            sb.Append("Main characters: ").AppendLine(string.Join(", ", chars.Where(c => !string.IsNullOrWhiteSpace(c))));
        if (!string.IsNullOrWhiteSpace(brief.CoreConflict)) sb.Append("Core conflict: ").AppendLine(brief.CoreConflict.Trim());
        if (!string.IsNullOrWhiteSpace(brief.Setting)) sb.Append("Setting: ").AppendLine(brief.Setting.Trim());
        sb.AppendLine();
        sb.Append("Produce the outline as JSON matching the schema. JSON only.");
        return sb.ToString();
    }

    private static bool TryParseOutline(string raw, out GeneratedOutline outline, out string? error)
    {
        outline = new GeneratedOutline([]);
        var json = StripFences(raw);
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "empty response";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("acts", out var actsEl) || actsEl.ValueKind != JsonValueKind.Array)
            {
                error = "missing 'acts' array";
                return false;
            }

            var actList = new List<GeneratedAct>(actsEl.GetArrayLength());
            foreach (var actEl in actsEl.EnumerateArray())
            {
                var title = actEl.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                var synopsis = actEl.TryGetProperty("synopsis", out var s) ? s.GetString() ?? string.Empty : string.Empty;
                var chapterList = new List<GeneratedChapter>();
                if (actEl.TryGetProperty("chapters", out var chsEl) && chsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var chEl in chsEl.EnumerateArray())
                    {
                        var ct = chEl.TryGetProperty("title", out var ctEl) ? ctEl.GetString() ?? string.Empty : string.Empty;
                        var cs = chEl.TryGetProperty("synopsis", out var csEl) ? csEl.GetString() ?? string.Empty : string.Empty;
                        if (!string.IsNullOrWhiteSpace(ct))
                            chapterList.Add(new GeneratedChapter(ct.Trim(), cs.Trim()));
                    }
                }

                if (string.IsNullOrWhiteSpace(title)) continue;
                actList.Add(new GeneratedAct(title.Trim(), synopsis.Trim(), chapterList));
            }

            if (actList.Count == 0)
            {
                error = "no usable acts";
                return false;
            }

            outline = new GeneratedOutline(actList);
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Strips markdown code fences if the model added them despite instructions.</summary>
    private static string StripFences(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith("```"))
        {
            var firstNewline = s.IndexOf('\n');
            if (firstNewline >= 0) s = s[(firstNewline + 1)..];
            if (s.EndsWith("```")) s = s[..^3];
        }
        return s.Trim();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
