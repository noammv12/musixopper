# Saley ⚫

**Your sales sidekick.** Saley pauses your music when a call starts and brings it back after, keeps your go-to texts one click away, reminds you who to call back, writes your call notes for you (opt-in) — and types what you dictate straight into any app.

Built for people who live between calls: sales, support, recruiting. One black-and-silver dock pill above the taskbar; everything happens there.

## Install

1. Download `Saley.exe` from the [latest release](../../releases) (or from the artifacts of the latest [build](../../actions)).
2. Run it. An **S** appears in the tray, the dock pill appears above the taskbar, and a 30-second welcome walks you through setup.
3. In the flyout (left-click the tray icon), turn on **Start with Windows**.

One self-contained exe — nothing to install. First launch takes a couple of seconds while Windows unpacks it.

> **SmartScreen note:** the exe is unsigned, so the first run may show "Windows protected your PC". Click *More info → Run anyway*.

Requires Windows 10 version 1903 or later (Windows 11 works).

## The dock

A small capsule floats just above the taskbar (drag it left/right — the spot is remembered):

- **Resting**: a quiet sliver with a status dot — green (listening), amber (on a call).
- **Hover**: expands into a pill with your status, snippet chips, the ⏰ reminder chip, and **…** for settings.
- **Call events**: briefly shows "Paused for your call" / "Music resumed", then tucks away.
- **Reminders**: when one is due, the pill expands and stays until you act on it.
- Auto-hides during presentations and fullscreen apps; never steals focus from what you're typing.

## Music pausing — two trigger modes

Flyout → **Detect calls by**:

- **Microphone** *(zero config)* — pauses when any app opens your mic, resumes ~2 s after it's released. Only ever resumes what it paused. Caveat: outbound dialing opens the mic, so ringing pauses music too.
- **Call events** *(recommended for outbound)* — pauses **only when your softphone reports the call was answered**. In Softphone.Pro add three handlers under *Settings → Integration → Third-party systems* (SIP account: *All*, Action: *Launch a program*):

  | Event                  | URL/Program                             |
  |------------------------|-----------------------------------------|
  | `Outgoing call answer` | `C:\path\to\Saley.exe pause %NUMBER%`   |
  | `Incoming call answer` | `C:\path\to\Saley.exe pause %NUMBER%`   |
  | `Call end`             | `C:\path\to\Saley.exe resume`           |

  The `%NUMBER%` part is optional — Softphone.Pro replaces it with the caller's number, which tags your call notes with who the call was with.

  **Don't quote the path** (move the exe to a space-free folder like `C:\Tools` if needed). Pick the "answer" events, not "ring". The app shows these commands with copy buttons. Test with the handler dialog's **Test** button or `Saley.exe test`; received commands are logged to `%LOCALAPPDATA%\Saley\log.txt`.

## Snippets

Your repeat texts as chips in the dock. **Click** pastes straight into the app you're working in (the dock never takes focus); **right-click** copies. Manage up to 15 via **…** → Snippets. Caveats: elevated (admin) apps silently ignore injected paste (the text is still on the clipboard), and some terminals bind paste to Ctrl+Shift+V.

## Reminders

⏰ chip (or flyout → *Reminders…*): paste the lead's link, pick a time — `30m / 1h / 3h / Tomorrow 9:00 / Custom` — done. When it's due, the dock expands with **Open / 10m / ✕**. Open launches the link in your browser. Due reminders queue up, wait politely while you're on a call or presenting, and anything missed while the PC was off fires on the next launch marked "Missed". Stored in `%LOCALAPPDATA%\Saley\reminders.json`.

## Call notes (opt-in)

Flyout → *Call notes…* → **Take notes on my calls**. From then on:

1. During a call, Saley records your mic + the caller's audio.
2. When you hang up, it transcribes the call. **With a Groq API key** (free at console.groq.com, paste it in the panel) transcription runs on Groq's whisper-large-v3-turbo — excellent Hebrew, done in seconds. **Without a key** it runs locally on your PC with the offline Whisper model (~466 MB one-time download, 1–2 min for a 10-min call). With both, Groq is used first and the local model is the automatic fallback when Groq is unreachable or rate-limited.
3. If you've pasted a DeepSeek API key, it writes **3 bullets + the next step**; without one you get the transcript only.
4. The note pops up in the dock ("Notes ready — click to view"), lands in the flyout with a Copy button, and is appended to a daily Markdown file (`%LOCALAPPDATA%\Saley\notes\`).

**Privacy:** recording is OFF by default. Audio files are deleted immediately after processing — only text is kept, on your PC. Keys are stored encrypted (Windows DPAPI, bound to your Windows account). With a Groq key, call audio is uploaded to Groq for transcription; only transcript text goes to DeepSeek. **Recording calls may require consent where you are — check your local law and company policy before enabling.**

The log at `%LOCALAPPDATA%\Saley\log.txt` narrates every step of note processing — if a note doesn't appear, the reason is in there.

## Dictation

Press **Ctrl+Alt+Space** (or the 🎙 chip in the dock), speak, press it again (or click Finish) — the text is typed straight into whatever app your cursor is in. Hebrew by default, powered by the same Groq/local engine as call notes (needs a Groq key or the offline model). ✕ on the dock cancels. Works mid-call.

## Network use

Saley talks to the network only when *you* opt in: the one-time voice-model download from `huggingface.co`, transcription requests to `api.groq.com` when you add a Groq key, and summary requests to `api.deepseek.com` when you add a DeepSeek key. Nothing else, ever.

## Upgrading from Musixopper

Settings and autostart migrate automatically on first run (the old exe's autostart entry is removed). Then: quit and delete the old `Musixopper.exe`, and re-point the three Softphone.Pro handlers to `Saley.exe` — the app reminds you and shows the new commands.

## Good to know

- Only media on the same Windows PC can be controlled — not a phone or another device.
- Turning "Pause music during calls" off mid-call deliberately does *not* resume the music into your call.
- Troubleshooting transcription: if it fails to start, install the Microsoft VC++ 2022 x64 redistributable; check `%LOCALAPPDATA%\Saley\log.txt`.

## Building from source

```
dotnet publish src/Saley.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

Requires the .NET 8 SDK on Windows. CI does exactly this on every push and verifies the output stays a single exe (`.github/workflows/build.yml`); tagging `v*` attaches the exe to a GitHub release. The app icon is generated by `assets/make_icon.py`; the whisper.cpp natives ship embedded in the exe and extract to `%LOCALAPPDATA%\Saley\whisper-runtime\` on first use.
