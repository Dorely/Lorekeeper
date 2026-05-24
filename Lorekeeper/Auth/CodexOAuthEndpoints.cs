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

        endpoints.MapGet("/auth/callback", async (
            string? code,
            string? state,
            ICodexAuthService authService,
            IEmbeddingConfigurationService embeddingConfiguration,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
                return RedirectToProviders("OAuth callback missing code or state.", "danger");

            try
            {
                var providerId = await authService.HandleCallbackAsync(code, state, cancellationToken);
                try
                {
                    var configured = await embeddingConfiguration.ConfigureCodexDefaultIfUnsetAsync(providerId, cancellationToken);
                    return configured
                        ? RedirectToProviders($"OpenAI account connected. Codex embeddings configured with {CodexProvider.DefaultEmbeddingModel}.", "success")
                        : RedirectToProviders("OpenAI account connected. Existing embedding model kept.", "info");
                }
                catch (Exception ex)
                {
                    return RedirectToProviders($"OpenAI account connected, but Codex embeddings were not configured: {ex.Message}", "warning");
                }
            }
            catch (Exception ex)
            {
                return RedirectToProviders("OAuth failed: " + ex.Message, "danger");
            }
        });

        return endpoints;
    }

    private static IResult RedirectToProviders(string message, string statusKind) =>
        Results.Redirect(
            "/settings/providers?message=" + Uri.EscapeDataString(message)
            + "&statusKind=" + Uri.EscapeDataString(statusKind));
}
