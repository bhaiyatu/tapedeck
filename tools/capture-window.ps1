# Captures a process's main window to a PNG using PrintWindow (works for WinUI / DirectComposition content).
param(
    [string]$ProcessName,
    [Parameter(Mandatory)][string]$OutFile,
    [string]$Title,  # optional: capture a secondary window of the process by its exact title
    [int]$ProcessId  # optional: a specific process, e.g. a debug build running next to the installed app
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32Capture {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[Win32Capture]::SetProcessDPIAware() | Out-Null

$proc = if ($ProcessId) { Get-Process -Id $ProcessId -ErrorAction Stop } else { Get-Process -Name $ProcessName -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1 }
if (-not $proc) { throw "No window found for $ProcessName" }
$hwnd = $proc.MainWindowHandle
if ($Title) {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), $Title
    $win = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    if (-not $win) { throw "No window titled '$Title'" }
    $hwnd = [IntPtr]$win.Current.NativeWindowHandle
}
$rect = New-Object Win32Capture+RECT
[Win32Capture]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[Win32Capture]::PrintWindow($hwnd, $hdc, 2) | Out-Null  # PW_RENDERFULLCONTENT
$g.ReleaseHdc($hdc); $g.Dispose()
$bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "Captured ${w}x${h} -> $OutFile"
