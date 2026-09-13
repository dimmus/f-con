using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using FCon.Abstractions.Model;

namespace FCon.Core.Net;

public static partial class PortProbe
{
    /// <summary>True when something on this machine is accepting TCP on the port.</summary>
    public static bool IsListening(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(e => e.Port == port);
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Name the process listening on a port, so a bind failure can say who took it
    /// rather than leaving the user with a bare Winsock error.
    /// </summary>
    public static string? DescribeListener(int port)
    {
        var pid = FindListenerPid(port);
        if (pid is null) return null;

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid.Value);
            var path = TryGetPath(process);
            return path is null
                ? $"{process.ProcessName} (pid {pid})"
                : $"{process.ProcessName} (pid {pid}) - {path}";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return $"pid {pid}";
        }
    }

    private static string? TryGetPath(System.Diagnostics.Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or NotSupportedException)
        {
            // Reading another user's or an elevated process's module list is not permitted.
            return null;
        }
    }

    /// <summary>Walk the IPv4 TCP table looking for a LISTEN row on this port.</summary>
    private static int? FindListenerPid(int port)
    {
        const int afInet = 2;
        const int tcpTableOwnerPidListener = 3;
        const int stateListen = 2;

        var size = 0;
        _ = GetExtendedTcpTable(nint.Zero, ref size, false, afInet, tcpTableOwnerPidListener, 0);
        if (size <= 0) return null;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, afInet, tcpTableOwnerPidListener, 0) != 0)
                return null;

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<TcpRowOwnerPid>();

            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<TcpRowOwnerPid>(buffer + sizeof(int) + (i * rowSize));
                if (row.State != stateListen) continue;

                // localPort is stored big-endian in the low two bytes.
                var rowPort = ((row.LocalPort & 0xFF) << 8) | ((row.LocalPort >> 8) & 0xFF);
                if (rowPort == port) return (int)row.OwningPid;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [LibraryImport("iphlpapi.dll", EntryPoint = "GetExtendedTcpTable")]
    private static partial uint GetExtendedTcpTable(
        nint table,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        int tableClass,
        int reserved);

    /// <summary>Find a free port near a preferred one, so a busy default does not block startup.</summary>
    public static int FindFree(int preferred, int attempts = 32)
    {
        for (var port = preferred; port < preferred + attempts && port <= 65535; port++)
        {
            if (!IsListening(port)) return port;
        }
        return preferred;
    }
}

public sealed record LatencyResult(Guid NodeId, int Milliseconds, string? Error)
{
    public bool Reachable => Milliseconds >= 0;
    public static LatencyResult Unreachable(Guid id, string error) => new(id, -1, error);
}

/// <summary>
/// Latency measurement. Bulk testing uses a TCP handshake against the server endpoint,
/// which needs no running core and parallelises cleanly; the active connection can also
/// be measured end-to-end through the tunnel.
/// </summary>
public static class LatencyProbe
{
    /// <summary>Time a TCP handshake to the node endpoint.</summary>
    public static async Task<LatencyResult> TcpAsync(
        ProxyNode node,
        int timeoutMs = 5000,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);

            if (IPAddress.TryParse(node.Server, out var ip))
                await socket.ConnectAsync(ip, node.Port, timeout.Token).ConfigureAwait(false);
            else
                await socket.ConnectAsync(node.Server, node.Port, timeout.Token).ConfigureAwait(false);

            stopwatch.Stop();
            return new LatencyResult(node.Id, (int)stopwatch.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return LatencyResult.Unreachable(node.Id, "Timed out.");
        }
        catch (SocketException ex)
        {
            return LatencyResult.Unreachable(node.Id, ex.SocketErrorCode.ToString());
        }
    }

    /// <summary>Test many nodes at once, bounded so we do not exhaust the socket pool.</summary>
    public static async Task<IReadOnlyList<LatencyResult>> TcpBatchAsync(
        IEnumerable<ProxyNode> nodes,
        int concurrency,
        int timeoutMs,
        IProgress<LatencyResult>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<LatencyResult>();
        var gate = new SemaphoreSlim(Math.Max(1, concurrency));

        var tasks = nodes.Select(async node =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = await TcpAsync(node, timeoutMs, ct).ConfigureAwait(false);
                progress?.Report(result);
                lock (results) results.Add(result);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    /// <summary>
    /// Measure real round-trip through the running tunnel by fetching a tiny endpoint via
    /// the local HTTP listener. This is the number that reflects what browsing will feel like.
    /// </summary>
    public static async Task<LatencyResult> ThroughProxyAsync(
        ProxyNode node,
        int httpPort,
        string url,
        int timeoutMs = 5000,
        CancellationToken ct = default)
    {
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{httpPort}"),
            UseProxy = true,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            stopwatch.Stop();

            return response.IsSuccessStatusCode || (int)response.StatusCode == 204
                ? new LatencyResult(node.Id, (int)stopwatch.ElapsedMilliseconds, null)
                : LatencyResult.Unreachable(node.Id, $"HTTP {(int)response.StatusCode}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return LatencyResult.Unreachable(node.Id, "Timed out.");
        }
        catch (HttpRequestException ex)
        {
            return LatencyResult.Unreachable(node.Id, ex.Message);
        }
    }
}
