# Tapedeck

A native Windows client for YouTube Music, built with C#, WinUI 3 and the Windows App SDK.
Inspired by [Kaset](https://github.com/sozercan/kaset) (a native macOS client) and built on the same idea:
a real native UI on top of YouTube Music's own API, with audio played by YouTube Music's official
web player running hidden in the background.

> Unofficial. Not affiliated with YouTube or Google.

## Features

- **Browse:** Home (with mood filters), Explore, Charts, New releases, Moods & genres
- **Search** with suggestions, results grouped by type, and filters (Songs, Albums, Artists, Playlists, Videos, Podcasts…)
- **Album, playlist, podcast and artist pages:** play, shuffle, play next, add to queue, copy link
- **Library** (when signed in): playlists, liked songs, albums, artists, subscriptions, history
- **Player bar:** seek, shuffle, repeat (all/one), volume and mute, like, and a "now playing" link to the album and artist
- **Up next queue:** endless radio mixes, drag to reorder, remove items, jump to any track
- **Lyrics:** time-synced lyrics from [LRCLIB](https://lrclib.net) that follow the song (click a line to seek), falling back to YouTube Music's own lyrics
- **Windows integration:** media keys, the volume-flyout and lock-screen media controls, Mica, light/dark themes
- **Keyboard:** Space play/pause, Ctrl+←/→ previous/next, Ctrl+↑/↓ volume, Ctrl+F search, Ctrl+L like, Ctrl+S shuffle, Ctrl+R repeat
- **Google sign-in** for your personal recommendations and library. YouTube Music Premium plays without ads, just as on the web.

## Install

You need Windows 10 (2004) or later, the WebView2 runtime (built into Windows 11), and the .NET 10 SDK to build.

```powershell
.\tools\install.ps1             # build Release into %LOCALAPPDATA%\Programs\Tapedeck and add a Start menu shortcut
.\tools\install.ps1 -Uninstall  # remove it (settings and sign-in in %LOCALAPPDATA%\Tapedeck are kept)
```

To run from source:

```powershell
dotnet build src\Tapedeck -p:Platform=x64
.\src\Tapedeck\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\Tapedeck.exe
```

## How it works

| Piece | What it does |
|---|---|
| `Services/Api/InnerTubeClient.cs` | Calls YouTube Music's InnerTube API (`browse`, `search`, `next`, `like`…) as the `WEB_REMIX` client. When signed in it sends the session cookies and a `SAPISIDHASH` authorization header, exactly like the website. |
| `Services/Api/ItemParser.cs`, `PageParser.cs` | Turn InnerTube's renderer soup into simple models: items, shelves, collections, artists, queues and lyrics. |
| `Services/PlayerBridge.cs` + `Scripts/player-bridge.js` | A 1×1 transparent WebView2 loads `music.youtube.com`. The injected script switches tracks through YouTube Music's own router (a `yt-navigate` event), reports playback state back to the app, and auto-advances to the next track the app picked. Using the official player means DRM, Premium audio quality and ads all behave as they do on the web. |
| `Services/PlayerService.cs` | Owns the queue (native, not YouTube's), shuffle and repeat, radio continuation and like state. |
| `Services/AuthService.cs`, `Views/LoginWindow.xaml` | Google sign-in happens in a WebView2 that shares the player's profile, so one session serves both playback and the API. |
| `Services/MediaControlsService.cs` | Publishes the current track to Windows' System Media Transport Controls. |
| `Services/LyricsService.cs` | LRCLIB synced lyrics, with YouTube Music lyrics as the fallback. |

Data lives in `%LOCALAPPDATA%\Tapedeck`: `settings.json`, the `WebView2` profile (your Google session), and `errors.log`.

## Developer tools

- **API probe:** `dotnet run --project tools\ApiProbe -- all` runs the real client and parsers against live YouTube Music and checks the results. Also takes `home`, `explore`, `search <q>`, `album <id>`, `artist <id>`, `queue <videoId>` or `lyrics <videoId>`. Run it first when YouTube changes something.
- **Playback self-test:** `Tapedeck.exe --selftest` plays real tracks with the sound muted and checks play, next, auto-advance at the end of a track, pause/resume, previous and album playback. It writes `%LOCALAPPDATA%\Tapedeck\selftest.log` and exits with 0 on pass.
- **Debug mode:** set `TAPEDECK_DEBUG=1` before launching to log bridge traffic to `bridge.log`, and to enable two file channels:
  - `tools\player-eval.ps1 -Code "…"` runs JavaScript inside the hidden player page.
  - `tools\dev-shot.ps1 -Command "search daft punk" -Out shot.png` drives the UI (`home`, `explore`, `search`, `album`, `artist`, `playsearch`, `panel lyrics`, `mute`, `signin`…) and captures the window.
- `tools\media-session.ps1` shows what Windows' media controls currently know about each app.

## Known limitations

- YouTube changes its web app without notice. If tracks stop switching, the router hook in `player-bridge.js` is the first place to look (debug mode + `player-eval.ps1` make that quick).
- No video mode, equalizer, mini-player or playlist editing yet.
- Library pages show the first page of results.
