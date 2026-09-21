using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lorekeeper.Models;
using Microsoft.Extensions.AI;

namespace Lorekeeper.Llm;

/// <summary>Versioned response data. Endpoint fingerprints never contain raw URLs.</summary>
public sealed class ChatResponseMetadata
{
    public int Version { get; set; } = 1;
    public int? ConnectionId { get; set; }
    public string? ModelId { get; set; }
    public string? EndpointFingerprint { get; set; }
    public Dictionary<string, JsonElement> ReasoningFields { get; set; } = [];
    public bool Incomplete { get; set; }
    public string? FinishReason { get; set; }
    public UsageDetails? Usage { get; set; }

    [JsonIgnore]
    public bool OutputLimitReached => string.Equals(FinishReason, "length", StringComparison.OrdinalIgnoreCase)
        || string.Equals(FinishReason, "max_output_tokens", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string? FailureMessage => OutputLimitReached ? OutputLimitMessage
        : FinishReason is "incomplete" or "content_filter" or "error"
            ? $"The model did not complete its response ({FinishReason}). Partial output is retained; pending tools were not executed."
            : null;

    public const string OutputLimitMessage =
        "The model exhausted its output budget before finishing. Partial output is retained; pending tools were not executed. Increase or clear the output limit, or ask for a smaller response.";

    public static ChatResponseMetadata ForProvider(LlmProvider provider) => new()
    {
        ConnectionId = provider.EffectiveCredentialProviderId,
        ModelId = provider.ModelId,
        EndpointFingerprint = Fingerprint(provider.EndpointUrl),
    };

    public bool Matches(LlmProvider provider) => Version == 1
        && provider.OpenAiAccountId is null
        && ConnectionId == provider.EffectiveCredentialProviderId
        && string.Equals(ModelId, provider.ModelId, StringComparison.Ordinal)
        && string.Equals(EndpointFingerprint, Fingerprint(provider.EndpointUrl), StringComparison.Ordinal);

    private static string Fingerprint(string endpoint) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.Trim().TrimEnd('/'))));

    public string Serialize() => JsonSerializer.Serialize(this);

    public static ChatResponseMetadata? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var metadata = JsonSerializer.Deserialize<ChatResponseMetadata>(json);
            return metadata?.Version == 1 && metadata.ReasoningFields is not null ? metadata : null;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>Protocol payload, separate from displayed reasoning. Never render or quote this content.</summary>
internal sealed class ChatProtocolContent(Func<ChatResponseMetadata> snapshot) : AIContent
{
    public ChatProtocolContent(ChatResponseMetadata metadata) : this(() => metadata) { }
    public ChatResponseMetadata Snapshot() => snapshot();
}

/// <summary>Collects terminal protocol/usage across updates, including failure and cancellation.</summary>
internal sealed class ChatResponseCapture
{
    private ChatProtocolContent? _protocol;
    private string? _finishReason;
    private UsageDetails? _usage;
    private readonly StringBuilder _reasoning = new();

    public string Reasoning => _reasoning.ToString();
    public ChatResponseMetadata Metadata
    {
        get
        {
            var result = _protocol?.Snapshot() ?? new ChatResponseMetadata();
            result.FinishReason = _finishReason ?? result.FinishReason;
            result.Usage = _usage ?? result.Usage;
            return result;
        }
    }

    public void Observe(ChatResponseUpdate update)
    {
        _protocol = update.Contents.OfType<ChatProtocolContent>().LastOrDefault() ?? _protocol;
        _finishReason = update.FinishReason?.ToString() ?? _finishReason;
        foreach (var content in update.Contents)
        {
            if (content is TextReasoningContent reasoning)
                _reasoning.Append(reasoning.Text);
            if (content is UsageContent usage)
                (_usage ??= new UsageDetails()).Add(usage.Details);
        }
    }

    public void ThrowIfIncomplete()
    {
        if (Metadata.FailureMessage is { } failure)
            throw new InvalidOperationException(failure);
    }

    public List<AIContent> Attach(List<AIContent> contents)
    {
        if (_reasoning.Length > 0) contents.Add(new TextReasoningContent(Reasoning));
        contents.Add(new ChatProtocolContent(Metadata));
        return contents;
    }
}
