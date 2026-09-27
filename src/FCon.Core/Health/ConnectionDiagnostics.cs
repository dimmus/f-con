using System.Net;
using System.Net.Sockets;
using System.Text;
using FCon.Abstractions.Model;
using FCon.Abstractions.Plugins;
using FCon.Core.Config;
using FCon.Core.Engine;
using FCon.Core.Localization;
using FCon.Core.Net;
using FCon.Core.Plugins;

namespace FCon.Core.Health;

public enum DiagnosticVerdict { Ok, Info, Warn, Fail }

public sealed record DiagnosticItem(string Name, DiagnosticVerdict Verdict, string Detail, string? Hint = null);

public sealed record DiagnosticReport(
    IReadOnlyList<DiagnosticItem> Items,
    /// <summary>Windows itself refused an outbound connection, or the firewall is configured to.</summary>
    bool FirewallBlockSuspected,
    /// <summary>Programs a firewall rule would have to name: the app and the core.</summary>
    IReadOnlyList<(string Name, string Program)> Programs)
{
    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var item in Items)
        {
            var mark = item.Verdict switch
            {
                DiagnosticVerdict.Ok => "[OK]  ",
                DiagnosticVerdict.Warn => "[!]   ",
                DiagnosticVerdict.Fail => "[FAIL]",
                _ => "[i]   ",
            };
            sb.Append(mark).Append(' ').Append(item.Name).Append(": ").AppendLine(item.Detail);
            if (item.Hint is not null) sb.Append("        ").AppendLine(item.Hint);
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Answers "why can't it connect?" one layer at a time: is the core there, does this
/// machine reach the internet at all, does the server name resolve, can Windows open
/// a socket to the server, is the core listening, does traffic go through, and what
/// does the firewall say. Each answer carries a hint in plain words.
/// </summary>
public static class ConnectionDiagnostics
{
    public static async Task<DiagnosticReport> RunAsync(
        ProxyNode? node,
        AppSettings settings,
        IEngineController engine,
        PluginRegistry registry,
        CancellationToken ct = default)
    {
        var items = new List<DiagnosticItem>();
        var firewall = false;
        var programs = new List<(string, string)>();

        if (Environment.ProcessPath is { } self) programs.Add(("KVN", self));

        // 1. The core.
        var core = EngineLocator.Describe(settings.Engine);
        if (core is null)
        {
            items.Add(new(L.T("Diag_Core"), DiagnosticVerdict.Fail,
                L.F("Diag_CoreMissing", EngineLocator.ExecutableName(settings.Engine)), L.T("Diag_CoreMissingHint")));
        }
        else
        {
            programs.Add((core.FileName, core.ExecutablePath));
            items.Add(new(L.T("Diag_Core"), DiagnosticVerdict.Ok, $"{core.Version} - {core.ExecutablePath}"));
        }

        // 2. Direct internet, without any proxy.
        var direct = await DirectFetchAsync(settings.LatencyTestUrl, ct).ConfigureAwait(false);
        if (direct is null)
        {
            items.Add(new(L.T("Diag_Internet"), DiagnosticVerdict.Ok, L.T("Diag_InternetOk")));
        }
        else
        {
            var (error, socket) = direct.Value;
            if (socket == SocketError.AccessDenied) firewall = true;
            items.Add(new(L.T("Diag_Internet"), DiagnosticVerdict.Warn, error,
                socket == SocketError.AccessDenied ? L.T("Diag_AccessDeniedHint") : L.T("Diag_InternetHint")));
        }

        if (node is null)
        {
            items.Add(new(L.T("Diag_Server"), DiagnosticVerdict.Info, L.T("Diag_NoServer")));
        }
        else
        {
            // 3. Name resolution through the system resolver.
            IPAddress[] addresses = [];
            if (IPAddress.TryParse(node.Server, out var literal))
            {
                addresses = [literal];
                items.Add(new(L.T("Diag_Dns"), DiagnosticVerdict.Ok, L.F("Diag_DnsLiteral", node.Server)));
            }
            else
            {
                try
                {
                    addresses = await Dns.GetHostAddressesAsync(node.Server, ct).ConfigureAwait(false);
                    items.Add(new(L.T("Diag_Dns"), addresses.Length > 0 ? DiagnosticVerdict.Ok : DiagnosticVerdict.Fail,
                        addresses.Length > 0
                            ? L.F("Diag_DnsResolved", node.Server, string.Join(", ", addresses.Take(3).Select(a => a.ToString())))
                            : L.F("Diag_DnsEmpty", node.Server),
                        addresses.Length > 0 ? null : L.T("Diag_DnsHint")));
                }
                catch (SocketException ex)
                {
                    items.Add(new(L.T("Diag_Dns"), DiagnosticVerdict.Fail, L.F("Diag_DnsFailed", node.Server, ex.SocketErrorCode), L.T("Diag_DnsHint")));
                }
            }

            // 4. A raw socket to the server, the way the core would open it.
            var plugin = registry.ById(node.Protocol);
            var udpOnly = node.Protocol is "hysteria2" or "tuic" or "wireguard"
                          || node.Transport.Kind is TransportKind.Kcp or TransportKind.Quic;
            if (udpOnly)
            {
                items.Add(new(L.T("Diag_Reach"), DiagnosticVerdict.Info, L.F("Diag_ReachUdp", plugin?.Descriptor.DisplayName ?? node.Protocol)));
            }
            else if (addresses.Length > 0)
            {
                var reach = await LatencyProbe.TcpAsync(node, 5000, ct).ConfigureAwait(false);
                if (reach.Reachable)
                {
                    items.Add(new(L.T("Diag_Reach"), DiagnosticVerdict.Ok, L.F("Diag_ReachOk", node.Endpoint, reach.Milliseconds)));
                }
                else
                {
                    var code = Enum.TryParse<SocketError>(reach.Error, out var parsed) ? parsed : SocketError.SocketError;
                    var (verdict, detail, hint) = DescribeSocketError(code, node.Endpoint);
                    if (code == SocketError.AccessDenied) firewall = true;
                    items.Add(new(L.T("Diag_Reach"), verdict, detail, hint));
                }
            }
        }

        // 5. The core's listener and the tunnel itself.
        var listening = PortProbe.IsListening(settings.SocksPort);
        if (engine.State == ConnectionState.Connected && listening)
        {
            items.Add(new(L.T("Diag_Listener"), DiagnosticVerdict.Ok, L.F("Diag_ListenerOk", settings.SocksPort, settings.HttpPort)));

            var health = await HealthProbe.CheckAsync(settings.HttpPort, settings.LatencyTestUrl, settings.LatencyTimeoutMs, ct)
                .ConfigureAwait(false);
            items.Add(new(L.T("Diag_Tunnel"),
                health.Ok ? DiagnosticVerdict.Ok : DiagnosticVerdict.Fail,
                health.Ok ? L.F("Diag_TunnelOk", health.LatencyMs) : L.F("Diag_TunnelFailed", health.Describe()),
                health.Ok ? null : L.T("Diag_TunnelHint")));
        }
        else if (engine.State == ConnectionState.Connected)
        {
            items.Add(new(L.T("Diag_Listener"), DiagnosticVerdict.Fail, L.F("Diag_ListenerMissing", settings.SocksPort), L.T("Diag_ListenerHint")));
        }
        else
        {
            items.Add(new(L.T("Diag_Listener"), DiagnosticVerdict.Info, L.T("Diag_NotConnected")));
        }

        // 6. Windows Firewall as configured.
        var finding = WindowsFirewall.Inspect(programs.Select(p => p.Item2).ToList());
        if (finding is null)
        {
            items.Add(new(L.T("Diag_Firewall"), DiagnosticVerdict.Info, L.T("Diag_FirewallUnknown")));
        }
        else if (!finding.Enabled)
        {
            items.Add(new(L.T("Diag_Firewall"), DiagnosticVerdict.Ok, L.F("Diag_FirewallOff", finding.Profiles)));
        }
        else if (finding.BlockRules.Count > 0)
        {
            firewall = true;
            items.Add(new(L.T("Diag_Firewall"), DiagnosticVerdict.Fail,
                L.F("Diag_FirewallBlockRules", string.Join("; ", finding.BlockRules)), L.T("Diag_FirewallHint")));
        }
        else if (finding.DefaultOutboundBlocked && finding.AllowRules.Count == 0)
        {
            firewall = true;
            items.Add(new(L.T("Diag_Firewall"), DiagnosticVerdict.Fail,
                L.F("Diag_FirewallDefaultBlock", finding.Profiles), L.T("Diag_FirewallHint")));
        }
        else
        {
            items.Add(new(L.T("Diag_Firewall"), DiagnosticVerdict.Ok,
                finding.AllowRules.Count > 0
                    ? L.F("Diag_FirewallAllowRules", string.Join("; ", finding.AllowRules))
                    : L.F("Diag_FirewallPermissive", finding.Profiles)));
        }

        // 7. Is Windows actually pointed at us?
        if (settings.TrafficMode == TrafficMode.SystemProxy)
        {
            var server = SystemProxy.CurrentServer ?? "";
            var ours = SystemProxy.IsEnabled && server.Contains($":{settings.HttpPort}", StringComparison.Ordinal);
            items.Add(new(L.T("Diag_SystemProxy"),
                engine.State != ConnectionState.Connected ? DiagnosticVerdict.Info : ours ? DiagnosticVerdict.Ok : DiagnosticVerdict.Warn,
                SystemProxy.IsEnabled ? L.F("Diag_SystemProxyIs", server) : L.T("Diag_SystemProxyOff"),
                engine.State == ConnectionState.Connected && !ours ? L.T("Diag_SystemProxyHint") : null));
        }

        return new DiagnosticReport(items, firewall, programs);
    }

    /// <summary>Winsock's verdict in words a user can act on.</summary>
    public static (DiagnosticVerdict Verdict, string Detail, string Hint) DescribeSocketError(SocketError error, string endpoint) => error switch
    {
        SocketError.AccessDenied => (DiagnosticVerdict.Fail, L.F("Diag_ReachDenied", endpoint), L.T("Diag_AccessDeniedHint")),
        SocketError.TimedOut => (DiagnosticVerdict.Warn, L.F("Diag_ReachTimeout", endpoint), L.T("Diag_TimeoutHint")),
        SocketError.ConnectionRefused => (DiagnosticVerdict.Fail, L.F("Diag_ReachRefused", endpoint), L.T("Diag_RefusedHint")),
        SocketError.NetworkUnreachable or SocketError.HostUnreachable =>
            (DiagnosticVerdict.Fail, L.F("Diag_ReachNoRoute", endpoint), L.T("Diag_NoRouteHint")),
        SocketError.ConnectionReset => (DiagnosticVerdict.Warn, L.F("Diag_ReachReset", endpoint), L.T("Diag_ResetHint")),
        _ => (DiagnosticVerdict.Fail, L.F("Diag_ReachOther", endpoint, error), L.T("Diag_TimeoutHint")),
    };

    /// <summary>Null on success, otherwise the error and the socket code behind it.</summary>
    private static async Task<(string Error, SocketError Code)?> DirectFetchAsync(string url, CancellationToken ct)
    {
        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return null;
        }
        catch (HttpRequestException ex)
        {
            var code = (ex.InnerException as SocketException)?.SocketErrorCode ?? SocketError.SocketError;
            return (ex.Message, code);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (L.T("Row_Timeout"), SocketError.TimedOut);
        }
    }
}
