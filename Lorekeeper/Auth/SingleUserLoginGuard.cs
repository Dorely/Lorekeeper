namespace Lorekeeper.Auth;

/// <summary>
/// Process-wide brute-force and replay protection for the single-user login: failed
/// attempts trigger a temporary lockout, and an accepted one-time code's time step can
/// never be accepted a second time.
/// </summary>
public sealed class SingleUserLoginGuard(SingleUserAuthOptions options)
{
    private readonly object _gate = new();
    private readonly int _maxFailures = options.MaxFailedAttempts;
    private readonly TimeSpan _lockoutDuration = TimeSpan.FromMinutes(options.LockoutMinutes);
    private int _failures;
    private DateTimeOffset? _lockedUntil;
    private long _lastAcceptedTotpStep = -1;

    public bool IsLockedOut()
    {
        lock (_gate)
        {
            if (_lockedUntil is { } lockedUntil && lockedUntil > DateTimeOffset.UtcNow)
                return true;

            _lockedUntil = null;
            return false;
        }
    }

    public void RecordFailure()
    {
        lock (_gate)
        {
            _failures++;
            if (_failures >= _maxFailures)
            {
                _failures = 0;
                _lockedUntil = DateTimeOffset.UtcNow + _lockoutDuration;
            }
        }
    }

    /// <summary>Accepts a validated one-time-code step unless it was already used.</summary>
    public bool TryAcceptTotpStep(long step)
    {
        lock (_gate)
        {
            if (step <= _lastAcceptedTotpStep)
                return false;

            _lastAcceptedTotpStep = step;
            return true;
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _failures = 0;
            _lockedUntil = null;
        }
    }
}
