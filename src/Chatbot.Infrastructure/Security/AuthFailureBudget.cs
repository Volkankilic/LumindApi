using Chatbot.Application.Options;

namespace Chatbot.Infrastructure.Security;

/// <summary>
/// Counts failed code attempts across ALL clients in a sliding window. When the threshold is reached it says so ONCE per
/// window (the caller writes a warning and an audit row). It never blocks anything: a global lock-out would hurt legitimate
/// participants and hand an attacker a way to lock everyone out. In memory, so it is per API instance (the API runs as one).
/// </summary>
public sealed class AuthFailureBudget(AuthOptions options)
{
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _failures = new();
    private DateTimeOffset? _lastAlert;

    public bool Enabled => options.FailureBudgetThreshold > 0;

    /// <summary>Records one failure. Returns true when this one crosses the threshold and no alert was raised in the current window.</summary>
    public bool RecordFailure(DateTimeOffset now)
    {
        if (!Enabled)
        {
            return false;
        }

        var window = TimeSpan.FromMinutes(options.FailureBudgetWindowMinutes);
        lock (_gate)
        {
            while (_failures.Count > 0 && now - _failures.Peek() >= window)
            {
                _failures.Dequeue();
            }

            _failures.Enqueue(now);
            if (_failures.Count < options.FailureBudgetThreshold)
            {
                return false;
            }

            if (_lastAlert is { } last && now - last < window)
            {
                return false;
            }

            _lastAlert = now;
            return true;
        }
    }
}
