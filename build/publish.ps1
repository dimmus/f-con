<#
.SYNOPSIS
    Builds the KVN release and portable packages.

.DESCRIPTION
    Produces two win-x64 packages under dist/:

      KVN-<version>-win-x64-portable.zip
          Self-contained single file. No .NET install needed. Keeps its settings in
          a data/ folder next to the executable, so it leaves nothing behind.

      KVN-<version>-win-x64.zip
          Framework-dependent. Much smaller, needs the .NET Desktop Runtime.
          Settings live in %APPDATA%\KVN.

    Proxy cores are never bundled: they are separate projects under their own
    licences, and users should get them from upstream. Both packages ship the
    engines/ folder layout and a README explaining where to put them.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SkipTests,
    # Restore from the local package cache only. Set automatically when nuget.org
    # cannot be reached, which is worth surviving for an app people install to get
    # around exactly that kind of block.
    [switch]$Offline,
    # Route the restore through an HTTP proxy, e.g. http://127.0.0.1:10809.
    # Left empty, a running local proxy is detected automatically.
    [string]$Proxy = ''
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$app = Join-Path $repo 'src/FCon.App/FCon.App.csproj'
$dist = Join-Path $repo 'dist'
$staging = Join-Path $dist 'staging'

$version = ([xml](Get-Content $app)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = '1.0.0' }

Write-Host "KVN $version -> $Runtime ($Configuration)" -ForegroundColor Cyan

# Only a KVN launched from this repo locks files we are about to write. An installed
# copy running elsewhere must keep running: on a machine where nuget.org is blocked, its
# proxy is how the restore reaches the runtime pack at all, so stopping it here would
# guarantee the downgraded build this check was never meant to cause.
$blocking = Get-Process KVN -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase) }

if ($blocking) {
    throw "KVN is running from $($blocking[0].Path); close it before publishing (it locks its own binaries)."
}

# A previous package may have been run in place, leaving a locked runtime file
# behind. Retry briefly, then fall back to a fresh directory rather than failing.
if (Test-Path $staging) {
    for ($try = 0; $try -lt 3 -and (Test-Path $staging); $try++) {
        try { Remove-Item $staging -Recurse -Force -ErrorAction Stop }
        catch { Start-Sleep -Milliseconds 700 }
    }
    if (Test-Path $staging) {
        Write-Host 'Staging is locked; using a fresh directory.' -ForegroundColor Yellow
        $staging = Join-Path $dist ('staging-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    }
}
New-Item -ItemType Directory -Path $dist -Force | Out-Null

# ------------------------------------------------------- restore strategy

# Can we reach nuget.org, directly or through a proxy? Returns $true on success and
# leaves $env:HTTPS_PROXY set when a proxy was needed - NuGet reads those variables,
# so every later 'dotnet' call inherits the route.
function Test-NuGetReachable {
    param([string]$Via)

    $params = @{
        Uri             = 'https://api.nuget.org/v3/index.json'
        TimeoutSec      = 10
        UseBasicParsing = $true
    }
    if ($Via) { $params.Proxy = $Via }

    try { Invoke-WebRequest @params | Out-Null; return $true }
    catch { return $false }
}

# Local HTTP proxies worth trying, most specific first: whatever KVN itself is
# configured to listen on, then the defaults other clients use. Building a
# circumvention tool on a machine that needs one is the normal case here, not an
# edge case, so this is the difference between a real package and a downgraded one.
function Get-ProxyCandidates {
    $ports = [System.Collections.Generic.List[int]]::new()

    $settingsFiles = @(
        (Join-Path $env:APPDATA 'KVN/settings.json')
        (Join-Path $env:APPDATA 'FCon/settings.json')
        (Join-Path $env:USERPROFILE 'Documents/KVN-*-win-x64-portable/data/settings.json')
        (Join-Path $env:USERPROFILE 'Documents/FCon-*-win-x64-portable/data/settings.json')
    )
    foreach ($pattern in $settingsFiles) {
        foreach ($file in (Get-ChildItem $pattern -ErrorAction SilentlyContinue)) {
            try {
                $port = (Get-Content $file.FullName -Raw | ConvertFrom-Json).HttpPort
                if ($port -and -not $ports.Contains([int]$port)) { $ports.Add([int]$port) }
            }
            catch { }   # A settings file we cannot read is not a reason to stop.
        }
    }

    foreach ($fallback in 10809, 10811, 2081, 8080) {
        if (-not $ports.Contains($fallback)) { $ports.Add($fallback) }
    }

    # Only offer ports something is actually listening on, so we do not spend the
    # full timeout on each of four dead addresses.
    $listening = (Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue).LocalPort
    $ports | Where-Object { $_ -in $listening } | ForEach-Object { "http://127.0.0.1:$_" }
}

if (-not $Offline) {
    if ($Proxy) {
        if (Test-NuGetReachable -Via $Proxy) {
            $env:HTTPS_PROXY = $Proxy
            $env:HTTP_PROXY = $Proxy
            Write-Host "nuget.org reachable via $Proxy." -ForegroundColor Green
        }
        else {
            throw "nuget.org is not reachable through $Proxy."
        }
    }
    elseif (Test-NuGetReachable) {
        # Direct route works; nothing to configure.
    }
    else {
        $candidates = @(Get-ProxyCandidates)
        Write-Host 'nuget.org is not reachable directly.' -ForegroundColor Yellow

        $found = $candidates | Where-Object { Test-NuGetReachable -Via $_ } | Select-Object -First 1
        if ($found) {
            $env:HTTPS_PROXY = $found
            $env:HTTP_PROXY = $found
            Write-Host "Using the local proxy at $found." -ForegroundColor Green
        }
        else {
            if ($candidates) {
                Write-Host "No local proxy worked (tried: $($candidates -join ', '))." -ForegroundColor Yellow
            }
            else {
                Write-Host 'No local proxy is listening. Connect KVN or Happ first,' -ForegroundColor Yellow
                Write-Host 'or pass -Proxy http://127.0.0.1:<port>.' -ForegroundColor Yellow
            }
            Write-Host 'Falling back to the local package cache.' -ForegroundColor Yellow
            $Offline = $true
        }
    }
}

$restoreArgs = @()
$extraArgs = @()
$canSelfContain = $true

if ($Offline) {
    $restoreArgs += @('--configfile', (Join-Path $PSScriptRoot 'nuget.offline.config'))

    # A self-contained build needs the runtime pack plus Crossgen2, ILLink and a matching
    # apphost. Those are rarely all in the cache, and a half-built runtime is worse than
    # no package at all, so offline skips the portable package entirely.
    $canSelfContain = $false
    $extraArgs += '-p:PublishReadyToRun=false'

    Write-Host ''
    Write-Host 'Offline: the portable package will be SKIPPED.' -ForegroundColor Yellow
    Write-Host 'It needs the runtime pack from nuget.org once; re-run with a route for it.' -ForegroundColor Yellow
    Write-Host ''
}

# ------------------------------------------------------------------ tests

if (-not $SkipTests) {
    Write-Host 'Running unit tests...' -ForegroundColor Cyan
    $unitArgs = @('test', (Join-Path $repo 'tests/FCon.Core.Tests/FCon.Core.Tests.csproj'),
                  '-v', 'q', '--nologo') + $restoreArgs
    & dotnet @unitArgs
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed; not publishing.' }

    Write-Host 'Running protocol checks...' -ForegroundColor Cyan
    $env:FCON_ENGINES_DIR = Join-Path $repo 'engines'
    $testArgs = @('run', '--project', (Join-Path $repo 'tools/FCon.Smoke/FCon.Smoke.csproj'),
                  '-v', 'q', '--nologo') + $restoreArgs
    & dotnet @testArgs
    if ($LASTEXITCODE -ne 0) { throw 'Protocol checks failed; not publishing.' }
}

# --------------------------------------------------------------- packaging

function New-Package {
    param(
        [string]$Name,
        [bool]$SelfContained,
        [bool]$Portable
    )

    $out = Join-Path $staging $Name
    Write-Host "Publishing $Name..." -ForegroundColor Cyan
    $selfContained = $SelfContained

    $flag = if ($selfContained) { 'true' } else { 'false' }

    # PublishSingleFile plus a RuntimeIdentifier bundles the runtime no matter what the
    # self-contained switch says - neither SelfContained nor PublishSelfContained
    # overrides it. That is how the small package came out a 130 MB self-contained exe
    # wearing the framework-dependent name. Single file is the portable package's whole
    # point, so keep it there and drop it here: the small package ships the ordinary
    # apphost-plus-dlls layout, which is what "needs the .NET runtime" should look like.
    $singleFile = if ($selfContained) { @() } else { @('-p:PublishSingleFile=false') }

    $publishArgs = @(
        'publish', $app,
        '-c', $Configuration,
        '-r', $Runtime,
        '--self-contained', $flag,
        '-o', $out,
        '--nologo',
        '-v', 'quiet'
    ) + $singleFile + $restoreArgs + $extraArgs

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $Name" }

    # Never ship runtime state. A portable package that was ever launched in place
    # writes a data/ folder next to the exe, complete with the core's cache database.
    $runtime = Join-Path $out 'data'
    if (Test-Path $runtime) { Remove-Item $runtime -Recurse -Force -ErrorAction SilentlyContinue }

    # The build mirrors the developer's engines/ folder into the output. Rebuild it from
    # nothing rather than filtering: the cores, their geo data and their licences all
    # belong to upstream projects and are the user's to fetch, not ours to redistribute.
    $engines = Join-Path $out 'engines'
    if (Test-Path $engines) { Remove-Item $engines -Recurse -Force }
    New-Item -ItemType Directory -Path (Join-Path $engines 'sing-box') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $engines 'xray') -Force | Out-Null
    Copy-Item (Join-Path $repo 'engines/README.md') $engines -Force

    Copy-Item (Join-Path $repo 'README.md') $out -Force

    if ($Portable) {
        Set-Content -Path (Join-Path $out 'portable.txt') -Encoding UTF8 -Value @(
            'This file puts KVN in portable mode.'
            ''
            'Settings, logs and profiles are kept in the data\ folder beside KVN.exe'
            'instead of %APPDATA%\KVN. Delete this file to use the normal location.'
            ''
            'This build carries its own .NET runtime; nothing needs installing.'
        )
    }

    # Publish leaves build leftovers that do not belong in a shipped package.
    Get-ChildItem $out -Include '*.pdb', '*.xml' -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue

    $zip = Join-Path $dist "$Name.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal

    $size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host "  $zip ($size MB)" -ForegroundColor Green
}

# The portable package's whole promise is that it needs nothing installed. Built
# framework-dependent it is a different product wearing the same name - and it came
# out byte-for-byte the size of the other zip, which is how the downgrade went
# unnoticed. Skip it instead: a missing package is honest, a mislabelled one is not.
if ($canSelfContain) {
    New-Package -Name "KVN-$version-$Runtime-portable" -SelfContained $true -Portable $true
}
else {
    $stale = Join-Path $dist "KVN-$version-$Runtime-portable.zip"
    if (Test-Path $stale) { Remove-Item $stale -Force }
    Write-Host '  portable package skipped (needs nuget.org).' -ForegroundColor Yellow
}

New-Package -Name "KVN-$version-$Runtime" -SelfContained $false -Portable $false

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
Get-ChildItem $dist -Filter '*.zip' |
    Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } |
    Format-Table -AutoSize
