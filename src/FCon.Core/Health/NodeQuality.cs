namespace FCon.Core.Health;

/// <summary>
/// What we have learned about one server from actually using it. A single latency
/// reading is a poor guide — servers that answer a TCP handshake quickly can still fail
/// to carry traffic — so this keeps smoothed latency, variability and a success history,
/// and lets selection prefer servers that have genuinely worked.
/// </summary>
public sealed record NodeQuality
{
    /// <summary>Weight given to the newest sample. High enough to react, low enough to ignore blips.</summary>
    private const double Alpha = 0.3;

    public Guid NodeId { get; init; }

    /// <summary>Exponentially weighted mean handshake latency, in ms. Null until first measured.</summary>
    public double? LatencyMs { get; init; }

    /// <summary>Smoothed absolute deviation of latency — a stand-in for jitter.</summary>
    public double? JitterMs { get; init; }

    /// <summary>Round trips that carried real traffic through the tunnel.</summary>
    public int Successes { get; init; }

    public int Failures { get; init; }

    /// <summary>Failures since the last success. Drives quarantine.</summary>
    public int ConsecutiveFailures { get; init; }

    public DateTimeOffset? LastSuccess { get; init; }
    public DateTimeOffset? LastFailure { get; init; }
    public string? LastError { get; init; }

    /// <summary>True once traffic has actually flowed through this server, not merely connected.</summary>
    public bool Verified => Successes > 0;

    /// <summary>
    /// Servers that keep failing are set aside for a while rather than retried forever.
    /// The window grows with the failure streak, capped so a server is never lost for good.
    /// </summary>
    public DateTimeOffset? QuarantinedUntil { get; init; }

    public bool IsQuarantined(DateTimeOffset now) => QuarantinedUntil > now;

    /// <summary>
    /// Reliability with Laplace smoothing, so one lucky success does not outrank a server
    /// with a long good record, and an untried server is not assumed perfect.
    /// </summary>
    public double Reliability => (Successes + 1.0) / (Successes + Failures + 2.0);

    /// <summary>
    /// Ranking score, higher is better. Reliability dominates: a fast server that drops
    /// traffic is worse than a slightly slower one that does not. Latency and jitter then
    /// separate servers of comparable reliability.
    /// </summary>
    public double Score(DateTimeOffset now)
    {
        if (IsQuarantined(now)) return double.MinValue;

        var score = Reliability * 100.0;

        if (LatencyMs is { } latency)
        {
            // Diminishing penalty: 50ms vs 100ms matters far more than 800ms vs 850ms.
            score -= 20.0 * Math.Log10(1 + Math.Max(0, latency) / 25.0);
        }
        else
        {
            // Never measured: rank below anything with a real reading, above known-bad.
            score -= 12.0;
        }

        if (JitterMs is { } jitter) score -= Math.Min(10.0, jitter / 20.0);

        // Prefer evidence that is still fresh.
        if (LastSuccess is { } success)
        {
            var staleHours = (now - success).TotalHours;
            score -= Math.Min(8.0, staleHours / 6.0);
        }

        return score;
    }

    public NodeQuality WithLatency(int milliseconds)
    {
        if (milliseconds < 0) return this;

        var latency = LatencyMs is null
            ? milliseconds
            : (Alpha * milliseconds) + ((1 - Alpha) * LatencyMs.Value);

        var deviation = LatencyMs is null ? 0 : Math.Abs(milliseconds - LatencyMs.Value);
        var jitter = JitterMs is null
            ? deviation
            : (Alpha * deviation) + ((1 - Alpha) * JitterMs.Value);

        return this with { LatencyMs = latency, JitterMs = jitter };
    }

    public NodeQuality WithSuccess(DateTimeOffset now) => this with
    {
        Successes = Successes + 1,
        ConsecutiveFailures = 0,
        LastSuccess = now,
        LastError = null,
        QuarantinedUntil = null,
    };

    public NodeQuality WithFailure(DateTimeOffset now, string? error)
    {
        var streak = ConsecutiveFailures + 1;

        // 1 min, 2, 4, 8 ... capped at an hour so a recovered server comes back on its own.
        var backoff = TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Min(streak - 1, 6))));

        return this with
        {
            Failures = Failures + 1,
            ConsecutiveFailures = streak,
            LastFailure = now,
            LastError = error,
            // One bad result is not a verdict; quarantine only after a repeated pattern.
            QuarantinedUntil = streak >= 3 ? now + backoff : QuarantinedUntil,
        };
    }
}
