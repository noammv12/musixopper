# UI Audit — v8.3 polish session

A full walk of every surface and flow, before touching anything. Findings are ranked by
user impact. Each maps to a commit in this session; the tables at the bottom are the
mechanical rulebook the migration follows.

Legend: **[bug]** broken behavior · **[chrome]** stock WPF leaking through ·
**[system]** design-system gap · **[polish]** rough but working.

---

## Ranked findings

### P0 — broken or visibly foreign

1. **[bug] Clickable toasts don't take the click.** `_toastContent.MouseLeftButtonUp`
   (`src/UI/DockWindow.cs:179`) never fires: `OnPillMouseDown` (`:1078`) calls
   `_pill.CaptureMouse()`, and element capture routes the mouse-up to the pill, not the
   child. Chips (`:939`) and reminder buttons (`:841`) set `Handled` on mouse-down to dodge
   exactly this — the toast doesn't. "Notes ready — click to view", "Palon answered —
   click to read", and the caller brief are all inert. Also `:184` collapses the pill even
   while hovered instead of settling via `RestState()`.

2. **[bug] Esc during hotkey capture closes the whole flyout.** The window-level
   `PreviewKeyDown` (`src/UI/FlyoutWindow.cs:179`) tunnels ahead of the capture box's
   handler and hides the card. The in-app hint says "Esc cancels" — it cancels far more
   than promised. And clicking anywhere else inside the flyout never defocuses the box
   (nothing else is focusable), so capture mode — and the global-hotkey suspension it
   triggers — persists silently.

3. **[chrome] Windows-blue input borders on a black glass card.** `Ui.TextBox` /
   `Ui.PasswordBox` (`src/UI/Controls.cs:185`, `:208`) set brushes but keep the stock
   Aero2 template, whose triggers override `BorderBrush` on hover (`#FF7EB4EA`) and focus
   (`#FF569DE5`). Every API-key box and editor flashes system blue. The boxes are also the
   only square-cornered elements in the app.

4. **[chrome] Stock Windows scrollbars on the graphite card.** Five `ScrollViewer`s
   (`src/UI/FlyoutWindow.cs:134`, `:505`; `Commands.cs:29`; `Notes.cs:90`;
   `Reminders.cs:86`) render the default ~17px light scrollbar. The drag code even
   special-cases `Primitives.ScrollBar` (`FlyoutWindow.cs:1061`) — the one foreign object
   the app knows it has.

5. **[bug] Hebrew reorders in composed lines.** Hebrew is the product's default language,
   yet no `FlowDirection` or bidi isolation exists anywhere. `NoteBrief.Compose`
   (`src/Notes/NoteBrief.cs:14`) concatenates an LTR prefix (`number · 3d ago: `) with an
   RTL key line — the neutral `·` and `:` resolve against the paragraph direction and
   visually reorder in the caller-brief toast, the headline feature. Same pattern:
   "Missed · " + Hebrew label (`DockWindow.cs:803`), note-card headers
   (`Notes.cs:549`). Truncation appends "…" at the logical end, which renders at the
   visual left of an RTL run. Hebrew paragraphs are left-aligned throughout.

6. **[bug] Long toasts hard-clip mid-glyph.** `_toastText` (`DockWindow.cs:174`) has no
   `TextTrimming` and no `MaxWidth`; the pill clamps at 600px and the text is simply cut.
   Real producers exist: hotkey-conflict warnings, "no summary ({reason})" toasts.

### P1 — interaction roughness

7. **[bug] Press-and-slide on a toggle drags the window.** The flyout's drag-from-anywhere
   preview handlers (`FlyoutWindow.cs:1008-1067`) exempt only TextBoxBase / PasswordBox /
   ScrollBar. A press that slides a few pixels on a `PillSwitch`, `Segmented`, or time chip
   moves the card and swallows the click (`OnRootDragEnd` sets `Handled`). The dock solved
   the same problem the opposite way (children set `Handled`).

8. **[bug] Nested lists trap the mouse wheel.** The four inner list scrollers sit inside
   the outer card scroller; at their extent WPF still marks the wheel handled, so the
   panel stops scrolling whenever the cursor is over a list.

9. **[bug] Overlapping feedback flashes revert early.** `Ui.Flash` (`Controls.cs:234`)
   spawns a fresh un-cancelled timer per call — spam-clicking Copy truncates the feedback.
   `DockWindow.Feedback` (`:943`) has the same bug plus a visible symptom: the first timer
   un-pins `MinWidth` while the second message shows, so the pill jumps. `CommandRow`
   (`:394`) already has the correct stop/restart pattern; the codebase disagrees with
   itself.

10. **[polish] Hotkey capture has no focused look.** Arming capture changes only the label
    text; hover (`ControlFillHoverBrush`) reads *stronger* than focused. The entire block
    is duplicated across `Notes.cs:283-438` and `Palon.cs:51-327`.

11. **[polish] Sticky failure strings.** "✨ Follow-up (failed — see log)"
    (`Notes.cs:585`) and the recap failure/no-key strings (`Stats.cs:122`, `:133`) never
    revert; the panel advertises a failure until rebuilt.

12. **[polish] Onboarding cards don't answer the press.** `OptionCard` (`Controls.cs:344`)
    has hover fill but no press feedback, and selection is an unanimated border swap —
    while the Continue button just below dips and springs. Welcome subtitle hardcodes
    "Ctrl+Alt+P" instead of reading the assistant default.

13. **[polish] Toast-replacing-toast teleports.** The swap path (`DockWindow.cs:660`) sets
    new text with no re-fade while the pill resizes underneath.

14. **[polish] Snippets has no empty state.** `RebuildSnippetList` (`FlyoutWindow.cs:534`)
    renders a blank gap for a new user — the most likely first path from the … chip. The
    five existing empty states use three different margins, two voices, and inconsistent
    wrapping.

### P2 — system gaps (the quiet noise)

15. **[system] Eight font sizes, no ramp.** {10, 10.5, 11, 11.5, 12, 12.5, 13.5, 15} —
    six near-indistinguishable half-point steps then a jump. ~86 `Ui.Text`/`Ui.Link`
    literal callsites; `DockWindow.cs` bypasses `Ui` entirely with 14 raw `FontSize=`
    literals and its own chip/button/flash clones. Font family string duplicated
    (`DockWindow.cs:136`, `FlyoutWindow.cs:102`).

16. **[system] 205 ad-hoc margins.** Dominant beats: 6px stack gap ×35, 8px ×23, 12px ×10,
    plus a long tail of 5/7/9/10/14. Five different card paddings for one visual card
    concept. A +1px-bottom optical padding applied to some chips and not others.

17. **[system] Radius drift.** Cards split between 10 and 11 across files; the working set
    is {3, 5-dynamic, 6, 8, 10, 11, 12}.

18. **[system] Motion literals off the scale.** `Motion` defines 120/180/240, but 150
    appears at six sites, plus 80, 90, 220, 700, 1200 — and four raw `DoubleAnimation`
    constructions bypass `Motion` with values that already equal tokens. Feedback revert
    timers disagree: 1200ms in the flyout, 900ms in the dock.

19. **[system] Colors outside Theme.cs.** Destructive red `#FF453A` hardcoded twice
    (`FlyoutWindow.cs:603`, `Commands.cs:143`), overriding a resource reference set one
    line above. `Brushes.White` knob (`Controls.cs:32`). Accent hover is a raw
    `Opacity = 0.92` in one place and `0.9` in another. Shadow specs diverge between the
    two windows (Blur 24/Depth 4 vs 16/2).

20. **[system] No pixel snapping anywhere.** `UseLayoutRounding` / `SnapsToDevicePixels`
    appear nowhere; both windows are `AllowsTransparency` and per-monitor DPI aware
    (`src/UI/Dpi.cs`). At 125/150% the 1px dividers — already at 8.6% alpha — blur across
    two device-pixel rows. `OptionCard`'s 1.5px border is fractional at every scale.

21. **[system] The same card, written five times.** Radius-10 / (10,8,10,8) / ControlFill
    card `Border` duplicated verbatim in five files; `RenderFollowUp`/`RenderRecap` are
    twins; snippet and command panels share six pair-wise identical methods including a
    character-for-character `Link` helper; the API-key row exists four times; the
    section caption ten times; copy-feedback six times across three mechanisms; three
    chip variants with three geometries.

22. **[polish] Small motion inconsistencies.** `ShowStats` is the only panel that skips
    `StaggerIn`. `Segmented.Select` lacks the same-value guard its sibling
    `PillSwitch.Set` documents and has. `HoverSpring` + `PressSpring` silently conflict
    (both overwrite `RenderTransform`).

---

## The rulebook (applied mechanically in the migration commits)

### Type ramp — five roles

| Role | Size | Weight | Replaces |
|---|---|---|---|
| Caption | 10.5 | SemiBold | 10, 10.5 — section captions, tiny labels |
| Small | 11 | Normal | 11; 11.5 when secondary-colored |
| Body | 12 | Normal | 12; 11.5 when primary-colored |
| Lead | 13 | SemiBold | 12.5, 13.5 — card titles, buttons, status headline |
| Title | 15 | SemiBold | 15 — panel headers |

Sanctioned exception: the dock toast and reminder labels keep `FontWeights.Medium` at
Lead size — on the compact pill, Medium reads better than SemiBold.

### Spacing — 4/8 grid

Scale: **4, 8, 12, 16, 20**. Mapping: the 6px stack beat splits by role — caption→content
and line→line inside one group tighten to 4; sibling rows/cards open to 8. 5→4, 7→8,
9→8, 10→8 or 12 by context, 14→12 or 16 by context. 1/2px stay literal (hairlines,
optical nudges). Card padding collapses to `(10,8,10,8)` (dense cards) and
`(12,10,12,10)` (option cards); inputs `(6,4,6,4)`. The +1px-bottom optical trick lives
only in the chip factory.

### Radius

| Token | Value | Use |
|---|---|---|
| Small | 6 | segmented thumb, fine details |
| Control | 8 | buttons, inputs, command rows, segmented track |
| Card | 10 | all cards and chips (11 → 10 everywhere) |
| Panel | 12 | flyout root; dock pill max |

Excluded by design: PillSwitch track (height/2), dock pill collapsed radius (dynamic),
scrollbar thumb (3, becomes the Hairline token).

### Motion

Spine stays Fast=120 / Base=180 / Slow=240. New named roles: Dip=80 (press-down),
Exit=90 (dismissals — exits run faster than entrances), PulseFast=700, PulseSlow=1200,
Revert=1200ms (feedback timers; the dock's 900 unifies up). The 150s retire: linear
slides → Fast, spring releases → Base (Overshoot needs the runway). 220 → Slow. Raw
`DoubleAnimation` sites route through `Motion` helpers.

### Theme

New tokens: `DangerBrush #FF453A` (destructive actions), `KnobBrush` (switch knob).
Accent hover stays an opacity dim, unified at one constant. One shadow spec (the
flyout's: Blur 24, Depth 4, Direction 270, Opacity 0.45) for both windows.

---

## Deferred (out of this session's scope)

- Brush-key string constants (~180 magic strings) — churn with no visual payoff.
- Scrollbar page-click (repeat buttons) — the thin overlay bar ships without track
  paging; wheel and thumb-drag cover it.
- Live theme re-apply on OS theme change — the palette is deliberately fixed dark.
- A `Slider` drag exemption — no sliders exist yet; noted for whoever adds one.
