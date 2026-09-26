# FCon

A Windows desktop VPN client with the full protocol surface that 3x-ui provisions,
implemented as plugins over two interchangeable proxy cores.

Everything outside the protocol layer — the UI, routing, subscriptions, storage,
process supervision — is FCon's own design.

## Status

| Area | State |
| --- | --- |
| Protocol plugins (10) | Implemented, round-trip tested |
| sing-box config generation | Every protocol starts the real core, alone and grouped |
| Xray config generation | Every protocol starts the real core, alone and balanced |
| In-core failover (selector / urltest, balancer / observatory) | Implemented |
| Connection supervision (verify, passive health, switch, restart, fail over) | Implemented, 43 unit tests |
| Real-request latency testing through a probe core | Implemented |
| Core download with checksum verification | Implemented |
| GUI (servers, subscriptions, routing, log, settings) | Implemented |
| System proxy, tray, single instance | Implemented |
| Release and portable packaging | `build/publish.ps1` |
| End-to-end tunnel through a real server | **Not yet verified** — needs a live server |

## Getting a proxy core

FCon does not bundle the cores. Settings → **Download** fetches the latest stable
release from GitHub, checks the archive's SHA-256 against what the release publishes
(the asset digest from GitHub's API, and for Xray also the `.dgst` file), and only then
puts the executable in place. A release without a published checksum is refused. When
a tunnel is already up the download goes through it, which is how GitHub is reached
from networks that block it. **Update** on an installed core does the same thing.

To install by hand instead:

1. Download the Windows amd64 archive from the upstream releases page
   (the **manual** link in Settings opens it).
2. Unzip and copy the executable into the repo-root `engines/` folder:

       engines/sing-box/sing-box.exe
       engines/xray/xray.exe

3. Build. `engines/` is mirrored into the output directory, which is where the app
   actually reads from (`<app>/engines/...`).
4. Press **Re-check engines**. Settings reports the version it found.

Lookup order: `%FCON_ENGINES_DIR%` if set, then `engines/` beside the executable,
then the executable's own directory, then `PATH`. Set `FCON_ENGINES_DIR` for a
portable install that keeps its binaries elsewhere.

Use a **stable** release. The generator needs **sing-box 1.12 or newer** (typed DNS
servers, rule actions, endpoints) and refuses to start an older one with a clear
message. The core's version is read at start and passed to every plugin, so options a
given core does not have are left out with a warning rather than crashing it: port
hopping and AnyTLS need 1.12, XHTTP needs Xray 24.10.31.

Xray additionally needs `geoip.dat` and `geosite.dat` in `%APPDATA%\FCon\assets`
for `geosite:` / `geoip:` routing rules. sing-box fetches equivalent rule-sets on
demand and needs no asset files.

## Protocol coverage

The 3x-ui matrix, reproduced client-side.

| Protocol | Link schemes | sing-box | Xray |
| --- | --- | --- | --- |
| VLESS (XTLS Vision, REALITY) | `vless://` | yes | yes |
| VMess (base64-JSON and URI forms) | `vmess://` | yes | yes |
| Trojan | `trojan://` | yes | yes |
| Shadowsocks (incl. 2022 ciphers, SIP003) | `ss://` | yes | yes (no SIP003 plugins) |
| SOCKS5 | `socks://`, `socks5://` | yes | yes |
| HTTP CONNECT | `http-proxy://` | yes | yes |
| WireGuard | `wireguard://`, `wg://` | yes | yes |
| Hysteria 2 (Salamander obfuscation, port hopping) | `hysteria2://`, `hy2://` | yes | no |
| TUIC v5 (BBR, native/QUIC UDP relay) | `tuic://` | yes | no |
| AnyTLS | `anytls://` | yes (1.12+) | no |
| ShadowTLS v3 over Shadowsocks | `ss://...?plugin=shadow-tls;host=...;password=...;version=3` | yes | no |

Transports: TCP/raw (with HTTP header obfuscation), mKCP, WebSocket, HTTP/2, QUIC,
gRPC, HTTPUpgrade, XHTTP.
Security: none, TLS (uTLS fingerprints, ALPN, pinned certs), REALITY.
Plus multiplexing, and early-data handling on WebSocket paths.

The last four are the sing-box-only protocols built for lossy links and for
TLS-in-TLS detection; 3x-ui does not provision them, but most other panels do.

**Engine gaps are surfaced, not hidden.** mKCP, XHTTP and raw HTTP obfuscation have
no sing-box equivalent; selecting them with sing-box produces a warning in the log
telling you to switch that server to Xray. The same applies in reverse for SIP003
Shadowsocks plugins and the QUIC protocols, which only sing-box runs. A server the
selected core cannot run is left out of the failover group with a note in the log.

## How a connection stays up

A core binds its listener whether or not the server behind it is alive, so nothing is
called connected until a real request has gone through the tunnel and come back.

**Servers are grouped inside the core.** Every usable server (best first, capped by
*Servers in the group*, default 32) goes into the config. With sing-box they sit behind
a `selector` the routing points at, plus a `urltest` group that keeps every member
measured on the health interval. With Xray they become extra outbounds behind a
least-ping balancer fed by the observatory. The user's pick is the default; switching
is a single API call, not a restart.

**Real traffic is the first health signal.** While connected, bytes arriving through
the core count as proof the link works, and the synthetic probe runs only when nothing
has flowed. A probe that fails is re-checked within seconds rather than after a full
interval.

**Recovery escalates.** When the threshold of failed probes is reached, the running
core is first asked to hand the connection to its automatic group; if traffic then
flows, nothing was restarted and other applications' connections were never dropped.
Only when that is not possible (Xray, no API, or the whole group is dead) is the core
restarted, then failed over to the next-ranked server, then retried in rounds with
backoff — indefinitely, until the user disconnects. The status card follows whichever
server the core is actually using.

**Testing measures what browsing will feel like.** *Test all* sends a real HTTPS
request through each server: servers already in the running core are measured through
its API, the rest go into a throw-away sing-box that carries them as outbounds and
nothing else. A TCP handshake to the front door is used only when no sing-box is
installed or a server cannot run on it. Results by real request feed the same quality
record the supervisor keeps, so *Sort by record* ranks on evidence.

The control API is protected by a bearer token generated on first run; nothing else on
the machine can re-point the selector.

## Protocols are plugins

`FCon.Abstractions` is the whole contract. A plugin implements `IProtocolPlugin`:
parse a link, build a link, validate a node, emit an outbound for a given engine.

The part that makes this more than a parser interface is `ProtocolDescriptor.Fields`:
a plugin declares its editable fields as `FieldSpec` records — kind, default, choices,
help text, and a `VisibleWhen` guard. The editor renders those, so a third-party
protocol gets the same UI as the built-ins without referencing WPF or shipping a view.
FCon's own transport, TLS and multiplexing sections are described with the same
vocabulary and go through the same renderer.

Shared emitters (`TransportEmitter`, `SecurityEmitter`) mean the transport matrix is
implemented exactly once rather than per protocol.

### Writing one

Build a class library referencing `FCon.Abstractions`, mark the assembly, and drop
the output in `plugins/<your-plugin>/` next to the executable:

```csharp
[assembly: FConPluginAssembly("Hysteria2", "1.0.0")]

public sealed class Hysteria2Plugin : IProtocolPlugin
{
    public ProtocolDescriptor Descriptor { get; } = new()
    {
        Id = "hysteria2",
        DisplayName = "Hysteria2",
        Schemes = ["hy2", "hysteria2"],
        Engines = EngineSupport.SingBox,
        Fields = [new FieldSpec("password", "Password", FieldKind.Secret) { Required = true }],
    };

    public bool TryParseLink(string link, out ProxyNode? node, out string? error) { ... }
    public string BuildLink(ProxyNode node) { ... }
    public IReadOnlyList<string> Validate(ProxyNode node) { ... }
    public JsonObject EmitOutbound(ProxyNode node, EngineKind engine, IEmitContext ctx) { ... }
}
```

Each package loads into its own collectible `AssemblyLoadContext`, so plugins can
carry their own dependency versions. `FCon.Abstractions` is deliberately resolved
from the host so shared types stay reference-equal. Built-ins always win a name
clash, and a package built against a different `ContractVersion` is rejected with a
reason shown in Settings rather than failing silently.

## Layout

    src/FCon.Abstractions      Plugin contract, node model, shared emitters. No dependencies.
    src/FCon.Plugins.Builtin   The ten built-in protocols.
    src/FCon.Core             Config generation, engine supervision, storage, routing, subscriptions.
    src/FCon.App              WPF UI (.NET 10, CommunityToolkit.Mvvm).
    tests/FCon.Core.Tests     xunit: supervisor state machine, config shapes, plugins, versions.
    tools/FCon.Smoke          Parse/round-trip/emit checks across every protocol, run on the real cores.

## Build and test

```
dotnet build FCon.slnx
dotnet test tests/FCon.Core.Tests                 # unit tests, no cores needed
dotnet run --project tools/FCon.Smoke             # protocol checks on the real cores
dotnet run --project tools/FCon.Smoke -- --dump   # print a full config pair
dotnet run --project tools/FCon.Smoke -- --seed   # load sample servers into the GUI
```

The unit tests drive the connect / verify / monitor / recover state machine with a
scripted probe and a fake core: verification failure with and without a group, in-core
switching, threshold handling, passive health, core death, unbounded retry and the
disconnect that ends it. They also pin the shape of the grouped sing-box and Xray
configs, the ShadowTLS helper outbound, the probe config, and the new link formats.

For each protocol the smoke tool parses a link, round-trips it back through the link
builder, and generates configs for both engines. It then **hands every config to the
real core** — single-server, the full grouped config, and the Xray balancer — and
requires each to start and stay up; valid JSON is a low bar, and this is what catches
deprecated options and schema drift between core versions. It finishes by starting a
probe core with every sample server and measuring them through the API, which proves
the real-request latency path end to end. Cores that are not installed are skipped with
a notice rather than silently passing.

It also verifies that every native symbol the app P/Invokes actually resolves;
`LibraryImport` binds lazily by exact name, so a wrong entry point would otherwise
stay invisible until the call happens at runtime.

Point it at the cores with `FCON_ENGINES_DIR=<repo>/engines` when running from the
repo, since the tool does not sit beside the app executable.

## Packaging

```
powershell -ExecutionPolicy Bypass -File build/publish.ps1
```

Produces two zips in `dist/`:

| Package | Needs .NET installed | Settings live in |
| --- | --- | --- |
| `FCon-<ver>-win-x64-portable.zip` | no — carries its own runtime | `data\` beside `FCon.exe` |
| `FCon-<ver>-win-x64.zip` | yes — .NET Desktop Runtime 10 | `%APPDATA%\FCon` |

Portable mode is switched on by the `portable.txt` file shipped in that zip; delete it
and the app reverts to `%APPDATA%`. Unzip and run — there is no installer.

The script runs the unit tests and the protocol checks first and refuses to publish if
either fails. Neither
package bundles a proxy core: both ship the `engines/` layout and its README, and the
cores stay the user's to fetch from upstream under their own licences.

If nuget.org is unreachable the script says so and falls back to the local package
cache, building framework-dependent packages only — a self-contained build needs the
runtime pack, which has to be downloaded once.

## Notes on behaviour

* **System proxy** settings are snapshotted before first use and restored on exit.
  If the app dies without cleaning up, the snapshot on disk is detected and restored
  at next startup, so you are never left pointed at a dead port. Two guards keep that
  honest: a leftover setting pointing at our own listener is never recorded as "the
  user's proxy", and a snapshot is not reapplied if the proxy it names has since
  stopped listening. A different client's loopback proxy is left alone and restored
  normally.
* **Listener ports** are checked before the core starts. 10808/10809 are the de-facto
  defaults across proxy clients, so on first run FCon moves off any port already taken,
  and a later conflict names the program holding it rather than surfacing a bind error.
* **Profile writes** are queued and serialised, and flushed on shutdown.
* **TUN mode** requires sing-box and administrator rights. System-proxy mode does not.
* **Subscription quota** is read from the `Subscription-Userinfo` response header.
  Nodes keep their id and measured latency across an update when the share link is
  unchanged, so the selected server survives a refresh.
