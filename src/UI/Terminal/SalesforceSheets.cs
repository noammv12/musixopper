using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Palon.Notes;
using Palon.Salesforce;

namespace Palon.UI;

/// <summary>
/// "Log to Salesforce" in the Terminal: a glass sheet that finds the record,
/// previews every change, and writes only after Approve — then shows
/// progress and a verified result. Plus the connection / teach section
/// (kept separate from the general settings sheet on purpose).
/// </summary>
static class SalesforceSheets
{
    /// <summary>From the dock or a Today row: open the Terminal and the preview.</summary>
    public static void Open(CallNote note, DateTime? callbackLocal = null)
    {
        TerminalWindow.ShowSingleton();
        if (TerminalWindow.Instance is { } host) LogCall(host, note, callbackLocal);
    }

    static TextBlock Line(string text, double size, Brush brush, FontWeight? weight = null)
    {
        var t = Kit.T(text, size, brush, weight, wrap: true);
        t.Margin = new Thickness(0, 6, 0, 0);
        return t;
    }

    static Grid Head(string title, string? subtitle, Action close)
    {
        var text = new StackPanel();
        text.Children.Add(Kit.T(title, Display.Sheet, Tone.Text, FontWeights.SemiBold));
        if (subtitle is not null) text.Children.Add(Line(subtitle, 13.5, Tone.MutedSoft));
        var x = Kit.IconButton(Icons.Close, 34, close);
        x.VerticalAlignment = VerticalAlignment.Top;
        var bar = Kit.Bar(text, x);
        bar.Margin = new Thickness(0, 0, 0, 16);
        return bar;
    }

    static StackPanel Buttons(params Border[] pills)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 22, 0, 0) };
        foreach (var p in pills)
        {
            if (row.Children.Count > 0) p.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(p);
        }
        return row;
    }

    // ---- log a call --------------------------------------------------------------------

    public static void LogCall(TerminalWindow host, CallNote note, DateTime? callbackLocal)
    {
        var cts = new CancellationTokenSource();
        var body = new StackPanel();
        var content = new StackPanel();
        Action close = () => { };
        body.Children.Add(Head("תיעוד ב-Salesforce", note.Number ?? "שיחה", () => { cts.Cancel(); close(); }));
        body.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 520 });
        close = host.ShowSheet(body, 620, 760);

        void Progress(string s) => host.Dispatcher.InvokeAsync(() =>
        {
            if (content.Tag as string == "busy" && content.Children.Count > 0 && content.Children[^1] is TextBlock tb) tb.Text = s;
        });
        SalesforceAgent.Progress += Progress;
        body.Unloaded += (_, _) => SalesforceAgent.Progress -= Progress;

        Busy("מחפש את הלקוח ב-Salesforce…");
        _ = PrepareAsync();

        void Busy(string text)
        {
            content.Children.Clear();
            content.Tag = "busy";
            content.Children.Add(Kit.T("Palon עובד בחלון ה-Edge שלו. אל תסגור אותו.", 13, Tone.Muted, wrap: true));
            content.Children.Add(Line(text, 15, Tone.Text));
        }

        async Task PrepareAsync()
        {
            var r = await SalesforceAgent.PrepareAsync(note, callbackLocal, ct: cts.Token);
            if (cts.IsCancellationRequested) return;
            if (r.Plan is null) { Problem(r.Error ?? "משהו השתבש", r.NeedsLogin); return; }
            Preview(r.Plan);
        }

        void Problem(string message, bool login)
        {
            content.Tag = null;
            content.Children.Clear();
            content.Children.Add(Line(message, 15, Tone.AmberText));
            var pills = new List<Border>();
            if (login)
                pills.Add(Kit.Pill("פתח את ה-Edge של Palon", PillKind.Primary, () => _ = OpenEdge(host, null)));
            pills.Add(Kit.Pill("נסה שוב", PillKind.Secondary, () => { Busy("מחפש את הלקוח ב-Salesforce…"); _ = PrepareAsync(); }));
            pills.Add(Kit.Pill("סגור", PillKind.Ghost, close));
            content.Children.Add(Buttons(pills.ToArray()));
        }

        void Preview(SfPlan plan)
        {
            content.Tag = null;
            content.Children.Clear();
            if (plan.Record is null)
            {
                if (plan.Candidates.Count == 0)
                {
                    Problem("לא נמצא איש קשר או ליד עם המספר הזה. Palon לא יוצר רשומות חדשות.", false);
                    return;
                }
                content.Children.Add(Kit.T("נמצאו כמה רשומות. על איזו לתעד?", 14, Tone.Text, wrap: true));
                foreach (var c in plan.Candidates.Take(8))
                {
                    var row = Kit.ListRow(Kit.Col(2,
                        Kit.Auto(Kit.T(c.Name.Length > 0 ? c.Name : c.Id, 14.5, Tone.Text, FontWeights.Medium)),
                        Kit.T(c.ObjectType == "Lead" ? "ליד" : "איש קשר", 12, Tone.Muted)), new Thickness(12, 10, 12, 10));
                    row.Margin = new Thickness(0, 8, 0, 0);
                    row.Cursor = System.Windows.Input.Cursors.Hand;
                    var chosen = c;
                    Kit.Clickable(row, () => Preview(plan.WithRecord(chosen)));
                    content.Children.Add(row);
                }
                content.Children.Add(Buttons(Kit.Pill("ביטול", PillKind.Ghost, close)));
                return;
            }

            var who = Kit.Glass(18, new Thickness(16, 12, 16, 12));
            who.Child = Kit.Col(2,
                Kit.Auto(Kit.T(plan.Record.Name.Length > 0 ? plan.Record.Name : plan.Record.Id, 16, Tone.Text, FontWeights.SemiBold)),
                Kit.T($"{(plan.Record.ObjectType == "Lead" ? "ליד" : "איש קשר")} · {plan.Record.Id}", 12, Tone.Muted));
            content.Children.Add(who);
            content.Children.Add(Line("השינויים ש-Palon יבצע אחרי שתאשר:", 13, Tone.MutedSoft));
            foreach (var change in plan.Changes)
            {
                var col = new StackPanel();
                col.Children.Add(Kit.Auto(Kit.T(SfPlanner.Describe(change), 14, Tone.Text, FontWeights.Medium)));
                if (change.Comments is { } comments)
                {
                    var preview = comments.Length > 400 ? comments[..400] + "…" : comments;
                    var t = Kit.Auto(Kit.T(preview, 12.5, Tone.Body, wrap: true));
                    t.Margin = new Thickness(0, 4, 0, 0);
                    col.Children.Add(t);
                }
                var row = Kit.ListRow(col, new Thickness(12, 10, 12, 10));
                row.Margin = new Thickness(0, 8, 0, 0);
                content.Children.Add(row);
            }
            content.Children.Add(Line("Palon לא מוחק ולא נוגע ברשומות אחרות. כל שמירה נבדקת בדף אחריה.", 12, Tone.Muted));

            var approve = Kit.Pill("אשר ושמור", PillKind.Primary, () => Execute(plan, dryRun: false));
            var rehearse = Kit.Pill("הרצה בלי שמירה", PillKind.Secondary, () => Execute(plan, dryRun: true));
            rehearse.ToolTip = "Palon ימלא הכל ויעצור לפני 'שמור' — לבדיקה";
            content.Children.Add(Buttons(approve, rehearse, Kit.Pill("ביטול", PillKind.Ghost, close)));
        }

        void Execute(SfPlan plan, bool dryRun)
        {
            // The token is minted here — by the user's click — and nowhere else.
            var token = dryRun ? null : SalesforceAgent.Approve(plan);
            Busy(dryRun ? "הרצה בלי שמירה…" : "שומר ב-Salesforce…");
            _ = Run();

            async Task Run()
            {
                var r = await SalesforceAgent.ExecuteAsync(plan, token, dryRun, cts.Token);
                Result(plan, r, dryRun);
            }
        }

        void Result(SfPlan plan, ExecuteResult r, bool dryRun)
        {
            content.Tag = null;
            content.Children.Clear();
            var headline = r.Ok
                ? dryRun ? "ההרצה עברה עד לפני השמירה. שום דבר לא נשמר." : "נשמר ואומת ב-Salesforce ✓"
                : r.Error ?? "לא הושלם";
            content.Children.Add(Line(headline, 16, r.Ok ? Tone.GreenText : Tone.AmberText, FontWeights.SemiBold));
            foreach (var c in r.Changes)
            {
                var (label, brush) = c.Status switch
                {
                    ChangeStatus.Verified => ("נשמר ואומת", (Brush)Tone.GreenText),
                    ChangeStatus.SavedUnverified => ("נשמר, לא אומת — בדוק ידנית", Tone.AmberText),
                    ChangeStatus.DryRun => ("נבדק בלי שמירה", Tone.MutedSoft),
                    ChangeStatus.Failed => ("לא נשמר", Tone.RedText),
                    _ => ("לא בוצע", Tone.Muted),
                };
                var col = Kit.Col(2, Kit.Auto(Kit.T(SfPlanner.Describe(c.Change), 14, Tone.Text)), Kit.T(label + (c.Detail is null ? "" : " · " + c.Detail), 12.5, brush, wrap: true));
                if (c.UndoNote is { } undo) col.Children.Add(Kit.T(undo, 12, Tone.Muted, wrap: true));
                if (c.AuditId is { } auditId) col.Children.Add(UndoPill(host, auditId));
                var row = Kit.ListRow(col, new Thickness(12, 10, 12, 10));
                row.Margin = new Thickness(0, 8, 0, 0);
                content.Children.Add(row);
            }
            if (r.Ok && !dryRun) host.Cheer();
            var pills = new List<Border>
            {
                Kit.Pill("פתח ב-Salesforce", PillKind.Primary, () => _ = OpenEdge(host, r.RecordUrl ?? plan.Record?.Url)),
            };
            if (dryRun && r.Ok) pills.Add(Kit.Pill("עכשיו לשמור", PillKind.Secondary, () => Preview(plan)));
            pills.Add(Kit.Pill("סגור", PillKind.Ghost, close));
            content.Children.Add(Buttons(pills.ToArray()));
        }
    }

    /// <summary>Undo for one audit entry. The token is minted by this click only.</summary>
    static Border UndoPill(TerminalWindow host, string auditId)
    {
        Border? pill = null;
        pill = Kit.Pill("בטל פעולה", PillKind.Ghost, async () =>
        {
            if (pill is not null) pill.IsEnabled = false;
            var token = SalesforceAgent.ApproveUndo(auditId);
            var r = await SalesforceAgent.UndoAsync(auditId, token);
            host.Toast(r.Message);
            if (pill is not null) pill.Visibility = r.Ok ? Visibility.Collapsed : Visibility.Visible;
            if (pill is not null) pill.IsEnabled = !r.Ok;
        }, height: 30, fontSize: 12.5);
        pill.HorizontalAlignment = HorizontalAlignment.Left;
        pill.Margin = new Thickness(0, 6, 0, 0);
        return pill;
    }

    /// <summary>Nightly rehearsal status + switch, and the recent actions Palon can undo.</summary>
    static FrameworkElement RehearsalAndUndo(TerminalWindow host)
    {
        var col = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        var s = RehearsalSettings.Load();
        var taught = RehearsalSchedule.Taught(DefaultSkills.Names.Select(SkillStore.Load)).Count > 0;
        var active = RehearsalSchedule.Active(s, taught);
        string state = !taught || s.TestRecordUrl is null
            ? "חזרה לילית: תופעל אחרי שתלמד את Palon פעולה ברשומת בדיקה"
            : active ? $"חזרה לילית ב-{s.Time} בימי עבודה (בלי שמירה)" : "חזרה לילית כבויה";
        col.Children.Add(Line(state, 13, Tone.MutedSoft));
        if (s.LastStatus is { } last)
            col.Children.Add(Line(last.Summary(), 12.5, last.Ok ? Tone.GreenText : Tone.AmberText));
        if (taught && s.TestRecordUrl is not null)
        {
            var status = Line("", 12.5, Tone.Muted);
            col.Children.Add(Buttons(
                Kit.Pill(active ? "כבה חזרה לילית" : "הפעל חזרה לילית", PillKind.Ghost, () =>
                {
                    var cur = RehearsalSettings.Load();
                    cur.Enabled = !RehearsalSchedule.Active(cur, true);
                    cur.Save();
                    host.Toast(cur.Enabled == true ? "החזרה הלילית פעילה" : "החזרה הלילית כבויה");
                }),
                Kit.Pill("הרץ חזרה עכשיו", PillKind.Ghost, async () =>
                {
                    status.Text = "מריץ בלי שמירה…";
                    var r = await Rehearsal.RunAsync(CancellationToken.None);
                    status.Text = r.Summary();
                    status.Foreground = r.Ok ? Tone.GreenText : Tone.AmberText;
                })));
            col.Children.Add(status);
        }

        var undoable = AuditLog.Undoable(DateTime.UtcNow).Take(5).ToList();
        if (undoable.Count > 0)
        {
            col.Children.Add(Line("אפשר לבטל (24 שעות אחרונות):", 13, Tone.MutedSoft));
            foreach (var e in undoable)
            {
                var row = Kit.Col(2, Kit.T($"{e.AtUtc.ToLocalTime():dd/MM HH:mm} · {e.Description}", 13, Tone.Text, wrap: true), UndoPill(host, e.Id));
                var item = Kit.ListRow(row, new Thickness(12, 8, 12, 8));
                item.Margin = new Thickness(0, 6, 0, 0);
                col.Children.Add(item);
            }
        }
        return col;
    }

    static async Task OpenEdge(TerminalWindow host, string? url)
    {
        try
        {
            await SalesforceAgent.OpenInEdgeAsync(url);
        }
        catch (Exception ex) when (ex is CdpException or System.Net.Http.HttpRequestException or System.IO.IOException)
        {
            host.Toast(ex.Message);
        }
    }

    // ---- connection + teach (the settings section) -------------------------------------------

    /// <summary>
    /// The Salesforce section: connection status, "Open Palon's Edge", and
    /// "Teach Palon". A self-contained element so the general settings sheet
    /// can embed it; <see cref="Settings"/> shows it as its own sheet too.
    /// </summary>
    public static FrameworkElement Section(TerminalWindow host)
    {
        var col = new StackPanel();
        col.Children.Add(Kit.SectionTitle("Salesforce"));
        var status = Line("בודק חיבור…", 13.5, Tone.MutedSoft);
        col.Children.Add(status);
        col.Children.Add(Line("Palon עובד בחלון Edge נפרד משלו. התחבר שם ל-Salesforce פעם אחת; החיבור נשמר.", 12.5, Tone.Muted));
        _ = Refresh();

        async Task Refresh()
        {
            var state = await EdgeBrowser.StateAsync(CancellationToken.None);
            status.Text = state switch
            {
                EdgeState.NotInstalled => "Microsoft Edge לא מותקן",
                EdgeState.BlockedByPolicy => "מדיניות הארגון חוסמת חיבור ל-Edge (RemoteDebuggingAllowed)",
                EdgeState.NeedsRestart => "ה-Edge של Palon פתוח בלי חיבור — סגור אותו ופתח מחדש מכאן",
                EdgeState.Running => SfOrg.Saved is { } o ? $"מחובר · {new Uri(o).Host}" : "Edge פתוח · התחבר ל-Salesforce",
                _ => SfOrg.Saved is { } o2 ? $"סגור · {new Uri(o2).Host}" : "עדיין לא הוגדר",
            };
            status.Foreground = state == EdgeState.Running ? Tone.GreenText : Tone.MutedSoft;
        }

        var teachPanel = new StackPanel();
        col.Children.Add(Buttons(
            Kit.Pill("פתח את ה-Edge של Palon", PillKind.Primary, async () => { await OpenEdge(host, null); await Refresh(); }),
            Kit.Pill("למד את Palon", PillKind.Secondary, () => Teach(host, teachPanel)),
            Kit.Pill("יומן פעולות", PillKind.Ghost, () =>
            {
                if (System.IO.File.Exists(Audit.PathFor))
                    Process.Start(new ProcessStartInfo(Audit.PathFor) { UseShellExecute = true })?.Dispose();
                else host.Toast("עוד אין פעולות ביומן");
            })));
        col.Children.Add(teachPanel);
        col.Children.Add(RehearsalAndUndo(host));
        return col;
    }

    public static void Settings(TerminalWindow host)
    {
        Action close = () => { };
        var body = new StackPanel();
        body.Children.Add(Head("חיבור ל-Salesforce", null, () => close()));
        body.Children.Add(Section(host));
        close = host.ShowSheet(body, 600);
    }

    static readonly (string Skill, string Label, string Howto)[] Teachable =
    {
        ("LogCall", "תיעוד שיחה",
            $"ברשומת בדיקה (למשל \"Palon Test\"): לחץ Log a Call, כתוב בנושא {TeachMerge.SubjectSentinel}, בהערות {TeachMerge.CommentsSentinel}, בחר תאריך ולחץ שמור."),
        ("NewTask", "משימת המשך",
            $"ברשומת בדיקה: לחץ New Task, כתוב בנושא {TeachMerge.SubjectSentinel}, בחר תאריך יעד ולחץ שמור."),
        ("FindRecordByPhone", "חיפוש לפי טלפון",
            "לחץ על תיבת החיפוש העליונה, הקלד מספר טלפון של לקוח (ספרות בלבד) ולחץ Enter."),
    };

    static void Teach(TerminalWindow host, StackPanel panel)
    {
        panel.Children.Clear();
        var pick = 0;
        var how = Line(Teachable[0].Howto, 13, Tone.Body);
        panel.Children.Add(Line("מה ללמד?", 13, Tone.MutedSoft));
        var seg = Kit.Segmented(Teachable.Select(t => t.Label).ToList(), 0, i => { pick = i; how.Text = Teachable[i].Howto; });
        seg.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(seg);
        panel.Children.Add(how);
        var log = new StackPanel();
        TeachSession? session = null;
        Border? finish = null;
        var start = Kit.Pill("התחל", PillKind.Primary, async () =>
        {
            if (session is not null) return;
            try
            {
                session = await TeachSession.StartAsync(CancellationToken.None);
                session.Observed += o => host.Dispatcher.InvokeAsync(() =>
                    log.Children.Add(Line($"{(o.Kind == "fill" ? "הקלדה" : "לחיצה")} · {o.Facts.Role} \"{o.Facts.Name ?? o.Facts.AriaLabel}\"", 12, Tone.Muted)));
                log.Children.Add(Line("Palon צופה. בצע את הפעולה בחלון ה-Edge שלו, ואז לחץ סיימתי.", 13, Tone.GreenText));
                if (finish is not null) finish.Visibility = Visibility.Visible;
            }
            catch (Exception ex) when (ex is CdpException or System.Net.Http.HttpRequestException or System.IO.IOException or System.Net.WebSockets.WebSocketException)
            {
                host.Toast(ex.Message);
            }
        });
        finish = Kit.Pill("סיימתי", PillKind.Secondary, async () =>
        {
            if (session is null) return;
            var s = session;
            session = null;
            var outcome = await s.FinishAsync(Teachable[pick].Skill);
            await s.DisposeAsync();
            log.Children.Clear();
            log.Children.Add(Line(outcome.Learned.Count > 0 ? $"נלמדו {outcome.Learned.Count} צעדים ✓" : "לא נלמד כלום — נסה שוב לאט יותר", 14,
                outcome.Learned.Count > 0 ? Tone.GreenText : Tone.AmberText, FontWeights.SemiBold));
            foreach (var l in outcome.Learned) log.Children.Add(Line(l, 12, Tone.Muted));
            foreach (var u in outcome.Unmatched) log.Children.Add(Line("לא שויך: " + u, 12, Tone.Faint));
            finish!.Visibility = Visibility.Collapsed;
        });
        finish.Visibility = Visibility.Collapsed;
        panel.Children.Add(Buttons(start, finish));
        panel.Children.Add(log);
    }
}
