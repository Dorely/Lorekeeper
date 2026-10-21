using Lorekeeper.Authorization;

namespace Lorekeeper.Auth;

public static class OpenAiOAuthEndpoints
{
    /// <summary>
    /// Maps the OpenAI OAuth callback onto Lorekeeper's existing web host. Codex and
    /// ChatGPT plan sign-in share this path; the attempt's state selects the protocol.
    /// </summary>
    public static IEndpointRouteBuilder MapOpenAiOAuth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/auth/callback", async (
            HttpContext context,
            IOpenAiAccountAuthorizationService authorization,
            CancellationToken cancellationToken) =>
        {
            var query = context.Request.Query;
            var result = await authorization.HandleCallbackAsync(
                new OpenAiAuthorizationCallback(
                    query["code"].FirstOrDefault(),
                    query["state"].FirstOrDefault(),
                    query["error"].FirstOrDefault(),
                    query["error_description"].FirstOrDefault(),
                    query["client_id"].FirstOrDefault(),
                    query["scope"].FirstOrDefault()),
                cancellationToken);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
            return Results.Content(
                OpenAiAuthorizationCompletionPage.Render(result),
                "text/html; charset=utf-8");
        });

        return endpoints;
    }
}
