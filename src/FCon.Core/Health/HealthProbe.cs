using System.Diagnostics;
using System.Net;

namespace FCon.Core.Health;

public enum HealthVerdict
{
    /// <summary>Traffic reached the internet through the tunnel.</summary>
    Healthy,
    /// <summary>The local listener refused the connection — the core is not serving.</summary>
    ListenerDown,
    /// <summary>The listener accepted, but nothing came back in time.</summary>
    TimedOut,
    /// <summary>The tunnel answered, but with something other than the expected response.</summary>
    Rejected,
}

public sealed record HealthResult(HealthVerdict Verdict, int LatencyMs, string? Error)
{
    public bool Ok => Verdict == HealthVerdict.Healthy;

    /// <summary>Short phrase for the status bar.</summary>
    public string Describe() => Verdict switch
    {
        HealthVerdict.Healthy => $"{LatencyMs} ms",
        HealthVerdict.ListenerDown => "proxy not responding",
        HealthVerdict.TimedOut => "timed out",
        _ => Error ?? "unexpected response",
    };
}

/// <summary>
/// The two questions the supervisor asks the network, behind an interface so the
/// state machine can be driven by scripted answers in tests.
/// </summary>
public interface IHealthProbe
{
    Task<HealthResult> CheckAsync(int httpPort, string url, int timeoutMs, CancellationToken ct);

    Task<ExitInfo?> LookupExitAsync(int httpPort, CancellationToken ct);
}

/// <summary>The real thing: an HTTP fetch through the local listener.</summary>
public sealed class DefaultHealthProbe : IHealthProbe
{
    public Task<HealthResult> CheckAsync(int httpPort, string url, int timeoutMs, CancellationToken ct) =>
        HealthProbe.CheckAsync(httpPort, url, timeoutMs, ct);

    public Task<ExitInfo?> LookupExitAsync(int httpPort, CancellationToken ct) =>
        ExitInfoProbe.LookupAsync(httpPort, ct: ct);
}

/// <summary>
/// Proves the tunnel actually carries traffic. Connecting a core and binding a port says
/// nothing about whether packets reach the internet, so every connection is confirmed by
/// fetching a tiny endpoint through the local proxy before it is called healthy.
/// </summary>
public static class HealthProbe
{
    public static async Task<HealthResult> CheckAsync(
        int httpPort,
        string url,
        int timeoutMs = 5000,
        CancellationToken ct = default)
    {
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{httpPort}"),
            UseProxy = true,
            AllowAutoRedirect = false,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };

        // Defeat any cache between here and the exit node, or a "success" may be local.
        client.DefaultRequestHeaders.CacheControl = new() { NoCache = true, NoStore = true };

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            stopwatch.Stop();

            var code = (int)response.StatusCode;
            return code is 204 or >= 200 and < 400
                ? new HealthResult(HealthVerdict.Healthy, (int)stopwatch.ElapsedMilliseconds, null)
                : new HealthResult(HealthVerdict.Rejected, (int)stopwatch.ElapsedMilliseconds, $"HTTP {code}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new HealthResult(HealthVerdict.TimedOut, timeoutMs, "Timed out.");
        }
        catch (HttpRequestException ex)
        {
            // A refused connection to the loopback listener means the core is not serving;
            // anything else failed further out, which is a tunnel problem instead.
            var refused = ex.InnerException is System.Net.Sockets.SocketException
            {
                SocketErrorCode: System.Net.Sockets.SocketError.ConnectionRefused,
            };
            return new HealthResult(
                refused ? HealthVerdict.ListenerDown : HealthVerdict.Rejected,
                (int)stopwatch.ElapsedMilliseconds,
                ex.Message);
        }
    }

    /// <summary>
    /// Rough download throughput through the tunnel, in bytes per second. Stops at either
    /// the byte budget or the time budget so a slow server cannot stall the UI.
    /// </summary>
    public static async Task<double> MeasureThroughputAsync(
        int httpPort,
        string url,
        int maxBytes = 8 * 1024 * 1024,
        int maxMilliseconds = 8000,
        CancellationToken ct = default)
    {
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{httpPort}"),
            UseProxy = true,
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(maxMilliseconds);

        var stopwatch = Stopwatch.StartNew();
        long total = 0;

        try
        {
            using var response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, budget.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];

            while (total < maxBytes)
            {
                var read = await stream.ReadAsync(buffer, budget.Token).ConfigureAwait(false);
                if (read <= 0) break;
                total += read;
            }
        }
        catch (OperationCanceledException)
        {
            // Hitting the time budget is the normal way this ends; measure what arrived.
        }
        catch (HttpRequestException)
        {
            return 0;
        }

        stopwatch.Stop();
        var seconds = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
        return total / seconds;
    }
}
