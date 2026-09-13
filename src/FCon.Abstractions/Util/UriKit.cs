using System.Text;

namespace FCon.Abstractions.Util;

/// <summary>
/// Share-link primitives. Real-world links are sloppy — missing padding, URL-safe
/// alphabets, unencoded fragments — so every helper here is deliberately forgiving.
/// </summary>
public static class UriKit
{
    /// <summary>Decode standard or URL-safe base64, tolerating absent padding. Null on failure.</summary>
    public static string? TryDecodeBase64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim().Replace('-', '+').Replace('_', '/');
        s = new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: return null;
        }
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(s));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string EncodeBase64(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    public static string EncodeBase64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>True when the text looks like a base64 blob rather than plain text.</summary>
    public static bool LooksBase64(string value)
    {
        var s = value.Trim();
        if (s.Length < 8) return false;
        if (s.Contains("://", StringComparison.Ordinal)) return false;
        var payload = s.Where(c => !char.IsWhiteSpace(c)).ToArray();
        return payload.All(c =>
            char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=' or '-' or '_');
    }

    /// <summary>Parse a query string into a case-insensitive map, with values URL-decoded.</summary>
    public static Dictionary<string, string> ParseQuery(string? query)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query)) return map;
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0)
            {
                map[Uri.UnescapeDataString(part)] = "";
                continue;
            }
            var key = Uri.UnescapeDataString(part[..eq]);
            var value = Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            map[key] = value;
        }
        return map;
    }

    /// <summary>Build a query string, skipping empty values.</summary>
    public static string BuildQuery(IEnumerable<KeyValuePair<string, string?>> pairs)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in pairs)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Split a share link into scheme, userinfo, host, port, query and fragment without
    /// going through <see cref="Uri"/>, which rejects several link shapes seen in the wild.
    /// </summary>
    public static bool TrySplit(string link, out LinkParts parts)
    {
        parts = default;
        if (string.IsNullOrWhiteSpace(link)) return false;

        var text = link.Trim();
        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0) return false;

        var scheme = text[..schemeEnd].ToLowerInvariant();
        var rest = text[(schemeEnd + 3)..];

        string fragment = "";
        var hash = rest.IndexOf('#');
        if (hash >= 0)
        {
            fragment = Uri.UnescapeDataString(rest[(hash + 1)..].Replace('+', ' '));
            rest = rest[..hash];
        }

        string query = "";
        var q = rest.IndexOf('?');
        if (q >= 0)
        {
            query = rest[(q + 1)..];
            rest = rest[..q];
        }

        string userInfo = "";
        var at = rest.LastIndexOf('@');
        if (at >= 0)
        {
            userInfo = rest[..at];
            rest = rest[(at + 1)..];
        }

        // Strip any trailing path — no supported protocol carries one in its authority.
        var slash = rest.IndexOf('/');
        if (slash >= 0) rest = rest[..slash];

        if (!TrySplitHostPort(rest, out var host, out var port)) return false;

        parts = new LinkParts(scheme, userInfo, host, port, query, fragment);
        return true;
    }

    /// <summary>Split "host:port", honouring bracketed IPv6 literals.</summary>
    public static bool TrySplitHostPort(string authority, out string host, out int port)
    {
        host = "";
        port = 0;
        if (string.IsNullOrWhiteSpace(authority)) return false;

        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0) return false;
            host = authority[1..close];
            var tail = authority[(close + 1)..];
            return tail.StartsWith(':') && int.TryParse(tail[1..], out port) && port is > 0 and <= 65535;
        }

        var colon = authority.LastIndexOf(':');
        if (colon < 0) return false;
        host = authority[..colon];
        return int.TryParse(authority[(colon + 1)..], out port)
               && port is > 0 and <= 65535
               && host.Length > 0;
    }

    /// <summary>Wrap IPv6 literals in brackets for use in a link authority.</summary>
    public static string FormatHost(string host) =>
        host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
}

public readonly record struct LinkParts(
    string Scheme,
    string UserInfo,
    string Host,
    int Port,
    string Query,
    string Fragment)
{
    public Dictionary<string, string> QueryMap => UriKit.ParseQuery(Query);
}
