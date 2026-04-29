using Lorekeeper.Llm;

namespace Lorekeeper.Auth;

public static class CodexOAuthEndpoints
{
    /// <summary>
    /// Maps the Codex OAuth2 PKCE flow endpoints. Returns to <c>/settings/providers</c>
    /// with a status message after callback success or failure.
    /// </summary>
    public static IEndpointRouteBuilder MapCodexOAuth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/auth/start/{providerId:int}", (int providerId, ICodexAuthService authService) =>
        {
            var (url, _) = authService.StartPkceFlow(providerId);
            return Results.Redirect(url);
        });

        endpoints.MapGet("/auth/callback", async (string? code, string? state, ICodexAuthService authService, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
                return Results.Redirect("/settings/providers?message=" + Uri.EscapeDataString("OAuth callback missing code or state."));

            try
            {
                await authService.HandleCallbackAsync(code, state, cancellationToken);
                return Results.Redirect("/settings/providers?message=" + Uri.EscapeDataString("OpenAI account connected."));
            }
            catch (Exception ex)
            {
                return Results.Redirect("/settings/providers?message=" + Uri.EscapeDataString("OAuth failed: " + ex.Message));
            }
        });

        return endpoints;
    }
}
