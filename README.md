<div align="center">

# 🎵 Kvasir

**A .NET 10 Discord music bot with isolated external connectors**

[![CI](https://github.com/Timbuhtuk/Kvasir/actions/workflows/ci.yml/badge.svg?branch=ToatalRebuild)](https://github.com/Timbuhtuk/Kvasir/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Timbuhtuk/Kvasir?display_name=tag&sort=semver)](https://github.com/Timbuhtuk/Kvasir/releases)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078D4)](#requirements)

</div>

Kvasir plays music in Discord voice channels, manages queues and playlists, and provides an interactive music panel. Its core is independent of any specific Discord library: when an API changes, the connector can be replaced without rewriting the bot logic.

## Features

- Search by title or direct link.
- Queue, pause, resume, repeat, skip, and clear controls.
- Playlists, playback history, and popular tracks.
- An interactive panel with buttons and dropdowns.
- YouTube through `YoutubeExplode`, with `yt-dlp` as a download fallback.
- Optional Spotify metadata refinement.
- Local SQLite storage.
- Discord voice connections with DAVE/E2EE.
- Logging by server and category.

## Architecture

~~~text
Entities ← Application ← Infrastructure
               ↑               ↑
               │               │
     Connectors/Media     Kvasir/Connectors
   Spotify / YouTube          NetCord
~~~

`Application` depends only on the `IBotConnector`, `IBotVoiceConnection`, `IVideoFinderService`, and `IAudioDownloaderService` interfaces. NetCord sits at the project's outer boundary. See [ARCHITECTURE.md](ARCHITECTURE.md) for details and the library replacement procedure.

Check the boundary automatically with:

~~~powershell
.\scripts\check-architecture.ps1
~~~

## Requirements

- Windows x64.
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build from source, or .NET 10 Runtime to run a release.
- A Discord bot application with `Guilds`, `Guild Messages`, `Message Content`, and `Guild Voice States` permissions.
- `ffmpeg.exe`, `ffplay.exe`, and `ffprobe.exe` in `Kvasir/appdata`.
- `yt-dlp.exe` in `Kvasir/appdata`.

Native DAVE and Opus libraries are included in the repository and release package.

## Quick start from source

~~~powershell
git clone https://github.com/Timbuhtuk/Kvasir.git
cd Kvasir
Copy-Item Kvasir/appdata/configuration.json.example Kvasir/appdata/configuration.json
.\scripts\download-dependencies.ps1
~~~

The script downloads the latest `yt-dlp` automatically. Place FFmpeg in `Kvasir/appdata` manually, or pass a ZIP archive URL to the script:

~~~powershell
.\scripts\download-dependencies.ps1 -FfmpegUrl "https://example.com/ffmpeg.zip"
~~~

Pass the token through an environment variable in the current PowerShell session:

~~~powershell
$env:KVASIR_DISCORD_TOKEN = "YOUR_NEW_DISCORD_TOKEN"
cd Kvasir
dotnet run
~~~

For persistent use, set `KVASIR_DISCORD_TOKEN` in the operating system's environment settings or a secret manager. Do not commit the token to Git.

Once connected, run `/anchor` in the text channel where the music panel should appear. Then join a voice channel and run `/play`.

## Commands

| Command | Purpose |
|---|---|
| `/play <query>` | Find a track, add it to the queue, and start playback |
| `/pause` | Pause playback |
| `/resume` | Resume playback |
| `/skip` | Skip to the next track |
| `/clear` | Clear the queue |
| `/leave` | Disconnect the bot from the voice channel |
| `/anchor` | Set the current text channel as the music panel channel |
| `/help` | Show brief help |

The `/reboot` and `/execute` commands are available only to the user specified by `owner_id`. Leave `owner_id` unset if remote command execution is not needed.

## Spotify

Spotify is used only to refine track titles and artists. Enable the integration in your local `configuration.json`:

~~~json
{
  "spotify_settings": {
    "SPOTIFY_ENABLED": true,
    "SPOTIFY_CLIENT_ID": "...",
    "SPOTIFY_CLIENT_SECRET": "..."
  }
}
~~~

This file is excluded from Git. Without Spotify, the bot continues to search for and download music through YouTube.

## Build and release

~~~powershell
dotnet restore Kvasir.sln
dotnet build Kvasir.sln --configuration Release --no-restore
dotnet publish Kvasir/Kvasir.csproj --configuration Release --runtime win-x64 --self-contained false
~~~

Tags matching `v*` trigger GitHub Actions to validate the project, publish it for Windows x64, package it as a ZIP archive, and attach it to a GitHub Release.

## Security

- The working `Kvasir/appdata/configuration.json` is not tracked by Git.
- Pass the Discord token only through `KVASIR_DISCORD_TOKEN` where possible.
- Cookies, the SQLite database, logs, downloaded music, and external executables are excluded from the repository.
- `configuration.json.example` contains only safe defaults.
- If a token ever appears in a message, log, or commit, reset it immediately in the Discord Developer Portal.

## Native component licenses

`libdave.dll` and `opus.dll` are distributed with their corresponding `libdave-LICENSE` and `opus-COPYING` files.
