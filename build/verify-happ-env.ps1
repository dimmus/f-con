<#
  Checks the machine is in a state where Happ can actually connect: the daemon is up,
  nothing else owns Happ's loopback ports, the system proxy is not left pointing at a
  dead listener, DNS and direct egress work, and the local data files are sane.

  This lives in the FCon repo because FCon defaults to 10808/10809/10810 - the same
  ports Happ uses - so a dev session that gets hard-killed leaves the ports taken and
  the system proxy redirected, and Happ then spins on "Connecting" forever.

  Read-only by default.
    -Fix          clear a stale system proxy and restart the Happ service (needs admin)
    -Fix -Force   also stop proxy cores that are not Happ's own
#>
param(
    [switch]$Fix,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$HappDir    = 'C:\Program Files\FlyFrogLLC\Happ'
$HappData   = Join-Path $env:LOCALAPPDATA 'Happ'
$HappShared = 'C:\ProgramData\Happ'
$ProxyKey   = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'

# Ports FCon claims by default (src/FCon.Core/Config/AppSettings.cs). Happ's TUN
# outbound dials the first one, so a collision there is fatal to Happ.
$SiblingPorts = @(10809, 10810)

$CoreNames = @(
    'sing-box', 'xray', 'v2ray', 'mihomo', 'clash', 'clash-verge', 'Clash for Windows',
    'nekoray', 'nekobox', 'hiddify', 'Hiddify', 'tun2proxy-bin', 'tun2socks',
    'AmneziaVPN', 'warp-svc', 'FCon', 'KVN'
)

$Checks = New-Object System.Collections.Generic.List[object]

function Add-Check {
    param(
        [ValidateSet('PASS', 'WARN', 'FAIL')][string]$State,
        [string]$Name,
        [string]$Detail = '',
        [string]$Remedy = ''
    )
    $Checks.Add([pscustomobject]@{ State = $State; Name = $Name; Detail = $Detail; Remedy = $Remedy })
    $colour = 'Green'
    if ($State -eq 'WARN') { $colour = 'Yellow' }
    if ($State -eq 'FAIL') { $colour = 'Red' }
    Write-Host ('  [{0}] {1}' -f $State.PadRight(4), $Name) -ForegroundColor $colour
    if ($Detail) { Write-Host ('         {0}' -f $Detail) -ForegroundColor DarkGray }
}

function Section([string]$Title) {
    Write-Host ''
    Write-Host $Title -ForegroundColor Cyan
}

function Get-PortOwner([int]$Port) {
    $conn = Get-NetTCPConnection -State Listen -LocalPort $Port -EA SilentlyContinue | Select-Object -First 1
    if (-not $conn) { return $null }
    $proc = Get-Process -Id $conn.OwningProcess -EA SilentlyContinue
    $name = '(gone)'
    $path = $null
    if ($proc) { $name = $proc.ProcessName; $path = $proc.Path }
    [pscustomobject]@{ Port = $Port; ProcessId = $conn.OwningProcess; Name = $name; Path = $path }
}

function Test-Tcp {
    param([string]$Target, [int]$Port, [int]$TimeoutMs = 3000)
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $async = $client.BeginConnect($Target, $Port, $null, $null)
        if (-not $async.AsyncWaitHandle.WaitOne($TimeoutMs)) { return $false }
        $client.EndConnect($async)
        return $true
    }
    catch { return $false }
    finally { $client.Close() }
}

function Test-IsHapps([string]$Path) {
    if (-not $Path) { return $false }
    return $Path.StartsWith($HappDir, [StringComparison]::OrdinalIgnoreCase)
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

Write-Host ''
Write-Host 'Happ environment check' -ForegroundColor White
Write-Host ('  elevated: {0}   session: {1}' -f $isAdmin, (Get-Process -Id $PID).SessionId) -ForegroundColor DarkGray

# ---------------------------------------------------------------- install, daemon
Section 'Install and daemon'

$happExe = Join-Path $HappDir 'Happ.exe'
if (Test-Path $happExe) {
    $ver = (Get-Item $happExe).VersionInfo.ProductVersion
    if (-not $ver) { $ver = 'unknown' }
    Add-Check PASS 'Happ installed' "$happExe (version $ver)"
}
else {
    Add-Check FAIL 'Happ installed' "not found at $HappDir" 'Reinstall Happ.'
}

$svc = Get-Service HappService -EA SilentlyContinue
if (-not $svc) {
    Add-Check FAIL 'HappService present' 'service is not registered' 'Reinstall Happ - the client cannot tunnel without its service.'
}
elseif ($svc.Status -ne 'Running') {
    Add-Check FAIL 'HappService running' "status is $($svc.Status)" 'Start-Service HappService   (elevated)'
}
else {
    Add-Check PASS 'HappService running' "status Running, start type $($svc.StartType)"
}

$happd = Get-Process happd -EA SilentlyContinue
if ($happd) {
    Add-Check PASS 'happd.exe alive' "pid $($happd.Id) - $($happd.Path)"
}
else {
    Add-Check FAIL 'happd.exe alive' 'the daemon process is not running' 'Restart-Service HappService   (elevated)'
}

# The GUI talks to the daemon over this pipe. No pipe, no connection, however long you wait.
$sessionId = (Get-Process -Id $PID).SessionId
$pipeName = "happd-$sessionId"
$pipes = @()
try { $pipes = [System.IO.Directory]::GetFiles('\\.\pipe\') } catch { }
if ($pipes -match [regex]::Escape($pipeName)) {
    Add-Check PASS 'Daemon pipe open' "\\.\pipe\$pipeName"
}
else {
    Add-Check FAIL 'Daemon pipe open' "no \\.\pipe\$pipeName for this session" 'Restart-Service HappService, then relaunch Happ.'
}

# ------------------------------------------------------------------------- ports
Section 'Loopback ports'

# The port Happ's TUN outbound dials, read from its own generated config.
$tunPort = 10808
$configPath = Join-Path $HappData 'config.json'
if (Test-Path $configPath) {
    try {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        $socks = $cfg.outbounds | Where-Object { $_.type -eq 'socks' } | Select-Object -First 1
        if ($socks -and $socks.server_port) { $tunPort = [int]$socks.server_port }
    }
    catch {
        Add-Check WARN 'config.json readable' "could not parse ${configPath}: $($_.Exception.Message)"
    }
}
else {
    Add-Check PASS 'Tunnel port source' "no config.json yet - assuming Happ's default $tunPort"
}

$blockers = @()

$owner = Get-PortOwner $tunPort
if (-not $owner) {
    Add-Check PASS "Port $tunPort free" 'nothing is listening - Happ can bind its core here'
}
elseif (Test-IsHapps $owner.Path) {
    Add-Check PASS "Port $tunPort held by Happ" "$($owner.Name) pid $($owner.ProcessId) - Happ is connected"
}
else {
    $blockers += $owner
    Add-Check FAIL "Port $tunPort taken" "$($owner.Name) pid $($owner.ProcessId) - $($owner.Path)" `
        "Stop that process. Happ's TUN outbound dials 127.0.0.1:$tunPort and its core cannot bind it."
}

foreach ($port in $SiblingPorts) {
    $owner = Get-PortOwner $port
    if (-not $owner) {
        Add-Check PASS "Port $port free"
    }
    elseif (Test-IsHapps $owner.Path) {
        Add-Check PASS "Port $port held by Happ" "$($owner.Name) pid $($owner.ProcessId)"
    }
    else {
        $blockers += $owner
        Add-Check WARN "Port $port taken" "$($owner.Name) pid $($owner.ProcessId) - $($owner.Path)"
    }
}

# ------------------------------------------------------------- competing clients
Section 'Competing proxy clients'

$foreign = @()
foreach ($proc in (Get-Process -EA SilentlyContinue | Where-Object { $CoreNames -contains $_.ProcessName })) {
    $path = $null
    try { $path = $proc.Path } catch { }
    if (-not (Test-IsHapps $path)) {
        $foreign += [pscustomobject]@{ Name = $proc.ProcessName; ProcessId = $proc.Id; Path = $path }
    }
}

if ($foreign.Count -eq 0) {
    Add-Check PASS 'No rival cores running' 'nothing outside Happ is running a proxy core'
}
else {
    foreach ($f in $foreign) {
        $detail = "$($f.Name) pid $($f.ProcessId) - $($f.Path)"
        if ($f.Name -eq 'FCon') {
            Add-Check FAIL 'FCon is running' $detail 'Quit FCon - it claims 10808/10809/10810 by default.'
        }
        else {
            Add-Check WARN 'Rival proxy core' $detail 'Stop it if Happ will not connect.'
        }
    }
}

# ------------------------------------------------------------------ system proxy
Section 'System proxy'

$proxy = Get-ItemProperty $ProxyKey -EA SilentlyContinue
$proxyEnabled = $false
$proxyServer = ''
if ($proxy) {
    if ($proxy.ProxyEnable -eq 1) { $proxyEnabled = $true }
    if ($proxy.ProxyServer) { $proxyServer = [string]$proxy.ProxyServer }
}

$proxyStale = $false
if (-not $proxyEnabled) {
    Add-Check PASS 'System proxy off' 'Happ can reach its API and subscriptions directly'
}
else {
    $proxyPort = 0
    if ($proxyServer -match ':(\d+)\s*$') { $proxyPort = [int]$Matches[1] }
    $proxyOwner = $null
    if ($proxyPort -gt 0) { $proxyOwner = Get-PortOwner $proxyPort }

    if ($proxyOwner -and (Test-IsHapps $proxyOwner.Path)) {
        Add-Check PASS 'System proxy is Happ' "$proxyServer -> $($proxyOwner.Name) pid $($proxyOwner.ProcessId)"
    }
    elseif ($proxyOwner) {
        $proxyStale = $true
        Add-Check FAIL 'System proxy hijacked' "$proxyServer -> $($proxyOwner.Name) pid $($proxyOwner.ProcessId), not Happ" `
            'Happ subscription and push traffic is going through another client. Clear it (-Fix).'
    }
    else {
        $proxyStale = $true
        Add-Check FAIL 'System proxy is dead' "$proxyServer is set but nothing is listening there" `
            'Left behind by a client that was killed. Clear it (-Fix).'
    }
}

if ($proxy -and $proxy.AutoConfigURL) {
    Add-Check WARN 'Proxy auto-config set' "AutoConfigURL = $($proxy.AutoConfigURL)"
}

$backup = Get-ItemProperty 'HKCU:\Software\Happ\OrganizationDefaults\ProxyBackup' -EA SilentlyContinue
if ($backup -and $backup.backup -eq '@Invalid()') {
    Add-Check WARN 'Happ proxy backup empty' 'ProxyBackup is @Invalid() - Happ has nothing to restore on disconnect'
}

# -------------------------------------------------------------------- interfaces
Section 'Network interfaces'

$tun = Get-NetAdapter -Name 'happ-tun' -EA SilentlyContinue
$happCore = Get-Process xray, sing-box -EA SilentlyContinue | Where-Object { Test-IsHapps $_.Path }
if (-not $tun) {
    Add-Check PASS 'No stale happ-tun' 'the tunnel adapter is created on connect'
}
elseif ($happCore) {
    Add-Check PASS 'happ-tun up' "status $($tun.Status) with a live Happ core"
}
else {
    Add-Check WARN 'Stale happ-tun adapter' "status $($tun.Status) but no Happ core is running" `
        'Restart-Service HappService - the daemon removes orphaned TUN interfaces on start.'
}

$otherTun = Get-NetAdapter -EA SilentlyContinue |
    Where-Object { $_.InterfaceDescription -match 'Wintun|WireGuard|TAP-Windows|OpenVPN' -and $_.Name -ne 'happ-tun' }
foreach ($adapter in $otherTun) {
    Add-Check WARN 'Other tunnel adapter' "$($adapter.Name) - $($adapter.InterfaceDescription) ($($adapter.Status))"
}

# ---------------------------------------------------------------- dns and egress
Section 'DNS and direct egress'

foreach ($name in @('mtalk.google.com', 'raw.githubusercontent.com')) {
    try {
        $answer = Resolve-DnsName $name -Type A -EA Stop | Where-Object { $_.IPAddress } | Select-Object -First 1
        if ($answer) { Add-Check PASS "DNS $name" "resolves to $($answer.IPAddress)" }
        else { Add-Check FAIL "DNS $name" 'no A record returned' 'Check the DNS servers on your active adapter.' }
    }
    catch {
        Add-Check FAIL "DNS $name" $_.Exception.Message 'Resolution is failing - Happ cannot fetch subscriptions or push.'
    }
}

# Raw TCP, so this ignores the system proxy and shows what the machine reaches unaided.
$egress = @(
    @{ Target = '1.1.1.1'; Port = 443; Label = 'baseline internet' },
    @{ Target = 'mtalk.google.com'; Port = 5228; Label = 'Happ push channel' },
    @{ Target = 'raw.githubusercontent.com'; Port = 443; Label = 'subscription source' }
)
$reached = 0
foreach ($probe in $egress) {
    if (Test-Tcp $probe.Target $probe.Port) {
        $reached++
        Add-Check PASS ('Reachable {0}:{1}' -f $probe.Target, $probe.Port) $probe.Label
    }
    else {
        Add-Check WARN ('Unreachable {0}:{1}' -f $probe.Target, $probe.Port) "$($probe.Label) - blocked or filtered without a tunnel"
    }
}
if ($reached -eq 0) {
    Add-Check FAIL 'No direct egress' 'every probe failed - the machine has no working internet path' 'Fix connectivity before blaming Happ.'
}

# --------------------------------------------------------------------- data files
Section 'Data files'

$subsDb = Join-Path $HappData 'subs.db'
if (Test-Path $subsDb) {
    $mb = [math]::Round((Get-Item $subsDb).Length / 1MB, 1)
    $header = ''
    try {
        $bytes = New-Object byte[] 15
        $stream = [System.IO.File]::OpenRead($subsDb)
        try { $null = $stream.Read($bytes, 0, 15) } finally { $stream.Dispose() }
        $header = [System.Text.Encoding]::ASCII.GetString($bytes)
    }
    catch { }

    if ($header -ne 'SQLite format ') {
        Add-Check FAIL 'subs.db intact' 'not a SQLite file - the server database is corrupt' `
            'Reset with ResetSettingsHapp.bat in the install folder.'
    }
    elseif ($mb -gt 150) {
        Add-Check WARN 'subs.db size' "$mb MB - bloated, ping history and subscription blobs pile up" 'Reset if the UI feels slow.'
    }
    else {
        Add-Check PASS 'subs.db intact' "SQLite, $mb MB"
    }
}
else {
    Add-Check WARN 'subs.db present' 'no server database yet - Happ will create one on first run'
}

$stateDb = Join-Path $HappShared 'state.db'
if (Test-Path $stateDb) {
    Add-Check PASS 'state.db present' "$stateDb, $([math]::Round((Get-Item $stateDb).Length / 1KB)) KB"
}
else {
    Add-Check WARN 'state.db present' 'daemon state missing - it will be recreated'
}

$free = [math]::Round((Get-PSDrive C).Free / 1GB, 1)
if ($free -lt 2) {
    Add-Check FAIL 'Disk space' "$free GB free on C:" 'Free space - the daemon cannot write logs or geo data.'
}
else {
    Add-Check PASS 'Disk space' "$free GB free on C:"
}

# --------------------------------------------------------------------------- logs
Section 'Recent log evidence'

$daemonLog = Join-Path $HappShared 'logs\happd.log'
if (Test-Path $daemonLog) {
    $tail = Get-Content $daemonLog -Tail 400 -EA SilentlyContinue
    # MCS host-not-found at service start is routine: the daemon races the network stack.
    $errors = $tail | Where-Object { $_ -match 'ERROR|CRITICAL|FATAL' -and $_ -notmatch 'MCS socket error' }
    if ($errors) {
        Add-Check WARN 'Daemon log clean' "$($errors.Count) error line(s) in the last 400"
        foreach ($line in ($errors | Select-Object -Last 3)) { Write-Host "         $line" -ForegroundColor DarkGray }
    }
    else {
        Add-Check PASS 'Daemon log clean' 'no errors in the last 400 lines'
    }
}
else {
    Add-Check WARN 'Daemon log present' "not found at $daemonLog"
}

$subLog = Join-Path $HappData 'logs\subscription_log.txt'
if (Test-Path $subLog) {
    $tail = Get-Content $subLog -Tail 200 -EA SilentlyContinue
    # "code: 0" means the HTTP request never completed - almost always a broken proxy.
    $dead = $tail | Where-Object { $_ -match 'code:\s*0,' }
    if ($dead) {
        Add-Check WARN 'Subscription fetches' "$($dead.Count) failed request(s) with code 0 in the last 200 lines" `
            'Happ HTTP is not getting out - check the system proxy result above.'
    }
    else {
        Add-Check PASS 'Subscription fetches' 'no failed requests in the recent log'
    }
}

# --------------------------------------------------------------------------- fixes
$fails = @($Checks | Where-Object { $_.State -eq 'FAIL' })
$warns = @($Checks | Where-Object { $_.State -eq 'WARN' })

if ($Fix) {
    Section 'Applying fixes'

    if (-not $isAdmin) {
        Write-Host '  -Fix needs an elevated shell for the service calls; doing what it can.' -ForegroundColor Yellow
    }

    if ($proxyStale) {
        Set-ItemProperty $ProxyKey ProxyEnable 0
        Remove-ItemProperty $ProxyKey ProxyServer -EA SilentlyContinue
        Write-Host '  cleared the system proxy' -ForegroundColor Green
    }

    $kill = @()
    foreach ($b in $blockers) { $kill += $b.ProcessId }
    foreach ($f in $foreign) { $kill += $f.ProcessId }
    $kill = @($kill | Sort-Object -Unique)
    if ($kill.Count -gt 0) {
        if ($Force) {
            foreach ($id in $kill) {
                Stop-Process -Id $id -Force -EA SilentlyContinue
                Write-Host "  stopped pid $id" -ForegroundColor Green
            }
        }
        else {
            Write-Host ('  not stopping pid(s) {0} - rerun with -Force to kill them' -f ($kill -join ', ')) -ForegroundColor Yellow
        }
    }

    if ($isAdmin -and (Get-Service HappService -EA SilentlyContinue)) {
        Restart-Service HappService -EA SilentlyContinue
        Write-Host '  restarted HappService' -ForegroundColor Green
    }

    Write-Host ''
    Write-Host '  Rerun without -Fix to confirm, then start Happ.' -ForegroundColor White
}

# ------------------------------------------------------------------------ summary
Write-Host ''
if ($fails.Count -eq 0 -and $warns.Count -eq 0) {
    Write-Output 'RESULT: PASS - environment is clean, Happ should connect'
    exit 0
}
if ($fails.Count -eq 0) {
    Write-Output "RESULT: PASS - $($warns.Count) warning(s), nothing that blocks Happ"
    exit 0
}

Write-Output "RESULT: FAIL - $($fails.Count) blocking problem(s), $($warns.Count) warning(s)"
Write-Host ''
Write-Host 'What to do:' -ForegroundColor White
foreach ($f in $fails) {
    Write-Host ('  - {0}: {1}' -f $f.Name, $f.Detail) -ForegroundColor Red
    if ($f.Remedy) { Write-Host ('    {0}' -f $f.Remedy) -ForegroundColor DarkGray }
}
if (-not $Fix) {
    Write-Host ''
    Write-Host '  Most of this is fixable with:  .\build\verify-happ-env.ps1 -Fix' -ForegroundColor White
}
exit 1
