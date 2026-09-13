<#
  Proves the job object works: start FCon, get it to launch a core, then kill FCon the
  hard way (the same TerminateProcess a crash or Task Manager would do) and check the
  core died with it instead of being left behind.
#>
param([string]$Exe)
$ErrorActionPreference = 'Stop'

function Cores { Get-Process sing-box, xray -EA SilentlyContinue }

$before = @(Cores | Select-Object -ExpandProperty Id)
Write-Output "cores before: $($before -join ', ')"

Start-Process $Exe
Start-Sleep -Seconds 6
$app = Get-Process FCon -EA SilentlyContinue
if (-not $app) { Write-Output 'RESULT: app did not start'; exit 1 }
Write-Output "FCon pid $($app.Id)"

# Connecting needs a server; the smoke seed leaves fake ones that still start a core.
Start-Sleep -Seconds 10
$during = @(Cores | Select-Object -ExpandProperty Id | Where-Object { $before -notcontains $_ })
Write-Output "cores started by this run: $(if ($during) { $during -join ', ' } else { '(none)' })"

Stop-Process -Id $app.Id -Force
Start-Sleep -Seconds 4

$after = @(Cores | Select-Object -ExpandProperty Id)
$leaked = @($during | Where-Object { $after -contains $_ })

if (-not $during) {
    Write-Output 'RESULT: INCONCLUSIVE - no core was started, nothing to orphan'
}
elseif ($leaked) {
    Write-Output "RESULT: FAIL - orphaned cores survived: $($leaked -join ', ')"
}
else {
    Write-Output 'RESULT: PASS - every core started by this run died with the app'
}
