using System.Security.Cryptography;

namespace Lorekeeper.Authorization;

public enum OpenAiAuthorizationFlowStatus
{
    Pending,
    ExchangingCode,
    CommittingCredentials,
    Succeeded,
    Denied,
    Canceled,
    Expired,
    Failed,
}

public sealed record OpenAiAuthorizationFlowSnapshot(
    Guid FlowId,
    int AccountId,
    OpenAiAuthorizationFlowStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    Uri AuthorizationTarget,
    string? Message)
{
    public bool IsPending => Status is OpenAiAuthorizationFlowStatus.Pending
        or OpenAiAuthorizationFlowStatus.ExchangingCode
        or OpenAiAuthorizationFlowStatus.CommittingCredentials;
}

public sealed record OpenAiAuthorizationFlowClaim(
    Guid FlowId,
    int AccountId,
    string CodeVerifier,
    OpenAiAuthorizationFlowSnapshot Snapshot);

public sealed class OpenAiAuthorizationFlowRegistry(TimeProvider timeProvider)
{
    public static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();
    private readonly Dictionary<int, FlowState> _byAccount = [];
    private readonly Dictionary<string, FlowState> _byState = new(StringComparer.Ordinal);

    public OpenAiAuthorizationFlowSnapshot Start(
        int accountId,
        Func<string, string, Uri> buildTarget)
    {
        lock (_lock)
        {
            ExpireFlows();
            if (_byAccount.TryGetValue(accountId, out var previous) && previous.Snapshot.IsPending)
            {
                previous.Snapshot = previous.Snapshot with
                {
                    Status = OpenAiAuthorizationFlowStatus.Canceled,
                    Message = "A newer authorization attempt replaced this one.",
                };
                _byState.Remove(previous.State);
            }

            var state = RandomUrlToken();
            var verifier = RandomUrlToken();
            var now = timeProvider.GetUtcNow();
            var target = ExternalAuthorizationTargetValidator.Validate(buildTarget(state, verifier));
            var snapshot = new OpenAiAuthorizationFlowSnapshot(
                Guid.NewGuid(),
                accountId,
                OpenAiAuthorizationFlowStatus.Pending,
                now,
                now.Add(FlowLifetime),
                target,
                null);
            var flow = new FlowState(state, verifier, snapshot);
            _byAccount[accountId] = flow;
            _byState[state] = flow;
            return snapshot;
        }
    }

    public OpenAiAuthorizationFlowClaim? Claim(string state, string? denialMessage = null)
    {
        lock (_lock)
        {
            ExpireFlows();
            if (!_byState.Remove(state, out var flow)
                || !_byAccount.TryGetValue(flow.Snapshot.AccountId, out var active)
                || !ReferenceEquals(active, flow)
                || flow.Snapshot.Status != OpenAiAuthorizationFlowStatus.Pending)
            {
                return null;
            }

            flow.Snapshot = flow.Snapshot with
            {
                Status = denialMessage is null
                    ? OpenAiAuthorizationFlowStatus.ExchangingCode
                    : OpenAiAuthorizationFlowStatus.Denied,
                Message = denialMessage,
            };
            return new OpenAiAuthorizationFlowClaim(
                flow.Snapshot.FlowId,
                flow.Snapshot.AccountId,
                flow.CodeVerifier,
                flow.Snapshot);
        }
    }

    public bool TryBeginCredentialCommit(OpenAiAuthorizationFlowClaim claim)
    {
        lock (_lock)
        {
            ExpireFlows();
            if (!_byAccount.TryGetValue(claim.AccountId, out var active)
                || active.Snapshot.FlowId != claim.FlowId
                || active.Snapshot.Status != OpenAiAuthorizationFlowStatus.ExchangingCode)
            {
                return false;
            }

            active.Snapshot = active.Snapshot with
            {
                Status = OpenAiAuthorizationFlowStatus.CommittingCredentials,
            };
            return true;
        }
    }

    public OpenAiAuthorizationFlowSnapshot? Complete(
        OpenAiAuthorizationFlowClaim claim,
        OpenAiAuthorizationFlowStatus status,
        string? message)
    {
        if (status is OpenAiAuthorizationFlowStatus.Pending
            or OpenAiAuthorizationFlowStatus.ExchangingCode
            or OpenAiAuthorizationFlowStatus.CommittingCredentials)
            throw new ArgumentOutOfRangeException(nameof(status));

        lock (_lock)
        {
            ExpireFlows();
            if (!_byAccount.TryGetValue(claim.AccountId, out var active)
                || active.Snapshot.FlowId != claim.FlowId
                || active.Snapshot.Status is not (OpenAiAuthorizationFlowStatus.ExchangingCode
                    or OpenAiAuthorizationFlowStatus.CommittingCredentials))
            {
                return null;
            }

            active.Snapshot = active.Snapshot with { Status = status, Message = message };
            return active.Snapshot;
        }
    }

    public OpenAiAuthorizationFlowSnapshot? Cancel(
        int accountId,
        string? message = null,
        Guid? expectedFlowId = null)
    {
        lock (_lock)
        {
            ExpireFlows();
            if (!_byAccount.TryGetValue(accountId, out var flow)
                || (expectedFlowId is not null && flow.Snapshot.FlowId != expectedFlowId)
                || !flow.Snapshot.IsPending)
            {
                return flow?.Snapshot;
            }

            _byState.Remove(flow.State);
            flow.Snapshot = flow.Snapshot with
            {
                Status = OpenAiAuthorizationFlowStatus.Canceled,
                Message = message ?? "Authorization was canceled.",
            };
            return flow.Snapshot;
        }
    }

    public OpenAiAuthorizationFlowSnapshot? Get(int accountId)
    {
        lock (_lock)
        {
            ExpireFlows();
            return _byAccount.TryGetValue(accountId, out var flow) ? flow.Snapshot : null;
        }
    }

    private void ExpireFlows()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var flow in _byAccount.Values.Where(flow =>
                     (flow.Snapshot.Status is OpenAiAuthorizationFlowStatus.Pending
                         or OpenAiAuthorizationFlowStatus.ExchangingCode)
                     && flow.Snapshot.ExpiresAt <= now))
        {
            _byState.Remove(flow.State);
            flow.Snapshot = flow.Snapshot with
            {
                Status = OpenAiAuthorizationFlowStatus.Expired,
                Message = "Authorization expired. Start a new connection attempt.",
            };
        }
    }

    private static string RandomUrlToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed class FlowState(
        string state,
        string codeVerifier,
        OpenAiAuthorizationFlowSnapshot snapshot)
    {
        public string State { get; } = state;
        public string CodeVerifier { get; } = codeVerifier;
        public OpenAiAuthorizationFlowSnapshot Snapshot { get; set; } = snapshot;
    }
}
