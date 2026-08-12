# Musixopper 🎵📞

A tiny Windows tray app that **automatically pauses your music when a call is answered, and resumes it when the call ends**.

Built for people who live between calls — sales, support, recruiting — and are tired of scrambling for the pause button. Works with **any** music source that Windows media keys can control (YouTube Music in Chrome/Edge, Spotify, Apple Music, foobar2000…).

## Install

1. Download `Musixopper.exe` from the [latest release](../../releases) (or from the artifacts of the latest [build run](../../actions)).
2. Put it anywhere (e.g. a folder in Documents) and run it. A ♪ icon appears in the tray:
   - 🟢 green — watching, no call in progress
   - 🟠 orange — on a call, music paused
   - ⚪ grey — disabled
3. Right-click the icon → check **Start with Windows** so it's always there.

It's a single self-contained exe — nothing else to install.

> **SmartScreen note:** the exe is unsigned, so the first run may show "Windows protected your PC". Click *More info → Run anyway*.

Requires Windows 10 version 1903 or later (Windows 11 works).

## Two trigger modes

Right-click the tray icon → **Pause trigger**:

### 1. "When my microphone is in use" (default, zero config)

Windows tracks which apps have the microphone open. The moment any app opens your mic, Musixopper pauses everything that's playing; when the mic is released, it resumes — after a 2-second grace period so quick back-to-back calls don't blip the music. It only ever resumes **what it paused**: if your music was already paused before the call, it stays paused.

Works with any calling app with no setup. **Caveat for outbound calling:** most softphones open the microphone as soon as you *dial*, so in this mode music pauses while the phone is still ringing — even if nobody answers.

### 2. "Only on softphone call events (answered calls)" — recommended for outbound sales

In this mode Musixopper ignores the microphone and pauses **only when your softphone reports the call was actually answered**. Dialing and ringing don't touch your music.

Setup with **Softphone.Pro** (one-time, ~2 minutes):

1. In Softphone.Pro open *Settings → Integration → Third-party systems* and add three handlers (Account: *All*, action: run a program):

   | Event                  | Program (URL/Program field)          |
   |------------------------|--------------------------------------|
   | `Outgoing call answer` | `"C:\path\to\Musixopper.exe" pause`  |
   | `Incoming call answer` | `"C:\path\to\Musixopper.exe" pause`  |
   | `Call end`             | `"C:\path\to\Musixopper.exe" resume` |

   (Event names can vary slightly between Softphone.Pro versions — pick the "answer" events, **not** "ring".)

2. Right-click the Musixopper tray icon → *Pause trigger* → **Only on softphone call events**.

That's it: music keeps playing while you dial; the instant the customer picks up it pauses; when you hang up it resumes.

Any other softphone that can run a program on call events works the same way — just point its "answered" event at `Musixopper.exe pause` and its "ended" event at `Musixopper.exe resume`.

> The tray app doesn't even have to be running for this mode: `Musixopper.exe pause`/`resume` also work standalone. With the tray running you additionally get the status icon and the grace-period resume.

## Tray menu

- **Pause music during calls** — toggle the whole behavior on/off.
- **Pause trigger** — choose between the two modes above.
- **Start with Windows** — adds/removes a registry Run entry for the current user.
- **Exit** — quits and stops touching your music.

## Good to know

- Music playing on a phone or another device can't be controlled — only media on the same Windows PC.
- Musixopper never sends anything anywhere. It reads one registry key and talks to the Windows media session API. No network access at all.

## Building from source

```
dotnet publish src/Musixopper.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

Requires the .NET 8 SDK. CI does exactly this on every push (see `.github/workflows/build.yml`); tagging `v*` attaches the exe to a GitHub release.
