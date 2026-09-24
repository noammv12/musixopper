# Palon: Salesforce autofill (A) and screen reading (B) — browser-first design

Status: rev 2, 2026-09-24. **User decision: no Salesforce API access. The browser path is the product.** Claims are tagged **VERIFIED** (with repo path or URL) or **CORRECTED**. Repos were cloned at HEAD on 2026-09-24 under `research/repos/`. `[verify-org]` means it has to be checked on the user's real org.

## 1. Attaching to the user's browser

- **VERIFIED:** Starting with Chrome 136, Chrome ignores `--remote-debugging-port` and `--remote-debugging-pipe` on the *default* user-data-dir. They only work together with a non-default `--user-data-dir`. The failure is silent: Chrome starts, the client connects, and then it hangs on a blank page. Sources: https://developer.chrome.com/blog/remote-debugging-port and browser-use issue #1520. Edge is Chromium, so assume it behaves the same. Palon must detect the hang (no `/json/version` within 3 s) and must not wait on it.
- **Three ways to attach, ranked:**
  1. **Dedicated "Palon profile"** (MVP). Launch `msedge.exe`/`chrome.exe --user-data-dir=%LocalAppData%\Palon\browser --remote-debugging-port=0 --remote-debugging-address=127.0.0.1`. Read the actual port from `DevToolsActivePort` in the profile dir. Then call Playwright .NET `ConnectOverCDPAsync`. The user logs into Salesforce there once. Salesforce "remember device" keeps 2FA prompts rare. Cost: Salesforce opens in a second browser window, not in the user's usual one.
  2. **MV3 extension + local CDP relay (the Playwright pattern, VERIFIED).** `microsoft/playwright` `packages/extension` (Apache-2.0) is an MV3 extension with permissions `debugger, activeTab, tabs, tabGroups`. It opens a WebSocket to a local relay and forwards `chrome.debugger.sendCommand`/`onEvent` (`src/relayConnection.ts`). The relay (`packages/playwright-core/src/tools/mcp/cdpRelay.ts`) exposes `/cdp/<guid>` to the client and `/extension/<guid>` to the extension, and a token skips the per-connection approval page. The client then calls `chromium.connectOverCDP(relay.cdpEndpoint())` (`extensionContextFactory.ts:43`). Port this to C#: a Kestrel WebSocket relay on 127.0.0.1, a random GUID path, and a DPAPI-stored token, plus a small Palon extension. It works in the user's **normal** profile. Chrome shows a "started debugging this browser" infobar while attached, so attach only for the duration of a job.
  3. **UIA only** (FlaUI) as a read-only fallback. No writes.

## 2. Salesforce Lightning specifics

- **Shadow DOM, CORRECTED/refined:** Lightning Experience runs LWC with **synthetic shadow by default** (a polyfill, so elements actually live in the light DOM). Native/mixed shadow is being rolled out. Lightning Web Security makes `shadowRoot.mode` look "closed" *to component JS*, but CDP/DevTools and Playwright work at the protocol level. Playwright's CSS and role engines pierce open shadow roots. The CDP accessibility tree (`Accessibility.getFullAXTree`) and `DOM.getDocument({pierce:true})` see through all shadow roots. Sources: https://developer.salesforce.com/docs/platform/lwc/guide/create-dom-synthetic.html, .../create-mixed-shadow.html. browser-use serializes shadow content explicitly and tags it `|SHADOW(open)|`/`|SHADOW(closed)|` (`browser_use/dom/serializer/serializer.py:1020-1027`). **Rule: target by accessibility role and name, never by CSS paths or XPaths into component internals.**
- **Dynamic ids:** Aura/LWC ids (`input-123`, `globalId`) change on every page load. Never cache them. Stagehand caches **absolute XPaths** (`actService.ts:466`, `selector: "xpath=..."`), which is fine for static sites but brittle in Lightning, so do *not* copy that part as-is (see §3).
- **Stable anchors, in priority order:**
  1. The URL: `/lightning/r/<Object>/<Id>/view` gives the record Id deterministically. The 15/18-char Id prefix tells you the object (`003` Contact, `00Q` Lead, `006` Opportunity).
  2. ARIA role + accessible name: `button "Log a Call"`, `combobox "Status"`, `textbox "Subject"`. These are labels, so they follow the UI language. Cache the *user's* label strings, since Hebrew UI gives Hebrew names.
  3. Field API-name attributes. Record forms render `data-target-selection-name="sfdc:RecordField.Task.Subject"` and `lightning-input-field[field-name]` `[verify-org]`. When present, these are language-independent.
  4. `records-*` / `force-*` custom element tag names (e.g. `records-lwc-highlights-panel`, `force-record-layout-item`) as scoping containers.
- **Flows to build as skills:**
  - **FindByPhone.** Global search box (`button "Search..."` → `searchbox`). Type the phone in *digits only*. Try the local 05X form, then +972. Wait for the results page `/one/one.app#/search` or the instant-results listbox. Read the rows from the a11y snapshot. Global search does token matching on normalized digits `[verify-org]`. If there are 0 results, try the last 7 digits.
  - **LogCall.** On the record page, open the Activity tab, then `button "Log a Call"`. The publisher opens *inline or as a docked composer* depending on org layout `[verify-org]`. Fill Subject (combobox), Comments (textarea) and the related-to lookup if it is empty, then `button "Save"`. Verify that the toast `"Task ... was created"` (role `status`/`alert`) appears *and* that the Activity timeline gains a new item containing the subject.
  - **NewTask** (follow-up): `button "New Task"`. Due Date is a date input that uses the user's locale format, so read the placeholder to learn the format. The Reminder checkbox and time come next.
  - **SetField** (Lead Status / Opp NextStep): either the inline-edit pencil `button "Edit Status"` or the path component `button "Mark Status as Complete"`. Picklists are `lightning-combobox`: click it, then `option "<value>"` in the listbox.
- **Waiting.** Lightning renders asynchronously. After every action, wait for all three: (a) no `.slds-spinner` visible, (b) network idle for 500 ms (browser-use and Stagehand both use a "DOM settle" timeout, `domSettleTimeoutMs`), (c) the step's expected post-condition locator. Never use fixed sleeps.

## 3. Cached deterministic skills + LLM recovery

**Stagehand, VERIFIED at code level** (`packages/extension/services/actService.ts`, `cacheService.ts`, MIT). This is the core pattern to copy:
- The cache **value** is a list of `Action {selector, method, arguments[], description}`. Arguments hold `%var%` placeholders that are substituted at replay (`substituteVariablesInArguments`, line 473), so the cache never stores the Hebrew summary text itself.
- `withCache()` flow: build the key → on a hit, `replayCachedActions` runs every action with **selfHeal:false**, and any failure *throws*. On a throw or a miss, it runs the full LLM pipeline and writes the new value. "Any cache failure falls back to normal execution and must never break the action."
- Self-heal inside a live act (`selfHealAction`, line 371): if the deterministic action fails, take a new snapshot, ask the LLM *only* "find the element for `<method> <description>`", and retry once with the new selector.
- **CORRECTED:** in the current repo the cache **key** is computed *server-side* (Browserbase API: URL normalization + "DOM shaping/hashing" of the CDP a11y tree + instruction). The local `cacheDir` variant (docs `v3/best-practices/caching.mdx`) keys on instruction + start URL + options. The earlier draft's "Lightning layout hash" key is our own design, not Stagehand's.
- The act-inference prompt (`packages/extension/prompt.ts:225`) is a good template. It asks for *one* element plus method plus args, and "if no matching element exists set action to null — do not fabricate".

**Palon skill format (C#), adapted for Lightning:**
```json
{ "skill":"LogCall", "version":3, "org":"acme.lightning.force.com",
  "steps":[ { "intent":"open Log a Call composer",
      "pre":{"urlRegex":"/lightning/r/(Contact|Lead)/\\w{15,18}/view"},
      "locators":[ {"role":"button","name":"Log a Call"}, {"role":"button","name":"תיעוד שיחה"},
                   {"css":"[data-target-selection-name$='LogACall']"} ],
      "method":"click", "args":[],
      "post":{"role":"textbox","name":"Subject","timeoutMs":6000},
      "lastGood":"2026-09-20", "hits":41, "heals":1 } ] }
```
- The key is `(orgHost, skill, stepIndex)`. There is no DOM hash, because Lightning DOMs change on every load. Validity comes from `pre`/`post` checks, not from a hash.
- Replay tries each locator in order and requires **exactly one visible, enabled match**. If there are 0 or more than 1, go to recovery.
- **Recovery** gets one LLM call with the step `intent` plus a Playwright-style **ref snapshot** of the *scoped* container (the active modal or the record page body). It returns `{ref}` or `null`. Execute, then check `post`. Only after `post` passes, derive a *role+name* locator from the resolved element (not an XPath) and prepend it to `locators`. Keep a maximum of 4 per step.
- **Budget:** at most 2 recoveries per job. After that, stop and hand the tab to the user, saying which step failed.

**Snapshot format to copy (Playwright ai-mode, VERIFIED** `packages/injected/src/ariaSnapshot.ts:41,229`, `mode:'ai'` assigns `ref = 'e'+n`, resolved later by the `aria-ref=` selector engine, `injectedScript.ts:245`):
```
- dialog "Log a Call" [ref=e12]:
  - combobox "Subject" [ref=e14]
  - textbox "Comments" [ref=e15]
  - button "Save" [ref=e20]
```
In .NET, `Locator.AriaSnapshotAsync()` gives the YAML *without* refs, and `aria-ref=` works only after an ai-mode snapshot has been taken `[verify: Playwright .NET 1.5x exposes _snapshotForAI?]`. Fallback: build our own from CDP `Accessibility.getFullAXTree`, using `backendDOMNodeId` as the ref (Stagehand's `EncodedId = "<frameOrdinal>-<backendNodeId>"`, `types/private/internal.ts:1`, line format `[id] role: name`, `treeFormatUtils.ts:12`). Then act through `DOM.resolveNode` → `Runtime.callFunctionOn` or `DOM.getBoxModel` → `Input.dispatchMouseEvent`.

**browser-use patterns worth copying** (MIT, VERIFIED `browser_use/agent/views.py`):
- Output schema per step: `{evaluation_previous_goal, memory, next_goal, action[]}` (lines 383-394). This is cheap self-verification.
- `max_failures=5`, `max_actions_per_step=5`. Multi-action sequences **stop early when the URL or focus changes** (`service.py` `multi_act`, around line 2734).
- `ActionLoopDetector`: a rolling window of 20 action hashes (click hash = element index, input hash = index + normalized text). It adds escalating nudges at 5, 8 and 12 repeats and flags page stagnation after 5 identical fingerprints. It is *soft*: it adds a message to the prompt and never blocks the action.
- In the DOM serialization, `*[12]` marks elements that are *new* since the last step (`serializer.py:1032`). This tells the model what its last action caused.

## 4. Plan → preview → approve → execute → verify

```
CallEnded → ContextBuilder → FindByPhone skill (read-only, no approval)
 → Planner (Gemini JSON schema: {recordId, subject, comments, followUp{date,time,subject}?, fieldChanges[]})
 → Preview (WPF RTL: record name+Id link, each field old→new) → Approve (token = SHA256(plan))
 → Executor (skills, one tab, record Id asserted before every write step)
 → Verifier (reload record, read Activity timeline + fields via a11y, compare to plan) → AuditLog
```
- **Safety is decided in code, not by the model.** CORRECTED: UFO's "safeguard" is model-declared. The AppAgent emits status `"CONFIRM"` for sensitive steps (`ufo/prompts/share/base/app_agent.yaml:36`) and `interactor.sensitive_step_asker` asks the user (`ufo/module/interactor.py:178`). Palon should instead hard-code that every `Save`/`Submit`/`Delete` click requires the approval token. The rest of UFO's design is worth copying: the step before Save ("inputting is not sensitive, sending is") and the blackboard `UserConfirm` field.
- **Allowlist:** the executor refuses any Save when the URL record Id ≠ the plan's record Id. It never clicks buttons whose name matches `Delete|Merge|Mass|Convert|מחק`.
- **Undo (browser):** the audit log keeps the created Task URL(s) and the old field values. Undo runs `DeleteTask` and `SetField(old)` as skills, gated by the same preview. It is best-effort, and the UI says so.
- **Session expiry / 2FA:** if the URL matches `login.salesforce.com|/_ui/identity/verification|/secur/` or the page has `input[type=password]`, pause, bring the window to the front, and show a toast saying "sign in, then press Continue". Never type credentials or MFA codes.

## 5. Reliability plan, on a real org with no sandbox

1. **Record once with the user.** In a "teach" session Palon listens to CDP `DOM`/`Input` events. The simplest approach is to inject a capture-phase click/input listener that reports the role+name of the target via `Accessibility.getPartialAXTree`. The user does one real Log-a-Call on a dummy record. This seeds the `locators` and `post` for every step, in the user's own UI language and layout.
2. **Dummy record for tests.** Create a Contact "Palon Test (אל תמחק)" once. All smoke tests write to it only. The allowlist enforces that in test mode.
3. **Dry-run mode:** run all steps up to *but not including* Save, take a screenshot, press Escape. Run it nightly and after each Salesforce release (3 per year) to catch drift before the user does.
4. **Metrics in the audit log:** replay hit rate, heals per step, verify failures. If a step heals more than twice in a week, rerun teach mode.
5. **Target:** at least 97% of jobs verified without user intervention after two weeks. Anything unverified is shown as "not saved" and never reported as a silent success.

## 6. Screen reading (B)

- **Windows.Media.Ocr Hebrew, CORRECTED to "not available":** a ShareX issue (#7854) and MS Q&A 1233524 both report no `Language.OCR~~~he-IL` capability, and I found no source listing Hebrew. Keep the runtime check `OcrEngine.IsLanguageSupported(new Language("he"))`, but plan on **Tesseract `heb`** (Apache-2.0) locally or Gemini vision.
- Text sources, best first: DOM/a11y (browser tab) → UIA (FlaUI, MIT) → Tesseract → Gemini vision.
- **Gemini data use, VERIFIED** (https://ai.google.dev/gemini-api/terms): on **unpaid** services Google uses prompts and responses to improve products, *human reviewers may read* them, and the terms say not to submit sensitive or confidential info. On paid services prompts are not used for improvement. This means client screenshots, receipts and call content must go through a paid key or stay local. That applies to the whole product, not just B.
- The receipt → deal flow is unchanged: extract with a schema, check that the amount appears in the OCR text, then the same preview/approve pipeline.

## 7. Licenses (VERIFIED from LICENSE files)
- Port freely: Playwright (Apache-2.0), Stagehand, browser-use, UFO, FlaUI (all MIT), and Tesseract (Apache-2.0).
- Skyvern is **AGPL-3.0**: take ideas only, no code.

## 8. Phases
- **MVP:** Palon profile via CDP. Teach mode for FindByPhone and LogCall. Replay with 1-shot LLM recovery. Preview/approve. Verify via timeline. Audit log. Nightly dry-run.
- **v2:** NewTask and SetField skills. Extension relay for the normal profile. Undo skills. Receipt → Opportunity.
- **Limits:** Salesforce releases and admin layout changes will break steps. Teach mode, heal and dry-run limit the damage, but they do not remove it.
