using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace Lorekeeper.Auth;

public static class SingleUserAuthEndpoints
{
    /// <summary>
    /// Maps the antiforgery-validated single-user login and logout form posts. The
    /// login page at <c>/login</c> is the application-owned Razor surface; these
    /// endpoints only exchange validated credentials for the session cookie.
    /// </summary>
    public static IEndpointRouteBuilder MapSingleUserAuth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/login", async (
            HttpContext context,
            [FromForm] string? username,
            [FromForm] string? password,
            [FromForm] string? code,
            [FromForm] string? returnUrl,
            SingleUserAuthOptions options,
            SingleUserLoginGuard guard) =>
        {
            if (guard.IsLockedOut())
                return Results.Redirect(LoginRoute("locked", returnUrl));

            // Evaluate every factor before deciding so a mismatched username does not
            // return measurably faster than a mismatched password or code.
            var usernameMatches = string.Equals(username, options.Username, StringComparison.Ordinal);
            var passwordMatches = SingleUserPasswordHasher.Verify(password ?? "", options.PasswordHash!);
            var codeMatches = TotpAuthenticator.Validate(
                options.TotpSecret!, code ?? "", DateTimeOffset.UtcNow, out var totpStep);

            if (!usernameMatches || !passwordMatches || !codeMatches || !guard.TryAcceptTotpStep(totpStep))
            {
                guard.RecordFailure();
                return Results.Redirect(LoginRoute("failed", returnUrl));
            }

            guard.RecordSuccess();
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, options.Username!)],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = true });
            return Results.Redirect(SafeLocalUrl(returnUrl));
        }).AllowAnonymous();

        endpoints.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        });

        return endpoints;
    }

    private static string LoginRoute(string status, string? returnUrl)
    {
        var route = "/login?status=" + Uri.EscapeDataString(status);
        var safeReturnUrl = SafeLocalUrl(returnUrl);
        return safeReturnUrl == "/" ? route : route + "&returnUrl=" + Uri.EscapeDataString(safeReturnUrl);
    }

    private static string SafeLocalUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl)
            || returnUrl[0] != '/'
            || returnUrl.StartsWith("//", StringComparison.Ordinal)
            || returnUrl.StartsWith("/\\", StringComparison.Ordinal))
        {
            return "/";
        }

        return returnUrl;
    }
}
