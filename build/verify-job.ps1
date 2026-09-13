<#
  Proves the kill-on-close job object: run a harness that starts a child under the job,
  then TerminateProcess the harness and confirm the child went with it.
#>
param([string]$Repo = "$PSScriptRoot/..")
$ErrorActionPreference = 'Stop'

$proj = Join-Path $Repo 'tools/FCon.Smoke/FCon.Smoke.csproj'
$exe = Join-Path $Repo 'tools/FCon.Smoke/bin/Debug/net10.0-windows/FCon.Smoke.exe'
if (-not (Test-Path $exe)) { throw "harness not built: $exe" }

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.Arguments = '--job-hold'
$psi.RedirectStandardOutput = $true
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true

$p = [System.Diagnostics.Process]::Start($psi)

$childPid = $null; $active = $null; $assigned = $null
while (-not $p.StandardOutput.EndOfStream) {
    $line = $p.StandardOutput.ReadLine()
    Write-Output "  harness: $line"
    if ($line -like 'job-active=*') { $active = $line.Split('=')[1] }
    if ($line -like 'child-pid=*') { $childPid = [int]$line.Split('=')[1] }
    if ($line -like 'assigned=*') { $assigned = $line.Split('=')[1]; break }
}

if (-not $childPid) { Write-Output 'RESULT: FAIL - harness never reported a child'; exit 1 }

$alive = $null -ne (Get-Process -Id $childPid -EA SilentlyContinue)
Write-Output "child $childPid alive before kill: $alive"

# The kill a crash or Task Manager would do: no cleanup code of ours runs.
Stop-Process -Id $p.Id -Force
Start-Sleep -Seconds 3

$survived = $null -ne (Get-Process -Id $childPid -EA SilentlyContinue)
Write-Output "child $childPid alive after kill:  $survived"

if ($active -ne 'True' -or $assigned -ne 'True') {
    Write-Output 'RESULT: FAIL - job was not active or the child was not assigned'
    if ($survived) { Stop-Process -Id $childPid -Force -EA SilentlyContinue }
    exit 1
}
if ($survived) {
    Stop-Process -Id $childPid -Force -EA SilentlyContinue
    Write-Output 'RESULT: FAIL - child outlived the killed parent'
    exit 1
}
Write-Output 'RESULT: PASS - child was terminated with the parent'
