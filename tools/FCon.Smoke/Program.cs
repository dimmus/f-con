using FCon.Abstractions.Plugins;
using FCon.Core.Config;
using FCon.Core.Engine;
using FCon.Core.Import;
using FCon.Core.Plugins;

// Job-object harness. Creates a job, starts a long-lived child under it, prints the
// child pid and then waits to be killed. The caller force-kills this process and checks
// the child died with it - the whole point of the job is surviving a kill we cannot
// intercept, so it cannot be tested from inside a single process.
if (args.Contains("--job-hold"))
{
    using var job = new FCon.Core.Engine.ProcessJob();
    Console.WriteLine($"job-active={job.IsActive}");

    using var child = System.Diagnostics.Process.Start(
        new System.Diagnostics.ProcessStartInfo("ping.exe", "-n 120 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;

    var assigned = job.Assign(child);
    Console.WriteLine($"child-pid={child.Id}");
    Console.WriteLine($"assigned={assigned}");
    Console.Out.Flush();

    await Task.Delay(TimeSpan.FromMinutes(2));
    return 0;
}

var registry = PluginRegistry.CreateDefault(loadExternal: false);
var importer = new LinkImporter(registry);

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine($"Plugins loaded: {string.Join(", ", registry.Plugins.Select(p => p.Descriptor.Id))}");
Console.WriteLine();

string[] links =
[
    // VLESS + REALITY + Vision (the 3x-ui default)
    "vless://b831381d-6324-4d53-ad4f-8cda48b30811@example.com:443?type=tcp&security=reality&flow=xtls-rprx-vision&sni=www.microsoft.com&fp=chrome&pbk=Xw5F2n8mQpLk3rTvB9cYdA1eGh7jNs4uZi6oPqRt0Ws&sid=6ba85179e30d4fc2#REALITY%20node",
    // VLESS + WebSocket + TLS
    "vless://b831381d-6324-4d53-ad4f-8cda48b30811@cdn.example.com:443?type=ws&security=tls&path=%2Fwspath%3Fed%3D2048&host=cdn.example.com&sni=cdn.example.com&fp=firefox#WS%20node",
    // VLESS + gRPC
    "vless://b831381d-6324-4d53-ad4f-8cda48b30811@grpc.example.com:443?type=grpc&security=tls&serviceName=GunService&mode=multi&sni=grpc.example.com#gRPC%20node",
    // VLESS + XHTTP
    "vless://b831381d-6324-4d53-ad4f-8cda48b30811@xh.example.com:443?type=xhttp&security=tls&path=%2Fxh&mode=stream-up&sni=xh.example.com#XHTTP%20node",
    // VMess base64 JSON
    "vmess://eyJ2IjoiMiIsInBzIjoiVk1lc3Mgbm9kZSIsImFkZCI6InZtLmV4YW1wbGUuY29tIiwicG9ydCI6IjQ0MyIsImlkIjoiYjgzMTM4MWQtNjMyNC00ZDUzLWFkNGYtOGNkYTQ4YjMwODExIiwiYWlkIjoiMCIsInNjeSI6ImF1dG8iLCJuZXQiOiJ3cyIsInR5cGUiOiJub25lIiwiaG9zdCI6InZtLmV4YW1wbGUuY29tIiwicGF0aCI6Ii92bXBhdGgiLCJ0bHMiOiJ0bHMiLCJzbmkiOiJ2bS5leGFtcGxlLmNvbSJ9",
    // Trojan
    "trojan://SuperSecret123@tj.example.com:443?type=tcp&security=tls&sni=tj.example.com#Trojan%20node",
    // Shadowsocks 2022, SIP002
    "ss://MjAyMi1ibGFrZTMtYWVzLTI1Ni1nY206OTUwbWVQdWNJK1YrN0RSYWhMbUR4bzRXeWlsQ1VVU2hDT0R3d1pucjVCbz0@ss.example.com:8388#SS2022%20node",
    // Legacy Shadowsocks
    "ss://YWVzLTI1Ni1nY206cGFzc3dvcmQxMjNAc3MyLmV4YW1wbGUuY29tOjg0NDM#Legacy%20SS",
    // SOCKS5
    "socks://dXNlcjpwYXNz@127.0.0.1:1080#Local%20SOCKS",
    // WireGuard
    // Keys are real 32-byte values so the cores accept them; they belong to nothing.
    "wireguard://DpFqN%2Bf%2Fq3lfTCpixdyFUYc2egR3SdSi9DE2DL%2FArNI%3D@wg.example.com:51820?publickey=GgGDJ1%2FxwGjL6l6%2FgeMb2Rfb5ksGfVZmtdt1BBxXFKI%3D&address=172.16.0.2/32&mtu=1420&reserved=1,2,3#WG%20node",
    // Hysteria 2 with obfuscation and port hopping (sing-box only)
    "hysteria2://hy2password@hy2.example.com:443?sni=hy2.example.com&obfs=salamander&obfs-password=obfs&mport=2080-3000&up=50&down=200#Hysteria2%20node",
    // TUIC v5 (sing-box only)
    "tuic://b831381d-6324-4d53-ad4f-8cda48b30811:tuicpass@tuic.example.com:443?congestion_control=bbr&udp_relay_mode=native&alpn=h3&sni=tuic.example.com#TUIC%20node",
    // AnyTLS (sing-box 1.12+ only)
    "anytls://anypass@any.example.com:443?sni=any.example.com&fp=chrome#AnyTLS%20node",
    // Shadowsocks 2022 wrapped in ShadowTLS v3 (sing-box only; Xray runs it as plain SS)
    "ss://MjAyMi1ibGFrZTMtYWVzLTEyOC1nY206UnMxbW1rd0o4cHRONGtqU1ozSDVmQT09@stls.example.com:443?plugin=shadow-tls%3Bhost%3Dcloud.tencent.com%3Bpassword%3Dstls%3Bversion%3D3#ShadowTLS%20node",
];

static bool Supports(FCon.Abstractions.Plugins.IProtocolPlugin plugin, EngineKind engine) =>
    FCon.Core.Engine.PoolEmitter.Supports(plugin.Descriptor.Engines, engine);

var settings = new AppSettings();
var routing = RoutingProfile.CreateDefault();
routing.BlockAds = true;
routing.Rules.Add(new RoutingRule
{
    Name = "Direct RU",
    Action = RuleAction.Direct,
    Domains = ["geosite:category-ru", "keyword:yandex", "full:example.org"],
    Ips = ["geoip:ru", "10.8.0.0/24"],
    Ports = ["80", "8000-9000"],
});

var xray = new XrayConfigBuilder(registry);
var singbox = new SingBoxConfigBuilder(registry);

var failures = 0;
foreach (var link in links)
{
    var result = importer.Import(link);
    if (!result.AnySucceeded)
    {
        failures++;
        Console.WriteLine($"FAIL parse: {string.Join("; ", result.Errors)}");
        continue;
    }

    var node = result.Nodes[0];
    var issues = importer.Validate(node);
    var rebuilt = registry.Require(node).BuildLink(node);

    Console.WriteLine($"[{node.Protocol,-12}] {node.DisplayName}");
    Console.WriteLine($"    endpoint  {node.Endpoint}  transport={node.Transport.Kind} security={node.Security.Kind}");
    if (issues.Count > 0) Console.WriteLine($"    validate  {string.Join(" | ", issues)}");
    Console.WriteLine($"    relink    {Trim(rebuilt)}");

    // Re-parse the rebuilt link: a round-trip must not lose the endpoint.
    var round = importer.Import(rebuilt);
    if (!round.AnySucceeded || round.Nodes[0].Endpoint != node.Endpoint)
    {
        failures++;
        Console.WriteLine("    ROUND-TRIP FAILED");
    }

    foreach (var engine in new[] { EngineKind.Xray, EngineKind.SingBox })
    {
        if (!Supports(registry.Require(node), engine))
        {
            Console.WriteLine($"    {engine,-8}  n/a (not supported by this core)");
            continue;
        }

        try
        {
            settings.Engine = engine;
            var config = engine == EngineKind.Xray
                ? xray.Build(node, settings, routing)
                : singbox.Build(node, settings, routing);

            var json = config.ToJson();
            System.Text.Json.JsonDocument.Parse(json); // must be valid JSON
            Console.WriteLine($"    {engine,-8}  {json.Length} bytes"
                              + (config.Warnings.Count > 0
                                  ? $"  warnings: {string.Join(" | ", config.Warnings)}"
                                  : ""));
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine($"    {engine,-8}  FAILED: {ex.Message}");
        }
    }
    Console.WriteLine();
}

// Native entry points. LibraryImport uses exact spelling and resolves lazily, so a
// wrong name is invisible until the call is made at runtime. Check the symbols exist.
foreach (var (library, export) in new[]
         {
             ("wininet.dll", "InternetSetOptionW"),
             ("dwmapi.dll", "DwmSetWindowAttribute"),
         })
{
    var handle = System.Runtime.InteropServices.NativeLibrary.Load(library);
    var found = System.Runtime.InteropServices.NativeLibrary.TryGetExport(handle, export, out _);
    Console.WriteLine($"{(found ? "ok  " : "MISS")} {library}!{export}");
    if (!found) failures++;
}
Console.WriteLine();

// The advisor is only useful if it actually fires. Feed it a deliberately bad server
// and settings, and require it to name the problems.
{
    var bad = importer.Import(
        "vmess://eyJ2IjoiMiIsInBzIjoiYmFkIiwiYWRkIjoiYi5leGFtcGxlLmNvbSIsInBvcnQiOiI4MCIsImlkIjoiYjgzMTM4MWQtNjMyNC00ZDUzLWFkNGYtOGNkYTQ4YjMwODExIiwiYWlkIjoiNjQiLCJzY3kiOiJub25lIiwibmV0IjoidGNwIiwidGxzIjoiIn0=")
        .Nodes[0] with
    {
        Security = new FCon.Abstractions.Model.SecurityOptions { AllowInsecure = true },
    };

    var risky = new AppSettings
    {
        AllowLan = true,
        VerifyOnConnect = false,
        ContinuousHealthCheck = false,
        LogLevel = "debug",
        EnableSniffing = false,
        RoutingMode = RoutingMode.Rules,
        InCoreFailover = false,
    };
    var riskyRouting = new RoutingProfile { BypassPrivateNetworks = false };

    var found = FCon.Core.Health.ConfigAdvisor.Inspect(risky, riskyRouting, bad);
    string[] expected =
    [
        "tls.insecure", "tls.none", "vmess.alterid", "vmess.cipher",
        "lan.open", "health.verify", "health.monitor", "sniff.off",
        "log.debug", "route.private", "failover.restart",
    ];

    foreach (var id in expected)
    {
        var hit = found.Any(a => a.Id == id);
        if (!hit) { Console.WriteLine($"MISS advisor did not report {id}"); failures++; }
    }
    Console.WriteLine($"ok   advisor reported {found.Count} finding(s), all {expected.Length} expected present");

    // Auto-fix must resolve exactly the settings-level ones and leave the rest alone.
    var fixes = FCon.Core.Health.ConfigAdvisor.ApplyFixes(risky, riskyRouting);
    var after = FCon.Core.Health.ConfigAdvisor.Inspect(risky, riskyRouting, bad);
    var stillSettings = after.Any(a => a.Id is "health.verify" or "health.monitor"
                                       or "sniff.off" or "log.debug" or "route.private"
                                       or "failover.restart");
    if (stillSettings) { Console.WriteLine("MISS auto-fix left a fixable finding behind"); failures++; }
    if (!after.Any(a => a.Id == "tls.insecure")) { Console.WriteLine("MISS auto-fix wrongly cleared a server issue"); failures++; }
    Console.WriteLine($"ok   auto-fix applied {fixes.Count} change(s); server issues correctly left for the user");
}

// Exit-info parsing, for both response shapes the probe accepts.
{
    var trace = FCon.Core.Health.ExitInfoProbe.Parse(
        "fl=123abc\nh=www.cloudflare.com\nip=185.199.110.153\nts=1700000000\nvisit_scheme=https\nloc=NL\n");
    if (trace?.Ip != "185.199.110.153" || trace.CountryCode != "NL")
    { Console.WriteLine("MISS trace parse"); failures++; }
    else if (trace.CountryName != "Netherlands")
    { Console.WriteLine($"MISS country name: got {trace.CountryName}"); failures++; }
    else Console.WriteLine($"ok   exit trace parse -> {trace.Describe()}");

    var json = FCon.Core.Health.ExitInfoProbe.Parse("{\"ip\":\"1.2.3.4\",\"country\":\"DE\"}");
    if (json?.Ip != "1.2.3.4" || json.CountryName != "Germany")
    { Console.WriteLine("MISS json exit parse"); failures++; }
    else Console.WriteLine($"ok   exit json parse -> {json.Describe()}");

    // An unknown code must degrade to the raw value, not throw.
    var odd = FCon.Core.Health.ExitInfoProbe.Parse("ip=9.9.9.9\nloc=T1\n");
    if (odd?.CountryName != "T1") { Console.WriteLine("MISS unknown country code handling"); failures++; }
    else Console.WriteLine("ok   unknown country code degrades safely");
}

// Byte formatting is what the status bar shows, so it should read the way a human writes it.
{
    (double Value, string Expected)[] cases =
    [
        (512, "512 B"), (2048, "2 KB"), (1_572_864, "1.5 MB"), (3_221_225_472, "3 GB"),
    ];
    foreach (var (value, expected) in cases)
    {
        var actual = FCon.Core.Health.TrafficSample.FormatBytes(value);
        if (actual != expected)
        { Console.WriteLine($"MISS format {value}: got {actual}, want {expected}"); failures++; }
    }
    Console.WriteLine($"ok   byte formatting ({FCon.Core.Health.TrafficSample.FormatRate(1_572_864)})");
}

// The health probe must classify a dead listener rather than reporting success.
{
    var deadPort = 39099;
    var probe = await FCon.Core.Health.HealthProbe.CheckAsync(
        deadPort, "https://www.gstatic.com/generate_204", 2500);
    if (probe.Ok) { Console.WriteLine("MISS health probe reported success against a dead port"); failures++; }
    else Console.WriteLine($"ok   health probe on a dead listener -> {probe.Verdict}");
}

// Duplicate handling in the profile store. Real feeds repeat entries and reuse labels,
// and a repeat used to abort the update with "an item with the same key has already been
// added" - permanently, because the stored repeat re-triggered it on every later update.
{
    var storePath = Path.Combine(Path.GetTempPath(), $"fcon-smoke-{Guid.NewGuid():N}.json");
    try
    {
        var store = new FCon.Core.Storage.ProfileStore(storePath);
        var subscriptionId = Guid.NewGuid();

        var dupLink = "vless://b831381d-6324-4d53-ad4f-8cda48b30811@dup.example.com:443"
                    + "?type=tcp&security=tls&sni=dup.example.com#Netherlands";
        var feed = importer.Import(string.Join(Environment.NewLine,
        [
            dupLink,
            dupLink,                                   // byte-identical repeat
            dupLink + "%20again",                      // same server, different label
            "vless://b831381d-6324-4d53-ad4f-8cda48b30811@other.example.com:443"
                + "?type=tcp&security=tls&sni=other.example.com#Netherlands",
        ]), subscriptionId);

        var stored = store.ReplaceSubscriptionNodes(subscriptionId, feed.Nodes);
        if (stored != 2)
        { Console.WriteLine($"MISS dedupe: stored {stored}, want 2"); failures++; }

        var remarks = store.Nodes.Select(n => n.Remark).ToList();
        if (remarks.Distinct(StringComparer.OrdinalIgnoreCase).Count() != remarks.Count)
        { Console.WriteLine($"MISS label collision left duplicates: {string.Join(", ", remarks)}"); failures++; }

        // Ids must survive an update, otherwise the active server is lost on every refresh.
        var before = store.Nodes.OrderBy(n => n.Server).Select(n => n.Id).ToList();
        store.ReplaceSubscriptionNodes(subscriptionId, feed.Nodes);
        var after = store.Nodes.OrderBy(n => n.Server).Select(n => n.Id).ToList();
        if (!before.SequenceEqual(after))
        { Console.WriteLine("MISS node ids churned across a repeat update"); failures++; }
        else Console.WriteLine($"ok   duplicate feed -> {stored} nodes, labels {string.Join(" / ", remarks)}");

        // Deactivating a subscription must hide its servers without losing them: the
        // stored list is what makes re-enabling instant and keeps the quality history.
        store.UpsertSubscription(new FCon.Core.Storage.Subscription
        {
            Id = subscriptionId, Name = "feed", Url = "https://example.com/sub",
        });

        var off = store.SetSubscriptionEnabled(subscriptionId, false);
        if (off != stored)
        { Console.WriteLine($"MISS deactivate reported {off} nodes, want {stored}"); failures++; }
        else if (store.ActiveNodes.Count != 0)
        { Console.WriteLine($"MISS deactivated servers still in play: {store.ActiveNodes.Count}"); failures++; }
        else if (store.Nodes.Count != stored)
        { Console.WriteLine("MISS deactivating deleted nodes instead of hiding them"); failures++; }
        else
        {
            store.SetSubscriptionEnabled(subscriptionId, true);
            if (store.ActiveNodes.Count != stored)
            { Console.WriteLine("MISS reactivating did not restore the servers"); failures++; }
            else Console.WriteLine("ok   deactivate hides servers, keeps them, and reactivates cleanly");
        }

        await store.FlushAsync();
    }
    finally
    {
        try { File.Delete(storePath); } catch (IOException) { }
    }
}

// Subscription-shaped input: base64 blob wrapping a link list.
var blob = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", links)));
var bulk = importer.Import(blob);
Console.WriteLine($"Base64 subscription blob -> {bulk.Nodes.Count} nodes, {bulk.Errors.Count} errors");
if (bulk.Nodes.Count != links.Length) failures++;

// Write the sample links into the real profile store so the GUI has something to show.
if (args.Contains("--seed"))
{
    var store = new FCon.Core.Storage.ProfileStore();
    store.RemoveNodes(store.Nodes.Select(n => n.Id).ToList());
    store.AddNodes(bulk.Nodes);
    await store.FlushAsync();
    Console.WriteLine($"Seeded {store.Nodes.Count} servers into {FCon.Core.AppPaths.ProfilesFile}");
}

// Hand every generated config to the real cores. Valid JSON is a low bar — this is
// what catches deprecated options and schema drift between core versions.
var emitIndex = Array.IndexOf(args, "--emit");
var outputDir = emitIndex >= 0 && emitIndex + 1 < args.Length
    ? args[emitIndex + 1]
    : Path.Combine(Path.GetTempPath(), "fcon-smoke-configs");

Directory.CreateDirectory(outputDir);

// Isolated ports: the cores are really started below, and must not collide with a
// running FCon or with each other.
settings.SocksPort = 39080;
settings.HttpPort = 39081;
settings.ApiPort = 0;

// Start-up configs use the shipping default routing profile. The rich profile above
// pulls in geosite/geoip rule-sets, and sing-box refuses to start if it cannot
// download one — which it never can here, since these servers do not exist.
var startupRouting = RoutingProfile.CreateDefault();

var written = 0;
foreach (var link in links)
{
    var n = importer.Import(link).Nodes[0];
    var safe = string.Concat(n.DisplayName.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

    foreach (var engine in new[] { EngineKind.SingBox, EngineKind.Xray })
    {
        if (!Supports(registry.Require(n), engine)) continue;
        settings.Engine = engine;
        var cfg = engine == EngineKind.Xray
            ? xray.Build(n, settings, startupRouting)
            : singbox.Build(n, settings, startupRouting);
        var suffix = engine == EngineKind.Xray ? "xray" : "singbox";
        File.WriteAllText(Path.Combine(outputDir, $"{safe}.{suffix}.json"), cfg.ToJson());
        written++;
    }
}

// The grouped shape: every server the core can run, in one config, with the selector
// and automatic group in front. This is what the app actually starts, so it must run.
{
    var all = links.Select(l => importer.Import(l).Nodes[0]).ToList();
    var version = EngineLocator.Describe(EngineKind.SingBox)?.Version;

    settings.Engine = EngineKind.SingBox;
    var fixedPool = singbox.Build(all[0], all, settings, startupRouting, version, autoSelect: false);
    File.WriteAllText(Path.Combine(outputDir, "_group_fixed.singbox.json"), fixedPool.ToJson());
    var autoPool = singbox.Build(all[0], all, settings, startupRouting, version, autoSelect: true);
    File.WriteAllText(Path.Combine(outputDir, "_group_auto.singbox.json"), autoPool.ToJson());
    written += 2;

    if (fixedPool.SelectorTag is null || fixedPool.Members.Count < all.Count - 1)
    {
        Console.WriteLine($"MISS grouped sing-box config: selector={fixedPool.SelectorTag}, members={fixedPool.Members.Count}");
        failures++;
    }
    else
    {
        Console.WriteLine($"ok   grouped sing-box config: {fixedPool.Members.Count} members behind selector \"{fixedPool.SelectorTag}\"");
    }

    settings.Engine = EngineKind.Xray;
    var xrayVersion = EngineLocator.Describe(EngineKind.Xray)?.Version;
    var balanced = xray.Build(all[0], all, settings, startupRouting, xrayVersion, autoSelect: true);
    File.WriteAllText(Path.Combine(outputDir, "_group_auto.xray.json"), balanced.ToJson());
    written++;

    if (balanced.AutoTag is null || balanced.Root["observatory"] is null)
    {
        Console.WriteLine("MISS grouped Xray config has no balancer/observatory");
        failures++;
    }
    else
    {
        Console.WriteLine($"ok   grouped Xray config: {balanced.Members.Count} members behind balancer \"{balanced.AutoTag}\"");
    }
}
Console.WriteLine($"Wrote {written} configs to {outputDir}");
Console.WriteLine();

foreach (var engine in new[] { EngineKind.SingBox, EngineKind.Xray })
{
    var info = EngineLocator.Describe(engine);
    if (info is null)
    {
        Console.WriteLine($"skip  {engine}: binary not installed, configs not verified");
        continue;
    }

    var suffix = engine == EngineKind.Xray ? "xray" : "singbox";
    foreach (var file in Directory.GetFiles(outputDir, $"*.{suffix}.json").Order())
    {
        var name = Path.GetFileName(file).Replace($".{suffix}.json", "");
        var (ok, output) = RunCore(info.ExecutablePath, engine, file);
        Console.WriteLine(ok
            ? $"ok    {engine} {name}"
            : $"FAIL  {engine} {name}: {FirstFatal(output)}");
        if (!ok) failures++;
    }
}
Console.WriteLine();

/// <summary>
/// Actually start the core and require it to stay up. "sing-box check" only validates
/// the schema: it happily passed configs that died on startup with deprecated-option
/// and routing errors, so the config is not considered good until it has run.
/// </summary>
static (bool Ok, string Output) RunCore(string exe, EngineKind engine, string configPath)
{
    var arguments = engine == EngineKind.Xray
        ? $"run -c \"{configPath}\""
        : $"run -c \"{configPath}\"";

    var startInfo = new System.Diagnostics.ProcessStartInfo(exe, arguments)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    // Match what EngineController sets, or the test would not exercise the real launch.
    startInfo.Environment["ENABLE_DEPRECATED_IMPLICIT_DEFAULT_HTTP_CLIENT"] = "true";

    using var process = System.Diagnostics.Process.Start(startInfo)!;

    var buffer = new System.Text.StringBuilder();
    process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (buffer) buffer.AppendLine(e.Data); };
    process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (buffer) buffer.AppendLine(e.Data); };
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();

    // Surviving a few seconds means it bound its listeners and initialised routing.
    var exited = process.WaitForExit(4000);
    string output;
    lock (buffer) output = buffer.ToString();

    if (!exited)
    {
        process.Kill(entireProcessTree: true);
        process.WaitForExit(3000);
        return (true, output);
    }

    return (process.ExitCode == 0, output);
}

static string FirstFatal(string output)
{
    // Strip the ANSI colouring the cores emit before picking the interesting line.
    var clean = System.Text.RegularExpressions.Regex.Replace(output, "\\[[0-9;]*m", "");
    var line = clean.ReplaceLineEndings("\n").Split('\n')
        .FirstOrDefault(l => l.Contains("FATAL", StringComparison.OrdinalIgnoreCase)
                             || l.Contains("failed", StringComparison.OrdinalIgnoreCase));
    line ??= clean.ReplaceLineEndings("\n").Split('\n').FirstOrDefault(l => l.Length > 0) ?? "unknown";
    return line.Length > 170 ? line[..170] : line;
}

// The real-URL latency path: a throw-away sing-box carrying every server, measured
// through its API. The servers do not exist, so every answer is "unreachable" - what is
// being proved is that the probe core starts, the API authenticates, and each server
// gets a verdict by the URL method rather than a handshake.
if (EngineLocator.Describe(EngineKind.SingBox) is not null)
{
    var probeSettings = new AppSettings { LatencyTimeoutMs = 1500, LatencyConcurrency = 16, ApiPort = 39082 };
    var tester = new FCon.Core.Health.LatencyTester(
        registry, () => probeSettings, () => (null, null), (m, _) => Console.WriteLine("      " + m));

    var all = links.Select(l => importer.Import(l).Nodes[0]).ToList();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var measured = await tester.TestAsync(all);
    sw.Stop();

    var byUrl = measured.Count(r => r.Method == FCon.Core.Health.LatencyTester.MethodUrl);
    if (measured.Count != all.Count || byUrl < all.Count - 1)
    {
        Console.WriteLine($"MISS latency tester: {measured.Count}/{all.Count} results, {byUrl} by real request");
        failures++;
    }
    else
    {
        Console.WriteLine($"ok   latency tester measured {byUrl} servers by real request in {sw.ElapsedMilliseconds} ms (probe core)");
    }
}
else
{
    Console.WriteLine("skip  latency tester: sing-box not installed");
}
Console.WriteLine();

// Dump a full config pair for visual inspection when asked.
if (args.Contains("--dump"))
{
    var n = importer.Import(links[0]).Nodes[0];
    settings.Engine = EngineKind.Xray;
    Console.WriteLine("===== XRAY =====");
    Console.WriteLine(xray.Build(n, settings, routing).ToJson());
    Console.WriteLine("===== SING-BOX =====");
    Console.WriteLine(singbox.Build(n, settings, routing).ToJson());
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

static string Trim(string s) => s.Length <= 100 ? s : s[..100] + "...";
