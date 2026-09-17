namespace Lorekeeper.Authorization;

public sealed record OpenAiAuthorizationStartResult(
    OpenAiAuthorizationFlowSnapshot Flow,
    ExternalAuthorizationLaunchResult Launch);

public sealed record OpenAiAuthorizationCallbackResult(
    OpenAiAuthorizationFlowStatus Status,
    string Message);

public interface IOpenAiAccountAuthorizationService
{
    Task<OpenAiAuthorizationStartResult> StartAsync(
        int accountId,
        CancellationToken cancellationToken = default);

    OpenAiAuthorizationFlowSnapshot? GetFlow(int accountId);

    Task<OpenAiAuthorizationFlowSnapshot?> CancelAsync(
        int accountId,
        CancellationToken cancellationToken = default);

    Task<OpenAiAuthorizationCallbackResult> HandleCallbackAsync(
        string? code,
        string? state,
        string? error,
        string? errorDescription,
        CancellationToken cancellationToken = default);
}
