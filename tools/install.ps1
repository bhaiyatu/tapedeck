# Builds a Release copy of Tapedeck into %LOCALAPPDATA%\Programs\Tapedeck and adds a Start menu shortcut.
#   .\tools\install.ps1                    install / update
#   .\tools\install.ps1 -Uninstall         remove the app and shortcut (keeps settings and sign-in)
#   .\tools\install.ps1 -Uninstall -Purge  also delete %LOCALAPPDATA%\Tapedeck: settings, logs and the
#                                          WebView2 profile holding your Google session
param([switch]$Uninstall, [switch]$Purge)

$ErrorActionPreference = "Stop"
$dest = Join-Path $env:LOCALAPPDATA "Programs\Tapedeck"
$data = Join-Path $env:LOCALAPPDATA "Tapedeck"
$shortcut = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Tapedeck.lnk"

Get-Process Tapedeck -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500  # let WebView2 release the profile

if ($Uninstall) {
    if (Test-Path $shortcut) { Remove-Item $shortcut }
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    if ($Purge -and (Test-Path $data)) { Remove-Item $data -Recurse -Force; Write-Output "Deleted $data (settings and sign-in)." }
    Write-Output "Tapedeck removed."
    return
}

$project = Join-Path $PSScriptRoot "..\src\Tapedeck\Tapedeck.csproj"
dotnet publish $project -c Release -r win-x64 -p:Platform=x64 --self-contained true -o $dest
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($shortcut)
$lnk.TargetPath = Join-Path $dest "Tapedeck.exe"
$lnk.WorkingDirectory = $dest
$lnk.IconLocation = Join-Path $dest "Assets\Tapedeck.ico"
$lnk.Description = "YouTube Music for Windows"
$lnk.Save()
Write-Output "Installed to $dest and added Tapedeck to the Start menu."
