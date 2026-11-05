# Tapedeck privacy policy

Tapedeck has no servers, accounts or analytics of its own. It collects no telemetry and sends nothing to its developers.

## What Tapedeck connects to, and why

Tapedeck only talks to other systems to do what you ask of it:

| Service | When | What is sent |
|---|---|---|
| **YouTube Music / Google** (`music.youtube.com`, `accounts.google.com` and Google's image servers) | While the app runs: browsing, searching, playing music, and signing in if you choose to | The same requests the YouTube Music website makes. When you are signed in, these carry your Google session. Google's own [privacy policy](https://policies.google.com/privacy) applies. |
| **LRCLIB** (`lrclib.net`) | Only while the Lyrics panel is open | The current song's title, artist, album and length, to find time-synced lyrics. No account details. You can turn this off in **Settings → Privacy & security → Synced lyrics from LRCLIB**. |

Tapedeck also hands the current song's title, artist and artwork to Windows, for the media controls in the volume flyout and on the lock screen.

## What Tapedeck stores on your PC

Everything lives in `%LOCALAPPDATA%\Tapedeck`:

- `settings.json`: your preferences (volume, theme, window size).
- `WebView2\`: the web profile that holds your Google sign-in session, encrypted with your Windows account. Turn off **Stay signed in on this PC** to keep the session in memory only. **Sign out** ends the session with Google and wipes this data.
- `errors.log`: error messages, if any. It never contains passwords, cookies or tokens.

Tapedeck never sees or stores your Google password. You type it into Google's own sign-in page.

Uninstalling Tapedeck offers to delete all of this data.

## Contact

Questions or concerns: [open an issue](https://github.com/bhaiyatu/tapedeck/issues).
