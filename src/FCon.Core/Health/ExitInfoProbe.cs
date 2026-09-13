using System.Globalization;
using System.Net;

namespace FCon.Core.Health;

/// <summary>Where traffic actually comes out, as reported by a server on the far side.</summary>
public sealed record ExitInfo(string? Ip, string? CountryCode)
{
    /// <summary>Country name for the ISO code, falling back to the raw code.</summary>
    public string? CountryName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CountryCode)) return null;
            try
            {
                return new RegionInfo(CountryCode).EnglishName;
            }
            catch (ArgumentException)
            {
                // Not every code the endpoint returns is a region .NET knows (T1 for Tor, XX).
                return CountryCode;
            }
        }
    }

    /// <summary>
    /// The flag as regional-indicator characters. Segoe UI Emoji renders these as letters
    /// rather than a flag on Windows, so callers should treat it as a bonus, not a label.
    /// </summary>
    public string? Flag
    {
        get
        {
            if (CountryCode is not { Length: 2 } code || !code.All(char.IsAsciiLetter)) return null;
            var upper = code.ToUpperInvariant();
            return string.Concat(upper.Select(c => char.ConvertFromUtf32(0x1F1E6 + (c - 'A'))));
        }
    }

    public string Describe() => (CountryName, Ip) switch
    {
        (not null, not null) => $"{CountryName} · {Ip}",
        (not null, null) => CountryName!,
        (null, not null) => Ip!,
        _ => "unknown exit",
    };
}

/// <summary>
/// Asks the far side of the tunnel where the traffic appears to originate. This is the
/// strongest cheap evidence that a connection is genuinely carrying traffic: the answer
/// can only come back if packets made the round trip, and the address it reports is the
/// exit node rather than the local machine.
/// </summary>
public static class ExitInfoProbe
{
    /// <summary>
    /// Cloudflare's trace endpoint: a few hundred bytes of key=value lines, no API key,
    /// and reachable from most networks.
    /// </summary>
    public const string DefaultUrl = "https://www.cloudflare.com/cdn-cgi/trace";

    public static async Task<ExitInfo?> LookupAsync(
        int httpPort,
        string url = DefaultUrl,
        int timeoutMs = 6000,
        CancellationToken ct = default)
    {
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{httpPort}"),
            UseProxy = true,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };

        try
        {
            var body = await client.GetStringAsync(url, ct).ConfigureAwait(false);
            return Parse(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>Parse the trace format, and fall back to JSON for other endpoints.</summary>
    internal static ExitInfo? Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        if (body.TrimStart().StartsWith('{')) return ParseJson(body);

        string? ip = null, loc = null;
        foreach (var line in body.ReplaceLineEndings("\n").Split('\n'))
        {
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Equals("ip", StringComparison.OrdinalIgnoreCase)) ip = value;
            else if (key.Equals("loc", StringComparison.OrdinalIgnoreCase)) loc = value;
        }

        return ip is null && loc is null ? null : new ExitInfo(ip, loc);
    }

    private static ExitInfo? ParseJson(string body)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            var root = document.RootElement;

            // Accept the field names used by the common geo-IP services.
            var ip = Read(root, "ip", "query", "YourFuckingIPAddress");
            var country = Read(root, "country", "country_code", "countryCode");

            return ip is null && country is null ? null : new ExitInfo(ip, country);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? Read(System.Text.Json.JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        return null;
    }
}
