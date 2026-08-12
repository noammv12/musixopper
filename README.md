# Musixopper 🎵📞

A tiny Windows tray app that **automatically pauses your music when you answer a call, and resumes it when the call ends**.

Built for people who live between calls — sales, support, recruiting — and are tired of scrambling for the pause button. Works with **any** softphone or calling app (Softphone.Pro, Teams, Zoom, Discord, WhatsApp Desktop, browser calls…) and **any** music source that Windows media keys can control (YouTube Music in Chrome/Edge, Spotify, Apple Music, foobar2000…).

## How it works

Windows keeps track of which apps have the microphone open. Musixopper watches that:

1. You answer a call → your softphone opens the microphone.
2. Musixopper notices within ~1 second and pauses everything that's playing, exactly like pressing the pause media key — but per-session, so it never accidentally *starts* anything.
3. The call ends → the mic is released. After a 2-second grace period (so quick back-to-back calls don't blip the music), Musixopper resumes **only what it paused**. If your music was already paused before the call, it stays paused.

No configuration, no browser extension, no hooks into your softphone.

## Install

1. Download `Musixopper.exe` from the [latest release](../../releases) (or from the artifacts of the latest [build run](../../actions)).
2. Put it anywhere (e.g. a folder in Documents) and run it. A ♪ icon appears in the tray:
   - 🟢 green — watching, no call in progress
   - 🟠 orange — on a call, music paused
   - ⚪ grey — disabled
3. Right-click the icon → check **Start with Windows** so it's always there.

That's it. It's a single self-contained exe — nothing else to install.

> **SmartScreen note:** the exe is unsigned, so the first run may show "Windows protected your PC". Click *More info → Run anyway*.

Requires Windows 10 version 1903 or later (Windows 11 works).

## Tray menu

- **Pause music during calls** — toggle the whole behavior on/off.
- **Start with Windows** — adds/removes a registry Run entry for the current user.
- **Exit** — quits and stops touching your music.

## Advanced: wiring directly into Softphone.Pro call events

If you'd rather trigger on the softphone's own events instead of mic detection, Musixopper also works as a command-line tool:

```
Musixopper.exe pause    # pauses everything playing, remembers what it paused
Musixopper.exe resume   # resumes only what the previous "pause" paused
```

In Softphone.Pro, open *Settings → Integration / Call Events* and set:

- **Call answered** → run `C:\path\to\Musixopper.exe pause`
- **Call finished** → run `C:\path\to\Musixopper.exe resume`

CLI mode and tray mode are independent — use one or the other.

## Good to know

- Mic detection means music also pauses when *anything* uses your microphone (voice recording, dictation, a browser mic check). Usually that's what you want; if not, use the CLI mode above.
- Music playing on a phone or another device can't be controlled — only media on the same Windows PC.
- Musixopper never sends anything anywhere. It reads one registry key and talks to the Windows media session API. No network access at all.

## Building from source

```
dotnet publish src/Musixopper.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

Requires the .NET 8 SDK. CI does exactly this on every push (see `.github/workflows/build.yml`); tagging `v*` attaches the exe to a GitHub release.
