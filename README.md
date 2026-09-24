# Palon.AI ⚫

**Your personal sales aide.** Palon pauses your music when a call starts and brings it back after, writes your call notes, books the callbacks you promised, types what you dictate into any app — and when you ask him something, he answers back in a composed British-butler register, or just does it: opens your CRM, sets the callback, digs the answer out of your call notes.

**v10** is one coherent experience: the **Now window** (one screen — Palon, the action stack, pay and daily rings, and a command bar that understands "דני מחר ב-11"), a rewritten **dock** (rest / hover / one moment at a time, **Ctrl+Alt+R** books a callback from anywhere), and an **agentic brain** that previews every command before it runs (with Undo), nudges only when it's worth it, and prepares work — drafted follow-ups, unbooked next steps — instead of telling you whom to call. **Focus mode** keeps Palon quiet except for callbacks you set. v9 brought the Terminal, memory, screen reading, Salesforce logging and the model switcher. See [`docs/FIRST_RUN.md`](docs/FIRST_RUN.md) for a first-run checklist.

Built for people who live between calls: sales, support, recruiting. One black-and-silver dock pill above the taskbar for the moment; the Terminal for everything else.

## Install

1. Download `Palon.exe` from the artifacts of the latest [build](../../actions) (or the [latest release](../../releases)).
2. Run it. A **P** appears in the tray, the dock pill appears above the taskbar, and a 30-second welcome walks you through setup.
3. In the flyout (left-click the tray icon), turn on **Start with Windows**.

One self-contained exe — nothing to install. First launch takes a couple of seconds while Windows unpacks it.

> **SmartScreen note:** the exe is unsigned, so the first run may show "Windows protected your PC". Click *More info → Run anyway*.

Requires Windows 10 version 1903 or later (Windows 11 works).

## The dock

One pill just above the taskbar (drag it left/right — the spot is remembered) with exactly three states:

- **Rest**: a status dot, three micro rings for today (calls, deposits, callbacks — the same goals as Now), the overdue count and a small Palon. On a call it becomes an amber capsule (who · timer).
- **Hover**: **+ חזרה**, your pinned templates (one click copies with the client's first name; pick them in Settings), Palon (opens Now) and **…** for dictate / read screen / snippets.
- **Moment** (one at a time): the after-call card (four time chips — one click books, with undo; **העתק סיכום** for Salesforce), the callback-due card, a single Palon nudge, or the quick field. **Ctrl+Alt+R** anywhere opens the quick "name · when" field (change or turn it off in Settings).
- Never takes focus except while the quick field is open; hides for presentations and fullscreen apps; nothing new appears during a call.

## The Now window

Tray → **Terminal**, or Palon on the dock. A Hebrew-first (RTL) glass screen:

- **Palon** on the right with one line for the moment, up to two nudges (dismiss here and it's gone from the dock too) and result cards for longer answers (drafts, briefs, searches — with Copy).
- **The action stack**: only callbacks you set that are due, promises Palon heard on a call, Salesforce logs, templates that fit a call, agreed next steps with nothing booked, buying signals and follow-ups Palon already drafted. Never a generic "call X now".
- **Pay and three rings** (calls, callbacks, deposits vs. pace) on the left; **the command bar** at the bottom (Ctrl+K): type what you want — "דני מחר ב-11", "מאיה הפקידה 500", "נסח הודעה לרון", "מה עכשיו", "פוקוס שעה" — see a preview, confirm, undo. Anything else goes to Ask.
- **Rituals**: a morning brief (your promises, pace and the best opportunities — in Palon's voice when an AI key is set), welcome-back, and an end-of-day recap with what's still open.
- **Side sheets** from the rail (Ctrl+2…9): Month, Callbacks, Clients, Templates, Coaching, Memory, Calls, **Settings** (brain keys/model/name, the Ctrl+Alt+R hotkey, dock pins, daily calls goal, focus mode, sounds).

**Focus mode** (Settings, or type "פוקוס שעה"): Palon stops proactive nudges; only reminders you set get through.

**Palon the character:** a small vector figure whose mood follows what he's doing. **Model switcher:** *Auto* (Gemini first, DeepSeek fallback) or a specific model.

## Month & deals

**Month** tracks the month's deposits against your target and the bonus rules: deals with region (ישראל/פרו), CLUB tier, source, amount, approved. **ייבוא מאקסל** imports your monthly sheet (`.xlsx` or `.csv`, columns A–I: name, date, ישראל/פרו, CLUB, source, amount, approved, FTD bonus — recomputed, note), with a preview and row warnings before anything is saved; export writes an Excel-ready CSV. Work days (Sun–Thu) drive the pace line. Local only (`sales.json`).

## Templates

Your WhatsApp messages as templates with `{name}`-style fields, copied from the Terminal or the after-call card. Palon keeps a small log of what you copied (and your edits inside Palon) and, over time, **proposes** an edit to a template or a new one from a message you keep writing by hand — you accept, tweak or reject; templates are versioned so a change can be undone.

## Coaching

Every summarized call also yields local talk/listen numbers (from the separate mic and caller tracks, measured before the audio is deleted) and — in the same AI call as the summary — the objections the client raised, the questions they asked, and whether a next step was agreed, each backed by a **verbatim quote** that is checked against the transcript (anything not found is dropped). The **Coaching** page shows the week against the last, your best call and why, and phrases that correlate with deposits once there are enough calls (8+). Everything stays on your PC (`coaching.json`).

## Memory

Palon remembers two things, both **encrypted with DPAPI** under `%LOCALAPPDATA%\Palon\memory\`:

- **About you** — say or type "תזכור ש…" ("remember that…") and it's saved; "תשכח ש…" forgets. From your Ask conversations he may also *suggest* things to remember — suggestions are never saved until you accept them on the **Memory** page.
- **About clients** — facts heard on calls (a spouse's name, when they get paid, the broker they use), each with the quote it came from, attached to the client's number. They surface in the caller brief and in Ask ("מה אני יודע על דני?").

The Memory page lists everything with edit / forget / undo, and a **pause** switch that stops all new memory. A toast tells you whenever Palon remembers or forgets something outside the Terminal.

## Screen reading

**קרא מהמסך** in Ask (or the dock chip, or the opt-in **Ctrl+Alt+Shift+S**): drag a region, and a **privacy gate** shows the exact image that would be sent and to which model — nothing leaves your PC until you press Send. Palon answers about it (with Copy for the extracted text), or, for a receipt, pre-fills a new deal for you to check before saving. Needs a vision-capable model (the Gemini models; DeepSeek is text-only and is skipped).

## Salesforce (via Palon's Edge)

*Log to Salesforce* on the after-call card or Today writes the call (note + the callback you booked) into Salesforce by driving **a dedicated Edge window** Palon opens with a private profile (`%LOCALAPPDATA%\Palon\edge-profile`) and local remote debugging (loopback only, random port). You sign in to Salesforce there once. First time: open a **test contact**, use **למד את Palon** (teach mode) to show him the clicks once, try a **dry run**, then approve real writes. Every write is **plan → your approval → execute → verify**, never deletes, and is undoable from the audit list; a quiet nightly rehearsal on the test record tells you if a taught skill broke. If your company sets the Edge policy `RemoteDebuggingAllowed=0`, Palon detects it and says so — Salesforce logging is then unavailable (everything else works).

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
3. With an AI key he writes **3 bullets + the next step**; without one you get the transcript only. Cheapest option: a **Gemini** key (aistudio.google.com — has a free tier, but its daily quota on the newest Flash models is small, so check your quota page; default model `gemini-3.8-flash`); a **DeepSeek** key works as the paid fallback (default model `deepseek-flash`). (Both ids can be overridden in the registry values `GeminiModel` / `DeepSeekModel`; blank means the default.) If a summary fails, the toast names the reason instead of failing silently.
4. The note pops up in the dock, lands at the top of the notes panel with a Copy button, and is appended to a daily Markdown file (`%LOCALAPPDATA%\Palon\notes\`). With `%NUMBER%` handlers, notes are tagged with the caller's number.
5. **✨ Follow-up** on each note card drafts a short WhatsApp-style follow-up message from the call — Copy it, or when the caller's number is known, **Open in WhatsApp** lands it straight in their chat, prefilled.
6. The panel's health line — "Last note … · Last call Palon saw …" — turns "notes stopped working" into a named cause: if Palon isn't seeing calls at all, your softphone handlers are pointing at the wrong exe, and the line links straight to the setup.

**Privacy:** recording is OFF by default. **Gemini free tier:** Google's terms allow unpaid-tier prompts and outputs to be used to improve its products and read by human reviewers — call transcripts are your clients' personal data, so for real calls prefer a **paid** Gemini key (billing enabled) or DeepSeek. Audio is deleted right after processing — only text is kept, on your PC. Keys are stored encrypted (Windows DPAPI, bound to your Windows account). With a Groq key, call audio is uploaded to Groq for transcription; only text goes to your AI provider (Gemini/DeepSeek — note Gemini's free tier may use prompts to improve Google's products). When you ask Palon something that needs your notes or stats, the matching snippets go to the AI provider as tool results — same consent as summaries. **Recording calls may require consent where you are — check your local law and company policy before enabling.**

The log at `%LOCALAPPDATA%\Palon\log.txt` narrates every step — if a note doesn't appear, the reason is in there.

## Dictation

Press **Ctrl+Alt+Space** (or the 🎙 chip), speak, press again — the text is typed straight into whatever app your cursor is in. Hebrew by default; works mid-call.

- **Your hotkey:** *Notes & dictation…* → **Dictation** — click the combo box and press the keys you want, or turn it off.
- **Polish with AI** (optional, any AI key): cleans punctuation and fillers before typing; **Professional tone** smooths phrasing. If the API is unreachable the raw transcript is typed — a dictation is never lost.

## Snippets

Your repeat texts as chips in the dock. **Click** pastes into the app you're working in (the dock never takes focus); **right-click** copies. Manage up to 15 via **…** → Snippets; optionally **Paste with Ctrl+Alt+1–9** (off by default — skip it if you type with AltGr). Caveats: elevated (admin) apps ignore injected paste (text stays on the clipboard); some terminals bind paste to Ctrl+Shift+V.

## Callbacks

Reminders are now **callbacks**: who (name and/or phone, optional), what (a note — what to do or what was discussed), when, and an optional link. ⏰ chip: fill what you know and pick a time: `30m / 1h / 3h / Tomorrow 9:00 / Custom`. Or just tell Palon ("תזכיר לי לחזור לדני בשלוש"). The list groups them — Overdue / Today / Tomorrow / Later this week / Later — with ✓ done and ✕ cancel. When due, the dock expands with **Open** (or **Done**) / **Copy #** (when there's a number) / **10m** / **✕**. Missed callbacks fire on next launch, marked "Missed".

After a call, if the summary hears a callback promise ("אחזור אליך מחר ב-11", "call me back in an hour") the note carries a *proposed* callback, resolved against the call's end time — nothing is created until you accept it. Accept it from the after-call card in one tap. Your old reminders.json is migrated automatically on first launch (the original is kept as `reminders.v1.bak.json`).

## Call stats

The flyout shows "Today: 14 calls · 1h 12m" under the status; **Stats…** opens today / last-7-days totals with average call length — or ask Palon out loud. Local only (`calls.json`, 90 days), never uploaded.

## Network use

Palon talks to the network only when *you* opt in: the one-time voice-model download from `huggingface.co`, transcription requests to `api.groq.com` (Groq key), AI requests to `generativelanguage.googleapis.com` (Gemini key, model `gemini-3.8-flash` by default) and/or `api.deepseek.com` (DeepSeek key, `deepseek-flash` by default) — call summaries, follow-up drafts, dictation polish when enabled, and Ask-Palon questions with their tool results — and, for his speaking voice, the text of his replies goes to Microsoft's Edge speech service (or to `api.elevenlabs.io` with an ElevenLabs key). Switch the voice to "Windows only" and speech never leaves your PC. In v9 also: screen-read images go to your vision model **only after you press Send** on the gate; memory and coaching ride inside the summary call (no extra requests, except one background suggestion call after Ask conversations while memory is on); Salesforce traffic is Edge talking to your Salesforce — Palon itself only speaks to that Edge over `127.0.0.1`. Nothing else, ever.

### What's stored locally

Everything under `%LOCALAPPDATA%\Palon\`: `log.txt`, `notes\` (index + daily Markdown), `reminders.json` (callbacks), `calls.json` (stats), `sales.json`, `templates.json` + `template_learning.json`, `coaching.json`, `memory\profile.dat` + `memory\clients.dat` (DPAPI-encrypted), `salesforce\` (taught skills, audit, rehearsal), `edge-profile\` (Palon's Edge, including your Salesforce login), `snippets.json`, `commands.json`, and the offline Whisper model. Keys and settings live in the registry (`HKCU\Software\Palon`), keys DPAPI-encrypted. Call audio is deleted after processing.

## Upgrading from Bridget (or Saley)

**Quit the old app first** (tray icon → Quit) — Palon warns you if it's still running. On first launch everything migrates automatically: settings, API keys, autostart, and your whole data folder (notes, snippets, reminders, stats, and the offline voice model). Then delete the old exe and re-point the three Softphone.Pro handlers to `Palon.exe` — the app reminds you and shows the new commands. (The Ask hotkey default moved to **Ctrl+Alt+P**; a custom combo you set before carries over.)

## Good to know

- Once a day Palon quietly checks GitHub for a newer release; when there is one, a small "v9.x available →" link appears in the flyout footer. No auto-update — the link just opens the release page.
- Only media on the same Windows PC can be controlled — not a phone or another device.
- Turning "Pause music during calls" off mid-call deliberately does *not* resume the music into your call.
- Troubleshooting transcription: if it fails to start, install the Microsoft VC++ 2022 x64 redistributable; check `%LOCALAPPDATA%\Palon\log.txt`.

## Building from source

```
dotnet publish src/Palon.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

Requires the .NET 8 SDK on Windows (the project also compiles on Linux for CI-style checks — no XAML, so WPF is only a framework reference there). Tests live in `tests/Palon.Tests` (`dotnet test`, Windows). CI publishes on every push, runs the tests, and verifies the output stays a single exe (`.github/workflows/build.yml`); tagging `v*` attaches the exe to a GitHub release. The app icon is generated by `assets/make_icon.py`; the whisper.cpp natives ship embedded in the exe and extract to `%LOCALAPPDATA%\Palon\whisper-runtime\` on first use.

### How Ask Palon works (agent architecture)

Ask Palon is a tool-calling agent, not an intent router. `src/Agent/` holds the pieces: each capability is one `AgentTool` (name + JSON-Schema parameters + code) registered in `ToolRegistry`; `AgentLoop` sends the persona (from `PalonPersona`, the one voice every AI-written text shares — dictation polish excepted, it stays a neutral cleaner), a short session memory, and live context (time, call state, the current caller's last note, today's stats) to the model with the tool specs, executes the calls it makes, and feeds results back until an answer emerges — terminal tools like "open" end the turn immediately, because the window opening is its own feedback. Gemini and DeepSeek both speak the OpenAI function-calling format; if the tool path fails, Palon degrades to the v7 single-shot JSON intent rather than to silence. Adding a capability = adding one tool class. Since v9 tools carry a policy tier (read-only tools run freely; anything that acts outside Palon asks first), multi-step requests can show a plan for approval, and a run pins the provider that answered (Gemini's `thought_signature` is echoed back between rounds).
