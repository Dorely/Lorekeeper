using System.Net;
using Lorekeeper.Startup;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Lorekeeper.ExternalAccess;

public sealed class ExternalAccessHttpGate(RequestDelegate next)
{
    internal const string GrantItem = "Lorekeeper.ExternalAccess.ClientGrantId";

    public async Task InvokeAsync(HttpContext context, McpClientGrantService grants,
        IApplicationStartupState startup, IOptions<ExternalAccessOptions> options)
    {
        if (!IsExternalRequest(context))
        {
            await next(context);
            return;
        }
        if (context.Connection.RemoteIpAddress is not IPAddress remote || !IPAddress.IsLoopback(remote)
            || !IsLoopbackHost(context.Request.Host.Host)
            || context.Request.Host.Port is int port && port != context.Connection.LocalPort
            || context.Request.Headers.Origin.Count > 0 && !ValidOrigin(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (startup.Current.Status != ApplicationStartupStatus.Ready)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "2";
            return;
        }
        if (!await grants.IsEnabledAsync(context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var authorization = context.Request.Headers.Authorization.ToString();
        var grant = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? await grants.AuthenticateAsync(authorization[7..], context.RequestAborted) : null;
        if (grant is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer realm=\"Lorekeeper\"";
            return;
        }
        var maximumBytes = context.Request.Path.StartsWithSegments("/mcp")
            ? options.Value.MaximumRequestBytes : options.Value.MaximumTransferBytes;
        if (context.Request.ContentLength > maximumBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = maximumBytes;
        context.Items[GrantItem] = grant.Id;
        if (grant.LastUsedAt is null || grant.LastUsedAt < DateTime.UtcNow.AddMinutes(-1))
            await grants.MarkUsedAsync(grant.Id, context.RequestAborted);
        context.Response.Headers.CacheControl = "no-store";
        // Deliberately do not register HTTP body/argument logging for this boundary.
        try { await next(context); }
        catch (Exception exception) when (!context.Response.HasStarted && exception is not OperationCanceledException)
        {
            context.Response.Clear();
            context.Response.Headers.CacheControl = "no-store";
            context.Response.StatusCode = exception switch
            {
                BadHttpRequestException request => request.StatusCode,
                ExternalAccessException => StatusCodes.Status403Forbidden,
                ArgumentException or InvalidDataException => StatusCodes.Status400BadRequest,
                KeyNotFoundException => StatusCodes.Status404NotFound,
                _ => StatusCodes.Status503ServiceUnavailable,
            };
            await context.Response.WriteAsJsonAsync(new { code = "EXTERNAL_REQUEST_UNAVAILABLE", message = "Refresh the connection and exact target before retrying. Reuse the original mutation identity for an uncertain write." }, context.RequestAborted);
        }
    }

    internal static bool IsExternalRequest(HttpContext context) => context.Request.Path.StartsWithSegments("/mcp")
        || context.Request.Path.StartsWithSegments("/external-access/transfers");

    internal static Guid ClientId(HttpContext context) => context.Items[GrantItem] is Guid id
        ? id : throw new ExternalAccessException("AUTHENTICATION_REQUIRED", "Configure this client in Lorekeeper External Access.");

    private static bool IsLoopbackHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);

    private static bool ValidOrigin(HttpContext context) => context.Request.Headers.Origin.Count == 1
        && Uri.TryCreate(context.Request.Headers.Origin.ToString(), UriKind.Absolute, out var origin)
        && origin.Scheme == context.Request.Scheme && IsLoopbackHost(origin.Host)
        && origin.Port == context.Connection.LocalPort && origin.AbsolutePath == "/"
        && string.IsNullOrEmpty(origin.Query) && string.IsNullOrEmpty(origin.UserInfo);
}
