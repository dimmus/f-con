using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace FCon.Core.Net;

/// <summary>What the core reports about one outbound or group.</summary>
public sealed record ProxyStatus(string Name, string Type, string? Now, IReadOnlyList<string> All, int? LastDelayMs);

public sealed record ConnectionsSnapshot(long UploadTotal, long DownloadTotal, int Connections);

/// <summary>
/// The slice of the Clash-compatible control API that FCon drives: read a group's
/// current pick, switch a selector, measure a real URL delay through one outbound,
/// and read the byte counters. Abstracted so the supervisor can be tested without a core.
/// </summary>
public interface IClashApi
{
    Task<ProxyStatus?> GetProxyAsync(string tag, CancellationToken ct = default);

    /// <summary>Point a selector group at one of its members. False when the core refused.</summary>
    Task<bool> SelectAsync(string selector, string tag, CancellationToken ct = default);

    /// <summary>Round trip of a real request through one outbound, or null on failure/timeout.</summary>
    Task<int?> DelayAsync(string tag, string url, int timeoutMs, CancellationToken ct = default);

    Task<ConnectionsSnapshot?> GetConnectionsAsync(CancellationToken ct = default);
}

/// <summary>HTTP client for sing-box's Clash API on loopback. Never routed through the proxy it controls.</summary>
public sealed class ClashApiClient : IClashApi, IDisposable
{
    private readonly HttpClient _http;

    public ClashApiClient(int port, string? secret)
    {
        var handler = new HttpClientHandler { UseProxy = false };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        if (!string.IsNullOrEmpty(secret))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
    }

    public int Port => _http.BaseAddress!.Port;

    public async Task<ProxyStatus?> GetProxyAsync(string tag, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync($"proxies/{Uri.EscapeDataString(tag)}", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = document.RootElement;

            var all = new List<string>();
            if (root.TryGetProperty("all", out var allEl) && allEl.ValueKind == JsonValueKind.Array)
                all.AddRange(allEl.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0));

            int? delay = null;
            if (root.TryGetProperty("history", out var history) && history.ValueKind == JsonValueKind.Array)
            {
                var last = history.EnumerateArray().LastOrDefault();
                if (last.ValueKind == JsonValueKind.Object && last.TryGetProperty("delay", out var d))
                    delay = d.GetInt32();
            }

            return new ProxyStatus(
                root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag,
                root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                root.TryGetProperty("now", out var now) ? now.GetString() : null,
                all,
                delay);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public async Task<bool> SelectAsync(string selector, string tag, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PutAsJsonAsync(
                $"proxies/{Uri.EscapeDataString(selector)}", new { name = tag }, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public async Task<int?> DelayAsync(string tag, string url, int timeoutMs, CancellationToken ct = default)
    {
        var path = $"proxies/{Uri.EscapeDataString(tag)}/delay?url={Uri.EscapeDataString(url)}&timeout={timeoutMs}";

        // The core enforces the timeout; give it a margin so a slow answer is its verdict, not ours.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(timeoutMs + 4000);

        try
        {
            using var response = await _http.GetAsync(path, budget.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.GatewayTimeout) return null;
            if (!response.IsSuccessStatusCode) return null;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false));
            return document.RootElement.TryGetProperty("delay", out var d) ? d.GetInt32() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public async Task<ConnectionsSnapshot?> GetConnectionsAsync(CancellationToken ct = default)
    {
        try
        {
            var body = await _http.GetStringAsync("connections", ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            long up = 0, down = 0;
            var count = 0;
            if (root.TryGetProperty("uploadTotal", out var u)) up = u.GetInt64();
            if (root.TryGetProperty("downloadTotal", out var d)) down = d.GetInt64();
            if (root.TryGetProperty("connections", out var c) && c.ValueKind == JsonValueKind.Array)
                count = c.GetArrayLength();

            return new ConnectionsSnapshot(up, down, count);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
