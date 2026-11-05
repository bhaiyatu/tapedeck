# Dev helper: run JavaScript inside Tapedeck's hidden player page (app must run with TAPEDECK_DEBUG=1).
# Usage: .\player-eval.ps1 -File script.js   or   .\player-eval.ps1 -Code "document.title"
param([string]$File, [string]$Code, [int]$TimeoutSeconds = 20)

$dir = Join-Path $env:LOCALAPPDATA "Tapedeck"
$out = Join-Path $dir "eval.out"
if (Test-Path $out) { Remove-Item $out }
$js = if ($File) { Get-Content $File -Raw } else { $Code }
[System.IO.File]::WriteAllText((Join-Path $dir "eval.js"), $js)
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while (-not (Test-Path $out) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
if (Test-Path $out) { Get-Content $out -Raw } else { "TIMEOUT (is the app running with TAPEDECK_DEBUG=1?)" }
