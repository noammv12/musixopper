# Palon.AI ⚫

**Your personal sales aide.** Palon pauses your music when a call starts and brings it back after, writes your call notes, reminds you who to call back, types what you dictate into any app — and when you ask him something out loud, he answers back in a composed British-butler register, or just does it: opens your CRM, sets the reminder, digs the answer out of your call notes.

Built for people who live between calls: sales, support, recruiting. One black-and-silver dock pill above the taskbar; everything happens there.

## Install

1. Download `Palon.exe` from the artifacts of the latest [build](../../actions) (or the [latest release](../../releases)).
2. Run it. A **P** appears in the tray, the dock pill appears above the taskbar, and a 30-second welcome walks you through setup.
3. In the flyout (left-click the tray icon), turn on **Start with Windows**.

One self-contained exe — nothing to install. First launch takes a couple of seconds while Windows unpacks it.

> **SmartScreen note:** the exe is unsigned, so the first run may show "Windows protected your PC". Click *More info → Run anyway*.

Requires Windows 10 version 1903 or later (Windows 11 works).

## The dock

A small capsule floats just above the taskbar (drag it left/right — the spot is remembered):

- **Resting**: a quiet sliver with a status dot — green (listening), amber (on a call).
- **Hover**: expands into a pill with your status, snippet chips, and the 🎙 💬 ⏰ 📝 chips — dictate, ask Palon, reminders, notes. **…** opens settings.
- **Call events**: briefly shows "Paused for your call" / "Music resumed", then tucks away.
- Auto-hides during presentations and fullscreen apps; never steals focus from what you're typing.

The settings card (flyout) is draggable too — grab any empty spot on it. It never grows taller than your screen; long panels scroll.

## Ask Palon 💬

Press **Ctrl+Alt+P** (or the 💬 chip) and just talk — **Palon stops listening by himself when you go quiet** (turn that off under LISTENING to go back to press-twice). He decides what the request needs, uses his tools, and either **answers out loud** or **does the thing**:

- "מה שעון בניו יורק?" → he answers, in your language, briefly.
- "תפתח סיילספורס" → your Salesforce opens (see Commands below).
- "תזכיר לי לחזור לדני בשלוש" → a reminder appears at 15:00 — no link, no form.
- "מה סיכמתי עם 050-1234567?" → he reads it out of your call notes.
- "כמה שיחות עשיתי היום?" → your stats, spoken.
- "עצור את המוזיקה" → the music pauses.

He can also open **well-known sites with no setup at all** — "תפתח יוטיוב" just opens YouTube, and "search for aircon suppliers" runs the Google search. **Follow-ups work**: for a few minutes Palon remembers the exchange, so "ומה מחר?" continues the conversation. **Mid-call he knows who you're talking to** — when your softphone passes the caller's number, your last note about that caller is already in his head, **and the moment a known number calls, the dock briefs you**: how long since you last spoke and the next step you promised. Two more he handles by voice: "תכין הודעת פולו-אפ לדני ותפתח בוואטסאפ" opens the WhatsApp chat with the drafted message prefilled, and "סכם לי את היום" gets you a spoken end-of-day recap (also a click away under **Stats… → ✨ Recap my day**).

Answers land as a clickable toast and in the *Ask Palon* panel with a Copy button. The hotkey is configurable there, and **Speak answers out loud** can be turned off. Needs a Gemini or DeepSeek key (same as notes) and a Groq key or the offline voice model for hearing you. Every exchange (what he heard → which tools he used) is logged, so a bad answer is diagnosable.

**Follow-ups without the hotkey:** turn on **Keep listening after answers** and Palon reopens the mic for a beat once he finishes speaking — ask the next thing, or say nothing and it closes itself. Off by default (it's a hot-mic preference).

**Voice:** Palon speaks with **Avri** — Microsoft's natural male Hebrew neural voice (**Ryan**, a composed British male, for English), free, via the Edge speech service. Replies **stream**: he starts talking well under a second after the answer is ready, while the rest is still being synthesized (ElevenLabs streams too). It needs internet; offline he falls back to the Windows voice. Want the truly premium sound? Paste an **ElevenLabs** key in the VOICE section and he uses it first (default voice: their British "Daniel"). A ▶ Preview button lets you hear the current voice, and "Windows only" mode keeps speech fully offline. Palon deliberately stays silent while a call is being recorded — his voice would end up in your transcript.

## Commands

Flyout → *Commands…*: name + target rows — a URL, an app path, a folder, anything Windows can open. Run them in one click, or just ask Palon in your own words ("open WhatsApp", "תפתח את הסי-אר-אם"). Up to 20, stored locally in `commands.json`.

## Music pausing — two trigger modes

Flyout → **Detect calls by**:

- **Microphone** *(zero config)* — pauses when any app opens your mic, resumes ~2 s after it's released. Only ever resumes what it paused. Caveat: outbound dialing opens the mic, so ringing pauses music too.
- **Call events** *(recommended for outbound)* — pauses **only when your softphone reports the call was answered**. In Softphone.Pro add three handlers under *Settings → Integration → Third-party systems* (SIP account: *All*, Action: *Launch a program*):

  | Event                  | URL/Program                            |
  |------------------------|----------------------------------------|
  | `Outgoing call answer` | `C:\path\to\Palon.exe pause %NUMBER%`  |
  | `Incoming call answer` | `C:\path\to\Palon.exe pause %NUMBER%`  |
  | `Call end`             | `C:\path\to\Palon.exe resume`          |

  The `%NUMBER%` part is optional — Softphone.Pro replaces it with the caller's number, which tags your call notes with who the call was with (and is what lets Palon brief you mid-call).

  **Don't quote the path** (move the exe to a space-free folder like `C:\Tools` if needed). Pick the "answer" events, not "ring". The app shows these commands with copy buttons. Test with the handler dialog's **Test** button or `Palon.exe test`; received commands are logged to `%LOCALAPPDATA%\Palon\log.txt`.

## Call notes (opt-in)

📝 chip (or flyout → *Notes & dictation…*) → **Take notes on my calls**. From then on:

1. During a call, Palon records your mic + the caller's audio.
2. When you hang up, he transcribes the call. **With a Groq API key** (free at console.groq.com) transcription runs on Groq's whisper-large-v3-turbo — excellent Hebrew, done in seconds. **Without a key** it runs locally with the offline Whisper model (~466 MB one-time download). With both, Groq is first and local is the automatic fallback.
3. With an AI key he writes **3 bullets + the next step**; without one you get the transcript only. Best free option: a **Gemini** key (aistudio.google.com — free Flash tier, ~1,500 calls/day); a **DeepSeek** key works as the paid fallback. If a summary fails, the toast names the reason instead of failing silently.
4. The note pops up in the dock, lands at the top of the notes panel with a Copy button, and is appended to a daily Markdown file (`%LOCALAPPDATA%\Palon\notes\`). With `%NUMBER%` handlers, notes are tagged with the caller's number.
5. **✨ Follow-up** on each note card drafts a short WhatsApp-style follow-up message from the call — Copy it, or when the caller's number is known, **Open in WhatsApp** lands it straight in their chat, prefilled.
6. The panel's health line — "Last note … · Last call Palon saw …" — turns "notes stopped working" into a named cause: if Palon isn't seeing calls at all, your softphone handlers are pointing at the wrong exe, and the line links straight to the setup.

**Privacy:** recording is OFF by default. Audio is deleted right after processing — only text is kept, on your PC. Keys are stored encrypted (Windows DPAPI, bound to your Windows account). With a Groq key, call audio is uploaded to Groq for transcription; only text goes to your AI provider (Gemini/DeepSeek — note Gemini's free tier may use prompts to improve Google's products). When you ask Palon something that needs your notes or stats, the matching snippets go to the AI provider as tool results — same consent as summaries. **Recording calls may require consent where you are — check your local law and company policy before enabling.**

The log at `%LOCALAPPDATA%\Palon\log.txt` narrates every step — if a note doesn't appear, the reason is in there.

## Dictation

Press **Ctrl+Alt+Space** (or the 🎙 chip), speak, press again — the text is typed straight into whatever app your cursor is in. Hebrew by default; works mid-call.

- **Your hotkey:** *Notes & dictation…* → **Dictation** — click the combo box and press the keys you want, or turn it off.
- **Polish with AI** (optional, any AI key): cleans punctuation and fillers before typing; **Professional tone** smooths phrasing. If the API is unreachable the raw transcript is typed — a dictation is never lost.

## Snippets

Your repeat texts as chips in the dock. **Click** pastes into the app you're working in (the dock never takes focus); **right-click** copies. Manage up to 15 via **…** → Snippets; optionally **Paste with Ctrl+Alt+1–9** (off by default — skip it if you type with AltGr). Caveats: elevated (admin) apps ignore injected paste (text stays on the clipboard); some terminals bind paste to Ctrl+Shift+V.

## Reminders

⏰ chip: type what to do — a link is optional now — and pick a time: `30m / 1h / 3h / Tomorrow 9:00 / Custom`. Or just tell Palon ("תזכיר לי לחזור לדני בשלוש"). When due, the dock expands with **Open / 10m / ✕** (text-only reminders show **Done** instead of Open). Missed reminders fire on next launch, marked "Missed".

## Call stats

The flyout shows "Today: 14 calls · 1h 12m" under the status; **Stats…** opens today / last-7-days totals with average call length — or ask Palon out loud. Local only (`calls.json`, 90 days), never uploaded.

## Network use

Palon talks to the network only when *you* opt in: the one-time voice-model download from `huggingface.co`, transcription requests to `api.groq.com` (Groq key), AI requests to `generativelanguage.googleapis.com` (Gemini key) and/or `api.deepseek.com` (DeepSeek key) — call summaries, follow-up drafts, dictation polish when enabled, and Ask-Palon questions with their tool results — and, for his speaking voice, the text of his replies goes to Microsoft's Edge speech service (or to `api.elevenlabs.io` with an ElevenLabs key). Switch the voice to "Windows only" and speech never leaves your PC. Nothing else, ever.

## Upgrading from Bridget (or Saley)

**Quit the old app first** (tray icon → Quit) — Palon warns you if it's still running. On first launch everything migrates automatically: settings, API keys, autostart, and your whole data folder (notes, snippets, reminders, stats, and the offline voice model). Then delete the old exe and re-point the three Softphone.Pro handlers to `Palon.exe` — the app reminds you and shows the new commands. (The Ask hotkey default moved to **Ctrl+Alt+P**; a custom combo you set before carries over.)

## Good to know

- Once a day Palon quietly checks GitHub for a newer release; when there is one, a small "v8.x available →" link appears in the flyout footer. No auto-update — the link just opens the release page.
- Only media on the same Windows PC can be controlled — not a phone or another device.
- Turning "Pause music during calls" off mid-call deliberately does *not* resume the music into your call.
- Troubleshooting transcription: if it fails to start, install the Microsoft VC++ 2022 x64 redistributable; check `%LOCALAPPDATA%\Palon\log.txt`.

## Building from source

```
dotnet publish src/Palon.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

Requires the .NET 8 SDK on Windows (the project also compiles on Linux for CI-style checks — no XAML, so WPF is only a framework reference there). Tests live in `tests/Palon.Tests` (`dotnet test`, Windows). CI publishes on every push, runs the tests, and verifies the output stays a single exe (`.github/workflows/build.yml`); tagging `v*` attaches the exe to a GitHub release. The app icon is generated by `assets/make_icon.py`; the whisper.cpp natives ship embedded in the exe and extract to `%LOCALAPPDATA%\Palon\whisper-runtime\` on first use.

### How Ask Palon works (v8 architecture)

Ask Palon is a tool-calling agent, not an intent router. `src/Agent/` holds the pieces: each capability is one `AgentTool` (name + JSON-Schema parameters + code) registered in `ToolRegistry`; `AgentLoop` sends the persona, a short session memory, and live context (time, call state, the current caller's last note, today's stats) to the model with the tool specs, executes the calls it makes, and feeds results back until an answer emerges — terminal tools like "open" end the turn immediately, because the window opening is its own feedback. Gemini and DeepSeek both speak the OpenAI function-calling format; if the tool path fails, Palon degrades to the v7 single-shot JSON intent rather than to silence. Adding a capability = adding one tool class.
