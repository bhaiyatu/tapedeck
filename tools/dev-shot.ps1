# Dev helper: send a command to a debug-mode Tapedeck (TAPEDECK_DEBUG=1), wait, then capture the window.
# Usage: .\dev-shot.ps1 -Command "search daft punk" -Out shot.png [-Wait 4]
param([string]$Command, [Parameter(Mandatory)][string]$Out, [double]$Wait = 4)

if ($Command) {
    [System.IO.File]::WriteAllText((Join-Path $env:LOCALAPPDATA "Tapedeck\command.txt"), $Command)
    Start-Sleep -Milliseconds ([int]($Wait * 1000))
}
& (Join-Path $PSScriptRoot "capture-window.ps1") -ProcessName Tapedeck -OutFile $Out
