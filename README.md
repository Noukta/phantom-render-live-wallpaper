# Wallpaper Engine (working name)

Video wallpapers for Windows, built for gamers. It's light while you play, and it gets out of the way when a game is fullscreen.

- Plays your videos behind the desktop icons with [mpv](https://mpv.io) and hardware decoding.
- Rotates through your folders on a timer, with a different wallpaper on each monitor or one spanning them all.
- When a fullscreen or borderless game is in front, it either **freezes** the video or **stops** the player and shows a still frame. In stop mode it uses no GPU or VRAM.
- It doesn't inject anything into games or hook them, so it doesn't interfere with anti-cheat.
- The engine is a small native (NativeAOT) exe. A control panel is coming.

## Build

Requires the .NET 8 SDK.

```powershell
scripts\get-mpv.ps1     # downloads mpv.exe into tools\mpv
scripts\build.ps1       # publishes publish\WallpaperEngine.exe + mpv.exe
```

## Settings

`%APPDATA%\WallpaperEngine\settings.json` (created on first run):

| key | default | meaning |
|---|---|---|
| `folders` | `[]` | folders scanned for videos (recursive) |
| `intervalMinutes` | `10` | time between automatic changes |
| `shuffle` | `true` | random order, otherwise alphabetical |
| `fullscreenMode` | `stop` | `freeze` (pause the video) or `stop` (close mpv, show a still frame) |
| `mute` | `true` | play without audio |
| `monitorMode` | `perMonitor` | `perMonitor` or `span` |
| `hwdec` | `auto-safe` | mpv hardware decoder |
| `mpvPriority` | `belowNormal` | process priority of mpv |
| `mpvPath` | – | explicit path to mpv.exe |

The log is at `%LOCALAPPDATA%\WallpaperEngine\engine.log`.

## Control pipe

The engine listens on `\\.\pipe\wpe_control` (current user only) for one JSON object per line:
`{"id":1,"cmd":"getState"}`, plus `next`, `setWallpaper` (`monitor`, `path`), `pause` (`paused`), `reloadSettings` and `stats`.
It answers with `{"id":1,"ok":true,...}` and pushes events such as `{"event":"wallpaperChanged",...}`.

## License

MIT, see [LICENSE](LICENSE). mpv isn't included in this repository; it's downloaded separately under its own license.
