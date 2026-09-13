# FCon

A Windows desktop VPN client with the full protocol surface that 3x-ui provisions,
implemented as plugins over two interchangeable proxy cores.

Everything outside the protocol layer — the UI, routing, subscriptions, storage,
process supervision — is FCon's own design.

## Status

| Area | State |
| --- | --- |
| Protocol plugins (7) | Implemented, round-trip tested |
| sing-box config generation | Every protocol starts the real core |
| Xray config generation | Every protocol starts the real core |
| GUI (servers, subscriptions, routing, log, settings) | Implemented |
| Engine supervision, system proxy, tray, single instance | Implemented |
| Release and portable packaging | `build/publish.ps1` |
| End-to-end tunnel through a real server | **Not yet verified** — needs a live server |

## Getting a proxy core

FCon does not bundle the cores and does not download them for you. Install one:

1. Download the Windows amd64 archive from the upstream releases page
   (Settings → **Get sing-box** / **Get Xray** opens it).
2. Unzip and copy the executable into the repo-root `engines/` folder:

       engines/sing-box/sing-box.exe
       engines/xray/xray.exe

3. Build. `engines/` is mirrored into the output directory, which is where the app
   actually reads from (`<app>/engines/...`).
4. Press **Re-check engines**. Settings reports the version it found.

Lookup order: `%FCON_ENGINES_DIR%` if set, then `engines/` beside the executable,
then the executable's own directory, then `PATH`. Set `FCON_ENGINES_DIR` for a
portable install that keeps its binaries elsewhere.

Use a **stable** release. The generator targets the sing-box 1.11+ stable schema;
alpha builds move options around and have already removed some.

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

Transports: TCP/raw (with HTTP header obfuscation), mKCP, WebSocket, HTTP/2, QUIC,
gRPC, HTTPUpgrade, XHTTP.
Security: none, TLS (uTLS fingerprints, ALPN, pinned certs), REALITY.
Plus multiplexing, and early-data handling on WebSocket paths.

**Engine gaps are surfaced, not hidden.** mKCP, XHTTP and raw HTTP obfuscation have
no sing-box equivalent; selecting them with sing-box produces a warning in the log
telling you to switch that server to Xray. The same applies in reverse for SIP003
Shadowsocks plugins, which only sing-box runs.

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
    src/FCon.Plugins.Builtin   The seven built-in protocols.
    src/FCon.Core             Config generation, engine supervision, storage, routing, subscriptions.
    src/FCon.App              WPF UI (.NET 10, CommunityToolkit.Mvvm).
    tools/FCon.Smoke          Parse/round-trip/emit checks across every protocol.

## Build and test

```
dotnet build FCon.slnx
dotnet run --project tools/FCon.Smoke             # protocol checks
dotnet run --project tools/FCon.Smoke -- --dump   # print a full config pair
dotnet run --project tools/FCon.Smoke -- --seed   # load sample servers into the GUI
```

For each protocol the smoke tool parses a link, round-trips it back through the link
builder, and generates configs for both engines. It then **hands every config to the
real core** (`sing-box check`, `xray run -test`) — valid JSON is a low bar, and this
is what catches deprecated options and schema drift between core versions. Cores that
are not installed are skipped with a notice rather than silently passing.

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

The script runs the protocol checks first and refuses to publish if they fail. Neither
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
