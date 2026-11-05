# Dev helper: show what Windows' media controls (volume flyout / lock screen) currently know about each app.
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
} | Select-Object -First 1
function Await($op, [Type]$type) {
    $t = $asTask.MakeGenericMethod($type).Invoke($null, @($op)); $t.Wait(-1) | Out-Null; $t.Result
}
[Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime] | Out-Null
$mgr = Await ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])
foreach ($s in $mgr.GetSessions()) {
    $props = Await ($s.TryGetMediaPropertiesAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties])
    $info = $s.GetPlaybackInfo()
    $tl = $s.GetTimelineProperties()
    [pscustomobject]@{
        App = $s.SourceAppUserModelId
        Title = $props.Title
        Artist = $props.Artist
        Album = $props.AlbumTitle
        HasArt = $null -ne $props.Thumbnail
        Status = $info.PlaybackStatus
        Position = $tl.Position
        End = $tl.EndTime
    }
}
