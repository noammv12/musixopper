# Saley ⚫

**Your sales sidekick.** Saley pauses your music when a call starts and brings it back after, keeps your go-to texts one click away, reminds you who to call back — and, if you turn it on, writes your call notes for you.

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

  | Event                  | URL/Program                    |
  |------------------------|--------------------------------|
  | `Outgoing call answer` | `C:\path\to\Saley.exe pause`   |
  | `Incoming call answer` | `C:\path\to\Saley.exe pause`   |
  | `Call end`             | `C:\path\to\Saley.exe resume`  |

  **Don't quote the path** (move the exe to a space-free folder like `C:\Tools` if needed). Pick the "answer" events, not "ring". The app shows these commands with copy buttons. Test with the handler dialog's **Test** button or `Saley.exe test`; received commands are logged to `%LOCALAPPDATA%\Saley\log.txt`.

## Snippets

Your repeat texts as chips in the dock. **Click** pastes straight into the app you're working in (the dock never takes focus); **right-click** copies. Manage up to 15 via **…** → Snippets. Caveats: elevated (admin) apps silently ignore injected paste (the text is still on the clipboard), and some terminals bind paste to Ctrl+Shift+V.

## Reminders

⏰ chip (or flyout → *Reminders…*): paste the lead's link, pick a time — `30m / 1h / 3h / Tomorrow 9:00 / Custom` — done. When it's due, the dock expands with **Open / 10m / ✕**. Open launches the link in your browser. Due reminders queue up, wait politely while you're on a call or presenting, and anything missed while the PC was off fires on the next launch marked "Missed". Stored in `%LOCALAPPDATA%\Saley\reminders.json`.

## Call notes (opt-in)

Flyout → *Call notes…* → **Take notes on my calls**. From then on:

1. During a call, Saley records your mic + the caller's audio.
2. When you hang up, it transcribes **locally on your PC** with Whisper (Hebrew-tuned by default; one-time ~466 MB voice-model download on first enable).
3. If you've pasted a DeepSeek API key, it writes **3 bullets + the next step**; without a key you get the transcript only.
4. The note pops up in the dock ("Notes ready — click to view"), lands in the flyout with a Copy button, and is appended to a daily Markdown file (`%LOCALAPPDATA%\Saley\notes\`).

**Privacy:** recording is OFF by default. Audio files are deleted immediately after transcription — only text is kept, on your PC. The DeepSeek key is stored encrypted (Windows DPAPI, bound to your Windows account) and only the transcript text is sent to DeepSeek when summarizing. **Recording calls may require consent where you are — check your local law and company policy before enabling.**

Transcription needs a CPU with AVX2 (any modern one) and takes roughly 1–2 minutes for a 10-minute call; notes are processed one at a time in the background while you keep calling.

## Network use

Saley talks to the network in exactly two cases, both opt-in: the one-time voice-model download from `huggingface.co`, and summary requests to `api.deepseek.com` when *you* add a key. Nothing else, ever.

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
