using ElectronNET.API;

namespace Lorekeeper.Authorization;

public enum ExternalAuthorizationLaunchDisposition
{
    OpenedExternally,
    UserActivationRequired,
}

public sealed record ExternalAuthorizationLaunchResult(
    Uri Target,
    ExternalAuthorizationLaunchDisposition Disposition);

public interface IExternalAuthorizationLauncher
{
    Task<ExternalAuthorizationLaunchResult> LaunchAsync(
        Uri target,
        CancellationToken cancellationToken = default);
}

public static class ExternalAuthorizationTargetValidator
{
    public static Uri Validate(Uri target)
    {
        if (!target.IsAbsoluteUri
            || target.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(target.UserInfo)
            || !string.IsNullOrEmpty(target.Fragment)
            || !target.IsDefaultPort)
        {
            throw new InvalidOperationException("The external authorization target is not allowed.");
        }

        var allowed = (target.Host.Equals("auth.openai.com", StringComparison.OrdinalIgnoreCase)
                && target.AbsolutePath.Equals("/oauth/authorize", StringComparison.Ordinal))
            || (target.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                && target.AbsolutePath.Equals("/login/device", StringComparison.Ordinal));
        if (!allowed)
            throw new InvalidOperationException("The external authorization target is not allowed.");

        return target;
    }
}

public sealed class BrowserExternalAuthorizationLauncher : IExternalAuthorizationLauncher
{
    public Task<ExternalAuthorizationLaunchResult> LaunchAsync(
        Uri target,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ExternalAuthorizationLaunchResult(
            ExternalAuthorizationTargetValidator.Validate(target),
            ExternalAuthorizationLaunchDisposition.UserActivationRequired));
    }
}

public sealed class ElectronExternalAuthorizationLauncher : IExternalAuthorizationLauncher
{
    public async Task<ExternalAuthorizationLaunchResult> LaunchAsync(
        Uri target,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validated = ExternalAuthorizationTargetValidator.Validate(target);
        var error = await Electron.Shell.OpenExternalAsync(validated.AbsoluteUri);
        if (!string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException("The system browser could not be opened for authorization.");

        return new ExternalAuthorizationLaunchResult(
            validated,
            ExternalAuthorizationLaunchDisposition.OpenedExternally);
    }
}
