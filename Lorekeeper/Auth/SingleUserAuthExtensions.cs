using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

namespace Lorekeeper.Auth;

public static class SingleUserAuthExtensions
{
    /// <summary>
    /// Registers cookie authentication and a require-authenticated fallback policy when
    /// the <c>Auth:SingleUser</c> section is configured. Returns whether the login is
    /// active so the composition root can install the matching middleware and endpoints.
    /// A partial configuration fails startup via <see cref="SingleUserAuthOptions.Validate"/>.
    /// </summary>
    public static bool AddSingleUserAuth(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(SingleUserAuthOptions.SectionName).Get<SingleUserAuthOptions>();
        if (options is null || !options.IsConfigured)
        {
            builder.Services.AddSingleton(new SingleUserAuthState(false));
            return false;
        }

        options.Validate();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new SingleUserAuthState(true));
        builder.Services.AddSingleton<SingleUserLoginGuard>();

        builder.Services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(cookie =>
            {
                cookie.Cookie.Name = ".Lorekeeper.Session";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                cookie.LoginPath = "/login";
                cookie.AccessDeniedPath = "/login";
                cookie.ReturnUrlParameter = "returnUrl";
                cookie.ExpireTimeSpan = TimeSpan.FromDays(options.SessionDays);
                cookie.SlidingExpiration = true;
            });

        builder.Services.AddAuthorization(authorization =>
            authorization.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

        return true;
    }
}
