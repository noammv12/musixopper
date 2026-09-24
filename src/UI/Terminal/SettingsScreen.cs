using System.Windows;
using System.Windows.Controls;
using Palon.Agentic;
using Palon.Interop;
using Palon.Terminal;

namespace Palon.UI;

/// <summary>
/// Settings (a side sheet): Palon's brain (name, keys, model), the quick-callback
/// hotkey, the dock's pinned templates, the daily calls goal (shared by the Now
/// rings and the dock), focus mode / proactive suggestions, and sounds.
/// Built once per opening — a re-render must not wipe a key being typed.
/// </summary>
sealed class SettingsScreen : TerminalScreen
{
    /// <summary>Re-registers the quick-callback hotkey (Shell wires the dock's); false = taken.</summary>
    public static Func<bool>? ApplyQuickHotkey { get; set; }

    bool _built;

    public SettingsScreen(TerminalWindow host) : base(host)
    {
        Unloaded += (_, _) => _built = false; // fresh values next time the sheet opens
    }

    public override string Title => "הגדרות";

    public override void Render(TermData d, bool entrance)
    {
        if (_built && !entrance) return;
        _built = true;
        Children.Clear();
        var stack = Stack();
        Add(stack, Header("הגדרות", "המוח של Palon, קיצורים, הדוק והיעדים שלך", out _));
        Add(stack, Section("המוח של Palon", BrainSheet.Content(Host, null).Body));
        Add(stack, Section("חזרה מהירה", QuickHotkeyRow()));
        Add(stack, Section("תבניות מוצמדות בדוק", PinnedTemplates()));
        Add(stack, Section("יעד שיחות יומי", CallsGoalRow(d)));
        Add(stack, Section("פוקוס והצעות", FocusRows()));
        Add(stack, Section("צלילים", SoundsRow()));
        Children.Add(stack);
        if (entrance) Kit.Rise(stack.Children.OfType<FrameworkElement>());
    }

    static Border Section(string title, FrameworkElement body)
    {
        var card = Kit.Glass(padding: new Thickness(22, 18, 22, 20));
        var col = new StackPanel();
        col.Children.Add(Kit.SectionTitle(title));
        body.Margin = new Thickness(body.Margin.Left, body.Margin.Top + 10, body.Margin.Right, body.Margin.Bottom);
        col.Children.Add(body);
        card.Child = col;
        return card;
    }

    static TextBlock Hint(string text)
    {
        var t = Kit.T(text, 12.5, Tone.MutedSoft, wrap: true);
        t.Margin = new Thickness(0, 8, 0, 0);
        return t;
    }

    // ---- quick-callback hotkey -------------------------------------------------------------

    static readonly Hotkey AltQuick = new(NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x42 /* B */);

    FrameworkElement QuickHotkeyRow()
    {
        var col = new StackPanel();
        var options = new[] { Hotkey.QuickCallbackDefault, AltQuick, Hotkey.Off };
        var current = Hotkey.LoadQuickCallback();
        var selected = Array.FindIndex(options, o => o.Serialize() == current.Serialize());
        var labels = options.Select(o => o.IsOff ? "כבוי" : o.ToString()).ToList();
        if (selected < 0)
        {
            labels.Insert(0, current.ToString());
            options = options.Prepend(current).ToArray();
            selected = 0;
        }
        var status = Hint(current.IsOff ? "כבוי — אפשר לקבוע חזרה מהדוק (+ חזרה)." : $"לחיצה על {current} בכל מקום פותחת את שדה החזרה בדוק.");
        var picker = Kit.Segmented(labels, selected, i =>
        {
            var combo = options[i];
            var previous = Settings.QuickCallbackHotkey;
            Settings.QuickCallbackHotkey = combo.Serialize();
            if (ApplyQuickHotkey?.Invoke() ?? true)
            {
                status.Foreground = Tone.MutedSoft;
                status.Text = combo.IsOff ? "כבוי — אפשר לקבוע חזרה מהדוק (+ חזרה)." : $"נשמר · {combo} פותח את שדה החזרה בדוק.";
            }
            else
            {
                Settings.QuickCallbackHotkey = previous;
                ApplyQuickHotkey?.Invoke();
                status.Foreground = Tone.AmberText;
                status.Text = $"{combo} תפוס (אפליקציה אחרת או קיצור אחר של Palon) — נשאר {Hotkey.LoadQuickCallback()}.";
            }
        });
        picker.FlowDirection = FlowDirection.LeftToRight;
        col.Children.Add(picker);
        col.Children.Add(status);
        return col;
    }

    // ---- dock pins ---------------------------------------------------------------------------

    FrameworkElement PinnedTemplates()
    {
        var col = new StackPanel();
        var templates = TemplatesStore.Load();
        var pinned = DockPins.Pick(templates, Settings.DockPinnedTemplates).Select(t => t.Id).ToList();
        var wrap = new WrapPanel();
        var hint = Hint("עד 3 — מופיעות בשורת הריחוף של הדוק, מוכנות להעתקה עם שם הלקוח.");
        void Rebuild()
        {
            wrap.Children.Clear();
            foreach (var t in templates)
            {
                var on = pinned.Contains(t.Id);
                var chip = Kit.Pill((on ? "✓ " : "") + t.Title, on ? PillKind.Primary : PillKind.Secondary, () =>
                {
                    if (pinned.Contains(t.Id))
                    {
                        if (pinned.Count == 1)
                        {
                            hint.Foreground = Tone.AmberText;
                            hint.Text = "לפחות תבנית אחת נשארת מוצמדת.";
                            return;
                        }
                        pinned.Remove(t.Id);
                    }
                    else if (pinned.Count >= 3)
                    {
                        hint.Foreground = Tone.AmberText;
                        hint.Text = "כבר יש 3 מוצמדות — הסר אחת קודם.";
                        return;
                    }
                    else pinned.Add(t.Id);
                    hint.Foreground = Tone.MutedSoft;
                    hint.Text = "נשמר · הדוק מתעדכן.";
                    Settings.DockPinnedTemplates = string.Join(",", pinned);
                    DockActions.NotifySettingsChanged();
                    Rebuild();
                }, height: 32, fontSize: 13);
                chip.Margin = new Thickness(0, 0, 8, 8);
                wrap.Children.Add(chip);
            }
            if (templates.Count == 0) wrap.Children.Add(Kit.T("אין תבניות עדיין.", 13, Tone.Muted));
        }
        Rebuild();
        col.Children.Add(wrap);
        var reset = Kit.Pill("ברירת מחדל", PillKind.Ghost, () =>
        {
            Settings.DockPinnedTemplates = null;
            pinned = DockPins.Pick(templates, null).Select(t => t.Id).ToList();
            DockActions.NotifySettingsChanged();
            Rebuild();
        }, height: 30, fontSize: 12.5);
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        col.Children.Add(Kit.Bar(hint, reset));
        return col;
    }

    // ---- calls goal ----------------------------------------------------------------------------

    FrameworkElement CallsGoalRow(TermData d)
    {
        var col = new StackPanel();
        var auto = DayRings.CallsGoal(CallStatsStore.Load(), d.Now);
        var values = new List<int?> { null, 20, 30, 40, 50, 60 };
        if (Settings.CallsGoal is int custom && !values.Contains(custom)) values.Add(custom);
        var labels = values.Select(v => v is int n ? n.ToString() : $"אוטומטי · {auto}").ToList();
        var selected = Math.Max(0, values.IndexOf(Settings.CallsGoal));
        col.Children.Add(Kit.Segmented(labels, selected, i =>
        {
            Settings.CallsGoal = values[i];
            DockActions.NotifySettingsChanged();
            Host.Refresh();
        }));
        col.Children.Add(Hint("טבעת השיחות בעכשיו ובדוק. אוטומטי = 10% מעל הממוצע של 5 הימים האחרונים (40 בלי היסטוריה)."));
        return col;
    }

    // ---- focus + nudges ---------------------------------------------------------------------------

    FrameworkElement FocusRows()
    {
        var col = new StackPanel();
        var until = Palon.Agentic.Focus.Until;
        var status = Hint(until is { } u ? $"בפוקוס עד {He.Clock(u.ToLocalTime())} — רק חזרות שקבעת מגיעות אליך." : "בפוקוס Palon שותק: רק חזרות שקבעת מגיעות. אפשר גם להקליד ״פוקוס שעה״ בשורת הפקודה.");
        col.Children.Add(Kit.Segmented(new[] { "כבוי", "שעה", "שעתיים", "עד סוף היום" }, until is null ? 0 : 1, i =>
        {
            if (i == 0) Palon.Agentic.Focus.Clear();
            else
            {
                var now = DateTime.Now;
                var length = i switch
                {
                    1 => TimeSpan.FromHours(1),
                    2 => TimeSpan.FromHours(2),
                    _ => now.Date.AddHours(19) > now ? now.Date.AddHours(19) - now : TimeSpan.FromHours(1),
                };
                Palon.Agentic.Focus.Set(length);
            }
            status.Text = Palon.Agentic.Focus.Until is { } t ? $"בפוקוס עד {He.Clock(t.ToLocalTime())} — רק חזרות שקבעת מגיעות אליך." : "פוקוס כבוי.";
        }));
        col.Children.Add(status);

        var nudgesLabel = Kit.T("הצעות יזומות מ-Palon (תובנות, לידים שהשתתקו, קצב)", 13.5, Tone.Text);
        nudgesLabel.Margin = new Thickness(0, 16, 0, 8);
        col.Children.Add(nudgesLabel);
        col.Children.Add(Kit.Segmented(new[] { "פעיל", "כבוי" }, Palon.Agentic.Focus.NudgesEnabled ? 0 : 1, i => Palon.Agentic.Focus.NudgesEnabled = i == 0));
        return col;
    }

    static FrameworkElement SoundsRow()
    {
        var col = new StackPanel();
        col.Children.Add(Kit.Segmented(new[] { "כבוי", "פעיל" }, Settings.Sounds ? 1 : 0, i => Settings.Sounds = i == 1));
        col.Children.Add(Hint("כבוי כברירת מחדל. (עוד אין צלילים — ההגדרה נשמרת לגרסה הבאה.)"));
        return col;
    }
}
