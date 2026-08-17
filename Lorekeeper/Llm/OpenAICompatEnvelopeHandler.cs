using System.Text;
using System.Text.Json;

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
    public OpenAICompatEnvelopeHandler()
        : base(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
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
