using Lorekeeper.Authorization;

namespace Lorekeeper.Auth;

public static class CodexOAuthEndpoints
{
    /// <summary>
    /// Maps the OpenAI OAuth callback onto Lorekeeper's existing web host.
    /// </summary>
    public static IEndpointRouteBuilder MapCodexOAuth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/auth/callback", async (
            HttpContext context,
            IOpenAiAccountAuthorizationService authorization,
            CancellationToken cancellationToken) =>
        {
            var result = await authorization.HandleCallbackAsync(
                context.Request.Query["code"].FirstOrDefault(),
                context.Request.Query["state"].FirstOrDefault(),
                context.Request.Query["error"].FirstOrDefault(),
                context.Request.Query["error_description"].FirstOrDefault(),
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
