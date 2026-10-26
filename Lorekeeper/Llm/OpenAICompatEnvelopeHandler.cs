using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;

namespace Lorekeeper.Llm;

/// <summary>
/// HTTP handler for OpenAI-compatible gateways that wrap non-streaming completion payloads
/// in an outer envelope. Cline, for example, answers with
/// <c>{"data": {"choices": [...], ...}, "success": true}</c> while the OpenAI SDK expects
/// <c>choices</c> at the root; without unwrapping, the SDK parses an empty Choices
/// collection and throws when reading index 0. Streaming responses (<c>text/event-stream</c>)
/// from these gateways use the standard root shape and pass through untouched, as do
/// standard OpenAI-shaped JSON bodies.
/// </summary>
internal sealed class OpenAICompatEnvelopeHandler : DelegatingHandler
{
    private static readonly ConcurrentDictionary<string, bool> RequiresInitialSystem = new(StringComparer.Ordinal);
    private static readonly SocketsHttpHandler DefaultInner = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };

    public OpenAICompatEnvelopeHandler()
        : base(DefaultInner)
    {
    }

    public OpenAICompatEnvelopeHandler(HttpMessageHandler inner)
        : base(inner)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var endpoint = request.RequestUri?.AbsoluteUri ?? "";
        var original = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        if (RequiresInitialSystem.ContainsKey(endpoint) && InitialSystemLayout(original) is { } initial)
            request.Content = new StringContent(initial, Encoding.UTF8, "application/json");
        var response = await base.SendAsync(request, cancellationToken);
        if ((int)response.StatusCode is 400 or 422 && original is not null
            && InitialSystemLayout(original) is { } fallback)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            if (RejectsLateSystem(error))
            {
                response.Dispose();
                RequiresInitialSystem[endpoint] = true;
                using var retry = new HttpRequestMessage(request.Method, request.RequestUri);
                foreach (var header in request.Headers) retry.Headers.TryAddWithoutValidation(header.Key, header.Value);
                retry.Content = new StringContent(fallback, Encoding.UTF8, "application/json");
                response = await base.SendAsync(retry, cancellationToken);
            }
        }
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrEmpty(mediaType) || !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            return response;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var payload = TryUnwrapEnvelope(body);
        if (payload is null)
            return response;

        response.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        return response;
    }

    private static bool RejectsLateSystem(string error)
    {
        try
        {
            using var json = JsonDocument.Parse(error);
            if (!json.RootElement.TryGetProperty("error", out var detail) || detail.ValueKind != JsonValueKind.Object
                || !detail.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String) return false;
            var text = message.GetString()!;
            return text.Contains("system", StringComparison.OrdinalIgnoreCase)
                && (text.Contains("first", StringComparison.OrdinalIgnoreCase) || text.Contains("beginning", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("start", StringComparison.OrdinalIgnoreCase) || text.Contains("not support", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException) { return false; }
    }

    private static string? InitialSystemLayout(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var body = JsonNode.Parse(json)?.AsObject();
            if (body?["messages"] is not JsonArray messages) return null;
            var seenConversation = false;
            var late = false;
            var systems = new List<string>();
            var conversation = new JsonArray();
            foreach (var message in messages)
            {
                if (message?["role"]?.GetValue<string>() == "system")
                {
                    late |= seenConversation;
                    if (message["content"] is JsonArray blocks)
                        systems.AddRange(blocks.Where(block => block?["type"]?.GetValue<string>() == "text").Select(block => block!["text"]!.GetValue<string>()));
                    else systems.Add(message["content"]?.GetValue<string>() ?? "");
                }
                else
                {
                    seenConversation = true;
                    conversation.Add(message?.DeepClone());
                }
            }
            if (!late) return null;
            conversation.Insert(0, new JsonObject { ["role"] = "system", ["content"] = string.Join("\n\n", systems) });
            body!["messages"] = conversation;
            return body.ToJsonString();
        }
        catch (JsonException) { return null; }
    }

    internal static string? TryUnwrapEnvelope(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.TryGetProperty("choices", out _)
                || !root.TryGetProperty("data", out var envelope)
                || envelope.ValueKind != JsonValueKind.Object
                || !envelope.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return envelope.GetRawText();
        }
        catch (JsonException)
        {
            // Non-JSON or malformed bodies are left untouched so the SDK surfaces its
            // own deserialization error for the original response.
            return null;
        }
    }
}
