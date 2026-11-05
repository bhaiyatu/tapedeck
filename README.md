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
- **Design:** Kaset's layout (a sectioned sidebar with your account pinned at the bottom, and a floating glass player), on a clean near-black canvas or light theme with a rose-to-fuchsia accent
- **Windows integration:** media keys, the volume-flyout and lock-screen media controls, light/dark themes
- **Keyboard:** Space play/pause, Ctrl+←/→ previous/next, Ctrl+↑/↓ volume, Ctrl+K or Ctrl+F search, Ctrl+L like, Ctrl+S shuffle, Ctrl+R repeat
- **Google sign-in** for your personal recommendations and library. YouTube Music Premium plays without ads, just as on the web.

## Privacy & security

Signing in is the riskiest thing a client like this does, so the design keeps Tapedeck out of the way:

- **Tapedeck never sees your password.** You type it into Google's own page. Tapedeck injects no script into that page, takes no messages from it, and never stores passwords or autofill data (both are switched off).
- **You can always see where you are.** The sign-in window has an address bar showing the host, with a padlock and a "Google" / "not Google" label. Non-HTTPS pages are blocked. Non-Google pages, such as a school or company single sign-on, pause behind a warning until you choose to continue. Help links open in your normal browser.
- **Your session only goes to YouTube Music.** The API client attaches your cookies only to `https://music.youtube.com`, never follows redirects (so a redirect can't forward them elsewhere), and accepts only well-formed values from the page into request headers.
- **The hidden player is locked to YouTube.** It can't navigate off YouTube or Google's consent and sign-in pages, and only YouTube Music's own top-level page can message the app. Downloads, popups, permission prompts and external app links are refused.
- **No back doors in release builds.** DevTools, the debug script and command channels, and bridge logging are compiled out. WebView2 environment overrides, which could open a debugging port or swap the browser, are ignored.
- **Stored session.** By default the Google session stays on this PC, in the WebView2 profile under `%LOCALAPPDATA%\Tapedeck`, encrypted with your Windows account (DPAPI). As with any browser, other programs running as you can read it. Turn off **Settings → Stay signed in on this PC** to keep the session in memory only, gone when the app closes.
- **Signing out** ends the session with Google first, then wipes every cookie and all site data from the profile. `install.ps1 -Uninstall -Purge` deletes everything.
- **Lyrics** come from LRCLIB, which receives the song's title and artist only. You can turn it off in Settings.
- Dependencies are pinned with a NuGet lock file (`packages.lock.json`).

## Download

Get the latest **[release](https://github.com/bhaiyatu/tapedeck/releases/latest)**:

- **Tapedeck-…-x64-setup.exe** for most PCs, or **-arm64-setup.exe** for Snapdragon / ARM PCs. It installs just for you, with no admin prompt, and adds a Start menu entry. Uninstalling offers to remove your settings and sign-in too.
- **…-portable.zip** if you'd rather not install: unzip and run `Tapedeck.exe`.

Releases are being moved to signed builds (see the [code signing policy](#code-signing-policy)). If a release isn't signed, Windows SmartScreen may say "Windows protected your PC": choose **More info → Run anyway**, and only for files from the official releases page. Each release lists SHA-256 checksums and carries a GitHub build-provenance attestation, so you can check a file was built from this repository: `gh attestation verify <file> --repo bhaiyatu/tapedeck`.

Requires Windows 10 (2004) or later with the WebView2 runtime (built into Windows 11).

### Releasing a new version

Push a tag and GitHub Actions does the rest (see `.github/workflows/build.yml`):

```powershell
git tag v0.2.0
git push origin v0.2.0
```

## Build from source

You need the .NET 10 SDK.

```powershell
.\tools\install.ps1             # build Release into %LOCALAPPDATA%\Programs\Tapedeck and add a Start menu shortcut
.\tools\install.ps1 -Uninstall  # remove it (settings and sign-in in %LOCALAPPDATA%\Tapedeck are kept)
.\tools\install.ps1 -Uninstall -Purge  # remove it and delete all settings and the stored sign-in
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
- **Debug mode** (Debug builds only, compiled out of Release): set `TAPEDECK_DEBUG=1` before launching to log bridge traffic to `bridge.log`, and to enable two file channels:
  - `tools\player-eval.ps1 -Code "…"` runs JavaScript inside the hidden player page.
  - `tools\dev-shot.ps1 -Command "search daft punk" -Out shot.png` drives the UI (`home`, `explore`, `search`, `album`, `artist`, `playsearch`, `panel lyrics`, `mute`, `signin`…) and captures the window.
- `tools\media-session.ps1` shows what Windows' media controls currently know about each app.

## Known limitations

- YouTube changes its web app without notice. If tracks stop switching, the router hook in `player-bridge.js` is the first place to look (debug mode + `player-eval.ps1` make that quick).
- No video mode, equalizer, mini-player or playlist editing yet.
- Library pages show the first page of results.

## Code signing policy

Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

Only binaries built by this repository's GitHub Actions workflow (`.github/workflows/build.yml`) from its own source are submitted for signing, and every signing request is approved by hand.

**Team roles**
- Committers and reviewers: [bhaiyatu](https://github.com/bhaiyatu)
- Approvers: [bhaiyatu](https://github.com/bhaiyatu)

**Privacy:** this program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it. Tapedeck connects to YouTube Music / Google to do what you ask (browse, play, sign in), and to LRCLIB only while the Lyrics panel is open (you can turn that off). It has no servers or analytics of its own. Details are in the [privacy policy](PRIVACY.md).

## License

[MIT](LICENSE). Tapedeck is an unofficial client and is not affiliated with YouTube or Google.
