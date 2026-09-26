using System.Diagnostics;
using System.Security.Cryptography;
using FCon.Abstractions.Model;
using FCon.Abstractions.Plugins;
using FCon.Core.Config;
using FCon.Core.Engine;
using FCon.Core.Net;
using FCon.Core.Plugins;

namespace FCon.Core.Health;

/// <summary>
/// Measures servers the way browsing will feel: a real HTTPS request through each one,
/// not a TCP handshake to its front door. A handshake to a CDN edge says nothing about
/// the tunnel behind it.
///
/// Servers that are already inside the running core are measured through its API. The
/// rest go into a throw-away sing-box process that carries every candidate as an
/// outbound and nothing else, and are measured through that. Only when no sing-box is
/// installed, or a server cannot run on it, does the old handshake test remain.
/// </summary>
public sealed class LatencyTester(
    PluginRegistry registry,
    Func<AppSettings> settings,
    Func<(GeneratedConfig? Config, IClashApi? Api)> live,
    Action<string, bool> log)
{
    public const string MethodUrl = "url";
    public const string MethodTcp = "tcp";

    public async Task<IReadOnlyList<LatencyResult>> TestAsync(
        IReadOnlyList<ProxyNode> nodes,
        IProgress<LatencyResult>? progress = null,
        CancellationToken ct = default)
    {
        var s = settings();
        var results = new List<LatencyResult>();
        var remaining = nodes.ToList();
        if (remaining.Count == 0) return results;

        // 1. Servers the running core already knows: measure in place.
        var (config, api) = live();
        if (config is not null && api is not null)
        {
            var onBoard = remaining
                .Select(n => (Node: n, Tag: config.TagFor(n.Id)))
                .Where(x => x.Tag is not null)
                .Select(x => (x.Node, x.Tag!))
                .ToList();

            if (onBoard.Count > 0)
            {
                results.AddRange(await MeasureAsync(api, onBoard, s, progress, ct).ConfigureAwait(false));
                var done = onBoard.Select(x => x.Item1.Id).ToHashSet();
                remaining.RemoveAll(n => done.Contains(n.Id));
            }
        }

        // 2. Everything else through a private probe core.
        if (remaining.Count > 0 && EngineLocator.Describe(EngineKind.SingBox) is { } singBox)
        {
            var capable = remaining.Where(n => CanProbe(n)).ToList();
            if (capable.Count > 0)
            {
                var apiPort = PortProbe.FindFree(s.ApiPort > 0 ? s.ApiPort + 10 : 10820);
                var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                var probeConfig = new SingBoxConfigBuilder(registry)
                    .BuildProbe(capable, s, apiPort, secret, singBox.Version);

                foreach (var warning in probeConfig.Warnings) log($"Latency test: {warning}", true);

                await using var core = await ProbeCore.StartAsync(singBox, probeConfig, apiPort, secret, ct)
                    .ConfigureAwait(false);

                if (core is not null)
                {
                    var members = probeConfig.Members.Select(m => (m.Node, m.Tag)).ToList();
                    results.AddRange(await MeasureAsync(core.Api, members, s, progress, ct).ConfigureAwait(false));
                    var done = members.Select(m => m.Node.Id).ToHashSet();
                    remaining.RemoveAll(n => done.Contains(n.Id));
                }
                else
                {
                    log("Latency test: the probe core did not start; falling back to TCP handshakes.", true);
                }
            }
        }

        // 3. Last resort: the handshake test.
        if (remaining.Count > 0)
        {
            var tcp = await LatencyProbe.TcpBatchAsync(
                remaining, s.LatencyConcurrency, s.LatencyTimeoutMs, progress, ct).ConfigureAwait(false);
            results.AddRange(tcp);
        }

        return results;
    }

    private bool CanProbe(ProxyNode node)
    {
        var plugin = registry.ById(node.Protocol);
        return plugin is not null
               && PoolEmitter.Supports(plugin.Descriptor.Engines, EngineKind.SingBox)
               && plugin.Validate(node).Count == 0;
    }

    private static async Task<List<LatencyResult>> MeasureAsync(
        IClashApi api,
        IReadOnlyList<(ProxyNode Node, string Tag)> members,
        AppSettings settings,
        IProgress<LatencyResult>? progress,
        CancellationToken ct)
    {
        var results = new List<LatencyResult>();
        var gate = new SemaphoreSlim(Math.Max(1, settings.LatencyConcurrency));

        var tasks = members.Select(async member =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var delay = await api.DelayAsync(member.Tag, settings.LatencyTestUrl, settings.LatencyTimeoutMs, ct)
                    .ConfigureAwait(false);

                var result = delay is { } ms
                    ? new LatencyResult(member.Node.Id, ms, null) { Method = MethodUrl }
                    : LatencyResult.Unreachable(member.Node.Id, "No response through the tunnel.") with { Method = MethodUrl };

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
}

/// <summary>
/// A sing-box started for measurement only: no listeners, no system proxy, no TUN.
/// Killed with the app through the same job-object guarantee as the real core.
/// </summary>
internal sealed class ProbeCore : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ProcessJob _job;

    private ProbeCore(Process process, ProcessJob job, ClashApiClient api)
    {
        _process = process;
        _job = job;
        Api = api;
    }

    public ClashApiClient Api { get; }

    public static async Task<ProbeCore?> StartAsync(
        EngineInfo engine,
        GeneratedConfig config,
        int apiPort,
        string secret,
        CancellationToken ct)
    {
        AppPaths.EnsureCreated();
        var workDir = Path.Combine(AppPaths.RuntimeDirectory, "probe");
        Directory.CreateDirectory(workDir);

        var configPath = Path.Combine(workDir, "sing-box.probe.json");
        await File.WriteAllTextAsync(configPath, config.ToJson(), ct).ConfigureAwait(false);

        var startInfo = new ProcessStartInfo(engine.ExecutablePath, $"run -c \"{configPath}\" -D \"{workDir}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(engine.ExecutablePath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var job = new ProcessJob();
        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            job.Dispose();
            return null;
        }

        job.Assign(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (process.HasExited) break;
            if (PortProbe.IsListening(apiPort))
                return new ProbeCore(process, job, new ClashApiClient(apiPort, secret));
            await Task.Delay(150, CancellationToken.None).ConfigureAwait(false);
        }

        Kill(process);
        process.Dispose();
        job.Dispose();
        return null;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(3000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    public ValueTask DisposeAsync()
    {
        Api.Dispose();
        Kill(_process);
        _process.Dispose();
        _job.Dispose();
        return ValueTask.CompletedTask;
    }
}
