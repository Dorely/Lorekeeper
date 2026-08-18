using System.Text;

namespace Lorekeeper.Llm;

/// <summary>
/// HTTP handler that rewrites the OpenAI-compatible chat request field
/// <c>max_completion_tokens</c> to the legacy <c>max_tokens</c> name. Several
/// open-weight gateways (<c>api.deepseek.com</c>, <c>api.together.xyz</c>) only
/// accept the legacy field. The handler shares the same underlying connection
/// pool as the envelope-unwrap transport and only mutates JSON request bodies
/// that actually contain the standard field.
/// </summary>
internal sealed class OpenAiLegacyMaxTokensFieldHandler : DelegatingHandler
{
    public OpenAiLegacyMaxTokensFieldHandler()
        : this(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
    {
    }

    public OpenAiLegacyMaxTokensFieldHandler(HttpMessageHandler inner)
        : base(inner)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Content is null
            || request.Content.Headers.ContentType is not { MediaType: "application/json" }
            || !request.Content.Headers.ContentType.MediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var body = await request.Content.ReadAsStringAsync(cancellationToken);
        var rewritten = Rewrite(body);
        if (string.Equals(rewritten, body, StringComparison.Ordinal))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        request.Content = new StringContent(rewritten, Encoding.UTF8, "application/json");
        return await base.SendAsync(request, cancellationToken);
    }

    internal static string Rewrite(string json)
    {
        // Only rewrite the exact, self-contained JSON property token. Reading the
        // body as a string and swapping tokens keeps the payload validation
        // untouched for the many providers that never send the standard field.
        if (!json.Contains("max_completion_tokens", StringComparison.Ordinal))
            return json;

        return json
            .Replace("\"max_completion_tokens\"", "\"max_tokens\"", StringComparison.Ordinal);
    }
}