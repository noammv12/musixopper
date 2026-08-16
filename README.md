# Bridget ⚫

**Your personal sales assistant.** Bridget pauses your music when a call starts and brings it back after, writes your call notes, reminds you who to call back, types what you dictate into any app — and when you ask her something out loud, she answers back or opens whatever you asked for.

Built for people who live between calls: sales, support, recruiting. One black-and-silver dock pill above the taskbar; everything happens there.

## Install

1. Download `Bridget.exe` from the artifacts of the latest [build](../../actions) (or the [latest release](../../releases)).
2. Run it. A **B** appears in the tray, the dock pill appears above the taskbar, and a 30-second welcome walks you through setup.
3. In the flyout (left-click the tray icon), turn on **Start with Windows**.

One self-contained exe — nothing to install. First launch takes a couple of seconds while Windows unpacks it.

> **SmartScreen note:** the exe is unsigned, so the first run may show "Windows protected your PC". Click *More info → Run anyway*.

Requires Windows 10 version 1903 or later (Windows 11 works).

## The dock

A small capsule floats just above the taskbar (drag it left/right — the spot is remembered):

- **Resting**: a quiet sliver with a status dot — green (listening), amber (on a call).
- **Hover**: expands into a pill with your status, snippet chips, and the 🎙 💬 ⏰ 📝 chips — dictate, ask Bridget, reminders, notes. **…** opens settings.
- **Call events**: briefly shows "Paused for your call" / "Music resumed", then tucks away.
- Auto-hides during presentations and fullscreen apps; never steals focus from what you're typing.

The settings card (flyout) is draggable too — grab any empty spot on it. It never grows taller than your screen; long panels scroll.

## Ask Bridget 💬

Press **Ctrl+Alt+B** (or the 💬 chip), ask out loud, press again — Bridget either **answers back, out loud**, or **runs one of your commands**:

- "מה שעון בניו יורק?" → she answers, in your language, briefly.
- "תפתחי סיילספורס" → your Salesforce opens (see Commands below).

She can also open **well-known sites with no setup at all** — "תפתחי יוטיוב" just opens YouTube, and "search for aircon suppliers" runs the Google search. Answers land as a clickable toast and in the *Ask Bridget* panel with a Copy button. The hotkey is configurable there, and **Speak answers out loud** can be turned off. Needs your DeepSeek key (same one as notes) and a Groq key or the offline voice model for hearing you. Every exchange (what she heard → what she decided) is logged, so a bad answer is diagnosable.

**Voice:** Bridget speaks with **Hila** — Microsoft's natural female Hebrew neural voice (Aria for English), free, via the Edge speech service. It needs internet; offline she falls back to the Windows voice (male for Hebrew — Windows ships nothing better offline). Want the truly premium sound? Paste an **ElevenLabs** key in the VOICE section and she uses it first. A ▶ Preview button lets you hear the current voice, and "Windows only" mode keeps speech fully offline. Bridget deliberately stays silent while a call is being recorded — her voice would end up in your transcript.

## Commands

Flyout → *Commands…*: name + target rows — a URL, an app path, a folder, anything Windows can open. Run them in one click, or just ask Bridget in your own words ("open WhatsApp", "תפתח את הסי-אר-אם"). Up to 20, stored locally in `commands.json`.

## Music pausing — two trigger modes

Flyout → **Detect calls by**:

- **Microphone** *(zero config)* — pauses when any app opens your mic, resumes ~2 s after it's released. Only ever resumes what it paused. Caveat: outbound dialing opens the mic, so ringing pauses music too.
- **Call events** *(recommended for outbound)* — pauses **only when your softphone reports the call was answered**. In Softphone.Pro add three handlers under *Settings → Integration → Third-party systems* (SIP account: *All*, Action: *Launch a program*):

  | Event                  | URL/Program                              |
  |------------------------|------------------------------------------|
  | `Outgoing call answer` | `C:\path\to\Bridget.exe pause %NUMBER%`  |
  | `Incoming call answer` | `C:\path\to\Bridget.exe pause %NUMBER%`  |
  | `Call end`             | `C:\path\to\Bridget.exe resume`          |

  The `%NUMBER%` part is optional — Softphone.Pro replaces it with the caller's number, which tags your call notes with who the call was with.

  **Don't quote the path** (move the exe to a space-free folder like `C:\Tools` if needed). Pick the "answer" events, not "ring". The app shows these commands with copy buttons. Test with the handler dialog's **Test** button or `Bridget.exe test`; received commands are logged to `%LOCALAPPDATA%\Bridget\log.txt`.

## Call notes (opt-in)

📝 chip (or flyout → *Notes & dictation…*) → **Take notes on my calls**. From then on:

1. During a call, Bridget records your mic + the caller's audio.
2. When you hang up, she transcribes the call. **With a Groq API key** (free at console.groq.com) transcription runs on Groq's whisper-large-v3-turbo — excellent Hebrew, done in seconds. **Without a key** it runs locally with the offline Whisper model (~466 MB one-time download). With both, Groq is first and local is the automatic fallback.
3. With a DeepSeek key she writes **3 bullets + the next step**; without one you get the transcript only.
4. The note pops up in the dock, lands at the top of the notes panel with a Copy button, and is appended to a daily Markdown file (`%LOCALAPPDATA%\Bridget\notes\`). With `%NUMBER%` handlers, notes are tagged with the caller's number.
5. **✨ Follow-up** on each note card drafts a short WhatsApp-style follow-up message from the call, ready to copy.

**Privacy:** recording is OFF by default. Audio is deleted right after processing — only text is kept, on your PC. Keys are stored encrypted (Windows DPAPI, bound to your Windows account). With a Groq key, call audio is uploaded to Groq for transcription; only text goes to DeepSeek. **Recording calls may require consent where you are — check your local law and company policy before enabling.**

The log at `%LOCALAPPDATA%\Bridget\log.txt` narrates every step — if a note doesn't appear, the reason is in there.

## Dictation

Press **Ctrl+Alt+Space** (or the 🎙 chip), speak, press again — the text is typed straight into whatever app your cursor is in. Hebrew by default; works mid-call.

- **Your hotkey:** *Notes & dictation…* → **Dictation** — click the combo box and press the keys you want, or turn it off.
- **Polish with AI** (optional, DeepSeek): cleans punctuation and fillers before typing; **Professional tone** smooths phrasing. If the API is unreachable the raw transcript is typed — a dictation is never lost.

## Snippets

Your repeat texts as chips in the dock. **Click** pastes into the app you're working in (the dock never takes focus); **right-click** copies. Manage up to 15 via **…** → Snippets; optionally **Paste with Ctrl+Alt+1–9** (off by default — skip it if you type with AltGr). Caveats: elevated (admin) apps ignore injected paste (text stays on the clipboard); some terminals bind paste to Ctrl+Shift+V.

## Reminders

⏰ chip: paste the lead's link, pick a time — `30m / 1h / 3h / Tomorrow 9:00 / Custom`. When due, the dock expands with **Open / 10m / ✕**. Missed reminders fire on next launch, marked "Missed".

## Call stats

The flyout shows "Today: 14 calls · 1h 12m" under the status; **Stats…** opens today / last-7-days totals with average call length. Local only (`calls.json`, 90 days), never uploaded.

## Network use

Bridget talks to the network only when *you* opt in: the one-time voice-model download from `huggingface.co`, transcription requests to `api.groq.com` (Groq key), requests to `api.deepseek.com` (DeepSeek key: call summaries, follow-up drafts, dictation polish when enabled, and Ask-Bridget questions), and — for her speaking voice — the text of her replies goes to Microsoft's Edge speech service (or to `api.elevenlabs.io` with an ElevenLabs key). Switch the voice to "Windows only" and speech never leaves your PC. Nothing else, ever.

## Upgrading from Saley

**Quit the old Saley first** (tray icon → Quit) — Bridget warns you if it's still running. On first launch everything migrates automatically: settings, API keys, autostart, and your whole data folder (notes, snippets, reminders, stats, and the offline voice model). Then delete `Saley.exe` and re-point the three Softphone.Pro handlers to `Bridget.exe` — the app reminds you and shows the new commands.

## Good to know

- Only media on the same Windows PC can be controlled — not a phone or another device.
- Turning "Pause music during calls" off mid-call deliberately does *not* resume the music into your call.
- Troubleshooting transcription: if it fails to start, install the Microsoft VC++ 2022 x64 redistributable; check `%LOCALAPPDATA%\Bridget\log.txt`.

## Building from source

```
dotnet publish src/Bridget.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

Requires the .NET 8 SDK on Windows. CI does exactly this on every push and verifies the output stays a single exe (`.github/workflows/build.yml`); tagging `v*` attaches the exe to a GitHub release. The app icon is generated by `assets/make_icon.py`; the whisper.cpp natives ship embedded in the exe and extract to `%LOCALAPPDATA%\Bridget\whisper-runtime\` on first use.
